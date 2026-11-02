"""The owned history fixture reaches the read consumer without widening other suites."""
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from factor_history_fixture import calendar


class FactorHistoryFixtureTests(unittest.TestCase):
    def test_calendar_preserves_the_transaction_and_names_its_actual_rule(self):
        text = calendar('BEGIN;\nSELECT 1;\nCOMMIT;\n', {'referenceCatalogId': 'r', 'referenceRevisionId': 'v'})
        self.assertTrue(text.startswith('BEGIN;\nSELECT 1;\n'))
        self.assertTrue(text.endswith('COMMIT;\n'))
        self.assertIn("'weekdays_utc','test_utc',96", text)
        self.assertNotIn('synthetic', text.lower())

    def test_missing_transaction_refuses(self):
        with self.assertRaises(ValueError):
            calendar('SELECT 1;', {'referenceCatalogId': 'r', 'referenceRevisionId': 'v'})
