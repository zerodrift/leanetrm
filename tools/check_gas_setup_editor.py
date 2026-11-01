"""Versioned gas setup authoring against an owned synthetic preview."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import gas_purchase


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
    catalog=c.call('gas/catalog',context)
    body=gas_purchase(context,catalog)
    # A booking pins the setup it was priced under, and a reopened booking keeps it after a later publication.
    preview=lambda b:c.call('gas/composition/preview',b)
    reopened=lambda trade:c.call('gas/composition/editor',{'context':context,'tradeId':trade})['valuation']
    with_setup=lambda b,setup,**physical:{**b,'operationId':str(uuid4()),'gasSetupId':setup,'physicalSides':[{**b['physicalSides'][0],**physical}]}
    before=preview(body);booked=c.call('gas/composition/book',body)
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
    moved=preview(with_setup(body,new['setupId'],zone='synthetic_gas_zone_b',deliveryPoint='synthetic_gas_point_d'))
    assert moved['sides'][0]['deliveryRows'][0]['start'].startswith('2021-01-01T02:00:00')
    assert Decimal(moved['presentValue'])==-50
    new_body=with_setup(body,new['setupId'])
    changed_value=preview(new_body)
    assert Decimal(changed_value['presentValue'])==0
    assert changed_value['sides'][0]['deliveryRows'][0]['meter']['setupId']==new['setupId']
    assert changed_value['sides'][0]['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-UPDATED'
    tariff=next(t for t in after['tariffs'] if t['name']=='synthetic_gas_usage_a')
    tariffed={**new_body,'cashSides':[new_body['cashSides'][0],{**new_body['cashSides'][1],'price':None,'tariffCode':tariff['code']}]}
    assert Decimal(preview(tariffed)['presentValue'])==-20
    second=c.call('gas/composition/book',new_body)
    assert Decimal(reopened(second['tradeId'])['presentValue'])==0
    first=reopened(booked['tradeId']);assert first=={k:before[k] for k in first},(first,before)
    c.call('profile',{'profile':'editor'})
    bad=deepcopy(changed);bad['meters'].append(deepcopy(bad['meters'][0]));bad['meters'][-1]['code']='synthetic_duplicate'
    invalid=c.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':new['setupId'],'configuration':bad})
    assert not c.call('gas-setup/validate',{'setupId':invalid['setupId'],'from':'2021-01-01','to':'2021-02-01'})['valid']
    c.call('profile',{'profile':'approver'})
    c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':invalid['setupId']},expected=422)
    c.call('profile',{'profile':'viewer'})
    c.call('gas-setup/save',save,expected=403)
    print('PASS gas setup editor: immutable drafts, validation, role separation, idempotent publish, conflict refusal and old/new booked price pins',flush=True)
