"""Trading fixtures select their original market inputs across additional closes and history."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from seed_preview_positions import execution_frames, EXECUTIONS
from check_recorded_cash import working_frame_at


class PreviewExecutionFramesTests(unittest.TestCase):
    def workspace(self):
        frames = [{'id': str(i), 'at': day + 'T16:00:00Z'}
                  for i, (day, *_) in enumerate(EXECUTIONS)]
        return {'frames': frames, 'initialFrameIds': [row['id'] for row in frames]}

    def test_selects_only_the_explicit_trade_example_dates(self):
        workspace = self.workspace()
        extra = {'id': 'history', 'at': '2020-12-01T16:00:00Z'}
        workspace['frames'].append(extra)
        workspace['initialFrameIds'].append(extra['id'])
        self.assertEqual([frame['at'][:10] for frame in execution_frames(workspace)],
                         [row[0] for row in EXECUTIONS])

    def test_missing_duplicate_and_wrong_time_remain_rejected(self):
        for mode in ('missing', 'duplicate', 'wrong_time'):
            with self.subTest(mode=mode):
                workspace = self.workspace()
                if mode == 'missing':
                    workspace['initialFrameIds'].pop()
                elif mode == 'duplicate':
                    workspace['frames'].append(dict(workspace['frames'][0]))
                else:
                    workspace['frames'][0]['at'] = '2020-12-15T17:00:00Z'
                with self.assertRaises(ValueError):
                    execution_frames(workspace)


class RecordedCashFrameTests(unittest.TestCase):
    def test_original_working_frame_is_selected_independently_of_order(self):
        at = '2026-10-16T16:00:00Z'
        working = {'id': 'working', 'at': '2026-10-16T16:00:00+00:00'}
        recorded = {'id': 'close', 'at': at, 'close': {'session': 'synthetic_west'}}
        variant = {'id': 'variant', 'at': at}
        later = {'id': 'later', 'at': '2026-10-16T16:30:00Z'}
        frames = [recorded, variant, later, working]
        for order in (frames, frames[::-1]):
            with self.subTest(first=order[0]['id']):
                workspace = {'frames': order, 'initialFrameIds': ['working', 'later', 'close']}
                self.assertEqual(working_frame_at(workspace, at), working)

    def test_missing_or_ambiguous_working_frame_is_refused(self):
        at = '2026-10-13T16:00:00Z'
        for ids in ([], ['one', 'two']):
            with self.subTest(ids=ids), self.assertRaises(AssertionError):
                working_frame_at({'frames': [{'id': i, 'at': at} for i in ids], 'initialFrameIds': ids}, at)


if __name__ == '__main__':
    unittest.main()
