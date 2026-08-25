#!/usr/bin/env node
'use strict';

/**
 * LiveAvatar setup helper.
 *
 *   npm run liveavatar -- check                 verify the API key works
 *   npm run liveavatar -- avatars [public|custom]   list avatar IDs
 *   npm run liveavatar -- push-context [id]     create or update the knowledge base
 *   npm run liveavatar -- prompt                print the assembled prompt locally
 *
 * Runs standalone (no Strapi boot) so it works before the app is configured.
 */

const fs = require('fs');
const path = require('path');

const { createClient, buildContextPrompt, DEFAULT_API_URL } = require('../src/api/avatar/lib/client');
const persona = require('../src/api/avatar/personas/george-anson');

loadEnvFile(path.join(__dirname, '..', '.env'));

const [command = 'help', ...args] = process.argv.slice(2);

const client = createClient({
  apiUrl: process.env.LIVEAVATAR_API_URL || DEFAULT_API_URL,
  apiKey: process.env.LIVEAVATAR_API_KEY,
});

const commands = {
  async check() {
    if (!process.env.LIVEAVATAR_API_KEY) {
      throw new Error('LIVEAVATAR_API_KEY is not set in .env');
    }

    const avatars = toArray(await client.listAvatars('public'));
    console.log(`API key works. ${avatars.length} public avatars available.`);

    const contextId = process.env.LIVEAVATAR_CONTEXT_ID;
    if (contextId) {
      const context = await client.getContext(contextId);
      console.log(`Context ${contextId} found: ${context.name ?? '(unnamed)'}`);
    } else {
      console.log('No LIVEAVATAR_CONTEXT_ID set - run `push-context` to create one.');
    }

    console.log(`Avatar: ${process.env.LIVEAVATAR_AVATAR_ID || 'NOT SET'}`);
    console.log(`Voice:  ${process.env.LIVEAVATAR_VOICE_ID || 'not set (avatar default)'}`);
  },

  async avatars() {
    const kind = args[0] === 'custom' ? 'custom' : 'public';
    const avatars = toArray(await client.listAvatars(kind));

    if (avatars.length === 0) {
      console.log(`No ${kind} avatars found.`);
      return;
    }

    console.log(`${avatars.length} ${kind} avatars:\n`);
    for (const avatar of avatars) {
      const id = avatar.avatar_id ?? avatar.id;
      const name = avatar.name ?? avatar.avatar_name ?? '(unnamed)';
      const extra = [avatar.gender, avatar.language].filter(Boolean).join(', ');
      console.log(`  ${id}  ${name}${extra ? `  [${extra}]` : ''}`);
    }
    console.log('\nSet LIVEAVATAR_AVATAR_ID in .env to the ID you want.');
  },

  async 'push-context'() {
    const contextId = args[0] || process.env.LIVEAVATAR_CONTEXT_ID;

    if (contextId) {
      await client.updateContext(contextId, persona);
      console.log(`Updated context ${contextId} from src/api/avatar/personas/george-anson.js`);
      return;
    }

    const created = await client.createContext(persona);
    const newId = created.context_id || created.id;
    console.log(`Created context ${newId}`);
    console.log(`\nAdd this to your .env:\n  LIVEAVATAR_CONTEXT_ID=${newId}`);
  },

  async prompt() {
    console.log(buildContextPrompt(persona));
  },

  async help() {
    console.log(
      [
        'LiveAvatar setup helper',
        '',
        '  npm run liveavatar -- check                    verify the API key and current config',
        '  npm run liveavatar -- avatars [public|custom]  list avatar IDs',
        '  npm run liveavatar -- push-context [id]        create or update the knowledge base',
        '  npm run liveavatar -- prompt                   print the assembled prompt',
      ].join('\n')
    );
  },
};

(async () => {
  const handler = commands[command];

  if (!handler) {
    console.error(`Unknown command: ${command}\n`);
    await commands.help();
    process.exit(1);
  }

  try {
    await handler();
  } catch (error) {
    console.error(`\n${error.message}`);
    process.exit(1);
  }
})();

/** Responses may be a bare array or wrapped as { avatars: [...] } / { data: [...] }. */
function toArray(payload) {
  if (Array.isArray(payload)) return payload;
  for (const key of ['avatars', 'list', 'data', 'items']) {
    if (Array.isArray(payload?.[key])) return payload[key];
  }
  return [];
}

/** Minimal .env loader so this script needs no extra dependency. */
function loadEnvFile(filePath) {
  if (!fs.existsSync(filePath)) return;

  for (const line of fs.readFileSync(filePath, 'utf8').split('\n')) {
    const match = line.match(/^\s*([\w.-]+)\s*=\s*(.*)?\s*$/);
    if (!match) continue;

    const [, key, rawValue = ''] = match;
    if (process.env[key] !== undefined) continue;

    process.env[key] = rawValue.trim().replace(/^(['"])([\s\S]*)\1$/, '$2');
  }
}
