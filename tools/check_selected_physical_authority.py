"""Selected physical continuation authority before any stock exists."""
import json
import os
from pathlib import Path
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from check_reference_editing import main
from check_futures_physical_delivery import fixture,checks as physical_checks
from check_futures_margin_adoption import successor_grants
from check_lifecycle_actions import upgrade_checks


def configured(query,manifest,setup):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer']
    assert query(f"SELECT count(*) FROM trading.gas_owned_positions WHERE catalog_id='{catalog}';")=='0'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    selected=json.loads(query("SELECT jsonb_build_object('series',s.code,'contract',f.id,'from',f.delivery_from,'to',f.delivery_to,"
        "'final',to_char(f.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'))::text FROM ref_data.futures_option_series s "
        "JOIN market_data.contracts o ON(o.reference_catalog_id,o.reference_revision_id,o.futures_option_series_code)=(s.catalog_id,s.revision_id,s.code) "
        "JOIN market_data.contracts f ON(f.catalog_id,f.revision_id,f.futures_series_code)=(o.catalog_id,o.revision_id,s.underlying_series_code) "
        f"WHERE(o.catalog_id,o.revision_id,s.product_code,s.option_right)=('{catalog}','{manifest['revisionId']}','nymex_on','call') ORDER BY s.expiry_at,s.strike LIMIT 1;"))
    series=selected['series']
    from datetime import date,timedelta
    delivery={**setup['profile'],'id':str(uuid4()),'contractId':selected['contract'],'fromAt':selected['from']+'T00:00:00Z',
        'toAt':selected['to']+'T00:00:00Z','cashDueAt':str(date.fromisoformat(selected['to'])+timedelta(days=1))+'T12:00:00Z'}
    captured=lambda:call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    profile_request=dict(operationId=str(uuid4()),expectedRevision=0,contextId=captured(),asOf=setup['at'],profile=delivery)
    call(f"SELECT settlement_data.save_futures_delivery_profile('{catalog}',{literal(profile_request)});")
    margin={**setup['marginRequest']['policy'],'id':str(uuid4()),'contractId':selected['contract'],'delivery':dict(id=delivery['id'],revision=1),
        'occurrences':[dict(id=str(uuid4()),at=selected['final'],dueAt=selected['final'],phase='final')]}
    margin_request=dict(operationId=str(uuid4()),expectedRevision=0,contextId=captured(),asOf=setup['at'],policy=margin)
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(margin_request)});",'editor')
    booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{setup['bookId']}','{manifest['revisionId']}','{series}',2,0.1,'2026-10-29','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(setup['booking'])})::text;"))
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.exercise',mode='review',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy)});")
    saved=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],action='option.exercise',expectedRevision=0,policyId=policy['policyId'],policyRevision=1,
        marginPolicyId=margin['id'],marginPolicyRevision=1,futureMarkCurve='nymex_ng',expiryCurve=None)
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(saved)});")
    source=call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
    request=dict(operationId=str(uuid4()),context=dict(contextId=source,asOf=setup['at'],businessDate='2026-10-16',snapshotId=str(uuid4())),
        target=dict(handler='listed_option',scopeId=booked['tradeId'],bindingId=booked['optionInstrumentId']),action='option.exercise',policyId=policy['policyId'],policyRevision=1,
        automatic=False,previewHash='0'*64,selection=dict(evidenceId=str(uuid4()),lots='1',expectedEventVersion=0,profileRevision=1))
    host=query('SHOW unix_socket_directories;');path=Path(host)/'selected-physical-authority.json'
    def managed(mode,body,deny,delay,expected_gas=False):
        application='synthetic_physical_authority_'+uuid4().hex
        path.write_text(json.dumps(dict(manifest=manifest,host=host,mode=mode,request=body,deny=deny,delay=delay,application=application,expectedGas=expected_gas)));path.chmod(0o600)
        def run():
            result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                '--','--selected-physical-authority',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=45)
            if result.returncode:raise RuntimeError(result.stdout+result.stderr)
            print(result.stdout.strip(),flush=True)
        return application,run
    for mode,body in [('policy_save',{**setup['marginRequest'],'operationId':str(uuid4())}),('adopt',{**setup['adoption'],'operationId':str(uuid4())}),('option_preview',request)]:
        profile='editor' if mode=='policy_save' else 'trader'
        with successor_grants(query,manifest,profile,'ReadGasOwnedHoldings',issuer=issuer):
            _,run=managed(mode,body,True,True);run()
            _,run=managed(mode,body,False,False);run()
    with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=issuer):
        _,read=managed('discovery',request,True,True);read()
        _,read=managed('discovery',request,False,False,True);read()
    principal=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{issuer}' AND subject='trader';")
    application,run=managed('option_preview',request,False,True)
    with ThreadPoolExecutor(max_workers=2) as pool:
        running=pool.submit(run);deadline=time.monotonic()+10
        while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{application}' AND wait_event='PgSleep';")!='1':
            if running.done():running.result()
            if time.monotonic()>deadline:raise AssertionError('The owned preview did not reach post-calculation work')
            time.sleep(.02)
        permissions=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE p.permission='ReadGasOwnedHoldings' AND p.role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
        revocation='synthetic_physical_revoke_'+uuid4().hex
        changing=pool.submit(query,f"SET application_name='{revocation}';DELETE FROM security.role_permissions WHERE permission='ReadGasOwnedHoldings' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
        try:
            deadline=time.monotonic()+4
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{revocation}' AND wait_event_type='Lock';")!='1':
                if changing.done():raise AssertionError('Revocation escaped the owned read security fence')
                if time.monotonic()>deadline:raise AssertionError('Revocation did not reach the owned read security fence')
                time.sleep(.02)
            running.result();changing.result()
            _,fresh=managed('option_preview',request,True,False);fresh()
        finally:
            changing.result()
            query("INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,"+literal(json.loads(permissions))+") ON CONFLICT DO NOTHING;")
    with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=issuer) as roles:
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()-interval '1 second' WHERE role_id=(SELECT id FROM security.roles WHERE code='{roles}_a');")
        _,read=managed('discovery',request,False,True);read()
    agent,delegation,role=(str(uuid4()) for _ in range(3))
    delegated=query(f"""BEGIN;
      INSERT INTO security.principals VALUES('{agent}','agent',true);
      INSERT INTO security.identity_bindings VALUES('{issuer}','synthetic_physical_reader','{agent}','agent_credential');
      INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{agent}','synthetic_physical_reader','Synthetic delegated reader',true,1);
      INSERT INTO security.role_permissions VALUES('a0000000-0000-0000-0000-000000000007','ReadGasOwnedHoldings') ON CONFLICT DO NOTHING;
      INSERT INTO security.roles VALUES('{role}','synthetic_physical_delegator','Synthetic physical delegator');
      INSERT INTO security.role_permissions VALUES('{role}','CreateDelegations');
      INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{agent}','a0000000-0000-0000-0000-000000000007','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false),
        (gen_random_uuid(),'{principal}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);
      INSERT INTO security.agent_delegations VALUES('{delegation}','{agent}','{principal}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);
      INSERT INTO security.delegation_permissions VALUES('{delegation}','ReadMarketData'),('{delegation}','ReadTrades');
      SET SESSION AUTHORIZATION preview_authenticator;
      SELECT security.issue_context('{issuer}','synthetic_physical_reader','agent_credential','{tenant}','{workspace}','{delegation}','preview_runtime',60) AS token \\gset
      SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context(:'token');
      DO $check$BEGIN IF security.has_permission('ReadGasOwnedHoldings') IS DISTINCT FROM false OR security.authority_until('ReadGasOwnedHoldings')<=clock_timestamp()
        THEN RAISE EXCEPTION 'Synthetic delegated subset premise is invalid';END IF;END$check$;
      SELECT lifecycle.discovery_authority('{catalog}',false)::text;ROLLBACK;""")
    assert json.loads(delegated)['gasUntil'] is None,'Optional discovery treated an undelegated gas grant as initial access'
    print('PASS optional delegated discovery: own and delegator gas grants do not substitute for the exact delegated permission subset',flush=True)
    assert query(f"SELECT count(*) FROM trading.gas_owned_positions WHERE catalog_id='{catalog}';")=='0'
    print('PASS physical admission without inventory: saved exact option/future policy, original deadlines, ordered revocation and initial optional discovery scope',flush=True)


def checks(query,manifest):physical_checks(query,manifest,configuration_checks=configured)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
