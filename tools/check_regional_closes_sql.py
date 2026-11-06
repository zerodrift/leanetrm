"""D334 close sessions, per-session grants and the close-in-force rule, qualified in SQL on an owned synthetic database."""
from uuid import uuid4


def run_checks(url, query, manifest):
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    reference = "'10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001'"
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    approver = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','approver');")

    def refused(statement, code):
        query(f"DO $$BEGIN {statement};RAISE EXCEPTION 'expected {code}';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{code}' THEN RAISE;END IF;END$$;")

    def session(code, calendar, cutoff):
        query(f"""INSERT INTO market_data.close_sessions VALUES('{catalog}','{code}','Synthetic {code}');
        INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
         calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
        VALUES('{catalog}','{code}',1,'published',1,'{revision}',{reference},'{calendar}','{cutoff}','2020-12-01','2021-12-01','{approver}','{uuid4()}','{{}}','{approver}','{uuid4()}');
        INSERT INTO market_data.close_session_curves VALUES('{catalog}','{code}',1,'{revision}','future');""")

    def instant(code, day):
        return query(f"SELECT to_char(security.close_session_instant('{catalog}','{code}',1,'{day}') AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"');")

    session('test_sql_london', 'test_london_days', '17:30')
    session('test_sql_new_york', 'test_new_york_days', '17:30')
    session('test_sql_gap', 'test_london_every_day', '01:30')
    assert instant('test_sql_london', '2021-01-15') == '2021-01-15T17:30:00Z'
    assert instant('test_sql_london', '2021-07-15') == '2021-07-15T16:30:00Z', 'British summer time was not applied'
    assert instant('test_sql_new_york', '2021-01-15') == '2021-01-15T22:30:00Z'
    # A Saturday and a day after the version's range are not session business days.
    refused(f"PERFORM security.close_session_instant('{catalog}','test_sql_london',1,'2021-01-16')", 'close_session')
    refused(f"PERFORM security.close_session_instant('{catalog}','test_sql_london',1,'2022-01-14')", 'close_session')
    # 01:30 does not exist in London on 28 March 2021, when clocks go forward; the day is refused, never moved.
    assert instant('test_sql_gap', '2021-03-27') == '2021-03-27T01:30:00Z'
    refused(f"PERFORM security.close_session_instant('{catalog}','test_sql_gap',1,'2021-03-28')", 'close_session_instant')
    # 01:30 happens twice on 31 October 2021, when clocks go back; an ambiguous cutoff is refused too.
    refused(f"PERFORM security.close_session_instant('{catalog}','test_sql_gap',1,'2021-10-31')", 'close_session_instant')
    print('PASS close session instants follow the session clock and calendar and refuse missing local times', flush=True)

    def close(code, day, order_hint):
        identity = str(uuid4())
        query(f"""INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,
         publication_order,publication_xid,session_code,session_version,business_date)
        SELECT '{catalog}','{identity}','{revision}',{reference},'Synthetic {order_hint}',security.close_session_instant('{catalog}','{code}',1,'{day}'),'retained_input',
         '{approver}','{uuid4()}','{{}}'::jsonb,coalesce(max(publication_order),0)+1,pg_current_xact_id(),'{code}',1,'{day}'
        FROM market_data.market_closes WHERE catalog_id='{catalog}';""")
        return identity

    first = close('test_sql_london', '2021-01-15', 'first London')
    other = close('test_sql_new_york', '2021-01-15', 'New York')
    second = close('test_sql_london', '2021-01-15', 'second London')
    def token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")

    def as_approver(expression):
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');SELECT {expression};COMMIT;").splitlines()[-1]

    in_force = lambda code, day='2021-01-15': as_approver(f"coalesce(security.close_in_force('{catalog}','{code}','{day}')::text,'none')")
    assert in_force('test_sql_london') == second and first != second
    assert in_force('test_sql_new_york') == other
    assert in_force('test_sql_london', '2021-01-14') == 'none'
    print('PASS the latest recorded close for a session and business day is in force, per session', flush=True)

    can_record = lambda code: as_approver(f"security.can_record_close('{catalog}','{code}')")

    assert can_record('test_sql_london') == 'f', 'Publishing authority alone recorded a close'
    grant = str(uuid4())
    query(f"""INSERT INTO security.close_session_grants(id,principal_id,tenant_id,workspace_id,catalog_id,session_code,granted_from,granted_to,revoked,actor_id,request_id)
    VALUES('{grant}','{approver}','{tenant}','{workspace}','{catalog}','test_sql_london',clock_timestamp(),'9999-12-31',false,'{approver}','{uuid4()}');""")
    assert can_record('test_sql_london') == 't'
    assert can_record('test_sql_new_york') == 'f', 'A London grant recorded a New York close'
    query(f"UPDATE security.close_session_grants SET revoked=true,revoker_id='{approver}',revoke_request_id='{uuid4()}' WHERE id='{grant}';")
    assert can_record('test_sql_london') == 'f', 'A revoked grant recorded a close'
    print('PASS recording a close needs a live grant for that session', flush=True)

    query(f"""DO $$BEGIN INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,publication_order,publication_xid)
     VALUES('{catalog}','{uuid4()}','{revision}',{reference},'Synthetic sessionless','2021-01-15T16:00:00Z','retained_input','{approver}','{uuid4()}','{{}}'::jsonb,999999,pg_current_xact_id());
     RAISE EXCEPTION 'A close without a session was recorded';EXCEPTION WHEN not_null_violation THEN NULL;END$$;""")
    print('PASS a close cannot be recorded without a session and business day', flush=True)

    # A day whose close in force the reader cannot see is named, never answered with an older close (D334): a close under a
    # definition revision sealed after a historical view's cutoff is invisible to that view.
    context = query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);COMMIT;").splitlines()[-1]
    later = str(uuid4())
    query(f"""BEGIN;
    INSERT INTO market_data.revisions SELECT(jsonb_populate_record(NULL::market_data.revisions,to_jsonb(r)||jsonb_build_object('id','{later}','state','draft','generation',0))).* FROM market_data.revisions r WHERE(catalog_id,id)=('{catalog}','{revision}');
    INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('revision_id','{later}'))).* FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{revision}','future');
    INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(k)||jsonb_build_object('revision_id','{later}'))).* FROM market_data.contracts k
     WHERE(k.catalog_id,k.revision_id)=('{catalog}','{revision}') AND k.id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','future'));
    INSERT INTO market_data.curve_members SELECT(jsonb_populate_record(NULL::market_data.curve_members,to_jsonb(m)||jsonb_build_object('revision_id','{later}'))).* FROM market_data.curve_members m
     WHERE(m.catalog_id,m.revision_id,m.curve_code)=('{catalog}','{revision}','future');
    INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(x)||jsonb_build_object('revision_id','{later}'))).* FROM market_data.series x
     WHERE(x.catalog_id,x.revision_id)=('{catalog}','{revision}') AND x.contract_id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','future'));
    INSERT INTO market_data.revision_periods VALUES('{catalog}','{later}','2020-01-01','2022-01-01');
    UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{later}');
    INSERT INTO market_data.close_sessions VALUES('{catalog}','test_sql_hidden','Synthetic hidden');
    INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
     calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
    VALUES('{catalog}','test_sql_hidden',1,'published',1,'{later}',{reference},'test_london_days','17:30','2020-12-01','2021-12-01','{approver}','{uuid4()}','{{}}','{approver}','{uuid4()}');
    INSERT INTO market_data.close_session_curves VALUES('{catalog}','test_sql_hidden',1,'{later}','future');
    COMMIT;""")
    hidden_close = str(uuid4())
    query(f"""INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,
     publication_order,publication_xid,session_code,session_version,business_date)
    SELECT '{catalog}','{hidden_close}','{later}',{reference},'Synthetic hidden close','2021-01-15T17:30:00Z','retained_input','{approver}','{uuid4()}','{{}}',
     coalesce(max(publication_order),0)+1,pg_current_xact_id(),'test_sql_hidden',1,'2021-01-15' FROM market_data.market_closes WHERE catalog_id='{catalog}';""")
    hidden = query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');SELECT security.use_market_context('{catalog}','{context}');SELECT security.close_session_hidden_days('{catalog}','test_sql_hidden')::text;COMMIT;").splitlines()[-1]
    assert hidden == '{2021-01-15}', hidden
    assert as_approver(f"security.close_session_hidden_days('{catalog}','test_sql_hidden')::text") == '{}', 'Current working data cannot see the close'
    print('PASS a day whose close in force the reader cannot see is named as hidden', flush=True)
