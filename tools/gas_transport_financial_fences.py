"""Complete economic refusal is independent of current financial components."""
from uuid import uuid4


def fences(f, customer, zero):
    from gas_transport_financial_managed import value
    at = '2020-12-21T00:00:00Z'
    policy = f.expiry_policy()
    body = dict(kind='expire', tradeId=zero['tradeId'], expectedFinancialStateId=zero['financialState']['id'],
        policyId=policy, policyRevision=1, evidence='Synthetic explicit fee-free rights expiry')
    f.record_financial(body, effective=at, at=at)
    ended = f.read_financial(zero['tradeId'], at=at)
    assert ended['financialState']['status'] == 'closed' and ended['cash'] is None and ended['mark'] is None and ended['unpricedReason'] is None
    value(f, zero['tradeId'], rate_ids=[], at=at, expected='0')

    def fence(book, trade, context, opening, through):
        return f.call(f"SELECT security.gas_transport_financial_projection_fence('{f.catalog}','{context}','{opening}','{through}',"
            f"ARRAY['{book}']::uuid[],ARRAY['{trade}']::uuid[]);") == 't'

    context = f.capture()
    assert fence(zero['bookId'], zero['tradeId'], context, at, '2020-12-22T00:00:00Z')
    assert fence(f.book, customer, context, at, '2020-12-22T00:00:00Z')
    assert not fence(zero['bookId'], str(uuid4()), context, at, '2020-12-22T00:00:00Z')
    original = f.book, f.entitlement, f.capacity_version, f.at, f.valuation_at
    try:
        before = f.capture(day='2020-12-19')
        f.at = '2020-12-20T23:59:59.999999Z'
        f.valuation_at = '2020-12-20T00:00:00Z'
        f.book, definition, version = f.new_capacity('synthetic_future_financial_birth', effective=f.valuation_at)
        f.entitlement, f.capacity_version = definition['entitlementId'], version['versionId']
        request = f.booking(key='synthetic_future_financial_birth', perspective='provider', fee='0', direction='receive')
        future = f.book_financial(request)
        known = f.capture(day='2020-12-19')
        opening = '2020-12-19T16:00:00Z'
        assert not fence(f.book, future['tradeId'], before, opening, f.valuation_at)
        assert not fence(f.book, future['tradeId'], known, opening, '2020-12-19T23:59:59Z')
        assert fence(f.book, future['tradeId'], known, opening, f.valuation_at)
        assert f.call(f"SELECT security.require_gas_transport_financial_scope('{f.catalog}','{known}','{opening}',ARRAY['{f.book}']::uuid[],NULL);") == 'f'
    finally:
        f.book, f.entitlement, f.capacity_version, f.at, f.valuation_at = original
    print('PASS economic fence retains completed cash-only and closed zero-fee agreements, exact excluded selectors, source-known future birth inside horizon, and excludes births beyond horizon or beyond knowledge.', flush=True)
