"""The lifecycle adapter can discover only an exact accepted expiry fact."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from gas_transport_financial_fixture import literal


def accepted_expiry(f, closed, rejected):
    original = json.loads(f.query(f"SELECT to_jsonb(c) FROM trading.gas_transport_financial_commands c "
        f"WHERE(catalog_id,operation_id)=('{f.catalog}','{closed['financialStateId']}');"))
    body = original['request']['body']

    def lookup(command=body, *, profile='trader'):
        return json.loads(f.call(f"SELECT security.gas_transport_financial_accepted_expiry('{f.catalog}',{literal(command)});", profile=profile))

    found = lookup()
    assert found['found'] and found['effectiveAt'] == original['request']['effectiveAt']
    assert found['outcome'] == original['outcome'] and found['authorityUntil']

    previous_at = f.at
    try:
        f.at = '2020-12-19T23:59:59.999999Z'
        book, capacity, version = f.new_capacity('synthetic_expiry_lookup_other_target')
        other = f.book_financial({**f.booking(key='synthetic_expiry_lookup_other_target', fee='0'),
            'bookId': book, 'entitlementId': capacity['entitlementId'], 'capacityVersionId': version['versionId']})
    finally:
        f.at = previous_at
    # A newer policy is not a replacement for the exact accepted policy pin.
    f.call(f"SELECT lifecycle.save_policy('{f.catalog}',{literal(dict(operationId=str(uuid4()), policyId=body['policyId'],
        expectedRevision=body['policyRevision'], action='gas.transport_financial.expire', mode='review',
        fromAt='2020-01-01T00:00:00Z', untilAt='2022-01-01T00:00:00Z', implementation='explicit_lifecycle_action_policy_v1'))});")
    counts = lambda: f.query(f"SELECT jsonb_build_array((SELECT count(*) FROM trading.publications WHERE catalog_id='{f.catalog}'),"
        f"(SELECT count(*) FROM trading.gas_transport_financial_states WHERE catalog_id='{f.catalog}'),"
        f"(SELECT count(*) FROM trading.gas_transport_financial_commands WHERE catalog_id='{f.catalog}'));")
    before = counts()
    assert lookup()['outcome'] == original['outcome']
    assert lookup({**body, 'evidence': '  ' + body['evidence'] + '  '})['outcome'] == original['outcome']
    for changed in [{**body, 'policyId': str(uuid4())}, {**body, 'policyRevision': body['policyRevision'] + 1},
            {**body, 'expectedFinancialStateId': closed['financialStateId']},
            {**body, 'evidence': 'Synthetic different expiry evidence'}, {**body, 'tradeId': other['tradeId']}]:
        absent = lookup(changed)
        assert absent['found'] is False and absent['authorityUntil'] and 'outcome' not in absent and 'effectiveAt' not in absent
    rejected(lambda: lookup(profile='viewer'), 'gas_transport_financial_denied')
    rejected(lambda: lookup({**body, 'unexpected': 'value'}), 'gas_transport_financial_input')
    assert f.query("SELECT NOT has_function_privilege('openetrm_risk_worker','security.gas_transport_financial_accepted_expiry(uuid,jsonb)','EXECUTE');") == 't'
    rejected(lambda: f.query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;"
        f"SELECT security.gas_transport_financial_accepted_expiry('{f.catalog}',{literal(body)});COMMIT;"), 'permission denied for function gas_transport_financial_accepted_expiry')
    for reference in [f.reference, f.manifest['referenceCatalogId']]:
        scope = json.loads(f.query(f"SELECT to_jsonb(s) FROM security.reference_catalog_scopes s "
            f"WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}');"))
        f.query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}');")
        try:
            rejected(lookup, 'gas_transport_financial_denied')
        finally:
            f.query(f"INSERT INTO security.reference_catalog_scopes SELECT(jsonb_populate_record(NULL::security.reference_catalog_scopes,{literal(scope)})).*;")
    role = f.query("SELECT id FROM security.roles WHERE code='trader';")
    actor = f.query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    temporary, first, successor = (str(uuid4()) for _ in range(3))
    with ThreadPoolExecutor(max_workers=1) as pool:
        blocker = pool.submit(f.query, "SET application_name='synthetic_expiry_lookup_wait';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(7);COMMIT;")
        timeout = time.monotonic() + 5
        while f.query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic_expiry_lookup_wait' AND wait_event='PgSleep';") != '1':
            assert time.monotonic() < timeout
            time.sleep(.02)
        f.query(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','AmendTrade');"
            f"INSERT INTO security.roles VALUES('{temporary}','synthetic_expiry_lookup_{temporary[:8]}','Synthetic expiry lookup original authority');"
            f"INSERT INTO security.role_permissions VALUES('{temporary}','AmendTrade');"
            f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '2 seconds',false);"
            f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
        epoch = f.query('SELECT generation FROM security.epoch;')
        try:
            rejected(lookup, 'gas_transport_financial_denied')
            blocker.result(timeout=15)
            assert f.query('SELECT generation FROM security.epoch;') == epoch
            assert f.call("SELECT security.has_permission('AmendTrade');") == 't'
        finally:
            blocker.result(timeout=15)
            f.query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';"
                f"DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');COMMIT;")
    assert lookup()['outcome'] == original['outcome']
    assert before == counts(), 'Accepted-expiry lookup published a state, receipt or trading event.'
    print('PASS accepted-expiry lookup retains exact original body/time/outcome, ignores fresh policy heads, refuses changed policy/evidence/state/target and reader/worker/reference denial, preserves original authority across a publication-lock wait despite an active successor, and publishes nothing.', flush=True)
