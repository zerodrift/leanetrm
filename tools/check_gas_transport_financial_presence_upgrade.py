"""The scoped absence upgrade preserves genuine financial facts and read grants."""
import json
from uuid import uuid4

from check_reference_editing import main
from seed_fixture_market import load_fixture
from check_gas_transport_financial_presence import SIGNATURES, absence_commit, rejected, scope
from gas_transport_financial_fixture import FinancialTransportFixture, fixture


def run():
    retained = {}

    def before(query, migration):
        if migration.name != '0357_scoped_transport_financial_presence.sql':
            return
        reference, market, access, manifest = fixture(migration.parents[2])
        load_fixture(query,reference,market,access,manifest)
        f = FinancialTransportFixture(query, manifest)
        booking = f.booking()
        saved = f.book_financial(booking)
        mark = f.mark(saved['tradeId'], booking['operationId'])
        operation, mark_context = str(uuid4()), f.capture()
        accepted = f.record_financial(mark, operation=operation, context=mark_context)
        context = f.capture()
        source = f.read_financial(saved['tradeId'], context=context)
        assert source['mark']['id'] == accepted['markId'] and source['cash']['amount'] == '-12.5'
        tables = json.loads(query("SELECT jsonb_agg(n.nspname||'.'||c.relname ORDER BY n.nspname,c.relname) "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN('trading','settlement_data') AND c.relkind='r' "
            "AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))
        def digest():
            return query('SELECT jsonb_object_agg(name,digest) FROM(' + ' UNION ALL '.join(
                f"SELECT '{table}'::text name,encode(sha256(convert_to(coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]')::text,'UTF8')),'hex') digest "
                f"FROM {table} t WHERE t.catalog_id='{f.catalog}'" for table in tables) + ')s;')
        metadata = "SELECT jsonb_object_agg(p.oid::regprocedure::text,jsonb_build_object('owner',p.proowner,'acl',p.proacl,'config',p.proconfig,'definer',p.prosecdef,'strict',p.proisstrict,'volatile',p.provolatile,'parallel',p.proparallel,'leakproof',p.proleakproof)) FROM pg_proc p WHERE p.oid=ANY(ARRAY[" + ','.join("'" + s + "'::regprocedure" for s in SIGNATURES) + ']);'
        other_definitions = lambda: query("SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.oid) FROM pg_proc p WHERE p.oid=ANY(ARRAY[" + ','.join("'" + s + "'::regprocedure" for s in SIGNATURES[1:]) + ']);')
        retained.update(f=f, manifest=manifest, booking=booking, saved=saved, mark=mark, accepted=accepted, operation=operation, mark_context=mark_context,
            context=context, source=source, digest=digest, before=digest(), metadata=metadata, attributes=query(metadata),
            definitions=other_definitions, original_definitions=other_definitions())

    def loaded(_):
        return '', '', '', retained['manifest']

    def verify(query, _):
        f = retained['f']
        assert retained['digest']() == retained['before'], 'The presence upgrade changed an existing financial, cash or source fact.'
        assert query(retained['metadata']) == retained['attributes'], 'The presence upgrade changed helper privileges or execution properties.'
        assert retained['definitions']() == retained['original_definitions'], 'The scope or future projection fence was changed.'
        source = f.read_financial(retained['saved']['tradeId'], context=retained['context'])
        # A new authorized read captures its own deadline, not another business fact.
        assert query(f"SELECT clock_timestamp()<'{source['authorityUntil']}'::timestamptz;") == 't'
        assert source == {**retained['source'], 'authorityUntil': source['authorityUntil']}
        assert f.book_financial(retained['booking']) == retained['saved']
        assert f.record_financial(retained['mark'], operation=retained['operation'], context=retained['mark_context']) == retained['accepted']
        assert retained['digest']() == retained['before']
        at = query("SELECT to_char(known_at+interval '1 day','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') "
                   f"FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{f.catalog}','{retained['context']}');")
        future = f.capture(day=at[:10])
        absence_commit(f, future, at, [f.book], ['00000000-0000-0000-0000-000000000001'])
        rejected(lambda: scope(f, future, at, [f.book]), 'gas_transport_financial_context')
        print('PASS populated Transport presence upgrade preserves exact financial states, mark provenance, ordinary cash, native relationships, helper grants and original outcomes; excluded absence works while real future valuation still refuses.', flush=True)

    main(verify, before_migration=before, fixture_factory=loaded,through_migration='0357')


if __name__ == '__main__':
    run()
