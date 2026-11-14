-- Current consumer: complete event-aware economic portfolio risk, including
-- retained worker replay under the original source business date and authority.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.authorized(risk.jobs)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.authorized(risk.jobs)';END IF;
 IF md5(p.prosrc)<>'f72b1b9f3d6e5503a20ac76dc0fbf72d' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.authorized(risk.jobs)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.authorized(','FUNCTION risk.authorized_pre_economic_lifecycle('),'authorized.','authorized_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.authorized_pre_economic_lifecycle(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.authorized_pre_economic_lifecycle(risk.jobs) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.worker_read()');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.worker_read()';END IF;
 IF md5(p.prosrc)<>'543556f0dc1240b1c4b2f1be4c9773b4' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.worker_read()';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.worker_read(','FUNCTION risk.worker_read_pre_economic_lifecycle('),'worker_read.','worker_read_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.worker_read_pre_economic_lifecycle() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.worker_read_pre_economic_lifecycle() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.bind(uuid,uuid)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.bind(uuid,uuid)';END IF;
 IF md5(p.prosrc)<>'1958be9f29cd13b2a4b7e3c9305e28c8' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'uuid'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.bind(uuid,uuid)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.bind(','FUNCTION risk.bind_pre_economic_lifecycle('),'bind.','bind_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.bind_pre_economic_lifecycle(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.bind_pre_economic_lifecycle(uuid,uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.axis_commands(jsonb)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.axis_commands(jsonb)';END IF;
 IF md5(p.prosrc)<>'b7ea9741b4c9b40d86a143c2a861636f' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM false
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.axis_commands(jsonb)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.axis_commands(','FUNCTION risk.axis_commands_pre_economic_lifecycle('),'axis_commands.','axis_commands_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.axis_commands_pre_economic_lifecycle(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.axis_commands_pre_economic_lifecycle(jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.retain_axes(risk.jobs,jsonb)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.retain_axes(risk.jobs,jsonb)';END IF;
 IF md5(p.prosrc)<>'defad04bfe27e9492e5527d81e461c28' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'void'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.retain_axes(risk.jobs,jsonb)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.retain_axes(','FUNCTION risk.retain_axes_pre_economic_lifecycle('),'retain_axes.','retain_axes_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.retain_axes_pre_economic_lifecycle(risk.jobs,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_axes_pre_economic_lifecycle(risk.jobs,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.retain_parametric(risk.jobs)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.retain_parametric(risk.jobs)';END IF;
 IF md5(p.prosrc)<>'3ade739d848f9ab4261cb1cbe0bd1b36' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'void'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.retain_parametric(risk.jobs)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.retain_parametric(','FUNCTION risk.retain_parametric_pre_economic_lifecycle('),'retain_parametric.','retain_parametric_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.retain_parametric_pre_economic_lifecycle(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_parametric_pre_economic_lifecycle(risk.jobs) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.guard_run()');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.guard_run()';END IF;
 IF md5(p.prosrc)<>'4fcee24df1d1a06e0396ea21894a2a73' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'trigger'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.guard_run()';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.guard_run(','FUNCTION risk.guard_run_pre_economic_lifecycle('),'guard_run.','guard_run_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.guard_run_pre_economic_lifecycle() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.guard_run_pre_economic_lifecycle() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.summary(risk.jobs)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.summary(risk.jobs)';END IF;
 IF md5(p.prosrc)<>'f9802b6de477de14758ec5da34703b2c' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM false
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.summary(risk.jobs)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.summary(','FUNCTION risk.summary_pre_economic_lifecycle('),'summary.','summary_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION risk.summary_pre_economic_lifecycle(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.summary_pre_economic_lifecycle(risk.jobs) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.authority_until(text)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.authority_until(text)';END IF;
 IF md5(p.prosrc)<>'4941838f43c6b679461b8f76ef698eaa' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'timestamp with time zone'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.authority_until(text)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.authority_until(','FUNCTION security.authority_until_pre_economic_lifecycle('),'authority_until.','authority_until_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION security.authority_until_pre_economic_lifecycle(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.authority_until_pre_economic_lifecycle(text) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_gas_owned(uuid,boolean,boolean)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.require_gas_owned(uuid,boolean,boolean)';END IF;
 IF md5(p.prosrc)<>'909c20ab92e31ebb80741938c8b90e5b' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'void'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v'
  OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.require_gas_owned(uuid,boolean,boolean)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.require_gas_owned(','FUNCTION security.require_gas_owned_pre_economic_lifecycle('),'require_gas_owned.','require_gas_owned_pre_economic_lifecycle.');
END$guard$;
ALTER FUNCTION security.require_gas_owned_pre_economic_lifecycle(uuid,boolean,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_owned_pre_economic_lifecycle(uuid,boolean,boolean) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
-- This is an explicit request branch, never a financial historical/MC alias.
CREATE FUNCTION risk.economic_commands(request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SET search_path=pg_catalog AS $function$
DECLARE e jsonb=request->'economic';p jsonb=e->'projection';r jsonb=p->'policy';method text=e#>>'{policy,implementation}';factors jsonb;confidence numeric;
BEGIN
 IF jsonb_typeof(e) IS DISTINCT FROM 'object' OR e-ARRAY['projection','policy','history','simulation','factors']<>'{}'::jsonb
  OR NOT(e ?& ARRAY['projection','policy','history','simulation','factors'])
  OR jsonb_typeof(p) IS DISTINCT FROM 'object' OR p-ARRAY['implementation','session','sessionVersion','horizonPolicy','rateTimingPolicy','eventMarketPolicy','policy','gasOwned']<>'{}'::jsonb
  OR p->>'implementation' IS DISTINCT FROM 'native_portfolio_economic_lifecycle_v1' OR coalesce(length(btrim(p->>'session')),0) NOT BETWEEN 1 AND 128
  OR jsonb_typeof(p->'sessionVersion') IS DISTINCT FROM 'number' OR(p->>'sessionVersion') !~ '^[1-9][0-9]{0,8}$'
  OR p->>'horizonPolicy' IS DISTINCT FROM 'source_known_effective_session_schedule_v1'
  OR p->>'rateTimingPolicy' IS DISTINCT FROM 'linear_utc_day_discount_ratio_v1'
  OR p->>'eventMarketPolicy' IS DISTINCT FROM 'selected_contract_projection_curves_v1'
  OR jsonb_typeof(p->'gasOwned') IS DISTINCT FROM 'array'
  OR jsonb_typeof(r) IS DISTINCT FROM 'object' OR r-ARRAY['implementation','pricePath','cashDisposition','cashAccumulation','numericalPolicy','nonPriceInputs','inputExpiry','physicalDelivery']<>'{}'::jsonb
  OR r->>'implementation' IS DISTINCT FROM 'economic_lifecycle_projection_v1'
  OR coalesce(r->>'pricePath','') NOT IN('shock_then_hold_native_quotes_v1','elapsed_linear_native_quotes_18_to_even_v1')
  OR coalesce(r->>'cashDisposition','') NOT IN('project_scheduled_cash_paid_at_due_v1','retain_scheduled_cash_outstanding_v1')
  OR coalesce(r->>'cashAccumulation','') NOT IN('nominal_currency_cash_v1','captured_currency_curve_exact_cash_accumulation_v1')
  OR r->>'numericalPolicy' IS DISTINCT FROM 'exact_residual_plus_horizon_cash_less_opening_v1'
  OR r->>'nonPriceInputs' IS DISTINCT FROM 'captured_rates_and_volatility_held_v1'
  OR coalesce(r->>'inputExpiry','') NOT IN('declared_curve_expiry_policy_v1','freeze_selected_native_mark_at_expiry_v1')
  OR r->>'physicalDelivery' IS DISTINCT FROM 'retain_generated_delivery_until_observed_performance_v1'
  OR jsonb_typeof(e->'policy') IS DISTINCT FROM 'object' OR(e->'policy')-ARRAY['implementation','confidence','quantilePolicy','tailPolicy']<>'{}'::jsonb
  OR coalesce(method,'') NOT IN('economic_lifecycle_no_variable_factors_v1','economic_lifecycle_historical_full_revaluation_v1','economic_lifecycle_monte_carlo_full_revaluation_v1')
  OR e#>>'{policy,quantilePolicy}' IS DISTINCT FROM 'empirical_inverse_cdf_v1'
  OR e#>>'{policy,tailPolicy}' IS DISTINCT FROM 'signed_upper_loss_fractional_mass_uniform_ties_v1'
  OR jsonb_typeof(e#>'{policy,confidence}') IS DISTINCT FROM 'string'
  OR jsonb_typeof(e->'factors') IS DISTINCT FROM 'array' OR request->'bumps' IS DISTINCT FROM '[]'::jsonb OR request->'stress' IS DISTINCT FROM '[]'::jsonb
  OR request->>'detail' IS DISTINCT FROM 'holdings' OR EXISTS(SELECT 1 FROM unnest(ARRAY['joint','parametric','historical','monteCarlo'])key WHERE NULLIF(request->key,'null'::jsonb) IS NOT NULL)
 THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 confidence=(e#>>'{policy,confidence}')::numeric;
 IF confidence IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) OR confidence<=0.5 OR confidence>0.9999 OR(e#>>'{policy,confidence}') !~ '^0\.[0-9]{1,18}$' THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(p->'gasOwned')s WHERE jsonb_typeof(s) IS DISTINCT FROM 'object'
  OR s-ARRAY['accountId','profileId','revision']<>'{}'::jsonb OR(s->>'accountId')::uuid IS NULL OR(s->>'profileId')::uuid IS NULL
  OR(s->>'accountId')::uuid='00000000-0000-0000-0000-000000000000'::uuid OR(s->>'profileId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(s->'revision') IS DISTINCT FROM 'number' OR(s->>'revision') !~ '^[1-9][0-9]{0,8}$')
  OR(SELECT count(*)<>count(DISTINCT s->>'accountId') FROM jsonb_array_elements(p->'gasOwned')s)
 THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 IF method='economic_lifecycle_no_variable_factors_v1' THEN
  IF e->'history' IS DISTINCT FROM 'null'::jsonb OR e->'simulation' IS DISTINCT FROM 'null'::jsonb OR e->'factors' IS DISTINCT FROM '[]'::jsonb
  THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 ELSE
  IF jsonb_typeof(e->'history') IS DISTINCT FROM 'object' OR jsonb_array_length(e->'factors') NOT BETWEEN 1 AND 128
   OR method='economic_lifecycle_historical_full_revaluation_v1' AND e->'simulation' IS DISTINCT FROM 'null'::jsonb
   OR method='economic_lifecycle_monte_carlo_full_revaluation_v1' AND(jsonb_typeof(e->'simulation') IS DISTINCT FROM 'object'
    OR e#>>'{simulation,convention}' IS DISTINCT FROM 'economic_lifecycle_monte_carlo_v1'
    OR e#>>'{simulation,valuationPolicy}' IS DISTINCT FROM 'economic_lifecycle_residual_and_horizon_cash_v1'
    OR e#>'{simulation,confidence}' IS DISTINCT FROM e#>'{policy,confidence}')
  THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 END IF;
 IF method='economic_lifecycle_monte_carlo_full_revaluation_v1' AND(
  (e->'simulation')-ARRAY['convention','numericalPolicy','estimator','centering','drift','horizon','quantilePolicy','tailPolicy','valuationPolicy','confidence','distribution','randomGenerator','shockPolicy','seed','paths','samplingUncertainty']<>'{}'::jsonb
  OR e#>>'{simulation,numericalPolicy}' IS DISTINCT FROM 'exact_ldl_binary64_polar_v1'
  OR e#>>'{simulation,estimator}' IS DISTINCT FROM 'equal_weight_sample_n_minus_one_v1'
  OR e#>>'{simulation,centering}' IS DISTINCT FROM 'sample_mean_centered_v1'
  OR coalesce(e#>>'{simulation,drift}','') NOT IN('zero_drift_v1','sample_mean_drift_v1')
  OR e#>>'{simulation,horizon}' IS DISTINCT FROM 'frozen_portfolio_one_adjacent_session_v1'
  OR e#>>'{simulation,quantilePolicy}' IS DISTINCT FROM 'empirical_inverse_cdf_v1'
  OR e#>>'{simulation,tailPolicy}' IS DISTINCT FROM 'signed_upper_loss_fractional_mass_uniform_ties_v1'
  OR e#>>'{simulation,distribution}' IS DISTINCT FROM 'joint_additive_gaussian_v1'
  OR e#>>'{simulation,randomGenerator}' IS DISTINCT FROM 'sha256_counter_polar_log_series_v1'
  OR e#>>'{simulation,shockPolicy}' IS DISTINCT FROM 'binary64_exact_to_native_18_places_to_even_v1'
  OR e#>>'{simulation,samplingUncertainty}' IS DISTINCT FROM 'binomial_order_statistic_binary64_95_v1'
  OR jsonb_typeof(e#>'{simulation,seed}') IS DISTINCT FROM 'string' OR coalesce(e#>>'{simulation,seed}','') !~ '^(0|[1-9][0-9]{0,19})$'
  OR(e#>>'{simulation,seed}')::numeric>18446744073709551615
  OR jsonb_typeof(e#>'{simulation,paths}') IS DISTINCT FROM 'number' OR coalesce(e#>>'{simulation,paths}','') !~ '^[1-9][0-9]{0,4}$'
  OR(e#>>'{simulation,paths}')::integer NOT BETWEEN 2 AND 16384 OR confidence::double precision<=0.5)
 THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(e->'factors')f WHERE jsonb_typeof(f) IS DISTINCT FROM 'object'
  OR f->>'coordinate' IS DISTINCT FROM 'market_quote' OR coalesce(f#>>'{target,kind}','') NOT IN('contract','points'))
  OR(SELECT count(*)<>count(DISTINCT jsonb_strip_nulls(f)) FROM jsonb_array_elements(e->'factors')f)
 THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('factor',f) ORDER BY ordinal),'[]'::jsonb) INTO factors FROM jsonb_array_elements(e->'factors')WITH ORDINALITY a(f,ordinal);
 RETURN jsonb_build_object('bumps','[]'::jsonb,'stress','[]'::jsonb,'factors',factors);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_parameter_value THEN RAISE EXCEPTION 'risk_economic_input';
END$function$;
ALTER FUNCTION risk.economic_commands(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.economic_commands(jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION risk.axis_commands(request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SET search_path=pg_catalog AS $function$
BEGIN
 IF NULLIF(request->'economic','null'::jsonb) IS NOT NULL THEN RETURN risk.economic_commands(request);END IF;
 RETURN risk.axis_commands_pre_economic_lifecycle(request);
END$function$;

CREATE FUNCTION risk.retain_economic(job risk.jobs) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE e jsonb=job.request->'economic';proof jsonb=job.frame->'economicLifecycle';native jsonb;axes jsonb;
BEGIN
 PERFORM risk.economic_commands(job.request);
 IF job.frame ? 'run' OR jsonb_typeof(proof) IS DISTINCT FROM 'object' OR proof-ARRAY['horizon','products','optionCalibrations','inputFinals']<>'{}'::jsonb
  OR jsonb_typeof(proof->'horizon') IS DISTINCT FROM 'object' OR jsonb_typeof(proof->'products') IS DISTINCT FROM 'array'
  OR jsonb_typeof(proof->'optionCalibrations') IS DISTINCT FROM 'array' OR jsonb_typeof(proof->'inputFinals') IS DISTINCT FROM 'array' OR jsonb_path_exists(proof,'$.**.authorityUntil')
  OR NULLIF(job.frame->'excludedPositions','null'::jsonb) IS DISTINCT FROM '[]'::jsonb
 THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 native=security.read_lifecycle_projection_horizon(job.catalog_id,e#>>'{projection,session}',(e#>>'{projection,sessionVersion}')::integer,
  job.context_id,job.valuation_at,(job.frame->>'businessDate')::date)-'authorityUntil';
 IF proof->'horizon' IS DISTINCT FROM native THEN RAISE EXCEPTION 'risk_economic_horizon';END IF;
 SELECT coalesce(jsonb_agg(jsonb_strip_nulls(a->'factor')ORDER BY ordinal),'[]'::jsonb) INTO axes
  FROM jsonb_array_elements(job.frame->'scenarioAxes')WITH ORDINALITY f(a,ordinal);
 -- Retained axes have their own canonical ID order. Economic factors retain
 -- calibrated history-column order; managed replay checks that order exactly.
 -- The native boundary proves the two lists cover the identical complete set.
 IF jsonb_array_length(axes)<>jsonb_array_length(e->'factors') OR EXISTS(
  SELECT 1 FROM jsonb_array_elements(e->'factors')f WHERE NOT axes @> jsonb_build_array(jsonb_strip_nulls(f)))
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements(axes)) THEN RAISE EXCEPTION 'risk_economic_axes';END IF;
 IF e#>>'{policy,implementation}'='economic_lifecycle_no_variable_factors_v1' THEN
  IF job.frame ? 'factorHistory' THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 ELSE
  IF jsonb_typeof(job.frame->'factorHistory') IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'risk_economic_input';END IF;
  IF e#>'{history,pricingPolicy}' IS DISTINCT FROM job.request->'pricingPolicy'
   OR e#>'{history,scope,context}' IS DISTINCT FROM job.request->'context'
   OR e#>'{history,scope,bookIds}' IS DISTINCT FROM job.request->'bookIds'
   OR e#>'{history,scope,portfolioIds}' IS DISTINCT FROM '[]'::jsonb
   OR NULLIF(e#>'{history,scope,tradeIds}','null'::jsonb) IS DISTINCT FROM NULLIF(job.request->'tradeIds','null'::jsonb)
  THEN RAISE EXCEPTION 'risk_economic_scope';END IF;
  PERFORM risk.retain_statistical_history(job,jsonb_build_object('history',e->'history'));
 END IF;
END$function$;
ALTER FUNCTION risk.retain_economic(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_economic(risk.jobs) FROM PUBLIC;

CREATE OR REPLACE FUNCTION risk.retain_parametric(job risk.jobs) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF NULLIF(job.request->'economic','null'::jsonb) IS NOT NULL THEN PERFORM risk.retain_economic(job);RETURN;END IF;
 IF job.frame ? 'economicLifecycle' THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 PERFORM risk.retain_parametric_pre_economic_lifecycle(job);
END$function$;

-- Retain the proven point/curve guards. Only the explicit deterministic branch
-- admits empty axes; every sampled request retains all factors in column order.
DO $patch$DECLARE definition text;needle text;replacement text;BEGIN
 definition=pg_get_functiondef('risk.retain_axes_pre_economic_lifecycle(risk.jobs,jsonb)'::regprocedure);
 definition=replace(definition,'FUNCTION risk.retain_axes_pre_economic_lifecycle(','FUNCTION risk.retain_axes(');
 needle='jsonb_array_length(axes) NOT BETWEEN 1 AND 4112';
 replacement='jsonb_array_length(axes) NOT BETWEEN (CASE WHEN job.request#>>''{economic,policy,implementation}''=''economic_lifecycle_no_variable_factors_v1'' THEN 0 ELSE 1 END) AND 4112';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'risk_economic_upgrade: axes count';END IF;
 definition=replace(definition,needle,replacement);
 needle='CASE WHEN jsonb_typeof(job.request->''joint'')=''object'' OR';
 replacement='CASE WHEN jsonb_typeof(job.request->''economic'')=''object'' OR jsonb_typeof(job.request->''joint'')=''object'' OR';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'risk_economic_upgrade: bump count';END IF;
 definition=replace(definition,needle,replacement);EXECUTE definition;
END$patch$;

DO $patch$DECLARE definition text;needle text;BEGIN
 definition=pg_get_functiondef('risk.guard_run_pre_economic_lifecycle()'::regprocedure);
 definition=replace(definition,'FUNCTION risk.guard_run_pre_economic_lifecycle(','FUNCTION risk.guard_run(');
 needle='BEGIN';
 definition=replace(definition,needle,E'BEGIN\n IF NULLIF(NEW.request->''economic'',''null''::jsonb) IS NOT NULL THEN\n  PERFORM risk.economic_commands(NEW.request);\n  IF NEW.frame ? ''run'' THEN RAISE EXCEPTION ''risk_job_run'';END IF;\n ELSIF NEW.frame ? ''economicLifecycle'' THEN RAISE EXCEPTION ''risk_economic_input'';END IF;');
 EXECUTE definition;
END$patch$;

CREATE OR REPLACE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb
 LANGUAGE sql SET search_path=pg_catalog AS $function$
 SELECT risk.summary_pre_economic_lifecycle(job)||CASE WHEN jsonb_typeof(job.request->'economic')='object'
 THEN jsonb_build_object('kind','economic_lifecycle','historyId',job.frame#>'{factorHistory,expectedManifestId}',
  'confidence',job.request#>'{economic,policy,confidence}','economicMethod',job.request#>'{economic,policy,implementation}',
  'scenarioCount',job.request#>'{economic,simulation,paths}') ELSE '{}'::jsonb END;
$function$;
-- Source authorization follows native holdings and accepted causal children.
-- Heads are resolved at the retained business/knowledge coordinates before
-- filtering books; a later unreadable or moved head never revives an old one.
CREATE FUNCTION risk.lifecycle_scope_trades(job risk.jobs)
 RETURNS TABLE(trade_id uuid,binding_id uuid,book_id uuid,revision integer,kind text)
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 WITH cut AS(SELECT x.known_through,t.known_at FROM market_data.temporal_contexts t JOIN trading.contexts x
  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id)),
 future_heads AS(SELECT DISTINCT ON(r.trade_id)r.* FROM trading.trade_revisions r JOIN trading.knowledge_receipts k
  ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order) CROSS JOIN cut
  WHERE r.catalog_id=job.catalog_id AND r.publication_order<=cut.known_through AND k.known_from<=cut.known_at AND r.effective_at<=job.valuation_at ORDER BY r.trade_id,r.revision DESC),
 option_heads AS(SELECT DISTINCT ON(r.trade_id)r.* FROM trading.futures_option_trade_revisions r JOIN trading.knowledge_receipts k
  ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order) CROSS JOIN cut
  WHERE r.catalog_id=job.catalog_id AND r.publication_order<=cut.known_through AND k.known_from<=cut.known_at AND r.effective_at<=job.valuation_at ORDER BY r.trade_id,r.revision DESC),
 swap_heads AS(SELECT DISTINCT ON(r.trade_id)r.* FROM trading.swap_trade_revisions r JOIN trading.knowledge_receipts k
  ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order) CROSS JOIN cut
  WHERE r.catalog_id=job.catalog_id AND r.publication_order<=cut.known_through AND k.known_from<=cut.known_at AND r.effective_at<=job.valuation_at ORDER BY r.trade_id,r.revision DESC),
 candidates AS(SELECT r.trade_id,r.binding_id,r.book_id,r.revision,'future'::text kind FROM future_heads r
  UNION ALL SELECT r.trade_id,r.option_instrument_id,r.book_id,r.revision,'option' FROM option_heads r
  UNION ALL SELECT r.trade_id,r.binding_id,r.book_id,r.revision,'swap' FROM swap_heads r),
 roots AS(SELECT c.* FROM candidates c WHERE job.request->'bookIds' @> jsonb_build_array(c.book_id)
  AND(NULLIF(job.request->'tradeIds','null'::jsonb) IS NULL OR job.request->'tradeIds' @> jsonb_build_array(c.trade_id)))
 SELECT * FROM roots UNION
 SELECT r.trade_id,r.binding_id,r.book_id,r.revision,'future' FROM roots parent
 JOIN trading.futures_option_events e ON(e.catalog_id,e.trade_id,e.trade_revision)=(job.catalog_id,parent.trade_id,parent.revision)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
 JOIN trading.trade_revisions r ON(r.catalog_id,r.trade_id,r.revision,r.publication_order,r.effective_at,r.execution_at)=
  (e.catalog_id,e.futures_trade_id,1,e.publication_order,e.effective_at,e.business_at) CROSS JOIN cut
 WHERE parent.kind='option' AND r.book_id=parent.book_id AND e.publication_order<=cut.known_through AND k.known_from<=cut.known_at;
$function$;
ALTER FUNCTION risk.lifecycle_scope_trades(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.lifecycle_scope_trades(risk.jobs) FROM PUBLIC;

CREATE FUNCTION risk.lifecycle_scope_policies(job risk.jobs) RETURNS TABLE(policy_id uuid,policy_revision integer,book_id uuid)
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 WITH cut AS(SELECT x.known_through,t.known_at FROM market_data.temporal_contexts t JOIN trading.contexts x
  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id)),
 owners AS MATERIALIZED(SELECT * FROM risk.lifecycle_scope_trades(job)),
 option_profiles AS(SELECT DISTINCT ON(p.trade_id,p.action)p.* FROM lifecycle.option_profiles p
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order) CROSS JOIN cut
  WHERE p.catalog_id=job.catalog_id AND p.action='option.expiry' AND p.publication_order<=cut.known_through AND k.known_from<=cut.known_at ORDER BY p.trade_id,p.action,p.revision DESC),
 selected AS(SELECT a.policy_id,a.policy_revision,o.book_id FROM settlement_data.futures_margin_adoptions a
  JOIN owners o ON(o.trade_id,o.binding_id,o.book_id,o.kind)=(a.trade_id,a.binding_id,a.book_id,'future')
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order) CROSS JOIN cut
  WHERE a.catalog_id=job.catalog_id AND a.publication_order<=cut.known_through AND k.known_from<=cut.known_at
  UNION SELECT p.margin_policy_id,p.margin_policy_revision,o.book_id FROM option_profiles p JOIN owners o ON(o.trade_id,o.revision,o.kind)=(p.trade_id,p.option_revision,'option'))
 SELECT s.* FROM selected s JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(job.catalog_id,s.policy_id,s.policy_revision)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order) CROSS JOIN cut
 WHERE p.publication_order<=cut.known_through AND k.known_from<=cut.known_at;
$function$;
ALTER FUNCTION risk.lifecycle_scope_policies(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.lifecycle_scope_policies(risk.jobs) FROM PUBLIC;

CREATE FUNCTION risk.bound_lifecycle_reader() RETURNS risk.jobs
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE b risk.worker_attempt_contexts;BEGIN
 PERFORM risk.require_worker_attempt();
 SELECT a.* INTO STRICT b FROM risk.worker_attempt_contexts a JOIN security.request_contexts q ON q.id=a.context_id
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 RETURN risk.owned_attempt(b.job_id,b.lease_token,false,false);
END$function$;
ALTER FUNCTION risk.bound_lifecycle_reader() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.bound_lifecycle_reader() FROM PUBLIC;

CREATE FUNCTION risk.lifecycle_trade_visible(catalog uuid,trade uuid,binding uuid,revision integer) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF NOT risk.worker_read() THEN RETURN false;END IF;job=risk.bound_lifecycle_reader();
 RETURN catalog=job.catalog_id AND EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)s WHERE s.trade_id=trade
  AND(binding IS NULL OR s.binding_id=binding) AND(revision IS NULL OR s.revision=revision));
END$function$;
CREATE FUNCTION risk.lifecycle_policy_visible(catalog uuid,policy uuid,revision integer) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF NOT risk.worker_read() THEN RETURN false;END IF;job=risk.bound_lifecycle_reader();
 RETURN catalog=job.catalog_id AND EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)s WHERE(s.policy_id,s.policy_revision)=(policy,revision));
END$function$;
ALTER FUNCTION risk.lifecycle_trade_visible(uuid,uuid,uuid,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION risk.lifecycle_policy_visible(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.lifecycle_trade_visible(uuid,uuid,uuid,integer),risk.lifecycle_policy_visible(uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.lifecycle_trade_visible(uuid,uuid,uuid,integer),risk.lifecycle_policy_visible(uuid,uuid,integer) TO openetrm_risk_worker;

CREATE FUNCTION risk.needs_gas(job risk.jobs) RETURNS boolean
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT NULLIF(job.request#>'{pricingPolicy,gasOwned}','null'::jsonb) IS NOT NULL
  OR CASE WHEN jsonb_typeof(job.request#>'{economic,projection,gasOwned}')='array'
    THEN jsonb_array_length(job.request#>'{economic,projection,gasOwned}')>0 ELSE false END
  OR EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)s JOIN settlement_data.futures_margin_policies p
   ON(p.catalog_id,p.id,p.revision)=(job.catalog_id,s.policy_id,s.policy_revision) WHERE p.delivery_profile_id IS NOT NULL);
$function$;
ALTER FUNCTION risk.needs_gas(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.needs_gas(risk.jobs) FROM PUBLIC;

CREATE OR REPLACE FUNCTION risk.authorized(job risk.jobs) RETURNS boolean
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT risk.authorized_pre_economic_lifecycle(job) AND(NOT risk.needs_gas(job)
  OR risk.actor_permission(job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,'ReadGasOwnedHoldings'));
$function$;

CREATE OR REPLACE FUNCTION risk.worker_read() RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT risk.worker_read_pre_economic_lifecycle() AND EXISTS(
  SELECT 1 FROM security.request_contexts q JOIN risk.worker_attempt_contexts b ON b.context_id=q.id
  JOIN risk.jobs j ON(j.id,j.lease_token)=(b.job_id,b.lease_token) JOIN risk.workers w ON w.login=session_user
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
   AND(j.worker_login,j.actor_id,j.tenant_id,j.workspace_id,j.catalog_id)=(session_user,q.actor_id,q.tenant_id,q.workspace_id,q.market_catalog_id)
   AND q.market_context_id=j.context_id AND j.lease_expires_at>clock_timestamp() AND j.state='running'
   AND j.security_generation=(SELECT generation FROM security.epoch) AND j.attempt_security_generation=j.security_generation
   AND risk.authorized(j) AND(NOT risk.needs_gas(j) OR security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,'ReadGasOwnedHoldings',clock_timestamp())));
$function$;

CREATE FUNCTION risk.worker_read_gas() RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT risk.worker_read() AND EXISTS(SELECT 1 FROM security.request_contexts q
  JOIN risk.worker_attempt_contexts b ON b.context_id=q.id JOIN risk.jobs j ON j.id=b.job_id
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND risk.needs_gas(j));
$function$;
ALTER FUNCTION risk.worker_read_gas() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.worker_read_gas() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.worker_read_gas() TO openetrm_risk_worker;

CREATE OR REPLACE FUNCTION security.authority_until(permission text) RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz;checked timestamptz=clock_timestamp();q security.request_contexts;j risk.jobs;w risk.workers;d security.agent_delegations;required text;
BEGIN
 until_at=security.authority_until_pre_economic_lifecycle(permission);
 IF NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN until_at;END IF;
 SELECT * INTO q FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 SELECT jobs.* INTO j FROM risk.worker_attempt_contexts b JOIN risk.jobs jobs ON(jobs.id,jobs.lease_token)=(b.job_id,b.lease_token)
  WHERE b.context_id=q.id;
 SELECT * INTO w FROM risk.workers WHERE login=session_user AND enabled;
 IF j.id IS NULL OR w.login IS NULL OR j.worker_login IS DISTINCT FROM session_user OR j.state<>'running'
  OR(j.actor_id,j.tenant_id,j.workspace_id,j.catalog_id) IS DISTINCT FROM(q.actor_id,q.tenant_id,q.workspace_id,w.catalog_id)
  OR(j.tenant_id,j.workspace_id) IS DISTINCT FROM(w.tenant_id,w.workspace_id)
  OR j.lease_expires_at<=checked OR j.security_generation<>(SELECT generation FROM security.epoch)
  OR j.attempt_security_generation<>j.security_generation OR NOT risk.authorized(j)
 THEN RETURN '-infinity';END IF;
 -- During bind, the exact owned attempt is linked before market admission. No
 -- partially bound context is returned to a worker caller.
 IF q.market_catalog_id IS NOT NULL AND(q.market_catalog_id,q.market_context_id) IS DISTINCT FROM(j.catalog_id,j.context_id)
 THEN RETURN '-infinity';END IF;
 until_at=least(until_at,j.lease_expires_at);
 IF j.delegation_id IS NOT NULL THEN SELECT * INTO d FROM security.agent_delegations WHERE id=j.delegation_id;
  until_at=least(until_at,coalesce(d.granted_to,'-infinity'),security.grant_until(d.delegator_id,j.tenant_id,j.workspace_id,'CreateDelegations',checked));END IF;
 FOREACH required IN ARRAY ARRAY['ReadMarketData','ReadTrades','RunScenario',permission]||CASE WHEN risk.needs_gas(j) THEN ARRAY['ReadGasOwnedHoldings'] ELSE ARRAY[]::text[] END LOOP
  until_at=least(until_at,security.grant_until(j.actor_id,j.tenant_id,j.workspace_id,required,checked),
   security.grant_until(w.principal_id,j.tenant_id,j.workspace_id,required,checked));
  IF j.delegation_id IS NOT NULL THEN until_at=least(until_at,security.grant_until(d.delegator_id,j.tenant_id,j.workspace_id,required,checked));END IF;
 END LOOP;
 RETURN until_at;
END$function$;

DO $guard$DECLARE p pg_proc;definition text;needle constant text='AND j.request->>''engineVersion''=engine_version';BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.claim(integer,text)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.claim(integer,text)';END IF;
 IF md5(p.prosrc)<>'e7b215c95dcb854c12ba0004bb7c091f' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.claim(integer,text)';END IF;
 definition=pg_get_functiondef(p.oid);
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'risk_economic_upgrade: service gate';END IF;
 EXECUTE replace(definition,needle,'AND j.request->>''engineVersion''=engine_version AND(NOT risk.needs_gas(j) OR security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,''ReadGasOwnedHoldings'',clock_timestamp()))');
END$guard$;

DO $guard$DECLARE p pg_proc;definition text;needle constant text='OR NOT risk.authorized(job) THEN';BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.owned_attempt(uuid,uuid,boolean,boolean)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.owned_attempt(uuid,uuid,boolean,boolean)';END IF;
 IF md5(p.prosrc)<>'ebf8e4ad30a2987e88c1ac561b85de11' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'risk.jobs'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.owned_attempt(uuid,uuid,boolean,boolean)';END IF;
 definition=pg_get_functiondef(p.oid);
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'risk_economic_upgrade: service gate';END IF;
 EXECUTE replace(definition,needle,'OR NOT risk.authorized(job) OR risk.needs_gas(job) AND NOT security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,''ReadGasOwnedHoldings'',clock_timestamp()) THEN');
END$guard$;

-- Direct RLS stays closed after cancellation. Protected readers first preserve
-- the exact owned-attempt control signal, after its lease and authority checks.
DO $guard$DECLARE p pg_proc;definition text;
 needle constant text=$needle$ IF NOT FOUND OR NOT risk.worker_read() THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 PERFORM risk.owned_attempt(binding.job_id,binding.lease_token,false,false);$needle$;
 replacement constant text=$replacement$ IF NOT FOUND THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 PERFORM risk.owned_attempt(binding.job_id,binding.lease_token,false,false);
 IF NOT risk.worker_read() THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;$replacement$;
BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.require_worker_attempt()');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing risk.require_worker_attempt()';END IF;
 IF md5(p.prosrc)<>'4fe4516340b840fb06262fa37faf9052' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'void'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f'
  OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.proallargtypes IS NOT NULL
  OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.require_worker_attempt()';END IF;
 definition=pg_get_functiondef(p.oid);
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'risk_economic_upgrade: worker cancellation gate';END IF;
 EXECUTE replace(definition,needle,replacement);
END$guard$;

-- Reuse the established role/worker identity checks without taking their epoch
-- lock. The ordinary owned-attempt check runs again after that lock is acquired.
DO $guard$DECLARE p pg_proc;definition text;needle constant text=' PERFORM 1 FROM security.epoch FOR SHARE;';BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid='risk.worker()'::regprocedure;
 IF md5(p.prosrc)<>'2b7395159c702f039a5f270e9d28acca' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'risk.workers'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.worker()';END IF;
 definition=pg_get_functiondef(p.oid);
 IF length(definition)-length(replace(definition,needle,''))<>length(needle)
  OR length(definition)-length(replace(definition,'clock_timestamp()',''))<>length('clock_timestamp()')
 THEN RAISE EXCEPTION 'risk_economic_upgrade: worker identity boundary';END IF;
 definition=replace(definition,'FUNCTION risk.worker()','FUNCTION risk.bind_worker_identity(checked_at timestamptz)');
 definition=replace(replace(definition,needle,''),'clock_timestamp()','checked_at');EXECUTE definition;
END$guard$;
ALTER FUNCTION risk.bind_worker_identity(timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.bind_worker_identity(timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION risk.bind(job_id uuid,token uuid) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE checked_at timestamptz=clock_timestamp();captured_generation bigint;candidate_until timestamptz;gas_until timestamptz;permission_until timestamptz;
 job risk.jobs;worker risk.workers;delegation security.agent_delegations;required text;context_token uuid=gen_random_uuid();day date;
BEGIN
 -- Plain MVCC reads take no epoch row lock and expose no business-source data.
 SELECT generation INTO captured_generation FROM security.epoch;
 worker=risk.bind_worker_identity(checked_at);
 SELECT * INTO job FROM risk.jobs j WHERE j.id=job_id AND(j.catalog_id,j.tenant_id,j.workspace_id)=(worker.catalog_id,worker.tenant_id,worker.workspace_id);
 IF job.id IS NULL OR job.worker_login IS DISTINCT FROM session_user OR job.lease_token IS DISTINCT FROM token OR token IS NULL
  OR job.lease_expires_at<=checked_at OR job.state NOT IN('running','cancellation_requested') THEN RAISE EXCEPTION 'risk_job_fence' USING ERRCODE='42501';END IF;
 IF captured_generation IS NULL OR job.security_generation IS DISTINCT FROM captured_generation OR job.attempt_security_generation IS DISTINCT FROM captured_generation
 THEN RAISE EXCEPTION 'risk_job_authority_changed' USING ERRCODE='42501';END IF;
 IF job.delegation_id IS NOT NULL THEN
  SELECT * INTO delegation FROM security.agent_delegations d WHERE d.id=job.delegation_id;
  IF delegation.id IS NULL OR(delegation.agent_id,delegation.tenant_id,delegation.workspace_id) IS DISTINCT FROM(job.actor_id,job.tenant_id,job.workspace_id)
   OR delegation.revoked OR checked_at<delegation.granted_from OR checked_at>=delegation.granted_to
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 END IF;
 candidate_until=least(job.lease_expires_at,checked_at+interval '60 seconds');
 -- Capture the optional gas candidate at the same original instant, before
 -- deriving whether the immutable native contract closure actually needs it.
 FOREACH required IN ARRAY ARRAY['ReadMarketData','ReadTrades','RunScenario','ReadGasOwnedHoldings'] LOOP
  permission_until=least(security.grant_until(job.actor_id,job.tenant_id,job.workspace_id,required,checked_at),
   security.grant_until(worker.principal_id,job.tenant_id,job.workspace_id,required,checked_at));
  IF job.delegation_id IS NOT NULL THEN
   permission_until=least(permission_until,delegation.granted_to,
    security.grant_until(delegation.delegator_id,job.tenant_id,job.workspace_id,required,checked_at),
    security.grant_until(delegation.delegator_id,job.tenant_id,job.workspace_id,'CreateDelegations',checked_at));
   IF NOT EXISTS(SELECT 1 FROM security.delegation_permissions p WHERE p.delegation_id=delegation.id AND p.permission=required)
   THEN permission_until='-infinity';END IF;
  END IF;
  IF required='ReadGasOwnedHoldings' THEN gas_until=permission_until;ELSE candidate_until=least(candidate_until,permission_until);END IF;
 END LOOP;
 IF candidate_until<=clock_timestamp() THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 IF risk.needs_gas(job) THEN candidate_until=least(candidate_until,gas_until);END IF;
 IF candidate_until<=clock_timestamp() THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 -- This is the first epoch SHARE wait. Recheck the exact current ownership and
 -- authority afterwards, while preserving the original grant/lease deadline.
 job=risk.owned_attempt(job_id,token,false,false);
 IF captured_generation IS DISTINCT FROM(SELECT generation FROM security.epoch) THEN RAISE EXCEPTION 'risk_job_authority_changed' USING ERRCODE='42501';END IF;
 IF candidate_until<=clock_timestamp() THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 day=(job.frame->>'businessDate')::date;
 IF day IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id,t.effective_on)=(job.catalog_id,job.context_id,day))
 THEN RAISE EXCEPTION 'risk_job_business_date';END IF;
 INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,delegation_id,runtime_login,expires_at)
 VALUES(context_token,job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,session_user,least(job.lease_expires_at,candidate_until));
 INSERT INTO risk.worker_attempt_contexts VALUES(context_token,job_id,token);
 PERFORM security.use_context(context_token);
 PERFORM security.use_market_context(job.catalog_id,job.context_id);
 PERFORM security.use_trading_context(job.catalog_id,job.valuation_at,day);
 PERFORM security.require_frame_inputs(job.catalog_id,job.revision_id,job.frame);
 PERFORM risk.owned_attempt(job_id,token,false,false);RETURN context_token;
END$function$;


CREATE OR REPLACE FUNCTION security.require_gas_owned(catalog uuid,writing boolean,configuring boolean DEFAULT false) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND(writing OR configuring OR NOT risk.worker_read_gas())
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_owned_pre_economic_lifecycle(catalog,writing,configuring);
END$function$;

GRANT EXECUTE ON FUNCTION security.use_trading_context(uuid,timestamptz,date),security.trading_context_day_matches(uuid,uuid,timestamptz),
 security.admit_trading_business_source(uuid,uuid,timestamptz,date),security.require_admitted_business_source(uuid,uuid,timestamptz,date,timestamptz) TO openetrm_risk_worker;

DO $patch$DECLARE p pg_proc;definition text;needle constant text='jsonb_array_length(frame->''observationIds'') NOT BETWEEN 1 AND 100000';BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid='risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure;
 IF md5(p.prosrc)<>'d2271c6b4f0a434a661aaff056d39ca6' OR p.proowner<>'openetrm_security_owner'::regrole OR NOT p.prosecdef
  OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)';END IF;
 definition=pg_get_functiondef(p.oid);
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'risk_economic_upgrade: observation count';END IF;
 EXECUTE replace(definition,needle,'jsonb_array_length(frame->''observationIds'') NOT BETWEEN (CASE WHEN request#>>''{economic,policy,implementation}''=''economic_lifecycle_no_variable_factors_v1'' THEN 0 ELSE 1 END) AND 100000');
END$patch$;

-- Each native reader rechecks the exact owned attempt before its definer read.
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'41acbf6567e715b51ebc0c46b39a98bf' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.read_futures_margin_state(','FUNCTION settlement_data.read_futures_margin_state_pre_risk('),'read_futures_margin_state.','read_futures_margin_state_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.read_futures_margin_state_pre_risk(uuid,uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_margin_state_pre_risk(uuid,uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.read_futures_margin_state(catalog uuid, trade uuid, binding uuid, context uuid, as_of timestamp with time zone) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id OR context IS DISTINCT FROM job.context_id OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE(scope.trade_id,scope.binding_id)=(trade,binding)) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.read_futures_margin_state_pre_risk(catalog,trade,binding,context,as_of);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.require_futures_margin_read(uuid,uuid,timestamptz,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.require_futures_margin_read(uuid,uuid,timestamptz,timestamptz)';END IF;
 IF md5(p.prosrc)<>'3ffca8b3afea62dbef69bca5a9b8aed8' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.require_futures_margin_read(uuid,uuid,timestamptz,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.require_futures_margin_read(','FUNCTION settlement_data.require_futures_margin_read_pre_risk('),'require_futures_margin_read.','require_futures_margin_read_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.require_futures_margin_read_pre_risk(uuid,uuid,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.require_futures_margin_read_pre_risk(uuid,uuid,timestamptz,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.require_futures_margin_read(catalog uuid, context uuid, as_of timestamp with time zone, until_at timestamp with time zone) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM settlement_data.require_futures_margin_read_pre_risk(catalog,context,as_of,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.require_futures_margin_read(uuid,uuid,timestamptz,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'bcd7f437e0a6187e957e07ec1ee45d3f' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.read_futures_known_history(','FUNCTION settlement_data.read_futures_known_history_pre_risk('),'read_futures_known_history.','read_futures_known_history_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.read_futures_known_history_pre_risk(uuid,uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_known_history_pre_risk(uuid,uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.read_futures_known_history(catalog uuid, trade uuid, binding uuid, context uuid, as_of timestamp with time zone) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.read_futures_known_history_pre_risk(catalog,trade,binding,context,as_of);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)';END IF;
 IF md5(p.prosrc)<>'fbe0cdf729c45b1db85d8f4ad542a8bf' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.read_futures_known_linked_history(','FUNCTION settlement_data.read_futures_known_linked_history_pre_risk('),'read_futures_known_linked_history.','read_futures_known_linked_history_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.read_futures_known_linked_history_pre_risk(uuid,uuid,uuid,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_known_linked_history_pre_risk(uuid,uuid,uuid,uuid,timestamptz,uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.read_futures_known_linked_history(catalog uuid, trade uuid, binding uuid, context uuid, as_of timestamp with time zone, event uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.read_futures_known_linked_history_pre_risk(catalog,trade,binding,context,as_of,event);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz)';END IF;
 IF md5(p.prosrc)<>'36e0434dc706a6b4efb48bbffbd60810' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.require_futures_known_reference(','FUNCTION settlement_data.require_futures_known_reference_pre_risk('),'require_futures_known_reference.','require_futures_known_reference_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.require_futures_known_reference_pre_risk(uuid,uuid,uuid,uuid,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.require_futures_known_reference_pre_risk(uuid,uuid,uuid,uuid,integer,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.require_futures_known_reference(catalog uuid, trade uuid, binding uuid, policy uuid, revision integer, until_at timestamp with time zone) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding)
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)scope WHERE(scope.policy_id,scope.policy_revision)=(policy,revision)) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM settlement_data.require_futures_known_reference_pre_risk(catalog,trade,binding,policy,revision,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.futures_margin_preview_authority(uuid)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.futures_margin_preview_authority(uuid)';END IF;
 IF md5(p.prosrc)<>'564f085f5ccb6b8ba6df7d0827ae5308' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'timestamp with time zone'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.futures_margin_preview_authority(uuid)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.futures_margin_preview_authority(','FUNCTION settlement_data.futures_margin_preview_authority_pre_risk('),'futures_margin_preview_authority.','futures_margin_preview_authority_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.futures_margin_preview_authority_pre_risk(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.futures_margin_preview_authority_pre_risk(uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.futures_margin_preview_authority(catalog uuid) RETURNS timestamp with time zone
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.futures_margin_preview_authority_pre_risk(catalog);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.futures_margin_preview_authority(uuid) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)';END IF;
 IF md5(p.prosrc)<>'1b987f77d87b9af6c1c70a4fed28bb52' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.read_option_known_history(','FUNCTION lifecycle.read_option_known_history_pre_risk('),'read_option_known_history.','read_option_known_history_pre_risk.');
END$guard$;
ALTER FUNCTION lifecycle.read_option_known_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamptz,date) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.read_option_known_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamptz,date) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION lifecycle.read_option_known_history(catalog uuid, trade uuid, binding uuid, revision integer, context uuid, as_of timestamp with time zone, business_date date) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding AND scope.revision=read_option_known_history.revision) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN lifecycle.read_option_known_history_pre_risk(catalog,trade,binding,revision,context,as_of,business_date);
END$function$;
GRANT EXECUTE ON FUNCTION lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.read_swap_payment_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing lifecycle.read_swap_payment_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)';END IF;
 IF md5(p.prosrc)<>'c0d15d6593d09d0cf6cddaeabe2c6e41' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper lifecycle.read_swap_payment_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.read_swap_payment_history(','FUNCTION lifecycle.read_swap_payment_history_pre_risk('),'read_swap_payment_history.','read_swap_payment_history_pre_risk.');
END$guard$;
ALTER FUNCTION lifecycle.read_swap_payment_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamptz,date) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.read_swap_payment_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamptz,date) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION lifecycle.read_swap_payment_history(catalog uuid, trade uuid, binding uuid, revision integer, context uuid, as_of timestamp with time zone, business_date date) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding AND scope.revision=read_swap_payment_history.revision) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN lifecycle.read_swap_payment_history_pre_risk(catalog,trade,binding,revision,context,as_of,business_date);
END$function$;
GRANT EXECUTE ON FUNCTION lifecycle.read_swap_payment_history(uuid,uuid,uuid,integer,uuid,timestamptz,date) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_gas_owned_valuation_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.require_gas_owned_valuation_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)';END IF;
 IF md5(p.prosrc)<>'ac8a4ad6e334737cfad8e21d26d260ad' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'boolean'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.require_gas_owned_valuation_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.require_gas_owned_valuation_scope(','FUNCTION security.require_gas_owned_valuation_scope_pre_risk('),'require_gas_owned_valuation_scope.','require_gas_owned_valuation_scope_pre_risk.');
END$guard$;
ALTER FUNCTION security.require_gas_owned_valuation_scope_pre_risk(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_owned_valuation_scope_pre_risk(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.require_gas_owned_valuation_scope(catalog uuid, context uuid, as_of timestamp with time zone, books uuid[], configured boolean, until_at timestamp with time zone) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR books IS NULL OR(SELECT array_agg(DISTINCT b ORDER BY b) FROM unnest(books)b) IS DISTINCT FROM(SELECT array_agg(value::uuid ORDER BY value::uuid) FROM jsonb_array_elements_text(job.request->'bookIds')) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN security.require_gas_owned_valuation_scope_pre_risk(catalog,context,as_of,books,configured,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION security.require_gas_owned_valuation_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)';END IF;
 IF md5(p.prosrc)<>'cb653a4bbd7cba12fedc4daa73c894af' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.require_gas_owned_read(','FUNCTION security.require_gas_owned_read_pre_risk('),'require_gas_owned_read.','require_gas_owned_read_pre_risk.');
END$guard$;
ALTER FUNCTION security.require_gas_owned_read_pre_risk(uuid,uuid,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_owned_read_pre_risk(uuid,uuid,timestamptz,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.require_gas_owned_read(catalog uuid, context uuid, at_time timestamp with time zone, until_at timestamp with time zone) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR at_time IS DISTINCT FROM job.valuation_at THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_gas_owned_read_pre_risk(catalog,context,at_time,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])';END IF;
 IF md5(p.prosrc)<>'ecfcf04d8377d077db490a2729b38331' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.read_gas_owned_holdings(','FUNCTION security.read_gas_owned_holdings_pre_risk('),'read_gas_owned_holdings.','read_gas_owned_holdings_pre_risk.');
END$guard$;
ALTER FUNCTION security.read_gas_owned_holdings_pre_risk(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_holdings_pre_risk(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.read_gas_owned_holdings(catalog uuid, context uuid, at_time timestamp with time zone, books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR at_time IS DISTINCT FROM job.valuation_at
   OR books IS NULL OR(SELECT array_agg(DISTINCT b ORDER BY b) FROM unnest(books)b) IS DISTINCT FROM(SELECT array_agg(value::uuid ORDER BY value::uuid) FROM jsonb_array_elements_text(job.request->'bookIds'))
   OR NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN security.read_gas_owned_holdings_pre_risk(catalog,context,at_time,books);
END$function$;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[]) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_cash_payment_read(uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.require_cash_payment_read(uuid,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'57fdceb059d90a44c6e6a7f43b38e340' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.require_cash_payment_read(uuid,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.require_cash_payment_read(','FUNCTION security.require_cash_payment_read_pre_risk('),'require_cash_payment_read.','require_cash_payment_read_pre_risk.');
END$guard$;
ALTER FUNCTION security.require_cash_payment_read_pre_risk(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_cash_payment_read_pre_risk(uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.require_cash_payment_read(catalog uuid, context uuid, valuation timestamp with time zone) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR valuation IS DISTINCT FROM job.valuation_at THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_cash_payment_read_pre_risk(catalog,context,valuation);
END$function$;
GRANT EXECUTE ON FUNCTION security.require_cash_payment_read(uuid,uuid,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)';END IF;
 IF md5(p.prosrc)<>'6cdd8bbb350b09c15482797105672705' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'boolean'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.require_gas_owned_projection_scope(','FUNCTION security.require_gas_owned_projection_scope_pre_risk('),'require_gas_owned_projection_scope.','require_gas_owned_projection_scope_pre_risk.');
END$guard$;
ALTER FUNCTION security.require_gas_owned_projection_scope_pre_risk(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_owned_projection_scope_pre_risk(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.require_gas_owned_projection_scope(catalog uuid, context uuid, at_time timestamp with time zone, books uuid[], configured boolean, until_at timestamp with time zone) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR at_time IS DISTINCT FROM job.valuation_at
   OR books IS NULL OR(SELECT array_agg(DISTINCT b ORDER BY b) FROM unnest(books)b) IS DISTINCT FROM(SELECT array_agg(value::uuid ORDER BY value::uuid) FROM jsonb_array_elements_text(job.request->'bookIds')) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN security.require_gas_owned_projection_scope_pre_risk(catalog,context,at_time,books,configured,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])';END IF;
 IF md5(p.prosrc)<>'0bd19cf50ad3026865838f7ad251508c' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.read_gas_owned_known_history(','FUNCTION security.read_gas_owned_known_history_pre_risk('),'read_gas_owned_known_history.','read_gas_owned_known_history_pre_risk.');
END$guard$;
ALTER FUNCTION security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.read_gas_owned_known_history(catalog uuid, context uuid, at_time timestamp with time zone, books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR at_time IS DISTINCT FROM job.valuation_at
   OR books IS NULL OR(SELECT array_agg(DISTINCT b ORDER BY b) FROM unnest(books)b) IS DISTINCT FROM(SELECT array_agg(value::uuid ORDER BY value::uuid) FROM jsonb_array_elements_text(job.request->'bookIds'))
   OR NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN security.read_gas_owned_known_history_pre_risk(catalog,context,at_time,books);
END$function$;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[]) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'06ae46a3ca4e8c2c47e802f23c881e78' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR p.prosecdef IS DISTINCT FROM true OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.read_gas_owned_projection_profile(','FUNCTION security.read_gas_owned_projection_profile_pre_risk('),'read_gas_owned_projection_profile.','read_gas_owned_projection_profile_pre_risk.');
END$guard$;
ALTER FUNCTION security.read_gas_owned_projection_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_projection_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.read_gas_owned_projection_profile(catalog uuid, requested_profile uuid, requested_revision integer, context uuid, at_time timestamp with time zone) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR at_time IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(job.request#>'{economic,projection,gasOwned}')selected
    JOIN trading.gas_owned_projection_profiles p ON(p.catalog_id,p.id,p.revision,p.account_id)=(job.catalog_id,requested_profile,requested_revision,(selected->>'accountId')::uuid)
    JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
    JOIN trading.contexts c ON(c.catalog_id,c.context_id)=(job.catalog_id,job.context_id) JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.catalog_id,c.context_id)
    JOIN trading.knowledge_receipts pk ON(pk.catalog_id,pk.publication_order)=(p.catalog_id,p.publication_order)
    JOIN trading.knowledge_receipts ak ON(ak.catalog_id,ak.publication_order)=(a.catalog_id,a.publication_order)
    WHERE(selected->>'profileId')::uuid=requested_profile AND(selected->>'revision')::integer=requested_revision
     AND job.request->'bookIds' @> jsonb_build_array(a.book_id) AND p.publication_order<=c.known_through AND a.publication_order<=c.known_through
     AND pk.known_from<=t.known_at AND ak.known_from<=t.known_at) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN security.read_gas_owned_projection_profile_pre_risk(catalog,requested_profile,requested_revision,context,at_time);
END$function$;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz) TO openetrm_risk_worker;



GRANT USAGE ON SCHEMA lifecycle TO openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION settlement_data.futures_margin_policy_visible(uuid,integer) TO openetrm_risk_worker;
DO $tables$DECLARE entry record;selected_policy record;found_policy boolean;BEGIN
 FOR entry IN SELECT * FROM(VALUES('settlement_data','futures_margin_policies'),('settlement_data','futures_margin_policy_occurrences'),
  ('settlement_data','futures_margin_adoptions'),('lifecycle','option_profiles'),('lifecycle','swap_profiles'))v(schema_name,table_name)
 LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE(n.nspname,c.relname)=(entry.schema_name,entry.table_name)
   AND c.relowner=current_user::regrole AND c.relrowsecurity AND c.relforcerowsecurity) THEN RAISE EXCEPTION 'risk_economic_upgrade: source table drift';END IF;
  found_policy=false;
  FOR selected_policy IN SELECT p.polname,p.polpermissive,pg_get_expr(p.polqual,p.polrelid)qual FROM pg_policy p WHERE p.polrelid=to_regclass(format('%I.%I',entry.schema_name,entry.table_name))
   AND p.polcmd='r' AND 'openetrm_runtime'::regrole=ANY(p.polroles)
  LOOP
   found_policy=true;
   EXECUTE format('CREATE POLICY %I ON %I.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)',
    selected_policy.polname||'_risk',entry.schema_name,entry.table_name,CASE WHEN selected_policy.polpermissive THEN 'PERMISSIVE' ELSE 'RESTRICTIVE' END,selected_policy.qual);
  END LOOP;
  IF NOT found_policy THEN RAISE EXCEPTION 'risk_economic_upgrade: missing runtime source policy';END IF;
  EXECUTE format('CREATE POLICY risk_owned_source ON %I.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',entry.schema_name,entry.table_name);
  IF entry.table_name='futures_margin_policies' THEN
   EXECUTE 'CREATE POLICY risk_lifecycle_scope ON settlement_data.futures_margin_policies AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.lifecycle_policy_visible(catalog_id,id,revision))';
  ELSIF entry.table_name='futures_margin_policy_occurrences' THEN
   EXECUTE 'CREATE POLICY risk_lifecycle_scope ON settlement_data.futures_margin_policy_occurrences AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.lifecycle_policy_visible(catalog_id,policy_id,policy_revision))';
  ELSIF entry.table_name='futures_margin_adoptions' THEN
   EXECUTE 'CREATE POLICY risk_lifecycle_scope ON settlement_data.futures_margin_adoptions AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.lifecycle_trade_visible(catalog_id,trade_id,binding_id,NULL))';
  ELSIF entry.table_name='option_profiles' THEN
   EXECUTE 'CREATE POLICY risk_lifecycle_scope ON lifecycle.option_profiles AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(action=''option.expiry'' AND risk.lifecycle_trade_visible(catalog_id,trade_id,NULL,option_revision))';
  ELSIF entry.table_name='swap_profiles' THEN
   EXECUTE 'CREATE POLICY risk_lifecycle_scope ON lifecycle.swap_profiles AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.lifecycle_trade_visible(catalog_id,trade_id,NULL,origin_revision))';
  END IF;
  EXECUTE format('GRANT SELECT ON %I.%I TO openetrm_risk_worker',entry.schema_name,entry.table_name);
 END LOOP;
END$tables$;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_lifecycle_projection_profile(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing security.require_lifecycle_projection_profile(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz)';END IF;
 IF md5(p.prosrc)<>'aa9a22232d1cd42a42c338b42b792412' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}' THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.require_lifecycle_projection_profile(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.require_lifecycle_projection_profile(','FUNCTION security.require_lifecycle_projection_profile_pre_risk('),'require_lifecycle_projection_profile.','require_lifecycle_projection_profile_pre_risk.');
END$guard$;
ALTER FUNCTION security.require_lifecycle_projection_profile_pre_risk(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_lifecycle_projection_profile_pre_risk(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.require_lifecycle_projection_profile(catalog uuid,kind text,trade uuid,profile uuid,revision integer,  context uuid,as_of timestamptz,business_date date,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR business_date IS DISTINCT FROM(job.frame->>'businessDate')::date
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.kind=CASE require_lifecycle_projection_profile.kind WHEN 'listed_option' THEN 'option' WHEN 'financial_swap' THEN 'swap' END) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_lifecycle_projection_profile_pre_risk(catalog,kind,trade,profile,revision,context,as_of,business_date,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION security.require_lifecycle_projection_profile(uuid,text,uuid,uuid,integer,uuid,timestamptz,date,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'7390b34a46df0bb1a3a804d130af8a4e' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'timestamp with time zone'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.futures_margin_delivery_read_authority(','FUNCTION settlement_data.futures_margin_delivery_read_authority_pre_risk('),'futures_margin_delivery_read_authority.','futures_margin_delivery_read_authority_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.futures_margin_delivery_read_authority_pre_risk(uuid,uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.futures_margin_delivery_read_authority_pre_risk(uuid,uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.futures_margin_delivery_read_authority(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS timestamp with time zone
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.futures_margin_delivery_read_authority_pre_risk(catalog,trade,binding,context,as_of);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'fca7d1bf94c010eb63a562d18e58a301' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.read_futures_delivery_profile(','FUNCTION settlement_data.read_futures_delivery_profile_pre_risk('),'read_futures_delivery_profile.','read_futures_delivery_profile_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.read_futures_delivery_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_delivery_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.read_futures_delivery_profile(catalog uuid,profile uuid,requested_revision integer,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)scope JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(job.catalog_id,scope.policy_id,scope.policy_revision)
    JOIN trading.gas_futures_delivery_terms g ON(g.catalog_id,g.profile_id,g.profile_revision)=(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision)
    JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id,a.book_id)=(g.catalog_id,g.account_id,scope.book_id)
    WHERE(p.delivery_profile_id,p.delivery_profile_revision)=(profile,requested_revision)) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.read_futures_delivery_profile_pre_risk(catalog,profile,requested_revision,context,as_of);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz)';END IF;
 IF md5(p.prosrc)<>'d27b911b22de52d09cc3d2241a9fd1c3' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.require_futures_delivery_profile_read(','FUNCTION settlement_data.require_futures_delivery_profile_read_pre_risk('),'require_futures_delivery_profile_read.','require_futures_delivery_profile_read_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.require_futures_delivery_profile_read_pre_risk(uuid,uuid,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.require_futures_delivery_profile_read_pre_risk(uuid,uuid,integer,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.require_futures_delivery_profile_read(catalog uuid,profile uuid,requested_revision integer,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)scope JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(job.catalog_id,scope.policy_id,scope.policy_revision)
    JOIN trading.gas_futures_delivery_terms g ON(g.catalog_id,g.profile_id,g.profile_revision)=(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision)
    JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id,a.book_id)=(g.catalog_id,g.account_id,scope.book_id)
    WHERE(p.delivery_profile_id,p.delivery_profile_revision)=(profile,requested_revision)) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM settlement_data.require_futures_delivery_profile_read_pre_risk(catalog,profile,requested_revision,until_at);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'c8d4767ba6a9e13395c51f39b00ede18' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.read_futures_delivery_obligation(','FUNCTION settlement_data.read_futures_delivery_obligation_pre_risk('),'read_futures_delivery_obligation.','read_futures_delivery_obligation_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.read_futures_delivery_obligation(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)scope WHERE scope.trade_id=trade AND scope.binding_id=binding) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.read_futures_delivery_obligation_pre_risk(catalog,trade,binding,context,as_of);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_economic_upgrade: missing settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz)';END IF;
 IF md5(p.prosrc)<>'3269aed6ae637282f7251fe370a4a98e' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION settlement_data.read_futures_delivery_position(','FUNCTION settlement_data.read_futures_delivery_position_pre_risk('),'read_futures_delivery_position.','read_futures_delivery_position_pre_risk.');
END$guard$;
ALTER FUNCTION settlement_data.read_futures_delivery_position_pre_risk(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_delivery_position_pre_risk(uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION settlement_data.read_futures_delivery_position(catalog uuid,holding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF catalog IS DISTINCT FROM job.catalog_id
   OR context IS DISTINCT FROM job.context_id
   OR as_of IS DISTINCT FROM job.valuation_at
   OR NOT EXISTS(SELECT 1 FROM settlement_data.futures_delivery_obligations d JOIN risk.lifecycle_scope_trades(job)scope ON(scope.trade_id,scope.binding_id)=(d.trade_id,d.binding_id) WHERE(d.catalog_id,d.position_id)=(catalog,holding)) THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN settlement_data.read_futures_delivery_position_pre_risk(catalog,holding,context,as_of);
END$function$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz) TO openetrm_risk_worker;


-- Session discovery belongs to interactive setup. A worker only reopens its
-- exact saved economic horizon contract, never another readable session.
REVOKE EXECUTE ON FUNCTION security.lifecycle_projection_sessions(uuid,uuid,timestamptz,date) FROM openetrm_risk_worker;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid='security.read_lifecycle_projection_horizon(uuid,text,integer,uuid,timestamptz,date)'::regprocedure;
 IF md5(p.prosrc)<>'281dd75cfed6c5a3157bfd46f50f39d6' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_economic_upgrade: unexpected helper security.read_lifecycle_projection_horizon(uuid,text,integer,uuid,timestamptz,date)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.read_lifecycle_projection_horizon(','FUNCTION security.read_lifecycle_projection_horizon_pre_risk('),'read_lifecycle_projection_horizon.','read_lifecycle_projection_horizon_pre_risk.');
END$guard$;
ALTER FUNCTION security.read_lifecycle_projection_horizon_pre_risk(uuid,text,integer,uuid,timestamptz,date) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_lifecycle_projection_horizon_pre_risk(uuid,text,integer,uuid,timestamptz,date) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.read_lifecycle_projection_horizon(catalog uuid,session text,opening_version integer,context uuid,as_of timestamptz,business_date date) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF(catalog,context,as_of,business_date) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at,(job.frame->>'businessDate')::date)
   OR session IS DISTINCT FROM job.request#>>'{economic,projection,session}'
   OR opening_version IS DISTINCT FROM(job.request#>>'{economic,projection,sessionVersion}')::integer
   OR job.request#>>'{economic,projection,horizonPolicy}' IS DISTINCT FROM 'source_known_effective_session_schedule_v1'
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 RETURN security.read_lifecycle_projection_horizon_pre_risk(catalog,session,opening_version,context,as_of,business_date);
END$function$;
COMMIT;
