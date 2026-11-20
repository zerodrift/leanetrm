"""Scoped absence does not impose Transport pricing law on unrelated positions."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from check_fact_anchor_upgrade import run_checks as guard_checks
from check_reference_editing import main
from gas_transport_capacity_fixture import TransportCapacityFixture, fixture
from gas_transport_financial_fixture import FinancialTransportFixture


SIGNATURES = (
    'security.require_gas_transport_financial_presence(uuid,uuid,timestamp with time zone,uuid[],uuid[],timestamp with time zone)',
    'security.require_gas_transport_financial_scope(uuid,uuid,timestamp with time zone,uuid[],uuid[])',
    'security.gas_transport_financial_projection_fence(uuid,uuid,timestamp with time zone,timestamp with time zone,uuid[],uuid[])',
)


def before_migration(query, migration):
    if migration.name == '0357_scoped_transport_financial_presence.sql':
        guard_checks(query, migration, number='0357', guard='transport_financial_presence_upgrade', signatures=SIGNATURES)


def rejected(work, code):
    try:
        work()
    except RuntimeError as error:
        assert code in str(error), str(error)
    else:
        raise AssertionError('Expected ' + code)


def array(values):
    return 'NULL' if values is None else 'ARRAY[' + ','.join("'" + value + "'" for value in values) + ']::uuid[]'


def bind(f, context, at):
    return (f"SELECT security.use_market_context('{f.catalog}','{context}');"
            f"SELECT security.use_trading_context('{f.catalog}','{at}','{at[:10]}');")


def scope(f, context, at, books, trades=None, *, profile='trader'):
    return f.call(bind(f, context, at) + f"SELECT security.require_gas_transport_financial_scope('{f.catalog}',"
                  f"'{context}','{at}',{array(books)},{array(trades)});", profile=profile)


def absence_commit(f, context, at, books, trades=None, *, marker=None):
    captured = '' if marker is None else f"PERFORM set_config('application_name','{marker}',true);"
    f.call(bind(f, context, at) + f"DO $test$ DECLARE original_until timestamptz;BEGIN "
        f"original_until=security.gas_transport_financial_authority('{f.catalog}',NULL);"
        + captured +
        f"IF security.require_gas_transport_financial_scope('{f.catalog}','{context}','{at}',{array(books)},{array(trades)}) "
        "THEN RAISE EXCEPTION 'synthetic_unexpected_transport_presence';END IF;"
        f"PERFORM security.require_gas_transport_financial_presence('{f.catalog}','{context}','{at}',{array(books)},{array(trades)},original_until);"
        "END$test$;")


def checks(query, manifest):
    f = TransportCapacityFixture(query, manifest)
    seed = f.capture()
    from datetime import datetime,timedelta
    from native_read_transport import captured_input
    at = (datetime.fromisoformat(captured_input(query,seed)['known_at'])+timedelta(days=1)).isoformat()
    context = f.capture(day=at[:10])
    assert datetime.fromisoformat(captured_input(query,context)['known_at']) < datetime.fromisoformat(at)
    assert query(f"SELECT count(*) FROM trading.gas_transport_financial_trades WHERE catalog_id='{f.catalog}';") == '0'
    absence_commit(f, context, at, [f.book])
    print('PASS absent Transport scope admits an exact future source and original commit bound without imposing a Transport pricing policy.', flush=True)
    f = FinancialTransportFixture.from_capacity(f)
    hidden = f.capture(day=at[:10])
    booking = f.booking()
    saved = f.book_financial(booking)
    known = f.capture(day=at[:10])
    assert scope(f, f.capture(), f.valuation_at, [f.book], profile='viewer') == 't'
    rejected(lambda: scope(f, known, at, [f.book]), 'gas_transport_financial_context')
    print('PASS a source-visible real Transport agreement retains strict current-clock admission under ordinary financial-only read authority.', flush=True)
    other_book = 'd1000000-0000-0000-0000-000000000002'
    absence_commit(f, known, at, [other_book])
    absence_commit(f, known, at, [f.book], [str(uuid4())])
    absence_commit(f, hidden, at, [f.book])
    print('PASS exact excluded books/trades and the pre-booking knowledge cut remain absent despite a later global Transport head.', flush=True)
    for books, trades in [(None, None), ([], None), ([f.book, f.book], None), ([f.book], [saved['tradeId'], saved['tradeId']])]:
        rejected(lambda: scope(f, known, at, books, trades), 'gas_transport_financial_scope')
    wrong = f.capture(day='2020-12-18')
    rejected(lambda: f.call(f"SELECT security.require_gas_transport_financial_scope('{f.catalog}','{wrong}','{at}',{array([other_book])},NULL);"), 'gas_transport_financial_context')
    rejected(lambda: f.call(f"SELECT security.require_gas_transport_financial_scope('{f.catalog}','{uuid4()}','{at}',{array([other_book])},NULL);"), 'gas_transport_financial_context')
    rejected(lambda: f.call(bind(f, known, at) + f"SELECT security.require_gas_transport_financial_presence('{f.catalog}','{known}','{at}',{array([other_book])},NULL,NULL);"), 'futures_margin_denied')
    print('PASS absent scopes still require complete selectors, a genuine exact source/day and the original finite authority bound.', flush=True)
    policy = f.expiry_policy()
    f.record_financial(dict(kind='expire', tradeId=saved['tradeId'], expectedFinancialStateId=booking['operationId'],
        policyId=policy, policyRevision=1, evidence='Synthetic retained unpaid cash after explicit expiry'),
        effective='2020-12-21T00:00:00Z', at='2020-12-21T00:00:00Z', context=f.capture(day='2020-12-21'))
    closed = f.read_financial(saved['tradeId'], at='2020-12-21T00:00:00Z', context=f.capture(day='2020-12-21'))
    assert closed['financialState']['status'] == 'closed' and closed['cash']['amount'] == '-12.5'
    rejected(lambda: scope(f, f.capture(day=at[:10]), at, [f.book]), 'gas_transport_financial_context')
    print('PASS explicit closed rights with independent unpaid cash remain present and cannot bypass the financial clock law.', flush=True)
    future_birth(f)
    original_authority(f, known, at, other_book)


def future_birth(f):
    hidden = f.capture()
    original = f.book, f.entitlement, f.capacity_version, f.at, f.valuation_at
    try:
        f.at, f.valuation_at = '2020-12-20T23:59:59.999999Z', '2020-12-20T00:00:00Z'
        f.book, definition, version = f.new_capacity('synthetic_future_presence_birth', effective=f.valuation_at)
        f.entitlement, f.capacity_version = definition['entitlementId'], version['versionId']
        saved = f.book_financial(f.booking(key='synthetic_future_presence_birth', fee='0'))
        known = f.capture(day='2020-12-19')
        opening = '2020-12-19T16:00:00Z'
        assert scope(f, known, opening, [f.book], [saved['tradeId']]) == 'f'
        def fence(context, horizon):
            return f.call(f"SELECT security.gas_transport_financial_projection_fence('{f.catalog}','{context}','{opening}','{horizon}',"
                          f"{array([f.book])},{array([saved['tradeId']])});")
        assert fence(hidden, f.valuation_at) == 'f'
        assert fence(known, '2020-12-19T23:59:59Z') == 'f'
        assert fence(known, f.valuation_at) == 't'
    finally:
        f.book, f.entitlement, f.capacity_version, f.at, f.valuation_at = original
    print('PASS an opening absence never removes a source-known future agreement inside the projection horizon.', flush=True)


def original_authority(f, context, at, book):
    q = f.query
    role = q("SELECT id FROM security.roles WHERE code='trader';")
    actor = q("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    temporary, first, successor = (str(uuid4()) for _ in range(3))
    name = 'synthetic_financial_presence_wait_' + temporary[:8]
    read_name = name + '_read'
    with ThreadPoolExecutor(max_workers=2) as pool:
        holder = pool.submit(q, f"SET application_name='{name}';BEGIN;LOCK TABLE trading.gas_transport_financial_trades IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep(6);COMMIT;")
        running, installed = None, False
        try:
            timeout = time.monotonic() + 3
            while q(f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{name}' AND wait_event='PgSleep');") != 't':
                assert time.monotonic() < timeout
                time.sleep(.01)
            q(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadTrades');"
              f"INSERT INTO security.roles VALUES('{temporary}','synthetic_presence_{temporary[:8]}','Synthetic original presence authority');"
              f"INSERT INTO security.role_permissions VALUES('{temporary}','ReadTrades');"
              f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '3 seconds',false);"
              f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
            installed = True
            epoch = q('SELECT generation FROM security.epoch;')
            running = pool.submit(absence_commit, f, context, at, [book], marker=read_name)
            timeout = time.monotonic() + 2
            while q("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid "
                    f"WHERE l.relation='trading.gas_transport_financial_trades'::regclass AND NOT l.granted AND a.application_name='{read_name}' AND a.wait_event_type='Lock');") != 't':
                assert time.monotonic() < timeout and not running.done(), 'The absence proof must reach its own source wait after capturing original authority. ' + (str(running.exception()) if running.done() else '')
                time.sleep(.01)
            assert q(f"SELECT clock_timestamp()<granted_to FROM security.role_grants WHERE id='{first}';") == 't'
            rejected(lambda: running.result(timeout=10), 'gas_transport_financial_denied')
            holder.result(timeout=10)
            assert q('SELECT generation FROM security.epoch;') == epoch
            assert q(f"SELECT granted_from<=clock_timestamp() AND clock_timestamp()<granted_to FROM security.role_grants WHERE id='{successor}';") == 't'
            absence_commit(f, context, at, [book])
        finally:
            holder.result(timeout=10)
            if installed:
                q(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';"
                  f"DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions VALUES('{role}','ReadTrades');COMMIT;")
    print('PASS a real absence-source wait cannot renew its original read bound from an active successor grant; epoch is unchanged and grants are restored.', flush=True)


if __name__ == '__main__':
    main(checks, before_migration=before_migration, fixture_factory=fixture)
