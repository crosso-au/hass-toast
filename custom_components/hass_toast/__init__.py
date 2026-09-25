"""HASS Windows Toast: rich Windows toast notifications from Home Assistant.

Home Assistant signs an envelope and fires it on the event bus; a Windows agent subscribed
over the WebSocket API verifies and renders it. Nothing is sent directly to the machine, so
no inbound port is opened on the desktop and no address for it is needed here.
"""

from __future__ import annotations

import logging
from typing import Any

import voluptuous as vol
from homeassistant.components.notify import (
    ATTR_DATA,
    ATTR_MESSAGE,
    ATTR_TARGET,
    ATTR_TITLE,
    DOMAIN as NOTIFY_DOMAIN,
)
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.service import async_set_service_schema

from .const import (
    ATTR_GROUP,
    ATTR_TAG,
    CONF_DEVICE_ID,
    CONF_EVENT_TYPE,
    CONF_SIGNING_KEY,
    DEFAULT_EVENT_TYPE,
    DOMAIN,
    OP_CLEAR,
    OP_REMOVE,
    OP_SEND,
    OP_UPDATE,
    SERVICE_CLEAR,
    SERVICE_REMOVE,
    SERVICE_UPDATE,
)
from .payload import build_lifecycle_payload, build_toast
from .service_schema import NOTIFY_SERVICE_SCHEMA
from .signing import build_envelope, decode_key

_LOGGER = logging.getLogger(__name__)

SCENARIOS = ["default", "reminder", "alarm", "incomingCall", "urgent"]

NOTIFY_SCHEMA = vol.Schema(
    {
        vol.Required(ATTR_MESSAGE): cv.string,
        vol.Optional(ATTR_TITLE): cv.string,
        vol.Optional(ATTR_TARGET): vol.Any(cv.string, [cv.string]),
        # The handful of options common enough to deserve their own UI field, so the ordinary
        # case needs no YAML. Everything else lives in `data`.
        vol.Optional(ATTR_TAG): cv.string,
        vol.Optional(ATTR_GROUP): cv.string,
        vol.Optional("scenario"): vol.In(SCENARIOS),
        vol.Optional("image"): cv.string,
        vol.Optional("attribution"): cv.string,
        vol.Optional(ATTR_DATA): dict,
    }
)

UPDATE_SCHEMA = vol.Schema(
    {
        vol.Optional(ATTR_TARGET): vol.Any(cv.string, [cv.string]),
        vol.Required(ATTR_TAG): cv.string,
        vol.Optional(ATTR_GROUP): cv.string,
        vol.Required("progress"): dict,
    }
)

REMOVE_SCHEMA = vol.Schema(
    vol.All(
        {
            vol.Optional(ATTR_TARGET): vol.Any(cv.string, [cv.string]),
            vol.Optional(ATTR_TAG): cv.string,
            vol.Optional(ATTR_GROUP): cv.string,
        },
        cv.has_at_least_one_key(ATTR_TAG, ATTR_GROUP),
    )
)

CLEAR_SCHEMA = vol.Schema({vol.Optional(ATTR_TARGET): vol.Any(cv.string, [cv.string])})


class HassToastDevice:
    """One configured Windows machine."""

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry) -> None:
        self.hass = hass
        self.device_id: str = entry.data[CONF_DEVICE_ID]
        self.event_type: str = entry.data.get(CONF_EVENT_TYPE, DEFAULT_EVENT_TYPE)
        self._key = decode_key(entry.data[CONF_SIGNING_KEY])

    def send(self, op: str, payload: Any) -> None:
        """Sign and fire an envelope for this device."""
        envelope = build_envelope(self.device_id, op, payload, self._key)

        _LOGGER.debug(
            "Firing %s for device %s (op=%s, nonce=%s)",
            self.event_type,
            self.device_id,
            op,
            envelope["nonce"],
        )

        self.hass.bus.async_fire(self.event_type, envelope)


def _devices(hass: HomeAssistant) -> dict[str, HassToastDevice]:
    return hass.data.setdefault(DOMAIN, {})


def _resolve_targets(
    hass: HomeAssistant, target: str | list[str] | None, operation: str
) -> list[HassToastDevice]:
    """Pick which configured machines a call is for.

    With no target the call goes to every configured device, so a single-machine setup never
    has to write one. That fan-out is logged rather than silent: with more than one machine
    configured, an untargeted ``clear`` reaching all of them should be visible in the log
    rather than something you discover from the empty Action Centers.
    """
    devices = _devices(hass)

    if not target:
        chosen = list(devices.values())

        if len(chosen) > 1:
            _LOGGER.info(
                "%s had no target, so it went to all %d configured machines: %s",
                operation,
                len(chosen),
                ", ".join(sorted(d.device_id for d in chosen)),
            )

        return chosen

    names = [target] if isinstance(target, str) else list(target)
    resolved: list[HassToastDevice] = []

    for name in names:
        device = devices.get(name)
        if device is None:
            _LOGGER.warning(
                "No HASS Windows Toast device named '%s' is configured; known devices: %s",
                name,
                ", ".join(sorted(devices)) or "(none)",
            )
            continue
        resolved.append(device)

    return resolved


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Set up a configured Windows machine."""
    device = HassToastDevice(hass, entry)
    _devices(hass)[device.device_id] = device

    _register_services(hass)

    entry.async_on_unload(entry.add_update_listener(_async_reload_entry))
    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Remove a configured machine, and the services once the last one goes."""
    devices = _devices(hass)
    devices.pop(entry.data[CONF_DEVICE_ID], None)

    if not devices:
        hass.services.async_remove(NOTIFY_DOMAIN, DOMAIN)
        for service in (SERVICE_UPDATE, SERVICE_REMOVE, SERVICE_CLEAR):
            hass.services.async_remove(DOMAIN, service)

    return True


async def _async_reload_entry(hass: HomeAssistant, entry: ConfigEntry) -> None:
    await hass.config_entries.async_reload(entry.entry_id)


def _register_services(hass: HomeAssistant) -> None:
    """Register the notify service and the lifecycle actions, once."""
    if hass.services.has_service(NOTIFY_DOMAIN, DOMAIN):
        return

    async def async_notify(call: ServiceCall) -> None:
        """notify.hass_toast — the primary way to raise a toast.

        Registered in the notify domain rather than our own so it reads like every other
        notifier in Home Assistant, and so `target` and `data` mean what people expect.
        """
        toast = build_toast(
            call.data[ATTR_MESSAGE],
            call.data.get(ATTR_TITLE),
            call.data.get(ATTR_DATA),
            tag=call.data.get(ATTR_TAG),
            group=call.data.get(ATTR_GROUP),
            scenario=call.data.get("scenario"),
            image=call.data.get("image"),
            attribution=call.data.get("attribution"),
        )

        for device in _resolve_targets(hass, call.data.get(ATTR_TARGET), "notify.hass_toast"):
            device.send(OP_SEND, toast)

    async def async_update(call: ServiceCall) -> None:
        """Advance an existing toast in place, keeping its position and raising no banner."""
        payload = build_lifecycle_payload(
            call.data[ATTR_TAG],
            call.data.get(ATTR_GROUP),
            {"visual": {"progress": call.data["progress"]}},
        )

        for device in _resolve_targets(hass, call.data.get(ATTR_TARGET), "hass_toast.update"):
            device.send(OP_UPDATE, payload)

    async def async_remove(call: ServiceCall) -> None:
        payload = build_lifecycle_payload(call.data.get(ATTR_TAG), call.data.get(ATTR_GROUP))

        for device in _resolve_targets(hass, call.data.get(ATTR_TARGET), "hass_toast.remove"):
            device.send(OP_REMOVE, payload)

    async def async_clear(call: ServiceCall) -> None:
        for device in _resolve_targets(hass, call.data.get(ATTR_TARGET), "hass_toast.clear"):
            device.send(OP_CLEAR, {})

    hass.services.async_register(NOTIFY_DOMAIN, DOMAIN, async_notify, schema=NOTIFY_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_UPDATE, async_update, schema=UPDATE_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_REMOVE, async_remove, schema=REMOVE_SCHEMA)
    hass.services.async_register(DOMAIN, SERVICE_CLEAR, async_clear, schema=CLEAR_SCHEMA)

    # services.yaml only describes services in this integration's own domain, so the notify
    # service would otherwise render in the UI with no fields and be YAML-only.
    async_set_service_schema(hass, NOTIFY_DOMAIN, DOMAIN, NOTIFY_SERVICE_SCHEMA)
