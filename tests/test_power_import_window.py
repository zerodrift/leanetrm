"""The live worker check must not reuse the independent DST oracle dates."""
from datetime import date
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_power_import import malformed_report_window


class PowerImportWindowTests(unittest.TestCase):
    def test_worker_window_stays_in_the_recent_past_outside_oracle_days(self):
        for today, expected in ((date(2026, 11, 1), date(2026, 10, 31)),
                                (date(2026, 11, 2), date(2026, 10, 31)),
                                (date(2026, 11, 3), date(2026, 10, 31)),
                                (date(2026, 11, 4), date(2026, 11, 3)),
                                (date(2027, 1, 1), date(2026, 12, 31))):
            with self.subTest(today=today):
                day, offset = malformed_report_window(today)
                self.assertEqual(day, expected)
                self.assertEqual((day - today).days, offset)
                self.assertTrue(-31 <= offset <= -1)


if __name__ == '__main__':
    unittest.main()
