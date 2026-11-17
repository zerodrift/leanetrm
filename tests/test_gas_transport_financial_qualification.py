"""The required managed financial consumer cannot disappear from qualification."""
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from gas_transport_financial_managed import checkpoint


class FinancialQualificationTests(unittest.TestCase):
    def test_missing_sdk_refuses_before_fixture_or_process_work(self):
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(RuntimeError, 'DOTNET.*required managed financial'):
                checkpoint(None, None, 'read')


if __name__ == '__main__':
    unittest.main()
