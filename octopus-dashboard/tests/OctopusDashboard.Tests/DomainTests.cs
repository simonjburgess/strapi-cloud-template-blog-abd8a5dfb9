using OctopusDashboard.Domain;
using OctopusDashboard.Models;
using OctopusDashboard.Services;
using Xunit;

namespace OctopusDashboard.Tests;

public class DomainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(1));

    private static Rate R(decimal v, DateTimeOffset from, DateTimeOffset? to, string? pm = null) => new(v / 1.05m, v, from, to, pm);

    [Fact]
    public void TariffCode_round_trips()
    {
        Assert.Equal("SILVER-24-10-01", TariffCode.ProductCode("E-1R-SILVER-24-10-01-C"));
        Assert.Equal("AGILE-FLEX-22-11-25", TariffCode.ProductCode("E-1R-AGILE-FLEX-22-11-25-M"));
        Assert.Equal("C", TariffCode.Region("E-1R-SILVER-24-10-01-C"));
        Assert.Equal("G-1R-VAR-22-11-01-J", TariffCode.Build(Fuel.Gas, "VAR-22-11-01", "j"));
    }

    [Fact]
    public void Timeline_respects_bounds_and_prefers_direct_debit()
    {
        var t = new RateTimeline([
            R(30, T0, T0.AddDays(1), "NON_DIRECT_DEBIT"),
            R(25, T0, T0.AddDays(1), "DIRECT_DEBIT"),
            R(20, T0.AddDays(2), null),
        ]);

        Assert.Null(t.At(T0.AddMinutes(-1)));
        Assert.Equal(25, t.At(T0.AddHours(3)));
        Assert.Null(t.At(T0.AddDays(1).AddHours(1))); // gap
        Assert.Equal(20, t.At(T0.AddDays(40)));        // open-ended
    }

    [Fact]
    public void Daily_handles_clock_change_day()
    {
        var clocksGoBack = new DateOnly(2026, 10, 25);
        Assert.Equal(50, UkTime.HalfHours(clocksGoBack).Count());
        Assert.Equal(48, UkTime.HalfHours(clocksGoBack.AddDays(1)).Count());

        // Cheap overnight, dear otherwise: the average is time-weighted across all 50 slots.
        var start = UkTime.StartOf(clocksGoBack);
        var t = new RateTimeline([R(10, start, start.AddHours(5)), R(30, start.AddHours(5), null)]);
        var day = RateMaths.Daily(t, clocksGoBack, clocksGoBack).Single();
        Assert.Equal(10, day.Min);
        Assert.Equal(30, day.Max);
        Assert.Equal(Math.Round((10m * 10 + 30m * 40) / 50, 3), day.Average);
    }

    [Fact]
    public void Costs_compare_same_complete_days_and_rank_incomplete_last()
    {
        var day = new DateOnly(2026, 10, 20);
        var usage = UkTime.HalfHours(day).Select(s => new Consumption(0.5m, s, s.AddMinutes(30)))
            // A partial second day must be ignored for every tariff.
            .Concat(UkTime.HalfHours(day.AddDays(1)).Take(10).Select(s => new Consumption(5m, s, s.AddMinutes(30))))
            .ToList();
        var standing = new RateTimeline([R(50, T0.AddYears(-1), null)]);
        var start = UkTime.StartOf(day);

        var result = CostCalculator.Compare(usage, new Dictionary<string, (RateTimeline, RateTimeline)>
        {
            ["flat"] = (new RateTimeline([R(20, T0.AddYears(-1), null)]), standing),
            ["cheap"] = (new RateTimeline([R(10, T0.AddYears(-1), null)]), standing),
            // Prices missing for the second half of the day, so its total looks lowest of all.
            ["gappy"] = (new RateTimeline([R(1, start, start.AddHours(12))]), standing),
        })!;

        Assert.Equal(1, result.Days);
        Assert.Equal(24m, result.Kwh);
        Assert.Equal(["cheap", "flat", "gappy"], result.Tariffs.Select(t => t.Key));
        var flat = result.Tariffs.Single(t => t.Key == "flat");
        Assert.Equal(4.80m, flat.UnitCostPounds);
        Assert.Equal(0.50m, flat.StandingCostPounds);
        Assert.Equal(5.30m, flat.TotalPounds);
        Assert.Equal(20m, flat.AverageUnitRate);
        Assert.Equal(24, result.Tariffs.Single(t => t.Key == "gappy").UnpricedIntervals);
    }

    [Fact]
    public void Costs_return_null_without_a_complete_day()
    {
        var s = UkTime.StartOf(new DateOnly(2026, 10, 20));
        Assert.Null(CostCalculator.Compare([new Consumption(1, s, s.AddMinutes(30))], new Dictionary<string, (RateTimeline, RateTimeline)>()));
    }

    [Fact]
    public void PickLatest_chooses_newest_import_product()
    {
        Product P(string code, string direction = "IMPORT", string name = "", bool prepay = false, string from = "2024-01-01") =>
            new(code, direction, name, name, true, false, code.StartsWith("SILVER"), prepay, false, false, DateTimeOffset.Parse(from), null, "OCTOPUS_ENERGY");

        var products = new[]
        {
            P("SILVER-23-12-06", from: "2023-12-06"),
            P("SILVER-24-10-01", from: "2024-10-01"),
            P("AGILE-OUTGOING-19-05-13", "EXPORT", from: "2025-01-01"),
            P("AGILE-24-10-01", from: "2024-10-01"),
            P("VAR-22-11-01", name: "Flexible Octopus", from: "2022-11-01"),
            P("VAR-25-01-01", name: "Flexible Octopus Prepay", prepay: true, from: "2025-01-01"),
        };

        Assert.Equal("SILVER-24-10-01", DashboardService.PickLatest(products, DashboardService.Tracker)?.Code);
        Assert.Equal("AGILE-24-10-01", DashboardService.PickLatest(products, DashboardService.Agile)?.Code);
        Assert.Equal("VAR-22-11-01", DashboardService.PickLatest(products, DashboardService.Flexible)?.Code);
    }

    [Fact]
    public void CurrentTariffs_ignores_ended_agreements_and_export()
    {
        var now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        var account = new Account("A-1", [new AccountProperty(
            [
                new ElectricityMeterPoint("2", [], [new Agreement("E-1R-AGILE-OUTGOING-19-05-13-C", null, null)], IsExport: true),
                new ElectricityMeterPoint("1", [], [
                    new Agreement("E-1R-VAR-22-11-01-C", DateTimeOffset.Parse("2023-01-01Z"), DateTimeOffset.Parse("2025-03-01Z")),
                    new Agreement("E-1R-SILVER-24-10-01-C", DateTimeOffset.Parse("2025-03-01Z"), null),
                ], IsExport: false),
            ],
            [new GasMeterPoint("9", [], [new Agreement("G-1R-SILVER-24-10-01-C", DateTimeOffset.Parse("2025-03-01Z"), null)])])]);

        var (elec, gas) = DashboardService.CurrentTariffs(account, now);
        Assert.Equal("E-1R-SILVER-24-10-01-C", elec);
        Assert.Equal("G-1R-SILVER-24-10-01-C", gas);
    }

    [Fact]
    public void Gas_conversion_uses_standard_formula() =>
        Assert.Equal(11.22m, Math.Round(RateMaths.GasToKwh(1m, 39.5m), 2));
}
