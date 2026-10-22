-- Complete effective timelines of exact published outputs. Later committed
-- knowledge replaces a timeline; it never rewrites an earlier certified view.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.custom_curve_activation_plans (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),
 actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 payload jsonb NOT NULL CHECK(octet_length(payload::text)<=32768),
 publication_order integer NOT NULL CHECK(publication_order>0),publication_xid xid8 NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,revision),UNIQUE(catalog_id,actor_id,request_id),
 UNIQUE(catalog_id,publication_order,publication_xid),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,curve_id) REFERENCES market_data.custom_curves(catalog_id,id)
);
CREATE TABLE market_data.custom_curve_activation_segments (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,revision integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 63),
 effective_from date NOT NULL,effective_to date NOT NULL,version integer NOT NULL,output_node text NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,revision,ordinal),
 CHECK(isfinite(effective_from) AND isfinite(effective_to) AND effective_from>=DATE '0001-01-01'
  AND effective_from<effective_to AND effective_to<=DATE '9999-12-31'),
 FOREIGN KEY(catalog_id,curve_id,revision) REFERENCES market_data.custom_curve_activation_plans(catalog_id,curve_id,revision),
 FOREIGN KEY(catalog_id,curve_id,version,output_node) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id)
);
CREATE TABLE market_data.custom_curve_activation_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid)
  REFERENCES market_data.custom_curve_activation_plans(catalog_id,publication_order,publication_xid)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['custom_curve_activation_plans','custom_curve_activation_segments','custom_curve_activation_receipts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

-- Pick the plan before checking its targets. An inaccessible newer plan or a
-- gap must fail rather than resurrecting an older covering/visible timeline.
CREATE FUNCTION security.read_custom_curve_activation(catalog uuid,curve uuid,requested_revision integer DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected market_data.custom_curve_activation_plans;cutoff timestamptz;result jsonb;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.market_context_id IS NOT NULL AND c.market_catalog_id IS DISTINCT FROM catalog)
 THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 SELECT t.known_at INTO cutoff FROM security.request_contexts c JOIN market_data.temporal_contexts t
  ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog;
 SELECT p.* INTO selected FROM market_data.custom_curve_activation_plans p
  LEFT JOIN market_data.custom_curve_activation_receipts k USING(catalog_id,publication_order,publication_xid)
  WHERE p.catalog_id=catalog AND p.curve_id=curve AND(requested_revision IS NULL OR p.revision=requested_revision)
   AND(cutoff IS NULL OR k.known_from<=cutoff) ORDER BY p.revision DESC LIMIT 1;
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_activation_segments s WHERE(s.catalog_id,s.curve_id,s.revision)=(catalog,curve,selected.revision)
  AND NOT security.custom_curve_visible(catalog,curve,s.version))
 THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 SELECT jsonb_build_object('curveId',curve,'revision',selected.revision,'policy',selected.payload->>'policy',
  'reason',selected.payload->>'reason','knownFrom',(SELECT known_from FROM market_data.custom_curve_activation_receipts k
   WHERE(k.catalog_id,k.publication_order,k.publication_xid)=(catalog,selected.publication_order,selected.publication_xid)),
  'segments',coalesce(jsonb_agg(jsonb_build_object('effectiveFrom',s.effective_from,'effectiveTo',s.effective_to,
    'version',s.version,'outputNode',s.output_node) ORDER BY s.effective_from) FILTER(WHERE s.ordinal IS NOT NULL),'[]'::jsonb))
 INTO result FROM market_data.custom_curve_activation_segments s WHERE(s.catalog_id,s.curve_id,s.revision)=(catalog,curve,selected.revision);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;

CREATE FUNCTION security.publish_custom_curve_activation(catalog uuid,curve uuid,operation uuid,expected integer,contract jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;prior market_data.custom_curve_activation_plans;head integer;publication integer;segment jsonb;result jsonb;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions')
  OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 actor=security.custom_curve_actor(catalog,'SealMarketDefinitions');
 IF actor IS NULL OR NOT security.current_write_context() THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR expected IS NULL OR expected<0
  OR jsonb_typeof(contract) IS DISTINCT FROM 'object' OR NOT contract ?& ARRAY['policy','reason','segments']
  OR(SELECT count(*) FROM jsonb_object_keys(contract))<>3
  OR contract->>'policy' IS DISTINCT FROM 'complete_effective_timeline_v1'
  OR jsonb_typeof(contract->'reason') IS DISTINCT FROM 'string' OR length(btrim(contract->>'reason')) NOT BETWEEN 1 AND 500
  OR jsonb_typeof(contract->'segments') IS DISTINCT FROM 'array' OR jsonb_array_length(contract->'segments')>64
  OR octet_length(contract::text)>32768
 THEN RAISE EXCEPTION 'curve_activation_input';END IF;
 SELECT * INTO prior FROM market_data.custom_curve_activation_plans p WHERE(p.catalog_id,p.actor_id,p.request_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.curve_id<>curve OR prior.payload<>contract OR prior.revision<>expected+1 THEN RAISE EXCEPTION 'curve_activation_conflict';END IF;
  result=security.read_custom_curve_activation(catalog,curve,prior.revision)-'knownFrom';
 ELSE
 SELECT coalesce(max(revision),0) INTO head FROM market_data.custom_curve_activation_plans p WHERE(p.catalog_id,p.curve_id)=(catalog,curve);
 IF head<>expected THEN RAISE EXCEPTION 'curve_activation_conflict';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id)=(catalog,curve)
  AND v.state='published' AND security.custom_curve_visible(catalog,curve,v.version)) THEN RAISE EXCEPTION 'curve_activation_input';END IF;
 FOR segment IN SELECT value FROM jsonb_array_elements(contract->'segments') LOOP
  IF jsonb_typeof(segment) IS DISTINCT FROM 'object' OR NOT segment ?& ARRAY['effectiveFrom','effectiveTo','version','outputNode']
   OR(SELECT count(*) FROM jsonb_object_keys(segment))<>4 OR jsonb_typeof(segment->'version') IS DISTINCT FROM 'number'
   OR jsonb_typeof(segment->'effectiveFrom') IS DISTINCT FROM 'string' OR jsonb_typeof(segment->'effectiveTo') IS DISTINCT FROM 'string'
   OR jsonb_typeof(segment->'outputNode') IS DISTINCT FROM 'string'
   OR NOT(segment->>'effectiveFrom'~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' AND segment->>'effectiveTo'~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$')
   OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node)=
    (catalog,curve,(segment->>'version')::integer,segment->>'outputNode') AND v.state='published' AND v.engine_version='custom_curve_recipe_v2')
  THEN RAISE EXCEPTION 'curve_activation_input';END IF;
  IF NOT security.custom_curve_visible(catalog,curve,(segment->>'version')::integer) THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM market_data.custom_curve_activation_plans WHERE catalog_id=catalog;
 INSERT INTO market_data.custom_curve_activation_plans VALUES(catalog,curve,head+1,actor,operation,contract,publication,pg_current_xact_id());
 INSERT INTO market_data.custom_curve_activation_segments
 SELECT catalog,curve,head+1,(ordinality-1)::integer,(value->>'effectiveFrom')::date,(value->>'effectiveTo')::date,
  (value->>'version')::integer,value->>'outputNode' FROM jsonb_array_elements(contract->'segments') WITH ORDINALITY;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_activation_segments a JOIN market_data.custom_curve_activation_segments b
  USING(catalog_id,curve_id,revision) WHERE(a.catalog_id,a.curve_id,a.revision)=(catalog,curve,head+1) AND a.ordinal<b.ordinal
   AND a.effective_from<b.effective_to AND b.effective_from<a.effective_to) THEN RAISE EXCEPTION 'curve_activation_overlap';END IF;
 result=security.read_custom_curve_activation(catalog,curve,head+1)-'knownFrom';
 END IF;
 -- Assembly can outlive the admission grant, including on an exact replay.
 IF NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.market_access(catalog,'ReadMarketData')
  OR NOT security.current_write_context()
 THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format OR check_violation OR numeric_value_out_of_range
 THEN RAISE EXCEPTION 'curve_activation_input';
END$$;

ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_curve_activation_context;
REVOKE ALL ON FUNCTION security.capture_pre_curve_activation_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.custom_curve_activation_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT p.* FROM market_data.custom_curve_activation_plans p LEFT JOIN market_data.custom_curve_activation_receipts k
  USING(catalog_id,publication_order) WHERE p.catalog_id=catalog AND k.publication_order IS NULL ORDER BY p.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.custom_curve_activation_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_curve_activation_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 RETURN token;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('read_custom_curve_activation','publish_custom_curve_activation','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
CREATE TRIGGER calculation_change AFTER INSERT ON market_data.custom_curve_activation_plans
 FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();
-- Reuse the authorized existing head, appending only this catalog's activation stream.
ALTER FUNCTION security.calculation_head(uuid) RENAME TO calculation_pre_activation_head;
REVOKE ALL ON FUNCTION security.calculation_pre_activation_head(uuid) FROM openetrm_runtime;
CREATE FUNCTION security.calculation_head(catalog uuid) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result text;
BEGIN
 result=(security.calculation_pre_activation_head(catalog)::jsonb||jsonb_build_array(
  (SELECT coalesce(max(publication_order),0) FROM market_data.custom_curve_activation_plans WHERE catalog_id=catalog)))::text;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.calculation_head(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.calculation_head(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.calculation_head(uuid) TO openetrm_runtime;
COMMIT;
