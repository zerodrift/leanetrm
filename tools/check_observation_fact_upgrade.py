"""The selected-fact definer consumer refuses an unreviewed visibility dependency."""
import re
from assistant_context import check_guard,migration_body


def run_checks(query,migration):
    text=migration.read_text()
    signatures=re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'",text,re.MULTILINE)
    assert len(signatures)==4
    changes=[]
    for signature in signatures:
        for change in (f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                       f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                       f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
                       f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic dependency drift */ ');END$$;"):
            changes.append((change,signature))
    check_guard(query,text,'0211','observation_facts_upgrade',changes)
    for change in ("CREATE POLICY synthetic_facts_allow ON market_data.observations FOR SELECT TO openetrm_runtime USING(true);",
                   "CREATE POLICY synthetic_facts_deny ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);",
                   "ALTER POLICY scoped_read ON market_data.observations USING(false);",
                   "ALTER TABLE market_data.observations NO FORCE ROW LEVEL SECURITY;"):
        try:query('BEGIN;'+change+migration_body(text)+'ROLLBACK;')
        except RuntimeError as error:assert 'observation_facts_upgrade: unexpected SELECT policies' in str(error),error
        else:raise AssertionError('Selected-fact upgrade accepted changed observation policies')
    print('PASS selected-fact upgrade refuses dependency definition, owner, ACL, config and policy drift',flush=True)
