#!/usr/bin/env python3
"""Machine-readable project progress for ChatGPT, GitHub and the local runner."""
from __future__ import annotations

import json
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


TERMINAL_STATUSES = {"completed", "deferred", "skipped", "superseded"}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def load_json(path: Path, default: Any = None) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return default


def save_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    # The local status console polls PROJECT_STATE.json on Windows. A reader that
    # opens the destination without delete sharing can make an otherwise atomic
    # os.replace fail transiently with WinError 5. Retry the atomic promotion so
    # a completed task cannot be left out of the canonical project snapshot.
    for attempt in range(8):
        try:
            temporary.replace(path)
            return
        except PermissionError:
            if attempt == 7:
                raise
            time.sleep(0.05 * (attempt + 1))


def _git(root: Path, *args: str) -> tuple[int, str]:
    completed = subprocess.run(
        ["git", *args], cwd=root, text=True, capture_output=True, errors="replace"
    )
    return completed.returncode, (completed.stdout or completed.stderr).strip()


def task_snapshot(root: Path) -> list[dict[str, Any]]:
    config = load_json(root / ".ai" / "config.json", {})
    prefix = config.get("task_prefix", "ERP")
    tasks: list[dict[str, Any]] = []
    for path in sorted((root / ".ai" / "tasks").glob(f"{prefix}-*.json")):
        task = load_json(path)
        if isinstance(task, dict):
            tasks.append(task)
    return tasks


def refresh_project_state(root: Path, **updates: Any) -> dict[str, Any]:
    """Refresh the canonical status file while preserving useful execution details."""
    state_path = root / ".ai" / "PROJECT_STATE.json"
    previous = load_json(state_path, {})
    tasks = task_snapshot(root)
    by_status: dict[str, list[str]] = {}
    for task in tasks:
        by_status.setdefault(str(task.get("status", "unknown")), []).append(str(task.get("id")))

    runnable = [
        str(task.get("id"))
        for task in tasks
        if task.get("status") in {"pending", "retry", "in_progress", "code_ready"}
    ]
    completed = by_status.get("completed", [])
    blocked = [
        {
            "task": str(task.get("id")),
            "status": str(task.get("status")),
            "reason": task.get("blocker") or task.get("last_error") or "",
            "attempts": int(task.get("attempts", 0) or 0),
        }
        for task in tasks
        if task.get("status") in {"blocked", "failed"}
    ]

    _, branch = _git(root, "branch", "--show-current")
    _, local_sha = _git(root, "rev-parse", "--short", "HEAD")
    remote_code, divergence = _git(root, "rev-list", "--left-right", "--count", "HEAD...@{upstream}")
    ahead = behind = None
    if remote_code == 0:
        try:
            ahead, behind = (int(part) for part in divergence.split())
        except (TypeError, ValueError):
            pass

    state: dict[str, Any] = {
        "schema_version": 2,
        "project": "NEWERP",
        "updated_at": utc_now(),
        "phase": previous.get("phase", "ready"),
        "current_task": previous.get("current_task"),
        "queue": runnable,
        "completed": completed,
        "blocked": blocked,
        "last_build": previous.get("last_build"),
        "last_error": previous.get("last_error"),
        "last_deepseek_fix": previous.get("last_deepseek_fix"),
        "git_sync": {
            "branch": branch or None,
            "local_sha": local_sha or None,
            "status": previous.get("git_sync", {}).get("status", "unknown"),
            "ahead": ahead,
            "behind": behind,
            "last_attempt_at": previous.get("git_sync", {}).get("last_attempt_at"),
            "last_error": previous.get("git_sync", {}).get("last_error"),
        },
        "next_recommended_tasks": runnable[:4],
        "runner": previous.get("runner", {"status": "stopped", "pid": None}),
    }
    git_sync_update = updates.pop("git_sync", None)
    state.update(updates)
    if isinstance(git_sync_update, dict):
        state["git_sync"].update(git_sync_update)
    save_json(state_path, state)
    return state

