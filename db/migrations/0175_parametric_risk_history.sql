-- Statistical calibration has its own immutable source pins. Historical marks
-- never become current PV observation selections or broaden the worker's reads.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

-- A trusted server issuer attests the actual captured rules and resolved closes.
-- The runtime role may reference this receipt, but cannot author or modify it.
CREATE TABLE risk.history_clock_captures(
 catalog_id uuid NOT NULL,id uuid NOT NULL,context_id uuid NOT NULL,digest text NOT NULL CHECK(digest~'^[0-9a-f]{64}$'),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,calendar_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 calendar jsonb NOT NULL,clock jsonb NOT NULL,created_at timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,context_id,digest),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code)
);
CREATE TABLE risk.job_history(
 catalog_id uuid NOT NULL,job_id uuid PRIMARY KEY,clock_capture_id uuid NOT NULL,manifest_id text NOT NULL CHECK(manifest_id~'^[0-9a-f]{64}$'),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,calendar_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 FOREIGN KEY(catalog_id,clock_capture_id) REFERENCES risk.history_clock_captures(catalog_id,id),
 UNIQUE(catalog_id,job_id),FOREIGN KEY(catalog_id,job_id) REFERENCES risk.jobs(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code)
);
CREATE TABLE risk.job_history_observations(
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,observation_id uuid NOT NULL,
 PRIMARY KEY(job_id,observation_id),FOREIGN KEY(catalog_id,job_id) REFERENCES risk.job_history(catalog_id,job_id),
 FOREIGN KEY(catalog_id,observation_id,revision_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['history_clock_captures','job_history','job_history_observations'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_history ON risk.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON risk.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_history BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE FUNCTION risk.capture_history_clock(catalog uuid,context uuid,calendar jsonb,clock jsonb) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE fingerprint text;capture uuid;ref_catalog uuid;ref_revision uuid;
BEGIN
 IF NOT pg_has_role(session_user,'openetrm_authenticator','MEMBER') OR pg_has_role(session_user,'openetrm_runtime','MEMBER')
  OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RAISE EXCEPTION 'risk_capture_issuer' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(calendar) IS DISTINCT FROM 'object' OR jsonb_typeof(clock) IS DISTINCT FROM 'object'
  OR pg_column_size(calendar)+pg_column_size(clock)>1000000 OR clock->>'resolution' IS DISTINCT FROM 'captured_calendar_session_closes_v1'
  OR jsonb_typeof(calendar->'businessDates') IS DISTINCT FROM 'array' OR jsonb_array_length(calendar->'businessDates') NOT BETWEEN 2 AND 2049
  OR jsonb_typeof(clock->'resolvedCloses') IS DISTINCT FROM 'array' OR jsonb_array_length(clock->'resolvedCloses')<>jsonb_array_length(calendar->'businessDates')
  OR clock#>'{reference,revision}' IS DISTINCT FROM calendar#>'{reference,revision}'
  OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context))
 THEN RAISE EXCEPTION 'risk_capture_input';END IF;
 ref_catalog=(calendar#>>'{reference,revision,catalogId}')::uuid;ref_revision=(calendar#>>'{reference,revision,revisionId}')::uuid;
 IF NOT EXISTS(SELECT 1 FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code,c.clock_code)=
  (ref_catalog,ref_revision,calendar#>>'{reference,code}',clock#>>'{reference,code}')) THEN RAISE EXCEPTION 'risk_capture_input';END IF;
 fingerprint=encode(sha256(convert_to(context::text||':'||calendar::text||':'||clock::text,'UTF8')),'hex');
 PERFORM pg_advisory_xact_lock(hashtextextended(catalog::text,730849173));
 SELECT c.id INTO capture FROM risk.history_clock_captures c WHERE(c.catalog_id,c.context_id,c.digest)=(catalog,context,fingerprint);
 IF FOUND THEN RETURN capture;END IF;
 -- Failed submissions reuse the same receipt; new unreferenced captures are
 -- bounded globally and within a forecast context, without deleting in-flight input.
 IF(SELECT count(*) FROM risk.history_clock_captures c WHERE c.catalog_id=catalog AND NOT EXISTS(
   SELECT 1 FROM risk.job_history h WHERE(h.catalog_id,h.clock_capture_id)=(c.catalog_id,c.id)))>=128
  OR(SELECT count(*) FROM risk.history_clock_captures c WHERE(c.catalog_id,c.context_id)=(catalog,context) AND NOT EXISTS(
   SELECT 1 FROM risk.job_history h WHERE(h.catalog_id,h.clock_capture_id)=(c.catalog_id,c.id)))>=16
 THEN RAISE EXCEPTION 'risk_capture_capacity';END IF;
 capture=gen_random_uuid();
 INSERT INTO risk.history_clock_captures VALUES(catalog,capture,context,fingerprint,ref_catalog,ref_revision,
  calendar#>>'{reference,code}',clock#>>'{reference,code}',calendar,clock,clock_timestamp());
 RETURN capture;
END$$;
ALTER FUNCTION risk.capture_history_clock(uuid,uuid,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.capture_history_clock(uuid,uuid,jsonb,jsonb) FROM PUBLIC;
GRANT USAGE ON SCHEMA risk TO openetrm_authenticator;
GRANT EXECUTE ON FUNCTION risk.capture_history_clock(uuid,uuid,jsonb,jsonb) TO openetrm_authenticator;

CREATE FUNCTION risk.retain_parametric(job risk.jobs) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE spec jsonb=NULLIF(job.request->'parametric','null'::jsonb);plan jsonb=job.frame->'factorHistory';forecast jsonb;policy jsonb;fd jsonb;
 calendar jsonb;clock jsonb;ref_catalog uuid;ref_revision uuid;pin uuid;observed record;market_prefix integer;known_at timestamptz;amount numeric;
BEGIN
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
 policy=spec->'policy';fd=spec->'finiteDifference';forecast=plan->'forecast';calendar=plan->'calendar';clock=plan->'clock';
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
 SELECT m.known_through,t.known_at INTO market_prefix,known_at FROM market_data.market_contexts m
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
  WHERE(m.catalog_id,m.context_id)=(job.catalog_id,job.context_id);
 IF(forecast->>'contextId')::uuid IS DISTINCT FROM job.context_id OR(forecast->>'catalogId')::uuid IS DISTINCT FROM job.catalog_id
  OR(forecast->>'valuationAt')::timestamptz IS DISTINCT FROM job.valuation_at OR forecast->>'marketCutoff' IS NULL OR(forecast->>'marketCutoff')::timestamptz>job.valuation_at
  OR(forecast->>'marketKnownThrough')::integer IS DISTINCT FROM market_prefix OR(forecast->>'knownAt')::timestamptz IS DISTINCT FROM known_at
  OR calendar->'reference' IS DISTINCT FROM spec#>'{history,calendar}' OR(calendar->>'from')::date IS DISTINCT FROM(spec#>>'{history,from}')::date
  OR(calendar->>'to')::date IS DISTINCT FROM(spec#>>'{history,to}')::date OR clock->>'closeTime' IS DISTINCT FROM spec#>>'{history,closeTime}'
  OR clock->>'resolution' IS DISTINCT FROM 'captured_calendar_session_closes_v1' OR clock->>'ambiguousTime' IS DISTINCT FROM 'reject' OR clock->>'invalidTime' IS DISTINCT FROM 'reject'
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
END$$;
ALTER FUNCTION risk.retain_parametric(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_parametric(risk.jobs) FROM PUBLIC;

-- The existing submit transaction owns the whole immutable envelope and every
-- pin. Its idempotency, axis/graph admission and publication fences remain intact.
DO $$DECLARE definition text;changed text;BEGIN
 definition=pg_get_functiondef('risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure);
 changed=replace(definition,'PERFORM risk.retain_axes(job,frame->''scenarioAxes'');',
  E'PERFORM risk.retain_axes(job,frame->''scenarioAxes'');\n PERFORM risk.retain_parametric(job);');
 IF changed=definition THEN RAISE EXCEPTION 'parametric_requires_known_submit_kernel';END IF;EXECUTE changed;
END$$;

CREATE FUNCTION risk.history_attempt(job_id uuid,token uuid) RETURNS risk.jobs
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;header risk.job_history;
BEGIN
 job=risk.owned_attempt(job_id,token,false,false);
 SELECT h.* INTO header FROM risk.job_history h WHERE(h.catalog_id,h.job_id)=(job.catalog_id,job.id);
 IF header.job_id IS NULL OR NOT EXISTS(SELECT 1 FROM risk.history_clock_captures c WHERE(c.catalog_id,c.id,c.context_id,c.calendar,c.clock)=
  (job.catalog_id,header.clock_capture_id,job.context_id,job.frame#>'{factorHistory,calendar}',job.frame#>'{factorHistory,clock}'))
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts c
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
   AND c.expires_at>clock_timestamp() AND(c.actor_id,c.tenant_id,c.workspace_id,c.market_catalog_id,c.market_context_id,c.trading_as_of)=
    (job.actor_id,job.tenant_id,job.workspace_id,job.catalog_id,job.context_id,job.valuation_at))
  OR NOT security.reference_access(header.reference_catalog_id,'ReadMarketData')
  OR NOT security.reference_version_visible(header.reference_catalog_id,header.reference_revision_id)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN job;
END$$;
ALTER FUNCTION risk.history_attempt(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.history_attempt(uuid,uuid) FROM PUBLIC;

CREATE FUNCTION risk.history_inputs(job_id uuid,token uuid) RETURNS SETOF jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;
BEGIN
 job=risk.history_attempt(job_id,token);
 RETURN QUERY SELECT to_jsonb(t) FROM(
  SELECT o.id,o.definition_revision_id,o.series_id,o.source_code,o.role,o.publication_state,o.observed_at,o.value,o.corrects_id,
   e.event_order AS history_order,k.known_from AS history_known
  FROM risk.job_history_observations p JOIN market_data.observations o ON(o.catalog_id,o.id,o.definition_revision_id)=(p.catalog_id,p.observation_id,p.revision_id)
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE(p.catalog_id,p.job_id)=(job.catalog_id,job.id) ORDER BY o.observed_at,o.id
 )t;
 PERFORM risk.history_attempt(job_id,token);
END$$;
ALTER FUNCTION risk.history_inputs(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.history_inputs(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.history_inputs(uuid,uuid) TO openetrm_risk_worker;
CREATE FUNCTION risk.check_history(job_id uuid,token uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM risk.history_attempt(job_id,token);END$$;
ALTER FUNCTION risk.check_history(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.check_history(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.check_history(uuid,uuid) TO openetrm_risk_worker;
CREATE OR REPLACE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb LANGUAGE sql SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('jobId',job.id,'requestId',job.request_id,'state',job.state,'contextId',job.context_id,
  'valuationAt',job.valuation_at,'createdAt',job.created_at,'startedAt',job.started_at,'finishedAt',job.finished_at,
  'attempt',job.attempt,'errorCode',job.error_code,'engineVersion',job.request->>'engineVersion','convention',job.request->>'convention',
  'run',job.frame->'run','scope',job.frame->'scope','shocks',jsonb_array_length(job.request->'bumps'))||
 CASE WHEN jsonb_typeof(job.request->'parametric')='object' THEN jsonb_build_object('kind','parametric',
  'historyId',job.frame#>'{factorHistory,expectedManifestId}','confidence',job.request#>'{parametric,policy,confidence}',
  'calibrationAvailable',job.result#>'{parametric,result,available}') ELSE '{}'::jsonb END||
 CASE WHEN jsonb_typeof(job.request->'joint')='object' THEN jsonb_build_object('kind',CASE WHEN jsonb_typeof(job.request#>'{joint,scenarios}')='object' THEN 'scenarios' ELSE 'derivatives' END,
  'scenarioCount',coalesce(jsonb_array_length(job.request#>'{joint,scenarios,scenarios}'),0),
  'derivativeCount',coalesce(2*jsonb_array_length(job.request#>'{joint,derivatives,axes}')+jsonb_array_length(job.request#>'{joint,derivatives,mixed}'),0)) ELSE '{}'::jsonb END;
$$;

CREATE FUNCTION risk.archive_parametric(catalog uuid,take integer,prior_created timestamptz,prior_id uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
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
   AND j.state='completed' AND jsonb_typeof(j.request->'parametric')='object' AND risk.authorized(j)
   AND(prior_created IS NULL OR(j.created_at,j.id)<(prior_created,prior_id)) ORDER BY j.created_at DESC,j.id DESC LIMIT take+1
 )j;
 SELECT coalesce(jsonb_agg(value ORDER BY ordinal),'[]'::jsonb) INTO items FROM jsonb_array_elements(rows) WITH ORDINALITY a(value,ordinal) WHERE ordinal<=take;
 FOR entry IN SELECT value FROM jsonb_array_elements(items) LOOP PERFORM risk.owner((entry->>'jobId')::uuid);END LOOP;
 IF jsonb_array_length(rows)>take THEN cursor=jsonb_build_object('createdAt',rows->(take-1)->'createdAt','id',rows->(take-1)->'jobId');END IF;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('items',items,'next',cursor);
END$$;
ALTER FUNCTION risk.archive_parametric(uuid,integer,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.archive_parametric(uuid,integer,timestamptz,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.archive_parametric(uuid,integer,timestamptz,uuid) TO openetrm_runtime;
COMMIT;
