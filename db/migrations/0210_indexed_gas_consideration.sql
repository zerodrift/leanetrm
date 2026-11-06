-- Daily indexed consideration reuses the ordinary FloatingSide program.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('security.write_gas_composition(uuid,jsonb,timestamp with time zone)','d186600619b98a5cdce5c024b23abad45f6fc280f1066bb7240b8004fea01034','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb),
  ('security.seal_side_schedule(uuid,uuid,uuid,text)','9bc017eef4e83c057f21d949ad24a9300a6a905cad08b59591727901399fb9e9','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "enable_nestloop=off", "jit=off"]'::jsonb),
  ('security.publish_admitted_side_schedules(uuid,uuid,uuid)','c3ad2d11478ab21ed6d1728c3375f9d9b92ad9af25994289e7872a3fe8872a29','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb)
 )v(signature,hash,acl,config) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature) AND p.proowner='openetrm_security_owner'::regrole
   AND to_jsonb(p.proacl)=helper.acl AND to_jsonb(p.proconfig)=helper.config AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'gas_index_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
DO $$DECLARE c record;BEGIN
 FOR c IN SELECT * FROM(VALUES
  ('gas_component_terms_role_check','fb890bdaea0f439144e03368705ed7fd595798e59a38294a71dc9c72e2acf740'),
  ('gas_component_terms_check2','713ff1662c2681c79d9c56bbdadcd4812a7e3f23b373c1aafab08bacd903742e')
 )v(name,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.gas_component_terms'::regclass AND conname=c.name AND convalidated
   AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')=c.hash)
  THEN RAISE EXCEPTION 'gas_index_upgrade: unexpected constraint %',c.name USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
ALTER TABLE trading.gas_component_terms DROP CONSTRAINT gas_component_terms_role_check,DROP CONSTRAINT gas_component_terms_check2,
 ADD CONSTRAINT gas_component_terms_role_check CHECK(role IN('physical','consideration','fee','indexed_consideration')),
 ADD CONSTRAINT gas_component_terms_check2 CHECK(role='physical' OR(source_slot IS NOT NULL AND stage IS NOT NULL AND formula IS NULL AND fraction IS NULL
  AND num_nonnulls(pipeline_code,zone_code,point_code,product_code,delivery_from,delivery_to,quantity_basis,independent_quantity)=0
  AND CASE WHEN role='indexed_consideration' THEN price IS NULL AND tariff_code IS NULL AND payment_at IS NULL ELSE price IS NOT NULL AND isfinite(payment_at) END));
CREATE TABLE trading.gas_index_cash_terms(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,index_code ref_data.code NOT NULL,
 observation_convention text NOT NULL CHECK(observation_convention='daily_published_index_v1'),payment_convention text NOT NULL CHECK(payment_convention='next_month_day_utc_v1'),
 payment_day integer NOT NULL CHECK(payment_day BETWEEN 1 AND 28),spread numeric NOT NULL CHECK(abs(spread)<1e12 AND scale(spread)<=18),projection_id uuid,
 PRIMARY KEY(catalog_id,instrument_id,slot),FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,projection_id) REFERENCES trading.custom_curve_projection_bindings(catalog_id,id));
ALTER TABLE trading.gas_index_cash_terms ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_index_cash_terms FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON trading.gas_index_cash_terms TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON trading.gas_index_cash_terms FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(EXISTS(
 SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(gas_index_cash_terms.catalog_id,gas_index_cash_terms.instrument_id)));
CREATE POLICY worker_service ON trading.gas_index_cash_terms AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE TRIGGER immutable_terms BEFORE INSERT OR UPDATE OR DELETE ON trading.gas_index_cash_terms FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_component();
GRANT SELECT,INSERT ON trading.gas_index_cash_terms TO openetrm_security_owner;
GRANT SELECT ON trading.gas_index_cash_terms TO openetrm_runtime,openetrm_risk_worker;

-- The shared seal already checks typed operation arity, units, graph and bytes.
-- This admission proves the exact daily index, contractual spread, quantity,
-- observation and payment geometry represented by those ordinary operations.
CREATE FUNCTION trading.require_gas_index_cash_shape(catalog uuid,schedule uuid,source trading.gas_component_terms,a market_data.gas_valuation_assignments,terms jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE quote ref_data.quote_specifications;curve market_data.curves;target ref_data.settlement_targets;days integer=source.delivery_to-source.delivery_from;
 selection jsonb;custom jsonb;payment_day integer;spread numeric;day_number integer;prefix text;from_day date;payment timestamptz;unit_total numeric;
 q trading.side_schedule_rows;price_row trading.side_schedule_rows;contract market_data.contracts;fixing market_data.index_fixing_observations;
 delivery ref_data.gas_delivery_conventions;clock ref_data.clock_rules;day_end timestamptz;
BEGIN
 IF terms IS NULL OR jsonb_typeof(terms)<>'object' OR NOT(terms ?& ARRAY['index','observationConvention','paymentConvention','paymentDay','spread','forecastCurve']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(terms) k WHERE k NOT IN('index','observationConvention','paymentConvention','paymentDay','spread','forecastCurve')) OR
 terms->>'observationConvention' IS DISTINCT FROM 'daily_published_index_v1' OR terms->>'paymentConvention' IS DISTINCT FROM 'next_month_day_utc_v1' OR
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
 IF target.kind<>'index' OR terms->>'index' IS DISTINCT FROM target.index_a_code::text THEN RAISE EXCEPTION 'gas_index_terms';END IF;
 selection=jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',trading.gas_identity_reference(a.reference_catalog_id,a.reference_revision_id,target.index_a_code),
  'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',trading.gas_identity_reference(a.reference_catalog_id,a.reference_revision_id,target.index_a_code));
 custom=CASE WHEN terms->'forecastCurve'='null'::jsonb THEN NULL ELSE security.validate_custom_projection(catalog,terms->'forecastCurve') END;
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
  SELECT c.* INTO STRICT contract FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(m.catalog_id,m.revision_id,m.curve_code,c.kind,c.delivery_from,c.delivery_to)=(catalog,a.revision_id,a.curve_code,'forward',from_day,from_day+1);
  SELECT * INTO STRICT fixing FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,implementation)=
   (catalog,a.revision_id,target.index_a_code,from_day,from_day+1,quote.catalog_id,quote.revision_id,quote.code,'published_index_observations_v1');
  IF fixing.fixing_at>=payment THEN RAISE EXCEPTION 'gas_index_payment';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id,s.contract_id,s.reference_catalog_id,s.reference_revision_id,s.quote_code)=
    (catalog,a.revision_id,fixing.series_id,contract.id,quote.catalog_id,quote.revision_id,quote.code)) THEN RAISE EXCEPTION 'gas_index_sources';END IF;
  IF(price_row.observation_from,price_row.observation_to,price_row.unit_catalog_id,price_row.unit_revision_id,price_row.unit_code,price_row.currency_catalog_id,price_row.currency_revision_id,price_row.currency_code,price_row.quote_multiplier) IS DISTINCT FROM
   (from_day,from_day+1,quote.catalog_id,quote.revision_id,quote.quantity_code,quote.catalog_id,quote.revision_id,quote.currency_code,quote.multiplier) OR
   (price_row.parameters->>'selection')::jsonb IS DISTINCT FROM selection OR(custom IS NOT NULL AND(price_row.parameters->>'policy',price_row.parameters->>'from',price_row.parameters->>'to') IS DISTINCT FROM('containing_delivery_interval_v1',from_day::text,(from_day+1)::text))
  THEN RAISE EXCEPTION 'gas_index_sources';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)=
    (catalog,schedule,price_row.ordinal,'market','contract_price',catalog,a.revision_id,a.curve_code,contract.id)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.reference_catalog_id,b.reference_revision_id,b.index_code)=
    (catalog,schedule,price_row.ordinal,'index','index',quote.catalog_id,quote.revision_id,target.index_a_code)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.reference_catalog_id,b.reference_revision_id,b.quote_code)=
    (catalog,schedule,price_row.ordinal,'quote','quote',quote.catalog_id,quote.revision_id,quote.code)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.reference_catalog_id,b.reference_revision_id,b.source_catalog_id,b.definition_revision_id,b.series_id,b.source_code,b.fixing_index_code,b.fixing_from,b.fixing_to,b.coordinate)=
    (catalog,schedule,price_row.ordinal,'fixing','index_fixing',quote.catalog_id,quote.revision_id,catalog,a.revision_id,fixing.series_id,fixing.source_code,target.index_a_code,from_day,from_day+1,'published_index_observations_v1')) OR
   (custom IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id,b.row_ordinal,b.port,b.kind,b.custom_catalog_id,b.custom_curve_id,b.custom_version,b.output_node,b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)=
    (catalog,schedule,price_row.ordinal,'projection','custom_output',(custom->>'catalogId')::uuid,(custom->>'curveId')::uuid,(custom->>'version')::integer,custom->>'outputNode',catalog,a.revision_id,a.curve_code,contract.id) AND b.position IS NULL))
  THEN RAISE EXCEPTION 'gas_index_sources';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/application/0','{"denominator":"1","mapping":"calendar_day_arithmetic_v1","numerator":"1"}'::jsonb)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/average','{"totalScaledWeight":"1000000000000000000"}'::jsonb)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/discount','{"policy":"context_required_v1"}'::jsonb)) OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.parameters)=(catalog,schedule,prefix||'/value','{"averaging":"CalendarDayArithmetic","firstFixingAt":"none","lifecycle":"undue_payment_v1","measure":"present_value_v1","paymentRule":"explicit_payment_date_v1","pricePolicy":"selected_context_v1","unadjustedPayment":"none"}'::jsonb))
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
END$$;
ALTER FUNCTION trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb) FROM PUBLIC;

DO $patch$ DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamp with time zone)'::regprocedure);c record;BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$body->>'profile' IS DISTINCT FROM 'fixed_gas_composition_v1'$before$,$after$(body->>'profile' IS NULL OR body->>'profile' NOT IN('fixed_gas_composition_v1','indexed_gas_composition_v1'))$after$),
 ($before$ physical=body->'physicalSides';cash=body->'cashSides';$before$,$after$ physical=body->'physicalSides';cash=body->'cashSides';
 IF body->>'profile' IS DISTINCT FROM (CASE WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(cash) x WHERE x->>'role'='indexed_consideration') THEN 'indexed_gas_composition_v1' ELSE 'fixed_gas_composition_v1' END) THEN RAISE EXCEPTION 'gas_index_terms';END IF;$after$),
 ($before$k NOT IN('code','role','direction','source','stage','price','tariffCode','paymentAt')$before$,$after$k NOT IN('code','role','direction','source','stage','price','tariffCode','paymentAt','indexed')$after$),
 ($before$  IF price IS NULL OR abs(price)>=1e12 OR scale(price)>18 OR payment IS NULL OR NOT isfinite(payment) OR payment<end_at OR item->>'role' NOT IN('consideration','fee') THEN RAISE EXCEPTION 'gas_composition_cash';END IF;$before$,
 $after$  IF item->>'role'='indexed_consideration' THEN
   IF item->'price' IS DISTINCT FROM 'null'::jsonb OR item->'tariffCode' IS DISTINCT FROM 'null'::jsonb OR item->'paymentAt' IS DISTINCT FROM 'null'::jsonb OR item->'indexed' IS NULL OR item->'indexed'='null'::jsonb THEN RAISE EXCEPTION 'gas_index_terms';END IF;
  ELSIF price IS NULL OR abs(price)>=1e12 OR scale(price)>18 OR payment IS NULL OR NOT isfinite(payment) OR payment<end_at OR item->>'role' NOT IN('consideration','fee') OR item ? 'indexed' THEN RAISE EXCEPTION 'gas_composition_cash';END IF;$after$),
 ($before$  PERFORM trading.require_gas_program_shape(catalog,schedule,1,first_day,last_day,payment,unit_total,price,a,d,start_at);$before$,
 $after$  IF item->>'role'='indexed_consideration' THEN PERFORM trading.require_gas_index_cash_shape(catalog,schedule,source_term,a,item->'indexed');
  ELSE PERFORM trading.require_gas_program_shape(catalog,schedule,1,first_day,last_day,payment,unit_total,price,a,d,start_at);END IF;$after$),
 ($before$price,item->>'tariffCode',payment);
 END LOOP;$before$,$after$price,item->>'tariffCode',payment);
  IF item->>'role'='indexed_consideration' THEN
   INSERT INTO trading.gas_index_cash_terms VALUES(catalog,instrument,position,a.reference_catalog_id,a.reference_revision_id,item#>>'{indexed,index}',item#>>'{indexed,observationConvention}',item#>>'{indexed,paymentConvention}',
    (item#>>'{indexed,paymentDay}')::integer,(item#>>'{indexed,spread}')::numeric,CASE WHEN item#>'{indexed,forecastCurve}'='null'::jsonb THEN NULL ELSE security.retain_custom_projection(catalog,item#>'{indexed,forecastCurve}',publication,valuation) END);
  END IF;
 END LOOP;$after$)
 )v(before_text,after_text) LOOP
  IF(length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_index_upgrade: unexpected builder' USING ERRCODE='55000';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;EXECUTE definition;
END$patch$;
DO $patch$ DECLARE definition text=pg_get_functiondef('security.publish_admitted_side_schedules(uuid,uuid,uuid)'::regprocedure);
 before_text text=$before$CASE m.role WHEN 'physical' THEN 'gas' ELSE 'fixed' END$before$;
 after_text text=$after$CASE m.role WHEN 'physical' THEN 'gas' WHEN 'indexed_consideration' THEN 'floating' ELSE 'fixed' END$after$;
BEGIN
 IF(length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1 THEN RAISE EXCEPTION 'gas_index_upgrade: unexpected publisher' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before_text,after_text);
END$patch$;
COMMIT;
