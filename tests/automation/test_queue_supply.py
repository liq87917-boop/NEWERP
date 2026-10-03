import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('queue_supply', ROOT / 'scripts/ai_pipeline.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class QueueSupplyTests(unittest.TestCase):
    def snapshot(self, tasks, state=None, previous=None, idle=True):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'tasks').mkdir()
            (root / 'logs').mkdir()
            config = {'task_prefix': 'ERP', 'queue_target_size': 4,
                      'rolling_queue': {'enabled': True, 'low_watermark': 2,
                                        'replenishment_owner': 'ChatGPT'}}
            (root / 'config.json').write_text(json.dumps(config))
            (root / 'state.json').write_text(json.dumps(state or {'phase': 'remote_degraded'}))
            for task in tasks:
                (root / 'tasks' / (task['id'] + '.json')).write_text(json.dumps(task))
            if previous:
                (root / 'logs/queue-replenishment.json').write_text(json.dumps(previous))
            with patch.multiple(m, ROOT=root, CONFIG_PATH=root/'config.json',
                                STATE_PATH=root/'state.json', TASKS_DIR=root/'tasks', LOGS_DIR=root/'logs'):
                value = m.mark_queue_replenishing(idle=idle)
                updated = json.loads((root/'state.json').read_text())
                self.assertEqual(value, json.loads((root/'logs/queue-replenishment.json').read_text()))
                return value, updated

    def test_exhausted_failure_still_requests_supply_preserving_remote_evidence(self):
        v, s = self.snapshot([{'id':'ERP-190', 'status':'blocked', 'supervised_recovery_cycles':2}],
                             {'phase':'remote_degraded', 'git_sync':{'status':'pending', 'ahead':25, 'behind':3}})
        self.assertEqual((v['requested_count'], v['owner']), (4, 'ChatGPT'))
        self.assertEqual(v['failures'][0]['recovery_cycles'], 2)
        self.assertEqual(s['phase'], 'replenishing')
        self.assertEqual(s['git_sync']['behind'], 3)

    def test_active_current_task_counts_once_and_does_not_reset_runner(self):
        state = {'phase':'developing', 'current_task':'ERP-277', 'runner':{'pid':123}}
        v, s = self.snapshot([{'id':'ERP-277','status':'in_progress'}], state)
        self.assertEqual(v['effective_count'], 1)
        self.assertEqual(v['requested_count'], 3)
        self.assertEqual(s, state)

    def test_excluded_statuses_never_inflate_watermark(self):
        statuses = ['failed','error','retry_pending','deferred','completed','skipped','superseded']
        v, _ = self.snapshot([{'id':f'ERP-{i}', 'status':status} for i,status in enumerate(statuses)])
        self.assertEqual(v['effective_count'], 0)
        self.assertEqual(v['requested_count'], 4)

    def test_overdue_request_does_not_reset_on_repeated_poll(self):
        since = '2026-01-01T00:00:00Z'
        v, _ = self.snapshot([], previous={'required':True, 'requested_at':since})
        self.assertEqual(v['requested_at'], since)
        self.assertTrue(v['overdue'])

    def test_sufficient_queue_resolves_previous_request(self):
        v, _ = self.snapshot([{'id':'ERP-277','status':'pending'}, {'id':'ERP-278','status':'code_ready'}],
                             previous={'required':True, 'requested_at':'2026-01-01T00:00:00Z'})
        self.assertFalse(v['required'])
        self.assertEqual(v['requested_count'], 0)
        self.assertIsNone(v['requested_at'])

    def test_human_gate_and_pause_state_never_overwritten(self):
        state = {'phase':'waiting_human_gate', 'conversation_control':{'paused':True}}
        _, s = self.snapshot([], state)
        self.assertEqual(s, state)


if __name__ == '__main__':
    unittest.main()
