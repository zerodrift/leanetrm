"""Exact delivery-source authority must survive the later unit-label read."""
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
import time


def check(client,path,body,query,manifest):
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    saved=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='ReadMarketData' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
    role,grant=str(uuid4()),str(uuid4())
    key=740822103
    try:
        query(f"""BEGIN;
        CREATE SEQUENCE security.delivery_unit_signal;
        CREATE FUNCTION security.delivery_unit_gate() RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $gate$
        BEGIN
          IF strpos(current_query(),'json_build_object(''code'',q.code,''unit'',u.symbol)')>0 THEN
            PERFORM nextval('security.delivery_unit_signal');
            PERFORM pg_advisory_xact_lock({key});
          END IF;
          RETURN true;
        END $gate$;
        CREATE POLICY delivery_unit_gate ON ref_data.units AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.delivery_unit_gate());
        INSERT INTO security.roles VALUES('{role}','delivery_expiry_{role}','Synthetic delivery source expiry');
        DELETE FROM security.role_permissions WHERE permission='ReadMarketData' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');
        INSERT INTO security.role_permissions VALUES('{role}','ReadMarketData');
        INSERT INTO security.role_grants VALUES('{grant}','{principal}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '2 seconds',false);
        COMMIT;""")
        with ThreadPoolExecutor(max_workers=2) as pool:
            holder=pool.submit(query,f"BEGIN;SELECT pg_advisory_xact_lock({key});SELECT pg_sleep(3);COMMIT;")
            until=time.monotonic()+2
            while query(f"SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid={key} AND granted;")!='1':
                assert time.monotonic()<until;time.sleep(.02)
            waiter=pool.submit(client.call,path,body,expected=403)
            until=time.monotonic()+2
            while query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE l.locktype='advisory' AND l.objid={key} AND NOT l.granted AND strpos(a.query,'json_build_object(''code'',q.code,''unit'',u.symbol)')>0;")!='1':
                assert time.monotonic()<until and not waiter.done(),'Did not observe the new unit-label query wait after program admission'
                time.sleep(.02)
            assert query('SELECT is_called FROM security.delivery_unit_signal;')=='t'
            assert query(f"SELECT granted_to>clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t','Authority must still be valid when the exact wait is observed'
            holder.result(timeout=5)
            waiter.result(timeout=5)
    finally:
        encoded=saved.replace("'","''")
        query(f"""BEGIN;
        DROP POLICY IF EXISTS delivery_unit_gate ON ref_data.units;
        DROP FUNCTION IF EXISTS security.delivery_unit_gate();DROP SEQUENCE IF EXISTS security.delivery_unit_signal;
        DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{role}';DELETE FROM security.roles WHERE id='{role}';
        INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded}'::jsonb) ON CONFLICT DO NOTHING;
        COMMIT;""")
    print('PASS physical delivery authority: ReadMarketData expires during the observed post-program unit-label wait; exact source publication refused',flush=True)
