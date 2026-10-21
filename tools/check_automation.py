#!/usr/bin/env python3
"""Qualify job ownership, RLS and worker fencing in a new stock-PG database."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[1]


def checks(query, worker_once=None, controller_once=None):
    assert query("SELECT to_regclass('automation.jobs') IS NOT NULL;") == 't', 'Durable jobs are missing'
    assert query("""SELECT
      NOT pg_has_role('openetrm_scheduler','openetrm_reference_importer','MEMBER')
      AND NOT pg_has_role('openetrm_reference_importer','openetrm_scheduler','MEMBER')
      AND NOT has_function_privilege('openetrm_scheduler','security.import_reference_capture(uuid,uuid,jsonb)','EXECUTE')
      AND NOT has_function_privilege('openetrm_reference_importer','automation.claim_next(uuid,integer)','EXECUTE');
      """) == 't', 'Independent service executors must not inherit each other\'s authority'
    tenant, workspace, alice, bob, admin, worker = [str(uuid4()) for _ in range(6)]
    query(f"""INSERT INTO security.tenants VALUES('{tenant}','synthetic_automation');
    INSERT INTO security.workspaces VALUES('{tenant}','{workspace}','synthetic_jobs');
    CREATE ROLE automation_test_runtime LOGIN IN ROLE openetrm_runtime;
    CREATE ROLE automation_test_auth LOGIN IN ROLE openetrm_authenticator;
    CREATE ROLE automation_test_worker LOGIN IN ROLE openetrm_scheduler;
    INSERT INTO security.principals VALUES('{alice}','human',true),('{bob}','human',true),('{admin}','human',true),('{worker}','service',true);
    INSERT INTO security.workspace_memberships VALUES
    ('{tenant}','{workspace}','{alice}','job_alice','Synthetic first member',true,1),
    ('{tenant}','{workspace}','{bob}','job_bob','Synthetic second member',true,1),
    ('{tenant}','{workspace}','{admin}','job_admin','Synthetic job administrator',true,1),
    ('{tenant}','{workspace}','{worker}','job_worker','Synthetic scheduler',true,1);
    INSERT INTO security.identity_bindings VALUES
    ('https://automation.invalid','alice','{alice}','human_interactive'),
    ('https://automation.invalid','bob','{bob}','human_interactive'),
    ('https://automation.invalid','admin','{admin}','human_interactive');
    INSERT INTO security.role_grants SELECT gen_random_uuid(),p.id,r.id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false
    FROM security.principals p CROSS JOIN security.roles r WHERE p.id IN('{alice}','{bob}','{worker}') AND r.code='viewer';
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{admin}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='access_administrator';
    INSERT INTO automation.workers VALUES('automation_test_worker','{worker}','{tenant}','{workspace}','scheduler');""")

    def user(subject, sql, selected_workspace=workspace):
        token = query(f"SET SESSION AUTHORIZATION automation_test_auth; SELECT security.issue_context('https://automation.invalid','{subject}','human_interactive','{tenant}','{selected_workspace}',NULL,'automation_test_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION automation_test_runtime; BEGIN; SELECT security.use_context('{token}'); {sql}; COMMIT;")

    def reject(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError(f'Expected rejection: {code}')

    assert query("SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='automation' AND c.relname IN('workers','installation_operators','execution_profiles') AND t.tgname='guard_control_change';") == '3', 'Automation grants must share the security revocation fence'
    profile = str(uuid4())
    query(f"INSERT INTO automation.execution_profiles VALUES('{profile}',1,'{tenant}','{workspace}','Synthetic bounded workload','registry.invalid/synthetic@sha256:{'a'*64}','{'a'*25}','synthetic',100000000,33554432,16,true,'bounded_exit_v1','amd64','');")
    reject(lambda: query(f"UPDATE automation.execution_profiles SET image='registry.invalid/synthetic@sha256:{'b'*64}' WHERE id='{profile}';"), 'profile_immutable')
    generation = int(query('SELECT generation FROM security.epoch;'))
    query(f"UPDATE automation.execution_profiles SET enabled=false WHERE id='{profile}';")
    assert int(query('SELECT generation FROM security.epoch;')) > generation

    due = query("SELECT (clock_timestamp()-interval '1 second')::text;")
    expiry = query("SELECT (clock_timestamp()+interval '1 hour')::text;")
    request = dict(requestId=str(uuid4()), name='Synthetic worker readiness', operation='worker_check',
                   dueAt=due, expiresAt=expiry, intervalSeconds=None, timeZone='UTC',
                   misfire='run_once', overlap='forbid', timeoutSeconds=30, maximumAttempts=2,
                   profileId=None, profileVersion=None)
    def create(subject, body):
        return json.loads(user(subject, f"SELECT automation.create_job('{json.dumps(body)}'::jsonb)::text"))
    first = create('alice', request)
    job = first['id']
    assert create('alice', request)['id'] == job, 'Exact replay created a second job'
    reject(lambda: create('alice', {**request, 'name': 'Changed request'}), 'job_conflict')
    reject(lambda: create('alice', {**request, 'requestId': str(uuid4()), 'ownerId': bob}), 'job_input')
    assert len(json.loads(user('alice', 'SELECT automation.job_list()::text'))['jobs']) == 1
    assert json.loads(user('bob', 'SELECT automation.job_list()::text'))['jobs'] == []
    assert len(json.loads(user('admin', 'SELECT automation.job_list()::text'))['jobs']) == 1
    assert user('bob', 'SELECT count(*) FROM automation.jobs') == '0', 'RLS leaked another owner job'
    assert query('SET SESSION AUTHORIZATION automation_test_runtime; SELECT count(*) FROM automation.jobs;') == '0', 'Unbound runtime saw jobs'
    reject(lambda: user('alice', 'UPDATE automation.jobs SET state=\'cancelled\''), 'permission denied')
    def control(subject, action, version, request_id=None):
        body = dict(requestId=request_id or str(uuid4()), id=job, action=action, expectedVersion=version)
        return json.loads(user(subject, f"SELECT automation.control_job('{json.dumps(body)}'::jsonb)::text"))
    reject(lambda: control('bob', 'cancel', 1), 'job_denied')
    paused = control('admin', 'pause', 1)
    assert paused['state'] == 'paused'
    edit_request=dict(requestId=str(uuid4()),id=job,expectedVersion=paused['version'],
                      name='Synthetic revised schedule',dueAt=due,expiresAt=expiry,
                      intervalSeconds=600,timeZone='UTC',misfire='run_once',overlap='forbid',
                      timeoutSeconds=45,maximumAttempts=3)
    def edit(subject,body):
        return json.loads(user(subject,f"SELECT automation.update_job('{json.dumps(body)}'::jsonb)::text"))
    reject(lambda:edit('bob',edit_request),'job_denied')
    revised=edit('admin',edit_request)
    assert revised['name']==edit_request['name'] and revised['state']=='paused' and revised['intervalSeconds']==600
    assert revised['timeoutSeconds']==45 and revised['maximumAttempts']==3 and revised['version']==paused['version']+1
    assert revised['canEdit'] and edit('admin',edit_request)==revised
    query("DELETE FROM security.role_permissions WHERE permission='ManageJobs' AND role_id=(SELECT id FROM security.roles WHERE code='access_administrator');")
    try:
        reject(lambda:edit('admin',edit_request),'job_denied')
    finally:
        query("INSERT INTO security.role_permissions SELECT id,'ManageJobs' FROM security.roles WHERE code='access_administrator';")
    reject(lambda:edit('admin',{**edit_request,'name':'Different replay'}),'job_conflict')
    reject(lambda:edit('alice',{**edit_request,'requestId':str(uuid4())}),'job_conflict')
    reject(lambda:edit('alice',{**edit_request,'requestId':str(uuid4()),'expectedVersion':revised['version'],'ownerId':bob}),'job_input')
    # Restore the once-only execution terms for the existing worker lifecycle checks.
    paused=edit('alice',{**edit_request,'requestId':str(uuid4()),'expectedVersion':revised['version'],'intervalSeconds':None})
    assert query("SET SESSION AUTHORIZATION automation_test_worker; SELECT automation.claim_next('00000000-0000-0000-0000-000000000011',20)::text;") == ''
    resumed = control('alice', 'resume', paused['version'])
    claim = json.loads(query("SET SESSION AUTHORIZATION automation_test_worker; SELECT automation.claim_next('00000000-0000-0000-0000-000000000011',20)::text;"))
    assert claim['jobId'] == job
    assert not json.loads(user('alice','SELECT automation.job_list()::text'))['jobs'][0]['canEdit']
    reject(lambda:edit('alice',{**edit_request,'requestId':str(uuid4()),'expectedVersion':resumed['version']}),'job_transition')
    assert query("SET SESSION AUTHORIZATION automation_test_worker; SELECT automation.claim_next('00000000-0000-0000-0000-000000000012',20)::text;") == '', 'Overlapping workers claimed one job'
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{claim['id']}';")
    replacement = json.loads(query("SET SESSION AUTHORIZATION automation_test_worker; SELECT automation.claim_next('00000000-0000-0000-0000-000000000012',20)::text;"))
    assert replacement['id'] == claim['id'] and replacement['fence'] != claim['fence']
    reject(lambda:query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.finish_run('{claim['id']}',NULL,NULL,true,NULL,NULL);"),'job_fence')
    def finish(c):
        return query(f"SET SESSION AUTHORIZATION automation_test_worker; SELECT automation.finish_run('{c['id']}',{c['fence']},'{c['workerId']}',true,NULL,NULL);")
    def wait_behind_job_lock(work,table='jobs',identifier=job):
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocker = pool.submit(query, f"SET application_name='automation_lock_check';BEGIN;SELECT id FROM automation.{table} WHERE id='{identifier}' FOR UPDATE;SELECT pg_sleep(1.5);COMMIT;")
            deadline = time.monotonic()+3
            while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='automation_lock_check' AND wait_event='PgSleep');") != 't':
                assert time.monotonic()<deadline, 'Owned blocker did not acquire its lock'
                time.sleep(.01)
            try:
                work()
            finally:
                blocker.result()
    # Natural expiry while waiting for the exact job lock cannot retain admission authority.
    waiting=create('bob',{**request,'requestId':str(uuid4()),'dueAt':query("SELECT (clock_timestamp()+interval '10 minutes')::text;")})
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.5 seconds' WHERE principal_id='{bob}';")
    try:
        wait_behind_job_lock(lambda:reject(lambda:edit('bob',{**edit_request,'requestId':str(uuid4()),'id':waiting['id'],'expectedVersion':waiting['version']}),'job_denied'),identifier=waiting['id'])
    finally:
        query(f"UPDATE security.role_grants SET granted_to='9999-12-31' WHERE principal_id='{bob}';")
    user('bob',f"SELECT automation.control_job('{json.dumps(dict(requestId=str(uuid4()),id=waiting['id'],action='cancel',expectedVersion=waiting['version']))}'::jsonb)")
    # A retained occurrence cannot become the next cursor again: it would be skipped
    # forever by the scheduler's exact (job, due) identity.
    collision=create('bob',{**request,'requestId':str(uuid4()),'intervalSeconds':60})
    executed=json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_next('00000000-0000-0000-0000-000000000013',20)::text;"))
    assert executed['jobId']==collision['id']
    next_due=query(f"SELECT ('{due}'::timestamptz+(floor(extract(epoch FROM(clock_timestamp()-'{due}'::timestamptz))/60)+1)*interval '60 seconds')::text;")
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.finish_run('{executed['id']}',{executed['fence']},'{executed['workerId']}',true,NULL,'{next_due}');")
    collision_edit={**edit_request,'requestId':str(uuid4()),'id':collision['id'],'expectedVersion':collision['version']+1}
    reject(lambda:edit('bob',collision_edit),'job_input')
    changed=edit('bob',{**collision_edit,'requestId':str(uuid4()),'dueAt':next_due})
    user('bob',f"SELECT automation.control_job('{json.dumps(dict(requestId=str(uuid4()),id=changed['id'],action='cancel',expectedVersion=changed['version']))}'::jsonb)")
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()+interval '0.5 seconds' WHERE id='{replacement['id']}';")
    wait_behind_job_lock(lambda: reject(lambda: finish(replacement), 'job_fence'))
    reject(lambda: finish(claim), 'job_fence')
    cancel = control('admin', 'cancel', resumed['version'])
    assert cancel['state'] == 'cancelled'
    reject(lambda: finish(replacement), 'job_fence')
    assert query(f"SELECT state FROM automation.runs WHERE id='{claim['id']}';") == 'cancelled'
    if worker_once is not None:
        real_job = create('bob', {**request, 'requestId': str(uuid4())})
        worker_once()
        assert query(f"SELECT state FROM automation.jobs WHERE id='{real_job['id']}';") == 'completed'
        assert query(f"SELECT state FROM automation.runs WHERE job_id='{real_job['id']}';") == 'succeeded'
        worker_once()
        assert query(f"SELECT count(*) FROM automation.runs WHERE job_id='{real_job['id']}';") == '1'
        print('PASS separate .NET worker executes and durably completes an owned job without duplication', flush=True)
    agent,delegation=str(uuid4()),str(uuid4())
    # Agent principals hold only the agent reader role (migration 0066), so no agent may hold RunJobs today. The
    # setup lifts that guard in its own transaction, so automation's own delegation rules stay covered.
    query(f"""BEGIN;INSERT INTO security.principals VALUES('{agent}','agent',true);
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{agent}','synthetic_agent','Synthetic scheduling agent',true,1);
    INSERT INTO security.identity_bindings VALUES('https://automation.invalid','agent','{agent}','agent_credential');
    ALTER TABLE security.role_grants DISABLE TRIGGER agent_roles;
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{agent}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='viewer';
    ALTER TABLE security.role_grants ENABLE TRIGGER agent_roles;
    INSERT INTO security.agent_delegations VALUES('{delegation}','{agent}','{admin}','{tenant}','{workspace}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 hour',false);
    INSERT INTO security.delegation_permissions VALUES('{delegation}','RunJobs');COMMIT;""")
    def agent_context(sql,mandate=delegation):
        selected=f"'{mandate}'" if mandate else 'NULL'
        token=query(f"SET SESSION AUTHORIZATION automation_test_auth;SELECT security.issue_context('https://automation.invalid','agent','agent_credential','{tenant}','{workspace}',{selected},'automation_test_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION automation_test_runtime;BEGIN;SELECT security.use_context('{token}');{sql};COMMIT;")
    agent_request={**request,'requestId':str(uuid4()),'name':'Synthetic delegated readiness'}
    reject(lambda:agent_context(f"SELECT automation.create_job('{json.dumps(agent_request)}'::jsonb)",None),'job_denied')
    agent_job=json.loads(agent_context(f"SELECT automation.create_job('{json.dumps(agent_request)}'::jsonb)::text"))
    assert query(f"SELECT owner_id='{agent}' AND delegation_id='{delegation}' FROM automation.jobs WHERE id='{agent_job['id']}';")=='t'
    assert agent_job['id'] not in [j['id'] for j in json.loads(user('alice','SELECT automation.job_list()::text'))['jobs']]
    query(f"UPDATE security.agent_delegations SET revoked=true WHERE id='{delegation}';")
    assert query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_next('00000000-0000-0000-0000-000000000012',20)::text;")==''
    assert query(f"SELECT state FROM automation.jobs WHERE id='{agent_job['id']}';")=='blocked'
    reject(lambda:agent_context('SELECT automation.job_list()'),'job_denied')
    print('PASS agent scheduling requires its own identity and current explicit delegation',flush=True)
    # Current membership/role authority, rather than retained scheduling identity,
    # controls all future admission and publication.
    second = create('alice', {**request, 'requestId': str(uuid4())})
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{alice}';")
    reject(lambda: user('alice', 'SELECT automation.job_list()'), 'job_denied')
    assert query("SET SESSION AUTHORIZATION automation_test_worker; SELECT automation.claim_next('00000000-0000-0000-0000-000000000012',20)::text;") == ''
    assert query(f"SELECT state FROM automation.jobs WHERE id='{second['id']}';") == 'blocked'
    assert query("SELECT bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='automation' AND c.relkind='r';") == 't'
    query(f"UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{alice}';")
    query(f"UPDATE automation.workers SET kind='controller' WHERE login='automation_test_worker';INSERT INTO automation.clusters VALUES('syntheticcluster00000000001','Synthetic compute cluster','automation_test_worker','{tenant}','{workspace}',true);")
    node = dict(id='syntheticnode0000000000001',version=7,name='Synthetic compute node',role='worker',state='ready',availability='active',cpuNano=1000000000,memoryBytes=1073741824,workloadNode=True)
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.observe_nodes('syntheticcluster00000000001','{json.dumps([node])}'::jsonb);")
    overview = json.loads(user('bob','SELECT automation.infrastructure_directory()::text'))
    assert overview['nodes'][0]['actions'] == [] and overview['nodes'][0]['name']=='Synthetic compute node'
    command = dict(requestId=str(uuid4()),clusterId='syntheticcluster00000000001',nodeId=node['id'],expectedVersion=7,availability='pause')
    def node_command(subject, body):
        return json.loads(user(subject,f"SELECT automation.request_node_command('{json.dumps(body)}'::jsonb)::text"))
    reject(lambda:node_command('bob',command),'infrastructure_denied')
    reject(lambda:node_command('admin',command),'infrastructure_denied')
    query(f"INSERT INTO automation.installation_operators VALUES('{admin}','ManageInfrastructure',clock_timestamp()+interval '1 hour');")
    reject(lambda:node_command('admin',command),'infrastructure_denied')
    query("INSERT INTO security.role_permissions SELECT id,'ManageInfrastructure' FROM security.roles WHERE code='access_administrator';")
    requested = node_command('admin',command)
    assert node_command('admin',command)['id']==requested['id']
    claimed = json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_node_command('syntheticcluster00000000001','00000000-0000-0000-0000-000000000013',20)::text;"))
    assert claimed['id']==requested['id']
    reject(lambda:query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.authorize_node_command('{claimed['id']}',NULL,NULL);"),'infrastructure_fence')
    reject(lambda:query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.complete_node_command('{claimed['id']}',NULL,NULL,'applied',NULL);"),'infrastructure_fence')
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.authorize_node_command('{claimed['id']}','{claimed['workerId']}',{claimed['fence']});")
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.complete_node_command('{claimed['id']}','{claimed['workerId']}',{claimed['fence']},'applied',NULL);")
    assert query(f"SELECT state FROM automation.node_commands WHERE id='{claimed['id']}';")=='applied'
    predispatch=node_command('admin',{**command,'requestId':str(uuid4())})
    no_dispatch=json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_node_command('syntheticcluster00000000001','00000000-0000-0000-0000-000000000013',20)::text;"))
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.complete_node_command('{no_dispatch['id']}','{no_dispatch['workerId']}',{no_dispatch['fence']},'failed','infrastructure_busy');")
    assert query(f"SELECT state FROM automation.node_commands WHERE id='{predispatch['id']}';")=='failed'
    # Revocation after enqueue must stop the controller, not just hide a button.
    queued=node_command('admin',{**command,'requestId':str(uuid4()),'availability':'drain'})
    query(f"DELETE FROM automation.installation_operators WHERE principal_id='{admin}';")
    assert query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_node_command('syntheticcluster00000000001','00000000-0000-0000-0000-000000000013',20)::text;")==''
    assert query(f"SELECT state FROM automation.node_commands WHERE id='{queued['id']}';")=='failed'
    query(f"INSERT INTO automation.installation_operators VALUES('{admin}','ManageInfrastructure',clock_timestamp()+interval '1 hour');")
    uncertain=node_command('admin',{**command,'requestId':str(uuid4())})
    unknown=json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_node_command('syntheticcluster00000000001','00000000-0000-0000-0000-000000000013',20)::text;"))
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.authorize_node_command('{unknown['id']}','{unknown['workerId']}',{unknown['fence']});SELECT automation.complete_node_command('{unknown['id']}','{unknown['workerId']}',{unknown['fence']},'unknown','outcome_unknown');")
    resolution=dict(requestId=str(uuid4()),id=uncertain['id'],expectedVersion=7)
    user('admin',f"SELECT automation.resolve_node_command('{json.dumps(resolution)}'::jsonb)")
    reconciler=json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_node_command('syntheticcluster00000000001','00000000-0000-0000-0000-000000000014',20)::text;"))
    assert reconciler['availability']=='active', 'Reconciliation replayed the original mutation instead of fencing observed state'
    assert reconciler['fence']>unknown['fence']
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.authorize_node_command('{reconciler['id']}','{reconciler['workerId']}',{reconciler['fence']});SELECT automation.complete_node_command('{reconciler['id']}','{reconciler['workerId']}',{reconciler['fence']},'applied',NULL);")
    assert query(f"SELECT state FROM automation.node_commands WHERE id='{uncertain['id']}';")=='reconciled'
    query(f"UPDATE automation.execution_profiles SET enabled=true WHERE id='{profile}';INSERT INTO security.role_permissions SELECT id,'LaunchWorkloads' FROM security.roles WHERE code='viewer';")
    image_job=create('alice',{**request,'requestId':str(uuid4()),'operation':'swarm_job','profileId':profile,'profileVersion':1,'maximumAttempts':1})
    image_claim=json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_next('00000000-0000-0000-0000-000000000015',20)::text;"))
    assert image_claim['jobId']==image_job['id'] and not image_claim['mustStop'] and not image_claim['launchPrepared']
    reject(lambda:query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.authorize_workload('{image_claim['id']}',NULL,NULL,false);"),'job_fence')
    admission=f"SELECT automation.authorize_workload('{image_claim['id']}','{image_claim['workerId']}',{image_claim['fence']},false)"
    query('SET SESSION AUTHORIZATION automation_test_worker;'+admission+';')
    reject(lambda:query('SET SESSION AUTHORIZATION automation_test_worker;'+admission+';'),'job_fence')
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.record_service('{image_claim['id']}','{image_claim['workerId']}',{image_claim['fence']},'syntheticservice0000000001');")
    later=create('bob',{**request,'requestId':str(uuid4()),'operation':'swarm_job','profileId':profile,'profileVersion':1,'maximumAttempts':1})
    fair=json.loads(query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_next('{image_claim['workerId']}',20)::text;"))
    assert fair['jobId']==later['id'], 'An uncertain earlier launch starved another due job'
    query(f"UPDATE automation.jobs SET state='cancelled' WHERE id='{later['id']}';UPDATE automation.runs SET state='cancelled',finished_at=clock_timestamp() WHERE job_id='{later['id']}';")
    cleanup_job=create('alice',{**request,'requestId':str(uuid4()),'operation':'swarm_job','profileId':profile,'profileVersion':1,'maximumAttempts':1,'intervalSeconds':60})
    cleanup_run=json.loads(query("SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_next('00000000-0000-0000-0000-000000000019',20)::text;"))
    assert cleanup_run['jobId']==cleanup_job['id']
    next_due=query(f"SELECT ('{due}'::timestamptz+(floor(extract(epoch FROM(clock_timestamp()-'{due}'::timestamptz))/60)+1)*interval '60 seconds')::text;")
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.authorize_workload('{cleanup_run['id']}','{cleanup_run['workerId']}',{cleanup_run['fence']},false);SELECT automation.record_service('{cleanup_run['id']}','{cleanup_run['workerId']}',{cleanup_run['fence']},'syntheticcleanupservice001');SELECT automation.finish_run('{cleanup_run['id']}',{cleanup_run['fence']},'{cleanup_run['workerId']}',true,NULL,'{next_due}');")
    cleanup_edit={**edit_request,'requestId':str(uuid4()),'id':cleanup_job['id'],'expectedVersion':cleanup_job['version']+1,'dueAt':next_due,'maximumAttempts':1}
    cleanup_description=json.loads(user('alice','SELECT automation.job_list()::text'))['jobs']
    assert not next(j for j in cleanup_description if j['id']==cleanup_job['id'])['canEdit'], 'Terminal physical work awaiting cleanup became editable'
    reject(lambda:edit('alice',cleanup_edit),'job_transition')
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.cleanup_workload('{cleanup_run['id']}','syntheticcleanupservice001',true);")
    cleaned=edit('alice',cleanup_edit)
    user('alice',f"SELECT automation.control_job('{json.dumps(dict(requestId=str(uuid4()),id=cleaned['id'],action='cancel',expectedVersion=cleaned['version']))}'::jsonb)")
    print('PASS job amendments: current authority after waits/revocation, exact replay, optimistic versions, occurrence identity and terminal physical cleanup fence',flush=True)
    reject(lambda:node_command('admin',{**command,'requestId':str(uuid4())}),'infrastructure_busy')
    query(f"UPDATE automation.execution_profiles SET enabled=false WHERE id='{profile}';")
    rejected=json.loads(query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.claim_next('{image_claim['workerId']}',20)::text;"))
    assert rejected['mustStop'] and rejected['launchPrepared']
    reject(lambda:finish(rejected),'job_fence')
    def expired_worker_wait(sql,table,identifier):
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.5 seconds' WHERE principal_id='{worker}';")
        try:
            wait_behind_job_lock(lambda:reject(lambda:query('SET SESSION AUTHORIZATION automation_test_worker;'+sql),'worker_denied'),table,identifier)
        finally:
            query(f"UPDATE security.role_grants SET granted_to='9999-12-31' WHERE principal_id='{worker}';")
    expired_worker_wait(f"SELECT automation.record_service('{rejected['id']}','{rejected['workerId']}',{rejected['fence']},'syntheticservice0000000001');",'runs',rejected['id'])
    expired_worker_wait(f"SELECT automation.stop_workload('{rejected['id']}','{rejected['workerId']}',{rejected['fence']});",'jobs',image_job['id'])
    query(f"SET SESSION AUTHORIZATION automation_test_worker;SELECT automation.stop_workload('{rejected['id']}','{rejected['workerId']}',{rejected['fence']});")
    expired_worker_wait(f"SELECT automation.cleanup_workload('{rejected['id']}','syntheticservice0000000001',true);",'runs',rejected['id'])
    print('PASS service authority expiry during lock waits rejects recording, stopping and cleanup accounting',flush=True)
    assert query(f"SELECT state FROM automation.jobs WHERE id='{image_job['id']}';")=='blocked'
    print('PASS one-use image dispatch, profile revocation, retained service identity and cleanup authority',flush=True)
    if controller_once is not None:
        query(f"UPDATE automation.execution_profiles SET enabled=true WHERE id='{profile}';")
        actual=create('bob',{**request,'requestId':str(uuid4()),'operation':'swarm_job','profileId':profile,'profileVersion':1,'maximumAttempts':1})
        controller_once(profile)
        assert query(f"SELECT state FROM automation.jobs WHERE id='{actual['id']}';")=='completed'
        assert query(f"SELECT bool_and(service_removed) FROM automation.runs WHERE job_id='{actual['id']}';")=='t'
    # Completed jobs do not reserve capacity, but reactivation must acquire it.
    query(f"""INSERT INTO automation.jobs SELECT (jsonb_populate_record(NULL::automation.jobs,to_jsonb(j)||jsonb_build_object('id',gen_random_uuid(),'name','Synthetic quota job','state','active'))).* FROM automation.jobs j CROSS JOIN generate_series(1,64) WHERE j.id='{job}';
    INSERT INTO automation.jobs SELECT (jsonb_populate_record(NULL::automation.jobs,to_jsonb(j)||jsonb_build_object('id','00000000-0000-0000-0000-000000000088','state','completed'))).* FROM automation.jobs j WHERE j.id='{job}';""")
    reactivation=dict(requestId=str(uuid4()),id='00000000-0000-0000-0000-000000000088',action='run_again',expectedVersion=cancel['version'])
    reject(lambda:user('admin',f"SELECT automation.control_job('{json.dumps(reactivation)}'::jsonb)"),'job_limit')
    print('PASS owner quota applies to administrator reactivation',flush=True)
    quota_owner,quota_workspace=str(uuid4()),str(uuid4())
    quota_jobs=[str(uuid4()),str(uuid4())]
    query(f"""INSERT INTO security.workspaces VALUES('{tenant}','{quota_workspace}','synthetic_quota');
    INSERT INTO security.principals VALUES('{quota_owner}','human',true);
    INSERT INTO security.identity_bindings VALUES('https://automation.invalid','quota','{quota_owner}','human_interactive');
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{quota_owner}','quota','Synthetic quota owner',true,1),('{tenant}','{quota_workspace}','{quota_owner}','quota','Synthetic quota owner',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{quota_owner}',r.id,'{tenant}',w.id,clock_timestamp(),'9999-12-31',false FROM security.roles r CROSS JOIN security.workspaces w WHERE r.code='viewer' AND w.id IN('{workspace}','{quota_workspace}');
    INSERT INTO automation.jobs SELECT (jsonb_populate_record(NULL::automation.jobs,to_jsonb(j)||jsonb_build_object('id',gen_random_uuid(),'owner_id','{quota_owner}','state','active'))).* FROM automation.jobs j CROSS JOIN generate_series(1,63) WHERE j.id='{job}';
    INSERT INTO automation.jobs SELECT (jsonb_populate_record(NULL::automation.jobs,to_jsonb(j)||jsonb_build_object('id','{quota_jobs[0]}','owner_id','{quota_owner}','state','completed'))).* FROM automation.jobs j WHERE j.id='{job}';
    INSERT INTO automation.jobs SELECT (jsonb_populate_record(NULL::automation.jobs,to_jsonb(j)||jsonb_build_object('id','{quota_jobs[1]}','owner_id','{quota_owner}','workspace_id','{quota_workspace}','state','completed'))).* FROM automation.jobs j WHERE j.id='{job}';""")
    def reactivate(index):
        body=dict(requestId=str(uuid4()),id=quota_jobs[index],action='run_again',expectedVersion=cancel['version'])
        try:
            user('quota',f"SELECT automation.control_job('{json.dumps(body)}'::jsonb)",[workspace,quota_workspace][index])
            return 'admitted'
        except RuntimeError as error:
            assert 'job_limit' in str(error),str(error)
            return 'limited'
    with ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(reactivate,range(2)))==['admitted','limited']
    assert query(f"SELECT count(*) FROM automation.jobs WHERE owner_id='{quota_owner}' AND state='active';")=='64'
    query(f"INSERT INTO automation.jobs SELECT (jsonb_populate_record(NULL::automation.jobs,to_jsonb(j)||jsonb_build_object('id',gen_random_uuid(),'state','completed'))).* FROM automation.jobs j CROSS JOIN generate_series(1,50) WHERE j.id='{job}';")
    first_page=json.loads(user('admin','SELECT automation.job_list(0)::text'))
    second_page=json.loads(user('admin','SELECT automation.job_list(1)::text'))
    assert len(first_page['jobs'])==100 and first_page['total']>100
    assert not ({j['id'] for j in first_page['jobs']}&{j['id'] for j in second_page['jobs']})
    print('PASS concurrent cross-workspace reactivation obeys owner quota and administrator lists are paginated',flush=True)

    assert query('SET SESSION AUTHORIZATION automation_test_runtime;SELECT has_table_privilege(current_user,\'automation.nodes\',\'UPDATE\');')=='f'
    print('PASS shared infrastructure status, separate cluster permissions, durable node commands and revocation',flush=True)
    print('PASS durable jobs: ownership, administrator control, forced RLS, idempotency, claims, stale worker fencing, cancellation and current identity', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path)
    args = parser.parse_args()
    bins = args.pg_bin.resolve()
    directory=tempfile.mkdtemp(prefix='oetrm-jobs-',dir='/tmp')
    safe_to_remove=True
    try:
        root = Path(directory).resolve()
        environment = {k: v for k, v in os.environ.items() if not k.startswith('PG')}
        environment.update(PGHOST=str(root), PGPORT='55439', PGDATABASE='postgres', PGUSER='automation_owner', PGTZ='UTC')
        def run(command, **kw):
            return subprocess.run([str(v) for v in command], cwd=ROOT, env=environment,
                                  capture_output=True, text=True, timeout=30, **kw)
        initialized = run([bins/'initdb', '-D', root/'data', '--auth-local=trust', '--auth-host=reject', '--locale=C', '--encoding=UTF8', '--username=automation_owner'])
        if initialized.returncode:
            raise RuntimeError(initialized.stderr)
        try:
            safe_to_remove=False
            started = run([bins/'pg_ctl', '-D', root/'data', '-l', root/'postgres.log', '-w', '-o',
                           shlex.join(['-k', str(root), '-h', '', '-p', '55439', '-c', 'track_commit_timestamp=on']), 'start'])
            if started.returncode:
                raise RuntimeError(started.stderr)
            def query(sql):
                result = run([bins/'psql', '-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1'], input=sql)
                if result.returncode:
                    raise RuntimeError(result.stderr)
                return result.stdout.strip()
            for migration in sorted((ROOT/'db/migrations').glob('*.sql')):
                if migration.name == '0035_automation_jobs.sql':
                    query("INSERT INTO security.roles(id,code,name) VALUES(gen_random_uuid(),'synthetic_external_executor','Synthetic external executor');")
                query(migration.read_text())
                if migration.name == '0035_automation_jobs.sql':
                    assert query("SELECT count(*) FROM security.roles WHERE code='synthetic_external_executor';") == '1', 'The retained custom-role fixture is required'
                    assert query("SELECT count(*) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id WHERE r.code='synthetic_external_executor' AND p.permission='RunJobs';") == '0', 'Existing custom roles must not silently acquire scheduling authority'
            def worker_once():
                connection = root/'worker.connection'
                connection.write_text(f'Host={root};Port=55439;Database=postgres;Username=automation_test_worker;SSL Mode=Disable')
                connection.chmod(0o600)
                if not hasattr(worker_once,'host_checked'):
                    unsafe=root/'mixed-host.connection'
                    unsafe.write_text(f'Host={root},192.0.2.1;Port=55439;Database=postgres;Username=automation_test_worker;SSL Mode=Disable')
                    unsafe.chmod(0o600)
                    rejected=subprocess.run([str(args.dotnet.resolve()),str(ROOT/'src/OpenEtrm.Worker/bin/Debug/net10.0/OpenEtrm.Worker.dll'),'--once'],cwd=ROOT,env={**environment,'OPENETRM_WORKER_CONNECTION_FILE':str(unsafe),'OPENETRM_WORKER_POLL_SECONDS':'1','OPENETRM_WORKER_LEASE_SECONDS':'20'},text=True,capture_output=True,timeout=20)
                    assert rejected.returncode!=0 and 'single explicit PostgreSQL host' in rejected.stderr,rejected.stderr
                    worker_once.host_checked=True
                worker_env = {**environment, 'OPENETRM_WORKER_CONNECTION_FILE':str(connection),
                              'OPENETRM_WORKER_POLL_SECONDS':'1','OPENETRM_WORKER_LEASE_SECONDS':'20'}
                result = subprocess.run([str(args.dotnet.resolve()),str(ROOT/'src/OpenEtrm.Worker/bin/Debug/net10.0/OpenEtrm.Worker.dll'),'--once'],
                                        cwd=ROOT,env=worker_env,text=True,capture_output=True,timeout=20)
                assert result.returncode == 0, result.stderr
            def controller_once(profile):
                result=subprocess.run([str(args.dotnet.resolve()),str(ROOT/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0/OpenEtrm.Api.Checks.dll'),
                                       '--automation-controller',str(root/'worker.connection'),profile],cwd=ROOT,env=environment,text=True,capture_output=True,timeout=20)
                assert result.returncode==0,result.stderr
                print(result.stdout.strip(),flush=True)
            checks(query, worker_once if args.dotnet else None,controller_once if args.dotnet else None)
        finally:
            stopped = run([bins/'pg_ctl', '-D', root/'data', '-w', '-m', 'fast', 'stop'])
            if stopped.returncode:
                raise RuntimeError('Owned automation database did not stop; files retained at '+str(root)+': '+stopped.stderr)
            safe_to_remove=True
    finally:
        if safe_to_remove:
            shutil.rmtree(directory)


if __name__ == '__main__':
    main()
