-- Page-assistant agent administration (docs/ai-assistant.md §5.1, §5.4, §7.5, §9): ManageAgents and UseAgents, the
-- agent reader role held only by agent principals, agents with immutable versions, and create, save, Publish and
-- Retire. Users & access no longer creates or changes agents. Grants, turns and the Usage tab come later.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority and model helpers the operations rely on and the two definitions this migration replaces,
-- then refuse to start while an agent principal holds a role: the agent reader role becomes the only one an
-- agent may hold, and a migration never revokes a grant on its own.
DO $$
DECLARE helper record;held record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('security.apply_administration(jsonb)','ffbd10ab7bc32357bf7e2fc338725b10d683c2b9e75fb87afeba0ffcdf42c6e7',true),
  ('security.administration_directory()','8926c98413ed44308318ad3fd5262c54ce0e80bc5e2c22fae831c0e79d9170c4',true),
  ('security.member_description(uuid,uuid,uuid)','40b3cb4e9fce26d08503a1b14719c3dc7907b8b9eff44fac0ccf4efa58556785',false),
  ('security.use_context(uuid)','decf3b4c66e8565199a4f6a8833e53cc97fccc0dea9953b336d42c69876b1d7d',true),
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',true),
  ('security.use_administration_context(uuid)','c7565bfd02ea7b601e60efa43d0ba3511362928d0b9f20f0a29423ee9eae3115',true),
  ('assistant.model_qualified(uuid)','bcbf74756334e92c969281f24fd98362f369f499c285bdc5cdcdf92e8d4fb73a',false),
  ('assistant.provider_qualified(uuid)','784555e5e2dd89746140ef3ab3a1a87cc06cb7b4482b4dfcaa0ff0a113d0be18',false),
  ('assistant.current_attestation(uuid)','142978f0af07358286fbd725af2e4b8e073a04143401f30cef8db1452e78a51c',false),
  ('security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)','43e35b62e3ad9ea86286250b208edf56f3dd7ddce12801f4288d75b98d7506bb',false),
  ('assistant.price_in_force(uuid,timestamp with time zone)','c1ab2055ec2a37b760c0fb8e15ef9d47ff444eddeac4ced4ad7fe11b28bf391b',false),
  ('assistant.current_model_version(uuid)','bc966aa2132643261ac9bdf28fc7fd3094e296f4da6617b2a69b89f4678464a2',false),
  ('assistant.is_display_name(text)','0fa2f8b4c9a0999d2098f85f44b63ea90a29d615855648f79d8361f11dd90952',false),
  ('assistant.is_plain_text(text)','aec11d6028a08ad4695caef4ed44c07176694a7a9f58e94304990a3cb2b35d0e',false)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'agent_administration: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 FOR held IN SELECT g.principal_id FROM security.role_grants g JOIN security.principals p ON p.id=g.principal_id
  WHERE p.kind='agent' AND NOT g.revoked AND clock_timestamp()<g.granted_to ORDER BY g.principal_id LIMIT 1 LOOP
  RAISE EXCEPTION 'agent_administration: agent principal % holds a role',held.principal_id USING ERRCODE='55000';
 END LOOP;
END$$;

-- Permissions and the agent reader role (§5.4). UseAgents goes only to roles that may delegate, which every agent
-- read requires.
INSERT INTO security.permissions VALUES('ManageAgents'),('UseAgents');
INSERT INTO security.role_permissions SELECT id,'ManageAgents' FROM security.roles WHERE code='access_administrator';
INSERT INTO security.role_permissions SELECT id,'UseAgents' FROM security.roles WHERE code IN('trader','risk_analyst','market_data_editor');
INSERT INTO security.roles(id,code,name) VALUES('a0000000-0000-0000-0000-000000000007','agent_reader','Agent reader');
INSERT INTO security.role_permissions VALUES('a0000000-0000-0000-0000-000000000007','ReadMarketData'),
 ('a0000000-0000-0000-0000-000000000007','ReadTrades'),('a0000000-0000-0000-0000-000000000007','ReadTradeParties');
-- Only an agent principal holds the agent reader role, and an agent principal holds no other. A principal's kind
-- never changes, so the rule holds for every grant once written; revoking a grant is always allowed.
CREATE FUNCTION security.guard_agent_roles() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT NEW.revoked AND(NEW.role_id='a0000000-0000-0000-0000-000000000007')
  IS DISTINCT FROM EXISTS(SELECT 1 FROM security.principals p WHERE p.id=NEW.principal_id AND p.kind='agent') THEN
  RAISE EXCEPTION 'agent_managed' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER agent_roles BEFORE INSERT OR UPDATE ON security.role_grants FOR EACH ROW EXECUTE FUNCTION security.guard_agent_roles();

-- Users & access creates and changes no agent and grants no one the agent reader role (§5.4); otherwise as in
-- 0024. An earlier operation's replay still returns its stored outcome.
CREATE OR REPLACE FUNCTION security.apply_administration(request jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts; previous security.administration_audit; member security.workspace_memberships;
 operation uuid; target uuid; action text; kind text; login text; name text; before_value jsonb; after_value jsonb; requested_roles text[]; desired_enabled boolean;
BEGIN
 IF NOT security.has_permission('ManageIdentities') OR NOT security.has_permission('ManageAccess') THEN RAISE EXCEPTION 'No administration access' USING ERRCODE='42501'; END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND administration;
 IF NOT FOUND OR c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'Exclusive current administration context required' USING ERRCODE='42501'; END IF;
 operation=(request->>'operationId')::uuid;action=request->>'action';name=request->>'name';
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'admin_input' USING ERRCODE='P0001'; END IF;
 SELECT * INTO previous FROM security.administration_audit WHERE(tenant_id,workspace_id,operation_id)=(c.tenant_id,c.workspace_id,operation);
 IF FOUND THEN
  IF previous.actor_id<>c.actor_id OR previous.request<>request THEN RAISE EXCEPTION 'admin_conflict' USING ERRCODE='P0001'; END IF;
  RETURN previous.after_state;
 END IF;
 IF name IS NULL OR btrim(name)='' OR length(name)>80 OR jsonb_typeof(request->'roles') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'admin_input' USING ERRCODE='P0001'; END IF;
 SELECT array_agg(value ORDER BY value) INTO requested_roles FROM jsonb_array_elements_text(request->'roles');
 IF 'agent_reader'=ANY(coalesce(requested_roles,ARRAY[]::text[])) THEN RAISE EXCEPTION 'agent_managed' USING ERRCODE='P0001'; END IF;
 IF coalesce(array_length(requested_roles,1),0)>6 OR EXISTS(SELECT 1 FROM unnest(requested_roles) role WHERE NOT EXISTS(SELECT 1 FROM security.roles WHERE code=role)) OR (SELECT count(*) FROM unnest(requested_roles))<>(SELECT count(DISTINCT x) FROM unnest(requested_roles)x) THEN RAISE EXCEPTION 'admin_input' USING ERRCODE='P0001'; END IF;
 IF action='create' THEN
  login=request->>'login';kind=request->>'kind';
  IF kind='agent' THEN RAISE EXCEPTION 'agent_managed' USING ERRCODE='P0001'; END IF;
  IF login IS NULL OR login!~'^[a-z][a-z0-9_]{0,47}$' OR kind IS NULL OR kind NOT IN('human','service') OR coalesce(array_length(requested_roles,1),0)=0 THEN RAISE EXCEPTION 'admin_input' USING ERRCODE='P0001'; END IF;
  IF(SELECT count(*) FROM security.workspace_memberships WHERE(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id))>=256 THEN RAISE EXCEPTION 'admin_limit' USING ERRCODE='P0001'; END IF;
  target=gen_random_uuid();desired_enabled=true;
  INSERT INTO security.principals VALUES(target,kind,true);
  -- Registration only: this preview issuer never validates a real credential.
  INSERT INTO security.identity_bindings VALUES(c.identity_issuer,login,target,CASE kind WHEN 'human' THEN 'human_interactive' ELSE 'service_credential' END);
  INSERT INTO security.workspace_memberships VALUES(c.tenant_id,c.workspace_id,target,login,name,true,1);
 ELSIF action='update' THEN
  target=(request->>'id')::uuid;desired_enabled=(request->>'enabled')::boolean;
  SELECT * INTO member FROM security.workspace_memberships WHERE(tenant_id,workspace_id,principal_id)=(c.tenant_id,c.workspace_id,target) FOR UPDATE;
  IF FOUND AND EXISTS(SELECT 1 FROM security.principals p WHERE p.id=target AND p.kind='agent') THEN RAISE EXCEPTION 'agent_managed' USING ERRCODE='P0001'; END IF;
  IF NOT FOUND OR member.version IS DISTINCT FROM (request->>'expectedVersion')::integer THEN RAISE EXCEPTION 'admin_conflict' USING ERRCODE='P0001'; END IF;
  IF desired_enabled IS NULL THEN RAISE EXCEPTION 'admin_input' USING ERRCODE='P0001'; END IF;
  before_value=security.member_description(c.tenant_id,c.workspace_id,target);
  IF NOT(desired_enabled AND 'access_administrator'=ANY(coalesce(requested_roles,ARRAY[]::text[]))) AND security.principal_has_permission(target,c.tenant_id,c.workspace_id,'ManageAccess',clock_timestamp())
   AND NOT EXISTS(SELECT 1 FROM security.workspace_memberships m JOIN security.principals p ON p.id=m.principal_id WHERE(m.tenant_id,m.workspace_id)=(c.tenant_id,c.workspace_id) AND m.principal_id<>target AND p.kind IN('human','service') AND security.principal_has_permission(m.principal_id,c.tenant_id,c.workspace_id,'ManageAccess',clock_timestamp()) AND security.principal_has_permission(m.principal_id,c.tenant_id,c.workspace_id,'ManageIdentities',clock_timestamp())) THEN
   RAISE EXCEPTION 'last_administrator' USING ERRCODE='P0001';
  END IF;
  IF target=c.actor_id AND NOT(desired_enabled AND 'access_administrator'=ANY(coalesce(requested_roles,ARRAY[]::text[]))) THEN RAISE EXCEPTION 'self_access_change' USING ERRCODE='P0001'; END IF;
  UPDATE security.workspace_memberships SET display_name=name,enabled=desired_enabled,version=version+1 WHERE(tenant_id,workspace_id,principal_id)=(c.tenant_id,c.workspace_id,target);
  UPDATE security.role_grants SET revoked=true WHERE(tenant_id,workspace_id,principal_id)=(c.tenant_id,c.workspace_id,target) AND NOT revoked;
 ELSE RAISE EXCEPTION 'admin_input' USING ERRCODE='P0001';END IF;
 INSERT INTO security.role_grants(id,principal_id,role_id,tenant_id,workspace_id,granted_from,granted_to,revoked)
 SELECT gen_random_uuid(),target,id,c.tenant_id,c.workspace_id,clock_timestamp(),TIMESTAMPTZ '9999-12-31 00:00:00+00',false FROM security.roles WHERE code=ANY(requested_roles);
 after_value=security.member_description(c.tenant_id,c.workspace_id,target);
 INSERT INTO security.administration_audit VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,target,clock_timestamp(),request,before_value,after_value);
 IF NOT security.has_permission('ManageIdentities') OR NOT security.has_permission('ManageAccess') THEN RAISE EXCEPTION 'Administration access expired during change' USING ERRCODE='42501'; END IF;
 RETURN after_value;
END $$;
-- As in 0026, with each role marked assignable unless it is the agent reader role, which stays listed so agents'
-- roles can be named.
CREATE OR REPLACE FUNCTION security.administration_directory() RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts; result jsonb;
BEGIN
 IF NOT security.has_permission('ManageIdentities') OR NOT security.has_permission('ManageAccess') THEN RAISE EXCEPTION 'No administration access' USING ERRCODE='42501'; END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT jsonb_build_object('members',coalesce((SELECT jsonb_agg(security.member_description(c.tenant_id,c.workspace_id,m.principal_id) ORDER BY m.display_name,m.principal_id)
 FROM security.workspace_memberships m WHERE(m.tenant_id,m.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
 'roles',(SELECT jsonb_agg(jsonb_build_object('code',r.code,'name',r.name,'assignable',r.id<>'a0000000-0000-0000-0000-000000000007',
   'permissions',(SELECT jsonb_agg(rp.permission ORDER BY rp.permission) FROM security.role_permissions rp WHERE rp.role_id=r.id)) ORDER BY r.name) FROM security.roles r),
 'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor_name,'action',a.request->>'action','before',a.before_state,'after',a.after_state) ORDER BY a.recorded_at DESC,a.operation_id)
 FROM(SELECT a.*,m.display_name actor_name FROM security.administration_audit a JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
 WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb)) INTO result;
 IF NOT security.has_permission('ManageIdentities') OR NOT security.has_permission('ManageAccess') THEN RAISE EXCEPTION 'Administration access expired' USING ERRCODE='42501'; END IF;
 RETURN result;
END $$;

-- The fixed tool set (§7.3) with the permissions each read needs; Read current page dispatches to another read and
-- Navigate issues server targets, so neither needs one of its own. Show card joins with step 7's cards.
CREATE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades'])
$$;
-- Distinct tools from the fixed set.
CREATE FUNCTION assistant.are_agent_tools(tools text[]) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT tools IS NOT NULL AND array_position(tools,NULL) IS NULL AND cardinality(tools)=(SELECT count(DISTINCT t) FROM unnest(tools)t)
  AND NOT EXISTS(SELECT 1 FROM unnest(tools)t WHERE t NOT IN(SELECT code FROM assistant.agent_tools()))
$$;
-- An agent's instructions: lines of text an administrator enters, separated by line feeds, with no other control,
-- line or paragraph separator, bidirectional formatting or zero-width no-break character, and no space or line
-- feed at either end. The runtime set bounds the length within this ceiling.
CREATE FUNCTION assistant.is_instructions(t text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog SET standard_conforming_strings=on AS $$
 SELECT length(t) BETWEEN 1 AND 32000 AND t!~'[\x01-\x09\x0b-\x1f\x7f-\x9f\u2028\u2029\u202a-\u202e\u2066-\u2069\ufeff]'
  AND t!~'^[\n \u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]|[\n \u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]$'
$$;

-- Agents (§5.1): the name and principal never change; each audited change advances the version; the published
-- version only moves forward, and Retire is final.
CREATE TABLE assistant.agents(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,principal_id uuid NOT NULL UNIQUE,
 name text NOT NULL CHECK(assistant.is_display_name(name)),
 version integer NOT NULL CHECK(version>0),published_version integer CHECK(published_version>0),retired_at ref_data.instant,
 UNIQUE(id,tenant_id,workspace_id),
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id)
);
-- Names are unique ignoring the case of ASCII letters, whatever the database's locale.
CREATE UNIQUE INDEX agents_name ON assistant.agents(tenant_id,workspace_id,lower(name COLLATE "C"));
-- An agent version (§5.1, §7.5): an exact model version with an effort it records, the caching switch, tools,
-- limits and the grant duration in days; its allowed roles are rows of their own.
CREATE TABLE assistant.agent_versions(
 agent_id uuid NOT NULL REFERENCES assistant.agents(id),version integer NOT NULL CHECK(version>0),
 description text NOT NULL CHECK(length(description) BETWEEN 1 AND 500 AND assistant.is_plain_text(description)),
 instructions text NOT NULL CHECK(assistant.is_instructions(instructions)),
 model_id uuid NOT NULL,model_version integer NOT NULL,reasoning text NOT NULL,caching boolean NOT NULL,
 tools text[] NOT NULL CHECK(assistant.are_agent_tools(tools)),
 max_tool_steps integer NOT NULL CHECK(max_tool_steps BETWEEN 1 AND 100),
 max_input_tokens bigint NOT NULL CHECK(max_input_tokens BETWEEN 1 AND 100000000),
 max_output_tokens integer NOT NULL CHECK(max_output_tokens BETWEEN 1 AND 10000000),
 max_elapsed_seconds integer NOT NULL CHECK(max_elapsed_seconds BETWEEN 1 AND 3600),
 max_retries integer NOT NULL CHECK(max_retries BETWEEN 0 AND 10),
 grant_days integer NOT NULL CHECK(grant_days>0),
 saved_by uuid NOT NULL REFERENCES security.principals(id),saved_at ref_data.instant NOT NULL,
 PRIMARY KEY(agent_id,version),
 FOREIGN KEY(model_id,model_version) REFERENCES assistant.model_versions(model_id,version)
);
ALTER TABLE assistant.agents ADD FOREIGN KEY(id,published_version) REFERENCES assistant.agent_versions(agent_id,version);
CREATE TABLE assistant.agent_version_roles(
 agent_id uuid NOT NULL,version integer NOT NULL,role_id uuid NOT NULL REFERENCES security.roles(id),
 PRIMARY KEY(agent_id,version,role_id),FOREIGN KEY(agent_id,version) REFERENCES assistant.agent_versions(agent_id,version)
);
ALTER TABLE assistant.administration_audit ADD COLUMN agent_id uuid,
 ADD FOREIGN KEY(agent_id,tenant_id,workspace_id) REFERENCES assistant.agents(id,tenant_id,workspace_id),
 ADD CHECK(action NOT IN('create_agent','save_agent','publish_agent','retire_agent') OR agent_id IS NOT NULL);
CREATE FUNCTION assistant.guard_agent() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='INSERT' THEN
  IF NOT EXISTS(SELECT 1 FROM security.principals p WHERE p.id=NEW.principal_id AND p.kind='agent')
   OR NEW.version<>1 OR NEW.published_version IS NOT NULL OR NEW.retired_at IS NOT NULL THEN
   RAISE EXCEPTION 'An agent starts at version 1 with an agent principal, unpublished and active' USING ERRCODE='55000';
  END IF;
 ELSIF TG_OP='DELETE' OR(NEW.id,NEW.tenant_id,NEW.workspace_id,NEW.principal_id,NEW.name) IS DISTINCT FROM(OLD.id,OLD.tenant_id,OLD.workspace_id,OLD.principal_id,OLD.name)
  OR NEW.version<>OLD.version+1 OR OLD.published_version IS NOT NULL AND(NEW.published_version IS NULL OR NEW.published_version<OLD.published_version)
  OR OLD.retired_at IS NOT NULL THEN
  RAISE EXCEPTION 'An agent keeps its identity, name and principal, advances one version at a time, publishes forward and retires once' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_agent BEFORE INSERT OR UPDATE OR DELETE ON assistant.agents FOR EACH ROW EXECUTE FUNCTION assistant.guard_agent();
-- Even for the owner, a version names a model of its agent's workspace, an effort that model version records and an
-- output limit within its own; each allowed role holds UseAgents and every permission its tools need.
CREATE FUNCTION assistant.guard_agent_version() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM assistant.agents a JOIN assistant.models m ON(m.tenant_id,m.workspace_id)=(a.tenant_id,a.workspace_id)
   JOIN assistant.model_versions v ON(v.model_id,v.version)=(m.id,NEW.model_version)
   WHERE a.id=NEW.agent_id AND m.id=NEW.model_id AND NEW.reasoning=ANY(v.reasoning_efforts) AND NEW.max_output_tokens<=v.output_limit) THEN
  RAISE EXCEPTION 'An agent version names a model of its workspace, an effort and an output limit its model version allows' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_agent_version BEFORE INSERT ON assistant.agent_versions FOR EACH ROW EXECUTE FUNCTION assistant.guard_agent_version();
CREATE FUNCTION assistant.guard_agent_role() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE missing record;
BEGIN
 FOR missing IN SELECT r.name,n.permission FROM security.roles r
  CROSS JOIN LATERAL(SELECT 'UseAgents' permission,0 rank UNION ALL SELECT DISTINCT p,1 FROM assistant.agent_versions v
   CROSS JOIN LATERAL unnest(v.tools) t JOIN assistant.agent_tools() a ON a.code=t CROSS JOIN LATERAL unnest(a.permissions) p
   WHERE(v.agent_id,v.version)=(NEW.agent_id,NEW.version))n
  WHERE r.id=NEW.role_id AND NOT EXISTS(SELECT 1 FROM security.role_permissions rp WHERE rp.role_id=r.id AND rp.permission=n.permission)
  ORDER BY n.rank,n.permission LIMIT 1 LOOP
  RAISE EXCEPTION 'agent_role_lacks_permission' USING ERRCODE='P0001',HINT=jsonb_build_object('role',missing.name,'permission',missing.permission)::text;
 END LOOP;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_agent_role BEFORE INSERT ON assistant.agent_version_roles FOR EACH ROW EXECUTE FUNCTION assistant.guard_agent_role();
CREATE TRIGGER immutable_agent_version BEFORE UPDATE OR DELETE ON assistant.agent_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_agent_role BEFORE UPDATE OR DELETE ON assistant.agent_version_roles FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- The bound request context of a human or service principal that currently holds ManageAgents. Agents never
-- administer agents, so a delegated context is refused.
CREATE FUNCTION assistant.agent_actor() RETURNS security.request_contexts
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.id IS NULL OR c.delegation_id IS NOT NULL OR NOT security.has_permission('ManageAgents') THEN
  RAISE EXCEPTION 'No current agent administration permission' USING ERRCODE='42501';
 END IF;
 RETURN c;
END $$;
-- Exclusive admission for the agent commands that write control tables (§5.4): it takes the epoch row before
-- binding, as Users & access does, so the write cannot deadlock against requests already admitted.
CREATE FUNCTION assistant.use_agent_admission(token uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user) THEN
  RAISE EXCEPTION 'Agent administration must be the first transaction admission' USING ERRCODE='42501';
 END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM security.use_context(token);
 PERFORM assistant.agent_actor();
 UPDATE security.request_contexts SET administration=true WHERE id=token;
END $$;
-- A version as Settings › AI shows it, with whether its model version is still the model's current one.
CREATE FUNCTION assistant.agent_version_state(agent uuid,agent_version integer) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('version',v.version,'description',v.description,'instructions',v.instructions,
  'model',jsonb_build_object('id',m.id,'version',v.model_version,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,
   'current',v.model_version=(SELECT c.version FROM assistant.current_model_version(m.id) c)),
  'reasoning',v.reasoning,'caching',v.caching,'tools',coalesce((SELECT jsonb_agg(t ORDER BY t) FROM unnest(v.tools)t),'[]'::jsonb),
  'allowedRoles',coalesce((SELECT jsonb_agg(r.code ORDER BY r.code) FROM assistant.agent_version_roles a JOIN security.roles r ON r.id=a.role_id
   WHERE(a.agent_id,a.version)=(v.agent_id,v.version)),'[]'::jsonb),
  'limits',jsonb_build_object('toolSteps',v.max_tool_steps,'inputTokens',v.max_input_tokens,'outputTokens',v.max_output_tokens,
   'elapsedSeconds',v.max_elapsed_seconds,'retries',v.max_retries),
  'grantDays',v.grant_days,'savedBy',s.display_name,'savedAt',v.saved_at)
 FROM assistant.agent_versions v JOIN assistant.agents g ON g.id=v.agent_id JOIN assistant.models m ON m.id=v.model_id
  JOIN assistant.providers p ON p.id=m.provider_id
  JOIN security.workspace_memberships s ON(s.tenant_id,s.workspace_id,s.principal_id)=(g.tenant_id,g.workspace_id,v.saved_by)
 WHERE(v.agent_id,v.version)=(agent,agent_version);
$$;
CREATE FUNCTION assistant.agent_state(agent uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',a.id,'name',a.name,'version',a.version,'state',CASE WHEN a.retired_at IS NULL THEN 'active' ELSE 'retired' END,
  'publishedVersion',a.published_version,
  'newest',(SELECT assistant.agent_version_state(a.id,max(v.version)) FROM assistant.agent_versions v WHERE v.agent_id=a.id),
  'published',assistant.agent_version_state(a.id,a.published_version))
 FROM assistant.agents a WHERE a.id=agent;
$$;
-- A command's earlier outcome under the same request identity: its stored state if the same actor sent the same
-- request, a conflict if the identity was used for any other administrative change in Settings › AI or Users &
-- access, otherwise none. As for the model commands, the actor's row serializes the actor's commands, so an
-- identical request sent while its original still runs waits for it and returns its outcome.
CREATE FUNCTION assistant.agent_replay(c security.request_contexts,operation uuid,request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE previous assistant.administration_audit;
BEGIN
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 SELECT * INTO previous FROM assistant.administration_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation);
 -- The wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.agent_actor();
 IF FOUND THEN
  IF previous.actor_id<>c.actor_id OR previous.request<>request THEN RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';END IF;
  RETURN previous.after_state;
 END IF;
 IF EXISTS(SELECT 1 FROM security.administration_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation)) THEN
  RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';
 END IF;
 RETURN NULL;
END $$;
-- The checks before an agent version is saved or published (§7.1, §9): the exact model version named is the
-- current one of an enabled, qualified model priced now, on an enabled, qualified provider.
CREATE FUNCTION assistant.check_agent_model(c security.request_contexts,model uuid,model_version integer) RETURNS void
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE m assistant.models;p assistant.providers;
BEGIN
 SELECT x.* INTO m FROM assistant.models x WHERE x.id=model AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id);
 IF NOT FOUND THEN RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';END IF;
 SELECT x.* INTO p FROM assistant.providers x WHERE x.id=m.provider_id;
 IF model_version IS DISTINCT FROM(SELECT v.version FROM assistant.current_model_version(m.id) v) THEN RAISE EXCEPTION 'model_changed' USING ERRCODE='P0001';END IF;
 IF NOT m.enabled THEN RAISE EXCEPTION 'model_disabled' USING ERRCODE='P0001';END IF;
 IF NOT assistant.model_qualified(m.id) THEN RAISE EXCEPTION 'model_unqualified' USING ERRCODE='P0001';END IF;
 IF(assistant.price_in_force(m.id,clock_timestamp())).kind IS DISTINCT FROM 'rates' THEN RAISE EXCEPTION 'model_unpriced' USING ERRCODE='P0001';END IF;
 IF NOT p.enabled THEN RAISE EXCEPTION 'model_provider_disabled' USING ERRCODE='P0001';END IF;
 IF NOT assistant.provider_qualified(p.id) THEN RAISE EXCEPTION 'model_provider_unqualified' USING ERRCODE='P0001';END IF;
END $$;
-- The agent a command names in the caller's workspace, locked, at the version the administrator saw.
CREATE FUNCTION assistant.agent_for_change(c security.request_contexts,agent uuid,expected integer) RETURNS assistant.agents
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE a assistant.agents;
BEGIN
 IF agent IS NULL OR agent='00000000-0000-0000-0000-000000000000'::uuid OR expected IS NULL THEN RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';END IF;
 SELECT x.* INTO a FROM assistant.agents x WHERE x.id=agent AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';END IF;
 IF a.retired_at IS NOT NULL THEN RAISE EXCEPTION 'agent_retired' USING ERRCODE='P0001';END IF;
 IF a.version<>expected THEN RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';END IF;
 RETURN a;
END $$;

-- The Agents tab (§9): this workspace's agents with their newest and published versions, the models a version may
-- name now, the roles that use agents, the tools with the permissions they need, and the latest 100 agent changes.
CREATE FUNCTION assistant.agent_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();
BEGIN
 RETURN jsonb_build_object(
  'agents',coalesce((SELECT jsonb_agg(assistant.agent_state(a.id) ORDER BY lower(a.name),a.id) FROM assistant.agents a
   WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'models',coalesce((SELECT jsonb_agg(jsonb_build_object('id',m.id,'version',v.version,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,
    'outputLimit',v.output_limit,'reasoningEfforts',to_jsonb(v.reasoning_efforts)) ORDER BY p.vendor,m.identifier)
   FROM assistant.models m JOIN assistant.providers p ON p.id=m.provider_id CROSS JOIN LATERAL assistant.current_model_version(m.id) v
   WHERE(m.tenant_id,m.workspace_id)=(c.tenant_id,c.workspace_id) AND m.enabled AND p.enabled AND assistant.model_qualified(m.id)
    AND assistant.provider_qualified(p.id) AND(assistant.price_in_force(m.id,clock_timestamp())).kind='rates'),'[]'::jsonb),
  'roles',(SELECT jsonb_agg(jsonb_build_object('code',r.code,'name',r.name,'permissions',
    (SELECT jsonb_agg(x.permission ORDER BY x.permission) FROM security.role_permissions x WHERE x.role_id=r.id)) ORDER BY r.code)
   FROM security.roles r WHERE EXISTS(SELECT 1 FROM security.role_permissions x WHERE x.role_id=r.id AND x.permission='UseAgents')),
  'tools',(SELECT jsonb_agg(jsonb_build_object('code',t.code,'permissions',to_jsonb(t.permissions)) ORDER BY t.code) FROM assistant.agent_tools() t),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,'agent',a.agent)
    ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,m.display_name actor,g.name agent FROM assistant.administration_audit a
    JOIN assistant.agents g ON g.id=a.agent_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
-- Create (§5.4): an agent principal bound by its own id at the manifest issuer, a member of the workspace, counted in
-- its 256-member limit, and an agent reader. Every check runs first; outside exclusive admission a create that would
-- write refuses with admission_required, which the API answers by repeating it under use_agent_admission.
CREATE FUNCTION assistant.create_agent(operation uuid,name text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();request jsonb:=jsonb_build_object('action','create_agent','operationId',operation,'name',name);
 previous jsonb;agent uuid:=gen_random_uuid();principal uuid:=gen_random_uuid();after_value jsonb;
BEGIN
 previous:=assistant.agent_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 IF name IS NULL OR NOT assistant.is_display_name(name) THEN RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM assistant.agents a WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND lower(a.name COLLATE "C")=lower(create_agent.name COLLATE "C")) THEN
  RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';
 END IF;
 IF(SELECT count(*) FROM security.workspace_memberships m WHERE(m.tenant_id,m.workspace_id)=(c.tenant_id,c.workspace_id))>=256 THEN
  RAISE EXCEPTION 'admin_limit' USING ERRCODE='P0001';
 END IF;
 IF NOT c.administration THEN RAISE EXCEPTION 'admission_required' USING ERRCODE='P0001';END IF;
 INSERT INTO security.principals VALUES(principal,'agent',true);
 INSERT INTO security.identity_bindings VALUES(c.identity_issuer,principal::text,principal,'agent_credential');
 INSERT INTO security.workspace_memberships VALUES(c.tenant_id,c.workspace_id,principal,'agent_'||replace(principal::text,'-',''),name,true,1);
 INSERT INTO security.role_grants VALUES(gen_random_uuid(),principal,'a0000000-0000-0000-0000-000000000007',c.tenant_id,c.workspace_id,clock_timestamp(),
  TIMESTAMPTZ '9999-12-31 00:00:00+00',false);
 INSERT INTO assistant.agents VALUES(agent,c.tenant_id,c.workspace_id,principal,name,1,NULL,NULL);
 INSERT INTO security.administration_audit VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,principal,clock_timestamp(),
  request,NULL,security.member_description(c.tenant_id,c.workspace_id,principal));
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'create_agent',request,NULL,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;
-- Save (§9): the next version, a draft, after the model and provider checks; the database rules for efforts,
-- output, tools and roles refuse the rest. The API passes the runtime set's bounds for instructions and grants.
CREATE FUNCTION assistant.save_agent_version(operation uuid,agent uuid,expected integer,description text,instructions text,model uuid,
 model_version integer,reasoning text,caching boolean,tools text[],allowed_roles text[],tool_steps integer,input_tokens bigint,output_tokens integer,
 elapsed_seconds integer,retries integer,grant_days integer,instructions_limit integer,grant_days_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();previous jsonb;a assistant.agents;v assistant.model_versions;before_value jsonb;after_value jsonb;
 next_version integer;
 request jsonb:=jsonb_build_object('action','save_agent','operationId',operation,'agentId',agent,'expectedVersion',expected,'description',description,'instructions',instructions,
  'modelId',model,'modelVersion',model_version,'reasoning',reasoning,'caching',caching,'tools',to_jsonb(tools),'allowedRoles',to_jsonb(allowed_roles),
  'limits',jsonb_build_object('toolSteps',tool_steps,'inputTokens',input_tokens,'outputTokens',output_tokens,'elapsedSeconds',elapsed_seconds,'retries',retries),
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
  OR retries IS NULL OR retries NOT BETWEEN 0 AND 10 OR grant_days IS NULL OR grant_days<1 THEN
  RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';
 END IF;
 a:=assistant.agent_for_change(c,agent,expected);
 PERFORM assistant.check_agent_model(c,model,model_version);
 SELECT x.* INTO v FROM assistant.model_versions x WHERE(x.model_id,x.version)=(model,model_version);
 IF NOT reasoning=ANY(v.reasoning_efforts) THEN RAISE EXCEPTION 'parameter_required' USING ERRCODE='P0001';END IF;
 IF output_tokens>v.output_limit THEN RAISE EXCEPTION 'model_limit_exceeded' USING ERRCODE='P0001';END IF;
 before_value:=assistant.agent_state(agent);
 next_version:=coalesce((SELECT max(x.version) FROM assistant.agent_versions x WHERE x.agent_id=agent),0)+1;
 INSERT INTO assistant.agent_versions VALUES(agent,next_version,description,instructions,model,model_version,reasoning,caching,tools,
  tool_steps,input_tokens,output_tokens,elapsed_seconds,retries,grant_days,c.actor_id,clock_timestamp());
 INSERT INTO assistant.agent_version_roles SELECT agent,next_version,r.id FROM security.roles r WHERE r.code=ANY(allowed_roles) ORDER BY r.code;
 UPDATE assistant.agents x SET version=x.version+1 WHERE x.id=agent;
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'save_agent',request,before_value,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;
-- Publish (§9): the newest version, after the same model and provider checks, once something newer than the
-- published version exists.
CREATE FUNCTION assistant.publish_agent(operation uuid,agent uuid,expected integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();previous jsonb;a assistant.agents;newest assistant.agent_versions;before_value jsonb;after_value jsonb;
 request jsonb:=jsonb_build_object('action','publish_agent','operationId',operation,'agentId',agent,'expectedVersion',expected);
BEGIN
 previous:=assistant.agent_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 a:=assistant.agent_for_change(c,agent,expected);
 SELECT x.* INTO newest FROM assistant.agent_versions x WHERE x.agent_id=agent ORDER BY x.version DESC LIMIT 1;
 IF NOT FOUND OR newest.version<=coalesce(a.published_version,0) THEN RAISE EXCEPTION 'model_admin_conflict' USING ERRCODE='P0001';END IF;
 PERFORM assistant.check_agent_model(c,newest.model_id,newest.model_version);
 before_value:=assistant.agent_state(agent);
 UPDATE assistant.agents x SET version=x.version+1,published_version=newest.version WHERE x.id=agent;
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'publish_agent',request,before_value,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;
-- Retire (§5.4): permanent; the principal and its membership are disabled and history stays. Like create it writes
-- control tables, so it refuses outside exclusive admission once every check has passed.
CREATE FUNCTION assistant.retire_agent(operation uuid,agent uuid,expected integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();previous jsonb;a assistant.agents;before_value jsonb;after_value jsonb;member_before jsonb;
 request jsonb:=jsonb_build_object('action','retire_agent','operationId',operation,'agentId',agent,'expectedVersion',expected);
BEGIN
 previous:=assistant.agent_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 a:=assistant.agent_for_change(c,agent,expected);
 IF NOT c.administration THEN RAISE EXCEPTION 'admission_required' USING ERRCODE='P0001';END IF;
 before_value:=assistant.agent_state(agent);
 member_before:=security.member_description(c.tenant_id,c.workspace_id,a.principal_id);
 UPDATE security.principals SET enabled=false WHERE id=a.principal_id;
 UPDATE security.workspace_memberships m SET enabled=false,version=m.version+1 WHERE(m.tenant_id,m.workspace_id,m.principal_id)=(c.tenant_id,c.workspace_id,a.principal_id);
 UPDATE assistant.agents x SET version=x.version+1,retired_at=clock_timestamp() WHERE x.id=agent;
 INSERT INTO security.administration_audit VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,a.principal_id,clock_timestamp(),
  request,member_before,security.member_description(c.tenant_id,c.workspace_id,a.principal_id));
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'retire_agent',request,before_value,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' AND tablename IN('agents','agent_versions','agent_version_roles') LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant'
  OR p.oid='security.guard_agent_roles()'::regprocedure LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.use_agent_admission(uuid),assistant.agent_directory(),assistant.create_agent(uuid,text),
 assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer),
 assistant.publish_agent(uuid,uuid,integer),assistant.retire_agent(uuid,uuid,integer) TO openetrm_runtime;
COMMIT;
