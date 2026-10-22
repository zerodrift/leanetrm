-- Page-assistant navigation targets (docs/ai-assistant.md §7.1, §7.3, §8). A Navigate step that issued a target records
-- Navigate's operation, the instant its page was checked and the accesses it was checked under, as a read's step does,
-- and its target is published as a navigation event right after the step's own, in the same transaction and under the
-- same publication recheck. A target names one of the pages the assistant reads, the page itself or a selection, keeps
-- the turn's pinned context and carries a plain-text label; once the replay recheck hides a turn's answer, its owner's
-- reads show each of its targets as the hidden marker, as they show its text.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the turn, step and text helpers the step relies on and the definitions this migration replaces, each with exactly
-- the privileges it was written against.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.is_plain_text(text)','aec11d6028a08ad4695caef4ed44c07176694a7a9f58e94304990a3cb2b35d0e',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.are_agent_tools(text[])','f2cc8a152599d7af31757be2dfce216bb6269f84fa4039f9718db7e2ba16b883',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.turn_state(assistant.turns)','c72f829f3c6541582b6a5d428d7a7a3dcbb002096807e7e4161d4b683c2b174e',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.turn_event(assistant.turns,text,jsonb)','2eaeaf1f25ffb0df425e16de67dc120f292a4555e323f61d38cae90e0c882f67',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.unclosed_call(uuid)','e027c4e8d99ffda89af66b6e95167bf335929929f8a61d069cd6ddcac7ff391f',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.turn_authorized(assistant.turns)','d42e6b9389fad6033cfc6526e2c68d17f70a09b4a07c3e940d6842bcd2e311a9',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.fenced_turn(uuid,bytea)','11646406f0f2eedc699b5bdf3cfa6f4fcb7dbe04b35c9a6ef17f320094b0f968',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.is_read_access(jsonb)','c1704bb394f64b7dbdf7cc503a667468abb6a49d148dafc17c41d5c0232eefb4',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.sources_accessible(assistant.turns)','9a675ae2c427682dbba4a398499785ffbceb4018068f9fb4904213218c7b055c',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamp with time zone,jsonb)','9393213bb3c2d368bf51e48ce409a878de38bf69111a8dd59c994a2984d54859',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.owner_event(assistant.turn_events,text)','eb42367dd29af2035a42db9f6d63ce37173ecb554d4704a3db9fc7d4a126ea21',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> helper.acl::aclitem[] AND p.proacl <@ helper.acl::aclitem[])
  THEN RAISE EXCEPTION 'navigation_targets: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- Each target is published as an event of its own (§8).
ALTER TABLE assistant.turn_events DROP CONSTRAINT turn_events_kind_check,
 ADD CONSTRAINT turn_events_kind_check CHECK(kind IN('accepted','text','step','navigation','usage','completed','failed','cancelled','interrupted'));

-- A target a Navigate step issues in a turn (§7.3): exactly its page, selection, context, kind and label. The page is one
-- of the seven the assistant reads; a page target selects nothing, opening the page as it opens, a record target, a curve
-- or a trade, names a selection on Curves & prices or the Blotter, and a filter target one on any of them, each an object
-- of at most 8 KiB; the context is the turn's pinned one, its knowledge mode, the context its reads bind and its As of
-- frame or none; and the label is plain text of 1 to 200 characters.
CREATE FUNCTION assistant.is_navigation_target(t assistant.turns,target jsonb) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(CASE WHEN jsonb_typeof(target) IS DISTINCT FROM 'object'
   OR(SELECT array_agg(k ORDER BY k COLLATE "C") FROM jsonb_object_keys(target) k)<>ARRAY['context','kind','label','page','selection'] THEN false
  ELSE jsonb_typeof(target->'page')='string'
   AND target->>'page' IN('curves','contract_definitions','schedules','power_tous','reference_data','parties','blotter')
   AND jsonb_typeof(target->'kind')='string' AND CASE target->>'kind'
    WHEN 'page' THEN jsonb_typeof(target->'selection')='null'
    WHEN 'record' THEN target->>'page' IN('curves','blotter') AND jsonb_typeof(target->'selection')='object'
     AND octet_length((target->'selection')::text)<=8192
    WHEN 'filter' THEN jsonb_typeof(target->'selection')='object' AND octet_length((target->'selection')::text)<=8192
    ELSE false END
   AND target->'context'=jsonb_build_object('mode',t.knowledge_mode,'contextId',t.context_id,'frameId',t.frame_id)
   AND jsonb_typeof(target->'label')='string' AND length(target->>'label') BETWEEN 1 AND 200 AND assistant.is_plain_text(target->>'label') END,false);
$$;

-- As in 0072, and a Navigate step that issued a target names it (§7.3, §8): only a Navigate step without a code carries
-- one, and it names Navigate's check as a read names its read: its operation, navigation.target_v1, the instant and the
-- accesses. Once the publication recheck passes, the target is published as a navigation event right after the step's
-- own, carrying the step's number and the target. The answer's sequence is the last event the step published.
DROP FUNCTION assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb);
CREATE FUNCTION assistant.record_tool_step(turn uuid,token bytea,tool text,operation text,input jsonb,code text,read_at timestamptz,access jsonb,
 target jsonb) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns;v assistant.agent_versions;last assistant.model_calls;ended assistant.model_call_outcomes;earlier integer;answered integer;
 number integer;published bigint;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 t:=assistant.fenced_turn(turn,token);
 SELECT x.* INTO v FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(t.agent_id,t.agent_version);
 IF tool IS NULL OR NOT tool=ANY(v.tools) OR code!~'^[a-z][a-z0-9_]{0,63}$'
  OR(CASE WHEN input IS NULL THEN code IS DISTINCT FROM 'tool_arguments_invalid' ELSE jsonb_typeof(input)<>'object' OR octet_length(input::text)>8192 END)
  OR(CASE WHEN operation IS NULL THEN read_at IS NOT NULL OR access IS NOT NULL
   ELSE operation!~'^[a-z][a-z0-9_]{0,63}([.][a-z][a-z0-9_]{0,63})*$' OR read_at IS NULL OR read_at>clock_timestamp()
    OR jsonb_typeof(access) IS DISTINCT FROM 'array' END)
  OR(target IS NOT NULL)<>(tool='navigate' AND code IS NULL)
  OR target IS NOT NULL AND(operation IS DISTINCT FROM 'navigation.target_v1' OR NOT assistant.is_navigation_target(t,target)) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF operation IS NOT NULL AND(jsonb_array_length(access)=0 OR EXISTS(SELECT 1 FROM jsonb_array_elements(access) a WHERE assistant.is_read_access(a) IS NOT TRUE)) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF t.state NOT IN('running','cancel_requested') OR assistant.unclosed_call(t.id) IS NOT NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(t));END IF;
 SELECT x.* INTO last FROM assistant.model_calls x WHERE x.turn_id=t.id ORDER BY x.attempt DESC LIMIT 1;
 SELECT o.* INTO ended FROM assistant.model_call_outcomes o WHERE o.call_id=last.id;
 SELECT count(*) FILTER(WHERE s.call_id<>last.id),count(*) FILTER(WHERE s.call_id=last.id) INTO earlier,answered
  FROM assistant.tool_steps s WHERE s.turn_id=t.id;
 IF ended.stop IS DISTINCT FROM 'tool_use' OR answered>=ended.tool_requests THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF earlier+ended.tool_requests>v.max_tool_steps THEN RAISE EXCEPTION 'limit_tool_steps';END IF;
 IF read_at<ended.closed_at THEN RAISE EXCEPTION 'turn_input';END IF;
 IF t.publication_refused_at IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(t),'refused','access_changed');END IF;
 number:=earlier+answered+1;
 INSERT INTO assistant.tool_steps VALUES(t.id,number,last.id,tool,operation,input,code,read_at,clock_timestamp());
 INSERT INTO assistant.tool_read_access SELECT DISTINCT t.id,number,a->>'access',a->>'permission',
   CASE WHEN a->>'access'<>'reference_access' THEN(a->>'catalogId')::uuid END,CASE WHEN a->>'access'='reference_access' THEN(a->>'catalogId')::uuid END
  FROM jsonb_array_elements(coalesce(access,'[]')) a;
 IF NOT assistant.turn_authorized(t) OR NOT assistant.sources_accessible(t) THEN
  UPDATE assistant.turns x SET publication_refused_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
  RETURN jsonb_build_object('turn',assistant.turn_state(t),'refused','access_changed');
 END IF;
 published:=assistant.turn_event(t,'step',
  jsonb_build_object('ordinal',number,'tool',tool)||CASE WHEN code IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('code',code) END);
 IF target IS NOT NULL THEN published:=assistant.turn_event(t,'navigation',jsonb_build_object('ordinal',number,'target',target));END IF;
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'step',number,'sequence',published);
END $$;

-- As in 0078, a navigation event's target hidden like a text's, by the code that hides its turn's answer.
CREATE OR REPLACE FUNCTION assistant.owner_event(e assistant.turn_events,hidden text) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('sequence',e.sequence,'turnId',e.turn_id,'kind',e.kind,
  'payload',CASE WHEN e.kind IN('text','navigation') AND hidden IS NOT NULL THEN jsonb_build_object('hidden',hidden) ELSE e.payload END,'at',e.recorded_at);
$$;

DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['assistant.is_navigation_target(assistant.turns,jsonb)',
   'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb) TO openetrm_runtime;
COMMIT;
