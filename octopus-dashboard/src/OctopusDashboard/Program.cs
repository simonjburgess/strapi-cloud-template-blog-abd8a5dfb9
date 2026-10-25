using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OctopusDashboard;
using OctopusDashboard.Domain;
using OctopusDashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Cloud hosts such as Render and Railway say which port to listen on via PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

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

// Optional access code. Set it whenever the app is reachable from anywhere but this computer,
// otherwise anyone who finds the address can see your usage.
var accessCode = builder.Configuration["Dashboard:AccessCode"];
var requireLogin = !string.IsNullOrWhiteSpace(accessCode);

builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Redirect("/login.html?error=rate");
        return ValueTask.CompletedTask;
    };
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Hosting platforms terminate HTTPS at their proxy; trust it so cookies are marked Secure.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

if (!requireLogin && app.Services.GetRequiredService<IOptions<OctopusOptions>>().Value.HasAccount)
    app.Logger.LogWarning("Dashboard:AccessCode is not set. Anyone who can reach this app can see your energy usage.");

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapPost("/login", async (HttpContext ctx, TimeProvider time) =>
{
    if (!requireLogin) return Results.Redirect("/");
    var form = await ctx.Request.ReadFormAsync();
    if (!SessionToken.CodeMatches(form["code"].ToString(), accessCode!))
        return Results.Redirect("/login.html?error=1");

    var now = time.GetUtcNow();
    ctx.Response.Cookies.Append(SessionToken.CookieName, SessionToken.Create(accessCode!, now), new CookieOptions
    {
        HttpOnly = true,
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Expires = now.Add(SessionToken.Lifetime),
    });
    return Results.Redirect("/");
}).RequireRateLimiting("login");

var api = app.MapGroup("/api");
if (requireLogin)
{
    api.AddEndpointFilter(async (ctx, next) =>
    {
        var http = ctx.HttpContext;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        return SessionToken.IsValid(http.Request.Cookies[SessionToken.CookieName], accessCode!, now)
            ? await next(ctx)
            : Results.Unauthorized();
    });
}

api.MapGet("/config", (IOptions<OctopusOptions> options) => new
{
    regions = Regions.Names.Select(r => new { code = r.Key, name = r.Value }),
    defaultRegion = options.Value.DefaultRegion,
    hasAccount = options.Value.HasAccount,
});

api.MapGet("/dashboard", async (string? region, int? days, DashboardService dashboard, CancellationToken ct) =>
{
    var span = Math.Clamp(days ?? 30, 2, 365);
    return Results.Ok(await dashboard.BuildAsync(region, span, ct));
});

api.MapGet("/region", async (string postcode, OctopusClient client, CancellationToken ct) =>
{
    var region = await client.GetRegionForPostcodeAsync(postcode, ct);
    return region is not null && Regions.IsValid(region)
        ? Results.Ok(new { region, name = Regions.Names[region] })
        : Results.NotFound(new { error = $"No region found for '{postcode}'." });
});

app.Run();

public partial class Program;
