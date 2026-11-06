"""Gas operation authorization is re-evaluated after observed real storage waits."""
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
import json
import time


def check(query,manifest,nomination,application,calculation,context):
    catalog=manifest['catalogId'];tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    counts=lambda:query("SELECT string_agg(n::text,',' ORDER BY name) FROM("+" UNION ALL ".join(f"SELECT '{t}' name,count(*) n FROM trading.{t}" for t in ['gas_operation_commands','gas_imbalance_applications','gas_account_movements','gas_quantity_revisions','publications'])+")x;")
    for mode,permission,table,lock in [('new','RecordGasOperations','gas_account_movements','SHARE'),('alias','RecordGasOperations','gas_operation_commands','SHARE'),('status','ReadGasOperations','gas_operation_commands','ACCESS EXCLUSIVE'),('unknown','ReadGasOperations','gas_operation_commands','ACCESS EXCLUSIVE')]:
        saved=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='{permission}' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
        role,grant,app=str(uuid4()),str(uuid4()),'gas_ops_expiry_'+uuid4().hex
        body={**(application if mode=='new' else nomination),'operationId':str(uuid4())};encoded=json.dumps(body).replace("'","''");calc=json.dumps(calculation).replace("'","''");before=counts()
        try:
            query(f"BEGIN;INSERT INTO security.roles VALUES('{role}','{app}','Synthetic expiring gas authority');DELETE FROM security.role_permissions WHERE permission='{permission}' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');INSERT INTO security.role_permissions VALUES('{role}','{permission}');INSERT INTO security.role_grants VALUES('{grant}','{principal}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '5 seconds',false);COMMIT;")
            token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            with ThreadPoolExecutor(max_workers=2) as pool:
                holder=pool.submit(query,f"SET application_name='{app}_holder';BEGIN;LOCK TABLE trading.{table} IN {lock} MODE;SELECT pg_sleep(8);COMMIT;")
                deadline=time.monotonic()+2
                while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}_holder' AND wait_event='PgSleep';")!='1':
                    assert time.monotonic()<deadline;time.sleep(.02)
                operation=(f"SELECT security.record_gas_operations('{catalog}','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{context}','{calc}'::jsonb);" if mode=='new' else f"SELECT security.replay_gas_operations('{catalog}','{encoded}'::jsonb,'2021-01-15T08:00:00Z');" if mode=='alias' else f"SELECT security.gas_operations_status('{catalog}','{nomination['operationId'] if mode=='status' else uuid4()}',NULL);")
                def execute():
                    try:query(f"\\set VERBOSITY verbose\nSET application_name='{app}';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{operation}COMMIT;")
                    except RuntimeError as error:return str(error)
                    raise AssertionError('Expired gas authority returned after storage wait: '+mode)
                waiter=pool.submit(execute);deadline=time.monotonic()+3
                while query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{app}' AND NOT l.granted AND l.relation='trading.{table}'::regclass;")!='1':
                    assert time.monotonic()<deadline and not waiter.done();time.sleep(.02)
                holder.result(timeout=12);error=waiter.result(timeout=4);assert '42501' in error,error
            assert counts()==before,'Expired operation leaked a fact, pair, receipt or publication'
        finally:
            encoded_saved=saved.replace("'","''")
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{role}';DELETE FROM security.roles WHERE id='{role}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded_saved}'::jsonb) ON CONFLICT DO NOTHING;COMMIT;")
    print('PASS gas operations: current authority after observed transfer/alias/known-status/unknown-status storage waits',flush=True)
