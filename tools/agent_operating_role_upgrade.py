"""0311 preserves exact qualified helper implementations and execution metadata."""


def checks(query,migration):
    if migration.name!='0311_agent_operating_roles.sql':return
    body=migration.read_text().strip().replace('BEGIN;','',1).removesuffix('COMMIT;')
    save='assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer)'
    signatures=['assistant.agent_directory()','assistant.agent_version_state(uuid,integer)','assistant.version_permissions(uuid,integer)',
        'assistant.publish_agent(uuid,uuid,integer)',save]
    probes=[]
    for signature in signatures:
        probes.extend([
            (f"ALTER FUNCTION {signature} SET work_mem='64kB';",signature),
            (f"ALTER FUNCTION {signature} COST 1;",signature),
            (f"ALTER FUNCTION {signature} PARALLEL SAFE;",signature),
            (f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_risk_worker;",signature),
            ("DO $change$DECLARE source text;BEGIN SELECT pg_get_functiondef('"+signature+"'::regprocedure) INTO source;"
                "EXECUTE replace(source,'AS $function$','AS $function$'||E'\\n-- Synthetic changed helper');END$change$;",signature),
        ])
        if signature not in ['assistant.agent_directory()']:
            probes.append((f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[pronargs]) WHERE oid='{signature}'::regprocedure;",signature))
    for change,signature in probes:
        try:query(f'BEGIN;{change}{body}ROLLBACK;')
        except RuntimeError as error:assert 'agent_operating_role_upgrade: '+signature in str(error),str(error)
        else:raise AssertionError('0311 accepted changed execution metadata or body: '+signature)
    shadow="DO $shadow$DECLARE source text;BEGIN SELECT pg_get_functiondef('"+save+"'::regprocedure) INTO source;"
    shadow+="EXECUTE replace(source,'grant_days_limit integer)','grant_days_limit integer, operating_role text)');END$shadow$;"
    extra=save[:-1]+',text)'
    try:query(f'BEGIN;{shadow}GRANT EXECUTE ON FUNCTION {extra} TO openetrm_risk_worker;{body}ROLLBACK;')
    except RuntimeError as error:assert 'agent_operating_role_upgrade: existing operating-role entry point' in str(error),str(error)
    else:raise AssertionError('0311 replaced a pre-existing operating-role entry point while preserving an unauthorized execute grant.')
    query(f'BEGIN;SET LOCAL search_path=security,assistant,public;{body}ROLLBACK;')
    print(f'PASS 0311 exact helper source/metadata/ACL guards ({len(probes)} drift refusals) and caller search path',flush=True)
