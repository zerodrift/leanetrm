"""The rolling rate curve cutover refuses retained dated rate curves (D359) in an owned preview only."""
from assistant_context import migration_body


def run_checks(query, fixtures, migration):
    """Load the dated fixtures into the schema before the cutover, run its body in one transaction, and roll everything back."""
    def body(sql):
        return sql.strip().replace('BEGIN;', '', 1).removesuffix('COMMIT;')
    loaded = 'BEGIN;' + '\n'.join(body(sql) for sql in fixtures)
    try:
        query(loaded + migration_body(migration.read_text()) + 'ROLLBACK;')
    except RuntimeError as error:
        assert 'literal_rate_curves' in str(error), error
    else:
        raise AssertionError('The rate curve cutover accepted retained dated rate curves')
    print('PASS rolling rate curve cutover refuses retained dated rate curves until a schema window removes them', flush=True)
