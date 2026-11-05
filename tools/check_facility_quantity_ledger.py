"""Synthetic facility quantity storage, exercised only in an owned preview."""
from copy import deepcopy
from concurrent.futures import ThreadPoolExecutor
import json
from uuid import uuid4

from check_booked_side_programs import runtime_query
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'trader'})
    workspace = client.call('workspace')
    before = dict(snapshotId=initial_frame(workspace, '2020-12-18')['id'],
                  convention='physical_gas_fixed_quantity_v4', knowledgeMode='current')
    after = {**before, 'snapshotId': next(f['id'] for f in workspace['frames']
                                        if f['at'].startswith('2021-01-15T08:'))}
    catalog = client.call('gas/catalog', before)
    point = next(p for p in catalog['locations'] if p['deliveryPoint'] == 'synthetic_gas_point_a')
    terms = {**before, **trade_booking(), 'bookId': catalog['books'][0]['id'],
             'definitionRevisionId': point['definitionRevisionId'], 'gasSetupId': catalog['gasSetupId'],
             'discounting': False, 'profile': 'fixed_gas_composition_v1',
             'physicalSides': [dict(code='receipt', pipeline=point['pipeline'], zone=point['zone'],
                                    deliveryPoint=point['deliveryPoint'], product=point['product'],
                                    direction='receive', deliveryFrom='2021-01-01', deliveryTo='2021-01-02',
                                    quantityBasis='period_total_equal_days', quantity='1000', link=None)],
             'cashSides': [dict(code='payment', role='consideration', direction='pay', source='receipt',
                                stage='delivery', price='3', tariffCode=None, paymentAt='2021-02-02T00:00:00Z')]}
    targets, trades = [], []
    for _ in range(3):
        booked = client.call('gas/composition/book', {**deepcopy(terms), 'operationId': str(uuid4())})
        trades.append(booked)
        rows = client.call('gas/quantities/read', dict(context=after, tradeId=booked['tradeId'],
                                                     selectionPolicy='operational_projection_v1'))
        targets.append(rows['rows'][0]['obligationId'])
    catalog_id = manifest['catalogId']
    at = '2021-01-15T08:00:00Z'

    def call(sql, profile='trader', context=None):
        bind = '' if context is None else (
            f"SELECT security.use_market_context('{catalog_id}','{context}');"
            f"SELECT security.use_trading_context('{catalog_id}','{at}');")
        return runtime_query(query, manifest, bind + sql, profile=profile)

    def rejected(sql, code, profile='trader', context=None):
        try:
            call(sql, profile, context)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected ' + code)

    def command(body, operation=None, effective=at):
        payload = json.dumps(body).replace("'", "''")
        return (f"SELECT security.record_facility_quantity_command('{catalog_id}',"
                f"'{operation or uuid4()}','{payload}'::jsonb,'{effective}')::text;")

    # New capabilities are explicitly granted in this disposable fixture only.
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles "
          "CROSS JOIN unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities'])p WHERE code='trader';")
    client.bootstrap()
    create = dict(kind='create', obligationId=targets[0], day='2021-01-01', facility=point['deliveryPoint'])
    creation = command(create)
    ledger = json.loads(call(creation))['ledgerId']
    assert json.loads(call(creation))['ledgerId'] == ledger
    assert json.loads(call(command(create)))['ledgerId'] == ledger
    rejected(command({**create, 'facility': point['pipeline']}), 'facility_quantity_scope')

    def capture():
        return call(f"SELECT security.capture_date_context('{catalog_id}','2021-01-15',NULL);")

    def read(context=None):
        return json.loads(call(f"SELECT security.read_facility_quantity_ledger('{catalog_id}','{ledger}')::text;",
                               context=context or capture()))

    empty = read()
    assert empty['source'] is None and empty['plan'] is None and empty['allocations'] == []
    http_context = dict(effectiveOn='2021-01-15', knowledgeMode='current', retained=None,
                        convention='facility_business_date_end_utc_v1')
    http_view = client.call('facilities/quantities/read', dict(context=http_context, ledgerId=ledger))
    assert http_view['balance']['constraint'] == 'unassessed' and http_view['balance']['available'] is None
    directory = client.call('facilities/quantities/directory', dict(context=http_context))
    assert any(row['id'] == ledger for row in directory['ledgers'])
    # Repeat the maintenance autovacuum performed before a slow hosted directory read: it
    # vacuums locations, counting rows without statistics, and analyzes units and
    # quantity specifications. The directory and its target choices must still check each
    # ledger's and each target's visibility once, not for every combination of joined rows.
    query('VACUUM ref_data.locations;ANALYZE ref_data.units,ref_data.quantity_specifications;')

    def pages(statement):
        plan = json.loads(call(f'EXPLAIN(ANALYZE,BUFFERS,FORMAT JSON) {statement}', context=capture()))[0]['Plan']
        return plan['Shared Hit Blocks'] + plan['Shared Read Blocks']
    reads = dict(directory=pages(f"SELECT security.facility_quantity_directory('{catalog_id}');"),
                 choices=pages(f"SELECT security.facility_quantity_choices('{catalog_id}',NULL,'{at}');"))
    assert reads['directory'] < 20000 and reads['choices'] < 100000, reads
    source_body = dict(kind='source', ledgerId=ledger, expectedPreviousId=None, quantity='100',
                       evidence='Synthetic facility statement')
    source_operation = str(uuid4())
    source_command = command(source_body, source_operation)
    source = json.loads(call(source_command))
    source_id = source['revisionId']
    assert json.loads(call(source_command)) == source
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles "
          "CROSS JOIN unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities'])p WHERE code='risk_analyst';")
    try:
        assert json.loads(call(source_command, profile='risk'))['revisionId'] == source_id
    finally:
        query("DELETE FROM security.role_permissions WHERE permission IN('ReadFacilityQuantities','RecordFacilityQuantities') "
              "AND role_id=(SELECT id FROM security.roles WHERE code='risk_analyst');")
    assert json.loads(call(command(source_body)))['revisionId'] == source_id
    rejected(command({**source_body, 'quantity': '101'}, source_operation), 'facility_quantity_conflict')
    assert read()['source']['quantity'] == '100'
    frozen = capture()

    def allocation(amounts, previous=None, selected_source=source_id):
        return dict(kind='allocate', ledgerId=ledger, expectedPreviousId=previous,
                    sourceRevisionId=selected_source, evidence='Synthetic allocation statement',
                    allocations=[dict(obligationId=t, tradeRevision=1, quantity=q) for t, q in zip(targets, amounts)])

    plan_body = allocation(['60', '25', '10'])
    plan_command = command(plan_body)
    plan = json.loads(call(plan_command))
    assert json.loads(call(plan_command)) == plan
    assert json.loads(call(command({**plan_body, 'allocations': list(reversed(plan_body['allocations']))})))['revisionId'] == plan['revisionId']
    decimal_equivalent = {**plan_body, 'allocations': [{**r, 'quantity': r['quantity'] + '.0'} for r in plan_body['allocations']]}
    assert json.loads(call(command(decimal_equivalent)))['revisionId'] == plan['revisionId']
    view = read()
    assert sorted(r['quantity'] for r in view['allocations']) == ['10', '25', '60']
    assert view['requiresReconciliation'] is False
    assert read(frozen)['plan'] is None
    rejected(command(allocation(['70', '25', '10'], plan['revisionId'])), 'facility_quantity_overallocated')
    rejected(command(allocation(['50', '25', '10'])), 'facility_quantity_conflict')
    rejected(command({**plan_body, 'allocations': plan_body['allocations'] * 2}), 'facility_quantity_input')
    rejected(command({**source_body, 'quantity': '-1', 'expectedPreviousId': source_id}), 'facility_quantity_input')
    rejected(command(source_body, effective='2021-01-01T00:00:00Z'), 'facility_quantity_interval')

    # A correction is a new fact, not a new source pool or silent reallocation.
    corrected_body = {**source_body, 'quantity': '90', 'expectedPreviousId': source_id}
    corrected = json.loads(call(command(corrected_body, effective='2021-01-15T09:00:00Z')))
    assert corrected['revisionId'] != source_id
    # Reads use an explicit business instant as well as a knowledge cutoff.
    at = '2021-01-15T10:00:00Z'
    corrected_view = read()
    assert corrected_view['source']['quantity'] == '90'
    assert corrected_view['plan']['id'] == plan['revisionId'] and corrected_view['requiresReconciliation']
    assert read(frozen)['source']['quantity'] == '100'
    rejected(command(allocation(['60', '25', '10'], plan['revisionId']),
                     effective='2021-01-15T10:00:00Z'), 'facility_quantity_conflict')
    replacement = allocation(['55', '25', '10'], plan['revisionId'], corrected['revisionId'])
    replaced = json.loads(call(command(replacement, effective='2021-01-15T10:00:00Z')))
    assert replaced['revisionId'] != plan['revisionId'] and read()['requiresReconciliation'] is False
    assert json.loads(call(source_command)) == source and json.loads(call(plan_command)) == plan

    attempts = [command(allocation([amount, '25', '10'], replaced['revisionId'], corrected['revisionId']),
                        effective='2021-01-15T11:00:00Z') for amount in ['50', '49']]

    def race(sql):
        try:
            return json.loads(call(sql))
        except RuntimeError as error:
            assert 'facility_quantity_conflict' in str(error), str(error)
            return None

    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(race, attempts))
    assert sum(result is not None for result in outcomes) == 1
    at = '2021-01-15T12:00:00Z'
    winner = next(result for result in outcomes if result is not None)
    assert read()['plan']['id'] == winner['revisionId']

    larger = json.loads(call(command({**source_body, 'quantity': '3000', 'expectedPreviousId': corrected['revisionId']},
                                     effective='2021-01-15T13:00:00Z')))
    wrong_day_terms = deepcopy(terms)
    wrong_day_terms['physicalSides'][0].update(deliveryFrom='2021-01-02', deliveryTo='2021-01-03')
    wrong_day = client.call('gas/composition/book', {**wrong_day_terms, 'operationId': str(uuid4())})
    wrong_day_target = client.call('gas/quantities/read', dict(context=after, tradeId=wrong_day['tradeId'],
                                                             selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    at = '2021-01-15T14:00:00Z'
    rejected(command(allocation(['1001'], winner['revisionId'], larger['revisionId']), effective=at), 'facility_quantity_target')
    rejected(command({**allocation(['10'], winner['revisionId'], larger['revisionId']),
                      'allocations': [dict(obligationId=wrong_day_target, tradeRevision=1, quantity='10')]}, effective=at), 'facility_quantity_target')

    # A deliberately incomplete definer writer bypasses the command checks.
    # Deferred constraints must still refuse bad source/target/native-cell claims.
    query("""CREATE FUNCTION security.synthetic_invalid_facility_plan(cat uuid,ledger uuid,source uuid,target uuid,amount numeric)
      RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $test$
      DECLARE publication integer;prior trading.facility_quantity_plans;identity uuid=gen_random_uuid();o trading.gas_delivery_obligations;r trading.gas_trade_revisions;
      BEGIN
       PERFORM pg_advisory_xact_lock(730849102);
       SELECT * INTO STRICT o FROM trading.gas_delivery_obligations WHERE(catalog_id,id)=(cat,target);
       SELECT * INTO STRICT r FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(cat,o.trade_id) ORDER BY revision DESC LIMIT 1;
       SELECT * INTO STRICT prior FROM trading.facility_quantity_plans WHERE(catalog_id,ledger_id)=(cat,ledger) ORDER BY revision DESC LIMIT 1;
       SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=cat;
       INSERT INTO trading.publications VALUES(cat,publication,pg_current_xact_id());
       INSERT INTO trading.facility_gas_targets VALUES(cat,ledger,target,'2021-01-01') ON CONFLICT DO NOTHING;
       INSERT INTO trading.facility_quantity_plans VALUES(cat,ledger,identity,prior.revision+1,prior.id,source,'Synthetic invalid writer',publication,'2021-01-15T14:00:00Z',
        (SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user));
       INSERT INTO trading.facility_gas_allocations VALUES(cat,ledger,identity,target,o.trade_id,r.revision,r.instrument_id,amount);
       SET CONSTRAINTS trading.valid_facility_quantity_plan,trading.valid_facility_quantity_allocation IMMEDIATE;
      END$test$;
      ALTER FUNCTION security.synthetic_invalid_facility_plan(uuid,uuid,uuid,uuid,numeric) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_invalid_facility_plan(uuid,uuid,uuid,uuid,numeric) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_invalid_facility_plan(uuid,uuid,uuid,uuid,numeric) TO openetrm_runtime;""")
    def bypass(target, amount, source=larger['revisionId']):
        return f"SELECT security.synthetic_invalid_facility_plan('{catalog_id}','{ledger}','{source}','{target}',{amount});"
    rejected(bypass(targets[0], '3001'), 'facility_quantity_overallocated')
    rejected(bypass(targets[0], '1001'), 'facility_quantity_target')
    rejected(bypass(wrong_day_target, '10'), 'facility_quantity_target')
    rejected(bypass(targets[0], '10', source_id), 'facility_quantity_conflict')
    # A plan cannot point forward across the knowledge publication boundary,
    # even when a future writer inserts both facts in the same transaction.
    query("""CREATE FUNCTION security.synthetic_future_facility_source(cat uuid,ledger uuid,target uuid) RETURNS void
      LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $test$
      DECLARE source trading.facility_quantity_revisions;plan trading.facility_quantity_plans;r trading.gas_trade_revisions;
       publication integer;actor uuid;new_source uuid=gen_random_uuid();new_plan uuid=gen_random_uuid();BEGIN
       PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;PERFORM pg_advisory_xact_lock(730849102);
       SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
       SELECT * INTO STRICT source FROM trading.facility_quantity_revisions WHERE(catalog_id,ledger_id)=(cat,ledger) ORDER BY revision DESC LIMIT 1;
       SELECT * INTO STRICT plan FROM trading.facility_quantity_plans WHERE(catalog_id,ledger_id)=(cat,ledger) ORDER BY revision DESC LIMIT 1;
       SELECT h.* INTO STRICT r FROM trading.gas_delivery_obligations o JOIN trading.gas_trade_revisions h ON(h.catalog_id,h.trade_id)=(o.catalog_id,o.trade_id) WHERE(o.catalog_id,o.id)=(cat,target);
       SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id=cat;
       INSERT INTO trading.publications VALUES(cat,publication,pg_current_xact_id()),(cat,publication+1,pg_current_xact_id());
       INSERT INTO trading.facility_quantity_revisions VALUES(cat,ledger,new_source,source.revision+1,source.id,3300,'Synthetic later publication',publication+1,'2021-01-15T14:00:00Z',actor);
       INSERT INTO trading.facility_quantity_plans VALUES(cat,ledger,new_plan,plan.revision+1,plan.id,new_source,'Synthetic earlier plan',publication,'2021-01-15T14:00:00Z',actor);
       INSERT INTO trading.facility_gas_allocations VALUES(cat,ledger,new_plan,target,r.trade_id,r.revision,r.instrument_id,10);
       SET CONSTRAINTS trading.valid_facility_quantity_revision,trading.valid_facility_quantity_plan,trading.valid_facility_quantity_allocation IMMEDIATE;
      END$test$;
      ALTER FUNCTION security.synthetic_future_facility_source(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_future_facility_source(uuid,uuid,uuid) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_future_facility_source(uuid,uuid,uuid) TO openetrm_runtime;""")
    rejected(f"SELECT security.synthetic_future_facility_source('{catalog_id}','{ledger}','{targets[0]}');", 'facility_quantity_interval')

    # Clock/metadata revisions must not create another capacity pool. This
    # bypasses create's checks to exercise the independent logical-day key.
    try:
        query(f"""BEGIN;SELECT pg_advisory_xact_lock(730849102);
          INSERT INTO trading.facility_quantity_ledgers
          SELECT catalog_id,'{uuid4()}',reference_catalog_id,reference_revision_id,facility_code,flow_point_code,commodity_code,quantity_code,
           legal_entity_id,legal_entity_revision,direction,native_from+interval '1 hour',native_to+interval '1 hour',publication_order,effective_at,actor_id
          FROM trading.facility_quantity_ledgers WHERE(catalog_id,id)=('{catalog_id}','{ledger}');
          INSERT INTO trading.facility_gas_delivery_scopes
          SELECT g.catalog_id,h.id,g.reference_catalog_id,g.reference_revision_id,g.product_code,g.pipeline_code,g.zone_code,g.point_code,g.legal_entity_id,g.direction,g.day,g.origin_obligation_id
          FROM trading.facility_gas_delivery_scopes g JOIN trading.facility_quantity_ledgers h ON(h.catalog_id,h.native_from)=(g.catalog_id,
           (SELECT native_from+interval '1 hour' FROM trading.facility_quantity_ledgers WHERE(catalog_id,id)=('{catalog_id}','{ledger}')))
          WHERE(g.catalog_id,g.ledger_id)=('{catalog_id}','{ledger}');COMMIT;""")
    except RuntimeError as error:
        assert 'duplicate key value' in str(error), str(error)
    else:
        raise AssertionError('A changed clock grid created another logical source pool')

    # Inject a target-specific authority loss at the existing business boundary.
    # The real guard still runs; this isolates union/replay scope independently
    # of today's catalog-wide role granularity, and is restored before returning.
    blocked_trade = query(f"SELECT trade_id FROM trading.gas_delivery_obligations WHERE(catalog_id,id)=('{catalog_id}','{targets[1]}');")
    query(f"""ALTER FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean) RENAME TO synthetic_original_gas_quantity_guard;
      CREATE FUNCTION security.require_gas_quantity_trade(catalog uuid,trade uuid,writing boolean) RETURNS void
      LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $test$ BEGIN
       PERFORM security.synthetic_original_gas_quantity_guard(catalog,trade,writing);
       IF trade='{blocked_trade}'::uuid THEN RAISE EXCEPTION 'synthetic_gas_scope_denied' USING ERRCODE='42501';END IF;
      END$test$;
      ALTER FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean) FROM PUBLIC;""")
    try:
        assert json.loads(call(creation))['ledgerId'] == ledger
        assert json.loads(call(source_command)) == source
        rejected(plan_command, 'synthetic_gas_scope_denied')
        rejected(command(allocation([], winner['revisionId'], larger['revisionId']), effective=at), 'synthetic_gas_scope_denied')
    finally:
        query("DROP FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean);"
              "ALTER FUNCTION security.synthetic_original_gas_quantity_guard(uuid,uuid,boolean) RENAME TO require_gas_quantity_trade;")

    # Current scope is checked before a caller can probe hidden source capacity.
    rejected(command(replacement), 'facility_quantity_denied', profile='viewer')
    rejected(f"SELECT security.read_facility_quantity_ledger('{catalog_id}','{ledger}');",
             'facility_quantity_denied', profile='viewer')
    rejected(command(replacement), 'historical_read_only', context=capture())
    rejected("UPDATE trading.facility_quantity_revisions SET quantity=1;", 'permission denied')
    # Revocation also closes successful command retries.
    query("DELETE FROM security.role_permissions WHERE permission='RecordFacilityQuantities' "
          "AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    rejected(source_command, 'facility_quantity_denied')
    query("INSERT INTO security.role_permissions SELECT id,'RecordFacilityQuantities' FROM security.roles WHERE code='trader';")
    query("DELETE FROM security.role_permissions WHERE permission='ReadMarketData' AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    rejected(f"SELECT security.read_facility_quantity_ledger('{catalog_id}','{ledger}');", 'facility_quantity_denied')
    query("INSERT INTO security.role_permissions SELECT id,'ReadMarketData' FROM security.roles WHERE code='trader';")
    assert query(f"SELECT count(*) FROM trading.facility_quantity_revisions WHERE catalog_id='{catalog_id}';") == '3'
    assert query(f"SELECT count(*) FROM trading.facility_quantity_plans WHERE catalog_id='{catalog_id}';") == '3'
    query("INSERT INTO security.role_permissions SELECT id,'ConfigureFacilityLimits' FROM security.roles WHERE code='trader';")
    client.bootstrap()

    def http_save(body, instant, operation=None, expected=200):
        return client.call('facilities/quantities/record', dict(context=http_context, command=body,
                           effectiveAt=instant, ownerId=directory['ownerId'], operationId=operation or str(uuid4())), expected=expected)

    limit_body = dict(kind='limit', ledgerId=ledger, expectedPreviousId=None, quantity='90',
                      evidence='Synthetic native-period maximum')
    limit = http_save(limit_body, '2021-01-15T16:00:00Z')['outcome']
    reported_body = {**source_body, 'quantity': '100', 'evidence': 'Synthetic corrected statement',
                     'expectedPreviousId': larger['revisionId']}
    operation = str(uuid4())
    reported = http_save(reported_body, '2021-01-15T16:01:00Z', operation)
    assert client.call('facilities/quantities/status', dict(operationId=operation, ownerId=directory['ownerId'])) == reported
    assert http_save(reported_body, '2021-01-15T16:01:00Z', operation) == reported
    assert http_save({**reported_body, 'quantity': '101'}, '2021-01-15T16:01:00Z', operation, 409)['code'] == 'facility_quantity_conflict'
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN "
          "unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities'])p WHERE code='risk_analyst';")
    client.call('profile', {'profile': 'risk'})
    assert http_save(reported_body, '2021-01-15T16:01:00Z', operation, 409)['code'] == 'facility_quantity_owner_changed'
    assert client.call('facilities/quantities/status', dict(operationId=operation, ownerId=directory['ownerId']), expected=409)['code'] == 'facility_quantity_owner_changed'
    assert query(f"SELECT count(*) FROM trading.facility_quantity_commands WHERE operation_id='{operation}';") == '1'
    client.call('profile', {'profile': 'trader'})
    query("DELETE FROM security.role_permissions WHERE permission IN('ReadFacilityQuantities','RecordFacilityQuantities') "
          "AND role_id=(SELECT id FROM security.roles WHERE code='risk_analyst');")
    api_plan = allocation(['60', '25', '10'], winner['revisionId'], reported['outcome']['revisionId'])
    preview_request = dict(ownerId=directory['ownerId'], context=http_context, ledgerId=ledger, sourceRevisionId=api_plan['sourceRevisionId'],
                           expectedPreviousId=winner['revisionId'], allocations=api_plan['allocations'])
    preview = client.call('facilities/quantities/preview', preview_request)
    assert preview['balance']['allocated'] == '95' and preview['balance']['available'] == '5'
    assert preview['balance']['constraint'] == 'breached' and preview['balance']['excess'] == '10'
    assert client.call('facilities/quantities/preview', {**preview_request, 'sourceRevisionId': larger['revisionId']}, expected=409)['code'] == 'facility_quantity_conflict'
    saved_plan = http_save(api_plan, '2021-01-15T16:02:00Z')['outcome']
    withdrawal = http_save({**limit_body, 'expectedPreviousId': limit['revisionId'], 'quantity': None,
                            'evidence': 'Synthetic withdrawal'}, '2021-01-15T16:03:00Z')['outcome']
    unassessed = client.call('facilities/quantities/read', dict(context=http_context, ledgerId=ledger))
    assert unassessed['balance']['constraint'] == 'unassessed' and unassessed['balance']['excess'] is None
    assert unassessed['facts']['limit']['quantity'] is None
    http_save({**limit_body, 'expectedPreviousId': withdrawal['revisionId'], 'evidence': 'Synthetic reinstatement'},
              '2021-01-15T16:04:00Z')
    final_view = client.call('facilities/quantities/read', dict(context=http_context, ledgerId=ledger))
    assert final_view['facts']['plan']['id'] == saved_plan['revisionId']
    assert final_view['balance']['constraint'] == 'breached' and not final_view['facts']['requiresReconciliation']
    assert len(final_view['facts']['limitHistory']) == 3
    assert client.call('facilities/quantities/read', dict(context={**http_context, 'convention': ''}, ledgerId=ledger), expected=422)['code'] == 'facility_quantity_input'
    # A trade reader sees only its own allocation, without facility totals or
    # global plan/source revision identifiers, even without facility permission.
    query("DELETE FROM security.role_permissions WHERE permission IN('ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits') "
          "AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    client.bootstrap()
    at = '2021-01-15T17:00:00Z'
    own_sql = f"SELECT security.read_trade_facility_allocations('{catalog_id}','{trades[0]['tradeId']}')::text;"
    own = json.loads(call(own_sql, context=capture()))
    assert len(own) == 1 and own[0]['quantity'] == '60' and own[0]['sideCode'] == 'receipt'
    assert own[0]['finalQuantity'] is None and own[0]['finalState']=='missing'
    assert set(own[0]) == {'obligationId', 'sideCode', 'day', 'quantity', 'unit', 'state', 'finalQuantity', 'finalState'}
    assert json.loads(call(own_sql, context=frozen)) == []
    inspection = {**before, 'snapshotId': next(f['id'] for f in workspace['frames'] if f['at'].startswith('2021-01-31T08:')),
                  'convention': 'trading_workspace_v1'}
    offset, fact = 0, None
    while fact is None:
        positions = client.call('trading/positions', dict(context=inspection, bookIds=[], portfolioIds=[], offset=offset, limit=64))['positions']
        assert positions, 'The booked facility target must be discoverable without valuation'
        fact = next((p for p in positions if p['tradeId'] == trades[0]['tradeId']), None)
        offset += 64
    terms_view = client.call('trading/gas', dict(context=inspection, tradeId=fact['tradeId'], bindingId=fact['bindingId'],
                                               revision=fact['revision'], instrumentType='physical_gas'))
    assert terms_view['facilityAllocations'] == own
    client.call('facilities/quantities/read', dict(context=http_context, ledgerId=ledger), expected=403)
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN "
          "unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits'])p WHERE code='trader';")
    client.bootstrap()
    another = http_save(allocation(['60', '24', '10'], saved_plan['revisionId'], reported['outcome']['revisionId']),
                        '2021-01-15T16:05:00Z')['outcome']
    assert json.loads(call(own_sql, context=capture())) == own
    removed_plan = allocation(['60', '24', '10'], another['revisionId'], reported['outcome']['revisionId'])
    removed_plan['allocations'] = removed_plan['allocations'][1:]
    removed = http_save(removed_plan, '2021-01-15T16:06:00Z')['outcome']
    removed_view = json.loads(call(own_sql, context=capture()))
    assert removed_view[0]['quantity'] is None and removed_view[0]['state'] == 'no_current_allocation'
    http_save(allocation(['60', '25', '10'], removed['revisionId'], reported['outcome']['revisionId']),
              '2021-01-15T16:07:00Z')
    print('PASS facility quantity ledger: exact references, shared source/three trades, immutable corrections, '
          'conservation, historical reads, retained retries, no-op facts and denied capacity probes', flush=True)
    return dict(ledgerId=ledger, trades=trades)
