-- The lifecycle page uses the ordinary native page/turn admission and the same
-- product-owned operations as interactive users. No additional financial journal.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $upgrade$
DECLARE signature text;definition text;before text;
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition=pg_get_functiondef(signature::regprocedure);before='runtime_set<>''turns_v20''';
  IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_runtime_guard';END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v21''');
 END LOOP;
 FOREACH signature IN ARRAY ARRAY['assistant.is_prompt_page(text)','assistant.is_navigation_target(assistant.turns,jsonb)'] LOOP
  definition=pg_get_functiondef(signature::regprocedure);before='''physical_operations''';
  IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_page_guard';END IF;
  EXECUTE replace(definition,before,before||',''lifecycle''');
 END LOOP;
END $upgrade$;
COMMIT;
