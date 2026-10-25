"""Durable risk HTTP checks against the caller's newly owned synthetic preview."""
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor


def run_checks(Client, check, run_worker):
    risk = Client(); risk.bootstrap(); risk.call('profile', {'profile': 'risk'})
    workspace = risk.call('workspace')
    frame = next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    catalog = risk.call('portfolio/risk/catalog', {'context': context, 'bookIds': [], 'portfolioIds': []})
    context = {**context, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
    factor = catalog['factors'][0]
    request = {'context': context, 'bookIds': [], 'portfolioIds': [], 'detail': 'books',
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'], 'pricingPolicy': catalog['pricingPolicy'],
               'bumps': [{'factor': factor['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}],
               'stress': [{'factor': factor['factor'], 'size': '0.2', 'convention': catalog['stressConvention']}]}
    expected = risk.call('portfolio/risk', request)
    operation = {'requestId': str(uuid4()), 'request': request}
    job = risk.call('portfolio/risk/jobs', operation)
    check('risk job admission captures the shared context and starts queued', job['state'] == 'queued' and job['context'] == expected['context'])
    replay = risk.call('portfolio/risk/jobs', operation)
    check('exact job admission retry returns the original run', replay['id'] == job['id'])
    recent = risk.call('portfolio/risk/jobs')
    check('recent jobs retain original identities for recovery after a browser reload',
          any(item['id'] == job['id'] and item['context'] == expected['context'] for item in recent))
    risk.call('portfolio/risk/jobs', {**operation, 'request': {**request, 'stress': []}}, expected=409)
    check('reused job identity cannot select different stress inputs')
    risk.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': {**request, 'context': {**context, 'knowledgeMode': 'current', 'contextId': None}}}, expected=422)
    check('queued risk rejects unresolved current knowledge instead of refreshing on retry')
    cancelled = risk.call('portfolio/risk/jobs', {**operation, 'requestId': str(uuid4())})
    stopped = risk.call('portfolio/risk/jobs/' + cancelled['id'] + '/cancel', {})
    check('queued risk can be cancelled before a worker starts', stopped['state'] == 'cancelled')
    risk.call('market/context', {'effectiveOn': '2020-12-17', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    run_worker()
    result = risk.call('portfolio/risk/jobs/' + job['id'])
    check('worker completes the original context after the user changes the UI dates', result['state'] == 'completed' and result['context'] == expected['context'])
    report = result['result']
    completion_won = risk.call('portfolio/risk/jobs/' + job['id'] + '/cancel', {})
    check('cancellation after completion returns the committed result without hiding it',
          completion_won['state'] == 'completed' and completion_won.get('result') == report)
    check('background and interactive risk reconcile exact factors and input receipts', report['factors'] == expected['factors'] and report['inputs'] == expected['inputs'] and report['scenario'] == expected['scenario'])
    check('cancelled work produces no successful result', risk.call('portfolio/risk/jobs/' + cancelled['id'])['state'] == 'cancelled')
    trader = Client(); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    trader.call('portfolio/risk/jobs/' + job['id'], expected=403)
    trader.call('portfolio/risk/jobs', expected=403)
    check('job identifiers do not grant another actor access to risk results')
    risk.call('market/current', {})
    parallel = [risk.call('portfolio/risk/jobs', {**operation, 'requestId': str(uuid4())}) for _ in range(2)]
    with ThreadPoolExecutor(max_workers=2) as workers:
        for completed in [workers.submit(run_worker) for _ in parallel]: completed.result()
    outputs = [risk.call('portfolio/risk/jobs/' + item['id']) for item in parallel]
    check('two independent worker processes claim and publish distinct runs with identical financial results',
          len({item['id'] for item in outputs}) == 2
          and all(item['state'] == 'completed' and item['result']['factors'] == expected['factors'] for item in outputs))
    other = risk.call('portfolio/risk/jobs', {**operation, 'requestId': str(uuid4())})
    admin = Client(); admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    member = next(m for m in admin.call('admin')['members'] if m['login'] == 'risk')
    changed = admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'], 'enabled': True, 'roles': ['trader']})
    risk.call('portfolio/risk/jobs/' + job['id'], expected=403)
    run_worker()
    admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': changed['version'], 'name': member['name'], 'enabled': True, 'roles': ['risk_analyst']})
    denied = risk.call('portfolio/risk/jobs/' + other['id'])
    check('revocation and restoration cannot revive an admitted job under its old authority', denied['state'] == 'failed' and denied['result'] is None)
    # The parts of a run made in parts (D220) each state the run, their part and the run's part count: one run at one retained context,
    # each part once. The job history lists every part with its run, and a job queued on its own states none.
    run = str(uuid4())
    parts = [{'requestId': str(uuid4()), 'request': request, 'run': {'id': run, 'part': part, 'parts': 2}} for part in (1, 2)]
    queued = [risk.call('portfolio/risk/jobs', body) for body in parts]
    check('each part of a run states the run, its part and the part count, and a job queued on its own states none',
          [item['run'] for item in queued] == [body['run'] for body in parts] and risk.call('portfolio/risk/jobs/' + job['id'])['run'] is None)
    listed = {item['id']: item['run'] for item in risk.call('portfolio/risk/jobs')}
    check('job history lists each part with its run, so that after a reload the parts are shown as one run',
          all(listed.get(item['id']) == item['run'] for item in queued))
    scopes = {item['id']: item['scope'] for item in risk.call('portfolio/risk/jobs')}
    check('every job read states the books and portfolios its run was asked for, as they were chosen, the job history included',
          all(item['scope'] == {'bookIds': [], 'portfolioIds': []} and scopes.get(item['id']) == item['scope'] for item in queued))
    check('a part sent again with its request identity returns the job it queued', risk.call('portfolio/risk/jobs', parts[0])['id'] == queued[0]['id'])
    def refused(body, code, status=422):
        return risk.call('portfolio/risk/jobs', body, expected=status).get('code') == code
    check('a part numbered beyond its run\'s part count is refused',
          refused({'requestId': str(uuid4()), 'request': request, 'run': {'id': str(uuid4()), 'part': 3, 'parts': 2}}, 'risk_job_run'))
    check('each part of a run is queued once: the same part under another request identity is refused',
          refused({'requestId': str(uuid4()), 'request': request, 'run': {'id': run, 'part': 2, 'parts': 2}}, 'risk_job_run'))
    check('every part of a run states the same part count',
          refused({'requestId': str(uuid4()), 'request': request, 'run': {'id': run, 'part': 3, 'parts': 3}}, 'risk_job_run'))
    check('a request identity keeps the run it was first sent with',
          refused({**parts[0], 'run': {'id': str(uuid4()), 'part': 1, 'parts': 2}}, 'risk_job_conflict', 409))
    scoped = str(uuid4())
    first_scoped = risk.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': request, 'run': {'id': scoped, 'part': 1, 'parts': 2}})
    check('every part of a run is asked for the same books and portfolios',
          refused({'requestId': str(uuid4()), 'request': {**request, 'bookIds': [catalog['books'][0]['bookId']]}, 'run': {'id': scoped, 'part': 2, 'parts': 2}}, 'risk_job_run'))
    later = risk.call('portfolio/risk/catalog', {'context': {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'},
                                                 'bookIds': [], 'portfolioIds': []})['context']['contextId']
    split = str(uuid4())
    first = risk.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': request, 'run': {'id': split, 'part': 1, 'parts': 2}})
    check('every part of a run is made at its one retained context',
          later != context['contextId'] and refused({'requestId': str(uuid4()), 'request': {**request, 'context': {**context, 'contextId': later}},
                                                      'run': {'id': split, 'part': 2, 'parts': 2}}, 'risk_job_run'))
    for item in [*queued, first, first_scoped]:
        risk.call('portfolio/risk/jobs/' + item['id'] + '/cancel', {})
