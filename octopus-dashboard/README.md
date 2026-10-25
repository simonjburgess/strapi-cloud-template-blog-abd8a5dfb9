# Tariff Compare: Octopus Tracker vs Agile vs Flexible

An ASP.NET Core (.NET 8) web dashboard that uses the public [Octopus Energy API](https://developer.octopus.energy/) to:

- show today's and tomorrow's **Tracker** prices (electricity and gas) and how they moved
- chart Tracker against **Agile** (daily average plus cheapest/dearest half-hour band) and **Flexible**
- show today's and tomorrow's Agile half-hourly prices against the flat Tracker and Flexible prices
- compare standing charges
- price **your own smart-meter usage** under each tariff (needs your API key), showing what you would have paid on each

![Dashboard screenshot (mock data)](docs/screenshot.png)

## Run it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd octopus-dashboard/src/OctopusDashboard
dotnet run
```

Open http://localhost:5080. Prices work straight away with no login. Choose your region from the list, or type your postcode and press **Find**.

### Add your own usage (optional)

Get your API key and account number from octopus.energy → **Account → Personal details → API access**, then store them as user secrets so they stay out of source control:

```bash
cd octopus-dashboard/src/OctopusDashboard
dotnet user-secrets set "Octopus:ApiKey" "sk_live_..."
dotnet user-secrets set "Octopus:AccountNumber" "A-1234ABCD"
```

With these set, the app:

- reads your region and **your actual Tracker product** from your account (older Tracker versions have different product codes)
- downloads your half-hourly consumption and prices every half-hour under each tariff, including VAT and standing charges

You can also use the environment variables `Octopus__ApiKey` and `Octopus__AccountNumber`.

## Settings (`appsettings.json`, section `Octopus`)

| Setting | Default | Notes |
|---|---|---|
| `DefaultRegion` | `C` | Region letter A–P, used when no account is configured |
| `TrackerProductCode` / `AgileProductCode` / `FlexibleProductCode` | blank | Leave blank to use the latest product on sale. Set one to pin a version, e.g. `SILVER-24-10-01` |
| `GasConsumptionInCubicMetres` | `true` | SMETS2 meters report gas in m³, SMETS1 in kWh. Set `false` for SMETS1 |
| `GasCalorificValue` | `39.5` | MJ/m³, used for the m³ → kWh conversion |
| `CacheMinutes` | `30` | How long API responses are cached in memory |

## How it works

```
wwwroot/ (HTML + Chart.js)  →  /api/dashboard  →  DashboardService  →  OctopusClient  →  api.octopus.energy
```

- `Services/OctopusClient.cs`: typed, cached client. Handles pagination, Basic auth (API key as username) and one retry on transient errors.
- `Services/DashboardService.cs`: picks the products, loads the rates and builds the charts and cost comparison.
- `Domain/RateTimeline.cs`: answers "what was the price at time t?" for any tariff shape. Daily averages are weighted across half-hours and handle the 46- and 50-slot clock-change days.
- `Domain/CostCalculator.cs`: prices the same complete days of usage under each tariff. A tariff with missing prices is flagged and never ranked as cheapest.

API endpoints the app exposes:

- `GET /api/dashboard?region=C&days=30`
- `GET /api/region?postcode=SW1A1AA`
- `GET /api/config`

## Tests

```bash
cd octopus-dashboard
dotnet test
```

## Notes

- Tracker prices for tomorrow are usually published in the evening, and Agile around 4pm. Until then they show as "not published yet".
- Smart-meter readings usually arrive about a day late. Costs cover only complete days.
- Agile is electricity only, so gas compares Tracker with Flexible.
- Chart.js 4.4.1 is bundled in `wwwroot/lib` (MIT licence), so the page loads no CDN scripts.
- Not affiliated with Octopus Energy.
