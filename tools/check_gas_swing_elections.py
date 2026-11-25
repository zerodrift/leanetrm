"""Commercial swing facts, both clocks and whole-right concurrency."""
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier
from uuid import uuid4
import json

from check_booked_side_programs import runtime_query
from preview_client import PreviewClient

AT = '2020-12-18T16:00:00Z'
LATER = '2020-12-18T16:02:00Z'
BETWEEN = '2020-12-18T16:01:00Z'
DAYS = ['2021-01-31', '2021-02-01', '2021-02-02']


def denied(call):
    return f"DO $proof$ BEGIN BEGIN PERFORM {call};RAISE EXCEPTION 'Unexpected election authority';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$proof$;"


def election_checks(client, query, manifest, context, booking, plain, run_worker):
    cat = manifest['catalogId']
    right = {**booking['swingRights'][0], 'dailyMinimum':'0', 'totalMinimum':'0', 'totalMaximum':'1000'}
    def book():
        saved = client.call('gas/composition/book', {**booking, 'operationId':str(uuid4()), 'swingRights':[right]})
        return {key:saved[key] for key in ('tradeId','instrumentId')}

    selected = book()
    def read(ctx=context, target=selected):
        return client.call('gas/swing/read', dict(context=ctx, **target))
    def heads(view):
        return [dict(day=h['day'], revisionId=h['revisionId']) for h in view['heads']]
    def row(i, quantity, effective=AT):
        return dict(day=DAYS[i], quantity=quantity, exercisingParty='us', submittedAt=AT,
                    effectiveAt=effective, evidence='Synthetic accepted election')
    def command(expected, *rows, target=selected):
        return dict(context=context, **target, expectedRevision=1, operationId=str(uuid4()), right=0,
                    expectedHeads=expected, elections=list(rows))
    def record(body, **kwargs):
        return client.call('gas/swing/record', body, **kwargs)
    def state():
        return query(f"""SELECT (SELECT count(*) FROM trading.gas_swing_elections WHERE trade_id='{selected['tradeId']}'),
            (SELECT count(*) FROM trading.gas_swing_commands WHERE trade_id='{selected['tradeId']}');""")
    def reject(body, code, status=422):
        before = state()
        assert record(body, expected=status)['code'] == code, body
        assert state() == before, body

    initial = read()
    empty = [dict(day=day,revisionId=None) for day in DAYS]
    assert heads(initial) == empty and initial['elections'] == [] and initial['nextElectionEffectiveAt'] is None
    old = {**context,'knowledgeMode':'retained','contextId':initial['context']['contextId']}
    first = row(0,'700')
    for bad in [dict(exercisingParty='counterparty'),dict(quantity='50'),dict(quantity='1200'),
                dict(day='2021-01-30'),dict(submittedAt='2020-12-18T15:59:59Z'),dict(submittedAt=LATER),
                dict(effectiveAt='2021-01-31T03:15:00.000001Z'),dict(effectiveAt='2021-01-31T03:15:00.0000001Z'),
                dict(evidence='\t')]:
        reject(command(empty, {**first, **bad}), 'swing_election')
    for malformed in [{k:v for k,v in first.items() if k!='quantity'}, {**first,'unknownField':1}]:
        reject(command(empty,malformed),'request_format',400)
    for bad in [dict(expectedHeads=empty[:-1]),dict(expectedHeads=[empty[0],empty[0],empty[2]]),
                dict(expectedRevision=2)]:
        reject({**command(empty,first),**bad},'swing_election_conflict',409)
    reject({k:v for k,v in command(empty,first).items() if k!='right'},'request_format',400)
    reject(command(empty, first, first), 'swing_election')
    batch = command(empty, first, row(2,'0'))
    saved = record(batch)
    assert record(batch) == saved
    active = read()
    d1 = saved['elections'][0]['revisionId']
    assert [e['quantity'] for e in active['elections']] == ['700','0']
    assert read(old)['elections'] == [] and heads(read(old)) == empty
    assert all(e['previousId'] is None and e['revision']==1 and e['publicationOrder']>0 and e['knownFrom'] for e in active['elections'])
    reject({**command(heads(active),first),'context':old},'historical_read_only')
    reject({**batch,'elections':[first]},'swing_election_conflict',409)
    future = record(command(heads(active),row(0,'200',LATER)))['elections'][0]
    now = read()
    assert heads(now)[0]['revisionId']==future['revisionId'] and now['elections'][0]['revisionId']==d1
    assert now['nextElectionEffectiveAt'].startswith(LATER[:-1])
    # The final sum is 900. Between the two instants, 700 + 600 + 100 exceeds 1,000.
    reject(command(heads(now),row(2,'100',BETWEEN),row(1,'600',BETWEEN)), 'swing_feasibility')
    reject(command(heads(now),row(0,'100',BETWEEN)), 'swing_election_conflict',409)
    reject(command(heads(active),row(1,'100')), 'swing_election_conflict',409)
    replayed = record(command(heads(now),{**first,'quantity':'700.0'}))['elections'][0]
    assert replayed['unchanged'] and replayed['revisionId']==d1 and read()['heads']==now['heads']
    assert record(batch)==saved
    cutoff = {**old,'contextId':now['context']['contextId']}
    def native(target):
        return f"security.read_gas_swing_elections('{cat}','{target['tradeId']}','{target['instrumentId']}')"
    def read_at(at):
        result = runtime_query(query,manifest,f"""SELECT security.use_market_context('{cat}',security.resolve_operation_context('{cat}','2020-12-18',NULL));
            SELECT security.use_trading_context('{cat}','{at}');SELECT {native(selected)};""")
        return json.loads(result.splitlines()[-1])
    assert read_at(LATER)['elections'][0]['quantity']=='200'
    withdrawn = record(command(heads(now),row(0,None,LATER)))['elections'][0]
    after = read_at(LATER)
    assert after['elections'][0]['quantity'] is None and after['elections'][0]['revisionId']==withdrawn['revisionId']
    assert after['nextElectionEffectiveAt'] is None and heads(read(cutoff))[0]['revisionId']==future['revisionId']
    complete = book()
    all_rows = [row(0,'700'),row(1,'300'),row(2,'0')]
    all_command = command(empty,*all_rows,target=complete)
    accepted = record(all_command)
    value = client.call('gas/composition/editor', dict(context=context,tradeId=complete['tradeId']))
    assert value['valuation']['presentValue']=='100',value['valuation']
    client.call('gas/swing/read',dict(context=context,tradeId=complete['tradeId'],instrumentId=plain['instrumentId']),expected=403)
    assert record({**all_command,'operationId':str(uuid4()),'instrumentId':plain['instrumentId']},expected=403)['code']=='access_denied'
    wire = json.dumps({k:v for k,v in all_command.items() if k!='context'})
    call = f"security.record_gas_swing_elections('{cat}','{wire}','{AT}')"
    runtime_query(query,manifest,denied(call),profile='risk')
    runtime_query(query,manifest,denied(call),prefix="DELETE FROM security.role_permissions WHERE permission='AmendTrade';",commit=False)
    revoke_source = f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}' AND workspace_id='{manifest['workspaceId']}';"
    runtime_query(query,manifest,denied(call),prefix=revoke_source,commit=False)
    runtime_query(query,manifest,f"""SELECT security.use_market_context('{cat}',security.resolve_operation_context('{cat}','2020-12-18',NULL));
        SELECT security.use_trading_context('{cat}','{AT}');RESET SESSION AUTHORIZATION;""" + revoke_source +
        'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + denied(native(complete)),commit=False)
    # Exercise the transaction seal after the writer's internal check and before commit.
    pending = {**all_command,'operationId':str(uuid4()),'expectedHeads':heads(read(target=complete)),
               'elections':[{**all_rows[0],'evidence':'Synthetic corrected acceptance evidence'}]}
    pending_wire = json.dumps({k:v for k,v in pending.items() if k!='context'})
    runtime_query(query,manifest,f"""SELECT security.record_gas_swing_elections('{cat}','{pending_wire}','{AT}');
        RESET SESSION AUTHORIZATION;DELETE FROM security.role_permissions WHERE permission='AmendTrade';
        SET LOCAL SESSION AUTHORIZATION preview_runtime;""" + denied('security.check_operation_seals()'), commit=False)
    assert heads(read(target=complete))==pending['expectedHeads']
    # Restore borrowed read inputs even when the caller already owns another financial source.
    runtime_query(query,manifest,f"""DO $proof$ DECLARE original uuid=security.resolve_operation_context('{cat}','2020-12-17',NULL);BEGIN
        PERFORM {call};PERFORM security.use_market_context('{cat}',original);
        IF NOT EXISTS(SELECT 1 FROM security.operation_knowledge WHERE id=original) THEN RAISE EXCEPTION 'Original source lost';END IF;
        END$proof$;""",commit=False)
    for change in [dict(elections=[{**first,'unknown':1}]),dict(expectedHeads=[{'day':DAYS[0]}]),dict(extra=1)]:
        invalid = {**pending,**change}
        malformed = json.dumps({k:v for k,v in invalid.items() if k!='context'})
        runtime_query(query,manifest,f"""DO $proof$ BEGIN BEGIN PERFORM security.record_gas_swing_elections('{cat}','{malformed}','{AT}');
            RAISE EXCEPTION 'Malformed native command accepted';EXCEPTION WHEN raise_exception THEN
            IF SQLERRM<>'swing_election' THEN RAISE;END IF;END;END$proof$;""")
    assert heads(read(target=complete))==pending['expectedHeads']
    # Accepted by the deadline, published after it. The operation's recording clock cannot stand in for either fact clock.
    late = {**all_command,'operationId':str(uuid4()),'expectedHeads':heads(read(target=complete)),
            'elections':[{**all_rows[0],'submittedAt':'2021-01-31T03:15:00Z','effectiveAt':'2021-01-31T03:15:00Z'}]}
    late_wire = json.dumps({k:v for k,v in late.items() if k!='context'})
    runtime_query(query,manifest,f"SELECT security.record_gas_swing_elections('{cat}','{late_wire}','2021-02-01T00:00:00Z');")

    concurrent = book()
    entrants = [command(empty,row(i,'700'),target=concurrent) for i in (0,1)]
    barrier = Barrier(2)
    def race(entry):
        writer = PreviewClient(client.url)
        writer.bootstrap()
        writer.call('profile',dict(profile='trader'))
        barrier.wait(timeout=20)
        return writer.call('gas/swing/record',entry,expected=(200,409))
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(race,entrants))
    assert sorted(v.get('code','accepted') for v in outcomes)==['accepted','swing_election_conflict']
    loser = next(i for i,v in enumerate(outcomes) if 'code' in v)
    view = read(target=concurrent)
    assert len(view['elections'])==1
    assert record({**entrants[loser],'expectedHeads':heads(view)},expected=422)['code']=='swing_feasibility'
    assert query(f"SELECT count(*) FROM trading.gas_swing_commands WHERE trade_id='{concurrent['tradeId']}';")=='1'
    assert read(target=concurrent)['elections']==view['elections']
    minimum = client.call('gas/composition/book',{**booking,'operationId':str(uuid4())})
    target = {key:minimum[key] for key in ('tradeId','instrumentId')}
    # 700 is inside the daily range but even two 1,100 days cannot reach 3,000.
    assert record(command(empty,row(0,'700'),target=target),expected=422)['code']=='swing_feasibility'
    assert heads(read(target=target))==empty
    # A simultaneous reallocation is feasible even when its first row alone would overcommit.
    swapped = command(heads(read(target=concurrent)),row(loser,'900'),row(1-loser,'100'),target=concurrent)
    assert len(record(swapped)['elections'])==2
    assert sum(int(e['quantity']) for e in read(target=concurrent)['elections'])==1000
    check_worker(client, query, manifest, context, complete, plain, accepted, run_worker)
    # A previous binding is readable at its certified cutoff. An exact retry
    # retains its outcome there, but a fresh command cannot amend that revision.
    from check_gas_exercise import amend_rights
    pinned = {**old,'contextId':read()['context']['contextId']}
    replacement = amend_rights(query,manifest,selected,[],AT,swing_rights=[{**right,'holder':'counterparty'}])
    assert replacement['revision']==2 and replacement['instrumentId']!=selected['instrumentId']
    assert record(batch)==saved
    assert read(pinned)['elections'][0]['revisionId']==d1
    client.call('gas/swing/read',dict(context=context,**selected),expected=403)
    reject(command(heads(after),first),'swing_election_conflict',409)
    print('PASS swing elections: atomic whole-budget writes, current authority, concurrent heads, semantic replay, withdrawal and both clocks',flush=True)


def check_worker(client, query, manifest, context, selected, other, accepted, run_worker):
    cat = manifest['catalogId']
    def reader(catalog, trade, binding):
        return f"security.read_gas_swing_elections('{catalog}','{trade}','{binding}')"
    call = reader(cat,selected['tradeId'],selected['instrumentId'])
    query('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;' + denied(call) + 'ROLLBACK;')
    risk = PreviewClient(client.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    scope = dict(context={**context,'convention':'trading_workspace_v1'},bookIds=[],portfolioIds=[],tradeIds=[selected['tradeId']])
    catalog = risk.call('portfolio/risk/catalog',scope)
    assert not catalog['excludedPositions'],catalog['excludedPositions']
    req = {**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':catalog['context']['contextId']},
           'detail':'holdings','convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],
           'pricingPolicy':catalog['pricingPolicy'],'stress':[],
           'bumps':[dict(factor=catalog['factors'][0]['factor'],size='0.1',convention=catalog['bumpConvention'])]}
    job = risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=req))
    engine = query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{job['id']}';")
    # Each probe rolls back its lease so the real worker can recover the queued request.
    bind = f"""DO $bind$ DECLARE lease jsonb=risk.claim(120,'{engine}');BEGIN
        IF lease->>'jobId' IS DISTINCT FROM '{job['id']}' THEN RAISE EXCEPTION 'Unexpected lease';END IF;
        PERFORM risk.bind((lease->>'jobId')::uuid,(lease->>'leaseToken')::uuid);END$bind$;"""
    def worker(sql):
        return query('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;' + bind + sql + 'ROLLBACK;')
    view = json.loads(worker(f"SELECT {call};").splitlines()[-1])
    assert [r['revisionId'] for r in view['elections']]==[r['revisionId'] for r in accepted['elections']]
    for refused in [reader(str(uuid4()),selected['tradeId'],selected['instrumentId']),
                    reader(cat,other['tradeId'],other['instrumentId']),reader(cat,selected['tradeId'],other['instrumentId'])]:
        worker(denied(refused))
    worker(f"RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{job['id']}';"
           + 'SET LOCAL SESSION AUTHORIZATION preview_risk_worker;' + denied(call))
    worker("RESET SESSION AUTHORIZATION;UPDATE risk.workers SET enabled=false WHERE login='preview_risk_worker';"
           + 'SET LOCAL SESSION AUTHORIZATION preview_risk_worker;' + denied(call))
    run_worker()
    completed = risk.call('portfolio/risk/jobs/'+job['id'])
    assert completed['state']=='completed',completed
    assert completed['result']['factors'][0]['positions'][0]['values']['base']=='100',completed['result']['factors']
    print('PASS swing reader admits the leased holding and refuses off-scope or expired worker reads with SQLSTATE 42501',flush=True)
