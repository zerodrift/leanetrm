-- Current consumers: digest tool admission, explicit tool consent, Looking at and runtime enforcement.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $upgrade$
DECLARE signature text;definition text;before text;after text;
BEGIN
 signature:='assistant.agent_tools()';definition:=pg_get_functiondef(signature::regprocedure);
 before:='(''read_workspace_page'',ARRAY[''UseAssistantWorkspace''])';
 after:='(''read_page_digest'',ARRAY[''UseAssistantWorkspace'']),'||before;
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'page_digest: unexpected tool catalog';END IF;
 EXECUTE replace(definition,before,after);
 signature:='assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)';
 definition:=pg_get_functiondef(signature::regprocedure);before:='runtime_set<>''turns_v12''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'page_digest: unexpected runtime admission';END IF;
 EXECUTE replace(definition,before,'runtime_set<>''turns_v13''');
 signature:='assistant.covering_grant(security.request_contexts,uuid,integer)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHERE a.id=covering_grant.agent AND';
 after:='WHERE NOT EXISTS(SELECT 1 FROM unnest(v.tools) tool WHERE tool IN(''read_page_digest'')'
  ||' AND (SELECT tool=ANY(granted.tools) FROM assistant.agent_versions granted WHERE(granted.agent_id,granted.version)=(g.agent_id,g.agent_version)) IS NOT TRUE) AND a.id=covering_grant.agent AND';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'page_digest: unexpected grant coverage';END IF;
 EXECUTE replace(definition,before,after);
 signature:='assistant.usable_agents()';definition:=pg_get_functiondef(signature::regprocedure);
 before:='''permissions'',to_jsonb(assistant.version_permissions(a.id,v.version))';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'page_digest: unexpected usable versions';END IF;
 EXECUTE replace(definition,before,'''tools'',to_jsonb(v.tools),'||before);
END $upgrade$;
COMMIT;
