using OctopusDashboard.Models;

namespace OctopusDashboard.Domain;

public sealed record TariffCost(
    string Key,
    decimal UnitCostPounds,
    decimal StandingCostPounds,
    decimal TotalPounds,
    decimal AverageUnitRate,
    int UnpricedIntervals);

public sealed record DailyCost(DateOnly Date, decimal Kwh, IReadOnlyDictionary<string, decimal> PoundsByTariff);

public sealed record CostComparison(
    DateOnly From,
    DateOnly To,
    int Days,
    decimal Kwh,
    IReadOnlyList<TariffCost> Tariffs,
    IReadOnlyList<DailyCost> Daily);

public static class CostCalculator
{
    /// <summary>
    /// Prices the same half-hourly usage under each tariff. Only whole UK days that have a full set of readings
    /// are included, so every tariff is compared over exactly the same period.
    /// </summary>
    public static CostComparison? Compare(
        IReadOnlyList<Consumption> usageKwh,
        IReadOnlyDictionary<string, (RateTimeline Unit, RateTimeline Standing)> tariffs)
    {
        var byDay = usageKwh
            .GroupBy(c => UkTime.DateOf(c.IntervalStart))
            .Where(g => g.Count() >= UkTime.HalfHours(g.Key).Count())
            .OrderBy(g => g.Key)
            .ToList();
        if (byDay.Count == 0) return null;

        var totals = tariffs.Keys.ToDictionary(k => k, _ => (Unit: 0m, Standing: 0m, Unpriced: 0));
        var daily = new List<DailyCost>();

        foreach (var day in byDay)
        {
            var poundsToday = new Dictionary<string, decimal>();
            foreach (var (key, (unit, standing)) in tariffs)
            {
                decimal unitPence = 0;
                int unpriced = 0;
                foreach (var reading in day)
                {
                    var price = unit.At(reading.IntervalStart);
                    if (price is null) { unpriced++; continue; }
                    unitPence += reading.Amount * price.Value;
                }
                // Standing charges are per day; use the one in force at midday to dodge midnight boundaries.
                var standingPence = standing.At(UkTime.StartOf(day.Key).AddHours(12)) ?? 0m;

                var t = totals[key];
                totals[key] = (t.Unit + unitPence / 100m, t.Standing + standingPence / 100m, t.Unpriced + unpriced);
                poundsToday[key] = Math.Round((unitPence + standingPence) / 100m, 2);
            }
            daily.Add(new DailyCost(day.Key, Math.Round(day.Sum(c => c.Amount), 3), poundsToday));
        }

        var kwh = byDay.Sum(d => d.Sum(c => c.Amount));
        var results = totals
            .Select(kv => new TariffCost(
                kv.Key,
                Math.Round(kv.Value.Unit, 2),
                Math.Round(kv.Value.Standing, 2),
                Math.Round(kv.Value.Unit + kv.Value.Standing, 2),
                kwh == 0 ? 0 : Math.Round(kv.Value.Unit * 100m / kwh, 2),
                kv.Value.Unpriced))
            // A tariff with missing prices looks artificially cheap, so rank complete ones first.
            .OrderBy(c => c.UnpricedIntervals > 0)
            .ThenBy(c => c.TotalPounds)
            .ToList();

        return new CostComparison(byDay[0].Key, byDay[^1].Key, byDay.Count, Math.Round(kwh, 2), results, daily);
    }
}
