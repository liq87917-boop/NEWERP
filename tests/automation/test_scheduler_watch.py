import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]

def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value

p = module('recovery_policy', 'ai_pipeline.py')
w = module('scheduler_watch', 'ai_scheduler_watch.py')

class RecoveryPolicyTests(unittest.TestCase):
    def test_blocked_engineering_error_with_nonstandard_wording_is_recoverable(self):
        self.assertTrue(p.automatic_recovery_allowed({'status':'blocked', 'blocker':'Compiler stopped',
                                                     'failure_kind':'validation_failure'}))

    def test_external_gates_and_deferrals_never_become_model_repairs(self):
        for kind in ['missing_credentials','provider_unavailable','environment_blocked','dependency_blocked']:
            self.assertFalse(p.automatic_recovery_allowed({'status':'failed','failure_kind':kind}))
        for status in ['deferred','completed','superseded']:
            self.assertFalse(p.automatic_recovery_allowed({'status':status,'failure_kind':'validation_failure'}))
        self.assertFalse(p.automatic_recovery_allowed({'status':'error','human_gate':{'level':'L4'}}))

class WatchTests(unittest.TestCase):
    def test_transient_exception_is_retried_then_success_resets_backoff(self):
        with tempfile.TemporaryDirectory() as d, patch.object(w,'cycle',side_effect=[OSError('locked'),{'status':'healthy'}]) as cycle, patch.object(w.time,'sleep',side_effect=[None,KeyboardInterrupt]):
            root=Path(d)
            with self.assertRaises(KeyboardInterrupt): w.watch(root, interval=1)
            self.assertEqual(2,cycle.call_count)
            saved=json.loads((root/'.ai/logs/scheduler-watch.json').read_text())
            self.assertEqual(('healthy',1),(saved['status'],saved['retry_in_seconds']))

    def test_duplicate_watcher_lock_prevents_second_writer(self):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'watch.lock'
            with w.watcher_lock(path):
                with self.assertRaises(OSError):
                    with w.watcher_lock(path): pass

    def test_user_pause_never_starts_a_child(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); (root/'.ai').mkdir()
            (root/'.ai/PROJECT_STATE.json').write_text(json.dumps({'conversation_control':{'paused':True}}))
            with patch.object(w,'executor_root',return_value=root),patch.object(w.subprocess,'run') as run:
                self.assertEqual('paused',w.cycle(root,root/'log')['status']);run.assert_not_called()

    def test_busy_pipeline_is_not_counted_as_failure(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); (root/'.ai').mkdir()
            (root/'.ai/PROJECT_STATE.json').write_text('{}')
            def busy(*a,**kw):
                kw['stderr']
                kw['stdout'].write('Another NEWERP automation pipeline is already running.')
                return subprocess.CompletedProcess([],1)
            with patch.object(w,'executor_root',return_value=root),patch.object(w.subprocess,'run',side_effect=busy):
                self.assertEqual('busy',w.cycle(root,root/'log')['status'])

if __name__ == '__main__': unittest.main()
