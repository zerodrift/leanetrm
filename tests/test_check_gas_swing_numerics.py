import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import check_gas_swing_numerics as oracle


class SwingOracleChecks(unittest.TestCase):
    def test_incorrect_solver_values_fail_before_any_pass_message(self):
        def incorrect_solver(args, **_):
            Path(args[-1]).write_text(json.dumps(['0'] * 6))

        with patch.object(oracle.subprocess, 'run', incorrect_solver), patch('builtins.print') as output:
            with self.assertRaisesRegex(AssertionError, 'finite grid'):
                oracle.run(Path('synthetic-check-program'))
        output.assert_not_called()
