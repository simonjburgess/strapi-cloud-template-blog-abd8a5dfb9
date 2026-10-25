using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OctopusDashboard.Domain;
using OctopusDashboard.Services;
using Xunit;

namespace OctopusDashboard.Tests;

public class OctopusClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OctopusClient Client(HttpMessageHandler handler, OctopusOptions? options = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") },
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options ?? new OctopusOptions()));

    [Fact]
    public async Task Follows_pagination_and_caches()
    {
        var handler = new FakeHandler(req => req.RequestUri!.Query.Contains("page=2")
            ? Json("""{"count":2,"next":null,"previous":null,"results":[{"value_exc_vat":20,"value_inc_vat":21,"valid_from":"2026-10-24T23:00:00Z","valid_to":null,"payment_method":null}]}""")
            : Json("""{"count":2,"next":"https://api.example/v1/x/?page=2","previous":null,"results":[{"value_exc_vat":10,"value_inc_vat":10.5,"valid_from":"2026-10-23T23:00:00Z","valid_to":"2026-10-24T23:00:00Z","payment_method":null}]}"""));
        var client = Client(handler);
        var from = DateTimeOffset.Parse("2026-10-20Z");

        var rates = await client.GetUnitRatesAsync(Fuel.Electricity, "SILVER-24-10-01", "E-1R-SILVER-24-10-01-C", from, from.AddDays(7), default);
        await client.GetUnitRatesAsync(Fuel.Electricity, "SILVER-24-10-01", "E-1R-SILVER-24-10-01-C", from, from.AddDays(7), default);

        Assert.Equal([10.5m, 21m], rates.Select(r => r.ValueIncVat));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/v1/products/SILVER-24-10-01/electricity-tariffs/E-1R-SILVER-24-10-01-C/standard-unit-rates/", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Consumption_uses_basic_auth_with_api_key()
    {
        var handler = new FakeHandler(_ => Json("""{"count":1,"next":null,"previous":null,"results":[{"consumption":0.25,"interval_start":"2026-10-20T00:00:00Z","interval_end":"2026-10-20T00:30:00Z"}]}"""));
        var client = Client(handler, new OctopusOptions { ApiKey = "sk_live_abc", AccountNumber = "A-1" });

        var usage = await client.GetConsumptionAsync(Fuel.Gas, "123", "G4P", DateTimeOffset.Parse("2026-10-20Z"), DateTimeOffset.Parse("2026-10-21Z"), default);

        Assert.Equal(0.25m, usage.Single().Amount);
        var auth = handler.Requests.Single().Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal("sk_live_abc:", Encoding.ASCII.GetString(Convert.FromBase64String(auth.Parameter!)));
        Assert.Contains("/v1/gas-meter-points/123/meters/G4P/consumption/", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Retry_handler_retries_once_on_server_error()
    {
        RetryHandler.Delay = TimeSpan.Zero;
        var calls = 0;
        var inner = new FakeHandler(_ => ++calls == 1 ? Json("{}", HttpStatusCode.BadGateway) : Json("""{"count":0,"next":null,"previous":null,"results":[{"group_id":"_M"}]}"""));
        var client = Client(new RetryHandler { InnerHandler = inner });

        Assert.Equal("M", await client.GetRegionForPostcodeAsync("LS1 1AA", default));
        Assert.Equal(2, calls);
    }
}
