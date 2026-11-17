"""Qualify retained route source membership and exact conservation on stock PostgreSQL."""
from copy import deepcopy
import json
from uuid import uuid4

from check_reference_editing import main
from gas_transport_route_fixture import RouteFixture,fixture
from gas_transport_route_authority import authority
from gas_transport_route_seals import seals
from gas_transport_route_future import future_memberships
from gas_transport_route_numerics import microseconds
from gas_transport_route_concurrency import concurrency
from gas_transport_route_managed import checkpoint


def refuses(action,code):
    try:action()
    except RuntimeError as error:
        assert code in str(error),str(error)
    else:raise AssertionError('Expected refusal '+code)


def checks(query,manifest):
    f=RouteFixture(query,manifest)
    protected=['trading.trades','trading.instruments','trading.gas_owned_positions','trading.gas_owned_reservations','trading.gas_owned_releases',
        'trading.gas_owned_fees','trading.cash_payment_memberships','settlement_data.cash_payment_revisions','trading.storage_facility_activities','trading.facility_quantity_revisions','trading.facility_gas_allocations']
    def financial_digest():
        return query('SELECT jsonb_object_agg(name,digest) FROM('+' UNION ALL '.join(
            f"SELECT '{table}'::text name,md5(coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]')::text) digest FROM {table} t WHERE t.catalog_id='{f.catalog}'" for table in protected)+')s;')
    original_financial=financial_digest()
    body=f.route_definition()
    operation=str(uuid4())
    outcome=f.route_record(body,operation=operation)
    assert outcome['routeId']==body['routeId'] and outcome['definitionId']==operation and outcome['changed']
    facts=f.route_read(body['routeId'])
    assert facts['definition']['id']==operation and len(facts['nodes'])==6 and len(facts['segments'])==7
    assert facts['plans']==[]
    print('PASS immutable route definition retains six exact same-pipeline nodes and seven native entitlement/path memberships.',flush=True)
    checkpoint(f,body['routeId'],'graph')
    f.source_plans()
    facts=f.route_read(body['routeId'])
    plan=f.route_plan(facts)
    selected=str(uuid4());witness=f.route_witness(facts,plan,selected)
    assert witness['feasible'] and len(witness['capacities'])==4 and len(witness['segments'])==7 and len(witness['nodes'])==4
    managed=checkpoint(f,body['routeId'],'plan',operation=selected,command=plan)
    assert managed['canonicalCommand']==plan
    # Native accepts interval sets independently of their JSON array order.
    # Record the actual managed witness, then require full retained parity.
    witness=managed['witness']
    accepted=f.route_record(plan,witness,operation=selected)
    assert accepted['revisionId']==selected and accepted['routePlanId']==witness['routePlanId']
    retained=f.route_read(body['routeId'])
    assert retained['plans'][0]['assessment']==witness and len(retained['memberships'])==4
    checkpoint(f,body['routeId'],'current',revision=selected)
    print('PASS seven explicit native claims conserve100 receipts,2 fuel,98 through60/38 split/merge and zero idle path; four whole source streams are exclusively adopted.',flush=True)
    original=f.route_read(body['routeId'])
    count=lambda:query(f"SELECT count(*) FROM trading.gas_transport_route_plans WHERE catalog_id='{f.catalog}';")
    before=count()
    assert f.route_record(plan,None,operation=selected,context=f.capture())==accepted
    ack=f.route_record(plan,None)
    assert ack=={**accepted,'changed':False} and count()==before
    altered={**plan,'evidence':'Synthetic different request with same operation'}
    refuses(lambda:f.route_record(altered,witness,operation=selected),'gas_transport_route_conflict')
    status=json.loads(f.call(f"SELECT security.gas_transport_route_status('{f.catalog}','{selected}')::text;"))
    assert status['outcome']==accepted and status['command']==plan and status['authorityUntil']
    print('PASS exact operation replay, actor receipt/status and fresh semantic acknowledgement retain first effect with no head or membership growth.',flush=True)
    second_body=f.route_definition('synthetic_second_route');f.route_record(second_body)
    second=f.route_read(second_body['routeId']);second_plan=f.route_plan(second)
    operation2=str(uuid4());second_witness=f.route_witness(second,second_plan,operation2)
    assert all(p['membershipState']=='occupied_v1' for c in second['sourceCapacities'] for p in c['sourcePlans'])
    refuses(lambda:f.route_record(second_plan,second_witness,operation=operation2),'gas_transport_route_membership')
    own_other=f.route_plan(original,'synthetic_another_own_stream');op=str(uuid4())
    refuses(lambda:f.route_record(own_other,f.route_witness(original,own_other,op),operation=op),'gas_transport_route_membership')
    print('PASS source stream exclusivity spans another route and another logical stream of the same route; no foreign owner identities are disclosed.',flush=True)
    # Witness flags, totals, exact identities and full arrays are independently
    # reconstructed by the native counterpart rather than trusted.
    release=dict(kind='release',routeId=body['routeId'],definitionId=accepted['definitionId'],expectedStateVersion=original['stateVersion'],expectedPreviousId=selected,
        businessKey=plan['businessKey'],admissionPolicy='explicit_route_membership_release_v1',evidence='Synthetic explicit own-stream release')
    released=f.route_record(release)
    checkpoint(f,body['routeId'],'released',revision=released['revisionId'])
    available=f.route_read(second_body['routeId']);adopt=f.route_plan(available)
    operation3=str(uuid4());proof=f.route_witness(available,adopt,operation3)
    for mutate in [lambda w:w.update(feasible=False),lambda w:w['segments'][0].update(claimId=str(uuid4())),
        lambda w:w['segments'][0]['fuelQuantity'].update(n='999'),lambda w:w['nodes'][0]['intervals'][0].update(incoming=[]),
        lambda w:w['capacities'].pop(),lambda w:w['segments'].pop()]:
        bad=deepcopy(proof);mutate(bad)
        refuses(lambda:f.route_record(adopt,bad,operation=operation3),'gas_transport_route_witness')
    accepted2=f.route_record(adopt,proof,operation=operation3)
    assert len(f.route_read(second_body['routeId'])['memberships'])==4
    assert f.route_record(plan,None)=={**accepted,'changed':False}
    assert f.route_read(body['routeId'])['plans'][0]['id']==released['revisionId']
    print('PASS explicit release frees exactly own memberships; full witness mutations refuse; old plan acknowledgement cannot restore ownership.',flush=True)
    old_context=f.capture()
    first=f.route_read(second_body['routeId'])['sourceCapacities'][0]
    f.replace_capacity(first)
    current=f.route_read(second_body['routeId'])
    assert current['plans'][0]['assessment']==proof and current['plans'][0]['sources']!=[]
    assert current['sourceCapacities'][0]['stateVersion']!=first['stateVersion']
    checkpoint(f,second_body['routeId'],'unavailable',revision=accepted2['revisionId'])
    stale={**adopt,'expectedStateVersion':current['stateVersion'],'expectedPreviousId':accepted2['revisionId'],'evidence':'Synthetic stale complete source assertion'}
    refuses(lambda:f.route_record(stale,proof),'gas_transport_route_stale')
    fresh=f.route_plan(current);fresh_op=str(uuid4());fresh_proof=f.route_witness(current,fresh,fresh_op)
    replacement=f.route_record(fresh,fresh_proof,operation=fresh_op)
    assert replacement['revision']==2 and replacement['changed']
    historical=f.route_read(second_body['routeId'],context=old_context)
    assert historical['plans'][0]['id']==accepted2['revisionId'] and historical['sourceCapacities'][0]['stateVersion']==first['stateVersion']
    print('PASS capacity-source correction retains old witness/provenance, invalidates stale assertions and admits only explicit refreshed complete source selection; old knowledge remains exact.',flush=True)
    # Input operands have their own4096-bit envelope, independently of reduced
    # numerical results; a huge equal ratio must not bypass authoring bounds.
    raw=f.route_definition('synthetic_raw_fraction_boundary')
    raw['segments'][0]['fuelFraction']=dict(n=str(2**4096),d=str(2**4096))
    refuses(lambda:f.route_record(raw),'gas_transport_route_input')
    print('PASS fraction input operands enforce4096-bit limits before reduction.',flush=True)
    boundary=f.route_definition('synthetic_maximum_input_fraction')
    boundary['segments'][0]['fuelFraction']=dict(n=str(2**4095),d=str(2**4095))
    assert f.route_record(boundary)['changed']
    for key,change in [('synthetic_sum_fractions',lambda b:b['segments'][0].update(fuelFraction=dict(n='2',d='3'),lossFraction=dict(n='1',d='2'))),
        ('synthetic_duplicate_segment',lambda b:b['segments'].append(deepcopy(b['segments'][0]))),
        ('synthetic_wrong_join',lambda b:b['segments'][0].update(fromNodeId=b['segments'][1]['fromNodeId'],fromNodeKey=b['segments'][1]['fromNodeKey']))]:
        bad=f.route_definition(key);change(bad)
        refuses(lambda:f.route_record(bad),'gas_transport_route_')
    seals(f,refuses,f.route_read(second_body['routeId']))
    authority(f,refuses,body['routeId'],operation,body)
    future_memberships(f,refuses,body['routeId'],second_body['routeId'])
    microseconds(f,refuses)
    concurrency(f)
    assert financial_digest()==original_financial
    assert f.call("SELECT has_table_privilege(current_user,'trading.gas_transport_route_plans','SELECT') OR has_function_privilege(current_user,'trading.verify_gas_transport_capacity_snapshot(uuid,uuid,uuid,integer,text,jsonb)','EXECUTE') OR has_function_privilege('openetrm_risk_worker','security.record_gas_transport_route(uuid,uuid,jsonb,timestamptz,jsonb,uuid)','EXECUTE');")=='f'
    print('PASS route operations leave every protected trade/title/cash/physical/allocation fact unchanged and expose no raw-table/private-verifier or worker producer privileges.',flush=True)


if __name__=='__main__':main(checks,fixture_factory=fixture)
