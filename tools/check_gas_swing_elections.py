"""Commercial swing facts, both clocks and whole-right concurrency."""
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier
from time import monotonic, sleep
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


def read_grant_transition(query, manifest, reader):
    """Delay the reader's first schedule access until a disjoint successor grant begins."""
    cat = manifest['catalogId']
    role, initial, successor = uuid4(), uuid4(), uuid4()
    name = 'swing_read_' + role.hex
    table = 'trading.instrument_side_schedules'
    try:
        boundary = query(f"""BEGIN;INSERT INTO security.roles VALUES('{role}','{name}','Synthetic swing read authority');
            INSERT INTO security.role_permissions VALUES('{role}','ReadTrades');
            INSERT INTO security.role_grants SELECT '{initial}',principal_id,'{role}','{manifest['tenantId']}','{manifest['workspaceId']}',
             clock_timestamp()-interval '1 day',clock_timestamp()+interval '4 seconds',false FROM security.identity_bindings
             WHERE issuer='https://preview.invalid' AND subject='editor';
            INSERT INTO security.role_grants SELECT '{successor}',principal_id,role_id,tenant_id,workspace_id,
             granted_to,granted_to+interval '1 minute',false FROM security.role_grants WHERE id='{initial}';
            SELECT granted_to FROM security.role_grants WHERE id='{initial}';COMMIT;""")
        # The ordinary fixture editor has market permission. Only the first of
        # the two ReadTrades grants may contribute to this request's initial deadline.
        bind = f"""SELECT security.use_market_context('{cat}',security.resolve_operation_context('{cat}','2020-12-18',NULL));
            SELECT security.use_trading_context('{cat}','{AT}');"""
        check = f"""DO $bound$ BEGIN IF settlement_data.futures_margin_preview_authority('{cat}') IS DISTINCT FROM TIMESTAMPTZ '{boundary}'
             THEN RAISE EXCEPTION 'Wrong initial grant deadline';END IF;END$bound$;"""
        with ThreadPoolExecutor(max_workers=2) as pool:
            holder = pool.submit(query,f"""SET application_name='{name}';BEGIN;LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;
                SELECT pg_sleep_until(TIMESTAMPTZ '{boundary}');COMMIT;""")
            limit = monotonic()+6
            while query(f"SELECT EXISTS(SELECT FROM pg_stat_activity WHERE application_name='{name}' AND wait_event='PgSleep');")!='t':
                assert not holder.done() and monotonic()<limit, 'Schedule lock did not start'
                sleep(.03)
            reading = pool.submit(runtime_query,query,manifest,bind + check + denied(reader),profile='editor',commit=False)
            while query(f"""SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a USING(pid)
                WHERE NOT l.granted AND l.relation='{table}'::regclass AND a.query LIKE '%read_gas_swing_elections%'
                AND clock_timestamp()<TIMESTAMPTZ '{boundary}');""")!='t':
                assert not reading.done() and monotonic()<limit, 'Reader did not reach the schedule under its initial grant'
                sleep(.03)
            holder.result(timeout=10)
            reading.result(timeout=10)
        new_read = runtime_query(query,manifest,bind+f"SELECT jsonb_array_length({reader}->'elections');",profile='editor',commit=False)
        assert new_read.splitlines()[-1]=='3', 'A new request under the successor grant must see the three retained elections'
    finally:
        query(f"""BEGIN;DELETE FROM security.role_grants WHERE role_id='{role}';DELETE FROM security.role_permissions WHERE role_id='{role}';
            DELETE FROM security.roles WHERE id='{role}';COMMIT;""")


def action_grant_transition(client, query, manifest, path, body, table, *, successor=True):
    """Hold the requested bound read across expiry, then check the selected ticket or action."""
    role, grant = uuid4(), uuid4()
    name = 'swing_amend_' + role.hex
    existing = json.loads(query("SELECT json_agg(role_id)::text FROM security.role_permissions WHERE permission='AmendTrade'"))
    next_grant = f"""INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,
        granted_to,granted_to+interval '1 minute',false FROM security.role_grants WHERE id='{grant}';""" if successor else ''
    try:
        boundary = query(f"""BEGIN;DELETE FROM security.role_permissions WHERE permission='AmendTrade';
            INSERT INTO security.roles VALUES('{role}','{name}','Synthetic swing action authority');
            INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');
            INSERT INTO security.role_grants SELECT '{grant}',principal_id,'{role}','{manifest['tenantId']}','{manifest['workspaceId']}',
             clock_timestamp()-interval '1 day',clock_timestamp()+interval '4 seconds',false FROM security.identity_bindings
             WHERE issuer='https://preview.invalid' AND subject='trader';
            {next_grant}
            SELECT granted_to FROM security.role_grants WHERE id='{grant}';COMMIT;""")
        with ThreadPoolExecutor(max_workers=2) as pool:
            locked = pool.submit(query, f"""SET application_name='{name}';BEGIN;LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;
                SELECT pg_sleep_until(TIMESTAMPTZ '{boundary}');COMMIT;""")
            limit = monotonic()+8
            while query(f"SELECT EXISTS(SELECT FROM pg_stat_activity WHERE application_name='{name}' AND wait_event='PgSleep');")!='t':
                assert not locked.done() and monotonic()<limit, 'Grant-transition table lock did not start'
                sleep(.03)
            request = pool.submit(client.call,path,body,expected=(200,403))
            while query(f"""SELECT EXISTS(SELECT FROM pg_locks WHERE NOT granted AND relation='{table}'::regclass
                    AND clock_timestamp()<TIMESTAMPTZ '{boundary}');""")!='t':
                assert not request.done() and monotonic()<limit, f'{path} did not reach its bound read under grant A'
                sleep(.03)
            locked.result(timeout=10)
            result = request.result(timeout=10)
            if successor:
                assert client.last_status==403, f'{path} published a writable result under successor B after waiting under A'
            else:
                assert client.last_status==200, f'The plain ticket lost its read when only AmendTrade expired: {result}'
                assert result['schedule'] and not result['actions']['record']['allowed'] and 'recordSwing' not in result['actions']
        fresh = client.call(path,body)
        if successor:
            assert ('command' in fresh if path.endswith('/prepare') else fresh['actions']['recordSwing']['allowed'])
        else:
            assert fresh['schedule'] and not fresh['actions']['record']['allowed']
    finally:
        restore = ','.join(f"('{r}','AmendTrade')" for r in existing)
        query(f"""BEGIN;DELETE FROM security.role_grants WHERE role_id='{role}';DELETE FROM security.role_permissions WHERE role_id='{role}';
            DELETE FROM security.roles WHERE id='{role}';INSERT INTO security.role_permissions VALUES {restore};COMMIT;""")


def ticket_source_lookups(client, context, draft):
    from copy import deepcopy
    from decimal import Decimal
    catalog = client.call('gas/catalog',context)
    draft = deepcopy(draft)
    source, cash = draft['sides']
    cash['values'].pop('cash_price')
    source['values']['delivery_to']='2021-02-01'
    for point, status in [('synthetic_intrinsic_point',200),('synthetic_swing_overlap_point',422)]:
        path = next(p for p in catalog['locations'] if p['deliveryPoint']==point)
        source['values'].update(pipeline=path['pipeline'],zone=path['zone'],delivery_point=point,product=path['product'])
        for endpoint, side, field in [('choices',source,'forecast_curve'),('lookup',source,'price'),('lookup',cash,'cash_price')]:
            value=client.call('tickets/'+endpoint,dict(context=context,draft=draft,side=side['code'],field=field),expected=(200,422))
            assert client.last_status==status,(point,endpoint,side['code'],field,value)
            if status==422:
                assert (value['code'],value['side'],value['field'])==('gas_composition_input',source['code'],'delivery_point')
            elif endpoint=='choices':
                assert any(c['value']=='assigned' for c in value['choices'])
            else:
                assert Decimal(value['value'])==Decimal('3.20'), value


def native_authority_fences(query, manifest, reader, replay, target):
    read_grant_transition(query,manifest,reader)
    cat = manifest['catalogId']
    assert query("""SELECT bool_or(has_column_privilege(login,'security.request_contexts',field,'UPDATE'))
        FROM unnest(ARRAY['preview_runtime','preview_risk_worker']) login,
         unnest(ARRAY['swing_replay_catalog_id','swing_replay_operation_id','swing_replay_until']) field;""")=='f'
    trade = target['tradeId']
    counts = f"""SELECT jsonb_build_array((SELECT count(*) FROM trading.gas_swing_elections WHERE trade_id='{trade}'),
        (SELECT count(*) FROM trading.gas_swing_commands WHERE trade_id='{trade}'))"""
    previous = query(counts)
    runtime_query(query,manifest,f"""SELECT {replay};
        SELECT security.use_market_context('{cat}',security.resolve_operation_context('{cat}','2020-12-17',NULL));
        SELECT security.use_trading_context('{cat}','2020-12-17T16:00:00Z');RESET SESSION AUTHORIZATION;
        DO $facts$ BEGIN IF ({counts}) IS DISTINCT FROM '{previous}'::jsonb THEN RAISE EXCEPTION 'Replay inserted a fact or receipt';END IF;END$facts$;
        DELETE FROM security.role_permissions WHERE permission='AmendTrade';SET LOCAL SESSION AUTHORIZATION preview_runtime;"""
        + denied('security.check_operation_seals()'), commit=False)
    # A second retry after a grant transition must not replace the earlier
    # pending admission. Both operations stay retries of the same receipt.
    role = uuid4()
    setup = f"""DELETE FROM security.role_permissions WHERE permission='AmendTrade';
        INSERT INTO security.roles VALUES('{role}','swing_replay_{role.hex}','Synthetic swing recording authority');
        INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');
        SELECT set_config('synthetic_swing.boundary',(clock_timestamp()+interval '2 seconds')::text,true);
        INSERT INTO security.role_grants SELECT gen_random_uuid(),b.principal_id,'{role}','{manifest['tenantId']}','{manifest['workspaceId']}',
         w.from_at,w.to_at,false FROM security.identity_bindings b CROSS JOIN LATERAL
         (VALUES (clock_timestamp()-interval '1 day',current_setting('synthetic_swing.boundary')::timestamptz),
                 (current_setting('synthetic_swing.boundary')::timestamptz,clock_timestamp()+interval '2 minutes')) AS w(from_at,to_at)
         WHERE b.issuer='https://preview.invalid' AND b.subject='trader';"""
    runtime_query(query,manifest,f"""SELECT security.check_operation_seals();
        DO $bound$ BEGIN IF security.gas_swing_write_until('{cat}') IS DISTINCT FROM current_setting('synthetic_swing.boundary')::timestamptz
        THEN RAISE EXCEPTION 'Wrong initial write deadline';END IF;END$bound$;
        SELECT {replay};SELECT pg_sleep_until(current_setting('synthetic_swing.boundary')::timestamptz);SELECT {replay};"""
        + denied('security.check_operation_seals()'),prefix=setup,commit=False)
    assert query(counts)==previous
    print('PASS blocked swing read expires, successor reads three elections, and every native retry retains its own receipt and deadline at the seal',flush=True)


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
    # The ticket keeps the effective commercial fact separate from the later known predecessor.
    ticket=client.call('tickets/trade',dict(context=context,tradeId=selected['tradeId'],bindingId=selected['instrumentId'],revision=1))
    contract=ticket['swingContract']
    assert contract['referenceQuantity']=='1000' and contract['priceUnit'] and [d['day'] for d in contract['days']]==DAYS
    new_terms=client.call('tickets/preview',dict(context=context,draft=ticket['draft']))
    assert new_terms['swingContract']==contract and new_terms['context']['knownAt']
    action=ticket['actions']['recordSwing']
    assert action['allowed'] and action['readContextId']==ticket['context']['contextId']
    assert action['days'][0]['head']==future['revisionId'] and action['days'][0]['electedQuantity']=='700'
    assert action['days'][2]['electedQuantity']=='0' and action['committed']=='700'
    assert ticket['draft']['sides'][0]['values']['quantity_policy']=='swing_elected_delivery_quantity_v1'
    choice=dict(context=context,readContextId=action['readContextId'],**selected,expectedRevision=1,operationId=str(uuid4()),right=0,
        firstDay=DAYS[1],lastDay=DAYS[2],decision='elect',quantity='600',exercisingParty='us',
        submittedAt=AT,effectiveAt=BETWEEN,evidence='Synthetic ticket range')
    ticket_request=dict(context=context,tradeId=selected['tradeId'],bindingId=selected['instrumentId'],revision=1)
    plain_request=dict(context=context,tradeId=plain['tradeId'],bindingId=plain['instrumentId'],revision=1)
    action_grant_transition(client,query,manifest,'tickets/trade',plain_request,'security.market_catalog_scopes',successor=False)
    print('PASS ordinary working ticket retains its schedule and disables recording when AmendTrade expires during a catalog-scope wait',flush=True)
    for path,body,table in [('gas/swing/prepare',choice,'security.market_catalog_scopes'),
                            ('gas/swing/prepare',choice,'trading.gas_trade_revisions'),
                            ('tickets/trade',ticket_request,'trading.gas_trade_revisions')]:
        action_grant_transition(client,query,manifest,path,body,table)
    ticket_source_lookups(client,context,ticket['draft'])
    print('PASS daily-only and overlapping swing source lookups; ticket and prepare retain their initial AmendTrade deadline',flush=True)
    prepared=client.call('gas/swing/prepare',choice)['command']
    assert prepared['expectedHeads']==heads(now) and [r['day'] for r in prepared['elections']]==DAYS[1:]
    failure=record(prepared,expected=422)
    assert (failure['code'],failure['termSection'],failure['termRow'],failure['field'])==('swing_feasibility','swingElections',1,'quantity')
    invalid=client.call('gas/swing/prepare',{**choice,'quantity':'75'},expected=422)
    assert (invalid['code'],invalid['termRow'],invalid['field'])==('swing_election',1,'quantity')
    print('PASS ticket retains governed source, effective zero and future head; atomic range rejects with its exact day row',flush=True)
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
    native_authority_fences(query,manifest,native(complete),call,complete)
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
        PERFORM {call};PERFORM security.use_market_context('{cat}',original);PERFORM security.check_operation_seals();
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
    # A historical ticket must use its original facts even after a later withdrawal.
    withdrawal=record(command(heads(read()),{**row(2,None),'evidence':'Synthetic zero withdrawn'}))['elections'][0]
    location=dict(tradeId=selected['tradeId'],bindingId=selected['instrumentId'],revision=1)
    historical=client.call('tickets/trade',dict(context=cutoff,**location))
    assert historical['context']['contextId']==cutoff['contextId'] and historical['scheduleRefusal']['code']=='exercise_marks'
    previous=historical['actions']['recordSwing']
    assert not previous['allowed'] and previous['readContextId'] is None
    assert previous['committed']=='700' and previous['remainingMaximum']=='300'
    assert previous['days'][0]['head']==future['revisionId'] and previous['days'][2]['electedQuantity']=='0'
    reader=PreviewClient(client.url)
    reader.bootstrap()
    reader.call('profile',dict(profile='risk'))
    current=reader.call('tickets/trade',dict(context=context,**location))
    action=current['actions']['recordSwing']
    assert not action['allowed'] and action['readContextId'] is None and action['committed']=='700'
    assert action['days'][2]['electedQuantity'] is None and action['days'][2]['effectiveRevision']==withdrawal['revisionId']
    assert action['days'][2]['evidence']=='Synthetic zero withdrawn'
    print('PASS unpriced read-only ticket retains the effective withdrawal; earlier cutoff still shows elected zero and the original budget',flush=True)
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
