"""Retained cash margin outcomes stay readable without a delivery receipt."""
import hashlib
import json
from pathlib import Path
import re

from check_reference_editing import main
from check_futures_margin_journal import checks as cash_checks


def checks(query,manifest):
    # Create old cash receipts through the exact, previously qualified 0272
    # public writer. No receipt/history row is deleted or edited. This temporary
    # code window exists only in this disposable upgrade-compatibility fixture.
    root=Path(__file__).resolve().parents[1]
    old_source=(root/'db/migrations/0273_futures_delivery_obligations.sql').read_text()
    old=re.search(r'CREATE OR REPLACE FUNCTION settlement_data.determine_futures_margin\(catalog uuid,body jsonb,steps jsonb\).*?END\$\$;',old_source,re.S).group(0)
    body=old.split('AS $$',1)[1].rsplit('$$;',1)[0]
    assert hashlib.md5(body.encode()).hexdigest()=='308b7182904cf08f1d5a19a69567052e'
    current=query("SELECT pg_get_functiondef('settlement_data.determine_futures_margin(uuid,jsonb,jsonb)'::regprocedure);")
    try:
        query(old)
        cash_checks(query,manifest)
    finally:
        query(current)
    catalog=manifest['catalogId']
    command=json.loads(query("SELECT jsonb_build_object('request',c.request,'outcome',c.outcome) FROM settlement_data.futures_margin_run_commands c "
        "JOIN security.identity_bindings i ON i.principal_id=c.actor_id "
        f"WHERE c.catalog_id='{catalog}' AND i.subject='trader' AND NOT EXISTS(SELECT 1 FROM settlement_data.futures_delivery_command_receipts r "
        "WHERE(r.catalog_id,r.operation_id)=(c.catalog_id,c.operation_id)) ORDER BY c.operation_id LIMIT 1;"))
    request=command['request'];operation=request['operationId']
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    literal="'"+json.dumps(request).replace("'","''")+"'::jsonb"
    result=json.loads(query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
        f"SELECT settlement_data.futures_delivery_run_status('{catalog}','{operation}',{literal})::text;COMMIT;"))
    assert result['outcome']==command['outcome'] and result['continuation'] is None
    assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_command_receipts WHERE(catalog_id,operation_id)=('{catalog}','{operation}');")=='0'
    print('PASS retained cash margin receipt: exact original outcome, current authorization, null delivery continuation and no history rewrite',flush=True)


if __name__=='__main__':main(checks=checks)
