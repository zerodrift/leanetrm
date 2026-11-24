-- Independent fixed payments retain a clock witness, exact quantity and their own days.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE helper record;p pg_proc;
BEGIN
 FOR helper IN SELECT * FROM(VALUES
 ('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','bd09888b447427ac691fb0b308c157bbb14b83723e702b2567551fcbd4f07f13',false),
 ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric)','2450c03277970350e0ec74435c8cad1e850cdeff892712fb6e1c5b836afcff7a',false),
 ('trading.require_gas_cash_payment()','fcf355558fb8b17aba1d4c74da9bd2bce8644bff1a211eb5433d3d9bb6f3feb4',false),
 ('security.record_gas_cash_payment(uuid,jsonb,timestamptz)','5bc64775e4168be69c916e6166fa9cdaefd6b45cd922cad0045feebcaf16030a',true)
 )v(signature,digest,runtime_grant) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(helper.signature);
  IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>helper.digest OR
   p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM (CASE WHEN helper.runtime_grant THEN
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' ELSE '{openetrm_security_owner=X/openetrm_security_owner}' END) OR
   p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_charge_upgrade: unexpected helper %',helper.signature;END IF;
 END LOOP;
END$guard$;

-- Staging resolves calendar pins by command slot before the booking writer runs.
DO $calendars$
DECLARE p pg_proc;definition text;marker text=$b$  ELSE selected=command->'cashSides'->(slot_number-jsonb_array_length(command->'physicalSides'))->'calendarPolicy';END IF;$b$;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='trading.request_side_calendar_policy(jsonb,integer)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'cdd774e0fb02cbc9917fb35389c00b65b6e874c550078e99d112ec670e41695a' OR
  p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}' OR
  p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'i' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_charge_upgrade: unexpected helper trading.request_side_calendar_policy(jsonb,integer)';END IF;
 IF (length(definition)-length(replace(definition,marker,'')))/length(marker)<>1 THEN RAISE EXCEPTION 'gas_charge_upgrade: calendar patch mismatch';END IF;
 EXECUTE replace(definition,marker,$a$  ELSIF command ? 'chargeSides' AND slot_number>=jsonb_array_length(command->'physicalSides')+jsonb_array_length(command->'cashSides') THEN
   selected=command->'chargeSides'->(slot_number-jsonb_array_length(command->'physicalSides')-jsonb_array_length(command->'cashSides'))->'calendarPolicy';
$a$||marker);
END$calendars$;

ALTER TABLE trading.gas_component_terms
 ADD COLUMN charge_basis text, ADD COLUMN charge_quantity_n numeric, ADD COLUMN charge_quantity_d numeric,
 ADD COLUMN clock_slot integer, ADD COLUMN payment_day integer, ADD COLUMN payment_convention text,
 ADD FOREIGN KEY(catalog_id,instrument_id,clock_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 DROP CONSTRAINT gas_component_terms_role_check,
 ADD CONSTRAINT gas_component_terms_role_check CHECK(role IN('physical','consideration','fee','indexed_consideration','charge'));
-- Preserve the checks for all preceding roles. Owning payments have neither a source nor a share.
DO $constraints$
DECLARE name text;definition text;
BEGIN
 FOREACH name IN ARRAY ARRAY['gas_component_terms_check2','gas_payment_entry_terms'] LOOP
  SELECT pg_get_constraintdef(oid) INTO STRICT definition FROM pg_constraint WHERE conrelid='trading.gas_component_terms'::regclass AND conname=name;
  EXECUTE format('ALTER TABLE trading.gas_component_terms DROP CONSTRAINT %I',name);
  EXECUTE format('ALTER TABLE trading.gas_component_terms ADD CONSTRAINT %I %s',name,replace(definition,'CHECK (','CHECK (role=''charge'' OR '));
 END LOOP;
END$constraints$;
ALTER TABLE trading.gas_component_terms ADD CONSTRAINT gas_charge_quantity_terms CHECK((
 CASE WHEN role='charge' THEN
  num_nonnulls(source_slot,stage,formula,fraction,pipeline_code,zone_code,point_code,product_code,quantity_basis,independent_quantity,
    share_n,share_d,part_kind,part_lower_n,part_lower_d,part_upper_n,part_upper_d,tariff_code)=0 AND
  charge_quantity_n>0 AND charge_quantity_d>0 AND charge_quantity_n<1e120 AND charge_quantity_d<1e120 AND
  scale(charge_quantity_n)=0 AND scale(charge_quantity_d)=0 AND gcd(charge_quantity_n,charge_quantity_d)=1 AND
  clock_slot>=0 AND clock_slot<slot AND isfinite(delivery_from) AND isfinite(delivery_to) AND delivery_from<delivery_to AND price IS NOT NULL AND
  CASE charge_basis WHEN 'lump_sum' THEN charge_quantity_n=1 AND charge_quantity_d=1 AND delivery_to=delivery_from+1
   AND isfinite(payment_at) AND payment_day IS NULL AND payment_convention IS NULL
   WHEN 'capacity' THEN price>0 AND payment_at IS NULL AND payment_day BETWEEN 1 AND 28 AND payment_convention='next_month_day_utc_v1' ELSE false END
 ELSE num_nonnulls(charge_basis,charge_quantity_n,charge_quantity_d,clock_slot,payment_day,payment_convention)=0 END) IS TRUE);

-- The immutable clock witness must itself be physical and carry the charge's
-- exact assignment, even for an insert outside the booking function.
CREATE FUNCTION trading.require_gas_charge_clock() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT FROM trading.gas_component_terms p
  WHERE(p.catalog_id,p.instrument_id,p.slot,p.role,p.definition_revision_id,p.assignment_code)=
   (NEW.catalog_id,NEW.instrument_id,NEW.clock_slot,'physical',NEW.definition_revision_id,NEW.assignment_code))
 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION trading.require_gas_charge_clock() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_charge_clock() FROM PUBLIC;
CREATE TRIGGER gas_charge_clock BEFORE INSERT ON trading.gas_component_terms
 FOR EACH ROW WHEN(NEW.role='charge') EXECUTE FUNCTION trading.require_gas_charge_clock();

-- All callers use the same graph proof. Only the charge writer supplies the independently
-- derived monthly boundaries. A flat price literal is shared by every payment occurrence.
DO $shape$
DECLARE signature text='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric';
 original text=pg_get_functiondef((signature||')')::regprocedure);definition text=original;c record;
BEGIN
 IF to_regprocedure(signature||',date[],timestamptz[])') IS NOT NULL THEN RAISE EXCEPTION 'gas_charge_upgrade: unexpected shape';END IF;
 FOR c IN SELECT * FROM(VALUES
 ($b$unit_denominator numeric)$b$,$a$unit_denominator numeric, charge_ends date[], charge_due timestamptz[])$a$),
 ($b$BEGIN
 IF unit_denominator$b$,$a$payments integer=coalesce(cardinality(charge_due),1);occ integer;begins date;ends date;due timestamptz;
BEGIN
 IF charge_ends IS NOT NULL AND (slot<>1 OR payments NOT BETWEEN 1 AND 1200 OR cardinality(charge_ends)<>payments) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF unit_denominator$a$),
 ($b$OR days>1200 THEN RAISE EXCEPTION 'gas_schedule_geometry';END IF;PERFORM trading.require_gas_completion(catalog,schedule,slot=0,first_day,last_day,assignment,delivery);$b$,
  $a$OR charge_ends IS NULL AND days>1200 THEN RAISE EXCEPTION 'gas_schedule_geometry';END IF;
 IF charge_ends IS NULL THEN PERFORM trading.require_gas_completion(catalog,schedule,slot=0,first_day,last_day,assignment,delivery);END IF;$a$),
 ($b$prefix='payment/0/';grid=NULL;
   IF row.value_at IS DISTINCT FROM payment OR row.delivery_from IS DISTINCT FROM first_day OR row.delivery_to IS DISTINCT FROM last_day$b$,
  $a$occ=0;begins=first_day;ends=last_day;due=payment;
   IF charge_ends IS NOT NULL THEN
    occ=row.payment_ordinal;
    IF occ IS NULL OR occ<0 OR occ>=payments OR split_part(row.local_key,'/',2) IS DISTINCT FROM occ::text THEN RAISE EXCEPTION 'gas_schedule_cash';END IF;
    begins=CASE WHEN occ=0 THEN first_day ELSE charge_ends[occ] END;ends=charge_ends[occ+1];due=charge_due[occ+1];unit_total=ends-begins;
    IF row.operation='price_application' AND (row.observation_from,row.observation_to) IS DISTINCT FROM (begins,ends) THEN RAISE EXCEPTION 'gas_schedule_application';END IF;
   END IF;
   IF row.value_at IS DISTINCT FROM due OR row.delivery_from IS DISTINCT FROM begins OR row.delivery_to IS DISTINCT FROM ends$a$),
 ($b$SELECT 'payment/0/'||v.k,v.op FROM(VALUES$b$,$a$SELECT 'payment/'||n||'/'||v.k,v.op FROM generate_series(0,payments-1) n CROSS JOIN(VALUES$a$),
 ($b$v(k,op) WHERE slot<>0)$b$,$a$v(k,op) WHERE slot<>0 AND(n=0 OR v.op<>'constant_price'))$a$),
 ($b$SELECT 'payment/0/'||v.target,v.port,v.ord,'payment/0/'||v.source FROM(VALUES$b$,
  $a$SELECT 'payment/'||n||'/'||v.target,v.port,v.ord,'payment/'||(CASE v.source WHEN 'observation/0' THEN 0 ELSE n END)||'/'||v.source
 FROM generate_series(0,payments-1) n CROSS JOIN(VALUES$a$),
 ($b$SELECT 'side/pv','input',0,'payment/0/value' WHERE slot<>0$b$,$a$SELECT 'side/pv','input',n,'payment/'||n||'/value' FROM generate_series(0,payments-1) n WHERE slot<>0$a$),
 ($b$SELECT 'payment/0/'||v.name,'payment/0/'||v.k,v.measure FROM(VALUES$b$,
  $a$SELECT 'payment/'||n||'/'||v.name,'payment/'||n||'/'||v.k,v.measure FROM generate_series(0,payments-1) n CROSS JOIN(VALUES$a$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_charge_upgrade: shape patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
 EXECUTE 'ALTER FUNCTION '||signature||',date[],timestamptz[]) OWNER TO openetrm_security_owner';
 EXECUTE 'REVOKE ALL ON FUNCTION '||signature||',date[],timestamptz[]) FROM PUBLIC';
 EXECUTE split_part(original,'AS $function$',1)||$wrapper$AS $function$
BEGIN
 PERFORM trading.require_gas_program_shape(catalog,schedule,slot,first_day,last_day,payment,unit_total,literal,assignment,delivery,start_at,forecast,unit_denominator,NULL,NULL);
END$function$ $wrapper$;
END$shape$;

CREATE FUNCTION trading.save_gas_charge(catalog uuid,instrument uuid,operation uuid,entry_slot integer,base numeric,item jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE clock_term trading.gas_component_terms;assignment market_data.gas_valuation_assignments;delivery ref_data.gas_delivery_conventions;
 quote ref_data.quote_specifications;clock ref_data.clock_rules;offset_interval interval;basis text=item->>'basis';sign_number integer;
 schedule uuid;side uuid;identity jsonb;expected_clock jsonb;price_unit jsonb;period jsonb;
 first_day date;last_day date;begins date;ends date;month_end date;completion timestamptz;payment timestamptz;due timestamptz;
 pay_day integer;payments timestamptz[]='{}';boundaries date[]='{}';daily numeric;nominal numeric;scaled numeric;literal numeric;
 qn numeric;qd numeric;rn numeric;rd numeric;g numeric;
BEGIN
 IF jsonb_typeof(item) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_object_keys(item) k WHERE k NOT IN('code','basis','direction','price','dailyQuantity','from','to','paymentAt','paymentDay','clockSide','calendarPolicy'))
  OR NOT(item ?& ARRAY['code','basis','direction','price','clockSide','calendarPolicy'])
  OR jsonb_typeof(item->'price') IS DISTINCT FROM 'string' OR item->>'price' !~ '^-?(0|[1-9][0-9]*)([.][0-9]{1,18})?$'
 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 sign_number=CASE item->>'direction' WHEN 'receive' THEN 1 WHEN 'pay' THEN -1 END;
 IF sign_number IS NULL THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 SELECT * INTO clock_term FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,code,role)=(catalog,instrument,item->>'clockSide','physical');
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 SELECT * INTO STRICT assignment FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,clock_term.definition_revision_id,clock_term.assignment_code);
 SELECT * INTO STRICT delivery FROM security.gas_setup_delivery(assignment);
 SELECT * INTO STRICT clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,delivery.clock_code);
 IF clock.kind<>'fixed_offset' OR clock.fixed_offset_seconds%60<>0 OR abs(clock.fixed_offset_seconds)>50400 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 offset_interval=make_interval(mins=>delivery.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
 SELECT q.* INTO STRICT quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
 WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,assignment.revision_id,assignment.curve_code);
 nominal=(item->>'price')::numeric;
 scaled=div(nominal*1e18,quote.multiplier);
 IF abs(nominal)>=1e12 OR abs(scaled)>=1e42 OR scaled*quote.multiplier<>nominal*1e18 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 literal=trim_scale(scaled*1e-18);
 CASE basis WHEN 'lump_sum' THEN
  IF num_nonnulls(item->>'dailyQuantity',item->>'from',item->>'to',item->>'paymentDay')<>0 OR
   (item->>'paymentAt' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}0?)?(Z|[+]00:00)$') IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  payment=(item->>'paymentAt')::timestamptz;
  last_day=((payment AT TIME ZONE 'UTC')::date::timestamp-offset_interval)::date;first_day=last_day-1;daily=1;
  IF first_day<date '0001-01-01' OR payment>=timestamptz '9999-12-31 UTC' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 WHEN 'capacity' THEN
  IF item->>'paymentAt' IS NOT NULL OR (jsonb_typeof(item->'dailyQuantity')='string' AND
   item->>'dailyQuantity' ~ '^(0|[1-9][0-9]*)([.][0-9]{1,18})?$' AND jsonb_typeof(item->'from')='string' AND
   item->>'from' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' AND jsonb_typeof(item->'to')='string' AND item->>'to' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
   AND jsonb_typeof(item->'paymentDay')='number' AND item->>'paymentDay' ~ '^([1-9]|1[0-9]|2[0-8])$') IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  first_day=(item->>'from')::date;last_day=(item->>'to')::date;pay_day=(item->>'paymentDay')::integer;daily=(item->>'dailyQuantity')::numeric;
  IF first_day>=last_day OR last_day>date '9999-12-01' OR nominal<=0 OR daily<=0 OR daily>=1e24 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 ELSE RAISE EXCEPTION 'gas_composition_input';
 END CASE;
 SELECT i.schedule_id,s.id,s.economic_identity::jsonb INTO STRICT schedule,side,identity FROM trading.side_schedule_request_items i
  JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id) JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE(i.catalog_id,i.request_id,i.slot)=(catalog,operation,entry_slot);
 SELECT s.economic_identity::jsonb#>'{Quantities,Clock}' INTO STRICT expected_clock FROM trading.gas_memberships m JOIN trading.sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
  WHERE(m.catalog_id,m.instrument_id,m.slot)=(catalog,instrument,clock_term.slot);
 price_unit=jsonb_build_array('price_unit',trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.currency_code),
  trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.quantity_code),trim_scale(quote.multiplier)::text);
 IF identity->'IntervalDefinition' IS DISTINCT FROM 'null'::jsonb OR identity->'PowerQuantity' IS DISTINCT FROM 'null'::jsonb OR
  identity->>'contract' IS DISTINCT FROM 'FixedSide_v5' OR identity->'GasDeliveryClock' IS DISTINCT FROM expected_clock
  OR identity->'DateQuoteKey' IS DISTINCT FROM trading.gas_identity_reference(quote.catalog_id,quote.revision_id,quote.code)
  OR identity->'QuantityBasis' IS DISTINCT FROM price_unit->2
  OR identity->'Price' IS DISTINCT FROM jsonb_build_object('contract','ppa_flat_price_v1','Price',jsonb_build_array('price',literal::text,price_unit))
  OR jsonb_typeof(identity->'Periods') IS DISTINCT FROM 'array'
  OR item->'calendarPolicy' IS DISTINCT FROM security.side_calendar_policy_document(catalog,side)
  OR (item#>>'{calendarPolicy,delivery,applicable}',item#>>'{calendarPolicy,observation,applicable}',item#>>'{calendarPolicy,payment,applicable}') IS DISTINCT FROM ('true','false','true')
 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 begins=first_day;
 WHILE begins<last_day LOOP
  month_end=(date_trunc('month',begins::timestamp)+interval '1 month')::date;
  ends=CASE basis WHEN 'capacity' THEN least(last_day,month_end) ELSE last_day END;
  due=CASE basis WHEN 'capacity' THEN (month_end+pay_day-1)::timestamp AT TIME ZONE 'UTC' ELSE payment END;
  completion=(ends::timestamp AT TIME ZONE 'UTC')+offset_interval;
  period=identity->'Periods'->cardinality(payments);
  IF (due AT TIME ZONE 'UTC')::date<(completion AT TIME ZONE 'UTC')::date OR
   period->>'contract' IS DISTINCT FROM 'fixed_side_period_v2' OR period->'PaymentSchedule' IS DISTINCT FROM 'null'::jsonb OR
   period->'Allocation' IS DISTINCT FROM jsonb_build_array('exact_ratio',(ends-begins)::text,'1') OR
   ((period->>'DeliveryFrom')::date,(period->>'DeliveryTo')::date,(period->>'PaymentAt')::timestamptz,(period->>'DeliveryCompletionAt')::timestamptz)
    IS DISTINCT FROM(begins,ends,due,completion)
  THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  boundaries=array_append(boundaries,ends);payments=array_append(payments,due);begins=ends;
 END LOOP;
 IF jsonb_array_length(identity->'Periods')<>cardinality(payments) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 PERFORM trading.require_gas_program_shape(catalog,schedule,1,first_day,last_day,payment,NULL,literal,assignment,delivery,NULL,NULL,1,boundaries,payments);
 qn=daily*1e18;qd=1e18;g=gcd(qn,qd);qn=trim_scale(div(qn,g));qd=trim_scale(div(qd,g));
 rn=daily*1e18;rd=base*1e18;g=gcd(rn,rd);rn=trim_scale(div(rn,g));rd=trim_scale(div(rd,g));
 INSERT INTO trading.gas_component_terms(catalog_id,instrument_id,slot,code,role,direction,ratio_n,ratio_d,receipt_n,receipt_d,clock_slot,assignment_code,
  definition_revision_id,charge_basis,charge_quantity_n,charge_quantity_d,price,delivery_from,delivery_to,payment_at,payment_day,payment_convention)
 VALUES(catalog,instrument,entry_slot,item->>'code','charge',sign_number,rn,rd,rn,rd,clock_term.slot,assignment.code,assignment.revision_id,
  basis,qn,qd,nominal,first_day,last_day,payment,pay_day,CASE WHEN basis='capacity' THEN 'next_month_day_utc_v1' END);
EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range THEN RAISE EXCEPTION 'gas_composition_input';
END$$;
ALTER FUNCTION trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb) FROM PUBLIC;

DO $write$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($b$physical jsonb;cash jsonb;$b$,$a$physical jsonb;cash jsonb;charges jsonb=coalesce(nullif(body->'chargeSides','null'),'[]');$a$),
 ($b$'amendmentPolicy','exerciseRights'))$b$,$a$'amendmentPolicy','exerciseRights','chargeSides'))$a$),
 ($b$IF jsonb_array_length(physical) NOT BETWEEN$b$,$a$IF jsonb_typeof(charges) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF jsonb_array_length(physical) NOT BETWEEN$a$),
 ($b$jsonb_array_length(cash)>7 OR jsonb_array_length(physical)+jsonb_array_length(cash)>8$b$,
  $a$jsonb_array_length(cash)+jsonb_array_length(charges)>7 OR jsonb_array_length(physical)+jsonb_array_length(cash)+jsonb_array_length(charges)>8$a$),
 ($b$UNION ALL SELECT value FROM jsonb_array_elements(cash)) r;$b$,
  $a$UNION ALL SELECT value FROM jsonb_array_elements(cash) UNION ALL SELECT value FROM jsonb_array_elements(charges)) r;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(charges) x WHERE (jsonb_typeof(x->'code')='string' AND x->>'code' ~ '^[a-z][a-z0-9_.-]{0,63}$') IS NOT TRUE
  OR (SELECT count(*) FROM unnest(codes) code WHERE code=x->>'code')<>1) THEN RAISE EXCEPTION 'gas_composition_input';END IF;$a$),
 ($b$<>jsonb_array_length(physical)+jsonb_array_length(cash) OR EXISTS$b$,$a$<>jsonb_array_length(physical)+jsonb_array_length(cash)+jsonb_array_length(charges) OR EXISTS$a$),
 ($b$ -- A common exact quote$b$,$a$ FOR item,position IN SELECT value,(ordinality-1+jsonb_array_length(physical)+jsonb_array_length(cash))::integer
  FROM jsonb_array_elements(charges) WITH ORDINALITY LOOP
  PERFORM trading.save_gas_charge(catalog,instrument,operation,position,base,item);
 END LOOP;
 IF charges<>'[]' AND EXISTS(SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(body->'exerciseRights')='array' THEN body->'exerciseRights' ELSE '[]' END) r JOIN trading.gas_component_terms m
  ON(m.catalog_id,m.instrument_id,m.role)=(catalog,instrument,'charge') AND r->'entries' ? m.code) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 -- A common exact quote$a$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_charge_upgrade: writer patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$write$;

DO $paid$
DECLARE definition text=pg_get_functiondef('security.record_gas_cash_payment(uuid,jsonb,timestamptz)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($b$m.role NOT IN('consideration','fee','indexed_consideration')$b$,$a$m.role NOT IN('consideration','fee','indexed_consideration','charge')$a$),
 ($b$ IF scale(total)>places OR sign(total)<>m.direction$b$,$a$ IF m.role='charge' THEN
  m.direction=m.direction*sign(m.price);
  IF m.direction=0 THEN RAISE EXCEPTION 'gas_payment_amount';END IF;
 END IF;
 IF scale(total)>places OR sign(total)<>m.direction$a$),
 ($b$  SELECT g.origin+$b$,$a$  IF m.role='charge' THEN
   -- The witness is an admitted fixed-offset gas grid. Its first boundary anchors
   -- every owned day, including dates outside the physical delivery term.
   SELECT g.origin+(guard.delivery_from+1-s.delivery_from)*interval '24 hours' INTO day_end
    FROM trading.gas_component_terms s JOIN trading.instrument_side_schedules h USING(catalog_id,instrument_id,slot)
    JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(h.catalog_id,h.schedule_id,0)
    WHERE(s.catalog_id,s.instrument_id,s.slot,s.role)=(catalog,head.instrument_id,m.clock_slot,'physical');
  ELSE
  SELECT g.origin+$a$),
 ($b$  IF guard.ordinal IS NULL OR day_end$b$,$a$  END IF;
  IF guard.ordinal IS NULL OR day_end$a$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_charge_upgrade: payment patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$paid$;

DO $allocation$
DECLARE definition text=pg_get_functiondef('trading.require_gas_cash_payment()'::regprocedure);
 marker text='SELECT m.direction INTO STRICT direction';
BEGIN
 IF (length(definition)-length(replace(definition,marker,'')))/length(marker)<>1 THEN RAISE EXCEPTION 'gas_charge_upgrade: allocation patch mismatch';END IF;
 EXECUTE replace(definition,marker,'SELECT CASE WHEN m.role=''charge'' THEN m.direction*sign(m.price) ELSE m.direction END INTO STRICT direction');
END$allocation$;
COMMIT;
