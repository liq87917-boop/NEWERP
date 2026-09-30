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
            self.assertEqual("Automation Test", subprocess.run(
                ["git", "config", "user.name"], cwd=executor, check=True,
                capture_output=True, text=True,
            ).stdout.strip())
            self.assertEqual("", subprocess.run(
                ["git", "status", "--porcelain"], cwd=executor, check=True,
                capture_output=True, text=True,
            ).stdout.strip())

    def test_exhausted_dirty_task_is_parked_while_independent_queue_continues(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            control, executor = base / "NEWERP", base / "NEWERP.executor"
            control.mkdir()
            def git(cwd, *args):
                result = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                return result.stdout.strip()
            git(control, "init", "-b", "main")
            git(control, "config", "user.name", "Scheduler Test")
            git(control, "config", "user.email", "scheduler@example.invalid")
            (control / ".ai" / "tasks").mkdir(parents=True)
            (control / ".ai" / "config.json").write_text(json.dumps({
                "task_prefix": "ERP", "pipeline": {"executor_worktree": {
                    "enabled": True, "path": "NEWERP.executor", "target_branch": "main"}},
                "autonomy": {"max_supervised_recovery_cycles": 2},
            }), encoding="utf-8")
            (control / ".ai" / "PROJECT_STATE.json").write_text('{"phase":"ready"}', encoding="utf-8")
            (control / "src").mkdir()
            (control / "src" / "README.txt").write_text("committed source", encoding="utf-8")
            for number, status in ((167, "pending"), (168, "pending"), (169, "pending")):
                value = self.task(f"ERP-{number}", status, depends_on=["ERP-167"] if number == 168 else [])
                (control / ".ai" / "tasks" / f"ERP-{number}.json").write_text(
                    json.dumps(value), encoding="utf-8")
            git(control, "add", ".ai", "src")
            git(control, "commit", "-m", "queue fixture")
            git(base, "clone", "--quiet", str(control), str(executor))
            git(executor, "config", "user.name", "Scheduler Test")
            git(executor, "config", "user.email", "scheduler@example.invalid")
            source_tree = git(executor, "rev-parse", "HEAD:src")
            task_path = executor / ".ai" / "tasks" / "ERP-167.json"
            failed = self.task("ERP-167", "retry_pending") | {
                "supervised_recovery_cycles": 2,
                "exhausted_revalidation_source_tree": source_tree,
                "preserved_work": {"execution_copy": str(executor), "changed_paths": [
                    ".ai/tasks/ERP-167.json", "src/ERP.Api/Feature.cs"]},
            }
            task_path.write_text(json.dumps(failed), encoding="utf-8")
            work = executor / "src" / "ERP.Api" / "Feature.cs"
            work.parent.mkdir(parents=True)
            work.write_text("unfinished feature", encoding="utf-8")
            outcome = executor_workspace.continue_after_exhausted_failure(control, executor)
            self.assertEqual("continued", outcome["status"])
            self.assertEqual("ERP-169", outcome["next_task"])
            self.assertEqual("unfinished feature", work.read_text(encoding="utf-8"))
            next_root = Path(outcome["path"])
            parked = json.loads((next_root / ".ai" / "tasks" / "ERP-167.json").read_text(encoding="utf-8"))
            self.assertEqual("blocked", parked["status"])
            self.assertEqual(str(executor), parked["parked_execution_copy"])
            self.assertFalse((next_root / "src" / "ERP.Api" / "Feature.cs").exists())
            self.assertEqual(next_root, executor_workspace.selected_executor(control, executor))

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

    def test_deepseek_supervisor_collects_evidence_and_requeues_exhausted_task(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ai_dir = root / ".ai"
            tasks = ai_dir / "tasks"
            results = ai_dir / "results"
            logs = ai_dir / "logs"
            tasks.mkdir(parents=True); results.mkdir(); logs.mkdir()
            config_path = ai_dir / "config.json"
            state_path = ai_dir / "PROJECT_STATE.json"
            audit_path = ai_dir / "audit.jsonl"
            config = {
                "task_prefix": "ERP",
                "autonomy": {"enabled": True, "deepseek_supervisor_enabled": True, "max_supervised_recovery_cycles": 2},
            }
            config_path.write_text(json.dumps(config), encoding="utf-8")
            state_path.write_text(json.dumps({"phase": "ready"}), encoding="utf-8")
            blocked = self.task("ERP-096", "blocked") | {
                "attempts": 3,
                "blocker": "Automatic repair budget exhausted",
                "failure_kind": "path_guard_failure",
                "last_error": "outside allowed_paths: src/ERP.Domain/Entities/InventoryDocuments.cs",
                "human_gate": {"level": "L1", "required": False, "status": "not_required"},
            }
            (tasks / "ERP-096.json").write_text(json.dumps(blocked), encoding="utf-8")
            (results / "ERP-096.json").write_text(json.dumps({"last_error": blocked["last_error"]}), encoding="utf-8")
            (logs / "ERP-096-attempt-3.jsonl").write_text("evidence", encoding="utf-8")
            old_values = (
                pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                pipeline.AUDIT_PATH, pipeline.RESULTS_DIR, pipeline.LOGS_DIR,
            )
            pipeline.ROOT, pipeline.TASKS_DIR = root, tasks
            pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH = config_path, state_path, audit_path
            pipeline.RESULTS_DIR, pipeline.LOGS_DIR = results, logs
            try:
                with patch.object(pipeline, "git_checkpoint"), patch.object(pipeline, "refresh_project_state"):
                    recovered = pipeline.recover_blocked_with_deepseek(config)
                value = json.loads((tasks / "ERP-096.json").read_text(encoding="utf-8"))
                self.assertEqual(["ERP-096"], recovered)
                self.assertEqual("retry", value["status"])
                self.assertEqual(1, value["supervised_recovery_cycles"])
                self.assertEqual("ERP-096-RECOVERY-1", value["recovery_context"]["remediation_task"])
                self.assertIn("ERP-096-attempt-3.jsonl", value["recovery_context"]["attempt_logs"][0])
                self.assertIn("different safe repair", value["recovery_context"]["instruction"])
            finally:
                (
                    pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                    pipeline.AUDIT_PATH, pipeline.RESULTS_DIR, pipeline.LOGS_DIR,
                ) = old_values

    def test_failure_scan_runs_even_when_four_development_tasks_already_exist(self):
        config = {"autonomy": {"enabled": True, "deepseek_supervisor_enabled": True}}
        with patch.object(pipeline, "pipeline_lock") as lock, \
             patch.object(pipeline, "load_json", side_effect=[config, {"conversation_control": {}}]), \
             patch.object(pipeline, "recover_obsolete_transport_failures"), \
             patch.object(pipeline, "reconcile_completed_results"), \
             patch.object(pipeline, "recover_blocked_with_deepseek") as recover, \
             patch.object(pipeline, "queue_head", return_value=(None, "queue_empty")), \
             patch.object(pipeline, "mark_queue_replenishing"):
            lock.return_value.__enter__.return_value = None
            self.assertEqual(0, pipeline.run_all())
        recover.assert_called_once_with(config)

    def test_completed_result_prevents_stale_task_reactivation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ai_dir = root / ".ai"
            tasks = ai_dir / "tasks"
            results = ai_dir / "results"
            tasks.mkdir(parents=True); results.mkdir()
            config_path = ai_dir / "config.json"
            state_path = ai_dir / "PROJECT_STATE.json"
            audit_path = ai_dir / "audit.jsonl"
            config_path.write_text(json.dumps({"task_prefix": "ERP"}), encoding="utf-8")
            state_path.write_text(json.dumps({"phase": "developing", "current_task": "ERP-096"}), encoding="utf-8")
            (tasks / "ERP-096.json").write_text(json.dumps(self.task("ERP-096", "in_progress")), encoding="utf-8")
            (results / "ERP-096.json").write_text(json.dumps({"task": "ERP-096", "status": "completed"}), encoding="utf-8")
            old_values = (
                pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                pipeline.AUDIT_PATH, pipeline.RESULTS_DIR,
            )
            pipeline.ROOT, pipeline.TASKS_DIR = root, tasks
            pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH = config_path, state_path, audit_path
            pipeline.RESULTS_DIR = results
            try:
                with patch.object(pipeline, "git_checkpoint"), patch.object(pipeline, "refresh_project_state"):
                    self.assertEqual(["ERP-096"], pipeline.reconcile_completed_results({"task_prefix": "ERP"}))
                value = json.loads((tasks / "ERP-096.json").read_text(encoding="utf-8"))
                self.assertEqual("completed", value["status"])
            finally:
                (
                    pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                    pipeline.AUDIT_PATH, pipeline.RESULTS_DIR,
                ) = old_values

    def test_scheduler_process_error_retries_instead_of_stopping_pipeline(self):
        config = {"autonomy": {"enabled": True, "deepseek_supervisor_enabled": True}}
        task = (Path("ERP-102.json"), {"id": "ERP-102"})
        failed = type("Completed", (), {"returncode": 1})()
        passed = type("Completed", (), {"returncode": 0})()
        with patch.object(pipeline, "pipeline_lock") as lock, \
             patch.object(pipeline, "load_json", side_effect=[config, {"conversation_control": {}}, {"conversation_control": {}}, {"conversation_control": {}}]), \
             patch.object(pipeline, "recover_obsolete_transport_failures"), \
             patch.object(pipeline, "reconcile_completed_results"), \
             patch.object(pipeline, "recover_blocked_with_deepseek"), \
             patch.object(pipeline, "queue_head", side_effect=[(task, "ready"), (task, "ready"), (None, "queue_empty")]), \
             patch.object(pipeline, "run", side_effect=[failed, passed]), \
             patch.object(pipeline, "audit"), \
             patch.object(pipeline, "mark_queue_replenishing"), \
             patch.object(pipeline.time, "sleep") as sleep:
            lock.return_value.__enter__.return_value = None
            self.assertEqual(0, pipeline.run_all())
        sleep.assert_called_once_with(1)

    def test_dirty_worktree_failure_stops_without_tight_retry(self):
        config = {"autonomy": {"enabled": True}}
        task = (Path("ERP-168.json"), {"id": "ERP-168"})
        failed = type("Completed", (), {"returncode": 5})()
        with patch.object(pipeline, "pipeline_lock") as lock, \
             patch.object(pipeline, "load_json", side_effect=[config, {"conversation_control": {}}]), \
             patch.object(pipeline, "recover_obsolete_transport_failures"), \
             patch.object(pipeline, "reconcile_completed_results"), \
             patch.object(pipeline, "recover_blocked_with_deepseek"), \
             patch.object(pipeline, "queue_head", return_value=(task, "ready")), \
             patch.object(pipeline, "run", return_value=failed) as run, \
             patch.object(pipeline, "audit"), \
             patch.object(pipeline.time, "sleep") as sleep:
            lock.return_value.__enter__.return_value = None
            self.assertEqual(5, pipeline.run_all())
        run.assert_called_once()
        sleep.assert_not_called()

    def test_failed_and_retry_pending_tasks_become_bounded_deepseek_repairs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ai_dir = root / ".ai"
            tasks = ai_dir / "tasks"
            results = ai_dir / "results"
            logs = ai_dir / "logs"
            tasks.mkdir(parents=True); results.mkdir(); logs.mkdir()
            config_path = ai_dir / "config.json"
            state_path = ai_dir / "PROJECT_STATE.json"
            audit_path = ai_dir / "audit.jsonl"
            config = {
                "task_prefix": "ERP",
                "autonomy": {"enabled": True, "deepseek_supervisor_enabled": True, "max_supervised_recovery_cycles": 1},
            }
            config_path.write_text(json.dumps(config), encoding="utf-8")
            state_path.write_text(json.dumps({
                "last_build": {"task": "ERP-201", "status": "failed", "log": ".ai/logs/ERP-201-validation-3.log"},
                "last_error": {"task": "ERP-201", "kind": "validation", "summary": "compile failed"},
            }), encoding="utf-8")
            for task_id, status in (("ERP-201", "failed"), ("ERP-202", "retry_pending")):
                value = self.task(task_id, status) | {"last_error": f"{task_id} error"}
                (tasks / f"{task_id}.json").write_text(json.dumps(value), encoding="utf-8")
            for number in range(203, 207):
                value = self.task(f"ERP-{number}", "pending")
                (tasks / f"ERP-{number}.json").write_text(json.dumps(value), encoding="utf-8")
            (logs / "ERP-201-validation-3.log").write_text("CS1002 ; expected", encoding="utf-8")
            old_values = (
                pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                pipeline.AUDIT_PATH, pipeline.RESULTS_DIR, pipeline.LOGS_DIR,
            )
            pipeline.ROOT, pipeline.TASKS_DIR = root, tasks
            pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH = config_path, state_path, audit_path
            pipeline.RESULTS_DIR, pipeline.LOGS_DIR = results, logs
            try:
                with patch.object(pipeline, "git_checkpoint"), patch.object(pipeline, "refresh_project_state"):
                    recovered = pipeline.recover_blocked_with_deepseek(config)
                self.assertEqual(["ERP-201", "ERP-202"], recovered)
                failed = json.loads((tasks / "ERP-201.json").read_text(encoding="utf-8"))
                self.assertEqual("retry", failed["status"])
                self.assertEqual("failed", failed["recovery_context"]["latest_build"]["status"])
                self.assertIn("ERP-201-validation-3.log", failed["recovery_context"]["validation_logs"][0])
                pending = json.loads((tasks / "ERP-202.json").read_text(encoding="utf-8"))
                self.assertEqual("retry", pending["status"])
                self.assertEqual(1, pending["supervised_recovery_cycles"])
                pending["status"] = "failed"
                (tasks / "ERP-202.json").write_text(json.dumps(pending), encoding="utf-8")
                with patch.object(pipeline, "git_checkpoint"), patch.object(pipeline, "refresh_project_state"):
                    self.assertEqual([], pipeline.recover_blocked_with_deepseek(config))
            finally:
                (
                    pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                    pipeline.AUDIT_PATH, pipeline.RESULTS_DIR, pipeline.LOGS_DIR,
                ) = old_values

    def test_in_progress_and_finalizing_tasks_are_recovered_before_pending_work(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ai_dir = root / ".ai"
            tasks = ai_dir / "tasks"
            results = ai_dir / "results"
            logs = ai_dir / "logs"
            tasks.mkdir(parents=True); results.mkdir(); logs.mkdir()
            config_path = ai_dir / "config.json"
            state_path = ai_dir / "PROJECT_STATE.json"
            audit_path = ai_dir / "audit.jsonl"
            config = {
                "task_prefix": "ERP",
                "autonomy": {"enabled": True, "deepseek_supervisor_enabled": True, "max_supervised_recovery_cycles": 2},
            }
            config_path.write_text(json.dumps(config), encoding="utf-8")
            state_path.write_text(json.dumps({"phase": "ready"}), encoding="utf-8")
            for task_id, status in (("ERP-301", "in_progress"), ("ERP-302", "finalizing")):
                (tasks / f"{task_id}.json").write_text(json.dumps(self.task(task_id, status)), encoding="utf-8")
            old_values = (
                pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                pipeline.AUDIT_PATH, pipeline.RESULTS_DIR, pipeline.LOGS_DIR,
            )
            pipeline.ROOT, pipeline.TASKS_DIR = root, tasks
            pipeline.CONFIG_PATH, pipeline.STATE_PATH, pipeline.AUDIT_PATH = config_path, state_path, audit_path
            pipeline.RESULTS_DIR, pipeline.LOGS_DIR = results, logs
            try:
                with patch.object(pipeline, "git_checkpoint"), patch.object(pipeline, "refresh_project_state"):
                    self.assertEqual(["ERP-301", "ERP-302"], pipeline.recover_blocked_with_deepseek(config))
                self.assertEqual("retry", json.loads((tasks / "ERP-301.json").read_text(encoding="utf-8"))["status"])
                self.assertEqual("retry", json.loads((tasks / "ERP-302.json").read_text(encoding="utf-8"))["status"])
            finally:
                (
                    pipeline.ROOT, pipeline.TASKS_DIR, pipeline.CONFIG_PATH, pipeline.STATE_PATH,
                    pipeline.AUDIT_PATH, pipeline.RESULTS_DIR, pipeline.LOGS_DIR,
                ) = old_values

    def test_dirty_execution_copy_is_claimed_by_its_preserved_task(self):
        task = self.task("ERP-096", "retry") | {
            "preserved_work": {"changed_paths": ["src/ERP.Api/Program.cs"]},
        }
        with patch.object(orchestrator, "all_tasks", return_value=[(Path("ERP-096.json"), task)]), \
             patch.object(orchestrator, "business_changed_paths", return_value=["src/ERP.Api/Program.cs"]), \
             patch.object(orchestrator, "validate_task"):
            item = orchestrator.recoverable_dirty_task({}, {"current_task": None})
        self.assertEqual("ERP-096", item[1]["id"])

    def test_exhausted_preserved_work_revalidates_only_once_per_source_tree(self):
        task = self.task("ERP-167", "retry_pending") | {
            "preserved_work": {"changed_paths": ["src/ERP.Api/Program.cs"]},
            "supervised_recovery_cycles": 2,
            "exhausted_revalidation_source_tree": "oldtree",
        }
        config = {"autonomy": {"max_supervised_recovery_cycles": 2}}
        with patch.object(orchestrator, "all_tasks", return_value=[(Path("ERP-167.json"), task)]), \
             patch.object(orchestrator, "business_changed_paths", return_value=["src/ERP.Api/Program.cs"]), \
             patch.object(orchestrator, "validate_task"), \
             patch.object(orchestrator, "run", return_value=type("Result", (), {"stdout": "newtree\n"})()):
            self.assertIsNotNone(orchestrator.recoverable_dirty_task(config, {"current_task": None}))
            task["exhausted_revalidation_source_tree"] = "newtree"
            self.assertIsNone(orchestrator.recoverable_dirty_task(config, {"current_task": None}))

    def test_recovery_evidence_prefers_active_executor_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            active = root / "active"; control = root / "control"
            (active / ".ai" / "logs").mkdir(parents=True)
            (control / ".ai" / "logs").mkdir(parents=True)
            (active / ".ai" / "logs" / "ERP-167-validation-3.log").write_text("current failure", encoding="utf-8")
            (control / ".ai" / "logs" / "ERP-167-validation-1.log").write_text("old failure", encoding="utf-8")
            task = self.task("ERP-167", "retry_pending") | {"failure_kind": "validation_failure"}
            with patch.object(pipeline, "ROOT", active), \
                 patch.object(pipeline, "LOGS_DIR", active / ".ai" / "logs"), \
                 patch.object(pipeline, "RESULTS_DIR", active / ".ai" / "results"), \
                 patch.object(pipeline, "TASKS_DIR", active / ".ai" / "tasks"), \
                 patch.object(pipeline, "STATE_PATH", active / ".ai" / "PROJECT_STATE.json"), \
                 patch.dict("os.environ", {"AI_CONTROL_ROOT": str(control)}):
                evidence = pipeline.recovery_evidence(task)
            self.assertEqual([".ai/logs/ERP-167-validation-3.log"], evidence["validation_logs"])

    def test_confirmed_baseline_failure_grants_only_failing_unit_test(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            logs = root / ".ai" / "logs"; logs.mkdir(parents=True)
            source = root / "src" / "ERP.UnitTests" / "DynamicSupplierAgingReportTests.cs"
            source.parent.mkdir(parents=True); source.write_text("test fixture", encoding="utf-8")
            name = "ERP.UnitTests.DynamicSupplierAgingReportTests.Preview_retains_separate_currencies_and_unknown_due_date_evidence"
            failure = f"[xUnit.net 00:00:01] {name} [FAIL]\nAssert.Equal() Failure: Values differ\nExpected: not_due\nActual: overdue_1_30\n at Test in {source}:line 308\n"
            (logs / "ERP-167-validation-3.log").write_text(failure, encoding="utf-8")
            evidence = {"failure_kind": "validation_failure", "latest_build": {"log": ".ai/logs/ERP-167-validation-3.log"}}
            task = self.task("ERP-167", "retry_pending")
            task["allowed_paths"] = ["src/ERP.Api/Controllers/Feature.cs"]
            responses = [
                type("Result", (), {"returncode": 0, "stdout": "tracked\n"})(),
                type("Result", (), {"returncode": 0, "stdout": ""})(),
                type("Result", (), {"returncode": 0, "stdout": "abc123\n"})(),
            ]
            baseline = type("Result", (), {"returncode": 1, "stdout": failure})()
            with patch.object(pipeline, "ROOT", root), patch.object(pipeline, "LOGS_DIR", logs), \
                 patch.object(pipeline, "run", side_effect=responses), \
                 patch.object(pipeline.subprocess, "run", side_effect=[
                     type("Result", (), {"returncode": 0})(),
                     type("Result", (), {"returncode": 0})(), baseline,
                 ]) as commands:
                confirmed = pipeline.confirmed_baseline_unit_failure(task, evidence)
            self.assertEqual("src/ERP.UnitTests/DynamicSupplierAgingReportTests.cs", confirmed["path"])
            self.assertEqual(name, confirmed["test"])
            self.assertEqual(3, commands.call_count)
            config = {"ignored_change_paths": [], "orchestrator_paths": [], "protected_paths": []}
            task["recovery_context"] = {"baseline_test_fix": confirmed, "repair_allowed_paths": [confirmed["path"]]}
            with patch.object(orchestrator, "changed_paths", return_value=[confirmed["path"]]):
                self.assertEqual([], orchestrator.path_violations(task, config))
            task["recovery_context"]["repair_allowed_paths"] = ["src/ERP.UnitTests/**"]
            with patch.object(orchestrator, "changed_paths", return_value=[confirmed["path"]]):
                self.assertIn("outside allowed_paths", orchestrator.path_violations(task, config)[0])

    def test_failed_work_is_preserved_without_stash_or_reset(self):
        task = self.task("ERP-096", "in_progress")
        config = {"pipeline": {"prompt_transport_version": 2}}
        state = {"phase": "repairing", "current_task": "ERP-096"}
        with tempfile.TemporaryDirectory() as directory, \
             patch.object(orchestrator, "RESULTS_DIR", Path(directory)), \
             patch.object(orchestrator, "write_recovery_diff", return_value=(".ai/logs/ERP-096-preserved-work.diff", ["src/x.cs"])), \
             patch.object(orchestrator, "save_json") as save, \
             patch.object(orchestrator, "set_state") as set_state, \
             patch.object(orchestrator, "audit") as audit, \
             patch.object(orchestrator.subprocess, "run") as run:
            self.assertEqual(0, orchestrator.preserve_failed_work(
                Path("ERP-096.json"), task, config, state, "compile failed",
                "validation_failure", "error", 3,
            ))
        self.assertEqual("retry_pending", task["status"])
        self.assertEqual(["src/x.cs"], task["preserved_work"]["changed_paths"])
        run.assert_not_called()
        self.assertGreaterEqual(save.call_count, 2)
        set_state.assert_called_once()
        audit.assert_called_once()

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

    def test_local_autonomy_can_disable_remote_push(self):
        with patch.dict(orchestrator.os.environ, {"AI_DISABLE_PUSH": "1"}):
            self.assertFalse(orchestrator.auto_push_enabled({"auto_push": True}))
        with patch.dict(orchestrator.os.environ, {}, clear=True):
            self.assertTrue(orchestrator.auto_push_enabled({"auto_push": True}))

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
