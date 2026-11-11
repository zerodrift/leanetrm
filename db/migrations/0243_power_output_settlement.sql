-- Physical power follows the exact settlement declared by its resolved output.
-- Drain preview admission before applying this schema change.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.price_publication_versions DROP CONSTRAINT price_publication_versions_implementation_check;
ALTER TABLE market_data.price_publication_versions ADD CHECK(implementation IN(
 'gas_daily_explicit_v1','gas_monthly_explicit_v1','crude_placeholder_v1','ngl_placeholder_v1','lng_placeholder_v1','power_placeholder_v1','power_captured_hourly_v1'));
CREATE TABLE market_data.custom_curve_power_settlements (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 publication_id uuid NOT NULL,publication_version integer NOT NULL,index_code ref_data.code NOT NULL,source_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id)
  REFERENCES market_data.custom_curve_node_power_intervals(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,publication_id,publication_version,index_code)
  REFERENCES market_data.price_publication_indices(catalog_id,publication_id,version,index_code),
 FOREIGN KEY(catalog_id,publication_id,publication_version,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.price_publication_versions(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(source_id,catalog_id,reference_catalog_id,reference_revision_id)
  REFERENCES power_data.sources(id,catalog_id,reference_catalog_id,reference_revision_id)
);
COMMENT ON TABLE market_data.custom_curve_power_settlements IS
 'Exact physical location output settlement, consumed by Side interval valuation; independent of underlying futures finals. Captured hourly mode asserts no official finality.';
ALTER TABLE market_data.custom_curve_power_settlements ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.custom_curve_power_settlements FORCE ROW LEVEL SECURITY;
CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.custom_curve_power_settlements
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve();
CREATE CONSTRAINT TRIGGER agent_draft_write AFTER INSERT OR UPDATE OR DELETE ON market_data.custom_curve_power_settlements
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.check_custom_curve_agent_write();
CREATE POLICY scoped_read ON market_data.custom_curve_power_settlements FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY risk_worker_service ON market_data.custom_curve_power_settlements AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
DO $$DECLARE op text;BEGIN
 FOREACH op IN ARRAY ARRAY['INSERT','UPDATE','DELETE'] LOOP
  EXECUTE format('CREATE POLICY current_%s ON market_data.custom_curve_power_settlements AS RESTRICTIVE FOR %s TO openetrm_runtime %s',lower(op),op,
    CASE op WHEN 'INSERT' THEN 'WITH CHECK(security.current_write_context())' WHEN 'UPDATE' THEN 'USING(security.current_write_context()) WITH CHECK(security.current_write_context())' ELSE 'USING(security.current_write_context())' END);
  EXECUTE format('CREATE POLICY scoped_%s ON market_data.custom_curve_power_settlements FOR %s TO openetrm_runtime %s',lower(op),op,
    CASE WHEN op='INSERT' THEN 'WITH CHECK' ELSE 'USING' END||'((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))');
 END LOOP;
END$$;
GRANT SELECT ON market_data.custom_curve_power_settlements TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker;
GRANT INSERT,UPDATE,DELETE ON market_data.custom_curve_power_settlements TO openetrm_runtime;

-- Preserve existing geometric admission. The two new scalar policies are
-- independently validated below; exact identities live only in FK columns.
DO $$DECLARE body text;needle text;replacement text;signature regprocedure;BEGIN
 FOR signature,needle,replacement IN SELECT * FROM(VALUES
  ('market_data.guard_power_interval_publication()'::regprocedure,'node.parameters<>expected','(node.parameters-ARRAY[''settlementPolicy'',''projectionPolicy''])<>expected'),
  ('market_data.power_saved_sources(uuid,uuid,integer)'::regprocedure,'node.parameters<>jsonb_build_object','(node.parameters-ARRAY[''settlementPolicy'',''projectionPolicy''])<>jsonb_build_object')) patches LOOP
  body=pg_get_functiondef(signature);
  IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_settlement_migration';END IF;
  EXECUTE replace(body,needle,replacement);
 END LOOP;
END$$;

CREATE FUNCTION market_data.guard_power_settlement_publication() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE binding record;node market_data.custom_curve_nodes;native market_data.curves;
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 FOR binding IN SELECT p.*,s.publication_id,s.publication_version,s.index_code,s.source_id,s.reference_catalog_id settlement_reference_catalog_id,s.reference_revision_id settlement_reference_revision_id
 FROM market_data.custom_curve_node_power_intervals p LEFT JOIN market_data.custom_curve_power_settlements s USING(catalog_id,curve_id,version,node_id)
 WHERE(p.catalog_id,p.curve_id,p.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) LOOP
  SELECT * INTO node FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id)=(binding.catalog_id,binding.curve_id,binding.version,binding.node_id);
  IF binding.publication_id IS NULL THEN
   IF node.parameters ?| ARRAY['settlementPolicy','projectionPolicy'] THEN RAISE EXCEPTION 'power_settlement_binding';END IF;
   CONTINUE;
  END IF;
  IF node.parameters->>'settlementPolicy' IS DISTINCT FROM 'captured_required_for_ended_v1' OR
     node.parameters->>'projectionPolicy' IS DISTINCT FROM 'flat_native_mark_replace_actual_v1' OR
     (binding.reference_catalog_id,binding.reference_revision_id) IS DISTINCT FROM(binding.settlement_reference_catalog_id,binding.settlement_reference_revision_id) OR
     NOT security.price_publication_visible(binding.catalog_id,binding.publication_id,binding.publication_version)
  THEN RAISE EXCEPTION 'power_settlement_binding';END IF;
  IF node.parameters->>'implementation'='saved_product_forwards_v1' THEN
   SELECT c.* INTO native FROM market_data.power_saved_sources(NEW.catalog_id,NEW.curve_id,NEW.version) s
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code) LIMIT 1;
  ELSE
   SELECT c.* INTO native FROM market_data.custom_curve_node_sources s
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    WHERE(s.catalog_id,s.curve_id,s.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) LIMIT 1;
  END IF;
  IF native.code IS NULL OR NOT EXISTS(
   SELECT 1 FROM market_data.price_publication_versions publication
   JOIN market_data.price_publication_indices i USING(catalog_id,publication_id,version)
   JOIN ref_data.price_index_definitions idx ON(idx.catalog_id,idx.revision_id,idx.code)=(i.reference_catalog_id,i.reference_revision_id,i.index_code)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(i.reference_catalog_id,i.reference_revision_id,i.quote_code)
   JOIN ref_data.quantity_specifications energy ON(energy.catalog_id,energy.revision_id,energy.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   JOIN ref_data.delivery_profiles profile ON(profile.catalog_id,profile.revision_id,profile.code)=(binding.reference_catalog_id,binding.reference_revision_id,binding.profile_code)
   JOIN power_data.sources source ON(source.id,source.catalog_id,source.reference_catalog_id,source.reference_revision_id)=
    (binding.source_id,binding.catalog_id,binding.reference_catalog_id,binding.reference_revision_id)
   WHERE(publication.catalog_id,publication.publication_id,publication.version,i.index_code)=
    (binding.catalog_id,binding.publication_id,binding.publication_version,binding.index_code)
    AND publication.implementation='power_captured_hourly_v1'
    AND i.statistic=CASE node.parameters->>'market' WHEN 'DA' THEN 'day_ahead_hourly_total_lmp' WHEN 'RT' THEN 'real_time_hourly_total_lmp' END
    AND(i.quote_code,i.commodity_code,idx.location_code)=(native.quote_code,native.commodity_code,native.location_code)
    AND(source.location_code,source.currency_code,source.quantity_code,source.clock_code,source.market,source.minutes)=
     (native.location_code,q.currency_code,q.quantity_code,profile.clock_code,node.parameters->>'market',profile.interval_minutes)
    AND publication.clock_code=source.clock_code AND source.provider='PJM'
    AND(source.dataset,source.market) IN(('da_hrl_lmps','DA'),('rt_hrl_lmps','RT'))
    AND source.minutes=60 AND q.kind='outright' AND q.multiplier=1 AND energy.unit_code='megawatt_hour'
    AND energy.commodity_code=native.commodity_code AND security.reference_version_visible(binding.reference_catalog_id,binding.reference_revision_id)
  ) THEN RAISE EXCEPTION 'power_settlement_binding';END IF;
 END LOOP;
 RETURN NEW;
END$$;
ALTER FUNCTION market_data.guard_power_settlement_publication() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_power_settlement_publication() FROM PUBLIC;
CREATE TRIGGER power_settlement_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_power_settlement_publication();

-- A graph's visible closure includes its exact physical publication version.
-- Source metadata is immutable and pinned to the already-authorized exact
-- market/reference catalogs by the FK above.
DO $$DECLARE body text;needle text:='   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars s';BEGIN
 body=pg_get_functiondef('security.custom_curve_visible(uuid,uuid,integer)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_settlement_visibility_migration';END IF;
 EXECUTE replace(body,needle,$patch$   OR EXISTS(SELECT 1 FROM market_data.custom_curve_power_settlements p
      WHERE(p.catalog_id,p.curve_id,p.version)=(d.catalog_id,d.curve_id,d.version)
       AND NOT security.price_publication_visible(p.catalog_id,p.publication_id,p.publication_version))
$patch$||needle);
END$$;

CREATE FUNCTION market_data.read_power_interval_terms(catalog uuid,curve uuid,selected_version integer,node text) RETURNS jsonb
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE p market_data.custom_curve_node_power_intervals;s market_data.custom_curve_power_settlements;result jsonb;
BEGIN
 SELECT * INTO p FROM market_data.custom_curve_node_power_intervals output_terms WHERE(output_terms.catalog_id,output_terms.curve_id,output_terms.version,output_terms.node_id)=(catalog,curve,selected_version,node);
 IF NOT FOUND THEN RETURN '{}'::jsonb;END IF;
 result=jsonb_build_object('product',p.product_id::text,'productVersion',p.product_version::text,'profile',p.profile_code);
 SELECT * INTO s FROM market_data.custom_curve_power_settlements selected WHERE(selected.catalog_id,selected.curve_id,selected.version,selected.node_id)=(catalog,curve,selected_version,node);
 IF FOUND THEN
  IF NOT security.price_publication_visible(s.catalog_id,s.publication_id,s.publication_version) OR NOT EXISTS(SELECT 1 FROM power_data.sources source WHERE source.id=s.source_id)
  THEN RAISE EXCEPTION 'power_settlement_unavailable' USING ERRCODE='42501';END IF;
  result=result||jsonb_build_object('settlementPublication',s.publication_id::text,'settlementVersion',s.publication_version::text,'settlementIndex',s.index_code,'settlementSource',s.source_id::text);
 END IF;
 RETURN result;
END$$;
REVOKE ALL ON FUNCTION market_data.read_power_interval_terms(uuid,uuid,integer,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.read_power_interval_terms(uuid,uuid,integer,text) TO openetrm_runtime,openetrm_risk_worker;
-- Retain one collected-price selection algorithm. Its extra eligibility bound
-- is applied before revision selection, independently of the certified read
-- knowledge context. Older historical valuations need no invented receipt at
-- their valuation instant. Existing four-argument readers preserve their policy.
DO $$DECLARE signature regprocedure:='power_data.read_prices_pre_portfolio_worker(uuid,timestamptz,timestamptz,timestamptz)'::regprocedure;
 body text;arguments text;header text;predicate text:='r.known_at<=cutoff';BEGIN
 SELECT pg_get_functiondef(signature),pg_get_function_arguments(signature) INTO body,arguments;
 header='CREATE OR REPLACE FUNCTION power_data.read_prices_pre_portfolio_worker('||arguments||')';
 IF position(header IN body)<>1 OR length(body)-length(replace(body,predicate,''))<>length(predicate) THEN RAISE EXCEPTION 'power_settlement_reader_migration';END IF;
 body=replace(body,header,'CREATE OR REPLACE FUNCTION power_data.read_prices_pre_portfolio_worker('||arguments||', eligible_at timestamptz)');
 body=replace(body,predicate,predicate||' AND (eligible_at IS NULL OR r.known_at<=eligible_at)');
 EXECUTE body;
END$$;
ALTER FUNCTION power_data.read_prices_pre_portfolio_worker(uuid,timestamptz,timestamptz,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION power_data.read_prices_pre_portfolio_worker(uuid,timestamptz,timestamptz,timestamptz,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION power_data.read_prices_pre_portfolio_worker(source uuid,first_at timestamptz,last_at timestamptz,cutoff timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN RETURN power_data.read_prices_pre_portfolio_worker(source,first_at,last_at,cutoff,NULL);END$$;
CREATE FUNCTION power_data.read_prices(source uuid,first_at timestamptz,last_at timestamptz,cutoff timestamptz,eligible_at timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 IF eligible_at IS NULL OR NOT isfinite(eligible_at) THEN RAISE EXCEPTION 'collection_window';END IF;
 PERFORM risk.require_worker_attempt();
 result=power_data.read_prices_pre_portfolio_worker(source,first_at,last_at,cutoff,eligible_at);
 PERFORM risk.require_worker_attempt();RETURN result;
END$$;
ALTER FUNCTION power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
