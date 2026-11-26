import json
from pathlib import Path
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
