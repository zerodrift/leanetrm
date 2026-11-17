"""Current and original admission checks for the operational capacity consumer."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from gas_transport_capacity_fixture import literal


def authority(f, rejected):
    q, manifest = f.query, f.manifest
    role=q("SELECT id FROM security.roles WHERE code='trader';")
    actor=q("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    # Remove unrelated authority explicitly, rather than relying on seed roles.
    unrelated=['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings','ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits','BookTrade','AmendTrade']
    permissions=json.loads(q(f"SELECT coalesce(jsonb_agg(permission),'[]') FROM security.role_permissions WHERE role_id='{role}' AND permission=ANY(ARRAY[{','.join(repr(p) for p in unrelated)}]);"))
    q(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission=ANY(ARRAY[{','.join(repr(p) for p in unrelated)}]);")
    try:
        assert f.call("SELECT security.has_permission('ReadGasOwnedHoldings') OR security.has_permission('RecordGasOwnedHoldings') OR security.has_permission('ReadFacilityQuantities') OR security.has_permission('BookTrade');")=='f'
        body=f.definition('synthetic_no_financial_authority')
        op=str(uuid4());outcome=f.record(body,operation=op)
        assert f.read(body['entitlementId'])['version']['id']==op
        q(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOperations');")
        try:
            rejected(lambda:f.read(body['entitlementId']),'gas_transport_capacity_denied')
            rejected(lambda:f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{op}');"),'gas_transport_capacity_denied')
        finally:q(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOperations');")
        q(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ConfigureGasServices');")
        try:
            rejected(lambda:f.record(f.definition('synthetic_missing_configuration')),'gas_transport_capacity_denied')
            assert f.read(body['entitlementId'])['version']['id']==op
        finally:q(f"INSERT INTO security.role_permissions VALUES('{role}','ConfigureGasServices');")
        scope=json.loads(q(f"SELECT to_jsonb(s) FROM security.reference_catalog_scopes s WHERE(catalog_id,tenant_id,workspace_id)=('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}');"))
        q(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{scope['catalog_id']}','{scope['tenant_id']}','{scope['workspace_id']}');")
        try:
            rejected(lambda:f.read(body['entitlementId']),'gas_transport_capacity_denied')
            rejected(lambda:f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{op}');"),'gas_transport_capacity_denied')
            rejected(lambda:f.call(f"SELECT security.gas_transport_capacity_prior_fact('{f.catalog}',{literal(body)},'{f.effective}');"),'gas_transport_capacity_denied')
        finally:q(f"INSERT INTO security.reference_catalog_scopes SELECT(jsonb_populate_record(NULL::security.reference_catalog_scopes,{literal(scope)})).*;")
        other=q("SELECT principal_id FROM security.identity_bindings WHERE subject='viewer';")
        grant=str(uuid4())
        q(f"INSERT INTO security.role_grants VALUES('{grant}','{other}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '30 seconds',false);")
        try:rejected(lambda:f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{op}');",profile='viewer'),'gas_transport_capacity_denied')
        finally:q(f"DELETE FROM security.role_grants WHERE id='{grant}';")
    finally:
        for permission in permissions:q(f"INSERT INTO security.role_permissions VALUES('{role}','{permission}') ON CONFLICT DO NOTHING;")
    print('PASS operational scope needs neither financial/owned/facility authority; current read/config/reference revocation and wrong-actor receipt recovery refuse.',flush=True)

    temporary,first,successor=str(uuid4()),str(uuid4()),str(uuid4())
    def grants():
        q(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ConfigureGasServices');"
            f"INSERT INTO security.roles VALUES('{temporary}','synthetic_transport_expiry_{temporary[:8]}','Synthetic Transport configuration expiry');"
            f"INSERT INTO security.role_permissions VALUES('{temporary}','ConfigureGasServices');"
            f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '2 seconds',false),"
            f"('{successor}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
    def restore():
        q(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';"
            f"INSERT INTO security.role_permissions VALUES('{role}','ConfigureGasServices');COMMIT;")
    # Source is certified BEFORE the supported publication wait; a later active
    # grant cannot replace the candidate captured at producer/status entry.
    for mode in ['record','status']:
        source=f.capture()
        with ThreadPoolExecutor(max_workers=1) as pool:
            lock=pool.submit(q,"SET application_name='synthetic_transport_expiry';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(7);COMMIT;")
            until=time.monotonic()+5
            while q("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic_transport_expiry' AND l.locktype='advisory' AND l.granted;")!='1':
                assert time.monotonic()<until
                time.sleep(.02)
            grants()
            try:
                if mode=='record':rejected(lambda:f.record(f.definition('synthetic_original_write_expiry'),context=source),'gas_transport_capacity_denied')
                else:rejected(lambda:f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{op}');"),'gas_transport_capacity_denied')
                lock.result(timeout=15)
                assert f.call("SELECT security.has_permission('ConfigureGasServices');")=='t'
            finally:
                lock.result(timeout=15);restore()
    status=json.loads(f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{op}')::text;"))
    assert status['found'] and status['outcome']==outcome and status['authorityUntil']
    context=f.capture();temporary_body=f.definition('synthetic_expired_accepted_admission');temporary_operation=str(uuid4())
    grants()
    try:
        original=f.record(temporary_body,operation=temporary_operation,context=context)
        q('SELECT pg_sleep(5);')
        assert q(f"SELECT authority_until<clock_timestamp() FROM trading.gas_transport_capacity_commands WHERE(catalog_id,operation_id)=('{f.catalog}','{temporary_operation}');")=='t'
        assert f.call("SELECT security.has_permission('ConfigureGasServices');")=='t'
        recovered=json.loads(f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{temporary_operation}')::text;"))
        assert recovered['found'] and recovered['outcome']==original
        assert f.record(temporary_body,operation=temporary_operation)==original
    finally:restore()
    source=f.capture();pending_body=f.definition('synthetic_commit_original_deadline');pending_operation=str(uuid4())
    grants()
    try:
        rejected(lambda:f.call(f"SELECT security.record_gas_transport_capacity('{f.catalog}','{pending_operation}',{literal(pending_body)},'{f.effective}',NULL,'{source}');SELECT pg_sleep(5);"),
            'gas_transport_capacity_denied')
        assert f.call("SELECT security.has_permission('ConfigureGasServices');")=='t'
        assert q(f"SELECT count(*) FROM trading.gas_transport_capacity_commands WHERE(catalog_id,operation_id)=('{f.catalog}','{pending_operation}');")=='0'
    finally:restore()
    scope_entry_waits(f,rejected)
    print('PASS original producer and status conditional deadlines survive real publication waits and active successor grants; a fresh authorized recovery retains the accepted outcome.',flush=True)


def scope_entry_waits(f,rejected):
    """Conditional admission precedes a supported initial scope-table wait."""
    q,manifest=f.query,f.manifest
    role=q("SELECT id FROM security.roles WHERE code='trader';")
    actor=q("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    operation=str(uuid4())
    original=f.record(f.definition('synthetic_entry_scope_recovery'),operation=operation)
    # Absence is a read even when neither possible write permission is present.
    q(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission IN('ConfigureGasServices','RecordGasOperations');")
    try:
        before=q(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{f.catalog}';")
        absence=json.loads(f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{uuid4()}')::text;"))
        assert not absence['found'] and absence['authorityUntil']
        assert q(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{f.catalog}';")==before
    finally:
        q(f"INSERT INTO security.role_permissions VALUES('{role}','ConfigureGasServices'),('{role}','RecordGasOperations');")
    for mode in ('status','record','status_missing','record_missing'):
        source=f.capture()
        temporary,first,successor=[str(uuid4()) for _ in range(3)]
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocker=pool.submit(q,"SET application_name='synthetic_capacity_entry_scope';BEGIN;LOCK TABLE security.market_catalog_scopes IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep(8);COMMIT;")
            until=time.monotonic()+5
            while q("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic_capacity_entry_scope' AND wait_event='PgSleep';")!='1':
                assert time.monotonic()<until
                time.sleep(.02)
            original_grant='' if mode.endswith('_missing') else (
                f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '3 seconds',false);")
            q(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ConfigureGasServices');"
                f"INSERT INTO security.roles VALUES('{temporary}','synthetic_capacity_entry_{temporary[:8]}','Synthetic capacity entry expiry');"
                f"INSERT INTO security.role_permissions VALUES('{temporary}','ConfigureGasServices');"
                +original_grant+
                f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '5 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
            generation=q('SELECT generation FROM security.epoch;')
            try:
                # The scope-table lock leaves grant tables free. Missing entry
                # authority is -infinity; a later grant cannot promote it.
                if mode.endswith('_missing'):
                    assert f.call("SELECT security.authority_until('ConfigureGasServices')='-infinity'::timestamptz;")=='t'
                else:
                    assert f.call("SELECT security.has_permission('ConfigureGasServices');")=='t'
                if mode.startswith('status'):
                    rejected(lambda:f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{operation}');"),'gas_transport_capacity_denied')
                else:
                    rejected(lambda:f.record(f.definition('synthetic_entry_scope_write'),context=source),'gas_transport_capacity_denied')
                blocker.result(timeout=15)
                assert q('SELECT generation FROM security.epoch;')==generation
                assert f.call("SELECT security.has_permission('ConfigureGasServices');")=='t'
            finally:
                blocker.result(timeout=15)
                q(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';"
                    f"INSERT INTO security.role_permissions VALUES('{role}','ConfigureGasServices');COMMIT;")
    recovered=json.loads(f.call(f"SELECT security.gas_transport_capacity_status('{f.catalog}','{operation}')::text;"))
    assert recovered['found'] and recovered['outcome']==original
    print('PASS initial scope waits preserve expired or missing original conditional producer/status authority with active successor grants and unchanged epoch; absent recovery retains base authority only.',flush=True)


def directory_scope(f,rejected):
    context=f.capture()
    # The producer's ordinary protocol creates every scope; no trigger/role
    # bypass is used just to populate the authorized-directory bound.
    for start in range(0,257,32):
        definitions=[f.definition('synthetic_directory_scope_'+str(i)) for i in range(start,min(start+32,257))]
        f.call("DO $batch$DECLARE body jsonb;BEGIN FOR body IN SELECT * FROM jsonb_array_elements("+literal(definitions)+") LOOP "
            f"PERFORM security.record_gas_transport_capacity('{f.catalog}',gen_random_uuid(),body,'{f.effective}',NULL,'{context}');END LOOP;END;$batch$;")
    selected=f.capture()
    statement=f"SELECT security.use_market_context('{f.catalog}','{selected}');SELECT security.use_trading_context('{f.catalog}','{f.at}');SELECT security.gas_transport_capacity_directory('{f.catalog}')::text;"
    rejected(lambda:f.call(statement),'gas_transport_capacity_limit')
    manifest=f.manifest
    scope=json.loads(f.query(f"SELECT to_jsonb(s) FROM security.reference_catalog_scopes s WHERE(catalog_id,tenant_id,workspace_id)=('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}');"))
    f.query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{scope['catalog_id']}','{scope['tenant_id']}','{scope['workspace_id']}');")
    try:assert json.loads(f.call(statement))==[], 'Unauthorized reference scopes consumed the authorized directory bound.'
    finally:f.query(f"INSERT INTO security.reference_catalog_scopes SELECT(jsonb_populate_record(NULL::security.reference_catalog_scopes,{literal(scope)})).*;")
    print('PASS257 ordinary definitions enforce the authorized directory bound; inaccessible exact reference scopes neither appear nor consume its bound.',flush=True)
