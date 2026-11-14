"""Native risk reader scope over actual synthetic physical and option holdings."""
import json
from uuid import uuid4
from check_reference_editing import main
from check_futures_physical_delivery import fixture, checks as physical_checks
from check_lifecycle_actions import upgrade_checks as action_upgrade_checks
from check_risk_jobs_db import provision_worker


original_needs_gas=None

def upgrade_checks(query,migration):
    global original_needs_gas
    action_upgrade_checks(query,migration)
    if migration.name=='0290_risk_gas_consumers.sql':
        original_needs_gas=query("SELECT pg_get_functiondef('risk.needs_gas(risk.jobs)'::regprocedure);")
        from check_fact_anchor_upgrade import run_checks
        run_checks(query,migration,number='0290',guard='risk_gas_consumers_upgrade',signatures=('risk.needs_gas(risk.jobs)',))
        from assistant_context import check_guard
        check_guard(query,migration.read_text(),'0290','risk_gas_consumers_upgrade',[(
            "UPDATE pg_proc SET proargnames=ARRAY['synthetic_changed']::text[] WHERE oid='risk.needs_gas(risk.jobs)'::regprocedure;",'risk.needs_gas(risk.jobs)')])


def checks(query, manifest):
    # Reuse the separately qualified native booking/adoption/final/correction
    # producers. These are real synthetic rows, not invented accepted facts.
    physical_checks(query, manifest)
    catalog,tenant,workspace=(manifest[k]for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];at='2026-10-16T16:00:00Z';day='2026-10-16'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def user(sql,profile='risk'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return user(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
    def reject(work,code):
        try:work()
        except RuntimeError as error:assert code in str(error),str(error)
        else:raise AssertionError('Accepted native source expected to refuse '+code)
    held=json.loads(query(f"SELECT jsonb_build_object('trade',a.trade_id,'binding',a.binding_id,'book',a.book_id,'policy',a.policy_id,'revision',a.policy_revision,'contract',p.contract_id) "
        f"FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(a.catalog_id,a.policy_id,a.policy_revision) WHERE a.catalog_id='{catalog}' AND p.delivery_profile_id IS NOT NULL ORDER BY a.trade_id LIMIT 1;"))
    book=held['book'];contract=held['contract'];policy=held['policy']
    parties={}
    for role,table in [('legalEntity','legal_entities'),('counterparty','counterparties')]:
        row=query(f"SELECT id::text||'|'||revision::text FROM trading.{table} WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;").split('|')
        parties[role+'Id'],parties[role+'Revision']=row[0],int(row[1])
    other_book=str(uuid4())
    query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
        f"INSERT INTO trading.books SELECT(jsonb_populate_record(NULL::trading.books,to_jsonb(b)||jsonb_build_object('id','{other_book}','name','Synthetic outside risk scope','publication_order',(SELECT max(publication_order)FROM trading.publications WHERE catalog_id='{catalog}')))).* FROM trading.books b WHERE(b.catalog_id,b.id)=('{catalog}','{book}');COMMIT;")
    outside=json.loads(user(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{other_book}','{manifest['revisionId']}',"
        f"'nymex_ng','{contract}','2026-10-16T14:00:00Z',NULL,1,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(parties)})::text;",'trader'))
    option_terms=json.loads(query(f"SELECT jsonb_build_object('series',s.code,'contract',c.id,'expiry',to_char(c.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')) FROM ref_data.futures_option_series s JOIN market_data.contracts c ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) JOIN market_data.contracts o ON(o.catalog_id,o.revision_id,o.kind,o.futures_option_series_code)=(c.catalog_id,c.revision_id,'future_option',s.code) WHERE(c.catalog_id,c.revision_id,s.product_code,s.option_right)=('{catalog}','{manifest['revisionId']}','nymex_on','call') ORDER BY s.expiry_at,s.strike LIMIT 1;"))
    series=option_terms['series']
    existing=json.loads(query(f"SELECT jsonb_build_object('accountId',g.account_id,'actionPolicyId',g.action_policy_id,'actionPolicyRevision',g.action_policy_revision) FROM trading.gas_futures_delivery_terms g JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision)=(g.catalog_id,g.profile_id,g.profile_revision) WHERE(p.catalog_id,p.id,p.revision)=('{catalog}','{policy}',{held['revision']});"))
    option_delivery=dict(id=str(uuid4()),name='Synthetic option delivery',implementation='gas_future_physical_delivery_v1',definitionRevisionId=manifest['revisionId'],
        contractId=option_terms['contract'],accountId=existing['accountId'],fromAt='2026-12-01T00:00:00Z',toAt='2027-01-01T00:00:00Z',cashDueAt='2027-01-02T12:00:00Z',
        carrying='currency_curve_v1',principalIncrement='0.01',principalTie='to_even',principalScope='delivery_cumulative_principal_total_v1',correctionDuePolicy='contractual_principal_due_v1',
        actionPolicyId=existing['actionPolicyId'],actionPolicyRevision=existing['actionPolicyRevision'])
    option_policy=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=option_terms['contract'],settlementCurve='nymex_ng',
        referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency='usd',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',
        opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',
        implementation='dated_futures_margin_policy_v2',delivery=dict(id=option_delivery['id'],revision=1),
        occurrences=[dict(id=str(uuid4()),at=option_terms['expiry'],dueAt=option_terms['expiry'],phase='final')])
    user(f"SELECT settlement_data.save_futures_delivery_profile('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=at,profile=option_delivery))});",'trader')
    user(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=at,policy=option_policy))});",'editor')
    option=json.loads(user(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','{series}',2,0.1,'2026-10-19',"
        f"'premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(parties)})::text;",'trader'))
    action=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.expiry',mode='manual',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    user(f"SELECT lifecycle.save_policy('{catalog}',{literal(action)});",'trader')
    profile=dict(operationId=str(uuid4()),tradeId=option['tradeId'],action='option.expiry',expectedRevision=0,
        policyId=action['policyId'],policyRevision=1,marginPolicyId=option_policy['id'],marginPolicyRevision=1,futureMarkCurve='nymex_ng',expiryCurve='nymex_ng')
    user(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(profile)});",'trader')
    unused_policy={**option_policy,'id':str(uuid4())}
    user(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=at,policy=unused_policy))});",'editor')
    context=capture()
    def scope():return f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}','{day}'::date);"
    seed=next(f for f in manifest['frames']if f['at']==at)
    curve=dict(revision=dict(catalogId=catalog,revisionId=manifest['revisionId']),code='nymex_ng')
    factor=dict(coordinate='market_quote',target=dict(kind='contract',curve=curve,contract=dict(revision=curve['revision'],id=contract)))
    frame=dict(id=str(uuid4()),name='Synthetic owned risk source scope',at=at,businessDate=day,contextId=context,observationIds=seed['observationIds'],
        frozenObservationIds=[],curveContracts=seed['curveContracts'],sourceInputs=None,resolvesLatestCorrection=False,
        source=dict(definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId']),
        supplementalMarkets={},swapMarketSelections={},excludedPositions=[],optionCalibrations=[],ppaReadContexts=[],
        scenarioAxes=[dict(id='a'*64,marketRevisionId=manifest['revisionId'],factor=factor,points=[dict(curve=curve,pointId=contract)])],
        connectedMarkets=[dict(revisionId=manifest['revisionId'],manifestId='b'*64,sourceExecutionConvention='contract_lot_execution_pnl_v2',recipes=[],samples=[])])
    # Native custody validates these scopes and prices no result. Numerical
    # workers separately rebuild and compare the actual connected graph.
    request=dict(context=dict(snapshotId=frame['id'],contextId=context,knowledgeMode='retained',convention='trading_workspace_v1'),bookIds=[book],portfolioIds=[],
        tradeIds=[option['tradeId']],bumps=[dict(factor=factor,size='0.01',convention='absolute_curve_coordinate_symmetric_v1')],stress=[],detail='holdings',
        engineVersion='synthetic_lifecycle_reader_scope_v1',convention='portfolio_instrument_repricing_v1',numericalPolicy='exact_rational_portfolio_repricing_v1',
        pricingPolicy=dict(curveNumericalPolicy='exact_18_fractional_abs_lt_1e24_v1',riskNumericalPolicy='exact_rational_portfolio_repricing_v1',
            futuresConvention='native_margin_remaining_value_v1',swapConvention='independent_side_financial_swap_v2',rateNumericalPolicy='rate_curve_lattice30_to_even_18_v1',
            discountInterpolationPolicy='log_discount_geometric_root_to_even_18_v1',bumpConvention='absolute_quote_v1',bumpBasis='trading_tick',bumpTicks=1))
    def submit():return json.loads(user(scope()+f"SELECT risk.submit('{catalog}','{uuid4()}','{context}','{manifest['revisionId']}','{at}',{literal(request)},{literal(frame)})::text;"))
    # The ordinary option value does not consume its saved physical action
    # profile. Its exact retained scope must remain readable without Gas.
    provision_worker(query,manifest,'synthetic_scope_worker');provision_worker(query,manifest,'synthetic_scope_no_gas_worker')
    ordinary=submit()
    assert query(f"SELECT NOT risk.needs_gas(j) FROM risk.jobs j WHERE id='{ordinary['jobId']}';")=='t'
    poison="""CREATE OR REPLACE FUNCTION risk.lifecycle_scope_policies(job risk.jobs) RETURNS TABLE(policy_id uuid,policy_revision integer,book_id uuid)
      LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $poison$ BEGIN RAISE EXCEPTION 'unused_lifecycle_policy_read';END$poison$;"""
    assert original_needs_gas
    for reads in (True,False):
        restore=original_needs_gas+';' if reads else ''
        try:value=query(f"BEGIN;{restore}{poison}SELECT risk.needs_gas(j) FROM risk.jobs j WHERE id='{ordinary['jobId']}';ROLLBACK;")
        except RuntimeError as error:assert reads and 'unused_lifecycle_policy_read' in str(error),str(error)
        else:assert not reads and value=='f','An ordinary option job read an unused physical continuation policy'
    plain=json.loads(query("SET SESSION AUTHORIZATION synthetic_scope_worker;SELECT risk.claim(30,'synthetic_lifecycle_reader_scope_v1')::text;"))
    assert plain['jobId']==ordinary['jobId']
    query(f"SET SESSION AUTHORIZATION synthetic_scope_worker;BEGIN;SELECT risk.bind('{plain['jobId']}','{plain['leaseToken']}');COMMIT;SELECT risk.finish('{plain['jobId']}','{plain['leaseToken']}',NULL,'qualification_complete');")
    # Economic continuation does consume that exact source-visible profile.
    assert query(f"SELECT risk.needs_gas((jsonb_populate_record(NULL::risk.jobs,to_jsonb(j)||jsonb_build_object('request',j.request||jsonb_build_object('economic',jsonb_build_object('projection',jsonb_build_object('gasOwned','[]'::jsonb))))))) FROM risk.jobs j WHERE id='{ordinary['jobId']}';")=='t'
    # Native physical-future PV reads its saved delivery document even before
    # stock exists. This actual consumer still requires Gas for user and service.
    request['tradeIds']=[option['tradeId'],held['trade']]
    assert query(f"SELECT count(*) FROM trading.gas_owned_positions WHERE catalog_id='{catalog}' AND effective_at<='{at}';")=='0'
    reject(submit,'risk_job_denied')
    gas_role=str(uuid4())
    query(f"INSERT INTO security.roles VALUES('{gas_role}','synthetic_scope_gas_reader','Synthetic scope gas reader');INSERT INTO security.role_permissions VALUES('{gas_role}','ReadGasOwnedHoldings');"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{gas_role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.identity_bindings WHERE issuer='{issuer}' AND subject='risk';"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{gas_role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM risk.workers WHERE login='synthetic_scope_worker';")
    accepted=submit()
    def worker(sql):return query('SET SESSION AUTHORIZATION synthetic_scope_worker;'+sql)
    assert query("SET SESSION AUTHORIZATION synthetic_scope_no_gas_worker;SELECT risk.claim(30,'synthetic_lifecycle_reader_scope_v1')::text;")=='', 'A physical native-state job was claimed without service Gas authority'
    assert query(f"SELECT state='queued' AND attempt=0 FROM risk.jobs WHERE id='{accepted['jobId']}';")=='t'
    claim=json.loads(worker("SELECT risk.claim(30,'synthetic_lifecycle_reader_scope_v1')::text;"));assert claim['jobId']==accepted['jobId']
    bind=f"BEGIN;SELECT risk.bind('{claim['jobId']}','{claim['leaseToken']}');"
    read=worker(bind+f"SELECT security.admit_trading_business_source('{catalog}','{context}','{at}','{day}'::date);"
        f"SELECT security.require_lifecycle_projection_profile('{catalog}','listed_option','{option['tradeId']}',NULL,1,'{context}','{at}','{day}'::date,settlement_data.futures_margin_preview_authority('{catalog}'));"
        f"SELECT jsonb_array_length(security.read_lifecycle_projection_profiles('{catalog}','listed_option','{option['tradeId']}',1,'{context}','{at}','{day}'::date,settlement_data.futures_margin_preview_authority('{catalog}')));"
        f"SELECT jsonb_array_length(lifecycle.read_option_known_history('{catalog}','{option['tradeId']}','{option['optionInstrumentId']}',1,'{context}','{at}','{day}')->'events');COMMIT;")
    assert read.splitlines()[-2:]==['1','0'],read
    visible=worker(bind+f"SELECT count(*) FROM settlement_data.futures_margin_policies WHERE id='{option_policy['id']}';SELECT count(*) FROM settlement_data.futures_margin_policies WHERE id='{unused_policy['id']}';SELECT count(*) FROM settlement_data.futures_margin_policy_occurrences WHERE policy_id='{unused_policy['id']}';COMMIT;")
    assert visible.splitlines()[-3:]==['1','0','0'],visible

    reject(lambda:worker(bind+f"SELECT settlement_data.read_futures_margin_state('{catalog}','{outside['tradeId']}','{outside['bindingId']}','{context}','{at}');COMMIT;"),'risk_job_scope')
    inside=worker(bind+f"SELECT(settlement_data.read_futures_margin_state('{catalog}','{held['trade']}','{held['binding']}','{context}','{at}')->>'available')::boolean;COMMIT;")
    assert inside.splitlines()[-1]=='t',inside
    worker(f"SELECT risk.finish('{claim['jobId']}','{claim['leaseToken']}',NULL,'qualification_complete');")
    query(f"UPDATE security.role_grants SET revoked=true WHERE role_id='{gas_role}' AND principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='{issuer}' AND subject='risk');")
    reject(lambda:user(f"SELECT risk.read('{accepted['jobId']}');"),'risk_job_denied')
    user(f"SELECT risk.read('{ordinary['jobId']}');")
    print('PASS gas consumer scope: unused option profile permits ordinary jobs; physical continuation/native state retain user/service Gas, exact source scope and result revocation',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
