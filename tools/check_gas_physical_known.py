"""Knowledge-only native physical attachments on disposable PostgreSQL."""
import json
from decimal import Decimal
import os
from pathlib import Path
import subprocess

from check_reference_editing import main
from check_gas_futures_delivery import checks as performance_checks, fixture, upgrade_checks as performance_upgrade_checks


def upgrade_checks(query, migration):
    performance_upgrade_checks(query, migration)
    if migration.name != '0276_gas_applied_source_reconciliation.sql':return
    from assistant_context import check_guard
    signature='security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])'
    changes=[(change,signature) for change in (
        f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[4]) WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
        f"ALTER FUNCTION {signature} SET work_mem='64kB';",f"ALTER FUNCTION {signature} STRICT;",
        f"ALTER FUNCTION {signature} LEAKPROOF;",f"ALTER FUNCTION {signature} PARALLEL SAFE;",
        f"ALTER FUNCTION {signature} STABLE;",f"ALTER FUNCTION {signature} SECURITY INVOKER;",
        f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
        f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime WITH GRANT OPTION;",
        f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
        f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;")]
    check_guard(query,migration.read_text(),'0276','gas_physical_source_upgrade',changes)


def checks(query, manifest):
    performance_checks(query, manifest)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    def call(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    context = call(f"SELECT security.capture_date_context('{catalog}','2026-11-04',NULL);")
    book = query(f"SELECT book_id FROM trading.gas_owned_accounts WHERE catalog_id='{catalog}' LIMIT 1;")
    at = '2026-11-04T08:00:00Z'
    bound = f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
    result = json.loads(call(bound+f"SELECT security.read_gas_physical_known_attachments('{catalog}','{context}','{at}',ARRAY['{book}']::uuid[])::text;"))
    assert result['contextId'] == context and result['stateProfile'] == 'known_gas_physical_attachments_v1'
    attachments = result['attachments']
    assert len(attachments) == 2
    for attachment in attachments:
        position = attachment['position']
        assert attachment['link']['position_id'] == position['id']
        assert attachment['final']['final_price'] == '6.03'
        assert attachment['finals'][-1]['id'] == attachment['final']['determination_id']
        assert all(row['position_id'] == position['id'] for row in attachment['performances'])
        assert any(row['performed_at'].startswith('2026-11-06') for row in attachment['performances'])
        assert all(row['position_id'] == position['id'] for row in attachment['runs'])
        assert all(row['position_id'] == position['id'] for row in attachment['cash'])
        assert len({row['id'] for row in attachment['steps']}) == len(attachment['steps'])
    long = next(a for a in attachments if Decimal(a['position']['quantity']) > 0)
    short = next(a for a in attachments if Decimal(a['position']['quantity']) < 0)
    assert len(long['performances']) == 4 and len(short['splits']) == 2
    assert sum(Decimal(row['amount']) for row in long['cash']) == Decimal('-6.03')
    assert len(long['payments']) == 2
    assert len(long['runs']) == 6 and len(long['finals']) == 4
    from uuid import uuid4
    malformed=json.dumps([dict(tradeId=str(uuid4()),bindingId=str(uuid4()))])
    try:call(bound+f"SELECT security.read_gas_physical_known_attachments('{catalog}','{context}','{at}',ARRAY['{book}']::uuid[],'{malformed}'::jsonb);")
    except RuntimeError as error:assert 'gas_physical_parent_scope' in str(error),str(error)
    else:raise AssertionError('An unknown parent binding bypassed the exact native source scope')
    print('PASS knowledge-only physical attachments: exact native parents, complete accepted finals, future performances, principal prefixes, stock links and payment revisions', flush=True)
    host=query('SHOW unix_socket_directories;')
    mark_contract=query(f"SELECT mark_contract_id FROM trading.gas_owned_accounts WHERE catalog_id='{catalog}' LIMIT 1;")
    pins=json.loads(query(f"SELECT jsonb_agg(o.id) FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE o.catalog_id='{catalog}' AND o.observed_at='{manifest['example']['at']}' AND(s.contract_id='{mark_contract}' OR o.role='parameter');"))
    packet=Path(host)/'physical-known-check.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=context,at=at,pins=pins,bookId=book,markContract=mark_contract)))
    packet.chmod(0o600)
    checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-physical-known',str(packet)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
    print(checked.stdout.strip(),flush=True)


if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture, before_migration=upgrade_checks)
