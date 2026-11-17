"""A physical activity and contract allocation share one exact reported flow."""
import json
from uuid import uuid4

from check_booked_side_programs import runtime_query
from storage_facility_fixture import literal


def checks(fixture, facility, version, port, rejected):
    initial_at = fixture.at
    before_link = fixture.capture()
    source = fixture.source()
    catalog, query = fixture.catalog, fixture.query
    choices = json.loads(fixture.call(f"SELECT security.storage_facility_flow_choices('{catalog}','{facility}')::text;"))
    assert any(c['sourceRevisionId'] == source['sourceRevisionId'] for c in choices)
    def search(text, context=None):
        return json.loads(fixture.call(f"SELECT security.storage_facility_flow_choices('{catalog}','{facility}',({literal(text)}#>>'{{}}'))::text;", context=context))
    assert len(search(source['day'] + ' ' + source['point'])) == 1
    assert search(source['sourceRevisionId'])[0]['ledgerId'] == source['ledgerId']
    assert search('synthetic_no_matching_reported_source') == []
    assert search('', before_link) == []
    rejected(lambda: search('x' * 201), 'storage_facility_input')
    role = query("SELECT id FROM security.roles WHERE code='trader';")
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadFacilityQuantities');")
    try:
        rejected(lambda: search(source['day']), 'facility_quantity_denied')
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadFacilityQuantities');")
    reference = fixture.point['catalog_id']
    access = query(f"SELECT access_mode FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)="
        f"('{reference}','{fixture.manifest['tenantId']}','{fixture.manifest['workspaceId']}');")
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)="
        f"('{reference}','{fixture.manifest['tenantId']}','{fixture.manifest['workspaceId']}');")
    try:
        for text in ['synthetic_no_matching_reported_source', '', source['day'] + ' ' + source['point']]:
            rejected(lambda: search(text), 'storage_facility_denied')
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{fixture.manifest['tenantId']}','{fixture.manifest['workspaceId']}','{access}');")
    unavailable = fixture.capture(day='2022-01-01')
    rejected(lambda: runtime_query(query, fixture.manifest,
        f"SELECT security.use_market_context('{catalog}','{unavailable}');SELECT security.use_trading_context('{catalog}','2022-01-01T16:00:00Z');"
        f"SELECT security.storage_facility_flow_choices('{catalog}','{facility}','synthetic_no_matching_reported_source');"), 'storage_facility_denied')
    body = dict(kind='activity', facilityId=facility, expectedStateVersion=fixture.read(facility)['stateVersion'],
        businessKey='synthetic_linked_flow', expectedPreviousId=None, versionId=version, portId=port,
        **{'from': source['from']}, through=source['through'], direction='inject', quantity=None,
        sourceRevisionId=source['sourceRevisionId'], evidence='Synthetic one-source physical activity')
    accepted = fixture.record(body)
    linked = lambda: next(a for a in fixture.read(facility)['activities'] if a['business_key'] == body['businessKey'])
    assert linked()['quantity'] == '100' and linked()['source_revision_id'] == source['sourceRevisionId']
    assert all(a['business_key'] != body['businessKey'] for a in fixture.read(facility, before_link)['activities']), \
        'A historical facility read consulted a later linked reported source.'
    assert query(f"SELECT quantity IS NULL FROM trading.storage_facility_activities WHERE(catalog_id,id)=('{catalog}','{accepted['revisionId']}');") == 't'
    rejected(lambda: fixture.record({**body, 'expectedStateVersion': fixture.read(facility)['stateVersion'],
        'businessKey': 'synthetic_duplicate_flow'}), 'storage_facility_flow_source')
    # A superseding rule retires this port, but exact earlier port memberships
    # remain current correction consumers for the same physical reported flow.
    fixture.at = '2021-01-17T16:00:00Z'
    current = fixture.read(facility)
    definition = json.loads(query(f"SELECT request->'body' FROM trading.storage_facility_commands WHERE catalog_id='{catalog}' "
        f"AND outcome->>'versionId'='{current['version']['id']}' LIMIT 1;"))
    connection = fixture.points[1]
    retired = fixture.record({**definition, 'expectedStateVersion': current['stateVersion'], 'expectedPreviousId': current['version']['id'],
        'ports': [dict(portId=str(uuid4()), pipeline=connection['pipeline_code'], zone=connection['zone_code'], point=connection['point_code'],
            product=connection['product_code'], direction='inject', contractDirection=1)], 'evidence': 'Synthetic retirement preserves historical source reconciliation'},
        effective='2021-01-17T00:00:00Z')
    assert fixture.read(facility)['version']['id'] == retired['versionId']
    historical = fixture.capture()
    revision = dict(kind='source', ledgerId=source['ledgerId'], expectedPreviousId=source['sourceRevisionId'], quantity='90',
        evidence='Synthetic independently corrected reported flow')
    corrected = json.loads(runtime_query(query, fixture.manifest,
        f"SELECT security.record_facility_quantity_command('{catalog}','{uuid4()}',{literal(revision)},'{fixture.at}')::text;"))
    compatible = search(source['day'] + ' ' + source['point'])
    assert len(compatible) == 1 and compatible[0]['sourceRevisionId'] == corrected['revisionId'], \
        'A current source correction disappeared when a later asset version retired its exact historical port.'
    assert search(source['day'] + ' ' + connection['point_code']) == [], 'A retained source was relabeled as the newer unrelated port.'
    assert linked()['quantity'] == '100' and linked()['sourceCorrected'], 'A source correction overwrote the accepted physical source pin.'
    assert fixture.read(facility)['linkedSourceCorrection'] and fixture.read(facility)['requiresReassessment']
    assert not next(a for a in fixture.read(facility, historical)['activities'] if a['business_key'] == body['businessKey'])['sourceCorrected']
    legacy = json.loads(fixture.call(f"SELECT security.read_facility_quantity_ledger('{catalog}','{source['ledgerId']}')::text;"))
    assert legacy['source']['quantity'] == '90' and legacy['allocations'][0]['quantity'] == '60'
    assert legacy['requiresReconciliation'], 'The source correction silently replaced the retained contract allocation.'
    amended = fixture.record({**body, 'expectedStateVersion': fixture.read(facility)['stateVersion'], 'expectedPreviousId': accepted['revisionId'],
        'sourceRevisionId': corrected['revisionId'], 'evidence': 'Synthetic explicit physical source reconciliation'})
    assert linked()['quantity'] == '90' and not linked()['sourceCorrected']
    assert linked()['id'] == amended['revisionId'] and linked()['source_revision_id'] == corrected['revisionId']
    rejected(lambda: fixture.record({**body, 'expectedStateVersion': fixture.read(facility)['stateVersion'], 'expectedPreviousId': amended['revisionId'],
        'quantity': '100', 'sourceRevisionId': None}), 'storage_facility_flow_source')
    assert fixture.record(body, effective=initial_at)['revisionId'] == accepted['revisionId'] and linked()['quantity'] == '90'
    print('PASS one reported flow: same100→90 physical source, independent allocation60, retired-port correction search, historical cuts and duplicate coverage guarded', flush=True)
