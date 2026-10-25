using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OctopusDashboard.Services;

/// <summary>
/// One person's Octopus API key and account number. On a shared server these arrive with each request from the
/// person's own browser and are never stored; they live only as long as the request.
/// </summary>
public sealed partial record OctopusCredentials
{
    public const string ApiKeyHeader = "X-Octopus-Api-Key";
    public const string AccountHeader = "X-Octopus-Account";

    public string ApiKey { get; }
    public string AccountNumber { get; }

    /// <summary>Hash of key and account, so cached data is only ever served back to the same person.</summary>
    public string CacheKey { get; }

    private OctopusCredentials(string apiKey, string accountNumber)
    {
        ApiKey = apiKey;
        AccountNumber = accountNumber;
        CacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{apiKey}\n{accountNumber}")));
    }

    public static bool TryCreate(string? apiKey, string? accountNumber, out OctopusCredentials? credentials, out string? error)
    {
        credentials = null;
        apiKey = apiKey?.Trim();
        accountNumber = accountNumber?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(apiKey) || !ApiKeyPattern().IsMatch(apiKey))
            error = "That doesn't look like an Octopus API key. It starts with sk_live_.";
        // The account number becomes part of the request path, so only accept the exact expected shape.
        else if (string.IsNullOrEmpty(accountNumber) || !AccountPattern().IsMatch(accountNumber))
            error = "That doesn't look like an Octopus account number. It looks like A-1234ABCD.";
        else
        {
            error = null;
            credentials = new OctopusCredentials(apiKey, accountNumber);
        }
        return credentials is not null;
    }

    // Never let the key end up in logs or exception messages.
    public override string ToString() => $"OctopusCredentials {{ AccountNumber = {AccountNumber} }}";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,128}$")]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex("^A-[A-Z0-9]{4,16}$")]
    private static partial Regex AccountPattern();
}
