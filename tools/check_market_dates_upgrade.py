"""Reviewed native close-reader predecessors in the owned preview database."""
from assistant_context import check_guard


def run_checks(query, migration):
    if migration.name.startswith('0363_'):
        guard = 'close_batch_upgrade'
        signatures = [('security.read_market_close_parts(uuid,uuid[],uuid)',
                       'catalog uuid,close_ids uuid[],expected_context uuid', 'jsonb')]
    else:
        guard = 'hidden_day_upgrade'
        signatures = [('security.market_close_batch_visible(uuid,uuid[])', 'catalog uuid,close_ids uuid[]', 'boolean'),
                      ('security.close_session_hidden_days(uuid,text)', 'catalog uuid,session text', 'date[]')]
    changes = []
    for signature, parameters, result in signatures:
        name = signature.split('(')[0]
        for change in (
            f"CREATE OR REPLACE FUNCTION {name}({parameters}) RETURNS {result} LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $changed$BEGIN RETURN NULL;END$changed$;",
            f'ALTER FUNCTION {signature} OWNER TO CURRENT_USER;',
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',
            f'ALTER FUNCTION {signature} STRICT;',
            f'ALTER FUNCTION {signature} PARALLEL SAFE;',
        ):
            changes.append((change, signature))
    check_guard(query, migration.read_text(), migration.name[:4], guard, tuple(changes))
