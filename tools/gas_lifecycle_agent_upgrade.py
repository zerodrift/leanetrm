"""Guarded extension of the three exact existing agent-role boundaries."""


def checks(query,migration):
    if migration.name!='0305_gas_lifecycle_agent_role.sql':return
    from assistant_context import migration_body
    body=migration_body(migration.read_text())
    signatures=('security.guard_agent_roles()','security.administration_directory()','security.apply_administration(jsonb)')
    for signature in signatures:
        oid=query(f"SELECT '{signature}'::regprocedure::oid;")
        changes=[
            f"UPDATE pg_proc SET prosrc=prosrc||E'\\n/* synthetic body drift */' WHERE oid={oid};",
            f"ALTER FUNCTION {signature} OWNER TO openetrm_runtime;",
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",
            f"ALTER FUNCTION {signature} STABLE;",
            f"ALTER FUNCTION {signature} PARALLEL SAFE;",
            f"ALTER FUNCTION {signature} STRICT;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"UPDATE pg_proc SET prosecdef=NOT prosecdef WHERE oid={oid};",
            f"UPDATE pg_proc SET proretset=true WHERE oid={oid};",
            f"UPDATE pg_proc SET provariadic='jsonb'::regtype WHERE oid={oid};",
            f"UPDATE pg_proc SET proargmodes=ARRAY['i']::\"char\"[] WHERE oid={oid};",
            f"UPDATE pg_proc SET proallargtypes=ARRAY['jsonb'::regtype::oid] WHERE oid={oid};",
            f"UPDATE pg_proc SET proargnames=ARRAY['changed'] WHERE oid={oid};",
            f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid={oid};",
            f"ALTER FUNCTION {signature} COST 200;",
        ]
        for change in changes:
            try:query('BEGIN;'+change+body+'ROLLBACK;')
            except RuntimeError as error:assert 'gas_agent_role_upgrade' in str(error),str(error)
            else:raise AssertionError('The agent role migration accepted changed native boundary metadata: '+signature)
    query('BEGIN;SET LOCAL search_path=security,public;'+body+'ROLLBACK;')
    print('PASS opt-in gas agent upgrade: exact predecessor bodies, public OIDs, role/admin shape, owner, privileges and caller search path',flush=True)
