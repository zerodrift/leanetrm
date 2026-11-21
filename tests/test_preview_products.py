"""The interactive preset covers every product it actually books."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from preview_fixture import example_products


class PreviewProductsChecks(unittest.TestCase):
    def test_combined_presets_retain_their_own_agreement_products(self):
        for gas,regional,ppa,expected in (
            (False,False,False,{'financial_swap'}),
            (True,False,False,{'physical_gas'}),
            (True,True,False,{'physical_gas','financial_swap'}),
            (True,True,True,{'physical_gas','financial_swap','ppa'}),
        ):
            with self.subTest(gas=gas,regional=regional,ppa=ppa):
                products=example_products('standard',gas=gas,regional=regional,ppa=ppa)
                self.assertEqual(set(products),expected)
                self.assertEqual(len(products),len(expected))
        self.assertEqual(example_products('mixed-gas',gas=True,regional=False,ppa=False),[])
