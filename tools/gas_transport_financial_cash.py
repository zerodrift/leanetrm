"""Independent fee owners, bank histories and company-signed provider sources."""
from uuid import uuid4

from gas_transport_financial_fixture import literal


def provider_and_cash(f, customer, rejected):
    from gas_transport_financial_managed import value
    original_book, original_entitlement, original_version, original_at = f.book, f.entitlement, f.capacity_version, f.at
    f.at = '2020-12-19T23:59:59.999999Z'
    stock_tables = ['gas_owned_positions', 'gas_owned_lots', 'gas_owned_reservations', 'gas_owned_consumptions', 'gas_owned_loans',
        'gas_owned_claim_settlements', 'storage_facility_activities', 'facility_quantity_revisions']

    def stock():
        return f.query('SELECT jsonb_object_agg(name,rows) FROM(' + ' UNION ALL '.join(
            f"SELECT '{table}'::text name,count(*) rows FROM trading.{table} WHERE catalog_id='{f.catalog}'" for table in stock_tables) + ')s;')

    before = stock()
    zero = None
    try:
        for fee, direction in [('12.5', 'receive'), ('12.5', 'pay'), ('0', 'receive')]:
            key = 'synthetic_provider_' + direction + ('_zero' if fee == '0' else '')
            book, definition, capacity = f.new_capacity(key)
            f.book = book
            f.entitlement, f.capacity_version = definition['entitlementId'], capacity['versionId']
            request = f.booking(key=key, perspective='provider', fee=fee, direction=direction)
            booked = f.book_financial(request)
            mark = {**f.mark(booked['tradeId'], request['operationId']), 'curveCode': 'provider_transport_rights',
                'contractId': f.provider_contract, 'seriesId': f.provider_series, 'observationId': f.provider_observation}
            f.record_financial(mark)
            row = f.read_financial(booked['tradeId'])
            assert row['terms']['perspective'] == 'provider' and row['mark']['observationId'] == f.provider_observation
            assert f.query(f"SELECT trim_scale(value)::text FROM market_data.observations WHERE(catalog_id,id)=('{f.catalog}','{row['mark']['observationId']}');") == '-20'
            assert (row['cash'] is None) == (fee == '0')
            value(f, booked['tradeId'], rate_ids=[] if fee == '0' else f.rate_observations[f.valuation_at], mark=row['mark'],
                expected='-20' if fee == '0' else '-7.5' if direction == 'receive' else '-32.5')
            if fee != '0':
                assert row['cash']['amount'] == ('12.5' if direction == 'receive' else '-12.5')
            else:
                zero = row
                from gas_transport_financial_receipts import booking_context
                booking_context(f, booked['tradeId'], rejected)
            assert stock() == before
    finally:
        f.book, f.entitlement, f.capacity_version, f.at = original_book, original_entitlement, original_version, original_at
    print('PASS independent signed provider mark stays-20 with either explicitly selected fee sign; zero fee creates no cash owner and no financial booking/adoption creates gas, custody, loan or physical facts.', flush=True)
    from gas_transport_financial_fences import fences
    fences(f, customer['tradeId'], zero)

    trade = customer['tradeId']
    at = '2020-12-21T00:00:00Z'
    row = f.read_financial(trade, at=at)
    assert row['financialState']['status'] == 'closed'
    binding = row['cash']['bindingId']
    payment = dict(operationId=str(uuid4()), tradeId=trade, bindingId=binding, allocationId=str(uuid4()),
        expectedRevision=0, policy='explicit_paid_cash_nominal_due_v1', paidAt=at, amount='-5')

    def record(body, when=at):
        return f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(body)},'{when}');")

    for amount in ['-13', '5']:
        rejected(lambda: record({**payment, 'operationId': str(uuid4()), 'amount': amount}), 'gas_transport_financial_cash_payment')
    rejected(lambda: record({**payment, 'operationId': str(uuid4()), 'paidAt': '2020-12-19T15:59:59Z'}), 'cash_payment_membership_time')
    first = {**payment, 'operationId': str(uuid4()), 'allocationId': str(uuid4()), 'amount': '-20'}
    second = {**first, 'operationId': str(uuid4()), 'expectedRevision': 1, 'amount': '-5'}
    rejected(lambda: f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(first)},'{at}');"
        f"SELECT security.record_cash_payment('{f.catalog}',{literal(second)},'{at}');"), 'gas_transport_financial_cash_payment')
    accepted = record(payment)
    assert record(payment) == accepted
    before_correction = f.capture()
    first_read = f.read_financial(trade, context=before_correction, at=at)
    assert len(first_read['cash']['payments']) == 1 and first_read['cash']['payments'][0]['amount'] == '-5'
    value(f, trade, rate_ids=f.rate_observations[at], at=at, expected='-7.5')
    future = '2020-12-22T00:00:00Z'
    record({**payment, 'operationId': str(uuid4()), 'expectedRevision': 1, 'paidAt': future, 'amount': '-4'}, future)
    current = f.read_financial(trade, at=at)
    assert len(current['cash']['payments']) == 2
    assert [p['revision'] for p in current['cash']['payments']] == [1, 2]
    assert current['cash']['payments'][1]['paid_at'].startswith('2020-12-22T00:00:00')
    assert f.read_financial(trade, context=before_correction, at=at)['cash']['payments'] == first_read['cash']['payments']
    assert current['financialState']['status'] == 'closed' and current['expiry']['id'] == row['expiry']['id']
    value(f, trade, rate_ids=f.rate_observations[at], at=at, expected='-12.5')
    f.at = '2020-12-22T23:59:59.999999Z'
    try:
        value(f, trade, rate_ids=f.rate_observations[future], at=future, expected='-8.5')
        due = '2020-12-23T16:00:00Z'
        f.at = '2020-12-23T23:59:59.999999Z'
        value(f, trade, rate_ids=[], at=due, expected='-8.5')
        record({**payment, 'operationId': str(uuid4()), 'allocationId': str(uuid4()), 'expectedRevision': 0, 'paidAt': due, 'amount': '-8.5'}, due)
        value(f, trade, rate_ids=[], at=due, expected='0')
    finally:
        f.at = original_at
    print('PASS ordinary independent bank payments survive rights expiry, enforce every revision/sign/capacity/birth, and retain complete correction history including a known business-future bank head.', flush=True)
