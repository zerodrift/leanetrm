-- Explicit collected-price sources for the current-only interval settlement consumer.
-- Drain runtime admission and the collection worker before this schema upgrade.
BEGIN;
SELECT pg_advisory_xact_lock(730849110);
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

ALTER TABLE power_data.sources ADD UNIQUE(id,catalog_id,reference_catalog_id,reference_revision_id);
CREATE TABLE market_data.curve_physical_price_sources (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,curve_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(source_id,catalog_id,reference_catalog_id,reference_revision_id)
  REFERENCES power_data.sources(id,catalog_id,reference_catalog_id,reference_revision_id)
);
COMMENT ON TABLE market_data.curve_physical_price_sources IS
 'One explicit collected interval-price source per exact curve version. Independent of forward-mark recipes; not a fixing publication or payment lifecycle.';

CREATE FUNCTION market_data.validate_curve_physical_price_source() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(
  SELECT 1 FROM market_data.curves c
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=
   (c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=
   (q.catalog_id,q.revision_id,q.quantity_code)
  JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=
   (q.catalog_id,q.revision_id,q.currency_code)
  JOIN power_data.sources s ON(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
   (NEW.source_id,NEW.catalog_id,NEW.reference_catalog_id,NEW.reference_revision_id)
  JOIN ref_data.clock_rules clock ON(clock.catalog_id,clock.revision_id,clock.code)=
   (s.reference_catalog_id,s.reference_revision_id,s.clock_code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(NEW.catalog_id,NEW.revision_id,NEW.curve_code)
   AND q.kind='outright' AND q.multiplier=1 AND currency.display_code='USD'
   AND u.dimension_code='energy' AND u.unit_code='megawatt_hour' AND u.commodity_code=c.commodity_code
   AND(s.currency_code,s.quantity_code,s.location_code)=(q.currency_code,q.quantity_code,c.location_code)
   AND clock.kind='iana' AND clock.iana_zone=CASE s.provider WHEN 'PJM' THEN 'America/New_York' WHEN 'ERCOT' THEN 'America/Chicago' END
 ) THEN RAISE EXCEPTION 'physical_source_definition: exact power quote, location, units and source clock must agree' USING ERRCODE='23514';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.curve_physical_price_sources
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition();
CREATE TRIGGER validate_physical_source AFTER INSERT ON market_data.curve_physical_price_sources
 FOR EACH ROW EXECUTE FUNCTION market_data.validate_curve_physical_price_source();
REVOKE ALL ON FUNCTION market_data.validate_curve_physical_price_source() FROM PUBLIC;

ALTER TABLE market_data.curve_physical_price_sources ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.curve_physical_price_sources FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.curve_physical_price_sources FOR SELECT TO openetrm_runtime USING(true);
CREATE POLICY temporal_read ON market_data.curve_physical_price_sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_fact_visible(catalog_id,revision_id,NULL));
CREATE POLICY reference_read ON market_data.curve_physical_price_sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY source_read ON market_data.curve_physical_price_sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM power_data.sources s WHERE(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
  (curve_physical_price_sources.source_id,curve_physical_price_sources.catalog_id,
   curve_physical_price_sources.reference_catalog_id,curve_physical_price_sources.reference_revision_id)));
CREATE POLICY scoped_insert ON market_data.curve_physical_price_sources FOR INSERT TO openetrm_runtime WITH CHECK(true);
CREATE POLICY current_insert ON market_data.curve_physical_price_sources AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND security.market_access(catalog_id,'WriteMarketDefinitions')
  AND security.reference_access(reference_catalog_id,'ReadMarketData')
  AND EXISTS(SELECT 1 FROM power_data.sources s WHERE(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
   (curve_physical_price_sources.source_id,curve_physical_price_sources.catalog_id,
    curve_physical_price_sources.reference_catalog_id,curve_physical_price_sources.reference_revision_id)));
GRANT SELECT,INSERT ON market_data.curve_physical_price_sources TO openetrm_runtime;
GRANT SELECT ON market_data.curve_physical_price_sources TO openetrm_security_owner;

-- The one-day consumer must not scan other sources or the source's later days.
-- ends_at > starts_at makes this extra upper bound lossless. Retain the exact
-- authorized reader, ownership and grants; refuse an unexpected predicate.
DO $$
DECLARE definition text;old_predicate constant text='p.starts_at>=first_at AND p.ends_at<=last_at';
BEGIN
 definition=pg_get_functiondef('power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz)'::regprocedure);
 IF length(definition)-length(replace(definition,old_predicate,''))<>length(old_predicate) THEN
  RAISE EXCEPTION 'Unexpected collected-price reader range predicate';
 END IF;
 EXECUTE replace(definition,old_predicate,'p.starts_at>=first_at AND p.starts_at<last_at AND p.ends_at<=last_at');
END$$;
CREATE INDEX prices_source_interval ON power_data.prices(source_id,starts_at);
COMMIT;
