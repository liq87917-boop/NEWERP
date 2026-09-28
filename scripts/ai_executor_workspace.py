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
from pathlib import Path
from typing import Any


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


def ensure_workspace(control_root: Path) -> dict[str, Any]:
    control_root = control_root.resolve()
    config = load_config(control_root)
    enabled, executor_root, target = workspace_settings(control_root, config)
    if not enabled:
        return {"status": "disabled", "path": str(control_root), "branch": target, "target_branch": target}

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
    git(executor_root, "branch", "--set-upstream-to", f"origin/{target}", target, check=False)
    dirty = bool(git(executor_root, "status", "--porcelain").stdout.strip())
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
    parser.add_argument("command", choices=["ensure"])
    parser.add_argument("--root", required=True)
    args = parser.parse_args()
    try:
        result = ensure_workspace(Path(args.root))
        print(json.dumps(result, ensure_ascii=False))
        return 0 if result["status"] != "diverged" else 4
    except Exception as exc:
        print(json.dumps({"status": "error", "error": str(exc)}, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
