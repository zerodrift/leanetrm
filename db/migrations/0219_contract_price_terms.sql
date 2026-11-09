-- D363. Exact custom source settlement terms and contract sampling maps.
-- Existing recipes receive no rows and retain their original declarations.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.custom_curve_requests DROP CONSTRAINT custom_curve_requests_operation_check;
ALTER TABLE market_data.custom_curve_requests ADD CHECK(operation IN('save','publish','contract_price_terms'));
ALTER POLICY scoped_insert ON market_data.custom_curve_requests WITH CHECK(
 actor_id=security.custom_curve_actor(catalog_id,CASE WHEN operation IN('save','contract_price_terms') THEN 'WriteMarketDefinitions' ELSE 'SealMarketDefinitions' END)
 AND security.custom_curve_source_visible(catalog_id,definition_revision_id) AND security.custom_curve_visible(catalog_id,curve_id,version));
CREATE TABLE market_data.custom_curve_price_mappings (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 source_catalog_id uuid NOT NULL,source_revision_id uuid NOT NULL,source_curve_code text NOT NULL,
 target_contract_id uuid NOT NULL,source_contract_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id,target_contract_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id,source_catalog_id,source_revision_id,source_curve_code)
  REFERENCES market_data.custom_curve_node_sources(catalog_id,curve_id,version,node_id,source_catalog_id,source_revision_id,source_curve_code),
 FOREIGN KEY(source_catalog_id,source_revision_id,source_curve_code,target_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(source_catalog_id,source_revision_id,source_curve_code,source_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE TABLE market_data.custom_curve_price_terms (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 120),contract_id uuid,
 settlement text NOT NULL CHECK(settlement IN('exchange_final_v1','published_index_v1','derived_inputs_v1')),
 publication_id uuid,publication_version integer,reference_catalog_id uuid,reference_revision_id uuid,index_code text,
 observation_rule text NOT NULL CHECK(observation_rule='single_assessment_v1'),
 pending_policy text NOT NULL CHECK(pending_policy='require_final_v1'),projection_policy text,
 mapping_rule text NOT NULL CHECK(mapping_rule IN('same_delivery_period_v1','explicit_contracts_v1')),
 PRIMARY KEY(catalog_id,curve_id,version,node_id,ordinal),
 UNIQUE NULLS NOT DISTINCT(catalog_id,curve_id,version,node_id,contract_id),
 CHECK((ordinal=0)=(contract_id IS NULL)),
 CHECK((settlement='published_index_v1' AND publication_id IS NOT NULL AND publication_version>0 AND reference_catalog_id IS NOT NULL AND reference_revision_id IS NOT NULL AND index_code IS NOT NULL AND (projection_policy IS NULL OR projection_policy='forward_curve_no_basis_v1')) OR
       (settlement<>'published_index_v1' AND publication_id IS NULL AND publication_version IS NULL AND reference_catalog_id IS NULL AND reference_revision_id IS NULL AND index_code IS NULL AND projection_policy IS NULL)),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_node_sources(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id,contract_id) REFERENCES market_data.custom_curve_price_mappings(catalog_id,curve_id,version,node_id,target_contract_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,publication_id,publication_version,index_code) REFERENCES market_data.price_publication_indices(catalog_id,publication_id,version,index_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code)
);
CREATE FUNCTION market_data.guard_contract_price_term() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.settlement='published_index_v1' AND (NOT security.price_publication_visible(NEW.catalog_id,NEW.publication_id,NEW.publication_version) OR
  NOT EXISTS(SELECT 1 FROM market_data.price_publication_versions p WHERE(p.catalog_id,p.publication_id,p.version,p.reference_catalog_id,p.reference_revision_id)=
   (NEW.catalog_id,NEW.publication_id,NEW.publication_version,NEW.reference_catalog_id,NEW.reference_revision_id)))
 THEN RAISE EXCEPTION 'contract_price_publication_unavailable' USING ERRCODE='42501';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION market_data.guard_contract_price_term() OWNER TO openetrm_security_owner;
CREATE TRIGGER selected_contract_price_term BEFORE INSERT OR UPDATE ON market_data.custom_curve_price_terms
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_contract_price_term();
CREATE FUNCTION market_data.guard_contract_price_mapping() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version,t.node_id,t.ordinal)=(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.node_id,0)
  AND t.mapping_rule='same_delivery_period_v1') AND EXISTS(SELECT 1 FROM market_data.contracts a JOIN market_data.contracts b ON(b.catalog_id,b.revision_id,b.id)=(NEW.source_catalog_id,NEW.source_revision_id,NEW.source_contract_id)
  WHERE(a.catalog_id,a.revision_id,a.id)=(NEW.source_catalog_id,NEW.source_revision_id,NEW.target_contract_id) AND(a.delivery_from,a.delivery_to) IS DISTINCT FROM(b.delivery_from,b.delivery_to))
 THEN RAISE EXCEPTION 'contract_price_mapping' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER selected_contract_price_mapping BEFORE INSERT OR UPDATE ON market_data.custom_curve_price_mappings
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_contract_price_mapping();
CREATE FUNCTION market_data.guard_contract_price_publication() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.state<>'published' THEN RETURN NEW;END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_price_mappings m
  JOIN market_data.custom_curve_node_sources src USING(catalog_id,curve_id,version,node_id)
  JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(src.source_catalog_id,src.source_revision_id,src.source_curve_code)
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.source_catalog_id,m.source_revision_id,m.source_contract_id)
  JOIN ref_data.settlement_targets target ON(target.catalog_id,target.revision_id,target.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
  JOIN LATERAL(SELECT term.* FROM market_data.custom_curve_price_terms term WHERE(term.catalog_id,term.curve_id,term.version,term.node_id)=(m.catalog_id,m.curve_id,m.version,m.node_id)
   AND(term.contract_id=m.target_contract_id OR term.ordinal=0) ORDER BY term.ordinal DESC LIMIT 1)t ON true
  WHERE(m.catalog_id,m.curve_id,m.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) AND(
   t.settlement='exchange_final_v1' AND(c.rule<>'direct' OR k.kind<>'future') OR t.settlement='derived_inputs_v1' AND c.rule='direct' OR
   t.settlement='published_index_v1' AND(k.kind<>'forward' OR target.kind<>'index' OR target.index_b_code IS NOT NULL OR
    (target.index_a_code=t.index_code AND t.projection_policy IS NOT NULL) OR(target.index_a_code<>t.index_code AND t.projection_policy IS DISTINCT FROM 'forward_curve_no_basis_v1') OR
    NOT security.price_publication_visible(t.catalog_id,t.publication_id,t.publication_version) OR
    NOT EXISTS(SELECT 1 FROM market_data.price_publication_indices i WHERE(i.catalog_id,i.publication_id,i.version,i.index_code,i.quote_code,i.commodity_code)=
     (t.catalog_id,t.publication_id,t.publication_version,t.index_code,c.quote_code,c.commodity_code)) OR
    NOT EXISTS(SELECT 1 FROM market_data.index_fixing_observations f WHERE(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to,f.quote_code,f.implementation)=
     (m.source_catalog_id,m.source_revision_id,t.index_code,k.delivery_from,k.delivery_to,c.quote_code,'published_index_observations_v1')))))
 THEN RAISE EXCEPTION 'contract_price_terms_invalid_source' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t JOIN market_data.custom_curve_price_mappings m USING(catalog_id,curve_id,version,node_id)
  JOIN market_data.contracts a ON(a.catalog_id,a.revision_id,a.id)=(m.source_catalog_id,m.source_revision_id,m.target_contract_id)
  JOIN market_data.contracts b ON(b.catalog_id,b.revision_id,b.id)=(m.source_catalog_id,m.source_revision_id,m.source_contract_id)
  WHERE(t.catalog_id,t.curve_id,t.version,t.ordinal)=(NEW.catalog_id,NEW.curve_id,NEW.version,0) AND t.mapping_rule='same_delivery_period_v1' AND(a.delivery_from,a.delivery_to) IS DISTINCT FROM(b.delivery_from,b.delivery_to))
 OR EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version)=(NEW.catalog_id,NEW.curve_id,NEW.version)
  AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms d WHERE(d.catalog_id,d.curve_id,d.version,d.node_id,d.ordinal)=(t.catalog_id,t.curve_id,t.version,t.node_id,0) AND d.mapping_rule=t.mapping_rule))
 OR EXISTS(SELECT 1 FROM market_data.custom_curve_price_mappings m WHERE(m.catalog_id,m.curve_id,m.version)=(NEW.catalog_id,NEW.curve_id,NEW.version)
  AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version,t.node_id,t.ordinal)=(m.catalog_id,m.curve_id,m.version,m.node_id,0)))
 OR EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t JOIN market_data.custom_curve_node_sources s USING(catalog_id,curve_id,version,node_id)
  JOIN market_data.curve_members c ON(c.catalog_id,c.revision_id,c.curve_code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
  WHERE(t.catalog_id,t.curve_id,t.version,t.ordinal)=(NEW.catalog_id,NEW.curve_id,NEW.version,0)
  AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_price_mappings m WHERE(m.catalog_id,m.curve_id,m.version,m.node_id,m.target_contract_id)=(t.catalog_id,t.curve_id,t.version,t.node_id,c.contract_id)))
 THEN RAISE EXCEPTION 'contract_price_terms_incomplete' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION market_data.guard_contract_price_publication() OWNER TO openetrm_security_owner;
CREATE TRIGGER contract_price_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_contract_price_publication();
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['custom_curve_price_terms','custom_curve_price_mappings'] LOOP
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
-- Invoker reads retain ordinary graph RLS. Unavailable publication dependencies
-- refuse the read instead of making a saved source silently lose its transition.
CREATE FUNCTION market_data.read_custom_curve_price_terms(catalog uuid,curve uuid,selected_version integer,node text) RETURNS jsonb
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version,t.node_id)=(catalog,curve,selected_version,node)
  AND t.publication_id IS NOT NULL AND NOT security.price_publication_visible(t.catalog_id,t.publication_id,t.publication_version))
 THEN RAISE EXCEPTION 'contract_price_publication_unavailable' USING ERRCODE='42501';END IF;
 WITH terms AS(SELECT t.*,jsonb_build_object('settlement',t.settlement,'publication',CASE WHEN publication_id IS NULL THEN NULL ELSE
  jsonb_build_object('catalogId',t.catalog_id,'id',t.publication_id,'version',t.publication_version) END,
  'index',CASE WHEN index_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',reference_catalog_id,'revisionId',reference_revision_id),'code',index_code) END,
  'observationRule',observation_rule,'pendingPolicy',pending_policy,'projectionPolicy',projection_policy) value
  FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version,t.node_id)=(catalog,curve,selected_version,node))
 SELECT jsonb_build_object('default',d.value,'mappingRule',d.mapping_rule,
  'replacements',coalesce((SELECT jsonb_agg(jsonb_build_object('contract',jsonb_build_object('revision',jsonb_build_object('catalogId',s.source_catalog_id,'revisionId',s.source_revision_id),'id',t.contract_id),'terms',t.value) ORDER BY t.ordinal)
    FROM terms t JOIN market_data.custom_curve_node_sources s USING(catalog_id,curve_id,version,node_id) WHERE t.ordinal>0),'[]'::jsonb),
  'mappings',coalesce((SELECT jsonb_agg(jsonb_build_object('target',jsonb_build_object('revision',jsonb_build_object('catalogId',m.source_catalog_id,'revisionId',m.source_revision_id),'id',m.target_contract_id),
   'source',jsonb_build_object('revision',jsonb_build_object('catalogId',m.source_catalog_id,'revisionId',m.source_revision_id),'id',m.source_contract_id)) ORDER BY m.target_contract_id)
    FROM market_data.custom_curve_price_mappings m WHERE(m.catalog_id,m.curve_id,m.version,m.node_id)=(catalog,curve,selected_version,node)),'[]'::jsonb)) INTO result FROM terms d WHERE d.ordinal=0;
 RETURN result;
END$$;
REVOKE ALL ON FUNCTION market_data.read_custom_curve_price_terms(uuid,uuid,integer,text),market_data.guard_contract_price_term(),market_data.guard_contract_price_mapping(),market_data.guard_contract_price_publication() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.read_custom_curve_price_terms(uuid,uuid,integer,text) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
