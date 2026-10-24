"""Reject mismatched protected worker inputs before claiming financial work."""
import json
from pathlib import Path
import subprocess
from uuid import uuid4


def run_checks(query,manifest,claim,worker_command,worker_environment,owned_root,repository):
    root=Path(owned_root);other=str(uuid4())
    query(f"INSERT INTO market_data.catalogs VALUES('{other}','synthetic_worker_{uuid4().hex[:12]}');INSERT INTO security.market_catalog_scopes VALUES('{other}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    def state():
        return query(f"SELECT jsonb_build_object('fence',r.fence,'attempt',r.attempt,'worker',r.worker_id,'run',r.state,'job',j.state)::text FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id WHERE r.id='{claim['id']}';")
    before=state()
    wrong_runtime=root/'pilot.wrong.runtime.connection'
    current=Path(worker_environment['OPENETRM_PILOT_RUNTIME_CONNECTION_FILE']).read_text()
    assert 'Username=pilot_fixture_runtime' in current
    wrong_runtime.write_text(current.replace('Username=pilot_fixture_runtime','Username=preview_runtime'));wrong_runtime.chmod(0o600)
    wrong_manifest=root/'pilot.wrong.manifest.json'
    wrong_manifest.write_text(json.dumps({**manifest,'catalogId':other}));wrong_manifest.chmod(0o600)
    for change in [{'OPENETRM_PILOT_RUNTIME_CONNECTION_FILE':str(wrong_runtime)}, {'OPENETRM_PILOT_MANIFEST_FILE':str(wrong_manifest)}]:
        result=subprocess.run(worker_command,cwd=repository,env={**worker_environment,**change},capture_output=True,text=True,timeout=30)
        assert result.returncode!=0,'A worker accepted a mismatched registered runtime/catalog configuration'
        assert state()==before,'A mismatched worker configuration consumed a financial attempt or changed its fence'
        assert 'must match this registered worker' in result.stderr,result.stdout+result.stderr
    # The database claim boundary also confines a correctly registered worker
    # to its configured catalog, independently of a local process's startup.
    try:
        query(f"UPDATE automation.pilot_runtime_logins SET catalog_id='{other}' WHERE worker_login='pilot_fixture_scheduler';")
        assert query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,true) IS NULL;")=='t','A worker claimed a profile from a different configured catalog'
        assert state()==before
    finally:query(f"UPDATE automation.pilot_runtime_logins SET catalog_id='{manifest['catalogId']}' WHERE worker_login='pilot_fixture_scheduler';")
    print('PASS pilot worker configuration: mismatched valid runtime or manifest fails before claim; a different configured catalog cannot consume an occurrence',flush=True)
