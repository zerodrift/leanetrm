"""An explicit empty release removes only one plan, even during a retained breach."""
from copy import deepcopy
from uuid import uuid4
from gas_park_loan_fixture import rejected


def release(f):
    definition=f.definition('synthetic_breach_cleanup')
    definition['agreement'][0]['maximumPerHour']='120'
    definition['deliveries'][0]['limits'][0]['maximumPerHour']='120'
    version=str(uuid4());f.record(definition,operation=version)
    facts=f.read(definition['entitlementId'])
    operation_a,a=f.plan(facts,'synthetic_cleanup_a',60)
    f.record(a,f.hourly_witness(facts,a['claims'],agreement=120,delivery=120),operation=operation_a)
    facts=f.read(definition['entitlementId'])
    operation_b,b=f.plan(facts,'synthetic_cleanup_b',60,path_key='synthetic_path_d')
    f.record(b,f.hourly_witness(facts,[*a['claims'],*b['claims']],agreement=120,delivery=120),operation=operation_b)
    facts=f.read(definition['entitlementId'])
    reduced=deepcopy(definition)
    reduced.update(expectedStateVersion=facts['stateVersion'],expectedPreviousId=version,evidence='Synthetic120 to50 parent capacity change')
    reduced['agreement'][0]['maximumPerHour']='50'
    f.record(reduced)
    facts=f.read(definition['entitlementId'])
    blocked_operation,blocked=f.plan(facts,'synthetic_cleanup_a',0,empty=True)
    rejected(lambda:f.record(blocked,f.hourly_witness(facts,b['claims'],agreement=50,delivery=120),operation=blocked_operation),'gas_transport_capacity_exceeded')
    bad_operation,bad=f.plan(facts,'synthetic_cleanup_a',1,admission_policy='explicit_empty_release_v1')
    rejected(lambda:f.record(bad,operation=bad_operation),'gas_transport_capacity_input')
    bad_operation,bad=f.plan(facts,'synthetic_unaccepted_release',0,empty=True,admission_policy='explicit_empty_release_v1')
    rejected(lambda:f.record(bad,operation=bad_operation),'gas_transport_capacity_input')
    operation,empty=f.plan(facts,'synthetic_cleanup_a',0,empty=True,admission_policy='explicit_empty_release_v1')
    witness=f.hourly_witness(facts,b['claims'],agreement=50,delivery=120,admission_policy='explicit_empty_release_v1')
    rejected(lambda:f.record(empty,{**witness,'admissionPolicy':'complete_feasible_replacement_v1'},operation=operation),'gas_transport_capacity_witness')
    # The selected plan releases all of its claims. Remaining B60/parent50 is a
    # retained breach, not a reason to fabricate or keep A's removed capacity.
    f.record(empty,witness,operation=operation)
    remaining=f.read(definition['entitlementId'])
    assert len(remaining['claims'])==1 and remaining['claims'][0]['plan_id']==operation_b
    assert remaining['claims'][0]['receipt_quantity']=='60'
    print('PASS explicit empty release during120→50 breach removes only A; independent B60 and the residual breach remain visible.',flush=True)
