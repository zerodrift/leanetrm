-- Complete historical statistical jobs share proven clock/source custody and
-- ordinary PV scenarios; legacy parametric and named-command contracts remain separate.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $guard$ DECLARE expected record;BEGIN
 FOR expected IN SELECT * FROM(VALUES
('risk.archive_parametric(uuid,integer,timestamp with time zone,uuid)','11920458627865a59487993ac62a1a40b021244d8179ff662bca78b6a052430b',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[]),
('risk.axis_commands(jsonb)','4e975850408d13044a3d8972c2f31099fd8166fdc3be0136e5a3485682cd5689',ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[]),
('risk.guard_run()','53d101e2d083617456e58b1b06fdc9b0f9f7f13921c4eb9ea3433af73c6f9ebb',ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[]),
('risk.require_joint_axes(risk.jobs)','89557a4080bc182408ee1fad894d88d670b7ec97630721c3266b6927367778b4',ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[]),
('risk.retain_axes(risk.jobs,jsonb)','4cd337b45b0d33d91f1428f75c49304ce805f7abe4ce031fff2b703f6bc573ea',ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[]),
('risk.retain_parametric(risk.jobs)','924ed07356d195586fa01b9f4c8af51b5ec69dd832b2f349fddadf7ab55c1436',ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[]),
('risk.summary(risk.jobs)','0f6fe33368aadebd935401a63031cefa6aa75496321cf85e1e04ceeb84782798',ARRAY['openetrm_security_owner=X/openetrm_security_owner']::text[],ARRAY['search_path=pg_catalog']::text[])
 )v(signature,hash,acl,configuration) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(expected.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=expected.hash
   AND p.proacl::text=expected.acl::aclitem[]::text AND p.proconfig=expected.configuration)
  THEN RAISE EXCEPTION 'historical_risk_upgrade: unexpected helper %',expected.signature USING ERRCODE='55000';END IF;
 END LOOP;
END $guard$;
CREATE OR REPLACE FUNCTION risk.axis_commands(request jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE joint jsonb=NULLIF(request->'joint','null'::jsonb);named jsonb;derivatives jsonb;sample jsonb;axis jsonb;pair jsonb;tolerance jsonb;field text;amount numeric;bumps jsonb;shifts jsonb;
BEGIN
 IF NULLIF(request->'historical','null'::jsonb) IS NOT NULL THEN
  IF jsonb_typeof(request->'historical') IS DISTINCT FROM 'object' OR NULLIF(request->'parametric','null'::jsonb) IS NOT NULL OR joint IS NOT NULL
   OR request->'bumps' IS DISTINCT FROM '[]'::jsonb OR request->'stress' IS DISTINCT FROM '[]'::jsonb
   OR jsonb_typeof(request#>'{historical,factors}') IS DISTINCT FROM 'array'
   OR jsonb_array_length(request#>'{historical,factors}') NOT BETWEEN 1 AND 128
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(request#>'{historical,factors}')f WHERE jsonb_typeof(f) IS DISTINCT FROM 'object'
    OR f->>'coordinate' IS DISTINCT FROM 'market_quote' OR f#>>'{target,kind}' IS NULL OR f#>>'{target,kind}' NOT IN('contract','points'))
   OR(SELECT count(*)<>count(DISTINCT jsonb_strip_nulls(f)) FROM jsonb_array_elements(request#>'{historical,factors}')f)
  THEN RAISE EXCEPTION 'risk_historical_input';END IF;
  SELECT jsonb_agg(jsonb_build_object('factor',f)) INTO shifts FROM jsonb_array_elements(request#>'{historical,factors}')f;
  RETURN jsonb_build_object('bumps','[]'::jsonb,'stress','[]'::jsonb,'factors',shifts);
 END IF;
 IF joint IS NULL THEN RETURN jsonb_build_object('bumps',request->'bumps','stress',request->'stress');END IF;
 IF jsonb_typeof(joint) IS DISTINCT FROM 'object' OR joint-ARRAY['convention','scenarios','derivatives']<>'{}'::jsonb
  OR joint->>'convention' IS DISTINCT FROM 'portfolio_joint_risk_v1' OR request->'bumps' IS DISTINCT FROM '[]'::jsonb
  OR jsonb_typeof(request->'stress') IS DISTINCT FROM 'array' OR jsonb_array_length(request->'stress')>16
 THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 named=NULLIF(joint->'scenarios','null'::jsonb);derivatives=NULLIF(joint->'derivatives','null'::jsonb);
 IF(named IS NULL)=(derivatives IS NULL) THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 IF named IS NOT NULL THEN
  IF jsonb_typeof(named) IS DISTINCT FROM 'object' OR named-ARRAY['id','version','convention','scenarios']<>'{}'::jsonb
   OR jsonb_typeof(named->'id') IS DISTINCT FROM 'string' OR length(btrim(named->>'id')) NOT BETWEEN 1 AND 128
   OR jsonb_typeof(named->'version') IS DISTINCT FROM 'string' OR length(btrim(named->>'version')) NOT BETWEEN 1 AND 128
   OR named->>'convention' IS DISTINCT FROM 'independent_joint_absolute_curve_scenarios_v1'
   OR jsonb_typeof(named->'scenarios') IS DISTINCT FROM 'array' OR jsonb_array_length(named->'scenarios') NOT BETWEEN 1 AND 32
  THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  FOR sample IN SELECT value FROM jsonb_array_elements(named->'scenarios') LOOP
   IF jsonb_typeof(sample) IS DISTINCT FROM 'object' OR sample-ARRAY['id','label','shifts']<>'{}'::jsonb
    OR jsonb_typeof(sample->'id') IS DISTINCT FROM 'string' OR length(btrim(sample->>'id')) NOT BETWEEN 1 AND 128
    OR jsonb_typeof(sample->'label') IS DISTINCT FROM 'string' OR length(btrim(sample->>'label')) NOT BETWEEN 1 AND 160
    OR jsonb_typeof(sample->'shifts') IS DISTINCT FROM 'array' OR jsonb_array_length(sample->'shifts') NOT BETWEEN 1 AND 16
    OR(SELECT count(*)<>count(DISTINCT jsonb_strip_nulls(value->'factor')) FROM jsonb_array_elements(sample->'shifts'))
   THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  END LOOP;
  IF(SELECT count(*)<>count(DISTINCT value->>'id') FROM jsonb_array_elements(named->'scenarios')) THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  SELECT coalesce(jsonb_agg(shift),'[]'::jsonb) INTO shifts FROM jsonb_array_elements(named->'scenarios') s CROSS JOIN LATERAL jsonb_array_elements(s->'shifts') shift;
  RETURN jsonb_build_object('bumps','[]'::jsonb,'stress',shifts||(request->'stress'));
 END IF;
 IF jsonb_typeof(derivatives) IS DISTINCT FROM 'object' OR derivatives-ARRAY['convention','axes','mixed']<>'{}'::jsonb
  OR derivatives->>'convention' IS DISTINCT FROM 'exact_central_half_nominal_double_v1'
  OR jsonb_typeof(derivatives->'axes') IS DISTINCT FROM 'array' OR jsonb_array_length(derivatives->'axes') NOT BETWEEN 1 AND 16
  OR jsonb_typeof(derivatives->'mixed') IS DISTINCT FROM 'array' OR jsonb_array_length(derivatives->'mixed')>16
 THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 FOR axis IN SELECT value FROM jsonb_array_elements(derivatives->'axes') LOOP
  IF jsonb_typeof(axis) IS DISTINCT FROM 'object' OR axis-ARRAY['id','bump','deltaTolerance','gammaTolerance']<>'{}'::jsonb
   OR jsonb_typeof(axis->'id') IS DISTINCT FROM 'string' OR length(btrim(axis->>'id')) NOT BETWEEN 1 AND 128
   OR jsonb_typeof(axis->'bump') IS DISTINCT FROM 'object'
  THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  IF jsonb_typeof(axis#>'{bump,size}') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  amount=(axis#>>'{bump,size}')::numeric;
  IF amount IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) OR amount<=0 OR amount*2>=1e24
   OR amount*1e18<>trunc(amount*1e18) OR mod(amount*1e18,2)<>0 THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 END LOOP;
 IF(SELECT count(*)<>count(DISTINCT value->>'id') FROM jsonb_array_elements(derivatives->'axes')) THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 FOR pair IN SELECT value FROM jsonb_array_elements(derivatives->'mixed') LOOP
  IF jsonb_typeof(pair) IS DISTINCT FROM 'object' OR pair-ARRAY['id','firstAxis','secondAxis','tolerance']<>'{}'::jsonb
   OR jsonb_typeof(pair->'id') IS DISTINCT FROM 'string' OR length(btrim(pair->>'id')) NOT BETWEEN 1 AND 128
   OR pair->>'firstAxis' IS NOT DISTINCT FROM pair->>'secondAxis'
   OR(SELECT count(*) FROM jsonb_array_elements(derivatives->'axes') a WHERE a->>'id'=pair->>'firstAxis')<>1
   OR(SELECT count(*) FROM jsonb_array_elements(derivatives->'axes') a WHERE a->>'id'=pair->>'secondAxis')<>1
  THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 END LOOP;
 IF(SELECT count(*)<>count(DISTINCT value->>'id') FROM jsonb_array_elements(derivatives->'mixed')) OR EXISTS(
  SELECT 1 FROM jsonb_array_elements(derivatives->'mixed') p GROUP BY least(p->>'firstAxis',p->>'secondAxis'),greatest(p->>'firstAxis',p->>'secondAxis') HAVING count(*)>1)
 THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 FOR tolerance IN
  SELECT a->'deltaTolerance' FROM jsonb_array_elements(derivatives->'axes') a
  UNION ALL SELECT a->'gammaTolerance' FROM jsonb_array_elements(derivatives->'axes') a
  UNION ALL SELECT p->'tolerance' FROM jsonb_array_elements(derivatives->'mixed') p
 LOOP
  IF jsonb_typeof(tolerance) IS DISTINCT FROM 'object' OR tolerance-ARRAY['absolute','relative']<>'{}'::jsonb THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  FOREACH field IN ARRAY ARRAY['absolute','relative'] LOOP
   IF jsonb_typeof(tolerance->field) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'risk_joint_input';END IF;
   amount=(tolerance->>field)::numeric;
   IF amount IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) OR amount<0 OR amount>=1e24 OR scale(amount)>18 THEN RAISE EXCEPTION 'risk_joint_input';END IF;
  END LOOP;
 END LOOP;
 SELECT jsonb_agg(a->'bump') INTO bumps FROM jsonb_array_elements(derivatives->'axes') a;
 RETURN jsonb_build_object('bumps',bumps,'stress',request->'stress');
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'risk_joint_input';
END$function$
;
CREATE OR REPLACE FUNCTION risk.require_historical_axes(job risk.jobs) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog AS $function$
BEGIN
 IF jsonb_typeof(job.request->'historical')='object' AND(
  (SELECT count(*) FROM risk.job_scenario_axes WHERE job_id=job.id)<>jsonb_array_length(job.request#>'{historical,factors}')
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(job.request#>'{historical,factors}')f
   WHERE NOT EXISTS(SELECT 1 FROM risk.job_scenario_axes a WHERE a.job_id=job.id AND a.factor=jsonb_strip_nulls(f))))
 THEN RAISE EXCEPTION 'risk_historical_input';END IF;
END
$function$;
ALTER FUNCTION risk.require_historical_axes(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.require_historical_axes(risk.jobs) FROM PUBLIC;
CREATE OR REPLACE FUNCTION risk.retain_axes(job risk.jobs, axes jsonb)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE axis jsonb;factor jsonb;target jsonb;point jsonb;command jsonb;group_name text;selected_axis_id text;market_revision uuid;manifest text;
 curve_catalog uuid;curve_revision uuid;curve_code text;contract_catalog uuid;contract_revision uuid;contract_id uuid;
 bucket_from date;bucket_to date;bucket_convention text;amount numeric;commands jsonb;
BEGIN
 commands=risk.axis_commands(job.request);
 IF jsonb_typeof(axes) IS DISTINCT FROM 'array' OR jsonb_array_length(axes) NOT BETWEEN 1 AND 4112
  OR jsonb_typeof(commands->'bumps') IS DISTINCT FROM 'array' OR jsonb_array_length(commands->'bumps') NOT BETWEEN (CASE WHEN jsonb_typeof(job.request->'joint')='object' OR jsonb_typeof(job.request->'historical')='object' THEN 0 ELSE 1 END) AND 4096
  OR jsonb_typeof(commands->'stress') IS DISTINCT FROM 'array' OR jsonb_array_length(commands->'stress')>(CASE WHEN jsonb_typeof(job.request->'joint')='object' THEN 528 ELSE 16 END)
 THEN RAISE EXCEPTION 'risk_axis_input';END IF;
 FOR axis IN SELECT value FROM jsonb_array_elements(axes) LOOP
  factor=jsonb_strip_nulls(axis->'factor');target=factor->'target';selected_axis_id=axis->>'id';market_revision=(axis->>'marketRevisionId')::uuid;
  IF jsonb_typeof(axis) IS DISTINCT FROM 'object' OR coalesce(selected_axis_id,'') !~ '^[0-9a-f]{64}$'
   OR jsonb_typeof(factor) IS DISTINCT FROM 'object' OR jsonb_typeof(target) IS DISTINCT FROM 'object'
   OR factor->>'coordinate' IS NULL OR factor->>'coordinate' NOT IN('market_quote','computed_output')
   OR target->>'kind' IS NULL OR target->>'kind' NOT IN('contract','points','parallel','delivery_bucket')
   OR jsonb_typeof(axis->'points') IS DISTINCT FROM 'array' OR jsonb_array_length(axis->'points') NOT BETWEEN 1 AND 100000
   OR factor-ARRAY['coordinate','target']<>'{}'::jsonb
   OR target-ARRAY['kind','curve','contract','points','from','to','convention']<>'{}'::jsonb
   OR jsonb_typeof(target->'curve') IS DISTINCT FROM 'object' OR jsonb_typeof(target#>'{curve,revision}') IS DISTINCT FROM 'object'
  THEN RAISE EXCEPTION 'risk_axis_input';END IF;
  SELECT m.manifest_id INTO manifest FROM risk.job_connected_markets m WHERE(m.job_id,m.revision_id)=(job.id,market_revision);
  IF NOT FOUND THEN RAISE EXCEPTION 'risk_axis_input';END IF;
  curve_catalog=(target#>>'{curve,revision,catalogId}')::uuid;curve_revision=(target#>>'{curve,revision,revisionId}')::uuid;curve_code=target#>>'{curve,code}';
  IF curve_catalog IS DISTINCT FROM job.catalog_id OR curve_revision IS NULL OR curve_revision='00000000-0000-0000-0000-000000000000'::uuid
   OR coalesce(length(curve_code),0) NOT BETWEEN 1 AND 1024 THEN RAISE EXCEPTION 'risk_axis_input';END IF;
  contract_catalog=NULL;contract_revision=NULL;contract_id=NULL;bucket_from=NULL;bucket_to=NULL;bucket_convention=NULL;
  CASE target->>'kind'
   WHEN 'contract' THEN
    IF jsonb_typeof(target->'contract') IS DISTINCT FROM 'object' OR target ?| ARRAY['points','from','to','convention'] THEN RAISE EXCEPTION 'risk_axis_input';END IF;
    contract_catalog=(target#>>'{contract,revision,catalogId}')::uuid;contract_revision=(target#>>'{contract,revision,revisionId}')::uuid;contract_id=(target#>>'{contract,id}')::uuid;
    IF contract_catalog IS DISTINCT FROM job.catalog_id OR contract_revision IS DISTINCT FROM market_revision OR contract_id IS NULL THEN RAISE EXCEPTION 'risk_axis_input';END IF;
   WHEN 'points' THEN
    IF jsonb_typeof(target->'points') IS DISTINCT FROM 'array' OR jsonb_array_length(target->'points') NOT BETWEEN 1 AND 100000
     OR target ?| ARRAY['contract','from','to','convention']
     OR(SELECT count(*)<>count(DISTINCT value::uuid) FROM jsonb_array_elements_text(target->'points'))
    THEN RAISE EXCEPTION 'risk_axis_input';END IF;
   WHEN 'parallel' THEN
    IF target ?| ARRAY['contract','points','from','to','convention'] THEN RAISE EXCEPTION 'risk_axis_input';END IF;
   WHEN 'delivery_bucket' THEN
    bucket_from=(target->>'from')::date;bucket_to=(target->>'to')::date;bucket_convention=target->>'convention';
    IF target ?| ARRAY['contract','points'] OR bucket_from IS NULL OR bucket_to IS NULL OR NOT isfinite(bucket_from) OR NOT isfinite(bucket_to)
     OR bucket_from>=bucket_to OR bucket_convention IS DISTINCT FROM 'whole_point_delivery_contained_half_open_v1'
    THEN RAISE EXCEPTION 'risk_axis_input';END IF;
  END CASE;
  INSERT INTO risk.job_scenario_axes VALUES(job.id,selected_axis_id,market_revision,manifest,factor->>'coordinate',target->>'kind',
   curve_catalog,curve_revision,curve_code,contract_catalog,contract_revision,contract_id,bucket_from,bucket_to,bucket_convention,factor);
  FOR point IN SELECT value FROM jsonb_array_elements(axis->'points') LOOP
   IF jsonb_typeof(point) IS DISTINCT FROM 'object' OR point->'curve' IS DISTINCT FROM target->'curve'
    OR(point->>'pointId')::uuid IS NULL THEN RAISE EXCEPTION 'risk_axis_input';END IF;
   INSERT INTO risk.job_scenario_points VALUES(job.id,selected_axis_id,curve_catalog,curve_revision,curve_code,(point->>'pointId')::uuid);
  END LOOP;
  IF target->>'kind'='points' AND(SELECT array_agg(value::uuid ORDER BY value::uuid) FROM jsonb_array_elements_text(target->'points')) IS DISTINCT FROM
   (SELECT array_agg(p.point_id ORDER BY p.point_id) FROM risk.job_scenario_points p WHERE(p.job_id,p.axis_id)=(job.id,selected_axis_id))
  THEN RAISE EXCEPTION 'risk_axis_input';END IF;
 END LOOP;
 FOREACH group_name IN ARRAY ARRAY['bumps','stress'] LOOP
  FOR command IN SELECT value FROM jsonb_array_elements(commands->group_name) LOOP
   IF jsonb_typeof(command) IS DISTINCT FROM 'object' OR command-ARRAY['factor','size','convention']<>'{}'::jsonb
    OR jsonb_typeof(command->'size') IS DISTINCT FROM 'string' OR jsonb_typeof(command->'factor') IS DISTINCT FROM 'object'
    OR NOT EXISTS(SELECT 1 FROM risk.job_scenario_axes a WHERE a.job_id=job.id AND a.factor=jsonb_strip_nulls(command->'factor'))
   THEN RAISE EXCEPTION 'risk_axis_input';END IF;
   amount=(command->>'size')::numeric;
   IF amount IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) OR abs(amount)>=1e24 OR scale(amount)>18
    OR group_name='bumps' AND(amount<=0 OR command->>'convention' IS DISTINCT FROM 'absolute_curve_coordinate_symmetric_v1')
    OR group_name='stress' AND(amount=0 OR command->>'convention' IS DISTINCT FROM 'absolute_curve_coordinate_parent_additive_v1')
   THEN RAISE EXCEPTION 'risk_axis_input';END IF;
  END LOOP;
 END LOOP;
 IF EXISTS(SELECT 1 FROM risk.job_scenario_axes a WHERE a.job_id=job.id AND NOT EXISTS(
   SELECT 1 FROM jsonb_array_elements((commands->'bumps')||(commands->'stress')||coalesce(commands->'factors','[]'::jsonb)) selected_command(value)
   WHERE jsonb_strip_nulls(selected_command.value->'factor')=a.factor))
  OR(SELECT count(*)<>count(DISTINCT jsonb_strip_nulls(value->'factor')) FROM jsonb_array_elements(commands->'bumps'))
 THEN RAISE EXCEPTION 'risk_axis_input';END IF;
PERFORM risk.require_joint_axes(job);
PERFORM risk.require_historical_axes(job);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 OR check_violation OR foreign_key_violation OR unique_violation OR not_null_violation THEN RAISE EXCEPTION 'risk_axis_input';
END$function$
;
CREATE OR REPLACE FUNCTION risk.validate_parametric_contract(spec jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE policy jsonb=spec->'policy';fd jsonb=spec->'finiteDifference';amount numeric;
BEGIN
 IF policy-ARRAY['convention','numericalPolicy','estimator','centering','drift','horizon','tail','confidence']<>'{}'::jsonb
  OR policy->>'convention' IS DISTINCT FROM 'dated_factor_delta_normal_v1' OR policy->>'numericalPolicy' IS DISTINCT FROM 'exact_moments_binary64_normal_v1'
  OR policy->>'estimator' IS DISTINCT FROM 'equal_weight_sample_n_minus_one_v1' OR policy->>'centering' IS DISTINCT FROM 'sample_mean_centered_v1'
  OR coalesce(policy->>'drift','') NOT IN('zero_drift_v1','sample_mean_drift_v1')
  OR policy->>'horizon' IS DISTINCT FROM 'frozen_portfolio_one_adjacent_session_v1' OR policy->>'tail' IS DISTINCT FROM 'signed_upper_loss_tail_v1'
  OR jsonb_typeof(policy->'confidence') IS DISTINCT FROM 'string' OR coalesce(policy->>'confidence','')!~'^0\.[0-9]{1,18}$' OR(policy->>'confidence')::numeric<=0.5 OR(policy->>'confidence')::numeric>0.9999
  OR fd-ARRAY['convention','tradingTicks','deltaTolerance']<>'{}'::jsonb OR fd->>'convention' IS DISTINCT FROM 'native_tick_central_half_nominal_double_v1'
  OR jsonb_typeof(fd->'tradingTicks') IS DISTINCT FROM 'number' OR coalesce(fd->>'tradingTicks','')!~'^[1-9][0-9]{0,3}$'
  OR(fd->>'tradingTicks')::integer NOT BETWEEN 1 AND 1000 OR fd->>'tradingTicks' IS NULL
  OR jsonb_typeof(fd#>'{deltaTolerance,absolute}') IS DISTINCT FROM 'string' OR jsonb_typeof(fd#>'{deltaTolerance,relative}') IS DISTINCT FROM 'string'
  OR coalesce(fd#>>'{deltaTolerance,absolute}','')!~'^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$'
  OR coalesce(fd#>>'{deltaTolerance,relative}','')!~'^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$'
  OR jsonb_typeof(fd->'deltaTolerance') IS DISTINCT FROM 'object' OR(fd->'deltaTolerance')-ARRAY['absolute','relative']<>'{}'::jsonb
 THEN RAISE EXCEPTION 'risk_parametric_input';END IF;
 amount=(fd#>>'{deltaTolerance,absolute}')::numeric;
 IF amount IS NULL OR amount<0 OR amount>=1e24 OR scale(amount)>18 THEN RAISE EXCEPTION 'risk_parametric_input';END IF;
 amount=(fd#>>'{deltaTolerance,relative}')::numeric;
 IF amount IS NULL OR amount<0 OR amount>=1e24 OR scale(amount)>18 THEN RAISE EXCEPTION 'risk_parametric_input';END IF;
END
$function$;
ALTER FUNCTION risk.validate_parametric_contract(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.validate_parametric_contract(jsonb) FROM PUBLIC;
CREATE OR REPLACE FUNCTION risk.retain_statistical_history(job risk.jobs,spec jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE plan jsonb=job.frame->'factorHistory';forecast jsonb=plan->'forecast';calendar jsonb=plan->'calendar';clock jsonb=plan->'clock';
 ref_catalog uuid;ref_revision uuid;pin uuid;observed record;market_prefix integer;known_at timestamptz;
BEGIN
 IF jsonb_typeof(spec->'history') IS DISTINCT FROM 'object' OR jsonb_typeof(plan) IS DISTINCT FROM 'object'
  OR plan-ARRAY['clockCaptureId','expectedManifestId','forecast','policy','calendar','clock','observations']<>'{}'::jsonb
  OR plan->>'expectedManifestId' IS DISTINCT FROM spec#>>'{history,expectedManifestId}' OR coalesce(plan->>'expectedManifestId','')!~'^[0-9a-f]{64}$'
  OR plan->'policy' IS DISTINCT FROM spec#>'{history,policy}' OR jsonb_typeof(plan->'observations') IS DISTINCT FROM 'array'
  OR jsonb_array_length(plan->'observations') NOT BETWEEN 1 AND 500000
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(plan->'observations'))
 THEN RAISE EXCEPTION 'risk_statistical_history';END IF;
 SELECT m.known_through,t.known_at INTO market_prefix,known_at FROM market_data.market_contexts m
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
  WHERE(m.catalog_id,m.context_id)=(job.catalog_id,job.context_id);
 IF(forecast->>'contextId')::uuid IS DISTINCT FROM job.context_id OR(forecast->>'catalogId')::uuid IS DISTINCT FROM job.catalog_id
  OR(forecast->>'valuationAt')::timestamptz IS DISTINCT FROM job.valuation_at OR forecast->>'marketCutoff' IS NULL OR(forecast->>'marketCutoff')::timestamptz>job.valuation_at
  OR(forecast->>'marketKnownThrough')::integer IS DISTINCT FROM market_prefix OR(forecast->>'knownAt')::timestamptz IS DISTINCT FROM known_at
  OR calendar->'reference' IS DISTINCT FROM spec#>'{history,calendar}' OR(calendar->>'from')::date IS DISTINCT FROM(spec#>>'{history,from}')::date
  OR(calendar->>'to')::date IS DISTINCT FROM(spec#>>'{history,to}')::date OR clock->>'closeTime' IS DISTINCT FROM spec#>>'{history,closeTime}'
  OR NOT coalesce(((clock->>'resolution'='captured_calendar_session_closes_v1' AND plan#>>'{policy,alignment}'='exact_session_close_v1' AND NOT(plan->'policy' ? 'priceTypes'))
  OR(clock->>'resolution'='captured_calendar_day_windows_v1' AND clock->>'closeTime'='end_of_day'
  AND plan#>>'{policy,alignment}'='daily_latest_visible_session_end_v1'
  AND plan#>>'{policy,regimes}'='ordered_price_types_fixed_series_source_unit_contract_v1'
  AND jsonb_typeof(plan#>'{policy,priceTypes}')='array' AND jsonb_array_length(plan#>'{policy,priceTypes}') BETWEEN 1 AND 7)),false) OR clock->>'ambiguousTime' IS DISTINCT FROM 'reject' OR clock->>'invalidTime' IS DISTINCT FROM 'reject'
 THEN RAISE EXCEPTION 'risk_parametric_input';END IF;
 IF NOT EXISTS(SELECT 1 FROM risk.history_clock_captures c
  WHERE(c.catalog_id,c.id,c.context_id,c.calendar,c.clock)=(job.catalog_id,(plan->>'clockCaptureId')::uuid,job.context_id,plan->'calendar',plan->'clock'))
 THEN RAISE EXCEPTION 'risk_history_capture';END IF;
 ref_catalog=(calendar#>>'{reference,revision,catalogId}')::uuid;ref_revision=(calendar#>>'{reference,revision,revisionId}')::uuid;
 IF clock#>'{reference,revision}' IS DISTINCT FROM calendar#>'{reference,revision}' OR NOT security.reference_access(ref_catalog,'ReadMarketData')
  OR NOT security.reference_version_visible(ref_catalog,ref_revision) OR NOT EXISTS(
   SELECT 1 FROM market_data.revisions r WHERE r.catalog_id=job.catalog_id AND(r.reference_catalog_id,r.reference_revision_id)=(ref_catalog,ref_revision)
    AND(r.id=job.revision_id OR EXISTS(SELECT 1 FROM risk.job_connected_markets m WHERE(m.catalog_id,m.job_id,m.revision_id)=(job.catalog_id,job.id,r.id))))
  OR NOT EXISTS(SELECT 1 FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code,c.clock_code)=(ref_catalog,ref_revision,calendar#>>'{reference,code}',clock#>>'{reference,code}'))
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 INSERT INTO risk.job_history VALUES(job.catalog_id,job.id,(plan->>'clockCaptureId')::uuid,plan->>'expectedManifestId',ref_catalog,ref_revision,calendar#>>'{reference,code}',clock#>>'{reference,code}');
 FOR pin IN SELECT value::uuid FROM jsonb_array_elements_text(plan->'observations') LOOP
  SELECT o.definition_revision_id,e.event_order,k.known_from,o.observed_at INTO observed
  FROM market_data.observations o JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE(o.catalog_id,o.id)=(job.catalog_id,pin);
  IF NOT FOUND OR observed.event_order>market_prefix OR observed.known_from>known_at OR observed.observed_at>(forecast->>'marketCutoff')::timestamptz
   OR NOT security.market_fact_visible(job.catalog_id,observed.definition_revision_id,pin)
   OR NOT EXISTS(SELECT 1 FROM risk.job_connected_markets m WHERE(m.catalog_id,m.job_id,m.revision_id)=(job.catalog_id,job.id,observed.definition_revision_id))
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  INSERT INTO risk.job_history_observations VALUES(job.catalog_id,job.id,observed.definition_revision_id,pin);
 END LOOP;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'risk_parametric_input';
END
$function$;
ALTER FUNCTION risk.retain_statistical_history(risk.jobs,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_statistical_history(risk.jobs,jsonb) FROM PUBLIC;
CREATE OR REPLACE FUNCTION risk.retain_historical(job risk.jobs) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE spec jsonb=job.request->'historical';policy jsonb=spec->'policy';comparison jsonb=NULLIF(spec->'comparison','null'::jsonb);
BEGIN
 IF jsonb_typeof(spec) IS DISTINCT FROM 'object' OR spec-ARRAY['history','policy','comparison','factors']<>'{}'::jsonb
  OR NULLIF(job.request->'parametric','null'::jsonb) IS NOT NULL OR NULLIF(job.request->'joint','null'::jsonb) IS NOT NULL
  OR job.frame ? 'run' OR job.request->>'detail' IS DISTINCT FROM 'holdings'
  OR job.request->'bumps' IS DISTINCT FROM '[]'::jsonb OR job.request->'stress' IS DISTINCT FROM '[]'::jsonb
  OR jsonb_typeof(policy) IS DISTINCT FROM 'object'
  OR policy-ARRAY['convention','numericalPolicy','horizon','samplePolicy','quantilePolicy','tailPolicy','valuationPolicy','confidence']<>'{}'::jsonb
  OR policy->>'convention' IS DISTINCT FROM 'dated_factor_full_pv_historical_v1'
  OR policy->>'numericalPolicy' IS DISTINCT FROM 'exact_empirical_probability_mass_v1'
  OR policy->>'horizon' IS DISTINCT FROM 'frozen_portfolio_one_adjacent_session_v1'
  OR policy->>'samplePolicy' IS DISTINCT FROM 'equal_weight_raw_joint_changes_v1'
  OR policy->>'quantilePolicy' IS DISTINCT FROM 'empirical_inverse_cdf_v1'
  OR policy->>'tailPolicy' IS DISTINCT FROM 'signed_upper_loss_fractional_mass_uniform_ties_v1'
  OR policy->>'valuationPolicy' IS DISTINCT FROM 'current_instrument_pv_frozen_nonfactor_inputs_v1'
  OR jsonb_typeof(policy->'confidence') IS DISTINCT FROM 'string'
  OR coalesce(policy->>'confidence','')!~'^0\.[0-9]{1,18}$' OR(policy->>'confidence')::numeric<=0.5 OR(policy->>'confidence')::numeric>0.9999
 THEN RAISE EXCEPTION 'risk_historical_input';END IF;
 PERFORM risk.axis_commands(job.request);PERFORM risk.require_historical_axes(job);
 IF comparison IS NOT NULL THEN
  IF jsonb_typeof(comparison) IS DISTINCT FROM 'object' OR comparison-ARRAY['policy','finiteDifference']<>'{}'::jsonb
   OR comparison#>>'{policy,confidence}' IS DISTINCT FROM policy->>'confidence'
   OR comparison#>>'{policy,horizon}' IS DISTINCT FROM policy->>'horizon'
   OR comparison#>>'{policy,drift}' IS DISTINCT FROM 'sample_mean_drift_v1'
  THEN RAISE EXCEPTION 'risk_historical_comparison';END IF;
  PERFORM risk.validate_parametric_contract(comparison);
 END IF;
 PERFORM risk.retain_statistical_history(job,spec);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'risk_historical_input';
END
$function$;
ALTER FUNCTION risk.retain_historical(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_historical(risk.jobs) FROM PUBLIC;
CREATE OR REPLACE FUNCTION risk.retain_parametric(job risk.jobs)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE spec jsonb=NULLIF(job.request->'parametric','null'::jsonb);plan jsonb=job.frame->'factorHistory';
BEGIN
 IF NULLIF(job.request->'historical','null'::jsonb) IS NOT NULL THEN PERFORM risk.retain_historical(job);RETURN;END IF;
 IF spec IS NULL THEN IF plan IS NOT NULL THEN RAISE EXCEPTION 'risk_parametric_input';END IF;RETURN;END IF;
 IF jsonb_typeof(spec) IS DISTINCT FROM 'object' OR spec-ARRAY['history','policy','finiteDifference']<>'{}'::jsonb
  OR jsonb_typeof(plan) IS DISTINCT FROM 'object' OR plan-ARRAY['clockCaptureId','expectedManifestId','forecast','policy','calendar','clock','observations']<>'{}'::jsonb
  OR job.frame ? 'run' OR NULLIF(job.request->'joint','null'::jsonb) IS NOT NULL OR job.request->'stress' IS DISTINCT FROM '[]'::jsonb
  OR job.request->>'detail' IS DISTINCT FROM 'books' OR jsonb_array_length(job.request->'bumps') NOT BETWEEN 1 AND 128
  OR plan->>'expectedManifestId' IS DISTINCT FROM spec#>>'{history,expectedManifestId}' OR coalesce(plan->>'expectedManifestId','')!~'^[0-9a-f]{64}$'
  OR plan->'policy' IS DISTINCT FROM spec#>'{history,policy}' OR jsonb_typeof(plan->'observations') IS DISTINCT FROM 'array'
  OR jsonb_array_length(plan->'observations') NOT BETWEEN 1 AND 500000
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(plan->'observations'))
 THEN RAISE EXCEPTION 'risk_parametric_input';END IF;
 PERFORM risk.validate_parametric_contract(spec);
 PERFORM risk.retain_statistical_history(job,spec);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'risk_parametric_input';
END
$function$;
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
  'calibrationAvailable',job.result#>'{historical,result,available}') ELSE '{}'::jsonb END||
 CASE WHEN jsonb_typeof(job.request->'joint')='object' THEN jsonb_build_object('kind',CASE WHEN jsonb_typeof(job.request#>'{joint,scenarios}')='object' THEN 'scenarios' ELSE 'derivatives' END,
  'scenarioCount',coalesce(jsonb_array_length(job.request#>'{joint,scenarios,scenarios}'),0),
  'derivativeCount',coalesce(2*jsonb_array_length(job.request#>'{joint,derivatives,axes}')+jsonb_array_length(job.request#>'{joint,derivatives,mixed}'),0)) ELSE '{}'::jsonb END;
$function$
;
CREATE OR REPLACE FUNCTION risk.archive_historical(catalog uuid, take integer, prior_created timestamp with time zone, prior_id uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;rows jsonb;items jsonb;cursor jsonb=NULL;entry jsonb;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 IF take IS NULL OR take NOT BETWEEN 1 AND 100 OR(prior_created IS NULL)<>(prior_id IS NULL)
  OR prior_created IS NOT NULL AND NOT isfinite(prior_created) THEN RAISE EXCEPTION 'risk_archive_input';END IF;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT coalesce(jsonb_agg(risk.summary(j) ORDER BY j.created_at DESC,j.id DESC),'[]'::jsonb) INTO rows FROM(
  SELECT * FROM risk.jobs j WHERE j.catalog_id=catalog AND(j.actor_id,j.tenant_id,j.workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
   AND j.state='completed' AND jsonb_typeof(j.request->'historical')='object' AND risk.authorized(j)
   AND(prior_created IS NULL OR(j.created_at,j.id)<(prior_created,prior_id)) ORDER BY j.created_at DESC,j.id DESC LIMIT take+1
 )j;
 SELECT coalesce(jsonb_agg(value ORDER BY ordinal),'[]'::jsonb) INTO items FROM jsonb_array_elements(rows) WITH ORDINALITY a(value,ordinal) WHERE ordinal<=take;
 FOR entry IN SELECT value FROM jsonb_array_elements(items) LOOP PERFORM risk.owner((entry->>'jobId')::uuid);END LOOP;
 IF jsonb_array_length(rows)>take THEN cursor=jsonb_build_object('createdAt',rows->(take-1)->'createdAt','id',rows->(take-1)->'jobId');END IF;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('items',items,'next',cursor);
END$function$
;
ALTER FUNCTION risk.archive_historical(uuid,integer,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.archive_historical(uuid,integer,timestamptz,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.archive_historical(uuid,integer,timestamptz,uuid) TO openetrm_runtime;
CREATE OR REPLACE FUNCTION risk.archive_statistical(catalog uuid, take integer, prior_created timestamp with time zone, prior_id uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;rows jsonb;items jsonb;cursor jsonb=NULL;entry jsonb;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 IF take IS NULL OR take NOT BETWEEN 1 AND 100 OR(prior_created IS NULL)<>(prior_id IS NULL)
  OR prior_created IS NOT NULL AND NOT isfinite(prior_created) THEN RAISE EXCEPTION 'risk_archive_input';END IF;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT coalesce(jsonb_agg(risk.summary(j) ORDER BY j.created_at DESC,j.id DESC),'[]'::jsonb) INTO rows FROM(
  SELECT * FROM risk.jobs j WHERE j.catalog_id=catalog AND(j.actor_id,j.tenant_id,j.workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
   AND j.state='completed' AND (jsonb_typeof(j.request->'parametric')='object' OR jsonb_typeof(j.request->'historical')='object') AND risk.authorized(j)
   AND(prior_created IS NULL OR(j.created_at,j.id)<(prior_created,prior_id)) ORDER BY j.created_at DESC,j.id DESC LIMIT take+1
 )j;
 SELECT coalesce(jsonb_agg(value ORDER BY ordinal),'[]'::jsonb) INTO items FROM jsonb_array_elements(rows) WITH ORDINALITY a(value,ordinal) WHERE ordinal<=take;
 FOR entry IN SELECT value FROM jsonb_array_elements(items) LOOP PERFORM risk.owner((entry->>'jobId')::uuid);END LOOP;
 IF jsonb_array_length(rows)>take THEN cursor=jsonb_build_object('createdAt',rows->(take-1)->'createdAt','id',rows->(take-1)->'jobId');END IF;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('items',items,'next',cursor);
END$function$
;
ALTER FUNCTION risk.archive_statistical(uuid,integer,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.archive_statistical(uuid,integer,timestamptz,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.archive_statistical(uuid,integer,timestamptz,uuid) TO openetrm_runtime;
CREATE OR REPLACE FUNCTION risk.guard_run()
 RETURNS trigger
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE run jsonb=NEW.frame->'run';
BEGIN
 IF (jsonb_typeof(NEW.request->'joint')='object' OR jsonb_typeof(NEW.request->'historical')='object') AND NEW.frame ? 'run' THEN RAISE EXCEPTION 'risk_job_run';END IF;
 IF NOT NEW.frame ? 'run' THEN RETURN NEW;END IF;
 IF jsonb_typeof(run) IS DISTINCT FROM 'object'
  OR(SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(run) key) IS DISTINCT FROM ARRAY['id','part','parts']
  OR jsonb_typeof(run->'id') IS DISTINCT FROM 'string' OR run->>'id'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  OR jsonb_typeof(run->'part') IS DISTINCT FROM 'number' OR jsonb_typeof(run->'parts') IS DISTINCT FROM 'number'
  OR run->>'part'!~'^[1-9][0-9]{0,2}$' OR run->>'parts'!~'^[1-9][0-9]{0,2}$'
  OR(run->>'part')::integer>(run->>'parts')::integer OR(run->>'parts')::integer>256
 THEN RAISE EXCEPTION 'risk_job_run';END IF;
 -- The parts of one run are admitted one at a time, so that two sent at once are checked against each other.
 PERFORM pg_advisory_xact_lock(hashtextextended(NEW.tenant_id::text||NEW.workspace_id::text||NEW.actor_id::text||(run->>'id'),730849137));
 IF EXISTS(SELECT 1 FROM risk.jobs j WHERE(j.tenant_id,j.workspace_id,j.actor_id)=(NEW.tenant_id,NEW.workspace_id,NEW.actor_id)
  AND j.frame ? 'run' AND j.frame#>>'{run,id}'=run->>'id'
  AND(j.catalog_id<>NEW.catalog_id OR j.context_id<>NEW.context_id OR j.frame->>'id' IS DISTINCT FROM NEW.frame->>'id'
   OR j.frame->'scope' IS DISTINCT FROM NEW.frame->'scope'
   OR j.frame#>'{run,parts}'<>run->'parts' OR j.frame#>'{run,part}'=run->'part'))
 THEN RAISE EXCEPTION 'risk_job_run';END IF;
 RETURN NEW;
END$function$
;
COMMIT;
