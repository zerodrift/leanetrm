"""Behavior checks for the local/CI verification entry point."""
from contextlib import redirect_stdout
import io
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from check_all import run_checks


class CheckAllTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(redirect_stdout(io.StringIO()))

    def test_every_executable_check_project_is_restored_and_run(self):
        expected = {
            str(project.relative_to(ROOT))
            for project in (ROOT / 'tests').glob('*/*.csproj')
            if ET.parse(project).findtext('.//OutputType') == 'Exe'
        }
        commands = []

        def run(command, **options):
            self.assertEqual(options['cwd'], ROOT)
            self.assertTrue(options['check'])
            self.assertFalse(options.get('shell', False))
            commands.append(command)

        run_checks(Path('/stock postgres/bin'), Path('/managed sdk/dotnet'),
                   Path('/browser executable'), run=run)
        restored = {c[c.index('restore') + 1] for c in commands if 'restore' in c}
        executed = {c[c.index('--project') + 1] for c in commands if '--project' in c}
        self.assertEqual(restored, expected | {'src/OpenEtrm.Worker/OpenEtrm.Worker.csproj'})
        self.assertEqual(executed, expected)
        schedule_commands = [c for c in commands if '--project' in c and 'Api.Checks' in c[c.index('--project') + 1]]
        self.assertEqual(len(schedule_commands), 3)
        self.assertEqual(schedule_commands[0][-2:], ['--', '--schedules'])
        self.assertEqual(schedule_commands[1][-2:], ['--', '--automation'])
        self.assertEqual(schedule_commands[2][-2:], ['--', '--preview-data'])
        self.assertTrue(any('build' in c and 'src/OpenEtrm.Worker/OpenEtrm.Worker.csproj' in c for c in commands))
        self.assertTrue(any('tools/check_automation.py' in c and '--dotnet' in c for c in commands))
        self.assertIn(['node','--test','tests/browser/owned-browser.test.mjs'],commands)
        self.assertTrue(any('--execution-check' in c for c in commands))
        self.assertTrue(any('--power-actual-check' in c for c in commands))
        self.assertTrue(any('--interval-series-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--interval-curves-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--contract-dates-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--swap-risk-holdings-check' in c for c in commands))
        self.assertTrue(any('--pilot-close-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--custom-curves-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--connected-portfolio-check' in c for c in commands))
        self.assertTrue(any('--market-dates-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--jobs-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('tools/check_reference_editing.py' in c for c in commands))
        self.assertTrue(any('tools/check_power_integrity.py' in c for c in commands))
        self.assertTrue(any('--reference-check' in c and '--browser-check' in c for c in commands))
        for command in commands:
            if '--pg-bin' in command:
                self.assertEqual(command[command.index('--pg-bin') + 1], '/stock postgres/bin')
        self.assertTrue(any('--browser-check' in command for command in commands))
        # Party race/revocation checks allocate their own disposable session pool.
        self.assertTrue(any('--party-check' in command and '--browser-check' in command for command in commands))
        # So does page-assistant administration: the standard run already spends the whole session pool.
        self.assertTrue(any('--assistant-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--unit-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--exchange-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--risk-jobs-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('mixed-gas' in command for command in commands))
        self.assertTrue(any('--hosted-check' in command and '--browser-check' in command for command in commands))

    def test_failed_browser_checks_stop_later_work_and_preserve_failure(self):
        commands = []

        def run(command, **options):
            commands.append(command)
            if '--browser-check' in command:
                raise subprocess.CalledProcessError(17, command)

        with self.assertRaises(subprocess.CalledProcessError) as failure:
            run_checks(Path('/postgres/bin'), Path('/sdk/dotnet'), Path('/browser'), run=run)
        self.assertEqual(failure.exception.returncode, 17)
        self.assertFalse(any('mixed-gas' in command for command in commands))

    def test_disabling_assertions_is_rejected_before_checks_start(self):
        with patch.dict(os.environ, {'PYTHONOPTIMIZE': '1'}):
            with self.assertRaisesRegex(ValueError, 'assertions'):
                run_checks(Path('/postgres/bin'), Path('/sdk/dotnet'), Path('/browser'),
                           run=lambda *args, **kwargs: self.fail('Started an unchecked run'))


if __name__ == '__main__':
    unittest.main()
