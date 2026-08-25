/**
 * Shugborough live avatar kiosk.
 *
 * Flow:
 *   1. GET  /api/avatar/persona  - greeting, prompt chips, which SDK build to load
 *   2. POST /api/avatar/session  - Strapi mints a short-lived LiveAvatar session token
 *   3. new LiveAvatarSession(token).start() - the SDK calls /v1/sessions/start and
 *      joins the LiveKit room, then hands us lip-synced audio + video tracks
 *   4. session.message(text) sends a visitor question to HeyGen's LLM, which
 *      answers in character and speaks it through the avatar
 *
 * The API key never reaches this file - only the session token does.
 */

const el = (role) => document.querySelector(`[data-role="${role}"]`);

const ui = {
  video: el('video'),
  idle: el('idle'),
  portrait: el('portrait'),
  greeting: el('greeting'),
  personaTitle: el('persona-title'),
  status: el('status'),
  speaking: el('speaking'),
  quality: el('quality'),
  timer: el('timer'),
  banner: el('banner'),
  start: el('start'),
  mic: el('mic'),
  micLabel: el('mic-label'),
  interrupt: el('interrupt'),
  stop: el('stop'),
  modeWrap: el('mode-wrap'),
  mode: el('mode'),
  composer: el('composer'),
  input: el('input'),
  suggestions: el('suggestions'),
  transcript: el('transcript'),
};

/**
 * Microphone capture needs a secure context. A phone opening this page over
 * plain http:// on a LAN address gets no navigator.mediaDevices at all, so
 * voice is impossible there - but typed questions still work. Detect it once
 * and degrade rather than letting session.start() fail outright.
 */
const micAvailable = Boolean(window.isSecureContext && navigator.mediaDevices?.getUserMedia);

const state = {
  persona: null,
  sdk: null,
  session: null,
  keepAliveTimer: null,
  countdownTimer: null,
  endsAt: null,
  handsFree: false,
  talking: false,
  liveBubbles: { visitor: null, anson: null },
};

boot();

async function boot() {
  ui.start.disabled = true;

  try {
    state.persona = await fetchJson('/api/avatar/persona');
  } catch (error) {
    return showBanner(`Could not reach the Strapi server: ${error.message}`, 'error');
  }

  renderPersona(state.persona);

  if (!state.persona.configured) {
    showBanner(
      'LiveAvatar is not configured yet. Set LIVEAVATAR_API_KEY and LIVEAVATAR_AVATAR_ID in .env, then restart Strapi.',
      'warn'
    );
    return;
  }

  try {
    state.sdk = await loadSdk(state.persona.sdk.url);
  } catch (error) {
    return showBanner(`Could not load the LiveAvatar SDK: ${error.message}`, 'error');
  }

  if (!micAvailable) {
    showBanner(
      'Voice needs a secure connection. Over plain http:// the browser blocks the microphone, ' +
        'so you can type questions but not speak them. Use https:// or localhost for voice.',
      'warn'
    );
  }

  ui.start.disabled = false;
  wireControls();
}

function renderPersona(persona) {
  if (persona.title) ui.personaTitle.textContent = persona.title;
  if (persona.greeting) ui.greeting.textContent = persona.greeting;

  if (persona.portraitUrl) {
    ui.portrait.src = persona.portraitUrl;
    ui.portrait.alt = `Portrait of ${persona.name}`;
    ui.portrait.hidden = false;
  }

  if (persona.suggestedQuestions?.length) {
    ui.suggestions.innerHTML = '';
    for (const question of persona.suggestedQuestions) {
      const chip = document.createElement('button');
      chip.type = 'button';
      chip.className = 'suggestions__chip';
      chip.textContent = question;
      chip.addEventListener('click', () => ask(question));
      ui.suggestions.append(chip);
    }
  }
}

function wireControls() {
  ui.start.addEventListener('click', startSession);
  ui.stop.addEventListener('click', () => endSession('Ended by the visitor.'));
  ui.interrupt.addEventListener('click', () => state.session?.interrupt());

  ui.composer.addEventListener('submit', (event) => {
    event.preventDefault();
    const text = ui.input.value.trim();
    if (text) {
      ui.input.value = '';
      ask(text);
    }
  });

  ui.mode.addEventListener('change', () => setHandsFree(ui.mode.checked));

  // Push to talk. Pointer events cover mouse and touch; keyboard uses space.
  ui.mic.addEventListener('pointerdown', onTalkStart);
  ui.mic.addEventListener('pointerup', onTalkEnd);
  ui.mic.addEventListener('pointercancel', onTalkEnd);
  ui.mic.addEventListener('pointerleave', onTalkEnd);
  ui.mic.addEventListener('keydown', (event) => {
    if ((event.key === ' ' || event.key === 'Enter') && !event.repeat) {
      event.preventDefault();
      onTalkStart();
    }
  });
  ui.mic.addEventListener('keyup', (event) => {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      onTalkEnd();
    }
  });

  // A kiosk left mid-session would keep burning credits.
  window.addEventListener('pagehide', () => state.session?.stop());
}

async function startSession() {
  const { LiveAvatarSession, SessionEvent, SessionState, AgentEventsEnum, SessionInteractivityMode } = state.sdk;

  ui.start.disabled = true;
  setStatus('connecting', 'Waking the Admiral...');
  hideBanner();

  let credentials;
  try {
    credentials = await fetchJson('/api/avatar/session', { method: 'POST' });
  } catch (error) {
    ui.start.disabled = false;
    setStatus('idle', 'Not connected');
    return showBanner(error.message, 'error');
  }

  // Mic starts muted and in push-to-talk: a gallery is noisy, and a hot mic
  // would have him answering passing conversations. Omitted entirely when the
  // browser cannot capture audio, so the session still starts for typed questions.
  const sessionConfig = micAvailable
    ? { voiceChat: { defaultMuted: true, mode: SessionInteractivityMode.PUSH_TO_TALK } }
    : {};

  const session = new LiveAvatarSession(credentials.sessionToken, sessionConfig);

  state.session = session;

  session.on(SessionEvent.SESSION_STATE_CHANGED, (sessionState) => {
    if (sessionState === SessionState.CONNECTED) setStatus('live', 'Live');
    if (sessionState === SessionState.CONNECTING) setStatus('connecting', 'Connecting...');
    if (sessionState === SessionState.DISCONNECTED) setStatus('idle', 'Not connected');
  });

  session.on(SessionEvent.SESSION_STREAM_READY, () => {
    // Attaches the remote audio and video tracks to the <video> element.
    session.attach(ui.video);
    ui.video.play().catch(() => showBanner('Tap the video to enable sound.', 'warn'));
    ui.idle.hidden = true;
    setStatus('live', 'Live');
  });

  session.on(SessionEvent.SESSION_CONNECTION_QUALITY_CHANGED, (quality) => {
    ui.quality.textContent = quality ? `Connection: ${String(quality).toLowerCase()}` : '';
  });

  session.on(SessionEvent.SESSION_DISCONNECTED, (reason) => {
    endSession(`Session ended (${String(reason).toLowerCase().replace(/_/g, ' ')}).`);
  });

  session.on(AgentEventsEnum.USER_TRANSCRIPTION_CHUNK, (event) => appendChunk('visitor', event.text));
  session.on(AgentEventsEnum.USER_TRANSCRIPTION, (event) => finaliseBubble('visitor', event.text));
  session.on(AgentEventsEnum.AVATAR_TRANSCRIPTION_CHUNK, (event) => appendChunk('anson', event.text));
  session.on(AgentEventsEnum.AVATAR_TRANSCRIPTION, (event) => finaliseBubble('anson', event.text));

  session.on(AgentEventsEnum.AVATAR_SPEAK_STARTED, () => {
    ui.speaking.hidden = false;
  });
  session.on(AgentEventsEnum.AVATAR_SPEAK_ENDED, () => {
    ui.speaking.hidden = true;
  });

  try {
    await session.start();
  } catch (error) {
    state.session = null;
    ui.start.disabled = false;
    setStatus('idle', 'Not connected');
    return showBanner(`Could not start the session: ${error.message}`, 'error');
  }

  showLiveControls();
  startKeepAlive();
  startCountdown(session.maxSessionDuration ?? credentials.maxSessionDuration);
}

function showLiveControls() {
  ui.start.hidden = true;

  const controls = micAvailable
    ? [ui.mic, ui.interrupt, ui.stop, ui.composer, ui.modeWrap]
    : [ui.interrupt, ui.stop, ui.composer];

  for (const control of controls) {
    control.hidden = false;
  }
  ui.suggestions.hidden = false;
}

/** Send a typed or suggested question. `message` routes it through the LLM. */
function ask(text) {
  if (!state.session) {
    return showBanner('Press Begin first.', 'warn');
  }
  finaliseBubble('visitor', text);
  state.session.message(text);
}

async function onTalkStart() {
  const session = state.session;
  if (!session || state.handsFree || state.talking) return;

  state.talking = true;
  ui.mic.classList.add('is-live');
  ui.micLabel.textContent = 'Listening...';

  try {
    await session.voiceChat.startPushToTalk();
  } catch (error) {
    state.talking = false;
    ui.mic.classList.remove('is-live');
    ui.micLabel.textContent = 'Hold to speak';
    showBanner(`Microphone unavailable: ${error.message}`, 'warn');
  }
}

async function onTalkEnd() {
  const session = state.session;
  if (!session || state.handsFree || !state.talking) return;

  state.talking = false;
  ui.mic.classList.remove('is-live');
  ui.micLabel.textContent = 'Hold to speak';

  try {
    await session.voiceChat.stopPushToTalk();
  } catch {
    // The session may already have gone away; nothing useful to do here.
  }
}

async function setHandsFree(enabled) {
  const session = state.session;
  if (!session) return;

  const { SessionInteractivityMode } = state.sdk;
  state.handsFree = enabled;

  try {
    if (enabled) {
      session.voiceChat.setMode(SessionInteractivityMode.CONVERSATIONAL);
      await session.voiceChat.unmute();
      ui.mic.disabled = true;
      ui.micLabel.textContent = 'Listening continuously';
    } else {
      await session.voiceChat.mute();
      session.voiceChat.setMode(SessionInteractivityMode.PUSH_TO_TALK);
      ui.mic.disabled = false;
      ui.micLabel.textContent = 'Hold to speak';
    }
  } catch (error) {
    ui.mode.checked = !enabled;
    state.handsFree = !enabled;
    showBanner(`Could not switch microphone mode: ${error.message}`, 'warn');
  }
}

async function endSession(reason) {
  const session = state.session;
  state.session = null;

  stopKeepAlive();
  stopCountdown();

  if (session) {
    try {
      await session.stop();
    } catch {
      // Already closed.
    }
  }

  ui.video.srcObject = null;
  ui.idle.hidden = false;
  ui.speaking.hidden = true;
  ui.quality.textContent = '';
  ui.start.hidden = false;
  ui.start.disabled = false;
  ui.start.textContent = 'Begin again';

  for (const control of [ui.mic, ui.interrupt, ui.stop, ui.composer, ui.modeWrap]) {
    control.hidden = true;
  }

  setStatus('idle', 'Not connected');
  if (reason) showBanner(reason, 'info');
}

/** LiveAvatar closes idle sessions; a heartbeat keeps a browsing visitor connected. */
function startKeepAlive() {
  stopKeepAlive();
  state.keepAliveTimer = setInterval(() => {
    state.session?.keepAlive().catch(() => {});
  }, 30000);
}

function stopKeepAlive() {
  clearInterval(state.keepAliveTimer);
  state.keepAliveTimer = null;
}

function startCountdown(durationSeconds) {
  stopCountdown();
  if (!durationSeconds) return;

  state.endsAt = Date.now() + durationSeconds * 1000;

  const tick = () => {
    const remaining = Math.max(0, Math.round((state.endsAt - Date.now()) / 1000));
    const minutes = String(Math.floor(remaining / 60)).padStart(2, '0');
    const seconds = String(remaining % 60).padStart(2, '0');
    ui.timer.textContent = `${minutes}:${seconds} remaining`;
    if (remaining === 0) endSession('Session time limit reached.');
  };

  tick();
  state.countdownTimer = setInterval(tick, 1000);
}

function stopCountdown() {
  clearInterval(state.countdownTimer);
  state.countdownTimer = null;
  ui.timer.textContent = '';
}

/* ---------- transcript ---------- */

function appendChunk(role, text) {
  if (!text) return;

  if (!state.liveBubbles[role]) {
    state.liveBubbles[role] = addBubble(role, '');
  }
  state.liveBubbles[role].textContent += text;
  scrollTranscript();
}

function finaliseBubble(role, text) {
  const live = state.liveBubbles[role];

  if (live) {
    live.textContent = text;
    state.liveBubbles[role] = null;
  } else {
    addBubble(role, text);
  }
  scrollTranscript();
}

function addBubble(role, text) {
  const bubble = document.createElement('p');
  bubble.className = `bubble bubble--${role}`;
  bubble.textContent = text;

  const line = document.createElement('div');
  line.className = `line line--${role}`;
  line.append(bubble);

  ui.transcript.append(line);
  return bubble;
}

function scrollTranscript() {
  ui.transcript.scrollTop = ui.transcript.scrollHeight;
}

/* ---------- helpers ---------- */

function setStatus(stateName, label) {
  ui.status.dataset.state = stateName;
  ui.status.textContent = label;
}

function showBanner(message, tone = 'info') {
  ui.banner.textContent = message;
  ui.banner.dataset.tone = tone;
  ui.banner.hidden = false;
}

function hideBanner() {
  ui.banner.hidden = true;
}

async function fetchJson(url, options) {
  const response = await fetch(url, {
    headers: { accept: 'application/json' },
    ...options,
  });

  const payload = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(payload?.error?.message || `Request failed with ${response.status}`);
  }
  return payload.data ?? payload;
}

/** Load the UMD build, which exposes window.LiveAvatarSDK. */
function loadSdk(url) {
  if (window.LiveAvatarSDK) {
    return Promise.resolve(window.LiveAvatarSDK);
  }

  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = url;
    script.crossOrigin = 'anonymous';
    script.onload = () =>
      window.LiveAvatarSDK
        ? resolve(window.LiveAvatarSDK)
        : reject(new Error('SDK loaded but window.LiveAvatarSDK is missing'));
    script.onerror = () => reject(new Error(`could not load ${url}`));
    document.head.append(script);
  });
}
