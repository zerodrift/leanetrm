"""Deal ticket schedule oracles for a composed gas trade in an owned synthetic database.

January 2021 is the first synthetic gas forward month after the 2020-12-18 frame: 31 gas days.
"""
from datetime import datetime
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


# The schedule columns each side kind declares in the physical_gas v1 ticket description, by booked role.
DECLARED={'physical':{'quantity','market_price','amount'},'consideration':{'quantity','price','amount'},'fee':{'quantity','price','amount'}}


def assert_declared(view,body):
    """Every cell key is a declared column of its side's kind or a trade column, on every row and the totals."""
    roles={**{s['code']:'physical' for s in body['physicalSides']},**{s['code']:s['role'] for s in body['cashSides']}}
    expected={f'{code}.{column}' for code,role in roles.items() for column in DECLARED[role]}|{'trade.net_cash','trade.payment_at'}
    for cells in [view['totals'],*(r['cells'] for r in view['rows'])]:
        assert set(cells)==expected,sorted(set(cells)^expected)


def run_checks(url):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    cat=c.call('gas/catalog',context)
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
    assert_declared(view,body)
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
    # daily quantity is 1000/1.02 = 50000/51 MMBtu and its quantities and amounts are exact a/b ratios.
    b_point=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_b')
    linked={**body,'operationId':str(uuid4()),'cashSides':[{**cash('fee','fee','pay','0.1'),'source':'delivery'}],
            'physicalSides':[{**body['physicalSides'][0],'code':'receipt','direction':'receive','quantity':'1000'},
                             {**body['physicalSides'][0],'code':'delivery','pipeline':b_point['pipeline'],'zone':b_point['zone'],'deliveryPoint':b_point['deliveryPoint'],
                              'product':b_point['product'],'quantity':None,'link':{'source':'receipt','formula':'gross_divided_by_one_plus_fraction','fraction':'0.02'}}]}
    def assert_linked(value):
        view=value['schedule'];assert value['scheduleRefusal'] is None and view is not None,value['scheduleRefusal']
        assert_declared(view,linked);totals=view['totals']
        for side in value['sides']:
            assert number(totals[side['code']+'.amount'])==Fraction(side['presentValue']),(side['code'],totals[side['code']+'.amount'],side['presentValue'])
        assert totals['delivery.quantity']['value']=='-1550000/51',totals['delivery.quantity']
        fuel=value['links'][0]['fuel']
        # Quantities carry the side's direction: the receipt is positive and the delivery negative.
        assert number(totals['receipt.quantity'])-(-number(totals['delivery.quantity']))==Fraction(int(fuel['numerator']),int(fuel['denominator'])),(totals,fuel)
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
    print('PASS deal ticket: gas preview and booked terms return one exact month/day schedule whose totals and amounts agree with side values; mixed gas-day clocks keep their valuation with a schedule refusal; linked receipt and delivery conserve fuel exactly at the current and an earlier As of frame',flush=True)
