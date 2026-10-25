using System.Text.Json.Serialization;

namespace OctopusDashboard.Models;

// Shapes returned by https://api.octopus.energy/v1/. Deserialised with a snake_case naming policy.

public sealed record Paged<T>(int Count, string? Next, string? Previous, List<T> Results);

public sealed record Product(
    string Code,
    string Direction,
    string FullName,
    string DisplayName,
    bool IsVariable,
    bool IsGreen,
    bool IsTracker,
    bool IsPrepay,
    bool IsBusiness,
    bool IsRestricted,
    DateTimeOffset? AvailableFrom,
    DateTimeOffset? AvailableTo,
    string? Brand);

public sealed record TariffInfo(string Code, decimal? StandingChargeIncVat, decimal? StandardUnitRateIncVat);

public sealed record ProductDetail(
    string Code,
    string DisplayName,
    string FullName,
    // Region key (e.g. "_C") -> payment method (e.g. "direct_debit_monthly") -> tariff.
    Dictionary<string, Dictionary<string, TariffInfo>>? SingleRegisterElectricityTariffs,
    Dictionary<string, Dictionary<string, TariffInfo>>? SingleRegisterGasTariffs);

public sealed record Rate(decimal ValueExcVat, decimal ValueIncVat, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? PaymentMethod);

// The API calls the amount "consumption"; it is kWh for electricity and SMETS1 gas, m³ for SMETS2 gas.
public sealed record Consumption(
    [property: JsonPropertyName("consumption")] decimal Amount,
    DateTimeOffset IntervalStart,
    DateTimeOffset IntervalEnd);

public sealed record Agreement(string TariffCode, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

public sealed record Meter(string SerialNumber);

public sealed record ElectricityMeterPoint(string Mpan, List<Meter> Meters, List<Agreement> Agreements, bool IsExport);

public sealed record GasMeterPoint(string Mprn, List<Meter> Meters, List<Agreement> Agreements);

public sealed record AccountProperty(List<ElectricityMeterPoint> ElectricityMeterPoints, List<GasMeterPoint> GasMeterPoints);

public sealed record Account(string Number, List<AccountProperty> Properties);

public sealed record GridSupplyPoint(string GroupId);
