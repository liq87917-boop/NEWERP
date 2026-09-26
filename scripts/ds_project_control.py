#!/usr/bin/env python3
"""File-backed DeepSeek conversation control for the NEWERP automation pipeline."""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
AI_DIR = ROOT / ".ai"
STATE_PATH = AI_DIR / "PROJECT_STATE.json"
CONTROL_DIR = AI_DIR / "control"
CONVERSATION_PATH = CONTROL_DIR / "CONVERSATION_STATE.json"
EVENTS_PATH = CONTROL_DIR / "conversation-events.jsonl"
AUDIT_PATH = AI_DIR / "audit.jsonl"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temp.replace(path)


def append_jsonl(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(value, ensure_ascii=False) + "\n")


def checkpoint(message: str) -> None:
    if subprocess.run(["git", "rev-parse", "--is-inside-work-tree"], cwd=ROOT, capture_output=True).returncode != 0:
        return
    paths = [STATE_PATH, CONVERSATION_PATH, EVENTS_PATH, AUDIT_PATH]
    existing = [str(path.relative_to(ROOT)) for path in paths if path.exists()]
    subprocess.run(["git", "add", "--", *existing], cwd=ROOT, check=True)
    if subprocess.run(["git", "diff", "--cached", "--quiet"], cwd=ROOT).returncode == 1:
        subprocess.run(["git", "commit", "-m", message], cwd=ROOT, check=True)


def current_control() -> dict[str, Any]:
    if CONVERSATION_PATH.exists():
        return load_json(CONVERSATION_PATH)
    return {
        "schema_version": 1,
        "controller": "deepseek_conversation",
        "conversation_id": None,
        "last_message_id": None,
        "last_intent": None,
        "last_summary": None,
        "paused": False,
        "updated_at": utc_now(),
    }


def update_control(intent: str, actor: str, summary: str, conversation_id: str | None, message_id: str | None, paused: bool | None = None) -> None:
    now = utc_now()
    control = current_control()
    control.update({
        "controller": "deepseek_conversation",
        "conversation_id": conversation_id or control.get("conversation_id"),
        "last_message_id": message_id or control.get("last_message_id"),
        "last_intent": intent,
        "last_summary": summary,
        "last_actor": actor,
        "updated_at": now,
    })
    if paused is not None:
        control["paused"] = paused
        control["pause_reason"] = summary if paused else None
    save_json(CONVERSATION_PATH, control)

    state = load_json(STATE_PATH)
    conversation = state.setdefault("conversation_control", {})
    conversation.update({
        "mode": "deepseek_file_backed",
        "paused": bool(control.get("paused", False)),
        "pause_reason": control.get("pause_reason"),
        "last_intent": intent,
        "last_actor": actor,
        "updated_at": now,
    })
    state["updated_at"] = now
    save_json(STATE_PATH, state)
    event = {"at": now, "intent": intent, "actor": actor, "summary": summary, "conversation_id": conversation_id, "message_id": message_id}
    append_jsonl(EVENTS_PATH, event)
    append_jsonl(AUDIT_PATH, {"at": now, "event": "deepseek_control_intent", **event})


def status() -> int:
    state = load_json(STATE_PATH)
    tasks = []
    for path in sorted((AI_DIR / "tasks").glob("ERP-*.json")):
        task = load_json(path)
        tasks.append({"id": task.get("id"), "status": task.get("status"), "title": task.get("title"), "depends_on": task.get("depends_on", [])})
    print(json.dumps({"conversation": current_control(), "project": state, "tasks": tasks}, ensure_ascii=False, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="NEWERP DeepSeek conversation control")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status")
    for name in ("record", "pause", "resume"):
        command = sub.add_parser(name)
        command.add_argument("--by", default="DeepSeek")
        command.add_argument("--summary", required=True)
        command.add_argument("--conversation-id")
        command.add_argument("--message-id")
        if name == "record": command.add_argument("--intent", required=True)
    args = parser.parse_args()
    if args.command == "status": return status()
    intent = args.intent if args.command == "record" else args.command
    paused = True if args.command == "pause" else False if args.command == "resume" else None
    update_control(intent, args.by, args.summary, args.conversation_id, args.message_id, paused)
    checkpoint(f"chore: record DeepSeek control intent {intent}")
    print(json.dumps({"status": "recorded", "intent": intent, "paused": current_control().get("paused")}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
