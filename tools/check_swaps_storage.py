"""Swap admission, immutable schedules and shared historical isolation on owned data."""
from preview_fixture import trade_booking
import json
import subprocess
import time
from uuid import uuid4
from swap_fixture import REVISION


def run_checks(query, manifest, psql, environment):
    catalog = manifest['catalogId']; tenant = manifest['tenantId']; workspace = manifest['workspaceId']
    tables = ['currency_discount_defaults', 'swap_fixed_sides', 'swap_floating_sides', 'swap_reset_projections', 'swap_instruments', 'swap_trade_revisions', 'swap_payment_rules']
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS swap storage: '+name, flush=True)
    def issue(profile='trader', seconds=60):
        return query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{seconds});")
    def runtime(sql, token=None):
        return f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token or issue()}'); {sql} COMMIT;"
    def reject(sql, code='P0001', message=None):
        guard = '' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE; END IF;"
        return f"DO $$ BEGIN {sql} RAISE EXCEPTION 'Expected rejection'; EXCEPTION WHEN SQLSTATE '{code}' THEN {guard} END $$;"
    def booking(operation=None, **changes):
        terms = dict(operation=str(operation or uuid4()), definition=REVISION, curve='daily_gas', index=query(f"SELECT index_a_code FROM ref_data.settlement_targets WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code='test_index_target';"),
                     price='3', quantity='100', discounting='false', averaging='calendar_day_arithmetic')
        terms.update(changes)
        def rule(days):
            return json.dumps({'frequency':'monthly','rule':'days_after_period_end','days':days,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'})
        return f"""SELECT security.save_indexed_swap_with_parties('{catalog}','{terms['operation']}',NULL,NULL,'d1000000-0000-0000-0000-000000000001',
         '{terms['definition']}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','{terms['curve']}','{terms['index']}',
         'usd','usd','2021-01-01','2021-02-01','2021-02-18T00:00:00Z','2021-01-01','2021-02-01',
         '2021-03-18T00:00:00Z','{terms['averaging']}',{terms['price']},{terms['quantity']},-1,{terms['discounting']},
         '2020-12-18T16:00:00Z',NULL,'2020-12-18T16:00:00Z','Initial booking','independent_side_financial_swap_v2','{terms.get('fixed_rule',rule(18))}'::jsonb,'{rule(46)}'::jsonb,'test_gas',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb,'{{}}'::uuid[]);"""
    check('manual-boundary writer cannot admit new runtime bookings', query(runtime("SELECT has_function_privilege(current_user,'security.save_swap_with_parties(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,text,text,text,date,date,timestamptz,date,date,timestamptz,timestamptz,text,numeric,numeric,numeric,boolean,timestamptz,timestamptz,timestamptz,text,text,jsonb,jsonb,text,uuid,integer,text,jsonb)','EXECUTE');")) == 'f')
    check('fixing schedules force RLS and deny unbound reads', query("SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='market_data.index_fixing_observations'::regclass;") == 't' and query("SET SESSION AUTHORIZATION preview_runtime; SELECT count(*) FROM market_data.index_fixing_observations;") == '0')
    # Run this first so the expired-duplicate regression is observed before its fix.
    token = issue()
    query(f"""BEGIN;
     ALTER TABLE market_data.contracts DISABLE TRIGGER guard_definition;
     ALTER TABLE market_data.curve_members DISABLE TRIGGER guard_definition;
     INSERT INTO market_data.contracts SELECT catalog_id,revision_id,'{uuid4()}',reference_catalog_id,reference_revision_id,kind,delivery_from,delivery_to,'2020-12-17T00:00:00Z',NULL
      FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{REVISION}' ORDER BY delivery_from LIMIT 1;
     INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'daily_gas',id,999 FROM market_data.contracts
      WHERE catalog_id='{catalog}' AND revision_id='{REVISION}' AND expiry_at='2020-12-17T00:00:00Z';
     SET SESSION AUTHORIZATION preview_runtime; SELECT security.use_context('{token}');
     {reject(booking().replace('SELECT ', 'PERFORM ', 1), message='swap_projection')}
     ROLLBACK;""")
    check('expired duplicate cannot create an unpriceable extra reset')
    check('all seven swap tables force row security', query("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='trading' AND c.relname IN("+','.join("'"+t+"'" for t in tables)+") AND c.relrowsecurity AND c.relforcerowsecurity;") == '7')
    # Ensure each table contains real retained rows before exercising visibility and immutability.
    query(runtime(booking()))
    for table in tables:
        check(table+' has a retained consumer row', int(query(f'SELECT count(*) FROM trading.{table};')) > 0)
        check(table+' denies unbound and market-only reads', query(runtime(f'SELECT count(*) FROM trading.{table};')) == '0' and query(runtime(f'SELECT count(*) FROM trading.{table};', issue('viewer'))) == '0')
        query(runtime(reject(f'INSERT INTO trading.{table} SELECT * FROM trading.{table} WHERE false;', '42501')))
        query('BEGIN; '+reject(f'UPDATE trading.{table} SET catalog_id=catalog_id;', '55000')+' ROLLBACK;')
        check(table+' denies direct writes and retained mutation')
    query(runtime(reject(booking().replace('SELECT ', 'PERFORM ', 1).replace('2021-02-18T00:00:00Z','2021-02-19T00:00:00Z'),message='payment_date_mismatch')))
    query(runtime(reject(booking().replace('SELECT ', 'PERFORM ', 1).replace('"frequency": "monthly"','"frequency": "weekly"'),message='payment_rule')))
    query(runtime(reject(f"PERFORM security.swap_payment_date('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','2021-01-01','2021-02-01','{{}}');",'42501')))
    check('raw admission validates generated dates and keeps calendar helpers private')
    token=issue()
    calendar=query(f"SELECT code FROM ref_data.calendars WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' ORDER BY code LIMIT 1;")
    excessive=json.dumps({'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'business_days','shortMonth':None,'adjustment':'unadjusted','calendar':calendar,'implementation':'monthly_payment_date_rules_v1'})
    query(f"BEGIN;ALTER TABLE ref_data.calendars DISABLE TRIGGER guard_definition;UPDATE ref_data.calendars SET coverage_from='1901-01-01',coverage_to='2199-01-01' WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code='{calendar}';SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"+reject(booking(fixed_rule=excessive).replace('SELECT ','PERFORM ',1),message='payment_calendar')+'ROLLBACK;')
    check('raw calendar work is bounded before scanning under the publication fence')

    before = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    for changes, code in [({'quantity': '0'}, 'swap_input'), ({'discounting': 'NULL'}, 'swap_input'), ({'averaging': 'unsupported'}, 'swap_input'), ({'curve': 'missing'}, 'swap_definition')]:
        query(runtime(reject(booking(**changes).replace('SELECT ', 'PERFORM ', 1), message=code)))
    check('invalid raw terms roll back without a publication', query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == before)
    query(runtime(booking(price='1001.003')))
    check('raw contractual strike is independent of observed quote bounds')
    context = query(runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);"))
    bind = f"SELECT security.use_market_context('{catalog}','{context}'); SELECT security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');"
    query(runtime(bind+reject(booking().replace('SELECT ', 'PERFORM ', 1), message='historical_read_only')))
    check('bound historical context cannot write a backdated swap')
    default = f"SELECT security.swap_discount_default('{catalog}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','usd','{REVISION}','2020-12-18T16:00:00Z')->>'id';"
    def mappings(source, ids, effective):
        """Publish copies of one retained mapping under explicit identities and instant."""
        identities = ','.join(f"'{i}'" for i in ids)
        return f"""SELECT pg_advisory_xact_lock(730849102);
         INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';
         INSERT INTO trading.currency_discount_defaults SELECT d.catalog_id,m.id,d.definition_revision_id,d.reference_catalog_id,d.reference_revision_id,d.currency_code,d.curve_code,d.interpolation_policy,
          (SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}'),'{effective}'
          FROM trading.currency_discount_defaults d CROSS JOIN unnest(ARRAY[{identities}]::uuid[]) m(id) WHERE d.catalog_id='{catalog}' AND d.id='{source}';"""
    def read_after(owner_sql, sql):
        """Read as the runtime after owner writes in one rolled-back transaction."""
        return f"BEGIN; {owner_sql} SET SESSION AUTHORIZATION preview_runtime; SELECT security.use_context('{issue()}'); {sql} ROLLBACK;"
    previous = query(runtime(bind+default))
    query('BEGIN;'+mappings(previous, [uuid4()], '2020-12-18T15:00:00Z')+'COMMIT;')
    latest = query(runtime(default))
    check('retained currency default stays frozen across a later backdated mapping', query(runtime(bind+default)) == previous and latest != previous)
    tie = mappings(latest, [uuid4(), uuid4()], '2020-12-18T15:30:00Z')
    query('BEGIN;'+reject(tie.replace('SELECT ', 'PERFORM ', 1), '23505', 'duplicate key value violates unique constraint "currency_discount_default_key"')+'ROLLBACK;')
    check('one publication cannot assert two defaults for one currency key and instant')
    replacement = str(uuid4())
    check('a later publication still replaces a default at the same instant',
          query(read_after(mappings(latest, [replacement], '2020-12-18T15:00:00Z'), default)) == replacement)
    # Without the key, the lookup itself still refuses to choose between tied mappings.
    unkeyed = 'ALTER TABLE trading.currency_discount_defaults DROP CONSTRAINT currency_discount_default_key;'
    query(read_after(unkeyed+tie, reject(default.replace('SELECT ', 'PERFORM ', 1), message='swap_discount_ambiguous')))
    check('lookup rejects tied latest defaults instead of choosing one')
    correction = str(uuid4())
    check('a later publication resolves a tied default',
          query(read_after(unkeyed+tie+mappings(latest, [correction], '2020-12-18T15:30:00Z'), default)) == correction)
    children = []
    try:
        locker = subprocess.Popen(psql, env={**environment, 'PGAPPNAME': 'swap_expiry_lock'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True); children.append(locker)
        locker.stdin.write('BEGIN; SELECT pg_advisory_xact_lock(730849102);\n\\echo READY\n'); locker.stdin.flush()
        while locker.stdout.readline().strip() != 'READY':
            if locker.poll() is not None: raise AssertionError(locker.stderr.read())
        token = issue(seconds=2); operation = str(uuid4())
        before = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        blocked = subprocess.Popen(psql+['-v','VERBOSITY=verbose'], env={**environment, 'PGAPPNAME': 'swap_expiry_waiter'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True); children.append(blocked)
        blocked.stdin.write(runtime(booking(operation), token)); blocked.stdin.close(); blocked.stdin = None
        for _ in range(100):
            if query("SELECT count(*) FROM pg_stat_activity WHERE application_name='swap_expiry_waiter' AND wait_event_type='Lock';") == '1': break
            time.sleep(.05)
        else: raise AssertionError('Swap write did not wait on publication fence')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>expires_at FROM security.request_contexts WHERE id='{token}';") == 't': break
            time.sleep(.05)
        else: raise AssertionError('Expected capability expiry')
        locker.stdin.write('COMMIT;\n'); locker.stdin.flush(); _, error = locker.communicate(timeout=10); assert locker.returncode == 0, error
        _, error = blocked.communicate(timeout=10)
        check('write rechecks expired authority after waiting on publication fence', blocked.returncode != 0 and '42501' in error)
        check('expired blocked write leaves no trade or publication', query(f"SELECT count(*) FROM trading.trades WHERE operation_id='{operation}';") == '0' and query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == before)
    finally:
        for child in children:
            if child.poll() is None: child.kill(); child.communicate(timeout=5)

    # A market-read grant can expire independently of the still-live booking
    # grant. Block the last revision INSERT, after fixing inputs were checked.
    actor, long_role, market_role, market_grant = [str(uuid4()) for _ in range(4)]
    children = []
    try:
        query(f"""INSERT INTO security.principals VALUES('{actor}','human',true);
         INSERT INTO security.identity_bindings VALUES('https://preview.invalid','swap_market_expiry','{actor}','human_interactive');
         INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{actor}','swap_market_expiry','Synthetic market expiry',true,1);
         INSERT INTO security.roles(id,code,name) VALUES('{long_role}','swap_market_expiry_trading','Synthetic long-lived trading'),('{market_role}','swap_market_expiry_market','Synthetic expiring market read');
         INSERT INTO security.role_permissions VALUES('{long_role}','ReadTrades'),('{long_role}','BookTrade'),('{market_role}','ReadMarketData');
         INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{long_role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false),
          ('{market_grant}','{actor}','{market_role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '3 seconds',false);""")
        token = issue('swap_market_expiry')
        check('independent market grant starts with live booking and market authority', query(runtime(f"SELECT security.trading_access('{catalog}','BookTrade') AND security.market_access('{catalog}','ReadMarketData');", issue('swap_market_expiry'))) == 't')
        locker = subprocess.Popen(psql, env={**environment, 'PGAPPNAME':'swap_market_expiry_lock'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True); children.append(locker)
        locker.stdin.write('BEGIN; LOCK TABLE trading.swap_trade_revisions IN SHARE MODE;\n\\echo READY\n'); locker.stdin.flush()
        while locker.stdout.readline().strip() != 'READY':
            if locker.poll() is not None: raise AssertionError(locker.stderr.read())
        operation = str(uuid4())
        before = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        blocked = subprocess.Popen(psql+['-v','VERBOSITY=verbose'], env={**environment, 'PGAPPNAME':'swap_market_expiry_waiter'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True); children.append(blocked)
        blocked.stdin.write(runtime(booking(operation), token)); blocked.stdin.close(); blocked.stdin = None
        for _ in range(100):
            if blocked.poll() is not None: raise AssertionError(blocked.stderr.read())
            if query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='swap_market_expiry_waiter' AND NOT l.granted AND l.mode='RowExclusiveLock' AND l.relation='trading.swap_trade_revisions'::regclass;") == '1': break
            time.sleep(.05)
        else: raise AssertionError('Indexed swap did not wait on its final revision INSERT')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>granted_to FROM security.role_grants WHERE id='{market_grant}';") == 't': break
            time.sleep(.05)
        else: raise AssertionError('Expected independent market grant expiry')
        check('late wait expires market access while context and booking permissions remain live', query(f"SELECT expires_at>clock_timestamp() FROM security.request_contexts WHERE id='{token}';") == 't' and query(runtime(f"SELECT security.has_permission('ReadTrades') AND security.has_permission('BookTrade') AND NOT security.trading_access('{catalog}','BookTrade') AND NOT security.market_access('{catalog}','ReadMarketData') AND NOT security.reference_access('{manifest['referenceCatalogId']}','ReadMarketData');", issue('swap_market_expiry'))) == 't')
        locker.stdin.write('COMMIT;\n'); locker.stdin.flush(); _, error = locker.communicate(timeout=10); assert locker.returncode == 0, error
        _, error = blocked.communicate(timeout=10)
        check('indexed write rechecks market authority after its final revision wait', blocked.returncode != 0 and '42501' in error)
        check('late market expiry rolls back every trade and publication', query(f"SELECT count(*) FROM trading.trades WHERE operation_id='{operation}';") == '0' and query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == before)
    finally:
        for child in children:
            if child.poll() is None: child.kill(); child.communicate(timeout=5)
        query(f"""DELETE FROM security.request_contexts WHERE actor_id='{actor}';
         DELETE FROM security.role_grants WHERE principal_id='{actor}';
         DELETE FROM security.role_permissions WHERE role_id IN('{long_role}','{market_role}');
         DELETE FROM security.roles WHERE id IN('{long_role}','{market_role}');
         DELETE FROM security.workspace_memberships WHERE principal_id='{actor}';
         DELETE FROM security.identity_bindings WHERE principal_id='{actor}';
         DELETE FROM security.principals WHERE id='{actor}';""")
    print(f'{passed} swap storage checks passed.', flush=True)
