"""Publication settlement upgrades retain the reviewed trigger security boundaries."""
from assistant_context import check_guard


def run_checks(query, migration):
    changes = []
    for signature in ('market_data.guard_contract_price_term()', 'market_data.guard_contract_price_publication()'):
        for change in (f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                       f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                       f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
                       f"ALTER FUNCTION {signature} SECURITY INVOKER;",
                       f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;"):
            changes.append((change, signature))
    check_guard(query, migration.read_text(), '0226', 'publication_price_upgrade', changes)
