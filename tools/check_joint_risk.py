"""Joint repricing through the owned API and retained worker, with an independent legacy PV oracle."""
from decimal import Decimal
from fractions import Fraction
from uuid import uuid4


def run_checks(client, request, whole, run_worker, check):
    bump = request['bumps'][0]
    up = {'factor': bump['factor'], 'size': bump['size'], 'convention': 'absolute_curve_coordinate_parent_additive_v1'}
    down = {**up, 'size': format(-Decimal(up['size']), 'f')}
    contract = {'convention': 'portfolio_joint_risk_v1', 'scenarios': {
        'id': 'synthetic_named_stresses', 'version': '1', 'convention': 'independent_joint_absolute_curve_scenarios_v1',
        'scenarios': [{'id': 'up', 'label': 'Upward stress', 'shifts': [up]}, {'id': 'down', 'label': 'Downward stress', 'shifts': [down]}]}, 'derivatives': None}
    selected = {**request, 'bumps': [], 'joint': contract}
    report = client.call('portfolio/risk', selected)
    assert report['joint']['kind'] == 'scenarios' and 'factors' not in report
    expected = {(p['tradeId'], p['bindingId']): p for p in whole['factors'][0]['positions']}
    for sample, direction in zip(report['joint']['samples'], ('up', 'down')):
        assert sample['id'] == direction and len(sample['positions']) == len(expected)
        for position in sample['positions']:
            oracle = expected[position['tradeId'], position['bindingId']]['values']
            assert Fraction(position['values']['value']) == Fraction(oracle[direction])
            assert Fraction(position['values']['loss']) == -Fraction(oracle[direction + 'Pnl'])
    check('named joint scenarios retain exact binding PV and signed loss under sibling shocks')
    job = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': selected})
    run_worker()
    done = client.call('portfolio/risk/jobs/' + job['id'])
    assert done['state'] == 'completed', done.get('error')
    assert done['result']['joint']['samples'] == report['joint']['samples']
    assert done['result']['joint']['baseTotals'] == report['joint']['baseTotals']
    check('retained joint jobs replay the same named samples and exact financial values')
    rejected = client.call('portfolio/risk', {**selected, 'joint': {**contract, 'scenarios': {**contract['scenarios'], 'scenarios': []}}}, expected=422)
    assert rejected['code'] == 'risk_joint_contract'
    check('an empty joint scenario set refuses rather than becoming legacy bumps')
    catalog = client.call('portfolio/risk/catalog', {k: request[k] for k in ('context', 'bookIds', 'portfolioIds')})
    first = next(f for f in catalog['factors'] if f['factor'] == bump['factor'])
    second = next(f for f in catalog['factors'] if f['seriesId'] != first['seriesId'])
    other = {'factor': second['factor'], 'size': bump['size'], 'convention': bump['convention']}
    parent = {**request, 'stress': [up], 'bumps': [bump, other]}
    oracle = client.call('portfolio/risk', parent)
    both = {**contract, 'scenarios': {**contract['scenarios'], 'scenarios': [
        {'id': 'both', 'label': 'Two simultaneous moves', 'shifts': [up, {**other, 'convention': up['convention']}]},
        {'id': 'single_down', 'label': 'Independent sibling below parent', 'shifts': [down]}]}}
    together = client.call('portfolio/risk', {**parent, 'bumps': [], 'joint': both})
    first_rows = {(p['tradeId'], p['bindingId']): p['values'] for p in oracle['factors'][0]['positions']}
    second_rows = {(p['tradeId'], p['bindingId']): p['values'] for p in oracle['factors'][1]['positions']}
    for p in together['joint']['samples'][0]['positions']:
        key = p['tradeId'], p['bindingId']
        assert Fraction(p['values']['value']) == Fraction(first_rows[key]['base']) + Fraction(first_rows[key]['upPnl']) + Fraction(second_rows[key]['upPnl'])
    for p in together['joint']['samples'][1]['positions']:
        assert Fraction(p['values']['value']) == Fraction(first_rows[p['tradeId'], p['bindingId']]['down'])
    assert together['scenario']['depth'] == 1
    assert all(s['scenario']['parentId'] == p['baseScenario']['id'] for p in together['joint']['partitions'] for s in p['samples'] if not s['unchanged'])
    check('multi-factor joint loss uses one nonzero parent and independent sibling moves with an independent linear PV oracle')
    zero = {'absolute': '0', 'relative': '0'}
    derivative_request = {'convention': 'portfolio_joint_risk_v1', 'scenarios': None, 'derivatives': {
        'convention': 'exact_central_half_nominal_double_v1', 'axes': [
            {'id': 'first', 'bump': bump, 'deltaTolerance': zero, 'gammaTolerance': zero},
            {'id': 'second', 'bump': other, 'deltaTolerance': zero, 'gammaTolerance': zero}],
        'mixed': [{'id': 'pair', 'firstAxis': 'first', 'secondAxis': 'second', 'tolerance': zero}]}}
    requested = {**parent, 'bumps': [], 'joint': derivative_request}
    derivatives = client.call('portfolio/risk', requested)
    for d in derivatives['joint']['derivatives']:
        for p in d['positions']:
            key = p['tradeId'], p['bindingId']
            expected = Fraction(0) if d['formula'] == 'central_mixed_second_difference_v1' else Fraction(
                (first_rows if d['id'] == 'first' else second_rows)[key]['delta' if d['formula'] == 'central_first_difference_v1' else 'gamma'])
            assert all(Fraction(p['values'][step]) == expected for step in ('half', 'nominal', 'double')) and p['values']['stable']
    queued = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': requested})
    assert queued['kind'] == 'derivatives' and queued['derivativeCount'] == 5
    run_worker()
    done = client.call('portfolio/risk/jobs/' + queued['id'])
    assert done['state'] == 'completed', done.get('error')
    assert done['result']['joint']['derivatives'] == derivatives['joint']['derivatives']
    check('selected delta, gamma and mixed gamma retain all three exact steps, native grouping and derivative worker parity')


def run_partition_checks(client, request, primary_revision, run_worker, check):
    scope = {**request, 'bookIds': []}
    catalog = client.call('portfolio/risk/catalog', {k: scope[k] for k in ('context', 'bookIds', 'portfolioIds')})
    primary = next(f for f in catalog['factors'] if f['coordinate'] == 'market_quote' and f['factor']['target']['curve']['revision']['revisionId'] == primary_revision)
    first = {'factor': primary['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}
    second = request['bumps'][0]
    shift = lambda b: {**b, 'convention': catalog['stressConvention']}
    parent = {**scope, 'bumps': [first, second], 'stress': [shift(first)]}
    oracle = client.call('portfolio/risk', parent)
    rows = [{(p['tradeId'], p['bindingId']): p['values'] for p in f['positions']} for f in oracle['factors']]
    names = ['primary', 'supplemental', 'both']
    joint = {'convention': catalog['joint']['convention'], 'scenarios': {
        'id': 'synthetic_multi_market', 'version': '1', 'convention': catalog['joint']['scenarioConvention'], 'scenarios': [
            {'id': names[0], 'label': 'Primary market', 'shifts': [shift(first)]},
            {'id': names[1], 'label': 'Supplemental market', 'shifts': [shift(second)]},
            {'id': names[2], 'label': 'Both markets', 'shifts': [shift(first), shift(second)]}]}, 'derivatives': None}
    selected = {**parent, 'bumps': [], 'joint': joint}
    report = client.call('portfolio/risk', selected)
    assert len(report['joint']['partitions']) >= 2
    for partition in report['joint']['partitions']:
        assert [s['id'] for s in partition['samples']] == names
        for sample in partition['samples']:
            if sample['unchanged']:
                assert sample['scenario']['id'] == partition['baseScenario']['id'] and sample['graphWork']['evaluatedNodes'] == 0
    assert any(s['unchanged'] for p in report['joint']['partitions'] for s in p['samples'])
    for index, sample in enumerate(report['joint']['samples']):
        for p in sample['positions']:
            key = p['tradeId'], p['bindingId']
            expected = Fraction(rows[0][key]['base']) + sum(Fraction(rows[n][key]['upPnl']) for n in ([index] if index < 2 else [0, 1]))
            assert Fraction(p['values']['value']) == expected and Fraction(p['values']['loss']) == Fraction(rows[0][key]['base']) - expected
    expected_groups = {(r['measure'], r['bookId'], str(r['values']['currencyReference'])) for r in oracle['factors'][0]['totals']}
    assert {(r['measure'], r['bookId'], str(r['currencyReference'])) for r in report['joint']['baseTotals']} == expected_groups
    check('multi-market joint samples keep common IDs, explicit unaffected partitions and exact native book/measure/currency totals')
    zero = {'absolute': '0', 'relative': '0'}
    fd = {'convention': catalog['joint']['convention'], 'scenarios': None, 'derivatives': {'convention': catalog['joint']['derivativeConvention'],
        'axes': [{'id': name, 'bump': bump, 'deltaTolerance': zero, 'gammaTolerance': zero} for name, bump in [('a', first), ('b', second)]],
        'mixed': [{'id': 'across_markets', 'firstAxis': 'a', 'secondAxis': 'b', 'tolerance': zero}]}}
    derivative_request = {**parent, 'bumps': [], 'joint': fd}
    derivatives = client.call('portfolio/risk', derivative_request)
    for d in derivatives['joint']['derivatives']:
        for p in d['positions']:
            expected = Fraction(0) if d['formula'] == 'central_mixed_second_difference_v1' else Fraction(rows[0 if d['id'] == 'a' else 1][p['tradeId'], p['bindingId']]['delta' if d['formula'] == 'central_first_difference_v1' else 'gamma'])
            assert all(Fraction(p['values'][step]) == expected for step in ('half', 'nominal', 'double')) and p['values']['stable']
    for body, immediate in [(selected, report), (derivative_request, derivatives)]:
        operation = {'requestId': str(uuid4()), 'request': body}
        job = client.call('portfolio/risk/jobs', operation)
        assert client.call('portfolio/risk/jobs', operation)['id'] == job['id']
        run_worker()
        done = client.call('portfolio/risk/jobs/' + job['id'])
        assert done['state'] == 'completed', done.get('error')
        for key in ('samples', 'derivatives', 'baseTotals'):
            assert done['result']['joint'][key] == immediate['joint'][key]
    check('cross-market mixed gamma is exactly zero while local Greeks and joint requests replay without combining native measures')
