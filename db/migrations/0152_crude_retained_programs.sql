-- Add crude to retained executable programs without rebasing existing schedules.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
ALTER TABLE trading.side_schedule_bindings
 ADD COLUMN crude_assignment_code ref_data.code,
 ADD COLUMN crude_grade_code ref_data.code,
 ADD COLUMN crude_product_code ref_data.code,
 ADD FOREIGN KEY(source_catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id,crude_assignment_code)
 REFERENCES market_data.crude_valuation_assignments(catalog_id,revision_id,reference_catalog_id,reference_revision_id,code),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,crude_grade_code) REFERENCES ref_data.crude_grades(catalog_id,revision_id,code),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,crude_product_code) REFERENCES ref_data.commodities(catalog_id,revision_id,code);
ALTER TABLE trading.instrument_side_schedules ADD COLUMN crude_side_id uuid,
 ADD FOREIGN KEY(catalog_id,instrument_id,slot,crude_side_id) REFERENCES trading.crude_memberships(catalog_id,instrument_id,slot,side_id);
-- Every edited function must be the previously qualified body. The narrow edits
-- also require exact occurrence counts; unknown predecessors fail transactionally.
CREATE FUNCTION pg_temp.crude_upgrade_function(signature regprocedure,expected_source text,changes jsonb,replace_return boolean DEFAULT false)
RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $helper$
DECLARE source text;change jsonb;before_text text;after_text text;matches integer;
BEGIN
 IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_proc WHERE oid=signature) IS DISTINCT FROM expected_source
 THEN RAISE EXCEPTION 'crude_schedule_upgrade_predecessor: %',signature;END IF;
 source=pg_get_functiondef(signature);
 FOR change IN SELECT value FROM jsonb_array_elements(changes) LOOP
  before_text=change->>'before';after_text=change->>'after';
  IF before_text IS NULL OR before_text='' OR after_text IS NULL THEN RAISE EXCEPTION 'crude_schedule_upgrade_patch';END IF;
  matches=(length(source)-length(replace(source,before_text,'')))/length(before_text);
  IF matches<>(change->>'count')::integer THEN RAISE EXCEPTION 'crude_schedule_upgrade_anchor: %',signature;END IF;
  source=replace(source,before_text,after_text);
 END LOOP;
 IF replace_return THEN EXECUTE format('DROP FUNCTION %s',signature);END IF;
 EXECUTE source;
END$helper$;

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.instrument_side_schedules'::regclass AND conname='instrument_side_schedules_check') IS DISTINCT FROM $expected$CHECK ((num_nonnulls(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id) = 1))$expected$ THEN RAISE EXCEPTION 'crude_schedule_constraint_predecessor: instrument_side_schedules_check';END IF;END$guard$;
ALTER TABLE trading.instrument_side_schedules DROP CONSTRAINT instrument_side_schedules_check;
ALTER TABLE trading.instrument_side_schedules ADD CONSTRAINT instrument_side_schedules_check CHECK ((num_nonnulls(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id, crude_side_id) = 1));

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.instrument_side_schedules'::regclass AND conname='instrument_side_schedules_check1') IS DISTINCT FROM $expected$CHECK ((side_id = COALESCE(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id)))$expected$ THEN RAISE EXCEPTION 'crude_schedule_constraint_predecessor: instrument_side_schedules_check1';END IF;END$guard$;
ALTER TABLE trading.instrument_side_schedules DROP CONSTRAINT instrument_side_schedules_check1;
ALTER TABLE trading.instrument_side_schedules ADD CONSTRAINT instrument_side_schedules_check1 CHECK ((side_id = COALESCE(swap_fixed_side_id, swap_floating_side_id, swap_whole_term_side_id, ppa_side_id, gas_side_id, crude_side_id)));

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND conname='side_schedule_bindings_kind_check') IS DISTINCT FROM $expected$CHECK ((kind = ANY (ARRAY['contract_price'::text, 'index_fixing'::text, 'settlement_source'::text, 'curve_interval'::text, 'custom_output'::text, 'quantity_source'::text, 'asset'::text, 'sibling_quantity'::text, 'location'::text, 'index'::text, 'quote'::text, 'gas_assignment'::text, 'gas_product'::text])))$expected$ THEN RAISE EXCEPTION 'crude_schedule_constraint_predecessor: side_schedule_bindings_kind_check';END IF;END$guard$;
ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT side_schedule_bindings_kind_check;
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_bindings_kind_check CHECK ((kind = ANY (ARRAY['contract_price'::text, 'index_fixing'::text, 'settlement_source'::text, 'curve_interval'::text, 'custom_output'::text, 'quantity_source'::text, 'asset'::text, 'sibling_quantity'::text, 'location'::text, 'index'::text, 'quote'::text, 'gas_assignment'::text, 'gas_product'::text, 'crude_assignment'::text, 'crude_grade'::text, 'crude_product'::text])));

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
    WHEN 'gas_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'gas_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_product_code) = 3)
    WHEN 'location'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, location_code) = 3) AND (((port = 'point'::text) AND (coordinate IS NULL)) OR ((port = 'location'::text) AND (coordinate IS NOT NULL))))
    WHEN 'index'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, index_code) = 3)
    WHEN 'quote'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, quote_code) = 3)
    ELSE false
END)$expected$ THEN RAISE EXCEPTION 'crude_schedule_constraint_predecessor: side_schedule_bindings_check';END IF;END$guard$;
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
    WHEN 'crude_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'crude_grade'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_grade_code) = 3)
    WHEN 'crude_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, crude_product_code) = 3)
    WHEN 'gas_assignment'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_assignment_code, source_catalog_id, definition_revision_id, coordinate) = 6)
    WHEN 'gas_product'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, gas_product_code) = 3)
    WHEN 'location'::text THEN ((num_nonnulls(reference_catalog_id, reference_revision_id, location_code) = 3) AND (((port = ANY(ARRAY['point'::text,'facility'::text,'custody'::text])) AND (coordinate IS NULL)) OR ((port = 'location'::text) AND (coordinate IS NOT NULL))))
    WHEN 'index'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, index_code) = 3)
    WHEN 'quote'::text THEN (num_nonnulls(reference_catalog_id, reference_revision_id, quote_code) = 3)
    ELSE false
END);

DO $guard$ BEGIN IF (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND conname='side_schedule_bindings_check1') IS DISTINCT FROM $expected$CHECK ((num_nonnulls(reference_catalog_id, reference_revision_id, index_code, quantity_code, location_code, quote_code, source_catalog_id, definition_revision_id, curve_code, contract_id, shaped_curve_code, quantity_source_code, asset_code, series_id, custom_catalog_id, custom_curve_id, custom_version, output_node, "position", coordinate, source_code, fixing_index_code, fixing_from, fixing_to, gas_assignment_code, gas_product_code) =
CASE kind
    WHEN 'contract_price'::text THEN 4
    WHEN 'index_fixing'::text THEN 11
    WHEN 'settlement_source'::text THEN 7
    WHEN 'curve_interval'::text THEN 3
    WHEN 'custom_output'::text THEN (9 + num_nonnulls("position"))
    WHEN 'quantity_source'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'asset'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'sibling_quantity'::text THEN 5
    WHEN 'gas_assignment'::text THEN 6
    WHEN 'gas_product'::text THEN 3
    WHEN 'location'::text THEN (3 + num_nonnulls(coordinate))
    WHEN 'index'::text THEN 3
    WHEN 'quote'::text THEN 3
    ELSE NULL::integer
END))$expected$ THEN RAISE EXCEPTION 'crude_schedule_constraint_predecessor: side_schedule_bindings_check1';END IF;END$guard$;
ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT side_schedule_bindings_check1;
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_bindings_check1 CHECK ((num_nonnulls(reference_catalog_id, reference_revision_id, index_code, quantity_code, location_code, quote_code, source_catalog_id, definition_revision_id, curve_code, contract_id, shaped_curve_code, quantity_source_code, asset_code, series_id, custom_catalog_id, custom_curve_id, custom_version, output_node, "position", coordinate, source_code, fixing_index_code, fixing_from, fixing_to, gas_assignment_code, gas_product_code, crude_assignment_code, crude_grade_code, crude_product_code) =
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
END));

SELECT pg_temp.crude_upgrade_function('security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb)','75e2a799757a9394a57e5710faf501e63ffbcdf8fc92057499b737bcb7d42512',
 jsonb_build_array(
  jsonb_build_object('before',$before$'capacity','gas')$before$,'after',$after$'capacity','gas','crude')$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.append_side_schedule_aux(uuid,uuid,uuid,text,jsonb)','76ae357d5a525919cd5d00af8291daf7dd60c2304b484e7c32a4103771e71e7f',
 jsonb_build_array(
  jsonb_build_object('before',$before$'gas_assignment_code','gas_product_code','sequence_ordinal'$before$,'after',$after$'gas_assignment_code','gas_product_code','crude_assignment_code','crude_grade_code','crude_product_code','sequence_ordinal'$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.seal_side_schedule(uuid,uuid,uuid,text)','3c308ec7c3b95636303fa9c8f84e1fe5b8f2ffb0e0345b3e37db3829ec40104a',
 jsonb_build_array(
  jsonb_build_object('before',$before$bool_or(port='assignment') gas_assignment$before$,'after',$after$bool_or(port='crude') crude_assignment,bool_or(port='facility') crude_facility,bool_or(port='custody') crude_custody,bool_or(port='grade') crude_grade,bool_or(port='commodity') crude_product,
   bool_or(port='assignment') gas_assignment$after$,'count',1),
  jsonb_build_object('before',$before$WHEN 'assignment' THEN 'gas_assignment'$before$,'after',$after$WHEN 'crude' THEN 'crude_assignment' WHEN 'facility' THEN 'location' WHEN 'custody' THEN 'location' WHEN 'grade' THEN 'crude_grade' WHEN 'commodity' THEN 'crude_product' WHEN 'assignment' THEN 'gas_assignment'$after$,'count',1),
  jsonb_build_object('before',$before$OR s.gas_assignment AND s.gas_point AND s.gas_product))$before$,'after',$after$OR s.gas_assignment AND s.gas_point AND s.gas_product) AND (NOT(coalesce(s.crude_assignment,false) OR coalesce(s.crude_facility,false) OR coalesce(s.crude_custody,false) OR coalesce(s.crude_grade,false) OR coalesce(s.crude_product,false)) OR s.crude_assignment AND s.crude_facility AND s.crude_custody AND s.crude_grade AND s.crude_product AND NOT(coalesce(s.gas_assignment,false) OR coalesce(s.gas_point,false) OR coalesce(s.gas_product,false))))$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('trading.require_side_program_content(uuid,uuid,bytea)','697ced339ce01b2703aeb6a10f18e7748027ce750f4e1a4f04b65f9ef30b092c',
 jsonb_build_array(
  jsonb_build_object('before',$before$'certificate_holding_v1','future_gas_delivery_v1')$before$,'after',$after$'certificate_holding_v1','future_gas_delivery_v1','future_crude_delivery_v1')$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('trading.side_program_document(uuid,uuid)','a72c7a8fcce3b7194ee3e0550748b8dcfef210271c0d5f0dbb523754f6f004fd',
 jsonb_build_array(
  jsonb_build_object('before',$before$WHEN 'gas_assignment' THEN b.gas_assignment_code WHEN 'gas_product'$before$,'after',$after$WHEN 'crude_assignment' THEN b.crude_assignment_code WHEN 'crude_grade' THEN b.crude_grade_code WHEN 'crude_product' THEN b.crude_product_code WHEN 'gas_assignment' THEN b.gas_assignment_code WHEN 'gas_product'$after$,'count',1),
  jsonb_build_object('before',$before$'asset','gas_assignment')$before$,'after',$after$'asset','gas_assignment','crude_assignment')$after$,'count',1),
  jsonb_build_object('before',$before$WHEN 'gas_assignment' THEN b.gas_assignment_code END$before$,'after',$after$WHEN 'gas_assignment' THEN b.gas_assignment_code WHEN 'crude_assignment' THEN b.crude_assignment_code END$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer)','32d7d3e4bbceab34ba1c1b5b6306ee675a2f5ec022b0f4f5c1382c2370d7398e',
 jsonb_build_array(
  jsonb_build_object('before',$before$gas_product_code text)$before$,'after',$after$gas_product_code text, crude_assignment_code text, crude_grade_code text, crude_product_code text)$after$,'count',1),
  jsonb_build_object('before',$before$r.fixing_from,r.fixing_to,r.gas_assignment_code::text,r.gas_product_code::text$before$,'after',$after$r.fixing_from,r.fixing_to,r.gas_assignment_code::text,r.gas_product_code::text,r.crude_assignment_code::text,r.crude_grade_code::text,r.crude_product_code::text$after$,'count',1)
 ),true);

ALTER FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) TO openetrm_runtime,openetrm_risk_worker;

SELECT pg_temp.crude_upgrade_function('security.publish_admitted_side_schedules(uuid,uuid,uuid)','ed7335c67bbf14615c088c56f1237d009dffacb2a0af10db540eb49f55123dc6',
 jsonb_build_array(
  jsonb_build_object('before',$before$UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)$before$,'after',$after$UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)$after$,'count',1),
  jsonb_build_object('before',$before$candidate.gas_side_id=NULL;$before$,'after',$after$candidate.gas_side_id=NULL;candidate.crude_side_id=NULL;
  SELECT side_id INTO candidate.crude_side_id FROM trading.crude_memberships WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,item.slot);$after$,'count',1),
  jsonb_build_object('before',$before$IF candidate.ppa_side_id IS NULL AND candidate.gas_side_id IS NULL THEN$before$,'after',$after$IF candidate.ppa_side_id IS NULL AND candidate.gas_side_id IS NULL AND candidate.crude_side_id IS NULL THEN$after$,'count',1),
  jsonb_build_object('before',$before$candidate.swap_whole_term_side_id,candidate.gas_side_id)$before$,'after',$after$candidate.swap_whole_term_side_id,candidate.gas_side_id,candidate.crude_side_id)$after$,'count',1),
  jsonb_build_object('before',$before$WHEN candidate.gas_side_id IS NOT NULL THEN$before$,'after',$after$WHEN candidate.crude_side_id IS NOT NULL THEN CASE item.slot WHEN 0 THEN 'crude' ELSE 'fixed' END
    WHEN candidate.gas_side_id IS NOT NULL THEN$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.publish_side_schedules(uuid,uuid,uuid)','8ad4ce9c41c4044644dfddd60cceb1a739b395e76a766d7828c1ef73a1a3c0fa',
 jsonb_build_array(
  jsonb_build_object('before',$before$  UNION ALL SELECT t.request FROM trading.gas_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)$before$,'after',$after$  UNION ALL SELECT t.request FROM trading.gas_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)
  UNION ALL SELECT t.request FROM trading.crude_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.publish_operation_side_schedules(uuid,uuid)','97404f676161ef3446eb17ebb9c16c5ee8f31e96c9066a118e29656e64d509d9',
 jsonb_build_array(
  jsonb_build_object('before',$before$UNION ALL SELECT instrument_id FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)$before$,'after',$after$UNION ALL SELECT instrument_id FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)
  UNION ALL SELECT instrument_id FROM trading.crude_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.require_side_schedule_read(uuid,uuid,uuid,uuid,timestamptz)','72926ea7cb139f2cea87dc3bc0f4232191bd3da91bee521ca5003ca2808809f1',
 jsonb_build_array(
  jsonb_build_object('before',$before$EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR$before$,'after',$after$EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR
   EXISTS(SELECT 1 FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR$after$,'count',1),
  jsonb_build_object('before',$before$EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR$before$,'after',$after$EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR
   EXISTS(SELECT 1 FROM trading.crude_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('security.read_instrument_side_schedule_headers(uuid,uuid,uuid,timestamptz)','058f8154f0d094658b678d9740006208d844e6fe305e665a4d7d351d93a41528',
 jsonb_build_array(
  jsonb_build_object('before',$before$IF expected=0 AND EXISTS(SELECT 1 FROM trading.swap_instruments$before$,'after',$after$IF expected=0 THEN SELECT count(*) INTO expected FROM trading.crude_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);END IF;
 IF expected=0 AND EXISTS(SELECT 1 FROM trading.swap_instruments$after$,'count',1)
 ),false);

SELECT pg_temp.crude_upgrade_function('trading.require_instrument_side_schedules()','bf40321ca8db1a95cbd466fde715a5ef80b01a20b7b4ddf314a569dbbf87fd65',
 jsonb_build_array(
  jsonb_build_object('before',$before$IF TG_TABLE_NAME='gas_instruments' THEN$before$,'after',$after$IF TG_TABLE_NAME='crude_trade_revisions' THEN
  SELECT count(*) INTO expected FROM trading.crude_memberships WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSIF TG_TABLE_NAME='gas_instruments' THEN$after$,'count',1)
 ),false);

CREATE CONSTRAINT TRIGGER retained_side_schedules AFTER INSERT ON trading.crude_trade_revisions
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_instrument_side_schedules();
DROP FUNCTION pg_temp.crude_upgrade_function(regprocedure,text,jsonb,boolean);
COMMIT;
