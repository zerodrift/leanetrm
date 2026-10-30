"""Versioned party editing against a newly owned synthetic workspace."""
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url):
    def client(profile):
        c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': profile}); return c
    editor = client('editor'); trader = client('trader'); viewer = client('viewer')
    frame = next(f for f in editor.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'kind': 'legal_entity'}
    directory = editor.call('parties/catalog', context)
    assert directory['canEdit'] and len(directory['versions']) >= 2
    # PostgreSQL counts Unicode scalars rather than UTF-16 code units. Accepted
    # party names must survive the domain materialization used by booked trades.
    unicode_name = '\U0001f50c' * 80
    unicode_parties = {}
    for kind in ('legal_entity', 'counterparty'):
        request = dict(operationId=str(uuid4()), kind=kind, id=None, expectedRevision=None,
                       code='unicode_'+kind, name=unicode_name, effectiveAt='2020-12-18T12:00:00Z')
        saved = editor.call('parties/save', request)
        assert saved['name'] == unicode_name
        listed = editor.call('parties/catalog', {**context, 'kind': kind})['versions']
        assert next(p for p in listed if p['id'] == saved['id'])['name'] == unicode_name
        editor.call('parties/save', {**request, 'operationId': str(uuid4()), 'code': 'long_'+kind,
                                    'name': unicode_name+'\U0001f50c'}, expected=422)
        unicode_parties[kind] = saved
    trading_frame = next(f for f in trader.call('workspace')['frames'] if f['at'] == frame['at'])
    trading_context = {'snapshotId': trading_frame['id'], 'convention': 'contract_lot_execution_pnl_v2', 'knowledgeMode': 'current'}
    trading = trader.call('futures/catalog', trading_context)
    contract = trading['contracts'][0]
    entity, counterparty = unicode_parties['legal_entity'], unicode_parties['counterparty']
    booked = trader.call('futures/book', {**trading_context, 'operationId': str(uuid4()), 'bookId': trading['books'][0]['id'],
        'curve': contract['curve'], 'contractId': contract['id'], 'lots': '1', 'executionPrice': '3',
        'legalEntityId': entity['id'], 'legalEntityRevision': entity['revision'],
        'counterpartyId': counterparty['id'], 'counterpartyRevision': counterparty['revision']})
    detail_request = {'context': trading_context, 'tradeId': booked['tradeId'], 'revision': None}
    detail = trader.call('futures/editor', detail_request)
    retained = trader.call('futures/editor', {**detail_request, 'context': {**trading_context, 'knowledgeMode': 'retained',
        'contextId': detail['context']['contextId']}})
    for result in (detail, retained):
        assert result['state']['booking']['legalEntity'] == unicode_name
        assert result['state']['booking']['counterparty'] == unicode_name
    print('PASS party names: 80 Unicode scalars survive save, read, booking and retained trade materialization; 81 reject', flush=True)
    for kind in ('legal_entity', 'counterparty'):
        body = dict(operationId=str(uuid4()), kind=kind, id=None, expectedRevision=None,
                    code='synthetic_'+kind, name='Synthetic editor '+kind,
                    effectiveAt='2020-12-18T12:00:00Z')
        saved = editor.call('parties/save', body)
        assert saved['revision'] == 1 and saved == editor.call('parties/save', body)
        before_edit = editor.call('parties/catalog', {**context, 'kind': kind})['context']
        update = {**body, 'operationId': str(uuid4()), 'id': saved['id'], 'expectedRevision': 1,
                  'name': 'Revised synthetic '+kind, 'effectiveAt': '2020-12-18T13:00:00Z'}
        changed = editor.call('parties/save', update)
        assert changed['id'] == saved['id'] and changed['revision'] == 2
        assert saved == editor.call('parties/save', body)  # original result after later edits
        editor.call('parties/save', {**update, 'operationId': str(uuid4())}, expected=409)
        editor.call('parties/save', {**body, 'operationId': str(uuid4())}, expected=409)
        editor.call('parties/save', {**update, 'expectedRevision': 2, 'name': 'Different retry'}, expected=409)
        versions = editor.call('parties/catalog', {**context, 'kind': kind})['versions']
        assert [v['name'] for v in versions if v['id'] == saved['id']] == [update['name'], body['name']]
        editor.call('parties/save', {**update, 'operationId': str(uuid4()), 'expectedRevision': 2,
                                   'code': 'changed_identity'}, expected=409)
        editor.call('parties/save', {**update, 'operationId': str(uuid4()), 'expectedRevision': 2,
                                   'effectiveAt': '2020-12-18T11:00:00Z'}, expected=422)
        future = editor.call('parties/save', {**body, 'operationId': str(uuid4()), 'id': None,
                                             'code': 'future_'+kind, 'effectiveAt': '2021-01-01T00:00:00Z'})
        assert not any(v['id'] == future['id'] for v in editor.call('parties/catalog', {**context, 'kind': kind})['versions'])
        trader.call('parties/save', {**body, 'operationId': str(uuid4()), 'code': 'denied_'+kind}, expected=403)
        editor.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': before_edit['knownAt'], 'mode': 'at', 'selection': 'latest_correction'})
        historical_frame = next(f for f in editor.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
        historical = editor.call('parties/catalog', {'snapshotId': historical_frame['id'], 'kind': kind})
        assert not historical['canEdit'] and [v['revision'] for v in historical['versions'] if v['id'] == saved['id']] == [1]
        editor.call('parties/save', body, expected=422)
        editor.call('market/current', {})
    viewer.call('parties/catalog', context, expected=403)
    editor.call('parties/save', {**body, 'operationId': str(uuid4()), 'kind': 'invented'}, expected=422)
    editor.call('parties/save', {**body, 'operationId': str(uuid4()), 'name': 'x\ncontrol'}, expected=422)
    # A save admitted in a separate session is still an optimistic append.
    from concurrent.futures import ThreadPoolExecutor
    other = client('editor')
    attempts = [{**update, 'expectedRevision': 2, 'operationId': str(uuid4()), 'name': name}
                for name in ('Concurrent synthetic A', 'Concurrent synthetic B')]
    def save(pair):
        c, request = pair
        try:
            return c.call('parties/save', request)
        except AssertionError as error:
            assert 'got 409:' in str(error), str(error)
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(save, zip((editor, other), attempts)))
    assert sum(result is not None for result in outcomes) == 1
    winner = next(result for result in outcomes if result is not None)
    assert winner['revision'] == 3
    admin = client('admin')
    subject = client('admin')
    user = subject.call('admin/create', {'operationId': str(uuid4()), 'login': 'party_editor', 'name': 'Synthetic party maintainer', 'kind': 'human', 'roles': ['market_data_editor']})
    subject.call('profile', {'profile': 'party_editor'})
    user = admin.call('admin/update', {'operationId': str(uuid4()), 'id': user['id'], 'expectedVersion': user['version'], 'name': user['name'], 'enabled': False, 'roles': ['market_data_editor']})
    subject.call('parties/save', {**body, 'operationId': str(uuid4()), 'code': 'revoked_party'}, expected=403)
    print('PASS party editors: immutable history, certified past reads, historical write denial, exact retries, concurrent stale conflicts, effective dates and current revocation', flush=True)
