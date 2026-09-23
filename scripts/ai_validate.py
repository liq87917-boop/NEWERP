#!/usr/bin/env python3
"""Run a named NEWERP validation profile with explicit high-risk gates."""
from __future__ import annotations
import argparse, json, os, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONFIG_PATH = ROOT / ".ai" / "config.json"
RESULTS_DIR = ROOT / ".ai" / "results"
HIGH_RISK_PROFILES = {"integration", "ui"}

def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")

def main() -> int:
    parser = argparse.ArgumentParser(description="Run a NEWERP validation profile")
    parser.add_argument("--profile", default="safe")
    parser.add_argument("--task", default="manual")
    args = parser.parse_args()
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    profiles = config["validation_profiles"]
    if args.profile not in profiles:
        print(f"Unknown validation profile: {args.profile}", file=sys.stderr); return 2
    if args.profile in HIGH_RISK_PROFILES and os.getenv("ERP_AI_ALLOW_HIGH_RISK_TESTS") != "APPROVED":
        print(f"Profile '{args.profile}' can touch a real database or start a browser/API. Set ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED only after Human Gate approval.", file=sys.stderr)
        return 3
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    steps: list[dict[str, object]] = []
    overall = 0
    for command in profiles[args.profile]:
        started = utc_now()
        print(f"[validate] {' '.join(command)}", flush=True)
        completed = subprocess.run(command, cwd=ROOT, text=True)
        steps.append({"command": command, "started_at": started, "finished_at": utc_now(), "exit_code": completed.returncode})
        if completed.returncode != 0:
            overall = completed.returncode; break
    result = {"task": args.task, "profile": args.profile, "status": "passed" if overall == 0 else "failed", "finished_at": utc_now(), "steps": steps}
    (RESULTS_DIR / f"{args.task}-validation.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return overall

if __name__ == "__main__":
    raise SystemExit(main())
