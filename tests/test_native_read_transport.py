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

    def test_each_transaction_receives_original_inputs_after_its_own_identity(self):
        transport=NativeReadTransport()
        source=self.source('2026-11-20T00:00:00+00:00')
        transport.receive(transport.marker+json.dumps([source]))
        script=(f"PREPARE work AS SELECT '{source['id']}'::uuid;"
            "BEGIN;SELECT security.use_context('warm');EXECUTE work;ROLLBACK;"
            "BEGIN;SELECT security.use_context('current');EXECUTE work;COMMIT;")
        prepared=transport.prepare(script)
        for transaction in prepared.split('BEGIN;')[1:]:
            identity=transaction.index('security.use_context(')
            inputs=transaction.index('security.register_read_source(')
            operation=transaction.index('EXECUTE work;')
            self.assertLess(identity,inputs)
            self.assertLess(inputs,operation)
        self.assertEqual(prepared.count('security.register_read_source('),2)

    def test_outer_binding_does_not_rewrite_a_later_function_body(self):
        transport=NativeReadTransport()
        source=self.source('2026-11-20T00:00:00+00:00')
        transport.receive(transport.marker+json.dumps([source]))
        body="CREATE FUNCTION public.probe() RETURNS void LANGUAGE plpgsql AS $$BEGIN PERFORM security.use_context('inner');END$$;"
        prepared=transport.prepare("BEGIN;SELECT security.use_context('outer');"+body+f"SELECT '{source['id']}';ROLLBACK;")
        self.assertIn(body,prepared)
        self.assertEqual(prepared.count('security.register_read_source('),1)
