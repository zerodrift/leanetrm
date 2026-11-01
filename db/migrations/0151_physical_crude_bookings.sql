-- Fixed total standard-volume crude obligations with independent retained cash sides.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
CREATE TABLE ref_data.crude_grades (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,commodity_code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 128),quantity_code ref_data.code NOT NULL,
 standard_temperature numeric NOT NULL CHECK(standard_temperature=60),temperature_scale text NOT NULL CHECK(temperature_scale='fahrenheit'),
 PRIMARY KEY(catalog_id,revision_id,code),UNIQUE(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,revision_id,quantity_code,commodity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code)
);
CREATE FUNCTION ref_data.require_crude_grade() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q JOIN ref_data.dimensions d ON(d.catalog_id,d.revision_id,d.code)=(q.catalog_id,q.revision_id,q.dimension_code)
 WHERE(q.catalog_id,q.revision_id,q.code,q.commodity_code,q.dimension_code)=(NEW.catalog_id,NEW.revision_id,NEW.quantity_code,NEW.commodity_code,'volume')
 AND(d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power)=(0,3,0,0,0,0,0)) THEN RAISE EXCEPTION 'crude_standard_volume';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_grade BEFORE INSERT OR UPDATE ON ref_data.crude_grades FOR EACH ROW EXECUTE FUNCTION ref_data.require_crude_grade();
CREATE TABLE ref_data.crude_delivery_points (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,commodity_code ref_data.code NOT NULL,grade_code ref_data.code NOT NULL,
 facility_code ref_data.code NOT NULL,custody_point_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,commodity_code,grade_code,facility_code,custody_point_code),CHECK(facility_code<>custody_point_code),
 FOREIGN KEY(catalog_id,revision_id,grade_code,commodity_code) REFERENCES ref_data.crude_grades(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,revision_id,commodity_code,facility_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(catalog_id,revision_id,commodity_code,custody_point_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_grades','crude_delivery_points'] LOOP
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
-- Display-only reference revisions preserve the complete typed delivery relationships.
CREATE OR REPLACE FUNCTION security.reference_display_tables() RETURNS text[]
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT ARRAY['dimensions','currencies','units','commodities','commodity_units','measurement_bases','quantity_specifications',
 'unit_conversions','clock_rules','calendars','calendar_days','location_types','locations','commodity_locations','pipeline_zones','pipeline_delivery_points','gas_delivery_conventions','gas_meter_assignments','crude_grades','crude_delivery_points','exchanges',
 'clearing_houses','exchange_clearing_houses','interest_rate_definitions','price_index_definitions','sources','delivery_profiles',
 'value_specifications','quote_specifications','settlement_targets','futures_products','futures_series',
 'futures_milestones','futures_product_terms','futures_lot_resolutions','futures_basis_settlements','futures_execution_policies','product_evidence'];
$$;
CREATE TABLE market_data.crude_valuation_assignments (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 commodity_code ref_data.code NOT NULL,grade_code ref_data.code NOT NULL,facility_code ref_data.code NOT NULL,custody_point_code ref_data.code NOT NULL,
 curve_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),UNIQUE(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code),
 UNIQUE(catalog_id,revision_id,commodity_code,grade_code,facility_code,custody_point_code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,commodity_code,grade_code,facility_code,custody_point_code)
 REFERENCES ref_data.crude_delivery_points(catalog_id,revision_id,commodity_code,grade_code,facility_code,custody_point_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code)
);
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.crude_valuation_assignments FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition();
ALTER TABLE market_data.crude_valuation_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.crude_valuation_assignments FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.crude_valuation_assignments FOR SELECT TO openetrm_runtime USING(true);
CREATE POLICY current_authority ON market_data.crude_valuation_assignments AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_access(catalog_id,'ReadMarketData') AND security.reference_access(reference_catalog_id,'ReadMarketData'));
CREATE POLICY version_scope ON market_data.crude_valuation_assignments AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_fact_visible(catalog_id,revision_id,NULL) AND security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY owner_access ON market_data.crude_valuation_assignments TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT ON market_data.crude_valuation_assignments TO openetrm_runtime,openetrm_security_owner;

CREATE TABLE trading.crude_instruments (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,assignment_code ref_data.code NOT NULL,
 delivery_from ref_data.instant NOT NULL,delivery_to ref_data.instant NOT NULL,quantity_basis text NOT NULL CHECK(quantity_basis='standard_volume_total'),
 tolerance numeric NOT NULL CHECK(tolerance=0),fixed_price numeric NOT NULL CHECK(abs(fixed_price)<1e12 AND scale(fixed_price)<=18),
 handling_fee numeric CHECK(abs(handling_fee)<1e12 AND scale(handling_fee)<=18),payment_at ref_data.instant NOT NULL,
 consideration_direction integer NOT NULL CHECK(consideration_direction IN(-1,1)),handling_fee_direction integer CHECK(handling_fee_direction IN(-1,1)),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),CHECK(delivery_from<delivery_to AND payment_at>=delivery_to),CHECK((handling_fee IS NULL)=(handling_fee_direction IS NULL)),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,assignment_code) REFERENCES market_data.crude_valuation_assignments(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.crude_memberships (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL CHECK(slot BETWEEN 0 AND 2),side_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,slot),UNIQUE(catalog_id,instrument_id,slot,side_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.crude_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.sides(catalog_id,id)
);
CREATE TABLE trading.crude_trade_revisions (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision=1),instrument_id uuid NOT NULL,book_id uuid NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),
 direction integer NOT NULL CHECK(direction IN(-1,1)),legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,
 counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),UNIQUE(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.crude_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_instruments','crude_memberships','crude_trade_revisions'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades''))',t);
  IF t='crude_memberships' THEN
   EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.crude_instruments i WHERE(i.catalog_id,i.instrument_id)=(crude_memberships.catalog_id,crude_memberships.instrument_id)))',t);
  ELSE
   EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  END IF;
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;

CREATE FUNCTION trading.crude_reference_identity(catalog uuid,revision uuid,code text) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$SELECT jsonb_build_array('reference',jsonb_build_array('reference_revision',catalog,revision),code)$$;
CREATE FUNCTION trading.require_crude_program_shape(catalog uuid,schedule uuid,slot integer,start_at timestamptz,end_at timestamptz,payment timestamptz,
 literal numeric,assignment market_data.crude_valuation_assignments) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE row trading.side_schedule_rows;curve market_data.curves;quote ref_data.quote_specifications;contract market_data.contracts;
 target ref_data.settlement_targets;fixing market_data.index_fixing_observations;grade ref_data.crude_grades;quantity ref_data.quantity_specifications;
 selected jsonb;selection jsonb;revision_key jsonb;contract_key jsonb;expected_assignment jsonb;expected_physical jsonb;quote_identity jsonb;
 first_day date=(start_at AT TIME ZONE 'UTC')::date;
 last_day date=(end_at AT TIME ZONE 'UTC')::date+CASE WHEN(end_at AT TIME ZONE 'UTC')::time=time '00:00:00' THEN 0 ELSE 1 END;
BEGIN
 IF slot NOT BETWEEN 0 AND 2 OR start_at IS NULL OR end_at IS NULL OR start_at>=end_at OR payment<end_at THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
 SELECT * INTO STRICT curve FROM market_data.curves WHERE(catalog_id,revision_id,code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code);
 SELECT * INTO STRICT grade FROM ref_data.crude_grades WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.grade_code);
 SELECT * INTO STRICT quantity FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(grade.catalog_id,grade.revision_id,grade.quantity_code);
 SELECT * INTO STRICT quote FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,curve.quote_code);
 IF curve.commodity_code<>assignment.commodity_code OR quote.kind<>'outright' OR quote.quantity_code<>grade.quantity_code OR quantity.dimension_code<>'volume'
 OR NOT EXISTS(SELECT 1 FROM ref_data.dimensions d WHERE(d.catalog_id,d.revision_id,d.code)=(quantity.catalog_id,quantity.revision_id,quantity.dimension_code)
 AND(d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power)=(0,3,0,0,0,0,0))
 OR grade.standard_temperature<>60 OR grade.temperature_scale<>'fahrenheit' OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules
 WHERE(catalog_id,revision_id,code,kind,fixed_offset_seconds)=(grade.catalog_id,grade.revision_id,assignment.clock_code,'fixed_offset',0)) THEN RAISE EXCEPTION 'crude_schedule_sources';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
 WHERE(h.catalog_id,h.id)=(catalog,schedule) AND(s.definition_revision_id,s.reference_catalog_id,s.reference_revision_id,s.family)=
 (assignment.revision_id,grade.catalog_id,grade.revision_id,CASE WHEN slot=0 THEN 'crude' ELSE 'fixed' END)) THEN RAISE EXCEPTION 'crude_schedule_revision';END IF;
 SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
 WHERE(m.catalog_id,m.revision_id,m.curve_code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code) AND c.kind='forward'
 AND c.delivery_from::timestamp AT TIME ZONE 'UTC'<=start_at AND c.delivery_to::timestamp AT TIME ZONE 'UTC'>=end_at
 AND NOT EXISTS(SELECT 1 FROM market_data.contract_intervals ci WHERE(ci.catalog_id,ci.revision_id,ci.contract_id)=(c.catalog_id,c.revision_id,c.id))
 AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date;
 SELECT * INTO STRICT target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(grade.catalog_id,grade.revision_id,curve.settlement_target_code);
 IF target.kind<>'index' OR target.index_b_code IS NOT NULL THEN RAISE EXCEPTION 'crude_schedule_sources';END IF;
 SELECT * INTO STRICT fixing FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code)=
 (assignment.catalog_id,assignment.revision_id,target.index_a_code,contract.delivery_from,contract.delivery_to,grade.catalog_id,grade.revision_id,quote.code);
 selected=trading.crude_reference_identity(grade.catalog_id,grade.revision_id,target.index_a_code);
 selection=jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',selected,'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',selected);
 revision_key=jsonb_build_array('definition_revision',assignment.catalog_id,assignment.revision_id);contract_key=jsonb_build_array('contract',revision_key,contract.id);
 quote_identity=jsonb_build_array('quote',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quote.code),1,
 trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quote.currency_code),trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quote.quantity_code),
 trim_scale(quote.multiplier)::text,trim_scale(quote.trading_tick)::text,trim_scale(quote.clearing_increment)::text,trim_scale(quote.minimum)::text,trim_scale(quote.maximum)::text);
 expected_assignment=jsonb_build_object('contract','crude_valuation_assignment_v1','Key',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,assignment.code),
 'Commodity',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,assignment.commodity_code),'Grade',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,assignment.grade_code),
 'Clock',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,assignment.clock_code),'StandardTemperature','60','TemperatureScale','fahrenheit',
 'Location',jsonb_build_object('contract','crude_delivery_location_v1','Facility',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,assignment.facility_code),'CustodyPoint',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,assignment.custody_point_code)),
 'Quantity',jsonb_build_array('quantity_specification_v1',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quantity.code),trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quantity.commodity_code),trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quantity.basis_code),trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quantity.unit_code),trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quantity.dimension_code)),
 'Settlement',selection,'Quote',quote_identity,
 'Projection',jsonb_build_array('projection',jsonb_build_array('request',jsonb_build_array('curve',revision_key,assignment.curve_code),contract_key),
 jsonb_build_array('market_contract',contract_key,2,contract.delivery_from,contract.delivery_to,to_char(contract.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',NULL),NULL),
 'Fixing',jsonb_build_object('contract','IndexFixing_v1','Index',selected,'Quote',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,quote.code),
 'From',fixing.observation_from,'To',fixing.observation_to,'FixingAt',to_char(fixing.fixing_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
 'Series',jsonb_build_array('series',revision_key,fixing.series_id),'Source',trading.crude_reference_identity(grade.catalog_id,grade.revision_id,fixing.source_code),'Implementation',fixing.implementation));
 expected_physical=jsonb_build_object('contract','physical_crude_side_v1','Clock',expected_assignment->'Clock','Commodity',expected_assignment->'Commodity',
 'Grade',expected_assignment->'Grade','Location',expected_assignment->'Location','Quantity',expected_assignment->'Quantity',
 'StandardTemperature','60','TemperatureScale','fahrenheit','Tolerance','0',
 'DeliveryFrom',to_char(start_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
 'DeliveryTo',to_char(end_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00');
 IF slot=0 THEN
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides d ON(d.catalog_id,d.id)=(h.catalog_id,h.side_id)
   WHERE(h.catalog_id,h.id)=(catalog,schedule) AND d.economic_identity::jsonb=expected_physical AND d.pricing_binding_identity::jsonb=expected_assignment) THEN RAISE EXCEPTION 'crude_schedule_assignment';END IF;
  IF(SELECT count(*) FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id)=(catalog,schedule))<>1 OR NOT EXISTS(
   SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id,name,unit_catalog_id,unit_revision_id,unit_code)=
   (catalog,schedule,'quantity',grade.catalog_id,grade.revision_id,quote.quantity_code) AND shape_identity::jsonb=expected_physical) OR
   (SELECT count(*) FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id)=(catalog,schedule))<>1 OR NOT EXISTS(
   SELECT 1 FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id,set_name,root_name)=(catalog,schedule,'quantity','crude/0/quantity'))
  THEN RAISE EXCEPTION 'crude_schedule_outputs';END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
   EXISTS(SELECT 1 FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'crude_schedule_outputs';END IF;
 END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'crude_schedule_ports';END IF;
 IF slot=0 THEN
  IF(SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>9 THEN RAISE EXCEPTION 'crude_schedule_sources';END IF;
  IF EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
   WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND(r.local_key<>'crude/0/market' OR CASE b.port
    WHEN 'market' THEN(b.kind,b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id) IS DISTINCT FROM('contract_price',assignment.catalog_id,assignment.revision_id,assignment.curve_code,contract.id)
    WHEN 'crude' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.crude_assignment_code,b.source_catalog_id,b.definition_revision_id,b.coordinate::jsonb)
     IS DISTINCT FROM('crude_assignment',grade.catalog_id,grade.revision_id,assignment.code,assignment.catalog_id,assignment.revision_id,expected_assignment)
    WHEN 'facility' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.location_code) IS DISTINCT FROM('location',grade.catalog_id,grade.revision_id,assignment.facility_code)
    WHEN 'custody' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.location_code) IS DISTINCT FROM('location',grade.catalog_id,grade.revision_id,assignment.custody_point_code)
    WHEN 'grade' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.crude_grade_code) IS DISTINCT FROM('crude_grade',grade.catalog_id,grade.revision_id,assignment.grade_code)
    WHEN 'commodity' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.crude_product_code) IS DISTINCT FROM('crude_product',grade.catalog_id,grade.revision_id,assignment.commodity_code)
    WHEN 'quote' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.quote_code) IS DISTINCT FROM('quote',grade.catalog_id,grade.revision_id,quote.code)
    WHEN 'index' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.index_code) IS DISTINCT FROM('index',grade.catalog_id,grade.revision_id,target.index_a_code)
    WHEN 'fixing' THEN(b.kind,b.source_catalog_id,b.definition_revision_id,b.reference_catalog_id,b.reference_revision_id,b.series_id,b.source_code,b.fixing_index_code,b.fixing_from,b.fixing_to,b.quote_code,b.coordinate)
     IS DISTINCT FROM('index_fixing',assignment.catalog_id,assignment.revision_id,grade.catalog_id,grade.revision_id,fixing.series_id,fixing.source_code,fixing.index_code,fixing.observation_from,fixing.observation_to,fixing.quote_code,fixing.implementation)
    ELSE true END)) THEN RAISE EXCEPTION 'crude_schedule_sources';END IF;
  IF(SELECT count(*) FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule))<>1 OR EXISTS(
   SELECT 1 FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule) AND(
   (g.clock_catalog_id,g.clock_revision_id,g.clock_code) IS DISTINCT FROM(grade.catalog_id,grade.revision_id,assignment.clock_code) OR
   g.ordinal<>0 OR g.local_key<>'crude/0/delivery' OR g.origin<>start_at OR g.step_ticks<>extract(epoch FROM(end_at-start_at))*10000000 OR
   g.month<>contract.delivery_from OR g.axis_count<>1 OR g.selection<>ARRAY[0] OR g.lengths<>ARRAY[1] OR g.first_row_ordinal IS DISTINCT FROM 0 OR
   g.operating_days<>ARRAY[first_day-date '0001-01-01'] OR g.local_minutes<>ARRAY[extract(hour FROM start_at AT TIME ZONE 'UTC')::integer*60+extract(minute FROM start_at AT TIME ZONE 'UTC')::integer] OR g.occurrences<>ARRAY[1])) THEN RAISE EXCEPTION 'crude_schedule_grid';END IF;
 ELSE
  IF literal IS NULL OR EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
   EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'crude_schedule_sources';END IF;
 END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND(
  r.output_kind IN('price','quantity') AND(r.unit_catalog_id,r.unit_revision_id,r.unit_code) IS DISTINCT FROM(grade.catalog_id,grade.revision_id,quote.quantity_code) OR
  r.output_kind IN('price','money') AND(r.currency_catalog_id,r.currency_revision_id,r.currency_code) IS DISTINCT FROM(grade.catalog_id,grade.revision_id,quote.currency_code) OR
  r.output_kind='price' AND r.quote_multiplier IS DISTINCT FROM quote.multiplier)) THEN RAISE EXCEPTION 'crude_schedule_units';END IF;
 FOR row IN SELECT * FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule) LOOP
  IF row.local_key='side/pv' THEN
   IF row.operation<>'sum' OR row.grid_ordinal IS NOT NULL OR row.value_at IS NOT NULL OR row.payment_ordinal IS NOT NULL THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
   CONTINUE;
  END IF;
  IF slot=0 THEN
   IF row.local_key='crude/0/total' THEN
    IF row.grid_ordinal IS NOT NULL OR row.value_at IS NOT NULL OR row.payment_ordinal IS NOT NULL THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
   ELSE
    IF row.value_at IS DISTINCT FROM end_at OR row.payment_ordinal IS DISTINCT FROM 0 OR row.delivery_from IS NOT NULL OR row.delivery_to IS NOT NULL OR
     row.grid_ordinal IS DISTINCT FROM(CASE WHEN row.local_key='crude/0/market' THEN NULL::integer ELSE 0 END) THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
   END IF;
   IF row.local_key='crude/0/market' THEN
    IF(row.interval_from,row.interval_to,row.operating_date,row.hour_beginning,row.minute,row.occurrence,row.observation_from,row.observation_to)
     IS DISTINCT FROM(start_at,end_at,first_day,extract(hour FROM start_at AT TIME ZONE 'UTC')::smallint,extract(minute FROM start_at AT TIME ZONE 'UTC')::smallint,1::smallint,contract.delivery_from,contract.delivery_to)
     OR(row.parameters->>'selection')::jsonb IS DISTINCT FROM selection THEN RAISE EXCEPTION 'crude_schedule_sources';END IF;
   ELSIF row.interval_from IS NOT NULL OR row.observation_from IS NOT NULL THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
   IF row.occurrence_key IS NOT NULL OR row.terms_digest IS NOT NULL THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
  ELSE
   IF row.value_at IS DISTINCT FROM payment OR row.payment_ordinal IS DISTINCT FROM 0 OR
    row.delivery_from IS DISTINCT FROM first_day OR row.delivery_to IS DISTINCT FROM last_day OR row.grid_ordinal IS NOT NULL OR row.interval_from IS NOT NULL
    THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
   IF row.operation='price_application' THEN
    IF(row.observation_from,row.observation_to) IS DISTINCT FROM(first_day,last_day) THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
   ELSIF row.observation_from IS NOT NULL THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
  END IF;
  IF row.operation='quantity_literal' AND(row.parameters->>'numerator'<>'1' OR row.parameters->>'denominator'<>'1') THEN RAISE EXCEPTION 'crude_schedule_quantity';END IF;
  IF row.operation='constant_price' AND(row.parameters->>'numerator')::numeric<>(row.parameters->>'denominator')::numeric*literal THEN RAISE EXCEPTION 'crude_schedule_price';END IF;
  IF row.operation='price_application' AND((row.parameters->>'numerator')::numeric<>(row.parameters->>'denominator')::numeric OR row.parameters->>'mapping'<>'fixed_total_payment_v1') THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
  IF row.operation='weighted_price' AND row.parameters->>'totalScaledWeight'<>'1000000000000000000' THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
  IF row.operation='discount' AND row.parameters->>'policy'<>'context_required_v1' THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
  IF row.operation='output_guard' AND(row.parameters->>'lifecycle'<>CASE WHEN slot=0 THEN 'future_crude_delivery_v1' ELSE 'undue_payment_v1' END OR
   row.parameters->>'measure'<>CASE WHEN slot=0 THEN 'value_excluding_overdue_v1' ELSE 'present_value_v1' END OR row.parameters->>'pricePolicy'<>'selected_context_v1' OR
   row.parameters->>'averaging'<>'none' OR row.parameters->>'firstFixingAt'<>'none' OR row.parameters->>'paymentRule'<>'explicit_payment_date_v1' OR row.parameters->>'unadjustedPayment'<>'none') THEN RAISE EXCEPTION 'crude_schedule_lifecycle';END IF;
 END LOOP;
 IF EXISTS(WITH expected AS(
  SELECT 'side/pv' k,'sum' op,'root' role,'money' kind UNION ALL
  SELECT 'crude/0/'||v.k,v.op,v.role,v.kind FROM(VALUES
   ('market','price_lookup','source','price'),('quantity','quantity_literal','source','quantity'),('amount','amount','contribution','money'),
   ('discount','discount','source','factor'),('discounted','pv','intermediate','money'),('value','output_guard','root','money'),('total','sum_cells','intermediate','money'))v(k,op,role,kind) WHERE slot=0 UNION ALL
  SELECT 'payment/0/'||v.k,v.op,v.role,v.kind FROM(VALUES
   ('quantity','quantity_literal','source','quantity'),('observation/0','constant_price','source','price'),('application/0','price_application','intermediate','price'),
   ('average','weighted_price','intermediate','price'),('amount','amount','contribution','money'),('discount','discount','source','factor'),('discounted','pv','intermediate','money'),('value','output_guard','root','money'))v(k,op,role,kind) WHERE slot<>0),
 actual AS(SELECT local_key k,operation op,role,output_kind kind FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'crude_schedule_rows';END IF;
 IF EXISTS(WITH expected AS(
  SELECT 'crude/0/'||v.target target,v.port,v.ord ordinal,'crude/0/'||v.source source FROM(VALUES
   ('amount','quantity',0,'quantity'),('amount','price',0,'market'),('discounted','amount',0,'amount'),('discounted','discount',0,'discount'),
   ('value','amount',0,'amount'),('value','discounted',0,'discounted'),('total','input',0,'value'))v(target,port,ord,source) WHERE slot=0 UNION ALL
  SELECT 'side/pv','input',0,'crude/0/total' WHERE slot=0 UNION ALL
  SELECT 'payment/0/'||v.target,v.port,v.ord,'payment/0/'||v.source FROM(VALUES
   ('application/0','input',0,'observation/0'),('average','input',0,'application/0'),('amount','quantity',0,'quantity'),('amount','price',0,'average'),
   ('discounted','amount',0,'amount'),('discounted','discount',0,'discount'),('value','amount',0,'amount'),('value','discounted',0,'discounted'))v(target,port,ord,source) WHERE slot<>0 UNION ALL
  SELECT 'side/pv','input',0,'payment/0/value' WHERE slot<>0),
 actual AS(SELECT t.local_key target,e.port,e.ordinal,s.local_key source FROM trading.side_schedule_edges e
 JOIN trading.side_schedule_rows t ON(t.catalog_id,t.schedule_id,t.ordinal)=(e.catalog_id,e.schedule_id,e.target)
 JOIN trading.side_schedule_rows s ON(s.catalog_id,s.schedule_id,s.ordinal)=(e.catalog_id,e.schedule_id,e.source) WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'crude_schedule_edges';END IF;
 IF EXISTS(WITH expected AS(
  SELECT 'pv' name,'side/pv' k,'pv' measure UNION ALL
  SELECT 'crude/0/'||v.name,'crude/0/'||v.k,v.measure FROM(VALUES('quantity','quantity','schedule_quantity'),('delivery-value','amount','physical_value'),('pv','value','contribution_pv'))v(name,k,measure) WHERE slot=0 UNION ALL
  SELECT 'payment/0/'||v.name,'payment/0/'||v.k,v.measure FROM(VALUES('cash','amount','contractual_amount'),('pv','value','contribution_pv'))v(name,k,measure) WHERE slot<>0),
 actual AS(SELECT o.name,r.local_key k,o.measure FROM trading.side_schedule_roots o JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal) WHERE(o.catalog_id,o.schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'crude_schedule_roots';END IF;
END$$;
ALTER FUNCTION trading.require_crude_program_shape(uuid,uuid,integer,timestamptz,timestamptz,timestamptz,numeric,market_data.crude_valuation_assignments) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_crude_program_shape(uuid,uuid,integer,timestamptz,timestamptz,timestamptz,numeric,market_data.crude_valuation_assignments) FROM PUBLIC;
REVOKE ALL ON FUNCTION trading.crude_reference_identity(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.crude_reference_identity(uuid,uuid,text) TO openetrm_security_owner;

CREATE FUNCTION security.crude_operation_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE r trading.crude_trade_revisions;actor uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No crude read access' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO r FROM trading.crude_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation) AND actor_id=actor;
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false);END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Crude read authority expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('tradeId',r.trade_id,'instrumentId',r.instrument_id,'revision',r.revision,'operationId',operation);
END$$;
CREATE FUNCTION security.book_physical_crude(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE c security.request_contexts;prior trading.trades;stage trading.side_schedule_requests;assignment market_data.crude_valuation_assignments;
 operation uuid=(body->>'operationId')::uuid;definition uuid=(body->>'definitionRevisionId')::uuid;book uuid=(body->>'bookId')::uuid;
 start_at timestamptz=(body->>'deliveryFrom')::timestamptz;end_at timestamptz=(body->>'deliveryTo')::timestamptz;payment timestamptz=(body->>'paymentAt')::timestamptz;
 quantity numeric=(body->>'quantity')::numeric;price numeric=(body->>'fixedPrice')::numeric;fee numeric=(body->>'handlingFee')::numeric;
 publication integer;trade uuid;instrument uuid;booking jsonb;item record;expected integer;direction integer;consideration_direction integer;fee_direction integer;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'No crude booking access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request IS DISTINCT FROM body OR NOT EXISTS(SELECT 1 FROM trading.crude_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'crude_conflict';END IF;
  RETURN security.crude_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR
 EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR EXISTS(SELECT 1 FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'crude_conflict';END IF;
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body ? 'handlingFee') OR NOT(body ? 'handlingFeeDirection') OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN(
 'snapshotId','convention','knowledgeMode','profile','operationId','bookId','definitionRevisionId','assignmentCode','deliveryFrom','deliveryTo','quantity','quantityBasis','tolerance',
 'direction','considerationDirection','fixedPrice','handlingFee','handlingFeeDirection','paymentAt','discounting','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision')) THEN RAISE EXCEPTION 'crude_input';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR body->>'convention' IS DISTINCT FROM 'physical_crude_fixed_volume_v1' OR
 body->>'knowledgeMode' IS DISTINCT FROM 'current' OR body->>'profile' IS DISTINCT FROM 'physical_crude_standard_volume_v1' OR
 body->>'quantityBasis' IS DISTINCT FROM 'standard_volume_total' OR(body->>'tolerance')::numeric IS DISTINCT FROM 0 OR body->'discounting' IS DISTINCT FROM 'false'::jsonb OR
 nullif(body->>'assignmentCode','') IS NULL OR quantity IS NULL OR quantity<=0 OR quantity>=1e24 OR scale(quantity)>18 OR price IS NULL OR abs(price)>=1e12 OR scale(price)>18 OR
 (fee IS NOT NULL AND(abs(fee)>=1e12 OR scale(fee)>18)) OR start_at IS NULL OR end_at IS NULL OR NOT isfinite(start_at) OR NOT isfinite(end_at) OR
 start_at>=end_at OR start_at<'2000-01-01T00:00:00Z'::timestamptz OR end_at>'2100-01-01T00:00:00Z'::timestamptz OR
 valuation IS NULL OR NOT isfinite(valuation) OR payment IS NULL OR NOT isfinite(payment) OR
 body->>'direction' IS NULL OR body->>'direction' NOT IN('receive','deliver') OR body->>'considerationDirection' IS NULL OR body->>'considerationDirection' NOT IN('pay','receive') OR
 (fee IS NULL AND body->>'handlingFeeDirection' IS NOT NULL) OR (fee IS NOT NULL AND coalesce(body->>'handlingFeeDirection','') NOT IN('pay','receive')) THEN RAISE EXCEPTION 'crude_input';END IF;
 -- PostgreSQL timestamps have microsecond precision. Refuse finer inputs before
 -- their rounded casts can become successful contractual terms.
 IF EXISTS(SELECT 1 FROM unnest(ARRAY[body->>'deliveryFrom',body->>'deliveryTo',body->>'paymentAt']) value
 WHERE value !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6}|\.[0-9]{6}0)?(Z|\+00:00)$') THEN RAISE EXCEPTION 'crude_input';END IF;
 IF valuation>=start_at OR payment<end_at THEN RAISE EXCEPTION 'crude_delivery_lifecycle';END IF;
 SELECT * INTO stage FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,operation);
 IF NOT FOUND OR stage.command_hash<>sha256(convert_to(body::text,'UTF8')) OR stage.actor_id<>c.actor_id OR stage.permission<>'BookTrade' THEN RAISE EXCEPTION 'crude_schedule';END IF;
 PERFORM security.lock_side_schedule_request(catalog,operation);
 SELECT a.* INTO assignment FROM market_data.crude_valuation_assignments a JOIN market_data.revisions r ON(r.catalog_id,r.id)=(a.catalog_id,a.revision_id)
 JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(r.catalog_id,r.id)
 WHERE(a.catalog_id,a.revision_id,a.code)=(catalog,definition,body->>'assignmentCode') AND r.state='sealed'
 AND p.effective_from<=(valuation AT TIME ZONE 'UTC')::date AND(valuation AT TIME ZONE 'UTC')::date<p.effective_to;
 IF NOT FOUND OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(assignment.reference_catalog_id,'ReadMarketData') OR
 NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(assignment.reference_catalog_id,assignment.reference_revision_id) THEN RAISE EXCEPTION 'crude_definition';END IF;
 expected=CASE WHEN fee IS NULL THEN 2 ELSE 3 END;
 IF(SELECT count(*) FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,operation))<>expected OR
 EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)) OR
 EXISTS(SELECT 1 FROM trading.instrument_schedule_cells WHERE(catalog_id,request_id)=(catalog,operation)) THEN RAISE EXCEPTION 'crude_schedule';END IF;
 FOR item IN SELECT i.slot,i.schedule_id,s.family FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id)
 JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) WHERE(i.catalog_id,i.request_id)=(catalog,operation) ORDER BY i.slot LOOP
  IF item.slot<0 OR item.slot>=expected OR item.family<>(CASE WHEN item.slot=0 THEN 'crude' ELSE 'fixed' END) THEN RAISE EXCEPTION 'crude_schedule';END IF;
  PERFORM trading.require_crude_program_shape(catalog,item.schedule_id,item.slot,start_at,end_at,payment,CASE WHEN item.slot=1 THEN price ELSE fee END,assignment);
 END LOOP;
 booking=jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date,'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision');
 PERFORM security.require_trade_parties(catalog,booking,valuation);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
 WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=valuation AND p.effective_at<=valuation) THEN RAISE EXCEPTION 'crude_book';END IF;
 direction=CASE body->>'direction' WHEN 'receive' THEN 1 ELSE -1 END;
 consideration_direction=CASE body->>'considerationDirection' WHEN 'receive' THEN 1 ELSE -1 END;
 fee_direction=CASE body->>'handlingFeeDirection' WHEN 'receive' THEN 1 WHEN 'pay' THEN -1 END;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());instrument=gen_random_uuid();trade=gen_random_uuid();
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.crude_instruments VALUES(catalog,instrument,definition,assignment.code,start_at,end_at,'standard_volume_total',0,price,fee,payment,consideration_direction,fee_direction,publication,valuation);
 INSERT INTO trading.crude_memberships SELECT catalog,instrument,i.slot,h.side_id FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id) WHERE(i.catalog_id,i.request_id)=(catalog,operation);
 PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,body,publication,valuation);
 INSERT INTO trading.crude_trade_revisions VALUES(catalog,trade,1,instrument,book,operation,c.actor_id,quantity,direction,
 (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation);
 IF NOT security.trading_access(catalog,'BookTrade') OR NOT security.trading_access(catalog,'ReadTrades') OR
 NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(assignment.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Crude booking authority expired' USING ERRCODE='42501';END IF;
 RETURN security.crude_operation_status(catalog,operation);
END$$;
ALTER FUNCTION security.crude_operation_status(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.book_physical_crude(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.crude_operation_status(uuid,uuid),security.book_physical_crude(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.crude_operation_status(uuid,uuid),security.book_physical_crude(uuid,jsonb,timestamptz) TO openetrm_runtime;
CREATE FUNCTION security.require_supported_crude_portfolio_scope(catalog uuid,books uuid[],portfolios uuid[]) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR(session_user='openetrm_risk_worker' AND NOT risk.worker_read()) THEN RAISE EXCEPTION 'No crude portfolio authority' USING ERRCODE='42501';END IF;
 IF books IS NULL OR portfolios IS NULL OR cardinality(books)>128 OR cardinality(portfolios)>128 OR array_position(books,NULL) IS NOT NULL OR array_position(portfolios,NULL) IS NOT NULL THEN RAISE EXCEPTION 'crude_portfolio_scope';END IF;
 IF EXISTS(SELECT 1 FROM trading.crude_trade_revisions r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
 WHERE r.catalog_id=catalog AND security.trading_visible(catalog,r.publication_order,r.effective_at)
 AND(cardinality(books)=0 OR r.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))) THEN RAISE EXCEPTION 'crude_portfolio_scope';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Crude portfolio authority expired' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.require_supported_crude_portfolio_scope(uuid,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_supported_crude_portfolio_scope(uuid,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.require_supported_crude_portfolio_scope(uuid,uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
