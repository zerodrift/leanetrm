"""Scope commit admission rechecks expiry after its last native source work."""
from check_futures_margin_adoption import successor_grants


def checks(query,manifest,verify):
    signature='security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)'
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    needle='RETURN security.require_gas_owned_projection_scope_pre_risk(catalog,context,at_time,books,configured,until_at);'
    assert original.count(needle)==1
    changed=original.replace('job risk.jobs;BEGIN','job risk.jobs;fixture_result boolean;BEGIN')
    assert changed!=original
    # The new managed boundary also rechecks admission at its own return; the
    # original has initial read + final commit. Delay only the final callback.
    delay_at=3 if query("SELECT to_regprocedure('security.read_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean)') IS NOT NULL;")=='t' else 2
    changed=changed.replace(needle,
        'fixture_result=security.require_gas_owned_projection_scope_pre_risk(catalog,context,at_time,books,configured,until_at);'
        f"IF nextval('trading.fixture_scope_callback_calls')={delay_at} THEN PERFORM pg_sleep(4);END IF;RETURN fixture_result;")
    query('CREATE SEQUENCE trading.fixture_scope_callback_calls;ALTER SEQUENCE trading.fixture_scope_callback_calls OWNER TO openetrm_security_owner;'
        'REVOKE ALL ON SEQUENCE trading.fixture_scope_callback_calls FROM PUBLIC;')
    try:
        query(changed)
        with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=manifest['identityIssuer']):
            verify('gas_successor')
            verify('original')
    finally:
        query(original)
        query('DROP SEQUENCE trading.fixture_scope_callback_calls;')
    print('PASS final scope callback retains original expiry after native source return while a fresh successor read remains authorized',flush=True)
