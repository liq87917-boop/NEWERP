from __future__ import annotations

import importlib.util
import json
import subprocess
from unittest.mock import patch
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


pipeline = load_module("ai_pipeline_contract", ROOT / "scripts" / "ai_pipeline.py")
orchestrator = load_module("ai_orchestrator_contract", ROOT / "scripts" / "ai_orchestrator.py")
state_module = load_module("ai_state_contract", ROOT / "scripts" / "ai_state.py")
executor_workspace = load_module("ai_executor_workspace_contract", ROOT / "scripts" / "ai_executor_workspace.py")


class PipelineContracts(unittest.TestCase):
    def task(self, task_id: str, status: str = "pending", depends_on=None, gate="L1"):
        return {
            "id": task_id,
            "title": task_id,
            "status": status,
            "allowed_paths": ["src/**"],
            "depends_on": depends_on or [],
            "auto_start": True,
            "requires_human_approval": gate in {"L3", "L4"},
            "human_gate": {"level": gate, "required": gate in {"L3", "L4"}, "status": "pending"},
        }

    def test_dependency_cycle_is_rejected(self):
        entries = [(Path("ERP-010.json"), self.task("ERP-010", depends_on=["ERP-011"])),
                   (Path("ERP-011.json"), self.task("ERP-011", depends_on=["ERP-010"]))]
        with self.assertRaisesRegex(ValueError, "dependency cycle"):
            pipeline.validate_dependency_graph(entries)

    def test_blocked_task_does_not_stop_independent_work(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tasks = root / "tasks"; tasks.mkdir()
            config = root / "config.json"
            config.write_text(json.dumps({"task_prefix": "ERP"}), encoding="utf-8")
            (tasks / "ERP-010.json").write_text(json.dumps(self.task("ERP-010", "blocked")), encoding="utf-8")
            (tasks / "ERP-011.json").write_text(json.dumps(self.task("ERP-011")), encoding="utf-8")
            old_tasks, old_config = pipeline.TASKS_DIR, pipeline.CONFIG_PATH
            pipeline.TASKS_DIR, pipeline.CONFIG_PATH = tasks, config
            try:
                item, reason = pipeline.queue_head()
                self.assertEqual("ERP-011", item[1]["id"])
                self.assertEqual("ready", reason)
            finally:
                pipeline.TASKS_DIR, pipeline.CONFIG_PATH = old_tasks, old_config

    def test_state_save_retries_transient_windows_destination_lock(self):
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(Path, "replace", side_effect=[PermissionError("locked"), None]) as replace, \
             patch.object(state_module.time, "sleep") as sleep:
            state_module.save_json(Path(directory) / "PROJECT_STATE.json", {"phase": "ready"})

        self.assertEqual(2, replace.call_count)
        sleep.assert_called_once_with(0.05)

    def test_executor_checkout_preserves_dirty_control_worktree(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            control = base / "control"
            executor = base / "executor"
            control.mkdir()
            subprocess.run(["git", "init", "-b", "main"], cwd=control, check=True, capture_output=True)
            subprocess.run(["git", "config", "user.email", "automation@test.invalid"], cwd=control, check=True)
            subprocess.run(["git", "config", "user.name", "Automation Test"], cwd=control, check=True)
            (control / ".ai").mkdir()
            (control / ".ai" / "config.json").write_text(json.dumps({
                "pipeline": {"executor_worktree": {
                    "enabled": True,
                    "path": str(executor),
                    "target_branch": "main",
                }}
            }), encoding="utf-8")
            (control / "tracked.txt").write_text("base\n", encoding="utf-8")
            subprocess.run(["git", "add", "."], cwd=control, check=True)
            subprocess.run(["git", "commit", "-m", "base"], cwd=control, check=True, capture_output=True)
            subprocess.run(["git", "remote", "add", "origin", str(control)], cwd=control, check=True)
            (control / "user-work.txt").write_text("preserve me\n", encoding="utf-8")

            result = executor_workspace.ensure_workspace(control)

            self.assertEqual("ready", result["status"])
            self.assertTrue(result["control_worktree_dirty"])
            self.assertFalse((executor / "user-work.txt").exists())
            self.assertEqual("", subprocess.run(
                ["git", "status", "--porcelain"], cwd=executor, check=True,
                capture_output=True, text=True,
            ).stdout.strip())

    def test_prompt_transport_upgrade_requeues_only_matching_blocked_tasks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ai_dir = root / ".ai"
            tasks = ai_dir / "tasks"
            tasks.mkdir(parents=True)
            config_path = ai_dir / "config.json"
            state_path = ai_dir / "PROJECT_STATE.json"
            audit_path = ai_dir / "audit.jsonl"
            config_path.write_text(json.dumps({"task_prefix": "ERP", "pipeline": {"prompt_transport_version": 2}}), encoding="utf-8")
            state_path.write_text(json.dumps({"phase": "ready"}), encoding="utf-8")
            affected = self.task("ERP-102", "blocked") | {
                "failure_kind": "prompt_transport_failure",
                "failed_transport_version": 1,
                "attempts": 3,
                "blocker": "Automatic repair budget exhausted",
            }
            unrelated = self.task("ERP-103", "blocked") | {"attempts": 3}
            (tasks / "ERP-102.json").write_text(json.dumps(affected), encoding="utf-8")
            (tasks / "ERP-103.json").write_text(json.dumps(unrelated), encoding="utf-8")
            old_values = (pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH)
            pipeline.ROOT, pipeline.TASKS_DIR = root, tasks
            pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH = config_path, state_path, audit_path
            try:
                with patch.object(pipeline, "git_checkpoint") as checkpoint, \
                     patch.object(pipeline, "refresh_project_state"):
                    recovered = pipeline.recover_obsolete_transport_failures(json.loads(config_path.read_text(encoding="utf-8")))
                self.assertEqual(["ERP-102"], recovered)
                self.assertEqual("retry", json.loads((tasks / "ERP-102.json").read_text(encoding="utf-8"))["status"])
                self.assertEqual("blocked", json.loads((tasks / "ERP-103.json").read_text(encoding="utf-8"))["status"])
                checkpoint.assert_called_once()
            finally:
                pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH = old_values

    def test_missing_task_request_is_classified_as_transport_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "attempt.jsonl"
            log.write_text("Please paste the full task JSON and I'll get started.", encoding="utf-8")
            self.assertTrue(orchestrator.executor_requested_missing_task(log))

    def test_l3_gate_requires_explicit_approval(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            tasks = root / "tasks"; tasks.mkdir()
            config = root / "config.json"
            config.write_text(json.dumps({"task_prefix": "ERP"}), encoding="utf-8")
            (tasks / "ERP-010.json").write_text(json.dumps(self.task("ERP-010", gate="L3")), encoding="utf-8")
            old_tasks, old_config = pipeline.TASKS_DIR, pipeline.CONFIG_PATH
            pipeline.TASKS_DIR, pipeline.CONFIG_PATH = tasks, config
            try:
                item, reason = pipeline.queue_head()
                self.assertIsNone(item)
                self.assertIn("ERP-010:human_gate=L3", reason)
            finally:
                pipeline.TASKS_DIR, pipeline.CONFIG_PATH = old_tasks, old_config

    def test_cline_finish_reason_remains_raw_metadata(self):
        value = {"result": {"finishReason": "tool_use"}}
        self.assertEqual("tool_use", orchestrator.nested_finish_reason(value))

    def test_windows_cline_batch_wrapper_resolves_real_executable(self):
        with tempfile.TemporaryDirectory() as directory:
            npm_root = Path(directory)
            wrapper = npm_root / "cline.cmd"
            wrapper.write_text("@echo off\n", encoding="utf-8")
            executable = (
                npm_root / "node_modules" / "cline" / "node_modules" /
                "@cline" / "cli-windows-x64" / "bin" / "cline.exe"
            )
            executable.parent.mkdir(parents=True)
            executable.touch()

            with patch.object(orchestrator.os, "name", "nt"):
                resolved = orchestrator.resolve_cline_command(str(wrapper))

        self.assertEqual(str(executable), resolved)

    def test_windows_cline_batch_wrapper_fails_closed_without_executable(self):
        with tempfile.TemporaryDirectory() as directory:
            wrapper = Path(directory) / "cline.cmd"
            wrapper.write_text("@echo off\n", encoding="utf-8")

            with patch.object(orchestrator.os, "name", "nt"), \
                 self.assertRaisesRegex(FileNotFoundError, "cannot safely carry multiline prompts"):
                orchestrator.resolve_cline_command(str(wrapper))

    def test_browser_task_requires_scenarios(self):
        task = self.task("ERP-010") | {
            "description": "business task",
            "acceptance_criteria": ["accepted"],
            "validation_profile": "safe",
            "completion_mode": "browser",
            "browser_acceptance": {"scenarios": []},
        }
        with self.assertRaisesRegex(ValueError, "require browser_acceptance.scenarios"):
            orchestrator.validate_task(task, {"task_prefix": "ERP", "validation_profiles": {"safe": []}, "completion_policy": {}})

    def test_build_task_does_not_require_browser_scenarios(self):
        task = self.task("ERP-010") | {
            "description": "business task",
            "acceptance_criteria": ["accepted"],
            "validation_profile": "safe",
            "completion_mode": "build",
        }
        orchestrator.validate_task(
            task,
            {"task_prefix": "ERP", "validation_profiles": {"safe": []}, "completion_policy": {}},
        )

    def test_disabled_human_gate_allows_development_task(self):
        task = self.task("ERP-010", gate="L4")
        self.assertTrue(orchestrator.gate_is_approved(task, {"human_gate": False}))

    def test_validation_failure_persists_full_log_and_bounded_summary(self):
        task = self.task("ERP-010") | {"validation_profile": "safe"}
        output = ("compile error\n" * 2000).encode("utf-8")
        completed = type("Completed", (), {"returncode": 1, "stdout": output})()
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(orchestrator, "LOGS_DIR", Path(directory)), \
             patch.object(orchestrator.subprocess, "run", return_value=completed):
            code, log_path, summary = orchestrator.run_validation(task, 2)
            self.assertEqual(1, code)
            self.assertLessEqual(len(summary), 12000)
            self.assertIn("compile error", summary)
            self.assertIn("ERP-010-validation-2.log", log_path)
            self.assertEqual(output.decode("utf-8"), (Path(directory) / "ERP-010-validation-2.log").read_text(encoding="utf-8"))

    def test_push_recovery_checkpoints_control_changes_before_pull(self):
        config = {
            "orchestrator_paths": [".ai/PROJECT_STATE.json", ".ai/audit.jsonl"],
            "ignored_change_paths": [".ai/logs/**"],
        }
        state = {"phase": "push_pending", "current_task": "ERP-020"}
        calls = []

        def run(command, **kwargs):
            calls.append(command)
            return type("Completed", (), {"returncode": 0})()

        with patch.object(orchestrator, "changed_paths", return_value=[".ai/PROJECT_STATE.json", ".ai/audit.jsonl"]), \
             patch.object(orchestrator, "audit"), \
             patch.object(orchestrator, "set_state"), \
             patch.object(orchestrator, "checkpoint_control_files") as checkpoint, \
             patch.object(orchestrator.subprocess, "run", side_effect=run):
            self.assertEqual(0, orchestrator.recover_push_pending(config, state))

        checkpoint.assert_any_call("chore: checkpoint pending push recovery state")
        self.assertEqual(["git", "pull", "--rebase"], calls[0])

    def test_push_recovery_refuses_non_control_changes(self):
        config = {
            "orchestrator_paths": [".ai/**"],
            "ignored_change_paths": [],
        }
        state = {"phase": "push_pending", "current_task": "ERP-020"}
        with patch.object(orchestrator, "changed_paths", return_value=["src/ERP.Api/Program.cs"]), \
             patch.object(orchestrator, "audit"), \
             patch.object(orchestrator, "set_state") as set_state, \
             patch.object(orchestrator, "checkpoint_control_files") as checkpoint, \
             patch.object(orchestrator.subprocess, "run") as run:
            self.assertEqual(5, orchestrator.recover_push_pending(config, state))

        set_state.assert_called_once()
        checkpoint.assert_not_called()
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
