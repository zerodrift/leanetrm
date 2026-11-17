"""Future membership blocks adoption while an independent own release remains usable."""
from uuid import uuid4
from gas_transport_route_managed import checkpoint


def future_memberships(f,refuses,route,other):
    def release(facts,key,evidence):
        previous=next(p for p in facts['plans'] if p['business_key']==key)
        return dict(kind='release',routeId=route,definitionId=facts['definition']['id'],expectedStateVersion=facts['stateVersion'],expectedPreviousId=previous['id'],businessKey=key,
            admissionPolicy='explicit_route_membership_release_v1',evidence=evidence)
    # Release the other route, then create a genuinely distinct logical native
    # source stream. Both streams independently consume the shared200 limits.
    other_facts=f.route_read(other);old=other_facts['plans'][0]
    f.route_record(dict(kind='release',routeId=other,definitionId=other_facts['definition']['id'],expectedStateVersion=other_facts['stateVersion'],expectedPreviousId=old['id'],businessKey=old['business_key'],
        admissionPolicy='explicit_route_membership_release_v1',evidence='Synthetic release before independent streams'))
    for source in f.route_read(route)['sourceCapacities']:f.add_capacity_stream(source,'synthetic_second_capacity_stream')
    facts=f.route_read(route);current=f.route_plan(facts,'synthetic_current_route_b',source_key='synthetic_second_capacity_stream');op=str(uuid4())
    current_result=f.route_record(current,f.route_witness(facts,current,op),operation=op)
    prior_at,prior_effective=f.at,f.effective
    f.at,f.effective='2020-12-20T23:59:59.999999Z','2020-12-20T16:00:00Z'
    try:
        facts=f.route_read(route);future=f.route_plan(facts,'synthetic_future_route_a',source_key='synthetic_complete_capacity_source');future_op=str(uuid4())
        f.route_record(future,f.route_witness(facts,future,future_op),operation=future_op)
    finally:f.at,f.effective=prior_at,prior_effective
    facts=f.route_read(route)
    assert facts['hasFutureHeads']
    descriptors={p['firstPlanRevisionId']:p for c in facts['sourceCapacities'] for p in c['sourcePlans']}
    own=[m for m in facts['memberships'] if m['route_plan_id']==future_op]
    assert len(own)==4 and all(descriptors[m['first_plan_revision_id']]['membershipState']=='occupied_v1' for m in own)
    checkpoint(f,route,'graph')
    # An ordinary replacement is conservatively frozen by future heads; this
    # does not turn an independent own release into a new flow assertion.
    changed=f.route_plan(facts,'synthetic_current_route_b',source_key='synthetic_second_capacity_stream');changed['evidence']='Synthetic new assertion during future head'
    op=str(uuid4())
    refuses(lambda:f.route_record(changed,f.route_witness(facts,changed,op),operation=op),'gas_transport_route_future_source')
    released=f.route_record(release(facts,'synthetic_current_route_b','Synthetic current B release despite future A'))
    assert released['revision']==2 and released['routePlanId']==current_result['routePlanId']
    checkpoint(f,route,'released',revision=released['revisionId'])
    after=f.route_read(route)
    assert len(after['memberships'])==4 and all(m['route_plan_id']==future_op for m in after['memberships'])
    for source in after['sourceCapacities']:
        assert sum(p['membershipState']=='available_v1' for p in source['sourcePlans'])==1
        assert sum(p['membershipState']=='occupied_v1' for p in source['sourcePlans'])==1
    print('PASS known future own A is truthfully occupied with retained own membership, freezes new adoption, and does not block current independent B release or free A.',flush=True)
