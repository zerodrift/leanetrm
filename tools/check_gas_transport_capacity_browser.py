"""Qualify commercial capacity operations on this tree's owned preview."""
import argparse
from decimal import Decimal
import os
from pathlib import Path

from check_reference_editing import main as native_main
from gas_transport_capacity_fixture import TransportCapacityFixture, fixture
from owned_lifecycle_browser import build_assets, run as run_browser


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--api-bin', type=Path, help='Coherent frozen current-tree API directory with --no-build')
    args = parser.parse_args()
    for path in [args.pg_bin / 'psql', args.dotnet, args.browser]:
        if not path.is_file():
            parser.error('An installed executable is required: ' + str(path))
    if args.api_bin is not None:
        if not args.no_build:
            parser.error('--api-bin requires --no-build')
        args.api_bin = args.api_bin.resolve()
        for name in ['OpenEtrm.Api.dll', 'OpenEtrm.Api.deps.json', 'OpenEtrm.Api.runtimeconfig.json']:
            if not (args.api_bin / name).is_file():
                parser.error('A coherent current-tree API artifact is required: ' + str(args.api_bin / name))
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    children = []
    if not args.no_build:
        build_assets(args.dotnet.resolve())

    def checks(query, manifest):
        print('Transport browser fixture migrations loaded.', flush=True)
        state = TransportCapacityFixture(query, manifest)
        query("DELETE FROM security.role_permissions WHERE role_id IN(SELECT id FROM security.roles WHERE code='trader') "
              "AND permission IN('BookTrade','ReadGasOwnedHoldings','RecordGasOwnedHoldings','ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits');")
        tables = ['trading.trades', 'trading.gas_owned_positions', 'trading.gas_owned_loans', 'trading.gas_owned_fees',
                  'trading.cash_payment_memberships', 'settlement_data.cash_payment_revisions',
                  'trading.storage_facility_activities', 'trading.facility_gas_allocations']
        def counts():
            return {table: int(query('SELECT count(*) FROM ' + table + ';')) for table in tables}
        before = counts()
        definition = state.definition('synthetic_browser_transport_capacity')
        packet = dict(profile='trader', definition=definition, day='2020-12-19', catchupDay='2020-12-20',
                      effectiveAt='2020-12-19T16:00:00Z', futureEffectiveAt='2020-12-20T00:00:00Z')
        run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet,
                    'tests/browser/gas-transport-capacity.mjs', worker_enabled=False, timeout=600, api_bin=args.api_bin,
                    artifacts=['transport-capacity-definition.png', 'transport-capacity-review.png', 'transport-capacity-acknowledgement.png', 'transport-capacity-plan.png',
                               'transport-capacity-breach.png', 'transport-capacity-future.png', 'transport-capacity-mobile.png'])
        assert counts() == before, 'Commercial capacity created a trade, title, cash or physical allocation.'
        entitlement = query("SELECT id FROM trading.gas_transport_capacity_entitlements WHERE business_key='synthetic_browser_transport_capacity';")
        assert entitlement and '\n' not in entitlement
        state.at = '2020-12-20T23:59:59.999999Z'
        facts = state.read(entitlement)
        assert facts['hasFutureHeads'] is False
        assert len(facts['paths']) == 2 and len(facts['plans']) == 3 and len(facts['claims']) == 2
        heads = {row['business_key']: row for row in facts['plans']}
        assert {key: row['revision'] for key, row in heads.items()} == {
            'synthetic_browser_plan_a': 3, 'synthetic_browser_plan_b': 2, 'synthetic_browser_plan_c': 1}
        assert not any(row['plan_id'] == heads['synthetic_browser_plan_a']['id'] for row in facts['claims'])
        assert sorted(Decimal(row['receipt_quantity']) for row in facts['claims']) == [Decimal('10'), Decimal('30')]
        assert sorted(Decimal(row['delivery_quantity']) for row in facts['claims']) == [Decimal('5'), Decimal('20')]
        assert all(row['receipt_from'].startswith('2020-12-20T00:00:00') and row['receipt_to'].startswith('2020-12-20T01:00:00')
                   and row['delivery_from'].startswith('2020-12-20T01:00:00') and row['delivery_to'].startswith('2020-12-20T02:00:00') for row in facts['claims'])
        assert query(f"SELECT count(*) FROM trading.gas_transport_capacity_versions WHERE entitlement_id='{entitlement}';") == '3'
        assert query(f"SELECT count(*) FROM trading.gas_transport_capacity_plans WHERE entitlement_id='{entitlement}';") == '6'
        release = query("SELECT id FROM trading.gas_transport_capacity_entitlements WHERE business_key='synthetic_browser_transport_release';")
        assert release and '\n' not in release and release != entitlement
        released = state.read(release)
        assert len(released['plans']) == 2 and released['claims'] == []
        assert {row['business_key']: row['revision'] for row in released['plans']} == {
            'synthetic_browser_release_a': 2, 'synthetic_browser_release_b': 2}
        assert query(f"SELECT count(*) FROM trading.gas_transport_capacity_versions WHERE entitlement_id='{release}';") == '2'
        assert query(f"SELECT count(*) FROM trading.gas_transport_capacity_claims WHERE entitlement_id='{release}';") == '2'
        print('PASS native UI postconditions: independent chosen plans, exact gross stages, empty release, three capacity versions and no trade/title/cash/physical effects', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
