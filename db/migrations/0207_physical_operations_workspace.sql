-- Physical work has its own page; native business commands retain their contracts.
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
  definition=pg_get_functiondef(signature::regprocedure);before='runtime_set<>''turns_v19''';
  IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'physical_operations_runtime_guard';END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v20''');
 END LOOP;
 definition=pg_get_functiondef('assistant.is_prompt_page(text)'::regprocedure);before='''facility_quantities'');';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'physical_operations_report_guard';END IF;
 EXECUTE replace(definition,before,'''facility_quantities'',''physical_operations'');');
 definition=pg_get_functiondef('assistant.is_navigation_target(assistant.turns,jsonb)'::regprocedure);before='''gas_setup'',''crude_setup''';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'physical_operations_navigation_guard';END IF;
 EXECUTE replace(definition,before,before||',''physical_operations''');
END $upgrade$;
COMMIT;
