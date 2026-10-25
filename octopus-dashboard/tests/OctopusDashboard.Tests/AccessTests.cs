using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OctopusDashboard.Tests;

public class AccessTests
{
    private static WebApplicationFactory<Program> App(string? accessCode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Dashboard:AccessCode", accessCode ?? "");
            b.UseSetting("Octopus:ApiKey", "");
        });

    private static readonly WebApplicationFactoryClientOptions NoRedirects = new() { AllowAutoRedirect = false, HandleCookies = true };

    private static Task<HttpResponseMessage> Login(HttpClient client, string code) =>
        client.PostAsync("/login", new FormUrlEncodedContent([new KeyValuePair<string, string>("code", code)]));

    [Fact]
    public async Task Api_is_open_when_no_access_code_is_set()
    {
        using var app = App(null);
        var res = await app.CreateClient().GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Api_requires_sign_in_when_access_code_is_set()
    {
        using var app = App("correct horse");
        var client = app.CreateClient(NoRedirects);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/config")).StatusCode);
        // The page shell itself is public; it holds no data.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login.html")).StatusCode);

        var wrong = await Login(client, "wrong");
        Assert.Equal("/login.html?error=1", wrong.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/config")).StatusCode);

        var right = await Login(client, "correct horse");
        Assert.Equal("/", right.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/config")).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_codes_are_rate_limited()
    {
        using var app = App("correct horse");
        var client = app.CreateClient(NoRedirects);

        for (var i = 0; i < 5; i++) await Login(client, "guess" + i);
        var blocked = await Login(client, "correct horse");

        Assert.Equal("/login.html?error=rate", blocked.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/config")).StatusCode);
    }
}
