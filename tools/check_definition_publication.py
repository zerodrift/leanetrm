"""Raw publication rechecks exact clock/calendar scope; replay creates no new fact."""
import json
from uuid import uuid4


def run_checks(query, manifest):
    catalog, ref, revision, tenant, workspace = (manifest[k] for k in ('catalogId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId'))
    def issue():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    identity = str(uuid4())
    definition = json.dumps({'name': 'Synthetic scope dates', 'symbol': 'oe.synthetic_scope', 'firstMonth': '2021-01', 'lastMonth': '2021-04', 'anchor': 'day', 'day': 1, 'shortMonth': 'reject', 'adjustment': 'unadjusted', 'calendar': None})
    definitions = [
        (f"INSERT INTO market_data.tou_products VALUES('{catalog}','{identity}','synthetic_scope');"
         f"INSERT INTO market_data.tou_product_versions(catalog_id,product_id,version,generation,state,name,clock_catalog_id,clock_revision_id,clock_code,peak_start_hour,peak_end_hour,peak_saturday) SELECT catalog_id,'{identity}',1,1,'draft','Synthetic scope peak',clock_catalog_id,clock_revision_id,clock_code,7,20,false FROM market_data.tou_product_versions WHERE catalog_id='{catalog}' AND state='published' ORDER BY publication_order LIMIT 1;",
         f"security.publish_tou_product('{catalog}','{identity}',1,1)"),
        (f"INSERT INTO market_data.date_schedules VALUES('{catalog}','{identity}','oe.synthetic_scope');"
         f"INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code) VALUES('{catalog}','{identity}',1,1,'draft','{definition}'::jsonb,'context_month','2021-01-01','2021-05-01','date_schedule_v2','{ref}','{revision}','test_business');",
         f"security.publish_date_schedule('{catalog}','{identity}',1,1)")]
    revoke = f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{ref}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';"
    for setup, publish in definitions:
        token = issue()
        query(f"BEGIN;{setup}{revoke}SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"
              f"DO $$ BEGIN PERFORM {publish};RAISE EXCEPTION 'Expected revoked reference publication rejection';EXCEPTION WHEN insufficient_privilege THEN NULL;END $$;ROLLBACK;")
        token = issue()
        query(f"BEGIN;{setup}SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');SELECT {publish};"
              f"RESET SESSION AUTHORIZATION;{revoke}SET SESSION AUTHORIZATION preview_runtime;SELECT {publish};ROLLBACK;")
    print('PASS raw definition publication: revoked exact clock/calendar scope blocks new publication, authorized publication and retained control replay remain available', flush=True)
