-- Exact published output imports; all calculated prices remain graph nodes.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','published_source','select','weighted_contracts','dates','sample','mean','output'));
CREATE TABLE market_data.custom_curve_node_published_sources (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 source_catalog_id uuid NOT NULL,source_curve_id uuid NOT NULL,source_version integer NOT NULL,source_output_node text NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id),CHECK(source_catalog_id=catalog_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(source_catalog_id,source_curve_id,source_version,source_output_node)
  REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id)
);
ALTER TABLE market_data.custom_curve_node_published_sources ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.custom_curve_node_published_sources FORCE ROW LEVEL SECURITY;
GRANT SELECT ON market_data.custom_curve_node_published_sources TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker;
GRANT INSERT,UPDATE,DELETE ON market_data.custom_curve_node_published_sources TO openetrm_runtime;
CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.custom_curve_node_published_sources
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve();

-- UNION deduplicates shared paths at each depth; no path enumeration or recursive
-- visibility-policy calls. A walk reaching the boundary with further edges is
-- refused, covering both overdeep graphs and corrupt retained cycles.
CREATE FUNCTION security.custom_curve_closure(catalog uuid,curve uuid,requested_version integer)
RETURNS TABLE(catalog_id uuid,curve_id uuid,version integer,depth integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 WITH RECURSIVE walk(catalog_id,curve_id,version,depth) AS(
  SELECT catalog,curve,requested_version,0
  UNION
  SELECT s.source_catalog_id,s.source_curve_id,s.source_version,w.depth+1 FROM walk w
   JOIN market_data.custom_curve_node_published_sources s USING(catalog_id,curve_id,version) WHERE w.depth<32)
 SELECT catalog_id,curve_id,version,max(depth) FROM walk GROUP BY catalog_id,curve_id,version
$$;
ALTER FUNCTION security.custom_curve_closure(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.custom_curve_closure(uuid,uuid,integer) FROM PUBLIC;

CREATE FUNCTION security.custom_curve_native_sources(catalog uuid,curve uuid,requested_version integer)
RETURNS SETOF market_data.custom_curve_node_sources LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT s.* FROM security.custom_curve_closure(catalog,curve,requested_version) d
 JOIN market_data.custom_curve_node_sources s USING(catalog_id,curve_id,version)
$$;
ALTER FUNCTION security.custom_curve_native_sources(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.custom_curve_native_sources(uuid,uuid,integer) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.custom_curve_visible(catalog uuid,curve uuid,requested_version integer)
RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData')
 AND EXISTS(SELECT 1 FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(catalog,curve,requested_version))
 AND(SELECT count(*)<=256 FROM security.custom_curve_closure(catalog,curve,requested_version))
 AND NOT EXISTS(SELECT 1 FROM security.custom_curve_closure(catalog,curve,requested_version) d
  LEFT JOIN market_data.custom_curve_versions v USING(catalog_id,curve_id,version)
  WHERE v.curve_id IS NULL OR v.engine_version<>'custom_curve_recipe_v2'
   OR(d.depth>0 AND v.state<>'published')
   OR(v.state<>'published' AND NOT(security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions')))
   OR(d.depth>=32 AND EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources s
      WHERE(s.catalog_id,s.curve_id,s.version)=(d.catalog_id,d.curve_id,d.version)))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version)=(d.catalog_id,d.curve_id,d.version)
      AND NOT security.custom_curve_source_visible(s.source_catalog_id,s.source_revision_id))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars s WHERE(s.catalog_id,s.curve_id,s.version)=(d.catalog_id,d.curve_id,d.version)
      AND NOT security.reference_version_visible(s.reference_catalog_id,s.reference_revision_id))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_schedules s
      JOIN market_data.date_schedule_versions schedule ON(schedule.catalog_id,schedule.schedule_id,schedule.version)=(s.schedule_catalog_id,s.schedule_id,s.schedule_version)
      WHERE(s.catalog_id,s.curve_id,s.version)=(d.catalog_id,d.curve_id,d.version)
       AND(schedule.state<>'published' OR NOT security.contract_date_schedule_visible(s.schedule_catalog_id,s.schedule_id,s.schedule_version)))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources s
      LEFT JOIN market_data.custom_curve_versions parent ON(parent.catalog_id,parent.curve_id,parent.version)=(s.source_catalog_id,s.source_curve_id,s.source_version)
      WHERE(s.catalog_id,s.curve_id,s.version)=(d.catalog_id,d.curve_id,d.version)
       AND(parent.state IS DISTINCT FROM 'published' OR parent.output_node IS DISTINCT FROM s.source_output_node))
   OR(v.state='published' AND EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n
      WHERE(n.catalog_id,n.curve_id,n.version)=(d.catalog_id,d.curve_id,d.version)
       AND((n.kind='published_source') IS DISTINCT FROM EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources s
        WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id))
        OR n.kind='published_source' AND n.parameters<>'{"selection":"all_output_points_v1"}'::jsonb)))
   OR(EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
      AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
    AND NOT EXISTS(SELECT 1 FROM security.request_contexts c
     JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
     JOIN market_data.custom_curve_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
     WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
      AND t.catalog_id=catalog AND k.known_from<=t.known_at)))
$$;

CREATE FUNCTION security.custom_curve_uses_revision(catalog uuid,curve uuid,requested_version integer,revision uuid)
RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.custom_curve_visible(catalog,curve,requested_version)
 AND EXISTS(SELECT 1 FROM security.custom_curve_native_sources(catalog,curve,requested_version))
 AND NOT EXISTS(SELECT 1 FROM security.custom_curve_native_sources(catalog,curve,requested_version) s
  WHERE(s.source_catalog_id,s.source_revision_id) IS DISTINCT FROM(catalog,revision))
$$;
ALTER FUNCTION security.custom_curve_uses_revision(uuid,uuid,integer,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.custom_curve_uses_revision(uuid,uuid,integer,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.custom_curve_uses_revision(uuid,uuid,integer,uuid) TO openetrm_runtime,openetrm_risk_worker;

CREATE POLICY scoped_read ON market_data.custom_curve_node_published_sources FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY risk_worker_service ON market_data.custom_curve_node_published_sources AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE POLICY current_insert ON market_data.custom_curve_node_published_sources AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context());
CREATE POLICY current_update ON market_data.custom_curve_node_published_sources AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context());
CREATE POLICY current_delete ON market_data.custom_curve_node_published_sources AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context());
CREATE POLICY scoped_insert ON market_data.custom_curve_node_published_sources FOR INSERT TO openetrm_runtime
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.custom_curve_visible(source_catalog_id,source_curve_id,source_version));
CREATE POLICY scoped_update ON market_data.custom_curve_node_published_sources FOR UPDATE TO openetrm_runtime
 USING(security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,'WriteMarketDefinitions'))
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.custom_curve_visible(source_catalog_id,source_curve_id,source_version));
CREATE POLICY scoped_delete ON market_data.custom_curve_node_published_sources FOR DELETE TO openetrm_runtime
 USING(security.custom_curve_visible(catalog_id,curve_id,version) AND security.market_access(catalog_id,'WriteMarketDefinitions'));

CREATE FUNCTION security.guard_published_curve_source() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id,n.kind)=
  (NEW.catalog_id,NEW.curve_id,NEW.version,NEW.node_id,'published_source') AND n.parameters='{"selection":"all_output_points_v1"}'::jsonb)
  OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state)=
   (NEW.source_catalog_id,NEW.source_curve_id,NEW.source_version,NEW.source_output_node,'published'))
  OR NOT security.custom_curve_visible(NEW.source_catalog_id,NEW.source_curve_id,NEW.source_version)
 THEN RAISE EXCEPTION 'published_curve_source';END IF;
 IF EXISTS(SELECT 1 FROM security.custom_curve_closure(NEW.source_catalog_id,NEW.source_curve_id,NEW.source_version) d
  WHERE(d.catalog_id,d.curve_id,d.version)=(NEW.catalog_id,NEW.curve_id,NEW.version)) THEN RAISE EXCEPTION 'custom_curve_cycle';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION security.guard_published_curve_source() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.guard_published_curve_source() FROM PUBLIC;
CREATE TRIGGER published_source_binding BEFORE INSERT OR UPDATE ON market_data.custom_curve_node_published_sources
 FOR EACH ROW EXECUTE FUNCTION security.guard_published_curve_source();

-- Preserve the installed command/security contracts and replace their direct
-- source lookups with the same exact transitive source relation. An unexpected
-- lookup shape is refused instead of guessing where to rewrite the command.
DO $upgrade$
DECLARE signature text;definition text;before text;after text;
BEGIN
 FOR signature,before,after IN SELECT * FROM(VALUES
 ('risk.retain_connected(risk.jobs,jsonb)',
  'IF jsonb_typeof(market) IS DISTINCT FROM ''object'' OR jsonb_typeof(market->''recipes'') IS DISTINCT FROM ''array''',
  'IF jsonb_typeof(market) IS DISTINCT FROM ''object'' OR coalesce(market->>''sourceExecutionConvention'','''') NOT IN(''fixed_total_cash_execution_pnl_v1'',''contract_lot_execution_pnl_v2'') OR jsonb_typeof(market->''recipes'') IS DISTINCT FROM ''array'''),
 ('security.validate_custom_projection(uuid,jsonb)',
  'SELECT 1 FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,curve,requested_version)',
  'SELECT 1 FROM security.custom_curve_native_sources(catalog,curve,requested_version) s'),
 ('security.save_projected_swap_with_parties(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,text,text,text,date,date,timestamp with time zone,date,date,timestamp with time zone,text,numeric,numeric,numeric,boolean,timestamp with time zone,timestamp with time zone,timestamp with time zone,text,text,jsonb,jsonb,text,uuid,integer,text,jsonb,uuid[],jsonb)',
  E'SELECT 1 FROM market_data.custom_curve_node_sources s\n  WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,(custom_projection->>''curveId'')::uuid,(custom_projection->>''version'')::integer)\n   AND',
  'SELECT 1 FROM security.custom_curve_native_sources(catalog,(custom_projection->>''curveId'')::uuid,(custom_projection->>''version'')::integer) s WHERE'),
 ('risk.retain_connected(risk.jobs,jsonb)',
  E'SELECT 1 FROM market_data.custom_curve_node_sources s\n    WHERE(s.catalog_id,s.curve_id,s.version)=(job.catalog_id,(recipe->>''curveId'')::uuid,(recipe->>''version'')::integer)\n     AND',
  'SELECT 1 FROM security.custom_curve_native_sources(job.catalog_id,(recipe->>''curveId'')::uuid,(recipe->>''version'')::integer) s WHERE'),
 ('risk.retain_connected(risk.jobs,jsonb)',
  'JOIN market_data.custom_curve_node_sources cs ON(cs.catalog_id,cs.curve_id,cs.version)=(jc.catalog_id,jc.curve_id,jc.version)',
  'CROSS JOIN LATERAL security.custom_curve_native_sources(jc.catalog_id,jc.curve_id,jc.version) cs'),
 ('risk.authorized(risk.jobs)',
  E'JOIN market_data.custom_curve_node_calendars cc\n  ON(cc.catalog_id,cc.curve_id,cc.version)=(jc.catalog_id,jc.curve_id,jc.version)',
  'CROSS JOIN LATERAL security.custom_curve_closure(jc.catalog_id,jc.curve_id,jc.version) deps JOIN market_data.custom_curve_node_calendars cc ON(cc.catalog_id,cc.curve_id,cc.version)=(deps.catalog_id,deps.curve_id,deps.version)'),
 ('risk.authorized(risk.jobs)',
  E'JOIN market_data.custom_curve_node_schedules cs\n  ON(cs.catalog_id,cs.curve_id,cs.version)=(jc.catalog_id,jc.curve_id,jc.version)',
  'CROSS JOIN LATERAL security.custom_curve_closure(jc.catalog_id,jc.curve_id,jc.version) deps JOIN market_data.custom_curve_node_schedules cs ON(cs.catalog_id,cs.curve_id,cs.version)=(deps.catalog_id,deps.curve_id,deps.version)'),
 ('security.publish_custom_curve(uuid,uuid,integer,integer)',
  'IF row.state=''published'' THEN RETURN row.publication_order;END IF;',
  E'IF NOT security.custom_curve_visible(catalog,curve,requested_version) OR EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,requested_version) AND((n.kind=''published_source'') IS DISTINCT FROM EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources s WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)) OR n.kind=''published_source'' AND n.parameters<>''{"selection":"all_output_points_v1"}''::jsonb)) THEN RAISE EXCEPTION ''published_curve_source'';END IF;\n IF row.state=''published'' THEN RETURN row.publication_order;END IF;')
 )changes(signature,before,after)
 LOOP
  definition=pg_get_functiondef(signature::regprocedure);
  IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'published_curve_sources: unexpected command %',signature;END IF;
  EXECUTE replace(definition,before,after);
 END LOOP;
END $upgrade$;
COMMIT;
