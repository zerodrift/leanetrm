"""Indexed consideration upgrade refuses unreviewed native predecessors."""
import re
from assistant_context import check_guard,migration_body


def run_checks(query,migration):
    text=migration.read_text()
    signatures=[s for s in re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'",text,re.MULTILINE) if '(' in s]
    assert len(signatures)==3
    changes=[]
    for signature in signatures:
        changes.extend((change,signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $drift$ DECLARE body text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(body,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;"))
    check_guard(query,text,'0207','gas_index_upgrade',changes)
    for constraint in ['gas_component_terms_role_check','gas_component_terms_check2']:
        try:query(f'BEGIN;ALTER TABLE trading.gas_component_terms DROP CONSTRAINT {constraint};'+migration_body(text)+'ROLLBACK;')
        except RuntimeError as error:assert f'gas_index_upgrade: unexpected constraint {constraint}' in str(error),error
        else:raise AssertionError('An unreviewed gas component constraint was admitted')
    print('PASS indexed consideration upgrade pins builder, seal, publisher and component constraints',flush=True)
