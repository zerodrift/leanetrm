-- Typed joint operations share the retained portfolio/worker envelope. Their
-- samples are one complete run, never independent sensitivity multipart jobs.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE FUNCTION risk.axis_commands(request jsonb) RETURNS jsonb
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE joint jsonb=NULLIF(request->'joint','null'::jsonb);named jsonb;derivatives jsonb;sample jsonb;axis jsonb;pair jsonb;tolerance jsonb;field text;amount numeric;bumps jsonb;shifts jsonb;
BEGIN
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
END$$;
ALTER FUNCTION risk.axis_commands(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.axis_commands(jsonb) FROM PUBLIC;

CREATE FUNCTION risk.require_joint_axes(job risk.jobs) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE pair jsonb;first_factor jsonb;second_factor jsonb;
BEGIN
 FOR pair IN SELECT value FROM jsonb_array_elements(coalesce(job.request#>'{joint,derivatives,mixed}','[]'::jsonb)) LOOP
  SELECT jsonb_strip_nulls(a#>'{bump,factor}') INTO first_factor FROM jsonb_array_elements(job.request#>'{joint,derivatives,axes}') a WHERE a->>'id'=pair->>'firstAxis';
  SELECT jsonb_strip_nulls(a#>'{bump,factor}') INTO second_factor FROM jsonb_array_elements(job.request#>'{joint,derivatives,axes}') a WHERE a->>'id'=pair->>'secondAxis';
  IF EXISTS(SELECT 1 FROM risk.job_scenario_axes a JOIN risk.job_scenario_points p ON(p.job_id,p.axis_id)=(a.job_id,a.axis_id)
   JOIN risk.job_scenario_axes b ON b.job_id=a.job_id AND b.factor=second_factor
   JOIN risk.job_scenario_points q ON(q.job_id,q.axis_id,q.curve_catalog_id,q.curve_revision_id,q.curve_code,q.point_id)=
    (b.job_id,b.axis_id,p.curve_catalog_id,p.curve_revision_id,p.curve_code,p.point_id)
   WHERE a.job_id=job.id AND a.factor=first_factor) THEN RAISE EXCEPTION 'risk_joint_input';END IF;
 END LOOP;
END$$;
ALTER FUNCTION risk.require_joint_axes(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.require_joint_axes(risk.jobs) FROM PUBLIC;

-- Keep the proven exact target/FK/current-authority kernel. Only its command
-- source changes; the retained request and original financial payload stay intact.
DO $patch$ DECLARE definition text;BEGIN
 definition=pg_get_functiondef('risk.retain_axes(risk.jobs,jsonb)'::regprocedure);
 IF position('bucket_convention text;amount numeric;' IN definition)=0 OR position(E'BEGIN\n IF jsonb_typeof(axes)' IN definition)=0
  OR position('job.request->group_name' IN definition)=0 OR position('jsonb_array_length(job.request->''bumps'') NOT BETWEEN 1 AND 4096' IN definition)=0
 THEN RAISE EXCEPTION 'joint_risk_requires_known_axis_kernel';END IF;
 definition=replace(definition,'bucket_convention text;amount numeric;','bucket_convention text;amount numeric;commands jsonb;');
 definition=replace(definition,E'BEGIN\n IF jsonb_typeof(axes)',E'BEGIN\n commands=risk.axis_commands(job.request);\n IF jsonb_typeof(axes)');
 definition=replace(definition,'job.request->''bumps''','commands->''bumps''');
 definition=replace(definition,'job.request->''stress''','commands->''stress''');
 definition=replace(definition,'job.request->group_name','commands->group_name');
 definition=replace(definition,'jsonb_array_length(commands->''bumps'') NOT BETWEEN 1 AND 4096',
  'jsonb_array_length(commands->''bumps'') NOT BETWEEN (CASE WHEN jsonb_typeof(job.request->''joint'')=''object'' THEN 0 ELSE 1 END) AND 4096');
 definition=replace(definition,'jsonb_array_length(commands->''stress'')>16',
  'jsonb_array_length(commands->''stress'')>(CASE WHEN jsonb_typeof(job.request->''joint'')=''object'' THEN 528 ELSE 16 END)');
 definition=replace(definition,'EXCEPTION WHEN invalid_text_representation',E'PERFORM risk.require_joint_axes(job);\nEXCEPTION WHEN invalid_text_representation');
 EXECUTE definition;
 definition=pg_get_functiondef('risk.guard_run()'::regprocedure);
 definition=replace(definition,E'BEGIN\n IF NOT NEW.frame',E'BEGIN\n IF jsonb_typeof(NEW.request->''joint'')=''object'' AND NEW.frame ? ''run'' THEN RAISE EXCEPTION ''risk_job_run'';END IF;\n IF NOT NEW.frame');
 EXECUTE definition;
END $patch$;

CREATE OR REPLACE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb LANGUAGE sql SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('jobId',job.id,'requestId',job.request_id,'state',job.state,'contextId',job.context_id,
  'valuationAt',job.valuation_at,'createdAt',job.created_at,'startedAt',job.started_at,'finishedAt',job.finished_at,
  'attempt',job.attempt,'errorCode',job.error_code,'engineVersion',job.request->>'engineVersion','convention',job.request->>'convention',
  'run',job.frame->'run','scope',job.frame->'scope','shocks',jsonb_array_length(job.request->'bumps'))||
 CASE WHEN jsonb_typeof(job.request->'joint')='object' THEN jsonb_build_object('kind',CASE WHEN jsonb_typeof(job.request#>'{joint,scenarios}')='object' THEN 'scenarios' ELSE 'derivatives' END,
  'scenarioCount',coalesce(jsonb_array_length(job.request#>'{joint,scenarios,scenarios}'),0),
  'derivativeCount',coalesce(2*jsonb_array_length(job.request#>'{joint,derivatives,axes}')+jsonb_array_length(job.request#>'{joint,derivatives,mixed}'),0)) ELSE '{}'::jsonb END;
$$;
COMMIT;
