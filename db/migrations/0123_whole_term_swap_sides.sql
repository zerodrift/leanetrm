-- Whole-term swap definitions own period parameters, never child sides.
-- This is a schema-window release: reset/archive retained swap definitions and
-- rebook them through the current API before admitting the new definitions.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.swap_instruments) OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions)
  OR EXISTS(SELECT 1 FROM trading.swap_fixed_sides) OR EXISTS(SELECT 1 FROM trading.swap_floating_sides)
  OR EXISTS(SELECT 1 FROM trading.swap_scheduled_sides)
 THEN RAISE EXCEPTION 'whole_term_swap_rebooking_required';END IF;
END$$;

ALTER TABLE trading.swap_scheduled_sides RENAME TO swap_whole_term_sides;
ALTER TABLE trading.swap_whole_term_sides DROP CONSTRAINT swap_scheduled_sides_implementation_check;
ALTER TABLE trading.swap_whole_term_sides ADD COLUMN kind text NOT NULL CHECK(kind IN('fixed','floating')),
 ADD COLUMN definition_version text NOT NULL CHECK(definition_version IN('FixedSide_v4','FloatingSide_v8')),
 ADD CHECK((kind='fixed')=(definition_version='FixedSide_v4'));
ALTER TABLE trading.swap_whole_term_sides ALTER COLUMN implementation SET DEFAULT 'whole_term_swap_side_v1';
ALTER TABLE trading.swap_whole_term_sides ADD CHECK(implementation='whole_term_swap_side_v1');

CREATE TABLE trading.swap_side_periods (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 119),
 id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('fixed','floating')),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,quote_code ref_data.code NOT NULL,
 delivery_from date NOT NULL,delivery_to date NOT NULL,payment_at ref_data.instant NOT NULL,payment_rule_id uuid NOT NULL,
 fixed_price ref_data.exact_number,definition_revision_id uuid,curve_code ref_data.code,contract_id uuid,index_code ref_data.code,
 first_fixing_at ref_data.instant,averaging text,price_implementation text NOT NULL,custom_projection_id uuid,
 spread ref_data.exact_number,observation_schedule_id uuid,
 settlement_mode text,settlement_index_code ref_data.code,settlement_projection_policy text,
 quantity_numerator numeric NOT NULL,quantity_denominator numeric NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,ordinal),UNIQUE(catalog_id,id),
 CHECK(delivery_from<delivery_to AND extract(day FROM delivery_from)=1 AND delivery_to=(delivery_from+INTERVAL '1 month')::date),
 CHECK(quantity_numerator>0 AND quantity_denominator>0 AND scale(quantity_numerator)=0 AND scale(quantity_denominator)=0 AND length(quantity_numerator::text)<=1234 AND length(quantity_denominator::text)<=1234),
 CHECK((kind='fixed' AND fixed_price IS NOT NULL AND num_nonnulls(definition_revision_id,curve_code,contract_id,index_code,first_fixing_at,averaging,custom_projection_id,spread,observation_schedule_id)=0 AND num_nonnulls(settlement_mode,settlement_index_code,settlement_projection_policy)=0 AND price_implementation='fixed_total_payment_v1') OR
  (kind='floating' AND fixed_price IS NULL AND num_nonnulls(definition_revision_id,curve_code,index_code,first_fixing_at,averaging,spread)=6 AND settlement_mode IS NOT NULL AND settlement_index_code IS NOT NULL AND ((settlement_mode='curve_target' AND settlement_index_code=index_code AND settlement_projection_policy IS NULL) OR (settlement_mode='override' AND settlement_index_code<>index_code AND settlement_projection_policy IS NOT NULL AND settlement_projection_policy='forward_curve_no_basis_v1' AND custom_projection_id IS NULL)) AND price_implementation='index_observation_fixings_v1' AND first_fixing_at<payment_at AND averaging IN('monthly_index_price','calendar_day_arithmetic','observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted') AND (averaging='monthly_index_price')=(contract_id IS NOT NULL))),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.swap_whole_term_sides(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,payment_rule_id) REFERENCES trading.swap_payment_rules(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,settlement_index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,custom_projection_id) REFERENCES trading.custom_curve_projection_bindings(catalog_id,id),
 FOREIGN KEY(catalog_id,observation_schedule_id) REFERENCES trading.swap_observation_schedules(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_period_projections (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,period_ordinal integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 365),
 definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,contract_id uuid NOT NULL,weight ref_data.exact_number NOT NULL CHECK(weight>0),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,period_ordinal,ordinal),UNIQUE(catalog_id,side_id,period_ordinal,contract_id),
 FOREIGN KEY(catalog_id,side_id,period_ordinal) REFERENCES trading.swap_side_periods(catalog_id,side_id,ordinal),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_side_periods','swap_period_projections'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('CREATE POLICY reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY risk_worker_scoped_read ON trading.%I FOR SELECT TO openetrm_risk_worker USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('CREATE POLICY risk_worker_reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY risk_worker_service ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime,openetrm_risk_worker',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;
ALTER TABLE trading.swap_instruments ALTER COLUMN floating_side_id DROP NOT NULL;
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='trading.swap_instruments'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%implementation%' LOOP
  EXECUTE format('ALTER TABLE trading.swap_instruments DROP CONSTRAINT %I',item.conname);
 END LOOP;
END$$;
ALTER TABLE trading.swap_instruments
 ADD CHECK(implementation IN('independent_side_financial_swap_v2','whole_term_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1')),
 ADD CHECK((implementation IN('whole_term_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1'))=(scheduled_first_id IS NOT NULL AND scheduled_second_id IS NOT NULL)),
 ADD CHECK(scheduled_first_id IS NULL OR num_nonnulls(fixed_side_id,first_floating_side_id,floating_side_id)=0),
 ADD CHECK(scheduled_first_id IS NOT NULL OR floating_side_id IS NOT NULL),
 ADD CHECK((implementation='factor_scheduled_financial_swap_v1')=(quantity_factor_id IS NOT NULL));

CREATE FUNCTION security.prepare_fixed_swap_period(catalog uuid,ref_catalog uuid,ref_revision uuid,quote text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q ref_data.quote_specifications;price numeric;payment timestamptz;rule uuid;BEGIN
 SELECT * INTO STRICT q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,quote);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('price','currency','deliveryFrom','deliveryTo','paymentAt','paymentSchedule')) OR body->>'paymentAt' IS NOT NULL OR body->>'currency' IS DISTINCT FROM q.currency_code THEN RAISE EXCEPTION 'swap_input';END IF;
 price=(body->>'price')::numeric;
 IF price IS NULL OR abs(price)>=1e24 OR scale(price)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
 payment=security.swap_payment_date(ref_catalog,ref_revision,(body->>'deliveryFrom')::date,(body->>'deliveryTo')::date,body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
 IF payment IS NULL OR valuation>=payment THEN RAISE EXCEPTION 'swap_input';END IF;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 RETURN jsonb_build_object('kind','fixed','quote',q.code,'price',trim_scale(price)::text,'deliveryFrom',body->>'deliveryFrom','deliveryTo',body->>'deliveryTo','paymentAt',payment,'paymentRule',rule,'implementation','fixed_total_payment_v1');
END$$;
CREATE OR REPLACE FUNCTION security.prepare_floating_swap_period(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz,input_pins uuid[])
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cv market_data.curves;q ref_data.quote_specifications;target ref_data.settlement_targets;k market_data.contracts;
 selection jsonb;selected_index text;selection_mode text;projection_policy text;
 side uuid;rule uuid;starts date;ends date;fixing timestamptz;payment timestamptz;sampling text;spread_value numeric;fixing_evidence jsonb;custom_projection jsonb;custom_binding uuid;observation_schedule jsonb;observation_rows jsonb;observation_binding uuid;
BEGIN
 IF body IS NULL OR jsonb_typeof(body)<>'object' OR body->>'paymentAt' IS NOT NULL OR body->>'averaging' IS NOT NULL OR body->'paymentSchedule' IS NULL OR body->>'spread' IS NULL OR body->>'firstFixingAt' IS NOT NULL
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('curve','index','currency','deliveryFrom','deliveryTo','firstFixingAt','paymentAt','averaging','paymentSchedule','resetFrequency','averagingMethod','spread','customProjection','observationSchedule','settlement')) THEN RAISE EXCEPTION 'swap_input';END IF;
 starts=(body->>'deliveryFrom')::date;ends=(body->>'deliveryTo')::date;spread_value=(body->>'spread')::numeric;
 sampling=security.swap_observation_sampling(body->>'resetFrequency',body->>'averagingMethod');
 observation_schedule=NULLIF(body->'observationSchedule','null'::jsonb);
 IF(sampling IN('observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted')) IS DISTINCT FROM(observation_schedule IS NOT NULL) THEN RAISE EXCEPTION 'swap_averaging';END IF;
 payment=security.swap_payment_date(ref_catalog,ref_revision,starts,ends,body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
 IF starts IS NULL OR ends IS NULL OR NOT isfinite(starts) OR NOT isfinite(ends) OR extract(day FROM starts)<>1 OR ends IS DISTINCT FROM(starts+INTERVAL '1 month')::date OR sampling IS NULL OR
  payment IS NULL OR valuation>=payment OR
  spread_value IS NULL OR abs(spread_value)>=1e24 OR scale(spread_value)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
 SELECT * INTO cv FROM market_data.curves WHERE catalog_id=catalog AND revision_id=definition AND code=body->>'curve' AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.quote_code);
 SELECT * INTO target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.settlement_target_code);
 IF cv.commodity_code IS DISTINCT FROM commodity OR q.kind IS DISTINCT FROM 'outright' OR target.kind IS DISTINCT FROM 'index' OR target.index_b_code IS NOT NULL OR target.index_a_code IS DISTINCT FROM body->>'index' OR q.currency_code IS DISTINCT FROM body->>'currency' OR
  NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,body->>'index') AND(i.currency_code,i.commodity_code,i.location_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,cv.location_code,u.unit_code,q.multiplier)) THEN RAISE EXCEPTION 'swap_definition';END IF;
 selection=body->'settlement';selection_mode=selection->>'mode';projection_policy=selection->>'projectionPolicy';
 IF jsonb_typeof(selection) IS DISTINCT FROM 'object' OR NOT selection ?& ARRAY['mode','index','projectionPolicy'] OR (SELECT count(*) FROM jsonb_object_keys(selection))<>3 THEN RAISE EXCEPTION 'price_settlement_selection';END IF;
 IF selection_mode='curve_target' AND selection->'index'='null'::jsonb AND selection->'projectionPolicy'='null'::jsonb THEN selected_index=target.index_a_code;
 ELSIF selection_mode='override' AND selection->>'index' IS NOT NULL AND selection->>'index'<>target.index_a_code AND projection_policy='forward_curve_no_basis_v1' AND NULLIF(body->'customProjection','null'::jsonb) IS NULL THEN selected_index=selection->>'index';
 ELSE RAISE EXCEPTION 'price_settlement_selection';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
  WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,selected_index) AND(i.currency_code,i.commodity_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,u.unit_code,q.multiplier)) THEN RAISE EXCEPTION 'price_settlement_selection';END IF;
 IF observation_schedule IS NULL THEN
  fixing_evidence=security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,cv.code,selected_index,starts,ends,sampling,valuation,payment,input_pins);
 ELSE
  observation_rows=security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,cv.code,selected_index,starts,ends,sampling,observation_schedule);
  fixing_evidence=security.swap_observation_fixing_evidence(catalog,definition,ref_catalog,ref_revision,cv.code,selected_index,valuation,payment,input_pins,observation_rows);
 END IF;
 fixing=(fixing_evidence->>'firstFixing')::timestamp AT TIME ZONE 'UTC';
 custom_projection=security.validate_custom_projection(catalog,body->'customProjection');
 IF observation_schedule IS NOT NULL AND custom_projection IS NOT NULL THEN RAISE EXCEPTION 'swap_custom_projection_averaging';END IF;
 IF custom_projection IS NOT NULL AND((sampling='monthly_index_price' AND custom_projection->>'selection'<>'single_output_position_v1') OR(sampling='calendar_day_arithmetic' AND custom_projection->>'selection'<>'exact_delivery_interval_v1') OR EXISTS(
  SELECT 1 FROM security.custom_curve_native_sources(catalog,(custom_projection->>'curveId')::uuid,(custom_projection->>'version')::integer) p WHERE(p.source_catalog_id,p.source_revision_id) IS DISTINCT FROM(catalog,definition))) THEN RAISE EXCEPTION 'custom_projection';END IF;
 custom_binding=security.retain_custom_projection(catalog,custom_projection,publication_number,from_at);
 IF observation_schedule IS NULL THEN
 IF(SELECT count(*) FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
  WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
   (sampling='monthly_index_price' AND(c.delivery_from,c.delivery_to)=(starts,ends) OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends))<>(CASE WHEN sampling='monthly_index_price' THEN 1 ELSE ends-starts END) OR
  sampling='calendar_day_arithmetic' AND(SELECT count(DISTINCT c.delivery_from) FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends)<>ends-starts THEN RAISE EXCEPTION 'swap_projection';END IF;
 IF sampling='monthly_index_price' THEN
  SELECT c.* INTO STRICT k FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND(c.delivery_from,c.delivery_to)=(starts,ends);
 END IF;
 SELECT jsonb_agg(jsonb_build_object('ordinal',r.ordinal,'contractId',r.id,'weight',1) ORDER BY r.ordinal) INTO observation_rows FROM(
  SELECT(row_number() OVER(ORDER BY c.delivery_from,c.id)-1)::integer AS ordinal,c.id FROM market_data.contracts c
  JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
  WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
   (sampling='monthly_index_price' AND c.id=k.id OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends)
 )r;
 ELSE
  observation_binding=security.retain_swap_observation_schedule(catalog,definition,ref_catalog,ref_revision,cv.code,selected_index,q,starts,ends,sampling,observation_schedule,observation_rows,publication_number,from_at);
 END IF;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 RETURN jsonb_build_object('kind','floating','definitionRevision',definition,'curve',cv.code,'contract',k.id,
  'index',body->>'index','settlementMode',selection_mode,'settlementIndex',selected_index,'settlementProjectionPolicy',projection_policy,'quote',q.code,'deliveryFrom',starts,'deliveryTo',ends,'firstFixingAt',fixing,'paymentAt',payment,
  'averaging',sampling,'implementation','index_observation_fixings_v1','paymentRule',rule,'customProjection',custom_binding,
  'spread',trim_scale(spread_value)::text,'observationSchedule',observation_binding,'projections',observation_rows,'fixingEvidence',fixing_evidence);
END$$;
CREATE OR REPLACE FUNCTION security.save_scheduled_swap(catalog uuid,body jsonb,compiled jsonb,quantity numeric,valuation timestamptz,booking jsonb,input_pins uuid[])
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;permission text;publication_number integer;
 operation uuid=(body->>'operationId')::uuid;trade uuid=(body->>'tradeId')::uuid;expected integer=(body->>'expectedRevision')::integer;book uuid=(body->>'bookId')::uuid;definition uuid=(body->>'definitionRevisionId')::uuid;
 ref_catalog uuid;ref_revision uuid;commodity text=body->>'commodity';coefficient numeric=(body->>'firstCoefficient')::numeric;discount_enabled boolean=(body->>'discounting')::boolean;
 effective timestamptz;executed timestamptz;from_at timestamptz;instrument uuid;binding uuid;ids uuid[]='{}';fixed_id uuid;first_float uuid;second_float uuid;child uuid;quote text;
 slot integer;n integer;side_body jsonb;period_body jsonb;shape jsonb;side_id uuid;children jsonb;digest_value bytea;num numeric;den numeric;total_num numeric;total_den numeric;common numeric;kind boolean;previous_end date;starts date;ends date;payment timestamptz;
 version text=body->'scheduled'->>'implementation';factor_body jsonb=body->'scheduled'->'factor';factor_id uuid;side_commodity text;
 parameters jsonb;side_quote text;side_index text;side_selection jsonb;request_value jsonb;side_fixings jsonb='[]';power jsonb;period_pins uuid[];
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
  version NOT IN('whole_term_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1') OR version IS NULL OR body->>'swapType' IS DISTINCT FROM 'scheduled_financial_swap' OR
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
   SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_side_periods p ON p.catalog_id=i.catalog_id AND p.side_id IN(i.scheduled_first_id,i.scheduled_second_id)
   WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND(valuation>=p.payment_at OR valuation>=p.first_fixing_at)) THEN RAISE EXCEPTION 'swap_amendment';END IF;
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
    parameters=security.prepare_fixed_swap_period(catalog,ref_catalog,ref_revision,quote,period_body->'fixed',valuation,publication_number,from_at);
   ELSE
    SELECT coalesce(array_agg(value::uuid),'{}'::uuid[]) INTO period_pins FROM jsonb_array_elements_text(shape->'periods'->n->'fixingPins');
    IF NOT period_pins<@input_pins THEN RAISE EXCEPTION 'index_fixing_inputs';END IF;
    side_commodity=commodity;
    IF version='factor_scheduled_financial_swap_v1' AND slot=1 THEN
     SELECT m.commodity_code INTO STRICT side_commodity FROM market_data.curves m WHERE(m.catalog_id,m.revision_id,m.code)=(catalog,definition,period_body->'floating'->>'curve');
    END IF;
    parameters=security.prepare_floating_swap_period(catalog,definition,ref_catalog,ref_revision,side_commodity,period_body->'floating',valuation,publication_number,from_at,period_pins);
    quote=parameters->>'quote';
   END IF;
   starts=(parameters->>'deliveryFrom')::date;ends=(parameters->>'deliveryTo')::date;payment=(parameters->>'paymentAt')::timestamptz;
   IF n=0 THEN side_quote=parameters->>'quote';side_index=parameters->>'index';side_selection=jsonb_build_array(parameters->'settlementMode',parameters->'settlementIndex',parameters->'settlementProjectionPolicy');
   ELSIF (side_quote,side_index,side_selection) IS DISTINCT FROM(parameters->>'quote',parameters->>'index',jsonb_build_array(parameters->'settlementMode',parameters->'settlementIndex',parameters->'settlementProjectionPolicy')) THEN RAISE EXCEPTION 'swap_definition';END IF;
   IF extract(day FROM starts)<>1 OR ends IS DISTINCT FROM(starts+INTERVAL '1 month')::date OR(previous_end IS NOT NULL AND previous_end<>starts) THEN RAISE EXCEPTION 'scheduled_swap_periods';END IF;
   previous_end=ends;
   num=(shape->'periods'->n->>'numerator')::numeric;den=(shape->'periods'->n->>'denominator')::numeric;
   IF num IS NULL OR den IS NULL OR num<=0 OR den<=0 OR scale(num)<>0 OR scale(den)<>0 THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
   common=gcd(num,den);num=trim_scale(num/common);den=trim_scale(den/common);
   total_num=total_num*den+num*total_den;total_den=total_den*den;common=gcd(total_num,total_den);total_num=total_num/common;total_den=total_den/common;
   children=children||jsonb_build_array(jsonb_build_object('terms',parameters,'n',num,'d',den));
  END LOOP;
  IF total_num<>total_den THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
  SELECT sha256(convert_to(jsonb_build_object('version',CASE WHEN kind THEN 'FixedSide_v4' ELSE 'FloatingSide_v8' END,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,
   'periods',jsonb_agg(jsonb_build_object('terms',(x.value->'terms')-'fixingEvidence','n',x.value->'n','d',x.value->'d') ORDER BY x.ordinality))::text,'UTF8')) INTO digest_value
   FROM jsonb_array_elements(children) WITH ORDINALITY x;
  SELECT id INTO side_id FROM trading.swap_whole_term_sides s WHERE s.catalog_id=catalog AND s.identity_hash=digest_value;
  IF side_id IS NULL THEN
   side_id=gen_random_uuid();
   INSERT INTO trading.swap_whole_term_sides(catalog_id,id,identity_hash,implementation,publication_order,effective_at,reference_catalog_id,reference_revision_id,kind,definition_version)
    VALUES(catalog,side_id,digest_value,'whole_term_swap_side_v1',publication_number,from_at,ref_catalog,ref_revision,CASE WHEN kind THEN 'fixed' ELSE 'floating' END,CASE WHEN kind THEN 'FixedSide_v4' ELSE 'FloatingSide_v8' END);
   INSERT INTO trading.swap_side_periods
    SELECT catalog,side_id,(x.ordinality-1)::integer,gen_random_uuid(),p->>'kind',ref_catalog,ref_revision,p->>'quote',
     (p->>'deliveryFrom')::date,(p->>'deliveryTo')::date,(p->>'paymentAt')::timestamptz,(p->>'paymentRule')::uuid,
     (p->>'price')::numeric,(p->>'definitionRevision')::uuid,p->>'curve',(p->>'contract')::uuid,p->>'index',(p->>'firstFixingAt')::timestamptz,
     p->>'averaging',p->>'implementation',(p->>'customProjection')::uuid,(p->>'spread')::numeric,(p->>'observationSchedule')::uuid,p->>'settlementMode',p->>'settlementIndex',p->>'settlementProjectionPolicy',
     (x.value->>'n')::numeric,(x.value->>'d')::numeric,publication_number,from_at
    FROM jsonb_array_elements(children) WITH ORDINALITY x CROSS JOIN LATERAL(SELECT x.value->'terms' p) terms;
   INSERT INTO trading.swap_period_projections
    SELECT catalog,side_id,(x.ordinality-1)::integer,(r.value->>'ordinal')::integer,definition,p->>'curve',(r.value->>'contractId')::uuid,(r.value->>'weight')::numeric,
     ref_catalog,ref_revision,publication_number,from_at
    FROM jsonb_array_elements(children) WITH ORDINALITY x CROSS JOIN LATERAL(SELECT x.value->'terms' p) terms
    CROSS JOIN LATERAL jsonb_array_elements(p->'projections') r;
  END IF;
  SELECT side_fixings||coalesce(jsonb_agg(jsonb_build_object('slot',slot,'ordinal',(x.ordinality-1)::integer,'sideId',side_id,'evidence',x.value->'terms'->'fixingEvidence') ORDER BY x.ordinality),'[]'::jsonb)
   INTO side_fixings FROM jsonb_array_elements(children) WITH ORDINALITY x WHERE x.value->'terms'->>'kind'='floating';
  ids=array_prepend(side_id,ids);
 END LOOP;
 IF(compiled->0->>'relativeNumerator')::numeric IS DISTINCT FROM 1 OR(compiled->0->>'relativeDenominator')::numeric IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'scheduled_quantity';END IF;
 num=(compiled->1->>'relativeNumerator')::numeric;den=(compiled->1->>'relativeDenominator')::numeric;
 SELECT i.instrument_id INTO instrument FROM trading.swap_instruments i WHERE i.catalog_id=catalog AND i.scheduled_first_id=ids[1] AND i.scheduled_second_id=ids[2] AND i.fixed_coefficient=coefficient AND i.discounting=discount_enabled AND i.second_quantity_numerator=num AND i.second_quantity_denominator=den AND i.implementation=version AND i.quantity_factor_id IS NOT DISTINCT FROM factor_id;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments(catalog_id,instrument_id,fixed_side_id,first_floating_side_id,floating_side_id,fixed_coefficient,discounting,implementation,publication_order,effective_at,scheduled_first_id,scheduled_second_id,second_quantity_numerator,second_quantity_denominator,quantity_factor_id)
   VALUES(catalog,instrument,NULL,NULL,NULL,coefficient,discount_enabled,version,publication_number,from_at,ids[1],ids[2],num,den,factor_id);
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

CREATE FUNCTION trading.whole_term_swap_period_input(catalog uuid,selected_side uuid,period integer) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN p.kind='fixed' THEN jsonb_build_object('fixed',jsonb_build_object('price',trim_scale(p.fixed_price)::text,'currency',q.currency_code,
  'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms),'floating',NULL)
 ELSE jsonb_build_object('fixed',NULL,'floating',jsonb_build_object('curve',p.curve_code,'index',p.index_code,'settlement',jsonb_build_object('mode',p.settlement_mode,'index',CASE WHEN p.settlement_mode='override' THEN p.settlement_index_code END,'projectionPolicy',p.settlement_projection_policy),'currency',q.currency_code,
  'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms,'spread',trim_scale(p.spread)::text,
  'resetFrequency',CASE WHEN p.averaging='monthly_index_price' THEN 'monthly' ELSE 'daily' END,
  'averagingMethod',CASE p.averaging WHEN 'monthly_index_price' THEN 'none' WHEN 'calendar_day_arithmetic' THEN 'arithmetic' ELSE p.averaging END,
  'observationSchedule',CASE WHEN p.observation_schedule_id IS NULL THEN NULL ELSE jsonb_build_object('implementation',o.implementation,
   'calendar',CASE WHEN o.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.calendar_code) END,
   'quantities',CASE WHEN o.averaging='quantity_weighted' THEN(SELECT jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'quantity',jsonb_build_object('value',trim_scale(w.amount)::text,'specification',jsonb_build_object('revision',jsonb_build_object('catalogId',w.reference_catalog_id,'revisionId',w.reference_revision_id),'code',w.quantity_code))) ORDER BY w.ordinal)
    FROM trading.swap_observation_quantities w JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(w.catalog_id,w.definition_revision_id,w.contract_id) WHERE(w.catalog_id,w.schedule_id)=(o.catalog_id,o.id)) END) END,
  'customProjection',CASE WHEN c.id IS NULL THEN NULL ELSE jsonb_build_object('catalogId',c.catalog_id,'curveId',c.curve_id,'version',c.version,'outputNode',c.output_node,'position',c.position,'selection',c.selection,'implementation',c.implementation) END)) END
 FROM trading.swap_side_periods p JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code)
 JOIN LATERAL(SELECT jsonb_build_object('frequency',r.frequency,'rule',r.rule,'days',r.days,'dayOfMonth',r.day_of_month,'dayBasis',r.day_basis,'shortMonth',r.short_month,'adjustment',r.adjustment,'calendar',r.calendar_code,'implementation',r.implementation) terms
  FROM trading.swap_payment_rules r WHERE(r.catalog_id,r.id)=(p.catalog_id,p.payment_rule_id)) payment ON true
 LEFT JOIN trading.swap_observation_schedules o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_schedule_id)
 LEFT JOIN trading.custom_curve_projection_bindings c ON(c.catalog_id,c.id)=(p.catalog_id,p.custom_projection_id)
 WHERE(p.catalog_id,p.side_id,p.ordinal)=(catalog,selected_side,period)
$$;

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
   'periods',jsonb_agg(trading.whole_term_swap_period_input(catalog,p.side_id,p.ordinal)||jsonb_build_object('amount',trim_scale(q.amount)::text) ORDER BY p.ordinal)) terms,
   jsonb_build_object('id',selected.side_id,'periods',jsonb_agg(jsonb_build_object('id',p.id,'n',trim_scale(p.quantity_numerator)::text,'d',trim_scale(p.quantity_denominator)::text,'projection_contracts',(SELECT jsonb_agg(r.contract_id ORDER BY r.ordinal) FROM trading.swap_period_projections r WHERE(r.catalog_id,r.side_id,r.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)),'projection_weights',(SELECT jsonb_agg(r.weight::text ORDER BY r.ordinal) FROM trading.swap_period_projections r WHERE(r.catalog_id,r.side_id,r.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal))) ORDER BY p.ordinal)) binding
  FROM slots selected JOIN trading.swap_quantity_authoring a ON(a.catalog_id,a.trade_id,a.revision,a.slot)=(catalog,trade,revision_number,selected.slot)
  JOIN trading.swap_side_periods p ON(p.catalog_id,p.side_id)=(catalog,selected.side_id)
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
 SELECT p.ordinal INTO STRICT period FROM trading.swap_side_periods p WHERE p.catalog_id=catalog
  AND p.side_id=CASE WHEN membership=0 THEN i.scheduled_first_id ELSE i.scheduled_second_id END AND p.id=child;
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

CREATE OR REPLACE FUNCTION settlement_data.swap_payment_terms(catalog uuid,instrument uuid,quantity numeric,membership integer,period_from date,period_to date)
 RETURNS TABLE(child uuid,coefficient numeric,numerator numeric,denominator numeric) LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH i AS(SELECT * FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument)),members AS(
  SELECT CASE WHEN membership=0 THEN coalesce(i.fixed_side_id,i.first_floating_side_id) ELSE i.floating_side_id END child,
   1::numeric n,1::numeric d FROM i WHERE i.scheduled_first_id IS NULL
  UNION ALL
  SELECT p.id,p.quantity_numerator*CASE WHEN membership=1 THEN i.second_quantity_numerator ELSE 1 END,
   p.quantity_denominator*CASE WHEN membership=1 THEN i.second_quantity_denominator ELSE 1 END
  FROM i JOIN trading.swap_side_periods p ON p.catalog_id=i.catalog_id AND p.side_id=CASE WHEN membership=0 THEN i.scheduled_first_id ELSE i.scheduled_second_id END
  WHERE(p.delivery_from,p.delivery_to)=(period_from,period_to)
 )
 SELECT m.child,i.fixed_coefficient*CASE WHEN membership=0 THEN 1 ELSE -1 END,m.n*quantity,m.d FROM members m CROSS JOIN i
 LEFT JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(catalog,m.child)
 LEFT JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(catalog,m.child)
 WHERE i.scheduled_first_id IS NOT NULL OR(coalesce(f.delivery_from,s.delivery_from),coalesce(f.delivery_to,s.delivery_to))=(period_from,period_to)
$$;

-- A one-period definition has the same scalar terms as its only owned period.
-- The explicit version and new digest certify the current definition contract;
-- the rebooking guard above prevents reinterpretation of any retained row.
ALTER TABLE trading.swap_fixed_sides ADD COLUMN definition_version text NOT NULL DEFAULT 'FixedSide_v4' CHECK(definition_version='FixedSide_v4'),
 ADD COLUMN identity_hash bytea NOT NULL CHECK(octet_length(identity_hash)=32),ADD UNIQUE(catalog_id,identity_hash);
ALTER TABLE trading.swap_floating_sides ADD COLUMN definition_version text NOT NULL DEFAULT 'FloatingSide_v8' CHECK(definition_version='FloatingSide_v8'),
 ADD COLUMN identity_hash bytea NOT NULL CHECK(octet_length(identity_hash)=32),ADD UNIQUE(catalog_id,identity_hash);
ALTER TABLE trading.swap_floating_sides ADD COLUMN settlement_mode text NOT NULL,
 ADD COLUMN settlement_index_code ref_data.code NOT NULL,ADD COLUMN settlement_projection_policy text,
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,settlement_index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 ADD CHECK((settlement_mode='curve_target' AND settlement_index_code=index_code AND settlement_projection_policy IS NULL) OR
  (settlement_mode='override' AND settlement_index_code<>index_code AND settlement_projection_policy IS NOT NULL AND settlement_projection_policy='forward_curve_no_basis_v1' AND custom_projection_id IS NULL));
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_economic_key;
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='trading.swap_floating_sides'::regclass AND contype='f' AND confrelid='trading.swap_observation_schedules'::regclass LOOP
  EXECUTE format('ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT %I',item.conname);
 END LOOP;
END$$;
ALTER TABLE trading.swap_floating_sides ADD FOREIGN KEY
 (catalog_id,observation_schedule_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,settlement_index_code,quote_code,delivery_from,delivery_to,averaging)
 REFERENCES trading.swap_observation_schedules(catalog_id,id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,averaging);
CREATE FUNCTION trading.stamp_whole_term_swap_identity() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE terms jsonb;BEGIN
 IF TG_TABLE_NAME='swap_fixed_sides' THEN
  terms=jsonb_build_object('version',NEW.definition_version,'referenceCatalog',NEW.reference_catalog_id,'referenceRevision',NEW.reference_revision_id,
   'quote',NEW.quote_code,'from',NEW.delivery_from,'to',NEW.delivery_to,'payment',to_char(NEW.payment_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US'),
   'price',trim_scale(NEW.fixed_price)::text,'paymentRule',NEW.payment_rule_id);
 ELSE
  terms=jsonb_build_object('version',NEW.definition_version,'definitionRevision',NEW.definition_revision_id,'referenceCatalog',NEW.reference_catalog_id,'referenceRevision',NEW.reference_revision_id,
   'quote',NEW.quote_code,'index',NEW.index_code,'settlementMode',NEW.settlement_mode,'settlementIndex',NEW.settlement_index_code,'settlementProjectionPolicy',NEW.settlement_projection_policy,'curve',NEW.curve_code,'from',NEW.delivery_from,'to',NEW.delivery_to,
   'payment',to_char(NEW.payment_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US'),'fixing',to_char(NEW.first_fixing_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US'),
   'averaging',NEW.averaging,'spread',trim_scale(NEW.spread)::text,'paymentRule',NEW.payment_rule_id,'customProjection',NEW.custom_projection_id,'observationSchedule',NEW.observation_schedule_id);
 END IF;
 NEW.identity_hash=sha256(convert_to(terms::text,'UTF8'));RETURN NEW;
END$$;
CREATE TRIGGER whole_term_identity BEFORE INSERT ON trading.swap_fixed_sides FOR EACH ROW EXECUTE FUNCTION trading.stamp_whole_term_swap_identity();
CREATE TRIGGER whole_term_identity BEFORE INSERT ON trading.swap_floating_sides FOR EACH ROW EXECUTE FUNCTION trading.stamp_whole_term_swap_identity();
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='trading.swap_instruments'::regclass AND contype='c' AND pg_get_constraintdef(oid) LIKE '%fixed_side_id IS NULL%first_floating_side_id IS NULL%' LOOP
  EXECUTE format('ALTER TABLE trading.swap_instruments DROP CONSTRAINT %I',item.conname);
 END LOOP;
END$$;
ALTER TABLE trading.swap_instruments ADD CHECK(scheduled_first_id IS NOT NULL OR((fixed_side_id IS NULL)<>(first_floating_side_id IS NULL)));


CREATE OR REPLACE FUNCTION security.retain_floating_swap_side(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz,input_pins uuid[])
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb;side uuid;BEGIN
 p=security.prepare_floating_swap_period(catalog,definition,ref_catalog,ref_revision,commodity,body,valuation,publication_number,from_at,input_pins);
 SELECT s.id INTO side FROM trading.swap_floating_sides s WHERE s.catalog_id=catalog AND s.definition_revision_id=definition AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision
  AND(s.curve_code,s.index_code,s.quote_code,s.delivery_from,s.delivery_to,s.first_fixing_at,s.payment_at,s.averaging,s.payment_rule_id,s.spread,s.implementation,s.settlement_mode,s.settlement_index_code)=
   (p->>'curve',p->>'index',p->>'quote',(p->>'deliveryFrom')::date,(p->>'deliveryTo')::date,(p->>'firstFixingAt')::timestamptz,(p->>'paymentAt')::timestamptz,p->>'averaging',(p->>'paymentRule')::uuid,(p->>'spread')::numeric,p->>'implementation',p->>'settlementMode',p->>'settlementIndex')
  AND(s.custom_projection_id,s.observation_schedule_id,s.settlement_projection_policy) IS NOT DISTINCT FROM((p->>'customProjection')::uuid,(p->>'observationSchedule')::uuid,p->>'settlementProjectionPolicy');
 IF side IS NULL THEN
  side=gen_random_uuid();
  INSERT INTO trading.swap_floating_sides(catalog_id,id,definition_revision_id,curve_code,contract_id,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,publication_order,effective_at,payment_rule_id,custom_projection_id,spread,observation_schedule_id,settlement_mode,settlement_index_code,settlement_projection_policy)
   VALUES(catalog,side,definition,p->>'curve',(p->>'contract')::uuid,ref_catalog,ref_revision,p->>'index',p->>'quote',(p->>'deliveryFrom')::date,(p->>'deliveryTo')::date,(p->>'firstFixingAt')::timestamptz,(p->>'paymentAt')::timestamptz,p->>'averaging',p->>'implementation',publication_number,from_at,(p->>'paymentRule')::uuid,(p->>'customProjection')::uuid,(p->>'spread')::numeric,(p->>'observationSchedule')::uuid,p->>'settlementMode',p->>'settlementIndex',p->>'settlementProjectionPolicy');
  INSERT INTO trading.swap_reset_projections SELECT catalog,side,x.ordinal,definition,p->>'curve',x."contractId",x.weight,publication_number,from_at
   FROM jsonb_to_recordset(p->'projections') x(ordinal integer,"contractId" uuid,weight numeric);
 END IF;
 IF EXISTS(SELECT 1 FROM(SELECT r.ordinal,r.definition_revision_id,r.curve_code,r.contract_id,r.weight FROM trading.swap_reset_projections r WHERE(r.catalog_id,r.side_id)=(catalog,side)) retained FULL JOIN
  (SELECT x.ordinal,definition AS definition_revision_id,p->>'curve' AS curve_code,x."contractId" AS contract_id,x.weight FROM jsonb_to_recordset(p->'projections') x(ordinal integer,"contractId" uuid,weight numeric)) expected USING(ordinal)
  WHERE retained.ordinal IS NULL OR expected.ordinal IS NULL OR(retained.definition_revision_id,retained.curve_code,retained.contract_id,retained.weight) IS DISTINCT FROM(expected.definition_revision_id,expected.curve_code,expected.contract_id,expected.weight)) THEN RAISE EXCEPTION 'swap_projection';END IF;
 RETURN side;
END$$;
-- Observation geometry belongs to the projection curve. The admitted selection
-- separately qualifies every exact fixing, and the retained schedule pins it.
DO $$DECLARE signature regprocedure;definition text;before text;BEGIN
 signature='security.swap_observation_rows(uuid,uuid,uuid,uuid,text,text,date,date,text,jsonb)'::regprocedure;
 definition=pg_get_functiondef(signature);before='AND t.kind=''index'' AND t.index_a_code=index_name';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected observation quote qualification';END IF;
 EXECUTE replace(definition,before,'AND t.kind=''index'' AND t.index_b_code IS NULL');
 signature='security.retained_swap_fixing_evidence(uuid,uuid,timestamptz,uuid[])'::regprocedure;
 definition=pg_get_functiondef(signature);before='side.curve_code,side.index_code,';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected retained fixing qualification';END IF;
 EXECUTE replace(definition,before,'side.curve_code,side.settlement_index_code,');
END$$;

-- Whole-term parameters replace the previous wrapper-to-child-side relationship.
DROP TABLE trading.swap_scheduled_periods;
-- Only the explicit current command contracts remain executable. Schema
-- history retains the old definitions; the installed application does not.
DO $$DECLARE name text;signature regprocedure;BEGIN
 FOREACH name IN ARRAY ARRAY['retry_legacy_swap_with_parties','save_projected_swap_with_parties','save_indexed_swap_with_parties',
  'save_observation_swap_with_parties','save_swap_with_parties','save_annotated_swap','save_monthly_swap',
  'save_projected_swap_core','save_scheduled_swap'] LOOP
  FOR signature IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
   WHERE n.nspname='security' AND p.proname=name AND(name<>'save_scheduled_swap' OR p.pronargs<>7) LOOP
   EXECUTE format('DROP FUNCTION %s',signature);
  END LOOP;
 END LOOP;
END$$;
ALTER TABLE trading.swap_fixed_sides ALTER COLUMN payment_rule_id SET NOT NULL;
ALTER TABLE trading.swap_floating_sides ALTER COLUMN payment_rule_id SET NOT NULL,
 ADD CHECK(implementation='index_observation_fixings_v1');
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE(n.nspname='security' AND p.proname IN('prepare_fixed_swap_period','prepare_floating_swap_period'))
   OR(n.nspname='trading' AND p.proname IN('whole_term_swap_period_input','stamp_whole_term_swap_identity')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',item.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',item.signature);
  IF item.proname='whole_term_swap_period_input' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime,openetrm_risk_worker',item.signature);END IF;
 END LOOP;
END$$;

-- A scheduled binding retains its definition/reference pins through its owned
-- floating parameters under the same historical and current-authority read.
DO $$DECLARE signature regprocedure='automation.close_risk_scope(uuid,uuid,timestamptz,uuid[])'::regprocedure;definition text;before text;after text;BEGIN
 definition=pg_get_functiondef(signature);
 before='f.definition_revision_id,f.reference_catalog_id,f.reference_revision_id,i.implementation FROM swaps';
 after='coalesce(f.definition_revision_id,p.definition_revision_id),coalesce(f.reference_catalog_id,p.reference_catalog_id),coalesce(f.reference_revision_id,p.reference_revision_id),i.implementation FROM swaps';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected close swap scope';END IF;
 definition=replace(definition,before,after);
 before='LEFT JOIN trading.swap_floating_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.floating_side_id) WHERE r.book_id=ANY(books)';
 after='LEFT JOIN trading.swap_floating_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.floating_side_id) LEFT JOIN trading.swap_side_periods p ON(p.catalog_id,p.side_id,p.ordinal)=(i.catalog_id,i.scheduled_second_id,0) WHERE r.book_id=ANY(books)';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected close swap period scope';END IF;
 EXECUTE replace(definition,before,after);
END$$;

-- Occurrence continuity belongs to the period economics, independently of the
-- whole-term side that owns it. A future change may create a new parent side.
CREATE FUNCTION settlement_data.swap_owned_period_identity(catalog uuid,period uuid) RETURNS bytea
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT sha256(convert_to(((to_jsonb(p)-ARRAY['side_id','ordinal','id','quantity_numerator','quantity_denominator','publication_order','effective_at','payment_at','first_fixing_at'])||
  jsonb_build_object('paymentAt',to_char(p.payment_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US'),
   'firstFixingAt',to_char(p.first_fixing_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US'),
   'projections',coalesce((SELECT jsonb_agg(jsonb_build_object('definition',r.definition_revision_id,'curve',r.curve_code,'contract',r.contract_id,'weight',trim_scale(r.weight)::text) ORDER BY r.ordinal)
    FROM trading.swap_period_projections r WHERE(r.catalog_id,r.side_id,r.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)),'[]'::jsonb)))::text,'UTF8'))
 FROM trading.swap_side_periods p WHERE(p.catalog_id,p.id)=(catalog,period)
$$;
CREATE OR REPLACE FUNCTION settlement_data.guard_swap_amendment() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE o settlement_data.swap_occurrences;origin trading.swap_trade_revisions;old record;replacement record;BEGIN
 IF NEW.previous_revision IS NULL THEN RETURN NEW;END IF;
 FOR o IN SELECT * FROM settlement_data.swap_occurrences WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id) LOOP
  SELECT * INTO STRICT origin FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=(o.catalog_id,o.trade_id,o.origin_revision);
  IF(origin.book_id,origin.legal_entity_id,origin.legal_entity_revision,origin.counterparty_id,origin.counterparty_revision)
   IS DISTINCT FROM(NEW.book_id,NEW.legal_entity_id,NEW.legal_entity_revision,NEW.counterparty_id,NEW.counterparty_revision)
  THEN RAISE EXCEPTION 'settlement_amendment_unsupported';END IF;
  SELECT * INTO STRICT old FROM settlement_data.swap_payment_terms(o.catalog_id,origin.instrument_id,origin.quantity,o.membership_ordinal,o.delivery_from,o.delivery_to);
  SELECT * INTO replacement FROM settlement_data.swap_payment_terms(o.catalog_id,NEW.instrument_id,NEW.quantity,o.membership_ordinal,o.delivery_from,o.delivery_to);
  IF replacement.child IS NULL OR (replacement.child<>old.child AND(settlement_data.swap_owned_period_identity(o.catalog_id,old.child) IS NULL
   OR settlement_data.swap_owned_period_identity(o.catalog_id,replacement.child) IS NULL
   OR settlement_data.swap_owned_period_identity(o.catalog_id,replacement.child) IS DISTINCT FROM settlement_data.swap_owned_period_identity(o.catalog_id,old.child))) OR replacement.coefficient<>old.coefficient
   OR replacement.numerator*old.denominator<>old.numerator*replacement.denominator
  THEN RAISE EXCEPTION 'settlement_amendment_unsupported';END IF;
 END LOOP;
 RETURN NEW;
END$$;
ALTER FUNCTION settlement_data.swap_owned_period_identity(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.swap_owned_period_identity(uuid,uuid) FROM PUBLIC;
COMMIT;
