"""Qualify whole-package Transport booking and financial actions on an owned preview."""
import argparse
from decimal import Decimal
import json
import os
from pathlib import Path
import shutil
import tempfile
from uuid import uuid4

from check_reference_editing import main as native_main
from close_session_fixture import sql_session
from gas_transport_financial_fixture import FinancialTransportFixture, PROFILE, RIGHTS_POLICY, FEE_PROFILE, EXPIRY_POLICY, fixture, literal
from owned_lifecycle_browser import api_artifact, build_assets, run as run_browser
from preview_fixture import catalogue_fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--api-bin', type=Path, help='Coherent frozen current-tree API artifact, with --no-build')
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
        print('Financial Transport browser fixture migrations loaded.', flush=True)
        state = FinancialTransportFixture(query, manifest)
        previous_catalogue = json.loads(state.call(f"SELECT security.read_current_catalogue('{state.catalog}',NULL)::text;", profile='approver'))
        query(catalogue_fixture(manifest, manifest['identityIssuer'], curve_sources=[state.definition_revision],
            expected_head=previous_catalogue['id'], request_id=str(uuid4())))
        selected_catalogue = json.loads(state.call(f"SELECT security.read_current_catalogue('{state.catalog}',NULL)::text;", profile='approver'))
        assert selected_catalogue['roles'] == previous_catalogue['roles'], 'Adding the explicit R2 curve source replaced a catalogue role.'
        assert {curve['definitionRevisionId'] for curve in selected_catalogue['curves'] if curve['code'] in ('transport_rights', 'provider_transport_rights')} == {state.definition_revision}
        policy = state.expiry_policy()
        cases = []
        for perspective, direction, amount, curve, value in [
                ('customer', 'pay', '12.5', 'transport_rights', '20'),
                ('provider', 'receive', '12.5', 'provider_transport_rights', '-20'),
                ('provider', 'pay', '12.5', 'transport_rights', '20'),
                ('customer', 'receive', '12.5', 'transport_rights', '20'),
                ('customer', 'pay', '0', 'transport_rights', '20')]:
            key = 'synthetic_browser_transport_' + perspective + '_' + direction + ('_zero' if amount == '0' else '')
            book_name = 'Synthetic Transport ' + perspective + ' ' + direction + (' zero fee' if amount == '0' else ' fixed fee')
            book, definition, outcome = state.new_capacity(key, name=book_name)
            fee = -Decimal(amount) if direction == 'pay' else Decimal(amount)
            provider = curve == 'provider_transport_rights'
            cases.append(dict(businessKey=key, bookId=book, bookName=book_name, entitlementId=definition['entitlementId'], capacityVersionId=outcome['versionId'],
                definition=definition, perspective=perspective, feeDirection=direction, fixedFeeAmount=amount, signedFee=str(fee),
                curveCode=curve, contractId=state.provider_contract if provider else state.contract,
                observationId=state.provider_observation if provider else state.observation,
                expectedValue=str(Decimal(value) + fee), rightsValue=value))
        assert len({case['bookId'] for case in cases}) == len(cases)
        query("DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='trader') "
              "AND permission IN('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits');"
              "INSERT INTO security.role_permissions SELECT id,'PublishMarketData' FROM security.roles WHERE code='trader' ON CONFLICT DO NOTHING;")
        curves = ['transport_rights', 'provider_transport_rights']
        sessions = dict.fromkeys(['initial', 'expiry'], 'synthetic_transport_financial_close')
        frame = state.frames[0]
        sql_session(query, manifest, sessions['initial'], 'every_day', '16:00:00', curves, ['trader', 'viewer', 'editor'],
            revision=state.definition_revision, effective=('2020-01-01', '2022-01-01'))
        pins = json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,"
            f"'observationId',id,'inputUse','variable') ORDER BY series_id) FROM market_data.observations "
            f"WHERE catalog_id='{state.catalog}' AND id IN(SELECT value::uuid FROM jsonb_array_elements_text({literal(frame['observationIds'])}));"))
        members = [dict(definitionRevisionId=state.definition_revision, curve=curve, contractId=contract, ordinal=index, scope='root')
            for curve, contracts in frame['curveContracts'].items() for index, contract in enumerate(contracts)]
        closes = dict(initial=state.call(f"SELECT security.register_market_close('{state.catalog}','{state.definition_revision}','{uuid4()}',"
            f"'Synthetic financial Transport close','{sessions['initial']}','{frame['at'][:10]}','{frame['at']}',"
            f"{literal(pins)},{literal(members)});"), expiry=None)
        # Through is a separate working evaluation instant with the same exact
        # R2 rate pins as the managed matrix. There is no rights observation,
        # inferred close or carried package mark at Through.
        manifest['frames'].append(dict(at=state.frames[1]['at'], observationIds=list(state.frames[1]['observationIds']), frozenObservationIds=[], curveContracts={}))
        unaffected = ['gas_owned_accounts', 'gas_owned_positions', 'gas_owned_loans', 'storage_facility_activities',
                      'storage_facility_plans', 'facility_gas_allocations', 'gas_transport_capacity_plans', 'gas_transport_route_plans']
        def counts():
            return {name: int(query(f"SELECT count(*) FROM trading.{name} WHERE catalog_id='{state.catalog}';")) for name in unaffected}
        before = counts()
        assert int(query(f"SELECT count(*) FROM trading.trades WHERE catalog_id='{state.catalog}';")) == 0, 'The empty-workspace consumer starts before any financial booking.'
        packet = dict(profile=PROFILE, rightsPolicy=RIGHTS_POLICY, feeProfile=FEE_PROFILE, expiryPolicy=EXPIRY_POLICY,
            sessions=sessions, closes=closes, cases=cases, expiryPolicyId=policy,
            valuationDefinition=state.definition_revision, valuationBundle=state.reference + '@' + state.valuation_reference,
            valuationReferenceCatalogId=state.reference, valuationReferenceRevisionId=state.valuation_reference,
            operatingBundle=manifest['referenceCatalogId'] + '@' + manifest['referenceRevisionId'], packageQuantity='whole_transport_package', currency='usd',
            legalEntity='d4000000-0000-0000-0000-000000000001@1', counterparty='d5000000-0000-0000-0000-000000000001@1',
            valuationAt=state.valuation_at, through='2020-12-21T00:00:00Z', feeDueAt='2020-12-23T16:00:00Z')
        artifacts = ['transport-financial-entry.png', 'transport-financial-review.png', 'transport-financial-risk.png',
                     'transport-financial-payment.png', 'transport-financial-closed.png', 'transport-financial-reader.png', 'transport-financial-mobile.png',
                     'transport-financial-market-source.png']
        images = Path(tempfile.mkdtemp(prefix='openetrm-financial-browser-artifacts-'))
        images.chmod(0o700)
        root = Path(query('SHOW unix_socket_directories;')).resolve()
        try:
            run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet,
                'tests/browser/gas-transport-financial.mjs', worker_enabled=False, timeout=1200, api_bin=args.api_bin)
        finally:
            for name in artifacts:
                if (root / name).is_file():
                    shutil.copyfile(root / name, images / name)
                    (images / name).chmod(0o600)
            print('Financial Transport browser screenshots: ' + str(images), flush=True)
        for index, case in enumerate(cases):
            trade = query(f"SELECT trade_id FROM trading.gas_transport_financial_trades WHERE(catalog_id,book_id)=('{state.catalog}','{case['bookId']}');")
            assert trade and '\n' not in trade
            source = state.read_financial(trade, at=packet['through'], context=state.capture(day='2020-12-21'))
            assert source['implementation'] == PROFILE and source['financialState']['status'] == 'closed'
            assert source['terms']['valuationReferenceCatalogId'] == state.reference and source['terms']['valuationReferenceRevisionId'] == state.valuation_reference
            assert source['terms']['operationalReferenceCatalogId'] == manifest['referenceCatalogId']
            assert source['terms']['operationalReferenceRevisionId'] == manifest['referenceRevisionId']
            assert source['terms']['feeDirection'] == case['feeDirection'] and source['terms']['fixedFeeAmount'] == case['fixedFeeAmount']
            if case['fixedFeeAmount'] == '0':
                assert source['cash'] is None
            else:
                assert Decimal(source['cash']['amount']) == Decimal(case['signedFee'])
                assert source['cash']['bindingId'] != source['instrumentId']
                assert len(source['cash']['payments']) == (1 if index == 0 else 0)
                assert sum((Decimal(payment['amount']) for payment in source['cash']['payments']), Decimal(0)) == (Decimal('-5') if index == 0 else Decimal(0))
        assert counts() == before, 'Financial Transport changed independent title, physical flow, route or chosen-capacity plans.'
        observations = json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('value',value::text,'role',role,'state',publication_state,'corrects',corrects_id)) "
            f"FROM market_data.observations WHERE(catalog_id,definition_revision_id,series_id)=('{state.catalog}','{state.definition_revision}','{state.provider_series}');"))
        assert any(row['value'] == '-21' and row['role'] == 'trader_mark' and row['state'] == 'final' and row['corrects'] == state.provider_observation for row in observations)
        assert any(row['value'] == '-20' and row['corrects'] is None for row in observations)
        print('PASS native browser postconditions: exact mixed references, whole packages, independent fees/payments, explicit closed rights and unchanged physical/title owners', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
