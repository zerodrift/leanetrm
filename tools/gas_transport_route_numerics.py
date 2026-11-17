"""Independent decimal/native adapters preserve exact fractional route arithmetic."""
from copy import deepcopy
from uuid import uuid4
from gas_transport_capacity_fixture import identity
from gas_transport_route_managed import checkpoint


def microseconds(f,refuses):
    definition=f.definition('synthetic_route_microsecond_capacity')
    ent=definition['entitlementId'];path=identity('gas_transport_path_v1:synthetic_micro_path',ent)
    window=dict(**{'from':'2020-12-20T00:00:00.000000Z'},through='2020-12-20T00:00:00.000010Z',maximumPerHour='10000000000')
    definition.update(through=window['through'],agreement=[dict(window)],
        paths=[dict(pathKey='synthetic_micro_path',pathId=path,pipeline='test_pipeline_a',receiptZone='synthetic_gas_zone_a',receiptPoint='synthetic_route_point_a',deliveryZone='synthetic_gas_zone_a',deliveryPoint='synthetic_route_point_h',product='test_gas',limits=[dict(window)])],
        receipts=[dict(point='synthetic_route_point_a',limits=[dict(window)])],deliveries=[dict(point='synthetic_route_point_h',limits=[dict(window)])])
    f.record(definition);facts=f.read(ent);op=str(uuid4())
    claims=[dict(claimId=identity('gas_transport_claim_v1',op),pathId=path,receiptFrom=window['from'],receiptTo='2020-12-20T00:00:00.000003Z',receiptQuantity='0.75',
        deliveryFrom='2020-12-20T00:00:00.000007Z',deliveryTo=window['through'],deliveryQuantity='0.5')]
    plan=dict(kind='plan',entitlementId=ent,expectedStateVersion=facts['stateVersion'],expectedPreviousId=None,businessKey='synthetic_microsecond_source',versionId=facts['version']['id'],admissionPolicy='complete_feasible_replacement_v1',evidence='Synthetic native decimal exact source',claims=claims)
    f.record(plan,f.capacity_witness(facts,claims),operation=op)
    graph=f.route_definition('synthetic_exact_microsecond_route')
    graph['nodes']=[n for n in graph['nodes'] if n['nodeKey'] in ['a','h']]
    next(n for n in graph['nodes'] if n['nodeKey']=='h')['role']='external_delivery_v1'
    graph['segments']=[s for s in graph['segments'] if s['segmentKey']=='ah']
    graph['segments'][0].update(entitlementId=ent,pathId=path,fuelFraction=dict(n='1',d='3'),transitMicroseconds='7')
    f.route_record(graph);facts=f.route_read(graph['routeId']);chosen=f.route_plan(facts);op=str(uuid4());proof=f.route_witness(facts,chosen,op)
    segment=proof['segments'][0]
    assert segment['fuelQuantity']==dict(n='1',d='4') and segment['expectedDeliveryQuantity']==dict(n='1',d='2')
    assert segment['intervals'][0]['expectedPerHour']==dict(n='600000000',d='1') and proof['nodes']==[]
    quantities=[dict(segmentKey='ah',receipt='0.75',fuel='0.25',loss='0',expectedDelivery='0.5',delivery='0.5')]
    managed=checkpoint(f,graph['routeId'],'plan',operation=op,command=chosen,quantities=quantities,segments=1,nodes=0,capacities=1)
    proof=managed['witness']
    outcome=f.route_record(chosen,proof,operation=op)
    checkpoint(f,graph['routeId'],'current',revision=op,quantities=quantities,segments=1,nodes=0,capacities=1)
    # A coherent decimal native source cannot become an approximation to an
    # unrepresentable rational result. Publish the approximate source as raw
    # capacity, then independently refuse its route conservation assertion.
    approximate=deepcopy(claims);op2=str(uuid4());approximate[0].update(claimId=identity('gas_transport_claim_v1',op2),receiptQuantity='1',deliveryQuantity='0.666666666666666667')
    source=f.read(ent);next_plan={**plan,'expectedStateVersion':source['stateVersion'],'expectedPreviousId':source['plans'][0]['id'],'evidence':'Synthetic decimal approximation source','claims':approximate}
    f.record(next_plan,f.capacity_witness(source,approximate),operation=op2)
    fresh=f.route_read(graph['routeId']);proposal=f.route_plan(fresh);op3=str(uuid4());bad=f.route_witness(fresh,proposal,op3)
    assert not bad['feasible']
    bad['feasible']=True
    refuses(lambda:f.route_record(proposal,bad,operation=op3),'gas_transport_route_conservation')
    assert f.route_read(graph['routeId'])['plans'][0]['id']==outcome['revisionId']
    print('PASS exact3µs duration/7µs transit, fuel1/3 of0.75=0.25 and delivery0.5 produce600000000/hour; approximate decimal2/3 refuses without rewriting retained assessment.',flush=True)
