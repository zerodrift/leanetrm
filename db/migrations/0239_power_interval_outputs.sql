-- Exact typed power interval outputs. Native panel semantics are unchanged.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','published_source','select','weighted_contracts','weighted_panels','dates','sample','mean','lookup','splice','power_intervals','output'));
CREATE TABLE market_data.custom_curve_node_power_intervals (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 product_catalog_id uuid NOT NULL,product_id uuid NOT NULL,product_version integer NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,profile_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id),CHECK(product_catalog_id=catalog_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(product_catalog_id,product_id,product_version) REFERENCES market_data.tou_product_versions(catalog_id,product_id,version),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,profile_code) REFERENCES ref_data.delivery_profiles(catalog_id,revision_id,code)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['custom_curve_node_power_intervals'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve()',t);
  EXECUTE format('CREATE CONSTRAINT TRIGGER agent_draft_write AFTER INSERT OR UPDATE OR DELETE ON market_data.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.check_custom_curve_agent_write()',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('CREATE POLICY risk_worker_service ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_update ON market_data.%I AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_delete ON market_data.%I AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('CREATE POLICY scoped_update ON market_data.%I FOR UPDATE TO openetrm_runtime USING((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version)) WITH CHECK((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('CREATE POLICY scoped_delete ON market_data.%I FOR DELETE TO openetrm_runtime USING((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker',t);
  EXECUTE format('GRANT INSERT,UPDATE,DELETE ON market_data.%I TO openetrm_runtime',t);
 END LOOP;
END$$;

-- Historical TOU admission needs committed evidence: a sealed source bundle or
-- an exact visible published interval graph. Unsaved versions are never inferred.
CREATE FUNCTION security.power_interval_products(catalog uuid,definition uuid) RETURNS SETOF jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_interval_denied' USING ERRCODE='42501';END IF;
 RETURN QUERY SELECT to_jsonb(v)||jsonb_build_object('code',p.code)
 FROM market_data.tou_product_versions v JOIN market_data.tou_products p ON(p.catalog_id,p.id)=(v.catalog_id,v.product_id)
 JOIN market_data.revisions r ON(r.catalog_id,r.id)=(catalog,definition)
 WHERE v.catalog_id=catalog AND v.state='published' AND(v.clock_catalog_id,v.clock_revision_id)=(r.reference_catalog_id,r.reference_revision_id) AND(
  EXISTS(SELECT 1 FROM market_data.curve_blocks b WHERE(b.catalog_id,b.revision_id,b.tou_product_id,b.tou_version)=(catalog,definition,v.product_id,v.version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals n JOIN market_data.custom_curve_versions g USING(catalog_id,curve_id,version)
   WHERE(n.product_catalog_id,n.product_id,n.product_version)=(catalog,v.product_id,v.version) AND g.state='published'
    AND security.custom_curve_visible(n.catalog_id,n.curve_id,n.version) AND security.custom_curve_uses_revision(n.catalog_id,n.curve_id,n.version,definition)));
 IF NOT security.custom_curve_source_visible(catalog,definition) THEN RAISE EXCEPTION 'power_interval_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.power_interval_products(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.power_interval_products(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.power_interval_products(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION market_data.guard_power_interval_publication() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;first_day date;last_day date;
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(NEW.catalog_id,NEW.curve_id,NEW.version)
  AND(n.kind='power_intervals') IS DISTINCT FROM EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals p WHERE(p.catalog_id,p.curve_id,p.version,p.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)))
 THEN RAISE EXCEPTION 'power_interval_binding';END IF;
 FOR node IN SELECT * FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,'power_intervals') LOOP
  IF node.parameters<>jsonb_build_object('implementation','flat_forward_only_v1','profileMapping','exact_native_grid_terms_v1','market',node.parameters->>'market','from',node.parameters->>'from','to',node.parameters->>'to')
   OR EXISTS(SELECT 1 FROM jsonb_each(node.parameters) p WHERE jsonb_typeof(p.value)<>'string')
   OR node.parameters->>'market' IS NULL OR node.parameters->>'market' NOT IN('DA','RT') OR node.parameters->>'from' IS NULL OR node.parameters->>'to' IS NULL
   OR node.parameters->>'from' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR node.parameters->>'to' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  THEN RAISE EXCEPTION 'power_interval_policy';END IF;
  first_day=(node.parameters->>'from')::date;last_day=(node.parameters->>'to')::date;
  IF NOT isfinite(first_day) OR NOT isfinite(last_day) OR last_day-first_day NOT BETWEEN 1 AND 1464 THEN RAISE EXCEPTION 'power_interval_policy';END IF;
  IF (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(NEW.catalog_id,NEW.curve_id,NEW.version))<>4
   OR (SELECT count(*) FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes s ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND e.port IN('peak','offPeak') AND s.kind='source')<>2
   OR (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id))<>2
   OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes o ON(o.catalog_id,o.curve_id,o.version,o.node_id)=(e.catalog_id,e.curve_id,e.version,e.node_id)
    WHERE(e.catalog_id,e.curve_id,e.version,e.source_node,e.port,o.node_id,o.kind)=(node.catalog_id,node.curve_id,node.version,node.node_id,'values',NEW.output_node,'output'))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.source_node)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND e.node_id<>NEW.output_node)
  THEN RAISE EXCEPTION 'power_interval_inputs';END IF;
  IF (SELECT count(*)=2 AND count(DISTINCT s.source_curve_code)=2 AND count(DISTINCT e.source_node)=2
    AND count(DISTINCT (s.source_catalog_id,s.source_revision_id))=1 AND bool_and(s.source_catalog_id=NEW.catalog_id)
    AND count(DISTINCT (c.quote_code,c.location_code,c.commodity_code))=1
    AND bool_and(q.kind='outright' AND energy.unit_code='megawatt_hour' AND q.commodity_code=c.commodity_code AND energy.commodity_code=c.commodity_code)
    FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_node_sources s
     ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
    JOIN ref_data.quantity_specifications energy ON(energy.catalog_id,energy.revision_id,energy.code)=(q.catalog_id,q.revision_id,q.quantity_code)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id)) IS DISTINCT FROM true
   OR (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,NEW.output_node))<>1
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources s JOIN market_data.curve_members m
    ON(m.catalog_id,m.revision_id,m.curve_code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(s.catalog_id,s.curve_id,s.version)=(node.catalog_id,node.curve_id,node.version) AND(k.kind NOT IN('future','forward') OR
     EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(k.catalog_id,k.revision_id,k.id))))
  THEN RAISE EXCEPTION 'power_interval_units';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals p JOIN market_data.tou_product_versions t
    ON(t.catalog_id,t.product_id,t.version)=(p.product_catalog_id,p.product_id,p.product_version)
   JOIN ref_data.delivery_profiles d ON(d.catalog_id,d.revision_id,d.code)=(p.reference_catalog_id,p.reference_revision_id,p.profile_code)
   WHERE(p.catalog_id,p.curve_id,p.version,p.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND t.state='published'
    AND(t.clock_catalog_id,t.clock_revision_id,t.clock_code)=(d.catalog_id,d.revision_id,d.clock_code) AND d.interval_minutes=60
    AND security.reference_version_visible(d.catalog_id,d.revision_id))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_edges e
    JOIN market_data.custom_curve_node_sources s ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    JOIN ref_data.delivery_profiles d ON(d.catalog_id,d.revision_id,d.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    JOIN market_data.custom_curve_node_power_intervals p ON(p.catalog_id,p.curve_id,p.version,p.node_id)=(e.catalog_id,e.curve_id,e.version,e.node_id)
    JOIN ref_data.delivery_profiles target ON(target.catalog_id,target.revision_id,target.code)=(p.reference_catalog_id,p.reference_revision_id,p.profile_code)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id)
     AND(c.rule NOT IN('direct','add') OR(c.reference_catalog_id,c.reference_revision_id)<>(p.reference_catalog_id,p.reference_revision_id)
      OR(d.kind,d.clock_code,d.calendar_code,d.quantity_basis,d.interval_minutes) IS DISTINCT FROM(target.kind,target.clock_code,target.calendar_code,target.quantity_basis,target.interval_minutes)))
  THEN RAISE EXCEPTION 'power_interval_grid';END IF;
 END LOOP;
 -- Interval outputs cannot be consumed as date panels, including imported ones.
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources s
  JOIN market_data.custom_curve_edges e ON(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(s.source_catalog_id,s.source_curve_id,s.source_version,s.source_output_node,'values')
  JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(s.catalog_id,s.curve_id,s.version,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,'power_intervals'))
 THEN RAISE EXCEPTION 'power_interval_panel';END IF;
 RETURN NEW;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format THEN RAISE EXCEPTION 'power_interval_policy';
END$$;
ALTER FUNCTION market_data.guard_power_interval_publication() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_power_interval_publication() FROM PUBLIC;
CREATE TRIGGER power_interval_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_power_interval_publication();
DO $$DECLARE body text;needle text:='   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars s';BEGIN
 body=pg_get_functiondef('security.custom_curve_visible(uuid,uuid,integer)'::regprocedure);
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_interval_upgrade_visibility';END IF;
 body=replace(body,needle,$added$   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals p JOIN market_data.tou_product_versions t ON(t.catalog_id,t.product_id,t.version)=(p.product_catalog_id,p.product_id,p.product_version)
    WHERE(p.catalog_id,p.curve_id,p.version)=(d.catalog_id,d.curve_id,d.version) AND(t.state<>'published' OR NOT security.reference_version_visible(p.reference_catalog_id,p.reference_revision_id)))
$added$||needle);EXECUTE body;
END$$;
COMMIT;
