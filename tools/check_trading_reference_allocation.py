"""Display references survive an archive/rebook without reusing retired numbers."""
import json
import subprocess
import time
from pathlib import Path
from uuid import UUID, uuid4


def run_checks(query, manifest, psql, environment):
    run_upgrade_checks(query, psql, environment)
    catalog = str(UUID(manifest['catalogId']))
    publication = query(f"SELECT min(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    assert publication and publication != '0'

    def instrument(entity):
        return ("INSERT INTO trading.instruments VALUES"
                f"('{catalog}','{entity}',{publication},'2020-12-18T16:00:00Z');")

    def number(kind, entity):
        return query(f"SELECT reference_number FROM trading.{kind}_references "
                     f"WHERE catalog_id='{catalog}' AND {kind}_id='{entity}';")

    # Only the disposable database owner simulates retiring its highest display
    # reference. The complete transaction rolls back, including its test anchor.
    for kind in ('instrument', 'trade'):
        high = int(query(f"SELECT max(reference_number) FROM trading.{kind}_references WHERE catalog_id='{catalog}';"))
        entity = str(uuid4())
        insert = instrument(entity) if kind == 'instrument' else (
            "INSERT INTO trading.trades SELECT (jsonb_populate_record(NULL::trading.trades,"
            f"to_jsonb(t)||jsonb_build_object('id','{entity}','operation_id','{uuid4()}'))).* "
            f"FROM trading.trades t WHERE catalog_id='{catalog}' LIMIT 1;")
        actual = query("BEGIN;"
                       f"ALTER TABLE trading.{kind}_references DISABLE TRIGGER immutable_record;"
                       f"DELETE FROM trading.{kind}_references WHERE catalog_id='{catalog}' AND reference_number={high};"
                       f"ALTER TABLE trading.{kind}_references ENABLE TRIGGER immutable_record;"
                       + insert + f"SELECT reference_number FROM trading.{kind}_references WHERE catalog_id='{catalog}' AND {kind}_id='{entity}';ROLLBACK;")
        assert actual == str(high + 1), f'Retired {kind} reference {high} was reused: {actual}'
        print('PASS retired '+kind+' display references are not reused by new anchors', flush=True)

    for kind in ('trade', 'instrument'):
        high = int(query(f"SELECT high_water FROM trading.reference_counters WHERE catalog_id='{catalog}' AND kind='{kind}';"))
        entity = str(uuid4())
        insert = instrument(entity) if kind == 'instrument' else (
            "INSERT INTO trading.trades SELECT (jsonb_populate_record(NULL::trading.trades,"
            f"to_jsonb(t)||jsonb_build_object('id','{entity}','operation_id','{uuid4()}'))).* "
            f"FROM trading.trades t WHERE catalog_id='{catalog}' LIMIT 1;")
        # A reviewed release owner restores the archived maximum before new
        # bookings. An exact retry of that reservation must not allocate a number.
        reserve = (f"INSERT INTO trading.reference_counters AS r VALUES('{catalog}','{kind}',{high + 7}) "
                   "ON CONFLICT(catalog_id,kind) DO UPDATE SET high_water=greatest(r.high_water,excluded.high_water);")
        actual = query('BEGIN;' + reserve + reserve + insert
                       + f"SELECT reference_number FROM trading.{kind}_references WHERE catalog_id='{catalog}' AND {kind}_id='{entity}';ROLLBACK;")
        assert actual == str(high + 8), actual
    print('PASS exact owner reservations retain archived maxima independently for trades and instruments', flush=True)

    before = query(f"SELECT jsonb_agg(to_jsonb(c) ORDER BY kind)::text FROM trading.reference_counters c WHERE catalog_id='{catalog}';")
    entity = str(uuid4())
    query('BEGIN;' + instrument(entity) + 'ROLLBACK;')
    query("DO $$BEGIN BEGIN " + instrument(entity) + "RAISE EXCEPTION 'Synthetic failed command';"
          "EXCEPTION WHEN SQLSTATE 'P0001' THEN NULL;END;END$$;")
    assert number('instrument', entity) == ''
    assert query(f"SELECT jsonb_agg(to_jsonb(c) ORDER BY kind)::text FROM trading.reference_counters c WHERE catalog_id='{catalog}';") == before
    print('PASS failed commands and rolled-back allocations consume no committed display number', flush=True)
    high = int(query(f"SELECT high_water FROM trading.reference_counters WHERE catalog_id='{catalog}' AND kind='instrument';"))
    entity = str(uuid4())
    actual = query('BEGIN;' + instrument(entity) + instrument(entity).replace(';', ' ON CONFLICT DO NOTHING;', 1)
                   + f"SELECT reference_number FROM trading.instrument_references WHERE(catalog_id,instrument_id)=('{catalog}','{entity}');"
                   + f"SELECT high_water FROM trading.reference_counters WHERE catalog_id='{catalog}' AND kind='instrument';ROLLBACK;")
    assert actual == f'{high + 1}\n{high + 1}', actual
    print('PASS an idempotent anchor insert allocates once and retains its original reference', flush=True)

    def refused(statement, state):
        return (f"DO $$BEGIN {statement};RAISE EXCEPTION 'Expected refusal';"
                f"EXCEPTION WHEN SQLSTATE '{state}' THEN NULL;END$$;")
    query("SET SESSION AUTHORIZATION preview_runtime;" + refused('PERFORM 1 FROM trading.reference_counters', '42501')
          + refused(f"INSERT INTO trading.reference_counters VALUES('{catalog}','trade',0)", '42501')
          + refused('UPDATE trading.reference_counters SET high_water=high_water', '42501')
          + refused('DELETE FROM trading.reference_counters', '42501'))
    query('BEGIN;' + refused(f"UPDATE trading.reference_counters SET high_water=high_water-1 WHERE catalog_id='{catalog}'", '55000')
          + refused(f"DELETE FROM trading.reference_counters WHERE catalog_id='{catalog}'", '55000')
          + refused(f"INSERT INTO trading.reference_counters VALUES('{uuid4()}','trade',1)", '23503') + 'ROLLBACK;')
    print('PASS runtime cannot inspect/write counters; catalog FKs and monotonic guards reject invalid owner writes', flush=True)

    children = []
    def start(body, label, ready=False):
        child = subprocess.Popen([str(v) for v in psql], env={**environment, 'PGAPPNAME': label},
                                 text=True, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        children.append(child); child.stdin.write(body + ('\n\\echo READY\n' if ready else '\n')); child.stdin.flush()
        if ready:
            while child.stdout.readline().strip() != 'READY':
                if child.poll() is not None:raise AssertionError(child.stderr.read())
        return child
    try:
        first, second = str(uuid4()), str(uuid4())
        high = int(query(f"SELECT high_water FROM trading.reference_counters WHERE catalog_id='{catalog}' AND kind='instrument';"))
        one = start('BEGIN;' + instrument(first), 'reference_first', ready=True)
        two = start('BEGIN;' + instrument(second) + 'COMMIT;', 'reference_second')
        two.stdin.close(); two.stdin = None
        for _ in range(100):
            if query("SELECT count(*) FROM pg_stat_activity WHERE application_name='reference_second' AND wait_event_type='Lock';") == '1':break
            if two.poll() is not None:raise AssertionError(two.stderr.read())
            time.sleep(.025)
        else:raise AssertionError('Concurrent reference allocation did not wait for its uncommitted predecessor')
        one.stdin.write('COMMIT;\n'); one.stdin.flush(); _, error = one.communicate(timeout=10)
        assert one.returncode == 0, error
        _, error = two.communicate(timeout=10); assert two.returncode == 0, error
        assert (number('instrument', first), number('instrument', second)) == (str(high + 1), str(high + 2))
        print('PASS concurrent committed allocations retain distinct consecutive references', flush=True)
    finally:
        for child in children:
            if child.poll() is None:child.terminate()
            child.communicate(timeout=10)


def run_upgrade_checks(query, psql, environment):
    """Apply the full additive migration to populated, separately owned storage."""
    from preview_fixture import fixture, trading_fixture
    root = Path(__file__).resolve().parents[1]
    name = 'openetrm_reference_upgrade_' + uuid4().hex
    target = [str(v) for v in psql]; target[target.index('-d') + 1] = name
    def upgraded(sql):
        result = subprocess.run(target, input=sql, text=True, capture_output=True,
                                env=environment, cwd=root, timeout=30)
        assert result.returncode == 0, result.stderr
        return result.stdout.strip()
    def existing_roles(sql):
        return '\n'.join(line for line in sql.splitlines() if not line.startswith('CREATE ROLE '))
    query(f'CREATE DATABASE {name} TEMPLATE template0;')
    try:
        assert Path(upgraded('SHOW data_directory;')).resolve() == Path(query('SHOW data_directory;')).resolve()
        for migration in sorted((root / 'db/migrations').glob('*.sql')):
            if int(migration.name[:4]) < 127:upgraded(existing_roles(migration.read_text()))
        reference, market, security, manifest = fixture(root)
        for data in (reference, market, security, trading_fixture(manifest['catalogId'])):
            upgraded(existing_roles(data))
        catalog = str(UUID(manifest['catalogId']))
        def anchors(instruments, trades):
            return (f"INSERT INTO trading.instruments SELECT '{catalog}',gen_random_uuid(),1,'2020-12-18T16:00:00Z' FROM generate_series(1,{instruments});"
                    f"INSERT INTO trading.trades SELECT '{catalog}',gen_random_uuid(),'d1000000-0000-0000-0000-000000000001',gen_random_uuid(),m.principal_id,'{{}}'::jsonb,1,'2020-12-18T16:00:00Z' "
                    f"FROM generate_series(1,{trades}) CROSS JOIN LATERAL(SELECT x.principal_id FROM security.workspace_memberships x JOIN security.principals p ON p.id=x.principal_id WHERE p.kind='human' LIMIT 1)m;")
        upgraded(anchors(2, 3))
        reference_sql = ("SELECT jsonb_build_object('trade',(SELECT jsonb_agg(to_jsonb(t) ORDER BY reference_number) FROM trading.trade_references t),"
                         "'instrument',(SELECT jsonb_agg(to_jsonb(t) ORDER BY reference_number) FROM trading.instrument_references t))::text;")
        before = upgraded(reference_sql)
        upgraded((root / 'db/migrations/0127_native_rebooking_boundary.sql').read_text())
        assert upgraded(reference_sql) == before
        assert json.loads(upgraded("SELECT jsonb_object_agg(kind,high_water)::text FROM trading.reference_counters;")) == {'trade': 3, 'instrument': 2}
        upgraded(anchors(1, 1))
        assert upgraded("SELECT max(reference_number) FROM trading.trade_references;SELECT max(reference_number) FROM trading.instrument_references;") == '4\n3'
        print('PASS populated upgrade preserves existing references and continues each retained maximum', flush=True)
    finally:
        query(f'DROP DATABASE {name};')


def run_http_checks(url, query, client=None):
    from preview_client import PreviewClient
    from preview_fixture import trade_booking
    c = client or PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'}
    catalog = c.call('futures/catalog', context); contract = catalog['contracts'][0]
    body = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'],
            'curve': contract['curve'], 'contractId': contract['id'], 'lots': '2', 'executionPrice': '3'}
    first = c.call('futures/book', body)
    reference = query(f"SELECT reference_number FROM trading.trade_references WHERE trade_id='{UUID(first['tradeId'])}';")
    counters = query('SELECT jsonb_agg(to_jsonb(c) ORDER BY catalog_id,kind)::text FROM trading.reference_counters c;')
    assert c.call('futures/book', body) == first
    assert reference == query(f"SELECT reference_number FROM trading.trade_references WHERE trade_id='{UUID(first['tradeId'])}';")
    assert counters == query('SELECT jsonb_agg(to_jsonb(c) ORDER BY catalog_id,kind)::text FROM trading.reference_counters c;')
    print('PASS native booking retries return the same reference without another allocation', flush=True)
