"""Independent cash/exposure oracles for temporary, unit-aware deal valuation."""
from copy import deepcopy
from fractions import Fraction as F
from preview_client import PreviewClient


def run_checks(url, query, manifest):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    w = c.call('workspace'); frame = next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    catalog = c.call('swaps/catalog', context)
    quantity = {'revision': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId']}, 'code': 'test_gas_quantity'}
    rule = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 18, 'dayOfMonth': None, 'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None, 'implementation': 'monthly_payment_date_rules_v1'}
    before = query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;")

    def draft_for(curve, amount='100'):
        index = next(i for i in catalog['indices'] if i['curve'] == curve)
        daily = curve.startswith('daily_')
        return {'context': context, 'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'],
                'quantity': {'value': amount, 'specification': index['quantityChoices'][0]['specification']}, 'fixedCoefficient': '-1', 'discounting': False,
                'fixed': {'price': '3', 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': rule},
                'floating': {'curve': curve, 'index': index['index'], 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'firstFixingAt': '2020-12-31T12:00:00Z', 'paymentSchedule': rule, 'resetFrequency': 'daily' if daily else 'monthly', 'averagingMethod': 'arithmetic' if daily else 'none'}}

    def run(draft, pv, deltas):
        priced = c.call('swaps/price', draft)
        assert F(priced['valuation']['presentValue']) == pv and priced['unbooked']
        pinned = {**draft, 'context': {**context, 'knowledgeMode': 'retained', 'contextId': priced['context']['contextId']}}
        request = {'draft': pinned, 'bumpPolicy': priced['bumpPolicy'], 'numericalPolicy': priced['numericalPolicy']}
        risk = c.call('swaps/draft-risk', request)
        assert risk['context']['contextId'] == priced['context']['contextId']
        assert len(risk['factors']) == len(deltas) and risk['scenarioCount'] == 1+2*len(deltas)
        assert sorted(F(r['delta']) for r in risk['factors']) == sorted(deltas)
        for row in risk['factors']:
            delta, bump = F(row['delta']), F(row['bump'])
            assert F(row['basePv']) == pv and F(row['gamma']) == 0
            assert F(row['upPnl']) == delta*bump and F(row['downPnl']) == -delta*bump
            assert F(row['upPv']) == pv+delta*bump and F(row['downPv']) == pv-delta*bump
        assert c.call('swaps/draft-risk', request)['factors'] == risk['factors']
        return priced, request

    base = draft_for('base_forward')
    point = next(p for p in c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'base_forward'})['points'] if p['deliveryFrom'] == '2021-01-01')
    pv = (F(point['value'])-3)*100
    priced, request = run(base, pv, [F(100), F(100)])
    negative = deepcopy(base); negative['fixedCoefficient'] = '1'
    run(negative, -pv, [F(-100), F(-100)])
    run(draft_for('daily_gas', '1'), F('0.16'), [F(1, 31)]*31)
    for curve, sensitivities in [('local_gas', [100]*3), ('local_power', [700]*3+[100]*2), ('local_power_all_in', [100])]:
        draft = draft_for(curve)
        point = next(p for p in c.call('evaluate', {'snapshotId': frame['id'], 'curve': curve})['points'] if p['deliveryFrom'] == '2021-01-01')
        run(draft, (F(point['value'])-3)*100, list(map(F, sensitivities)))
    # The submitted amount is in therms; the pricer consumes the canonical MMBtu amount.
    index = next(i for i in catalog['indices'] if i['curve'] == 'base_forward')
    therm = next(q for q in index['quantityChoices'] if q['unit'] == 'therm (IT)')
    converted = deepcopy(base); converted['quantity'] = {'value': '1000', 'specification': therm['specification']}
    result, _ = run(converted, pv, [F(100), F(100)])
    assert F(result['quantity']['amount']) == 100 and F(result['enteredQuantity']['amount']) == 1000, result
    # Explicit policy, typed quantity and context are required; malformed requests are 4xx.
    for edit in [{'context': None}, {'quantity': None}, {'quantity': {'value': None, 'specification': quantity}}]:
        c.call('swaps/price', {**base, **edit}, expected=422)
    omitted = deepcopy(base); del omitted['discounting']
    c.call('swaps/price', omitted, expected=400)
    c.call('swaps/price', {**base, 'discounting': None}, expected=400)
    c.call('swaps/draft-risk', {**request, 'draft': None}, expected=422)
    c.call('swaps/draft-risk', {**request, 'numericalPolicy': 'unselected'}, expected=422)
    futures_context = {**context, 'convention': w['futuresConvention']}
    futures = c.call('futures/catalog', futures_context)
    contract = futures['contracts'][0]
    future = {'context': futures_context, 'curve': contract['curve'], 'contractId': contract['id'], 'lots': '-2', 'executionPrice': contract['mark']}
    fp = c.call('futures/price', future)
    assert fp['measure'] == 'execution_pnl' and F(fp['executionPnl']) == 0
    fr = c.call('futures/draft-risk', {'draft': {**future, 'context': {**futures_context, 'knowledgeMode': 'retained', 'contextId': fp['context']['contextId']}}, 'bumpPolicy': fp['bumpPolicy'], 'numericalPolicy': fp['numericalPolicy']})
    assert len(fr['factors']) == 1
    expected_delta = -2*F(contract['contractSize'])*F(contract['quoteMultiplier'])
    assert F(fr['factors'][0]['delta']) == expected_delta and F(fr['factors'][0]['gamma']) == 0
    for field in ['lots', 'executionPrice', 'context']:
        c.call('futures/price', {**future, field: None}, expected=422)
    # Price authority cannot substitute for the distinct deal-risk capability.
    query("DELETE FROM security.role_permissions WHERE permission='RunDealRisk' AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    try:
        c.call('swaps/price', base)
        c.call('swaps/draft-risk', request, expected=403)
    finally:
        query("INSERT INTO security.role_permissions SELECT id,'RunDealRisk' FROM security.roles WHERE code='trader' ON CONFLICT DO NOTHING;")
    # A reader may inspect a hypothetical price but cannot run risk or book it.
    c.call('profile', {'profile': 'viewer'})
    c.call('swaps/draft-risk', request, expected=403)
    assert before == query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;")
    print('PASS unbooked deal price/risk: independent base/basis/all-in and signed exposures, exact 31-day rational sensitivities, therm conversion, pinned replay, explicit policies, denied authority and no booking writes', flush=True)
    return c
