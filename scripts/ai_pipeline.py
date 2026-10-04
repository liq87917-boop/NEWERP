#!/usr/bin/env python3
"""Queue-level automation for the NEWERP guarded task runner."""
from __future__ import annotations

import argparse
import contextlib
import fnmatch
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ai_state import refresh_project_state, save_json
from ai_provider_availability import is_provider_failure, retry_due

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
RESULTS_DIR = AI_DIR / "results"
LOGS_DIR = AI_DIR / "logs"
ORCHESTRATOR = ROOT / "scripts" / "ai_orchestrator.py"
AUTOMATION_TESTS = ROOT / "tests" / "automation"
RUNNABLE_STATUSES = {"pending", "retry"}
TERMINAL_STATUSES = {"completed", "deferred", "skipped", "superseded"}
FAILURE_STATUSES = {"blocked", "failed", "error", "retry_pending"}
NONBLOCKING_WAIT_STATUSES = FAILURE_STATUSES
ACTIVE_STATUSES = {"in_progress", "code_ready"}
RECOVERY_SCAN_STATUSES = FAILURE_STATUSES | ACTIVE_STATUSES | {"finalizing"}
SUPPORTED_GATES = {"L1", "L2", "L3", "L4"}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def load_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def audit(event: str, **details: Any) -> None:
    with AUDIT_PATH.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps({"at": utc_now(), "event": event, **details}, ensure_ascii=False) + "\n")


def run(command: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=ROOT, text=True, capture_output=capture)


def resolve_cline_command(command: str) -> str:
    """Use the native Cline executable when a Windows batch wrapper is configured."""
    configured = Path(command)
    if os.name != "nt" or configured.suffix.lower() not in {".cmd", ".bat"}:
        return command
    npm_root = configured.parent
    candidates = [npm_root / "node_modules" / "cline" / "bin" / ".cline"]
    candidates.extend(sorted(
        (npm_root / "node_modules" / "cline" / "node_modules" / "@cline").glob("cli-windows-*/bin/cline.exe")
    ))
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return command


def task_entries() -> list[tuple[Path, dict[str, Any]]]:
    config = load_json(CONFIG_PATH)
    entries = []
    for path in sorted(TASKS_DIR.glob(f"{config['task_prefix']}-*.json")):
        entries.append((path, load_json(path)))
    return entries


def task_dependencies(task: dict[str, Any]) -> list[str]:
    value = task.get("depends_on", [])
    if isinstance(value, str): value = [value]
    if not isinstance(value, list) or any(not isinstance(item, str) or not item.strip() for item in value):
        raise ValueError(f"{task.get('id')}: depends_on must be a string list")
    if task.get("id") in value: raise ValueError(f"{task.get('id')}: self dependency")
    return value


def validate_dependency_graph(entries: list[tuple[Path, dict[str, Any]]]) -> None:
    by_id = {task.get("id"): task for _, task in entries}
    visiting: set[str] = set(); visited: set[str] = set()
    def visit(task_id: str) -> None:
        if task_id in visiting: raise ValueError(f"dependency cycle at {task_id}")
        if task_id in visited: return
        visiting.add(task_id)
        for dependency in task_dependencies(by_id[task_id]):
            if dependency not in by_id: raise ValueError(f"{task_id}: dependency missing: {dependency}")
            visit(dependency)
        visiting.remove(task_id); visited.add(task_id)
    for task_id in by_id:
        if not isinstance(task_id, str): raise ValueError("task id must be a string")
        visit(task_id)


def queue_head() -> tuple[tuple[Path, dict[str, Any]] | None, str]:
    """Return the first dependency-safe runnable task without head-of-line blocking."""
    config = load_json(CONFIG_PATH)
    entries = task_entries(); validate_dependency_graph(entries)
    by_id = {task["id"]: task for _, task in entries}
    waiting: list[str] = []
    unfinished = False
    for path, task in entries:
        status = task.get("status")
        if status in TERMINAL_STATUSES:
            continue
        unfinished = True
        if status in ACTIVE_STATUSES:
            return None, f"{task['id']} status={status} is an active execution"
        if status in NONBLOCKING_WAIT_STATUSES:
            waiting.append(f"{task['id']}:{status}")
            continue
        if status not in RUNNABLE_STATUSES:
            waiting.append(f"{task['id']}:{status}")
            continue
        if not isinstance(task.get("auto_start", True), bool):
            waiting.append(f"{task['id']}:invalid_auto_start")
            continue
        if not isinstance(task.get("requires_human_approval", False), bool):
            waiting.append(f"{task['id']}:invalid_human_approval")
            continue
        gate = str(task.get("human_gate", {}).get("level", "L1")).upper()
        if gate not in SUPPORTED_GATES:
            waiting.append(f"{task['id']}:unknown_gate={gate}")
            continue
        unmet = [
            dependency for dependency in task_dependencies(task)
            if by_id[dependency].get("status") != "completed"
        ]
        if unmet:
            waiting.append(
                f"{task['id']}:waits_for=" +
                ",".join(f"{dependency}:{by_id[dependency].get('status')}" for dependency in unmet)
            )
            continue
        gate_status = task.get("human_gate", {}).get("status")
        approved = gate_status in {"approved", "not_required", "ai_reviewed"}
        if not task.get("auto_start", True):
            waiting.append(f"{task['id']}:auto_start=false")
            continue
        if config.get("human_gate", True) and (task.get("human_gate", {}).get("required", False) or gate in {"L3", "L4"} or task.get("requires_human_approval", False)) and not approved:
            waiting.append(f"{task['id']}:human_gate={gate}")
            continue
        return (path, task), "ready"
    if not unfinished:
        return None, "queue_empty"
    summary = "; ".join(waiting[:8])
    if len(waiting) > 8:
        summary += f"; +{len(waiting) - 8} more"
    return None, "no_runnable_tasks" + (f": {summary}" if summary else "")


def recover_obsolete_transport_failures(config: dict[str, Any]) -> list[str]:
    """Requeue only failures tied to an older, now-replaced prompt transport."""
    current_version = int(config.get("pipeline", {}).get("prompt_transport_version", 1))
    recovered: list[str] = []
    changed: list[str] = []
    for path, task in task_entries():
        failed_version = int(task.get("failed_transport_version", current_version) or current_version)
        if (
            task.get("status") != "blocked"
            or task.get("failure_kind") != "prompt_transport_failure"
            or failed_version >= current_version
        ):
            continue
        task.setdefault("recovery_history", []).append({
            "kind": "prompt_transport_failure",
            "from_version": failed_version,
            "to_version": current_version,
            "at": utc_now(),
        })
        task["status"] = "retry"
        task["attempts"] = 0
        task.pop("blocker", None)
        task.pop("last_error", None)
        task.pop("failure_kind", None)
        task.pop("failed_transport_version", None)
        save_json(path, task)
        recovered.append(task["id"])
        changed.append(str(path.relative_to(ROOT)))
        audit("task_auto_requeued", task=task["id"], reason="prompt_transport_upgraded", from_version=failed_version, to_version=current_version)
    if recovered:
        refresh_project_state(ROOT, phase="ready", current_task=None, blocker=None, finish_reason="transport_failures_requeued")
        changed.extend([str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        git_checkpoint("chore: requeue tasks after prompt transport upgrade", changed)
    return recovered


def reconcile_completed_results(config: dict[str, Any]) -> list[str]:
    """Make successful results terminal even when a stale worker rewrites task state."""
    reconciled: list[str] = []
    changed: list[str] = []
    for path, task in task_entries():
        if task.get("status") == "completed":
            continue
        result_path = RESULTS_DIR / f"{task['id']}.json"
        if not result_path.exists():
            continue
        result = load_json(result_path)
        if result.get("status") != "completed":
            continue
        task["status"] = "completed"
        for key in ("blocker", "last_error", "failure_kind", "failed_transport_version", "recovery_context", "provider_retry", "preserved_work", "quarantine"):
            task.pop(key, None)
        save_json(path, task)
        reconciled.append(task["id"])
        changed.append(str(path.relative_to(ROOT)))
        audit("terminal_completion_reconciled", task=task["id"], result=str(result_path.relative_to(ROOT)))
    if reconciled:
        refresh_project_state(ROOT, phase="ready", current_task=None, blocker=None, last_error=None, finish_reason="terminal_results_reconciled")
        changed.extend([str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        git_checkpoint("chore: reconcile terminal task completions", changed)
    return reconciled


def recovery_evidence(task: dict[str, Any]) -> dict[str, Any]:
    task_id = str(task["id"])
    result_path = RESULTS_DIR / f"{task_id}.json"
    result = load_json(result_path) if result_path.exists() else {}
    summary = task.get("last_error") or result.get("last_error") or task.get("blocker") or ""
    failure_kind = task.get("failure_kind") or result.get("failure_kind")
    if not failure_kind and str(summary).startswith("Path guard failed:"):
        failure_kind = "path_guard_failure"
    elif not failure_kind and "no checkpointable business changes" in str(summary).lower():
        failure_kind = "no_checkpoint_changes"
    # The active executor owns the current attempt. Control-root logs are only
    # historical fallback evidence; preferring them hid ERP-167's actual failure.
    attempt_logs = sorted(LOGS_DIR.glob(f"{task_id}-attempt-*.jsonl"))[-3:]
    validation_logs = sorted(LOGS_DIR.glob(f"{task_id}-validation-*.log"))[-3:]
    control_root = os.environ.get("AI_CONTROL_ROOT")
    if control_root and not (attempt_logs or validation_logs):
        control_logs = Path(control_root) / ".ai" / "logs"
        attempt_logs = sorted(control_logs.glob(f"{task_id}-attempt-*.jsonl"))[-3:]
        validation_logs = sorted(control_logs.glob(f"{task_id}-validation-*.log"))[-3:]
    preserved = task.get("preserved_work") or {}
    def evidence_path(path: Path) -> str:
        try:
            return path.relative_to(ROOT).as_posix()
        except ValueError:
            return str(path.resolve())
    state = load_json(STATE_PATH) if STATE_PATH.exists() else {}
    state_error = state.get("last_error")
    state_build = state.get("last_build")
    if isinstance(state_error, dict) and state_error.get("task") != task_id:
        state_error = None
    if isinstance(state_build, dict) and state_build.get("task") != task_id:
        state_build = None
    return {
        "failure_kind": failure_kind or "unclassified_engineering_failure",
        "summary": summary,
        "result": str(result_path.relative_to(ROOT)) if result_path.exists() else None,
        "attempt_logs": [evidence_path(path) for path in attempt_logs],
        "validation_logs": [evidence_path(path) for path in validation_logs],
        "latest_state_error": state_error,
        "latest_build": state_build,
        "task_context": str((TASKS_DIR / f"{task_id}.json").relative_to(ROOT)),
        "changed_paths": preserved.get("changed_paths", []),
        "diff": preserved.get("diff"),
        "execution_copy": preserved.get("execution_copy"),
    }


def confirmed_baseline_unit_failure(task: dict[str, Any], evidence: dict[str, Any]) -> dict[str, Any] | None:
    """Grant an exact test-file repair only after reproducing it at clean HEAD.

    A feature task cannot silently broaden its own path guard. This independent
    remediation scope is limited to one tracked unit test whose same failure
    reproduces without the feature's uncommitted changes.
    """
    if evidence.get("failure_kind") != "validation_failure":
        return None
    build = evidence.get("latest_build") or {}
    reference = build.get("log") or (evidence.get("validation_logs") or [None])[-1]
    if not reference:
        return None
    validation_log = Path(reference)
    if not validation_log.is_absolute():
        validation_log = ROOT / validation_log
    if not validation_log.is_file():
        return None
    output = validation_log.read_text(encoding="utf-8", errors="replace")
    failed = re.findall(r"\[xUnit\.net[^\]]*\]\s+([^\s]+)\s+\[FAIL\]", output)
    if len(failed) != 1:
        return None
    match = re.search(r"\bin\s+([^\r\n]+?\.cs):line\s+\d+", output)
    if not match:
        return None
    try:
        path = Path(match.group(1)).resolve().relative_to(ROOT.resolve()).as_posix()
    except ValueError:
        return None
    if not path.startswith("src/ERP.UnitTests/") or not path.endswith("Tests.cs"):
        return None
    if any(fnmatch.fnmatchcase(path, pattern) for pattern in task.get("allowed_paths", [])):
        return None
    if run(["git", "ls-files", "--error-unmatch", "--", path], capture=True).returncode != 0:
        return None
    if run(["git", "status", "--porcelain", "--", path], capture=True).stdout.strip():
        return None
    revision = run(["git", "rev-parse", "HEAD"], capture=True).stdout.strip()
    if not revision:
        return None
    failure_values = re.search(r"Expected:\s*(\S+)\s+Actual:\s*(\S+)", output)
    signature = hashlib.sha256(f"{revision}|{failed[0]}|{failure_values.group(0) if failure_values else ''}".encode()).hexdigest()[:12]
    baseline_log = LOGS_DIR / f"{task['id']}-baseline-{signature}.log"
    if baseline_log.is_file():
        baseline_output = baseline_log.read_text(encoding="utf-8", errors="replace")
    else:
        try:
            with tempfile.TemporaryDirectory(prefix="newerp-baseline-") as directory:
                temporary = Path(directory).resolve()
                if not temporary.is_relative_to(Path(tempfile.gettempdir()).resolve()):
                    raise RuntimeError("Baseline checkout escaped the temporary directory")
                checkout = temporary / "checkout"
                clone = subprocess.run(
                    ["git", "clone", "--shared", "--quiet", "--no-checkout", str(ROOT), str(checkout)],
                    cwd=ROOT, capture_output=True, text=True, timeout=120,
                )
                if clone.returncode != 0:
                    return None
                checkout_result = subprocess.run(
                    ["git", "checkout", "--quiet", "--detach", revision],
                    cwd=checkout, capture_output=True, text=True, timeout=120,
                )
                if checkout_result.returncode != 0:
                    return None
                baseline = subprocess.run(
                    ["dotnet", "test", "src/ERP.UnitTests/ERP.UnitTests.csproj", "-c", "Release",
                     "--filter", f"FullyQualifiedName={failed[0]}", "--verbosity", "minimal"],
                    cwd=checkout, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                    text=True, errors="replace", timeout=300,
                )
                baseline_output = baseline.stdout or ""
                if baseline.returncode == 0:
                    return None
        except (OSError, subprocess.TimeoutExpired, RuntimeError):
            return None
        LOGS_DIR.mkdir(parents=True, exist_ok=True)
        baseline_log.write_text(baseline_output, encoding="utf-8")
    if failed[0].split(".")[-1] not in baseline_output or not re.search(r"\bFAIL(?:ED)?\b|失败", baseline_output, re.IGNORECASE):
        return None
    if failure_values and not all(value in baseline_output for value in failure_values.groups()):
        return None
    return {
        "path": path,
        "test": failed[0],
        "baseline_revision": revision,
        "baseline_log": str(baseline_log.relative_to(ROOT)),
        "baseline_confirmed": True,
    }


def automatic_recovery_allowed(task: dict[str, Any]) -> bool:
    """Engineering failures are recoverable regardless of the blocker wording.

    External prerequisites and intentional deferrals are not code repairs.
    Never reset their budgets or authorize changes to protected resources.
    """
    if task.get("status") not in RECOVERY_SCAN_STATUSES or not task.get("auto_start", True):
        return False
    if task.get("requires_human_approval", False) or str(task.get("human_gate", {}).get("level", "L1")).upper() in {"L3", "L4"}:
        return False
    kind = str(task.get("failure_kind") or "").lower()
    if kind in {"provider_unavailable", "environment_blocked", "browser_infrastructure_blocked",
                "missing_credentials", "dependency_blocked", "human_approval_required"}:
        return False
    reason = str(task.get("blocker") or "") + " " + str(task.get("last_error") or "")
    if re.search(r"insufficient (?:balance|credits|quota)|quota exceeded|billing hard limit|missing credentials|"
                 r"environment blocked|browser infrastructure|dependency.*(?:not completed|blocked)|"
                 r"working tree is not clean|waiting.*approval|production.*approval", reason, re.IGNORECASE):
        return False
    # A blocked task with no engineering evidence may be a business/planning gate.
    return task.get("status") != "blocked" or kind in {
        "validation_failure", "path_guard_failure", "prompt_transport_failure",
        "no_checkpoint_changes", "unclassified_engineering_failure",
    } or bool(task.get("last_error")) or str(task.get("blocker", "")).startswith("Automatic repair budget exhausted")


def recover_blocked_with_deepseek(config: dict[str, Any]) -> list[str]:
    """Turn recoverable failures into bounded, evidence-rich DeepSeek repair work."""
    autonomy = config.get("autonomy", {})
    if not autonomy.get("enabled", False) or not autonomy.get("deepseek_supervisor_enabled", False):
        return []
    maximum = int(autonomy.get("max_supervised_recovery_cycles", 2))
    recovered: list[str] = []
    changed: list[str] = []
    for path, task in task_entries():
        status = task.get("status")
        if status not in RECOVERY_SCAN_STATUSES:
            continue
        if is_provider_failure(task):
            # Billing/quota recovery needs a timed probe, not a code repair cycle.
            continue
        if not automatic_recovery_allowed(task):
            continue
        cycles = int(task.get("supervised_recovery_cycles", 0) or 0)
        if cycles >= maximum:
            continue
        evidence = recovery_evidence(task)
        baseline_fix = confirmed_baseline_unit_failure(task, evidence)
        if baseline_fix:
            evidence["baseline_test_fix"] = baseline_fix
        remediation_id = f"{task['id']}-RECOVERY-{cycles + 1}"
        task.setdefault("recovery_history", []).append({
            "remediation_task": remediation_id,
            "cycle": cycles + 1,
            "at": utc_now(),
            "evidence": evidence,
        })
        task["status"] = "retry"
        task["attempts"] = 0
        task["supervised_recovery_cycles"] = cycles + 1
        task["recovery_context"] = {
            "remediation_task": remediation_id,
            **evidence,
            "repair_allowed_paths": [baseline_fix["path"]] if baseline_fix else [],
            "instruction": (
                "DeepSeek supervisor recovery: inspect the referenced complete logs, diagnose the root cause, "
                "and implement a different safe repair within allowed_paths. Rebuild and rerun the configured tests; "
                "if validation fails, use the newest log as the next repair input. Do not repeat the failed approach. "
                "Do not edit task/state/result files or relax production and irreversible-operation gates. "
                + (
                    f"Clean-HEAD replay confirms {baseline_fix['test']} also fails without this feature. "
                    f"The separate {remediation_id} repair may change only the exact baseline test file "
                    f"{baseline_fix['path']} in addition to the original task paths; keep the original assertions "
                    "meaningful and fix deterministic fixtures rather than hiding a failure. "
                    if baseline_fix else ""
                )
            ),
        }
        task.pop("blocker", None)
        task.pop("last_error", None)
        save_json(path, task)
        recovered.append(task["id"])
        changed.append(str(path.relative_to(ROOT)))
        audit("deepseek_supervisor_requeued", task=task["id"], remediation_task=remediation_id, cycle=cycles + 1, evidence=evidence)
    if recovered:
        refresh_project_state(ROOT, phase="ready", current_task=None, blocker=None, finish_reason="deepseek_supervisor_recovery")
        changed.extend([str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        git_checkpoint("chore: schedule DeepSeek supervised failure recovery", changed)
    return recovered


def schedule_provider_retries(config: dict[str, Any]) -> list[str]:
    """Wake quota failures when due, including failures recorded by older runners."""
    recovered: list[str] = []
    changed: list[str] = []
    for path, task in task_entries():
        if task.get("status") not in FAILURE_STATUSES or not is_provider_failure(task):
            continue
        if task.get("requires_human_approval", False) or str(task.get("human_gate", {}).get("level", "L1")).upper() in {"L3", "L4"}:
            continue
        if not retry_due(task.get("provider_retry")):
            continue
        task["status"] = "retry"
        task["attempts"] = 0
        task["failure_kind"] = "provider_unavailable"
        task.pop("blocker", None)
        task.pop("recovery_context", None)
        save_json(path, task)
        recovered.append(task["id"])
        changed.append(str(path.relative_to(ROOT)))
        audit("provider_retry_due", task=task["id"], previous_retry=task.get("provider_retry"))
    if recovered:
        refresh_project_state(ROOT, phase="ready", current_task=None, blocker=None,
                              finish_reason="provider_retry_due")
        changed.extend([str(STATE_PATH.relative_to(ROOT)), str(AUDIT_PATH.relative_to(ROOT))])
        git_checkpoint("chore: schedule model provider retry", changed)
    return recovered


def queue_status() -> int:
    rows = [{"id": task.get("id"), "status": task.get("status"), "risk": task.get("risk_level"), "title": task.get("title")} for _, task in task_entries()]
    print(json.dumps(rows, ensure_ascii=False, indent=2)); return 0


def create_task(title: str, description: str, acceptance: list[str], allowed: list[str], profile: str, risk: str, depends_on: list[str], gate: str, completion_mode: str, browser_scenarios: list[str]) -> int:
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
        gate = gate.upper()
        if gate not in SUPPORTED_GATES: print(f"Unknown gate: {gate}", file=sys.stderr); return 2
        if completion_mode == "browser" and not browser_scenarios: browser_scenarios = list(acceptance)
        gate_required = gate in {"L3", "L4"} or risk == "high" or profile in {"integration", "ui"} or protected
        task = {
            "id": task_id,
            "title": title,
            "status": "pending",
            "risk_level": risk,
            "description": description,
            "acceptance_criteria": acceptance,
            "allowed_paths": allowed,
            "validation_profile": profile,
            "depends_on": depends_on,
            "auto_start": True,
            "requires_human_approval": gate in {"L3", "L4"},
            "completion_mode": completion_mode,
            "browser_acceptance": {
                "required": completion_mode == "browser",
                "scenarios": browser_scenarios,
                "test_filter": "Collection=UiTests",
                "minimum_screenshots": 1
            },
            "human_gate": {
                "required": gate_required,
                "status": "pending" if gate_required else "not_required",
                "level": gate,
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
        # Windows byte-range locks also reject reads of the locked byte.
        # Inspect length without reading it, so contention reaches our busy handler.
        stream.seek(0, os.SEEK_END)
        if stream.tell() == 0:
            stream.write(b"0"); stream.flush()
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


def mark_queue_replenishing(*, idle: bool = False) -> dict[str, Any]:
    """Request planner supply without inventing work or bypassing failure recovery.

    Caller holds pipeline_lock. Runtime evidence belongs in ignored logs; it must
    not dirty business files or overwrite the runner/current task on a low queue.
    """
    config, state = load_json(CONFIG_PATH), load_json(STATE_PATH)
    policy = config.get("rolling_queue", {})
    entries = task_entries()
    effective = {t["id"] for _, t in entries
                 if t.get("status") in RUNNABLE_STATUSES | ACTIVE_STATUSES}
    by_id = {t["id"]: t for _, t in entries}
    current = state.get("current_task")
    if current and current in by_id and by_id[current].get("status") not in TERMINAL_STATUSES | FAILURE_STATUSES:
        effective.add(current)
    low = int(policy.get("low_watermark", 2))
    target = int(config.get("queue_target_size", policy.get("batch_size", 4)))
    required = bool(policy.get("enabled", False)) and len(effective) < low
    path = LOGS_DIR / "queue-replenishment.json"
    previous = load_json(path) if path.exists() else {}
    now = utc_now()
    since = previous.get("requested_at") if previous.get("required") and required else now
    seconds = max(0, (datetime.now(timezone.utc) - datetime.fromisoformat(since.replace("Z", "+00:00"))).total_seconds())
    failures = [{"task": t["id"], "status": t.get("status"),
                 "recovery_cycles": t.get("supervised_recovery_cycles", 0),
                 "reason": t.get("blocker") or t.get("last_error")}
                for _, t in entries if t.get("status") in FAILURE_STATUSES]
    value = {"updated_at": now, "executor_root": str(ROOT), "required": required,
             "owner": policy.get("replenishment_owner", "ChatGPT"),
             "effective_tasks": sorted(effective), "effective_count": len(effective),
             "requested_count": max(0, target - len(effective)) if required else 0,
             "requested_at": since if required else None,
             "overdue": required and seconds >= int(policy.get("replenishment_timeout_seconds", 1800)),
             "reason": "queue_empty" if required and not effective else "low_watermark" if required else "queue_sufficient",
             "failures": failures, "remote_sync": state.get("git_sync", {}),
             "constraints": "Only verified, nonduplicate current-stage tasks; do not restore deferred work or pass environment acceptance."}
    save_json(path, value)
    # The remote-sync result remains a separate field. An empty local queue is
    # planner work, not a remote-sync failure or a request to repeat old repairs.
    if idle and required and not current and not state.get("conversation_control", {}).get("paused", False) and state.get("phase") in {"ready", "remote_degraded", "replenishing"}:
        state.update(phase="replenishing", finish_reason="planner_supply_required")
        if state != load_json(STATE_PATH):
            state["updated_at"] = now
            save_json(STATE_PATH, state)
    return value


def run_all() -> int:
    with pipeline_lock():
        config = load_json(CONFIG_PATH)
        recover_obsolete_transport_failures(config)
        scheduler_failures = 0
        while True:
            # Failure handling is a first-class phase of every scheduler cycle.
            # Queue capacity only controls replenishment; it must never bypass
            # failed-task/log collection or DeepSeek repair scheduling.
            reconcile_completed_results(config)
            schedule_provider_retries(config)
            recover_blocked_with_deepseek(config)
            state = load_json(STATE_PATH)
            conversation = state.get("conversation_control", {})
            if conversation.get("paused", False):
                reason = conversation.get("pause_reason") or "paused by DeepSeek conversation control"
                audit("pipeline_paused", reason=reason)
                print(f"Pipeline paused: {reason}", file=sys.stderr)
                return 11
            try: item, reason = queue_head()
            except ValueError as exc:
                print(f"Queue metadata error: {exc}", file=sys.stderr); return 10
            if item is None and reason == "queue_empty":
                mark_queue_replenishing(idle=True)
                from ai_queue_planner import replenish
                if config.get("autonomy", {}).get("auto_planner_enabled", False) and replenish(__import__("types").SimpleNamespace(**globals())):
                    continue
                print("Autonomous planner evidence: .ai/logs/autonomous-planner.json.")
                return 0
            if item is None:
                mark_queue_replenishing(idle=True)
                audit("pipeline_waiting", reason=reason)
                print(f"[pipeline] no dependency-safe runnable task: {reason}")
                return 0
            mark_queue_replenishing()
            task_id = item[1]["id"]
            print(f"[pipeline] starting {task_id}", flush=True)
            completed = run([sys.executable, str(ORCHESTRATOR), "run-next"])
            if completed.returncode != 0:
                audit("pipeline_stopped", task=task_id, exit_code=completed.returncode)
                scheduler_failures += 1
                if completed.returncode in {5, 6, 10, 20} or scheduler_failures >= 3:
                    print(
                        f"Scheduler stopped at {task_id} after {scheduler_failures} failed launch(es); "
                        "preserved work and failure evidence remain available.",
                        file=sys.stderr,
                    )
                    return completed.returncode
                delay = min(10, scheduler_failures)
                print(
                    f"Scheduler execution failed at {task_id} with exit code {completed.returncode}; "
                    f"automatic recovery will retry in {delay}s.",
                    file=sys.stderr,
                )
                time.sleep(delay)
                continue
            scheduler_failures = 0


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
    allowed_statuses = {"pending", "retry", "retry_pending", "in_progress", "code_ready", "finalizing", "blocked", "completed", "failed", "error", "deferred", "skipped", "superseded"}
    for path, task in task_entries():
        ids.append(task.get("id"))
        if task.get("id") != path.stem: errors.append(f"Task id/file mismatch: {path.name}")
        if task.get("status") not in allowed_statuses: errors.append(f"Invalid status in {path.name}")
        if not task.get("allowed_paths"): errors.append(f"Missing allowed_paths in {path.name}")
    if len(ids) != len(set(ids)): errors.append("Duplicate task ids")
    try: validate_dependency_graph(task_entries())
    except ValueError as exc: errors.append(str(exc))
    checks["tasks"] = len(ids)
    checks["git"] = "ok" if run(["git", "rev-parse", "--is-inside-work-tree"], capture=True).returncode == 0 else "missing"
    dirty = run(["git", "status", "--porcelain"], capture=True).stdout.strip()
    checks["worktree"] = "clean" if not dirty else "dirty"
    cline = Path(resolve_cline_command(config["cline_command"]))
    checks["cline"] = "ok" if cline.exists() and run([str(cline), "--version"], capture=True).returncode == 0 else "unavailable"
    checks["dotnet"] = run(["dotnet", "--version"], capture=True).stdout.strip() or "unavailable"
    protected = set(config.get("protected_paths", []))
    for required in {"**/*.sql", "deploy/**", "src/ERP.Api/appsettings*.json"}:
        if required not in protected: errors.append(f"Missing protected path: {required}")
    checks["high_risk_profiles"] = sorted({"integration", "ui"}.intersection(config.get("validation_profiles", {})))
    checks["queue_target_size"] = config.get("queue_target_size", 3)
    checks["browser_completion_required"] = config.get("completion_policy", {}).get("require_real_browser", False)
    if not (ROOT / "scripts" / "ai_browser_acceptance.py").exists(): errors.append("Browser acceptance runner missing")
    if not (ROOT / "scripts" / "ds_project_control.py").exists(): errors.append("DeepSeek conversation controller missing")
    if AUTOMATION_TESTS.exists():
        contract_tests = run([sys.executable, "-m", "unittest", "discover", "-s", str(AUTOMATION_TESTS), "-p", "test_*.py"], capture=True)
        checks["automation_contract_tests"] = "passed" if contract_tests.returncode == 0 else "failed"
        if contract_tests.returncode != 0: errors.append((contract_tests.stdout + contract_tests.stderr).strip())
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
    sub.add_parser("replenishment-status")
    cp = sub.add_parser("create")
    cp.add_argument("--title", required=True); cp.add_argument("--description", required=True)
    cp.add_argument("--accept", action="append", required=True); cp.add_argument("--allow", action="append", required=True)
    cp.add_argument("--profile", default="safe"); cp.add_argument("--risk", choices=["low", "medium", "high"], default="low")
    cp.add_argument("--depends-on", action="append", default=[]); cp.add_argument("--gate", choices=["L1", "L2", "L3", "L4"], default="L1")
    cp.add_argument("--completion-mode", choices=["build", "browser", "control_plane"], default="build")
    cp.add_argument("--browser-scenario", action="append", default=[])
    dp = sub.add_parser("defer"); dp.add_argument("task_id"); dp.add_argument("--by", required=True); dp.add_argument("--note", required=True)
    rp = sub.add_parser("retry"); rp.add_argument("task_id"); rp.add_argument("--by", required=True); rp.add_argument("--note", required=True)
    args = parser.parse_args()
    if args.command in {"run", "resume"}: return run_all()
    if args.command == "retry-push": return retry_push()
    if args.command == "self-test": return self_test()
    if args.command == "queue": return queue_status()
    if args.command == "replenishment-status":
        with pipeline_lock():
            print(json.dumps(mark_queue_replenishing(idle=True), ensure_ascii=False, indent=2))
        return 0
    if args.command == "create": return create_task(args.title, args.description, args.accept, args.allow, args.profile, args.risk, args.depends_on, args.gate, args.completion_mode, args.browser_scenario)
    if args.command == "defer": return defer_task(args.task_id, args.by, args.note)
    return retry_task(args.task_id, args.by, args.note)


if __name__ == "__main__":
    raise SystemExit(main())
