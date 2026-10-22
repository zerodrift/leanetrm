-- Exact, sealed asset, quantity-source and location-valuation definitions.
-- No booking, settlement, definition-authoring API or cross-revision valuation is introduced.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE market_data.generation_assets (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80 AND btrim(name)=name),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 location_code ref_data.code NOT NULL,capacity_quantity_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,clock_code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,capacity_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code)
);
CREATE TABLE market_data.generation_asset_versions (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,asset_code ref_data.code NOT NULL,
 effective_from ref_data.instant NOT NULL,effective_to ref_data.instant NOT NULL,
 capacity_basis ref_data.exact_number NOT NULL CHECK(capacity_basis>0),
 accredited_capacity ref_data.exact_number CHECK(accredited_capacity>=0),
 PRIMARY KEY(catalog_id,revision_id,asset_code,effective_from),
 FOREIGN KEY(catalog_id,revision_id,asset_code) REFERENCES market_data.generation_assets(catalog_id,revision_id,code),
 CHECK(effective_from<effective_to)
);
CREATE TABLE market_data.location_valuation_assignments (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,location_code ref_data.code NOT NULL,
 attribute text NOT NULL CHECK(attribute IN('energy','certificate','capacity')),
 effective_from ref_data.instant NOT NULL,effective_to ref_data.instant NOT NULL,
 curve_code ref_data.code,shaped_curve_code ref_data.code,adjustment ref_data.exact_number NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,shaped_curve_code) REFERENCES market_data.shaped_curves(catalog_id,revision_id,code),
 CHECK(effective_from<effective_to),CHECK((curve_code IS NULL)<>(shaped_curve_code IS NULL))
);
-- Both source series must retain the exact reference, clock and grid declared by their consumer.
ALTER TABLE market_data.interval_series ADD UNIQUE(catalog_id,id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes);
CREATE TABLE market_data.volume_sources (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,asset_code ref_data.code NOT NULL,
 clock_code ref_data.code NOT NULL,interval_minutes integer NOT NULL CHECK(interval_minutes IN(15,60)),
 forecast_series_id uuid NOT NULL,forecast_stage text NOT NULL CHECK(forecast_stage IN('generated','delivered')),
 metered_series_id uuid NOT NULL,metered_stage text NOT NULL CHECK(metered_stage IN('generated','delivered')),
 loss ref_data.exact_number NOT NULL CHECK(loss>=0 AND loss<1),
 range_rule text NOT NULL CHECK(range_rule='generation_nonnegative_v1'),
 availability_mode text NOT NULL CHECK(availability_mode IN('apply','reflected')),
 availability ref_data.exact_number CHECK(availability>=0 AND availability<=1),
 curtailment_mode text NOT NULL CHECK(curtailment_mode IN('apply','reflected')),
 curtailment ref_data.exact_number CHECK(curtailment>=0 AND curtailment<=1),
 degradation_mode text NOT NULL CHECK(degradation_mode IN('apply','reflected')),
 degradation ref_data.exact_number CHECK(degradation>=0 AND degradation<1),
 degradation_basis text CHECK(degradation_basis='anniversary_step_v1'),degradation_base_date date,
 meter_due_rule text NOT NULL CHECK(meter_due_rule='meter_due_v1'),
 meter_due_offset_minutes integer NOT NULL CHECK(meter_due_offset_minutes BETWEEN 0 AND 129600),
 PRIMARY KEY(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,revision_id,asset_code,reference_catalog_id,reference_revision_id,clock_code)
  REFERENCES market_data.generation_assets(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,clock_code),
 FOREIGN KEY(catalog_id,forecast_series_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes)
  REFERENCES market_data.interval_series(catalog_id,id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes),
 FOREIGN KEY(catalog_id,metered_series_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes)
  REFERENCES market_data.interval_series(catalog_id,id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes),
 CHECK((availability_mode='apply')=(availability IS NOT NULL)),
 CHECK((curtailment_mode='apply')=(curtailment IS NOT NULL)),
 CHECK((degradation_mode='apply')=(degradation IS NOT NULL)
  AND(degradation IS NULL)=(degradation_basis IS NULL) AND(degradation IS NULL)=(degradation_base_date IS NULL)),
 CHECK(degradation_base_date IS NULL OR(isfinite(degradation_base_date)
  AND degradation_base_date>=DATE '0001-01-01' AND degradation_base_date<DATE '9999-12-31'
  AND NOT(extract(month FROM degradation_base_date)=2 AND extract(day FROM degradation_base_date)=29)))
);
COMMENT ON TABLE market_data.generation_assets IS 'Exact location, MW capacity specification and operating clock for a generation asset.';
COMMENT ON TABLE market_data.generation_asset_versions IS 'Nonoverlapping operating-day versions of whole-asset capacity basis and optional accreditation; lineage includes effective_from.';
COMMENT ON TABLE market_data.location_valuation_assignments IS 'One explicit curve and additive adjustment in that curve''s exact price unit for a location and valued attribute over a delivery-time interval.';
COMMENT ON TABLE market_data.volume_sources IS 'Whole-asset capacity-factor forecast and MWh meter with explicit stages, forecast-only adjustment modes, loss conversion and elapsed-minute meter deadline.';

-- This is a rejection check against the declared clock, never a replacement for a retained
-- UTC bound. Like the existing execution-date proof, IANA candidates use supported minute
-- offsets within fourteen hours. Ambiguous, nonexistent or unsupported midnights fail closed.
CREATE FUNCTION market_data.ppa_day_boundary(at timestamptz,reference_catalog uuid,reference_revision uuid,clock_code ref_data.code)
 RETURNS boolean LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE clock ref_data.clock_rules;local_at timestamp;candidate_count bigint;
BEGIN
 SELECT * INTO clock FROM ref_data.clock_rules c WHERE(c.catalog_id,c.revision_id,c.code)=(reference_catalog,reference_revision,clock_code);
 IF NOT FOUND OR NOT isfinite(at) THEN RETURN false;END IF;
 IF clock.kind='fixed_offset' THEN local_at=(at AT TIME ZONE 'UTC')+make_interval(secs=>clock.fixed_offset_seconds);
 ELSE local_at=at AT TIME ZONE clock.iana_zone;END IF;
 IF local_at<>date_trunc('day',local_at) OR local_at<timestamp '2000-01-01' OR local_at>timestamp '2100-01-01' THEN RETURN false;END IF;
 IF clock.kind='iana' THEN
  SELECT count(*) INTO candidate_count FROM generate_series(-840,840) candidate(minutes)
   WHERE(((local_at AT TIME ZONE 'UTC')-make_interval(mins=>candidate.minutes)) AT TIME ZONE clock.iana_zone)=local_at;
  RETURN candidate_count=1;
 END IF;
 RETURN true;
END$$;
REVOKE ALL ON FUNCTION market_data.ppa_day_boundary(timestamptz,uuid,uuid,ref_data.code) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.ppa_day_boundary(timestamptz,uuid,uuid,ref_data.code) TO openetrm_runtime,openetrm_security_owner;

CREATE FUNCTION market_data.validate_ppa_reference() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.generation_assets a WHERE(a.catalog_id,a.revision_id)=(NEW.catalog_id,NEW.id) AND(
   NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q WHERE(q.catalog_id,q.revision_id,q.code)=
    (a.reference_catalog_id,a.reference_revision_id,a.capacity_quantity_code) AND q.dimension_code='power' AND q.unit_code='megawatt')
   OR NOT EXISTS(SELECT 1 FROM market_data.generation_asset_versions v WHERE(v.catalog_id,v.revision_id,v.asset_code)=(a.catalog_id,a.revision_id,a.code))))
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Each generation asset requires an exact MW power specification and effective capacity versions.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.generation_asset_versions a JOIN market_data.generation_asset_versions b
   ON(a.catalog_id,a.revision_id,a.asset_code)=(b.catalog_id,b.revision_id,b.asset_code)
   WHERE(a.catalog_id,a.revision_id)=(NEW.catalog_id,NEW.id) AND a.effective_from<b.effective_from AND b.effective_from<a.effective_to)
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Asset versions cannot overlap.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.generation_asset_versions v
   WHERE(v.catalog_id,v.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
    SELECT 1 FROM market_data.generation_assets a WHERE(a.catalog_id,a.revision_id,a.code)=(v.catalog_id,v.revision_id,v.asset_code)
     AND market_data.ppa_day_boundary(v.effective_from,a.reference_catalog_id,a.reference_revision_id,a.clock_code)
     AND market_data.ppa_day_boundary(v.effective_to,a.reference_catalog_id,a.reference_revision_id,a.clock_code)))
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Asset versions require unambiguous operating-day boundaries on the exact asset clock within the supported 2000-2099 interval-series window.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.location_valuation_assignments a JOIN market_data.location_valuation_assignments b
   ON(a.catalog_id,a.revision_id,a.location_code,a.attribute)=(b.catalog_id,b.revision_id,b.location_code,b.attribute)
   WHERE(a.catalog_id,a.revision_id)=(NEW.catalog_id,NEW.id) AND a.code<b.code AND a.effective_from<b.effective_to AND b.effective_from<a.effective_to)
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Location valuation assignments cannot overlap for the same location and attribute.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.location_valuation_assignments a WHERE(a.catalog_id,a.revision_id)=(NEW.catalog_id,NEW.id) AND a.attribute='energy' AND NOT(
   EXISTS(SELECT 1 FROM market_data.curve_actual_rules r WHERE(r.catalog_id,r.revision_id,r.curve_code)=(a.catalog_id,a.revision_id,a.curve_code)) OR
   EXISTS(SELECT 1 FROM market_data.shaped_curve_actual_rules r WHERE(r.catalog_id,r.revision_id,r.curve_code)=(a.catalog_id,a.revision_id,a.shaped_curve_code))))
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='An energy assignment requires a marked or shaped interval curve with its exact collected actual rule.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.location_valuation_assignments a WHERE(a.catalog_id,a.revision_id)=(NEW.catalog_id,NEW.id) AND a.attribute IN('certificate','capacity') AND NOT EXISTS(
   SELECT 1 FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code) AND c.rule='direct'
    AND EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code))
    AND NOT EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.contract_intervals i ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
     WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code))))
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Certificate and capacity assignments require direct date-only contract curves.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.location_valuation_assignments a
   JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
   JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
   JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(a.catalog_id,a.revision_id)=(NEW.catalog_id,NEW.id) AND a.attribute='capacity' AND NOT EXISTS(
    SELECT 1 FROM ref_data.settlement_targets t JOIN market_data.index_fixing_observations f
     ON(f.catalog_id,f.revision_id,f.index_code)=(c.catalog_id,c.revision_id,t.index_a_code)
    WHERE(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
     AND t.kind='index' AND(f.reference_catalog_id,f.reference_revision_id,f.quote_code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
     AND(f.observation_from,f.observation_to)=(k.delivery_from,k.delivery_to) AND k.expiry_at>=f.fixing_at
     AND k.delivery_from=date_trunc('month',k.delivery_from)::date AND k.delivery_to=(k.delivery_from+interval '1 month')::date))
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Each capacity month requires its exact qualified fixing schedule and a contract live until the fixing instant.';END IF;
 IF EXISTS(SELECT 1 FROM market_data.volume_sources s WHERE(s.catalog_id,s.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
   SELECT 1 FROM market_data.generation_assets a
   JOIN ref_data.quantity_specifications capacity ON(capacity.catalog_id,capacity.revision_id,capacity.code)=
    (a.reference_catalog_id,a.reference_revision_id,a.capacity_quantity_code)
   JOIN market_data.interval_series forecast ON(forecast.catalog_id,forecast.id)=(s.catalog_id,s.forecast_series_id)
   JOIN ref_data.value_specifications factor ON(factor.catalog_id,factor.revision_id,factor.code)=
    (forecast.reference_catalog_id,forecast.reference_revision_id,forecast.value_code)
   JOIN market_data.interval_series meter ON(meter.catalog_id,meter.id)=(s.catalog_id,s.metered_series_id)
   JOIN ref_data.quantity_specifications energy ON(energy.catalog_id,energy.revision_id,energy.code)=
    (meter.reference_catalog_id,meter.reference_revision_id,meter.quantity_code)
   WHERE(a.catalog_id,a.revision_id,a.code)=(s.catalog_id,s.revision_id,s.asset_code)
    AND forecast.kind='capacity_factor_forecast' AND factor.kind='dimensionless' AND meter.kind='metered_energy'
    AND energy.dimension_code='energy' AND energy.unit_code='megawatt_hour' AND energy.commodity_code=capacity.commodity_code))
 THEN RAISE EXCEPTION 'ppa_reference_definition' USING ERRCODE='23514',DETAIL='Volume sources require a dimensionless capacity-factor forecast and whole-asset MWh meter in the asset commodity on their exact declared clock and grid.';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER validate_ppa_reference BEFORE UPDATE OF state ON market_data.revisions
 FOR EACH ROW WHEN(OLD.state='draft' AND NEW.state='sealed') EXECUTE FUNCTION market_data.validate_ppa_reference();
REVOKE ALL ON FUNCTION market_data.validate_ppa_reference() FROM PUBLIC;

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['generation_assets','generation_asset_versions','location_valuation_assignments','volume_sources'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY temporal_read ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.market_fact_visible(catalog_id,revision_id,NULL))',t);
  EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(true)',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context() AND security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_security_owner',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['generation_assets','location_valuation_assignments','volume_sources'] LOOP
  EXECUTE format('CREATE POLICY reference_read ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY reference_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.reference_access(reference_catalog_id,''ReadMarketData''))',t);
 END LOOP;
END$$;
CREATE POLICY asset_read ON market_data.generation_asset_versions AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM market_data.generation_assets a WHERE(a.catalog_id,a.revision_id,a.code)=
  (generation_asset_versions.catalog_id,generation_asset_versions.revision_id,generation_asset_versions.asset_code)));
CREATE POLICY asset_insert ON market_data.generation_asset_versions AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(EXISTS(SELECT 1 FROM market_data.generation_assets a WHERE(a.catalog_id,a.revision_id,a.code)=
  (generation_asset_versions.catalog_id,generation_asset_versions.revision_id,generation_asset_versions.asset_code)));
CREATE POLICY source_read ON market_data.volume_sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM market_data.generation_assets a JOIN market_data.interval_series f ON(f.catalog_id,f.id)=(a.catalog_id,volume_sources.forecast_series_id)
  JOIN market_data.interval_series m ON(m.catalog_id,m.id)=(a.catalog_id,volume_sources.metered_series_id)
  WHERE(a.catalog_id,a.revision_id,a.code)=(volume_sources.catalog_id,volume_sources.revision_id,volume_sources.asset_code)));
CREATE POLICY source_insert ON market_data.volume_sources AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(EXISTS(SELECT 1 FROM market_data.generation_assets a JOIN market_data.interval_series f ON(f.catalog_id,f.id)=(a.catalog_id,volume_sources.forecast_series_id)
  JOIN market_data.interval_series m ON(m.catalog_id,m.id)=(a.catalog_id,volume_sources.metered_series_id)
  WHERE(a.catalog_id,a.revision_id,a.code)=(volume_sources.catalog_id,volume_sources.revision_id,volume_sources.asset_code)));
-- FOR ALL reuses this exact visibility predicate as the INSERT check as well.
-- Mutations remain unavailable and the immutable definition guard refuses them.
CREATE POLICY curve_access ON market_data.location_valuation_assignments AS RESTRICTIVE FOR ALL TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=
  (location_valuation_assignments.catalog_id,location_valuation_assignments.revision_id,location_valuation_assignments.curve_code)
  AND(location_valuation_assignments.attribute<>'energy' OR EXISTS(SELECT 1 FROM market_data.curve_actual_rules r
   JOIN market_data.curve_physical_price_sources p USING(catalog_id,revision_id,curve_code)
   WHERE(r.catalog_id,r.revision_id,r.curve_code)=(c.catalog_id,c.revision_id,c.code)))) OR
 EXISTS(SELECT 1 FROM market_data.shaped_curves c JOIN market_data.shaped_curve_actual_rules r ON(r.catalog_id,r.revision_id,r.curve_code)=(c.catalog_id,c.revision_id,c.code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(location_valuation_assignments.catalog_id,location_valuation_assignments.revision_id,location_valuation_assignments.shaped_curve_code)));
COMMIT;
