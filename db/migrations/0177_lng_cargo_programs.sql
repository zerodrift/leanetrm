-- LNG bookings retain their own exact physical terms and common executable programs.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.lng_instruments (
 catalog_id uuid NOT NULL, instrument_id uuid NOT NULL, definition_revision_id uuid NOT NULL,
 assignment_code ref_data.code NOT NULL, reference_catalog_id uuid NOT NULL, reference_revision_id uuid NOT NULL,
 product_code ref_data.code NOT NULL, energy_quantity_code ref_data.code NOT NULL,
 load_stage text NOT NULL CHECK(load_stage='load'), load_facility_code ref_data.code NOT NULL, load_custody_code ref_data.code NOT NULL,
 load_from ref_data.instant NOT NULL, load_to ref_data.instant NOT NULL,
 discharge_stage text NOT NULL CHECK(discharge_stage='discharge'), discharge_facility_code ref_data.code NOT NULL, discharge_custody_code ref_data.code NOT NULL,
 discharge_from ref_data.instant NOT NULL, discharge_to ref_data.instant NOT NULL, clock_code ref_data.code NOT NULL,
 incoterms_edition_code ref_data.code NOT NULL, incoterms_rule_code ref_data.code NOT NULL, incoterms_place_code ref_data.code NOT NULL,
 fixed_price numeric NOT NULL CHECK(abs(fixed_price)<1e12 AND scale(fixed_price)<=18),
 payment_at ref_data.instant NOT NULL, cash_direction integer NOT NULL CHECK(cash_direction IN(-1,1)),
 publication_order integer NOT NULL, effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 UNIQUE(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,product_code,energy_quantity_code),
 CHECK(load_from<load_to AND load_to<=discharge_from AND discharge_from<discharge_to AND payment_at>=discharge_to),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id,assignment_code)
 REFERENCES market_data.lng_valuation_assignments(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,energy_quantity_code)
 REFERENCES ref_data.lng_products(catalog_id,revision_id,code,energy_quantity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,load_stage,load_facility_code,load_custody_code)
 REFERENCES ref_data.lng_custody_points(catalog_id,revision_id,product_code,stage,facility_code,custody_point_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,discharge_stage,discharge_facility_code,discharge_custody_code)
 REFERENCES ref_data.lng_custody_points(catalog_id,revision_id,product_code,stage,facility_code,custody_point_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,incoterms_edition_code,incoterms_rule_code)
 REFERENCES ref_data.incoterms_rules(catalog_id,revision_id,edition_code,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,incoterms_place_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.lng_memberships (
 catalog_id uuid NOT NULL, instrument_id uuid NOT NULL, slot integer NOT NULL CHECK(slot IN(0,1)), side_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,slot), UNIQUE(catalog_id,instrument_id,slot,side_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.lng_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.sides(catalog_id,id)
);
CREATE TABLE trading.lng_trade_revisions (
 catalog_id uuid NOT NULL, trade_id uuid NOT NULL, revision integer NOT NULL CHECK(revision=1), instrument_id uuid NOT NULL, book_id uuid NOT NULL,
 operation_id uuid NOT NULL, actor_id uuid NOT NULL REFERENCES security.principals(id),
 reference_catalog_id uuid NOT NULL, reference_revision_id uuid NOT NULL, product_code ref_data.code NOT NULL, energy_quantity_code ref_data.code NOT NULL,
 original_quantity numeric NOT NULL CHECK(original_quantity>0 AND original_quantity<1e24 AND scale(original_quantity)<=18),
 quantity_code ref_data.code NOT NULL, conversion_code ref_data.code,
 energy_quantity numeric NOT NULL CHECK(energy_quantity>0 AND energy_quantity<1e24 AND scale(energy_quantity)<=18),
 direction integer NOT NULL CHECK(direction IN(-1,1)), legal_entity_id uuid NOT NULL, legal_entity_revision integer NOT NULL,
 counterparty_id uuid NOT NULL, counterparty_revision integer NOT NULL, publication_order integer NOT NULL, effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision), UNIQUE(catalog_id,operation_id),
 CHECK((quantity_code=energy_quantity_code AND conversion_code IS NULL AND original_quantity=energy_quantity)
 OR(quantity_code<>energy_quantity_code AND conversion_code IS NOT NULL)),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,product_code,energy_quantity_code)
 REFERENCES trading.lng_instruments(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,product_code,energy_quantity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,conversion_code,product_code,quantity_code,energy_quantity_code)
 REFERENCES ref_data.lng_mass_energy_conversions(catalog_id,revision_id,code,product_code,mass_quantity_code,energy_quantity_code),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['lng_instruments','lng_memberships','lng_trade_revisions'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades''))',t);
  IF t='lng_memberships' THEN
   EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.lng_instruments i WHERE(i.catalog_id,i.instrument_id)=(lng_memberships.catalog_id,lng_memberships.instrument_id)))',t);
  ELSE
   EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  END IF;
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;

CREATE FUNCTION security.resolve_lng_delivery(catalog uuid,physical jsonb,start_at timestamptz,end_at timestamptz,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE reference_catalog uuid; reference_revision uuid; candidate record; result jsonb;
 overlapping integer=0; covered integer=0; tuples integer=0; intersects boolean; complete boolean;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
 THEN RAISE EXCEPTION 'No current LNG resolution authority' USING ERRCODE='42501'; END IF;
 IF jsonb_typeof(physical) IS DISTINCT FROM 'object' OR
 NOT physical ?& ARRAY['referenceCatalogId','referenceRevisionId','commodity','product','loadFacility','loadCustody','dischargeFacility','dischargeCustody','energyQuantityCode','clock'] OR
 physical-ARRAY['referenceCatalogId','referenceRevisionId','commodity','product','loadFacility','loadCustody','dischargeFacility','dischargeCustody','energyQuantityCode','clock']<>'{}'::jsonb OR
 EXISTS(SELECT 1 FROM jsonb_each(physical) p WHERE jsonb_typeof(p.value)<>'string' OR nullif(btrim(p.value#>>'{}'),'') IS NULL) OR
 start_at IS NULL OR end_at IS NULL OR valuation IS NULL OR NOT isfinite(start_at) OR NOT isfinite(end_at) OR NOT isfinite(valuation) OR start_at>=end_at
 THEN RAISE EXCEPTION 'lng_reference_input'; END IF;
 reference_catalog=(physical->>'referenceCatalogId')::uuid; reference_revision=(physical->>'referenceRevisionId')::uuid;
 IF NOT security.reference_access(reference_catalog,'ReadMarketData') OR NOT security.reference_version_visible(reference_catalog,reference_revision)
 THEN RAISE EXCEPTION 'No LNG master authority' USING ERRCODE='42501'; END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.lng_products p JOIN ref_data.lng_custody_points l
 ON(l.catalog_id,l.revision_id,l.product_code,l.stage,l.facility_code,l.custody_point_code)=
 (p.catalog_id,p.revision_id,p.code,'load',physical->>'loadFacility',physical->>'loadCustody')
 JOIN ref_data.lng_custody_points d ON(d.catalog_id,d.revision_id,d.product_code,d.stage,d.facility_code,d.custody_point_code)=
 (p.catalog_id,p.revision_id,p.code,'discharge',physical->>'dischargeFacility',physical->>'dischargeCustody')
 JOIN ref_data.clock_rules clock ON(clock.catalog_id,clock.revision_id,clock.code,clock.kind,clock.fixed_offset_seconds)=
 (p.catalog_id,p.revision_id,physical->>'clock','fixed_offset',0)
 WHERE(p.catalog_id,p.revision_id,p.code,p.commodity_code,p.energy_quantity_code)=
 (reference_catalog,reference_revision,physical->>'product',physical->>'commodity',physical->>'energyQuantityCode'))
 THEN RAISE EXCEPTION 'lng_reference_relationship'; END IF;
 FOR candidate IN SELECT a.* FROM market_data.lng_valuation_assignments a JOIN market_data.revisions r
 ON(r.catalog_id,r.id,r.state)=(a.catalog_id,a.revision_id,'sealed')
 JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(r.catalog_id,r.id)
 WHERE(a.catalog_id,a.reference_catalog_id,a.reference_revision_id,a.product_code,a.discharge_facility_code,a.discharge_custody_code,a.energy_quantity_code,a.clock_code)=
 (catalog,reference_catalog,reference_revision,physical->>'product',physical->>'dischargeFacility',physical->>'dischargeCustody',physical->>'energyQuantityCode',physical->>'clock')
 AND p.effective_from<=(valuation AT TIME ZONE 'UTC')::date AND(valuation AT TIME ZONE 'UTC')::date<p.effective_to
 LOOP
  IF NOT security.market_fact_visible(catalog,candidate.revision_id,NULL) THEN CONTINUE; END IF;
  tuples=tuples+1;
  SELECT coalesce(bool_or(c.delivery_from::timestamp AT TIME ZONE 'UTC'<end_at AND start_at<c.delivery_to::timestamp AT TIME ZONE 'UTC'),false),
   coalesce(bool_or(c.delivery_from::timestamp AT TIME ZONE 'UTC'<=start_at AND end_at<=c.delivery_to::timestamp AT TIME ZONE 'UTC'),false)
  INTO intersects,complete FROM market_data.curve_members m JOIN market_data.contracts c
  ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
  WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,candidate.revision_id,candidate.curve_code)
  AND c.kind='forward' AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date
  AND NOT EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(c.catalog_id,c.revision_id,c.id));
  IF intersects THEN overlapping=overlapping+1; END IF;
  IF complete THEN
   covered=covered+1;
   result=jsonb_build_object('definitionRevisionId',candidate.revision_id,'assignmentCode',candidate.code,'curve',candidate.curve_code);
  END IF;
 END LOOP;
 IF overlapping>1 OR covered>1 THEN RAISE EXCEPTION 'lng_price_mapping_ambiguous'; END IF;
 IF covered<>1 THEN
  IF tuples=0 THEN RAISE EXCEPTION 'lng_price_mapping_missing'; ELSE RAISE EXCEPTION 'lng_price_mapping_coverage'; END IF;
 END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(reference_catalog,'ReadMarketData')
 OR NOT security.reference_version_visible(reference_catalog,reference_revision)
 OR NOT security.market_fact_visible(catalog,(result->>'definitionRevisionId')::uuid,NULL)
 THEN RAISE EXCEPTION 'LNG resolution authority expired' USING ERRCODE='42501'; END IF;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'lng_reference_input';
END$$;
ALTER FUNCTION security.resolve_lng_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.resolve_lng_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.resolve_lng_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz) TO openetrm_runtime;

ALTER TABLE trading.side_schedule_bindings
 ADD COLUMN lng_assignment_code ref_data.code, ADD COLUMN lng_product_code ref_data.code,
 ADD FOREIGN KEY(source_catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id,lng_assignment_code)
 REFERENCES market_data.lng_valuation_assignments(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,lng_product_code) REFERENCES ref_data.lng_products(catalog_id,revision_id,code);
ALTER TABLE trading.instrument_side_schedules ADD COLUMN lng_side_id uuid,
 ADD FOREIGN KEY(catalog_id,instrument_id,slot,lng_side_id) REFERENCES trading.lng_memberships(catalog_id,instrument_id,slot,side_id);

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.instrument_side_schedules'::regclass AND conname='instrument_side_schedules_check') IS DISTINCT FROM $expected$CHECK ((num_nonnulls(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id, crude_side_id) = 1))$expected$ THEN RAISE EXCEPTION 'lng_program_constraint_predecessor: instrument_side_schedules_check'; END IF; END$guard$;
ALTER TABLE trading.instrument_side_schedules DROP CONSTRAINT instrument_side_schedules_check;
ALTER TABLE trading.instrument_side_schedules ADD CONSTRAINT instrument_side_schedules_check CHECK ((num_nonnulls(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id, crude_side_id, lng_side_id) = 1));

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.instrument_side_schedules'::regclass AND conname='instrument_side_schedules_check1') IS DISTINCT FROM $expected$CHECK ((side_id = COALESCE(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id, crude_side_id)))$expected$ THEN RAISE EXCEPTION 'lng_program_constraint_predecessor: instrument_side_schedules_check1'; END IF; END$guard$;
ALTER TABLE trading.instrument_side_schedules DROP CONSTRAINT instrument_side_schedules_check1;
ALTER TABLE trading.instrument_side_schedules ADD CONSTRAINT instrument_side_schedules_check1 CHECK ((side_id = COALESCE(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id, crude_side_id, lng_side_id)));

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND conname='side_schedule_bindings_kind_check') IS DISTINCT FROM $expected$CHECK ((kind = ANY (ARRAY['contract_price'::text, 'index_fixing'::text, 'settlement_source'::text, 'curve_interval'::text, 'custom_output'::text, 'quantity_source'::text, 'asset'::text, 'sibling_quantity'::text, 'location'::text, 'index'::text, 'quote'::text, 'gas_assignment'::text, 'gas_product'::text, 'crude_assignment'::text, 'crude_grade'::text, 'crude_product'::text])))$expected$ THEN RAISE EXCEPTION 'lng_program_constraint_predecessor: side_schedule_bindings_kind_check'; END IF; END$guard$;
ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT side_schedule_bindings_kind_check;
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_bindings_kind_check CHECK ((kind = ANY (ARRAY['contract_price'::text, 'index_fixing'::text, 'settlement_source'::text, 'curve_interval'::text, 'custom_output'::text, 'quantity_source'::text, 'asset'::text, 'sibling_quantity'::text, 'location'::text, 'index'::text, 'quote'::text, 'gas_assignment'::text, 'gas_product'::text, 'crude_assignment'::text, 'crude_grade'::text, 'crude_product'::text, 'lng_assignment'::text, 'lng_product'::text])));

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND conname='side_schedule_bindings_check') IS DISTINCT FROM $expected$CHECK (
CASE kind
    WHEN 'contract_price'::text THEN (num_nonnulls(source_catalog_id, definition_revision_id, curve_code, contract_id) = 4)
    WHEN 'index_fixing'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, source_code, source_catalog_id, definition_revision_id, series_id, fixing_index_code, fixing_from, fixing_to, quote_code, coordinate) = 11)
    WHEN 'settlement_source'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, source_code, source_catalog_id, definition_revision_id, series_id, coordinate) = 7) AND (coordinate = ANY (ARRAY['DailySettlement'::text, 'TraderMark'::text])))
    WHEN 'curve_interval'::text THEN ((num_nonnulls(source_catalog_id, definition_revision_id) = 2) AND (num_nonnulls(curve_code, shaped_curve_code) = 1))
    WHEN 'custom_output'::text THEN ((num_nonnulls(custom_catalog_id, custom_curve_id, custom_version, output_node, source_catalog_id, definition_revision_id, curve_code, contract_id, coordinate) = 9) AND (coordinate = output_node) AND (("position" IS NULL) OR ("position" >= 0)))
    WHEN 'quantity_source'::text THEN (num_nonnulls(source_catalog_id, definition_revision_id, quantity_source_code) = 3)
    WHEN 'asset'::text THEN (num_nonnulls(source_catalog_id, definition_revision_id, asset_code) = 3)
    WHEN 'sibling_quantity'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, quantity_code, coordinate, "position") = 5) AND (("position" >= 0) AND ("position" <= 7)))
    WHEN 'crude_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'crude_grade'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_grade_code) = 3)
    WHEN 'crude_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_product_code) = 3)
    WHEN 'gas_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'gas_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_product_code) = 3)
    WHEN 'location'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, location_code) = 3) AND (((port = ANY (ARRAY['point'::text, 'facility'::text, 'custody'::text])) AND (coordinate IS NULL)) OR ((port = 'location'::text) AND (coordinate IS NOT NULL))))
    WHEN 'index'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, index_code) = 3)
    WHEN 'quote'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, quote_code) = 3)
    ELSE false
END)$expected$ THEN RAISE EXCEPTION 'lng_program_constraint_predecessor: side_schedule_bindings_check'; END IF; END$guard$;
ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT side_schedule_bindings_check;
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_bindings_check CHECK (
CASE kind
    WHEN 'contract_price'::text THEN (num_nonnulls(source_catalog_id, definition_revision_id, curve_code, contract_id) = 4)
    WHEN 'index_fixing'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, source_code, source_catalog_id, definition_revision_id, series_id, fixing_index_code, fixing_from, fixing_to, quote_code, coordinate) = 11)
    WHEN 'settlement_source'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, source_code, source_catalog_id, definition_revision_id, series_id, coordinate) = 7) AND (coordinate = ANY (ARRAY['DailySettlement'::text, 'TraderMark'::text])))
    WHEN 'curve_interval'::text THEN ((num_nonnulls(source_catalog_id, definition_revision_id) = 2) AND (num_nonnulls(curve_code, shaped_curve_code) = 1))
    WHEN 'custom_output'::text THEN ((num_nonnulls(custom_catalog_id, custom_curve_id, custom_version, output_node, source_catalog_id, definition_revision_id, curve_code, contract_id, coordinate) = 9) AND (coordinate = output_node) AND (("position" IS NULL) OR ("position" >= 0)))
    WHEN 'quantity_source'::text THEN (num_nonnulls(source_catalog_id, definition_revision_id, quantity_source_code) = 3)
    WHEN 'asset'::text THEN (num_nonnulls(source_catalog_id, definition_revision_id, asset_code) = 3)
    WHEN 'sibling_quantity'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, quantity_code, coordinate, "position") = 5) AND (("position" >= 0) AND ("position" <= 7)))
    WHEN 'lng_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, lng_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'lng_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, lng_product_code) = 3)
    WHEN 'crude_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'crude_grade'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_grade_code) = 3)
    WHEN 'crude_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_product_code) = 3)
    WHEN 'gas_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'gas_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_product_code) = 3)
    WHEN 'location'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, location_code) = 3) AND (((port = ANY (ARRAY['point'::text, 'facility'::text, 'custody'::text, 'lng_terminal'::text, 'lng_custody'::text])) AND (coordinate IS NULL)) OR ((port = 'location'::text) AND (coordinate IS NOT NULL))))
    WHEN 'index'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, index_code) = 3)
    WHEN 'quote'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, quote_code) = 3)
    ELSE false
END);

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND conname='side_schedule_bindings_check1') IS DISTINCT FROM $expected$CHECK ((num_nonnulls(reference_catalog_id, reference_revision_id, index_code, quantity_code, location_code, quote_code, source_catalog_id, definition_revision_id, curve_code, contract_id, shaped_curve_code, quantity_source_code, asset_code, series_id, custom_catalog_id, custom_curve_id, custom_version, output_node, "position", coordinate, source_code, fixing_index_code, fixing_from, fixing_to, gas_assignment_code, gas_product_code, crude_assignment_code, crude_grade_code, crude_product_code) =
CASE kind
    WHEN 'contract_price'::text THEN 4
    WHEN 'index_fixing'::text THEN 11
    WHEN 'settlement_source'::text THEN 7
    WHEN 'curve_interval'::text THEN 3
    WHEN 'custom_output'::text THEN (9 + num_nonnulls("position"))
    WHEN 'quantity_source'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'asset'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'sibling_quantity'::text THEN 5
    WHEN 'crude_assignment'::text THEN 6
    WHEN 'crude_grade'::text THEN 3
    WHEN 'crude_product'::text THEN 3
    WHEN 'gas_assignment'::text THEN 6
    WHEN 'gas_product'::text THEN 3
    WHEN 'location'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'index'::text THEN 3
    WHEN 'quote'::text THEN 3
    ELSE NULL::integer
END))$expected$ THEN RAISE EXCEPTION 'lng_program_constraint_predecessor: side_schedule_bindings_check1'; END IF; END$guard$;
ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT side_schedule_bindings_check1;
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_bindings_check1 CHECK ((num_nonnulls(reference_catalog_id, reference_revision_id, index_code, quantity_code, location_code, quote_code, source_catalog_id, definition_revision_id, curve_code, contract_id, shaped_curve_code, quantity_source_code, asset_code, series_id, custom_catalog_id, custom_curve_id, custom_version, output_node, "position", coordinate, source_code, fixing_index_code, fixing_from, fixing_to, gas_assignment_code, gas_product_code, crude_assignment_code, crude_grade_code, crude_product_code, lng_assignment_code, lng_product_code) =
CASE kind
    WHEN 'contract_price'::text THEN 4
    WHEN 'index_fixing'::text THEN 11
    WHEN 'settlement_source'::text THEN 7
    WHEN 'curve_interval'::text THEN 3
    WHEN 'custom_output'::text THEN (9 + num_nonnulls("position"))
    WHEN 'quantity_source'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'asset'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'sibling_quantity'::text THEN 5
    WHEN 'lng_assignment'::text THEN 6
    WHEN 'lng_product'::text THEN 3
    WHEN 'crude_assignment'::text THEN 6
    WHEN 'crude_grade'::text THEN 3
    WHEN 'crude_product'::text THEN 3
    WHEN 'gas_assignment'::text THEN 6
    WHEN 'gas_product'::text THEN 3
    WHEN 'location'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'index'::text THEN 3
    WHEN 'quote'::text THEN 3
    ELSE NULL::integer
END));

-- Exact source hashes and anchor counts guard the additive program extension.
CREATE FUNCTION pg_temp.lng_upgrade_function(signature regprocedure,expected_source text,changes jsonb,replace_return boolean DEFAULT false)
RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $helper$
DECLARE source text; change jsonb; before_text text; after_text text; matches integer;
BEGIN
 IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_proc WHERE oid=signature) IS DISTINCT FROM expected_source
 THEN RAISE EXCEPTION 'lng_program_upgrade_predecessor: %',signature; END IF;
 source=pg_get_functiondef(signature);
 FOR change IN SELECT value FROM jsonb_array_elements(changes) LOOP
  before_text=change->>'before'; after_text=change->>'after';
  IF before_text IS NULL OR before_text='' OR after_text IS NULL THEN RAISE EXCEPTION 'lng_program_upgrade_patch'; END IF;
  matches=(length(source)-length(replace(source,before_text,'')))/length(before_text);
  IF matches<>(change->>'count')::integer THEN RAISE EXCEPTION 'lng_program_upgrade_anchor: %',signature; END IF;
  source=replace(source,before_text,after_text);
 END LOOP;
 IF replace_return THEN EXECUTE format('DROP FUNCTION %s',signature); END IF;
 EXECUTE source;
END$helper$;

SELECT pg_temp.lng_upgrade_function('security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb)','84b0a8fd824b7b4ee5d967d43927f405cffacd3936298178a81d0a9b65eda0dc',
 jsonb_build_array(
 jsonb_build_object('before',$before$'capacity','gas','crude')$before$,'after',$after$'capacity','gas','crude','lng')$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.append_side_schedule_aux(uuid,uuid,uuid,text,jsonb)','4cd39598c3ace94a94bc5dd26ba54ef5e580c0d3395729a1438605d143342352',
 jsonb_build_array(
 jsonb_build_object('before',$before$'crude_product_code','sequence_ordinal'$before$,'after',$after$'crude_product_code','lng_assignment_code','lng_product_code','sequence_ordinal'$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.seal_side_schedule(uuid,uuid,uuid,text)','a5908c1f572f1b3c4daebbf2bf6ef887b70e67708bb390a5b881c4c4845bc1c4',
 jsonb_build_array(
 jsonb_build_object('before',$before$bool_or(port='crude') crude_assignment$before$,'after',$after$bool_or(port='lng') lng_assignment,bool_or(port='lng_terminal') lng_terminal,bool_or(port='lng_custody') lng_custody,bool_or(port='lng_product') lng_product,
   bool_or(port='crude') crude_assignment$after$,'count',1),
 jsonb_build_object('before',$before$WHEN 'crude' THEN 'crude_assignment'$before$,'after',$after$WHEN 'lng' THEN 'lng_assignment' WHEN 'lng_terminal' THEN 'location' WHEN 'lng_custody' THEN 'location' WHEN 'lng_product' THEN 'lng_product' WHEN 'crude' THEN 'crude_assignment'$after$,'count',1),
 jsonb_build_object('before',$before$s.gas_assignment AND s.gas_point AND s.gas_product) AND (NOT(coalesce(s.crude_assignment$before$,'after',$after$s.gas_assignment AND s.gas_point AND s.gas_product) AND (NOT(coalesce(s.lng_assignment,false) OR coalesce(s.lng_terminal,false) OR coalesce(s.lng_custody,false) OR coalesce(s.lng_product,false)) OR s.lng_assignment AND s.lng_terminal AND s.lng_custody AND s.lng_product AND NOT(coalesce(s.gas_assignment,false) OR coalesce(s.gas_point,false) OR coalesce(s.gas_product,false) OR coalesce(s.crude_assignment,false) OR coalesce(s.crude_facility,false) OR coalesce(s.crude_custody,false) OR coalesce(s.crude_grade,false) OR coalesce(s.crude_product,false))) AND (NOT(coalesce(s.crude_assignment$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('trading.require_side_program_content(uuid,uuid,bytea)','a3073213de5c0a6c72bc8b4dd83614752afd7a0fea11a98d5128d58a0b9cfffa',
 jsonb_build_array(
 jsonb_build_object('before',$before$'future_crude_delivery_v1')$before$,'after',$after$'future_crude_delivery_v1','future_lng_cargo_v1')$after$,'count',1),
 jsonb_build_object('before',$before$ IF document IS DISTINCT FROM trading.side_program_document(catalog,schedule)$before$,'after',$after$ IF document->>'family'<>'lng' AND (EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule) AND kind IN('lng_assignment','lng_product'))
 OR EXISTS(SELECT 1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'output_guard') AND parameters->>'lifecycle'='future_lng_cargo_v1')) THEN RAISE EXCEPTION 'side_schedule_program'; END IF;
 IF document IS DISTINCT FROM trading.side_program_document(catalog,schedule)$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('trading.side_program_document(uuid,uuid)','591f4c029fbbcd3bb846044c27c51b307add3f4703a14549db724202de755648',
 jsonb_build_array(
 jsonb_build_object('before',$before$WHEN 'crude_assignment' THEN b.crude_assignment_code WHEN 'crude_grade'$before$,'after',$after$WHEN 'lng_assignment' THEN b.lng_assignment_code WHEN 'lng_product' THEN b.lng_product_code WHEN 'crude_assignment' THEN b.crude_assignment_code WHEN 'crude_grade'$after$,'count',1),
 jsonb_build_object('before',$before$'gas_assignment','crude_assignment')$before$,'after',$after$'gas_assignment','crude_assignment','lng_assignment')$after$,'count',1),
 jsonb_build_object('before',$before$WHEN 'crude_assignment' THEN b.crude_assignment_code END$before$,'after',$after$WHEN 'crude_assignment' THEN b.crude_assignment_code WHEN 'lng_assignment' THEN b.lng_assignment_code END$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,integer,integer)','9138856b5a6b5bad0f3658e1f7b4fd7eb2e0e05a4998f6adcc708b13e6ad00d0',
 jsonb_build_array(
 jsonb_build_object('before',$before$crude_product_code text)$before$,'after',$after$crude_product_code text, lng_assignment_code text, lng_product_code text)$after$,'count',1),
 jsonb_build_object('before',$before$r.crude_grade_code::text,r.crude_product_code::text$before$,'after',$after$r.crude_grade_code::text,r.crude_product_code::text,r.lng_assignment_code::text,r.lng_product_code::text$after$,'count',1)
 ),true);

ALTER FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) TO openetrm_runtime,openetrm_risk_worker;

SELECT pg_temp.lng_upgrade_function('security.publish_admitted_side_schedules(uuid,uuid,uuid)','27d9ae7090eabc2c6992aaee6d15bfef9ca95d8768b900ea19f826d5f0b0af70',
 jsonb_build_array(
 jsonb_build_object('before',$before$UNION ALL SELECT r.publication_order,r.effective_at FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)$before$,'after',$after$UNION ALL SELECT r.publication_order,r.effective_at FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.lng_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)$after$,'count',1),
 jsonb_build_object('before',$before$candidate.crude_side_id=NULL;$before$,'after',$after$candidate.crude_side_id=NULL;candidate.lng_side_id=NULL;
  SELECT side_id INTO candidate.lng_side_id FROM trading.lng_memberships WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,item.slot);$after$,'count',1),
 jsonb_build_object('before',$before$candidate.gas_side_id IS NULL AND candidate.crude_side_id IS NULL THEN$before$,'after',$after$candidate.gas_side_id IS NULL AND candidate.crude_side_id IS NULL AND candidate.lng_side_id IS NULL THEN$after$,'count',1),
 jsonb_build_object('before',$before$candidate.gas_side_id,candidate.crude_side_id)$before$,'after',$after$candidate.gas_side_id,candidate.crude_side_id,candidate.lng_side_id)$after$,'count',1),
 jsonb_build_object('before',$before$WHEN candidate.crude_side_id IS NOT NULL THEN$before$,'after',$after$WHEN candidate.lng_side_id IS NOT NULL THEN CASE item.slot WHEN 0 THEN 'lng' ELSE 'fixed' END
    WHEN candidate.crude_side_id IS NOT NULL THEN$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.publish_side_schedules(uuid,uuid,uuid)','db5c25300c5ff6f5e90785d3e2708fb02dcbfc32767483f3aaae9ff46ee02c48',
 jsonb_build_array(
 jsonb_build_object('before',$before$  UNION ALL SELECT t.request FROM trading.crude_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)$before$,'after',$after$  UNION ALL SELECT t.request FROM trading.crude_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)
  UNION ALL SELECT t.request FROM trading.lng_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.publish_operation_side_schedules(uuid,uuid)','eb467f4c6f77c1595c1a6a42895b1042d3e2f91e7a0a4a4179f7a2a5f46182c6',
 jsonb_build_array(
 jsonb_build_object('before',$before$  UNION ALL SELECT instrument_id FROM trading.crude_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)$before$,'after',$after$  UNION ALL SELECT instrument_id FROM trading.crude_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)
  UNION ALL SELECT instrument_id FROM trading.lng_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.require_side_schedule_read(uuid,uuid,uuid,uuid,timestamp with time zone)','12c7126b96d66e1063f02ebc49558c5c98ca9aa4b70d59d2581899dd1c27923a',
 jsonb_build_array(
 jsonb_build_object('before',$before$   EXISTS(SELECT 1 FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR$before$,'after',$after$   EXISTS(SELECT 1 FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR
   EXISTS(SELECT 1 FROM trading.lng_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR$after$,'count',1),
 jsonb_build_object('before',$before$   EXISTS(SELECT 1 FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR$before$,'after',$after$   EXISTS(SELECT 1 FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR
   EXISTS(SELECT 1 FROM trading.lng_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('security.read_instrument_side_schedule_headers(uuid,uuid,uuid,timestamp with time zone)','9bea7f198a42aad5ba5e56a4f10bc3f0cbb43dad4ed41ddfd3f27e39c37cf376',
 jsonb_build_array(
 jsonb_build_object('before',$before$ IF expected=0 THEN SELECT count(*) INTO expected FROM trading.crude_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);END IF;$before$,'after',$after$ IF expected=0 THEN SELECT count(*) INTO expected FROM trading.crude_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);END IF;
 IF expected=0 THEN SELECT count(*) INTO expected FROM trading.lng_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);END IF;$after$,'count',1)
 ),false);

SELECT pg_temp.lng_upgrade_function('trading.require_instrument_side_schedules()','b0aa591c5ac0154b25a1e71c630f3c796a2647628e6b1616cf6a0c7aa37a8ef8',
 jsonb_build_array(
 jsonb_build_object('before',$before$ IF TG_TABLE_NAME='crude_trade_revisions' THEN$before$,'after',$after$ IF TG_TABLE_NAME='lng_trade_revisions' THEN
  SELECT count(*) INTO expected FROM trading.lng_memberships WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSIF TG_TABLE_NAME='crude_trade_revisions' THEN$after$,'count',1)
 ),false);

CREATE CONSTRAINT TRIGGER retained_side_schedules AFTER INSERT ON trading.lng_trade_revisions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_instrument_side_schedules();

COMMIT;
