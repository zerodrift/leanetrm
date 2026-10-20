-- Shared contract-date rules. No exchange terms or existing session drafts are imported.
BEGIN;
CREATE TABLE market_data.contract_date_definitions (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),id uuid NOT NULL,
 code text NOT NULL CHECK(code ~ '^[a-z][a-z0-9_]{0,31}$'),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,code)
);
CREATE TABLE market_data.contract_date_versions (
 catalog_id uuid NOT NULL,definition_id uuid NOT NULL,version integer NOT NULL CHECK(version BETWEEN 1 AND 64),
 generation integer NOT NULL CHECK(generation>0),state text NOT NULL CHECK(state IN('draft','published')),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),engine_version text NOT NULL CHECK(engine_version='contract_dates_v1'),
 validation_context_id uuid NOT NULL,publication_order integer,published_at timestamptz,publication_xid xid8,
 PRIMARY KEY(catalog_id,definition_id,version),UNIQUE(catalog_id,publication_order,publication_xid),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,definition_id) REFERENCES market_data.contract_date_definitions(catalog_id,id),
 FOREIGN KEY(catalog_id,validation_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 CHECK((state='published' AND publication_order IS NOT NULL AND publication_order>0 AND published_at IS NOT NULL AND publication_xid IS NOT NULL)
    OR(state='draft' AND publication_order IS NULL AND published_at IS NULL AND publication_xid IS NULL))
);
CREATE UNIQUE INDEX one_contract_date_draft ON market_data.contract_date_versions(catalog_id,definition_id) WHERE state='draft';
CREATE TABLE market_data.contract_date_contracts (
 catalog_id uuid NOT NULL,definition_id uuid NOT NULL,version integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 119),
 label text NOT NULL CHECK(length(label) BETWEEN 1 AND 80),cutoff_utc time(0) NOT NULL CHECK(extract(second FROM cutoff_utc)=0 AND cutoff_utc<time '24:00'),
 PRIMARY KEY(catalog_id,definition_id,version,ordinal),UNIQUE(catalog_id,definition_id,version,label),
 FOREIGN KEY(catalog_id,definition_id,version) REFERENCES market_data.contract_date_versions(catalog_id,definition_id,version)
);
CREATE TABLE market_data.contract_date_fields (
 catalog_id uuid NOT NULL,definition_id uuid NOT NULL,version integer NOT NULL,ordinal integer NOT NULL,
 role text NOT NULL CHECK(role IN('start','end','last_trading')),literal_date date,
 schedule_catalog_id uuid,schedule_id uuid,schedule_version integer,occurrence integer,business_days integer,
 PRIMARY KEY(catalog_id,definition_id,version,ordinal,role),
 FOREIGN KEY(catalog_id,definition_id,version,ordinal) REFERENCES market_data.contract_date_contracts(catalog_id,definition_id,version,ordinal),
 FOREIGN KEY(schedule_catalog_id,schedule_id,schedule_version) REFERENCES market_data.date_schedule_versions(catalog_id,schedule_id,version),
 CHECK((literal_date IS NOT NULL AND isfinite(literal_date) AND literal_date>=DATE '0001-01-01' AND literal_date<DATE '9999-12-31'
       AND schedule_catalog_id IS NULL AND schedule_id IS NULL AND schedule_version IS NULL AND occurrence IS NULL AND business_days IS NULL)
    OR(literal_date IS NULL AND schedule_catalog_id IS NOT NULL AND schedule_catalog_id=catalog_id AND schedule_id IS NOT NULL AND schedule_version IS NOT NULL
       AND occurrence IS NOT NULL AND occurrence BETWEEN 0 AND 9999 AND (business_days IS NULL OR business_days BETWEEN -10000 AND 10000 AND business_days<>0)))
);
CREATE TABLE market_data.contract_date_requests (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 operation text NOT NULL CHECK(operation IN('save','publish')),payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9A-F]{64}$'),
 definition_id uuid NOT NULL,version integer NOT NULL,generation integer NOT NULL CHECK(generation>0),state text NOT NULL CHECK(state IN('draft','published')),
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,definition_id,version) REFERENCES market_data.contract_date_versions(catalog_id,definition_id,version)
);
CREATE TABLE market_data.contract_date_knowledge_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES market_data.contract_date_versions(catalog_id,publication_order,publication_xid)
);
CREATE FUNCTION security.contract_date_schedule_visible(catalog uuid,schedule uuid,requested_version integer) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND EXISTS(SELECT 1 FROM market_data.date_schedule_versions d
 WHERE(d.catalog_id,d.schedule_id,d.version)=(catalog,schedule,requested_version) AND d.state='published'
 AND(d.calendar_catalog_id IS NULL OR security.reference_version_visible(d.calendar_catalog_id,d.calendar_revision_id))
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.knowledge_publications k ON(k.catalog_id,k.publication_order,k.publication_xid)=(d.catalog_id,d.publication_order,d.publication_xid)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
 AND t.catalog_id=catalog AND d.effective_from<=t.effective_on AND t.effective_on<d.effective_to AND k.known_from<=t.known_at)));
$$;
CREATE FUNCTION security.contract_date_visible(catalog uuid,definition uuid,requested_version integer) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND EXISTS(SELECT 1 FROM market_data.contract_date_versions v
 WHERE(v.catalog_id,v.definition_id,v.version)=(catalog,definition,requested_version)
 AND(v.state='published' OR security.current_write_context() AND(security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions')))
 AND NOT EXISTS(SELECT 1 FROM market_data.contract_date_fields f WHERE(f.catalog_id,f.definition_id,f.version)=(catalog,definition,requested_version)
   AND f.schedule_id IS NOT NULL AND NOT security.contract_date_schedule_visible(f.schedule_catalog_id,f.schedule_id,f.schedule_version))
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.contract_date_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog AND k.known_from<=t.known_at)));
$$;
CREATE FUNCTION market_data.guard_contract_dates() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE parent_state text;
BEGIN
 IF TG_TABLE_NAME IN('contract_date_definitions','contract_date_requests','contract_date_knowledge_receipts') THEN
  IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Contract-date identities and receipts are immutable' USING ERRCODE='55000';END IF;
 ELSIF TG_TABLE_NAME='contract_date_versions' THEN
  IF TG_OP='DELETE' OR(TG_OP='UPDATE' AND OLD.state='published') THEN RAISE EXCEPTION 'Published definitions are immutable' USING ERRCODE='55000';END IF;
  IF TG_OP='UPDATE' AND(NEW.catalog_id,NEW.definition_id,NEW.version) IS DISTINCT FROM(OLD.catalog_id,OLD.definition_id,OLD.version) THEN
   RAISE EXCEPTION 'Version identity is immutable' USING ERRCODE='55000';END IF;
  IF TG_OP='UPDATE' AND NEW.state='draft' AND NEW.generation<>OLD.generation+1 THEN RAISE EXCEPTION 'Stale generation' USING ERRCODE='40001';END IF;
  IF current_user<>pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid=TG_RELID))
   AND (NOT security.current_write_context() OR NOT security.market_access(NEW.catalog_id,CASE NEW.state WHEN 'published' THEN 'SealMarketDefinitions' ELSE 'WriteMarketDefinitions' END)) THEN
   RAISE EXCEPTION 'Contract-date authoring authority expired' USING ERRCODE='42501';END IF;
 ELSE
  IF TG_OP<>'INSERT' THEN
   SELECT state INTO parent_state FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=(OLD.catalog_id,OLD.definition_id,OLD.version) FOR UPDATE;
   IF current_user<>pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid=TG_RELID))
    AND (NOT security.market_access(OLD.catalog_id,'WriteMarketDefinitions') OR NOT security.contract_date_visible(OLD.catalog_id,OLD.definition_id,OLD.version)) THEN
    RAISE EXCEPTION 'Contract-date authoring authority expired' USING ERRCODE='42501';END IF;
   IF parent_state IS DISTINCT FROM 'draft' THEN RAISE EXCEPTION 'Published date fields cannot change or move' USING ERRCODE='55000';END IF;
  END IF;
  IF TG_OP<>'DELETE' THEN
   SELECT state INTO parent_state FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=(NEW.catalog_id,NEW.definition_id,NEW.version) FOR UPDATE;
   IF current_user<>pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid=TG_RELID))
    AND (NOT security.market_access(NEW.catalog_id,'WriteMarketDefinitions') OR NOT security.contract_date_visible(NEW.catalog_id,NEW.definition_id,NEW.version)) THEN
    RAISE EXCEPTION 'Contract-date authoring authority expired' USING ERRCODE='42501';END IF;
   IF parent_state IS DISTINCT FROM 'draft' THEN RAISE EXCEPTION 'Only draft fields can change' USING ERRCODE='55000';END IF;

  END IF;
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD;END IF;RETURN NEW;
END;$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['contract_date_definitions','contract_date_versions','contract_date_contracts','contract_date_fields','contract_date_requests','contract_date_knowledge_receipts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER guard_contract_dates BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_contract_dates()',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_update ON market_data.%I AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_delete ON market_data.%I AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context())',t);
  IF t='contract_date_definitions' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM market_data.contract_date_versions v WHERE v.catalog_id=contract_date_definitions.catalog_id AND v.definition_id=contract_date_definitions.id))',t);
  ELSIF t='contract_date_requests' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,''ReadMarketData'') AND security.contract_date_visible(catalog_id,definition_id,version))',t);
  ELSIF t='contract_date_knowledge_receipts' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData''))',t);
  ELSE
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.contract_date_visible(catalog_id,definition_id,version))',t);
  END IF;
  IF t<>'contract_date_knowledge_receipts' THEN
   EXECUTE format('GRANT INSERT ON market_data.%I TO openetrm_runtime',t);
   IF t='contract_date_requests' THEN
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(actor_id=security.custom_curve_actor(catalog_id,CASE operation WHEN ''save'' THEN ''WriteMarketDefinitions'' ELSE ''SealMarketDefinitions'' END) AND security.contract_date_visible(catalog_id,definition_id,version))',t);
   ELSIF t='contract_date_fields' THEN
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(security.market_access(catalog_id,''WriteMarketDefinitions'') AND (schedule_id IS NULL OR security.contract_date_schedule_visible(schedule_catalog_id,schedule_id,schedule_version)))',t);
   ELSE
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
   END IF;
  END IF;
  IF t IN('contract_date_versions','contract_date_contracts','contract_date_fields') THEN
   EXECUTE format('GRANT DELETE ON market_data.%I TO openetrm_runtime',t);
   EXECUTE format('CREATE POLICY scoped_delete ON market_data.%I FOR DELETE TO openetrm_runtime USING(security.market_access(catalog_id,''WriteMarketDefinitions'') AND security.contract_date_visible(catalog_id,definition_id,version))',t);
  END IF;
 END LOOP;
END$$;
GRANT UPDATE(name,generation,validation_context_id) ON market_data.contract_date_versions TO openetrm_runtime;
CREATE POLICY scoped_update ON market_data.contract_date_versions FOR UPDATE TO openetrm_runtime
 USING(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.contract_date_visible(catalog_id,definition_id,version))
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions'));
GRANT UPDATE ON market_data.contract_date_versions TO openetrm_security_owner;
GRANT INSERT ON market_data.contract_date_knowledge_receipts TO openetrm_security_owner;
CREATE FUNCTION security.publish_contract_dates(catalog uuid,definition uuid,requested_version integer,expected_generation integer) RETURNS integer
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.contract_date_versions;ordinal integer;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO row FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version) FOR UPDATE;
 IF NOT FOUND OR row.generation IS DISTINCT FROM expected_generation THEN RAISE EXCEPTION 'Stale definition generation' USING ERRCODE='40001';END IF;
 IF NOT security.contract_date_visible(catalog,definition,requested_version) OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'Publishing authority expired' USING ERRCODE='42501';END IF;
 IF row.state='published' THEN RETURN row.publication_order;END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.contract_date_contracts WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version))
 OR EXISTS(SELECT 1 FROM market_data.contract_date_contracts c WHERE(c.catalog_id,c.definition_id,c.version)=(catalog,definition,requested_version)
  AND 3<>(SELECT count(*) FROM market_data.contract_date_fields f WHERE(f.catalog_id,f.definition_id,f.version,f.ordinal)=(c.catalog_id,c.definition_id,c.version,c.ordinal))) THEN
  RAISE EXCEPTION 'Incomplete definition fields' USING ERRCODE='23514';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.contract_date_versions WHERE catalog_id=catalog;
 UPDATE market_data.contract_date_versions SET state='published',publication_order=ordinal,published_at=clock_timestamp(),publication_xid=pg_current_xact_id()
 WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version);
 RETURN ordinal;
END;$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_contract_dates_context;
REVOKE ALL ON FUNCTION security.capture_pre_contract_dates_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.contract_date_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT v.* FROM market_data.contract_date_versions v LEFT JOIN market_data.contract_date_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE v.catalog_id=catalog AND v.state='published' AND k.publication_order IS NULL ORDER BY v.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.contract_date_knowledge_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);
  last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_contract_dates_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('contract_date_schedule_visible','contract_date_visible','publish_contract_dates','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
REVOKE ALL ON FUNCTION market_data.guard_contract_dates() FROM PUBLIC;
COMMIT;
