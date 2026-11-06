"""Whole-term gas upgrade qualification in the caller's owned database."""
import re
from assistant_context import check_guard, migration_body


def run_checks(query,migration):
    text=migration.read_text()
    signatures=re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'",text,re.MULTILINE)
    assert len(signatures)==3
    changes=[]
    for signature in signatures:
        changes.extend((change,signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $drift$ DECLARE body text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(body,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;"))
    check_guard(query,text,'0205','gas_term_upgrade',changes)
    try:query("BEGIN;ALTER TABLE trading.gas_component_terms DROP CONSTRAINT gas_component_terms_check1;"+migration_body(text)+"ROLLBACK;")
    except RuntimeError as error:assert 'gas_term_upgrade: unexpected constraint trading.gas_component_terms' in str(error),error
    else:raise AssertionError('An unreviewed gas geometry constraint was admitted')
    print('PASS multi-month gas upgrade: helper bodies, owners, ACLs, configuration, geometry constraint and caller search path are pinned',flush=True)
