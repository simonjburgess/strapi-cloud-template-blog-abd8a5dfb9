using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using OctopusDashboard.Domain;
using OctopusDashboard.Models;

namespace OctopusDashboard.Services;

/// <summary>Thin, cached wrapper over the public Octopus Energy REST API (https://developer.octopus.energy/).</summary>
public sealed class OctopusClient(HttpClient http, IMemoryCache cache, IOptions<OctopusOptions> options)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly OctopusOptions _options = options.Value;

    private TimeSpan RateTtl => TimeSpan.FromMinutes(_options.CacheMinutes);

    public Task<List<Product>> GetProductsAsync(CancellationToken ct) =>
        Cached("products", TimeSpan.FromHours(6),
            () => GetAllPagesAsync<Product>("v1/products/?brand=OCTOPUS_ENERGY&is_business=false&page_size=100", null, ct));

    public Task<ProductDetail> GetProductAsync(string productCode, CancellationToken ct) =>
        Cached($"product:{productCode}", TimeSpan.FromHours(6),
            async () => (await http.GetFromJsonAsync<ProductDetail>($"v1/products/{productCode}/", Json, ct))!);

    public Task<List<Rate>> GetUnitRatesAsync(Fuel fuel, string productCode, string tariffCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetRatesAsync(fuel, productCode, tariffCode, "standard-unit-rates", from, to, ct);

    public Task<List<Rate>> GetStandingChargesAsync(Fuel fuel, string productCode, string tariffCode, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        GetRatesAsync(fuel, productCode, tariffCode, "standing-charges", from, to, ct);

    // Personal data is cached under a hash of the caller's own key, so it can only be served back to them.
    public Task<Account> GetAccountAsync(OctopusCredentials credentials, CancellationToken ct) =>
        Cached($"account:{credentials.CacheKey}", TimeSpan.FromHours(1), async () =>
        {
            using var request = Authorised(HttpMethod.Get, $"v1/accounts/{credentials.AccountNumber}/", credentials);
            using var response = await http.SendAsync(request, ct);
            EnsureSuccess(response);
            return (await response.Content.ReadFromJsonAsync<Account>(Json, ct))!;
        });

    public Task<List<Consumption>> GetConsumptionAsync(OctopusCredentials credentials, Fuel fuel, string meterPoint, string serial, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var point = fuel == Fuel.Electricity ? "electricity-meter-points" : "gas-meter-points";
        var url = $"v1/{point}/{Uri.EscapeDataString(meterPoint)}/meters/{Uri.EscapeDataString(serial)}/consumption/?period_from={Iso(from)}&period_to={Iso(to)}&order_by=period&page_size=25000";
        return Cached($"consumption:{credentials.CacheKey}:{url}", RateTtl, () => GetAllPagesAsync<Consumption>(url, credentials, ct));
    }

    public async Task<string?> GetRegionForPostcodeAsync(string postcode, CancellationToken ct)
    {
        var url = $"v1/industry/grid-supply-points/?postcode={Uri.EscapeDataString(postcode.Trim())}";
        var page = await http.GetFromJsonAsync<Paged<GridSupplyPoint>>(url, Json, ct);
        // group_id looks like "_C".
        return page?.Results.FirstOrDefault()?.GroupId.TrimStart('_');
    }

    private Task<List<Rate>> GetRatesAsync(Fuel fuel, string productCode, string tariffCode, string kind, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var url = $"v1/products/{productCode}/{fuel.ApiName()}-tariffs/{tariffCode}/{kind}/?period_from={Iso(from)}&period_to={Iso(to)}&page_size=1500";
        return Cached($"rates:{url}", RateTtl, () => GetAllPagesAsync<Rate>(url, null, ct));
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string url, OctopusCredentials? credentials, CancellationToken ct)
    {
        var all = new List<T>();
        string? next = url;
        while (next is not null)
        {
            using var request = credentials is null ? new HttpRequestMessage(HttpMethod.Get, next) : Authorised(HttpMethod.Get, next, credentials);
            using var response = await http.SendAsync(request, ct);
            EnsureSuccess(response);
            var page = await response.Content.ReadFromJsonAsync<Paged<T>>(Json, ct)
                       ?? throw new InvalidOperationException($"Empty response from {next}");
            all.AddRange(page.Results);
            next = page.Next;
        }
        return all;
    }

    private static HttpRequestMessage Authorised(HttpMethod method, string url, OctopusCredentials credentials)
    {
        var request = new HttpRequestMessage(method, url);
        // Octopus uses HTTP Basic auth with the API key as the username and an empty password.
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.ApiKey}:"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Octopus didn't accept the API key and account number",
            HttpStatusCode.NotFound => "Octopus couldn't find that account or meter",
            HttpStatusCode.TooManyRequests => "Octopus is limiting requests; try again in a minute",
            _ => $"Octopus returned {(int)response.StatusCode}",
        }, null, response.StatusCode);
    }

    private async Task<T> Cached<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        if (cache.TryGetValue(key, out T? hit) && hit is not null) return hit;
        var value = await factory();
        cache.Set(key, value, ttl);
        return value;
    }

    private static string Iso(DateTimeOffset t) =>
        Uri.EscapeDataString(t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
