-- Normalize the commercial volume grid and bind each deadline to its sealed gas-day geometry.
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
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_swing_upgrade: unexpected helper security.write_gas_composition(uuid,jsonb,timestamptz,boolean)';END IF;
END$guard$;

-- Publication follows native term admission within the same booking transaction.
CREATE UNIQUE INDEX gas_swing_published_slot ON trading.instrument_side_schedules(catalog_id,instrument_id,slot,schedule_id);
CREATE TABLE trading.gas_swing_rights(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal=0),
 physical_slot integer NOT NULL,schedule_id uuid NOT NULL,
 holder text NOT NULL CHECK(holder IN('us','counterparty')),
 reference_lots integer NOT NULL CHECK(reference_lots>0),
 daily_minimum integer NOT NULL CHECK(daily_minimum BETWEEN 0 AND reference_lots),
 daily_maximum integer NOT NULL CHECK(daily_maximum>=reference_lots),
 total_minimum integer NOT NULL CHECK(total_minimum>=0),total_maximum integer NOT NULL CHECK(total_maximum>=total_minimum),
 quantity_policy text NOT NULL CHECK(quantity_policy='swing_elected_delivery_quantity_v1'),
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
 numerical_policy text NOT NULL CHECK(numerical_policy='swing_trinomial_6h_half_even_18_v1'),
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
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime,openetrm_risk_worker',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$access$;

CREATE FUNCTION trading.gas_swing_terms(catalog uuid,instrument uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog SET timezone='UTC' AS $$
 SELECT coalesce(jsonb_agg((to_jsonb(r)-ARRAY['catalog_id','instrument_id','schedule_id'])||
  (to_jsonb(p)-ARRAY['catalog_id','instrument_id','ordinal'])||jsonb_build_object(
   'slots',(SELECT jsonb_agg(m.slot ORDER BY m.slot) FROM trading.gas_swing_members m
    WHERE(m.catalog_id,m.instrument_id,m.ordinal)=(catalog,instrument,r.ordinal)),
   'days',(SELECT jsonb_agg(jsonb_build_object('day',d.day,'exercise_at',d.exercise_at) ORDER BY d.day)
    FROM trading.gas_swing_days d WHERE(d.catalog_id,d.instrument_id,d.ordinal)=(catalog,instrument,r.ordinal))) ORDER BY r.ordinal),'[]')
 FROM trading.gas_swing_rights r JOIN trading.gas_swing_pricing p USING(catalog_id,instrument_id,ordinal)
 WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
$$;
ALTER FUNCTION trading.gas_swing_terms(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_swing_terms(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.gas_swing_terms(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.save_gas_swing(catalog uuid,instrument uuid,operation uuid,rights jsonb) RETURNS void
LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE term jsonb;pricing jsonb;source trading.gas_component_terms;covered integer[];schedule uuid;days integer;lead integer;
 increment numeric;amount numeric;lots integer[]='{}';key text;inserted integer;
BEGIN
 IF rights IS NULL OR rights IN('null','[]') THEN RETURN;END IF;
 IF jsonb_typeof(rights)<>'array' THEN RAISE EXCEPTION 'swing_right';END IF;
 IF jsonb_array_length(rights)<>1 THEN RAISE EXCEPTION 'swing_right';END IF;
 term=rights->0;
 IF jsonb_typeof(term) IS DISTINCT FROM 'object' OR NOT(term ?& ARRAY['side','entries','holder','increment','dailyMinimum','dailyMaximum','totalMinimum','totalMaximum','leadHours','quantityPolicy']) OR
  term-ARRAY['side','entries','holder','increment','dailyMinimum','dailyMaximum','totalMinimum','totalMaximum','leadHours','quantityPolicy','pricing']<>'{}' OR
  (term->>'holder' IN('us','counterparty') AND term->>'quantityPolicy'='swing_elected_delivery_quantity_v1') IS NOT TRUE OR
  jsonb_typeof(term->'entries') IS DISTINCT FROM 'array' OR jsonb_typeof(term->'leadHours') IS DISTINCT FROM 'number' OR
  (term->>'leadHours' ~ '^[0-9]{1,10}$') IS NOT TRUE
 THEN RAISE EXCEPTION 'swing_right';END IF;
 lead=(term->>'leadHours')::integer;
 pricing=term->'pricing';
 IF pricing IS DISTINCT FROM '{"model":"gas_swing_one_factor_lognormal_v1","dayCount":"act_365_fixed_instant_v1","numericalPolicy":"swing_trinomial_6h_half_even_18_v1","marksConvention":"curve_marks_log_return_held_v1"}'::jsonb
 THEN RAISE EXCEPTION 'swing_model_scope';END IF;
 SELECT * INTO source FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,code,role)=(catalog,instrument,term->>'side','physical');
 days=source.delivery_to-source.delivery_from;
 IF source.slot IS NULL OR source.source_slot IS NOT NULL OR source.quantity_policy IS NOT NULL OR
  source.quantity_basis<>'per_operating_day' OR days NOT BETWEEN 1 AND 31 OR EXISTS(SELECT 1 FROM trading.gas_component_terms m
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
 FOREACH key IN ARRAY ARRAY['increment','dailyMinimum','dailyMaximum','totalMinimum','totalMaximum'] LOOP
  IF jsonb_typeof(term->key) IS DISTINCT FROM 'string' OR (term->>key ~ '^(0|[1-9][0-9]{0,23})([.][0-9]{1,18})?$') IS NOT TRUE THEN RAISE EXCEPTION 'swing_right';END IF;
  amount=(term->>key)::numeric;
  IF key='increment' THEN
   increment=amount;amount=source.independent_quantity;
   IF increment<=0 THEN RAISE EXCEPTION 'swing_right';END IF;
  END IF;
  IF mod(amount,increment)<>0 OR div(amount,increment)>2147483647 THEN RAISE EXCEPTION 'swing_right';END IF;
  lots=array_append(lots,div(amount,increment)::integer);
 END LOOP;
 IF lots[1] NOT BETWEEN lots[2] AND lots[3] OR lots[4]>lots[5] THEN RAISE EXCEPTION 'swing_right';END IF;
 IF greatest(lots[4],days::bigint*lots[2])>least(lots[5],days::bigint*lots[3]) THEN RAISE EXCEPTION 'swing_feasibility';END IF;
 INSERT INTO trading.gas_swing_rights VALUES(catalog,instrument,0,source.slot,schedule,term->>'holder',lots[1],lots[2],lots[3],lots[4],lots[5],
  term->>'quantityPolicy','lead_before_first_gas_day_v1',lead);
 INSERT INTO trading.gas_swing_members SELECT catalog,instrument,0,slot FROM unnest(covered) slot;
 INSERT INTO trading.gas_swing_pricing VALUES(catalog,instrument,0,pricing->>'model',pricing->>'dayCount',pricing->>'numericalPolicy',pricing->>'marksConvention');
 INSERT INTO trading.gas_swing_days SELECT catalog,instrument,0,source.delivery_from+g.ordinal,schedule,g.ordinal,g.origin-make_interval(hours=>lead)
  FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule)
   AND g.ordinal BETWEEN 0 AND days-1 AND g.operating_days=ARRAY[(source.delivery_from+g.ordinal)-date '0001-01-01']
   AND g.origin-make_interval(hours=>lead)>timestamptz '0001-01-01 UTC';
 GET DIAGNOSTICS inserted=ROW_COUNT;
 IF inserted<>days THEN RAISE EXCEPTION 'swing_right';END IF;
EXCEPTION WHEN datetime_field_overflow OR interval_field_overflow OR numeric_value_out_of_range THEN RAISE EXCEPTION 'swing_right';
END$$;
ALTER FUNCTION trading.save_gas_swing(uuid,uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.save_gas_swing(uuid,uuid,uuid,jsonb) FROM PUBLIC;

DO $patch$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$'exerciseRights','chargeSides','imbalanceCashouts'))$before$,$after$'exerciseRights','chargeSides','imbalanceCashouts','swingRights'))$after$),
 ($before$ -- Equality is proved$before$,$after$ PERFORM trading.save_gas_swing(catalog,instrument,operation,body->'swingRights');
 -- Equality is proved$after$),
 ($before$   trading.gas_exercise_terms(catalog,instrument)=$before$,$after$   trading.gas_swing_terms(catalog,instrument)=trading.gas_swing_terms(catalog,amend_head.instrument_id) AND
   trading.gas_exercise_terms(catalog,instrument)=$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_swing_upgrade: patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$patch$;
COMMIT;
