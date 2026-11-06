"""Observed storage waits must not retain expired billing authority."""
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
import json
import time


def check(query, manifest, original, pending, calculation, context):
    catalog=manifest['catalogId'];tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    saved=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='AmendTrade' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
    counts=lambda:query(f"SELECT (SELECT count(*) FROM trading.gas_cash_determinations WHERE catalog_id='{catalog}')||'|'||(SELECT count(*) FROM trading.gas_billing_commands WHERE catalog_id='{catalog}')||'|'||(SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}');")
    for mode,table,lock in [('new','gas_cash_determinations','SHARE'),('alias','gas_billing_commands','SHARE'),('status','gas_billing_commands','ACCESS EXCLUSIVE')]:
        role,grant=str(uuid4()),str(uuid4());app='billing_expiry_'+str(uuid4())
        payload={**(pending if mode=='new' else original),'operationId':str(uuid4())}
        if mode=='status':payload=original
        encoded=json.dumps(payload).replace("'","''");calc=json.dumps(calculation).replace("'","''")
        before=counts()
        try:
            query(f"""BEGIN;INSERT INTO security.roles VALUES('{role}','{app}','Synthetic expiring billing authority');
              DELETE FROM security.role_permissions WHERE permission='AmendTrade' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');
              INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');
              INSERT INTO security.role_grants VALUES('{grant}','{principal}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '5 seconds',false);COMMIT;""")
            token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            with ThreadPoolExecutor(max_workers=2) as pool:
                holder=pool.submit(query,f"SET application_name='{app}_holder';BEGIN;LOCK TABLE trading.{table} IN {lock} MODE;SELECT pg_sleep(8);COMMIT;")
                deadline=time.monotonic()+2
                while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}_holder' AND wait_event='PgSleep';")!='1':
                    assert time.monotonic()<deadline;time.sleep(.02)
                operation=(f"SELECT security.record_gas_billing('{catalog}','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{context}','{calc}'::jsonb);" if mode=='new' else
                    f"SELECT security.replay_gas_billing('{catalog}','{payload['operationId']}','{encoded}'::jsonb);" if mode=='alias' else
                    f"SELECT security.gas_billing_status('{catalog}','{original['operationId']}',NULL);")
                def execute():
                    try:
                        query(f"\\set VERBOSITY verbose\nSET application_name='{app}';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{operation}COMMIT;")
                    except RuntimeError as error:return str(error)
                    raise AssertionError('Expired billing authority returned after a storage wait: '+mode)
                waiter=pool.submit(execute)
                deadline=time.monotonic()+3
                while query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{app}' AND NOT l.granted AND l.relation='trading.{table}'::regclass;")!='1':
                    assert time.monotonic()<deadline and not waiter.done();time.sleep(.02)
                holder.result(timeout=12);error=waiter.result(timeout=4)
                assert '42501' in error,error
            assert counts()==before,'Expired command leaked a fact, receipt or publication'
        finally:
            encoded_saved=saved.replace("'","''")
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{role}';DELETE FROM security.roles WHERE id='{role}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded_saved}'::jsonb) ON CONFLICT DO NOTHING;COMMIT;")
    print('PASS gas billing: independent AmendTrade expiry during observed fact/alias/status storage waits rolls back or refuses',flush=True)
