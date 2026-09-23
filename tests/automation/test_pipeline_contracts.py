from __future__ import annotations

import importlib.util
import json
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

    def test_first_blocked_task_is_not_skipped(self):
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
                self.assertIsNone(item)
                self.assertIn("ERP-010 status=blocked stops queue", reason)
            finally:
                pipeline.TASKS_DIR, pipeline.CONFIG_PATH = old_tasks, old_config

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
                self.assertIn("waits for human_gate=L3", reason)
            finally:
                pipeline.TASKS_DIR, pipeline.CONFIG_PATH = old_tasks, old_config

    def test_cline_finish_reason_remains_raw_metadata(self):
        value = {"result": {"finishReason": "tool_use"}}
        self.assertEqual("tool_use", orchestrator.nested_finish_reason(value))

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


if __name__ == "__main__":
    unittest.main()
