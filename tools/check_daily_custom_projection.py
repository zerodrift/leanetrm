"""Exact daily delivery mappings through ordinary PV, risk and retained workers."""
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
    assert len(contracts)==31
    # Reverse the output ordinals: delivery identity must determine mapping.
    definition={'code':'daily_connected_panel','name':'Synthetic daily connected panel','output':'output','nodes':[
        {'id':'source','kind':'source','parameters':{'curve':'ng_daily'},'inputs':{}},
        {'id':'mapping','kind':'weighted_contracts','parameters':{'aggregation':'signed_sum_v1'},'inputs':{'source_a':'source'},
         'contractWeights':[{'outputPosition':31-i,'sourcePort':'source_a','contractId':c['id'],'weight':'2'} for i,c in enumerate(contracts)]},
        {'id':'dates','kind':'dates','parameters':{'mode':'context','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'},'inputs':{}},
        {'id':'sample','kind':'sample','parameters':{},'inputs':{'selection':'mapping','dates':'dates'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'sample'}}]}
    def publish(graph,prior=None):
        client.call('profile',{'profile':'editor'})
        saved=client.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':graph,'layout':{},
            **({'id':prior['id'],'version':prior['version'],'generation':prior['generation'],'expectedState':prior['state']} if prior else {})})
        client.call('profile',{'profile':'approver'})
        return client.call('custom-curves/publish',{'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation']})
    saved=publish(definition)
    panel=client.call('custom-curves/preview',{'snapshotId':frame['id'],'id':saved['id'],'version':1,'definition':definition})
    expected=sum(Fraction(p['price']['numerator'])/Fraction(p['price']['denominator']) for p in panel['points'])
    client.call('profile',{'profile':'trader'})
    daily_book=str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{daily_book}',portfolio_id,'Synthetic daily projection',publication_order,effective_at FROM trading.books LIMIT 1;")
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog=client.call('swaps/catalog',context)
    index=next(i for i in catalog['indices'] if i['curve']=='ng_daily' and i['definitionRevisionId']==manifest['revisionId'])
    choice=next(p for p in index['customProjections'] if p['reference']['curveId']==saved['id'] and p['reference']['selection']=='exact_delivery_interval_v1')
    assert choice['reference']['position'] is None and choice['samplings']==[{'resetFrequency':'daily','averagingMethod':'arithmetic'}]
    delivery={'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01'}
    payment={'frequency':'monthly','rule':'days_after_period_end','days':0,'dayOfMonth':None,'dayBasis':'calendar_days',
             'shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    terms={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':daily_book,'definitionRevisionId':index['definitionRevisionId'],
        'commodity':index['commodity'],'quantity':'31','fixedCoefficient':'-1','discounting':False,
        'fixed':{'price':'0','currency':index['currencyCode'],**delivery,'paymentSchedule':payment},
        'floating':{'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],**delivery,'paymentSchedule':payment,
                    'resetFrequency':'daily','averagingMethod':'arithmetic','customProjection':choice['reference']}}
    period_query={'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'context':context,'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],
                  'curve':index['curve'],'index':index['index'],'resetFrequency':'daily','averagingMethod':'arithmetic',
                  'customProjection':choice['reference']}
    assert any(p['deliveryFrom']=='2021-01-01' for p in client.call('swaps/observation-options',period_query)['deliveryPeriods'])
    omitted={k:v for k,v in choice['reference'].items() if k!='position'}
    assert client.call('swaps/book',{**terms,'floating':{**terms['floating'],'customProjection':omitted}},expected=400)['code']=='request_format'
    booked=client.call('swaps/book',terms)
    assert client.call('swaps/book',terms)==booked
    again=client.call('swaps/book',{**terms,'operationId':str(uuid4())})
    assert again['tradeId']!=booked['tradeId']
    assert query(f"SELECT count(*) FROM trading.custom_curve_projection_bindings WHERE curve_id='{saved['id']}';")=='1'
    detail=client.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})
    assert Fraction(detail['valuation']['presentValue'])==expected
    by_contract={p['contracts'][0]['id']:Fraction(p['price']['numerator'])/Fraction(p['price']['denominator']) for p in panel['points']}
    resets=next(s for s in detail['valuation']['sides'] if s['kind']=='floating')['payments'][0]['resets']
    assert len(resets)==31
    for reset,contract in zip(resets,contracts):
        assert (reset['from'],reset['to'])==(contract['deliveryFrom'],contract['deliveryTo'])
        assert Fraction(reset['exactPrice']['numerator'])/Fraction(reset['exactPrice']['denominator'])==by_contract[contract['id']]
    assert detail['state']['terms']['floating']['customProjection']==choice['reference']
    for variant in ('missing','ambiguous'):
        graph=deepcopy(definition);graph.update(code='daily_'+variant,name='Synthetic '+variant+' mapping')
        if variant=='missing':graph['nodes'][1]['contractWeights'].pop()
        else:graph['nodes'][2]['parameters'].update(mode='prior_calendar',count='2',includeContext='true')
        invalid=publish(graph)
        client.call('profile',{'profile':'trader'})
        reference={**choice['reference'],'curveId':invalid['id']}
        assert client.call('swaps/observation-options',{**period_query,'customProjection':reference})['deliveryPeriods']==[], 'Incomplete or ambiguous daily custom mapping became selectable.'
        rejected=client.call('swaps/book',{**terms,'operationId':str(uuid4()),'floating':{**terms['floating'],'customProjection':reference}},expected=422)
        assert rejected['code'] in ('custom_projection','custom_curve_definition'),rejected
    client.call('profile',{'profile':'risk'})
    selected={**context,'convention':'trading_workspace_v1'}
    catalog=client.call('portfolio/risk/catalog',{'context':selected,'bookIds':[terms['bookId']],'portfolioIds':[]})
    request={'context':{**selected,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'bookIds':[terms['bookId']],
        'portfolioIds':[],'detail':'holdings','convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],
        'stress':[]}
    factors=[f for f in catalog['factors'] if 'ng_daily' in f['curves'] and f['contractId'] in {c['id'] for c in contracts}]
    assert len(factors)==31
    exposures=[]
    for offset in range(0,len(factors),16):
        request['bumps']=[{'factor':f['factor'],'size':'0.1','convention':catalog['bumpConvention']} for f in factors[offset:offset+16]]
        report=client.call('portfolio/risk',request)
        exposures.extend(p['values'] for f in report['factors'] for p in f['positions'] if p['tradeId']==booked['tradeId'] and Fraction(p['values']['delta']))
    assert len(exposures)==31 and all(Fraction(v['base'])==expected and Fraction(v['delta'])==2 and Fraction(v['gamma'])==0 for v in exposures)
    output=next(t['factor'] for t in catalog['scenarioTargets'] if t['coordinate']=='computed_output' and t['kind']=='parallel' and t['factor']['target']['curve']['code']==definition['code'])
    parent={**request,'stress':[{'factor':output,'size':'1','convention':catalog['stressConvention']}]}
    nested={**parent,'stress':[*parent['stress'],{'factor':factors[0]['factor'],'size':'0.5','convention':catalog['stressConvention']}]}
    parent_report=client.call('portfolio/risk',parent);report=client.call('portfolio/risk',nested)
    held=lambda result:next(p['values'] for p in result['factors'][0]['positions'] if p['tradeId']==booked['tradeId'])
    assert Fraction(held(parent_report)['base'])==expected+31 and Fraction(held(report)['base'])==expected+32
    assert client.call('portfolio/risk',parent)['factors']==parent_report['factors']
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':nested})
    successor=deepcopy(definition)
    for weight in successor['nodes'][1]['contractWeights']:weight['weight']='3'
    next_version=publish(successor,saved)
    client.call('custom-curves/activation',{'requestId':str(uuid4()),'curveId':saved['id'],'expectedRevision':0,
        'policy':'complete_effective_timeline_v1','reason':'Synthetic successor after queue',
        'segments':[{'effectiveFrom':'2020-12-01','effectiveTo':'2021-02-01','version':next_version['version'],'outputNode':definition['output']}]})
    effective=client.call('custom-curves/effective-preview',{'curveId':saved['id'],'snapshotId':frame['id'],'policy':'complete_effective_timeline_v1'})
    assert effective['recipe']['version']==2
    client.call('profile',{'profile':'trader'})
    assert Fraction(client.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})['valuation']['presentValue'])==expected
    amendment={**terms,'operationId':str(uuid4()),'tradeId':again['tradeId'],'expectedRevision':1,'effectiveAt':frame['at'],'reason':'Synthetic daily quantity amendment','quantity':'62'}
    client.call('swaps/amend',amendment)
    assert query(f"SELECT count(*) FROM trading.custom_curve_projection_bindings WHERE curve_id='{saved['id']}';")=='1'
    client.call('profile',{'profile':'risk'})
    run_worker();result=client.call('portfolio/risk/jobs/'+job['id'])
    assert result['state']=='completed', result
    assert financial(result['result'])==financial(report), {
        'immediateWork': report['work'], 'workerWork': result['result']['work'],
        'immediateGraphWork': [scenario['work'] for scenario in report['connectedScenarios']],
        'workerGraphWork': [scenario['work'] for scenario in result['result']['connectedScenarios']]}
    assert query(f"SELECT count(*) FROM risk.job_custom_curves WHERE job_id='{job['id']}' AND position IS NULL AND selection='exact_delivery_interval_v1';")=='1'
    client.call('profile',{'profile':'trader'})
    later=initial_frame(client.call('workspace/navigation'),'2021-01-02')
    later_context={**context,'snapshotId':later['id']}
    partly_fixed=client.call('swaps/editor',{'context':later_context,'tradeId':booked['tradeId'],'revision':None})
    resets=next(s for s in partly_fixed['valuation']['sides'] if s['kind']=='floating')['payments'][0]['resets']
    assert [r['fixingState'] for r in resets]==['Actual']*3+['Projected']*28
    assert [Fraction(r['price']) for r in resets[:3]]==[10,20,30]
    forecast_values=query(f"SELECT contract_id,value FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE(o.catalog_id,o.definition_revision_id,o.role,o.observed_at)=('{manifest['catalogId']}','{manifest['revisionId']}','trader_mark','2021-01-02 16:00:00+00') AND s.code LIKE 'custom_ng_daily_%';")
    prices={row.split('|')[0]:Fraction(row.split('|')[1])*2 for row in forecast_values.splitlines()}
    for reset,contract in zip(resets[3:],contracts[3:]):assert Fraction(reset['price'])==prices[contract['id']]
    partial_expected=60+sum(prices[c['id']] for c in contracts[3:])
    assert Fraction(partly_fixed['valuation']['presentValue'])==partial_expected
    client.call('profile',{'profile':'risk'})
    partial_scope={'context':{**later_context,'convention':'trading_workspace_v1'},'bookIds':[daily_book],'portfolioIds':[]}
    partial_catalog=client.call('portfolio/risk/catalog',partial_scope)
    assert len(partial_catalog['factors'])==28
    partial_request={**request,**partial_scope,'context':{**partial_scope['context'],'knowledgeMode':'retained','contextId':partial_catalog['context']['contextId']},
        'bumps':[{'factor':partial_catalog['factors'][0]['factor'],'size':'0.1','convention':partial_catalog['bumpConvention']}]}
    partial_report=client.call('portfolio/risk',partial_request)
    assert Fraction(held(partial_report)['base'])==partial_expected and Fraction(held(partial_report)['delta'])==2
    partial_job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':partial_request})
    run_worker();partial_result=client.call('portfolio/risk/jobs/'+partial_job['id'])
    assert partial_result['state']=='completed' and financial(partial_result['result'])==financial(partial_report)
    # One historical ancestor is consumed as a full scalar panel and through a
    # daily child subset. Shared January cells must have one sample identity.
    graph=deepcopy(definition);graph.update(code='overlapping_history',name='Synthetic shared historical daily panel')
    longer=[c for c in source['contracts'] if '2021-01-01'<=c['deliveryFrom']<'2021-03-01']
    assert len(longer)==59
    graph['nodes'][1]['contractWeights']=[{'outputPosition':59-i,'sourcePort':'source_a','contractId':c['id'],'weight':'2'} for i,c in enumerate(longer)]
    graph['nodes'][2]['parameters'].update(mode='prior_calendar',count='2',includeContext='true')
    graph['nodes'].insert(-1,{'id':'mean','kind':'mean','parameters':{'axis':'dates'},'inputs':{'values':'sample'}})
    graph['nodes'][-1]['inputs']['values']='mean'
    parent=publish(graph)
    child_definition={'code':'overlapping_child','name':'Synthetic shared daily child','output':'output','nodes':[
        {'id':'parent','kind':'published_source','parameters':{'selection':'all_output_points_v1'},'inputs':{},
         'publishedSource':{'catalogId':manifest['catalogId'],'curveId':parent['id'],'version':1,'outputNode':'output'}},
        {'id':'output','kind':'output','parameters':{},'inputs':{'values':'parent'}}]}
    child=publish(child_definition)
    subset=deepcopy(graph)
    subset['nodes'][1]['contractWeights']=[w for w in subset['nodes'][1]['contractWeights'] if w['contractId'] in {c['id'] for c in contracts}]
    smaller=publish(subset,parent)
    client.call('profile',{'profile':'trader'})
    choices=client.call('swaps/catalog',context)
    monthly=next(i for i in choices['indices'] if i['definitionRevisionId']==manifest['revisionId'] and i['fixingConvention']
        and any(p['reference']['curveId']==parent['id'] and p['reference']['position']==1 for p in i['customProjections']))
    scalar=next(p['reference'] for p in monthly['customProjections'] if p['reference']['curveId']==parent['id'] and p['reference']['position']==1)
    daily=next(i for i in choices['indices'] if i['curve']=='ng_daily' and i['definitionRevisionId']==manifest['revisionId'])
    child_reference=next(p['reference'] for p in daily['customProjections'] if p['reference']['curveId']==child['id'] and p['reference']['selection']=='exact_delivery_interval_v1')
    smaller_reference=next(p['reference'] for p in daily['customProjections'] if p['reference']['curveId']==smaller['id'] and p['reference']['version']==2 and p['reference']['selection']=='exact_delivery_interval_v1')
    overlap_book=str(uuid4());query(f"INSERT INTO trading.books SELECT catalog_id,'{overlap_book}',portfolio_id,'Synthetic overlapping projections',publication_order,effective_at FROM trading.books LIMIT 1;")
    child_trade=client.call('swaps/book',{**terms,'bookId':overlap_book,'operationId':str(uuid4()),'floating':{**terms['floating'],'customProjection':child_reference}})
    scalar_trade=client.call('swaps/book',{**terms,'bookId':overlap_book,'operationId':str(uuid4()),'commodity':monthly['commodity'],'quantity':'1',
        'floating':{**terms['floating'],'curve':monthly['curve'],'index':monthly['index'],'resetFrequency':'monthly','averagingMethod':'none','customProjection':scalar}})
    smaller_trade=client.call('swaps/book',{**terms,'bookId':overlap_book,'operationId':str(uuid4()),'floating':{**terms['floating'],'customProjection':smaller_reference}})
    client.call('profile',{'profile':'risk'})
    scope={'context':{**context,'convention':'trading_workspace_v1'},'bookIds':[overlap_book],'portfolioIds':[]}
    directory=client.call('portfolio/risk/catalog',scope)
    overlapping={**request,**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':directory['context']['contextId']},
        'bumps':[{'factor':directory['factors'][0]['factor'],'size':'0.1','convention':directory['bumpConvention']}]}
    shared=client.call('portfolio/risk',overlapping)
    assert {p['tradeId'] for p in shared['factors'][0]['positions']}=={child_trade['tradeId'],scalar_trade['tradeId'],smaller_trade['tradeId']}
    queued=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':overlapping})
    run_worker();finished=client.call('portfolio/risk/jobs/'+queued['id'])
    assert finished['state']=='completed' and financial(finished['result'])==financial(shared)
    print('PASS daily custom projections: per-reset delivery identity, exact weighted PV/nested risk, rejected gaps/duplicates, amendments, partly fixed PV/risk/worker and overlapping historical ancestors',flush=True)
