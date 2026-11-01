"""Deal ticket schedule oracles for a composed gas trade in an owned synthetic database.

January 2021 is the first synthetic gas forward month after the 2020-12-18 frame: 31 gas days.
"""
import json
from datetime import datetime
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


# The side kind each booked composition role is drawn as (GasTicket's role map).
ROLE_KIND={'physical':'physical','consideration':'cash','fee':'fee'}


def assert_declared(view,body,description):
    """Every cell key is a column the served description declares for its side's kind, or a trade column, on every row and the totals."""
    columns={k['kind']:{c['id'] for c in k['columns']} for k in description['sides']}
    roles={**{s['code']:'physical' for s in body['physicalSides']},**{s['code']:s['role'] for s in body['cashSides']}}
    expected={f'{code}.{column}' for code,role in roles.items() for column in columns[ROLE_KIND[role]]}|{f"trade.{c['id']}" for c in description['tradeColumns']}
    for cells in [view['totals'],*(r['cells'] for r in view['rows'])]:
        assert set(cells)==expected,sorted(set(cells)^expected)


def run_checks(url):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    cat=c.call('gas/catalog',context)
    description=c.call('tickets/description',{'context':context,'product':'physical_gas'})
    assert description['product']=='physical_gas' and description['version']==1,description['product']
    c.call('tickets/description',{'context':context,'product':'power'},expected=422)
    p=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    cash=lambda code,role,direction,price:{'code':code,'role':role,'direction':direction,'source':'physical','stage':'delivery','price':price,'tariffCode':None,'paymentAt':'2021-02-05T00:00:00Z'}
    body={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':cat['books'][0]['id'],'definitionRevisionId':p['definitionRevisionId'],
          'gasSetupId':cat['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
          'physicalSides':[{'code':'physical','pipeline':p['pipeline'],'zone':p['zone'],'deliveryPoint':p['deliveryPoint'],'product':p['product'],'direction':'deliver',
                            'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','quantityBasis':'per_operating_day','quantity':'10000','link':None}],
          'cashSides':[cash('cash','consideration','receive','3.412'),cash('fee','fee','pay','0.045')]}
    value=c.call('gas/composition/preview',body)
    view=value['schedule'];assert value['scheduleRefusal'] is None
    assert view['levels']==['month','day'],view['levels']
    months=[r for r in view['rows'] if r['level']=='month'];days=[r for r in view['rows'] if r['level']=='day']
    assert len(months)==1 and len(days)==31 and all(d['parent']==months[0]['key'] for d in days),[r['key'] for r in view['rows']]
    month=months[0]['cells'];number=lambda cell:Fraction(cell['value'])
    delivered=sum(Fraction(int(r['quantity']['numerator']),int(r['quantity']['denominator'])) for r in value['sides'][0]['deliveryRows'])
    # Quantities and amounts carry the side's signed membership, as its present value does: delivering gas is negative.
    assert delivered==310000 and number(month['physical.quantity'])==-delivered,(month['physical.quantity'],delivered)
    assert all(number(d['cells']['physical.quantity'])==-10000 for d in days)
    assert number(month['cash.quantity'])==310000 and number(month['cash.amount'])==number(month['cash.quantity'])*Fraction('3.412'),month['cash.amount']
    assert number(month['fee.amount'])==-number(month['cash.quantity'])*Fraction('0.045'),month['fee.amount']
    assert number(month['trade.net_cash'])==number(month['cash.amount'])+number(month['fee.amount'])==1043770,month['trade.net_cash']
    assert month['trade.payment_at'] is not None and all(d['cells']['cash.amount']['value'] is None for d in days)
    assert view['totals']==month,(view['totals'],month)
    assert_declared(view,body,description)
    for side in value['sides']:
        assert number(view['totals'][side['code']+'.amount'])==Fraction(side['presentValue']),(side['code'],side['presentValue'])
    assert value['summaries']==[{'side':code,'billedQuantity':view['totals'][code+'.quantity']['value'],'amount':view['totals'][code+'.amount']['value'],'againstMarket':None}
                                for code in ['physical','cash','fee']],value['summaries']
    booked=c.call('gas/composition/book',body)
    assert c.call('gas/composition/book',body)==booked
    reopened=c.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']})['valuation']
    assert reopened['schedule']==view and reopened['summaries']==value['summaries']
    # Independent physical sides may keep their own gas-day clocks (point C starts at 01:00). The view cannot place both
    # on one day grid, so it refuses while the preview and the booked read keep their terms and valuation.
    c_point=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_c')
    clocks={**body,'operationId':str(uuid4()),'cashSides':[cash('fee','fee','pay','0.045')],
            'physicalSides':[body['physicalSides'][0],{**body['physicalSides'][0],'code':'other','pipeline':c_point['pipeline'],'zone':c_point['zone'],
                             'deliveryPoint':c_point['deliveryPoint'],'product':c_point['product'],'direction':'receive','quantity':'5000'}]}
    mixed=c.call('gas/composition/preview',clocks)
    assert mixed['schedule'] is None and mixed['summaries'] is None and mixed['scheduleRefusal']['code']=='schedule_view',mixed.get('scheduleRefusal')
    assert len(mixed['sides'])==3 and Fraction(mixed['presentValue'])==sum(Fraction(s['presentValue']) for s in mixed['sides'])
    mixed_booked=c.call('gas/composition/book',clocks)
    mixed_loaded=c.call('gas/composition/editor',{'context':context,'tradeId':mixed_booked['tradeId']})
    assert mixed_loaded['terms']['physicalSides']==clocks['physicalSides'],(mixed_loaded['terms']['physicalSides'],clocks['physicalSides'])
    # The route adds its read context to the preview; the booked valuation carries every valuation field.
    assert mixed_loaded['valuation']=={k:mixed[k] for k in mixed_loaded['valuation']} and 'scheduleRefusal' in mixed_loaded['valuation']
    # A linked receipt and delivery on one gas-day clock: the delivery follows the receipt through a fuel rule, so its
    # daily quantity is 1000/1.02 = 50000/51 and its quantities and amounts are exact a/b ratios.
    b_point=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_b')
    linked={**body,'operationId':str(uuid4()),'cashSides':[{**cash('fee','fee','pay','0.1'),'source':'delivery'}],
            'physicalSides':[{**body['physicalSides'][0],'code':'receipt','direction':'receive','quantity':'1000'},
                             {**body['physicalSides'][0],'code':'delivery','pipeline':b_point['pipeline'],'zone':b_point['zone'],'deliveryPoint':b_point['deliveryPoint'],
                              'product':b_point['product'],'quantity':None,'link':{'source':'receipt','formula':'gross_divided_by_one_plus_fraction','fraction':'0.02'}}]}
    def assert_linked(value):
        view=value['schedule'];assert value['scheduleRefusal'] is None and view is not None,value['scheduleRefusal']
        assert_declared(view,linked,description);totals=view['totals']
        for side in value['sides']:
            assert number(totals[side['code']+'.amount'])==Fraction(side['presentValue']),(side['code'],totals[side['code']+'.amount'],side['presentValue'])
        assert totals['delivery.quantity']['value']=='-1550000/51',totals['delivery.quantity']
        fuel=value['links'][0]['fuel']
        # Quantities carry the side's direction: the receipt is positive and the delivery negative.
        assert number(totals['receipt.quantity'])-(-number(totals['delivery.quantity']))==Fraction(fuel),(totals,fuel)
        return view
    linked_view=assert_linked(c.call('gas/composition/preview',linked))
    # Booked at the earlier 2020-12-17 frame, the trade reads at the current frame and at that earlier As of frame;
    # each view takes its valuation instant from the read's pinned context.
    earlier_frame=initial_frame(c.call('workspace'),'2020-12-17');earlier={**context,'snapshotId':earlier_frame['id']}
    early={**linked,**earlier,'operationId':str(uuid4())}
    early_view=assert_linked(c.call('gas/composition/preview',early))
    early_booked=c.call('gas/composition/book',early)
    read=lambda at:c.call('gas/composition/editor',{'context':at,'tradeId':early_booked['tradeId']})['valuation']
    assert read(context)['schedule']==linked_view
    past=read(earlier);assert_linked(past)
    assert past['schedule']==early_view and datetime.fromisoformat(early_view['valuationAt'])==datetime.fromisoformat(earlier_frame['at'])<datetime.fromisoformat(linked_view['valuationAt']),(early_view['valuationAt'],linked_view['valuationAt'])
    check_ticket_operations(c,context,cat,description,body,view)
    print('PASS deal ticket: gas preview and booked terms return one exact month/day schedule whose totals and amounts agree with side values; mixed gas-day clocks keep their valuation with a schedule refusal; linked receipt and delivery conserve fuel exactly at the current and an earlier As of frame',flush=True)


def check_ticket_operations(c,context,cat,description,body,view):
    """The ticket routes run the gas operations from a ticket draft, under the read's context."""
    def refused(path,request,code):
        error=c.call(path,request,expected=422);assert error['code']==code,(path,error)
    def choices(at,draft,side,field):
        return [x['value'] for x in c.call('tickets/choices',{'context':at,'draft':draft,'side':side,'field':field})['choices']]
    def lookup(at,draft,side,field):
        return c.call('tickets/lookup',{'context':at,'draft':draft,'side':side,'field':field})
    p=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    # Party choices are exact '<id>@<revision>' references, the shape the booking takes.
    party=lambda field,id:next(v for v in choices(context,{'product':'physical_gas','version':1,'trade':{},'sides':[]},None,field) if v.startswith(id+'@'))
    # The draft names the context's gas setup, or none where the context resolves none.
    setup=lambda setup_id:{} if setup_id is None else {'gas_setup':setup_id}
    trade={'book':cat['books'][0]['id'],'legal_entity':party('legal_entity',body['legalEntityId']),'counterparty':party('counterparty',body['counterpartyId']),**setup(cat['gasSetupId'])}
    assert trade['legal_entity']==f"{body['legalEntityId']}@{body['legalEntityRevision']}",trade
    physical={'direction':'deliver','delivery_from':'2021-01-01','delivery_to':'2021-02-01','product':p['product'],'quantity':'10000','quantity_basis':'per_operating_day',
              'pipeline':p['pipeline'],'zone':p['zone'],'delivery_point':p['deliveryPoint']}
    def charge(kind,direction,price):
        return {f'{kind}_direction':direction,f'{kind}_source':'physical',f'{kind}_stage':'delivery',f'{kind}_price':price,f'{kind}_payment_at':'2021-02-05T00:00:00Z'}
    draft={'product':'physical_gas','version':1,'trade':trade,'sides':[{'kind':'physical','code':'physical','values':physical},
           {'kind':'cash','code':'cash','values':charge('cash','receive','3.412')},{'kind':'fee','code':'fee','values':charge('fee','pay','0.045')}]}

    # Choices: a zone lists only its pipeline's valued zones and needs the pipeline; points follow the zone.
    valued=[l for l in cat['locations'] if l['valuation'] is not None]
    pipelines={l['pipeline'] for l in valued};assert len(pipelines)>1,pipelines
    assert set(choices(context,draft,'physical','pipeline'))==pipelines
    zones=choices(context,draft,'physical','zone')
    assert set(zones)=={l['zone'] for l in valued if l['pipeline']==p['pipeline']} and not set(zones)&{l['zone'] for l in valued if l['pipeline']!=p['pipeline']},zones
    assert set(choices(context,draft,'physical','delivery_point'))=={l['deliveryPoint'] for l in valued if (l['pipeline'],l['zone'])==(p['pipeline'],p['zone'])}
    unpiped={**draft,'sides':[{**draft['sides'][0],'values':{k:v for k,v in physical.items() if k!='pipeline'}},*draft['sides'][1:]]}
    refused('tickets/choices',{'context':context,'draft':unpiped,'side':'physical','field':'zone'},'ticket_input')
    refused('tickets/choices',{'context':context,'draft':None,'side':'physical','field':'zone'},'ticket_input')

    # Lookups: a tariff is proposed as its exact reference, never as a price, and only for a delivery-stage fee;
    # the market price is read at the pinned close, not the newest.
    unpriced={k:v for k,v in draft['sides'][2]['values'].items() if k!='fee_price'}
    with_fee=lambda values:{**draft,'sides':[*draft['sides'][:2],{**draft['sides'][2],'values':values}]}
    refused('tickets/lookup',{'context':context,'draft':with_fee({**unpriced,'fee_stage':'receipt'}),'side':'fee','field':'fee_tariff'},'ticket_lookup')
    refused('tickets/lookup',{'context':context,'draft':draft,'side':'fee','field':'fee_tariff'},'ticket_lookup')
    refused('tickets/lookup',{'context':context,'draft':draft,'side':'fee','field':'fee_price'},'ticket_input')
    eligible=[t for t in cat['tariffs'] if (t['pipeline'],t['product'],t['quote'])==(p['pipeline'],p['product'],p['valuation']['quote']) and t['validFrom']<='2021-01-01' and t['validTo']>='2021-02-01']
    assert len(eligible)==1,eligible
    tariff=lookup(context,with_fee(unpriced),'fee','fee_tariff')
    assert (tariff['value'],tariff['source'])==(eligible[0]['code'],{'kind':'tariff','label':f"Tariff {eligible[0]['name']}"}),tariff
    tariffed=c.call('tickets/preview',{'context':context,'draft':with_fee({**unpriced,'fee_tariff':tariff['value']})})
    assert tariffed['schedule']['totals']['fee.price']['value']==eligible[0]['rate'],(tariffed['schedule']['totals']['fee.price'],eligible[0]['rate'])
    newest=lookup(context,draft,'physical','price')
    assert newest['value']==view['totals']['physical.market_price']['value'],(newest,view['totals']['physical.market_price'])
    assert newest['source']=={'kind':'market','label':f"{p['deliveryPointName']} · close 2020-12-18"},newest['source']
    earlier={**context,'snapshotId':initial_frame(c.call('workspace'),'2020-12-17')['id']}
    early_view=c.call('tickets/preview',{'context':earlier,'draft':draft})['schedule']
    early=lookup(earlier,draft,'physical','price')
    assert early['value']==early_view['totals']['physical.market_price']['value']!=newest['value'],(early,newest)
    assert early['source']['label'].endswith('close 2020-12-17'),early['source']
    start=lookup(context,draft,'physical','gas_day_start')['value'];minute=p['dayStartMinute']
    assert start==f'{minute//60:02d}:{minute%60:02d}',start
    assert lookup(context,draft,'physical','payment')['value']=='cash,fee'

    # Preview and book: the mapped draft, with a payment and a fee side, is accepted by the composition store.
    preview=c.call('tickets/preview',{'context':context,'draft':draft})
    direct=c.call('gas/composition/preview',body)
    assert preview['schedule']==view and preview['presentValue']==direct['presentValue'] and preview['sides']==direct['sides'],preview['presentValue']
    assert [s['role'] for s in preview['sides']]==['physical','consideration','fee'],preview['sides']
    # The quantity unit is the reference data's for the side's gas path; the description holds no unit symbol.
    assert preview['quantityUnit']==p['valuation']['quantityUnit'] and {f['unit'] for k in description['sides'] for f in k['fields']}=={None,'quantity'},preview['quantityUnit']
    # A refused value names its side and field, so the ticket shows the reason on that field.
    malformed={**draft,'sides':[{**draft['sides'][0],'values':{**physical,'quantity':'10,000'}},*draft['sides'][1:]]}
    error=c.call('tickets/preview',{'context':context,'draft':malformed},expected=422)
    assert (error['code'],error['side'],error['field'])==('numeric_format','physical','quantity'),error
    error=c.call('tickets/preview',{'context':context,'draft':{**draft,'trade':{k:v for k,v in trade.items() if k!='book'}}},expected=422)
    assert (error['code'],error['side'],error['field'])==('ticket_input',None,'book'),error
    operation=str(uuid4());request={'context':context,'draft':draft,'operationId':operation}
    booked=c.call('tickets/book',request)
    assert c.call('tickets/book',request)==booked and c.call('tickets/status',{'operationId':operation})['tradeId']==booked['tradeId'],booked
    retained={**context,'knowledgeMode':'retained','contextId':cat['context']['contextId']}
    refused('tickets/book',{**request,'context':retained,'operationId':str(uuid4())},'historical_read_only')
    refused('tickets/book',{**request,'context':{**context,'contextId':cat['context']['contextId']},'operationId':str(uuid4())},'historical_read_only')
    refused('tickets/book',{**request,'context':{**context,'convention':'trading_workspace_v1'},'operationId':str(uuid4())},'swap_context')
    shared={**context,'convention':'trading_workspace_v1'}
    def fact_for(trade_id,offset=0):
        page=c.call('trading/positions',{'context':shared,'bookIds':[],'portfolioIds':[],'offset':offset,'limit':64})['positions']
        assert page,trade_id
        return next((f for f in page if f['tradeId']==trade_id),None) or fact_for(trade_id,offset+64)
    fact=fact_for(booked['tradeId'])
    opened=c.call('tickets/trade',{'context':context,'tradeId':fact['tradeId'],'bindingId':fact['bindingId'],'revision':fact['revision']})
    assert opened['draft']==draft,(opened['draft'],draft)
    assert opened['schedule']==view and opened['summaries']==preview['summaries'] and opened['scheduleRefusal'] is None and opened['quantityUnit']==preview['quantityUnit']
    assert opened['actions']['amend']['allowed'] is False and opened['actions']['closeOut']['allowed'] is False and opened['actions']['amend']['reason'],opened['actions']
    assert opened['links']==preview['links']==[] and opened['quantityPolicy']=='operational_projection_v1',(opened['links'],opened['quantityPolicy'])
    # A daily quantity recorded through its own operation shows on that day of the booked schedule, with its kind as the source, and in
    # the month's quantity; the amounts stay the contract's. A retry with the same operation receives its first outcome.
    quantities=c.call('gas/quantities/read',{'context':context,'tradeId':fact['tradeId'],'selectionPolicy':opened['quantityPolicy'],'includeHistory':False})
    day=next(r for r in quantities['rows'] if r['sideCode']=='physical' and r['day']=='2021-01-05')
    record={'context':context,'tradeId':fact['tradeId'],'expectedRevision':quantities['expectedRevision'],'operationId':str(uuid4()),'obligationId':day['obligationId'],
            'kind':'nominated','status':'provisional','day':'2021-01-05','quantity':'9000','evidence':'Synthetic nomination','expectedPreviousId':None}
    receipt=c.call('gas/quantities/record',record)
    assert c.call('gas/quantities/record',record)==receipt and c.call('gas/quantities/status',{'operationId':record['operationId']})['revisionId']==receipt['revisionId'],receipt
    recorded=c.call('tickets/trade',{'context':context,'tradeId':fact['tradeId'],'bindingId':fact['bindingId'],'revision':fact['revision']})['schedule']
    cells={r['key']:r['cells'] for r in recorded['rows']};before={r['key']:r['cells'] for r in view['rows']}
    assert cells['2021-01-05']['physical.quantity']=={'value':'-9000','sources':['nominated']},cells['2021-01-05']['physical.quantity']
    assert cells['2021-01-05']['physical.amount']==before['2021-01-05']['physical.amount'] and cells['2021-01-04']==before['2021-01-04'],cells['2021-01-05']
    # The recorded day's amount is still the contract's, and its sources say so: quantity times price need not equal it on that day.
    assert 'contract' in cells['2021-01-05']['physical.amount']['sources'],cells['2021-01-05']['physical.amount']
    assert cells['2021-01']['physical.quantity']['value']=='-309000' and cells['2021-01']['physical.amount']==before['2021-01']['physical.amount'],cells['2021-01']
    refused('tickets/trade',{'context':context,'tradeId':fact['tradeId'],'bindingId':str(uuid4()),'revision':fact['revision']},'position_unavailable')
    # A single-point gas booking opens read-only as the composition it is: one physical side, its payment and its fee, each charged on the
    # side's delivered quantity and paid at the booking's one payment instant. Its schedule, from its own retained programs, is exactly the
    # one a ticket preview of those terms returns; it records no daily quantities, so it names no quantity selection.
    single={**context,**{k:body[k] for k in ['legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','bookId','definitionRevisionId','gasSetupId']},'operationId':str(uuid4()),
            'pipeline':p['pipeline'],'zone':p['zone'],'deliveryPoint':p['deliveryPoint'],'product':p['product'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','quantity':'1000',
            'quantityBasis':'period_total_equal_days','direction':'receive','fixedPrice':'3','usageFee':'0.045','usageTariffCode':None,'usageFeeDirection':'pay','paymentAt':'2021-02-02T00:00:00Z','discounting':False}
    single_booked=c.call('gas/book',single)
    single_fact=fact_for(single_booked['tradeId'])
    point=c.call('tickets/trade',{'context':context,'tradeId':single_fact['tradeId'],'bindingId':single_fact['bindingId'],'revision':single_fact['revision']})
    sides=point['draft']['sides'];paid=sides[1]['values']['cash_payment_at']
    assert datetime.fromisoformat(paid)==datetime.fromisoformat('2021-02-02T00:00:00+00:00') and sides[2]['values']['fee_payment_at']==paid,sides
    charged=lambda kind,direction,price:{f'{kind}_direction':direction,f'{kind}_source':'physical-1',f'{kind}_stage':'delivery',f'{kind}_price':price,f'{kind}_payment_at':paid}
    assert point['draft']=={'product':'physical_gas','version':1,'trade':trade,'sides':[
        {'kind':'physical','code':'physical-1','values':{**physical,'direction':'receive','quantity':'1000','quantity_basis':'period_total_equal_days'}},
        {'kind':'cash','code':'cash-1','values':charged('cash','pay','3')},{'kind':'fee','code':'fee-1','values':charged('fee','pay','0.045')}]},point['draft']
    as_composed=c.call('tickets/preview',{'context':context,'draft':point['draft']})
    exact=lambda value:json.dumps(value,sort_keys=True)
    assert point['schedule'] is not None and exact(point['schedule'])==exact(as_composed['schedule']),(point['schedule'],as_composed['schedule'])
    assert point['summaries']==as_composed['summaries'] and point['scheduleRefusal'] is None and point['quantityUnit']==as_composed['quantityUnit'] and point['links']==[],point['summaries']
    assert point['quantityPolicy'] is None and point['actions']['record']['allowed'] is False and point['actions']['record']['reason'],point['actions']
    assert point['actions']['amend']==opened['actions']['amend'] and point['actions']['closeOut']==opened['actions']['closeOut'] and opened['actions']['record']['allowed'] is True,opened['actions']

    # Historical choices: a point retired by a later gas setup stays listed, and priced, in the earlier context.
    # Publication is append-only, so the check leaves the setup as it found it by publishing the found configuration again.
    c.call('profile',{'profile':'editor'})
    authored=c.call('gas-setup/catalog');original=authored['configuration'];retired='synthetic_gas_point_d'
    without={**original,'paths':[x for x in original['paths'] if x['point']!=retired],'meters':[m for m in original['meters'] if m['point']!=retired]}
    def publish(configuration,base):
        saved=c.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':base,'configuration':configuration})
        c.call('profile',{'profile':'approver'});c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':saved['setupId']});c.call('profile',{'profile':'editor'})
        return saved['setupId']
    later=publish(without,authored['activeSetupId'])
    order=next(v['publication_order'] for v in c.call('gas-setup/catalog')['versions'] if v['id']==later)
    c.call('profile',{'profile':'trader'})
    # A gas setup is named by its publication order.
    named=c.call('tickets/choices',{'context':context,'draft':{'product':'physical_gas','version':1,'trade':{},'sides':[]},'side':None,'field':'gas_setup'})['choices']
    assert named==[{'value':later,'label':f'Published setup {order}'}],named
    d=next(l for l in cat['locations'] if l['deliveryPoint']==retired)
    at_d=lambda setup_id:{**draft,'trade':{**{k:v for k,v in trade.items() if k!='gas_setup'},**setup(setup_id)},'sides':[{**draft['sides'][0],'values':{**physical,'pipeline':d['pipeline'],'zone':d['zone'],'delivery_point':retired,'product':d['product']}}]}
    assert retired in choices(retained,at_d(cat['gasSetupId']),'physical','delivery_point')
    assert retired not in choices(context,at_d(later),'physical','delivery_point')
    refused('tickets/choices',{'context':context,'draft':at_d(cat['gasSetupId']),'side':'physical','field':'delivery_point'},'ticket_input')
    then=lookup(retained,at_d(cat['gasSetupId']),'physical','price')
    assert then['value'] and then['source']['label']==f"{d['deliveryPointName']} · close 2020-12-18",then
    c.call('profile',{'profile':'editor'});publish(original,later)
    assert c.call('gas-setup/catalog')['configuration']==original,'The deal ticket check must leave the gas setup configuration it found.'

    # Historical party choices: a counterparty revised after a context was captured lists its earlier revision there.
    party={'kind':'counterparty','id':None,'expectedRevision':None,'code':'ticket_counterparty','name':'Synthetic ticket counterparty','effectiveAt':'2020-12-01T00:00:00Z'}
    first=c.call('parties/save',{**party,'operationId':str(uuid4())})
    c.call('profile',{'profile':'trader'})
    captured={**context,'knowledgeMode':'retained','contextId':c.call('gas/catalog',context)['context']['contextId']}
    c.call('profile',{'profile':'editor'})
    second=c.call('parties/save',{**party,'operationId':str(uuid4()),'id':first['id'],'expectedRevision':1,'name':'Revised synthetic ticket counterparty','effectiveAt':'2020-12-02T00:00:00Z'})
    c.call('profile',{'profile':'trader'})
    listed=lambda at:[v for v in choices(at,{'product':'physical_gas','version':1,'trade':{},'sides':[]},None,'counterparty') if v.startswith(first['id']+'@')]
    assert second['revision']==2 and listed(captured)==[f"{first['id']}@1"] and listed(context)==[f"{first['id']}@2"],(listed(captured),listed(context))
    print('PASS deal ticket operations: description, catalog and party choices by parent and context, exact tariff references and labelled market prices at the pinned close, mapped drafts preview and book idempotently with payment and fee sides, working-data-only booking, and a booked trade reopens as its draft',flush=True)
