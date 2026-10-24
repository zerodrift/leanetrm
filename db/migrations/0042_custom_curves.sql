-- Saved typed curve graphs. Relationships retain exact versions; published graphs are immutable.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.custom_curves (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),id uuid NOT NULL,
 code text NOT NULL CHECK(code ~ '^[a-z][a-z0-9_]{0,31}$'),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,code)
);
CREATE TABLE market_data.custom_curve_versions (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL CHECK(version BETWEEN 1 AND 64),
 generation integer NOT NULL CHECK(generation>0),state text NOT NULL CHECK(state IN('draft','published')),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80 AND btrim(name)=name),
 output_node text NOT NULL,engine_version text NOT NULL CHECK(engine_version='custom_curve_recipe_v1'),
 layout jsonb NOT NULL CHECK(jsonb_typeof(layout)='object' AND octet_length(layout::text)<=16384),
 publication_order integer CHECK(publication_order>0),published_at timestamptz CHECK(isfinite(published_at)),publication_xid xid8,
 PRIMARY KEY(catalog_id,curve_id,version),FOREIGN KEY(catalog_id,curve_id) REFERENCES market_data.custom_curves(catalog_id,id),
 UNIQUE(catalog_id,publication_order),UNIQUE(catalog_id,publication_order,publication_xid),
 CHECK((state='published')=(publication_order IS NOT NULL AND published_at IS NOT NULL AND publication_xid IS NOT NULL)),
 CHECK((state='draft')=(publication_order IS NULL AND published_at IS NULL AND publication_xid IS NULL))
);
CREATE UNIQUE INDEX one_custom_curve_draft ON market_data.custom_curve_versions(catalog_id,curve_id) WHERE state='draft';
CREATE TABLE market_data.custom_curve_nodes (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL CHECK(node_id ~ '^[a-z][a-z0-9_]{0,31}$'),
 kind text NOT NULL CHECK(kind IN('source','select','dates','sample','mean','sql_transform','output')),
 parameters jsonb NOT NULL CHECK(jsonb_typeof(parameters)='object' AND octet_length(parameters::text)<=4096
 AND NOT parameters ?| ARRAY['curve','calendar','schedule']),
 PRIMARY KEY(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version) REFERENCES market_data.custom_curve_versions(catalog_id,curve_id,version)
);
ALTER TABLE market_data.custom_curve_versions ADD FOREIGN KEY(catalog_id,curve_id,version,output_node)
 REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id) DEFERRABLE INITIALLY DEFERRED;
CREATE TABLE market_data.custom_curve_edges (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,port text NOT NULL CHECK(length(port) BETWEEN 1 AND 32),source_node text NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id,port),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,source_node) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id)
);
CREATE TABLE market_data.custom_curve_node_sources (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 source_catalog_id uuid NOT NULL,source_revision_id uuid NOT NULL,source_curve_code text NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(source_catalog_id,source_revision_id,source_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
CREATE TABLE market_data.custom_curve_node_calendars (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,calendar_code text NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code)
);
CREATE TABLE market_data.custom_curve_node_schedules (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 schedule_catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,schedule_version integer NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(schedule_catalog_id,schedule_id,schedule_version) REFERENCES market_data.date_schedule_versions(catalog_id,schedule_id,version)
);
CREATE TABLE market_data.custom_curve_requests (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 operation text NOT NULL CHECK(operation IN('save','publish')),payload jsonb NOT NULL CHECK(octet_length(payload::text)<=65536),response jsonb NOT NULL,
 definition_revision_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,definition_revision_id) REFERENCES market_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,curve_id,version) REFERENCES market_data.custom_curve_versions(catalog_id,curve_id,version)
);
CREATE TABLE market_data.custom_curve_knowledge_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES market_data.custom_curve_versions(catalog_id,publication_order,publication_xid)
);
CREATE FUNCTION security.custom_curve_actor(catalog uuid,permission text) RETURNS uuid LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
 AND runtime_login=session_user AND security.market_access(catalog,permission);
$$;
CREATE FUNCTION security.custom_curve_source_visible(catalog uuid,revision uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_fact_visible(catalog,revision,NULL) AND EXISTS(SELECT 1 FROM market_data.revisions r
 WHERE(r.catalog_id,r.id)=(catalog,revision) AND security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id));
$$;
CREATE FUNCTION security.custom_curve_visible(catalog uuid,curve uuid,requested_version integer) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND EXISTS(
 SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version)=(catalog,curve,requested_version)
 AND(v.state='published' OR security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions'))
 AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version)=(v.catalog_id,v.curve_id,v.version) AND NOT security.custom_curve_source_visible(s.source_catalog_id,s.source_revision_id))
 AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars s WHERE(s.catalog_id,s.curve_id,s.version)=(v.catalog_id,v.curve_id,v.version) AND NOT security.reference_version_visible(s.reference_catalog_id,s.reference_revision_id))
 AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_node_schedules s JOIN market_data.date_schedule_versions d ON(d.catalog_id,d.schedule_id,d.version)=(s.schedule_catalog_id,s.schedule_id,s.schedule_version) WHERE(s.catalog_id,s.curve_id,s.version)=(v.catalog_id,v.curve_id,v.version) AND(d.state<>'published' OR NOT security.market_access(s.schedule_catalog_id,'ReadMarketData')))
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.custom_curve_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog AND k.known_from<=t.known_at)));
$$;
CREATE FUNCTION market_data.guard_custom_curve() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE published boolean;
BEGIN
 IF TG_TABLE_NAME IN('custom_curves','custom_curve_requests','custom_curve_knowledge_receipts') THEN
  IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Curve identities and receipts are immutable' USING ERRCODE='55000';END IF;
  RETURN NEW;
 END IF;
 IF TG_TABLE_NAME='custom_curve_versions' THEN
  IF TG_OP='DELETE' OR(TG_OP='UPDATE' AND OLD.state='published') THEN RAISE EXCEPTION 'Published curve versions are immutable' USING ERRCODE='55000';END IF;
  IF TG_OP='UPDATE' AND NEW.state='draft' AND NEW.generation<>OLD.generation+1 THEN RAISE EXCEPTION 'Stale curve generation' USING ERRCODE='40001';END IF;
 ELSE
  IF TG_OP='UPDATE' THEN
   SELECT state='published' INTO published FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(OLD.catalog_id,OLD.curve_id,OLD.version);
   IF published IS DISTINCT FROM false THEN RAISE EXCEPTION 'Published graph relationships cannot move' USING ERRCODE='55000';END IF;
  END IF;
  IF TG_OP<>'DELETE' AND TG_TABLE_NAME='custom_curve_node_sources' THEN
   IF NOT security.custom_curve_source_visible(NEW.source_catalog_id,NEW.source_revision_id) THEN RAISE EXCEPTION 'No current exact source access' USING ERRCODE='42501';END IF;
  ELSIF TG_OP<>'DELETE' AND TG_TABLE_NAME='custom_curve_node_calendars' THEN
   IF NOT security.reference_version_visible(NEW.reference_catalog_id,NEW.reference_revision_id) THEN RAISE EXCEPTION 'No current exact calendar access' USING ERRCODE='42501';END IF;
  ELSIF TG_OP<>'DELETE' AND TG_TABLE_NAME='custom_curve_node_schedules' THEN
   IF NOT security.market_access(NEW.schedule_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current schedule access' USING ERRCODE='42501';END IF;
  END IF;
  IF TG_OP='DELETE' THEN SELECT state='published' INTO published FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(OLD.catalog_id,OLD.curve_id,OLD.version);
  ELSE SELECT state='published' INTO published FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(NEW.catalog_id,NEW.curve_id,NEW.version);END IF;
  IF published IS DISTINCT FROM false THEN RAISE EXCEPTION 'Only draft curve graphs can change' USING ERRCODE='55000';END IF;
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD;END IF;RETURN NEW;
END;$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['custom_curves','custom_curve_versions','custom_curve_nodes','custom_curve_edges','custom_curve_node_sources','custom_curve_node_calendars','custom_curve_node_schedules','custom_curve_requests','custom_curve_knowledge_receipts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve()',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_update ON market_data.%I AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_delete ON market_data.%I AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context())',t);
  IF t='custom_curves' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData'') AND EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE v.catalog_id=custom_curves.catalog_id AND v.curve_id=custom_curves.id))',t);
  ELSIF t='custom_curve_requests' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,''ReadMarketData'') AND security.custom_curve_source_visible(catalog_id,definition_revision_id) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  ELSIF t='custom_curve_knowledge_receipts' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData''))',t);
  ELSE
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.custom_curve_visible(catalog_id,curve_id,version))',t);
  END IF;
  IF t NOT IN('custom_curve_knowledge_receipts') THEN
   EXECUTE format('GRANT INSERT ON market_data.%I TO openetrm_runtime',t);
   IF t='custom_curve_requests' THEN
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(actor_id=security.custom_curve_actor(catalog_id,CASE operation WHEN ''save'' THEN ''WriteMarketDefinitions'' ELSE ''SealMarketDefinitions'' END) AND security.custom_curve_source_visible(catalog_id,definition_revision_id) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
   ELSIF t='custom_curve_versions' THEN
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(state=''draft'' AND security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
   ELSE
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
   END IF;
  END IF;
  IF t NOT IN('custom_curves','custom_curve_requests','custom_curve_knowledge_receipts') THEN
   EXECUTE format('GRANT UPDATE,DELETE ON market_data.%I TO openetrm_runtime',t);
   EXECUTE format('CREATE POLICY scoped_update ON market_data.%I FOR UPDATE TO openetrm_runtime USING(security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,''WriteMarketDefinitions'')) WITH CHECK(security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
   EXECUTE format('CREATE POLICY scoped_delete ON market_data.%I FOR DELETE TO openetrm_runtime USING(security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
  END IF;
 END LOOP;
END$$;
-- Runtime authors cannot set publication metadata; that transition has its own current-authority gate.
REVOKE UPDATE ON market_data.custom_curve_versions FROM openetrm_runtime;
GRANT UPDATE(generation,name,output_node,layout) ON market_data.custom_curve_versions TO openetrm_runtime;
GRANT UPDATE ON market_data.custom_curve_versions TO openetrm_security_owner;
GRANT INSERT ON market_data.custom_curve_knowledge_receipts TO openetrm_security_owner;
CREATE FUNCTION security.publish_custom_curve(catalog uuid,curve uuid,requested_version integer,expected_generation integer) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.custom_curve_versions;ordinal integer;source record;
BEGIN
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing permission' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO row FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(catalog,curve,requested_version) FOR UPDATE;
 IF NOT FOUND OR row.generation<>expected_generation THEN RAISE EXCEPTION 'Stale curve version' USING ERRCODE='40001';END IF;
 IF row.state='published' THEN RETURN row.publication_order;END IF;
 FOR source IN SELECT * FROM market_data.custom_curve_node_sources WHERE(catalog_id,curve_id,version)=(catalog,curve,requested_version) LOOP
  IF NOT security.custom_curve_source_visible(source.source_catalog_id,source.source_revision_id) THEN RAISE EXCEPTION 'No current exact source access' USING ERRCODE='42501';END IF;
 END LOOP;
 FOR source IN SELECT * FROM market_data.custom_curve_node_calendars WHERE(catalog_id,curve_id,version)=(catalog,curve,requested_version) LOOP
  IF NOT security.reference_version_visible(source.reference_catalog_id,source.reference_revision_id) THEN RAISE EXCEPTION 'No current exact calendar access' USING ERRCODE='42501';END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_node_schedules n JOIN market_data.date_schedule_versions s ON(s.catalog_id,s.schedule_id,s.version)=(n.schedule_catalog_id,n.schedule_id,n.schedule_version) WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,requested_version) AND(s.state<>'published' OR NOT security.market_access(s.catalog_id,'ReadMarketData'))) THEN RAISE EXCEPTION 'No published schedule access' USING ERRCODE='42501';END IF;
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'Publishing authority expired' USING ERRCODE='42501';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.custom_curve_versions WHERE catalog_id=catalog;
 UPDATE market_data.custom_curve_versions SET state='published',publication_order=ordinal,published_at=clock_timestamp(),publication_xid=pg_current_xact_id() WHERE(catalog_id,curve_id,version)=(catalog,curve,requested_version);
 RETURN ordinal;
END;$$;
-- Certify this stream under the same transaction gate and cutoff as schedules/market data.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_custom_curve_context;
REVOKE ALL ON FUNCTION security.capture_pre_custom_curve_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read permission' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.custom_curve_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT v.* FROM market_data.custom_curve_versions v LEFT JOIN market_data.custom_curve_knowledge_receipts k USING(catalog_id,publication_order) WHERE v.catalog_id=catalog AND v.state='published' AND k.publication_order IS NULL ORDER BY v.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.custom_curve_knowledge_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);
  last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_custom_curve_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('custom_curve_actor','custom_curve_source_visible','custom_curve_visible','publish_custom_curve','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
REVOKE ALL ON FUNCTION market_data.guard_custom_curve() FROM PUBLIC;
COMMIT;
