from pathlib import Path
from copy import deepcopy
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_gas_swing_routes import check_graph_work, check_portfolio_groups, check_reserved_lanes


class SwingPortfolioGroups(unittest.TestCase):
    def test_requires_each_portfolio_and_measure_exactly_once(self):
        holdings = [
            dict(tradeId='first', portfolioId='north', measure='physical', unassessedDays=1, presentValue='1/3'),
            dict(tradeId='second', portfolioId='north', measure='cash', unassessedDays=0, presentValue='2/3'),
        ]
        groups = [{**h, 'tradeCount':1} for h in holdings]
        check_portfolio_groups(holdings, groups)
        for cause, invalid in [('absent portfolio', []), ('missing measure', groups[:1]),
                ('duplicate group', [*groups, groups[0]]), ('wrong portfolio', [dict(groups[0], portfolioId='south'), groups[1]])]:
            with self.subTest(cause=cause), self.assertRaises(AssertionError):
                check_portfolio_groups(holdings, invalid)

    def test_subtotals_include_only_holdings_of_the_selected_portfolio_and_measure(self):
        holdings = [
            dict(tradeId='first', portfolioId='north', measure='physical', unassessedDays=1, presentValue='1/3'),
            dict(tradeId='other', portfolioId='south', measure='physical', unassessedDays=2, presentValue='2/3'),
        ]
        groups = [{**h, 'tradeCount':1} for h in holdings]
        check_portfolio_groups(holdings, groups)
        for wrong in [dict(tradeCount=2),dict(presentValue='1'),dict(unassessedDays=3)]:
            with self.subTest(wrong=wrong), self.assertRaises(AssertionError):
                check_portfolio_groups(holdings, [dict(groups[0], **wrong), groups[1]])

class SwingRiskEvidence(unittest.TestCase):
    def test_rejects_reused_shocked_amount_or_election(self):
        amounts = dict(base='390',up='391.1',down='388.9')
        right = dict(days=[{}, {}, dict(revisionId='first')])
        held = dict(values=amounts,swingRights={k:[deepcopy(right)] for k in amounts})
        check_reserved_lanes(held,amounts,'first')
        for lane in amounts:
            for evidence in ('amount','election'):
                other = deepcopy(held)
                if evidence=='amount':
                    other['values'][lane]='450'
                else:
                    other['swingRights'][lane][0]['days'][2]['revisionId']='second'
                with self.subTest(lane=lane,evidence=evidence), self.assertRaises(AssertionError):
                    check_reserved_lanes(other,amounts,'first')

    def test_rejects_boolean_partition_counters(self):
        work = dict(evaluatedNodes=3,reusedReads=0,affectedNodes=0,traversedAffectedEdges=0,evaluatedWork=0)
        check_graph_work(work)
        for counter in work:
            with self.subTest(counter=counter), self.assertRaises(AssertionError):
                check_graph_work({**work,counter:False})
