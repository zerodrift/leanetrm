"""Preserve genuine imported markets and valued agreements during publisher installation."""
import json
import os
from pathlib import Path
from uuid import uuid4

from assistant_context import migration_body
from check_fact_anchor_upgrade import run_checks as guard_checks
from check_reference_editing import main
from gas_transport_financial_managed import value
from point_trader_mark_fixture import PointTraderMarkFixture, fixture


def run():
    prepared = {}
    root = Path(__file__).resolve().parents[1]

    def before(query, migration):
        if migration.name != '0355_point_trader_marks.sql':
            return
        guard_checks(query, migration, number='0355', guard='point_trader_mark_upgrade',
            signatures=('market_data.guard_observation()', 'security.record_market_event()'))
        reference, market, access, manifest = fixture(root)
        for sql in [reference, market, access]:
            query(sql)
        f = PointTraderMarkFixture(query, manifest)
        query("CREATE FUNCTION security.synthetic_point_trader_mark_neighbor() RETURNS integer LANGUAGE sql STABLE "
            "SET search_path=pg_catalog AS 'SELECT 7';REVOKE ALL ON FUNCTION security.synthetic_point_trader_mark_neighbor() FROM PUBLIC;"
            "GRANT EXECUTE ON FUNCTION security.synthetic_point_trader_mark_neighbor() TO openetrm_runtime;")

        def decoy():
            return query("SELECT jsonb_build_object('definition',pg_get_functiondef(oid),'owner',pg_get_userbyid(proowner),"
                "'acl',proacl,'configuration',proconfig)::text FROM pg_proc WHERE oid='security.synthetic_point_trader_mark_neighbor()'::regprocedure;")

        decoy_before = decoy()
        book, capacity, version = f.new_capacity('synthetic_imported_market_owner')
        booking = {**f.booking(key='synthetic_imported_market_agreement'), 'definitionRevisionId': f.imported_definition,
            'bookId': book, 'entitlementId': capacity['entitlementId'], 'capacityVersionId': version['versionId']}
        booked = f.book_financial(booking)
        adopted = f.record_financial({**f.mark(booked['tradeId'], booking['operationId']),
            'marketDefinitionRevisionId': f.imported_definition, 'observationId': f.imported_observation})
        context = f.market_context()
        retained = f.read_financial(booked['tradeId'], context=context)
        retained.pop('authorityUntil')
        value(f, booked['tradeId'], context=context, rate_ids=f.imported_rate_observations[f.valuation_at],
            mark=retained['mark'], expected='7.5')
        tables = json.loads(query("SELECT jsonb_agg(n.nspname||'.'||c.relname ORDER BY n.nspname,c.relname) "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN('ref_data','market_data','trading','settlement_data') "
            "AND c.relkind='r' AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))

        # Migration 0361 gives every commodity an icon after this upgrade; the proof compares the columns that preceded it.
        def row(table):
            return "to_jsonb(t)-'icon_code'" if table == 'ref_data.commodities' else 'to_jsonb(t)'

        def digest():
            catalogs = ','.join("'" + catalog + "'" for catalog in [f.catalog, f.reference, manifest['referenceCatalogId']])
            return query('SELECT jsonb_object_agg(name,digest) FROM(' + ' UNION ALL '.join(
                f"SELECT '{table}'::text name,md5(coalesce(jsonb_agg({row(table)} ORDER BY {row(table)}::text),'[]')::text) digest "
                f"FROM {table} t WHERE t.catalog_id IN({catalogs})" for table in tables) + ')s;')

        old = digest()
        try:
            query("BEGIN;INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
                f"jsonb_build_object('id','{uuid4()}','source_event_id','synthetic_reserved_collision','source_revision_id','manual_point_revision_v1:unexplained',"
                f"'received_at',clock_timestamp()))).* FROM market_data.observations o WHERE(catalog_id,id)=('{f.catalog}','{f.imported_observation}');"
                + migration_body(migration.read_text()) + 'ROLLBACK;')
        except RuntimeError as error:
            assert 'point_trader_mark_upgrade: unexplained reserved revision namespace' in str(error), str(error)
        else:
            raise AssertionError('An unexplained pre-existing manual namespace collision was accepted.')
        assert digest() == old
        prepared.update(f=f, manifest=manifest, digest=digest, old=old, context=context, retained=retained, booked=booked, adopted=adopted,
            decoy=decoy, decoy_before=decoy_before)

    def loaded(_):
        return '', '', '', prepared['manifest']

    def verify(query, _):
        f = prepared['f']
        assert prepared['decoy']() == prepared['decoy_before'], 'Publisher installation changed an unrelated helper with a similar name.'
        assert prepared['digest']() == prepared['old'], 'Publisher installation changed existing reference, market, financial, title or operating rows.'
        retained = f.read_financial(prepared['booked']['tradeId'], context=prepared['context'])
        retained.pop('authorityUntil')
        assert retained == prepared['retained']
        assert query('SELECT count(*) FROM market_data.point_trader_mark_commands;') == '0'
        value(f, prepared['booked']['tradeId'], context=prepared['context'], rate_ids=f.imported_rate_observations[f.valuation_at],
            mark=retained['mark'], expected='7.5')
        first = f.publish(f.command('20'))
        assert first['changed'] is True
        f.observation = first['observationId']
        booking = f.booking(key='synthetic_first_published_market_agreement')
        booked = f.book_financial(booking)
        assert f.read_financial(booked['tradeId'])['mark'] is None
        f.record_financial(f.mark(booked['tradeId'], booking['operationId']))
        source = f.read_financial(booked['tradeId'])
        value(f, booked['tradeId'], rate_ids=f.rate_observations[f.valuation_at], mark=source['mark'], expected='7.5')
        assert f.read_financial(prepared['booked']['tradeId'])['mark']['id'] == prepared['adopted']['markId']
        print('PASS populated publisher upgrade preserves all imported/reference/financial raw facts and original valued source; first publication and separate new agreement adoption execute afterward.', flush=True)

    main(verify, before_migration=before, fixture_factory=loaded)


if __name__ == '__main__':
    if not os.environ.get('DOTNET'):
        raise SystemExit('DOTNET is required for the real managed financial consumer checks.')
    run()
