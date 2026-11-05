-- Hybrid Monte Carlo retains explicit validation and fallback consent on existing jobs.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $guard$ DECLARE expected record;BEGIN
 FOR expected IN SELECT * FROM(VALUES
 ('risk.retain_monte_carlo(risk.jobs)','d5bf9b840ab897fe69f5de07b09bdc7e0b76a4ef1db44f140b76be629674b47b'),
 ('risk.summary(risk.jobs)','21feea7864648ee869c3023d90b9b095f0b909085477fcbb936d542331b68949')
 )v(signature,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(expected.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=expected.hash
   AND p.proacl::text=ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[]::aclitem[]::text
   AND p.proconfig=ARRAY['search_path=pg_catalog']::text[])
  THEN RAISE EXCEPTION 'hybrid_monte_carlo_upgrade: unexpected helper %',expected.signature USING ERRCODE='55000';END IF;
 END LOOP;
END $guard$;

CREATE OR REPLACE FUNCTION risk.retain_monte_carlo(job risk.jobs) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE spec jsonb=job.request->'monteCarlo';policy jsonb=spec->'policy';analysis jsonb=NULLIF(spec->'analysis','null'::jsonb);execution jsonb=NULLIF(spec->'hybrid','null'::jsonb);
 is_hybrid boolean=coalesce(policy->>'convention'='dated_factor_hybrid_monte_carlo_v1',false);field text;
BEGIN
 IF jsonb_typeof(spec) IS DISTINCT FROM 'object' OR spec-ARRAY['history','policy','factors','analysis','hybrid']<>'{}'::jsonb
  OR NULLIF(job.request->'parametric','null'::jsonb) IS NOT NULL OR NULLIF(job.request->'joint','null'::jsonb) IS NOT NULL
  OR NULLIF(job.request->'historical','null'::jsonb) IS NOT NULL
  OR job.frame ? 'run' OR job.request->>'detail' IS DISTINCT FROM 'holdings'
  OR job.request->'bumps' IS DISTINCT FROM '[]'::jsonb OR job.request->'stress' IS DISTINCT FROM '[]'::jsonb
  OR spec#>'{history,pricingPolicy}' IS DISTINCT FROM job.request->'pricingPolicy'
  OR spec#>'{history,scope,context}' IS DISTINCT FROM job.request->'context'
  OR jsonb_typeof(policy) IS DISTINCT FROM 'object'
  OR policy-ARRAY['convention','numericalPolicy','estimator','centering','drift','horizon','quantilePolicy','tailPolicy','valuationPolicy','confidence',
    'distribution','randomGenerator','shockPolicy','seed','paths','samplingUncertainty']<>'{}'::jsonb
  OR NOT coalesce((policy->>'convention'='dated_factor_full_pv_monte_carlo_v1'
    AND policy->>'valuationPolicy'='current_instrument_pv_frozen_nonfactor_inputs_v1'
    AND policy->>'samplingUncertainty'='binomial_order_statistic_binary64_95_v1')
   OR(policy->>'convention'='dated_factor_hybrid_monte_carlo_v1'
    AND policy->>'valuationPolicy'='pca_delta_gamma_frozen_nonfactor_inputs_v1'
    AND policy->>'samplingUncertainty'='empirical_prefix_diagnostics_no_interval_v1'),false)
  OR policy->>'numericalPolicy' IS DISTINCT FROM 'exact_ldl_binary64_polar_v1'
  OR policy->>'estimator' IS DISTINCT FROM 'equal_weight_sample_n_minus_one_v1'
  OR policy->>'centering' IS DISTINCT FROM 'sample_mean_centered_v1'
  OR NOT coalesce(policy->>'drift' IN('zero_drift_v1','sample_mean_drift_v1'),false)
  OR policy->>'horizon' IS DISTINCT FROM 'frozen_portfolio_one_adjacent_session_v1'
  OR policy->>'quantilePolicy' IS DISTINCT FROM 'empirical_inverse_cdf_v1'
  OR policy->>'tailPolicy' IS DISTINCT FROM 'signed_upper_loss_fractional_mass_uniform_ties_v1'
  OR policy->>'distribution' IS DISTINCT FROM 'joint_additive_gaussian_v1'
  OR policy->>'randomGenerator' IS DISTINCT FROM 'sha256_counter_polar_log_series_v1'
  OR policy->>'shockPolicy' IS DISTINCT FROM 'binary64_exact_to_native_18_places_to_even_v1'
  OR jsonb_typeof(policy->'confidence') IS DISTINCT FROM 'string'
  OR coalesce(policy->>'confidence','')!~'^0\.[0-9]{1,18}$' OR(policy->>'confidence')::numeric<=0.5 OR(policy->>'confidence')::numeric>0.9999
  OR(policy->>'confidence')::double precision<=0.5
  OR jsonb_typeof(policy->'seed') IS DISTINCT FROM 'string' OR coalesce(policy->>'seed','')!~'^(0|[1-9][0-9]{0,19})$'
  OR(policy->>'seed')::numeric>18446744073709551615
  OR jsonb_typeof(policy->'paths') IS DISTINCT FROM 'number' OR coalesce(policy->>'paths','')!~'^[1-9][0-9]{0,4}$'
  OR(policy->>'paths')::integer NOT BETWEEN 2 AND 16384
 THEN RAISE EXCEPTION 'risk_monte_carlo_input';END IF;
 IF is_hybrid THEN
  IF analysis IS NULL OR jsonb_typeof(execution) IS DISTINCT FROM 'object'
   OR execution-ARRAY['convention','validationPolicy','validationPaths','validationSeed','fallbackScope']<>'{}'::jsonb
   OR execution->>'convention' IS DISTINCT FROM 'pca_delta_gamma_sampled_pv_validation_v1'
   OR execution->>'validationPolicy' IS DISTINCT FROM 'sha256_ranked_indices_independent_of_loss_v1'
   OR execution->>'fallbackScope' IS DISTINCT FROM 'whole_portfolio_full_pv_v1'
   OR jsonb_typeof(execution->'validationPaths') IS DISTINCT FROM 'number'
   OR coalesce(execution->>'validationPaths','')!~'^[1-9][0-9]{0,4}$'
   OR(execution->>'validationPaths')::integer NOT BETWEEN 2 AND(policy->>'paths')::integer
   OR jsonb_typeof(execution->'validationSeed') IS DISTINCT FROM 'string'
   OR coalesce(execution->>'validationSeed','')!~'^(0|[1-9][0-9]{0,19})$'
   OR(execution->>'validationSeed')::numeric>18446744073709551615
  THEN RAISE EXCEPTION 'risk_monte_carlo_input';END IF;
 ELSIF execution IS NOT NULL THEN RAISE EXCEPTION 'risk_monte_carlo_input';END IF;
 IF analysis IS NOT NULL THEN
  IF jsonb_typeof(analysis) IS DISTINCT FROM 'object'
   OR analysis-ARRAY['convention','numericalPolicy','scalingPolicy','residualPolicy','valuationPolicy','directionPolicy','retainedComponents','tradingTicks','stabilityRelative','lossAbsolute','lossRelative']<>'{}'::jsonb
   OR analysis->>'convention' IS DISTINCT FROM 'correlation_pca_hybrid_qualification_v1'
   OR analysis->>'numericalPolicy' IS DISTINCT FROM 'cyclic_jacobi_binary64_residual_checks_v1'
   OR analysis->>'scalingPolicy' IS DISTINCT FROM 'sample_sd_constant_unit_v1'
   OR analysis->>'residualPolicy' IS DISTINCT FROM 'complete_native_shocks_all_factor_delta_v1'
   OR NOT coalesce(CASE WHEN is_hybrid
    THEN analysis->>'valuationPolicy' IN('sampled_panel_no_fallback_qualification_v1','sampled_panel_opt_in_whole_portfolio_full_pv_v1')
    ELSE analysis->>'valuationPolicy' IN('full_reference_no_fallback_qualification_v1','full_reference_opt_in_full_pv_qualification_v1') END,false)
   OR analysis->>'directionPolicy' IS DISTINCT FROM 'pc_0_01_native_17_places_collinear_v1'
   OR jsonb_typeof(analysis->'retainedComponents') IS DISTINCT FROM 'number'
   OR coalesce(analysis->>'retainedComponents','')!~'^[1-8]$'
   OR (analysis->>'retainedComponents')::integer>jsonb_array_length(spec->'factors')
   OR jsonb_typeof(analysis->'tradingTicks') IS DISTINCT FROM 'number'
   OR coalesce(analysis->>'tradingTicks','')!~'^[1-9][0-9]{0,3}$'
   OR (analysis->>'tradingTicks')::integer>1000
  THEN RAISE EXCEPTION 'risk_monte_carlo_input';END IF;
  FOREACH field IN ARRAY ARRAY['stabilityRelative','lossAbsolute','lossRelative'] LOOP
   IF jsonb_typeof(analysis->field) IS DISTINCT FROM 'string'
    OR coalesce(analysis->>field,'')!~'^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$'
    OR length(analysis->>field)>45 OR (analysis->>field)::numeric<0 OR (analysis->>field)::numeric>=1e24
   THEN RAISE EXCEPTION 'risk_monte_carlo_input';END IF;
  END LOOP;
 END IF;
 PERFORM risk.axis_commands(job.request);PERFORM risk.require_monte_carlo_axes(job);
 PERFORM risk.retain_statistical_history(job,spec);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'risk_monte_carlo_input';
END
$function$;
ALTER FUNCTION risk.retain_monte_carlo(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_monte_carlo(risk.jobs) FROM PUBLIC;

CREATE OR REPLACE FUNCTION risk.summary(job risk.jobs)
 RETURNS jsonb
 LANGUAGE sql
 SET search_path TO 'pg_catalog'
AS $function$
 SELECT jsonb_build_object('jobId',job.id,'requestId',job.request_id,'state',job.state,'contextId',job.context_id,
  'valuationAt',job.valuation_at,'createdAt',job.created_at,'startedAt',job.started_at,'finishedAt',job.finished_at,
  'attempt',job.attempt,'errorCode',job.error_code,'engineVersion',job.request->>'engineVersion','convention',job.request->>'convention',
  'run',job.frame->'run','scope',job.frame->'scope','shocks',jsonb_array_length(job.request->'bumps'))||
 CASE WHEN jsonb_typeof(job.request->'parametric')='object' THEN jsonb_build_object('kind','parametric',
  'historyId',job.frame#>'{factorHistory,expectedManifestId}','confidence',job.request#>'{parametric,policy,confidence}',
  'calibrationAvailable',job.result#>'{parametric,result,available}') WHEN jsonb_typeof(job.request->'historical')='object' THEN jsonb_build_object('kind','historical',
  'historyId',job.frame#>'{factorHistory,expectedManifestId}','confidence',job.request#>'{historical,policy,confidence}',
  'calibrationAvailable',job.result#>'{historical,result,available}') WHEN jsonb_typeof(job.request->'monteCarlo')='object' THEN jsonb_build_object('kind',CASE WHEN job.request#>>'{monteCarlo,policy,convention}'='dated_factor_hybrid_monte_carlo_v1' THEN 'hybrid_monte_carlo' ELSE 'monte_carlo' END,
  'historyId',job.frame#>'{factorHistory,expectedManifestId}','confidence',job.request#>'{monteCarlo,policy,confidence}',
  'scenarioCount',job.request#>'{monteCarlo,policy,paths}','calibrationAvailable',job.result#>'{monteCarlo,result,available}') ELSE '{}'::jsonb END||
 CASE WHEN jsonb_typeof(job.request->'joint')='object' THEN jsonb_build_object('kind',CASE WHEN jsonb_typeof(job.request#>'{joint,scenarios}')='object' THEN 'scenarios' ELSE 'derivatives' END,
  'scenarioCount',coalesce(jsonb_array_length(job.request#>'{joint,scenarios,scenarios}'),0),
  'derivativeCount',coalesce(2*jsonb_array_length(job.request#>'{joint,derivatives,axes}')+jsonb_array_length(job.request#>'{joint,derivatives,mixed}'),0)) ELSE '{}'::jsonb END||
 CASE WHEN job.request#>>'{monteCarlo,policy,convention}'='dated_factor_hybrid_monte_carlo_v1' THEN jsonb_build_object(
  'appliedMethod',job.result#>'{monteCarlo,hybrid,appliedMethod}',
  'fallbackApplied',job.result#>'{monteCarlo,hybrid,fallbackApplied}',
  'approximate',job.result#>'{monteCarlo,hybrid,approximate}') ELSE '{}'::jsonb END;
$function$;

COMMIT;
