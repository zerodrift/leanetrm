"""Composed close-risk inputs cover their selected UTC business day."""
from contextlib import ExitStack
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
import re
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import close_risk_fixture
import interval_curve_fixture
import ppa_bookings_fixture
import ppa_fixture
import preview_fixture


class CloseRiskFixtureTests(unittest.TestCase):
    def inputs(self, clock):
        class FrozenDateTime(datetime):
            @classmethod
            def now(cls, tz=None):
                return clock.astimezone(tz) if tz is not None else clock.replace(tzinfo=None)
        stack = ExitStack()
        for module in (preview_fixture, interval_curve_fixture, ppa_fixture, ppa_bookings_fixture, close_risk_fixture):
            stack.enter_context(patch.object(module, 'datetime', FrozenDateTime))
        stack.enter_context(patch('datetime.datetime', FrozenDateTime))
        self.addCleanup(stack.close)
        inputs = preview_fixture.fixture(ROOT)
        for prepare in (interval_curve_fixture.prepare, ppa_fixture.prepare, ppa_bookings_fixture.prepare):
            inputs = prepare(*inputs)
        return inputs

    def periods(self, market):
        return [(date.fromisoformat(first), date.fromisoformat(last)) for first, last in re.findall(
            r"INSERT INTO market_data\.revision_periods VALUES \(" + re.escape(preview_fixture.M) +
            r",'([0-9-]+)','([0-9-]+)'\);", market)]

    def test_composed_source_covers_close_after_utc_midnight_month_and_year_rollover(self):
        for clock in (datetime(2026, 11, 16, 23, 55, 30, tzinfo=timezone.utc),
                      datetime(2026, 11, 30, 23, 55, 30, tzinfo=timezone.utc),
                      datetime(2026, 12, 31, 23, 55, 30, tzinfo=timezone.utc)):
            with self.subTest(clock=clock):
                inputs = self.inputs(clock)
                _, market, _, manifest = close_risk_fixture.prepare(*inputs)
                close = datetime.fromisoformat(manifest['closeRisk']['at'])
                self.assertEqual(close, (clock + timedelta(minutes=6)).replace(second=0, microsecond=0))
                self.assertGreater(close.date(), clock.date())
                periods = self.periods(market)
                self.assertEqual(len(periods), 1)
                first, last = periods[0]
                self.assertLessEqual(first, close.date())
                self.assertGreater(last, close.date(), 'The source end is exclusive and must cover the selected close day.')
                self.assertLessEqual(first, date(2020, 12, 18))
                self.assertGreater(last, date(2020, 12, 18))

    def test_longer_and_unrelated_source_periods_are_preserved(self):
        reference, market, security, manifest = self.inputs(datetime(2026, 11, 16, 12, tzinfo=timezone.utc))
        first, last = self.periods(market)[0]
        row = f"INSERT INTO market_data.revision_periods VALUES ({preview_fixture.M},'{first}','{last}');"
        market = market.replace(row, row.replace(str(last), '2035-01-01'))
        unrelated = "INSERT INTO market_data.revision_periods VALUES ('40000000-0000-0000-0000-000000000099','60000000-0000-0000-0000-000000000099','2020-01-01','2021-01-01');"
        market = market.replace('COMMIT;', unrelated + '\nCOMMIT;', 1)
        _, market, _, _ = close_risk_fixture.prepare(reference, market, security, manifest)
        self.assertEqual(self.periods(market), [(first, date(2035, 1, 1))])
        self.assertIn(unrelated, market)

    def test_missing_or_ambiguous_owned_source_period_refuses(self):
        reference, market, security, manifest = self.inputs(datetime(2026, 11, 16, 12, tzinfo=timezone.utc))
        first, last = self.periods(market)[0]
        row = f"INSERT INTO market_data.revision_periods VALUES ({preview_fixture.M},'{first}','{last}');"
        for changed in (market.replace(row, ''), market.replace(row, row + '\n' + row)):
            with self.subTest(duplicate=changed.count(row) > 1), self.assertRaisesRegex(ValueError, 'exactly one.*applicability period'):
                close_risk_fixture.prepare(reference, changed, security, manifest)


if __name__ == '__main__':
    unittest.main()
