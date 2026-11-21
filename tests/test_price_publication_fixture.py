"""Publication inputs retain financial references without catalogue display fields."""
from copy import deepcopy
from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from price_publication_fixture import index_binding


class PricePublicationFixtureChecks(unittest.TestCase):
    def test_choice_projects_only_explicit_binding_fields(self):
        expected=dict(key={'code':'index'},commodity={'code':'gas'},location={'code':'point'},
                      quote={'code':'gbp_gj'},unit={'currency':{'code':'gbp'},'quantity':{'code':'gj'},'multiplier':'100'})
        choice=deepcopy(expected)|dict(name='Synthetic index',unitLabel='GBP/GJ × 100',futureDisplayField='Synthetic display')
        before=deepcopy(choice)
        self.assertEqual(index_binding(choice,symbol='SYNTHETIC',statistic='monthly_index'),
                         expected|dict(symbol='SYNTHETIC',statistic='monthly_index'))
        self.assertEqual(choice,before)
