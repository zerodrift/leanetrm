-- Each account keeps one market dashboard layout; saves replay by request id.
BEGIN;
CREATE TABLE presentation.dashboard_layouts (
 tenant_id uuid NOT NULL, workspace_id uuid NOT NULL, principal_id uuid NOT NULL REFERENCES security.principals(id),
 version uuid NOT NULL, layout jsonb NOT NULL CHECK(jsonb_typeof(layout)='object' AND octet_length(layout::text)<=24000),
 PRIMARY KEY(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TABLE presentation.dashboard_layout_requests (
 tenant_id uuid NOT NULL, workspace_id uuid NOT NULL, principal_id uuid NOT NULL REFERENCES security.principals(id),
 request_id uuid NOT NULL, body_hash bytea NOT NULL CHECK(octet_length(body_hash)=32), response jsonb NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,principal_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE FUNCTION security.dashboard_identity() RETURNS TABLE(tenant_id uuid,workspace_id uuid,principal_id uuid)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT c.tenant_id,c.workspace_id,c.actor_id FROM security.request_contexts c
 JOIN security.principals p ON p.id=c.actor_id AND p.kind='human'
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
 AND c.runtime_login=session_user AND security.has_permission('ReadMarketData');
$$;
ALTER FUNCTION security.dashboard_identity() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.dashboard_identity() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.dashboard_identity() TO openetrm_runtime;
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['dashboard_layouts','dashboard_layout_requests'] LOOP
  EXECUTE format('ALTER TABLE presentation.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE presentation.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY own_layout ON presentation.%1$I TO openetrm_runtime
   USING (EXISTS(SELECT 1 FROM security.dashboard_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(%1$I.tenant_id,%1$I.workspace_id,%1$I.principal_id)))
   WITH CHECK (EXISTS(SELECT 1 FROM security.dashboard_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(%1$I.tenant_id,%1$I.workspace_id,%1$I.principal_id)))',t);
 END LOOP;
END $$;
GRANT SELECT,INSERT,UPDATE ON presentation.dashboard_layouts TO openetrm_runtime;
GRANT SELECT,INSERT ON presentation.dashboard_layout_requests TO openetrm_runtime;
COMMIT;
