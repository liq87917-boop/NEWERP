#!/usr/bin/env python3
"""Single-task NEWERP executor with bounded DeepSeek repair and build-first delivery."""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from contextlib import contextmanager
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ai_state import refresh_project_state, save_json as _save_json
from ai_console_panel import show_console_panel
from ai_provider_availability import next_retry, provider_failure_from_log

ROOT = Path(__file__).resolve().parents[1]
AI_DIR = ROOT / ".ai"
TASKS_DIR, RESULTS_DIR = AI_DIR / "tasks", AI_DIR / "results"
LOGS_DIR, DECISIONS_DIR = AI_DIR / "logs", AI_DIR / "decisions"
CONFIG_PATH, STATE_PATH = AI_DIR / "config.json", AI_DIR / "PROJECT_STATE.json"
AUDIT_PATH = AI_DIR / "audit.jsonl"
TERMINAL_STATUSES = {"completed", "deferred", "skipped", "superseded"}
RECOVERABLE_DIRTY_STATUSES = {"retry", "retry_pending", "in_progress", "code_ready", "finalizing", "failed", "blocked"}



def refresh_task_scope(task: dict[str, Any], path: Path | None = None) -> None:
    """Retain current published scope while the executor owns runtime fields."""
    task_id = str(task.get("id", ""))
    if not task_id.startswith("ERP-") or not task_id[4:].isdigit():
        return
    path = path or TASKS_DIR / f"{task_id}.json"
    if path.parent != TASKS_DIR or not path.exists():
        return
    latest = load_json(path)
    if latest.get("id") != task_id:
        raise ValueError("Published task identity changed")
    allowed = latest.get("allowed_paths")
    if not isinstance(allowed, list) or not allowed or not all(isinstance(x, str) and x for x in allowed):
        raise ValueError("Published task scope is invalid")
    for key in ("title", "description", "allowed_paths", "acceptance_criteria",
                "criterion_path_map", "implementation_evidence", "updated_at"):
        if key in latest:
            task[key] = latest[key]


def save_json(path: Path, value: dict[str, Any]) -> None:
    # A long running model may finish after an authorized scope amendment.
    # Do not overwrite that amendment with the model's initial task snapshot.
    if path.parent == TASKS_DIR:
        refresh_task_scope(value, path)
    _save_json(path, value)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


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
    result = None
    for retry in range(3):
        result = subprocess.run(
            ["git", "-c", "core.quotepath=false", *args],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=False,
        )
        if result.returncode == 0:
            break
        time.sleep(0.1 * (retry + 1))
    assert result is not None
    stdout = (result.stdout or b"").decode("utf-8", errors="replace")
    stderr = (result.stderr or b"").decode("utf-8", errors="replace")
    if result.returncode != 0:
        raise RuntimeError(stderr.strip() or f"Git command failed with code {result.returncode}: {' '.join(args)}")
    return [line.strip().replace("\\", "/") for line in stdout.splitlines() if line.strip()]


def changed_paths() -> list[str]:
    return sorted(set(git_lines("diff", "--name-only") + git_lines("diff", "--cached", "--name-only") + git_lines("ls-files", "--others", "--exclude-standard")))


def business_changed_paths(config: dict[str, Any]) -> list[str]:
    return [
        path for path in changed_paths()
        if not matches(path, config["ignored_change_paths"])
        and not matches(path, config.get("orchestrator_paths", []))
    ]


def write_recovery_diff(task_id: str) -> tuple[str, list[str]]:
    """Persist a bounded, inspectable snapshot without moving or discarding work."""
    LOGS_DIR.mkdir(parents=True, exist_ok=True)
    paths = changed_paths()
    tracked = subprocess.run(
        ["git", "diff", "--binary", "HEAD"], cwd=ROOT,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=False,
    )
    tracked_text = (tracked.stdout or b"").decode("utf-8", errors="replace")
    untracked = git_lines("ls-files", "--others", "--exclude-standard")
    payload = tracked_text
    if untracked:
        payload += "\n# Untracked files preserved in the execution copy\n" + "\n".join(untracked) + "\n"
    path = LOGS_DIR / f"{task_id}-preserved-work.diff"
    path.write_text(payload[-500000:], encoding="utf-8")
    return str(path.relative_to(ROOT)), paths


def matches(path: str, patterns: list[str]) -> bool:
    normalized = path.replace("\\", "/")
    return any(fnmatch.fnmatch(normalized, pattern) for pattern in patterns)


def auto_push_enabled(config: dict[str, Any]) -> bool:
    return config.get("auto_push", False) and os.environ.get("AI_DISABLE_PUSH") != "1"


def all_tasks(config: dict[str, Any]) -> list[tuple[Path, dict[str, Any]]]:
    return [(path, load_json(path)) for path in sorted(TASKS_DIR.glob(f"{config['task_prefix']}-*.json"))]


def completed_result(task_id: str) -> dict[str, Any] | None:
    path = RESULTS_DIR / f"{task_id}.json"
    if not path.exists():
        return None
    result = load_json(path)
    return result if result.get("status") == "completed" else None


def honor_terminal_completion(task_path: Path, task: dict[str, Any], state: dict[str, Any]) -> bool:
    """A committed completion result is a terminal fence against stale workers."""
    result = completed_result(str(task.get("id")))
    if result is None:
        return False
    task["status"] = "completed"
    for key in ("blocker", "last_error", "failure_kind", "failed_transport_version", "recovery_context", "provider_retry", "preserved_work", "quarantine"):
        task.pop(key, None)
    save_json(task_path, task)
    set_state(state, phase="ready", current_task=None, last_error=None, blocker=None, finish_reason="terminal_completion_reconciled")
    audit("terminal_completion_reconciled", task=task["id"], result=result)
    return True


class ActiveExecution(ValueError):
    """A concurrent launcher must yield without replacing the running worker state."""


def next_task(config: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Select a dependency-safe task; failed/gated/blocked tasks do not globally stop work."""
    for path, task in all_tasks(config):
        status = task.get("status")
        if status in TERMINAL_STATUSES:
            continue
        if status in {"in_progress", "code_ready"}:
            raise ActiveExecution(f"{task.get('id')} status={status} is an active execution")
        if status not in {"pending", "retry"}:
            continue
        dependencies_ok, _ = dependencies_completed(task, config)
        if not dependencies_ok:
            continue
        if not task.get("auto_start", True):
            continue
        if not gate_is_approved(task, config):
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
    if mode not in {"build", "browser", "control_plane"}: raise ValueError("completion_mode must be build, browser or control_plane")
    if mode == "browser" and not task.get("browser_acceptance", {}).get("scenarios"):
        raise ValueError("Browser-completed tasks require browser_acceptance.scenarios")


def browser_acceptance_is_deferred(config: dict[str, Any]) -> bool:
    """Return True while feature development intentionally defers real-browser/UI acceptance."""
    return bool(config.get("completion_policy", {}).get("defer_browser_during_development", False))


def gate_is_approved(task: dict[str, Any], config: dict[str, Any] | None = None) -> bool:
    if config is not None and not config.get("human_gate", True):
        return True
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
    refresh_task_scope(task)
    violations = []
    recovery = task.get("recovery_context") or {}
    baseline = recovery.get("baseline_test_fix") or {}
    repair_paths = recovery.get("repair_allowed_paths") or []
    if (baseline.get("baseline_confirmed") is True
            and repair_paths == [baseline.get("path")]
            and str(baseline.get("path", "")).startswith("src/ERP.UnitTests/")
            and str(baseline.get("path", "")).endswith("Tests.cs")):
        allowed = [*task["allowed_paths"], *repair_paths]
    else:
        allowed = task["allowed_paths"]
    for path in changed_paths():
        if matches(path, config["ignored_change_paths"]) or matches(path, config.get("orchestrator_paths", [])): continue
        if not matches(path, allowed): violations.append(f"outside allowed_paths: {path}")
        if matches(path, config["protected_paths"]) and not gate_is_approved(task, config): violations.append(f"protected without approved gate: {path}")
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


def checkpoint_content_signature(config: dict[str, Any]) -> str:
    digest = hashlib.sha256()
    for path in business_changed_paths(config):
        digest.update(path.encode())
        file = ROOT / path
        digest.update(file.read_bytes() if file.is_file() else b"<deleted>")
    return digest.hexdigest()


def recoverable_completed_checkpoint(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Recover only the validated task whose completion file is still uncommitted."""
    build = state.get("last_build") or {}
    task_id = build.get("task")
    if build.get("status") != "passed" or build.get("exit_code") != 0 or not task_id:
        return None
    path = TASKS_DIR / f"{task_id}.json"
    result_path = RESULTS_DIR / f"{task_id}.json"
    if not path.exists() or not result_path.exists():
        return None
    task, result = load_json(path), load_json(result_path)
    if task.get("status") != "completed" or result.get("status") != "completed" or result.get("task") != task_id:
        return None
    dirty = changed_paths()
    # Legacy runs lack a manifest: require uncommitted task completion evidence.
    if f".ai/tasks/{task_id}.json" not in dirty or not business_changed_paths(config):
        return None
    try:
        validate_task(task, config)
    except ValueError:
        return None
    if path_violations(task, config) or not gate_is_approved(task, config):
        return None
    return path, task


def recover_completed_checkpoint(config: dict[str, Any], state: dict[str, Any]) -> int | None:
    item = recoverable_completed_checkpoint(config, state)
    if item is None:
        return None
    _, task = item
    task_id = task["id"]
    signature = checkpoint_content_signature(config)
    prior = task.get("checkpoint_recovery") or state.get("checkpoint_recovery") or {}
    cycles = int(prior.get("cycles", 0)) if prior.get("signature") == signature else 0
    maximum = int(config.get("autonomy", {}).get("max_supervised_recovery_cycles", 2))
    if cycles >= maximum:
        return 8
    task["checkpoint_recovery"] = {"task": task_id, "signature": signature, "cycles": cycles + 1}
    save_json(TASKS_DIR / f"{task_id}.json", task)
    audit("completed_checkpoint_recovery_started", task=task_id)
    # Revalidate the current content; historical success cannot authorize new edits.
    code, log, summary = run_validation(task, "checkpoint-recovery")
    if code or path_violations(task, config) or checkpoint_content_signature(config) != signature:
        set_state(state, phase="blocked", blocker="Completed checkpoint revalidation failed",
                  last_error={"task": task_id, "kind": "checkpoint_revalidation", "log": log, "summary": summary})
        return 8
    paths = [p for p in changed_paths() if not matches(p, config["ignored_change_paths"])]
    staged = git_lines("diff", "--cached", "--name-only")
    if any(p not in paths for p in staged):
        raise RuntimeError("Unrelated staged changes prevent completed checkpoint recovery")
    for attempt in range(3):
        added = run(["git", "add", "--", *paths])
        committed = run(["git", "commit", "-m", f"{task_id}: recover validated checkpoint"]) if added.returncode == 0 else added
        if committed.returncode == 0:
            set_state(state, phase="ready", current_task=None, blocker=None, last_error=None,
                      finish_reason="completed_checkpoint_recovered")
            audit("completed_checkpoint_recovered", task=task_id, log=log)
            checkpoint_control_files("chore: record completed checkpoint recovery")
            return 0
        time.sleep(0.2 * (attempt + 1))
    set_state(state, phase="blocked", blocker="Validated checkpoint commit retry exhausted",
              last_error={"task": task_id, "kind": "checkpoint_commit", "summary": (committed.stderr or committed.stdout)[-4000:]})
    return 8


def recoverable_dirty_task(config: dict[str, Any], state: dict[str, Any]) -> tuple[Path, dict[str, Any]] | None:
    """Find the owner of preserved dirty executor work after a scheduler interruption."""
    current = state.get("current_task")
    entries = all_tasks(config)
    entries.sort(key=lambda entry: (entry[1].get("id") != current, entry[0].name))
    current_business = set(business_changed_paths(config))
    if not current_business:
        return None
    revision = run(["git", "rev-parse", "HEAD:src"]).stdout.strip()
    maximum = int(config.get("autonomy", {}).get("max_supervised_recovery_cycles", 2))
    for path, task in entries:
        if task.get("status") not in RECOVERABLE_DIRTY_STATUSES:
            continue
        if (task.get("status") in {"retry_pending", "failed", "blocked"}
                and int(task.get("supervised_recovery_cycles", 0) or 0) >= maximum
                and task.get("exhausted_revalidation_source_tree") == revision):
            continue
        preserved = task.get("preserved_work") or {}
        expected = set(preserved.get("changed_paths") or [])
        recovery = task.get("recovery_context") or {}
        baseline = recovery.get("baseline_test_fix") or {}
        if baseline.get("baseline_confirmed") is True:
            expected.update(recovery.get("repair_allowed_paths") or [])
        if task.get("id") != current and not expected:
            continue
        if expected and not current_business.issubset(expected):
            continue
        try:
            validate_task(task, config)
        except ValueError:
            continue
        return path, task
    return None


def preserve_failed_work(
    task_path: Path,
    task: dict[str, Any],
    config: dict[str, Any],
    state: dict[str, Any],
    previous_error: str,
    failure_kind: str | None,
    cline_raw_reason: str | None,
    max_attempts: int,
) -> int:
    """Keep failed work in place and make it first-class recovery input."""
    diff_path, paths = write_recovery_diff(task["id"])
    task["status"] = "retry_pending"
    task["blocker"] = "Automatic repair cycle pending"
    task["last_error"] = previous_error[-12000:]
    task["preserved_work"] = {
        "execution_copy": str(ROOT),
        "changed_paths": paths,
        "diff": diff_path,
        "preserved_at": utc_now(),
    }
    if failure_kind:
        task["failure_kind"] = failure_kind
        task["failed_transport_version"] = int(config.get("pipeline", {}).get("prompt_transport_version", 1))
    task.pop("quarantine", None)
    save_json(task_path, task)
    result = {
        "task": task["id"], "status": "retry_pending", "execution_outcome": "automatic_repair_pending",
        "attempts": max_attempts, "last_error": previous_error[-12000:], "failure_kind": failure_kind,
        "attempted_fix": {"provider": "deepseek", "model": os.environ.get("AI_CLINE_MODEL", "deepseek-v4-pro")},
        "preserved_work": task["preserved_work"], "finished_at": utc_now(),
    }
    save_json(RESULTS_DIR / f"{task['id']}.json", result)
    set_state(
        state, phase="ready", current_task=None,
        last_error={"task": task["id"], "kind": "automatic_repair_pending", "summary": previous_error[-12000:]},
        last_deepseek_fix={"task": task["id"], "attempts": max_attempts, "status": "retry_pending", "at": utc_now()},
        finish_reason="failed_work_preserved_for_automatic_repair",
    )
    audit(
        "failed_work_preserved", task=task["id"], reason=previous_error,
        cline_finish_reason_raw=cline_raw_reason, changed_paths=paths, diff=diff_path,
    )
    print(f"Preserved {task['id']} work for automatic repair; no stash or reset was used.")
    return 0


def preserve_provider_unavailable(
    task_path: Path, task: dict[str, Any], config: dict[str, Any],
    state: dict[str, Any], message: str, log_path: Path, attempt: int,
) -> int:
    """Keep the task queued for a timed provider probe without using repair budget."""
    retry = next_retry(task.get("provider_retry"), config.get("autonomy", {}))
    diff_path, paths = write_recovery_diff(task["id"])
    task.update({
        "status": "retry_pending", "attempts": 0,
        "failure_kind": "provider_unavailable",
        "blocker": "Model provider quota unavailable; timed retry pending",
        "last_error": message[-12000:], "provider_retry": retry,
        "preserved_work": {
            "execution_copy": str(ROOT), "changed_paths": paths,
            "diff": diff_path, "preserved_at": utc_now(),
        },
    })
    task.pop("recovery_context", None)
    save_json(task_path, task)
    save_json(RESULTS_DIR / f"{task['id']}.json", {
        "task": task["id"], "status": "retry_pending",
        "execution_outcome": "provider_unavailable", "failure_kind": "provider_unavailable",
        "attempt": attempt, "last_error": message[-12000:],
        "executor_log": str(log_path.relative_to(ROOT)),
        "provider_retry": retry, "preserved_work": task["preserved_work"],
        "finished_at": utc_now(),
    })
    set_state(state, phase="ready", current_task=None, blocker=None,
              last_error={"task": task["id"], "kind": "provider_unavailable",
                          "summary": message[-12000:], "log": str(log_path.relative_to(ROOT)),
                          "next_probe_at": retry["next_probe_at"]},
              finish_reason="provider_retry_scheduled")
    audit("provider_retry_scheduled", task=task["id"], attempt=attempt,
          log=str(log_path.relative_to(ROOT)), retry=retry)
    print(f"Model provider unavailable for {task['id']}; next probe at {retry['next_probe_at']}.")
    return 0


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
    state.clear()
    state.update(refresh_project_state(ROOT, **updates))


def checkpoint_control_files(message: str) -> None:
    if not git_available(): return
    config = load_json(CONFIG_PATH); paths = changed_paths()
    unexpected = [path for path in paths if not matches(path, config["ignored_change_paths"]) and not matches(path, config.get("orchestrator_paths", []))]
    if unexpected: raise RuntimeError("Unrelated changes prevent control checkpoint: " + ", ".join(unexpected))
    control = [path for path in paths if matches(path, config.get("orchestrator_paths", []))]
    if not control: return
    for attempt in range(3):
        added = subprocess.run(["git", "add", "--", *control], cwd=ROOT, capture_output=True, text=True)
        if added.returncode == 0: break
        if "index.lock" not in added.stderr or attempt == 2:
            raise RuntimeError("Control checkpoint add failed: " + added.stderr.strip())
        time.sleep(0.2 * (attempt + 1))
    staged = git_lines("diff", "--cached", "--name-only")
    outside = [path for path in staged if not matches(path, config.get("orchestrator_paths", []))]
    if outside: raise RuntimeError("Unrelated staged changes prevent control checkpoint: " + ", ".join(outside))
    for attempt in range(3):
        committed = subprocess.run(["git", "commit", "-m", message], cwd=ROOT, capture_output=True, text=True)
        if committed.returncode == 0: return
        if "index.lock" not in committed.stderr or attempt == 2:
            raise RuntimeError("Control checkpoint failed: " + committed.stderr.strip())
        time.sleep(0.2 * (attempt + 1))


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


def executor_requested_missing_task(log_path: Path) -> bool:
    """Recognize the no-op response caused by a truncated task prompt."""
    if not log_path.exists():
        return False
    text = log_path.read_text(encoding="utf-8", errors="replace")[-24000:].lower()
    markers = (
        "describe what you'd like me to build",
        "describe the specific feature or bug fix",
        "paste the full task json",
        "still need the specifics",
        "still need the details",
    )
    return any(marker in text for marker in markers)


def build_prompt(task: dict[str, Any], attempt: int, previous_error: str) -> str:
    base = (AI_DIR / "prompts" / "developer.md").read_text(encoding="utf-8")
    retry = f"\nPrevious attempt failed. The complete validation log remains at the path in this summary:\n{previous_error[-12000:]}\n" if previous_error else ""
    return f"{base}\n\nCurrent task JSON:\n{json.dumps(task, ensure_ascii=False, indent=2)}\n\nAttempt: {attempt}{retry}"


def resolve_cline_command(command: str) -> str:
    """Bypass npm batch wrappers that truncate multiline positional prompts on Windows."""
    configured = Path(command)
    if os.name != "nt" or configured.suffix.lower() not in {".cmd", ".bat"}:
        return command

    npm_root = configured.parent
    candidates = [npm_root / "node_modules" / "cline" / "bin" / ".cline"]
    candidates.extend(sorted(
        (npm_root / "node_modules" / "cline" / "node_modules" / "@cline").glob(
            "cli-windows-*/bin/cline.exe"
        )
    ))
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)

    raise FileNotFoundError(
        f"Configured Cline wrapper {configured} cannot safely carry multiline prompts, "
        "and no platform cline.exe was found under its npm installation."
    )


def run_validation(task: dict[str, Any], attempt: int) -> tuple[int, str, str]:
    """Run validation, persist the complete log, and return a bounded repair summary."""
    LOGS_DIR.mkdir(parents=True, exist_ok=True)
    log_path = LOGS_DIR / f"{task['id']}-validation-{attempt}.log"
    completed = subprocess.run(
        [sys.executable, str(ROOT / "scripts" / "ai_validate.py"), "--profile", task["validation_profile"], "--task", task["id"]],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=False,
    )
    output = (completed.stdout or b"").decode("utf-8", errors="replace")
    log_path.write_text(output, encoding="utf-8")
    meaningful = [line.rstrip() for line in output.splitlines() if line.strip()]
    summary = "\n".join(meaningful[-160:])[-12000:]
    try:
        log_reference = str(log_path.relative_to(ROOT))
    except ValueError:
        log_reference = str(log_path)
    return completed.returncode, log_reference, summary


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
    branch = run(["git", "branch", "--show-current"]).stdout.strip()
    fetch = subprocess.run(["git", "fetch", "origin", branch], cwd=ROOT)
    if fetch.returncode != 0:
        set_state(state, phase="remote_degraded", current_task=None, blocker=None, finish_reason="remote_sync_degraded", remote_sync={"status": "pending", "reason": "pull_failed"})
        audit("remote_sync_degraded", task=task_id, reason="pull_failed")
        checkpoint_control_files("chore: record degraded remote sync")
        return 0
    behind = subprocess.run(["git", "merge-base", "--is-ancestor", "HEAD", f"origin/{branch}"], cwd=ROOT)
    if behind.returncode == 0:
        merged = subprocess.run(["git", "merge", "--ff-only", f"origin/{branch}"], cwd=ROOT)
        if merged.returncode != 0:
            set_state(state, phase="remote_degraded", current_task=None, blocker=None, finish_reason="remote_sync_degraded", remote_sync={"status": "pending", "reason": "fast_forward_failed"})
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


@contextmanager
def execution_lease():
    """Serialize direct host and pipeline launches, including dirty recovery.

    A separate lock avoids recursively acquiring the pipeline parent's lock.
    The OS releases it on process exit so interrupted work remains resumable.
    """
    lock_path = LOGS_DIR / "orchestrator-execution.lock"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a+b") as stream:
        stream.seek(0, os.SEEK_END)
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            yield False
            return
        try:
            yield True
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def legacy_executor_is_running() -> bool:
    """Bridge rollout to processes started before execution leases existed."""
    if os.name != "nt":
        return False
    # Read only PIDs; command lines stay inside PowerShell, never enter logs.
    script = ("$needle = $env:NEWERP_EXECUTOR_SCRIPT; "
              "Get-CimInstance Win32_Process -ErrorAction Stop | "
              "Where-Object { $_.Name -eq 'python.exe' -and "
              f"$_.ProcessId -ne {os.getpid()} -and "
              "$_.CommandLine -and $_.CommandLine.Contains($needle) -and "
              "$_.CommandLine -match 'run-next' } | "
              "Select-Object -ExpandProperty ProcessId")
    env = os.environ.copy()
    env["NEWERP_EXECUTOR_SCRIPT"] = str(Path(__file__).resolve())
    result = subprocess.run(["powershell.exe", "-NoLogo", "-NoProfile", "-Command", script],
                            env=env, capture_output=True, text=True, timeout=20)
    if result.returncode != 0:
        raise RuntimeError("Cannot verify existing NEWERP task executor ownership.")
    return bool(result.stdout.strip())


def run_next(dry_run: bool) -> int:
    with execution_lease() as acquired:
        if not acquired:
            print("Another NEWERP task executor owns the execution lease.")
            return 0
        if legacy_executor_is_running():
            print("An existing NEWERP task executor is still running.")
            return 0
        return run_next_owned(dry_run)


def run_next_owned(dry_run: bool) -> int:
    config, state = load_json(CONFIG_PATH), load_json(STATE_PATH)
    if not dry_run:
        checkpoint = recover_completed_checkpoint(config, state)
        if checkpoint is not None: return checkpoint
    recovered = recover_push_pending(config, state)
    if recovered is not None: return recovered
    # A failed remote push or transient Git lock may leave only scheduler state
    # dirty. Checkpoint that state so the next independent task can still run.
    pending = changed_paths()
    if not dry_run and pending and all(
        matches(path, config.get("orchestrator_paths", [])) or matches(path, config["ignored_change_paths"])
        for path in pending
    ):
        checkpoint_control_files("chore: resume after interrupted control checkpoint")
        state = load_json(STATE_PATH)

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
        item = recoverable_dirty_task(config, state)
        if item is not None:
            resume_existing = True
            resume_reason = "dirty_execution_copy_recovered"
            audit("dirty_execution_copy_recovery_started", task=item[1]["id"], changed_paths=changed_paths())
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
                            except ActiveExecution as exc:
                                # The task owner continues running. A duplicate launcher is
                                # neither a task failure nor permission to overwrite its state.
                                audit("queue_active_execution", reason=str(exc))
                                return 0
                            except ValueError as exc:
                                set_state(state, phase="blocked", blocker=str(exc), finish_reason="queue_head_blocked")
                                audit("queue_head_blocked", reason=str(exc)); return 10
    if item is None: print("No runnable task."); return 0
    task_path, task = item
    if honor_terminal_completion(task_path, task, state):
        print(f"Ignored stale execution for already completed {task['id']}.")
        return 0
    try: validate_task(task, config)
    except ValueError as exc:
        set_state(state, phase="blocked", current_task=task.get("id"), blocker=str(exc), finish_reason="invalid_task")
        audit("invalid_task", task=task.get("id"), reason=str(exc)); return 10
    dependencies_ok, dependency_reason = dependencies_completed(task, config)
    if not dependencies_ok:
        set_state(state, phase="blocked", current_task=task["id"], blocker=dependency_reason, finish_reason="dependency_blocked"); return 10
    if not task.get("auto_start", True):
        set_state(state, phase="waiting_human_gate", current_task=task["id"], blocker="auto_start=false"); return 6
    if not gate_is_approved(task, config):
        set_state(state, phase="waiting_human_gate", current_task=task["id"], blocker=task.get("human_gate", {}).get("reason", "Human Gate approval required"))
        audit("waiting_human_gate", task=task["id"]); return 6
    if config.get("require_git", True) and not git_available(): return 4
    if config.get("require_clean_worktree", True) and changed_paths() and not resume_existing:
        set_state(state, phase="blocked", current_task=task["id"], blocker="Working tree is not clean", finish_reason="dirty_worktree"); return 5
    if dry_run: print(build_prompt(task, 1, "")); return 0
    if (resume_reason == "dirty_execution_copy_recovered"
            and task.get("status") in {"retry_pending", "failed", "blocked"}
            and int(task.get("supervised_recovery_cycles", 0) or 0)
            >= int(config.get("autonomy", {}).get("max_supervised_recovery_cycles", 2))):
        # At the repair limit, permit one validation of preserved work per base
        # source revision. A baseline test fix can unblock it without another
        # DeepSeek cycle; task-queue commits cannot trigger repeated validation.
        task["exhausted_revalidation_source_tree"] = run(["git", "rev-parse", "HEAD:src"]).stdout.strip()
        save_json(task_path, task)
    cline_command = resolve_cline_command(config["cline_command"])

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
    failure_kind: str | None = None
    last_executor_log: Path | None = None
    max_attempts = int(task.get("max_attempts", config["max_attempts"]))
    start_attempt = max(1, min(int(task.get("attempts", 0) or 1), max_attempts))
    validate_existing_first = resume_existing

    for attempt in range(start_attempt, max_attempts + 1):
        if honor_terminal_completion(task_path, task, state):
            print(f"Stopped stale worker for already completed {task['id']}.")
            return 0
        task["attempts"] = attempt; save_json(task_path, task)
        panel = show_console_panel(ROOT)
        audit("console_panel_requested", task=task["id"], attempt=attempt, **panel)
        if validate_existing_first:
            validate_existing_first = False
            audit("path_guard_recovery_validation_started", task=task["id"], attempt=attempt)
            violations = path_violations(task, config)
            if violations:
                failure_kind = "path_guard_failure"
                previous_error = "Path guard failed:\n" + "\n".join(violations)
                set_state(state, phase="repairing", current_task=task["id"], last_error={"task": task["id"], "attempt": attempt, "kind": "path_guard", "summary": previous_error})
                audit("path_guard_failed", task=task["id"], violations=violations)
                continue
        else:
            log_path = LOGS_DIR / f"{task['id']}-attempt-{attempt}.jsonl"; LOGS_DIR.mkdir(parents=True, exist_ok=True)
            last_executor_log = log_path
            provider = os.environ.get("AI_CLINE_PROVIDER", "deepseek")
            model = os.environ.get("AI_CLINE_MODEL", "deepseek-v4-pro")
            command = [
                cline_command,
                "--json",
                "--auto-approve", "true",
                "--provider", provider,
                "--model", model,
                "--cwd", str(ROOT),
                "--timeout", str(config["cline_timeout_seconds"]),
                build_prompt(task, attempt, previous_error),
            ]
            if previous_error:
                set_state(
                    state,
                    phase="repairing",
                    current_task=task["id"],
                    last_deepseek_fix={"task": task["id"], "attempt": attempt, "model": model, "started_at": utc_now()},
                    last_error={"task": task["id"], "attempt": attempt - 1, "summary": previous_error[-12000:]},
                )
            with log_path.open("w", encoding="utf-8") as log:
                cline_code = subprocess.run(command, cwd=ROOT, text=True, stdout=log, stderr=subprocess.STDOUT).returncode
            cline_raw_reason = parse_cline_finish_reason(log_path)
            if previous_error or attempt > 1:
                set_state(
                    state,
                    phase="developing",
                    current_task=task["id"],
                    last_deepseek_fix={"task": task["id"], "attempt": attempt, "model": model, "exit_code": cline_code, "finished_at": utc_now()},
                )
            violations = path_violations(task, config)
            if violations:
                failure_kind = "path_guard_failure"
                previous_error = "Path guard failed:\n" + "\n".join(violations); audit("path_guard_failed", task=task["id"], violations=violations); break
            provider_error = provider_failure_from_log(log_path)
            if provider_error:
                return preserve_provider_unavailable(task_path, task, config, state,
                                                     provider_error, log_path, attempt)
            if cline_code != 0:
                failure_kind = "executor_failure"
                cline_tail = log_path.read_text(encoding="utf-8", errors="replace")[-12000:]
                previous_error = f"DeepSeek executor exited with code {cline_code}; raw reason={cline_raw_reason}.\nLog tail:\n{cline_tail}"
                set_state(state, phase="repairing", current_task=task["id"], last_error={"task": task["id"], "attempt": attempt, "kind": "deepseek", "summary": previous_error})
                audit("cline_failed", task=task["id"], attempt=attempt); continue

        validation_code, validation_log, validation_summary = run_validation(task, attempt)
        build_record = {"task": task["id"], "profile": task["validation_profile"], "status": "passed" if validation_code == 0 else "failed", "exit_code": validation_code, "log": validation_log, "at": utc_now()}
        if validation_code != 0:
            failure_kind = "validation_failure"
            previous_error = f"Engineering validation failed with code {validation_code}. Full log: {validation_log}\nStructured error summary:\n{validation_summary}"
            set_state(state, phase="repairing", current_task=task["id"], last_build=build_record, last_error={"task": task["id"], "attempt": attempt, "kind": "validation", "log": validation_log, "summary": validation_summary})
            audit("validation_failed", task=task["id"], attempt=attempt, log=validation_log); continue
        set_state(state, phase="developing", current_task=task["id"], last_build=build_record, last_error=None)
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
                failure_kind = "browser_infrastructure_failure"
                task["status"] = "blocked"; save_json(task_path, task)
                set_state(state, phase="blocked", blocker=f"Browser infrastructure blocked; see {browser_log}", finish_reason="browser_infrastructure_blocked")
                audit("browser_infrastructure_blocked", task=task["id"], log=browser_log); return 20
            if browser_code != 0:
                failure_kind = "browser_acceptance_failure"
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
            if last_executor_log is not None and executor_requested_missing_task(last_executor_log):
                failure_kind = "prompt_transport_failure"
                previous_error = "Executor did not receive the complete task prompt and requested task details."
            else:
                failure_kind = "no_checkpoint_changes"
                previous_error = "Task produced no checkpointable business changes."
            task["status"] = "in_progress"; save_json(task_path, task); continue
        task["status"] = "finalizing"; save_json(task_path, task)
        set_state(state, phase="finalizing", current_task=task["id"], last_build=build_record, last_error=None, finish_reason="validation_passed_commit_pending")
        audit("task_finalizing", task=task["id"], changed_paths=business_changes)
        task["status"] = "completed"
        for stale_key in ("blocker", "last_error", "failure_kind", "failed_transport_version", "recovery_context", "provider_retry", "preserved_work", "quarantine", "exhausted_revalidation_head", "exhausted_revalidation_source_tree"):
            task.pop(stale_key, None)
        save_json(task_path, task)
        normalized = (
            "browser_deferred"
            if completion_mode == "browser" and browser_deferred
            else ("browser_accepted" if completion_mode == "browser" else "build_validated")
        )
        result = {
            "task": task["id"], "status": "completed", "execution_outcome": "completed",
            "normalized_finish_reason": normalized, "cline_exit_code": cline_code,
            "cline_finish_reason_raw": cline_raw_reason, "attempts": attempt,
            "browser_acceptance": browser_manifest, "finished_at": utc_now()
        }
        save_json(RESULTS_DIR / f"{task['id']}.json", result)
        set_state(state, phase="ready", current_task=None, last_build=build_record, last_error=None, git_sync={"status": "pending", "last_attempt_at": utc_now(), "last_error": None})
        audit("task_completed", **result)
        checkpoint_paths = [path for path in changed_paths() if not matches(path, config["ignored_change_paths"])]
        subprocess.run(["git", "add", "--", *checkpoint_paths], cwd=ROOT, check=True)
        if subprocess.run(["git", "commit", "-m", f"{task['id']}: {task['title']}"], cwd=ROOT).returncode != 0: return 8
        if auto_push_enabled(config) and subprocess.run(["git", "push"], cwd=ROOT).returncode != 0:
            set_state(
                state,
                phase="remote_degraded",
                current_task=None,
                blocker=None,
                finish_reason="remote_sync_degraded",
                git_sync={"status": "pending", "last_attempt_at": utc_now(), "last_error": "push_failed", "task": task["id"]},
            )
            audit("remote_sync_degraded", task=task["id"], reason="push_failed")
            checkpoint_control_files("chore: record degraded remote sync")
            print(f"Completed {task['id']} locally; remote push is degraded and will be retried later.")
            return 0
        if auto_push_enabled(config):
            set_state(state, phase="ready", current_task=None, git_sync={"status": "synced", "last_attempt_at": utc_now(), "last_error": None})
            checkpoint_control_files("chore: record successful remote sync")
            subprocess.run(["git", "push"], cwd=ROOT)
        print(f"Completed {task['id']} by {normalized}"); return 0

    if honor_terminal_completion(task_path, task, state):
        print(f"Skipped failure handling for already completed {task['id']}.")
        return 0
    return preserve_failed_work(
        task_path, task, config, state, previous_error, failure_kind,
        cline_raw_reason, max_attempts,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description="NEWERP guarded task orchestrator with deferrable browser acceptance")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status"); runner = sub.add_parser("run-next"); runner.add_argument("--dry-run", action="store_true")
    approval = sub.add_parser("approve"); approval.add_argument("task_id"); approval.add_argument("--by", required=True); approval.add_argument("--note", required=True)
    sub.add_parser("checkpoint-status")
    sub.add_parser("recover-checkpoint")
    args = parser.parse_args()
    if args.command == "checkpoint-status":
        return 0 if recoverable_completed_checkpoint(load_json(CONFIG_PATH), load_json(STATE_PATH)) else 1
    if args.command == "recover-checkpoint":
        return recover_completed_checkpoint(load_json(CONFIG_PATH), load_json(STATE_PATH)) or 0
    if args.command == "status": return status()
    if args.command == "approve": return approve(args.task_id, args.by, args.note)
    return run_next(args.dry_run)


if __name__ == "__main__":
    raise SystemExit(main())
