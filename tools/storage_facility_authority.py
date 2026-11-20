"""Independent authority, publication-wait and immutable-witness probes."""
from concurrent.futures import ThreadPoolExecutor
import json
import time
from uuid import uuid4

from check_booked_side_programs import runtime_query
from storage_facility_fixture import literal


def checks(fixture, facility, plan_operation, plan_body, accepted, historical, before_definition, rejected):
    query, manifest, catalog = fixture.query, fixture.manifest, fixture.catalog
    role = query("SELECT id FROM security.roles WHERE code='trader';")
    def native(sql, profile='trader'):
        return runtime_query(query, manifest, sql, profile=profile)
    status = f"SELECT security.storage_facility_status('{catalog}','{plan_operation}')::text;"
    # Recovery is a current-authority read, even on a bound historical context.
    assert json.loads(fixture.call(status, context=historical))['outcome'] == accepted
    rejected(lambda: native('SELECT * FROM trading.storage_facility_openings;'), 'permission denied')
    original = json.loads(query(f"SELECT jsonb_build_object('operation',operation_id,'request',request) FROM trading.storage_facility_commands "
        f"WHERE(catalog_id,facility_id)=('{catalog}','{facility}') AND request#>>'{{body,kind}}'='define' LIMIT 1;"))
    reference = original['request']['body']['referenceCatalogId']
    access = query(f"SELECT access_mode FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)="
        f"('{reference}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    before_receipts = query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';")
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)="
        f"('{reference}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    try:
        authority = json.loads(native(f"SELECT jsonb_build_object('market',security.market_access('{catalog}','ReadMarketData'),"
            f"'facility',security.has_permission('RecordFacilityQuantities'),'reference',security.reference_access('{reference}','ReadMarketData'))::text;"))
        assert authority == dict(market=True, facility=True, reference=False)
        def repeat(operation):
            return native(f"SELECT security.record_storage_facility('{catalog}','{operation}',{literal(original['request']['body'])},"
                f"'{original['request']['effectiveAt']}',NULL,NULL);")
        rejected(lambda: repeat(original['operation']), 'storage_facility_denied')
        rejected(lambda: repeat(str(uuid4())), 'storage_facility_denied')
        assert query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';") == before_receipts
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{manifest['tenantId']}','{manifest['workspaceId']}','{access}');")
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadFacilityQuantities');")
    try:
        rejected(lambda: fixture.read(facility, historical), 'facility_quantity_denied')
        rejected(lambda: native(status), 'facility_quantity_denied')
        rejected(lambda: native(f"SELECT security.record_storage_facility('{catalog}','{plan_operation}',{literal(plan_body)},'{fixture.at}',NULL,NULL);"),
            'facility_quantity_denied')
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadFacilityQuantities');")
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','RecordFacilityQuantities');")
    try:
        assert fixture.read(facility)['facility']['id'] == facility
        rejected(lambda: native(status), 'storage_facility_denied')
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','RecordFacilityQuantities');")
    query("INSERT INTO security.role_permissions SELECT DISTINCT g.role_id,p FROM security.role_grants g "
        "JOIN security.identity_bindings i ON i.principal_id=g.principal_id CROSS JOIN unnest(ARRAY['ReadTrades','ReadFacilityQuantities'])p "
        "WHERE i.subject='viewer' ON CONFLICT DO NOTHING;")
    assert json.loads(native(status, 'viewer')) == {'found': False}, 'A different actor recovered another request receipt.'
    assert json.loads(native(f"SELECT security.record_storage_facility('{catalog}','{plan_operation}',{literal(plan_body)},'{fixture.at}',NULL,NULL)::text;")) == accepted

    # An independently incomplete future definer cannot add to a committed plan.
    query("CREATE FUNCTION security.synthetic_append_storage_plan(cat uuid,selected uuid) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
        "INSERT INTO trading.storage_facility_plan_activities SELECT catalog_id,plan_id,gen_random_uuid(),version_id,port_id,from_at,through_at,direction,quantity "
        "FROM trading.storage_facility_plan_activities WHERE(catalog_id,plan_id)=(cat,selected) LIMIT 1;$$;"
        "ALTER FUNCTION security.synthetic_append_storage_plan(uuid,uuid) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_append_storage_plan(uuid,uuid) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_append_storage_plan(uuid,uuid) TO openetrm_runtime;")
    rejected(lambda: native(f"SELECT security.synthetic_append_storage_plan('{catalog}','{accepted['revisionId']}');"), 'storage_facility_integrity')

    # A receipt must attest an existing typed fact, even if a future definer
    # accidentally bypasses the public producer and has current write authority.
    query("CREATE FUNCTION security.synthetic_storage_receipt(cat uuid,selected uuid) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
        "SELECT security.register_owner_coordinates(cat,source_context_id,source_business_on,source_known_at,source_system_id,source_timeline) FROM trading.storage_facility_commands WHERE(catalog_id,operation_id)=(cat,selected);"
        "INSERT INTO trading.storage_facility_commands SELECT catalog_id,actor_id,gen_random_uuid(),facility_id,request,fact,"
        "jsonb_set(outcome,'{revisionId}',to_jsonb(gen_random_uuid())),security.storage_facility_authority(cat,'plan'),source_context_id "
        "FROM trading.storage_facility_commands WHERE(catalog_id,operation_id)=(cat,selected);$$;"
        "ALTER FUNCTION security.synthetic_storage_receipt(uuid,uuid) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_storage_receipt(uuid,uuid) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_storage_receipt(uuid,uuid) TO openetrm_runtime;")
    rejected(lambda: native(f"SELECT security.synthetic_storage_receipt('{catalog}','{plan_operation}');"), 'storage_facility_integrity')

    query("CREATE FUNCTION security.synthetic_storage_receipt_terms(cat uuid,selected uuid,recompute boolean) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
        "SELECT security.register_owner_coordinates(cat,source_context_id,source_business_on,source_known_at,source_system_id,source_timeline) FROM trading.storage_facility_commands WHERE(catalog_id,operation_id)=(cat,selected);"
        "INSERT INTO trading.storage_facility_commands SELECT catalog_id,actor_id,gen_random_uuid(),facility_id,payload,"
        "CASE WHEN recompute THEN jsonb_build_object('body',trading.storage_facility_fact_key(payload->'body'),'effectiveAt',payload->'effectiveAt') ELSE fact END,"
        "outcome,security.storage_facility_authority(cat,'plan'),source_context_id "
        "FROM trading.storage_facility_commands c CROSS JOIN LATERAL(SELECT jsonb_set(c.request,CASE WHEN c.request#>>'{body,kind}'='plan' "
        "THEN ARRAY['body','activities','0','quantity'] ELSE ARRAY['body','quantity'] END,'\"999\"'::jsonb) payload)s "
        "WHERE(catalog_id,operation_id)=(cat,selected);$$;"
        "ALTER FUNCTION security.synthetic_storage_receipt_terms(uuid,uuid,boolean) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_storage_receipt_terms(uuid,uuid,boolean) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_storage_receipt_terms(uuid,uuid,boolean) TO openetrm_runtime;")
    for recompute in ['false', 'true']:
        rejected(lambda: native(f"SELECT security.synthetic_storage_receipt_terms('{catalog}','{plan_operation}',{recompute});"), 'storage_facility_integrity')

    query("CREATE FUNCTION security.synthetic_storage_receipt_context(cat uuid,selected uuid,source uuid,changed boolean) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
        "INSERT INTO trading.storage_facility_commands SELECT catalog_id,actor_id,gen_random_uuid(),facility_id,jsonb_set(request,'{contextId}',to_jsonb(source)),"
        "fact,outcome||jsonb_build_object('changed',changed),security.storage_facility_authority(cat,request#>>'{body,kind}'),source "
        "FROM trading.storage_facility_commands WHERE(catalog_id,operation_id)=(cat,selected);$$;"
        "ALTER FUNCTION security.synthetic_storage_receipt_context(uuid,uuid,uuid,boolean) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_storage_receipt_context(uuid,uuid,uuid,boolean) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_storage_receipt_context(uuid,uuid,uuid,boolean) TO openetrm_runtime;")
    for changed in ['true', 'false']:
        rejected(lambda: native(f"SELECT security.synthetic_storage_receipt_context('{catalog}','{plan_operation}','{before_definition}',{changed});"),
            'storage_facility_integrity')
    # The same proof must hold inside the original publication transaction;
    # merely comparing publication xids would miss forged same-transaction terms.
    operation = str(uuid4())
    context = fixture.capture()
    candidate = dict(kind='opening', facilityId=facility, expectedStateVersion=fixture.read(facility)['stateVersion'],
        businessKey='synthetic_same_transaction_receipt', expectedPreviousId=None, observedAt='2021-01-15T00:00:00Z', quantity='400',
        evidence='Synthetic receipt provenance probe')
    publish = f"SELECT security.record_storage_facility('{catalog}','{operation}',{literal(candidate)},'{fixture.at}',NULL,'{context}');"
    receipts = query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';")
    publication = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    rejected(lambda: native(publish + f"SELECT security.synthetic_storage_receipt_terms('{catalog}','{operation}',true);"), 'storage_facility_integrity')
    rejected(lambda: native(publish + f"SELECT security.synthetic_storage_receipt_context('{catalog}','{operation}','{before_definition}',true);"), 'storage_facility_stale')
    assert query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';") == receipts
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == publication

    query("CREATE FUNCTION security.synthetic_storage_unreceipted_opening(cat uuid,asset uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
        "DECLARE publication integer;actor uuid;BEGIN PERFORM pg_advisory_xact_lock(730849102);"
        "SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;"
        "SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id=cat;"
        "INSERT INTO trading.publications VALUES(cat,publication,pg_current_xact_id());"
        "INSERT INTO trading.storage_facility_openings VALUES(cat,gen_random_uuid(),asset,'synthetic_unreceipted_opening',1,NULL,"
        "'2021-01-15T00:00:00Z',1,'Synthetic missing source and request attestation',actor,publication,'2021-01-15T16:00:00Z');END$$;"
        "ALTER FUNCTION security.synthetic_storage_unreceipted_opening(uuid,uuid) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_storage_unreceipted_opening(uuid,uuid) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_storage_unreceipted_opening(uuid,uuid) TO openetrm_runtime;")
    rejected(lambda: native(f"SELECT security.synthetic_storage_unreceipted_opening('{catalog}','{facility}');"), 'storage_facility_integrity')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == publication

    query("CREATE FUNCTION security.synthetic_storage_append_port(cat uuid,selected uuid,point text) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$ "
        "INSERT INTO trading.storage_facility_ports SELECT p.catalog_id,p.version_id,gen_random_uuid(),p.reference_catalog_id,p.reference_revision_id,"
        "d.product_code,d.pipeline_code,d.zone_code,d.point_code,p.direction,p.contract_direction "
        "FROM trading.storage_facility_commands c JOIN trading.storage_facility_ports p ON(p.catalog_id,p.version_id)=(c.catalog_id,(c.outcome->>'versionId')::uuid) "
        "JOIN ref_data.gas_delivery_conventions d ON(d.catalog_id,d.revision_id,d.product_code)=(p.reference_catalog_id,p.reference_revision_id,p.product_code) "
        "WHERE(c.catalog_id,c.operation_id,d.point_code)=(cat,selected,point) LIMIT 1;$$;"
        "ALTER FUNCTION security.synthetic_storage_append_port(uuid,uuid,text) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_storage_append_port(uuid,uuid,text) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_storage_append_port(uuid,uuid,text) TO openetrm_runtime;")
    connection = json.loads(query(f"SELECT to_jsonb(p) FROM ref_data.gas_delivery_conventions p WHERE(catalog_id,revision_id,point_code)="
        f"('{reference}','{original['request']['body']['referenceRevisionId']}','synthetic_gas_point_b');"))
    operation = str(uuid4())
    definition = {**original['request']['body'], 'facilityId': str(uuid4()), 'facilityCode': connection['point_code'],
        'expectedStateVersion': 0, 'expectedPreviousId': None, 'ports': [dict(portId=str(uuid4()), pipeline=connection['pipeline_code'],
            zone=connection['zone_code'], point=connection['point_code'], product=connection['product_code'], direction='inject', contractDirection=1)]}
    context = fixture.capture()
    rejected(lambda: native(f"SELECT security.record_storage_facility('{catalog}','{operation}',{literal(definition)},'{fixture.at}',NULL,'{context}');"
        f"SELECT security.synthetic_storage_append_port('{catalog}','{operation}','synthetic_gas_point_c');"), 'storage_facility_integrity')
    assert query(f"SELECT count(*) FROM trading.storage_facilities WHERE(catalog_id,id)=('{catalog}','{definition['facilityId']}');") == '0'
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == publication

    grant = query(f"SELECT id FROM security.role_grants WHERE role_id='{role}' AND principal_id="
        "(SELECT principal_id FROM security.identity_bindings WHERE subject='trader') "
        f"AND(tenant_id,workspace_id)=('{manifest['tenantId']}','{manifest['workspaceId']}');")
    until = query(f"SELECT granted_to FROM security.role_grants WHERE id='{grant}';")
    before = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    count = query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';")
    command = dict(kind='opening', facilityId=facility, expectedStateVersion=fixture.read(facility)['stateVersion'],
        businessKey='synthetic_expired_observation', expectedPreviousId=None, observedAt='2021-01-15T00:00:00Z', quantity='1',
        evidence='Synthetic authority expiry must leave no observation')
    selected_context = fixture.capture()
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.75 seconds' WHERE id='{grant}';")
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocker = pool.submit(query, "SET application_name='storage_facility_authority_wait';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(2);COMMIT;")
            try:
                deadline = time.monotonic() + 3
                while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='storage_facility_authority_wait' AND wait_event='PgSleep');") != 't':
                    assert time.monotonic() < deadline
                    time.sleep(.01)
                rejected(lambda: fixture.record(command, context=selected_context), 'storage_facility_denied')
            finally:
                blocker.result()
    finally:
        query(f"UPDATE security.role_grants SET granted_to='{until}' WHERE id='{grant}';")
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == before
    assert query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';") == count
    selected_context = fixture.capture()
    after_producer = {**command, 'businessKey': 'synthetic_expired_after_producer',
        'evidence': 'Synthetic original authority must survive through commit'}
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '4 seconds' WHERE id='{grant}';")
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(native, f"SELECT security.record_storage_facility('{catalog}','{uuid4()}',{literal(after_producer)},'{fixture.at}',NULL,'{selected_context}');"
                "SET LOCAL application_name='storage_facility_after_producer';SELECT pg_sleep(5);")
            deadline = time.monotonic() + 3
            while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='storage_facility_after_producer' AND wait_event='PgSleep');") != 't':
                if pending.done():
                    raise AssertionError('The producer did not return before the intended post-producer expiry window: ' + str(pending.result()))
                assert time.monotonic() < deadline, 'The producer did not reach its explicit post-return pause.'
                time.sleep(.01)
            rejected(lambda: pending.result(), 'storage_facility_denied')
    finally:
        query(f"UPDATE security.role_grants SET granted_to='{until}' WHERE id='{grant}';")
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == before
    assert query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';") == count
    head = fixture.read(facility)['stateVersion']
    context = fixture.capture()
    def simultaneous(index):
        body = {**command, 'expectedStateVersion': head, 'businessKey': 'synthetic_concurrent_observation_' + str(index),
            'quantity': str(400 + index), 'evidence': 'Synthetic shared-facility admission race'}
        try:
            return fixture.record(body, context=context)
        except RuntimeError as error:
            return str(error)
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(simultaneous, [0, 1]))
    assert sum(isinstance(outcome, dict) for outcome in outcomes) == 1, outcomes
    assert sum(isinstance(outcome, str) and 'storage_facility_stale' in outcome for outcome in outcomes) == 1, outcomes
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")) == int(before) + 1
    assert int(query(f"SELECT count(*) FROM trading.storage_facility_commands WHERE catalog_id='{catalog}';")) == int(count) + 1
    print('PASS storage current/reference authority, typed receipt sealing, original deadline through supported commit, expiry after waits and serialized shared-facility admission', flush=True)
