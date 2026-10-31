"""Physical gas ticket checks against an owned synthetic preview."""
from uuid import uuid4
from decimal import Decimal
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    workspace=c.call('workspace'); frame = initial_frame(workspace,'2020-12-18')
    context = {'snapshotId':frame['id'], 'convention':'physical_gas_fixed_quantity_v4', 'knowledgeMode':'current'}
    catalog = c.call('gas/catalog', context)
    assignment = next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    assert assignment['valuation']['group']=='synthetic_gas_group_a', 'Location must disclose its shared valuation group'
    body = {**context, **trade_booking(), 'operationId':str(uuid4()), 'bookId':catalog['books'][0]['id'],
            'gasSetupId':catalog['gasSetupId'],'definitionRevisionId':assignment['definitionRevisionId'], 'pipeline':assignment['pipeline'], 'zone':assignment['zone'], 'deliveryPoint':assignment['deliveryPoint'], 'product':assignment['product'],
            'deliveryFrom':'2021-01-01', 'deliveryTo':'2021-02-01', 'quantity':'1000',
            'quantityBasis':'period_total_equal_days', 'direction':'receive', 'fixedPrice':'3', 'usageFee':'0.1','usageTariffCode':None,'usageFeeDirection':'pay',
            'paymentAt':'2021-02-01T00:00:00Z', 'discounting':False}
    c.call('gas/preview',{**body,'floorPrice':'2'},expected=400)
    omitted_fee={k:v for k,v in body.items() if k!='usageFee'}
    c.call('gas/preview',omitted_fee,expected=400)
    c.call('gas/preview',{k:v for k,v in body.items() if k!='usageTariffCode'},expected=400)
    c.call('gas/preview',{**body,'usageFeeDirection':None},expected=422)
    preview = c.call('gas/preview', body)
    assert preview['valuation']['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-JAN'
    instructions=c.call('gas/instructions',body)
    assert instructions['status']=='unsubmitted_contractual_schedule'
    assert all(row['quantitySource']=='contractual' for row in instructions['rows'])
    assert len(instructions['rows'])==31 and instructions['rows'][0]['meter']['identifier']=='SYN-METER-A-JAN'
    tariff_body={**body,'operationId':str(uuid4()),'usageFee':None,'usageTariffCode':'synthetic_gas_usage_a'}
    tariff_value=c.call('gas/preview',tariff_body)
    assert Decimal(tariff_value['valuation']['presentValue'])==80
    tariff_booked=c.call('gas/book',tariff_body)
    tariff_loaded=c.call('gas/editor',{'context':context,'tradeId':tariff_booked['tradeId']})
    assert tariff_loaded['terms']['usageTariffCode']=='synthetic_gas_usage_a'
    assert tariff_loaded['valuation']==tariff_value['valuation']
    c.call('gas/preview',{**tariff_body,'usageFee':'0.1'},expected=422)
    c.call('gas/preview',{**tariff_body,'deliveryFrom':'2021-02-01','deliveryTo':'2021-03-01','paymentAt':'2021-03-01T00:00:00Z'},expected=422)
    sides = preview['valuation']['sides']
    assert Decimal(preview['valuation']['presentValue'])==100 and Decimal(sides[0]['presentValue'])==3200
    assert all(row['meter'] is None for row in c.call('gas/preview',{**body,'deliveryPoint':'synthetic_gas_point_d'})['valuation']['deliveryRows'])
    assert len(sides)==3 and sides[0]['economicClass']=='physical_value'
    assert Decimal(sides[1]['presentValue'])==-3000 and Decimal(sides[2]['presentValue'])==-100
    assert sum(Decimal(s['presentValue']) for s in sides)==Decimal(preview['valuation']['presentValue'])
    booked=c.call('gas/book',body)
    assert c.call('gas/book',body)==booked
    retained=c.call('gas/editor',{'context':context,'tradeId':booked['tradeId']})
    assert retained['valuation']==preview['valuation']
    assert len(retained['valuation']['deliveryRows'])==31
    assert retained['valuation']['deliveryRows'][0]['quantity']['numerator']=='1000' and retained['valuation']['deliveryRows'][0]['quantity']['denominator']=='31'
    assert retained['terms']['pipeline']==body['pipeline'] and retained['terms']['zone']==body['zone']
    assert retained['physical']['valuation']['marketLocation']!=retained['terms']['deliveryPoint']
    assert retained['terms']['legalEntityId']==body['legalEntityId'] and retained['terms']['counterpartyRevision']==body['counterpartyRevision']
    c.call('gas/book',{**body,'quantity':'1001'},expected=409)
    for change in [{'direction':None},{'quantityBasis':'per_elapsed_hour'},{'discounting':True},{'deliveryPoint':'unknown'}, {'paymentAt':'2020-12-31T00:00:00Z'}]:
        c.call('gas/preview',{**body,**change},expected=422)
    assert assignment['clock']=='test_utc' and 'clock' not in assignment['valuation']
    assert len(catalog['locations'])==5, 'Each physical path appears once, independently of six forward months'
    february=c.call('gas/preview',{**body,'deliveryFrom':'2021-02-01','deliveryTo':'2021-03-01','paymentAt':'2021-03-01T00:00:00Z'})
    assert Decimal(february['valuation']['presentValue'])==165
    assert february['physical']['pipeline']==preview['physical']['pipeline']
    assert february['valuation']['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-FEB'
    same_zone=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_d')
    assert same_zone['zone']==assignment['zone'] and same_zone['valuation']['group']==assignment['valuation']['group']
    assert Decimal(c.call('gas/preview',{**body,'deliveryPoint':same_zone['deliveryPoint']})['valuation']['presentValue'])==50
    c.call('gas/instructions',{**body,'deliveryPoint':same_zone['deliveryPoint']},expected=422)
    second=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_b')
    changed={**body,'pipeline':second['pipeline'],'zone':second['zone'],'deliveryPoint':second['deliveryPoint']}
    assert Decimal(c.call('gas/preview',changed)['valuation']['presentValue'])==0
    c.call('gas/preview',{**changed,'zone':body['zone']},expected=422)
    third=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_c')
    third_body={**body,'operationId':str(uuid4()),'pipeline':third['pipeline'],'zone':third['zone'],'deliveryPoint':third['deliveryPoint'],'paymentAt':'2021-02-02T00:00:00Z'}
    different_day=c.call('gas/preview',third_body)
    assert third['valuation']['curve']==assignment['valuation']['curve'] and third['valuation']['group']!=assignment['valuation']['group']
    c.call('gas/preview',{**third_body,'usageFee':None,'usageTariffCode':'synthetic_gas_usage_a'},expected=422)
    assert different_day['physical']['dayStartMinute']==60 and different_day['valuation']['deliveryRows'][0]['start'].startswith('2021-01-01T01:00:00')
    assert Decimal(different_day['valuation']['presentValue'])==-100
    third_booked=c.call('gas/book',third_body)
    third_loaded=c.call('gas/editor',{'context':context,'tradeId':third_booked['tradeId']})
    assert third_loaded['valuation']==different_day['valuation'] and third_loaded['physical']['dayStartMinute']==60
    missing=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_unpriced')
    c.call('gas/preview',{**body,'pipeline':missing['pipeline'],'zone':missing['zone'],'deliveryPoint':missing['deliveryPoint']},expected=422)
    sale=c.call('gas/preview',{**body,'direction':'deliver'})
    assert Decimal(sale['valuation']['presentValue'])==-300
    assert Decimal(c.call('gas/preview',{**body,'direction':'deliver','usageFeeDirection':'receive'})['valuation']['presentValue'])==-100
    without_fee=c.call('gas/preview',{**body,'usageFee':None,'usageFeeDirection':None})
    assert len(without_fee['valuation']['sides'])==2 and Decimal(without_fee['valuation']['presentValue'])==200
    daily=c.call('gas/preview',{**body,'quantityBasis':'per_operating_day','quantity':'10'})
    assert Decimal(daily['valuation']['presentValue'])==31
    before_booking={**context,'knowledgeMode':'retained','contextId':catalog['context']['contextId']}
    c.call('gas/editor',{'context':before_booking,'tradeId':booked['tradeId']},expected=422)
    listed=c.call('gas/directory',context)['trades']
    assert any(t['trade_id']==booked['tradeId'] for t in listed)
    shared={**context,'convention':'trading_workspace_v1'}
    view={'mode':'trades','bookIds':[body['bookId']],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    mixed=c.call('trading/blotter',{'context':shared,'view':view})
    gas=next(h for h in mixed['holdings'] if h['tradeId']==booked['tradeId'])
    assert gas['instrumentType']=='physical_gas' and Decimal(gas['presentValue'])==100
    assert gas['bindingId']==booked['instrumentId']
    workspace=c.call('trading/workspace',{'context':shared,'view':view,'bookingCatalog':'directory','summaries':[]})
    assert any(h['tradeId']==booked['tradeId'] for h in workspace['blotter']['holdings'])
    c.call('profile',{'profile':'risk'})
    c.call('gas/book',{**body,'operationId':str(uuid4())},expected=403)
    assert c.call('gas/status',{'operationId':body['operationId']})=={'found':False}
    print('PASS physical gas: PV100 oracle, composition, quantities, retained booking, idempotency, temporal scope and authority',flush=True)

    return booked


def run_shape_checks(query, manifest, receipt):
    """Alter only owned disposable rows inside rolled-back subtransactions.

    Calling final admission directly isolates program/terms correspondence from
    the separate byte/relational seal checks exercised by the shared suite.
    """
    catalog=manifest['catalogId']; instrument=receipt['instrumentId']
    from check_booked_side_programs import runtime_query
    trade=receipt['tradeId']
    operation=query(f"SELECT operation_id FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=('{catalog}','{trade}');")
    assert query(f"SELECT mapping_count FROM trading.instrument_schedule_cell_seals WHERE(catalog_id,request_id)=('{catalog}','{operation}');")=='0'
    changed=f"ALTER TABLE trading.trades DISABLE TRIGGER ALL;UPDATE trading.trades SET request=request||jsonb_build_object('syntheticMismatch',true) WHERE(catalog_id,id)=('{catalog}','{trade}');"
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
      PERFORM security.publish_side_schedules('{catalog}','{operation}','{instrument}');
      RAISE EXCEPTION 'gas_command_mismatch_accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'side_schedule_command' THEN RAISE;END IF;END;END $test$;""",prefix=changed,commit=False)
    assert query("SELECT has_function_privilege('openetrm_runtime','security.publish_admitted_side_schedules(uuid,uuid,uuid)','EXECUTE');")=='f'
    print('PASS gas retained publication: zero-map seal, exact accepted command and private underlying publisher',flush=True)

    assert query("SELECT has_function_privilege('openetrm_risk_worker','security.capture_date_context(uuid,date,timestamptz)','EXECUTE'),has_function_privilege('openetrm_risk_worker','security.capture_pre_gas_setup_context(uuid,date,timestamptz)','EXECUTE');")=='t|f'
    query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;
      DO $test$ BEGIN BEGIN
        PERFORM security.capture_date_context('{catalog}','2020-12-18',NULL);
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
    cases=[
        (0,"UPDATE trading.side_schedule_rows SET parameters=jsonb_build_object('numerator','999','denominator','1') WHERE(catalog_id,schedule_id)=(c,schedule) AND operation='constant_price'",'gas_schedule_price'),
        (0,"UPDATE trading.side_schedule_grids SET origin=origin+interval '1 day' WHERE(catalog_id,schedule_id,ordinal)=(c,schedule,0)",'gas_schedule_grid'),
        (0,"UPDATE trading.side_schedule_bindings SET coordinate=jsonb_set(coordinate::jsonb,'{Fixing,FixingAt}','\"2019-01-01T00:00:00.0000000+00:00\"')::text WHERE(catalog_id,schedule_id,kind)=(c,schedule,'gas_assignment')",'gas_schedule_assignment'),
        (0,"DELETE FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id,kind)=(c,schedule,'gas_assignment')",'gas_schedule_sources'),
        (0,"INSERT INTO trading.side_schedule_edges SELECT c,schedule,target,'input',31,source,(SELECT max(sequence_ordinal)+1 FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(c,schedule)) FROM trading.side_schedule_edges e WHERE(e.catalog_id,e.schedule_id,e.target,e.ordinal)=(c,schedule,(SELECT ordinal FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,local_key)=(c,schedule,'side/pv')),0)",'gas_schedule_edges'),
        (1,"UPDATE trading.side_schedule_edges SET source=(SELECT ordinal FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,local_key)=(c,schedule,'payment/0/amount')) WHERE(catalog_id,schedule_id,target)=(c,schedule,(SELECT ordinal FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,local_key)=(c,schedule,'side/pv')))",'gas_schedule_edges'),
        (0,"UPDATE trading.side_schedule_rows SET parameters=jsonb_build_object('numerator',((power(10::numeric,80)+1)/gcd(power(10::numeric,80)+1,31*power(10::numeric,80)))::numeric(120,0)::text,'denominator',(31*power(10::numeric,80)/gcd(power(10::numeric,80)+1,31*power(10::numeric,80)))::numeric(120,0)::text) WHERE(catalog_id,schedule_id,local_key)=(c,schedule,'gas/0/quantity')",'gas_schedule_quantity'),
    ]
    body=[]
    for slot,mutation,expected in cases:
        body.append(f"""
        SELECT m.schedule_id INTO STRICT schedule FROM trading.instrument_side_schedules m
        WHERE(m.catalog_id,m.instrument_id,m.slot)=(c,'{instrument}',{slot});
        BEGIN
          {mutation};
          PERFORM trading.require_gas_program_shape(c,schedule,{slot},i.delivery_from,i.delivery_to,i.payment_at,1,
           CASE {slot} WHEN 1 THEN i.fixed_price ELSE i.usage_fee END,a,delivery,i.delivery_from::timestamp AT TIME ZONE 'UTC');
          RAISE EXCEPTION 'gas_tamper_accepted';
        EXCEPTION WHEN raise_exception THEN
          IF SQLERRM<>'{expected}' THEN RAISE;END IF;
        END;
        """)
    query("""BEGIN;
    ALTER TABLE trading.side_schedule_rows DISABLE TRIGGER USER;
    ALTER TABLE trading.side_schedule_edges DISABLE TRIGGER USER;
    ALTER TABLE trading.side_schedule_grids DISABLE TRIGGER USER;
    ALTER TABLE trading.side_schedule_bindings DISABLE TRIGGER USER;
    """+f"""DO $test$ DECLARE c uuid='{catalog}';schedule uuid;i trading.gas_instruments;a market_data.gas_valuation_assignments;delivery ref_data.gas_delivery_conventions;
    BEGIN
      SELECT * INTO STRICT i FROM trading.gas_instruments WHERE(catalog_id,instrument_id)=(c,'{instrument}');
      SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(c,i.definition_revision_id,i.assignment_code);
      SELECT * INTO STRICT delivery FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code);
      {''.join(body)}
    END $test$;ROLLBACK;""")
    print(f'PASS physical gas: {len(cases)} independently mutated graphs reject at final admission',flush=True)
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

    import json
    original=json.loads(query(f"SELECT request::text FROM trading.trades WHERE(catalog_id,id)=('{catalog}','{receipt['tradeId']}');"))
    original['operationId']=str(uuid4());original['fuelFraction']='0.02'
    encoded=json.dumps(original).replace("'","''")
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
      PERFORM security.book_physical_gas('{catalog}','{encoded}'::jsonb,'2020-12-18T16:00:00Z');
      RAISE EXCEPTION 'gas_unknown_field_accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_input' THEN RAISE;END IF;END;
    END $test$;""")
    print('PASS physical gas: direct authorized booking rejects unsupported economic fields',flush=True)

    # A display-only reference revision must preserve and prove the physical hierarchy.
    reference=manifest['referenceCatalogId'];source=manifest['referenceRevisionId'];draft=str(uuid4())
    runtime_query(query,manifest,f"SELECT security.save_reference_display_edit('{reference}','{source}','{source}','{draft}','locations','synthetic_gas_point_a','{{\"name\":\"Renamed synthetic delivery point\"}}'::jsonb);",profile='editor')
    assert query(f"SELECT (SELECT count(*) FROM ref_data.pipeline_zones WHERE(catalog_id,revision_id)=('{reference}','{draft}')),(SELECT count(*) FROM ref_data.pipeline_delivery_points WHERE(catalog_id,revision_id)=('{reference}','{draft}')),(SELECT count(*) FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id)=('{reference}','{draft}'));")=='3|5|5', 'Display edits must preserve PZL relationships'
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
