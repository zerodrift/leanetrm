"""Original gas authority must survive work inside the final deferred loan seal."""
from check_futures_margin_adoption import successor_grants


def checks(query,call,command,manifest):
    catalog=manifest['catalogId'];signature='trading.gas_loan_correction_outcome(uuid,uuid)'
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    needle='RETURN jsonb_build_object(\'operationId\',operation'
    assert original.count(needle)==1
    changed=original.replace('through_publication integer;BEGIN','through_publication integer;fixture_result jsonb;BEGIN')
    assert changed!=original
    changed=changed.replace(needle,"fixture_result=jsonb_build_object('operationId',operation")
    tail="'stockId',loan.received_lot_id,'returnId',loan.return_position_id,'feeDisposition',body->>'feeDisposition','consumptions',uses,'sourceAllocations',facts);"
    assert changed.count(tail)==1
    changed=changed.replace(tail,tail+"\n IF nextval('trading.fixture_loan_seal_calls')=2 THEN PERFORM pg_sleep(4);END IF;RETURN fixture_result;")
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    query('CREATE SEQUENCE trading.fixture_loan_seal_calls;ALTER SEQUENCE trading.fixture_loan_seal_calls OWNER TO openetrm_security_owner;'
        'REVOKE ALL ON SEQUENCE trading.fixture_loan_seal_calls FROM PUBLIC;')
    try:
        query(changed)
        with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=manifest['identityIssuer']):
            try:call(command)
            except RuntimeError as error:assert 'gas_owned_denied' in str(error),str(error)
            else:raise AssertionError('Loan correction committed after its original gas authority expired inside the last deferred seal.')
        assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    finally:
        query(original)
        query('DROP SEQUENCE trading.fixture_loan_seal_calls;')
    print('PASS loan final deferred seal rechecks its original gas authority after complete outcome work despite an active successor',flush=True)
