"""Durable risk qualification distinguishes cached base values from scenario work."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_close_risk_leases import check_work


class CloseRiskWorkTests(unittest.TestCase):
    def test_twelve_month_native_quarter_hour_two_side_book(self):
        # November 2031 through October 2032 contains 366 days. The fixed
        # payment is unaffected by the 24 monthly peak/off-peak price factors.
        # Base: 70,272 amounts; four physical repricings: 140,544 amounts.
        cells = 35136
        work = dict(uniqueInstruments=1, uniqueValuations=1, positionCount=1,
                    scenarioCount=49, unitPricerCalls=49,
                    volumeComputations=12, rowEvaluations=210816,
                    uniqueSideCalculations=50, calibratedBlocks=24, reusedBlocks=0,
                    scheduleGraphEvaluatedNodes=1, scheduleGraphReusedReads=1,
                    resolvedCurveMonths=12, reusedCurveMonths=0,
                    rebuiltCurveNodes=48, reusedCurveNodes=1104)
        admission = dict(declaredRows=70272, declaredRowScenarios=3443328)
        check_work(work, cells, admission)
        # Neither extra evaluations nor missing reuse may pass qualification.
        for field in ('scenarioCount', 'unitPricerCalls', 'volumeComputations',
                      'rowEvaluations', 'uniqueSideCalculations', 'calibratedBlocks',
                      'reusedBlocks', 'resolvedCurveMonths', 'reusedCurveMonths',
                      'rebuiltCurveNodes', 'reusedCurveNodes'):
            for difference in (-1, 1):
                with self.subTest(field=field, difference=difference), self.assertRaises(AssertionError):
                    check_work({**work, field: work[field]+difference}, cells, admission)
        for field in admission:
            with self.subTest(field=field), self.assertRaises(AssertionError):
                check_work(work, cells, {**admission, field: admission[field]+1})
        for field in ('scheduleGraphEvaluatedNodes', 'scheduleGraphReusedReads'):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                check_work({**work, field: 0}, cells, admission)


if __name__ == '__main__':
    unittest.main()
