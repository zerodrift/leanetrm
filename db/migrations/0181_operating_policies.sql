-- Current operating policies are immutable scoped revisions. Initial settings
-- explicitly carry forward the existing behavior; they are never read fallbacks.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
-- Refuse drift in the reviewed helper bodies, ownership or execution privileges.
DO $$DECLARE replaced record;BEGIN
 PERFORM 1 FROM security.epoch WHERE id FOR UPDATE;
 FOR replaced IN SELECT * FROM(VALUES
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,integer,integer,integer)','d4768c8d5adaa1e1849337e84478d7d0b6b6015cf91ddf7ca14d430aef76f059',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)','d1b2d3cb4cb28d25510b498df2c9c6b9f9bb588b1047f0286e833c6646d38470',ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.grant_agent(uuid,uuid,integer,integer)','08d027fa6e8cb2e3695700e44a37d41e43ea962483f574a4a311cfded7691284',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer)','010427b97209a25fef9d83cc0724e405efbef08ff719b59fb52e543d94ee1c8c',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.start_turn(uuid,bytea)','bd277f7de410ee4efd83f9c35645170e4b44817bfccfa66f3e64e332fb32970b',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(replaced.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=replaced.hash
   AND p.proacl=replaced.acl::aclitem[])
  THEN RAISE EXCEPTION 'operating_policy_upgrade: unexpected helper %',replaced.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

CREATE FUNCTION security.operating_policy_fields(kind text) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE kind WHEN 'assistant' THEN '[
 {"key":"pinBytes","label":"Pinned input bytes","min":1024,"max":262144,"initial":262144},
 {"key":"workspaceTurns","label":"Concurrent workspace turns","min":1,"max":4,"initial":4},
 {"key":"retainedContextReserve","label":"Contexts reserved for pages","min":1,"max":64,"initial":64},
 {"key":"maxGrantDays","label":"Longest new agent grant (days)","min":1,"max":90,"initial":90},
 {"key":"toolResultRows","label":"Records per tool result","min":1,"max":500,"initial":500},
 {"key":"toolResultBytes","label":"Bytes per tool result","min":1024,"max":65536,"initial":65536}]'::jsonb
 WHEN 'access' THEN '[
 {"key":"sessionMinutes","label":"New sign-in lifetime (minutes)","min":1,"max":30,"initial":30},
 {"key":"failedAttempts","label":"Failed attempts before lockout","min":1,"max":5,"initial":5},
 {"key":"lockoutMinutes","label":"New lockout duration (minutes)","min":1,"max":10,"initial":10},
 {"key":"passwordMinimum","label":"Minimum new password length","min":15,"max":1024,"initial":15},
 {"key":"invitationHours","label":"New invitation lifetime (hours)","min":1,"max":24,"initial":24}]'::jsonb END;
$$;
CREATE FUNCTION security.operating_policy_valid(kind text,settings jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(jsonb_typeof(settings)='object' AND security.operating_policy_fields(kind) IS NOT NULL
 AND (SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(settings)k)=
     (SELECT array_agg(f->>'key' ORDER BY f->>'key') FROM jsonb_array_elements(security.operating_policy_fields(kind))f)
 AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(security.operating_policy_fields(kind))f
  WHERE jsonb_typeof(settings->(f->>'key')) IS DISTINCT FROM 'number'
   OR CASE WHEN settings->>(f->>'key') ~ '^[0-9]{1,9}$' THEN
      (settings->>(f->>'key'))::bigint NOT BETWEEN (f->>'min')::bigint AND (f->>'max')::bigint ELSE true END),false);
$$;
CREATE TABLE security.operating_policy_versions(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,kind text NOT NULL,
 revision_id uuid NOT NULL,version integer NOT NULL CHECK(version>0),settings jsonb NOT NULL,
 actor_id uuid,recorded_at ref_data.instant NOT NULL,origin text NOT NULL CHECK(origin IN('carried_forward','configured')),
 PRIMARY KEY(tenant_id,workspace_id,kind,revision_id),UNIQUE(tenant_id,workspace_id,kind,version),
 UNIQUE(tenant_id,workspace_id,kind,recorded_at),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 CHECK(security.operating_policy_valid(kind,settings)),CHECK((actor_id IS NULL)=(origin='carried_forward'))
);
CREATE TABLE security.operating_policy_commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL,operation_id uuid NOT NULL,
 kind text NOT NULL,expected_version integer NOT NULL,settings jsonb NOT NULL,revision_id uuid NOT NULL,response jsonb NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,actor_id,operation_id),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['operating_policy_versions','operating_policy_commands'] LOOP
  EXECUTE format('ALTER TABLE security.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE security.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE security.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON security.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON security.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION security.presentation_actor() RETURNS security.request_contexts
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 SELECT x.* INTO c FROM security.request_contexts x JOIN security.principals p ON p.id=x.actor_id AND p.enabled AND p.kind='human'
 JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(x.tenant_id,x.workspace_id,x.actor_id) AND m.enabled
 WHERE x.backend_pid=pg_backend_pid() AND x.transaction_id=pg_current_xact_id_if_assigned() AND x.runtime_login=session_user
 AND x.expires_at>clock_timestamp() AND x.delegation_id IS NULL;
 IF c.id IS NULL THEN RAISE EXCEPTION 'An active human request is required' USING ERRCODE='42501';END IF;
 RETURN c;
END$$;
CREATE FUNCTION security.use_operating_policy_context(token uuid,permission text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF permission IS NULL OR permission NOT IN('ManageAgents','ManageAccess') OR EXISTS(SELECT 1 FROM security.request_contexts
  WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)
 THEN RAISE EXCEPTION 'Operating policy admission must be first' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM security.use_context(token);PERFORM security.presentation_actor();
 IF NOT security.has_permission(permission) THEN RAISE EXCEPTION 'No operating policy permission' USING ERRCODE='42501';END IF;
 UPDATE security.request_contexts SET administration=true WHERE id=token;
END$$;
CREATE FUNCTION security.operating_policy_current(tenant uuid,workspace uuid,policy text) RETURNS security.operating_policy_versions
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result security.operating_policy_versions;
BEGIN
 SELECT * INTO result FROM security.operating_policy_versions v WHERE(v.tenant_id,v.workspace_id,v.kind)=(tenant,workspace,policy)
 ORDER BY v.recorded_at DESC LIMIT 1;
 IF result.revision_id IS NULL THEN RAISE EXCEPTION 'operating_policy_missing';END IF;
 RETURN result;
END$$;
CREATE FUNCTION security.operating_policy_state(value security.operating_policy_versions) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('revisionId',value.revision_id,'version',value.version,'values',value.settings,'recordedAt',value.recorded_at,'origin',value.origin);
$$;
CREATE FUNCTION security.initialize_operating_policies(tenant uuid,workspace uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE policy text;initial jsonb;
BEGIN
 FOREACH policy IN ARRAY ARRAY['assistant','access'] LOOP
  PERFORM pg_advisory_xact_lock(hashtextextended(tenant::text||workspace::text||':operating:'||policy,0));
  IF NOT EXISTS(SELECT 1 FROM security.operating_policy_versions v WHERE(v.tenant_id,v.workspace_id,v.kind)=(tenant,workspace,policy)) THEN
   SELECT jsonb_object_agg(f->>'key',f->'initial') INTO initial FROM jsonb_array_elements(security.operating_policy_fields(policy))f;
   INSERT INTO security.operating_policy_versions VALUES(tenant,workspace,policy,gen_random_uuid(),1,initial,NULL,clock_timestamp(),'carried_forward');
  END IF;
 END LOOP;
END$$;
DO $$DECLARE w record;BEGIN FOR w IN SELECT tenant_id,id FROM security.workspaces LOOP
 PERFORM security.initialize_operating_policies(w.tenant_id,w.id);END LOOP;END$$;
CREATE FUNCTION security.operating_policy_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=security.presentation_actor();result jsonb:='[]';policy text;permission text;value security.operating_policy_versions;
BEGIN
 FOREACH policy IN ARRAY ARRAY['assistant','access'] LOOP
  permission:=CASE policy WHEN 'assistant' THEN 'ManageAgents' ELSE 'ManageAccess' END;
  IF security.has_permission(permission) THEN
   value:=security.operating_policy_current(c.tenant_id,c.workspace_id,policy);
   result:=result||jsonb_build_array(jsonb_build_object('kind',policy,'fields',security.operating_policy_fields(policy),
    'current',security.operating_policy_state(value),'history',(SELECT coalesce(jsonb_agg(security.operating_policy_state(v) ORDER BY v.recorded_at DESC),'[]')
      FROM(SELECT * FROM security.operating_policy_versions x WHERE(x.tenant_id,x.workspace_id,x.kind)=(c.tenant_id,c.workspace_id,policy)
       ORDER BY recorded_at DESC LIMIT 20)v)));
  END IF;
 END LOOP;
 FOR policy IN SELECT p->>'kind' FROM jsonb_array_elements(result)p LOOP
  IF NOT security.has_permission(CASE policy WHEN 'assistant' THEN 'ManageAgents' ELSE 'ManageAccess' END) THEN RAISE EXCEPTION 'Operating policy permission expired' USING ERRCODE='42501';END IF;
 END LOOP;
 PERFORM security.presentation_actor();
 RETURN jsonb_build_object('policies',result);
END$$;
CREATE FUNCTION security.save_operating_policy(policy text,operation uuid,expected integer,settings jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=security.presentation_actor();previous security.operating_policy_commands;
 head security.operating_policy_versions;next security.operating_policy_versions;permission text;response jsonb;n timestamptz;
BEGIN
 permission:=CASE policy WHEN 'assistant' THEN 'ManageAgents' WHEN 'access' THEN 'ManageAccess' END;
 IF permission IS NULL OR NOT c.administration OR NOT security.has_permission(permission) THEN RAISE EXCEPTION 'No operating policy permission' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR expected IS NULL OR expected<1 OR NOT security.operating_policy_valid(policy,settings)
 THEN RAISE EXCEPTION 'operating_policy_input';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(c.tenant_id::text||c.workspace_id::text||':operating:'||policy,0));
 c:=security.presentation_actor();
 IF NOT security.has_permission(permission) THEN RAISE EXCEPTION 'Operating policy permission expired' USING ERRCODE='42501';END IF;
 SELECT * INTO previous FROM security.operating_policy_commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.operation_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  IF(previous.kind,previous.expected_version,previous.settings) IS DISTINCT FROM(policy,expected,settings) THEN RAISE EXCEPTION 'operating_policy_conflict';END IF;
  IF NOT security.has_permission(permission) THEN RAISE EXCEPTION 'Operating policy permission expired' USING ERRCODE='42501';END IF;
  RETURN previous.response;
 END IF;
 head:=security.operating_policy_current(c.tenant_id,c.workspace_id,policy);
 IF head.version<>expected THEN RAISE EXCEPTION 'operating_policy_conflict';END IF;
 next:=head;
 IF head.settings IS DISTINCT FROM settings THEN
  n:=clock_timestamp();IF n<=head.recorded_at THEN RAISE EXCEPTION 'operating_policy_clock';END IF;
  INSERT INTO security.operating_policy_versions VALUES(c.tenant_id,c.workspace_id,policy,gen_random_uuid(),head.version+1,settings,c.actor_id,n,'configured') RETURNING * INTO next;
 END IF;
 response:=security.operating_policy_state(next);
 INSERT INTO security.operating_policy_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,policy,expected,settings,next.revision_id,response);
 PERFORM security.presentation_actor();
 IF NOT security.has_permission(permission) THEN RAISE EXCEPTION 'Operating policy permission expired' USING ERRCODE='42501';END IF;
 RETURN response;
END$$;
CREATE FUNCTION security.operating_values(policy text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=security.presentation_actor();value security.operating_policy_versions;
BEGIN
 IF NOT(CASE policy WHEN 'assistant' THEN security.has_permission('ManageAgents') OR security.has_permission('UseAgents') WHEN 'access' THEN security.has_permission('ManageAccess') ELSE false END)
 THEN RAISE EXCEPTION 'No operating policy access' USING ERRCODE='42501';END IF;
 value:=security.operating_policy_current(c.tenant_id,c.workspace_id,policy);RETURN value.settings;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('operating_policy_fields','operating_policy_valid','presentation_actor','use_operating_policy_context','operating_policy_current','operating_policy_state',
 'initialize_operating_policies','operating_policy_directory','save_operating_policy','operating_values') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.use_operating_policy_context(uuid,text),security.operating_policy_directory(),security.save_operating_policy(text,uuid,integer,jsonb),security.operating_values(text) TO openetrm_runtime;

-- Pre-settings turns retain their original runtime provenance. Only new asks
-- acquire these exact operating-revision relationships.
CREATE TABLE assistant.turn_operating_pins(
 turn_id uuid PRIMARY KEY REFERENCES assistant.turns(id),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 kind text NOT NULL DEFAULT 'assistant' CHECK(kind='assistant'),revision_id uuid NOT NULL,
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions
);
ALTER TABLE assistant.turn_operating_pins OWNER TO openetrm_security_owner;
ALTER TABLE assistant.turn_operating_pins ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.turn_operating_pins FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON assistant.turn_operating_pins TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.turn_operating_pins FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION assistant.pin_operating_limits() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE policy security.operating_policy_versions;
BEGIN
 policy:=security.operating_policy_current(NEW.tenant_id,NEW.workspace_id,'assistant');
 INSERT INTO assistant.turn_operating_pins VALUES(NEW.id,NEW.tenant_id,NEW.workspace_id,'assistant',policy.revision_id);
 RETURN NEW;
END$$;
ALTER FUNCTION assistant.pin_operating_limits() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.pin_operating_limits() FROM PUBLIC;
CREATE TRIGGER operating_limits AFTER INSERT ON assistant.turns FOR EACH ROW EXECUTE FUNCTION assistant.pin_operating_limits();
CREATE FUNCTION assistant.turn_operating_limits(turn uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 SELECT v.settings||jsonb_build_object('revisionId',v.revision_id) INTO result FROM assistant.turn_operating_pins p
 JOIN security.operating_policy_versions v USING(tenant_id,workspace_id,kind,revision_id) WHERE p.turn_id=turn;
 IF result IS NULL THEN RAISE EXCEPTION 'operating_policy_missing';END IF;
 RETURN result;
END$$;
ALTER FUNCTION assistant.turn_operating_limits(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.turn_operating_limits(uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION assistant.ask_turn(operation uuid, scope text, conversation uuid, agent uuid, agent_version integer, question text, page_report jsonb, catalog uuid, knowledge_mode text, retained_context uuid, frame uuid, session_digest text, profile text, session_ends_at timestamp with time zone, token_digest text, generation bigint, runtime_set text, question_limit integer, workspace_turns integer, admission_seconds integer)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET "TimeZone" TO 'UTC'
AS $function$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous assistant.turns;w assistant.worker_generation;k assistant.conversations;
 a assistant.agents;delegation uuid;blocker text;n timestamptz;t assistant.turns;stale assistant.turns;earlier uuid[];withheld integer[];number integer;
 skipped integer;history integer;
 zero constant uuid:='00000000-0000-0000-0000-000000000000';
BEGIN
 IF operation IS NULL OR operation=zero OR scope IS NULL OR scope NOT IN('api','exchange') OR conversation=zero OR agent IS NULL OR agent=zero
  OR agent_version IS NULL OR agent_version<1 OR question IS NULL OR NOT assistant.is_instructions(question) OR page_report IS NULL
  OR jsonb_typeof(page_report)<>'object' OR octet_length(page_report::text)>8192 OR catalog IS NULL OR knowledge_mode IS NULL
  OR knowledge_mode NOT IN('current','retained','operational') OR(knowledge_mode='retained')<>(retained_context IS NOT NULL) OR retained_context=zero OR frame=zero
  OR session_digest IS NULL OR session_digest!~'^[0-9a-f]{64}$' OR profile IS NULL OR NOT assistant.is_display_name(profile)
  OR session_ends_at IS NULL OR session_ends_at<=clock_timestamp() OR session_ends_at>='9999-12-31 00:00:00+00'
  OR token_digest IS NULL OR token_digest!~'^[0-9a-f]{64}$' OR runtime_set IS NULL OR runtime_set!~'^[a-z][a-z0-9_]{0,63}$'
  OR question_limit IS NULL OR question_limit<1 OR workspace_turns<1 OR admission_seconds IS NULL OR admission_seconds<1 THEN
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
 -- A prior binary cannot enforce the pinned output contract. Retained request replay above remains available.
 IF runtime_set<>'turns_v12' THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 workspace_turns:=least(workspace_turns,((security.operating_policy_current(c.tenant_id,c.workspace_id,'assistant')).settings->>'workspaceTurns')::integer);
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
END $function$
;
CREATE OR REPLACE FUNCTION assistant.pin_market_turn(turn uuid, token bytea, effective_date date, pins jsonb, reserve integer)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET "TimeZone" TO 'UTC'
AS $function$
DECLARE c security.request_contexts:=assistant.grant_actor(true);t assistant.turns;x market_data.temporal_contexts;context uuid;operating jsonb;
BEGIN
 t:=assistant.fenced_turn(turn,token);
 -- The wait can outlive the authority.
 c:=assistant.grant_actor(true);
 IF t.owner_id<>c.actor_id OR(t.tenant_id,t.workspace_id)<>(c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 operating:=assistant.turn_operating_limits(t.id);
 reserve:=greatest(reserve,(operating->>'retainedContextReserve')::integer);
 IF pins IS NOT NULL AND octet_length(pins::text)>(operating->>'pinBytes')::integer THEN RAISE EXCEPTION 'turn_input';END IF;
 IF t.recorded_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.owner_turn(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF(t.frame_id IS NULL)<>(pins IS NULL) OR pins IS NOT NULL AND(jsonb_typeof(pins)<>'object' OR pins->>'frameId' IS DISTINCT FROM t.frame_id::text
   OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(pins) key)<>ARRAY['at','curveContracts','frameId','frozenObservationIds','name',
    'observationIds','resolvesLatestCorrection','swapMarketSelections']
   OR jsonb_typeof(pins->'at')<>'string' OR pins->>'at'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$'
   OR jsonb_typeof(pins->'name')<>'string' OR btrim(pins->>'name')=''
   OR NOT assistant.is_uuid_array(pins->'observationIds') OR NOT assistant.is_uuid_array(pins->'frozenObservationIds')
   OR jsonb_typeof(pins->'curveContracts')<>'object' OR jsonb_typeof(pins->'swapMarketSelections') NOT IN('object','null')
   OR jsonb_typeof(pins->'resolvesLatestCorrection')<>'boolean' OR octet_length(pins::text)>262144) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF pins IS NOT NULL AND(EXISTS(SELECT 1 FROM jsonb_array_elements_text(pins->'frozenObservationIds') f WHERE NOT(pins->'observationIds')?f)
   OR EXISTS(SELECT 1 FROM jsonb_each(pins->'curveContracts') e WHERE e.key='' OR NOT assistant.is_uuid_array(e.value))
   OR jsonb_typeof(pins->'swapMarketSelections')='object' AND EXISTS(SELECT 1 FROM jsonb_each(pins->'swapMarketSelections') e
    WHERE NOT assistant.is_uuid_array(jsonb_build_array(e.key)) OR NOT assistant.is_uuid_array(e.value))) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 -- The frame's instant is one this database reads.
 BEGIN
  PERFORM(pins->>'at')::timestamptz;
 EXCEPTION WHEN datetime_field_overflow OR invalid_datetime_format OR invalid_time_zone_displacement_value THEN RAISE EXCEPTION 'turn_input';
 END;
 IF t.knowledge_mode='current' THEN
  IF effective_date IS NULL OR reserve IS NULL OR reserve<0 THEN RAISE EXCEPTION 'turn_input';END IF;
  -- Every capture certifies under this lock, held to commit, so the count cannot move before this one does.
  PERFORM pg_advisory_xact_lock(730849102);
  c:=assistant.grant_actor(true);
  IF(SELECT count(*) FROM market_data.temporal_contexts y WHERE y.catalog_id=t.catalog_id)>=4096-reserve THEN RAISE EXCEPTION 'knowledge_budget_reserved';END IF;
  context:=security.capture_date_context(t.catalog_id,effective_date,NULL);
 ELSE
  IF NOT security.market_access(t.catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current market read authority' USING ERRCODE='42501';END IF;
  context:=t.requested_context;
 END IF;
 SELECT y.* INTO x FROM market_data.temporal_contexts y WHERE(y.catalog_id,y.id)=(t.catalog_id,context);
 IF x.id IS NULL THEN RAISE EXCEPTION 'temporal_context';END IF;
 UPDATE assistant.turns y SET context_id=x.id,cutoff=x.known_at,pins=pin_market_turn.pins,recorded_at=clock_timestamp() WHERE y.id=t.id RETURNING y.* INTO t;
 RETURN jsonb_build_object('turn',assistant.owner_turn(t));
END $function$
;
CREATE OR REPLACE FUNCTION assistant.grant_agent(operation uuid, agent uuid, agent_version integer, grant_days_limit integer)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET "TimeZone" TO 'UTC'
AS $function$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous jsonb;a assistant.agents;v assistant.agent_versions;permissions text[];
 missing text;delegation uuid:=gen_random_uuid();at timestamptz;
BEGIN
 IF agent IS NULL OR agent='00000000-0000-0000-0000-000000000000'::uuid OR agent_version IS NULL OR agent_version<1 THEN
  RAISE EXCEPTION 'grant_input' USING ERRCODE='P0001';
 END IF;
 previous:=assistant.grant_replay(c,true,operation,agent,agent_version,NULL);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 SELECT x.* INTO a FROM assistant.agents x WHERE x.id=agent AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR SHARE;
 SELECT x.* INTO v FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(a.id,agent_version);
 IF v.agent_id IS NULL THEN RAISE EXCEPTION 'agent_not_allowed' USING ERRCODE='P0001';END IF;
 IF a.retired_at IS NOT NULL THEN RAISE EXCEPTION 'agent_retired' USING ERRCODE='P0001';END IF;
 IF NOT assistant.may_use(c,a,agent_version) THEN RAISE EXCEPTION 'agent_not_allowed' USING ERRCODE='P0001';END IF;
 permissions:=assistant.version_permissions(agent,agent_version);
 SELECT p INTO missing FROM unnest(permissions) p WHERE NOT security.has_permission(p) ORDER BY p COLLATE "C" LIMIT 1;
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'grant_permission_missing' USING ERRCODE='P0001',HINT=missing;END IF;
 IF grant_days_limit IS NOT NULL THEN grant_days_limit:=least(grant_days_limit,((security.operating_policy_current(c.tenant_id,c.workspace_id,'assistant')).settings->>'maxGrantDays')::integer);END IF;
 IF grant_days_limit IS NULL OR v.grant_days>grant_days_limit THEN RAISE EXCEPTION 'grant_duration_exceeded' USING ERRCODE='P0001';END IF;
 IF NOT c.administration THEN RAISE EXCEPTION 'admission_required' USING ERRCODE='P0001';END IF;
 at:=clock_timestamp();
 INSERT INTO assistant.grant_audit SELECT c.tenant_id,c.workspace_id,operation,g.delegation_id,'replaced',g.delegator_id,g.agent_id,g.agent_version,
   g.model_id,g.model_version,g.permissions,g.expires_at,c.actor_id,at
  FROM security.agent_delegations d JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted'
  WHERE(d.tenant_id,d.workspace_id,d.delegator_id,d.agent_id)=(c.tenant_id,c.workspace_id,c.actor_id,a.principal_id) AND NOT d.revoked;
 UPDATE security.agent_delegations d SET revoked=true
  WHERE(d.tenant_id,d.workspace_id,d.delegator_id,d.agent_id)=(c.tenant_id,c.workspace_id,c.actor_id,a.principal_id) AND NOT d.revoked;
 INSERT INTO security.agent_delegations VALUES(delegation,a.principal_id,c.actor_id,c.tenant_id,c.workspace_id,at,at+make_interval(days=>v.grant_days),false);
 INSERT INTO security.delegation_permissions SELECT delegation,p FROM unnest(permissions) p;
 INSERT INTO assistant.grant_audit VALUES(c.tenant_id,c.workspace_id,operation,delegation,'granted',c.actor_id,agent,agent_version,v.model_id,v.model_version,
  permissions,at+make_interval(days=>v.grant_days),c.actor_id,at);
 PERFORM assistant.grant_actor(true);
 RETURN assistant.grant_facts(delegation);
END $function$
;
CREATE OR REPLACE FUNCTION assistant.save_agent_version(operation uuid, agent uuid, expected integer, description text, instructions text, model uuid, model_version integer, reasoning text, caching boolean, tools text[], allowed_roles text[], tool_steps integer, input_tokens bigint, output_tokens integer, elapsed_seconds integer, read_seconds integer, retries integer, history_turns integer, grant_days integer, instructions_limit integer, grant_days_limit integer)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET "TimeZone" TO 'UTC'
AS $function$
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
 IF grant_days_limit IS NOT NULL THEN grant_days_limit:=least(grant_days_limit,((security.operating_policy_current(c.tenant_id,c.workspace_id,'assistant')).settings->>'maxGrantDays')::integer);END IF;
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
END $function$
;
CREATE OR REPLACE FUNCTION assistant.start_turn(turn uuid, token bytea)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE result jsonb:=assistant.start_turn_without_page_prompt(turn,token);pin assistant.turn_prompt_pins;instructions text;
BEGIN
 IF result ? 'started' THEN
  result:=jsonb_set(result,'{started}',(result->'started')||jsonb_build_object('operatingLimits',assistant.turn_operating_limits(turn)));
  SELECT * INTO pin FROM assistant.turn_prompt_pins p WHERE p.turn_id=turn;
  SELECT v.instructions INTO instructions FROM assistant.page_prompt_versions v
   WHERE(v.tenant_id,v.workspace_id,v.page,v.version)=(pin.tenant_id,pin.workspace_id,pin.page,pin.version);
  result:=jsonb_set(result,'{started}',(result->'started')||jsonb_build_object('pagePrompt',coalesce(instructions,''),'pagePromptVersion',coalesce(pin.version,0)));
 END IF;
 RETURN result;
END$function$
;
CREATE TABLE assistant.agent_version_operating_pins(
 agent_id uuid NOT NULL,version integer NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 kind text NOT NULL DEFAULT 'assistant' CHECK(kind='assistant'),revision_id uuid NOT NULL,
 PRIMARY KEY(agent_id,version),FOREIGN KEY(agent_id,version) REFERENCES assistant.agent_versions(agent_id,version),
 FOREIGN KEY(agent_id,tenant_id,workspace_id) REFERENCES assistant.agents(id,tenant_id,workspace_id),
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions
);
CREATE TABLE assistant.grant_operating_pins(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,operation_id uuid NOT NULL,delegation_id uuid PRIMARY KEY,
 kind text NOT NULL DEFAULT 'assistant' CHECK(kind='assistant'),revision_id uuid NOT NULL,
 FOREIGN KEY(tenant_id,workspace_id,operation_id,delegation_id) REFERENCES assistant.grant_audit,
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['agent_version_operating_pins','grant_operating_pins'] LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON assistant.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION assistant.pin_agent_operating_policy() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE a assistant.agents;policy security.operating_policy_versions;
BEGIN
 SELECT * INTO STRICT a FROM assistant.agents WHERE id=NEW.agent_id;
 policy:=security.operating_policy_current(a.tenant_id,a.workspace_id,'assistant');
 INSERT INTO assistant.agent_version_operating_pins VALUES(NEW.agent_id,NEW.version,a.tenant_id,a.workspace_id,'assistant',policy.revision_id);
 RETURN NEW;
END$$;
CREATE FUNCTION assistant.pin_grant_operating_policy() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE policy security.operating_policy_versions;
BEGIN
 policy:=security.operating_policy_current(NEW.tenant_id,NEW.workspace_id,'assistant');
 INSERT INTO assistant.grant_operating_pins VALUES(NEW.tenant_id,NEW.workspace_id,NEW.operation_id,NEW.delegation_id,'assistant',policy.revision_id);
 RETURN NEW;
END$$;
ALTER FUNCTION assistant.pin_agent_operating_policy() OWNER TO openetrm_security_owner;
ALTER FUNCTION assistant.pin_grant_operating_policy() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.pin_agent_operating_policy(),assistant.pin_grant_operating_policy() FROM PUBLIC;
CREATE TRIGGER operating_policy AFTER INSERT ON assistant.agent_versions FOR EACH ROW EXECUTE FUNCTION assistant.pin_agent_operating_policy();
CREATE TRIGGER operating_policy AFTER INSERT ON assistant.grant_audit FOR EACH ROW WHEN(NEW.outcome='granted') EXECUTE FUNCTION assistant.pin_grant_operating_policy();

COMMIT;
