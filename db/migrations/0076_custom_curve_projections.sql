-- Explicit published custom outputs for the existing monthly indexed floating side.
-- Existing definitions retain their IDs and indexed/fixing implementation.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.custom_curve_projection_bindings (
 catalog_id uuid NOT NULL,id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,
 output_node text NOT NULL,position integer NOT NULL CHECK(position>=0),
 selection text NOT NULL CHECK(selection='single_output_position_v1'),
 implementation text NOT NULL CHECK(implementation='published_custom_curve_projection_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,curve_id,version,output_node,position,selection,implementation),
 FOREIGN KEY(catalog_id,curve_id,version,output_node) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.custom_curve_projection_bindings ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.custom_curve_projection_bindings FORCE ROW LEVEL SECURITY;
GRANT SELECT ON trading.custom_curve_projection_bindings TO openetrm_runtime;
GRANT SELECT,INSERT ON trading.custom_curve_projection_bindings TO openetrm_security_owner;
CREATE POLICY scoped_read ON trading.custom_curve_projection_bindings FOR SELECT TO openetrm_runtime
 USING(security.trading_visible(catalog_id,publication_order,effective_at) AND security.custom_curve_visible(catalog_id,curve_id,version));
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.custom_curve_projection_bindings
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION security.guard_custom_projection() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node)=(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.output_node) AND v.state='published')
 THEN RAISE EXCEPTION 'custom_projection' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER published_projection BEFORE INSERT ON trading.custom_curve_projection_bindings
 FOR EACH ROW EXECUTE FUNCTION security.guard_custom_projection();
ALTER TABLE trading.swap_floating_sides ADD COLUMN custom_projection_id uuid,
 ADD FOREIGN KEY(catalog_id,custom_projection_id) REFERENCES trading.custom_curve_projection_bindings(catalog_id,id),
 ADD CHECK(custom_projection_id IS NULL OR(averaging='monthly_index_price' AND implementation='index_observation_fixings_v1'));
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_economic_key;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_floating_economic_key UNIQUE NULLS NOT DISTINCT
 (catalog_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,
  delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,payment_rule_id,custom_projection_id);

CREATE FUNCTION security.validate_custom_projection(catalog uuid,projection jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE curve uuid;requested_version integer;node text;ordinal integer;
BEGIN
 IF projection IS NULL THEN RETURN NULL;END IF;
 IF jsonb_typeof(projection) IS DISTINCT FROM 'object' OR NOT projection ?& ARRAY['catalogId','curveId','version','outputNode','position','selection','implementation']
  OR (SELECT count(*) FROM jsonb_object_keys(projection))<>7 OR (projection->>'catalogId')::uuid IS DISTINCT FROM catalog
  OR projection->>'selection' IS DISTINCT FROM 'single_output_position_v1' OR projection->>'implementation' IS DISTINCT FROM 'published_custom_curve_projection_v1'
 THEN RAISE EXCEPTION 'custom_projection' USING ERRCODE='P0001';END IF;
 curve=(projection->>'curveId')::uuid;requested_version=(projection->>'version')::integer;node=projection->>'outputNode';ordinal=(projection->>'position')::integer;
 IF ordinal IS NULL OR ordinal<0 OR NOT security.custom_curve_visible(catalog,curve,requested_version)
  OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node)=(catalog,curve,requested_version,node) AND v.state='published')
  OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,curve,requested_version))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,requested_version) AND n.kind='sql_transform')
 THEN RAISE EXCEPTION 'custom_projection' USING ERRCODE='P0001';END IF;
 RETURN jsonb_build_object('catalogId',catalog,'curveId',curve,'version',requested_version,'outputNode',node,'position',ordinal,
  'selection','single_output_position_v1','implementation','published_custom_curve_projection_v1');
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'custom_projection' USING ERRCODE='P0001';
END$$;
CREATE FUNCTION security.retain_custom_projection(catalog uuid,projection jsonb,publication integer,effective timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE retained uuid;
BEGIN
 projection=security.validate_custom_projection(catalog,projection);
 IF projection IS NULL THEN RETURN NULL;END IF;
 SELECT p.id INTO retained FROM trading.custom_curve_projection_bindings p WHERE p.catalog_id=catalog AND
  (p.curve_id,p.version,p.output_node,p.position,p.selection,p.implementation)=((projection->>'curveId')::uuid,(projection->>'version')::integer,projection->>'outputNode',(projection->>'position')::integer,projection->>'selection',projection->>'implementation');
 IF retained IS NULL THEN
  retained=gen_random_uuid();INSERT INTO trading.custom_curve_projection_bindings VALUES(catalog,retained,(projection->>'curveId')::uuid,
   (projection->>'version')::integer,projection->>'outputNode',(projection->>'position')::integer,projection->>'selection',projection->>'implementation',publication,effective);
 END IF;
 RETURN retained;
END$$;
CREATE FUNCTION security.save_projected_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb,input_pins uuid[],custom_projection jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE custom_binding uuid;fixing timestamptz;fixing_evidence jsonb;fixed_rule uuid;float_rule uuid;c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;request_value jsonb;permission text;publication_number integer;
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
 custom_projection=security.validate_custom_projection(catalog,custom_projection);
 IF custom_projection IS NOT NULL AND(averaging<>'monthly_index_price' OR EXISTS(
  SELECT 1 FROM market_data.custom_curve_node_sources s
  WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,(custom_projection->>'curveId')::uuid,(custom_projection->>'version')::integer)
   AND(s.source_catalog_id,s.source_revision_id) IS DISTINCT FROM(catalog,definition)))
 THEN RAISE EXCEPTION 'custom_projection' USING ERRCODE='P0001';END IF;
 request_value=jsonb_build_object('fixingConvention','published_index_observations_v1','trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,
  'curve',curve,'index',index_name,'fixedCurrency',fixed_currency,'floatingCurrency',float_currency,'fixedFrom',fixed_from,'fixedTo',fixed_to,'fixedPayment',fixed_payment AT TIME ZONE 'UTC',
  'floatingFrom',float_from,'floatingTo',float_to,'firstFixing',fixing AT TIME ZONE 'UTC','floatingPayment',float_payment AT TIME ZONE 'UTC','averaging',averaging,
  'price',price,'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation AT TIME ZONE 'UTC','executed',NULL,'effective',effective AT TIME ZONE 'UTC','reason',reason,'implementation',implementation);
 IF fixed_schedule IS NOT NULL OR float_schedule IS NOT NULL OR commodity IS NOT NULL THEN request_value=request_value||jsonb_build_object('fixedSchedule',fixed_schedule,'floatingSchedule',float_schedule,'commodity',commodity);END IF;
 request_value=request_value||jsonb_build_object('booking',booking,'bookingFactsPolicy','server_booking_utc_v1');
 IF trader IS NOT NULL OR trader_revision IS NOT NULL OR memo IS NOT NULL THEN request_value=request_value||jsonb_build_object('trader',trader,'traderRevision',trader_revision,'memo',memo);END IF;
 IF custom_projection IS NOT NULL THEN request_value=request_value||jsonb_build_object('customProjection',custom_projection);END IF;
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR (prior.request-'firstFixing'-'fixingInputs')<>(request_value-'firstFixing') THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
  PERFORM security.validate_custom_projection(catalog,custom_projection);
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
 custom_binding=security.retain_custom_projection(catalog,custom_projection,publication_number,from_at);
 fixed_rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,fixed_schedule,publication_number,from_at);
 float_rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,float_schedule,publication_number,from_at);
 SELECT id INTO fixed_side FROM trading.swap_fixed_sides WHERE catalog_id=catalog AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision AND quote_code=q.code AND delivery_from=fixed_from AND delivery_to=fixed_to AND payment_at=fixed_payment AND fixed_price=price AND payment_rule_id=fixed_rule;
 IF fixed_side IS NULL THEN
  fixed_side=gen_random_uuid();INSERT INTO trading.swap_fixed_sides VALUES(catalog,fixed_side,ref_catalog,ref_revision,q.code,fixed_from,fixed_to,fixed_payment,price,'fixed_total_payment_v1',publication_number,from_at,fixed_rule);
 END IF;
 SELECT id INTO floating_side FROM trading.swap_floating_sides WHERE catalog_id=catalog AND definition_revision_id=definition AND curve_code=curve AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision AND quote_code=q.code AND delivery_from=float_from AND delivery_to=float_to AND index_code=index_name AND first_fixing_at=fixing AND swap_floating_sides.implementation='index_observation_fixings_v1' AND payment_at=float_payment AND swap_floating_sides.averaging=save_projected_swap_with_parties.averaging AND payment_rule_id=float_rule AND custom_projection_id IS NOT DISTINCT FROM custom_binding;
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
  floating_side=gen_random_uuid();INSERT INTO trading.swap_floating_sides VALUES(catalog,floating_side,definition,curve,k.id,ref_catalog,ref_revision,index_name,q.code,float_from,float_to,fixing,float_payment,averaging,'index_observation_fixings_v1',publication_number,from_at,float_rule,custom_binding);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,floating_side,(row_number() OVER(ORDER BY k1.delivery_from,k1.id)-1)::integer,definition,curve,k1.id,1,publication_number,from_at
   FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND
    (averaging='monthly_index_price' AND k1.id=k.id OR averaging='calendar_day_arithmetic' AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to);
 END IF;
 SELECT instrument_id INTO instrument FROM trading.swap_instruments WHERE catalog_id=catalog AND fixed_side_id=fixed_side AND floating_side_id=floating_side AND fixed_coefficient=coefficient AND swap_instruments.discounting=save_projected_swap_with_parties.discounting;
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
 PERFORM security.validate_custom_projection(catalog,custom_projection);
 RETURN security.swap_operation_status(catalog,operation);
END $$;
CREATE OR REPLACE FUNCTION security.save_indexed_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb,input_pins uuid[]) RETURNS jsonb
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.save_projected_swap_with_parties(catalog,operation,trade,expected,book,definition,ref_catalog,ref_revision,curve,index_name,fixed_currency,float_currency,fixed_from,fixed_to,fixed_payment,float_from,float_to,float_payment,averaging,price,quantity,coefficient,discounting,valuation,executed,effective,reason,implementation,fixed_schedule,float_schedule,commodity,trader,trader_revision,memo,booking,input_pins,NULL);
$$;

-- Pure read helpers and the existing SELECT predicates are copied verbatim.
-- Every worker row additionally requires its independent live service authority.
DO $$DECLARE item record;p record;BEGIN
 FOR item IN SELECT 'market_data' AS schema_name,unnest(ARRAY['custom_curves','custom_curve_versions','custom_curve_nodes','custom_curve_edges',
  'custom_curve_node_sources','custom_curve_node_calendars','custom_curve_node_schedules','custom_curve_knowledge_receipts',
  'date_schedules','date_schedule_versions','knowledge_publications']) AS table_name
  UNION ALL SELECT 'trading','custom_curve_projection_bindings' LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname=item.schema_name AND tablename=item.table_name AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' AND permissive='PERMISSIVE')
  THEN RAISE EXCEPTION 'risk_worker_policy_missing: %.%',item.schema_name,item.table_name;END IF;
  FOR p IN SELECT * FROM pg_policies WHERE schemaname=item.schema_name AND tablename=item.table_name AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' LOOP
   EXECUTE format('CREATE POLICY %I ON %I.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)','risk_worker_'||p.policyname,item.schema_name,item.table_name,p.permissive,p.qual);
  END LOOP;
  EXECUTE format('CREATE POLICY risk_worker_service ON %I.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',item.schema_name,item.table_name);
  EXECUTE format('GRANT SELECT ON %I.%I TO openetrm_risk_worker',item.schema_name,item.table_name);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.custom_curve_visible(uuid,uuid,integer),security.custom_curve_source_visible(uuid,uuid),
 market_data.date_schedule_facts_at(uuid,integer) TO openetrm_risk_worker;
DO $$DECLARE fn record;BEGIN
 FOR fn IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('guard_custom_projection','validate_custom_projection','retain_custom_projection','save_projected_swap_with_parties','save_indexed_swap_with_parties') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',fn.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',fn.signature);
  IF fn.proname IN('save_projected_swap_with_parties','save_indexed_swap_with_parties') THEN
   EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',fn.signature);
  END IF;
 END LOOP;
END$$;
COMMIT;
