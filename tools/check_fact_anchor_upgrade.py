"""The bounded fact-anchor upgrade refuses changed definer boundaries."""
from assistant_context import check_guard


def run_checks(query, migration, *, number='0223', guard='fact_anchor_upgrade'):
    changes = []
    for signature in ('security.read_observation_facts(uuid,uuid,uuid[])',
                      'security.observation_visibility_scope(uuid,uuid,uuid[])'):
        for change in (f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                       f"ALTER FUNCTION {signature} STRICT;",
                       f"ALTER FUNCTION {signature} LEAKPROOF;",
                       f"ALTER FUNCTION {signature} PARALLEL SAFE;",
                       f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                       f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
                       f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;"):
            changes.append((change, signature))
    check_guard(query, migration.read_text(), number, guard, changes)
