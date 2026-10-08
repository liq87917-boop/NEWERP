import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('scope_refresh',ROOT/'scripts/ai_orchestrator.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

class ScopeRefreshTests(unittest.TestCase):
    def test_runtime_save_retains_published_amendment_and_worker_progress(self):
        with tempfile.TemporaryDirectory() as d:
            tasks=Path(d);p=tasks/'ERP-344.json'
            p.write_text(json.dumps(dict(id='ERP-344',allowed_paths=['src/new.cs'],acceptance_criteria=['new'],status='in_progress',attempts=1)))
            stale=dict(id='ERP-344',allowed_paths=['src/old.cs'],acceptance_criteria=['old'],status='code_ready',attempts=2)
            with patch.object(m,'TASKS_DIR',tasks):m.save_json(p,stale)
            saved=json.loads(p.read_text())
            self.assertEqual(['src/new.cs'],saved['allowed_paths'])
            self.assertEqual(['new'],saved['acceptance_criteria'])
            self.assertEqual(('code_ready',2),(saved['status'],saved['attempts']))

    def test_path_guard_uses_current_scope_and_still_enforces_protection(self):
        with tempfile.TemporaryDirectory() as d:
            tasks=Path(d);(tasks/'ERP-344.json').write_text(json.dumps(dict(id='ERP-344',allowed_paths=['src/new.cs'])))
            stale=dict(id='ERP-344',allowed_paths=['src/old.cs'])
            config=dict(ignored_change_paths=[],orchestrator_paths=[],protected_paths=['src/new.cs'])
            with patch.object(m,'TASKS_DIR',tasks),patch.object(m,'changed_paths',return_value=['src/new.cs','src/unrelated.cs']),patch.object(m,'gate_is_approved',return_value=False):
                violations=m.path_violations(stale,config)
            self.assertIn('protected without approved gate: src/new.cs',violations)
            self.assertIn('outside allowed_paths: src/unrelated.cs',violations)
            self.assertNotIn('outside allowed_paths: src/new.cs',violations)

    def test_changed_identity_rejects_save_without_overwriting_evidence(self):
        with tempfile.TemporaryDirectory() as d:
            tasks=Path(d);p=tasks/'ERP-344.json';original=json.dumps(dict(id='ERP-345',allowed_paths=['src/new.cs']));p.write_text(original)
            with patch.object(m,'TASKS_DIR',tasks),self.assertRaises(ValueError):m.save_json(p,dict(id='ERP-344',allowed_paths=['src/old.cs']))
            self.assertEqual(original,p.read_text())
