using System.Text.Json;

namespace GoalMonitor;

public enum TradingMode
{
    /// <summary>Simulate entries and exits; no bets are sent to Bet Angel.</summary>
    Paper,
    /// <summary>Send real bets to Bet Angel. Real money.</summary>
    Live
}

public sealed class AppConfig
{
    public BetAngelSettings BetAngel { get; set; } = new();
    public MarketSelection Markets { get; set; } = new();
    public SignalSettings Signal { get; set; } = new();
    public TradingSettings Trading { get; set; } = new();
    public FieldNames Fields { get; set; } = new();
    public string LogDirectory { get; set; } = "logs";

    public static AppConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Deserialize<AppConfig>(json, options)
               ?? throw new InvalidOperationException($"Could not read config from {path}");
    }
}

public sealed class BetAngelSettings
{
    public string BaseUrl { get; set; } = "http://localhost:9000";
    public int PollIntervalMs { get; set; } = 1000;
    public int MarketRefreshSeconds { get; set; } = 30;
    public string[] MarketDataRequired { get; set; } =
        ["ID", "NAME", "MARKET_TYPE", "SELECTION_IDS", "SELECTION_NAMES"];
    public string[] PriceDataRequired { get; set; } =
        ["LAST_TRADED_PRICE", "BEST_PRICE_ONLY", "VOLUME"];
}

public sealed class MarketSelection
{
    /// <summary>A market is monitored if its market type starts with any of these (e.g. OVER_UNDER_25).</summary>
    public string[] MarketTypePrefixes { get; set; } = ["OVER_UNDER"];
    /// <summary>Fallback when market type isn't returned: match on market name.</summary>
    public string[] MarketNameContains { get; set; } = ["Over/Under"];
    /// <summary>The selection we back on a signal, matched by name prefix ("Over 2.5 Goals").</summary>
    public string SelectionNamePrefix { get; set; } = "Over";
}

public sealed class SignalSettings
{
    /// <summary>Recent window that must show a volume burst.</summary>
    public int ShortWindowSeconds { get; set; } = 10;
    /// <summary>Longer window used to work out the "normal" matching rate.</summary>
    public int BaselineWindowSeconds { get; set; } = 300;
    /// <summary>Need at least this much history before signalling.</summary>
    public int WarmupSeconds { get; set; } = 120;
    /// <summary>Short-window volume must be this many times the baseline rate.</summary>
    public double SpikeMultiplier { get; set; } = 3.0;
    /// <summary>Ignore bursts smaller than this (in account currency) in the short window.</summary>
    public double MinShortWindowVolume { get; set; } = 500;
    /// <summary>Over price must have shortened by at least this many ticks during the short window. 0 disables.</summary>
    public int MinTicksShortened { get; set; } = 2;
    /// <summary>Only trade the Over line while its price is in this range (picks the "next goal" line).</summary>
    public double MinOverPrice { get; set; } = 1.5;
    public double MaxOverPrice { get; set; } = 6.0;
    /// <summary>Over price at or below this means the goal has gone in (line won).</summary>
    public double GoalPriceThreshold { get; set; } = 1.10;
    /// <summary>Don't signal the same market again within this many seconds.</summary>
    public int CooldownSeconds { get; set; } = 180;
}

public sealed class TradingSettings
{
    public TradingMode Mode { get; set; } = TradingMode.Paper;
    public double Stake { get; set; } = 2.0;
    /// <summary>Ask for this many ticks below the best back price, to improve the chance of being matched.</summary>
    public int EntryTicksBelowBest { get; set; } = 0;
    /// <summary>
    /// Paper mode: seconds to wait before treating the back bet as matched, mimicking Betfair's in-play
    /// bet delay. If the market suspends during the wait the bet counts as cancelled. 0 = instant fill.
    /// </summary>
    public int PaperFillDelaySeconds { get; set; } = 5;
    public int MaxOpenPositions { get; set; } = 1;
    /// <summary>Close the trade if no goal within this many seconds of entry.</summary>
    public int ExitAfterSeconds { get; set; } = 90;
    /// <summary>Betfair commission on net market winnings (0.05 = 5%).</summary>
    public double CommissionRate { get; set; } = 0.05;
    /// <summary>Stop opening new trades once session P&amp;L falls to -this amount.</summary>
    public double MaxSessionLoss { get; set; } = 20.0;
    /// <summary>
    /// Live mode only: let the app place the closing lay bet. Off by default because the app
    /// cannot yet confirm how much of the entry bet was matched (see README).
    /// </summary>
    public bool LiveAutoExit { get; set; } = false;
}

/// <summary>
/// Candidate JSON field names for Bet Angel responses. The first name found wins (case-insensitive).
/// Run with --dump and adjust these if your Bet Angel version uses different names.
/// </summary>
public sealed class FieldNames
{
    public string[] MarketId { get; set; } = ["id", "marketId"];
    public string[] MarketName { get; set; } = ["name", "marketName"];
    public string[] MarketType { get; set; } = ["marketType", "type"];
    public string[] MarketStatus { get; set; } = ["status", "marketStatus"];
    public string[] MarketVolume { get; set; } = ["totalMatched", "volume", "tradedVolume", "matched"];
    public string[] Selections { get; set; } = ["selections", "runners"];
    public string[] SelectionId { get; set; } = ["id", "selectionId"];
    public string[] SelectionName { get; set; } = ["name", "selectionName"];
    public string[] SelectionVolume { get; set; } = ["totalMatched", "volume", "tradedVolume", "matched"];
    public string[] LastTradedPrice { get; set; } = ["lastTradedPrice", "ltp", "lastPriceTraded", "lastTraded"];
    public string[] BackPrices { get; set; } = ["backPrices", "availableToBack", "bestBackPrices", "back"];
    public string[] LayPrices { get; set; } = ["layPrices", "availableToLay", "bestLayPrices", "lay"];
    public string[] Price { get; set; } = ["price", "odds"];
    public string[] SuspendedStatusContains { get; set; } = ["SUSPEND", "CLOSED"];
}
