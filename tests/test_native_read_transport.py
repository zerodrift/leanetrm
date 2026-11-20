import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from native_read_transport import NativeReadTransport


class NativeReadTransportChecks(unittest.TestCase):
    def source(self,instant):
        return dict(catalog_id='00000000-0000-0000-0000-000000000001',id='00000000-0000-0000-0000-000000000002',
                    effective_on='2020-12-18',known_at=instant,schedule_prefix=3,system_id='synthetic',timeline=1)

    def test_timezone_changes_do_not_change_the_certified_instant(self):
        transport=NativeReadTransport()
        for instant in ['2026-11-20T00:00:00+00:00','2026-11-20T09:00:00+09:00']:
            transport.receive(transport.marker+json.dumps([self.source(instant)]))
        self.assertEqual(len(transport.sources),1)

    def test_changed_instant_cannot_reuse_a_correlation(self):
        transport=NativeReadTransport()
        transport.receive(transport.marker+json.dumps([self.source('2026-11-20T00:00:00+00:00')]))
        with self.assertRaisesRegex(AssertionError,'changed its exact coordinates'):
            transport.receive(transport.marker+json.dumps([self.source('2026-11-20T09:00:00+00:00')]))

    def test_interactive_native_input_uses_the_same_instant_comparison(self):
        transport=NativeReadTransport()
        source=self.source('2026-11-20T09:00:00+09:00')
        transport.sources[source['id']]=source
        transport.receive(transport.marker+json.dumps([self.source('2026-11-20T00:00:00+00:00')]))
