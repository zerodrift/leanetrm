"""Exercise exact contract terms through the real authored-curve HTTP consumer."""
from copy import deepcopy
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url, query, manifest):
    c=PreviewClient(url);boot=c.bootstrap();c.call('profile',{'profile':'editor'})
    workspace=c.call('workspace');original=set(workspace['initialFrameIds'])
    frames=[f for f in workspace['frames'] if f['id'] in original]
    live=next(f for f in frames if f['at'].startswith('2020-12-18T16:00:00'))
    fixed=next(f for f in frames if f['at'].startswith('2021-01-15T08:00:00'))
    catalog=c.call('custom-curves')
    template=deepcopy(next(t['definition'] for t in catalog['templates'] if t['code']=='ng_prompt'))
    template.update(code='http_terms_exchange',name='Synthetic exchange contract terms')
    exchange=c.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=live['id'],definition=template,layout={}))

    def selected(saved, snapshot=live):
        choices=c.call('contract-price-terms?snapshotId='+snapshot['id'])
        curve=next(v for v in choices['curves'] if v['id']==saved['id'] and v['version']==saved['version'])
        return curve,curve['sources'][0]

    def terms(source,settlement,publication=None,index=None):
        return dict(nodeId=source['nodeId'],scope='curve',contractId=None,mapping='same_delivery_period_v1',mappings=[],
                    settlement=settlement,publicationId=publication['key']['id'] if publication else None,
                    publicationVersion=publication['key']['version'] if publication else None,index=index,
                    observationRule='single_assessment_v1',pendingPolicy='require_final_v1',projectionPolicy=None)

    def request(saved,setting,snapshot=live):
        return dict(snapshotId=snapshot['id'],curveId=saved['id'],version=saved['version'],terms=setting)

    _,source=selected(exchange);official=terms(source,'exchange_final_v1')
    original_price=c.call('custom-curves/preview',dict(snapshotId=live['id'],definition=template))
    checked=c.call('contract-price-terms/preview',request(exchange,official))
    assert checked['preview']['rows']==original_price['rows'],checked
    save=dict(request(exchange,official),requestId=str(uuid4()),generation=exchange['generation'])
    saved=c.call('contract-price-terms/save',save)
    assert c.call('contract-price-terms/save',save)==saved,'Lost-response retry changed the accepted receipt'
    changed=deepcopy(save);changed['terms']['settlement']='derived_inputs_v1'
    assert c.call('contract-price-terms/save',changed,expected=409)['code']=='custom_curve_conflict'
    reloaded,source=selected(saved)
    assert source['mapping']=='same_delivery_period_v1' and len(source['mappings'])==len(source['contracts'])
    assert source['terms']['default']['settlement']=='exchange_final_v1'
    formula=c.call('custom-curves/format',{'definition':saved['definition']})['formula']
    compiled=c.call('custom-curves/compile',dict(formula=formula,code=saved['definition']['code'],name=saved['definition']['name'],snapshotId=live['id']))['definition']
    assert next(n for n in compiled['nodes'] if n['id']==source['nodeId'])['contractTerms']==source['terms']

    native=next(s for s in catalog['sources'] if s['code']=='base_forward')
    target=native['contracts'][0]['id']
    def node(id,kind,parameters=None,inputs=None,**rest):return dict(id=id,kind=kind,parameters=parameters or {},inputs=inputs or {},**rest)
    definition=dict(code='http_terms_forward',name='Synthetic published contract terms',output='out',nodes=[
        node('src','source',{'curve':'base_forward'}),
        node('map','weighted_contracts',{'aggregation':'signed_sum_v1'},{'source_a':'src'},contractWeights=[dict(outputPosition=1,sourcePort='source_a',contractId=target,weight='1')]),
        node('dates','dates',{'mode':'context','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}),
        node('sample','sample',inputs={'selection':'map','dates':'dates'}),node('out','output',inputs={'values':'sample'})])
    forward=c.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=live['id'],definition=definition,layout={}))
    _,source=selected(forward)
    publications=c.call('price-publications?snapshotId='+live['id'])
    index=next(i for i in publications['indices'] if i['key']['code']=='test_price' and i['quote']['code']==source['quote'])
    publication=dict(key=dict(catalogId=publications['catalogId'],id=str(uuid4()),version=1),references=publications['references'],
        publisher=dict(key=dict(catalogId=publications['catalogId'],id=str(uuid4()),version=1),name='Synthetic contract check publisher'),
        name='Synthetic contract check monthly',commodities=[index['commodity']],
        indices=[{k:index[k] for k in ('key','commodity','location','quote','unit')}|dict(symbol='HTTP_SYNTH',statistic='monthly_index')],
        calendar=publications['calendars'][0]['reference'],clock=publications['clocks'][0]['reference'],implementation='gas_monthly_explicit_v1')
    publication=c.call('price-publications/save',dict(requestId=str(uuid4()),snapshotId=live['id'],expectedVersion=0,definition=publication))
    # The publication save returns the exact immutable domain definition.
    if 'definition' in publication:publication=publication['definition']
    occurrence=dict(publication=publication['key'],assessmentFrom='2020-12-28T12:00:00Z',assessmentTo='2020-12-30T12:00:00Z',assessmentDate=source['contracts'][0]['deliveryFrom'],
        periods=[dict(from_=source['contracts'][0]['deliveryFrom'],to=source['contracts'][0]['deliveryTo'])],expectedReleaseAt='2020-12-31T14:00:00Z')
    occurrence['periods'][0]['from']=occurrence['periods'][0].pop('from_')
    occurrence_id=str(uuid4())
    c.call('price-publications/occurrences/save',dict(requestId=str(uuid4()),snapshotId=live['id'],id=occurrence_id,occurrence=occurrence))
    setting=terms(source,'published_index_v1',publication,index['key']['code'])
    checked=c.call('contract-price-terms/preview',request(forward,setting))
    assert checked['preview']['dependencies'],checked
    save=dict(request(forward,setting),requestId=str(uuid4()),generation=forward['generation'])
    saved=c.call('contract-price-terms/save',save)
    assert c.call('contract-price-terms/save',save)==saved
    _,source=selected(saved)
    shifted=deepcopy(setting);shifted['mapping']='explicit_contracts_v1'
    shifted['mappings']=[dict(targetContractId=m['id'],sourceContractId=source['contracts'][1]['id'] if i==0 else m['id']) for i,m in enumerate(source['contracts'])]
    mapped=c.call('contract-price-terms/preview',request(saved,shifted))
    receipt=next(m for m in mapped['mappings'] if m['targetContractId']==target)
    assert receipt['sourceContractId']==source['contracts'][1]['id'] and receipt['targetFrom']!=receipt['sourceFrom'],receipt
    assert mapped['preview']['summary']['firstPeriod']['from']==source['contracts'][0]['deliveryFrom']
    single=deepcopy(shifted);single.update(scope='contract',contractId=target,mappings=[shifted['mappings'][0]])
    saved=c.call('contract-price-terms/save',dict(request(saved,single),requestId=str(uuid4()),generation=saved['generation']))
    _,scoped=selected(saved)
    assert next(m for m in scoped['mappings'] if m['targetContractId']==target)['sourceContractId']==source['contracts'][1]['id']
    saved=c.call('contract-price-terms/save',dict(request(saved,setting),requestId=str(uuid4()),generation=saved['generation']))
    _,preserved=selected(saved)
    assert next(m for m in preserved['mappings'] if m['targetContractId']==target)['sourceContractId']==source['contracts'][1]['id'],'Whole-curve defaults discarded an explicit contract replacement'
    conflicting=deepcopy(setting);conflicting['mapping']='explicit_contracts_v1'
    conflicting['mappings']=[dict(targetContractId=m['id'],sourceContractId=m['id']) for m in source['contracts']]
    refused=c.call('contract-price-terms/save',dict(request(saved,conflicting),requestId=str(uuid4()),generation=saved['generation']),expected=422)
    assert refused['code']=='custom_curve_definition',refused
    _,unchanged=selected(saved)
    assert unchanged['mappings']==preserved['mappings'],'Rejected default edit changed a retained contract exception'
    restore=deepcopy(single);restore['mappings']=[dict(targetContractId=target,sourceContractId=target)]
    saved=c.call('contract-price-terms/save',dict(request(saved,restore),requestId=str(uuid4()),generation=saved['generation']))
    actual=c.call('contract-price-terms/preview',request(saved,setting,fixed))
    actual_row=next(row for row in actual['mappings'] if row['targetContractId']==target)
    assert actual_row['occurrenceId']==occurrence_id and actual_row['expectedPublicationAt'].startswith('2020-12-31T14:00:00')
    assert actual_row['fixingAt']!=actual_row['expectedPublicationAt']
    assert actual['preview']['rows'][0][1]=='3.5',actual
    assert actual['preview']['dependencies']==[],actual
    assert all(i['fixedValue'] for i in actual['preview']['inputs']),actual
    # Ordinary saved-graph preview is the same executable consumer, including final-only behavior.
    ordinary=c.call('custom-curves/preview',dict(snapshotId=fixed['id'],definition=saved['definition']))
    assert ordinary['rows']==actual['preview']['rows'] and ordinary['dependencies']==[]
    # The bounded calendar planner and direct candidate path must admit the same
    # exact source terms, including the fixing series after publication.
    for frame in (live,fixed):
        calendar_request=dict(curve=None,timeZone='UTC',session=frame['close']['session'],definition=saved['definition'])
        calendar=c.call('market/dates',calendar_request)
        assert frame['at'][:10] in {row['date'] for row in calendar['dates']},calendar
        latest=c.call('market/dates',calendar_request|dict(maximumDates=1))
        assert latest['dates']==calendar['dates'][-1:],(calendar,latest)
    c.call('profile',{'profile':'viewer'})
    c.call('contract-price-terms/save',save,expected=403)
    print('PASS contract terms HTTP: official forward preservation, exact publication fixes, shifted source mapping, zero fixed-price quote risk, bounded calendar parity, formula round-trip, draft persistence, authorized idempotent replay and changed-request refusal',flush=True)
