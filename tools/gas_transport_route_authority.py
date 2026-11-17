"""Route read/write admissions retain current scope and original deadlines."""
from concurrent.futures import ThreadPoolExecutor
import json
import time
from uuid import uuid4

from gas_transport_capacity_fixture import literal


def authority(f,refuses,route,operation,body):
    q=f.query;manifest=f.manifest
    role=q("SELECT id FROM security.roles WHERE code='trader';")
    actor=q("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    unrelated=['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings','ReadFacilityQuantities','RecordFacilityQuantities','ConfigureFacilityLimits','BookTrade','AmendTrade']
    rows=json.loads(q(f"SELECT coalesce(jsonb_agg(permission),'[]') FROM security.role_permissions WHERE role_id='{role}' AND permission=ANY(ARRAY[{','.join(repr(p) for p in unrelated)}]);"))
    q(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission=ANY(ARRAY[{','.join(repr(p) for p in unrelated)}]);")
    try:
        assert f.route_read(route)['route']['id']==route
        q(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOperations');")
        try:
            refuses(lambda:f.route_read(route),'gas_transport_route_denied')
            refuses(lambda:f.call(f"SELECT security.gas_transport_route_status('{f.catalog}','{operation}');"),'gas_transport_route_denied')
        finally:q(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOperations');")
        scope=json.loads(q(f"SELECT to_jsonb(s) FROM security.reference_catalog_scopes s WHERE(catalog_id,tenant_id,workspace_id)=('{manifest['referenceCatalogId']}','{manifest['tenantId']}','{manifest['workspaceId']}');"))
        q(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{scope['catalog_id']}','{scope['tenant_id']}','{scope['workspace_id']}');")
        try:
            refuses(lambda:f.route_read(route),'gas_transport_route_denied')
            refuses(lambda:f.call(f"SELECT security.gas_transport_route_status('{f.catalog}','{operation}');"),'gas_transport_route_denied')
            refuses(lambda:f.call(f"SELECT security.gas_transport_route_prior_fact('{f.catalog}',{literal(body)},'{f.effective}');"),'gas_transport_route_denied')
        finally:q(f"INSERT INTO security.reference_catalog_scopes SELECT(jsonb_populate_record(NULL::security.reference_catalog_scopes,{literal(scope)})).*;")
    finally:
        for permission in rows:q(f"INSERT INTO security.role_permissions VALUES('{role}','{permission}') ON CONFLICT DO NOTHING;")
    print('PASS route read/status/prior-fact enforce current gas/reference authority without financial, owned-gas or facility permissions.',flush=True)

    temporary,first,successor=str(uuid4()),str(uuid4()),str(uuid4())
    for mode in ['status','record','commit']:
        source=f.capture();newbody=f.route_definition('synthetic_original_route_deadline_'+mode)
        with ThreadPoolExecutor(max_workers=1) as pool:
            lock=None
            if mode!='commit':
                lock=pool.submit(q,"SET application_name='synthetic_route_expiry';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(7);COMMIT;")
                until=time.monotonic()+5
                while q("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic_route_expiry' AND l.locktype='advisory' AND l.granted;")!='1':
                    assert time.monotonic()<until;time.sleep(.02)
            q(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ConfigureGasServices');"
                f"INSERT INTO security.roles VALUES('{temporary}','synthetic_route_expiry_{temporary[:8]}','Synthetic route expiry');"
                f"INSERT INTO security.role_permissions VALUES('{temporary}','ConfigureGasServices');"
                f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '2 seconds',false),"
                f"('{successor}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
            try:
                if mode=='status':refuses(lambda:f.call(f"SELECT security.gas_transport_route_status('{f.catalog}','{operation}');"),'gas_transport_route_denied')
                elif mode=='record':refuses(lambda:f.route_record(newbody,context=source),'gas_transport_route_denied')
                else:refuses(lambda:f.call(f"SELECT security.record_gas_transport_route('{f.catalog}','{uuid4()}',{literal(newbody)},'{f.effective}',NULL,'{source}');SELECT pg_sleep(7);"),'gas_transport_route_denied')
                if lock:lock.result(timeout=15)
                assert f.call("SELECT security.has_permission('ConfigureGasServices');")=='t'
            finally:
                if lock:lock.result(timeout=15)
                q(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';"
                    f"INSERT INTO security.role_permissions VALUES('{role}','ConfigureGasServices');COMMIT;")
    assert json.loads(f.call(f"SELECT security.gas_transport_route_status('{f.catalog}','{operation}')::text;"))['found']
    print('PASS original route status/write deadlines survive publication waits and deferred commit; an active successor cannot renew the accepted entry admission.',flush=True)
