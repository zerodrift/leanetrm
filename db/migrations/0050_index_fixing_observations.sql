-- Stop write admission and drain requests before applying. Existing floating
-- definitions retain their original projection-only implementation and boundary.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);

-- Dated publication instants are compiled, source-qualified index data. Neither
-- delivery dates, curve expiry nor the viewer's time zone supplies a convention.
CREATE TABLE market_data.index_fixing_observations (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,index_code ref_data.code NOT NULL,
 observation_from date NOT NULL,observation_to date NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 quote_code ref_data.code NOT NULL,series_id uuid NOT NULL,source_code ref_data.code NOT NULL,
 fixing_at ref_data.instant NOT NULL,implementation text NOT NULL CHECK(implementation='published_index_observations_v1'),
 PRIMARY KEY(catalog_id,revision_id,index_code,observation_from,observation_to),
 CHECK(observation_from<observation_to AND observation_to-observation_from<=366),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code)
  REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code)
  REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code)
  REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id)
);
CREATE FUNCTION market_data.validate_index_fixing_observation() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(
  SELECT 1 FROM market_data.series s
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
  JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
  JOIN ref_data.price_index_definitions i ON(i.catalog_id,i.revision_id,i.code)=(q.catalog_id,q.revision_id,NEW.index_code)
  WHERE(s.catalog_id,s.revision_id,s.id)=(NEW.catalog_id,NEW.revision_id,NEW.series_id)
   AND(s.reference_catalog_id,s.reference_revision_id,s.quote_code)=(NEW.reference_catalog_id,NEW.reference_revision_id,NEW.quote_code)
   AND(k.delivery_from,k.delivery_to)=(NEW.observation_from,NEW.observation_to)
   AND k.kind='forward' AND q.kind='outright'
   AND(i.currency_code,i.commodity_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,q.commodity_code,u.unit_code,q.multiplier)
 ) THEN RAISE EXCEPTION 'index_fixing_definition' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.index_fixing_observations
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition();
CREATE TRIGGER validate_index_fixing BEFORE INSERT ON market_data.index_fixing_observations
 FOR EACH ROW EXECUTE FUNCTION market_data.validate_index_fixing_observation();
ALTER TABLE market_data.index_fixing_observations ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.index_fixing_observations FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.index_fixing_observations FOR SELECT TO openetrm_runtime
 USING(true);
-- Each restrictive visibility helper also checks current authority. Additional
-- permissive policies cannot bypass either exact-version visibility boundary.
CREATE POLICY temporal_read ON market_data.index_fixing_observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_fact_visible(catalog_id,revision_id,NULL));
CREATE POLICY reference_read ON market_data.index_fixing_observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY scoped_insert ON market_data.index_fixing_observations FOR INSERT TO openetrm_runtime
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions') AND security.reference_access(reference_catalog_id,'ReadMarketData'));
CREATE POLICY current_insert ON market_data.index_fixing_observations AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context());
GRANT SELECT,INSERT ON market_data.index_fixing_observations TO openetrm_runtime;
GRANT SELECT ON market_data.index_fixing_observations TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.validate_index_fixing_observation() FROM PUBLIC;

ALTER TABLE market_data.observations DROP CONSTRAINT observations_role_check;
ALTER TABLE market_data.observations ADD CONSTRAINT observations_role_check
 CHECK(role IN('trader_mark','daily_settlement','final_contract_settlement','parameter','index_fixing'));
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_sides_implementation_check;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_floating_sides_implementation_check
 CHECK(implementation IN('prefixing_index_projection_v1','index_observation_fixings_v1'));

-- This helper is private to the authorized writer. It validates the same exact
-- observation schedule and supplied actual pins as the managed pricing path.
CREATE FUNCTION security.swap_fixing_evidence(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,from_date date,to_date date,averaging text,valuation timestamptz,payment timestamptz,input_pins uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;first_fixing timestamptz;required_count integer;seen integer:=0;actual uuid;actual_count integer;used uuid[]:='{}';
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
  OR NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(ref_catalog,ref_revision)
 THEN RAISE EXCEPTION 'No current fixing access' USING ERRCODE='42501';END IF;
 IF input_pins IS NULL OR cardinality(input_pins)>366 OR cardinality(input_pins)<>(SELECT count(DISTINCT x) FROM unnest(input_pins) x)
 THEN RAISE EXCEPTION 'index_fixing_inputs' USING ERRCODE='P0001';END IF;
 required_count=CASE WHEN averaging='monthly_index_price' THEN 1 ELSE to_date-from_date END;
 FOR r IN
  SELECT k.expiry_at,f.* FROM market_data.curve_members m
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
  LEFT JOIN market_data.index_fixing_observations f ON(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to)=
   (k.catalog_id,k.revision_id,index_name,k.delivery_from,k.delivery_to)
  WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,curve) AND k.kind='forward'
   AND(averaging='monthly_index_price' AND(k.delivery_from,k.delivery_to)=(from_date,to_date)
    OR averaging='calendar_day_arithmetic' AND k.delivery_to=k.delivery_from+1 AND from_date<=k.delivery_from AND k.delivery_from<to_date)
  ORDER BY k.delivery_from,k.id
 LOOP
  seen=seen+1;
  IF r.index_code IS NULL OR(r.reference_catalog_id,r.reference_revision_id) IS DISTINCT FROM(ref_catalog,ref_revision)
   OR r.quote_code IS DISTINCT FROM(SELECT cv.quote_code FROM market_data.curves cv WHERE(cv.catalog_id,cv.revision_id,cv.code)=(catalog,definition,curve))
  THEN RAISE EXCEPTION 'index_fixing_convention_missing' USING ERRCODE='P0001';END IF;
  IF r.fixing_at>=payment THEN RAISE EXCEPTION 'index_fixing_definition' USING ERRCODE='P0001';END IF;
  first_fixing=least(first_fixing,r.fixing_at);
  IF valuation<r.fixing_at THEN
   IF valuation>=r.expiry_at THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001';END IF;
  ELSE
   SELECT count(*),min(o.id::text)::uuid INTO actual_count,actual FROM market_data.observations o
   WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.reference_catalog_id,o.reference_revision_id,o.source_code)=
    (catalog,definition,r.series_id,ref_catalog,ref_revision,r.source_code)
    AND o.id=ANY(input_pins) AND o.role='index_fixing' AND o.publication_state='final'
    AND r.fixing_at<=o.observed_at AND o.observed_at<=valuation
    AND security.market_fact_visible(o.catalog_id,o.definition_revision_id,o.id);
   IF actual_count<>1 THEN RAISE EXCEPTION 'index_fixing_missing' USING ERRCODE='P0001';END IF;
   used=array_append(used,actual);
  END IF;
 END LOOP;
 IF seen<>required_count OR first_fixing IS NULL THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001';END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'Fixing access expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('firstFixing',first_fixing AT TIME ZONE 'UTC','inputs',to_jsonb(used));
END $$;

CREATE FUNCTION security.save_indexed_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb,input_pins uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE fixing timestamptz;fixing_evidence jsonb;fixed_rule uuid;float_rule uuid;c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;request_value jsonb;permission text;publication_number integer;
 cv market_data.curves;q ref_data.quote_specifications;k market_data.contracts;target ref_data.settlement_targets;fixed_side uuid;floating_side uuid;instrument uuid;binding uuid;from_at timestamptz;
BEGIN
 permission=CASE WHEN trade IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501'; END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001'; END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF executed IS NOT NULL OR trader IS NOT NULL OR trader_revision IS NOT NULL OR booking->>'tradeDate' IS NOT NULL THEN RAISE EXCEPTION 'trade_booking_read_only' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501'; END IF;
 executed=valuation;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR implementation IS DISTINCT FROM 'independent_side_financial_swap_v2' OR
  discounting IS NULL OR coefficient IS NULL OR coefficient NOT IN(-1,1) OR quantity IS NULL OR quantity<=0 OR abs(quantity)>=1e24 OR scale(quantity)>18 OR
  valuation IS NULL OR executed IS NULL OR effective IS NULL OR NOT isfinite(valuation) OR NOT isfinite(executed) OR NOT isfinite(effective) OR executed>effective OR effective>valuation OR
  fixed_from IS NULL OR fixed_to IS NULL OR fixed_to<=fixed_from OR fixed_to-fixed_from>366 OR
  fixed_payment IS NULL OR float_payment IS NULL OR NOT isfinite(fixed_payment) OR NOT isfinite(float_payment) OR
  valuation>=fixed_payment OR valuation>=float_payment OR(fixed_schedule IS NULL AND(fixed_payment AT TIME ZONE 'UTC')::date<fixed_to) OR(float_schedule IS NULL AND(float_payment AT TIME ZONE 'UTC')::date<float_to) OR
  averaging IS NULL OR averaging NOT IN('monthly_index_price','calendar_day_arithmetic') OR price IS NULL OR abs(price)>=1e24 OR scale(price)>18
 THEN RAISE EXCEPTION 'swap_input' USING ERRCODE='P0001'; END IF;
 request_value=jsonb_build_object('fixingConvention','published_index_observations_v1','trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,
  'curve',curve,'index',index_name,'fixedCurrency',fixed_currency,'floatingCurrency',float_currency,'fixedFrom',fixed_from,'fixedTo',fixed_to,'fixedPayment',fixed_payment AT TIME ZONE 'UTC',
  'floatingFrom',float_from,'floatingTo',float_to,'firstFixing',fixing AT TIME ZONE 'UTC','floatingPayment',float_payment AT TIME ZONE 'UTC','averaging',averaging,
  'price',price,'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation AT TIME ZONE 'UTC','executed',NULL,'effective',effective AT TIME ZONE 'UTC','reason',reason,'implementation',implementation);
 IF fixed_schedule IS NOT NULL OR float_schedule IS NOT NULL OR commodity IS NOT NULL THEN request_value=request_value||jsonb_build_object('fixedSchedule',fixed_schedule,'floatingSchedule',float_schedule,'commodity',commodity);END IF;
 request_value=request_value||jsonb_build_object('booking',booking,'bookingFactsPolicy','server_booking_utc_v1');
 IF trader IS NOT NULL OR trader_revision IS NOT NULL OR memo IS NOT NULL THEN request_value=request_value||jsonb_build_object('trader',trader,'traderRevision',trader_revision,'memo',memo);END IF;
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR (prior.request-'firstFixing'-'fixingInputs')<>(request_value-'firstFixing') THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
  RETURN security.swap_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation)
 THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501'; END IF;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade' USING ERRCODE='P0001';END IF;
  executed=head.execution_at;trader=head.trader_id;trader_revision=head.trader_revision;
  booking=booking||jsonb_build_object('tradeDate',coalesce(head.trade_date,(SELECT f.trade_date FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(catalog,trade))));
 ELSE
  booking=booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date);
 END IF;
 PERFORM security.require_trade_annotation(catalog,trader,trader_revision,memo,executed);
 PERFORM security.require_trade_parties(catalog,booking,executed);
 IF fixed_schedule IS NULL OR float_schedule IS NULL OR commodity IS NULL THEN RAISE EXCEPTION 'payment_rule' USING ERRCODE='P0001';END IF;
 IF fixed_payment IS DISTINCT FROM(security.swap_payment_date(ref_catalog,ref_revision,fixed_from,fixed_to,fixed_schedule)::timestamp AT TIME ZONE 'UTC') OR float_payment IS DISTINCT FROM(security.swap_payment_date(ref_catalog,ref_revision,float_from,float_to,float_schedule)::timestamp AT TIME ZONE 'UTC') THEN RAISE EXCEPTION 'payment_date_mismatch' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=executed AND p.effective_at<=executed)
 THEN RAISE EXCEPTION 'swap_book' USING ERRCODE='P0001'; END IF;
 SELECT * INTO cv FROM market_data.curves WHERE catalog_id=catalog AND revision_id=definition AND code=curve AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.quote_code);
 SELECT * INTO target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.settlement_target_code);
 IF cv.commodity_code IS DISTINCT FROM commodity OR q.kind IS DISTINCT FROM 'outright' OR target.kind IS DISTINCT FROM 'index' OR target.index_a_code IS DISTINCT FROM index_name OR
  q.currency_code IS DISTINCT FROM fixed_currency OR q.currency_code IS DISTINCT FROM float_currency OR
  NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,index_name) AND(i.currency_code,i.commodity_code,i.location_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,cv.location_code,u.unit_code,q.multiplier))
 THEN RAISE EXCEPTION 'swap_definition' USING ERRCODE='P0001'; END IF;
 -- Derived input versions belong to the committed outcome, not the submitted
 -- command identity. A retry above returns the original outcome even after a
 -- later actual correction; a fresh command resolves the newly selected pins.
 fixing_evidence=security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,curve,index_name,float_from,float_to,averaging,valuation,float_payment,input_pins);
 fixing=(fixing_evidence->>'firstFixing')::timestamp AT TIME ZONE 'UTC';
 request_value=request_value||jsonb_build_object('firstFixing',fixing AT TIME ZONE 'UTC','fixingInputs',fixing_evidence->'inputs');
 IF float_from IS NULL OR float_to IS NULL OR extract(day FROM float_from)<>1 OR float_to IS DISTINCT FROM(float_from+INTERVAL '1 month')::date THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 IF averaging='monthly_index_price' THEN
  IF(SELECT count(*) FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_from=float_from AND k1.delivery_to=float_to)<>1 THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
  SELECT k1.* INTO k FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_from=float_from AND k1.delivery_to=float_to;
 ELSE
  IF(SELECT count(*) FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
    WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)<>float_to-float_from OR
    (SELECT count(DISTINCT k1.delivery_from) FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
    WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)<>float_to-float_from
  THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 END IF;
 SELECT effective_from::timestamp AT TIME ZONE 'UTC' INTO from_at FROM market_data.revision_periods WHERE catalog_id=catalog AND revision_id=definition AND effective_from<=(executed AT TIME ZONE 'UTC')::date AND(executed AT TIME ZONE 'UTC')::date<effective_to;
 IF from_at IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE catalog_id=catalog AND id=definition AND state='sealed') THEN RAISE EXCEPTION 'swap_definition' USING ERRCODE='P0001'; END IF;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade' USING ERRCODE='P0001'; END IF;
  IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'swap_revision_conflict' USING ERRCODE='P0001'; END IF;
  IF head.revision>=256 OR effective IS DISTINCT FROM valuation OR effective<head.effective_at OR reason IS NULL OR length(btrim(reason)) NOT BETWEEN 1 AND 500 OR reason ~ '^[[:space:]]*$'
   OR EXISTS(SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
    JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id) WHERE i.catalog_id=catalog AND i.instrument_id=head.instrument_id
    AND (valuation>=s.first_fixing_at OR valuation>=s.payment_at OR valuation>=f.payment_at OR f.reference_catalog_id<>ref_catalog OR f.reference_revision_id<>ref_revision OR f.quote_code<>q.code))
  THEN RAISE EXCEPTION 'swap_amendment' USING ERRCODE='P0001'; END IF;
 ELSE
  IF effective IS DISTINCT FROM executed OR expected IS NOT NULL OR (SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'swap_limit' USING ERRCODE='P0001'; END IF;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication_number FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication_number,pg_current_xact_id());
 fixed_rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,fixed_schedule,publication_number,from_at);
 float_rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,float_schedule,publication_number,from_at);
 SELECT id INTO fixed_side FROM trading.swap_fixed_sides WHERE catalog_id=catalog AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision AND quote_code=q.code AND delivery_from=fixed_from AND delivery_to=fixed_to AND payment_at=fixed_payment AND fixed_price=price AND payment_rule_id=fixed_rule;
 IF fixed_side IS NULL THEN
  fixed_side=gen_random_uuid();INSERT INTO trading.swap_fixed_sides VALUES(catalog,fixed_side,ref_catalog,ref_revision,q.code,fixed_from,fixed_to,fixed_payment,price,'fixed_total_payment_v1',publication_number,from_at,fixed_rule);
 END IF;
 SELECT id INTO floating_side FROM trading.swap_floating_sides WHERE catalog_id=catalog AND definition_revision_id=definition AND curve_code=curve AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision AND quote_code=q.code AND delivery_from=float_from AND delivery_to=float_to AND index_code=index_name AND first_fixing_at=fixing AND swap_floating_sides.implementation='index_observation_fixings_v1' AND payment_at=float_payment AND swap_floating_sides.averaging=save_indexed_swap_with_parties.averaging AND payment_rule_id=float_rule;
 IF floating_side IS NOT NULL AND EXISTS(
  SELECT 1 FROM (
   SELECT p.ordinal,p.definition_revision_id,p.curve_code,p.contract_id,p.weight FROM trading.swap_reset_projections p
   WHERE(p.catalog_id,p.side_id)=(catalog,floating_side)
  ) retained FULL JOIN (
   SELECT (row_number() OVER(ORDER BY k1.delivery_from,k1.id)-1)::integer AS ordinal,definition AS definition_revision_id,
    curve AS curve_code,k1.id AS contract_id,1::numeric AS weight
   FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND
    (averaging='monthly_index_price' AND k1.id=k.id OR averaging='calendar_day_arithmetic' AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)
  ) expected USING(ordinal)
  WHERE retained.ordinal IS NULL OR expected.ordinal IS NULL OR
   (retained.definition_revision_id,retained.curve_code,retained.contract_id,retained.weight)
    IS DISTINCT FROM(expected.definition_revision_id,expected.curve_code,expected.contract_id,expected.weight)
 ) THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 IF floating_side IS NULL THEN
  floating_side=gen_random_uuid();INSERT INTO trading.swap_floating_sides VALUES(catalog,floating_side,definition,curve,k.id,ref_catalog,ref_revision,index_name,q.code,float_from,float_to,fixing,float_payment,averaging,'index_observation_fixings_v1',publication_number,from_at,float_rule);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,floating_side,(row_number() OVER(ORDER BY k1.delivery_from,k1.id)-1)::integer,definition,curve,k1.id,1,publication_number,from_at
   FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND
    (averaging='monthly_index_price' AND k1.id=k.id OR averaging='calendar_day_arithmetic' AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to);
 END IF;
 SELECT instrument_id INTO instrument FROM trading.swap_instruments WHERE catalog_id=catalog AND fixed_side_id=fixed_side AND floating_side_id=floating_side AND fixed_coefficient=coefficient AND swap_instruments.discounting=save_indexed_swap_with_parties.discounting;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments VALUES(catalog,instrument,fixed_side,floating_side,coefficient,discounting,implementation,publication_number,from_at);
 END IF;
 IF trade IS NULL THEN
  trade=gen_random_uuid();binding=gen_random_uuid();
  booking=booking||jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request_value,publication_number,executed);
 ELSE
  IF(book,instrument,quantity,executed,trader,trader_revision,memo,booking) IS NOT DISTINCT FROM(head.book_id,head.instrument_id,head.quantity,head.execution_at,head.trader_id,head.trader_revision,head.memo,security.trade_party_payload(head.trade_date,head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision)) THEN RAISE EXCEPTION 'swap_no_change' USING ERRCODE='P0001'; END IF;
  binding=CASE WHEN instrument=head.instrument_id THEN head.binding_id ELSE gen_random_uuid() END;
 END IF;
 INSERT INTO trading.swap_trade_revisions VALUES(catalog,trade,coalesce(head.revision,0)+1,head.revision,gen_random_uuid(),'monthly_financial_swap_booking_v2',CASE WHEN head.revision IS NULL THEN 'booked' ELSE 'amended' END,
  book,binding,instrument,quantity,executed,effective,operation,c.actor_id,CASE WHEN head.revision IS NULL THEN 'Initial booking' ELSE btrim(reason) END,request_value,publication_number,trader,trader_revision,memo,(booking->>'tradeDate')::date,(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,(booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501'; END IF;
 RETURN security.swap_operation_status(catalog,operation);
END $$;
CREATE FUNCTION security.retry_legacy_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 fixing timestamptz,float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,CASE WHEN trade IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END)
 THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation)
 THEN RAISE EXCEPTION 'swap_fixing_read_only' USING ERRCODE='P0001';END IF;
 RETURN security.save_swap_with_parties(catalog,operation,trade,expected,book,definition,ref_catalog,ref_revision,curve,index_name,fixed_currency,float_currency,fixed_from,fixed_to,fixed_payment,float_from,float_to,fixing,float_payment,averaging,price,quantity,coefficient,discounting,valuation,executed,effective,reason,implementation,fixed_schedule,float_schedule,commodity,trader,trader_revision,memo,booking);
END $$;

-- Retain the implementation for exact legacy retries, but no runtime caller may
-- use it to author another arbitrary fixing boundary.
REVOKE ALL ON FUNCTION security.save_swap_with_parties(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,text,text,text,date,date,timestamptz,date,date,timestamptz,timestamptz,text,numeric,numeric,numeric,boolean,timestamptz,timestamptz,timestamptz,text,text,jsonb,jsonb,text,uuid,integer,text,jsonb) FROM openetrm_runtime;
DO $$ DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('swap_fixing_evidence','save_indexed_swap_with_parties','retry_legacy_swap_with_parties') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname<>'swap_fixing_evidence' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END $$;
COMMIT;
