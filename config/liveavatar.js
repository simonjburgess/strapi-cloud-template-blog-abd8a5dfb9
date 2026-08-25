/**
 * HeyGen LiveAvatar configuration.
 *
 * LiveAvatar is the successor to HeyGen's Interactive Avatar / Streaming API.
 * The API key is a server-side secret: it is read here and never sent to the
 * browser. The browser only ever receives a short-lived session token.
 */
module.exports = ({ env }) => ({
  apiUrl: env('LIVEAVATAR_API_URL', 'https://api.liveavatar.com'),
  apiKey: env('LIVEAVATAR_API_KEY'),

  // Which avatar/voice to stream. Discover IDs with `npm run liveavatar -- avatars`.
  avatarId: env('LIVEAVATAR_AVATAR_ID'),
  voiceId: env('LIVEAVATAR_VOICE_ID'),

  // The "context" is LiveAvatar's knowledge base / system prompt.
  // Create one with `npm run liveavatar -- push-context`.
  contextId: env('LIVEAVATAR_CONTEXT_ID'),

  // FULL = HeyGen runs the LLM, ASR and TTS for you (what this example uses).
  // LITE = you bring your own conversational stack and HeyGen only streams the face.
  mode: env('LIVEAVATAR_MODE', 'FULL'),
  language: env('LIVEAVATAR_LANGUAGE', 'en'),

  // Sandbox sessions don't consume streaming credits but are watermarked.
  sandbox: env.bool('LIVEAVATAR_SANDBOX', false),

  // Hard cap so an abandoned kiosk session cannot burn credits forever (seconds).
  maxSessionDuration: env.int('LIVEAVATAR_MAX_SESSION_DURATION', 600),

  // If no context ID is configured, create one from the persona on first use
  // and store it back on the Avatar Persona single type.
  autoCreateContext: env.bool('LIVEAVATAR_AUTO_CREATE_CONTEXT', true),

  // Browser SDK delivered from a CDN. Pin the version you have tested against.
  sdkVersion: env('LIVEAVATAR_SDK_VERSION', '0.0.18'),

  // Crude abuse guard for the public token endpoint. Each token is billable.
  rateLimit: {
    max: env.int('LIVEAVATAR_RATE_LIMIT_MAX', 10),
    windowMs: env.int('LIVEAVATAR_RATE_LIMIT_WINDOW_MS', 60 * 1000),
  },
});
