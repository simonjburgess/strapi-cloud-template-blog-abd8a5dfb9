# Live avatar: Admiral George Anson

A working example that hooks this Strapi app into **HeyGen LiveAvatar** so visitors
at Shugborough can walk up to a screen, ask Admiral George Anson a question out
loud, and get a spoken, lip-synced answer in character.

Visit **`http://localhost:1337/anson/`** once configured.

---

## Which HeyGen API this uses

HeyGen has two generations of real-time avatar API, and this matters if you find
older tutorials:

| | Status |
|---|---|
| Interactive Avatar / Streaming API (`streaming.new`, `streaming.task`, `@heygen/streaming-avatar`) | **Deprecated.** The npm package now ships a deprecation notice pointing at the new SDK. |
| **LiveAvatar** (`api.liveavatar.com`, `@heygen/liveavatar-web-sdk`) | **Current.** What this example uses. |

LiveAvatar runs over WebRTC via LiveKit. HeyGen renders the lip sync server side,
so the browser just receives an ordinary audio + video track and attaches it to a
`<video>` element — there is no client-side visemes or animation work to do.

Two modes exist:

- **FULL** — HeyGen runs speech recognition, the LLM and the voice. You supply a
  *context* (a knowledge base / system prompt). This example uses FULL, because
  "visitors ask him questions and he answers" is exactly what it does out of the box.
- **LITE** — HeyGen only streams the face; you bring your own LLM/ASR/TTS. Set
  `LIVEAVATAR_MODE=LITE` if you later want to drive Anson from your own stack.

---

## Setup

### 1. Get credentials

Sign up at [app.liveavatar.com](https://app.liveavatar.com) and copy an API key
from the developers page into `.env`:

```bash
LIVEAVATAR_API_KEY=your_key_here
```

### 2. Pick an avatar

```bash
npm run liveavatar -- avatars          # stock avatars
npm run liveavatar -- avatars custom   # avatars trained on your own footage
```

Put the ID you want in `.env` as `LIVEAVATAR_AVATAR_ID`. Optionally set
`LIVEAVATAR_VOICE_ID` too, otherwise the avatar's default voice is used.

> For a real Anson you would train a **custom avatar** from footage of a costumed
> interpreter, and clone that performer's voice. A stock avatar is fine to prove
> the pipeline works.

### 3. Push his knowledge base

```bash
npm run liveavatar -- push-context
```

This uploads the persona in `src/api/avatar/personas/george-anson.js` as a
LiveAvatar *context* and prints the ID. Add it to `.env`:

```bash
LIVEAVATAR_CONTEXT_ID=ctx_...
```

If you skip this step the server creates a context automatically on the first
session and stores the ID on the Avatar Persona single type.

### 4. Check and run

```bash
npm run liveavatar -- check
npm run develop
```

Open <http://localhost:1337/anson/>.

---

## How it fits together

```
Browser (/anson/)                    Strapi                          HeyGen
─────────────────                    ──────                          ──────
GET /api/avatar/persona   ───────▶  reads Avatar Persona
                          ◀───────  greeting, prompt chips, SDK URL

POST /api/avatar/session  ───────▶  rate limit per IP
                                    POST /v1/sessions/token  ─────▶  mints token
                          ◀───────  { sessionToken }         ◀─────

new LiveAvatarSession(token).start()
   └─ SDK: POST /v1/sessions/start ──────────────────────────────▶  LiveKit creds
   └─ WebRTC join ◀══════════ lip-synced audio + video ═══════════

session.message("Where did you sail?")  ───────────────────────▶  LLM answers
                                                                  in character,
                                        ◀═════ he speaks it ═════ lip-synced
```

**The API key never leaves the server.** The browser only ever receives a
short-lived session token scoped to one session. This is the main reason the
token endpoint exists rather than calling HeyGen from the page directly.

---

## Files

| Path | What it does |
|---|---|
| `config/liveavatar.js` | All LiveAvatar settings, read from env |
| `src/api/avatar/lib/client.js` | Plain Node client for the LiveAvatar REST API |
| `src/api/avatar/services/avatar.js` | Persona resolution, context sync, token minting |
| `src/api/avatar/controllers/avatar.js` | The two public endpoints, plus rate limiting |
| `src/api/avatar/routes/avatar.js` | Routes, marked `auth: false` for walk-up use |
| `src/api/avatar/personas/george-anson.js` | Default character + knowledge base |
| `src/api/avatar-persona/…/schema.json` | Single type so curators can edit him in the admin |
| `public/anson/` | The visitor-facing kiosk page |
| `scripts/liveavatar.js` | CLI for avatars, contexts and config checks |
| `config/middlewares.js` | CSP opened up for the CDN, `wss:` and `blob:` media |

---

## Editing Anson without touching code

In the Strapi admin, open **Content Manager → Avatar Persona**. Any field left
blank falls back to the bundled defaults in `george-anson.js`. Fields:

- **greeting** — his opening line
- **personality** — how he speaks (tone, register, answer length)
- **boundaries** — what he must refuse or deflect
- **knowledge** — everything he is permitted to state as fact
- **suggestedQuestions** — the prompt chips, as a JSON array of strings
- **avatarId / voiceId / contextId** — override the env values

After editing `personality`, `boundaries` or `knowledge`, push the change to
HeyGen so the running avatar picks it up:

```bash
npm run liveavatar -- push-context   # updates the context named in .env
```

The single type is deliberately **not** exposed over the REST API — it holds the
system prompt. Only the safe subset is served, via `/api/avatar/persona`.

---

## The kiosk page

- **Push-to-talk by default.** A gallery is noisy and a hot mic would have Anson
  answering passing conversations. Hold the button, speak, release.
- **Hands-free toggle** switches to continuous conversational listening.
- **Text box and prompt chips** for visitors who would rather not speak, and for
  testing without a microphone.
- **Live captions** for both sides, built from the SDK's transcription events —
  worth keeping for accessibility.
- **Interrupt** stops him mid-sentence, which matters when answers run long.
- **Session timer** enforced by `LIVEAVATAR_MAX_SESSION_DURATION`, plus a
  `keepAlive` heartbeat while someone is actually present, and a `pagehide`
  handler so a closed tab does not leave a paid session running.

---

## Running it from a phone

The page is responsive and works on a phone, but **voice needs HTTPS**.

Microphone capture requires what browsers call a *secure context*. On a plain
`http://192.168.x.x:1337/anson/` LAN address, `navigator.mediaDevices` is not
merely blocked — it is `undefined` entirely. Only `https://` and `localhost`
count as secure.

The page detects this and degrades rather than breaking: it warns the visitor,
starts the session without requesting voice chat (so `start()` cannot fail on
the missing microphone), hides the push-to-talk and hands-free controls, and
leaves the typed question box and prompt chips working. You still see and hear
Anson answer — you just have to type.

To get voice on a phone, pick one:

| Route | How | Good for |
|---|---|---|
| **Tunnel** | `npx localtunnel --port 1337`, or `cloudflared tunnel --url http://localhost:1337`, or ngrok. Open the `https://` URL it prints. | Quickest way to test on a real phone |
| **Deploy** | Push to Strapi Cloud or any host with TLS. | Sharing with colleagues |
| **LAN + certificate** | Terminate TLS in front of Strapi with a trusted cert. | A permanent in-gallery kiosk |

Note that a tunnel exposes `/api/avatar/session` publicly, and every call to it
mints a billable session. Keep tunnels short-lived, and see the hardening notes
below before leaving one running.

Also worth knowing on mobile:

- **iOS Safari** will not play audio until the visitor taps. The Begin button
  counts as that tap, so this normally resolves itself; the page falls back to a
  "Tap the video to enable sound" prompt if it does not.
- **The screen locking or the tab going to the background** tears the WebRTC
  session down. The `pagehide` handler stops the session cleanly so it does not
  keep billing.
- **Mobile data.** A live avatar stream is real video — do not leave it running
  on a metered connection.

---

## Before this goes in front of the public

1. **Lock down `/api/avatar/session`.** It is `auth: false` so a kiosk needs no
   login, and every call mints a *billable* session. The per-IP rate limit in the
   controller is a speed bump, not a control. Put it behind an origin allowlist,
   a kiosk-specific API token, or a network boundary.
2. **Self-host the SDK.** The page currently loads it from jsDelivr. For a kiosk
   that must work when the museum's link to the outside world is flaky, install
   `@heygen/liveavatar-web-sdk` and serve `dist/index.umd.js` from `public/`,
   then narrow the CSP `script-src` back to `'self'`.
3. **Set a budget.** Streaming is billed per minute. Keep
   `LIVEAVATAR_MAX_SESSION_DURATION` tight and consider an idle auto-disconnect.
4. **Have a curator check the knowledge base.** The facts in
   `george-anson.js` were written from general historical knowledge and have
   **not** been verified against Shugborough's own records. Treat that file as a
   first draft for the collections team to correct — particularly casualty
   figures, dates, and the attribution of the park monuments.
5. **Test the failure mode.** If HeyGen is down or credits run out, the page
   shows a banner rather than a dead screen. Decide what a visitor should see.
6. **HTTPS.** `getUserMedia` only works on a secure origin (or `localhost`).

---

## Troubleshooting

**"LIVEAVATAR_API_KEY is not set"** — add it to `.env` and restart Strapi.

**The page loads but Begin does nothing** — open the console. If you see a CSP
error, check `config/middlewares.js` still has the `script-src` and `connect-src`
entries.

**Video connects but there is no sound** — browsers block autoplay with audio
until the user interacts. The page catches this and shows "Tap the video to
enable sound".

**He answers from general knowledge instead of the knowledge base** — the context
was not applied. Confirm `LIVEAVATAR_CONTEXT_ID` is set and `LIVEAVATAR_MODE=FULL`,
then re-run `npm run liveavatar -- push-context`.

**403 on `/v1/sessions/start`** — the session token has expired. Tokens are
short-lived; mint one immediately before starting a session rather than caching.
