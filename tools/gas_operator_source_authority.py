"""An operator source reader must retain its original deadline at final return."""
from concurrent.futures import ThreadPoolExecutor
import json
import re
import time
from uuid import uuid4


REQUIRE='security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)'


def _literal(value):
    return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def _state(query,catalog):
    tables=json.loads(query("SELECT jsonb_agg(c.relname ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
        "WHERE n.nspname='trading' AND c.relkind='r' AND c.relname LIKE 'gas\\_%' ESCAPE '\\' "
        "AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))
    assert tables and all(re.fullmatch(r'gas_[a-z0-9_]+',name) for name in tables)
    return query('SELECT jsonb_object_agg(name,digest ORDER BY name) FROM('+' UNION ALL '.join(
        "SELECT '"+table+"'::text name,(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
        f"FROM trading.{table} x WHERE x.catalog_id='{catalog}') digest" for table in [*tables,'publications','knowledge_receipts'])+')states;')


def checks(query,call,read,manifest,claim,context):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    actor=query("SELECT principal_id FROM security.identity_bindings "
        "WHERE issuer='https://preview.invalid' AND subject='trader';")
    original=query(f"SELECT pg_get_functiondef('{REQUIRE}'::regprocedure);")
    ending='PERFORM security.require_gas_owned_read_pre_risk(catalog,context,at_time,until_at);'
    assert original.count(ending)==1
    saved=json.loads(query("SELECT coalesce(jsonb_agg(to_jsonb(p)),'[]') FROM security.role_permissions p WHERE p.permission='ReadGasOwnedHoldings' "
        f"AND p.role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');"))
    assert actor and saved
    for target in (claim,str(uuid4())):
        role,grant_a,grant_b=(str(uuid4()) for _ in range(3));tag='operator_source_'+uuid4().hex
        before=_state(query,catalog)
        try:
            query(f"CREATE SEQUENCE security.{tag};ALTER SEQUENCE security.{tag} OWNER TO openetrm_security_owner;")
            # Entry is call one. The second call has already passed its original
            # clock, current permission and complete bound-source work. Delay
            # only there, so the outer reader must check its original cutoff.
            delay=f" IF nextval('security.{tag}')=2 THEN PERFORM pg_sleep(greatest(0,extract(epoch FROM(granted_from-clock_timestamp())))+0.05) "\
                f"FROM security.role_grants WHERE id='{grant_b}';END IF;"
            query(original.replace(ending,ending+delay,1))
            query(f"BEGIN;INSERT INTO security.roles VALUES('{role}','{tag}','Synthetic operator source authority');"
                "DELETE FROM security.role_permissions WHERE permission='ReadGasOwnedHoldings' "
                f"AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');"
                f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');"
                f"INSERT INTO security.role_grants VALUES('{grant_a}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '4 seconds',false),"
                f"('{grant_b}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()+interval '5 seconds','9999-12-31',false);COMMIT;")
            def refused():
                try:read(target,context)
                except RuntimeError as error:
                    assert re.search(r'ERROR:\s+(?:[A-Z0-9]{5}:\s+)?gas_owned_denied(?:\n|$)',str(error)),str(error)
                else:raise AssertionError('The operator source reader returned after its original ReadGas deadline expired inside its final helper.')
            with ThreadPoolExecutor(max_workers=1) as pool:
                worker=pool.submit(refused);deadline=time.monotonic()+3
                while query(f"SELECT is_called AND last_value=2 FROM security.{tag};")!='t':
                    if worker.done():worker.result();raise AssertionError('The reader never reached its final helper under original A.')
                    assert time.monotonic()<deadline,'The reader did not finish source work before the original authority window.'
                    time.sleep(.01)
                assert query(f"SELECT clock_timestamp()<a.granted_to AND clock_timestamp()<b.granted_from FROM security.role_grants a,security.role_grants b "
                    f"WHERE a.id='{grant_a}' AND b.id='{grant_b}';")=='t','The final helper was not observed while original A was active.'
                worker.result(timeout=10)
            assert call("SELECT security.has_permission('ReadGasOwnedHoldings');")=='t','Successor B must be independently active.'
        finally:
            query(original)
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{grant_a}','{grant_b}');DELETE FROM security.role_permissions WHERE role_id='{role}';"
                f"DELETE FROM security.roles WHERE id='{role}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,{_literal(saved)}) ON CONFLICT DO NOTHING;COMMIT;")
            query(f"DROP SEQUENCE IF EXISTS security.{tag};")
        assert _state(query,catalog)==before,'A read-only operator source probe changed financial, bank or receipt facts.'
    print('PASS original operator source deadline: exact final-helper return and NoRows paths reject expired A despite prepublished successor B, with no financial changes',flush=True)
