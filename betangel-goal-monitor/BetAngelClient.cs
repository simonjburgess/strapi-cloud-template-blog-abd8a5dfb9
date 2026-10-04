using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace GoalMonitor;

public sealed record MarketInfo(string Id, string Name, string MarketType, IReadOnlyList<SelectionRef> Selections);

public sealed record SelectionRef(string Id, string Name);

public sealed record SelectionSnapshot(
    string Id,
    string Name,
    double? LastTradedPrice,
    double? BestBack,
    double? BestLay,
    double? Volume);

public sealed record MarketSnapshot(
    string MarketId,
    DateTime TimeUtc,
    string? Status,
    double? Volume,
    IReadOnlyList<SelectionSnapshot> Selections)
{
    public double? TotalVolume =>
        Volume ?? (Selections.Any(s => s.Volume.HasValue) ? Selections.Sum(s => s.Volume ?? 0) : null);
}

/// <summary>
/// Thin client for the Bet Angel Professional local API (Settings → API must be enabled).
/// All calls are JSON POSTs to http://localhost:9000/api/{component}/v1.0/{operation}.
/// </summary>
public sealed class BetAngelClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly BetAngelSettings _settings;
    private readonly FieldNames _f;

    public BetAngelClient(BetAngelSettings settings, FieldNames fields)
    {
        _settings = settings;
        _f = fields;
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task<JsonNode> PostAsync(string component, string operation, JsonObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"api/{component}/v1.0/{operation}", content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var node = JsonNode.Parse(text) ?? throw new BetAngelException($"{operation}: empty response");
        var status = node["status"]?.ToString();
        if (status is not null && !status.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new BetAngelException($"{operation} returned status {status}: {node["errors"]?.ToJsonString()}");
        }
        return node;
    }

    public Task<JsonNode> GetMarketsRawAsync(CancellationToken ct) =>
        PostAsync("markets", "getMarkets", new JsonObject
        {
            ["dataRequired"] = ToArray(_settings.MarketDataRequired),
        }, ct);

    public Task<JsonNode> GetMarketPricesRawAsync(IEnumerable<string> marketIds, CancellationToken ct) =>
        PostAsync("markets", "getMarketPrices", new JsonObject
        {
            ["marketsFilter"] = new JsonObject
            {
                ["filter"] = "SPECIFIED_IDS",
                ["ids"] = ToArray(marketIds),
            },
            ["dataRequired"] = ToArray(_settings.PriceDataRequired),
        }, ct);

    public async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken ct)
    {
        var node = await GetMarketsRawAsync(ct);
        var result = new List<MarketInfo>();
        foreach (var m in Markets(node))
        {
            var id = Json.Str(m, _f.MarketId);
            if (id is null) continue;

            var selections = Json.Arr(m, _f.Selections)
                .Select(s => new SelectionRef(Json.Str(s, _f.SelectionId) ?? "", Json.Str(s, _f.SelectionName) ?? ""))
                .Where(s => s.Id.Length > 0)
                .ToList();

            result.Add(new MarketInfo(id, Json.Str(m, _f.MarketName) ?? "", Json.Str(m, _f.MarketType) ?? "", selections));
        }
        return result;
    }

    public async Task<IReadOnlyList<MarketSnapshot>> GetMarketPricesAsync(IEnumerable<string> marketIds, CancellationToken ct)
    {
        var node = await GetMarketPricesRawAsync(marketIds, ct);
        var now = DateTime.UtcNow;
        var result = new List<MarketSnapshot>();
        foreach (var m in Markets(node))
        {
            var id = Json.Str(m, _f.MarketId);
            if (id is null) continue;

            var selections = Json.Arr(m, _f.Selections).Select(s => new SelectionSnapshot(
                Json.Str(s, _f.SelectionId) ?? "",
                Json.Str(s, _f.SelectionName) ?? "",
                Json.Num(s, _f.LastTradedPrice),
                BestPrice(s, _f.BackPrices),
                BestPrice(s, _f.LayPrices),
                Json.Num(s, _f.SelectionVolume))).ToList();

            result.Add(new MarketSnapshot(id, now, Json.Str(m, _f.MarketStatus), Json.Num(m, _f.MarketVolume), selections));
        }
        return result;
    }

    public Task<JsonNode> PlaceBetAsync(string marketId, string selectionId, string side, double price, double stake, CancellationToken ct) =>
        PostAsync("betting", "placeBets", new JsonObject
        {
            ["marketId"] = marketId,
            ["globalSettings"] = new JsonObject { ["action"] = "NONE" },
            ["async"] = false,
            ["betsToPlace"] = new JsonArray(new JsonObject
            {
                ["selectionId"] = selectionId,
                ["type"] = side,
                ["price"] = Math.Round(price, 2),
                ["stake"] = Math.Round(stake, 2),
            }),
        }, ct);

    private IEnumerable<JsonNode> Markets(JsonNode response)
    {
        var result = response["result"] ?? response;
        return Json.Arr(result, ["markets"]);
    }

    /// <summary>Best price from either an array of {price,size} objects, an array of numbers, or a plain number.</summary>
    private double? BestPrice(JsonNode selection, string[] names)
    {
        var node = Json.Get(selection, names);
        switch (node)
        {
            case null:
                return null;
            case JsonArray arr:
                var first = arr.FirstOrDefault();
                if (first is null) return null;
                return first is JsonObject level ? Json.Num(level, _f.Price) : Json.AsDouble(first);
            case JsonObject obj:
                return Json.Num(obj, _f.Price);
            default:
                return Json.AsDouble(node);
        }
    }

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    public void Dispose() => _http.Dispose();
}

public sealed class BetAngelException(string message) : Exception(message);

/// <summary>Case-insensitive, multi-name lookups so the parser tolerates small schema differences.</summary>
internal static class Json
{
    public static JsonNode? Get(JsonNode? node, IEnumerable<string> names)
    {
        if (node is not JsonObject obj) return null;
        foreach (var name in names)
        {
            foreach (var (key, value) in obj)
            {
                if (value is not null && key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
            }
        }
        return null;
    }

    public static string? Str(JsonNode? node, IEnumerable<string> names)
    {
        var v = Get(node, names);
        return v is null or JsonObject or JsonArray ? null : v.ToString();
    }

    public static double? Num(JsonNode? node, IEnumerable<string> names) => AsDouble(Get(node, names));

    public static IEnumerable<JsonNode> Arr(JsonNode? node, IEnumerable<string> names) =>
        Get(node, names) is JsonArray arr ? arr.OfType<JsonNode>() : [];

    public static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }
}
