-- New explicit scheduled economics. Retained one-period events are unchanged.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.swap_scheduled_sides (
 catalog_id uuid NOT NULL,id uuid NOT NULL,identity_hash bytea NOT NULL CHECK(octet_length(identity_hash)=32),
 implementation text NOT NULL CHECK(implementation='explicit_monthly_quantity_schedule_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,identity_hash),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_scheduled_periods (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 119),
 fixed_side_id uuid,floating_side_id uuid,quantity_numerator numeric NOT NULL,quantity_denominator numeric NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,ordinal),CHECK(num_nonnulls(fixed_side_id,floating_side_id)=1),
 CHECK(quantity_numerator>0 AND quantity_denominator>0 AND scale(quantity_numerator)=0 AND scale(quantity_denominator)=0 AND length(quantity_numerator::text)<=1234 AND length(quantity_denominator::text)<=1234),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.swap_scheduled_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,fixed_side_id) REFERENCES trading.swap_fixed_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,floating_side_id) REFERENCES trading.swap_floating_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.swap_instruments DROP CONSTRAINT swap_instruments_implementation_check;
ALTER TABLE trading.swap_instruments ADD CHECK(implementation IN('independent_side_financial_swap_v2','independent_scheduled_financial_swap_v1')),
 ADD COLUMN scheduled_first_id uuid,ADD COLUMN scheduled_second_id uuid,
 ADD COLUMN second_quantity_numerator numeric NOT NULL DEFAULT 1,ADD COLUMN second_quantity_denominator numeric NOT NULL DEFAULT 1,
 ADD FOREIGN KEY(catalog_id,scheduled_first_id) REFERENCES trading.swap_scheduled_sides(catalog_id,id),
 ADD FOREIGN KEY(catalog_id,scheduled_second_id) REFERENCES trading.swap_scheduled_sides(catalog_id,id),
 ADD CHECK((implementation='independent_scheduled_financial_swap_v1')=(scheduled_first_id IS NOT NULL AND scheduled_second_id IS NOT NULL)),
 ADD CHECK((scheduled_first_id IS NULL)=(scheduled_second_id IS NULL)),
 ADD CHECK(second_quantity_numerator>0 AND second_quantity_denominator>0 AND scale(second_quantity_numerator)=0 AND scale(second_quantity_denominator)=0 AND length(second_quantity_numerator::text)<=1234 AND length(second_quantity_denominator::text)<=1234),
 ADD CHECK(scheduled_first_id IS NOT NULL OR(second_quantity_numerator=1 AND second_quantity_denominator=1));
DO $$DECLARE constraint_name text;BEGIN
 SELECT conname INTO STRICT constraint_name FROM pg_constraint WHERE conrelid='trading.swap_instruments'::regclass AND contype='u' AND pg_get_constraintdef(oid) LIKE '%fixed_coefficient, discounting, implementation)%';
 EXECUTE format('ALTER TABLE trading.swap_instruments DROP CONSTRAINT %I',constraint_name);
END$$;
DROP INDEX trading.swap_floating_pair_key;
ALTER TABLE trading.swap_instruments ADD UNIQUE NULLS NOT DISTINCT(catalog_id,fixed_side_id,first_floating_side_id,floating_side_id,fixed_coefficient,discounting,implementation,scheduled_first_id,scheduled_second_id,second_quantity_numerator,second_quantity_denominator);
ALTER TABLE trading.swap_trade_revisions DROP CONSTRAINT swap_trade_revisions_event_schema_check;
ALTER TABLE trading.swap_trade_revisions ADD CHECK(event_schema IN('monthly_financial_swap_booking_v1','monthly_financial_swap_booking_v2','scheduled_financial_swap_booking_v1')), ADD CHECK(event_schema<>'scheduled_financial_swap_booking_v1' OR trade_date IS NOT NULL);
CREATE TABLE trading.swap_quantity_authoring (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL,slot integer NOT NULL CHECK(slot IN(0,1)),
 basis text NOT NULL CHECK(basis IN('period_total','calendar_day_rate','contract_total','selected_interval_power')),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,quantity_code ref_data.code NOT NULL,
 contract_amount ref_data.exact_number,allocation text,price_application text NOT NULL CHECK(price_application='period_average_times_quantity_v1'),
 clock_code ref_data.code,delivery_product text,start_hour integer,end_hour integer,saturday boolean,calendar_code ref_data.code,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision,slot),
 CHECK((basis='contract_total')=(contract_amount IS NOT NULL AND allocation IS NOT NULL)),
 CHECK(contract_amount IS NULL OR contract_amount>0),CHECK(allocation IS NULL OR allocation IN('equal_periods','calendar_days')),
 CHECK((basis='selected_interval_power')=(clock_code IS NOT NULL AND delivery_product IS NOT NULL)),
 CHECK(delivery_product IS NULL OR delivery_product='all_hours' AND num_nonnulls(start_hour,end_hour,saturday,calendar_code)=0 OR delivery_product='weekday_block' AND start_hour>=0 AND end_hour<=24 AND start_hour<end_hour AND saturday IS NOT NULL AND calendar_code IS NOT NULL),
 FOREIGN KEY(catalog_id,trade_id,revision) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_quantity_amounts (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL,slot integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 119),amount ref_data.exact_number NOT NULL CHECK(amount>0),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision,slot,ordinal),
 FOREIGN KEY(catalog_id,trade_id,revision,slot) REFERENCES trading.swap_quantity_authoring(catalog_id,trade_id,revision,slot),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_power_quantity_intervals (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL,slot integer NOT NULL,period_ordinal integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 771),
 starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,operating_date date NOT NULL,
 energy_numerator numeric NOT NULL CHECK(energy_numerator>0),energy_denominator numeric NOT NULL CHECK(energy_denominator>0),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision,slot,period_ordinal,ordinal),UNIQUE(catalog_id,trade_id,revision,slot,starts_at),CHECK(ends_at=starts_at+INTERVAL '1 hour'),
 FOREIGN KEY(catalog_id,trade_id,revision,slot,period_ordinal) REFERENCES trading.swap_quantity_amounts(catalog_id,trade_id,revision,slot,ordinal),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.swap_quantity_authoring ADD COLUMN clock_rules text,ADD COLUMN clock_fingerprint text,
 ADD CHECK((basis='selected_interval_power')=(clock_rules IS NOT NULL AND clock_fingerprint IS NOT NULL)),
 ADD CHECK(clock_fingerprint IS NULL OR clock_fingerprint=encode(sha256(convert_to(clock_rules,'UTF8')),'hex'));
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_scheduled_sides','swap_scheduled_periods','swap_quantity_amounts','swap_power_quantity_intervals'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ADD COLUMN reference_catalog_id uuid NOT NULL, ADD COLUMN reference_revision_id uuid NOT NULL, ADD FOREIGN KEY(reference_catalog_id,reference_revision_id) REFERENCES ref_data.revisions(catalog_id,id)',t);
 END LOOP;
END$$;
ALTER TABLE trading.swap_quantity_amounts ADD COLUMN delivery_duration_ticks bigint,ADD COLUMN energy_numerator numeric,ADD COLUMN energy_denominator numeric,ADD COLUMN energy_quantity_code ref_data.code,
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,energy_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 ADD CHECK(num_nonnulls(delivery_duration_ticks,energy_numerator,energy_denominator,energy_quantity_code) IN(0,4)),
 ADD CHECK(delivery_duration_ticks IS NULL OR delivery_duration_ticks>0 AND energy_numerator>0 AND energy_denominator>0);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_scheduled_sides','swap_scheduled_periods','swap_quantity_authoring','swap_quantity_amounts','swap_power_quantity_intervals'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('CREATE POLICY reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY risk_worker_scoped_read ON trading.%I FOR SELECT TO openetrm_risk_worker USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('CREATE POLICY risk_worker_reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY risk_worker_service ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_risk_worker',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
 END LOOP;
END$$;

-- Shared primitive retention: scheduled and single-period compositions use the
-- same fixed-side, payment-calendar and exact-quote validation.
CREATE FUNCTION security.retain_fixed_swap_side(catalog uuid,ref_catalog uuid,ref_revision uuid,quote text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q ref_data.quote_specifications;side uuid;price numeric;payment timestamptz;rule uuid;
BEGIN
 SELECT * INTO STRICT q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,quote);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('price','currency','deliveryFrom','deliveryTo','paymentAt','paymentSchedule')) OR body->>'paymentAt' IS NOT NULL OR body->>'currency' IS DISTINCT FROM q.currency_code THEN RAISE EXCEPTION 'swap_input';END IF;
 price=(body->>'price')::numeric;
 IF price IS NULL OR abs(price)>=1e24 OR scale(price)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
 payment=security.swap_payment_date(ref_catalog,ref_revision,(body->>'deliveryFrom')::date,(body->>'deliveryTo')::date,body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
 IF payment IS NULL OR valuation>=payment THEN RAISE EXCEPTION 'swap_input';END IF;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 SELECT id INTO side FROM trading.swap_fixed_sides s WHERE s.catalog_id=catalog AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.quote_code=q.code AND s.delivery_from=(body->>'deliveryFrom')::date AND s.delivery_to=(body->>'deliveryTo')::date AND s.payment_at=payment AND s.fixed_price=price AND s.payment_rule_id=rule;
 IF side IS NULL THEN
  side=gen_random_uuid();INSERT INTO trading.swap_fixed_sides VALUES(catalog,side,ref_catalog,ref_revision,q.code,(body->>'deliveryFrom')::date,(body->>'deliveryTo')::date,payment,price,'fixed_total_payment_v1',publication_number,from_at,rule);
 END IF;
 RETURN side;
END$$;
DO $$DECLARE signature regprocedure='security.save_composed_swap(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,jsonb,jsonb,jsonb,numeric,numeric,boolean,timestamptz,timestamptz,text,text,text,jsonb,uuid[])'::regprocedure;definition text;first_at integer;last_at integer;BEGIN
 definition=pg_get_functiondef(signature);
 first_at=strpos(definition,'  IF jsonb_typeof(fixed_body)');last_at=strpos(definition,' request_value=request_value||jsonb_build_object(''sideFixings''');
 IF first_at=0 OR last_at<=first_at THEN RAISE EXCEPTION 'Unexpected fixed-side retention definition';END IF;
 definition=left(definition,first_at-1)||'  fixed_side=security.retain_fixed_swap_side(catalog,ref_catalog,ref_revision,q.code,fixed_body,valuation,publication_number,from_at);'||E'\n END IF;\n'||substr(definition,last_at);
 EXECUTE definition;
END$$;
-- Primitive operations cannot select a scheduled instrument by its first period.
DO $$DECLARE f record;definition text;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('save_swap_with_parties','save_indexed_swap_with_parties','save_projected_swap_core','save_projected_swap_with_parties','save_composed_swap') LOOP
  definition=pg_get_functiondef(f.signature);
  definition=regexp_replace(definition,'(SELECT [^;]+ INTO instrument FROM trading.swap_instruments i WHERE [^;]+)(;)','\1 AND i.scheduled_first_id IS NULL\2','g');
  definition=regexp_replace(definition,'(SELECT [^;]+ INTO instrument FROM trading.swap_instruments WHERE [^;]+)(;)','\1 AND swap_instruments.scheduled_first_id IS NULL\2','g');
  EXECUTE definition;
 END LOOP;
END$$;

CREATE FUNCTION security.save_scheduled_swap(catalog uuid,body jsonb,compiled jsonb,quantity numeric,valuation timestamptz,booking jsonb,input_pins uuid[])
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;permission text;publication_number integer;
 operation uuid=(body->>'operationId')::uuid;trade uuid=(body->>'tradeId')::uuid;expected integer=(body->>'expectedRevision')::integer;book uuid=(body->>'bookId')::uuid;definition uuid=(body->>'definitionRevisionId')::uuid;
 ref_catalog uuid;ref_revision uuid;commodity text=body->>'commodity';coefficient numeric=(body->>'firstCoefficient')::numeric;discount_enabled boolean=(body->>'discounting')::boolean;
 effective timestamptz;executed timestamptz;from_at timestamptz;instrument uuid;binding uuid;ids uuid[]='{}';fixed_id uuid;first_float uuid;second_float uuid;child uuid;quote text;
 slot integer;n integer;side_body jsonb;period_body jsonb;shape jsonb;side_id uuid;children jsonb;digest_value bytea;num numeric;den numeric;total_num numeric;total_den numeric;common numeric;kind boolean;previous_end date;starts date;ends date;payment timestamptz;
 request_value jsonb;side_fixings jsonb='[]';power jsonb;period_pins uuid[];
BEGIN
 permission=CASE WHEN trade IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 request_value=jsonb_build_object('schema','scheduled_financial_swap_booking_v1','body',body,'valuation',valuation,'booking',booking);
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request-'sideFixings'<>request_value THEN RAISE EXCEPTION 'swap_conflict';END IF;
  RETURN security.swap_operation_status(catalog,operation);
 END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR body->>'knowledgeMode' IS DISTINCT FROM 'current' OR body->>'convention' IS DISTINCT FROM 'independent_side_financial_swap_v2' OR
  body->'scheduled'->>'implementation' IS DISTINCT FROM 'independent_scheduled_financial_swap_v1' OR body->>'swapType' IS DISTINCT FROM 'scheduled_financial_swap' OR
  body->>'quantity' IS NOT NULL OR body->>'fixed' IS NOT NULL OR body->>'floating' IS NOT NULL OR body->>'firstFloating' IS NOT NULL OR body->>'fixedCoefficient' IS NOT NULL OR
  coefficient IS NULL OR coefficient NOT IN(-1,1) OR discount_enabled IS NULL OR quantity IS NULL OR quantity<=0 OR abs(quantity)>=1e24 OR scale(quantity)>18 OR
  valuation IS NULL OR NOT isfinite(valuation) OR input_pins IS NULL OR cardinality(input_pins)>87840 OR cardinality(input_pins)<>(SELECT count(DISTINCT x) FROM unnest(input_pins) x) OR jsonb_array_length(compiled)<>2 OR booking->>'tradeDate' IS NOT NULL THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE catalog_id=catalog AND operation_id=operation) THEN RAISE EXCEPTION 'swap_conflict';END IF;
 SELECT reference_catalog_id,reference_revision_id,effective_from::timestamp AT TIME ZONE 'UTC' INTO ref_catalog,ref_revision,from_at FROM market_data.revisions r JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(r.catalog_id,r.id) WHERE r.catalog_id=catalog AND r.id=definition AND r.state='sealed' AND p.effective_from<=(valuation AT TIME ZONE 'UTC')::date AND(valuation AT TIME ZONE 'UTC')::date<p.effective_to;
 IF from_at IS NULL OR NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'swap_definition';END IF;
 executed=valuation;effective=valuation;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade';END IF;
  IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
  IF head.revision>=256 OR(body->>'effectiveAt')::timestamptz IS DISTINCT FROM valuation OR valuation<head.effective_at OR length(btrim(body->>'reason')) NOT BETWEEN 1 AND 500 OR body->>'reason' IS NULL THEN RAISE EXCEPTION 'swap_amendment';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND i.scheduled_first_id IS NOT NULL) OR EXISTS(
   SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_scheduled_periods p ON p.catalog_id=i.catalog_id AND p.side_id IN(i.scheduled_first_id,i.scheduled_second_id)
   LEFT JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(p.catalog_id,p.fixed_side_id) LEFT JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(p.catalog_id,p.floating_side_id)
   WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND(valuation>=f.payment_at OR valuation>=s.payment_at OR valuation>=s.first_fixing_at)) THEN RAISE EXCEPTION 'swap_amendment';END IF;
  executed=head.execution_at;booking=booking||jsonb_build_object('tradeDate',head.trade_date);
 ELSE
  IF expected IS NOT NULL OR(SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'swap_limit';END IF;
  booking=booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date);
 END IF;
 PERFORM security.require_trade_annotation(catalog,head.trader_id,head.trader_revision,body->>'memo',executed);
 PERFORM security.require_trade_parties(catalog,booking,executed);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=executed AND p.effective_at<=executed) THEN RAISE EXCEPTION 'swap_book';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication_number FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication_number,pg_current_xact_id());
 -- Retain the second side first, so its exact quote qualifies fixed constituents.
 FOR slot IN REVERSE 1..0 LOOP
  side_body=body->'scheduled'->CASE slot WHEN 0 THEN 'first' ELSE 'second' END;shape=compiled->slot;children='[]';previous_end=NULL;total_num=0;total_den=1;
  IF jsonb_typeof(side_body->'periods')<>'array' OR jsonb_array_length(side_body->'periods') NOT BETWEEN 1 AND 120 OR jsonb_array_length(side_body->'periods')<>jsonb_array_length(shape->'periods') OR side_body->>'priceApplication' IS DISTINCT FROM 'period_average_times_quantity_v1' THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
  kind=(side_body->'periods'->0->>'fixed') IS NOT NULL;
  IF slot=1 AND kind THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
  FOR n IN 0..jsonb_array_length(side_body->'periods')-1 LOOP
   period_body=side_body->'periods'->n;
   IF ((period_body->>'fixed') IS NULL)=((period_body->>'floating') IS NULL) OR kind<>((period_body->>'fixed') IS NOT NULL) THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
   IF kind THEN
    child=security.retain_fixed_swap_side(catalog,ref_catalog,ref_revision,quote,period_body->'fixed',valuation,publication_number,from_at);
    SELECT delivery_from,delivery_to,payment_at INTO starts,ends,payment FROM trading.swap_fixed_sides WHERE(catalog_id,id)=(catalog,child);
    IF slot=0 AND n=0 THEN fixed_id=child;END IF;
   ELSE
    SELECT coalesce(array_agg(value::uuid),'{}'::uuid[]) INTO period_pins FROM jsonb_array_elements_text(shape->'periods'->n->'fixingPins');
    IF NOT period_pins<@input_pins THEN RAISE EXCEPTION 'index_fixing_inputs';END IF;
    child=security.retain_floating_swap_side(catalog,definition,ref_catalog,ref_revision,commodity,period_body->'floating',valuation,publication_number,from_at,period_pins);
    SELECT delivery_from,delivery_to,payment_at,quote_code INTO starts,ends,payment,quote FROM trading.swap_floating_sides WHERE(catalog_id,id)=(catalog,child);
    side_fixings=side_fixings||jsonb_build_array(jsonb_build_object('slot',slot,'ordinal',n,'sideId',child,'evidence',security.retained_swap_fixing_evidence(catalog,child,valuation,period_pins)));
    IF slot=1 AND n=0 THEN second_float=child;ELSIF slot=0 AND n=0 THEN first_float=child;END IF;
   END IF;
   IF extract(day FROM starts)<>1 OR ends IS DISTINCT FROM(starts+INTERVAL '1 month')::date OR(previous_end IS NOT NULL AND previous_end<>starts) THEN RAISE EXCEPTION 'scheduled_swap_periods';END IF;
   previous_end=ends;
   num=(shape->'periods'->n->>'numerator')::numeric;den=(shape->'periods'->n->>'denominator')::numeric;
   IF num IS NULL OR den IS NULL OR num<=0 OR den<=0 OR scale(num)<>0 OR scale(den)<>0 THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
   total_num=total_num*den+num*total_den;total_den=total_den*den;common=gcd(total_num,total_den);total_num=total_num/common;total_den=total_den/common;
   children=children||jsonb_build_array(jsonb_build_object('id',child,'fixed',kind,'n',num,'d',den));
  END LOOP;
  IF total_num<>total_den THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
  digest_value=sha256(convert_to(children::text,'UTF8'));
  SELECT id INTO side_id FROM trading.swap_scheduled_sides s WHERE s.catalog_id=catalog AND s.identity_hash=digest_value;
  IF side_id IS NULL THEN
   side_id=gen_random_uuid();INSERT INTO trading.swap_scheduled_sides VALUES(catalog,side_id,digest_value,'explicit_monthly_quantity_schedule_v1',publication_number,from_at,ref_catalog,ref_revision);
   INSERT INTO trading.swap_scheduled_periods SELECT catalog,side_id,(x.ordinality-1)::integer,CASE WHEN(x.value->>'fixed')::boolean THEN(x.value->>'id')::uuid END,CASE WHEN NOT(x.value->>'fixed')::boolean THEN(x.value->>'id')::uuid END,(x.value->>'n')::numeric,(x.value->>'d')::numeric,publication_number,from_at,ref_catalog,ref_revision FROM jsonb_array_elements(children) WITH ORDINALITY x;
  END IF;
  ids=array_prepend(side_id,ids);
 END LOOP;
 IF(compiled->0->>'relativeNumerator')::numeric IS DISTINCT FROM 1 OR(compiled->0->>'relativeDenominator')::numeric IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
 num=(compiled->1->>'relativeNumerator')::numeric;den=(compiled->1->>'relativeDenominator')::numeric;
 SELECT i.instrument_id INTO instrument FROM trading.swap_instruments i WHERE i.catalog_id=catalog AND i.scheduled_first_id=ids[1] AND i.scheduled_second_id=ids[2] AND i.fixed_coefficient=coefficient AND i.discounting=discount_enabled AND i.second_quantity_numerator=num AND i.second_quantity_denominator=den;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments(catalog_id,instrument_id,fixed_side_id,first_floating_side_id,floating_side_id,fixed_coefficient,discounting,implementation,publication_order,effective_at,scheduled_first_id,scheduled_second_id,second_quantity_numerator,second_quantity_denominator)
   VALUES(catalog,instrument,fixed_id,first_float,second_float,coefficient,discount_enabled,'independent_scheduled_financial_swap_v1',publication_number,from_at,ids[1],ids[2],num,den);
 END IF;
 IF trade IS NULL THEN
  trade=gen_random_uuid();binding=gen_random_uuid();booking=booking||jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request_value,publication_number,executed);
 ELSE
  IF(body->'scheduled',book,instrument,quantity,body->>'memo',booking) IS NOT DISTINCT FROM(head.request->'body'->'scheduled',head.book_id,head.instrument_id,head.quantity,head.memo,security.trade_party_payload(head.trade_date,head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision)) THEN RAISE EXCEPTION 'swap_no_change';END IF;
  binding=CASE WHEN instrument=head.instrument_id THEN head.binding_id ELSE gen_random_uuid() END;
 END IF;
 INSERT INTO trading.swap_trade_revisions VALUES(catalog,trade,coalesce(head.revision,0)+1,head.revision,gen_random_uuid(),'scheduled_financial_swap_booking_v1',CASE WHEN head.revision IS NULL THEN 'booked' ELSE 'amended' END,
  book,binding,instrument,quantity,executed,effective,operation,c.actor_id,CASE WHEN head.revision IS NULL THEN 'Initial booking' ELSE btrim(body->>'reason') END,request_value||jsonb_build_object('sideFixings',side_fixings),publication_number,head.trader_id,head.trader_revision,body->>'memo',(booking->>'tradeDate')::date,(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,(booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer);
 FOR slot IN 0..1 LOOP
  side_body=body->'scheduled'->CASE slot WHEN 0 THEN 'first' ELSE 'second' END;
  IF(side_body->'quantity'->'revision'->>'catalogId')::uuid IS DISTINCT FROM ref_catalog OR(side_body->'quantity'->'revision'->>'revisionId')::uuid IS DISTINCT FROM ref_revision THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
  INSERT INTO trading.swap_quantity_authoring VALUES(catalog,trade,coalesce(head.revision,0)+1,slot,side_body->>'basis',ref_catalog,ref_revision,side_body->'quantity'->>'code',(side_body->>'amount')::numeric,side_body->>'allocation',side_body->>'priceApplication',side_body->'power'->'clock'->>'code',side_body->'power'->>'product',(side_body->'power'->>'startHour')::integer,(side_body->'power'->>'endHour')::integer,(side_body->'power'->>'saturday')::boolean,side_body->'power'->'calendar'->>'code',publication_number,effective,compiled->slot->'clock'->>'rules',compiled->slot->'clock'->>'fingerprint');
  FOR n IN 0..jsonb_array_length(side_body->'periods')-1 LOOP
   power=NULLIF(compiled->slot->'periods'->n->'power','null'::jsonb);
   IF side_body->>'basis'<>'contract_total' THEN INSERT INTO trading.swap_quantity_amounts VALUES(catalog,trade,coalesce(head.revision,0)+1,slot,n,(side_body->'periods'->n->>'amount')::numeric,publication_number,effective,ref_catalog,ref_revision,(power->>'durationTicks')::bigint,(power->'total'->>'numerator')::numeric,(power->'total'->>'denominator')::numeric,power->>'energyQuantity');END IF;
   IF side_body->>'basis'='selected_interval_power' THEN
    IF power IS NULL OR power='null'::jsonb THEN RAISE EXCEPTION 'scheduled_power';END IF;
    INSERT INTO trading.swap_power_quantity_intervals
     SELECT catalog,trade,coalesce(head.revision,0)+1,slot,n,(x.ordinality-1)::integer,(x.value->'interval'->>'start')::timestamptz,(x.value->'interval'->>'end')::timestamptz,(x.value->'interval'->>'operatingDate')::date,(x.value->'energy'->>'numerator')::numeric,(x.value->'energy'->>'denominator')::numeric,publication_number,effective,ref_catalog,ref_revision
     FROM jsonb_array_elements(power->'intervals') WITH ORDINALITY x;
   END IF;
  END LOOP;
 END LOOP;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 RETURN security.swap_operation_status(catalog,operation);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('retain_fixed_swap_side','save_scheduled_swap') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname='save_scheduled_swap' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;

-- A legacy operation cannot turn a retained scheduled revision into primitive
-- one-period economics. An upgrade needs its own explicit operation/version.
CREATE FUNCTION trading.guard_swap_contract_version() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE current_scheduled boolean;prior_scheduled boolean;
BEGIN
 SELECT scheduled_first_id IS NOT NULL INTO STRICT current_scheduled FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 IF current_scheduled IS DISTINCT FROM(NEW.event_schema='scheduled_financial_swap_booking_v1') THEN RAISE EXCEPTION 'swap_amendment';END IF;
 IF NEW.previous_revision IS NOT NULL THEN
  SELECT i.scheduled_first_id IS NOT NULL INTO STRICT prior_scheduled FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) WHERE(r.catalog_id,r.trade_id,r.revision)=(NEW.catalog_id,NEW.trade_id,NEW.previous_revision);
  IF current_scheduled IS DISTINCT FROM prior_scheduled THEN RAISE EXCEPTION 'swap_amendment';END IF;
 END IF;
 RETURN NEW;
END$$;
REVOKE ALL ON FUNCTION trading.guard_swap_contract_version() FROM PUBLIC;
CREATE TRIGGER contract_version BEFORE INSERT ON trading.swap_trade_revisions FOR EACH ROW EXECUTE FUNCTION trading.guard_swap_contract_version();

-- Invoker reads preserve every underlying visibility policy. Economic side terms
-- are reconstructed from exact foreign keys, not the retained command envelope.
CREATE FUNCTION trading.swap_period_input(catalog uuid,fixed_id uuid,float_id uuid) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH payment AS(
  SELECT p.id,jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) terms FROM trading.swap_payment_rules p WHERE p.catalog_id=catalog
 ),observation AS(
  SELECT o.id,jsonb_build_object('implementation',o.implementation,'calendar',CASE WHEN o.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.calendar_code) END,
   'quantities',CASE WHEN o.averaging='quantity_weighted' THEN(SELECT jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'quantity',jsonb_build_object('value',trim_scale(q.amount)::text,'specification',jsonb_build_object('revision',jsonb_build_object('catalogId',q.reference_catalog_id,'revisionId',q.reference_revision_id),'code',q.quantity_code))) ORDER BY q.ordinal)
    FROM trading.swap_observation_quantities q JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(q.catalog_id,q.definition_revision_id,q.contract_id) WHERE(q.catalog_id,q.schedule_id)=(o.catalog_id,o.id)) END) terms FROM trading.swap_observation_schedules o WHERE o.catalog_id=catalog
 )
 SELECT CASE WHEN fixed_id IS NOT NULL THEN(
  SELECT jsonb_build_object('fixed',jsonb_build_object('price',trim_scale(f.fixed_price)::text,'currency',q.currency_code,'deliveryFrom',f.delivery_from,'deliveryTo',f.delivery_to,'paymentSchedule',p.terms),'floating',NULL)
   FROM trading.swap_fixed_sides f JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(f.reference_catalog_id,f.reference_revision_id,f.quote_code) JOIN payment p ON p.id=f.payment_rule_id WHERE(f.catalog_id,f.id)=(catalog,fixed_id)
 )ELSE(
  SELECT jsonb_build_object('fixed',NULL,'floating',jsonb_build_object('curve',s.curve_code,'index',s.index_code,'currency',q.currency_code,'deliveryFrom',s.delivery_from,'deliveryTo',s.delivery_to,'paymentSchedule',p.terms,
   'resetFrequency',CASE WHEN s.averaging='monthly_index_price' THEN 'monthly' ELSE 'daily' END,'averagingMethod',CASE s.averaging WHEN 'monthly_index_price' THEN 'none' WHEN 'calendar_day_arithmetic' THEN 'arithmetic' ELSE s.averaging END,'spread',trim_scale(s.spread)::text,'observationSchedule',o.terms,
   'customProjection',CASE WHEN c.id IS NULL THEN NULL ELSE jsonb_build_object('catalogId',c.catalog_id,'curveId',c.curve_id,'version',c.version,'outputNode',c.output_node,'position',c.position,'selection',c.selection,'implementation',c.implementation) END))
   FROM trading.swap_floating_sides s JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) JOIN payment p ON p.id=s.payment_rule_id LEFT JOIN observation o ON o.id=s.observation_schedule_id LEFT JOIN trading.custom_curve_projection_bindings c ON(c.catalog_id,c.id)=(s.catalog_id,s.custom_projection_id) WHERE(s.catalog_id,s.id)=(catalog,float_id)
 )END;
$$;
CREATE FUNCTION trading.scheduled_swap_terms(catalog uuid,trade uuid,revision_number integer,instrument uuid) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH slots AS(
  SELECT selected.slot,selected.side_id FROM trading.swap_instruments i CROSS JOIN LATERAL(VALUES(0,i.scheduled_first_id),(1,i.scheduled_second_id)) selected(slot,side_id) WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument) AND i.scheduled_first_id IS NOT NULL
 ),side_terms AS(
  SELECT selected.slot,selected.side_id,jsonb_build_object('basis',a.basis,'quantity',jsonb_build_object('revision',jsonb_build_object('catalogId',a.reference_catalog_id,'revisionId',a.reference_revision_id),'code',a.quantity_code),'amount',trim_scale(a.contract_amount)::text,'allocation',a.allocation,'priceApplication',a.price_application,
   'power',CASE WHEN a.clock_code IS NULL THEN NULL ELSE jsonb_build_object('clock',jsonb_build_object('revision',jsonb_build_object('catalogId',a.reference_catalog_id,'revisionId',a.reference_revision_id),'code',a.clock_code),'product',a.delivery_product,'startHour',a.start_hour,'endHour',a.end_hour,'saturday',a.saturday,'calendar',CASE WHEN a.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',a.reference_catalog_id,'revisionId',a.reference_revision_id),'code',a.calendar_code) END) END,
   'periods',jsonb_agg(trading.swap_period_input(catalog,p.fixed_side_id,p.floating_side_id)||jsonb_build_object('amount',trim_scale(q.amount)::text) ORDER BY p.ordinal)) terms,
   jsonb_build_object('id',selected.side_id,'periods',jsonb_agg(jsonb_build_object('id',coalesce(p.fixed_side_id,p.floating_side_id),'n',trim_scale(p.quantity_numerator)::text,'d',trim_scale(p.quantity_denominator)::text,'projection_contracts',(SELECT jsonb_agg(r.contract_id ORDER BY r.ordinal) FROM trading.swap_reset_projections r WHERE(r.catalog_id,r.side_id)=(p.catalog_id,p.floating_side_id)),'projection_weights',(SELECT jsonb_agg(r.weight::text ORDER BY r.ordinal) FROM trading.swap_reset_projections r WHERE(r.catalog_id,r.side_id)=(p.catalog_id,p.floating_side_id))) ORDER BY p.ordinal)) binding
  FROM slots selected JOIN trading.swap_quantity_authoring a ON(a.catalog_id,a.trade_id,a.revision,a.slot)=(catalog,trade,revision_number,selected.slot)
  JOIN trading.swap_scheduled_periods p ON(p.catalog_id,p.side_id)=(catalog,selected.side_id)
  LEFT JOIN trading.swap_quantity_amounts q ON(q.catalog_id,q.trade_id,q.revision,q.slot,q.ordinal)=(catalog,trade,revision_number,selected.slot,p.ordinal)
  GROUP BY selected.slot,selected.side_id,a.basis,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.contract_amount,a.allocation,a.price_application,a.clock_code,a.delivery_product,a.start_hour,a.end_hour,a.saturday,a.calendar_code
 )
 SELECT CASE WHEN count(*)=0 THEN NULL ELSE jsonb_build_object('terms',jsonb_build_object('implementation','independent_scheduled_financial_swap_v1','first',jsonb_agg(terms ORDER BY slot)->0,'second',jsonb_agg(terms ORDER BY slot)->1),'bindings',jsonb_agg(binding ORDER BY slot)) END FROM side_terms;
$$;
REVOKE ALL ON FUNCTION trading.swap_period_input(uuid,uuid,uuid),trading.scheduled_swap_terms(uuid,uuid,integer,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.swap_period_input(uuid,uuid,uuid),trading.scheduled_swap_terms(uuid,uuid,integer,uuid) TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker;

ALTER TABLE trading.swap_instruments ADD UNIQUE(catalog_id,instrument_id,scheduled_first_id),ADD UNIQUE(catalog_id,instrument_id,scheduled_second_id);
ALTER TABLE automation.pilot_report_discounts ADD COLUMN scheduled_first_side_id uuid,ADD COLUMN scheduled_second_side_id uuid,
 ADD FOREIGN KEY(catalog_id,instrument_id,scheduled_first_side_id) REFERENCES trading.swap_instruments(catalog_id,instrument_id,scheduled_first_id),
 ADD FOREIGN KEY(catalog_id,instrument_id,scheduled_second_side_id) REFERENCES trading.swap_instruments(catalog_id,instrument_id,scheduled_second_id);
DO $$DECLARE key_name text;BEGIN
 SELECT conname INTO STRICT key_name FROM pg_constraint WHERE conrelid='automation.pilot_report_discounts'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%num_nonnulls(fixed_side_id, first_floating_side_id, floating_side_id, ppa_side_id)%';
 EXECUTE format('ALTER TABLE automation.pilot_report_discounts DROP CONSTRAINT %I',key_name);
END$$;
ALTER TABLE automation.pilot_report_discounts ADD CHECK(num_nonnulls(fixed_side_id,first_floating_side_id,floating_side_id,ppa_side_id,scheduled_first_side_id,scheduled_second_side_id)=1);
DO $$DECLARE signature regprocedure='automation.publish_pilot_report_pre_renewal(uuid,jsonb)'::regprocedure;definition text;before text;BEGIN
 definition=pg_get_functiondef(signature);before='x.currency,x.rate_policy,x.interpolation_policy,x.ppa,x.first_floating';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report discount writer';END IF;
 definition=replace(definition,before,before||',x.scheduled_first,x.scheduled_second');
 before='currency text,rate_policy text,interpolation_policy text,ppa uuid,first_floating uuid)';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report discount parser';END IF;
 EXECUTE replace(definition,before,'currency text,rate_policy text,interpolation_policy text,ppa uuid,first_floating uuid,scheduled_first uuid,scheduled_second uuid)');
END$$;
COMMIT;
