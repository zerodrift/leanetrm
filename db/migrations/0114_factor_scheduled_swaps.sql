-- Typed contractual sizing: selected units are immutable exact reference keys.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.swap_quantity_factors (
 catalog_id uuid NOT NULL,id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 value ref_data.exact_number NOT NULL CHECK(value>0),numerator_quantity ref_data.code NOT NULL,denominator_quantity ref_data.code NOT NULL,
 implementation text NOT NULL CHECK(implementation='contractual_quantity_factor_v1'),
 conversion_implementation text NOT NULL CHECK(conversion_implementation='consistent_exact_unit_ratios_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,reference_catalog_id,reference_revision_id,value,numerator_quantity,denominator_quantity,implementation,conversion_implementation),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,numerator_quantity) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,denominator_quantity) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.swap_quantity_factors ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.swap_quantity_factors FORCE ROW LEVEL SECURITY;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.swap_quantity_factors FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE POLICY scoped_read ON trading.swap_quantity_factors FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY reference_read ON trading.swap_quantity_factors AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY risk_worker_scoped_read ON trading.swap_quantity_factors FOR SELECT TO openetrm_risk_worker USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY risk_worker_reference_read ON trading.swap_quantity_factors AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY risk_worker_service ON trading.swap_quantity_factors AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
GRANT SELECT ON trading.swap_quantity_factors TO openetrm_runtime,openetrm_risk_worker;
GRANT SELECT,INSERT ON trading.swap_quantity_factors TO openetrm_security_owner;
ALTER TABLE trading.swap_instruments ADD COLUMN quantity_factor_id uuid,ADD FOREIGN KEY(catalog_id,quantity_factor_id) REFERENCES trading.swap_quantity_factors(catalog_id,id);
DO $$DECLARE constraint_name text;BEGIN
 FOR constraint_name IN SELECT conname FROM pg_constraint WHERE conrelid='trading.swap_instruments'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%implementation%' LOOP
  EXECUTE format('ALTER TABLE trading.swap_instruments DROP CONSTRAINT %I',constraint_name);
 END LOOP;
 SELECT conname INTO STRICT constraint_name FROM pg_constraint WHERE conrelid='trading.swap_instruments'::regclass AND contype='u' AND pg_get_constraintdef(oid) LIKE '%fixed_coefficient, discounting, implementation,%';
 EXECUTE format('ALTER TABLE trading.swap_instruments DROP CONSTRAINT %I',constraint_name);
 SELECT conname INTO STRICT constraint_name FROM pg_constraint WHERE conrelid='trading.swap_quantity_authoring'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%basis%period_total%';
 EXECUTE format('ALTER TABLE trading.swap_quantity_authoring DROP CONSTRAINT %I',constraint_name);
END$$;
ALTER TABLE trading.swap_instruments
 ADD CHECK(implementation IN('independent_side_financial_swap_v2','independent_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1')),
 ADD CHECK((implementation IN('independent_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1'))=(scheduled_first_id IS NOT NULL AND scheduled_second_id IS NOT NULL)),
 ADD CHECK((implementation='factor_scheduled_financial_swap_v1')=(quantity_factor_id IS NOT NULL)),
 ADD UNIQUE NULLS NOT DISTINCT(catalog_id,fixed_side_id,first_floating_side_id,floating_side_id,fixed_coefficient,discounting,implementation,scheduled_first_id,scheduled_second_id,second_quantity_numerator,second_quantity_denominator,quantity_factor_id);
ALTER TABLE trading.swap_quantity_authoring ADD CHECK(basis IN('period_total','calendar_day_rate','contract_total','selected_interval_power','factor_of_first_side_v1')),
 ADD CHECK(basis<>'factor_of_first_side_v1' OR(slot=1 AND contract_amount IS NULL AND allocation IS NULL AND clock_code IS NULL));

CREATE OR REPLACE FUNCTION security.save_scheduled_swap(catalog uuid,body jsonb,compiled jsonb,quantity numeric,valuation timestamptz,booking jsonb,input_pins uuid[])
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;permission text;publication_number integer;
 operation uuid=(body->>'operationId')::uuid;trade uuid=(body->>'tradeId')::uuid;expected integer=(body->>'expectedRevision')::integer;book uuid=(body->>'bookId')::uuid;definition uuid=(body->>'definitionRevisionId')::uuid;
 ref_catalog uuid;ref_revision uuid;commodity text=body->>'commodity';coefficient numeric=(body->>'firstCoefficient')::numeric;discount_enabled boolean=(body->>'discounting')::boolean;
 effective timestamptz;executed timestamptz;from_at timestamptz;instrument uuid;binding uuid;ids uuid[]='{}';fixed_id uuid;first_float uuid;second_float uuid;child uuid;quote text;
 slot integer;n integer;side_body jsonb;period_body jsonb;shape jsonb;side_id uuid;children jsonb;digest_value bytea;num numeric;den numeric;total_num numeric;total_den numeric;common numeric;kind boolean;previous_end date;starts date;ends date;payment timestamptz;
 version text=body->'scheduled'->>'implementation';factor_body jsonb=body->'scheduled'->'factor';factor_id uuid;side_commodity text;
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
  version NOT IN('independent_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1') OR version IS NULL OR body->>'swapType' IS DISTINCT FROM 'scheduled_financial_swap' OR
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
  IF NOT EXISTS(SELECT 1 FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND i.scheduled_first_id IS NOT NULL AND i.implementation=version) OR EXISTS(
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
 IF version='factor_scheduled_financial_swap_v1' THEN
  IF factor_body IS NULL OR jsonb_typeof(factor_body)<>'object' OR factor_body->>'implementation' IS DISTINCT FROM 'contractual_quantity_factor_v1' OR factor_body->>'unitConversionImplementation' IS DISTINCT FROM 'consistent_exact_unit_ratios_v1'
   OR(factor_body->>'value')::numeric IS NULL OR(factor_body->>'value')::numeric<=0
   OR(factor_body->'numeratorQuantity'->'revision'->>'catalogId')::uuid IS DISTINCT FROM ref_catalog OR(factor_body->'numeratorQuantity'->'revision'->>'revisionId')::uuid IS DISTINCT FROM ref_revision
   OR(factor_body->'denominatorQuantity'->'revision'->>'catalogId')::uuid IS DISTINCT FROM ref_catalog OR(factor_body->'denominatorQuantity'->'revision'->>'revisionId')::uuid IS DISTINCT FROM ref_revision
   OR body->'scheduled'->'second'->>'basis' IS DISTINCT FROM 'factor_of_first_side_v1' OR body->'scheduled'->'first'->>'basis'='factor_of_first_side_v1'
   OR body->'scheduled'->'second'->>'amount' IS NOT NULL OR body->'scheduled'->'second'->>'allocation' IS NOT NULL OR body->'scheduled'->'second'->>'power' IS NOT NULL
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'scheduled'->'second'->'periods') p WHERE p->>'amount' IS NOT NULL)
  THEN RAISE EXCEPTION 'quantity_factor';END IF;
  SELECT id INTO factor_id FROM trading.swap_quantity_factors f WHERE(f.catalog_id,f.reference_catalog_id,f.reference_revision_id,f.value,f.numerator_quantity,f.denominator_quantity,f.implementation,f.conversion_implementation)=
   (catalog,ref_catalog,ref_revision,(factor_body->>'value')::numeric,factor_body->'numeratorQuantity'->>'code',factor_body->'denominatorQuantity'->>'code',factor_body->>'implementation',factor_body->>'unitConversionImplementation');
  IF factor_id IS NULL THEN
   factor_id=gen_random_uuid();INSERT INTO trading.swap_quantity_factors VALUES(catalog,factor_id,ref_catalog,ref_revision,(factor_body->>'value')::numeric,factor_body->'numeratorQuantity'->>'code',factor_body->'denominatorQuantity'->>'code',factor_body->>'implementation',factor_body->>'unitConversionImplementation',publication_number,from_at);
  END IF;
 ELSIF factor_body IS NOT NULL THEN RAISE EXCEPTION 'quantity_factor';END IF;
 -- Retain the second side first, so its exact quote qualifies fixed constituents.
 FOR slot IN REVERSE 1..0 LOOP
  side_body=body->'scheduled'->CASE slot WHEN 0 THEN 'first' ELSE 'second' END;shape=compiled->slot;children='[]';previous_end=NULL;total_num=0;total_den=1;
  IF jsonb_typeof(side_body->'periods')<>'array' OR jsonb_array_length(side_body->'periods') NOT BETWEEN 1 AND 120 OR jsonb_array_length(side_body->'periods')<>jsonb_array_length(shape->'periods') OR side_body->>'priceApplication' IS DISTINCT FROM 'period_average_times_quantity_v1' THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
  kind=(side_body->'periods'->0->>'fixed') IS NOT NULL;
  IF (slot=1 OR version='factor_scheduled_financial_swap_v1') AND kind THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
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
    side_commodity=commodity;
    IF version='factor_scheduled_financial_swap_v1' AND slot=1 THEN
     SELECT m.commodity_code INTO STRICT side_commodity FROM market_data.curves m WHERE(m.catalog_id,m.revision_id,m.code)=(catalog,definition,period_body->'floating'->>'curve');
    END IF;
    child=security.retain_floating_swap_side(catalog,definition,ref_catalog,ref_revision,side_commodity,period_body->'floating',valuation,publication_number,from_at,period_pins);
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
 SELECT i.instrument_id INTO instrument FROM trading.swap_instruments i WHERE i.catalog_id=catalog AND i.scheduled_first_id=ids[1] AND i.scheduled_second_id=ids[2] AND i.fixed_coefficient=coefficient AND i.discounting=discount_enabled AND i.second_quantity_numerator=num AND i.second_quantity_denominator=den AND i.implementation=version AND i.quantity_factor_id IS NOT DISTINCT FROM factor_id;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments(catalog_id,instrument_id,fixed_side_id,first_floating_side_id,floating_side_id,fixed_coefficient,discounting,implementation,publication_order,effective_at,scheduled_first_id,scheduled_second_id,second_quantity_numerator,second_quantity_denominator,quantity_factor_id)
   VALUES(catalog,instrument,fixed_id,first_float,second_float,coefficient,discount_enabled,version,publication_number,from_at,ids[1],ids[2],num,den,factor_id);
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
   IF side_body->>'basis' NOT IN('contract_total','factor_of_first_side_v1') THEN INSERT INTO trading.swap_quantity_amounts VALUES(catalog,trade,coalesce(head.revision,0)+1,slot,n,(side_body->'periods'->n->>'amount')::numeric,publication_number,effective,ref_catalog,ref_revision,(power->>'durationTicks')::bigint,(power->'total'->>'numerator')::numeric,(power->'total'->>'denominator')::numeric,power->>'energyQuantity');END IF;
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
CREATE OR REPLACE FUNCTION trading.guard_swap_contract_version() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE current_scheduled boolean;prior_scheduled boolean;current_version text;prior_version text;
BEGIN
 SELECT scheduled_first_id IS NOT NULL,implementation INTO STRICT current_scheduled,current_version FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 IF current_scheduled IS DISTINCT FROM(NEW.event_schema='scheduled_financial_swap_booking_v1') THEN RAISE EXCEPTION 'swap_amendment';END IF;
 IF NEW.previous_revision IS NOT NULL THEN
  SELECT i.scheduled_first_id IS NOT NULL,i.implementation INTO STRICT prior_scheduled,prior_version FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) WHERE(r.catalog_id,r.trade_id,r.revision)=(NEW.catalog_id,NEW.trade_id,NEW.previous_revision);
  IF current_scheduled IS DISTINCT FROM prior_scheduled OR current_version IS DISTINCT FROM prior_version THEN RAISE EXCEPTION 'swap_amendment';END IF;
 END IF;
 RETURN NEW;
END$$;
CREATE OR REPLACE FUNCTION trading.scheduled_swap_terms(catalog uuid,trade uuid,revision_number integer,instrument uuid) RETURNS jsonb
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
 SELECT CASE WHEN count(*)=0 THEN NULL ELSE jsonb_build_object('terms',jsonb_build_object('implementation',(SELECT implementation FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument)),'first',jsonb_agg(terms ORDER BY slot)->0,'second',jsonb_agg(terms ORDER BY slot)->1)||coalesce((SELECT jsonb_build_object('factor',jsonb_build_object('value',trim_scale(f.value)::text,'numeratorQuantity',jsonb_build_object('revision',jsonb_build_object('catalogId',f.reference_catalog_id,'revisionId',f.reference_revision_id),'code',f.numerator_quantity),'denominatorQuantity',jsonb_build_object('revision',jsonb_build_object('catalogId',f.reference_catalog_id,'revisionId',f.reference_revision_id),'code',f.denominator_quantity),'implementation',f.implementation,'unitConversionImplementation',f.conversion_implementation)) FROM trading.swap_instruments i JOIN trading.swap_quantity_factors f ON(f.catalog_id,f.id)=(i.catalog_id,i.quantity_factor_id) WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument)),'{}'::jsonb),'bindings',jsonb_agg(binding ORDER BY slot)) END FROM side_terms;
$$;
CREATE OR REPLACE FUNCTION settlement_data.swap_completion_at(catalog uuid,trade uuid,revision integer,instrument uuid,membership integer,period_from date,period_to date)
 RETURNS timestamptz LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE i trading.swap_instruments;basis text;period integer;child uuid;completed timestamptz;BEGIN
 SELECT * INTO STRICT i FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF i.scheduled_first_id IS NULL THEN RETURN period_to::timestamp AT TIME ZONE 'UTC';END IF;
 SELECT a.basis INTO basis FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision,a.slot)=(catalog,trade,swap_completion_at.revision,membership);
 IF basis='factor_of_first_side_v1' THEN
  IF membership<>1 THEN RAISE EXCEPTION 'settlement_completion';END IF;
  RETURN settlement_data.swap_completion_at(catalog,trade,revision,instrument,0,period_from,period_to);
 END IF;
 IF basis IS NULL THEN RAISE EXCEPTION 'settlement_completion';END IF;
 IF basis IN('period_total','calendar_day_rate','contract_total') THEN RETURN period_to::timestamp AT TIME ZONE 'UTC';END IF;
 IF basis<>'selected_interval_power' THEN RAISE EXCEPTION 'settlement_completion';END IF;
 SELECT t.child INTO STRICT child FROM settlement_data.swap_payment_terms(catalog,instrument,1,membership,period_from,period_to)t;
 SELECT p.ordinal INTO STRICT period FROM trading.swap_scheduled_periods p WHERE p.catalog_id=catalog
  AND p.side_id=CASE WHEN membership=0 THEN i.scheduled_first_id ELSE i.scheduled_second_id END AND coalesce(p.fixed_side_id,p.floating_side_id)=child;
 SELECT max(p.ends_at) INTO completed FROM trading.swap_power_quantity_intervals p WHERE(p.catalog_id,p.trade_id,p.revision,p.slot,p.period_ordinal)=(catalog,trade,swap_completion_at.revision,membership,period);
 IF completed IS NULL THEN RAISE EXCEPTION 'settlement_completion';END IF;RETURN completed;
END$$;
CREATE OR REPLACE FUNCTION settlement_data.swap_completion_source(catalog uuid,trade uuid,revision integer,instrument uuid,membership integer) RETURNS text
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE scheduled boolean;basis text;BEGIN
 SELECT scheduled_first_id IS NOT NULL INTO STRICT scheduled FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF NOT scheduled THEN RETURN 'date_only_utc_v1';END IF;
 SELECT a.basis INTO basis FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision,a.slot)=(catalog,trade,swap_completion_source.revision,membership);
 IF basis='factor_of_first_side_v1' THEN
  IF membership<>1 THEN RAISE EXCEPTION 'settlement_completion';END IF;
  RETURN settlement_data.swap_completion_source(catalog,trade,revision,instrument,0);
 END IF;
 IF basis='selected_interval_power' THEN RETURN 'retained_quantity_interval_end_v1';END IF;
 IF basis IN('period_total','calendar_day_rate','contract_total') THEN RETURN 'date_only_utc_v1';END IF;
 RAISE EXCEPTION 'settlement_completion';
END$$;
COMMIT;
