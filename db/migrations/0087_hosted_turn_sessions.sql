-- The hosted page assistant's sign-in sessions (docs/ai-assistant.md §11). An application session belongs to the sign-in
-- session it was opened under and ends with it, so a session's information now names when it ends. A hosted ask binds the
-- sign-in session its request context was issued for, ending no later than it, and revoking a sign-in session, which
-- signing out, a password reset and disabling the account or its membership all do, cancels its turns session_ended in
-- the revoking transaction.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the definitions this migration replaces and the helpers the binding relies on, each with exactly the privileges it
-- was written against.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,integer,integer,integer)',
   '301ea61cb524c975a0705cf2e3f10126db6f40bf9fd198c923a1a2981d94af10',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('security.hosted_session_info(text)','e8731d3de983647aeb387e27c1378762f3be74a86505cab8d6d00051571cda5f',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_authenticator=X/openetrm_security_owner']),
  ('assistant.grant_actor(boolean)','4aaf7c789d860df03d222e891b74008dafb6aae71d01a77b17219c35621a7503',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.request_cancel(uuid,text)','0873236232fc1946610027563db6ec784997daf24e959d3282d08e9a484005d9',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('security.issue_hosted_context(text,name)','dc78b6d12fca7da5ea46f6d7ee0d80c0c1f9f979e576b5b49d26c5b694e213b2',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_authenticator=X/openetrm_security_owner']),
  ('security.require_hosted_session()','6e663949fc10c4c23e5395d1c444d53590b1cbcce4f0230437814afe9e85c332',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> helper.acl::aclitem[] AND p.proacl <@ helper.acl::aclitem[])
  THEN RAISE EXCEPTION 'hosted_turn_sessions: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- As in 0032, naming when the session ends. Its SQL callers read it by column name, so their definitions stand.
DROP FUNCTION security.hosted_session_info(text);
CREATE FUNCTION security.hosted_session_info(digest text)
RETURNS TABLE(principal_id uuid,issuer text,subject text,login text,name text,tenant_id uuid,workspace_id uuid,expires_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT a.principal_id,a.issuer,a.principal_id::text,a.login,m.display_name,a.tenant_id,a.workspace_id,s.expires_at
 FROM security.hosted_sessions s JOIN security.hosted_accounts a ON a.principal_id=s.principal_id AND a.credential_version=s.credential_version
 JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.principal_id)
 WHERE s.token_digest=digest AND s.revoked_at IS NULL AND s.expires_at>clock_timestamp() AND security.hosted_account_active(a.principal_id);
$$;
ALTER FUNCTION security.hosted_session_info(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.hosted_session_info(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.hosted_session_info(text) TO openetrm_authenticator;

-- As in 0072, with a hosted ask bound to the sign-in session its request context was issued for.
CREATE OR REPLACE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
 catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
 token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous assistant.turns;w assistant.worker_generation;k assistant.conversations;
 a assistant.agents;delegation uuid;blocker text;n timestamptz;t assistant.turns;stale assistant.turns;earlier uuid[];withheld integer[];number integer;
 skipped integer;
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
 SELECT coalesce(array_agg(x.id ORDER BY x.ordinal) FILTER(WHERE x.included),ARRAY[]::uuid[]),
   coalesce(array_agg(x.ordinal ORDER BY x.ordinal) FILTER(WHERE NOT x.included),ARRAY[]::integer[]),count(*)+1
  INTO earlier,withheld,number
  FROM(SELECT y.id,y.ordinal,assistant.sources_readable(y.id) AND NOT EXISTS(SELECT 1 FROM assistant.turn_sources(y.id) s
     WHERE NOT EXISTS(SELECT 1 FROM security.delegation_permissions p WHERE p.delegation_id=delegation AND p.permission=s.permission)) included
   FROM assistant.turns y WHERE y.conversation_id=k.id)x;
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

-- Revoking a sign-in session cancels its turns session_ended (§11), as a trigger so every revocation is covered: signing out,
-- a password reset and disabling the account or its membership. It locks the turns it cancels in id order.
CREATE FUNCTION assistant.cancel_session_turns() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE t uuid;
BEGIN
 FOR t IN SELECT x.id FROM assistant.turns x WHERE x.state IN('queued','running') AND(x.owner_id,x.session_digest)=(NEW.principal_id,NEW.token_digest)
  ORDER BY x.id LOOP
  PERFORM assistant.request_cancel(t,'session_ended');
 END LOOP;
 RETURN NULL;
END $$;
ALTER FUNCTION assistant.cancel_session_turns() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.cancel_session_turns() FROM PUBLIC;
CREATE TRIGGER cancel_session_turns AFTER UPDATE OF revoked_at ON security.hosted_sessions FOR EACH ROW
 WHEN(OLD.revoked_at IS NULL AND NEW.revoked_at IS NOT NULL) EXECUTE FUNCTION assistant.cancel_session_turns();
COMMIT;
