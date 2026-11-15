"""A directly granted fee reader retains authority through its last helper."""
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from check_futures_margin_adoption import successor_grants
from gas_fee_adversarial_cases import _refused, _state, _wait


def checks(query, call, capture, manifest, loan, at):
    catalog=manifest['catalogId'];context=capture();before=_state(query,catalog)
    signature='security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)'
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    needle='PERFORM security.require_gas_owned_read_pre_risk(catalog,context,at_time,until_at);'
    assert original.count(needle)==1
    sequence='fee_read_authority_'+uuid4().hex
    query(f"CREATE SEQUENCE security.{sequence};ALTER SEQUENCE security.{sequence} OWNER TO openetrm_security_owner;")
    delayed=original.replace(needle,needle+f"IF nextval('security.{sequence}')=2 THEN PERFORM pg_sleep(4);END IF;",1)
    try:
        query(delayed)
        with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=manifest['identityIssuer']) as authority:
            statement=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"\
                f"SELECT security.read_gas_loan_fee_terms('{catalog}','{loan}','{context}','{at}');"
            with ThreadPoolExecutor(max_workers=1) as pool:
                worker=pool.submit(_refused,call,statement,'gas_owned_denied')
                _wait(query,f"SELECT is_called AND last_value=2 FROM security.{sequence};",
                    'The fee reader did not reach its final successful source helper.',worker)
                assert query("SELECT clock_timestamp()<a.granted_to AND clock_timestamp()<b.granted_from FROM security.role_grants a "
                    "JOIN security.roles ar ON ar.id=a.role_id CROSS JOIN security.role_grants b JOIN security.roles br ON br.id=b.role_id "
                    f"WHERE ar.code='{authority}_a' AND br.code='{authority}_b';")=='t','The final helper must be observed while original A is active and B is still future.'
                worker.result(timeout=10)
            assert call("SELECT security.has_permission('ReadGasOwnedHoldings');")=='t','Successor B must be independently active.'
            assert query(f"SELECT last_value FROM security.{sequence};")=='2'
    finally:
        query(original)
        query(f"DROP SEQUENCE security.{sequence};")
    assert _state(query,catalog)==before,'A refused read changed native financial or receipt state.'
    print('PASS direct native fee reader retains original A through its last successful source helper after successor B is active',flush=True)
