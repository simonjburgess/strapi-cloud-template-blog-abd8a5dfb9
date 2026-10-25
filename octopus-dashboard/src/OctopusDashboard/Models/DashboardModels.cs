using OctopusDashboard.Domain;

namespace OctopusDashboard.Models;

// Shapes returned by this app's own /api endpoints to the browser.

public sealed record TariffChoice(string Key, string Name, string ProductCode, string? ElectricityTariffCode, string? GasTariffCode, bool IsYours);

public sealed record TariffSummary(
    string Key,
    string Name,
    string ProductCode,
    string? ElectricityTariffCode,
    string? GasTariffCode,
    bool IsYours,
    decimal? ElectricityNow,
    decimal? ElectricityToday,
    decimal? ElectricityTomorrow,
    decimal? ElectricityPeriodAverage,
    decimal? ElectricityStandingCharge,
    decimal? GasToday,
    decimal? GasTomorrow,
    decimal? GasPeriodAverage,
    decimal? GasStandingCharge);

public sealed record FuelSeries(
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyDictionary<string, IReadOnlyList<decimal?>> Average,
    IReadOnlyDictionary<string, IReadOnlyList<decimal?>> Min,
    IReadOnlyDictionary<string, IReadOnlyList<decimal?>> Max);

public sealed record DayHalfHours(DateOnly Date, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, IReadOnlyList<decimal?>> Series);

public sealed record CostSection(CostComparison? Electricity, CostComparison? Gas, IReadOnlyList<string> Notes);

public sealed record DashboardResponse(
    string Region,
    string RegionName,
    int Days,
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAt,
    bool HasAccount,
    IReadOnlyList<TariffSummary> Tariffs,
    FuelSeries Electricity,
    FuelSeries Gas,
    IReadOnlyList<DayHalfHours> HalfHourly,
    CostSection? Costs,
    IReadOnlyList<string> Warnings);
