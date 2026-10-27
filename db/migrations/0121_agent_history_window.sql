-- Each agent version states its history window (D249): the most earlier turns of its conversation, 0 to 1,000, a turn
-- sends the model. An ask includes the latest of them by ordinal, withholding as before any whose sources are no longer
-- readable, and records those it includes; a turn before the window is neither included nor reported withheld. The
-- version's input-token limit still bounds what is sent.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Fail closed if a definition this migration replaces differs from the reviewed one, in its text or its privileges.
DO $$
DECLARE replaced record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR replaced IN SELECT * FROM(VALUES
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,integer,integer,integer)',
   'e3e94a21eeb3250c9c36c138bff687b710ca4a31ede1433c51bd58e798ebd835',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.agent_version_state(uuid,integer)','97f2027514913b08830404a41cd6777208c127169e2a8358b0063da17895c067',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer)',
   '42ed3133e9d063d496262f8935c2c2aabd4a820a746c0b027ef73c9b09acc77a',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(replaced.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=replaced.hash
   AND p.proacl=replaced.acl::aclitem[])
  THEN RAISE EXCEPTION 'agent_history_window: unexpected helper %',replaced.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
ALTER TABLE assistant.agent_versions ADD COLUMN history_turns integer;
-- A version saved before this one sent every earlier turn. It takes the largest window, which holds every earlier turn of
-- every conversation of up to 1,001 turns; the table's exclusive lock keeps out asks, so no conversation is longer than it
-- checks here, and one already longer, whose next turn would send fewer, refuses the migration. The immutable guard is
-- restored in this transaction while that lock keeps out every other read and write.
DO $$
BEGIN
 IF EXISTS(SELECT 1 FROM assistant.turns GROUP BY conversation_id HAVING count(*)>1000) THEN
  RAISE EXCEPTION 'agent_history_window: a conversation has more than 1,000 turns' USING ERRCODE='55000';
 END IF;
END$$;
ALTER TABLE assistant.agent_versions DISABLE TRIGGER immutable_agent_version;
UPDATE assistant.agent_versions SET history_turns=1000;
ALTER TABLE assistant.agent_versions ENABLE TRIGGER immutable_agent_version;
ALTER TABLE assistant.agent_versions ALTER COLUMN history_turns SET NOT NULL,ADD CHECK(history_turns BETWEEN 0 AND 1000);

-- As in 0120, with the version's history window.
DROP FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer);
CREATE FUNCTION assistant.save_agent_version(operation uuid,agent uuid,expected integer,description text,instructions text,model uuid,
 model_version integer,reasoning text,caching boolean,tools text[],allowed_roles text[],tool_steps integer,input_tokens bigint,output_tokens integer,
 elapsed_seconds integer,read_seconds integer,retries integer,history_turns integer,grant_days integer,instructions_limit integer,grant_days_limit integer)
 RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();previous jsonb;a assistant.agents;v assistant.model_versions;before_value jsonb;after_value jsonb;
 next_version integer;
 request jsonb:=jsonb_build_object('action','save_agent','operationId',operation,'agentId',agent,'expectedVersion',expected,'description',description,'instructions',instructions,
  'modelId',model,'modelVersion',model_version,'reasoning',reasoning,'caching',caching,'tools',to_jsonb(tools),'allowedRoles',to_jsonb(allowed_roles),
  'limits',jsonb_build_object('toolSteps',tool_steps,'inputTokens',input_tokens,'outputTokens',output_tokens,'elapsedSeconds',elapsed_seconds,'readSeconds',read_seconds,
   'retries',retries,'historyTurns',history_turns),
  'grantDays',grant_days);
BEGIN
 previous:=assistant.agent_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 -- The runtime set's bounds apply first, so instructions beyond them are too long whatever else they hold.
 IF instructions_limit IS NULL OR grant_days_limit IS NULL THEN RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';END IF;
 IF length(instructions)>instructions_limit THEN RAISE EXCEPTION 'instructions_too_long' USING ERRCODE='P0001';END IF;
 IF grant_days>grant_days_limit THEN RAISE EXCEPTION 'grant_duration_exceeded' USING ERRCODE='P0001';END IF;
 IF description IS NULL OR NOT(length(description) BETWEEN 1 AND 500 AND assistant.is_plain_text(description)) OR instructions IS NULL OR NOT assistant.is_instructions(instructions)
  OR model IS NULL OR model='00000000-0000-0000-0000-000000000000'::uuid OR model_version IS NULL OR reasoning IS NULL OR caching IS NULL
  OR NOT assistant.are_agent_tools(tools) OR allowed_roles IS NULL OR array_position(allowed_roles,NULL) IS NOT NULL
  OR cardinality(allowed_roles)<>(SELECT count(DISTINCT r) FROM unnest(allowed_roles)r)
  OR EXISTS(SELECT 1 FROM unnest(allowed_roles)r WHERE NOT EXISTS(SELECT 1 FROM security.roles x WHERE x.code=r))
  OR tool_steps IS NULL OR tool_steps NOT BETWEEN 1 AND 100 OR input_tokens IS NULL OR input_tokens NOT BETWEEN 1 AND 100000000
  OR output_tokens IS NULL OR output_tokens NOT BETWEEN 1 AND 10000000 OR elapsed_seconds IS NULL OR elapsed_seconds NOT BETWEEN 1 AND 3600
  OR read_seconds IS NULL OR read_seconds NOT BETWEEN 1 AND 30 OR read_seconds>elapsed_seconds
  OR retries IS NULL OR retries NOT BETWEEN 0 AND 10 OR history_turns IS NULL OR history_turns NOT BETWEEN 0 AND 1000
  OR grant_days IS NULL OR grant_days<1 THEN
  RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';
 END IF;
 a:=assistant.agent_for_change(c,agent,expected);
 PERFORM assistant.check_agent_model(c,model,model_version);
 SELECT x.* INTO v FROM assistant.model_versions x WHERE(x.model_id,x.version)=(model,model_version);
 IF NOT reasoning=ANY(v.reasoning_efforts) THEN RAISE EXCEPTION 'parameter_required' USING ERRCODE='P0001';END IF;
 IF output_tokens>v.output_limit THEN RAISE EXCEPTION 'model_limit_exceeded' USING ERRCODE='P0001';END IF;
 before_value:=assistant.agent_state(agent);
 next_version:=coalesce((SELECT max(x.version) FROM assistant.agent_versions x WHERE x.agent_id=agent),0)+1;
 INSERT INTO assistant.agent_versions(agent_id,version,description,instructions,model_id,model_version,reasoning,caching,tools,max_tool_steps,
  max_input_tokens,max_output_tokens,max_elapsed_seconds,max_read_seconds,max_retries,history_turns,grant_days,saved_by,saved_at)
  VALUES(agent,next_version,description,instructions,model,model_version,reasoning,caching,tools,
  tool_steps,input_tokens,output_tokens,elapsed_seconds,read_seconds,retries,history_turns,grant_days,c.actor_id,clock_timestamp());
 INSERT INTO assistant.agent_version_roles SELECT agent,next_version,r.id FROM security.roles r WHERE r.code=ANY(allowed_roles) ORDER BY r.code;
 UPDATE assistant.agents x SET version=x.version+1 WHERE x.id=agent;
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'save_agent',request,before_value,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;

-- As in 0120, with the version's history window.
CREATE OR REPLACE FUNCTION assistant.agent_version_state(agent uuid,agent_version integer) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('version',v.version,'description',v.description,'instructions',v.instructions,
  'model',jsonb_build_object('id',m.id,'version',v.model_version,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,
   'current',v.model_version=(SELECT c.version FROM assistant.current_model_version(m.id) c)),
  'reasoning',v.reasoning,'caching',v.caching,'tools',coalesce((SELECT jsonb_agg(t ORDER BY t) FROM unnest(v.tools)t),'[]'::jsonb),
  'allowedRoles',coalesce((SELECT jsonb_agg(r.code ORDER BY r.code) FROM assistant.agent_version_roles a JOIN security.roles r ON r.id=a.role_id
   WHERE(a.agent_id,a.version)=(v.agent_id,v.version)),'[]'::jsonb),
  'limits',jsonb_build_object('toolSteps',v.max_tool_steps,'inputTokens',v.max_input_tokens,'outputTokens',v.max_output_tokens,
   'elapsedSeconds',v.max_elapsed_seconds,'readSeconds',v.max_read_seconds,'retries',v.max_retries,'historyTurns',v.history_turns),
  'grantDays',v.grant_days,'savedBy',s.display_name,'savedAt',v.saved_at)
 FROM assistant.agent_versions v JOIN assistant.agents g ON g.id=v.agent_id JOIN assistant.models m ON m.id=v.model_id
  JOIN assistant.providers p ON p.id=m.provider_id
  JOIN security.workspace_memberships s ON(s.tenant_id,s.workspace_id,s.principal_id)=(g.tenant_id,g.workspace_id,v.saved_by)
 WHERE(v.agent_id,v.version)=(agent,agent_version);
$$;

-- As in 0087, with the version's history window.
CREATE OR REPLACE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
 catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
 token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous assistant.turns;w assistant.worker_generation;k assistant.conversations;
 a assistant.agents;delegation uuid;blocker text;n timestamptz;t assistant.turns;stale assistant.turns;earlier uuid[];withheld integer[];number integer;
 skipped integer;history integer;
 zero constant uuid:='00000000-0000-0000-0000-000000000000';
BEGIN
 IF operation IS NULL OR operation=zero OR scope IS NULL OR scope NOT IN('api','exchange') OR conversation=zero OR agent IS NULL OR agent=zero
  OR agent_version IS NULL OR agent_version<1 OR question IS NULL OR NOT assistant.is_instructions(question) OR page_report IS NULL
  OR jsonb_typeof(page_report)<>'object' OR octet_length(page_report::text)>8192 OR catalog IS NULL OR knowledge_mode IS NULL
  OR knowledge_mode NOT IN('current','retained') OR(knowledge_mode='retained')<>(retained_context IS NOT NULL) OR retained_context=zero OR frame=zero
  OR session_digest IS NULL OR session_digest!~'^[0-9a-f]{64}$' OR profile IS NULL OR NOT assistant.is_display_name(profile)
  OR session_ends_at IS NULL OR session_ends_at<=clock_timestamp() OR session_ends_at>='9999-12-31 00:00:00+00'
  OR token_digest IS NULL OR token_digest!~'^[0-9a-f]{64}$' OR runtime_set IS NULL OR runtime_set!~'^[a-z][a-z0-9_]{0,63}$'
  OR question_limit IS NULL OR question_limit<1 OR workspace_turns IS NULL OR workspace_turns<1 OR admission_seconds IS NULL OR admission_seconds<1 THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF length(question)>question_limit THEN RAISE EXCEPTION 'question_too_long';END IF;
 -- A hosted ask binds the sign-in session its request context was issued for, ending no later than it (§11).
 IF c.hosted_session_digest IS NOT NULL AND NOT EXISTS(SELECT 1 FROM security.hosted_session_info(c.hosted_session_digest) s
   WHERE ask_turn.session_digest=c.hosted_session_digest AND ask_turn.session_ends_at<=s.expires_at) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 -- The person's principal row serializes their asks, so an identical ask sent while its original still runs
 -- waits for it.
 PERFORM 1 FROM security.principals x WHERE x.id=c.actor_id FOR NO KEY UPDATE;
 SELECT x.* INTO previous FROM assistant.turns x WHERE(x.owner_id,x.operation_id)=(c.actor_id,operation);
 -- The wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.grant_actor(true);
 IF FOUND THEN
  IF(previous.tenant_id,previous.workspace_id)<>(c.tenant_id,c.workspace_id)
   OR(SELECT x.scope FROM assistant.conversations x WHERE x.id=previous.conversation_id)<>ask_turn.scope
   OR(CASE WHEN conversation IS NULL THEN previous.ordinal<>1 ELSE previous.conversation_id<>conversation OR previous.ordinal=1 END)
   OR(previous.agent_id,previous.agent_version,previous.question,previous.page_report,previous.catalog_id,previous.knowledge_mode)
    IS DISTINCT FROM(agent,agent_version,question,page_report,catalog,knowledge_mode)
   OR(previous.requested_context,previous.frame_id) IS DISTINCT FROM(retained_context,frame) THEN
   RAISE EXCEPTION 'turn_conflict';
  END IF;
  RETURN jsonb_build_object('turn',assistant.owner_turn(previous));
 END IF;
 SELECT x.* INTO w FROM assistant.worker_generation x FOR SHARE;
 IF generation IS NULL OR w.generation<>generation OR NOT assistant.holds_worker_lock(w.worker_pid) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 -- A conversation's owner, scope and agent never change, so it is checked before admission (§8).
 IF conversation IS NOT NULL THEN
  SELECT x.* INTO k FROM assistant.conversations x
   WHERE x.id=conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,ask_turn.scope);
  IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
  IF k.agent_id<>agent THEN RAISE EXCEPTION 'conversation_agent_mismatch';END IF;
 END IF;
 -- Admission (§7.4): one workspace at a time. What it ends, in one pass in id order, as every writer of several turns
 -- locks them: a turn still without its record at the deadline (ask_incomplete, or cancelled once its cancel was
 -- requested), and any turn past its elapsed limit plus the deadline (interrupted).
 PERFORM pg_advisory_xact_lock(730849161,hashtext(c.tenant_id::text||c.workspace_id::text));
 n:=clock_timestamp();
 FOR stale IN SELECT x.* FROM assistant.turns x JOIN assistant.agent_versions y ON(y.agent_id,y.version)=(x.agent_id,x.agent_version)
  WHERE(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) AND x.state IN('queued','running','cancel_requested')
   AND(x.recorded_at IS NULL AND x.asked_at+make_interval(secs=>admission_seconds)<=n
    OR x.asked_at+make_interval(secs=>y.max_elapsed_seconds+admission_seconds)<=n) ORDER BY x.id FOR UPDATE OF x LOOP
  IF stale.recorded_at IS NOT NULL THEN PERFORM assistant.end_turn_as(stale,'interrupted',NULL);
  ELSIF stale.state='cancel_requested' THEN PERFORM assistant.end_turn_as(stale,'cancelled',stale.cancel_code);
  ELSE PERFORM assistant.end_turn_as(stale,'failed','ask_incomplete');
  END IF;
 END LOOP;
 -- Save and Publish lock the agent's row, so the version stands as checked until the turn commits.
 SELECT x.* INTO a FROM assistant.agents x WHERE x.id=agent AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR SHARE;
 -- The waits can outlive the authority, which is checked from here on.
 c:=assistant.grant_actor(true);
 delegation:=assistant.ask_authority(c,a,agent_version);
 n:=clock_timestamp();
 blocker:=assistant.call_blocker(a.id,agent_version,n);
 IF blocker IS NOT NULL THEN RAISE EXCEPTION '%',blocker;END IF;
 IF EXISTS(SELECT 1 FROM assistant.turns x WHERE(x.tenant_id,x.workspace_id,x.owner_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND x.state IN('queued','running','cancel_requested')) THEN
  RETURN jsonb_build_object('refused','turn_active');
 END IF;
 IF(SELECT count(*) FROM assistant.turns x WHERE(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) AND x.state IN('queued','running','cancel_requested'))
   >=workspace_turns THEN
  RETURN jsonb_build_object('refused','workspace_turn_limit');
 END IF;
 IF k.id IS NULL THEN
  INSERT INTO assistant.conversations VALUES(gen_random_uuid(),c.tenant_id,c.workspace_id,c.actor_id,scope,a.id,n,0) RETURNING * INTO k;
 END IF;
 -- The version's history window (D249): only its latest earlier turns are considered, and of those only the ones whose
 -- sources are still readable are included.
 SELECT x.history_turns INTO STRICT history FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(a.id,agent_version);
 SELECT count(*)+1 INTO number FROM assistant.turns y WHERE y.conversation_id=k.id;
 SELECT coalesce(array_agg(x.id ORDER BY x.ordinal) FILTER(WHERE x.included),ARRAY[]::uuid[]),
   coalesce(array_agg(x.ordinal ORDER BY x.ordinal) FILTER(WHERE NOT x.included),ARRAY[]::integer[])
  INTO earlier,withheld
  FROM(SELECT y.id,y.ordinal,assistant.sources_readable(y.id) AND NOT EXISTS(SELECT 1 FROM assistant.turn_sources(y.id) s
     WHERE NOT EXISTS(SELECT 1 FROM security.delegation_permissions p WHERE p.delegation_id=delegation AND p.permission=s.permission)) included
   FROM assistant.turns y WHERE y.conversation_id=k.id ORDER BY y.ordinal DESC LIMIT history)x;
 INSERT INTO assistant.turns VALUES(gen_random_uuid(),k.id,c.tenant_id,c.workspace_id,number,c.actor_id,operation,a.id,agent_version,
  agent_version>coalesce(a.published_version,0),delegation,question,page_report,catalog,knowledge_mode,retained_context,frame,NULL,NULL,NULL,NULL,
  earlier,runtime_set,'queued',NULL,NULL,'',false,generation,token_digest,session_digest,profile,session_ends_at,n,NULL,NULL,NULL,NULL) RETURNING * INTO t;
 IF NOT assistant.sources_accessible(t) THEN RAISE EXCEPTION 'access_changed';END IF;
 PERFORM assistant.turn_event(t,'accepted',jsonb_build_object('ordinal',t.ordinal,'agentId',t.agent_id,'version',t.agent_version));
 FOREACH skipped IN ARRAY withheld LOOP
  PERFORM assistant.turn_event(t,'step',jsonb_build_object('withheld',skipped));
 END LOOP;
 RETURN jsonb_build_object('turn',assistant.owner_turn(t),'admitted',true);
END $$;

ALTER FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer) TO openetrm_runtime;
COMMIT;
