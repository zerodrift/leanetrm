-- An ask's answer says when it admitted the turn (docs/ai-assistant.md §7.4, §8). Only the process that asked holds
-- the turn's token, so only the answer that admits a turn may pin it and hand it to the worker; a repeat answers with
-- its turn as it stands, as before. In 0069 the two answers were alike, so the asking process could not tell an
-- admission from a repeat. Nothing else changes; the replaced definition keeps its owner and grants.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the helpers the ask relies on and the definition this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.grant_actor(boolean)','4aaf7c789d860df03d222e891b74008dafb6aae71d01a77b17219c35621a7503',false),
  ('assistant.is_instructions(text)','9637ce303efb18669266c94083fb9cfb66810143e47f15ca12b66e2c698ca59e',false),
  ('assistant.is_display_name(text)','0fa2f8b4c9a0999d2098f85f44b63ea90a29d615855648f79d8361f11dd90952',false),
  ('assistant.holds_worker_lock(integer)','b38b1bead5ffe6cd25cb6311d8f335b2d09f0be46bd505beea9584aca7f9da13',false),
  ('assistant.turn_state(assistant.turns)','c72f829f3c6541582b6a5d428d7a7a3dcbb002096807e7e4161d4b683c2b174e',false),
  ('assistant.turn_event(assistant.turns,text,jsonb)','2eaeaf1f25ffb0df425e16de67dc120f292a4555e323f61d38cae90e0c882f67',false),
  ('assistant.end_turn_as(assistant.turns,text,text)','d83ac178978b9ead8b7f34d801534279104608489a7d5193a170a040d61ce3d5',false),
  ('assistant.ask_authority(security.request_contexts,assistant.agents,integer)','65155d6d5f29c006c835dc4e9396b27fdad4332a54586c1a37abb38fe881a619',false),
  ('assistant.call_blocker(uuid,integer,timestamp with time zone)','218d413e89c6ce399b5a17fb4f2b185fd74b1d74667df939c346860eca42f0ae',false),
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,integer,integer,integer)','57e2235161f150ed2516b083a5dbaad8cbdb1f31dd06d58596e081655ea9b611',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'admitted_asks: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- As in 0069, with the answer that admits the turn saying so.
CREATE OR REPLACE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
 catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
 token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous assistant.turns;w assistant.worker_generation;k assistant.conversations;
 a assistant.agents;delegation uuid;blocker text;n timestamptz;t assistant.turns;stale assistant.turns;earlier uuid[];
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
  RETURN jsonb_build_object('turn',assistant.turn_state(previous));
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
 SELECT coalesce(array_agg(x.id ORDER BY x.ordinal),ARRAY[]::uuid[]) INTO earlier FROM assistant.turns x WHERE x.conversation_id=k.id;
 INSERT INTO assistant.turns VALUES(gen_random_uuid(),k.id,c.tenant_id,c.workspace_id,cardinality(earlier)+1,c.actor_id,operation,a.id,agent_version,
  agent_version>coalesce(a.published_version,0),delegation,question,page_report,catalog,knowledge_mode,retained_context,frame,NULL,NULL,NULL,NULL,
  earlier,runtime_set,'queued',NULL,NULL,'',false,generation,token_digest,session_digest,profile,session_ends_at,n,NULL,NULL,NULL,NULL) RETURNING * INTO t;
 PERFORM assistant.turn_event(t,'accepted',jsonb_build_object('ordinal',t.ordinal,'agentId',t.agent_id,'version',t.agent_version));
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'admitted',true);
END $$;
COMMIT;
