"""Fresh market-row authority checks in the launcher's owned synthetic database."""
import json
from uuid import UUID


TABLES = ('constituent_mappings','contract_intervals','contracts','curve_actual_rules','curve_blocks','curve_inputs','curve_members','curves',
          'observations','rate_curve_points','rate_curves','revision_periods','revisions','series','shaped_curve_blocks','shaped_curves')


def run_checks(query, manifest):
    catalog, revision, tenant, workspace = [str(UUID(manifest[key])) for key in
        ['catalogId','revisionId','tenantId','workspaceId']]
    passed = 0
    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS market authority: '+name, flush=True)
    def issue(profile='viewer', seconds=60):
        return str(UUID(query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{seconds});")))
    def runtime(statement, selected=None, profile='viewer', seconds=60, prefix=''):
        bind = '' if selected is None else f"SELECT security.use_market_context('{catalog}','{selected}');"
        return 'BEGIN;'+prefix+f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue(profile,seconds)}');"+bind+statement+'ROLLBACK;'
    context = str(UUID(query(runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);").removesuffix('ROLLBACK;')+'COMMIT;')))
    selected = json.loads(query(f"SELECT json_agg(id ORDER BY id) FROM(SELECT id FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{revision}' ORDER BY id LIMIT 30)q;"))
    assert len(selected)==30
    selected_ids='ARRAY['+','.join("'"+str(UUID(value))+"'::uuid" for value in selected)+']'
    count = f"SELECT count(*) FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND id=ANY({selected_ids});"

    # Measure the actual runtime RLS scan, not a replacement helper. Transaction
    # counters exclude context admission and prove one fresh access call for
    # each mandatory visibility call, without a wall-clock performance threshold.
    stats = "SELECT 'CALLS|'||coalesce(sum(calls) FILTER(WHERE funcname='market_access'),0)||'|'||coalesce(sum(calls) FILTER(WHERE funcname='market_fact_visible'),0) FROM pg_stat_xact_user_functions WHERE schemaname='security';"
    measured = count.replace('SELECT count(*)',"SELECT 'ROWS|'||count(*)")
    output = query("SET track_functions='all';"+runtime(stats+measured+stats,context)).splitlines()
    calls = [list(map(int,line.split('|')[1:])) for line in output if line.startswith('CALLS|')]
    rows = int(next(line[5:] for line in output if line.startswith('ROWS|')))
    access, visibility = [end-start for start,end in zip(*calls)]
    check(f'30 retained contract rows each execute exactly one current-access and visibility check (rows={rows}, access={access}, visibility={visibility})',
          rows == access == visibility == 30)

    tables = ','.join("'"+name+"'" for name in TABLES)
    check('sixteen read policies retain the exact restrictive temporal barrier and FORCE RLS',query(f"""
      SELECT count(*)=16 AND bool_and(p.permissive='PERMISSIVE' AND p.roles=ARRAY['openetrm_runtime']::name[]
       AND p.cmd='SELECT' AND p.qual='true' AND p.with_check IS NULL
       AND t.permissive='RESTRICTIVE' AND t.roles=p.roles AND t.cmd='SELECT' AND t.with_check IS NULL
       AND t.qual=CASE p.tablename
        WHEN 'revisions' THEN 'security.market_fact_visible(catalog_id, id, NULL::uuid)'
        WHEN 'observations' THEN 'security.market_fact_visible(catalog_id, definition_revision_id, id)'
        ELSE 'security.market_fact_visible(catalog_id, revision_id, NULL::uuid)' END
       AND c.relrowsecurity AND c.relforcerowsecurity)
      FROM pg_policies p JOIN pg_policies t USING(schemaname,tablename)
      JOIN pg_class c ON c.oid=('market_data.'||quote_ident(p.tablename))::regclass
      WHERE p.schemaname='market_data' AND p.tablename IN({tables})
       AND p.policyname='scoped_read' AND t.policyname='temporal_read';""")=='t')
    check('catalog-only access remains independently scoped',query("SELECT qual='security.market_access(id, ''ReadMarketData''::text)' FROM pg_policies WHERE schemaname='market_data' AND tablename='catalogs' AND policyname='scoped_read';")=='t')
    check('current working rows remain authorized',query(runtime(count))=='30')
    check('the exact retained bundle remains authorized',query(runtime(count,context))=='30')
    total = 'SELECT sum(n) FROM('+' UNION ALL '.join('SELECT count(*) n FROM market_data.'+name for name in TABLES)+')q;'
    check('a runtime login without bound identity has no market rows',query('SET SESSION AUTHORIZATION preview_runtime;'+total)=='0')
    pooled = query(runtime(count,context).removesuffix('ROLLBACK;')+'COMMIT;SET SESSION AUTHORIZATION preview_runtime;BEGIN;'+count+'COMMIT;').splitlines()
    check('a pooled connection cannot carry authority into another transaction',pooled==['30','0'])
    outside = str(UUID(query(runtime(f"SELECT security.capture_date_context('{catalog}','2019-01-01',NULL);").removesuffix('ROLLBACK;')+'COMMIT;')))
    check('unavailable historical rows never fall back to current data',query(runtime(total,outside))=='0')
    allow = ''.join('CREATE POLICY synthetic_market_allow ON market_data.'+name+' FOR SELECT TO openetrm_runtime USING(true);' for name in TABLES)
    check('an extra permissive policy cannot bypass missing identity',query('BEGIN;'+allow+'SET LOCAL SESSION AUTHORIZATION preview_runtime;'+total+'ROLLBACK;')=='0')
    check('an extra permissive policy cannot bypass historical visibility',query(runtime(total,outside,prefix=allow))=='0')
    viewer = "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer')"
    for edit, name in [(f"DELETE FROM security.market_catalog_scopes WHERE catalog_id='{catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';",'market scope'),
                       (f'UPDATE security.principals SET enabled=false WHERE id={viewer};','enabled principal'),
                       (f'UPDATE security.role_grants SET revoked=true WHERE principal_id={viewer};','role grant')]:
        statement='SET SESSION AUTHORIZATION preview_owner;'+edit+'SET SESSION AUTHORIZATION preview_runtime;'+total
        check('retained reads recheck the current '+name,query(runtime(statement,context))=='0')

    locked=f"SELECT count(*) FROM(SELECT id FROM market_data.revisions WHERE catalog_id='{catalog}' AND id='{revision}' FOR UPDATE)q;"
    changed=f"WITH changed AS(UPDATE market_data.revisions SET generation=generation WHERE catalog_id='{catalog}' AND id='{revision}' RETURNING id)SELECT count(*) FROM changed;"
    check('an authorized editor can lock its exact definition revision',query(runtime(locked,profile='editor'))=='1')
    withdraw="DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='market_data_editor') AND permission='ReadMarketData';"
    for statement,name in [(locked,'SELECT FOR UPDATE'),(changed,'UPDATE RETURNING')]:
        sql='SET SESSION AUTHORIZATION preview_owner;'+withdraw+"SET SESSION AUTHORIZATION preview_runtime;SELECT security.has_permission('WriteMarketDefinitions');"+statement
        check(name+' cannot use write authority to bypass missing read authority',query(runtime(sql,profile='editor')).splitlines()==['t','0'])

    ids=json.loads(query(f"SELECT json_agg(id ORDER BY id) FROM(SELECT id FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{revision}' ORDER BY id LIMIT 2)q;"))
    assert len(ids)==2
    delayed=f"""CREATE FUNCTION pg_temp.read_market_after_pause(n integer) RETURNS boolean LANGUAGE plpgsql VOLATILE AS $$BEGIN
      IF n=2 THEN PERFORM pg_sleep(2.1);END IF;
      RETURN EXISTS(SELECT 1 FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND id=(ARRAY['{ids[0]}'::uuid,'{ids[1]}'::uuid])[n]);END$$;
      SELECT array_agg(pg_temp.read_market_after_pause(n) ORDER BY n) FROM generate_series(1,2)n;"""
    for mode in ['force_custom_plan','force_generic_plan']:
        for selected,name in [(None,'current'),(context,'retained')]:
            result=query(runtime(f'SET LOCAL plan_cache_mode={mode};'+delayed,selected,seconds=2))
            check(name+' real RLS rechecks token expiry within one statement under '+mode,result=='{t,f}')
    print(f'{passed} market authority checks passed.',flush=True)
