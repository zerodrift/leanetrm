"""Durable risk qualification distinguishes cached base values from scenario work."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_close_risk_leases import check_work


class CloseRiskWorkTests(unittest.TestCase):
    def test_warmed_base_and_each_signed_direct_factor(self):
        factors, scenarios, cells = 360, 721, 87648
        work = dict(uniqueInstruments=1, uniqueValuations=1, positionCount=1,
                    scenarioCount=scenarios, unitPricerCalls=scenarios,
                    volumeComputations=120, rowEvaluations=cells*5*scenarios,
                    resolvedCurveMonths=240, reusedCurveMonths=240*(scenarios-1),
                    rebuiltCurveNodes=2*factors, reusedCurveNodes=2*factors*(factors-1))
        admission = dict(declaredRows=cells*5, declaredRowScenarios=cells*5*scenarios)
        check_work(work, cells, admission)
        # Neither extra evaluations nor missing reuse may pass qualification.
        for field in ('rebuiltCurveNodes', 'reusedCurveNodes'):
            for difference in (-1, 1):
                with self.subTest(field=field, difference=difference), self.assertRaises(AssertionError):
                    check_work({**work, field: work[field]+difference}, cells, admission)


if __name__ == '__main__':
    unittest.main()
