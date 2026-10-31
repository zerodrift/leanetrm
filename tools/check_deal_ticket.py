"""Deal ticket schedule oracles for a composed gas trade in an owned synthetic database.

January 2021 is the first synthetic gas forward month after the 2020-12-18 frame: 31 gas days.
"""
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


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
    view=value['schedule']
    assert view['levels']==['month','day'],view['levels']
    months=[r for r in view['rows'] if r['level']=='month'];days=[r for r in view['rows'] if r['level']=='day']
    assert len(months)==1 and len(days)==31 and all(d['parent']==months[0]['key'] for d in days),[r['key'] for r in view['rows']]
    month=months[0]['cells'];number=lambda cell:Decimal(cell['value'])
    delivered=sum(Decimal(r['quantity']['numerator'])/Decimal(r['quantity']['denominator']) for r in value['sides'][0]['deliveryRows'])
    # Quantities and amounts carry the side's signed membership, as its present value does: delivering gas is negative.
    assert delivered==310000 and number(month['physical.quantity'])==-delivered,(month['physical.quantity'],delivered)
    assert all(number(d['cells']['physical.quantity'])==-10000 for d in days)
    assert number(month['cash.quantity'])==310000 and number(month['cash.amount'])==number(month['cash.quantity'])*Decimal('3.412'),month['cash.amount']
    assert number(month['fee.amount'])==-number(month['cash.quantity'])*Decimal('0.045'),month['fee.amount']
    assert number(month['trade.net_cash'])==number(month['cash.amount'])+number(month['fee.amount'])==Decimal('1043770'),month['trade.net_cash']
    assert month['trade.payment_at'] is not None and all(d['cells']['cash.amount']['value'] is None for d in days)
    assert view['totals']==month,(view['totals'],month)
    for side in value['sides']:
        assert number(view['totals'][side['code']+'.amount'])==Decimal(side['presentValue']),(side['code'],side['presentValue'])
    assert value['summaries']==[{'side':code,'billedQuantity':view['totals'][code+'.quantity']['value'],'amount':view['totals'][code+'.amount']['value'],'againstMarket':None}
                                for code in ['physical','cash','fee']],value['summaries']
    booked=c.call('gas/composition/book',body)
    assert c.call('gas/composition/book',body)==booked
    reopened=c.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']})['valuation']
    assert reopened['schedule']==view and reopened['summaries']==value['summaries']
    print('PASS deal ticket: gas preview and booked terms return one exact month/day schedule whose totals and amounts agree with side values',flush=True)
