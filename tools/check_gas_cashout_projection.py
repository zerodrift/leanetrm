"""Saved explicit operator cashout projection under an owned native source."""
import json
import os
from decimal import Decimal, ROUND_HALF_EVEN
from pathlib import Path
import subprocess
import shutil
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_holdings import checks as owned_checks

before={}
def upgrade_checks(query,migration):
    if migration.name!='0294_gas_cashout_projection.sql':return
    signatures=('trading.gas_owned_projection_fact(jsonb)',
        'security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)',
        'security.read_gas_owned_projection_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz)')
    for signature in signatures:before[signature]=query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;")
    from check_fact_anchor_upgrade import run_checks
    run_checks(query,migration,number=migration.name[:4],guard='gas_cashout_projection_upgrade',signatures=signatures)
    from assistant_context import check_guard
    changes=[]
    for signature in signatures:
        types=signature.split('(',1)[1][:-1].split(',');count=len(types)
        for alteration in (f"proargnames=array_fill('changed'::text,ARRAY[{count}])",
            "proallargtypes=ARRAY["+','.join("'"+value+"'::regtype::oid" for value in types)+"]",
            'pronargdefaults=1',f"proargmodes=array_fill('i'::\"char\",ARRAY[{count}])",'proretset=true'):
            changes.append((f"UPDATE pg_proc SET {alteration} WHERE oid='{signature}'::regprocedure;",signature))
    check_guard(query,migration.read_text(),migration.name[:4],'gas_cashout_projection_upgrade',changes)


def checks(query,manifest):
    for signature,oid in before.items():assert query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;")==oid
    owned_checks(query,manifest)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,rollback=False):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}"
            +('SET CONSTRAINTS ALL IMMEDIATE;ROLLBACK;' if rollback else 'COMMIT;'))
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")
    # A separate synthetic book has no unrelated ownership or capital origin.
    book=str(uuid4())
    query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
        f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic cashout projection',(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}'),effective_at "
        f"FROM trading.books WHERE(catalog_id,id)=('{catalog}','d1000000-0000-0000-0000-000000000001');COMMIT;")
    original=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}' AND kind='operator_balance';"))
    account=str(uuid4())
    def command(action,body,policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))
    command('account',dict(code='synthetic_cashout_projection',kind='operator_balance',bookId=book,legalEntityId=original['legal_entity_id'],legalEntityRevision=original['legal_entity_revision'],
        counterpartyId=original['counterparty_id'],counterpartyRevision=original['counterparty_revision'],referenceCatalogId=original['reference_catalog_id'],referenceRevisionId=original['reference_revision_id'],
        definitionRevisionId=original['definition_revision_id'],markCurve=original['mark_curve_code'],markContractId=original['mark_contract_id'],quantity=original['quantity_code'],currency=original['currency_code'],
        location=original['location_code'],titlePolicy=original['title_policy'],lossPolicy=original['loss_policy'],effectiveAt='2020-12-18T00:00:00Z'))
    policy=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{original['id']}','gas.operator.record') ORDER BY revision DESC LIMIT 1;")
    command('bind_policy',dict(action='gas.operator.record',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    evidence=command('evidence',dict(sourceKey='synthetic_cashout_projection_statement',kind='operator_statement',quantity='2',occurredAt=at,
        evidence='Synthetic future cashout claim',expectedRevision=0))['id']
    official=query(f"SELECT source_code FROM trading.gas_owned_cashout_terms WHERE catalog_id='{catalog}' LIMIT 1;")
    claim=command('operator_claim',dict(evidenceId=evidence,quantity='2',effectiveAt=at,dueAt='2020-12-21T12:00:00Z',carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',
        cashout=dict(source=official,role='daily_settlement',observedAt='2020-12-20T20:00:00Z',cashDueAt='2020-12-21T12:00:00Z',rounding='payment_total_half_even_2dp_v1')),policy)
    body=dict(profileId=str(uuid4()),expectedRevision=0,implementation='gas_quantity_and_cashout_lifecycle_projection_v2',quantityDisposition='retain_quantity_obligations_v1',effectiveAt=at,performances=[],
        cashouts=[dict(positionId=claim['id'],scenarioDefinitionRevisionId=original['definition_revision_id'],scenarioCurve=original['mark_curve_code'],scenarioContractId=original['mark_contract_id'],
            priceInterpretation='selected_valuation_mark_is_cashout_expectation_v1',cashCarrying='captured_currency_curve_cashout_cash_v1')])
    def configure(value,operation=None,context=None,rollback=False):
        return json.loads(call(f"SELECT security.configure_gas_owned_projection('{catalog}','{operation or uuid4()}','projection_profile','{account}',{literal(value)},'{at}','{context or capture()}',false)::text;",rollback))
    operation=str(uuid4());source=capture();saved=configure(body,operation,source)
    assert saved['changed'] and saved['acknowledgedRevision']==1
    assert configure(body,operation,source)==saved
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    assert not configure({**body,'expectedRevision':999})['changed']
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    changed=configure({**body,'expectedRevision':1,'quantityDisposition':'perform_full_quantity_at_contractual_due_v1'})
    assert changed['acknowledgedRevision']==2
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    older=configure(body)
    assert older['acknowledgedRevision']==1 and older['currentRevision']==2 and not older['changed']
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    try:call(f"SELECT security.use_market_context('{catalog}','{source}');SELECT security.use_trading_context('{catalog}','{at}');SELECT security.read_gas_owned_projection_profile('{catalog}','{body['profileId']}',1,'{source}','{at}');")
    except RuntimeError as error:assert 'gas_owned_projection_policy' in str(error),str(error)
    else:raise AssertionError('A saved profile appeared in a source captured before publication.')
    for invalid in ({k:v for k,v in body.items() if k!='cashouts'},
        {**body,'cashouts':body['cashouts']*2},
        {**body,'cashouts':[{**body['cashouts'][0],'cashCarrying':'explicit_nominal_cashout_cash_v1'}]},
        {**body,'cashouts':[{**body['cashouts'][0],'priceInterpretation':''}]}):
        try:configure(invalid,rollback=True)
        except RuntimeError as error:assert 'gas_owned_' in str(error),str(error)
        else:raise AssertionError('An incomplete, duplicated or financially incompatible cashout policy was accepted.')
    current=capture()
    retained=json.loads(call(f"SELECT security.use_market_context('{catalog}','{current}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_projection_profile('{catalog}','{body['profileId']}',1,'{current}','{at}')::text;"))
    assert retained['profile']['implementation']==body['implementation'] and retained['cashouts'][0]['position_id']==claim['id']
    print('PASS native cashout projection policy: exact saved scenario expectation, cash carrying, first outcomes, semantic no-op and complete retained references',flush=True)
    host=query('SHOW unix_socket_directories;')
    rates=json.loads(query("SELECT jsonb_agg(o.id ORDER BY o.id) FROM market_data.rate_curves c JOIN market_data.rate_curve_tenors t ON(t.catalog_id,t.revision_id,t.curve_code)=(c.catalog_id,c.revision_id,c.code) "
        "JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code)=(c.catalog_id,c.revision_id,t.series_id,c.source_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','interest_rates') AND o.role='parameter' AND o.publication_state='final' AND o.observed_at='2020-12-18T16:00:00Z' "
        "AND NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id));"))
    marked=Decimal(query(f"SELECT o.value*q.multiplier*2 FROM market_data.observations o JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(o.catalog_id,o.definition_revision_id,'future') "
        f"JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code) WHERE(o.catalog_id,o.id)=('{catalog}','70000000-0000-0000-0000-000000000001');"))
    target=Path(host)/'gas-cashout-projection.json';target.write_text(json.dumps(dict(manifest=manifest,host=host,at=at,contextId=capture(),bookId=book,accountId=account,profileId=body['profileId'],positionId=claim['id'],
        markId='70000000-0000-0000-0000-000000000001',contractId=original['mark_contract_id'],rateIds=rates,expectedOpening=str(marked),expectedCash=str(marked.quantize(Decimal('.01'),rounding=ROUND_HALF_EVEN)))));target.chmod(0o600)
    # Establish the worker's exact role before submissions capture the epoch.
    from check_risk_jobs_db import provision_worker
    login='synthetic_cashout_worker';provision_worker(query,manifest,login);role=str(uuid4())
    query(f"INSERT INTO security.roles VALUES('{role}','synthetic_cashout_source_reader','Synthetic cashout source reader');INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}',tenant_id,workspace_id,clock_timestamp(),'9999-12-31',false FROM risk.workers WHERE login='{login}';")
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-cashout-native',str(target)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    print(result.stdout.strip(),flush=True)
    root=Path(__file__).resolve().parents[1];frozen=Path(host)/'cashout-worker-binaries';shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    connection=Path(host)/'cashout-worker-connection';connection.write_text(f'Host={host};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2');connection.chmod(0o600)
    config=Path(host)/'cashout-worker.json';config.write_text(json.dumps(dict(connectionFile=str(connection),leaseSeconds=30,pollSeconds=1,timeoutSeconds=60)));config.chmod(0o600)
    environment={k:v for k,v in os.environ.items() if not k.startswith(('PG','OPENETRM_','ASPNETCORE_','DOTNET_'))};environment['OPENETRM_RISK_WORKER_CONFIG']=str(config)
    jobs=json.loads(target.with_suffix('.economic-worker.json').read_text())['jobs'];assert len(jobs)==2
    for saved in jobs:
        worker=subprocess.run([os.environ.get('DOTNET','dotnet'),str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','once'],cwd=root,env=environment,capture_output=True,text=True,timeout=90)
        if worker.returncode:raise RuntimeError(worker.stdout+worker.stderr)
        actual=json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result) FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state']=='completed' and actual['error'] is None and actual['result']==saved['report'],actual|{'workerDiagnostics':worker.stdout+worker.stderr}
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    print('PASS actual cashout historical and Monte Carlo workers: exact native profile/source custody, complete interactive report parity and no financial mutation',flush=True)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
