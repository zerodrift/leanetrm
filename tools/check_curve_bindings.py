"""Direct mixed-grid sources through the existing physical ticket and risk path."""
import json
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from preview_fixture import trade_booking
from check_portfolio_risk import financial


def run_checks(client, manifest, query, run_worker, frame, mixed, daily_graph, publish, preview, exact):
    alternate=deepcopy(daily_graph)
    alternate.update(code='gas_alternate_source',name='Synthetic alternate gas forecast')
    for weight in alternate['nodes'][1]['contractWeights']:weight['weight']='3'
    alternate=publish(alternate)
    client.call('profile',{'profile':'trader'})
    context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    catalog=client.call('gas/catalog',context)
    path=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    reference=lambda saved:dict(catalogId=manifest['catalogId'],curveId=saved['id'],version=saved['version'],outputNode='output',position=None,
        selection='containing_delivery_interval_v1',implementation='published_custom_curve_projection_v1')
    def physical(code,start,end):
        return dict(code=code,pipeline=path['pipeline'],zone=path['zone'],deliveryPoint=path['deliveryPoint'],product=path['product'],direction='receive',
            deliveryFrom=start,deliveryTo=end,quantityBasis='per_operating_day',quantity='1',link=None,forecastCurve=reference(mixed))
    book=str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic mixed curve gas',publication_order,effective_at FROM trading.books LIMIT 1;")
    terms={**context,**trade_booking(), 'operationId':str(uuid4()),'bookId':book,'definitionRevisionId':path['definitionRevisionId'],
        'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
        'physicalSides':[physical('daily','2021-01-01','2021-02-01'),physical('monthly','2021-02-01','2021-03-01')],'cashSides':[]}
    points=preview(mixed['definition'],id=mixed['id'],version=mixed['version'])['points']
    expected=sum(map(exact,points[:31]))+28*exact(points[31])-Fraction(59,10)
    valued=client.call('gas/composition/preview',terms)
    assert Fraction(valued['presentValue'])==expected,(valued,expected)
    booked=client.call('gas/composition/book',terms)
    assert client.call('gas/composition/book',terms)==booked
    opened=client.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']})
    assert opened['valuation']=={key:valued[key] for key in opened['valuation']}
    assert opened['terms']['physicalSides']==terms['physicalSides']
    shared={**context,'convention':'trading_workspace_v1'}
    facts=client.call('trading/positions',{'context':shared,'bookIds':[book],'portfolioIds':[],'offset':0,'limit':64})
    selected=next(p for p in facts['positions'] if p['tradeId']==booked['tradeId'])
    ticket=client.call('tickets/trade',{'context':context,'tradeId':selected['tradeId'],'bindingId':selected['bindingId'],'revision':selected['revision']})
    draft=ticket['draft']
    assert all('forecast_curve' in side['values'] for side in draft['sides'])
    choices=client.call('tickets/choices',{'context':context,'draft':draft,'side':'daily','field':'forecast_curve'})['choices']
    assert any(c['value']!='assigned' and json.loads(c['value'])['curveId']==mixed['id'] for c in choices)
    # Same contractual side, different published source: different pricing programs.
    changed=deepcopy(terms);changed['operationId']=str(uuid4());changed['physicalSides']=changed['physicalSides'][:1]
    changed['physicalSides'][0]['forecastCurve']=reference(alternate)
    alt=client.call('gas/composition/book',changed)
    alt_value=client.call('gas/composition/editor',{'context':context,'tradeId':alt['tradeId']})['valuation']
    alt_expected=sum(map(exact,preview(alternate['definition'],id=alternate['id'],version=alternate['version'])['points']))-Fraction(31,10)
    assert Fraction(alt_value['presentValue'])==alt_expected
    identities=query(f"SELECT count(DISTINCT s.economic_identity),count(DISTINCT s.pricing_binding_identity) FROM trading.gas_memberships m JOIN trading.sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE m.instrument_id IN('{booked['instrumentId']}','{alt['instrumentId']}') AND m.slot=0;")
    assert identities.splitlines()[-1]=='1|2',identities
    # Raw admission cannot reinterpret a source/query independently of the receipt.
    from check_booked_side_programs import runtime_query
    signature='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)'
    assert query(f"SELECT has_function_privilege('openetrm_runtime','{signature}','EXECUTE'),has_function_privilege('openetrm_risk_worker','{signature}','EXECUTE');")=='f|f'
    schedule=query(f"SELECT h.id FROM trading.gas_memberships m JOIN trading.side_schedules h ON(h.catalog_id,h.side_id)=(m.catalog_id,m.side_id) WHERE m.instrument_id='{booked['instrumentId']}' AND m.slot=0;")
    for source,mutation in [(None,''),(reference(alternate),''),(reference(mixed),f"ALTER TABLE trading.side_schedule_rows DISABLE TRIGGER USER;UPDATE trading.side_schedule_rows SET parameters=jsonb_set(parameters,'{{to}}','\"2021-01-03\"'::jsonb) WHERE schedule_id='{schedule}' AND local_key='gas/0/market';")]:
        encoded='NULL' if source is None else "'"+json.dumps(source).replace("'","''")+"'::jsonb"
        runtime_query(query,manifest,f"""SELECT security.use_market_context('{manifest['catalogId']}','{facts['context']['contextId']}');
          SELECT security.use_trading_context('{manifest['catalogId']}','2020-12-18T16:00:00Z');
          DO $reject$ DECLARE a market_data.gas_valuation_assignments;BEGIN
          SELECT x.* INTO STRICT a FROM market_data.gas_valuation_assignments x JOIN trading.gas_component_terms m
           ON(m.catalog_id,m.definition_revision_id,m.assignment_code)=(x.catalog_id,x.revision_id,x.code)
           WHERE m.instrument_id='{booked['instrumentId']}' AND m.slot=0;
          BEGIN
           PERFORM trading.require_gas_program_shape('{manifest['catalogId']}','{schedule}',0,'2021-01-01','2021-02-01','2021-02-01',31,NULL,a,NULL,'2021-01-01',{encoded});
           RAISE EXCEPTION 'wrong_source_or_query_accepted';
          EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_schedule_curve_source' THEN RAISE;END IF;END;
        END$reject$;""",prefix=f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;"+mutation,commit=False)
    assert query(f"SELECT has_function_privilege('openetrm_runtime','{signature}','EXECUTE');")=='f'
    client.call('profile',{'profile':'risk'})
    scope={'context':shared,'bookIds':[book],'portfolioIds':[]}
    risk=client.call('portfolio/risk/catalog',scope)
    assert len(risk['factors'])==32,risk
    factors=[f for f in risk['factors'] if f['curves']==['ng_daily']][:2]+[f for f in risk['factors'] if f['curves']==['ng_monthly']]
    assert len(factors)==3
    request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':risk['context']['contextId']},'detail':'holdings',
        'convention':risk['convention'],'numericalPolicy':risk['numericalPolicy'],'pricingPolicy':risk['pricingPolicy'],'stress':[],
        'bumps':[{'factor':f['factor'],'size':'0.1','convention':risk['bumpConvention']} for f in factors]}
    report=client.call('portfolio/risk',request)
    values=[position['values'] for factor in report['factors'] for position in factor['positions']]
    assert sorted(Fraction(value['delta']) for value in values if Fraction(value['delta']))==[2,2,3,3,28],values
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':request})
    run_worker();result=client.call('portfolio/risk/jobs/'+job['id'])
    assert result['state']=='completed' and financial(result['result'])==financial(report),result
    # Current authority also gates retained computed-source reads.
    query(f"CREATE POLICY synthetic_bound_curve_hidden ON market_data.custom_curve_versions AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(curve_id<>'{mixed['id']}');")
    try:
        client.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']},expected=422)
    finally:query('DROP POLICY synthetic_bound_curve_hidden ON market_data.custom_curve_versions;')
    print('PASS direct mixed curve: physical ticket choices, 59 daily schedule rows, exact native/monthly forecast pricing, distinct source identities, idempotent booking and retained worker risk parity',flush=True)
