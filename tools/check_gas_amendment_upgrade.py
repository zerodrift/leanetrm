"""Physical gas amendment migration guards, in an owned disposable database."""
import re
from assistant_context import check_guard, migration_body


def run_checks(query, migration):
    text = migration.read_text()
    signatures = re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'", text, re.MULTILINE)
    assert len(signatures) == 6
    changes = []
    for signature in signatures:
        changes.extend((change, signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $drift$ DECLARE body text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(body,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;"))
    check_guard(query, text, '0206', 'gas_amendment_upgrade', changes)
    for table, constraint in [('gas_trade_revisions', 'gas_trade_revisions_revision_check'),
                              ('gas_delivery_obligations', 'gas_delivery_obligations_catalog_id_trade_id_slot_key')]:
        try:
            query(f'BEGIN;ALTER TABLE trading.{table} DROP CONSTRAINT {constraint};' + migration_body(text) + 'ROLLBACK;')
        except RuntimeError as error:
            assert f'gas_amendment_upgrade: unexpected constraint {constraint}' in str(error), error
        else:
            raise AssertionError('An unreviewed gas revision/occurrence constraint was admitted')
    try:
        query("BEGIN;CREATE FUNCTION security.write_gas_composition(uuid,jsonb,timestamptz) RETURNS jsonb LANGUAGE sql AS 'SELECT NULL::jsonb';" + migration_body(text) + 'ROLLBACK;')
    except RuntimeError as error:
        assert 'gas_amendment_upgrade: unexpected private writer' in str(error), error
    else:
        raise AssertionError('A preexisting private writer was admitted')
    print('PASS gas amendment upgrade: six helper bodies/owners/ACLs/configurations, two constraints and private writer identity are pinned', flush=True)
