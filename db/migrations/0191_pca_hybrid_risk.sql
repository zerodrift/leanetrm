-- PCA and hybrid qualification retain the existing complete Monte Carlo custody.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $guard$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('risk.retain_monte_carlo(risk.jobs)')
  AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='3eaffb59b67df9c84c5ebeb64d7a079599e6fe39d97a21d1e50046c4ef9e5906'
  AND p.proacl::text=ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[]::aclitem[]::text
  AND p.proconfig=ARRAY['search_path=pg_catalog']::text[])
 THEN RAISE EXCEPTION 'pca_hybrid_risk_upgrade: unexpected retained simulation helper' USING ERRCODE='55000';END IF;
END $guard$;

CREATE OR REPLACE FUNCTION risk.retain_monte_carlo(job risk.jobs) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE spec jsonb=job.request->'monteCarlo';policy jsonb=spec->'policy';analysis jsonb=NULLIF(spec->'analysis','null'::jsonb);field text;
BEGIN
 IF jsonb_typeof(spec) IS DISTINCT FROM 'object' OR spec-ARRAY['history','policy','factors','analysis']<>'{}'::jsonb
  OR NULLIF(job.request->'parametric','null'::jsonb) IS NOT NULL OR NULLIF(job.request->'joint','null'::jsonb) IS NOT NULL
  OR NULLIF(job.request->'historical','null'::jsonb) IS NOT NULL
  OR job.frame ? 'run' OR job.request->>'detail' IS DISTINCT FROM 'holdings'
  OR job.request->'bumps' IS DISTINCT FROM '[]'::jsonb OR job.request->'stress' IS DISTINCT FROM '[]'::jsonb
  OR spec#>'{history,pricingPolicy}' IS DISTINCT FROM job.request->'pricingPolicy'
  OR spec#>'{history,scope,context}' IS DISTINCT FROM job.request->'context'
  OR jsonb_typeof(policy) IS DISTINCT FROM 'object'
  OR policy-ARRAY['convention','numericalPolicy','estimator','centering','drift','horizon','quantilePolicy','tailPolicy','valuationPolicy','confidence',
    'distribution','randomGenerator','shockPolicy','seed','paths','samplingUncertainty']<>'{}'::jsonb
  OR policy->>'convention' IS DISTINCT FROM 'dated_factor_full_pv_monte_carlo_v1'
  OR policy->>'numericalPolicy' IS DISTINCT FROM 'exact_ldl_binary64_polar_v1'
  OR policy->>'estimator' IS DISTINCT FROM 'equal_weight_sample_n_minus_one_v1'
  OR policy->>'centering' IS DISTINCT FROM 'sample_mean_centered_v1'
  OR NOT coalesce(policy->>'drift' IN('zero_drift_v1','sample_mean_drift_v1'),false)
  OR policy->>'horizon' IS DISTINCT FROM 'frozen_portfolio_one_adjacent_session_v1'
  OR policy->>'quantilePolicy' IS DISTINCT FROM 'empirical_inverse_cdf_v1'
  OR policy->>'tailPolicy' IS DISTINCT FROM 'signed_upper_loss_fractional_mass_uniform_ties_v1'
  OR policy->>'valuationPolicy' IS DISTINCT FROM 'current_instrument_pv_frozen_nonfactor_inputs_v1'
  OR policy->>'distribution' IS DISTINCT FROM 'joint_additive_gaussian_v1'
  OR policy->>'randomGenerator' IS DISTINCT FROM 'sha256_counter_polar_log_series_v1'
  OR policy->>'shockPolicy' IS DISTINCT FROM 'binary64_exact_to_native_18_places_to_even_v1'
  OR policy->>'samplingUncertainty' IS DISTINCT FROM 'binomial_order_statistic_binary64_95_v1'
  OR jsonb_typeof(policy->'confidence') IS DISTINCT FROM 'string'
  OR coalesce(policy->>'confidence','')!~'^0\.[0-9]{1,18}$' OR(policy->>'confidence')::numeric<=0.5 OR(policy->>'confidence')::numeric>0.9999
  OR(policy->>'confidence')::double precision<=0.5
  OR jsonb_typeof(policy->'seed') IS DISTINCT FROM 'string' OR coalesce(policy->>'seed','')!~'^(0|[1-9][0-9]{0,19})$'
  OR(policy->>'seed')::numeric>18446744073709551615
  OR jsonb_typeof(policy->'paths') IS DISTINCT FROM 'number' OR coalesce(policy->>'paths','')!~'^[1-9][0-9]{0,4}$'
  OR(policy->>'paths')::integer NOT BETWEEN 2 AND 16384
 THEN RAISE EXCEPTION 'risk_monte_carlo_input';END IF;
 IF analysis IS NOT NULL THEN
  IF jsonb_typeof(analysis) IS DISTINCT FROM 'object'
   OR analysis-ARRAY['convention','numericalPolicy','scalingPolicy','residualPolicy','valuationPolicy','directionPolicy','retainedComponents','tradingTicks','stabilityRelative','lossAbsolute','lossRelative']<>'{}'::jsonb
   OR analysis->>'convention' IS DISTINCT FROM 'correlation_pca_hybrid_qualification_v1'
   OR analysis->>'numericalPolicy' IS DISTINCT FROM 'cyclic_jacobi_binary64_residual_checks_v1'
   OR analysis->>'scalingPolicy' IS DISTINCT FROM 'sample_sd_constant_unit_v1'
   OR analysis->>'residualPolicy' IS DISTINCT FROM 'complete_native_shocks_all_factor_delta_v1'
   OR analysis->>'valuationPolicy' IS DISTINCT FROM 'full_reference_qualification_v1'
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

COMMIT;
