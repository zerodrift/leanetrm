"""Direct RLS, temporal and immutable-booking checks in a disposable cluster."""
import json
from preview_fixture import trade_booking
import subprocess
import time
from uuid import UUID, uuid4


def run_checks(query, manifest, psql, environment):
    catalog = str(UUID(manifest['catalogId'])); tenant = str(UUID(manifest['tenantId'])); workspace = str(UUID(manifest['workspaceId']))
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS '+name, flush=True)
    def issue(profile='trader', login='preview_runtime', seconds=60):
        return query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'{login}',{seconds});")
    def runtime(sql, token=None):
        token = token or issue()
        return f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} COMMIT;"
    def reject(sql, code, message=None):
        guard = '' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE; END IF;"
        return f"DO $$ BEGIN {sql} RAISE EXCEPTION 'Expected rejection'; EXCEPTION WHEN SQLSTATE '{code}' THEN {guard} END $$;"
    def bind(context, at):
        return f"SELECT security.use_market_context('{catalog}','{context}'); SELECT security.use_trading_context('{catalog}','{at}');"
    context = query(runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);"))
    count = query(f"SELECT count(*) FROM trading.trades WHERE catalog_id='{catalog}';")
    check('every futures storage table forces RLS', query("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='trading' AND c.relname IN('publications','knowledge_receipts','contexts','portfolios','books','futures_sides','instruments','instrument_sides','trades','trade_instruments','trade_revisions','trade_references','instrument_references','futures_contract_references','futures_revision_markets') AND c.relkind='r' AND c.relrowsecurity AND c.relforcerowsecurity;") == '15')
    check('unbound trading reads cannot fall back to current state', query(runtime('SELECT count(*) FROM trading.trades; SELECT count(*) FROM trading.books;')) == '0\n0')
    check('same-day later executions are absent before their exact instant', query(runtime(bind(context, '2020-12-18T15:59:59Z')+'SELECT count(*) FROM trading.trades; SELECT count(*) FROM trading.trade_instruments;')) == '0\n0')
    check('exact execution instant includes the committed obligations', query(runtime(bind(context, '2020-12-18T16:00:00Z')+'SELECT count(*) FROM trading.trades;')) == count)
    for table in ['publications', 'knowledge_receipts', 'contexts', 'portfolios', 'books', 'futures_sides', 'instruments', 'instrument_sides', 'trades', 'trade_instruments', 'trade_revisions', 'trade_references', 'instrument_references','futures_contract_references','futures_revision_markets']:
        query(runtime(reject(f'INSERT INTO trading.{table} SELECT * FROM trading.{table} WHERE false;', '42501')))
        check('runtime has no direct write path to '+table)
        check('immutable storage guard exists in '+table, query(f"SELECT count(*) FROM pg_trigger WHERE tgrelid='trading.{table}'::regclass AND tgname='immutable_record';")=='1')
        if query(f'SELECT EXISTS(SELECT 1 FROM trading.{table});')=='t':
            query('BEGIN; '+reject(f'UPDATE trading.{table} SET catalog_id=catalog_id;', '55000')+' ROLLBACK;')
    old = query(f"SELECT x.context_id FROM trading.contexts x JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(x.catalog_id,x.context_id) WHERE x.catalog_id='{catalog}' AND x.known_through=1 AND t.effective_on='2020-12-18' ORDER BY t.known_at LIMIT 1;")
    check('prior certified knowledge excludes later backdated executions at the same business instant', bool(old) and query(runtime(bind(old, '2020-12-18T16:00:00Z')+'SELECT count(*) FROM trading.trades;')) == '0')
    missing = query(f"BEGIN; SELECT security.use_context('{issue(login='preview_owner')}'); SELECT security.capture_market_and_schedule_context('{catalog}','2020-12-18',NULL); COMMIT;")
    query(runtime(f"SELECT security.use_market_context('{catalog}','{missing}');"+reject(f"PERFORM security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');", 'P0001', 'knowledge_coverage')))
    check('a context without a trading certificate is explicitly unavailable')
    cutoff = query(f"SELECT known_at::text FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{missing}';")
    query(runtime(reject(f"PERFORM security.capture_date_context('{catalog}','2020-12-18','{cutoff}');", 'P0001', 'knowledge_coverage')))
    check('recapturing cannot silently qualify an older context missing trading history')
    query(runtime(reject(f"PERFORM security.capture_market_and_schedule_context('{catalog}','2020-12-18',NULL);", '42501')))
    check('runtime cannot bypass trading certification through the underlying capture')
    query(runtime(reject(f"PERFORM security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');", 'P0001', 'knowledge_coverage')))
    check('trading requires an explicit common market context')
    query(runtime(f"SELECT security.use_market_context('{catalog}','{context}');"+reject(f"PERFORM security.use_trading_context('{catalog}','2020-12-17T16:00:00Z');", 'P0001', 'knowledge_coverage')))
    check('trading cannot reinterpret the retained business date')
    query(runtime(bind(context, '2020-12-18T16:00:00Z')+reject(f"PERFORM security.use_trading_context('{catalog}','2020-12-18T16:01:00Z');", 'P0001', 'knowledge_coverage')))
    check('a request cannot rebind its exact trading valuation instant')
    check('unknown current operation status reveals no business record', query(runtime(f"SELECT security.futures_booking_status('{catalog}','{uuid4()}') IS NULL;")) == 't')
    contract = query(f"SELECT id FROM market_data.contracts WHERE catalog_id='{catalog}' AND kind='future' ORDER BY delivery_from LIMIT 1;")
    expiry = query(f"SELECT expiry_at::text FROM market_data.contracts WHERE catalog_id='{catalog}' AND id='{contract}';")
    def booking(valuation='2020-12-18T16:00:00Z', executed=None, lots='1', price='3', operation=None):
        operation = operation or uuid4()
        return f"PERFORM security.book_futures_with_parties('{catalog}','{operation}','d1000000-0000-0000-0000-000000000001','{manifest['revisionId']}','future','{contract}','{valuation}',{('NULL' if executed is None else chr(39)+executed+chr(39))},{lots},{price},'contract_lot_execution_pnl_v2',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb);"
    query(runtime(reject(booking().replace('contract_lot_execution_pnl_v2','fixed_total_cash_execution_pnl_v1'), 'P0001', 'futures_input')))
    check('obsolete futures execution convention cannot create a current booking')
    query(f"""BEGIN;
ALTER TABLE ref_data.futures_execution_policies DISABLE TRIGGER frozen_definition;
DELETE FROM ref_data.futures_execution_policies WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}';
SET LOCAL SESSION AUTHORIZATION preview_runtime; SELECT security.use_context('{issue()}');
{reject(booking(), 'P0001', 'futures_execution_policy')}
ROLLBACK;""")
    check('synthetic futures without an explicit execution policy cannot book')
    query(runtime(reject(booking(valuation='2020-12-18T15:59:59Z',executed='2020-12-18T16:00:00Z'), 'P0001', 'trade_booking_read_only')))
    check('raw booking cannot supply its own execution timestamp')
    query(runtime(reject(booking(valuation=expiry), 'P0001', 'futures_execution')))
    check('raw booking rejects the exact last-trading boundary')
    query(runtime(reject(booking(lots='0.5'), 'P0001', 'futures_lots')))
    query(runtime(reject(booking(price='3.001'), 'P0001', 'futures_execution')))
    check('raw booking enforces integer lots and the retained trading tick')
    token = issue()
    query(f"""BEGIN;
ALTER TABLE ref_data.quantity_specifications DISABLE TRIGGER guard_definition;
INSERT INTO ref_data.quantity_specifications
 SELECT catalog_id,revision_id,'ambiguous_synthetic_quantity',commodity_code,basis_code,unit_code,dimension_code
 FROM ref_data.quantity_specifications WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code='test_gas_quantity';
SET SESSION AUTHORIZATION preview_runtime; SELECT security.use_context('{token}');
{reject(booking(), 'P0001', 'futures_convention')}
ROLLBACK;""")
    check('raw booking rejects ambiguous exact quantity identities sharing one physical unit')
    children = []
    def connect(sql, name):
        process = subprocess.Popen(psql, env={**environment, 'PGAPPNAME': name}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(process); process.stdin.write(sql+'\n\\echo READY\n'); process.stdin.flush()
        while process.stdout.readline().strip() != 'READY':
            if process.poll() is not None: raise AssertionError(process.stderr.read())
        return process
    def waiter(sql, name):
        process = subprocess.Popen(psql+['-v','VERBOSITY=verbose'], env={**environment, 'PGAPPNAME': name}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(process); process.stdin.write(sql); process.stdin.close(); process.stdin = None
        for _ in range(100):
            if query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{name}' AND wait_event_type='Lock';") == '1': return process
            time.sleep(.05)
        raise AssertionError('Expected waiter did not block: '+name)
    def finish(process):
        process.stdin.write('COMMIT;\n'); process.stdin.flush()
        _, error = process.communicate(timeout=10)
        assert process.returncode == 0, error
    try:
        operation = str(uuid4())
        publisher = connect(runtime(booking(operation=operation).replace('PERFORM','SELECT',1)).rsplit(' COMMIT;',1)[0], 'futures_uncommitted_booking')
        cutoff = query('SELECT clock_timestamp()::text;')
        capture = waiter(runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18','{cutoff}');"), 'futures_capture_waiter')
        check('historical capture waits for the in-flight booking commit gate', capture.poll() is None)
        finish(publisher)
        output, error = capture.communicate(timeout=10)
        assert capture.returncode == 0, error
        earlier = str(UUID(output.strip()))
        check('a booking committed after the requested cutoff is absent from that certified view', query(runtime(bind(earlier,'2020-12-18T16:00:00Z')+f"SELECT count(*) FROM trading.trades WHERE operation_id='{operation}';")) == '0')
        later = query(runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);"))
        check('a later certified cutoff includes the same committed booking', query(runtime(bind(later,'2020-12-18T16:00:00Z')+f"SELECT count(*) FROM trading.trades WHERE operation_id='{operation}';")) == '1')
        before = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        locker = connect('BEGIN; SELECT pg_advisory_xact_lock(730849102);', 'futures_expiry_lock')
        token = issue(seconds=2); expired = str(uuid4())
        blocked = waiter(runtime(booking(operation=expired).replace('PERFORM','SELECT',1),token), 'futures_expiry_waiter')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>expires_at FROM security.request_contexts WHERE id='{token}';") == 't': break
            time.sleep(.05)
        else: raise AssertionError('Expected booking capability expiry')
        finish(locker)
        _, error = blocked.communicate(timeout=10)
        check('booking rechecks expiring authority after its commit-gate wait', blocked.returncode != 0 and '42501' in error)
        check('an expired blocked booking leaves no trade or publication', query(f"SELECT count(*) FROM trading.trades WHERE operation_id='{expired}';") == '0' and query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == before)
    finally:
        for child in children:
            if child.poll() is None:
                child.kill(); child.communicate(timeout=5)
    print(f'{passed} futures storage checks passed.', flush=True)
