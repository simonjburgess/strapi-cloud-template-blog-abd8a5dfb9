using OctopusDashboard.Models;

namespace OctopusDashboard.Domain;

/// <summary>
/// A set of prices (p/kWh or p/day, inc VAT) ordered by start time, answering "what was the price at time t?".
/// Works for any tariff shape: Agile (half-hourly), Tracker (daily) and Flexible (changes a few times a year).
/// </summary>
public sealed class RateTimeline
{
    private readonly Rate[] _rates;

    public RateTimeline(IEnumerable<Rate> rates)
    {
        // The API can return overlapping rows for different payment methods; prefer direct debit.
        _rates = rates
            .Where(r => r.PaymentMethod is null or "DIRECT_DEBIT")
            .GroupBy(r => r.ValidFrom)
            .Select(g => g.First())
            .OrderBy(r => r.ValidFrom)
            .ToArray();
    }

    public bool IsEmpty => _rates.Length == 0;

    public decimal? At(DateTimeOffset t)
    {
        int lo = 0, hi = _rates.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_rates[mid].ValidFrom <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return null;
        var rate = _rates[found];
        return rate.ValidTo is null || t < rate.ValidTo ? rate.ValueIncVat : null;
    }

    public IEnumerable<Rate> Between(DateTimeOffset from, DateTimeOffset to) =>
        _rates.Where(r => r.ValidFrom < to && (r.ValidTo is null || r.ValidTo > from));
}

public sealed record DailyRate(DateOnly Date, decimal? Average, decimal? Min, decimal? Max);

public static class UkTime
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    public static DateOnly DateOf(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, Zone).DateTime);

    public static DateTimeOffset StartOf(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    /// <summary>The 46/48/50 half-hour slot start times in a UK day (handles clock changes).</summary>
    public static IEnumerable<DateTimeOffset> HalfHours(DateOnly date)
    {
        var start = StartOf(date).ToUniversalTime();
        var end = StartOf(date.AddDays(1)).ToUniversalTime();
        for (var t = start; t < end; t = t.AddMinutes(30)) yield return t;
    }
}

public static class RateMaths
{
    /// <summary>Time-weighted average price per UK day, plus the day's cheapest and dearest half-hour.</summary>
    public static IReadOnlyList<DailyRate> Daily(RateTimeline timeline, DateOnly from, DateOnly to)
    {
        var days = new List<DailyRate>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var values = UkTime.HalfHours(d).Select(timeline.At).Where(v => v is not null).Select(v => v!.Value).ToList();
            days.Add(values.Count == 0
                ? new DailyRate(d, null, null, null)
                : new DailyRate(d, Math.Round(values.Average(), 3), values.Min(), values.Max()));
        }
        return days;
    }

    public static decimal GasToKwh(decimal cubicMetres, decimal calorificValue) =>
        // Standard UK conversion: m³ × volume correction × CV ÷ 3.6.
        cubicMetres * 1.02264m * calorificValue / 3.6m;
}
