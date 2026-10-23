"""Interactive PPA example selection is explicit and loads the real fixture."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import run_preview

class PpaExampleTests(unittest.TestCase):
    def test_explicit_ppa_example_reaches_startup_without_running_checks(self):
        with patch.object(sys, 'argv', ['run_preview.py', '--pg-bin', '/synthetic/pg',
                '--dotnet', '/synthetic/dotnet', '--no-build', '--with-ppa']), \
             patch('check_standalone.main'), patch.object(Path, 'is_file', return_value=True), \
             patch.object(run_preview.signal, 'signal'), \
             patch.object(run_preview.subprocess, 'run', side_effect=RuntimeError('startup reached')):
            with self.assertRaisesRegex(RuntimeError, 'startup reached'):
                run_preview.main()

if __name__ == '__main__':
    unittest.main()
