#!/usr/bin/env python3
"""Run task-level acceptance in the installed Microsoft Edge browser."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
AI_DIR = ROOT / ".ai"
TASKS_DIR = AI_DIR / "tasks"
EVIDENCE_ROOT = AI_DIR / "evidence"
EDGE_CANDIDATES = (
    Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
    Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def load_env(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.exists():
        raise RuntimeError(".env.local is required for browser acceptance")
    for number, raw in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"): continue
        if "=" not in line: raise RuntimeError(f"Invalid .env.local line {number}")
        name, value = line.split("=", 1)
        name = name.strip()
        if not name or not value or "<" in value: raise RuntimeError(f"Incomplete .env.local variable: {name}")
        values[name] = value
    return values


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""): digest.update(chunk)
    return digest.hexdigest()


def write_manifest(directory: Path, payload: dict[str, Any]) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "manifest.json").write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description="NEWERP real-browser acceptance gate")
    parser.add_argument("--task", required=True)
    args = parser.parse_args()
    task_path = TASKS_DIR / f"{args.task}.json"
    if not task_path.exists(): print(f"Task not found: {args.task}", file=sys.stderr); return 20
    task = json.loads(task_path.read_text(encoding="utf-8"))
    acceptance = task.get("browser_acceptance", {})
    if task.get("completion_mode", "browser") != "browser":
        print("Task is not configured for browser completion.", file=sys.stderr); return 20
    scenarios = acceptance.get("scenarios") or []
    if not scenarios: print("Task has no browser acceptance scenarios.", file=sys.stderr); return 20
    edge = next((path for path in EDGE_CANDIDATES if path.exists()), None)
    if edge is None: print("Microsoft Edge is not installed in a supported location.", file=sys.stderr); return 20

    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    evidence_dir = EVIDENCE_ROOT / args.task / run_id
    evidence_dir.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    try: env.update(load_env(ROOT / ".env.local"))
    except RuntimeError as exc:
        write_manifest(evidence_dir, {"task": args.task, "status": "infrastructure_blocked", "reason": str(exc), "finished_at": utc_now()})
        print(str(exc), file=sys.stderr); return 20
    env["ERP_AI_EVIDENCE_DIR"] = str(evidence_dir)
    env["ERP_AI_ACCEPTANCE_TASK"] = args.task
    env["ERP_AI_EDGE_BINARY"] = str(edge)
    env["ASPNETCORE_ENVIRONMENT"] = "Development"

    test_filter = acceptance.get("test_filter") or "Collection=UiTests"
    build_command = ["dotnet", "build", "src/ERP.Api/ERP.Api.csproj", "-c", "Debug", "--verbosity", "minimal"]
    test_command = [
        "dotnet", "test", "src/ERP.IntegrationTests/ERP.IntegrationTests.csproj",
        "-c", "Debug", "--filter", str(test_filter), "--logger", f"trx;LogFileName={args.task}.trx",
        "--results-directory", str(evidence_dir), "--verbosity", "normal"
    ]
    started = utc_now()
    output_path = evidence_dir / "browser-test.log"
    with output_path.open("w", encoding="utf-8") as output:
        build = subprocess.run(build_command, cwd=ROOT, env=env, text=True, stdout=output, stderr=subprocess.STDOUT)
        if build.returncode == 0:
            completed = subprocess.run(test_command, cwd=ROOT, env=env, text=True, stdout=output, stderr=subprocess.STDOUT)
            test_exit_code = completed.returncode
        else:
            test_exit_code = build.returncode

    screenshots = sorted(evidence_dir.glob("*.png"))
    metadata = sorted(evidence_dir.glob("browser-session.json"))
    artifacts = [output_path, *sorted(evidence_dir.glob("*.trx")), *metadata, *screenshots]
    artifact_rows = [{"path": str(path.relative_to(ROOT)).replace("\\", "/"), "sha256": sha256(path), "bytes": path.stat().st_size} for path in artifacts if path.exists()]
    minimum = int(acceptance.get("minimum_screenshots", 1))
    if test_exit_code != 0: status, exit_code = "failed", 21
    elif len(screenshots) < minimum: status, exit_code = "evidence_incomplete", 22
    else: status, exit_code = "passed", 0
    manifest = {
        "task": args.task,
        "status": status,
        "started_at": started,
        "finished_at": utc_now(),
        "browser": {"name": "Microsoft Edge", "binary": str(edge), "real_browser": True, "headless": True},
        "scenarios": scenarios,
        "test_filter": test_filter,
        "build_exit_code": build.returncode,
        "test_exit_code": test_exit_code,
        "screenshot_count": len(screenshots),
        "minimum_screenshots": minimum,
        "artifacts": artifact_rows,
    }
    write_manifest(evidence_dir, manifest)
    print(json.dumps({"status": status, "manifest": str(evidence_dir / 'manifest.json'), "screenshots": len(screenshots)}, ensure_ascii=False))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
