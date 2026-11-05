-- Current consumers: identity-only page asks and their immutable prompt pins under turns_v15.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $pages$
DECLARE definition text;before text:='''blotter_new_lng'');';
BEGIN
 SELECT pg_get_functiondef(oid) INTO STRICT definition FROM pg_proc
 WHERE oid='assistant.is_prompt_page(text)'::regprocedure
 AND encode(sha256(convert_to(prosrc,'UTF8')),'hex')='9c8794b3199d7f4f8389d3702c941774311fbf265244a7bcc571483a00df88fc';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'assistant_report_catalog_predecessor';END IF;
 EXECUTE replace(definition,before,'''blotter_new_lng'',''dashboard'',''crude_setup'',''vol_surfaces'',''facility_quantities'');');
EXCEPTION WHEN no_data_found THEN RAISE EXCEPTION 'assistant_report_catalog_predecessor';
END $pages$;
DO $runtime$
DECLARE signature text;definition text;before text:='runtime_set<>''turns_v14''';
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'assistant_report_catalog_runtime';END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v15''');
 END LOOP;
END $runtime$;
COMMIT;
