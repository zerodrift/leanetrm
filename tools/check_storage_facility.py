"""Exercise physical storage operations on an owned stock PostgreSQL cluster."""
import json
import os
from copy import deepcopy
from datetime import datetime, timedelta
from uuid import uuid4

from check_booked_side_programs import runtime_query
from check_reference_editing import main
from storage_facility_fixture import StorageFacilityFixture, literal


def rejected(work, code):
    try:
        work()
    except RuntimeError as error:
        assert code in str(error), str(error)
    else:
        raise AssertionError('Expected ' + code)


def checks(query, manifest):
    for page in ('facility_definitions','facility_activity'):
        assert query(f"SELECT assistant.is_prompt_page('{page}');") == 't', 'Facility page identity must use the closed native prompt catalog: ' + page
    assert query("SELECT assistant.is_prompt_page('synthetic_uninstalled_facility_page');") == 'f'
    fixture = StorageFacilityFixture(query, manifest)
    catalog, point, at = fixture.catalog, fixture.point, fixture.at
    facility, port = str(uuid4()), str(uuid4())
    before_definition = fixture.capture()

    call = lambda sql, context=None: fixture.call(sql, context=context)
    record = fixture.record

    definition = dict(kind='define', facilityId=facility, expectedStateVersion=0, expectedPreviousId=None,
        referenceCatalogId=point['catalog_id'], referenceRevisionId=point['revision_id'], facilityCode=point['point_code'],
        commodity=point['product_code'], quantity=point['quantity'], clock=point['clock_code'], minimum='0', maximum='1000',
        implementation='storage_total_physical_step_ratchets_v1', inventoryBasis='total_physical_inventory_v1',
        rateBasis='native_quantity_per_elapsed_hour_v1', simultaneousFlow='separate_injection_withdrawal_v1', lossTreatment='no_loss_v1',
        ports=[dict(portId=port, pipeline=point['pipeline_code'], zone=point['zone_code'], point=point['point_code'],
            product=point['product_code'], direction='inject', contractDirection=1)],
        ratchets=[dict(lower='0', upper='500', injectionPerHour='100', withdrawalPerHour='100'),
            dict(lower='500', upper='1000', injectionPerHour='25', withdrawalPerHour='100')], evidence='Synthetic storage operating definition')
    operation = str(uuid4())
    other_clock = query(f"SELECT code FROM ref_data.clock_rules WHERE(catalog_id,revision_id)=('{point['catalog_id']}','{point['revision_id']}') AND code<>'{point['clock_code']}' LIMIT 1;")
    assert other_clock
    rejected(lambda: record({**definition, 'clock': other_clock}), 'storage_facility_clock')
    defined = record(definition, operation=operation, effective='2021-01-15T00:00:00Z')
    assert record(definition, operation=operation, effective='2021-01-15T00:00:00Z') == defined
    def zoned_retry(request):
        selected=fixture.capture(day='2021-01-15')
        return json.loads(runtime_query(query, manifest, "SET LOCAL TimeZone='Etc/GMT-1';"
            f"SELECT security.record_storage_facility('{catalog}','{request}',{literal(definition)},'2021-01-15T00:00:00Z',NULL,'{selected}')::text;"))
    assert zoned_retry(operation) == defined
    original_publication = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    assert zoned_retry(str(uuid4())) == {**defined, 'changed': False}
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == original_publication
    read = lambda context=None: fixture.read(facility, context)
    facts = read()
    from native_read_transport import captured_input
    assert datetime.fromisoformat(facts['knownAt']) == datetime.fromisoformat(captured_input(query,facts['contextId'])['known_at'])
    assert facts['version']['id'] == defined['versionId'] and len(facts['ports']) == 1
    assert facts['opening'] is None and facts['activities'] == [] and facts['coverage'] is None
    assert query("SELECT count(*) FROM trading.gas_owned_positions;") == '0'
    opening_body = dict(kind='opening', facilityId=facility, expectedStateVersion=facts['stateVersion'],
        businessKey='synthetic_opening', expectedPreviousId=None, observedAt='2021-01-15T00:00:00Z', quantity='400',
        evidence='Synthetic dated physical level')
    opening = record(opening_body)
    assert read()['opening']['id'] == opening['revisionId']
    plan = dict(kind='plan', facilityId=facility, expectedStateVersion=read()['stateVersion'], expectedPreviousId=None,
        planId=str(uuid4()), versionId=defined['versionId'], openingRevisionId=opening['revisionId'],
        coverageStatement='chosen_plan_idle_between_activities_v1', **{'from': '2021-01-15T00:00:00Z'},
        through='2021-01-15T02:00:00Z', activities=[dict(revisionId=str(uuid4()), portId=port, direction='inject',
            **{'from': '2021-01-15T00:00:00Z'}, through='2021-01-15T02:00:00Z', quantity='200')], evidence='Synthetic crossed-band plan')
    # End inventory600 is below capacity1000, but100/hour crosses a25/hour band.
    # A raw caller cannot replace the required native proof with a success flag.
    rejected(lambda: record(plan, dict(implementation='storage_assessment_witness_v1', constraint='within_limit', intervals=[])),
        'storage_facility_witness')

    def ratio(number, denominator=1):
        return dict(n=str(number), d=str(denominator))

    def witness(command, closing, intervals):
        return dict(implementation='storage_assessment_witness_v1', rulesVersion=command['versionId'],
            openingRevision=command['openingRevisionId'], coverageRevision=command['planId'],
            **{'from': command['from']}, through=command['through'], constraint='within_limit', closing=ratio(closing), intervals=intervals)

    def segment(begin, end, activities, level, closing, injection, bands):
        return dict(**{'from': begin}, through=end, activities=activities, opening=ratio(level), closing=ratio(closing),
            injectionPerHour=ratio(injection), withdrawalPerHour=ratio(0), bands=bands)

    # A currently selected rule which began inside the window cannot be applied
    # to its earlier physical hours, even though it is known at admission time.
    other = fixture.points[1]
    later_facility, later_port = str(uuid4()), str(uuid4())
    later_definition = {**definition, 'facilityId': later_facility, 'facilityCode': other['point_code'],
        'ports': [dict(portId=later_port, pipeline=other['pipeline_code'], zone=other['zone_code'], point=other['point_code'],
            product=other['product_code'], direction='inject', contractDirection=1)]}
    later_version = record(later_definition, effective='2021-01-15T01:00:00Z')
    later_opening = record({**opening_body, 'facilityId': later_facility,
        'expectedStateVersion': fixture.read(later_facility)['stateVersion']})
    earlier_plan = {**plan, 'facilityId': later_facility, 'expectedStateVersion': fixture.read(later_facility)['stateVersion'],
        'versionId': later_version['versionId'], 'openingRevisionId': later_opening['revisionId'], 'activities': []}
    rejected(lambda: record(earlier_plan, witness(earlier_plan, 400, [segment(earlier_plan['from'], earlier_plan['through'], [], 400, 400, 0, [0])])),
        'storage_facility_rule_transition')
    fraction_opening = record({**opening_body, 'facilityId': later_facility, 'businessKey': 'synthetic_fractional_opening',
        'observedAt': '2021-01-15T01:00:00Z', 'expectedStateVersion': fixture.read(later_facility)['stateVersion']})
    first, second = str(uuid4()), str(uuid4())
    rational_plan = {**earlier_plan, 'planId': str(uuid4()), 'expectedStateVersion': fixture.read(later_facility)['stateVersion'],
        'openingRevisionId': fraction_opening['revisionId'], 'from': '2021-01-15T01:00:00Z', 'through': '2021-01-15T05:00:00Z',
        'activities': [dict(revisionId=first, portId=later_port, direction='inject', quantity='1',
            **{'from': '2021-01-15T01:00:00Z'}, through='2021-01-15T04:00:00Z'),
            dict(revisionId=second, portId=later_port, direction='inject', quantity='1',
                **{'from': '2021-01-15T02:00:00Z'}, through='2021-01-15T05:00:00Z')]}
    parts = [dict(**{'from': '2021-01-15T01:00:00Z'}, through='2021-01-15T02:00:00Z', activities=[first], opening=ratio(400),
        closing=ratio(1201, 3), injectionPerHour=ratio(1, 3), withdrawalPerHour=ratio(0), bands=[0]),
        dict(**{'from': '2021-01-15T02:00:00Z'}, through='2021-01-15T04:00:00Z', activities=sorted([first, second]), opening=ratio(1201, 3),
            closing=ratio(1205, 3), injectionPerHour=ratio(2, 3), withdrawalPerHour=ratio(0), bands=[0]),
        dict(**{'from': '2021-01-15T04:00:00Z'}, through='2021-01-15T05:00:00Z', activities=[second], opening=ratio(1205, 3),
            closing=ratio(402), injectionPerHour=ratio(1, 3), withdrawalPerHour=ratio(0), bands=[0])]
    rational_proof = witness(rational_plan, 402, parts)
    omitted = deepcopy(rational_proof); omitted['intervals'].pop(1)
    rejected(lambda: record(rational_plan, omitted), 'storage_facility_witness')
    omitted = deepcopy(rational_proof); omitted['intervals'][1]['injectionPerHour'] = ratio(1, 3)
    rejected(lambda: record(rational_plan, omitted), 'storage_facility_witness')
    rational = record(rational_plan, rational_proof)
    assert fixture.read(later_facility)['planAssessment']['closing'] == ratio(402)
    assert fixture.read(later_facility)['plan']['id'] == rational['revisionId']
    print('PASS native exact rational overlap witness:1/3,2/3,1/3 hourly rates and400→1201/3→1205/3→402 levels, no omitted event segments', flush=True)

    forged = witness(plan, 600, [segment(plan['from'], plan['through'], [plan['activities'][0]['revisionId']], 400, 600, 100, [0])])
    rejected(lambda: record(plan, forged), 'storage_facility_witness')
    assert read()['plan'] is None
    feasible = deepcopy(plan)
    feasible['activities'][0].update(quantity='100', through='2021-01-15T01:00:00Z')
    proof = witness(feasible, 500, [segment(feasible['from'], '2021-01-15T01:00:00Z', [feasible['activities'][0]['revisionId']], 400, 500, 100, [0]),
        segment('2021-01-15T01:00:00Z', feasible['through'], [], 500, 500, 0, [1])])
    absent = deepcopy(proof); absent['intervals'][0]['activities'] = []
    rejected(lambda: record(feasible, absent), 'storage_facility_witness')
    absent = deepcopy(proof); absent['intervals'][0]['bands'] = []
    rejected(lambda: record(feasible, absent), 'storage_facility_witness')
    plan_operation = str(uuid4())
    accepted = record(feasible, proof, plan_operation)
    assert read()['plan']['id'] == accepted['revisionId']
    assert read()['planAssessment']['closing'] == ratio(500)
    # Receipt recovery precedes all ephemeral source resolution.
    recovered = json.loads(runtime_query(query, manifest, f"SELECT security.record_storage_facility('{catalog}','{plan_operation}',"
        f"{literal(feasible)},'{at}',NULL,NULL)::text;"))
    assert recovered == accepted
    publication = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    assert record({**feasible, 'expectedStateVersion': read()['stateVersion'], 'expectedPreviousId': accepted['revisionId']}, proof)['changed'] is False
    assert publication == query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    print('PASS native plan proof: endpoint arrival, exact active/band coverage, crossed-band refusal and receipts-first recovery', flush=True)

    current = read()
    rejected(lambda: record({**definition, 'expectedStateVersion': current['stateVersion'], 'expectedPreviousId': defined['versionId'],
        'clock': other_clock, 'ports': [{**definition['ports'][0], 'portId': str(uuid4())}], 'evidence': 'Synthetic forbidden basis change'}),
        'storage_facility_scope_change')
    next_definition = {**definition, 'expectedStateVersion': current['stateVersion'], 'expectedPreviousId': defined['versionId'],
        'ports': [{**definition['ports'][0], 'portId': str(uuid4())}], 'evidence': 'Synthetic future effective ratchet version'}
    future = record(next_definition, effective='2021-01-16T01:00:00Z')
    assert read()['version']['id'] == defined['versionId'] and len(read()['versionBundles']) == 1
    extended = {**feasible, 'expectedStateVersion': read()['stateVersion'], 'expectedPreviousId': accepted['revisionId'],
        'planId': str(uuid4()), 'through': '2021-01-16T02:00:00Z', 'activities': [], 'evidence': 'Synthetic known future rule crossing'}
    idle = witness(extended, 400, [segment(extended['from'], extended['through'], [], 400, 400, 0, [0])])
    rejected(lambda: record(extended, idle), 'storage_facility_rule_transition')
    print('PASS source-known future operating rules cannot disappear from a plan horizon', flush=True)

    actual_body = dict(kind='activity', facilityId=facility, expectedStateVersion=read()['stateVersion'],
        businessKey='synthetic_injection', expectedPreviousId=None, versionId=defined['versionId'], portId=port,
        **{'from': plan['from']}, through=plan['through'], direction='inject', quantity='200', sourceRevisionId=None,
        evidence='Synthetic measured injection crossing the rate band')
    measured = record(actual_body)
    assert read()['activities'][0]['quantity'] == '200', 'A measured operating breach was clamped or refused.'
    coverage_body = dict(kind='coverage', facilityId=facility, expectedStateVersion=read()['stateVersion'],
        businessKey='synthetic_complete_window', expectedPreviousId=None, versionId=defined['versionId'], openingRevisionId=opening['revisionId'],
        **{'from': plan['from']}, through=plan['through'], statement='complete_observed_window_v1',
        activityRevisionIds=[measured['revisionId']], evidence='Synthetic complete physical activity statement')
    for statement in ['complete_observed_window_v1', 'incomplete_observed_window_v1']:
        rejected(lambda: record({**coverage_body, 'businessKey': 'synthetic_future_coverage',
            'through': '2021-01-15T18:00:00Z', 'statement': statement}), 'storage_facility_observation_time')
    # This reference-only bundle gives the physical consumer an explicit future
    # business date. It installs no financial curve, mark or pricing fallback.
    clock_context = fixture.capture()
    known_at = datetime.fromisoformat(captured_input(query,clock_context)['known_at'])
    future_at = known_at + timedelta(days=1)
    future_text = future_at.isoformat().replace('+00:00', 'Z')
    future_day, future_end = future_at.date(), future_at.date() + timedelta(days=1)
    future_reference = str(uuid4())
    query(f"BEGIN;INSERT INTO market_data.revisions VALUES('{catalog}','{future_reference}','{point['catalog_id']}','{point['revision_id']}','draft',0);"
        f"INSERT INTO market_data.revision_periods VALUES('{catalog}','{future_reference}','{future_day}','{future_end}');"
        f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{future_reference}');COMMIT;")
    future_context = fixture.capture(day=str(future_day))
    rejected(lambda: record({**opening_body, 'expectedStateVersion': read()['stateVersion'], 'businessKey': 'synthetic_unknown_future_level',
        'observedAt': future_text}, effective=future_text, context=future_context), 'storage_facility_observation_time')
    rejected(lambda: record({**actual_body, 'expectedStateVersion': read()['stateVersion'], 'businessKey': 'synthetic_unknown_future_flow',
        'from': (future_at - timedelta(hours=1)).isoformat().replace('+00:00', 'Z'), 'through': future_text}, effective=future_text, context=future_context),
        'storage_facility_observation_time')
    rejected(lambda: record({**coverage_body, 'businessKey': 'synthetic_unknown_future_window', 'through': future_text},
        effective=future_text, context=future_context), 'storage_facility_observation_time')
    prospective = {**rational_plan, 'planId': str(uuid4()), 'expectedStateVersion': fixture.read(later_facility)['stateVersion'],
        'expectedPreviousId': rational['revisionId'], 'activities': [], 'through': future_text}
    accepted_prospective = record(prospective, witness(prospective, 400, [segment(prospective['from'], future_text, [], 400, 400, 0, [0])]))
    assert fixture.read(later_facility)['plan']['id'] == accepted_prospective['revisionId']
    coverage = record(coverage_body)
    assert read()['coverage']['activityRevisionIds'] == [measured['revisionId']]
    historical = runtime_query(query, manifest, f"SELECT security.resolve_operation_context('{catalog}','2021-01-15',NULL);")
    amended = record({**actual_body, 'expectedStateVersion': read()['stateVersion'], 'expectedPreviousId': measured['revisionId'],
        'quantity': '50', 'evidence': 'Synthetic corrected measured injection'})
    assert read()['activities'][0]['quantity'] == '50'
    assert read(historical)['activities'][0]['quantity'] == '200'
    assert read()['coverage']['activityRevisionIds'] == [measured['revisionId']], 'A correction rewrote accepted completeness pins.'
    assert read()['requiresReassessment'] and read()['planAssessment']['closing'] == ratio(500)
    wrong = {**coverage_body, 'expectedStateVersion': read()['stateVersion'], 'expectedPreviousId': coverage['revisionId'],
        'evidence': 'Synthetic stale completeness attempt'}
    rejected(lambda: record(wrong), 'storage_facility_coverage')
    record({**wrong, 'activityRevisionIds': [amended['revisionId']], 'evidence': 'Synthetic explicitly reconciled window'})
    corrected = record({**opening_body, 'expectedStateVersion': read()['stateVersion'], 'expectedPreviousId': opening['revisionId'],
        'quantity': '-5', 'evidence': 'Synthetic observed below-minimum level'})
    assert read()['opening']['quantity'] == '-5'
    assert record(opening_body)['revisionId'] == opening['revisionId'] and read()['opening']['id'] == corrected['revisionId']
    assert query("SELECT count(*) FROM trading.gas_owned_positions;") == '0'
    print('PASS measured breaches, explicit completeness, corrections/history, negative physical levels and no old-head reinstatement', flush=True)
    from storage_facility_authority import checks as authority
    authority(fixture, facility, plan_operation, feasible, accepted, historical, before_definition, rejected)
    if os.environ.get('DOTNET'):
        from storage_facility_sources import checks as sources
        sources(fixture, facility, defined['versionId'], port, rejected)
    else:
        print('Native-only qualification; set DOTNET to include ordinary managed booking and shared-source allocation integration.', flush=True)


if __name__ == '__main__':
    main(checks=checks)
