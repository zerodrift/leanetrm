"""Full Lifecycle UI over this tree's API and an owned native synthetic database."""
import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import time
from urllib.request import urlopen
from uuid import uuid4

from check_reference_editing import main as native_main
from check_lifecycle_scheduling import fixture, configured
from check_futures_physical_delivery import checks as physical_checks
from owned_process import stop_owned_groups

ROOT=Path(__file__).resolve().parents[1]


def run(query,manifest,dotnet,browser,children):
    def prepared(query,manifest,setup):
        saved=configured(query,manifest,setup)
        catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        def call(sql):
            token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
        booked=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{setup['bookId']}','{manifest['revisionId']}',"
            f"'nymex_ng','{setup['contractId']}','2026-10-16T14:00:00Z',NULL,2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(setup['booking'])})::text;"))
        adoption={**setup['adoption'],'operationId':str(uuid4()),'tradeId':booked['tradeId'],'bindingId':booked['bindingId'],
            'contextId':call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")}
        call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption)});")
        close_at=query(f"SELECT to_char(at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{saved['closeId']}');")
        account=setup['profile']['accountId'];title_key='synthetic_browser_opening_title';title_policy=str(uuid4())
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=title_policy,expectedRevision=0,action='gas.owned.receive_title',mode='review',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        for command,body in [('bind_policy',dict(action='gas.owned.receive_title',policyId=title_policy,policyRevision=1,effectiveAt='2026-10-01T00:00:00Z')),
            ('evidence',dict(sourceKey=title_key,kind='title_receipt',quantity='0.125',occurredAt=close_at,evidence='Synthetic independently observed opening title',expectedRevision=0))]:
            context=call(f"SELECT security.capture_date_context('{catalog}','{close_at[:10]}',NULL);")
            call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{command}','{account}',{literal(body)},'{close_at}','{context}',NULL,NULL,false);")
        root=Path(query('SHOW unix_socket_directories;')).resolve()
        run_id=str(uuid4());(root/'.openetrm-preview').write_text(run_id)
        (root/'manifest.json').write_text(json.dumps(manifest));(root/'manifest.json').chmod(0o600)
        shutil.copyfile(ROOT/'deploy/process-limits.example.json',root/'process-limits.json')
        shutil.copytree(ROOT/'web/dist',root/'web')
        shutil.copytree(ROOT/'src/OpenEtrm.Api/bin/Debug/net10.0',root/'api')
        shutil.copytree(ROOT/'src/OpenEtrm.Worker/bin/Debug/net10.0',root/'worker')
        environment={k:v for k,v in os.environ.items() if not k.startswith(('PG','OPENETRM_','ASPNETCORE_','DOTNET_'))}
        environment.update(OPENETRM_PREVIEW_ROOT=str(root),OPENETRM_PREVIEW_WEB=str(root/'web'),OPENETRM_BROWSER_ACTION_WAIT_MS='30000')
        for variable,name in [('OPENETRM_PREVIEW_CREDENTIAL_KEY','credential.key'),('OPENETRM_PREVIEW_CREDENTIAL_RETRY_KEY','credential-retry.key')]:
            path=root/name;path.write_bytes(secrets.token_bytes(32));path.chmod(0o600);environment[variable]=str(path)
        def connection(login):return f'Host={root};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=5;Maximum Pool Size=2'
        environment.update(OPENETRM_PREVIEW_RUNTIME=connection('preview_runtime'),OPENETRM_PREVIEW_AUTHENTICATOR=connection('preview_authenticator'))
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1',0));port=reservation.getsockname()[1]
        environment['OPENETRM_PREVIEW_PORT']=str(port);url=f'http://127.0.0.1:{port}'
        for name,login in [('scheduler-connection','pilot_fixture_scheduler'),('runtime-connection','pilot_fixture_runtime')]:
            path=root/name;path.write_text(connection(login));path.chmod(0o600)
        worker_environment={**environment,'OPENETRM_WORKER_CONNECTION_FILE':str(root/'scheduler-connection'),
            'OPENETRM_PILOT_RUNTIME_CONNECTION_FILE':str(root/'runtime-connection'),'OPENETRM_PILOT_MANIFEST_FILE':str(root/'manifest.json'),
            'OPENETRM_WORKER_POLL_SECONDS':'1','OPENETRM_WORKER_LEASE_SECONDS':'30'}
        target=root/'browser-target.json';target.write_text(json.dumps(dict(suite='business_browser_v1',runId=run_id,url=url,
            lifecycle=dict(closeId=saved['closeId'],tradeId=booked['tradeId'],reference=query(f"SELECT 'TR-'||lpad(reference_number::text,6,'0') FROM trading.trade_references WHERE(catalog_id,trade_id)=('{catalog}','{booked['tradeId']}');"),reviewJobId=saved['reviewJobId'],
                gasAccountCode='synthetic_physical_delivery',titleSourceKey=title_key,titleAt=close_at))));target.chmod(0o600)
        try:
            with (root/'api.log').open('w') as api_log,(root/'worker.log').open('w') as worker_log:
                api=subprocess.Popen([str(dotnet),str(root/'api/OpenEtrm.Api.dll'),'--local-preview'],cwd=ROOT,env=environment,stdout=api_log,stderr=subprocess.STDOUT,start_new_session=True);children.append(api)
                deadline=time.monotonic()+25
                while True:
                    if api.poll() is not None:raise RuntimeError((root/'api.log').read_text())
                    try:
                        with urlopen(url+'/api/health',timeout=.5) as response:health=json.load(response)
                        assert health['runId']==run_id;break
                    except OSError:
                        if time.monotonic()>deadline:raise RuntimeError('The owned API did not start.\n'+(root/'api.log').read_text())
                        time.sleep(.1)
                worker=subprocess.Popen([str(dotnet),str(root/'worker/OpenEtrm.Worker.dll')],cwd=ROOT,env=worker_environment,stdout=worker_log,stderr=subprocess.STDOUT,start_new_session=True);children.append(worker)
                chrome=subprocess.Popen(['node',str(ROOT/'tests/browser/lifecycle.mjs'),str(target),str(browser)],cwd=ROOT,env=environment,start_new_session=True);children.append(chrome)
                if chrome.wait(timeout=240):
                    diagnostic='\n'.join(line for line in (root/'postgres.log').read_text().splitlines() if 'ERROR:' in line or 'CONTEXT:' in line or line.startswith('\tPL/pgSQL function') or line.startswith('\tSQL statement'))
                    raise RuntimeError('The full-app lifecycle browser check failed.\n'+(root/'api.log').read_text()+'\n'+(root/'worker.log').read_text()+'\n'+diagnostic[-16000:])
                assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_obligations WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{booked['tradeId']}','{booked['bindingId']}');")=='1'
                assert query(f"SELECT count(*) FROM trading.gas_owned_lots l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.id) JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) WHERE l.catalog_id='{catalog}' AND e.source_key='{title_key}' AND p.quantity=0.125;")=='1'
                for name in ['lifecycle-desktop.png','lifecycle-mobile.png','lifecycle-physical-review.png','lifecycle-gas-review.png']:
                    shutil.copyfile(root/name,Path('/private/tmp')/name)
                print('PASS full app Lifecycle browser: real API, shared financial action, local time, scheduled native worker, retained outcome and independent obligation',flush=True)
        finally:
            stop_owned_groups(children)
    physical_checks(query,manifest,configuration_checks=prepared)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin',type=Path,required=True);parser.add_argument('--dotnet',type=Path,required=True);parser.add_argument('--browser',type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use assets already built by the qualified verification runner; do not rebuild during the check')
    args=parser.parse_args()
    for path in [args.dotnet,args.browser]:
        if not path.is_file():parser.error('An installed executable is required: '+str(path))
    os.environ['DOTNET']=str(args.dotnet.resolve())
    if not args.no_build:
        for project in ['tests/OpenEtrm.Api.Checks','src/OpenEtrm.Worker']:
            subprocess.run([str(args.dotnet.resolve()),'restore',project,'--locked-mode'],cwd=ROOT,check=True,timeout=120)
            subprocess.run([str(args.dotnet.resolve()),'build',project,'--no-restore','--disable-build-servers','-m:1','-p:UseSharedCompilation=false','-nodeReuse:false'],cwd=ROOT,check=True,timeout=120)
        subprocess.run(['npm','run','build','--prefix','web'],cwd=ROOT,check=True,timeout=120)
    children=[]
    native_main(checks=lambda q,m:run(q,m,args.dotnet.resolve(),args.browser.resolve(),children),fixture_factory=fixture,arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)


if __name__=='__main__':main()
