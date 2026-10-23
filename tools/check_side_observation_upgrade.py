"""Keep pre-observation monthly/daily definitions and operation receipts exact."""
import json
import subprocess
from pathlib import Path
from uuid import uuid4

from preview_fixture import fixture, trade_booking, trading_fixture
from swap_fixture import fixture as swap_fixture


def run_checks(query, psql, environment):
    root = Path(__file__).resolve().parents[1]
    name = 'openetrm_observation_upgrade_' + uuid4().hex
    target = list(psql)
    target[target.index('-d') + 1] = name

    def sql(text):
        result = subprocess.run(target, input=text, text=True, capture_output=True, env=environment, cwd=root, timeout=40)
        if result.returncode:
            raise AssertionError(result.stderr)
        return result.stdout.strip()

    def local_roles(text):
        return '\n'.join(line for line in text.splitlines() if not line.startswith('CREATE ROLE '))

    query(f'CREATE DATABASE {name} TEMPLATE template0;')
    try:
        assert Path(sql('SHOW data_directory;')).resolve() == Path(query('SHOW data_directory;')).resolve()
        for migration in sorted((root/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            if int(migration.name[:4]) < 101:
                sql(local_roles(migration.read_text()))
        reference, market, security, manifest = fixture(root)
        secondary, _ = swap_fixture()
        for content in (reference, market, secondary, security, trading_fixture(manifest['catalogId'], swap_defaults=True)):
            sql(local_roles(content))
        catalog, tenant, workspace, rc, rr = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId', 'referenceCatalogId', 'referenceRevisionId'))

        def runtime(statement):
            token = sql("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                        f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return sql(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;")

        rule = json.dumps({'frequency': 'monthly', 'rule': 'day_of_following_month', 'days': None, 'dayOfMonth': 18,
                           'dayBasis': None, 'shortMonth': 'reject', 'adjustment': 'unadjusted', 'calendar': None,
                           'implementation': 'monthly_payment_date_rules_v1'})
        commands = []
        for revision, curve, mode in [(manifest['revisionId'], 'base_forward', 'monthly_index_price'), ('60000000-0000-0000-0000-000000000002', 'daily_gas', 'calendar_day_arithmetic')]:
            command = f"""SELECT security.save_projected_swap_with_parties('{catalog}','{uuid4()}',NULL,NULL,'d1000000-0000-0000-0000-000000000001',
             '{revision}','{rc}','{rr}','{curve}','test_price','usd','usd','2021-01-01','2021-02-01','2021-02-18T00:00:00Z',
             '2021-01-01','2021-02-01','2021-02-18T00:00:00Z','{mode}',3,100,-1,false,'2020-12-18T16:00:00Z',NULL,'2020-12-18T16:00:00Z',
             'Initial booking','independent_side_financial_swap_v2','{rule}'::jsonb,'{rule}'::jsonb,'test_gas',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb,'{{}}'::uuid[],NULL);"""
            commands.append((command, json.loads(runtime(command))))
        tables = ['swap_fixed_sides', 'swap_floating_sides', 'swap_reset_projections', 'swap_instruments', 'swap_trade_revisions', 'swap_payment_rules', 'publications', 'trades']

        def rows(table, added=False):
            expression = "to_jsonb(t)-'observation_schedule_id'" if table == 'swap_floating_sides' and added else 'to_jsonb(t)'
            return json.loads(sql(f"SELECT coalesce(jsonb_agg(row ORDER BY row::text),'[]'::jsonb)::text FROM(SELECT {expression} AS row FROM trading.{table} t)v;"))

        before = {table: rows(table) for table in tables}
        signature = "security.save_projected_swap_with_parties(uuid,uuid,uuid,integer,uuid,uuid,uuid,uuid,text,text,text,text,date,date,timestamptz,date,date,timestamptz,text,numeric,numeric,numeric,boolean,timestamptz,timestamptz,timestamptz,text,text,jsonb,jsonb,text,uuid,integer,text,jsonb,uuid[],jsonb)"
        oid = sql(f"SELECT '{signature}'::regprocedure::oid;")
        sql((root/'db/migrations/0101_side_observation_schedules.sql').read_text())
        assert sql(f"SELECT '{signature}'::regprocedure::oid;") == oid
        assert before == {table: rows(table, True) for table in tables}, 'Schema adoption changed retained financial rows or request bytes'
        assert sql('SELECT count(*) FROM trading.swap_observation_schedules;') == '0'
        for command, expected in commands:
            assert json.loads(runtime(command)) == expected, 'A pending original operation lost its exact replay outcome'
        assert before == {table: rows(table, True) for table in tables}, 'Replay created or changed a retained definition'
        print('PASS observation upgrade: original monthly/daily rows, function identity, instrument keys and pending-command receipts remain exact', flush=True)
    finally:
        query(f'DROP DATABASE {name} WITH(FORCE);')
