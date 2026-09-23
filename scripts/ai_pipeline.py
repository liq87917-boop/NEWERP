#!/usr/bin/env python3
"""Queue-level automation for the NEWERP guarded task runner."""
from __future__ import annotations

import argparse
import contextlib
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

if os.name == "nt":
    import msvcrt
else:
    import fcntl

ROOT = Path(__file__).resolve().parents[1]
AI_DIR = ROOT / ".ai"
CONFIG_PATH = AI_DIR / "config.json"
STATE_PATH = AI_DIR / "PROJECT_STATE.json"
TASKS_DIR = AI_DIR / "tasks"
DECISIONS_DIR = AI_DIR / "decisions"
AUDIT_PATH = AI_DIR / "audit.jsonl"
ORCHESTRATOR = ROOT / "scripts" / "ai_orchestrator.py"
ACTIVE_STATUSES = {"pending", "retry"}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def save_json(path: Path, value: dict[str, Any]) -> None:
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temp.replace(path)


def audit(event: str, **details: Any) -> None:
    with AUDIT_PATH.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps({"at": utc_now(), "event": event, **details}, ensure_ascii=False) + "\n")


def run(command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=ROOT, text=True, capture_output=capture)


def task_entries() -> list[tuple[Path, dict[str, Any]]]:
    config = load_json(CONFIG_PATH)
    entries = []
    for path in sorted(TASKS_DIR.glob(f"{config['task_prefix']}-*.json")):
        entries.append((path, load_json(path)))
    return entries


def active_tasks() -> list[tuple[Path, dict[str, Any]]]:
    return [(path, task) for path, task in task_entries() if task.get("status") in ACTIVE_STATUSES]


def queue_status() -> int:
    rows = [{"id": task.get("id"), "status": task.get("status"), "risk": task.get("risk_level"), "title": task.get("title")} for _, task in task_entries()]
    print(json.dumps(rows, ensure_ascii=False, indent=2)); return 0


def create_task(title: str, description: str, acceptance: list[str], allowed: list[str], profile: str, risk: str) -> int:
    with pipeline_lock():
        config = load_json(CONFIG_PATH)
        if profile not in config.get("validation_profiles", {}):
            print(f"Unknown validation profile: {profile}", file=sys.stderr); return 2
        numbers = []
        for _, task in task_entries():
            try: numbers.append(int(str(task["id"]).split("-")[-1]))
            except (KeyError, ValueError): pass
        task_id = f"{config['task_prefix']}-{max(numbers, default=0) + 1:03d}"
        sensitive_tokens = ("appsettings", "seeddata", "schemaupgrader", ".sql", ".env", "deploy", "release", "user_input_files")
        protected = any(any(token in path.lower() for token in sensitive_tokens) for path in allowed)
        gate_required = risk == "high" or profile in {"integration", "ui"} or protected
        task = {
            "id": task_id,
            "title": title,
            "status": "pending",
            "risk_level": risk,
            "description": description,
            "acceptance_criteria": acceptance,
            "allowed_paths": allowed,
            "validation_profile": profile,
            "human_gate": {
                "required": gate_required,
                "status": "pending" if gate_required else "not_required",
                "reason": "Automatically required by risk/profile/path policy." if gate_required else ""
            },
            "attempts": 0,
            "created_at": utc_now()
        }
        path = TASKS_DIR / f"{task_id}.json"; save_json(path, task)
        state = load_json(STATE_PATH); state.update({"phase": "ready", "current_task": None, "blocker": None, "finish_reason": "task_queued", "updated_at": utc_now()})
        save_json(STATE_PATH, state); audit("task_created", task=task_id, risk=risk, validation_profile=profile, human_gate=gate_required)
        git_checkpoint(f"{task_id}: queue {title}", [str(path.relative_to(ROOT)), str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        print(json.dumps({"task": task_id, "human_gate_required": gate_required, "path": str(path)}, ensure_ascii=False, indent=2)); return 0


@contextlib.contextmanager
def pipeline_lock() -> Iterator[None]:
    config = load_json(CONFIG_PATH)
    relative = config.get("pipeline", {}).get("lock_file", ".ai/pipeline.lock")
    lock_path = ROOT / relative
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a+b") as stream:
        stream.seek(0)
        if stream.read(1) == b"":
            stream.seek(0); stream.write(b"0"); stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt": msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else: fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as exc:
            raise RuntimeError("Another NEWERP automation pipeline is already running.") from exc
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt": msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else: fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def git_checkpoint(message: str, paths: list[str]) -> None:
    if run(["git", "rev-parse", "--is-inside-work-tree"], capture=True).returncode != 0:
        return
    run(["git", "add", "--", *paths])
    staged = run(["git", "diff", "--cached", "--quiet"])
    if staged.returncode == 1:
        completed = run(["git", "commit", "-m", message])
        if completed.returncode != 0: raise RuntimeError("Could not create pipeline control checkpoint.")


def mark_queue_idle() -> None:
    state = load_json(STATE_PATH)
    if state.get("phase") == "idle" and state.get("finish_reason") == "queue_empty": return
    state.update({"phase": "idle", "current_task": None, "blocker": None, "finish_reason": "queue_empty", "updated_at": utc_now()})
    save_json(STATE_PATH, state); audit("queue_empty")
    git_checkpoint("chore: automation queue drained", [str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])


def run_all() -> int:
    with pipeline_lock():
        while True:
            tasks = active_tasks()
            if not tasks:
                mark_queue_idle(); print("Automation queue completed."); return 0
            task_id = tasks[0][1]["id"]
            print(f"[pipeline] starting {task_id}", flush=True)
            completed = run([sys.executable, str(ORCHESTRATOR), "run-next"])
            if completed.returncode != 0:
                audit("pipeline_stopped", task=task_id, exit_code=completed.returncode)
                print(f"Pipeline stopped at {task_id} with exit code {completed.returncode}.", file=sys.stderr)
                return completed.returncode


def defer_task(task_id: str, actor: str, note: str) -> int:
    with pipeline_lock():
        path = TASKS_DIR / f"{task_id}.json"
        if not path.exists(): print(f"Task not found: {task_id}", file=sys.stderr); return 2
        task = load_json(path)
        if task.get("status") == "completed": print("Completed tasks cannot be deferred.", file=sys.stderr); return 3
        task["status"] = "deferred"
        task.setdefault("human_gate", {}).update({"status": "deferred", "deferred_by": actor, "deferred_at": utc_now(), "note": note})
        save_json(path, task)
        decision_path = DECISIONS_DIR / f"{task_id}-deferred.json"
        save_json(decision_path, {"task": task_id, "decision": "deferred", "actor": actor, "note": note, "at": utc_now()})
        state = load_json(STATE_PATH)
        if state.get("current_task") == task_id:
            state.update({"phase": "ready", "current_task": None, "blocker": None, "finish_reason": "task_deferred", "updated_at": utc_now()})
            save_json(STATE_PATH, state)
        audit("task_deferred", task=task_id, actor=actor, note=note)
        git_checkpoint(f"{task_id}: defer task", [str(path.relative_to(ROOT)), str(decision_path.relative_to(ROOT)), str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        print(f"Deferred {task_id}"); return 0


def retry_task(task_id: str, actor: str, note: str) -> int:
    with pipeline_lock():
        path = TASKS_DIR / f"{task_id}.json"
        if not path.exists(): print(f"Task not found: {task_id}", file=sys.stderr); return 2
        task = load_json(path)
        if task.get("status") not in {"failed", "deferred"}:
            print(f"Task status must be failed or deferred, got {task.get('status')}", file=sys.stderr); return 3
        dirty = run(["git", "status", "--porcelain"], capture=True)
        if dirty.stdout.strip():
            print("Working tree must be clean before retrying a task.", file=sys.stderr); return 4
        task["status"] = "retry"; task["attempts"] = 0
        task.setdefault("human_gate", {}).update({"status": "pending" if task.get("human_gate", {}).get("required") else "not_required"})
        save_json(path, task)
        state = load_json(STATE_PATH)
        state.update({"phase": "ready", "current_task": None, "blocker": None, "finish_reason": "task_requeued", "updated_at": utc_now()})
        save_json(STATE_PATH, state); audit("task_requeued", task=task_id, actor=actor, note=note)
        git_checkpoint(f"{task_id}: requeue task", [str(path.relative_to(ROOT)), str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        print(f"Requeued {task_id}"); return 0


def retry_push() -> int:
    with pipeline_lock():
        config = load_json(CONFIG_PATH)
        attempts = int(config.get("pipeline", {}).get("push_retry_attempts", 3))
        if not run(["git", "remote"], capture=True).stdout.strip():
            print("No Git remote is configured.", file=sys.stderr); return 2
        for attempt in range(1, attempts + 1):
            if run(["git", "push"]).returncode == 0:
                state = load_json(STATE_PATH); state.update({"phase": "ready", "blocker": None, "finish_reason": "push_completed", "updated_at": utc_now()})
                save_json(STATE_PATH, state); audit("push_completed", attempt=attempt)
                git_checkpoint("chore: record successful push", [str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
                return 0
            audit("push_failed", attempt=attempt)
        return 9


def self_test() -> int:
    checks: dict[str, Any] = {}
    errors: list[str] = []
    try: config = load_json(CONFIG_PATH); checks["config"] = "ok"
    except Exception as exc: print(f"Configuration error: {exc}", file=sys.stderr); return 2
    ids = []
    allowed_statuses = {"pending", "retry", "in_progress", "completed", "failed", "deferred", "skipped"}
    for path, task in task_entries():
        ids.append(task.get("id"))
        if task.get("id") != path.stem: errors.append(f"Task id/file mismatch: {path.name}")
        if task.get("status") not in allowed_statuses: errors.append(f"Invalid status in {path.name}")
        if not task.get("allowed_paths"): errors.append(f"Missing allowed_paths in {path.name}")
    if len(ids) != len(set(ids)): errors.append("Duplicate task ids")
    checks["tasks"] = len(ids)
    checks["git"] = "ok" if run(["git", "rev-parse", "--is-inside-work-tree"], capture=True).returncode == 0 else "missing"
    dirty = run(["git", "status", "--porcelain"], capture=True).stdout.strip()
    checks["worktree"] = "clean" if not dirty else "dirty"
    cline = Path(config["cline_command"])
    checks["cline"] = "ok" if cline.exists() and run([str(cline), "--version"], capture=True).returncode == 0 else "unavailable"
    checks["dotnet"] = run(["dotnet", "--version"], capture=True).stdout.strip() or "unavailable"
    protected = set(config.get("protected_paths", []))
    for required in {"**/*.sql", "deploy/**", "src/ERP.Api/appsettings*.json"}:
        if required not in protected: errors.append(f"Missing protected path: {required}")
    checks["high_risk_profiles"] = sorted({"integration", "ui"}.intersection(config.get("validation_profiles", {})))
    if checks["git"] != "ok" or checks["cline"] != "ok": errors.append("Required runtime dependency unavailable")
    print(json.dumps({"status": "passed" if not errors else "failed", "checks": checks, "errors": errors}, ensure_ascii=False, indent=2))
    return 0 if not errors else 5


def main() -> int:
    parser = argparse.ArgumentParser(description="NEWERP full automation pipeline")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("run")
    sub.add_parser("resume")
    sub.add_parser("retry-push")
    sub.add_parser("self-test")
    sub.add_parser("queue")
    cp = sub.add_parser("create")
    cp.add_argument("--title", required=True); cp.add_argument("--description", required=True)
    cp.add_argument("--accept", action="append", required=True); cp.add_argument("--allow", action="append", required=True)
    cp.add_argument("--profile", default="safe"); cp.add_argument("--risk", choices=["low", "medium", "high"], default="low")
    dp = sub.add_parser("defer"); dp.add_argument("task_id"); dp.add_argument("--by", required=True); dp.add_argument("--note", required=True)
    rp = sub.add_parser("retry"); rp.add_argument("task_id"); rp.add_argument("--by", required=True); rp.add_argument("--note", required=True)
    args = parser.parse_args()
    if args.command in {"run", "resume"}: return run_all()
    if args.command == "retry-push": return retry_push()
    if args.command == "self-test": return self_test()
    if args.command == "queue": return queue_status()
    if args.command == "create": return create_task(args.title, args.description, args.accept, args.allow, args.profile, args.risk)
    if args.command == "defer": return defer_task(args.task_id, args.by, args.note)
    return retry_task(args.task_id, args.by, args.note)


if __name__ == "__main__":
    raise SystemExit(main())
