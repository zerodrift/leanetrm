-- Final native admission independently checks exact LNG terms and executable content.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION trading.lng_reference_identity(catalog uuid,revision uuid,code text) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_array('reference',jsonb_build_array('reference_revision',catalog,revision),code)
$$;

CREATE FUNCTION trading.require_lng_program_shape(catalog uuid,schedule uuid,slot integer,body jsonb,assignment market_data.lng_valuation_assignments) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE program_row trading.side_schedule_rows; curve market_data.curves; quote ref_data.quote_specifications; contract market_data.contracts;
 product ref_data.lng_products; quantity ref_data.quantity_specifications; target ref_data.settlement_targets; fixing market_data.index_fixing_observations;
 rc uuid=assignment.reference_catalog_id; rr uuid=assignment.reference_revision_id;
 load_start timestamptz=(body->>'loadFrom')::timestamptz; load_end timestamptz=(body->>'loadTo')::timestamptz;
 discharge_start timestamptz=(body->>'dischargeFrom')::timestamptz; discharge_end timestamptz=(body->>'dischargeTo')::timestamptz;
 payment timestamptz=(body->>'paymentAt')::timestamptz; literal numeric=(body->>'fixedPrice')::numeric;
 first_day date=(load_start AT TIME ZONE 'UTC')::date;
 last_day date=(discharge_end AT TIME ZONE 'UTC')::date+CASE WHEN(discharge_end AT TIME ZONE 'UTC')::time=time '00:00:00' THEN 0 ELSE 1 END;
 selected jsonb; selection jsonb; definition_key jsonb; contract_key jsonb; quote_identity jsonb; expected_assignment jsonb;
 expected_physical jsonb; expected_cash jsonb;
BEGIN
 IF slot NOT IN(0,1) THEN RAISE EXCEPTION 'lng_schedule'; END IF;
 SELECT * INTO STRICT curve FROM market_data.curves WHERE(catalog_id,revision_id,code)=(catalog,assignment.revision_id,assignment.curve_code);
 SELECT * INTO STRICT product FROM ref_data.lng_products WHERE(catalog_id,revision_id,code)=(rc,rr,assignment.product_code);
 SELECT * INTO STRICT quantity FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(rc,rr,assignment.energy_quantity_code);
 SELECT * INTO STRICT quote FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(rc,rr,curve.quote_code);
 IF curve.commodity_code<>product.commodity_code OR quote.kind<>'outright' OR quote.quantity_code<>quantity.code
 OR quantity.commodity_code<>product.commodity_code OR quantity.basis_code<>'gross_energy' OR quantity.dimension_code<>'energy'
 OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code,kind,fixed_offset_seconds)=(rc,rr,assignment.clock_code,'fixed_offset',0))
 THEN RAISE EXCEPTION 'lng_schedule_sources'; END IF;
 SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m
 ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
 WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,assignment.revision_id,assignment.curve_code)
 AND c.kind='forward' AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date
 AND c.delivery_from::timestamp AT TIME ZONE 'UTC'<=discharge_start AND discharge_end<=c.delivery_to::timestamp AT TIME ZONE 'UTC'
 AND NOT EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(c.catalog_id,c.revision_id,c.id));
 SELECT * INTO STRICT target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(rc,rr,curve.settlement_target_code);
 IF target.kind<>'index' OR target.index_b_code IS NOT NULL THEN RAISE EXCEPTION 'lng_schedule_sources'; END IF;
 SELECT * INTO STRICT fixing FROM market_data.index_fixing_observations
 WHERE(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code)=
 (catalog,assignment.revision_id,target.index_a_code,contract.delivery_from,contract.delivery_to,rc,rr,quote.code);
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
 WHERE(h.catalog_id,h.id)=(catalog,schedule) AND(s.definition_revision_id,s.reference_catalog_id,s.reference_revision_id,s.family)=
 (assignment.revision_id,rc,rr,CASE slot WHEN 0 THEN 'lng' ELSE 'fixed' END)) THEN RAISE EXCEPTION 'lng_schedule_revision'; END IF;

 selected=trading.lng_reference_identity(rc,rr,target.index_a_code);
 selection=jsonb_build_object('contract','price_settlement_selection_v1','CurveTargetIndex',selected,'Mode',1,'ProjectionPolicy',NULL,'SelectedIndex',selected);
 definition_key=jsonb_build_array('definition_revision',catalog,assignment.revision_id);
 contract_key=jsonb_build_array('contract',definition_key,contract.id);
 quote_identity=jsonb_build_array('quote',trading.lng_reference_identity(rc,rr,quote.code),1,
 trading.lng_reference_identity(rc,rr,quote.currency_code),trading.lng_reference_identity(rc,rr,quote.quantity_code),
 trim_scale(quote.multiplier)::text,trim_scale(quote.trading_tick)::text,trim_scale(quote.clearing_increment)::text,trim_scale(quote.minimum)::text,trim_scale(quote.maximum)::text);
 expected_assignment=jsonb_build_object('contract','lng_valuation_assignment_v1','Key',trading.lng_reference_identity(rc,rr,assignment.code),
 'Product',trading.lng_reference_identity(rc,rr,assignment.product_code),
 'Energy',jsonb_build_array('quantity_specification_v1',trading.lng_reference_identity(rc,rr,quantity.code),trading.lng_reference_identity(rc,rr,quantity.commodity_code),
  trading.lng_reference_identity(rc,rr,quantity.basis_code),trading.lng_reference_identity(rc,rr,quantity.unit_code),trading.lng_reference_identity(rc,rr,quantity.dimension_code)),
 'DischargeFacility',trading.lng_reference_identity(rc,rr,assignment.discharge_facility_code),
 'DischargeCustody',trading.lng_reference_identity(rc,rr,assignment.discharge_custody_code),'Clock',trading.lng_reference_identity(rc,rr,assignment.clock_code),
 'Settlement',selection,'Quote',quote_identity,
 'Projection',jsonb_build_array('projection',jsonb_build_array('request',jsonb_build_array('curve',definition_key,assignment.curve_code),contract_key),
  jsonb_build_array('market_contract',contract_key,2,contract.delivery_from,contract.delivery_to,to_char(contract.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',NULL),NULL),
 'Fixing',jsonb_build_object('contract','IndexFixing_v1','Index',selected,'Quote',trading.lng_reference_identity(rc,rr,quote.code),
  'From',fixing.observation_from,'To',fixing.observation_to,'FixingAt',to_char(fixing.fixing_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
  'Series',jsonb_build_array('series',definition_key,fixing.series_id),'Source',trading.lng_reference_identity(rc,rr,fixing.source_code),'Implementation',fixing.implementation));
 expected_physical=jsonb_build_object('contract','physical_lng_cargo_side_v1','Assignment',expected_assignment,
 'LoadFacility',trading.lng_reference_identity(rc,rr,body->'physical'->>'loadFacility'),'LoadCustody',trading.lng_reference_identity(rc,rr,body->'physical'->>'loadCustody'),
 'LoadFrom',to_char(load_start AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
 'LoadTo',to_char(load_end AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
 'DischargeFrom',to_char(discharge_start AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00',
 'DischargeTo',to_char(discharge_end AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00');
 expected_cash=jsonb_build_object('contract','FixedSide_v4','DateQuoteKey',trading.lng_reference_identity(rc,rr,quote.code),'IntervalDefinition',NULL,
 'QuantityBasis',trading.lng_reference_identity(rc,rr,quantity.code),
 'Price',jsonb_build_object('contract','ppa_flat_price_v1','Price',jsonb_build_array('price',trim_scale(literal)::text,
  jsonb_build_array('price_unit',trading.lng_reference_identity(rc,rr,quote.currency_code),trading.lng_reference_identity(rc,rr,quantity.code),trim_scale(quote.multiplier)::text))),
 'Periods',jsonb_build_array(jsonb_build_object('contract','fixed_side_period_v1','DeliveryFrom',first_day,'DeliveryTo',last_day,
  'PaymentAt',to_char(payment AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00','PaymentSchedule',NULL,'Allocation',jsonb_build_array('exact_ratio','1','1'))));
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
 WHERE(h.catalog_id,h.id)=(catalog,schedule) AND s.economic_identity::jsonb=CASE slot WHEN 0 THEN expected_physical ELSE expected_cash END
 AND CASE WHEN slot=0 THEN s.pricing_binding_identity::jsonb=expected_assignment ELSE s.pricing_binding_identity='' END)
 THEN RAISE EXCEPTION 'lng_schedule_identity'; END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'lng_schedule_ports'; END IF;
 IF slot=0 THEN
  IF(SELECT count(*) FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id)=(catalog,schedule))<>1 OR
  NOT EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id,name,unit_catalog_id,unit_revision_id,unit_code)=
   (catalog,schedule,'quantity',rc,rr,quantity.code) AND shape_identity::jsonb=expected_physical) OR
  (SELECT count(*) FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id)=(catalog,schedule))<>1 OR
  NOT EXISTS(SELECT 1 FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id,set_name,root_name)=(catalog,schedule,'quantity','lng/0/quantity'))
  THEN RAISE EXCEPTION 'lng_schedule_outputs'; END IF;
  IF(SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>8 OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r
  ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
  WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND(r.local_key<>'lng/0/market' OR CASE b.port
   WHEN 'market' THEN(b.kind,b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)
    IS DISTINCT FROM('contract_price',catalog,assignment.revision_id,assignment.curve_code,contract.id)
   WHEN 'lng' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.lng_assignment_code,b.source_catalog_id,b.definition_revision_id,b.coordinate::jsonb)
    IS DISTINCT FROM('lng_assignment',rc,rr,assignment.code,catalog,assignment.revision_id,expected_assignment)
   WHEN 'lng_terminal' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.location_code) IS DISTINCT FROM('location',rc,rr,assignment.discharge_facility_code)
   WHEN 'lng_custody' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.location_code) IS DISTINCT FROM('location',rc,rr,assignment.discharge_custody_code)
   WHEN 'lng_product' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.lng_product_code) IS DISTINCT FROM('lng_product',rc,rr,assignment.product_code)
   WHEN 'quote' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.quote_code) IS DISTINCT FROM('quote',rc,rr,quote.code)
   WHEN 'index' THEN(b.kind,b.reference_catalog_id,b.reference_revision_id,b.index_code) IS DISTINCT FROM('index',rc,rr,target.index_a_code)
   WHEN 'fixing' THEN(b.kind,b.source_catalog_id,b.definition_revision_id,b.reference_catalog_id,b.reference_revision_id,b.series_id,b.source_code,b.fixing_index_code,b.fixing_from,b.fixing_to,b.quote_code,b.coordinate)
    IS DISTINCT FROM('index_fixing',catalog,assignment.revision_id,rc,rr,fixing.series_id,fixing.source_code,fixing.index_code,fixing.observation_from,fixing.observation_to,fixing.quote_code,fixing.implementation)
   ELSE true END)) THEN RAISE EXCEPTION 'lng_schedule_sources'; END IF;
  IF(SELECT count(*) FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule))<>1 OR
  EXISTS(SELECT 1 FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule) AND(
   (g.clock_catalog_id,g.clock_revision_id,g.clock_code) IS DISTINCT FROM(rc,rr,assignment.clock_code) OR g.ordinal<>0 OR g.local_key<>'lng/0/cargo'
   OR g.origin<>load_start OR g.step_ticks<>extract(epoch FROM(discharge_end-load_start))*10000000 OR g.axis_count<>1
   OR g.month<>date_trunc('month',first_day)::date OR g.selection<>ARRAY[0] OR g.lengths<>ARRAY[1] OR g.first_row_ordinal IS DISTINCT FROM 0
   OR g.operating_days<>ARRAY[first_day-date '0001-01-01'] OR g.local_minutes<>ARRAY[extract(hour FROM load_start AT TIME ZONE 'UTC')::integer*60+extract(minute FROM load_start AT TIME ZONE 'UTC')::integer]
   OR g.occurrences<>ARRAY[1])) THEN RAISE EXCEPTION 'lng_schedule_grid'; END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'lng_schedule_sources'; END IF;
 END IF;
 FOR program_row IN SELECT * FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule) LOOP
  IF program_row.output_kind IN('quantity','price') AND(program_row.unit_catalog_id,program_row.unit_revision_id,program_row.unit_code) IS DISTINCT FROM(rc,rr,quantity.code)
  OR program_row.output_kind IN('price','money') AND(program_row.currency_catalog_id,program_row.currency_revision_id,program_row.currency_code) IS DISTINCT FROM(rc,rr,quote.currency_code)
  OR program_row.output_kind='price' AND program_row.quote_multiplier IS DISTINCT FROM quote.multiplier THEN RAISE EXCEPTION 'lng_schedule_units'; END IF;
  IF program_row.local_key='side/pv' THEN
   IF program_row.grid_ordinal IS NOT NULL OR program_row.value_at IS NOT NULL OR program_row.payment_ordinal IS NOT NULL
   OR(program_row.delivery_from,program_row.delivery_to,program_row.interval_from,program_row.interval_to,program_row.operating_date,
    program_row.hour_beginning,program_row.minute,program_row.occurrence,program_row.observation_from,program_row.observation_to)
    IS DISTINCT FROM(NULL::date,NULL::date,NULL::timestamptz,NULL::timestamptz,NULL::date,NULL::smallint,NULL::smallint,NULL::smallint,NULL::date,NULL::date)
   THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
   CONTINUE;
  END IF;
  IF slot=0 THEN
   IF program_row.local_key='lng/0/total' THEN
    IF program_row.grid_ordinal IS NOT NULL OR program_row.value_at IS NOT NULL OR program_row.payment_ordinal IS NOT NULL THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
   ELSE
    IF program_row.value_at IS DISTINCT FROM discharge_end OR program_row.payment_ordinal IS DISTINCT FROM 0 OR program_row.delivery_from IS NOT NULL OR program_row.delivery_to IS NOT NULL
    OR program_row.grid_ordinal IS DISTINCT FROM(CASE WHEN program_row.local_key='lng/0/market' THEN NULL::integer ELSE 0 END) THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
   END IF;
   IF program_row.local_key='lng/0/market' THEN
    IF(program_row.interval_from,program_row.interval_to,program_row.operating_date,program_row.hour_beginning,program_row.minute,program_row.occurrence,program_row.observation_from,program_row.observation_to)
    IS DISTINCT FROM(load_start,discharge_end,first_day,extract(hour FROM load_start AT TIME ZONE 'UTC')::smallint,extract(minute FROM load_start AT TIME ZONE 'UTC')::smallint,1::smallint,contract.delivery_from,contract.delivery_to)
    OR(program_row.parameters->>'selection')::jsonb IS DISTINCT FROM selection THEN RAISE EXCEPTION 'lng_schedule_sources'; END IF;
   ELSIF program_row.interval_from IS NOT NULL OR program_row.observation_from IS NOT NULL THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
   IF program_row.occurrence_key IS NOT NULL OR program_row.terms_digest IS NOT NULL THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
  ELSE
   IF program_row.value_at IS DISTINCT FROM payment OR program_row.payment_ordinal IS DISTINCT FROM 0 OR program_row.delivery_from IS DISTINCT FROM first_day
   OR program_row.delivery_to IS DISTINCT FROM last_day OR program_row.grid_ordinal IS NOT NULL OR program_row.interval_from IS NOT NULL THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
   IF program_row.operation='price_application' THEN
    IF(program_row.observation_from,program_row.observation_to) IS DISTINCT FROM(first_day,last_day) THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
   ELSIF program_row.observation_from IS NOT NULL THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
  END IF;
  IF program_row.operation='quantity_literal' AND(program_row.parameters->>'numerator'<>'1' OR program_row.parameters->>'denominator'<>'1') THEN RAISE EXCEPTION 'lng_schedule_quantity'; END IF;
  IF program_row.operation='constant_price' AND(program_row.parameters->>'numerator')::numeric<>literal*(program_row.parameters->>'denominator')::numeric THEN RAISE EXCEPTION 'lng_schedule_price'; END IF;
  IF program_row.operation='price_application' AND(program_row.parameters->>'numerator'<>program_row.parameters->>'denominator' OR program_row.parameters->>'mapping'<>'fixed_total_payment_v1') THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
  IF program_row.operation='weighted_price' AND program_row.parameters->>'totalScaledWeight'<>'1000000000000000000' THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
  IF program_row.operation='discount' AND program_row.parameters->>'policy'<>'context_required_v1' THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
  IF program_row.operation='output_guard' AND(program_row.parameters IS DISTINCT FROM jsonb_build_object('measure',CASE slot WHEN 0 THEN 'value_excluding_overdue_v1' ELSE 'present_value_v1' END,
   'lifecycle',CASE slot WHEN 0 THEN 'future_lng_cargo_v1' ELSE 'undue_payment_v1' END,'pricePolicy','selected_context_v1','averaging','none',
   'firstFixingAt','none','paymentRule','explicit_payment_date_v1','unadjustedPayment','none')) THEN RAISE EXCEPTION 'lng_schedule_lifecycle'; END IF;
 END LOOP;
 IF EXISTS(WITH expected AS(
  SELECT CASE slot WHEN 0 THEN 7 ELSE 8 END n,'side/pv' k,'sum' op,'root' role,'money' kind UNION ALL
  SELECT v.n,'lng/0/'||v.k,v.op,v.role,v.kind FROM(VALUES(0,'market','price_lookup','source','price'),(1,'quantity','quantity_literal','source','quantity'),
   (2,'amount','amount','contribution','money'),(3,'discount','discount','source','factor'),(4,'discounted','pv','intermediate','money'),
   (5,'value','output_guard','root','money'),(6,'total','sum_cells','intermediate','money'))v(n,k,op,role,kind) WHERE slot=0 UNION ALL
  SELECT v.n,'payment/0/'||v.k,v.op,v.role,v.kind FROM(VALUES(0,'quantity','quantity_literal','source','quantity'),(1,'observation/0','constant_price','source','price'),
   (2,'application/0','price_application','intermediate','price'),(3,'average','weighted_price','intermediate','price'),(4,'amount','amount','contribution','money'),
   (5,'discount','discount','source','factor'),(6,'discounted','pv','intermediate','money'),(7,'value','output_guard','root','money'))v(n,k,op,role,kind) WHERE slot=1),
 actual AS(SELECT ordinal n,local_key k,operation op,role,output_kind kind FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'lng_schedule_rows'; END IF;
 IF EXISTS(WITH expected AS(
  SELECT 'lng/0/'||v.t target,v.p port,v.n ordinal,'lng/0/'||v.s source FROM(VALUES
   ('amount','quantity',0,'quantity'),('amount','price',0,'market'),('discounted','amount',0,'amount'),('discounted','discount',0,'discount'),
   ('value','amount',0,'amount'),('value','discounted',0,'discounted'),('total','input',0,'value'))v(t,p,n,s) WHERE slot=0 UNION ALL
  SELECT 'side/pv','input',0,'lng/0/total' WHERE slot=0 UNION ALL
  SELECT 'payment/0/'||v.t,v.p,v.n,'payment/0/'||v.s FROM(VALUES('application/0','input',0,'observation/0'),('average','input',0,'application/0'),
   ('amount','quantity',0,'quantity'),('amount','price',0,'average'),('discounted','amount',0,'amount'),('discounted','discount',0,'discount'),
   ('value','amount',0,'amount'),('value','discounted',0,'discounted'))v(t,p,n,s) WHERE slot=1 UNION ALL
  SELECT 'side/pv','input',0,'payment/0/value' WHERE slot=1),
 actual AS(SELECT t.local_key,e.port,e.ordinal,s.local_key FROM trading.side_schedule_edges e
  JOIN trading.side_schedule_rows t ON(t.catalog_id,t.schedule_id,t.ordinal)=(e.catalog_id,e.schedule_id,e.target)
  JOIN trading.side_schedule_rows s ON(s.catalog_id,s.schedule_id,s.ordinal)=(e.catalog_id,e.schedule_id,e.source) WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'lng_schedule_edges'; END IF;
 IF EXISTS(WITH expected AS(
  SELECT 'pv' name,'side/pv' k,'pv' measure UNION ALL
  SELECT 'lng/0/'||v.name,'lng/0/'||v.k,v.measure FROM(VALUES('quantity','quantity','schedule_quantity'),('delivery-value','amount','physical_value'),('pv','value','contribution_pv'))v(name,k,measure) WHERE slot=0 UNION ALL
  SELECT 'payment/0/'||v.name,'payment/0/'||v.k,v.measure FROM(VALUES('cash','amount','contractual_amount'),('pv','value','contribution_pv'))v(name,k,measure) WHERE slot=1),
 actual AS(SELECT o.name,r.local_key,o.measure FROM trading.side_schedule_roots o JOIN trading.side_schedule_rows r
  ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal) WHERE(o.catalog_id,o.schedule_id)=(catalog,schedule))
 (SELECT * FROM expected EXCEPT ALL SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT ALL SELECT * FROM expected)) THEN RAISE EXCEPTION 'lng_schedule_roots'; END IF;
EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'lng_schedule_sources';
END$$;
ALTER FUNCTION trading.require_lng_program_shape(uuid,uuid,integer,jsonb,market_data.lng_valuation_assignments) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_lng_program_shape(uuid,uuid,integer,jsonb,market_data.lng_valuation_assignments),trading.lng_reference_identity(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.lng_reference_identity(uuid,uuid,text) TO openetrm_security_owner;

CREATE FUNCTION security.lng_operation_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE actor uuid; recorded trading.lng_trade_revisions;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No LNG read authority' USING ERRCODE='42501'; END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts
 WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO recorded FROM trading.lng_trade_revisions WHERE(catalog_id,operation_id,actor_id)=(catalog,operation,actor);
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false); END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'LNG read authority expired' USING ERRCODE='42501'; END IF;
 RETURN jsonb_build_object('tradeId',recorded.trade_id,'instrumentId',recorded.instrument_id,'revision',recorded.revision,'operationId',operation);
END$$;

CREATE FUNCTION security.book_physical_lng(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE ctx security.request_contexts; prior trading.trades; staged trading.side_schedule_requests;
 assignment market_data.lng_valuation_assignments; input ref_data.quantity_specifications; conversion ref_data.lng_mass_energy_conversions;
 operation uuid; book uuid; reference_catalog uuid; reference_revision uuid;
 load_start timestamptz; load_end timestamptz; discharge_start timestamptz; discharge_end timestamptz; payment timestamptz;
 original numeric; energy numeric; price numeric; physical jsonb; incoterms jsonb; resolved jsonb;
 publication integer; trade uuid; instrument uuid; booking jsonb; item record; physical_direction integer; cash_direction integer;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade')
 THEN RAISE EXCEPTION 'No LNG booking authority' USING ERRCODE='42501'; END IF;
 SELECT * INTO STRICT ctx FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
 AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF ctx.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only'; END IF;
 operation=(body->>'operationId')::uuid;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade')
 OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'LNG booking authority expired' USING ERRCODE='42501'; END IF;
 SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>ctx.actor_id OR prior.request IS DISTINCT FROM body OR NOT EXISTS(
   SELECT 1 FROM trading.lng_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'lng_conflict'; END IF;
  RETURN security.lng_operation_status(catalog,operation);
 END IF;
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT body ?& ARRAY[
 'snapshotId','convention','knowledgeMode','profile','operationId','bookId','physical','loadFrom','loadTo','dischargeFrom','dischargeTo',
 'quantity','quantityCode','quantityUnit','conversionCode','quantityBasis','tolerance','incoterms','direction','considerationDirection','fixedPrice','paymentAt',
 'discounting','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision'] OR
 body-ARRAY['snapshotId','convention','knowledgeMode','profile','operationId','bookId','physical','loadFrom','loadTo','dischargeFrom','dischargeTo',
 'quantity','quantityCode','quantityUnit','conversionCode','quantityBasis','tolerance','incoterms','direction','considerationDirection','fixedPrice','paymentAt',
 'discounting','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision']<>'{}'::jsonb
 THEN RAISE EXCEPTION 'lng_input'; END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR
 (body->>'snapshotId')::uuid IS NULL OR(body->>'snapshotId')::uuid='00000000-0000-0000-0000-000000000000'::uuid OR
 body->>'convention' IS DISTINCT FROM 'physical_lng_cargo_v1' OR body->>'knowledgeMode' IS DISTINCT FROM 'current' OR
 body->>'profile' IS DISTINCT FROM 'fixed_gross_discharge_energy_v1' OR body->>'quantityBasis' IS DISTINCT FROM 'gross_discharge_energy_total' OR
 body->'discounting' IS DISTINCT FROM 'false'::jsonb OR nullif(body->>'quantityCode','') IS NULL OR nullif(body->>'quantityUnit','') IS NULL OR
 coalesce(body->>'direction','') NOT IN('receive','deliver') OR coalesce(body->>'considerationDirection','') NOT IN('pay','receive')
 THEN RAISE EXCEPTION 'lng_input'; END IF;
 IF EXISTS(SELECT 1 FROM unnest(ARRAY[body->>'quantity',body->>'fixedPrice',body->>'tolerance']) value
 WHERE value IS NULL OR length(value)>45 OR value !~ '^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$') THEN RAISE EXCEPTION 'lng_input'; END IF;
 original=(body->>'quantity')::numeric; price=(body->>'fixedPrice')::numeric;
 IF original<=0 OR original>=1e24 OR abs(price)>=1e12 OR(body->>'tolerance')::numeric<>0 THEN RAISE EXCEPTION 'lng_input'; END IF;
 IF EXISTS(SELECT 1 FROM unnest(ARRAY[body->>'loadFrom',body->>'loadTo',body->>'dischargeFrom',body->>'dischargeTo',body->>'paymentAt']) value
 WHERE value IS NULL OR value !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6}|\.[0-9]{6}0)?(Z|\+00:00)$')
 THEN RAISE EXCEPTION 'lng_input'; END IF;
 load_start=(body->>'loadFrom')::timestamptz; load_end=(body->>'loadTo')::timestamptz;
 discharge_start=(body->>'dischargeFrom')::timestamptz; discharge_end=(body->>'dischargeTo')::timestamptz; payment=(body->>'paymentAt')::timestamptz;
 IF load_start>=load_end OR load_end>discharge_start OR discharge_start>=discharge_end OR payment<discharge_end OR
 load_start<'2000-01-01T00:00:00Z'::timestamptz OR discharge_end>'2100-01-01T00:00:00Z'::timestamptz OR
 valuation IS NULL OR NOT isfinite(valuation) OR valuation>=load_start THEN RAISE EXCEPTION 'lng_delivery_lifecycle'; END IF;
 physical=body->'physical'; incoterms=body->'incoterms';
 IF jsonb_typeof(incoterms) IS DISTINCT FROM 'object' OR NOT incoterms ?& ARRAY['edition','rule','namedPlace'] OR
 incoterms-ARRAY['edition','rule','namedPlace']<>'{}'::jsonb OR EXISTS(SELECT 1 FROM jsonb_each(incoterms) p
 WHERE jsonb_typeof(p.value)<>'string' OR nullif(btrim(p.value#>>'{}'),'') IS NULL) THEN RAISE EXCEPTION 'lng_incoterms'; END IF;
 -- Source identities are derived outputs; callers cannot narrow physical resolution.
 resolved=security.resolve_lng_delivery(catalog,physical,discharge_start,discharge_end,valuation);
 reference_catalog=(physical->>'referenceCatalogId')::uuid; reference_revision=(physical->>'referenceRevisionId')::uuid;
 SELECT * INTO STRICT assignment FROM market_data.lng_valuation_assignments
 WHERE(catalog_id,revision_id,code)=(catalog,(resolved->>'definitionRevisionId')::uuid,resolved->>'assignmentCode');
 IF NOT EXISTS(SELECT 1 FROM ref_data.incoterms_rules r JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=
 (r.catalog_id,r.revision_id,incoterms->>'namedPlace') WHERE(r.catalog_id,r.revision_id,r.edition_code,r.code)=
 (reference_catalog,reference_revision,incoterms->>'edition',incoterms->>'rule')) THEN RAISE EXCEPTION 'lng_incoterms'; END IF;
 SELECT * INTO input FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code,unit_code)=
 (reference_catalog,reference_revision,body->>'quantityCode',body->>'quantityUnit');
 IF NOT FOUND OR input.commodity_code<>physical->>'commodity' THEN RAISE EXCEPTION 'lng_quantity'; END IF;
 IF input.code=assignment.energy_quantity_code THEN
  IF body->'conversionCode' IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'lng_quantity_conversion'; END IF;
  energy=original;
 ELSE
  SELECT * INTO conversion FROM ref_data.lng_mass_energy_conversions
  WHERE(catalog_id,revision_id,code,product_code,mass_quantity_code,energy_quantity_code)=
  (reference_catalog,reference_revision,body->>'conversionCode',assignment.product_code,input.code,assignment.energy_quantity_code);
  IF NOT FOUND OR input.basis_code<>'discharge_mass' OR input.dimension_code<>'mass' THEN RAISE EXCEPTION 'lng_quantity_conversion'; END IF;
  energy=original*conversion.energy_per_mass;
  IF scale(trim_scale(energy))>18 THEN RAISE EXCEPTION 'lng_quantity_precision'; END IF;
 END IF;
 IF energy<=0 OR energy>=1e24 THEN RAISE EXCEPTION 'lng_quantity'; END IF;
 SELECT * INTO staged FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,operation);
 IF NOT FOUND OR staged.actor_id<>ctx.actor_id OR staged.command_hash<>sha256(convert_to(body::text,'UTF8')) OR staged.permission<>'BookTrade'
 THEN RAISE EXCEPTION 'lng_schedule'; END IF;
 PERFORM security.lock_side_schedule_request(catalog,operation);
 IF(SELECT count(*) FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,operation))<>2 OR
 EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)) OR
 EXISTS(SELECT 1 FROM trading.instrument_schedule_cells WHERE(catalog_id,request_id)=(catalog,operation)) THEN RAISE EXCEPTION 'lng_schedule'; END IF;
 FOR item IN SELECT i.slot,i.schedule_id FROM trading.side_schedule_request_items i WHERE(i.catalog_id,i.request_id)=(catalog,operation) ORDER BY i.slot LOOP
  PERFORM trading.require_lng_program_shape(catalog,item.schedule_id,item.slot,body,assignment);
 END LOOP;
 book=(body->>'bookId')::uuid;
 booking=jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date,'legalEntityId',body->'legalEntityId',
 'legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision');
 PERFORM security.require_trade_parties(catalog,booking,valuation);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
 WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=valuation AND p.effective_at<=valuation) THEN RAISE EXCEPTION 'lng_book'; END IF;
 physical_direction=CASE body->>'direction' WHEN 'receive' THEN 1 ELSE -1 END;
 cash_direction=CASE body->>'considerationDirection' WHEN 'receive' THEN 1 ELSE -1 END;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id()); trade=gen_random_uuid(); instrument=gen_random_uuid();
 INSERT INTO trading.instruments(catalog_id,id,publication_order,effective_at) VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.lng_instruments(catalog_id,instrument_id,definition_revision_id,assignment_code,reference_catalog_id,reference_revision_id,
 product_code,energy_quantity_code,load_stage,load_facility_code,load_custody_code,load_from,load_to,discharge_stage,discharge_facility_code,discharge_custody_code,
 discharge_from,discharge_to,clock_code,incoterms_edition_code,incoterms_rule_code,incoterms_place_code,fixed_price,payment_at,cash_direction,publication_order,effective_at)
 VALUES(catalog,instrument,assignment.revision_id,assignment.code,reference_catalog,reference_revision,assignment.product_code,assignment.energy_quantity_code,
 'load',physical->>'loadFacility',physical->>'loadCustody',load_start,load_end,'discharge',assignment.discharge_facility_code,assignment.discharge_custody_code,
 discharge_start,discharge_end,assignment.clock_code,incoterms->>'edition',incoterms->>'rule',incoterms->>'namedPlace',price,payment,cash_direction,publication,valuation);
 INSERT INTO trading.lng_memberships SELECT catalog,instrument,i.slot,h.side_id FROM trading.side_schedule_request_items i JOIN trading.side_schedules h
 ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id) WHERE(i.catalog_id,i.request_id)=(catalog,operation);
 PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades(catalog_id,id,book_id,operation_id,actor_id,request,publication_order,effective_at)
 VALUES(catalog,trade,book,operation,ctx.actor_id,body,publication,valuation);
 INSERT INTO trading.lng_trade_revisions(catalog_id,trade_id,revision,instrument_id,book_id,operation_id,actor_id,reference_catalog_id,reference_revision_id,
 product_code,energy_quantity_code,original_quantity,quantity_code,conversion_code,energy_quantity,direction,legal_entity_id,legal_entity_revision,
 counterparty_id,counterparty_revision,publication_order,effective_at)
 VALUES(catalog,trade,1,instrument,book,operation,ctx.actor_id,reference_catalog,reference_revision,assignment.product_code,assignment.energy_quantity_code,
 original,input.code,conversion.code,energy,physical_direction,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,
 (body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') OR NOT security.market_access(catalog,'ReadMarketData')
 OR NOT security.reference_access(reference_catalog,'ReadMarketData') OR NOT security.reference_version_visible(reference_catalog,reference_revision)
 OR NOT security.market_fact_visible(catalog,assignment.revision_id,NULL) THEN RAISE EXCEPTION 'LNG booking authority expired' USING ERRCODE='42501'; END IF;
 RETURN security.lng_operation_status(catalog,operation);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow
 THEN RAISE EXCEPTION 'lng_input';
END$$;
ALTER FUNCTION security.lng_operation_status(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.book_physical_lng(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.lng_operation_status(uuid,uuid),security.book_physical_lng(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.lng_operation_status(uuid,uuid),security.book_physical_lng(uuid,jsonb,timestamptz) TO openetrm_runtime;
COMMIT;
