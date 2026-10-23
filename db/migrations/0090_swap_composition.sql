-- Drain runtime writes before applying. Earlier definitions and events remain exact.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.swap_floating_sides ADD COLUMN spread ref_data.exact_number NOT NULL DEFAULT 0;
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_economic_key;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_floating_economic_key UNIQUE NULLS NOT DISTINCT
 (catalog_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,
  delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,payment_rule_id,custom_projection_id,spread);
ALTER TABLE trading.swap_instruments ALTER COLUMN fixed_side_id DROP NOT NULL;
ALTER TABLE trading.swap_instruments ADD COLUMN first_floating_side_id uuid,
 ADD FOREIGN KEY(catalog_id,first_floating_side_id) REFERENCES trading.swap_floating_sides(catalog_id,id),
 ADD CHECK((fixed_side_id IS NULL)<>(first_floating_side_id IS NULL));
CREATE UNIQUE INDEX swap_floating_pair_key ON trading.swap_instruments(catalog_id,first_floating_side_id,floating_side_id,fixed_coefficient,discounting,implementation)
 WHERE first_floating_side_id IS NOT NULL;

-- Private helper: validate and retain one independent floating side. Only the
-- fenced business command below can invoke it; runtime has no direct execute.
CREATE FUNCTION security.retain_floating_swap_side(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz,input_pins uuid[])
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cv market_data.curves;q ref_data.quote_specifications;target ref_data.settlement_targets;k market_data.contracts;
 side uuid;rule uuid;starts date;ends date;fixing timestamptz;payment timestamptz;sampling text;spread_value numeric;fixing_evidence jsonb;custom_projection jsonb;custom_binding uuid;
BEGIN
 IF body IS NULL OR jsonb_typeof(body)<>'object' OR body->>'paymentAt' IS NOT NULL OR body->>'averaging' IS NOT NULL OR body->'paymentSchedule' IS NULL OR body->>'spread' IS NULL OR body->>'firstFixingAt' IS NOT NULL
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('curve','index','currency','deliveryFrom','deliveryTo','firstFixingAt','paymentAt','averaging','paymentSchedule','resetFrequency','averagingMethod','spread','customProjection')) THEN RAISE EXCEPTION 'swap_input';END IF;
 starts=(body->>'deliveryFrom')::date;ends=(body->>'deliveryTo')::date;spread_value=(body->>'spread')::numeric;
 sampling=CASE WHEN(body->>'resetFrequency',body->>'averagingMethod')=('monthly','none') THEN 'monthly_index_price' WHEN(body->>'resetFrequency',body->>'averagingMethod')=('daily','arithmetic') THEN 'calendar_day_arithmetic' END;
 payment=security.swap_payment_date(ref_catalog,ref_revision,starts,ends,body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
 IF starts IS NULL OR ends IS NULL OR NOT isfinite(starts) OR NOT isfinite(ends) OR extract(day FROM starts)<>1 OR ends IS DISTINCT FROM(starts+INTERVAL '1 month')::date OR sampling IS NULL OR
  payment IS NULL OR valuation>=payment OR
  spread_value IS NULL OR abs(spread_value)>=1e24 OR scale(spread_value)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
 SELECT * INTO cv FROM market_data.curves WHERE catalog_id=catalog AND revision_id=definition AND code=body->>'curve' AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.quote_code);
 SELECT * INTO target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.settlement_target_code);
 IF cv.commodity_code IS DISTINCT FROM commodity OR q.kind IS DISTINCT FROM 'outright' OR target.kind IS DISTINCT FROM 'index' OR target.index_a_code IS DISTINCT FROM body->>'index' OR q.currency_code IS DISTINCT FROM body->>'currency' OR
  NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,body->>'index') AND(i.currency_code,i.commodity_code,i.location_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,cv.location_code,u.unit_code,q.multiplier)) THEN RAISE EXCEPTION 'swap_definition';END IF;
 fixing_evidence=security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,cv.code,body->>'index',starts,ends,sampling,valuation,payment,input_pins);
 fixing=(fixing_evidence->>'firstFixing')::timestamp AT TIME ZONE 'UTC';
 custom_projection=security.validate_custom_projection(catalog,body->'customProjection');
 IF custom_projection IS NOT NULL AND((sampling='monthly_index_price' AND custom_projection->>'selection'<>'single_output_position_v1') OR(sampling='calendar_day_arithmetic' AND custom_projection->>'selection'<>'exact_delivery_interval_v1') OR EXISTS(
  SELECT 1 FROM security.custom_curve_native_sources(catalog,(custom_projection->>'curveId')::uuid,(custom_projection->>'version')::integer) p WHERE(p.source_catalog_id,p.source_revision_id) IS DISTINCT FROM(catalog,definition))) THEN RAISE EXCEPTION 'custom_projection';END IF;
 custom_binding=security.retain_custom_projection(catalog,custom_projection,publication_number,from_at);
 IF(SELECT count(*) FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
  WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
   (sampling='monthly_index_price' AND(c.delivery_from,c.delivery_to)=(starts,ends) OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends))<>(CASE WHEN sampling='monthly_index_price' THEN 1 ELSE ends-starts END) OR
  sampling='calendar_day_arithmetic' AND(SELECT count(DISTINCT c.delivery_from) FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends)<>ends-starts THEN RAISE EXCEPTION 'swap_projection';END IF;
 IF sampling='monthly_index_price' THEN
  SELECT c.* INTO STRICT k FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND(c.delivery_from,c.delivery_to)=(starts,ends);
 END IF;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 SELECT id INTO side FROM trading.swap_floating_sides s WHERE s.catalog_id=catalog AND s.definition_revision_id=definition AND s.curve_code=cv.code AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.index_code=body->>'index' AND s.quote_code=q.code AND s.delivery_from=starts AND s.delivery_to=ends AND s.first_fixing_at=fixing AND s.payment_at=payment AND s.averaging=sampling AND s.payment_rule_id=rule AND s.spread=spread_value AND s.implementation='index_observation_fixings_v1' AND s.custom_projection_id IS NOT DISTINCT FROM custom_binding;
 IF side IS NULL THEN
  side=gen_random_uuid();
  INSERT INTO trading.swap_floating_sides VALUES(catalog,side,definition,cv.code,k.id,ref_catalog,ref_revision,body->>'index',q.code,starts,ends,fixing,payment,sampling,'index_observation_fixings_v1',publication_number,from_at,rule,custom_binding,spread_value);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,side,(row_number() OVER(ORDER BY c.delivery_from,c.id)-1)::integer,definition,cv.code,c.id,1,publication_number,from_at
   FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
    (sampling='monthly_index_price' AND c.id=k.id OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends);
 END IF;
 IF EXISTS(SELECT 1 FROM(SELECT p.ordinal,p.definition_revision_id,p.curve_code,p.contract_id,p.weight FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(catalog,side)) retained FULL JOIN
  (SELECT(row_number() OVER(ORDER BY c.delivery_from,c.id)-1)::integer AS ordinal,definition AS definition_revision_id,cv.code AS curve_code,c.id AS contract_id,1::numeric AS weight FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND(sampling='monthly_index_price' AND c.id=k.id OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends)) expected USING(ordinal)
  WHERE retained.ordinal IS NULL OR expected.ordinal IS NULL OR(retained.definition_revision_id,retained.curve_code,retained.contract_id,retained.weight) IS DISTINCT FROM(expected.definition_revision_id,expected.curve_code,expected.contract_id,expected.weight)) THEN RAISE EXCEPTION 'swap_projection';END IF;
 RETURN side;
END $$;

CREATE FUNCTION security.save_composed_swap(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,
 fixed_body jsonb,first_body jsonb,float_body jsonb,quantity numeric,coefficient numeric,discounting boolean,valuation timestamptz,effective timestamptz,reason text,implementation text,memo text,booking jsonb,input_pins uuid[])
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;request_value jsonb;permission text;publication_number integer;
 first_side uuid;floating_side uuid;fixed_side uuid;instrument uuid;binding uuid;from_at timestamptz;executed timestamptz;q ref_data.quote_specifications;other_quote ref_data.quote_specifications;rule uuid;payment timestamptz;price numeric;
BEGIN
 permission=CASE WHEN trade IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR implementation IS DISTINCT FROM 'independent_side_financial_swap_v2' OR
  (fixed_body IS NULL)=(first_body IS NULL) OR discounting IS NULL OR coefficient IS NULL OR coefficient NOT IN(-1,1) OR quantity IS NULL OR quantity<=0 OR abs(quantity)>=1e24 OR scale(quantity)>18 OR
  valuation IS NULL OR effective IS NULL OR NOT isfinite(valuation) OR NOT isfinite(effective) OR effective>valuation OR booking->>'tradeDate' IS NOT NULL THEN RAISE EXCEPTION 'swap_input';END IF;
 request_value=jsonb_build_object('trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,'commodity',commodity,
  'fixed',fixed_body||jsonb_build_object('price',(fixed_body->>'price')::numeric),'firstFloating',first_body||jsonb_build_object('spread',(first_body->>'spread')::numeric),'floating',float_body||jsonb_build_object('spread',(float_body->>'spread')::numeric),'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation,'effective',effective,'reason',reason,'implementation',implementation,'memo',memo,'booking',booking,'schema','independent_swap_sides_v1');
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request-'sideFixings'<>request_value THEN RAISE EXCEPTION 'swap_conflict';END IF;
  PERFORM security.validate_custom_projection(catalog,first_body->'customProjection');
  PERFORM security.validate_custom_projection(catalog,float_body->'customProjection');
  RETURN security.swap_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation) THEN RAISE EXCEPTION 'swap_conflict';END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 executed=valuation;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade';END IF;
  IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
  IF head.revision>=256 OR effective IS DISTINCT FROM valuation OR effective<head.effective_at OR reason IS NULL OR length(btrim(reason)) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'swap_amendment';END IF;
  IF EXISTS(SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
   LEFT JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id) LEFT JOIN trading.swap_floating_sides a ON(a.catalog_id,a.id)=(i.catalog_id,i.first_floating_side_id)
   WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND(valuation>=s.first_fixing_at OR valuation>=s.payment_at OR valuation>=f.payment_at OR valuation>=a.first_fixing_at OR valuation>=a.payment_at OR (f.id IS NULL)<>(fixed_body IS NULL))) THEN RAISE EXCEPTION 'swap_amendment';END IF;
  executed=head.execution_at;
  booking=booking||jsonb_build_object('tradeDate',coalesce(head.trade_date,(SELECT f.trade_date FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(catalog,trade))));
 ELSE
  IF effective IS DISTINCT FROM executed OR expected IS NOT NULL OR(SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'swap_limit';END IF;
  booking=booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date);
 END IF;
 PERFORM security.require_trade_annotation(catalog,head.trader_id,head.trader_revision,memo,executed);
 PERFORM security.require_trade_parties(catalog,booking,executed);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=executed AND p.effective_at<=executed) THEN RAISE EXCEPTION 'swap_book';END IF;
 SELECT effective_from::timestamp AT TIME ZONE 'UTC' INTO from_at FROM market_data.revision_periods WHERE catalog_id=catalog AND revision_id=definition AND effective_from<=(executed AT TIME ZONE 'UTC')::date AND(executed AT TIME ZONE 'UTC')::date<effective_to;
 IF from_at IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE catalog_id=catalog AND id=definition AND state='sealed') THEN RAISE EXCEPTION 'swap_definition';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication_number FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication_number,pg_current_xact_id());
 floating_side=security.retain_floating_swap_side(catalog,definition,ref_catalog,ref_revision,commodity,float_body,valuation,publication_number,from_at,input_pins);
 SELECT q1.* INTO q FROM trading.swap_floating_sides s JOIN ref_data.quote_specifications q1 ON(q1.catalog_id,q1.revision_id,q1.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) WHERE(s.catalog_id,s.id)=(catalog,floating_side);
 IF first_body IS NOT NULL THEN
  first_side=security.retain_floating_swap_side(catalog,definition,ref_catalog,ref_revision,commodity,first_body,valuation,publication_number,from_at,input_pins);
  SELECT q1.* INTO other_quote FROM trading.swap_floating_sides s JOIN ref_data.quote_specifications q1 ON(q1.catalog_id,q1.revision_id,q1.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) WHERE(s.catalog_id,s.id)=(catalog,first_side);
  IF(other_quote.currency_code,other_quote.quantity_code) IS DISTINCT FROM(q.currency_code,q.quantity_code) THEN RAISE EXCEPTION 'swap_definition';END IF;
 ELSE
  IF jsonb_typeof(fixed_body) IS DISTINCT FROM 'object' OR EXISTS(SELECT 1 FROM jsonb_object_keys(fixed_body) key WHERE key NOT IN('price','currency','deliveryFrom','deliveryTo','paymentAt','paymentSchedule')) OR fixed_body->>'paymentAt' IS NOT NULL OR fixed_body->>'currency' IS DISTINCT FROM q.currency_code THEN RAISE EXCEPTION 'swap_input';END IF;
  price=(fixed_body->>'price')::numeric;
  IF price IS NULL OR abs(price)>=1e24 OR scale(price)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
  payment=security.swap_payment_date(ref_catalog,ref_revision,(fixed_body->>'deliveryFrom')::date,(fixed_body->>'deliveryTo')::date,fixed_body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
  IF payment IS NULL OR valuation>=payment THEN RAISE EXCEPTION 'swap_input';END IF;
  rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,fixed_body->'paymentSchedule',publication_number,from_at);
  SELECT id INTO fixed_side FROM trading.swap_fixed_sides s WHERE s.catalog_id=catalog AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.quote_code=q.code AND s.delivery_from=(fixed_body->>'deliveryFrom')::date AND s.delivery_to=(fixed_body->>'deliveryTo')::date AND s.payment_at=payment AND s.fixed_price=price AND s.payment_rule_id=rule;
  IF fixed_side IS NULL THEN
   fixed_side=gen_random_uuid();INSERT INTO trading.swap_fixed_sides VALUES(catalog,fixed_side,ref_catalog,ref_revision,q.code,(fixed_body->>'deliveryFrom')::date,(fixed_body->>'deliveryTo')::date,payment,price,'fixed_total_payment_v1',publication_number,from_at,rule);
  END IF;
 END IF;
 request_value=request_value||jsonb_build_object('sideFixings',(
  SELECT jsonb_agg(jsonb_build_object('slot',selected.slot,'sideId',s.id,'evidence',security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,s.curve_code,s.index_code,s.delivery_from,s.delivery_to,s.averaging,valuation,s.payment_at,input_pins)) ORDER BY selected.slot)
  FROM(VALUES(0,first_side),(1,floating_side)) selected(slot,id) JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(catalog,selected.id)));
 IF trade IS NOT NULL AND EXISTS(SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id) WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND(s.reference_catalog_id,s.reference_revision_id,s.quote_code) IS DISTINCT FROM(ref_catalog,ref_revision,q.code)) THEN RAISE EXCEPTION 'swap_amendment';END IF;
 SELECT i.instrument_id INTO instrument FROM trading.swap_instruments i WHERE i.catalog_id=catalog AND i.fixed_side_id IS NOT DISTINCT FROM fixed_side AND i.first_floating_side_id IS NOT DISTINCT FROM first_side AND i.floating_side_id=floating_side AND i.fixed_coefficient=coefficient AND i.discounting=save_composed_swap.discounting;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments VALUES(catalog,instrument,fixed_side,floating_side,coefficient,discounting,implementation,publication_number,from_at,first_side);
 END IF;
 IF trade IS NULL THEN
  trade=gen_random_uuid();binding=gen_random_uuid();booking=booking||jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request_value,publication_number,executed);
 ELSE
  IF(book,instrument,quantity,memo,booking) IS NOT DISTINCT FROM(head.book_id,head.instrument_id,head.quantity,head.memo,security.trade_party_payload(head.trade_date,head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision)) THEN RAISE EXCEPTION 'swap_no_change';END IF;
  binding=CASE WHEN instrument=head.instrument_id THEN head.binding_id ELSE gen_random_uuid() END;
 END IF;
 INSERT INTO trading.swap_trade_revisions VALUES(catalog,trade,coalesce(head.revision,0)+1,head.revision,gen_random_uuid(),'monthly_financial_swap_booking_v2',CASE WHEN head.revision IS NULL THEN 'booked' ELSE 'amended' END,
  book,binding,instrument,quantity,executed,effective,operation,c.actor_id,CASE WHEN head.revision IS NULL THEN 'Initial booking' ELSE btrim(reason) END,request_value,publication_number,head.trader_id,head.trader_revision,memo,(booking->>'tradeDate')::date,(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,(booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 PERFORM security.validate_custom_projection(catalog,first_body->'customProjection');
 PERFORM security.validate_custom_projection(catalog,float_body->'customProjection');
 RETURN security.swap_operation_status(catalog,operation);
END $$;
DO $$ DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('retain_floating_swap_side','save_composed_swap') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname='save_composed_swap' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END $$;

-- Retained legacy operations keep their exact command/retry policies. Restrict
-- their reusable side lookup to the zero spread they implement.
DO $$DECLARE f record;definition text;before text;BEGIN
 FOR f IN SELECT p.oid::regprocedure AS signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('save_swap_with_parties','save_projected_swap_with_parties') LOOP
  definition=pg_get_functiondef(f.signature);
  before=CASE f.proname WHEN 'save_swap_with_parties' THEN 'AND payment_rule_id=float_rule;' ELSE 'AND custom_projection_id IS NOT DISTINCT FROM custom_binding;' END;
  IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected retained swap command';END IF;
  definition=replace(definition,before,replace(before,';',' AND spread=0;'));
  before='SELECT * INTO head FROM trading.swap_trade_revisions';
  IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected retained amendment command';END IF;
  definition=replace(definition,before,'IF EXISTS(SELECT 1 FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) WHERE r.catalog_id=catalog AND r.trade_id=trade AND i.first_floating_side_id IS NOT NULL) THEN RAISE EXCEPTION ''swap_amendment'';END IF; '||before);
  EXECUTE definition;
 END LOOP;
END$$;
-- Generated-close reports retain the exact first floating membership as well.
ALTER TABLE trading.swap_instruments ADD UNIQUE(catalog_id,instrument_id,first_floating_side_id);
ALTER TABLE automation.pilot_report_discounts ADD COLUMN first_floating_side_id uuid,
 ADD FOREIGN KEY(catalog_id,instrument_id,first_floating_side_id) REFERENCES trading.swap_instruments(catalog_id,instrument_id,first_floating_side_id);
DO $$DECLARE key_name text;BEGIN
 SELECT conname INTO STRICT key_name FROM pg_constraint WHERE conrelid='automation.pilot_report_discounts'::regclass AND contype='c'
  AND pg_get_constraintdef(oid) LIKE '%num_nonnulls(fixed_side_id, floating_side_id)%';
 EXECUTE format('ALTER TABLE automation.pilot_report_discounts DROP CONSTRAINT %I',key_name);
END$$;
ALTER TABLE automation.pilot_report_discounts ADD CHECK(num_nonnulls(fixed_side_id,first_floating_side_id,floating_side_id)=1);
DO $$DECLARE signature regprocedure='automation.publish_pilot_report(uuid,jsonb)'::regprocedure;definition text;before text;BEGIN
 definition=pg_get_functiondef(signature);
 before='x.currency,x.rate_policy,x.interpolation_policy';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report discount insert';END IF;
 definition=replace(definition,before,before||',x.first_floating');
 before='currency text,rate_policy text,interpolation_policy text)';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report discount parser';END IF;
 EXECUTE replace(definition,before,'currency text,rate_policy text,interpolation_policy text,first_floating uuid)');
END$$;
COMMIT;
