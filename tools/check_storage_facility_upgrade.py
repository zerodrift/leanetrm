"""Qualify additive storage operations over retained native facility allocations."""
import json
import os
from pathlib import Path
from uuid import uuid4

from check_booked_side_programs import runtime_query
from check_reference_editing import main
from preview_fixture import fixture
from storage_facility_fixture import StorageFacilityFixture, literal


def run():
    if not os.environ.get('DOTNET'):
        raise ValueError('Set DOTNET to the installed SDK executable; upgrade qualification uses ordinary managed booking.')
    root = Path(__file__).resolve().parents[1]
    prepared = {}
    tables = ['facility_quantity_ledgers', 'facility_gas_delivery_scopes', 'facility_quantity_revisions',
        'facility_quantity_plans', 'facility_gas_targets', 'facility_gas_allocations', 'facility_quantity_limits',
        'facility_quantity_commands', 'facility_quantity_adoptions', 'facility_adopted_targets',
        'gas_trade_revisions', 'gas_delivery_obligations', 'publications', 'knowledge_receipts',
        'gas_owned_positions', 'gas_owned_fees', 'gas_owned_fee_payments']

    def state(query, catalog):
        return query('SELECT jsonb_object_agg(name,digest ORDER BY name) FROM(' + ' UNION ALL '.join(
            "SELECT '" + table + "'::text name,(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
            f"FROM trading.{table} x WHERE x.catalog_id='{catalog}') digest" for table in tables) + ')s;')

    def before(query, migration):
        if migration.name != '0349_storage_facility_operations.sql':
            return
        reference, market, security, manifest = fixture(root)
        for sql in [reference, market, security]:
            query(sql)
        owned = StorageFacilityFixture(query, manifest)
        source = owned.source()
        prepared.update(f=owned, source=source, manifest=manifest, digest=state(query, owned.catalog))

    def loaded(_):
        assert prepared
        return '', '', '', prepared['manifest']

    def verify(query, manifest):
        owned, source = prepared['f'], prepared['source']
        catalog = owned.catalog
        assert state(query, catalog) == prepared['digest'], 'Installation changed retained physical sources, allocations, title or cash.'
        assert query(f"SELECT count(*) FROM trading.storage_facilities WHERE catalog_id='{catalog}';") == '0'
        assert query("SELECT NOT has_table_privilege('openetrm_runtime','trading.storage_facility_activities','SELECT');") == 't'
        read = lambda: json.loads(owned.call(f"SELECT security.read_facility_quantity_ledger('{catalog}','{source['ledgerId']}')::text;"))
        assert read()['source']['id'] == source['sourceRevisionId'] and read()['allocations'][0]['quantity'] == '60'
        command = dict(kind='source', ledgerId=source['ledgerId'], expectedPreviousId=None, quantity='100', evidence='Synthetic reported physical source')
        result = json.loads(runtime_query(query, manifest,
            f"SELECT security.record_facility_quantity_command('{catalog}','{uuid4()}',{literal(command)},'{owned.at}')::text;"))
        assert result['revisionId'] == source['sourceRevisionId']
        corrected = {**command, 'expectedPreviousId': source['sourceRevisionId'], 'quantity': '90', 'evidence': 'Synthetic retained-source correction'}
        runtime_query(query, manifest,
            f"SELECT security.record_facility_quantity_command('{catalog}','{uuid4()}',{literal(corrected)},'{owned.at}');")
        assert read()['source']['quantity'] == '90' and read()['allocations'][0]['quantity'] == '60'
        assert read()['requiresReconciliation']
        print('PASS additive storage upgrade: original gas booking/source100/allocation60/title/cash unchanged; legacy fact retry and source90 correction remain executable', flush=True)

    main(checks=verify, before_migration=before, fixture_factory=loaded)


if __name__ == '__main__':
    run()
