-- Current consumer: explicit controls-only digests and unsaved swap presentation fields.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $guard$
DECLARE helper record;
BEGIN
 FOR helper IN SELECT * FROM (VALUES
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)','dfddb70bd8396dddc8002d4ef228af6100ccee07de7e8ec8b7d90d0119897dbc','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog","TimeZone=UTC"]'::jsonb,true),
  ('assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)','bce6a4a6c2efe1fb61ded4e8b436a36d3fb00d60f8df0da5fbbfd1c92054beb2','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb,true),
  ('assistant.is_walkthrough(assistant.turns,jsonb)','fa84468681f2c099c2de46135787c00cad7c404288ebf09221f56f77d58ed195','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb,false)
 ) AS reviewed(signature,hash,acl,config,definer) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=helper.signature::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND coalesce(to_jsonb(p.proacl),'null'::jsonb)=helper.acl
   AND coalesce(to_jsonb(p.proconfig),'null'::jsonb)=helper.config AND p.prosecdef=helper.definer) THEN
   RAISE EXCEPTION 'presentation_assistant_upgrade: unexpected helper %',helper.signature;
  END IF;
 END LOOP;
END $guard$;
DO $upgrade$
DECLARE signature text;definition text;before text:='runtime_set<>''turns_v16''';
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN
   RAISE EXCEPTION 'presentation_assistant: unexpected runtime guard';
  END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v17''');
 END LOOP;
END $upgrade$;

DO $upgrade$
DECLARE definition text;before text:='''ticket.lots'',''ticket.executionPrice'',''ticket.memo''';
BEGIN
 definition:=pg_get_functiondef('assistant.is_walkthrough(assistant.turns,jsonb)'::regprocedure);
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'presentation_assistant: unexpected fields';END IF;
 EXECUTE replace(definition,before,before||',''swap.quantity'',''swap.fixedPrice'',''swap.memo'',''swap.structure'',''swap.fixedDirection'',''swap.priceMode''');
END $upgrade$;
COMMIT;
