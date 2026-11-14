"""Run the real economic application and worker in an owned native fixture."""
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

from check_reference_editing import main
from check_gas_owned_holdings import checks as gas_checks
from check_lifecycle_economic_worker import ROOT,stop_owned,worker_checks


def browser_checks(query,manifest,fixture):
    worker_checks(query,manifest,fixture)
    directory=fixture.parent.resolve();f=json.loads(fixture.read_text());dotnet=os.environ.get('DOTNET','dotnet')
    browser=Path(os.environ['OPENETRM_CHECK_BROWSER']).resolve()
    assert browser.is_file() and directory.name.startswith('oetrm-ref-')
    frozen=directory/'economic-worker-binaries';web=directory/'economic-web';shutil.copytree(ROOT/'web/dist',web)
    selected=json.loads(json.dumps(manifest))
    selected['frames']=[frame for frame in selected['frames'] if not frame['at'].startswith(f['at'][:10])]
    selected['frames'].append(dict(at=f['at'],observationIds=[f['markId'],*f['rateIds']],curveContracts={'future':[f['contractId']]}))
    (directory/'manifest.json').write_text(json.dumps(selected));(directory/'manifest.json').chmod(0o600)
    run_id=str(uuid4());(directory/'.openetrm-preview').write_text(run_id)
    shutil.copyfile(ROOT/'deploy/process-limits.example.json',directory/'process-limits.json')
    environment={k:v for k,v in os.environ.items() if not k.startswith(('PG','OPENETRM_PREVIEW_','ASPNETCORE_','DOTNET_'))}
    for variable,name in [('OPENETRM_PREVIEW_CREDENTIAL_KEY','economic-credential.key'),('OPENETRM_PREVIEW_CREDENTIAL_RETRY_KEY','economic-retry.key')]:
        path=directory/name;path.write_bytes(secrets.token_bytes(32));path.chmod(0o600);environment[variable]=str(path)
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1',0));port=reservation.getsockname()[1]
    url=f'http://127.0.0.1:{port}/'
    environment.update(OPENETRM_PREVIEW_ROOT=str(directory),OPENETRM_PREVIEW_PORT=str(port),OPENETRM_PREVIEW_WEB=str(web),
        OPENETRM_PREVIEW_RUNTIME=f'Host={directory};Port=55439;Database=postgres;Username=preview_runtime;SSL Mode=Disable;Timeout=5;Command Timeout=5;Maximum Pool Size=8',
        OPENETRM_PREVIEW_AUTHENTICATOR=f'Host={directory};Port=55439;Database=postgres;Username=preview_authenticator;SSL Mode=Disable;Timeout=5;Command Timeout=5;Maximum Pool Size=8')
    target=directory/'economic-browser-target.json';target.write_text(json.dumps(dict(suite='business_browser_v1',runId=run_id,url=url,economic=f)))
    worker_env={**environment,'OPENETRM_RISK_WORKER_CONFIG':str(directory/'synthetic_economic_worker.json')}
    api=None;worker=None
    with (directory/'economic-app.log').open('w') as app_log,(directory/'economic-live-worker.log').open('w') as worker_log:
        try:
            api=subprocess.Popen([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--local-preview'],cwd=ROOT,env=environment,stdout=app_log,stderr=subprocess.STDOUT)
            for _ in range(200):
                if api.poll() is not None:raise RuntimeError('The owned API stopped during startup. '+(directory/'economic-app.log').read_text())
                try:
                    with urlopen(url+'api/health',timeout=1) as response:health=json.load(response)
                    assert health['mode']=='local-synthetic-preview' and health['runId']==run_id
                    break
                except OSError:time.sleep(.05)
            else:raise AssertionError('The owned API did not become ready.')
            worker=subprocess.Popen([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','serve'],cwd=ROOT,env=worker_env,stdout=worker_log,stderr=subprocess.STDOUT)
            result=subprocess.run(['node',str(ROOT/'tests/browser/lifecycle-economic.mjs'),str(target),str(browser)],cwd=ROOT,capture_output=True,text=True,timeout=240)
            if result.returncode:raise RuntimeError(result.stdout+result.stderr+'\nOwned API diagnostics:\n'+(directory/'economic-app.log').read_text()+'\nOwned worker diagnostics:\n'+(directory/'economic-live-worker.log').read_text())
            print(result.stdout.strip(),flush=True)
            for name in ('economic-real-application.png','economic-real-retained-report.png'):
                shutil.copyfile(directory/name,Path('/tmp')/name)
        finally:
            stop_owned(worker);stop_owned(api)


def checks(query,manifest):gas_checks(query,manifest,economic_worker=browser_checks)


if __name__=='__main__':
    if not os.environ.get('OPENETRM_CHECK_BROWSER'):raise ValueError('Set OPENETRM_CHECK_BROWSER to an installed Chromium executable.')
    for command in ([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks'],['npm','run','build','--prefix','web']):
        result=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,timeout=120)
        if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    main(checks=checks)
