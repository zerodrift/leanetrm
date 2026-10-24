-- Retain the complete market inputs of shared instrument portfolio risk.
-- Existing futures jobs and their engine fingerprints remain unchanged.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE TABLE risk.job_markets (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,origin_close_id uuid,
 PRIMARY KEY(job_id,revision_id),UNIQUE(catalog_id,job_id,revision_id),
 FOREIGN KEY(catalog_id,job_id) REFERENCES risk.jobs(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES market_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,origin_close_id) REFERENCES market_data.market_closes(catalog_id,id)
);
CREATE TABLE risk.job_market_observations (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,observation_id uuid NOT NULL,frozen boolean NOT NULL,
 PRIMARY KEY(job_id,revision_id,observation_id),
 FOREIGN KEY(catalog_id,job_id,revision_id) REFERENCES risk.job_markets(catalog_id,job_id,revision_id),
 FOREIGN KEY(catalog_id,observation_id,revision_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id)
);
CREATE TABLE risk.job_market_contracts (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,contract_id uuid NOT NULL,
 PRIMARY KEY(job_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,job_id,revision_id) REFERENCES risk.job_markets(catalog_id,job_id,revision_id),
 FOREIGN KEY(catalog_id,revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['job_markets','job_market_observations','job_market_contracts'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON risk.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_job_input BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

-- The current consumer is SwapStore's authorized trade-to-instrument projection.
-- Preserve each table's current reference, trading and temporal policies, and
-- apply the worker's independent service authority on every selected row.
DO $$DECLARE t text;p record;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_trade_revisions','swap_instruments','swap_fixed_sides','swap_floating_sides','swap_reset_projections','swap_payment_rules'] LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='trading' AND tablename=t
   AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' AND permissive='PERMISSIVE')
  THEN RAISE EXCEPTION 'risk_worker_policy_missing: trading.%',t;END IF;
  FOR p IN SELECT * FROM pg_policies WHERE schemaname='trading' AND tablename=t AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' LOOP
   EXECUTE format('CREATE POLICY %I ON trading.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)',
    'risk_worker_'||p.policyname,t,p.permissive,p.qual);
  END LOOP;
  EXECUTE format('CREATE POLICY risk_worker_service ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_risk_worker',t);
 END LOOP;
END$$;

CREATE FUNCTION risk.retain_market(job risk.jobs,revision uuid,frame jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE pin uuid;entry record;
BEGIN
 IF revision IS NULL OR revision=job.revision_id OR jsonb_typeof(frame) IS DISTINCT FROM 'object'
  OR(frame->>'id')::uuid IS DISTINCT FROM(job.frame->>'id')::uuid
  OR(frame->>'contextId')::uuid IS DISTINCT FROM job.context_id OR(frame->>'at')::timestamptz IS DISTINCT FROM job.valuation_at
  OR jsonb_typeof(frame->'observationIds') IS DISTINCT FROM 'array' OR jsonb_typeof(frame->'frozenObservationIds') IS DISTINCT FROM 'array'
  OR jsonb_typeof(frame->'curveContracts') IS DISTINCT FROM 'object' OR frame ? 'supplementalMarkets'
  OR frame ? 'swapMarketSelections' AND frame->'swapMarketSelections' NOT IN('null'::jsonb,'{}'::jsonb)
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF jsonb_array_length(frame->'observationIds') NOT BETWEEN 1 AND 100000
  OR jsonb_array_length(frame->'frozenObservationIds')>jsonb_array_length(frame->'observationIds')
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(frame->'observationIds'))
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(frame->'frozenObservationIds'))
  OR EXISTS(SELECT 1 FROM jsonb_array_elements_text(frame->'frozenObservationIds') f(value)
   WHERE NOT(frame->'observationIds' @> jsonb_build_array(f.value)))
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF NOT security.market_fact_visible(job.catalog_id,revision,NULL) OR NOT EXISTS(SELECT 1 FROM market_data.revisions r
  WHERE(r.catalog_id,r.id)=(job.catalog_id,revision) AND r.state='sealed' AND security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id))
  OR frame->>'originCloseId' IS NOT NULL AND NOT security.market_close_visible(job.catalog_id,(frame->>'originCloseId')::uuid)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 INSERT INTO risk.job_markets VALUES(job.catalog_id,job.id,revision,(frame->>'originCloseId')::uuid);
 FOR pin IN SELECT value::uuid FROM jsonb_array_elements_text(frame->'observationIds') LOOP
  IF NOT EXISTS(SELECT 1 FROM market_data.observations o WHERE(o.catalog_id,o.id,o.definition_revision_id)=(job.catalog_id,pin,revision)
   AND o.observed_at<=job.valuation_at AND security.market_fact_visible(job.catalog_id,revision,pin))
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  INSERT INTO risk.job_market_observations VALUES(job.catalog_id,job.id,revision,pin,
   EXISTS(SELECT 1 FROM jsonb_array_elements_text(frame->'frozenObservationIds') f(value) WHERE f.value::uuid=pin));
 END LOOP;
 FOR entry IN SELECT key,value FROM jsonb_each(frame->'curveContracts') LOOP
  IF jsonb_typeof(entry.value)<>'array' OR jsonb_array_length(entry.value)>100000 THEN RAISE EXCEPTION 'risk_job_input';END IF;
  FOR pin IN SELECT value::uuid FROM jsonb_array_elements_text(entry.value) LOOP
   IF NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(job.catalog_id,revision,entry.key,pin))
   THEN RAISE EXCEPTION 'risk_job_input';END IF;
   INSERT INTO risk.job_market_contracts VALUES(job.catalog_id,job.id,revision,entry.key,pin);
  END LOOP;
 END LOOP;
END$$;
ALTER FUNCTION risk.retain_market(risk.jobs,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_market(risk.jobs,uuid,jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION risk.authorized(job risk.jobs) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(job.catalog_id,job.tenant_id,job.workspace_id))
 AND EXISTS(SELECT 1 FROM market_data.revisions r JOIN security.reference_catalog_scopes s ON s.catalog_id=r.reference_catalog_id
  WHERE(r.catalog_id,r.id,s.tenant_id,s.workspace_id)=(job.catalog_id,job.revision_id,job.tenant_id,job.workspace_id))
 AND NOT EXISTS(SELECT 1 FROM risk.job_markets m JOIN market_data.revisions r ON(r.catalog_id,r.id)=(m.catalog_id,m.revision_id)
  WHERE m.job_id=job.id AND NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s
   WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(r.reference_catalog_id,job.tenant_id,job.workspace_id)))
 AND NOT EXISTS(SELECT 1 FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario']) permission
  WHERE NOT risk.actor_permission(job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,permission));
$$;

CREATE OR REPLACE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb LANGUAGE sql SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('jobId',job.id,'requestId',job.request_id,'state',job.state,'contextId',job.context_id,
  'valuationAt',job.valuation_at,'createdAt',job.created_at,'startedAt',job.started_at,'finishedAt',job.finished_at,
  'attempt',job.attempt,'errorCode',job.error_code,'engineVersion',job.request->>'engineVersion','convention',job.request->>'convention');
$$;

CREATE FUNCTION risk.list(catalog uuid,convention text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;job_id uuid;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(jsonb_agg(risk.summary(j) ORDER BY j.created_at DESC,j.id),'[]'::jsonb) INTO result FROM
 (SELECT * FROM risk.jobs WHERE catalog_id=catalog AND(actor_id,tenant_id,workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
  AND(convention IS NULL OR request->>'convention'=convention)
  AND risk.authorized(jobs) ORDER BY created_at DESC,id LIMIT 50) j;
 FOR job_id IN SELECT(value->>'jobId')::uuid FROM jsonb_array_elements(result) LOOP
  PERFORM risk.owner(job_id);
 END LOOP;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION risk.list(uuid,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.list(uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.list(uuid,text) TO openetrm_runtime;
CREATE OR REPLACE FUNCTION risk.list(catalog uuid) RETURNS jsonb LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT risk.list(catalog,NULL::text);
$$;

CREATE OR REPLACE FUNCTION risk.submit(catalog uuid,operation uuid,context uuid,revision uuid,valuation_at timestamptz,request jsonb,frame jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;job risk.jobs;pin uuid;entry record;new_id uuid=gen_random_uuid();
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL OR NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades')
  OR(c.market_catalog_id,c.market_context_id,c.trading_as_of) IS DISTINCT FROM(catalog,context,valuation_at)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR context IS NULL OR revision IS NULL OR valuation_at IS NULL
  OR jsonb_typeof(request) IS DISTINCT FROM 'object' OR jsonb_typeof(frame) IS DISTINCT FROM 'object'
  OR pg_column_size(request)>1000000 OR pg_column_size(frame)>8000000
  OR coalesce(length(request->>'engineVersion'),0) NOT BETWEEN 1 AND 256
  OR jsonb_typeof(request->'bookIds') IS DISTINCT FROM 'array' OR jsonb_array_length(request->'bookIds') NOT BETWEEN 1 AND 4096
  OR request->'portfolioIds' IS DISTINCT FROM '[]'::jsonb OR jsonb_typeof(request->'stress') IS DISTINCT FROM 'array'
  OR request#>>'{context,knowledgeMode}' IS DISTINCT FROM 'retained'
  OR(request#>>'{context,contextId}')::uuid IS DISTINCT FROM context OR(frame->>'contextId')::uuid IS DISTINCT FROM context
  OR(request#>>'{context,snapshotId}')::uuid IS DISTINCT FROM(frame->>'id')::uuid OR(frame->>'id')::uuid IS NULL
  OR(frame->>'at')::timestamptz IS DISTINCT FROM valuation_at OR jsonb_typeof(frame->'observationIds') IS DISTINCT FROM 'array'
  OR jsonb_array_length(frame->'observationIds') NOT BETWEEN 1 AND 100000 OR jsonb_typeof(frame->'curveContracts') IS DISTINCT FROM 'object'
  OR jsonb_typeof(frame->'frozenObservationIds') IS DISTINCT FROM 'array'
  OR jsonb_array_length(frame->'frozenObservationIds')>jsonb_array_length(frame->'observationIds')
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements_text(frame->'frozenObservationIds') frozen(value)
  WHERE NOT(frame->'observationIds' @> jsonb_build_array(frozen.value)))
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(frame->'frozenObservationIds'))
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF request->>'convention'='portfolio_instrument_repricing_v1' THEN
  IF request->>'numericalPolicy' IS DISTINCT FROM 'exact_rational_portfolio_repricing_v1'
   OR jsonb_typeof(request->'pricingPolicy') IS DISTINCT FROM 'object'
   OR NOT(request->'pricingPolicy' ?& ARRAY['curveNumericalPolicy','riskNumericalPolicy','futuresConvention','swapConvention',
    'rateNumericalPolicy','discountInterpolationPolicy','bumpConvention','bumpBasis','bumpTicks'])
   OR jsonb_typeof(frame->'supplementalMarkets') IS DISTINCT FROM 'object'
   OR jsonb_typeof(frame->'swapMarketSelections') IS DISTINCT FROM 'object'
  THEN RAISE EXCEPTION 'risk_job_input';END IF;
  IF(SELECT count(*) FROM jsonb_each(frame->'supplementalMarkets'))>64
   OR frame->'swapMarketSelections' IS DISTINCT FROM
    (SELECT coalesce(jsonb_object_agg(key,value->'observationIds'),'{}'::jsonb) FROM jsonb_each(frame->'supplementalMarkets'))
  THEN RAISE EXCEPTION 'risk_job_input';END IF;
 ELSE
  -- Existing futures submissions and retained request identities keep their original contract.
  IF frame ? 'swapMarketSelections' AND frame->'swapMarketSelections' NOT IN('null'::jsonb,'{}'::jsonb)
   OR frame ? 'supplementalMarkets' AND frame->'supplementalMarkets' NOT IN('null'::jsonb,'{}'::jsonb)
  THEN RAISE EXCEPTION 'risk_job_input';END IF;
 END IF;
 -- The operation key is serialized before checking an earlier identical request.
 PERFORM pg_advisory_xact_lock(hashtextextended(c.tenant_id::text||c.workspace_id::text||c.actor_id::text||operation::text,730849136));
 SELECT * INTO job FROM risk.jobs WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  IF(job.catalog_id,job.context_id,job.revision_id,job.valuation_at,job.request,job.frame,job.delegation_id)
   IS DISTINCT FROM(catalog,context,revision,valuation_at,request,frame,c.delegation_id) THEN RAISE EXCEPTION 'risk_job_conflict';END IF;
  RETURN risk.summary(risk.owner(job.id));
 END IF;
 IF NOT security.market_fact_visible(catalog,revision,NULL) OR NOT EXISTS(SELECT 1 FROM market_data.revisions r
  WHERE(r.catalog_id,r.id)=(catalog,revision) AND r.state='sealed' AND security.reference_access(r.reference_catalog_id,'ReadMarketData'))
  OR frame->>'originCloseId' IS NOT NULL AND NOT security.market_close_visible(catalog,(frame->>'originCloseId')::uuid)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 INSERT INTO risk.jobs(id,catalog_id,request_id,actor_id,tenant_id,workspace_id,delegation_id,context_id,revision_id,origin_close_id,valuation_at,request,frame,security_generation,created_at,state,attempt)
 VALUES(new_id,catalog,operation,c.actor_id,c.tenant_id,c.workspace_id,c.delegation_id,context,revision,(frame->>'originCloseId')::uuid,valuation_at,request,frame,(SELECT generation FROM security.epoch),clock_timestamp(),'queued',0);
 FOR pin IN SELECT value::uuid FROM jsonb_array_elements_text(request->'bookIds') LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.books b WHERE(b.catalog_id,b.id)=(catalog,pin) AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at)) THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  INSERT INTO risk.job_books VALUES(catalog,new_id,pin);
 END LOOP;
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
 SELECT * INTO job FROM risk.jobs WHERE id=new_id;
 IF request->>'convention'='portfolio_instrument_repricing_v1' THEN
  FOR entry IN SELECT key,value FROM jsonb_each(frame->'supplementalMarkets') LOOP
   PERFORM risk.retain_market(job,entry.key::uuid,entry.value);
  END LOOP;
 END IF;
 job=risk.owner(new_id);
 RETURN risk.summary(job);
END$$;

CREATE OR REPLACE FUNCTION security.swap_discount_default(catalog uuid,ref_catalog uuid,ref_revision uuid,currency text,definition uuid,valuation timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;candidates jsonb;
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read()
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501'; END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL AND c.trading_as_of IS DISTINCT FROM valuation THEN RAISE EXCEPTION 'swap_context' USING ERRCODE='P0001'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') OR NOT security.reference_version_visible(ref_catalog,ref_revision) OR NOT security.market_fact_visible(catalog,definition,NULL) THEN RAISE EXCEPTION 'No visible reference or market definition' USING ERRCODE='42501'; END IF;
 -- Group by the selection order so every mapping at the latest instant and publication is seen.
 SELECT jsonb_agg(to_jsonb(d)) INTO candidates FROM trading.currency_discount_defaults d
 WHERE d.catalog_id=catalog AND d.reference_catalog_id=ref_catalog AND d.reference_revision_id=ref_revision AND d.currency_code=currency AND d.definition_revision_id=definition AND d.effective_at<=valuation
 AND(c.market_context_id IS NULL OR security.trading_visible(d.catalog_id,d.publication_order,d.effective_at))
 GROUP BY d.effective_at,d.publication_order ORDER BY d.effective_at DESC,d.publication_order DESC LIMIT 1;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') OR NOT security.reference_version_visible(ref_catalog,ref_revision) OR NOT security.market_fact_visible(catalog,definition,NULL) THEN RAISE EXCEPTION 'No visible reference or market definition' USING ERRCODE='42501'; END IF;
 IF jsonb_array_length(candidates)>1 THEN RAISE EXCEPTION 'swap_discount_ambiguous' USING ERRCODE='P0001'; END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read()
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN candidates->0;
END $$;
GRANT EXECUTE ON FUNCTION security.swap_discount_default(uuid,uuid,uuid,text,uuid,timestamptz) TO openetrm_risk_worker;
COMMIT;
