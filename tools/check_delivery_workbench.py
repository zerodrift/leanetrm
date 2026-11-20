"""Qualify delivery workbench source/target relationships in an owned stock database."""
import json
import os
from uuid import uuid4
from check_reference_editing import main
from gas_transport_route_fixture import RouteFixture, fixture, identity, literal
from storage_facility_fixture import StorageFacilityFixture
from check_delivery_workbench_http import daily_route


def refuses(action, code):
    try:
        action()
    except RuntimeError as error:
        assert code in str(error), str(error)
    else:
        raise AssertionError('Expected refusal ' + code)


def before(query, migration):
    if migration.name != '0358_delivery_workbench_allocations.sql':
        return
    from check_fact_anchor_upgrade import run_checks as guard_checks
    guard_checks(query, migration, number='0358', guard='delivery_workbench_upgrade', signatures=('assistant.is_prompt_page(text)',))


def source_setup(query,manifest):
    if not os.environ.get('DOTNET'):
        raise ValueError('Set DOTNET to qualify ordinary managed booking and source allocation.')
    f = RouteFixture(query, manifest)
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN "
          "unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
    # One existing real booked occurrence and reported source/assignment are
    # created by the same managed native operations as the installed product.
    owned = StorageFacilityFixture.__new__(StorageFacilityFixture)
    owned.query, owned.manifest, owned.catalog, owned.at = query, manifest, f.catalog, '2021-01-15T16:00:00Z'
    owned.point = dict(point_code='synthetic_gas_point_a', quantity='test_gas_quantity')
    source = owned.source()
    route = daily_route(f, quantity='1000')
    facts = f.route_read(route['routeId'])
    capacity = facts['sourceCapacities'][0]
    f.add_capacity_stream(capacity, 'synthetic_delivery_second_source')
    for key, source_key in [('synthetic_route_chosen', 'synthetic_complete_daily_source'),
                            ('synthetic_route_second', 'synthetic_delivery_second_source')]:
        facts = f.route_read(route['routeId'])
        command = f.route_plan(facts, key=key, source_key=source_key)
        operation = str(uuid4())
        f.route_record(command, f.route_witness(facts, command, operation), operation=operation)
    retained = f.capture()
    route_facts = f.route_read(route['routeId'], context=retained)
    return dict(f=f,owned=owned,source=source,route=route,retained=retained,route_facts=route_facts)


def temporal_target_regression(f, source, route):
    f.at, f.effective = '2021-01-15T23:59:59.999999Z', '2021-01-15T08:00:00Z'
    facts = f.route_read(route['routeId'])
    plans = {p['business_key']:p for p in facts['plans']}
    def command(plan, *, kind='allocate', previous=None):
        return dict(kind=kind, scopeId=route['routeId'], routePlanId=plan['id'], expectedPreviousId=previous,
                    expectedRouteStateVersion=facts['stateVersion'], policy='exact_terminal_native_interval_v1',
                    evidence='Synthetic explicit native target timing', allocations=[] if kind=='release' else [
                        dict(nodeId=route['receiptNode'], obligationId=source['obligationId'], tradeRevision=source['tradeRevision'], day=source['day'], quantity='1000')])
    def record(body, effective, *, context=None, commit=True):
        context = context or f.capture(day=effective[:10])
        return json.loads(f.call(f"SELECT security.record_delivery_route_allocation('{f.catalog}','{uuid4()}',{literal(body)},'{effective}','{context}')::text;", commit=commit))
    old_context = f.capture()
    capacity = facts['sourceCapacities'][0]
    head = next(p for p in capacity['plans'] if p['business_key']=='synthetic_complete_daily_source')
    def native_claim(c):
        return dict(claimId=c['id'], pathId=c['path_id'], receiptFrom=c['receipt_from'], receiptTo=c['receipt_to'], receiptQuantity=c['receipt_quantity'],
                    deliveryFrom=c['delivery_from'], deliveryTo=c['delivery_to'], deliveryQuantity=c['delivery_quantity'])
    correction = str(uuid4())
    replacement = [native_claim(c) for c in capacity['claims'] if c['plan_id']==head['id']]
    for ordinal, claim in enumerate(replacement): claim['claimId'] = identity('gas_transport_claim_v1', correction, ordinal)
    complete = [native_claim(c) for c in capacity['claims'] if c['plan_id']!=head['id']] + replacement
    capacity_command = dict(kind='plan', entitlementId=capacity['entitlement']['id'], expectedStateVersion=capacity['stateVersion'], expectedPreviousId=head['id'],
        businessKey=head['business_key'], versionId=capacity['version']['id'], admissionPolicy='complete_feasible_replacement_v1',
        evidence='Synthetic source correction after a certified cut', claims=sorted(replacement, key=lambda c:(c['pathId'],c['claimId'])))
    f.record(capacity_command, f.capacity_witness(capacity, complete), operation=correction)
    refuses(lambda: record(command(plans['synthetic_route_chosen']), f.effective, context=old_context, commit=False), 'delivery_workbench_stale')
    for key, source_key in [('synthetic_route_chosen', 'synthetic_complete_daily_source'), ('synthetic_route_second', 'synthetic_delivery_second_source')]:
        current = f.route_read(route['routeId'])
        selection = f.route_plan(current, key=key, source_key=source_key)
        operation = str(uuid4())
        f.route_record(selection, f.route_witness(current, selection, operation), operation=operation)
    facts = f.route_read(route['routeId'])
    plans = {p['business_key']:p for p in facts['plans']}
    first = record(command(plans['synthetic_route_chosen']), f.effective)
    future = record(command(plans['synthetic_route_chosen'], kind='release', previous=first['allocationPlanId']), '2021-01-16T08:00:00Z')
    assert future['changed'] is True
    before_count = f.query('SELECT count(*) FROM trading.delivery_route_allocation_plans;')
    # A latest-known release tomorrow must not free yesterday's full native
    # target for a second route stream effective today.
    refuses(lambda: record(command(plans['synthetic_route_second']), '2021-01-15T09:00:00Z'), 'delivery_workbench_stale')
    assert f.query('SELECT count(*) FROM trading.delivery_route_allocation_plans;') == before_count
    later = record(command(plans['synthetic_route_second']), '2021-01-17T09:00:00Z')
    assert later['changed'] is True
    print('PASS delivery target timing: a future release cannot free a currently allocated target for an earlier second stream', flush=True)


def checks(query, manifest):
    prepared=source_setup(query,manifest)
    f = prepared['f']
    assert query("SELECT assistant.is_prompt_page('delivery_workbench_gas') AND assistant.is_prompt_page('delivery_workbench_crude') AND NOT assistant.is_prompt_page('synthetic_uninstalled_delivery');") == 't', 'Displayed delivery page identities must be admitted for prompts without enabling financial operations.'
    assert query('SELECT count(*) FROM trading.delivery_route_allocation_plans;') == '0'
    assert f.route_read(prepared['route']['routeId'], context=prepared['retained'])['plans'] == prepared['route_facts']['plans']
    source = prepared['source']
    legacy = json.loads(prepared['owned'].call(f"SELECT security.read_facility_quantity_ledger('{f.catalog}','{source['ledgerId']}')::text;"))
    assert legacy['source']['id'] == source['sourceRevisionId'] and legacy['allocations'][0]['quantity'] == '60'
    print('PASS fresh delivery relationships retain booked1000, reported100/allocation60, exact native route/capacity IDs and original source facts', flush=True)
    temporal_target_regression(f, source, prepared['route'])
    f.at, f.effective = '2020-12-19T23:59:59.999999Z', '2020-12-19T16:00:00Z'
    definition = f.route_definition('synthetic_delivery_workbench_route')
    f.route_record(definition)
    context = f.capture()
    state = json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');SELECT security.use_trading_context('{f.catalog}','{f.at}');SELECT security.delivery_route_allocation_state('{f.catalog}','{definition['routeId']}')::text;"))
    assert state['plans'] == []
    assert state['source']['route']['id'] == definition['routeId']
    missing = json.loads(f.call(f"SELECT security.delivery_route_allocation_status('{f.catalog}','{uuid4()}')::text;"))
    assert missing['found'] is False and missing['authorityUntil']
    assert state['history'] == []
    command = dict(kind='allocate', scopeId=definition['routeId'], routePlanId=str(uuid4()),
                   expectedPreviousId=None, expectedRouteStateVersion=1,
                   policy='exact_terminal_native_interval_v1', evidence='Synthetic explicit assignment',
                   allocations=[dict(nodeId=str(uuid4()), obligationId=str(uuid4()), tradeRevision=1,
                                     day='2020-12-20', quantity='1')])
    def record(body):
        return f.call(f"SELECT security.record_delivery_route_allocation('{f.catalog}','{uuid4()}',{literal(body)},'{f.effective}','{context}')::text;")
    for field, value in [('policy', 'inferred'), ('allocations', []), ('extra', 'unsupported')]:
        refuses(lambda field=field, value=value: record({**command, field:value}), 'delivery_workbench_input')
    refuses(lambda: f.call('SELECT * FROM trading.delivery_route_allocation_plans;'), 'permission denied')
    print('PASS delivery workbench: exact source, empty history, closed assignment ingress and private storage', flush=True)


if __name__ == '__main__':
    main(checks=checks, before_migration=before, fixture_factory=fixture)
