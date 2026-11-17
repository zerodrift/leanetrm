"""Preservation of the existing synthetic chain and explicit additional wings."""
import csv
import hashlib
import json
from pathlib import Path
import sys
import unittest
from decimal import Decimal

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import exchange_fixture as exchange


class OptionWingChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        _, cls.market, *_ = exchange.fixture(ROOT)

    def test_existing_strikes_and_all_564_settlements_are_unchanged(self):
        # The original 141 series, independently enumerated, including its intrinsic specimen.
        codes = set()
        for product, centre, step, interval in [('nymex_on', '3.25', '0.05', '0.05'), ('nymex_lo', '60.50', '0.50', '0.50')]:
            for i, month in enumerate(['20261201', '20270101', '20270201', '20270301', '20270401']):
                for n in [-6, -4, -2, 0, 2, 4, 6]:
                    strike = Decimal(centre) + i * Decimal(step) + n * Decimal(interval)
                    codes.update(f'{product}_{month}_{right}{strike}' for right in ['c', 'p'])
        codes.add('nymex_on_20261201_c2.00')
        ids = {exchange.key(exchange.key('price_' + code)) for code in codes}
        rows = []
        for line in self.market.splitlines():
            if line.startswith('INSERT INTO market_data.observations VALUES('):
                fields = next(csv.reader([line.split('VALUES(', 1)[1][:-2]], quotechar="'"))
                if fields[3] in ids:
                    rows.append([fields[3], fields[11], fields[13]])
        self.assertEqual(len(rows), 564)
        payload = json.dumps(sorted(rows), separators=(',', ':'))
        self.assertEqual(hashlib.sha256(payload.encode()).hexdigest(), '4a9dea111fef341d02bdff3905e2ac880423fcdd78284b50db0d8ca00b8c1a32')

    def test_every_expiry_adds_six_wing_strikes_on_the_existing_grid(self):
        _, series = exchange.option_reference("'synthetic','synthetic'")
        self.assertEqual(len(series), 261)
        for product, grid in [('nymex_on', Decimal('0.05')), ('nymex_lo', Decimal('0.50'))]:
            for month in ['20261201', '20270101', '20270201', '20270301', '20270401']:
                members = [s for s in series if s[1] == product and s[0].split('_')[2] == month]
                self.assertEqual(len(members), 27 if product == 'nymex_on' and month == '20261201' else 26)
                self.assertTrue(all(s[4] % grid == 0 for s in members))


if __name__ == '__main__':
    unittest.main()
