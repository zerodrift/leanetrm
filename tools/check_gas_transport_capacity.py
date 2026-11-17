"""Qualify direct Transport capacity plans on isolated stock PostgreSQL."""
import json
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from uuid import uuid4

from check_reference_editing import main
from gas_transport_capacity_fixture import TransportCapacityFixture, fixture, literal


def rejected(work, code):
    try:
        work()
    except RuntimeError as error:
        assert code in str(error), str(error)
    else:
        raise AssertionError('Expected ' + code)


def checks(query, manifest):
    f = TransportCapacityFixture(query, manifest)
    independent_tables=['trades','instruments','gas_owned_positions','gas_owned_lots','gas_owned_loans','cash_payment_instruments','cash_payment_memberships','storage_facility_activities','facility_quantity_revisions']
    def effects():
        return query('SELECT jsonb_object_agg(name,rows) FROM('+' UNION ALL '.join(f"SELECT '{table}'::text name,count(*) rows FROM trading.{table} WHERE catalog_id='{f.catalog}'" for table in independent_tables)+')s;')
    initial_effects=effects()
    body = f.definition()
    operation = str(uuid4())
    defined = f.record(body, operation=operation)
    assert defined['changed'] and defined['versionId'] == operation
    facts = f.read(body['entitlementId'])
    assert facts['version']['id'] == operation and facts['version']['entitlement_id'] == body['entitlementId']
    assert facts['version']['book_id'] == f.book and facts['plans'] == [] and facts['claims'] == []
    assert f.record(body, operation=operation) == defined
    assert f.record(body) == {**defined, 'changed': False}
    plan_operation, plan = f.plan(facts, 'synthetic_plan_a', 60)
    rejected(lambda: f.record(plan, dict(implementation='gas_transport_capacity_witness_v1', versionId=operation, admissionPolicy=plan['admissionPolicy'],intervals=[]),
        operation=plan_operation), 'gas_transport_capacity_witness')
    witness = f.hourly_witness(facts, plan['claims'])
    assert len(witness['intervals']) == 10
    for mutation in ('missing', 'rate', 'members', 'bound'):
        wrong = deepcopy(witness)
        if mutation == 'missing': wrong['intervals'].pop()
        elif mutation == 'rate': wrong['intervals'][0]['used']['n'] = '59'
        elif mutation == 'members': wrong['intervals'][0]['claims'] = []
        else: wrong['intervals'][0]['maximum']['n'] = '1000'
        rejected(lambda: f.record(plan, wrong, operation=plan_operation), 'gas_transport_capacity_witness')
    accepted = f.record(plan, witness, operation=plan_operation)
    assert accepted['changed'] and accepted['revisionId'] == plan_operation
    old = f.read(body['entitlementId'])
    assert len(old['plans']) == len(old['claims']) == 1
    assert f.record(plan, witness, operation=plan_operation) == accepted
    operation_b, plan_b = f.plan(old, 'synthetic_plan_b', 50, path_key='synthetic_path_d')
    over = f.hourly_witness(old, [*plan['claims'], *plan_b['claims']])
    # An attacker can change all booleans;110 still exceeds the independently
    # stored parent/delivery100 even though each route separately has room.
    forged_over=deepcopy(over)
    for row in forged_over['intervals']: row['withinLimit'] = True
    rejected(lambda: f.record(plan_b, forged_over, operation=operation_b), 'gas_transport_capacity_witness')
    rejected(lambda: f.record(plan_b, over, operation=operation_b), 'gas_transport_capacity_exceeded')
    operation_b, plan_b = f.plan(old, 'synthetic_plan_b', 40, path_key='synthetic_path_d')
    at_boundary = f.hourly_witness(old, [*plan['claims'], *plan_b['claims']])
    assert at_boundary['intervals'][0]['used']['n'] == '100'
    f.record(plan_b, at_boundary, operation=operation_b)
    current = f.read(body['entitlementId'])
    assert len(current['plans']) == len(current['claims']) == 2
    # Old facts acknowledge their authentic outcome; neither an obsolete state
    # version nor an empty source can rewind the now complete shared scope.
    prior = f.call(f"SELECT security.gas_transport_capacity_prior_fact('{f.catalog}',{literal(plan)},'{f.effective}');")
    assert prior == 't'
    ack_op = str(uuid4())
    from gas_transport_capacity_fixture import identity
    ack = deepcopy(plan)
    ack['claims'][0]['claimId'] = identity('gas_transport_claim_v1', ack_op)
    before = f.call(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{f.catalog}';")
    recovered = json.loads(f.call(f"SELECT security.record_gas_transport_capacity('{f.catalog}','{ack_op}',{literal(ack)},'{f.effective}',NULL,NULL)::text;"))
    assert recovered == {**accepted, 'changed': False}
    assert f.read(body['entitlementId'])['stateVersion'] == current['stateVersion']
    assert f.call(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{f.catalog}';") == before
    release_op, release = f.plan(current, 'synthetic_plan_b', 0, empty=True)
    f.record(release, f.hourly_witness(current, plan['claims']), operation=release_op)
    released = f.read(body['entitlementId'])
    assert len(released['plans']) == 2 and len(released['claims']) == 1 and released['claims'][0]['plan_id'] == plan_operation
    print('PASS native exact recovery/D238; independent complete10-interval witness, forged proof refusal, shared110>100 refusal, exact100 admission and plan-only release', flush=True)
    # Two independently reviewed plans each see40 spare; only one may use it.
    context = f.capture()
    proposals = [f.plan(released, key, 40, path_key='synthetic_path_d') for key in ['synthetic_race_b','synthetic_race_c']]
    def race(proposal):
        op, chosen = proposal
        try:
            return f.record(chosen, f.hourly_witness(released,[*plan['claims'],*chosen['claims']]), operation=op, context=context)
        except RuntimeError as error:
            assert 'gas_transport_capacity_stale' in str(error), str(error)
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(race, proposals))
    assert sum(result is not None for result in outcomes) == 1
    raced = f.read(body['entitlementId'])
    assert sum(int(c['receipt_quantity']) for c in raced['claims']) == 100
    winner = next(p for p in raced['plans'] if p['business_key'].startswith('synthetic_race_'))
    op, chosen = f.plan(raced, winner['business_key'], 0, empty=True)
    f.record(chosen,f.hourly_witness(raced,plan['claims']),operation=op)
    before_downgrade = f.read(body['entitlementId'])
    old_context = f.capture()
    reduced = deepcopy(body)
    reduced.update(expectedStateVersion=before_downgrade['stateVersion'],expectedPreviousId=operation,evidence='Synthetic reduced agreement capacity')
    reduced['agreement'][0]['maximumPerHour'] = '50'
    reduced_operation = str(uuid4())
    f.record(reduced,operation=reduced_operation)
    reduced_facts = f.read(body['entitlementId'])
    assert reduced_facts['claims'] == before_downgrade['claims']
    assert f.read(body['entitlementId'],context=old_context)['version']['id'] == operation
    assert json.loads(query(f"SELECT assessment::text FROM trading.gas_transport_capacity_plans WHERE(catalog_id,id)=('{f.catalog}','{plan_operation}');")) == witness
    op, chosen = f.plan(reduced_facts,'synthetic_plan_a',50)
    f.record(chosen,f.hourly_witness(reduced_facts,chosen['claims'],agreement=50),operation=op)
    assert len(f.read(body['entitlementId'])['claims']) == 1
    print('PASS shared-capacity concurrent admission accepts exactly one40; coherent limit downgrade retains old claims/witness and old cut, then explicit replacement resolves60→50.',flush=True)
    # Consume actual managed normalization. The three notations change ordinal
    # ordering if native hashing silently uses a different instant format.
    micro = f.definition('synthetic_microsecond_capacity')
    micro_operation = str(uuid4())
    normalized = f.normalize([dict(operationId=micro_operation,command=micro)])[0]['command']
    f.record(normalized, operation=micro_operation)
    micro_facts = f.read(micro['entitlementId'])
    operation, command = f.plan(micro_facts, 'synthetic_microsecond_claims', 0)
    base = command['claims'][0]
    command['claims'] = [{k:v for k,v in {**base,'receiptFrom':start,'deliveryFrom':start}.items() if k!='claimId'}
        for start in ['2020-12-20T00:00:00Z','2020-12-20T00:00:00.120000Z','2020-12-20T00:00:00.000001Z']]
    normalized = f.normalize([dict(operationId=operation,command=command)])[0]['command']
    rejected(lambda: f.record(normalized, operation=operation), 'gas_transport_capacity_witness')
    print('PASS actual managed canonical claim IDs at whole seconds,120000µs and1µs are admitted through native ID verification before missing-witness refusal.', flush=True)
    from gas_transport_capacity_seals import seals
    seals(f,rejected)
    # A future-effective head remains a current dependency even after refresh.
    current = f.read(body['entitlementId'])
    prior_future = f.capture()
    f.at = '2020-12-20T23:59:59.999999Z'
    op, future_plan = f.plan(current,'synthetic_future_plan',0,empty=True)
    current_claim = next(c for c in current['claims'] if c['plan_id'] == next(p['id'] for p in current['plans'] if p['business_key']=='synthetic_plan_a'))
    retained_claim = dict(claimId=current_claim['id'],pathId=current_claim['path_id'],receiptQuantity=current_claim['receipt_quantity'])
    f.record(future_plan,f.hourly_witness(current,[retained_claim],agreement=50),operation=op,effective='2020-12-20T00:00:00Z')
    f.at = '2020-12-19T23:59:59.999999Z'
    future_facts = f.read(body['entitlementId'])
    assert future_facts['hasFutureHeads'] and future_facts['stateVersion'] > current['stateVersion']
    op, hidden = f.plan(future_facts,'synthetic_hidden_spare',0,empty=True)
    rejected(lambda:f.record(hidden,f.hourly_witness(future_facts,[retained_claim],agreement=50),operation=op),'gas_transport_capacity_future_source')
    unavailable_definition = deepcopy(reduced)
    unavailable_definition.update(expectedStateVersion=future_facts['stateVersion'],expectedPreviousId=reduced_operation,evidence='Synthetic future-head guarded definition')
    unavailable_definition['agreement'][0]['maximumPerHour']='55'
    rejected(lambda:f.record(unavailable_definition),'gas_transport_capacity_future_source')
    assert f.record(body) == {**defined,'changed':False}
    assert f.read(body['entitlementId'],context=prior_future)['hasFutureHeads'] is False
    print('PASS latest-known future plan freezes every new capacity/plan change after refresh; authentic prior facts still acknowledge without head rewind.',flush=True)
    f.at='2020-12-20T23:59:59.999999Z'
    activated_definition={**unavailable_definition,'expectedStateVersion':future_facts['stateVersion'],'evidence':'Synthetic later business-date capacity revision'}
    activated_operation=str(uuid4())
    f.record(activated_definition,operation=activated_operation,effective='2020-12-20T00:00:00Z')
    f.at='2020-12-19T23:59:59.999999Z'
    unavailable=f.read(body['entitlementId'])
    assert unavailable['version'] is None and unavailable['hasFutureHeads']
    assert f.read(body['entitlementId'],context=prior_future)['version']['id']==reduced_operation
    f.at='2020-12-20T23:59:59.999999Z'
    caught_up=f.read(body['entitlementId'])
    assert caught_up['version']['id']==activated_operation and not caught_up['hasFutureHeads']
    op,catchup=f.plan(caught_up,'synthetic_business_catchup',0,empty=True)
    proof=f.hourly_witness(caught_up,[retained_claim],agreement=55)
    rejected(lambda:f.record(catchup,proof,operation=op),'gas_transport_capacity_context')
    f.record(catchup,proof,operation=op,effective='2020-12-20T16:00:00Z')
    f.at='2020-12-19T23:59:59.999999Z'
    print('PASS future definition does not resurrect an obsolete version; matching business-date catchup restores admission and mismatched effective/source dates refuse.',flush=True)
    canonical = f.definition('synthetic_ascii_evidence')
    canonical['evidence'] = ' Synthetic evidence\nretains its second line '
    canonical_operation = str(uuid4())
    expected = f.normalize([dict(operationId=canonical_operation,command=canonical)])[0]['command']
    f.record(canonical,operation=canonical_operation)
    status = json.loads(f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{canonical_operation}')::text;"))
    assert status['command'] == expected, 'Native evidence notation diverged from the actual managed command.'
    for bad in [None,42]:
        malformed = f.definition('synthetic_typed_key')
        malformed['businessKey'] = bad
        rejected(lambda:f.record(malformed),'gas_transport_capacity_input')
    from gas_transport_capacity_release import release
    release(f)
    from gas_transport_capacity_numerics import cancelling_rates
    cancelling_rates(f)
    from gas_transport_capacity_authority import authority,directory_scope
    authority(f,rejected)
    directory_scope(f,rejected)
    assert effects()==initial_effects,'Capacity operations created or changed title, cash, trades or physical activity.'
    assert query("SELECT has_table_privilege('openetrm_runtime','trading.gas_transport_capacity_plans','SELECT') OR has_function_privilege('openetrm_risk_worker','security.record_gas_transport_capacity(uuid,uuid,jsonb,timestamptz,jsonb,uuid)','EXECUTE');")=='f'
    print('PASS complete capacity qualifier has no financial/physical side effects and grants no raw-table or worker producer access.',flush=True)


if __name__ == '__main__':
    main(checks, fixture_factory=fixture)
