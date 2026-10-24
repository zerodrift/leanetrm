"""Connected curve scenarios in an owned synthetic preview."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame


def run_checks(url, run_worker=None):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'risk'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    report = client.call('trading/workspace', {'context': current, 'view': view})
    context = {**current, 'knowledgeMode': 'retained', 'contextId': report['blotter']['context']['contextId']}
    scope = {'context': context, 'bookIds': [], 'portfolioIds': []}
    catalog = client.call('portfolio/risk/catalog', scope)
    assert catalog.get('scenarioTargets'), 'Risk must expose exact connected curve scenario targets'
    targets = catalog['scenarioTargets']
    output = next(t for t in targets if t['coordinate'] == 'computed_output' and t['kind'] == 'contract')
    quote = next(t for t in targets if t['coordinate'] == 'market_quote' and t['kind'] == 'contract')
    request = {**scope, 'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
               'pricingPolicy': catalog['pricingPolicy'], 'detail': 'holdings', 'stress': [],
               'bumps': [{'factor': output['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}]}
    base = client.call('portfolio/risk', request)
    assert base['connectedScenarios'] and all(s['manifestId'] for s in base['connectedScenarios'])
    assert base['factors'][0]['factor']['coordinate'] == 'computed_output'
    assert any(Fraction(t['values']['delta']) != 0 for t in base['factors'][0]['totals'])
    for total in base['factors'][0]['totals']:
        assert Fraction(total['values']['gamma']) == 0, 'Synthetic linear holdings must retain zero gamma'
    parallel = next(t for t in targets if t['coordinate'] == 'computed_output' and t['kind'] == 'parallel'
                    and t['factor']['target']['curve'] == output['factor']['target']['curve'])
    bucket = deepcopy(next(t for t in targets if t['coordinate'] == 'computed_output' and t['kind'] == 'delivery_bucket'
                           and t['factor']['target']['curve'] == output['factor']['target']['curve']))
    # The owned fixture's delivery contracts are wholly inside this explicit
    # interval. A bucket covering them all has the same PV vector as parallel.
    bucket['factor']['target'].update({'from': '2020-01-01', 'to': '2030-01-01'})
    broad = client.call('portfolio/risk', {**request, 'bumps': [
        {'factor': target['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}
        for target in (parallel, bucket)]})
    assert broad['factors'][0]['totals'] == broad['factors'][1]['totals']
    assert broad['factors'][0]['positions'] == broad['factors'][1]['positions']
    assert any(Fraction(t['values']['delta']) != 0 for t in broad['factors'][0]['totals'])
    parent = {**request, 'stress': [{'factor': quote['factor'], 'size': '0.2', 'convention': catalog['stressConvention']}]}
    child = {**parent, 'stress': [*parent['stress'], {'factor': output['factor'], 'size': '0.3', 'convention': catalog['stressConvention']}]}
    parent_result = client.call('portfolio/risk', parent)
    child_result = client.call('portfolio/risk', child)
    assert parent_result['scenario']['depth'] == 1 and child_result['scenario']['depth'] == 2
    assert client.call('portfolio/risk', parent)['factors'] == parent_result['factors']
    assert client.call('portfolio/risk', request)['factors'] == base['factors']
    wrong = deepcopy(request)
    wrong['bumps'][0]['factor']['coordinate'] = 'market_quote'
    assert client.call('portfolio/risk', wrong, expected=422)['code'] == 'scenario_coordinate'
    if run_worker:
        queued = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': child})
        run_worker()
        completed = client.call('portfolio/risk/jobs/' + queued['id'])
        assert completed['state'] == 'completed', completed.get('error')
        assert completed['result']['factors'] == child_result['factors']
    print('PASS connected curve scenarios: contract/parallel/bucket output targets, nested isolation and retained worker parity', flush=True)
