-- Fixed contractual gas delivery with retained independent delivery and cash programs.
BEGIN;
-- Physical hierarchy is reference data, independent of market delivery contracts.
CREATE TABLE ref_data.pipeline_zones (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,pipeline_code,zone_code),UNIQUE(catalog_id,revision_id,zone_code),
 FOREIGN KEY(catalog_id,revision_id,pipeline_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,zone_code) REFERENCES ref_data.locations(catalog_id,revision_id,code)
);
CREATE TABLE ref_data.pipeline_delivery_points (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,pipeline_code,zone_code,point_code),UNIQUE(catalog_id,revision_id,point_code),
 FOREIGN KEY(catalog_id,revision_id,pipeline_code,zone_code) REFERENCES ref_data.pipeline_zones(catalog_id,revision_id,pipeline_code,zone_code),
 FOREIGN KEY(catalog_id,revision_id,point_code) REFERENCES ref_data.locations(catalog_id,revision_id,code)
);
CREATE TABLE ref_data.gas_delivery_conventions (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 clock_code ref_data.code NOT NULL,day_start_minute integer NOT NULL CHECK(day_start_minute BETWEEN 0 AND 1439),
 PRIMARY KEY(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(catalog_id,revision_id,pipeline_code,zone_code,point_code) REFERENCES ref_data.pipeline_delivery_points(catalog_id,revision_id,pipeline_code,zone_code,point_code),
 FOREIGN KEY(catalog_id,revision_id,product_code,point_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(catalog_id,revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code)
);
CREATE TABLE ref_data.gas_meter_assignments (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 identifier text NOT NULL CHECK(length(identifier) BETWEEN 1 AND 128 AND identifier=btrim(identifier)),
 quantity_code ref_data.code NOT NULL,valid_from date NOT NULL,valid_to date NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),CHECK(isfinite(valid_from) AND isfinite(valid_to) AND valid_from<valid_to),
 FOREIGN KEY(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code) REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(catalog_id,revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code)
);
-- The bounded direct-meter profile forbids overlapping point assignments and
-- overlapping reuse of a pipeline-scoped external identifier. This also serializes
-- concurrent authors of the same reference revision before checking eligibility.
CREATE FUNCTION ref_data.require_gas_meter_assignment() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF current_setting('transaction_isolation')<>'read committed' THEN RAISE EXCEPTION 'gas_reference_isolation' USING ERRCODE='25000';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(NEW.catalog_id::text||NEW.revision_id::text,319));
 IF EXISTS(SELECT 1 FROM ref_data.gas_meter_assignments m WHERE(m.catalog_id,m.revision_id,m.pipeline_code)=(NEW.catalog_id,NEW.revision_id,NEW.pipeline_code)
  AND daterange(m.valid_from,m.valid_to,'[)')&&daterange(NEW.valid_from,NEW.valid_to,'[)')
  AND ((m.point_code,m.product_code)=(NEW.point_code,NEW.product_code) OR m.identifier=NEW.identifier)) THEN RAISE EXCEPTION 'gas_meter_overlap';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q WHERE(q.catalog_id,q.revision_id,q.code,q.commodity_code)=(NEW.catalog_id,NEW.revision_id,NEW.quantity_code,NEW.product_code)) THEN RAISE EXCEPTION 'gas_meter_unit';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_assignment BEFORE INSERT ON ref_data.gas_meter_assignments FOR EACH ROW EXECUTE FUNCTION ref_data.require_gas_meter_assignment();
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['pipeline_zones','pipeline_delivery_points','gas_delivery_conventions','gas_meter_assignments'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON ref_data.%I FOR EACH ROW EXECUTE FUNCTION ref_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE ref_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE ref_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON ref_data.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY version_scope ON ref_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(catalog_id,revision_id))',t);
  EXECUTE format('CREATE POLICY current_authority ON ref_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_access(catalog_id,''ReadMarketData''))',t);
  EXECUTE format('CREATE POLICY owner_access ON ref_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT ON ref_data.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON ref_data.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;
-- Label-only revisions retain the same physical relationships and prove their equivalence.
CREATE OR REPLACE FUNCTION security.reference_display_tables() RETURNS text[]
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT ARRAY['dimensions','currencies','units','commodities','commodity_units','measurement_bases','quantity_specifications',
 'unit_conversions','clock_rules','calendars','calendar_days','location_types','locations','commodity_locations','pipeline_zones','pipeline_delivery_points','gas_delivery_conventions','gas_meter_assignments','exchanges',
 'clearing_houses','exchange_clearing_houses','interest_rate_definitions','price_index_definitions','sources','delivery_profiles',
 'value_specifications','quote_specifications','settlement_targets','futures_products','futures_series',
 'futures_milestones','futures_product_terms','futures_lot_resolutions','futures_basis_settlements','futures_execution_policies','product_evidence'];
$$;
-- A group shares a curve and valuation-only price adder across explicitly
-- assigned points. No implicit pipeline/zone fallback or payable fee is created.
CREATE TABLE market_data.gas_valuation_groups (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,pipeline_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 curve_code ref_data.code NOT NULL,adjustment numeric NOT NULL CHECK(abs(adjustment)<1e12 AND scale(adjustment)<=18),
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,product_code,curve_code,adjustment),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,pipeline_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
CREATE TABLE market_data.gas_usage_tariffs (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,pipeline_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 quote_code ref_data.code NOT NULL,rate numeric NOT NULL CHECK(rate>=0 AND rate<1e12 AND scale(rate)<=18),
 valid_from date NOT NULL,valid_to date NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),CHECK(isfinite(valid_from) AND isfinite(valid_to) AND valid_from<valid_to),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,pipeline_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_valuation_groups','gas_usage_tariffs'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY version_scope ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.market_fact_visible(catalog_id,revision_id,NULL) AND security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY current_authority ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData'') AND security.reference_access(reference_catalog_id,''ReadMarketData''))',t);
  EXECUTE format('CREATE POLICY owner_access ON market_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner',t);
 END LOOP;
END$$;
CREATE TABLE market_data.gas_valuation_assignments (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,curve_code ref_data.code NOT NULL,
 group_code ref_data.code NOT NULL,group_adjustment numeric NOT NULL,
 location_adjustment numeric NOT NULL CHECK(abs(location_adjustment)<1e12 AND scale(location_adjustment)<=18),
 adjustment numeric GENERATED ALWAYS AS(group_adjustment+location_adjustment) STORED CHECK(abs(adjustment)<1e12 AND scale(adjustment)<=18),
 PRIMARY KEY(catalog_id,revision_id,code),UNIQUE(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code),
 UNIQUE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code) REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code) REFERENCES ref_data.pipeline_delivery_points(catalog_id,revision_id,pipeline_code,zone_code,point_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,point_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,pipeline_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,point_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code) REFERENCES ref_data.commodities(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,group_code,reference_catalog_id,reference_revision_id,pipeline_code,product_code,curve_code,group_adjustment)
 REFERENCES market_data.gas_valuation_groups(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,product_code,curve_code,adjustment)
);
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.gas_valuation_assignments FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition();
ALTER TABLE market_data.gas_valuation_assignments ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.gas_valuation_assignments FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.gas_valuation_assignments FOR SELECT TO openetrm_runtime USING(security.market_fact_visible(catalog_id,revision_id,NULL) AND security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY owner_access ON market_data.gas_valuation_assignments TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT ON market_data.gas_valuation_assignments TO openetrm_runtime,openetrm_security_owner;

CREATE TABLE trading.gas_instruments (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,assignment_code ref_data.code NOT NULL,
 delivery_from date NOT NULL,delivery_to date NOT NULL,quantity_basis text NOT NULL CHECK(quantity_basis IN('per_operating_day','period_total_equal_days')),
 fixed_price numeric NOT NULL CHECK(abs(fixed_price)<1e12 AND scale(fixed_price)<=18),usage_fee numeric CHECK(abs(usage_fee)<1e12 AND scale(usage_fee)<=18),
 payment_at timestamptz NOT NULL,publication_order integer NOT NULL,effective_at timestamptz NOT NULL,usage_tariff_code ref_data.code,usage_fee_direction integer CHECK(usage_fee_direction IN(-1,1)),
 PRIMARY KEY(catalog_id,instrument_id),FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,assignment_code) REFERENCES market_data.gas_valuation_assignments(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,usage_tariff_code) REFERENCES market_data.gas_usage_tariffs(catalog_id,revision_id,code),
 CHECK((usage_fee IS NULL)=(usage_fee_direction IS NULL)),
 CHECK(delivery_from<delivery_to AND delivery_to-delivery_from<=31),CHECK(isfinite(payment_at)),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_memberships (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL CHECK(slot BETWEEN 0 AND 2),side_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,slot),UNIQUE(catalog_id,instrument_id,slot,side_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.sides(catalog_id,id)
);
CREATE TABLE trading.gas_trade_revisions (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision=1),instrument_id uuid NOT NULL,book_id uuid NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL,quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),
 direction integer NOT NULL CHECK(direction IN(-1,1)),legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,publication_order integer NOT NULL,effective_at timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),UNIQUE(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_instruments','gas_trade_revisions'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades'') AND security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;
ALTER TABLE trading.gas_memberships ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_memberships FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON trading.gas_memberships FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(gas_memberships.catalog_id,gas_memberships.instrument_id)));
CREATE POLICY owner_access ON trading.gas_memberships TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT ON trading.gas_memberships TO openetrm_runtime;
GRANT SELECT,INSERT ON trading.gas_memberships TO openetrm_security_owner;

ALTER TABLE trading.side_schedule_bindings ADD COLUMN gas_assignment_code ref_data.code,ADD COLUMN gas_product_code ref_data.code;
ALTER TABLE trading.side_schedule_bindings ADD FOREIGN KEY(source_catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id,gas_assignment_code)
 REFERENCES market_data.gas_valuation_assignments(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code);
ALTER TABLE trading.side_schedule_bindings ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,gas_product_code) REFERENCES ref_data.commodities(catalog_id,revision_id,code);
ALTER TABLE trading.instrument_side_schedules ADD COLUMN gas_side_id uuid;
ALTER TABLE trading.instrument_side_schedules ADD FOREIGN KEY(catalog_id,instrument_id,slot,gas_side_id) REFERENCES trading.gas_memberships(catalog_id,instrument_id,slot,side_id);
DO $$DECLARE r record;BEGIN
 FOR r IN SELECT conname FROM pg_constraint WHERE conrelid='trading.instrument_side_schedules'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%swap_fixed_side_id%' LOOP
  EXECUTE format('ALTER TABLE trading.instrument_side_schedules DROP CONSTRAINT %I',r.conname);
 END LOOP;
 FOR r IN SELECT conname FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND contype='c' AND pg_get_constraintdef(oid) NOT LIKE '%published_index_observations_v1%' LOOP
  EXECUTE format('ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT %I',r.conname);
 END LOOP;
END$$;
ALTER TABLE trading.instrument_side_schedules ADD CHECK(num_nonnulls(swap_fixed_side_id,swap_floating_side_id,swap_whole_term_side_id,ppa_side_id,gas_side_id)=1),
 ADD CHECK(side_id=coalesce(swap_fixed_side_id,swap_floating_side_id,swap_whole_term_side_id,ppa_side_id,gas_side_id));

ALTER TABLE trading.side_schedule_bindings ADD CHECK(kind IN('contract_price','index_fixing','settlement_source','curve_interval','custom_output','quantity_source',
 'asset','sibling_quantity','location','index','quote','gas_assignment','gas_product'));
ALTER TABLE trading.side_schedule_bindings ADD CHECK(CASE kind
 WHEN 'contract_price' THEN num_nonnulls(source_catalog_id,definition_revision_id,curve_code,contract_id)=4
 WHEN 'index_fixing' THEN num_nonnulls(reference_catalog_id,reference_revision_id,source_code,source_catalog_id,definition_revision_id,series_id,fixing_index_code,fixing_from,fixing_to,quote_code,coordinate)=11
 WHEN 'settlement_source' THEN num_nonnulls(reference_catalog_id,reference_revision_id,source_code,source_catalog_id,definition_revision_id,series_id,coordinate)=7 AND coordinate IN('DailySettlement','TraderMark')
 WHEN 'curve_interval' THEN num_nonnulls(source_catalog_id,definition_revision_id)=2 AND num_nonnulls(curve_code,shaped_curve_code)=1
 WHEN 'custom_output' THEN num_nonnulls(custom_catalog_id,custom_curve_id,custom_version,output_node,source_catalog_id,definition_revision_id,curve_code,contract_id,coordinate)=9
  AND coordinate=output_node AND(position IS NULL OR position>=0)
 WHEN 'quantity_source' THEN num_nonnulls(source_catalog_id,definition_revision_id,quantity_source_code)=3
 WHEN 'asset' THEN num_nonnulls(source_catalog_id,definition_revision_id,asset_code)=3
 WHEN 'sibling_quantity' THEN num_nonnulls(reference_catalog_id,reference_revision_id,quantity_code,coordinate,position)=5 AND position BETWEEN 0 AND 7
 WHEN 'gas_assignment' THEN num_nonnulls(reference_catalog_id,reference_revision_id,gas_assignment_code,source_catalog_id,definition_revision_id,coordinate)=6
 WHEN 'gas_product' THEN num_nonnulls(reference_catalog_id,reference_revision_id,gas_product_code)=3
 WHEN 'location' THEN num_nonnulls(reference_catalog_id,reference_revision_id,location_code)=3 AND ((port='point' AND coordinate IS NULL) OR (port='location' AND coordinate IS NOT NULL))
 WHEN 'index' THEN num_nonnulls(reference_catalog_id,reference_revision_id,index_code)=3
 WHEN 'quote' THEN num_nonnulls(reference_catalog_id,reference_revision_id,quote_code)=3 ELSE false END);
ALTER TABLE trading.side_schedule_bindings ADD CHECK(num_nonnulls(reference_catalog_id,reference_revision_id,index_code,quantity_code,location_code,quote_code,
 source_catalog_id,definition_revision_id,curve_code,contract_id,shaped_curve_code,quantity_source_code,asset_code,series_id,
 custom_catalog_id,custom_curve_id,custom_version,output_node,position,coordinate,source_code,fixing_index_code,fixing_from,fixing_to,gas_assignment_code,gas_product_code)=
 CASE kind WHEN 'contract_price' THEN 4 WHEN 'index_fixing' THEN 11 WHEN 'settlement_source' THEN 7 WHEN 'curve_interval' THEN 3
  WHEN 'custom_output' THEN 9+num_nonnulls(position) WHEN 'quantity_source' THEN 3+num_nonnulls(coordinate)
  WHEN 'asset' THEN 3+num_nonnulls(coordinate) WHEN 'sibling_quantity' THEN 5 WHEN 'gas_assignment' THEN 6 WHEN 'gas_product' THEN 3 WHEN 'location' THEN 3+num_nonnulls(coordinate) WHEN 'index' THEN 3 WHEN 'quote' THEN 3 END);
ALTER TABLE trading.side_schedule_bindings ADD CHECK(kind<>'index_fixing' OR coordinate='published_index_observations_v1');


CREATE OR REPLACE FUNCTION security.begin_side_schedule(catalog uuid,request uuid,slot_number integer,command jsonb,
 permission_name text,trade uuid,expected integer,header jsonb) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE actor uuid;prior trading.side_schedule_requests;item trading.side_schedules;parent trading.sides;
 command_digest bytea;economic_digest bytea;found_side uuid;found_schedule uuid;
BEGIN
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000'::uuid OR slot_number NOT BETWEEN 0 AND 7
  OR jsonb_typeof(command) IS DISTINCT FROM 'object' OR octet_length(command::text)>65536
  OR permission_name NOT IN('BookTrade','AmendTrade') OR(trade IS NULL)<>(expected IS NULL)
 THEN RAISE EXCEPTION 'side_schedule_request';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission_name)
 THEN RAISE EXCEPTION 'No current schedule write authority' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts
  WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND market_context_id IS NULL;
 PERFORM pg_advisory_xact_lock(730849105,hashtext(request::text));
 command_digest=sha256(convert_to(command::text,'UTF8'));
 SELECT * INTO prior FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,request);
 IF FOUND THEN
  IF(prior.actor_id,prior.command_hash,prior.permission,prior.trade_id,prior.expected_revision) IS DISTINCT FROM(actor,command_digest,permission_name,trade,expected)
  THEN RAISE EXCEPTION 'side_schedule_request_conflict';END IF;
 ELSE
  INSERT INTO trading.side_schedule_requests(catalog_id,request_id,actor_id,command_hash,permission,trade_id,expected_revision)
   VALUES(catalog,request,actor,command_digest,permission_name,trade,expected);
 END IF;
 IF jsonb_typeof(header) IS DISTINCT FROM 'object' OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(header) k WHERE k NOT IN('family','definitionRevisionId','referenceCatalogId','referenceRevisionId','economicIdentity','pricingBindingIdentity','schemaVersion','generatorVersion','manifest','rowCount','edgeCount','bindingCount','rootCount','portCount','gridCount')) OR
  header->>'family' IS NULL OR header->>'economicIdentity' IS NULL OR header->>'pricingBindingIdentity' IS NULL OR
  header->>'schemaVersion' IS DISTINCT FROM 'side_schedule_blocks_v2' OR header->>'generatorVersion' IS DISTINCT FROM 'whole_term_side_blocks_v2' OR
  header->>'family' NOT IN('fixed','floating','power','certificate','capacity','gas') OR header->>'manifest' !~ '^[0-9a-f]{64}$' OR
  NOT security.reference_access((header->>'referenceCatalogId')::uuid,'ReadMarketData')
 THEN RAISE EXCEPTION 'side_schedule_header';END IF;
 economic_digest=sha256(convert_to(jsonb_build_array(header->>'family',header->>'economicIdentity',header->>'pricingBindingIdentity')::text,'UTF8'));
 PERFORM pg_advisory_xact_lock(730849106,hashtext(encode(economic_digest,'hex')));
 SELECT * INTO parent FROM trading.sides WHERE catalog_id=catalog AND family=header->>'family' AND identity_hash=economic_digest;
 IF FOUND THEN
  IF(parent.economic_identity,parent.pricing_binding_identity,parent.reference_catalog_id,parent.reference_revision_id) IS DISTINCT FROM
   (header->>'economicIdentity',header->>'pricingBindingIdentity',(header->>'referenceCatalogId')::uuid,(header->>'referenceRevisionId')::uuid)
  THEN RAISE EXCEPTION 'side_schedule_identity_conflict';END IF;
  found_side=parent.id;
 ELSE
  found_side=gen_random_uuid();
  INSERT INTO trading.sides VALUES(catalog,found_side,header->>'family',(header->>'definitionRevisionId')::uuid,
   (header->>'referenceCatalogId')::uuid,(header->>'referenceRevisionId')::uuid,header->>'economicIdentity',header->>'pricingBindingIdentity',economic_digest);
 END IF;
 SELECT * INTO item FROM trading.side_schedules WHERE catalog_id=catalog AND side_id=found_side AND
  (schema_version,generator_version)=(header->>'schemaVersion',header->>'generatorVersion');
 IF FOUND THEN
  IF item.content_hash<>decode(header->>'manifest','hex') THEN RAISE EXCEPTION 'side_schedule_generator_conflict';END IF;
  IF(item.row_count,item.edge_count,item.binding_count,item.root_count,item.port_count,item.grid_count) IS DISTINCT FROM
   ((header->>'rowCount')::integer,(header->>'edgeCount')::integer,(header->>'bindingCount')::integer,(header->>'rootCount')::integer,(header->>'portCount')::integer,(header->>'gridCount')::integer)
  THEN RAISE EXCEPTION 'side_schedule_manifest_conflict';END IF;
  found_schedule=item.id;
 ELSE
  found_schedule=gen_random_uuid();
  INSERT INTO trading.side_schedules VALUES(catalog,found_schedule,found_side,request,header->>'schemaVersion',header->>'generatorVersion',decode(header->>'manifest','hex'),
   (header->>'rowCount')::integer,(header->>'edgeCount')::integer,(header->>'bindingCount')::integer,(header->>'rootCount')::integer,(header->>'portCount')::integer,(header->>'gridCount')::integer);
 END IF;
 SELECT schedule_id INTO found_side FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,request,slot_number);
 IF FOUND AND found_side<>found_schedule THEN RAISE EXCEPTION 'side_schedule_request_conflict';END IF;
 IF NOT FOUND THEN
  IF EXISTS(SELECT 1 FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=(catalog,request)) THEN RAISE EXCEPTION 'side_schedule_immutable';END IF;
  INSERT INTO trading.side_schedule_request_items VALUES(catalog,request,slot_number,found_schedule);
 END IF;
 IF NOT security.trading_access(catalog,permission_name) THEN RAISE EXCEPTION 'Schedule write authority expired' USING ERRCODE='42501';END IF;
 RETURN found_schedule;
END$$;

CREATE OR REPLACE FUNCTION security.append_side_schedule_aux(catalog uuid,request uuid,schedule uuid,part text,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off SET jit=off AS $$
DECLARE port_document jsonb;binding_batch trading.side_schedule_bindings[];root_batch trading.side_schedule_roots[];authority uuid;
 port_value trading.side_schedule_ports;prior_port trading.side_schedule_ports;written bigint=-1;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 PERFORM 1 FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>2048 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 IF part='bindings' THEN
  FOR authority IN SELECT DISTINCT (d->>'reference_catalog_id')::uuid FROM jsonb_array_elements(documents) d WHERE d->>'reference_catalog_id' IS NOT NULL LOOP
   IF NOT security.reference_access(authority,'ReadMarketData') THEN RAISE EXCEPTION 'No current binding authority' USING ERRCODE='42501';END IF;
   INSERT INTO trading.side_schedule_authorities SELECT catalog,schedule,'reference',authority,NULL,authority
    WHERE NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id,a.kind,a.authority_catalog_id)=(catalog,schedule,'reference',authority));
  END LOOP;
  FOR authority IN SELECT DISTINCT refs.authority_catalog FROM jsonb_array_elements(documents) d
   CROSS JOIN LATERAL unnest(ARRAY[(d->>'source_catalog_id')::uuid,(d->>'custom_catalog_id')::uuid]) AS refs(authority_catalog) WHERE refs.authority_catalog IS NOT NULL LOOP
   IF NOT security.market_access(authority,'ReadMarketData') THEN RAISE EXCEPTION 'No current binding authority' USING ERRCODE='42501';END IF;
   INSERT INTO trading.side_schedule_authorities SELECT catalog,schedule,'market',authority,authority,NULL
    WHERE NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id,a.kind,a.authority_catalog_id)=(catalog,schedule,'market',authority));
  END LOOP;
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k WHERE k NOT IN('row_ordinal','port','kind','reference_catalog_id','reference_revision_id','index_code','quantity_code','location_code','quote_code',
    'source_catalog_id','definition_revision_id','curve_code','contract_id','shaped_curve_code','quantity_source_code','asset_code','series_id','custom_catalog_id','custom_curve_id','custom_version','output_node','position','coordinate','source_code','fixing_index_code','fixing_from','fixing_to','gas_assignment_code','gas_product_code','sequence_ordinal'))
  THEN RAISE EXCEPTION 'side_schedule_binding';END IF;
  SELECT array_agg(jsonb_populate_record(NULL::trading.side_schedule_bindings,document||jsonb_build_object('catalog_id',catalog,'schedule_id',schedule)))
   INTO binding_batch FROM jsonb_array_elements(documents) document;
  IF EXISTS(SELECT 1 FROM unnest(binding_batch) b WHERE b.kind='custom_output' AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v
    WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state)=(b.custom_catalog_id,b.custom_curve_id,b.custom_version,b.output_node,'published'))
   ) THEN RAISE EXCEPTION 'side_schedule_custom_output';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
   INSERT INTO trading.side_schedule_bindings SELECT b.* FROM unnest(binding_batch) b ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS written=ROW_COUNT;
  END IF;
  IF written<>coalesce(cardinality(binding_batch),0) AND EXISTS(SELECT 1 FROM unnest(binding_batch) b LEFT JOIN trading.side_schedule_bindings p
   ON(p.catalog_id,p.schedule_id,p.row_ordinal,p.port)=(catalog,schedule,b.row_ordinal,b.port)
   WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM b)
  THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 ELSIF part='roots' THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k
   WHERE k NOT IN('name','row','measure','sequence')) THEN RAISE EXCEPTION 'side_schedule_root';END IF;
  SELECT array_agg(ROW(catalog,schedule,document->>'name',(document->>'row')::integer,document->>'measure',(document->>'sequence')::integer)::trading.side_schedule_roots)
   INTO root_batch FROM jsonb_array_elements(documents) document;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
   INSERT INTO trading.side_schedule_roots SELECT r.* FROM unnest(root_batch) r ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS written=ROW_COUNT;
  END IF;
  IF written<>coalesce(cardinality(root_batch),0) AND EXISTS(SELECT 1 FROM unnest(root_batch) r LEFT JOIN trading.side_schedule_roots p
   ON(p.catalog_id,p.schedule_id,p.name)=(catalog,schedule,r.name)
   WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM r)
  THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 ELSIF part='ports' THEN
  FOR port_document IN SELECT value FROM jsonb_array_elements(documents) LOOP
   IF EXISTS(SELECT 1 FROM jsonb_object_keys(port_document) k WHERE k NOT IN('name','slot','kind','unit','shapeIdentity','sequence')) THEN RAISE EXCEPTION 'side_schedule_port';END IF;
   port_value=ROW(catalog,schedule,port_document->>'name',port_document#>>'{slot,role}',(port_document#>>'{slot,ordinal}')::integer,port_document->>'kind',
    (port_document#>>'{unit,revision,catalogId}')::uuid,(port_document#>>'{unit,revision,revisionId}')::uuid,port_document#>>'{unit,code}',port_document->>'shapeIdentity',(port_document->>'sequence')::integer);
   SELECT * INTO prior_port FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id,name)=(catalog,schedule,port_value.name);
   IF FOUND THEN IF prior_port IS DISTINCT FROM port_value THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
   ELSE INSERT INTO trading.side_schedule_ports SELECT port_value.*;END IF;
  END LOOP;
 ELSE RAISE EXCEPTION 'side_schedule_part';END IF;
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE OR REPLACE FUNCTION security.seal_side_schedule(catalog uuid,request uuid,schedule uuid,manifest text) RETURNS void
-- Fresh immutable batches have no per-schedule planner statistics. Nested
-- self-joins otherwise repeatedly scan the whole graph while estimating one
-- row; this validation pass requires hash/merge joins over its exact keys.
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off SET jit=off AS $$
DECLARE h trading.side_schedules;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 SELECT * INTO STRICT h FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF manifest !~ '^[0-9a-f]{64}$' OR decode(manifest,'hex')<>h.content_hash THEN RAISE EXCEPTION 'side_schedule_manifest_conflict';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RETURN;END IF;
 IF (SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.row_count OR
  (SELECT count(*) FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.edge_count OR
  (SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.binding_count OR
  (SELECT count(*) FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.root_count OR
  (SELECT count(*) FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.port_count OR
  (SELECT count(*) FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.grid_count OR
  (SELECT min(ordinal)<>0 OR max(ordinal)<>h.grid_count-1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  (SELECT min(ordinal)<>0 OR max(ordinal)<>h.row_count-1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))
 THEN RAISE EXCEPTION 'side_schedule_incomplete';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule)
  GROUP BY local_key HAVING count(*)<>1) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)
   GROUP BY local_key HAVING count(*)<>1) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule)
  GROUP BY target,port HAVING min(ordinal)<>0 OR max(ordinal)<>count(*)-1) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_roots o JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal)
   WHERE(o.catalog_id,o.schedule_id)=(catalog,schedule) AND
    (o.measure='schedule_quantity' AND r.output_kind<>'quantity' OR o.measure IN('contractual_amount','contribution_pv','pv','physical_value') AND r.output_kind<>'money')) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b LEFT JOIN trading.side_schedule_ports p
   ON(p.catalog_id,p.schedule_id,p.name)=(b.catalog_id,b.schedule_id,b.coordinate)
   WHERE(b.catalog_id,b.schedule_id,b.kind)=(catalog,schedule,'sibling_quantity') AND p.name IS NULL)
 THEN RAISE EXCEPTION 'side_schedule_topology';END IF;
 IF EXISTS(WITH inputs AS MATERIALIZED(SELECT target,count(*) n FROM trading.side_schedule_edges
   WHERE(catalog_id,schedule_id)=(catalog,schedule) GROUP BY target)
  SELECT 1 FROM trading.side_schedule_rows r LEFT JOIN inputs ON inputs.target=r.ordinal
  WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND CASE
   WHEN r.operation IN('constant_price','price_lookup','contract_observation_price','quantity_input','quantity_literal','profile_quantity','asset_capacity','discount') THEN coalesce(inputs.n,0)<>0
   WHEN r.operation IN('quantity_scale','quantity_transform','price_scale','price_application','sum_cells','quantity_sum_cells') THEN coalesce(inputs.n,0)<>1
   WHEN r.operation IN('quantity_normalize','amount','pv','output_guard','price_add') THEN coalesce(inputs.n,0)<>2
   WHEN r.operation IN('quantity_add','weighted_price','sum') THEN coalesce(inputs.n,0)<1 ELSE true END)
 THEN RAISE EXCEPTION 'side_schedule_arity';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND(r.operation='profile_quantity' AND r.grid_ordinal IS NULL OR r.operation='discount' AND r.value_at IS NULL)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_edges e
   JOIN trading.side_schedule_rows target ON(target.catalog_id,target.schedule_id,target.ordinal)=(e.catalog_id,e.schedule_id,e.target)
   JOIN trading.side_schedule_rows source ON(source.catalog_id,source.schedule_id,source.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   LEFT JOIN trading.side_schedule_grids target_grid ON(target_grid.catalog_id,target_grid.schedule_id,target_grid.ordinal)=(target.catalog_id,target.schedule_id,target.grid_ordinal)
   WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule) AND CASE
    WHEN target.operation='sum_cells' THEN source.grid_ordinal IS NULL OR target.grid_ordinal IS NOT NULL
    WHEN target.operation='quantity_sum_cells' THEN source.grid_ordinal IS NULL OR target.grid_ordinal IS NULL OR cardinality(target_grid.selection)<>1
    ELSE source.grid_ordinal IS NOT NULL AND source.grid_ordinal IS DISTINCT FROM target.grid_ordinal END)
 THEN RAISE EXCEPTION 'side_schedule_grid';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name,measure)=(catalog,schedule,'pv','pv')) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows target
   ON(target.catalog_id,target.schedule_id,target.ordinal)=(e.catalog_id,e.schedule_id,e.target)
   JOIN trading.side_schedule_rows source ON(source.catalog_id,source.schedule_id,source.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule) AND(
    e.port<>ALL(CASE target.operation
     WHEN 'sum_cells' THEN ARRAY['input'] WHEN 'quantity_sum_cells' THEN ARRAY['input']
     WHEN 'quantity_normalize' THEN ARRAY['input','basis'] WHEN 'amount' THEN ARRAY['quantity','price']
     WHEN 'pv' THEN ARRAY['amount','discount'] WHEN 'output_guard' THEN ARRAY['amount','discounted']
     WHEN 'quantity_scale' THEN ARRAY['input'] WHEN 'quantity_transform' THEN ARRAY['input']
     WHEN 'price_scale' THEN ARRAY['input'] WHEN 'price_application' THEN ARRAY['input']
     WHEN 'quantity_add' THEN ARRAY['input'] WHEN 'price_add' THEN ARRAY['input'] WHEN 'weighted_price' THEN ARRAY['input']
     WHEN 'sum' THEN ARRAY['input'] ELSE ARRAY[]::text[] END) OR
    e.ordinal<>0 AND target.operation NOT IN('quantity_add','price_add','weighted_price','sum') OR
    source.output_kind<>CASE WHEN target.operation IN('quantity_normalize','quantity_add','quantity_scale','quantity_transform','quantity_sum_cells') THEN 'quantity'
     WHEN target.operation IN('price_scale','price_application','price_add','weighted_price') THEN 'price'
     WHEN target.operation='amount' THEN e.port WHEN target.operation='pv' AND e.port='discount' THEN 'factor' ELSE 'money' END OR
    source.output_kind IN('price','money') AND(source.currency_catalog_id,source.currency_revision_id,source.currency_code)
     IS DISTINCT FROM(target.currency_catalog_id,target.currency_revision_id,target.currency_code) OR
    target.operation IN('quantity_add','quantity_scale','quantity_sum_cells','price_scale','price_application','price_add','weighted_price') AND
     (source.unit_catalog_id,source.unit_revision_id,source.unit_code,source.quote_multiplier)
      IS DISTINCT FROM(target.unit_catalog_id,target.unit_revision_id,target.unit_code,target.quote_multiplier) OR
    target.operation='quantity_normalize' AND e.port='input' AND(source.unit_catalog_id,source.unit_revision_id,source.unit_code)
     IS DISTINCT FROM(target.unit_catalog_id,target.unit_revision_id,target.unit_code))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_rows amount
   JOIN trading.side_schedule_edges qe ON(qe.catalog_id,qe.schedule_id,qe.target,qe.port)=(amount.catalog_id,amount.schedule_id,amount.ordinal,'quantity')
   JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(qe.catalog_id,qe.schedule_id,qe.source)
   JOIN trading.side_schedule_edges pe ON(pe.catalog_id,pe.schedule_id,pe.target,pe.port)=(amount.catalog_id,amount.schedule_id,amount.ordinal,'price')
   JOIN trading.side_schedule_rows p ON(p.catalog_id,p.schedule_id,p.ordinal)=(pe.catalog_id,pe.schedule_id,pe.source)
   WHERE(amount.catalog_id,amount.schedule_id,amount.operation)=(catalog,schedule,'amount') AND
    (q.unit_catalog_id,q.unit_revision_id,q.unit_code) IS DISTINCT FROM(p.unit_catalog_id,p.unit_revision_id,p.unit_code))
 THEN RAISE EXCEPTION 'side_schedule_ports';END IF;
 IF EXISTS(WITH sources AS MATERIALIZED(SELECT row_ordinal,count(*) n,bool_or(kind='contract_price') dated,bool_or(kind='curve_interval') curve,
   bool_or(port='market' AND kind='contract_price') market_port,bool_or(port='index' AND kind='index') index_port,
   bool_or(port='quote' AND kind='quote') quote_port,bool_or(port='fixing' AND kind='index_fixing') fixing_port,bool_or(port='curve' AND kind='curve_interval') curve_port,
   bool_or(port='assignment') gas_assignment,bool_or(port='point') gas_point,bool_or(port='product') gas_product,
   bool_and(kind=CASE port WHEN 'market' THEN 'contract_price' WHEN 'index' THEN 'index' WHEN 'quote' THEN 'quote'
    WHEN 'fixing' THEN 'index_fixing' WHEN 'projection' THEN 'custom_output' WHEN 'assignment' THEN 'gas_assignment' WHEN 'point' THEN 'location' WHEN 'product' THEN 'gas_product' ELSE '' END) dated_ports,
   bool_and((port,kind) IN(('curve','curve_interval'),('index','index'))) curve_ports,
   bool_and((port,kind) IN(('market','contract_price'),('quote','quote'),('settlement','settlement_source'))) sample_ports,
   bool_and(port='location' AND kind IN('location','quantity_source','asset')) location_ports,
   bool_and((port,kind) IN(('source','quantity_source'),('quantity','sibling_quantity'))) quantity_sources,bool_and(port='asset' AND kind='asset') assets
   FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule) GROUP BY row_ordinal)
  SELECT 1 FROM trading.side_schedule_rows r LEFT JOIN sources s ON s.row_ordinal=r.ordinal
  WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND CASE r.operation
   WHEN 'contract_observation_price' THEN NOT(coalesce(s.n,0)=3 AND s.market_port AND s.quote_port AND s.sample_ports)
   WHEN 'price_lookup' THEN CASE WHEN s.dated THEN NOT(s.market_port AND s.index_port AND s.quote_port AND s.fixing_port AND s.dated_ports AND (NOT(coalesce(s.gas_assignment,false) OR coalesce(s.gas_point,false) OR coalesce(s.gas_product,false)) OR s.gas_assignment AND s.gas_point AND s.gas_product))
    WHEN s.curve THEN NOT(s.curve_port AND s.curve_ports AND CASE WHEN r.parameters->>'selection'='interval_attribute_lookup_v1'
     THEN s.n=1 ELSE s.n=2 AND s.index_port END) ELSE coalesce(s.n,0)<>1 OR NOT s.location_ports END
   WHEN 'quantity_input' THEN coalesce(s.n,0)<>1 OR NOT s.quantity_sources
   WHEN 'asset_capacity' THEN coalesce(s.n,0)<>1 OR NOT s.assets
   WHEN 'discount' THEN coalesce(s.n,0)<>0
   ELSE coalesce(s.n,0)<>0 END)
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r
   ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
   WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND(
    b.kind IN('quantity_source','asset') AND(b.coordinate IS NOT NULL)<>(r.operation='price_lookup' AND b.port='location') OR
    b.kind='index_fixing' AND((r.observation_from,r.observation_to) IS DISTINCT FROM(b.fixing_from,b.fixing_to) OR
     NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings selected
      WHERE(selected.catalog_id,selected.schedule_id,selected.row_ordinal,selected.port,selected.kind)=(catalog,schedule,b.row_ordinal,'index','index') AND
       (selected.reference_catalog_id,selected.reference_revision_id,selected.index_code)=(b.reference_catalog_id,b.reference_revision_id,b.fixing_index_code)) OR
     NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings quote
      WHERE(quote.catalog_id,quote.schedule_id,quote.row_ordinal,quote.port,quote.kind)=(catalog,schedule,b.row_ordinal,'quote','quote') AND
       (quote.reference_catalog_id,quote.reference_revision_id,quote.quote_code)=(b.reference_catalog_id,b.reference_revision_id,b.quote_code))) OR
    b.kind='custom_output' AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings market
     WHERE(market.catalog_id,market.schedule_id,market.row_ordinal,market.port,market.kind)=(catalog,schedule,b.row_ordinal,'market','contract_price') AND
      (market.source_catalog_id,market.definition_revision_id,market.curve_code,market.contract_id)=
       (b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)) OR
    b.kind='sibling_quantity' AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_ports p
     WHERE(p.catalog_id,p.schedule_id,p.name,p.slot_ordinal,p.unit_catalog_id,p.unit_revision_id,p.unit_code)=
      (catalog,schedule,b.coordinate,b.position,b.reference_catalog_id,b.reference_revision_id,b.quantity_code))))
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,measure)=(catalog,schedule,'schedule_quantity')) THEN
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id,name)=(catalog,schedule,'quantity')) OR
   EXISTS(SELECT name FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,measure)=(catalog,schedule,'schedule_quantity')
    EXCEPT SELECT root_name FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id,set_name)=(catalog,schedule,'quantity')) OR
   EXISTS(SELECT 1 FROM trading.side_schedule_output_members m JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.name)=(m.catalog_id,m.schedule_id,m.root_name)
    JOIN trading.side_schedule_rows row ON(row.catalog_id,row.schedule_id,row.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
    JOIN trading.side_schedule_output_sets o ON(o.catalog_id,o.schedule_id,o.name)=(m.catalog_id,m.schedule_id,m.set_name)
    WHERE(m.catalog_id,m.schedule_id)=(catalog,schedule) AND(root.measure<>'schedule_quantity' OR
     (row.unit_catalog_id,row.unit_revision_id,row.unit_code) IS DISTINCT FROM(o.unit_catalog_id,o.unit_revision_id,o.unit_code)))
  THEN RAISE EXCEPTION 'side_schedule_output_set';END IF;
 ELSIF EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'side_schedule_output_set';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r
  JOIN trading.side_schedule_bindings m ON(m.catalog_id,m.schedule_id,m.row_ordinal,m.port)=(r.catalog_id,r.schedule_id,r.ordinal,'market')
  JOIN trading.side_schedule_bindings q ON(q.catalog_id,q.schedule_id,q.row_ordinal,q.port)=(r.catalog_id,r.schedule_id,r.ordinal,'quote')
  JOIN trading.side_schedule_bindings s ON(s.catalog_id,s.schedule_id,s.row_ordinal,s.port)=(r.catalog_id,r.schedule_id,r.ordinal,'settlement')
  JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.source_catalog_id,m.definition_revision_id,m.contract_id)
  JOIN market_data.curves curve ON(curve.catalog_id,curve.revision_id,curve.code)=(m.source_catalog_id,m.definition_revision_id,m.curve_code)
  JOIN market_data.series series ON(series.catalog_id,series.revision_id,series.id)=(s.source_catalog_id,s.definition_revision_id,s.series_id)
  JOIN ref_data.quote_specifications quote ON(quote.catalog_id,quote.revision_id,quote.code)=(q.reference_catalog_id,q.reference_revision_id,q.quote_code)
  WHERE(r.catalog_id,r.schedule_id,r.operation)=(catalog,schedule,'contract_observation_price') AND(
   (s.source_catalog_id,s.definition_revision_id,series.contract_id,series.quote_code) IS DISTINCT FROM(m.source_catalog_id,m.definition_revision_id,m.contract_id,q.quote_code) OR
   (s.reference_catalog_id,s.reference_revision_id) IS DISTINCT FROM(q.reference_catalog_id,q.reference_revision_id) OR
   (curve.reference_catalog_id,curve.reference_revision_id) IS DISTINCT FROM(q.reference_catalog_id,q.reference_revision_id) OR
   curve.quote_code<>q.quote_code OR quote.kind<>'outright' OR curve.rule IS DISTINCT FROM 'direct' OR
   curve.direct_source_code IS DISTINCT FROM s.source_code OR
   curve.direct_role IS DISTINCT FROM CASE s.coordinate WHEN 'DailySettlement' THEN 'daily_settlement' WHEN 'TraderMark' THEN 'trader_mark' END OR
   (r.unit_catalog_id,r.unit_revision_id,r.unit_code,r.currency_catalog_id,r.currency_revision_id,r.currency_code,r.quote_multiplier)
    IS DISTINCT FROM(q.reference_catalog_id,q.reference_revision_id,quote.quantity_code,q.reference_catalog_id,q.reference_revision_id,quote.currency_code,quote.multiplier) OR
   r.parameters->>'expiryPolicy'='CarryFinalSettlement' AND NOT EXISTS(SELECT 1 FROM market_data.curve_expiry_rules e
    WHERE(e.catalog_id,e.revision_id,e.curve_code,e.rule_version)=(m.source_catalog_id,m.definition_revision_id,m.curve_code,'freeze_final_settlement_v1')) OR
   c.kind='future' AND s.coordinate<>'DailySettlement' OR
   (r.parameters->>'sampleAt')::timestamptz>=c.expiry_at AND r.parameters->>'expiryPolicy'='RequireLive'))
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r
  WHERE(r.catalog_id,r.schedule_id,r.operation)=(catalog,schedule,'output_guard') AND r.grid_ordinal IS NULL
   AND r.parameters->>'firstFixingAt' IS DISTINCT FROM 'none')
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 INSERT INTO trading.side_schedule_seals(catalog_id,schedule_id,content_hash) VALUES(catalog,schedule,h.content_hash);
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE OR REPLACE FUNCTION security.publish_side_schedules(catalog uuid,operation uuid,instrument uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE command trading.side_schedule_requests;publication_number integer;from_at timestamptz;actor uuid;item record;member record;
 candidate trading.instrument_side_schedules;prior trading.instrument_side_schedules;seal trading.side_schedule_seals;source uuid;current_publication boolean;prior_composition uuid;
BEGIN
 command=security.side_schedule_request_authority(catalog,operation);
 actor=command.actor_id;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM 1 FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=(catalog,operation) FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'side_schedule_unsealed';END IF;
 SELECT publication_order,effective_at INTO publication_number,from_at FROM(
  SELECT r.publication_order,r.effective_at FROM trading.swap_trade_revisions r
   WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.ppa_trade_revisions r
   WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,actor)
    AND EXISTS(SELECT 1 FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.revision,b.instrument_id)=(catalog,r.trade_id,r.revision,instrument))
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)
 ) published;
 IF publication_number IS NULL THEN RAISE EXCEPTION 'side_schedule_atomic_publication';END IF;
 SELECT publication_xid=pg_current_xact_id() INTO STRICT current_publication FROM trading.publications
  WHERE(catalog_id,publication_order)=(catalog,publication_number);
 FOR item IN SELECT i.slot,i.schedule_id FROM trading.side_schedule_request_items i
  WHERE(i.catalog_id,i.request_id)=(catalog,operation) ORDER BY i.slot LOOP
  PERFORM security.side_schedule_stage_authority(catalog,operation,item.schedule_id);
  SELECT * INTO STRICT seal FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,item.schedule_id) FOR UPDATE;
  FOR source IN SELECT reference_catalog_id FROM trading.side_schedule_authorities
   WHERE(catalog_id,schedule_id,kind)=(catalog,item.schedule_id,'reference') LOOP
   IF NOT security.reference_access(source,'ReadMarketData') THEN RAISE EXCEPTION 'No current bound reference authority' USING ERRCODE='42501';END IF;
  END LOOP;
  FOR source IN SELECT market_catalog_id FROM trading.side_schedule_authorities
   WHERE(catalog_id,schedule_id,kind)=(catalog,item.schedule_id,'market') LOOP
   IF NOT security.market_access(source,'ReadMarketData') THEN RAISE EXCEPTION 'No current bound market authority' USING ERRCODE='42501';END IF;
  END LOOP;
  candidate.catalog_id=catalog;candidate.instrument_id=instrument;candidate.slot=item.slot;candidate.schedule_id=item.schedule_id;
  candidate.swap_fixed_side_id=NULL;candidate.swap_floating_side_id=NULL;candidate.swap_whole_term_side_id=NULL;candidate.ppa_side_id=NULL;candidate.gas_side_id=NULL;
  SELECT side_id INTO candidate.gas_side_id FROM trading.gas_memberships WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,item.slot);
  SELECT s.id INTO candidate.ppa_side_id FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
   WHERE(m.catalog_id,m.instrument_id,m.position)=(catalog,instrument,item.slot);
  IF candidate.ppa_side_id IS NULL AND candidate.gas_side_id IS NULL THEN
   SELECT CASE item.slot WHEN 0 THEN i.fixed_side_id END AS fixed,
    CASE item.slot WHEN 0 THEN i.first_floating_side_id WHEN 1 THEN i.floating_side_id END AS floating,
    CASE item.slot WHEN 0 THEN i.scheduled_first_id WHEN 1 THEN i.scheduled_second_id END AS whole
    INTO STRICT member FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument);
   candidate.swap_fixed_side_id=member.fixed;candidate.swap_floating_side_id=member.floating;candidate.swap_whole_term_side_id=member.whole;
  END IF;
  candidate.side_id=coalesce(candidate.ppa_side_id,candidate.swap_fixed_side_id,candidate.swap_floating_side_id,candidate.swap_whole_term_side_id,candidate.gas_side_id);
  IF candidate.side_id IS NULL THEN RAISE EXCEPTION 'side_schedule_membership';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
   WHERE(h.catalog_id,h.id)=(catalog,item.schedule_id) AND s.family=CASE
    WHEN candidate.gas_side_id IS NOT NULL THEN CASE item.slot WHEN 0 THEN 'gas' ELSE 'fixed' END WHEN candidate.swap_fixed_side_id IS NOT NULL THEN 'fixed' WHEN candidate.swap_floating_side_id IS NOT NULL THEN 'floating'
    WHEN candidate.swap_whole_term_side_id IS NOT NULL THEN(SELECT kind FROM trading.swap_whole_term_sides WHERE(catalog_id,id)=(catalog,candidate.swap_whole_term_side_id))
    ELSE(SELECT CASE kind WHEN 'energy' THEN 'power' WHEN 'indexed' THEN 'floating' ELSE kind END FROM trading.ppa_sides WHERE(catalog_id,id)=(catalog,candidate.ppa_side_id)) END)
  THEN RAISE EXCEPTION 'side_schedule_family';END IF;
  SELECT * INTO prior FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,item.slot);
  IF FOUND THEN
   IF prior IS DISTINCT FROM candidate THEN RAISE EXCEPTION 'side_schedule_membership_conflict';END IF;
  ELSE
   IF NOT current_publication THEN RAISE EXCEPTION 'side_schedule_atomic_publication';END IF;
   INSERT INTO trading.side_schedule_publications
    SELECT catalog,item.schedule_id,seal.content_hash,publication_number,i.effective_at FROM trading.instruments i WHERE(i.catalog_id,i.id)=(catalog,instrument)
    ON CONFLICT DO NOTHING;
   INSERT INTO trading.instrument_side_schedules SELECT candidate.*;
  END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs i JOIN trading.side_schedule_ports p ON(p.catalog_id,p.schedule_id,p.name)=(i.catalog_id,i.target_schedule,i.port)
  LEFT JOIN trading.ppa_memberships m ON(m.catalog_id,m.instrument_id,m.position)=(catalog,instrument,i.source_slot)
  WHERE(i.catalog_id,i.request_id)=(catalog,operation) AND(m.role IS DISTINCT FROM CASE p.slot_role
   WHEN '1' THEN 'energy_delivery' WHEN '2' THEN 'certificate_delivery' WHEN '3' THEN 'capacity_delivery' WHEN '4' THEN 'strike_payment'
   WHEN '5' THEN 'indexed_payment' WHEN '6' THEN 'fixed_charge' WHEN '7' THEN 'certificate_payment' WHEN '8' THEN 'capacity_payment' ELSE NULL END
   OR m.ordinal IS DISTINCT FROM p.slot_ordinal)) THEN RAISE EXCEPTION 'side_schedule_port_membership';END IF;
 SELECT request_id INTO prior_composition FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF FOUND THEN
  IF EXISTS((SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)
   EXCEPT SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,prior_composition))
   UNION ALL(SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,prior_composition)
   EXCEPT SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)))
  THEN RAISE EXCEPTION 'side_schedule_composition_conflict';END IF;
 ELSE
  IF NOT current_publication THEN RAISE EXCEPTION 'side_schedule_atomic_publication';END IF;
  INSERT INTO trading.instrument_schedule_compositions VALUES(catalog,instrument,operation);
 END IF;
 IF NOT security.trading_access(catalog,command.permission) THEN RAISE EXCEPTION 'Schedule publication authority expired' USING ERRCODE='42501';END IF;
END$$;

CREATE OR REPLACE FUNCTION trading.require_instrument_side_schedules() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE expected integer;actual integer;
BEGIN
 IF TG_TABLE_NAME='gas_instruments' THEN
  SELECT count(*) INTO expected FROM trading.gas_memberships WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSIF TG_TABLE_NAME='ppa_instruments' THEN
  SELECT count(*) INTO expected FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSE expected=2;END IF;
 SELECT count(*) INTO actual FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 IF expected=0 OR actual<>expected OR NOT EXISTS(SELECT 1 FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id))
 THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 RETURN NEW;
END$$;

CREATE OR REPLACE FUNCTION security.publish_operation_side_schedules(catalog uuid,operation uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE instrument uuid;
BEGIN
 PERFORM security.side_schedule_request_authority(catalog,operation);
 SELECT i INTO STRICT instrument FROM(
  SELECT r.instrument_id i FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.operation_id)=(catalog,operation)
  UNION ALL SELECT b.instrument_id FROM trading.ppa_trade_revisions r JOIN trading.ppa_revision_bindings b
   ON(b.catalog_id,b.trade_id,b.revision)=(r.catalog_id,r.trade_id,r.revision)
   WHERE(r.catalog_id,r.operation_id)=(catalog,operation) AND NOT EXISTS(SELECT 1 FROM trading.ppa_revision_bindings old
    WHERE(old.catalog_id,old.trade_id,old.revision,old.binding_id)=(r.catalog_id,r.trade_id,r.previous_revision,b.binding_id))
  UNION ALL SELECT instrument_id FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)
 ) replacements;
 PERFORM security.publish_side_schedules(catalog,operation,instrument);
END$$;

CREATE OR REPLACE FUNCTION security.require_side_schedule_read(catalog uuid,instrument uuid,schedule uuid,context uuid,as_of timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE p trading.side_schedule_publications;reference_catalog uuid;allowed boolean;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 IF NOT EXISTS(SELECT 1 FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,schedule_id)=(catalog,instrument,schedule))
 THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=(catalog,instrument)) THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 SELECT * INTO STRICT p FROM trading.side_schedule_publications WHERE(catalog_id,schedule_id)=(catalog,schedule);
 IF context IS NULL THEN
  allowed=EXISTS(SELECT 1 FROM trading.instruments i WHERE(i.catalog_id,i.id)=(catalog,instrument)
   AND security.trading_visible(catalog,i.publication_order,i.effective_at)) AND(
   EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
    AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR
   EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR
   EXISTS(SELECT 1 FROM trading.ppa_revision_bindings b JOIN trading.ppa_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(b.catalog_id,b.trade_id,b.revision)
    WHERE(b.catalog_id,b.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)));
 ELSE
  allowed=EXISTS(SELECT 1 FROM trading.instruments i WHERE(i.catalog_id,i.id)=(catalog,instrument)
   AND security.ppa_visible_at(catalog,i.publication_order,i.effective_at,context,as_of)) AND(
   EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
    AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR
   EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR
   EXISTS(SELECT 1 FROM trading.ppa_revision_bindings b JOIN trading.ppa_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(b.catalog_id,b.trade_id,b.revision)
    WHERE(b.catalog_id,b.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)));
 END IF;
 SELECT s.reference_catalog_id INTO STRICT reference_catalog FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE(h.catalog_id,h.id)=(catalog,schedule);
 IF allowed IS NOT TRUE OR NOT security.reference_access(reference_catalog,'ReadMarketData')
  OR EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id)=(catalog,schedule)
   AND CASE a.kind WHEN 'reference' THEN NOT security.reference_access(a.authority_catalog_id,'ReadMarketData') ELSE NOT security.market_access(a.authority_catalog_id,'ReadMarketData') END)
 THEN RAISE EXCEPTION 'No current retained schedule access' USING ERRCODE='42501';END IF;
END$$;

CREATE OR REPLACE FUNCTION security.read_instrument_side_schedule_headers(catalog uuid,instrument uuid,context uuid,as_of timestamptz) RETURNS SETOF jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE item trading.instrument_side_schedules;expected integer;actual integer=0;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 SELECT count(*) INTO expected FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF expected=0 THEN SELECT count(*) INTO expected FROM trading.gas_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);END IF;
 IF expected=0 AND EXISTS(SELECT 1 FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument)) THEN expected=2;END IF;
 FOR item IN SELECT * FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id)=(catalog,instrument) ORDER BY slot LOOP
  PERFORM security.require_side_schedule_read(catalog,instrument,item.schedule_id,context,as_of);
  actual=actual+1;RETURN NEXT jsonb_build_object('side_id',item.side_id,'schedule_id',item.schedule_id,'slot',item.slot);
 END LOOP;
 IF expected=0 OR actual<>expected THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
END$$;

CREATE OR REPLACE FUNCTION trading.side_program_document(catalog uuid,schedule uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET enable_nestloop=off SET jit=off AS $$
 WITH RECURSIVE
 rows AS MATERIALIZED(SELECT * FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule)),
 edges AS MATERIALIZED(SELECT * FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule)),
 walk(source,node) AS(
  SELECT r.ordinal,e.target FROM rows r JOIN edges e ON e.source=r.ordinal
   WHERE r.operation IN('price_lookup','contract_observation_price','quantity_input','asset_capacity','discount')
  UNION
  SELECT w.source,e.target FROM walk w JOIN rows r ON r.ordinal=w.node
   JOIN edges e ON e.source=w.node WHERE r.operation<>'output_guard'),
 consumers AS(SELECT w.source,jsonb_agg(w.node ORDER BY w.node) addresses
  FROM walk w JOIN rows r ON r.ordinal=w.node WHERE r.operation='output_guard' GROUP BY w.source),
 dependents AS(SELECT source,jsonb_agg(target ORDER BY target) addresses FROM(SELECT DISTINCT source,target FROM edges) e GROUP BY source),
 operands AS(SELECT target,jsonb_object_agg(port,addresses ORDER BY port) ports
  FROM(SELECT target,port,jsonb_agg(source ORDER BY ordinal) addresses FROM edges GROUP BY target,port) p GROUP BY target),
 bindings AS(SELECT b.row_ordinal,jsonb_agg(jsonb_build_object(
  'row',b.row_ordinal,'port',b.port,'kind',b.kind,
  'reference',trading.side_program_reference(b.reference_catalog_id,b.reference_revision_id,CASE b.kind
   WHEN 'index_fixing' THEN b.source_code WHEN 'settlement_source' THEN b.source_code WHEN 'sibling_quantity' THEN b.quantity_code WHEN 'location' THEN b.location_code
   WHEN 'gas_assignment' THEN b.gas_assignment_code WHEN 'gas_product' THEN b.gas_product_code WHEN 'index' THEN b.index_code WHEN 'quote' THEN b.quote_code END),
  'market',CASE WHEN b.contract_id IS NOT NULL THEN jsonb_build_object(
   'curve',trading.side_program_reference(b.source_catalog_id,b.definition_revision_id,b.curve_code),
   'contract',jsonb_build_object('revision',jsonb_build_object('catalogId',b.source_catalog_id,'revisionId',b.definition_revision_id),'id',b.contract_id)) END,
  'series',CASE WHEN b.series_id IS NOT NULL THEN jsonb_build_object(
   'revision',jsonb_build_object('catalogId',b.source_catalog_id,'revisionId',b.definition_revision_id),'id',b.series_id) END,
  'entity',b.custom_curve_id,'version',b.custom_version,'coordinate',b.coordinate,'position',b.position,
  'definitions',CASE WHEN b.kind IN('curve_interval','quantity_source','asset','gas_assignment') THEN
   jsonb_build_object('catalogId',b.source_catalog_id,'revisionId',b.definition_revision_id) END,
  'code',CASE b.kind WHEN 'curve_interval' THEN coalesce(b.shaped_curve_code,b.curve_code)
   WHEN 'quantity_source' THEN b.quantity_source_code WHEN 'asset' THEN b.asset_code WHEN 'gas_assignment' THEN b.gas_assignment_code END,
  'entityCatalog',b.custom_catalog_id) ORDER BY b.port COLLATE "C") items
  FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) GROUP BY b.row_ordinal),
 instructions AS(SELECT jsonb_agg(jsonb_build_object('output',jsonb_build_object(
  'ordinal',r.ordinal,'key',r.local_key,'role',r.role,'operation',r.operation,'version',r.operation_version,'outputKind',r.output_kind,
  'unit',trading.side_program_reference(r.unit_catalog_id,r.unit_revision_id,r.unit_code),
  'currency',trading.side_program_reference(r.currency_catalog_id,r.currency_revision_id,r.currency_code),
  'quoteMultiplier',trim_scale(r.quote_multiplier)::text,'deliveryFrom',r.delivery_from,'deliveryTo',r.delivery_to,
  'interval',CASE WHEN r.interval_from IS NOT NULL THEN jsonb_build_object(
   'start',trading.side_program_instant(r.interval_from),'end',trading.side_program_instant(r.interval_to),'operatingDate',r.operating_date,
   'hourBeginning',r.hour_beginning,'minute',r.minute,'occurrence',r.occurrence) END,
  'observationFrom',r.observation_from,'observationTo',r.observation_to,'valueAt',trading.side_program_instant(r.value_at),
  'paymentOrdinal',r.payment_ordinal,'occurrenceKey',r.occurrence_key,'termsDigest',r.terms_digest,'parameters',r.parameters,'grid',r.grid_ordinal),
  'inputs',coalesce(o.ports,'{}'::jsonb),'bindings',coalesce(b.items,'[]'::jsonb),
  'dependents',coalesce(d.addresses,'[]'::jsonb),'consumers',coalesce(c.addresses,'[]'::jsonb)) ORDER BY r.ordinal) items
  FROM rows r LEFT JOIN operands o ON o.target=r.ordinal LEFT JOIN bindings b ON b.row_ordinal=r.ordinal
   LEFT JOIN dependents d ON d.source=r.ordinal LEFT JOIN consumers c ON c.source=r.ordinal)
 SELECT jsonb_build_object('version','booked_side_program_v1','scheduleManifest',encode(h.content_hash,'hex'),
  'family',s.family,'economicIdentity',s.economic_identity,'pricingBindingIdentity',s.pricing_binding_identity,
  'instructions',(SELECT items FROM instructions),
  'grids',coalesce((SELECT jsonb_agg(jsonb_build_object(
   'ordinal',g.ordinal,'key',g.local_key,'clock',trading.side_program_reference(g.clock_catalog_id,g.clock_revision_id,g.clock_code),
   'month',g.month,'firstRowOrdinal',g.first_row_ordinal,'origin',trading.side_program_instant(g.origin),
   'stepTicks',g.step_ticks,'axisCount',g.axis_count,'selection',g.selection,'lengths',g.lengths,'operatingDays',g.operating_days,
   'localMinutes',g.local_minutes,'occurrences',g.occurrences) ORDER BY g.ordinal)
   FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule)),'[]'::jsonb),
  'roots',coalesce((SELECT jsonb_agg(jsonb_build_object('name',r.name,'row',r.row_ordinal,'measure',r.measure) ORDER BY r.sequence_ordinal)
   FROM trading.side_schedule_roots r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)),'[]'::jsonb),
  'ports',coalesce((SELECT jsonb_agg(jsonb_build_object('name',p.name,'kind',p.kind,
   'slot',CASE WHEN p.slot_role IS NOT NULL THEN jsonb_build_object('role',p.slot_role::integer,'ordinal',p.slot_ordinal) END,
   'unit',trading.side_program_reference(p.unit_catalog_id,p.unit_revision_id,p.unit_code),'shapeIdentity',p.shape_identity) ORDER BY p.sequence_ordinal)
   FROM trading.side_schedule_ports p WHERE(p.catalog_id,p.schedule_id)=(catalog,schedule)),'[]'::jsonb))
 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
 WHERE(h.catalog_id,h.id)=(catalog,schedule);
$$;
CREATE CONSTRAINT TRIGGER retained_side_schedules AFTER INSERT ON trading.gas_instruments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_instrument_side_schedules();
CREATE POLICY current_authority ON market_data.gas_valuation_assignments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,'ReadMarketData') AND security.reference_access(reference_catalog_id,'ReadMarketData'));
CREATE POLICY version_scope ON market_data.gas_valuation_assignments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.market_fact_visible(catalog_id,revision_id,NULL) AND security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY current_authority ON trading.gas_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND EXISTS(SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(gas_instruments.catalog_id,gas_instruments.definition_revision_id,gas_instruments.assignment_code)));
CREATE POLICY temporal_scope ON trading.gas_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY current_authority ON trading.gas_trade_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,'ReadTrades') AND EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(gas_trade_revisions.catalog_id,gas_trade_revisions.instrument_id)));
CREATE POLICY temporal_scope ON trading.gas_trade_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY current_authority ON trading.gas_memberships AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,'ReadTrades') AND EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(gas_memberships.catalog_id,gas_memberships.instrument_id)));
DROP FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer);
CREATE FUNCTION security.read_side_schedule_bindings(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(row_ordinal integer,port text,kind text,reference_catalog_id uuid,
  reference_revision_id uuid,index_code text,quantity_code text,location_code text,
  quote_code text,source_catalog_id uuid,definition_revision_id uuid,curve_code text,
  contract_id uuid,shaped_curve_code text,quantity_source_code text,asset_code text,
  series_id uuid,custom_catalog_id uuid,custom_curve_id uuid,custom_version integer,
  output_node text,"position" integer,coordinate text,source_code text,
  fixing_index_code text,fixing_from date,fixing_to date,gas_assignment_code text,gas_product_code text) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT r.row_ordinal,r.port,r.kind,r.reference_catalog_id,r.reference_revision_id,
  r.index_code::text,r.quantity_code::text,r.location_code::text,r.quote_code::text,r.source_catalog_id,
  r.definition_revision_id,r.curve_code::text,r.contract_id,r.shaped_curve_code::text,r.quantity_source_code::text,
  r.asset_code::text,r.series_id,r.custom_catalog_id,r.custom_curve_id,r.custom_version,
  r.output_node,r.position,r.coordinate,r.source_code::text,r.fixing_index_code::text,
  r.fixing_from,r.fixing_to,r.gas_assignment_code::text,r.gas_product_code::text
  FROM trading.side_schedule_bindings r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND r.sequence_ordinal>=0 AND r.sequence_ordinal>=first_row ORDER BY r.sequence_ordinal LIMIT maximum;
END$$;

ALTER FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION trading.require_side_program_content(catalog uuid,schedule uuid,content bytea) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog SET jit=off AS $$
DECLARE original text;document jsonb;
BEGIN
 original=convert_from(content,'UTF8');
 IF original IS NULL OR NOT(original IS JSON OBJECT WITH UNIQUE KEYS)
  OR NOT trading.side_program_integer_tokens(original::json)
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 document=original::jsonb;
 IF document->>'version' IS DISTINCT FROM 'booked_side_program_v1'
 THEN RAISE EXCEPTION 'side_schedule_unsupported';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
  AND r.operation='output_guard' AND(
   r.value_at IS NULL OR r.currency_code IS NULL OR
   r.parameters->>'lifecycle' NOT IN('undue_payment_v1','interval_payment_v1','interval_delivery_v1','certificate_holding_v1','future_gas_delivery_v1') OR
   (r.parameters->>'lifecycle'='undue_payment_v1') IS DISTINCT FROM(r.grid_ordinal IS NULL) OR
   r.parameters->>'measure' IS DISTINCT FROM CASE WHEN r.grid_ordinal IS NULL THEN 'present_value_v1' ELSE 'value_excluding_overdue_v1' END))
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 IF document IS DISTINCT FROM trading.side_program_document(catalog,schedule)
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 PERFORM trading.require_side_program_structure(catalog,schedule,document);
EXCEPTION WHEN data_exception OR program_limit_exceeded THEN RAISE EXCEPTION 'side_schedule_program';
END$$;

CREATE FUNCTION trading.gas_identity_reference(catalog uuid,revision uuid,code text) RETURNS jsonb
LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog SET jit=off AS $$
 SELECT jsonb_build_array('reference',jsonb_build_array('reference_revision',catalog,revision),code);
$$;
ALTER FUNCTION trading.gas_identity_reference(uuid,uuid,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_identity_reference(uuid,uuid,text) FROM PUBLIC;
CREATE FUNCTION trading.require_gas_program_shape(catalog uuid,schedule uuid,slot integer,first_day date,last_day date,payment timestamptz,
 unit_total numeric,literal numeric,assignment market_data.gas_valuation_assignments,delivery ref_data.gas_delivery_conventions,start_at timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE days integer=last_day-first_day;prefix text;grid integer;row trading.side_schedule_rows;curve market_data.curves;quote ref_data.quote_specifications;contract market_data.contracts;target ref_data.settlement_targets;fixing market_data.index_fixing_observations;selected jsonb;selection jsonb;expected_assignment jsonb;revision_key jsonb;contract_key jsonb;quote_identity jsonb;
BEGIN
 SELECT * INTO STRICT curve FROM market_data.curves WHERE(catalog_id,revision_id,code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code);
 SELECT * INTO STRICT quote FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,curve.quote_code);
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE(h.catalog_id,h.id)=(catalog,schedule) AND(s.definition_revision_id,s.reference_catalog_id,s.reference_revision_id)=
  (assignment.revision_id,assignment.reference_catalog_id,assignment.reference_revision_id)) THEN RAISE EXCEPTION 'gas_schedule_revision';END IF;
 SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
 WHERE(m.catalog_id,m.revision_id,m.curve_code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code) AND c.kind='forward' AND c.delivery_from<=first_day AND c.delivery_to>=last_day
 AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date;
 SELECT * INTO STRICT target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,curve.settlement_target_code);
 IF target.kind<>'index' OR target.index_b_code IS NOT NULL OR quote.kind<>'outright' OR curve.commodity_code IS DISTINCT FROM assignment.product_code THEN RAISE EXCEPTION 'gas_schedule_sources';END IF;
 SELECT * INTO STRICT fixing FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code)=
  (assignment.catalog_id,assignment.revision_id,target.index_a_code,contract.delivery_from,contract.delivery_to,assignment.reference_catalog_id,assignment.reference_revision_id,quote.code);
 selected=jsonb_build_array('reference',jsonb_build_array('reference_revision',assignment.reference_catalog_id,assignment.reference_revision_id),target.index_a_code);
 selection=jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',selected,'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',selected);
 revision_key=jsonb_build_array('definition_revision',assignment.catalog_id,assignment.revision_id);
 contract_key=jsonb_build_array('contract',revision_key,contract.id);
 quote_identity=jsonb_build_array('quote',trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.code),1,
  trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.currency_code),trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.quantity_code),
  trim_scale(quote.multiplier)::text,trim_scale(quote.trading_tick)::text,trim_scale(quote.clearing_increment)::text,trim_scale(quote.minimum)::text,trim_scale(quote.maximum)::text);
 expected_assignment=jsonb_build_object('contract','gas_valuation_assignment_v1',
  'Key',trading.gas_identity_reference(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.code),
  'Location',jsonb_build_object('contract','gas_delivery_location_v1','Pipeline',trading.gas_identity_reference(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.pipeline_code),
   'Zone',trading.gas_identity_reference(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.zone_code),'Point',trading.gas_identity_reference(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.point_code)),
  'Product',trading.gas_identity_reference(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.product_code),
  'From',contract.delivery_from,'To',contract.delivery_to,'Settlement',selection,'Quote',quote_identity,
  'Projection',jsonb_build_array('projection',jsonb_build_array('request',jsonb_build_array('curve',revision_key,assignment.curve_code),contract_key),
   jsonb_build_array('market_contract',contract_key,2,contract.delivery_from,contract.delivery_to,to_char(contract.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',NULL),NULL),
  'Fixing',jsonb_build_object('contract','IndexFixing_v1','Index',selected,'Quote',trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.code),
   'From',fixing.observation_from,'To',fixing.observation_to,'FixingAt',to_char(fixing.fixing_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
   'Series',jsonb_build_array('series',revision_key,fixing.series_id),'Source',trading.gas_identity_reference(assignment.reference_catalog_id,assignment.reference_revision_id,fixing.source_code),'Implementation',fixing.implementation),
  'Adjustment',jsonb_build_array('price',trim_scale(assignment.adjustment)::text,jsonb_build_array('price_unit',
   trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.currency_code),trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.quantity_code),trim_scale(quote.multiplier)::text)));
 IF slot=0 AND EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id,kind)=(catalog,schedule,'gas_assignment') AND coordinate::jsonb IS DISTINCT FROM expected_assignment) THEN RAISE EXCEPTION 'gas_schedule_assignment';END IF;
 IF slot=0 AND(EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND r.operation='price_lookup' AND(
   (r.observation_from,r.observation_to) IS DISTINCT FROM(contract.delivery_from,contract.delivery_to) OR (r.parameters->>'selection')::jsonb IS DISTINCT FROM
    jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',selected,'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',selected))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND CASE b.port
   WHEN 'market' THEN(b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id) IS DISTINCT FROM(assignment.catalog_id,assignment.revision_id,assignment.curve_code,contract.id)
   WHEN 'assignment' THEN(b.reference_catalog_id,b.reference_revision_id,b.gas_assignment_code,b.source_catalog_id,b.definition_revision_id) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.code,assignment.catalog_id,assignment.revision_id)
   WHEN 'point' THEN(b.reference_catalog_id,b.reference_revision_id,b.location_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.point_code)
   WHEN 'product' THEN(b.reference_catalog_id,b.reference_revision_id,b.gas_product_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,assignment.product_code)
   WHEN 'quote' THEN(b.reference_catalog_id,b.reference_revision_id,b.quote_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,quote.code)
   WHEN 'index' THEN(b.reference_catalog_id,b.reference_revision_id,b.index_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,target.index_a_code)
   WHEN 'fixing' THEN(b.source_catalog_id,b.definition_revision_id,b.reference_catalog_id,b.reference_revision_id,b.series_id,b.source_code,b.fixing_index_code,b.fixing_from,b.fixing_to,b.quote_code,b.coordinate)
    IS DISTINCT FROM(assignment.catalog_id,assignment.revision_id,assignment.reference_catalog_id,assignment.reference_revision_id,fixing.series_id,fixing.source_code,fixing.index_code,fixing.observation_from,fixing.observation_to,fixing.quote_code,fixing.implementation)
   ELSE true END)) THEN RAISE EXCEPTION 'gas_schedule_sources';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND(
  r.output_kind IN('price','quantity') AND(r.unit_catalog_id,r.unit_revision_id,r.unit_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,quote.quantity_code) OR
  r.output_kind IN('price','money') AND(r.currency_catalog_id,r.currency_revision_id,r.currency_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,quote.currency_code) OR
  r.output_kind='price' AND r.quote_multiplier<>quote.multiplier)) THEN RAISE EXCEPTION 'gas_schedule_units';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'gas_schedule_ports';END IF;
 IF slot=0 THEN
  IF (SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>7*days OR
   EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
    WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND(r.operation<>'price_lookup' OR b.port NOT IN('market','index','quote','fixing','assignment','point','product'))) THEN RAISE EXCEPTION 'gas_schedule_sources';END IF;
  IF EXISTS(SELECT 1 FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule) AND(
   (g.clock_catalog_id,g.clock_revision_id,g.clock_code) IS DISTINCT FROM(assignment.reference_catalog_id,assignment.reference_revision_id,delivery.clock_code) OR
   g.ordinal>=days OR g.local_key<>'gas/'||g.ordinal||'/day' OR g.origin<>start_at+g.ordinal*interval '1 day' OR g.step_ticks<>864000000000 OR
   g.month<>date_trunc('month',first_day+g.ordinal)::date OR g.axis_count<>1 OR g.selection<>ARRAY[0] OR g.lengths<>ARRAY[1] OR
   g.first_row_ordinal IS DISTINCT FROM g.ordinal OR g.operating_days<>ARRAY[first_day+g.ordinal-date '0001-01-01'] OR g.local_minutes<>ARRAY[delivery.day_start_minute] OR g.occurrences<>ARRAY[1])) THEN RAISE EXCEPTION 'gas_schedule_grid';END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
   EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'gas_schedule_cash';END IF;
 END IF;
 FOR row IN SELECT * FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule) LOOP
  IF row.local_key='side/pv' THEN
   IF row.operation<>'sum' OR row.grid_ordinal IS NOT NULL OR row.value_at IS NOT NULL THEN RAISE EXCEPTION 'gas_schedule_sum';END IF;
   CONTINUE;
  END IF;
  IF slot=0 THEN
   IF row.local_key !~ '^gas/[0-9]+/(market|adjustment|price|quantity|amount|discount|discounted|value|total)$' THEN RAISE EXCEPTION 'gas_schedule_row';END IF;
   grid=split_part(row.local_key,'/',2)::integer;prefix='gas/'||grid||'/';
   IF grid>=days OR row.value_at IS DISTINCT FROM(CASE WHEN row.operation='sum_cells' THEN NULL ELSE start_at+(grid+1)*interval '1 day' END) OR
    row.grid_ordinal IS DISTINCT FROM(CASE split_part(row.local_key,'/',3) WHEN 'market' THEN NULL WHEN 'adjustment' THEN NULL WHEN 'price' THEN NULL WHEN 'total' THEN NULL ELSE grid END)
    THEN RAISE EXCEPTION 'gas_schedule_row';END IF;
  ELSE
   prefix='payment/0/';grid=NULL;
   IF row.value_at IS DISTINCT FROM payment OR row.delivery_from IS DISTINCT FROM first_day OR row.delivery_to IS DISTINCT FROM last_day OR row.grid_ordinal IS NOT NULL THEN RAISE EXCEPTION 'gas_schedule_cash';END IF;
  END IF;
  IF row.operation='quantity_literal' AND(row.parameters->>'numerator')::numeric*(CASE WHEN slot=0 THEN days ELSE 1 END)<>(row.parameters->>'denominator')::numeric*unit_total THEN RAISE EXCEPTION 'gas_schedule_quantity';END IF;
  IF row.operation='constant_price' AND(row.parameters->>'numerator')::numeric<>(row.parameters->>'denominator')::numeric*(CASE WHEN slot=0 THEN assignment.adjustment ELSE literal END) THEN RAISE EXCEPTION 'gas_schedule_price';END IF;
  IF row.operation='price_application' AND((row.parameters->>'numerator')::numeric<>(row.parameters->>'denominator')::numeric OR row.parameters->>'mapping'<>'fixed_total_payment_v1') THEN RAISE EXCEPTION 'gas_schedule_application';END IF;
  IF row.operation='weighted_price' AND row.parameters->>'totalScaledWeight'<>'1000000000000000000' THEN RAISE EXCEPTION 'gas_schedule_weight';END IF;
  IF row.operation='discount' AND row.parameters->>'policy'<>'context_required_v1' THEN RAISE EXCEPTION 'gas_schedule_discount';END IF;
  IF row.operation='output_guard' AND(row.parameters->>'lifecycle'<>CASE WHEN slot=0 THEN 'future_gas_delivery_v1' ELSE 'undue_payment_v1' END OR
   row.parameters->>'measure'<>CASE WHEN slot=0 THEN 'value_excluding_overdue_v1' ELSE 'present_value_v1' END OR row.parameters->>'pricePolicy'<>'selected_context_v1' OR
   row.parameters->>'averaging'<>'none' OR row.parameters->>'firstFixingAt'<>'none' OR row.parameters->>'paymentRule'<>'explicit_payment_date_v1' OR row.parameters->>'unadjustedPayment'<>'none') THEN RAISE EXCEPTION 'gas_schedule_lifecycle';END IF;
 END LOOP;
 IF EXISTS(WITH expected AS(
 SELECT 'side/pv' k,'sum' op UNION ALL
 SELECT 'gas/'||n||'/'||v.k,v.op FROM generate_series(0,days-1) n CROSS JOIN(VALUES ('market','price_lookup'),('price','price_add'),('quantity','quantity_literal'),('amount','amount'),('discount','discount'),('discounted','pv'),('value','output_guard'),('total','sum_cells'))v(k,op) WHERE slot=0 UNION ALL
 SELECT 'gas/0/adjustment','constant_price' WHERE slot=0 UNION ALL
 SELECT 'payment/0/'||v.k,v.op FROM(VALUES ('quantity','quantity_literal'),('observation/0','constant_price'),('application/0','price_application'),('average','weighted_price'),('amount','amount'),('discount','discount'),('discounted','pv'),('value','output_guard'))v(k,op) WHERE slot<>0),
 actual AS(SELECT local_key k,operation op FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'gas_schedule_rows';END IF;
 IF EXISTS(WITH expected AS(
 SELECT 'gas/'||n||'/'||v.target target,v.port,v.ord ordinal,CASE WHEN v.source LIKE '@%' THEN substr(v.source,2) ELSE 'gas/'||n||'/'||v.source END source
 FROM generate_series(0,days-1) n CROSS JOIN(VALUES ('price','input',0,'market'),('price','input',1,'@gas/0/adjustment'),('amount','quantity',0,'quantity'),('amount','price',0,'price'),('discounted','amount',0,'amount'),('discounted','discount',0,'discount'),('value','amount',0,'amount'),('value','discounted',0,'discounted'),('total','input',0,'value'))v(target,port,ord,source) WHERE slot=0 UNION ALL
 SELECT 'side/pv','input',n,'gas/'||n||'/total' FROM generate_series(0,days-1) n WHERE slot=0 UNION ALL
 SELECT 'payment/0/'||v.target,v.port,v.ord,'payment/0/'||v.source FROM(VALUES ('application/0','input',0,'observation/0'),('average','input',0,'application/0'),('amount','quantity',0,'quantity'),('amount','price',0,'average'),('discounted','amount',0,'amount'),('discounted','discount',0,'discount'),('value','amount',0,'amount'),('value','discounted',0,'discounted'))v(target,port,ord,source) WHERE slot<>0 UNION ALL
 SELECT 'side/pv','input',0,'payment/0/value' WHERE slot<>0),
 actual AS(SELECT t.local_key target,e.port,e.ordinal,s.local_key source FROM trading.side_schedule_edges e
 JOIN trading.side_schedule_rows t ON(t.catalog_id,t.schedule_id,t.ordinal)=(e.catalog_id,e.schedule_id,e.target)
 JOIN trading.side_schedule_rows s ON(s.catalog_id,s.schedule_id,s.ordinal)=(e.catalog_id,e.schedule_id,e.source) WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'gas_schedule_edges';END IF;
 IF EXISTS(WITH expected AS(
 SELECT 'pv' name,'side/pv' k,'pv' measure UNION ALL
 SELECT 'gas/'||n||'/'||v.name,'gas/'||n||'/'||v.k,v.measure FROM generate_series(0,days-1) n
 CROSS JOIN(VALUES('quantity','quantity','schedule_quantity'),('delivery-value','amount','physical_value'),('pv','value','contribution_pv'))v(name,k,measure) WHERE slot=0 UNION ALL
 SELECT 'payment/0/'||v.name,'payment/0/'||v.k,v.measure FROM(VALUES('cash','amount','contractual_amount'),('pv','value','contribution_pv'))v(name,k,measure) WHERE slot<>0),
 actual AS(SELECT o.name,r.local_key k,o.measure FROM trading.side_schedule_roots o JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal) WHERE(o.catalog_id,o.schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'gas_schedule_roots';END IF;
END$$;
ALTER FUNCTION trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz) FROM PUBLIC;

CREATE FUNCTION security.gas_operation_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE r trading.gas_trade_revisions;actor uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No gas read access' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO r FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation) AND actor_id=actor;
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false);END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Gas read authority expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('tradeId',r.trade_id,'instrumentId',r.instrument_id,'revision',r.revision,'operationId',operation);
END$$;

CREATE FUNCTION security.book_physical_gas(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE c security.request_contexts;prior trading.trades;stage trading.side_schedule_requests;assignment market_data.gas_valuation_assignments;delivery ref_data.gas_delivery_conventions;
 booking_operation uuid=(body->>'operationId')::uuid;definition uuid=(body->>'definitionRevisionId')::uuid;book uuid=(body->>'bookId')::uuid;
 first_day date=(body->>'deliveryFrom')::date;last_day date=(body->>'deliveryTo')::date;payment timestamptz=(body->>'paymentAt')::timestamptz;
 quantity numeric=(body->>'quantity')::numeric;price numeric=(body->>'fixedPrice')::numeric;fee numeric=(body->>'usageFee')::numeric;
 publication integer;trade uuid;instrument uuid;booking jsonb;item record;expected integer;days integer;unit_total numeric;start_at timestamptz;end_at timestamptz;clock ref_data.clock_rules;tariff market_data.gas_usage_tariffs;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'No gas booking access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,booking_operation);
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request IS DISTINCT FROM body OR NOT EXISTS(SELECT 1 FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,booking_operation)) THEN RAISE EXCEPTION 'gas_conflict';END IF;
  RETURN security.gas_operation_status(catalog,booking_operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trade_revisions WHERE(catalog_id,operation_id)=(catalog,booking_operation)) OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE(catalog_id,operation_id)=(catalog,booking_operation)) OR EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE(catalog_id,operation_id)=(catalog,booking_operation)) THEN RAISE EXCEPTION 'gas_conflict';END IF;
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body ? 'usageFee') OR NOT(body ? 'usageTariffCode') OR NOT(body ? 'usageFeeDirection') OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN(
  'snapshotId','convention','knowledgeMode','operationId','bookId','definitionRevisionId','pipeline','zone','deliveryPoint','product','deliveryFrom','deliveryTo','quantity','quantityBasis','direction','fixedPrice','usageFee','usageTariffCode','usageFeeDirection','paymentAt','discounting','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision')) THEN RAISE EXCEPTION 'gas_input';END IF;
 IF booking_operation IS NULL OR booking_operation='00000000-0000-0000-0000-000000000000'::uuid OR body->>'convention' IS DISTINCT FROM 'physical_gas_fixed_quantity_v3' OR body->>'knowledgeMode' IS DISTINCT FROM 'current'
  OR nullif(body->>'pipeline','') IS NULL OR nullif(body->>'zone','') IS NULL OR nullif(body->>'deliveryPoint','') IS NULL OR nullif(body->>'product','') IS NULL
  OR body->'discounting' IS DISTINCT FROM 'false'::jsonb OR quantity IS NULL OR quantity<=0 OR quantity>=1e24 OR scale(quantity)>18 OR price IS NULL OR
  body->>'direction' NOT IN('receive','deliver') OR body->>'direction' IS NULL OR body->>'quantityBasis' NOT IN('per_operating_day','period_total_equal_days') OR body->>'quantityBasis' IS NULL OR
  first_day IS NULL OR last_day IS NULL OR first_day>=last_day OR last_day-first_day>31 OR first_day<date '2000-01-01' OR last_day>date '2100-01-01'
  OR valuation IS NULL OR NOT isfinite(valuation) OR payment IS NULL OR NOT isfinite(payment) THEN RAISE EXCEPTION 'gas_input';END IF;
 SELECT * INTO stage FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,booking_operation);
 IF NOT FOUND OR stage.command_hash<>sha256(convert_to(body::text,'UTF8')) OR stage.actor_id<>c.actor_id OR stage.permission<>'BookTrade' THEN RAISE EXCEPTION 'gas_schedule';END IF;
 PERFORM security.lock_side_schedule_request(catalog,booking_operation);
 SELECT a.* INTO assignment FROM market_data.gas_valuation_assignments a JOIN market_data.revisions r ON(r.catalog_id,r.id)=(a.catalog_id,a.revision_id)
 JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(r.catalog_id,r.id)
 WHERE(a.catalog_id,a.revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)=(catalog,definition,body->>'pipeline',body->>'zone',body->>'deliveryPoint',body->>'product') AND r.state='sealed'
 AND p.effective_from<=(valuation AT TIME ZONE 'UTC')::date AND(valuation AT TIME ZONE 'UTC')::date<p.effective_to;
 IF NOT FOUND OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(assignment.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'gas_definition';END IF;
 IF body->>'usageTariffCode' IS NOT NULL THEN
  IF fee IS NOT NULL THEN RAISE EXCEPTION 'gas_tariff';END IF;
  SELECT t.* INTO tariff FROM market_data.gas_usage_tariffs t JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code)
  WHERE(t.catalog_id,t.revision_id,t.code,t.reference_catalog_id,t.reference_revision_id,t.pipeline_code,t.product_code,t.quote_code)=
   (catalog,definition,body->>'usageTariffCode',assignment.reference_catalog_id,assignment.reference_revision_id,assignment.pipeline_code,assignment.product_code,v.quote_code)
  AND t.valid_from<=first_day AND last_day<=t.valid_to;
  IF NOT FOUND THEN RAISE EXCEPTION 'gas_tariff';END IF;
  fee=tariff.rate;
 END IF;
 SELECT * INTO STRICT delivery FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)=
  (assignment.reference_catalog_id,assignment.reference_revision_id,assignment.pipeline_code,assignment.zone_code,assignment.point_code,assignment.product_code);
 SELECT * INTO STRICT clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,delivery.clock_code);
 -- Initial persisted profile has an unambiguous fixed-offset gas day. Broader captured clocks remain a domain capability.
 IF clock.kind<>'fixed_offset' THEN RAISE EXCEPTION 'gas_clock';END IF;
 start_at=first_day::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>delivery.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
 end_at=last_day::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>delivery.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
 IF valuation>=start_at OR payment<end_at THEN RAISE EXCEPTION 'gas_delivery_lifecycle';END IF;
 IF (fee IS NULL AND body->>'usageFeeDirection' IS NOT NULL) OR (fee IS NOT NULL AND coalesce(body->>'usageFeeDirection','') NOT IN('pay','receive')) THEN RAISE EXCEPTION 'gas_fee_direction';END IF;
 expected=CASE WHEN fee IS NULL THEN 2 ELSE 3 END;days=last_day-first_day;
 unit_total=CASE body->>'quantityBasis' WHEN 'per_operating_day' THEN days ELSE 1 END;
 IF (SELECT count(*) FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,booking_operation))<>expected OR
  EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,booking_operation)) THEN RAISE EXCEPTION 'gas_schedule';END IF;
 FOR item IN SELECT i.slot,i.schedule_id,s.family FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id)
  JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) WHERE(i.catalog_id,i.request_id)=(catalog,booking_operation) ORDER BY i.slot LOOP
  IF item.slot>=expected OR item.family<>(CASE item.slot WHEN 0 THEN 'gas' ELSE 'fixed' END) THEN RAISE EXCEPTION 'gas_schedule';END IF;
  PERFORM trading.require_gas_program_shape(catalog,item.schedule_id,item.slot,first_day,last_day,payment,unit_total,CASE item.slot WHEN 1 THEN price ELSE fee END,assignment,delivery,start_at);
 END LOOP;
 booking=jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date,'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision');
 PERFORM security.require_trade_parties(catalog,booking,valuation);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=valuation AND p.effective_at<=valuation) THEN RAISE EXCEPTION 'gas_book';END IF;
 IF(SELECT count(*) FROM trading.gas_trade_revisions WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'gas_limit';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());instrument=gen_random_uuid();trade=gen_random_uuid();
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.gas_instruments VALUES(catalog,instrument,definition,assignment.code,first_day,last_day,body->>'quantityBasis',price,fee,payment,publication,valuation,body->>'usageTariffCode',CASE body->>'usageFeeDirection' WHEN 'pay' THEN -1 WHEN 'receive' THEN 1 END);
 INSERT INTO trading.gas_memberships SELECT catalog,instrument,i.slot,h.side_id FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id) WHERE(i.catalog_id,i.request_id)=(catalog,booking_operation);
 PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades VALUES(catalog,trade,book,booking_operation,c.actor_id,body,publication,valuation);
 INSERT INTO trading.gas_trade_revisions VALUES(catalog,trade,1,instrument,book,booking_operation,c.actor_id,quantity,CASE body->>'direction' WHEN 'receive' THEN 1 ELSE -1 END,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation);
 IF NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'Gas write access expired' USING ERRCODE='42501';END IF;
 RETURN security.gas_operation_status(catalog,booking_operation);
END$$;
ALTER FUNCTION security.gas_operation_status(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.book_physical_gas(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_operation_status(uuid,uuid),security.book_physical_gas(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_operation_status(uuid,uuid),security.book_physical_gas(uuid,jsonb,timestamptz) TO openetrm_runtime;
-- Product-specific ticket valuation is admitted; a mixed portfolio cannot silently omit these positions.
CREATE FUNCTION security.require_supported_gas_portfolio_scope(catalog uuid,books uuid[],portfolios uuid[]) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No portfolio authority' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_trade_revisions r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
  WHERE r.catalog_id=catalog AND security.trading_visible(catalog,r.publication_order,r.effective_at)
  AND(cardinality(books)=0 OR r.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios)))
 THEN RAISE EXCEPTION 'gas_portfolio_scope';END IF;
END$$;
ALTER FUNCTION security.require_supported_gas_portfolio_scope(uuid,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_supported_gas_portfolio_scope(uuid,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.require_supported_gas_portfolio_scope(uuid,uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
