using System.Security.Cryptography;
using System.Text;

namespace OctopusDashboard.Services;

/// <summary>
/// Sign-in cookie for the single owner of the dashboard. It is signed with a key derived from the access code
/// rather than server-held keys, so it survives restarts (free hosts restart whenever the app sleeps),
/// and changing the access code signs out every device.
/// </summary>
public static class SessionToken
{
    public const string CookieName = "tariff_auth";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    public static bool CodeMatches(string given, string accessCode) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(accessCode)));

    public static string Create(string accessCode, DateTimeOffset now)
    {
        var expires = now.Add(Lifetime).ToUnixTimeSeconds();
        return $"{expires}.{Sign(accessCode, expires)}";
    }

    public static bool IsValid(string? token, string accessCode, DateTimeOffset now)
    {
        var dot = token?.IndexOf('.') ?? -1;
        if (dot <= 0 || !long.TryParse(token![..dot], out var expires) || expires < now.ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(token[(dot + 1)..]),
            Encoding.ASCII.GetBytes(Sign(accessCode, expires)));
    }

    private static string Sign(string accessCode, long expires)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(accessCode), Encoding.UTF8.GetBytes($"tariff-compare-session:{expires}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
