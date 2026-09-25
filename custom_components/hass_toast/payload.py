"""Turns a notify call into the toast payload the agent expects.

Home Assistant hands us ``message``, ``title`` and a free-form ``data`` dict. The agent wants
a structured toast object. This module is the translation, and deliberately the only place
that knows about both shapes.
"""

from __future__ import annotations

from typing import Any


def build_toast(
    message: str,
    title: str | None,
    data: dict[str, Any] | None,
    *,
    tag: str | None = None,
    group: str | None = None,
    scenario: str | None = None,
    image: str | None = None,
    attribution: str | None = None,
) -> dict[str, Any]:
    """Compose the toast payload.

    The keyword arguments are the handful of options common enough to deserve their own field
    in the Home Assistant UI, so the ordinary case needs no YAML at all. ``data`` carries
    everything else and is passed through almost verbatim — the agent validates it, and
    duplicating that validation here would only create a second place for the two to disagree.

    Where both are given, ``data`` wins. It is the more explicit, more capable form, and
    someone who reached for it meant it.
    """
    toast: dict[str, Any] = {}

    if tag:
        toast["tag"] = tag
    if group:
        toast["group"] = group
    if scenario and scenario != "default":
        toast["scenario"] = scenario

    visual: dict[str, Any] = {}
    if attribution:
        visual["attribution"] = attribution
    if image:
        visual["hero"] = {"src": image}

    # data is applied over the convenience fields, so the richer form takes precedence.
    supplied = dict(data or {})
    supplied_visual = dict(supplied.pop("visual", None) or {})

    toast.update(supplied)
    visual.update(supplied_visual)

    # An explicit visual.text wins: someone who wrote one meant it, and silently prepending
    # to it would make the result impossible to predict.
    if "text" not in visual:
        lines = [line for line in (title, message) if line]
        if lines:
            visual["text"] = lines

    toast["visual"] = visual
    return toast


def build_lifecycle_payload(
    tag: str | None, group: str | None, extra: dict[str, Any] | None = None
) -> dict[str, Any]:
    """Payload for update, remove and clear, which address an existing toast rather than
    describing a new one."""
    payload: dict[str, Any] = dict(extra or {})

    if tag:
        payload["tag"] = tag
    if group:
        payload["group"] = group

    return payload
