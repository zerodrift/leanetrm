"""Native HTTP fixed-volume crude qualification in a newly owned synthetic database."""
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest):
    c=PreviewClient(url); c.bootstrap(); c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_crude_fixed_volume_v1','knowledgeMode':'current'}
    catalog=c.call('crude/catalog',context)
    assignment=catalog['assignments'][0]; profile=catalog['profiles'][0]
    assert assignment['standardTemperature']=='60' and assignment['temperatureScale']=='fahrenheit'
    body={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],
          'definitionRevisionId':assignment['definitionRevisionId'],'assignmentCode':assignment['code'],
          'profile':profile['code'],'quantityBasis':profile['quantityBasis'],'tolerance':profile['tolerance'],'discounting':profile['discounting'],
          'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-02-01T00:00:00Z','paymentAt':'2021-02-01T00:00:00Z',
          'quantity':'1000','direction':'receive','considerationDirection':'pay','fixedPrice':'70','handlingFee':'0.1','handlingFeeDirection':'receive'}
    c.call('crude/preview',{**body,'unmodeledTerm':'x'},expected=400)
    c.call('crude/preview',{k:v for k,v in body.items() if k!='handlingFee'},expected=400)
    for change in [{'profile':None},{'quantityBasis':'observed_volume'},{'tolerance':'0.1'},{'discounting':True},
                   {'direction':None},{'considerationDirection':None},{'handlingFeeDirection':None},{'assignmentCode':'unknown'},
                   {'deliveryFrom':'2021-01-01T01:00:00+01:00'},{'deliveryTo':'2021-02-02T00:00:00Z'},
                   {'paymentAt':'2021-01-31T00:00:00Z'},{'quantity':'0'},
                   {'deliveryFrom':'2021-01-01T00:00:00.0000001Z'},{'paymentAt':'2021-02-01T00:00:00.0000001Z'}]:
        c.call('crude/preview',{**body,**change},expected=422)
    value=c.call('crude/preview',body)
    assert Decimal(value['presentValue'])==10100, value
    assert [Decimal(side['presentValue']) for side in value['sides']]==[80000,-70000,100]
    assert Decimal(c.call('crude/preview',{**body,'deliveryFrom':'2021-01-12T06:30:00Z','deliveryTo':'2021-01-12T08:30:00Z'})['presentValue'])==10100
    assert Decimal(c.call('crude/preview',{**body,'direction':'deliver','considerationDirection':'receive'})['presentValue'])==-9900
    assert Decimal(c.call('crude/preview',{**body,'handlingFee':None,'handlingFeeDirection':None})['presentValue'])==10000
    before=query(f"SELECT count(*) FROM trading.crude_trade_revisions WHERE catalog_id='{manifest['catalogId']}';")
    booked=c.call('crude/book',body)
    assert c.call('crude/book',body)==booked
    assert c.call('crude/status',{'operationId':body['operationId']})==booked
    c.call('crude/book',{**body,'quantity':'1001'},expected=409)
    assert int(query(f"SELECT count(*) FROM trading.crude_trade_revisions WHERE catalog_id='{manifest['catalogId']}';"))==int(before)+1
    loaded=c.call('crude/editor',{'context':context,'tradeId':booked['tradeId']})
    assert loaded['valuation']=={key:value[key] for key in loaded['valuation']}
    assert loaded['terms']['considerationDirection']=='pay' and loaded['terms']['handlingFeeDirection']=='receive'
    assert any(row['tradeId']==booked['tradeId'] for row in c.call('crude/directory',context)['trades'])
    prior={**context,'knowledgeMode':'retained','contextId':value['context']['contextId']}
    assert not any(row['tradeId']==booked['tradeId'] for row in c.call('crude/directory',prior)['trades']), 'A context captured before booking cannot see a later publication'
    c.call('crude/editor',{'context':prior,'tradeId':booked['tradeId']},expected=422)
    shared={**context,'convention':'trading_workspace_v1'}
    positions={'context':shared,'bookIds':[],'portfolioIds':[],'offset':0,'limit':64}
    facts=c.call('trading/positions',positions)
    fact=next(row for row in facts['positions'] if row['tradeId']==booked['tradeId'])
    assert fact['instrumentType']=='physical_crude' and fact['bindingId']==booked['instrumentId']
    assert fact['bookedQuantity']=='1000' and fact['bookedUnit']==assignment['quantityUnit']
    oldfacts=c.call('trading/positions',{**positions,'context':{**prior,'convention':'trading_workspace_v1'}})
    assert not any(row['tradeId']==booked['tradeId'] for row in oldfacts['positions'])
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    mixed=c.call('trading/blotter',{'context':shared,'view':view})
    holding=next(row for row in mixed['holdings'] if row['tradeId']==booked['tradeId'])
    assert holding['instrumentType']=='physical_crude' and holding['measure']=='crude_contractual_present_value'
    assert Decimal(holding['presentValue'])==10100 and Decimal(holding['quantity'])==1000
    assert holding['direction']=='receive' and holding['considerationDirection']=='pay'
    exact={'context':shared,'tradeId':booked['tradeId'],'bindingId':booked['instrumentId'],'revision':1,'instrumentType':'physical_crude'}
    single=c.call('trading/position',exact)
    assert single['holding']['presentValue']==holding['presentValue']
    terms=c.call('trading/crude',exact)
    assert terms['head']['request']['assignmentCode']==body['assignmentCode']
    assert terms['physical']['grade_code']==assignment['grade']
    c.call('trading/position',{**exact,'revision':2},expected=422)
    other=next(book['id'] for book in catalog['books'] if book['id']!=body['bookId'])
    assert c.call('trading/positions',{**positions,'bookIds':[other]})['positions']==[]
    c.call('profile',{'profile':'risk'})
    risk=c.call('portfolio/risk/catalog',{'context':shared,'bookIds':[],'portfolioIds':[]})
    assert any(measure['measure']=='crude_contractual_present_value' for measure in risk['measures'])
    other_grant=str(uuid4())
    query(f"INSERT INTO security.role_grants SELECT '{other_grant}',principal_id,'a0000000-0000-0000-0000-000000000002','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 hour',false FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer';")
    try:
        c.call('profile',{'profile':'viewer'})
        assert c.call('crude/status',{'operationId':body['operationId']})=={'found':False}, 'Recovery must be actor bound'
        c.call('crude/book',body,expected=409)
    finally:
        query(f"UPDATE security.role_grants SET revoked=true WHERE id='{other_grant}';")
    c.call('profile',{'profile':'trader'})
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    grants=query(f"SELECT string_agg(quote_literal(id::text),',') FROM security.role_grants WHERE principal_id='{principal}' AND NOT revoked;")
    assert grants
    try:
        query(f"UPDATE security.role_grants SET revoked=true WHERE id::text IN({grants});")
        c.call('crude/status',{'operationId':body['operationId']},expected=403)
        c.call('crude/editor',{'context':context,'tradeId':booked['tradeId']},expected=403)
        c.call('crude/book',body,expected=403)
    finally:
        query(f"UPDATE security.role_grants SET revoked=false WHERE id::text IN({grants});")
    c.call('profile',{'profile':'risk'})
    assert not c.call('crude/catalog',context)['canBook']
    c.call('crude/book',{**body,'operationId':str(uuid4())},expected=403)
    c.call('crude/editor',{'context':context,'tradeId':booked['tradeId']})
    query("CREATE POLICY crude_test_hidden_grade ON ref_data.crude_grades AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);")
    try:
        assert c.call('trading/positions',positions,expected=422)['code']=='position_facts'
        assert c.call('trading/crude',exact,expected=422)['code']=='position_facts'
    finally:
        query("DROP POLICY crude_test_hidden_grade ON ref_data.crude_grades;")
    c.call('profile',{'profile':'trader'})
    check_publication_expiry(c,query,manifest,positions,principal,grants)
    equal_cash={**body,'operationId':str(uuid4()),'bookId':other,'handlingFee':'70','handlingFeeDirection':'receive'}
    equal_booked=c.call('crude/book',equal_cash)
    equal_value=c.call('trading/position',{**exact,'tradeId':equal_booked['tradeId'],'bindingId':equal_booked['instrumentId']})
    assert Decimal(equal_value['holding']['presentValue'])==80000, 'Oppositely signed identical cash sides must cancel without erasing physical delivery'
    assert query(f"SELECT count(DISTINCT side_id) FROM trading.crude_memberships WHERE(catalog_id,instrument_id)=('{manifest['catalogId']}','{equal_booked['instrumentId']}') AND slot IN(1,2);")=='1'
    print('PASS native crude: exact 60°F standard volume, independent signs, fixed total, durable retry/status/reopen, strict terms and current authority',flush=True)
    return booked


def run_shape_checks(query, manifest, receipt):
    """Attack final admission in rolled-back owned rows, separately from byte seals."""
    import json
    from check_booked_side_programs import runtime_query
    catalog=manifest['catalogId']; instrument=receipt['instrumentId']; trade=receipt['tradeId']
    operation=query(f"SELECT operation_id FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id)=('{catalog}','{trade}');")
    cases=[
        (0,"UPDATE trading.side_schedule_rows SET parameters=jsonb_build_object('numerator','2','denominator','1') WHERE(catalog_id,schedule_id,operation)=(c,schedule,'quantity_literal')",'crude_schedule_quantity'),
        (0,"UPDATE trading.side_schedule_grids SET origin=origin+interval '1 hour' WHERE(catalog_id,schedule_id)=(c,schedule)",'crude_schedule_grid'),
        (0,"UPDATE trading.side_schedule_bindings SET coordinate=jsonb_set(coordinate::jsonb,'{StandardTemperature}','\"15\"')::text WHERE(catalog_id,schedule_id,kind)=(c,schedule,'crude_assignment')",'crude_schedule_sources'),
        (0,"DELETE FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id,kind)=(c,schedule,'crude_grade')",'crude_schedule_sources'),
        (0,"UPDATE trading.side_schedule_bindings SET location_code='test_hub_a' WHERE(catalog_id,schedule_id,port)=(c,schedule,'custody')",'crude_schedule_sources'),
        (1,"UPDATE trading.side_schedule_rows SET parameters=jsonb_build_object('numerator','999','denominator','1') WHERE(catalog_id,schedule_id,operation)=(c,schedule,'constant_price')",'crude_schedule_price'),
        (1,"DELETE FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id,target)=(c,schedule,(SELECT ordinal FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,local_key)=(c,schedule,'side/pv')))",'crude_schedule_edges'),
    ]
    checks=[]
    for slot,mutation,expected in cases:
        checks.append(f"""
        SELECT schedule_id INTO STRICT schedule FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=(c,'{instrument}',{slot});
        BEGIN
         {mutation};
         PERFORM trading.require_crude_program_shape(c,schedule,{slot},i.delivery_from,i.delivery_to,i.payment_at,
           CASE {slot} WHEN 1 THEN i.fixed_price ELSE i.handling_fee END,a);
         RAISE EXCEPTION 'crude_tamper_accepted';
        EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{expected}' THEN RAISE;END IF;END;
        """)
    query("""BEGIN;
     ALTER TABLE trading.side_schedule_rows DISABLE TRIGGER USER;
     ALTER TABLE trading.side_schedule_edges DISABLE TRIGGER USER;
     ALTER TABLE trading.side_schedule_grids DISABLE TRIGGER USER;
     ALTER TABLE trading.side_schedule_bindings DISABLE TRIGGER USER;
    """+f"""DO $test$ DECLARE c uuid='{catalog}';schedule uuid;i trading.crude_instruments;a market_data.crude_valuation_assignments;
    BEGIN
     SELECT * INTO STRICT i FROM trading.crude_instruments WHERE(catalog_id,instrument_id)=(c,'{instrument}');
     SELECT * INTO STRICT a FROM market_data.crude_valuation_assignments WHERE(catalog_id,revision_id,code)=(c,i.definition_revision_id,i.assignment_code);
     {''.join(checks)}
     SELECT schedule_id INTO STRICT schedule FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=(c,'{instrument}',0);
     BEGIN
      UPDATE trading.side_schedule_bindings SET reference_revision_id='20000000-0000-0000-0000-000000000002'
       WHERE(catalog_id,schedule_id,kind)=(c,schedule,'crude_grade');
      RAISE EXCEPTION 'crude_cross_revision_grade_accepted';
     EXCEPTION WHEN foreign_key_violation THEN NULL;END;
    END $test$;ROLLBACK;""")
    changed=f"ALTER TABLE trading.trades DISABLE TRIGGER ALL;UPDATE trading.trades SET request=request||jsonb_build_object('changed',true) WHERE(catalog_id,id)=('{catalog}','{trade}');"
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
     PERFORM security.publish_side_schedules('{catalog}','{operation}','{instrument}');
     RAISE EXCEPTION 'crude_command_mismatch_accepted';
     EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'side_schedule_command' THEN RAISE;END IF;END;END $test$;""",prefix=changed,commit=False)
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
     PERFORM security.crude_position_scope('{catalog}','{{}}'::uuid[],'{{}}'::uuid[]);
     RAISE EXCEPTION 'unbound_crude_scope_accepted';
     EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'knowledge_coverage' THEN RAISE;END IF;END;END $test$;""")
    original=json.loads(query(f"SELECT request::text FROM trading.trades WHERE(catalog_id,id)=('{catalog}','{trade}');"))
    original.update(operationId=str(uuid4()),unmodeledTerm='unsupported')
    encoded=json.dumps(original).replace("'","''")
    runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
     PERFORM security.book_physical_crude('{catalog}','{encoded}'::jsonb,'2020-12-18T16:00:00Z');
     RAISE EXCEPTION 'crude_unknown_field_accepted';
     EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'crude_input' THEN RAISE;END IF;END;END $test$;""")
    query("""BEGIN;
     CREATE POLICY crude_test_wide ON trading.crude_instruments FOR SELECT TO openetrm_runtime USING(true);
     CREATE POLICY crude_test_wide ON trading.crude_trade_revisions FOR SELECT TO openetrm_runtime USING(true);
     CREATE POLICY crude_test_wide ON market_data.crude_valuation_assignments FOR SELECT TO openetrm_runtime USING(true);
     CREATE POLICY crude_test_wide ON ref_data.crude_grades FOR SELECT TO openetrm_runtime USING(true);
     SET LOCAL ROLE openetrm_runtime;
     DO $test$ BEGIN
      IF EXISTS(SELECT 1 FROM trading.crude_instruments) OR EXISTS(SELECT 1 FROM trading.crude_trade_revisions)
       OR EXISTS(SELECT 1 FROM market_data.crude_valuation_assignments) OR EXISTS(SELECT 1 FROM ref_data.crude_grades)
      THEN RAISE EXCEPTION 'crude_scope_bypassed';END IF;
     END $test$;ROLLBACK;""")
    query("""BEGIN;
     CREATE POLICY crude_test_worker_wide ON trading.crude_instruments FOR SELECT TO openetrm_risk_worker USING(true);
     CREATE POLICY crude_test_worker_wide ON trading.crude_trade_revisions FOR SELECT TO openetrm_risk_worker USING(true);
     CREATE POLICY crude_test_worker_wide ON market_data.crude_valuation_assignments FOR SELECT TO openetrm_risk_worker USING(true);
     CREATE POLICY crude_test_worker_wide ON ref_data.crude_grades FOR SELECT TO openetrm_risk_worker USING(true);
     SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;
     DO $test$ BEGIN
      IF EXISTS(SELECT 1 FROM trading.crude_instruments) OR EXISTS(SELECT 1 FROM trading.crude_trade_revisions)
       OR EXISTS(SELECT 1 FROM market_data.crude_valuation_assignments) OR EXISTS(SELECT 1 FROM ref_data.crude_grades)
      THEN RAISE EXCEPTION 'unbound_crude_worker_read';END IF;
     END $test$;ROLLBACK;""")
    reference=manifest['referenceCatalogId']; revision=manifest['referenceRevisionId']; draft=str(uuid4())
    runtime_query(query,manifest,f"SELECT security.save_reference_display_edit('{reference}','{revision}','{revision}','{draft}','locations','test_facility_a','{{\"name\":\"Renamed synthetic crude facility\"}}'::jsonb);",profile='editor')
    assert query(f"SELECT(SELECT count(*) FROM ref_data.crude_grades WHERE(catalog_id,revision_id)=('{reference}','{draft}')),(SELECT count(*) FROM ref_data.crude_delivery_points WHERE(catalog_id,revision_id)=('{reference}','{draft}'));")=='1|1'
    runtime_query(query,manifest,f"SELECT security.apply_reference_display('{reference}','{revision}','{draft}','{uuid4()}',0);",profile='approver')
    assert query(f"SELECT state FROM ref_data.revisions WHERE(catalog_id,id)=('{reference}','{draft}');")=='sealed'
    print(f'PASS crude final admission: {len(cases)} independent physical/cash graph mutations, exact accepted command, direct unknown terms and restrictive authority',flush=True)


def check_publication_expiry(client,query,manifest,positions,principal,grants):
    """Let an independent market grant expire specifically inside Rights materialization."""
    read_role,market_role,read_grant,market_grant=[str(uuid4()) for _ in range(4)]
    original=query("SELECT pg_get_functiondef('security.has_permission(text)'::regprocedure);")
    # This disposable-only barrier is reached by Rights after the actual business read.
    # Existing ReadTrades remains valid; only its independently granted market access expires.
    barrier=f"""IF permission='BookTrade' THEN
      PERFORM nextval('security.publication_expiry_barrier');
      PERFORM pg_sleep(GREATEST(0,extract(epoch FROM ((SELECT granted_to FROM security.role_grants WHERE id='{market_grant}')-clock_timestamp())))+0.05);
    END IF;"""
    assert '\nBEGIN\n' in original
    try:
        query(f"""CREATE SEQUENCE security.publication_expiry_barrier;
        GRANT USAGE ON SEQUENCE security.publication_expiry_barrier TO openetrm_security_owner;
        INSERT INTO security.roles(id,code,name) VALUES('{read_role}','expiry_read_{read_role}','Synthetic expiry read'),('{market_role}','expiry_market_{market_role}','Synthetic expiry market');
        INSERT INTO security.role_permissions SELECT DISTINCT '{read_role}'::uuid,permission FROM security.role_permissions WHERE role_id IN(SELECT role_id FROM security.role_grants WHERE id::text IN({grants})) AND permission<>'ReadMarketData';
        INSERT INTO security.role_permissions VALUES('{market_role}','ReadMarketData');
        INSERT INTO security.role_grants VALUES
          ('{read_grant}','{principal}','{read_role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 hour',false),
          ('{market_grant}','{principal}','{market_role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '2 seconds',false);
        UPDATE security.role_grants SET revoked=true WHERE id::text IN({grants});""")
        query(original.replace('\nBEGIN\n','\nBEGIN\n'+barrier+'\n',1))
        client.call('trading/positions',positions,expected=403)
        assert query('SELECT is_called FROM security.publication_expiry_barrier;')=='t', 'Grant must expire during Rights, not before the business read'
    finally:
        query(original)
        query(f"""DROP SEQUENCE IF EXISTS security.publication_expiry_barrier;
        UPDATE security.role_grants SET revoked=false WHERE id::text IN({grants});
        DELETE FROM security.role_grants WHERE id IN('{read_grant}','{market_grant}');
        DELETE FROM security.role_permissions WHERE role_id IN('{read_role}','{market_role}');
        DELETE FROM security.roles WHERE id IN('{read_role}','{market_role}');""")
    print('PASS trading publication: independent market grant expires after business read during Rights; response refused',flush=True)
