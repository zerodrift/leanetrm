-- A workspace's page-assistant grants (docs/ai-assistant.md §7.1): those who administer its access read every grant in
-- force and the latest grant audit records. Nothing here revokes; Revoke stays the delegator's.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority helper the read relies on.
DO $$
DECLARE helper record;
BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'workspace_grants: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- The latest grant audit records of a workspace, newest first, as the read below takes them.
CREATE INDEX grant_audit_recent ON assistant.grant_audit(tenant_id,workspace_id,recorded_at DESC,operation_id,delegation_id);

-- A workspace's grants (§7.1), for an undelegated holder of ManageIdentities with ManageAccess, rechecked after the read
-- as the account directory is: every grant in force, unrevoked and unexpired, by person and then agent, and the latest
-- 100 grant audit records, newest first, so a grant older than those records is still listed. Each names its person,
-- agent and version, the model it was given for and the permissions it lists, and nothing a turn holds.
CREATE FUNCTION assistant.workspace_grants() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts;result jsonb;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL OR c.delegation_id IS NOT NULL OR NOT security.has_permission('ManageIdentities') OR NOT security.has_permission('ManageAccess') THEN
  RAISE EXCEPTION 'No current workspace grants permission' USING ERRCODE='42501';
 END IF;
 SELECT jsonb_build_object(
  'current',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',g.operation_id,'delegator',m.display_name,'agentName',a.name,
     'version',g.agent_version,'vendor',p.vendor,'identifier',o.identifier,'displayName',o.display_name,'permissions',to_jsonb(g.permissions),
     'grantedAt',g.recorded_at,'expiresAt',g.expires_at) ORDER BY lower(m.display_name),d.delegator_id,lower(a.name),a.id)
   FROM security.agent_delegations d JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted'
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(d.tenant_id,d.workspace_id,d.delegator_id)
    JOIN assistant.agents a ON a.id=g.agent_id JOIN assistant.models o ON o.id=g.model_id JOIN assistant.providers p ON p.id=o.provider_id
   WHERE(d.tenant_id,d.workspace_id)=(c.tenant_id,c.workspace_id) AND NOT d.revoked AND d.granted_from<=clock_timestamp()
    AND clock_timestamp()<d.granted_to),'[]'::jsonb),
  'history',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',g.operation_id,'at',g.recorded_at,'outcome',g.outcome,
     'delegator',m.display_name,'actor',b.display_name,'agentName',a.name,'version',g.agent_version,'vendor',p.vendor,'identifier',o.identifier,
     'displayName',o.display_name,'permissions',to_jsonb(g.permissions),'expiresAt',g.expires_at)
    ORDER BY g.recorded_at DESC,g.operation_id,g.delegation_id)
   FROM(SELECT x.* FROM assistant.grant_audit x WHERE(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id)
     ORDER BY x.recorded_at DESC,x.operation_id,x.delegation_id LIMIT 100)g
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(g.tenant_id,g.workspace_id,g.delegator_id)
    JOIN security.workspace_memberships b ON(b.tenant_id,b.workspace_id,b.principal_id)=(g.tenant_id,g.workspace_id,g.actor_id)
    JOIN assistant.agents a ON a.id=g.agent_id JOIN assistant.models o ON o.id=g.model_id JOIN assistant.providers p ON p.id=o.provider_id),'[]'::jsonb))
  INTO result;
 IF NOT security.has_permission('ManageIdentities') OR NOT security.has_permission('ManageAccess') THEN
  RAISE EXCEPTION 'Workspace grants access expired' USING ERRCODE='42501';
 END IF;
 RETURN result;
END $$;
ALTER FUNCTION assistant.workspace_grants() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.workspace_grants() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.workspace_grants() TO openetrm_runtime;
COMMIT;
