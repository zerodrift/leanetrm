"""Exercise an actual report commit whose worker finish acknowledgement is lost."""
from pathlib import Path
import json
import subprocess
import time
from uuid import uuid4
from owned_process import stop_owned_group


def run_checks(owner,body,query,psql,environment,worker_command,worker_environment,repository):
    request={**body,'schedule':{**body['schedule'],'requestId':str(uuid4()),'name':'Synthetic report acknowledgement recovery'}}
    job=owner.call('jobs/pilot-close/create',request)
    query(f"UPDATE automation.jobs SET next_due_at='2020-12-20T16:00:00Z' WHERE id='{job['id']}';")
    suffix=uuid4().hex;function='synthetic_finish_wait_'+suffix;trigger='synthetic_finish_wait_'+suffix
    key=uuid4().int%((1<<62)-1)+1
    locker=None;process=None;waiting_backend=None
    def finish_backend():
        if waiting_backend is None:return
        # Only this owned fixture's exact connection incarnation may be
        # terminated. PostgreSQL need not notice a killed client while it is
        # waiting for our advisory lock, so do not release that lock first.
        identity=f"a.pid={waiting_backend['pid']} AND a.backend_start='{waiting_backend['backendStart']}'::timestamptz AND a.query_start='{waiting_backend['queryStart']}'::timestamptz AND a.datname=current_database() AND a.usename='pilot_fixture_scheduler' AND a.application_name='synthetic-pilot-worker' AND a.backend_type='client backend'"
        query(f"SELECT pg_terminate_backend(a.pid) FROM pg_stat_activity a WHERE {identity} AND EXISTS(SELECT 1 FROM pg_locks l WHERE l.pid=a.pid AND l.locktype='advisory' AND NOT l.granted AND l.classid={key>>32} AND l.objid={key&0xffffffff});")
        deadline=time.monotonic()+10
        while time.monotonic()<deadline:
            if query(f"SELECT count(*) FROM pg_stat_activity a WHERE {identity};")=='0':return
            time.sleep(.03)
        raise AssertionError('The exact owned finish backend did not terminate before fixture cleanup')
    try:
        # Owned fixture instrumentation only: the deployed worker and financial
        # publisher remain unchanged. The finish transaction waits after the
        # report's separate transaction has committed.
        query(f"CREATE FUNCTION automation.{function}() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN PERFORM pg_advisory_xact_lock({key});RETURN NEW;END$$;CREATE TRIGGER {trigger} BEFORE UPDATE OF state ON automation.runs FOR EACH ROW WHEN(NEW.job_id='{job['id']}' AND NEW.state='succeeded') EXECUTE FUNCTION automation.{function}();")
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        locker.stdin.write(f'BEGIN;SELECT pg_advisory_xact_lock({key});\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        process=subprocess.Popen(worker_command,cwd=Path(repository),env=worker_environment,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,start_new_session=True)
        blocked=False
        deadline=time.monotonic()+60
        while time.monotonic()<deadline:
            candidates=json.loads(query(f"SELECT coalesce(jsonb_agg(jsonb_build_object('pid',a.pid,'backendStart',a.backend_start,'queryStart',a.query_start)),'[]'::jsonb)::text FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND a.usename='pilot_fixture_scheduler' AND a.application_name='synthetic-pilot-worker' AND a.backend_type='client backend' AND a.query LIKE '%automation.finish_run%' AND l.locktype='advisory' AND NOT l.granted AND l.classid={key>>32} AND l.objid={key&0xffffffff};"))
            if candidates:
                assert len(candidates)==1,'More than one owned finish backend waited on the unique fixture key'
                waiting_backend=candidates[0];blocked=True;break
            if process.poll() is not None:raise AssertionError('Worker exited before its report finish boundary: '+''.join(process.communicate()))
            time.sleep(.02)
        if not blocked:
            state=query(f"SELECT coalesce(jsonb_agg(jsonb_build_object('state',r.state,'error',r.error_code,'reportCommitted',EXISTS(SELECT 1 FROM automation.pilot_reports s WHERE s.run_id=r.id))),'[]'::jsonb)::text FROM automation.runs r WHERE r.job_id='{job['id']}';")
            raise AssertionError('The actual worker did not reach its controlled finish acknowledgement boundary: '+state)
        run=query(f"SELECT id FROM automation.runs WHERE job_id='{job['id']}';")
        report=query(f"SELECT request_sha256 FROM automation.pilot_reports WHERE run_id='{run}';")
        marks=query(f"SELECT request::text FROM market_data.market_closes WHERE run_id='{run}';")
        assert len(report)==64 and marks
        assert query(f"SELECT state FROM automation.runs WHERE id='{run}';")=='running'
        stop_owned_group(process)
        finish_backend()
    finally:
        if process is not None and process.poll() is None:stop_owned_group(process)
        finish_backend()
        if locker is not None and locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
            assert locker.returncode==0,errors
        query(f"DROP TRIGGER IF EXISTS {trigger} ON automation.runs;DROP FUNCTION IF EXISTS automation.{function}();")
    assert query(f"SELECT state FROM automation.runs WHERE id='{run}';")=='running','The killed finish transaction unexpectedly committed'
    _finish_expiry(query,run,job['id'],psql,environment)
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{run}';")
    completed=subprocess.run(worker_command,cwd=Path(repository),env=worker_environment,capture_output=True,text=True,timeout=90)
    assert completed.returncode==0,completed.stdout+completed.stderr
    assert query(f"SELECT state FROM automation.runs WHERE id='{run}';")=='succeeded'
    assert query(f"SELECT request_sha256 FROM automation.pilot_reports WHERE run_id='{run}';")==report
    assert query(f"SELECT request::text FROM market_data.market_closes WHERE run_id='{run}';")==marks
    assert query(f"SELECT count(*) FROM automation.pilot_reports WHERE run_id='{run}';")=='1'
    print('PASS pilot report recovery: committed exact report and marks survive a killed finish transaction and are reused by the replacement worker',flush=True)


def _finish_expiry(query,run,job,psql,environment):
    """The real finish boundary must recheck its lease after an UPDATE wait."""
    suffix=uuid4().hex;function='synthetic_finish_expiry_'+suffix;trigger=function
    claim=json.loads(query(f"SELECT jsonb_build_object('fence',fence,'worker',worker_id,'live',deadline_at>clock_timestamp()+interval '20 seconds')::text FROM automation.runs WHERE id='{run}';"))
    assert claim['live'],'The finish lease fixture needs an independently live deadline'
    before=query(f"SELECT jsonb_build_object('run',r.state,'job',j.state,'version',j.version)::text FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id WHERE r.id='{run}';")
    try:
        query(f"CREATE FUNCTION automation.{function}() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN RAISE NOTICE 'synthetic_finish_wait_entered';PERFORM pg_sleep(9);IF OLD.lease_until>clock_timestamp() THEN RAISE EXCEPTION 'fixture_lease_not_expired';END IF;RETURN NEW;END$$;CREATE TRIGGER {trigger} BEFORE UPDATE OF state ON automation.runs FOR EACH ROW WHEN(NEW.job_id='{job}' AND NEW.state='succeeded') EXECUTE FUNCTION automation.{function}();")
        query(f"UPDATE automation.runs SET lease_until=clock_timestamp()+interval '8 seconds' WHERE id='{run}';")
        command=f"\\set VERBOSITY verbose\nSET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.finish_run('{run}',{claim['fence']},'{claim['worker']}',true,NULL,NULL);\n"
        completed=subprocess.run(psql,env=environment,input=command,capture_output=True,text=True,timeout=20)
        assert 'synthetic_finish_wait_entered' in completed.stderr,'The finish did not pass its initial guards and reach the controlled UPDATE wait: '+completed.stdout+completed.stderr
        assert completed.returncode!=0 and '42501' in completed.stderr,'A pilot finish committed after its actual lease expired during UPDATE: '+completed.stdout+completed.stderr
        assert query(f"SELECT jsonb_build_object('run',r.state,'job',j.state,'version',j.version)::text FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id WHERE r.id='{run}';")==before,'The refused finish changed the occurrence or job'
    finally:
        query(f"DROP TRIGGER IF EXISTS {trigger} ON automation.runs;DROP FUNCTION IF EXISTS automation.{function}();")
    print('PASS pilot finish: valid exact scheduler call waits through natural lease expiry and rolls back its complete finish transaction',flush=True)
