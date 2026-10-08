import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, Mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('console_panel', ROOT/'scripts/ai_console_panel.py')
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


class ConsolePanelTests(unittest.TestCase):
    def test_existing_viewer_is_restored_without_launching_or_touching_task_state(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            with patch.object(panel.os, 'name', 'nt'), patch.object(panel, 'Windows') as api, patch.object(panel.subprocess, 'Popen') as launch:
                api.return_value.request.return_value = True
                self.assertEqual('reused', panel.show_console_panel(root)['status'])
                launch.assert_not_called()
                self.assertEqual([], list(root.iterdir()))

    def test_new_viewer_has_its_own_visible_console_and_no_executor_command(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            with patch.object(panel.os, 'name', 'nt'), patch.object(panel, 'Windows') as api, patch.object(panel.subprocess, 'Popen') as launch, patch.object(panel.subprocess, 'CREATE_NEW_CONSOLE', 16, create=True):
                api.return_value.request.return_value = False
                launch.return_value.pid = 123
                self.assertEqual('launched', panel.show_console_panel(root)['status'])
                args, kwargs = launch.call_args
                self.assertIn('--root', args[0])
                self.assertNotIn('run-next', args[0])
                self.assertEqual(16, kwargs['creationflags'])

    def test_ui_launch_failure_is_nonfatal(self):
        with patch.object(panel.os, 'name', 'nt'), patch.object(panel, 'Windows', side_effect=OSError('denied')):
            self.assertEqual('unavailable', panel.show_console_panel(ROOT)['status'])

    def test_status_is_read_only_sanitized_and_never_prints_raw_errors(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); (root/'.ai').mkdir()
            path = root/'.ai/PROJECT_STATE.json'
            data = {'phase':'developing\x1b\n', 'current_task':'../../secret', 'last_error': 'SECRET', 'last_build':'invalid'}
            path.write_text(json.dumps(data), encoding='utf-8')
            before = path.read_bytes()
            rendered = '\n'.join(panel.status_lines(root))
            self.assertNotIn('SECRET', rendered)
            self.assertNotIn('\x1b', rendered)
            self.assertEqual(before, path.read_bytes())


if __name__ == '__main__':
    unittest.main()
