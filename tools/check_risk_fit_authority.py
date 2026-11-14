"""Fit-purpose worker authority, scoped to an owned disposable native fixture."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from check_reference_editing import main
from check_lifecycle_actions import fixture
from check_risk_jobs_db import provision_worker
from preview_fixture import catalogue_fixture

before={}
def upgrade_checks(query,migration):
    if migration.name!='0295_risk_fit_read_admission.sql':return
    signatures=('risk.bind(uuid,uuid)','security.authority_until_pre_lifecycle_jobs(text)')
    for signature in signatures:before[signature]=query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;")
    from check_fact_anchor_upgrade import run_checks
    run_checks(query,migration,number=migration.name[:4],guard='risk_fit_admission_upgrade',signatures=signatures)
    from assistant_context import check_guard
    check_guard(query,migration.read_text(),migration.name[:4],'risk_fit_admission_upgrade',[(
        f"UPDATE pg_proc SET proargnames=ARRAY['synthetic_changed']::text[] WHERE oid='{signature}'::regprocedure;",signature)for signature in signatures])

def checks(query,manifest):
    for signature,oid in before.items():assert query(f"SELECT oid FROM pg_proc WHERE oid='{signature}'::regprocedure;")==oid,'A fit admission upgrade changed helper identity'
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer']
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,issuer))
    actor=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{issuer}' AND subject='trader';")
    role,grant=str(uuid4()),str(uuid4())
    query(f"INSERT INTO security.roles VALUES('{role}','synthetic_fit_publisher','Synthetic fit publisher');"
          f"INSERT INTO security.role_permissions VALUES('{role}','PublishMarketData');"
          f"INSERT INTO security.role_grants VALUES('{grant}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);")
    provision_worker(query,manifest,'synthetic_fit_reader')
    service=query("SELECT principal_id FROM risk.workers WHERE login='synthetic_fit_reader';")
    assert query(f"SELECT NOT security.principal_has_permission('{actor}','{tenant}','{workspace}','RunScenario',clock_timestamp()) "
                 f"AND NOT security.principal_has_permission('{service}','{tenant}','{workspace}','PublishMarketData',clock_timestamp());")=='t'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def user(sql):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    seed=manifest['frames'][0];at=seed['at'];day=at[:10]
    context=user(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
    scope=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}','{day}'::date);"
    frame=dict(id=str(uuid4()),name='Synthetic fit authority inputs',at=at,businessDate=day,contextId=context,
        observationIds=seed['observationIds'],frozenObservationIds=[],curveContracts=seed['curveContracts'],sourceInputs=None,resolvesLatestCorrection=False,
        source=dict(definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId']))
    request=dict(convention='vol_surface_fit_v1',engineVersion='synthetic_fit_authority_v1',businessDate=day,
        futuresConvention=manifest['futuresConvention'],solverPolicy='american_futures_option_crr_v1',model='smile_quadratic_log_moneyness_v1',
        product=dict(referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],code='nymex_on'))
    def submit():return json.loads(user(scope+f"SELECT risk.submit_vol_surface_fit('{catalog}','{uuid4()}','{context}','{manifest['revisionId']}','{at}',{literal(request)},{literal(frame)})::text;"))
    def worker(sql):return query('SET SESSION AUTHORIZATION synthetic_fit_reader;'+sql)
    def claim():return json.loads(worker("SELECT risk.claim(30,'synthetic_fit_authority_v1')::text;"))
    job=submit();held=claim();assert held['jobId']==job['jobId']
    result=worker(f"BEGIN;SELECT risk.bind('{job['jobId']}','{held['leaseToken']}');"
        "SELECT security.authority_until('ReadTrades')>clock_timestamp();"
        f"SELECT count(*) FROM ref_data.futures_option_products WHERE(catalog_id,revision_id,code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','nymex_on');COMMIT;")
    assert result.splitlines()[-2:]==['t','1'],result
    worker(f"SELECT risk.finish('{job['jobId']}','{held['leaseToken']}',NULL,'qualification_complete');")
    print('PASS fit-purpose native bind/read requires actor publication and existing service risk-read authority, without actor scenario or service publication grants',flush=True)
    # Both publisher grants preexist the operation. Waiting across the gap must
    # preserve the first grant's deadline even though current authority returns.
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '3 seconds' WHERE id='{grant}';"
          f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,granted_to+interval '0.2 seconds','9999-12-31',false FROM security.role_grants WHERE id='{grant}';")
    job=submit();held=claim();assert held['jobId']==job['jobId']
    holder_name='synthetic_fit_epoch_'+uuid4().hex
    with ThreadPoolExecutor(max_workers=2)as pool:
        holder=pool.submit(query,f"SET application_name='{holder_name}';BEGIN;SELECT generation FROM security.epoch FOR UPDATE;SELECT pg_sleep(4.4);ROLLBACK;")
        deadline=time.monotonic()+3
        while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{holder_name}' AND wait_event='PgSleep';")!='1':
            assert time.monotonic()<deadline,'Fit epoch holder did not acquire its owned lock'
            time.sleep(.01)
        pending=pool.submit(worker,f"SET application_name='synthetic_fit_bind_wait';BEGIN;SELECT risk.bind('{job['jobId']}','{held['leaseToken']}');COMMIT;")
        deadline=time.monotonic()+3
        while query("SELECT count(*)FROM pg_stat_activity WHERE application_name='synthetic_fit_bind_wait' AND wait_event_type='Lock' AND query LIKE '%risk.bind%';")!='1':
            assert time.monotonic()<deadline,'Fit bind did not reach its first epoch wait'
            time.sleep(.01)
        try:pending.result(timeout=8)
        except RuntimeError as error:assert 'risk_job_denied' in str(error),str(error)
        else:raise AssertionError('A successor publisher grant revived a started fit bind')
        holder.result(timeout=3)
    assert query(f"SELECT security.principal_has_permission('{actor}','{tenant}','{workspace}','PublishMarketData',clock_timestamp());")=='t'
    print('PASS fit bind retains the original publisher deadline across its first epoch wait and a preexisting successor grant',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
