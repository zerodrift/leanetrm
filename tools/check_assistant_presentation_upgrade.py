"""Presentation runtime upgrade predecessors, inside an owned preview only."""
import re
from assistant_context import check_guard


def run_checks(query, migration):
    text = migration.read_text()
    signatures = re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'", text, re.MULTILINE)
    assert len(signatures) == 3
    changes = []
    for signature in signatures:
        changes.extend((change, signature) for change in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"""DO $drift$ DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN
             EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic upgrade drift */ ');END$drift$;"""))
    check_guard(query, text, '0203', 'presentation_assistant_upgrade', changes)
