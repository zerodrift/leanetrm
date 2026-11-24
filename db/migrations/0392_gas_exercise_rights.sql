-- Immutable exercise terms belong to the exact gas Instrument revision.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE TABLE trading.gas_exercise_rights(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),
 period text NOT NULL CHECK(period IN('delivery_day','delivery_month')),
 holder text NOT NULL CHECK(holder IN('us','counterparty')),
 settlement text NOT NULL CHECK(settlement IN('physical','cash_automatic')),
 model text NOT NULL CHECK(model='spread_call_kirk_lognormal_v1'),
 day_count text NOT NULL CHECK(day_count='act_365_fixed_instant_v1'),
 time_rule text,lead_hours integer,
 PRIMARY KEY(catalog_id,instrument_id,ordinal),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_instruments(catalog_id,instrument_id),
 CHECK((CASE settlement WHEN 'physical' THEN time_rule='lead_before_first_gas_day_v1' AND lead_hours>=0
  ELSE time_rule IS NULL AND lead_hours IS NULL END) IS TRUE)
);
CREATE TABLE trading.gas_exercise_right_entries(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL,
 entry_ordinal integer NOT NULL CHECK(entry_ordinal>=0),slot integer NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,ordinal,entry_ordinal),
 UNIQUE(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_exercise_rights,
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot)
);
CREATE TABLE trading.gas_exercise_groups(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL,
 period_from date NOT NULL,period_to date NOT NULL,exercise_at timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,ordinal,period_from),
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_exercise_rights,
 CHECK(isfinite(period_from) AND isfinite(period_to) AND period_from<period_to AND isfinite(exercise_at))
);
-- A binding can be shared. At least one selected trade must hold it at the worker's retained cut.
CREATE FUNCTION risk.gas_exercise_binding_visible(catalog uuid,binding uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;
BEGIN
 IF risk.worker_read() IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
  OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RETURN false;END IF;
 job=risk.bound_lifecycle_reader();
 RETURN catalog=job.catalog_id AND EXISTS(
  SELECT FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
   AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
   AND (q.market_catalog_id,q.market_context_id,q.trading_as_of)=(catalog,job.context_id,job.valuation_at)
   AND q.expires_at>clock_timestamp())
 AND security.gas_component_sources_visible(catalog,binding)
 AND EXISTS(
  SELECT FROM trading.gas_trade_revisions r WHERE (r.catalog_id,r.instrument_id)=(catalog,binding)
   AND job.request->'bookIds' @> jsonb_build_array(r.book_id)
   AND (NULLIF(job.request->'tradeIds','null'::jsonb) IS NULL OR job.request->'tradeIds' @> jsonb_build_array(r.trade_id))
   AND security.trading_visible(catalog,r.publication_order,r.effective_at)
   AND NOT EXISTS(SELECT FROM trading.gas_trade_revisions newer WHERE (newer.catalog_id,newer.trade_id)=(catalog,r.trade_id)
    AND newer.revision>r.revision AND security.trading_visible(catalog,newer.publication_order,newer.effective_at)));
END$$;
ALTER FUNCTION risk.gas_exercise_binding_visible(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.gas_exercise_binding_visible(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.gas_exercise_binding_visible(uuid,uuid) TO openetrm_risk_worker;

DO $access$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['gas_exercise_rights','gas_exercise_right_entries','gas_exercise_groups'] LOOP
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

-- Used by held reconstruction and the writer's exact no-change comparison.
CREATE FUNCTION trading.gas_exercise_terms(catalog uuid,instrument uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog SET timezone='UTC' AS $$
 SELECT coalesce(jsonb_agg((to_jsonb(r)-'catalog_id'-'instrument_id')||jsonb_build_object(
  'slots',(SELECT jsonb_agg(e.slot ORDER BY e.entry_ordinal) FROM trading.gas_exercise_right_entries e
   WHERE(e.catalog_id,e.instrument_id,e.ordinal)=(catalog,instrument,r.ordinal)),
  'groups',(SELECT jsonb_agg(to_jsonb(g)-'catalog_id'-'instrument_id'-'ordinal' ORDER BY g.period_from)
   FROM trading.gas_exercise_groups g WHERE(g.catalog_id,g.instrument_id,g.ordinal)=(catalog,instrument,r.ordinal))) ORDER BY r.ordinal),'[]'::jsonb)
 FROM trading.gas_exercise_rights r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
$$;
ALTER FUNCTION trading.gas_exercise_terms(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_exercise_terms(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.gas_exercise_terms(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;

-- The private writer has already verified each program against its exact Side terms.
CREATE FUNCTION trading.save_gas_exercise_rights(catalog uuid,instrument uuid,operation uuid,rights jsonb) RETURNS void
LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE term jsonb;n integer;lead integer;first_day date;last_day date;day date;next_day date;at_time timestamptz;
 count_entries integer;equal_days boolean;has_physical boolean;
BEGIN
 IF rights IS NULL OR rights='null'::jsonb THEN RETURN;END IF;
 IF jsonb_typeof(rights)<>'array' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF rights='[]' THEN RETURN;END IF;
 FOR term,n IN SELECT value,(ordinality-1)::integer FROM jsonb_array_elements(rights) WITH ORDINALITY LOOP
  IF jsonb_typeof(term)<>'object' OR NOT(term ?& ARRAY['entries','period','holder','settlement']) OR
   EXISTS(SELECT 1 FROM jsonb_object_keys(term) k WHERE k NOT IN('entries','period','holder','settlement','leadHours')) OR
   (term->>'period' IN('delivery_day','delivery_month') AND term->>'holder' IN('us','counterparty')
     AND term->>'settlement' IN('physical','cash_automatic') AND jsonb_typeof(term->'entries')='array') IS NOT TRUE
   THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  lead=NULL;
  IF term->>'settlement'='physical' THEN
   IF (jsonb_typeof(term->'leadHours')='number' AND term->>'leadHours' ~ '^[0-9]+$') IS NOT TRUE THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   IF (term->>'leadHours')::numeric>2147483647 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   lead=(term->>'leadHours')::integer;
  ELSIF term->>'leadHours' IS NOT NULL THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  SELECT count(*),min(m.delivery_from),min(m.delivery_to),count(DISTINCT (m.delivery_from,m.delivery_to))=1,bool_or(m.role='physical')
   INTO count_entries,first_day,last_day,equal_days,has_physical FROM trading.gas_component_terms m
   WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND term->'entries' ? m.code;
  IF count_entries=0 OR count_entries<>jsonb_array_length(term->'entries') OR NOT equal_days OR has_physical<>(lead IS NOT NULL)
   OR EXISTS(SELECT 1 FROM trading.gas_component_terms m JOIN trading.gas_exercise_right_entries e USING(catalog_id,instrument_id,slot)
    WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND term->'entries' ? m.code) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  INSERT INTO trading.gas_exercise_rights VALUES(catalog,instrument,n,term->>'period',term->>'holder',term->>'settlement',
   'spread_call_kirk_lognormal_v1','act_365_fixed_instant_v1',CASE WHEN lead IS NOT NULL THEN 'lead_before_first_gas_day_v1' END,lead);
  INSERT INTO trading.gas_exercise_right_entries SELECT catalog,instrument,n,(e.ordinality-1)::integer,m.slot
   FROM jsonb_array_elements_text(term->'entries') WITH ORDINALITY e JOIN trading.gas_component_terms m
   ON(m.catalog_id,m.instrument_id,m.code)=(catalog,instrument,e.value);
  day=first_day;
  WHILE day<last_day LOOP
   next_day=least(last_day,CASE term->>'period' WHEN 'delivery_day' THEN day+1 ELSE(date_trunc('month',day)+interval '1 month')::date END);
   IF lead IS NOT NULL THEN
    -- Each payment takes the gas-day clock of its own physical source.
    SELECT min(g.origin)-make_interval(hours=>lead) INTO at_time
     FROM trading.gas_exercise_right_entries e JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
     JOIN trading.gas_component_terms source ON(source.catalog_id,source.instrument_id,source.slot)=(catalog,instrument,coalesce(m.source_slot,m.slot))
     JOIN trading.side_schedule_request_items s ON(s.catalog_id,s.request_id,s.slot)=(catalog,operation,source.slot)
     JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(catalog,s.schedule_id,day-source.delivery_from)
      AND g.operating_days=ARRAY[day-date '0001-01-01']
     WHERE(e.catalog_id,e.instrument_id,e.ordinal)=(catalog,instrument,n) HAVING count(*)=count_entries;
   ELSE
    -- The fixing binding is the admitted occurrence for this delivery row, including monthly observations.
    SELECT max(f.fixing_at) INTO at_time FROM trading.gas_exercise_right_entries e
     JOIN trading.side_schedule_request_items s ON(s.catalog_id,s.request_id,s.slot)=(catalog,operation,e.slot)
     JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id)=(catalog,s.schedule_id)
     JOIN trading.side_schedule_bindings b ON(b.catalog_id,b.schedule_id,b.row_ordinal,b.kind,b.port)=(catalog,s.schedule_id,r.ordinal,'index_fixing','fixing')
     JOIN market_data.index_fixing_observations f ON(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to,f.series_id,f.source_code)=
      (b.source_catalog_id,b.definition_revision_id,b.fixing_index_code,b.fixing_from,b.fixing_to,b.series_id,b.source_code)
     WHERE(e.catalog_id,e.instrument_id,e.ordinal)=(catalog,instrument,n) AND r.delivery_from>=day AND r.delivery_to<=next_day;
   END IF;
   IF at_time IS NULL OR at_time<timestamptz '0001-01-01 UTC' OR at_time>=timestamptz '9999-12-31 UTC' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   INSERT INTO trading.gas_exercise_groups VALUES(catalog,instrument,n,day,next_day,at_time);
   day=next_day;
  END LOOP;
 END LOOP;
EXCEPTION WHEN datetime_field_overflow OR interval_field_overflow OR numeric_value_out_of_range THEN RAISE EXCEPTION 'gas_composition_input';
END$$;
ALTER FUNCTION trading.save_gas_exercise_rights(uuid,uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.save_gas_exercise_rights(uuid,uuid,uuid,jsonb) FROM PUBLIC;

DO $patch$
DECLARE p pg_proc;definition text;old_text text;new_text text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'dece2217a1311735c3e0fc79b003ec1464b7b36d0274cd6dd5241568e5f5754f'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_exercise_upgrade: unexpected helper %',p.oid::regprocedure;END IF;
 FOR old_text,new_text IN SELECT * FROM(VALUES
  ($old$'reason','amendmentPolicy'))$old$,$new$'reason','amendmentPolicy','exerciseRights'))$new$),
  ($old$ IF jsonb_array_length(physical) NOT BETWEEN$old$,$new$ IF body->'exerciseRights' NOT IN('[]'::jsonb,'null'::jsonb)
  AND EXISTS(SELECT 1 FROM jsonb_array_elements(physical) x WHERE x->'link' IS DISTINCT FROM 'null'::jsonb)
  THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF jsonb_array_length(physical) NOT BETWEEN$new$),
  ($old$ -- Equality is proved$old$,$new$ PERFORM trading.save_gas_exercise_rights(catalog,instrument,operation,body->'exerciseRights');
 -- Equality is proved$new$),
  ($old$IF amending AND valuation=amend_head.effective_at$old$,$new$IF amending AND
   trading.gas_exercise_terms(catalog,instrument)=trading.gas_exercise_terms(catalog,amend_head.instrument_id)
   AND valuation=amend_head.effective_at$new$)
 )changes(a,b) LOOP
  IF (length(definition)-length(replace(definition,old_text,'')))/length(old_text)<>1 THEN RAISE EXCEPTION 'gas_exercise_upgrade: patch mismatch';END IF;
  definition=replace(definition,old_text,new_text);
 END LOOP;
 EXECUTE definition;
END$patch$;
COMMIT;
