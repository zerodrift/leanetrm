-- Retain saved custom recipe versions and multi-date sample inputs separately
-- from the primary market snapshot's one-selection-per-input contract.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
CREATE TABLE risk.job_connected_markets (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,manifest_id text NOT NULL CHECK(manifest_id ~ '^[0-9a-f]{64}$'),
 PRIMARY KEY(job_id,revision_id),UNIQUE(catalog_id,job_id,revision_id),
 FOREIGN KEY(catalog_id,job_id) REFERENCES risk.jobs(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES market_data.revisions(catalog_id,id)
);
CREATE TABLE risk.job_custom_curves (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,
 output_node text NOT NULL,position integer NOT NULL CHECK(position>=0),
 selection text NOT NULL CHECK(selection='single_output_position_v1'),
 implementation text NOT NULL CHECK(implementation='published_custom_curve_projection_v1'),
 PRIMARY KEY(job_id,revision_id,curve_id,version,output_node,position),
 FOREIGN KEY(catalog_id,job_id,revision_id) REFERENCES risk.job_connected_markets(catalog_id,job_id,revision_id),
 FOREIGN KEY(catalog_id,curve_id,version,output_node) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id)
);
CREATE TABLE risk.job_curve_samples (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,snapshot_id uuid NOT NULL,
 source_curve ref_data.code NOT NULL,contract_id uuid NOT NULL,sample_at ref_data.instant NOT NULL,freeze_historical boolean NOT NULL,
 PRIMARY KEY(job_id,revision_id,snapshot_id,source_curve,contract_id),UNIQUE(catalog_id,job_id,revision_id,snapshot_id,source_curve,contract_id),
 FOREIGN KEY(catalog_id,job_id,revision_id) REFERENCES risk.job_connected_markets(catalog_id,job_id,revision_id),
 FOREIGN KEY(catalog_id,revision_id,source_curve,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE TABLE risk.job_curve_sample_observations (
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,revision_id uuid NOT NULL,snapshot_id uuid NOT NULL,
 source_curve ref_data.code NOT NULL,contract_id uuid NOT NULL,observation_id uuid NOT NULL,series_id uuid NOT NULL,input_use text NOT NULL CHECK(input_use IN('Variable','FrozenFact')),
 PRIMARY KEY(job_id,revision_id,snapshot_id,source_curve,contract_id,series_id,input_use),
 FOREIGN KEY(catalog_id,job_id,revision_id,snapshot_id,source_curve,contract_id)
  REFERENCES risk.job_curve_samples(catalog_id,job_id,revision_id,snapshot_id,source_curve,contract_id),
 FOREIGN KEY(catalog_id,observation_id,revision_id,series_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id,series_id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['job_connected_markets','job_custom_curves','job_curve_samples','job_curve_sample_observations'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON risk.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_job_input BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION risk.retain_connected(job risk.jobs,markets jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE market jsonb;recipe jsonb;sample jsonb;observation jsonb;revision uuid;sample_id uuid;selected_sample_at timestamptz;source text;contract uuid;pin uuid;series uuid;use_name text;
BEGIN
 -- Previously admitted ordinary/futures clients have no connected envelope.
 IF markets IS NULL OR markets='[]'::jsonb THEN RETURN;END IF;
 IF job.request->>'convention' IS DISTINCT FROM 'portfolio_instrument_repricing_v1' OR jsonb_typeof(markets) IS DISTINCT FROM 'array'
  OR jsonb_array_length(markets)>64 THEN RAISE EXCEPTION 'risk_graph_input';END IF;
 FOR market IN SELECT value FROM jsonb_array_elements(markets) LOOP
  IF jsonb_typeof(market) IS DISTINCT FROM 'object' OR jsonb_typeof(market->'recipes') IS DISTINCT FROM 'array'
   OR jsonb_typeof(market->'samples') IS DISTINCT FROM 'array' OR coalesce(market->>'manifestId','') !~ '^[0-9a-f]{64}$'
   OR jsonb_array_length(market->'recipes') NOT BETWEEN 1 AND 4096 OR jsonb_array_length(market->'samples') NOT BETWEEN 1 AND 100000
  THEN RAISE EXCEPTION 'risk_graph_input';END IF;
  revision=(market->>'revisionId')::uuid;
  IF revision IS NULL OR(revision<>job.revision_id AND NOT EXISTS(SELECT 1 FROM risk.job_markets m WHERE(m.job_id,m.revision_id)=(job.id,revision)))
   OR NOT security.market_fact_visible(job.catalog_id,revision,NULL)
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  INSERT INTO risk.job_connected_markets VALUES(job.catalog_id,job.id,revision,market->>'manifestId');
  FOR recipe IN SELECT value FROM jsonb_array_elements(market->'recipes') LOOP
   recipe=security.validate_custom_projection(job.catalog_id,recipe);
   IF recipe IS NULL OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources s
    WHERE(s.catalog_id,s.curve_id,s.version)=(job.catalog_id,(recipe->>'curveId')::uuid,(recipe->>'version')::integer)
     AND(s.source_catalog_id,s.source_revision_id) IS DISTINCT FROM(job.catalog_id,revision))
   THEN RAISE EXCEPTION 'risk_graph_input';END IF;
   INSERT INTO risk.job_custom_curves VALUES(job.catalog_id,job.id,revision,(recipe->>'curveId')::uuid,(recipe->>'version')::integer,
    recipe->>'outputNode',(recipe->>'position')::integer,recipe->>'selection',recipe->>'implementation');
  END LOOP;
  FOR sample IN SELECT value FROM jsonb_array_elements(market->'samples') LOOP
   IF jsonb_typeof(sample) IS DISTINCT FROM 'object' OR jsonb_typeof(sample->'observations') IS DISTINCT FROM 'array'
    OR jsonb_typeof(sample->'freezeHistorical') IS DISTINCT FROM 'boolean' OR jsonb_array_length(sample->'observations') NOT BETWEEN 1 AND 100000
   THEN RAISE EXCEPTION 'risk_graph_input';END IF;
   sample_id=(sample->>'snapshotId')::uuid;selected_sample_at=(sample->>'at')::timestamptz;source=sample->>'sourceCurve';contract=(sample->>'contractId')::uuid;
   IF sample_id IS NULL OR sample_id='00000000-0000-0000-0000-000000000000'::uuid OR selected_sample_at IS NULL OR NOT isfinite(selected_sample_at) OR selected_sample_at>job.valuation_at
    OR (sample->>'freezeHistorical')::boolean IS DISTINCT FROM(selected_sample_at<job.valuation_at)
    OR NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(job.catalog_id,revision,source,contract))
    OR NOT EXISTS(SELECT 1 FROM risk.job_custom_curves jc JOIN market_data.custom_curve_node_sources cs ON(cs.catalog_id,cs.curve_id,cs.version)=(jc.catalog_id,jc.curve_id,jc.version)
     WHERE jc.job_id=job.id AND jc.revision_id=revision AND(cs.source_catalog_id,cs.source_revision_id,cs.source_curve_code)=(job.catalog_id,revision,source))
    OR EXISTS(SELECT 1 FROM risk.job_curve_samples s WHERE(s.job_id,s.revision_id,s.snapshot_id)=(job.id,revision,sample_id) AND s.sample_at<>selected_sample_at)
   THEN RAISE EXCEPTION 'risk_graph_input';END IF;
   INSERT INTO risk.job_curve_samples VALUES(job.catalog_id,job.id,revision,sample_id,source,contract,selected_sample_at,(sample->>'freezeHistorical')::boolean);
   FOR observation IN SELECT value FROM jsonb_array_elements(sample->'observations') LOOP
    pin=(observation->>'id')::uuid;series=(observation->>'seriesId')::uuid;use_name=observation->>'use';
    IF use_name IS NULL OR use_name NOT IN('Variable','FrozenFact') OR NOT EXISTS(SELECT 1 FROM market_data.observations o
     WHERE(o.catalog_id,o.id,o.definition_revision_id,o.series_id)=(job.catalog_id,pin,revision,series)
      AND o.publication_state='final' AND o.observed_at<=selected_sample_at AND security.market_fact_visible(job.catalog_id,revision,pin))
    THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
    IF EXISTS(SELECT 1 FROM risk.job_curve_sample_observations prior
     WHERE(prior.job_id,prior.revision_id,prior.snapshot_id,prior.series_id,prior.input_use)=(job.id,revision,sample_id,series,use_name)
      AND prior.observation_id<>pin) THEN RAISE EXCEPTION 'risk_graph_input';END IF;
    INSERT INTO risk.job_curve_sample_observations VALUES(job.catalog_id,job.id,revision,sample_id,source,contract,pin,series,use_name);
   END LOOP;
  END LOOP;
 END LOOP;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'risk_graph_input';
END$$;
ALTER FUNCTION risk.retain_connected(risk.jobs,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_connected(risk.jobs,jsonb) FROM PUBLIC;
CREATE OR REPLACE FUNCTION risk.authorized(job risk.jobs) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(job.catalog_id,job.tenant_id,job.workspace_id))
 AND EXISTS(SELECT 1 FROM market_data.revisions r JOIN security.reference_catalog_scopes s ON s.catalog_id=r.reference_catalog_id
  WHERE(r.catalog_id,r.id,s.tenant_id,s.workspace_id)=(job.catalog_id,job.revision_id,job.tenant_id,job.workspace_id))
 AND NOT EXISTS(SELECT 1 FROM risk.job_markets m JOIN market_data.revisions r ON(r.catalog_id,r.id)=(m.catalog_id,m.revision_id)
  WHERE m.job_id=job.id AND NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s
   WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(r.reference_catalog_id,job.tenant_id,job.workspace_id)))
 AND NOT EXISTS(SELECT 1 FROM risk.job_custom_curves jc JOIN market_data.custom_curve_node_calendars cc
  ON(cc.catalog_id,cc.curve_id,cc.version)=(jc.catalog_id,jc.curve_id,jc.version)
  WHERE jc.job_id=job.id AND NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s
   WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(cc.reference_catalog_id,job.tenant_id,job.workspace_id)))
 AND NOT EXISTS(SELECT 1 FROM risk.job_custom_curves jc JOIN market_data.custom_curve_node_schedules cs
  ON(cs.catalog_id,cs.curve_id,cs.version)=(jc.catalog_id,jc.curve_id,jc.version)
  WHERE jc.job_id=job.id AND NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes s
   WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(cs.schedule_catalog_id,job.tenant_id,job.workspace_id)))
 AND NOT EXISTS(SELECT 1 FROM risk.job_custom_curves jc JOIN market_data.custom_curve_node_schedules cs
  ON(cs.catalog_id,cs.curve_id,cs.version)=(jc.catalog_id,jc.curve_id,jc.version)
  JOIN market_data.date_schedule_versions v ON(v.catalog_id,v.schedule_id,v.version)=(cs.schedule_catalog_id,cs.schedule_id,cs.schedule_version)
  WHERE jc.job_id=job.id AND v.calendar_catalog_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s
   WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(v.calendar_catalog_id,job.tenant_id,job.workspace_id)))
 AND NOT EXISTS(SELECT 1 FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario']) permission
  WHERE NOT risk.actor_permission(job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,permission));
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
 PERFORM risk.retain_connected(job,frame->'connectedMarkets');
 job=risk.owner(new_id);
 RETURN risk.summary(job);
END$$;

COMMIT;
