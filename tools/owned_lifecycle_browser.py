"""Launch this tree's Lifecycle UI against its newly owned native fixture."""
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
from urllib.request import urlopen
from uuid import uuid4
from owned_process import stop_owned_groups

ROOT=Path(__file__).resolve().parents[1]


def api_artifact(path):
    source=Path(path).resolve()
    for name in ['OpenEtrm.Api.dll','OpenEtrm.Api.deps.json','OpenEtrm.Api.runtimeconfig.json']:
        if not (source/name).is_file():raise ValueError('A coherent current-tree API artifact is required: '+str(source/name))
    return source


def build_assets(dotnet):
    for project in ['tests/OpenEtrm.Api.Checks','src/OpenEtrm.Worker']:
        subprocess.run([str(dotnet),'restore',project,'--locked-mode'],cwd=ROOT,check=True,timeout=120)
        subprocess.run([str(dotnet),'build',project,'--no-restore','--disable-build-servers','-m:1','-p:UseSharedCompilation=false','-nodeReuse:false'],cwd=ROOT,check=True,timeout=120)
    subprocess.run(['npm','run','build','--prefix','web'],cwd=ROOT,check=True,timeout=120)


def run(query,manifest,dotnet,browser,children,fixture,script,*,worker_enabled=True,timeout=240,artifacts=(),api_bin=None):
    api_source=api_artifact(ROOT/'src/OpenEtrm.Api/bin/Debug/net10.0' if api_bin is None else api_bin)
    root=Path(query('SHOW unix_socket_directories;')).resolve()
    run_id=str(uuid4());(root/'.openetrm-preview').write_text(run_id)
    (root/'manifest.json').write_text(json.dumps(manifest));(root/'manifest.json').chmod(0o600)
    shutil.copyfile(ROOT/'deploy/process-limits.example.json',root/'process-limits.json')
    shutil.copytree(ROOT/'web/dist',root/'web')
    shutil.copytree(api_source,root/'api')
    if worker_enabled:
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
    target=root/'browser-target.json';target.write_text(json.dumps(dict(suite='business_browser_v1',runId=run_id,url=url,lifecycle=fixture)));target.chmod(0o600)
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
            if worker_enabled:
                worker=subprocess.Popen([str(dotnet),str(root/'worker/OpenEtrm.Worker.dll')],cwd=ROOT,env=worker_environment,stdout=worker_log,stderr=subprocess.STDOUT,start_new_session=True);children.append(worker)
            chrome=subprocess.Popen(['node',str(ROOT/script),str(target),str(browser)],cwd=ROOT,env=environment,start_new_session=True);children.append(chrome)
            if chrome.wait(timeout=timeout):
                diagnostic='\n'.join(line for line in (root/'postgres.log').read_text().splitlines() if 'ERROR:' in line or 'CONTEXT:' in line or line.startswith('\tPL/pgSQL function') or line.startswith('\tSQL statement'))
                raise RuntimeError('The full-app lifecycle browser check failed.\n'+(root/'api.log').read_text()+'\n'+(root/'worker.log').read_text()+'\n'+diagnostic[-16000:])
            for name in artifacts:
                shutil.copyfile(root/name,Path(tempfile.gettempdir())/name)
    finally:
        stop_owned_groups(children)
