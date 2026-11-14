"""Rollback-only fault probes for the ordinary correction's native boundary."""
def upgrade_checks(query,migration):
    if migration.name!='0301_gas_owned_quantity_reconciliation.sql':return
    from assistant_context import migration_body
    signature='trading.check_gas_quantity_revision()'
    changes=[
        f"ALTER FUNCTION {signature} SECURITY INVOKER;",
        f"ALTER FUNCTION {signature} STABLE;",
        f"ALTER FUNCTION {signature} PARALLEL SAFE;",
        f"ALTER FUNCTION {signature} STRICT;",
        f"ALTER FUNCTION {signature} SET work_mem='64kB';",
        f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;",
        f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proargmodes=ARRAY['i']::\"char\"[] WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proallargtypes=ARRAY['uuid'::regtype::oid] WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET proargnames=ARRAY['changed'] WHERE oid='{signature}'::regprocedure;",
        f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{signature}'::regprocedure;",
        f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic source drift */ ');END$$;"]
    body=migration_body(migration.read_text())
    for change in changes:
        try:query('BEGIN;'+change+body+'ROLLBACK;')
        except RuntimeError as error:assert 'gas_owned_quantity_upgrade' in str(error),str(error)
        else:raise AssertionError('Ordinary correction migration accepted changed native quantity closure metadata')
    query('BEGIN;SET LOCAL search_path=security,public;'+body+'ROLLBACK;')
    print('PASS ordinary correction upgrade: exact original quantity-seal body, owner/privileges, function shape and caller search path',flush=True)


def checks(query,call,command,catalog):
    # The accepted financial rows remain untouched. These test-only command
    # faults occur after the producer derived its result and before the
    # independent deferred seal reads the stored first-outcome receipt.
    faults={
        'missing_positions':"NEW.request=NEW.request#-ARRAY['body','positions'];",
        'null_consumptions':"NEW.request=jsonb_set(NEW.request,ARRAY['body','consumptions'],'null'::jsonb);",
        'reported_quantity':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['positions','0','currentQuantity'],'\"100\"'::jsonb);",
        'reported_revision':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['positions','0','currentRevision'],'100'::jsonb);",
        'reported_source_allocation':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['sourceAllocations','0','allocated'],'\"0\"'::jsonb);",
        'missing_sources':"NEW.outcome=NEW.outcome-'sourceAllocations';",
        'false_acknowledgement':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['semanticAcknowledgement'],'true'::jsonb);"}
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    missing=[]
    try:
        for name,change in faults.items():
            query("CREATE OR REPLACE FUNCTION trading.fixture_owned_correction_fault() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN "
                "IF NEW.request->>'action'='owned_quantity_reconciliation' THEN "+change+"END IF;RETURN NEW;END$$;")
            query("CREATE TRIGGER fixture_owned_correction_fault BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.fixture_owned_correction_fault();")
            try:
                for ending in ('immediate','commit'):
                    # Immediate probes roll back even on an unexpected pass.
                    # A COMMIT probe exercises the actual deferred boundary;
                    # unexpected acceptance stops this disposable fixture.
                    try:call(command+("SET CONSTRAINTS ALL IMMEDIATE;" if ending=='immediate' else ''),rollback=ending=='immediate')
                    except RuntimeError as error:assert 'gas_owned_quantity_seal' in str(error),str(error)
                    else:
                        missing.append(name+':'+ending)
                        if ending=='commit':raise AssertionError('Malformed correction committed in owned fixture: '+name)
            finally:query('DROP TRIGGER fixture_owned_correction_fault ON trading.gas_owned_commands;')
    finally:query('DROP FUNCTION IF EXISTS trading.fixture_owned_correction_fault();')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    assert not missing,'Ordinary correction accepted malformed persisted financial receipt: '+','.join(missing)
    print('PASS ordinary correction seal: malformed stored request, quantities/revisions, allocation and acknowledgement refuse at immediate constraints and COMMIT without financial publication',flush=True)
