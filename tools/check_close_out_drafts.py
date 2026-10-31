"""Qualify Blotter close-out drafts against real isolated bookings and recorded settlement."""
import json
from pathlib import Path
import subprocess
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import CAT, R, REV, identity, trade_booking

# A close after the January and February monthly index fixings, before March's: two periods can then be settled.
FEBRUARY='2021-02-01T08:00:00Z'
SWAP_CONVENTION='independent_side_financial_swap_v2'
PERIODS=[('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01'),('2021-03-01','2021-04-01'),('2021-04-01','2021-05-01')]


def prepare(market,manifest):
    """Publish February's synthetic monthly index fixing and a close that pins it with the January 31 prices."""
    base=next(f for f in manifest['frames'] if f['at']=='2021-01-31T08:00:00Z')
    # The same series and identities as the fixture's own monthly fixings (preview_fixture.fixture), for the month it leaves out.
    fixings=[(identity('7',5000000+10+n),sid,value) for n,sid,value in [(0,identity('9',9201),'3.6'),(1,identity('9',107),'46')]]
    rows=[f"INSERT INTO market_data.observations VALUES ('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_monthly_fixing_1_{n}','1','index_fixing','final','2021-01-31T12:00:00Z',clock_timestamp(),{value},NULL);"
          for n,(oid,sid,value) in enumerate(fixings)]
    manifest['frames'].append({**base,'at':FEBRUARY,'observationIds':[*base['observationIds'],*(oid for oid,_,_ in fixings)]})
    return market.rsplit('COMMIT;',1)[0]+'\n'+'\n'.join(rows)+'\nCOMMIT;\n'


def run_checks(url,query,dotnet,owned_root,environment):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    frames=client.call('workspace')['frames']
    booking=next(f for f in frames if f['at'].startswith('2020-12-18'))
    february=next(f for f in frames if f['at'].startswith(FEBRUARY[:19]))
    futures_context={'snapshotId':booking['id'],'convention':'fixed_total_cash_execution_pnl_v1','knowledgeMode':'current'}
    swap_context={**futures_context,'convention':SWAP_CONVENTION}
    def blotter(frame):
        view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
        return {(h['tradeId']):h for h in client.call('trading/blotter',{'context':{**futures_context,'snapshotId':frame['id'],'convention':'trading_workspace_v1'},'view':view})['holdings']}

    catalog=client.call('futures/catalog',futures_context)
    contract=next(k for k in catalog['contracts'] if k['deliveryFrom']=='2021-01-01')
    book=catalog['books'][0]['id']
    def book_future(lots):
        return client.call('futures/book',{**futures_context,**trade_booking(),'operationId':str(uuid4()),'bookId':book,'curve':contract['curve'],
                                           'contractId':contract['id'],'lots':lots,'executionPrice':'2.9'})['tradeId']
    future=book_future('40')
    amended=book_future('5')
    earlier=client.call('futures/editor',{'context':futures_context,'tradeId':amended,'revision':None})['context']
    client.call('futures/amend',{**futures_context,**trade_booking(),'operationId':str(uuid4()),'tradeId':amended,'expectedRevision':1,'bookId':book,
        'curve':contract['curve'],'contractId':contract['id'],'lots':'6','executionPrice':'2.9','effectiveAt':booking['at'],
        'reason':'Synthetic close-out revision','amendmentConvention':'unprocessed_futures_booking_amendment_v1'})

    swaps=client.call('swaps/catalog',swap_context)
    index=next(i for i in swaps['indices'] if i['curve']=='base_forward')
    payment={'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    def floating(start,end):
        return {'settlement':{'mode':'curve_target','index':None,'projectionPolicy':None},'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],
                'deliveryFrom':start,'deliveryTo':end,'paymentSchedule':payment,'resetFrequency':'monthly','averagingMethod':'none','spread':'0'}
    def fixed(start,end):return {'price':'3','currency':index['currencyCode'],'deliveryFrom':start,'deliveryTo':end,'paymentSchedule':payment}
    common={**swap_context,**trade_booking(),'bookId':book,'executedAt':None,'discounting':False,'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity']}
    swap=client.call('swaps/book',{**common,'operationId':str(uuid4()),'quantity':'100','fixedCoefficient':'-1','fixed':fixed('2021-03-01','2021-04-01'),'floating':floating('2021-03-01','2021-04-01')})['tradeId']
    amounts=['100','200','300','400']
    def side(fixed_side):
        return {'basis':'period_total','quantity':index['quantityChoices'][0]['specification'],'amount':None,'allocation':None,'priceApplication':'period_average_times_quantity_v1','power':None,
                'periods':[{'amount':amount,'fixed':fixed(*period) if fixed_side else None,'floating':None if fixed_side else floating(*period)} for period,amount in zip(PERIODS,amounts)]}
    scheduled_terms={**common,'quantity':None,'fixedCoefficient':None,'fixed':None,'floating':None,'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
                     'scheduled':{'implementation':'whole_term_scheduled_financial_swap_v1','first':side(True),'second':side(False)}}
    scheduled,settled,partly=(client.call('swaps/book',{**scheduled_terms,'operationId':str(uuid4())})['tradeId'] for _ in range(3))

    # Settle January and February on both sides of one swap, and only January's fixed side of another, at the February close.
    settlement_context={**swap_context,'snapshotId':february['id']}
    currency=client.call('swaps/editor',{'context':swap_context,'tradeId':scheduled,'revision':None})['valuation']['currency']
    def determine(trade,membership,period):
        version=client.call('swaps/settlement/inspect',{'context':settlement_context,'tradeId':trade,'revision':None,'duePolicy':'swap_due_nominal_v1',
                                                         'recognitionPolicy':'swap_completed_period_trading_pnl_v1'})['settlement']['version']
        request={'context':settlement_context,'tradeId':trade,'expectedRevision':1,'membershipOrdinal':membership,'paymentOrdinal':period,
                 'currency':currency,'decimalPlaces':2,'roundingPolicy':'currency_minor_units_to_even_v1'}
        calculation=client.call('swaps/settlement/preview',request)
        client.call('swaps/settlement/determine',{'preview':request,'operationId':str(uuid4()),'expectedVersion':version,'previewHash':calculation['previewHash'],
            'duePolicy':'swap_due_nominal_v1','recognitionPolicy':'swap_completed_period_trading_pnl_v1','correctionPolicy':'swap_explicit_correction_due_v1','correctionDueAt':None})
    for period in (0,1):
        for membership in (0,1):determine(settled,membership,period)
    determine(partly,0,0)

    # The oracle marks: each period's one projected reset price in the swap editor's own valuation of the unsettled swap.
    def marks(frame):
        value=client.call('swaps/editor',{'context':{**swap_context,'snapshotId':frame['id']},'tradeId':scheduled,'revision':None})['valuation']
        return {p['deliveryFrom']:p['resets'][0]['price'] for p in value['sides'][1]['payments'] if p['resets'][0]['fixingState']=='Projected'}
    booking_marks,february_marks=marks(booking),marks(february)
    rows=blotter(booking)
    def position(trade,**extra):
        row=rows[trade]
        return {'tradeId':trade,'bindingId':row['bindingId'],'revision':int(row['revision']),'instrumentType':row['instrumentType'],'bookId':row['bookId'],**extra}
    future_row=rows[future]
    pins=",".join(f"'{oid}'" for oid in booking['observationIds'])
    unpriced=set(query(f"SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
                       f"WHERE s.contract_id='{contract['id']}' AND o.id IN ({pins});").split())
    assert unpriced,'The booking close pins no price of the future contract'
    fixture={'frames':{'booking':booking,'february':february,'unpriced':{**booking,'id':str(uuid4()),'observationIds':[o for o in booking['observationIds'] if o not in unpriced]}},
             'earlierContext':earlier,
             'positions':{'future':position(future,contractId=contract['id'],curve=contract['curve'],mark=future_row['mark'],
                                          priceUnit=f"{contract['currency']}/{contract['unit']}"+('' if contract['quoteMultiplier']=='1' else ' × '+contract['quoteMultiplier']),
                                          legalEntityId=trade_booking()['legalEntityId'],counterpartyId=trade_booking()['counterpartyId']),
                          'amendedFuture':position(amended),'swap':position(swap,quantity='100',mark=rows[swap]['mark']),
                          'scheduled':position(scheduled,periods=[{'from':start,'quantity':amount,'bookingMark':booking_marks[start],'februaryMark':february_marks.get(start)} for (start,_),amount in zip(PERIODS,amounts)]),
                          'settled':position(settled),'partlySettled':position(partly)}}
    path=Path(owned_root)/'close-out-drafts.json';path.write_text(json.dumps(fixture))
    repository=Path(__file__).resolve().parents[1]
    subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--','--close-out-drafts',str(path)],cwd=repository,env=environment,check=True,timeout=180)
    # The page's route answers through the shared trading read. Current knowledge is captured afresh by each read, so the
    # same request twice is alike under the context the first one pinned.
    request={'context':{**futures_context,'convention':'trading_workspace_v1'},'tradeId':future,'bindingId':future_row['bindingId'],'revision':int(future_row['revision']),'instrumentType':'future'}
    first=client.call('trading/close-out-draft',request)
    assert first['terms']['lots']=='-40' and first['canBook'],first
    pinned={**request,'context':{**request['context'],'knowledgeMode':'retained','contextId':first['context']['contextId']}}
    repeated=client.call('trading/close-out-draft',pinned)
    assert repeated==client.call('trading/close-out-draft',pinned) and repeated['terms']=={**first['terms'],'knowledgeMode':'retained'} and repeated['price']==first['price'],repeated
    assert client.call('trading/close-out-draft',{**request,'instrumentType':'ppa'},expected=422)['code']=='close_out'
    print('PASS close-out draft HTTP: the trading read route returns the same reviewed terms twice and refuses with its structured reason',flush=True)
