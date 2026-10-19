-- Bounded operator-provisioned PJM/ERCOT delivery-price collection.
-- No credentials, sources, service accounts or enabled schedules are seeded.
BEGIN;
CREATE ROLE openetrm_collector NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE SCHEMA power_data;
CREATE TABLE power_data.collectors(
 login name PRIMARY KEY,principal_id uuid NOT NULL REFERENCES security.principals(id),
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 UNIQUE(login,catalog_id)
);
CREATE TABLE power_data.sources(
 id uuid PRIMARY KEY,catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),name text NOT NULL CHECK(length(name) BETWEEN 1 AND 160),
 provider text NOT NULL,dataset text NOT NULL,market text NOT NULL,point text NOT NULL CHECK(length(point) BETWEEN 1 AND 80),minutes integer NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,location_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 currency_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 CHECK((provider,dataset,market,minutes) IN(('PJM','da_hrl_lmps','DA',60),('PJM','rt_hrl_lmps','RT',60),('ERCOT','np4-190-cd/dam_stlmnt_pnt_prices','DA',60),('ERCOT','np6-905-cd/spp_node_zone_hub','RT',15))),
 UNIQUE(catalog_id,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id),UNIQUE(id,catalog_id)
);
CREATE TABLE power_data.jobs(
 id uuid PRIMARY KEY,generation uuid NOT NULL,name text NOT NULL,collector_login name NOT NULL, catalog_id uuid NOT NULL,
 provider text NOT NULL CHECK(provider IN('PJM','ERCOT')),enabled boolean NOT NULL,schedule_enabled boolean NOT NULL,
 cron text NOT NULL CHECK(cron='0 8 * * *'),schedule_zone text NOT NULL CHECK(schedule_zone='UTC'),
 from_day integer NOT NULL CHECK(from_day BETWEEN -31 AND -1),through_day integer NOT NULL CHECK(through_day BETWEEN -31 AND -1 AND through_day>=from_day),
 FOREIGN KEY(collector_login,catalog_id) REFERENCES power_data.collectors(login,catalog_id),UNIQUE(id,catalog_id)
);
CREATE FUNCTION power_data.change_job_generation() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$BEGIN NEW.generation=gen_random_uuid();RETURN NEW;END$$;
CREATE TRIGGER change_generation BEFORE UPDATE ON power_data.jobs FOR EACH ROW EXECUTE FUNCTION power_data.change_job_generation();
CREATE TABLE power_data.job_sources(
 job_id uuid NOT NULL,source_id uuid NOT NULL,catalog_id uuid NOT NULL,
 PRIMARY KEY(job_id,source_id),FOREIGN KEY(job_id,catalog_id) REFERENCES power_data.jobs(id,catalog_id),FOREIGN KEY(source_id,catalog_id) REFERENCES power_data.sources(id,catalog_id)
);
CREATE TABLE power_data.attempts(
 id uuid PRIMARY KEY,job_id uuid NOT NULL REFERENCES power_data.jobs(id),generation uuid NOT NULL,worker_pid integer NOT NULL,security_generation bigint NOT NULL,
 from_date date NOT NULL,through_date date NOT NULL,CHECK(through_date>=from_date AND through_date-from_date<31),
 started_at timestamptz NOT NULL,state text NOT NULL CHECK(state IN('running','succeeded','failed','interrupted')),finished_at timestamptz,error_code text,
 CHECK((state='running')=(finished_at IS NULL))
);
CREATE TABLE power_data.batches(
 sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,id uuid NOT NULL UNIQUE REFERENCES power_data.attempts(id),
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),publication_xid xid8 NOT NULL
);
CREATE TABLE power_data.receipts(
 sequence bigint PRIMARY KEY REFERENCES power_data.batches(sequence),known_at timestamptz NOT NULL
);
CREATE TABLE power_data.prices(
 batch_id uuid NOT NULL REFERENCES power_data.batches(id),source_id uuid NOT NULL REFERENCES power_data.sources(id),
 starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,price ref_data.exact_number NOT NULL,provider_revision integer CHECK(provider_revision>0),
 PRIMARY KEY(batch_id,source_id,starts_at),CHECK(ends_at>starts_at)
);
CREATE TABLE power_data.operating_days(
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,clock_code ref_data.code NOT NULL,operating_date date NOT NULL,
 starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,clock_fingerprint text NOT NULL CHECK(clock_fingerprint ~ '^[0-9a-f]{64}$'),
 PRIMARY KEY(reference_catalog_id,reference_revision_id,clock_code,operating_date),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 CHECK(ends_at>starts_at AND ends_at-starts_at IN(interval '23 hours',interval '24 hours',interval '25 hours'))
);
CREATE TABLE power_data.request_gates(provider text PRIMARY KEY CHECK(provider IN('PJM','ERCOT')),requests_per_minute integer NOT NULL CHECK(requests_per_minute BETWEEN 1 AND 120),next_at timestamptz NOT NULL);

CREATE FUNCTION power_data.worker_catalog() RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE worker power_data.collectors;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO worker FROM power_data.collectors WHERE login=session_user;
 IF worker.login IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=(worker.catalog_id,worker.tenant_id,worker.workspace_id)) OR NOT EXISTS(SELECT 1 FROM security.principals p JOIN security.workspace_memberships m ON m.principal_id=p.id WHERE p.id=worker.principal_id AND p.kind='service' AND p.enabled AND m.enabled AND(m.tenant_id,m.workspace_id)=(worker.tenant_id,worker.workspace_id))
 OR NOT security.principal_has_permission(worker.principal_id,worker.tenant_id,worker.workspace_id,'ImportObservations',clock_timestamp()) OR NOT security.principal_has_permission(worker.principal_id,worker.tenant_id,worker.workspace_id,'ReadMarketData',clock_timestamp()) THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 RETURN worker.catalog_id;
END$$;
CREATE FUNCTION power_data.worker_reference_read(reference_catalog uuid) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM power_data.worker_catalog();
 RETURN EXISTS(SELECT 1 FROM power_data.collectors actor JOIN security.reference_catalog_scopes scope ON(scope.tenant_id,scope.workspace_id)=(actor.tenant_id,actor.workspace_id) WHERE actor.login=session_user AND scope.catalog_id=reference_catalog);
END$$;
CREATE FUNCTION power_data.worker_lock() RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM power_data.worker_catalog();RETURN pg_try_advisory_lock(730849110);END$$;
CREATE FUNCTION power_data.require_lock() RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN IF NOT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND pid=pg_backend_pid() AND classid=0 AND objid=730849110 AND objsubid=1 AND granted) THEN RAISE EXCEPTION 'collection_fence' USING ERRCODE='42501';END IF;END$$;
CREATE FUNCTION power_data.validate_sources(job uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor power_data.collectors;selected power_data.jobs;members integer;accepted integer;
BEGIN
 PERFORM power_data.worker_catalog();SELECT * INTO actor FROM power_data.collectors WHERE login=session_user;
 SELECT * INTO selected FROM power_data.jobs WHERE id=job AND collector_login=session_user AND catalog_id=actor.catalog_id AND enabled;
 IF selected.id IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=(selected.catalog_id,actor.tenant_id,actor.workspace_id)) THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 SELECT count(*) INTO members FROM power_data.job_sources WHERE job_id=job;
 SELECT count(*) INTO accepted FROM power_data.job_sources m JOIN power_data.sources s ON s.id=m.source_id
 JOIN ref_data.revisions v ON(v.catalog_id,v.id)=(s.reference_catalog_id,s.reference_revision_id) AND v.state='sealed'
 JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(s.reference_catalog_id,s.reference_revision_id,s.currency_code) AND c.display_code='USD'
 JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quantity_code) AND q.dimension_code='energy'
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code) AND u.symbol='MWh'
 JOIN ref_data.clock_rules clock ON(clock.catalog_id,clock.revision_id,clock.code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND clock.kind='iana' AND clock.iana_zone=CASE s.provider WHEN 'PJM' THEN 'America/New_York' ELSE 'America/Chicago' END
 JOIN security.reference_catalog_scopes scope ON(scope.catalog_id,scope.tenant_id,scope.workspace_id)=(s.reference_catalog_id,actor.tenant_id,actor.workspace_id)
 WHERE m.job_id=job AND s.catalog_id=selected.catalog_id AND s.provider=selected.provider
 AND security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,'ReadMarketData',clock_timestamp());
 IF members=0 OR members<>accepted THEN RAISE EXCEPTION 'collection_reference_scope' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION power_data.begin_attempt(job uuid,first_day date,last_day date) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected power_data.jobs;token uuid=gen_random_uuid();catalog uuid;
BEGIN
 catalog=power_data.worker_catalog();PERFORM power_data.require_lock();PERFORM power_data.validate_sources(job);
 SELECT * INTO selected FROM power_data.jobs WHERE id=job AND catalog_id=catalog AND collector_login=session_user AND enabled FOR SHARE;
 IF selected.id IS NULL OR first_day IS NULL OR last_day IS NULL THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 IF (SELECT count(*) FROM power_data.job_sources m JOIN power_data.sources s ON s.id=m.source_id JOIN power_data.operating_days d ON(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.operating_date BETWEEN first_day AND last_day WHERE m.job_id=job)<>(SELECT count(*) FROM power_data.job_sources WHERE job_id=job)*(last_day-first_day+1) THEN RAISE EXCEPTION 'collection_calendar';END IF;
 UPDATE power_data.attempts SET state='interrupted',finished_at=clock_timestamp(),error_code='worker_interrupted' WHERE state='running' AND job_id IN(SELECT id FROM power_data.jobs WHERE collector_login=session_user);
 INSERT INTO power_data.attempts VALUES(token,job,selected.generation,pg_backend_pid(),(SELECT generation FROM security.epoch),first_day,last_day,clock_timestamp(),'running',NULL,NULL);
 RETURN token;
END$$;
CREATE FUNCTION power_data.reserve_request(provider_code text) RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE slot timestamptz;
BEGIN
 PERFORM power_data.worker_catalog();PERFORM power_data.require_lock();
 UPDATE power_data.request_gates SET next_at=greatest(clock_timestamp(),next_at)+interval '1 minute'/requests_per_minute WHERE provider=provider_code RETURNING next_at-interval '1 minute'/requests_per_minute INTO slot;
 IF NOT FOUND THEN RAISE EXCEPTION 'collection_rate';END IF;RETURN slot;
END$$;
CREATE FUNCTION power_data.finish_attempt(token uuid,payload jsonb,failure text) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE attempt power_data.attempts;job power_data.jobs;catalog uuid;item jsonb;row jsonb;source power_data.sources;
BEGIN
 catalog=power_data.worker_catalog();PERFORM power_data.require_lock();
 SELECT * INTO attempt FROM power_data.attempts WHERE id=token AND state='running' AND worker_pid=pg_backend_pid() AND security_generation=(SELECT generation FROM security.epoch) FOR UPDATE;
 SELECT * INTO job FROM power_data.jobs WHERE id=attempt.job_id AND catalog_id=catalog AND collector_login=session_user AND enabled AND generation=attempt.generation FOR SHARE;
 IF job.id IS NULL THEN RAISE EXCEPTION 'collection_fence' USING ERRCODE='42501';END IF;
 PERFORM power_data.validate_sources(job.id);
 IF failure IS NOT NULL THEN
  IF failure !~ '^[a-z_]{1,64}$' OR payload IS NOT NULL THEN RAISE EXCEPTION 'collection_input';END IF;
  UPDATE power_data.attempts SET state='failed',finished_at=clock_timestamp(),error_code=failure WHERE id=token;RETURN;
 END IF;
 IF payload IS NULL OR jsonb_typeof(payload)<>'array' OR jsonb_array_length(payload)<>(SELECT count(*) FROM power_data.job_sources WHERE job_id=job.id) OR pg_column_size(payload)>32000000 THEN RAISE EXCEPTION 'collection_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 INSERT INTO power_data.batches(id,catalog_id,publication_xid) VALUES(token,catalog,pg_current_xact_id());
 FOR item IN SELECT value FROM jsonb_array_elements(payload) LOOP
  SELECT s.* INTO source FROM power_data.sources s JOIN power_data.job_sources j ON j.source_id=s.id WHERE j.job_id=job.id AND s.id=(item->>'sourceId')::uuid AND s.catalog_id=catalog AND s.provider=job.provider;
  IF source.id IS NULL OR jsonb_typeof(item->'rows')<>'array' OR jsonb_array_length(item->'rows') NOT BETWEEN 1 AND 100000 THEN RAISE EXCEPTION 'collection_input';END IF;
  FOR row IN SELECT value FROM jsonb_array_elements(item->'rows') LOOP
   IF (row->>'end')::timestamptz-(row->>'start')::timestamptz<>make_interval(mins=>source.minutes) OR (row->>'price') IS NULL THEN RAISE EXCEPTION 'collection_input';END IF;
   IF (source.provider='PJM' AND ((row->>'revision') IS NULL OR (row->>'revision')::integer<1)) OR (source.provider='ERCOT' AND (row->>'revision') IS NOT NULL) THEN RAISE EXCEPTION 'collection_input';END IF;
   INSERT INTO power_data.prices VALUES(token,source.id,(row->>'start')::timestamptz,(row->>'end')::timestamptz,(row->>'price')::numeric,(row->>'revision')::integer);
  END LOOP;
 END LOOP;
 IF (SELECT count(DISTINCT source_id) FROM power_data.prices WHERE batch_id=token)<>jsonb_array_length(payload) THEN RAISE EXCEPTION 'collection_input';END IF;
 IF EXISTS(
  WITH expected AS(SELECT s.id source_id,d.starts_at+n*make_interval(mins=>s.minutes) starts_at,d.starts_at+(n+1)*make_interval(mins=>s.minutes) ends_at
   FROM power_data.job_sources m JOIN power_data.sources s ON s.id=m.source_id
   JOIN power_data.operating_days d ON(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.operating_date BETWEEN attempt.from_date AND attempt.through_date
   CROSS JOIN LATERAL generate_series(0,(extract(epoch FROM(d.ends_at-d.starts_at))/60/s.minutes)::integer-1)n WHERE m.job_id=job.id),
  actual AS(SELECT * FROM power_data.prices WHERE batch_id=token)
  SELECT 1 FROM expected e FULL JOIN actual a ON(a.source_id,a.starts_at,a.ends_at)=(e.source_id,e.starts_at,e.ends_at) WHERE e.source_id IS NULL OR a.source_id IS NULL
 ) OR (SELECT count(*) FROM power_data.prices WHERE batch_id=token)>100000 THEN RAISE EXCEPTION 'collection_coverage';END IF;
 -- Grant expiry is a clock boundary as well as a control-plane mutation.
 PERFORM power_data.validate_sources(job.id);
 UPDATE power_data.attempts SET state='succeeded',finished_at=clock_timestamp() WHERE id=token;
END$$;
CREATE FUNCTION power_data.certify(catalog uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE batch power_data.batches;at timestamptz;previous timestamptz;clock market_data.knowledge_clocks;origin_at timestamptz;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO clock FROM market_data.knowledge_clocks WHERE catalog_id=catalog FOR SHARE;
 IF clock.catalog_id IS NULL OR current_setting('track_commit_timestamp')<>'on' THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 IF pg_is_in_recovery() OR clock.system_identifier IS DISTINCT FROM (pg_control_system()).system_identifier::text OR clock.timeline_id IS DISTINCT FROM(pg_control_checkpoint()).timeline_id THEN RAISE EXCEPTION 'knowledge_timeline';END IF;
 origin_at=coalesce(clock.coverage_from,security.checked_commit_time(clock.origin_xid));
 SELECT r.known_at INTO previous FROM power_data.receipts r JOIN power_data.batches b USING(sequence) WHERE b.catalog_id=catalog ORDER BY sequence DESC LIMIT 1;
 FOR batch IN SELECT b.* FROM power_data.batches b LEFT JOIN power_data.receipts r USING(sequence) WHERE r.sequence IS NULL AND b.catalog_id=catalog ORDER BY sequence LOOP
  IF batch.publication_xid<clock.origin_xid THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
  at=security.checked_commit_time(batch.publication_xid);
  IF at<origin_at OR at<previous OR at>clock_timestamp() OR at<=clock.certified_through THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO power_data.receipts VALUES(batch.sequence,at);previous=at;
 END LOOP;
END$$;
-- Every shared-clock advancement first certifies committed collection batches.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_noncollection_date_context;
REVOKE ALL ON FUNCTION security.capture_noncollection_date_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 PERFORM power_data.certify(catalog);
 RETURN security.capture_noncollection_date_context(catalog,effective_date,requested_at);
END$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_date_context(uuid,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime;
CREATE FUNCTION power_data.source_config(job uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE catalog uuid;result jsonb;
BEGIN
 catalog=power_data.worker_catalog();
 SELECT jsonb_agg(to_jsonb(s)||jsonb_build_object('clock',to_jsonb(c))) INTO result
 FROM power_data.jobs j JOIN power_data.job_sources m ON m.job_id=j.id JOIN power_data.sources s ON s.id=m.source_id
 JOIN ref_data.clock_rules c ON(c.catalog_id,c.revision_id,c.code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code)
 JOIN power_data.collectors actor ON actor.login=session_user
 JOIN security.reference_catalog_scopes scope ON(scope.catalog_id,scope.tenant_id,scope.workspace_id)=(s.reference_catalog_id,actor.tenant_id,actor.workspace_id)
 WHERE j.id=job AND j.catalog_id=catalog AND j.collector_login=session_user AND j.enabled
 AND security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,'ReadMarketData',clock_timestamp());
 IF result IS NULL OR jsonb_array_length(result)<>(SELECT count(*) FROM power_data.job_sources WHERE job_id=job) THEN RAISE EXCEPTION 'collection_reference_scope' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION power_data.read_prices(source uuid,first_at timestamptz,last_at timestamptz,cutoff timestamptz) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected power_data.sources;result jsonb;context_id uuid;
BEGIN
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'collection_historical_context';END IF;
 SELECT * INTO selected FROM power_data.sources WHERE id=source;
 IF selected.id IS NULL OR NOT security.market_access(selected.catalog_id,'ReadMarketData') OR NOT security.reference_access(selected.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 IF first_at IS NULL OR last_at IS NULL OR cutoff IS NULL OR NOT isfinite(first_at) OR NOT isfinite(last_at) OR NOT isfinite(cutoff) OR first_at>=last_at OR last_at-first_at>interval '31 days 1 hour' OR cutoff>clock_timestamp() THEN RAISE EXCEPTION 'collection_window';END IF;
 context_id=security.capture_date_context(selected.catalog_id,(first_at AT TIME ZONE 'UTC')::date,cutoff);
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.starts_at),'[]'::jsonb) INTO result FROM(
  SELECT DISTINCT ON(p.starts_at) p.starts_at,p.ends_at,p.price::text price,p.provider_revision,r.known_at,b.id batch_id
  FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id JOIN power_data.receipts r USING(sequence)
  WHERE p.source_id=source AND p.starts_at>=first_at AND p.ends_at<=last_at AND r.known_at<=cutoff ORDER BY p.starts_at,b.sequence DESC
 )p;
 RETURN jsonb_build_object('source',to_jsonb(selected),'knownAt',cutoff,'contextId',context_id,'rows',result);
END$$;
-- Credentialed worker receives only scoped reads and bounded entry points.
DO $$DECLARE t text;f regprocedure;BEGIN
 FOREACH t IN ARRAY ARRAY['collectors','sources','jobs','job_sources','attempts','batches','receipts','prices','request_gates','operating_days'] LOOP
  EXECUTE format('ALTER TABLE power_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE power_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON power_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT ALL ON power_data.%I TO openetrm_security_owner',t);
 END LOOP;
 FOR f IN SELECT oid::regprocedure FROM pg_proc WHERE pronamespace='power_data'::regnamespace LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT USAGE ON SCHEMA power_data TO openetrm_security_owner,openetrm_runtime,openetrm_collector;
GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA power_data TO openetrm_security_owner;
GRANT EXECUTE ON FUNCTION power_data.worker_lock(),power_data.begin_attempt(uuid,date,date),power_data.reserve_request(text),power_data.finish_attempt(uuid,jsonb,text) TO openetrm_collector;
GRANT EXECUTE ON FUNCTION power_data.worker_catalog(),power_data.worker_reference_read(uuid),power_data.source_config(uuid) TO openetrm_collector;
GRANT EXECUTE ON FUNCTION power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz) TO openetrm_runtime;
GRANT SELECT ON power_data.sources,power_data.jobs,power_data.attempts TO openetrm_runtime;
CREATE POLICY runtime_read ON power_data.sources FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,'ReadMarketData') AND security.reference_access(reference_catalog_id,'ReadMarketData'));
CREATE POLICY runtime_read ON power_data.jobs FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,'ReadMarketData'));
CREATE POLICY runtime_read ON power_data.attempts FOR SELECT TO openetrm_runtime USING(job_id IN(SELECT id FROM power_data.jobs));
GRANT SELECT ON power_data.sources,power_data.jobs,power_data.job_sources,power_data.attempts,power_data.request_gates TO openetrm_collector;
CREATE POLICY collector_read ON power_data.sources FOR SELECT TO openetrm_collector USING(catalog_id=power_data.worker_catalog() AND power_data.worker_reference_read(reference_catalog_id));
CREATE POLICY collector_read ON power_data.jobs FOR SELECT TO openetrm_collector USING(catalog_id=power_data.worker_catalog() AND collector_login=session_user);
CREATE POLICY collector_read ON power_data.job_sources FOR SELECT TO openetrm_collector USING(catalog_id=power_data.worker_catalog() AND job_id IN(SELECT id FROM power_data.jobs) AND source_id IN(SELECT id FROM power_data.sources));
CREATE POLICY collector_read ON power_data.attempts FOR SELECT TO openetrm_collector USING(job_id IN(SELECT id FROM power_data.jobs));
CREATE POLICY collector_read ON power_data.request_gates FOR SELECT TO openetrm_collector USING(power_data.worker_catalog() IS NOT NULL);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['sources','job_sources','batches','receipts','prices','operating_days'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_fact BEFORE UPDATE OR DELETE ON power_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
COMMIT;
