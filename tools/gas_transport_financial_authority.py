"""Current scope and original-deadline controls for financial Transport."""
import json
import os
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from gas_transport_financial_fixture import literal


def worker_validation(f, rejected):
    signature = 'security.require_gas_transport_financial_commit(uuid,uuid,text,timestamptz)'
    assert f.query(f"SELECT has_function_privilege('openetrm_risk_worker','{signature}','EXECUTE');") == 't', 'The readonly financial worker cannot recheck its captured admission.'
    for kind, trade in [('book', None), ('adopt_capacity_version', None), ('adopt_rights_mark', None), ('expire', None), (None, str(uuid4()))]:
        selected_kind = 'NULL' if kind is None else repr(kind)
        selected_trade = 'NULL' if trade is None else repr(trade)
        rejected(lambda: f.query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;"
            f"SELECT security.require_gas_transport_financial_commit('{f.catalog}',{selected_trade},{selected_kind},clock_timestamp()+interval '1 minute');COMMIT;"), 'gas_transport_financial_denied')
    assert f.query("SELECT bool_and(NOT has_function_privilege('openetrm_risk_worker',oid,'EXECUTE')) FROM pg_proc WHERE pronamespace='security'::regnamespace "
        "AND proname IN('book_gas_transport_financial','record_gas_transport_financial','gas_transport_financial_status','gas_transport_financial_capacity_facts');") == 't'
    assert f.query("SELECT NOT has_table_privilege('openetrm_risk_worker','trading.gas_transport_financial_states','SELECT,INSERT,UPDATE,DELETE');") == 't'
    print('PASS financial worker may validate only its null-target readonly admission; every write kind and foreign target refuses before disclosure, with no producer or raw-table access.', flush=True)


def entry_authority(f):
    """The production pre-source capture cannot acquire a later GasOps grant."""
    from gas_transport_financial_managed import _environment, _publication_heads
    root, directory = _environment(f)
    role = f.query("SELECT id FROM security.roles WHERE code='trader';")
    actor = f.query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    for missing in (False, True):
        context = f.capture(day=f.valuation_at[:10])
        temporary, first, successor = (str(uuid4()) for _ in range(3))
        packet = dict(host=str(directory), manifest=f.manifest, mode='entry-authority', profile='trader', contextId=context,
            at=f.valuation_at, businessDate=f.valuation_at[:10], entitlementId=f.entitlement, holdSeconds=6)
        path = directory / ('financial-entry-missing.json' if missing else 'financial-entry-expired.json')
        path.write_text(json.dumps(packet))
        path.chmod(0o600)
        original = '' if missing else f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '3 seconds',false);"
        f.query(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOperations');"
            f"INSERT INTO security.roles VALUES('{temporary}','synthetic_financial_entry_{temporary[:8]}','Synthetic original financial entry authority');"
            f"INSERT INTO security.role_permissions VALUES('{temporary}','ReadGasOperations');" + original +
            f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{f.manifest['tenantId']}','{f.manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
        epoch, publications = f.query('SELECT generation FROM security.epoch;'), _publication_heads(f)
        try:
            result = subprocess.run([os.environ['DOTNET'], str(f._financial_checks / 'OpenEtrm.Api.Checks.dll'),
                '--gas-transport-financial-native', str(path)], cwd=root, capture_output=True, text=True, timeout=30)
            detail = result.stdout + result.stderr
            assert result.returncode != 0 and 'FINANCIAL_ENTRY_READ_SUCCEEDED' not in detail, 'Financial source resolution promoted a successor GasOps grant.\n' + detail
            if missing:
                assert 'OpenEtrm.Api.PreviewDeniedException' in detail, detail
            else:
                assert any(code in detail for code in ['gas_transport_capacity_denied', 'gas_transport_financial_denied']), detail
            assert ('FINANCIAL_ENTRY_CAPTURED' in detail) is not missing, detail
            f.query('SELECT pg_sleep(4);')
            assert f.query('SELECT generation FROM security.epoch;') == epoch
            assert f.call("SELECT security.has_permission('ReadGasOperations');") == 't'
            assert _publication_heads(f) == publications
        finally:
            f.query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';"
                f"DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions VALUES('{role}','ReadGasOperations');COMMIT;")
    print('PASS production financial entry captures GasOps before source resolution: expired and absent original authority refuse despite active successor, unchanged epoch and no publication.', flush=True)


def authority(f, trade, rejected):
    q, manifest = f.query, f.manifest
    role = q("SELECT id FROM security.roles WHERE code='trader';")
    actor = q("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    assert json.loads(q("SELECT jsonb_agg(permission ORDER BY permission) FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='viewer');")) == ['ReadMarketData', 'ReadTrades']
    assert f.read_financial(trade, profile='viewer')['mark'] is not None
    unrelated = ['ReadGasOperations', 'RecordGasOperations', 'ConfigureGasServices', 'ReadGasOwnedHoldings',
        'RecordGasOwnedHoldings', 'ConfigureGasOwnedHoldings', 'ReadFacilityQuantities', 'RecordFacilityQuantities', 'ConfigureFacilityLimits']
    quoted = ','.join(repr(p) for p in unrelated)
    removed = json.loads(q(f"SELECT coalesce(jsonb_agg(permission),'[]') FROM security.role_permissions WHERE role_id='{role}' AND permission=ANY(ARRAY[{quoted}]);"))
    q(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission=ANY(ARRAY[{quoted}]);")
    try:
        assert f.read_financial(trade)['mark'] is not None
        source = f.capture()
        assert f.call(f"SELECT security.require_gas_transport_financial_scope('{f.catalog}','{source}','{f.valuation_at}',ARRAY['{f.book}']::uuid[],NULL);") == 't'
        rejected(lambda: f.call(f"SELECT security.require_gas_transport_financial_scope('{f.catalog}','{source}','{f.valuation_at}',ARRAY['{f.book}','{f.book}']::uuid[],NULL);"), 'gas_transport_financial_scope')
        q(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','AmendTrade');")
        try:
            assert f.read_financial(trade)['mark'] is not None
            assert json.loads(f.call(f"SELECT security.gas_transport_financial_status('{f.catalog}','{uuid4()}');"))['found'] is False
            current = f.read_financial(trade)
            rejected(lambda: f.call(f"SELECT security.gas_transport_financial_status('{f.catalog}','{current['mark']['id']}');"), 'gas_transport_financial_denied')
        finally:
            q(f"INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');")
    finally:
        for permission in removed:
            q(f"INSERT INTO security.role_permissions VALUES('{role}','{permission}') ON CONFLICT DO NOTHING;")
    print('PASS complete financial read/presence needs no gas-operation, owned-stock or facility authority; missing financial write permission denies receipt recovery while absence remains a read.', flush=True)

    for catalog in [f.reference, manifest['referenceCatalogId']]:
        context = f.capture()
        scope = json.loads(q(f"SELECT to_jsonb(s) FROM security.reference_catalog_scopes s WHERE(catalog_id,tenant_id,workspace_id)=('{catalog}','{manifest['tenantId']}','{manifest['workspaceId']}');"))
        q(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{catalog}','{manifest['tenantId']}','{manifest['workspaceId']}');")
        try:
            rejected(lambda: f.read_financial(trade, context=context), 'gas_transport_financial_denied')
        finally:
            q(f"INSERT INTO security.reference_catalog_scopes SELECT(jsonb_populate_record(NULL::security.reference_catalog_scopes,{literal(scope)})).*;")

    def install(permission, *, missing=False):
        temporary, first, successor = [str(uuid4()) for _ in range(3)]
        original = '' if missing else f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '2 seconds',false);"
        q(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','{permission}');"
            f"INSERT INTO security.roles VALUES('{temporary}','synthetic_financial_timer_{temporary[:8]}','Synthetic financial original admission');"
            f"INSERT INTO security.role_permissions VALUES('{temporary}','{permission}');" + original +
            f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
        return temporary, first, successor

    def restore(permission, ids):
        temporary, first, successor = ids
        q(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';"
            f"DELETE FROM security.roles WHERE id='{temporary}';INSERT INTO security.role_permissions VALUES('{role}','{permission}');COMMIT;")

    def proposal(evidence):
        current = f.read_financial(trade)
        return {**f.mark(trade, current['financialState']['id']), 'expectedPreviousMarkId': current['mark']['id'],
            'observationId': current['mark']['observationId'], 'evidence': evidence}

    for mode in ['read', 'status_read', 'record', 'status_amend', 'record_missing', 'status_missing', 'capacity_read', 'capacity_missing']:
        permission = 'ReadMarketData' if mode in ['read', 'status_read'] else 'ReadGasOperations' if mode.startswith('capacity') else 'AmendTrade'
        context = f.capture()
        body = proposal('Synthetic original admission ' + mode)
        accepted_mark = f.read_financial(trade)['mark']['id']
        table = 'security.reference_catalog_scopes' if permission == 'ReadMarketData' or mode.startswith('capacity') else 'security.market_catalog_scopes'
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocker = pool.submit(q, f"SET application_name='synthetic_financial_scope_wait';BEGIN;LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep(7);COMMIT;")
            until = time.monotonic() + 5
            while q("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic_financial_scope_wait' AND wait_event='PgSleep';") != '1':
                assert time.monotonic() < until
                time.sleep(.02)
            ids = install(permission, missing=mode.endswith('_missing'))
            generation = q('SELECT generation FROM security.epoch;')
            try:
                if mode.startswith('capacity'):
                    rejected(lambda: f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');"
                        f"SELECT security.use_trading_context('{f.catalog}','{f.valuation_at}','{f.valuation_at[:10]}');"
                        f"SELECT security.gas_transport_financial_capacity_facts('{f.catalog}','{context}','{f.valuation_at}','{f.entitlement}');"), 'gas_transport_financial_denied')
                elif mode == 'read':
                    rejected(lambda: f.read_financial(trade, context=context), 'gas_transport_financial_denied')
                elif mode.startswith('status'):
                    rejected(lambda: f.call(f"SELECT security.gas_transport_financial_status('{f.catalog}','{accepted_mark}');"), 'gas_transport_financial_denied')
                else:
                    # Probe the native entry deadline. Transport registration
                    # must not wait on the scope before that deadline exists.
                    f.query=q.raw
                    try:rejected(lambda: f.record_financial(body, context=context), 'gas_transport_financial_denied')
                    finally:f.query=q
                blocker.result(timeout=15)
                assert q('SELECT generation FROM security.epoch;') == generation
                assert f.call(f"SELECT security.has_permission('{permission}');") == 't'
            finally:
                blocker.result(timeout=15)
                restore(permission, ids)
    print('PASS real exact-reference/base-scope waits preserve original read/status/write/capacity-choice authority; expired or absent entry grants never become successor authority, with unchanged epoch.', flush=True)

    body = proposal('Synthetic finite accepted mark for recovery')
    context = f.capture()
    ids = install('AmendTrade')
    try:
        operation = str(uuid4())
        accepted = f.record_financial(body, operation=operation, context=context)
        q('SELECT pg_sleep(5);')
        assert q(f"SELECT authority_until<clock_timestamp() FROM trading.gas_transport_financial_commands WHERE(catalog_id,operation_id)=('{f.catalog}','{operation}');") == 't'
        recovered = json.loads(f.call(f"SELECT security.gas_transport_financial_status('{f.catalog}','{operation}');"))
        assert recovered['found'] and recovered['outcome'] == accepted
        assert f.record_financial(body, operation=operation, context=context) == accepted
    finally:
        restore('AmendTrade', ids)
    body = proposal('Synthetic final admission retains original deadline')
    context = f.capture()
    ids = install('AmendTrade')
    operation = str(uuid4())
    try:
        rejected(lambda: f.call(f"SELECT security.record_gas_transport_financial('{f.catalog}','{operation}',{literal(body)},'{f.valuation_at}','{context}','{f.valuation_at}');SELECT pg_sleep(5);"), 'gas_transport_financial_denied')
        assert f.call("SELECT security.has_permission('AmendTrade');") == 't'
        assert q(f"SELECT count(*) FROM trading.gas_transport_financial_commands WHERE(catalog_id,operation_id)=('{f.catalog}','{operation}');") == '0'
    finally:
        restore('AmendTrade', ids)
    print('PASS accepted finite-grant receipt recovers under newly authorized admission; a new mark still rolls back at commit after original deadline expiry despite an active successor.', flush=True)
