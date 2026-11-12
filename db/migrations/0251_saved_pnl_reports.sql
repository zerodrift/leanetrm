-- Current consumer: durable exact P&L results and source receipts after an authenticated Run → Save.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE risk.saved_pnl_reports(
 catalog_id uuid NOT NULL,id uuid NOT NULL,request_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 owner_id uuid NOT NULL REFERENCES security.principals(id),name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 80),
 opening_context_id uuid NOT NULL,closing_context_id uuid NOT NULL,result_hash text NOT NULL CHECK(result_hash~'^[0-9a-f]{64}$'),
 artifact_hash text NOT NULL CHECK(artifact_hash~'^[0-9a-f]{64}$'),
 artifact text NOT NULL CHECK(length(artifact) BETWEEN 1 AND 2000000),
 saved_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(tenant_id,workspace_id,owner_id,request_id),
 FOREIGN KEY(catalog_id,opening_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,closing_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
ALTER TABLE risk.saved_pnl_reports ENABLE ROW LEVEL SECURITY;
ALTER TABLE risk.saved_pnl_reports FORCE ROW LEVEL SECURITY;
CREATE POLICY owned_report ON risk.saved_pnl_reports TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(saved_pnl_reports.tenant_id,saved_pnl_reports.workspace_id,saved_pnl_reports.owner_id)))
 WITH CHECK(EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(saved_pnl_reports.tenant_id,saved_pnl_reports.workspace_id,saved_pnl_reports.owner_id)));
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON risk.saved_pnl_reports FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
GRANT SELECT,INSERT ON risk.saved_pnl_reports TO openetrm_runtime;
CREATE TABLE risk.pnl_save_requests(
 catalog_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,owner_id uuid NOT NULL REFERENCES security.principals(id),
 request_id uuid NOT NULL,report_id uuid NOT NULL,name text NOT NULL,result_hash text NOT NULL,
 artifact_hash text NOT NULL CHECK(artifact_hash~'^[0-9a-f]{64}$'),
 receipt text NOT NULL CHECK(length(receipt) BETWEEN 1 AND 16384),
 PRIMARY KEY(tenant_id,workspace_id,owner_id,request_id),
 FOREIGN KEY(catalog_id,report_id) REFERENCES risk.saved_pnl_reports(catalog_id,id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
ALTER TABLE risk.pnl_save_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE risk.pnl_save_requests FORCE ROW LEVEL SECURITY;
CREATE POLICY owned_request ON risk.pnl_save_requests TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(pnl_save_requests.tenant_id,pnl_save_requests.workspace_id,pnl_save_requests.owner_id)))
 WITH CHECK(EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(pnl_save_requests.tenant_id,pnl_save_requests.workspace_id,pnl_save_requests.owner_id)));
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON risk.pnl_save_requests FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
GRANT SELECT,INSERT ON risk.pnl_save_requests TO openetrm_runtime;
COMMIT;
