"""Independent typed-factor cash oracles through ordinary authorized operations."""
from copy import deepcopy
from fractions import Fraction as F
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking,RCAT
import factor_swap_fixture as fixture


def run_checks(url,query,manifest,run_worker=None):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace');frame=next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    context=dict(snapshotId=frame['id'],convention='independent_side_financial_swap_v2',knowledgeMode='current')
    old=c.call('swaps/catalog',context)
    query(fixture.SQL)
    catalog=c.call('swaps/catalog',context)
    indices={i['curve']:i for i in catalog['indices'] if i['definitionRevisionId']==fixture.REVISION}
    assert set(indices)=={'factor_daily_gas','factor_daily_power'}
    power,gas=indices['factor_daily_power'],indices['factor_daily_gas']
    rule=dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    periods=[('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01')]
    def side(index,amounts,basis):
        return dict(basis=basis,quantity=index['quantityChoices'][0]['specification'],amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,
            periods=[dict(amount=amount,fixed=None,floating=dict(curve=index['curve'],index=index['index'],currency=index['currencyCode'],deliveryFrom=start,deliveryTo=end,paymentSchedule=rule,resetFrequency='daily',averagingMethod='arithmetic',spread='0')) for (start,end),amount in zip(periods,amounts)])
    options=catalog['factorOptions']
    factor=dict(value='7',numeratorQuantity=gas['quantityChoices'][0]['specification'],denominatorQuantity=power['quantityChoices'][0]['specification'],implementation=options['factorImplementation'],unitConversionImplementation=options['unitConversionImplementation'])
    body=dict(**context,operationId=str(uuid4()),bookId=catalog['books'][1]['id'],executedAt=None,quantity=None,fixedCoefficient=None,discounting=False,fixed=None,floating=None,definitionRevisionId=fixture.REVISION,commodity=power['commodity'],swapType='scheduled_financial_swap',firstCoefficient='1',scheduled=dict(implementation=options['swapImplementation'],factor=factor,first=side(power,['100','200'],'period_total'),second=side(gas,[None,None],options['quantityBasis'])),**trade_booking())
    value=c.call('swaps/preview',body)['valuation']
    # Jan power41.6*100 -gas3.16*700; Feb power44.55*200 -gas3.455*1400.
    assert F(value['presentValue'])==6021
    assert [F(p['resolvedQuantity']) for s in value['sides'] for p in s['payments']]==[100,200,700,1400]
    equivalent=deepcopy(body);equivalent['scheduled']['factor'].update(value='0.0035',numeratorQuantity=dict(revision=dict(catalogId=RCAT,revisionId=fixture.REFERENCE),code='factor_gas_bundle'),denominatorQuantity=dict(revision=dict(catalogId=RCAT,revisionId=fixture.REFERENCE),code='factor_power_slice'))
    assert c.call('swaps/preview',equivalent)['valuation']['presentValue']==value['presentValue']
    saved=c.call('swaps/book',equivalent);assert c.call('swaps/book',equivalent)==saved
    stored=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=None))
    assert stored['state']['terms']['scheduled']['factor']==equivalent['scheduled']['factor']
    assert stored['valuation']['presentValue']==value['presentValue']
    print('PASS factor swap: DB-configured equivalent units, independent unequal monthly quantities, one-time gas/power sizing, immutable selected factor and booking replay',flush=True)

    # Version changes are separate contracts, never an ordinary amendment.
    upgrade=deepcopy(body);upgrade.update(operationId=str(uuid4()),tradeId=saved['tradeId'],expectedRevision=1,effectiveAt=frame['at'],reason='Synthetic refused version change')
    upgrade['scheduled']['implementation']=catalog['scheduledOptions']['implementation'];upgrade['scheduled'].pop('factor')
    upgrade['commodity']=gas['commodity'];upgrade['scheduled']['first']=side(gas,['100','200'],'period_total');upgrade['scheduled']['second']=side(gas,['700','1400'],'period_total')
    c.call('swaps/preview',upgrade,expected=422)
    c.call('swaps/amend',upgrade,expected=422)
    c.call('swaps/book',{**body,'compiled':[]},expected=400)
    retained_old={**context,'knowledgeMode':'retained','contextId':old['context']['contextId']}
    assert not any(i['definitionRevisionId']==fixture.REVISION for i in c.call('swaps/catalog',retained_old)['indices'])
    for change in ('missing','negative','zero','wrong_revision','wrong_commodity','manual_second','misaligned','legacy'):
        invalid=deepcopy(body);invalid['operationId']=str(uuid4());f=invalid['scheduled']['factor']
        if change=='missing':del invalid['scheduled']['factor']
        elif change=='negative':f['value']='-7'
        elif change=='zero':f['value']='0'
        elif change=='wrong_revision':f['numeratorQuantity']=old['indices'][0]['quantityChoices'][0]['specification']
        elif change=='wrong_commodity':f['numeratorQuantity']=f['denominatorQuantity']
        elif change=='manual_second':invalid['scheduled']['second']['periods'][0]['amount']='700'
        elif change=='misaligned':invalid['scheduled']['second']['periods']=invalid['scheduled']['second']['periods'][1:]
        else:invalid['scheduled']['implementation']=catalog['scheduledOptions']['implementation']
        c.call('swaps/preview',invalid,expected=422)
        c.call('swaps/book',invalid,expected=422)
    independent=deepcopy(body)
    for p in independent['scheduled']['second']['periods']:p['floating']['paymentSchedule']={**rule,'days':46}
    discounted=c.call('swaps/preview',{**independent,'discounting':True})['valuation']
    assert discounted['sides'][0]['payments'][0]['paymentDate']!=discounted['sides'][1]['payments'][0]['paymentDate']
    assert F(discounted['presentValue'])==sum((F(p['tradeCashAmount'])*F(p['discountFactor']) for s in discounted['sides'] for p in s['payments']),F())
    # Daily source shocks are sized in each quote's native quantity; the factor
    # does not change with marks. Check every factor against independent amounts.
    c.call('profile',{'profile':'risk'})
    selected={**context,'convention':'trading_workspace_v1'}
    rc=c.call('portfolio/risk/catalog',dict(context=selected,bookIds=[body['bookId']],portfolioIds=[]))
    request=dict(context={**selected,'knowledgeMode':'retained','contextId':rc['context']['contextId']},bookIds=[body['bookId']],portfolioIds=[],detail='holdings',bumps=[dict(factor=f['factor'],size='0.1',convention=rc['bumpConvention']) for f in rc['factors']],stress=[],convention=rc['convention'],numericalPolicy=rc['numericalPolicy'],pricingPolicy=rc['pricingPolicy'])
    all_bumps=request['bumps'];results=[]
    for start in range(0,len(all_bumps),16):
        request['bumps']=all_bumps[start:start+16]
        risk=c.call('portfolio/risk',request);results.extend(risk['factors'])
    assert len(results)==118
    totals={'power':F(),'gas':F()}
    from datetime import date
    for item in results:
        values=next(p['values'] for p in item['positions'] if p['tradeId']==saved['tradeId'])
        assert F(values['base'])==6021 and F(values['gamma'])==0
        factor=item['factor'];commodity='power' if factor['curves']==['factor_daily_power'] else 'gas'
        month=date.fromisoformat(factor['deliveryFrom']).month
        expected=F(100,31) if month==1 else F(200,28)
        if commodity=='gas':expected*=-7
        assert F(values['delta'])==expected and F(values['upPnl'])==expected/10 and F(values['downPnl'])==-expected/10
        totals[commodity]+=expected
    assert totals==dict(power=300,gas=-2100)
    if run_worker:
        operation=dict(requestId=str(uuid4()),request=request)
        queued=c.call('portfolio/risk/jobs',operation);assert c.call('portfolio/risk/jobs',operation)['id']==queued['id']
        run_worker()
        finished=c.call('portfolio/risk/jobs/'+queued['id'])
        assert finished['state']=='completed',finished
        assert finished['result']['factors']==risk['factors']
    c.call('profile',{'profile':'trader'})
    partial_frame=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    partial=c.call('swaps/editor',dict(context={**context,'snapshotId':partial_frame['id']},tradeId=saved['tradeId'],revision=None))['valuation']
    assert F(partial['presentValue'])==6066
    assert {r['fixingState'] for leg in partial['sides'] for p in leg['payments'] for r in p['resets']}=={'Projected','Actual'}
    # Monthly actual averages × the resolved per-period quantities, not a sum of
    # hourly price/quantity products. Four gross determinations and cash receipts.
    frames=c.call('workspace')['frames']
    after_frame=next(f for f in frames if f['at'].startswith('2021-04-05'))
    after={**context,'snapshotId':after_frame['id']};version=0
    for slot,amounts in [(0,['4470','9530']),(1,['-2429','-5271'])]:
        for ordinal,amount in enumerate(amounts):
            p=dict(context=after,tradeId=saved['tradeId'],expectedRevision=1,membershipOrdinal=slot,paymentOrdinal=ordinal,currency=value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
            preview=c.call('swaps/settlement/preview',p)
            assert F(preview['calculation']['amount'])==F(amount)
            command=dict(preview=p,operationId=str(uuid4()),expectedVersion=version,previewHash=preview['previewHash'],duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None)
            determination=c.call('swaps/settlement/determine',command);assert c.call('swaps/settlement/determine',command)==determination;version+=1
            payment=dict(context=after,tradeId=saved['tradeId'],operationId=str(uuid4()),expectedRevision=1,expectedVersion=version,determinationId=determination['determinationId'],amount=amount,currency=value['currency'],effectiveAt=after_frame['at'],reference='Synthetic factor settlement',reversesId=None)
            receipt=c.call('swaps/settlement/payment',payment);assert c.call('swaps/settlement/payment',payment)==receipt;version+=1
    inspection=c.call('swaps/settlement/inspect',dict(context=after,tradeId=saved['tradeId'],revision=None,duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1'))
    assert F(inspection['valuation']['netCash'])==6300 and F(inspection['valuation']['remainingValue'])==0
    # A March MW schedule uses 743 actual New York hours, and both sides inherit
    # its final retained interval (04:00 UTC), including the derived gas side.
    clocked=deepcopy(body);clocked['operationId']=str(uuid4())
    mw=next(q['specification'] for q in catalog['scheduledOptions']['powerQuantities'] if q['definitionRevisionId']==fixture.REVISION)
    ny=next(q['reference'] for q in catalog['scheduledOptions']['clocks'] if q['definitionRevisionId']==fixture.REVISION and q['reference']['code']=='synthetic_new_york')
    clocked['scheduled']['first'].update(basis='selected_interval_power',quantity=mw,power=dict(clock=ny,product='all_hours',startHour=None,endHour=None,saturday=None,calendar=None))
    for slot,name in enumerate(('first','second')):
        leg=clocked['scheduled'][name];leg['periods']=leg['periods'][:1];leg['periods'][0]['amount']='1' if slot==0 else None
        leg['periods'][0]['floating'].update(deliveryFrom='2021-03-01',deliveryTo='2021-04-01')
    cv=c.call('swaps/preview',clocked)
    assert [p['resolvedQuantity'] for s in cv['valuation']['sides'] for p in s['payments']]==['743','5201']
    assert cv['quantityInputs'][0]['periods'][0]['deliveryHours']=='743'
    booked_clock=c.call('swaps/book',clocked)
    for slot in (0,1):
        p=dict(context=after,tradeId=booked_clock['tradeId'],expectedRevision=1,membershipOrdinal=slot,paymentOrdinal=0,currency=value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
        preview=c.call('swaps/settlement/preview',p)
        assert preview['calculation']['completion']['at'].startswith('2021-04-01T04:00:00')
        assert preview['calculation']['completion']['source']=='retained_quantity_interval_end_v1'
    # An open future factor trade supplies live axes for a settlement-aware
    # report. Paid and finally fixed positions must have zero price sensitivity.
    future=deepcopy(body);future.update(operationId=str(uuid4()))
    for name in ('first','second'):
        for n,p in enumerate(future['scheduled'][name]['periods']):
            p['floating'].update(deliveryFrom=['2021-05-01','2021-06-01'][n],deliveryTo=['2021-06-01','2021-07-01'][n])
    opened=c.call('swaps/book',future)
    c.call('profile',{'profile':'risk'})
    latest={**after,'convention':'trading_workspace_v1'}
    latest_catalog=c.call('portfolio/risk/catalog',dict(context=latest,bookIds=[body['bookId']],portfolioIds=[]))
    after_request={**request,'context':{**latest,'knowledgeMode':'retained','contextId':latest_catalog['context']['contextId']},'bumps':[dict(factor=f['factor'],size='0.1',convention=latest_catalog['bumpConvention']) for f in latest_catalog['factors'][:2]]}
    after_risk=c.call('portfolio/risk',after_request)
    for factor in after_risk['factors']:
        for position in factor['positions']:
            if position['tradeId']==saved['tradeId']:assert all(F(position['values'][key])==0 for key in ('base','up','down','delta','gamma'))
            if position['tradeId']==booked_clock['tradeId']:assert F(position['values']['delta'])==F(position['values']['gamma'])==0 and F(position['values']['base'])==F('16479.74')
    c.call('profile',{'profile':'trader'})
    # Warm retained reads must still recheck current reference authority.
    warm={**context,'knowledgeMode':'retained','contextId':stored['context']['contextId']}
    c.call('swaps/catalog',warm)
    scope=f"catalog_id='{manifest['referenceCatalogId']}' AND tenant_id='{manifest['tenantId']}' AND workspace_id='{manifest['workspaceId']}'"
    access=query('SELECT access_mode FROM security.reference_catalog_scopes WHERE '+scope+';')
    try:
        query('DELETE FROM security.reference_catalog_scopes WHERE '+scope+';')
        c.call('swaps/catalog',warm,expected=403)
        c.call('swaps/preview',body,expected=403)
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}','{access}');")
    print('PASS factor integration: prior knowledge invisibility, explicit refusals, discounting, 118 independent native source shocks, retained worker parity, gross actual settlement/cash and DST-derived completion',flush=True)
