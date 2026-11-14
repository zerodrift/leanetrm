"""Exact source-only lifecycle scheduling on disposable native PostgreSQL."""
from datetime import datetime, timedelta, timezone
from uuid import uuid4
import json
import os
from pathlib import Path
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from check_futures_margin_adoption import successor_grants
from check_reference_editing import main
from check_futures_physical_delivery import fixture as physical_fixture, checks as physical_checks
from check_lifecycle_actions import upgrade_checks as action_upgrade_checks
from close_session_fixture import sql_session


_PUBLIC_OIDS={}
SCHEDULE_HELPERS=('automation.authorized(automation.jobs)','automation.pilot_authorized(automation.jobs,uuid)',
 'automation.pilot_service_eligible(automation.jobs,automation.workers)','security.has_permission(text)','security.authority_until(text)',
 'automation.issue_pilot_context(uuid,bigint,uuid)')
SCOPE_HELPERS=('lifecycle.capture_action_authority(uuid,text,jsonb)','lifecycle.option_proposal(uuid,jsonb)','lifecycle.execute_option_action(uuid,jsonb)',
 'lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)','lifecycle.command_status(uuid,uuid,text,jsonb,text,timestamptz)',
 'lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)')

def upgrade_checks(query,migration):
    action_upgrade_checks(query,migration)
    selected=SCHEDULE_HELPERS if migration.name=='0279_lifecycle_schedules.sql' else SCOPE_HELPERS if migration.name=='0284_lifecycle_scheduled_action_scope.sql' else ()
    if not selected:return
    from assistant_context import check_guard
    guard='lifecycle_schedule_upgrade' if migration.name.startswith('0279') else 'lifecycle_schedule_scope_upgrade'
    for signature in selected:
        _PUBLIC_OIDS[signature]=query(f"SELECT to_regprocedure('{signature}')::oid;")
        changes=[(sql,signature) for sql in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",f"ALTER FUNCTION {signature} STRICT;",
            f"ALTER FUNCTION {signature} LEAKPROOF;",f"ALTER FUNCTION {signature} PARALLEL SAFE;",f"ALTER FUNCTION {signature} STABLE;",
            f"ALTER FUNCTION {signature} SECURITY INVOKER;",f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime WITH GRANT OPTION;",f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $$DECLARE definition text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(definition,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;",
            f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[{signature.count(',')+1}]) WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proallargtypes=proargtypes::oid[] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{signature}'::regprocedure;")]
        check_guard(query,migration.read_text(),migration.name[:4],guard,changes)


def fixture(root):
    reference,market,security,manifest=physical_fixture(root)
    marker="UPDATE ref_data.revisions SET state='sealed'"
    assert market.count(marker)==1
    market=market.replace(marker,f"INSERT INTO ref_data.calendars VALUES('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','synthetic_lifecycle_days','test_utc',0,'2026-01-01','2027-01-01');\n"+marker)
    return reference,market,security,manifest


def configured(query, manifest, setup):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    occurrence=setup['marginRequest']['policy']['occurrences'][-1]
    at=occurrence['at'];day=at[:10]
    calendar='synthetic_lifecycle_days'
    sql_session(query,manifest,'synthetic_lifecycle_close',calendar,at[11:19],['nymex_ng'],['trader'],effective=('2026-01-01','2027-01-01'))
    pins=json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',s.revision_id,'seriesId',s.id,'observationId',o.id,'inputUse','variable')) FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE(o.catalog_id,o.definition_revision_id)=('{catalog}','{manifest['revisionId']}') AND o.id=ANY(ARRAY[{','.join(repr(v)+'::uuid' for v in manifest['frames'][0]['observationIds'])}]);"))
    members=[dict(definitionRevisionId=manifest['revisionId'],curve='nymex_ng',contractId=setup['contractId'],ordinal=0,scope='root')]
    close=call(f"SELECT security.register_market_close('{catalog}','{manifest['revisionId']}','{uuid4()}','Synthetic lifecycle close','synthetic_lifecycle_close','{day}','{at}',{literal(pins)},{literal(members)});")
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='futures.margin',mode='manual',fromAt='2026-01-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy)});")
    now=datetime.now(timezone.utc)
    body=dict(schedule=dict(requestId=str(uuid4()),name='Synthetic source-only lifecycle',operation='lifecycle_action_v1',dueAt=(now-timedelta(seconds=1)).isoformat(),expiresAt=(now+timedelta(hours=1)).isoformat(),intervalSeconds=None,timeZone='UTC',misfire='run_once',overlap='forbid',timeoutSeconds=30,maximumAttempts=2,profileId=None,profileVersion=None),
        definition=dict(closeId=close,target=dict(handler='futures',scopeId=setup['adoption']['tradeId'],bindingId=setup['adoption']['bindingId']),action='futures.margin',occurrenceId=occurrence['id'],policyId=policy['policyId'],policyRevision=1,knowledgePolicy='current_knowledge_at_execution_v1',settingsPolicy='source_known_exact_action_settings_v1',inputPolicy='source_derived_only_no_cash_corrections_v1',settings=dict(tradeRevision=1,optionProfileRevision=None,swapProfileId=None,swapProfileRevision=None,marginPolicyId=setup['marginPolicyId'],marginPolicyRevision=1)))
    create=lambda value:json.loads(call(f"SELECT automation.create_lifecycle_job('{catalog}',{literal(value)})::text;"))['outcome']
    created=create(body)
    assert create(body)==created
    semantic=json.loads(json.dumps(body));semantic['schedule']['requestId']=str(uuid4())
    assert create(semantic)==created
    assert query(f"SELECT count(*) FROM automation.jobs WHERE operation='lifecycle_action_v1';")=='1'
    print('PASS exact lifecycle task admission, original request and semantic retries retain one job and pinned native settings',flush=True)
    from check_pilot_close import prepare
    service=prepare(query,manifest)
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r CROSS JOIN security.permissions p WHERE r.permission='ImportObservations' AND p.code IN('AmendTrade','BookTrade','ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    worker_id=str(uuid4())
    claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker_id}',120)::text;"))
    assert claim['jobId']==created['id'] and claim['operation']=='lifecycle_action_v1'
    token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{claim['id']}',{claim['fence']},'{worker_id}');")
    outcome=query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{token}');SELECT automation.capture_lifecycle_source()::text;"
        "SELECT security.has_permission(NULL) OR security.has_permission('absent_lifecycle_permission') OR security.has_permission('ConfigureGasOwnedHoldings');"
        "SELECT security.authority_until(NULL)='-infinity'::timestamptz AND security.authority_until('ConfigureGasOwnedHoldings')='-infinity'::timestamptz;"
        "SELECT automation.publish_lifecycle_result('manual',NULL,NULL)::text;COMMIT;")
    assert outcome.splitlines()[-3:-1]==['f','t'],outcome
    assert query(f"SELECT state FROM automation.runs WHERE id='{claim['id']}';")=='succeeded'
    assert query(f"SELECT count(*) FROM lifecycle.commands WHERE operation_id='{claim['id']}';")=='0'
    print('PASS lifecycle service uses task-required capability intersection and atomically records a manual-required outcome without a financial mutation',flush=True)
    # A newer settings revision changes new work. It cannot rewrite an old
    # first outcome or make an old same-fact acknowledgement re-enroll it.
    current_policy={**policy,'operationId':str(uuid4()),'expectedRevision':1,'mode':'review'}
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(current_policy)});")
    semantic['schedule']['requestId']=str(uuid4())
    assert create(semantic)==created
    assert query(f"SELECT count(*) FROM automation.jobs WHERE operation='lifecycle_action_v1';")=='1'
    print('PASS old same-fact task acknowledgements precede changed native settings and never reactivate or duplicate work',flush=True)
    original=query(f"SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE(o.catalog_id,o.definition_revision_id,s.contract_id)=('{catalog}','{manifest['revisionId']}','{setup['contractId']}') ORDER BY o.observed_at,o.id LIMIT 1;")
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{uuid4()}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,'synthetic_scheduled_final','1','final_contract_settlement','final','{at}',clock_timestamp(),5,NULL FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{original}');")
    def managed(job):
        selected=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker_id}',120)::text;"))
        assert selected['jobId']==job['id'],selected
        host=query('SHOW unix_socket_directories;');path=Path(host)/'lifecycle-schedule-check.json'
        path.write_text(json.dumps(dict(manifest=manifest,host=host,claim=selected)));path.chmod(0o600)
        executed=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-schedule-run',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=45)
        if executed.returncode:raise RuntimeError(executed.stdout+executed.stderr)
        print(executed.stdout.strip(),flush=True)
        return selected
    review_body=json.loads(json.dumps(body));review_body['schedule']['requestId']=str(uuid4());review_body['definition']['policyRevision']=2
    reviewed=managed(create(review_body))
    assert query(f"SELECT mode||'|'||(proposal IS NOT NULL)::text FROM automation.lifecycle_results WHERE run_id='{reviewed['id']}';")=='review|true'
    assert query(f"SELECT count(*) FROM lifecycle.commands WHERE operation_id='{reviewed['id']}';")=='0'
    invalid_review=json.loads(json.dumps(review_body));invalid_review['schedule'].update(requestId=str(uuid4()),name='Synthetic invalid review proof')
    bad_job=create(invalid_review)
    bad_claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker_id}',120)::text;"))
    assert bad_claim['jobId']==bad_job['id']
    bad_token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{bad_claim['id']}',{bad_claim['fence']},'{worker_id}');")
    try:
        query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{bad_token}');DO $probe$DECLARE saved jsonb=automation.capture_lifecycle_source();BEGIN "
            f"PERFORM automation.publish_lifecycle_result('review',jsonb_build_object('request',jsonb_build_object('operationId','{bad_claim['id']}','context',saved->'context'),'review','{{}}'::jsonb),NULL);END$probe$;COMMIT;")
    except RuntimeError as error:assert 'lifecycle_schedule_result' in str(error),str(error)
    else:raise AssertionError('A scheduled review retained a missing hash and an unscoped incomplete request')
    print('PASS scheduled review refuses an incomplete, unscoped proposal before terminal publication',flush=True)
    unrelated_token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{bad_claim['id']}',{bad_claim['fence']},'{worker_id}');")
    try:
        query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{unrelated_token}');DO $probe$DECLARE saved jsonb=automation.capture_lifecycle_source();request jsonb;target jsonb;BEGIN "
            f"target=jsonb_set(saved#>'{{definition,target}}','{{scopeId}}',to_jsonb('{uuid4()}'::text));"
            f"request=jsonb_build_object('operationId','{bad_claim['id']}','context',saved->'context','target',target,'action','futures.margin',"
            "'policyId',saved#>'{definition,policyId}','policyRevision',saved#>'{definition,policyRevision}','automatic',false,'previewHash',repeat('a',64),"
            f"'selection',jsonb_build_object('expectedRevision',1,'expectedMarginRevision',1,'throughOccurrence','{occurrence['id']}','correctionDue','[]'::jsonb));"
            f"PERFORM automation.publish_lifecycle_result('review',jsonb_build_object('request',request,'review',jsonb_build_object('hash',repeat('a',64),'details','{{}}'::jsonb,'offer',jsonb_build_object('target',target,'action','futures.margin','occurrenceId','{occurrence['id']}','eligible',true))),NULL);END$probe$;COMMIT;")
    except RuntimeError as error:assert 'lifecycle_schedule_scope' in str(error),str(error)
    else:raise AssertionError('A scheduled review retained an unrelated target behind a syntactically valid hash')
    print('PASS a complete-looking reviewed offer cannot substitute another native target',flush=True)

    current_policy.update(operationId=str(uuid4()),expectedRevision=2,mode='automatic')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(current_policy)});")
    automatic=json.loads(json.dumps(body));automatic['schedule']['requestId']=str(uuid4());automatic['definition']['policyRevision']=3
    executed=managed(create(automatic))
    assert query(f"SELECT mode||'|'||(command_id=run_id)::text FROM automation.lifecycle_results WHERE run_id='{executed['id']}';")=='automatic|true'
    assert query(f"SELECT count(*) FROM lifecycle.commands WHERE operation_id='{executed['id']}';")=='1'
    assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_obligations WHERE catalog_id='{catalog}';")=='1'
    assert query(f"SELECT count(*) FROM trading.gas_owned_lots WHERE catalog_id='{catalog}';")=='0'
    print('PASS review queues no financial effects; explicit automatic policy commits native final margin, surviving obligation and terminal job together without invented title',flush=True)
    scoped=json.loads(json.dumps(automatic));scoped['schedule'].update(requestId=str(uuid4()),name='Synthetic exact scheduled authority')
    scoped_job=create(scoped)
    owned=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker_id}',120)::text;"))
    assert owned['jobId']==scoped_job['id']
    scoped_token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{owned['id']}',{owned['fence']},'{worker_id}');")
    wrong_operation=str(uuid4())
    try:
        query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{scoped_token}');DO $probe$DECLARE saved jsonb=automation.capture_lifecycle_source();body jsonb;BEGIN "
            f"body=jsonb_build_object('operationId','{wrong_operation}','context',saved->'context','target',saved#>'{{definition,target}}','action','futures.margin',"
            "'policyId',saved#>'{definition,policyId}','policyRevision',saved#>'{definition,policyRevision}','automatic',true,'previewHash',repeat('0',64),"
            f"'selection',jsonb_build_object('expectedRevision',1,'expectedMarginRevision',2,'throughOccurrence','{occurrence['id']}','correctionDue','[]'::jsonb));"
            f"PERFORM lifecycle.capture_action_authority('{catalog}','futures.margin',body);END$probe$;COMMIT;")
    except RuntimeError as error:
        assert 'lifecycle_schedule_scope' in str(error),str(error)
    else:raise AssertionError('A scheduled context admitted another command operation identity')
    print('PASS scheduled source-only authority rejects an unrelated command before product execution',flush=True)
    with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=issuer):
        application='synthetic_lifecycle_epoch_'+uuid4().hex
        with ThreadPoolExecutor(max_workers=2) as pool:
            blocked=pool.submit(query,f"SET application_name='{application}';BEGIN;SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;SELECT pg_sleep(4);COMMIT;")
            deadline=time.monotonic()+5
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{application}' AND wait_event='PgSleep';")!='1':
                if blocked.done():blocked.result()
                if time.monotonic()>deadline:raise AssertionError('The owned epoch probe did not acquire its lock')
                time.sleep(0.03)
            try:
                query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{owned['id']}',{owned['fence']},'{worker_id}');")
            except RuntimeError as error:assert 'lifecycle_schedule_authority' in str(error),str(error)
            else:raise AssertionError('A scheduled attempt chained a successor gas grant through its first epoch wait')
            blocked.result()
        fresh=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{owned['id']}',{owned['fence']},'{worker_id}');")
        assert fresh and fresh!=scoped_token
    print('PASS scheduled first-wait admission retains the original owner gas deadline; a fresh successor attempt is admitted separately',flush=True)
    host=query('SHOW unix_socket_directories;');read_path=Path(host)/'lifecycle-read-check.json'
    def read_result(delayed):
        read_path.write_text(json.dumps(dict(manifest=manifest,host=host,jobId=reviewed['jobId'],delayed=delayed)));read_path.chmod(0o600)
        checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-schedule-read',str(read_path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=30)
        if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
        print(checked.stdout.strip(),flush=True)
    with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=issuer):
        read_result(True);read_result(False)
    # Use another real held future so omitted terminal work would leak NEW
    # variation margin and a delivery obligation, rather than just a receipt.
    second=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{setup['bookId']}','{manifest['revisionId']}',"
        f"'nymex_ng','{setup['contractId']}','2026-10-16T14:00:00Z',NULL,2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(setup['booking'])})::text;"))
    adoption={**setup['adoption'],'operationId':str(uuid4()),'tradeId':second['tradeId'],'bindingId':second['bindingId'],
        'contextId':call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")}
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption)});")
    unfinished=json.loads(json.dumps(automatic));unfinished['schedule'].update(requestId=str(uuid4()),name='Synthetic atomic terminal publication')
    unfinished['definition']['target'].update(scopeId=second['tradeId'],bindingId=second['bindingId'])
    path=Path(host)/'lifecycle-missing-result-check.json'
    for early in (False,True):
        # Each adversarial transaction has its own real, bounded attempt. A
        # rollback does not extend the previous attempt's original deadline.
        unfinished['schedule'].update(requestId=str(uuid4()),name='Synthetic atomic terminal '+str(early))
        terminal_job=create(unfinished)
        terminal_claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker_id}',120)::text;"))
        assert terminal_claim['jobId']==terminal_job['id']
        path.write_text(json.dumps(dict(manifest=manifest,host=host,claim=terminal_claim,earlySeal=early)));path.chmod(0o600)
        check=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-schedule-no-result',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=45)
        if check.returncode:raise RuntimeError(check.stdout+check.stderr)
        print(check.stdout.strip(),flush=True)
        assert query(f"SELECT count(*) FROM settlement_data.futures_margin_determinations WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{second['tradeId']}','{second['bindingId']}');")=='0'
        assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_obligations WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{second['tradeId']}','{second['bindingId']}');")=='0'
    unfinished['schedule'].update(requestId=str(uuid4()),name='Synthetic complete terminal')
    terminal_job=create(unfinished)
    terminal_claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker_id}',120)::text;"))
    assert terminal_claim['jobId']==terminal_job['id']
    path.write_text(json.dumps(dict(manifest=manifest,host=host,claim=terminal_claim)));path.chmod(0o600)
    completed=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-schedule-run',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=45)
    if completed.returncode:raise RuntimeError(completed.stdout+completed.stderr)
    assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_obligations WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{second['tradeId']}','{second['bindingId']}');")=='1'
    print('PASS omitted result and early constraint draining both roll back real financial effects; a fresh owned task for the same occurrence then completes normally',flush=True)
    return dict(closeId=close,definition=automatic['definition'],setup=setup,reviewJobId=reviewed['jobId'])








def checks(query,manifest):
    for signature,identity in _PUBLIC_OIDS.items():
        assert query(f"SELECT to_regprocedure('{signature}')::oid;")==identity,'A qualified public lifecycle function identity changed'
    for signature in SCOPE_HELPERS:
        private=signature.replace('(', '_pre_schedule_scope(',1)
        assert query(f"SELECT has_function_privilege('preview_runtime','{private}','EXECUTE');")=='f'
    physical_checks(query,manifest,configuration_checks=configured)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
