-- Human-approved workspace operations. The model has a read/preview boundary and can retain command proposals;
-- only an authenticated human's separate request executes a proposal through the ordinary application operation.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
INSERT INTO security.permissions VALUES('UseAssistantWorkspace');
INSERT INTO security.role_permissions SELECT id,'UseAssistantWorkspace' FROM security.roles
 WHERE code IN('trader','risk_analyst','market_data_editor','access_administrator','agent_reader');
-- Withdraw the former exception allowing agents to save curve drafts. The retained history and SQL contract stay
-- auditable, but the runtime identity no longer has the capability and the tool refuses with approval_required.
DELETE FROM security.role_permissions WHERE permission='DraftCustomCurves';
CREATE OR REPLACE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades']),
  ('read_custom_curves',ARRAY['ReadMarketData']),('prepare_custom_curve',ARRAY['ReadMarketData']),('read_jobs',ARRAY['ReadJobs']),
  ('save_custom_curve_draft',ARRAY['ReadMarketData','WriteMarketDefinitions']),
  ('read_workspace_page',ARRAY['ReadMarketData','UseAssistantWorkspace']),
  ('discover_page_operations',ARRAY['ReadMarketData','UseAssistantWorkspace']),
  ('read_page_operation',ARRAY['ReadMarketData','UseAssistantWorkspace']),
  ('propose_page_action',ARRAY['ReadMarketData','UseAssistantWorkspace'])
$$;

ALTER TABLE assistant.tool_read_access DROP CONSTRAINT tool_read_access_check,ADD CONSTRAINT tool_read_access_check CHECK(CASE access
 WHEN 'market_access' THEN permission='ReadMarketData' AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
 WHEN 'reference_access' THEN permission='ReadMarketData' AND market_catalog_id IS NULL AND reference_catalog_id IS NOT NULL
 WHEN 'trading_access' THEN permission IN('ReadTrades','ReadTradeParties') AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
 WHEN 'job_access' THEN permission='ReadJobs' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_access' THEN market_catalog_id IS NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_installation_jobs' THEN permission='UseAssistantWorkspace' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_installation_infrastructure' THEN permission='UseAssistantWorkspace' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
 ELSE false END);

-- Each read invokes the user's original operation, whose current authority and resource policies are unchanged.
-- Its permission sources are retained too: later provider calls, answer publication and conversation replay must
-- not disclose administration, risk or operational data after the relevant user authority is removed.
DO $upgrade$
DECLARE definition text;before text;after text;signature text;
BEGIN
 signature:='assistant.agent_access(assistant.turns,text,text,uuid,uuid)';
 definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHEN ''job_access'' THEN permission=''ReadJobs'' AND market_catalog IS NULL AND reference_catalog IS NULL AND assistant.agent_permission(t,permission)';
 after:=before||E'\n  WHEN ''workspace_access'' THEN market_catalog IS NULL AND reference_catalog IS NULL\n'
  ||'   AND assistant.agent_permission(t,''UseAssistantWorkspace'') AND security.principal_has_permission(t.owner_id,t.tenant_id,t.workspace_id,permission,clock_timestamp())'
  ||E'\n  WHEN ''workspace_installation_jobs'' THEN assistant.agent_permission(t,''UseAssistantWorkspace'') AND EXISTS(SELECT 1 FROM automation.installation_operators o WHERE o.principal_id=t.owner_id AND o.capability=''ManageAllJobs'' AND o.expires_at>clock_timestamp())'
  ||E'\n  WHEN ''workspace_installation_infrastructure'' THEN assistant.agent_permission(t,''UseAssistantWorkspace'') AND EXISTS(SELECT 1 FROM automation.installation_operators o WHERE o.principal_id=t.owner_id AND o.capability=''ManageInfrastructure'' AND o.expires_at>clock_timestamp())';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'workspace_operations: unexpected agent access';END IF;
 EXECUTE replace(definition,before,after);
 signature:='assistant.is_read_access(jsonb)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHEN a->>''access''=''job_access'' THEN a->>''permission''=''ReadJobs'' AND jsonb_typeof(a->''catalogId'')=''null''';
 after:=before||E'\n  WHEN a->>''access''=''workspace_access'' THEN jsonb_typeof(a->''catalogId'')=''null''\n'
  ||'   AND EXISTS(SELECT 1 FROM security.permissions p WHERE p.code=a->>''permission'')'
  ||E'\n  WHEN a->>''access'' IN(''workspace_installation_jobs'',''workspace_installation_infrastructure'') THEN a->>''permission''=''UseAssistantWorkspace'' AND jsonb_typeof(a->''catalogId'')=''null''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'workspace_operations: unexpected source format';END IF;
 EXECUTE replace(definition,before,after);
 signature:='assistant.sources_readable(uuid)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHEN ''job_access'' THEN security.has_permission(s.permission)';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'workspace_operations: unexpected source replay';END IF;
 EXECUTE replace(definition,before,before||E'\n  WHEN ''workspace_access'' THEN security.has_permission(s.permission)'
  ||E'\n  WHEN ''workspace_installation_jobs'' THEN automation.operator(''ManageAllJobs'')'
  ||E'\n  WHEN ''workspace_installation_infrastructure'' THEN automation.operator(''ManageInfrastructure'')');

 signature:='assistant.is_navigation_target(assistant.turns,jsonb)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='''parties'',''blotter'',''custom_curves'',''jobs''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'workspace_operations: unexpected navigation pages';END IF;
 EXECUTE replace(definition,before,before||',''blotter_risk'',''blotter_risk_lens'',''blotter_new_trade'',''blotter_new_future'',''blotter_new_swap'',''risk_jobs'',''delivery_prices'',''interval_series'',''interval_curves'',''users_access'',''settings_ai''');
END $upgrade$;

CREATE FUNCTION assistant.workspace_sources() RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);result jsonb;
BEGIN
 IF NOT security.has_permission('UseAssistantWorkspace') THEN RAISE EXCEPTION 'No workspace assistant permission' USING ERRCODE='42501';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('access',a.access,'permission',a.permission,'catalogId',a.catalog)),'[]'::jsonb) INTO result FROM(
  SELECT 'workspace_access'::text access,p permission,NULL::uuid catalog FROM unnest(security.current_permissions()) p
  UNION ALL SELECT 'market_access','ReadMarketData',s.catalog_id FROM security.market_catalog_scopes s WHERE(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id)
  UNION ALL SELECT 'reference_access','ReadMarketData',s.catalog_id FROM security.reference_catalog_scopes s WHERE(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id)
  UNION ALL SELECT CASE o.capability WHEN 'ManageAllJobs' THEN 'workspace_installation_jobs' ELSE 'workspace_installation_infrastructure' END,
   'UseAssistantWorkspace',NULL::uuid FROM automation.installation_operators o WHERE o.principal_id=c.actor_id AND o.expires_at>clock_timestamp()
 )a;
 RETURN result;
END$$;
ALTER FUNCTION assistant.workspace_sources() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.workspace_sources() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.workspace_sources() TO openetrm_runtime;

CREATE FUNCTION assistant.action_proposal(scope text,turn uuid,step integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);t assistant.turns;arguments jsonb;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') OR step IS NULL OR step<1 THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO t FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
  WHERE x.id=action_proposal.turn AND(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,action_proposal.scope);
 IF t.id IS NULL THEN RAISE EXCEPTION 'turn_not_found';END IF;
 IF NOT security.has_permission('UseAssistantWorkspace') OR NOT assistant.turn_authorized(t) OR NOT assistant.sources_readable(t.id)
 THEN RAISE EXCEPTION 'The proposal is no longer authorized' USING ERRCODE='42501';END IF;
 IF t.knowledge_mode<>'current' OR t.state<>'completed' THEN RAISE EXCEPTION 'proposal_not_ready';END IF;
 SELECT s.input INTO arguments FROM assistant.tool_steps s WHERE(s.turn_id,s.ordinal,s.tool,s.operation)=
  (t.id,action_proposal.step,'propose_page_action','workspace.propose_v1') AND s.code IS NULL;
 IF arguments IS NULL THEN RAISE EXCEPTION 'turn_input';END IF;
 RETURN jsonb_build_object('input',arguments,'context',assistant.turn_state(t)->'context','pins',t.pins);
END$$;
ALTER FUNCTION assistant.action_proposal(text,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.action_proposal(text,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.action_proposal(text,uuid,integer) TO openetrm_runtime;

CREATE TABLE assistant.action_authorizations(
 turn_id uuid NOT NULL,step integer NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),authorized_at ref_data.instant NOT NULL,
 PRIMARY KEY(turn_id,step),FOREIGN KEY(turn_id,step) REFERENCES assistant.tool_steps(turn_id,ordinal)
);
ALTER TABLE assistant.action_authorizations OWNER TO openetrm_security_owner;
ALTER TABLE assistant.action_authorizations ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.action_authorizations FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON assistant.action_authorizations TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.action_authorizations FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION assistant.authorize_action(scope text,turn uuid,step integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);proposal jsonb;
BEGIN
 -- Called inside the original command's human-authorized transaction, under its epoch fence. A completed revoke
 -- between Review and this command therefore prevents the effect. The approval receipt commits with that effect.
 proposal:=assistant.action_proposal(scope,turn,step);
 INSERT INTO assistant.action_authorizations VALUES(turn,step,c.actor_id,clock_timestamp()) ON CONFLICT DO NOTHING;
 RETURN proposal;
END$$;
ALTER FUNCTION assistant.authorize_action(text,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.authorize_action(text,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.authorize_action(text,uuid,integer) TO openetrm_runtime;

CREATE TABLE assistant.action_outcomes(
 turn_id uuid NOT NULL,step integer NOT NULL,outcome jsonb NOT NULL CHECK(octet_length(outcome::text)<=65536),
 PRIMARY KEY(turn_id,step),FOREIGN KEY(turn_id,step) REFERENCES assistant.action_authorizations
);
ALTER TABLE assistant.action_outcomes OWNER TO openetrm_security_owner;
ALTER TABLE assistant.action_outcomes ENABLE ROW LEVEL SECURITY;
ALTER TABLE assistant.action_outcomes FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON assistant.action_outcomes TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.action_outcomes FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION assistant.action_outcome(scope text,turn uuid,step integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE value jsonb;
BEGIN
 PERFORM assistant.authorize_action(scope,turn,step);
 -- Serialize the exact proposal, including a retry with a different HTTP request identity.
 PERFORM 1 FROM assistant.action_authorizations a WHERE(a.turn_id,a.step)=(action_outcome.turn,action_outcome.step) FOR UPDATE;
 SELECT o.outcome INTO value FROM assistant.action_outcomes o WHERE(o.turn_id,o.step)=(action_outcome.turn,action_outcome.step);
 RETURN jsonb_build_object('found',FOUND,'outcome',value);
END$$;
CREATE FUNCTION assistant.record_action_outcome(scope text,turn uuid,step integer,value jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM assistant.authorize_action(scope,turn,step);
 INSERT INTO assistant.action_outcomes VALUES(turn,step,value);
 RETURN jsonb_build_object('recorded',true);
END$$;
ALTER FUNCTION assistant.action_outcome(text,uuid,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION assistant.record_action_outcome(text,uuid,integer,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.action_outcome(text,uuid,integer),assistant.record_action_outcome(text,uuid,integer,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.action_outcome(text,uuid,integer),assistant.record_action_outcome(text,uuid,integer,jsonb) TO openetrm_runtime;
COMMIT;
