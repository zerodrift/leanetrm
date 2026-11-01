"""Shared contract-date library checks in a newly owned synthetic preview."""
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor
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
        assert query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT count(*) FROM market_data.contract_date_requests WHERE definition_id='{definition_id}';ROLLBACK;").splitlines()[-1]=='0'
        viewer.call('date-schedules/contracts/resolve', {**resolution,'context':resolution['context']}, expected=422)
        approver.call('date-schedules/contracts/publish', publish, expected=403)
    finally:
        tenant,workspace,mode=rows.split('|');query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{mode}');")
    print('PASS shared contract dates: separate sessions, author/publisher gates, exact source/context FKs, immutable publication, retained cutoff, pinned resolution and revocation',flush=True)
