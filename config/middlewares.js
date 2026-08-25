module.exports = [
  'strapi::logger',
  'strapi::errors',
  {
    // Strapi's default Content-Security-Policy blocks the LiveAvatar kiosk page:
    // the SDK is loaded from a CDN, LiveKit signals over wss:, and the WebRTC
    // media tracks are attached to the <video> element as blob: URLs.
    // The market-assets.strapi.io entries are Strapi's own defaults - keep them
    // or the admin marketplace stops rendering.
    name: 'strapi::security',
    config: {
      contentSecurityPolicy: {
        useDefaults: true,
        directives: {
          'script-src': ["'self'", "'unsafe-inline'", 'cdn.jsdelivr.net'],
          'connect-src': ["'self'", 'https:', 'wss:'],
          'img-src': ["'self'", 'data:', 'blob:', 'market-assets.strapi.io'],
          'media-src': ["'self'", 'data:', 'blob:', 'market-assets.strapi.io'],
          'worker-src': ["'self'", 'blob:'],
          upgradeInsecureRequests: null,
        },
      },
    },
  },
  'strapi::cors',
  'strapi::poweredBy',
  'strapi::query',
  'strapi::body',
  'strapi::session',
  'strapi::favicon',
  'strapi::public',
];
