"""Independent role, cash-envelope and cumulative-rounding examples."""
from decimal import Decimal
from uuid import uuid4

from gas_balance_fee_fixture import BalanceFeeService, booking, terms
from gas_park_loan_fixture import literal, rejected
from gas_park_loan_valuation import checkpoint


def role_checks(f):
    # Fee direction is selected independently of the four principal mappings.
    for kind, perspective, fee_direction in [('loan', 'customer', 'pay'), ('park', 'provider', 'receive'),
                                             ('loan', 'provider', 'receive'), ('park', 'customer', 'pay')]:
        label = 'balance_' + kind + '_' + perspective
        s = BalanceFeeService(f, label, kind=kind, perspective=perspective, fee_direction=fee_direction)
        sign = Decimal(-1 if fee_direction == 'pay' else 1)
        exposure = Decimal(720 if s.direction == -1 else 0)
        assert s.read()['advance'] is None and f.holdings(s.book)['positions'] == []
        checkpoint(f, s.booked, s.book, Decimal(0), sign * Decimal('8.64'), 3, label + ' booked')
        s.advance(lambda stage: checkpoint(f, s.booked, s.book, exposure, sign * Decimal('8.64'),
            3 if stage == 'before' else 1, label + ' same-clock ' + stage + ' native handoff'))
        assert s.read()['balances']['outstandingPrincipal'] == '720'
        assert s.read()['fee']['determinations'] == []
        s.returned('270', '2020-12-20T11:00:00Z', 'synthetic_partial')
        checkpoint(f, s.booked, s.book, exposure, sign * Decimal('6.48'), 1, label + ' partial before determination')
        first_body = s.selection('2020-12-20T11:00:00Z', 'synthetic_first')
        first = s.command('fee_determine', first_body)
        assert Decimal(first['roundedTotal']) == sign * Decimal('2.88')
        assert Decimal(first['contribution']) == sign * Decimal('2.88')
        assert (first['exactNumerator'], first['exactDenominator']) == (str(int(sign) * 72), '25')
        checkpoint(f, s.booked, s.book, exposure, sign * Decimal('6.48'), 1, label + ' determined separate cash')
        s.command('fee_payment', dict(expectedVersion=s.read()['stateVersion'], feeId=first['feeId'], businessKey='synthetic_bank',
            expectedRevision=0, paidAt='2020-12-20T11:05:00Z', amount=str(sign * Decimal('2.88')), evidence='Synthetic independent bank record'))
        checkpoint(f, s.booked, s.book, exposure, sign * Decimal('3.6'), 1, label + ' paid independent cash')
        s.returned('450', '2020-12-20T12:00:00Z', 'synthetic_final')
        final = s.command('fee_determine', s.selection('2020-12-20T12:00:00Z', 'synthetic_final_fee'))
        assert Decimal(final['roundedTotal']) == sign * Decimal('4.68')
        assert Decimal(final['contribution']) == sign * Decimal('1.8')
        assert (final['exactNumerator'], final['exactDenominator']) == (str(int(sign) * 117), '25')
        publication = f.publication()
        assert s.command('fee_determine', first_body)['changed'] is False
        assert f.publication() == publication
        state = s.read()
        assert state['fee']['cumulativeDeterminedTotal'] == str(sign * Decimal('4.68'))
        assert Decimal(state['fee']['unpaid']) == sign * Decimal('1.8')
        assert len(state['fee']['determinations']) == 2 and state['balances']['outstandingPrincipal'] == '0'
        # Fully returned borrower books have only independent cash and no gas
        # factor; no invented gas scenario is added to manufacture a risk axis.
        if s.direction == -1:
            checkpoint(f, s.booked, s.book, exposure, sign * Decimal('1.8'), 0, label + ' final native cash survives principal')
        print('PASS ' + label + ': independent ' + fee_direction + ', partial6.48, first2.88, final4.68/delta1.80, no duplicate cash or principal.', flush=True)


def rounding_checks(f):
    # Rounding each period gives .00+.00 or .01+.01; cumulative difference must
    # instead determine total .01 after two half-cent hours under either rule.
    for tie, first_total in [('to_even', '0'), ('away_from_zero', '-0.01')]:
        s = BalanceFeeService(f, 'balance_tie_' + tie, quantity='1', rate='0.005', tie=tie)
        s.advance()
        first = s.command('fee_determine', s.selection('2020-12-20T11:00:00Z', 'synthetic_half'))
        assert first['roundedTotal'] == first_total
        assert (first['exactNumerator'], first['exactDenominator']) == ('-1', '200')
        second = s.command('fee_determine', s.selection('2020-12-20T12:00:00Z', 'synthetic_full'))
        assert second['roundedTotal'] == '-0.01'
        assert Decimal(second['contribution']) == Decimal('-0.01') - Decimal(first_total)
        assert (first['feeId'] is None) == (first_total == '0')
        assert (second['feeId'] is None) == (second['contribution'] == '0')
    micro = BalanceFeeService(f, 'balance_microsecond', increment='0.000000000000000001')
    micro.advance()
    determined = micro.command('fee_determine', micro.selection('2020-12-20T10:00:00.000001Z', 'synthetic_microsecond'))
    assert (determined['exactNumerator'], determined['exactDenominator']) == ('-1', '1250000000')
    assert determined['roundedTotal'] == determined['contribution'] == '-0.0000000008'
    zero = BalanceFeeService(f, 'balance_zero_rate', rate='0')
    zero.advance()
    determined = zero.command('fee_determine', zero.selection('2020-12-20T11:00:00Z', 'synthetic_zero_rate'))
    assert determined['roundedTotal'] == determined['contribution'] == '0' and determined['feeId'] is None
    print('PASS explicit cumulative tie rules, zero rate, exact UTC microseconds and zero-delta owner suppression.', flush=True)
    correction_check(f)


def correction_check(f):
    s = BalanceFeeService(f, 'balance_source_correction')
    s.advance()
    first_body = s.selection('2020-12-20T11:00:00Z', 'synthetic_first')
    operation, first_context = str(uuid4()), f.capture()
    first = s.command('fee_determine', first_body, operation=operation, context=first_context)
    assert first['contribution'] == '-2.88'
    source = f.capture()
    original = s.read(source)
    body = s.selection('2020-12-20T12:00:00Z', 'synthetic_corrected_source')
    from gas_balance_fee_adversarial import typed_fact_probe
    probe = typed_fact_probe(f, s.trade, first, '2020-12-20T11:00:00Z', '2020-12-20T11:10:00Z')
    probe(source, 'valid')
    evidence = s.accepted['stockId']
    assert evidence is not None and original['reconciliationRequired'] is False
    f.evidence(s.owned, s.label + '_advance', 'title_receipt', '700', '2020-12-20T10:00:00Z', revision=1)
    assert s.read()['reconciliationRequired'] is True
    assert s.read(source)['reconciliationRequired'] is False
    before = f.publication()
    rejected(lambda: s.command('fee_determine', body), 'gas_park_loan_correction_required')
    assert f.publication() == before and len(s.read()['fee']['determinations']) == 1
    probe(f.capture(), 'valid', 'gas_park_loan_correction_required')
    probe(source, 'valid', 'gas_balance_fee_context')
    assert s.command('fee_determine', first_body, operation=operation, context=first_context) == first
    assert s.command('fee_determine', first_body)['changed'] is False
    assert len(s.read()['fee']['determinations']) == 1
    assert s.read()['balances']['outstandingPrincipal'] == '720', 'An observed correction silently changed accepted principal.'
    print('PASS later source-evidence correction requires reconciliation before new fee cash; prior knowledge and accepted principal remain intact.', flush=True)


def envelope_checks(f):
    owned, book = f.account('balance_cash_envelope')
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    body = booking(f, owned, None, book)
    def book_body(value, *, rollback=True):
        return f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(value)},'{f.at}');", rollback=rollback)
    before = f.publication()
    for field in ['basis', 'timeBasis', 'startPolicy', 'endPolicy', 'overduePolicy', 'projectionPolicy',
                  'unadvancedPolicy', 'determinationPolicy', 'roundingStage', 'roundingTieRule']:
        rejected(lambda: book_body({**body, 'operationId': str(uuid4()), 'balanceFee': {**terms(), field: 'synthetic_unsupported'}}), 'gas_balance_fee_terms')
    # One full hour at just below the existing cash limit is representable;
    # exactly the limit and a round-to-limit boundary are explicitly refused.
    largest = '999999999999999999999999'
    for selected in [
        {**body, 'quantity': largest, 'balanceFee': {**terms('1'), 'accrualThroughAt': '2020-12-20T13:00:00Z'}},
        {**body, 'quantity': '1', 'balanceFee': terms(largest)},
        {**body, 'balanceFee': {**terms(), 'roundingIncrement': '0'}},
        {**body, 'balanceFee': {**terms(), 'accrualThroughAt': '2020-12-20T11:59:59Z'}},
        {**body, 'feeDueAt': '2020-12-20T12:59:59Z'}]:
        rejected(lambda: book_body({**selected, 'operationId': str(uuid4())}),
            'gas_balance_fee_cash_range' if selected.get('quantity') == largest or selected['balanceFee']['rateAmount'] == largest else 'gas_balance_fee_terms')
    safe = {**body, 'operationId': str(uuid4()), 'quantity': largest, 'returnAt': '2020-12-20T11:00:00Z',
        'balanceFee': {**terms('1'), 'accrualThroughAt': '2020-12-20T11:00:00Z'}}
    book_body(safe)
    exactly_limit = {**safe, 'operationId': str(uuid4()), 'quantity': '500000000000000000000000',
        'returnAt': '2020-12-20T12:00:00Z', 'balanceFee': {**terms('1'), 'accrualThroughAt': '2020-12-20T12:00:00Z'}}
    rejected(lambda: book_body(exactly_limit), 'gas_balance_fee_cash_range')
    rounded_limit = {**safe, 'operationId': str(uuid4()), 'balanceFee': {**safe['balanceFee'], 'roundingIncrement': '2'}}
    rejected(lambda: book_body(rounded_limit), 'gas_balance_fee_cash_range')
    assert f.publication() == before
    print('PASS booking independently bounds the maximum cumulative rounded cash handoff, including round-to-limit boundaries.', flush=True)
