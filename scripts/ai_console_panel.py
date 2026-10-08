"""Read-only Windows console for the active NEWERP executor.

Each task requests visibility; a named event reuses the existing viewer.
The viewer owns neither execution leases nor task state. Closing it is safe.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def names(root: Path) -> tuple[str, str]:
    key = hashlib.sha256(str(root.resolve()).casefold().encode()).hexdigest()[:24]
    return (f"Local\\NEWERP_Console_{key}", f"Local\\NEWERP_Console_Show_{key}")


class Windows:
    def __init__(self):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        for name, args, result in [
            ("OpenEventW", [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR], wintypes.HANDLE),
            ("CreateEventW", [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR], wintypes.HANDLE),
            ("CreateMutexW", [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR], wintypes.HANDLE),
            ("SetEvent", [wintypes.HANDLE], wintypes.BOOL),
            ("CloseHandle", [wintypes.HANDLE], wintypes.BOOL),
            ("WaitForSingleObject", [wintypes.HANDLE, wintypes.DWORD], wintypes.DWORD),
            ("GetConsoleWindow", [], wintypes.HWND),
            ("SetConsoleTitleW", [wintypes.LPCWSTR], wintypes.BOOL),
        ]:
            fn = getattr(self.kernel, name); fn.argtypes = args; fn.restype = result
        self.user.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
        self.user.SetForegroundWindow.argtypes = [wintypes.HWND]

    def request(self, root: Path) -> bool:
        event = self.kernel.OpenEventW(2, False, names(root)[1])
        if not event:
            return False
        try:
            return bool(self.kernel.SetEvent(event))
        finally:
            self.kernel.CloseHandle(event)

    def restore(self):
        window = self.kernel.GetConsoleWindow()
        if window:
            self.user.ShowWindow(window, 9)  # SW_RESTORE
            self.user.SetForegroundWindow(window)


def show_console_panel(root: Path) -> dict:
    """Nonblocking, best effort; UI failures must never fail a business task."""
    if os.name != "nt":
        return {"status": "unsupported"}
    try:
        if Windows().request(root):
            return {"status": "reused"}
        child = subprocess.Popen(
            [sys.executable, "-B", str(Path(__file__).resolve()), "--root", str(root.resolve())],
            cwd=root, creationflags=subprocess.CREATE_NEW_CONSOLE,
            close_fds=True,
        )
        return {"status": "launched", "pid": child.pid}
    except (OSError, ValueError) as exc:
        return {"status": "unavailable", "error_type": type(exc).__name__}


def read_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def safe_text(value, limit=110) -> str:
    return "".join(c for c in str(value or "") if c.isprintable())[:limit]


def status_lines(root: Path) -> list[str]:
    state = read_json(root / ".ai/PROJECT_STATE.json")
    task_id = str(state.get("current_task") or "")
    # A corrupt state must never select an arbitrary file outside the task directory.
    task = read_json(root / f".ai/tasks/{task_id}.json") if task_id.startswith("ERP-") and task_id[4:].isdigit() else {}
    decision = read_json(root / ".ai/decisions/STAGE3_PRIORITY_AUTHORIZED.json")
    build = state.get("last_build") or {}
    if not isinstance(build, dict):
        build = {}
    return [
        "NEWERP - development console", "",
        "Phase: " + safe_text(state.get("phase"), 30),
        "Development stage: " + safe_text(decision.get("development_stage") or task.get("planning_stage") or "see acceptance records", 35),
        "Task: " + safe_text(task_id or "waiting", 30),
        "Title: " + safe_text(task.get("title")),
        "Attempt: " + safe_text(task.get("attempts"), 10),
        "Validation: " + safe_text(build.get("status") or build.get("exit_code") or "see project validation logs", 45),
        "", "Closing this panel does not stop development.",
        "The next task opens or restores this panel automatically.",
        "Raw model output, credentials and business data are not displayed.",
    ]


def viewer(root: Path) -> int:
    api = Windows()
    mutex_name, event_name = names(root)
    mutex = api.kernel.CreateMutexW(None, False, mutex_name)
    already_exists = ctypes.get_last_error() == 183
    if not mutex:
        raise ctypes.WinError(ctypes.get_last_error())
    event = None
    try:
        if already_exists:
            for _ in range(20):
                if api.request(root):
                    return 0
                time.sleep(0.1)
            return 0
        event = api.kernel.CreateEventW(None, False, False, event_name)
        if not event:
            raise ctypes.WinError(ctypes.get_last_error())
        api.kernel.SetConsoleTitleW("NEWERP - development console")
        api.restore()
        # VT sequences keep the same screen instead of adding a new dashboard each tick.
        os.system("")
        previous = None
        while True:
            if api.kernel.WaitForSingleObject(event, 0) == 0:
                api.restore()
            screen = "\n".join(status_lines(root))
            if screen != previous:
                print("\x1b[2J\x1b[H" + screen, flush=True)
                previous = screen
            time.sleep(0.5)
    except KeyboardInterrupt:
        return 0
    finally:
        if event:
            api.kernel.CloseHandle(event)
        api.kernel.CloseHandle(mutex)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--show", action="store_true")
    args = parser.parse_args()
    if args.show:
        print(json.dumps(show_console_panel(args.root)))
    else:
        raise SystemExit(viewer(args.root.resolve()))
