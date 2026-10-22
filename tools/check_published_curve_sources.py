"""Published custom outputs remain connected when reused by another saved curve."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4

from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def exact(point):
    return Fraction(point['price']['numerator']) / Fraction(point['price']['denominator'])


def run_checks(url, manifest, query, run_worker):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    catalog = client.call('custom-curves')
    parent = deepcopy(next(t['definition'] for t in catalog['templates'] if t['code'] == 'ng_average'))
    parent.update(code='published_parent', name='Synthetic published parent')
    for node in parent['nodes']:
        if node['kind'] == 'select':
            node['parameters']['count'] = '2'
        if node['kind'] == 'dates':
            node['parameters'].update(count='3', includeContext='true')
    saved = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'],
        'definition': parent, 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    published = client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved['id'],
        'version': saved['version'], 'generation': saved['generation']})
    reference = {'catalogId': manifest['catalogId'], 'curveId': published['id'],
                 'version': published['version'], 'outputNode': parent['output']}
    panel = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': parent,
        'id': published['id'], 'version': published['version']})
    expected = sum(exact(point) for point in panel['points']) / len(panel['points'])
    child = {'code': 'published_child', 'name': parent['name'], 'output': 'result', 'nodes': [
        {'id': 'upstream', 'kind': 'published_source', 'parameters': {'selection': 'all_output_points_v1'},
         'inputs': {}, 'publishedSource': reference},
        {'id': 'average', 'kind': 'mean', 'parameters': {'axis': 'contracts'}, 'inputs': {'values': 'upstream'}},
        {'id': 'result', 'kind': 'output', 'parameters': {}, 'inputs': {'values': 'average'}}]}
    result = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': child})
    assert exact(result['points'][0]) == expected, result
    assert result['dependencies'], 'A published source must retain its native quote dependencies'
    print('PASS published source: exact upstream panel averaging and connected quote lineage', flush=True)
    assert reference in [source['reference'] for source in client.call('custom-curves')['publishedSources']]
    formula = client.call('custom-curves/format', {'definition': child})['formula']
    compiled = client.call('custom-curves/compile', {'formula': formula, 'code': child['code'],
        'name': child['name'], 'snapshotId': frame['id']})['definition']
    assert compiled['nodes'][0]['publishedSource'] == reference
    for invalid in ({**reference, 'curveId': str(uuid4())}, {**reference, 'version': 64}, {**reference, 'outputNode': 'missing'}):
        refused = deepcopy(child)
        refused['nodes'][0]['publishedSource'] = invalid
        client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': refused}, expected=422)

    def save(definition, **version):
        client.call('profile', {'profile': 'editor'})
        return client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'],
            'definition': definition, 'layout': {}, **version})

    def publish(saved_curve):
        client.call('profile', {'profile': 'approver'})
        return client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved_curve['id'],
            'version': saved_curve['version'], 'generation': saved_curve['generation']})

    saved_child = save(child)
    assert next(n['publishedSource'] for n in saved_child['definition']['nodes'] if n['kind'] == 'published_source') == reference
    grandchild = {'code': 'published_grandchild', 'name': 'Synthetic published grandchild', 'output': 'result',
        'nodes': [deepcopy(child['nodes'][0]), deepcopy(child['nodes'][-1])]}
    grandchild['nodes'][0]['publishedSource'] = {**reference, 'curveId': saved_child['id'], 'outputNode': 'result'}
    grandchild['nodes'][-1]['inputs']['values'] = 'upstream'
    client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': grandchild}, expected=422)
    publish(saved_child)
    same_name = [s for s in client.call('custom-curves')['publishedSources']
        if s['reference']['curveId'] in (published['id'], saved_child['id'])]
    assert len(same_name) == 2 and len({s['label'] for s in same_name}) == 2
    assert all(code in next(s['label'] for s in same_name if s['reference']['curveId'] == curve)
        for code, curve in ((parent['code'], published['id']), (child['code'], saved_child['id'])))
    saved_grandchild = publish(save(grandchild))
    preview = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': grandchild,
        'id': saved_grandchild['id'], 'version': 1})
    assert exact(preview['points'][0]) == expected
    rows = query(f"SELECT count(*) FROM market_data.custom_curve_node_published_sources WHERE curve_id IN('{saved_child['id']}','{saved_grandchild['id']}');")
    assert rows == '2'
    from check_published_curve_sources_db import run_checks as check_storage
    check_storage(query, manifest, saved_child['id'], published['id'])
    print('PASS published source storage: exact typed roundtrip, multi-level publication and rejected missing/draft/output references', flush=True)

    client.call('profile', {'profile': 'trader'})
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    swaps = client.call('swaps/catalog', context)
    index = next(i for i in swaps['indices'] if i['curve'] == 'base_forward' and i['definitionRevisionId'] == manifest['revisionId'])
    choice = next(p for p in index['customProjections'] if p['reference']['curveId'] == saved_grandchild['id'])
    delivery = next(c for c in index['contracts'] if c['deliveryFrom'] == '2021-01-01')
    payment = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 0, 'dayOfMonth': None,
        'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None,
        'implementation': 'monthly_payment_date_rules_v1'}
    terms = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': swaps['books'][0]['id'],
        'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'], 'quantity': '10',
        'fixedCoefficient': '-1', 'discounting': False,
        'fixed': {'price': '0', 'currency': index['currencyCode'], **delivery, 'paymentSchedule': payment},
        'floating': {'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'], **delivery,
            'paymentSchedule': payment, 'resetFrequency': 'monthly', 'averagingMethod': 'none', 'customProjection': choice['reference']}}
    booked = client.call('swaps/book', terms)
    detail = client.call('swaps/editor', {'context': context, 'tradeId': booked['tradeId'], 'revision': None})
    assert Fraction(detail['valuation']['presentValue']) == expected * 10
    client.call('profile', {'profile': 'risk'})
    selected = {**context, 'convention': 'trading_workspace_v1'}
    risk_catalog = client.call('portfolio/risk/catalog', {'context': selected, 'bookIds': [terms['bookId']], 'portfolioIds': []})

    def output_axis(code):
        return next(t['factor'] for t in risk_catalog['scenarioTargets'] if t['factor']['coordinate'] == 'computed_output'
            and t['factor']['target']['curve']['code'] == code and t['factor']['target']['kind'] == 'parallel')

    parent_axis, child_axis = output_axis(parent['code']), output_axis(child['code'])
    request = {'context': {**selected, 'knowledgeMode': 'retained', 'contextId': risk_catalog['context']['contextId']},
        'bookIds': [terms['bookId']], 'portfolioIds': [], 'detail': 'holdings',
        'bumps': [{'factor': parent_axis, 'size': '0.1', 'convention': risk_catalog['bumpConvention']}],
        'stress': [{'factor': parent_axis, 'size': '1', 'convention': risk_catalog['stressConvention']},
                   {'factor': child_axis, 'size': '2', 'convention': risk_catalog['stressConvention']}],
        'convention': risk_catalog['convention'], 'numericalPolicy': risk_catalog['numericalPolicy'], 'pricingPolicy': risk_catalog['pricingPolicy']}
    risk = client.call('portfolio/risk', request)
    value = next(p['values'] for p in risk['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert Fraction(value['base']) == (expected + 3) * 10
    assert Fraction(value['delta']) == 10 and Fraction(value['gamma']) == 0
    assert Fraction(value['upPnl']) == 1 and Fraction(value['downPnl']) == -1
    quote_request = {**request, 'stress': [], 'bumps': [
        {'factor': f['factor'], 'size': '0.1', 'convention': risk_catalog['bumpConvention']}
        for f in risk_catalog['factors']]}
    quote_risk = client.call('portfolio/risk', quote_request)
    exposed = [p['values'] for f in quote_risk['factors'] for p in f['positions']
        if p['tradeId'] == booked['tradeId'] and Fraction(p['values']['delta']) != 0]
    assert len(exposed) == 2 and all(Fraction(p['delta']) == Fraction(5, 3) and Fraction(p['gamma']) == 0 for p in exposed)
    queued = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': request})
    sample = next(s for market in risk['inputs']['connectedMarkets'] for s in market['samples']
        if s['freezeHistorical'] and s['sourceCurve'] == next(n['parameters']['curve'] for n in parent['nodes'] if n['kind'] == 'source'))
    original, correction = sample['observations'][0]['id'], str(uuid4())
    query(f"""INSERT INTO market_data.observations SELECT catalog_id,'{correction}',definition_revision_id,series_id,
     reference_catalog_id,reference_revision_id,source_code,source_event_id,'{correction}',role,publication_state,
     observed_at,clock_timestamp(),value+1,id FROM market_data.observations
     WHERE catalog_id='{manifest['catalogId']}' AND id='{original}';""")
    client.call('market/current', {})
    fresh = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': grandchild})
    assert exact(fresh['points'][0]) == expected + Fraction(1, 6)
    assert client.call('portfolio/risk', request)['factors'] == risk['factors']
    run_worker()
    finished = client.call('portfolio/risk/jobs/' + queued['id'])
    assert finished['state'] == 'completed', finished
    assert finished['result']['factors'] == risk['factors']
    assert finished['result']['inputs']['connectedMarkets'] == risk['inputs']['connectedMarkets']
    print('PASS published source portfolio: multi-level booked PV, ancestor-output stresses and retained worker equality after a source correction', flush=True)
    # A newer version may refer through the child to its own older version.
    # Exact-version identity makes this a DAG, and must not rebind the child.
    second = deepcopy(grandchild)
    second.update(code=parent['code'], name='Synthetic parent successor')
    successor = publish(save(second, id=published['id'], version=1,
        generation=published['generation'], expectedState='published'))
    assert successor['version'] == 2
    unchanged = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': grandchild})
    assert exact(unchanged['points'][0]) == expected + Fraction(1, 6)
    client.call('profile', {'profile': 'risk'})
    assert client.call('portfolio/risk', request)['factors'] == risk['factors']
    client.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': risk_catalog['context']['knownAt'],
        'mode': 'at', 'selection': 'latest_correction'})
    historical = client.call('custom-curves')
    assert reference in [s['reference'] for s in historical['publishedSources']]
    assert not any(s['reference']['curveId'] == published['id'] and s['reference']['version'] == 2
        for s in historical['publishedSources'])
    historical_frame = initial_frame(client.call('workspace/navigation'), '2020-12-18')
    restored = client.call('custom-curves/preview', {'snapshotId': historical_frame['id'], 'definition': grandchild})
    assert exact(restored['points'][0]) == expected
    client.call('market/current', {})
    print('PASS published source versions: older-version DAG, stable pins after successor publication and certified historical graph/prices', flush=True)
