-- Current consumer: native physical assistant reads and explicit dated facility questions.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $upgrade$
DECLARE signature text;definition text;before text:='runtime_set<>''turns_v15''';
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN
   RAISE EXCEPTION 'physical_assistant: unexpected runtime guard';
  END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v16''');
 END LOOP;
END $upgrade$;

-- The immutable ask date participates in retry equality before the ordinary capture,
-- publication gate and budget produce its certified knowledge cutoff.
DO $upgrade$
DECLARE definition text;before text:='IF t.knowledge_mode<>''operational'' THEN RETURN assistant.pin_market_turn(turn,token,effective_date,pins,reserve);END IF;';
BEGIN
 definition:=pg_get_functiondef('assistant.pin_turn(uuid,bytea,date,jsonb,integer)'::regprocedure);
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'physical_assistant: unexpected pin wrapper';END IF;
 EXECUTE replace(definition,before,$replacement$
 IF t.page_report ? 'businessDate' THEN
  IF(t.owner_id,t.tenant_id,t.workspace_id) IS DISTINCT FROM(c.actor_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
  IF t.page_report->>'page' IS DISTINCT FROM 'facility_quantities' OR jsonb_typeof(t.page_report->'businessDate')<>'string'
   OR t.page_report->>'businessDate'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR t.frame_id IS NOT NULL OR pins IS NOT NULL
   OR to_char((t.page_report->>'businessDate')::date,'YYYY-MM-DD')<>t.page_report->>'businessDate'
   OR t.knowledge_mode='current' AND effective_date IS DISTINCT FROM (t.page_report->>'businessDate')::date
   OR t.knowledge_mode='retained' AND NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts x
     WHERE x.id=t.requested_context AND x.catalog_id=t.catalog_id AND x.effective_on=(t.page_report->>'businessDate')::date)
   THEN RAISE EXCEPTION 'turn_input';END IF;
 ELSIF t.page_report->>'page'='facility_quantities' THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF t.knowledge_mode<>'operational' THEN RETURN assistant.pin_market_turn(turn,token,effective_date,pins,reserve);END IF;
$replacement$);
 definition:=pg_get_functiondef('assistant.is_navigation_target(assistant.turns,jsonb)'::regprocedure);
 before:='''users_access'',''settings_ai''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'physical_assistant: unexpected navigation pages';END IF;
 definition:=replace(definition,before,before||',''gas_setup'',''crude_setup''');
 before:='SELECT coalesce(CASE WHEN';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'physical_assistant: unexpected navigation context';END IF;
 EXECUTE replace(definition,before,'SELECT NOT(t.page_report ? ''businessDate'') AND coalesce(CASE WHEN');
END $upgrade$;
COMMIT;
