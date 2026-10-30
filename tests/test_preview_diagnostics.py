"""Failure diagnostics reveal only known error identifiers from exact log lines."""
from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from preview_diagnostics import failure_identifiers


class PreviewDiagnosticsTests(unittest.TestCase):
    def test_exact_known_messages_are_extracted_and_deduplicated(self):
        source=['fail: OpenEtrm.Api[0]\n','      Preview operation failed: InvalidOperationException\n',
                '      Database operation rejected: 42702\n','Preview operation failed: InvalidOperationException\n']
        self.assertEqual(failure_identifiers(source),('sqlstate=42702','error_type=InvalidOperationException'))

    def test_single_line_console_prefix_is_exactly_recognized(self):
        self.assertEqual(failure_identifiers([
            'fail: Program[0] Preview operation failed: InvalidOperationException',
            'warn: Program[0] Database operation rejected: 42702',
            'fail: Program[0] Preview failure code: OpenEtrm.Api.SwapStore.Method',
        ]),('error_type=InvalidOperationException','sqlstate=42702','code=OpenEtrm.Api.SwapStore.Method'))
        self.assertEqual(failure_identifiers([
            'secret fail: Program[0] Preview operation failed: InvalidOperationException',
            'fail: Program[0] credential Preview operation failed: InvalidOperationException',
        ]),())

    def test_other_log_material_cannot_escape(self):
        source=['connection=private-value','Stack trace secret/path',
                'Preview operation failed: CustomerSecretException',
                'Database operation rejected: TOKEN',
                'prefix Preview operation failed: InvalidOperationException',
                'Preview operation failed: InvalidOperationException private-value',
                'Database operation rejected: 42702 private-value',
                'Preview operation failed: InvalidOperationException\x1b[31m',
                'Database operation rejected: 42702\ncredential=private-value']
        self.assertEqual(failure_identifiers(source),())

    def test_only_project_code_identifiers_are_accepted(self):
        code='OpenEtrm.Api.SwapStore+<QuantityPeriodBounds>d__42.MoveNext'
        self.assertEqual(failure_identifiers(['Preview failure code: '+code]),('code='+code,))
        for invalid in ('System.IO.File.ReadAllText','OpenEtrm.Api.Method(argument)',
                        'OpenEtrm.Api.Method /private/file','OpenEtrm.Api.Method:password',
                        'OpenEtrm.Api.Method\\file','OpenEtrm.Api.Method\nsecret',
                        'OpenEtrm.Api.'+'A'*241):
            with self.subTest(value=invalid):
                self.assertEqual(failure_identifiers(['Preview failure code: '+invalid]),())

    def test_output_is_bounded_and_keeps_the_latest_failure(self):
        lines=['Database operation rejected: '+value for value in
               ('23505','23503','23514','42501','55000','P0001','42702','42703','42P01','42883',
                '22023','22P02','22003','22007','57014','55P03','40P01','40001')]
        result=failure_identifiers(lines)
        self.assertEqual(len(result),16)
        self.assertEqual(result[-1],'sqlstate=40001')


if __name__=='__main__':unittest.main()
