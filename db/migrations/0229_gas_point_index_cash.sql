-- Exact location publication legs, independently admitted from the forecast.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid='trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)'::regprocedure
  AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='c0ecb1e38b938f7caddf28438e4642e319337eddbfdc2a87d9ac2a08d1bd8c05' AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
  AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[] AND p.proconfig=ARRAY['search_path=pg_catalog','jit=off'])
 THEN RAISE EXCEPTION 'gas_point_cash_upgrade: unexpected helper trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)';END IF;
END$$;
ALTER TABLE trading.gas_index_cash_terms ALTER COLUMN index_code DROP NOT NULL,
 DROP CONSTRAINT gas_index_cash_terms_observation_convention_check,
 ADD CONSTRAINT gas_index_cash_terms_observation_convention_check CHECK((observation_convention='daily_published_index_v1' AND index_code IS NOT NULL) OR(observation_convention='point_publications_v1' AND index_code IS NULL));
CREATE OR REPLACE FUNCTION trading.require_gas_index_cash_shape(catalog uuid, schedule uuid, source trading.gas_component_terms, a market_data.gas_valuation_assignments, terms jsonb)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET jit TO 'off'
AS $function$
DECLARE quote ref_data.quote_specifications;curve market_data.curves;target ref_data.settlement_targets;days integer=source.delivery_to-source.delivery_from;
 selection jsonb;custom jsonb;payment_day integer;spread numeric;day_number integer;prefix text;from_day date;payment timestamptz;unit_total numeric;
 q trading.side_schedule_rows;price_row trading.side_schedule_rows;contract market_data.contracts;fixing market_data.index_fixing_observations;
 mapping market_data.gas_point_index_periods;delivery ref_data.gas_delivery_conventions;clock ref_data.clock_rules;day_end timestamptz;
BEGIN
 IF terms IS NULL OR jsonb_typeof(terms)<>'object' OR NOT(terms ?& ARRAY['index','observationConvention','paymentConvention','paymentDay','spread','forecastCurve']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(terms) k WHERE k NOT IN('index','observationConvention','paymentConvention','paymentDay','spread','forecastCurve')) OR
 (terms->>'observationConvention' IS NULL OR terms->>'observationConvention' NOT IN('daily_published_index_v1','point_publications_v1')) OR terms->>'paymentConvention' IS DISTINCT FROM 'next_month_day_utc_v1' OR
 jsonb_typeof(terms->'paymentDay') IS DISTINCT FROM 'number' OR jsonb_typeof(terms->'spread') IS DISTINCT FROM 'string'
 THEN RAISE EXCEPTION 'gas_index_terms';END IF;
 payment_day=(terms->>'paymentDay')::integer;spread=(terms->>'spread')::numeric;
 IF payment_day NOT BETWEEN 1 AND 28 OR abs(spread)>=1e12 OR scale(spread)>18 OR days NOT BETWEEN 1 AND 1200 THEN RAISE EXCEPTION 'gas_index_terms';END IF;
 SELECT * INTO STRICT curve FROM market_data.curves WHERE(catalog_id,revision_id,code)=(a.catalog_id,a.revision_id,a.curve_code);
 SELECT * INTO STRICT quote FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,curve.quote_code);
 SELECT * INTO STRICT target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,curve.settlement_target_code);
 SELECT * INTO STRICT delivery FROM security.gas_setup_delivery(a);
 SELECT * INTO STRICT clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,delivery.clock_code);
 IF clock.kind<>'fixed_offset' THEN RAISE EXCEPTION 'gas_index_terms';END IF;
 IF terms->>'observationConvention'='point_publications_v1' THEN
  IF terms->'index' IS DISTINCT FROM 'null'::jsonb OR a.setup_id IS NULL OR NOT security.gas_setup_visible(catalog,a.setup_id) THEN RAISE EXCEPTION 'gas_point_index_terms';END IF;
  custom=security.gas_point_forecast(catalog,a,terms->'forecastCurve');
  IF custom IS NULL THEN RAISE EXCEPTION 'gas_point_index_terms';END IF;
 ELSE
  IF a.setup_id IS NOT NULL AND EXISTS(SELECT 1 FROM market_data.gas_point_index_periods ip WHERE(ip.catalog_id,ip.setup_id,ip.pipeline_code,ip.zone_code,ip.point_code,ip.product_code)=(catalog,a.setup_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)) THEN RAISE EXCEPTION 'gas_location_index_conflict';END IF;
  IF target.kind<>'index' OR terms->>'index' IS DISTINCT FROM target.index_a_code::text THEN RAISE EXCEPTION 'gas_index_terms';END IF;
  custom=CASE WHEN terms->'forecastCurve'='null'::jsonb THEN NULL ELSE security.validate_custom_projection(catalog,terms->'forecastCurve') END;
 END IF;
 mapping.curve_code=a.curve_code;mapping.index_code=target.index_a_code;mapping.observation_convention='daily_published_index_v1';
 IF custom IS NOT NULL AND custom->>'selection'<>'containing_delivery_interval_v1' THEN RAISE EXCEPTION 'gas_index_terms';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.side_schedule_seals z ON(z.catalog_id,z.schedule_id)=(h.catalog_id,h.id)
  JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE(h.catalog_id,h.id,s.family,s.definition_revision_id,s.reference_catalog_id,s.reference_revision_id)=
   (catalog,schedule,'floating',a.revision_id,quote.catalog_id,quote.revision_id)) OR
 EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
 EXISTS(SELECT 1 FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
 (SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))<>days*9+2 OR
 (SELECT count(*) FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule))<>days*11 OR
 (SELECT count(*) FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id)=(catalog,schedule))<>days*2+1 OR
 (SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>days*(CASE WHEN custom IS NULL THEN 4 ELSE 5 END)
 THEN RAISE EXCEPTION 'gas_index_schedule';END IF;
 unit_total=CASE source.quantity_basis WHEN 'per_operating_day' THEN days ELSE 1 END;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.operation)=(catalog,schedule,'payment/0/observation/0/adder','constant_price')
  AND(r.parameters->>'numerator')::numeric=spread*(r.parameters->>'denominator')::numeric) OR
 NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.row_ordinal,root.name,root.measure)=(r.catalog_id,r.schedule_id,r.ordinal,'pv','pv')
  WHERE(r.catalog_id,r.schedule_id,r.local_key,r.operation)=(catalog,schedule,'side/pv','sum'))
 THEN RAISE EXCEPTION 'gas_index_schedule';END IF;
 FOR day_number IN 0..days-1 LOOP
  prefix='payment/'||day_number;from_day=source.delivery_from+day_number;
  payment=(date_trunc('month',from_day::timestamp)+interval '1 month'+make_interval(days=>payment_day-1)) AT TIME ZONE 'UTC';
  day_end=(from_day+1)::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>delivery.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
  IF payment<day_end THEN RAISE EXCEPTION 'gas_index_payment';END IF;
  IF(SELECT count(*) FROM trading.side_schedule_rows r JOIN(VALUES('/quantity','quantity_literal'),('/observation/0',CASE WHEN custom IS NULL THEN 'price_lookup' ELSE 'curve_price_lookup' END),
   ('/observation/0/all-in','price_add'),('/application/0','price_application'),('/average','weighted_price'),('/amount','amount'),('/discount','discount'),('/discounted','pv'),('/value','output_guard')) expected(key,operation)
   ON(r.local_key,r.operation)=(prefix||expected.key,expected.operation)
   WHERE(r.catalog_id,r.schedule_id,r.payment_ordinal,r.delivery_from,r.delivery_to,r.value_at)=(catalog,schedule,day_number,from_day,from_day+1,payment) AND r.grid_ordinal IS NULL AND r.interval_from IS NULL AND r.interval_to IS NULL)<>9
  THEN RAISE EXCEPTION 'gas_index_schedule';END IF;
  SELECT * INTO STRICT q FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,local_key)=(catalog,schedule,prefix||'/quantity');
  IF(q.unit_catalog_id,q.unit_revision_id,q.unit_code) IS DISTINCT FROM(quote.catalog_id,quote.revision_id,quote.quantity_code) OR
   (q.parameters->>'numerator')::numeric*days IS DISTINCT FROM unit_total*(q.parameters->>'denominator')::numeric OR
   ((q.parameters->>'numerator')::numeric>0 AND(q.parameters->>'numerator')::numeric<1e120 AND(q.parameters->>'denominator')::numeric>0 AND(q.parameters->>'denominator')::numeric<1e120) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_index_quantity';END IF;
  SELECT * INTO STRICT price_row FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,local_key)=(catalog,schedule,prefix||'/observation/0');
  IF terms->>'observationConvention'='point_publications_v1' THEN
  SELECT * INTO STRICT mapping FROM market_data.gas_point_index_periods ip WHERE(ip.catalog_id,ip.setup_id,ip.pipeline_code,ip.zone_code,ip.point_code,ip.product_code)=
   (catalog,a.setup_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code) AND ip.valid_from<=from_day AND ip.valid_to>=from_day+1;
  IF NOT EXISTS(SELECT 1 FROM market_data.curves mc WHERE(mc.catalog_id,mc.revision_id,mc.code,mc.quote_code,mc.commodity_code,mc.reference_catalog_id,mc.reference_revision_id)=
   (catalog,a.revision_id,mapping.curve_code,quote.code,a.product_code,quote.catalog_id,quote.revision_id)) THEN RAISE EXCEPTION 'gas_point_index_terms';END IF;
  END IF;
  target.index_a_code=mapping.index_code;
  selection=jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',trading.gas_identity_reference(a.reference_catalog_id,a.reference_revision_id,mapping.index_code),
   'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',trading.gas_identity_reference(a.reference_catalog_id,a.reference_revision_id,mapping.index_code));
  SELECT c.* INTO STRICT contract FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(m.catalog_id,m.revision_id,m.curve_code,c.kind)=(catalog,a.revision_id,mapping.curve_code,'forward') AND c.delivery_from<=from_day AND c.delivery_to>=from_day+1
    AND CASE mapping.observation_convention WHEN 'daily_published_index_v1' THEN c.delivery_from=from_day AND c.delivery_to=from_day+1
     WHEN 'monthly_published_index_v1' THEN extract(day FROM c.delivery_from)=1 AND c.delivery_to=(c.delivery_from+interval '1 month')::date ELSE false END;
  SELECT * INTO STRICT fixing FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,implementation)=
   (catalog,a.revision_id,target.index_a_code,contract.delivery_from,contract.delivery_to,quote.catalog_id,quote.revision_id,quote.code,'published_index_observations_v1');
  IF fixing.fixing_at>=payment THEN RAISE EXCEPTION 'gas_index_payment';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id,s.contract_id,s.reference_catalog_id,s.reference_revision_id,s.quote_code)=
    (catalog,a.revision_id,fixing.series_id,contract.id,quote.catalog_id,quote.revision_id,quote.code)) THEN RAISE EXCEPTION 'gas_index_sources';END IF;
  IF(price_row.observation_from,price_row.observation_to,price_row.unit_catalog_id,price_row.unit_revision_id,price_row.unit_code,price_row.currency_catalog_id,price_row.currency_revision_id,price_row.currency_code,price_row.quote_multiplier) IS DISTINCT FROM
   (contract.delivery_from,contract.delivery_to,quote.catalog_id,quote.revision_id,quote.quantity_code,quote.catalog_id,quote.revision_id,quote.currency_code,quote.multiplier) OR
   (price_row.parameters->>'selection')::jsonb IS DISTINCT FROM selection OR(custom IS NOT NULL AND(price_row.parameters->>'policy',price_row.parameters->>'from',price_row.parameters->>'to') IS DISTINCT FROM('containing_delivery_interval_v1',from_day::text,(from_day+1)::text))
  THEN RAISE EXCEPTION 'gas_index_sources';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)=
    (catalog,schedule,price_row.ordinal,'market','contract_price',catalog,a.revision_id,mapping.curve_code,contract.id)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.reference_catalog_id,b.reference_revision_id,b.index_code)=
    (catalog,schedule,price_row.ordinal,'index','index',quote.catalog_id,quote.revision_id,target.index_a_code)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.reference_catalog_id,b.reference_revision_id,b.quote_code)=
    (catalog,schedule,price_row.ordinal,'quote','quote',quote.catalog_id,quote.revision_id,quote.code)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.reference_catalog_id,b.reference_revision_id,b.source_catalog_id,b.definition_revision_id,b.series_id,b.source_code,b.fixing_index_code,b.fixing_from,b.fixing_to,b.coordinate)=
    (catalog,schedule,price_row.ordinal,'fixing','index_fixing',quote.catalog_id,quote.revision_id,catalog,a.revision_id,fixing.series_id,fixing.source_code,target.index_a_code,contract.delivery_from,contract.delivery_to,'published_index_observations_v1')) OR
   (custom IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.custom_catalog_id,b.custom_curve_id,b.custom_version,b.output_node,b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)=
    (catalog,schedule,price_row.ordinal,'projection','custom_output',(custom->>'catalogId')::uuid,(custom->>'curveId')::uuid,(custom->>'version')::integer,custom->>'outputNode',catalog,a.revision_id,mapping.curve_code,contract.id) AND b.position IS NULL))
  THEN RAISE EXCEPTION 'gas_index_sources';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/application/0',jsonb_build_object('denominator','1','mapping',CASE WHEN terms->>'observationConvention'='point_publications_v1' THEN 'delivery_index_fixings_v1' ELSE 'calendar_day_arithmetic_v1' END,'numerator','1'))) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/average','{"totalScaledWeight":"1000000000000000000"}'::jsonb)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/discount','{"policy":"context_required_v1"}'::jsonb)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/value','{"averaging":"CalendarDayArithmetic","firstFixingAt":"none","lifecycle":"accrued_payment_v1","measure":"present_value_v1","paymentRule":"explicit_payment_date_v1","pricePolicy":"selected_context_v1","unadjustedPayment":"none"}'::jsonb))
  THEN RAISE EXCEPTION 'gas_index_schedule';END IF;
  IF(SELECT count(*) FROM(VALUES('/observation/0/all-in','input',0,prefix||'/observation/0'),('/observation/0/all-in','input',1,'payment/0/observation/0/adder'),
   ('/application/0','input',0,prefix||'/observation/0/all-in'),('/average','input',0,prefix||'/application/0'),('/amount','price',0,prefix||'/average'),('/amount','quantity',0,prefix||'/quantity'),
   ('/discounted','amount',0,prefix||'/amount'),('/discounted','discount',0,prefix||'/discount'),('/value','amount',0,prefix||'/amount'),('/value','discounted',0,prefix||'/discounted')) expected(target,port,ordinal,source)
   JOIN trading.side_schedule_rows t ON(t.catalog_id,t.schedule_id,t.local_key)=(catalog,schedule,prefix||expected.target)
   JOIN trading.side_schedule_rows s ON(s.catalog_id,s.schedule_id,s.local_key)=(catalog,schedule,expected.source)
   JOIN trading.side_schedule_edges e ON(e.catalog_id,e.schedule_id,e.target,e.port,e.ordinal,e.source)=(catalog,schedule,t.ordinal,expected.port,expected.ordinal,s.ordinal))<>10 OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows t JOIN trading.side_schedule_edges e ON(e.catalog_id,e.schedule_id,e.target,e.port,e.ordinal)=(t.catalog_id,t.schedule_id,t.ordinal,'input',day_number)
    JOIN trading.side_schedule_rows s ON(s.catalog_id,s.schedule_id,s.ordinal,s.local_key)=(e.catalog_id,e.schedule_id,e.source,prefix||'/value') WHERE(t.catalog_id,t.schedule_id,t.local_key)=(catalog,schedule,'side/pv')) OR
   (SELECT count(*) FROM trading.side_schedule_roots root JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
    WHERE(root.catalog_id,root.schedule_id)=(catalog,schedule) AND((root.name,root.measure,r.local_key)=(prefix||'/cash','contractual_amount',prefix||'/amount') OR(root.name,root.measure,r.local_key)=(prefix||'/pv','contribution_pv',prefix||'/value')))<>2
  THEN RAISE EXCEPTION 'gas_index_schedule';END IF;
 END LOOP;
END$function$
;
COMMIT;
