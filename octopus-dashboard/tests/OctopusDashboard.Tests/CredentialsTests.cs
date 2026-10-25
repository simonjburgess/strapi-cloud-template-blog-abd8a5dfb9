using OctopusDashboard.Services;
using Xunit;

namespace OctopusDashboard.Tests;

public class CredentialsTests
{
    [Theory]
    [InlineData("sk_live_abcDEF123456", "A-1234ABCD", true)]
    [InlineData("  sk_live_abcDEF123456 ", " a-1234abcd ", true)]
    [InlineData("", "A-1234ABCD", false)]
    [InlineData("sk_live_abc DEF", "A-1234ABCD", false)]
    [InlineData("sk_live_abcDEF123456", "", false)]
    [InlineData("sk_live_abcDEF123456", "A-1234/../../x", false)]
    [InlineData("sk_live_abcDEF123456", "1234ABCD", false)]
    public void Validates_key_and_account(string key, string account, bool valid)
    {
        Assert.Equal(valid, OctopusCredentials.TryCreate(key, account, out var creds, out var error));
        Assert.Equal(valid, creds is not null);
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void Normalises_account_and_keeps_key_out_of_logs()
    {
        OctopusCredentials.TryCreate("sk_live_secret123", "a-1234abcd", out var creds, out _);

        Assert.Equal("A-1234ABCD", creds!.AccountNumber);
        Assert.DoesNotContain("secret", creds.ToString());
        Assert.DoesNotContain("secret", creds.CacheKey);
    }
}
