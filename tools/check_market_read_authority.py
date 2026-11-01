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
    # counters exclude context admission and prove the statement makes one fresh
    # visibility and authority decision for all of its rows, without a wall-clock
    # performance threshold; each row then compares the clock with that authority.
    stats = "SELECT 'CALLS|'||coalesce(sum(calls) FILTER(WHERE funcname='authority_until'),0)||'|'||coalesce(sum(calls) FILTER(WHERE funcname='visible_market_revisions'),0) FROM pg_stat_xact_user_functions WHERE schemaname='security';"
    measured = count.replace('SELECT count(*)',"SELECT 'ROWS|'||count(*)")
    output = query("SET track_functions='all';"+runtime(stats+measured+stats,context)).splitlines()
    calls = [list(map(int,line.split('|')[1:])) for line in output if line.startswith('CALLS|')]
    rows = int(next(line[5:] for line in output if line.startswith('ROWS|')))
    access, visibility = [end-start for start,end in zip(*calls)]
    check(f'30 retained contract rows share one fresh authority and visibility decision (rows={rows}, authority={access}, visibility={visibility})',
          rows == 30 and access == visibility == 1)

    tables = ','.join("'"+name+"'" for name in TABLES)
    check('sixteen read policies retain the exact restrictive temporal barrier and FORCE RLS',query(f"""
      SELECT count(*)=16 AND bool_and(p.permissive='PERMISSIVE' AND p.roles=ARRAY['openetrm_runtime']::name[]
       AND p.cmd='SELECT' AND p.qual='true' AND p.with_check IS NULL
       AND t.permissive='RESTRICTIVE' AND t.roles=p.roles AND t.cmd='SELECT' AND t.with_check IS NULL
       AND regexp_replace(t.qual,'\\s+',' ','g')=CASE p.tablename
        WHEN 'revisions' THEN 'COALESCE((((catalog_id, id) IN ( SELECT v.catalog, v.revision FROM security.visible_market_revisions() v(catalog, revision))) AND (clock_timestamp() < ( SELECT security.authority_until(''ReadMarketData''::text) AS authority_until))), false)'
        WHEN 'observations' THEN 'COALESCE((((catalog_id, definition_revision_id, id) IN ( SELECT v.catalog, v.revision, v.observation FROM security.visible_observations() v(catalog, revision, observation))) AND (clock_timestamp() < ( SELECT security.authority_until(''ReadMarketData''::text) AS authority_until))), false)'
        ELSE 'COALESCE((((catalog_id, revision_id) IN ( SELECT v.catalog, v.revision FROM security.visible_market_revisions() v(catalog, revision))) AND (clock_timestamp() < ( SELECT security.authority_until(''ReadMarketData''::text) AS authority_until))), false)' END
       AND c.relrowsecurity AND c.relforcerowsecurity)
      FROM pg_policies p JOIN pg_policies t USING(schemaname,tablename)
      JOIN pg_class c ON c.oid=('market_data.'||quote_ident(p.tablename))::regclass
      WHERE p.schemaname='market_data' AND p.tablename IN({tables})
       AND p.policyname='scoped_read' AND t.policyname='temporal_read';""")=='t')
    check('catalog-only access remains independently scoped',query("SELECT regexp_replace(qual,'\\s+',' ','g')='COALESCE(((id IN ( SELECT security.market_catalogs(''ReadMarketData''::text) AS market_catalogs)) AND (clock_timestamp() < ( SELECT security.authority_until(''ReadMarketData''::text) AS authority_until))), false)' FROM pg_policies WHERE schemaname='market_data' AND tablename='catalogs' AND policyname='scoped_read';")=='t')
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
    # One scan, so the statement's sets are decided once: the first row read pauses past the token's expiry,
    # and the second row is denied by the per-row clock cutoff alone.
    scan=f"""CREATE FUNCTION pg_temp.pause_once() RETURNS boolean LANGUAGE plpgsql VOLATILE AS $$BEGIN
      IF current_setting('check.paused',true) IS DISTINCT FROM '1' THEN PERFORM set_config('check.paused','1',true);PERFORM pg_sleep(2.1);END IF;
      RETURN true;END$$;
      SELECT count(*) FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{revision}'
       AND id=ANY(ARRAY['{ids[0]}'::uuid,'{ids[1]}'::uuid]) AND pg_temp.pause_once();"""
    for mode in ['force_custom_plan','force_generic_plan']:
        for selected,name in [(None,'current'),(context,'retained')]:
            result=query(runtime(f'SET LOCAL plan_cache_mode={mode};'+delayed,selected,seconds=2))
            check(name+' real RLS rechecks token expiry within one statement under '+mode,result=='{t,f}')
            result=query(runtime(f'SET LOCAL plan_cache_mode={mode};'+scan,selected,seconds=2))
            check(name+' authority ending within one scan denies the rows read after it under '+mode,result=='1')

    # Each statement set accepts exactly what its per-row function accepts, over every row of its table, for a current,
    # a bound, a trading-bound and a write-profile request. Candidates are copied before the request binds, so row
    # security cannot hide the rows the two decisions are compared on.
    pairs=[('market_data.revisions','security.market_fact_visible(r.catalog_id,r.id,NULL)',
            'COALESCE((r.catalog_id,r.id) IN (SELECT v.catalog,v.revision FROM security.visible_market_revisions() v),false)',True),
           ('ref_data.revisions','security.reference_version_visible(r.catalog_id,r.id)',
            'COALESCE((r.catalog_id,r.id) IN (SELECT v.catalog,v.revision FROM security.visible_reference_revisions() v),false)',True),
           ('market_data.observations','security.market_fact_visible(r.catalog_id,r.definition_revision_id,r.id)',
            'COALESCE((r.catalog_id,r.definition_revision_id,r.id) IN (SELECT v.catalog,v.revision,v.observation FROM security.visible_observations() v),false)',True),
           ('trading.instruments','security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)',
            'COALESCE((r.catalog_id,r.publication_order) IN (SELECT v.catalog,v.ordinal FROM security.visible_trading_publications() v) AND r.effective_at<=(SELECT security.request_trading_as_of()),false)',False),
           ('trading.side_schedule_publications','security.side_schedule_visible(r.catalog_id,r.schedule_id)',
            'COALESCE((r.catalog_id,r.schedule_id) IN (SELECT v.catalog,v.schedule FROM security.visible_side_schedules() v),false)',False)]
    for permission in ['ReadMarketData','WriteMarketDefinitions','SealMarketDefinitions']:
        pairs.append(('market_data.catalogs',f"security.market_access(r.id,'{permission}')",f"COALESCE(r.id IN (SELECT security.market_catalogs('{permission}')),false)",permission=='ReadMarketData'))
    for permission in ['ReadMarketData','WriteReferenceData']:
        pairs.append(('ref_data.catalogs',f"security.reference_access(r.id,'{permission}')",f"COALESCE(r.id IN (SELECT security.reference_catalogs('{permission}')),false)",permission=='ReadMarketData'))
    pairs.append(('market_data.catalogs',"security.trading_access(r.id,'ReadTrades')","COALESCE(r.id IN (SELECT security.trading_catalogs('ReadTrades')),false)",False))
    copies=''.join(f"CREATE TEMP TABLE candidates_{n} ON COMMIT DROP AS SELECT * FROM {table};GRANT SELECT ON candidates_{n} TO PUBLIC;"
                   for n,(table,_,_,_) in enumerate(pairs))
    compare=''.join(f"SELECT 'PAIR|{n}|'||count(*) FILTER(WHERE ({old}) IS DISTINCT FROM ({new}))||'|'||count(*) FILTER(WHERE {old}) FROM candidates_{n} r;"
                    for n,(_,old,new,_) in enumerate(pairs))
    trading=f"SELECT security.use_trading_context('{catalog}','2020-12-17T23:00:00Z');"
    for name,selected,profile,extra in [('current',None,'viewer',''),('bound',context,'viewer',''),('trading-bound',context,'trader',trading),('editor',None,'editor','')]:
        lines=[l for l in query(runtime(extra+compare,selected,profile=profile,prefix=copies)).splitlines() if l.startswith('PAIR|')]
        assert len(lines)==len(pairs),lines
        for line in lines:
            n,differing,accepted=map(int,line.split('|')[1:])
            table,old,_,populated=pairs[n]
            check(f'{name} request: the statement set for {old.split("(")[0]} on {table} accepts exactly its per-row function '
                  f'(differing={differing}, accepted={accepted})',differing==0 and (accepted>0 or not populated))
    print(f'{passed} market authority checks passed.',flush=True)
