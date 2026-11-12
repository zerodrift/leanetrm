"""Recorded-cash migration over existing native option bookings in an owned PG18 cluster."""
import json
from uuid import uuid4
from check_reference_editing import main
from exchange_fixture import fixture,remap
from preview_fixture import catalogue_fixture,trade_booking


def qualification():
    retained={}

    def before(query,migration):
        if migration.name=='0250_recorded_cash_payments.sql':
            reference,market,security,trading,manifest=fixture(migration.parents[2])
            for sql in (reference,market,security,trading):query(sql)
            query("CREATE ROLE preview_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT IN ROLE openetrm_runtime;"
                  "CREATE ROLE preview_authenticator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT IN ROLE openetrm_authenticator;")
            query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
            query(catalogue_fixture(manifest,manifest['identityIssuer']))
            catalog=manifest['catalogId']
            book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
            for lots in (1,-2):
                token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                            f"'{manifest['identityIssuer']}','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
                query(f"""BEGIN;SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
                    SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}',
                    'nymex_on_20270101_c3.10',{lots},1.25,'2026-10-19','premium_paid_up_front','2026-10-16T16:00:00Z',
                    NULL,'futures_option_premium_v1',NULL,NULL,NULL,'{remap(json.dumps(trade_booking()))}'::jsonb);COMMIT;""")
            retained.update(catalog=catalog,rows=query(f"SELECT json_agg(t ORDER BY trade_id)::text FROM trading.futures_option_trade_revisions t WHERE catalog_id='{catalog}';"))
        elif migration.name=='0251_saved_pnl_reports.sql':
            catalog=retained['catalog']
            assert query(f"SELECT json_agg(t ORDER BY trade_id)::text FROM trading.futures_option_trade_revisions t WHERE catalog_id='{catalog}';")==retained['rows']
            assert query(f"""SELECT count(*),count(DISTINCT instrument_id),bool_and(
                (m.binding_id,m.instrument_id,m.book_id,m.publication_order,m.effective_at)=
                (r.premium_instrument_id,r.premium_instrument_id,r.book_id,r.publication_order,r.effective_at))
                FROM trading.cash_payment_memberships m JOIN trading.futures_option_trade_revisions r
                ON(m.catalog_id,m.trade_id)=(r.catalog_id,r.trade_id) WHERE m.catalog_id='{catalog}';""")=='2|1|t'
            assert query(f"SELECT count(*) FROM settlement_data.cash_payment_revisions WHERE catalog_id='{catalog}';")=='0'
            retained['verified']=True
            # Only the check's temporary login roles are recreated by the shared
            # runner's ordinary baseline fixture after the migration loop.
            query('DROP ROLE preview_runtime;DROP ROLE preview_authenticator;')
            print('PASS populated cash upgrade: original option rows and exact memberships retained; shared premium units have separate owners and no fabricated payment facts',flush=True)

    def after(query,_):
        assert retained.get('verified') is True,'The populated upgrade check did not run.'
        assert query("SELECT to_regclass('risk.saved_pnl_reports') IS NOT NULL;")=='t'

    main(checks=after,before_migration=before)


if __name__=='__main__':qualification()
