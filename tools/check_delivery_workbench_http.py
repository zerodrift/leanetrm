"""Real delivery workbench HTTP/UI qualification in a disposable stock database."""
import argparse
import json
import os
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main as native_main
from crude_fixture import REVISION as CRUDE_REVISION, prepare as prepare_crude
from gas_transport_route_fixture import RouteFixture, identity, literal
from owned_lifecycle_browser import build_assets, run as run_browser
from preview_fixture import catalogue_fixture, fixture as preview_fixture, trade_booking
from synthetic_side_calendars import side_calendar_policy


def fixture(root):
    reference, market, access, manifest = preview_fixture(root, fixing_frames=True)
    reference, market, manifest = prepare_crude(reference, market, manifest)
    manifest['inputMarkets'] = [manifest.pop('crudeMarket')]
    manifest['identityIssuer'] = 'https://preview.invalid'
    return reference, market, access, manifest


def daily_route(f, *, quantity='100'):
    """Use native executable points and an exact whole gas day, without conversion."""
    start, end = '2021-01-01T00:00:00Z', '2021-01-02T00:00:00Z'
    definition = f.definition('synthetic_delivery_daily_capacity')
    limit = dict(**{'from': start}, through=end, maximumPerHour='100')
    path = next(p for p in definition['paths'] if p['receiptPoint'] == 'synthetic_gas_point_a')
    path['limits'] = [dict(limit)]
    definition.update(**{'from': start}, through=end, agreement=[dict(limit)], paths=[path],
                      receipts=[dict(point='synthetic_gas_point_a', limits=[dict(limit)])],
                      deliveries=[dict(point='synthetic_gas_point_b', limits=[dict(limit)])])
    f.record(definition)
    facts = f.read(definition['entitlementId'])
    operation = str(uuid4())
    claim = dict(claimId=identity('gas_transport_claim_v1', operation), pathId=path['pathId'],
                 receiptFrom=start, receiptTo=end, receiptQuantity=quantity,
                 deliveryFrom=start, deliveryTo=end, deliveryQuantity=quantity)
    command = dict(kind='plan', entitlementId=definition['entitlementId'], expectedStateVersion=facts['stateVersion'],
                   expectedPreviousId=None, businessKey='synthetic_complete_daily_source', versionId=facts['version']['id'],
                   admissionPolicy='complete_feasible_replacement_v1', evidence='Synthetic daily native capacity claim', claims=[claim])
    f.record(command, f.capacity_witness(facts, [claim]), operation=operation)
    route = identity('gas_transport_route_v1:synthetic_delivery_daily_route', f.book)
    nodes = [dict(nodeKey=key, nodeId=identity('gas_transport_route_node_v1:' + key, route), role=role,
                  pipeline='test_pipeline_a', zone='synthetic_gas_zone_' + key, point='synthetic_gas_point_' + key)
             for key, role in [('a', 'external_receipt_v1'), ('b', 'external_delivery_v1')]]
    route_definition = dict(kind='define', routeId=route, expectedStateVersion=0, expectedPreviousId=None,
        businessKey='synthetic_delivery_daily_route', bookId=f.book, **{k:v for k,v in trade_booking().items() if k.startswith('legalEntity')},
        referenceCatalogId=f.manifest['referenceCatalogId'], referenceRevisionId=f.manifest['referenceRevisionId'],
        product='test_gas', quantity='test_gas_quantity', implementation='gas_transport_chosen_uniform_route_v1',
        fractionBasis='fuel_and_loss_each_fraction_of_gross_receipt_v1', flowShape='one_explicit_uniform_receipt_and_delivery_per_segment_v1',
        transitLaw='fixed_nonnegative_elapsed_microseconds_v1', nodeBalanceLaw='simultaneous_inflow_equals_outflow_no_storage_v1',
        junctionPolicy='same_exact_pipeline_point_handoff_v1', membershipPolicy='whole_capacity_plan_stream_exclusive_v1',
        nodes=sorted(nodes, key=lambda n:n['nodeId']), segments=[dict(segmentKey='ab', segmentId=identity('gas_transport_route_segment_v1:ab', route),
            entitlementId=definition['entitlementId'], pathId=path['pathId'], fromNodeKey='a', toNodeKey='b',
            fromNodeId=nodes[0]['nodeId'], toNodeId=nodes[1]['nodeId'], fuelFraction=dict(n='0',d='1'), lossFraction=dict(n='0',d='1'), transitMicroseconds='0')],
        evidence='Synthetic direct daily route using retained native delivery points')
    f.route_record(route_definition)
    facts = f.route_read(route)
    command = f.route_plan(facts)
    operation = str(uuid4())
    f.route_record(command, f.route_witness(facts, command, operation), operation=operation)
    facts = f.route_read(route)
    return dict(routeId=route, routePlanId=facts['plans'][0]['id'], routeStateVersion=facts['stateVersion'],
                receiptNode=nodes[0]['nodeId'], deliveryNode=nodes[1]['nodeId'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin', 'dotnet', 'browser']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--api-bin', type=Path)
    args = parser.parse_args()
    for executable in [args.pg_bin/'psql', args.dotnet, args.browser]:
        if not executable.is_file():
            parser.error('An installed executable is required: ' + str(executable))
    if args.api_bin is not None and not args.no_build:
        parser.error('--api-bin requires --no-build')
    os.environ['DOTNET'] = str(args.dotnet.resolve())
    if not args.no_build:
        build_assets(args.dotnet.resolve())
    children = []

    def checks(query, manifest):
        print('Delivery workbench migrations loaded; preparing exact native sources.', flush=True)
        f = RouteFixture(query, manifest)
        previous = query(f"SELECT id FROM market_data.catalogue_publications WHERE catalog_id='{f.catalog}' ORDER BY publication_order DESC LIMIT 1;")
        query(catalogue_fixture(manifest, manifest['identityIssuer'], role_revisions={'crude':CRUDE_REVISION},
                                expected_head=previous, request_id=str(uuid4())))
        native = daily_route(f)
        f.at = '2021-01-15T23:59:59.999999Z'
        f.effective = '2021-01-15T08:00:00Z'
        principal = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
        for role in ['gas_operator', 'gas_service_admin']:
            query(f"INSERT INTO security.role_grants SELECT '{uuid4()}','{principal}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE code='{role}';")
        # This disposable fault proves rollback after an earlier child command has
        # written. The production schema and PostgreSQL installation stay intact.
        query("CREATE FUNCTION trading.synthetic_delivery_batch_fault() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN IF NEW.external_reference='synthetic-delivery-rollback-second' THEN RAISE EXCEPTION 'gas_operations_input';END IF;RETURN NEW;END$$;"
              "CREATE TRIGGER synthetic_delivery_batch_fault BEFORE INSERT ON trading.gas_nomination_requests FOR EACH ROW EXECUTE FUNCTION trading.synthetic_delivery_batch_fault();")
        calendar = dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code='test_utc_every_day')
        root = Path(query('SHOW unix_socket_directories;')).resolve()
        report_path = root/'delivery-workbench-result.json'
        packet = dict(profile='trader', day='2021-01-15', effectiveAt='2021-01-15T08:00:00Z', bookId=f.book,
            parties=trade_booking(), physicalCalendar=side_calendar_policy(calendar,delivery=True,observation=False,payment=False),
            cashCalendar=side_calendar_policy(calendar,delivery=True,observation=False,payment=True), reportPath=str(report_path), **native)
        run_browser(query, manifest, args.dotnet.resolve(), args.browser.resolve(), children, packet,
                    'tests/browser/delivery-workbench.mjs', worker_enabled=False, timeout=600, api_bin=args.api_bin,
                    artifacts=['delivery-workbench-gas.png','delivery-workbench-crude.png','delivery-workbench-mobile.png','delivery-workbench-crude-native.png'])
        report = json.loads(report_path.read_text())
        assert query("SELECT count(*) FROM trading.gas_nomination_requests WHERE external_reference LIKE 'synthetic-delivery-rollback-%';") == '0', 'A failing second child left partial nominations'
        assert query(f"SELECT count(*) FROM trading.delivery_nomination_batches WHERE operation_id='{report['failedBatchId']}';") == '0', 'A failed batch retained a command receipt'
        assert query("SELECT count(*) FROM trading.gas_nomination_requests WHERE external_reference LIKE 'synthetic-delivery-success-%';") == '2', 'Exact batch retry duplicated native nominations'
        assert query(f"SELECT count(*) FROM trading.delivery_nomination_batches WHERE operation_id='{report['batchId']}';") == '1'
        assert query(f"SELECT count(*) FROM trading.delivery_route_allocations WHERE plan_id='{report['allocationId']}';") == '2'
        assert query('SELECT count(*) FROM trading.gas_transport_capacity_claims;') == '1', 'Allocations or nominations copied commercial capacity claims'
        def bound(sql, context=None):
            return f.call(f"SELECT security.use_market_context('{f.catalog}','{context or f.capture()}');"
                          f"SELECT security.use_trading_context('{f.catalog}','{f.at}');" + sql)
        def refuses(action, code):
            try:
                action()
            except RuntimeError as error:
                assert code in str(error), str(error)
            else:
                raise AssertionError('Expected ' + code)
        # A native caller cannot attach unrelated existing nominations to an
        # otherwise valid batch merely by supplying a correct managed digest.
        payload = json.loads(query(f"SELECT request::text FROM trading.delivery_nomination_batches WHERE operation_id='{report['batchId']}';"))
        nominations = query(f"SELECT nomination_id FROM trading.delivery_nomination_batch_members WHERE batch_id='{report['batchId']}' ORDER BY ordinal;").splitlines()
        assert query(f"SELECT actor_id FROM trading.delivery_nomination_batches WHERE operation_id='{report['batchId']}';") == principal
        assert f.call(f"SELECT security.gas_transport_route_owner('{f.catalog}');") == report['ownerId'], 'Raw probes must retain the original batch actor and workspace'
        original_ids = ','.join("'" + value + "'" for value in nominations)
        assert query(f"SELECT DISTINCT actor_id FROM trading.gas_operation_commands WHERE outcome->>'id' IN({original_ids});") == principal, 'Forged-frame refusal must use actor-owned native children'
        source_context = f.capture()
        def group_record(body, ids):
            return f"SELECT security.record_delivery_nomination_batch('{f.catalog}','{uuid4()}',{literal(body)},ARRAY[{ids}]::uuid[],'{source_context}');"
        mismatched = ','.join("'" + value + "'" for value in reversed(nominations))
        refuses(lambda: f.call(group_record(payload, mismatched)), 'delivery_nomination_batch_scope')
        forged = json.loads(json.dumps(payload))
        forged['input']['context']['snapshotId'] = str(uuid4())
        refuses(lambda: bound(group_record(payload, original_ids), source_context), 'historical_read_only')
        refuses(lambda: f.call(group_record(forged, original_ids)), 'delivery_nomination_batch_scope')
        before_correction = f.capture()
        read = lambda current, context=None: json.loads(bound(f"SELECT security.read_delivery_route_allocations('{f.catalog}','{report['allocationId']}',{str(current).lower()})::text;",context))
        accepted = read(True, before_correction)
        source = f.route_read(native['routeId'])['sourceCapacities'][0]
        f.replace_capacity(source, evidence='Synthetic corrected commercial capacity evidence')
        refuses(lambda: read(True), 'delivery_workbench_stale')
        assert read(True, before_correction)['allocations'] == accepted['allocations'], 'A later source correction rewrote the retained source assessment'
        refuses(lambda: f.call(group_record(payload, original_ids)), 'delivery_workbench_stale')
        assert read(False, before_correction)['allocations'] == accepted['allocations'], 'A later source correction rewrote retained receipt evidence'
        assert read(False)['allocations'] == accepted['allocations'], 'Source correction erased original receipt evidence'
        grants = query(f"SELECT string_agg(quote_literal(id::text),',') FROM security.role_grants WHERE principal_id='{principal}' AND NOT revoked;")
        try:
            query(f"UPDATE security.role_grants SET revoked=true WHERE id::text IN({grants});")
            refuses(lambda: f.call(f"SELECT security.read_delivery_route_allocations('{f.catalog}','{report['allocationId']}',false)::text;"), 'gas_transport_route_denied')
        finally:
            query(f"UPDATE security.role_grants SET revoked=false WHERE id::text IN({grants});")
        print('PASS delivery HTTP/UI: exact daily route allocations, native opposite-direction obligations, atomic manual nomination batch, retries, retained context, permissions and shared gas/crude presentation', flush=True)

    native_main(checks=checks, fixture_factory=fixture, arguments=['--pg-bin',str(args.pg_bin.resolve())], cleanup_ready=lambda:not children)


if __name__ == '__main__':
    main()
