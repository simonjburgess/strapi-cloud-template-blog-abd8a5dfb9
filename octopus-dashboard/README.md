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

To see your own costs, open **Your Octopus account** on the page and enter your API key and account number, from octopus.energy → **Account → Personal details → API access**. The app then:

- reads your region and **your actual Tracker product** from your account (older Tracker versions have different product codes)
- downloads your half-hourly consumption and prices every half-hour under each tariff, including VAT and standing charges

## Two ways to run it

### Shared: one site, many people (the default)

Leave the Octopus settings and `Dashboard:AccessCode` blank on the server. Anyone can visit and see prices. Each visitor connects their own Octopus account on the page:

- The key is saved only in **their** browser: localStorage if they tick "Remember on this device", otherwise sessionStorage, which is cleared when the tab closes.
- The browser sends it in request headers (`X-Octopus-Api-Key`, `X-Octopus-Account`) with each refresh. It never goes in a URL, so it doesn't appear in server or proxy access logs.
- The server uses it for that request only and never writes it anywhere. Account data and readings are cached in memory for up to an hour under a hash of the key, so they're only ever served back to the same person.
- Account numbers are strictly validated before use, each visitor is limited to 30 requests a minute, and the page sends a strict Content Security Policy to protect keys stored in the browser.
- **Disconnect and forget** removes the key and the offline copy of that person's data from the device.

If you share it publicly, add a short privacy notice saying what the site does with keys (as above), who runs it, and how to contact you. Read Octopus's developer terms too.

### Private: just you, with your account built in

Set your own key on the server, and an access code so nobody else can see your data:

```bash
cd octopus-dashboard/src/OctopusDashboard
dotnet user-secrets set "Octopus:ApiKey" "sk_live_..."
dotnet user-secrets set "Octopus:AccountNumber" "A-1234ABCD"
dotnet user-secrets set "Dashboard:AccessCode" "pick-a-long-passphrase"
```

On a host, use the environment variables `Octopus__ApiKey`, `Octopus__AccountNumber` and `Dashboard__AccessCode` (double underscores). You enter the code once on each device and stay signed in for 90 days, even when the host restarts the app. Five wrong attempts in a minute lock out further attempts for that minute. Changing the code signs out every device.

## Host it online

The repository root has a `render.yaml` [Render Blueprint](https://render.com/docs/blueprint-spec) that runs the shared version on Render's free plan:

1. Sign up at [render.com](https://render.com) with your GitHub account, and give Render access to this repository.
2. Click **New → Blueprint** and pick this repository. Choose the branch that contains `octopus-dashboard/`.
3. Click **Apply**. The first build takes a few minutes. Then share the `https://tariff-compare-….onrender.com` address.

For the private version instead, add the three environment variables above in the service's **Environment** tab.

Free Render services sleep when idle, so the first visit after a while can take up to a minute to load. For a site other people rely on, a paid instance avoids that.

The `Dockerfile` also runs on other hosts that build Docker images, such as [Railway](https://railway.app), [Fly.io](https://fly.io) or Azure App Service.

### Or on your home Wi-Fi

```bash
cd octopus-dashboard/src/OctopusDashboard
dotnet run --launch-profile Phone
```

Find your computer's local IP address (`ipconfig` on Windows, or `ipconfig getifaddr en0` on a Mac), then open `http://<that address>:5080` on your phone. Allow the app through your computer's firewall if it asks. This is plain `http://`, so only use it on a network you trust.

### Add it to your home screen

- **iPhone (Safari):** tap Share → **Add to Home Screen**.
- **Android (Chrome):** tap ⋮ → **Install app** (or **Add to Home screen**).

Over HTTPS it also shows the last prices it loaded when you're offline. Over plain `http://` on your Wi-Fi, the home-screen shortcut still works, but browsers turn off offline support.

## Settings (`appsettings.json`, section `Octopus`)

| Setting | Default | Notes |
|---|---|---|
| `DefaultRegion` | `C` | Region letter A–P, used when no account is configured |
| `TrackerProductCode` / `AgileProductCode` / `FlexibleProductCode` | blank | Leave blank to use the latest product on sale. Set one to pin a version, e.g. `SILVER-24-10-01` |
| `GasConsumptionInCubicMetres` | `true` | SMETS2 meters report gas in m³, SMETS1 in kWh. Set `false` for SMETS1 |
| `GasCalorificValue` | `39.5` | MJ/m³, used for the m³ → kWh conversion |
| `CacheMinutes` | `30` | How long API responses are cached in memory |

`Octopus:ApiKey` and `Octopus:AccountNumber` build your own account into the server (private mode). `Dashboard:AccessCode`, outside the `Octopus` section, turns on the sign-in page. Always set it when the server has your key.

## How it works

```
wwwroot/ (HTML + Chart.js)  →  /api/dashboard  →  DashboardService  →  OctopusClient  →  api.octopus.energy
```

- `Services/OctopusClient.cs`: typed, cached client. Handles pagination, Basic auth (API key as username) and one retry on transient errors.
- `Services/OctopusCredentials.cs`: validates a visitor's key and account number, keeps the key out of logs, and provides the hash used to key their cache entries.
- `Services/DashboardService.cs`: picks the products, loads the rates and builds the charts and cost comparison.
- `Domain/RateTimeline.cs`: answers "what was the price at time t?" for any tariff shape. Daily averages are weighted across half-hours and handle the 46- and 50-slot clock-change days.
- `Domain/CostCalculator.cs`: prices the same complete days of usage under each tariff. A tariff with missing prices is flagged and never ranked as cheapest.

API endpoints the app exposes:

- `GET /api/dashboard?region=C&days=30`, optionally with `X-Octopus-Api-Key` and `X-Octopus-Account` headers
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
