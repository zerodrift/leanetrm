"""Financial job details must retain authority through their payload read."""
from concurrent.futures import ThreadPoolExecutor
import json
import subprocess
import time
from uuid import uuid4


def _wait_for_payload(query,psql,environment,sql,arm_expiry,is_expired):
    locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    try:
        locker.stdin.write('BEGIN;LOCK TABLE automation.pilot_close_books IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        query(arm_expiry)
        with ThreadPoolExecutor(max_workers=1) as pool:
            request=pool.submit(subprocess.run,psql,input=sql,env=environment,capture_output=True,text=True,timeout=25)
            try:
                for _ in range(200):
                    if query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic_pilot_definition_expiry' AND NOT l.granted AND l.relation='automation.pilot_close_books'::regclass;")=='1':break
                    if request.done():raise AssertionError(request.result().stderr+request.result().stdout)
                    time.sleep(.03)
                else:raise AssertionError('The financial definition did not reach the controlled normalized book read')
                for _ in range(200):
                    if query(is_expired)=='t':break
                    time.sleep(.05)
                else:raise AssertionError('The independent financial definition authority did not expire')
            finally:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
                assert locker.returncode==0,errors
            return request.result(timeout=20)
    finally:
        if locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)


def run_checks(query,manifest,psql,environment,job):
    owner=query(f"SELECT owner_id FROM automation.jobs WHERE id='{job}';")
    roles=json.loads(query(f"SELECT coalesce(jsonb_agg(DISTINCT p.role_id),'[]') FROM security.role_permissions p JOIN security.role_grants g ON g.role_id=p.role_id WHERE g.principal_id='{owner}' AND p.permission='ReadTrades';"))
    temporary,grant=str(uuid4()),str(uuid4())
    try:
        query(f"""INSERT INTO security.roles(id,code,name) VALUES('{temporary}','synthetic_expiring_profile_reader','Synthetic expiring profile reader');
        INSERT INTO security.role_permissions VALUES('{temporary}','ReadTrades');
        DELETE FROM security.role_permissions WHERE permission='ReadTrades' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{owner}');
        INSERT INTO security.role_grants VALUES('{grant}','{owner}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false);""")
        def token():
            return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        active=token()
        positive=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{active}');SELECT automation.pilot_job_definition('{job}') IS NOT NULL;ROLLBACK;")
        assert positive.splitlines()[-1:] == ['t'],'The authorized financial profile was unavailable before the wait'
        active=token()
        sql=f"""SET SESSION AUTHORIZATION preview_runtime;SET application_name='synthetic_pilot_definition_expiry';BEGIN;SET LOCAL statement_timeout='15s';
        SELECT security.use_context('{active}');
        DO $$BEGIN PERFORM automation.pilot_job_definition('{job}');
        RAISE EXCEPTION 'Expired job reader returned financial profile';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;
        SELECT NOT security.has_permission('ReadTrades') AND security.has_permission('RunJobs') AND security.has_permission('ReadMarketData')
        AND security.has_permission('RunValuation') AND security.has_permission('RunScenario');ROLLBACK;"""
        result=_wait_for_payload(query,psql,environment,sql,
            f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '8 seconds' WHERE id='{grant}';",
            f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")
        assert result.returncode==0 and result.stdout.splitlines()[-1:]==['t'],result.stderr+result.stdout
        assert query("SELECT has_function_privilege('preview_runtime','automation.require_pilot_job_definition(uuid)','EXECUTE');")=='f','The private financial profile guard was granted directly to runtime callers'
    finally:
        restored=''.join(f"INSERT INTO security.role_permissions VALUES('{role}','ReadTrades') ON CONFLICT DO NOTHING;" for role in roles)
        query(restored+f"DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';")
    print('PASS pilot profile: natural ReadTrades expiry during normalized payload wait rejects details while all other required rights remain active',flush=True)


def run_context_checks(query,manifest,psql,environment,claim,token):
    run=claim['id']
    original=query(f"SELECT lease_until::text FROM automation.runs WHERE id='{run}';")
    before=query(f"SELECT jsonb_build_object('fence',fence,'attempt',attempt,'worker',worker_id,'login',worker_login)::text FROM automation.runs WHERE id='{run}';")
    sql=f"""SET SESSION AUTHORIZATION pilot_fixture_runtime;SET application_name='synthetic_pilot_definition_expiry';BEGIN;SET LOCAL statement_timeout='15s';
    SELECT security.use_context('{token}');
    DO $$BEGIN PERFORM automation.pilot_run_definition();RAISE EXCEPTION 'Expired run returned financial definition';
    EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;SELECT NOT security.has_permission('RunJobs');ROLLBACK;"""
    try:
        result=_wait_for_payload(query,psql,environment,sql,
            f"UPDATE automation.runs SET lease_until=clock_timestamp()+interval '8 seconds' WHERE id='{run}';",
            f"SELECT lease_until<=clock_timestamp() FROM automation.runs WHERE id='{run}';")
        assert result.returncode==0 and result.stdout.splitlines()[-1:]==['t'],result.stderr+result.stdout
        service=query("SELECT principal_id FROM automation.workers WHERE login='pilot_fixture_scheduler';")
        assert query(f"SELECT bool_and(security.principal_has_permission('{service}','{manifest['tenantId']}','{manifest['workspaceId']}',p,clock_timestamp())) FROM unnest(ARRAY['RunJobs','ReadMarketData','ReadTrades','RunValuation','RunScenario'])p;")=='t','The run test must expire its lease while retaining service permissions'
        assert query(f"SELECT jsonb_build_object('fence',fence,'attempt',attempt,'worker',worker_id,'login',worker_login)::text FROM automation.runs WHERE id='{run}';")==before,'A rejected definition read changed the run fence or ownership'
    finally:
        query(f"UPDATE automation.runs SET lease_until='{original}' WHERE id='{run}';")
    print('PASS pilot run definition: natural lease expiry during normalized payload wait rejects details without changing claim ownership',flush=True)
