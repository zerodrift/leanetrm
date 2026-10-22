-- One connected valuation operation with exact retained curve-coordinate targets.
-- This fresh-schema baseline requires an explicit operator archive/reset of
-- all retained pre-cutover jobs. It never deletes or rewrites financial history.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM risk.jobs) THEN
  RAISE EXCEPTION 'risk_cutover_requires_empty_retained_jobs: explicitly archive/reset before installing this baseline';
 END IF;
END$$;
ALTER TABLE risk.job_connected_markets ADD UNIQUE(job_id,revision_id,manifest_id);
CREATE TABLE risk.job_scenario_axes (
 job_id uuid NOT NULL,axis_id text NOT NULL CHECK(axis_id ~ '^[0-9a-f]{64}$'),market_revision_id uuid NOT NULL,manifest_id text NOT NULL,
 coordinate text NOT NULL CHECK(coordinate IN('market_quote','computed_output')),
 target_kind text NOT NULL CHECK(target_kind IN('contract','points','parallel','delivery_bucket')),
 curve_catalog_id uuid NOT NULL,curve_revision_id uuid NOT NULL,curve_code text NOT NULL CHECK(length(curve_code) BETWEEN 1 AND 1024),
 contract_catalog_id uuid,contract_revision_id uuid,contract_id uuid,bucket_from date,bucket_to date,bucket_convention text,
 factor jsonb NOT NULL CHECK(jsonb_typeof(factor)='object'),
 PRIMARY KEY(job_id,axis_id),UNIQUE(job_id,axis_id,curve_catalog_id,curve_revision_id,curve_code),
 FOREIGN KEY(job_id,market_revision_id,manifest_id) REFERENCES risk.job_connected_markets(job_id,revision_id,manifest_id),
 FOREIGN KEY(contract_catalog_id,contract_revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 CHECK((target_kind='contract')=(contract_id IS NOT NULL)),
 CHECK((contract_id IS NULL)=(contract_catalog_id IS NULL) AND(contract_id IS NULL)=(contract_revision_id IS NULL)),
 CHECK((target_kind='delivery_bucket')=(bucket_from IS NOT NULL) AND(bucket_from IS NULL)=(bucket_to IS NULL)
  AND(bucket_from IS NULL)=(bucket_convention IS NULL)),
 CHECK(bucket_from IS NULL OR bucket_from<bucket_to AND bucket_convention='whole_point_delivery_contained_half_open_v1')
);
-- Curve revisions here can be compiled recipe identities. They are anchored to
-- the retained graph manifest, not falsely presented as persisted definitions.
CREATE TABLE risk.job_scenario_points (
 job_id uuid NOT NULL,axis_id text NOT NULL,curve_catalog_id uuid NOT NULL,curve_revision_id uuid NOT NULL,curve_code text NOT NULL,
 point_id uuid NOT NULL CHECK(point_id<>'00000000-0000-0000-0000-000000000000'::uuid),
 PRIMARY KEY(job_id,axis_id,point_id),
 FOREIGN KEY(job_id,axis_id,curve_catalog_id,curve_revision_id,curve_code)
  REFERENCES risk.job_scenario_axes(job_id,axis_id,curve_catalog_id,curve_revision_id,curve_code)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['job_scenario_axes','job_scenario_points'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON risk.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_job_input BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION risk.retain_axes(job risk.jobs,axes jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE axis jsonb;factor jsonb;target jsonb;point jsonb;command jsonb;group_name text;selected_axis_id text;market_revision uuid;manifest text;
 curve_catalog uuid;curve_revision uuid;curve_code text;contract_catalog uuid;contract_revision uuid;contract_id uuid;
 bucket_from date;bucket_to date;bucket_convention text;amount numeric;
BEGIN
 IF jsonb_typeof(axes) IS DISTINCT FROM 'array' OR jsonb_array_length(axes) NOT BETWEEN 1 AND 4112
  OR jsonb_typeof(job.request->'bumps') IS DISTINCT FROM 'array' OR jsonb_array_length(job.request->'bumps') NOT BETWEEN 1 AND 4096
  OR jsonb_typeof(job.request->'stress') IS DISTINCT FROM 'array' OR jsonb_array_length(job.request->'stress')>16
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
  FOR command IN SELECT value FROM jsonb_array_elements(job.request->group_name) LOOP
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
   SELECT 1 FROM jsonb_array_elements((job.request->'bumps')||(job.request->'stress')) selected_command(value)
   WHERE jsonb_strip_nulls(selected_command.value->'factor')=a.factor))
  OR(SELECT count(*)<>count(DISTINCT jsonb_strip_nulls(value->'factor')) FROM jsonb_array_elements(job.request->'bumps'))
 THEN RAISE EXCEPTION 'risk_axis_input';END IF;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 OR check_violation OR foreign_key_violation OR unique_violation OR not_null_violation THEN RAISE EXCEPTION 'risk_axis_input';
END$$;
ALTER FUNCTION risk.retain_axes(risk.jobs,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_axes(risk.jobs,jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION risk.retain_connected(job risk.jobs,markets jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE market jsonb;recipe jsonb;sample jsonb;observation jsonb;revision uuid;sample_id uuid;selected_sample_at timestamptz;source text;contract uuid;pin uuid;series uuid;use_name text;
BEGIN
 IF job.request->>'convention' IS DISTINCT FROM 'portfolio_instrument_repricing_v1' OR jsonb_typeof(markets) IS DISTINCT FROM 'array'
  OR jsonb_array_length(markets) NOT BETWEEN 1 AND 65 THEN RAISE EXCEPTION 'risk_graph_input';END IF;
 FOR market IN SELECT value FROM jsonb_array_elements(markets) LOOP
  IF jsonb_typeof(market) IS DISTINCT FROM 'object' OR jsonb_typeof(market->'recipes') IS DISTINCT FROM 'array'
   OR jsonb_typeof(market->'samples') IS DISTINCT FROM 'array' OR coalesce(market->>'manifestId','') !~ '^[0-9a-f]{64}$'
   OR jsonb_array_length(market->'recipes') NOT BETWEEN 0 AND 4096 OR jsonb_array_length(market->'samples') NOT BETWEEN 0 AND 100000
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
 -- Every supplemental frame is a consumed prepared market. The primary frame
 -- also anchors operation authority when the selected books hold only a
 -- supplemental market. The worker verifies the exact held partition set.
 IF EXISTS(SELECT 1 FROM risk.job_markets m WHERE m.job_id=job.id AND NOT EXISTS(
   SELECT 1 FROM risk.job_connected_markets c WHERE(c.job_id,c.revision_id)=(m.job_id,m.revision_id)))
 THEN RAISE EXCEPTION 'risk_graph_input';END IF;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'risk_graph_input';
END$$;
ALTER FUNCTION risk.retain_connected(risk.jobs,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_connected(risk.jobs,jsonb) FROM PUBLIC;

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
 IF request->>'convention' IS DISTINCT FROM 'portfolio_instrument_repricing_v1'
  OR request->>'numericalPolicy' IS DISTINCT FROM 'exact_rational_portfolio_repricing_v1'
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
 FOR entry IN SELECT key,value FROM jsonb_each(frame->'supplementalMarkets') LOOP
  PERFORM risk.retain_market(job,entry.key::uuid,entry.value);
 END LOOP;
 PERFORM risk.retain_connected(job,frame->'connectedMarkets');
 PERFORM risk.retain_axes(job,frame->'scenarioAxes');
 job=risk.owner(new_id);
 RETURN risk.summary(job);
END$$;

COMMIT;
