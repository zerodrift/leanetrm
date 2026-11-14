"""Qualify explicit economic cash funding and exact native input custody."""
from copy import deepcopy
from decimal import Decimal,localcontext,ROUND_HALF_EVEN
from fractions import Fraction
import json
from uuid import uuid4

from assistant_context import migration_body
from check_reference_editing import main
from check_lifecycle_future_worker import fixture


def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def request():
    return dict(context=dict(snapshotId=str(uuid4())),bumps=[],stress=[],detail='holdings',economic=dict(
        projection=dict(implementation='native_portfolio_economic_lifecycle_v2',session='synthetic_cash_session',sessionVersion=1,
            horizonPolicy='source_known_effective_session_schedule_v1',rateTimingPolicy='linear_utc_day_discount_ratio_v1',
            eventMarketPolicy='selected_contract_projection_curves_v1',gasOwned=[],cashAccumulationSelections=[],
            policy=dict(implementation='economic_lifecycle_projection_v1',pricePath='shock_then_hold_native_quotes_v1',
                cashDisposition='project_scheduled_cash_paid_at_due_v1',cashAccumulation='nominal_currency_cash_v1',
                numericalPolicy='exact_residual_plus_horizon_cash_less_opening_v1',nonPriceInputs='captured_rates_and_volatility_held_v1',
                inputExpiry='declared_curve_expiry_policy_v1',physicalDelivery='retain_generated_delivery_until_observed_performance_v1')),
        policy=dict(implementation='economic_lifecycle_no_variable_factors_v1',confidence='0.99',quantilePolicy='empirical_inverse_cdf_v1',
            tailPolicy='signed_upper_loss_fractional_mass_uniform_ties_v1'),history=None,simulation=None,factors=[]))


def before_migration(query,migration):
    if migration.name!='0291_portfolio_cash_accumulation.sql':return
    try:query('SELECT risk.economic_commands('+literal(request())+');')
    except RuntimeError as error:assert 'risk_economic_input'in str(error),str(error)
    else:raise AssertionError('The predecessor unexpectedly admitted the new explicit funding profile.')
    body=migration_body(migration.read_text())
    signatures=('risk.economic_commands(jsonb)','risk.retain_economic(risk.jobs)','risk.retain_connected(risk.jobs,jsonb)')
    for signature in signatures:
        for change in (
            f'ALTER FUNCTION {signature} STABLE;',f'ALTER FUNCTION {signature} COST 101;',
            f"UPDATE pg_proc SET prosupport='pg_catalog.textlike_support'::regproc WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET prorows=1 WHERE oid='{signature}'::regprocedure;",f'ALTER FUNCTION {signature} STRICT;',f'ALTER FUNCTION {signature} LEAKPROOF;',
            f'ALTER FUNCTION {signature} PARALLEL SAFE;',f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',f'GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;',
            f'GRANT EXECUTE ON FUNCTION {signature} TO openetrm_risk_worker WITH GRANT OPTION;',f'ALTER FUNCTION {signature} OWNER TO CURRENT_USER;',
            f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargmodes=ARRAY['i']::\"char\"[] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargnames=ARRAY['synthetic_changed'] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{signature}'::regprocedure;",
            f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;",
        ):
            try:query('BEGIN;'+change+body+'ROLLBACK;')
            except RuntimeError as error:assert 'cash_accumulation_upgrade:'in str(error),(signature,change,str(error))
            else:raise AssertionError('A changed economic custody helper was admitted: '+change)
    query("CREATE TABLE public.synthetic_cash_upgrade_oids AS SELECT oid,oid::regprocedure::text signature FROM pg_proc WHERE oid=ANY(ARRAY["
          "'risk.economic_commands(jsonb)'::regprocedure,'risk.retain_economic(risk.jobs)'::regprocedure,'risk.retain_connected(risk.jobs,jsonb)'::regprocedure]);")
    print('PASS explicit funding predecessor refuses v2; changed helper bodies, metadata, owners and ACLs refuse before upgrade',flush=True)


def checks(query,manifest):
    assert query('SELECT bool_and(oid=signature::regprocedure::oid)FROM public.synthetic_cash_upgrade_oids;')=='t'
    for name,args in [('economic_commands','jsonb'),('retain_economic','risk.jobs')]:
        signature=f'risk.{name}_pre_explicit_cash({args})'
        assert query(f"SELECT NOT has_function_privilege('public','{signature}','EXECUTE')AND NOT has_function_privilege('openetrm_runtime','{signature}','EXECUTE')AND NOT has_function_privilege('openetrm_risk_worker','{signature}','EXECUTE');")=='t'
    signature='security.read_projection_cash_curve(uuid,jsonb,jsonb,timestamptz)'
    assert query(f"SELECT NOT has_function_privilege('public','{signature}','EXECUTE')AND has_function_privilege('openetrm_runtime','{signature}','EXECUTE')AND has_function_privilege('openetrm_risk_worker','{signature}','EXECUTE');")=='t'
    current=request();original=deepcopy(current)
    original['economic']['projection'].update(implementation='native_portfolio_economic_lifecycle_v1')
    del original['economic']['projection']['cashAccumulationSelections']
    assert query('SELECT risk.economic_commands('+literal(current)+')=risk.economic_commands('+literal(original)+');')=='t'
    for field,value in [('cashAccumulationSelections',None),('cashAccumulationSelections',{}),('sessionVersion',0)]:
        invalid=deepcopy(current);invalid['economic']['projection'][field]=value
        try:query('SELECT risk.economic_commands('+literal(invalid)+');')
        except RuntimeError as error:assert 'risk_economic_input'in str(error),str(error)
        else:raise AssertionError('Malformed explicit economic settings were accepted.')
    invalid=deepcopy(original);invalid['economic']['projection']['cashAccumulationSelections']=[]
    try:query('SELECT risk.economic_commands('+literal(invalid)+');')
    except RuntimeError as error:assert 'risk_economic_input'in str(error),str(error)
    else:raise AssertionError('The legacy profile accepted a new financial selection field.')
    print('PASS explicit funding native envelope: unchanged legacy request, explicit empty nominal selection, closed profile and preserved helper OIDs',flush=True)
    from check_lifecycle_future_worker import checks as future_workers
    future_workers(query,manifest,execution_price='2.8',daily_at='2020-12-21T08:00:00Z',daily_due_at='2020-12-21T12:00:00Z',source_fields=funding_source,job_checks=custody_checks)


def custody_checks(query,manifest,jobs):
    original=json.loads(query(f"SELECT to_jsonb(j)FROM risk.jobs j WHERE id='{jobs[0]['id']}';"))
    catalog,tenant,workspace=(manifest[k]for k in('catalogId','tenantId','workspaceId'))
    def runtime(sql):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
            f"SELECT security.use_market_context('{catalog}','{original['context_id']}');"
            f"SELECT security.use_trading_context('{catalog}','{original['valuation_at']}','{original['frame']['businessDate']}');"
            f"SELECT security.admit_trading_business_source('{catalog}','{original['context_id']}','{original['valuation_at']}','{original['frame']['businessDate']}');"+sql+'ROLLBACK;')
    unchanged=query('SELECT jsonb_build_array((SELECT count(*)FROM risk.jobs),(SELECT count(*)FROM risk.job_markets),(SELECT count(*)FROM risk.job_connected_markets));')
    def change_value(frame):frame['economicLifecycle']['cashAccumulation']['selections'][0]['source']['selections'][0]['observation']['value']='0.06'
    def change_series(frame):frame['economicLifecycle']['cashAccumulation']['selections'][0]['definition']['tenors'][0]['series']['id']=str(uuid4())
    def change_convention(frame):frame['economicLifecycle']['cashAccumulation']['selections'][0]['definition']['conventions']['compounding']='continuous'
    def change_mapping(frame):frame['economicLifecycle']['cashAccumulation']['selections'][0]['selection']['mappingId']=str(uuid4())
    def omit_source(frame):del frame['sourceInputs'][frame['economicLifecycle']['cashAccumulation']['selections'][0]['selection']['curve']['revision']['revisionId']]
    def forge_price_axis(frame):
        selected=deepcopy(frame['scenarioAxes'][0]);selected['marketRevisionId']=frame['economicLifecycle']['cashAccumulation']['selections'][0]['selection']['curve']['revision']['revisionId']
        frame['scenarioAxes'].append(selected)
    for mutate in (change_value,change_series,change_convention,change_mapping,omit_source,forge_price_axis):
        changed=deepcopy(original['frame']);mutate(changed)
        try:runtime(f"SELECT risk.submit('{catalog}','{uuid4()}','{original['context_id']}','{original['revision_id']}','{original['valuation_at']}',{literal(original['request'])},{literal(changed)});")
        except RuntimeError as error:
            assert any(code in str(error)for code in ('risk_economic_cash','projection_cash_source','risk_graph_input','temporal_inputs')),str(error)
        else:raise AssertionError('An altered cash input proof or a fabricated rate-only price axis was retained: '+mutate.__name__)
        assert unchanged==query('SELECT jsonb_build_array((SELECT count(*)FROM risk.jobs),(SELECT count(*)FROM risk.job_markets),(SELECT count(*)FROM risk.job_connected_markets));')
    print('PASS explicit funding native custody refuses substituted values, tenor series, conventions, mappings, omitted source and price-axis graph evasion atomically',flush=True)
    selected=original['request']['economic']['projection']['cashAccumulationSelections'][0]
    # All test grant changes roll back with this one owned transaction. The
    # successor is published before admission, so natural expiry introduces no
    # intervening security-generation change or mutable authorization cache.
    try:query('BEGIN;'+f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id='{original['actor_id']}' AND NOT revoked;"
        +f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,granted_to+interval '0.1 seconds','9999-12-31',false FROM security.role_grants WHERE principal_id='{original['actor_id']}' AND NOT revoked;"
        +'CREATE TEMP TABLE synthetic_funding_token(token uuid);GRANT SELECT,INSERT ON synthetic_funding_token TO preview_authenticator,preview_runtime;'
        +'SET SESSION AUTHORIZATION preview_authenticator;'
        +f"INSERT INTO synthetic_funding_token SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);"
        +'SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context((SELECT token FROM synthetic_funding_token));'
        +f"SELECT security.use_market_context('{catalog}','{original['context_id']}');SELECT security.use_trading_context('{catalog}','{original['valuation_at']}','{original['frame']['businessDate']}');"
        +'CREATE TEMP TABLE synthetic_funding_until(value timestamptz);'
        +f"INSERT INTO synthetic_funding_until SELECT security.admit_trading_business_source('{catalog}','{original['context_id']}','{original['valuation_at']}','{original['frame']['businessDate']}');"
        +f"SELECT security.read_projection_cash_curve('{catalog}',{literal(original['frame'])},{literal(selected)},(SELECT value FROM synthetic_funding_until));"
        +"SELECT pg_sleep(2.3);DO $$BEGIN IF NOT security.has_permission('ReadMarketData')THEN RAISE EXCEPTION 'synthetic_successor_not_live';END IF;END$$;"
        +f"SELECT security.read_projection_cash_curve('{catalog}',{literal(original['frame'])},{literal(selected)},(SELECT value FROM synthetic_funding_until));ROLLBACK;")
    except RuntimeError as error:
        assert 'futures_margin_denied'in str(error) and 'synthetic_successor_not_live'not in str(error),str(error)
    else:raise AssertionError('A cash-source read extended its original authority under a later grant.')
    print('PASS explicit funding read retains its original authority after natural grant expiry despite a live prepublished successor',flush=True)


def funding_source(query,manifest,frame):
    from rate_curve_fixture import admitted_curve,discount_default
    catalog,reference,revision=(manifest[k]for k in('catalogId','referenceCatalogId','referenceRevisionId'))
    funding=str(uuid4());snapshot=str(uuid4());mapping=str(uuid4());observation=str(uuid4());code='synthetic_cash_funding'
    refs=f"'{reference}','{revision}'"
    definition,series=admitted_curve(catalog,funding,refs,code,'Synthetic cash funding',[('2021-01-18','2021-01-18')],currency='usd',
        unit='decimal_per_year',specification='test_zero_rate',source='fixture',day_count='actual_365_fixed',compounding='simple',
        interpolation='log_linear_discount',extrapolation='none')
    query(f"BEGIN;INSERT INTO market_data.revisions VALUES('{catalog}','{funding}',{refs},'draft',0);"
          f"INSERT INTO market_data.revision_periods VALUES('{catalog}','{funding}','2020-01-01','2022-01-01');"
          'DO $funding$ BEGIN '+definition+' END $funding$;'
          f"UPDATE market_data.revisions SET state='sealed'WHERE(catalog_id,id)=('{catalog}','{funding}');"
          'INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,'
          'source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value)'
          f"VALUES('{catalog}','{observation}','{funding}','{series[0]}',{refs},'fixture','synthetic_funding_{observation}','1','parameter','final','{frame['at']}',clock_timestamp(),0.05);"
          f"INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
          +discount_default(catalog,mapping,funding,refs,'usd',code,f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')",'2020-01-01T00:00:00Z')+'COMMIT;')
    selected=json.loads(query(f"SELECT jsonb_build_object('currency',jsonb_build_object('revision',jsonb_build_object('catalogId','{reference}','revisionId','{revision}'),'code','usd'),"
        f"'snapshotId','{snapshot}','curve',jsonb_build_object('revision',jsonb_build_object('catalogId',catalog_id,'revisionId',revision_id),'code',code),"
        f"'mappingId','{mapping}','definitionId',definition_id,'definitionVersion',definition_version)FROM market_data.rate_curves WHERE(catalog_id,revision_id,code)=('{catalog}','{funding}','{code}');"))
    def source(rev):return dict(definitionRevisionId=rev,referenceCatalogId=reference,referenceRevisionId=revision)
    matrix={manifest['revisionId']:dict(source=source(manifest['revisionId']),observationIds=frame['observationIds'],frozenObservationIds=[],rootCurves=['future'],
                curveContracts={'future':['50000000-0000-0000-0000-000000000001']}),
            funding:dict(source=source(funding),observationIds=[observation],frozenObservationIds=[],rootCurves=[],curveContracts={})}
    price=query(f"SELECT o.value FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)"
        f" WHERE o.id IN({','.join(chr(39)+p+chr(39)for p in frame['observationIds'])}) AND s.contract_id='50000000-0000-0000-0000-000000000001' AND s.quote_code='test_gas_outright' AND o.role='daily_settlement';")
    assert len(price.splitlines())==1
    # Independent decimal exponentials and exact rational UTC weights. The
    # captured curve is simple ACT/365 with one 31-day 5% pillar. Native dated
    # factors and each conditional factor round half-even at 18 places.
    with localcontext()as context:
        context.prec=100;unit=Decimal('1e-18')
        pillar=(1/(1+Decimal('0.05')*31/365)).quantize(unit,rounding=ROUND_HALF_EVEN)
        d=[Decimal(1),*((pillar.ln()*n/31).exp().quantize(unit,rounding=ROUND_HALF_EVEN)for n in range(1,5))]
        opening=(d[0]+2*d[1])/3
        due=((d[3]+d[4])/2/opening).quantize(unit,rounding=ROUND_HALF_EVEN)
        horizon=((d[3]+2*d[4])/3/opening).quantize(unit,rounding=ROUND_HALF_EVEN)
        cash=((Decimal(price)-Decimal('2.8'))*80).quantize(Decimal('0.01'),rounding=ROUND_HALF_EVEN)
        oracle=Fraction(cash)*Fraction(due)/Fraction(horizon)
    return dict(snapshotId=snapshot,cashAccumulation=selected,sourceInputs=matrix,fundingOracle=str(oracle))


if __name__=='__main__':main(checks=checks,before_migration=before_migration,fixture_factory=fixture)
