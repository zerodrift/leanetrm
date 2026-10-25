"""Qualified scheduled price authoring, using independent synthetic cash oracles."""
from copy import deepcopy
from datetime import date,timedelta
from fractions import Fraction as F
from uuid import uuid4
from preview_fixture import trade_booking


def run(c,context,manifest):
    catalog=c.call('swaps/catalog',context)
    daily=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    monthly=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    rule=dict(frequency='monthly',rule='day_of_following_month',days=None,dayOfMonth=18,dayBasis=None,shortMonth='reject',adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    def body(index,frequency,method,schedule):
        quantity=next(q['specification'] for q in index['quantityChoices'] if q['unit']=='MMBtu (IT)')
        fixed=dict(price='3',currency=index['currencyCode'],deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=rule)
        floating=dict(curve=index['curve'],index=index['index'],currency=index['currencyCode'],deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=rule,resetFrequency=frequency,averagingMethod=method,spread='0',observationSchedule=schedule)
        def side(fixed_side):return dict(basis='period_total',quantity=quantity,amount=None,allocation=None,power=None,priceApplication='period_average_times_quantity_v1',periods=[dict(amount='100',fixed=fixed if fixed_side else None,floating=None if fixed_side else floating)])
        return {**context,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'executedAt':None,'quantity':None,'fixedCoefficient':None,'discounting':False,'fixed':None,'floating':None,
          'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
          'scheduled':dict(implementation='independent_scheduled_financial_swap_v1',first=side(True),second=side(False))}
    calendar=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code='test_business')
    quantities=[dict(from_=str(date(2021,1,n)),to=str(date(2021,1,n)+timedelta(days=1)),quantity=dict(value=str(n),specification=next(q['specification'] for q in daily['quantityChoices'] if q['unit']=='MMBtu (IT)'))) for n in range(1,32)]
    quantities=[{('from' if k=='from_' else k):v for k,v in row.items()} for row in quantities]
    for method,expected in [('none',F(10)),('arithmetic',F(16)),('observation_arithmetic',F(33,2)),('calendar_days_previous',F(478,31)),('calendar_days_next',F(514,31)),('quantity_weighted',F(21))]:
        index=monthly if method=='none' else daily
        schedule=None if method in ('none','arithmetic') else dict(implementation='side_observation_schedule_v1',calendar=None if method=='quantity_weighted' else calendar,quantities=quantities if method=='quantity_weighted' else None)
        terms=body(index,'monthly' if method=='none' else 'daily',method,schedule)
        preview=c.call('swaps/preview',terms)
        assert F(preview['valuation']['presentValue'])==expected,(method,preview['valuation']['presentValue'])
        saved=c.call('swaps/book',terms)
        old=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=1))
        assert old['state']['terms']['scheduled']['second']['periods'][0]['floating'].get('observationSchedule')==schedule
        amendment=deepcopy(terms);amendment.update(operationId=str(uuid4()),tradeId=saved['tradeId'],expectedRevision=1,effectiveAt=old['asOf'],reason='Synthetic future observation-preserving amendment')
        amendment['scheduled']['second']['periods'][0]['floating']['spread']='0.1'
        c.call('swaps/amend',amendment)
        current=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=None))
        assert F(current['valuation']['presentValue'])==expected+10
        assert current['state']['terms']['scheduled']['second']['periods'][0]['floating'].get('observationSchedule')==schedule
        replay=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=1))
        assert F(replay['valuation']['presentValue'])==expected
        assert [p['tradeCashAmount'] for s in replay['valuation']['sides'] for p in s['payments']]==[p['tradeCashAmount'] for s in old['valuation']['sides'] for p in s['payments']]
    print('PASS all six scheduled observation conventions: independent cash, exact calendars/weights, booking, reopen, amendment and retained revision',flush=True)
    # A published synthetic mean supplies a concrete custom projection consumer.
    c.call('profile',{'profile':'editor'});library=c.call('custom-curves')
    definition=deepcopy(next(t['definition'] for t in library['templates'] if t['code']=='ng_average'))
    definition.update(code='synthetic_scheduled_projection',name='Synthetic scheduled projection')
    for node in definition['nodes']:
        if node['kind']=='source':node['parameters']['curve']='base_forward'
        if node['kind']=='select':node['parameters']['count']='1'
        if node['kind']=='dates':node['parameters'].update(count='2',includeContext='true')
    draft=c.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=context['snapshotId'],definition=definition,layout={}))
    c.call('profile',{'profile':'approver'});published=c.call('custom-curves/publish',dict(requestId=str(uuid4()),id=draft['id'],version=draft['version'],generation=draft['generation']))
    c.call('profile',{'profile':'trader'});fresh=c.call('swaps/catalog',context)
    index=next(i for i in fresh['indices'] if i['curve']=='base_forward')
    projection=next(p['reference'] for p in index['customProjections'] if p['reference']['curveId']==published['id'])
    terms=body(index,'monthly','none',None);terms['scheduled']['second']['periods'][0]['floating']['customProjection']=projection
    result=c.call('swaps/preview',terms);saved=c.call('swaps/book',terms)
    retained=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=None))
    assert retained['valuation']['presentValue']==result['valuation']['presentValue'] and retained['state']['terms']['scheduled']['second']['periods'][0]['floating']['customProjection']==projection
    print('PASS scheduled custom projection uses an exact published binding through preview, booking and retained editing',flush=True)
