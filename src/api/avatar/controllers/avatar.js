'use strict';

/**
 * Avatar controller.
 *
 * Two public endpoints backing the kiosk page at /anson/:
 *   GET  /api/avatar/persona  - what to render before connecting
 *   POST /api/avatar/session  - mint a LiveAvatar session token
 *
 * The session endpoint is unauthenticated so a walk-up kiosk works with no
 * login, but every token it issues is billable, so it is rate limited per IP.
 * Put it behind a real gateway, an origin allowlist or a kiosk token before
 * exposing it to the open internet.
 */

const rateLimitHits = new Map();

module.exports = ({ strapi }) => ({
  async persona(ctx) {
    const service = strapi.service('api::avatar.avatar');
    ctx.body = { data: await service.getPublicPersona() };
  },

  async session(ctx) {
    const { rateLimit } = strapi.config.get('liveavatar');
    const clientKey = ctx.request.ip || 'unknown';

    if (isRateLimited(clientKey, rateLimit)) {
      return ctx.tooManyRequests(
        `Too many avatar sessions from this address. Try again in ${Math.ceil(rateLimit.windowMs / 1000)}s.`
      );
    }

    const service = strapi.service('api::avatar.avatar');

    try {
      ctx.body = { data: await service.createSessionToken() };
    } catch (error) {
      // Surface a useful message but never the API key or raw upstream body.
      strapi.log.error(`[avatar] Could not create a session: ${error.message}`);
      ctx.status = error.status && error.status >= 400 && error.status < 600 ? error.status : 502;
      ctx.body = { error: { status: ctx.status, name: 'LiveAvatarError', message: error.message } };
    }
  },
});

/** Fixed-window per-IP limiter. Fine for one kiosk; use Redis for a fleet. */
function isRateLimited(key, { max, windowMs }) {
  const now = Date.now();
  const entry = rateLimitHits.get(key);

  if (!entry || now - entry.start > windowMs) {
    rateLimitHits.set(key, { start: now, count: 1 });
    pruneRateLimitHits(now, windowMs);
    return false;
  }

  entry.count += 1;
  return entry.count > max;
}

function pruneRateLimitHits(now, windowMs) {
  if (rateLimitHits.size < 1000) {
    return;
  }
  for (const [key, entry] of rateLimitHits) {
    if (now - entry.start > windowMs) {
      rateLimitHits.delete(key);
    }
  }
}
