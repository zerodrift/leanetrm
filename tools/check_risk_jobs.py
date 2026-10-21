"""Durable risk HTTP checks against the caller's newly owned synthetic preview."""
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor


def run_checks(Client, check, run_worker):
    risk = Client(); risk.bootstrap(); risk.call('profile', {'profile': 'risk'})
    workspace = risk.call('workspace')
    frame = next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': workspace['futuresConvention'], 'knowledgeMode': 'current'}
    catalog = risk.call('futures/risk/catalog', {'context': context, 'bookIds': [], 'portfolioIds': []})
    context = {**context, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
    factor = catalog['factors'][0]
    request = {'context': context, 'bookIds': [], 'portfolioIds': [], 'detail': 'books',
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
               'bumps': [{'seriesId': factor['seriesId'], 'quote': factor['quote'], 'size': '0.1', 'convention': catalog['bumpConvention']}],
               'stress': [{'seriesId': factor['seriesId'], 'quote': factor['quote'], 'size': '0.2', 'convention': catalog['stressConvention']}]}
    expected = risk.call('futures/risk', request)
    operation = {'requestId': str(uuid4()), 'request': request}
    job = risk.call('futures/risk/jobs', operation)
    check('risk job admission captures the shared context and starts queued', job['state'] == 'queued' and job['context'] == expected['context'])
    replay = risk.call('futures/risk/jobs', operation)
    check('exact job admission retry returns the original run', replay['id'] == job['id'])
    recent = risk.call('futures/risk/jobs')
    check('recent jobs retain original identities for recovery after a browser reload',
          any(item['id'] == job['id'] and item['context'] == expected['context'] for item in recent))
    risk.call('futures/risk/jobs', {**operation, 'request': {**request, 'stress': []}}, expected=409)
    check('reused job identity cannot select different stress inputs')
    risk.call('futures/risk/jobs', {'requestId': str(uuid4()), 'request': {**request, 'context': {**context, 'knowledgeMode': 'current', 'contextId': None}}}, expected=422)
    check('queued risk rejects unresolved current knowledge instead of refreshing on retry')
    cancelled = risk.call('futures/risk/jobs', {**operation, 'requestId': str(uuid4())})
    stopped = risk.call('futures/risk/jobs/' + cancelled['id'] + '/cancel', {})
    check('queued risk can be cancelled before a worker starts', stopped['state'] == 'cancelled')
    risk.call('market/context', {'effectiveOn': '2020-12-17', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    run_worker()
    result = risk.call('futures/risk/jobs/' + job['id'])
    check('worker completes the original context after the user changes the UI dates', result['state'] == 'completed' and result['context'] == expected['context'])
    report = result['result']
    completion_won = risk.call('futures/risk/jobs/' + job['id'] + '/cancel', {})
    check('cancellation after completion returns the committed result without hiding it',
          completion_won['state'] == 'completed' and completion_won.get('result') == report)
    check('background and interactive risk reconcile exact factors and input receipts', report['factors'] == expected['factors'] and report['inputs'] == expected['inputs'] and report['scenario'] == expected['scenario'])
    check('cancelled work produces no successful result', risk.call('futures/risk/jobs/' + cancelled['id'])['state'] == 'cancelled')
    trader = Client(); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    trader.call('futures/risk/jobs/' + job['id'], expected=403)
    trader.call('futures/risk/jobs', expected=403)
    check('job identifiers do not grant another actor access to risk results')
    risk.call('market/current', {})
    parallel = [risk.call('futures/risk/jobs', {**operation, 'requestId': str(uuid4())}) for _ in range(2)]
    with ThreadPoolExecutor(max_workers=2) as workers:
        for completed in [workers.submit(run_worker) for _ in parallel]: completed.result()
    outputs = [risk.call('futures/risk/jobs/' + item['id']) for item in parallel]
    check('two independent worker processes claim and publish distinct runs with identical financial results',
          len({item['id'] for item in outputs}) == 2
          and all(item['state'] == 'completed' and item['result']['factors'] == expected['factors'] for item in outputs))
    other = risk.call('futures/risk/jobs', {**operation, 'requestId': str(uuid4())})
    admin = Client(); admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    member = next(m for m in admin.call('admin')['members'] if m['login'] == 'risk')
    changed = admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'], 'enabled': True, 'roles': ['trader']})
    risk.call('futures/risk/jobs/' + job['id'], expected=403)
    run_worker()
    admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': changed['version'], 'name': member['name'], 'enabled': True, 'roles': ['risk_analyst']})
    denied = risk.call('futures/risk/jobs/' + other['id'])
    check('revocation and restoration cannot revive an admitted job under its old authority', denied['state'] == 'failed' and denied['result'] is None)
