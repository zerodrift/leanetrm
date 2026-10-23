"""An interruption inside an owned required stage cannot qualify the gate."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import run_preview
from check_all import run_checks


class PreviewInterruptTests(unittest.TestCase):
    def test_required_stage_interrupt_stops_qualification_and_cleans_up(self):
        output = io.StringIO()
        commands = []
        # Interrupt the real preview entry point during initdb, before a database
        # can start. Real private-directory creation and cleanup still execute.
        def stage(command, **options):
            commands.append(command)
            if 'tools/run_preview.py' not in command:
                return
            with patch.object(sys, 'argv', ['run_preview.py', '--pg-bin', '/synthetic/pg',
                                           '--dotnet', '/synthetic/dotnet', '--no-build', '--check']), \
                 patch('check_standalone.main'), patch.object(Path, 'is_file', return_value=True), \
                 patch.object(run_preview.signal, 'signal'), \
                 patch.object(run_preview.subprocess, 'run', side_effect=KeyboardInterrupt):
                run_preview.main()
        with redirect_stdout(output):
            with self.assertRaises(KeyboardInterrupt):
                run_checks(Path('/synthetic/pg'), Path('/synthetic/dotnet'), Path('/synthetic/browser'), run=stage)
        self.assertIn('Removed owned disposable preview database.', output.getvalue())
        self.assertNotIn('All synthetic verification suites passed', output.getvalue())
        self.assertEqual(sum('tools/run_preview.py' in c for c in commands), 1)


if __name__ == '__main__':
    unittest.main()
