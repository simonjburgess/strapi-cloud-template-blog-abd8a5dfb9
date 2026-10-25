namespace OctopusDashboard.Domain;

public static class Regions
{
    // Grid supply point groups used as the last letter of Octopus tariff codes.
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["A"] = "East England",
        ["B"] = "East Midlands",
        ["C"] = "London",
        ["D"] = "Merseyside and North Wales",
        ["E"] = "West Midlands",
        ["F"] = "North East England",
        ["G"] = "North West England",
        ["H"] = "Southern England",
        ["J"] = "South East England",
        ["K"] = "South Wales",
        ["L"] = "South West England",
        ["M"] = "Yorkshire",
        ["N"] = "South Scotland",
        ["P"] = "North Scotland",
    };

    public static bool IsValid(string? region) => region is not null && Names.ContainsKey(region.ToUpperInvariant());
}

public static class TariffCode
{
    /// <summary>"E-1R-SILVER-24-10-01-C" -> "SILVER-24-10-01".</summary>
    public static string ProductCode(string tariffCode)
    {
        var parts = tariffCode.Split('-');
        if (parts.Length < 4) throw new FormatException($"Unrecognised tariff code '{tariffCode}'.");
        return string.Join('-', parts[2..^1]);
    }

    /// <summary>"E-1R-SILVER-24-10-01-C" -> "C".</summary>
    public static string Region(string tariffCode) => tariffCode[^1..].ToUpperInvariant();

    public static string Build(Fuel fuel, string productCode, string region) =>
        $"{(fuel == Fuel.Electricity ? "E" : "G")}-1R-{productCode}-{region.ToUpperInvariant()}";
}

public enum Fuel { Electricity, Gas }

public static class FuelExtensions
{
    public static string ApiName(this Fuel fuel) => fuel == Fuel.Electricity ? "electricity" : "gas";
}
