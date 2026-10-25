using Microsoft.Extensions.Options;
using OctopusDashboard;
using OctopusDashboard.Domain;
using OctopusDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OctopusOptions>(builder.Configuration.GetSection(OctopusOptions.Section));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<OctopusClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<OctopusOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("OctopusTariffDashboard/1.0");
}).AddHttpMessageHandler<RetryHandler>();
builder.Services.AddTransient<RetryHandler>();
builder.Services.AddScoped<DashboardService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (IOptions<OctopusOptions> options) => new
{
    regions = Regions.Names.Select(r => new { code = r.Key, name = r.Value }),
    defaultRegion = options.Value.DefaultRegion,
    hasAccount = options.Value.HasAccount,
});

app.MapGet("/api/dashboard", async (string? region, int? days, DashboardService dashboard, CancellationToken ct) =>
{
    var span = Math.Clamp(days ?? 30, 2, 365);
    return Results.Ok(await dashboard.BuildAsync(region, span, ct));
});

app.MapGet("/api/region", async (string postcode, OctopusClient client, CancellationToken ct) =>
{
    var region = await client.GetRegionForPostcodeAsync(postcode, ct);
    return region is not null && Regions.IsValid(region)
        ? Results.Ok(new { region, name = Regions.Names[region] })
        : Results.NotFound(new { error = $"No region found for '{postcode}'." });
});

app.Run();

public partial class Program;
