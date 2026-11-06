"""Adversarial exact reads against the archived policy oracle; owned, rollback-only faults."""
from uuid import uuid4


def run_checks(query, manifest, context, pins, legacy, current):
    catalog,revision,tenant,workspace=(manifest[k] for k in ('catalogId','revisionId','tenantId','workspaceId'))
    ids="ARRAY["+','.join("'"+p+"'::uuid" for p in pins)+"]"
    def issue(seconds=120):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{seconds});")
    def bound(token, retained=True):
        return (f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"+
                (f"SELECT security.use_market_context('{catalog}','{context}');" if retained else '')+
                f"SELECT security.use_close_session('{catalog}','test_utc_close');")
    def equal(selected=ids):
        return f"""SELECT coalesce((SELECT jsonb_agg(to_jsonb(o) ORDER BY o.observed_at,o.received_at,o.id)
          FROM market_data.observations o WHERE o.catalog_id='{catalog}' AND o.definition_revision_id='{revision}' AND o.id=ANY({selected})),'[]')
         =coalesce((SELECT jsonb_agg(to_jsonb(o) ORDER BY o.observed_at,o.received_at,o.id)
          FROM security.read_observation_pins('{catalog}','{revision}',{selected})o),'[]');"""
    def denied():
        return f"""DO $$BEGIN PERFORM * FROM security.read_observation_pins('{catalog}','{revision}',{ids});
         RAISE EXCEPTION 'Expected exact-read refusal';EXCEPTION WHEN insufficient_privilege THEN NULL;END$$;"""
    query(legacy)
    try:
        for retained in (False,True):
            for selected in (ids,ids+f"||ARRAY['{pins[0]}'::uuid]","ARRAY[]::uuid[]"):
                assert query('BEGIN;'+bound(issue(),retained)+equal(selected)+'ROLLBACK;')=='t'
            no_refs=f"DELETE FROM security.reference_catalog_scopes WHERE(tenant_id,workspace_id)=('{tenant}','{workspace}');"
            assert query('BEGIN;'+no_refs+bound(issue(),retained)+equal()+'ROLLBACK;')=='t'
        print('PASS exact observation rows match archived RLS for duplicate/empty pins and withdrawn reference scopes',flush=True)

        changes=[
            "CREATE POLICY synthetic_pin_allow ON market_data.observations FOR SELECT TO openetrm_runtime USING(true);",
            "CREATE POLICY synthetic_pin_deny ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);",
            "ALTER POLICY scoped_read ON market_data.observations USING(false);",
            "DROP POLICY scoped_read ON market_data.observations;",
            "ALTER POLICY temporal_read ON market_data.observations TO openetrm_risk_worker;",
            "ALTER TABLE market_data.observations DISABLE ROW LEVEL SECURITY;",
            "ALTER TABLE market_data.observations NO FORCE ROW LEVEL SECURITY;",
            "REVOKE SELECT ON market_data.observations FROM openetrm_runtime;",
        ]
        for change in changes:
            query('BEGIN;'+change+bound(issue())+denied()+'ROLLBACK;')
        print('PASS exact reads refuse changed permissive/restrictive policies, roles, RLS flags and revoked SELECT',flush=True)
        assert query("SELECT NOT has_function_privilege('preview_runtime','security.observation_visibility_scope(uuid,uuid,uuid[])','EXECUTE');")=='t'
        query('BEGIN;GRANT openetrm_risk_worker TO preview_runtime;'+bound(issue())+
              "DO $$BEGIN IF security.has_permission('ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'Missing baseline authority';END IF;END$$;"+denied()+'ROLLBACK;')
        print('PASS the internal visibility core is private and a risk-role caller without a live claim is refused',flush=True)

        newest=query(f"SELECT id FROM market_data.market_closes WHERE catalog_id='{catalog}' AND session_code='test_utc_close' AND business_date='2020-12-18' ORDER BY publication_order DESC LIMIT 1;")
        late=query(f"SELECT p.observation_id FROM market_data.market_close_pins p JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(p.catalog_id,p.observation_id) WHERE(p.catalog_id,p.close_id)=('{catalog}','{newest}') AND e.event_order>(SELECT known_through FROM market_data.market_contexts WHERE(catalog_id,context_id)=('{catalog}','{context}')) ORDER BY p.observation_id LIMIT 1;")
        assert late,'The retained fixture must precede a selected close pin'
        other=next(p for p in pins if p!=late)
        one=f"ARRAY['{late}'::uuid]"
        fault=f"""SET LOCAL session_replication_role=replica;UPDATE market_data.observations SET observed_at=(SELECT at+interval '1 second' FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{newest}')),
          received_at=greatest(received_at,(SELECT at+interval '1 second' FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{newest}')))
          WHERE(catalog_id,id)=('{catalog}','{other}');SET LOCAL session_replication_role=origin;"""
        query('BEGIN;'+fault+bound(issue())+f"DO $$BEGIN IF EXISTS(SELECT 1 FROM security.read_observation_pins('{catalog}','{revision}',{one})) THEN RAISE EXCEPTION 'Invalid unrequested close pin was ignored';END IF;END$$;"+'ROLLBACK;')
        hidden=query(f"SELECT definition_revision_id FROM market_data.close_session_versions WHERE catalog_id='{catalog}' AND code='test_sql_hidden';")
        assert hidden and hidden!=revision
        later=str(uuid4())
        newer=f"""SET LOCAL session_replication_role=replica;
          INSERT INTO market_data.market_closes SELECT(jsonb_populate_record(NULL::market_data.market_closes,to_jsonb(s)||jsonb_build_object(
            'id','{later}','definition_revision_id','{hidden}','request_id','{uuid4()}','publication_order',(SELECT max(publication_order)+1 FROM market_data.market_closes WHERE catalog_id='{catalog}')))).*
          FROM market_data.market_closes s WHERE(s.catalog_id,s.id)=('{catalog}','{newest}');SET LOCAL session_replication_role=origin;"""
        query('BEGIN;'+newer+bound(issue())+f"DO $$BEGIN IF EXISTS(SELECT 1 FROM security.read_observation_pins('{catalog}','{revision}',{one})) THEN RAISE EXCEPTION 'A hidden newer close fell back to the older revision';END IF;END$$;"+'ROLLBACK;')
        print('PASS point reads validate unrequested close pins and choose newest before revision/visibility filters',flush=True)

        core=query("SELECT pg_get_functiondef('security.observation_visibility_scope(uuid,uuid,uuid[])'::regprocedure);")
        split=core.rfind('\nEND;\n');assert split>=0
        slow=core[:split]+"\nPERFORM pg_sleep(2.1);"+core[split:]+';'
        for grant_expiry in (False,True):
            token=issue(120 if grant_expiry else 2)
            end_grants=(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE subject='viewer') AND NOT revoked AND granted_from<=clock_timestamp() AND clock_timestamp()<granted_to;" if grant_expiry else '')
            entered="SELECT coalesce(sum(calls),0) FROM pg_stat_xact_user_functions WHERE schemaname='security' AND funcname='observation_visibility_scope';"
            assert query("SET track_functions='all';BEGIN;"+slow+end_grants+bound(token)+denied()+entered+'ROLLBACK;')=='1','The expiry fault did not complete the core read'
        print('PASS token/grant expiry while the core buffers results refuses the complete point read',flush=True)

        # Old and new bulk readers must return the same early-cutoff facts. Later
        # unrelated history must not force a new full-catalog materialization.
        query(f"""INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
          'id',md5('synthetic_later_visibility_'||n)::uuid,'source_event_id','synthetic_later_visibility_'||n,'source_revision_id','first','corrects_id',NULL,'received_at',clock_timestamp()))).*
          FROM market_data.observations o CROSS JOIN generate_series(1,4096)n WHERE(o.catalog_id,o.id)=('{catalog}','{pins[0]}');ANALYZE market_data.observations;ANALYZE market_data.knowledge_events;ANALYZE market_data.market_knowledge_receipts;""")
        stats="SELECT 'WORK|'||coalesce(sum(seq_tup_read+idx_tup_fetch),0) FROM pg_stat_xact_user_tables WHERE schemaname='market_data' AND relname='observations';"
        for mode in ('force_custom_plan','force_generic_plan'):
            measurements=[]
            for implementation in (legacy,current):
                query(implementation)
                token=issue()
                output=query('BEGIN;'+f'SET LOCAL plan_cache_mode={mode};'+bound(token).replace(f"SELECT security.use_close_session('{catalog}','test_utc_close');",'')+
                             stats+"SELECT 'ROWS|'||coalesce(jsonb_agg(v ORDER BY v.catalog,v.revision,v.observation),'[]')::text FROM security.visible_observations()v;"+stats+'ROLLBACK;').splitlines()
                work=[int(x.split('|')[1]) for x in output if x.startswith('WORK|')]
                rows=next(x for x in output if x.startswith('ROWS|'))
                measurements.append((work[1]-work[0],rows))
            assert measurements[0][1]==measurements[1][1]
            assert measurements[1][0]<=2*measurements[0][0]+32,(mode,measurements[0][0],measurements[1][0])
            print(f'PASS early retained bulk read: {mode}, archived/new observation tuple reads {measurements[0][0]}/{measurements[1][0]} with 4096 later facts',flush=True)
    finally:
        query(current)
