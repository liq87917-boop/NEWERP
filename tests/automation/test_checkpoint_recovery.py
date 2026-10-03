import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("checkpoint_orchestrator", ROOT / "scripts/ai_orchestrator.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
class CheckpointRecoveryTests(unittest.TestCase):
    def test_completed_owner_requires_passed_build_and_pending_task_file(self):
        with tempfile.TemporaryDirectory() as directory:
            p=Path(directory); tasks=p/'tasks'; results=p/'results';tasks.mkdir();results.mkdir()
            t={"id":"ERP-274","status":"completed"}
            (tasks/'ERP-274.json').write_text(json.dumps(t))
            (results/'ERP-274.json').write_text(json.dumps({"task":"ERP-274","status":"completed"}))
            state={"current_task":"ERP-275","last_build":{"task":"ERP-274","status":"passed","exit_code":0}}
            with patch.object(m,'TASKS_DIR',tasks),patch.object(m,'RESULTS_DIR',results),patch.object(m,'validate_task'),patch.object(m,'gate_is_approved',return_value=True),patch.object(m,'path_violations',return_value=[]),patch.object(m,'business_changed_paths',return_value=['src/a.cs']),patch.object(m,'changed_paths',return_value=['.ai/tasks/ERP-274.json','src/a.cs']) as dirty:
                self.assertEqual(m.recoverable_completed_checkpoint({},state)[1]['id'],'ERP-274')
                dirty.return_value=['src/a.cs']
                self.assertIsNone(m.recoverable_completed_checkpoint({},state))
                dirty.return_value=['.ai/tasks/ERP-274.json','src/a.cs']
                state['last_build']['status']='failed'
                self.assertIsNone(m.recoverable_completed_checkpoint({},state))
    def test_revalidation_failure_never_commits(self):
        state={}
        with patch.object(m,'recoverable_completed_checkpoint',return_value=(Path('task'),{'id':'ERP-274'})),patch.object(m,'checkpoint_content_signature',return_value='hash'),patch.object(m,'save_json'),patch.object(m,'audit'),patch.object(m,'run_validation',return_value=(1,'log','failed')),patch.object(m,'set_state'),patch.object(m,'run') as run:
            self.assertEqual(m.recover_completed_checkpoint({'autonomy':{}},state),8)
            run.assert_not_called()
    def test_recovery_budget_prevents_repeated_validation(self):
        state={'checkpoint_recovery':{'signature':'hash','cycles':2}}
        with patch.object(m,'recoverable_completed_checkpoint',return_value=(Path('task'),{'id':'ERP-274'})),patch.object(m,'checkpoint_content_signature',return_value='hash'),patch.object(m,'run_validation') as validate:
            self.assertEqual(m.recover_completed_checkpoint({'autonomy':{'max_supervised_recovery_cycles':2}},state),8)
            validate.assert_not_called()
    def test_content_changed_during_validation_never_commits(self):
        with patch.object(m,'recoverable_completed_checkpoint',return_value=(Path('task'),{'id':'ERP-274'})),patch.object(m,'checkpoint_content_signature',side_effect=['before','after']),patch.object(m,'save_json'),patch.object(m,'audit'),patch.object(m,'run_validation',return_value=(0,'log','ok')),patch.object(m,'path_violations',return_value=[]),patch.object(m,'set_state'),patch.object(m,'run') as run:
            self.assertEqual(m.recover_completed_checkpoint({'autonomy':{}},{}),8)
            run.assert_not_called()
