-- Add one explicit dated-contract observation source to the existing retained
-- schedule format. Existing source operations and sealed bytes remain unchanged.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $migration$
DECLARE definition text;changed text;item record;n integer;
BEGIN
 SELECT pg_get_functiondef('trading.side_schedule_parameters(text,jsonb)'::regprocedure) INTO definition;
 changed=replace(definition,$old$WHEN 'price_lookup' THEN ARRAY['selection']$old$,
  $new$WHEN 'price_lookup' THEN ARRAY['selection']
  WHEN 'contract_observation_price' THEN ARRAY['expiryPolicy','implementation','observationOrdinal','sampleAt','selectionAt']$new$);
 IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;
 definition=changed;
 changed=replace(definition,$old$ IF parameters ? 'numerator' THEN$old$,$new$ IF operation='contract_observation_price' THEN
  IF parameters->>'implementation'<>'contract_observation_source_v1' OR
   parameters->>'expiryPolicy' NOT IN('RequireLive','CarryFinalSettlement') OR
   parameters->>'observationOrdinal' !~ '^(0|[1-9][0-9]*)$' OR length(parameters->>'observationOrdinal')>10 OR
   (parameters->>'observationOrdinal')::numeric>2147483647 OR
   parameters->>'sampleAt' IS DISTINCT FROM parameters->>'selectionAt' OR
   parameters->>'sampleAt' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}0\+00:00$'
  THEN RETURN false;END IF;
  BEGIN
   IF (parameters->>'sampleAt')::timestamptz<'0001-01-01T00:00:00Z'::timestamptz OR
    (parameters->>'sampleAt')::timestamptz>='9999-01-01T00:00:00Z'::timestamptz THEN RETURN false;END IF;
  EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow THEN RETURN false;END;
 END IF;
 IF parameters ? 'numerator' THEN$new$);
 IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;
 EXECUTE changed;

 n=0;
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='trading.side_schedule_rows'::regclass AND contype='c'
  AND pg_get_constraintdef(oid) LIKE '%output_kind%CASE%constant_price%'
 LOOP EXECUTE format('ALTER TABLE trading.side_schedule_rows DROP CONSTRAINT %I',item.conname);n=n+1;END LOOP;
 IF n<>1 THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;
 n=0;
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='trading.side_schedule_bindings'::regclass AND contype='c'
  AND pg_get_constraintdef(oid) LIKE '%contract_price%'
 LOOP EXECUTE format('ALTER TABLE trading.side_schedule_bindings DROP CONSTRAINT %I',item.conname);n=n+1;END LOOP;
 IF n<>3 THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;
END$migration$;

ALTER TABLE trading.side_schedule_rows ADD CONSTRAINT side_schedule_source_output_kind CHECK(output_kind=CASE
 WHEN operation IN('constant_price','price_lookup','contract_observation_price','price_scale','price_add','weighted_price','price_application') THEN 'price'
 WHEN operation IN('quantity_input','quantity_literal','profile_quantity','asset_capacity','quantity_normalize','quantity_add','quantity_scale','quantity_transform','quantity_sum_cells') THEN 'quantity'
 WHEN operation='discount' THEN 'factor' WHEN operation IN('amount','pv','sum','sum_cells','output_guard') THEN 'money' END);
ALTER TABLE trading.side_schedule_rows ADD CONSTRAINT contract_observation_shape CHECK(operation<>'contract_observation_price' OR
 (grid_ordinal IS NULL AND observation_from IS NOT NULL AND observation_to=observation_from+1));
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_binding_kinds CHECK(kind IN('contract_price','index_fixing','settlement_source','curve_interval','custom_output','quantity_source',
 'asset','sibling_quantity','location','index','quote'));
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_binding_shape CHECK(CASE kind
 WHEN 'contract_price' THEN num_nonnulls(source_catalog_id,definition_revision_id,curve_code,contract_id)=4
 WHEN 'index_fixing' THEN num_nonnulls(reference_catalog_id,reference_revision_id,source_code,source_catalog_id,definition_revision_id,series_id,fixing_index_code,fixing_from,fixing_to,quote_code,coordinate)=11
 WHEN 'settlement_source' THEN num_nonnulls(reference_catalog_id,reference_revision_id,source_code,source_catalog_id,definition_revision_id,series_id,coordinate)=7 AND coordinate IN('DailySettlement','TraderMark')
 WHEN 'curve_interval' THEN num_nonnulls(source_catalog_id,definition_revision_id)=2 AND num_nonnulls(curve_code,shaped_curve_code)=1
 WHEN 'custom_output' THEN num_nonnulls(custom_catalog_id,custom_curve_id,custom_version,output_node,source_catalog_id,definition_revision_id,curve_code,contract_id,coordinate)=9
  AND coordinate=output_node AND(position IS NULL OR position>=0)
 WHEN 'quantity_source' THEN num_nonnulls(source_catalog_id,definition_revision_id,quantity_source_code)=3
 WHEN 'asset' THEN num_nonnulls(source_catalog_id,definition_revision_id,asset_code)=3
 WHEN 'sibling_quantity' THEN num_nonnulls(reference_catalog_id,reference_revision_id,quantity_code,coordinate,position)=5 AND position BETWEEN 0 AND 7
 WHEN 'location' THEN num_nonnulls(reference_catalog_id,reference_revision_id,location_code,coordinate)=4
 WHEN 'index' THEN num_nonnulls(reference_catalog_id,reference_revision_id,index_code)=3
 WHEN 'quote' THEN num_nonnulls(reference_catalog_id,reference_revision_id,quote_code)=3 ELSE false END);
ALTER TABLE trading.side_schedule_bindings ADD CONSTRAINT side_schedule_binding_fields CHECK(num_nonnulls(reference_catalog_id,reference_revision_id,index_code,quantity_code,location_code,quote_code,
 source_catalog_id,definition_revision_id,curve_code,contract_id,shaped_curve_code,quantity_source_code,asset_code,series_id,
 custom_catalog_id,custom_curve_id,custom_version,output_node,position,coordinate,source_code,fixing_index_code,fixing_from,fixing_to)=
 CASE kind WHEN 'contract_price' THEN 4 WHEN 'index_fixing' THEN 11 WHEN 'settlement_source' THEN 7 WHEN 'curve_interval' THEN 3
  WHEN 'custom_output' THEN 9+num_nonnulls(position) WHEN 'quantity_source' THEN 3+num_nonnulls(coordinate)
  WHEN 'asset' THEN 3+num_nonnulls(coordinate) WHEN 'sibling_quantity' THEN 5 WHEN 'location' THEN 4 WHEN 'index' THEN 3 WHEN 'quote' THEN 3 END);

DO $migration$
DECLARE definition text;changed text;signature regprocedure;
BEGIN
 SELECT pg_get_functiondef('security.seal_side_schedule(uuid,uuid,uuid,text)'::regprocedure) INTO definition;
 changed=replace(definition,$old$IN('constant_price','price_lookup','quantity_input'$old$,$new$IN('constant_price','price_lookup','contract_observation_price','quantity_input'$new$);
 IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;definition=changed;
 changed=replace(definition,$old$bool_and((port,kind) IN(('curve','curve_interval'),('index','index'))) curve_ports,$old$,
  $new$bool_and((port,kind) IN(('curve','curve_interval'),('index','index'))) curve_ports,
   bool_and((port,kind) IN(('market','contract_price'),('quote','quote'),('settlement','settlement_source'))) sample_ports,$new$);
 IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;definition=changed;
 changed=replace(definition,$old$WHEN 'price_lookup' THEN CASE WHEN s.dated$old$,
  $new$WHEN 'contract_observation_price' THEN NOT(coalesce(s.n,0)=3 AND s.market_port AND s.quote_port AND s.sample_ports)
   WHEN 'price_lookup' THEN CASE WHEN s.dated$new$);
 IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;definition=changed;
 changed=replace(definition,$old$ INSERT INTO trading.side_schedule_seals$old$,$new$ IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r
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
 INSERT INTO trading.side_schedule_seals$new$);
 IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;EXECUTE changed;

 FOREACH signature IN ARRAY ARRAY['trading.require_side_program_structure(uuid,uuid,jsonb)'::regprocedure,'trading.side_program_document(uuid,uuid)'::regprocedure]
 LOOP
  SELECT pg_get_functiondef(signature) INTO definition;
  changed=replace(definition,$old$IN('price_lookup','quantity_input','asset_capacity','discount')$old$,
   $new$IN('price_lookup','contract_observation_price','quantity_input','asset_capacity','discount')$new$);
  IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;
  IF signature='trading.side_program_document(uuid,uuid)'::regprocedure THEN
   definition=changed;
   changed=replace(definition,$old$WHEN 'index_fixing' THEN b.source_code$old$,$new$WHEN 'index_fixing' THEN b.source_code WHEN 'settlement_source' THEN b.source_code$new$);
   IF changed=definition THEN RAISE EXCEPTION 'contract_observation_migration_shape';END IF;
  END IF;
  EXECUTE changed;
 END LOOP;
END$migration$;
COMMIT;
