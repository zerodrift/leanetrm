"""Synthetic checks for independent swap sides, spreads and reviewed par entry."""
from copy import deepcopy
from fractions import Fraction
from decimal import Decimal, localcontext, ROUND_HALF_EVEN
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url, query=None, client=None):
    c=client or PreviewClient(url)
    if client is None:c.bootstrap()
    c.call('profile',{'profile':'trader'})
    w=c.call('workspace');f=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':f['id'],'knowledgeMode':'current','convention':'independent_side_financial_swap_v2'}
    catalog=c.call('swaps/catalog',context)
    daily=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    monthly=daily
    def side(i,spread,frequency):
        return {'curve':i['curve'],'index':i['index'],'currency':i['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','firstFixingAt':'2020-12-31T12:00:00Z',
            'paymentSchedule':{'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'},
            'resetFrequency':frequency,'averagingMethod':'arithmetic' if frequency=='daily' else 'none','spread':spread}
    right=side(daily,'0.2','daily');left=side(monthly,'0.1','daily')
    draft={'context':context,'definitionRevisionId':daily['definitionRevisionId'],'commodity':daily['commodity'],'quantity':{'value':'100','specification':daily['quantityChoices'][0]['specification']},'swapType':'financial_floating_floating','firstCoefficient':'-1','fixedCoefficient':None,'fixed':None,'firstFloating':left,'floating':right,'discounting':False}
    priced=c.call('swaps/price',draft)
    def review(value):
        assert value['quantityApplication']=='total_per_side_delivery_period_v1'
        with localcontext() as numeric:
            numeric.prec=80
            total=Fraction(0)
            for s in value['sides']:
                assert len(s['payments'])==1
                for p in s['payments']:
                    assert p['resolvedQuantity']==value['quantity']
                    cash=Fraction(p['cashAmount'])*Fraction(s['coefficient'])*Fraction(value['quantity'])
                    assert Fraction(p['tradeCashAmount'])==cash
                    assert Fraction(p['tradePresentValue'])==cash*Fraction(p['discountFactor'])
                    total+=Fraction(p['tradePresentValue'])
            assert total==Fraction(value['presentValue'])
    review(priced['valuation'])
    review(c.call('swaps/price',{**draft,'discounting':True,'firstFloating':{**left,'paymentSchedule':{**left['paymentSchedule'],'days':25}}})['valuation'])
    a=Decimal(c.call('swaps/price',{**draft,'firstFloating':{**left,'spread':'0'},'floating':{**right,'spread':'0'}})['valuation']['presentValue'])
    assert Decimal(priced['valuation']['presentValue'])==a+10
    print('PASS swap composition: each floating spread contributes once after averaging',flush=True)
    wide=c.call('swaps/price',{**draft,'floating':{**right,'spread':'1001.003'}});assert Decimal(wide['valuation']['presentValue'])==Decimal('100090.3')
    body={k:v for k,v in draft.items() if k not in ('context','quantity')};body.update(context);body.update(trade_booking());body.update(quantity='100',bookId=catalog['books'][0]['id'],operationId=str(uuid4()))
    preview=c.call('swaps/preview',body);booked=c.call('swaps/book',body)
    assert c.call('swaps/book',body)==booked
    spelling=deepcopy(body);spelling['firstFloating']['spread']='0.10';spelling['floating']['spread']='0.20';assert c.call('swaps/book',spelling)==booked
    detail=c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})
    review(detail['valuation']);review(preview['valuation'])
    assert detail['valuation']['presentValue']==preview['valuation']['presentValue']==priced['valuation']['presentValue']
    assert detail['state']['terms']['firstFloating']['spread']=='0.1' and detail['state']['terms']['floating']['spread']=='0.2'
    assert [s['kind'] for s in detail['valuation']['sides']]==['floating','floating']
    conflict=deepcopy(body);conflict['firstFloating']['spread']='0.3';c.call('swaps/book',conflict,expected=409)
    amendment={**body,'operationId':str(uuid4()),'tradeId':booked['tradeId'],'expectedRevision':1,'effectiveAt':catalog['asOf'],'reason':'Synthetic spread amendment','firstFloating':{**left,'spread':'0.3'}}
    c.call('swaps/amend',amendment)
    old=c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':1})
    assert old['state']['terms']['firstFloating']['spread']=='0.1' and not old['canAmend']
    review(old['valuation'])
    review(c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})['valuation'])
    print('PASS swap composition: booking replay, conflict and retained spread amendments',flush=True)
    fixed={'price':'','currency':daily['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':right['paymentSchedule']}
    par_draft={**draft,'swapType':'financial_fixed_floating','firstCoefficient':None,'fixedCoefficient':'-1','fixed':fixed,'firstFloating':None,'parRatePolicy':{'implementation':'par_fixed_price_to_even_v1','decimalPlaces':6,'tieRule':'to_even'}}
    different_periods={**par_draft,'parRatePolicy':None,'fixed':{**fixed,'price':'3','deliveryFrom':'2021-02-01','deliveryTo':'2021-03-01'}}
    different=c.call('swaps/price',different_periods)['valuation'];review(different)
    assert different['presentValue']=='36'
    assert different['sides'][0]['payments'][0]['deliveryFrom']=='2021-02-01' and different['sides'][1]['payments'][0]['deliveryFrom']=='2021-01-01'
    assert [p['tradeCashAmount'] for side in different['sides'] for p in side['payments']]==['-300','336']
    negative=c.call('swaps/price',{**different_periods,'fixed':{**different_periods['fixed'],'price':'-3'}})['valuation'];review(negative)
    assert negative['sides'][0]['payments'][0]['tradeCashAmount']=='300'
    print('PASS swap quantity: independent delivery months each consume one total; daily observations never repeat it; negative agreed prices retain signed cash',flush=True)
    par=c.call('swaps/price',par_draft)
    assert par['resolvedFixedPrice']=='3.36' and par['valuation']['presentValue']=='0' and par['parRate']['policy']=='par_fixed_price_to_even_v1'
    assert c.call('swaps/price',{**par_draft,'fixed':{**fixed,'price':'0'},'parRatePolicy':None})['resolvedFixedPrice']=='0'
    c.call('swaps/price',{**par_draft,'parRatePolicy':None},expected=422)
    c.call('swaps/draft-risk',{'draft':par_draft,'bumpPolicy':par['bumpPolicy'],'numericalPolicy':par['numericalPolicy']},expected=422)
    print('PASS swap composition: explicit par policy solves the fixed price, blank agreed price fails and explicit zero remains zero, risk requires fixed reviewed terms',flush=True)
    c.call('profile',{'profile':'risk'})
    try:c.call('swaps/book',{**body,'operationId':str(uuid4())},expected=403)
    finally:c.call('profile',{'profile':'trader'})
    incompatible={**draft,'firstFloating':{**left,'currency':'unknown'}};c.call('swaps/price',incompatible,expected=422)
    print('PASS swap composition: write authority and exact side compatibility remain enforced',flush=True)
    # Both occurrences may reference one reusable side without losing either membership.
    zero={**body,'operationId':str(uuid4()),'firstFloating':{**left,'spread':'0'},'floating':{**right,'spread':'0'}}
    z=c.call('swaps/book',zero);zv=c.call('swaps/editor',{'context':context,'tradeId':z['tradeId'],'revision':None})['valuation']
    assert zv['presentValue']=='0' and len(zv['sides'])==2
    # Distinct indices/curves in the same exact definition revision retain their own factors.
    base=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    local=next(i for i in catalog['indices'] if i['curve']=='local_gas')
    distinct={**draft,'definitionRevisionId':base['definitionRevisionId'],'firstFloating':side(base,'0','monthly'),'floating':side(local,'0','monthly')}
    p=c.call('swaps/price',distinct)
    def point(curve):return Decimal(next(p for p in c.call('evaluate',{'snapshotId':f['id'],'curve':curve})['points'] if p['deliveryFrom']=='2021-01-01')['value'])
    assert Decimal(p['valuation']['presentValue'])==(point('local_gas')-point('base_forward'))*100
    r=c.call('swaps/draft-risk',{'draft':{**distinct,'context':{**context,'knowledgeMode':'retained','contextId':p['context']['contextId']}},'bumpPolicy':p['bumpPolicy'],'numericalPolicy':p['numericalPolicy']})
    assert any(Decimal(x['delta'])==0 for x in r['factors']) and any(Decimal(x['delta'])==100 for x in r['factors'])
    print('PASS swap composition: reusable opposing memberships and independent index factor repricing',flush=True)
    par_book={**body,'operationId':str(uuid4()),'swapType':'financial_fixed_floating','firstCoefficient':None,'fixedCoefficient':'-1','firstFloating':None,'fixed':{**fixed,'price':par['resolvedFixedPrice']}}
    pb=c.call('swaps/book',par_book)
    pv=c.call('swaps/editor',{'context':context,'tradeId':pb['tradeId'],'revision':None})
    assert pv['valuation']['presentValue']=='0' and pv['state']['terms']['floating']['spread']=='0.2'
    c.call('swaps/amend',{**par_book,'operationId':str(uuid4()),'tradeId':pb['tradeId'],'expectedRevision':1,'effectiveAt':catalog['asOf'],'reason':'Synthetic zero spread selection','floating':{**right,'spread':'0'}})
    c.call('swaps/preview',{**amendment,'expectedRevision':2,'firstFloating':{**left,'spread':'0.30'}},expected=422)
    print('PASS swap composition: reviewed par quote books concrete terms and zero-spread amendments preserve legacy admission',flush=True)
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    shared={**context,'convention':'trading_workspace_v1'}
    rows=c.call('trading/blotter',{'context':shared,'view':view})['holdings']
    measures=[field+suffix for field in ('executionPrice','mark','fixedPrice') for suffix in ('Min','Max','Mean')]
    pivot={**view,'mode':'pivot','groupBy':['book','contract'],'measures':measures,'pivotConvention':'trading_blotter_totals_v1','priceNumericalPolicy':'arithmetic_price_mean_to_even_18_v1'}
    groups=c.call('trading/blotter',{'context':shared,'view':pivot})['groups']
    for group in groups:
        members=[r for r in rows if (r['instrumentType'],r['bookId'],r['contractId'],r['definitionRevisionId'],r['quoteMultiplier'])==(group['instrumentType'],group['bookId'],group['contractId'],group['definitionRevisionId'],group['quoteMultiplier'])]
        assert members and group['priceUnit']
        for field in ('executionPrice','mark','fixedPrice'):
            values=[r[field] for r in members]
            if any(v is None for v in values):assert all(group[field+suffix] is None for suffix in ('Min','Max','Mean'));continue
            values=list(map(Decimal,values))
            with localcontext() as numeric:
                numeric.prec=80
                mean=(sum(values)/len(values)).quantize(Decimal('1e-18'),rounding=ROUND_HALF_EVEN)
            assert Decimal(group[field+'Min'])==min(values) and Decimal(group[field+'Max'])==max(values) and Decimal(group[field+'Mean'])==mean
    c.call('trading/blotter',{'context':shared,'view':{**pivot,'priceNumericalPolicy':None}},expected=422)
    print('PASS blotter prices: explicit extrema and arithmetic means match independent decimal oracles with unavailable values and quote partitions',flush=True)
    if query is not None:
        assert query("SELECT bool_and(NOT has_function_privilege('openetrm_runtime',p.oid,'EXECUTE')) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname='retain_floating_swap_side';")=='t'
        assert query("SELECT count(*) FROM pg_constraint WHERE conrelid='trading.swap_instruments'::regclass AND contype='f';")== '5'
        print('PASS swap composition: private side-retention helper and exact-version side foreign keys',flush=True)
