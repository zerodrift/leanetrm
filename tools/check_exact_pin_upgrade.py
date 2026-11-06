"""Exact observation reader predecessor and policy guards in an owned database."""
import re
from assistant_context import check_guard, migration_body


def run_checks(query, migration):
    text=migration.read_text()
    signatures=re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'",text,re.MULTILINE)
    assert len(signatures)==5
    changes=[]
    for signature in signatures:
        for change in (f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                       f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                       f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
                       f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic predecessor drift */ ');END$$;"):
            changes.append((change,signature))
    check_guard(query,text,'0204','exact_observation_upgrade',changes)
    for change in (
        "CREATE POLICY synthetic_upgrade_allow ON market_data.observations FOR SELECT TO openetrm_runtime USING(true);",
        "CREATE POLICY synthetic_upgrade_deny ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);",
        "ALTER POLICY scoped_read ON market_data.observations USING(false);",
        "DROP POLICY scoped_read ON market_data.observations;",
        "ALTER POLICY temporal_read ON market_data.observations TO openetrm_risk_worker;",
        "ALTER TABLE market_data.observations DISABLE ROW LEVEL SECURITY;",
        "ALTER TABLE market_data.observations NO FORCE ROW LEVEL SECURITY;",
    ):
        try:query('BEGIN;'+change+migration_body(text)+'ROLLBACK;')
        except RuntimeError as error:
            assert 'exact_observation_upgrade: unexpected SELECT policies' in str(error),error
        else:raise AssertionError('Exact observation upgrade accepted a changed policy contract')
    print('PASS exact observation upgrade refuses changed complete SELECT policy and RLS state',flush=True)
