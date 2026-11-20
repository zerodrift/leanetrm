-- Personal chat search is controlled by the user's saved preferences (default false) and recorded per turn on admission.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;

DO $upgrade$
DECLARE definition text;before text;signature text;
BEGIN
  signature:='presentation.valid_user_preferences(jsonb)';
  definition:=pg_get_functiondef(signature::regprocedure);
  before:='IF settings?''contractDisplay'' THEN';
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN
    RAISE EXCEPTION 'history_search_pref: unexpected valid_user_preferences';
  END IF;
  EXECUTE replace(definition,before,
    'IF settings?''assistantSearchHistory'' THEN'
    ||' IF jsonb_typeof(settings->''assistantSearchHistory'') IS DISTINCT FROM ''boolean'' THEN RETURN false;END IF;'
    ||' RETURN presentation.valid_user_preferences(settings-''assistantSearchHistory'');'
    ||' END IF; '||before);
END $upgrade$;

ALTER FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)
  RENAME TO ask_turn_core;
REVOKE ALL ON FUNCTION assistant.ask_turn_core(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)
  FROM PUBLIC,openetrm_runtime;

CREATE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
  catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
  token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;turn uuid;enabled boolean;
BEGIN
  result:=assistant.ask_turn_core(operation,scope,conversation,agent,agent_version,question,page_report,catalog,knowledge_mode,retained_context,frame,
    session_digest,profile,session_ends_at,token_digest,generation,runtime_set,question_limit,workspace_turns,admission_seconds);
  IF result ? 'turn' THEN
    turn:=(result->'turn'->>'id')::uuid;
    IF coalesce((result->>'admitted')::boolean,false) THEN
      SELECT coalesce((
        SELECT (v.preferences->>'assistantSearchHistory')::boolean
        FROM presentation.user_preference_versions v
        WHERE (v.tenant_id,v.workspace_id,v.principal_id,v.namespace)=(t.tenant_id,t.workspace_id,t.owner_id,scope)
        ORDER BY v.recorded_at DESC LIMIT 1
      ),false) INTO enabled
      FROM assistant.turns t WHERE t.id=turn;
      enabled:=coalesce(enabled,false);
      INSERT INTO assistant.turn_history_options VALUES(turn,enabled);
      IF enabled THEN
        INSERT INTO assistant.turn_instruction_pins
        SELECT t.id,t.tenant_id,t.workspace_id,'tool:search_chat_history',
          (SELECT max(v.version) FROM assistant.page_prompt_versions v WHERE (v.tenant_id,v.workspace_id,v.page)=(t.tenant_id,t.workspace_id,'tool:search_chat_history'))
        FROM assistant.turns t WHERE t.id=turn ON CONFLICT DO NOTHING;
      END IF;
    ELSE
      SELECT coalesce((SELECT o.enabled FROM assistant.turn_history_options o WHERE o.turn_id=turn),false) INTO enabled;
    END IF;
    result:=jsonb_set(result,'{turn,searchHistory}',to_jsonb(enabled));
  END IF;
  RETURN result;
END$$;

ALTER FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)
  OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)
  FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)
  TO openetrm_runtime;
DROP FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer,boolean);

COMMIT;
