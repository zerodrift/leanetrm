"""Migration 0252 against its reviewed predecessors and a populated database, inside the owned preview only.

The drift checks run in every owned preview. The populated-database qualification loads the preview's own fixtures in
one transaction, so it runs only under the contract observation curve check, whose fixtures it was written for."""
from uuid import uuid4
from assistant_context import check_guard, migration_body


def run_checks(query, migration):
    text = migration.read_text()
    signatures = ('market_data.guard_panel_weight()', 'market_data.guard_panel_publication()')
    changes = tuple((change, signature) for signature in signatures for change in (
        f"CREATE OR REPLACE FUNCTION {signature} RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $changed$BEGIN RETURN NEW;END$changed$;",
        f"ALTER FUNCTION {signature} OWNER TO openetrm_security_owner;",
        f"ALTER FUNCTION {signature} SET work_mem='64kB';",
        f"REVOKE EXECUTE ON FUNCTION {signature} FROM PUBLIC;"))
    check_guard(query, text, '0252', 'contract_observation_upgrade', changes)
    try:
        query('BEGIN;ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;'
              'ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind<>\'mean\');' + migration_body(text) + 'ROLLBACK;')
    except RuntimeError as error:
        assert 'contract_observation_upgrade: unexpected node kinds' in str(error), error
    else:
        raise AssertionError('Migration 0252 admitted an unreviewed node-kind constraint')
    print('PASS migration 0252 refuses panel guard body, owner, configuration, ACL and node-constraint drift', flush=True)


def populated_checks(query, migration, retained, manifest):
    text = migration.read_text()
    # A populated database from the prior schema: the fixtures and one published computed-curve combination, then a
    # draft successor on which each panel guard is probed before and after the migration. Everything is rolled back.
    catalog, curve = manifest['catalogId'], str(uuid4())
    loaded = '\n'.join(migration_body(sql) for sql in retained)
    def graph(version, panel_kind, panel_parameters, weighted):
        return f"""
        INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
         VALUES('{catalog}','{curve}',{version},1,'draft','Synthetic retained combination','output','custom_curve_recipe_v2','{{}}');
        INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{curve}',{version},'panel','{panel_kind}','{panel_parameters}'),
         ('{catalog}','{curve}',{version},'combined','weighted_panels','{{"aggregation":"signed_sum_v1","alignment":"exact_delivery_intervals_v1"}}'),
         ('{catalog}','{curve}',{version},'output','output','{{}}');
        INSERT INTO market_data.custom_curve_edges VALUES('{catalog}','{curve}',{version},'combined','formula','panel'),
         ('{catalog}','{curve}',{version},'output','values','combined');
        {f"INSERT INTO market_data.custom_curve_panel_weights VALUES('{catalog}','{curve}',{version},'combined','formula','panel',2);" if weighted else ''}"""
    seed = (f"INSERT INTO market_data.custom_curves VALUES('{catalog}','{curve}','synthetic_retained_combination');"
            + graph(1, 'mean', '{"axis":"dates"}', True)
            + f"""UPDATE market_data.custom_curve_versions SET state='published',publication_order=1,published_at=clock_timestamp(),
             publication_xid=pg_current_xact_id() WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',1);"""
            # The successor's panel is a source, which no computed-curve combination accepts.
            + graph(2, 'source', '{}', False))
    retained_rows = f"""(SELECT jsonb_build_object(
        'versions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY v.version) FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version)=('{catalog}','{curve}',1)),
        'nodes',(SELECT jsonb_agg(to_jsonb(n) ORDER BY n.node_id) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=('{catalog}','{curve}',1)),
        'edges',(SELECT jsonb_agg(to_jsonb(e) ORDER BY e.node_id,e.port) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version)=('{catalog}','{curve}',1)),
        'weights',(SELECT jsonb_agg(to_jsonb(w) ORDER BY w.node_id,w.source_port) FROM market_data.custom_curve_panel_weights w WHERE(w.catalog_id,w.curve_id,w.version)=('{catalog}','{curve}',1))))"""
    # Each guard refuses the successor as it did: a weight on a source input, and publication with that input unweighted.
    probes = f"""
         BEGIN
          INSERT INTO market_data.custom_curve_panel_weights VALUES('{catalog}','{curve}',2,'combined','formula','panel',1);
          RAISE EXCEPTION 'The panel weight guard admitted a source input';
         EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'recipe_panel_weights' THEN RAISE;END IF;
         END;
         BEGIN
          UPDATE market_data.custom_curve_versions SET state='published',publication_order=2,published_at=clock_timestamp(),publication_xid=pg_current_xact_id()
           WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',2);
          RAISE EXCEPTION 'The panel publication guard admitted an unweighted source input';
         EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'recipe_panel_weights' THEN RAISE;END IF;
         END;"""
    assert '$upgrade_migration$' not in text
    query(f"""BEGIN;{loaded}{seed}
        SET CONSTRAINTS ALL IMMEDIATE;
        DO $upgrade_check$ DECLARE original jsonb; BEGIN
         IF(SELECT state FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',1))<>'published'
         THEN RAISE EXCEPTION 'The retained combination is not published';END IF;
         original={retained_rows};
         {probes}
         EXECUTE $upgrade_migration${migration_body(text)}$upgrade_migration$;
         IF {retained_rows} IS DISTINCT FROM original THEN RAISE EXCEPTION 'Migration 0252 changed a retained published combination';END IF;
         {probes}
        END $upgrade_check$;ROLLBACK;""")
    print('PASS migration 0252 over a populated database: a published computed-curve combination reads back unchanged and both panel guards refuse as before', flush=True)
