"""Exercise the catalog admission boundary in the launcher's own synthetic database."""
from concurrent.futures import ThreadPoolExecutor
from uuid import UUID

from assistant_context import expect, refused


def run_checks(query, manifest):
    catalog, tenant, workspace = (str(UUID(manifest[key])) for key in ('catalogId', 'tenantId', 'workspaceId'))
    day = '2021-02-15'
    capture = f"security.capture_date_context('{catalog}','{day}',"
    table = 'security.retained_context_capacity_versions'
    configure = 'security.configure_retained_context_capacity'
    usage = f"SELECT count(*) FROM market_data.temporal_contexts WHERE catalog_id='{catalog}';"

    def viewer(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} COMMIT;")

    def change(at, maximum):
        return int(query(f'SELECT {configure}({at},{maximum});'))

    def fresh_capture():
        return viewer(f'SELECT {capture}NULL);')

    original = fresh_capture()
    known_at = query(f"SELECT known_at FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{original}';")
    used = int(query(usage))
    revision, previous = map(int, query(f'SELECT revision,maximum_per_catalog FROM {table} ORDER BY revision DESC LIMIT 1;').split('|'))
    assert previous > used + 1

    full = change(revision, used)
    viewer(refused(f'PERFORM {capture}NULL);', 'knowledge_limit'))
    assert viewer(f"SELECT {capture}'{known_at}');") == original and int(query(usage)) == used
    viewer(expect(f'PERFORM {configure}({full},{used + 1});', '42501'))
    viewer(expect(f'INSERT INTO {table}(revision,maximum_per_catalog) VALUES (100,100);', '42501'))
    query(expect(f'PERFORM {configure}({full},0);', '22023'))
    query('BEGIN ISOLATION LEVEL REPEATABLE READ; ' + expect(f'PERFORM {configure}({full},{used});', '25000') + ' ROLLBACK;')
    query(expect(f'DELETE FROM {table} WHERE revision={full};', '55000'))
    # A policy history ahead of the server clock can be replayed, but cannot grow backwards.
    query(f"BEGIN; INSERT INTO {table}(revision,maximum_per_catalog,recorded_at) "
          f"VALUES ({full + 1},{used + 1},clock_timestamp()+interval '1 day'); "
          + expect(f'PERFORM {configure}({full + 1},{used + 2});', '22008') + ' ROLLBACK;')
    assert change(full, used) == full
    raised = change(full, used + 1)
    assert raised == full + 1 and change(full, used + 1) == raised
    query(expect(f'PERFORM {configure}({full},{used + 2});', '40001'))
    # Two authorized reads compete for the last remaining slot.
    admitted = []
    with ThreadPoolExecutor(max_workers=2) as readers:
        requests = [readers.submit(fresh_capture) for _ in range(2)]
        for request in requests:
            try:
                admitted.append(request.result())
            except RuntimeError as error:
                assert 'ERROR:  knowledge_limit' in str(error), error
    assert len(admitted) == 1, admitted
    fresh = admitted[0]
    assert fresh != original and int(query(usage)) == used + 1
    assert int(query(f"SELECT capacity_revision FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{fresh}';")) == raised
    viewer(refused(f'PERFORM {capture}NULL);', 'knowledge_limit'))
    lowered = change(raised, used)
    viewer(refused(f'PERFORM {capture}NULL);', 'knowledge_limit'))
    assert int(query(usage)) == used + 1
    assert change(lowered, previous) == lowered + 1 and change(revision, used) == full
    assert viewer(f"SELECT {capture}'{known_at}');") == original
    print('PASS configured capacity refuses new captures at the boundary, preserves exact replay, and resumes under an operator revision', flush=True)
