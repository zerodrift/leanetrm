"""Qualify this tree's physical facility UI on owned PostgreSQL and Chrome."""
import argparse
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import json
import os
from pathlib import Path
import shutil

from check_reference_editing import main as native_main
from owned_lifecycle_browser import api_artifact, build_assets, run as run_browser
from storage_facility_fixture import StorageFacilityFixture


def instant(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00')).astimezone(timezone.utc)


def utc(value):
    return value.isoformat().replace('+00:00', 'Z')



def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true', help='Use the coordinated frozen API and web artifacts')
    parser.add_argument('--api-bin', type=Path, help='Coherent frozen current-tree API directory with --no-build')
    args = parser.parse_args()
    for path in [args.pg_bin / 'psql', args.dotnet, args.browser]:
        if not path.is_file():
            parser.error('An installed executable is required: ' + str(path))
    if args.api_bin is not None:
        if not args.no_build:
            parser.error('--api-bin requires --no-build')
        try:
            args.api_bin = api_artifact(args.api_bin)
        except ValueError as error:
            parser.error(str(error))
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    children = []
    if not args.no_build:
        build_assets(args.dotnet.resolve())

    def checks(query, manifest):
        manifest['identityIssuer'] = 'https://preview.invalid'
        state = StorageFacilityFixture(query, manifest)
        source = state.source()
        begin, end = instant(source['from']), instant(source['through'])
        assert end - begin == timedelta(hours=24)
        assert source['quantity'] == '100' and source['allocated'] == '60'
        assert query('SELECT count(*) FROM trading.storage_facilities;') == '0'
        owner_counts = query("SELECT jsonb_build_object('positions',(SELECT count(*) FROM trading.gas_owned_positions),"
            "'fees',(SELECT count(*) FROM trading.gas_owned_fees),'payments',(SELECT count(*) FROM trading.gas_owned_fee_payments));")
        fixture = dict(at=state.at, day=state.at[:10], definitionAt=utc(begin), definitionDay=utc(begin)[:10],
            points=state.points, source=source,
            planFrom=utc(begin), planMiddle=utc(begin + timedelta(hours=1)), planThrough=utc(begin + timedelta(hours=2)),
            actualThrough=utc(end), profile='trader')
        artifacts = ['storage-facility-plan.png', 'storage-facility-measured.png', 'storage-facility-mobile.png']
        try:
            run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, fixture,
                'tests/browser/storage-facility-operations.mjs', worker_enabled=False, timeout=480, artifacts=artifacts, api_bin=args.api_bin)
        finally:
            owned = Path(query('SHOW unix_socket_directories;')).resolve()
            for name in artifacts:
                if (owned / name).is_file():
                    shutil.copyfile(owned / name, Path('/tmp') / name)

        assert query('SELECT count(*) FROM trading.storage_facilities;') == '1'
        facility = query('SELECT id FROM trading.storage_facilities;')
        facts = state.read(facility)
        assert len(facts['ports']) == 2
        assert len(facts['versionBundles']) == 1
        assert Decimal(facts['opening']['quantity']) == Decimal('400')
        assert len(facts['activities']) == 2
        quantities = sorted(Decimal(row['quantity']) for row in facts['activities'])
        assert quantities == [Decimal('100'), Decimal('700')]
        assert facts['planAssessment']['closing'] == dict(n='500', d='1')
        assert len(facts['coverage']['activityRevisionIds']) == 2
        assert facts['coverage']['statement'] == 'complete_observed_window_v1'
        linked = json.loads(query(f"SELECT jsonb_build_object('quantity',quantity,'source',source_revision_id) "
            f"FROM trading.storage_facility_activities WHERE facility_id='{facility}' AND source_revision_id IS NOT NULL;"))
        assert linked == dict(quantity=None, source=source['sourceRevisionId']), 'A linked flow was copied into another writable quantity.'
        assert query("SELECT jsonb_build_object('positions',(SELECT count(*) FROM trading.gas_owned_positions),"
            "'fees',(SELECT count(*) FROM trading.gas_owned_fees),'payments',(SELECT count(*) FROM trading.gas_owned_fee_payments));") == owner_counts
        assert query(f"SELECT trim_scale(quantity)::text FROM trading.facility_quantity_revisions WHERE id='{source['sourceRevisionId']}';") == '100'
        assert query(f"SELECT trim_scale(quantity)::text FROM trading.facility_gas_allocations WHERE plan_id='{source['allocationRevisionId']}';") == '60'
        print('PASS real facility UI: two explicit ports, step-ratchet plan refusal/acceptance, measured1200 breach, source100/allocation60 and no title/cash effects', flush=True)

    native_main(checks=checks, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
