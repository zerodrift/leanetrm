"""Install financial Transport over immutable populated capacity and routes."""
import json
from pathlib import Path
from uuid import uuid4

from check_fact_anchor_upgrade import run_checks as guard_checks
from check_reference_editing import main
from gas_transport_financial_fixture import FinancialTransportFixture
from gas_transport_route_fixture import RouteFixture, fixture


def run():
    prepared = {}
    root = Path(__file__).resolve().parents[1]

    def before(query, migration):
        if migration.name != '0354_gas_transport_financial.sql':
            return
        guard_checks(query, migration, number='0354', guard='gas_transport_financial_upgrade',
            signatures=('assistant.is_prompt_page(text)',))
        reference, market, access, manifest = fixture(root)
        for sql in [reference, market, access]:
            query(sql)
        f = RouteFixture(query, manifest)
        f.source_plans()
        definition = f.route_definition()
        f.route_record(definition)
        facts = f.route_read(definition['routeId'])
        plan, operation = f.route_plan(facts), str(uuid4())
        f.route_record(plan, f.route_witness(facts, plan, operation), operation=operation)
        context = f.capture()
        retained = f.route_read(definition['routeId'], context=context)
        tables = json.loads(query("SELECT jsonb_agg(n.nspname||'.'||c.relname ORDER BY n.nspname,c.relname) "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN('trading','settlement_data') AND c.relkind='r' "
            "AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))

        def digest():
            return query('SELECT jsonb_object_agg(name,digest) FROM(' + ' UNION ALL '.join(
                f"SELECT '{table}'::text name,md5(coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]')::text) digest "
                f"FROM {table} t WHERE t.catalog_id='{f.catalog}'" for table in tables) + ')s;')

        prepared.update(f=f, manifest=manifest, digest=digest, before=digest(), context=context, retained=retained,
            definition=definition, plan=plan, operation=operation)

    def loaded(_):
        return '', '', '', prepared['manifest']

    def verify(query, _):
        f = prepared['f']
        assert prepared['digest']() == prepared['before'], 'Financial installation changed retained operating, title or cash facts.'
        assert f.route_read(prepared['definition']['routeId'], context=prepared['context']) == prepared['retained']
        assert query('SELECT count(*) FROM trading.gas_transport_financial_trades;') == '0'
        assert query("SELECT assistant.is_prompt_page('blotter_new_transport') AND assistant.is_prompt_page('transport_route_plans') AND assistant.is_prompt_page('transport_capacity') AND assistant.is_prompt_page('options') AND NOT assistant.is_prompt_page('synthetic_uninstalled_financial_page');") == 't'
        financial = FinancialTransportFixture.from_capacity(f)
        request = financial.booking()
        booked = financial.book_financial(request)
        mark = financial.mark(booked['tradeId'], request['operationId'])
        accepted = financial.record_financial(mark)
        assert financial.read_financial(booked['tradeId'])['mark']['id'] == accepted['markId']
        source = financial.capture()
        capacity = json.loads(financial.call(f"SELECT security.use_market_context('{f.catalog}','{source}');"
            f"SELECT security.use_trading_context('{f.catalog}','{financial.valuation_at}','{financial.valuation_at[:10]}');"
            f"SELECT security.gas_transport_financial_capacity_facts('{f.catalog}','{source}','{financial.valuation_at}','{financial.entitlement}');"))
        assert capacity['capacities'][0]['version']['id'] == financial.capacity_version
        assert f.route_read(prepared['definition']['routeId'], context=prepared['context']) == prepared['retained']
        print('PASS populated upgrade preserves exact four-capacity/seven-claim route, historical witnesses and raw facts; genuine mixed-reference financial booking and mark adoption execute afterward.', flush=True)

    main(verify, before_migration=before, fixture_factory=loaded)


if __name__ == '__main__':
    run()
