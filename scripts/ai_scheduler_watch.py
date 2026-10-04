#!/usr/bin/env python3
"""Resident safety net: reload and run the guarded scheduler, never the model directly.

The existing host stays alive. Both launchers share pipeline.lock, so there is
only one executing pipeline. No remote sync, task invention or budget reset here.
"""
from __future__ import annotations

import argparse
import contextlib
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from ai_executor_workspace import selected_executor, workspace_settings, load_config
from ai_state import save_json, utc_now


def executor_root(control: Path) -> Path:
    _, configured, _ = workspace_settings(control, load_config(control))
    selected = selected_executor(control, configured)
    if not (selected / "scripts/ai_pipeline.py").is_file():
        raise RuntimeError(f"Executor scheduler missing: {selected}")
    return selected


@contextlib.contextmanager
def watcher_lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a+b") as stream:
        stream.seek(0)
        if not stream.read(1):
            stream.write(b"0"); stream.flush()
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def cycle(control: Path, log: Path) -> dict:
    root = executor_root(control)
    state = json.loads((root / ".ai/PROJECT_STATE.json").read_text(encoding="utf-8-sig"))
    if state.get("conversation_control", {}).get("paused", False):
        return {"status": "paused", "executor_root": str(root)}
    # No timeout: never kill a running DeepSeek/build operation to service a tick.
    # Fresh child reloads repairs to the scheduler without restarting the host.
    env = dict(os.environ, AI_CONTROL_ROOT=str(control), PYTHONIOENCODING="utf-8")
    with log.open("w", encoding="utf-8") as output:
        completed = subprocess.run(
            [sys.executable, "-B", str(root / "scripts/ai_pipeline.py"), "run"],
            cwd=root, env=env, stdout=output, stderr=subprocess.STDOUT,
        )
    text = log.read_text(encoding="utf-8", errors="replace")
    busy = "automation pipeline is already running" in text
    latest = json.loads((root / ".ai/PROJECT_STATE.json").read_text(encoding="utf-8-sig"))
    return {"status": "busy" if busy else "healthy" if completed.returncode == 0 else "retry_wait",
            "executor_root": str(root), "exit_code": completed.returncode,
            "phase": latest.get("phase"), "current_task": latest.get("current_task"),
            "log": str(log), "error": text[-1200:] if completed.returncode and not busy else None}


def watch(control: Path, interval: int = 30, once: bool = False) -> int:
    logs = control / ".ai/logs"
    failures = 0
    lock = watcher_lock(logs / "scheduler-watch.lock")
    try:
        lock.__enter__()
    except OSError:
        return 0
    try:
            while True:
                # Publish liveness before a long running development cycle.
                try:
                    save_json(logs / "scheduler-watch.json", {
                        "updated_at": utc_now(), "pid": os.getpid(), "status": "checking"})
                    value = cycle(control, logs / "scheduler-watch-cycle.log")
                except Exception as exc:
                    # Broken JSON, transient file locks, failed launch and missing
                    # executor never terminate the resident watcher.
                    value = {"status": "retry_wait", "error": str(exc)}
                failures = failures + 1 if value["status"] == "retry_wait" else 0
                delay = min(300, max(1, interval) * 2 ** min(failures, 3))
                value.update(updated_at=utc_now(), pid=os.getpid(), retry_in_seconds=delay)
                try:
                    save_json(logs / "scheduler-watch.json", value)
                except OSError as exc:
                    print(f"Watch status temporarily unavailable: {exc}", file=sys.stderr)
                if once:
                    print(json.dumps(value, ensure_ascii=False)); return 0
                time.sleep(delay)
    finally:
        lock.__exit__(None, None, None)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--interval", type=int, default=30)
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    raise SystemExit(watch(args.root.resolve(), args.interval, args.once))
