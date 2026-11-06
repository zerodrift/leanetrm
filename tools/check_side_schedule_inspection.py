"""Exact authorized schedule pages and retained calculation evidence."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient,initial_frame


def check_ppa(client,frame,trade,binding,native_rows):
    position=dict(context=dict(snapshotId=frame,convention='trading_workspace_v1',knowledgeMode='current'),tradeId=trade,bindingId=binding,revision=1,instrumentType='ppa')
    request=dict(position=position,sideSlot=None,contribution=None,offset=0,limit=7)
    directory=client.call('trading/side-schedules',request)
    assert len(directory['sides'])==2 and all(s['windows']==[dict(id='2031-11-01',label='Nov 2031')] for s in directory['sides'])
    position['context'].update(knowledgeMode='retained',contextId=directory['contextId'])
    side=directory['sides'][0]
    selected={**request,'sideSlot':side['slot'],'window':'2031-11-01'}
    pages=[client.call('trading/side-schedules',{**selected,'offset':n}) for n in (0,7,14,21)]
    assert all(p['contextId']==directory['contextId'] and p['window']=='2031-11-01' and p['page']['total']==25 for p in pages)
    inspected=[r for p in pages for r in p['page']['rows']]
    assert [r['start'] for r in inspected]==[r['interval']['start'] for r in native_rows]
    assert [Fraction(r['amount']) for r in inspected]==[Fraction(fraction_text(r['amount'])) for r in native_rows]
    detail=client.call('trading/side-schedules',{**selected,'contribution':inspected[0]['key'],'limit':128})['page']
    assert detail['valueBasis']=='unit_side_v1' and any(r['operation']=='price_component' for r in detail['rows'])
    client.call('trading/side-schedules',{**selected,'window':None},expected=422)
    client.call('trading/side-schedules',{**selected,'window':'2031-12-01'},expected=422)
    client.call('profile',dict(profile='viewer'));client.call('trading/side-schedules',selected,expected=403);client.call('profile',dict(profile='trader'))
    print('PASS shared PPA inspection: native DST rows, bounded windows, exact retained context, calculation and current authority',flush=True)


def fraction_text(value):
    return str(value) if isinstance(value,str) else str(value['numerator'])+'/'+str(value['denominator'])


def check_swap(client,frame,saved,instrument_type='financial_swap'):
    position=dict(context=dict(snapshotId=frame['id'],convention='trading_workspace_v1',knowledgeMode='current'),tradeId=saved['tradeId'],bindingId=saved['bindingId'],revision=saved['revision'],instrumentType=instrument_type)
    request=dict(position=position,sideSlot=None,contribution=None,offset=0,limit=128)
    directory=client.call('trading/side-schedules',request)
    position['context'].update(knowledgeMode='retained',contextId=directory['contextId'])
    native=client.call('swaps/editor',dict(context={**position['context'],'convention':'independent_side_financial_swap_v2'},tradeId=saved['tradeId'],revision=saved['revision']))['valuation']
    assert len(directory['sides'])==len(native['sides'])==2
    for side,valued in zip(directory['sides'],native['sides']):
        selected={**request,'sideSlot':side['slot']}
        read=client.call('trading/side-schedules',selected)
        assert read['contextId']==directory['contextId'] and read['page']['rows'] and not side['windows']
        assert sum(Fraction(r['presentValue']) for r in read['page']['rows'])==sum(Fraction(p['tradePresentValue']) for p in valued['payments'])
        detail=client.call('trading/side-schedules',{**selected,'contribution':read['page']['rows'][0]['key']})['page']
        assert detail['valueBasis']=='unit_side_v1' and any(r['operation']=='pv' for r in detail['rows'])
    client.call('profile',dict(profile='viewer'));client.call('trading/side-schedules',request,expected=403);client.call('profile',dict(profile='trader'))
    print('PASS shared '+instrument_type+' inspection: native Side membership, signed holding PV, calculation evidence and current authority',flush=True)


def check_swap_head(client,query,manifest,frame,saved,booking):
    position=dict(context=dict(snapshotId=frame['id'],convention='trading_workspace_v1',knowledgeMode='current'),tradeId=saved['tradeId'],bindingId=saved['bindingId'],revision=saved['revision'],instrumentType='financial_swap')
    request=dict(position=position,sideSlot=None,contribution=None,offset=0,limit=8)
    original=client.call('trading/side-schedules',request)
    pinned=deepcopy(request);pinned['position']['context'].update(knowledgeMode='retained',contextId=original['contextId'])
    change=deepcopy(booking);change.update(snapshotId=frame['id'],operationId=str(uuid4()),tradeId=saved['tradeId'],expectedRevision=1,effectiveAt=frame['at'],reason='Synthetic schedule head visibility')
    change['fixed']['price']='3.125';change['floating']['spread']='0.0125'
    amended=client.call('swaps/amend',change)
    assert client.call('trading/side-schedules',request,expected=422)['code']=='position_unavailable'
    new_instrument=amended['instrumentId']
    query(f"CREATE POLICY synthetic_schedule_head_hidden ON trading.swap_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(instrument_id<>'{new_instrument}');")
    try:
        client.call('trading/side-schedules',request,expected=403)
        assert client.call('trading/side-schedules',pinned)['sides']==original['sides']
    finally:query('DROP POLICY synthetic_schedule_head_hidden ON trading.swap_instruments;')
    print('PASS shared swap inspection: hidden latest required child cannot resurrect an older head; exact prior knowledge still reads',flush=True)


def run_checks(url,query,manifest,saved):
    c=PreviewClient(url);c.bootstrap();c.call('profile',dict(profile='trader'))
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    position=dict(context=dict(snapshotId=frame['id'],convention='trading_workspace_v1',knowledgeMode='current'),
                  tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=saved['revision'],instrumentType='physical_gas')
    body=dict(position=position,sideSlot=None,contribution=None,offset=0,limit=4)
    ticket=c.call('tickets/trade',dict(context={**position['context'],'convention':'physical_gas_fixed_quantity_v4'},tradeId=position['tradeId'],bindingId=position['bindingId'],revision=position['revision']))
    assert len(ticket['sideSlots'])==2 and set(ticket['sideSlots'].values())=={0,1},ticket['sideSlots']
    directory=c.call('trading/side-schedules',body)
    assert [s['family'] for s in directory['sides']]==['gas','floating']
    position['context'].update(knowledgeMode='retained',contextId=directory['contextId'])
    body['sideSlot']=1
    value=c.call('trading/side-schedules',body);page=value['page']
    assert value['contextId']==directory['contextId'] and value['sideId']==directory['sides'][1]['sideId']
    assert page['total']==59 and len(page['rows'])==4 and page['manifest']==directory['sides'][1]['manifest']
    first=page['rows'][0]
    assert first['deliveryFrom']=='2021-01-01' and first['deliveryTo']=='2021-01-02' and first['paymentAt'].startswith('2021-02-11T00:00:00')
    assert [Fraction(first[k]) for k in ['quantity','price','amount','discountFactor','presentValue']]==[-100,Fraction('3.02'),-302,1,-302]
    assert first['lifecycle']['due']=='NotDue' and first['lifecycle']['cash']=='NotTracked' and first['lifecycle']['delivery']=='Scheduled'
    second=c.call('trading/side-schedules',{**body,'offset':4})['page']
    assert second['rows'][0]['deliveryFrom']=='2021-01-05' and second['manifest']==page['manifest']
    calculation=c.call('trading/side-schedules',{**body,'contribution':first['key'],'limit':128})['page']
    assert calculation['valueBasis']=='unit_side_v1' and calculation['contribution']==first['key']
    steps=calculation['rows'];assert {'price_lookup','weighted_price','amount','discount','pv','output_guard'}<={r['operation'] for r in steps}
    source=next(r for r in steps if r['operation']=='price_lookup')
    assert source['contractFrom']=='2021-01-01' and source['contractTo']=='2021-01-02' and source['fixingAt'].startswith('2021-01-01T12:00:00') and source['lineage']
    assert Fraction(next(r['value'] for r in steps if r['operation']=='amount'))==Fraction('3.02')
    # An operational publication after the ticket read must not change a retained inline grid.
    quantity_context={**position['context'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    quantity_context.pop('contextId',None)
    quantities=c.call('gas/quantities/read',dict(context=quantity_context,tradeId=saved['tradeId'],selectionPolicy='operational_projection_v1'))
    obligation=next(row for row in quantities['rows'] if row['day']=='2021-01-01')
    c.call('gas/quantities/record',dict(context=quantity_context,tradeId=saved['tradeId'],expectedRevision=saved['revision'],operationId=str(uuid4()),
        obligationId=obligation['obligationId'],kind='nominated',status='provisional',day='2021-01-01',quantity='105',evidence='Synthetic inspection context check',expectedPreviousId=None))
    pinned_context={**quantity_context,'knowledgeMode':'retained','contextId':ticket['context']['contextId']}
    pinned_ticket=c.call('tickets/trade',dict(context=pinned_context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=saved['revision']))
    fresh_ticket=c.call('tickets/trade',dict(context=quantity_context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=saved['revision']))
    side=next(side for side in ticket['draft']['sides'] if side['kind']=='physical')['code']
    def day_quantity(read):return next(row for row in read['schedule']['rows'] if row['key']=='2021-01-01')['cells'][side+'.quantity']['value']
    assert day_quantity(ticket)==day_quantity(pinned_ticket)=='100' and day_quantity(fresh_ticket)=='105'
    pinned_position={**position,'context':{**pinned_context,'convention':'trading_workspace_v1'}}
    pinned_directory=c.call('trading/side-schedules',dict(body,position=pinned_position,sideSlot=None,offset=0))
    assert pinned_directory['contextId']==ticket['context']['contextId'] and ticket['snapshotId']==frame['id']
    later=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:'))
    in_term=deepcopy(body);in_term.update(sideSlot=0);in_term['position']['context']=dict(snapshotId=later['id'],convention='trading_workspace_v1',knowledgeMode='current')
    plan=c.call('trading/side-schedules',in_term)['page']
    assert plan['rows'][0]['lifecycle']['delivery']=='Elapsed' and plan['rows'][0]['lifecycle']['fulfillment']=='NotTracked'
    assert all(r['presentValue'] is None and r['lifecycle']['unavailableReason'] for r in plan['rows'])
    assert Fraction(c.call('trading/side-schedules',body)['page']['rows'][0]['presentValue'])==-302
    for bad in [{**body,'sideSlot':99},{**body,'limit':129},{**body,'contribution':'0:0'},
                {**body,'position':{**position,'revision':1}},{**body,'position':{**position,'bindingId':str(uuid4())}}]:
        c.call('trading/side-schedules',bad,expected=422)
    c.call('profile',dict(profile='viewer'));c.call('trading/side-schedules',body,expected=403)
    c.call('profile',dict(profile='trader'))
    # A current source denial cannot be bypassed by retaining a context or page key.
    query("CREATE POLICY synthetic_inspection_hidden ON trading.gas_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);")
    try:assert c.call('trading/side-schedules',body,expected=422)['code']=='position_facts'
    finally:query('DROP POLICY synthetic_inspection_hidden ON trading.gas_instruments;')
    print('PASS shared Side inspection: exact retained binding/context, daily dates, signed values, DF/PV, native calculation/source evidence, paging and authority refusals',flush=True)
    check_due_swap(c,frame,later)


def check_due_swap(client,before,later):
    from preview_fixture import trade_booking
    context=dict(snapshotId=before['id'],convention='independent_side_financial_swap_v2',knowledgeMode='current')
    catalog=client.call('swaps/catalog',context);index=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    def payment(days):return dict(frequency='monthly',rule='days_after_period_end',days=days,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    body={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':index['definitionRevisionId'],
          'commodity':index['commodity'],'quantity':'10','fixedCoefficient':'-1','discounting':False,
          'fixed':dict(currency=index['currencyCode'],deliveryFrom='2020-12-01',deliveryTo='2021-01-01',price='3',paymentSchedule=payment(1)),
          'floating':dict(currency=index['currencyCode'],deliveryFrom='2021-02-01',deliveryTo='2021-03-01',curve=index['curve'],index=index['index'],
                          resetFrequency='monthly',averagingMethod='none',spread='0',paymentSchedule=payment(18),settlement=dict(mode='curve_target',index=None,projectionPolicy=None))}
    saved=client.call('swaps/book',body)
    position=dict(context=dict(snapshotId=later['id'],convention='trading_workspace_v1',knowledgeMode='current'),tradeId=saved['tradeId'],bindingId=saved['bindingId'],revision=1,instrumentType='financial_swap')
    read=client.call('trading/side-schedules',dict(position=position,sideSlot=0,contribution=None,offset=0,limit=8))['page']
    assert read['rows'] and all(r['lifecycle']['unavailableReason']=='side_payment_due' and r['presentValue'] is None and r['amount'] is None for r in read['rows'])
    assert all(r['lifecycle']['cash']=='NotTracked' for r in read['rows'])
    print('PASS shared swap inspection: due payment without recorded cash exposes its retained plan without inventing value or paid state',flush=True)


def check_physical(url,query,manifest,saved,instrument_type,expected):
    c=PreviewClient(url);c.bootstrap();c.call('profile',dict(profile='trader'))
    workspace=c.call('workspace');frame=initial_frame(workspace,'2020-12-18')
    position=dict(context=dict(snapshotId=frame['id'],convention='trading_workspace_v1',knowledgeMode='current'),
                  tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=saved['revision'],instrumentType=instrument_type)
    request=dict(position=position,sideSlot=None,contribution=None,offset=0,limit=128)
    directory=c.call('trading/side-schedules',request)
    assert len(directory['sides'])==len(expected)
    position['context'].update(knowledgeMode='retained',contextId=directory['contextId'])
    for side,total in zip(directory['sides'],expected):
        selected={**request,'sideSlot':side['slot']}
        page=c.call('trading/side-schedules',selected)['page']
        assert page['manifest']==side['manifest'] and page['rows']
        assert page['total']==len(page['rows'])
        assert sum(Fraction(row['presentValue']) for row in page['rows'])==total
        assert all(row['start']<row['end'] and row['lifecycle']['delivery']=='Scheduled' for row in page['rows'])
        row=page['rows'][0]
        detail=c.call('trading/side-schedules',{**selected,'contribution':row['key']})['page']
        assert detail['valueBasis']=='unit_side_v1' and detail['rows']
        if side['slot']==0:
            source=next(step for step in detail['rows'] if step['operation']=='price_lookup')
            assert source['contractFrom']=='2021-01-01' and source['contractTo']=='2021-02-01' and source['lineage']
            assert source['fixingAt'] is not None
        else:
            assert row['paymentAt'] and row['lifecycle']['cash']=='NotTracked' and row['lifecycle']['due']=='NotDue'
    later=next(frame for frame in workspace['frames'] if frame['at'].startswith('2021-01-15T08:'))
    started=deepcopy(request);started['sideSlot']=0
    started['position']['context']=dict(snapshotId=later['id'],convention='trading_workspace_v1',knowledgeMode='current')
    plan=c.call('trading/side-schedules',started)['page']
    reason='crude_lifecycle' if instrument_type=='physical_crude' else 'lng_delivery_lifecycle'
    assert all(row['lifecycle']['unavailableReason']==reason and row['presentValue'] is None and row['amount'] is None for row in plan['rows'])
    assert any(row['lifecycle']['delivery'] in ('Elapsed','InProgress') for row in plan['rows'])
    c.call('profile',dict(profile='viewer'));c.call('trading/side-schedules',request,expected=403)
    c.call('profile',dict(profile='trader'))
    print(f'PASS shared {instrument_type} Side inspection: signed values, native intervals and source evidence, exact retained context, started-plan refusal and current authority',flush=True)
