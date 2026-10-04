using System.Globalization;

namespace GoalMonitor;

public sealed class Position
{
    public required string MarketId { get; init; }
    public required string MarketName { get; init; }
    public required string SelectionId { get; init; }
    public required string SelectionName { get; init; }
    public required DateTime EntryTimeUtc { get; set; }
    public required double BackPrice { get; init; }
    public required double Stake { get; init; }
    /// <summary>Paper mode: false while the simulated in-play delay is running.</summary>
    public bool Matched { get; set; }
    public bool SeenSuspension { get; set; }
}

/// <summary>
/// Opens a back bet on the Over line when a signal fires, then either takes the profit after a
/// goal or closes out (lays at the current price, spreading the result equally across both outcomes)
/// when no goal arrives in time.
/// </summary>
public sealed class Trader(TradingSettings settings, SignalSettings signalSettings, BetAngelClient client, CsvLog trades)
{
    private readonly List<Position> _open = [];

    public double SessionPnl { get; private set; }
    public int OpenCount => _open.Count;
    public bool HasPosition(string marketId) => _open.Any(p => p.MarketId == marketId);

    public async Task OnSignalAsync(Signal signal, string marketName, CancellationToken ct)
    {
        if (_open.Count >= settings.MaxOpenPositions)
        {
            Log.Info($"  skip: already {_open.Count} open position(s)");
            return;
        }
        if (SessionPnl <= -settings.MaxSessionLoss)
        {
            Log.Warn($"  skip: session loss limit reached ({SessionPnl:F2})");
            return;
        }
        if (HasPosition(signal.MarketId)) return;

        var price = Ticks.Down(signal.BackPrice, settings.EntryTicksBelowBest);

        if (settings.Mode == TradingMode.Live)
        {
            try
            {
                var response = await client.PlaceBetAsync(signal.MarketId, signal.SelectionId, "BACK", price, settings.Stake, ct);
                Log.Info($"  LIVE back placed: {response["result"]?.ToJsonString() ?? response.ToJsonString()}");
            }
            catch (Exception ex)
            {
                Log.Error($"  LIVE back failed, no position opened: {ex.Message}");
                return;
            }
        }

        var position = new Position
        {
            MarketId = signal.MarketId,
            MarketName = marketName,
            SelectionId = signal.SelectionId,
            SelectionName = signal.SelectionName,
            EntryTimeUtc = signal.TimeUtc,
            BackPrice = price,
            Stake = settings.Stake,
            Matched = settings.Mode == TradingMode.Live || settings.PaperFillDelaySeconds <= 0,
        };
        _open.Add(position);
        Log.Trade($"  ENTER {Tag} back {position.SelectionName} @ {position.BackPrice:F2} for {position.Stake:F2} ({marketName})" +
                  (position.Matched ? "" : $", waiting {settings.PaperFillDelaySeconds}s in-play delay"));
    }

    /// <summary>Call on every poll with the latest snapshot for a market that has an open position.</summary>
    public async Task UpdateAsync(MarketSnapshot snapshot, SelectionSnapshot? over, bool suspended, CancellationToken ct)
    {
        var position = _open.FirstOrDefault(p => p.MarketId == snapshot.MarketId);
        if (position is null) return;

        if (!position.Matched)
        {
            SimulateFill(position, snapshot, over, suspended);
            return;
        }

        if (suspended)
        {
            if (!position.SeenSuspension) Log.Info($"  {position.MarketName}: market suspended (goal?)");
            position.SeenSuspension = true;
            return;
        }

        var price = over?.LastTradedPrice ?? over?.BestBack;
        var held = (snapshot.TimeUtc - position.EntryTimeUtc).TotalSeconds;

        if (price is { } p && p <= signalSettings.GoalPriceThreshold)
        {
            // Over line has effectively won: take the full win.
            Close(position, "GOAL", position.Stake * (position.BackPrice - 1), null);
            return;
        }

        if (position.SeenSuspension && over?.BestLay is { } layAfterSuspension)
        {
            // Market re-opened after a suspension without the line being won (VAR, booking,
            // or goal on a line we don't hold). Close at the current price.
            await CloseAtLayAsync(position, layAfterSuspension, "REOPENED", ct);
            return;
        }

        if (held >= settings.ExitAfterSeconds && over?.BestLay is { } lay)
        {
            await CloseAtLayAsync(position, lay, "TIMEOUT", ct);
        }
    }

    /// <summary>
    /// Betfair holds in-play bets for a few seconds before matching them. If the market suspends in
    /// that time (the goal you were anticipating), the bet is cancelled. It is also unmatched if the
    /// price has moved below what you asked for.
    /// </summary>
    private void SimulateFill(Position position, MarketSnapshot snapshot, SelectionSnapshot? over, bool suspended)
    {
        if (suspended)
        {
            Discard(position, "CANCELLED_SUSPENDED", "market suspended during the bet delay, bet would be cancelled");
            return;
        }
        if ((snapshot.TimeUtc - position.EntryTimeUtc).TotalSeconds < settings.PaperFillDelaySeconds) return;

        if (over?.BestBack is { } back && back >= position.BackPrice)
        {
            position.Matched = true;
            position.EntryTimeUtc = snapshot.TimeUtc;
            Log.Trade($"  MATCH {Tag} {position.SelectionName} @ {position.BackPrice:F2}");
        }
        else
        {
            Discard(position, "UNMATCHED", $"price moved to {over?.BestBack:F2}, bet would not be matched");
        }
    }

    private void Discard(Position position, string reason, string why)
    {
        _open.Remove(position);
        Log.Warn($"  NO FILL {Tag} {position.SelectionName}: {why}");
        trades.Write(
            position.EntryTimeUtc.ToString("o"), DateTime.UtcNow.ToString("o"), settings.Mode.ToString(),
            position.MarketId, position.MarketName, position.SelectionName,
            F(position.BackPrice), F(position.Stake), "", reason, "0", F(SessionPnl));
    }

    /// <summary>Market dropped out of Bet Angel (closed/settled) while we were holding.</summary>
    public void OnMarketGone(string marketId)
    {
        var position = _open.FirstOrDefault(p => p.MarketId == marketId);
        if (position is null) return;
        Log.Warn($"  {position.MarketName}: market no longer returned; result unknown, check Betfair");
        Close(position, "UNKNOWN", 0, null);
    }

    private async Task CloseAtLayAsync(Position position, double layPrice, string reason, CancellationToken ct)
    {
        // Lay stake that gives the same result whatever happens: back stake × back price ÷ lay price.
        var layStake = position.Stake * position.BackPrice / layPrice;
        var pnl = layStake - position.Stake;

        if (settings.Mode == TradingMode.Live)
        {
            if (settings.LiveAutoExit)
            {
                try
                {
                    await client.PlaceBetAsync(position.MarketId, position.SelectionId, "LAY", layPrice, layStake, ct);
                    Log.Info($"  LIVE lay placed {layStake:F2} @ {layPrice:F2}");
                }
                catch (Exception ex)
                {
                    Log.Error($"  LIVE lay failed, close the position manually: {ex.Message}");
                }
            }
            else
            {
                Log.Warn($"  LIVE: close manually in Bet Angel (suggested lay {layStake:F2} @ {layPrice:F2})");
            }
        }

        Close(position, reason, pnl, layPrice);
    }

    private void Close(Position position, string reason, double grossPnl, double? exitPrice)
    {
        var net = grossPnl > 0 ? grossPnl * (1 - settings.CommissionRate) : grossPnl;
        SessionPnl += net;
        _open.Remove(position);

        var held = (DateTime.UtcNow - position.EntryTimeUtc).TotalSeconds;
        Log.Trade($"  EXIT  {Tag} {reason} {position.SelectionName} pnl {net:+0.00;-0.00} (session {SessionPnl:+0.00;-0.00}) after {held:F0}s");

        trades.Write(
            position.EntryTimeUtc.ToString("o"), DateTime.UtcNow.ToString("o"), settings.Mode.ToString(),
            position.MarketId, position.MarketName, position.SelectionName,
            F(position.BackPrice), F(position.Stake), exitPrice is { } x ? F(x) : "",
            reason, F(net), F(SessionPnl));
    }

    private string Tag => settings.Mode == TradingMode.Paper ? "[PAPER]" : "[LIVE]";

    private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}
