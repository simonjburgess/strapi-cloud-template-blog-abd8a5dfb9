using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OctopusDashboard.Services;
using Xunit;

namespace OctopusDashboard.Tests;

/// <summary>A public server with no account of its own: each visitor brings their key with each request.</summary>
public class SharedServerTests
{
    private sealed class FakeOctopus : HttpMessageHandler
    {
        public List<(string Path, string? Auth)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Calls) Calls.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter));
            var body = request.RequestUri.AbsolutePath.StartsWith("/v1/accounts/")
                ? """{"number":"A-1234ABCD","properties":[{"electricity_meter_points":[],"gas_meter_points":[]}]}"""
                : """{"count":0,"next":null,"previous":null,"results":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (WebApplicationFactory<Program> App, FakeOctopus Octopus) Server()
    {
        var octopus = new FakeOctopus();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Dashboard:AccessCode", "");
            b.UseSetting("Octopus:ApiKey", "");
            b.UseSetting("Octopus:AccountNumber", "");
            b.ConfigureTestServices(s => s.AddHttpClient<OctopusClient>().ConfigurePrimaryHttpMessageHandler(() => octopus));
        });
        return (app, octopus);
    }

    private static HttpRequestMessage Dashboard(string? key = null, string? account = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard?days=2&region=C");
        if (key is not null) request.Headers.Add(OctopusCredentials.ApiKeyHeader, key);
        if (account is not null) request.Headers.Add(OctopusCredentials.AccountHeader, account);
        return request;
    }

    [Fact]
    public async Task Visitor_key_is_used_for_their_own_account_only()
    {
        var (app, octopus) = Server();
        using var _ = app;

        var res = await app.CreateClient().SendAsync(Dashboard("sk_live_visitor1", "a-1234abcd"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("hasAccount").GetBoolean());
        var accountCall = Assert.Single(octopus.Calls, c => c.Path.StartsWith("/v1/accounts/"));
        Assert.Equal("/v1/accounts/A-1234ABCD/", accountCall.Path);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes("sk_live_visitor1:")), accountCall.Auth);
        // Public price requests never carry anyone's key.
        Assert.All(octopus.Calls.Where(c => !c.Path.StartsWith("/v1/accounts/")), c => Assert.Null(c.Auth));
    }

    [Fact]
    public async Task Visitors_without_a_key_get_prices_only()
    {
        var (app, octopus) = Server();
        using var _ = app;

        var res = await app.CreateClient().SendAsync(Dashboard());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("hasAccount").GetBoolean());
        Assert.DoesNotContain(octopus.Calls, c => c.Auth is not null);
        var config = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/config");
        Assert.False(config.GetProperty("serverAccount").GetBoolean());
    }

    [Fact]
    public async Task Malformed_credentials_are_rejected_before_reaching_octopus()
    {
        var (app, octopus) = Server();
        using var _ = app;

        var res = await app.CreateClient().SendAsync(Dashboard("sk_live_visitor1", "A-1234/../../accounts/A-OTHER"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("account number", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(octopus.Calls);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_are_not_cached()
    {
        var (app, _) = Server();
        using var __ = app;
        var client = app.CreateClient();

        var page = await client.GetAsync("/");
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());

        var api = await client.GetAsync("/api/config");
        Assert.True(api.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Api_is_rate_limited_per_visitor()
    {
        var (app, _) = Server();
        using var __ = app;
        var client = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++) statuses.Add((await client.GetAsync("/api/config")).StatusCode);

        Assert.All(statuses.Take(30), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }
}
