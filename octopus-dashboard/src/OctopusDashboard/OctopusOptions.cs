namespace OctopusDashboard;

public sealed class OctopusOptions
{
    public const string Section = "Octopus";

    public string BaseUrl { get; set; } = "https://api.octopus.energy/";

    /// <summary>Personal API key from the Octopus dashboard. Needed only for your own usage and costs.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Account number, e.g. A-1234ABCD. Needed only for your own usage and costs.</summary>
    public string? AccountNumber { get; set; }

    /// <summary>Region letter (A–P) used when no account is configured.</summary>
    public string DefaultRegion { get; set; } = "C";

    /// <summary>Optional product code overrides, e.g. "SILVER-24-10-01". Blank means pick the latest on sale.</summary>
    public string? TrackerProductCode { get; set; }
    public string? AgileProductCode { get; set; }
    public string? FlexibleProductCode { get; set; }

    /// <summary>SMETS2 gas meters report m³, SMETS1 report kWh. Set false for SMETS1.</summary>
    public bool GasConsumptionInCubicMetres { get; set; } = true;

    /// <summary>Calorific value (MJ/m³) used to convert gas m³ to kWh.</summary>
    public decimal GasCalorificValue { get; set; } = 39.5m;

    public int CacheMinutes { get; set; } = 30;

    public bool HasAccount => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(AccountNumber);
}
