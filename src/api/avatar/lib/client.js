'use strict';

/**
 * Plain Node client for the HeyGen LiveAvatar REST API.
 *
 * Deliberately free of Strapi imports so it can be used both by the Strapi
 * service (src/api/avatar/services/avatar.js) and by the standalone CLI
 * (scripts/liveavatar.js).
 *
 * LiveAvatar is the successor to HeyGen's Interactive Avatar / Streaming API
 * (the old `streaming.new` / `streaming.task` endpoints and the
 * `@heygen/streaming-avatar` package are deprecated).
 */

const DEFAULT_API_URL = 'https://api.liveavatar.com';
const REQUEST_TIMEOUT_MS = 20000;

class LiveAvatarError extends Error {
  constructor(message, status) {
    super(message);
    this.name = 'LiveAvatarError';
    this.status = status ?? 502;
  }
}

/**
 * Turn a persona definition into the single prompt string that LiveAvatar
 * stores as a "context". Structure matters more than length: the model is
 * told to answer only from the knowledge block.
 */
function buildContextPrompt(persona) {
  return [
    `You are ${persona.name}. ${persona.title}.`,
    '',
    'You are installed as a talking portrait at Shugborough Hall in Staffordshire, the Anson family seat.',
    'Visitors of all ages walk up and speak to you out loud.',
    '',
    '# How you speak',
    persona.personality,
    '',
    '# Rules you must not break',
    persona.boundaries,
    '',
    '# Your opening line',
    persona.greeting,
    '',
    '# Everything you know',
    persona.knowledge,
  ]
    .filter((part) => part !== undefined && part !== null)
    .join('\n');
}

function createClient({ apiUrl = DEFAULT_API_URL, apiKey } = {}) {
  async function request(path, { method = 'GET', body } = {}) {
    if (!apiKey) {
      throw new LiveAvatarError(
        'LIVEAVATAR_API_KEY is not set. Get one from the developers page at https://app.liveavatar.com.',
        503
      );
    }

    let response;
    try {
      response = await fetch(`${apiUrl}${path}`, {
        method,
        headers: {
          'X-API-KEY': apiKey,
          accept: 'application/json',
          'content-type': 'application/json',
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      });
    } catch (error) {
      throw new LiveAvatarError(`Could not reach LiveAvatar (${method} ${path}): ${error.message}`, 504);
    }

    const raw = await response.text();
    let payload;
    try {
      payload = raw ? JSON.parse(raw) : {};
    } catch {
      payload = { message: raw };
    }

    if (!response.ok) {
      const detail = payload.message || payload.error || raw || 'no response body';
      throw new LiveAvatarError(`LiveAvatar ${method} ${path} failed with ${response.status}: ${detail}`, 502);
    }

    // Successful responses are wrapped as { code, message, data }.
    return payload.data ?? payload;
  }

  function contextBody(persona) {
    return {
      name: `${persona.name} - Shugborough`,
      opening: persona.greeting,
      prompt: buildContextPrompt(persona),
    };
  }

  return {
    request,

    /** POST /v1/contexts - create a knowledge base. */
    createContext: (persona) => request('/v1/contexts', { method: 'POST', body: contextBody(persona) }),

    /** PATCH /v1/contexts/:id - update an existing knowledge base in place. */
    updateContext: (contextId, persona) =>
      request(`/v1/contexts/${contextId}`, { method: 'PATCH', body: contextBody(persona) }),

    getContext: (contextId) => request(`/v1/contexts/${contextId}`),

    /** GET /v1/avatars/public or /v1/avatars/custom. */
    listAvatars: (kind = 'public') => request(`/v1/avatars/${kind}`),

    /**
     * POST /v1/sessions/token - mint the short-lived token the browser SDK uses.
     * This is the only value from this client that may reach an untrusted client.
     */
    async createSessionToken({
      avatarId,
      voiceId,
      contextId,
      language = 'en',
      mode = 'FULL',
      sandbox = false,
      maxSessionDuration,
    }) {
      if (!avatarId) {
        throw new LiveAvatarError('No avatar_id supplied for the LiveAvatar session.', 503);
      }

      const avatarPersona = { language };
      if (voiceId) avatarPersona.voice_id = voiceId;
      if (contextId) avatarPersona.context_id = contextId;

      const body = {
        mode,
        avatar_id: avatarId,
        avatar_persona: avatarPersona,
        is_sandbox: sandbox,
      };
      if (maxSessionDuration) {
        body.max_session_duration = maxSessionDuration;
      }

      const session = await request('/v1/sessions/token', { method: 'POST', body });
      const sessionToken = session.session_token || session.token;

      if (!sessionToken) {
        throw new LiveAvatarError('LiveAvatar did not return a session token.');
      }

      return { sessionId: session.session_id ?? null, sessionToken };
    },
  };
}

module.exports = { createClient, buildContextPrompt, LiveAvatarError, DEFAULT_API_URL };
