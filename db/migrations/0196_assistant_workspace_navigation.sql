-- Current consumer: workspace page reads issue navigation handles for native entry walkthroughs under turns_v14.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $upgrade$
DECLARE signature text;definition text;before text:='runtime_set<>''turns_v13''';
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN
   RAISE EXCEPTION 'workspace_navigation: unexpected runtime guard';
  END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v14''');
 END LOOP;
END $upgrade$;
COMMIT;
