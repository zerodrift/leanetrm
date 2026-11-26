"""Contract examples shared by authenticated native and HTTP quantity-right checks."""
from copy import deepcopy
from uuid import uuid4


def january(base):
    body = deepcopy(base)
    body.update(operationId=str(uuid4()))
    body['physicalSides'][0].update(deliveryFrom='2021-01-01', deliveryTo='2021-02-01')
    body['swingRights'] = [dict(side='gas', entries=['gas','strike'], holder='us',
        dailyMinimum='900', dailyMaximum='1100', planningStep='100', limitPeriod='delivery_month',
        totalMaximum='1005', totalBasis='per_day_v1', changeCount=2, countRule='differs_from_base_v1',
        planRule='best_plan_on_forwards_v1', deadlineRule='lead_before_first_gas_day_v1', leadHours=6,
        quantityPolicy='swing_elected_quantity_v1', pricing=dict(model='gas_swing_one_factor_lognormal_v1',
            dayCount='act_365_fixed_instant_v1', numericalPolicy='swing_trinomial_6h_unit_reference_v2',
            marksConvention='curve_marks_log_return_held_v1'))]
    return body


def booking_fields(client,query,manifest,base):
    from check_gas_pricing import staged_booking
    body=january(base)
    booked=client.call('gas/composition/book',body)
    from check_gas_exercise import stage_gas_composition
    native=dict(body,operationId=str(uuid4()))
    def trades():
        return int(query("SELECT count(*) FROM trading.trades"))
    before=trades()
    stage_gas_composition(query,manifest,booked['instrumentId'],native,'2020-12-18T10:00:00Z',refusal='swing_booking_frame')
    assert trades()==before
    receipt=stage_gas_composition(query,manifest,booked['instrumentId'],native,'2020-12-18T16:00:00Z')
    assert receipt['tradeId'] and trades()==before+1
    print('PASS native named-frame clock mismatch leaves trade count unchanged and a matching command publishes',flush=True)
    session_frames(client,query,manifest,body,booked)
    context={k:body[k] for k in ('snapshotId','convention','knowledgeMode')}
    draft=client.call('tickets/trade',dict(context=context,tradeId=booked['tradeId'],bindingId=booked['instrumentId'],revision=1))['draft']
    for field in ('swing_limit_period','swing_plan_rule','swing_deadline_rule','quantity_policy'):
        invalid=deepcopy(draft)
        if field=='quantity_policy':
            next(side for side in invalid['sides'] if side['code']=='gas')['values'][field]='unsupported'
        else:
            invalid['swingRights'][0][field]='unsupported'
        refusal=client.call('tickets/preview',dict(context=context,draft=invalid),expected=422)
        assert (refusal.get('code'),refusal.get('field'))==('swing_right',field),refusal
        if field=='quantity_policy':
            assert refusal['side']=='gas' and refusal.get('termRow') is None,refusal
        else:
            assert (refusal['termSection'],refusal['termRow'])==('swingRights',0),refusal
    right=body['swingRights'][0]
    for edits,code,field in [
        (dict(limitPeriod='unsupported'),'swing_right','limitPeriod'),(dict(planRule='unsupported'),'swing_right','planRule'),
        (dict(deadlineRule='unsupported'),'swing_right','deadlineRule'),
        (dict(quantityPolicy='swing_elected_delivery_quantity_v1'),'swing_right','quantityPolicy'),
        (dict(planningStep='0'),'swing_right','planningStep'),(dict(dailyMaximum='950'),'swing_right','dailyMaximum'),
        (dict(totalMinimum='985'),'swing_right','totalMaximum'),(dict(totalBasis=None),'swing_right','totalBasis'),
        (dict(changeCount=-1),'swing_right','changeCount'),(dict(countRule=None),'swing_right','countRule'),
        (dict(totalMaximum='800'),'swing_feasibility','totalMaximum'),
        (dict(planningStep='0.000000000000000001'),'swing_numerical_scope','numericalPolicy')]:
        invalid=dict(body,operationId=str(uuid4()),swingRights=[dict(right,**edits)])
        refusal=client.call('gas/composition/book',invalid,expected=422)
        assert (refusal.get('code'),refusal.get('field'))==(code,field),(edits,refusal)
        staged_booking(query,manifest,body,invalid,code,fields=dict(field=field))
    assert booked['instrumentId']
    print('PASS native/composition/ticket field coordinates for period, plan, deadline and source selection; grid, width, count, basis, cap and fan-out refusals',flush=True)


def finalized_frame_booking(query,manifest,instrument,native,at,attest,before):
    from check_gas_exercise import stage_gas_composition
    # Observe the transaction's private evidence using only the disposable
    # fixture owner's read access. Finalization and commit run as the caller.
    finish="""SELECT security.finish_operation_read();
      SELECT security.check_operation_seals();
      SELECT security.finish_operation_read();
      SET LOCAL SESSION AUTHORIZATION DEFAULT;
      """
    live=f"""EXISTS(SELECT FROM security.gas_swing_booking_frames f
      JOIN security.request_contexts q ON q.id=f.request_id
      WHERE f.operation_id='{native['operationId']}' AND f.admitted
      AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
      AND clock_timestamp()<f.authority_until AND f.authority_until<q.expires_at)"""
    runtime='SET LOCAL SESSION AUTHORIZATION preview_runtime;'
    observed=stage_gas_composition(query,manifest,instrument,native,at,attest=attest,
        before_commit=finish+f'SELECT to_jsonb({live});'+runtime,commit=False)
    assert query('SELECT count(*) FROM trading.trades')==before
    assert observed is True, 'Finalization must retain the live original frame until commit'
    print('PASS repeated operation finalization retains the live frame and rolls back cleanly',flush=True)
    finalized=finish+f"""DO $test$ BEGIN IF NOT {live} THEN
      RAISE EXCEPTION 'missing live frame after finalization';END IF;END$test$;"""+runtime
    try:
        stage_gas_composition(query,manifest,instrument,native,at,
            attest=lambda token:attest(token,lifetime=4),before_commit="SELECT pg_sleep(4);")
    except RuntimeError as error:
        assert 'commit_gas_swing_booking_frame()' in str(error),error
    else:
        raise AssertionError('An expired frame admission committed.')
    assert query('SELECT count(*) FROM trading.trades')==before
    # The raw disposable connection ends with COMMIT immediately after this
    # check, just as the managed caller does. Its transport adds no finalizer.
    def raw(sql):
        return query.raw('\\set VERBOSITY verbose\n'+sql)
    cat=manifest['catalogId']
    current=f"""DO $test$ BEGIN
      IF security.trading_access('{cat}','BookTrade') IS NOT TRUE
        OR security.trading_access('{cat}','ReadTrades') IS NOT TRUE
        OR security.market_access('{cat}','ReadMarketData') IS NOT TRUE
      THEN RAISE EXCEPTION 'expected current publication authority';END IF;
      END$test$;"""
    try:
        stage_gas_composition(raw,manifest,instrument,native,at,
            attest=lambda token:attest(token,lifetime=4),before_commit=finalized+'SELECT pg_sleep(4);'+current)
    except RuntimeError as error:
        assert '42501:' in str(error) and 'commit_gas_swing_booking_frame()' in str(error),error
    else:
        raise AssertionError('Expected the original frame deadline to refuse commit after finalization.')
    assert query('SELECT count(*) FROM trading.trades')==before
    print('PASS expired original frame refused at commit after finalization with current publication authority and unchanged trade count',flush=True)
    receipt=stage_gas_composition(query,manifest,instrument,native,at,attest=attest,before_commit=finalized)
    assert query(f"SELECT count(*) FROM security.gas_swing_booking_frames WHERE operation_id='{native['operationId']}' AND admitted")=='0'
    return receipt


def session_frames(client,query,manifest,body,booked):
    import json
    from check_booked_side_programs import runtime_query
    from check_gas_exercise import stage_gas_composition
    cat=manifest['catalogId']
    from preview_client import PreviewClient,initial_frame
    workspace=client.call('workspace')
    frame=next(f for f in workspace['frames'] if f['id']==body['snapshotId'])
    observation=next(o for o in workspace['observations'] if o['id'] in frame['observationIds'] and o['role']=='daily_settlement')
    editor=PreviewClient(client.url);editor.bootstrap();editor.call('profile',dict(profile='editor'))
    corrected=editor.call('corrections',dict(snapshotId=frame['id'],observationId=observation['id'],value=observation['value']))
    working=client.call('select-observation',dict(snapshotId=frame['id'],observationId=corrected['observationId']))['frame']
    assert working['at']==frame['at'] and working['id']!=frame['id']
    command=dict(body,snapshotId=working['id'],operationId=str(uuid4()))
    assert client.call('gas/composition/book',command)['tradeId']
    native=dict(command,operationId=str(uuid4()))
    actor=runtime_query(query,manifest,f"SELECT security.booking_actor('{cat}')->>'id';").splitlines()[-1]
    def attest(token,lifetime=40,selected=native):
        wire=json.dumps(selected).replace("'","''")
        query(f"""SET SESSION AUTHORIZATION preview_authenticator;
          SELECT security.attest_gas_swing_booking_frame('{token}','{actor}','{cat}','{wire}'::jsonb,
            '{selected['snapshotId']}','{working['at']}',clock_timestamp()+interval '{lifetime} seconds')""")
    signature='security.attest_gas_swing_booking_frame(uuid,uuid,uuid,jsonb,uuid,timestamptz,timestamptz)'
    assert query(f"SELECT NOT has_function_privilege('preview_runtime','{signature}','EXECUTE') AND NOT has_table_privilege('preview_runtime','security.gas_swing_booking_frames','INSERT')")=='t'
    before=query('SELECT count(*) FROM trading.trades')
    durable=dict(body,operationId=str(uuid4()))
    def expire_selected_close(token):
        attest(token,lifetime=1,selected=durable)
        query("SELECT pg_sleep(1)")
        runtime_query(query,manifest,"SELECT true;",attest=attest)
        # The original token still exists. Issuing another frame must retain its
        # expired source bound rather than treating the recorded close as unbound.
        assert query(f"""SELECT count(*) FROM security.gas_swing_booking_frames f JOIN security.request_contexts q ON q.id=f.request_id
            WHERE q.id='{token}' AND f.authority_until<=clock_timestamp() AND q.expires_at>clock_timestamp()""")=='1', 'lost original source deadline'
    stage_gas_composition(query,manifest,booked['instrumentId'],durable,frame['at'],refusal='42501',attest=expire_selected_close)
    assert query('SELECT count(*) FROM trading.trades')==before
    print('PASS issuing another frame retains the original expired recorded-close bound and denies fresh publication on its still-current token',flush=True)
    for proposed,at,proof in [(native,working['at'],None),(native,'2020-12-18T10:00:00Z',attest),
                              (dict(native,operationId=str(uuid4())),working['at'],attest),
                              (dict(native,snapshotId=str(uuid4())),working['at'],attest)]:
        stage_gas_composition(query,manifest,booked['instrumentId'],proposed,at,refusal='swing_booking_frame',attest=proof)
    assert query('SELECT count(*) FROM trading.trades')==before
    receipt=finalized_frame_booking(query,manifest,booked['instrumentId'],native,working['at'],attest,before)
    wire=json.dumps(native).replace("'","''")
    replay=runtime_query(query,manifest,f"SELECT security.book_gas_composition('{cat}','{wire}'::jsonb,'{working['at']}');")
    assert json.loads(replay.splitlines()[-1])==receipt
    stage_gas_composition(query,manifest,booked['instrumentId'],dict(native,operationId=str(uuid4())),working['at'],refusal='swing_booking_frame')
    assert int(query('SELECT count(*) FROM trading.trades'))==int(before)+1
    client.call('market/context',dict(effectiveOn='2020-12-18',mode='current',selection='latest_correction'))
    try:
        historical=initial_frame(client.call('workspace'),'2020-12-18')
        assert historical['id']!=frame['id']
        refused=client.call('gas/composition/book',dict(command,operationId=str(uuid4()),snapshotId=historical['id']),expected=422)
        assert refused['code']=='historical_read_only'
    finally:
        client.call('market/current',{})
    live=client.call('market/live',dict(snapshotId=frame['id'],policy='selected_facts_latest_committed_corrections_v1'))
    try:
        refused=client.call('gas/composition/book',dict(command,operationId=str(uuid4()),snapshotId=live['frame']['id']),expected=422)
        assert refused['code']=='historical_read_only'
    finally:
        client.call('market/current',{})
    print('PASS working frame HTTP/native booking, scope, replay, mismatched clock and expiry at commit; historical/live selections stay read-only',flush=True)


def signed_parcel(client,query,manifest,context,base):
    import json
    from check_booked_side_programs import runtime_query
    from check_gas_exercise import stage_gas_composition
    body=january(base)
    body['physicalSides'][0].update(quantity='3',deliveryFrom='2021-01-15',deliveryTo='2021-01-16')
    body['cashSides'][0]['price']='4'
    term=body['swingRights'][0]
    for key in ('totalMaximum','totalBasis','changeCount','countRule'):term.pop(key)
    term.update(dailyMinimum='2',dailyMaximum='4',planningStep='1')
    receipt=client.call('gas/composition/book',body)
    native=stage_gas_composition(query,manifest,receipt['instrumentId'],dict(body,operationId=str(uuid4())),'2020-12-18T16:00:00Z')
    for trade in (receipt,native):
        ids={k:trade[k] for k in ('tradeId','instrumentId')}
        day='2021-01-15'
        command=dict(**ids,operationId=str(uuid4()),expectedRevision=1,right=0,expectedHeads=[dict(day=day,revisionId=None)],
            elections=[dict(day=day,quantity='2.5',exercisingParty='us',submittedAt='2020-12-18T16:00:00Z',
                effectiveAt='2020-12-18T16:00:00Z',evidence='Synthetic five-sixths parcel')])
        if trade is native:
            stored=runtime_query(query,manifest,f"SELECT security.record_gas_swing_elections('{manifest['catalogId']}','{json.dumps(command)}'::jsonb,'2020-12-18T16:00:00Z');")
            assert json.loads(stored.splitlines()[-1])['elections'][0]['quantity']=='2.5'
        else:client.call('gas/swing/record',dict(context=context,**command))
        value=client.call('gas/composition/editor',dict(context=context,tradeId=ids['tradeId']))['valuation']
        assert [member['presentValue'] for member in value['sides']]==['7.75','-10'] and value['presentValue']=='-2.25',value
    print('PASS HTTP and native election of 5/6 of B3: exact 7.75 physical, -10 fixed, signed held PV -2.25',flush=True)


def january_http(client, context, base):
    body = january(base)
    binding = client.call('gas/composition/book', body)
    ids = {key:binding[key] for key in ('tradeId','instrumentId')}
    read = lambda: client.call('gas/swing/read', dict(context=context,**ids))
    def heads():
        return [dict(day=h['day'], revisionId=h['revisionId']) for h in read()['heads']]
    def election(day, quantity):
        return dict(day=day, quantity=quantity, exercisingParty='us', evidence='Synthetic January acceptance',
                    submittedAt='2020-12-17T16:00:00Z', effectiveAt='2020-12-17T16:00:00Z')
    def command(rows):
        return dict(context=context, **ids, expectedRevision=1, operationId=str(uuid4()), right=0,
                    expectedHeads=heads(), elections=rows)
    first = command([election('2021-01-01','1050')])
    receipt = client.call('gas/swing/record',first)
    assert client.call('gas/swing/record',first)==receipt
    assert [e['quantity'] for e in read()['elections']]==['1050']
    before = heads()
    for edits,field in [(dict(quantity='1100.001'),'quantity'),(dict(exercisingParty='counterparty'),'exercisingParty'),
                        (dict(evidence='  '),'evidence'),(dict(submittedAt='2020-12-18T00:00:00Z'),'effectiveAt')]:
        invalid=command([dict(election('2021-01-04','1000'),**edits)])
        failed=client.call('gas/swing/record',invalid,expected=422)
        assert (failed['code'],failed['field'],failed['termRow'])==('swing_election',field,3),failed
    excess = command([election('2021-01-02','1100'),election('2021-01-03','1100')])
    assert client.call('gas/swing/record',excess,expected=422)['code']=='swing_feasibility'
    stale = dict(excess,operationId=str(uuid4()),expectedHeads=first['expectedHeads'],elections=[election('2021-01-04','1000')])
    assert client.call('gas/swing/record',stale,expected=409)['code']=='swing_election_conflict'
    assert heads()==before
    ticket = client.call('tickets/trade',dict(context=context,tradeId=ids['tradeId'],bindingId=ids['instrumentId'],revision=1))
    period, = ticket['actions']['recordSwing']['periods']
    assert (period['appliedUpper'],period['remainingUpper'],period['remainingMaximum'])==('31155','30105','30100')
    remaining = command([election(h['day'],'1000') for h in before[1:]])
    client.call('gas/swing/record',remaining)
    assert sum(int(e['quantity']) for e in read()['elections'])==31050
    print('PASS January HTTP off-grid late-booked acceptance, 31155 applied cap, 30105 arithmetic remaining, 30100 feasible maximum and 30000 remaining base schedule',flush=True)


def january_native(client, query, manifest, context, base):
    import json
    from check_booked_side_programs import runtime_query
    from check_gas_exercise import stage_gas_composition
    body = january(base)
    ordinary = {k:v for k,v in body.items() if k!='swingRights'}
    ordinary['operationId'] = str(uuid4())
    source = client.call('gas/composition/book',ordinary)
    trade = stage_gas_composition(query,manifest,source['instrumentId'],body,'2020-12-18T16:00:00Z')
    ids = {key:trade[key] for key in ('tradeId','instrumentId')}
    cat = manifest['catalogId']
    at = '2020-12-18T16:00:00Z'
    def read():
        sql = f"""SELECT security.use_market_context('{cat}',security.resolve_operation_context('{cat}','2020-12-18',NULL));
        SELECT security.use_trading_context('{cat}','{at}');SELECT security.read_gas_swing_elections('{cat}','{ids['tradeId']}','{ids['instrumentId']}')"""
        return json.loads(runtime_query(query,manifest,sql+';').splitlines()[-1])
    def command(elections):
        heads = [dict(day=h['day'], revisionId=h['revisionId']) for h in read()['heads']]
        return dict(**ids,expectedRevision=1,operationId=str(uuid4()),right=0,expectedHeads=heads,
                    elections=[dict(day=d,quantity=q,exercisingParty='us',submittedAt='2020-12-17T16:00:00Z',
                    effectiveAt='2020-12-17T16:00:00Z',evidence='Synthetic native acceptance') for d,q in elections])
    def record(command):
        wire=json.dumps(command).replace("'","''")
        return json.loads(runtime_query(query,manifest,f"SELECT security.record_gas_swing_elections('{cat}','{wire}'::jsonb,'{at}');").splitlines()[-1])
    first = command([('2021-01-01','1050')])
    assert record(first)==record(first)
    before=read()
    assert [e['quantity'] for e in before['elections']]==['1050']
    ticket=client.call('tickets/trade',dict(context=context,tradeId=ids['tradeId'],bindingId=ids['instrumentId'],revision=1))
    period,=ticket['actions']['recordSwing']['periods']
    assert (period['appliedUpper'],period['remainingUpper'],period['remainingMaximum'])==('31155','30105','30100')
    for test,error in [(command([('2021-01-02','1100'),('2021-01-03','1100')]),'swing_feasibility'),
                       (dict(first,operationId=str(uuid4())),'swing_election_conflict')]:
        try: record(test)
        except RuntimeError as failure:
            if error not in str(failure):raise
        else:raise AssertionError(f'native writer accepted {error}')
        assert read()['heads']==before['heads']
    record(command([(h['day'],'1000') for h in before['heads'][1:]]))
    assert sum(int(e['quantity']) for e in read()['elections'])==31050
    print('PASS authenticated native January booking, late off-grid election, replay, stale and over-budget refusal and all 30 remaining days at 1000',flush=True)


def earned_cash_and_amendment(client, query, manifest, context, base):
    import json
    from check_booked_side_programs import runtime_query
    from preview_fixture import CURRENCY
    trade=client.call('gas/composition/book',january(base))
    ids={key:trade[key] for key in ('tradeId','instrumentId')}
    cat=manifest['catalogId']
    at='2021-01-05T12:00:00Z'
    paid='2021-01-04T12:00:00Z'
    def payment(amount='-1',when=paid):
        return dict(operationId=str(uuid4()),tradeId=ids['tradeId'],expectedRevision=1,slot=1,
                    businessKey='synthetic-january-cash',paidAt=when,currency=CURRENCY,amount=amount,
                    allocations=[dict(occurrence=0,amount=amount)])
    def submit(p):
        from check_booked_side_programs import capture_context
        capture_context(query,manifest,'2021-01-05')
        return json.loads(runtime_query(query,manifest,
            f"SELECT security.record_gas_cash_payment('{cat}','{json.dumps(p)}'::jsonb,'{at}');").splitlines()[-1])
    def refused(p):
        try:submit(p)
        except RuntimeError as error:
            if 'gas_payment_occurrence' not in str(error):raise
        else:raise AssertionError('paid an occurrence without an elected day ended at paidAt')
    draft=payment()
    refused(draft)
    def elect(q):
        heads=client.call('gas/swing/read',dict(context=context,**ids))['heads']
        client.call('gas/swing/record',dict(context=context,**ids,expectedRevision=1,operationId=str(uuid4()),right=0,
            expectedHeads=[dict(day=h['day'],revisionId=h['revisionId']) for h in heads],
            elections=[dict(day='2021-01-02',quantity=q,exercisingParty='us',evidence='Synthetic earned cash',
                submittedAt='2020-12-18T16:00:00Z',effectiveAt='2020-12-18T16:00:00Z')]))
    elect('1000')
    refused(payment(when='2021-01-02T12:00:00Z'))
    accepted=submit(draft)
    elect(None)
    assert submit(draft)==accepted
    duplicate=submit(dict(draft,operationId=str(uuid4())))
    assert duplicate['paymentId']==accepted['paymentId']
    refused(payment('-2'))
    check=f"""SELECT security.gas_amendment_refusal('{cat}','{ids['tradeId']}',1,'{ids['instrumentId']}','2020-12-18T16:00:00Z');"""
    assert runtime_query(query,manifest,check).splitlines()[-1]=='gas_amendment_swing'
    print('PASS native cash admission requires an elected ended day in the paid occurrence; withdrawal preserves original and semantic replay, and bars amendment',flush=True)


def long_transport(client, context, base, book_native):
    from datetime import date,timedelta
    import json
    for days in (365,1200):
        body=january(base)
        source=body['physicalSides'][0]
        source.update(deliveryPoint='synthetic_swing_long_point',deliveryTo=(date(2021,1,1)+timedelta(days=days)).isoformat())
        body['cashSides'][0]['paymentAt']='2024-06-10T12:00:00Z'
        terms=body['swingRights'][0]
        for key in ('totalMaximum','totalBasis','changeCount','countRule'):terms.pop(key)
        if days==1200:
            # The full-head transport example needs one physical membership; the annual contract also carries its fixed payment.
            body['cashSides']=[]
            terms.update(entries=['gas'],changeCount=0,countRule='differs_from_base_v1')
        from time import monotonic
        started=monotonic()
        # The upstream program sealer can exceed the normal HTTP booking deadline with 1200 previously unseen days.
        # Stage and publish that fixture through the same authorized native writer before testing its HTTP transport.
        if days==1200:
            receipt=book_native(body)
            channel='authenticated native'
        else:
            receipt=client.call('gas/composition/book',body)
            channel='HTTP'
        print(f'PASS {days}-day {channel} booking in {monotonic()-started:.2f}s',flush=True)
        ids={key:receipt[key] for key in ('tradeId','instrumentId')}
        def read():
            return client.call('gas/swing/read',dict(context=context,**ids))
        dates=[h['day'] for h in read()['heads']]
        assert len(dates)==days
        evidence='測'*499+'🌱'
        def command(rows,view):
            return dict(context=context,**ids,right=0,expectedRevision=1,operationId=str(uuid4()),
                expectedHeads=[dict(day=h['day'],revisionId=h['revisionId']) for h in view['heads']],elections=rows)
        def row(index,proof):
            return dict(day=dates[index],exercisingParty='us',submittedAt='2020-12-17T16:00:00Z',
                effectiveAt='2020-12-17T16:00:00Z',evidence=proof,quantity=f'1000.{index+1:04d}' if days==365 else '1000')
        def send(draft):
            raw=json.dumps(draft,ensure_ascii=False,separators=(',',':')).encode('utf8')
            return client.call('gas/swing/record',raw),len(raw)
        original=command([row(i,'Synthetic annual instruction') for i in range(days)],read())
        outcome,length=send(original)
        assert len(outcome['elections'])==days
        if days==365:
            selected=read()['elections']
            assert [(e['day'],e['quantity'].rstrip('0').rstrip('.')) for e in selected]==[(r['day'],r['quantity'].rstrip('0').rstrip('.')) for r in original['elections']]
        opened=read()
        assert all(h['revisionId'] for h in opened['heads'])
        action=command([row(0,evidence)],opened)
        stale=command([row(days-1,'Synthetic distant correction')],opened)
        result,size=send(action)
        assert send(action)[0]==result
        assert client.call('gas/swing/record',stale,expected=409)['code']=='swing_election_conflict'
        assert read()['elections'][0]['evidence']==evidence
        print(f'PASS HTTP {days} atomic elections {length} UTF8 bytes, complete nonempty heads and a 500-scalar Unicode action {size} bytes',flush=True)
    before=read()
    excessive=command([row(0,'測'*(4*1024*1024//3+1))],before)
    raw=json.dumps(excessive,ensure_ascii=False).encode('utf8')
    # Kestrel may refuse the declared length while the client is still uploading.
    # A streaming HTTP/1.1 client can read that response even if the peer closes the write half.
    from http.client import HTTPConnection
    from urllib.parse import urlsplit
    from urllib.request import Request
    endpoint=client.url+client.api_prefix+'gas/swing/record'
    parts=urlsplit(endpoint)
    request=Request(endpoint)
    client.jar.add_cookie_header(request)
    connection=HTTPConnection(parts.hostname,parts.port,timeout=20)
    try:
        connection.putrequest('POST',parts.path)
        for key,value in {'Content-Type':'application/json','Content-Length':str(len(raw)),
                          'Cookie':request.get_header('Cookie'),'Origin':client.url,'X-Preview-CSRF':client.token}.items():
            connection.putheader(key,value)
        connection.endheaders()
        try:connection.send(raw)
        except (BrokenPipeError,ConnectionResetError):pass
        response=connection.getresponse()
        assert response.status==413,response.status
        refusal=json.loads(response.read())
    finally:
        connection.close()
    assert (refusal['code'],refusal['field'])==('swing_payload','elections')
    assert read()['heads']==before['heads']
    print('PASS HTTP oversized Unicode command returned 413 swing_payload on elections and preserved every original head',flush=True)
