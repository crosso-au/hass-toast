"""UI description for the notify service.

``services.yaml`` only describes services in the integration's own domain. ``notify.hass_toast``
is registered in the *notify* domain, so Home Assistant has nothing to render for it and the
action shows up in the UI with no fields at all — usable only from YAML.

``async_set_service_schema`` supplies that description at runtime, which is how the built-in
notify platforms get their fields too.
"""

from __future__ import annotations

from typing import Any

NOTIFY_SERVICE_SCHEMA: dict[str, Any] = {
    # Carries the integration name explicitly. Home Assistant prefixes actions with it only in
    # the integration's own domain, and this one is registered under notify, so without it the
    # action sorts away from the other three and reads as though it belongs to nothing.
    "name": "HASS Windows Toast: Send a Windows toast",
    "description": (
        "Raise a rich toast notification on a Windows machine running the HASS Windows Toast agent."
    ),
    "fields": {
        "message": {
            "name": "Message",
            "description": "Body text of the toast.",
            "required": True,
            "example": "main @ a1b2c3d — 3 tests failing",
            "selector": {"text": {"multiline": True}},
        },
        "title": {
            "name": "Title",
            "description": "Bold first line, shown above the message.",
            "required": False,
            "example": "Build failed",
            "selector": {"text": None},
        },
        "target": {
            "name": "Device",
            "description": (
                "Device id of the machine to notify, matching the agent's configured device id. "
                "Leave empty to reach every configured machine."
            ),
            "required": False,
            "example": "ryan-desktop",
            "selector": {"text": None},
        },
        "tag": {
            "name": "Tag",
            "description": (
                "Identity for this toast. Sending again with the same tag replaces it rather "
                "than stacking up, and it is what hass_toast.update and .remove address."
            ),
            "required": False,
            "example": "backup-nightly",
            "selector": {"text": None},
        },
        "group": {
            "name": "Group",
            "description": "Groups related toasts, so a whole group can be removed at once.",
            "required": False,
            "example": "maintenance",
            "selector": {"text": None},
        },
        "scenario": {
            "name": "Scenario",
            "description": (
                "Anything other than Default stays on screen until dismissed. Alarm loops its "
                "sound, Incoming call uses the call layout, and Urgent breaks through Do Not "
                "Disturb where Windows supports it. Reminder, Alarm and Incoming call require a "
                "button to stay on screen; the agent adds a Dismiss button if you supply none."
            ),
            "required": False,
            "selector": {
                "select": {
                    "mode": "dropdown",
                    "options": [
                        {"value": "default", "label": "Default"},
                        {"value": "reminder", "label": "Reminder — stays until dismissed"},
                        {"value": "alarm", "label": "Alarm — stays, loops alarm sound"},
                        {"value": "incomingCall", "label": "Incoming call — call layout"},
                        {"value": "urgent", "label": "Urgent — breaks through Do Not Disturb"},
                    ],
                }
            },
        },
        "image": {
            "name": "Image",
            "description": (
                "URL of a banner image shown above the text. Fetched and checked by the agent, "
                "which caps it at 200 KB."
            ),
            "required": False,
            "example": "https://ha.local/local/camera-snapshot.jpg",
            "selector": {"text": None},
        },
        "attribution": {
            "name": "Attribution",
            "description": "Small line at the bottom, for naming the source.",
            "required": False,
            "example": "Front door camera",
            "selector": {"text": None},
        },
        "data": {
            "name": "Windows options",
            "description": (
                "Everything Windows-specific: tag, group, scenario, buttons, inputs, images, "
                "progress and adaptive layouts. See docs/payload-schema.md for the full set."
            ),
            "required": False,
            "example": (
                'tag: build-42\n'
                'scenario: reminder\n'
                'buttons:\n'
                '  - content: Retry\n'
                '    args: {action: retry}\n'
                '    style: Success'
            ),
            "selector": {"object": None},
        },
    },
}
