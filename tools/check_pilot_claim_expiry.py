"""An owned scheduler must recheck financial authority after a run-row wait."""
from concurrent.futures import ThreadPoolExecutor
import json
import subprocess
import time
from uuid import uuid4


def run_checks(query,manifest,psql,environment,claim):
    service=query("SELECT principal_id FROM automation.workers WHERE login='pilot_fixture_scheduler';")
    role=query("SELECT id FROM security.roles WHERE code='synthetic_pilot_operator';")
    temporary,grant=str(uuid4()),str(uuid4())
    before=query(f"SELECT jsonb_build_object('fence',fence,'attempt',attempt,'worker',worker_id,'login',worker_login)::text FROM automation.runs WHERE id='{claim['id']}';")
    locker=None
    try:
        query(f"""INSERT INTO security.roles(id,code,name) VALUES('{temporary}','synthetic_expiring_pilot_reader','Synthetic expiring pilot reader');
        INSERT INTO security.role_permissions VALUES('{temporary}','ReadMarketData');
        DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='ReadMarketData';
        INSERT INTO security.role_grants VALUES('{grant}','{service}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false);
        UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{claim['id']}';""")
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        locker.stdin.write(f"BEGIN;SELECT id FROM automation.runs WHERE id='{claim['id']}' FOR UPDATE;\n\\echo LOCKED\n");locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '8 seconds' WHERE id='{grant}';")
        sql=f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SET application_name='synthetic_pilot_claim_expiry';SET statement_timeout='15s';SELECT automation.claim_next('{uuid4()}',120,true)::text;"
        with ThreadPoolExecutor(max_workers=1) as pool:
            request=pool.submit(subprocess.run,psql,input=sql,env=environment,capture_output=True,text=True,timeout=25)
            try:
                for _ in range(200):
                    if query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic_pilot_claim_expiry' AND NOT l.granted AND l.locktype='transactionid';")=='1':break
                    if request.done():raise AssertionError(request.result().stderr+request.result().stdout)
                    time.sleep(.03)
                else:raise AssertionError('The financial claim did not wait for the controlled existing run row')
                for _ in range(200):
                    if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
                    time.sleep(.05)
                else:raise AssertionError('The independent service market-read grant did not expire')
            finally:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
                assert locker.returncode==0,errors
            result=request.result(timeout=20)
        assert result.returncode==0,result.stderr
        returned=json.loads(result.stdout) if result.stdout.strip() else None
        assert returned is None, 'A financial claim consumed a replacement lease after service ReadMarketData expired during its run-row wait'
        after=query(f"SELECT jsonb_build_object('fence',fence,'attempt',attempt,'worker',worker_id,'login',worker_login)::text FROM automation.runs WHERE id='{claim['id']}';")
        assert after==before,'The rejected financial claim changed its existing run fence or ownership'
        permissions=query(f"SELECT security.principal_has_permission('{service}','{manifest['tenantId']}','{manifest['workspaceId']}','RunJobs',clock_timestamp()) AND NOT security.principal_has_permission('{service}','{manifest['tenantId']}','{manifest['workspaceId']}','ReadMarketData',clock_timestamp());")
        assert permissions=='t','The test must expire only the independent service market-read grant'
    finally:
        if locker is not None and locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadMarketData') ON CONFLICT DO NOTHING;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';")
    print('PASS pilot financial claim: independent service permission expiry after existing-run lock wait rejects replacement without changing fence or ownership',flush=True)
