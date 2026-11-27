"""Read-only worker replay of already captured economic reports in an owned fixture."""
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4

from check_risk_jobs_db import provision_worker


def provision(query,manifest,login,*,permissions):
    # Risk analyst reads owned gas as installed (D460); this worker holds it only where `permissions` says so.
    provision_worker(query,manifest,login,without=('ReadGasOwnedHoldings',))
    for permission in permissions:
        assert permission in ('ReadGasOwnedHoldings',)
        role=str(uuid4())
        query(f"INSERT INTO security.roles VALUES('{role}','synthetic_native_reader_{role.replace('-','')}','Synthetic native economic source reader');"
              f"INSERT INTO security.role_permissions VALUES('{role}','{permission}');"
              f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false FROM risk.workers WHERE login='{login}';")


def run_saved(query, manifest, packet, frozen, login, *, permissions, expected_jobs,verify_mode='--economic-worker-verify'):
    directory=Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory==packet.parent.resolve() and directory.name.startswith('oetrm-ref-')
    receipts=json.loads(packet.with_suffix('.economic-worker.json').read_text())
    assert len(receipts['jobs'])==expected_jobs
    assert query(f"SELECT NOT has_function_privilege('{login}','security.require_gas_owned_preview(uuid,timestamptz)','EXECUTE');")=='t'
    stamps={p.name:p.read_bytes() for p in frozen.glob('OpenEtrm.*.dll')}
    counts="SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.futures_margin_runs),(SELECT count(*) FROM trading.gas_owned_positions),(SELECT count(*) FROM trading.gas_futures_delivery_performances),(SELECT count(*) FROM trading.gas_futures_delivery_principal_runs))::text;"
    before=query(counts)
    connection=directory/(login+'-connection')
    connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2');connection.chmod(0o600)
    config=directory/(login+'-config.json')
    config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)));config.chmod(0o600)
    environment={key:value for key,value in os.environ.items() if not key.startswith('OPENETRM_')}
    environment['OPENETRM_RISK_WORKER_CONFIG']=str(config)
    root=Path(__file__).resolve().parents[1];dotnet=os.environ.get('DOTNET','dotnet')
    for saved in receipts['jobs']:
        assert query(f"SELECT risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';")==('t' if 'ReadGasOwnedHoldings' in permissions else 'f')
        result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=root,env=environment,capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
        actual=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state']=='completed',dict(state=actual['state'],error=actual['error'],workerDiagnostics=result.stdout+result.stderr)
        if actual['result']!=saved['report']:
            differences=[]
            def compare(expected,observed,path='$'):
                if len(differences)>=20:return
                if isinstance(expected,dict) and isinstance(observed,dict):
                    for key in sorted(expected.keys()|observed.keys()):
                        if key not in expected or key not in observed:differences.append((path+'.'+key,'field presence differs'))
                        else:compare(expected[key],observed[key],path+'.'+key)
                elif isinstance(expected,list) and isinstance(observed,list):
                    if len(expected)!=len(observed):differences.append((path,'array lengths',len(expected),len(observed)))
                    for index,(left,right) in enumerate(zip(expected,observed)):compare(left,right,f'{path}[{index}]')
                elif expected!=observed:differences.append((path,repr(expected)[:160],repr(observed)[:160]))
            compare(saved['report'],actual['result'])
            raise AssertionError(dict(state=actual['state'],differences=differences,workerDiagnostics=result.stdout+result.stderr))
    result=subprocess.run([dotnet,str(frozen/'OpenEtrm.Api.Checks.dll'),verify_mode,str(packet)],cwd=root,capture_output=True,text=True,timeout=90)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    assert query(counts)==before,'An economic worker changed accepted financial or market facts.'
    assert all((frozen/name).read_bytes()==value for name,value in stamps.items()),'The retained worker engine changed after admission.'
    print('PASS actual native risk workers: exact captured reports, retained ownership/source/history, unchanged engine and no financial writes',flush=True)
