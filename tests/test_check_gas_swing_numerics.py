import json
from decimal import Decimal
from pathlib import Path
import subprocess
import sys
import unittest
from threading import Event
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import check_gas_swing_unit_numerics as unit_oracle
import check_gas_swing as workload
import gas_swing_reference as reference


class SwingOracleChecks(unittest.TestCase):
    def test_wrong_unit_reference_value_fails_in_optimized_mode_too(self):
        def incorrect_solver(args, **_):
            cases = json.loads(Path(args[-2]).read_text())
            Path(args[-1]).write_text(json.dumps(['0'] * len(cases)))

        with patch.object(unit_oracle.subprocess, 'run', incorrect_solver), patch('builtins.print') as output:
            with self.assertRaisesRegex(AssertionError, 'Unit-reference swing finite value'):
                unit_oracle.run(Path('synthetic-check-program'))
        output.assert_not_called()

    def test_long_root_and_monthly_sum_refuse_wrong_values(self):
        with patch.object(reference,'long_reference',return_value=(Decimal(1),(Decimal(1),))):
            with self.assertRaisesRegex(AssertionError,'normalized root'):
                reference.check_long_value(365,'0')
        with patch.object(reference,'long_reference',return_value=(Decimal(1),(Decimal(2),))):
            with self.assertRaisesRegex(AssertionError,'Monthly optima'):
                reference.check_long_value(365,'1000')

    def test_interrupted_rss_sampler_invalidates_workload(self):
        sampled=Event()
        def failed_read(*args,**kwargs):
            sampled.set()
            raise OSError('sample unavailable')
        def finish(*args):
            self.assertTrue(sampled.wait(1))
            return [], {}
        with patch('subprocess.run',side_effect=failed_read), patch.object(workload,'workload_values',side_effect=finish):
            with self.assertRaisesRegex(OSError,'sample unavailable'):
                workload.workload_routes(None,None,None,None,1,90)

    def test_role_specific_host_rss_boundaries_in_kib(self):
        workload.require_host_memory(2_097_152,{'price':1_048_576,'volatility':1_048_576})
        with self.assertRaisesRegex(AssertionError,'API'):
            workload.require_host_memory(2_097_153,{'price':1})
        with self.assertRaisesRegex(AssertionError,'volatility'):
            workload.require_host_memory(1,{'price':1_048_576,'volatility':1_048_577})

    def test_direct_swing_preview_refuses_optimized_mode_before_checking(self):
        tools = Path(__file__).resolve().parents[1] / 'tools'
        sentinel = ['--pg-bin','/missing/synthetic','--dotnet','/missing/synthetic/dotnet']
        commands = [
            [tools/'run_preview.py','--gas-swing-check',*sentinel],
            [tools/'run_preview.py','--gas-swing-workload-check',*sentinel],
            [tools/'check_gas_swing.py','--workloads'],
        ]
        for command in commands:
            with self.subTest(command=command):
                result = subprocess.run([sys.executable,'-O',*map(str,command)],capture_output=True,text=True)
                self.assertNotEqual(0,result.returncode)
                self.assertIn('requires Python assertions',result.stderr)
                self.assertNotIn('PASS',result.stdout)
