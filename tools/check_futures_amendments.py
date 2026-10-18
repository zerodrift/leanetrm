"""Amendment, retained-state and concurrency checks in the disposable preview."""
import json
from preview_fixture import trade_booking
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
import subprocess
import time


def run_checks(url, query, manifest, psql, environment):
    from preview_client import PreviewClient
    Client = lambda: PreviewClient(url)
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS amendment: '+name, flush=True)
    c = Client(); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frames = c.call('workspace')['frames']
    frame = next(f for f in frames if f['at'].startswith('2020-12-18'))
    earlier = next(f for f in frames if f['at'].startswith('2020-12-17'))
    context = {'snapshotId': frame['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'}
    catalog = c.call('futures/catalog', context)
    contract = next(k for k in catalog['contracts'] if k['deliveryFrom'] == '2021-01-01')
    book_a, book_b = [b['id'] for b in catalog['books']]
    booking = {'operationId': str(uuid4()), **context, **trade_booking(), 'bookId': book_a, 'curve': contract['curve'], 'contractId': contract['id'],
               'lots': '1', 'executionPrice': '2.9', 'snapshotId':earlier['id']}
    trade = c.call('futures/book', booking)
    other = c.call('futures/book', {**booking, 'operationId': str(uuid4())})
    details = lambda revision=None, selected=context: c.call('futures/editor', {'context': selected, 'tradeId': trade['tradeId'], 'revision': revision})
    initial = details()
    check('row editor exposes the current revision and retained booking event', initial['state']['revision'] == 1 and initial['canAmend'] and len(initial['history']) == 1)
    check('trade and instrument references are readable and stable', initial['state']['tradeReference'].startswith('TR-') and initial['state']['instrumentReference'].startswith('IN-'))
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': initial['context']['contextId']}
    change = {'operationId': str(uuid4()), **booking, 'snapshotId':frame['id'], 'tradeId': trade['tradeId'], 'expectedRevision': 1,
              'effectiveAt': frame['at'], 'reason': 'Synthetic booking correction', 'amendmentConvention': 'unprocessed_futures_booking_amendment_v1',
              'bookId': book_b, 'lots': '2', 'executionPrice': '2.95'}
    change['operationId'] = str(uuid4())
    preview = c.call('futures/amend-preview', change)
    check('amendment preview reprices before and after without changing the trade', preview['before']['executionPnl'] == '4' and preview['after']['executionPnl'] == '4' and details()['state']['revision'] == 1)
    token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                  f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    raw = query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); "
                f"SELECT security.amend_futures_with_parties('{manifest['catalogId']}','{uuid4()}','{trade['tradeId']}',1,'{book_b}','{manifest['revisionId']}',"
                f"'{contract['curve']}','{contract['id']}','{frame['at']}','{frame['at']}',NULL,2,2.95,'Synthetic correction',"
                f"'fixed_total_cash_execution_pnl_v1','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb); ROLLBACK;")
    check('raw amendment creates the same successor inside an isolated rollback', json.loads(raw)['revision'] == 2 and details()['state']['revision'] == 1)
    amended = c.call('futures/amend', change)
    check('amendment atomically creates revision two and a linked retained event', amended['revision'] == 2 and details()['state']['lots'] == '2' and len(details()['history']) == 2)
    check('amendment retains its trade reference and shared instrument reference', details()['state']['tradeReference'] == initial['state']['tradeReference'] and details()['state']['instrumentReference'] == initial['state']['instrumentReference'])
    check('unchanged contract reuses its instrument and does not modify sibling trades', details()['state']['instrumentId'] == initial['state']['instrumentId'] and next(h for h in c.call('futures/holdings', context)['holdings'] if h['tradeId'] == other['tradeId'])['lots'] == '1')
    check('prior knowledge and earlier effective views retain revision one', details(1, retained)['state']['lots'] == '1' and details(None, {**context, 'snapshotId': earlier['id']})['state']['revision'] == 1)
    old = details(1)
    check('superseded trade revisions are read only', old['state']['revision'] == 1 and not old['canAmend'])
    rows = c.call('futures/blotter', {'context': context, 'view': {'mode': 'trades', 'bookIds': [book_a], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})['holdings']
    check('moving a trade to another book cannot revive its original book state', not any(h['tradeId'] == trade['tradeId'] for h in rows))
    stale = c.call('futures/amend', {**change, 'operationId': str(uuid4()), 'lots': '3'}, expected=409)
    check('stale revision cannot fork trade history', stale['code'] == 'futures_revision_conflict')
    # Two sessions deliberately retain the same expected head.
    clients = []
    for _ in range(2):
        client = Client(); client.bootstrap(); client.call('profile', {'profile': 'trader'})
        f = next(f for f in client.call('workspace')['frames'] if f['at'] == frame['at'])
        clients.append((client, {**change, 'snapshotId': f['id'], 'operationId': str(uuid4()), 'expectedRevision': 2, 'lots': '3'}))
    def submit(pair):
        client, payload = pair
        try: return client.call('futures/amend', payload), payload
        except AssertionError as error:
            if not str(error).startswith('futures/amend: expected 200, got 409:') or 'futures_revision_conflict' not in str(error): raise
            return None, payload
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(submit, clients))
    check('concurrent amendments accept exactly one successor', sum(result is not None for result, _ in outcomes) == 1 and details()['state']['revision'] == 3)
    c.call('futures/amend', {**change, 'lots': '9'}, expected=409)
    check('same amendment identity cannot be reused for changed economics')
    check('retry returns its original committed revision after later amendments', c.call('futures/amend', change) == amended)
    c.call('futures/book', {**booking, 'operationId': change['operationId']}, expected=409)
    c.call('futures/amend', {**change, 'operationId': booking['operationId']}, expected=409)
    check('booking and amendment commands share one operation namespace')
    for patch in [{'reason': ''}, {'reason': '\t'}, {'lots': '0.5'}, {'executionPrice': '3.001'}, {'executedAt': '2020-12-19T16:00:00Z'}, {'effectiveAt': earlier['at']}]:
        c.call('futures/amend', {**change, 'operationId': str(uuid4()), 'expectedRevision': 3, **patch}, expected=422)
        check('invalid amendment has no successful lifecycle event')
    check('rejected amendments leave the event history unchanged', details()['state']['revision'] == 3 and len(details()['history']) == 3)
    # Commit boundary must reject an expired old state even when its replacement
    # remains live. This invokes storage directly, without the managed preview.
    february = next(k for k in catalog['contracts'] if k['deliveryFrom'] == '2021-02-01')
    token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                  f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); DO $$ BEGIN "
          f"PERFORM security.amend_futures_with_parties('{manifest['catalogId']}','{uuid4()}','{trade['tradeId']}',3,'{book_b}','{manifest['revisionId']}',"
          f"'{february['curve']}','{february['id']}','{contract['lastTrading']}','{contract['lastTrading']}',NULL,4,2.95,'Synthetic expired correction',"
          f"'fixed_total_cash_execution_pnl_v1','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb); RAISE EXCEPTION 'Expected expired-head rejection'; "
          "EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'futures_execution' THEN RAISE; END IF; END $$; ROLLBACK;")
    check('raw commit cannot use amendment to escape an expired prior contract', details()['state']['revision'] == 3)
    # Let the short-lived capability expire while a separate connection owns the
    # publication gate. No event or publication may survive this rejected write.
    children = []
    try:
        locker = subprocess.Popen([str(p) for p in psql], env={**environment, 'PGAPPNAME': 'amendment_expiry_lock'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(locker)
        locker.stdin.write("BEGIN; SELECT pg_advisory_xact_lock(730849102);\n\\echo READY\n"); locker.stdin.flush()
        while locker.stdout.readline().strip() != 'READY':
            if locker.poll() is not None: raise AssertionError(locker.stderr.read())
        before_publications = query('SELECT count(*) FROM trading.publications;')
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',2);")
        expired_operation = str(uuid4())
        sql = (f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); "
               f"SELECT security.amend_futures_with_parties('{manifest['catalogId']}','{expired_operation}','{trade['tradeId']}',3,'{book_b}','{manifest['revisionId']}',"
               f"'{contract['curve']}','{contract['id']}','{frame['at']}','{frame['at']}',NULL,4,2.95,'Synthetic expired capability',"
               f"'fixed_total_cash_execution_pnl_v1','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb); COMMIT;")
        blocked = subprocess.Popen([str(p) for p in psql]+['-v','VERBOSITY=verbose'], env={**environment, 'PGAPPNAME': 'amendment_expiry_waiter'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(blocked); blocked.stdin.write(sql); blocked.stdin.close(); blocked.stdin = None
        for _ in range(100):
            if query("SELECT count(*) FROM pg_stat_activity WHERE application_name='amendment_expiry_waiter' AND wait_event_type='Lock';") == '1': break
            time.sleep(.05)
        else: raise AssertionError('Amendment did not wait on the publication gate')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>expires_at FROM security.request_contexts WHERE id='{token}';") == 't': break
            time.sleep(.05)
        else: raise AssertionError('Amendment capability did not expire')
        locker.stdin.write('COMMIT;\n'); locker.stdin.flush(); _, error = locker.communicate(timeout=10)
        assert locker.returncode == 0, error
        _, error = blocked.communicate(timeout=10)
        check('amendment rechecks expiring authority after the publication gate', blocked.returncode != 0 and '42501' in error)
        check('expired blocked amendment leaves no event or publication', query(f"SELECT count(*) FROM trading.trade_revisions WHERE operation_id='{expired_operation}';") == '0' and query('SELECT count(*) FROM trading.publications;') == before_publications)
    finally:
        for child in children:
            if child.poll() is None: child.kill(); child.communicate(timeout=5)
    check('accepted event schema and reconciliation control state are explicit', amended['eventSchema'] == 'futures_booking_state_v2' and amended['controlState'] == 'current')
    analyst = Client(); analyst.bootstrap(); analyst.call('profile', {'profile': 'risk'})
    analyst_frame = next(f for f in analyst.call('workspace')['frames'] if f['at'] == frame['at'])
    analyst_context = {**context, 'snapshotId': analyst_frame['id']}
    inspect = analyst.call('futures/editor', {'context': analyst_context, 'tradeId': trade['tradeId'], 'revision': None})
    analyst.call('futures/amend-preview', {**change, 'snapshotId': analyst_frame['id'], 'operationId': str(uuid4()), 'expectedRevision': 3}, expected=403)
    analyst.call('futures/amend', {**change, 'snapshotId': analyst_frame['id'], 'operationId': str(uuid4()), 'expectedRevision': 3}, expected=403)
    check('read-only trade authority can inspect but cannot preview or commit amendments', not inspect['canAmend'])
    c.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': initial['context']['knownAt'], 'mode': 'at', 'selection': 'latest_correction'})
    historical = next(f for f in c.call('workspace')['frames'] if f['at'] == frame['at'])
    c.call('futures/amend', {**change, 'snapshotId': historical['id'], 'operationId': str(uuid4()), 'expectedRevision': 3}, expected=422)
    check('a historical session cannot forge a current-knowledge amendment')
    c.call('market/current', {})
    current_frame = next(f for f in c.call('workspace')['frames'] if f['at'] == frame['at'])
    replacement = {**change, 'snapshotId': current_frame['id'], 'operationId': str(uuid4()), 'expectedRevision': 3, 'contractId': february['id'], 'curve': february['curve'], 'lots': '3'}
    c.call('futures/amend-preview', replacement)
    fourth = c.call('futures/amend', replacement)
    edited = details()['state']
    check('contract replacement retains trade reference and changes only instrument reference', edited['tradeReference'] == initial['state']['tradeReference'] and edited['instrumentReference'] != initial['state']['instrumentReference'])
    check('contract replacement creates a new binding without changing the shared original', fourth['revision'] == 4 and edited['instrumentId'] != initial['state']['instrumentId'] and next(h for h in c.call('futures/holdings', context)['holdings'] if h['tradeId'] == other['tradeId'])['instrumentId'] == initial['state']['instrumentId'])
    risk_catalog = analyst.call('futures/risk/catalog', analyst_context)
    factor = next(f for f in risk_catalog['factors'] if f['contractId'] == february['id'] and 'future' in f['curves'])
    risk = analyst.call('futures/risk', {'context': analyst_context, 'bookIds': [], 'portfolioIds': [], 'bumps': [{'seriesId': factor['seriesId'], 'quote': factor['quote'], 'size': '0.1', 'convention': risk_catalog['bumpConvention']}], 'convention': risk_catalog['convention'], 'numericalPolicy': risk_catalog['numericalPolicy']})
    pinned = next(r for r in risk['inputs']['positions'] if r['tradeId'] == trade['tradeId'])
    check('risk inputs pin the accepted replacement revision and exact event', pinned['revision'] == '4' and pinned['eventId'] == fourth['eventId'] and pinned['eventSchema'] == fourth['eventSchema'] and pinned['instrumentId'] == fourth['instrumentId'])
    # Restore the same January contract for the later shared-instrument browser fixture.
    restored = c.call('futures/amend', {**replacement, 'operationId': str(uuid4()), 'expectedRevision': 4, 'contractId': contract['id'], 'curve': contract['curve']})
    check('restoring an exact instrument restores its original readable reference', details()['state']['instrumentReference'] == initial['state']['instrumentReference'])
    check('a later amendment reuses the original exact instrument without rewriting history', restored['revision'] == 5 and details()['state']['instrumentId'] == initial['state']['instrumentId'] and details(4)['state']['instrumentId'] == fourth['instrumentId'])
    print(f'{passed} amendment HTTP checks passed.', flush=True)
