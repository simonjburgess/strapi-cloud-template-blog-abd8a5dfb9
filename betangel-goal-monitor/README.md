# Bet Angel goal monitor

A C# console app that watches in-play football **Over/Under** markets loaded in Bet Angel. When a
burst of matched volume and a shortening Over price suggest a goal may be coming, it backs the
current Over line, then:

- **goal** (Over price collapses to ≤ 1.10): takes the win
- **market re-opens after a suspension** without the line being won (VAR, card, etc.): closes out at the current lay price
- **no goal within `ExitAfterSeconds`**: closes out at the current lay price, usually for a small loss

It runs in **Paper mode by default**: nothing is sent to Betfair. Every price it polls is also
logged to CSV, so you build up data you can analyse later.

## Requirements

- Windows PC running **Bet Angel Professional** with the API enabled (Settings → API, default port 9000)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Setup

1. In Bet Angel Guardian, load the in-play matches' **Over/Under** markets (0.5 to 4.5 lines).
   The app watches every loaded market whose type starts with `OVER_UNDER` (or name contains "Over/Under").
2. Check the app can read Bet Angel's data:

   ```
   cd betangel-goal-monitor
   dotnet run -- --dump
   ```

   This writes `logs/dump_getMarkets.json` and `logs/dump_getMarketPrices.json` and prints what it
   parsed. **If prices, volume or status come back blank**, the field names differ from what the app
   expects. Open the dump files and edit the `Fields` section of `appsettings.json` to match. If the
   API rejects a request, check `PriceDataRequired` against the Bet Angel API guide's list of values.
   No recompile is needed for either.
3. Run in paper mode:

   ```
   dotnet run
   ```

   Ctrl+C stops it.

## How the signal works

Every poll (1s by default) the app records the market's total matched volume and the Over price. A
signal fires when all of these hold:

| Check | Setting |
|---|---|
| At least `WarmupSeconds` of history | `WarmupSeconds` |
| Volume matched in the last `ShortWindowSeconds` ≥ `SpikeMultiplier` × the normal rate over the baseline window | `ShortWindowSeconds`, `BaselineWindowSeconds`, `SpikeMultiplier` |
| That volume is at least `MinShortWindowVolume` | `MinShortWindowVolume` |
| Over price shortened by ≥ `MinTicksShortened` ticks in the same window | `MinTicksShortened` |
| Over back price between `MinOverPrice` and `MaxOverPrice`. This picks the "next goal" line, since lines already won trade near 1.01 | `MinOverPrice`, `MaxOverPrice` |
| Market not suspended, and not signalled within `CooldownSeconds` | `CooldownSeconds` |

## Realistic paper fills

Betfair holds in-play football bets for about 5 seconds before matching them. If the market suspends
during that time, the bet is cancelled. Paper mode simulates this (`PaperFillDelaySeconds`):

- market suspends during the delay → `CANCELLED_SUSPENDED` (the goal you predicted, but no bet)
- price shortened below your requested price → `UNMATCHED`
- otherwise → matched at your price

These cancelled and unmatched entries show you how many signals you could actually have acted on.
`EntryTicksBelowBest` asks for a slightly worse price to improve the chance of a match.

## Output (in `logs/`)

- `ticks_*.csv`: every polled price and volume per selection, for later analysis
- `signals_*.csv`: every signal with its volume, baseline and tick move
- `trades_*.csv`: entries and exits with reason and net P&L (after `CommissionRate`)

## Live mode (real money)

Set `"Mode": "Live"` in `appsettings.json`. The app asks you to type `LIVE` before it starts. Then:

- On a signal it places a **BACK** bet through `placeBets` at the requested price and `Stake`.
- It does **not** yet check how much of that bet was matched. So by default (`LiveAutoExit: false`)
  it only logs the suggested closing lay, and **you close the position in Bet Angel** (or use a Bet
  Angel green-up/stop rule).
- `LiveAutoExit: true` makes the app place the closing lay itself. Only use this once you've
  confirmed entries are being matched. If the entry wasn't matched, the closing lay leaves you with
  an open lay bet.

Safeguards: `MaxOpenPositions`, `MaxSessionLoss` (stops new entries), `CooldownSeconds`.

## Suggested approach

1. Run in paper mode across a good number of matches (a few weekends).
2. Open `trades_*.csv` and look at the split between `GOAL`, `TIMEOUT`, `UNMATCHED` and
   `CANCELLED_SUSPENDED`, and at the total net P&L.
3. Tune the signal settings, and see whether the results hold on matches you didn't tune on.
4. Only then try Live mode with the minimum stake.

## API notes

Calls use Bet Angel's local API: JSON `POST`s to `http://localhost:9000/api/{markets|betting}/v1.0/{operation}`.

- `markets/getMarkets` with `dataRequired`: lists loaded markets and selections
- `markets/getMarketPrices` with `marketsFilter: { filter: "SPECIFIED_IDS", ids: [...] }` and `dataRequired`: prices, volume and status
- `betting/placeBets` with `marketId`, `betsToPlace: [{ selectionId, type: "BACK"|"LAY", price, stake }]`

See the [Bet Angel API guide](https://www.betangel.com/api-guide/) for the full reference.
