"""Versioned gas setup authoring against an owned synthetic preview."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'editor'})
    setup=c.call('gas-setup/catalog')
    assert setup['canSave'] and not setup['canPublish']
    c.call('gas-setup/validate',{'setupId':str(uuid4()),'from':'2021-01-01','to':'2021-02-01'},expected=422)
    c.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':setup['activeSetupId']},expected=400)
    payload=deepcopy(setup['configuration'])
    save={'operationId':str(uuid4()),'baseSetupId':setup['activeSetupId'],'configuration':payload}
    draft=c.call('gas-setup/save',save)
    assert c.call('gas-setup/save',save)==draft
    validation=c.call('gas-setup/validate',{'setupId':draft['setupId'],'from':'2021-01-01','to':'2021-02-01'})
    assert validation['valid'] and len(validation['points'])==5
    c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':draft['setupId']},expected=403)
    c.call('profile',{'profile':'trader'})
    w=c.call('workspace');frame=initial_frame(w,'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    catalog=c.call('gas/catalog',context);a=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    body={**context,**trade_booking(),'operationId':str(uuid4()),'gasSetupId':catalog['gasSetupId'],'bookId':catalog['books'][0]['id'],
          'definitionRevisionId':a['definitionRevisionId'],'pipeline':a['pipeline'],'zone':a['zone'],'deliveryPoint':a['deliveryPoint'],'product':a['product'],
          'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','quantity':'1000','quantityBasis':'period_total_equal_days','direction':'receive','fixedPrice':'3',
          'usageFee':'0.1','usageTariffCode':None,'usageFeeDirection':'pay','paymentAt':'2021-02-02T00:00:00Z','discounting':False}
    before=c.call('gas/preview',body);booked=c.call('gas/book',body)
    c.call('profile',{'profile':'editor'})
    changed=deepcopy(payload);next(g for g in changed['groups'] if g['code']=='synthetic_gas_group_a')['adjustment']='-0.2'
    next(p for p in changed['paths'] if p['point']=='synthetic_gas_point_d')['zone']='synthetic_gas_zone_b'
    next(p for p in changed['paths'] if p['point']=='synthetic_gas_point_d')['dayStartMinute']=120
    next(m for m in changed['meters'] if m['code']=='synthetic_gas_meter_a_jan')['identifier']='SYN-METER-A-UPDATED'
    change={'operationId':str(uuid4()),'baseSetupId':setup['activeSetupId'],'configuration':changed}
    new=c.call('gas-setup/save',change)
    c.call('profile',{'profile':'approver'})
    publish={'operationId':str(uuid4()),'setupId':new['setupId']}
    receipt=c.call('gas-setup/publish',publish);assert c.call('gas-setup/publish',publish)==receipt
    c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':draft['setupId']},expected=409)
    c.call('profile',{'profile':'trader'})
    after=c.call('gas/catalog',context);assert after['gasSetupId']==new['setupId']
    earlier=c.call('gas/catalog',{**context,'knowledgeMode':'retained','contextId':catalog['context']['contextId']})
    assert earlier['gasSetupId']==catalog['gasSetupId'], 'Later setup publications cannot enter an earlier certified context'
    assert next(p for p in after['locations'] if p['deliveryPoint']=='synthetic_gas_point_d')['zone']=='synthetic_gas_zone_b'
    moved=c.call('gas/preview',{**body,'operationId':str(uuid4()),'gasSetupId':new['setupId'],'zone':'synthetic_gas_zone_b','deliveryPoint':'synthetic_gas_point_d'})
    assert moved['valuation']['deliveryRows'][0]['start'].startswith('2021-01-01T02:00:00')
    assert Decimal(moved['valuation']['presentValue'])==-50
    new_body={**body,'operationId':str(uuid4()),'gasSetupId':new['setupId']}
    changed_value=c.call('gas/preview',new_body)
    assert Decimal(changed_value['valuation']['presentValue'])==0
    assert changed_value['valuation']['deliveryRows'][0]['meter']['setupId']==new['setupId']
    assert changed_value['valuation']['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-UPDATED'
    tariff=next(t for t in after['tariffs'] if t['name']=='synthetic_gas_usage_a')
    assert Decimal(c.call('gas/preview',{**new_body,'usageFee':None,'usageTariffCode':tariff['code']})['valuation']['presentValue'])==-20
    second=c.call('gas/book',new_body)
    assert Decimal(c.call('gas/editor',{'context':context,'tradeId':second['tradeId']})['valuation']['presentValue'])==0
    assert c.call('gas/editor',{'context':context,'tradeId':booked['tradeId']})['valuation']==before['valuation']
    c.call('profile',{'profile':'editor'})
    bad=deepcopy(changed);bad['meters'].append(deepcopy(bad['meters'][0]));bad['meters'][-1]['code']='synthetic_duplicate'
    invalid=c.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':new['setupId'],'configuration':bad})
    assert not c.call('gas-setup/validate',{'setupId':invalid['setupId'],'from':'2021-01-01','to':'2021-02-01'})['valid']
    c.call('profile',{'profile':'approver'})
    c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':invalid['setupId']},expected=422)
    c.call('profile',{'profile':'viewer'})
    c.call('gas-setup/save',save,expected=403)
    print('PASS gas setup editor: immutable drafts, validation, role separation, idempotent publish, conflict refusal and old/new booked price pins',flush=True)
