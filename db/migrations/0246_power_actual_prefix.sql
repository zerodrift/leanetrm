-- A declared historical prefix uses captured settlement, never invented anchors.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $guards$DECLARE item record;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.read_power_locations(uuid,uuid,uuid,jsonb)','cab74aeca352efe88190bfb8adb77fbd0a5d3cef1eb3e10aa4b9302c41179550'),
  ('security.publish_custom_curve_activation(uuid,uuid,uuid,integer,jsonb)','00d9d1752eee1bbdba8248ab641b5be47b2baea35d59f5dea848258afcff30ec')
 ) d(signature,digest) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(item.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.digest)
  THEN RAISE EXCEPTION 'power_prefix_upgrade: changed dependency %',item.signature;END IF;
 END LOOP;
END$guards$;

CREATE TABLE market_data.power_location_actual_prefixes(
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,activation_revision integer NOT NULL,
 actual_from ref_data.instant NOT NULL,policy text NOT NULL CHECK(policy='follow_activation_actual_prefix_v1'),
 PRIMARY KEY(catalog_id,curve_id,activation_revision),
 FOREIGN KEY(catalog_id,curve_id) REFERENCES market_data.power_location_graph_bindings(catalog_id,curve_id),
 FOREIGN KEY(catalog_id,curve_id,activation_revision) REFERENCES market_data.custom_curve_activation_plans(catalog_id,curve_id,revision)
);
ALTER TABLE market_data.power_location_actual_prefixes ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.power_location_actual_prefixes FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON market_data.power_location_actual_prefixes TO openetrm_security_owner;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.power_location_actual_prefixes
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION market_data.retain_power_actual_prefix(catalog uuid,curve uuid,selected_revision integer,first_instant timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b market_data.power_location_graph_bindings;prior market_data.power_location_actual_prefixes;segment record;node record;
BEGIN
 SELECT * INTO b FROM market_data.power_location_graph_bindings WHERE(catalog_id,curve_id)=(catalog,curve);
 IF NOT FOUND OR b.market<>'DA' OR first_instant IS NULL OR NOT isfinite(first_instant) OR first_instant>=b.delivery_from
 THEN RAISE EXCEPTION 'power_prefix_input';END IF;
 IF EXISTS(SELECT 1 FROM market_data.location_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.location_code,a.attribute)=
  (catalog,b.definition_revision_id,b.location_code,'energy') AND a.effective_from<b.delivery_to AND first_instant<a.effective_to)
 THEN RAISE EXCEPTION 'power_location_conflict';END IF;
 SELECT * INTO prior FROM market_data.power_location_actual_prefixes WHERE(catalog_id,curve_id,activation_revision)=(catalog,curve,selected_revision);
 IF FOUND THEN
  IF prior.actual_from<>first_instant THEN RAISE EXCEPTION 'power_prefix_conflict';END IF;
 ELSE
  IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_plans p WHERE(p.catalog_id,p.curve_id,p.revision,p.publication_xid)=
    (catalog,curve,selected_revision,pg_current_xact_id())) THEN RAISE EXCEPTION 'power_prefix_new_activation';END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_segments s WHERE(s.catalog_id,s.curve_id,s.revision)=(catalog,curve,selected_revision))
 THEN RAISE EXCEPTION 'power_prefix_input';END IF;
 FOR segment IN SELECT * FROM market_data.custom_curve_activation_segments s WHERE(s.catalog_id,s.curve_id,s.revision)=(catalog,curve,selected_revision) LOOP
  IF NOT market_data.power_output_matches(catalog,curve,segment.version,segment.output_node,b.definition_revision_id,b.location_code,b.market)
   OR NOT security.custom_curve_visible(catalog,curve,segment.version) THEN RAISE EXCEPTION 'power_prefix_output';END IF;
  SELECT n.parameters,profile.clock_code INTO node FROM market_data.custom_curve_edges e
   JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   JOIN market_data.custom_curve_node_power_intervals i ON(i.catalog_id,i.curve_id,i.version,i.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)
   JOIN ref_data.delivery_profiles profile ON(profile.catalog_id,profile.revision_id,profile.code)=(i.reference_catalog_id,i.reference_revision_id,i.profile_code)
   JOIN market_data.custom_curve_power_settlements settlement ON(settlement.catalog_id,settlement.curve_id,settlement.version,settlement.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(catalog,curve,segment.version,segment.output_node,'values');
  IF NOT FOUND OR node.parameters->>'settlementPolicy' IS DISTINCT FROM 'pjm_da_normal_release_replay_v1'
   OR security.ppa_day_start(b.reference_catalog_id,b.reference_revision_id,node.clock_code,(node.parameters->>'from')::date) IS DISTINCT FROM b.delivery_from
   OR security.ppa_day_start(b.reference_catalog_id,b.reference_revision_id,node.clock_code,(node.parameters->>'to')::date) IS DISTINCT FROM b.delivery_to
   OR security.ppa_day_start(b.reference_catalog_id,b.reference_revision_id,node.clock_code,date_trunc('month',(node.parameters->>'from')::date)::date) IS DISTINCT FROM first_instant
  THEN RAISE EXCEPTION 'power_prefix_output';END IF;
 END LOOP;
 IF prior.curve_id IS NULL THEN INSERT INTO market_data.power_location_actual_prefixes VALUES(catalog,curve,selected_revision,first_instant,'follow_activation_actual_prefix_v1');END IF;
 IF NOT security.custom_curve_source_visible(catalog,b.definition_revision_id) OR NOT security.reference_version_visible(b.reference_catalog_id,b.reference_revision_id)
 THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('from',first_instant,'to',b.delivery_from,'activationRevision',selected_revision,'policy','follow_activation_actual_prefix_v1');
END$$;
ALTER FUNCTION market_data.retain_power_actual_prefix(uuid,uuid,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.retain_power_actual_prefix(uuid,uuid,integer,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION market_data.carry_power_actual_prefix(catalog uuid,curve uuid,selected_revision integer)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior market_data.power_location_actual_prefixes;BEGIN
 -- Replay/no-op never manufactures a prefix under a past publication receipt.
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_plans p WHERE(p.catalog_id,p.curve_id,p.revision,p.publication_xid)=
   (catalog,curve,selected_revision,pg_current_xact_id())) THEN RETURN NULL;END IF;
 SELECT * INTO prior FROM market_data.power_location_actual_prefixes WHERE(catalog_id,curve_id,activation_revision)=(catalog,curve,selected_revision-1);
 IF NOT FOUND THEN RETURN NULL;END IF;
 RETURN market_data.retain_power_actual_prefix(catalog,curve,selected_revision,prior.actual_from);
END$$;
ALTER FUNCTION market_data.carry_power_actual_prefix(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.carry_power_actual_prefix(uuid,uuid,integer) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $upgrade$DECLARE body text;needle text:=' -- Assembly can outlive the admission grant, including on an exact replay.';BEGIN
 body=pg_get_functiondef('security.publish_custom_curve_activation(uuid,uuid,uuid,integer,jsonb)'::regprocedure);
 IF strpos(body,needle)=0 OR length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_prefix_upgrade';END IF;
 body=replace(body,needle,$added$ PERFORM market_data.carry_power_actual_prefix(catalog,curve,(result->>'revision')::integer);
$added$||needle);EXECUTE body;
END$upgrade$;

CREATE FUNCTION security.publish_power_location_history(catalog uuid,definition uuid,location text,curve uuid,operation uuid,
 expected integer,first_instant timestamptz,contract jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;b market_data.power_location_graph_bindings;prior market_data.power_location_binding_requests;payload jsonb;result jsonb;prefix jsonb;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.custom_curve_source_visible(catalog,definition)
 THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);actor=security.custom_curve_actor(catalog,'SealMarketDefinitions');
 IF actor IS NULL OR NOT security.current_write_context() OR NOT security.custom_curve_source_visible(catalog,definition)
 THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'power_prefix_input';END IF;
 payload=jsonb_build_object('definition',definition,'location',location,'curve',curve,'expected',expected,'actualFrom',first_instant,'activation',contract);
 SELECT * INTO prior FROM market_data.power_location_binding_requests q WHERE(q.catalog_id,q.actor_id,q.request_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.payload<>payload THEN RAISE EXCEPTION 'power_prefix_conflict';END IF;
  PERFORM security.read_custom_curve_activation(catalog,curve,(prior.response#>>'{activation,revision}')::integer);
  IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.custom_curve_source_visible(catalog,definition)
  THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
  RETURN prior.response;
 END IF;
 SELECT * INTO b FROM market_data.power_location_graph_bindings WHERE(catalog_id,definition_revision_id,location_code,curve_id)=(catalog,definition,location,curve);
 IF NOT FOUND THEN RAISE EXCEPTION 'power_prefix_input';END IF;
 result=security.publish_custom_curve_activation(catalog,curve,operation,expected,contract);
 prefix=market_data.retain_power_actual_prefix(catalog,curve,(result->>'revision')::integer,first_instant);
 result=jsonb_build_object('activation',result,'actualPrefix',prefix);
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.custom_curve_source_visible(catalog,definition)
 THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 INSERT INTO market_data.power_location_binding_requests VALUES(catalog,actor,operation,definition,location,payload,result);
 RETURN result;
END$$;
ALTER FUNCTION security.publish_power_location_history(uuid,uuid,text,uuid,uuid,integer,timestamptz,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.publish_power_location_history(uuid,uuid,text,uuid,uuid,integer,timestamptz,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.publish_power_location_history(uuid,uuid,text,uuid,uuid,integer,timestamptz,jsonb) TO openetrm_runtime;

CREATE OR REPLACE FUNCTION security.read_power_locations(catalog uuid,definition uuid,context uuid,demands jsonb) RETURNS SETOF jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b market_data.power_location_graph_bindings;plan jsonb;t market_data.temporal_contexts;r market_data.revisions;
 prefix market_data.power_location_actual_prefixes;first_instant timestamptz;
BEGIN
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO t FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context);
 IF NOT FOUND OR jsonb_typeof(demands) IS DISTINCT FROM 'array' OR jsonb_array_length(demands)>4096 THEN RAISE EXCEPTION 'power_location_input';END IF;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition);
 PERFORM security.ppa_require_market(catalog,definition,r.reference_catalog_id,r.reference_revision_id,context,t.effective_on::timestamp AT TIME ZONE 'UTC',ARRAY[]::uuid[]);
 FOR b IN SELECT * FROM market_data.power_location_graph_bindings assigned WHERE(assigned.catalog_id,assigned.definition_revision_id)=(catalog,definition)
  AND EXISTS(SELECT 1 FROM jsonb_array_elements(demands) d WHERE d->>'location'=assigned.location_code) LOOP
  IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_activation_plans p JOIN market_data.custom_curve_activation_receipts k USING(catalog_id,publication_order,publication_xid)
    WHERE(p.catalog_id,p.curve_id,p.revision)=(b.catalog_id,b.curve_id,b.activation_revision) AND k.known_from<=t.known_at) THEN CONTINUE;END IF;
  IF NOT security.reference_version_visible(b.reference_catalog_id,b.reference_revision_id) THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
  plan=security.read_custom_curve_activation_at(catalog,b.curve_id,NULL,t.known_at);
  IF plan IS NULL THEN RAISE EXCEPTION 'power_location_activation';END IF;
  SELECT * INTO prefix FROM market_data.power_location_actual_prefixes p WHERE(p.catalog_id,p.curve_id,p.activation_revision)=(catalog,b.curve_id,(plan->>'revision')::integer);
  first_instant=coalesce(prefix.actual_from,b.delivery_from);
  IF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(demands) d WHERE d->>'location'=b.location_code AND(d->>'from')::timestamptz<b.delivery_to AND first_instant<(d->>'to')::timestamptz) THEN CONTINUE;END IF;
  IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RAISE EXCEPTION 'power_location_worker_unsupported';END IF;
  RETURN NEXT to_jsonb(b)||jsonb_build_object('activation',plan,'binding_revision',1,'delivery_from',first_instant,
   'actual_only_from',prefix.actual_from,'actual_only_to',CASE WHEN prefix.curve_id IS NOT NULL THEN b.delivery_from END,'prefix_activation_revision',prefix.activation_revision,'prefix_policy',prefix.policy);
 END LOOP;
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_location_denied' USING ERRCODE='42501';END IF;
END$$;
COMMIT;
