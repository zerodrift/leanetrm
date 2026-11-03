-- Exact LNG cargo masters and their current pre-delivery booking consumer.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE ref_data.lng_products (
 catalog_id uuid NOT NULL, revision_id uuid NOT NULL, code ref_data.code NOT NULL,
 commodity_code ref_data.code NOT NULL, name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 128),
 energy_quantity_code ref_data.code NOT NULL, energy_basis text NOT NULL CHECK(energy_basis='gross_calorific_v1'),
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,code,commodity_code),
 UNIQUE(catalog_id,revision_id,code,energy_quantity_code),
 FOREIGN KEY(catalog_id,revision_id,energy_quantity_code,commodity_code)
 REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code)
);
CREATE TABLE ref_data.lng_custody_points (
 catalog_id uuid NOT NULL, revision_id uuid NOT NULL, product_code ref_data.code NOT NULL,
 commodity_code ref_data.code NOT NULL, stage text NOT NULL CHECK(stage IN('load','discharge')),
 facility_code ref_data.code NOT NULL, custody_point_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,product_code,stage,facility_code,custody_point_code),
 CHECK(facility_code<>custody_point_code),
 FOREIGN KEY(catalog_id,revision_id,product_code,commodity_code)
 REFERENCES ref_data.lng_products(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,revision_id,commodity_code,facility_code)
 REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(catalog_id,revision_id,commodity_code,custody_point_code)
 REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code)
);
CREATE TABLE ref_data.lng_mass_energy_conversions (
 catalog_id uuid NOT NULL, revision_id uuid NOT NULL, code ref_data.code NOT NULL,
 product_code ref_data.code NOT NULL, commodity_code ref_data.code NOT NULL,
 mass_quantity_code ref_data.code NOT NULL, energy_quantity_code ref_data.code NOT NULL,
 energy_per_mass numeric NOT NULL CHECK(energy_per_mass>0 AND energy_per_mass<1e24 AND scale(energy_per_mass)<=18),
 implementation text NOT NULL CHECK(implementation='gross_discharge_energy_per_mass_v1'),
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,code,product_code,mass_quantity_code,energy_quantity_code),
 FOREIGN KEY(catalog_id,revision_id,product_code,commodity_code)
 REFERENCES ref_data.lng_products(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,revision_id,product_code,energy_quantity_code)
 REFERENCES ref_data.lng_products(catalog_id,revision_id,code,energy_quantity_code),
 FOREIGN KEY(catalog_id,revision_id,mass_quantity_code,commodity_code)
 REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code)
);
CREATE FUNCTION ref_data.require_lng_quantity_dimensions() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE energy ref_data.code; mass ref_data.code;
BEGIN
 energy=NEW.energy_quantity_code;
 IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q JOIN ref_data.dimensions d
 ON(d.catalog_id,d.revision_id,d.code)=(q.catalog_id,q.revision_id,q.dimension_code)
 WHERE(q.catalog_id,q.revision_id,q.code)=(NEW.catalog_id,NEW.revision_id,energy)
 AND q.dimension_code='energy' AND q.basis_code='gross_energy' AND
 (d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power)=(1,2,-2,0,0,0,0))
 THEN RAISE EXCEPTION 'lng_energy_quantity'; END IF;
 IF TG_TABLE_NAME='lng_mass_energy_conversions' THEN
  mass=NEW.mass_quantity_code;
  IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q JOIN ref_data.dimensions d
  ON(d.catalog_id,d.revision_id,d.code)=(q.catalog_id,q.revision_id,q.dimension_code)
  WHERE(q.catalog_id,q.revision_id,q.code)=(NEW.catalog_id,NEW.revision_id,mass)
  AND q.dimension_code='mass' AND q.basis_code='discharge_mass' AND
  (d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power)=(1,0,0,0,0,0,0))
  THEN RAISE EXCEPTION 'lng_mass_quantity'; END IF;
 END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_quantity BEFORE INSERT OR UPDATE ON ref_data.lng_products
 FOR EACH ROW EXECUTE FUNCTION ref_data.require_lng_quantity_dimensions();
CREATE TRIGGER require_quantity BEFORE INSERT OR UPDATE ON ref_data.lng_mass_energy_conversions
 FOR EACH ROW EXECUTE FUNCTION ref_data.require_lng_quantity_dimensions();

CREATE TABLE ref_data.incoterms_editions (
 catalog_id uuid NOT NULL, revision_id uuid NOT NULL, code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 128),
 edition_year integer NOT NULL CHECK(edition_year BETWEEN 1936 AND 2100),
 PRIMARY KEY(catalog_id,revision_id,code), UNIQUE(catalog_id,revision_id,edition_year),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id)
);
CREATE TABLE ref_data.incoterms_rules (
 catalog_id uuid NOT NULL, revision_id uuid NOT NULL, code ref_data.code NOT NULL,
 edition_code ref_data.code NOT NULL, rule_code text NOT NULL CHECK(rule_code ~ '^[A-Z]{3}$'),
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 128),
 PRIMARY KEY(catalog_id,revision_id,code), UNIQUE(catalog_id,revision_id,edition_code,rule_code),
 UNIQUE(catalog_id,revision_id,edition_code,code),
 FOREIGN KEY(catalog_id,revision_id,edition_code) REFERENCES ref_data.incoterms_editions(catalog_id,revision_id,code)
);
DO $$DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['lng_products','lng_custody_points','lng_mass_energy_conversions','incoterms_editions','incoterms_rules'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON ref_data.%I FOR EACH ROW EXECUTE FUNCTION ref_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE ref_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE ref_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON ref_data.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY current_authority ON ref_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_access(catalog_id,''ReadMarketData''))',t);
  EXECUTE format('CREATE POLICY version_scope ON ref_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(catalog_id,revision_id))',t);
  EXECUTE format('CREATE POLICY owner_access ON ref_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT ON ref_data.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON ref_data.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;

CREATE TABLE market_data.lng_valuation_assignments (
 catalog_id uuid NOT NULL, revision_id uuid NOT NULL, code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL, reference_revision_id uuid NOT NULL,
 product_code ref_data.code NOT NULL, stage text NOT NULL CHECK(stage='discharge'),
 discharge_facility_code ref_data.code NOT NULL, discharge_custody_code ref_data.code NOT NULL,
 energy_quantity_code ref_data.code NOT NULL, clock_code ref_data.code NOT NULL, curve_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code),
 UNIQUE(catalog_id,revision_id,product_code,discharge_facility_code,discharge_custody_code,energy_quantity_code,clock_code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,stage,discharge_facility_code,discharge_custody_code)
 REFERENCES ref_data.lng_custody_points(catalog_id,revision_id,product_code,stage,facility_code,custody_point_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,energy_quantity_code)
 REFERENCES ref_data.lng_products(catalog_id,revision_id,code,energy_quantity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.lng_valuation_assignments
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition();
ALTER TABLE market_data.lng_valuation_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.lng_valuation_assignments FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.lng_valuation_assignments FOR SELECT TO openetrm_runtime USING(true);
CREATE POLICY current_authority ON market_data.lng_valuation_assignments AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_access(catalog_id,'ReadMarketData') AND security.reference_access(reference_catalog_id,'ReadMarketData'));
CREATE POLICY version_scope ON market_data.lng_valuation_assignments AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_fact_visible(catalog_id,revision_id,NULL) AND security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY owner_access ON market_data.lng_valuation_assignments TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT ON market_data.lng_valuation_assignments TO openetrm_runtime,openetrm_security_owner;
-- Display revisions retain all exact LNG and saved Incoterms relationships.
DO $guard$ BEGIN IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_proc WHERE oid='security.reference_display_tables()'::regprocedure) IS DISTINCT FROM '5fb5fb83acfff0326ad990fade68c7ba56a00a23612372c77552812480d55c75' THEN RAISE EXCEPTION 'lng_reference_display_predecessor'; END IF; END$guard$;
CREATE OR REPLACE FUNCTION security.reference_display_tables()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
 SET search_path TO 'pg_catalog'
AS $function$
 SELECT ARRAY['dimensions','currencies','units','commodities','commodity_units','measurement_bases','quantity_specifications',
 'unit_conversions','clock_rules','calendars','calendar_days','location_types','locations','commodity_locations','pipeline_zones','pipeline_delivery_points','gas_delivery_conventions','gas_meter_assignments','crude_grades','crude_delivery_points','exchanges',
 'clearing_houses','exchange_clearing_houses','interest_rate_definitions','price_index_definitions','sources','delivery_profiles',
 'value_specifications','quote_specifications','settlement_targets','futures_products','futures_series',
 'futures_milestones','futures_product_terms','futures_lot_resolutions','futures_basis_settlements','futures_execution_policies','product_evidence',
 'futures_option_rules','futures_option_products','trading_close_times','futures_option_series','lng_products','lng_custody_points','lng_mass_energy_conversions','incoterms_editions','incoterms_rules'];
$function$
;
COMMIT;
