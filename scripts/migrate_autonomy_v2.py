#!/usr/bin/env python3
"""One-way migration from legacy review/control tasks to the build-first V2 queue."""
from __future__ import annotations

import json
from pathlib import Path

from ai_state import refresh_project_state


ROOT = Path(__file__).resolve().parents[1]
TASKS = ROOT / ".ai" / "tasks"


def save(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def main() -> int:
    retired: list[str] = []
    for path in sorted(TASKS.glob("ERP-*.json")):
        task = json.loads(path.read_text(encoding="utf-8"))
        task_id = str(task.get("id", ""))
        numeric = task_id.removeprefix("ERP-")
        is_legacy_control = task_id in {"ERP-077-F1", "ERP-078-F2"} or (
            numeric.isdigit() and 78 <= int(numeric) <= 94 and task_id != "ERP-086"
        ) or task_id.startswith("ERP-CPF-")
        if not is_legacy_control:
            continue
        task["status"] = "superseded"
        task["superseded_by"] = "AUTONOMY_V2"
        task["superseded_reason"] = "Legacy review, evidence or control-plane task no longer blocks feature development."
        save(path, task)
        retired.append(task_id)

    business = TASKS / "ERP-086.json"
    if business.exists():
        task = json.loads(business.read_text(encoding="utf-8"))
        task["status"] = "pending"
        task["attempts"] = 0
        task["completion_mode"] = "build"
        task["browser_acceptance"] = {"required": False, "deferred": True}
        task["requires_human_approval"] = False
        task["human_gate"] = {"required": False, "level": "L1", "status": "not_required", "reason": "Development build gate."}
        save(business, task)

    refresh_project_state(
        ROOT,
        phase="ready",
        current_task=None,
        last_error=None,
        last_deepseek_fix=None,
        git_sync={"status": "pending", "last_attempt_at": None, "last_error": None},
        runner={"status": "stopped", "pid": None},
    )
    print(json.dumps({"retired": retired, "queue_head": "ERP-086"}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

