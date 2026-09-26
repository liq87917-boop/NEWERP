#!/usr/bin/env python3
"""Single-task NEWERP executor. Real browser acceptance, not Cline exit, defines done."""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
AI_DIR = ROOT / ".ai"
TASKS_DIR, RESULTS_DIR = AI_DIR / "tasks", AI_DIR / "results"
LOGS_DIR, DECISIONS_DIR = AI_DIR / "logs", AI_DIR / "decisions"
CONFIG_PATH, STATE_PATH = AI_DIR / "config.json", AI_DIR / "PROJECT_STATE.json"
AUDIT_PATH = AI_DIR / "audit.jsonl"
TERMINAL_STATUSES = {"completed", "deferred", "skipped"}


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
    with AUDIT_PATH.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps({"at": utc_now(), "event": event, **details}, ensure_ascii=False) + "\n")


def run(command: list[str], *, capture: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=ROOT, text=True, capture_output=capture)


def git_available() -> bool:
    return run(["git", "rev-parse", "--is-inside-work-tree"]).returncode == 0


def git_lines(*args: str) -> list[str]:
    # Force Git to emit real UTF-8 paths instead of C-style quoted/octal names.
    # Use explicit byte pipes here instead of the generic run() helper because
    # local recovery must never depend on a console host's stdout redirection state.
    result = subprocess.run(
        ["git", "-c", "core.quotepath=false", *args],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=False,
    )
    stdout = (result.stdout or b"").decode("utf-8", errors="replace")
    stderr = (result.stderr or b"").decode("utf-8", errors="replace")
    if result.returncode != 0:
        raise RuntimeError(stderr.strip() or "Git command failed")
    return [line.strip().replace("\\", "/") for line in stdout.splitlines() if line.strip()]


def changed_paths() -> list[str]:
    return sorted(set(git_lines("diff", "--name-only") + git_lines("diff", "--cached", "--name-only") + git_lines("ls-files", "--others", "--exclude-standard")))


def matches(path: str, patterns: list[str]) -> bool:
    normalized = path.replace("\\", "/")
    return any(fnmatch.fnmatch(normalized, pattern) for pattern in patterns)


def all_tasks(config: dict[str, Any]) -> list[tuple[Path, dict[str, Any]]]:
    return [(path, load_json(path)) for path in sorted(TASKS_DIR.glob(f"{config['task_prefix']}-*.json"))]


def next_task(config: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Select a dependency-safe task; failed/gated/blocked tasks do not globally stop work."""
    for path, task in all_tasks(config):
        status = task.get("status")
        if status in TERMINAL_STATUSES:
            continue
        if status in {"in_progress", "code_ready"}:
            raise ValueError(f"{task.get('id')} status={status} is an active execution")
        if status not in {"pending", "retry"}:
            continue
        dependencies_ok, _ = dependencies_completed(task, config)
        if not dependencies_ok:
            continue
        if not task.get("auto_start", True):
            continue
        if not gate_is_approved(task):
            continue
        return path, task
    return None


def normalize_failed_head_for_deferred_browser(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Turn a stale failed queue head into retry when browser/UI is globally deferred.

    This intentionally ignores historical phase/blocker wording. The task is not
    marked completed here; it must still pass the configured core engineering
    validation before completion.
    """
    if not browser_acceptance_is_deferred(config):
        return None
    failure_context = " ".join([
        str(state.get("blocker") or ""),
        str(state.get("finish_reason") or ""),
        json.dumps(state.get("browser_acceptance") or {}, ensure_ascii=False),
    ]).lower()
    if "browser" not in failure_context:
        return None
    for path, task in all_tasks(config):
        if task.get("status") in TERMINAL_STATUSES:
            continue
        if task.get("status") != "failed":
            return None
        task["status"] = "retry"
        task["attempts"] = 0
        save_json(path, task)
        set_state(
            state,
            phase="ready",
            current_task=task.get("id"),
            blocker=None,
            finish_reason="failed_head_normalized_for_deferred_browser",
            browser_acceptance={"status": "deferred"},
        )
        audit("failed_head_normalized_for_deferred_browser", task=task.get("id"))
        return path, task
    return None


def validate_task(task: dict[str, Any], config: dict[str, Any]) -> None:
    required = {"id", "title", "status", "description", "acceptance_criteria", "allowed_paths", "validation_profile", "human_gate"}
    missing = sorted(required - task.keys())
    if missing: raise ValueError(f"Task is missing fields: {', '.join(missing)}")
    if not task["id"].startswith(config["task_prefix"] + "-"): raise ValueError("Task id has the wrong prefix")
    if not task["allowed_paths"]: raise ValueError("Task must declare allowed_paths")
    if task["validation_profile"] not in config["validation_profiles"]: raise ValueError("Unknown validation profile")
    mode = task.get("completion_mode", config.get("completion_policy", {}).get("default_mode", "browser"))
    if mode not in {"browser", "control_plane"}: raise ValueError("completion_mode must be browser or control_plane")
    if mode == "browser" and not task.get("browser_acceptance", {}).get("scenarios"):
        raise ValueError("Browser-completed tasks require browser_acceptance.scenarios")


def browser_acceptance_is_deferred(config: dict[str, Any]) -> bool:
    """Return True while feature development intentionally defers real-browser/UI acceptance."""
    return bool(config.get("completion_policy", {}).get("defer_browser_during_development", False))


def gate_is_approved(task: dict[str, Any]) -> bool:
    gate = task.get("human_gate", {})
    level = str(gate.get("level", "L1")).upper()
    if level in {"L3", "L4"} or task.get("requires_human_approval", False):
        return gate.get("status") == "approved"
    return not gate.get("required", False) or gate.get("status") in {"approved", "not_required", "ai_reviewed"}


def dependencies_completed(task: dict[str, Any], config: dict[str, Any]) -> tuple[bool, str]:
    by_id = {entry.get("id"): entry for _, entry in all_tasks(config)}
    for dependency in task.get("depends_on", []):
        if dependency not in by_id: return False, f"dependency missing: {dependency}"
        status = by_id[dependency].get("status")
        if status != "completed": return False, f"dependency {dependency} status={status}"
    return True, "ready"


def path_violations(task: dict[str, Any], config: dict[str, Any]) -> list[str]:
    violations = []
    for path in changed_paths():
        if matches(path, config["ignored_change_paths"]) or matches(path, config.get("orchestrator_paths", [])): continue
        if not matches(path, task["allowed_paths"]): violations.append(f"outside allowed_paths: {path}")
        if matches(path, config["protected_paths"]) and not gate_is_approved(task): violations.append(f"protected without approved gate: {path}")
    return violations


def recoverable_path_guard_task(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Return a failed task whose existing dirty work can be safely resumed.

    This recovery is intentionally narrow: it only applies when the previous stop
    reason was Path Guard, the task is still the current failed task, and the
    *current* path guard now reports zero violations. This lets us recover from
    control-plane false positives (for example quoted Unicode filenames) without
    weakening the guard for genuinely out-of-scope changes.
    """
    if state.get("phase") != "human_attention":
        return None
    if state.get("finish_reason") != "attempts_exhausted":
        return None
    blocker = str(state.get("blocker") or "")
    if not blocker.startswith("Path guard failed:"):
        return None
    task_id = state.get("current_task")
    if not task_id:
        return None
    path = TASKS_DIR / f"{task_id}.json"
    if not path.exists():
        return None
    task = load_json(path)
    if task.get("status") != "failed":
        return None
    try:
        validate_task(task, config)
    except ValueError:
        return None
    if path_violations(task, config):
        return None
    return path, task


def recoverable_interrupted_task(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Resume an interrupted local task without discarding its existing work.

    The agent owns the local pipeline process. If that process disappears while the
    durable state still says developing/browser_acceptance, we may safely validate
    the existing dirty tree again only when every changed path is still inside the
    task guard. This turns console/process crashes into resumable work instead of a
    permanent manual stop.
    """
    if state.get("phase") not in {"developing", "browser_acceptance"}:
        return None
    task_id = state.get("current_task")
    if not task_id:
        return None
    path = TASKS_DIR / f"{task_id}.json"
    if not path.exists():
        return None
    task = load_json(path)
    if task.get("status") not in {"in_progress", "code_ready"}:
        return None
    try:
        validate_task(task, config)
    except ValueError:
        return None
    if not changed_paths() or path_violations(task, config):
        return None
    return path, task


def recoverable_deferred_failed_head(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Recover a queue head left failed by the old browser-blocking policy.

    After the policy switches to deferred browser/UI acceptance, an old failed task
    may already have been rewritten by queue inspection to phase=blocked with a
    generic "status=failed stops queue" blocker. Recover that stale state once and
    re-run core engineering validation against the existing guarded worktree.
    """
    if not browser_acceptance_is_deferred(config):
        return None
    if state.get("phase") != "blocked" or state.get("finish_reason") != "queue_head_blocked":
        return None
    task_id = state.get("current_task")
    if not task_id:
        return None
    blocker = str(state.get("blocker") or "")
    if blocker != f"{task_id} status=failed stops queue":
        return None
    path = TASKS_DIR / f"{task_id}.json"
    if not path.exists():
        return None
    task = load_json(path)
    if task.get("status") != "failed":
        return None
    try:
        validate_task(task, config)
    except ValueError:
        return None
    if path_violations(task, config):
        return None
    return path, task


def recoverable_browser_failure_task(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Recover a task that was previously failed only by real-browser acceptance.

    When browser/UI acceptance is deferred for the feature-development phase, an
    existing browser failure must be recoverable regardless of the historical retry
    count so the task can be re-evaluated using core engineering validation only.
    """
    if state.get("phase") != "human_attention":
        return None
    if state.get("finish_reason") != "attempts_exhausted":
        return None
    blocker = str(state.get("blocker") or "")
    if not blocker.startswith("Real-browser acceptance failed"):
        return None
    task_id = state.get("current_task")
    if not task_id:
        return None
    path = TASKS_DIR / f"{task_id}.json"
    if not path.exists():
        return None
    task = load_json(path)
    if task.get("status") != "failed":
        return None
    if not browser_acceptance_is_deferred(config) and int(task.get("browser_recovery_cycles", 0) or 0) >= 2:
        return None
    try:
        validate_task(task, config)
    except ValueError:
        return None
    if not changed_paths() or path_violations(task, config):
        return None
    return path, task


def set_state(state: dict[str, Any], **updates: Any) -> None:
    state.update(updates); state["updated_at"] = utc_now(); save_json(STATE_PATH, state)


def checkpoint_control_files(message: str) -> None:
    if not git_available(): return
    config = load_json(CONFIG_PATH); paths = changed_paths()
    unexpected = [path for path in paths if not matches(path, config["ignored_change_paths"]) and not matches(path, config.get("orchestrator_paths", []))]
    if unexpected: raise RuntimeError("Unrelated changes prevent control checkpoint: " + ", ".join(unexpected))
    control = [path for path in paths if matches(path, config.get("orchestrator_paths", []))]
    if not control: return
    subprocess.run(["git", "add", "--", *control], cwd=ROOT, check=True)
    if subprocess.run(["git", "commit", "-m", message], cwd=ROOT).returncode != 0: raise RuntimeError("Control checkpoint failed")


def status() -> int:
    config, state = load_json(CONFIG_PATH), load_json(STATE_PATH)
    try:
        item = next_task(config); queue_blocker = None
    except ValueError as exc:
        item = None; queue_blocker = str(exc)
    print(json.dumps({"git_repository": git_available(), "state": state, "next_task": item[1]["id"] if item else None, "queue_blocker": queue_blocker}, ensure_ascii=False, indent=2)); return 0


def approve(task_id: str, actor: str, note: str) -> int:
    path = TASKS_DIR / f"{task_id}.json"
    if not path.exists(): print(f"Task not found: {task_id}", file=sys.stderr); return 2
    task = load_json(path); gate = task.setdefault("human_gate", {})
    gate.update({"required": True, "status": "approved", "approved_by": actor, "approved_at": utc_now(), "note": note})
    save_json(path, task)
    decision = {"task": task_id, "decision": "approved", "actor": actor, "note": note, "at": utc_now()}
    save_json(DECISIONS_DIR / f"{task_id}-approved.json", decision); audit("human_gate_approved", **decision)
    checkpoint_control_files(f"{task_id}: approve Human Gate"); print(f"Approved {task_id}"); return 0


def nested_finish_reason(value: Any) -> str | None:
    if isinstance(value, dict):
        for key in ("finish_reason", "finishReason", "stop_reason", "stopReason"):
            if value.get(key) is not None: return str(value[key])
        for child in value.values():
            found = nested_finish_reason(child)
            if found: return found
    elif isinstance(value, list):
        for child in value:
            found = nested_finish_reason(child)
            if found: return found
    return None


def parse_cline_finish_reason(log_path: Path) -> str | None:
    reason = None
    for line in log_path.read_text(encoding="utf-8", errors="replace").splitlines():
        try: value = json.loads(line)
        except json.JSONDecodeError: continue
        reason = nested_finish_reason(value) or reason
    return reason


def build_prompt(task: dict[str, Any], attempt: int, previous_error: str) -> str:
    base = (AI_DIR / "prompts" / "developer.md").read_text(encoding="utf-8")
    retry = f"\nPrevious attempt failed:\n{previous_error[-6000:]}\n" if previous_error else ""
    return f"{base}\n\nCurrent task JSON:\n{json.dumps(task, ensure_ascii=False, indent=2)}\n\nAttempt: {attempt}{retry}"


def run_browser_acceptance(task: dict[str, Any]) -> tuple[int, dict[str, Any] | None, str]:
    command = [sys.executable, str(ROOT / "scripts" / "ai_browser_acceptance.py"), "--task", task["id"]]
    completed = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=False)
    stdout = (completed.stdout or b"").decode("utf-8", errors="replace")
    stderr = (completed.stderr or b"").decode("utf-8", errors="replace")
    log_path = LOGS_DIR / f"{task['id']}-browser-acceptance.log"
    log_path.write_text(stdout + stderr, encoding="utf-8")
    manifests = sorted((AI_DIR / "evidence" / task["id"]).glob("*/manifest.json"))
    manifest = load_json(manifests[-1]) if manifests else None
    return completed.returncode, manifest, str(log_path.relative_to(ROOT))


def recover_push_pending(config: dict[str, Any], state: dict[str, Any]) -> int | None:
    if state.get("phase") != "push_pending": return None
    task_id = state.get("current_task") or state.get("last_completed_task")
    audit("push_recovery_started", task=task_id)
    pending_paths = changed_paths()
    non_control_paths = [
        path for path in pending_paths
        if not matches(path, config.get("orchestrator_paths", []))
        and not matches(path, config.get("ignored_change_paths", []))
    ]
    if non_control_paths:
        set_state(
            state,
            blocker="Push recovery stopped because non-control changes are present",
            finish_reason="push_recovery_dirty_worktree",
        )
        audit("push_recovery_blocked", task=task_id, changed_paths=non_control_paths)
        return 5
    if pending_paths:
        checkpoint_control_files("chore: checkpoint pending push recovery state")
    pull = subprocess.run(["git", "pull", "--rebase"], cwd=ROOT)
    if pull.returncode != 0:
        set_state(state, phase="remote_degraded", current_task=None, blocker=None, finish_reason="remote_sync_degraded", remote_sync={"status": "pending", "reason": "pull_failed"})
        audit("remote_sync_degraded", task=task_id, reason="pull_failed")
        checkpoint_control_files("chore: record degraded remote sync")
        return 0
    push = subprocess.run(["git", "push"], cwd=ROOT)
    if push.returncode != 0:
        set_state(state, phase="remote_degraded", current_task=None, blocker=None, finish_reason="remote_sync_degraded", remote_sync={"status": "pending", "reason": "push_failed"})
        audit("remote_sync_degraded", task=task_id, reason="push_failed")
        checkpoint_control_files("chore: record degraded remote sync")
        return 0
    set_state(state, phase="ready", current_task=None, blocker=None, finish_reason="remote_synced")
    audit("push_recovery_completed", task=task_id); checkpoint_control_files("chore: record remote sync recovery")
    subprocess.run(["git", "push"], cwd=ROOT)
    return 0


def run_next(dry_run: bool) -> int:
    config, state = load_json(CONFIG_PATH), load_json(STATE_PATH)
    recovered = recover_push_pending(config, state)
    if recovered is not None: return recovered

    normalized_failed_head = normalize_failed_head_for_deferred_browser(config, state)
    if normalized_failed_head is not None:
        state = load_json(STATE_PATH)

    resume_existing = False
    resume_reason: str | None = None
    item = normalized_failed_head

    if item is not None:
        resume_existing = bool(changed_paths())
        resume_reason = "failed_head_normalized_for_deferred_browser"
    else:
        item = recoverable_path_guard_task(config, state)
        if item is not None:
            resume_existing = True
            resume_reason = "path_guard_recovered"
            audit("path_guard_recovery_started", task=item[1]["id"], changed_paths=changed_paths())
        else:
            item = recoverable_interrupted_task(config, state)
            if item is not None:
                resume_existing = True
                resume_reason = "interrupted_task_recovered"
                audit("interrupted_task_recovery_started", task=item[1]["id"], phase=state.get("phase"), changed_paths=changed_paths())
            else:
                item = recoverable_browser_failure_task(config, state)
                if item is not None:
                    resume_existing = True
                    resume_reason = "browser_failure_recovered"
                    item[1]["browser_recovery_cycles"] = int(item[1].get("browser_recovery_cycles", 0) or 0) + 1
                    item[1]["attempts"] = 0
                    save_json(item[0], item[1])
                    audit("browser_failure_recovery_started", task=item[1]["id"], cycle=item[1]["browser_recovery_cycles"], changed_paths=changed_paths())
                else:
                    item = recoverable_deferred_failed_head(config, state)
                    if item is not None:
                        resume_existing = True
                        resume_reason = "deferred_browser_failed_head_recovered"
                        item[1]["status"] = "retry"
                        item[1]["attempts"] = 0
                        save_json(item[0], item[1])
                        audit("deferred_browser_failed_head_recovery_started", task=item[1]["id"], changed_paths=changed_paths())
                    else:
                        try:
                            item = next_task(config)
                        except ValueError as exc:
                            set_state(state, phase="blocked", blocker=str(exc), finish_reason="queue_head_blocked")
                            audit("queue_head_blocked", reason=str(exc)); return 10
    if item is None: print("No runnable task."); return 0
    task_path, task = item
    try: validate_task(task, config)
    except ValueError as exc:
        set_state(state, phase="blocked", current_task=task.get("id"), blocker=str(exc), finish_reason="invalid_task")
        audit("invalid_task", task=task.get("id"), reason=str(exc)); return 10
    dependencies_ok, dependency_reason = dependencies_completed(task, config)
    if not dependencies_ok:
        set_state(state, phase="blocked", current_task=task["id"], blocker=dependency_reason, finish_reason="dependency_blocked"); return 10
    if not task.get("auto_start", True):
        set_state(state, phase="waiting_human_gate", current_task=task["id"], blocker="auto_start=false"); return 6
    if not gate_is_approved(task):
        set_state(state, phase="waiting_human_gate", current_task=task["id"], blocker=task.get("human_gate", {}).get("reason", "Human Gate approval required"))
        audit("waiting_human_gate", task=task["id"]); return 6
    if config.get("require_git", True) and not git_available(): return 4
    if config.get("require_clean_worktree", True) and changed_paths() and not resume_existing:
        set_state(state, phase="blocked", current_task=task["id"], blocker="Working tree is not clean", finish_reason="dirty_worktree"); return 5
    if dry_run: print(build_prompt(task, 1, "")); return 0

    task["status"] = "in_progress"; save_json(task_path, task)
    if resume_existing:
        set_state(state, phase="developing", current_task=task["id"], blocker=None, finish_reason=resume_reason)
        audit(resume_reason or "existing_work_recovered", task=task["id"], changed_paths=changed_paths())
    else:
        set_state(state, phase="developing", current_task=task["id"], blocker=None, finish_reason=None)
        audit("task_started", task=task["id"])

    previous_error = "Recovered existing local work after an interrupted/guarded run; validate the current working tree before asking Cline to change it again." if resume_existing else ""
    cline_code: int | None = 0 if resume_existing else None
    cline_raw_reason: str | None = "path_guard_recovered_existing_work" if resume_existing else None
    browser_manifest: dict[str, Any] | None = None
    max_attempts = int(task.get("max_attempts", config["max_attempts"]))
    start_attempt = max(1, min(int(task.get("attempts", 0) or 1), max_attempts))
    validate_existing_first = resume_existing

    for attempt in range(start_attempt, max_attempts + 1):
        task["attempts"] = attempt; save_json(task_path, task)
        if validate_existing_first:
            validate_existing_first = False
            audit("path_guard_recovery_validation_started", task=task["id"], attempt=attempt)
        else:
            log_path = LOGS_DIR / f"{task['id']}-attempt-{attempt}.jsonl"; LOGS_DIR.mkdir(parents=True, exist_ok=True)
            provider = os.environ.get("AI_CLINE_PROVIDER", "deepseek")
            model = os.environ.get("AI_CLINE_MODEL", "deepseek-v4.1-flash")
            command = [
                config["cline_command"],
                "--json",
                "--auto-approve", "true",
                "--provider", provider,
                "--model", model,
                "--cwd", str(ROOT),
                "--timeout", str(config["cline_timeout_seconds"]),
                build_prompt(task, attempt, previous_error),
            ]
            with log_path.open("w", encoding="utf-8") as log:
                cline_code = subprocess.run(command, cwd=ROOT, text=True, stdout=log, stderr=subprocess.STDOUT).returncode
            cline_raw_reason = parse_cline_finish_reason(log_path)
            violations = path_violations(task, config)
            if violations:
                previous_error = "Path guard failed:\n" + "\n".join(violations); audit("path_guard_failed", task=task["id"], violations=violations); break
            if cline_code != 0:
                previous_error = f"Cline exited with code {cline_code}; raw reason={cline_raw_reason}"; audit("cline_failed", task=task["id"], attempt=attempt); continue

        validation = subprocess.run([sys.executable, str(ROOT / "scripts" / "ai_validate.py"), "--profile", task["validation_profile"], "--task", task["id"]], cwd=ROOT)
        if validation.returncode != 0:
            previous_error = f"Engineering validation failed with code {validation.returncode}."; audit("validation_failed", task=task["id"], attempt=attempt); continue
        task["status"] = "code_ready"; save_json(task_path, task)
        completion_mode = task.get("completion_mode", config.get("completion_policy", {}).get("default_mode", "browser"))
        browser_deferred = completion_mode == "browser" and browser_acceptance_is_deferred(config)
        if browser_deferred:
            browser_manifest = {
                "status": "deferred",
                "reason": "Real-browser/UI acceptance is deferred until FINAL-UI-ACCEPTANCE.",
                "deferred_at": utc_now(),
            }
            set_state(
                state,
                phase="developing",
                current_task=task["id"],
                browser_acceptance={"status": "deferred"},
                finish_reason="core_validated_browser_deferred",
            )
            audit("browser_acceptance_deferred", task=task["id"])
        else:
            set_state(state, phase="browser_acceptance", current_task=task["id"], finish_reason="code_ready_not_complete")

        if completion_mode == "browser" and not browser_deferred:
            browser_code, browser_manifest, browser_log = run_browser_acceptance(task)
            if browser_code == 20:
                task["status"] = "blocked"; save_json(task_path, task)
                set_state(state, phase="blocked", blocker=f"Browser infrastructure blocked; see {browser_log}", finish_reason="browser_infrastructure_blocked")
                audit("browser_infrastructure_blocked", task=task["id"], log=browser_log); return 20
            if browser_code != 0:
                task["status"] = "in_progress"; save_json(task_path, task)
                log_text = ""
                browser_log_path = ROOT / browser_log
                if browser_log_path.exists():
                    log_text = browser_log_path.read_text(encoding="utf-8", errors="replace")[-7000:]
                previous_error = (
                    f"Real-browser acceptance failed with code {browser_code}; see {browser_log}. "
                    "Cline output alone is not completion.\n\n"
                    f"Browser acceptance log tail:\n{log_text}"
                )
                audit("browser_acceptance_failed", task=task["id"], attempt=attempt, manifest=browser_manifest); continue

        business_changes = [path for path in changed_paths() if not matches(path, config["ignored_change_paths"]) and not matches(path, config.get("orchestrator_paths", []))]
        if not business_changes:
            previous_error = "Task produced no checkpointable business changes."; task["status"] = "in_progress"; save_json(task_path, task); continue
        task["status"] = "completed"; save_json(task_path, task)
        normalized = (
            "browser_deferred"
            if completion_mode == "browser" and browser_deferred
            else ("browser_accepted" if completion_mode == "browser" else "control_plane_validated")
        )
        result = {
            "task": task["id"], "status": "completed", "execution_outcome": "completed",
            "normalized_finish_reason": normalized, "cline_exit_code": cline_code,
            "cline_finish_reason_raw": cline_raw_reason, "attempts": attempt,
            "browser_acceptance": browser_manifest, "finished_at": utc_now()
        }
        save_json(RESULTS_DIR / f"{task['id']}.json", result)
        set_state(state, phase="ready", current_task=None, last_completed_task=task["id"], validation={"profile": task["validation_profile"], "status": "passed"}, browser_acceptance={"status": normalized}, cline_exit_code=cline_code, finish_reason=normalized)
        audit("task_completed", **result)
        checkpoint_paths = [path for path in changed_paths() if not matches(path, config["ignored_change_paths"])]
        subprocess.run(["git", "add", "--", *checkpoint_paths], cwd=ROOT, check=True)
        if subprocess.run(["git", "commit", "-m", f"{task['id']}: {task['title']}"], cwd=ROOT).returncode != 0: return 8
        if config.get("auto_push", False) and subprocess.run(["git", "push"], cwd=ROOT).returncode != 0:
            set_state(
                state,
                phase="remote_degraded",
                current_task=None,
                blocker=None,
                finish_reason="remote_sync_degraded",
                remote_sync={"status": "pending", "task": task["id"]},
            )
            audit("remote_sync_degraded", task=task["id"], reason="push_failed")
            checkpoint_control_files("chore: record degraded remote sync")
            print(f"Completed {task['id']} locally; remote push is degraded and will be retried later.")
            return 0
        print(f"Completed {task['id']} by {normalized}"); return 0

    quarantine = subprocess.run(
        ["git", "stash", "push", "-u", "-m", f"autonomy quarantine {task['id']}"],
        cwd=ROOT, capture_output=True, text=True,
    )
    if quarantine.returncode != 0:
        task["status"] = "failed"; save_json(task_path, task)
        set_state(state, phase="human_attention", current_task=task["id"], blocker="Failed task could not be quarantined safely", cline_exit_code=cline_code, finish_reason="quarantine_failed")
        audit("task_quarantine_failed", task=task["id"], reason=previous_error, git_error=quarantine.stderr.strip())
        return 7
    task["status"] = "failed"; save_json(task_path, task)
    set_state(
        state,
        phase="ready",
        current_task=None,
        blocker=None,
        cline_exit_code=cline_code,
        finish_reason="task_quarantined",
        last_failed_task=task["id"],
    )
    audit("task_quarantined", task=task["id"], reason=previous_error, cline_finish_reason_raw=cline_raw_reason, stash=quarantine.stdout.strip())
    checkpoint_control_files(f"{task['id']}: quarantine failed task state")
    print(f"Quarantined {task['id']}; continuing with dependency-safe work.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="NEWERP guarded task orchestrator with deferrable browser acceptance")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status"); runner = sub.add_parser("run-next"); runner.add_argument("--dry-run", action="store_true")
    approval = sub.add_parser("approve"); approval.add_argument("task_id"); approval.add_argument("--by", required=True); approval.add_argument("--note", required=True)
    args = parser.parse_args()
    if args.command == "status": return status()
    if args.command == "approve": return approve(args.task_id, args.by, args.note)
    return run_next(args.dry_run)


if __name__ == "__main__":
    raise SystemExit(main())
