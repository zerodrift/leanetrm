"""Exercise the market editor with certified browser inputs on a disposable preview."""
from concurrent.futures import ThreadPoolExecutor
import json
import time
from uuid import uuid4

from preview_client import PreviewClient


def run_checks(url, query, manifest):
    def client(profile):
        c = PreviewClient(url)
        c.bootstrap()
        c.call('profile', dict(profile=profile))
        return c

    day = '2020-12-18'
    at = day + 'T16:00:00Z'
    def capture(c):
        known = c.call('date-schedules/context', dict(effectiveOn=day, mode='current'))['context']
        return dict(contextId=known['contextId'], asOf=at, businessDate=day, snapshotId=None)

    def call(c, route, body, expected=200):
        return c.call('curve-marks/editor/' + route, body, expected=expected)

    editor = client('editor')
    query("DELETE FROM security.role_permissions WHERE permission='ReadTrades';")
    source = dict(definitionRevisionId=manifest['revisionId'], referenceCatalogId=manifest['referenceCatalogId'],
                  referenceRevisionId=manifest['referenceRevisionId'])
    original = capture(editor)
    frames = editor.bootstrap()['frames']
    frame = next(f for f in frames if f['at'].startswith(at[:-1]) and f['businessDate'] == day)
    original['snapshotId'] = frame['id']
    setup = dict(context=original, source=source, view=None, search='', offset=0, limit=50)
    choices = call(editor, 'setup', setup)
    assert choices['canPublish'] and choices['source'] == source and choices['context']['mode'] == 'working'
    first = choices['curves'][0]['code']
    key = dict(firstCurve=first, secondCurve=None, month='2024-06')
    view = dict(kind='volatility', mode='grid', curves=[first], fromMonth=key['month'], toMonth=key['month'])
    diagonal = {**view,'kind':'correlation'}
    one = call(editor,'setup',{**setup,'view':diagonal})
    assert one['layout']['cells'] == [dict(address=dict(row=0,column=0),key=None,editable=False)]
    assert one['draft']['coverage'] == dict(marked=0,missing=0,changed=0,invalid=0)
    assert call(editor,'setup',{**setup,'view':{**diagonal,'curves':['absent_curve']}},422)['code'] == 'curve_mark_source'
    setup['view'] = view
    batch = dict(policy='curve_mark_batch_v1', kind='volatility', ownerId=choices['ownerId'], operationId=str(uuid4()),
                 context=original, source=source, changes=[dict(**key, expectedRevision=0, value='0.50')],
                 reason='Synthetic publisher review')
    status = {k: batch[k] for k in ('kind', 'ownerId', 'operationId')}
    assert call(editor, 'status', status) == dict(status='absent', receipt=None)
    def counts():
        return query('SELECT jsonb_build_array((SELECT count(*) FROM trading.curve_mark_requests),'
                     '(SELECT count(*) FROM trading.curve_mark_facts),(SELECT count(*) FROM trading.publications));')
    before = counts()
    first_review = call(editor, 'review', batch)
    assert first_review['changes'][0]['wouldChange'] and not first_review['alreadyRecorded']
    assert set(first_review['changes'][0]['headAfterReview']) == {'revision','value','effectiveAt'}
    assert counts() == before
    assert call(editor, 'status', status)['status'] == 'absent'
    first_receipt = call(editor, 'record', batch)
    assert first_receipt['cells'][0]['value'] == '0.5'
    assert call(client('editor'), 'status', status) == dict(status='committed', receipt=first_receipt)
    renewed = client('editor')
    assert frame['id'] not in [f['id'] for f in renewed.bootstrap()['frames']]
    assert call(renewed, 'record', batch) == first_receipt
    assert call(renewed, 'record', {**batch, 'reason':'Different reason'}, 409)['code'] == 'curve_mark_request_conflict'
    pinned = call(editor, 'setup', setup)['draft']
    assert pinned['coverage'] == dict(marked=0, missing=1, changed=0, invalid=0)
    assert pinned['cells'][0]['state']['writeHead'] is None
    fresh = capture(editor)
    validated = dict(context=fresh, source=source, view=view, edits=[dict(key=key,value='0.600000000001')])
    draft = call(editor, 'validate', validated)['draft']
    assert draft['coverage'] == dict(marked=1,missing=0,changed=1,invalid=0)
    assert draft['cells'][0]['difference'] == '0.100000000001'
    assert call(editor, 'validate', {**validated, 'edits':[dict(key=key,value='')]})['draft']['coverage']['invalid'] == 1
    assert call(editor, 'validate', {**validated, 'edits':[dict(key=key,value=None)]}, 400)['code'] == 'request_format'
    duplicate = json.dumps(validated).replace('"edits":', '"edits": [], "edits":').encode()
    assert call(editor, 'validate', duplicate, 400)['code'] == 'request_format'
    paste = call(editor, 'validate', {**validated, 'paste':dict(mode='rectangle',start=dict(row=0,column=0),text='0.25')})
    assert paste['addressed'][0]['key'] == key and paste['draft']['cells'][0]['difference'] == '-0.25'
    invalid = call(editor, 'validate', {**validated, 'paste':dict(mode='rectangle',start=dict(row=0,column=0),text='=4')}, 422)
    assert invalid['code'] == 'curve_mark_value' and invalid['address'] == dict(row=0,column=0)
    for changes in [dict(businessDate='2020-12-19'),dict(snapshotId=str(uuid4())),dict(contextId=str(uuid4()))]:
        assert call(editor,'setup',{**setup,'context':{**fresh,**changes}},422)['code'] in ('curve_mark_context','temporal_context')
    assert call(editor,'setup',{**setup,'source':{**source,'definitionRevisionId':str(uuid4())}},422)['code'] == 'curve_mark_source'

    def change(value, revision, context=fresh):
        return {**batch,'operationId':str(uuid4()),'context':context,'changes':[dict(**key,expectedRevision=revision,value=value)]}

    contender = change('0.7',1)
    assert call(editor,'review',contender)['changes'][0]['wouldChange']
    second = call(editor,'record',change('0.6',1))
    replay_review = call(renewed,'review',batch)
    assert replay_review['alreadyRecorded'] and not replay_review['changes'][0]['wouldChange']
    assert replay_review['changes'][0]['acknowledgedRevision'] == 1 and replay_review['changes'][0]['headAfterReview'] is None
    assert call(renewed,'status',status)['receipt'] == first_receipt
    assert call(editor,'record',contender,409)['code'] == 'curve_mark_stale'
    old = change('0.5',0)
    review = call(editor,'review',old)['changes'][0]
    assert not review['wouldChange'] and review['acknowledgedRevision'] == 1 and review['headAfterReview']['value'] == '0.6'
    ack = call(editor,'record',old)['cells'][0]
    assert ack['markId'] == first_receipt['cells'][0]['markId'] and not ack['changed']
    assert ack['headAtPublication']['markId'] == second['cells'][0]['markId'] and ack['headAtPublication']['revision'] == 2
    later = change('0.8',2,{**fresh,'asOf':day+'T17:00:00Z'})
    call(editor,'record',later)
    current = capture(editor)
    latest = call(editor,'setup',{**setup,'context':current})['draft']['cells'][0]['state']
    assert (latest['selected']['value'],latest['writeHead']['value'],latest['expectedRevision'],latest['editBlock']['code']) == ('0.6','0.8',None,'curve_mark_time')
    hist = dict(source=source,context=current,kind='volatility',key=key,offset=0,limit=2)
    result = call(editor,'history',hist)
    assert result['hasMore'] and [r['effectiveEligible'] for r in result['revisions']] == [False, True]
    assert [r['mark']['value'] for r in result['revisions']] == ['0.8','0.6']
    # Canonical pair writes and mirror reads use one stored mark per month.
    currency = choices['curves'][0]['currency']
    second_curve = next(c['code'] for c in choices['curves'] if c['code'] != first and c['currency'] == currency)
    pair = dict(firstCurve=second_curve,secondCurve=first,month=key['month'])
    correlation = {**batch,'operationId':str(uuid4()),'kind':'correlation','context':current,
                   'changes':[dict(**pair,value='-0.25',expectedRevision=0)]}
    pair_review = call(editor,'review',correlation)
    pair_key = dict(firstCurve=min(first,second_curve),secondCurve=max(first,second_curve),month=key['month'])
    assert pair_review['changes'][0]['key'] == pair_key
    assert call(editor,'record',correlation)['cells'][0]['key'] == pair_key
    matrix = {**diagonal,'curves':[second_curve,first]}
    grid = call(editor,'setup',{**setup,'context':capture(editor),'view':matrix})
    assert [cell['key'] for cell in grid['layout']['cells']] == [None,pair_key,pair_key,None]
    assert grid['draft']['coverage'] == dict(marked=1,missing=0,invalid=0,changed=0)
    assert grid['draft']['cells'][0]['state']['selected']['value'] == '-0.25'
    before = call(editor,'setup',{**setup,'context':fresh})['context']
    editor.call('market/context',dict(effectiveOn=day,mode='at',knownAt=before['knownAt'],selection='latest_correction'))
    historical = call(editor,'setup',{**setup,'context':fresh})
    state = historical['draft']['cells'][0]['state']
    assert historical['context']['mode'] == 'historical' and not historical['canPublish']
    assert (state['selected']['value'],state['writeHead'],state['expectedRevision']) == ('0.5',None,None)
    assert len(call(editor,'history',{**hist,'context':fresh})['revisions']) == 1
    assert call(editor,'setup',{**setup,'context':current},422)['code'] == 'curve_mark_context'
    assert call(editor,'review',batch,422)['code'] == 'historical_read_only'
    reader = client('viewer')
    read = call(reader,'setup',{**setup,'context':capture(reader)})
    assert not read['canPublish'] and read['draft']['cells'][0]['state']['writeHead'] is None
    readonly = call(reader,'validate',{**validated,'context':capture(reader)})
    assert readonly['draft']['cells'][0]['error']['code'] == 'curve_mark_read_only'
    for route in ['review','record']:
        assert call(reader,route,{**batch,'ownerId':read['ownerId']},403)['code'] == 'curve_mark_denied'
    assert call(reader,'status',status,403)['code'] == 'curve_mark_owner_changed'
    assert call(reader,'status',{**status,'ownerId':read['ownerId']})['status'] == 'absent'
    expiring = change('0.4',0,capture(renewed))
    expiring['changes'][0]['month'] = '2024-07'
    check_expired(query, manifest, lambda: call(renewed,'record',expiring,403))
    assert call(renewed,'status',{**status,'operationId':expiring['operationId']})['status'] == 'absent'
    # Even a read-only original publisher may reconcile a committed request.
    query("DELETE FROM security.role_permissions WHERE permission='ImportObservations';")
    assert call(renewed,'status',status)['receipt'] == first_receipt
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{source['referenceCatalogId']}';")
    assert call(renewed,'status',status,403)['code'] == 'curve_mark_denied'
    print('PASS curve mark HTTP publisher, temporal analytics, review, stale race, receipt recovery and source revocation',flush=True)


def check_expired(query, manifest, attempt):
    """A later import grant cannot replace the deadline captured before the status lock."""
    role = query("SELECT id FROM security.roles WHERE code='market_data_editor';")
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE subject='editor';")
    temporary = str(uuid4())
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    with ThreadPoolExecutor(max_workers=1) as pool:
        blocker = pool.submit(query, "BEGIN; SET LOCAL application_name='curve_editor_http_wait';"
                              "SELECT pg_advisory_xact_lock(730849102); SELECT pg_sleep(3.5); COMMIT;")
        until = time.monotonic() + 4
        while query("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE application_name='curve_editor_http_wait' "
                   "AND wait_event='PgSleep');") != 't':
            assert time.monotonic() < until, 'The publication wait did not start'
            time.sleep(.02)
        query(f"""BEGIN;
            DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ImportObservations');
            INSERT INTO security.roles VALUES('{temporary}','synthetic_curve_timer','Synthetic publication timer');
            INSERT INTO security.role_permissions VALUES('{temporary}','ImportObservations');
            INSERT INTO security.role_grants VALUES
              (gen_random_uuid(),'{actor}','{temporary}','{tenant}','{workspace}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '1.2 seconds',false),
              (gen_random_uuid(),'{actor}','{temporary}','{tenant}','{workspace}',clock_timestamp()+interval '2 seconds',clock_timestamp()+interval '30 seconds',false);
            COMMIT;""")
        try:
            assert attempt()['code'] == 'curve_mark_denied'
            blocker.result(timeout=10)
        finally:
            query(f"""BEGIN;
                DELETE FROM security.role_grants WHERE role_id='{temporary}';
                DELETE FROM security.role_permissions WHERE role_id='{temporary}';
                DELETE FROM security.roles WHERE id='{temporary}';
                INSERT INTO security.role_permissions VALUES('{role}','ImportObservations');
                COMMIT;""")
