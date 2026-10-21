-- Page-assistant grants (docs/ai-assistant.md §5.1, §5.4, §7.1, §8): a user's grant to an agent version, derived from
-- the stored version, with one current grant per user and agent, coverage, Revoke and the grant audit; Retire now also
-- revokes the agent's open grants. Turns, which grants cover, come later.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority helpers grants rely on, the agent helpers Retire uses and the definition this migration replaces,
-- then refuse to start while any delegation is unrevoked: a grant is a delegation with a grant record, and a migration
-- neither adopts nor revokes one on its own.
DO $$
DECLARE helper record;held record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',true),
  ('security.has_pre_pilot_permission(text)','ba2b9b5fbaaf8eadea6a60a149bc5b04d63774721cb8b788911cd29bd41cb436',false),
  ('security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)','43e35b62e3ad9ea86286250b208edf56f3dd7ddce12801f4288d75b98d7506bb',false),
  ('security.use_context(uuid)','decf3b4c66e8565199a4f6a8833e53cc97fccc0dea9953b336d42c69876b1d7d',true),
  ('security.guard_control_change()','40c571d388bc15c7c053ddb937b1382b29847ae0d0c700baba4e0cde89c4f367',false),
  ('security.validate_delegation()','b5eec0e6d968017468a1331d6f2bc5b412823649d4c84acf88daa87996e04048',false),
  ('security.member_description(uuid,uuid,uuid)','40b3cb4e9fce26d08503a1b14719c3dc7907b8b9eff44fac0ccf4efa58556785',false),
  ('assistant.agent_tools()','2bbf2767ce1432bcf18cd7f657d2c613bea2a4e274992a7fc1c5385ebd2fdbd0',false),
  ('assistant.agent_actor()','34563c3da769cf4ea2cc4db224159a8dd8cf80947d0f6a5c3d3bdf28bc97294a',false),
  ('assistant.agent_replay(security.request_contexts,uuid,jsonb)','e8a5c5d1b7bf14cbc2a21da88785cf2fa971900280d97659f6b6b72e5ce16b97',false),
  ('assistant.agent_for_change(security.request_contexts,uuid,integer)','872bc32f607f3657a0979ea0336c09b5841f22f19353dc27718cd610e6f2ea57',false),
  ('assistant.agent_state(uuid)','1b44fee369e0c321dadf1da0720be36a19ef47566cf53a5ab661f52df7732202',false),
  ('assistant.agent_version_state(uuid,integer)','ec11d091851215da124b2013a836f37785866160fac154e4f0e35918c5fe00e3',false),
  ('assistant.current_model_version(uuid)','bc966aa2132643261ac9bdf28fc7fd3094e296f4da6617b2a69b89f4678464a2',false),
  ('assistant.retire_agent(uuid,uuid,integer)','8204b1cff9b90f73ce3666ffd5c13ef1050e11e60c2ad1c53900b29321c257ea',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'agent_grants: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 FOR held IN SELECT d.delegator_id,d.agent_id FROM security.agent_delegations d WHERE NOT d.revoked ORDER BY d.delegator_id,d.agent_id LIMIT 1 LOOP
  RAISE EXCEPTION 'agent_grants: % holds an unrevoked delegation to agent %',held.delegator_id,held.agent_id USING ERRCODE='55000';
 END LOOP;
END$$;

-- One current grant per user and agent (§7.1): a grant revokes the user's earlier one to that agent, expired or not.
CREATE UNIQUE INDEX agent_delegations_current ON security.agent_delegations(tenant_id,workspace_id,delegator_id,agent_id) WHERE NOT revoked;

-- The grant audit (§5.1). A grant is a delegation with a granted record, which keeps the version, model identity and
-- permissions it was given for; an ending record says how it ended: replaced by the user's next grant to the agent,
-- revoked by the user, or with the agent's Retire. The provider is the model's, which never changes.
CREATE TABLE assistant.grant_audit(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,operation_id uuid NOT NULL,
 delegation_id uuid NOT NULL REFERENCES security.agent_delegations(id),
 outcome text NOT NULL CHECK(outcome IN('granted','replaced','revoked','retired')),
 delegator_id uuid NOT NULL REFERENCES security.principals(id),
 agent_id uuid NOT NULL,agent_version integer NOT NULL,model_id uuid NOT NULL,model_version integer NOT NULL,
 permissions text[] NOT NULL CHECK(array_position(permissions,NULL) IS NULL),
 expires_at timestamptz NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),recorded_at timestamptz NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,operation_id,delegation_id),
 FOREIGN KEY(agent_id,tenant_id,workspace_id) REFERENCES assistant.agents(id,tenant_id,workspace_id),
 FOREIGN KEY(agent_id,agent_version) REFERENCES assistant.agent_versions(agent_id,version),
 FOREIGN KEY(model_id,model_version) REFERENCES assistant.model_versions(model_id,version)
);
CREATE UNIQUE INDEX grant_audit_granted ON assistant.grant_audit(delegation_id) WHERE outcome='granted';
CREATE UNIQUE INDEX grant_audit_ended ON assistant.grant_audit(delegation_id) WHERE outcome<>'granted';
CREATE TRIGGER immutable_grant_audit BEFORE UPDATE OR DELETE ON assistant.grant_audit FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- The bound, unexpired and undelegated request context of an enabled member who is a person; with uses, one who
-- currently holds UseAgents, and CreateDelegations, which every delegated read requires of its delegator. Agents and
-- services never grant.
CREATE FUNCTION assistant.grant_actor(uses boolean) RETURNS security.request_contexts
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL OR c.delegation_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM security.principals p JOIN security.workspace_memberships m ON m.principal_id=p.id
   WHERE p.id=c.actor_id AND p.kind='human' AND p.enabled AND(m.tenant_id,m.workspace_id)=(c.tenant_id,c.workspace_id) AND m.enabled)
  OR uses AND NOT(security.has_permission('UseAgents') AND security.has_permission('CreateDelegations')) THEN
  RAISE EXCEPTION 'No current agent grant permission' USING ERRCODE='42501';
 END IF;
 RETURN c;
END $$;
-- Exclusive admission for grant and Revoke, which write control tables (§5.4): it takes the epoch row before binding,
-- as Users & access does, so the write cannot deadlock against requests already admitted.
CREATE FUNCTION assistant.use_grant_admission(token uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user) THEN
  RAISE EXCEPTION 'A grant must be the first transaction admission' USING ERRCODE='42501';
 END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM security.use_context(token);
 PERFORM assistant.grant_actor(false);
 UPDATE security.request_contexts SET administration=true WHERE id=token;
END $$;
-- The permissions an agent version's tools need, as a grant lists them.
CREATE FUNCTION assistant.version_permissions(agent uuid,agent_version integer) RETURNS text[]
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(array_agg(DISTINCT p COLLATE "C" ORDER BY p COLLATE "C"),ARRAY[]::text[]) FROM assistant.agent_versions v CROSS JOIN LATERAL unnest(v.tools) t
  JOIN assistant.agent_tools() a ON a.code=t CROSS JOIN LATERAL unnest(a.permissions) p
 WHERE(v.agent_id,v.version)=(version_permissions.agent,version_permissions.agent_version);
$$;
-- Whether a user may use an agent version (§7.1): an active agent's published version, by an allowed role held now, or
-- its newest version while still a draft, by a user who manages agents. In these SQL functions a column of the same
-- name would shadow an unqualified parameter, so parameters are named with their function.
CREATE FUNCTION assistant.may_use(c security.request_contexts,a assistant.agents,agent_version integer) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT a.retired_at IS NULL AND(
  may_use.agent_version=a.published_version AND EXISTS(SELECT 1 FROM assistant.agent_version_roles r JOIN security.role_grants g ON g.role_id=r.role_id
    JOIN security.principals p ON p.id=g.principal_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(g.tenant_id,g.workspace_id,g.principal_id)
   WHERE(r.agent_id,r.version)=(a.id,may_use.agent_version) AND(g.principal_id,g.tenant_id,g.workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
    AND p.enabled AND m.enabled AND NOT g.revoked AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to)
  OR may_use.agent_version>coalesce(a.published_version,0)
   AND may_use.agent_version=(SELECT max(v.version) FROM assistant.agent_versions v WHERE v.agent_id=a.id)
   AND security.has_permission('ManageAgents'));
$$;
-- A grant's facts, from its granted record: they never change, so a repeated command answers as its original did.
CREATE FUNCTION assistant.grant_facts(delegation uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('id',g.delegation_id,'agentId',g.agent_id,'version',g.agent_version,'modelId',g.model_id,'modelVersion',g.model_version,
  'permissions',to_jsonb(g.permissions),'grantedAt',g.recorded_at,'expiresAt',g.expires_at)
 FROM assistant.grant_audit g WHERE g.delegation_id=grant_facts.delegation AND g.outcome='granted';
$$;
-- The user's current grant covering an agent version (§7.1): unrevoked and unexpired, from the user to the version's
-- agent, given for a version with the same model identity, and listing every permission the version needs.
CREATE FUNCTION assistant.covering_grant(c security.request_contexts,agent uuid,agent_version integer) RETURNS uuid
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT d.id FROM assistant.agents a JOIN assistant.agent_versions v ON v.agent_id=a.id AND v.version=covering_grant.agent_version
  JOIN security.agent_delegations d ON d.agent_id=a.principal_id AND(d.tenant_id,d.workspace_id,d.delegator_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND NOT d.revoked AND d.granted_from<=clock_timestamp() AND clock_timestamp()<d.granted_to
  JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted' AND g.model_id=v.model_id
 WHERE a.id=covering_grant.agent AND coalesce((SELECT array_agg(x.permission) FROM security.delegation_permissions x WHERE x.delegation_id=d.id),
  ARRAY[]::text[])@>assistant.version_permissions(a.id,v.version);
$$;
-- A grant command's earlier outcome under the same request identity (§8): the original answer if the same user sent
-- the same request, a conflict if another grant, Revoke or Retire recorded the identity, otherwise none. Identities are
-- scoped by the audit a command writes, and each writer of the grant audit refuses one another command recorded there.
-- The user's principal row serializes the user's commands, so an identical request sent while its original still runs
-- waits for it.
CREATE FUNCTION assistant.grant_replay(c security.request_contexts,uses boolean,operation uuid,agent uuid,agent_version integer,delegation uuid)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE previous assistant.grant_audit;
BEGIN
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'grant_input' USING ERRCODE='P0001';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 -- A grant answers from its granted record, a Revoke from its revoked one.
 SELECT * INTO previous FROM assistant.grant_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation)
  ORDER BY a.outcome='replaced' LIMIT 1;
 -- The wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.grant_actor(uses);
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF previous.actor_id<>c.actor_id OR delegation IS NULL AND(previous.outcome<>'granted' OR(previous.agent_id,previous.agent_version)<>(agent,agent_version))
  OR delegation IS NOT NULL AND(previous.outcome<>'revoked' OR previous.delegation_id<>delegation) THEN
  RAISE EXCEPTION 'grant_conflict' USING ERRCODE='P0001';
 END IF;
 RETURN assistant.grant_facts(previous.delegation_id)||CASE WHEN delegation IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('revokedAt',previous.recorded_at) END;
END $$;

-- The agents a user may use (§7.1, §10.1): each usable version with its model, the permissions it needs, its grant
-- duration and the user's current grant covering it, if any.
CREATE FUNCTION assistant.usable_agents() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);
BEGIN
 RETURN jsonb_build_object('agents',coalesce((SELECT jsonb_agg(jsonb_build_object('id',a.id,'name',a.name,'version',v.version,
   'draft',v.version>coalesce(a.published_version,0),
   'model',jsonb_build_object('id',m.id,'version',v.model_version,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name),
   'permissions',to_jsonb(assistant.version_permissions(a.id,v.version)),'grantDays',v.grant_days,
   'grant',(SELECT jsonb_build_object('id',g.delegation_id,'expiresAt',g.expires_at) FROM assistant.grant_audit g
    WHERE g.delegation_id=assistant.covering_grant(c,a.id,v.version) AND g.outcome='granted'))
   ORDER BY lower(a.name),a.id,v.version)
  FROM assistant.agents a JOIN assistant.agent_versions v ON v.agent_id=a.id JOIN assistant.models m ON m.id=v.model_id
   JOIN assistant.providers p ON p.id=m.provider_id
  WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND assistant.may_use(c,a,v.version)),'[]'::jsonb));
END $$;
-- The user's current grants (§7.1), which Revoke names; a user who no longer uses agents still lists them.
CREATE FUNCTION assistant.grants() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);
BEGIN
 RETURN jsonb_build_object('grants',coalesce((SELECT jsonb_agg(assistant.grant_facts(d.id)||jsonb_build_object('agentName',a.name,'vendor',p.vendor,
   'identifier',m.identifier,'displayName',m.display_name) ORDER BY lower(a.name),d.id)
  FROM security.agent_delegations d JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted'
   JOIN assistant.agents a ON a.id=g.agent_id JOIN assistant.models m ON m.id=g.model_id JOIN assistant.providers p ON p.id=m.provider_id
  WHERE(d.tenant_id,d.workspace_id,d.delegator_id)=(c.tenant_id,c.workspace_id,c.actor_id) AND NOT d.revoked
   AND d.granted_from<=clock_timestamp() AND clock_timestamp()<d.granted_to),'[]'::jsonb));
END $$;
-- Grant (§7.1): the request names only the agent version; the delegator is the bound user, and the principal,
-- permissions and expiry come from the stored version. It refuses rather than grants partially, and it replaces the
-- user's earlier grant to the agent. Like the other control-table writes it refuses outside exclusive admission once
-- every check has passed.
CREATE FUNCTION assistant.grant_agent(operation uuid,agent uuid,agent_version integer,grant_days_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
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
END $$;
-- Revoke (§7.1): only its delegator revokes a grant, needing no UseAgents, and anyone else's grant is answered as an
-- unknown one. Revoking a grant already revoked, replaced or ended by Retire answers when it ended and writes nothing.
CREATE FUNCTION assistant.revoke_grant(operation uuid,delegation uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);previous jsonb;d security.agent_delegations;g assistant.grant_audit;at timestamptz;
BEGIN
 IF delegation IS NULL OR delegation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'grant_input' USING ERRCODE='P0001';END IF;
 previous:=assistant.grant_replay(c,false,operation,NULL,NULL,delegation);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 SELECT x.* INTO d FROM security.agent_delegations x WHERE x.id=delegation AND(x.tenant_id,x.workspace_id,x.delegator_id)=(c.tenant_id,c.workspace_id,c.actor_id)
  FOR NO KEY UPDATE;
 SELECT x.* INTO g FROM assistant.grant_audit x WHERE x.delegation_id=d.id AND x.outcome='granted';
 IF g.delegation_id IS NULL THEN RAISE EXCEPTION 'grant_unknown' USING ERRCODE='P0001';END IF;
 IF d.revoked THEN
  RETURN assistant.grant_facts(d.id)||jsonb_build_object('revokedAt',(SELECT x.recorded_at FROM assistant.grant_audit x WHERE x.delegation_id=d.id AND x.outcome<>'granted'));
 END IF;
 IF NOT c.administration THEN RAISE EXCEPTION 'admission_required' USING ERRCODE='P0001';END IF;
 at:=clock_timestamp();
 UPDATE security.agent_delegations SET revoked=true WHERE id=d.id;
 INSERT INTO assistant.grant_audit VALUES(c.tenant_id,c.workspace_id,operation,d.id,'revoked',g.delegator_id,g.agent_id,g.agent_version,g.model_id,
  g.model_version,g.permissions,g.expires_at,c.actor_id,at);
 PERFORM assistant.grant_actor(false);
 RETURN assistant.grant_facts(d.id)||jsonb_build_object('revokedAt',at);
END $$;
-- Retire (§5.4): permanent; the principal and its membership are disabled, its open grants are revoked, each recorded
-- against the administrator under the Retire's request identity, which a grant command must not have used, and history
-- stays. Like create it writes control tables, so it refuses outside exclusive admission once every check has passed.
-- Its running turns join with turns.
CREATE OR REPLACE FUNCTION assistant.retire_agent(operation uuid,agent uuid,expected integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();previous jsonb;a assistant.agents;before_value jsonb;after_value jsonb;member_before jsonb;
 request jsonb:=jsonb_build_object('action','retire_agent','operationId',operation,'agentId',agent,'expectedVersion',expected);
BEGIN
 previous:=assistant.agent_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 IF EXISTS(SELECT 1 FROM assistant.grant_audit g WHERE(g.tenant_id,g.workspace_id,g.operation_id)=(c.tenant_id,c.workspace_id,operation)) THEN
  RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';
 END IF;
 a:=assistant.agent_for_change(c,agent,expected);
 IF NOT c.administration THEN RAISE EXCEPTION 'admission_required' USING ERRCODE='P0001';END IF;
 before_value:=assistant.agent_state(agent);
 member_before:=security.member_description(c.tenant_id,c.workspace_id,a.principal_id);
 UPDATE security.principals SET enabled=false WHERE id=a.principal_id;
 UPDATE security.workspace_memberships m SET enabled=false,version=m.version+1 WHERE(m.tenant_id,m.workspace_id,m.principal_id)=(c.tenant_id,c.workspace_id,a.principal_id);
 INSERT INTO assistant.grant_audit SELECT c.tenant_id,c.workspace_id,operation,g.delegation_id,'retired',g.delegator_id,g.agent_id,g.agent_version,
   g.model_id,g.model_version,g.permissions,g.expires_at,c.actor_id,clock_timestamp()
  FROM security.agent_delegations d JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted'
  WHERE d.agent_id=a.principal_id AND NOT d.revoked;
 UPDATE security.agent_delegations SET revoked=true WHERE agent_id=a.principal_id AND NOT revoked;
 UPDATE assistant.agents x SET version=x.version+1,retired_at=clock_timestamp() WHERE x.id=agent;
 INSERT INTO security.administration_audit VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,a.principal_id,clock_timestamp(),
  request,member_before,security.member_description(c.tenant_id,c.workspace_id,a.principal_id));
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'retire_agent',request,before_value,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;

ALTER TABLE assistant.grant_audit OWNER TO openetrm_security_owner;
ALTER TABLE assistant.grant_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.grant_audit FORCE ROW LEVEL SECURITY;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.use_grant_admission(uuid),assistant.usable_agents(),assistant.grants(),
 assistant.grant_agent(uuid,uuid,integer,integer),assistant.revoke_grant(uuid,uuid) TO openetrm_runtime;
COMMIT;
