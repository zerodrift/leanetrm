-- The page assistant's live event stream (docs/ai-assistant.md §7.1, §8). A stream serves one turn, and every stored
-- event it sends comes through the read below, in the turn owner's own request context with the replay recheck: the
-- turn as its owner reads it, the instant its elapsed limit passes and its events after a sequence, at most the replay
-- window's. Text the worker publishes reaches a stream in the process that wrote it once its publication recheck has
-- committed, so nothing here stores or notifies anything. One helper shows an event as its owner reads it, and the
-- conversation read now shares it, answering exactly as before.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority, access and turn helpers the read relies on and the definition this migration replaces, each with
-- exactly the privileges it was written against: since 0073 the risk worker also executes the market and reference
-- access functions the replay recheck calls.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('security.has_pre_pilot_permission(text)','ba2b9b5fbaaf8eadea6a60a149bc5b04d63774721cb8b788911cd29bd41cb436',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)','43e35b62e3ad9ea86286250b208edf56f3dd7ddce12801f4288d75b98d7506bb',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('security.market_access(uuid,text)','804fc62e6057b1b2d528e3493d761aa52d613a766a9087c90fcd6313b5832383',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner','openetrm_risk_worker=X/openetrm_security_owner']),
  ('security.reference_access(uuid,text)','697d1e75ddba8a9a7fbdfa948951d3c6760e445f6f681ba307c1c937568c0828',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner','openetrm_risk_worker=X/openetrm_security_owner']),
  ('security.trading_access(uuid,text)','422b9a2343e622a55ea85cdc707c17762b3be087228d5a653a895875639bb5cb',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.grant_actor(boolean)','4aaf7c789d860df03d222e891b74008dafb6aae71d01a77b17219c35621a7503',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.turn_state(assistant.turns)','c72f829f3c6541582b6a5d428d7a7a3dcbb002096807e7e4161d4b683c2b174e',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.turn_sources(uuid)','6af9af83498f582358dae3866b893ea23f037a63ffe1cb3a2b5b9a05dc025aba',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.sources_readable(uuid)','c77f51e6257b2af498f7618396e971ffc0b4aa233cdbfc708066f50d541b0379',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.owner_turn(assistant.turns)','d35d3b217c9f2b5a1ee2ecdd38ff61ce8dc9b5d5afb9d73d7dddaace8138ff5f',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.conversation(text,uuid,bigint)','2dd7bc747cb63c2c11fa2fa70186e21f215f4c6931e9d47b19a7407cc1487125',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> helper.acl::aclitem[] AND p.proacl <@ helper.acl::aclitem[])
  THEN RAISE EXCEPTION 'turn_stream: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- An event as its owner reads it (§7.1, §8): its sequence, turn, kind, payload and instant, a text event's payload
-- hidden by the code that hides its turn's answer.
CREATE FUNCTION assistant.owner_event(e assistant.turn_events,hidden text) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('sequence',e.sequence,'turnId',e.turn_id,'kind',e.kind,
  'payload',CASE WHEN e.kind='text' AND hidden IS NOT NULL THEN jsonb_build_object('hidden',hidden) ELSE e.payload END,'at',e.recorded_at);
$$;

-- As in 0072, each event as owner_event shows it.
CREATE OR REPLACE FUNCTION assistant.conversation(scope text,conversation uuid,after bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);k assistant.conversations;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') OR after IS NULL OR after<0 THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO k FROM assistant.conversations x
  WHERE x.id=conversation.conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,conversation.scope);
 IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
 RETURN(WITH shown AS(SELECT t.id,t.ordinal,assistant.owner_turn(t) turn FROM assistant.turns t WHERE t.conversation_id=k.id)
  SELECT jsonb_build_object('conversation',jsonb_build_object('id',k.id,'agentId',k.agent_id,'createdAt',k.created_at,'readThrough',k.read_through),
   'turns',coalesce((SELECT jsonb_agg(s.turn ORDER BY s.ordinal) FROM shown s),'[]'::jsonb),
   'events',coalesce((SELECT jsonb_agg(assistant.owner_event(e,s.turn->>'hidden') ORDER BY e.sequence)
    FROM assistant.turn_events e JOIN shown s ON s.id=e.turn_id WHERE e.conversation_id=k.id AND e.sequence>after),'[]'::jsonb)));
END $$;

-- The stream's read (§8), in its owner's own request context while they hold UseAgents: one turn of one of their
-- conversations in the scope, the latest unless named, as its owner reads it (its state, whether it has started, whether
-- its published answer ended incomplete, and whether the replay recheck hides its answer), the instant its elapsed
-- limit passes, the conversation's latest sequence, and the turn's events after a sequence as the conversation read
-- shows them. A sequence beyond the conversation's latest, before the one preceding the turn's first event or further
-- behind its last than the replay window is refused replay_unavailable, and one that nothing follows in an ended turn
-- turn_not_running. A named turn must be the conversation's. It writes nothing.
CREATE FUNCTION assistant.turn_stream(scope text,conversation uuid,turn uuid,after bigint,replay_window integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);k assistant.conversations;t assistant.turns;shown jsonb;
 latest_sequence bigint;first_sequence bigint;last_sequence bigint;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') OR conversation IS NULL OR after IS NULL OR after<0 OR replay_window IS NULL OR replay_window<1 THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 SELECT x.* INTO k FROM assistant.conversations x
  WHERE x.id=turn_stream.conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,turn_stream.scope);
 IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
 SELECT x.* INTO t FROM assistant.turns x WHERE x.conversation_id=k.id AND(turn_stream.turn IS NULL OR x.id=turn_stream.turn) ORDER BY x.ordinal DESC LIMIT 1;
 IF t.id IS NULL THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT max(e.sequence) INTO latest_sequence FROM assistant.turn_events e WHERE e.conversation_id=k.id;
 SELECT min(e.sequence),max(e.sequence) INTO first_sequence,last_sequence FROM assistant.turn_events e WHERE e.conversation_id=k.id AND e.turn_id=t.id;
 IF after>latest_sequence OR after<first_sequence-1 OR last_sequence-after>replay_window THEN
  RETURN jsonb_build_object('refused','replay_unavailable');
 END IF;
 IF t.state IN('completed','failed','cancelled','interrupted') AND after>=last_sequence THEN RETURN jsonb_build_object('refused','turn_not_running');END IF;
 shown:=assistant.owner_turn(t);
 RETURN jsonb_build_object(
  'turn',jsonb_build_object('id',t.id,'state',t.state,'startedAt',t.started_at,'incomplete',t.incomplete,'hidden',shown->'hidden'),
  'deadline',t.asked_at+make_interval(secs=>(SELECT v.max_elapsed_seconds FROM assistant.agent_versions v WHERE(v.agent_id,v.version)=(t.agent_id,t.agent_version))),
  'latest',latest_sequence,
  'events',coalesce((SELECT jsonb_agg(assistant.owner_event(e,shown->>'hidden') ORDER BY e.sequence) FROM assistant.turn_events e
   WHERE e.conversation_id=k.id AND e.turn_id=t.id AND e.sequence>after),'[]'::jsonb));
END $$;

DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['assistant.owner_event(assistant.turn_events,text)','assistant.turn_stream(text,uuid,uuid,bigint,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.turn_stream(text,uuid,uuid,bigint,integer) TO openetrm_runtime;
COMMIT;
