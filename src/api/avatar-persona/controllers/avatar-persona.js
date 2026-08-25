'use strict';

/**
 * avatar-persona controller.
 *
 * No routes file is provided on purpose: the persona holds the avatar's system
 * prompt, so it is editable in the admin but not exposed over the REST API.
 * The public-safe subset is served by /api/avatar/persona instead.
 */

const { createCoreController } = require('@strapi/strapi').factories;

module.exports = createCoreController('api::avatar-persona.avatar-persona');
