-- Named scenarios retain their exact declared volatility inputs beside their curve shifts.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $patch$
DECLARE p pg_proc;signature text;digest text;definition text;needle text;replacement text;
BEGIN
 FOR signature,digest IN VALUES
  ('risk.axis_commands_pre_economic_lifecycle(jsonb)','c7b13a6455a1ca7a651d93f82e3810ff4a4f57cf176160e311082448b30c4b02'),
  ('risk.retain_axes(risk.jobs,jsonb)','0578d552f9cb68c277c17b2ee44ba09c6095e92b0f73c35ad7a01db9922a48df')
 LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=signature::regprocedure;
  definition=pg_get_functiondef(p.oid);
  IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>digest OR p.proowner<>'openetrm_security_owner'::regrole
   OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.provolatile<>'v' OR p.proisstrict OR p.proleakproof OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'joint_volatility_upgrade: unexpected helper %',signature;END IF;
  IF signature='risk.axis_commands_pre_economic_lifecycle(jsonb)' THEN
   definition=replace(definition,$old$sample-ARRAY['id','label','shifts']$old$,$new$sample-ARRAY['id','label','shifts','volatility']$new$);
   definition=replace(definition,$old$jsonb_array_length(sample->'shifts') NOT BETWEEN 1 AND 16$old$,
    $new$jsonb_array_length(sample->'shifts')+coalesce(jsonb_array_length(sample->'volatility'),0) NOT BETWEEN 1 AND 16$new$);
   needle=$old$FOR sample IN SELECT value FROM jsonb_array_elements(named->'scenarios') LOOP$old$;
   replacement=needle||$new$
   IF sample ? 'volatility' THEN
    IF jsonb_typeof(sample->'volatility') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'risk_joint_input';END IF;
    IF jsonb_array_length(sample->'volatility')>16
     OR EXISTS(SELECT 1 FROM jsonb_array_elements(sample->'volatility') v WHERE jsonb_typeof(v)<>'object')
    THEN RAISE EXCEPTION 'risk_joint_input';END IF;
    IF EXISTS(SELECT 1 FROM jsonb_array_elements(sample->'volatility') v WHERE v-ARRAY['inputId','size','convention']<>'{}'::jsonb
      OR jsonb_typeof(v->'inputId') IS DISTINCT FROM 'string'
      OR v->>'inputId' !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
      OR (v->>'inputId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
      OR v->>'convention' IS DISTINCT FROM 'absolute_annualized_volatility_parent_additive_v1'
      OR jsonb_typeof(v->'size') IS DISTINCT FROM 'string'
      OR v->>'size' !~ '^-?(0|[1-9][0-9]{0,23})(\.[0-9]{1,18})?$' OR (v->>'size')::numeric=0)
     OR (SELECT count(*)<>count(DISTINCT (v->>'inputId')::uuid) FROM jsonb_array_elements(sample->'volatility') v)
    THEN RAISE EXCEPTION 'risk_joint_input';END IF;
   END IF;$new$;
  ELSE
   -- Structural validation precedes this check. A purely volatility-driven request has no curve axes to retain.
   needle=$old$job.request#>>'{economic,policy,implementation}'='economic_lifecycle_no_variable_factors_v1' THEN 0$old$;
   replacement=$new$job.request#>>'{economic,policy,implementation}'='economic_lifecycle_no_variable_factors_v1'
     OR jsonb_typeof(job.request#>'{joint,scenarios}')='object'
      AND commands->'bumps'='[]'::jsonb AND commands->'stress'='[]'::jsonb THEN 0$new$;
  END IF;
  IF position(needle IN definition)=0 THEN RAISE EXCEPTION 'joint_volatility_upgrade: missing clause %',signature;END IF;
  EXECUTE replace(definition,needle,replacement);
 END LOOP;
END$patch$;
COMMIT;
