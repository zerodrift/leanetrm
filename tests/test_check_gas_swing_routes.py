from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_gas_swing_routes import check_portfolio_groups


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
