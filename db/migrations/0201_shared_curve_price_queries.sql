-- Explicit whole-delivery queries share the retained price-source interpreter.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES

  ('trading.side_schedule_parameters(text,jsonb)','bd210bb2a18f3f678d2c77bab5d1a82ab258b9fd8d91f87174ff017a0214efe3','null'::jsonb,'["search_path=pg_catalog"]'::jsonb,true),
  ('trading.side_program_document(uuid,uuid)','7bf41aa85430c006ec69650a6a740cac38e242052357ef29ada56f38d419f658','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "enable_nestloop=off", "jit=off"]'::jsonb,false),
  ('security.validate_custom_projection(uuid,jsonb)','0025e8ad9590959a16ae74320c9489e1aca2e01e052fc92ebff744c9020285f7','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb,false),
  ('security.prepare_floating_swap_period(uuid,uuid,uuid,uuid,text,jsonb,timestamp with time zone,integer,timestamp with time zone,uuid[])','1bc5c6f691a467882dd3ee850463efc61289dab8ff3ebfd8aa5cf3461a6971b6','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb,false),
  ('security.seal_side_schedule(uuid,uuid,uuid,text)','d52c7bc751bdbdbb5a8cc49df27926c5b057876331b41f676e93df35574ee5c7','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "enable_nestloop=off", "jit=off"]'::jsonb,false),
  ('trading.require_side_program_structure(uuid,uuid,jsonb)','76785ec9d04add0cc724e3d0a269cb2de24b62c68e1249c71bc655e1367b285d','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "enable_nestloop=off", "jit=off"]'::jsonb,false),
  ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone)','38c30e59da7f3d3dd4ab0c136b9a32c9ee9dfff69ef7ddd8d6b5719129944b81','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb,false),
  ('security.book_gas_composition(uuid,jsonb,timestamp with time zone)','b1b710d836902e77330207387d6618e51347f63cbe9254932218dbbba7fd53d5','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb,false),
  ('security.gas_component_sources_visible(uuid,uuid)','79d3afd426e4d6453bf58de282f3376f20a28bea0be58ef052afc6c0661a4915','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner", "openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb,false)
 )v(signature,hash,acl,config,schema_owned) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
   WHERE p.oid=to_regprocedure(helper.signature) AND
    (CASE WHEN helper.schema_owned THEN p.proowner=n.nspowner ELSE p.proowner='openetrm_security_owner'::regrole END)
    AND coalesce(to_jsonb(p.proacl),'null'::jsonb)=helper.acl AND coalesce(to_jsonb(p.proconfig),'null'::jsonb)=helper.config
    AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'curve_binding_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;


DO $$BEGIN IF to_regprocedure('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)') IS NOT NULL THEN RAISE EXCEPTION 'curve_binding_upgrade: unexpected helper trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)';END IF;END$$;

DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.custom_curve_projection_bindings'::regclass AND conname='custom_projection_coordinate' AND convalidated AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='e8f49b728512371f507fb9c9fd53697ba1acbc834bc71cb472e39aee5fae76dc') THEN RAISE EXCEPTION 'curve_binding_upgrade: unexpected constraint trading.custom_curve_projection_bindings';END IF;END$$;

ALTER TABLE trading.custom_curve_projection_bindings DROP CONSTRAINT custom_projection_coordinate;
ALTER TABLE trading.custom_curve_projection_bindings ADD CONSTRAINT custom_projection_coordinate CHECK ((((selection = 'single_output_position_v1'::text) AND ("position" IS NOT NULL) AND ("position" >= 0)) OR ((selection IN('exact_delivery_interval_v1','containing_delivery_interval_v1')) AND ("position" IS NULL))));

DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='risk.job_custom_curves'::regclass AND conname='custom_projection_coordinate' AND convalidated AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='e8f49b728512371f507fb9c9fd53697ba1acbc834bc71cb472e39aee5fae76dc') THEN RAISE EXCEPTION 'curve_binding_upgrade: unexpected constraint risk.job_custom_curves';END IF;END$$;

ALTER TABLE risk.job_custom_curves DROP CONSTRAINT custom_projection_coordinate;
ALTER TABLE risk.job_custom_curves ADD CONSTRAINT custom_projection_coordinate CHECK ((((selection = 'single_output_position_v1'::text) AND ("position" IS NOT NULL) AND ("position" >= 0)) OR ((selection IN('exact_delivery_interval_v1','containing_delivery_interval_v1')) AND ("position" IS NULL))));

DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.side_schedule_rows'::regclass AND conname='side_schedule_source_output_kind' AND convalidated AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='7056dc68b876d020636de60b9c5e1ede0ac1a3e2d1f2e9880f2f81469826aa09') THEN RAISE EXCEPTION 'curve_binding_upgrade: unexpected constraint trading.side_schedule_rows';END IF;END$$;

ALTER TABLE trading.side_schedule_rows DROP CONSTRAINT side_schedule_source_output_kind;
ALTER TABLE trading.side_schedule_rows ADD CONSTRAINT side_schedule_source_output_kind CHECK ((output_kind =
CASE
    WHEN (operation = ANY (ARRAY['constant_price'::text, 'price_lookup'::text, 'curve_price_lookup'::text, 'contract_observation_price'::text, 'price_scale'::text, 'price_add'::text, 'weighted_price'::text, 'price_application'::text])) THEN 'price'::text
    WHEN (operation = ANY (ARRAY['quantity_input'::text, 'quantity_literal'::text, 'profile_quantity'::text, 'asset_capacity'::text, 'quantity_normalize'::text, 'quantity_add'::text, 'quantity_scale'::text, 'quantity_transform'::text, 'quantity_sum_cells'::text])) THEN 'quantity'::text
    WHEN (operation = 'discount'::text) THEN 'factor'::text
    WHEN (operation = ANY (ARRAY['amount'::text, 'pv'::text, 'sum'::text, 'sum_cells'::text, 'output_guard'::text])) THEN 'money'::text
    ELSE NULL::text
END));

DO $patch$ DECLARE definition text=pg_get_functiondef('trading.side_schedule_parameters(text,jsonb)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$WHEN 'price_lookup' THEN ARRAY['selection']$old$,$new$WHEN 'price_lookup' THEN ARRAY['selection'] WHEN 'curve_price_lookup' THEN ARRAY['from','policy','selection','to']$new$,1),
($old$ IF parameters ? 'numerator' THEN$old$,$new$ IF operation='curve_price_lookup' THEN
  IF parameters->>'policy'<>'containing_delivery_interval_v1' OR
   parameters->>'from' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR parameters->>'to' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN RETURN false;END IF;
  BEGIN
   IF (parameters->>'from')::date<'0001-01-01' OR (parameters->>'to')::date>'9999-12-31' OR
    (parameters->>'from')::date>=(parameters->>'to')::date THEN RETURN false;END IF;
  EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow THEN RETURN false;END;
 END IF;
 IF parameters ? 'numerator' THEN$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','trading.side_schedule_parameters(text,jsonb)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

ALTER TABLE trading.side_schedule_rows ADD CONSTRAINT curve_price_query_shape CHECK(operation<>'curve_price_lookup' OR coalesce(
 grid_ordinal IS NULL AND observation_from IS NOT NULL AND observation_to IS NOT NULL AND
 (parameters->>'from')::date>=observation_from AND (parameters->>'to')::date<=observation_to,false));

DO $patch$ DECLARE definition text=pg_get_functiondef('security.validate_custom_projection(uuid,jsonb)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$selection='exact_delivery_interval_v1'$old$,$new$selection IN('exact_delivery_interval_v1','containing_delivery_interval_v1')$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','security.validate_custom_projection(uuid,jsonb)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

DO $patch$ DECLARE definition text=pg_get_functiondef('security.prepare_floating_swap_period(uuid,uuid,uuid,uuid,text,jsonb,timestamp with time zone,integer,timestamp with time zone,uuid[])'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$custom_projection->>'selection'<>'single_output_position_v1'$old$,$new$custom_projection->>'selection' NOT IN('single_output_position_v1','containing_delivery_interval_v1')$new$,1),
($old$custom_projection->>'selection'<>'exact_delivery_interval_v1'$old$,$new$custom_projection->>'selection' NOT IN('exact_delivery_interval_v1','containing_delivery_interval_v1')$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','security.prepare_floating_swap_period(uuid,uuid,uuid,uuid,text,jsonb,timestamp with time zone,integer,timestamp with time zone,uuid[])';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

DO $patch$ DECLARE definition text=pg_get_functiondef('trading.side_program_document(uuid,uuid)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$IN('price_lookup','contract_observation_price','quantity_input'$old$,$new$IN('price_lookup','curve_price_lookup','contract_observation_price','quantity_input'$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','trading.side_program_document(uuid,uuid)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

DO $patch$ DECLARE definition text=pg_get_functiondef('trading.require_side_program_structure(uuid,uuid,jsonb)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$IN('price_lookup','contract_observation_price','quantity_input'$old$,$new$IN('price_lookup','curve_price_lookup','contract_observation_price','quantity_input'$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','trading.require_side_program_structure(uuid,uuid,jsonb)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

DO $patch$ DECLARE definition text=pg_get_functiondef('security.seal_side_schedule(uuid,uuid,uuid,text)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$IN('constant_price','price_lookup','contract_observation_price'$old$,$new$IN('constant_price','price_lookup','curve_price_lookup','contract_observation_price'$new$,1),
($old$AND CASE r.operation
$old$,$new$AND CASE (CASE WHEN r.operation='curve_price_lookup' THEN 'price_lookup' ELSE r.operation END)
$new$,1),
($old$ IF EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r
$old$,$new$ IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.operation)=(catalog,schedule,'curve_price_lookup') AND
  NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind)=(catalog,schedule,r.ordinal,'projection','custom_output') AND b.position IS NULL))
 THEN RAISE EXCEPTION 'side_schedule_curve_source';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r
$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','security.seal_side_schedule(uuid,uuid,uuid,text)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

CREATE FUNCTION trading.require_gas_program_shape(catalog uuid, schedule uuid, slot integer, first_day date, last_day date, payment timestamp with time zone, unit_total numeric, literal numeric, assignment market_data.gas_valuation_assignments, delivery ref_data.gas_delivery_conventions, start_at timestamp with time zone, forecast jsonb)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET jit TO 'off'
AS $function$
DECLARE days integer=last_day-first_day;prefix text;grid integer;row trading.side_schedule_rows;curve market_data.curves;quote ref_data.quote_specifications;contract market_data.contracts;target ref_data.settlement_targets;fixing market_data.index_fixing_observations;selected jsonb;selection jsonb;expected_assignment jsonb;revision_key jsonb;contract_key jsonb;quote_identity jsonb;
BEGIN
 forecast=NULLIF(forecast,'null'::jsonb);
 IF slot=0 THEN
  IF forecast IS NOT NULL THEN
   forecast=security.validate_custom_projection(catalog,forecast);
   IF forecast->>'selection'<>'containing_delivery_interval_v1' OR EXISTS(
    SELECT 1 FROM security.custom_curve_native_sources(catalog,(forecast->>'curveId')::uuid,(forecast->>'version')::integer) s
     WHERE(s.source_catalog_id,s.source_revision_id) IS DISTINCT FROM(assignment.catalog_id,assignment.revision_id))
   THEN RAISE EXCEPTION 'gas_schedule_curve_source';END IF;
  END IF;
  IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND r.operation IN('price_lookup','curve_price_lookup') AND
   (r.operation IS DISTINCT FROM CASE WHEN forecast IS NULL THEN 'price_lookup' ELSE 'curve_price_lookup' END OR
    forecast IS NOT NULL AND ((r.parameters->>'from')::date IS DISTINCT FROM first_day+split_part(r.local_key,'/',2)::integer OR (r.parameters->>'to')::date IS DISTINCT FROM first_day+split_part(r.local_key,'/',2)::integer+1))) OR
   EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.port)=(catalog,schedule,'projection') AND
    (forecast IS NULL OR (b.custom_catalog_id,b.custom_curve_id,b.custom_version,b.coordinate,b.position) IS DISTINCT FROM
     ((forecast->>'catalogId')::uuid,(forecast->>'curveId')::uuid,(forecast->>'version')::integer,forecast->>'outputNode',NULL::integer)))
  THEN RAISE EXCEPTION 'gas_schedule_curve_source';END IF;
 END IF;
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
 IF slot=0 AND(EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND r.operation IN('price_lookup','curve_price_lookup') AND(
   (r.observation_from,r.observation_to) IS DISTINCT FROM(contract.delivery_from,contract.delivery_to) OR (r.parameters->>'selection')::jsonb IS DISTINCT FROM
    jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',selected,'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',selected))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND CASE b.port
   WHEN 'projection' THEN forecast IS NULL
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
  IF (SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>(CASE WHEN forecast IS NULL THEN 7 ELSE 8 END)*days OR
   EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
    WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND(r.operation NOT IN('price_lookup','curve_price_lookup') OR b.port NOT IN('projection','market','index','quote','fixing','assignment','point','product'))) THEN RAISE EXCEPTION 'gas_schedule_sources';END IF;
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
 SELECT 'gas/'||n||'/'||v.k,v.op FROM generate_series(0,days-1) n CROSS JOIN(VALUES ('market',CASE WHEN forecast IS NULL THEN 'price_lookup' ELSE 'curve_price_lookup' END),('price','price_add'),('quantity','quantity_literal'),('amount','amount'),('discount','discount'),('discounted','pv'),('value','output_guard'),('total','sum_cells'))v(k,op) WHERE slot=0 UNION ALL
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
END$function$
;

ALTER FUNCTION trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb) FROM PUBLIC;

CREATE OR REPLACE FUNCTION trading.require_gas_program_shape(catalog uuid, schedule uuid, slot integer, first_day date, last_day date, payment timestamp with time zone, unit_total numeric, literal numeric, assignment market_data.gas_valuation_assignments, delivery ref_data.gas_delivery_conventions, start_at timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET jit TO 'off'
AS $function$BEGIN
 PERFORM trading.require_gas_program_shape(catalog,schedule,slot,first_day,last_day,payment,unit_total,literal,assignment,delivery,start_at,NULL);
END$function$;

DO $patch$ DECLARE definition text=pg_get_functiondef('security.book_gas_composition(uuid,jsonb,timestamp with time zone)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$'quantityBasis','quantity','link'))$old$,$new$'quantityBasis','quantity','link','forecastCurve'))$new$,1),
($old$unit_total,NULL,a,d,start_at);$old$,$new$unit_total,NULL,a,d,start_at,item->'forecastCurve');$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','security.book_gas_composition(uuid,jsonb,timestamp with time zone)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

DO $patch$ DECLARE definition text=pg_get_functiondef('security.gas_component_sources_visible(uuid,uuid)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$ AND NOT EXISTS(
 SELECT 1 FROM trading.gas_component_terms$old$,$new$ AND NOT EXISTS(
 SELECT 1 FROM trading.gas_memberships m JOIN trading.side_schedules h ON(h.catalog_id,h.side_id)=(m.catalog_id,m.side_id)
 JOIN trading.side_schedule_bindings b ON(b.catalog_id,b.schedule_id)=(h.catalog_id,h.id)
 WHERE(m.catalog_id,m.instrument_id,b.kind)=(catalog,instrument,'custom_output') AND NOT security.custom_curve_visible(b.custom_catalog_id,b.custom_curve_id,b.custom_version)) AND NOT EXISTS(
 SELECT 1 FROM trading.gas_component_terms$new$,1)
 )v(before_text,after_text,expected) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>change.expected
  THEN RAISE EXCEPTION 'curve_binding_upgrade: replacement count for %','security.gas_component_sources_visible(uuid,uuid)';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;EXECUTE definition;END$patch$;

COMMIT;
