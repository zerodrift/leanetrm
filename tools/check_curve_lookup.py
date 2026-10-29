"""Synthetic saved interpolation through booking, risk and retained workers."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from check_portfolio_risk import financial


def run_checks(url, manifest, query, run_worker):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'editor'})
    frame=initial_frame(client.call('workspace'),'2020-12-18')
    source=next(s for s in client.call('custom-curves')['sources'] if s['code']=='ng_daily')
    contracts=[c for c in source['contracts'] if '2021-01-01'<=c['deliveryFrom']<'2021-02-01']
    dates={'sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}
    parent={'code':'lookup_endpoints','name':'Synthetic lookup endpoints','output':'output','nodes':[
        {'id':'source','kind':'source','parameters':{'curve':'ng_daily'},'inputs':{}},
        {'id':'mapping','kind':'weighted_contracts','parameters':{'aggregation':'signed_sum_v1'},'inputs':{'source_a':'source'},
         'contractWeights':[{'outputPosition':i+1,'sourcePort':'source_a','contractId':c['id'],'weight':str(i+1)} for i,c in enumerate((contracts[0],contracts[-1]))]},
        {'id':'dates','kind':'dates','parameters':{'mode':'context',**dates},'inputs':{}},
        {'id':'sample','kind':'sample','parameters':{},'inputs':{'selection':'mapping','dates':'dates'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'sample'}}]}
    def save(graph):
        client.call('profile',{'profile':'editor'})
        return client.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':graph,'layout':{}})
    def publish(graph):
        saved=save(graph);client.call('profile',{'profile':'approver'})
        return client.call('custom-curves/publish',{'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation']})
    endpoints=publish(parent)
    preview=lambda graph,**extra:client.call('custom-curves/preview',{'snapshotId':frame['id'],'definition':graph,**extra})
    exact=lambda p:Fraction(p['price']['numerator'])/Fraction(p['price']['denominator'])
    left,right=map(exact,preview(parent)['points'])
    graph={'code':'lookup_daily','name':'Synthetic daily interpolation','output':'output','nodes':[
        {'id':'parent','kind':'published_source','parameters':{'selection':'all_output_points_v1'},'inputs':{},
         'publishedSource':{'catalogId':manifest['catalogId'],'curveId':endpoints['id'],'version':1,'outputNode':'output'}},
        {'id':'targets','kind':'dates','parameters':{'mode':'delivery_range','from':'2021-01-01','to':'2021-02-01',**dates},'inputs':{}},
        {'id':'lookup','kind':'lookup','parameters':{'axis':'delivery_start','method':'linear_price_actual_days_v1','maxGapDays':'30','leftBoundary':'reject','rightBoundary':'reject','implementation':'asset_curve_lookup_v1','output':'daily_delivery_v1'},'inputs':{'values':'parent','dates':'targets'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'lookup'}}]}
    expected_prices=[left+(right-left)*Fraction(i,30) for i in range(31)]
    assert list(map(exact,preview(graph)['points']))==expected_prices
    formula=client.call('custom-curves/format',{'definition':graph})['formula']
    compiled=client.call('custom-curves/compile',{'formula':formula,'code':graph['code'],'name':graph['name'],'snapshotId':frame['id']})['definition']
    assert list(map(exact,preview(compiled)['points']))==expected_prices
    bad=deepcopy(graph);bad['nodes'][2]['parameters']['maxGapDays']='29'
    assert client.call('custom-curves/preview',{'snapshotId':frame['id'],'definition':bad},expected=422)['code']=='curve_coverage'
    bad=deepcopy(graph);bad['nodes'][1]['parameters']['to']='2021-02-02'
    client.call('custom-curves/preview',{'snapshotId':frame['id'],'definition':bad},expected=422)
    # Publication also rejects invalid policies inserted outside the API.
    invalid=save(graph)
    query(f"UPDATE market_data.custom_curve_nodes SET parameters=parameters||'{{\"maxGapDays\":\"0\"}}'::jsonb WHERE curve_id='{invalid['id']}' AND node_id='lookup';")
    query(f"""DO $$ BEGIN
      BEGIN UPDATE market_data.custom_curve_versions SET state='published' WHERE curve_id='{invalid['id']}';
        RAISE EXCEPTION 'Invalid lookup was published';
      EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM NOT IN('recipe_lookup_inputs','Published versions require the publication function') THEN RAISE;END IF;END;
    END $$;""")
    graph['code']='lookup_daily_booked'
    saved=publish(graph)
    panel=preview(graph,id=saved['id'],version=1)
    assert list(map(exact,panel['points']))==expected_prices
    client.call('profile',{'profile':'trader'})
    book=str(uuid4());query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic interpolated delivery',publication_order,effective_at FROM trading.books LIMIT 1;")
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    index=next(i for i in client.call('swaps/catalog',context)['indices'] if i['curve']=='ng_daily' and i['definitionRevisionId']==manifest['revisionId'])
    reference=next(p['reference'] for p in index['customProjections'] if p['reference']['curveId']==saved['id'] and p['reference']['selection']=='exact_delivery_interval_v1')
    delivery={'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01'}
    payment={'frequency':'monthly','rule':'days_after_period_end','days':0,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    terms={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':book,'definitionRevisionId':index['definitionRevisionId'],
        'commodity':index['commodity'],'quantity':'31','fixedCoefficient':'-1','discounting':False,
        'fixed':{'price':'0','currency':index['currencyCode'],**delivery,'paymentSchedule':payment},
        'floating':{'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],**delivery,'paymentSchedule':payment,
                    'resetFrequency':'daily','averagingMethod':'arithmetic','customProjection':reference}}
    booked=client.call('swaps/book',terms)
    detail=client.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})
    expected=sum(expected_prices)
    assert Fraction(detail['valuation']['presentValue'])==expected
    resets=next(s for s in detail['valuation']['sides'] if s['kind']=='floating')['payments'][0]['resets']
    assert [Fraction(r['exactPrice']['numerator'])/Fraction(r['exactPrice']['denominator']) for r in resets]==expected_prices
    client.call('profile',{'profile':'risk'})
    scope={'context':{**context,'convention':'trading_workspace_v1'},'bookIds':[book],'portfolioIds':[]}
    catalog=client.call('portfolio/risk/catalog',scope)
    assert len(catalog['factors'])==2
    request={**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
        'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
        'bumps':[{'factor':f['factor'],'size':'0.1','convention':catalog['bumpConvention']} for f in catalog['factors']]}
    report=client.call('portfolio/risk',request)
    values=[next(p['values'] for p in f['positions'] if p['tradeId']==booked['tradeId']) for f in report['factors']]
    assert all(Fraction(v['base'])==expected for v in values)
    assert sorted(Fraction(v['delta']) for v in values)==[Fraction(31,2),Fraction(31)]
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':request})
    run_worker();result=client.call('portfolio/risk/jobs/'+job['id'])
    assert result['state']=='completed' and financial(result['result'])==financial(report)
    print('PASS saved curve lookup: policy validation, rational daily interpolation, formula roundtrip, booking, endpoint risk and retained worker parity',flush=True)
    # A containing-delivery lookup must stop demanding points whose resets are actual.
    client.call('profile',{'profile':'editor'})
    all_points=deepcopy(parent);all_points.update(code='lookup_all_days',name='Synthetic full daily parent')
    all_points['nodes'][1]['contractWeights']=[{'outputPosition':i+1,'sourcePort':'source_a','contractId':c['id'],'weight':'2'} for i,c in enumerate(contracts)]
    all_saved=publish(all_points)
    containing=deepcopy(graph);containing.update(code='lookup_containing',name='Synthetic containing daily child')
    containing['nodes'][0]['publishedSource']['curveId']=all_saved['id']
    containing['nodes'][2]['parameters']['method']='containing_delivery'
    containing_saved=publish(containing)
    client.call('profile',{'profile':'trader'})
    partial_book=str(uuid4());query(f"INSERT INTO trading.books SELECT catalog_id,'{partial_book}',portfolio_id,'Synthetic partly fixed lookup',publication_order,effective_at FROM trading.books LIMIT 1;")
    choices=client.call('swaps/catalog',context)
    index=next(i for i in choices['indices'] if i['curve']=='ng_daily' and i['definitionRevisionId']==manifest['revisionId'])
    partial_ref=next(p['reference'] for p in index['customProjections'] if p['reference']['curveId']==containing_saved['id'] and p['reference']['selection']=='exact_delivery_interval_v1')
    trade=client.call('swaps/book',{**terms,'bookId':partial_book,'operationId':str(uuid4()),'floating':{**terms['floating'],'customProjection':partial_ref}})
    later=initial_frame(client.call('workspace/navigation'),'2021-01-02')
    later_context={**context,'snapshotId':later['id']}
    detail=client.call('swaps/editor',{'context':later_context,'tradeId':trade['tradeId'],'revision':None})
    resets=next(s for s in detail['valuation']['sides'] if s['kind']=='floating')['payments'][0]['resets']
    assert [r['fixingState'] for r in resets]==['Actual']*3+['Projected']*28
    assert [Fraction(r['price']) for r in resets[:3]]==[10,20,30]
    forecasts=query(f"SELECT value FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id) WHERE(o.catalog_id,o.definition_revision_id,o.role,o.observed_at)=('{manifest['catalogId']}','{manifest['revisionId']}','trader_mark','2021-01-02 16:00:00+00') AND s.code LIKE 'custom_ng_daily_%' AND k.delivery_from>='2021-01-04' AND k.delivery_from<'2021-02-01';")
    partial_expected=60+2*sum(Fraction(v) for v in forecasts.splitlines())
    assert Fraction(detail['valuation']['presentValue'])==partial_expected
    client.call('profile',{'profile':'risk'})
    scope={'context':{**later_context,'convention':'trading_workspace_v1'},'bookIds':[partial_book],'portfolioIds':[]}
    catalog=client.call('portfolio/risk/catalog',scope)
    assert len(catalog['factors'])==28
    partial_request={**request,**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':catalog['context']['contextId']},
        'bumps':[{'factor':catalog['factors'][0]['factor'],'size':'0.1','convention':catalog['bumpConvention']}]}
    partial_report=client.call('portfolio/risk',partial_request)
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':partial_request})
    run_worker();result=client.call('portfolio/risk/jobs/'+job['id'])
    assert result['state']=='completed' and financial(result['result'])==financial(partial_report)
    print('PASS partly fixed saved lookup: expired unused parent points are pruned, actual resets retained and forecast risk/worker parity holds',flush=True)
