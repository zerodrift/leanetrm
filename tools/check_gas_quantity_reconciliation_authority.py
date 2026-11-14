"""Original read authority at the final native financial-quantity boundary."""
from concurrent.futures import ThreadPoolExecutor
import time


def read_check(query,manifest,holding,performance,context,at,call,*,phase='result'):
    catalog=manifest['catalogId']
    principal=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{manifest['identityIssuer']}' AND subject='trader';")
    saved=query(f"SELECT jsonb_agg(to_jsonb(g)) FROM security.role_grants g WHERE principal_id='{principal}';")
    original=query("SELECT pg_get_functiondef('security.read_gas_futures_reconciliation(uuid,uuid,uuid,uuid,timestamptz)'::regprocedure);")
    marker=' -- The complete native result retains the delegated original read admission.'
    needle=marker if marker in original else ' RETURN result;'
    if phase=='source':needle='source=settlement_data.read_futures_delivery_position(catalog,holding,context,as_of);'
    assert original.count(needle)==1
    # Inject latency after all data has been read, before its final admission.
    # No source row, result or financial fact changes.
    query("CREATE SEQUENCE security.gas_quantity_read_signal;ALTER SEQUENCE security.gas_quantity_read_signal OWNER TO openetrm_security_owner;")
    latency=" PERFORM nextval('security.gas_quantity_read_signal');PERFORM pg_sleep(2);"
    query(original.replace(needle,needle+latency if phase=='source' else latency+needle))
    refused=False
    try:
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '1.5 seconds' WHERE principal_id='{principal}';")
        with ThreadPoolExecutor(max_workers=1) as pool:
            reader=pool.submit(call,f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
                f"SELECT security.read_gas_futures_reconciliation('{catalog}','{holding}','{performance}','{context}','{at}');")
            until=time.monotonic()+1
            while query('SELECT is_called FROM security.gas_quantity_read_signal;')!='t':
                assert time.monotonic()<until and not reader.done(),'The native reader did not reach its controlled final-result wait.'
                time.sleep(.01)
            assert query(f"SELECT bool_and(granted_to>clock_timestamp()) FROM security.role_grants WHERE principal_id='{principal}';")=='t'
            try:reader.result(timeout=5)
            except RuntimeError as error:
                assert 'gas_owned_denied' in str(error),str(error);refused=True
    finally:
        encoded=saved.replace("'","''")
        query("UPDATE security.role_grants g SET granted_to=s.granted_to FROM jsonb_populate_recordset(NULL::security.role_grants,"
            f"'{encoded}'::jsonb)s WHERE g.id=s.id;")
        query(original)
        query('DROP SEQUENCE security.gas_quantity_read_signal;')
    return refused
