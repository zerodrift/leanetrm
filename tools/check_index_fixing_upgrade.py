"""Preserve a real legacy swap across fixing-convention admission changes."""
import json
import subprocess
from pathlib import Path
from uuid import uuid4

from preview_fixture import fixture, trade_booking, trading_fixture
from swap_fixture import fixture as swap_fixture


def run_checks(query, psql, environment, root):
    name = 'openetrm_fixing_upgrade_' + uuid4().hex
    target = list(psql)
    target[target.index('-d') + 1] = name

    def upgraded(sql):
        result = subprocess.run([str(p) for p in target], input=sql, text=True,
                                capture_output=True, env=environment, cwd=root, timeout=30)
        if result.returncode:
            raise AssertionError(result.stderr)
        return result.stdout.strip()

    def legacy_fixture(sql):
        # Cluster roles already belong to the outer, newly owned preview. The
        # two fixtures otherwise remain unchanged, including their sealed pins.
        return '\n'.join(line for line in sql.splitlines()
                         if not line.startswith('CREATE ROLE ')
                         and not line.startswith('INSERT INTO market_data.index_fixing_observations '))

    query(f'CREATE DATABASE {name} TEMPLATE template0;')
    try:
        assert Path(upgraded('SHOW data_directory;')).resolve() == Path(query('SHOW data_directory;')).resolve()
        for migration in sorted((root/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            if int(migration.name[:4]) <= 49:
                upgraded(legacy_fixture(migration.read_text()))
        reference, market, security, manifest = fixture(root, current_schema=True)
        swap_market, _ = swap_fixture()
        for sql in (reference, market, swap_market, security,
                    trading_fixture(manifest['catalogId'], swap_defaults=True)):
            upgraded(legacy_fixture(sql))
        catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))

        def runtime(sql):
            token = upgraded("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                             f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return upgraded(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} COMMIT;")

        def rule(days):
            return json.dumps(dict(frequency='monthly', rule='days_after_period_end', days=days,
                                   dayOfMonth=None, dayBasis='calendar_days', shortMonth=None,
                                   adjustment='unadjusted', calendar=None,
                                   implementation='monthly_payment_date_rules_v1'))

        operation = str(uuid4())
        index = upgraded(f"SELECT index_a_code FROM ref_data.settlement_targets WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code='test_index_target';")

        def arguments(op=operation, quantity=100):
            return (f"'{catalog}','{op}',NULL,NULL,'d1000000-0000-0000-0000-000000000001',"
                    f"'{manifest['revisionId']}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}',"
                    f"'base_forward','{index}','usd','usd','2021-01-01','2021-02-01','2021-02-18T00:00:00Z',"
                    "'2021-01-01','2021-02-01','2020-12-31T12:00:00Z','2021-03-18T00:00:00Z','monthly_index_price',"
                    f"3,{quantity},-1,false,'2020-12-18T16:00:00Z',NULL,'2020-12-18T16:00:00Z','Initial booking',"
                    f"'independent_side_financial_swap_v2','{rule(18)}'::jsonb,'{rule(46)}'::jsonb,'test_gas',"
                    f"NULL,NULL,'Synthetic retained swap','{json.dumps(trade_booking())}'::jsonb")

        receipt = runtime('SELECT security.save_swap_with_parties(' + arguments() + ');')
        retained = json.loads(receipt)
        assert retained['revision'] == 1 and retained['kind'] == 'booked'
        context = runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")
        bind = (f"SELECT security.use_market_context('{catalog}','{context}');"
                f"SELECT security.use_trading_context('{catalog}','2020-12-18T16:00:00Z');")
        history_sql = ("SELECT jsonb_build_object('side',to_jsonb(s),'instrument',to_jsonb(i),'revision',to_jsonb(r)) "
                       "FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i "
                       "ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) "
                       "JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id) "
                       f"WHERE r.catalog_id='{catalog}' AND r.trade_id='{retained['tradeId']}';")
        historical = runtime(bind + history_sql)
        assert json.loads(historical)['side']['implementation'] == 'prefixing_index_projection_v1'

        def table_names():
            return upgraded("SELECT quote_ident(schemaname)||'.'||quote_ident(tablename) FROM pg_tables "
                            "WHERE schemaname IN('ref_data','market_data','trading') ORDER BY schemaname COLLATE \"C\",tablename COLLATE \"C\";").splitlines()
        tables = table_names()

        def snapshot():
            return {table: upgraded(f'SELECT to_jsonb(t)::text FROM {table} t ORDER BY to_jsonb(t)::text COLLATE "C";')
                    for table in tables}

        before = snapshot()
        upgraded((root/'db/migrations/0050_index_fixing_observations.sql').read_text())
        assert snapshot() == before
        assert upgraded('SELECT count(*) FROM market_data.index_fixing_observations;') == '0'
        print('PASS index-fixing upgrade: original financial/reference/market rows and publication/knowledge coordinates remain byte-identical; no fixing policy is inferred for sealed bundles', flush=True)
        assert runtime(bind + history_sql) == historical
        assert runtime(f"SELECT security.swap_operation_status('{catalog}','{operation}');") == receipt
        assert runtime('SELECT security.retry_legacy_swap_with_parties(' + arguments() + ');') == receipt
        signature = ('uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,text,text,text,date,date,timestamptz,'
                     'date,date,timestamptz,timestamptz,text,numeric,numeric,numeric,boolean,timestamptz,timestamptz,'
                     'timestamptz,text,text,jsonb,jsonb,text,uuid,integer,text,jsonb')
        assert runtime(f"SELECT has_function_privilege(current_user,'security.save_swap_with_parties({signature})','EXECUTE');") == 'f'

        def reject(arguments_sql, message):
            runtime("DO $$BEGIN PERFORM security.retry_legacy_swap_with_parties(" + arguments_sql + "); "
                    "RAISE EXCEPTION 'Expected legacy rejection'; EXCEPTION WHEN SQLSTATE 'P0001' THEN "
                    f"IF SQLERRM<>'{message}' THEN RAISE; END IF; END$$;")

        reject(arguments(quantity=101), 'swap_conflict')
        reject(arguments(op=str(uuid4())), 'swap_fixing_read_only')
        assert snapshot() == before
        print('PASS index-fixing upgrade: exact legacy retry/status and certified history survive; changed terms conflict and unused commands cannot create manual-boundary trades', flush=True)
        for migration in sorted((root/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            if 50 < int(migration.name[:4]) < 123:
                upgraded('\n'.join(line for line in migration.read_text().splitlines() if not line.startswith('CREATE ROLE ')))
        tables = table_names()
        before_cutover = snapshot()
        try:
            upgraded((root/'db/migrations/0123_whole_term_swap_sides.sql').read_text())
        except AssertionError as error:
            assert 'whole_term_swap_rebooking_required' in str(error), error
        else:
            raise AssertionError('An old swap crossed the explicit whole-term rebooking boundary')
        assert snapshot() == before_cutover
        assert upgraded("SELECT to_regclass('trading.swap_side_periods') IS NULL;") == 't'
        print('PASS index-fixing cutover: whole-term adoption refuses retained old swaps atomically; the current adapter does not reinterpret stored definitions', flush=True)
    finally:
        # Every target connection is a completed subprocess. No force-drop or
        # shared cluster teardown is allowed here.
        query(f'DROP DATABASE {name};')
