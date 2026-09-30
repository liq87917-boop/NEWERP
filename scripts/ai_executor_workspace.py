#!/usr/bin/env python3
"""Create and synchronize the scheduler-owned executor checkout.

The human-facing checkout may contain unrelated in-progress work.  The scheduler
must never stash or rewrite that work merely to obtain a clean execution base.
Instead it owns a sibling checkout on the configured integration branch.  A
separate .git directory is intentional: Git cannot safely check out the same
branch in two linked worktrees, while the existing pipeline expects an ordinary
fast-forward push from that branch.
"""
from __future__ import annotations

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from ai_state import refresh_project_state, save_json


def git(root: Path, *args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    completed = subprocess.run(
        ["git", *args], cwd=root, text=True, capture_output=True, errors="replace"
    )
    if check and completed.returncode != 0:
        raise RuntimeError((completed.stderr or completed.stdout).strip() or "git command failed")
    return completed


def load_config(root: Path) -> dict[str, Any]:
    return json.loads((root / ".ai" / "config.json").read_text(encoding="utf-8"))


def workspace_settings(root: Path, config: dict[str, Any]) -> tuple[bool, Path, str]:
    pipeline = config.get("pipeline", {})
    executor = pipeline.get("executor_worktree", {})
    enabled = bool(executor.get("enabled", True))
    configured_path = executor.get("path") or f"{root.name}.executor"
    path = Path(configured_path)
    if not path.is_absolute():
        path = root.parent / path
    target = str(executor.get("target_branch") or "main")
    return enabled, path.resolve(), target


def remote_url(root: Path) -> str:
    completed = git(root, "remote", "get-url", "origin")
    if not completed.stdout.strip():
        raise RuntimeError("origin remote is not configured")
    return completed.stdout.strip()


def active_manifest(control_root: Path) -> Path:
    git_dir = Path(git(control_root, "rev-parse", "--absolute-git-dir").stdout.strip())
    return git_dir / "newerp-active-executor.json"


def selected_executor(control_root: Path, configured: Path) -> Path:
    manifest = active_manifest(control_root)
    if not manifest.is_file():
        return configured
    value = json.loads(manifest.read_text(encoding="utf-8"))
    selected = Path(value["path"]).resolve()
    if selected.parent != configured.parent or not selected.name.startswith(configured.name + ".continue-"):
        raise RuntimeError("Executor manifest points outside the managed sibling checkouts")
    if git(selected, "rev-parse", "--is-inside-work-tree", check=False).returncode != 0:
        raise RuntimeError(f"Executor manifest checkout is unavailable: {selected}")
    return selected


def continue_after_exhausted_failure(control_root: Path, active_root: Path) -> dict[str, Any]:
    """Park a failed dirty checkout and continue independent work in a clean clone."""
    control_root, active_root = control_root.resolve(), active_root.resolve()
    config = load_config(active_root)
    _, configured, target = workspace_settings(control_root, config)
    if selected_executor(control_root, configured) != active_root:
        raise RuntimeError("Executor changed while continuation was being prepared")
    revision = git(active_root, "rev-parse", "--short", "HEAD").stdout.strip()
    maximum = int(config.get("autonomy", {}).get("max_supervised_recovery_cycles", 2))
    tasks_dir = active_root / ".ai" / "tasks"
    tasks = [json.loads(path.read_text(encoding="utf-8")) for path in sorted(tasks_dir.glob("ERP-*.json"))]
    by_id = {task["id"]: task for task in tasks}
    exhausted = [task for task in tasks if
                 task.get("status") in {"retry_pending", "failed", "blocked", "error"}
                 and int(task.get("supervised_recovery_cycles", 0) or 0) >= maximum
                 and task.get("exhausted_revalidation_head") == revision
                 and Path((task.get("preserved_work") or {}).get("execution_copy", "")).resolve() == active_root]
    if len(exhausted) != 1:
        return {"status": "not_needed", "reason": "no unique exhausted preserved task"}
    failed = exhausted[0]
    def can_run(task: dict[str, Any]) -> bool:
        if task.get("status") not in {"pending", "retry"} or not task.get("auto_start", True):
            return False
        if not all(by_id.get(dep, {}).get("status") == "completed" for dep in task.get("depends_on", [])):
            return False
        gate = task.get("human_gate") or {}
        if task.get("requires_human_approval") or str(gate.get("level", "L1")).upper() in {"L3", "L4"}:
            return gate.get("status") == "approved"
        if gate.get("required"):
            return gate.get("status") in {"approved", "ai_reviewed", "not_required"}
        return True

    runnable = [task for task in tasks if can_run(task)]
    if not runnable:
        return {"status": "not_needed", "reason": "no independent runnable task"}
    porcelain = git(active_root, "-c", "core.quotepath=false", "status", "--porcelain", "--untracked-files=all").stdout
    changed = set()
    for line in porcelain.splitlines():
        if len(line) < 4 or " -> " in line:
            raise RuntimeError("Cannot safely isolate renamed or malformed executor changes")
        changed.add(line[3:].replace("\\", "/"))
    expected = set((failed.get("preserved_work") or {}).get("changed_paths") or [])
    expected.update((failed.get("recovery_context") or {}).get("repair_allowed_paths") or [])
    if not changed or not changed.issubset(expected):
        raise RuntimeError("Executor has changes outside the failed task's preserved work")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    destination = configured.with_name(f"{configured.name}.continue-{failed['id'].lower()}-{stamp}")
    if destination.exists():
        raise RuntimeError(f"Continuation checkout already exists: {destination}")
    clone = subprocess.run(
        ["git", "clone", "--shared", "--quiet", "--branch", target, str(active_root), str(destination)],
        cwd=control_root, capture_output=True, text=True,
    )
    if clone.returncode != 0:
        raise RuntimeError((clone.stderr or clone.stdout).strip() or "Continuation clone failed")
    git(destination, "remote", "set-url", "origin", remote_url(active_root))
    for key in ("user.name", "user.email"):
        value = git(active_root, "config", key, check=False).stdout.strip()
        if value:
            git(destination, "config", key, value)
    parked = dict(failed)
    parked["status"] = "blocked"
    parked["blocker"] = "Automatic repair budget exhausted; preserved work remains in " + str(active_root)
    parked["parked_execution_copy"] = str(active_root)
    task_relative = Path(".ai") / "tasks" / f"{failed['id']}.json"
    save_json(destination / task_relative, parked)
    result_relative = Path(".ai") / "results" / f"{failed['id']}.json"
    old_result = active_root / result_relative
    result = json.loads(old_result.read_text(encoding="utf-8")) if old_result.is_file() else {"task": failed["id"]}
    result.update({"status": "blocked", "execution_outcome": "preserved_failure_isolated",
                   "preserved_work": failed.get("preserved_work")})
    save_json(destination / result_relative, result)
    state = refresh_project_state(
        destination, phase="ready", current_task=None,
        last_error={"task": failed["id"], "kind": "repair_budget_exhausted",
                    "summary": str(failed.get("last_error") or failed.get("blocker") or "")[-12000:]},
        finish_reason="failed_work_isolated_for_independent_tasks",
    )
    commit_paths = [task_relative.as_posix(), result_relative.as_posix(), ".ai/PROJECT_STATE.json"]
    git(destination, "add", "--", *commit_paths)
    staged = set(git(destination, "diff", "--cached", "--name-only").stdout.splitlines())
    if not staged.issubset(set(commit_paths)):
        raise RuntimeError("Continuation checkout staged unrelated paths")
    git(destination, "commit", "-m", f"{failed['id']}: isolate exhausted repair and continue independent tasks")
    manifest = active_manifest(control_root)
    save_json(manifest, {"path": str(destination), "parked": str(active_root),
                         "failed_task": failed["id"], "created_at": datetime.now(timezone.utc).isoformat()})
    return {"status": "continued", "path": str(destination), "parked": str(active_root),
            "failed_task": failed["id"], "next_task": runnable[0]["id"], "queue": state.get("queue", [])}


def ensure_workspace(control_root: Path) -> dict[str, Any]:
    control_root = control_root.resolve()
    config = load_config(control_root)
    enabled, executor_root, target = workspace_settings(control_root, config)
    if not enabled:
        return {"status": "disabled", "path": str(control_root), "branch": target, "target_branch": target}

    executor_root = selected_executor(control_root, executor_root)

    git(control_root, "fetch", "--quiet", "origin", target, check=False)
    if not executor_root.exists():
        executor_root.parent.mkdir(parents=True, exist_ok=True)
        # Clone locally to avoid duplicating objects, then restore the real remote.
        # Uncommitted files in the control checkout are never copied.
        source_remote = remote_url(control_root)
        clone = subprocess.run(
            ["git", "clone", "--shared", "--branch", target, str(control_root), str(executor_root)],
            cwd=control_root.parent, text=True, capture_output=True, errors="replace"
        )
        if clone.returncode != 0:
            raise RuntimeError((clone.stderr or clone.stdout).strip() or "executor clone failed")
        git(executor_root, "remote", "set-url", "origin", source_remote)
        git(executor_root, "fetch", "--quiet", "origin", target, check=False)
    elif git(executor_root, "rev-parse", "--is-inside-work-tree", check=False).returncode != 0:
        raise RuntimeError(f"Executor path is not a Git checkout: {executor_root}")

    current_branch = git(executor_root, "branch", "--show-current").stdout.strip()
    if current_branch != target:
        raise RuntimeError(f"Executor checkout uses unexpected branch {current_branch!r}; expected {target!r}")
    source_name = git(control_root, "config", "user.name", check=False).stdout.strip() or "NEWERP Automation"
    source_email = git(control_root, "config", "user.email", check=False).stdout.strip() or "automation@local"
    git(executor_root, "config", "user.name", source_name)
    git(executor_root, "config", "user.email", source_email)
    git(executor_root, "branch", "--set-upstream-to", f"origin/{target}", target, check=False)
    dirty = bool(git(executor_root, "status", "--porcelain").stdout.strip())
    # Local scheduler upgrades may be committed in the occupied control checkout
    # before they are pushed.  Fast-forward those commits into the clean executor;
    # never rewind executor work that is already ahead.
    if not dirty:
        local_fetch = git(executor_root, "fetch", "--quiet", str(control_root), target, check=False)
        if local_fetch.returncode == 0 and git(executor_root, "merge-base", "--is-ancestor", "HEAD", "FETCH_HEAD", check=False).returncode == 0:
            git(executor_root, "merge", "--ff-only", "--quiet", "FETCH_HEAD")
    ahead = behind = 0
    counts = git(executor_root, "rev-list", "--left-right", "--count", f"HEAD...origin/{target}", check=False)
    if counts.returncode == 0:
        parts = counts.stdout.split()
        if len(parts) >= 2:
            ahead, behind = int(parts[0]), int(parts[1])

    if not dirty and ahead == 0 and behind > 0:
        git(executor_root, "merge", "--ff-only", "--quiet", f"origin/{target}")
        behind = 0

    if ahead > 0 and behind > 0:
        status = "diverged"
    elif dirty:
        status = "recoverable_dirty"
    elif ahead > 0:
        status = "push_pending"
    else:
        status = "ready"
    return {
        "status": status,
        "path": str(executor_root),
        "branch": target,
        "target_branch": target,
        "ahead": ahead,
        "behind": behind,
        "control_worktree_dirty": bool(git(control_root, "status", "--porcelain").stdout.strip()),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["ensure", "continue"])
    parser.add_argument("--root", required=True)
    parser.add_argument("--executor")
    args = parser.parse_args()
    try:
        if args.command == "continue":
            if not args.executor:
                parser.error("continue requires --executor")
            result = continue_after_exhausted_failure(Path(args.root), Path(args.executor))
        else:
            result = ensure_workspace(Path(args.root))
        print(json.dumps(result, ensure_ascii=False))
        return 0 if result["status"] != "diverged" else 4
    except Exception as exc:
        print(json.dumps({"status": "error", "error": str(exc)}, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
