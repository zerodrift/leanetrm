"""Independent actual-ledger qualification for the balance-time fee profile."""
import json
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from gas_park_loan_fixture import ParkLoanFixture, fixture, literal, rejected


from gas_balance_fee_fixture import PROFILE, booking, terms


def checks(query, manifest):
    f = ParkLoanFixture(query, manifest)
    owned, book = f.account('balance_fee_borrower')
    for action in ['gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment']:
        f.bind(owned, action)
    f.at = '2020-12-19T16:00:00Z'; f.day = '2020-12-19'
    body = booking(f, owned, None, book)
    booked = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"))
    assert booked['revision'] == 1
    trade = booked['tradeId']

    def read(context=None):
        context = context or f.capture()
        return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');"
            f"SELECT security.use_trading_context('{f.catalog}','{f.at}');"
            f"SELECT security.read_gas_park_loan('{f.catalog}','{trade}','{context}','{f.at}')::text;"))

    initial = read()
    assert initial['implementation'] == PROFILE
    assert initial['terms']['balanceFee']['rateAmount'] == '0.004'
    assert initial['fee']['determinations'] == [] and initial['fee']['cumulativeDeterminedTotal'] == '0'
    assert f.holdings(book)['positions'] == [], 'A future fee-bearing commitment created actual stock.'
    print('PASS balance-time booking: explicit terms, independent fee direction, and no actual stock before acceptance.', flush=True)

    def command(action, value, *, operation=None, context=None, preview=False):
        return json.loads(f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{operation or uuid4()}',"
            f"'{trade}','{action}',{literal(value)},'{f.at}','{context or f.capture()}',{str(preview).lower()})::text;"))

    f.at = '2020-12-20T16:00:00Z'; f.day = '2020-12-20'
    agreement = f.evidence(owned, 'balance_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z')
    title = f.evidence(owned, 'balance_receipt', 'title_receipt', '720', '2020-12-20T10:00:00Z')
    advanced = command('advance', dict(expectedVersion=read()['stateVersion'], agreementEvidenceId=agreement,
        evidenceId=title, claimEvidenceId=None, lotId=None, reservationId=None, effectiveAt='2020-12-20T10:00:00Z'))
    assert advanced['feeId'] is None and read()['balances']['outstandingPrincipal'] == '720'

    def selection(through, previous, key, *, effective=None):
        return dict(expectedVersion=read()['stateVersion'], previousDeterminationId=previous, through=through,
            effectiveAt=effective or through, businessKey=key, evidence='Synthetic accepted balance-time determination')

    first_body = selection('2020-12-20T11:00:00Z', None, 'synthetic_first')
    rejected(lambda: command('fee_determine', first_body), 'gas_balance_fee_policy')
    f.bind(owned, 'gas.park_loan.fee_determine')
    publication = f.publication()
    preview = command('fee_determine', first_body, preview=True)
    assert f.publication() == publication and read()['fee']['determinations'] == []
    reviewed_context = f.capture()
    f.bind(owned, 'gas.park_loan.fee_determine')
    rejected(lambda: command('fee_determine', first_body, context=reviewed_context), 'gas_balance_fee_policy_changed')
    assert read()['fee']['determinations'] == []
    operation, context = str(uuid4()), f.capture()
    first = command('fee_determine', first_body, operation=operation, context=context)
    assert first['roundedTotal'] == first['contribution'] == '-2.88'
    assert (first['exactNumerator'], first['exactDenominator']) == ('-72', '25')
    assert preview['contribution'] == first['contribution'] and first['feeId'] is not None
    assert command('fee_determine', first_body, operation=operation, context=context) == first
    publication = f.publication()
    assert command('fee_determine', first_body)['changed'] is False and f.publication() == publication
    payment = dict(expectedVersion=read()['stateVersion'], feeId=first['feeId'], businessKey='synthetic_payment',
        expectedRevision=0, paidAt='2020-12-20T11:05:00Z', amount='-2.88', evidence='Synthetic independent bank payment')
    paid = command('fee_payment', payment)
    before_return = f.capture()
    assert read()['fee']['determinations'][0]['paid'] == '-2.88'
    assert read()['fee']['determinations'][0]['unpaid'] == '0'
    returned_evidence = f.evidence(owned, 'balance_late_return', 'loan_return', '270', '2020-12-20T10:30:00Z')
    command('return', dict(expectedVersion=read()['stateVersion'], businessKey='synthetic_partial', evidenceId=returned_evidence,
        lotId=advanced['stockId'], reservationId=None, quantity='270', effectiveAt='2020-12-20T10:30:00Z'))
    restated_body = selection('2020-12-20T11:00:00Z', first['id'], 'synthetic_restatement')
    restated = command('fee_determine', restated_body)
    assert (restated['exactNumerator'], restated['exactDenominator']) == ('-117', '50')
    assert restated['roundedTotal'] == '-2.34' and restated['contribution'] == '0.54'
    assert restated['feeId'] != first['feeId']
    current = read()
    assert current['balances']['outstandingPrincipal'] == '450'
    assert current['fee']['determinations'][0]['paid'] == '-2.88'
    assert current['fee']['determinations'][1]['unpaid'] == '0.54'
    historical = read(before_return)
    assert historical['balances']['outstandingPrincipal'] == '720'
    assert historical['fee']['cumulativeDeterminedTotal'] == '-2.88'
    assert command('fee_determine', first_body, operation=operation, context=context) == first
    final = command('fee_determine', selection('2020-12-20T13:00:00Z', restated['id'], 'synthetic_cutoff'))
    assert (final['exactNumerator'], final['exactDenominator']) == ('-297', '50')
    assert final['roundedTotal'] == '-5.94' and final['contribution'] == '-3.6'
    assert read()['balances']['outstandingPrincipal'] == '450', 'Fee cutoff discharged physical principal.'
    zero = command('fee_determine', selection('2020-12-20T13:00:00Z', final['id'], 'synthetic_zero',
        effective='2020-12-20T13:05:00Z'))
    assert zero['contribution'] == '0' and zero['feeId'] is None
    assert len(read()['fee']['determinations']) == 4
    assert query(f"SELECT count(*) FROM trading.gas_owned_fee_payments WHERE catalog_id='{f.catalog}' AND id='{paid['id']}';") == '1'
    assert query(f"SELECT count(*) FROM trading.gas_owned_fees WHERE catalog_id='{f.catalog}' AND id IN "
        f"(SELECT fee_id FROM trading.gas_park_loan_fee_determinations WHERE(catalog_id,trade_id)=('{f.catalog}','{trade}'));") == '3'
    current_context = f.capture()
    f.at = '2020-12-20T11:30:00Z'
    rejected(lambda: read(current_context), 'gas_balance_fee_context')
    assert read(before_return)['fee']['cumulativeDeterminedTotal'] == '-2.88'
    f.at = '2020-12-20T16:00:00Z'
    final_evidence = f.evidence(owned, 'balance_final_return', 'loan_return', '450', '2020-12-20T14:00:00Z')
    command('return', dict(expectedVersion=read()['stateVersion'], businessKey='synthetic_final', evidenceId=final_evidence,
        lotId=advanced['stockId'], reservationId=None, quantity='450', effectiveAt='2020-12-20T14:00:00Z'))
    assert read()['balances']['outstandingPrincipal'] == '0'
    assert read()['fee']['cumulativeDeterminedTotal'] == '-5.94' and read()['fee']['unpaid'] == '-3.06'
    print('PASS exact native fee: 720×.004×1h=2.88; paid cash survives backdated return; cumulative2.34 creates+.54 refund; cutoff5.94 leaves principal450; zero delta creates no cash.', flush=True)
    from gas_balance_fee_adversarial import acknowledgement_checks, typed_fact_checks, uncommitted_source_check
    uncommitted_source_check(f, trade, selection('2020-12-20T13:00:00Z', zero['id'], 'synthetic_uncommitted',
        effective='2020-12-20T13:10:00Z'))
    acknowledgement_checks(f, zero)
    fence_context = f.capture()
    fenced = f.call(f"SELECT security.use_market_context('{f.catalog}','{fence_context}');"
        f"SELECT security.use_trading_context('{f.catalog}','{f.at}');"
        f"SELECT security.gas_park_loan_projection_fence('{f.catalog}','{fence_context}','{f.at}',"
        f"'2020-12-21T16:00:00Z',ARRAY['{book}']::uuid[],ARRAY['{trade}']::uuid[]);")
    assert fenced == 't', 'Fully determined, principal-complete v2 continuation escaped the explicit economic projection fence: ' + fenced
    print('PASS full economic continuation remains explicitly fenced after all principal and fee determinations.', flush=True)
    typed_fact_checks(f, trade, zero)
    from gas_balance_fee_cases import envelope_checks, role_checks, rounding_checks
    envelope_checks(f)
    role_checks(f)
    rounding_checks(f)


if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture)
