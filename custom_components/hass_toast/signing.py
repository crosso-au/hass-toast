"""Envelope signing, byte-for-byte compatible with the Windows agent.

The agent signs the toast body as an *opaque JSON string* rather than a re-serialised
object. Canonical JSON across Python and C# is a trap: float formatting, key ordering,
integer-versus-float typing and non-ASCII escaping all differ between the two, and any
one of them silently breaks every signature. Signing the exact bytes produced here
removes the whole class of problem — the agent verifies the string first, then parses it.

Fields are length-prefixed (``<utf-8 byte count>:<bytes>``) rather than joined with a
delimiter, so no field's content can be crafted to look like a field boundary.

Any change to this format must be made in ``PayloadSigner.cs`` at the same time.
``docs/signing-vectors.json`` is checked by both test suites to catch drift.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import secrets
import time
from typing import Any

SCHEMA_VERSION = 1


def compact_json(value: Any) -> str:
    """Serialise a payload the way the agent expects to receive it.

    Separators are tightened so the output carries no incidental whitespace, and
    non-ASCII characters are left as themselves rather than escaped — the agent reads
    UTF-8, and escaping would only make the signed bytes harder to reason about.
    """
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def build_signing_input(
    version: int,
    device_id: str,
    op: str,
    nonce: str,
    timestamp: int,
    payload: str,
) -> bytes:
    """Build the exact byte string both sides sign."""

    def field(value: str) -> bytes:
        encoded = value.encode("utf-8")
        return f"{len(encoded)}:".encode("utf-8") + encoded

    return (
        field(str(version))
        + field(device_id)
        + field(op)
        + field(nonce)
        + field(str(timestamp))
        + field(payload)
    )


def compute_signature(key: bytes, signing_input: bytes) -> str:
    """HMAC-SHA256 over the signing input, base64 encoded."""
    return base64.b64encode(
        hmac.new(key, signing_input, hashlib.sha256).digest()
    ).decode("ascii")


def build_envelope(
    device_id: str,
    op: str,
    payload: Any,
    key: bytes,
    *,
    nonce: str | None = None,
    timestamp: int | None = None,
) -> dict[str, Any]:
    """Build a signed envelope ready to fire onto the event bus.

    The nonce and timestamp together bound replay: the agent rejects an envelope outside
    its tolerance window, and rejects any nonce it has already seen inside that window.
    """
    payload_json = compact_json(payload)
    nonce = nonce or secrets.token_hex(16)
    timestamp = timestamp if timestamp is not None else int(time.time())

    signing_input = build_signing_input(
        SCHEMA_VERSION, device_id, op, nonce, timestamp, payload_json
    )

    return {
        "v": SCHEMA_VERSION,
        "device_id": device_id,
        "op": op,
        "nonce": nonce,
        "ts": timestamp,
        "payload": payload_json,
        "sig": compute_signature(key, signing_input),
    }


def decode_key(value: str) -> bytes:
    """Decode the base64 signing key the agent prints from ``--show-key``."""
    key = base64.b64decode(value, validate=True)
    if len(key) != 32:
        raise ValueError(
            f"signing key must decode to 32 bytes, got {len(key)}"
        )
    return key
