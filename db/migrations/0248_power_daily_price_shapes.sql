-- Exact additive hourly source for daily native power blocks; monthly remains flat.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
-- The invoker reader retains its migration owner in a fresh database. Security
-- definers must retain the dedicated owner; every body is pinned exactly.
DO $guards$DECLARE item record;BEGIN
 FOR item IN SELECT * FROM(VALUES
 ('market_data.guard_power_interval_publication()','b8e267d2fce884598df986c65c0ccbfe7b9f71de64e22cfa1cf490f6676d2d68'),
 ('market_data.power_saved_sources(uuid,uuid,integer)','861047e2f3e074d728185ffe2328db3d186bbcd2927c7e57ca552a0d165c2373'),
 ('market_data.read_power_interval_terms(uuid,uuid,integer,text)','c51688f6e99a04e6cbce5a97da8c5591be740a21f6cab9ab1ed8742d42052bc1')
 ) d(signature,digest) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(item.signature) AND (pg_get_userbyid(p.proowner)='openetrm_security_owner' OR
    (item.signature='market_data.read_power_interval_terms(uuid,uuid,integer,text)' AND NOT p.prosecdef AND pg_get_userbyid(p.proowner)=current_user))
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.digest)
  THEN RAISE EXCEPTION 'power_price_shape_upgrade: changed dependency %',item.signature;END IF;
 END LOOP;
END$guards$;
CREATE TABLE market_data.custom_curve_power_price_shapes(
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,series_id uuid NOT NULL,
 policy text NOT NULL CHECK(policy='daily_native_block_additive_v1'),
 PRIMARY KEY(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_node_power_intervals(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,series_id) REFERENCES market_data.interval_series(catalog_id,id)
);
ALTER TABLE market_data.custom_curve_power_price_shapes ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.custom_curve_power_price_shapes FORCE ROW LEVEL SECURITY;
CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.custom_curve_power_price_shapes FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve();
CREATE CONSTRAINT TRIGGER agent_draft_write AFTER INSERT OR UPDATE OR DELETE ON market_data.custom_curve_power_price_shapes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.check_custom_curve_agent_write();
CREATE POLICY scoped_read ON market_data.custom_curve_power_price_shapes FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY risk_worker_service ON market_data.custom_curve_power_price_shapes AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
DO $$DECLARE op text;BEGIN
 FOREACH op IN ARRAY ARRAY['INSERT','UPDATE','DELETE'] LOOP
  EXECUTE format('CREATE POLICY current_%s ON market_data.custom_curve_power_price_shapes AS RESTRICTIVE FOR %s TO openetrm_runtime %s',lower(op),op,
   CASE op WHEN 'INSERT' THEN 'WITH CHECK(security.current_write_context())' WHEN 'UPDATE' THEN 'USING(security.current_write_context()) WITH CHECK(security.current_write_context())' ELSE 'USING(security.current_write_context())' END);
  EXECUTE format('CREATE POLICY scoped_%s ON market_data.custom_curve_power_price_shapes FOR %s TO openetrm_runtime %s',lower(op),op,
   CASE WHEN op='INSERT' THEN 'WITH CHECK' ELSE 'USING' END||'((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))');
 END LOOP;
END$$;
GRANT SELECT ON market_data.custom_curve_power_price_shapes TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker;
GRANT INSERT,UPDATE,DELETE ON market_data.custom_curve_power_price_shapes TO openetrm_runtime;

DO $patch$DECLARE body text;needle text:=$needle$ARRAY['settlementPolicy','projectionPolicy']$needle$;BEGIN
 body=pg_get_functiondef('market_data.guard_power_interval_publication()'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_price_shape_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$ARRAY['settlementPolicy','projectionPolicy','shapePolicy']$replacement$);
END$patch$;

DO $patch$DECLARE body text;needle text:=$needle$ARRAY['settlementPolicy','projectionPolicy']$needle$;BEGIN
 body=pg_get_functiondef('market_data.power_saved_sources(uuid,uuid,integer)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_price_shape_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$ARRAY['settlementPolicy','projectionPolicy','shapePolicy']$replacement$);
END$patch$;

CREATE FUNCTION market_data.guard_power_price_shape_publication() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE binding record;node market_data.custom_curve_nodes;native market_data.curves;daily integer;
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 FOR binding IN SELECT p.*,s.series_id,s.policy FROM market_data.custom_curve_node_power_intervals p
  LEFT JOIN market_data.custom_curve_power_price_shapes s USING(catalog_id,curve_id,version,node_id)
  WHERE(p.catalog_id,p.curve_id,p.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) LOOP
  SELECT * INTO node FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id)=(binding.catalog_id,binding.curve_id,binding.version,binding.node_id);
  IF(binding.series_id IS NOT NULL) IS DISTINCT FROM(node.parameters ? 'shapePolicy') THEN RAISE EXCEPTION 'power_price_shape_binding';END IF;
  IF binding.series_id IS NULL THEN CONTINUE;END IF;
  IF node.parameters->>'shapePolicy' IS DISTINCT FROM binding.policy THEN RAISE EXCEPTION 'power_price_shape_binding';END IF;
  IF node.parameters->>'implementation'='saved_product_forwards_v1' THEN
   SELECT count(*) INTO daily FROM market_data.power_saved_sources(NEW.catalog_id,NEW.curve_id,NEW.version) WHERE membership IN('dailyPeak','dailyOffPeak');
   SELECT c.* INTO native FROM market_data.power_saved_sources(NEW.catalog_id,NEW.curve_id,NEW.version) s JOIN market_data.curves c
    ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code) LIMIT 1;
  ELSIF node.parameters->>'implementation'='daily_then_monthly_forward_v1' THEN
   SELECT count(*) INTO daily FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(NEW.catalog_id,NEW.curve_id,NEW.version,node.node_id) AND port IN('dailyPeak','dailyOffPeak');
   SELECT c.* INTO native FROM market_data.custom_curve_node_sources s JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    WHERE(s.catalog_id,s.curve_id,s.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) LIMIT 1;
  ELSE RAISE EXCEPTION 'power_price_shape_binding';END IF;
  IF daily<>2 OR native.code IS NULL OR jsonb_typeof(market_data.read_interval_series_header(binding.series_id)) IS DISTINCT FROM 'object' OR NOT EXISTS(
   SELECT 1 FROM market_data.interval_series s JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(binding.reference_catalog_id,binding.reference_revision_id,binding.profile_code)
   WHERE(s.catalog_id,s.id,s.reference_catalog_id,s.reference_revision_id,s.quote_code,s.clock_code,s.interval_minutes,s.kind)=
    (binding.catalog_id,binding.series_id,binding.reference_catalog_id,binding.reference_revision_id,native.quote_code,p.clock_code,60,'additive_price_shape'))
  THEN RAISE EXCEPTION 'power_price_shape_binding';END IF;
 END LOOP;
 RETURN NEW;
END$$;
ALTER FUNCTION market_data.guard_power_price_shape_publication() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_power_price_shape_publication() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TRIGGER power_price_shape_publication BEFORE UPDATE ON market_data.custom_curve_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_power_price_shape_publication();

DO $patch$DECLARE body text;needle text:=$needle$ RETURN result;$needle$;BEGIN
 body=pg_get_functiondef('market_data.read_power_interval_terms(uuid,uuid,integer,text)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_price_shape_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$ IF EXISTS(SELECT 1 FROM market_data.custom_curve_power_price_shapes shape WHERE(shape.catalog_id,shape.curve_id,shape.version,shape.node_id)=(catalog,curve,selected_version,node)) THEN
  result=result||(SELECT jsonb_build_object('shapeSeries',shape.series_id::text) FROM market_data.custom_curve_power_price_shapes shape WHERE(shape.catalog_id,shape.curve_id,shape.version,shape.node_id)=(catalog,curve,selected_version,node));
 END IF;
 RETURN result;$replacement$);
END$patch$;
COMMIT;
