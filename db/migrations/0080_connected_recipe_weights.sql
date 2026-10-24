-- Clean saved-recipe cutover: one connected evaluator and explicit typed weights.
-- Existing installations must independently reset/rebook v1 recipes before adopting
-- this schema baseline. This migration never rewrites immutable economic history.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_versions) THEN
  RAISE EXCEPTION 'recipe_cutover_requires_empty_saved_recipes: explicitly reset/rebook before installing this baseline';
 END IF;
END$$;
ALTER TABLE market_data.custom_curve_versions DROP CONSTRAINT custom_curve_versions_engine_version_check;
ALTER TABLE market_data.custom_curve_versions ADD CHECK(engine_version='custom_curve_recipe_v2');
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','select','weighted_contracts','dates','sample','mean','output'));
ALTER TABLE market_data.custom_curve_node_sources ADD UNIQUE(catalog_id,curve_id,version,node_id,source_catalog_id,source_revision_id,source_curve_code);
ALTER TABLE market_data.custom_curve_edges ADD UNIQUE(catalog_id,curve_id,version,node_id,port,source_node);
CREATE TABLE market_data.custom_curve_contract_weights (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 output_position integer NOT NULL CHECK(output_position BETWEEN 1 AND 120),source_port text NOT NULL,source_node text NOT NULL,
 source_catalog_id uuid NOT NULL,source_revision_id uuid NOT NULL,source_curve_code text NOT NULL,contract_id uuid NOT NULL,
 weight numeric NOT NULL CHECK(weight NOT IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) AND abs(weight)<1e24 AND scale(weight)<=18),
 PRIMARY KEY(catalog_id,curve_id,version,node_id,output_position,source_port,contract_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id,source_port,source_node) REFERENCES market_data.custom_curve_edges(catalog_id,curve_id,version,node_id,port,source_node),
 FOREIGN KEY(catalog_id,curve_id,version,source_node,source_catalog_id,source_revision_id,source_curve_code)
  REFERENCES market_data.custom_curve_node_sources(catalog_id,curve_id,version,node_id,source_catalog_id,source_revision_id,source_curve_code),
 FOREIGN KEY(source_catalog_id,source_revision_id,source_curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
ALTER TABLE market_data.custom_curve_contract_weights ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.custom_curve_contract_weights FORCE ROW LEVEL SECURITY;
CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.custom_curve_contract_weights
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve();
CREATE FUNCTION market_data.guard_recipe_weight() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;
BEGIN
 SELECT * INTO node FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id)=(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.node_id);
 IF node.kind IS DISTINCT FROM 'weighted_contracts' OR node.parameters->>'aggregation' IS NULL OR
  node.parameters->>'aggregation' NOT IN('signed_sum_v1','normalized_weighted_mean_v1') OR
  node.parameters->>'aggregation'='normalized_weighted_mean_v1' AND NEW.weight<0
 THEN RAISE EXCEPTION 'recipe_weights' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER selected_weight_operator BEFORE INSERT OR UPDATE ON market_data.custom_curve_contract_weights
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_recipe_weight();
CREATE POLICY scoped_read ON market_data.custom_curve_contract_weights FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY risk_worker_service ON market_data.custom_curve_contract_weights AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE POLICY current_insert ON market_data.custom_curve_contract_weights AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context());
CREATE POLICY current_update ON market_data.custom_curve_contract_weights AS RESTRICTIVE FOR UPDATE TO openetrm_runtime
 USING(security.current_write_context()) WITH CHECK(security.current_write_context());
CREATE POLICY current_delete ON market_data.custom_curve_contract_weights AS RESTRICTIVE FOR DELETE TO openetrm_runtime
 USING(security.current_write_context());
CREATE POLICY scoped_insert ON market_data.custom_curve_contract_weights FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND security.market_access(catalog_id,'WriteMarketDefinitions') AND security.custom_curve_source_visible(source_catalog_id,source_revision_id));
CREATE POLICY scoped_update ON market_data.custom_curve_contract_weights FOR UPDATE TO openetrm_runtime
 USING(security.current_write_context() AND security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,'WriteMarketDefinitions'))
 WITH CHECK(security.current_write_context() AND security.market_access(catalog_id,'WriteMarketDefinitions') AND security.custom_curve_source_visible(source_catalog_id,source_revision_id));
CREATE POLICY scoped_delete ON market_data.custom_curve_contract_weights FOR DELETE TO openetrm_runtime
 USING(security.current_write_context() AND security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,'WriteMarketDefinitions'));
GRANT SELECT ON market_data.custom_curve_contract_weights TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker;
GRANT INSERT,UPDATE,DELETE ON market_data.custom_curve_contract_weights TO openetrm_runtime;
CREATE OR REPLACE FUNCTION security.publish_custom_curve(catalog uuid,curve uuid,requested_version integer,expected_generation integer) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.custom_curve_versions;ordinal integer;source record;
BEGIN
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing permission' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO row FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(catalog,curve,requested_version) FOR UPDATE;
 IF NOT FOUND OR row.generation<>expected_generation THEN RAISE EXCEPTION 'Stale curve version' USING ERRCODE='40001';END IF;
 IF row.engine_version<>'custom_curve_recipe_v2' THEN RAISE EXCEPTION 'custom_curve_engine' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,requested_version) AND n.kind='weighted_contracts'
   AND(n.parameters->>'aggregation' IS NULL OR n.parameters->>'aggregation' NOT IN('signed_sum_v1','normalized_weighted_mean_v1')
    OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_contract_weights w WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id))
    OR EXISTS(SELECT 1 FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)
      AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_contract_weights w WHERE(w.catalog_id,w.curve_id,w.version,w.node_id,w.source_port)=(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)))
    OR n.parameters->>'aggregation'='normalized_weighted_mean_v1' AND EXISTS(
      SELECT 1 FROM market_data.custom_curve_contract_weights w WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)
      GROUP BY w.output_position HAVING min(w.weight)<0 OR sum(w.weight)<=0)))
 THEN RAISE EXCEPTION 'recipe_weights' USING ERRCODE='P0001';END IF;
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
ALTER FUNCTION security.publish_custom_curve(uuid,uuid,integer,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.publish_custom_curve(uuid,uuid,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.publish_custom_curve(uuid,uuid,integer,integer) TO openetrm_runtime;
COMMIT;
