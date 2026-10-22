-- Computed-curve combinations retain typed weights on exact graph edges.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','published_source','select','weighted_contracts','weighted_panels','dates','sample','mean','output'));
CREATE TABLE market_data.custom_curve_panel_weights (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 source_port text NOT NULL,source_node text NOT NULL,
 weight numeric NOT NULL CHECK(weight NOT IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) AND abs(weight)<1e24 AND scale(weight)<=18),
 PRIMARY KEY(catalog_id,curve_id,version,node_id,source_port),
 FOREIGN KEY(catalog_id,curve_id,version,node_id,source_port,source_node)
  REFERENCES market_data.custom_curve_edges(catalog_id,curve_id,version,node_id,port,source_node)
);
ALTER TABLE market_data.custom_curve_panel_weights ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.custom_curve_panel_weights FORCE ROW LEVEL SECURITY;
CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.custom_curve_panel_weights
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve();
CREATE FUNCTION market_data.guard_panel_weight() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;
BEGIN
 SELECT * INTO node FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id)=(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.node_id);
 IF node.kind IS DISTINCT FROM 'weighted_panels' OR node.parameters->>'alignment' IS DISTINCT FROM 'exact_delivery_intervals_v1'
  OR node.parameters->>'aggregation' IS NULL OR node.parameters->>'aggregation' NOT IN('signed_sum_v1','normalized_weighted_mean_v1')
  OR node.parameters->>'aggregation'='normalized_weighted_mean_v1' AND NEW.weight<0
  OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id)=
    (NEW.catalog_id,NEW.curve_id,NEW.version,NEW.source_node) AND n.kind IN('sample','mean','published_source','weighted_panels'))
 THEN RAISE EXCEPTION 'recipe_panel_weights' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER selected_panel_operator BEFORE INSERT OR UPDATE ON market_data.custom_curve_panel_weights
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_panel_weight();
CREATE FUNCTION market_data.guard_panel_publication() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.state='published' AND OLD.state='draft' AND EXISTS(
  SELECT 1 FROM market_data.custom_curve_panel_weights w JOIN market_data.custom_curve_nodes n
   ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(w.catalog_id,w.curve_id,w.version,w.node_id)
  WHERE(w.catalog_id,w.curve_id,w.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) AND n.kind<>'weighted_panels')
 THEN RAISE EXCEPTION 'recipe_panel_weights' USING ERRCODE='P0001';END IF;
 IF NEW.state='published' AND OLD.state='draft' AND EXISTS(
  SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) AND n.kind='weighted_panels'
  AND(n.parameters->>'alignment' IS DISTINCT FROM 'exact_delivery_intervals_v1' OR n.parameters->>'aggregation' IS NULL
   OR n.parameters->>'aggregation' NOT IN('signed_sum_v1','normalized_weighted_mean_v1')
   OR (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)) NOT BETWEEN 1 AND 32
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_edges e LEFT JOIN market_data.custom_curve_panel_weights w
     ON(w.catalog_id,w.curve_id,w.version,w.node_id,w.source_port,w.source_node)=(e.catalog_id,e.curve_id,e.version,e.node_id,e.port,e.source_node)
     JOIN market_data.custom_curve_nodes source ON(source.catalog_id,source.curve_id,source.version,source.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
     WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)
      AND(w.source_port IS NULL OR source.kind NOT IN('sample','mean','published_source','weighted_panels')))
   OR n.parameters->>'aggregation'='normalized_weighted_mean_v1' AND EXISTS(
     SELECT 1 FROM market_data.custom_curve_panel_weights w WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)
     GROUP BY w.node_id HAVING min(w.weight)<0 OR sum(w.weight)<=0)))
 THEN RAISE EXCEPTION 'recipe_panel_weights' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER panel_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_panel_publication();
CREATE POLICY scoped_read ON market_data.custom_curve_panel_weights FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY risk_worker_service ON market_data.custom_curve_panel_weights AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE POLICY current_insert ON market_data.custom_curve_panel_weights AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context());
CREATE POLICY current_update ON market_data.custom_curve_panel_weights AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context());
CREATE POLICY current_delete ON market_data.custom_curve_panel_weights AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context());
CREATE POLICY scoped_insert ON market_data.custom_curve_panel_weights FOR INSERT TO openetrm_runtime
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY scoped_update ON market_data.custom_curve_panel_weights FOR UPDATE TO openetrm_runtime
 USING(security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,'WriteMarketDefinitions'))
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY scoped_delete ON market_data.custom_curve_panel_weights FOR DELETE TO openetrm_runtime
 USING(security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,'WriteMarketDefinitions'));
GRANT SELECT ON market_data.custom_curve_panel_weights TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker;
GRANT INSERT,UPDATE,DELETE ON market_data.custom_curve_panel_weights TO openetrm_runtime;
COMMIT;
