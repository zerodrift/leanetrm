"""Physical gas ticket checks against an owned synthetic preview."""
from uuid import uuid4
from decimal import Decimal
from preview_client import PreviewClient, initial_frame
from preview_fixture import gas_purchase, held_gas_measures


def run_checks(url,query=None,manifest=None,*,extra_locations=0):
    """A gas purchase of one physical side, its payment and a usage fee: the trade a single-point booking is, entered as its composition."""
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    workspace=c.call('workspace'); frame = initial_frame(workspace,'2020-12-18')
    context = {'snapshotId':frame['id'], 'convention':'physical_gas_fixed_quantity_v4', 'knowledgeMode':'current'}
    catalog = c.call('gas/catalog', context)
    assignment = next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    assert assignment['valuation']['group']=='synthetic_gas_group_a', 'Location must disclose its shared valuation group'
    assert assignment['clock']=='test_utc' and 'clock' not in assignment['valuation']
    assert len(catalog['locations'])==5+extra_locations, 'Each physical path appears once, independently of six forward months'
    body = gas_purchase(context, catalog)
    preview = lambda b, expected=200: c.call('gas/composition/preview', b, expected=expected)
    pv = lambda b: Decimal(preview(b)['presentValue'])
    gas = lambda b, **terms: {**b, 'physicalSides': [{**b['physicalSides'][0], **terms}]}
    cash = lambda b, n, **terms: {**b, 'cashSides': [{**s, **terms} if k == n else s for k, s in enumerate(b['cashSides'])]}
    paid = lambda b, at: {**b, 'cashSides': [{**s, 'paymentAt': at} for s in b['cashSides']]}
    at = lambda point: {k: v for k, v in next(p for p in catalog['locations'] if p['deliveryPoint']==point).items() if k in ('pipeline', 'zone', 'deliveryPoint')}
    value = preview(body)
    assert value['sides'][0]['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-JAN'
    assert Decimal(value['presentValue'])==100 and [Decimal(s['presentValue']) for s in value['sides']]==[3200,-3000,-100], value['sides']
    assert len(value['sides'][0]['deliveryRows'])==31 and value['sides'][0]['deliveryRows'][0]['quantity']=={'numerator':'1000','denominator':'31'}
    assert pv(cash(body, 1, price=None, tariffCode='synthetic_gas_usage_a'))==80
    preview(cash(body, 1, tariffCode='synthetic_gas_usage_a'), expected=422)
    february = paid(gas(body, deliveryFrom='2021-02-01', deliveryTo='2021-03-01'), '2021-03-01T00:00:00Z')
    preview(cash(february, 1, price=None, tariffCode='synthetic_gas_usage_a'), expected=422)
    february = preview(february)
    assert Decimal(february['presentValue'])==165 and february['sides'][0]['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-FEB'
    same_zone = next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_d')
    assert same_zone['zone']==assignment['zone'] and same_zone['valuation']['group']==assignment['valuation']['group']
    assert pv(gas(body, deliveryPoint=same_zone['deliveryPoint']))==50
    assert pv(gas(body, **at('synthetic_gas_point_b')))==0
    preview(gas(body, **{**at('synthetic_gas_point_b'), 'zone': assignment['zone']}), expected=422)
    third = next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_c')
    assert third['valuation']['curve']==assignment['valuation']['curve'] and third['valuation']['group']!=assignment['valuation']['group']
    different_day = preview(gas(body, **at('synthetic_gas_point_c')))
    assert different_day['sides'][0]['deliveryRows'][0]['start'].startswith('2021-01-01T01:00:00') and Decimal(different_day['presentValue'])==-100
    preview(cash(gas(body, **at('synthetic_gas_point_c')), 1, price=None, tariffCode='synthetic_gas_usage_a'), expected=422)
    preview(gas(body, **at('synthetic_gas_point_unpriced')), expected=422)
    sale = cash(gas(body, direction='deliver'), 0, direction='receive')
    assert pv(sale)==-300 and pv(cash(sale, 1, direction='receive'))==-100
    without_fee = preview(gas_purchase(context, catalog, fee=None))
    assert len(without_fee['sides'])==2 and Decimal(without_fee['presentValue'])==200
    assert pv(gas(body, quantityBasis='per_operating_day', quantity='10'))==31
    for change in [{'direction':None}, {'quantityBasis':'per_elapsed_hour'}, {'deliveryPoint':'unknown'}]:
        preview(gas(body, **change), expected=422)
    preview({**body, 'discounting': True}, expected=422)
    preview(paid(body, '2020-12-31T00:00:00Z'), expected=422)
    booked = c.call('gas/composition/book', body)
    assert c.call('gas/composition/book', body)==booked
    if query is not None:
        check_completion_admission(query,manifest,booked)

    retained = c.call('gas/composition/editor', {'context':context, 'tradeId':booked['tradeId']})['valuation']
    retained = held_gas_measures(retained, value)
    assert retained=={k:value[k] for k in retained}, (retained, value)
    c.call('gas/composition/book', gas(body, quantity='1001'), expected=409)
    before_booking = {**context, 'knowledgeMode':'retained', 'contextId':catalog['context']['contextId']}
    c.call('gas/composition/editor', {'context':before_booking, 'tradeId':booked['tradeId']}, expected=422)
    shared = {**context, 'convention':'trading_workspace_v1'}
    view = {'mode':'trades', 'bookIds':[body['bookId']], 'portfolioIds':[], 'groupBy':[], 'measures':[], 'pivotConvention':None}
    mixed = c.call('trading/blotter', {'context':shared, 'view':view})
    held = next(h for h in mixed['holdings'] if h['tradeId']==booked['tradeId'])
    assert held['instrumentType']=='physical_gas' and Decimal(held['presentValue'])==100 and held['bindingId']==booked['instrumentId']
    workspace = c.call('trading/workspace', {'context':shared, 'view':view, 'bookingCatalog':'directory', 'summaries':[]})
    assert any(h['tradeId']==booked['tradeId'] for h in workspace['blotter']['holdings'])
    c.call('profile', {'profile':'risk'})
    refused = {**body, 'operationId':str(uuid4())}
    c.call('gas/composition/book', refused, expected=403)
    assert c.call('gas/composition/status', {'operationId':refused['operationId']})=={'found':False}
    print('PASS physical gas: PV100 purchase oracle, points, gas days, tariffs, directions, retained booking, idempotency, temporal scope and authority',flush=True)


def run_shape_checks(query, manifest, *, extra_locations=0):
    """Alter only owned disposable rows inside rolled-back subtransactions."""
    catalog=manifest['catalogId']
    from check_booked_side_programs import runtime_query
    assert query("SELECT has_function_privilege('openetrm_risk_worker','security.register_read_source(uuid,uuid,date,timestamptz,integer,text,integer)','EXECUTE') AND to_regprocedure('security.capture_pre_gas_setup_context(uuid,date,timestamptz)') IS NULL;")=='t'
    # Single-point gas trades are no longer booked: the runtime cannot call their retired booking entry, while their status stays readable.
    assert query("SELECT has_function_privilege('openetrm_runtime','security.book_physical_gas(uuid,jsonb,timestamptz)','EXECUTE'),has_function_privilege('openetrm_runtime','security.gas_operation_status(uuid,uuid)','EXECUTE');")=='f|t'
    query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;
      DO $test$ BEGIN BEGIN
        PERFORM security.resolve_operation_context('{catalog}','2020-12-18',NULL);
        RAISE EXCEPTION 'unclaimed_worker_context_accepted';
      EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'risk_job_denied' THEN RAISE;END IF;END;END $test$;
      ROLLBACK;""")
    print('PASS gas setup temporal wrapper: worker entry ACL and original lease/pin guard preserved',flush=True)
    reference=manifest['referenceCatalogId']; revision=manifest['referenceRevisionId']
    query(f"""BEGIN;
      ALTER TABLE ref_data.gas_meter_assignments DISABLE TRIGGER guard_definition;
      ALTER TABLE market_data.gas_valuation_assignments DISABLE TRIGGER guard_definition;
      DO $test$ BEGIN
        BEGIN
          INSERT INTO ref_data.gas_meter_assignments VALUES('{reference}','{revision}','synthetic_gas_meter_overlap','test_pipeline_a','synthetic_gas_zone_a','synthetic_gas_point_a','test_gas','SYN-DUPLICATE','test_gas_quantity','2021-01-15','2021-02-15');
          RAISE EXCEPTION 'gas_overlap_accepted';
        EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_meter_overlap' THEN RAISE;END IF;END;
        BEGIN
          INSERT INTO ref_data.gas_meter_assignments VALUES('{reference}','{revision}','synthetic_gas_meter_duplicate','test_pipeline_a','synthetic_gas_zone_a','synthetic_gas_point_d','test_gas','SYN-METER-A-JAN','test_gas_quantity','2021-01-15','2021-02-01');
          RAISE EXCEPTION 'gas_duplicate_meter_accepted';
        EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_meter_overlap' THEN RAISE;END IF;END;
        BEGIN
          INSERT INTO ref_data.gas_meter_assignments VALUES('{reference}','{revision}','synthetic_gas_meter_wrong_unit','test_pipeline_a','synthetic_gas_zone_a','synthetic_gas_point_d','test_gas','SYN-WRONG-UNIT','test_power_quantity','2021-01-01','2021-02-01');
          RAISE EXCEPTION 'gas_meter_unit_accepted';
        EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_meter_unit' THEN RAISE;END IF;END;
        BEGIN
          INSERT INTO market_data.gas_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,curve_code,group_code,group_adjustment,location_adjustment)
          SELECT catalog_id,revision_id,'synthetic_gas_group_mismatch',reference_catalog_id,reference_revision_id,pipeline_code,zone_code,'synthetic_gas_point_unpriced',product_code,curve_code,group_code,999,0 FROM market_data.gas_valuation_assignments WHERE(catalog_id,code)=('{catalog}','synthetic_gas_value_a');
          RAISE EXCEPTION 'gas_group_mismatch_accepted';
        EXCEPTION WHEN foreign_key_violation THEN NULL;END;
      END $test$;ROLLBACK;""")
    query(f"""BEGIN ISOLATION LEVEL REPEATABLE READ;
      ALTER TABLE ref_data.gas_meter_assignments DISABLE TRIGGER guard_definition;
      DO $test$ BEGIN BEGIN
        INSERT INTO ref_data.gas_meter_assignments VALUES('{reference}','{revision}','synthetic_gas_meter_stale_snapshot','test_pipeline_a','synthetic_gas_zone_a','synthetic_gas_point_d','test_gas','SYN-STALE-SNAPSHOT','test_gas_quantity','2021-01-01','2021-02-01');
        RAISE EXCEPTION 'gas_stale_meter_snapshot_accepted';
      EXCEPTION WHEN SQLSTATE '25000' THEN IF SQLERRM<>'gas_reference_isolation' THEN RAISE;END IF;END;END $test$;
      ROLLBACK;""")
    print('PASS gas reference integrity: meter overlap, external-identifier collisions, incompatible units and inconsistent group adjustments refuse',flush=True)
    query("""BEGIN;
    CREATE POLICY gas_test_wide ON trading.gas_instruments FOR SELECT TO openetrm_runtime USING(true);
    CREATE POLICY gas_test_wide ON trading.gas_trade_revisions FOR SELECT TO openetrm_runtime USING(true);
    CREATE POLICY gas_test_wide ON market_data.gas_valuation_assignments FOR SELECT TO openetrm_runtime USING(true);
    CREATE POLICY gas_test_wide ON market_data.gas_valuation_groups FOR SELECT TO openetrm_runtime USING(true);
    CREATE POLICY gas_test_wide ON market_data.gas_usage_tariffs FOR SELECT TO openetrm_runtime USING(true);
    CREATE POLICY gas_test_wide ON ref_data.gas_meter_assignments FOR SELECT TO openetrm_runtime USING(true);
    SET LOCAL ROLE openetrm_runtime;
    DO $test$ BEGIN
      IF EXISTS(SELECT 1 FROM trading.gas_instruments) OR EXISTS(SELECT 1 FROM trading.gas_trade_revisions)
       OR EXISTS(SELECT 1 FROM market_data.gas_valuation_assignments) OR EXISTS(SELECT 1 FROM market_data.gas_valuation_groups) OR EXISTS(SELECT 1 FROM market_data.gas_usage_tariffs) OR EXISTS(SELECT 1 FROM ref_data.gas_meter_assignments) THEN RAISE EXCEPTION 'gas_scope_bypassed';END IF;
    END $test$;ROLLBACK;""")
    print('PASS physical gas: additional permissive policies cannot bypass current authority',flush=True)

    # A display-only reference revision must preserve and prove the physical hierarchy.
    reference=manifest['referenceCatalogId'];source=manifest['referenceRevisionId'];draft=str(uuid4())
    runtime_query(query,manifest,f"SELECT security.save_reference_display_edit('{reference}','{source}','{source}','{draft}','locations','synthetic_gas_point_a','{{\"name\":\"Renamed synthetic delivery point\"}}'::jsonb);",profile='editor')
    assert query(f"SELECT (SELECT count(*) FROM ref_data.pipeline_zones WHERE(catalog_id,revision_id)=('{reference}','{draft}')),(SELECT count(*) FROM ref_data.pipeline_delivery_points WHERE(catalog_id,revision_id)=('{reference}','{draft}')),(SELECT count(*) FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id)=('{reference}','{draft}'));")==f'3|{6+extra_locations}|{5+extra_locations}', 'Display edits must preserve PZL relationships'
    assert query(f"SELECT count(*) FROM ref_data.gas_meter_assignments WHERE(catalog_id,revision_id)=('{reference}','{draft}');")=='4', 'Display edits must retain dated meter bindings'
    # Inject corruption only in the owned disposable draft; normal reference rows cannot be changed.
    query(f"BEGIN; ALTER TABLE ref_data.gas_delivery_conventions DISABLE TRIGGER guard_definition; ALTER TABLE ref_data.pipeline_delivery_points DISABLE TRIGGER guard_definition; DELETE FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,point_code)=('{reference}','{draft}','synthetic_gas_point_unpriced'); DELETE FROM ref_data.pipeline_delivery_points WHERE(catalog_id,revision_id,point_code)=('{reference}','{draft}','synthetic_gas_point_unpriced'); ALTER TABLE ref_data.pipeline_delivery_points ENABLE TRIGGER guard_definition; ALTER TABLE ref_data.gas_delivery_conventions ENABLE TRIGGER guard_definition; COMMIT;")
    apply=str(uuid4())
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
      PERFORM security.apply_reference_display('{reference}','{source}','{draft}','{apply}',0);
      RAISE EXCEPTION 'physical_hierarchy_change_accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'reference_economic_change' THEN RAISE;END IF;END;
    END $test$;""",profile='approver')
    query(f"INSERT INTO ref_data.pipeline_delivery_points SELECT catalog_id,'{draft}',pipeline_code,zone_code,point_code FROM ref_data.pipeline_delivery_points WHERE(catalog_id,revision_id,point_code)=('{reference}','{source}','synthetic_gas_point_unpriced');")
    query(f"INSERT INTO ref_data.gas_delivery_conventions SELECT catalog_id,'{draft}',pipeline_code,zone_code,point_code,product_code,clock_code,day_start_minute FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,point_code)=('{reference}','{source}','synthetic_gas_point_unpriced');")
    query(f"BEGIN;ALTER TABLE ref_data.gas_meter_assignments DISABLE TRIGGER guard_definition;UPDATE ref_data.gas_meter_assignments SET identifier='SYN-CORRUPTED' WHERE(catalog_id,revision_id,code)=('{reference}','{draft}','synthetic_gas_meter_a_jan');ALTER TABLE ref_data.gas_meter_assignments ENABLE TRIGGER guard_definition;COMMIT;")
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
      PERFORM security.apply_reference_display('{reference}','{source}','{draft}','{apply}',0);
      RAISE EXCEPTION 'meter_identity_change_accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'reference_economic_change' THEN RAISE;END IF;END;
    END $test$;""",profile='approver')
    query(f"BEGIN;ALTER TABLE ref_data.gas_meter_assignments DISABLE TRIGGER guard_definition;UPDATE ref_data.gas_meter_assignments SET identifier='SYN-METER-A-JAN' WHERE(catalog_id,revision_id,code)=('{reference}','{draft}','synthetic_gas_meter_a_jan');ALTER TABLE ref_data.gas_meter_assignments ENABLE TRIGGER guard_definition;COMMIT;")
    runtime_query(query,manifest,f"SELECT security.apply_reference_display('{reference}','{source}','{draft}','{apply}',0);",profile='approver')
    assert query(f"SELECT state FROM ref_data.revisions WHERE(catalog_id,id)=('{reference}','{draft}');")=='sealed'
    print('PASS physical references: display edits preserve hierarchy; altered memberships fail economic-equivalence admission',flush=True)


if __name__=='__main__':
    import sys
    run_checks(sys.argv[1])


def check_completion_admission(query,manifest,receipt):
    catalog=manifest['catalogId'];instrument=receipt['instrumentId']
    cases=[
        ("jsonb_set(economic_identity::jsonb,'{Periods,0,DeliveryCompletionAt}',to_jsonb('2021-01-31T23:59:00.0000000+00:00'::text))",'gas_schedule_completion'),
        ("jsonb_set(economic_identity::jsonb,'{GasDeliveryClock,StartMinute}','1'::jsonb)",'gas_schedule_completion'),
        ("economic_identity::jsonb-'GasDeliveryClock'",'gas_schedule_completion'),
    ]
    checks=[]
    for mutation,expected in cases:
        checks.append(f"""BEGIN
          UPDATE trading.sides SET economic_identity=({mutation})::text WHERE(catalog_id,id)=(c,selected_side);
          PERFORM trading.require_gas_program_shape(c,schedule,1,p.delivery_from,p.delivery_to,m.payment_at,1,m.price,a,d,start_at);
          RAISE EXCEPTION 'gas_completion_tamper_accepted';
        EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{expected}' THEN RAISE;END IF;END;""")
    query(f"""BEGIN;
      ALTER TABLE trading.sides DISABLE TRIGGER USER;
      DO $test$ DECLARE c uuid='{catalog}';schedule uuid;selected_side uuid;m trading.gas_component_terms;p trading.gas_component_terms;
        a market_data.gas_valuation_assignments;d ref_data.gas_delivery_conventions;clock ref_data.clock_rules;start_at timestamptz;
      BEGIN
        SELECT * INTO STRICT m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(c,'{instrument}',1);
        SELECT * INTO STRICT p FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(c,'{instrument}',m.source_slot);
        SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(c,p.definition_revision_id,p.assignment_code);
        SELECT * INTO STRICT d FROM security.gas_setup_delivery(a);
        SELECT * INTO STRICT clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,d.clock_code);
        start_at=p.delivery_from::timestamp AT TIME ZONE 'UTC'+make_interval(mins=>d.day_start_minute)-make_interval(secs=>clock.fixed_offset_seconds);
        SELECT h.id,h.side_id INTO STRICT schedule,selected_side FROM trading.instrument_side_schedules i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id)
         WHERE(i.catalog_id,i.instrument_id,i.slot)=(c,'{instrument}',1);
        {''.join(checks)}
      END $test$;ROLLBACK;""")
    print('PASS gas native completion: missing, forged completion and altered clock refuse independently of unchanged executable rows',flush=True)
