"""Closed physical-option read boundaries in one disposable native worker."""
import json
import time
from uuid import uuid4

from economic_worker_fixture import provision


def prepare(query,manifest,login):
    provision(query,manifest,login,permissions=())
    reader=str(uuid4())
    query(f"""WITH role AS(INSERT INTO security.roles VALUES('{reader}','synthetic_option_source_reader','Synthetic read-only option source reader') RETURNING id),
      permissions AS(INSERT INTO security.role_permissions SELECT id,permission FROM role CROSS JOIN(VALUES('ReadTrades'),('RunScenario'))p(permission) RETURNING role_id)
      INSERT INTO security.role_grants SELECT gen_random_uuid(),b.principal_id,r.id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false
      FROM role r JOIN security.identity_bindings b ON b.issuer='{manifest['identityIssuer']}' AND b.subject='viewer';""")
    role=str(uuid4())
    return query(f"""WITH role AS(INSERT INTO security.roles VALUES('{role}','synthetic_option_worker_gas','Synthetic finite worker Gas reader') RETURNING id),
      permission AS(INSERT INTO security.role_permissions SELECT id,'ReadGasOwnedHoldings' FROM role RETURNING role_id),
      clocks AS MATERIALIZED(SELECT clock_timestamp() AS at), worker AS MATERIALIZED(SELECT principal_id FROM risk.workers WHERE login='{login}'),
      grants AS(INSERT INTO security.role_grants SELECT gen_random_uuid(),worker.principal_id,permission.role_id,'{manifest['tenantId']}'::uuid,'{manifest['workspaceId']}'::uuid,
        clocks.at,clocks.at+interval '60 seconds',false FROM permission CROSS JOIN worker CROSS JOIN clocks
        UNION ALL SELECT gen_random_uuid(),worker.principal_id,permission.role_id,'{manifest['tenantId']}'::uuid,'{manifest['workspaceId']}'::uuid,
        clocks.at+interval '60.2 seconds',TIMESTAMPTZ '9999-12-31',false FROM permission CROSS JOIN worker CROSS JOIN clocks RETURNING granted_to)
      SELECT min(granted_to) FROM grants;""")


def check(query,manifest,login,packet,foreign,original_until):
    receipts=json.loads(packet.with_suffix('.economic-worker.json').read_text())
    selected=json.loads(packet.read_text())
    catalog=manifest['catalogId'];context=selected['contextId'];at=selected['at'];day=at[:10]
    signature='lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz)'
    assert query(f"SELECT has_function_privilege('{login}','{signature}','EXECUTE') AND NOT has_function_privilege('{login}','lifecycle.require_option_origin(uuid,uuid,integer,timestamptz)','EXECUTE');")=='t'
    def invocation(trade=selected['tradeId'],binding=selected['bindingId'],revision=1,source='source_until',history='history_until'):
        return f"lifecycle.require_option_history_read('{catalog}','{trade}','{binding}',{revision},'{context}','{at}','{day}',{source},{history})"
    def refuses(call):
        return f"BEGIN PERFORM {call};RAISE EXCEPTION 'synthetic_read_was_accepted';EXCEPTION WHEN insufficient_privilege THEN NULL;END;"
    engine=receipts['engineVersion']
    setup=f"""claimed=risk.claim(30,'{engine}');IF claimed IS NULL THEN RAISE EXCEPTION 'synthetic_job_missing';END IF;
      PERFORM risk.bind((claimed->>'jobId')::uuid,(claimed->>'leaseToken')::uuid);
      source_until=security.admit_trading_business_source('{catalog}','{context}','{at}','{day}');
      history_until=source_until;"""
    declaration='DECLARE claimed jsonb;source_until timestamptz;history_until timestamptz;captured jsonb;BEGIN '
    query(f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe${declaration}"
          +refuses(invocation())+setup+f"PERFORM {invocation()};"
          +refuses(invocation(foreign['tradeId'],foreign['optionInstrumentId']))
          +refuses(invocation(revision=2))
          +refuses(invocation(source="clock_timestamp()-interval '1 second'"))
          +refuses(invocation(history="clock_timestamp()-interval '1 second'"))
          +"END$probe$;ROLLBACK;")

    # A shorter native history cutoff can expire during the final source
    # verification. Delay only that read guard, restoring it even on failure.
    source_signature='security.require_admitted_business_source(uuid,uuid,timestamptz,date,timestamptz)'
    definition=query(f"SELECT pg_get_functiondef('{source_signature}'::regprocedure);")
    end=definition.rfind('END')
    assert end>0 and 'LANGUAGE plpgsql' in definition
    delayed=definition[:end]+"IF pg_catalog.nextval('pg_temp.synthetic_source_calls'::regclass)=3 THEN PERFORM pg_catalog.pg_sleep(1);END IF;"+definition[end:]
    boundary=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    minimum=" IF source_until IS NULL OR history_until IS NULL OR NOT isfinite(source_until) OR NOT isfinite(history_until)\n  OR clock_timestamp()>=least(source_until,history_until)\n THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;"
    assert boundary.count(minimum)==1
    expiry_probe=(f"CREATE TEMP SEQUENCE synthetic_source_calls;GRANT USAGE ON SEQUENCE synthetic_source_calls TO openetrm_security_owner;"
                  +f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe${declaration}"+setup
                  +"history_until=clock_timestamp()+interval '0.8 seconds';"
                  +refuses(invocation())+"END$probe$;ROLLBACK;")
    try:
        query(delayed)
        query(boundary.replace(minimum,''))
        try:
            query(expiry_probe)
            raise AssertionError('The missing final-minimum counterexample did not reach its expected acceptance.')
        except RuntimeError as error:
            assert 'synthetic_read_was_accepted' in str(error),str(error)
        query(boundary)
        query(expiry_probe)
    finally:
        query(boundary)
        query(definition)

    remaining=float(query(f"SELECT extract(epoch FROM '{original_until}'::timestamptz-clock_timestamp());"))
    assert remaining>2,'The initial grant must still be live before the original-deadline probe binds.'
    # Natural successor activation changes no security generation and cannot
    # extend an already-bound attempt. Each wait stays short and bounded.
    while remaining>1:
        time.sleep(min(remaining-1,10))
        remaining=float(query(f"SELECT extract(epoch FROM '{original_until}'::timestamptz-clock_timestamp());"))
    query(f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe${declaration}"+setup
          +"captured=security.capture_physical_valuation_read();IF(captured->>'allowed')::boolean IS NOT TRUE THEN RAISE EXCEPTION 'synthetic_original_gas_missing';END IF;"
          +"PERFORM pg_sleep(2);"
          +refuses("security.require_physical_valuation_read((captured->>'authorityUntil')::timestamptz)")
          +refuses(invocation())+"END$probe$;ROLLBACK;")
    assert query(f"SELECT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,'ReadGasOwnedHoldings',clock_timestamp()) FROM risk.workers w WHERE login='{login}';")=='t'
    print('PASS native physical option read admission: exact worker option scope, unchanged private-origin ACL, separate original source/history cutoffs and no successor-grant renewal',flush=True)
