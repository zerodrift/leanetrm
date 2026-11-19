-- Heat-rate call option (D430): retained terms, dated exercises and one idempotent booking.
-- Marks, exercise determination and settlement cash are separate operations.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.heat_rate_call_instruments(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 capacity_quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,
 power_curve_code ref_data.code NOT NULL,fuel_curve_code ref_data.code NOT NULL,
 power_quote_code ref_data.code NOT NULL,fuel_quote_code ref_data.code NOT NULL,
 heat_rate ref_data.exact_number NOT NULL CHECK(heat_rate>0 AND heat_rate<1e12 AND scale(heat_rate)<=18),
 strike ref_data.exact_number NOT NULL CHECK(strike>=0 AND strike<1e12 AND scale(strike)<=18),
 exercise_style text NOT NULL CHECK(exercise_style='daily_automatic_cash_exercise_v1'),
 model text NOT NULL CHECK(model='spread_call_kirk_lognormal_v1'),
 day_count text NOT NULL CHECK(day_count='act_365_fixed_instant_v1'),
 delivery_from date NOT NULL,delivery_to date NOT NULL,
 clock_code ref_data.code NOT NULL,interval_minutes integer NOT NULL CHECK(interval_minutes IN(15,60)),
 hours_membership text NOT NULL CHECK(hours_membership IN('all','peak','off')),tou_product_id uuid,tou_version integer,
 holiday_calendar_code ref_data.code NOT NULL,
 exercise_rule text NOT NULL CHECK(exercise_rule='business_day_lead_local_cutoff_v1'),
 exercise_lead_business_days integer NOT NULL CHECK(exercise_lead_business_days BETWEEN 1 AND 10),
 exercise_local_cutoff time NOT NULL CHECK(exercise_local_cutoff=date_trunc('minute',exercise_local_cutoff)),
 exercise_calendar_code ref_data.code NOT NULL,payment_rule_id uuid NOT NULL,
 terms jsonb NOT NULL CHECK(jsonb_typeof(terms)='object'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 UNIQUE(catalog_id,instrument_id,definition_revision_id,power_curve_code,fuel_curve_code),
 CHECK(delivery_from<delivery_to AND delivery_to-delivery_from<=3660),
 CHECK((hours_membership='all')=(tou_product_id IS NULL)),CHECK((tou_product_id IS NULL)=(tou_version IS NULL)),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,capacity_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,power_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,fuel_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,power_quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,fuel_quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,holiday_calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,exercise_calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,tou_product_id,tou_version) REFERENCES market_data.tou_product_versions(catalog_id,product_id,version),
 FOREIGN KEY(catalog_id,payment_rule_id) REFERENCES trading.swap_payment_rules(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.heat_rate_call_exercises(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 3659),
 delivery_day date NOT NULL,exercise_at ref_data.instant NOT NULL,payment_at ref_data.instant NOT NULL,
 energy_per_unit ref_data.exact_number NOT NULL CHECK(energy_per_unit>0 AND energy_per_unit<1e6 AND scale(energy_per_unit)<=18),
 definition_revision_id uuid NOT NULL,power_curve_code ref_data.code NOT NULL,power_contract_id uuid NOT NULL,
 fuel_curve_code ref_data.code NOT NULL,fuel_contract_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,ordinal),UNIQUE(catalog_id,instrument_id,delivery_day),
 CHECK(payment_at>=exercise_at),
 FOREIGN KEY(catalog_id,instrument_id,definition_revision_id,power_curve_code,fuel_curve_code)
  REFERENCES trading.heat_rate_call_instruments(catalog_id,instrument_id,definition_revision_id,power_curve_code,fuel_curve_code),
 FOREIGN KEY(catalog_id,definition_revision_id,power_curve_code,power_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,fuel_curve_code,fuel_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE TABLE trading.heat_rate_call_trade_revisions(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision=1),instrument_id uuid NOT NULL,book_id uuid NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 quantity ref_data.exact_number NOT NULL CHECK(quantity<>0 AND abs(quantity)<1e12 AND scale(quantity)<=18),
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.heat_rate_call_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.heat_rate_call_booking_requests(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,
 command jsonb NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,operation_id),FOREIGN KEY(catalog_id) REFERENCES market_data.catalogs(id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['heat_rate_call_instruments','heat_rate_call_exercises','heat_rate_call_trade_revisions','heat_rate_call_booking_requests'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_content ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE FUNCTION security.heat_rate_call_scope(catalog uuid,writing boolean) RETURNS uuid
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR(writing AND(security.trading_access(catalog,'BookTrade') IS NOT TRUE OR security.current_write_context() IS NOT TRUE))
  OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND(writing OR risk.worker_read() IS NOT TRUE))
 THEN RAISE EXCEPTION 'heat_rate_call_denied' USING ERRCODE='42501';END IF;
 SELECT q.actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF actor IS NULL THEN RAISE EXCEPTION 'heat_rate_call_denied' USING ERRCODE='42501';END IF;
 RETURN actor;
END$$;
CREATE FUNCTION security.heat_rate_call_booking_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;result jsonb;BEGIN
 actor=security.heat_rate_call_scope(catalog,false);
 SELECT r.outcome INTO result FROM trading.heat_rate_call_booking_requests r WHERE(r.catalog_id,r.actor_id,r.operation_id)=(catalog,actor,operation);
 PERFORM security.heat_rate_call_scope(catalog,false);RETURN result;
END$$;

-- The reference terms an exercise row is valued from must stay readable to whoever reads the trade.
CREATE FUNCTION trading.heat_rate_call_readable(h trading.heat_rate_call_instruments) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT security.market_fact_visible(h.catalog_id,h.definition_revision_id,NULL) IS TRUE
  AND security.reference_access(h.reference_catalog_id,'ReadMarketData') IS TRUE
  AND security.reference_version_visible(h.reference_catalog_id,h.reference_revision_id) IS TRUE
$$;

-- An exact decimal as entered: the text is the number, never a quotient rebuilt from it.
CREATE FUNCTION trading.heat_rate_call_number(value text) RETURNS numeric LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$BEGIN
 IF value IS NULL OR length(value)>80 OR value !~ '^-?[0-9]+(\.[0-9]{1,18})?$' OR abs(value::numeric)>=1e24 THEN RAISE EXCEPTION 'heat_rate_call_input';END IF;
 RETURN trim_scale(value::numeric);
END$$;

CREATE FUNCTION security.book_heat_rate_call(catalog uuid,body jsonb,prepared jsonb,valuation timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;book uuid;definition uuid;rc uuid;rr uuid;t jsonb;hours jsonb;rule jsonb;payment jsonb;item jsonb;facts jsonb;outcome jsonb;
 prior trading.heat_rate_call_booking_requests;power_quote ref_data.quote_specifications;fuel_quote ref_data.quote_specifications;
 quantity numeric;heat numeric;strike_price numeric;first_day date;end_day date;day date;previous_day date;exercise timestamptz;previous_exercise timestamptz;
 paid timestamptz;month_paid timestamptz;energy numeric;power_contract uuid;fuel_contract uuid;curve text;contract uuid;
 ordinal integer=0;publication integer;trade uuid;instrument uuid;payment_rule uuid;rows jsonb='[]';
BEGIN
 actor=security.heat_rate_call_scope(catalog,true);
 IF valuation IS NULL OR NOT isfinite(valuation) OR valuation>clock_timestamp() OR pg_column_size(body)>32768 OR pg_column_size(prepared)>1048576
  OR trading.rec_keys(body,ARRAY['operationId','convention','bookId','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','reason','terms']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE
   WHEN e.key='terms' THEN 'object' WHEN e.key IN('legalEntityRevision','counterpartyRevision') THEN 'number' ELSE 'string' END)
  OR body->>'convention' IS DISTINCT FROM 'heat_rate_call_trade_v1'
  OR body->>'legalEntityRevision' !~ '^[1-9][0-9]{0,9}$' OR body->>'counterpartyRevision' !~ '^[1-9][0-9]{0,9}$'
  OR nullif(btrim(body->>'reason'),'') IS NULL OR length(body->>'reason')>500
  OR trading.rec_keys(prepared,ARRAY['instrumentId','exercises']) IS NOT TRUE OR jsonb_typeof(prepared->'instrumentId') IS DISTINCT FROM 'string'
  OR jsonb_typeof(prepared->'exercises') IS DISTINCT FROM 'array' OR jsonb_array_length(prepared->'exercises') NOT BETWEEN 1 AND 3660
 THEN RAISE EXCEPTION 'heat_rate_call_input';END IF;
 operation=(body->>'operationId')::uuid;book=(body->>'bookId')::uuid;instrument=(prepared->>'instrumentId')::uuid;
 IF '00000000-0000-0000-0000-000000000000'::uuid IN(operation,book,instrument) THEN RAISE EXCEPTION 'heat_rate_call_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.heat_rate_call_scope(catalog,true);
 SELECT * INTO prior FROM trading.heat_rate_call_booking_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN IF prior.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'heat_rate_call_request_conflict';END IF;RETURN prior.outcome;END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'heat_rate_call_request_conflict';END IF;

 t=body->'terms';hours=t->'hours';rule=t->'exerciseRule';payment=t->'paymentRule';
 IF trading.rec_keys(t,ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId','quantity','capacityQuantity','powerCurve','fuelCurve','heatRate','strike',
   'exerciseStyle','model','dayCount','deliveryFrom','deliveryTo','clock','intervalMinutes','hours','exerciseRule','paymentRule']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(t)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE
   WHEN e.key IN('hours','exerciseRule','paymentRule') THEN 'object' WHEN e.key='intervalMinutes' THEN 'number' ELSE 'string' END)
  OR trading.rec_keys(hours,ARRAY['membership','touProduct','touVersion','calendar']) IS NOT TRUE
  OR jsonb_typeof(hours->'membership') IS DISTINCT FROM 'string' OR jsonb_typeof(hours->'calendar') IS DISTINCT FROM 'string'
  OR jsonb_typeof(hours->'touProduct') NOT IN('string','null') OR jsonb_typeof(hours->'touVersion') NOT IN('number','null')
  OR trading.rec_keys(rule,ARRAY['implementation','leadBusinessDays','localCutoff','calendar']) IS NOT TRUE
  OR jsonb_typeof(rule->'leadBusinessDays') IS DISTINCT FROM 'number' OR rule->>'leadBusinessDays' !~ '^([1-9]|10)$'
  OR jsonb_typeof(rule->'localCutoff') IS DISTINCT FROM 'string' OR rule->>'localCutoff' !~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'
  OR jsonb_typeof(rule->'calendar') IS DISTINCT FROM 'string'
  OR trading.rec_keys(payment,ARRAY['frequency','rule','days','dayOfMonth','dayBasis','shortMonth','adjustment','calendar','implementation']) IS NOT TRUE
  OR t->>'deliveryFrom' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR t->>'deliveryTo' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  OR t->>'intervalMinutes' NOT IN('15','60')
 THEN RAISE EXCEPTION 'heat_rate_call_input';END IF;
 IF t->>'exerciseStyle' IS DISTINCT FROM 'daily_automatic_cash_exercise_v1' OR t->>'model' IS DISTINCT FROM 'spread_call_kirk_lognormal_v1'
  OR t->>'dayCount' IS DISTINCT FROM 'act_365_fixed_instant_v1' OR rule->>'implementation' IS DISTINCT FROM 'business_day_lead_local_cutoff_v1'
  OR hours->>'membership' NOT IN('all','peak','off') OR(hours->>'membership'='all')<>(hours->'touProduct'='null'::jsonb)
  OR(hours->'touProduct'='null'::jsonb)<>(hours->'touVersion'='null'::jsonb)
 THEN RAISE EXCEPTION 'heat_rate_call_policy';END IF;
 quantity=trading.heat_rate_call_number(t->>'quantity');
 heat=trading.heat_rate_call_number(t->>'heatRate');
 strike_price=trading.heat_rate_call_number(t->>'strike');
 IF quantity=0 OR abs(quantity)>=1e12 OR heat<=0 OR heat>=1e12 OR strike_price<0 OR strike_price>=1e12 THEN RAISE EXCEPTION 'heat_rate_call_terms';END IF;
 first_day=(t->>'deliveryFrom')::date;end_day=(t->>'deliveryTo')::date;
 IF first_day>=end_day OR end_day-first_day>3660 OR first_day<DATE '1901-01-01' OR end_day>=DATE '2200-01-01' THEN RAISE EXCEPTION 'heat_rate_call_terms';END IF;

 definition=(t->>'definitionRevisionId')::uuid;rc=(t->>'referenceCatalogId')::uuid;rr=(t->>'referenceRevisionId')::uuid;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=(catalog,definition,rc,rr))
  OR security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE OR security.reference_access(rc,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(rc,rr) IS NOT TRUE
 THEN RAISE EXCEPTION 'heat_rate_call_source' USING ERRCODE='42501';END IF;
 SELECT q.* INTO power_quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id)=(catalog,definition,t->>'powerCurve',rc,rr);
 SELECT q.* INTO fuel_quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id)=(catalog,definition,t->>'fuelCurve',rc,rr);
 -- Both prices are outright per unit in one currency, and the two commodities differ: the heat rate is a contractual ratio between them.
 IF power_quote.code IS NULL OR fuel_quote.code IS NULL OR t->>'powerCurve'=t->>'fuelCurve'
  OR(power_quote.kind,power_quote.multiplier,fuel_quote.kind,fuel_quote.multiplier) IS DISTINCT FROM('outright',1::numeric,'outright',1::numeric)
  OR power_quote.currency_code<>fuel_quote.currency_code OR power_quote.commodity_code=fuel_quote.commodity_code
  OR NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q WHERE(q.catalog_id,q.revision_id,q.code,q.commodity_code)=(rc,rr,t->>'capacityQuantity',power_quote.commodity_code))
  OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules c WHERE(c.catalog_id,c.revision_id,c.code)=(rc,rr,t->>'clock'))
  OR NOT EXISTS(SELECT 1 FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code)=(rc,rr,hours->>'calendar'))
  OR NOT EXISTS(SELECT 1 FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code)=(rc,rr,rule->>'calendar'))
  OR hours->'touProduct'<>'null'::jsonb AND NOT EXISTS(SELECT 1 FROM market_data.tou_product_versions v
   WHERE(v.catalog_id,v.product_id,v.version,v.state)=(catalog,(hours->>'touProduct')::uuid,(hours->>'touVersion')::integer,'published'))
 THEN RAISE EXCEPTION 'heat_rate_call_terms';END IF;

 -- Every operating day keeps the one contract that delivers it on each curve and is exercised ahead of the booking instant.
 FOR item IN SELECT value FROM jsonb_array_elements(prepared->'exercises') LOOP
  IF trading.rec_keys(item,ARRAY['deliveryDay','exerciseAt','paymentAt','energyPerUnit','powerContractId','fuelContractId']) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(item)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM 'string')
   OR item->>'deliveryDay' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN RAISE EXCEPTION 'heat_rate_call_input';END IF;
  day=(item->>'deliveryDay')::date;exercise=trading.operating_instant(item->>'exerciseAt');paid=trading.operating_instant(item->>'paymentAt');
  energy=trading.heat_rate_call_number(item->>'energyPerUnit');
  power_contract=(item->>'powerContractId')::uuid;fuel_contract=(item->>'fuelContractId')::uuid;
  IF day<first_day OR day>=end_day OR day<=previous_day OR exercise<=valuation OR exercise<previous_exercise OR paid<exercise
   OR exercise>=((day+1)::timestamp AT TIME ZONE 'UTC') OR energy<=0 OR energy>=1e6
   OR date_trunc('month',day)=date_trunc('month',previous_day) AND paid<>month_paid
  THEN RAISE EXCEPTION 'heat_rate_call_exercise';END IF;
  FOR curve,contract IN SELECT * FROM(VALUES(t->>'powerCurve',power_contract),(t->>'fuelCurve',fuel_contract))x LOOP
   IF(SELECT array_agg(k.id) FROM market_data.curve_members m JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,curve) AND k.delivery_from<=day AND day<k.delivery_to) IS DISTINCT FROM ARRAY[contract]
   THEN RAISE EXCEPTION 'heat_rate_call_contract';END IF;
  END LOOP;
  rows=rows||jsonb_build_array(jsonb_build_object('ordinal',ordinal,'deliveryDay',day,'exerciseAt',exercise,'paymentAt',paid,
   'energyPerUnit',trim_scale(energy)::text,'powerContractId',power_contract,'fuelContractId',fuel_contract));
  previous_day=day;previous_exercise=exercise;month_paid=paid;ordinal=ordinal+1;
 END LOOP;

 -- The business fact excludes the request identity and reason: the same terms in the same book for the same parties are one trade.
 facts=jsonb_build_object('bookId',book,'legalEntityId',(body->>'legalEntityId')::uuid,'legalEntityRevision',(body->>'legalEntityRevision')::integer,
  'counterpartyId',(body->>'counterpartyId')::uuid,'counterpartyRevision',(body->>'counterpartyRevision')::integer,
  'terms',trading.operating_canonical_value(t),'exercises',rows);
 SELECT jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'revision',r.revision,'instrumentId',r.instrument_id,'changed',false)
  INTO outcome FROM trading.heat_rate_call_trade_revisions r JOIN trading.heat_rate_call_instruments h USING(catalog_id,instrument_id)
  WHERE r.catalog_id=catalog AND h.terms=facts;
 IF FOUND THEN
  INSERT INTO trading.heat_rate_call_booking_requests VALUES(catalog,actor,operation,body,outcome);
  PERFORM security.heat_rate_call_scope(catalog,true);RETURN outcome;
 END IF;
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date,
  'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision'),valuation);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=valuation AND p.effective_at<=valuation)
 THEN RAISE EXCEPTION 'heat_rate_call_book';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());trade=gen_random_uuid();
 payment_rule=security.retain_swap_payment_rule(catalog,rc,rr,payment,publication,valuation);
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.heat_rate_call_instruments VALUES(catalog,instrument,definition,rc,rr,t->>'capacityQuantity',power_quote.currency_code,
  t->>'powerCurve',t->>'fuelCurve',power_quote.code,fuel_quote.code,heat,strike_price,t->>'exerciseStyle',t->>'model',t->>'dayCount',first_day,end_day,
  t->>'clock',(t->>'intervalMinutes')::integer,hours->>'membership',(hours->>'touProduct')::uuid,(hours->>'touVersion')::integer,hours->>'calendar',
  rule->>'implementation',(rule->>'leadBusinessDays')::integer,(rule->>'localCutoff')::time,rule->>'calendar',payment_rule,facts,publication,valuation);
 INSERT INTO trading.heat_rate_call_exercises SELECT catalog,instrument,(x->>'ordinal')::integer,(x->>'deliveryDay')::date,(x->>'exerciseAt')::timestamptz,
  (x->>'paymentAt')::timestamptz,(x->>'energyPerUnit')::numeric,definition,t->>'powerCurve',(x->>'powerContractId')::uuid,t->>'fuelCurve',(x->>'fuelContractId')::uuid
  FROM jsonb_array_elements(rows)x;
 INSERT INTO trading.trades VALUES(catalog,trade,book,operation,actor,body,publication,valuation);
 INSERT INTO trading.heat_rate_call_trade_revisions VALUES(catalog,trade,1,instrument,book,operation,actor,quantity,
  (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation);
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'revision',1,'instrumentId',instrument,'changed',true);
 INSERT INTO trading.heat_rate_call_booking_requests VALUES(catalog,actor,operation,body,outcome);
 PERFORM security.heat_rate_call_scope(catalog,true);
 IF security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE OR security.reference_access(rc,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(rc,rr) IS NOT TRUE THEN RAISE EXCEPTION 'heat_rate_call_denied' USING ERRCODE='42501';END IF;
 RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range OR null_value_not_allowed
  OR check_violation OR foreign_key_violation THEN RAISE EXCEPTION 'heat_rate_call_input';
END$$;

CREATE FUNCTION security.heat_rate_call_position_scope(catalog uuid,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;result jsonb;complete boolean;BEGIN
 PERFORM security.heat_rate_call_scope(catalog,false);
 IF books IS NULL OR portfolios IS NULL OR cardinality(books)>128 OR cardinality(portfolios)>128
  OR array_position(books,NULL) IS NOT NULL OR array_position(portfolios,NULL) IS NOT NULL
  OR(trades IS NOT NULL AND(cardinality(trades) NOT BETWEEN 1 AND 256 OR array_position(trades,NULL) IS NOT NULL OR(SELECT count(DISTINCT x) FROM unnest(trades)x)<>cardinality(trades)))
 THEN RAISE EXCEPTION 'heat_rate_call_position_scope';END IF;
 SELECT c.market_context_id INTO context FROM security.request_contexts c JOIN trading.contexts t ON(t.catalog_id,t.context_id)=(c.market_catalog_id,c.market_context_id)
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);
 WITH held AS(SELECT r.* FROM trading.heat_rate_call_trade_revisions r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
  JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE r.catalog_id=catalog AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
   AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at) AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)
   AND(cardinality(books)=0 OR r.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
   AND(trades IS NULL OR r.trade_id=ANY(trades)))
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',r.trade_id,'binding_id',r.instrument_id,'instrument_id',r.instrument_id,
   'definition_revision_id',h.definition_revision_id,'book_id',r.book_id,'revision',r.revision,'trade_reference',tr.reference_number) ORDER BY tr.reference_number),'[]'),
  coalesce(bool_and(tr.reference_number IS NOT NULL AND trading.heat_rate_call_readable(h)),true)
 INTO result,complete FROM held r JOIN trading.heat_rate_call_instruments h USING(catalog_id,instrument_id)
 LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id);
 IF complete IS NOT TRUE THEN RAISE EXCEPTION 'heat_rate_call_unavailable' USING ERRCODE='42501';END IF;
 PERFORM security.heat_rate_call_scope(catalog,false);RETURN result;
END$$;
CREATE FUNCTION security.read_heat_rate_call_positions(catalog uuid,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE inventory jsonb;result jsonb;BEGIN
 inventory=security.heat_rate_call_position_scope(catalog,books,portfolios,trades);
 SELECT coalesce(jsonb_agg(jsonb_build_object(
  'head',jsonb_build_object('trade_id',r.trade_id,'revision',r.revision,'instrument_id',r.instrument_id,'binding_id',r.instrument_id,'book_id',r.book_id,'definition_revision_id',h.definition_revision_id,
   'operation_id',r.operation_id,'quantity',trim_scale(r.quantity)::text,'publication_order',r.publication_order,'effective_at',r.effective_at,
   'legal_entity_id',r.legal_entity_id,'legal_entity_revision',r.legal_entity_revision,'legal_entity',le.name,
   'counterparty_id',r.counterparty_id,'counterparty_revision',r.counterparty_revision,'counterparty',cp.name,
   'trade_reference',tr.reference_number,'instrument_reference',ir.reference_number,'reason',t.request->>'reason',
   'trade_date',b.trade_date,'booker_id',b.booker_id,'booker_name',b.booker_name,'booker_login',b.booker_login,'executed_at',b.executed_at,
   'booking_implementation',b.implementation,'label_captured_at',b.label_captured_at),
  'terms',jsonb_build_object('definition_revision_id',h.definition_revision_id,'reference_catalog_id',h.reference_catalog_id,'reference_revision_id',h.reference_revision_id,
   'capacity_quantity_code',h.capacity_quantity_code,'capacity_unit',cu.symbol,'currency_code',h.currency_code,'currency',currency.display_code,
   'power_curve_code',h.power_curve_code,'fuel_curve_code',h.fuel_curve_code,'power_quote_code',h.power_quote_code,'fuel_quote_code',h.fuel_quote_code,
   'power_quantity_code',pq.quantity_code,'power_unit',pu.symbol,'power_commodity_code',pq.commodity_code,'power_commodity',pc.name,
   'fuel_quantity_code',fq.quantity_code,'fuel_unit',fu.symbol,'fuel_commodity_code',fq.commodity_code,'fuel_commodity',fc.name,
   'heat_rate',trim_scale(h.heat_rate)::text,'strike',trim_scale(h.strike)::text,'exercise_style',h.exercise_style,'model',h.model,'day_count',h.day_count,
   'delivery_from',h.delivery_from,'delivery_to',h.delivery_to,'clock_code',h.clock_code,'interval_minutes',h.interval_minutes,
   'hours_membership',h.hours_membership,'tou_product_id',h.tou_product_id,'tou_version',h.tou_version,'holiday_calendar_code',h.holiday_calendar_code,
   'exercise_rule',h.exercise_rule,'exercise_lead_business_days',h.exercise_lead_business_days,'exercise_local_cutoff',to_char(h.exercise_local_cutoff,'HH24:MI'),
   'exercise_calendar_code',h.exercise_calendar_code,'payment_rule',to_jsonb(pay)-'catalog_id'-'publication_order'-'effective_at',
   'projection_curves',jsonb_build_array(h.power_curve_code,h.fuel_curve_code)),
  'exercises',(SELECT jsonb_agg(jsonb_build_object('ordinal',e.ordinal,'delivery_day',e.delivery_day,'exercise_at',e.exercise_at,'payment_at',e.payment_at,
    'energy_per_unit',trim_scale(e.energy_per_unit)::text,'power_contract_id',e.power_contract_id,'fuel_contract_id',e.fuel_contract_id) ORDER BY e.ordinal)
   FROM trading.heat_rate_call_exercises e WHERE(e.catalog_id,e.instrument_id)=(h.catalog_id,h.instrument_id))) ORDER BY tr.reference_number),'[]')
 INTO result FROM trading.heat_rate_call_trade_revisions r JOIN trading.heat_rate_call_instruments h USING(catalog_id,instrument_id)
 JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
 JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
 JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
 JOIN trading.booking_facts b ON(b.catalog_id,b.trade_id)=(r.catalog_id,r.trade_id)
 JOIN trading.legal_entities le ON(le.catalog_id,le.id,le.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
 JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
 JOIN trading.swap_payment_rules pay ON(pay.catalog_id,pay.id)=(h.catalog_id,h.payment_rule_id)
 JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=(h.reference_catalog_id,h.reference_revision_id,h.currency_code)
 JOIN ref_data.quantity_specifications cq ON(cq.catalog_id,cq.revision_id,cq.code)=(h.reference_catalog_id,h.reference_revision_id,h.capacity_quantity_code)
 JOIN ref_data.units cu ON(cu.catalog_id,cu.revision_id,cu.code)=(cq.catalog_id,cq.revision_id,cq.unit_code)
 JOIN ref_data.quote_specifications pq ON(pq.catalog_id,pq.revision_id,pq.code)=(h.reference_catalog_id,h.reference_revision_id,h.power_quote_code)
 JOIN ref_data.quantity_specifications pqs ON(pqs.catalog_id,pqs.revision_id,pqs.code)=(pq.catalog_id,pq.revision_id,pq.quantity_code)
 JOIN ref_data.units pu ON(pu.catalog_id,pu.revision_id,pu.code)=(pqs.catalog_id,pqs.revision_id,pqs.unit_code)
 JOIN ref_data.commodities pc ON(pc.catalog_id,pc.revision_id,pc.code)=(pq.catalog_id,pq.revision_id,pq.commodity_code)
 JOIN ref_data.quote_specifications fq ON(fq.catalog_id,fq.revision_id,fq.code)=(h.reference_catalog_id,h.reference_revision_id,h.fuel_quote_code)
 JOIN ref_data.quantity_specifications fqs ON(fqs.catalog_id,fqs.revision_id,fqs.code)=(fq.catalog_id,fq.revision_id,fq.quantity_code)
 JOIN ref_data.units fu ON(fu.catalog_id,fu.revision_id,fu.code)=(fqs.catalog_id,fqs.revision_id,fqs.unit_code)
 JOIN ref_data.commodities fc ON(fc.catalog_id,fc.revision_id,fc.code)=(fq.catalog_id,fq.revision_id,fq.commodity_code)
 WHERE r.catalog_id=catalog AND r.trade_id IN(SELECT(value->>'trade_id')::uuid FROM jsonb_array_elements(inventory));
 IF jsonb_array_length(result)<>jsonb_array_length(inventory) THEN RAISE EXCEPTION 'heat_rate_call_metadata';END IF;
 PERFORM security.heat_rate_call_position_scope(catalog,books,portfolios,trades);RETURN result;
END$$;
CREATE FUNCTION security.heat_rate_call_booking_replay(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;r trading.heat_rate_call_booking_requests;BEGIN
 actor=security.heat_rate_call_scope(catalog,false);
 SELECT * INTO r FROM trading.heat_rate_call_booking_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,(body->>'operationId')::uuid);
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF r.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'heat_rate_call_request_conflict';END IF;
 PERFORM security.heat_rate_call_scope(catalog,false);RETURN r.outcome;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('security','trading') AND p.proname LIKE '%heat_rate_call%' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.heat_rate_call_booking_status(uuid,uuid),security.heat_rate_call_booking_replay(uuid,jsonb),
 security.book_heat_rate_call(uuid,jsonb,jsonb,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.heat_rate_call_position_scope(uuid,uuid[],uuid[],uuid[]),security.read_heat_rate_call_positions(uuid,uuid[],uuid[],uuid[])
 TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
