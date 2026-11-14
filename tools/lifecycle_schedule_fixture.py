"""Execute one explicitly configured synthetic product through the real task processor."""
from datetime import datetime,timedelta,timezone
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4


def execute(query,manifest,call,*,close_id,target,action,occurrence_id,policy_id,policy_revision,settings,execution_permission,extra_permissions,expected_admission_error=None):
    catalog=manifest['catalogId']
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    # A disposable service has only the named current consumer's permissions.
    # This fixture never changes an installed or pre-existing application role.
    if query("SELECT to_regrole('pilot_fixture_scheduler') IS NULL;")=='t':
        from check_pilot_close import prepare
        prepare(query,manifest)
    assert execution_permission in ('AmendTrade','BookTrade')
    assert set(extra_permissions)<=set(('ReadGasOwnedHoldings','RecordGasOwnedHoldings'))
    permissions=','.join(repr(p) for p in (execution_permission,*extra_permissions))
    query(f"INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY[{permissions}])p WHERE permission='ImportObservations' ON CONFLICT DO NOTHING;")
    now=datetime.now(timezone.utc)
    body=dict(schedule=dict(requestId=str(uuid4()),name='Synthetic scheduled '+action,operation='lifecycle_action_v1',
        dueAt=(now-timedelta(seconds=1)).isoformat(),expiresAt=(now+timedelta(hours=1)).isoformat(),intervalSeconds=None,timeZone='UTC',
        misfire='run_once',overlap='forbid',timeoutSeconds=30,maximumAttempts=2,profileId=None,profileVersion=None),
        definition=dict(closeId=close_id,target=target,action=action,occurrenceId=occurrence_id,policyId=policy_id,policyRevision=policy_revision,
            knowledgePolicy='current_knowledge_at_execution_v1',settingsPolicy='source_known_exact_action_settings_v1',
            inputPolicy='source_derived_only_no_cash_corrections_v1',settings=settings))
    before=query("SELECT count(*)::text||':'||(SELECT count(*) FROM automation.commands)::text FROM automation.jobs;")
    if expected_admission_error is not None:
        try:call(f"SELECT automation.create_lifecycle_job('{catalog}',{lit(body)})::text;")
        except RuntimeError as error:assert expected_admission_error in str(error),str(error)
        else:raise AssertionError('The task accepted '+expected_admission_error)
        assert query("SELECT count(*)::text||':'||(SELECT count(*) FROM automation.commands)::text FROM automation.jobs;")==before
        return None
    admitted=json.loads(call(f"SELECT automation.create_lifecycle_job('{catalog}',{lit(body)})::text;"))['outcome']
    worker=str(uuid4())
    claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker}',120)::text;"))
    assert claim['jobId']==admitted['id'] and claim['operation']=='lifecycle_action_v1'
    host=query('SHOW unix_socket_directories;');path=Path(host)/('scheduled-'+action.replace('.','-')+'.json')
    path.write_text(json.dumps(dict(manifest=manifest,host=host,claim=claim)));path.chmod(0o600)
    checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--',
        '--lifecycle-schedule-run',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
    assert query(f"SELECT mode||'|'||(command_id=run_id)::text FROM automation.lifecycle_results WHERE run_id='{claim['id']}';")=='automatic|true'
    assert query(f"SELECT count(*) FROM lifecycle.commands WHERE operation_id='{claim['id']}';")=='1'
    print(checked.stdout.strip(),flush=True)
    return claim
