-- Normalize the commercial quantities and planning step and bind each deadline to its sealed gas-day geometry.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE p pg_proc;
BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)');
 IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>'1918e843d3f761ae794312ab0ce254df33ef0197aedd8004e1893454bc701098'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR (SELECT count(*) FROM pg_proc q WHERE(q.pronamespace,q.proname)=(p.pronamespace,p.proname))<>1
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_swing_upgrade: unexpected helper security.write_gas_composition(uuid,jsonb,timestamptz,boolean)';END IF;
END$guard$;

-- Publication follows native term admission within the same booking transaction.
CREATE UNIQUE INDEX gas_swing_published_slot ON trading.instrument_side_schedules(catalog_id,instrument_id,slot,schedule_id);
CREATE DOMAIN trading.gas_swing_ratio AS numeric[]
 CHECK(VALUE IS NULL OR (array_ndims(VALUE)=1 AND array_lower(VALUE,1)=1 AND cardinality(VALUE)=2
 AND VALUE[1] IS NOT NULL AND VALUE[2] IS NOT NULL AND VALUE[1]>=0 AND VALUE[2]>0
 AND scale(VALUE[1])=0 AND scale(VALUE[2])=0 AND gcd(VALUE[1],VALUE[2])=1));
ALTER DOMAIN trading.gas_swing_ratio OWNER TO openetrm_security_owner;
REVOKE ALL ON DOMAIN trading.gas_swing_ratio FROM PUBLIC;
CREATE FUNCTION trading.gas_swing_ratio_of(amount numeric,reference_quantity numeric) RETURNS trading.gas_swing_ratio
LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT ARRAY[div(amount,g),div(reference_quantity,g)]::trading.gas_swing_ratio FROM gcd(amount,reference_quantity) g
$$;
-- Both amounts have the same admitted eighteen-place wire precision.
CREATE FUNCTION trading.gas_swing_absolute(r trading.gas_swing_ratio,reference_quantity numeric) RETURNS numeric
LANGUAGE plpgsql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
DECLARE scaled numeric=r[1]*reference_quantity*1000000000000000000;
BEGIN
 IF mod(scaled,r[2])<>0 THEN RAISE EXCEPTION 'swing_right';END IF;
 RETURN trim_scale(div(scaled,r[2])*0.000000000000000001);
END$$;
ALTER FUNCTION trading.gas_swing_ratio_of(numeric,numeric) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.gas_swing_absolute(trading.gas_swing_ratio,numeric) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_swing_ratio_of(numeric,numeric),trading.gas_swing_absolute(trading.gas_swing_ratio,numeric) FROM PUBLIC;

CREATE TABLE trading.gas_swing_rights(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal=0),
 physical_slot integer NOT NULL,schedule_id uuid NOT NULL,
 holder text NOT NULL CHECK(holder IN('us','counterparty')),
 daily_minimum trading.gas_swing_ratio NOT NULL,daily_maximum trading.gas_swing_ratio NOT NULL,planning_step trading.gas_swing_ratio NOT NULL,
 limit_period text NOT NULL CHECK(limit_period IN ('term','delivery_month')),
 total_minimum trading.gas_swing_ratio,total_maximum trading.gas_swing_ratio,total_basis text,
 change_count integer CHECK(change_count>=0),count_rule text,
 quantity_policy text NOT NULL CHECK(quantity_policy IN('swing_elected_quantity_v1','swing_elected_delivery_quantity_v1')),
 plan_rule text NOT NULL CHECK(plan_rule='best_plan_on_forwards_v1'),
 CHECK((total_basis IS NULL)=(total_minimum IS NULL AND total_maximum IS NULL)
   AND total_basis IN('per_day_v1','per_period_v1') IS NOT FALSE),
 CHECK((change_count IS NULL)=(count_rule IS NULL) AND (count_rule='differs_from_base_v1') IS NOT FALSE),
 CHECK(daily_minimum[1]<=daily_minimum[2] AND daily_maximum[1]>=daily_maximum[2] AND planning_step[1]>0),
 time_rule text NOT NULL CHECK(time_rule='lead_before_first_gas_day_v1'),lead_hours integer NOT NULL CHECK(lead_hours>=0),
 PRIMARY KEY(catalog_id,instrument_id,ordinal),UNIQUE(catalog_id,instrument_id,ordinal,schedule_id),
 FOREIGN KEY(catalog_id,instrument_id,physical_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 CONSTRAINT swing_published_schedule FOREIGN KEY(catalog_id,instrument_id,physical_slot,schedule_id)
  REFERENCES trading.instrument_side_schedules(catalog_id,instrument_id,slot,schedule_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_swing_members(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL,slot integer NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,ordinal,slot),
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_swing_rights,
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot)
);
CREATE TABLE trading.gas_swing_days(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL,day date NOT NULL,
 schedule_id uuid NOT NULL,grid_ordinal integer NOT NULL,exercise_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,ordinal,day),
 UNIQUE(catalog_id,instrument_id,ordinal,grid_ordinal),
 FOREIGN KEY(catalog_id,instrument_id,ordinal,schedule_id) REFERENCES trading.gas_swing_rights(catalog_id,instrument_id,ordinal,schedule_id),
 FOREIGN KEY(catalog_id,schedule_id,grid_ordinal) REFERENCES trading.side_schedule_grids(catalog_id,schedule_id,ordinal)
);
CREATE TABLE trading.gas_swing_pricing(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL,
 model text NOT NULL CHECK(model='gas_swing_one_factor_lognormal_v1'),day_count text NOT NULL CHECK(day_count='act_365_fixed_instant_v1'),
 numerical_policy text NOT NULL CHECK(numerical_policy='swing_trinomial_6h_unit_reference_v2'),
 booking_work jsonb NOT NULL,
 marks_convention text NOT NULL CHECK(marks_convention='curve_marks_log_return_held_v1'),
 PRIMARY KEY(catalog_id,instrument_id,ordinal),
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_swing_rights
);
ALTER TABLE trading.gas_swing_rights ADD CONSTRAINT swing_required_pricing
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_swing_pricing DEFERRABLE INITIALLY DEFERRED;
DO $access$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['gas_swing_rights','gas_swing_members','gas_swing_days','gas_swing_pricing'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_term BEFORE INSERT OR UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_component()',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(EXISTS(
   SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(%I.catalog_id,%I.instrument_id)))',t,t,t);
  EXECUTE format('CREATE POLICY worker_service ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.gas_exercise_binding_visible(catalog_id,instrument_id))',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$access$;

CREATE FUNCTION trading.gas_swing_terms(catalog uuid,instrument uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog SET timezone='UTC' AS $$
 SELECT coalesce(jsonb_agg((to_jsonb(r)-ARRAY['catalog_id','instrument_id','schedule_id'])||
  (to_jsonb(p)-ARRAY['catalog_id','instrument_id','ordinal','booking_work'])||jsonb_build_object(
   'slots',(SELECT jsonb_agg(m.slot ORDER BY m.slot) FROM trading.gas_swing_members m
    WHERE(m.catalog_id,m.instrument_id,m.ordinal)=(catalog,instrument,r.ordinal)),
   'days',(SELECT jsonb_agg(jsonb_build_object('day',d.day,'exercise_at',d.exercise_at) ORDER BY d.day)
    FROM trading.gas_swing_days d WHERE(d.catalog_id,d.instrument_id,d.ordinal)=(catalog,instrument,r.ordinal))) ORDER BY r.ordinal),'[]')
 FROM trading.gas_swing_rights r JOIN trading.gas_swing_pricing p USING(catalog_id,instrument_id,ordinal)
 WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
$$;
ALTER FUNCTION trading.gas_swing_terms(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_swing_terms(uuid,uuid) FROM PUBLIC;

-- Bound the retained decision graph without constructing its states or prices.
CREATE FUNCTION trading.gas_swing_layer(depth integer,days integer,last_index numeric,base_index numeric,
 has_total boolean,lower_index numeric,upper_index numeric,change_limit integer,
 OUT states numeric, OUT moves numeric, OUT visits integer)
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE remaining integer=days-depth; changes integer=CASE WHEN last_index=0 THEN 0 ELSE least(days,change_limit) END;
 allowance integer; variable integer;k integer;fixed_prefix numeric;fixed_suffix numeric;
 lo numeric;hi numeric;width numeric;first_move numeric;last_move numeric;fan numeric;
BEGIN
 states=0;moves=0;visits=0;
 FOR h IN 0..CASE WHEN change_limit IS NULL THEN 0 ELSE least(depth,changes) END LOOP
  visits=visits+1;
  allowance=CASE WHEN change_limit IS NULL THEN remaining ELSE changes-h END;
  lo=0;hi=0;
  IF has_total THEN
   variable=CASE WHEN change_limit IS NULL THEN depth ELSE h END;
   fixed_prefix=(depth-variable)*base_index;
   k=least(remaining,allowance);fixed_suffix=(remaining-k)*base_index;
   lo=greatest(fixed_prefix,lower_index-fixed_suffix-k*last_index);
   hi=least(fixed_prefix+variable*last_index,upper_index-fixed_suffix);
   IF lo>hi THEN CONTINUE;END IF;
  END IF;
  width=hi-lo+1;states=states+width;
  IF states>65536 THEN RAISE EXCEPTION 'swing_numerical_scope' USING DETAIL='state slots';END IF;
  IF remaining=0 THEN CONTINUE;END IF;
  fan=1;
  IF allowance>0 THEN
   first_move=0;last_move=last_index;
   IF has_total THEN
    k=least(remaining-1,CASE WHEN change_limit IS NULL THEN remaining-1 ELSE allowance-1 END);
    fixed_suffix=(remaining-1-k)*base_index;
    first_move=greatest(0,lower_index-hi-fixed_suffix-k*last_index);
    last_move=least(last_index,upper_index-lo-fixed_suffix);
   END IF;
   fan=greatest(0,last_move-first_move+1);
   IF change_limit IS NOT NULL AND base_index NOT BETWEEN first_move AND last_move THEN fan=fan+1;END IF;
  END IF;
  moves=moves+width*fan;
  IF moves>100000000 THEN RAISE EXCEPTION 'swing_numerical_scope' USING DETAIL='candidate visits';END IF;
 END LOOP;
END$$;

CREATE FUNCTION trading.gas_swing_booking_work(calendar date[],deadlines timestamptz[],at_time timestamptz,
 reference_quantity numeric,minimum numeric,maximum numeric,step numeric,period text,lower_limit numeric,upper_limit numeric,
 basis text,change_limit integer) RETURNS jsonb
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE p record;has_total boolean=lower_limit IS NOT NULL OR upper_limit IS NOT NULL;
 last_index numeric=div(maximum-minimum,step);base_index numeric=div(reference_quantity-minimum,step);
 lo numeric;hi numeric;lower_index numeric;upper_index numeric;n integer;k integer;flexible boolean;
 low_move numeric;high_move numeric;fixed numeric;days integer=0;before_layer record;after_layer record;
 preflight bigint=cardinality(calendar);slots numeric=0;moves numeric=0;branches numeric=0;choices numeric=0;
 payoffs numeric=0;normalizers numeric=0;rolling numeric=0;max_edges numeric=0;
 tick_us constant bigint=21600000000;phase bigint;elapsed_us bigint;splits integer=0;t bigint=0;previous bigint=0;
 nodes bigint;months date[]='{}';month date;operations numeric;peak_bytes numeric;
 format_version constant text='swing_trinomial_6h_unit_reference_v2';
BEGIN
 -- The first future retained deadline sets the tick phase, including commercially forced dates.
 SELECT mod((extract(epoch FROM min(d)-at_time)*1000000)::bigint-1,tick_us)+1 INTO phase
 FROM unnest(deadlines) d WHERE d>at_time;
 FOR p IN SELECT array_agg(i ORDER BY i) FILTER(WHERE deadlines[i]>at_time) AS future,
   bool_or(deadlines[i]<=at_time) AS elapsed,count(*)::int AS size
  FROM generate_subscripts(calendar,1) i
  GROUP BY CASE WHEN period='delivery_month' THEN date_trunc('month',calendar[i]::timestamp)::date ELSE calendar[1] END
  ORDER BY min(i)
 LOOP
  n=coalesce(cardinality(p.future),0);
  IF n=0 THEN CONTINUE;END IF;
  lo=lower_limit*CASE WHEN basis='per_day_v1' THEN p.size ELSE 1 END;
  hi=upper_limit*CASE WHEN basis='per_day_v1' THEN p.size ELSE 1 END;
  IF p.elapsed THEN lo=NULL;hi=NULL;END IF;
  -- Signed integer ceil/floor keep grid endpoints inward even for negative differences.
  lower_index=coalesce(div(lo-n*minimum,step)+(mod(lo-n*minimum,step)>0)::integer,0);
  upper_index=coalesce(div(hi-n*minimum,step)-(mod(hi-n*minimum,step)<0)::integer,n*last_index);
  flexible=minimum<>maximum AND change_limit IS DISTINCT FROM 0;
  IF flexible AND (lo IS NOT NULL OR hi IS NOT NULL) THEN
   k=least(n-1,coalesce(change_limit,n)-1);fixed=(n-1-k)*reference_quantity;
   low_move=greatest(minimum,lo-fixed-k*maximum);high_move=least(maximum,hi-fixed-k*minimum);
   flexible=low_move<high_move;
   IF low_move=high_move AND low_move<>reference_quantity THEN
    k=least(n-1,coalesce(change_limit,n));fixed=(n-1-k)*reference_quantity;
    flexible=(lo IS NULL OR lo<=reference_quantity+fixed+k*maximum) AND
             (hi IS NULL OR hi>=reference_quantity+fixed+k*minimum);
   END IF;
  END IF;
  slots=slots+1;
  FOR d IN 1..n LOOP
   SELECT * INTO before_layer FROM trading.gas_swing_layer(d-1,n,last_index,base_index,has_total,lower_index,upper_index,change_limit);
   SELECT * INTO after_layer FROM trading.gas_swing_layer(d,n,last_index,base_index,has_total,lower_index,upper_index,change_limit);
   slots=slots+after_layer.states;moves=moves+before_layer.moves;days=days+1;
   preflight=preflight+before_layer.visits+after_layer.visits;
   elapsed_us=(extract(epoch FROM deadlines[p.future[d]]-at_time)*1000000)::bigint;
   IF mod(elapsed_us-phase,tick_us)<>0 THEN splits=splits+1;END IF;
   IF flexible THEN
    t=(elapsed_us-phase)/tick_us+1+splits;
    IF 3*t::numeric*t>100000000 THEN RAISE EXCEPTION 'swing_numerical_scope' USING DETAIL='operations';END IF;
    nodes=2*t+1;
    branches=branches+3*(t*t-previous*previous)*before_layer.states;
    choices=choices+nodes*before_layer.moves;payoffs=payoffs+nodes*least(last_index+1,before_layer.moves);
    normalizers=normalizers+t;rolling=greatest(rolling,(nodes+2)*greatest(before_layer.states,after_layer.states));
    max_edges=greatest(max_edges,before_layer.moves);previous=t;
    month=date_trunc('month',calendar[p.future[d]]::timestamp)::date;
    IF NOT month=ANY(months) THEN months=array_append(months,month);END IF;
   END IF;
   operations=preflight+4*slots+3*moves+branches+choices+payoffs+normalizers+(2*previous+1)*cardinality(months)+previous+days;
   peak_bytes=32768+4096*slots+8192*days+256*previous+96*((2*previous+1)*cardinality(months)+2*rolling)+1536*max_edges;
   IF operations>100000000 THEN RAISE EXCEPTION 'swing_numerical_scope' USING DETAIL='operations';END IF;
   IF peak_bytes>268435456 THEN RAISE EXCEPTION 'swing_numerical_scope' USING DETAIL='modeled live bytes';END IF;
  END LOOP;
 END LOOP;
 RETURN jsonb_build_object('policy',format_version,'calculationAt',at_time,'operations',coalesce(operations,preflight),
    'peakBytes',coalesce(peak_bytes,32768),'timeSteps',previous);
END$$;
ALTER FUNCTION trading.gas_swing_layer(integer,integer,numeric,numeric,boolean,numeric,numeric,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.gas_swing_booking_work(date[],timestamptz[],timestamptz,numeric,numeric,numeric,numeric,text,numeric,numeric,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_swing_layer(integer,integer,numeric,numeric,boolean,numeric,numeric,integer),
 trading.gas_swing_booking_work(date[],timestamptz[],timestamptz,numeric,numeric,numeric,numeric,text,numeric,numeric,text,integer) FROM PUBLIC;

CREATE FUNCTION trading.save_gas_swing(catalog uuid,instrument uuid,operation uuid,rights jsonb,as_of timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE term jsonb;pricing jsonb;source trading.gas_component_terms;covered integer[];schedule uuid;days integer;lead integer;
 minimum numeric;maximum numeric;step numeric;lower_limit numeric;upper_limit numeric;change_limit integer;
 key text;ref numeric;selection text;period text;basis text;count_rule text;calendar date[];deadlines timestamptz[];
 work jsonb;length integer;allowance integer;multiplier integer;required_keys text[];allowed_keys text[];
BEGIN
 IF rights IS NULL OR rights IN('null','[]') THEN RETURN;END IF;
 IF jsonb_typeof(rights)<>'array' THEN RAISE EXCEPTION 'swing_right';END IF;
 IF jsonb_array_length(rights)<>1 THEN RAISE EXCEPTION 'swing_right';END IF;
 term=rights->0;
 required_keys=ARRAY['side','entries','holder','planningStep','dailyMinimum','dailyMaximum','leadHours','pricing'];
 allowed_keys=required_keys||ARRAY['limitPeriod','planRule','deadlineRule','totalMinimum','totalMaximum','totalBasis','changeCount','countRule','quantityPolicy'];
 IF jsonb_typeof(term) IS DISTINCT FROM 'object' OR NOT(term ?& required_keys) OR term-allowed_keys<>'{}' OR
  (term->>'holder' IN('us','counterparty')) IS NOT TRUE OR
  jsonb_typeof(term->'entries') IS DISTINCT FROM 'array' OR jsonb_typeof(term->'leadHours') IS DISTINCT FROM 'number' OR
  (term->>'leadHours' ~ '^[0-9]{1,10}$') IS NOT TRUE
 THEN RAISE EXCEPTION 'swing_right';END IF;
 IF (term->>'limitPeriod' IN('term','delivery_month')) IS NOT TRUE THEN
  RAISE EXCEPTION 'swing_right' USING DETAIL='{"field":"limitPeriod"}';END IF;
 IF term->>'planRule' IS DISTINCT FROM 'best_plan_on_forwards_v1' THEN
  RAISE EXCEPTION 'swing_right' USING DETAIL='{"field":"planRule"}';END IF;
 IF term->>'deadlineRule' IS DISTINCT FROM 'lead_before_first_gas_day_v1' THEN
  RAISE EXCEPTION 'swing_right' USING DETAIL='{"field":"deadlineRule"}';END IF;
 lead=(term->>'leadHours')::integer;
 pricing=term->'pricing';
 IF pricing IS DISTINCT FROM '{"model":"gas_swing_one_factor_lognormal_v1","dayCount":"act_365_fixed_instant_v1","numericalPolicy":"swing_trinomial_6h_unit_reference_v2","marksConvention":"curve_marks_log_return_held_v1"}'::jsonb
 THEN RAISE EXCEPTION 'swing_model_scope';END IF;
 SELECT * INTO source FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,code,role)=(catalog,instrument,term->>'side','physical');
 days=source.delivery_to-source.delivery_from;
 IF source.slot IS NULL OR source.source_slot IS NOT NULL OR (source.quantity_policy IS NOT NULL AND source.quantity_policy<>'delivery_schedule_v1') OR
  source.quantity_basis<>'per_operating_day' OR days NOT BETWEEN 1 AND 1200 OR EXISTS(SELECT 1 FROM trading.gas_component_terms m
   WHERE(m.catalog_id,m.instrument_id,m.source_slot,m.role)=(catalog,instrument,source.slot,'physical'))
 THEN RAISE EXCEPTION 'swing_right';END IF;
 SELECT array_agg(m.slot ORDER BY m.slot) INTO covered FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)
  AND (m.slot=source.slot OR m.source_slot=source.slot AND m.role IN('consideration','indexed_consideration','fee'));
 IF jsonb_array_length(term->'entries')<>cardinality(covered) OR EXISTS(SELECT 1 FROM trading.gas_component_terms m
  WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND m.slot=ANY(covered) AND
   (NOT (term->'entries' ? m.code) OR m.role='indexed_consideration' OR m.part_kind IS NOT NULL OR
    (m.delivery_from,m.delivery_to) IS DISTINCT FROM(source.delivery_from,source.delivery_to))) OR
  EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e WHERE(e.catalog_id,e.instrument_id)=(catalog,instrument) AND e.slot=ANY(covered))
 THEN RAISE EXCEPTION 'swing_right';END IF;

 -- The preceding writer verified the whole program, every dated assignment and its curve/contract bindings.
 SELECT schedule_id INTO STRICT schedule FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,operation,source.slot);
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.role,r.output_kind)=(catalog,schedule,'source','price') AND
   (r.operation NOT IN('constant_price','price_lookup') OR r.operation='constant_price' AND r.parameters->>'numerator' IS DISTINCT FROM '0'))
 THEN RAISE EXCEPTION 'swing_model_scope';END IF;
 ref=source.independent_quantity;
 selection=CASE WHEN source.quantity_policy IS NULL THEN 'swing_elected_quantity_v1' ELSE 'swing_elected_delivery_quantity_v1' END;
 IF term->>'quantityPolicy' IS NOT NULL AND term->>'quantityPolicy'<>selection THEN RAISE EXCEPTION 'swing_right' USING DETAIL='{"field":"quantityPolicy"}';END IF;
 FOREACH key IN ARRAY ARRAY['planningStep','dailyMinimum','dailyMaximum','totalMinimum','totalMaximum'] LOOP
  IF key IN('totalMinimum','totalMaximum') AND term->>key IS NULL THEN CONTINUE;END IF;
  IF jsonb_typeof(term->key) IS DISTINCT FROM 'string' OR (term->>key ~ '^(0|[1-9][0-9]{0,23})([.][0-9]{1,18})?$') IS NOT TRUE
  THEN RAISE EXCEPTION 'swing_right' USING DETAIL=jsonb_build_object('field',key)::text;END IF;
 END LOOP;
 minimum=(term->>'dailyMinimum')::numeric;maximum=(term->>'dailyMaximum')::numeric;step=(term->>'planningStep')::numeric;
 lower_limit=(term->>'totalMinimum')::numeric;upper_limit=(term->>'totalMaximum')::numeric;
 period=term->>'limitPeriod';basis=term->>'totalBasis';count_rule=term->>'countRule';
 IF term->>'changeCount' IS NOT NULL AND (jsonb_typeof(term->'changeCount')<>'number' OR term->>'changeCount' !~ '^[0-9]{1,10}$')
 THEN RAISE EXCEPTION 'swing_right' USING DETAIL='{"field":"changeCount"}';END IF;
 change_limit=(term->>'changeCount')::integer;
 key=CASE
  WHEN step<=0 THEN 'planningStep'
  WHEN minimum>ref THEN 'dailyMinimum'
  WHEN maximum<ref THEN 'dailyMaximum'
  WHEN mod(ref-minimum,step)<>0 OR mod(maximum-minimum,step)<>0 THEN 'planningStep'
  WHEN upper_limit-lower_limit<step THEN 'totalMaximum'
  WHEN (basis IS NULL)<>(lower_limit IS NULL AND upper_limit IS NULL) OR basis NOT IN('per_period_v1','per_day_v1') THEN 'totalBasis'
  WHEN (change_limit IS NULL)<>(count_rule IS NULL) OR count_rule<>'differs_from_base_v1' THEN 'countRule'
 END;
 IF key IS NOT NULL THEN RAISE EXCEPTION 'swing_right' USING DETAIL=jsonb_build_object('field',key)::text;END IF;
 SELECT array_agg(source.delivery_from+g.ordinal ORDER BY g.ordinal),array_agg(g.origin-make_interval(hours=>lead) ORDER BY g.ordinal)
 INTO calendar,deadlines FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule)
 AND g.ordinal BETWEEN 0 AND days-1 AND g.operating_days=ARRAY[(source.delivery_from+g.ordinal)-date '0001-01-01']
 AND g.origin-make_interval(hours=>lead)>timestamptz '0001-01-01 UTC';
 IF cardinality(calendar) IS DISTINCT FROM days THEN RAISE EXCEPTION 'swing_right';END IF;
 FOR length IN SELECT count(*) FROM unnest(calendar) d GROUP BY
   CASE WHEN period='delivery_month' THEN date_trunc('month',d::timestamp)::date ELSE calendar[1] END
 LOOP
  allowance=least(length,change_limit);multiplier=CASE WHEN basis='per_day_v1' THEN length ELSE 1 END;
  key=CASE
   WHEN length*ref-allowance*(ref-minimum)>upper_limit*multiplier THEN 'totalMaximum'
   WHEN length*ref+allowance*(maximum-ref)<lower_limit*multiplier THEN 'totalMinimum'
  END;
  IF key IS NOT NULL THEN RAISE EXCEPTION 'swing_feasibility' USING DETAIL=jsonb_build_object('field',key)::text;END IF;
 END LOOP;
 BEGIN
  work=trading.gas_swing_booking_work(calendar,deadlines,as_of,ref,minimum,maximum,step,period,lower_limit,upper_limit,basis,change_limit);
 EXCEPTION WHEN raise_exception THEN
  IF SQLERRM<>'swing_numerical_scope' THEN RAISE;END IF;
  GET STACKED DIAGNOSTICS key=PG_EXCEPTION_DETAIL;
  RAISE EXCEPTION 'swing_numerical_scope' USING DETAIL='{"field":"numericalPolicy"}',HINT=key;
 END;
 INSERT INTO trading.gas_swing_rights VALUES(catalog,instrument,0,source.slot,schedule,term->>'holder',
  trading.gas_swing_ratio_of(minimum,ref),trading.gas_swing_ratio_of(maximum,ref),trading.gas_swing_ratio_of(step,ref),period,
  trading.gas_swing_ratio_of(lower_limit,ref),trading.gas_swing_ratio_of(upper_limit,ref),basis,change_limit,count_rule,
  selection,term->>'planRule',term->>'deadlineRule',lead);
 INSERT INTO trading.gas_swing_members SELECT catalog,instrument,0,slot FROM unnest(covered) slot;
 INSERT INTO trading.gas_swing_pricing VALUES(catalog,instrument,0,pricing->>'model',pricing->>'dayCount',pricing->>'numericalPolicy',work,pricing->>'marksConvention');
 INSERT INTO trading.gas_swing_days SELECT catalog,instrument,0,calendar[i],schedule,i-1,deadlines[i] FROM generate_subscripts(calendar,1) i;
EXCEPTION WHEN datetime_field_overflow OR interval_field_overflow OR numeric_value_out_of_range THEN RAISE EXCEPTION 'swing_right';
END$$;
ALTER FUNCTION trading.save_gas_swing(uuid,uuid,uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.save_gas_swing(uuid,uuid,uuid,jsonb,timestamptz) FROM PUBLIC;

COMMIT;
