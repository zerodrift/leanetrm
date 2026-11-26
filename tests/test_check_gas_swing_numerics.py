import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import check_gas_swing_unit_numerics as unit_oracle


class SwingOracleChecks(unittest.TestCase):
    def test_wrong_unit_reference_value_fails_in_optimized_mode_too(self):
        def incorrect_solver(args, **_):
            cases = json.loads(Path(args[-2]).read_text())
            Path(args[-1]).write_text(json.dumps(['0'] * len(cases)))

        with patch.object(unit_oracle.subprocess, 'run', incorrect_solver), patch('builtins.print') as output:
            with self.assertRaisesRegex(AssertionError, 'Unit-reference swing finite value'):
                unit_oracle.run(Path('synthetic-check-program'))
        output.assert_not_called()

    def test_direct_swing_preview_refuses_optimized_mode_before_checking(self):
        runner = Path(__file__).resolve().parents[1] / 'tools/run_preview.py'
        result = subprocess.run([sys.executable, '-O', str(runner), '--gas-swing-check',
                                 '--pg-bin', '/missing/synthetic', '--dotnet', '/missing/synthetic/dotnet'],
                                capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('requires Python assertions', result.stderr)
        self.assertNotIn('PASS', result.stdout)
