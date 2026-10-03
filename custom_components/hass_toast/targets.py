"""Matching a call's ``target`` to the configured machines.

Device names are not case-sensitive anywhere: ``F00BR-NERO``, ``f00br-nero`` and
`` f00br-nero `` all mean the same machine. The agent already compares names that way, so this
only has to make Home Assistant's side agree.

Free of Home Assistant imports so it can be tested without it, like ``signing`` and ``payload``.
"""

from __future__ import annotations

from collections.abc import Iterable, Mapping
from typing import TypeVar

T = TypeVar("T")


def normalise_device_id(name: str) -> str:
    """The form a device name is stored and looked up in."""
    return name.strip().lower()


def pick_devices(
    devices: Mapping[str, T], names: Iterable[str]
) -> tuple[list[T], list[str]]:
    """Return the devices ``names`` refer to, and the names that matched nothing.

    ``devices`` is keyed by :func:`normalise_device_id`. A machine named twice in one target,
    in any mix of case, is returned once: it would otherwise show the same toast twice.
    """
    chosen: list[T] = []
    seen: set[str] = set()
    unknown: list[str] = []

    for name in names:
        key = normalise_device_id(name)
        device = devices.get(key)
        if device is None:
            unknown.append(name)
        elif key not in seen:
            seen.add(key)
            chosen.append(device)

    return chosen, unknown
