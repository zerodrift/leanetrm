-- Current consumer: Curves & prices → Curve definitions (D358) saves, publishes and edits curve definitions.
-- A version names exact reference terms and one exact date-mapping version and generation; publication is immutable,
-- certified by its own knowledge receipt and follows the contract-date library's request and guard pattern (0054).
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
CREATE TABLE market_data.curve_definitions (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),id uuid NOT NULL,
 code text NOT NULL CHECK(code ~ '^[a-z][a-z0-9_.-]{0,63}$'),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,code)
);
CREATE TABLE market_data.curve_definition_versions (
 catalog_id uuid NOT NULL,definition_id uuid NOT NULL,version integer NOT NULL CHECK(version BETWEEN 1 AND 64),
 generation integer NOT NULL CHECK(generation>0),state text NOT NULL CHECK(state IN('draft','published')),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),kind text NOT NULL CHECK(kind IN('future','forward','interest_rate')),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 mapping_definition_id uuid NOT NULL,mapping_version integer NOT NULL,mapping_generation integer NOT NULL CHECK(mapping_generation>0),
 contract_count integer NOT NULL CHECK(contract_count BETWEEN 1 AND 120),
 commodity_code text,location_code text,quote_code text,profile_code text,futures_product_code text,settlement_target_code text,
 price_rule text CHECK(price_rule='direct'),mark_role text CHECK(mark_role IN('trader_mark','daily_settlement')),
 missing_price text CHECK(missing_price='reject'),interpolation text CHECK(interpolation='none'),
 interest_rate_code text,mark_source_code text NOT NULL,
 validation_context_id uuid NOT NULL,publication_order integer,published_at timestamptz,publication_xid xid8,
 PRIMARY KEY(catalog_id,definition_id,version),UNIQUE(catalog_id,publication_order,publication_xid),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,definition_id) REFERENCES market_data.curve_definitions(catalog_id,id),
 FOREIGN KEY(catalog_id,validation_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,mapping_definition_id,mapping_version) REFERENCES market_data.contract_date_versions(catalog_id,definition_id,version),
 FOREIGN KEY(reference_catalog_id,reference_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,commodity_code,location_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,profile_code) REFERENCES ref_data.delivery_profiles(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,futures_product_code) REFERENCES ref_data.futures_products(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,settlement_target_code) REFERENCES ref_data.settlement_targets(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,interest_rate_code) REFERENCES ref_data.interest_rate_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,mark_source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 -- Contract curves carry every contract term and no rate specification; an interest-rate curve carries only its specification.
 CHECK(CASE kind
  WHEN 'interest_rate' THEN interest_rate_code IS NOT NULL AND num_nonnulls(commodity_code,location_code,quote_code,profile_code,futures_product_code,
   settlement_target_code,price_rule,mark_role,missing_price,interpolation)=0
  ELSE interest_rate_code IS NULL AND num_nulls(commodity_code,location_code,quote_code,profile_code,settlement_target_code,price_rule,mark_role,missing_price,interpolation)=0
   AND (kind='future')=(futures_product_code IS NOT NULL) END),
 CHECK((state='published' AND publication_order IS NOT NULL AND publication_order>0 AND published_at IS NOT NULL AND publication_xid IS NOT NULL)
    OR(state='draft' AND publication_order IS NULL AND published_at IS NULL AND publication_xid IS NULL))
);
CREATE UNIQUE INDEX one_curve_definition_draft ON market_data.curve_definition_versions(catalog_id,definition_id) WHERE state='draft';
CREATE TABLE market_data.curve_definition_requests (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 operation text NOT NULL CHECK(operation IN('save','publish')),payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9A-F]{64}$'),
 definition_id uuid NOT NULL,version integer NOT NULL,generation integer NOT NULL CHECK(generation>0),state text NOT NULL CHECK(state IN('draft','published')),
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,definition_id,version) REFERENCES market_data.curve_definition_versions(catalog_id,definition_id,version)
);
CREATE TABLE market_data.curve_definition_knowledge_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES market_data.curve_definition_versions(catalog_id,publication_order,publication_xid)
);
-- A version is visible when its mapping and reference revision are, drafts only to current authors and publishers,
-- and a publication only to knowledge views at or after its certified receipt.
CREATE FUNCTION security.curve_definition_visible(catalog uuid,definition uuid,requested_version integer) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND EXISTS(SELECT 1 FROM market_data.curve_definition_versions v
 WHERE(v.catalog_id,v.definition_id,v.version)=(catalog,definition,requested_version)
 AND(v.state='published' OR security.current_write_context() AND(security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions')))
 AND security.contract_date_visible(catalog,v.mapping_definition_id,v.mapping_version)
 AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id)
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR v.state='draft'
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.curve_definition_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog AND k.known_from<=t.known_at)));
$$;
-- Publishes a draft whose pinned mapping is already published at the pinned generation, with the engine its kind needs.
CREATE FUNCTION security.publish_curve_definition(catalog uuid,definition uuid,requested_version integer,expected_generation integer) RETURNS integer
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.curve_definition_versions;mapping market_data.contract_date_versions;ordinal integer;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO row FROM market_data.curve_definition_versions WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version) FOR UPDATE;
 IF NOT FOUND OR row.generation IS DISTINCT FROM expected_generation THEN RAISE EXCEPTION 'Stale definition generation' USING ERRCODE='40001';END IF;
 IF NOT security.curve_definition_visible(catalog,definition,requested_version) THEN RAISE EXCEPTION 'Publishing authority expired' USING ERRCODE='42501';END IF;
 IF row.state='published' THEN RETURN row.publication_order;END IF;
 SELECT * INTO mapping FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=(catalog,row.mapping_definition_id,row.mapping_version);
 IF mapping.state IS DISTINCT FROM 'published' OR mapping.generation<>row.mapping_generation THEN RAISE EXCEPTION 'Stale mapping generation' USING ERRCODE='40001';END IF;
 IF (row.kind='interest_rate')<>(mapping.engine_version='date_list_v1') THEN RAISE EXCEPTION 'Mapping type does not match the curve kind' USING ERRCODE='23514';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.curve_definition_versions WHERE catalog_id=catalog;
 UPDATE market_data.curve_definition_versions SET state='published',publication_order=ordinal,published_at=clock_timestamp(),publication_xid=pg_current_xact_id()
 WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version);
 RETURN ordinal;
END;$$;
CREATE FUNCTION market_data.guard_curve_definitions() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_TABLE_NAME IN('curve_definitions','curve_definition_requests','curve_definition_knowledge_receipts') THEN
  IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Curve-definition identities and receipts are immutable' USING ERRCODE='55000';END IF;
 ELSE
  IF TG_OP='DELETE' OR(TG_OP='UPDATE' AND OLD.state='published') THEN RAISE EXCEPTION 'Published curve definitions are immutable' USING ERRCODE='55000';END IF;
  IF TG_OP='UPDATE' AND(NEW.catalog_id,NEW.definition_id,NEW.version) IS DISTINCT FROM(OLD.catalog_id,OLD.definition_id,OLD.version) THEN
   RAISE EXCEPTION 'Version identity is immutable' USING ERRCODE='55000';END IF;
  IF TG_OP='UPDATE' AND NEW.state='draft' AND NEW.generation<>OLD.generation+1 THEN RAISE EXCEPTION 'Stale generation' USING ERRCODE='40001';END IF;
  IF current_user<>pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid=TG_RELID))
   AND (NOT security.current_write_context() OR NOT security.market_access(NEW.catalog_id,CASE NEW.state WHEN 'published' THEN 'SealMarketDefinitions' ELSE 'WriteMarketDefinitions' END)) THEN
   RAISE EXCEPTION 'Curve-definition authoring authority expired' USING ERRCODE='42501';END IF;
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD;END IF;RETURN NEW;
END;$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['curve_definitions','curve_definition_versions','curve_definition_requests','curve_definition_knowledge_receipts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER guard_curve_definitions BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_curve_definitions()',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_update ON market_data.%I AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context())',t);
  IF t='curve_definitions' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM market_data.curve_definition_versions v WHERE v.catalog_id=curve_definitions.catalog_id AND v.definition_id=curve_definitions.id))',t);
  ELSIF t='curve_definition_requests' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,''ReadMarketData'') AND security.curve_definition_visible(catalog_id,definition_id,version))',t);
  ELSIF t='curve_definition_knowledge_receipts' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData''))',t);
  ELSE
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.curve_definition_visible(catalog_id,definition_id,version))',t);
  END IF;
  IF t<>'curve_definition_knowledge_receipts' THEN
   EXECUTE format('GRANT INSERT ON market_data.%I TO openetrm_runtime',t);
   IF t='curve_definition_requests' THEN
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(actor_id=security.custom_curve_actor(catalog_id,CASE operation WHEN ''save'' THEN ''WriteMarketDefinitions'' ELSE ''SealMarketDefinitions'' END) AND security.curve_definition_visible(catalog_id,definition_id,version))',t);
   ELSE
    EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
   END IF;
  END IF;
 END LOOP;
END$$;
-- Drafts are rewritten in place by their authors; publication goes through security.publish_curve_definition.
GRANT UPDATE(name,generation,validation_context_id,reference_catalog_id,reference_revision_id,mapping_definition_id,mapping_version,mapping_generation,contract_count,
 commodity_code,location_code,quote_code,profile_code,futures_product_code,settlement_target_code,price_rule,mark_role,missing_price,interpolation,interest_rate_code,mark_source_code)
 ON market_data.curve_definition_versions TO openetrm_runtime;
CREATE POLICY scoped_update ON market_data.curve_definition_versions FOR UPDATE TO openetrm_runtime
 USING(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.curve_definition_visible(catalog_id,definition_id,version))
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions'));
GRANT UPDATE ON market_data.curve_definition_versions TO openetrm_security_owner;
GRANT INSERT ON market_data.curve_definition_knowledge_receipts TO openetrm_security_owner;
-- Capturing a knowledge context first certifies new curve-definition publications, then runs the earlier capture chain.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_curve_definitions_context;
REVOKE ALL ON FUNCTION security.capture_pre_curve_definitions_context(uuid,date,timestamptz) FROM openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 -- The risk worker captures through the earlier chain, as it has since 0117; only runtime captures certify publications.
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN security.capture_pre_curve_definitions_context(catalog,effective_date,requested_at);END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.curve_definition_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT v.* FROM market_data.curve_definition_versions v LEFT JOIN market_data.curve_definition_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE v.catalog_id=catalog AND v.state='published' AND k.publication_order IS NULL ORDER BY v.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.curve_definition_knowledge_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);
  last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_curve_definitions_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('curve_definition_visible','publish_curve_definition','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_risk_worker;
REVOKE ALL ON FUNCTION market_data.guard_curve_definitions() FROM PUBLIC;
COMMIT;
