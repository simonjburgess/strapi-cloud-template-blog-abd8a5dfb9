namespace GoalMonitor;

/// <summary>Betfair price ladder helpers.</summary>
public static class Ticks
{
    private static readonly (double UpTo, double Step)[] Bands =
    [
        (2, 0.01), (3, 0.02), (4, 0.05), (6, 0.1), (10, 0.2),
        (20, 0.5), (30, 1), (50, 2), (100, 5), (1000, 10),
    ];

    /// <summary>Ticks from 1.01 to <paramref name="price"/> (approximate for off-ladder prices).</summary>
    public static double Index(double price)
    {
        double index = 0, lower = 1.0;
        foreach (var (upTo, step) in Bands)
        {
            if (price <= upTo) return index + (price - lower) / step;
            index += (upTo - lower) / step;
            lower = upTo;
        }
        return index;
    }

    public static double Between(double from, double to) => Index(from) - Index(to);

    /// <summary>The ladder price <paramref name="count"/> ticks below <paramref name="price"/> (never below 1.01).</summary>
    public static double Down(double price, int count)
    {
        for (var i = 0; i < count && price > 1.01; i++)
        {
            var step = Bands.First(b => price <= b.UpTo + 1e-9).Step;
            var lowerBand = Bands.LastOrDefault(b => b.UpTo < price - 1e-9);
            if (lowerBand.UpTo > 0 && price - step < lowerBand.UpTo - 1e-9) step = lowerBand.Step;
            price = Math.Round(price - step, 2);
        }
        return Math.Max(price, 1.01);
    }
}

public sealed record Signal(
    string MarketId,
    string SelectionId,
    string SelectionName,
    DateTime TimeUtc,
    double ShortWindowVolume,
    double BaselineVolumePerWindow,
    double TicksShortened,
    double BackPrice);

/// <summary>
/// Keeps a short history of each market's total matched volume and the Over price, and fires
/// when the most recent window shows a burst of matching that's well above the baseline rate.
/// </summary>
public sealed class SignalDetector(SignalSettings settings)
{
    private sealed record Point(DateTime Time, double Volume, double? OverPrice);

    private readonly Dictionary<string, List<Point>> _history = new();
    private readonly Dictionary<string, DateTime> _lastSignal = new();

    public Signal? Update(MarketSnapshot snapshot, SelectionSnapshot over, bool suspended)
    {
        var volume = snapshot.TotalVolume;
        if (volume is null) return null;

        if (!_history.TryGetValue(snapshot.MarketId, out var points))
        {
            points = [];
            _history[snapshot.MarketId] = points;
        }

        var overPrice = over.LastTradedPrice ?? over.BestBack;
        points.Add(new Point(snapshot.TimeUtc, volume.Value, overPrice));
        var keepFrom = snapshot.TimeUtc.AddSeconds(-settings.BaselineWindowSeconds - settings.ShortWindowSeconds);
        points.RemoveAll(p => p.Time < keepFrom);

        if (suspended || over.BestBack is not { } back) return null;
        if (back < settings.MinOverPrice || back > settings.MaxOverPrice) return null;
        if (_lastSignal.TryGetValue(snapshot.MarketId, out var last) &&
            (snapshot.TimeUtc - last).TotalSeconds < settings.CooldownSeconds) return null;

        var first = points[0];
        if ((snapshot.TimeUtc - first.Time).TotalSeconds < settings.WarmupSeconds) return null;

        var shortStart = snapshot.TimeUtc.AddSeconds(-settings.ShortWindowSeconds);
        var atShortStart = points.LastOrDefault(p => p.Time <= shortStart);
        if (atShortStart is null) return null;

        var shortVolume = volume.Value - atShortStart.Volume;
        var baselineSeconds = (atShortStart.Time - first.Time).TotalSeconds;
        if (baselineSeconds <= 0) return null;
        var baselinePerWindow = (atShortStart.Volume - first.Volume) / baselineSeconds * settings.ShortWindowSeconds;

        if (shortVolume < settings.MinShortWindowVolume) return null;
        if (shortVolume < settings.SpikeMultiplier * Math.Max(baselinePerWindow, 1)) return null;

        double ticks = 0;
        if (atShortStart.OverPrice is { } before && overPrice is { } now)
        {
            ticks = Ticks.Between(before, now);
        }
        if (settings.MinTicksShortened > 0 && ticks < settings.MinTicksShortened) return null;

        _lastSignal[snapshot.MarketId] = snapshot.TimeUtc;
        return new Signal(snapshot.MarketId, over.Id, over.Name, snapshot.TimeUtc,
            shortVolume, baselinePerWindow, ticks, back);
    }
}
