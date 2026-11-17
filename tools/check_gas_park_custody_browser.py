"""Qualify retained-title Park custody through this tree's owned application."""
import argparse
from decimal import Decimal
import json
import os
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main as native_main
from close_session_fixture import sql_session
from gas_park_custody_fixture import CustodyFixture, PROFILE, fixture
from gas_park_loan_fixture import literal
from owned_lifecycle_browser import build_assets, run as run_browser


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true', help='Use coordinated frozen API and web artifacts')
    parser.add_argument('--api-bin', type=Path, help='Use a coherent frozen current-tree API directory with --no-build')
    parser.add_argument('--provider-only', action='store_true', help='Qualify foreign custody without inventory or bank-payment authority')
    args = parser.parse_args()
    for path in [args.pg_bin / 'psql', args.dotnet, args.browser]:
        if not path.is_file():
            parser.error('An installed executable is required: ' + str(path))
    if args.api_bin is not None:
        if not args.no_build:
            parser.error('--api-bin requires --no-build for an explicitly frozen current-tree artifact')
        args.api_bin = args.api_bin.resolve()
        for name in ['OpenEtrm.Api.dll', 'OpenEtrm.Api.deps.json', 'OpenEtrm.Api.runtimeconfig.json']:
            if not (args.api_bin / name).is_file():
                parser.error('A coherent current-tree API artifact is required: ' + str(args.api_bin / name))
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    children = []
    if not args.no_build:
        build_assets(args.dotnet.resolve())

    def checks(query, manifest):
        print('Custody browser fixture migrations loaded.', flush=True)
        state = CustodyFixture(query, manifest)
        policies = {action: state.custody_policy(action) for action in ['accept', 'return']}
        cases = []
        for perspective in (['provider'] if args.provider_only else ['customer', 'provider']):
            for direction in ['pay', 'receive']:
                label = 'custody_browser_' + perspective + '_' + direction
                owned, lot = None, None
                if perspective == 'customer':
                    owned, book = state.account(label)
                    title_policy = state.bind(owned, 'gas.owned.receive_title')
                    for action in ['gas.owned.reserve', 'gas.owned.release']:
                        state.bind(owned, action)
                    title = state.evidence(owned, label + '_title', 'title_receipt', '1000', '2020-12-18T08:00:00Z')
                    lot = state.native(owned, 'receive_title', dict(evidenceId=title, quantity='1000', titleAt='2020-12-18T08:00:00Z',
                        availableAt='2020-12-18T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=title_policy)['id']
                else:
                    book = state.book_scope(label)
                cases.append(dict(perspective=perspective, feeDirection=direction, signedFee='-12.5' if direction == 'pay' else '12.5',
                    signedPayment='-5' if direction == 'pay' else '5', expectedUnpaid=('-12.5' if direction == 'pay' else '12.5') if args.provider_only else ('-7.5' if direction == 'pay' else '7.5'),
                    bookId=book, ownedAccountId=owned, lotId=lot, point=state.terms['location'] if owned else 'synthetic_gas_point_unpriced',
                    legalEntityId=state.entity, counterpartyId=state.party))
        assert len({case['bookId'] for case in cases}) == len(cases)
        market = state.valuation_inputs()
        session = 'synthetic_custody_close'
        sql_session(query, manifest, session, 'test_utc_every_day', '16:00:00', ['future'], ['trader'], effective=('2020-01-01', '2022-01-01'))
        query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
        closes = {label: state.call(f"SELECT security.register_market_close('{state.catalog}','{manifest['revisionId']}','{uuid4()}',"
            f"'Synthetic custody {label}','{session}','{day}','{day}T16:00:00Z',{literal(market['pins'])},{literal(market['members'])});")
            for label, day in [('before', '2020-12-18'), ('flow', '2020-12-20')]}
        if args.provider_only:
            role = query("SELECT g.role_id FROM security.role_grants g JOIN security.identity_bindings i ON i.principal_id=g.principal_id WHERE i.subject='trader';")
            assert role and '\n' not in role
            query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission IN('ReadGasOwnedHoldings','RecordGasOwnedHoldings','AmendTrade');")
        physical_tables = ['storage_facility_activities', 'storage_facility_plans', 'facility_gas_allocations', 'gas_quantity_revisions', 'gas_nomination_requests']
        def physical_counts():
            return {table: int(query(f"SELECT count(*) FROM trading.{table} WHERE catalog_id='{state.catalog}';")) for table in physical_tables}
        before_physical = physical_counts()
        owned_count = query(f"SELECT count(*) FROM trading.gas_owned_accounts WHERE catalog_id='{state.catalog}';")
        prefix = 'gas-park-custody-provider-only' if args.provider_only else 'gas-park-custody'
        packet = dict(profile=PROFILE, restricted=args.provider_only, artifactPrefix=prefix, session=session, closes=closes, cases=cases, policies=policies,
            referenceBundle=manifest['referenceCatalogId'] + '@' + manifest['referenceRevisionId'], quantitySpecification=state.terms['quantity'], currency=state.terms['currency'],
            parkAt='2020-12-20T10:00:00Z', partialAt='2020-12-20T12:00:00Z', finalAt='2020-12-20T13:00:00Z',
            returnAt='2020-12-21T12:00:00Z', feeDueAt='2020-12-22T12:00:00Z')
        run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet, 'tests/browser/gas-park-custody.mjs',
            worker_enabled=False, timeout=720, api_bin=args.api_bin, artifacts=[prefix + '-entry.png', prefix + '-review.png', prefix + '-mobile.png', *[prefix + '-' + perspective + suffix for perspective in sorted({case['perspective'] for case in cases}) for suffix in ['-history.png', '-raw-history.png']]])
        for case in cases:
            trade = query(f"SELECT trade_id FROM trading.gas_park_custody_trades WHERE(catalog_id,book_id)=('{state.catalog}','{case['bookId']}');")
            assert trade and '\n' not in trade
            source = state.read(trade)
            assert source['implementation'] == PROFILE and source['ownedAccountId'] == case['ownedAccountId']
            assert source['balances']['inCustody'] == '0' and source['balances']['reserved'] == '0'
            assert len(source['returns']) == 2 and source['reservations'] == [] and not source['reconciliationRequired']
            assert source['cash']['amount'] == case['signedFee']
            assert sum((Decimal(payment['amount']) for payment in source['cash']['payments']), Decimal(0)) == (Decimal(0) if args.provider_only else Decimal(case['signedPayment']))
            assert Decimal(source['cash']['amount']) - sum((Decimal(payment['amount']) for payment in source['cash']['payments']), Decimal(0)) == Decimal(case['expectedUnpaid'])
            if case['ownedAccountId']:
                holdings = state.holdings(case['bookId'])
                assert len(holdings['positions']) == 1 and holdings['positions'][0]['id'] == case['lotId']
                assert holdings['positions'][0]['remaining_quantity'] == '1000'
        assert query(f"SELECT count(*) FROM trading.gas_owned_accounts WHERE catalog_id='{state.catalog}';") == owned_count
        assert query(f"SELECT count(*) FROM trading.gas_owned_loans WHERE catalog_id='{state.catalog}';") == '0'
        assert physical_counts() == before_physical
        print('PASS custody UI native postconditions: retained customer title, released exact reservations, foreign provider custody, independent cash and unchanged physical facts', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
