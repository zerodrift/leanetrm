"""Adversarial temporal/RLS checks in the launcher's newly owned cluster."""
import http.cookiejar
import json
from pathlib import Path
import subprocess
import time
from urllib.error import HTTPError
from urllib.request import Request, build_opener, HTTPCookieProcessor
from uuid import UUID, uuid4


def run_checks(query, psql, environment, manifest, url):
    catalog = str(UUID(manifest['catalogId'])); tenant = str(UUID(manifest['tenantId'])); workspace = str(UUID(manifest['workspaceId']))
    passed = 0; children = []
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS '+name, flush=True)
    def issue(profile='approver', seconds=60, login='preview_runtime'):
        return query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'{login}',{seconds});")
    def runtime(token, sql):
        return f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} COMMIT;"
    def expect(sql, code, message=None):
        guard = '' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE; END IF;"
        return f"DO $$ BEGIN {sql} RAISE EXCEPTION 'Expected rejection'; EXCEPTION WHEN SQLSTATE '{code}' THEN {guard} END $$;"
    def connect(sql, name):
        process = subprocess.Popen(psql, env={**environment, 'PGAPPNAME': name}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(process); process.stdin.write(sql+'\n\\echo READY\n'); process.stdin.flush()
        while process.stdout.readline().strip() != 'READY':
            if process.poll() is not None: raise AssertionError(process.stderr.read())
        return process
    def wait_lock(name):
        for _ in range(100):
            if query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{name}' AND wait_event_type='Lock';") == '1': return
            time.sleep(.05)
        raise AssertionError('Expected waiter did not block: '+name)
    def finish(process, sql='COMMIT;'):
        process.stdin.write(sql+'\n'); process.stdin.flush()
        output, error = process.communicate(timeout=10)
        assert process.returncode == 0, error
        return output.strip()
    def capture(token=None):
        return query(runtime(token or issue('viewer'), f"SELECT security.capture_date_context('{catalog}','2021-02-15',NULL);"))
    columns = 'catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code,publication_order,published_at,publication_xid'
    try:
        check('commit tracking explicitly enabled in owned preview', query('SHOW track_commit_timestamp;') == 'on')
        capture()
        check('all new knowledge stores force scoped RLS', query("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='market_data' AND c.relname IN ('knowledge_clocks','knowledge_publications','temporal_contexts') AND c.relrowsecurity AND c.relforcerowsecurity;") == '3')
        for table in ['knowledge_clocks', 'knowledge_publications', 'temporal_contexts', 'date_schedule_facts']:
            check('unbound runtime sees no '+table, query('SET SESSION AUTHORIZATION preview_runtime; SELECT count(*) FROM market_data.'+table+';') == '0')
        for table in ['knowledge_publications', 'temporal_contexts']:
            query(runtime(issue('viewer'), expect(f"DELETE FROM market_data.{table};", '42501')))
            check('runtime cannot rewrite '+table)
        certificate = query(f"SELECT publication_order FROM market_data.knowledge_publications WHERE catalog_id='{catalog}' LIMIT 1;")
        query(expect(f"UPDATE market_data.knowledge_publications SET known_from=known_from WHERE catalog_id='{catalog}' AND publication_order={certificate};", '55000'))
        check('certified commit receipts remain immutable for privileged ordinary DML')
        query(expect("PERFORM security.checked_commit_time('3'::xid8);", 'P0001', 'knowledge_retention'))
        check('missing engine commit metadata is rejected rather than reconstructed')
        query(expect("PERFORM security.checked_commit_time((pg_snapshot_xmax(pg_current_snapshot())::text::numeric+4294967296)::text::xid8);", 'P0001', 'knowledge_retention'))
        check('full transaction epoch is validated before narrowing transaction identity')
        query(runtime(issue('viewer'),expect("PERFORM security.checked_commit_time('3'::xid8);", '42501')))
        check('runtime cannot use private commit certification as a cross-scope oracle')
        hidden=query("SELECT id FROM market_data.catalogs WHERE code='hidden_schedule_test';")
        query(expect(f"INSERT INTO market_data.knowledge_publications VALUES('{hidden}',10000,'3'::xid8,clock_timestamp());", '23503'))
        check('certificate references the exact original publication transaction through a foreign key')
        # XID ordering is intentionally opposite commit/admission ordering.
        schedule = str(UUID(query(f"SELECT id FROM market_data.date_schedules WHERE catalog_id='{catalog}' AND symbol='oe.ng';")))
        version = int(query(f"SELECT max(version)+1 FROM market_data.date_schedule_versions WHERE catalog_id='{catalog}' AND schedule_id='{schedule}';"))
        query(f"INSERT INTO market_data.date_schedule_versions({columns}) SELECT catalog_id,schedule_id,{version},1,'draft',definition,selection,effective_from,effective_to,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code,NULL,NULL,NULL FROM market_data.date_schedule_versions WHERE catalog_id='{catalog}' AND schedule_id='{schedule}' AND version={version-1};")
        old_head = query(f"SELECT max(publication_order) FROM market_data.date_schedule_versions WHERE catalog_id='{catalog}';")
        capture_token = issue('viewer')
        capture_process = connect(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{capture_token}');", 'temporal_older_capture')
        publisher = connect(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{issue()}'); SELECT security.publish_date_schedule('{catalog}','{schedule}',{version},1);", 'temporal_newer_publisher')
        cutoff = query("SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"');")
        capture_process.stdin.write(f"SELECT security.capture_date_context('{catalog}','2021-02-15','{cutoff}'); COMMIT;\n"); capture_process.stdin.flush()
        wait_lock('temporal_older_capture')
        check('capture waits until in-flight publisher has committed', capture_process.poll() is None)
        finish(publisher)
        output, error = capture_process.communicate(timeout=10)
        check('capture admitted before a newer-XID publisher certifies successfully', capture_process.returncode == 0)
        if capture_process.returncode: raise AssertionError(error)
        context_id = str(UUID(output.strip()))
        check('commit after requested cutoff cannot leak into earlier known time', query(f"SELECT known_through FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{context_id}';") == old_head)
        check('frozen context stores exact effective and knowledge coordinates', query(f"SELECT effective_on='2021-02-15' AND known_at='{cutoff}'::timestamptz FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{context_id}';") == 't')
        # Two true publications in one transaction have exactly tied commit times.
        owner_token = issue(login='preview_owner')
        sql = f"BEGIN; SELECT security.use_context('{owner_token}');"
        for next_version in [version+1, version+2]:
            sql += f"INSERT INTO market_data.date_schedule_versions({columns}) SELECT catalog_id,schedule_id,{next_version},1,'draft',definition,selection,effective_from,effective_to,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code,NULL,NULL,NULL FROM market_data.date_schedule_versions WHERE catalog_id='{catalog}' AND schedule_id='{schedule}' AND version={next_version-1}; SELECT security.publish_date_schedule('{catalog}','{schedule}',{next_version},1);"
        query(sql+' COMMIT;')
        capture()
        check('same-commit publications retain an identical knowledge boundary', query(f"SELECT count(DISTINCT k.known_from) FROM market_data.knowledge_publications k JOIN market_data.date_schedule_versions v USING(catalog_id,publication_order) WHERE v.catalog_id='{catalog}' AND v.schedule_id='{schedule}' AND v.version IN ({version+1},{version+2});") == '1')
        check('temporal rectangles never expose zero-width knowledge intervals', query(f"SELECT count(*) FROM market_data.date_schedule_facts WHERE catalog_id='{catalog}' AND (known_from>=known_to OR effective_from>=effective_to);") == '0')
        # Capability expiry after a real admission wait must reject a context.
        locker = connect('BEGIN; SELECT pg_advisory_xact_lock(730849102);', 'temporal_expiry_lock')
        token = issue('viewer', seconds=2)
        waiter = subprocess.Popen(psql+['-v','VERBOSITY=verbose'], env={**environment,'PGAPPNAME':'temporal_expiry_waiter'}, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(waiter);waiter.stdin.write(runtime(token,f"SELECT security.capture_date_context('{catalog}','2021-02-15',NULL);"));waiter.stdin.close();waiter.stdin=None
        wait_lock('temporal_expiry_waiter')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>expires_at FROM security.request_contexts WHERE id='{token}';") == 't': break
            time.sleep(.05)
        else: raise AssertionError('Expected capability expiry')
        finish(locker);_,error=waiter.communicate(timeout=10)
        check('knowledge capture rechecks authority after lock waits', waiter.returncode != 0 and '42501' in error)
        # Current revocation blocks both HTTP replay and direct historical RLS.
        opener = build_opener(HTTPCookieProcessor(http.cookiejar.CookieJar()))
        def call_api(path, body=None, expected=200):
            headers={'Origin':url}
            if body is not None: headers.update({'Content-Type':'application/json','X-Preview-CSRF':csrf})
            request=Request(url+'/api/'+path, data=None if body is None else json.dumps(body).encode(), headers=headers)
            try: response=opener.open(request,timeout=10)
            except HTTPError as error: response=error
            data=json.loads(response.read())
            assert response.code == expected,(path,response.code,data)
            return data
        csrf=call_api('bootstrap')['csrfToken'];call_api('profile',{'profile':'viewer'})
        snapshot=call_api('date-schedules/context',{'effectiveOn':'2021-02-15','mode':'current','knownAt':None})['context']
        baseline=call_api('date-schedules/resolve',{'expression':'oe.ng','context':snapshot})
        actor="(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer')"
        query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id={actor};")
        try:
            call_api('date-schedules/resolve',{'expression':'oe.ng','context':snapshot},expected=403)
            check('retained historical context cannot restore revoked HTTP permission')
            check('revoked runtime cannot read even an existing context receipt', query(runtime(issue('viewer'),f"SELECT count(*) FROM market_data.temporal_contexts WHERE catalog_id='{catalog}';")).strip() == '0')
        finally: query(f"UPDATE security.role_grants SET revoked=false WHERE principal_id={actor};")
        check('restored current permission replays the exact retained result', call_api('date-schedules/resolve',{'expression':'oe.ng','context':snapshot}) == baseline)
        # One context restricts raw market/reference reads as well as HTTP adapters.
        ctx = query(runtime(issue('viewer'), f"SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);"))
        bind = f"SELECT security.use_market_context('{catalog}','{ctx}');"
        visible = query(runtime(issue('viewer'), bind+f"SELECT count(*) FROM market_data.observations WHERE catalog_id='{catalog}' AND observed_at>='2020-12-18';"))
        check('bound temporal RLS hides later market observation dates', visible == '0')
        check('bound reference reads retain only exact certified bundle versions', query(runtime(issue('viewer'), bind+"SELECT count(DISTINCT revision_id) FROM ref_data.units;")) == '1')
        reference_catalog = str(UUID(manifest['referenceCatalogId']))
        reference_revision = str(UUID(manifest['referenceRevisionId']))
        reference_visible = f"security.reference_version_visible('{reference_catalog}','{reference_revision}')"
        for mode in ['force_generic_plan', 'force_custom_plan']:
            check('reference visibility preserves the bound bundle and caller planning policy: '+mode,
                  query(runtime(issue('viewer'), bind+f"SET LOCAL plan_cache_mode={mode}; SELECT {reference_visible}; "
                                "SELECT current_setting('plan_cache_mode');")) == 't\n'+mode)
        # Current catalog scopes are separate authorities. A historical receipt
        # cannot restore either one after admission. Roll back each injected
        # revocation together with its bound request in this disposable cluster.
        for table, scoped_catalog in [('market_catalog_scopes', catalog),
                                      ('reference_catalog_scopes', reference_catalog)]:
            revoked = (bind+"SET SESSION AUTHORIZATION preview_owner; "
                       f"DELETE FROM security.{table} WHERE catalog_id='{scoped_catalog}' "
                       f"AND tenant_id='{tenant}' AND workspace_id='{workspace}'; "
                       "SET SESSION AUTHORIZATION preview_runtime; "
                       f"SELECT NOT {reference_visible} AND NOT EXISTS(SELECT 1 FROM ref_data.units);")
            check('bound reference visibility rechecks current '+table,
                  query(runtime(issue('viewer'), revoked).replace(' COMMIT;', ' ROLLBACK;')) == 't')
        check('reference visibility rechecks token expiry during a statement',
              query(runtime(issue('viewer', seconds=1), bind+
                    f"WITH checked AS MATERIALIZED(SELECT n,{reference_visible} AS allowed FROM "
                    "(SELECT n,pg_sleep((n-1)*1.1) FROM generate_series(1,2)n) delayed) "
                    "SELECT array_agg(allowed ORDER BY n) FROM checked;")).strip() == '{t,f}')
        check('visibility helpers retain authority metadata and their explicit local planning policy',
              query("SELECT bool_and(provolatile='v' AND prosecdef AND NOT proisstrict "
                    "AND pg_get_userbyid(proowner)='openetrm_security_owner' "
                    "AND proconfig @> ARRAY['search_path=pg_catalog','plan_cache_mode=force_generic_plan'] "
                    "AND cardinality(proconfig)=2) FROM pg_proc "
                    "WHERE oid IN ('security.market_fact_visible(uuid,uuid,uuid)'::regprocedure,"
                    "'security.reference_version_visible(uuid,uuid)'::regprocedure);") == 't')
        query(runtime(issue('viewer'), bind+expect(f"PERFORM security.use_market_context('{catalog}','{ctx}');", '42501')))
        check('a request cannot rebind its historical scope after reading')
        check('historical editor has no database write context', query(runtime(issue('editor'),bind+"SELECT security.market_access('"+catalog+"','ImportObservations');")) == 'f')
        check('TOU products are current definitions: visible now, invisible under a historical market context',
              int(query(runtime(issue('viewer'),"SELECT count(*) FROM market_data.tou_product_versions;"))) >= 7
              and query(runtime(issue('viewer'),bind+"SELECT count(*) FROM market_data.tou_product_versions;")) == '0')
        out_of_period = query(runtime(issue('viewer'),f"SELECT security.capture_date_context('{catalog}','2019-01-01',NULL);"))
        check('definition applicability excludes bundles outside their business interval', query(runtime(issue('viewer'),f"SELECT security.use_market_context('{catalog}','{out_of_period}'); SELECT count(*) FROM market_data.revisions;")) == '0')
        check('reference closure cannot fall back outside definition applicability', query(runtime(issue('viewer'),f"SELECT security.use_market_context('{catalog}','{out_of_period}'); SELECT count(*) FROM ref_data.units;")) == '0')
        for table in ['revision_periods','knowledge_events','market_knowledge_receipts','market_contexts']:
            check('market temporal table forces scoped RLS: '+table, query(f"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='market_data.{table}'::regclass;") == 't')
        # received_at precedes commit. Pause an actual import before committing,
        # ask for a cutoff while it is in flight, and prove it stays excluded.
        oid = query(f"SELECT id FROM market_data.observations o WHERE catalog_id='{catalog}' AND observed_at::date='2020-12-15' AND corrects_id IS NULL AND NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE n.catalog_id=o.catalog_id AND n.corrects_id=o.id) ORDER BY id LIMIT 1;")
        new_id = str(uuid4())
        insert = f"INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id) SELECT catalog_id,'{new_id}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,'{new_id}',role,publication_state,observed_at,clock_timestamp(),value,id FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{oid}';"
        imported = connect(runtime(issue('editor'),insert).rsplit(' COMMIT;',1)[0], 'market_uncommitted_import')
        requested = query('SELECT clock_timestamp()::text;')
        waiter = subprocess.Popen(psql,env={**environment,'PGAPPNAME':'market_cutoff_waiter'},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        children.append(waiter)
        waiter.stdin.write(runtime(issue('viewer'),f"SELECT security.capture_date_context('{catalog}','2020-12-17','{requested}');"));waiter.stdin.close();waiter.stdin=None
        wait_lock('market_cutoff_waiter');finish(imported)
        output,error=waiter.communicate(timeout=10);assert waiter.returncode==0,error
        old_context=output.strip()
        count=query(runtime(issue('viewer'),f"SELECT security.use_market_context('{catalog}','{old_context}'); SELECT count(*) FROM market_data.observations WHERE id='{new_id}';"))
        check('in-flight import is excluded even when received_at precedes cutoff',count=='0')
        now_context=query(runtime(issue('viewer'),f"SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);"))
        check('later cutoff includes the genuinely committed correction',query(runtime(issue('viewer'),f"SELECT security.use_market_context('{catalog}','{now_context}'); SELECT count(*) FROM market_data.observations WHERE id='{new_id}';"))=='1')
        # Expiry while blocked must roll back both observation and event assertion.
        locker=connect('BEGIN; SELECT pg_advisory_xact_lock(730849102);','market_expiry_lock')
        expired_id=str(uuid4())
        expired_insert=insert.replace(new_id,expired_id).replace(f"id='{oid}'",f"id='{new_id}'")
        token=issue('editor',seconds=2)
        waiter=subprocess.Popen(psql+['-v','VERBOSITY=verbose'],env={**environment,'PGAPPNAME':'market_expiry_waiter'},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        children.append(waiter);waiter.stdin.write(runtime(token,expired_insert));waiter.stdin.close();waiter.stdin=None
        wait_lock('market_expiry_waiter')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>expires_at FROM security.request_contexts WHERE id='{token}';")=='t':break
            time.sleep(.05)
        else:raise AssertionError('Expected import capability expiry')
        finish(locker);_,error=waiter.communicate(timeout=10)
        check('import admission rechecks expiring permission after its commit-gate wait',waiter.returncode!=0 and '42501' in error)
        check('expired import leaves neither a price nor a knowledge assertion',query(f"SELECT (SELECT count(*) FROM market_data.observations WHERE id='{expired_id}')+(SELECT count(*) FROM market_data.knowledge_events WHERE observation_id='{expired_id}');")=='0')
        # Administration is admitted exclusively; runtime never writes security tables.
        for table in ['workspace_memberships','administration_audit']:
            query(runtime(issue('admin'),expect(f"PERFORM 1 FROM security.{table};",'42501')))
            check('runtime cannot bypass administration through '+table)
        query(runtime(issue('admin'),expect(f"PERFORM security.use_administration_context('{issue('admin')}');",'42501')))
        check('administration rejects a shared-to-exclusive admission upgrade')
        admin_actor="(SELECT principal_id FROM security.identity_bindings WHERE subject='admin' AND issuer='https://preview.invalid')"
        second=str(uuid4());other_workspace=str(uuid4())
        query(f"INSERT INTO security.workspaces VALUES('{tenant}','{other_workspace}','other_admin_test'); INSERT INTO security.workspace_memberships VALUES('{tenant}','{other_workspace}',{admin_actor},'admin','Other workspace administrator',true,1); INSERT INTO security.role_grants VALUES(gen_random_uuid(),{admin_actor},'a0000000-0000-0000-0000-000000000001','{tenant}','{other_workspace}',clock_timestamp(),TIMESTAMPTZ '9999-12-31',false);")
        # The same global identity has independent membership controls per scope.
        query(f"UPDATE security.workspace_memberships SET enabled=false WHERE tenant_id='{tenant}' AND workspace_id='{other_workspace}';")
        check('scoped suspension leaves another workspace permission intact',query(f"SELECT security.principal_has_permission({admin_actor},'{tenant}','{workspace}','ManageAccess',clock_timestamp());")=='t')
        # Race the exact same new-account command through two independent sessions.
        command=json.dumps({'action':'create','operationId':second,'login':'concurrent_account','name':'Concurrent synthetic account','kind':'human','roles':['viewer']})
        def admin_sql(token,statement):return f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_administration_context('{token}'); {statement}"
        first=connect(admin_sql(issue('admin'),f"SELECT security.apply_administration('{command}'::jsonb)->>'id';"),'admin_first')
        waiter=subprocess.Popen(psql,env={**environment,'PGAPPNAME':'admin_second'},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        children.append(waiter);waiter.stdin.write(admin_sql(issue('admin'),f"SELECT security.apply_administration('{command}'::jsonb)->>'id'; COMMIT;"));waiter.stdin.close();waiter.stdin=None
        wait_lock('admin_second');finish(first);output,error=waiter.communicate(timeout=10);assert waiter.returncode==0,error
        check('concurrent identical admin requests create one member and audit outcome',query(f"SELECT count(*) FROM security.administration_audit WHERE operation_id='{second}';")=='1' and query("SELECT count(*) FROM security.identity_bindings WHERE subject='concurrent_account';")=='1')
        query(expect(f"UPDATE security.administration_audit SET request=request WHERE operation_id='{second}';",'55000'))
        check('administration audit rejects ordinary privileged rewrites')
        foreign_member=str(uuid4())
        query(f"INSERT INTO security.principals VALUES('{foreign_member}','human',true); INSERT INTO security.workspace_memberships VALUES('{tenant}','{other_workspace}','{foreign_member}','other_person','Other workspace person',true,1);")
        update=json.dumps({'action':'update','operationId':str(uuid4()),'id':foreign_member,'expectedVersion':1,'name':'Attempted cross scope change','enabled':False,'roles':[]})
        query(admin_sql(issue('admin'),expect(f"PERFORM security.apply_administration('{update}'::jsonb);",'P0001','admin_conflict'))+' COMMIT;')
        check('workspace administrator cannot mutate another workspace member',query(f"SELECT enabled FROM security.workspace_memberships WHERE principal_id='{foreign_member}';")=='t')
        directory=json.loads(query(runtime(issue('admin'),"SELECT security.administration_directory()::text;")))
        check('administrative directory omits other-scope identities',not any(m['id']==foreign_member for m in directory['members']))
        # Account state cannot be escalated through a different credential kind. An agent created through
        # ManageAgents is an agent reader and can hold no other role.
        created=json.loads(query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT assistant.use_agent_admission('{issue('admin')}'); "
                                 f"SELECT assistant.create_agent('{uuid4()}','Synthetic storage agent')::text; COMMIT;").splitlines()[-1])
        agent_actor=query(f"SELECT principal_id FROM assistant.agents WHERE id='{created['id']}';")
        query(expect(f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{agent_actor}','a0000000-0000-0000-0000-000000000006','{tenant}','{workspace}',clock_timestamp(),TIMESTAMPTZ '9999-12-31',false);",'23514'))
        check('an agent created through ManageAgents is refused any role but the agent reader')
        def agent_token(delegation='NULL'):
            return query(f"SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context('https://preview.invalid','{agent_actor}','agent_credential','{tenant}','{workspace}',{delegation},'preview_runtime',60);")
        query(runtime(agent_token(),expect("PERFORM security.administration_directory();",'42501')))
        check('an agent context cannot administer workspace access')
        # Delegated authority (docs/ai-assistant.md §13): the agent reads market data only under a current delegation
        # from a user who may delegate and read it.
        reads=f"SELECT security.market_access('{catalog}','ReadMarketData');"
        undelegated=query(runtime(agent_token(),reads)).splitlines()[-1]
        delegation=str(uuid4())
        trader=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
        query(f"INSERT INTO security.agent_delegations VALUES('{delegation}','{agent_actor}','{trader}','{tenant}','{workspace}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 hour',false);"
              f"INSERT INTO security.delegation_permissions VALUES('{delegation}','ReadMarketData');")
        delegated=query(runtime(agent_token(f"'{delegation}'"),reads)).splitlines()[-1]
        query(f"UPDATE security.agent_delegations SET revoked=true WHERE id='{delegation}';")
        revoked=query(runtime(agent_token(f"'{delegation}'"),reads)).splitlines()[-1]
        check('an agent created through ManageAgents reads market data only under a current delegation',
              (undelegated,delegated,revoked)==('f','t','f'))
        # A blocked administrative request cannot revive expired credentials.
        locker=connect('BEGIN; SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;','admin_expiry_lock')
        token=issue('admin',seconds=2)
        waiter=subprocess.Popen(psql+['-v','VERBOSITY=verbose'],env={**environment,'PGAPPNAME':'admin_expiry_waiter'},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        children.append(waiter);waiter.stdin.write(admin_sql(token,'COMMIT;'));waiter.stdin.close();waiter.stdin=None
        wait_lock('admin_expiry_waiter')
        for _ in range(100):
            if query(f"SELECT clock_timestamp()>expires_at FROM security.request_contexts WHERE id='{token}';")=='t':break
            time.sleep(.05)
        else:raise AssertionError('Expected administrative capability expiry')
        finish(locker);_,error=waiter.communicate(timeout=10)
        check('exclusive administrative admission rechecks expiry after waiting',waiter.returncode!=0 and '42501' in error)
        migration=(Path(__file__).resolve().parents[1]/'db/migrations/0010_workspace_administration.sql').read_text()
        rejected=subprocess.run(psql,env=environment,input=migration,text=True,capture_output=True,timeout=10)
        check('existing grants cannot receive an inferred membership migration',rejected.returncode!=0 and 'Existing grants require a verified workspace membership migration' in rejected.stderr)
        names=(Path(__file__).resolve().parents[1]/'db/migrations/0026_display_names.sql').read_text()
        rejected=subprocess.run(psql,env=environment,input=names,text=True,capture_output=True,timeout=10)
        check('existing curves cannot receive inferred display names',rejected.returncode!=0 and 'Existing curves require a verified display-name migration' in rejected.stderr)
        # Fault injection is confined to this disposable database and restored.
        timeline=query(f"SELECT timeline_id FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")
        query(f"UPDATE market_data.knowledge_clocks SET timeline_id=timeline_id+1 WHERE catalog_id='{catalog}';")
        try:
            query(runtime(issue('viewer'),expect(f"PERFORM security.capture_date_context('{catalog}','2021-02-15',NULL);",'P0001','knowledge_timeline')))
            check('fresh certification rejects an unqualified database timeline')
        finally: query(f"UPDATE market_data.knowledge_clocks SET timeline_id={timeline} WHERE catalog_id='{catalog}';")
        watermark=query(f"SELECT certified_through::text FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")
        query(f"UPDATE market_data.knowledge_clocks SET certified_through='2099-01-01' WHERE catalog_id='{catalog}';")
        try:
            query(runtime(issue('viewer'),expect(f"PERFORM security.capture_date_context('{catalog}','2021-02-15',NULL);",'P0001','knowledge_clock')))
            check('clock regression cannot reopen a certified knowledge interval')
        finally: query(f"UPDATE market_data.knowledge_clocks SET certified_through='{watermark}' WHERE catalog_id='{catalog}';")
        print(f'{passed} temporal storage/security checks passed.',flush=True)
    finally:
        for process in children:
            if process.poll() is None: process.kill();process.wait(timeout=5)
