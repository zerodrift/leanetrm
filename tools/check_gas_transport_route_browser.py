"""Qualify retained chosen routes through this tree's owned operations UI."""
import argparse
from decimal import Decimal
import os
from pathlib import Path

from check_reference_editing import main as native_main
from gas_transport_route_fixture import RouteFixture, fixture
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
        print('Route browser fixture migrations loaded.', flush=True)
        state = RouteFixture(query, manifest)
        sources = state.source_plans()
        query("DELETE FROM security.role_permissions WHERE role_id IN(SELECT id FROM security.roles WHERE code='trader') "
              "AND permission IN('BookTrade','AmendTrade','ReadGasOwnedHoldings','RecordGasOwnedHoldings',"
              "'ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits');")
        independent = ['trading.trades', 'trading.gas_owned_positions', 'trading.gas_owned_loans', 'trading.gas_owned_fees',
                       'trading.cash_payment_memberships', 'settlement_data.cash_payment_revisions',
                       'trading.storage_facility_activities', 'trading.facility_gas_allocations']

        def counts():
            return {table: int(query('SELECT count(*) FROM ' + table + ';')) for table in independent}

        before = counts()
        definition = state.route_definition('synthetic_browser_route_a')
        packet = dict(profile='trader', definition=definition, day='2020-12-19', catchupDay='2020-12-20',
                      effectiveAt='2020-12-19T16:00:00Z', futureEffectiveAt='2020-12-20T00:00:00Z')
        run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet,
                    'tests/browser/transport-routes.mjs', worker_enabled=False, timeout=900, api_bin=args.api_bin,
                    artifacts=['transport-route-definition.png', 'transport-route-graph.png', 'transport-route-review.png',
                               'transport-route-current.png', 'transport-route-historical-evidence.png',
                               'transport-route-release.png', 'transport-route-local-time.png', 'transport-route-mobile.png'])
        assert counts() == before, 'Chosen route planning changed trade, title, cash or physical facts.'
        state.at = '2020-12-20T23:59:59.999999Z'
        routes = {}
        for key in ['synthetic_browser_route_a', 'synthetic_browser_route_b']:
            route = query("SELECT id FROM trading.gas_transport_routes WHERE business_key='" + key + "';")
            assert route and '\n' not in route
            routes[key] = state.route_read(route)
        a, b = routes.values()
        assert len(a['nodes']) == len(b['nodes']) == 6 and len(a['segments']) == len(b['segments']) == 7
        assert len(a['plans']) == 1 and a['plans'][0]['kind'] == 'release' and a['plans'][0]['revision'] == 3
        assert a['memberships'] == [], 'Own release must not retain an active source membership.'
        assert len(b['plans']) == 1 and b['plans'][0]['kind'] == 'plan' and b['plans'][0]['revision'] == 1
        assert len(b['memberships']) == 4 and len(b['plans'][0]['flows']) == 7
        witness = b['plans'][0]['assessment']
        assert witness['feasible'] is True and len(witness['segments']) == 7
        segments = {row['id']: row['segment_key'] for row in b['segments']}
        by_segment = {segments[row['segmentId']]: row for row in witness['segments']}
        assert by_segment['ah']['receiptQuantity'] == {'n': '100', 'd': '1'}
        assert by_segment['ah']['fuelQuantity'] == {'n': '2', 'd': '1'}
        assert by_segment['ah']['deliveryQuantity'] == by_segment['md']['deliveryQuantity'] == {'n': '98', 'd': '1'}
        assert by_segment['hm']['receiptQuantity'] == by_segment['hm']['deliveryQuantity'] == {'n': '0', 'd': '1'}
        assert all(row['conserved'] and all(i['residualPerHour'] == {'n': '0', 'd': '1'} for i in row['intervals'])
                   for row in witness['nodes'])
        assert query('SELECT count(*) FROM trading.gas_transport_routes;') == '2'
        assert query('SELECT count(*) FROM trading.gas_transport_route_plans;') == '4'
        assert query('SELECT count(*) FROM trading.gas_transport_route_flows;') == '21'
        assert query('SELECT count(*) FROM trading.gas_transport_capacity_versions;') == '5'
        assert query('SELECT count(*) FROM trading.gas_transport_capacity_plans;') == '5'
        assert query('SELECT count(*) FROM trading.gas_transport_capacity_claims;') == '8'
        # Route adoption never revises or copies a commercial capacity claim.
        for original in sources:
            current = state.read(original['entitlement']['id'])
            assert len(current['claims']) == len(original['claims'])
            assert sorted((c['path_id'], Decimal(c['receipt_quantity']), Decimal(c['delivery_quantity'])) for c in current['claims']) == sorted(
                (c['path_id'], Decimal(c['receipt_quantity']), Decimal(c['delivery_quantity'])) for c in original['claims'])
        print('PASS native UI postconditions: two immutable graphs, complete seven-claim route, exact fuel and transit, exclusive memberships, own release and no trade/title/cash/physical effects', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
