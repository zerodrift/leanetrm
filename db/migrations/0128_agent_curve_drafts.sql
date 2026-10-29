-- The page assistant may save a custom-curve draft for the person (D260): each draft generation records who saved it, the
-- assistant's scope is a new curve or a draft whose latest generation it saved in the same conversation, the tool needs
-- market data and market-definition authoring, and a draft target opens a successful save step of its own turn that saved a draft. An agent
-- writes a draft only through the ordinary save's tables, for the one curve version it claimed for a step of its turn,
-- while it holds DraftCustomCurves and its delegator holds WriteMarketDefinitions; it never holds WriteMarketDefinitions.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Fail closed if a helper this migration replaces differs from the reviewed contract.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.agent_tools()','5475089d48459b413e32724b4970c9069f1eecdd7196ce88189c9b191734a8fb',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]),
  ('assistant.is_navigation_target(assistant.turns,jsonb)','14937a42cc083b927568cf66dea16f5c01e0dd9dad926f51fae583a3de1d12d3',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]),
  ('security.custom_curve_visible(uuid,uuid,integer)','a3bfdf717fcd93d7e2eb606b14e8f5139702b6dffe2f8aaebf0e44ec1ca0f88a',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner',
    'openetrm_risk_worker=X/openetrm_security_owner']::aclitem[])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl=helper.acl)
  THEN RAISE EXCEPTION 'curve_drafts: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- The agent's own draft authority, held only by the agent reader role (§5.4): it never holds WriteMarketDefinitions.
INSERT INTO security.permissions VALUES('DraftCustomCurves');
INSERT INTO security.role_permissions VALUES('a0000000-0000-0000-0000-000000000007','DraftCustomCurves');

-- Who saved each draft generation: a person, or the assistant with the turn and step that saved it. The step is recorded
-- in the transaction that saves the draft, so its reference is checked at commit.
CREATE TABLE market_data.custom_curve_generation_savers(
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,generation integer NOT NULL,
 saved_by text NOT NULL CHECK(saved_by IN('person','assistant')),
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 turn_id uuid REFERENCES assistant.turns(id),step integer,
 PRIMARY KEY(catalog_id,curve_id,version,generation),
 FOREIGN KEY(catalog_id,curve_id,version) REFERENCES market_data.custom_curve_versions(catalog_id,curve_id,version),
 FOREIGN KEY(turn_id,step) REFERENCES assistant.tool_steps(turn_id,ordinal) DEFERRABLE INITIALLY DEFERRED,
 CHECK((saved_by='assistant')=(turn_id IS NOT NULL) AND(saved_by='assistant')=(step IS NOT NULL))
);
-- A record names its draft's current generation and never changes. The rule reads the version whoever writes, so a
-- writer who may not see the draft is refused by its insert policy.
CREATE FUNCTION market_data.guard_custom_curve_saver() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v market_data.custom_curve_versions;
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Curve savers are immutable' USING ERRCODE='55000';END IF;
 SELECT * INTO v FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=(NEW.catalog_id,NEW.curve_id,NEW.version);
 IF v.state IS DISTINCT FROM 'draft' THEN RAISE EXCEPTION 'Only a draft generation records its saver' USING ERRCODE='55000';END IF;
 IF v.generation<>NEW.generation THEN RAISE EXCEPTION 'Stale curve generation' USING ERRCODE='40001';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_custom_curve_saver BEFORE INSERT OR UPDATE OR DELETE ON market_data.custom_curve_generation_savers
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve_saver();

-- Whether the bound agent context may write a custom-curve draft in `catalog` for `turn` now (D260): the agent principal
-- holds DraftCustomCurves; its grant is current, is the running turn's, lists WriteMarketDefinitions and was given for a
-- version that switches on Save curve draft, as the turn's version does; and the delegator currently holds
-- WriteMarketDefinitions and CreateDelegations. The catalog is the turn's, in the context's workspace.
CREATE FUNCTION security.custom_curve_agent_authority(c security.request_contexts,catalog uuid,turn uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT coalesce(c.delegation_id IS NOT NULL AND c.market_context_id IS NULL AND c.expires_at>clock_timestamp() AND EXISTS(
  SELECT 1 FROM assistant.turns t JOIN security.agent_delegations d ON d.id=t.delegation_id JOIN assistant.agents a ON a.id=t.agent_id
   JOIN assistant.agent_versions v ON(v.agent_id,v.version)=(t.agent_id,t.agent_version)
   JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted'
   JOIN assistant.agent_versions granted ON(granted.agent_id,granted.version)=(g.agent_id,g.agent_version)
  WHERE t.id=custom_curve_agent_authority.turn AND t.state='running' AND t.delegation_id=c.delegation_id
   AND(t.tenant_id,t.workspace_id,t.catalog_id)=(c.tenant_id,c.workspace_id,custom_curve_agent_authority.catalog)
   AND d.agent_id=c.actor_id AND a.principal_id=c.actor_id AND NOT d.revoked AND d.granted_from<=clock_timestamp() AND clock_timestamp()<d.granted_to
   AND 'save_custom_curve_draft'=ANY(v.tools) AND 'save_custom_curve_draft'=ANY(granted.tools)
   AND EXISTS(SELECT 1 FROM security.delegation_permissions p WHERE p.delegation_id=d.id AND p.permission='WriteMarketDefinitions')
   AND EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE s.catalog_id=custom_curve_agent_authority.catalog
    AND(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id))
   AND security.principal_has_permission(c.actor_id,c.tenant_id,c.workspace_id,'DraftCustomCurves',clock_timestamp())
   AND security.principal_has_permission(d.delegator_id,c.tenant_id,c.workspace_id,'WriteMarketDefinitions',clock_timestamp())
   AND security.principal_has_permission(d.delegator_id,c.tenant_id,c.workspace_id,'CreateDelegations',clock_timestamp())),false);
$$;
-- The curve version an agent context claimed for one step of its turn, and the generation it saw then, none for a new
-- curve, whose claim is its first version. A request context binds one transaction, so a claim lasts that long.
CREATE TABLE security.custom_curve_agent_drafts(
 context_id uuid PRIMARY KEY REFERENCES security.request_contexts(id) ON DELETE CASCADE,
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL CHECK(version BETWEEN 1 AND 64),generation integer CHECK(generation>0),
 turn_id uuid NOT NULL REFERENCES assistant.turns(id),step integer NOT NULL CHECK(step>0),
 CHECK(generation IS NOT NULL OR version=1)
);
ALTER TABLE security.custom_curve_agent_drafts ENABLE ROW LEVEL SECURITY;
ALTER TABLE security.custom_curve_agent_drafts FORCE ROW LEVEL SECURITY;
REVOKE ALL ON security.custom_curve_agent_drafts FROM PUBLIC;
-- Whether the bound context is an agent's that claimed this curve (any of its versions when `version` is null) and may
-- still write it: each draft write rechecks the authority, so losing it after the claim refuses the write.
CREATE FUNCTION security.custom_curve_agent_draft(catalog uuid,curve uuid,version integer) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM security.request_contexts c JOIN security.custom_curve_agent_drafts a ON a.context_id=c.id
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
   AND(a.catalog_id,a.curve_id)=(custom_curve_agent_draft.catalog,custom_curve_agent_draft.curve)
   AND a.version=coalesce(custom_curve_agent_draft.version,a.version) AND security.custom_curve_agent_authority(c,a.catalog_id,a.turn_id));
$$;
-- Every write made under an agent context is checked when its transaction commits, or at the end of its statement once
-- constraints are set immediate, so no write escapes it: the agent's authority still holds, and its claim has saved
-- exactly one new draft generation, recorded as the assistant's for the claimed turn and a successful save step of it.
CREATE FUNCTION security.check_custom_curve_agent_write() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;a security.custom_curve_agent_drafts;v market_data.custom_curve_versions;
BEGIN
 SELECT * INTO c FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned()
  AND r.runtime_login=session_user;
 IF c.delegation_id IS NULL THEN RETURN NULL;END IF;
 SELECT * INTO a FROM security.custom_curve_agent_drafts x WHERE x.context_id=c.id;
 IF a.context_id IS NULL OR NOT security.custom_curve_agent_authority(c,a.catalog_id,a.turn_id) THEN
  RAISE EXCEPTION 'delegation_denied' USING ERRCODE='42501';
 END IF;
 SELECT * INTO v FROM market_data.custom_curve_versions x WHERE(x.catalog_id,x.curve_id,x.version)=(a.catalog_id,a.curve_id,a.version);
 IF v.state IS DISTINCT FROM 'draft' OR v.generation IS DISTINCT FROM coalesce(a.generation,0)+1 OR NOT EXISTS(
   SELECT 1 FROM market_data.custom_curve_generation_savers s JOIN assistant.tool_steps t ON(t.turn_id,t.ordinal)=(s.turn_id,s.step)
   WHERE(s.catalog_id,s.curve_id,s.version,s.generation)=(v.catalog_id,v.curve_id,v.version,v.generation)
    AND(s.saved_by,s.turn_id,s.step)=('assistant',a.turn_id,a.step)
    AND(t.tool,t.operation)=('save_custom_curve_draft','custom_curves.save_draft_v1') AND t.code IS NULL) THEN
  RAISE EXCEPTION 'custom_curve_agent_saver' USING ERRCODE='23514';
 END IF;
 RETURN NULL;
END $$;

-- The bound actor who may record a save as `saved_by`: an author of the catalog recording a person's save undelegated, or an
-- agent recording the assistant's save for the turn and step its claim names.
CREATE FUNCTION security.custom_curve_saver(catalog uuid,curve uuid,version integer,saved_by text,turn uuid,step integer) RETURNS uuid
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT c.actor_id FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
  AND c.runtime_login=session_user AND CASE custom_curve_saver.saved_by
   WHEN 'person' THEN c.delegation_id IS NULL AND custom_curve_saver.turn IS NULL AND security.market_access(custom_curve_saver.catalog,'WriteMarketDefinitions')
   WHEN 'assistant' THEN security.custom_curve_agent_draft(custom_curve_saver.catalog,custom_curve_saver.curve,custom_curve_saver.version)
    AND EXISTS(SELECT 1 FROM security.custom_curve_agent_drafts a WHERE a.context_id=c.id
     AND(a.turn_id,a.step)=(custom_curve_saver.turn,custom_curve_saver.step))
   ELSE false END;
$$;
ALTER TABLE market_data.custom_curve_generation_savers ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.custom_curve_generation_savers FORCE ROW LEVEL SECURITY;
GRANT SELECT ON market_data.custom_curve_generation_savers TO openetrm_runtime,openetrm_security_owner;
GRANT INSERT ON market_data.custom_curve_generation_savers TO openetrm_runtime;
CREATE POLICY current_insert ON market_data.custom_curve_generation_savers AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context());
CREATE POLICY scoped_read ON market_data.custom_curve_generation_savers FOR SELECT TO openetrm_runtime
 USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY scoped_insert ON market_data.custom_curve_generation_savers FOR INSERT TO openetrm_runtime
 WITH CHECK(actor_id=security.custom_curve_saver(catalog_id,curve_id,version,saved_by,turn_id,step) AND security.custom_curve_visible(catalog_id,curve_id,version));
-- The risk worker reads who saved each generation, as it reads curve versions (0076): under the same visibility and its own
-- live service authority, never the actor, turn or step, and never inserting.
GRANT SELECT(catalog_id,curve_id,version,generation,saved_by) ON market_data.custom_curve_generation_savers TO openetrm_risk_worker;
CREATE POLICY risk_worker_scoped_read ON market_data.custom_curve_generation_savers FOR SELECT TO openetrm_risk_worker
 USING(security.custom_curve_visible(catalog_id,curve_id,version));
CREATE POLICY risk_worker_service ON market_data.custom_curve_generation_savers AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker
 USING(risk.worker_read());

-- Whether the assistant, in `turn`, may save this curve version: a curve the catalog does not have yet, or a draft whose
-- latest generation the assistant saved in the same conversation. A person's generation, one with no recorded saver and
-- a published version are never the assistant's to change.
CREATE FUNCTION security.custom_curve_agent_writable(catalog uuid,curve uuid,version integer,turn uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT coalesce(catalog IS NOT NULL AND curve IS NOT NULL AND version IS NOT NULL
  AND EXISTS(SELECT 1 FROM assistant.turns t WHERE t.id=custom_curve_agent_writable.turn)
  AND(NOT EXISTS(SELECT 1 FROM market_data.custom_curves c WHERE(c.catalog_id,c.id)=(custom_curve_agent_writable.catalog,custom_curve_agent_writable.curve))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_versions v
    JOIN market_data.custom_curve_generation_savers s ON(s.catalog_id,s.curve_id,s.version,s.generation)=(v.catalog_id,v.curve_id,v.version,v.generation)
    JOIN assistant.turns saver ON saver.id=s.turn_id JOIN assistant.turns t ON t.id=custom_curve_agent_writable.turn
    WHERE(v.catalog_id,v.curve_id,v.version)=(custom_curve_agent_writable.catalog,custom_curve_agent_writable.curve,custom_curve_agent_writable.version)
     AND v.state='draft' AND s.saved_by='assistant' AND saver.conversation_id=t.conversation_id)),false);
$$;

-- An agent context claims the one curve version a step of its running turn saves, before writing it, fencing the turn by
-- its token and the current worker generation as the turn's worker writes do. It holds the turn, so a cancel waits for
-- the save, and the version, so a person's save or a publication waits too, and remembers the generation it saw. Refused `delegation_denied` without the draft authority,
-- `custom_curve_agent_scope` outside the assistant's scope, `custom_curve_catalog_limit` for a new curve in a catalog that already
-- holds 64, and `custom_curve_agent_claimed` for a second claim.
CREATE FUNCTION security.claim_custom_curve_draft(catalog uuid,curve uuid,version integer,turn uuid,token bytea,step integer) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;v market_data.custom_curve_versions;
BEGIN
 SELECT * INTO c FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned()
  AND r.runtime_login=session_user;
 IF c.id IS NULL OR catalog IS NULL OR curve IS NULL OR version IS NULL OR token IS NULL OR step IS NULL OR step<1
  OR NOT security.current_write_context() THEN RAISE EXCEPTION 'delegation_denied' USING ERRCODE='42501';END IF;
 -- The turn as its worker writes it: the current worker generation, then the turn, held until commit, refusing a stale
 -- worker or another token `agent_runtime_unavailable`.
 PERFORM assistant.fenced_turn(claim_custom_curve_draft.turn,claim_custom_curve_draft.token);
 IF NOT security.custom_curve_agent_authority(c,catalog,turn) THEN RAISE EXCEPTION 'delegation_denied' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM security.custom_curve_agent_drafts a WHERE a.context_id=c.id) THEN
  RAISE EXCEPTION 'custom_curve_agent_claimed' USING ERRCODE='P0001';
 END IF;
 SELECT * INTO v FROM market_data.custom_curve_versions x
  WHERE(x.catalog_id,x.curve_id,x.version)=(claim_custom_curve_draft.catalog,claim_custom_curve_draft.curve,claim_custom_curve_draft.version) FOR UPDATE;
 IF NOT security.custom_curve_agent_writable(catalog,curve,version,turn) OR v.curve_id IS NULL AND version<>1 THEN
  RAISE EXCEPTION 'custom_curve_agent_scope' USING ERRCODE='P0001';
 END IF;
 -- A catalog holds at most 64 curves. The bound counts every curve of the catalog, drafts the agent cannot see included.
 IF v.curve_id IS NULL AND(SELECT count(*) FROM market_data.custom_curves x WHERE x.catalog_id=claim_custom_curve_draft.catalog)>=64 THEN
  RAISE EXCEPTION 'custom_curve_catalog_limit' USING ERRCODE='P0001';
 END IF;
 INSERT INTO security.custom_curve_agent_drafts VALUES(c.id,catalog,curve,version,v.generation,turn,step);
END $$;

-- A claimed draft is the agent's to read and to write through the ordinary save's tables: each author policy gains an
-- agent twin naming the claim where the author's permission stood. Publishing, activation and every other table keep
-- their author permissions, which an agent never holds.
DO $agent$
DECLARE p record;agent text;marker text:='security.market_access(catalog_id, ''WriteMarketDefinitions''::text)';
BEGIN
 FOR p IN SELECT * FROM pg_policies WHERE schemaname='market_data' AND policyname IN('scoped_insert','scoped_update','scoped_delete')
  AND tablename IN('custom_curves','custom_curve_versions','custom_curve_nodes','custom_curve_edges','custom_curve_node_sources',
   'custom_curve_node_calendars','custom_curve_node_schedules','custom_curve_node_published_sources','custom_curve_contract_weights',
   'custom_curve_panel_weights')
  AND NOT(tablename='custom_curve_versions' AND cmd='DELETE') ORDER BY tablename,policyname LOOP
  agent:=CASE p.tablename WHEN 'custom_curves' THEN 'security.custom_curve_agent_draft(catalog_id,id,NULL)'
   ELSE 'security.custom_curve_agent_draft(catalog_id,curve_id,version)' END;
  IF p.qual IS NOT NULL AND(length(p.qual)-length(replace(p.qual,marker,'')))/length(marker)<>1
   OR p.with_check IS NOT NULL AND(length(p.with_check)-length(replace(p.with_check,marker,'')))/length(marker)<>1
  THEN RAISE EXCEPTION 'curve_drafts: unexpected policy %.%',p.tablename,p.policyname USING ERRCODE='55000';END IF;
  EXECUTE format('CREATE POLICY %I ON market_data.%I FOR %s TO openetrm_runtime',replace(p.policyname,'scoped_','agent_draft_'),p.tablename,p.cmd)
   ||coalesce(' USING('||replace(p.qual,marker,agent)||')','')||coalesce(' WITH CHECK('||replace(p.with_check,marker,agent)||')','');
 END LOOP;
END $agent$;
CREATE POLICY agent_draft_insert ON market_data.custom_curve_requests FOR INSERT TO openetrm_runtime
 WITH CHECK(operation='save' AND actor_id=security.custom_curve_actor(catalog_id,'ReadMarketData')
  AND security.custom_curve_agent_draft(catalog_id,curve_id,version) AND security.custom_curve_source_visible(catalog_id,definition_revision_id)
  AND security.custom_curve_visible(catalog_id,curve_id,version));
-- A claimed draft is visible to its agent as a draft is to an author.
DO $visible$
DECLARE definition text;before text;
BEGIN
 definition:=pg_get_functiondef('security.custom_curve_visible(uuid,uuid,integer)'::regprocedure);
 before:='OR security.market_access(catalog,''SealMarketDefinitions'')))';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'curve_drafts: unexpected draft visibility' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before,'OR security.market_access(catalog,''SealMarketDefinitions'')'
  ||' OR security.custom_curve_agent_draft(d.catalog_id,d.curve_id,d.version)))');
END $visible$;

-- Each agent write queues its own check (above).
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['custom_curves','custom_curve_versions','custom_curve_nodes','custom_curve_edges','custom_curve_node_sources',
  'custom_curve_node_calendars','custom_curve_node_schedules','custom_curve_node_published_sources','custom_curve_contract_weights',
  'custom_curve_panel_weights','custom_curve_generation_savers','custom_curve_requests'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER agent_draft_write AFTER INSERT OR UPDATE OR DELETE ON market_data.%I DEFERRABLE INITIALLY DEFERRED '
   'FOR EACH ROW EXECUTE FUNCTION security.check_custom_curve_agent_write()',t);
 END LOOP;
END$$;

ALTER TABLE security.custom_curve_agent_drafts OWNER TO openetrm_security_owner;
ALTER FUNCTION market_data.guard_custom_curve_saver() OWNER TO openetrm_security_owner;
DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['security.custom_curve_agent_authority(security.request_contexts,uuid,uuid)','security.custom_curve_agent_draft(uuid,uuid,integer)',
  'security.check_custom_curve_agent_write()','security.custom_curve_saver(uuid,uuid,integer,text,uuid,integer)',
  'security.custom_curve_agent_writable(uuid,uuid,integer,uuid)','security.claim_custom_curve_draft(uuid,uuid,integer,uuid,bytea,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
REVOKE ALL ON FUNCTION market_data.guard_custom_curve_saver() FROM PUBLIC;
-- The scope helper answers for any turn and catalog, so only the claim, which binds both to the caller, may ask it.
GRANT EXECUTE ON FUNCTION security.custom_curve_agent_draft(uuid,uuid,integer),security.custom_curve_saver(uuid,uuid,integer,text,uuid,integer),
 security.claim_custom_curve_draft(uuid,uuid,integer,uuid,bytea,integer) TO openetrm_runtime;

CREATE OR REPLACE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades']),
  ('read_custom_curves',ARRAY['ReadMarketData']),('prepare_custom_curve',ARRAY['ReadMarketData']),('read_jobs',ARRAY['ReadJobs']),
  ('save_custom_curve_draft',ARRAY['ReadMarketData','WriteMarketDefinitions'])
$$;
-- Saving curve drafts is the one tool that needs WriteMarketDefinitions; the draft authority also checks the tool itself.
DO $$BEGIN
 IF(SELECT array_agg(code) FROM assistant.agent_tools() WHERE 'WriteMarketDefinitions'=ANY(permissions))IS DISTINCT FROM ARRAY['save_custom_curve_draft'] THEN
  RAISE EXCEPTION 'curve_drafts: unexpected tool permissions' USING ERRCODE='55000';
 END IF;
END$$;

-- Retain the existing targets and add a draft: a successful save step of this same turn that recorded the assistant as a
-- generation's saver, never a model-supplied curve. An identical save, which writes no saver, has no draft to open.
DO $upgrade$
DECLARE definition text;before text;after text;
BEGIN
 definition:=pg_get_functiondef('assistant.is_navigation_target(assistant.turns,jsonb)'::regprocedure);
 before:=E'        AND s.operation=''custom_curves.prepare_v1'' AND s.code IS NULL)';
 after:=before||E'\n    WHEN ''draft'' THEN target->>''page''=''custom_curves'' AND jsonb_typeof(target->''selection'')=''object''\n'
  ||E'     AND (SELECT array_agg(k ORDER BY k COLLATE "C") FROM jsonb_object_keys(target->''selection'') k)=ARRAY[''step'',''turnId'']\n'
  ||E'     AND target->''selection''->>''turnId''=t.id::text AND EXISTS(SELECT 1 FROM assistant.tool_steps s\n'
  ||E'       WHERE s.turn_id=t.id AND to_jsonb(s.ordinal)=target->''selection''->''step'' AND s.tool=''save_custom_curve_draft''\n'
  ||E'        AND s.operation=''custom_curves.save_draft_v1'' AND s.code IS NULL\n'
  ||E'        AND EXISTS(SELECT 1 FROM market_data.custom_curve_generation_savers g\n'
  ||E'         WHERE(g.turn_id,g.step)=(t.id,s.ordinal) AND g.saved_by=''assistant''))';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'curve_drafts: unexpected target selection contract' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before,after);
END $upgrade$;
COMMIT;
