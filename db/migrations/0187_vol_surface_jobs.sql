-- Volatility surface fits as a second kind of retained risk job, convention vol_surface_fit_v1, under the queue's lease,
-- attempt, cancellation and security-generation rules. A fit job is admitted at a working frame's captured context by an
-- actor holding PublishMarketData, ReadTrades and ReadMarketData; the risk worker fits it and, in the transaction that
-- completes the job, records the surface through trading.record_vol_surface_revision. Portfolio risk jobs keep their
-- admission, claim, list and read behavior; a worker build claims only jobs admitted under its own engine version.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

-- A fit job's actor needs the authority the fit uses: market data and trades to read its inputs and the discount default,
-- and PublishMarketData to record the surface. Every other job keeps the portfolio risk set.
-- Only the permission set changes: the installed definition (0077, with 0082's transitive custom-curve scopes) is otherwise kept.
DO $upgrade$
DECLARE definition text=pg_get_functiondef('risk.authorized(risk.jobs)'::regprocedure);
 before text='unnest(ARRAY[''ReadMarketData'',''ReadTrades'',''RunScenario'']) permission';
 after text='unnest(CASE WHEN job.request->>''convention''=''vol_surface_fit_v1'' THEN ARRAY[''ReadMarketData'',''ReadTrades'',''PublishMarketData''] ELSE ARRAY[''ReadMarketData'',''ReadTrades'',''RunScenario''] END) permission';
BEGIN
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'vol_surface_jobs: unexpected risk.authorized definition';END IF;
 EXECUTE replace(definition,before,after);
END $upgrade$;

-- A fit request answered with another queued or running job for the same key and pinned frame: a retry of the request
-- identity returns that job, its first outcome (D238).
CREATE TABLE risk.vol_surface_fit_requests (
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL,request_id uuid NOT NULL,job_id uuid NOT NULL REFERENCES risk.jobs(id),
 PRIMARY KEY(tenant_id,workspace_id,actor_id,request_id)
);
ALTER TABLE risk.vol_surface_fit_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE risk.vol_surface_fit_requests FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON risk.vol_surface_fit_requests TO openetrm_security_owner;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON risk.vol_surface_fit_requests FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- Admits a fit at the caller's bound trading context. The request names the key (one exact product version, the context's
-- business date and the vol model), the solver policy, the engine version and the futures convention; the frame is the
-- working frame pinned at that context. Only a context captured from current knowledge is admitted: its cutoff is the
-- catalog knowledge clock's latest certification, and the share lock taken on the clock here makes a later certification
-- wait for this transaction to end, so the context is still current when the job commits; an earlier cutoff, or a current capture a later one superseded, is refused with historical_read_only. The
-- request identity returns its first job; otherwise a queued or running job of the caller for the same request and the
-- same frame, whatever context it captured, is returned instead of a new one.
CREATE FUNCTION risk.submit_vol_surface_fit(catalog uuid,operation uuid,context uuid,revision uuid,valuation_at timestamptz,request jsonb,frame jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;job risk.jobs;p ref_data.futures_option_products;business_date date;pin uuid;entry record;new_id uuid=gen_random_uuid();
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL OR NOT security.has_permission('PublishMarketData') OR NOT security.trading_access(catalog,'ReadTrades')
  OR NOT security.market_access(catalog,'ReadMarketData') OR EXISTS(SELECT 1 FROM security.principals x WHERE x.id=c.actor_id AND x.kind='agent')
  OR(c.market_catalog_id,c.market_context_id,c.trading_as_of) IS DISTINCT FROM(catalog,context,valuation_at)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN market_data.knowledge_clocks k ON k.catalog_id=t.catalog_id
  WHERE(t.catalog_id,t.id)=(catalog,context) AND t.known_at=k.certified_through AND t.certified_through=k.certified_through FOR SHARE OF k)
 THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR context IS NULL OR revision IS NULL OR valuation_at IS NULL
  OR jsonb_typeof(request) IS DISTINCT FROM 'object' OR jsonb_typeof(frame) IS DISTINCT FROM 'object' OR pg_column_size(frame)>8000000
  OR(SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(request) key)
   IS DISTINCT FROM ARRAY['businessDate','convention','engineVersion','futuresConvention','model','product','solverPolicy']
  OR request->>'convention' IS DISTINCT FROM 'vol_surface_fit_v1' OR request->>'solverPolicy' IS DISTINCT FROM 'american_futures_option_crr_v1'
  OR jsonb_typeof(request->'engineVersion') IS DISTINCT FROM 'string' OR length(request->>'engineVersion') NOT BETWEEN 1 AND 256
  OR jsonb_typeof(request->'futuresConvention') IS DISTINCT FROM 'string' OR length(request->>'futuresConvention') NOT BETWEEN 1 AND 128
  OR jsonb_typeof(request->'businessDate') IS DISTINCT FROM 'string' OR request->>'businessDate' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  OR jsonb_typeof(request->'model') IS DISTINCT FROM 'string'
  OR(frame->>'contextId')::uuid IS DISTINCT FROM context OR(frame->>'id')::uuid IS NULL OR(frame->>'at')::timestamptz IS DISTINCT FROM valuation_at
  OR jsonb_typeof(frame->'observationIds') IS DISTINCT FROM 'array' OR jsonb_array_length(frame->'observationIds') NOT BETWEEN 1 AND 100000
  OR jsonb_typeof(frame->'curveContracts') IS DISTINCT FROM 'object' OR jsonb_typeof(frame->'frozenObservationIds') IS DISTINCT FROM 'array'
  OR frame ? 'swapMarketSelections' AND frame->'swapMarketSelections'<>'null'::jsonb OR frame ? 'supplementalMarkets' OR frame ? 'run'
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements_text(frame->'frozenObservationIds') frozen(value) WHERE NOT(frame->'observationIds' @> jsonb_build_array(frozen.value)))
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(frame->'frozenObservationIds'))
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 business_date=(request->>'businessDate')::date;
 p=trading.vol_surface_key(request->'product',business_date,request->>'model');
 IF NOT security.market_fact_visible(catalog,revision,NULL) OR NOT security.reference_access(p.catalog_id,'ReadMarketData')
  OR NOT security.reference_version_visible(p.catalog_id,p.revision_id)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=(catalog,revision,p.catalog_id,p.revision_id)
  AND r.state='sealed') THEN RAISE EXCEPTION 'vol_surface_product';END IF;
 IF business_date IS DISTINCT FROM(SELECT t.effective_on FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context))
 THEN RAISE EXCEPTION 'vol_surface_context';END IF;
 -- The request identity is serialized with every risk job's, so it names one job of either kind.
 PERFORM pg_advisory_xact_lock(hashtextextended(c.tenant_id::text||c.workspace_id::text||c.actor_id::text||operation::text,730849136));
 SELECT j.* INTO job FROM risk.jobs j WHERE(j.tenant_id,j.workspace_id,j.actor_id,j.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF NOT FOUND THEN
  SELECT j.* INTO job FROM risk.vol_surface_fit_requests q JOIN risk.jobs j ON j.id=q.job_id
   WHERE(q.tenant_id,q.workspace_id,q.actor_id,q.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 END IF;
 IF job.id IS NOT NULL THEN
  IF(job.catalog_id,job.revision_id,job.valuation_at,job.request,job.frame-'contextId',job.delegation_id)
   IS DISTINCT FROM(catalog,revision,valuation_at,request,frame-'contextId',c.delegation_id) THEN RAISE EXCEPTION 'risk_job_conflict';END IF;
  RETURN risk.summary(job);
 END IF;
 -- One admission at a time per key and caller, so two requests sent at once share one job.
 PERFORM pg_advisory_xact_lock(hashtextextended(c.tenant_id::text||c.workspace_id::text||c.actor_id::text||catalog::text||p.catalog_id::text||p.revision_id::text
  ||p.code||'|'||(request->>'businessDate')||'|'||(request->>'model'),730849138));
 SELECT j.* INTO job FROM risk.jobs j WHERE j.catalog_id=catalog AND(j.tenant_id,j.workspace_id,j.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
  AND j.state IN('queued','running') AND j.request=submit_vol_surface_fit.request AND j.revision_id=revision
  AND j.valuation_at=submit_vol_surface_fit.valuation_at AND j.frame-'contextId'=submit_vol_surface_fit.frame-'contextId'
  AND j.delegation_id IS NOT DISTINCT FROM c.delegation_id
  ORDER BY j.created_at,j.id LIMIT 1;
 IF FOUND THEN
  INSERT INTO risk.vol_surface_fit_requests VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,job.id);
  RETURN risk.summary(job);
 END IF;
 INSERT INTO risk.jobs(id,catalog_id,request_id,actor_id,tenant_id,workspace_id,delegation_id,context_id,revision_id,valuation_at,request,frame,security_generation,created_at,state,attempt)
 VALUES(new_id,catalog,operation,c.actor_id,c.tenant_id,c.workspace_id,c.delegation_id,context,revision,valuation_at,request,frame,(SELECT generation FROM security.epoch),clock_timestamp(),'queued',0)
 RETURNING * INTO job;
 FOR pin IN SELECT value::uuid FROM jsonb_array_elements_text(frame->'observationIds') LOOP
  IF NOT EXISTS(SELECT 1 FROM market_data.observations o WHERE(o.catalog_id,o.id,o.definition_revision_id)=(catalog,pin,revision)
   AND o.observed_at<=valuation_at AND security.market_fact_visible(catalog,revision,pin)) THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  INSERT INTO risk.job_observations VALUES(catalog,new_id,pin);
 END LOOP;
 FOR entry IN SELECT key,value FROM jsonb_each(frame->'curveContracts') LOOP
  IF jsonb_typeof(entry.value)<>'array' OR jsonb_array_length(entry.value)>100000 THEN RAISE EXCEPTION 'risk_job_input';END IF;
  FOR pin IN SELECT value::uuid FROM jsonb_array_elements_text(entry.value) LOOP
   INSERT INTO risk.job_contracts VALUES(catalog,new_id,revision,entry.key,pin);
  END LOOP;
 END LOOP;
 IF NOT risk.authorized(job) OR NOT security.has_permission('PublishMarketData') OR NOT security.trading_access(catalog,'ReadTrades')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN risk.summary(job);
END$$;

-- Completes a fit job's current attempt by recording its surface. The attempt is fenced, and its actor's authority and the
-- security generation rechecked, exactly as risk.finish does, before anything is recorded; the key, request, frame and
-- valuation instant are the job's own, and every input must be one of the observations the job pinned. Recording and
-- completion share this transaction: a refusal, a lost lease or lost authority records nothing.
CREATE FUNCTION risk.finish_vol_surface_fit(job_id uuid,token uuid,inputs uuid[],surface jsonb,digest text,measures jsonb) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;outcome jsonb;
BEGIN
 job=risk.owned_attempt(job_id,token,true,true);
 IF job.state='cancellation_requested' THEN RETURN job.state;END IF;
 IF job.request->>'convention' IS DISTINCT FROM 'vol_surface_fit_v1' OR jsonb_typeof(measures) IS DISTINCT FROM 'object'
  OR(SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(measures) key) IS DISTINCT FROM ARRAY['elapsedMs','series','solves']
  OR EXISTS(SELECT 1 FROM jsonb_each(measures) m WHERE jsonb_typeof(m.value)<>'number' OR m.value::text!~'^[0-9]{1,12}$')
  OR inputs IS NULL OR EXISTS(SELECT 1 FROM unnest(inputs) i(id) WHERE NOT EXISTS(SELECT 1 FROM risk.job_observations o WHERE(o.job_id,o.observation_id)=(job.id,i.id)))
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 outcome=trading.record_vol_surface_revision(job.catalog_id,job.actor_id,job.id,job.request->'product',(job.request->>'businessDate')::date,
  job.request->>'model',job.request->>'solverPolicy',(job.frame->>'id')::uuid,job.valuation_at,inputs,surface,digest);
 RETURN risk.finish(job_id,token,jsonb_build_object('revision',outcome->'revision','created',outcome->'created')||measures,NULL);
END$$;

-- Whether any observation a visible surface revision pinned, a settlement or a discount-curve point, has a newer revision
-- of the same source fact known at the bound context: a later knowledge event, visible exactly as the context's market
-- reads are.
CREATE FUNCTION security.vol_surface_inputs_revised(catalog uuid,revision uuid) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE revised boolean;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 THEN RAISE EXCEPTION 'No volatility surface read access' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL AND c.trading_as_of IS NOT NULL)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.vol_surface_revisions r WHERE(r.catalog_id,r.id)=(catalog,revision)
  AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_on::timestamp AT TIME ZONE 'UTC'))
 THEN RAISE EXCEPTION 'vol_surface_input' USING ERRCODE='P0001';END IF;
 SELECT EXISTS(SELECT 1 FROM trading.vol_surface_inputs i
  JOIN market_data.observations p ON(p.catalog_id,p.id)=(i.catalog_id,i.observation_id)
  JOIN market_data.knowledge_events pe ON(pe.catalog_id,pe.observation_id)=(p.catalog_id,p.id)
  JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.source_event_id,o.role,o.observed_at)
   =(p.catalog_id,p.definition_revision_id,p.series_id,p.source_code,p.source_event_id,p.role,p.observed_at) AND o.id<>p.id
  JOIN market_data.knowledge_events oe ON(oe.catalog_id,oe.observation_id)=(o.catalog_id,o.id)
  WHERE(i.catalog_id,i.revision_id)=(catalog,revision) AND oe.event_order>pe.event_order
   AND security.market_fact_visible(o.catalog_id,o.definition_revision_id,o.id)) INTO revised;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.trading_access(catalog,'ReadTrades')
 THEN RAISE EXCEPTION 'Volatility surface read authority expired' USING ERRCODE='42501';END IF;
 RETURN revised;
END$$;

-- The caller's newest fit job for a key, with its state and error code, or null. Jobs are operational state: the read
-- shows each as it is now.
CREATE FUNCTION risk.vol_surface_fit_job(catalog uuid,product jsonb,business_date date,model text) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT jsonb_build_object('id',j.id,'state',j.state,'errorCode',j.error_code) INTO result FROM risk.jobs j
  WHERE j.catalog_id=catalog AND(j.tenant_id,j.workspace_id,j.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
  AND j.request->>'convention'='vol_surface_fit_v1' AND j.request->'product'=product
  AND j.request->>'businessDate'=to_char(business_date,'YYYY-MM-DD') AND j.request->>'model'=model
  ORDER BY j.created_at DESC,j.id DESC LIMIT 1;
 RETURN result;
END$$;

DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)',
  'risk.finish_vol_surface_fit(uuid,uuid,uuid[],jsonb,text,jsonb)','security.vol_surface_inputs_revised(uuid,uuid)','risk.vol_surface_fit_job(uuid,jsonb,date,text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb),security.vol_surface_inputs_revised(uuid,uuid),
 risk.vol_surface_fit_job(uuid,jsonb,date,text) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION risk.finish_vol_surface_fit(uuid,uuid,uuid[],jsonb,text,jsonb) TO openetrm_risk_worker;
COMMIT;
