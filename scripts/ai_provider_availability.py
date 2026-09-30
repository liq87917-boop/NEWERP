"""Bounded scheduling for transient model-provider quota failures."""
from __future__ import annotations

import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

MARKERS = (
    "insufficient balance",
    "insufficient credits",
    "insufficient quota",
    "quota exceeded",
    "billing hard limit",
)


def is_provider_unavailable(message: object) -> bool:
    return isinstance(message, str) and any(marker in message.lower() for marker in MARKERS)


def provider_failure_from_log(path: Path) -> str | None:
    """Only trust an executor error event, never task or prompt text."""
    if not path.exists():
        return None
    failure = None
    with path.open(encoding="utf-8", errors="replace") as stream:
        for line in stream:
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(event, dict):
                continue
            if (event.get("type") == "run_result" and event.get("finishReason") == "error"
                    and is_provider_unavailable(event.get("text"))):
                failure = str(event["text"])
    return failure


def is_provider_failure(task: dict[str, Any]) -> bool:
    return task.get("failure_kind") == "provider_unavailable" or is_provider_unavailable(task.get("last_error"))


def retry_due(retry: object, now: datetime | None = None) -> bool:
    if not isinstance(retry, dict) or not retry.get("next_probe_at"):
        return True
    try:
        due = datetime.fromisoformat(str(retry["next_probe_at"]).replace("Z", "+00:00"))
        if due.tzinfo is None:
            return True
        return due <= (now or datetime.now(timezone.utc))
    except ValueError:
        return True


def next_retry(previous: object, autonomy: dict[str, Any], now: datetime | None = None) -> dict[str, Any]:
    now = now or datetime.now(timezone.utc)
    previous = previous if isinstance(previous, dict) else {}
    count = max(0, int(previous.get("probe_count", 0) or 0)) + 1
    initial = max(30, int(autonomy.get("provider_retry_initial_seconds", 60)))
    maximum = max(initial, int(autonomy.get("provider_retry_max_seconds", 300)))
    delay = min(maximum, initial * (2 ** min(count - 1, 16)))
    return {
        "probe_count": count,
        "cooldown_seconds": delay,
        "next_probe_at": (now + timedelta(seconds=delay)).isoformat().replace("+00:00", "Z"),
        "last_seen_at": now.isoformat().replace("+00:00", "Z"),
    }
