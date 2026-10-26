"""Synthetic swap lifecycle and independent economic bridge oracles."""
from fractions import Fraction as F
from uuid import uuid4
from copy import deepcopy
from concurrent.futures import ThreadPoolExecutor
from preview_client import PreviewClient

TRADES={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
# When settlement starts, a swap's Blotter row changes only what its lifecycle now values: its PV is the remaining value, it has no
# curve mark and so no mark minus price, and it states its settlement and lifecycle. Every other fact stays as the projection stated it.
SETTLEMENT_VALUES={'presentValue','mark','markMinusPrice','settlement','lifecycle'}


def held_row(c,context,trade):
    """A trade's Blotter row at the close of a swap context."""
    report=c.call('trading/blotter',dict(context={**context,'convention':'trading_workspace_v1'},view=TRADES))
    return next(h for h in report['holdings'] if h['tradeId']==trade)


def check_settlement_keeps_facts(projected,settled,what):
    """The row of a swap before its first determination and after it differ only in what the lifecycle values."""
    changed=sorted(k for k in projected.keys()|settled.keys() if projected.get(k)!=settled.get(k))
    assert (projected['settlement'],settled['settlement'])==('Projection','Active') and settled['mark'] is None and settled['unavailableReason'] is None,(what,projected,settled)
    assert set(changed)<=SETTLEMENT_VALUES,(what,[(k,projected.get(k),settled.get(k)) for k in changed])


def check_stated_sides(c,context,trade,expected):
    """A read with no pure valuation, as a swap valued by its lifecycle has, states each membership's kind and direction (D207)."""
    read=c.call('swaps/editor',dict(context=context,tradeId=trade,revision=None))
    assert read['valuation'] is None and read.get('memberships')==expected,read.get('memberships')


def run_foundation(c,query,url,body,before,after,before_at,currency):
    terms=deepcopy(body);terms['operationId']=str(uuid4())
    booked=c.call('swaps/book',terms)
    def preview(trade,context,revision=1):return dict(context=context,tradeId=trade,expectedRevision=revision,membershipOrdinal=0,paymentOrdinal=0,
        currency=currency,decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    def command(p,value,version):return dict(preview=p,operationId=str(uuid4()),expectedVersion=version,previewHash=value['previewHash'],
        duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None)
    p=preview(booked['tradeId'],before);original=c.call('swaps/settlement/preview',p)
    accepted=c.call('swaps/settlement/determine',command(p,original,0))
    amendment={**terms,'operationId':str(uuid4()),'tradeId':booked['tradeId'],'expectedRevision':1,'effectiveAt':before_at,'reason':'Synthetic unchanged January continuity'}
    amendment['scheduled']=deepcopy(terms['scheduled']);amendment['scheduled']['first']['periods'][1]['amount']='210'
    assert c.call('swaps/amend',amendment)['revision']==2
    continued=c.call('swaps/settlement/preview',preview(booked['tradeId'],before,2))
    assert continued['calculation']['occurrence']==original['calculation']['occurrence']
    assert continued['calculation']['amount']==original['calculation']['amount']=='-300'
    rewrite=deepcopy(amendment);rewrite.update(operationId=str(uuid4()),expectedRevision=2)
    rewrite['scheduled']['first']['periods'][0]['amount']='101'
    c.call('swaps/amend',rewrite,expected=422)
    c.call('swaps/editor',dict(context=before,tradeId=booked['tradeId'],revision=1),expected=422)
    if query:
        assert query(f"SELECT max(revision) FROM trading.swap_trade_revisions WHERE trade_id='{booked['tradeId']}';").strip()=='2'
        query(f"DO $$BEGIN UPDATE settlement_data.swap_determinations SET rounded_total=0 WHERE id='{accepted['determinationId']}';RAISE EXCEPTION 'Expected immutable determination';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;")
    bridge=dict(opening={**before,'knowledgeMode':'retained','contextId':original['context']['contextId']},closing=before,
        tradeIds=[booked['tradeId']],bookIds=[],contractChanges='reject_contract_changes_v1',duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    c.call('swaps/settlement/pnl',bridge,expected=422)
    changed=c.call('swaps/settlement/pnl',{**bridge,'contractChanges':'closing_context_contract_changes_v1'})['report']
    row=changed['trades'][0]
    assert F(row['bridge']['contractualAmendment'])==-30
    assert F(row['bridge']['continuingEconomics'])==0
    assert F(row['bridge']['newTradeEconomics'])==0
    assert F(row['bridge']['economicPnl'])==-30
    assert F(row['bridge']['realizedTransfer'])+F(row['bridge']['unrealizedMovement'])==-30
    assert row['openingRevision']==1 and row['closingRevision']==2
    assert changed==c.call('swaps/settlement/pnl',{**bridge,'contractChanges':'closing_context_contract_changes_v1'})['report']
    print('PASS closing-context amendment bridge: unchanged determined January plus independently valued future February quantity change')
    c.call('swaps/settlement/pnl',{**bridge,'opening':before,'tradeIds':[booked['tradeId'],str(uuid4())]},expected=422)
    c.call('swaps/settlement/pnl',{**bridge,'opening':after,'closing':before},expected=422)
    concurrent=deepcopy(terms);concurrent['operationId']=str(uuid4());other=c.call('swaps/book',concurrent)
    selected=preview(other['tradeId'],after);value=c.call('swaps/settlement/preview',selected)
    clients=[PreviewClient(url),PreviewClient(url)]
    after_at=next(f['at'] for f in c.call('workspace')['frames'] if f['id']==after['snapshotId'])
    attempts=[]
    for client in clients:
        client.bootstrap();client.call('profile',{'profile':'trader'});workspace=client.call('workspace')
        own_context={**after,'snapshotId':next(f['id'] for f in workspace['frames'] if f['at']==after_at)}
        own_preview=preview(other['tradeId'],own_context);own_value=client.call('swaps/settlement/preview',own_preview)
        attempts.append((client,command(own_preview,own_value,0)))
    def write(attempt):
        client,request=attempt
        try:return client.call('swaps/settlement/determine',request)
        except AssertionError as error:
            if 'got 409:' not in str(error) or not any(code in str(error) for code in ('settlement_version_conflict','settlement_stale_preview')):raise
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(write,attempts))
    assert sum(result is not None for result in results)==1
    earlier=preview(other['tradeId'],before);early_value=c.call('swaps/settlement/preview',earlier)
    c.call('swaps/settlement/determine',command(earlier,early_value,1),expected=422)
    if query:assert query(f"SELECT count(*) FROM settlement_data.swap_determinations WHERE trade_id='{other['tradeId']}';").strip()=='1'
    print('PASS settlement continuity and boundaries: unchanged January survives February amendment, determined rewrites/backdating refuse, history is immutable and concurrent publication commits once')


def run_lifecycle(c, workspace, context, trade, currency):
    frames={f['at'][:19]:f for f in workspace['frames']}
    def at(day):return {**context,'snapshotId':frames[day]['id']}
    def inspect(selected):return c.call('swaps/settlement/inspect',dict(context=selected,tradeId=trade,revision=None,
        duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1'))
    def preview(selected,slot):return dict(context=selected,tradeId=trade,expectedRevision=1,membershipOrdinal=slot,paymentOrdinal=0,
        currency=currency,decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    def determine(selected,slot,version,due=None):
        p=preview(selected,slot);value=c.call('swaps/settlement/preview',p)
        return dict(preview=p,operationId=str(uuid4()),expectedVersion=version,previewHash=value['previewHash'],
            duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',
            correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=due)
    def cash(selected,determination,amount,version):
        return c.call('swaps/settlement/payment',dict(context=selected,tradeId=trade,operationId=str(uuid4()),
            expectedRevision=1,expectedVersion=version,determinationId=determination,amount=amount,currency=currency,
            effectiveAt=frames[next(k for k,v in frames.items() if v['id']==selected['snapshotId'])]['at'],
            reference='Synthetic contractual cash',reversesId=None))
    january=at('2021-01-15T08:00:00')
    first=inspect(january)
    projected=held_row(c,january,trade)
    fixed=c.call('swaps/settlement/determine',determine(january,0,0))
    floating=c.call('swaps/settlement/determine',determine(january,1,1))
    assert fixed['total']=='-300' and floating['total']=='350'
    check_settlement_keeps_facts(projected,held_row(c,january,trade),'a single-period swap')
    check_stated_sides(c,january,trade,[{'slot':0,'kind':'fixed','coefficient':'-1'},{'slot':1,'kind':'floating','coefficient':'1'}])
    print('PASS a single-period swap whose settlement starts keeps its Blotter facts, and its read states its sides\' kinds and directions')
    cash(january,floating['determinationId'],'100',2)
    partial=inspect(january)
    assert partial['valuation']['remainingValue']=='-50' and partial['valuation']['realized']=='0'
    february=inspect(at('2021-02-01T08:00:00'))
    assert february['valuation']['remainingValue']=='-50' and february['valuation']['realized']=='50'
    assert february['valuation']['unrealized']=='0'
    due_context=at('2021-03-18T00:00:00')
    due=inspect(due_context)
    assert due['valuation']['dueBalance']=='-50' and due['valuation']['determinedOutstanding']=='-50'
    cash(due_context,floating['determinationId'],'250',3)
    cash(due_context,fixed['determinationId'],'-300',4)
    paid=inspect(due_context)
    assert paid['valuation']['remainingValue']=='0' and paid['valuation']['netCash']=='50'
    assert paid['valuation']['realized']=='50' and paid['valuation']['unrealized']=='0'
    assert all(p['state']=='Paid' for p in paid['valuation']['payments'])
    original_pin=c.call('swaps/settlement/preview',preview(due_context,1))['calculation']['prices'][0]['lineage'][0]['id']
    correction_context=at('2021-03-19T08:00:00')
    c.call('profile',{'profile':'editor'})
    corrected=c.call('corrections',dict(snapshotId=correction_context['snapshotId'],observationId=original_pin,value='3.7'))
    c.call('profile',{'profile':'trader'})
    correction_context={**correction_context,'snapshotId':corrected['frame']['id']}
    # A revised fixing does not reprice the already determined original balance.
    assert inspect(correction_context)['valuation']['remainingValue']=='0'
    adjustment=determine(correction_context,1,5)
    c.call('swaps/settlement/determine',adjustment,expected=422)
    adjustment['correctionDueAt']='2021-03-20T00:00:00Z'
    revised=c.call('swaps/settlement/determine',adjustment)
    assert revised['total']=='370' and revised['adjustment']=='20' and revised['version']==6
    assert c.call('swaps/settlement/determine',adjustment)==revised
    fresh=c.call('swaps/settlement/preview',preview(correction_context,1))
    assert fresh['previousTotal']=='370' and fresh['proposedContribution']=='0' and not fresh['inputsChanged']
    c.call('swaps/settlement/determine',{**adjustment,'operationId':str(uuid4()),'expectedVersion':6,'previewHash':fresh['previewHash']},expected=422)
    changed=inspect(correction_context)
    assert changed['valuation']['remainingValue']=='20' and changed['valuation']['realized']=='70'
    assert changed['valuation']['netCash']=='50' and changed['valuation']['corrections']=='20'
    old={**due_context,'knowledgeMode':'retained','contextId':paid['context']['contextId']}
    replay=inspect(old)
    assert replay['valuation']==paid['valuation'] and replay['settlement']==paid['settlement']
    report=c.call('swaps/settlement/pnl',dict(opening=old,closing=correction_context,tradeIds=[trade],bookIds=[],
        contractChanges='reject_contract_changes_v1',duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1'))['report']
    bridge=report['trades'][0]['bridge']
    assert F(bridge['economicPnl'])==F(bridge['remainingValueMovement'])+F(bridge['netCashMovement'])==20
    assert F(bridge['economicPnl'])==F(bridge['realizedTransfer'])+F(bridge['unrealizedMovement'])
    assert F(bridge['economicPnl'])==F(bridge['contractualCashMovement'])+F(bridge['discountEffectMovement'])
    cash(at('2021-03-20T00:00:00'),revised['determinationId'],'20',6)
    final=inspect(at('2021-03-20T00:00:00'))
    assert final['valuation']['remainingValue']=='0' and final['valuation']['netCash']=='70' and final['valuation']['realized']=='70'
    # Payment changes balances and cash only, without another realization.
    assert final['valuation']['unrealized']=='0'
    print('PASS complete swap lifecycle: projection, final fixing, determination, completed-period realization, overdue cash, partial/full payment, correction, replay and reconciled P&L')


def run_late_cash(c,monthly_body,opening_context,closing_context,currency,opening_at):
    body={**monthly_body,'operationId':str(uuid4())};booked=c.call('swaps/book',body)
    preview=dict(context=opening_context,tradeId=booked['tradeId'],expectedRevision=1,membershipOrdinal=0,paymentOrdinal=0,
        currency=currency,decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    calculated=c.call('swaps/settlement/preview',preview)
    original=c.call('swaps/settlement/determine',dict(preview=preview,operationId=str(uuid4()),expectedVersion=0,previewHash=calculated['previewHash'],
        duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None))
    inspection=dict(context=opening_context,tradeId=booked['tradeId'],revision=None,duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    opening=c.call('swaps/settlement/inspect',inspection)
    c.call('swaps/settlement/payment',dict(context=closing_context,tradeId=booked['tradeId'],operationId=str(uuid4()),expectedRevision=1,expectedVersion=1,
        determinationId=original['determinationId'],amount='-100',currency=currency,effectiveAt=opening_at,reference='Synthetic late-recorded contractual cash',reversesId=None))
    report=c.call('swaps/settlement/pnl',dict(opening={**opening_context,'knowledgeMode':'retained','contextId':opening['context']['contextId']},closing=closing_context,
        tradeIds=[booked['tradeId']],bookIds=[],contractChanges='reject_contract_changes_v1',duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1'))['report']
    bridge=report['trades'][0]['bridge']
    assert bridge['economicPnl']=='0' and bridge['periodCashReceived']=='0' and bridge['priorCashKnowledgeAdjustment']=='-100'
    assert bridge['remainingValueMovement']=='100' and bridge['netCashMovement']=='-100'
    print('PASS cash knowledge bridge: prior cash is separate from period cash; the undiscounted balance/cash movements reconcile without new earnings')


def run_future_boundary(c,body,before,after):
    """Independent side ranges must not make completed fixed terms amendable in a bridge."""
    terms=deepcopy(body);terms['operationId']=str(uuid4())
    first=terms['scheduled']['first'];second=terms['scheduled']['second']
    first['periods']=first['periods'][:1];second['periods']=second['periods'][:1]
    first['periods'][0]['fixed']['paymentSchedule']['days']=90
    floating=second['periods'][0]['floating'];floating['deliveryFrom']='2021-03-01';floating['deliveryTo']='2021-04-01'
    trade=c.call('swaps/book',terms)['tradeId']
    inspection=dict(context=before,tradeId=trade,revision=None,duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    initial=c.call('swaps/settlement/inspect',inspection)
    amendment={**terms,**after,'operationId':str(uuid4()),'tradeId':trade,'expectedRevision':1,'effectiveAt':next(f['at'] for f in c.call('workspace')['frames'] if f['id']==after['snapshotId']),'reason':'Synthetic completed fixed period boundary'}
    amendment['scheduled']=deepcopy(terms['scheduled']);amendment['scheduled']['first']['periods'][0]['fixed']['price']='4'
    c.call('swaps/amend',amendment)
    c.call('swaps/settlement/pnl',dict(opening={**before,'knowledgeMode':'retained','contextId':initial['context']['contextId']},closing=after,
        tradeIds=[trade],bookIds=[],contractChanges='closing_context_contract_changes_v1',duePolicy=inspection['duePolicy'],recognitionPolicy=inspection['recognitionPolicy']),expected=422)
    print('PASS contractual bridge refuses completed undetermined fixed-period rewrites with independent later floating delivery')


def run_cross_market_bridge(c,legacy,context):
    catalog=c.call('swaps/catalog',context)
    source=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    terms=deepcopy(legacy);terms.update(operationId=str(uuid4()),definitionRevisionId=source['definitionRevisionId'])
    terms['floating'].update(curve='daily_gas',index=source['index'],resetFrequency='daily',averagingMethod='arithmetic')
    trade=c.call('swaps/book',terms)['tradeId']
    inspection=dict(context=context,tradeId=trade,revision=None,duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    opening=c.call('swaps/settlement/inspect',inspection)
    assert F(opening['valuation']['remainingValue'])==16
    amendment={**deepcopy(legacy),'operationId':str(uuid4()),'tradeId':trade,'expectedRevision':1,
        'effectiveAt':next(f['at'] for f in c.call('workspace')['frames'] if f['id']==context['snapshotId']),'reason':'Synthetic supplemental to primary market amendment'}
    c.call('swaps/amend',amendment)
    report=c.call('swaps/settlement/pnl',dict(opening={**context,'knowledgeMode':'retained','contextId':opening['context']['contextId']},closing=context,
        tradeIds=[trade],bookIds=[],contractChanges='closing_context_contract_changes_v1',duePolicy=inspection['duePolicy'],recognitionPolicy=inspection['recognitionPolicy']))['report']['trades'][0]['bridge']
    assert F(report['contractualAmendment'])==-6 and F(report['continuingEconomics'])==0 and F(report['economicPnl'])==-6
    print('PASS closing-context amendment resolves supplemental prior and primary revised markets independently with a -6 cash oracle')


def run_newtrade_basis_refusal(c,body,context):
    opening=c.call('swaps/catalog',context)
    terms=deepcopy(body);terms['operationId']=str(uuid4())
    trade=c.call('swaps/book',terms)['tradeId']
    source=next(i for i in opening['indices'] if i['curve']=='daily_power')
    amendment={**terms,'operationId':str(uuid4()),'tradeId':trade,'expectedRevision':1,
        'effectiveAt':next(f['at'] for f in c.call('workspace')['frames'] if f['id']==context['snapshotId']),
        'reason':'Synthetic unsupported exact-basis transformation','definitionRevisionId':source['definitionRevisionId'],'commodity':source['commodity']}
    amendment['scheduled']=deepcopy(terms['scheduled'])
    for side in (amendment['scheduled']['first'],amendment['scheduled']['second']):
        side['quantity']=source['quantityChoices'][0]['specification']
        for period in side['periods']:
            if period['fixed']:period['fixed'].update(price='40',currency=source['currencyCode'])
            else:period['floating'].update(curve=source['curve'],index=source['index'],currency=source['currencyCode'],resetFrequency='daily',averagingMethod='arithmetic')
    c.call('swaps/amend',amendment)
    c.call('swaps/settlement/pnl',dict(opening={**context,'knowledgeMode':'retained','contextId':opening['context']['contextId']},closing=context,
        tradeIds=[trade],bookIds=[],contractChanges='closing_context_contract_changes_v1',duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1'),expected=422)
    print('PASS newly visible amended trade refuses an intervening exact currency/quantity-basis transformation')
