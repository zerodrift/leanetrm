"""Owned pre-upgrade refusal checks for independent dataset namespaces."""
import re
from assistant_context import migration_body


def run_checks(query,migration):
    body=migration_body(migration)
    signatures=re.findall(r"\('((?:assistant|presentation)\.[^']+)','[0-9a-f]{64}'\)",migration)
    assert len(signatures)==11
    for signature in signatures:
        for change in (f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                       f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                       f"ALTER FUNCTION {signature} OWNER TO preview_owner;"):
            try:query('BEGIN;'+change+body+'ROLLBACK;')
            except RuntimeError as error:assert 'synthetic_namespace_upgrade_mismatch: '+signature in str(error),error
            else:raise AssertionError('Namespace upgrade accepted changed function authority or implementation')
    for relation,name in (('assistant.conversations','conversations_scope_check'),
                          ('presentation.user_preference_versions','user_preference_versions_namespace_check')):
        try:query(f'BEGIN;ALTER TABLE {relation} DROP CONSTRAINT {name},ADD CONSTRAINT {name} CHECK(true);'+body+'ROLLBACK;')
        except RuntimeError as error:assert 'synthetic_namespace_constraint_mismatch: '+relation in str(error),error
        else:raise AssertionError('Namespace upgrade accepted changed scope constraint')
    query('BEGIN;SET LOCAL search_path=assistant,presentation,public;'+body+'ROLLBACK;')
    print('PASS synthetic namespace upgrade refuses definition, owner, grant and constraint drift and accepts an independent caller search path',flush=True)
