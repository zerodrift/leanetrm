"""Corrected native operator stock/debt through ordinary PV and real workers."""
import argparse
from decimal import Decimal
import json
import os
from pathlib import Path
import subprocess

from check_gas_operator_quantity_reconciliation import checks as native_checks
from check_lifecycle_economic_worker import children, worker_checks
from check_reference_editing import main as native_main
from gas_operator_physical_value_fixture import configure, fixture
from gas_operator_source_authority import _state


def market_inputs(query, manifest, context, *, loss_units):
    """Exact retained mark/rate pins and independent history endpoints for these consumers."""
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    from native_read_transport import captured_input
    inputs=captured_input(query,context)
    mark = '70000000-0000-0000-0000-000000000001'
    point = Decimal(query(f"SELECT value FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{mark}');"))
    multiplier = Decimal(query("SELECT q.multiplier FROM market_data.curves c JOIN ref_data.quote_specifications q "
        "ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{revision}','future');"))
    rates = json.loads(query("SELECT jsonb_agg(o.id ORDER BY t.ordinal) FROM market_data.rate_curves c "
        "JOIN market_data.rate_curve_tenors t ON(t.catalog_id,t.revision_id,t.curve_code)=(c.catalog_id,c.revision_id,c.code) "
        "JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code)="
        "(c.catalog_id,c.revision_id,t.series_id,c.source_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{revision}','interest_rates') "
        "AND o.role='parameter' AND o.publication_state='final' AND o.observed_at='2020-12-18T16:00:00Z' "
        "AND NOT EXISTS(SELECT 1 FROM market_data.observations next WHERE(next.catalog_id,next.corrects_id)=(o.catalog_id,o.id));"))
    count = int(query(f"SELECT count(*) FROM market_data.rate_curve_tenors WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','interest_rates');"))
    assert rates and len(rates) == len(set(rates)) == count
    assert query(f"SELECT bool_and(value=0) FROM market_data.observations WHERE catalog_id='{catalog}' "
        f"AND id=ANY(ARRAY[{','.join(repr(value)+'::uuid' for value in rates)}]);") == 't'
    history = json.loads(query("WITH eligible AS MATERIALIZED(SELECT o.* FROM market_data.observations o "
        "JOIN market_data.observations anchor ON(anchor.catalog_id,anchor.id)="+f"('{catalog}','{mark}'::uuid) "
        "JOIN market_data.knowledge_events event ON(event.catalog_id,event.revision_id,event.observation_id)=(o.catalog_id,o.definition_revision_id,o.id) "
        "JOIN market_data.market_knowledge_receipts receipt ON(receipt.catalog_id,receipt.event_order,receipt.publication_xid)=(event.catalog_id,event.event_order,event.publication_xid) "
        "WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role)="
        "(anchor.catalog_id,anchor.definition_revision_id,anchor.series_id,anchor.source_code,anchor.role) "
        "AND o.observed_at IN('2020-12-15T16:00:00Z','2020-12-16T16:00:00Z','2020-12-17T16:00:00Z') "
        f"AND event.event_order<={inputs['market_prefix']} AND receipt.known_from<='{inputs['known_at']}'::timestamptz) "
        "SELECT jsonb_build_object('count',count(*),'values',jsonb_object_agg(o.observed_at::date::text,o.value::text)) "
        "FROM eligible o WHERE o.publication_state='final' AND NOT EXISTS(SELECT 1 FROM eligible next WHERE next.corrects_id=o.id);"))
    assert history['count']==3,history
    assert {day:Decimal(value) for day,value in history['values'].items()} == {
        '2020-12-15':point-Decimal('.1'),'2020-12-16':point+Decimal('.1'),'2020-12-17':point-Decimal('.01')},dict(point=str(point),history=history)
    # The exact remaining seven units lose .77 under the half-open history window.
    return dict(markId=mark, contractId='50000000-0000-0000-0000-000000000001', rateIds=rates,
        unitValue=str(point * multiplier), expectedHistoricalVar=str((loss_units * point + Decimal('.77')) * multiplier),
        lossAt='2020-12-22T12:00:00Z', horizon='2020-12-23T16:00:00Z')


def checks(query, manifest, call, capture, read, record, amend, packet):
    selected = configure(query, manifest, call, capture, read, record)
    catalog=manifest['catalogId'];directory=Path(query('SHOW unix_socket_directories;')).resolve()
    path=directory/'gas-operator-physical-value.json'
    selected.update(manifest=manifest,host=str(directory),**market_inputs(query,manifest,selected['contextId'],loss_units=2))
    path.write_text(json.dumps(selected));path.chmod(0o600)
    financial_state = _state(query,catalog)
    worker_checks(query, manifest, path, submit_mode='--gas-operator-physical-value', check_labels=False)
    assert _state(query,catalog)==financial_state,'Operator PV or real economic workers changed a native financial, bank or receipt fact.'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    for path in [args.pg_bin / 'psql', args.dotnet]:
        if not path.is_file():parser.error('An installed executable is required: ' + str(path))
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    root = Path(__file__).resolve().parents[1]
    if not args.no_build:
        subprocess.run([str(args.dotnet), 'build', 'tests/OpenEtrm.Api.Checks', '--no-restore', '--disable-build-servers',
            '-m:1', '-p:UseSharedCompilation=false', '-nodeReuse:false'], cwd=root, check=True, timeout=120)
    native_main(checks=lambda query, manifest:native_checks(query, manifest, after_writer=checks),
        fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda:not children)


if __name__ == '__main__':main()
