-- Accepted commercial elections belong to one trade's exact swing binding.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE p pg_proc;
BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_gas_cashout_binding(uuid,uuid,uuid)');
 IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>'a583939d101832afcec6a06b5a4175a62c0c633cbecb75fa834c038be5acc138'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
  OR (SELECT count(*) FROM pg_proc q WHERE(q.pronamespace,q.proname)=(p.pronamespace,p.proname))<>1
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_swing_election_upgrade: unexpected helper security.require_gas_cashout_binding(uuid,uuid,uuid)';END IF;
END$guard$;

CREATE TABLE trading.gas_swing_commands(
 catalog_id uuid NOT NULL, operation_id uuid NOT NULL, trade_id uuid NOT NULL, trade_revision integer NOT NULL,
 instrument_id uuid NOT NULL, ordinal integer NOT NULL, actor_id uuid NOT NULL REFERENCES security.principals(id),
 authority_until ref_data.instant NOT NULL, request jsonb NOT NULL, recording_as_of ref_data.instant NOT NULL, outcome jsonb NOT NULL,
 context_id uuid NOT NULL, input_business_on date NOT NULL, input_known_at ref_data.instant NOT NULL,
 input_system_id text NOT NULL, input_timeline integer NOT NULL,
 PRIMARY KEY(catalog_id,operation_id), UNIQUE(catalog_id,operation_id,trade_id,instrument_id,ordinal),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_swing_rights,
 CHECK(input_timeline>0 AND context_id=security.workspace_context_id(catalog_id,input_business_on,input_known_at))
);
CREATE TRIGGER capture_inputs BEFORE INSERT ON trading.gas_swing_commands FOR EACH ROW
 EXECUTE FUNCTION security.record_owner_read_inputs('catalog_id','context_id','input');

CREATE TABLE trading.gas_swing_elections(
 catalog_id uuid NOT NULL, id uuid NOT NULL, trade_id uuid NOT NULL, instrument_id uuid NOT NULL, ordinal integer NOT NULL,
 day date NOT NULL, revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000), previous_id uuid,
 quantity numeric CHECK(quantity>=0), exercising_party text NOT NULL CHECK(exercising_party IN('us','counterparty')),
 submitted_at ref_data.instant NOT NULL, effective_at ref_data.instant NOT NULL, evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 operation_id uuid NOT NULL, publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id), UNIQUE(catalog_id,trade_id,instrument_id,ordinal,day,revision),
 UNIQUE(catalog_id,trade_id,instrument_id,ordinal,day,id),
 UNIQUE NULLS NOT DISTINCT(catalog_id,trade_id,instrument_id,ordinal,day,quantity,exercising_party,submitted_at,effective_at,evidence),
 FOREIGN KEY(catalog_id,instrument_id,ordinal,day) REFERENCES trading.gas_swing_days,
 FOREIGN KEY(catalog_id,trade_id,instrument_id,ordinal,day,previous_id)
  REFERENCES trading.gas_swing_elections(catalog_id,trade_id,instrument_id,ordinal,day,id),
 FOREIGN KEY(catalog_id,operation_id,trade_id,instrument_id,ordinal) REFERENCES trading.gas_swing_commands(catalog_id,operation_id,trade_id,instrument_id,ordinal)
  DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK((revision=1)=(previous_id IS NULL) AND (previous_id IS NOT NULL OR quantity IS NOT NULL)
  AND submitted_at<=effective_at)
);

DO $access$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['gas_swing_commands','gas_swing_elections'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$access$;

-- The D454 financial guard checks current trading/source authority, the selected
-- trade head and (for workers) the exact bound job, catalog, book and trade.
CREATE FUNCTION security.require_gas_swing_binding(catalog uuid,trade uuid,binding uuid) RETURNS trading.gas_trade_revisions
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE head trading.gas_trade_revisions;
BEGIN
 head=security.require_gas_cashout_binding(catalog,trade,binding);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(b.catalog_id,b.id)=(catalog,head.book_id) AND security.trading_visible(catalog,b.publication_order,b.effective_at)
    AND security.trading_visible(catalog,p.publication_order,p.effective_at))
  OR EXISTS(SELECT 1 FROM trading.instrument_side_schedules m WHERE(m.catalog_id,m.instrument_id)=(catalog,binding)
    AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_publications p WHERE(p.catalog_id,p.schedule_id)=(catalog,m.schedule_id)
     AND security.trading_visible(catalog,p.publication_order,p.effective_at)))
 THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 RETURN head;
EXCEPTION WHEN raise_exception THEN
 IF SQLERRM='gas_quantity_trade' THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 RAISE;
END$$;

CREATE FUNCTION security.gas_swing_write_until(catalog uuid) RETURNS timestamptz
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz=least(security.authority_until('AmendTrade'),settlement_data.futures_margin_preview_authority(catalog));
BEGIN
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR clock_timestamp()>=until_at
 THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 RETURN until_at;
END$$;

-- At commit the command may already have released its working view. Reopen
-- the exact inputs owned by the receipt, then restore the caller's bindings.
CREATE FUNCTION security.require_gas_swing_receipt(catalog uuid,operation uuid,until_at timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE receipt trading.gas_swing_commands;caller security.request_contexts;borrowed boolean;
BEGIN
 IF until_at IS NULL OR clock_timestamp()>=until_at OR security.trading_access(catalog,'AmendTrade') IS NOT TRUE
 THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO caller FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 SELECT * INTO receipt FROM trading.gas_swing_commands WHERE(catalog_id,operation_id,actor_id)=(catalog,operation,caller.actor_id);
 IF receipt.operation_id IS NULL THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 borrowed=(caller.market_catalog_id,caller.market_context_id,caller.trading_as_of) IS DISTINCT FROM(catalog,receipt.context_id,receipt.recording_as_of);
 IF borrowed THEN
  UPDATE security.request_contexts SET market_catalog_id=NULL,market_context_id=NULL,trading_as_of=NULL,trading_business_date=NULL,calendar_cut=NULL WHERE id=caller.id;
  PERFORM security.register_owner_coordinates(catalog,receipt.context_id,receipt.input_business_on,receipt.input_known_at,receipt.input_system_id,receipt.input_timeline);
  PERFORM security.use_market_context(catalog,receipt.context_id);
  PERFORM security.use_trading_context(catalog,receipt.recording_as_of);
 END IF;
 PERFORM security.require_gas_swing_binding(catalog,receipt.trade_id,receipt.instrument_id);
 IF borrowed THEN
  UPDATE security.request_contexts SET market_catalog_id=caller.market_catalog_id,market_context_id=caller.market_context_id,
   trading_as_of=caller.trading_as_of,trading_business_date=caller.trading_business_date,calendar_cut=caller.calendar_cut,read_sources=caller.read_sources WHERE id=caller.id;
 END IF;
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION trading.guard_gas_swing_commit() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_swing_receipt(NEW.catalog_id,NEW.operation_id,NEW.authority_until);
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER swing_command_authority AFTER INSERT ON trading.gas_swing_commands
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_swing_commit();

-- An exact retry returns the existing receipt and has its own authority
-- deadline. Each deferred trigger retains its NEW tuple even when the request
-- clears these inputs, binds another view or retries another command.
ALTER TABLE security.request_contexts
 ADD COLUMN swing_replay_catalog_id uuid, ADD COLUMN swing_replay_operation_id uuid, ADD COLUMN swing_replay_until ref_data.instant,
 ADD CHECK(num_nonnulls(swing_replay_catalog_id,swing_replay_operation_id,swing_replay_until) IN(0,3)),
 ADD FOREIGN KEY(swing_replay_catalog_id,swing_replay_operation_id) REFERENCES trading.gas_swing_commands(catalog_id,operation_id);
CREATE FUNCTION security.guard_gas_swing_replay() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_swing_receipt(NEW.swing_replay_catalog_id,NEW.swing_replay_operation_id,NEW.swing_replay_until);
 RETURN NULL;
END$$;
ALTER FUNCTION security.guard_gas_swing_replay() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.guard_gas_swing_replay() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER swing_replay_authority AFTER UPDATE OF swing_replay_operation_id ON security.request_contexts
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN(NEW.swing_replay_operation_id IS NOT NULL)
 EXECUTE FUNCTION security.guard_gas_swing_replay();

-- The final check may temporarily reopen the receipt's exact coordinates
-- after the read has released them. Enforce release again before returning.
CREATE FUNCTION security.commit_gas_swing_receipt() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 SET CONSTRAINTS security.read_sources_released DEFERRED;
 IF TG_RELID='trading.gas_swing_commands'::regclass THEN
  PERFORM security.require_gas_swing_receipt(NEW.catalog_id,NEW.operation_id,NEW.authority_until);
 ELSE
  PERFORM security.require_gas_swing_receipt(NEW.swing_replay_catalog_id,NEW.swing_replay_operation_id,NEW.swing_replay_until);
 END IF;
 SET CONSTRAINTS security.read_sources_released IMMEDIATE;
 RETURN NULL;
END$$;
ALTER FUNCTION security.commit_gas_swing_receipt() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.commit_gas_swing_receipt() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER commit_swing_election AFTER INSERT ON trading.gas_swing_commands
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.commit_gas_swing_receipt();
CREATE CONSTRAINT TRIGGER commit_swing_replay AFTER UPDATE OF swing_replay_operation_id ON security.request_contexts
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.swing_replay_operation_id IS NOT NULL)
 EXECUTE FUNCTION security.commit_gas_swing_receipt();

-- Validate the union of effective-time boundaries across all days. A revision
-- replaces the prior quantity and occupied-day count. Simultaneous revisions
-- enter the budget together. Withdrawal releases both, including a zero election.
CREATE FUNCTION trading.require_gas_swing_completion(catalog uuid,trade uuid,r trading.gas_swing_rights,reference_quantity numeric)
RETURNS void LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE minimum numeric=trading.gas_swing_absolute(r.daily_minimum,reference_quantity);
 maximum numeric=trading.gas_swing_absolute(r.daily_maximum,reference_quantity);
 lower_limit numeric=trading.gas_swing_absolute(r.total_minimum,reference_quantity);
 upper_limit numeric=trading.gas_swing_absolute(r.total_maximum,reference_quantity);
 failed_period date;
BEGIN
  WITH calendar AS (
   SELECT d.day,CASE WHEN r.limit_period='delivery_month' THEN date_trunc('month',d.day::timestamp)::date ELSE date '0001-01-01' END AS period
   FROM trading.gas_swing_days d WHERE(d.catalog_id,d.instrument_id,d.ordinal)=(catalog,r.instrument_id,r.ordinal)
  ), periods AS (SELECT period,count(*) AS size FROM calendar GROUP BY period),
  changes AS (
   SELECT d.period,e.effective_at,coalesce(e.quantity,0)-lag(coalesce(e.quantity,0),1,0) OVER chain AS amount,
    (e.quantity IS NOT NULL)::int-lag((e.quantity IS NOT NULL)::int,1,0) OVER chain AS occupied,
    coalesce(e.quantity<>reference_quantity,false)::int-lag(coalesce(e.quantity<>reference_quantity,false)::int,1,0) OVER chain AS changed
   FROM trading.gas_swing_elections e JOIN calendar d USING(day)
   WHERE(e.catalog_id,e.trade_id,e.instrument_id,e.ordinal)=(catalog,trade,r.instrument_id,r.ordinal)
   WINDOW chain AS (PARTITION BY day ORDER BY revision)
  ), boundaries AS (
   SELECT period,effective_at,sum(amount) AS amount,sum(occupied) AS occupied,sum(changed) AS changed
   FROM changes GROUP BY period,effective_at
  ), budgets AS (
   SELECT period,size,sum(amount) OVER timeline AS elected,size-sum(occupied) OVER timeline AS open_days,
    r.change_count-sum(changed) OVER timeline AS available
   FROM boundaries JOIN periods USING(period) WINDOW timeline AS (PARTITION BY period ORDER BY effective_at)
  )
  SELECT period INTO failed_period FROM budgets WHERE available<0 OR
   elected+open_days*reference_quantity-least(open_days,available)*(reference_quantity-minimum)>
      upper_limit*CASE WHEN r.total_basis='per_day_v1' THEN size ELSE 1 END OR
   elected+open_days*reference_quantity+least(open_days,available)*(maximum-reference_quantity)<
      lower_limit*CASE WHEN r.total_basis='per_day_v1' THEN size ELSE 1 END
  ORDER BY period LIMIT 1;
 IF FOUND THEN RAISE EXCEPTION 'swing_feasibility' USING HINT=failed_period::text;END IF;
END$$;

CREATE FUNCTION security.record_gas_swing_elections(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off SET timezone='UTC' AS $function$
DECLARE
 trade uuid; binding uuid; selected_binding uuid; operation uuid; right_no integer; record_context uuid; caller security.request_contexts;
 head trading.gas_trade_revisions; command trading.gas_swing_commands; r trading.gas_swing_rights; prior trading.gas_swing_elections;
 day_count integer; reference_quantity numeric; expected_heads jsonb; item jsonb; gas_day date; deadline timestamptz;
 amount numeric; submitted timestamptz; effective timestamptz; proof text; unchanged boolean; failed_field text; field_hint jsonb;
 until_at timestamptz=security.gas_swing_write_until(catalog);
 publication integer; receipt_rows jsonb[]='{}'; result jsonb;
 instant_pattern constant text='^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$';
BEGIN
 IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF trading.rec_keys(body,ARRAY['operationId','tradeId','instrumentId','expectedRevision','right','expectedHeads','elections']) IS NOT TRUE
  OR as_of IS NULL OR NOT isfinite(as_of)
  OR EXISTS(SELECT 1 FROM unnest(ARRAY['operationId','tradeId','instrumentId']) k WHERE jsonb_typeof(body->k)<>'string')
  OR jsonb_typeof(body->'expectedRevision')<>'number' OR body->>'expectedRevision' !~ '^[1-9][0-9]{0,2}$'
  OR jsonb_typeof(body->'right')<>'number' OR body->>'right' !~ '^(0|[1-9][0-9]{0,8})$'
  OR jsonb_typeof(body->'elections')<>'array' OR jsonb_array_length(body->'elections') NOT BETWEEN 1 AND 1200
  OR jsonb_typeof(body->'expectedHeads')<>'array' OR jsonb_array_length(body->'expectedHeads')>1200
 THEN RAISE EXCEPTION 'swing_election';END IF;
 IF octet_length(convert_to(body::text,'UTF8'))>4194304 THEN RAISE EXCEPTION 'swing_payload' USING DETAIL='{"field":"elections"}';END IF;
 trade=(body->>'tradeId')::uuid;binding=(body->>'instrumentId')::uuid;operation=(body->>'operationId')::uuid;right_no=(body->>'right')::int;
 IF operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'swing_election';END IF;

 -- All trading publications share this gate, including another writer on a
 -- different day of this right and a concurrent replacement of the trade head.
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO caller FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF caller.id IS NULL OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO command FROM trading.gas_swing_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF command.operation_id IS NOT NULL THEN
  PERFORM security.require_gas_swing_receipt(catalog,operation,until_at);
  IF (command.request,command.recording_as_of) IS DISTINCT FROM(body,as_of) THEN RAISE EXCEPTION 'swing_election_conflict';END IF;
  SET CONSTRAINTS security.swing_replay_authority DEFERRED;
  UPDATE security.request_contexts SET swing_replay_catalog_id=catalog,swing_replay_operation_id=operation,swing_replay_until=until_at WHERE id=caller.id;
  UPDATE security.request_contexts SET swing_replay_catalog_id=NULL,swing_replay_operation_id=NULL,swing_replay_until=NULL WHERE id=caller.id;
  RETURN command.outcome;
 END IF;
 record_context=security.resolve_operation_context(catalog,(as_of AT TIME ZONE 'UTC')::date,NULL);
 PERFORM security.use_market_context(catalog,record_context);
 PERFORM security.use_trading_context(catalog,as_of);
 SELECT h.instrument_id INTO selected_binding FROM trading.gas_trade_revisions h WHERE(h.catalog_id,h.trade_id)=(catalog,trade)
  AND security.trading_visible(catalog,h.publication_order,h.effective_at) ORDER BY h.revision DESC LIMIT 1;
 head=security.require_gas_swing_binding(catalog,trade,selected_binding);
 IF binding<>selected_binding THEN
  -- A prior binding on this trade is a stale command. Naming another trade's
  -- binding does not establish authority over that binding or its elections.
  IF NOT EXISTS(SELECT 1 FROM trading.gas_trade_revisions h WHERE(h.catalog_id,h.trade_id,h.instrument_id,h.revision)=
    (catalog,trade,binding,(body->>'expectedRevision')::integer) AND security.trading_visible(catalog,h.publication_order,h.effective_at))
   OR security.gas_component_sources_visible(catalog,binding) IS NOT TRUE
  THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
  RAISE EXCEPTION 'swing_election_conflict';
 END IF;
 IF head.revision<>(body->>'expectedRevision')::integer OR EXISTS(SELECT 1 FROM trading.gas_trade_revisions h
   WHERE(h.catalog_id,h.trade_id)=(catalog,trade) AND h.revision>head.revision)
 THEN RAISE EXCEPTION 'swing_election_conflict';END IF;
 SELECT * INTO r FROM trading.gas_swing_rights WHERE(catalog_id,instrument_id,ordinal)=(catalog,binding,right_no);
 IF r.instrument_id IS NULL THEN RAISE EXCEPTION 'swing_election';END IF;
 SELECT independent_quantity INTO STRICT reference_quantity FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,binding,r.physical_slot);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(body->'expectedHeads') h WHERE trading.rec_keys(h,ARRAY['day','revisionId']) IS NOT TRUE
   OR jsonb_typeof(h->'day')<>'string' OR h->>'day' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR jsonb_typeof(h->'revisionId') NOT IN('string','null'))
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'elections') e GROUP BY e->>'day' HAVING count(*)>1)
 THEN RAISE EXCEPTION 'swing_election';END IF;
 SELECT count(*),jsonb_agg(jsonb_build_object('day',d.day,'revisionId',h.id) ORDER BY d.day) INTO day_count,expected_heads
  FROM trading.gas_swing_days d LEFT JOIN LATERAL(SELECT e.id FROM trading.gas_swing_elections e
   WHERE(e.catalog_id,e.trade_id,e.instrument_id,e.ordinal,e.day)=(catalog,trade,binding,right_no,d.day) ORDER BY revision DESC LIMIT 1) h ON true
  WHERE(d.catalog_id,d.instrument_id,d.ordinal)=(catalog,binding,right_no);
 IF expected_heads IS DISTINCT FROM (SELECT jsonb_agg(jsonb_build_object('day',h->>'day','revisionId',(h->>'revisionId')::uuid) ORDER BY h->>'day') FROM jsonb_array_elements(body->'expectedHeads') h)
 THEN RAISE EXCEPTION 'swing_election_conflict';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'elections') LOOP
  failed_field=CASE
   WHEN trading.rec_keys(item,ARRAY['day','quantity','exercisingParty','submittedAt','effectiveAt','evidence']) IS NOT TRUE THEN 'elections'
   WHEN jsonb_typeof(item->'day')<>'string' OR item->>'day' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
    OR NOT pg_input_is_valid(item->>'day','date') THEN 'day'
   WHEN jsonb_typeof(item->'quantity') NOT IN('string','null') OR item->>'quantity' !~ '^(0|[1-9][0-9]{0,23})([.][0-9]{1,18})?$' THEN 'quantity'
   WHEN item->>'exercisingParty' IS DISTINCT FROM r.holder THEN 'exercisingParty'
   WHEN jsonb_typeof(item->'submittedAt')<>'string' OR item->>'submittedAt' !~ instant_pattern
    OR NOT pg_input_is_valid(item->>'submittedAt','timestamptz') THEN 'submittedAt'
   WHEN jsonb_typeof(item->'effectiveAt')<>'string' OR item->>'effectiveAt' !~ instant_pattern
    OR NOT pg_input_is_valid(item->>'effectiveAt','timestamptz') THEN 'effectiveAt'
   WHEN jsonb_typeof(item->'evidence')<>'string' OR length(btrim(item->>'evidence')) NOT BETWEEN 1 AND 500
    OR item->>'evidence' ~ U&'^[[:space:]\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]*$' THEN 'evidence' END;
  IF failed_field IS NULL THEN
   gas_day=(item->>'day')::date; amount=(item->>'quantity')::numeric; proof=btrim(item->>'evidence');
   submitted=(item->>'submittedAt')::timestamptz;effective=(item->>'effectiveAt')::timestamptz;
   SELECT d.exercise_at INTO deadline FROM trading.gas_swing_days d WHERE(d.catalog_id,d.instrument_id,d.ordinal,d.day)=(catalog,binding,right_no,gas_day);
   failed_field=CASE WHEN deadline IS NULL THEN 'day' WHEN submitted>effective OR effective>deadline THEN 'effectiveAt' WHEN amount*r.daily_minimum[2]<r.daily_minimum[1]*reference_quantity
     OR amount*r.daily_maximum[2]>r.daily_maximum[1]*reference_quantity THEN 'quantity' END;
  END IF;
  IF failed_field IS NOT NULL THEN
   field_hint=jsonb_build_object('field',failed_field,'row',(SELECT ord-1 FROM jsonb_array_elements(expected_heads) WITH ORDINALITY h(v,ord)
    WHERE v->>'day'=item->>'day'));
   RAISE EXCEPTION 'swing_election' USING HINT=field_hint::text,DETAIL=field_hint::text;
  END IF;
  SELECT * INTO prior FROM trading.gas_swing_elections e WHERE(e.catalog_id,e.trade_id,e.instrument_id,e.ordinal,e.day)=(catalog,trade,binding,right_no,gas_day)
   AND (e.quantity,e.submitted_at,e.effective_at,e.evidence) IS NOT DISTINCT FROM(amount,submitted,effective,proof);
  unchanged=prior.id IS NOT NULL;
  IF NOT unchanged THEN
   SELECT * INTO prior FROM trading.gas_swing_elections e WHERE(e.catalog_id,e.trade_id,e.instrument_id,e.ordinal,e.day)=(catalog,trade,binding,right_no,gas_day)
    ORDER BY revision DESC LIMIT 1;
   IF prior.effective_at>effective OR amount IS NULL AND prior.quantity IS NULL THEN RAISE EXCEPTION 'swing_election_conflict';END IF;
   IF prior.revision>=1000 THEN RAISE EXCEPTION 'swing_election';END IF;
   IF publication IS NULL THEN
    SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
    INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   END IF;
   INSERT INTO trading.gas_swing_elections VALUES(catalog,gen_random_uuid(),trade,binding,right_no,gas_day,coalesce(prior.revision,0)+1,
    prior.id,amount,r.holder,submitted,effective,proof,operation,publication) RETURNING * INTO prior;
  END IF;
  receipt_rows=array_append(receipt_rows,jsonb_build_object('day',gas_day,'revisionId',prior.id,'revision',prior.revision,'quantity',trim_scale(prior.quantity)::text,'unchanged',unchanged));
 END LOOP;
 IF publication IS NOT NULL THEN PERFORM trading.require_gas_swing_completion(catalog,trade,r,reference_quantity);END IF;
 result=jsonb_build_object('tradeId',trade,'instrumentId',binding,'operationId',operation,'right',right_no,'elections',to_jsonb(receipt_rows));
 INSERT INTO trading.gas_swing_commands(catalog_id,operation_id,trade_id,trade_revision,instrument_id,ordinal,actor_id,authority_until,request,recording_as_of,outcome,context_id)
  VALUES(catalog,operation,trade,head.revision,binding,right_no,caller.actor_id,until_at,body,as_of,result,record_context);
 PERFORM security.require_gas_swing_receipt(catalog,operation,until_at);
 UPDATE security.request_contexts SET market_catalog_id=caller.market_catalog_id,market_context_id=caller.market_context_id,
  trading_as_of=caller.trading_as_of,trading_business_date=caller.trading_business_date,calendar_cut=caller.calendar_cut,read_sources=caller.read_sources
  WHERE id=caller.id;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format OR invalid_parameter_value THEN RAISE EXCEPTION 'swing_election';
END$function$;

CREATE FUNCTION security.read_gas_swing_elections(catalog uuid,trade uuid,binding uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off SET timezone='UTC' AS $function$
DECLARE as_of timestamptz;result jsonb;
 until_at timestamptz=least(security.authority_until('ReadTrades'),security.authority_until('ReadMarketData'));
BEGIN
 -- The financial reader also checks the active worker lease and hosted session.
 -- Those checks may shorten the original grants' deadline.
 until_at=least(until_at,settlement_data.futures_margin_preview_authority(catalog));
 PERFORM security.require_gas_swing_binding(catalog,trade,binding);
 SELECT q.trading_as_of INTO as_of FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF as_of IS NULL THEN RAISE EXCEPTION 'swing_election_denied' USING ERRCODE='42501';END IF;
 WITH known AS MATERIALIZED(
  SELECT e.ordinal,e.day,e.revision,e.effective_at,e.id,jsonb_build_object('right',e.ordinal,'day',e.day,'revisionId',e.id,'previousId',e.previous_id,
   'revision',e.revision,'quantity',trim_scale(e.quantity)::text,'exercisingParty',e.exercising_party,'submittedAt',e.submitted_at,'effectiveAt',e.effective_at,
   'evidence',e.evidence,'knownFrom',k.known_from,'publicationOrder',e.publication_order) AS fact
   FROM trading.gas_swing_elections e JOIN trading.knowledge_receipts k USING(catalog_id,publication_order)
   WHERE(e.catalog_id,e.trade_id,e.instrument_id)=(catalog,trade,binding) AND security.trading_visible(catalog,e.publication_order,as_of)
 ), current_heads AS (
  SELECT DISTINCT ON(ordinal,day) ordinal,day,id FROM known ORDER BY ordinal,day,revision DESC
 ), selected AS (
  SELECT DISTINCT ON(ordinal,day) ordinal,day,fact FROM known WHERE effective_at<=as_of ORDER BY ordinal,day,revision DESC
 )
 SELECT jsonb_build_object('authorityUntil',until_at,'heads',coalesce((SELECT jsonb_agg(jsonb_build_object('right',d.ordinal,'day',d.day,'exerciseAt',d.exercise_at,'revisionId',h.id)
   ORDER BY d.ordinal,d.day) FROM trading.gas_swing_days d LEFT JOIN current_heads h USING(ordinal,day)
   WHERE(d.catalog_id,d.instrument_id)=(catalog,binding)),'[]'),
  'elections',coalesce((SELECT jsonb_agg(fact ORDER BY ordinal,day) FROM selected),'[]'),
  'nextElectionEffectiveAt',(SELECT min(effective_at) FROM known WHERE effective_at>as_of)) INTO result;
 PERFORM security.require_gas_swing_binding(catalog,trade,binding);
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 RETURN result;
END$function$;

ALTER FUNCTION security.gas_swing_write_until(uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_gas_swing_receipt(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.guard_gas_swing_commit() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_swing_write_until(uuid),security.require_gas_swing_receipt(uuid,uuid,timestamptz),trading.guard_gas_swing_commit() FROM PUBLIC;
ALTER FUNCTION security.require_gas_swing_binding(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.require_gas_swing_completion(uuid,uuid,trading.gas_swing_rights,numeric) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.record_gas_swing_elections(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.read_gas_swing_elections(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_swing_binding(uuid,uuid,uuid),trading.require_gas_swing_completion(uuid,uuid,trading.gas_swing_rights,numeric),
 security.record_gas_swing_elections(uuid,jsonb,timestamptz),security.read_gas_swing_elections(uuid,uuid,uuid) FROM PUBLIC;
COMMIT;
