"""Qualify explicit balance-time fee entry and performance through the owned UI."""
import argparse
from decimal import Decimal
import json
import os
from pathlib import Path
import shutil
from uuid import uuid4

from check_reference_editing import main as native_main
from close_session_fixture import sql_session
from gas_balance_fee_fixture import PROFILE, terms
from gas_park_loan_fixture import ParkLoanFixture, fixture, literal
from owned_lifecycle_browser import build_assets, run as run_browser


def number(value):
    return '0' if Decimal(value) == 0 else format(Decimal(value).normalize(), 'f')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true', help='Use coordinated frozen API and web artifacts')
    args = parser.parse_args()
    for path in [args.pg_bin / 'psql', args.dotnet, args.browser]:
        if not path.is_file():
            parser.error('An installed executable is required: ' + str(path))
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    children = []
    if not args.no_build:
        build_assets(args.dotnet.resolve())

    def checks(query, manifest):
        state = ParkLoanFixture(query, manifest)
        cases = []
        for kind, perspective, receiver in [('loan', 'customer', True), ('loan', 'provider', False),
                                             ('park', 'customer', False), ('park', 'provider', True)]:
            for direction in ['pay', 'receive']:
                label = 'balance_browser_' + kind + '_' + perspective + '_' + direction
                owned, book = state.account(label)
                claim = state.account(label + '_claim', kind='operator_balance', book=book)[0] if not receiver else None
                policies = {}
                for action in ['gas.owned.receive_title', 'gas.owned.dispatch', 'gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment']:
                    policies[action] = state.bind(owned, action)
                if claim:
                    for action in ['gas.operator.record', 'gas.operator.settle', 'gas.cash.record_payment']:
                        policies[action] = state.bind(claim, action)
                policies['gas.park_loan.fee_determine'] = state.bind(owned if receiver else claim, 'gas.park_loan.fee_determine')
                state.at, state.day = '2020-12-23T16:00:00Z', '2020-12-23'
                stock = None
                if not receiver:
                    supply = state.evidence(owned, label + '_funding', 'title_receipt', '720', '2020-12-18T08:00:00Z')
                    stock = state.native(owned, 'receive_title', dict(evidenceId=supply, quantity='720', titleAt='2020-12-18T08:00:00Z',
                        availableAt='2020-12-18T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'),
                        policy=policies['gas.owned.receive_title'])['id']
                sign = Decimal('-1' if direction == 'pay' else '1')
                # Independent literal oracles: 720*1h*.004=2.88;
                # 450*1h*.004=1.80. The complete remaining-return plan ends13:00.
                stages = {'before': ('8.64', '8.64'), 'advanced': ('8.64', '8.64'), 'partial': ('6.48', '6.48'),
                    'determined_first': ('3.6', '6.48'), 'paid_first': ('3.6', '3.6'), 'returned': ('1.8', '1.8'),
                    'determined_final': ('0', '1.8'), 'paid_final': ('0', '0')}
                expected = {stage: dict(service=number(sign * Decimal(service)), book={key: number(Decimal('0' if receiver else base) + sign * Decimal(total))
                    for key, base in [('base', '2160'), ('up', '2880'), ('down', '1440')]}) for stage, (service, total) in stages.items()}
                expected['determined_first']['native'] = {key: number(Decimal('0' if receiver else base) + sign * Decimal('2.88'))
                    for key, base in [('base', '2160'), ('up', '2880'), ('down', '1440')]}
                cases.append(dict(serviceKind=kind, perspective=perspective, receiver=receiver, feeDirection=direction,
                    bookId=book, ownedAccountId=owned, claimAccountId=claim, fundingLotId=stock,
                    legalEntityId=state.entity, counterpartyId=state.party, expected=expected,
                    firstCash=number(sign * Decimal('2.88')), finalCash=number(sign * Decimal('1.8')), cumulative=number(sign * Decimal('4.68')),
                    agreementEvidenceId=state.evidence(owned, label + '_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z'),
                    evidenceId=state.evidence(owned, label + '_advance', 'title_receipt' if receiver else 'physical_delivery', '720', '2020-12-20T10:00:00Z'),
                    claimEvidenceId=state.evidence(claim, label + '_claim', 'operator_statement', '720', '2020-12-20T10:00:00Z') if claim else None,
                    partialEvidenceId=state.evidence(owned, label + '_partial', 'loan_return' if receiver else 'title_receipt', '270', '2020-12-20T11:00:00Z'),
                    finalEvidenceId=state.evidence(owned, label + '_final', 'loan_return' if receiver else 'title_receipt', '450', '2020-12-20T12:00:00Z')))
        assert len({case['bookId'] for case in cases}) == len(cases) == 8
        future_owned, future_book = state.account('balance_browser_future_entry')
        assert future_book not in {case['bookId'] for case in cases}
        future_entry = dict(serviceKind='loan', perspective='customer', receiver=True, feeDirection='pay',
            bookId=future_book, ownedAccountId=future_owned, claimAccountId=None,
            legalEntityId=state.entity, counterpartyId=state.party, expected={'before': {'service': '-8.64'}})
        market = state.valuation_inputs()
        session = 'synthetic_balance_fee_close'
        sql_session(query, manifest, session, 'test_utc_every_day', '16:00:00', ['future'], ['trader'], effective=('2020-01-01', '2022-01-01'))
        query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
        closes = {label: state.call(f"SELECT security.register_market_close('{state.catalog}','{manifest['revisionId']}','{uuid4()}',"
            f"'Synthetic balance fee {label}','{session}','{day}','{day}T16:00:00Z',{literal(market['pins'])},{literal(market['members'])});")
            for label, day in [('before', '2020-12-18'), ('flow', '2020-12-20')]}
        packet = dict(profile=PROFILE, balanceFee=terms('0.004', 'to_even', '0.01'), currency=state.terms['currency'], session=session, closes=closes, cases=cases, futureEntry=future_entry,
            advanceAt='2020-12-20T10:00:00Z', partialAt='2020-12-20T11:00:00Z', returnAt='2020-12-20T12:00:00Z', feeDueAt='2020-12-23T16:00:00Z',
            **{key: market[key] for key in ['contractId', 'definitionRevisionId']})
        artifacts = ['gas-balance-fee-entry.png', 'gas-balance-fee-review.png', 'gas-balance-fee-mobile.png', 'gas-balance-fee-risk.png']
        try:
            run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet, 'tests/browser/gas-balance-fee.mjs',
                worker_enabled=False, timeout=900, artifacts=artifacts)
        finally:
            owned = Path(query('SHOW unix_socket_directories;')).resolve()
            for name in artifacts:
                if (owned / name).is_file():
                    shutil.copyfile(owned / name, Path('/tmp') / name)
        for case in cases:
            trade = query(f"SELECT trade_id FROM trading.gas_park_loan_trades WHERE(catalog_id,owned_account_id)=('{state.catalog}','{case['ownedAccountId']}');")
            assert trade and '\n' not in trade
            context = state.capture()
            source = json.loads(state.call(f"SELECT security.use_market_context('{state.catalog}','{context}');SELECT security.use_trading_context('{state.catalog}','{state.at}');"
                f"SELECT security.read_gas_park_loan('{state.catalog}','{trade}','{context}','{state.at}')::text;"))
            assert source['implementation'] == PROFILE
            assert source['balances'] == dict(acceptedPrincipal='720', acceptedReturn='720', outstandingPrincipal='0')
            assert source['fee']['cumulativeDeterminedTotal'] == case['cumulative']
            assert source['fee']['unpaid'] == '0' and len(source['fee']['determinations']) == 2
            assert sum(Decimal(row['paid']) for row in source['fee']['determinations']) == Decimal(case['cumulative'])
            assert all(row['unpaid'] == '0' for row in source['fee']['determinations']) and not source['reconciliationRequired']
        assert query(f"SELECT count(*) FROM trading.gas_park_loan_trades WHERE(catalog_id,owned_account_id)=('{state.catalog}','{future_owned}');") == '1'
        assert query(f"SELECT count(*) FROM trading.gas_park_loan_advances a JOIN trading.gas_park_loan_trades t ON(t.catalog_id,t.trade_id)=(a.catalog_id,a.trade_id) WHERE(t.catalog_id,t.owned_account_id)=('{state.catalog}','{future_owned}');") == '0'
        print('PASS balance-time UI: all four commercial roles and both fee signs, exact2.88/1.80 cash, independent payments and no duplicated remaining service value', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin', str(args.pg_bin.resolve())], cleanup_ready=lambda: not children)


if __name__ == '__main__':
    main()
