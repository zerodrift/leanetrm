"""Source-known accepted gas projection in an owned native fixture."""
import json
import os
from decimal import Decimal
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_holdings import checks as owned_checks
from check_gas_cashout_projection import upgrade_checks as cashout_upgrade
from owned_process import stop_owned_groups


SIGNATURES=('security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)',
    'trading.require_gas_cashout_projection(uuid,uuid,jsonb,uuid,timestamptz)')
before={}
children=[]


def run_owned(arguments,root,environment,timeout):
    process=subprocess.Popen(arguments,cwd=root,env=environment,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,start_new_session=True)
    children.append(process)
    try:
        output,error=process.communicate(timeout=timeout)
        return subprocess.CompletedProcess(arguments,process.returncode,output,error)
    finally:stop_owned_groups(children)


def upgrade_checks(query,migration):
    cashout_upgrade(query,migration)
    if migration.name!='0295_gas_accepted_projection.sql':return
    for signature in SIGNATURES:before[signature]=query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;")
    from check_fact_anchor_upgrade import run_checks
    run_checks(query,migration,number='0295',guard='gas_accepted_projection_upgrade',signatures=SIGNATURES)
    from assistant_context import check_guard
    changes=[]
    for signature in SIGNATURES:
        count=len(signature.split('(',1)[1][:-1].split(','))
        for alteration in (f"proargnames=array_fill('changed'::text,ARRAY[{count}])",'pronargdefaults=1',
            f"proargmodes=array_fill('i'::\"char\",ARRAY[{count}])",'proretset=true','provariadic=23'):
            changes.append((f"UPDATE pg_proc SET {alteration} WHERE oid='{signature}'::regprocedure;",signature))
    check_guard(query,migration.read_text(),'0295','gas_accepted_projection_upgrade',changes)


def accepted_checks(query,manifest,fixture):
    f=json.loads(fixture.read_text());catalog=manifest['catalogId']
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    for signature in SIGNATURES:
        assert before[signature]==query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;"),'Upgrade replaced native operation identity'
    source=call(f"SELECT security.capture_date_context('{catalog}','{f['at'][:10]}',NULL);")
    body=dict(profileId=str(uuid4()),expectedRevision=0,implementation='gas_accepted_quantity_lifecycle_projection_v3',
        quantityDisposition='retain_quantity_obligations_v1',effectiveAt=f['at'],performances=[],cashouts=[])
    def configure(selected,operation=None,context=None,account=None):
        current=context or call(f"SELECT security.capture_date_context('{catalog}','{f['at'][:10]}',NULL);")
        literal="'"+json.dumps(selected).replace("'","''")+"'::jsonb"
        return json.loads(call(f"SELECT security.configure_gas_owned_projection('{catalog}','{operation or uuid4()}','projection_profile','{account or f['accountId']}',"
            f"{literal},'{f['at']}','{current}',false)::text;"))
    operation=str(uuid4());result=configure(body,operation,source)
    assert result['changed'] and result['currentRevision']==1,result
    assert configure(body,operation,source)==result,'Retry changed its first native outcome'
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    same=configure(body)
    assert not same['changed'] and same['acknowledgedRevision']==1 and same['currentRevision']==1,same
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    changed=configure({**body,'expectedRevision':1,'quantityDisposition':'perform_full_quantity_at_contractual_due_v1'})
    assert changed['changed'] and changed['currentRevision']==2,changed
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    older=configure(body)
    assert not older['changed'] and older['acknowledgedRevision']==1 and older['currentRevision']==2,older
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    for invalid in ({k:v for k,v in body.items()if k!='cashouts'},
        {**body,'profileId':str(uuid4()),'cashouts':None},
        {**body,'profileId':str(uuid4()),'implementation':'gas_quantity_and_cashout_lifecycle_projection_v2'}):
        try:configure(invalid)
        except RuntimeError as error:assert 'gas_owned_input' in str(error) or 'gas_owned_projection_policy' in str(error),str(error)
        else:raise AssertionError('A missing policy choice or empty v2 cashout model was accepted')
    print('PASS explicit accepted-quantity projection profile preserves an empty cashout choice',flush=True)
    # Add an independently observed future loss before the next retained close.
    # The older fixture's other loss stays after that close as source evidence.
    lots=json.loads(query("SELECT jsonb_agg(id)::text FROM trading.gas_owned_positions "
        f"WHERE(catalog_id,account_id,kind,origin_policy)=('{catalog}','{f['accountId']}','owned_stock','operator_balance_transfer_v1');"))
    assert len(lots)==1,lots
    binding=json.loads(query("SELECT jsonb_build_object('policy',policy_id,'revision',policy_revision)::text FROM trading.gas_owned_action_bindings b "
        f"WHERE(b.catalog_id,b.account_id,b.action)=('{catalog}','{f['accountId']}','gas.owned.loss') AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_action_bindings n "
        "WHERE(n.catalog_id,n.account_id,n.action)=(b.catalog_id,b.account_id,b.action) AND n.revision>b.revision);"))
    future='2020-12-21T12:00:00Z'
    def native(action,selected,policy=None,revision=None,account=None,asof=future):
        context=call(f"SELECT security.capture_date_context('{catalog}','{asof[:10]}',NULL);")
        literal="'"+json.dumps(selected).replace("'","''")+"'::jsonb";policy_sql='NULL' if policy is None else "'"+policy+"'"
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account or f['accountId']}',{literal},'{asof}',"
            f"'{context}',{policy_sql},{revision or 'NULL'},false)::text;"))
    evidence=native('evidence',dict(sourceKey='synthetic_accepted_projection_loss',kind='physical_loss',quantity='0.125',occurredAt=future,
        evidence='Synthetic source-known physical loss before the next close',expectedRevision=0))
    native('loss',dict(lotId=lots[0],evidenceId=evidence['id'],quantity='0.125',reservationId=None,effectiveAt=future),binding['policy'],binding['revision'])
    # Establish exact worker rights before capture/submission pins the epoch.
    from check_risk_jobs_db import provision_worker
    login='synthetic_accepted_gas_worker';provision_worker(query,manifest,login);role=str(uuid4())
    query(f"INSERT INTO security.roles VALUES('{role}','synthetic_accepted_gas_reader','Synthetic accepted gas source reader');INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}',tenant_id,workspace_id,clock_timestamp(),'9999-12-31',false FROM risk.workers WHERE login='{login}';")
    mark=Decimal(query(f"SELECT value FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{f['markId']}');"))
    multiplier=Decimal(query("SELECT q.multiplier FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)="
        f"(c.reference_catalog_id,c.reference_revision_id,c.quote_code) WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','future');"))
    # Opening net quantity is -15; losing .125 leaves -15.125. The native
    # positive historical move is .2, so its loss is 3.025 plus actual lost gas.
    target=Path(f['host'])/'gas-accepted-projection.json'
    target.write_text(json.dumps({**f,'profileId':body['profileId'],
        'profiles':[dict(accountId=f['accountId'],profileId=body['profileId'],revision=1)],'futureOwnerIds':[],
        'contextId':call(f"SELECT security.capture_date_context('{catalog}','{f['at'][:10]}',NULL);"),
        'expectedEconomicPnl':str(-Decimal('.125')*mark*multiplier),
        'expectedHistoricalVar':str((Decimal('.125')*mark+Decimal('3.025'))*multiplier)}));target.chmod(0o600)
    qualify(query,target)
    operator=query(f"SELECT id FROM trading.gas_owned_accounts WHERE(catalog_id,book_id,kind)=('{catalog}','{f['bookId']}','operator_balance');")
    def action_binding(account,action):
        return json.loads(query("SELECT jsonb_build_object('policy',policy_id,'revision',policy_revision)::text FROM trading.gas_owned_action_bindings b "
            f"WHERE(b.catalog_id,b.account_id,b.action)=('{catalog}','{account}','{action}') AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_action_bindings n "
            "WHERE(n.catalog_id,n.account_id,n.action)=(b.catalog_id,b.account_id,b.action) AND n.revision>b.revision);"))
    statement=native('evidence',dict(sourceKey='synthetic_partial_operator_projection',kind='operator_statement',quantity='6',occurredAt=f['at'],
        evidence='Synthetic accepted operator claim',expectedRevision=0),account=operator,asof=f['at'])
    record=action_binding(operator,'gas.operator.record')
    claim=native('operator_claim',dict(evidenceId=statement['id'],quantity='6',effectiveAt=f['at'],dueAt=future,
        carrying='nominal_mark_v1',settlementMode='return_quantity_v1',cashout=None),record['policy'],record['revision'],operator,f['at'])
    title=native('evidence',dict(sourceKey='synthetic_partial_operator_receipt',kind='title_receipt',quantity='2',occurredAt=future,
        evidence='Synthetic actual partial title receipt',expectedRevision=0))
    settlement=action_binding(operator,'gas.operator.settle')
    native('operator_settle',dict(positionId=claim['id'],quantity='2',ownedAccountId=f['accountId'],lotId=None,reservationId=None,
        evidenceId=title['id'],effectiveAt=future,availableAt=future,sourceObservationId=None),settlement['policy'],settlement['revision'],operator)
    receipt=query(f"SELECT receipt_lot_id FROM trading.gas_owned_claim_settlements WHERE(catalog_id,position_id)=('{catalog}','{claim['id']}');")
    partial={**body,'profileId':str(uuid4()),'quantityDisposition':'perform_full_quantity_at_contractual_due_v1',
        'performances':[dict(positionId=claim['id'],quantity='4',destinationAccountId=f['accountId'],inventoryDefinitionRevisionId=manifest['revisionId'],
            inventoryCurve='future',inventoryContractId=f['contractId'],availability='available_at_projected_title_v1',supply=[])]}
    configure(partial,account=operator)
    target=Path(f['host'])/'gas-accepted-partial-projection.json'
    target.write_text(json.dumps({**f,'profileId':body['profileId'],
        'profiles':[dict(accountId=f['accountId'],profileId=body['profileId'],revision=1),dict(accountId=operator,profileId=partial['profileId'],revision=1)],
        'futureOwnerIds':[receipt],'contextId':call(f"SELECT security.capture_date_context('{catalog}','{f['at'][:10]}',NULL);"),
        'expectedEconomicPnl':str(-Decimal('.125')*mark*multiplier),
        'expectedHistoricalVar':str((Decimal('.125')*mark+Decimal('1.825'))*multiplier)}));target.chmod(0o600)
    qualify(query,target)


def qualify(query,target):
    f=json.loads(target.read_text());catalog=f['manifest']['catalogId']
    root=Path(__file__).resolve().parents[1];dotnet=os.environ['DOTNET']
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    managed=run_owned([dotnet,'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-accepted-native',str(target)],root,os.environ,120)
    if managed.returncode:raise RuntimeError(managed.stdout+managed.stderr)
    print(managed.stdout.strip(),flush=True)
    frozen=Path(f['host'])/(target.stem+'-worker-binaries');shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    connection=Path(f['host'])/'accepted-worker-connection';connection.write_text(f'Host={f["host"]};Port=55439;Database=postgres;Username=synthetic_accepted_gas_worker;SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2');connection.chmod(0o600)
    config=Path(f['host'])/'accepted-worker.json';config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)));config.chmod(0o600)
    environment={k:v for k,v in os.environ.items() if not k.startswith(('PG','OPENETRM_','ASPNETCORE_','DOTNET_'))};environment['OPENETRM_RISK_WORKER_CONFIG']=str(config)
    jobs=json.loads(target.with_suffix('.economic-worker.json').read_text())['jobs'];assert len(jobs)==2
    for saved in jobs:
        worker=run_owned([dotnet,str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],root,environment,90)
        if worker.returncode:raise RuntimeError(worker.stdout+worker.stderr)
        actual=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result) FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state']=='completed' and actual['error'] is None and actual['result']==saved['report'],actual|{'workerDiagnostics':worker.stdout+worker.stderr}
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    print('PASS accepted gas historical and Monte Carlo workers: exact source/profile custody, native event parity and no financial mutation',flush=True)


def checks(query,manifest):owned_checks(query,manifest,economic_worker=accepted_checks)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks,cleanup_ready=lambda:not children)
