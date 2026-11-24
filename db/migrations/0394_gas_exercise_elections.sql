-- A physical exercise instruction belongs to the selected trade and retained group.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE TABLE trading.gas_exercise_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL,recording_as_of ref_data.instant NOT NULL,outcome jsonb NOT NULL,
 context_id uuid NOT NULL,input_business_on date NOT NULL,input_known_at ref_data.instant NOT NULL,input_system_id text NOT NULL,input_timeline integer NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,operation_id,trade_id,instrument_id,actor_id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 CHECK(input_timeline>0 AND context_id=security.workspace_context_id(catalog_id,input_business_on,input_known_at))
);
CREATE TRIGGER capture_inputs BEFORE INSERT ON trading.gas_exercise_commands FOR EACH ROW
 EXECUTE FUNCTION security.record_owner_read_inputs('catalog_id','context_id','input');

CREATE TABLE trading.gas_exercise_elections(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL,period_from date NOT NULL,
 revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,outcome text CHECK(outcome IN('exercised','abandoned')),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),effective_at ref_data.instant NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,instrument_id,ordinal,period_from,revision),
 UNIQUE(catalog_id,trade_id,instrument_id,ordinal,period_from,id),
 UNIQUE NULLS NOT DISTINCT(catalog_id,trade_id,instrument_id,ordinal,period_from,effective_at,outcome,evidence),
 FOREIGN KEY(catalog_id,instrument_id,ordinal,period_from) REFERENCES trading.gas_exercise_groups,
 FOREIGN KEY(catalog_id,trade_id,instrument_id,ordinal,period_from,previous_id)
  REFERENCES trading.gas_exercise_elections(catalog_id,trade_id,instrument_id,ordinal,period_from,id),
 FOREIGN KEY(catalog_id,operation_id,trade_id,instrument_id,actor_id)
  REFERENCES trading.gas_exercise_commands(catalog_id,operation_id,trade_id,instrument_id,actor_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK((revision=1)=(previous_id IS NULL) AND(outcome IS NOT NULL OR previous_id IS NOT NULL))
);

DO $access$
DECLARE t text;
BEGIN
 FOREACH t IN ARRAY ARRAY['gas_exercise_commands','gas_exercise_elections'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$access$;

CREATE FUNCTION security.record_gas_exercise_elections(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off SET timezone='UTC' AS $function$
DECLARE trade uuid;binding uuid;operation uuid;caller security.request_contexts;record_context uuid;head trading.gas_trade_revisions;
 command trading.gas_exercise_commands;prior trading.gas_exercise_elections;publication integer;
 item jsonb;group_ordinal integer;group_from date;effective timestamptz;expected uuid;decision text;proof text;
 deadline timestamptz;fact_count integer;unchanged boolean;receipt_rows jsonb[]=ARRAY[]::jsonb[];result jsonb;
BEGIN
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF trading.rec_keys(body,ARRAY['operationId','tradeId','instrumentId','expectedRevision','elections']) IS NOT TRUE
  OR as_of IS NULL OR NOT isfinite(as_of) OR pg_column_size(body)>2097152
  OR EXISTS(SELECT FROM unnest(ARRAY['operationId','tradeId','instrumentId']) k WHERE jsonb_typeof(body->k)<>'string')
  OR jsonb_typeof(body->'expectedRevision')<>'number' OR body->>'expectedRevision' !~ '^[1-9][0-9]{0,2}$'
  OR jsonb_typeof(body->'elections')<>'array' OR jsonb_array_length(body->'elections') NOT BETWEEN 1 AND 9600
 THEN RAISE EXCEPTION 'exercise_election';END IF;
 trade=(body->>'tradeId')::uuid;binding=(body->>'instrumentId')::uuid;operation=(body->>'operationId')::uuid;
 IF operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'exercise_election';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO STRICT caller FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 SELECT * INTO command FROM trading.gas_exercise_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF command.operation_id IS NULL THEN
  -- Capture working knowledge under the publication gate. Clients cannot attach an old cutoff to a new command.
  record_context=security.resolve_operation_context(catalog,(as_of AT TIME ZONE 'UTC')::date,NULL);
 ELSE
  IF (command.actor_id,command.request,command.recording_as_of) IS DISTINCT FROM(caller.actor_id,body,as_of) THEN RAISE EXCEPTION 'exercise_election_conflict';END IF;
  -- Recheck today's authority against the exact original binding, including a retry before this transaction commits.
  PERFORM security.register_owner_coordinates(catalog,command.context_id,command.input_business_on,command.input_known_at,command.input_system_id,command.input_timeline);
  record_context=command.context_id;
 END IF;
 PERFORM security.use_market_context(catalog,record_context);
 PERFORM security.use_trading_context(catalog,as_of);
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 IF security.gas_component_sources_visible(catalog,binding) IS NOT TRUE THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 IF command.operation_id IS NOT NULL THEN
  result=command.outcome;
 ELSE
  SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
  IF (head.revision,head.instrument_id) IS DISTINCT FROM((body->>'expectedRevision')::integer,binding)
   OR security.trading_visible(catalog,head.publication_order,head.effective_at) IS NOT TRUE THEN RAISE EXCEPTION 'exercise_election_conflict';END IF;
  IF EXISTS(SELECT FROM trading.instrument_side_schedules m JOIN trading.side_schedule_publications p USING(catalog_id,schedule_id)
    WHERE(m.catalog_id,m.instrument_id)=(catalog,binding) AND NOT security.trading_visible(catalog,p.publication_order,p.effective_at))
   OR EXISTS(SELECT FROM jsonb_array_elements(body->'elections') e GROUP BY e->'right',e->'fromDate' HAVING count(*)>1)
  THEN RAISE EXCEPTION 'exercise_election';END IF;
  IF (SELECT count(*) FROM trading.gas_exercise_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade))>=10000
   THEN RAISE EXCEPTION 'exercise_election_limit';END IF;
  SELECT count(*) INTO fact_count FROM trading.gas_exercise_elections e WHERE(e.catalog_id,e.trade_id)=(catalog,trade);
  FOR item IN SELECT value FROM jsonb_array_elements(body->'elections') LOOP
   IF trading.rec_keys(item,ARRAY['right','fromDate','outcome','effectiveAt','evidence','expectedPreviousId']) IS NOT TRUE
    OR jsonb_typeof(item->'right')<>'number' OR item->>'right' !~ '^(0|[1-9][0-9]{0,8})$'
    OR jsonb_typeof(item->'fromDate')<>'string' OR item->>'fromDate' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
    OR jsonb_typeof(item->'effectiveAt')<>'string' OR item->>'effectiveAt' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$'
    OR jsonb_typeof(item->'evidence')<>'string' OR length(btrim(item->>'evidence')) NOT BETWEEN 1 AND 500
    OR item->>'evidence' ~ U&'^[[:space:]\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]*$'
    OR jsonb_typeof(item->'expectedPreviousId') NOT IN('string','null') OR item->>'outcome' NOT IN('abandoned','exercised')
   THEN RAISE EXCEPTION 'exercise_election';END IF;
   group_ordinal=(item->>'right')::integer;group_from=(item->>'fromDate')::date;effective=(item->>'effectiveAt')::timestamptz;
   expected=(item->>'expectedPreviousId')::uuid;decision=item->>'outcome';proof=btrim(item->>'evidence');
   SELECT g.exercise_at INTO deadline FROM trading.gas_exercise_groups g JOIN trading.gas_exercise_rights r USING(catalog_id,instrument_id,ordinal)
    WHERE(g.catalog_id,g.instrument_id,g.ordinal,g.period_from,r.settlement)=(catalog,binding,group_ordinal,group_from,'physical');
   IF deadline IS NULL OR effective>deadline OR effective<=TIMESTAMPTZ '0001-01-01 00:00Z' THEN RAISE EXCEPTION 'exercise_election';END IF;
   -- A fresh command can identify an already superseded business fact. Returning its revision never changes the head.
   SELECT * INTO prior FROM trading.gas_exercise_elections e WHERE(e.catalog_id,e.trade_id,e.instrument_id,e.ordinal,e.period_from)=
    (catalog,trade,binding,group_ordinal,group_from)
    ORDER BY ((e.outcome,e.evidence,e.effective_at) IS NOT DISTINCT FROM(decision,proof,effective)) DESC,e.revision DESC LIMIT 1;
   unchanged=prior.id IS NOT NULL AND(prior.outcome,prior.evidence,prior.effective_at) IS NOT DISTINCT FROM(decision,proof,effective);
   IF NOT unchanged THEN
    IF prior.id IS DISTINCT FROM expected OR prior.effective_at>effective OR decision IS NULL AND prior.outcome IS NULL
     THEN RAISE EXCEPTION 'exercise_election_conflict';END IF;
    fact_count=fact_count+1;
    IF fact_count>10000 OR prior.revision>=1000 THEN RAISE EXCEPTION 'exercise_election_limit';END IF;
    IF publication IS NULL THEN
     SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
     INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
    END IF;
    INSERT INTO trading.gas_exercise_elections VALUES(catalog,gen_random_uuid(),trade,binding,group_ordinal,group_from,
      coalesce(prior.revision,0)+1,prior.id,decision,proof,effective,operation,caller.actor_id,publication) RETURNING * INTO prior;
   END IF;
   receipt_rows=array_append(receipt_rows,jsonb_build_object('right',group_ordinal,'fromDate',group_from,'revisionId',prior.id,'revision',prior.revision,'unchanged',unchanged));
  END LOOP;
  result=jsonb_build_object('tradeId',trade,'instrumentId',binding,'operationId',operation,'elections',to_jsonb(receipt_rows));
  INSERT INTO trading.gas_exercise_commands(catalog_id,operation_id,trade_id,trade_revision,instrument_id,actor_id,request,recording_as_of,outcome,context_id)
   VALUES(catalog,operation,trade,head.revision,binding,caller.actor_id,body,as_of,result,record_context);
 END IF;
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.gas_component_sources_visible(catalog,binding) IS NOT TRUE
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 -- Release only the coordinates this command borrowed. The caller keeps any previously registered read inputs.
 UPDATE security.request_contexts SET market_catalog_id=caller.market_catalog_id,market_context_id=caller.market_context_id,
  trading_as_of=caller.trading_as_of,trading_business_date=caller.trading_business_date,calendar_cut=caller.calendar_cut,read_sources=caller.read_sources
  WHERE id=caller.id;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format OR invalid_parameter_value THEN RAISE EXCEPTION 'exercise_election';
END$function$;

CREATE FUNCTION security.read_gas_exercise_elections(catalog uuid,trade uuid,instrument uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off SET timezone='UTC' AS $function$
DECLARE q security.request_contexts;head trading.gas_trade_revisions;job risk.jobs;result jsonb;
BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF q.market_catalog_id IS DISTINCT FROM catalog OR q.trading_as_of IS NULL
  OR NOT EXISTS(SELECT FROM security.operation_trading t WHERE(t.catalog_id,t.context_id)=(catalog,q.market_context_id))
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  IF risk.worker_read() IS NOT TRUE THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
  job=risk.bound_lifecycle_reader();
  IF (catalog,q.market_context_id,q.trading_as_of) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at)
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.trading_visible(catalog,r.publication_order,r.effective_at) ORDER BY revision DESC LIMIT 1;
 IF head.trade_id IS NULL OR head.instrument_id IS DISTINCT FROM instrument OR security.gas_component_sources_visible(catalog,instrument) IS NOT TRUE
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 IF job.id IS NOT NULL AND(job.request->'bookIds' @> jsonb_build_array(head.book_id) IS NOT TRUE
  OR NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL AND job.request->'tradeIds' @> jsonb_build_array(trade) IS NOT TRUE)
 THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 WITH known AS MATERIALIZED(
  SELECT e.* FROM trading.gas_exercise_elections e WHERE(e.catalog_id,e.trade_id,e.instrument_id)=(catalog,trade,instrument)
   AND security.trading_visible(catalog,e.publication_order,q.trading_as_of)
 ), heads AS(
  SELECT DISTINCT ON(ordinal,period_from) * FROM known WHERE effective_at<=q.trading_as_of ORDER BY ordinal,period_from,revision DESC
 )
 SELECT jsonb_build_object('elections',coalesce((SELECT jsonb_agg(jsonb_build_object('right',ordinal,'fromDate',period_from,'revisionId',id,
  'revision',revision,'outcome',outcome,'effectiveAt',effective_at,'evidence',evidence) ORDER BY ordinal,period_from) FROM heads),'[]'::jsonb),
  'nextElectionEffectiveAt',(SELECT min(effective_at) FROM known WHERE effective_at>q.trading_as_of)) INTO result;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 IF security.gas_component_sources_visible(catalog,instrument) IS NOT TRUE OR job.id IS NOT NULL AND risk.worker_read() IS NOT TRUE
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 IF job.id IS NOT NULL THEN PERFORM risk.bound_lifecycle_reader();END IF;
 RETURN result;
END$function$;
ALTER FUNCTION security.record_gas_exercise_elections(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.read_gas_exercise_elections(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_gas_exercise_elections(uuid,jsonb,timestamptz),security.read_gas_exercise_elections(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.record_gas_exercise_elections(uuid,jsonb,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_gas_exercise_elections(uuid,uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
