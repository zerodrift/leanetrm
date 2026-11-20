"""Original market admissions survive genuine scope and publication waits."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from point_trader_mark_fixture import literal


def authority(f, rejected):
    q, manifest = f.query, f.manifest
    role = q("SELECT id FROM security.roles WHERE code='market_data_editor';")
    actor = q("SELECT principal_id FROM security.identity_bindings WHERE subject='editor';")

    def install(permission, missing):
        temporary, first, successor = [str(uuid4()) for _ in range(3)]
        initial = '' if missing else f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '2 seconds',false);"
        q(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','{permission}');"
            f"INSERT INTO security.roles VALUES('{temporary}','synthetic_point_timer_{temporary[:8]}','Synthetic original market admission');"
            f"INSERT INTO security.role_permissions VALUES('{temporary}','{permission}');" + initial +
            f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
        return temporary, first, successor

    def restore(permission, ids):
        temporary, first, successor = ids
        q(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';"
            f"DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions VALUES('{role}','{permission}');COMMIT;")

    # Capture the context before scope locks and role scheduling. The value is
    # fresh at another explicit microsecond, so any unexpected success cannot
    # hide behind an old accepted fact's acknowledgement path.
    for mode, permission, missing in [('record', 'ImportObservations', False), ('record', 'ImportObservations', True),
            ('record', 'ReadMarketData', False), ('status', 'ReadMarketData', False), ('setup', 'ImportObservations', True)]:
        context = f.market_context()
        at = '2020-12-19T16:00:00.000007Z'
        body = f.command('17', target=f.point_target(at=at))
        before = f.publication_heads()
        with ThreadPoolExecutor(max_workers=1) as pool:
            lock = 'LOCK TABLE security.market_catalog_scopes IN ACCESS EXCLUSIVE MODE;' if mode == 'record' else 'SELECT pg_advisory_xact_lock(730849102);'
            blocker = pool.submit(q, "SET application_name='synthetic_point_scope_wait';BEGIN;" + lock + "SELECT pg_sleep(7);COMMIT;")
            limit = time.monotonic() + 5
            while q("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic_point_scope_wait' AND wait_event='PgSleep';") != '1':
                assert time.monotonic() < limit, 'The owned lock did not become active.'
                time.sleep(.02)
            ids = install(permission, missing)
            epoch = q('SELECT generation FROM security.epoch;')
            try:
                if mode == 'record':
                    # This negative probe starts at the producer's admission.
                    # Client input registration would itself wait on the scope
                    # lock, before the native original deadline is captured.
                    f.query=q.raw
                    try:
                        rejected(lambda: f.call(f"SELECT security.record_point_trader_mark('{f.catalog}','{uuid4()}',{literal(body)},'{context}','{at}');", profile='editor'), 'point_trader_mark_denied')
                    finally:f.query=q
                elif mode == 'status':
                    rejected(lambda: f.call(f"SELECT security.point_trader_mark_status('{f.catalog}','{uuid4()}');", profile='editor'), 'point_trader_mark_denied')
                else:
                    filters = dict(curveCode='transport_rights', contractId=f.contract, search='', offset=0, limit=10, quoteFilter=None, eligibility=None)
                    result = json.loads(f.call(f"SELECT security.point_trader_mark_setup('{f.catalog}','{context}','{at}','{f.definition_revision}','{f.reference}','{f.valuation_reference}',{literal(filters)});", profile='editor'))
                    assert result['canPublish'] is False, 'Setup promoted import authority that was absent on entry.'
                blocker.result(timeout=15)
                assert q('SELECT generation FROM security.epoch;') == epoch
                assert f.call(f"SELECT security.has_permission('{permission}');", profile='editor') == 't'
                assert f.publication_heads() == before
            finally:
                blocker.result(timeout=15)
                restore(permission, ids)
    print('PASS scope/publication waits retain original read/import absence and deadlines despite active successor grants and unchanged epoch; readonly setup never promotes import permission.', flush=True)
