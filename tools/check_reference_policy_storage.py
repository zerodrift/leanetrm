"""Reference read authorization checks in the launcher's owned synthetic database."""
import json
from uuid import UUID, uuid4


TABLES = ('calendar_days','calendars','clearing_houses','clock_rules',
          'commodities','commodity_locations','commodity_units','currencies','delivery_profiles','dimensions',
          'exchange_clearing_houses','exchanges','futures_execution_admissions','futures_milestones',
          'futures_products','futures_series','imported_product_keys','imported_series_keys',
          'interest_rate_definitions','location_types','locations','measurement_bases','price_index_definitions',
          'product_evidence','quantity_specifications','quote_specifications','reference_import_receipts',
          'revisions','settlement_targets','source_documents','sources','unit_conversions','units','value_specifications')


def run_checks(query, manifest):
    catalog, reference, revision, tenant, workspace = [str(UUID(manifest[key])) for key in
        ['catalogId','referenceCatalogId','referenceRevisionId','tenantId','workspaceId']]
    passed = 0
    def check(name, value):
        nonlocal passed
        assert value, name
        passed += 1
        print('PASS reference policy: '+name, flush=True)
    tables = ','.join("'"+table+"'" for table in TABLES)
    shape = query(f"""SELECT count(*)=34 AND bool_and(p.permissive='PERMISSIVE' AND p.roles=ARRAY['openetrm_runtime']::name[]
      AND p.cmd='SELECT' AND p.qual='true' AND p.with_check IS NULL
      AND t.permissive='RESTRICTIVE' AND t.roles=p.roles AND t.cmd='SELECT' AND t.with_check IS NULL
      AND t.qual=CASE p.tablename WHEN 'revisions' THEN 'security.reference_version_visible(catalog_id, id)'
         ELSE 'security.reference_version_visible(catalog_id, revision_id)' END
      AND c.relrowsecurity AND c.relforcerowsecurity)
      FROM pg_policies p JOIN pg_policies t USING(schemaname,tablename)
      JOIN pg_class c ON c.oid=('ref_data.'||quote_ident(p.tablename))::regclass
      WHERE p.schemaname='ref_data' AND p.tablename IN({tables}) AND p.policyname='scoped_read' AND t.policyname='temporal_read';""")
    check('34 permissive read policies retain their mandatory temporal/current-authority barrier', shape=='t')
    check('catalog and combined policies remain independently scoped', query("""SELECT count(*)=5 AND bool_and(qual LIKE '%reference_access%')
        FROM pg_policies WHERE schemaname='ref_data' AND policyname='scoped_read'
        AND tablename IN('catalogs','futures_product_terms','futures_basis_settlements','futures_lot_resolutions','futures_execution_policies');""")=='t')
    check('imported quantities retain their separate restrictive scope', query("""SELECT permissive='RESTRICTIVE' AND roles=ARRAY['openetrm_runtime']::name[]
        AND cmd='SELECT' AND qual LIKE '%reference_access(quantity_catalog_id%' AND qual LIKE '%reference_version_visible(quantity_catalog_id%'
        FROM pg_policies WHERE schemaname='ref_data' AND tablename='imported_series_keys' AND policyname='quantity_scope';""")=='t')

    def issue(profile='viewer', seconds=60):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{seconds});")
    def runtime(token, sql, rollback=False):
        return f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql} {'ROLLBACK' if rollback else 'COMMIT'};"
    count = f"SELECT count(*) FROM ref_data.units WHERE catalog_id='{reference}' AND revision_id='{revision}';"
    check('unbound pooled login has no reference rows', query('SET SESSION AUTHORIZATION preview_runtime;'+count)=='0')
    check('inherited runtime role reads its current exact reference bundle', int(query(runtime(issue(), 'SET LOCAL ROLE openetrm_runtime;'+count)))>0)
    pooled = query(runtime(issue(), count)+'BEGIN;'+count+'COMMIT;').splitlines()
    check('a reused connection cannot carry authority across transactions', int(pooled[0])>0 and pooled[1]=='0')
    context = str(UUID(query(runtime(issue(),f"SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);"))))
    bind = f"SELECT security.use_market_context('{catalog}','{context}');"
    check('a bound exact reference bundle remains visible', int(query(runtime(issue(),bind+count)))>0)
    outside = str(UUID(query(runtime(issue(),f"SELECT security.capture_date_context('{catalog}','2019-01-01',NULL);"))))
    check('an unavailable historical bundle never falls back to current references', query(runtime(issue(),f"SELECT security.use_market_context('{catalog}','{outside}');"+count))=='0')
    for selected, expected in [('', 't'),(bind,'f')]:
        check('NULL revision preserves the existing '+('bound' if selected else 'current')+' helper contract',
              query(runtime(issue(),selected+f"SELECT security.reference_version_visible('{reference}',NULL);"))==expected)
    check('NULL catalog cannot obtain current reference authority', query(runtime(issue(),"SELECT security.reference_version_visible(NULL,NULL);"))=='f')

    allow = '\n'.join(f'CREATE POLICY synthetic_read_allow ON ref_data.{table} FOR SELECT TO openetrm_runtime USING(true);' for table in TABLES)
    all_counts = 'SELECT sum(n) FROM('+ ' UNION ALL '.join(f'SELECT count(*) n FROM ref_data.{table}' for table in TABLES)+') q;'
    check('an additional permissive policy cannot bypass missing identity',
          query('BEGIN;'+allow+'SET LOCAL SESSION AUTHORIZATION preview_runtime;'+all_counts+'ROLLBACK;')=='0')
    hidden_bind=f"SELECT security.use_market_context('{catalog}','{outside}');"
    check('an additional permissive policy cannot bypass historical visibility',
          query('BEGIN;'+allow+f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');"+hidden_bind+all_counts+'ROLLBACK;')=='0')
    for table, scoped_catalog in [('reference_catalog_scopes',reference),('market_catalog_scopes',catalog)]:
        revoked = bind+"SET SESSION AUTHORIZATION preview_owner;"+f"DELETE FROM security.{table} WHERE catalog_id='{scoped_catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';SET SESSION AUTHORIZATION preview_runtime;"+count
        check('a retained context rechecks current '+table,
              query(runtime(issue(),revoked,rollback=True))=='0')
    # Reference ownership alone is sufficient for an unbound reference read;
    # the bound branch additionally requires its selected market scope.
    unbound = f"SET SESSION AUTHORIZATION preview_owner;DELETE FROM security.market_catalog_scopes WHERE catalog_id='{catalog}';SET SESSION AUTHORIZATION preview_runtime;"+count
    check('independent unbound reference authority is preserved', int(query(runtime(issue(),unbound,rollback=True)))>0)
    viewer=f"(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer')"
    for edit, name in [(f'UPDATE security.principals SET enabled=false WHERE id={viewer};','disabled identity'),
                       (f'UPDATE security.role_grants SET revoked=true WHERE principal_id={viewer};','revoked role grant')]:
        sql=bind+'SET SESSION AUTHORIZATION preview_owner;'+edit+'SET SESSION AUTHORIZATION preview_runtime;'+count
        check(name+' cannot reuse historical reference authority',query(runtime(issue(),sql,rollback=True))=='0')

    # SELECT policies also compose with row locks and UPDATE/RETURNING. Keep
    # WriteReferenceData present while withdrawing ReadMarketData explicitly.
    edit="DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='market_data_editor') AND permission='ReadMarketData';"
    locked=f"SELECT count(*) FROM(SELECT id FROM ref_data.revisions WHERE catalog_id='{reference}' AND id='{revision}' FOR UPDATE) q;"
    updated=f"WITH changed AS(UPDATE ref_data.revisions SET created_at=created_at WHERE catalog_id='{reference}' AND id='{revision}' RETURNING id) SELECT count(*) FROM changed;"
    check('authorized row locking retains the same exact reference row',query(runtime(issue('editor'),locked))=='1')
    for statement, name in [(locked,'SELECT FOR UPDATE'),(updated,'UPDATE RETURNING')]:
        sql='SET SESSION AUTHORIZATION preview_owner;'+edit+'SET SESSION AUTHORIZATION preview_runtime;'+statement
        check(name+' cannot use write permission to bypass missing read authority',query(runtime(issue('editor'),sql,rollback=True))=='0')

    codes=json.loads(query(f"SELECT json_agg(code ORDER BY code) FROM(SELECT code FROM ref_data.units WHERE catalog_id='{reference}' AND revision_id='{revision}' ORDER BY code LIMIT 2) q;"))
    assert len(codes)==2
    array='ARRAY['+','.join("'"+code+"'" for code in codes)+']'
    def delayed():
        return f"WITH checked AS MATERIALIZED(SELECT n,(SELECT count(*) FROM ref_data.units WHERE catalog_id='{reference}' AND revision_id='{revision}' AND code=({array})[n])>0 allowed FROM(SELECT n,pg_sleep((n-1)*2.1) FROM generate_series(1,2)n) delayed) SELECT array_agg(allowed ORDER BY n) FROM checked;"
    for mode in ['force_custom_plan','force_generic_plan']:
        for selected, name in [('', 'current'),(bind,'bound')]:
            result=query(runtime(issue(seconds=2),selected+f'SET LOCAL plan_cache_mode={mode};'+delayed()))
            check(name+' real RLS rechecks token expiry within one statement under '+mode,result=='{t,f}')

    # The actual RLS scan must re-evaluate every part of delegated authority,
    # independently of the longer-lived request token and retained market cut.
    # The agent holds the agent reader role, the only role an agent principal may hold.
    agent,delegator,delegation,grant_agent,grant_delegator=[str(uuid4()) for _ in range(5)]
    subject='policy_agent_'+agent.replace('-','')
    query(f"""INSERT INTO security.principals VALUES('{agent}','agent',true),('{delegator}','human',true);
      INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{agent}','{subject}','Synthetic policy agent',true,1),
        ('{tenant}','{workspace}','{delegator}','policy_user_{delegator.replace('-','')}','Synthetic policy delegator',true,1);
      INSERT INTO security.identity_bindings VALUES('https://reference-policy.invalid','{subject}','{agent}','agent_credential');
      INSERT INTO security.role_grants VALUES('{grant_agent}','{agent}','a0000000-0000-0000-0000-000000000007','{tenant}','{workspace}',clock_timestamp()-interval '1 day','9999-12-31',false),
        ('{grant_delegator}','{delegator}','a0000000-0000-0000-0000-000000000004','{tenant}','{workspace}',clock_timestamp()-interval '1 day','9999-12-31',false);
      INSERT INTO security.agent_delegations VALUES('{delegation}','{agent}','{delegator}','{tenant}','{workspace}',clock_timestamp()-interval '1 day','9999-12-31',false);
      INSERT INTO security.delegation_permissions VALUES('{delegation}','ReadMarketData');""")
    def agent_issue():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://reference-policy.invalid','{subject}','agent_credential','{tenant}','{workspace}','{delegation}','preview_runtime',60);")
    try:
        check('an agent reads only with its own and its delegator current permissions',int(query(runtime(agent_issue(),bind+count)))>0)
        for table,key,value in [('agent_delegations','id',delegation),('role_grants','id',grant_agent),('role_grants','id',grant_delegator)]:
            query(f"UPDATE security.{table} SET granted_to=clock_timestamp()+interval '2 seconds' WHERE {key}='{value}';")
            check('real RLS rechecks '+('delegation' if table=='agent_delegations' else 'agent grant' if value==grant_agent else 'delegator grant')+' expiry',
                  query(runtime(agent_issue(),bind+delayed()))=='{t,f}')
            query(f"UPDATE security.{table} SET granted_to='9999-12-31' WHERE {key}='{value}';")
    finally:
        query(f"""DELETE FROM security.request_contexts WHERE actor_id='{agent}';
          DELETE FROM security.delegation_permissions WHERE delegation_id='{delegation}';DELETE FROM security.agent_delegations WHERE id='{delegation}';
          DELETE FROM security.role_grants WHERE principal_id IN('{agent}','{delegator}');DELETE FROM security.identity_bindings WHERE principal_id='{agent}';
          DELETE FROM security.workspace_memberships WHERE principal_id IN('{agent}','{delegator}');DELETE FROM security.principals WHERE id IN('{agent}','{delegator}');""")
    print(f'{passed} reference policy checks passed.',flush=True)
