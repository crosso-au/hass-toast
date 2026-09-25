"""Config flow for HASS Windows Toast.

Only two things are needed: which machine this entry represents, and the signing key it
shares with the agent. No address or credentials — the agent connects out to Home Assistant,
so Home Assistant never needs to reach the desktop.

The bus event type is deliberately not asked for. Both halves default to the same constant, so
the only way they could ever disagree was a person typing the same name into two places and
getting one of them wrong. It stays editable in the agent's ``config.json`` for the rare case
that the default collides with another integration's event.
"""

from __future__ import annotations

from typing import Any

import voluptuous as vol
from homeassistant.config_entries import ConfigFlow, ConfigFlowResult

from .const import CONF_DEVICE_ID, CONF_SIGNING_KEY, DOMAIN
from .signing import decode_key


def _key_error(value: str) -> str | None:
    """Return an error code if ``value`` is not a usable signing key, else None."""
    try:
        decode_key(value)
    except Exception:  # noqa: BLE001 - every decode failure is the same user error
        return "invalid_key"
    return None


class HassToastConfigFlow(ConfigFlow, domain=DOMAIN):
    """Add one Windows machine per entry."""

    VERSION = 1

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        errors: dict[str, str] = {}

        if user_input is not None:
            device_id = user_input[CONF_DEVICE_ID].strip()
            signing_key = user_input[CONF_SIGNING_KEY].strip()

            # The device id is how the agent decides an envelope is for it, so two entries
            # sharing one would both fire and the machine would toast twice.
            await self.async_set_unique_id(device_id)
            self._abort_if_unique_id_configured()

            if error := _key_error(signing_key):
                errors[CONF_SIGNING_KEY] = error
            else:
                return self.async_create_entry(
                    title=device_id,
                    data={
                        CONF_DEVICE_ID: device_id,
                        CONF_SIGNING_KEY: signing_key,
                    },
                )

        return self.async_show_form(
            step_id="user",
            data_schema=vol.Schema(
                {
                    vol.Required(CONF_DEVICE_ID): str,
                    vol.Required(CONF_SIGNING_KEY): str,
                }
            ),
            errors=errors,
        )

    async def async_step_reconfigure(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Replace the signing key on a machine that is already configured.

        Rotating the key on the agent invalidates this side until the new one arrives. Without
        this step the only way to deliver it was to delete the entry and add it back, which is a
        great deal of ceremony for changing one field — and it is why the update listener
        registered in ``async_setup_entry`` had nothing to listen for.

        The device id is not editable here. It is the entry's unique id, and a different one does
        not describe the same machine any more; that is an add, not a reconfigure.
        """
        entry = self._get_reconfigure_entry()
        errors: dict[str, str] = {}

        if user_input is not None:
            signing_key = user_input[CONF_SIGNING_KEY].strip()

            if error := _key_error(signing_key):
                errors[CONF_SIGNING_KEY] = error
            else:
                return self.async_update_reload_and_abort(
                    entry, data_updates={CONF_SIGNING_KEY: signing_key}
                )

        return self.async_show_form(
            step_id="reconfigure",
            data_schema=vol.Schema({vol.Required(CONF_SIGNING_KEY): str}),
            description_placeholders={"device_id": entry.data[CONF_DEVICE_ID]},
            errors=errors,
        )
