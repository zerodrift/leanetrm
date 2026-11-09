"""Shared contract-date library checks in a newly owned synthetic preview."""
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor
import json
import re
import subprocess
import time
from preview_client import PreviewClient


def run_checks(url, query, manifest, psql, environment):
    def client(profile):
        value = PreviewClient(url); value.bootstrap(); value.call('profile', {'profile': profile}); return value
    editor, approver, viewer = (client(p) for p in ('editor', 'approver', 'viewer'))
    calendar = next(c['reference'] for c in editor.call('date-schedules')['calendars'] if c['reference']['code'] == 'test_business')
    definition = {'name':'Synthetic delivery boundaries','symbol':'oe.shared_dates','firstMonth':'2021-01','lastMonth':'2021-05',
                  'anchor':'day','day':1,'shortMonth':'reject','adjustment':'unadjusted','calendar':None}
    schedule = editor.call('date-schedules/save', {'definition':definition,'calendar':calendar,'selection':'context_month',
                                                 'effectiveFrom':'2021-01-01','effectiveTo':'2021-05-01'})
    schedule = approver.call('date-schedules/publish', {k:schedule[k] for k in ('id','version','generation')})
    def context(day='2021-01-15'):
        return viewer.call('date-schedules/context', {'effectiveOn':day,'knownAt':None,'mode':'current'})['context']
    uploaded={'name':'Imported explicit dates','symbol':'oe.imported_days','dates':['2021-02-03','2021-01-19','2021-01-19'],
        'calendar':calendar,'selection':'on_or_after','effectiveFrom':'2021-01-01','effectiveTo':'2022-01-01'}
    viewer.call('date-schedules/import',uploaded,expected=403)
    imported=editor.call('date-schedules/import',uploaded)
    assert imported==editor.call('date-schedules/import',uploaded), 'Repeat import changed the draft'
    imported=approver.call('date-schedules/publish',{k:imported[k] for k in ('id','version','generation')})
    assert imported==editor.call('date-schedules/import',uploaded), 'Repeat import forked a publication'
    imported_context=context()
    assert viewer.call('date-schedules/resolve',{'expression':'oe.imported_days[0]','context':imported_context})['date']=='2021-01-19'
    extended=editor.call('date-schedules/import',{**uploaded,'dates':['2021-03-01','2021-01-19']})
    assert extended['version']==2 and extended['definition']['dates']==['2021-01-19','2021-02-03','2021-03-01']
    assert extended==editor.call('date-schedules/import',{**uploaded,'dates':['2021-03-01']})
    editor.call('date-schedules/import',{**uploaded,'selection':'absolute_index'},expected=409)
    plain={**uploaded,'name':'Plain date list','symbol':'oe.plain_dates','calendar':None}
    plain_draft=editor.call('date-schedules/import',plain)
    assert plain_draft['engineVersion']=='date_schedule_v3' and plain_draft['calendar'] is None
    plain_saved=approver.call('date-schedules/publish',{k:plain_draft[k] for k in ('id','version','generation')})
    assert editor.call('date-schedules/import',plain)==plain_saved
    assert viewer.call('date-schedules/resolve',{'expression':'oe.plain_dates[0]','context':context()})['date']=='2021-01-19'
    viewer.call('date-schedules/resolve',{'expression':'oe.plain_dates[0] + 1bd','context':context()},expected=422)
    # A daily rule stores no dates; each context selects its own first day, and +Nbd counts on the bound calendar.
    daily={'definition':{'name':'Synthetic calendar days','symbol':'oe.days','daily':{'days':'calendar','firstDate':'2021-01-01','lastDate':'2021-12-31'}},
        'calendar':calendar,'selection':'on_or_after','effectiveFrom':'2021-01-01','effectiveTo':'2022-01-01'}
    viewer.call('date-schedules/save',daily,expected=403)
    editor.call('date-schedules/save',{**daily,'selection':'context_month'},expected=422)
    days=editor.call('date-schedules/save',daily)
    assert days['engineVersion']=='date_schedule_v4' and 'dates' not in days['definition'], days
    approver.call('date-schedules/publish',{k:days[k] for k in ('id','version','generation')})
    resolve=lambda expression,day:viewer.call('date-schedules/resolve',{'expression':expression,'context':context(day)})['date']
    assert (resolve('oe.days[1]','2021-01-15'),resolve('oe.days[1]','2021-01-16'))==('2021-01-16','2021-01-17')
    assert resolve('oe.days + 1bd','2021-01-15')!='2021-01-16', 'A business-day offset landed on a Saturday'
    before = context()
    body = {'requestId':str(uuid4()),'code':'shared_delivery','name':'Synthetic monthly delivery dates','context':before,
            'contracts':[{'label':'Month one','start':'oe.shared_dates[0]','end':'oe.shared_dates[1]',
                          'lastTrading':'oe.shared_dates[0] - 3bd','cutoffUtc':'15:30'}]}
    viewer.call('date-schedules/contracts/save', body, expected=403)
    receipt = editor.call('date-schedules/contracts/save', body)
    assert receipt == editor.call('date-schedules/contracts/save', body)
    assert receipt['state'] == 'draft' and receipt['version'] == 1
    for invalid in [{**body,'requestId':str(uuid4()),'context':None},
                    {**body,'requestId':str(uuid4()),'context':{**before,'contextId':str(uuid4())}},
                    {**body,'requestId':str(uuid4()),'contracts':None}]:
        editor.call('date-schedules/contracts/save', invalid, expected=422)
    def version(who=editor, version=1):
        return next(v for v in who.call('date-schedules')['contractDefinitions'] if v['id']==receipt['id'] and v['version']==version)
    draft = version(approver)
    assert draft['definition']['context'] == before and draft['bindings'][0]['scheduleVersion'] == 1
    assert not any(v['id']==receipt['id'] for v in viewer.call('date-schedules')['contractDefinitions'])
    changed = {**body,'name':'Changed replay'}
    editor.call('date-schedules/contracts/save', changed, expected=409)
    publish = {'requestId':str(uuid4()),'id':receipt['id'],'version':1,'generation':receipt['generation']}
    editor.call('date-schedules/contracts/publish', publish, expected=403)
    published = approver.call('date-schedules/contracts/publish', publish)
    assert published == approver.call('date-schedules/contracts/publish', publish)
    assert version(viewer)['state'] == 'published'
    resolution = {'id':receipt['id'],'version':1,'context':context()}
    first = viewer.call('date-schedules/contracts/resolve', resolution)
    assert (first['definitionId'],first['version'],first['engineVersion']) == (receipt['id'],1,'contract_dates_v1')
    assert first['contracts'][0]['start']=='2021-01-01' and first['contracts'][0]['lastTradingAt']=='2020-12-29T15:30:00+00:00'
    late = {**body,'requestId':str(uuid4()),'code':'shared_later_cutoffs','name':'Synthetic daily delivery dates',
            'context':context(),'contracts':[
                {'label':'First day','start':'2021-01-05','end':'2021-01-06','lastTrading':'2021-01-06','cutoffUtc':'04:00'},
                {'label':'Second day','start':'2021-01-06','end':'2021-01-07','lastTrading':'2021-01-08','cutoffUtc':'04:00'}]}
    late_receipt=editor.call('date-schedules/contracts/save',late)
    approver.call('date-schedules/contracts/publish',{'requestId':str(uuid4()),**{key:late_receipt[key] for key in ('id','version','generation')}})
    late_rows=viewer.call('date-schedules/contracts/resolve',{'id':late_receipt['id'],'version':late_receipt['version'],'context':context()})['contracts']
    assert [row['lastTradingAt'] for row in late_rows]==['2021-01-06T04:00:00+00:00','2021-01-08T04:00:00+00:00']
    # A date list is one labelled date per row over the daily rule; it rolls with the context and is never read as contracts.
    list_before=context()
    tenors={'requestId':str(uuid4()),'code':'synthetic_tenors','name':'Synthetic tenors','context':list_before,'contracts':None,
        'dates':[{'label':'1D','date':'oe.days + 1bd'},{'label':'6M','date':'oe.days[181]'}]}
    preview=viewer.call('date-schedules/date-lists/preview',{key:tenors[key] for key in ('code','name','context','contracts','dates')})
    assert [d['label'] for d in preview['dates']]==['1D','6M'] and preview['dates'][1]['date']=='2021-07-15', preview
    viewer.call('date-schedules/contracts/save',tenors,expected=403)
    editor.call('date-schedules/contracts/save',{**tenors,'requestId':str(uuid4()),'dates':[{'label':'A','date':'oe.days[2]'},{'label':'B','date':'oe.days[1]'}]},expected=422)
    tenor_receipt=editor.call('date-schedules/contracts/save',tenors)
    assert tenor_receipt==editor.call('date-schedules/contracts/save',tenors), 'A retried date-list save changed its receipt'
    assert sum(v['id']==tenor_receipt['id'] for v in approver.call('date-schedules')['contractDefinitions'])==1
    approver.call('date-schedules/contracts/publish',{'requestId':str(uuid4()),**{key:tenor_receipt[key] for key in ('id','version','generation')}})
    tenor_read={'id':tenor_receipt['id'],'version':tenor_receipt['version']}
    rolled=[viewer.call('date-schedules/date-lists/resolve',{**tenor_read,'context':context(day)})['dates'] for day in ('2021-01-15','2021-02-15')]
    assert [d['date'] for d in rolled[1]][1]=='2021-08-15' and rolled[0][1]['date']=='2021-07-15', rolled
    viewer.call('date-schedules/contracts/resolve',{**tenor_read,'context':context()},expected=422)
    viewer.call('date-schedules/date-lists/resolve',{**resolution,'context':context()},expected=422)
    viewer.call('date-schedules/date-lists/resolve',{**tenor_read,'context':list_before},expected=422)
    # A thirty-year calendar-day tenor is stored as its occurrence, beyond four digits.
    long_days=editor.call('date-schedules/save',{'definition':{'name':'Synthetic long days','symbol':'oe.long_days','daily':{'days':'calendar','firstDate':'2021-01-01','lastDate':'2060-12-31'}},
        'calendar':None,'selection':'on_or_after','effectiveFrom':'2021-01-01','effectiveTo':'2022-01-01'})
    approver.call('date-schedules/publish',{k:long_days[k] for k in ('id','version','generation')})
    editor.call('date-schedules/contracts/save',{'requestId':str(uuid4()),'code':'long_tenors','name':'Synthetic long tenors','context':context(),'contracts':None,
        'dates':[{'label':'30Y','date':'oe.long_days[10957]'}]})
    later = viewer.call('date-schedules/contracts/resolve', {**resolution,'context':context('2021-02-15')})
    assert later['contracts'][0]['start']=='2021-02-01'
    # A new publication of the symbolic source must not reinterpret this definition.
    revised = editor.call('date-schedules/save', {'id':schedule['id'],'version':1,'generation':schedule['generation'],'expectedState':'published',
        'definition':{**definition,'day':2},'calendar':calendar,'selection':'context_month','effectiveFrom':'2021-01-01','effectiveTo':'2021-05-01'})
    approver.call('date-schedules/publish', {k:revised[k] for k in ('id','version','generation')})
    pinned = viewer.call('date-schedules/contracts/resolve', {**resolution,'context':context()})['contracts'][0]
    assert (pinned['start'],pinned['end'],pinned['lastTradingAt']) == ('2021-01-01','2021-02-01','2020-12-29T15:30:00+00:00')
    assert pinned['startReference']['source']['version'] == 1
    # The publication is invisible at the earlier certified cutoff even through exact-ID reads.
    viewer.call('date-schedules/contracts/resolve', {**resolution,'context':before}, expected=422)
    viewer.call('date-schedules/contracts/resolve', {**resolution,'context':context('2021-06-15')}, expected=422)
    fork = {**body,'requestId':str(uuid4()),'id':receipt['id'],'version':1,'generation':receipt['generation'],'expectedState':'published','name':'Reviewed next draft'}
    next_receipt = editor.call('date-schedules/contracts/save', fork)
    assert next_receipt['version']==2 and version(viewer)['definition']['name']==body['name']
    editor.call('date-schedules/contracts/save', {**fork,'requestId':str(uuid4())}, expected=409)
    catalog=manifest['catalogId']; definition_id=receipt['id']
    old=version(editor,2)
    locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    with ThreadPoolExecutor(max_workers=1) as pool:
        try:
            locker.stdin.write('BEGIN;LOCK TABLE market_data.contract_date_contracts IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
            while locker.stdout.readline().strip()!='LOCKED':
                if locker.poll() is not None:raise AssertionError(locker.stderr.read())
            reading=pool.submit(editor.call,'date-schedules')
            blocked=False
            for _ in range(100):
                if query("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%market_data.contract_date_%';")=='1':blocked=True;break
                time.sleep(.02)
            assert blocked,'Library read did not reach its controlled storage wait'
            locker.stdin.write(f"UPDATE market_data.contract_date_versions SET name='Concurrent header',generation=generation+1 WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2;UPDATE market_data.contract_date_contracts SET label='Concurrent row' WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2;COMMIT;\n\\q\n");locker.stdin.flush()
            _,errors=locker.communicate(timeout=10);assert locker.returncode==0,errors
            read=next(v for v in reading.result(timeout=20)['contractDefinitions'] if v['id']==definition_id and v['version']==2)
            observed=(read['generation'],read['definition']['name'],read['definition']['contracts'][0]['label'])
            assert observed in [(old['generation'],old['definition']['name'],old['definition']['contracts'][0]['label']),
                                (old['generation']+1,'Concurrent header','Concurrent row')], 'Library mixed definition snapshots: '+repr(observed)
        finally:
            if locker.poll() is None:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
    # Each statement below is the one the API sent, captured behind a controlled lock and planned for the
    # same reader: row security is paid for what the operation asked about, once per version it visits.
    def captured(who, path, *request, expected=200):
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        with ThreadPoolExecutor(max_workers=1) as pool:
            try:
                locker.stdin.write('BEGIN;LOCK TABLE market_data.contract_date_contracts IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
                while locker.stdout.readline().strip()!='LOCKED':
                    if locker.poll() is not None:raise AssertionError(locker.stderr.read())
                reading=pool.submit(who.call,path,*request,expected=expected)
                statement=''
                for _ in range(500):
                    statement=query("SELECT query FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%market_data.contract_date_contracts%';")
                    if statement:break
                    time.sleep(.02)
                assert statement,'The read did not reach its controlled storage wait'
            finally:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
            reading.result(timeout=20)
        return statement
    # A sequential scan would count every row after an ANALYZE, whatever the statement's scope.
    def issued(profile):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    def binding(statement, *values):
        return re.sub(r'\$(\d+)',lambda m:f"'{values[int(m[1])-1]}'",statement)
    def visited(statement, profile, *values):
        token=issued(profile)
        bound=binding(statement,*values)
        output=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SET LOCAL enable_seqscan=off;EXPLAIN(ANALYZE,BUFFERS,FORMAT JSON) {bound};ROLLBACK;")
        rows={}
        def walk(node):
            if 'Relation Name' in node:
                rows[node['Relation Name']]=rows.get(node['Relation Name'],0)+round((node['Actual Rows']+node.get('Rows Removed by Filter',0))*node['Actual Loops'])
            for child in node.get('Plans',()):walk(child)
        walk(json.loads(output[output.index('['):])[0]['Plan'])
        return rows
    versions_held=int(query(f"SELECT count(*) FROM market_data.contract_date_versions WHERE catalog_id='{catalog}';"))
    exact=visited(captured(viewer,'date-schedules/contracts/resolve',{**resolution,'context':context()}),'viewer',catalog,definition_id,1)
    assert (exact.get('contract_date_contracts'),exact.get('contract_date_fields'))==(1,3),'An exact-version resolve read other versions: '+repr(exact)
    stale={**fork,'requestId':str(uuid4()),'version':2,'generation':old['generation']+99,'expectedState':'draft'}
    owned=visited(captured(editor,'date-schedules/contracts/save',stale,expected=409),'editor',catalog,definition_id)
    assert (owned.get('contract_date_contracts'),owned.get('contract_date_fields'))==(2,6),"A definition's save read other definitions: "+repr(owned)
    library=visited(captured(viewer,'date-schedules'),'viewer',catalog)
    assert library.get('contract_date_definitions',0)<=versions_held,'The library checked a definition for each joined row: '+repr(library)
    # Every secured relation in the statement decides its rows from one set: the count follows the
    # statement's relations, while a check per row would follow the library's versions, contracts and fields.
    def decisions(statement, profile, *values):
        stats="SELECT 'CALLS|'||coalesce(sum(calls) FILTER(WHERE funcname IN('contract_date_visible','contract_date_schedule_visible')),0)||'|'||coalesce(sum(calls) FILTER(WHERE funcname='visible_contract_date_versions'),0) FROM pg_stat_xact_user_functions WHERE schemaname='security';"
        output=query(f"SET track_functions='all';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issued(profile)}');{stats}{binding(statement,*values)};{stats}ROLLBACK;").splitlines()
        calls=[list(map(int,line.split('|')[1:])) for line in output if line.startswith('CALLS|')]
        return [end-start for start,end in zip(*calls)]
    assert versions_held>4,'The library is too small to tell a statement decision from a row decision'
    for who,profile in ((viewer,'viewer'),(editor,'editor'),(approver,'approver')):
        per_row,set_based=decisions(captured(who,'date-schedules'),profile,catalog)
        assert per_row==0 and 1<=set_based<=4,f'{profile} listing did not make one visibility decision per statement: per-row={per_row}, sets={set_based}'
    rules=query("SELECT string_agg(policyname||':'||tablename||'='||regexp_replace(qual,'\\s+',' ','g'),chr(10) ORDER BY policyname,tablename) FROM pg_policies WHERE schemaname='market_data' AND policyname IN('scoped_read','risk_worker_scoped_read') AND tablename IN('contract_date_versions','contract_date_contracts','contract_date_fields');").splitlines()
    expected="COALESCE(((clock_timestamp() < ( SELECT security.contract_date_authority_until() AS contract_date_authority_until)) AND ((catalog_id, definition_id, version) IN ( SELECT v.catalog, v.definition, v.version FROM security.visible_contract_date_versions() v(catalog, definition, version)))), false)"
    assert rules==[f'{policy}:{table}={expected}' for policy in ('risk_worker_scoped_read','scoped_read') for table in ('contract_date_contracts','contract_date_fields','contract_date_versions')],rules
    # A write's row checks stay per row: the guard's parent lock must not pay for the whole library once per field.
    fields_in_draft=int(query(f"SELECT count(*) FROM market_data.contract_date_fields WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2;"))
    assert fields_in_draft>=3
    _,write_sets=decisions(f"DELETE FROM market_data.contract_date_fields WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2",'editor')
    assert write_sets==1,f'A draft write decided the library set {write_sets} times for {fields_in_draft} fields'
    # Every version in the library, decided both ways under the same request: the set must be exactly what the per-row rule admits.
    every=query("SELECT string_agg(format('(%L::uuid,%L::uuid,%s)',catalog_id,definition_id,version),',') FROM market_data.contract_date_versions;")
    def agreed(profile, bind='', setup='', later='', extra=''):
        admitted=f"SELECT 'SET|'||coalesce(string_agg(k.c||','||k.d||','||k.v,';') FILTER(WHERE security.contract_date_visible(k.c,k.d,k.v)),'') FROM(VALUES {every}{extra})k(c,d,v);"
        listed="SELECT 'SET|'||coalesce(string_agg(s.catalog||','||s.definition||','||s.version,';'),'') FROM security.visible_contract_date_versions() s;"
        identity=f"SELECT security.use_context('{issued(profile)}');{bind}" if profile else ''
        changed=f"SET LOCAL SESSION AUTHORIZATION preview_owner;{later}SET LOCAL SESSION AUTHORIZATION preview_runtime;" if later else ''
        output=query(f"BEGIN;{setup}SET LOCAL SESSION AUTHORIZATION preview_runtime;{identity}{changed}{admitted}{listed}ROLLBACK;").splitlines()
        per_row,set_based=[set(filter(None,line[4:].split(';'))) for line in output if line.startswith('SET|')]
        assert per_row==set_based,f'Set visibility differs from the per-row rule for {profile} {bind} {setup}: only per-row {per_row-set_based}, only set {set_based-per_row}'
        return set_based
    def market(view):
        return f"SELECT security.use_market_context('{catalog}','{view['contextId']}');"
    def raw(statement):
        return f"SET LOCAL session_replication_role=replica;{statement}SET LOCAL session_replication_role=origin;"
    draft_key,published_key,literal_key=f'{catalog},{definition_id},2',f'{catalog},{definition_id},1',f"{catalog},{late_receipt['id']},{late_receipt['version']}"
    for profile,sees_draft in (('viewer',False),('editor',True),('approver',True)):
        current=agreed(profile)
        assert published_key in current and (draft_key in current)==sees_draft,(profile,current)
        bound=agreed(profile,market(context()))
        assert published_key in bound and draft_key not in bound,(profile,'historical read',bound)
        assert published_key not in agreed(profile,market(before)),'A publication was visible before its knowledge cut'
        assert published_key not in agreed(profile,market(context('2021-06-15'))),'A definition was visible outside its schedule window'
    assert agreed(None)==set(),'A login with no bound request saw contract dates'
    unpublished=raw(f"UPDATE market_data.contract_date_fields SET schedule_id='{extended['id']}',schedule_version={extended['version']} WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2 AND schedule_id IS NOT NULL;")
    assert draft_key not in agreed('editor',setup=unpublished),'A definition over an unpublished schedule was admitted'
    late_schedules=raw(f"UPDATE market_data.knowledge_publications SET known_from=known_from+interval '100 years' WHERE catalog_id='{catalog}';")
    known_later=agreed('viewer',market(context()),late_schedules)
    assert published_key not in known_later and literal_key in known_later,'A schedule known after the cut did not hide only its definitions'
    late_calendars=raw(f"UPDATE market_data.market_knowledge_receipts SET known_from=known_from+interval '100 years' WHERE catalog_id='{catalog}';")
    assert published_key not in agreed('viewer',market(context()),late_calendars),'A calendar hidden at the cut left its definition visible'
    viewer_grants="(SELECT principal_id FROM security.identity_bindings WHERE subject='viewer')"
    assert agreed('viewer',later=f"UPDATE security.role_grants SET revoked=true WHERE principal_id={viewer_grants};")==set(),'A request with no read authority saw contract dates'
    # The same definition in a second readable catalog: visible now, never through a context bound to the first.
    sibling=str(uuid4())
    copies=raw(f"""INSERT INTO security.market_catalog_scopes SELECT(jsonb_populate_record(NULL::security.market_catalog_scopes,to_jsonb(s)||jsonb_build_object('catalog_id','{sibling}'))).* FROM security.market_catalog_scopes s WHERE s.catalog_id='{catalog}';
      INSERT INTO market_data.contract_date_versions SELECT(jsonb_populate_record(NULL::market_data.contract_date_versions,to_jsonb(v)||jsonb_build_object('catalog_id','{sibling}'))).* FROM market_data.contract_date_versions v WHERE(v.catalog_id,v.definition_id,v.version)=('{catalog}','{definition_id}',1);
      INSERT INTO market_data.contract_date_knowledge_receipts SELECT(jsonb_populate_record(NULL::market_data.contract_date_knowledge_receipts,to_jsonb(k)||jsonb_build_object('catalog_id','{sibling}'))).* FROM market_data.contract_date_knowledge_receipts k JOIN market_data.contract_date_versions v USING(catalog_id,publication_order) WHERE(v.catalog_id,v.definition_id,v.version)=('{catalog}','{definition_id}',1);""")
    extra=f",('{sibling}'::uuid,'{definition_id}'::uuid,1)";sibling_key=f'{sibling},{definition_id},1'
    assert sibling_key in agreed('viewer',setup=copies,extra=extra),'The second catalog was not readable'
    assert sibling_key not in agreed('viewer',market(context()),copies,extra=extra),'A context admitted another catalog'
    # A draft is held by authoring authority, so it ends the statement's window when that authority ends first.
    def grants(subject):
        return f"(SELECT principal_id FROM security.identity_bindings WHERE subject='{subject}')"
    ending=raw(f"""INSERT INTO security.role_grants SELECT gen_random_uuid(),{grants('editor')},r.role_id,r.tenant_id,r.workspace_id,r.granted_from,r.granted_to,false FROM security.role_grants r WHERE r.principal_id={grants('viewer')} AND NOT r.revoked;
      UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '30 seconds' WHERE principal_id={grants('editor')} AND role_id NOT IN(SELECT role_id FROM security.role_grants WHERE principal_id={grants('viewer')});""")
    windows="SELECT 'UNTIL|'||(security.contract_date_authority_until()=security.authority_until('ReadMarketData'))||'|'||(security.contract_date_authority_until()=greatest(security.authority_until('WriteMarketDefinitions'),security.authority_until('SealMarketDefinitions')));"
    def window(profile, bind='', setup=''):
        output=query(f"BEGIN;{setup}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issued(profile)}');{bind}{windows}ROLLBACK;").splitlines()
        return next(line for line in output if line.startswith('UNTIL|'))
    assert window('viewer')=='UNTIL|true|false',window('viewer')
    assert window('editor',market(context()),ending).split('|')[1]=='true','A historical read kept the authoring window'
    assert window('editor',setup=ending)=='UNTIL|false|true','Drafts outlived their authoring authority: '+window('editor',setup=ending)
    # Field mutations must take the same parent row lock as publication. An API
    # advisory gate alone does not protect this authorized raw storage boundary.
    def child_interlock(expired=False):
        owner=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        child=None;errors=''
        try:
            owner.stdin.write(f"BEGIN;SELECT version FROM market_data.contract_date_versions WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2 FOR UPDATE;\n\\echo LOCKED\n");owner.stdin.flush()
            while owner.stdout.readline().strip()!='LOCKED':
                if owner.poll() is not None:raise AssertionError(owner.stderr.read())
            prefix=''
            if expired:
                token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',2);")
                prefix=f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
            child=subprocess.Popen(psql,env={**environment,'PGAPPNAME':'contract-date-child-interlock'},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
            child.stdin.write("\\set VERBOSITY verbose\n"+(prefix or 'BEGIN;')+f"DELETE FROM market_data.contract_date_fields WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2;ROLLBACK;\n\\q\n");child.stdin.flush()
            blocked=False
            for _ in range(100):
                if query("SELECT count(*) FROM pg_stat_activity WHERE application_name='contract-date-child-interlock' AND wait_event_type='Lock';")=='1':blocked=True;break
                if child.poll() is not None:break
                time.sleep(.02)
            assert blocked,'Raw child mutation bypassed the publication parent lock'
            if expired:time.sleep(2.1)
        finally:
            owner.stdin.write('ROLLBACK;\n\\q\n');owner.stdin.flush();owner.communicate(timeout=10)
            if child is not None:
                _,errors=child.communicate(timeout=10)
        assert child is not None
        if expired:assert child.returncode!=0 and '42501' in errors,'Authority expired during the parent wait but raw mutation was admitted: '+errors
        else:assert child.returncode==0,errors
    child_interlock()
    child_interlock(expired=True)
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    hidden=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{before['contextId']}');SELECT (SELECT count(*) FROM market_data.contract_date_versions WHERE definition_id='{definition_id}')||','||(SELECT count(*) FROM market_data.contract_date_requests WHERE definition_id='{definition_id}');ROLLBACK;")
    assert hidden.splitlines()[-1]=='0,0',hidden
    def reject(statement, state):
        query(f"BEGIN;DO $$BEGIN {statement};RAISE EXCEPTION 'Expected rejection';EXCEPTION WHEN SQLSTATE '{state}' THEN NULL;END$$;ROLLBACK;")
    reject(f"UPDATE market_data.contract_date_fields SET schedule_version=63 WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2", '23503')
    reject(f"UPDATE market_data.contract_date_versions SET generation=generation+1,validation_context_id='{uuid4()}' WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2", '23503')
    reject(f"UPDATE market_data.contract_date_fields SET version=2 WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=1", '55000')
    reject(f"DELETE FROM market_data.contract_date_versions WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=1", '55000')
    reject(f"UPDATE market_data.contract_date_fields SET schedule_catalog_id=NULL WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2", '23514')
    reject(f"UPDATE market_data.contract_date_contracts SET cutoff_utc='24:00' WHERE catalog_id='{catalog}' AND definition_id='{definition_id}' AND version=2", '23514')
    # Current calendar authority is required for full-content catalog, resolve and replay.
    reference=manifest['referenceCatalogId']
    rows=query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}' RETURNING tenant_id,workspace_id,access_mode;")
    try:
        viewer.call('date-schedules/resolve',{'expression':'oe.imported_days[0]','context':resolution['context']},expected=422)
        approver.call('date-schedules/publish',{k:extended[k] for k in ('id','version','generation')},expected=422)
        assert viewer.call('date-schedules/resolve',{'expression':'oe.plain_dates[0]','context':resolution['context']})['date']=='2021-01-19'
        assert not any(v['id']==definition_id for v in viewer.call('date-schedules')['contractDefinitions'])
        assert published_key not in agreed('viewer'),'A definition over a revoked calendar was admitted'
        locked=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issued('editor')}');SELECT coalesce(security.lock_contract_date_version('{catalog}','{definition_id}',1),'none');ROLLBACK;").splitlines()[-1]
        assert locked=='none','An author locked a version it may not see: '+locked
        assert query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT count(*) FROM market_data.contract_date_requests WHERE definition_id='{definition_id}';ROLLBACK;").splitlines()[-1]=='0'
        viewer.call('date-schedules/contracts/resolve', {**resolution,'context':resolution['context']}, expected=422)
        approver.call('date-schedules/contracts/publish', publish, expected=403)
    finally:
        tenant,workspace,mode=rows.split('|');query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{mode}');")
    print('PASS shared contract dates: separate sessions, author/publisher gates, exact source/context FKs, immutable publication, retained cutoff, pinned resolution and revocation',flush=True)
