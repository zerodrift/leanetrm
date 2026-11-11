-- A stable logical graph per power location. Its versions follow the existing
-- certified activation timeline. Reassignment to another family is not admitted.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.power_location_graph_bindings (
 catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,location_code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,market text NOT NULL CHECK(market IN('DA','RT')),
 purpose text NOT NULL CHECK(purpose='energy'),policy text NOT NULL CHECK(policy='follow_location_activation_v1'),
 delivery_from ref_data.instant NOT NULL,delivery_to ref_data.instant NOT NULL,
 curve_id uuid NOT NULL,activation_revision integer NOT NULL CHECK(activation_revision=1),
 PRIMARY KEY(catalog_id,definition_revision_id,location_code),UNIQUE(catalog_id,curve_id),CHECK(delivery_from<delivery_to),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,curve_id,activation_revision) REFERENCES market_data.custom_curve_activation_plans(catalog_id,curve_id,revision)
);
ALTER TABLE market_data.power_location_graph_bindings ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.power_location_graph_bindings FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON market_data.power_location_graph_bindings TO openetrm_security_owner;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.power_location_graph_bindings FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TABLE market_data.power_location_binding_requests (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 definition_revision_id uuid NOT NULL,location_code ref_data.code NOT NULL,payload jsonb NOT NULL,response jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,definition_revision_id,location_code) REFERENCES market_data.power_location_graph_bindings(catalog_id,definition_revision_id,location_code)
);
ALTER TABLE market_data.power_location_binding_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.power_location_binding_requests FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON market_data.power_location_binding_requests TO openetrm_security_owner;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.power_location_binding_requests FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION market_data.power_output_matches(catalog uuid,curve uuid,selected_version integer,output text,definition uuid,location text,market text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v JOIN market_data.custom_curve_edges e
  ON(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(v.catalog_id,v.curve_id,v.version,v.output_node,'values')
  JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state,n.kind,n.parameters->>'market')=(catalog,curve,selected_version,output,'published','power_intervals',market))
 AND security.custom_curve_uses_revision(catalog,curve,selected_version,definition)
 AND(SELECT count(*)=2 AND bool_and(c.location_code=location) FROM market_data.custom_curve_node_sources s
  JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
  WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,curve,selected_version))
$$;
ALTER FUNCTION market_data.power_output_matches(uuid,uuid,integer,text,uuid,text,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.power_output_matches(uuid,uuid,integer,text,uuid,text,text) FROM PUBLIC;

CREATE FUNCTION market_data.guard_power_activation() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b market_data.power_location_graph_bindings;
BEGIN
 SELECT * INTO b FROM market_data.power_location_graph_bindings WHERE(catalog_id,curve_id)=(NEW.catalog_id,NEW.curve_id);
 IF FOUND AND NOT market_data.power_output_matches(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.output_node,b.definition_revision_id,b.location_code,b.market)
 THEN RAISE EXCEPTION 'power_location_output';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION market_data.guard_power_activation() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_power_activation() FROM PUBLIC;
CREATE TRIGGER power_output_activation BEFORE INSERT ON market_data.custom_curve_activation_segments FOR EACH ROW EXECUTE FUNCTION market_data.guard_power_activation();

CREATE FUNCTION security.publish_power_location(catalog uuid,definition uuid,location text,market text,first_instant timestamptz,last_instant timestamptz,
 curve uuid,operation uuid,policy text,contract jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b market_data.power_location_graph_bindings;r market_data.revisions;result jsonb;actor uuid;payload jsonb;prior market_data.power_location_binding_requests;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.custom_curve_source_visible(catalog,definition)
 THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 actor=security.custom_curve_actor(catalog,'SealMarketDefinitions');
 IF actor IS NULL THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'power_location_input';END IF;
 payload=jsonb_build_object('definition',definition,'location',location,'market',market,'from',first_instant,'to',last_instant,'curve',curve,'policy',policy,'activation',contract);
 SELECT * INTO prior FROM market_data.power_location_binding_requests q WHERE(q.catalog_id,q.actor_id,q.request_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.payload<>payload THEN RAISE EXCEPTION 'power_location_conflict';END IF;
  PERFORM security.read_custom_curve_activation(catalog,curve,1);
  IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.custom_curve_source_visible(catalog,definition)
  THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
  RETURN prior.response;
 END IF;
 IF policy IS DISTINCT FROM 'follow_location_activation_v1' OR market IS NULL OR market NOT IN('DA','RT') OR location IS NULL
  OR NOT isfinite(first_instant) OR NOT isfinite(last_instant) OR first_instant>=last_instant OR first_instant IS NULL OR last_instant IS NULL
 THEN RAISE EXCEPTION 'power_location_input';END IF;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition);
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM ref_data.locations l WHERE(l.catalog_id,l.revision_id,l.code)=(r.reference_catalog_id,r.reference_revision_id,location))
 THEN RAISE EXCEPTION 'power_location_input';END IF;
 SELECT * INTO b FROM market_data.power_location_graph_bindings WHERE(catalog_id,definition_revision_id,location_code)=(catalog,definition,location);
 IF FOUND THEN
  IF(b.market,b.policy,b.delivery_from,b.delivery_to,b.curve_id) IS DISTINCT FROM(market,policy,first_instant,last_instant,curve) THEN RAISE EXCEPTION 'power_location_conflict';END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM market_data.custom_curve_activation_plans p WHERE(p.catalog_id,p.curve_id)=(catalog,curve))
   OR EXISTS(SELECT 1 FROM market_data.location_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.location_code,a.attribute)=(catalog,definition,location,'energy')
    AND a.effective_from<last_instant AND first_instant<a.effective_to)
  THEN RAISE EXCEPTION 'power_location_conflict';END IF;
 END IF;
 IF b.curve_id IS NULL THEN
  result=security.publish_custom_curve_activation(catalog,curve,operation,0,contract);
 ELSE
  IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_plans p WHERE(p.catalog_id,p.curve_id,p.revision)=(catalog,curve,1) AND p.payload-'reason'=contract-'reason')
  THEN RAISE EXCEPTION 'power_location_conflict';END IF;
  result=security.read_custom_curve_activation(catalog,curve,1)-'knownFrom';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_segments s WHERE(s.catalog_id,s.curve_id,s.revision)=(catalog,curve,1))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_activation_segments s WHERE(s.catalog_id,s.curve_id,s.revision)=(catalog,curve,1)
   AND NOT market_data.power_output_matches(catalog,curve,s.version,s.output_node,definition,location,market))
 THEN RAISE EXCEPTION 'power_location_output';END IF;
 IF b.curve_id IS NULL THEN
  INSERT INTO market_data.power_location_graph_bindings VALUES(catalog,definition,location,r.reference_catalog_id,r.reference_revision_id,market,'energy',policy,first_instant,last_instant,curve,1);
 END IF;
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.custom_curve_source_visible(catalog,definition)
 THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 INSERT INTO market_data.power_location_binding_requests VALUES(catalog,actor,operation,definition,location,payload,result);
 RETURN result;
END$$;
ALTER FUNCTION security.publish_power_location(uuid,uuid,text,text,timestamptz,timestamptz,uuid,uuid,text,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.publish_power_location(uuid,uuid,text,text,timestamptz,timestamptz,uuid,uuid,text,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.publish_power_location(uuid,uuid,text,text,timestamptz,timestamptz,uuid,uuid,text,jsonb) TO openetrm_runtime;

CREATE FUNCTION security.read_custom_curve_activation_at(catalog uuid,curve uuid,requested_revision integer,known_before timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected market_data.custom_curve_activation_plans;cutoff timestamptz:=known_before;result jsonb;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.market_context_id IS NOT NULL AND c.market_catalog_id IS DISTINCT FROM catalog)
 THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
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

ALTER FUNCTION security.read_custom_curve_activation_at(uuid,uuid,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_custom_curve_activation_at(uuid,uuid,integer,timestamptz) FROM PUBLIC;
CREATE OR REPLACE FUNCTION security.read_custom_curve_activation(catalog uuid,curve uuid,requested_revision integer DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cutoff timestamptz;
BEGIN
 SELECT t.known_at INTO cutoff FROM security.request_contexts c JOIN market_data.temporal_contexts t
  ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog;
 RETURN security.read_custom_curve_activation_at(catalog,curve,requested_revision,cutoff);
END$$;
CREATE FUNCTION security.read_power_locations(catalog uuid,definition uuid,context uuid,demands jsonb) RETURNS SETOF jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b market_data.power_location_graph_bindings;plan jsonb;cutoff timestamptz;t market_data.temporal_contexts;r market_data.revisions;
BEGIN
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO t FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context);
 IF NOT FOUND OR jsonb_typeof(demands) IS DISTINCT FROM 'array' OR jsonb_array_length(demands)>4096 THEN RAISE EXCEPTION 'power_location_input';END IF;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition);
 PERFORM security.ppa_require_market(catalog,definition,r.reference_catalog_id,r.reference_revision_id,context,t.effective_on::timestamp AT TIME ZONE 'UTC',ARRAY[]::uuid[]);
 cutoff=t.known_at;
 FOR b IN SELECT * FROM market_data.power_location_graph_bindings assigned WHERE(assigned.catalog_id,assigned.definition_revision_id)=(catalog,definition)
  AND EXISTS(SELECT 1 FROM jsonb_array_elements(demands) d WHERE d->>'location'=assigned.location_code AND(d->>'from')::timestamptz<assigned.delivery_to AND assigned.delivery_from<(d->>'to')::timestamptz) LOOP
  IF cutoff IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_plans p JOIN market_data.custom_curve_activation_receipts k USING(catalog_id,publication_order,publication_xid)
    WHERE(p.catalog_id,p.curve_id,p.revision)=(b.catalog_id,b.curve_id,b.activation_revision) AND k.known_from<=cutoff) THEN CONTINUE;END IF;
  IF NOT security.reference_version_visible(b.reference_catalog_id,b.reference_revision_id) THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
  plan=security.read_custom_curve_activation_at(catalog,b.curve_id,NULL,cutoff);
  IF plan IS NULL THEN RAISE EXCEPTION 'power_location_activation';END IF;
  RETURN NEXT to_jsonb(b)||jsonb_build_object('activation',plan,'binding_revision',1);
 END LOOP;
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.read_power_locations(uuid,uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_power_locations(uuid,uuid,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_power_locations(uuid,uuid,uuid,jsonb) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
