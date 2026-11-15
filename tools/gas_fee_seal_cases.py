"""A native fee receipt must prove its result independently of the producer."""
from uuid import uuid4


def checks(query,call,command,body,capture):
    original=query("SELECT pg_get_functiondef('security.amend_gas_loan_fee_terms(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure);")
    needle='outcome=trading.gas_loan_fee_outcome(catalog,operation);'
    assert original.count(needle)==1
    try:
        query(original.replace(needle,needle+"outcome=jsonb_set(outcome,'{currentAmount}','\"-999\"');",1))
        for immediate in (True,False):
            try:call(command(body,str(uuid4()),capture())+('SET CONSTRAINTS ALL IMMEDIATE;' if immediate else ''),rollback=immediate)
            except RuntimeError as error:assert 'gas_fee_terms_seal' in str(error),str(error)
            else:raise AssertionError('The native fee acknowledgement accepted an unproved first outcome at '+('immediate validation' if immediate else 'COMMIT')+'.')
    finally:query(original)
    print('PASS fixed loan fee native seal rejects a producer-corrupted semantic acknowledgement',flush=True)
