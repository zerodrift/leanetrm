"""Guard and install route admission over populated retained capacity sources."""
from pathlib import Path
import json
from uuid import uuid4

from check_fact_anchor_upgrade import run_checks as guard_checks
from check_reference_editing import main
from gas_transport_route_fixture import RouteFixture,fixture


def run():
    prepared={}
    root=Path(__file__).resolve().parents[1]
    def wrapper_contract(query):
        return query("SELECT jsonb_build_object('owner',proowner,'definer',prosecdef,'volatile',provolatile,'parallel',proparallel,'strict',proisstrict,'leakproof',proleakproof,"
            "'kind',prokind,'language',prolang,'arguments',proargnames,'return',prorettype,'settings',proconfig,'acl',proacl,'variadic',provariadic,'modes',proargmodes) "
            "FROM pg_proc WHERE oid='trading.verify_gas_transport_capacity_plan(uuid,uuid)'::regprocedure;")
    def before(query,migration):
        if migration.name!='0353_gas_transport_route.sql':return
        guard_checks(query,migration,number='0353',guard='gas_transport_route_upgrade',signatures=(
            'assistant.is_prompt_page(text)','trading.verify_gas_transport_capacity_plan(uuid,uuid)'))
        reference,market,access,manifest=fixture(root)
        for sql in [reference,market,access]:query(sql)
        f=RouteFixture(query,manifest);sources=f.source_plans()
        context=f.capture()
        sources=[f.read(source['entitlement']['id'],context=context) for source in sources]
        tables=json.loads(query("SELECT jsonb_agg(n.nspname||'.'||c.relname ORDER BY n.nspname,c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "WHERE n.nspname IN('trading','settlement_data') AND c.relkind='r' AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))
        def digest():
            return query('SELECT jsonb_object_agg(name,digest) FROM('+' UNION ALL '.join(
                f"SELECT '{table}'::text name,md5(coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]')::text) digest FROM {table} t WHERE t.catalog_id='{f.catalog}'" for table in tables)+')s;')
        prepared.update(f=f,manifest=manifest,sources=sources,context=context,digest=digest,before=digest(),wrapper=wrapper_contract(query))
    def loaded(_):return '','','',prepared['manifest']
    def verify(query,_):
        f=prepared['f']
        assert prepared['digest']()==prepared['before'],'Route installation changed retained capacity, title, custody, cash or physical facts.'
        assert wrapper_contract(query)==prepared['wrapper'],'Capacity extraction changed the predecessor wrapper authority or execution metadata.'
        assert query('SELECT count(*) FROM trading.gas_transport_routes;')=='0'
        assert query("SELECT assistant.is_prompt_page('transport_route_plans') AND assistant.is_prompt_page('transport_capacity') AND assistant.is_prompt_page('options') AND NOT assistant.is_prompt_page('synthetic_uninstalled_routes');")=='t'
        for old in prepared['sources']:
            assert f.read(old['entitlement']['id'],context=prepared['context'])==old
            first=old['plans'][0]
            request=json.loads(query(f"SELECT request FROM trading.gas_transport_capacity_commands WHERE(catalog_id,operation_id)=('{f.catalog}','{first['id']}');"))
            assert f.record(request['body'],first['assessment'],operation=first['id'],context=request['contextId'])['revisionId']==first['id']
        definition=f.route_definition();defined=f.route_record(definition)
        facts=f.route_read(definition['routeId']);plan=f.route_plan(facts);operation=str(uuid4())
        accepted=f.route_record(plan,f.route_witness(facts,plan,operation),operation=operation)
        assert accepted['definitionId']==defined['definitionId'] and len(f.route_read(definition['routeId'])['memberships'])==4
        assert f.call(f"SELECT has_function_privilege(current_user,'trading.verify_gas_transport_capacity_snapshot(uuid,uuid,uuid,integer,text,jsonb)','EXECUTE');")=='f'
        print('PASS populated route upgrade preserves all four capacity source streams/seven claims, historical exact reads and original receipts; extracted verifier remains private and new route adoption is executable.',flush=True)
    main(verify,before_migration=before,fixture_factory=loaded)


if __name__=='__main__':run()
