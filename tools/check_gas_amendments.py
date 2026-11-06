"""Retained physical gas amendments through the ordinary deal ticket."""
from copy import deepcopy
from fractions import Fraction
import json
from uuid import uuid4
from preview_client import PreviewClient,initial_frame
from preview_fixture import trade_booking


def run_checks(url,query,manifest,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context=dict(snapshotId=frame['id'],convention='physical_gas_fixed_quantity_v4',knowledgeMode='current')
    catalog=c.call('gas/catalog',context)
    path=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    terms={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':path['definitionRevisionId'],
        'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
        'physicalSides':[dict(code='gas',pipeline=path['pipeline'],zone=path['zone'],deliveryPoint=path['deliveryPoint'],product=path['product'],direction='receive',
            deliveryFrom='2021-01-01',deliveryTo='2021-03-01',quantityBasis='per_operating_day',quantity='100',link=None)],
        'cashSides':[dict(code='payment',role='consideration',direction='pay',source='gas',stage='delivery',price='3',tariffCode=None,paymentAt='2021-03-02T00:00:00Z')]}
    saved=c.call('gas/composition/book',terms)
    trade=saved['tradeId']
    original=query(f"SELECT jsonb_build_array(to_jsonb(t),to_jsonb(f),to_jsonb(r))::text FROM trading.trades t JOIN trading.booking_facts f ON(f.catalog_id,f.trade_id)=(t.catalog_id,t.id) JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(t.catalog_id,t.id,1) WHERE t.id='{trade}';")
    original_context=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))['context']['contextId']
    ticket=c.call('tickets/trade',dict(context=context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1))
    assert ticket['actions']['amend']['allowed'],ticket['actions']['amend']
    draft=deepcopy(ticket['draft']);payment=next(s for s in draft['sides'] if s['code']=='payment')
    payment['values']['cash_price']='3.1'
    request=dict(context=context,draft=draft,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],expectedRevision=1,definitionRevisionId=terms['definitionRevisionId'],reason='Synthetic price correction',policy='future_gas_composition_replace_v1')
    preview=c.call('tickets/amend-preview',request)
    assert Fraction(preview['presentValue'])==772
    command={**request,'operationId':str(uuid4())}
    amended=c.call('tickets/amend',command)
    assert amended['tradeId']==saved['tradeId'] and amended['revision']==2 and amended['instrumentId']!=saved['instrumentId']
    assert c.call('tickets/amend',command)==amended
    assert c.call('tickets/status',dict(operationId=command['operationId']))==amended
    reopened=c.call('tickets/trade',dict(context=context,tradeId=amended['tradeId'],bindingId=amended['instrumentId'],revision=2))
    assert reopened['draft']==draft
    assert query(f"SELECT count(*) FROM trading.gas_trade_revisions WHERE trade_id='{saved['tradeId']}';")=='2'
    def counts():
        return query("SELECT jsonb_build_array("+','.join(f'(SELECT count(*) FROM trading.{table})' for table in ['gas_trade_revisions','gas_instruments','gas_memberships','gas_component_terms','gas_delivery_obligations','instrument_side_schedules','knowledge_receipts'])+")::text;")
    second={**request,'draft':deepcopy(draft),'bindingId':amended['instrumentId'],'expectedRevision':2,'reason':'Synthetic unchanged terms'}
    before=counts()
    no_op={**deepcopy(second),'operationId':str(uuid4())}
    unchanged=c.call('tickets/amend',no_op)
    assert unchanged['unchanged'] and unchanged['revision']==2 and unchanged['instrumentId']==amended['instrumentId'],unchanged
    assert counts()==before,'unchanged terms must not publish a new revision, instrument, occurrence or schedule'
    assert c.call('tickets/amend',no_op)==unchanged and c.call('tickets/status',dict(operationId=no_op['operationId']))==unchanged
    assert c.call('tickets/amend',{**command,'operationId':str(uuid4())},expected=409)['code']=='gas_revision_conflict'
    assert c.call('tickets/amend-preview',request,expected=409)['code']=='gas_revision_conflict'
    assert c.call('tickets/amend',{**command,'reason':'Altered retry'},expected=409)['code']=='gas_conflict'
    second['draft']['sides'][1]['values']['cash_price']='3.2'
    successor=c.call('tickets/amend',{**second,'operationId':str(uuid4())})
    assert successor['revision']==3 and not successor['unchanged']
    assert c.call('tickets/amend',command)==amended and c.call('tickets/amend',no_op)==unchanged
    from check_booked_side_programs import runtime_query
    runtime_query(query,manifest,f"SELECT security.lock_side_schedule_request('{manifest['catalogId']}','{command['operationId']}');SELECT security.lock_side_schedule_request('{manifest['catalogId']}','{no_op['operationId']}');")
    try:
        runtime_query(query,manifest,f"SELECT security.lock_side_schedule_request('{manifest['catalogId']}','{command['operationId']}');",profile='risk')
    except RuntimeError as error:assert 'No current schedule request authority' in str(error),error
    else:raise AssertionError('A different actor acquired the retained amendment stage')
    assert query(f"SELECT jsonb_build_array(to_jsonb(t),to_jsonb(f),to_jsonb(r))::text FROM trading.trades t JOIN trading.booking_facts f ON(f.catalog_id,f.trade_id)=(t.catalog_id,t.id) JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(t.catalog_id,t.id,1) WHERE t.id='{trade}';")==original
    old=c.call('tickets/trade',dict(context={**context,'knowledgeMode':'retained','contextId':original_context},tradeId=trade,bindingId=saved['instrumentId'],revision=1))
    assert old['draft']==ticket['draft'] and not old['actions']['amend']['allowed']
    shared={**context,'convention':'trading_workspace_v1'}
    selected=dict(context=shared,tradeId=trade,bindingId=successor['instrumentId'],revision=3,instrumentType='physical_gas')
    detail=c.call('trading/gas',selected)
    assert [int(r['revision']) for r in detail['history']]==[3,2,1],detail['history']
    assert detail['history'][1]['reason']==request['reason']
    quantities=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))
    current_obligations={r['obligationId'] for r in quantities['rows']}
    assert len(current_obligations)==1
    assert query(f"SELECT count(DISTINCT id) FROM trading.gas_delivery_obligations WHERE trade_id='{trade}';")=='3'
    c.call('profile',dict(profile='risk'))
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[trade])
    risk_catalog=c.call('portfolio/risk/catalog',scope)
    risk_request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':risk_catalog['context']['contextId']},'detail':'holdings',
        'convention':risk_catalog['convention'],'numericalPolicy':risk_catalog['numericalPolicy'],'pricingPolicy':risk_catalog['pricingPolicy'],'stress':[],
        'bumps':[dict(factor=f['factor'],size='0.01',convention=risk_catalog['bumpConvention']) for f in risk_catalog['factors']]}
    risk=c.call('portfolio/risk',risk_request)
    assert risk['work']['positionCount']==1 and not risk['excludedPositions']
    for factor in risk['factors']:
        values=factor['positions'][0]['values'];assert Fraction(values['base'])==182 and Fraction(values['gamma'])==0
        expected=3100 if factor['factor']['deliveryFrom']=='2021-01-01' else 2800
        assert Fraction(values['delta'])==expected
    job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=risk_request));run_worker()
    result=c.call('portfolio/risk/jobs/'+job['id'])
    from check_portfolio_risk import financial
    assert result['state']=='completed',result
    assert financial(result['result'])==financial(risk),(financial(result['result']),financial(risk))
    assert query('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;SELECT count(*) FROM trading.gas_amendment_commands;ROLLBACK;')=='0','an unclaimed worker cannot read amendment receipts'
    c.call('tickets/amend',{**second,'operationId':str(uuid4())},expected=403)
    c.call('profile',dict(profile='trader'))
    # Denying the exact latest binding cannot expose a previously visible revision.
    query(f"CREATE POLICY synthetic_amend_head_hidden ON trading.gas_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(instrument_id<>'{successor['instrumentId']}');")
    try:
        assert c.call('trading/positions',dict(context=shared,bookIds=[],portfolioIds=[],offset=0,limit=64),expected=422)['code']=='position_facts'
        c.call('tickets/trade',dict(context=context,tradeId=trade,bindingId=successor['instrumentId'],revision=3),expected=422)
        c.call('profile',dict(profile='risk'))
        c.call('portfolio/risk/catalog',scope,expected=422)
    finally:
        query('DROP POLICY synthetic_amend_head_hidden ON trading.gas_instruments;')
        c.call('profile',dict(profile='trader'))
    check_restrictions(c,query,manifest,terms,context)
    check_concurrency(url,c,terms,context)
    print('PASS gas amendment: multi-month correction, immutable original facts, historical head, exact retry/status after successor, unchanged publication, owned occurrences, denied latest binding and generic risk/worker parity',flush=True)
    return {**successor,'tradeReference':c.call('trading/position',selected)['holding']['tradeReference']}


def check_restrictions(c,query,manifest,terms,context):
    from check_booked_side_programs import runtime_query
    catalog=manifest['catalogId']
    short=deepcopy(terms);short['physicalSides'][0]['deliveryTo']='2021-01-02'
    def book():
        result=c.call('gas/composition/book',{**short,'operationId':str(uuid4())})
        rows=c.call('gas/quantities/read',dict(context=context,tradeId=result['tradeId'],selectionPolicy='operational_projection_v1'))
        return result,rows['rows'][0]['obligationId']
    def read(saved,ctx=context):
        return c.call('tickets/trade',dict(context=ctx,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1))
    # Service custody exists before any nomination or quantity observation.
    serviced,service_obligation=book()
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    grants=[str(uuid4()),str(uuid4())]
    for grant,role in zip(grants,['gas_operator','gas_service_admin']):
        query(f"INSERT INTO security.role_grants SELECT '{grant}','{principal}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE code='{role}';")
    try:
        service=dict(context=context,tradeId=serviced['tradeId'],expectedRevision=1,day='2021-01-01',kind='service',details=dict(
            obligationId=service_obligation,holderRole='legal_entity',externalReference='synthetic-amendment-service',expectedPreviousId=None,
            validFrom='2021-01-01',validTo='2021-02-01',active=True,statementPolicy='final_statement_balance_v1',termsReference='Synthetic service terms',evidence='Synthetic service custody'))
        review=c.call('gas/operations/preview',service)
        c.call('gas/operations/configure',dict(preview=service,operationId=str(uuid4()),previewHash=review['previewHash']))
        assert query(f"SELECT (SELECT count(*) FROM trading.gas_quantity_commands WHERE trade_id='{serviced['tradeId']}')+(SELECT count(*) FROM trading.gas_quantity_series WHERE obligation_id='{service_obligation}');")=='0'
        refusal=runtime_query(query,manifest,f"SELECT security.gas_amendment_refusal('{catalog}','{serviced['tradeId']}',1,'{serviced['instrumentId']}','2020-12-18T16:00:00Z');")
        assert refusal=='gas_amendment_operational',('service-only native amendment refusal',refusal)
        opened=read(serviced)
        assert not opened['actions']['amend']['allowed'] and 'service' in opened['actions']['amend']['reason']
        draft=deepcopy(opened['draft']);next(s for s in draft['sides'] if s['kind']=='cash')['values']['cash_price']='3.2'
        change=dict(context=context,draft=draft,tradeId=serviced['tradeId'],bindingId=serviced['instrumentId'],expectedRevision=1,
            definitionRevisionId=short['definitionRevisionId'],reason='Synthetic service-only amendment refusal',policy='future_gas_composition_replace_v1')
        assert c.call('tickets/amend-preview',change,expected=422)['code']=='gas_amendment_operational'
        assert c.call('tickets/amend',{**change,'operationId':str(uuid4())},expected=422)['code']=='gas_amendment_operational'
        assert query(f"SELECT count(*) FROM trading.gas_trade_revisions WHERE trade_id='{serviced['tradeId']}';")=='1'
        assert query(f"SELECT count(*) FROM trading.gas_delivery_obligations WHERE trade_id='{serviced['tradeId']}' AND id='{service_obligation}';")=='1'
        print('PASS service-only custody: no quantity facts, native/API amendment refusal and original obligation retained',flush=True)
    finally:
        query("DELETE FROM security.role_grants WHERE id=ANY(ARRAY["+','.join("'"+g+"'::uuid" for g in grants)+"]);")
    saved,obligation=book()
    later=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:'))
    after={**context,'snapshotId':later['id']}
    started=c.call('trading/gas',dict(context={**after,'convention':'trading_workspace_v1'},tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1,instrumentType='physical_gas'))['amend']
    assert not started['allowed'] and 'future' in started['reason']
    c.call('gas/quantities/record',dict(context=after,tradeId=saved['tradeId'],expectedRevision=1,operationId=str(uuid4()),obligationId=obligation,
        kind='nominated',status='provisional',day='2021-01-01',quantity='98',evidence='Synthetic nomination',expectedPreviousId=None))
    blocked=read(saved)
    assert not blocked['actions']['amend']['allowed'] and 'operational quantities' in blocked['actions']['amend']['reason']
    request=dict(context=context,draft=blocked['draft'],tradeId=saved['tradeId'],bindingId=saved['instrumentId'],expectedRevision=1,
        definitionRevisionId=short['definitionRevisionId'],reason='Synthetic correction',policy='future_gas_composition_replace_v1')
    assert c.call('tickets/amend',{**request,'operationId':str(uuid4())},expected=422)['code']=='gas_amendment_operational'
    assert c.call('tickets/amend',{**request,'context':after,'operationId':str(uuid4())},expected=422)['code']=='gas_amendment_started'
    short['physicalSides'][0].update(deliveryFrom='2021-01-03',deliveryTo='2021-01-04')
    origin,origin_obligation=book();target,target_obligation=book()
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
    def facility(body):
        encoded=json.dumps(body).replace("'","''")
        return json.loads(runtime_query(query,manifest,f"SELECT security.record_facility_quantity_command('{catalog}','{uuid4()}','{encoded}'::jsonb,'2021-01-15T08:00:00Z');"))
    try:
        ledger=facility(dict(kind='create',obligationId=origin_obligation,day='2021-01-03',facility=short['physicalSides'][0]['deliveryPoint']))
        source=facility(dict(kind='source',ledgerId=ledger['ledgerId'],expectedPreviousId=None,quantity='100',evidence='Synthetic source quantity'))
        facility(dict(kind='allocate',ledgerId=ledger['ledgerId'],expectedPreviousId=None,sourceRevisionId=source['revisionId'],evidence='Synthetic allocation',
            allocations=[dict(obligationId=target_obligation,tradeRevision=1,quantity='100')]))
        for trade in (origin,target):
            action=read(trade)['actions']['amend']
            assert not action['allowed'] and 'facility references' in action['reason'],action
    finally:
        query("DELETE FROM security.role_permissions WHERE permission IN('ReadFacilityQuantities','RecordFacilityQuantities') AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    print('PASS gas amendment refusals: started delivery, later operational facts, facility origin and a different target trade remain unamendable',flush=True)


def check_concurrency(url,c,terms,context):
    from concurrent.futures import ThreadPoolExecutor
    saved=c.call('gas/composition/book',{**terms,'operationId':str(uuid4())})
    ticket=c.call('tickets/trade',dict(context=context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1))
    attempts=[]
    for price in ['3.11','3.12']:
        client=PreviewClient(url);client.bootstrap();client.call('profile',dict(profile='trader'))
        own={**context,'snapshotId':initial_frame(client.call('workspace'),'2020-12-18')['id']}
        draft=deepcopy(ticket['draft']);draft['sides'][1]['values']['cash_price']=price
        attempts.append((client,dict(context=own,draft=draft,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],expectedRevision=1,
            definitionRevisionId=terms['definitionRevisionId'],reason='Synthetic concurrent correction',policy='future_gas_composition_replace_v1',operationId=str(uuid4()))))
    def amend(attempt):
        client,request=attempt
        try:return client.call('tickets/amend',request)
        except AssertionError as error:
            assert 'got 409:' in str(error),error
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:outcomes=list(pool.map(amend,attempts))
    assert sum(o is not None for o in outcomes)==1,outcomes
    print('PASS gas amendment concurrency: one exact-head winner and one stale revision refusal',flush=True)
