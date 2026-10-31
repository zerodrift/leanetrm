-- Fixed contractual gas compositions, with independent directions and exact
-- native-unit quantity normalization. No transport rights or actuals inferred.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.gas_instruments ADD COLUMN composition boolean NOT NULL DEFAULT false,
 ALTER COLUMN assignment_code DROP NOT NULL,ALTER COLUMN delivery_from DROP NOT NULL,ALTER COLUMN delivery_to DROP NOT NULL,
 ALTER COLUMN quantity_basis DROP NOT NULL,ALTER COLUMN fixed_price DROP NOT NULL,ALTER COLUMN payment_at DROP NOT NULL;
ALTER TABLE trading.gas_instruments ADD CHECK(
 (NOT composition AND num_nulls(assignment_code,delivery_from,delivery_to,quantity_basis,fixed_price,payment_at)=0)
 OR(composition AND num_nonnulls(assignment_code,delivery_from,delivery_to,quantity_basis,fixed_price,payment_at,usage_fee,usage_fee_direction,usage_tariff_code)=0));
ALTER TABLE trading.gas_trade_revisions ALTER COLUMN direction DROP NOT NULL;
ALTER TABLE trading.gas_memberships DROP CONSTRAINT gas_memberships_slot_check,ADD CHECK(slot BETWEEN 0 AND 7);
CREATE TABLE trading.gas_component_terms (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL,code ref_data.code NOT NULL,role text NOT NULL CHECK(role IN('physical','consideration','fee')),
 direction integer NOT NULL CHECK(direction IN(-1,1)),ratio_n numeric NOT NULL,ratio_d numeric NOT NULL,receipt_n numeric NOT NULL,receipt_d numeric NOT NULL,
 source_slot integer,stage text CHECK(stage IN('receipt','delivery','fuel')),formula text CHECK(formula IN('none','gross_less_fraction','gross_divided_by_one_plus_fraction')),fraction numeric,
 assignment_code ref_data.code NOT NULL,definition_revision_id uuid NOT NULL,
 pipeline_code ref_data.code,zone_code ref_data.code,point_code ref_data.code,product_code ref_data.code,delivery_from date,delivery_to date,quantity_basis text,independent_quantity numeric,
 price numeric,tariff_code ref_data.code,payment_at timestamptz,
 PRIMARY KEY(catalog_id,instrument_id,slot),UNIQUE(catalog_id,instrument_id,code),
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.gas_memberships(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,instrument_id,source_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,definition_revision_id,assignment_code) REFERENCES market_data.gas_valuation_assignments(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,tariff_code) REFERENCES market_data.gas_usage_tariffs(catalog_id,revision_id,code),
 CHECK(ratio_n>0 AND ratio_d>0 AND receipt_n>0 AND receipt_d>0 AND greatest(ratio_n,ratio_d,receipt_n,receipt_d)<1e120 AND scale(ratio_n)=0 AND scale(ratio_d)=0 AND scale(receipt_n)=0 AND scale(receipt_d)=0),
 CHECK(fraction IS NULL OR(fraction>=0 AND fraction<1 AND scale(fraction)<=18)),
 CHECK(price IS NULL OR(abs(price)<1e12 AND scale(price)<=18)),
 CHECK(independent_quantity IS NULL OR(independent_quantity>0 AND independent_quantity<1e24 AND scale(independent_quantity)<=18)),
 CHECK(role<>'physical' OR(num_nulls(pipeline_code,zone_code,point_code,product_code,delivery_from,delivery_to,quantity_basis)=0 AND delivery_from<delivery_to AND delivery_to-delivery_from<=31 AND quantity_basis IN('period_total_equal_days','per_operating_day') AND stage IS NULL AND price IS NULL AND tariff_code IS NULL AND payment_at IS NULL AND
 ((source_slot IS NULL AND independent_quantity IS NOT NULL AND formula IS NULL AND fraction IS NULL) OR(source_slot IS NOT NULL AND independent_quantity IS NULL AND formula IS NOT NULL AND fraction IS NOT NULL)))),
 CHECK(role='physical' OR(source_slot IS NOT NULL AND stage IS NOT NULL AND formula IS NULL AND fraction IS NULL AND price IS NOT NULL AND isfinite(payment_at) AND num_nonnulls(pipeline_code,zone_code,point_code,product_code,delivery_from,delivery_to,quantity_basis,independent_quantity)=0))
);
CREATE FUNCTION trading.guard_gas_component() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$ BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Gas components are immutable' USING ERRCODE='55000';END IF;RETURN NEW;
END$$;
CREATE TRIGGER immutable_component BEFORE INSERT OR UPDATE OR DELETE ON trading.gas_component_terms FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_component();
ALTER TABLE trading.gas_component_terms ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_component_terms FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON trading.gas_component_terms TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON trading.gas_component_terms FOR SELECT TO openetrm_runtime USING(true);
-- Avoid a policy cycle through gas_instruments when proving all component sources.
CREATE FUNCTION security.gas_component_sources_visible(catalog uuid,instrument uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.trading_access(catalog,'ReadTrades') AND security.market_access(catalog,'ReadMarketData') AND EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument) AND i.composition AND security.trading_visible(catalog,i.publication_order,i.effective_at)) AND EXISTS(SELECT 1 FROM trading.gas_component_terms WHERE(catalog_id,instrument_id)=(catalog,instrument)) AND NOT EXISTS(
 SELECT 1 FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND NOT EXISTS(
 SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code)
 AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) AND security.gas_setup_visible(a.catalog_id,a.setup_id)))
$$;
CREATE POLICY current_authority ON trading.gas_component_terms AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,'ReadTrades') AND EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(gas_component_terms.catalog_id,gas_component_terms.instrument_id)));
GRANT SELECT ON trading.gas_component_terms TO openetrm_runtime;
GRANT SELECT,INSERT ON trading.gas_component_terms TO openetrm_security_owner;
DROP POLICY current_authority ON trading.gas_instruments;
CREATE POLICY current_authority ON trading.gas_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND
 (composition AND security.gas_component_sources_visible(catalog_id,instrument_id) OR NOT composition AND EXISTS(SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(gas_instruments.catalog_id,gas_instruments.definition_revision_id,gas_instruments.assignment_code))));
CREATE FUNCTION security.book_gas_composition(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE q security.request_contexts;prior trading.trades;stage trading.side_schedule_requests;operation uuid=(body->>'operationId')::uuid;definition uuid=(body->>'definitionRevisionId')::uuid;setup uuid=(body->>'gasSetupId')::uuid;
 book uuid=(body->>'bookId')::uuid;instrument uuid;trade uuid;publication integer;base numeric;physical jsonb;cash jsonb;item jsonb;source jsonb;position integer;source_position integer;n integer;sign integer;
 a market_data.gas_valuation_assignments;d ref_data.gas_delivery_conventions;clock ref_data.clock_rules;t market_data.gas_usage_tariffs;curve market_data.curves;
 first_day date;last_day date;start_at timestamptz;end_at timestamptz;payment timestamptz;unit_total numeric;price numeric;rn numeric;rd numeric;inn numeric;ind numeric;f numeric;fn numeric;fd numeric;g numeric;source_term trading.gas_component_terms;schedule uuid;unresolved integer;processed integer;codes text[];
BEGIN
 IF NOT security.trading_access(catalog,'BookTrade') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No composition booking access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>q.actor_id OR prior.request IS DISTINCT FROM body OR NOT EXISTS(SELECT 1 FROM trading.gas_instruments i JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.instrument_id)=(i.catalog_id,i.instrument_id) WHERE(r.catalog_id,r.operation_id)=(catalog,operation) AND i.composition) THEN RAISE EXCEPTION 'gas_conflict';END IF;
  RETURN security.gas_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'gas_conflict';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR body->>'profile' IS DISTINCT FROM 'fixed_gas_composition_v1' OR body->>'convention' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR body->>'knowledgeMode' IS DISTINCT FROM 'current' OR body->'discounting' IS DISTINCT FROM 'false'::jsonb OR NOT(body ? 'gasSetupId') OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('snapshotId','convention','knowledgeMode','profile','operationId','bookId','definitionRevisionId','gasSetupId','discounting','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','physicalSides','cashSides')) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(r.catalog_id,r.id) WHERE(r.catalog_id,r.id,r.state)=(catalog,definition,'sealed') AND p.effective_from<=(valuation AT TIME ZONE 'UTC')::date AND (valuation AT TIME ZONE 'UTC')::date<p.effective_to) THEN RAISE EXCEPTION 'gas_definition';END IF;
 physical=body->'physicalSides';cash=body->'cashSides';
 IF jsonb_typeof(physical) IS DISTINCT FROM 'array' OR jsonb_typeof(cash) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF jsonb_array_length(physical) NOT BETWEEN 1 AND 4 OR jsonb_array_length(cash)>4 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 SELECT array_agg(x->>'code') INTO codes FROM(SELECT value x FROM jsonb_array_elements(physical) UNION ALL SELECT value FROM jsonb_array_elements(cash)) r;
 IF EXISTS(SELECT 1 FROM unnest(codes) x WHERE x IS NULL OR x!~'^[a-z][a-z0-9_.-]{0,63}$') OR cardinality(codes)<>(SELECT count(DISTINCT x) FROM unnest(codes) x) THEN RAISE EXCEPTION 'gas_composition_codes';END IF;
 SELECT (x->>'quantity')::numeric INTO base FROM jsonb_array_elements(physical) x WHERE x->'link'='null'::jsonb LIMIT 1;
 IF base IS NULL OR base<=0 OR base>=1e24 OR scale(base)>18 THEN RAISE EXCEPTION 'gas_composition_quantity';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(physical) x WHERE x->'link'<>'null'::jsonb GROUP BY x->'link'->>'source' HAVING count(*)>1) THEN RAISE EXCEPTION 'gas_composition_split';END IF;
 IF valuation IS NULL OR NOT isfinite(valuation) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 SELECT * INTO stage FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,operation);
 IF NOT FOUND OR stage.actor_id<>q.actor_id OR stage.permission<>'BookTrade' OR stage.command_hash<>sha256(convert_to(body::text,'UTF8')) THEN RAISE EXCEPTION 'gas_schedule';END IF;
 PERFORM security.lock_side_schedule_request(catalog,operation);
 IF (SELECT count(*) FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,operation))<>jsonb_array_length(physical)+jsonb_array_length(cash) OR EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)) THEN RAISE EXCEPTION 'gas_schedule';END IF;
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date,'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision'),valuation);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=valuation AND p.effective_at<=valuation) THEN RAISE EXCEPTION 'gas_book';END IF;
 IF(SELECT count(*) FROM trading.gas_trade_revisions WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'gas_limit';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());instrument=gen_random_uuid();trade=gen_random_uuid();
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.gas_instruments(catalog_id,instrument_id,definition_revision_id,gas_setup_id,composition,publication_order,effective_at) VALUES(catalog,instrument,definition,setup,true,publication,valuation);
 INSERT INTO trading.gas_memberships SELECT catalog,instrument,i.slot,h.side_id FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id) WHERE(i.catalog_id,i.request_id)=(catalog,operation);
 unresolved=jsonb_array_length(physical);
 FOR n IN 1..jsonb_array_length(physical) LOOP
  processed=0;
  FOR item,position IN SELECT value,(ordinality-1)::integer FROM jsonb_array_elements(physical) WITH ORDINALITY LOOP
   IF EXISTS(SELECT 1 FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,position)) THEN CONTINUE;END IF;
   IF NOT(item ?& ARRAY['code','pipeline','zone','deliveryPoint','product','direction','deliveryFrom','deliveryTo','quantityBasis','quantity','link']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(item) k WHERE k NOT IN('code','pipeline','zone','deliveryPoint','product','direction','deliveryFrom','deliveryTo','quantityBasis','quantity','link')) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   source_position=NULL;
   IF item->'link'<>'null'::jsonb THEN
    IF jsonb_typeof(item->'link') IS DISTINCT FROM 'object' OR NOT(item->'link' ?& ARRAY['source','formula','fraction']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(item->'link') k WHERE k NOT IN('source','formula','fraction')) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
    SELECT (ordinality-1)::integer INTO source_position FROM jsonb_array_elements(physical) WITH ORDINALITY s WHERE value->>'code'=item->'link'->>'source';
    IF source_position IS NULL THEN RAISE EXCEPTION 'gas_composition_source';END IF;
    SELECT * INTO source_term FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,source_position);
    IF NOT FOUND THEN CONTINUE;END IF;
   END IF;
   SELECT * INTO a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)=(catalog,definition,item->>'pipeline',item->>'zone',item->>'deliveryPoint',item->>'product') AND setup_id IS NOT DISTINCT FROM setup;
   IF NOT FOUND OR NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) OR NOT security.gas_setup_visible(catalog,setup) THEN RAISE EXCEPTION 'gas_definition';END IF;
   SELECT * INTO STRICT d FROM security.gas_setup_delivery(a);
   SELECT * INTO STRICT clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,d.clock_code);
   IF clock.kind<>'fixed_offset' THEN RAISE EXCEPTION 'gas_clock';END IF;
   first_day=(item->>'deliveryFrom')::date;last_day=(item->>'deliveryTo')::date;
   IF first_day IS NULL OR last_day IS NULL OR NOT isfinite(first_day) OR NOT isfinite(last_day) OR first_day>=last_day OR last_day-first_day>31 OR first_day<date '2000-01-01' OR last_day>date '2100-01-01' OR item->>'quantityBasis' NOT IN('period_total_equal_days','per_operating_day') THEN RAISE EXCEPTION 'gas_composition_geometry';END IF;
   start_at=first_day::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>d.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
   end_at=last_day::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>d.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
   IF valuation>=start_at THEN RAISE EXCEPTION 'gas_delivery_lifecycle';END IF;
   sign=CASE item->>'direction' WHEN 'receive' THEN 1 WHEN 'deliver' THEN -1 END;
   IF sign IS NULL THEN RAISE EXCEPTION 'gas_composition_direction';END IF;
   IF source_position IS NULL THEN
    IF item->>'quantity' IS NULL OR (item->>'quantity')::numeric<=0 OR (item->>'quantity')::numeric>=1e24 OR scale((item->>'quantity')::numeric)>18 THEN RAISE EXCEPTION 'gas_composition_quantity';END IF;
    rn=(item->>'quantity')::numeric*1e18;rd=base*1e18;g=gcd(rn,rd);rn=trim_scale(div(rn,g));rd=trim_scale(div(rd,g));inn=rn;ind=rd;f=NULL;
   ELSE
    IF item->>'quantity' IS NOT NULL OR (source_term.delivery_from,source_term.delivery_to,source_term.quantity_basis,source_term.product_code) IS DISTINCT FROM(first_day,last_day,item->>'quantityBasis',a.product_code) THEN RAISE EXCEPTION 'gas_composition_geometry';END IF;
    SELECT * INTO source FROM(SELECT to_jsonb(x) FROM security.gas_setup_delivery((SELECT x FROM market_data.gas_valuation_assignments x WHERE(x.catalog_id,x.revision_id,x.code)=(catalog,definition,source_term.assignment_code))) x) r;
    IF (source->>'clock_code',source->>'day_start_minute') IS DISTINCT FROM(d.clock_code::text,d.day_start_minute::text) THEN RAISE EXCEPTION 'gas_composition_geometry';END IF;
    f=(item->'link'->>'fraction')::numeric;
    IF f IS NULL OR f<0 OR f>=1 OR scale(f)>18 THEN RAISE EXCEPTION 'gas_composition_fuel';END IF;
    inn=source_term.ratio_n;ind=source_term.ratio_d;
    CASE item->'link'->>'formula'
     WHEN 'none' THEN IF f<>0 THEN RAISE EXCEPTION 'gas_composition_fuel';END IF;fn=1;fd=1;
     WHEN 'gross_less_fraction' THEN fn=(1-f)*1e18;fd=1e18;
     WHEN 'gross_divided_by_one_plus_fraction' THEN fn=1e18;fd=(1+f)*1e18;
     ELSE RAISE EXCEPTION 'gas_composition_fuel';
    END CASE;
    rn=inn*fn;rd=ind*fd;g=gcd(rn,rd);rn=trim_scale(div(rn,g));rd=trim_scale(div(rd,g));
   END IF;
   SELECT schedule_id INTO STRICT schedule FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,operation,position);
   unit_total=CASE item->>'quantityBasis' WHEN 'per_operating_day' THEN last_day-first_day ELSE 1 END;
   PERFORM trading.require_gas_program_shape(catalog,schedule,0,first_day,last_day,end_at,unit_total,NULL,a,d,start_at);
   INSERT INTO trading.gas_component_terms(catalog_id,instrument_id,slot,code,role,direction,ratio_n,ratio_d,receipt_n,receipt_d,source_slot,formula,fraction,assignment_code,definition_revision_id,pipeline_code,zone_code,point_code,product_code,delivery_from,delivery_to,quantity_basis,independent_quantity)
   VALUES(catalog,instrument,position,item->>'code','physical',sign,rn,rd,inn,ind,source_position,item->'link'->>'formula',f,a.code,definition,a.pipeline_code,a.zone_code,a.point_code,a.product_code,first_day,last_day,item->>'quantityBasis',(item->>'quantity')::numeric);
   processed=processed+1;unresolved=unresolved-1;
  END LOOP;
  IF unresolved=0 THEN EXIT;END IF;
  IF processed=0 THEN RAISE EXCEPTION 'gas_composition_cycle';END IF;
 END LOOP;
 FOR item,position IN SELECT value,(ordinality-1+jsonb_array_length(physical))::integer FROM jsonb_array_elements(cash) WITH ORDINALITY LOOP
  IF NOT(item ?& ARRAY['code','role','direction','source','stage','price','tariffCode','paymentAt']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(item) k WHERE k NOT IN('code','role','direction','source','stage','price','tariffCode','paymentAt')) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  SELECT * INTO source_term FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,code,role)=(catalog,instrument,item->>'source','physical');
  IF NOT FOUND THEN RAISE EXCEPTION 'gas_composition_source';END IF;
  SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,definition,source_term.assignment_code);
  SELECT * INTO STRICT d FROM security.gas_setup_delivery(a);
  SELECT * INTO STRICT clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,d.clock_code);
  first_day=source_term.delivery_from;last_day=source_term.delivery_to;
  start_at=first_day::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>d.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
  end_at=last_day::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>d.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
  payment=(item->>'paymentAt')::timestamptz;price=(item->>'price')::numeric;
  IF item->>'tariffCode' IS NOT NULL THEN
   IF price IS NOT NULL OR item->>'role'<>'fee' OR item->>'stage'<>'delivery' THEN RAISE EXCEPTION 'gas_tariff';END IF;
   SELECT * INTO STRICT curve FROM market_data.curves WHERE(catalog_id,revision_id,code)=(catalog,definition,a.curve_code);
   SELECT * INTO t FROM market_data.gas_usage_tariffs WHERE(catalog_id,revision_id,code,pipeline_code,product_code,quote_code)=(catalog,definition,item->>'tariffCode',a.pipeline_code,a.product_code,curve.quote_code) AND setup_id IS NOT DISTINCT FROM setup AND valid_from<=first_day AND last_day<=valid_to;
   IF NOT FOUND THEN RAISE EXCEPTION 'gas_tariff';END IF;price=t.rate;
  END IF;
  IF price IS NULL OR abs(price)>=1e12 OR scale(price)>18 OR payment IS NULL OR NOT isfinite(payment) OR payment<end_at OR item->>'role' NOT IN('consideration','fee') THEN RAISE EXCEPTION 'gas_composition_cash';END IF;
  sign=CASE item->>'direction' WHEN 'receive' THEN 1 WHEN 'pay' THEN -1 END;IF sign IS NULL THEN RAISE EXCEPTION 'gas_composition_direction';END IF;
  CASE item->>'stage'
   WHEN 'receipt' THEN rn=source_term.receipt_n;rd=source_term.receipt_d;
   WHEN 'delivery' THEN rn=source_term.ratio_n;rd=source_term.ratio_d;
   WHEN 'fuel' THEN rn=source_term.receipt_n*source_term.ratio_d-source_term.ratio_n*source_term.receipt_d;rd=source_term.receipt_d*source_term.ratio_d;IF rn<=0 THEN RAISE EXCEPTION 'gas_composition_quantity';END IF;g=gcd(rn,rd);rn=trim_scale(div(rn,g));rd=trim_scale(div(rd,g));
   ELSE RAISE EXCEPTION 'gas_composition_quantity';
  END CASE;
  SELECT schedule_id INTO STRICT schedule FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,operation,position);
  unit_total=CASE source_term.quantity_basis WHEN 'per_operating_day' THEN last_day-first_day ELSE 1 END;
  PERFORM trading.require_gas_program_shape(catalog,schedule,1,first_day,last_day,payment,unit_total,price,a,d,start_at);
  INSERT INTO trading.gas_component_terms(catalog_id,instrument_id,slot,code,role,direction,ratio_n,ratio_d,receipt_n,receipt_d,source_slot,stage,assignment_code,definition_revision_id,price,tariff_code,payment_at)
  VALUES(catalog,instrument,position,item->>'code',item->>'role',sign,rn,rd,rn,rd,source_term.slot,item->>'stage',a.code,definition,price,item->>'tariffCode',payment);
 END LOOP;
 -- A common exact quote is required; ratios never perform implicit unit or FX conversion.
 IF EXISTS(SELECT 1 FROM trading.gas_component_terms m JOIN market_data.gas_valuation_assignments assignment_meta ON(assignment_meta.catalog_id,assignment_meta.revision_id,assignment_meta.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code) JOIN market_data.curves curve_meta ON(curve_meta.catalog_id,curve_meta.revision_id,curve_meta.code)=(assignment_meta.catalog_id,assignment_meta.revision_id,assignment_meta.curve_code)
 JOIN ref_data.quote_specifications quote_meta ON(quote_meta.catalog_id,quote_meta.revision_id,quote_meta.code)=(assignment_meta.reference_catalog_id,assignment_meta.reference_revision_id,curve_meta.quote_code) WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)
 GROUP BY m.catalog_id,m.instrument_id HAVING count(DISTINCT(quote_meta.quantity_code,quote_meta.currency_code))>1) THEN RAISE EXCEPTION 'gas_composition_units';END IF;
 PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades VALUES(catalog,trade,book,operation,q.actor_id,body,publication,valuation);
 INSERT INTO trading.gas_trade_revisions VALUES(catalog,trade,1,instrument,book,operation,q.actor_id,base,NULL,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation);
 IF NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'Composition booking authority expired' USING ERRCODE='42501';END IF;
 RETURN security.gas_operation_status(catalog,operation);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('gas_component_sources_visible','book_gas_composition') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.gas_component_sources_visible(uuid,uuid),security.book_gas_composition(uuid,jsonb,timestamptz) TO openetrm_runtime;
DO $$DECLARE source text;BEGIN
 source=pg_get_functiondef('security.publish_side_schedules(uuid,uuid,uuid)'::regprocedure);
 IF strpos(source,'WHEN candidate.gas_side_id IS NOT NULL THEN CASE item.slot WHEN 0 THEN ''gas'' ELSE ''fixed'' END')=0 THEN RAISE EXCEPTION 'gas_composition_publication_upgrade';END IF;
 source=replace(source,'WHEN candidate.gas_side_id IS NOT NULL THEN CASE item.slot WHEN 0 THEN ''gas'' ELSE ''fixed'' END',
  'WHEN candidate.gas_side_id IS NOT NULL THEN coalesce((SELECT CASE m.role WHEN ''physical'' THEN ''gas'' ELSE ''fixed'' END FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id,m.slot)=(catalog,instrument,item.slot)),CASE item.slot WHEN 0 THEN ''gas'' ELSE ''fixed'' END)');
 EXECUTE source;
END$$;
COMMIT;
