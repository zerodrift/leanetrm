"""Reviewed splice migration predecessors, inside the owned preview only."""
from assistant_context import check_guard, migration_body


def run_checks(query, migration):
    signatures=('market_data.guard_panel_weight()', 'market_data.guard_panel_publication()', 'market_data.guard_lookup_publication()')
    changes=tuple((change,signature) for signature in signatures for change in (
        f"CREATE OR REPLACE FUNCTION {signature} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $changed$BEGIN RETURN NEW;END$changed$;",
        f"ALTER FUNCTION {signature} OWNER TO openetrm_security_owner;",
        f"ALTER FUNCTION {signature} SET work_mem='64kB';",
        f"REVOKE EXECUTE ON FUNCTION {signature} FROM PUBLIC;"))
    check_guard(query,migration.read_text(),'0200','curve_splice_upgrade',changes)
    try:
        query('BEGIN;ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;'+migration_body(migration.read_text())+'ROLLBACK;')
    except RuntimeError as error:
        assert 'curve_splice_upgrade: unexpected node kinds' in str(error), error
    else:
        raise AssertionError('Splice migration admitted an unreviewed node-kind constraint')
    print('PASS splice migration refuses helper body, owner, configuration, ACL and node-constraint drift; caller search path is independent',flush=True)
