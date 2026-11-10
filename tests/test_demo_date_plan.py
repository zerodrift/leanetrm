"""Behavior checks for the demo contract-date guard and the panels it leaves out."""
import json
from pathlib import Path
import re
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from custom_curve_fixture import prepare
from demo_date_plan import verify_instrument_dates

PILLARS = ['2021-01-18', '2021-06-18']
PLAN = {
    'schedules': [{'definition': {'symbol': 'oe.rate_pillars', 'dates': PILLARS}}],
    'mappings': [
        {'code': 'gas_delivery', 'contracts': [{'start': '2021-01-01', 'end': '2021-02-01', 'lastTradingAt': '2020-12-28T15:30:00+00:00'}]},
        {'code': 'forward_delivery', 'contracts': [{'start': '2021-01-01', 'end': '2021-02-01', 'lastTradingAt': '2021-02-01T00:00:00+00:00'}]},
    ],
}
MAPPED = {'id': 'plan-future', 'kind': 'future', 'delivery_from': '2021-01-01', 'delivery_to': '2021-02-01', 'expiry_at': '2020-12-28T15:30:00+00:00'}
UNMAPPED = {'id': 'panel-future', 'kind': 'future', 'delivery_from': '2020-11-01', 'delivery_to': '2020-12-01', 'expiry_at': '2020-10-28T15:30:00+00:00'}


def verify(rows, *outside_plan):
    def query(sql):
        return json.dumps(rows if 'market_data.contracts' in sql else PILLARS)
    return verify_instrument_dates(query, PLAN, 'catalog', 'revision', *outside_plan)


class VerifyInstrumentDatesTests(unittest.TestCase):
    def test_refuses_a_contract_with_no_source_mapping(self):
        with self.assertRaisesRegex(ValueError, 'no source mapping: panel-future'):
            verify([MAPPED, UNMAPPED])

    def test_declaring_one_contract_outside_the_plan_covers_only_that_contract(self):
        self.assertEqual(verify([MAPPED, UNMAPPED], ['panel-future']), 1)
        other = {**UNMAPPED, 'id': 'undeclared-future'}
        with self.assertRaisesRegex(ValueError, 'no source mapping: undeclared-future'):
            verify([MAPPED, UNMAPPED, other], ['panel-future'])

    def test_refuses_a_revision_with_only_contracts_outside_the_plan(self):
        with self.assertRaisesRegex(ValueError, 'no contracts'):
            verify([UNMAPPED], ['panel-future'])


class CustomCurveFixtureTests(unittest.TestCase):
    MARKET = "INSERT INTO market_data.contracts VALUES('c','plan-future');UPDATE ref_data.revisions SET a=1;UPDATE market_data.revisions SET b=1;COMMIT;"

    def test_declares_exactly_the_contracts_it_adds(self):
        for daily_lifecycle in (False, True):
            with self.subTest(daily_lifecycle=daily_lifecycle):
                _, market, manifest = prepare('', self.MARKET, {'frames': [], 'schedules': []}, daily_lifecycle=daily_lifecycle)
                added = re.findall(r"INSERT INTO market_data\.contracts VALUES\('[^']+','[^']+','([^']+)'", market)
                self.assertTrue(added)
                self.assertCountEqual(manifest['contractsOutsideDatePlan'], added)


if __name__ == '__main__':
    unittest.main()
