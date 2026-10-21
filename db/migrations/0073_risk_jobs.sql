-- Durable execution of the existing bounded futures-risk operation.
-- No workers, principals, credentials or scheduled work are provisioned here.
BEGIN;
CREATE ROLE openetrm_risk_worker NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE SCHEMA risk;
CREATE TABLE risk.workers (
 login name PRIMARY KEY, principal_id uuid NOT NULL REFERENCES security.principals(id),
 tenant_id uuid NOT NULL, workspace_id uuid NOT NULL, catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id), enabled boolean NOT NULL,
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id)
);
CREATE TRIGGER worker_authority_change BEFORE INSERT OR UPDATE OR DELETE ON risk.workers
 FOR EACH STATEMENT EXECUTE FUNCTION security.guard_control_change();
CREATE TABLE risk.jobs (
 id uuid PRIMARY KEY, catalog_id uuid NOT NULL, request_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id), tenant_id uuid NOT NULL, workspace_id uuid NOT NULL,
 delegation_id uuid REFERENCES security.agent_delegations(id), context_id uuid NOT NULL, revision_id uuid NOT NULL,origin_close_id uuid,
 valuation_at ref_data.instant NOT NULL, request jsonb NOT NULL, frame jsonb NOT NULL, security_generation bigint NOT NULL,
 created_at ref_data.instant NOT NULL, state text NOT NULL CHECK(state IN('queued','running','completed','failed','cancellation_requested','cancelled')),
 attempt integer NOT NULL CHECK(attempt>=0), worker_login name REFERENCES risk.workers(login), lease_token uuid,
 lease_expires_at timestamptz, attempt_security_generation bigint, started_at timestamptz, finished_at timestamptz,
 result jsonb, error_code text CHECK(error_code ~ '^[a-z_]{1,64}$'),
 UNIQUE(tenant_id,workspace_id,actor_id,request_id), UNIQUE(catalog_id,id), UNIQUE(catalog_id,id,revision_id),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(catalog_id,context_id) REFERENCES trading.contexts(catalog_id,context_id),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES market_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,origin_close_id) REFERENCES market_data.market_closes(catalog_id,id),
 CHECK(jsonb_typeof(request)='object' AND jsonb_typeof(frame)='object'),
 CHECK((state IN('completed','failed','cancelled'))=(finished_at IS NOT NULL)),
 CHECK((state='completed')=(result IS NOT NULL)),
 CHECK((state IN('running','cancellation_requested'))=(lease_token IS NOT NULL)),
 CHECK((lease_token IS NULL)=(lease_expires_at IS NULL)), CHECK((lease_token IS NULL)=(worker_login IS NULL))
);
CREATE INDEX risk_jobs_claim ON risk.jobs(created_at,id) WHERE state IN('queued','running');
CREATE INDEX risk_jobs_owner ON risk.jobs(tenant_id,workspace_id,actor_id,created_at DESC);
CREATE TABLE risk.job_books (
 catalog_id uuid NOT NULL, job_id uuid NOT NULL, book_id uuid NOT NULL,
 PRIMARY KEY(job_id,book_id), FOREIGN KEY(catalog_id,job_id) REFERENCES risk.jobs(catalog_id,id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id)
);
CREATE TABLE risk.job_observations (
 catalog_id uuid NOT NULL, job_id uuid NOT NULL, observation_id uuid NOT NULL,
 PRIMARY KEY(job_id,observation_id), FOREIGN KEY(catalog_id,job_id) REFERENCES risk.jobs(catalog_id,id),
 FOREIGN KEY(catalog_id,observation_id) REFERENCES market_data.observations(catalog_id,id)
);
CREATE TABLE risk.job_contracts (
 catalog_id uuid NOT NULL, job_id uuid NOT NULL, revision_id uuid NOT NULL, curve_code ref_data.code NOT NULL, contract_id uuid NOT NULL,
 PRIMARY KEY(job_id,curve_code,contract_id), FOREIGN KEY(catalog_id,job_id,revision_id) REFERENCES risk.jobs(catalog_id,id,revision_id),
 FOREIGN KEY(catalog_id,revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE FUNCTION risk.guard_inputs() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR (to_jsonb(NEW)-ARRAY['state','attempt','worker_login','lease_token','lease_expires_at','attempt_security_generation','started_at','finished_at','result','error_code'])
  IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['state','attempt','worker_login','lease_token','lease_expires_at','attempt_security_generation','started_at','finished_at','result','error_code']) THEN
  RAISE EXCEPTION 'risk_job_immutable' USING ERRCODE='55000';
 END IF;RETURN NEW;
END$$;
CREATE TRIGGER immutable_job_inputs BEFORE UPDATE OR DELETE ON risk.jobs FOR EACH ROW EXECUTE FUNCTION risk.guard_inputs();
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['workers','jobs','job_books','job_observations','job_contracts'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON risk.%I TO openetrm_security_owner',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['job_books','job_observations','job_contracts'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_job_input BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
GRANT USAGE ON SCHEMA risk TO openetrm_runtime,openetrm_risk_worker,openetrm_security_owner;

-- Only the tables consumed by PreviewDatabase, FuturesStore and KnowledgeStore
-- are readable. Preserve every current restrictive/permissive SELECT policy,
-- including the per-row authority checks retained by migrations 0041 and 0044.
-- Authoring commands, source documents and unrelated job stores stay private.
GRANT USAGE ON SCHEMA security,ref_data,market_data,trading TO openetrm_risk_worker;
DO $$ DECLARE p record;tables record;table_name text;BEGIN
 FOR tables IN SELECT * FROM(VALUES
  ('ref_data',ARRAY['revisions','currencies','dimensions','units','unit_conversions','commodities','measurement_bases',
   'quantity_specifications','quote_specifications','value_specifications','locations','clock_rules','calendars','calendar_days',
   'commodity_units','location_types','commodity_locations','exchanges','clearing_houses','exchange_clearing_houses',
   'interest_rate_definitions','price_index_definitions','sources','product_evidence','delivery_profiles','settlement_targets',
   'cash_rounding_conventions','futures_products','futures_series','futures_execution_policies','futures_milestones',
   'futures_lot_resolutions','futures_product_terms','futures_basis_settlements']),
  ('market_data',ARRAY['revisions','contracts','curves','series','curve_members','curve_inputs','constituent_mappings',
   'rate_curves','rate_curve_points','revision_periods','index_fixing_observations','contract_intervals','curve_blocks',
   'shaped_curves','shaped_curve_blocks','observations','temporal_contexts']),
  ('trading',ARRAY['books','portfolios','trade_revisions','trades','booking_facts','legal_entities','counterparties','traders',
   'trade_references','instrument_references','instruments','futures_revision_markets','knowledge_receipts'])
 )v(schema_name,table_names) LOOP
  FOREACH table_name IN ARRAY tables.table_names LOOP
   IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname=tables.schema_name AND tablename=table_name
    AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' AND permissive='PERMISSIVE')
   THEN RAISE EXCEPTION 'risk_worker_policy_missing: %.%',tables.schema_name,table_name;END IF;
   FOR p IN SELECT * FROM pg_policies WHERE schemaname=tables.schema_name AND tablename=table_name
    AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' LOOP
    EXECUTE format('CREATE POLICY %I ON %I.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)',
     'risk_worker_'||p.policyname,p.schemaname,p.tablename,p.permissive,p.qual);
   END LOOP;
   EXECUTE format('GRANT SELECT ON %I.%I TO openetrm_risk_worker',tables.schema_name,table_name);
  END LOOP;
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.market_access(uuid,text),security.reference_access(uuid,text),
 security.market_fact_visible(uuid,uuid,uuid),security.reference_version_visible(uuid,uuid),
 security.trading_visible(uuid,integer,timestamptz),security.trading_context_visible(uuid,uuid),security.current_write_context()
 TO openetrm_risk_worker;

CREATE FUNCTION risk.actor_permission(actor uuid,tenant uuid,workspace uuid,delegation uuid,permission text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE kind text;d security.agent_delegations;at timestamptz=clock_timestamp();
BEGIN
 IF NOT security.principal_has_permission(actor,tenant,workspace,permission,at) THEN RETURN false;END IF;
 SELECT p.kind INTO kind FROM security.principals p WHERE p.id=actor;
 IF kind='agent' THEN
  SELECT * INTO d FROM security.agent_delegations WHERE id=delegation;
  RETURN d.id IS NOT NULL AND(d.agent_id,d.tenant_id,d.workspace_id)=(actor,tenant,workspace) AND NOT d.revoked
   AND d.granted_from<=at AND at<d.granted_to
   AND security.principal_has_permission(d.delegator_id,tenant,workspace,permission,at)
   AND security.principal_has_permission(d.delegator_id,tenant,workspace,'CreateDelegations',at)
   AND EXISTS(SELECT 1 FROM security.delegation_permissions dp WHERE dp.delegation_id=d.id AND dp.permission=actor_permission.permission);
 END IF;RETURN delegation IS NULL;
END$$;
CREATE FUNCTION risk.authorized(job risk.jobs) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(job.catalog_id,job.tenant_id,job.workspace_id))
 AND EXISTS(SELECT 1 FROM market_data.revisions r JOIN security.reference_catalog_scopes s ON s.catalog_id=r.reference_catalog_id
  WHERE(r.catalog_id,r.id,s.tenant_id,s.workspace_id)=(job.catalog_id,job.revision_id,job.tenant_id,job.workspace_id))
 AND NOT EXISTS(SELECT 1 FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario']) permission
  WHERE NOT risk.actor_permission(job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,permission));
$$;
CREATE FUNCTION risk.worker() RETURNS risk.workers LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor risk.workers;
BEGIN
 IF current_setting('transaction_isolation')<>'read committed' THEN RAISE EXCEPTION 'risk_job_isolation' USING ERRCODE='25000';END IF;
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO actor FROM risk.workers WHERE login=session_user AND enabled;
 IF actor.login IS NULL OR NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
  OR pg_has_role(session_user,'openetrm_runtime','MEMBER') OR pg_has_role(session_user,'openetrm_authenticator','MEMBER')
  OR pg_has_role(session_user,'openetrm_security_owner','MEMBER') OR pg_has_role(session_user,'openetrm_scheduler','MEMBER')
  OR pg_has_role(session_user,'openetrm_reference_importer','MEMBER') OR pg_has_role(session_user,'openetrm_collector','MEMBER')
  OR NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=session_user AND rolcanlogin
   AND NOT(rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole))
  OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema' AND pg_has_role(session_user,c.relowner,'MEMBER'))
  OR EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname<>'information_schema'
   AND pg_has_role(session_user,n.nspowner,'MEMBER'))
  OR EXISTS(SELECT 1 FROM pg_database d WHERE d.datname=current_database() AND pg_has_role(session_user,d.datdba,'MEMBER'))
  OR NOT EXISTS(SELECT 1 FROM security.principals WHERE id=actor.principal_id AND kind='service' AND enabled)
  OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id))
  OR EXISTS(SELECT 1 FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario']) p
   WHERE NOT security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,p,clock_timestamp()))
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN actor;
END$$;
CREATE FUNCTION risk.worker_read() RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM risk.workers w JOIN security.principals p ON p.id=w.principal_id AND p.kind='service' AND p.enabled
  JOIN security.request_contexts c ON(c.runtime_login,c.tenant_id,c.workspace_id,c.market_catalog_id)=(w.login,w.tenant_id,w.workspace_id,w.catalog_id)
  JOIN security.market_catalog_scopes s ON(s.catalog_id,s.tenant_id,s.workspace_id)=(w.catalog_id,w.tenant_id,w.workspace_id)
  WHERE w.login=session_user AND w.enabled AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.expires_at>clock_timestamp() AND NOT EXISTS(SELECT 1 FROM unnest(ARRAY['ReadMarketData','ReadTrades','RunScenario']) permission
    WHERE NOT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,permission,clock_timestamp())));
$$;
DO $$ DECLARE t record;BEGIN
 FOR t IN SELECT DISTINCT schemaname,tablename FROM pg_policies WHERE schemaname IN('ref_data','market_data','trading')
  AND 'openetrm_risk_worker'=ANY(roles) AND cmd='SELECT' LOOP
  EXECUTE format('CREATE POLICY risk_worker_service ON %I.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t.schemaname,t.tablename);
 END LOOP;
END$$;
CREATE FUNCTION risk.owner(job_id uuid) RETURNS risk.jobs LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;c security.request_contexts;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 SELECT * INTO job FROM risk.jobs WHERE id=job_id AND(actor_id,tenant_id,workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id);
 IF job.id IS NULL OR NOT risk.authorized(job) OR NOT security.has_permission('ReadTrades') OR NOT security.has_permission('RunScenario')
  OR NOT security.market_access(job.catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN job;
END$$;
CREATE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb LANGUAGE sql SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('jobId',job.id,'requestId',job.request_id,'state',job.state,'contextId',job.context_id,
  'valuationAt',job.valuation_at,'createdAt',job.created_at,'startedAt',job.started_at,'finishedAt',job.finished_at,
  'attempt',job.attempt,'errorCode',job.error_code,'engineVersion',job.request->>'engineVersion');
$$;
CREATE FUNCTION risk.submit(catalog uuid,operation uuid,context uuid,revision uuid,valuation_at timestamptz,request jsonb,frame jsonb)
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
  OR frame ? 'swapMarketSelections' AND frame->'swapMarketSelections' NOT IN('null'::jsonb,'{}'::jsonb)
 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements_text(frame->'frozenObservationIds') frozen(value)
  WHERE NOT(frame->'observationIds' @> jsonb_build_array(frozen.value)))
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(frame->'frozenObservationIds'))
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
 job=risk.owner(new_id);
 RETURN risk.summary(job);
END$$;
CREATE FUNCTION risk.read(job_id uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;result jsonb;
BEGIN
 job=risk.owner(job_id);result=risk.summary(job)||jsonb_build_object('result',job.result);
 PERFORM risk.owner(job_id);RETURN result;
END$$;
CREATE FUNCTION risk.list(catalog uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;job_id uuid;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(jsonb_agg(risk.summary(j) ORDER BY j.created_at DESC,j.id),'[]'::jsonb) INTO result FROM
 (SELECT * FROM risk.jobs WHERE catalog_id=catalog AND(actor_id,tenant_id,workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
  AND risk.authorized(jobs) ORDER BY created_at DESC,id LIMIT 50) j;
 FOR job_id IN SELECT(value->>'jobId')::uuid FROM jsonb_array_elements(result) LOOP
  PERFORM risk.owner(job_id);
 END LOOP;
 IF NOT security.has_permission('RunScenario') OR NOT security.trading_access(catalog,'ReadTrades')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION risk.claim(lease_seconds integer,engine_version text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor risk.workers;job risk.jobs;at timestamptz;scanned integer;
BEGIN
 actor=risk.worker();IF lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 300 OR coalesce(length(engine_version),0) NOT BETWEEN 1 AND 256 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 FOR scanned IN 1..50 LOOP
  -- Claim one row at a time. A cursor over multiple FOR UPDATE rows can lock
  -- prefetched work and unnecessarily starve another worker replica.
  SELECT * INTO job FROM risk.jobs j WHERE(j.catalog_id,j.tenant_id,j.workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id)
   AND j.request->>'engineVersion'=engine_version AND(j.state='queued' OR j.state='running' AND j.lease_expires_at<=clock_timestamp())
   ORDER BY j.created_at,j.id LIMIT 1 FOR UPDATE SKIP LOCKED;
  IF NOT FOUND THEN RETURN NULL;END IF;
  IF job.security_generation<>(SELECT generation FROM security.epoch) OR NOT risk.authorized(job) THEN
   UPDATE risk.jobs SET state='failed',finished_at=clock_timestamp(),error_code='risk_job_authority_changed',worker_login=NULL,lease_token=NULL,lease_expires_at=NULL WHERE id=job.id;CONTINUE;
  END IF;
  at=clock_timestamp();
  UPDATE risk.jobs SET state='running',attempt=attempt+1,worker_login=session_user,lease_token=gen_random_uuid(),lease_expires_at=at+make_interval(secs=>lease_seconds),
   attempt_security_generation=(SELECT generation FROM security.epoch),started_at=coalesce(started_at,at) WHERE id=job.id RETURNING * INTO job;
  RETURN risk.summary(job)||jsonb_build_object('leaseToken',job.lease_token,'leaseExpiresAt',job.lease_expires_at,'catalogId',job.catalog_id,
   'revisionId',job.revision_id,'tenantId',job.tenant_id,'workspaceId',job.workspace_id,'request',job.request,'frame',job.frame);
 END LOOP;RETURN NULL;
END$$;
CREATE FUNCTION risk.owned_attempt(job_id uuid,token uuid,allow_cancel boolean,lock_row boolean) RETURNS risk.jobs LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor risk.workers;job risk.jobs;
BEGIN
 actor=risk.worker();
 IF lock_row THEN
  SELECT * INTO job FROM risk.jobs WHERE id=job_id AND(catalog_id,tenant_id,workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id) FOR UPDATE;
 ELSE
  SELECT * INTO job FROM risk.jobs WHERE id=job_id AND(catalog_id,tenant_id,workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id);
 END IF;
 IF job.id IS NULL OR job.worker_login IS DISTINCT FROM session_user OR job.lease_token IS DISTINCT FROM token OR token IS NULL
  OR job.lease_expires_at<=clock_timestamp() OR job.state NOT IN('running','cancellation_requested') THEN RAISE EXCEPTION 'risk_job_fence' USING ERRCODE='42501';END IF;
 IF job.security_generation<>(SELECT generation FROM security.epoch) OR job.attempt_security_generation<>(SELECT generation FROM security.epoch)
  OR NOT risk.authorized(job) THEN RAISE EXCEPTION 'risk_job_authority_changed' USING ERRCODE='42501';END IF;
 IF NOT allow_cancel AND job.state='cancellation_requested' THEN RAISE EXCEPTION 'risk_job_cancelled';END IF;
 RETURN job;
END$$;
CREATE FUNCTION risk.bind(job_id uuid,token uuid) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;context_token uuid=gen_random_uuid();
BEGIN
 -- No row lock across the subsequent input read: heartbeat and cancellation
 -- remain available while a large pinned portfolio is loaded.
 job=risk.owned_attempt(job_id,token,false,false);
 INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,delegation_id,runtime_login,expires_at)
 VALUES(context_token,job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,session_user,least(job.lease_expires_at,clock_timestamp()+interval '60 seconds'));
 PERFORM security.use_context(context_token);PERFORM security.use_market_context(job.catalog_id,job.context_id);PERFORM security.use_trading_context(job.catalog_id,job.valuation_at);
 RETURN context_token;
END$$;
CREATE FUNCTION risk.heartbeat(job_id uuid,token uuid,lease_seconds integer) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;
BEGIN
 job=risk.owned_attempt(job_id,token,true,true);IF lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 300 THEN RAISE EXCEPTION 'risk_job_input';END IF;
 UPDATE risk.jobs SET lease_expires_at=clock_timestamp()+make_interval(secs=>lease_seconds) WHERE id=job.id;RETURN job.state;
END$$;
CREATE FUNCTION risk.finish(job_id uuid,token uuid,payload jsonb,failure text) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;terminal text;
BEGIN
 job=risk.owned_attempt(job_id,token,true,true);
 IF job.state='cancellation_requested' THEN RETURN job.state;END IF;
 IF(failure IS NULL)=(payload IS NULL) OR failure IS NOT NULL AND failure!~'^[a-z_]{1,64}$'
  OR payload IS NOT NULL AND jsonb_typeof(payload)<>'object' THEN RAISE EXCEPTION 'risk_job_input';END IF;
 IF pg_column_size(payload)>32000000 THEN RAISE EXCEPTION 'risk_result_limit';END IF;
 terminal=CASE WHEN failure IS NULL THEN 'completed' ELSE 'failed' END;
 UPDATE risk.jobs SET state=terminal,result=payload,error_code=failure,finished_at=clock_timestamp(),worker_login=NULL,lease_token=NULL,lease_expires_at=NULL WHERE id=job.id;
 -- Expiring grants and leases are clock boundaries even without an epoch write.
 PERFORM risk.worker();
 IF NOT risk.authorized(job) THEN RAISE EXCEPTION 'risk_job_authority_changed' USING ERRCODE='42501';END IF;
 IF job.lease_expires_at<=clock_timestamp() THEN RAISE EXCEPTION 'risk_job_fence' USING ERRCODE='42501';END IF;
 RETURN terminal;
END$$;
CREATE FUNCTION risk.cancel(job_id uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;
BEGIN
 job=risk.owner(job_id);SELECT * INTO job FROM risk.jobs WHERE id=job_id FOR UPDATE;
 PERFORM risk.owner(job_id);
 IF job.state='queued' THEN UPDATE risk.jobs SET state='cancelled',finished_at=clock_timestamp() WHERE id=job_id RETURNING * INTO job;
 ELSIF job.state='running' THEN UPDATE risk.jobs SET state='cancellation_requested' WHERE id=job_id RETURNING * INTO job;END IF;
 RETURN risk.summary(job);
END$$;
CREATE FUNCTION risk.acknowledge_cancel(job_id uuid,token uuid) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;actor risk.workers;
BEGIN
 actor=risk.worker();
 SELECT * INTO job FROM risk.jobs WHERE id=job_id AND(catalog_id,tenant_id,workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id) FOR UPDATE;
 -- The same worker may confirm that its computation stopped after its lease
 -- expired. This acknowledgment publishes no financial result and cannot revive
 -- cancelled authority. Lease expiry alone never establishes physical stop.
 IF job.id IS NULL OR job.worker_login IS DISTINCT FROM session_user OR job.lease_token IS DISTINCT FROM token OR token IS NULL
  OR job.state<>'cancellation_requested' THEN RAISE EXCEPTION 'risk_job_fence' USING ERRCODE='42501';END IF;
 UPDATE risk.jobs SET state='cancelled',finished_at=clock_timestamp(),worker_login=NULL,lease_token=NULL,lease_expires_at=NULL WHERE id=job_id;RETURN 'cancelled';
END$$;
DO $$ DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='risk' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb),risk.read(uuid),risk.list(uuid),risk.cancel(uuid) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION risk.worker_read(),risk.claim(integer,text),risk.bind(uuid,uuid),risk.heartbeat(uuid,uuid,integer),risk.finish(uuid,uuid,jsonb,text),risk.acknowledge_cancel(uuid,uuid) TO openetrm_risk_worker;
COMMIT;
