import json
from decimal import Decimal
from itertools import product
from pathlib import Path
import subprocess
import sys
import unittest
from threading import Event
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import check_gas_swing_unit_numerics as unit_oracle
import check_gas_swing as workload
import gas_swing_reference as reference
import run_preview


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

    def test_worker_rss_rejects_live_sample_failures_and_reaps_child(self):
        valid = subprocess.CompletedProcess(['ps'], 0, '200 R')
        failures = [
            subprocess.CompletedProcess(['ps'], 1, ''),
            subprocess.CompletedProcess(['ps'], 1, '200 R'),
            subprocess.CompletedProcess(['ps'], 0, ''),
            subprocess.CompletedProcess(['ps'], 0, '0 S'),
            subprocess.CompletedProcess(['ps'], 0, '0 ?'),
            subprocess.CompletedProcess(['ps'], 0, '0 UNKNOWN_STATE'),
            subprocess.CompletedProcess(['ps'], 0, '-1 ?E'),
            subprocess.CompletedProcess(['ps'], 0, 'unreadable Z'),
            subprocess.CompletedProcess(['ps'], 0, '100'),
            OSError('ps unavailable'),
        ]
        # Once a worker has had pages, a zero reading from a runnable or waiting one is a lost reading.
        lost = [subprocess.CompletedProcess(['ps'], 0, '0 R'), subprocess.CompletedProcess(['ps'], 0, '0 D')]
        for prefix, failure in [*product(([], [valid]), failures), *product(([valid],), lost)]:
            with self.subTest(previous_samples=len(prefix), failure=failure):
                process = Mock(pid=123, returncode=0)
                # A final valid sample would let a worker with a lost reading qualify.
                readings = [*prefix, failure, valid]
                def sample(*args, **kwargs):
                    reading = readings.pop(0)
                    if isinstance(reading, Exception):
                        raise reading
                    return reading
                process.poll.side_effect = lambda: None if readings else 0
                with patch.object(subprocess, 'Popen', return_value=process), \
                     patch.object(subprocess, 'run', side_effect=sample), patch('time.sleep'):
                    with self.assertRaises((RuntimeError, OSError)):
                        run_preview.measure_worker(['worker'], None, {}, 1)
                self.assertEqual([valid], readings)
                process.kill.assert_called_once()
                process.wait.assert_called_once()

    def test_worker_rss_start_race_accepts_zero_until_the_worker_first_runs(self):
        # A started process has no resident pages until it runs: Linux reports it runnable (R) or waiting on its image (D).
        for state in ('R', 'D'):
            with self.subTest(state=state):
                process = Mock(pid=123, returncode=0)
                readings = [subprocess.CompletedProcess(['ps'], 0, reading)
                            for reading in (f'0 {state}', f'0 {state}', '128 S', '256 R')]
                process.poll.side_effect = lambda: None if readings else 0
                with patch.object(subprocess, 'Popen', return_value=process), \
                     patch.object(subprocess, 'run', side_effect=lambda *a, **kw: readings.pop(0)), patch('time.sleep'):
                    self.assertEqual((128, 256), run_preview.measure_worker(['worker'], None, {}, 1))
                process.kill.assert_not_called()
                process.wait.assert_called_once()
        # A worker that never reports pages has no measurement.
        process = Mock(pid=123, returncode=0)
        readings = [subprocess.CompletedProcess(['ps'], 0, '0 R'), subprocess.CompletedProcess(['ps'], 0, '0 R')]
        process.poll.side_effect = lambda: None if readings else 0
        with patch.object(subprocess, 'Popen', return_value=process), \
             patch.object(subprocess, 'run', side_effect=lambda *a, **kw: readings.pop(0)), patch('time.sleep'):
            with self.assertRaisesRegex(RuntimeError, 'positive RSS'):
                run_preview.measure_worker(['worker'], None, {}, 1)

    def test_worker_rss_exit_race_requires_a_positive_sample_and_reaps(self):
        # A vanished PID returns no match. A worker can lose its pages in exit before waitpid is ready.
        exit_readings = [(1, ''), (0, '0 ?E'), (0, '0 Z')]
        for first, (status, output) in product(([], [128, 256]), exit_readings):
            with self.subTest(first=first, status=status, output=output):
                process = Mock(pid=123, returncode=0)
                readings = [subprocess.CompletedProcess(['ps'], 0, f'{value} R') for value in first]
                readings.append(subprocess.CompletedProcess(['ps'], status, output))
                if status == 0:
                    readings.append(subprocess.CompletedProcess(['ps'], 0, '0 Z'))
                process.poll.side_effect = lambda: None if readings else 0
                with patch.object(subprocess, 'Popen', return_value=process), \
                     patch.object(subprocess, 'run', side_effect=lambda *a, **kw: readings.pop(0)), patch('time.sleep'):
                    if first:
                        self.assertEqual((128, 256), run_preview.measure_worker(['worker'], None, {}, 1))
                    else:
                        with self.assertRaisesRegex(RuntimeError, 'positive RSS'):
                            run_preview.measure_worker(['worker'], None, {}, 1)
                process.kill.assert_not_called()
                process.wait.assert_called_once()

    def test_ps_error_at_exit_is_not_a_vanished_pid(self):
        process = Mock(pid=123, returncode=0)
        reads = [subprocess.CompletedProcess(['ps'], 0, '128 R', ''),
                 subprocess.CompletedProcess(['ps'], 1, '', 'cannot inspect processes')]
        process.poll.side_effect = lambda: None if reads else 0
        with patch.object(subprocess, 'Popen', return_value=process), \
             patch.object(subprocess, 'run', side_effect=lambda *a, **kw: reads.pop(0)), patch('time.sleep'):
            with self.assertRaisesRegex(RuntimeError, 'Invalid worker RSS'):
                run_preview.measure_worker(['worker'], None, {}, 1)
        process.kill.assert_not_called()
        process.wait.assert_called_once()

    def test_worker_process_timeout_and_unsuccessful_exit_are_reaped(self):
        process = Mock(pid=123, returncode=None)
        process.poll.return_value = None
        with patch.object(subprocess, 'Popen', return_value=process), \
             patch('time.monotonic', side_effect=[10, 12]):
            with self.assertRaisesRegex(RuntimeError, 'did not finish'):
                run_preview.measure_worker(['worker'], None, {}, 1)
        process.kill.assert_called_once()
        process.wait.assert_called_once()

        process = Mock(pid=123, returncode=9)
        process.poll.return_value = 9
        with patch.object(subprocess, 'Popen', return_value=process):
            with self.assertRaises(subprocess.CalledProcessError) as error:
                run_preview.measure_worker(['worker'], None, {}, 1)
        self.assertEqual(9, error.exception.returncode)
        process.kill.assert_not_called()
        process.wait.assert_called_once()

    def test_sample_error_reaps_a_real_owned_worker(self):
        process = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(10)'])
        try:
            with patch.object(subprocess, 'Popen', return_value=process), \
                 patch.object(subprocess, 'run', side_effect=OSError('sampling failed')):
                with self.assertRaisesRegex(OSError, 'sampling failed'):
                    run_preview.measure_worker(process.args, None, {}, 1)
            self.assertIsNotNone(process.returncode)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait()

    def test_zero_worker_rss_cannot_qualify(self):
        with self.assertRaisesRegex(AssertionError, 'price'):
            workload.require_host_memory(1, {'price':0})

    def test_native_cancellation_duration_boundary(self):
        self.assertEqual(1, workload.cancellation_elapsed(100, 101))
        with self.assertRaisesRegex(AssertionError, 'cancellation'):
            workload.cancellation_elapsed(100, 101.001)

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
