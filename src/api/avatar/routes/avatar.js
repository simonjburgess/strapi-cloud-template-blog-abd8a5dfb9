'use strict';

/**
 * Avatar routes.
 *
 * `auth: false` makes these reachable without a Strapi API token, so the kiosk
 * page works with no login and no permission setup in the admin. The session
 * route is rate limited in the controller because each token costs credits.
 */

module.exports = {
  routes: [
    {
      method: 'GET',
      path: '/avatar/persona',
      handler: 'avatar.persona',
      config: { auth: false },
    },
    {
      method: 'POST',
      path: '/avatar/session',
      handler: 'avatar.session',
      config: { auth: false },
    },
  ],
};
