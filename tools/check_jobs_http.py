"""Exercise job ownership through authenticated HTTP and the RLS boundary."""
from datetime import datetime, timedelta, timezone
from uuid import uuid4
from preview_client import PreviewClient


def prepare(query,manifest):
    # This projection is synthetic. No Docker or host connection is configured.
    tenant,workspace=manifest['tenantId'],manifest['workspaceId']
    worker=str(uuid4())
    query(f"""CREATE ROLE jobs_fixture_worker LOGIN IN ROLE openetrm_scheduler;
    INSERT INTO security.principals VALUES('{worker}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{worker}','jobs_fixture_worker','Synthetic infrastructure observer',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{worker}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='viewer';
    INSERT INTO automation.workers VALUES('jobs_fixture_worker','{worker}','{tenant}','{workspace}','controller');
    INSERT INTO automation.clusters VALUES('syntheticcluster00000000001','Synthetic cluster','jobs_fixture_worker','{tenant}','{workspace}',true);
    INSERT INTO security.role_permissions SELECT id,'ManageInfrastructure' FROM security.roles WHERE code='access_administrator';
    INSERT INTO automation.installation_operators SELECT principal_id,'ManageInfrastructure',clock_timestamp()+interval '1 hour' FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','admin');
    SET SESSION AUTHORIZATION jobs_fixture_worker;
    SELECT automation.observe_nodes('syntheticcluster00000000001','[{{"id":"syntheticnode0000000000001","version":7,"name":"Synthetic shared worker","role":"worker","state":"ready","availability":"active","cpuNano":1000000000,"memoryBytes":1073741824,"workloadNode":true}}]'::jsonb);""")
    collector=str(uuid4());catalog=manifest['catalogId'];reference=manifest['referenceCatalogId'];revision=manifest['referenceRevisionId']
    query(f"""CREATE ROLE jobs_fixture_collector LOGIN IN ROLE openetrm_collector;
    INSERT INTO security.principals VALUES('{collector}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{collector}','jobs_fixture_collector','Synthetic collection service',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{collector}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='market_data_editor';
    INSERT INTO power_data.collectors VALUES('jobs_fixture_collector','{collector}','{tenant}','{workspace}','{catalog}');""")
    for provider,dataset,clock,point in [('PJM','da_hrl_lmps','synthetic_eastern','51288'),('ERCOT','np4-190-cd/dam_stlmnt_pnt_prices','synthetic_central','HB_NORTH')]:
        source,job=str(uuid4()),str(uuid4())
        query(f"""INSERT INTO power_data.sources VALUES('{source}','{catalog}','Synthetic {provider} delivery source','{provider}','{dataset}','DA','{point}',60,'{reference}','{revision}','test_hub_a','{clock}','usd','test_power_quantity');
        INSERT INTO power_data.jobs VALUES('{job}',gen_random_uuid(),'Synthetic {provider} daily collection','jobs_fixture_collector','{catalog}','{provider}',true,false,'0 8 * * *','UTC',-7,-1);
        INSERT INTO power_data.job_sources VALUES('{job}','{source}','{catalog}');""")


def run_checks(url,query=None):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'viewer'})
    collection=client.call('jobs')['collectionJobs']
    assert len(collection['jobs'])==2 and collection['total']==2
    assert all(j['state']=='paused' and not j['canEdit'] and j['cadence']=='Daily at 08:00 UTC' for j in collection['jobs'])
    assert all('collector_login' not in j and 'sourceIds' not in j for j in collection['jobs'])
    if query is not None:
        # Job-only members can resolve their own wall clocks without market access.
        query("DELETE FROM security.role_permissions WHERE permission='ReadMarketData' AND role_id=(SELECT id FROM security.roles WHERE code='viewer');")
        try:
            assert 'ReadMarketData' not in client.call('access')['permissions']
            candidates=client.call('time/inspect',{'localTime':'2040-01-02T08:30:00','timeZone':'Asia/Tokyo'})['candidates']
            assert len(candidates)==1 and candidates[0]['instant'].startswith('2040-01-01T23:30:00')
        finally:
            query("INSERT INTO security.role_permissions SELECT id,'ReadMarketData' FROM security.roles WHERE code='viewer';")
    now = datetime.now(timezone.utc)
    body = dict(requestId=str(uuid4()),name='Synthetic HTTP readiness',operation='worker_check',
                dueAt=(now+timedelta(minutes=1)).isoformat(),expiresAt=(now+timedelta(hours=1)).isoformat(),
                intervalSeconds=None,timeZone='UTC',misfire='run_once',overlap='forbid',
                timeoutSeconds=30,maximumAttempts=2,profileId=None,profileVersion=None)
    job = client.call('jobs/create', body)
    assert client.call('jobs/create', body)['id'] == job['id']
    client.call('jobs/create', {**body,'name':'Changed replay'},expected=409)
    client.call('jobs/create', {**body,'ownerId':str(uuid4())},expected=400)
    directory=client.call('jobs')
    assert any(j['id']==job['id'] for j in directory['jobs'])
    listed=next(j for j in directory['jobs'] if j['id']==job['id'])
    edit=dict(requestId=str(uuid4()),id=job['id'],expectedVersion=job['version'],
              name='Synthetic revised readiness',dueAt=(now+timedelta(minutes=3)).isoformat(),
              expiresAt=(now+timedelta(hours=2)).isoformat(),intervalSeconds=600,
              timeZone='UTC',misfire='run_once',overlap='forbid',timeoutSeconds=45,maximumAttempts=3)
    revised=client.call('jobs/update',edit)
    assert revised['name']==edit['name'] and revised['intervalSeconds']==600
    assert revised['timeoutSeconds']==45 and revised['maximumAttempts']==3
    assert revised['version']==job['version']+1 and revised['operation']==job['operation']
    assert client.call('jobs/update',edit)==revised, 'Exact edit replay changed the retained outcome'
    client.call('jobs/update',{**edit,'name':'Changed replay'},expected=409)
    client.call('jobs/update',{**edit,'requestId':str(uuid4())},expected=409)
    client.call('jobs/update',{**edit,'requestId':str(uuid4()),'expectedVersion':revised['version'],'operation':'swarm_job'},expected=400)
    assert listed['canEdit'] and listed['timeZone']=='UTC'
    job=revised
    assert directory['infrastructure']['nodes'][0]['actions']==[]
    client.call('infrastructure/availability',dict(requestId=str(uuid4()),clusterId='syntheticcluster00000000001',nodeId='syntheticnode0000000000001',expectedVersion=7,availability='pause'),expected=403)
    client.call('profile', {'profile':'editor'})
    assert not any(j['id']==job['id'] for j in client.call('jobs')['jobs'])
    client.call('jobs/update',{**edit,'requestId':str(uuid4()),'expectedVersion':job['version']},expected=403)
    control = dict(requestId=str(uuid4()),id=job['id'],action='pause',expectedVersion=job['version'])
    client.call('jobs/control',control,expected=403)
    client.call('profile',{'profile':'admin'})
    admin_edit={**edit,'requestId':str(uuid4()),'expectedVersion':job['version'],'name':'Synthetic administrator revision'}
    job=client.call('jobs/update',admin_edit)
    control['expectedVersion']=job['version']
    paused = client.call('jobs/control',control)
    assert paused['state']=='paused'
    client.call('profile',{'profile':'viewer'})
    resumed=client.call('jobs/control',{**control,'requestId':str(uuid4()),'action':'resume','expectedVersion':paused['version']})
    assert resumed['state']=='active'
    cancelled=client.call('jobs/control',{**control,'requestId':str(uuid4()),'action':'cancel','expectedVersion':resumed['version']})
    assert cancelled['state']=='cancelled'
    assert not cancelled['canEdit']
    client.call('jobs/update',{**edit,'requestId':str(uuid4()),'expectedVersion':cancelled['version']},expected=422)
    if query is not None:check_collection_jobs(url,query)
    print('PASS HTTP job creation/editing, exact replays, version conflicts, owner isolation, administrator control and terminal read-only terms',flush=True)


def check_collection_jobs(url,query):
    import json
    import time
    from contextlib import contextmanager
    from concurrent.futures import ThreadPoolExecutor
    admin=PreviewClient(url);admin.bootstrap();admin.call('profile',{'profile':'admin'})
    viewer=PreviewClient(url);viewer.bootstrap();viewer.call('profile',{'profile':'viewer'})
    directory=admin.call('jobs')['collectionJobs']
    selected=next(j for j in directory['jobs'] if j['provider']=='PJM')
    original=selected.copy();job=selected['id']
    before=json.loads(query(f"SELECT row_to_json(j)::text FROM power_data.jobs j WHERE id='{job}';"))
    assert selected['canEdit'] and 'ReadMarketData' not in admin.call('access')['permissions']
    request=dict(requestId=str(uuid4()),id=job,expectedGeneration=selected['generation'],name='Synthetic revised collection',scheduleEnabled=False,fromDay=-14,throughDay=-2)
    viewer.call('jobs/collection/update',request,expected=403)
    revised=admin.call('jobs/collection/update',request)
    assert revised['generation']!=selected['generation'] and revised['name']==request['name']
    assert revised['fromDay']==-14 and revised['throughDay']==-2 and revised['scheduleEnabled'] is False and revised['requestId']==request['requestId']
    assert 'serviceName' not in revised and 'lastAttempt' not in revised
    assert admin.call('jobs/collection/update',request)==revised
    admin.call('jobs/collection/update',{**request,'name':'Changed replay'},expected=409)
    admin.call('jobs/collection/update',{**request,'requestId':str(uuid4())},expected=409)
    for invalid in [{'fromDay':0},{'throughDay':-32},{'fromDay':-2,'throughDay':-7}]:
        admin.call('jobs/collection/update',{**request,'requestId':str(uuid4()),'expectedGeneration':revised['generation'],**invalid},expected=422)
    admin.call('jobs/collection/update',{**request,'enabled':True},expected=400)
    after=json.loads(query(f"SELECT row_to_json(j)::text FROM power_data.jobs j WHERE id='{job}';"))
    immutable=['id','collector_login','catalog_id','provider','enabled','cron','schedule_zone']
    assert all(before[k]==after[k] for k in immutable), 'An edit changed the collection execution identity'
    assert query(f"SELECT count(*) FROM power_data.job_commands WHERE job_id='{job}';")=='1'
    assert query("SELECT has_function_privilege('openetrm_runtime','power_data.collector_eligible(power_data.collectors)','EXECUTE');")=='f'
    assert query("SELECT has_table_privilege('openetrm_runtime','power_data.job_commands','INSERT');")=='f'

    # Replays still require current management permission; a denied retry is not
    # proof that the first transaction did not commit.
    role=query("SELECT id FROM security.roles WHERE code='access_administrator';")
    query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='ManageJobs';")
    try:admin.call('jobs/collection/update',request,expected=403)
    finally:query(f"INSERT INTO security.role_permissions VALUES('{role}','ManageJobs');")
    assert admin.call('jobs/collection/update',request)==revised

    # Catalog ownership, not possession of a job UUID, determines visibility.
    scope=json.loads(query(f"SELECT row_to_json(s)::text FROM security.market_catalog_scopes s WHERE catalog_id='{before['catalog_id']}';"))
    query(f"DELETE FROM security.market_catalog_scopes WHERE catalog_id='{before['catalog_id']}';")
    try:
        assert admin.call('jobs')['collectionJobs']['jobs']==[]
        admin.call('jobs/collection/update',request,expected=403)
    finally:query(f"INSERT INTO security.market_catalog_scopes VALUES('{scope['catalog_id']}','{scope['tenant_id']}','{scope['workspace_id']}');")
    foreign=str(uuid4())
    query(f"INSERT INTO security.workspaces VALUES('{scope['tenant_id']}','{foreign}','synthetic_collection_scope');UPDATE security.market_catalog_scopes SET workspace_id='{foreign}' WHERE catalog_id='{scope['catalog_id']}';")
    try:
        assert admin.call('jobs')['collectionJobs']['jobs']==[]
        admin.call('jobs/collection/update',request,expected=403)
    finally:query(f"UPDATE security.market_catalog_scopes SET workspace_id='{scope['workspace_id']}' WHERE catalog_id='{scope['catalog_id']}';DELETE FROM security.workspaces WHERE id='{foreign}';")

    collector=json.loads(query("SELECT row_to_json(c)::text FROM power_data.collectors c WHERE login='jobs_fixture_collector';"))
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{collector['principal_id']}';")
    try:
        blocked=next(j for j in admin.call('jobs')['collectionJobs']['jobs'] if j['id']==job)
        assert not blocked['canEdit'] and 'execution authority' in blocked['editReason']
        admin.call('jobs/collection/update',request,expected=422)
    finally:query(f"UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{collector['principal_id']}';")

    def wait_for(sql):
        until=time.monotonic()+10
        while time.monotonic()<until:
            if query(sql)=='t':return
            time.sleep(.02)
        raise AssertionError('The owned database concurrency barrier was not reached')

    @contextmanager
    def owned_hold(sql):
        name='synthetic_collection_hold_'+uuid4().hex
        with ThreadPoolExecutor(max_workers=1) as pool:
            held=pool.submit(query,f"\\set VERBOSITY verbose\nSET application_name='{name}';{sql};SELECT pg_sleep(30);")
            try:
                wait_for(f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{name}' AND datname=current_database() AND wait_event='PgSleep');")
                yield
            finally:
                # Only the exact backend created above is cancelled. The owned
                # client exits and rolls back/releases its test-only locks.
                cancelled=query(f"SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE application_name='{name}' AND datname=current_database() AND pid<>pg_backend_pid();")
                assert cancelled=='t', 'The owned lock holder disappeared unexpectedly'
                try:held.result(timeout=10)
                except RuntimeError as error:assert '57014' in str(error),str(error)
                else:raise AssertionError('The owned lock holder was not cancelled')

    with owned_hold("SET SESSION AUTHORIZATION jobs_fixture_collector;SELECT power_data.worker_lock()"):
        busy=next(j for j in admin.call('jobs')['collectionJobs']['jobs'] if j['id']==job)
        assert not busy['canEdit'] and 'worker is active' in busy['editReason']
        admin.call('jobs/collection/update',request,expected=422)
    assert query(f"SELECT generation FROM power_data.jobs WHERE id='{job}';")==revised['generation']

    # Natural authority expiry must be rechecked after an actual row-lock wait.
    grants=json.loads(query("SELECT json_agg(json_build_object('id',g.id,'until',g.granted_to))::text FROM security.role_grants g JOIN security.workspace_memberships m ON(m.principal_id,m.tenant_id,m.workspace_id)=(g.principal_id,g.tenant_id,g.workspace_id) WHERE m.login_handle='admin';"))
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            with owned_hold(f"BEGIN;SELECT id FROM power_data.jobs WHERE id='{job}' FOR UPDATE"):
                query("UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1 second' WHERE principal_id=(SELECT principal_id FROM security.workspace_memberships WHERE login_handle='admin');")
                waiting=pool.submit(admin.call,'jobs/collection/update',{**request,'requestId':str(uuid4()),'expectedGeneration':revised['generation']},expected=403)
                wait_for("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND query LIKE 'SELECT power_data.update_job%' AND wait_event_type='Lock');")
                wait_for("SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM security.workspace_memberships WHERE login_handle='admin');")
            waiting.result(timeout=10)
    finally:
        for grant in grants:query(f"UPDATE security.role_grants SET granted_to='{grant['until']}' WHERE id='{grant['id']}';")
    assert query(f"SELECT generation FROM power_data.jobs WHERE id='{job}';")==revised['generation']

    restore=dict(requestId=str(uuid4()),id=job,expectedGeneration=revised['generation'],name=original['name'],scheduleEnabled=False,fromDay=original['fromDay'],throughDay=original['throughDay'])
    restored=admin.call('jobs/collection/update',restore)
    assert restored['scheduleEnabled'] is False and all(j['state']=='paused' for j in admin.call('jobs')['collectionJobs']['jobs'])
    print('PASS collection jobs: scoped metadata, exact audited replay, generation conflicts, denied callers, unchanged identities, active-worker fencing and authority expiry after row waits',flush=True)
