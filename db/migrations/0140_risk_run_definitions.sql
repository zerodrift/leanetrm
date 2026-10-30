-- A risk run definition belongs to one account and has immutable versions: its books, the preset that
-- chooses the shocked targets, and one shock size per price unit. Saves and runs replay by request id. A run of a definition
-- records the version it ran, its retained context and the queued job of each of its parts.
BEGIN;
CREATE TABLE risk.run_definitions (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id), id uuid NOT NULL,
 tenant_id uuid NOT NULL, workspace_id uuid NOT NULL, owner_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,id),
 FOREIGN KEY(tenant_id,workspace_id,owner_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id)
);
CREATE INDEX risk_run_definitions_owner ON risk.run_definitions(tenant_id,workspace_id,owner_id);
CREATE TABLE risk.run_definition_versions (
 catalog_id uuid NOT NULL, definition_id uuid NOT NULL, version integer NOT NULL CHECK(version>=1),
 name text NOT NULL CHECK(btrim(name)<>'' AND length(name)<=80),
 preset text NOT NULL CHECK(preset IN('held_contracts','held_curves')),
 -- When the version was saved, by the database clock: shown to the person, never a knowledge time (D267).
 saved_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,definition_id,version), FOREIGN KEY(catalog_id,definition_id) REFERENCES risk.run_definitions(catalog_id,id)
);
CREATE TABLE risk.run_definition_books (
 catalog_id uuid NOT NULL, definition_id uuid NOT NULL, version integer NOT NULL, book_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,definition_id,version,book_id),
 FOREIGN KEY(catalog_id,definition_id,version) REFERENCES risk.run_definition_versions(catalog_id,definition_id,version),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id)
);
-- A price unit is a currency, a quantity specification of the same reference revision, and the quote multiplier.
CREATE TABLE risk.run_definition_sizes (
 catalog_id uuid NOT NULL, definition_id uuid NOT NULL, version integer NOT NULL,
 reference_catalog_id uuid NOT NULL, revision_id uuid NOT NULL, currency_code ref_data.code NOT NULL, quantity_code ref_data.code NOT NULL,
 multiplier ref_data.exact_number NOT NULL CHECK(multiplier>0), size ref_data.exact_number NOT NULL CHECK(size>0),
 PRIMARY KEY(catalog_id,definition_id,version,reference_catalog_id,revision_id,currency_code,quantity_code,multiplier),
 FOREIGN KEY(catalog_id,definition_id,version) REFERENCES risk.run_definition_versions(catalog_id,definition_id,version),
 FOREIGN KEY(reference_catalog_id,revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code)
);
CREATE TABLE risk.definition_runs (
 run_id uuid PRIMARY KEY, catalog_id uuid NOT NULL, definition_id uuid NOT NULL, version integer NOT NULL, context_id uuid NOT NULL,
 FOREIGN KEY(catalog_id,definition_id,version) REFERENCES risk.run_definition_versions(catalog_id,definition_id,version),
 FOREIGN KEY(catalog_id,context_id) REFERENCES trading.contexts(catalog_id,context_id)
);
CREATE INDEX risk_definition_runs_definition ON risk.definition_runs(catalog_id,definition_id,version);
CREATE TABLE risk.definition_run_jobs (
 run_id uuid NOT NULL REFERENCES risk.definition_runs(run_id), part integer NOT NULL CHECK(part BETWEEN 1 AND 256),
 job_id uuid NOT NULL UNIQUE REFERENCES risk.jobs(id),
 PRIMARY KEY(run_id,part)
);
CREATE TABLE risk.run_definition_requests (
 tenant_id uuid NOT NULL, workspace_id uuid NOT NULL, principal_id uuid NOT NULL REFERENCES security.principals(id),
 request_id uuid NOT NULL, body_hash bytea NOT NULL CHECK(octet_length(body_hash)=32), response jsonb NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,principal_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE FUNCTION risk.definition_identity() RETURNS TABLE(tenant_id uuid,workspace_id uuid,principal_id uuid)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT c.tenant_id,c.workspace_id,c.actor_id FROM security.request_contexts c
 JOIN security.principals p ON p.id=c.actor_id AND p.kind='human'
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
 AND c.runtime_login=session_user AND security.has_permission('RunScenario');
$$;
ALTER FUNCTION risk.definition_identity() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.definition_identity() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.definition_identity() TO openetrm_runtime;
-- Definitions and requests belong to their account; a version's rows and runs are visible with their definition, a run's jobs with its run.
CREATE POLICY own_definition ON risk.run_definitions TO openetrm_runtime
 USING (EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(run_definitions.tenant_id,run_definitions.workspace_id,run_definitions.owner_id)))
 WITH CHECK (EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(run_definitions.tenant_id,run_definitions.workspace_id,run_definitions.owner_id)));
CREATE POLICY own_request ON risk.run_definition_requests TO openetrm_runtime
 USING (EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(run_definition_requests.tenant_id,run_definition_requests.workspace_id,run_definition_requests.principal_id)))
 WITH CHECK (EXISTS(SELECT 1 FROM risk.definition_identity() i WHERE(i.tenant_id,i.workspace_id,i.principal_id)=(run_definition_requests.tenant_id,run_definition_requests.workspace_id,run_definition_requests.principal_id)));
CREATE POLICY own_run_jobs ON risk.definition_run_jobs TO openetrm_runtime
 USING (EXISTS(SELECT 1 FROM risk.definition_runs r WHERE r.run_id=definition_run_jobs.run_id))
 WITH CHECK (EXISTS(SELECT 1 FROM risk.definition_runs r WHERE r.run_id=definition_run_jobs.run_id));
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['run_definitions','run_definition_versions','run_definition_books','run_definition_sizes','run_definition_requests',
  'definition_runs','definition_run_jobs'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON risk.%I TO openetrm_runtime',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['run_definition_versions','run_definition_books','run_definition_sizes','definition_runs'] LOOP
  EXECUTE format('CREATE POLICY own_version ON risk.%1$I TO openetrm_runtime
   USING (EXISTS(SELECT 1 FROM risk.run_definitions d WHERE(d.catalog_id,d.id)=(%1$I.catalog_id,%1$I.definition_id)))
   WITH CHECK (EXISTS(SELECT 1 FROM risk.run_definitions d WHERE(d.catalog_id,d.id)=(%1$I.catalog_id,%1$I.definition_id)))',t);
 END LOOP;
END $$;
COMMIT;
