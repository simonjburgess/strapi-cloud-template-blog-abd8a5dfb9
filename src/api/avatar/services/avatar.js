'use strict';

/**
 * Avatar service - resolves the persona, keeps the LiveAvatar context in sync,
 * and mints session tokens for the browser.
 *
 * The LiveAvatar API key lives here and on the server only. The browser gets a
 * short-lived session token and nothing else.
 */

const { createClient, LiveAvatarError } = require('../lib/client');
const defaultPersona = require('../personas/george-anson');

const PERSONA_UID = 'api::avatar-persona.avatar-persona';

/** In-flight context creation, so concurrent first requests create only one context. */
let pendingContextCreation = null;

module.exports = ({ strapi }) => {
  const config = () => strapi.config.get('liveavatar');
  const client = () => {
    const { apiUrl, apiKey } = config();
    return createClient({ apiUrl, apiKey });
  };

  return {
    /**
     * Read the persona from the Avatar Persona single type, falling back to the
     * bundled George Anson definition for any field a curator has left blank.
     */
    async getPersona() {
      let stored = null;

      try {
        stored = await strapi.documents(PERSONA_UID).findFirst({ populate: ['portrait'] });
      } catch (error) {
        strapi.log.warn(`[avatar] Could not read ${PERSONA_UID}, using bundled persona: ${error.message}`);
      }

      const persona = { ...defaultPersona };
      for (const [key, value] of Object.entries(stored ?? {})) {
        if (value !== null && value !== undefined && value !== '') {
          persona[key] = value;
        }
      }

      persona.documentId = stored?.documentId ?? null;
      persona.suggestedQuestions = normaliseQuestions(persona.suggestedQuestions);
      persona.portraitUrl = stored?.portrait?.url ?? null;

      return persona;
    },

    /**
     * Resolve the context ID to use, creating one on first run when allowed.
     * The new ID is written back to the single type so it survives a restart.
     */
    async resolveContextId(persona) {
      const { mode, contextId: configuredContextId, autoCreateContext } = config();

      // LITE mode means you bring your own LLM, so there is no HeyGen context.
      if (mode !== 'FULL') {
        return null;
      }

      const existing = persona.contextId || configuredContextId;
      if (existing) {
        return existing;
      }

      if (!autoCreateContext) {
        throw new LiveAvatarError(
          'No LiveAvatar context configured. Run `npm run liveavatar -- push-context` and set LIVEAVATAR_CONTEXT_ID.',
          503
        );
      }

      if (!pendingContextCreation) {
        pendingContextCreation = this.createAndStoreContext(persona).catch((error) => {
          pendingContextCreation = null;
          throw error;
        });
      }

      return pendingContextCreation;
    },

    async createAndStoreContext(persona) {
      strapi.log.info('[avatar] No LiveAvatar context configured - creating one from the persona.');

      const created = await client().createContext(persona);
      const contextId = created.context_id || created.id;

      if (persona.documentId) {
        try {
          await strapi.documents(PERSONA_UID).update({
            documentId: persona.documentId,
            data: { contextId },
          });
        } catch (error) {
          strapi.log.warn(`[avatar] Created context ${contextId} but could not store it: ${error.message}`);
        }
      }

      strapi.log.info(`[avatar] Created LiveAvatar context ${contextId} - set LIVEAVATAR_CONTEXT_ID to pin it.`);
      return contextId;
    },

    /** Mint a session token for the browser SDK. */
    async createSessionToken() {
      const { avatarId, voiceId, language, mode, sandbox, maxSessionDuration } = config();
      const persona = await this.getPersona();

      const resolvedAvatarId = persona.avatarId || avatarId;
      if (!resolvedAvatarId) {
        throw new LiveAvatarError(
          'No avatar configured. Set LIVEAVATAR_AVATAR_ID - list the options with `npm run liveavatar -- avatars`.',
          503
        );
      }

      const session = await client().createSessionToken({
        avatarId: resolvedAvatarId,
        voiceId: persona.voiceId || voiceId,
        contextId: await this.resolveContextId(persona),
        language: persona.language || language,
        mode,
        sandbox,
        maxSessionDuration,
      });

      return { ...session, mode, maxSessionDuration };
    },

    /** Persona fields that are safe to render before a session is opened. */
    async getPublicPersona() {
      const { apiKey, avatarId, sdkVersion } = config();
      const persona = await this.getPersona();

      return {
        name: persona.name,
        title: persona.title,
        greeting: persona.greeting,
        suggestedQuestions: persona.suggestedQuestions,
        portraitUrl: persona.portraitUrl,
        sdk: {
          version: sdkVersion,
          url: `https://cdn.jsdelivr.net/npm/@heygen/liveavatar-web-sdk@${sdkVersion}/dist/index.umd.js`,
        },
        configured: Boolean(apiKey && (persona.avatarId || avatarId)),
      };
    },

    listAvatars(kind = 'public') {
      return client().listAvatars(kind);
    },
  };
};

/** Suggested questions may be stored as a JSON array or as newline-separated text. */
function normaliseQuestions(value) {
  if (Array.isArray(value)) {
    return value.map(String).map((question) => question.trim()).filter(Boolean);
  }
  if (typeof value === 'string') {
    return value
      .split('\n')
      .map((question) => question.trim())
      .filter(Boolean);
  }
  return [];
}
