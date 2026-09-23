#!/usr/bin/env python3
"""Guarded single-task Cline orchestrator for NEWERP."""
from __future__ import annotations
import argparse, fnmatch, json, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
AI_DIR, TASKS_DIR = ROOT / ".ai", ROOT / ".ai" / "tasks"
CONFIG_PATH, STATE_PATH = AI_DIR / "config.json", AI_DIR / "PROJECT_STATE.json"
LOGS_DIR, RESULTS_DIR, DECISIONS_DIR = AI_DIR / "logs", AI_DIR / "results", AI_DIR / "decisions"
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

def audit(event: str, **details: Any) -> None:
    record = {"at": utc_now(), "event": event, **details}
    with AUDIT_PATH.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(record, ensure_ascii=False) + "\n")

def run(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=ROOT, text=True, capture_output=True)

def git_available() -> bool:
    return run(["git", "rev-parse", "--is-inside-work-tree"]).returncode == 0

def git_lines(*args: str) -> list[str]:
    result = run(["git", *args])
    if result.returncode != 0: raise RuntimeError(result.stderr.strip() or "Git command failed")
    return [line.strip().replace("\\", "/") for line in result.stdout.splitlines() if line.strip()]

def changed_paths() -> list[str]:
    return sorted(set(git_lines("diff", "--name-only") + git_lines("diff", "--cached", "--name-only") + git_lines("ls-files", "--others", "--exclude-standard")))

def matches(path: str, patterns: list[str]) -> bool:
    normalized = path.replace("\\", "/")
    return any(fnmatch.fnmatch(normalized, pattern) for pattern in patterns)

def validate_task(task: dict[str, Any], config: dict[str, Any]) -> None:
    required = {"id", "title", "status", "description", "acceptance_criteria", "allowed_paths", "validation_profile", "human_gate"}
    missing = sorted(required - task.keys())
    if missing: raise ValueError(f"Task is missing fields: {', '.join(missing)}")
    if not task["id"].startswith(config["task_prefix"] + "-"): raise ValueError("Task id has the wrong prefix")
    if not task["allowed_paths"]: raise ValueError("Task must declare at least one allowed path")
    if task["validation_profile"] not in config["validation_profiles"]: raise ValueError("Task names an unknown validation profile")

def pending_tasks(config: dict[str, Any]) -> list[tuple[Path, dict[str, Any]]]:
    found = []
    for path in sorted(TASKS_DIR.glob(f"{config['task_prefix']}-*.json")):
        task = load_json(path)
        if task.get("status") in {"pending", "retry"}: found.append((path, task))
    return found

def gate_is_approved(task: dict[str, Any]) -> bool:
    gate = task.get("human_gate", {})
    return not gate.get("required", False) or gate.get("status") == "approved"

def path_violations(task: dict[str, Any], config: dict[str, Any], paths: list[str]) -> list[str]:
    violations = []
    for path in paths:
        if matches(path, config["ignored_change_paths"]): continue
        if matches(path, config.get("orchestrator_paths", [])): continue
        if not matches(path, task["allowed_paths"]): violations.append(f"outside allowed_paths: {path}")
        if matches(path, config["protected_paths"]) and not gate_is_approved(task): violations.append(f"protected path without approved Human Gate: {path}")
    return violations

def set_state(state: dict[str, Any], **updates: Any) -> None:
    state.update(updates); state["updated_at"] = utc_now(); save_json(STATE_PATH, state)

def status() -> int:
    config, state = load_json(CONFIG_PATH), load_json(STATE_PATH)
    tasks = pending_tasks(config)
    print(json.dumps({"git_repository": git_available(), "state": state, "next_task": tasks[0][1]["id"] if tasks else None, "pending_count": len(tasks)}, ensure_ascii=False, indent=2))
    return 0

def approve(task_id: str, actor: str, note: str) -> int:
    path = TASKS_DIR / f"{task_id}.json"
    if not path.exists(): print(f"Task not found: {task_id}", file=sys.stderr); return 2
    task = load_json(path); gate = task.setdefault("human_gate", {})
    gate.update({"required": True, "status": "approved", "approved_by": actor, "approved_at": utc_now(), "note": note})
    save_json(path, task)
    decision = {"task": task_id, "decision": "approved", "actor": actor, "note": note, "at": utc_now()}
    save_json(DECISIONS_DIR / f"{task_id}-approved.json", decision); audit("human_gate_approved", **decision)
    print(f"Approved {task_id}"); return 0

def approve_baseline(actor: str, note: str) -> int:
    decision = {"decision": "approved", "scope": "security_baseline", "actor": actor, "note": note, "at": utc_now()}
    save_json(DECISIONS_DIR / "SECURITY_BASELINE_APPROVED.json", decision)
    audit("security_baseline_approved", **decision)
    print("Approved security baseline"); return 0

def build_prompt(task: dict[str, Any], attempt: int, previous_error: str) -> str:
    base = (AI_DIR / "prompts" / "developer.md").read_text(encoding="utf-8")
    retry = f"\nPrevious attempt failed:\n{previous_error[-4000:]}\n" if previous_error else ""
    return f"{base}\n\nCurrent task JSON:\n{json.dumps(task, ensure_ascii=False, indent=2)}\n\nAttempt: {attempt}{retry}"

def run_next(dry_run: bool) -> int:
    config, state = load_json(CONFIG_PATH), load_json(STATE_PATH)
    tasks = pending_tasks(config)
    if not tasks: print("No pending task."); return 0
    task_path, task = tasks[0]; validate_task(task, config)
    baseline_path = ROOT / config["security_baseline_decision"]
    baseline = load_json(baseline_path) if baseline_path.exists() else {}
    if baseline.get("decision") != "approved":
        set_state(state, phase="blocked", current_task=task["id"], blocker="Security baseline approval is required after credential rotation and secret cleanup.")
        audit("blocked", task=task["id"], reason="security_baseline_not_approved")
        print("Blocked: rotate/remove exposed credentials, then approve the security baseline.", file=sys.stderr); return 3
    if config.get("require_git", True) and not git_available():
        set_state(state, phase="blocked", current_task=task["id"], blocker="Git repository is required before automation can run.")
        audit("blocked", task=task["id"], reason="git_repository_missing")
        print("Blocked: initialize and secure the Git repository first.", file=sys.stderr); return 4
    if config.get("require_clean_worktree", True) and changed_paths():
        set_state(state, phase="blocked", current_task=task["id"], blocker="Working tree is not clean.")
        audit("blocked", task=task["id"], reason="dirty_worktree")
        print("Blocked: working tree must be clean before starting a task.", file=sys.stderr); return 5
    if not gate_is_approved(task):
        set_state(state, phase="waiting_human_gate", current_task=task["id"], blocker=task.get("human_gate", {}).get("reason", "Human Gate approval required."))
        audit("waiting_human_gate", task=task["id"]); print(f"Waiting for Human Gate approval: {task['id']}"); return 6
    if dry_run: print(build_prompt(task, 1, "")); return 0
    task["status"] = "in_progress"; save_json(task_path, task)
    set_state(state, phase="developing", current_task=task["id"], blocker=None, finish_reason=None); audit("task_started", task=task["id"])
    previous_error, cline_code = "", None
    for attempt in range(1, int(config["max_attempts"]) + 1):
        task["attempts"] = attempt; save_json(task_path, task)
        log_path = LOGS_DIR / f"{task['id']}-attempt-{attempt}.jsonl"; LOGS_DIR.mkdir(parents=True, exist_ok=True)
        command = [config["cline_command"], "--json", "--auto-approve", "true", "--cwd", str(ROOT), "--timeout", str(config["cline_timeout_seconds"]), build_prompt(task, attempt, previous_error)]
        with log_path.open("w", encoding="utf-8") as log:
            cline_code = subprocess.run(command, cwd=ROOT, text=True, stdout=log, stderr=subprocess.STDOUT).returncode
        violations = path_violations(task, config, changed_paths())
        if violations:
            previous_error = "Path guard failed:\n" + "\n".join(violations); audit("path_guard_failed", task=task["id"], attempt=attempt, violations=violations); break
        if cline_code != 0:
            previous_error = f"Cline exited with code {cline_code}. See {log_path.relative_to(ROOT)}"; audit("cline_failed", task=task["id"], attempt=attempt, exit_code=cline_code); continue
        validation = subprocess.run([sys.executable, str(ROOT / "scripts" / "ai_validate.py"), "--profile", task["validation_profile"], "--task", task["id"]], cwd=ROOT)
        if validation.returncode == 0:
            business_changes = [p for p in changed_paths() if not matches(p, config["ignored_change_paths"]) and not matches(p, config.get("orchestrator_paths", []))]
            if not business_changes: previous_error = "Task produced no checkpointable business changes."; continue
            task["status"] = "completed"; save_json(task_path, task)
            result = {"task": task["id"], "status": "completed", "attempts": attempt, "cline_exit_code": cline_code, "finished_at": utc_now()}
            save_json(RESULTS_DIR / f"{task['id']}.json", result)
            set_state(state, phase="ready", current_task=None, last_completed_task=task["id"], validation={"profile": task["validation_profile"], "status": "passed"}, cline_exit_code=cline_code, finish_reason="task_completed")
            audit("task_completed", **result)
            if config.get("auto_commit", True):
                checkpoint_paths = [p for p in changed_paths() if not matches(p, config["ignored_change_paths"])]
                subprocess.run(["git", "add", "--", *checkpoint_paths], cwd=ROOT, check=True)
                if subprocess.run(["git", "commit", "-m", f"{task['id']}: {task['title']}"], cwd=ROOT).returncode != 0:
                    set_state(state, phase="human_attention", blocker="Git commit failed; validated work was not rerun.", finish_reason="checkpoint_failed")
                    audit("checkpoint_failed", task=task["id"]); return 8
                if config.get("auto_push", False):
                    pushed = False
                    for push_attempt in range(1, 4):
                        if subprocess.run(["git", "push"], cwd=ROOT).returncode == 0:
                            pushed = True; break
                        audit("push_failed", task=task["id"], attempt=push_attempt)
                    if not pushed:
                        set_state(state, phase="push_pending", blocker="Git push failed after 3 attempts; development and validation will not be rerun.", finish_reason="push_failed")
                        return 9
            print(f"Completed {task['id']}"); return 0
        previous_error = f"Validation failed with code {validation.returncode}."; audit("validation_failed", task=task["id"], attempt=attempt, exit_code=validation.returncode)
    task["status"] = "failed"; save_json(task_path, task)
    set_state(state, phase="human_attention", current_task=task["id"], blocker=previous_error, cline_exit_code=cline_code, finish_reason="attempts_exhausted_or_guard_failed")
    audit("task_failed", task=task["id"], reason=previous_error); print(f"Failed {task['id']}: {previous_error}", file=sys.stderr); return 7

def main() -> int:
    parser = argparse.ArgumentParser(description="NEWERP guarded Cline orchestrator"); sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status"); rp = sub.add_parser("run-next"); rp.add_argument("--dry-run", action="store_true")
    ap = sub.add_parser("approve"); ap.add_argument("task_id"); ap.add_argument("--by", required=True); ap.add_argument("--note", required=True)
    bp = sub.add_parser("approve-baseline"); bp.add_argument("--by", required=True); bp.add_argument("--note", required=True)
    args = parser.parse_args()
    if args.command == "status": return status()
    if args.command == "approve": return approve(args.task_id, args.by, args.note)
    if args.command == "approve-baseline": return approve_baseline(args.by, args.note)
    return run_next(args.dry_run)

if __name__ == "__main__": raise SystemExit(main())
