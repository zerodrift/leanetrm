"""Replay comparison ignores only validated physical cache-work counters."""
from copy import deepcopy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_portfolio_risk import financial


class FinancialComparisonTests(unittest.TestCase):
    def report(self):
        return {'totals': [{'amount': '25/3'}], 'context': {'id': 'synthetic-cutoff'},
                'work': {'rebuiltCurveNodes': 0, 'reusedCurveNodes': 0,
                         'curveGraphEvaluatedNodes': 12, 'curveGraphReusedReads': 3,
                         'unitPricerCalls': 5, 'rowEvaluations': 721},
                'connectedScenarios': [{'manifestId': 'synthetic-topology',
                    'work': {'evaluatedNodes': 12, 'reusedReads': 3,
                             'affectedNodes': 4, 'traversedAffectedEdges': 8}}]}

    def test_cache_history_does_not_change_financial_comparison(self):
        initial = self.report()
        replay = deepcopy(initial)
        replay['work']['curveGraphEvaluatedNodes'] = 0
        replay['work']['curveGraphReusedReads'] = 15
        replay['connectedScenarios'][0]['work'].update(evaluatedNodes=0, reusedReads=15)
        self.assertEqual(financial(initial), financial(replay))
        self.assertEqual(initial, self.report(), 'comparison must not mutate the report')

    def test_economic_evidence_and_pricing_work_remain_exact(self):
        for field in ('amount', 'context', 'manifest', 'unitPricerCalls', 'rowEvaluations'):
            with self.subTest(field=field):
                changed = self.report()
                if field == 'amount':
                    changed['totals'][0]['amount'] = '26/3'
                elif field == 'context':
                    changed['context']['id'] = 'different-cutoff'
                elif field == 'manifest':
                    changed['connectedScenarios'][0]['manifestId'] = 'different-topology'
                else:
                    changed['work'][field] += 1
                self.assertNotEqual(financial(self.report()), financial(changed))

    def test_physical_counters_must_be_nonnegative_integers(self):
        for field in ('curveGraphEvaluatedNodes', 'curveGraphReusedReads'):
            for invalid in (-1, True, '12', None):
                with self.subTest(field=field, invalid=invalid):
                    changed = self.report()
                    changed['work'][field] = invalid
                    with self.assertRaises(AssertionError):
                        financial(changed)


if __name__ == '__main__':
    unittest.main()
