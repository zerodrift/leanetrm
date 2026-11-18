-- Personal chat search is explicitly authorized by the human on each ask. It never grants access to another
-- person's interactions or to business evidence that the current user/agent can no longer read.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;

CREATE TABLE assistant.turn_history_options(
 turn_id uuid PRIMARY KEY REFERENCES assistant.turns(id),enabled boolean NOT NULL
);
ALTER TABLE assistant.turn_history_options OWNER TO openetrm_security_owner;
ALTER TABLE assistant.turn_history_options ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.turn_history_options FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON assistant.turn_history_options TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.turn_history_options FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- This step is a runtime capability with per-question consent, deliberately absent from version-configurable tools.
ALTER TABLE assistant.tool_steps DROP CONSTRAINT tool_steps_tool_check,
 ADD CONSTRAINT tool_steps_tool_check CHECK(assistant.are_agent_tools(ARRAY[tool]) OR tool='search_chat_history');

-- Keep the existing admission contract and its serialization. The extra option commits in that same transaction;
-- an uncertain retry must name the original choice. Older callers and retained turns have no search authority.
CREATE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
 catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
 token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer,search_history boolean) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;turn uuid;enabled boolean;
BEGIN
 IF search_history IS NULL THEN RAISE EXCEPTION 'turn_input';END IF;
 result:=assistant.ask_turn(operation,scope,conversation,agent,agent_version,question,page_report,catalog,knowledge_mode,retained_context,frame,
  session_digest,profile,session_ends_at,token_digest,generation,runtime_set,question_limit,workspace_turns,admission_seconds);
 IF result ? 'turn' THEN
  turn:=(result->'turn'->>'id')::uuid;
  IF coalesce((result->>'admitted')::boolean,false) THEN
   INSERT INTO assistant.turn_history_options VALUES(turn,search_history);
   IF search_history THEN
    INSERT INTO assistant.turn_instruction_pins
    SELECT t.id,t.tenant_id,t.workspace_id,'tool:search_chat_history',
     (SELECT max(v.version) FROM assistant.page_prompt_versions v WHERE(v.tenant_id,v.workspace_id,v.page)=(t.tenant_id,t.workspace_id,'tool:search_chat_history'))
    FROM assistant.turns t WHERE t.id=turn ON CONFLICT DO NOTHING;
   END IF;
  ELSE
   SELECT coalesce((SELECT o.enabled FROM assistant.turn_history_options o WHERE o.turn_id=turn),false) INTO enabled;
   IF enabled<>search_history THEN RAISE EXCEPTION 'turn_conflict';END IF;
  END IF;
  result:=jsonb_set(result,'{turn,searchHistory}',to_jsonb(search_history));
 END IF;
 RETURN result;
END$$;

-- Expose the pinned option in state and offer the tool only for an explicitly opted-in question. It is a personal
-- interaction capability, rather than an administrator-enabled business-data permission on an agent version.
DO $upgrade$
DECLARE definition text;before text;signature text;
BEGIN
 signature:='assistant.turn_state(assistant.turns)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='''draft'',t.draft';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected turn state';END IF;
 EXECUTE replace(definition,before,'''searchHistory'',coalesce((SELECT o.enabled FROM assistant.turn_history_options o WHERE o.turn_id=t.id),false),'||before);
 signature:='assistant.start_turn(uuid,bytea)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='RETURN result;';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected start';END IF;
 EXECUTE replace(definition,before,
  'IF result ? ''started'' THEN result:=jsonb_set(result,''{started,tools}'',(result->''started''->''tools'')-''search_chat_history'');END IF;'
  ||'IF result ? ''started'' AND coalesce((SELECT o.enabled FROM assistant.turn_history_options o WHERE o.turn_id=turn),false) THEN'
  ||' result:=jsonb_set(result,''{started,tools}'',(result->''started''->''tools'')||''["search_chat_history"]''::jsonb);END IF;'||before);
 signature:='assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='NOT tool=ANY(v.tools)';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected step tools';END IF;
 definition:=replace(definition,before,'(tool=''search_chat_history'' AND NOT coalesce((SELECT o.enabled FROM assistant.turn_history_options o WHERE o.turn_id=t.id),false) OR NOT(tool=ANY(v.tools) OR tool=''search_chat_history''))');
 before:='jsonb_array_length(access)=0';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected step access';END IF;
 EXECUTE replace(definition,before,'(jsonb_array_length(access)=0 AND tool<>''search_chat_history'')');
 -- Guidance is configurable, but a version editor cannot authorize this turn-only personal capability.
 signature:='assistant.is_prompt_scope(text)';definition:=pg_get_functiondef(signature::regprocedure);before:='SELECT assistant.is_prompt_page(scope)';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected tool catalog';END IF;
 EXECUTE replace(definition,before,'SELECT scope=''tool:search_chat_history'' OR assistant.is_prompt_page(scope)');
END $upgrade$;

-- Preserve demand-based financial context acquisition, including unpinned text-only turns, in the new runtime.
DO $runtime$
DECLARE signature text;definition text;before text;c record;
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)',
  'assistant.start_turn_without_page_prompt(uuid,bytea)',
  'assistant.resolve_turn_context(uuid,bytea,date,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);before:='runtime_set<>''turns_v22''';
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected runtime %',signature;END IF;
  EXECUTE replace(definition,before,'runtime_set NOT IN(''turns_v22'',''turns_v23'')');
 END LOOP;
 signature:='assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)';definition:=pg_get_functiondef(signature::regprocedure);before:='runtime_set=''turns_v22''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'history_search: unexpected market pin';END IF;
 EXECUTE replace(definition,before,'runtime_set IN(''turns_v22'',''turns_v23'')');
 FOR c IN SELECT conname,pg_get_constraintdef(oid) definition FROM pg_constraint WHERE conrelid='assistant.turns'::regclass AND contype='c' LOOP
  IF position('runtime_set = ''turns_v22''::text' in c.definition)>0 THEN
   definition:=replace(c.definition,'runtime_set = ''turns_v22''::text','runtime_set IN(''turns_v22'',''turns_v23'')');
   EXECUTE format('ALTER TABLE assistant.turns DROP CONSTRAINT %I,ADD CONSTRAINT %I %s',c.conname,c.conname,definition);
  END IF;
 END LOOP;
END $runtime$;

CREATE FUNCTION assistant.search_history(turn uuid,phrase text,before_turn uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts;t assistant.turns;selected_scope text;cursor_at timestamptz;result jsonb;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 SELECT x.* INTO t FROM assistant.turns x JOIN assistant.agents a ON a.id=x.agent_id
  WHERE x.id=turn AND (x.tenant_id,x.workspace_id,x.delegation_id,a.principal_id)=(c.tenant_id,c.workspace_id,c.delegation_id,c.actor_id);
 IF t.id IS NULL OR t.state<>'running' OR NOT assistant.turn_authorized(t) OR NOT assistant.sources_accessible(t)
  OR NOT coalesce((SELECT o.enabled FROM assistant.turn_history_options o WHERE o.turn_id=t.id),false)
 THEN RAISE EXCEPTION 'history_search_disabled' USING ERRCODE='42501';END IF;
 IF phrase IS NULL OR length(phrase) NOT BETWEEN 1 AND 160 OR btrim(phrase)='' OR NOT assistant.is_plain_text(phrase)
 THEN RAISE EXCEPTION 'tool_arguments_invalid';END IF;
 SELECT k.scope INTO selected_scope FROM assistant.conversations k WHERE k.id=t.conversation_id;
 IF before_turn IS NOT NULL THEN
  SELECT x.asked_at INTO cursor_at FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
   WHERE x.id=before_turn AND(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(t.tenant_id,t.workspace_id,t.owner_id,selected_scope)
    AND x.ended_at<=t.asked_at;
  IF NOT FOUND THEN RAISE EXCEPTION 'tool_arguments_invalid';END IF;
 END IF;
 WITH candidates AS MATERIALIZED(
  SELECT x.* FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
   WHERE(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(t.tenant_id,t.workspace_id,t.owner_id,selected_scope)
    AND x.ended_at<=t.asked_at AND (before_turn IS NULL OR (x.asked_at,x.id)<(cursor_at,before_turn))
    AND strpos(lower(x.question||E'\n'||x.answer),lower(phrase))>0
    AND NOT EXISTS(SELECT 1 FROM assistant.turn_sources(x.id) s
     WHERE NOT assistant.agent_access(t,s.access,s.permission,s.market_catalog_id,s.reference_catalog_id))
   ORDER BY x.asked_at DESC,x.id DESC LIMIT 11
 ), hits AS MATERIALIZED(SELECT * FROM candidates ORDER BY asked_at DESC,id DESC LIMIT 10)
 SELECT jsonb_build_object('operation','assistant.history_search_v1','searchedBefore',t.asked_at,
  'matches',coalesce((SELECT jsonb_agg(jsonb_build_object('turnId',h.id,'conversationId',h.conversation_id,'ordinal',h.ordinal,
   'askedAt',h.asked_at,'state',h.state,'incomplete',h.incomplete,'context',assistant.turn_state(h)->'context',
   'questionExcerpt',substring(h.question FROM greatest(1,strpos(lower(h.question),lower(phrase))-120) FOR 500),
   'answerExcerpt',substring(h.answer FROM greatest(1,strpos(lower(h.answer),lower(phrase))-120) FOR 500),
   'questionTruncated',length(h.question)>500,'answerTruncated',length(h.answer)>500) ORDER BY h.asked_at DESC,h.id DESC) FROM hits h),'[]'::jsonb),
  'nextBefore',CASE WHEN (SELECT count(*) FROM candidates)>10 THEN(SELECT h.id FROM hits h ORDER BY h.asked_at,h.id LIMIT 1) END,
  'access',coalesce((SELECT jsonb_agg(jsonb_build_object('access',s.access,'permission',s.permission,'catalogId',coalesce(s.market_catalog_id,s.reference_catalog_id)))
   FROM(SELECT DISTINCT a.* FROM hits h CROSS JOIN LATERAL assistant.turn_sources(h.id) a)s),'[]'::jsonb)) INTO result;
 RETURN result;
END$$;

ALTER FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer,boolean) OWNER TO openetrm_security_owner;
ALTER FUNCTION assistant.search_history(uuid,text,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer,boolean),assistant.search_history(uuid,text,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer,boolean),assistant.search_history(uuid,text,uuid) TO openetrm_runtime;
COMMIT;
