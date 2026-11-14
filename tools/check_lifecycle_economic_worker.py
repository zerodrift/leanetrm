"""Run real economic historical/Monte Carlo workers against owned native gas facts."""
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import time
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_holdings import checks as gas_checks
from check_risk_jobs_db import provision_worker

ROOT=Path(__file__).resolve().parents[1]


def stop_owned(process):
    if process is None or process.poll() is not None:return
    process.terminate()
    try:process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill();process.wait(timeout=5)


def worker_checks(query, manifest, fixture):
    directory=Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory==fixture.parent.resolve() and directory.name.startswith('oetrm-ref-')
    dotnet=os.environ.get('DOTNET','dotnet')
    frozen=directory/'economic-worker-binaries'
    shutil.copytree(ROOT/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    binary_stamps={p.name:p.read_bytes() for p in frozen.glob('OpenEtrm.*.dll')}
    login='synthetic_economic_worker';without_gas='synthetic_economic_no_gas_worker'
    provision_worker(query,manifest,login);provision_worker(query,manifest,without_gas)
    role=str(uuid4());tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    query(f"INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_economic_gas_reader','Synthetic economic gas reader');"
          f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');"
          f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM risk.workers WHERE login='{login}';")
    assert query(f"SELECT security.principal_has_permission(principal_id,tenant_id,workspace_id,'ReadGasOwnedHoldings',clock_timestamp()) FROM risk.workers WHERE login='{without_gas}';")=='f'
    assert query(f"SELECT NOT has_function_privilege('{login}','security.require_gas_owned_preview(uuid,timestamptz)','EXECUTE');")=='t','The read-only source consumer must not acquire interactive preview authority'

    def managed(mode):
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.Checks.dll'),mode,str(fixture)],cwd=ROOT,capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        print(result.stdout.strip(),flush=True)
    managed('--economic-worker-submit')
    receipts=json.loads(fixture.with_suffix('.economic-worker.json').read_text())
    ids=[r['id'] for r in receipts['jobs']]
    assert len(ids)==3 and len(set(ids))==3
    listed=','.join("'"+value+"'::uuid" for value in ids)
    assert query(f"SELECT bool_and(request->>'engineVersion'='{receipts['engineVersion']}' AND risk.needs_gas(j) AND frame?'economicLifecycle' AND frame?'factorHistory') FROM risk.jobs j WHERE id=ANY(ARRAY[{listed}]);")=='t'
    assert query(f"SELECT count(*) FROM risk.job_scenario_axes WHERE job_id='{ids[0]}';")=='1'
    assert query(f"SELECT count(*) FROM risk.job_lifecycle_final_inputs WHERE job_id='{ids[0]}';")=='0'
    sources=query("SELECT jsonb_build_object('observations',(SELECT count(*) FROM market_data.observations),'tradingPublications',(SELECT count(*) FROM trading.publications),'ownedPositions',(SELECT count(*) FROM trading.gas_owned_positions))::text;")

    def environment(worker):
        connection=directory/(worker+'-connection')
        connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={worker};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2')
        connection.chmod(0o600)
        config=directory/(worker+'.json')
        config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)))
        config.chmod(0o600)
        return {**os.environ,'OPENETRM_RISK_WORKER_CONFIG':str(config)}
    def run(worker=login):
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=ROOT,env=environment(worker),capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        return result.stdout+result.stderr
    run(without_gas)
    assert query(f"SELECT bool_and(state='queued' AND attempt=0) FROM risk.jobs WHERE id=ANY(ARRAY[{listed}]);")=='t', 'A worker without selected gas authority claimed an economic job'
    print('PASS actual worker claim preserves conditional gas authority before any owned calculation attempt',flush=True)
    for id in ids[:2]:
        output=run()
        state=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{id}';"))
        assert state['state']=='completed' and state['error'] is None,state|{'workerDiagnostics':output}
        expected=next(r['report'] for r in receipts['jobs'] if r['id']==id)
        assert state['result']==expected,'Actual economic worker report differs from its complete interactive source/history oracle'
    print('PASS actual managed historical and Monte Carlo workers reproduce complete native-owner reports and immutable event/source evidence',flush=True)

    # Hold only the native observations relation. Claim/bind and cancellation
    # remain available; the real worker stops at its first source fact read.
    psql=Path(os.sys.argv[os.sys.argv.index('--pg-bin')+1])/'psql'
    owner_env={k:v for k,v in os.environ.items() if not k.startswith('PG')}
    owner_env.update(PGHOST=str(directory),PGPORT='55439',PGDATABASE='postgres',PGUSER='preview_owner',PGTZ='UTC')
    holder=subprocess.Popen([str(psql),'-X','-qAt','-v','ON_ERROR_STOP=1'],cwd=ROOT,env=owner_env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    process=None
    try:
        holder.stdin.write("BEGIN;LOCK market_data.observations IN ACCESS EXCLUSIVE MODE;SELECT 'economic-source-held';\n");holder.stdin.flush()
        readable,_,_=select.select([holder.stdout],[],[],5)
        assert readable and holder.stdout.readline().strip()=='economic-source-held','The owned source relation lock was not acquired'
        process=subprocess.Popen([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=ROOT,env=environment(login),stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        deadline=time.monotonic()+8
        while time.monotonic()<deadline:
            if process.poll() is not None:raise AssertionError('Worker ended before the controlled source-read cancellation: '+''.join(process.communicate()))
            waiting=query(f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE usename='{login}' AND wait_event_type='Lock');")=='t'
            running=query(f"SELECT state FROM risk.jobs WHERE id='{ids[2]}';")=='running'
            if waiting and running:break
            time.sleep(.02)
        else:raise AssertionError('The real worker did not enter its controlled native source read')
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SET LOCAL lock_timeout='1s';SELECT security.use_context('{token}');SELECT risk.cancel('{ids[2]}');COMMIT;")
        assert query(f"SELECT state FROM risk.jobs WHERE id='{ids[2]}';")=='cancellation_requested'
        holder.stdin.write('ROLLBACK;\n');holder.stdin.close();holder.wait(timeout=5)
        stdout,stderr=process.communicate(timeout=30)
        assert process.returncode==0,stdout+stderr
        cancelled=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{ids[2]}';"))
        assert cancelled['state']=='cancelled' and cancelled['result'] is None,cancelled|{'workerDiagnostics':stdout+stderr}
    finally:
        if holder.poll() is None:
            try:holder.stdin.write('ROLLBACK;\n');holder.stdin.close()
            except (BrokenPipeError,ValueError):pass
            try:holder.wait(timeout=5)
            except subprocess.TimeoutExpired:stop_owned(holder)
        stop_owned(process)
    assert sources==query("SELECT jsonb_build_object('observations',(SELECT count(*) FROM market_data.observations),'tradingPublications',(SELECT count(*) FROM trading.publications),'ownedPositions',(SELECT count(*) FROM trading.gas_owned_positions))::text;"), 'Hypothetical calculation persisted a market or lifecycle fact'
    assert all((frozen/name).read_bytes()==value for name,value in binary_stamps.items()),'The admitted engine binaries changed during worker qualification'
    managed('--economic-worker-verify')
    print('PASS actual economic worker cancellation during source loading publishes no result or market/lifecycle facts; frozen binaries retain the admitted engine identity',flush=True)


def checks(query, manifest):
    gas_checks(query,manifest,economic_worker=worker_checks)


if __name__=='__main__':
    dotnet=os.environ.get('DOTNET','dotnet')
    result=subprocess.run([dotnet,'build','tests/OpenEtrm.Api.Checks'],cwd=ROOT,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    main(checks=checks)
