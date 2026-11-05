-- Current consumer: present_walkthrough publishes a bounded immutable presentation plan.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $$DECLARE signature text;definition text;before text;BEGIN
 signature:='assistant.agent_tools()';definition:=pg_get_functiondef(signature::regprocedure);
 before:='(''read_page_digest'',ARRAY[''UseAssistantWorkspace''])';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'walkthrough: unexpected tool catalog';END IF;
 EXECUTE replace(definition,before,'(''present_walkthrough'',ARRAY[''UseAssistantWorkspace'']),'||before);
 signature:='assistant.covering_grant(security.request_contexts,uuid,integer)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='tool IN(''read_page_digest'')';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'walkthrough: unexpected grant coverage';END IF;
 EXECUTE replace(definition,before,'tool IN(''read_page_digest'',''present_walkthrough'')');
END$$;
ALTER TABLE assistant.turn_events DROP CONSTRAINT turn_events_kind_check,
 ADD CONSTRAINT turn_events_kind_check CHECK(kind IN('accepted','text','step','navigation','walkthrough','usage','completed','failed','cancelled','interrupted'));

-- The managed validator resolves handles, successful reads, native identities and exact ask evidence.
-- Storage independently refuses unknown vocabulary, commit fields and oversized/partial plans.
CREATE FUNCTION assistant.is_walkthrough(t assistant.turns,plan jsonb) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE s jsonb;keys text[];page text:=t.page_report->>'page';
BEGIN
 IF jsonb_typeof(plan) IS DISTINCT FROM 'object' OR octet_length(plan::text)>32768
  OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM jsonb_object_keys(plan) k)<>ARRAY['context','pageReport','steps']
  OR plan->'pageReport' IS DISTINCT FROM t.page_report
  OR plan->'context' IS DISTINCT FROM jsonb_build_object('mode',t.knowledge_mode,'contextId',t.context_id,'frameId',t.frame_id)
  OR jsonb_typeof(plan->'steps') IS DISTINCT FROM 'array' THEN RETURN false;END IF;
 IF jsonb_array_length(plan->'steps') NOT BETWEEN 1 AND 12 THEN RETURN false;END IF;
 FOR s IN SELECT value FROM jsonb_array_elements(plan->'steps') LOOP
  IF jsonb_typeof(s) IS DISTINCT FROM 'object' THEN RETURN false;END IF;
  SELECT array_agg(k ORDER BY k COLLATE "C") INTO keys FROM jsonb_object_keys(s) k;
  IF s->>'kind'='go' THEN
   IF keys<>ARRAY['kind','target'] OR NOT assistant.is_navigation_target(t,s->'target') THEN RETURN false;END IF;
   page:=s->'target'->>'page';
  ELSE
   IF jsonb_typeof(s->'page') IS DISTINCT FROM 'string' OR s->>'page' IS DISTINCT FROM page THEN RETURN false;END IF;
   CASE s->>'kind'
    WHEN 'pause' THEN IF keys<>ARRAY['kind','page'] THEN RETURN false;END IF;
    WHEN 'say' THEN IF keys<>ARRAY['kind','page','text'] OR jsonb_typeof(s->'text') IS DISTINCT FROM 'string' OR length(s->>'text') NOT BETWEEN 1 AND 200 OR NOT assistant.is_plain_text(s->>'text') THEN RETURN false;END IF;
    WHEN 'scroll_to' THEN IF keys<>ARRAY['kind','page','widget'] OR s->>'widget' NOT IN('page','positions','details','charts') THEN RETURN false;END IF;
    WHEN 'sort' THEN IF keys<>ARRAY['column','direction','kind','page'] OR s->>'direction' NOT IN('asc','desc') THEN RETURN false;END IF;
    WHEN 'filter' THEN IF keys<>ARRAY['field','kind','page','value'] OR jsonb_typeof(s->'value') IS DISTINCT FROM 'string' OR length(s->>'value')>200 THEN RETURN false;END IF;
    WHEN 'select' THEN IF keys<>ARRAY['focus','kind','page'] OR jsonb_typeof(s->'focus') IS DISTINCT FROM 'object' THEN RETURN false;END IF;
    WHEN 'open' THEN IF keys<>ARRAY['kind','page','pane','proposalStep','target'] OR s->>'pane' NOT IN('details','charts','new_future','review')
      OR (s->'target'<>'null'::jsonb AND NOT assistant.is_navigation_target(t,s->'target')) THEN RETURN false;END IF;
    WHEN 'fill' THEN IF keys<>ARRAY['field','kind','page','value'] OR s->>'field' NOT IN('ticket.lots','ticket.executionPrice','ticket.memo')
      OR jsonb_typeof(s->'value') IS DISTINCT FROM 'string' OR length(s->>'value') NOT BETWEEN 1 AND 200 THEN RETURN false;END IF;
    ELSE RETURN false;
   END CASE;
  END IF;
 END LOOP;
 RETURN true;
END$$;

CREATE FUNCTION assistant.record_tool_step(turn uuid,token bytea,tool text,operation text,input jsonb,code text,read_at timestamptz,access jsonb,target jsonb,plan jsonb)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t assistant.turns;result jsonb;published bigint;label text;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 t:=assistant.fenced_turn(turn,token);
 IF (plan IS NOT NULL)<>(tool='present_walkthrough' AND code IS NULL)
  OR plan IS NOT NULL AND(operation IS DISTINCT FROM 'workspace.walkthrough_v1' OR t.runtime_set<>'turns_v13' OR NOT assistant.is_walkthrough(t,plan)) THEN RAISE EXCEPTION 'turn_input';END IF;
 result:=assistant.record_tool_step(turn,token,tool,operation,input,code,read_at,access,target);
 IF plan IS NOT NULL AND result ? 'step' THEN
  SELECT a.name INTO label FROM assistant.agents a WHERE a.id=t.agent_id;
  published:=assistant.turn_event(t,'walkthrough',jsonb_build_object('ordinal',result->'step','agentLabel',label,'plan',plan));
  result:=jsonb_set(result,'{sequence}',to_jsonb(published));
 END IF;
 RETURN result;
END$$;
CREATE OR REPLACE FUNCTION assistant.owner_event(e assistant.turn_events,hidden text) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('sequence',e.sequence,'turnId',e.turn_id,'kind',e.kind,
  'payload',CASE WHEN e.kind IN('text','navigation','walkthrough') AND hidden IS NOT NULL THEN jsonb_build_object('hidden',hidden) ELSE e.payload END,'at',e.recorded_at);
$$;
ALTER FUNCTION assistant.is_walkthrough(assistant.turns,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.is_walkthrough(assistant.turns,jsonb) FROM PUBLIC;
ALTER FUNCTION assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb) TO openetrm_runtime;
COMMIT;
