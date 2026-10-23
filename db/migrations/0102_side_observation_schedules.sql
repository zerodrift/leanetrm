-- Stop write admission and drain requests before applying. Existing side IDs,
-- request receipts and the two earlier authoring operations remain unchanged.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE ref_data.quote_specifications ADD CONSTRAINT quote_observation_quantity_key
 UNIQUE(catalog_id,revision_id,code,quantity_code);

CREATE TABLE trading.swap_observation_schedules (
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,index_code ref_data.code NOT NULL,quote_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,
 delivery_from date NOT NULL,delivery_to date NOT NULL,averaging text NOT NULL,calendar_code ref_data.code,
 identity_hash bytea NOT NULL CHECK(octet_length(identity_hash)=32),
 implementation text NOT NULL CHECK(implementation='side_observation_schedule_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,identity_hash),
 UNIQUE(catalog_id,id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,averaging),
 UNIQUE(catalog_id,id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,quantity_code),
 CHECK(delivery_to=(delivery_from+INTERVAL '1 month')::date AND extract(day FROM delivery_from)=1),
 CHECK(averaging IN('observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted')),
 CHECK((averaging='quantity_weighted')=(calendar_code IS NULL)),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code,quantity_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code,quantity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_observation_quantities (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 365),
 definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,contract_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,quantity_code ref_data.code NOT NULL,
 amount ref_data.exact_number NOT NULL CHECK(amount>0),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,schedule_id,ordinal),UNIQUE(catalog_id,schedule_id,contract_id),
 FOREIGN KEY(catalog_id,schedule_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,quantity_code)
 REFERENCES trading.swap_observation_schedules(catalog_id,id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,quantity_code),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.swap_floating_sides ADD COLUMN observation_schedule_id uuid;
ALTER TABLE trading.swap_floating_sides ADD FOREIGN KEY
 (catalog_id,observation_schedule_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,averaging)
 REFERENCES trading.swap_observation_schedules
 (catalog_id,id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,averaging);
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_sides_averaging_check;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_floating_sides_averaging_check
 CHECK(averaging IN('monthly_index_price','calendar_day_arithmetic','observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted'));
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_observation_schedule_required
 CHECK((averaging IN('observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted'))=(observation_schedule_id IS NOT NULL));
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_economic_key;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_floating_economic_key UNIQUE NULLS NOT DISTINCT
 (catalog_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,
 delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,payment_rule_id,custom_projection_id,spread,observation_schedule_id);
ALTER TABLE trading.swap_reset_projections DROP CONSTRAINT swap_reset_projections_weight_check;
ALTER TABLE trading.swap_reset_projections ADD CONSTRAINT swap_reset_projections_weight_check CHECK(weight>0);

DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['swap_observation_schedules','swap_observation_quantities'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',name);
  EXECUTE format('CREATE POLICY reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',name);
  EXECUTE format('CREATE POLICY risk_worker_scoped_read ON trading.%I FOR SELECT TO openetrm_risk_worker USING(security.trading_visible(catalog_id,publication_order,effective_at))',name);
  EXECUTE format('CREATE POLICY risk_worker_reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',name);
  EXECUTE format('CREATE POLICY risk_worker_service ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',name);
  EXECUTE format('CREATE POLICY current_insert ON trading.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',name);
  EXECUTE format('CREATE POLICY current_update ON trading.%I AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context())',name);
  EXECUTE format('CREATE POLICY current_delete ON trading.%I AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context())',name);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',name);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime,openetrm_risk_worker',name);
 END LOOP;
END$$;

-- Admission checks the managed plan independently against immutable exact
-- source/calendar rows. No author-supplied price, fixing or contract ID is trusted.
CREATE FUNCTION security.swap_observation_rows(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,from_date date,to_date date,averaging text,schedule jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE calendar ref_data.calendars;quote ref_data.quote_specifications;item jsonb;entry record;chosen date;required_dates date[]:='{}';
 weights numeric[]:='{}';amounts jsonb:='{}';found_contract uuid;counted integer;result jsonb:='[]';ordinal integer:=0;value numeric;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
  OR NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(ref_catalog,ref_revision)
 THEN RAISE EXCEPTION 'No current observation access' USING ERRCODE='42501';END IF;
 IF from_date IS NULL OR to_date IS NULL OR extract(day FROM from_date)<>1 OR from_date>'9999-11-30'::date
  OR to_date IS DISTINCT FROM(from_date+INTERVAL '1 month')::date OR averaging IS NULL
  OR averaging NOT IN('observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted')
  OR jsonb_typeof(schedule) IS DISTINCT FROM 'object' OR NOT schedule ?& ARRAY['implementation','calendar','quantities']
  OR(SELECT count(*) FROM jsonb_object_keys(schedule))<>3 OR schedule->>'implementation' IS DISTINCT FROM 'side_observation_schedule_v1'
 THEN RAISE EXCEPTION 'swap_averaging';END IF;
 SELECT q.* INTO quote FROM market_data.curves c JOIN ref_data.quote_specifications q
  ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
  WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id)=(catalog,definition,curve,ref_catalog,ref_revision)
   AND q.kind='outright' AND t.kind='index' AND t.index_a_code=index_name;
 IF NOT FOUND THEN RAISE EXCEPTION 'swap_definition';END IF;
 IF averaging='quantity_weighted' THEN
  IF schedule->'calendar' IS DISTINCT FROM 'null'::jsonb OR jsonb_typeof(schedule->'quantities') IS DISTINCT FROM 'array'
   OR jsonb_array_length(schedule->'quantities')<>to_date-from_date THEN RAISE EXCEPTION 'swap_averaging';END IF;
  FOR item IN SELECT * FROM jsonb_array_elements(schedule->'quantities') LOOP
   IF jsonb_typeof(item) IS DISTINCT FROM 'object' OR NOT item ?& ARRAY['from','to','quantity'] OR(SELECT count(*) FROM jsonb_object_keys(item))<>3
    OR item->>'from' IS DISTINCT FROM to_char(from_date+ordinal,'YYYY-MM-DD') OR item->>'to' IS DISTINCT FROM to_char(from_date+ordinal+1,'YYYY-MM-DD')
    OR jsonb_typeof(item->'quantity') IS DISTINCT FROM 'object' OR NOT(item->'quantity') ?& ARRAY['value','specification']
    OR(SELECT count(*) FROM jsonb_object_keys(item->'quantity'))<>2 OR jsonb_typeof(item->'quantity'->'value') IS DISTINCT FROM 'string'
    OR item->'quantity'->'specification' IS DISTINCT FROM jsonb_build_object('revision',jsonb_build_object('catalogId',ref_catalog,'revisionId',ref_revision),'code',quote.quantity_code)
   THEN RAISE EXCEPTION 'swap_averaging';END IF;
   value=(item->'quantity'->>'value')::numeric;
   IF value IS NULL OR value<=0 OR abs(value)>=1e24 OR scale(value)>18 OR value::text IN('NaN','Infinity','-Infinity') THEN RAISE EXCEPTION 'swap_averaging';END IF;
   amounts=amounts||jsonb_build_object(to_char(from_date+ordinal,'YYYY-MM-DD'),trim_scale(value)::text);
   required_dates=array_append(required_dates,from_date+ordinal);weights=array_append(weights,value);ordinal=ordinal+1;
  END LOOP;
 ELSE
  IF schedule->'quantities' IS DISTINCT FROM 'null'::jsonb OR jsonb_typeof(schedule->'calendar') IS DISTINCT FROM 'object'
   OR schedule->'calendar'->'revision' IS DISTINCT FROM jsonb_build_object('catalogId',ref_catalog,'revisionId',ref_revision)
   OR NOT(schedule->'calendar') ?& ARRAY['revision','code'] OR(SELECT count(*) FROM jsonb_object_keys(schedule->'calendar'))<>2
  THEN RAISE EXCEPTION 'swap_averaging';END IF;
  SELECT * INTO calendar FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code)=(ref_catalog,ref_revision,schedule->'calendar'->>'code');
  IF NOT FOUND OR calendar.coverage_from>from_date OR calendar.coverage_to<to_date OR calendar.coverage_to-calendar.coverage_from>36600
  THEN RAISE EXCEPTION 'swap_averaging';END IF;
  FOR entry IN SELECT (from_date+n)::date AS date FROM generate_series(0,to_date-from_date-1) n LOOP
   SELECT CASE WHEN averaging='calendar_days_previous' THEN max(d.date) ELSE min(d.date) END INTO chosen
   FROM(SELECT calendar.coverage_from+n AS date FROM generate_series(0,calendar.coverage_to-calendar.coverage_from-1) n) d
   LEFT JOIN ref_data.calendar_days x ON(x.catalog_id,x.revision_id,x.calendar_code,x.business_date)=(ref_catalog,ref_revision,calendar.code,d.date)
   WHERE coalesce(x.is_business_day,(calendar.weekend_mask&(1<<(extract(isodow FROM d.date)::integer-1)))=0)
    AND(CASE averaging WHEN 'observation_arithmetic' THEN d.date=entry.date WHEN 'calendar_days_previous' THEN d.date<=entry.date ELSE d.date>=entry.date END);
   IF chosen IS NULL THEN
    IF averaging='observation_arithmetic' THEN CONTINUE;END IF;
    RAISE EXCEPTION 'swap_averaging';
   END IF;
   counted=array_position(required_dates,chosen);
   IF counted IS NULL THEN required_dates=array_append(required_dates,chosen);weights=array_append(weights,1);
   ELSE weights[counted]=weights[counted]+1;END IF;
  END LOOP;
 END IF;
 IF cardinality(required_dates)=0 OR cardinality(required_dates)>366 THEN RAISE EXCEPTION 'swap_averaging';END IF;
 ordinal=0;
 FOR entry IN SELECT date,weights[n] AS weight FROM unnest(required_dates) WITH ORDINALITY r(date,n) ORDER BY date LOOP
  SELECT count(*),min(k.id::text)::uuid INTO counted,found_contract FROM market_data.curve_members m
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
  WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,curve) AND k.kind='forward'
   AND k.delivery_from=entry.date AND k.delivery_to=entry.date+1;
  IF counted<>1 THEN RAISE EXCEPTION 'swap_projection';END IF;
  result=result||jsonb_build_array(jsonb_build_object('ordinal',ordinal,'contractId',found_contract,'from',entry.date,'to',entry.date+1,
   'weight',trim_scale(entry.weight),'quantity',CASE WHEN averaging='quantity_weighted' THEN amounts->>to_char(entry.date,'YYYY-MM-DD') ELSE NULL END));
  ordinal=ordinal+1;
 END LOOP;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_version_visible(ref_catalog,ref_revision)
 THEN RAISE EXCEPTION 'Observation access expired' USING ERRCODE='42501';END IF;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR datetime_field_overflow THEN RAISE EXCEPTION 'swap_averaging';
END$$;

CREATE FUNCTION security.swap_observation_fixing_evidence(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,valuation timestamptz,payment timestamptz,input_pins uuid[],selected jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;first_fixing timestamptz;seen integer:=0;actual uuid;actual_count integer;used uuid[]:='{}';
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
  OR NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(ref_catalog,ref_revision)
 THEN RAISE EXCEPTION 'No current fixing access' USING ERRCODE='42501';END IF;
 IF input_pins IS NULL OR cardinality(input_pins)>366 OR cardinality(input_pins)<>(SELECT count(DISTINCT x) FROM unnest(input_pins) x)
  OR jsonb_typeof(selected) IS DISTINCT FROM 'array' OR jsonb_array_length(selected) NOT BETWEEN 1 AND 366
 THEN RAISE EXCEPTION 'index_fixing_inputs';END IF;
 FOR r IN
  SELECT k.expiry_at,f.* FROM jsonb_to_recordset(selected) picked("contractId" uuid)
  JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(catalog,definition,curve,picked."contractId")
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
  LEFT JOIN market_data.index_fixing_observations f ON(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to)=
   (k.catalog_id,k.revision_id,index_name,k.delivery_from,k.delivery_to)
  ORDER BY k.delivery_from,k.id
 LOOP
  seen=seen+1;
  IF r.index_code IS NULL OR(r.reference_catalog_id,r.reference_revision_id) IS DISTINCT FROM(ref_catalog,ref_revision)
   OR r.quote_code IS DISTINCT FROM(SELECT cv.quote_code FROM market_data.curves cv WHERE(cv.catalog_id,cv.revision_id,cv.code)=(catalog,definition,curve))
  THEN RAISE EXCEPTION 'index_fixing_convention_missing';END IF;
  IF r.fixing_at>=payment THEN RAISE EXCEPTION 'index_fixing_definition';END IF;
  first_fixing=least(first_fixing,r.fixing_at);
  IF valuation<r.fixing_at THEN
   IF valuation>=r.expiry_at THEN RAISE EXCEPTION 'swap_projection';END IF;
  ELSE
   SELECT count(*),min(o.id::text)::uuid INTO actual_count,actual FROM market_data.observations o
   WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.reference_catalog_id,o.reference_revision_id,o.source_code)=
    (catalog,definition,r.series_id,ref_catalog,ref_revision,r.source_code)
    AND o.id=ANY(input_pins) AND o.role='index_fixing' AND o.publication_state='final'
    AND r.fixing_at<=o.observed_at AND o.observed_at<=valuation AND security.market_fact_visible(o.catalog_id,o.definition_revision_id,o.id);
   IF actual_count<>1 THEN RAISE EXCEPTION 'index_fixing_missing';END IF;
   used=array_append(used,actual);
  END IF;
 END LOOP;
 IF seen<>jsonb_array_length(selected) OR first_fixing IS NULL THEN RAISE EXCEPTION 'swap_projection';END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
  OR NOT security.reference_version_visible(ref_catalog,ref_revision)
 THEN RAISE EXCEPTION 'Fixing access expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('firstFixing',first_fixing AT TIME ZONE 'UTC','inputs',to_jsonb(used));
END$$;

CREATE FUNCTION security.retain_swap_observation_schedule(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,quote ref_data.quote_specifications,from_date date,to_date date,averaging text,schedule jsonb,selected jsonb,
 publication integer,effective timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE key bytea;retained uuid;calendar_name text;canonical jsonb;
BEGIN
 calendar_name=schedule->'calendar'->>'code';
 canonical=jsonb_build_array(definition,curve,ref_catalog,ref_revision,index_name,quote.code,quote.quantity_code,from_date,to_date,averaging,
  calendar_name,'side_observation_schedule_v1',selected);
 key=sha256(convert_to(canonical::text,'UTF8'));
 SELECT s.id INTO retained FROM trading.swap_observation_schedules s WHERE(s.catalog_id,s.identity_hash)=(catalog,key);
 IF retained IS NOT NULL THEN
  IF NOT EXISTS(SELECT 1 FROM trading.swap_observation_schedules s WHERE(s.catalog_id,s.id,s.definition_revision_id,s.curve_code,s.reference_catalog_id,s.reference_revision_id,s.index_code,s.quote_code,s.quantity_code,s.delivery_from,s.delivery_to,s.averaging,s.calendar_code,s.implementation)
   IS NOT DISTINCT FROM(catalog,retained,definition,curve,ref_catalog,ref_revision,index_name,quote.code,quote.quantity_code,from_date,to_date,retain_swap_observation_schedule.averaging,calendar_name,'side_observation_schedule_v1'))
   OR EXISTS(
    SELECT 1 FROM(SELECT ordinal,contract_id,amount,quantity_code FROM trading.swap_observation_quantities q WHERE(q.catalog_id,q.schedule_id)=(catalog,retained)) old
    FULL JOIN(SELECT x.ordinal,x."contractId" AS contract_id,x.quantity::numeric AS amount,quote.quantity_code AS quantity_code
      FROM jsonb_to_recordset(selected) x(ordinal integer,"contractId" uuid,quantity text) WHERE x.quantity IS NOT NULL) wanted USING(ordinal)
    WHERE(old.contract_id,old.amount,old.quantity_code) IS DISTINCT FROM(wanted.contract_id,wanted.amount,wanted.quantity_code)
   ) THEN RAISE EXCEPTION 'swap_observation_identity';END IF;
  RETURN retained;
 END IF;
 retained=gen_random_uuid();
 INSERT INTO trading.swap_observation_schedules VALUES(catalog,retained,definition,curve,ref_catalog,ref_revision,index_name,quote.code,quote.quantity_code,
  from_date,to_date,averaging,calendar_name,key,'side_observation_schedule_v1',publication,effective);
 INSERT INTO trading.swap_observation_quantities
 SELECT catalog,retained,x.ordinal,definition,curve,x."contractId",ref_catalog,ref_revision,quote.quantity_code,x.quantity::numeric,publication,effective
 FROM jsonb_to_recordset(selected) x(ordinal integer,"contractId" uuid,quantity text) WHERE x.quantity IS NOT NULL;
 RETURN retained;
END$$;

CREATE FUNCTION security.save_projected_swap_core(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb,input_pins uuid[],custom_projection jsonb,observation_schedule jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE observation_rows jsonb;observation_binding uuid;custom_binding uuid;fixing timestamptz;fixing_evidence jsonb;fixed_rule uuid;float_rule uuid;c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;request_value jsonb;permission text;publication_number integer;
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
  averaging IS NULL OR averaging NOT IN('monthly_index_price','calendar_day_arithmetic','observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted') OR price IS NULL OR abs(price)>=1e24 OR scale(price)>18
 THEN RAISE EXCEPTION 'swap_input' USING ERRCODE='P0001'; END IF;
 IF (averaging IN('observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted')) IS DISTINCT FROM(observation_schedule IS NOT NULL) THEN RAISE EXCEPTION 'swap_averaging';END IF;
 IF observation_schedule IS NOT NULL AND custom_projection IS NOT NULL THEN RAISE EXCEPTION 'swap_custom_projection_averaging';END IF;
 custom_projection=security.validate_custom_projection(catalog,custom_projection);
 IF custom_projection IS NOT NULL AND((averaging='monthly_index_price' AND custom_projection->>'selection'<>'single_output_position_v1') OR(averaging='calendar_day_arithmetic' AND custom_projection->>'selection'<>'exact_delivery_interval_v1') OR EXISTS(
  SELECT 1 FROM security.custom_curve_native_sources(catalog,(custom_projection->>'curveId')::uuid,(custom_projection->>'version')::integer) s WHERE(s.source_catalog_id,s.source_revision_id) IS DISTINCT FROM(catalog,definition)))
 THEN RAISE EXCEPTION 'custom_projection' USING ERRCODE='P0001';END IF;
 request_value=jsonb_build_object('fixingConvention','published_index_observations_v1','trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,
  'curve',curve,'index',index_name,'fixedCurrency',fixed_currency,'floatingCurrency',float_currency,'fixedFrom',fixed_from,'fixedTo',fixed_to,'fixedPayment',fixed_payment AT TIME ZONE 'UTC',
  'floatingFrom',float_from,'floatingTo',float_to,'firstFixing',fixing AT TIME ZONE 'UTC','floatingPayment',float_payment AT TIME ZONE 'UTC','averaging',averaging,
  'price',price,'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation AT TIME ZONE 'UTC','executed',NULL,'effective',effective AT TIME ZONE 'UTC','reason',reason,'implementation',implementation);
 IF fixed_schedule IS NOT NULL OR float_schedule IS NOT NULL OR commodity IS NOT NULL THEN request_value=request_value||jsonb_build_object('fixedSchedule',fixed_schedule,'floatingSchedule',float_schedule,'commodity',commodity);END IF;
 request_value=request_value||jsonb_build_object('booking',booking,'bookingFactsPolicy','server_booking_utc_v1');
 IF trader IS NOT NULL OR trader_revision IS NOT NULL OR memo IS NOT NULL THEN request_value=request_value||jsonb_build_object('trader',trader,'traderRevision',trader_revision,'memo',memo);END IF;
 IF custom_projection IS NOT NULL THEN request_value=request_value||jsonb_build_object('customProjection',custom_projection);END IF;
 IF observation_schedule IS NOT NULL THEN request_value=request_value||jsonb_build_object('observationSchedule',observation_schedule);END IF;
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR (prior.request-'firstFixing'-'fixingInputs')<>(request_value-'firstFixing') THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
  PERFORM security.validate_custom_projection(catalog,custom_projection);
 IF observation_schedule IS NOT NULL THEN PERFORM security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,curve,index_name,float_from,float_to,averaging,observation_schedule);END IF;
 RETURN security.swap_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE catalog_id=catalog AND operation_id=operation)
 THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501'; END IF;
 IF trade IS NOT NULL THEN
  IF EXISTS(SELECT 1 FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) WHERE r.catalog_id=catalog AND r.trade_id=trade AND i.first_floating_side_id IS NOT NULL) THEN RAISE EXCEPTION 'swap_amendment';END IF;
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
 IF observation_schedule IS NULL THEN
  fixing_evidence=security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,curve,index_name,float_from,float_to,averaging,valuation,float_payment,input_pins);
 ELSE
  observation_rows=security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,curve,index_name,float_from,float_to,averaging,observation_schedule);
  fixing_evidence=security.swap_observation_fixing_evidence(catalog,definition,ref_catalog,ref_revision,curve,index_name,valuation,float_payment,input_pins,observation_rows);
 END IF;
 fixing=(fixing_evidence->>'firstFixing')::timestamp AT TIME ZONE 'UTC';
 request_value=request_value||jsonb_build_object('firstFixing',fixing AT TIME ZONE 'UTC','fixingInputs',fixing_evidence->'inputs');
 IF float_from IS NULL OR float_to IS NULL OR extract(day FROM float_from)<>1 OR float_to IS DISTINCT FROM(float_from+INTERVAL '1 month')::date THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 IF observation_schedule IS NULL THEN
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
 SELECT jsonb_agg(jsonb_build_object('ordinal',r.ordinal,'contractId',r.id,'weight',1) ORDER BY r.ordinal) INTO observation_rows FROM(
  SELECT (row_number() OVER(ORDER BY k1.delivery_from,k1.id)-1)::integer AS ordinal,k1.id
  FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
  WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND
   (averaging='monthly_index_price' AND k1.id=k.id OR averaging='calendar_day_arithmetic' AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)
 )r;
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
 IF observation_schedule IS NOT NULL THEN
  observation_binding=security.retain_swap_observation_schedule(catalog,definition,ref_catalog,ref_revision,curve,index_name,q,float_from,float_to,averaging,observation_schedule,observation_rows,publication_number,from_at);
 END IF;
 SELECT id INTO floating_side FROM trading.swap_floating_sides s WHERE s.catalog_id=catalog AND s.definition_revision_id=definition AND s.curve_code=curve AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.quote_code=q.code AND s.delivery_from=float_from AND s.delivery_to=float_to AND s.index_code=index_name AND s.first_fixing_at=fixing AND s.implementation='index_observation_fixings_v1' AND s.payment_at=float_payment AND s.averaging=save_projected_swap_core.averaging AND s.payment_rule_id=float_rule AND s.custom_projection_id IS NOT DISTINCT FROM custom_binding AND s.spread=0 AND s.observation_schedule_id IS NOT DISTINCT FROM observation_binding;
 IF floating_side IS NOT NULL AND EXISTS(
  SELECT 1 FROM(SELECT ordinal,definition_revision_id,curve_code,contract_id,weight FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(catalog,floating_side)) old
  FULL JOIN(SELECT x.ordinal,definition AS definition_revision_id,curve AS curve_code,x."contractId" AS contract_id,x.weight
    FROM jsonb_to_recordset(observation_rows) x(ordinal integer,"contractId" uuid,weight numeric)) wanted USING(ordinal)
  WHERE(old.definition_revision_id,old.curve_code,old.contract_id,old.weight) IS DISTINCT FROM(wanted.definition_revision_id,wanted.curve_code,wanted.contract_id,wanted.weight)
 ) THEN RAISE EXCEPTION 'swap_projection';END IF;
 IF floating_side IS NULL THEN
  floating_side=gen_random_uuid();
  INSERT INTO trading.swap_floating_sides(catalog_id,id,definition_revision_id,curve_code,contract_id,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,publication_order,effective_at,payment_rule_id,custom_projection_id,spread,observation_schedule_id) VALUES(catalog,floating_side,definition,curve,k.id,ref_catalog,ref_revision,index_name,q.code,float_from,float_to,fixing,float_payment,averaging,'index_observation_fixings_v1',publication_number,from_at,float_rule,custom_binding,0,observation_binding);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,floating_side,x.ordinal,definition,curve,x."contractId",x.weight,publication_number,from_at
   FROM jsonb_to_recordset(observation_rows) x(ordinal integer,"contractId" uuid,weight numeric);
 END IF;
 SELECT instrument_id INTO instrument FROM trading.swap_instruments WHERE catalog_id=catalog AND fixed_side_id=fixed_side AND floating_side_id=floating_side AND fixed_coefficient=coefficient AND swap_instruments.discounting=save_projected_swap_core.discounting;
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
 IF observation_schedule IS NOT NULL THEN
  PERFORM security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,curve,index_name,float_from,float_to,averaging,observation_schedule);
  PERFORM security.swap_observation_fixing_evidence(catalog,definition,ref_catalog,ref_revision,curve,index_name,valuation,float_payment,input_pins,observation_rows);
 END IF;
 RETURN security.swap_operation_status(catalog,operation);
END $$;

CREATE OR REPLACE FUNCTION security.save_projected_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb,input_pins uuid[],custom_projection jsonb) RETURNS jsonb LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.save_projected_swap_core(catalog,operation,trade,expected,book,definition,ref_catalog,ref_revision,curve,index_name,fixed_currency,float_currency,fixed_from,fixed_to,fixed_payment,float_from,float_to,float_payment,averaging,price,quantity,coefficient,discounting,valuation,executed,effective,reason,implementation,fixed_schedule,float_schedule,commodity,trader,trader_revision,memo,booking,input_pins,custom_projection,NULL);
$$;
CREATE FUNCTION security.save_observation_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb,input_pins uuid[],custom_projection jsonb,observation_schedule jsonb) RETURNS jsonb LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.save_projected_swap_core(catalog,operation,trade,expected,book,definition,ref_catalog,ref_revision,curve,index_name,fixed_currency,float_currency,fixed_from,fixed_to,fixed_payment,float_from,float_to,float_payment,averaging,price,quantity,coefficient,discounting,valuation,executed,effective,reason,implementation,fixed_schedule,float_schedule,commodity,trader,trader_revision,memo,booking,input_pins,custom_projection,observation_schedule);
$$;

-- Both floating memberships use this exact six-mode storage admission.
CREATE FUNCTION security.swap_observation_sampling(frequency text,method text) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN frequency='monthly' AND method='none' THEN 'monthly_index_price'
  WHEN frequency='daily' AND method='arithmetic' THEN 'calendar_day_arithmetic'
  WHEN frequency='daily' AND method IN('observation_arithmetic','calendar_days_previous','calendar_days_next','quantity_weighted') THEN method END;
$$;

CREATE OR REPLACE FUNCTION security.retain_floating_swap_side(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz,input_pins uuid[])
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cv market_data.curves;q ref_data.quote_specifications;target ref_data.settlement_targets;k market_data.contracts;
 side uuid;rule uuid;starts date;ends date;fixing timestamptz;payment timestamptz;sampling text;spread_value numeric;fixing_evidence jsonb;custom_projection jsonb;custom_binding uuid;observation_schedule jsonb;observation_rows jsonb;observation_binding uuid;
BEGIN
 IF body IS NULL OR jsonb_typeof(body)<>'object' OR body->>'paymentAt' IS NOT NULL OR body->>'averaging' IS NOT NULL OR body->'paymentSchedule' IS NULL OR body->>'spread' IS NULL OR body->>'firstFixingAt' IS NOT NULL
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('curve','index','currency','deliveryFrom','deliveryTo','firstFixingAt','paymentAt','averaging','paymentSchedule','resetFrequency','averagingMethod','spread','customProjection','observationSchedule')) THEN RAISE EXCEPTION 'swap_input';END IF;
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
 IF cv.commodity_code IS DISTINCT FROM commodity OR q.kind IS DISTINCT FROM 'outright' OR target.kind IS DISTINCT FROM 'index' OR target.index_a_code IS DISTINCT FROM body->>'index' OR q.currency_code IS DISTINCT FROM body->>'currency' OR
  NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,body->>'index') AND(i.currency_code,i.commodity_code,i.location_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,cv.location_code,u.unit_code,q.multiplier)) THEN RAISE EXCEPTION 'swap_definition';END IF;
 IF observation_schedule IS NULL THEN
  fixing_evidence=security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,cv.code,body->>'index',starts,ends,sampling,valuation,payment,input_pins);
 ELSE
  observation_rows=security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,cv.code,body->>'index',starts,ends,sampling,observation_schedule);
  fixing_evidence=security.swap_observation_fixing_evidence(catalog,definition,ref_catalog,ref_revision,cv.code,body->>'index',valuation,payment,input_pins,observation_rows);
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
  observation_binding=security.retain_swap_observation_schedule(catalog,definition,ref_catalog,ref_revision,cv.code,body->>'index',q,starts,ends,sampling,observation_schedule,observation_rows,publication_number,from_at);
 END IF;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 SELECT id INTO side FROM trading.swap_floating_sides s WHERE s.catalog_id=catalog AND s.definition_revision_id=definition AND s.curve_code=cv.code AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.index_code=body->>'index' AND s.quote_code=q.code AND s.delivery_from=starts AND s.delivery_to=ends AND s.first_fixing_at=fixing AND s.payment_at=payment AND s.averaging=sampling AND s.payment_rule_id=rule AND s.spread=spread_value AND s.implementation='index_observation_fixings_v1' AND s.custom_projection_id IS NOT DISTINCT FROM custom_binding AND s.observation_schedule_id IS NOT DISTINCT FROM observation_binding;
 IF side IS NULL THEN
  side=gen_random_uuid();
  INSERT INTO trading.swap_floating_sides(catalog_id,id,definition_revision_id,curve_code,contract_id,reference_catalog_id,reference_revision_id,index_code,quote_code,delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,publication_order,effective_at,payment_rule_id,custom_projection_id,spread,observation_schedule_id)
   VALUES(catalog,side,definition,cv.code,k.id,ref_catalog,ref_revision,body->>'index',q.code,starts,ends,fixing,payment,sampling,'index_observation_fixings_v1',publication_number,from_at,rule,custom_binding,spread_value,observation_binding);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,side,x.ordinal,definition,cv.code,x."contractId",x.weight,publication_number,from_at
   FROM jsonb_to_recordset(observation_rows) x(ordinal integer,"contractId" uuid,weight numeric);
 END IF;
 IF EXISTS(SELECT 1 FROM(SELECT p.ordinal,p.definition_revision_id,p.curve_code,p.contract_id,p.weight FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(catalog,side)) retained FULL JOIN
  (SELECT x.ordinal,definition AS definition_revision_id,cv.code AS curve_code,x."contractId" AS contract_id,x.weight
   FROM jsonb_to_recordset(observation_rows) x(ordinal integer,"contractId" uuid,weight numeric)) expected USING(ordinal)
  WHERE retained.ordinal IS NULL OR expected.ordinal IS NULL OR(retained.definition_revision_id,retained.curve_code,retained.contract_id,retained.weight) IS DISTINCT FROM(expected.definition_revision_id,expected.curve_code,expected.contract_id,expected.weight)) THEN RAISE EXCEPTION 'swap_projection';END IF;
 RETURN side;
END $$;

-- Retained old schedules keep their original evidence shape. New schedules use
-- their exact stored projection rows, never a reconstructed daily denominator.
CREATE FUNCTION security.retained_swap_fixing_evidence(catalog uuid,selected_side uuid,valuation timestamptz,input_pins uuid[]) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE side trading.swap_floating_sides;selected jsonb;
BEGIN
 SELECT * INTO STRICT side FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(catalog,selected_side);
 IF side.observation_schedule_id IS NULL THEN
  RETURN security.swap_fixing_evidence(catalog,side.definition_revision_id,side.reference_catalog_id,side.reference_revision_id,
   side.curve_code,side.index_code,side.delivery_from,side.delivery_to,side.averaging,valuation,side.payment_at,input_pins);
 END IF;
 SELECT jsonb_agg(jsonb_build_object('contractId',p.contract_id) ORDER BY p.ordinal) INTO selected
 FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(catalog,selected_side);
 RETURN security.swap_observation_fixing_evidence(catalog,side.definition_revision_id,side.reference_catalog_id,side.reference_revision_id,
  side.curve_code,side.index_code,valuation,side.payment_at,input_pins,selected);
END$$;

-- Preserve the composed command identity, ownership and publication fences.
-- Only its evidence adapter and new-rule authority checks change.
DO $$DECLARE signature regprocedure='security.save_composed_swap(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,jsonb,jsonb,jsonb,numeric,numeric,boolean,timestamptz,timestamptz,text,text,text,jsonb,uuid[])'::regprocedure;
 definition text;before text;checks text;
BEGIN
 definition=pg_get_functiondef(signature);
 before='security.swap_fixing_evidence(catalog,definition,ref_catalog,ref_revision,s.curve_code,s.index_code,s.delivery_from,s.delivery_to,s.averaging,valuation,s.payment_at,input_pins)';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'Unexpected composed fixing evidence';END IF;
 definition=replace(definition,before,'security.retained_swap_fixing_evidence(catalog,s.id,valuation,input_pins)');
 before='PERFORM security.validate_custom_projection(catalog,float_body->''customProjection'');';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>2 THEN RAISE EXCEPTION 'Unexpected composed source checks';END IF;
 checks=$check$
 IF NULLIF(first_body->'observationSchedule','null'::jsonb) IS NOT NULL THEN
  PERFORM security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,first_body->>'curve',first_body->>'index',
   (first_body->>'deliveryFrom')::date,(first_body->>'deliveryTo')::date,security.swap_observation_sampling(first_body->>'resetFrequency',first_body->>'averagingMethod'),first_body->'observationSchedule');
 END IF;
 IF NULLIF(float_body->'observationSchedule','null'::jsonb) IS NOT NULL THEN
  PERFORM security.swap_observation_rows(catalog,definition,ref_catalog,ref_revision,float_body->>'curve',float_body->>'index',
   (float_body->>'deliveryFrom')::date,(float_body->>'deliveryTo')::date,security.swap_observation_sampling(float_body->>'resetFrequency',float_body->>'averagingMethod'),float_body->'observationSchedule');
 END IF;
$check$;
 EXECUTE replace(definition,before,before||checks);
END$$;

DO $$DECLARE fn record;BEGIN
 FOR fn IN SELECT p.oid::regprocedure AS signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('swap_observation_rows','swap_observation_fixing_evidence','retain_swap_observation_schedule','save_projected_swap_core','save_projected_swap_with_parties','save_observation_swap_with_parties','swap_observation_sampling','retain_floating_swap_side','retained_swap_fixing_evidence') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',fn.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',fn.signature);
  IF fn.proname IN('save_projected_swap_with_parties','save_observation_swap_with_parties') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',fn.signature);END IF;
 END LOOP;
END$$;

COMMIT;
