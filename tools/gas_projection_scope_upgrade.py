"""The fixed scope reader depends only on its exact admitted predecessors."""


def checks(query,migration):
    if migration.name!='0312_gas_projection_scope_admission.sql':return
    body=migration.read_text().strip().replace('BEGIN;','',1).removesuffix('COMMIT;')
    signatures=['settlement_data.futures_margin_preview_authority(uuid)',
        'security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)']
    probes=[]
    for signature in signatures:
        probes.extend([
            (f"ALTER FUNCTION {signature} COST 1;",signature),
            (f"ALTER FUNCTION {signature} SET work_mem='64kB';",signature),
            (f"ALTER FUNCTION {signature} PARALLEL SAFE;",signature),
            (f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",signature),
            (f"REVOKE EXECUTE ON FUNCTION {signature} FROM openetrm_risk_worker;",signature),
            (f"UPDATE pg_proc SET prorows=1 WHERE oid='{signature}'::regprocedure;",signature),
            (f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[pronargs]) WHERE oid='{signature}'::regprocedure;",signature),
            (f"UPDATE pg_proc SET proargnames=array_fill('changed_argument'::text,ARRAY[pronargs]) WHERE oid='{signature}'::regprocedure;",signature),
            (f"UPDATE pg_proc SET prosrc=prosrc||E'\\n-- Synthetic changed source' WHERE oid='{signature}'::regprocedure;",signature),
        ])
    for change,signature in probes:
        try:query(f'BEGIN;{change}{body}ROLLBACK;')
        except RuntimeError as error:assert 'gas_projection_scope_upgrade: '+signature in str(error),str(error)
        else:raise AssertionError('Gas scope admission accepted changed predecessor execution identity: '+signature)
    shadows=["CREATE FUNCTION security.read_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean) RETURNS jsonb LANGUAGE sql AS $$SELECT '{}'::jsonb$$;",
        "CREATE FUNCTION security.require_gas_owned_projection_scope_admission(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) RETURNS boolean LANGUAGE sql AS $$SELECT false$$;"]
    for shadow in shadows:
        try:query('BEGIN;'+shadow+body+'ROLLBACK;')
        except RuntimeError as error:assert 'gas_projection_scope_upgrade: existing entry point' in str(error),str(error)
        else:raise AssertionError('Gas scope admission replaced a pre-existing entry point.')
    query('BEGIN;SET LOCAL search_path=security,public;'+body+'ROLLBACK;')
    print(f'PASS gas scope admission exact predecessor guards ({len(probes)} drift refusals), entry-point absence and caller search path',flush=True)
