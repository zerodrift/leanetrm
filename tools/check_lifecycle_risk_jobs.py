"""Economic risk custody and worker reads in owned disposable PostgreSQL."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from uuid import uuid4
from check_reference_editing import main
from check_risk_jobs_db import provision_worker
from close_session_fixture import sql_session
from preview_fixture import catalogue_fixture, trading_fixture

HELPERS = ('risk.worker()','risk.claim(integer,text)','risk.owned_attempt(uuid,uuid,boolean,boolean)','risk.authorized(risk.jobs)', 'risk.worker_read()', 'risk.bind(uuid,uuid)',
           'risk.bind_pre_ppa_context(uuid,uuid)', 'risk.axis_commands(jsonb)', 'risk.retain_axes(risk.jobs,jsonb)',
           'risk.retain_parametric(risk.jobs)', 'risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)',
           'risk.guard_run()', 'risk.summary(risk.jobs)', 'security.authority_until(text)', 'risk.require_worker_attempt()',
           'security.require_gas_owned(uuid,boolean,boolean)')


READERS = ('settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz)',
 'settlement_data.require_futures_margin_read(uuid,uuid,timestamptz,timestamptz)',
 'settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz)',
 'settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)',
 'settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz)',
 'settlement_data.futures_margin_preview_authority(uuid)',
 'lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)',
 'lifecycle.read_swap_payment_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)',
 'security.require_gas_owned_valuation_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)',
 'security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)',
 'security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])',
 'security.require_cash_payment_read(uuid,uuid,timestamptz)',
 'security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)',
 'security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])',
 'security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz)')

READERS += ('security.require_lifecycle_projection_profile(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz)', 'settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz)', 'settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz)', 'settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz)', 'settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz)', 'settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz)')


def helper_metadata(query):
    result = {}
    for signature in HELPERS+READERS:
        result[signature] = json.loads(query("SELECT jsonb_build_object('oid',p.oid,'hash',md5(p.prosrc)) "
            f"FROM pg_proc p WHERE p.oid=to_regprocedure('{signature}');"))
    return result


_before = {}
def before_migration(query, migration):
    if migration.name != '0278_economic_lifecycle_risk_jobs.sql':
        return
    _before.update(helper_metadata(query))
    from check_fact_anchor_upgrade import run_checks
    run_checks(query,migration,number=migration.name[:4],guard='risk_economic_upgrade',signatures=(
        'security.authority_until(text)','risk.retain_axes(risk.jobs,jsonb)',
        'risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)','risk.require_worker_attempt()',
        'security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])'))


def checks(query, manifest, read_policy_checks=None):
    after=helper_metadata(query)
    assert all(after[s]['oid']==p['oid'] for s,p in _before.items()), 'An existing public helper OID changed'

    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))
    sql_session(query, manifest, 'synthetic_lifecycle_risk', 'test_business', '16:00', ['future'], [])
    sql_session(query, manifest, 'synthetic_alternative_risk', 'test_business', '17:00', ['future'], [])
    provision_worker(query, manifest, 'synthetic_lifecycle_worker')
    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    def user(sql, profile='risk'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    context = user(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
    seed=next(f for f in manifest['frames'] if f['at']=='2020-12-18T16:00:00Z')
    frame=dict(id=str(uuid4()), name='Synthetic economic risk inputs', at=seed['at'], businessDate='2020-12-18',contextId=context,
        observationIds=seed['observationIds'], frozenObservationIds=[],curveContracts=seed['curveContracts'],sourceInputs=None,
        resolvesLatestCorrection=False,source=dict(definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId']),
        supplementalMarkets={},swapMarketSelections={},excludedPositions=[],optionCalibrations=[],ppaReadContexts=[],scenarioAxes=[],
        connectedMarkets=[dict(revisionId=manifest['revisionId'],manifestId='b'*64,sourceExecutionConvention='contract_lot_execution_pnl_v2',recipes=[],samples=[])])
    books=json.loads(user(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{seed['at']}','2020-12-18'::date);"
        f"SELECT jsonb_agg(id ORDER BY id)::text FROM trading.books WHERE catalog_id='{catalog}';"))
    request=dict(context=dict(snapshotId=frame['id'],contextId=context,knowledgeMode='retained',convention='trading_workspace_v1'),bookIds=books,portfolioIds=[],
        bumps=[],stress=[],detail='holdings',engineVersion='synthetic_economic_engine_v1',convention='portfolio_instrument_repricing_v1',numericalPolicy='exact_rational_portfolio_repricing_v1',
        pricingPolicy=dict(curveNumericalPolicy='exact_18_fractional_abs_lt_1e24_v1',riskNumericalPolicy='exact_rational_portfolio_repricing_v1',
            futuresConvention='native_margin_remaining_value_v1',swapConvention='independent_side_financial_swap_v2',rateNumericalPolicy='rate_curve_lattice30_to_even_18_v1',
            discountInterpolationPolicy='log_discount_geometric_root_to_even_18_v1',bumpConvention='absolute_quote_v1',bumpBasis='trading_tick',bumpTicks=1,
            nativeFutures=dict(cashDiscounting='captured_currency_default_v1',timingPolicy='linear_utc_day_discount_ratio_v1')),
        economic=dict(projection=dict(implementation='native_portfolio_economic_lifecycle_v1',session='synthetic_lifecycle_risk',sessionVersion=1,
            horizonPolicy='source_known_effective_session_schedule_v1',rateTimingPolicy='linear_utc_day_discount_ratio_v1',eventMarketPolicy='selected_contract_projection_curves_v1',gasOwned=[],
            policy=dict(implementation='economic_lifecycle_projection_v1',pricePath='shock_then_hold_native_quotes_v1',cashDisposition='retain_scheduled_cash_outstanding_v1',
                cashAccumulation='nominal_currency_cash_v1',numericalPolicy='exact_residual_plus_horizon_cash_less_opening_v1',nonPriceInputs='captured_rates_and_volatility_held_v1',inputExpiry='declared_curve_expiry_policy_v1',physicalDelivery='retain_generated_delivery_until_observed_performance_v1')),
            policy=dict(implementation='economic_lifecycle_no_variable_factors_v1',confidence='0.95',quantilePolicy='empirical_inverse_cdf_v1',tailPolicy='signed_upper_loss_fractional_mass_uniform_ties_v1'),
            history=None,simulation=None,factors=[]))
    # The numerical worker replays these graphs before executing any result. This
    # synthetic empty-scope packet qualifies native custody, not product pricing.
    commands=json.loads(query(f"SELECT risk.axis_commands({literal(request)})::text;"))
    assert commands == dict(bumps=[],stress=[],factors=[]), commands
    print('PASS economic deterministic custody selects explicit empty axes without inventing a calibration history', flush=True)
    def scope():
        return f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{seed['at']}','2020-12-18'::date);"
    horizon=json.loads(user(scope()+f"SELECT(security.read_lifecycle_projection_horizon('{catalog}','synthetic_lifecycle_risk',1,'{context}','{seed['at']}','2020-12-18')-'authorityUntil')::text;"))
    frame['economicLifecycle']=dict(horizon=horizon,products=[],optionCalibrations=[],inputFinals=[])
    def submit(payload=None, retained=None, operation=None):
        return json.loads(user(scope()+f"SELECT risk.submit('{catalog}','{operation or uuid4()}','{context}','{manifest['revisionId']}','{seed['at']}',{literal(payload or request)},{literal(retained or frame)})::text;"))
    def reject(action, expected):
        try:
            action()
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError('Accepted operation expected to reject with '+expected)
    operation=str(uuid4())
    accepted=submit(operation=operation)
    assert accepted['kind']=='economic_lifecycle' and accepted['economicMethod']==request['economic']['policy']['implementation'], accepted
    assert submit(operation=operation)['jobId']==accepted['jobId']
    assert query(f"SELECT count(*) FROM risk.job_scenario_axes WHERE job_id='{accepted['jobId']}';")=='0'
    for label,change in [
        ('missing explicit expiry',lambda r: r['economic']['projection']['policy'].pop('inputExpiry')),
        ('missing explicit physical disposition',lambda r: r['economic']['projection']['policy'].pop('physicalDelivery')),
        ('missing explicit gas selection',lambda r: r['economic']['projection'].pop('gasOwned')),
        ('unrequested calibration',lambda r: r['economic'].__setitem__('history',{})),
        ('mixed static method',lambda r: r.__setitem__('historical',{})),
        ('missing explicit factors',lambda r: r['economic'].pop('factors'))]:
        bad=deepcopy(request);change(bad)
        reject(lambda:submit(bad),'risk_economic_input')
    missing_finals=deepcopy(frame);missing_finals['economicLifecycle'].pop('inputFinals')
    reject(lambda:submit(retained=missing_finals),'risk_economic_input')
    bad=deepcopy(frame);bad['factorHistory']={}
    reject(lambda:submit(retained=bad),'risk_economic_input')
    bad=deepcopy(frame);bad['economicLifecycle']['horizon']['openingAt']='2020-12-18T15:00:00+00:00'
    reject(lambda:submit(retained=bad),'risk_economic_horizon')
    bad=deepcopy(frame);bad['run']=dict(id=str(uuid4()),part=1,parts=2)
    reject(lambda:submit(retained=bad),'risk_job_run')
    bad=deepcopy(request);bad.pop('economic')
    reject(lambda:submit(bad),'risk_economic_input')
    def worker(sql):
        return query('SET SESSION AUTHORIZATION synthetic_lifecycle_worker;'+sql)
    claimed=json.loads(worker("SELECT risk.claim(30,'synthetic_economic_engine_v1')::text;"))
    assert claimed['jobId']==accepted['jobId'],claimed
    lease=claimed['leaseToken']
    lines=worker(f"BEGIN;SELECT risk.bind('{accepted['jobId']}','{lease}');"
        "SELECT risk.worker_read();RESET SESSION AUTHORIZATION;SELECT jsonb_build_object('businessDate',q.trading_business_date,'asOf',q.trading_as_of,"
        "'deadlineCaptured',q.expires_at<=j.lease_expires_at)::text FROM security.request_contexts q JOIN risk.worker_attempt_contexts b ON b.context_id=q.id JOIN risk.jobs j ON j.id=b.job_id "
        "WHERE q.runtime_login='synthetic_lifecycle_worker' AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();COMMIT;").splitlines()
    assert lines[-2]=='t', lines
    result=json.loads(lines[-1])
    assert result['businessDate']=='2020-12-18' and result['deadlineCaptured'],result
    print('PASS economic native custody: exact source horizon, immutable retry, explicit choices, exclusive method and owned worker business date',flush=True)

    empty=deepcopy(frame);empty['observationIds']=[];empty['frozenObservationIds']=[]
    no_quotes=submit(retained=empty)
    assert no_quotes['kind']=='economic_lifecycle'
    assert query(f"SELECT count(*) FROM risk.job_observations WHERE job_id='{no_quotes['jobId']}';")=='0'
    static=deepcopy(request);static.pop('economic');static_frame=deepcopy(empty);static_frame.pop('economicLifecycle')
    reject(lambda:submit(static,static_frame),'risk_job_input')
    print('PASS explicit deterministic custody admits zero selected quotes without weakening static input admission',flush=True)

    candidates=json.loads(query(f"SELECT jsonb_agg(to_jsonb(x))::text FROM(SELECT DISTINCT ON(contract_id)curve_code,contract_id FROM market_data.curve_members WHERE catalog_id='{catalog}' AND revision_id='{manifest['revisionId']}' ORDER BY contract_id,curve_code LIMIT 2)x;"))
    assert len(candidates)==2
    factors=[];axes=[]
    for i,candidate in enumerate(candidates):
        curve=dict(revision=dict(catalogId=catalog,revisionId=manifest['revisionId']),code=candidate['curve_code'])
        factor=dict(coordinate='market_quote',target=dict(kind='contract',curve=curve,contract=dict(revision=curve['revision'],id=candidate['contract_id'])))
        factors.append(factor);axes.append(dict(id=str(2-i)*64,marketRevisionId=manifest['revisionId'],factor=factor,points=[dict(curve=curve,pointId=candidate['contract_id'])]))
    calendar_ref=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code='test_business')
    clock_code=query(f"SELECT clock_code FROM ref_data.calendars WHERE(catalog_id,revision_id,code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','test_business');")
    calendar=dict(reference=calendar_ref,from_='2020-12-16',to='2020-12-18',businessDates=['2020-12-16','2020-12-17'])
    calendar['from']=calendar.pop('from_')
    clock=dict(reference=dict(revision=calendar_ref['revision'],code=clock_code),resolution='captured_calendar_session_closes_v1',closeTime='16:00',ambiguousTime='reject',invalidTime='reject',
        resolvedCloses=[dict(date=day,instant=day+'T16:00:00Z',offsetSeconds=0)for day in calendar['businessDates']])
    capture=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT risk.capture_history_clock('{catalog}','{context}',{literal(calendar)},{literal(clock)});")
    forecast=json.loads(query(f"SELECT jsonb_build_object('catalogId',t.catalog_id,'contextId',t.id,'valuationAt','{seed['at']}','marketCutoff','{seed['at']}','knownAt',t.known_at,'marketKnownThrough',m.known_through)::text FROM market_data.temporal_contexts t JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=('{catalog}','{context}');"))
    history=dict(scope=dict(context=request['context'],bookIds=books,portfolioIds=[]),pricingPolicy=request['pricingPolicy'],calendar=calendar_ref,
        from_='2020-12-16',to='2020-12-18',closeTime='16:00',expectedManifestId='c'*64,policy=dict(alignment='exact_session_close_v1'))
    history['from']=history.pop('from_')
    sampled=deepcopy(request);sampled['engineVersion']='synthetic_economic_sampled_v1';sampled['economic'].update(history=history,factors=factors)
    sampled['economic']['policy']['implementation']='economic_lifecycle_historical_full_revaluation_v1'
    sample_frame=deepcopy(frame);sample_frame['scenarioAxes']=list(reversed(axes))
    sample_frame['factorHistory']=dict(clockCaptureId=capture,expectedManifestId=history['expectedManifestId'],forecast=forecast,policy=history['policy'],calendar=calendar,clock=clock,observations=frame['observationIds'])
    retained=submit(sampled,sample_frame)
    assert retained['kind']=='economic_lifecycle'
    assert query(f"SELECT count(*) FROM risk.job_scenario_axes WHERE job_id='{retained['jobId']}';")=='2'
    assert query(f"SELECT request#>'{{economic,factors}}'=({literal(factors)}) FROM risk.jobs WHERE id='{retained['jobId']}';")=='t'
    dropped=deepcopy(sample_frame);dropped['scenarioAxes'].pop()
    reject(lambda:submit(sampled,dropped),'risk_economic_axes')
    duplicated=deepcopy(sampled);duplicated['economic']['factors'].append(factors[0])
    reject(lambda:submit(duplicated,sample_frame),'risk_economic_input')
    print('PASS economic sampled custody keeps calibrated factor order distinct from canonical retained-axis order, with complete closure',flush=True)
    simulated=deepcopy(sampled);simulated['economic']['policy']['implementation']='economic_lifecycle_monte_carlo_full_revaluation_v1'
    simulated['economic']['simulation']=dict(convention='economic_lifecycle_monte_carlo_v1',numericalPolicy='exact_ldl_binary64_polar_v1',
        estimator='equal_weight_sample_n_minus_one_v1',centering='sample_mean_centered_v1',drift='zero_drift_v1',
        horizon='frozen_portfolio_one_adjacent_session_v1',quantilePolicy='empirical_inverse_cdf_v1',tailPolicy='signed_upper_loss_fractional_mass_uniform_ties_v1',
        valuationPolicy='economic_lifecycle_residual_and_horizon_cash_v1',confidence='0.95',distribution='joint_additive_gaussian_v1',
        randomGenerator='sha256_counter_polar_log_series_v1',shockPolicy='binary64_exact_to_native_18_places_to_even_v1',seed='17',paths=16,
        samplingUncertainty='binomial_order_statistic_binary64_95_v1')
    assert submit(simulated,sample_frame)['kind']=='economic_lifecycle'
    invalid_simulation=deepcopy(simulated);invalid_simulation['economic']['simulation']['randomGenerator']='unqualified'
    reject(lambda:submit(invalid_simulation,sample_frame),'risk_economic_input')
    changed_scope=deepcopy(sampled);changed_scope['economic']['history']['scope']['bookIds']=[]
    reject(lambda:submit(changed_scope,sample_frame),'risk_economic_scope')
    print('PASS economic Monte Carlo custody retains its explicit simulation policy and refuses altered calibration scope',flush=True)


    assert query("SELECT has_function_privilege('synthetic_lifecycle_worker','security.read_gas_owned_projection_choices(uuid,uuid,timestamptz,uuid[])','EXECUTE');")=='f'
    # New native source readers are read-only and cannot bypass an owned lease.
    for signature in READERS:
        assert query(f"SELECT has_function_privilege('synthetic_lifecycle_worker','{signature}','EXECUTE');")=='t',signature
    for signature in ('settlement_data.save_futures_margin_policy(uuid,jsonb)','settlement_data.adopt_futures_margin(uuid,jsonb)',
        'lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)','security.record_gas_owned(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'):
        assert query(f"SELECT has_function_privilege('synthetic_lifecycle_worker','{signature}','EXECUTE');")=='f',signature
    reject(lambda:worker(f"SELECT settlement_data.futures_margin_preview_authority('{catalog}');"),'risk_job_denied')
    guarded=worker(f"BEGIN;SELECT risk.bind('{accepted['jobId']}','{lease}');SELECT settlement_data.futures_margin_preview_authority('{catalog}');"
        "SELECT count(*) FROM lifecycle.option_profiles;SELECT count(*) FROM lifecycle.swap_profiles;SELECT count(*) FROM settlement_data.futures_margin_policies;COMMIT;")
    assert guarded.splitlines()[-3:]==['0','0','0'],guarded
    cancelled=f"BEGIN;SELECT risk.bind('{accepted['jobId']}','{lease}');RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET state='cancellation_requested' WHERE id='{accepted['jobId']}';SET SESSION AUTHORIZATION synthetic_lifecycle_worker;"
    assert worker(cancelled+"SELECT risk.worker_read();ROLLBACK;").splitlines()[-1]=='f', 'Cancellation must still deny direct worker RLS reads'
    reject(lambda:worker(cancelled+f"SELECT settlement_data.futures_margin_preview_authority('{catalog}');ROLLBACK;"),'risk_job_cancelled')
    print('PASS protected worker readers preserve the owned cancellation control signal while direct RLS reads remain denied',flush=True)
    reject(lambda:worker(f"BEGIN;SELECT risk.bind('{accepted['jobId']}','{lease}');SELECT security.read_gas_owned_holdings('{catalog}','{context}','{seed['at']}',ARRAY[{','.join(repr(b)+'::uuid'for b in books)}]);COMMIT;"),'gas_owned_denied')
    print('PASS owned read-only source grants refuse unbound reads, unselected gas authority and native writers',flush=True)
    bound=lambda sql:f"BEGIN;SELECT risk.bind('{accepted['jobId']}','{lease}');"+sql+'COMMIT;'
    reject(lambda:worker(bound(f"SELECT settlement_data.read_futures_margin_state('{catalog}','{uuid4()}','{uuid4()}','{context}','{seed['at']}');")),'risk_job_scope')
    reject(lambda:worker(bound(f"SELECT security.require_cash_payment_read('{catalog}','{uuid4()}','{seed['at']}');")),'risk_job_scope')
    reject(lambda:worker(bound(f"SELECT security.require_cash_payment_read('{catalog}','{context}','2020-12-18T16:01:00Z');")),'risk_job_scope')
    reject(lambda:worker(bound(f"SELECT security.require_gas_owned_projection_scope('{catalog}','{context}','{seed['at']}',ARRAY['{books[0]}']::uuid[],false,settlement_data.futures_margin_preview_authority('{catalog}'));")),'risk_job_scope')
    reject(lambda:worker(bound(f"SELECT settlement_data.read_futures_delivery_profile('{catalog}','{uuid4()}',1,'{context}','{seed['at']}');")),'risk_job_scope')
    print('PASS worker source scope refuses foreign owner/profile, altered context/clock and partial book reads before native source lookup',flush=True)
    selected_horizon=json.loads(worker(bound(f"SELECT(security.read_lifecycle_projection_horizon('{catalog}','synthetic_lifecycle_risk',1,'{context}','{seed['at']}','2020-12-18')-'authorityUntil')::text;")).splitlines()[-1])
    assert selected_horizon==horizon
    reject(lambda:worker(bound(f"SELECT security.read_lifecycle_projection_horizon('{catalog}','synthetic_alternative_risk',1,'{context}','{seed['at']}','2020-12-18');")),'risk_job_scope')
    assert query("SELECT has_function_privilege('synthetic_lifecycle_worker','security.lifecycle_projection_sessions(uuid,uuid,timestamptz,date)','EXECUTE');")=='f'
    print('PASS worker horizon reopens exactly the retained selected session; interactive session discovery remains ungranted',flush=True)



    # A conditional gas job stays queued for a service that has no current gas
    # read grant. The source-known fixture is intentionally empty, not a fake PV.
    gas_request=deepcopy(request);gas_request['engineVersion']='synthetic_economic_gas_v1';gas_request['pricingPolicy']['gasOwned']={}
    reject(lambda:submit(gas_request),'risk_job_denied')
    gas_role=str(uuid4());actor=query(f"SELECT actor_id FROM risk.jobs WHERE id='{accepted['jobId']}';")
    query(f"INSERT INTO security.roles(id,code,name) VALUES('{gas_role}','synthetic_gas_risk_reader','Synthetic gas risk reader');INSERT INTO security.role_permissions VALUES('{gas_role}','ReadGasOwnedHoldings');"
        f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{gas_role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{gas_role}','{tenant}','{workspace}',clock_timestamp()+interval '1 second','9999-12-31',false FROM risk.workers WHERE login='synthetic_lifecycle_worker';")
    gas_job=submit(gas_request)
    assert worker("SELECT risk.claim(30,'synthetic_economic_gas_v1')::text;")==''
    assert query(f"SELECT state FROM risk.jobs WHERE id='{gas_job['jobId']}';")=='queued'
    gas_claim=json.loads(worker("SELECT pg_sleep(1.1);SELECT risk.claim(30,'synthetic_economic_gas_v1')::text;"))
    assert gas_claim['jobId']==gas_job['jobId'],gas_claim
    gas_read=json.loads(worker(f"BEGIN;SELECT risk.bind('{gas_job['jobId']}','{gas_claim['leaseToken']}');"
        f"SELECT security.read_gas_owned_known_history('{catalog}','{context}','{seed['at']}',ARRAY[{','.join(repr(b)+'::uuid'for b in books)}])::text;COMMIT;").splitlines()[-1])
    assert gas_read['positions']==[] and gas_read['accounts']==[],gas_read
    print('PASS conditional gas worker admission waits for explicit actor and service gas authority and performs an owned known-history read',flush=True)

    # Both grants exist before the job. Their gap is temporal and publishes no
    # authority change while this read runs; a successor grant must not extend it.
    provision_worker(query,manifest,'synthetic_deadline_worker')
    query("UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='synthetic_deadline_worker');"
        "INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,granted_to+interval '0.2 seconds','9999-12-31',false FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='synthetic_deadline_worker');")
    deadline_request=deepcopy(request);deadline_request['engineVersion']='synthetic_economic_deadline_v1'
    deadline_job=submit(deadline_request)
    deadline_claim=json.loads(query("SET SESSION AUTHORIZATION synthetic_deadline_worker;SELECT risk.claim(30,'synthetic_economic_deadline_v1')::text;"))
    deadline_read=query(f"BEGIN;SET SESSION AUTHORIZATION synthetic_deadline_worker;SELECT risk.bind('{deadline_job['jobId']}','{deadline_claim['leaseToken']}');RESET SESSION AUTHORIZATION;"
        "DO $$BEGIN IF EXISTS(SELECT 1 FROM security.request_contexts q JOIN risk.worker_attempt_contexts b ON b.context_id=q.id "
        "JOIN risk.workers w ON w.login=q.runtime_login JOIN security.role_grants g ON g.principal_id=w.principal_id "
        "WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login='synthetic_deadline_worker' "
        "AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to AND q.expires_at>g.granted_to) "
        "THEN RAISE EXCEPTION 'synthetic_original_service_deadline_not_retained';END IF;END$$;SELECT pg_sleep(2.4);"
        f"SELECT security.principal_has_permission(w.principal_id,'{tenant}','{workspace}','RunScenario',clock_timestamp()) FROM risk.workers w WHERE w.login='synthetic_deadline_worker';"
        "SET SESSION AUTHORIZATION synthetic_deadline_worker;DO $$BEGIN IF risk.worker_read() THEN RAISE EXCEPTION 'synthetic_expired_read_accepted';END IF;"
        f"BEGIN PERFORM settlement_data.futures_margin_preview_authority('{catalog}');EXCEPTION WHEN insufficient_privilege THEN RETURN;END;"
        "RAISE EXCEPTION 'synthetic_expired_source_read_accepted';END$$;COMMIT;")
    assert deadline_read.splitlines()[-1]=='t',deadline_read
    print('PASS original service read deadline survives a permission gap and successor grant; no newly valid grant revives the bound read',flush=True)
    for conditional_gas in (False,True):
        login='synthetic_bind_gas_wait'if conditional_gas else'synthetic_bind_base_wait'
        provision_worker(query,manifest,login)
        if conditional_gas:
            query(f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{gas_role}','{tenant}','{workspace}',clock_timestamp(),clock_timestamp()+interval '3 seconds',false FROM risk.workers WHERE login='{login}';"
                f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{gas_role}','{tenant}','{workspace}',clock_timestamp()+interval '3.2 seconds','9999-12-31',false FROM risk.workers WHERE login='{login}';")
        else:
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '3 seconds' WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='{login}');"
                f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,granted_to+interval '0.2 seconds','9999-12-31',false FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='{login}');")
        first_request=deepcopy(gas_request if conditional_gas else request);first_request['engineVersion']=login+'_v1'
        first=submit(first_request)
        first_claim=json.loads(query(f"SET SESSION AUTHORIZATION {login};SELECT risk.claim(30,'{first_request['engineVersion']}')::text;"))
        holder_name='synthetic_bind_holder_'+uuid4().hex
        with ThreadPoolExecutor(max_workers=2)as pool:
            holder=pool.submit(query,f"SET application_name='{holder_name}';BEGIN;SELECT generation FROM security.epoch FOR UPDATE;SELECT pg_sleep(4.4);ROLLBACK;")
            deadline=time.monotonic()+3
            while query(f"SELECT count(*)FROM pg_stat_activity WHERE application_name='{holder_name}' AND wait_event='PgSleep';")!='1':
                assert time.monotonic()<deadline,'Epoch holder never acquired its row lock'
                time.sleep(.01)
            work=pool.submit(query,f"SET application_name='{login}_call';SET SESSION AUTHORIZATION {login};BEGIN;SELECT risk.bind('{first['jobId']}','{first_claim['leaseToken']}');COMMIT;")
            deadline=time.monotonic()+3
            while query(f"SELECT count(*)FROM pg_stat_activity WHERE application_name='{login}_call' AND wait_event_type='Lock' AND query LIKE '%risk.bind%';")!='1':
                assert time.monotonic()<deadline,'Bind did not enter the intended first epoch wait'
                time.sleep(.01)
            try:
                accepted_after_gap=work.result(timeout=8)
            except RuntimeError as error:
                assert 'risk_job_denied'in str(error),str(error)
            else:
                raise AssertionError('First bind admission was revived by a preexisting successor grant: '+accepted_after_gap)
            holder.result(timeout=3)
        assert query(f"SELECT security.principal_has_permission(w.principal_id,'{tenant}','{workspace}','{'ReadGasOwnedHoldings'if conditional_gas else'RunScenario'}',clock_timestamp())FROM risk.workers w WHERE w.login='{login}';")=='t'
    print('PASS bind first-wait admission preserves original base and conditional gas deadlines across epoch waits and preexisting successor grants',flush=True)

    if read_policy_checks is not None:
        read_policy_checks(query,manifest,request,frame,submit)
    print('QUALIFIED authority_until source hash '+query("SELECT md5(prosrc) FROM pg_proc WHERE oid='security.authority_until(text)'::regprocedure;"),flush=True)





if __name__ == '__main__':
    main(checks=checks,before_migration=before_migration)
