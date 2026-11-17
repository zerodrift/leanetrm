"""The required route qualifier never silently drops its managed consumer."""
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from gas_transport_route_managed import checkpoint


class RouteQualificationTests(unittest.TestCase):
    def test_missing_managed_tool_refuses_before_database_or_partial_qualification(self):
        with patch.dict(os.environ,{},clear=True):
            with self.assertRaisesRegex(RuntimeError,'DOTNET'):
                checkpoint(None,'synthetic-route','graph')


if __name__=='__main__':unittest.main()
