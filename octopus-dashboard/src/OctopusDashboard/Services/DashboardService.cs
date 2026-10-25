using Microsoft.Extensions.Options;
using OctopusDashboard.Domain;
using OctopusDashboard.Models;

namespace OctopusDashboard.Services;

public sealed class DashboardService(OctopusClient client, IOptions<OctopusOptions> options, TimeProvider time, ILogger<DashboardService> log)
{
    public const string Tracker = "tracker";
    public const string Agile = "agile";
    public const string Flexible = "flexible";

    private readonly OctopusOptions _options = options.Value;

    private sealed record Loaded(TariffChoice Choice, RateTimeline ElecUnit, RateTimeline ElecStanding, RateTimeline GasUnit, RateTimeline GasStanding);

    public async Task<DashboardResponse> BuildAsync(string? requestedRegion, int days, OctopusCredentials? credentials, CancellationToken ct)
    {
        var warnings = new List<string>();
        var now = time.GetUtcNow();
        var today = UkTime.Today(now);
        var from = today.AddDays(-(days - 1));
        var tomorrow = today.AddDays(1);
        var fromT = UkTime.StartOf(from);
        var toT = UkTime.StartOf(tomorrow.AddDays(1));

        // Your account (optional) tells us your region and your exact Tracker product.
        Account? account = null;
        string? yourElec = null, yourGas = null;
        if (credentials is not null)
        {
            try
            {
                account = await client.GetAccountAsync(credentials, ct);
                (yourElec, yourGas) = CurrentTariffs(account, now);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not load Octopus account");
                warnings.Add($"Couldn't load your Octopus account ({ex.Message}). Showing public prices only.");
            }
        }

        var region = Regions.IsValid(requestedRegion) ? requestedRegion!.ToUpperInvariant()
            : yourElec is not null ? TariffCode.Region(yourElec)
            : _options.DefaultRegion.ToUpperInvariant();

        var choices = await ResolveTariffsAsync(region, yourElec, yourGas, warnings, ct);
        var loaded = (await Task.WhenAll(choices.Select(c => LoadAsync(c, fromT, toT, warnings, ct)))).ToList();

        var summaries = loaded.Select(l => Summarise(l, now, today, tomorrow, from)).ToList();
        var electricity = Series(loaded, l => l.ElecUnit, from, tomorrow);
        var gas = Series(loaded.Where(l => !l.GasUnit.IsEmpty).ToList(), l => l.GasUnit, from, tomorrow);
        var halfHourly = new[] { today, tomorrow }.Select(d => HalfHours(loaded, d)).ToList();

        CostSection? costs = null;
        if (account is not null)
            costs = await CostsAsync(credentials!, account, loaded, fromT, UkTime.StartOf(today), warnings, ct);

        return new DashboardResponse(region, Regions.Names[region], days, from, tomorrow, now, account is not null,
            summaries, electricity, gas, halfHourly, costs, warnings);
    }

    internal static (string? Electricity, string? Gas) CurrentTariffs(Account account, DateTimeOffset now)
    {
        static string? Current(IEnumerable<Agreement> agreements, DateTimeOffset now) => agreements
            .Where(a => (a.ValidFrom is null || a.ValidFrom <= now) && (a.ValidTo is null || a.ValidTo > now))
            .Select(a => a.TariffCode)
            .FirstOrDefault();

        var property = account.Properties.FirstOrDefault();
        if (property is null) return (null, null);
        var elec = property.ElectricityMeterPoints.Where(m => !m.IsExport).Select(m => Current(m.Agreements, now)).FirstOrDefault(c => c is not null);
        var gas = property.GasMeterPoints.Select(m => Current(m.Agreements, now)).FirstOrDefault(c => c is not null);
        return (elec, gas);
    }

    internal static bool IsTrackerCode(string productCode) => productCode.StartsWith("SILVER-", StringComparison.OrdinalIgnoreCase);

    internal static Product? PickLatest(IEnumerable<Product> products, string key)
    {
        Func<Product, bool> match = key switch
        {
            Tracker => p => IsTrackerCode(p.Code),
            Agile => p => p.Code.StartsWith("AGILE-", StringComparison.OrdinalIgnoreCase) && !p.Code.Contains("OUTGOING", StringComparison.OrdinalIgnoreCase),
            Flexible => p => p.Code.StartsWith("VAR-", StringComparison.OrdinalIgnoreCase) && p.DisplayName.Contains("Flexible", StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        var candidates = products.Where(p => p.Direction == "IMPORT" && !p.IsBusiness && !p.IsPrepay).ToList();
        return candidates.Where(match).OrderByDescending(p => p.AvailableFrom).FirstOrDefault()
               // Fall back on the API's own tracker flag in case the SILVER naming ever changes.
               ?? (key == Tracker ? candidates.Where(p => p.IsTracker).OrderByDescending(p => p.AvailableFrom).FirstOrDefault() : null);
    }

    private async Task<List<TariffChoice>> ResolveTariffsAsync(string region, string? yourElec, string? yourGas, List<string> warnings, CancellationToken ct)
    {
        List<Product> products = [];
        try { products = await client.GetProductsAsync(ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not list products");
            warnings.Add($"Couldn't list Octopus products ({ex.Message}).");
        }

        var yourProduct = yourElec is not null ? TariffCode.ProductCode(yourElec) : null;
        var yourGasProduct = yourGas is not null ? TariffCode.ProductCode(yourGas) : null;

        var choices = new List<TariffChoice>();
        foreach (var (key, label, overrideCode) in new[]
                 {
                     (Tracker, "Tracker", _options.TrackerProductCode),
                     (Agile, "Agile", _options.AgileProductCode),
                     (Flexible, "Flexible", _options.FlexibleProductCode),
                 })
        {
            // Precedence: explicit config, then the Tracker you are actually on, then the latest one on sale.
            var isYours = key == Tracker && string.IsNullOrWhiteSpace(overrideCode) && yourProduct is not null && IsTrackerCode(yourProduct);
            var code = !string.IsNullOrWhiteSpace(overrideCode) ? overrideCode.Trim()
                : isYours ? yourProduct
                : PickLatest(products, key)?.Code;
            if (yourProduct is not null && code == yourProduct) isYours = true;

            if (code is null)
            {
                warnings.Add($"Couldn't find a {label} product on sale. Set Octopus:{label}ProductCode in appsettings.json to compare it.");
                continue;
            }

            var (elec, gas, name) = await TariffCodesAsync(code, region, ct);
            // Your gas Tracker can be a different product from your electricity one.
            if (key == Tracker && isYours && yourGasProduct is not null && IsTrackerCode(yourGasProduct))
                gas = TariffCode.Build(Fuel.Gas, yourGasProduct, region);

            choices.Add(new TariffChoice(key, isYours ? $"{label} (your tariff)" : label, code, elec, key == Agile ? null : gas, isYours));
        }
        return choices;
    }

    private async Task<(string? Elec, string? Gas, string? Name)> TariffCodesAsync(string productCode, string region, CancellationToken ct)
    {
        static string? Pick(Dictionary<string, Dictionary<string, TariffInfo>>? tariffs, string region) =>
            tariffs is not null && tariffs.TryGetValue($"_{region}", out var byPayment)
                ? (byPayment.TryGetValue("direct_debit_monthly", out var dd) ? dd : byPayment.Values.FirstOrDefault())?.Code
                : null;

        try
        {
            var detail = await client.GetProductAsync(productCode, ct);
            return (Pick(detail.SingleRegisterElectricityTariffs, region), Pick(detail.SingleRegisterGasTariffs, region), detail.DisplayName);
        }
        catch (Exception ex)
        {
            // Older products drop out of the detail endpoint, but their rates are still served; build the code ourselves.
            log.LogInformation(ex, "No product detail for {Product}; building tariff codes", productCode);
            return (TariffCode.Build(Fuel.Electricity, productCode, region), TariffCode.Build(Fuel.Gas, productCode, region), null);
        }
    }

    private async Task<Loaded> LoadAsync(TariffChoice c, DateTimeOffset from, DateTimeOffset to, List<string> warnings, CancellationToken ct)
    {
        async Task<RateTimeline> Fetch(Fuel fuel, string? tariff, bool standing)
        {
            if (tariff is null) return new RateTimeline([]);
            try
            {
                var rates = standing
                    ? await client.GetStandingChargesAsync(fuel, c.ProductCode, tariff, from, to, ct)
                    : await client.GetUnitRatesAsync(fuel, c.ProductCode, tariff, from, to, ct);
                return new RateTimeline(rates);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to load {Tariff}", tariff);
                lock (warnings) warnings.Add($"Couldn't load {(standing ? "standing charges" : "unit rates")} for {tariff} ({ex.Message}).");
                return new RateTimeline([]);
            }
        }

        var results = await Task.WhenAll(
            Fetch(Fuel.Electricity, c.ElectricityTariffCode, false),
            Fetch(Fuel.Electricity, c.ElectricityTariffCode, true),
            Fetch(Fuel.Gas, c.GasTariffCode, false),
            Fetch(Fuel.Gas, c.GasTariffCode, true));
        return new Loaded(c, results[0], results[1], results[2], results[3]);
    }

    private static TariffSummary Summarise(Loaded l, DateTimeOffset now, DateOnly today, DateOnly tomorrow, DateOnly from)
    {
        static decimal? DayAvg(RateTimeline t, DateOnly d) => RateMaths.Daily(t, d, d)[0].Average;
        static decimal? PeriodAvg(RateTimeline t, DateOnly from, DateOnly to)
        {
            var values = RateMaths.Daily(t, from, to).Where(d => d.Average is not null).Select(d => d.Average!.Value).ToList();
            return values.Count == 0 ? null : Math.Round(values.Average(), 2);
        }
        var noon = UkTime.StartOf(today).AddHours(12);

        return new TariffSummary(
            l.Choice.Key, l.Choice.Name, l.Choice.ProductCode, l.Choice.ElectricityTariffCode, l.Choice.GasTariffCode, l.Choice.IsYours,
            l.ElecUnit.At(now), DayAvg(l.ElecUnit, today), DayAvg(l.ElecUnit, tomorrow), PeriodAvg(l.ElecUnit, from, today), l.ElecStanding.At(noon),
            DayAvg(l.GasUnit, today), DayAvg(l.GasUnit, tomorrow), PeriodAvg(l.GasUnit, from, today), l.GasStanding.At(noon));
    }

    private static FuelSeries Series(IReadOnlyList<Loaded> loaded, Func<Loaded, RateTimeline> pick, DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) dates.Add(d);

        var avg = new Dictionary<string, IReadOnlyList<decimal?>>();
        var min = new Dictionary<string, IReadOnlyList<decimal?>>();
        var max = new Dictionary<string, IReadOnlyList<decimal?>>();
        foreach (var l in loaded)
        {
            var daily = RateMaths.Daily(pick(l), from, to);
            avg[l.Choice.Key] = daily.Select(d => d.Average).ToList();
            min[l.Choice.Key] = daily.Select(d => d.Min).ToList();
            max[l.Choice.Key] = daily.Select(d => d.Max).ToList();
        }
        return new FuelSeries(dates, avg, min, max);
    }

    private static DayHalfHours HalfHours(IReadOnlyList<Loaded> loaded, DateOnly date)
    {
        var slots = UkTime.HalfHours(date).ToList();
        var labels = slots.Select(s => TimeZoneInfo.ConvertTime(s, UkTime.Zone).ToString("HH:mm")).ToList();
        var series = loaded.ToDictionary(l => l.Choice.Key, l => (IReadOnlyList<decimal?>)slots.Select(l.ElecUnit.At).ToList());
        return new DayHalfHours(date, labels, series);
    }

    private async Task<CostSection> CostsAsync(OctopusCredentials credentials, Account account, List<Loaded> loaded, DateTimeOffset from, DateTimeOffset to, List<string> warnings, CancellationToken ct)
    {
        var notes = new List<string>();
        var property = account.Properties.FirstOrDefault();

        CostComparison? elec = null, gas = null;
        var elecPoint = property?.ElectricityMeterPoints.FirstOrDefault(m => !m.IsExport);
        if (elecPoint is not null)
        {
            var usage = await BestMeterUsageAsync(credentials, Fuel.Electricity, elecPoint.Mpan, elecPoint.Meters, from, to, warnings, ct);
            elec = CostCalculator.Compare(usage, loaded.Where(l => !l.ElecUnit.IsEmpty).ToDictionary(l => l.Choice.Key, l => (l.ElecUnit, l.ElecStanding)));
            if (elec is null) notes.Add("No complete days of smart meter electricity readings found for this period yet.");
        }

        var gasPoint = property?.GasMeterPoints.FirstOrDefault();
        if (gasPoint is not null)
        {
            var usage = await BestMeterUsageAsync(credentials, Fuel.Gas, gasPoint.Mprn, gasPoint.Meters, from, to, warnings, ct);
            if (_options.GasConsumptionInCubicMetres)
            {
                usage = usage.Select(u => u with { Amount = RateMaths.GasToKwh(u.Amount, _options.GasCalorificValue) }).ToList();
                notes.Add($"Gas readings converted from m³ to kWh using a calorific value of {_options.GasCalorificValue} MJ/m³. Set Octopus:GasConsumptionInCubicMetres to false if you have a SMETS1 meter.");
            }
            gas = CostCalculator.Compare(usage, loaded.Where(l => !l.GasUnit.IsEmpty).ToDictionary(l => l.Choice.Key, l => (l.GasUnit, l.GasStanding)));
        }

        notes.Add("Costs include VAT and standing charges. Readings usually arrive a day or so late, so the most recent days may be missing.");
        return new CostSection(elec, gas, notes);
    }

    private async Task<List<Consumption>> BestMeterUsageAsync(OctopusCredentials credentials, Fuel fuel, string point, List<Meter> meters, DateTimeOffset from, DateTimeOffset to, List<string> warnings, CancellationToken ct)
    {
        // Properties often list old, replaced meters too; use whichever serial actually has readings.
        List<Consumption> best = [];
        foreach (var meter in meters)
        {
            try
            {
                var usage = await client.GetConsumptionAsync(credentials, fuel, point, meter.SerialNumber, from, to, ct);
                if (usage.Count > best.Count) best = usage;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Consumption failed for {Point}/{Serial}", point, meter.SerialNumber);
            }
        }
        if (best.Count == 0) warnings.Add($"No {fuel.ApiName()} readings returned for meter point {point}.");
        return best;
    }
}
