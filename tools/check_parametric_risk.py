"""Delta-normal integration against the owned generated dated-factor history."""
from copy import deepcopy
from fractions import Fraction
from math import exp, isclose, pi, sqrt
from statistics import NormalDist
from uuid import uuid4

from preview_client import PreviewClient, initial_frame


def run_checks(url, manifest, query, run_worker):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'risk'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    holdings = c.call('trading/blotter', {'context': current, 'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})
    held = next(h for h in holdings['holdings'] if h['instrumentType'] == 'future')
    scope = {'context': {**current, 'knowledgeMode': 'retained', 'contextId': holdings['context']['contextId']},
             'bookIds': [held['bookId']], 'portfolioIds': [], 'tradeIds': [held['tradeId']]}
    catalog = c.call('portfolio/risk/catalog', scope)
    history = c.call('portfolio/risk/history', {'scope': scope, 'pricingPolicy': catalog['pricingPolicy'],
        'policy': {**catalog['history']['profile'], 'minimumSamples': 200},
        'calendar': {'revision': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId']}, 'code': 'weekdays_utc'},
        'from': '2020-01-06', 'to': '2020-12-19', 'closeTime': '16:00:00', 'ambiguousTime': 'reject', 'invalidTime': 'reject'})
    profile = catalog['parametric']
    request = {'history': history['request'], 'policy': {**profile['profile'], 'drift': profile['drifts']['zero'], 'confidence': '0.99'},
               'finiteDifference': {'convention': profile['finiteDifference'], 'tradingTicks': 1, 'deltaTolerance': {'absolute': '0', 'relative': '0'}}}
    direct = c.call('portfolio/risk/parametric', request)
    result = direct['parametric']['result']; p = result['partitions'][0]
    assert result['available'] and len(result['partitions']) == 1 and 'factors' not in direct
    deltas = direct['parametric']['finiteDifference']['estimates']
    assert len(deltas) == 1 and deltas[0]['stable']
    delta = Fraction(deltas[0]['nominal'])
    moves = [Fraction(s['changes'][0]) for s in history['manifest']['samples']]
    mean = sum(moves) / len(moves)
    variance = delta * delta * sum((move - mean) ** 2 for move in moves) / (len(moves) - 1)
    assert Fraction(p['variance']) == variance and Fraction(p['mean']) == 0
    z = NormalDist().inv_cdf(.99)
    assert isclose(float(p['valueAtRisk']), z * sqrt(variance), rel_tol=2e-10)
    assert isclose(float(p['expectedShortfall']), sqrt(variance) * exp(-z*z/2) / sqrt(2*pi) / .01, rel_tol=2e-10)
    assert len(p['linearizedLosses']) == 249 and result['calibration']['historyId'] == history['manifest']['id']
    print('PASS parametric HTTP: complete native delta, independent exact sample covariance and Gaussian VaR/ES oracle', flush=True)
    for name in ('confidence', 'rational', 'drift', 'minimum', 'scope', 'forged'):
        bad = deepcopy(request)
        if name == 'confidence': bad['policy']['confidence'] = '1'
        elif name == 'rational': bad['policy']['confidence'] = '99/100'
        elif name == 'drift': bad['policy']['drift'] = 'automatic'
        elif name == 'minimum': bad['finiteDifference']['tradingTicks'] = 0
        elif name == 'scope': bad['history']['expectedManifestId'] = '0' * 64
        else: bad['resolvedCloses'] = []
        c.call('portfolio/risk/parametric', bad, expected=400 if name == 'forged' else 422)
    print('PASS parametric admission: explicit confidence/drift/FD policies, captured identity and server-owned history evidence', flush=True)
    insufficient = deepcopy(history['request']); insufficient.pop('expectedManifestId')
    insufficient['policy']['minimumSamples'] = 1000
    unavailable_history = c.call('portfolio/risk/history', insufficient)
    unavailable = c.call('portfolio/risk/parametric', {**request, 'history': unavailable_history['request']})['parametric']
    assert not unavailable['result']['available'] and unavailable['result']['partitions'] == []
    assert not unavailable['finiteDifference']['evaluated'] and unavailable['finiteDifference']['estimates'] == []
    compact = exact_amounts(c.call('portfolio/risk/parametric/summary', {**request, 'history': unavailable_history['request']}))
    assert not compact['available'] and compact['partitions'] == [] and compact['unavailableReasons'] == unavailable['result']['unavailableReasons']
    assert not compact['coverage']['available'] and compact['coverage']['minimumSamples'] == 1000
    print('PASS parametric unavailable coverage: retained calibration diagnostics contain no fabricated sensitivities or risk estimates', flush=True)
    mixed_source=deepcopy(history['request']);mixed_source.pop('expectedManifestId');mixed_source['scope']={**scope,'bookIds':[],'tradeIds':None}
    mixed_source['from']='2020-12-15';mixed_source['policy']['minimumSamples']=2
    mixed_history=c.call('portfolio/risk/history',mixed_source)
    mixed_request={**request,'history':mixed_history['request']}
    mixed=c.call('portfolio/risk/parametric',mixed_request)['parametric']
    assert mixed['result']['available'] and len(mixed['result']['partitions'])>1 and len(mixed['factors'])>1
    samples=[[Fraction(v) for v in s['changes']] for s in mixed_history['manifest']['samples']];n=len(samples);d=len(samples[0])
    means=[sum(s[j] for s in samples)/n for j in range(d)]
    covariance=[[sum((s[i]-means[i])*(s[j]-means[j]) for s in samples)/(n-1) for j in range(d)] for i in range(d)]
    for partition in mixed['result']['partitions']:
        estimates=[e for e in mixed['finiteDifference']['estimates'] if e['measure']==partition['measure'] and e['currency']==partition['currency']]
        delta=[sum(Fraction(e['nominal']) for e in estimates if e['columnId']==column['id']) for column in mixed_history['manifest']['columns']]
        assert Fraction(partition['variance'])==sum(delta[i]*covariance[i][j]*delta[j] for i in range(d) for j in range(d))
    check_compact_reads(c, {**scope, 'bookIds': [], 'tradeIds': None}, mixed_request, mixed, mixed_history)
    mixed_job=c.call('portfolio/risk/parametric/jobs',{'requestId':str(uuid4()),'request':mixed_request});run_worker()
    assert c.call('portfolio/risk/jobs/'+mixed_job['id'])['result']['parametric']['result']==mixed['result']
    print('PASS parametric mixed scope: complete shared factor union, independent multi-factor native variance oracles and exact worker partition replay',flush=True)
    queued_request = {'requestId': str(uuid4()), 'request': request}
    job = c.call('portfolio/risk/parametric/jobs', queued_request)
    assert c.call('portfolio/risk/parametric/jobs', queued_request)['id'] == job['id']
    run_worker()
    completed = c.call('portfolio/risk/jobs/' + job['id'])
    assert completed['state'] == 'completed', completed
    retained = completed['result']['parametric']
    assert {key for key in direct['parametric'] if retained.get(key) != direct['parametric'][key]} <= {'passes'}, 'Financial inputs/results must replay exactly; preparation cache work can differ'
    for report in (direct, completed['result']):
        assert len(report['parametric']['passes']) == 3
        assert all(p['scenarioCount'] == 3 for p in report['parametric']['passes'])
        assert report['work']['scenarioCount'] == 9, 'Whole operation work must count all three FD passes'
        assert report['work']['unitPricerCalls'] == sum(p['unitPricerCalls'] for p in report['parametric']['passes'])
    print('PASS parametric retention: idempotent complete job and exact captured history/delta/calibration/report replay', flush=True)
    archive = c.call('portfolio/risk/parametric/archive?limit=1')
    assert [row['id'] for row in archive['items']] == [job['id']]
    assert archive['items'][0]['kind'] == 'parametric'
    second=deepcopy(request);second['policy']['confidence']='0.975'
    second_job=c.call('portfolio/risk/parametric/jobs',{'requestId':str(uuid4()),'request':second});run_worker()
    assert c.call('portfolio/risk/jobs/'+second_job['id'])['state']=='completed'
    page=c.call('portfolio/risk/parametric/archive?limit=1');assert page['next'] and page['items'][0]['id']==second_job['id']
    from urllib.parse import urlencode
    tail=c.call('portfolio/risk/parametric/archive?'+urlencode({'limit':1,'before':page['next']['createdAt'],'id':page['next']['id']}))
    assert [r['id'] for r in tail['items']]==[job['id']] and tail['next'] is not None
    final_page=c.call('portfolio/risk/parametric/archive?'+urlencode({'limit':1,'before':tail['next']['createdAt'],'id':tail['next']['id']}))
    assert [r['id'] for r in final_page['items']]==[mixed_job['id']] and final_page['next'] is None
    c.call('portfolio/risk/parametric/archive?limit=0',expected=422)
    print('PASS parametric archive: authorized completed reports are discoverable beyond the bounded recent directory', flush=True)
    from check_day_end_risk import run_checks as check_day_end
    check_day_end(c,query,scope,catalog,history,request,run_worker)
    from check_parametric_risk_db import run_checks as check_db
    check_db(query, manifest, job['id'],run_worker)
    c.call('profile', {'profile': 'viewer'})
    c.call('portfolio/risk/jobs/' + job['id'], expected=403)
    c.call('portfolio/risk/parametric/archive?limit=1', expected=403)
    c.call('portfolio/risk/parametric', request, expected=403)
    c.call('portfolio/risk/statistical/setup', scope, expected=403)
    c.call('portfolio/risk/parametric/summary', request, expected=403)
    c.call('profile', {'profile': 'risk'})
    print('PASS parametric authority: retained identity never grants a viewer calculation or report access', flush=True)


ASSISTANT_RESULT_BYTES = 65536


def exact_amounts(read):
    """An assistant read with each shown amount ({value, display} at money display precision) replaced by its exact value."""
    import re
    shown = []
    def walk(node):
        if isinstance(node, list): return [walk(v) for v in node]
        if not isinstance(node, dict): return node
        if set(node) == {'value', 'display'}:
            assert re.fullmatch(r'-?[0-9]{1,3}(,[0-9]{3})*\.[0-9]{3}', node['display']), node
            shown.append(node['value']); return node['value']
        return {k: walk(v) for k, v in node.items()}
    plain = walk(read)
    assert shown or not read.get('partitions'), 'A read with results shows its amounts'
    return plain


def check_compact_reads(c, scope, request, report, history):
    """The assistant's statistical setup and parametric summary are bounded projections of the same catalog and calculation the Risk page uses."""
    import json
    def size(value): return len(json.dumps(value, separators=(',', ':')).encode())
    catalog = c.call('portfolio/risk/catalog', scope)
    setup = c.call('portfolio/risk/statistical/setup', scope)
    assert size(setup) <= ASSISTANT_RESULT_BYTES, size(setup)
    assert (setup['history'], setup['parametric'], setup['historical'], setup['pricingPolicy']) == (catalog['history'], catalog['parametric'], catalog['historical'], catalog['pricingPolicy'])
    assert {b['id']: b['name'] for b in setup['books']} == {b['id']: b['name'] for b in report['books']}
    assert sum(b['valuedTrades'] + b['excludedTrades'] for b in setup['books']) == c.call('portfolio/risk/scope', {**scope, 'offset': 0, 'limit': 1})['total']
    uncaptured = deepcopy(request); uncaptured['history'].pop('expectedManifestId')
    for body in (request, uncaptured):
        summary = c.call('portfolio/risk/parametric/summary', body)
        assert size(summary) <= ASSISTANT_RESULT_BYTES, size(summary)
        summary = exact_amounts(summary)
        full = report['result']
        assert summary['available'] and summary['historyId'] == history['manifest']['id'] == full['calibration']['historyId']
        assert summary['policy'] == request['policy']
        assert summary['history'] == {k: request['history'][k] for k in ('calendar', 'from', 'to', 'policy')}
        coverage = history['coverage']
        assert summary['coverage'] == {k: coverage[k] for k in ('available', 'factors', 'sessions', 'samples', 'rejected', 'minimumSamples')}
        assert [(f['id'], f['label'], f['unit']) for f in summary['factors']] == [(f['id'], f['factor']['label'], f['factor']['unit']) for f in report['factors']]
        day_end = request['history']['policy']['alignment'] == 'daily_latest_visible_session_end_v1'
        assert all(sum(f['sessionPriceTypes'].values()) == coverage['sessions'] if day_end else f['sessionPriceTypes'] is None for f in summary['factors'])
        assert summary['books'] == report['books']
        position = {f['id']: i for i, f in enumerate(report['factors'])}
        assert len(summary['partitions']) == len(full['partitions'])
        for compact, exact in zip(summary['partitions'], full['partitions']):
            assert (compact['measure'], compact['currency']) == (exact['measure'], next(p['currency'] for p in report['partitions'] if p['measure'] == exact['measure']))
            assert {k: compact[k] for k in ('mean', 'standardDeviation', 'valueAtRisk', 'expectedShortfall')} == {k: exact[k] for k in ('mean', 'standardDeviation', 'valueAtRisk', 'expectedShortfall')}
            assert compact['books'] == [{'bookId': b['id'], 'valueAtRisk': b['valueAtRisk'], 'expectedShortfall': b['expectedShortfall']} for b in exact['books']]
            ranked = sorted(exact['factors'], key=lambda f: -abs(Fraction(f['valueAtRisk'])))
            assert compact['factors'] == [{'factor': position[f['id']], 'valueAtRisk': f['valueAtRisk'], 'expectedShortfall': f['expectedShortfall']} for f in ranked]
    current = deepcopy(uncaptured); current['history']['scope']['context']['knowledgeMode'] = 'current'; current['history']['scope']['context'].pop('contextId')
    c.call('portfolio/risk/parametric/summary', current, expected=422)
    print('PASS parametric assistant reads: bounded setup and summary repeat the catalog and the exact calculated report', flush=True)
