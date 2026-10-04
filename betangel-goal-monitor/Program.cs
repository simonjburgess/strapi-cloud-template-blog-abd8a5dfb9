using System.Globalization;
using System.Text.Json;
using GoalMonitor;

var configPath = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var config = AppConfig.Load(configPath);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

using var client = new BetAngelClient(config.BetAngel, config.Fields);

if (args.Contains("--dump"))
{
    await DumpAsync();
    return;
}

if (config.Trading.Mode == TradingMode.Live)
{
    Log.Warn("LIVE MODE: real bets will be placed. Type LIVE to continue:");
    if (Console.ReadLine()?.Trim() != "LIVE") return;
}

var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
using var ticks = new CsvLog(Path.Combine(config.LogDirectory, $"ticks_{stamp}.csv"),
    "timeUtc", "marketId", "status", "marketVolume", "selectionId", "selection", "ltp", "back", "lay", "selectionVolume");
using var signals = new CsvLog(Path.Combine(config.LogDirectory, $"signals_{stamp}.csv"),
    "timeUtc", "marketId", "market", "selection", "shortWindowVolume", "baselinePerWindow", "ticksShortened", "backPrice");
using var trades = new CsvLog(Path.Combine(config.LogDirectory, $"trades_{stamp}.csv"),
    "entryUtc", "exitUtc", "mode", "marketId", "market", "selection", "backPrice", "stake", "exitPrice", "reason", "netPnl", "sessionPnl");

var detector = new SignalDetector(config.Signal);
var trader = new Trader(config.Trading, config.Signal, client, trades);

Log.Info($"Goal monitor starting in {config.Trading.Mode} mode against {config.BetAngel.BaseUrl}. Ctrl+C to stop.");

var markets = new Dictionary<string, MarketInfo>();
var lastRefresh = DateTime.MinValue;

while (!ct.IsCancellationRequested)
{
    var loopStart = DateTime.UtcNow;
    try
    {
        if ((loopStart - lastRefresh).TotalSeconds >= config.BetAngel.MarketRefreshSeconds)
        {
            await RefreshMarketsAsync();
            lastRefresh = loopStart;
        }

        if (markets.Count > 0)
        {
            var snapshots = await client.GetMarketPricesAsync(markets.Keys, ct);
            var returned = snapshots.Select(s => s.MarketId).ToHashSet();

            foreach (var snapshot in snapshots)
            {
                await ProcessAsync(snapshot);
            }

            foreach (var gone in markets.Keys.Where(id => !returned.Contains(id) && trader.HasPosition(id)).ToList())
            {
                trader.OnMarketGone(gone);
            }
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        break;
    }
    catch (Exception ex)
    {
        Log.Error($"Poll failed: {ex.Message}");
    }

    var delay = config.BetAngel.PollIntervalMs - (int)(DateTime.UtcNow - loopStart).TotalMilliseconds;
    try { await Task.Delay(Math.Max(delay, 50), ct); }
    catch (OperationCanceledException) { break; }
}

Log.Info($"Stopped. Session P&L {trader.SessionPnl:+0.00;-0.00}, open positions {trader.OpenCount}.");
if (trader.OpenCount > 0) Log.Warn("Positions still open: check Bet Angel.");

async Task RefreshMarketsAsync()
{
    var all = await client.GetMarketsAsync(ct);
    var wanted = all.Where(IsWantedMarket).ToList();
    var added = wanted.Where(m => !markets.ContainsKey(m.Id)).ToList();

    markets.Clear();
    foreach (var m in wanted) markets[m.Id] = m;

    foreach (var m in added) Log.Info($"Monitoring {m.Name} ({m.Id})");
    if (all.Count > 0 && wanted.Count == 0)
        Log.Warn($"{all.Count} market(s) loaded in Bet Angel but none match the Over/Under filter.");
    if (all.Count == 0)
        Log.Warn("No markets loaded in Bet Angel. Add in-play Over/Under markets to Guardian.");
}

bool IsWantedMarket(MarketInfo m) =>
    config.Markets.MarketTypePrefixes.Any(p => m.MarketType.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
    config.Markets.MarketNameContains.Any(n => m.Name.Contains(n, StringComparison.OrdinalIgnoreCase));

async Task ProcessAsync(MarketSnapshot snapshot)
{
    if (!markets.TryGetValue(snapshot.MarketId, out var market)) return;

    // getMarketPrices may not return names, so fall back to the names from getMarkets.
    string NameOf(SelectionSnapshot s) => s.Name.Length > 0
        ? s.Name
        : market.Selections.FirstOrDefault(r => r.Id == s.Id)?.Name ?? "";

    foreach (var s in snapshot.Selections)
    {
        ticks.Write(snapshot.TimeUtc.ToString("o"), snapshot.MarketId, snapshot.Status ?? "", F(snapshot.Volume),
            s.Id, NameOf(s), F(s.LastTradedPrice), F(s.BestBack), F(s.BestLay), F(s.Volume));
    }

    var over = snapshot.Selections.FirstOrDefault(s =>
        NameOf(s).StartsWith(config.Markets.SelectionNamePrefix, StringComparison.OrdinalIgnoreCase));
    var overNamed = over is null ? null : over with { Name = NameOf(over) };

    var suspended = IsSuspended(snapshot);

    if (trader.HasPosition(snapshot.MarketId))
    {
        await trader.UpdateAsync(snapshot, overNamed, suspended, ct);
    }

    if (overNamed is null) return;

    var signal = detector.Update(snapshot, overNamed, suspended);
    if (signal is null) return;

    Log.Signal($"SIGNAL {market.Name}: {signal.ShortWindowVolume:F0} matched in {config.Signal.ShortWindowSeconds}s " +
               $"(baseline {signal.BaselineVolumePerWindow:F0}), {signal.SelectionName} in {signal.TicksShortened:F1} ticks, back {signal.BackPrice:F2}");
    signals.Write(signal.TimeUtc.ToString("o"), signal.MarketId, market.Name, signal.SelectionName,
        F(signal.ShortWindowVolume), F(signal.BaselineVolumePerWindow), F(signal.TicksShortened), F(signal.BackPrice));

    await trader.OnSignalAsync(signal, market.Name, ct);
}

bool IsSuspended(MarketSnapshot snapshot)
{
    if (snapshot.Status is { } status &&
        config.Fields.SuspendedStatusContains.Any(x => status.Contains(x, StringComparison.OrdinalIgnoreCase)))
        return true;
    // No prices offered on any selection is treated as a suspension.
    return snapshot.Selections.Count > 0 && snapshot.Selections.All(s => s.BestBack is null && s.BestLay is null);
}

async Task DumpAsync()
{
    var options = new JsonSerializerOptions { WriteIndented = true };
    Directory.CreateDirectory(config.LogDirectory);

    var marketsRaw = await client.GetMarketsRawAsync(ct);
    var marketsFile = Path.Combine(config.LogDirectory, "dump_getMarkets.json");
    await File.WriteAllTextAsync(marketsFile, marketsRaw.ToJsonString(options), ct);
    Log.Info($"Wrote {marketsFile}");

    var parsed = await client.GetMarketsAsync(ct);
    foreach (var m in parsed)
        Log.Info($"  {m.Id} | {m.Name} | type '{m.MarketType}' | {m.Selections.Count} selections | monitored: {IsWantedMarket(m)}");

    if (parsed.Count == 0)
    {
        Log.Warn("No markets parsed. Load some markets in Bet Angel, or check Fields in appsettings.json against the dump.");
        return;
    }

    var pricesRaw = await client.GetMarketPricesRawAsync(parsed.Select(m => m.Id), ct);
    var pricesFile = Path.Combine(config.LogDirectory, "dump_getMarketPrices.json");
    await File.WriteAllTextAsync(pricesFile, pricesRaw.ToJsonString(options), ct);
    Log.Info($"Wrote {pricesFile}");

    foreach (var s in await client.GetMarketPricesAsync(parsed.Select(m => m.Id), ct))
    {
        Log.Info($"  {s.MarketId} status '{s.Status}' volume {F(s.TotalVolume)}");
        foreach (var sel in s.Selections)
            Log.Info($"    {sel.Id} {sel.Name}: ltp {F(sel.LastTradedPrice)} back {F(sel.BestBack)} lay {F(sel.BestLay)} vol {F(sel.Volume)}");
    }
    Log.Info("Any blank values above mean a field name didn't match. Compare with the dump files and edit Fields.");
}

static string F(double? d) => d?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
