"""Constants shared across the HASS Windows Toast integration."""

from __future__ import annotations

from typing import Final

DOMAIN: Final = "hass_toast"

# Configuration keys stored on the config entry.
CONF_DEVICE_ID: Final = "device_id"
CONF_SIGNING_KEY: Final = "signing_key"
# No longer written by the config flow: both halves default to DEFAULT_EVENT_TYPE, and the only
# way they could disagree was someone typing the same name into two places. Still read, so entries
# created before that change keep whatever they were configured with.
CONF_EVENT_TYPE: Final = "event_type"

DEFAULT_EVENT_TYPE: Final = "hass_toast_notify"

# Fired by the agent when a user interacts with a toast. Nothing here imports it — the agent
# owns the name and automations match on it in YAML — but it is the integration's half of that
# contract, so it is written down once rather than only in the docs.
EVENT_RESPONSE: Final = "hass_toast_response"

# Operations the agent understands.
OP_SEND: Final = "send"
OP_UPDATE: Final = "update"
OP_REMOVE: Final = "remove"
OP_CLEAR: Final = "clear"

# Integration-specific actions. Sending is done through notify.<name> instead, which is the
# idiomatic shape and gives target-based routing for free; these three are lifecycle
# operations that are not notifications and do not fit notify semantics.
SERVICE_UPDATE: Final = "update"
SERVICE_REMOVE: Final = "remove"
SERVICE_CLEAR: Final = "clear"

ATTR_TAG: Final = "tag"
ATTR_GROUP: Final = "group"
