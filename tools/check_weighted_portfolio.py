"""Explicit multi-source weights through saved graph, booking and the retained worker."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4

from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, manifest, query, run_worker):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    catalog = client.call('custom-curves')
    assert 'weighted_contracts' in {o['kind'] for o in catalog['operators']}
    assert 'sql_transform' not in {o['kind'] for o in catalog['operators']}
    sources = {s['code']: s for s in catalog['sources']}
    contracts = {name: next(k for k in sources[name]['contracts'] if k['deliveryFrom'] == '2021-01-01')['id']
                 for name in ('base_forward', 'local_gas')}
    definition = {'code': 'synthetic_weighted_portfolio', 'name': 'Synthetic weighted portfolio', 'output': 'output', 'nodes': [
        {'id': 'source_a', 'kind': 'source', 'parameters': {'curve': 'base_forward'}, 'inputs': {}},
        {'id': 'source_b', 'kind': 'source', 'parameters': {'curve': 'local_gas'}, 'inputs': {}},
        {'id': 'mapping', 'kind': 'weighted_contracts', 'parameters': {'aggregation': 'normalized_weighted_mean_v1'},
         'inputs': {'source_a': 'source_a', 'source_b': 'source_b'},
         'contractWeights': [{'outputPosition': 1, 'sourcePort': 'source_a', 'contractId': contracts['base_forward'], 'weight': '7'},
                             {'outputPosition': 1, 'sourcePort': 'source_b', 'contractId': contracts['local_gas'], 'weight': '3'}]},
        {'id': 'dates', 'kind': 'dates', 'parameters': {'mode': 'context', 'sampling': 'context_utc_time',
         'corrections': 'exact_context_frame_history_latest_visible'}, 'inputs': {}},
        {'id': 'sample', 'kind': 'sample', 'parameters': {}, 'inputs': {'selection': 'mapping', 'dates': 'dates'}},
        {'id': 'output', 'kind': 'output', 'parameters': {}, 'inputs': {'values': 'sample'}}]}
    panel = client.call('custom-curves/preview', {'definition': definition, 'snapshotId': frame['id']})
    prices = {s['contract']['id']: Fraction(s['price']['value']) for s in panel['inputs']}
    expected = (7 * prices[contracts['base_forward']] + 3 * prices[contracts['local_gas']]) / 10
    actual = panel['points'][0]['price']
    assert Fraction(int(actual['numerator']), int(actual['denominator'])) == expected
    formula = client.call('custom-curves/format', {'definition': definition})['formula']
    compiled = client.call('custom-curves/compile', {'formula': formula, 'code': definition['code'], 'name': definition['name'], 'snapshotId': frame['id']})['definition']
    assert next(n for n in compiled['nodes'] if n['kind'] == 'weighted_contracts')['contractWeights'] == definition['nodes'][2]['contractWeights']
    signed = deepcopy(definition)
    signed['nodes'][2]['parameters']['aggregation'] = 'signed_sum_v1'
    signed['nodes'][2]['contractWeights'][0]['weight'] = '0.7'
    signed['nodes'][2]['contractWeights'][1]['weight'] = '-0.3'
    signed_price = client.call('custom-curves/preview', {'definition': signed, 'snapshotId': frame['id']})['points'][0]['price']
    assert Fraction(int(signed_price['numerator']), int(signed_price['denominator'])) == Fraction(7, 10) * prices[contracts['base_forward']] - Fraction(3, 10) * prices[contracts['local_gas']]
    invalid = deepcopy(definition)
    invalid['nodes'][2]['contractWeights'][0]['contractId'] = contracts['local_gas']
    client.call('custom-curves/save', {'requestId': str(uuid4()), 'definition': invalid, 'snapshotId': frame['id'], 'layout': {}}, expected=422)
    saved = client.call('custom-curves/save', {'requestId': str(uuid4()), 'definition': definition, 'snapshotId': frame['id'], 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    published = client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved['id'], 'version': 1, 'generation': saved['generation']})
    assert published['engineVersion'] == 'custom_curve_recipe_v2'
    from check_recipe_weights_db import run_checks as check_weights
    check_weights(query, manifest, saved['id'])
    client.call('profile', {'profile': 'trader'})
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    swaps = client.call('swaps/catalog', context)
    index = next(i for i in swaps['indices'] if i['curve'] == 'base_forward' and i['definitionRevisionId'] == manifest['revisionId'])
    choice = next(p for p in index['customProjections'] if p['reference']['curveId'] == saved['id'])
    assert choice['samplings'] == [{'resetFrequency': 'monthly', 'averagingMethod': 'none'}]
    delivery = next(c for c in index['contracts'] if c['deliveryFrom'] == '2021-01-01')
    payment = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 0, 'dayOfMonth': None,
               'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None,
               'implementation': 'monthly_payment_date_rules_v1'}
    terms = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': swaps['books'][0]['id'],
        'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'], 'quantity': '10',
        'fixedCoefficient': '-1', 'discounting': False,
        'fixed': {'price': '0', 'currency': index['currencyCode'], **delivery, 'paymentSchedule': payment},
        'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'], **delivery,
         'paymentSchedule': payment, 'resetFrequency': 'monthly', 'averagingMethod': 'none', 'customProjection': choice['reference']}}
    booked = client.call('swaps/book', terms)
    assert client.call('swaps/book', terms) == booked
    detail = client.call('swaps/editor', {'context': context, 'tradeId': booked['tradeId'], 'revision': None})
    assert Fraction(detail['valuation']['presentValue']) == expected * 10
    assert detail['state']['terms']['floating']['customProjection'] == choice['reference']
    client.call('profile', {'profile': 'risk'})
    risk_context = {**context, 'convention': 'trading_workspace_v1'}
    risk_catalog = client.call('portfolio/risk/catalog', {'context': risk_context, 'bookIds': [terms['bookId']], 'portfolioIds': []})
    bucket = next(t for t in risk_catalog['scenarioTargets'] if t['factor']['coordinate'] == 'computed_output'
                  and t['factor']['target']['curve']['code'] == definition['code'] and t['kind'] == 'delivery_bucket')
    assert bucket['label'] == f"{definition['name']} · version {saved['version']} · delivery bucket", bucket['label']
    axis = next(t['factor'] for t in risk_catalog['scenarioTargets'] if t['factor']['coordinate'] == 'computed_output'
                and t['factor']['target']['curve']['code'] == definition['code'] and t['factor']['target']['kind'] == 'points'
                and len(t['factor']['target']['points']) == 1)
    request = {'context': {**risk_context, 'knowledgeMode': 'retained', 'contextId': risk_catalog['context']['contextId']},
        'bookIds': [terms['bookId']], 'portfolioIds': [], 'detail': 'holdings',
        'bumps': [{'factor': axis, 'size': '0.1', 'convention': risk_catalog['bumpConvention']}],
        'stress': [{'factor': axis, 'size': '1', 'convention': risk_catalog['stressConvention']}],
        'convention': risk_catalog['convention'], 'numericalPolicy': risk_catalog['numericalPolicy'], 'pricingPolicy': risk_catalog['pricingPolicy']}
    result = client.call('portfolio/risk', request)
    value = next(p['values'] for p in result['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert Fraction(value['base']) == (expected + 1) * 10
    assert Fraction(value['delta']) == 10 and Fraction(value['gamma']) == 0
    assert Fraction(value['upPnl']) == 1 and Fraction(value['downPnl']) == -1
    base = client.call('portfolio/risk', {**request, 'stress': []})
    base_value = next(p['values'] for p in base['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert Fraction(base_value['base']) == expected * 10
    queued = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': request})
    run_worker()
    finished = client.call('portfolio/risk/jobs/' + queued['id'])
    assert finished['state'] == 'completed', finished
    assert finished['result']['factors'] == result['factors']
    assert finished['result']['inputs']['connectedMarkets'] == result['inputs']['connectedMarkets']
    print('PASS weighted portfolio: explicit multi-source weights, signed/normalized graph parity, exact storage, backend picker, booked PV and nested output-axis worker parity', flush=True)

    from check_composed_portfolio import run_checks as check_composed
    exact_signed = Fraction(int(signed_price['numerator']), int(signed_price['denominator']))
    check_composed(client, manifest, query, run_worker, frame, published, signed, terms, expected, exact_signed)
