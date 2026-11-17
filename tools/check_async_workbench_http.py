"""Shared asynchronous workbench reads in the launcher-owned synthetic database."""
import http.client
import json
import threading
import time
from urllib.parse import urlsplit
from uuid import uuid4
from assistant_context import holding
from preview_client import PreviewClient, initial_frame
from copy import deepcopy

PROTOCOL = 'async_reads_v1'


def financial_response(operation,value):
    """Graph execution counts measure this run; retained financial results do not."""
    if operation!='portfolio.risk.calculate.v1':return value
    copy=deepcopy(value)
    for work,key in [(copy['work'],'curveGraphEvaluatedNodes'),
                     *[(scenario['work'],'evaluatedNodes') for scenario in copy.get('connectedScenarios',[])]]:
        assert isinstance(work[key],int) and work[key]>=0, 'Invalid graph execution telemetry'
        del work[key]
    return copy


class Stream:
    def __init__(self, client, channel, headers=None):
        address = urlsplit(client.url)
        self.connection = http.client.HTTPConnection(address.hostname, address.port, timeout=15)
        self.connection.request('GET', client.api_prefix + 'async-reads/events?channelId=' + channel, headers={
            'Accept': 'text/event-stream', 'Sec-Fetch-Site': 'same-origin', 'Sec-Fetch-Mode': 'cors',
            'Cookie': '; '.join(f'{c.name}={c.value}' for c in client.jar), **(headers or {})})
        self.response = self.connection.getresponse()

    def event(self, kind):
        while True:
            fields = {}
            while True:
                line = self.response.readline()
                if not line:
                    return None
                line = line.decode().rstrip('\r\n')
                if not line:
                    break
                if not line.startswith(':'):
                    key, _, value = line.partition(':')
                    fields[key] = value.lstrip()
            if fields.get('event') == kind:
                return json.loads(fields['data'])

    def close(self):
        self.response.close()
        self.connection.close()



def run_checks(url, query, psql, environment, manifest):
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS async workbench: '+name, flush=True)

    c = PreviewClient(url); boot = c.bootstrap(); c.call('profile', {'profile': 'risk'})
    client_id=str(uuid4())
    opened = c.call('async-reads/channels', {'clientId':client_id})
    working=c.call('workspace/catalog')
    frame = initial_frame(working, '2020-12-18')
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
    blotter = c.call('trading/blotter', {'context': current, 'view': view})
    context = {**current, 'knowledgeMode': 'retained', 'contextId': blotter['context']['contextId']}
    scope = {'context': context, 'bookIds': [], 'portfolioIds': []}
    catalog = c.call('portfolio/risk/catalog', scope)
    factor = next(f for f in catalog['factors'] if 'future' in f['curves'])
    risk = {**scope, 'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
            'pricingPolicy': catalog['pricingPolicy'], 'detail': 'books', 'stress': [],
            'bumps': [{'factor': factor['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}]}
    command={'requestId':str(uuid4()),'request':risk}
    retained_job=c.call('portfolio/risk/jobs',command)
    replayed=c.call('portfolio/risk/jobs',command)
    check('ordinary retained job submission keeps its idempotent HTTP outcome',retained_job['id']==replayed['id'])
    held = next(h for h in blotter['holdings'] if h['instrumentType']=='future')
    swap = next(h for h in blotter['holdings'] if h['instrumentType']=='financial_swap')
    single = {**scope, 'bookIds':[held['bookId']], 'tradeIds':[held['tradeId']]}
    one = c.call('portfolio/risk/catalog', single)
    history_body = {'scope': single, 'pricingPolicy': one['pricingPolicy'],
        'policy': {**one['history']['profile'], 'minimumSamples': 2},
        'calendar': {'revision': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId']}, 'code': 'weekdays_utc'},
        'from':'2020-12-15','to':'2020-12-19','closeTime':'16:00:00','ambiguousTime':'reject','invalidTime':'reject'}
    history = c.call('portfolio/risk/history', history_body)
    curve = {'snapshotId':frame['id'], 'curve':'local_power', 'includeSources':True, 'includeWorkspace':True,
             'contractDisplay':{'version':'curve_display_v1','contracts':'eligible_contracts_v1'}}
    curve_points=c.call('evaluate',curve)['points']
    cases = [
        ('curve.evaluate.v1','evaluate',curve),
        ('curve.history.v1','history',{'snapshotId':frame['id'],'curve':'local_power','contractId':curve_points[0]['contractId'],'revisionPolicy':'exact_snapshot_pins_v1'}),
        ('curve.shapes.v1','market/curve-shapes',{'snapshotId':frame['id'],'curves':['future','local_power'],'contractDisplay':curve['contractDisplay']}),
        ('trading.directory.v1','trading/directory',context),
        ('trading.positions.v1','trading/positions',{'context':context,'bookIds':[],'portfolioIds':[],'offset':0,'limit':64}),
        ('futures.editor.v1','futures/editor',{'context':{**context,'convention':working['futuresConvention']},'tradeId':held['tradeId'],'revision':int(held['revision'])}),
        ('swaps.editor.v1','swaps/editor',{'context':{**context,'convention':'independent_side_financial_swap_v2'},'tradeId':swap['tradeId'],'revision':int(swap['revision'])}),
        ('trading.workspace.v1','trading/workspace',{'context':context,'view':view,'summaries':[],'bookingCatalog':'directory'}),
        ('trading.blotter.v1','trading/blotter',{'context':context,'view':view}),
        ('portfolio.risk.catalog.v1','portfolio/risk/catalog',scope),
        ('portfolio.risk.calculate.v1','portfolio/risk',risk),
        ('portfolio.risk.jobs.list.v1','portfolio/risk/jobs',None),
        ('portfolio.risk.jobs.read.v1','portfolio/risk/jobs/'+retained_job['id'],None),
        ('portfolio.risk.history.v1','portfolio/risk/history',history['request']),
        ('portfolio.risk.archive.v1','portfolio/risk/statistical/archive?limit=10',None),
    ]
    expected = {operation:c.call(path,body) for operation,path,body in cases}
    check('channel states protocol and exact selected owner/context', opened['protocol']==PROTOCOL and opened['profile']=='risk' and opened['context'] is None)
    channel = opened['channelId']; stream = Stream(c,channel)
    try:
        check('SSE ready precedes read submissions',stream.response.status==200 and stream.event('ready')=={'protocol':PROTOCOL,'channelId':channel})
        def submit(operation,body,channel_id=None):
            request={'channelId':channel_id or channel,'requestId':str(uuid4()),'operation':operation,'body':body or {}}
            ack=c.call('async-reads',request,expected=202,timeout=5)
            assert ack=={'channelId':request['channelId'],'requestId':request['requestId'],'state':'accepted'}
            return request
        cancelled={'channelId':channel,'requestId':str(uuid4()),'operation':'curve.evaluate.v1','body':curve}
        cancellation={key:cancelled[key] for key in ('channelId','requestId')}
        receipt=c.call('async-reads/cancel',cancellation)
        check('cancellation before admission fences the exact request identity',
              receipt=={**cancellation,'state':'not_admitted'} and c.call('async-reads/cancel',cancellation)==receipt)
        refused=c.call('async-reads',cancelled,expected=409)
        independent=submit('curve.evaluate.v1',curve);completed=stream.event('response')
        check('delayed cancelled submissions cannot execute or interrupt independent reads',refused['code']=='async_request_cancelled' and
              completed['requestId']==independent['requestId'] and completed['state']=='completed' and completed['result']==expected['curve.evaluate.v1'])
        failures=[]
        for operation,path,body in cases:
            if operation=='portfolio.risk.archive.v1':body={'method':'statistical','limit':10,'before':None,'id':None}
            if operation=='portfolio.risk.jobs.read.v1':body={'id':retained_job['id']}
            request=submit(operation,body); reply=stream.event('response')
            assert reply is not None,path+': response stream ended'
            if reply.get('state')!='completed':
                failures.append(f"{path}: terminal response {reply.get('state')}: {reply.get('error')}")
                continue
            def differences(left,right,path='result'):
                if type(left) is not type(right):return [path+': type differs']
                if isinstance(left,dict):
                    if left.keys()!=right.keys():return [path+': keys differ '+str(sorted(left.keys()^right.keys()))]
                    return [item for key in left for item in differences(left[key],right[key],path+'.'+key)][:8]
                if isinstance(left,list):
                    if len(left)!=len(right):return [path+': length differs']
                    return [item for i,(a,b) in enumerate(zip(left,right)) for item in differences(a,b,path+f'[{i}]')][:8]
                return [] if left==right else [path+': values differ']
            actual=financial_response(operation,reply['result']); wanted=financial_response(operation,expected[operation])
            if actual!=wanted:
                failures.append(path+': '+', '.join(differences(actual,wanted)))
                continue
            check('HTTP/SSE financial and response-contract parity for '+path,reply['requestId']==request['requestId'] and reply['operation']==operation and
                  reply['state']=='completed' and actual==wanted)
        assert not failures,'; '.join(failures)
        def waiting(count):
            deadline=time.monotonic()+6
            while int(query("SELECT count(DISTINCT pid) FROM pg_locks WHERE relation='market_data.observations'::regclass AND NOT granted AND mode='AccessShareLock';"))!=count:
                assert time.monotonic()<deadline, 'Expected independent observation readers'
                time.sleep(.025)
        def blocked():
            requests=[submit('curve.evaluate.v1',curve),submit('portfolio.risk.calculate.v1',risk)]
            waiting(2);c.call('access',timeout=5)
            check('curve and Risk execute concurrently without blocking session navigation')
            c.call('async-reads',requests[0],expected=202,timeout=5)
            waiting(2)
            c.call('async-reads',{**requests[0],'body':{**curve,'curve':'future'}},expected=409)
            c.call('async-reads',{'channelId':channel,'requestId':str(uuid4()),'operation':'curve.evaluate.v1','body':curve},expected=429)
            check('duplicate work, conflicting identities and excess active work stay bounded')
            return requests
        requests=holding(psql,environment,'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE',blocked)
        replies={r['requestId']:r for r in [stream.event('response'),stream.event('response')]}
        check('mixed concurrent work returns only its own exact result',all(financial_response(r['operation'],replies[r['requestId']]['result'])==financial_response(r['operation'],expected[r['operation']]) for r in requests))
        other=c.call('async-reads/channels',{'clientId':str(uuid4())})['channelId']
        other_stream=Stream(c,other)
        try:
            assert other_stream.event('ready') is not None
            def two_tabs():
                first=submit('curve.evaluate.v1',curve)
                second=submit('curve.evaluate.v1',curve,other)
                waiting(2)
                return first,second
            first,second=holding(psql,environment,'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE',two_tabs)
            a,b=stream.event('response'),other_stream.event('response')
            check('separate workbench tabs share an account without cancelling each other',a['state']==b['state']=='completed' and a['requestId']==first['requestId'] and b['requestId']==second['requestId'])
            replacement=c.call('async-reads/channels',{'clientId':client_id})['channelId']
            assert stream.event('response') is None
            stream.close();channel=replacement;stream=Stream(c,channel)
            check('explicit retry replaces only its own tab response channel',stream.event('ready') is not None and other_stream.response.status==200)
            third=submit('curve.evaluate.v1',curve,other)
            check('another tab still completes after its neighbour reconnects',other_stream.event('response')['requestId']==third['requestId'])
        finally:
            other_stream.close()
        for operation in ['futures.book','portfolio.risk.jobs.submit.v1','arbitrary.route.v1']:
            c.call('async-reads',{'channelId':channel,'requestId':str(uuid4()),'operation':operation,'body':{}},expected=422)
        check('writes and unknown operations cannot enter the read channel')
        def revoked():
            request=submit('portfolio.risk.calculate.v1',risk);waiting(1)
            c.call('profile',{'profile':'viewer'},timeout=5)
            return request
        holding(psql,environment,'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE',revoked)
        terminal=stream.event('response')
        check('account changes during Risk execution prevent former-account result delivery',terminal is None or terminal['state']!='completed')
    finally:
        stream.close()
    c.call('profile',{'profile':'risk'})
    opened=c.call('async-reads/channels',{'clientId':client_id});channel=opened['channelId'];stream=Stream(c,channel)
    assert stream.event('ready') is not None
    principal="(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk')"
    saved=json.loads(query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to)) FROM security.role_grants WHERE principal_id={principal};"))
    try:
        def expires():
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id={principal};")
            submit('portfolio.risk.calculate.v1',risk);waiting(1)
            deadline=time.monotonic()+5
            while query(f"SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id={principal};")!='t':
                assert time.monotonic()<deadline,'The owned Risk role did not expire'
                time.sleep(.025)
        holding(psql,environment,'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE',expires)
        terminal=stream.event('response')
        check('Risk authority expiring during execution prevents result disclosure',terminal is None or terminal['state']!='completed')
    finally:
        stream.close()
        for grant in saved:
            query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")
    # A read of working data captures current knowledge under the publication gate. It must not keep that gate while
    # it values: every other read of working data, in any session, would wait behind it and fail at its command timeout.
    other=PreviewClient(url);other.bootstrap();other.call('profile',{'profile':'risk'})
    other_frame=initial_frame(other.call('workspace/catalog'),'2020-12-18')
    def at_work(selected,convention='trading_workspace_v1'):
        return {'snapshotId':selected['id'],'convention':convention,'knowledgeMode':'current'}
    def scope_at_work(selected):
        return {'context':at_work(selected),'bookIds':[],'portfolioIds':[]}
    observation_waiters="SELECT count(DISTINCT pid) FROM pg_locks WHERE relation='market_data.observations'::regclass AND NOT granted AND mode='AccessShareLock';"
    publication_waiters="SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid=730849102 AND NOT granted;"
    def reading(client,path,body):
        result={}
        def work():
            try:result['value']=client.call(path,body)
            except Exception as error:result['error']=repr(error)[:200]
        thread=threading.Thread(target=work);thread.start()
        return thread,result
    def valuing_together(path,body):
        def held():
            first=reading(c,path,body);waiting(1)
            second=reading(other,'portfolio/risk/catalog',scope_at_work(other_frame))
            deadline=time.monotonic()+4
            while int(query(observation_waiters))!=2:
                assert time.monotonic()<deadline,f'behind {path}, a read of working data waits for the publication gate ({query(publication_waiters)} waiting): first {first[1]}, second {second[1]}'
                time.sleep(.025)
            assert query(publication_waiters)=='0',f'a read of working data waits for the publication gate behind {path}'
            return first,second
        first,second=holding(psql,environment,'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE',held)
        for thread,_ in (first,second):thread.join(timeout=20)
        assert all('value' in result for _,result in (first,second)),f'{path}: {first[1].get("error")} {second[1].get("error")}'
        return first[1]['value'],second[1]['value']
    one,two=valuing_together('portfolio/risk/catalog',scope_at_work(frame))
    check('a read of working data does not hold the publication gate while it values')
    # A ticket read also reads its trade's current head, under the same gate, before it values.
    ticket,_=valuing_together('futures/editor',{'context':at_work(frame,working['futuresConvention']),'tradeId':held['tradeId'],'revision':int(held['revision'])})
    check('a trade ticket read does not hold the publication gate while it values',ticket['canAmend'] is True or ticket.get('amendUnavailableReason'))
    # A P&L run captures the knowledge cutoff of both its dates under the same gate, then values both.
    policies=c.call('portfolio/pnl/policies')
    prior=max((f for f in working['frames'] if f['id'] in working['initialFrameIds'] and f['at']<frame['at']),key=lambda f:f['at'])
    def dated(selected):
        return {'snapshotId':selected['id'],'convention':'trading_workspace_v1','knowledgeMode':'at'}
    pnl={'opening':dated(prior),'closing':dated(frame),'knownAt':blotter['context']['knownAt'],'bookIds':[held['bookId']],'portfolioIds':[],
         'tradeIds':[held['tradeId']],'accountingPolicy':policies['accounting'],'attributionPolicy':policies['attribution'],'factorPolicy':policies['factors'],
         'factorBump':policies['factorBump']['offered'],'time':{'mode':'opening_to_closing_clock_v1','policy':policies['clockPolicy'],'clock':None}}
    run,_=valuing_together('portfolio/pnl/run',pnl)
    check('a P&L run does not hold the publication gate while it values',run['report']['positions'] and run['saveRefusal'] is None)
    # The ordinary routes of the covered reads execute outside the session gate (docs/async-reads.md).
    LOCK='LOCK market_data.observations IN ACCESS EXCLUSIVE MODE'
    def tab():
        """Another connection of the same session, so requests overlap as a page's do."""
        other=PreviewClient(url);other.session=c.session;other.token=c.token
        for cookie in c.jar:other.jar.set_cookie(cookie)
        return other
    def background(path,body,expected=200):
        result={}
        def work():
            try:result['value']=tab().call(path,body,expected=expected)
            except Exception as error:result['error']=repr(error)[:300]
            result['at']=time.monotonic()
        thread=threading.Thread(target=work);thread.start()
        return thread,result
    def beside():
        thread,held=background('portfolio/risk',risk);waiting(1)
        started=time.monotonic();c.call('access',timeout=5)
        return thread,held,time.monotonic()-started
    thread,held,elapsed=holding(psql,environment,LOCK,beside);thread.join(timeout=20)
    check('an ordinary request proceeds beside an ordinary read that waits in the database',elapsed<2 and
          financial_response('portfolio.risk.calculate.v1',held.get('value'))==financial_response('portfolio.risk.calculate.v1',expected['portfolio.risk.calculate.v1']))
    def three():
        reads=[background('evaluate',curve) for _ in range(3)];waiting(2);time.sleep(.3);waiting(2)
        return reads
    reads=holding(psql,environment,LOCK,three)
    for thread,_ in reads:thread.join(timeout=20)
    check('a third ordinary read of one session waits for a slot, then completes',all(read.get('value')==expected['curve.evaluate.v1'] for _,read in reads))
    def switched():
        thread,held=background('portfolio/risk',risk,expected=403);waiting(1)
        c.call('profile',{'profile':'viewer'},timeout=5)
        return thread,held
    thread,held=holding(psql,environment,LOCK,switched);thread.join(timeout=20)
    check('a profile switch during an ordinary read refuses its answer',(held.get('value') or {}).get('code')=='access_denied')
    c.call('profile',{'profile':'risk'})
    saved=json.loads(query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to)) FROM security.role_grants WHERE principal_id={principal};"))
    try:
        def expires_ordinary():
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id={principal};")
            thread,held=background('portfolio/risk',risk,expected=403);waiting(1)
            deadline=time.monotonic()+5
            while query(f"SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id={principal};")!='t':
                assert time.monotonic()<deadline,'The owned Risk role did not expire'
                time.sleep(.025)
            return thread,held
        thread,held=holding(psql,environment,LOCK,expires_ordinary);thread.join(timeout=20)
        check('Risk authority expiring during an ordinary read prevents its answer',(held.get('value') or {}).get('code')=='access_denied')
    finally:
        for grant in saved:
            query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")
    # An ordinary read's answer waits for the session gate. Authority that ends in that wait, after the read's own
    # transaction has finished with it intact, must still refuse the answer. A gated read of the same session holds
    # the gate on a second lock meanwhile. A role change waits for admitted transactions, so the role ends by its own
    # term, set before either request is admitted and inside the five seconds the gate holder's command may wait.
    GATE='LOCK presentation.dashboard_layouts IN ACCESS EXCLUSIVE MODE'
    gate_waiters="SELECT count(*) FROM pg_locks WHERE relation='presentation.dashboard_layouts'::regclass AND NOT granted;"
    role_ended=f"SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id={principal};"
    def until(sql,value,message,seconds=4):
        deadline=time.monotonic()+seconds
        while query(sql)!=value:
            assert time.monotonic()<deadline,message
            time.sleep(.025)
    saved=json.loads(query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to)) FROM security.role_grants WHERE principal_id={principal};"))
    try:
        def answering():
            def reading():
                query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '3 seconds' WHERE principal_id={principal};")
                thread,held=background('evaluate',curve,expected=403);waiting(1)
                gated,_=background('dashboard/layout',None)
                until(gate_waiters,'1','The gated request did not hold the session gate')
                return thread,held,gated
            thread,held,gated=holding(psql,environment,LOCK,reading)
            # Only the gated request still works in the database once the read's own transaction has committed.
            until("SELECT count(*) FROM pg_stat_activity WHERE usename='preview_runtime' AND state<>'idle';",'1','The read did not finish its own transaction')
            assert query(role_ended)=='f' and 'at' not in held,f'The read must finish its transaction with its authority intact and then wait for the gate: {held}'
            until(role_ended,'t','The owned Risk role did not expire')
            assert query(gate_waiters)=='1','The gated request released the session gate before the role ended'
            return thread,held,gated
        thread,held,gated=holding(psql,environment,GATE,answering);thread.join(timeout=20);gated.join(timeout=20)
        assert (held.get('value') or {}).get('code')=='access_denied',f'the waiting read answered {held}'
        check('authority that ends while an ordinary read waits to answer refuses it')
    finally:
        for grant in saved:
            query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")
    print(f'{passed} asynchronous workbench HTTP checks passed.',flush=True)
