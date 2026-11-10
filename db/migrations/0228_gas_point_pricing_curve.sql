-- Retain a point's explicitly selected published forecast in its immutable setup.
-- Its native index assignment remains the contractual settlement anchor.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid='security.materialize_gas_setup(uuid,uuid)'::regprocedure
  AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='2d92b797dc7dba81db648008a99ee1e77f371be5ede8f2bcf674299828230796'
  AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
  AND p.proconfig=ARRAY['search_path=pg_catalog'] AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[])
 THEN RAISE EXCEPTION 'gas_point_pricing_upgrade: unexpected helper security.materialize_gas_setup(uuid,uuid)';END IF;
END$$;
ALTER TABLE market_data.gas_setup_points
 ADD COLUMN forecast_source text NOT NULL DEFAULT 'assigned_native_v1',
 ADD COLUMN forecast_curve_id uuid,ADD COLUMN forecast_version integer,ADD COLUMN forecast_output_node text,
 ADD COLUMN forecast_selection text,ADD COLUMN forecast_implementation text,
 ADD CONSTRAINT gas_point_forecast_node FOREIGN KEY(catalog_id,forecast_curve_id,forecast_version,forecast_output_node)
  REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 ADD CONSTRAINT gas_point_forecast_contract CHECK(
  (forecast_source='assigned_native_v1' AND forecast_curve_id IS NULL AND forecast_version IS NULL AND forecast_output_node IS NULL
   AND forecast_selection IS NULL AND forecast_implementation IS NULL) OR
  (forecast_source='published_custom_v1' AND forecast_curve_id IS NOT NULL AND forecast_version IS NOT NULL AND forecast_version>0 AND forecast_output_node IS NOT NULL
   AND forecast_selection IS NOT NULL AND forecast_implementation IS NOT NULL AND forecast_selection='containing_delivery_interval_v1' AND forecast_implementation='published_custom_curve_projection_v1'));
-- Existing setups already selected their native index. The persisted label above
-- records that existing contract; no old setup JSON or booked Side is rewritten.
-- Dated cash legs belong to the same exact immutable point setup as its forecast.
CREATE TABLE market_data.gas_point_index_periods(
 catalog_id uuid NOT NULL,setup_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 valid_from date NOT NULL,valid_to date NOT NULL,curve_code ref_data.code NOT NULL,index_code ref_data.code NOT NULL,
 observation_convention text NOT NULL CHECK(observation_convention IN('daily_published_index_v1','monthly_published_index_v1')),
 CHECK(isfinite(valid_from) AND isfinite(valid_to) AND valid_from<valid_to),
 PRIMARY KEY(catalog_id,setup_id,point_code,product_code,valid_from),
 FOREIGN KEY(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code) REFERENCES market_data.gas_setup_points(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(catalog_id,setup_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.gas_setup_versions(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code));
ALTER TABLE market_data.gas_point_index_periods ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.gas_point_index_periods FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON market_data.gas_point_index_periods TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON market_data.gas_point_index_periods FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,'ReadMarketData') AND security.gas_setup_visible(catalog_id,setup_id));
CREATE POLICY worker_read ON market_data.gas_point_index_periods FOR SELECT TO openetrm_risk_worker USING(security.market_access(catalog_id,'ReadMarketData') AND security.gas_setup_visible(catalog_id,setup_id));
CREATE POLICY worker_service ON market_data.gas_point_index_periods AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.gas_point_index_periods FOR EACH ROW EXECUTE FUNCTION market_data.guard_gas_setup();
GRANT SELECT,INSERT ON market_data.gas_point_index_periods TO openetrm_security_owner;
GRANT SELECT ON market_data.gas_point_index_periods TO openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.materialize_gas_point_indices(catalog uuid,setup uuid,path jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v market_data.gas_setup_versions;x jsonb;previous_to date;first_day date;last_day date;BEGIN
 IF NOT(path ? 'settlementPeriods') THEN RETURN;END IF;
 IF path#>>'{pricing,source}' IS DISTINCT FROM 'published_custom_v1' THEN RAISE EXCEPTION 'gas_setup_index_forecast';END IF;
 IF jsonb_typeof(path->'settlementPeriods') IS DISTINCT FROM 'array' OR jsonb_array_length(path->'settlementPeriods') NOT BETWEEN 1 AND 1200 THEN RAISE EXCEPTION 'gas_setup_index_periods';END IF;
 SELECT * INTO STRICT v FROM market_data.gas_setup_versions WHERE(catalog_id,id)=(catalog,setup);
 FOR x IN SELECT value FROM jsonb_array_elements(path->'settlementPeriods') LOOP
  IF jsonb_typeof(x) IS DISTINCT FROM 'object' OR NOT(x ?& ARRAY['from','to','curve','index','observationConvention']) OR(SELECT count(*) FROM jsonb_object_keys(x))<>5
   OR x->>'observationConvention' IS NULL OR x->>'observationConvention' NOT IN('daily_published_index_v1','monthly_published_index_v1') THEN RAISE EXCEPTION 'gas_setup_index_periods';END IF;
  first_day=(x->>'from')::date;last_day=(x->>'to')::date;
  IF first_day IS NULL OR last_day IS NULL OR first_day>=last_day OR(previous_to IS NOT NULL AND first_day<>previous_to) OR
   (x->>'observationConvention'='monthly_published_index_v1' AND(first_day<>date_trunc('month',first_day)::date OR last_day<>date_trunc('month',last_day)::date)) THEN RAISE EXCEPTION 'gas_setup_index_periods';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.curves c JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
   WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id,c.commodity_code,t.kind,t.index_a_code,q.kind)=(catalog,v.definition_revision_id,x->>'curve',v.reference_catalog_id,v.reference_revision_id,path->>'product','index',x->>'index','outright') AND t.index_b_code IS NULL)
  THEN RAISE EXCEPTION 'gas_setup_index_source';END IF;
  INSERT INTO market_data.gas_point_index_periods VALUES(catalog,setup,v.definition_revision_id,v.reference_catalog_id,v.reference_revision_id,path->>'pipeline',path->>'zone',path->>'point',path->>'product',first_day,last_day,x->>'curve',x->>'index',x->>'observationConvention');
  previous_to=last_day;
 END LOOP;
END$$;
ALTER FUNCTION security.materialize_gas_point_indices(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.materialize_gas_point_indices(uuid,uuid,jsonb) FROM PUBLIC;
CREATE FUNCTION security.validate_gas_point_pricing(catalog uuid,definition uuid,pricing jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF pricing IS NULL THEN RETURN;END IF;
 IF jsonb_typeof(pricing) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'gas_setup_pricing';END IF;
 IF pricing->>'source'='assigned_native_v1' AND (SELECT count(*) FROM jsonb_object_keys(pricing))=1 THEN RETURN;END IF;
 IF pricing->>'source' IS DISTINCT FROM 'published_custom_v1' OR NOT pricing ?& ARRAY['source','curveId','version','outputNode','selection','implementation']
  OR(SELECT count(*) FROM jsonb_object_keys(pricing))<>6 OR pricing->>'selection' IS DISTINCT FROM 'containing_delivery_interval_v1'
  OR pricing->>'implementation' IS DISTINCT FROM 'published_custom_curve_projection_v1'
 THEN RAISE EXCEPTION 'gas_setup_pricing';END IF;
 IF EXISTS(SELECT 1 FROM security.custom_curve_native_sources(catalog,(pricing->>'curveId')::uuid,(pricing->>'version')::integer) s WHERE(s.source_catalog_id,s.source_revision_id) IS DISTINCT FROM(catalog,definition)) THEN RAISE EXCEPTION 'gas_setup_pricing_source';END IF;
 PERFORM security.validate_custom_projection(catalog,(pricing-'source')||jsonb_build_object('catalogId',catalog,'position',NULL));
END$$;
ALTER FUNCTION security.validate_gas_point_pricing(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.validate_gas_point_pricing(uuid,uuid,jsonb) FROM PUBLIC;
DO $patch$
DECLARE body text=pg_get_functiondef('security.materialize_gas_setup(uuid,uuid)'::regprocedure);old text;replacement text;
BEGIN
 old=$old$k NOT IN('pipeline','zone','point','product','clock','dayStartMinute','groupCode','adjustment')$old$;
 IF length(body)-length(replace(body,old,''))<>length(old) THEN RAISE EXCEPTION 'gas_point_pricing_upgrade: unexpected path contract';END IF;
 body=replace(body,old,$new$k NOT IN('pipeline','zone','point','product','clock','dayStartMinute','groupCode','adjustment','pricing','settlementPeriods')$new$);
 old=$old$INSERT INTO market_data.gas_setup_points VALUES(catalog,setup,v.definition_revision_id,v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'zone',x->>'point',x->>'product',x->>'clock',(x->>'dayStartMinute')::integer);$old$;
 replacement=$new$PERFORM security.validate_gas_point_pricing(catalog,v.definition_revision_id,x->'pricing');
  INSERT INTO market_data.gas_setup_points(catalog_id,setup_id,definition_revision_id,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,clock_code,day_start_minute,
   forecast_source,forecast_curve_id,forecast_version,forecast_output_node,forecast_selection,forecast_implementation)
  VALUES(catalog,setup,v.definition_revision_id,v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'zone',x->>'point',x->>'product',x->>'clock',(x->>'dayStartMinute')::integer,
   coalesce(x->'pricing'->>'source','assigned_native_v1'),(x->'pricing'->>'curveId')::uuid,(x->'pricing'->>'version')::integer,x->'pricing'->>'outputNode',x->'pricing'->>'selection',x->'pricing'->>'implementation');
  PERFORM security.materialize_gas_point_indices(catalog,setup,x);$new$;
 IF length(body)-length(replace(body,old,''))<>length(old) THEN RAISE EXCEPTION 'gas_point_pricing_upgrade: unexpected point writer';END IF;
 EXECUTE replace(body,old,replacement);
END $patch$;
-- The SQL admission boundary resolves the same exact setup-derived forecast.
-- The user's original command JSON remains unchanged for first-outcome replay.
CREATE FUNCTION security.gas_point_forecast(catalog uuid,assignment market_data.gas_valuation_assignments,requested jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE point market_data.gas_setup_points;configured jsonb;
BEGIN
 requested=NULLIF(requested,'null'::jsonb);
 IF assignment.setup_id IS NULL THEN RETURN requested;END IF;
 IF NOT security.gas_setup_visible(catalog,assignment.setup_id) THEN RAISE EXCEPTION 'gas_location_curve_unavailable';END IF;
 SELECT * INTO STRICT point FROM market_data.gas_setup_points p WHERE(p.catalog_id,p.setup_id,p.pipeline_code,p.zone_code,p.point_code,p.product_code)=
  (catalog,assignment.setup_id,assignment.pipeline_code,assignment.zone_code,assignment.point_code,assignment.product_code);
 IF point.forecast_source='assigned_native_v1' THEN RETURN requested;END IF;
 IF point.forecast_source IS DISTINCT FROM 'published_custom_v1' THEN RAISE EXCEPTION 'gas_location_curve_unavailable';END IF;
 configured=jsonb_build_object('catalogId',catalog,'curveId',point.forecast_curve_id,'version',point.forecast_version,'outputNode',point.forecast_output_node,'position',NULL,'selection',point.forecast_selection,'implementation',point.forecast_implementation);
 IF requested IS NOT NULL AND requested IS DISTINCT FROM configured THEN RAISE EXCEPTION 'gas_location_curve_conflict';END IF;
 RETURN security.validate_custom_projection(catalog,configured);
END$$;
ALTER FUNCTION security.gas_point_forecast(uuid,market_data.gas_valuation_assignments,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_point_forecast(uuid,market_data.gas_valuation_assignments,jsonb) FROM PUBLIC;
DO $patch$
DECLARE signature regprocedure='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb)'::regprocedure;body text;old text=$old$ IF slot=0 THEN
  IF forecast IS NOT NULL THEN$old$;
BEGIN
 SELECT pg_get_functiondef(p.oid) INTO body FROM pg_proc p WHERE p.oid=signature
  AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='ad2a9a210f70a833362666801d0945b873fb15085d59af239e22a910de4d039f'
  AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
  AND p.proconfig=ARRAY['search_path=pg_catalog','jit=off'] AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
 IF body IS NULL OR length(body)-length(replace(body,old,''))<>length(old) THEN RAISE EXCEPTION 'gas_point_pricing_upgrade: unexpected helper trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb)';END IF;
 EXECUTE replace(body,old,$new$ IF slot=0 THEN
  forecast=security.gas_point_forecast(catalog,assignment,forecast);
  IF forecast IS NOT NULL THEN$new$);
END $patch$;
COMMIT;
