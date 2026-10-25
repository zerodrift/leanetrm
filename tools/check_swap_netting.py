"""Original synthetic gross/net settlement and replay checks."""
from copy import deepcopy
from uuid import uuid4
from fractions import Fraction as F


def run(c,workspace,context,legacy,currency,url):
    body=deepcopy(legacy);body['operationId']=str(uuid4());body['fixed']['price']='3';body['floating']['spread']='0'
    opening=c.call('swaps/catalog',context)['context']
    saved=c.call('swaps/book',body);trade=saved['tradeId']
    added=dict(opening={**context,'knowledgeMode':'retained','contextId':opening['contextId']},closing=context,tradeIds=[trade],bookIds=[],contractChanges='closing_context_contract_changes_v1',duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    new=c.call('swaps/settlement/pnl',added)['report']['trades'][0]['bridge']
    assert F(new['newTradeEconomics'])==10 and F(new['continuingEconomics'])==0 and F(new['economicPnl'])==10
    c.call('swaps/settlement/pnl',{**added,'contractChanges':'reject_contract_changes_v1'},expected=422)
    print('PASS newly booked swap bridge: selected opening scope is empty and the independently known closing economics is +10')
    final=next(f for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    ctx={**context,'snapshotId':final['id']}
    inspect=dict(context=ctx,tradeId=trade,revision=None,duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    ids=[]
    for membership in (0,1):
        request=dict(context=ctx,tradeId=trade,expectedRevision=1,membershipOrdinal=membership,paymentOrdinal=0,currency=currency,decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
        preview=c.call('swaps/settlement/preview',request)
        result=c.call('swaps/settlement/determine',dict(preview=request,operationId=str(uuid4()),expectedVersion=membership,previewHash=preview['previewHash'],duePolicy=inspect['duePolicy'],recognitionPolicy=inspect['recognitionPolicy'],correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None))
        ids.append(result['determinationId'])
    before=c.call('swaps/settlement/inspect',inspect)
    assert [F(b['outstanding']) for b in sorted(before['balances'],key=lambda b:b['membershipOrdinal'])]==[-300,350],before['balances']
    def request(version,amount,values,reverses=None):
        return dict(context=ctx,tradeId=trade,expectedRevision=1,expectedVersion=version,currency=currency,amount=amount,
            allocations=[dict(determinationId=i,amount=v) for i,v in zip(ids,values)],policy='explicit_same_due_swap_net_v1',reversesId=reverses)
    def record(req,reference):
        p=c.call('swaps/settlement/net-preview',req)
        command=dict(preview=req,operationId=str(uuid4()),previewHash=p['previewHash'],effectiveAt=final['at'],reference=reference)
        result=c.call('swaps/settlement/net',command)
        assert c.call('swaps/settlement/net',command)==result
        assert c.call('swaps/settlement/status',dict(operationId=command['operationId']))['found']
        return result
    # Two gross obligations are cleared by a single +50 movement.
    first=record(request(2,'50',['-300','350']),'Synthetic net +50')
    after=c.call('swaps/settlement/inspect',inspect)
    assert all(F(b['outstanding'])==0 for b in after['balances'])
    assert F(after['valuation']['netCash'])==50
    assert len(after['cashMovements'])==1 and len(after['settlement']['allocations'])==2
    assert F(after['valuation']['remainingValue'])+F(after['valuation']['netCash'])==F(before['valuation']['remainingValue'])
    old=c.call('swaps/settlement/inspect',{**inspect,'context':{**ctx,'knowledgeMode':'retained','contextId':before['context']['contextId']}})
    assert old['settlement']['version']==2 and not old['cashMovements']
    c.call('swaps/settlement/net-preview',request(3,'50',['-300','350']),expected=422)
    # Exact reversal restores both gross balances, even when net cash is small.
    reverse=dict(request(3,None,[]),reversesId=first['paymentId'])
    record(reverse,'Synthetic net reversal')
    restored=c.call('swaps/settlement/inspect',inspect)
    assert [F(b['outstanding']) for b in sorted(restored['balances'],key=lambda b:b['membershipOrdinal'])]==[-300,350]
    partial=record(request(4,'20',['-100','120']),'Synthetic explicit partial net')
    part=c.call('swaps/settlement/inspect',inspect)
    assert [F(b['outstanding']) for b in sorted(part['balances'],key=lambda b:b['membershipOrdinal'])]==[-200,230]
    zero=record(request(5,'0',['-200','200']),'Synthetic zero net discharge')
    assert zero['recordKind']=='zero_net_discharge_v1'
    current=c.call('swaps/settlement/inspect',inspect)
    assert [F(b['outstanding']) for b in sorted(current['balances'],key=lambda b:b['membershipOrdinal'])]==[0,30]
    assert F(current['valuation']['netCash'])==20
    assert F(current['valuation']['remainingValue'])+F(current['valuation']['netCash'])==F(before['valuation']['remainingValue'])
    record(dict(request(6,None,[]),reversesId=zero['paymentId']),'Synthetic zero discharge reversal')
    current=c.call('swaps/settlement/inspect',inspect)
    assert [F(b['outstanding']) for b in sorted(current['balances'],key=lambda b:b['membershipOrdinal'])]==[-200,230]
    for cash,values in [('31',['-200','230']),('29',['-201','230']),('231',['1','230'])]:
        c.call('swaps/settlement/net-preview',request(7,cash,values),expected=422)
    c.call('swaps/settlement/net-preview',{**request(7,'30',['-200','230']),'policy':''},expected=422)
    for bad in (None,[None,None],[],[dict(determinationId=ids[0],amount='-1')]*257):
        c.call('swaps/settlement/net-preview',{**request(7,'0',['-1','1']),'allocations':bad},expected=400 if bad is None else 422)
    # Independent sessions prepare the same available balance; exactly one can consume it.
    from preview_client import PreviewClient
    from concurrent.futures import ThreadPoolExecutor
    attempts=[]
    for _ in range(2):
        client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
        frame=next(f for f in client.call('workspace')['frames'] if f['at']==final['at'])
        req={**request(7,'0',['-1','1']),'context':{**ctx,'snapshotId':frame['id']}}
        value=client.call('swaps/settlement/net-preview',req)
        attempts.append((client,dict(preview=req,operationId=str(uuid4()),previewHash=value['previewHash'],effectiveAt=final['at'],reference='Synthetic concurrent zero discharge')))
    def write(attempt):
        client,command=attempt
        try:return client.call('swaps/settlement/net',command)
        except AssertionError as error:
            if 'got 409:' not in str(error):raise
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:outcomes=list(pool.map(write,attempts))
    committed=[r for r in outcomes if r is not None];assert len(committed)==1
    record(dict(request(8,None,[]),reversesId=committed[0]['paymentId']),'Synthetic concurrent discharge reversal')
    assert [F(b['outstanding']) for b in sorted(c.call('swaps/settlement/inspect',inspect)['balances'],key=lambda b:b['membershipOrdinal'])]==[-200,230]
    print('PASS net concurrent consumption commits once and malformed allocations refuse before mutation')
    print('PASS net +50, explicit partial/zero discharge, exact gross reversals, historical replay and no duplicate earnings')

    return dict(tradeId=trade)
