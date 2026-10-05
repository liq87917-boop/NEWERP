from __future__ import annotations
import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "scripts" / "ai_orchestrator.py"
spec = importlib.util.spec_from_file_location("orchestrator_lease_contract", SOURCE)
orchestrator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(orchestrator)

class OrchestratorLeaseContracts(unittest.TestCase):
    def test_other_process_cannot_enter_dirty_recovery_and_release_allows_resume(self):
        with tempfile.TemporaryDirectory() as directory:
            logs = Path(directory)
            child = ("import importlib.util; from pathlib import Path; "
                     f"s=importlib.util.spec_from_file_location('lease_child',{str(SOURCE)!r}); "
                     "m=importlib.util.module_from_spec(s); s.loader.exec_module(m); "
                     f"m.LOGS_DIR=Path({str(logs)!r}); "
                     "m.run_next_owned=lambda dry: 23; raise SystemExit(m.run_next(False))")
            with patch.object(orchestrator, "LOGS_DIR", logs):
                with orchestrator.execution_lease() as acquired:
                    self.assertTrue(acquired)
                    result = subprocess.run([sys.executable, "-B", "-c", child], capture_output=True, text=True, timeout=20)
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("owns the execution lease", result.stdout)
                result = subprocess.run([sys.executable, "-B", "-c", child], capture_output=True, text=True, timeout=20)
                self.assertEqual(23, result.returncode, result.stderr)

    def test_exception_releases_lease(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(orchestrator, "LOGS_DIR", Path(directory)):
            with patch.object(orchestrator, "run_next_owned", side_effect=ValueError("diagnostic")):
                with self.assertRaisesRegex(ValueError, "diagnostic"):
                    orchestrator.run_next(False)
            with orchestrator.execution_lease() as acquired:
                self.assertTrue(acquired)
