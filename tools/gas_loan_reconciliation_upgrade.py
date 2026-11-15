"""Qualified old function identities are required before changing loan sources."""


def checks(query,migration):
    if migration.name!='0307_gas_loan_quantity_reconciliation.sql':return
    from assistant_context import migration_body
    body=migration_body(migration.read_text())
    signature='trading.check_gas_owned_integrity()'
    changes=[
        f'ALTER FUNCTION {signature} SECURITY INVOKER;',
        f'ALTER FUNCTION {signature} STABLE;',
        f'ALTER FUNCTION {signature} PARALLEL SAFE;',
        f'ALTER FUNCTION {signature} STRICT;',
        f"ALTER FUNCTION {signature} SET work_mem='64kB';",
        f'ALTER FUNCTION {signature} OWNER TO openetrm_runtime;',
        f'GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;',
        f'GRANT EXECUTE ON FUNCTION {signature} TO openetrm_risk_worker;',
        f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',
        f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proargmodes=ARRAY['i']::\"char\"[] WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proallargtypes=ARRAY['uuid'::regtype::oid] WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proargnames=ARRAY['changed'] WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proretset=true WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET prorettype='jsonb'::regtype WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proleakproof=true WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET prolang=(SELECT oid FROM pg_language WHERE lanname='sql') WHERE oid='{signature}'::regprocedure;"]
    signatures=[signature,'trading.check_gas_quantity_revision()',
        'security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid)',
        'security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamptz,uuid[])',
        'security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[])',
        'trading.apply_gas_owned_financial(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer)',
        'trading.require_gas_owned_quantity_head(uuid,uuid,uuid,timestamptz)']
    changes += ["DO $$DECLARE d text=pg_get_functiondef('"+name+"'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* Synthetic source drift */ ');END$$;" for name in signatures]
    changes += [f'ALTER FUNCTION {name} COST 1;' for name in signatures]
    changes += [f"UPDATE pg_proc SET prorows=1 WHERE oid='{name}'::regprocedure;" for name in signatures]
    for change in changes:
        try:query('BEGIN;'+change+body+'ROLLBACK;')
        except RuntimeError as error:assert 'gas_loan_quantity_upgrade' in str(error),str(error)
        else:raise AssertionError('Loan quantity migration accepted changed native source/integrity identity or authority metadata')
    query('BEGIN;SET LOCAL search_path=security,public;'+body+'ROLLBACK;')
    print('PASS loan quantity upgrade: all seven exact predecessor bodies, function shape, owner/ACL, original source helpers and caller search path',flush=True)
