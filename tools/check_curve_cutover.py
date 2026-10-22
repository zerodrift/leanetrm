"""Refuse retained data before the connected cutover in an owned preview only."""
from uuid import uuid4


def run_checks(query, retained, manifest, migration, number):
    """Run the actual migration body in a subtransaction; roll all fixtures back."""
    def body(sql):
        return sql.strip().replace('BEGIN;', '', 1).removesuffix('COMMIT;')

    loaded = 'BEGIN;' + '\n'.join(body(sql) for sql in retained)
    catalog, revision, tenant, workspace = (manifest[key] for key in
                                           ('catalogId', 'revisionId', 'tenantId', 'workspaceId'))
    if number == '0080':
        table = 'market_data.custom_curve_versions'
        expected = 'recipe_cutover_requires_empty_saved_recipes: explicitly reset/rebook before installing this baseline'
        cases = []
        for state in ('draft', 'published'):
            curve = str(uuid4())
            seed = f"""
            INSERT INTO market_data.custom_curves VALUES('{catalog}','{curve}','synthetic_retained_recipe');
            INSERT INTO market_data.custom_curve_versions
             (catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
             VALUES('{catalog}','{curve}',1,1,'draft','Synthetic retained recipe','result','custom_curve_recipe_v1','{{}}');
            INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{curve}',1,'result','output','{{}}');
            """
            if state == 'published':
                seed += f"""UPDATE market_data.custom_curve_versions SET state='published',publication_order=1,
                 published_at=clock_timestamp(),publication_xid=pg_current_xact_id() WHERE curve_id='{curve}';"""
            cases.append((state, seed))
    elif number == '0081':
        table = 'risk.jobs'
        expected = 'risk_cutover_requires_empty_retained_jobs: explicitly archive/reset before installing this baseline'
        cases = []
        for state in ('queued', 'completed'):
            context = str(uuid4())
            # Storage fixtures exercise the installation boundary, not execution
            # or admission of these deliberately non-executable requests.
            seed = f"""
            INSERT INTO market_data.temporal_contexts VALUES
             ('{catalog}','{context}','2020-12-18','2020-12-18T16:00:00Z',0,'2020-12-18T16:00:00Z','2020-12-18T16:00:00Z');
            INSERT INTO market_data.market_contexts VALUES('{catalog}','{context}',0);
            INSERT INTO trading.contexts VALUES('{catalog}','{context}',0);
            INSERT INTO risk.jobs(id,catalog_id,request_id,actor_id,tenant_id,workspace_id,context_id,
             revision_id,valuation_at,request,frame,security_generation,created_at,state,attempt,finished_at,result)
             SELECT '{uuid4()}','{catalog}','{uuid4()}',principal_id,'{tenant}','{workspace}','{context}',
              '{revision}','2020-12-18T16:00:00Z','{{}}','{{}}',1,clock_timestamp(),'{state}',0,
              {'clock_timestamp()' if state == 'completed' else 'NULL'},
              {"'{}'::jsonb" if state == 'completed' else 'NULL'}
             FROM security.workspace_memberships WHERE tenant_id='{tenant}' AND workspace_id='{workspace}' AND login_handle='risk';
            """
            cases.append((state, seed))
    else:
        raise ValueError('Unsupported connected-curve cutover migration')

    # Compare retained rows, table/constraint inventory and relevant function
    # definitions/privileges after the caught refusal, before fixture rollback.
    snapshot = f"""SELECT jsonb_build_object(
     'rows',(SELECT jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text) FROM {table} t),
     'tables',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.oid) FROM pg_class c
       JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN('market_data','risk')),
     'constraints',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.oid) FROM pg_constraint c
       JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname IN('market_data','risk')),
     'functions',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.oid) FROM pg_proc p
       JOIN pg_namespace n ON n.oid=p.pronamespace
       WHERE n.nspname='risk' OR n.nspname='security' AND p.proname='publish_custom_curve'))"""
    assert '$cutover_migration$' not in migration
    for state, seed in cases:
        query(loaded + seed + f"""
        SET CONSTRAINTS ALL IMMEDIATE;
        DO $cutover_check$ DECLARE original jsonb; BEGIN
         IF(SELECT count(*) FROM {table})<>1 THEN RAISE EXCEPTION 'Retained cutover fixture is missing';END IF;
         original=({snapshot});
         BEGIN
          EXECUTE $cutover_migration${body(migration)}$cutover_migration$;
          RAISE EXCEPTION 'Cutover accepted retained {state} data';
         EXCEPTION WHEN raise_exception THEN
          IF SQLERRM<>'{expected}' THEN RAISE;END IF;
         END;
         IF({snapshot}) IS DISTINCT FROM original THEN RAISE EXCEPTION 'Cutover refusal changed retained data or schema';END IF;
        END $cutover_check$;
        ROLLBACK;
        """)
        assert query(f'SELECT count(*) FROM {table};') == '0', 'Cutover fixture escaped rollback'
        print(f'PASS cutover {number}: retained {state} data refuses installation without changing rows or schema', flush=True)
