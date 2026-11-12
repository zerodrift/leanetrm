"""Selected observation history for a populated, recorded synthetic close."""
import json
from uuid import uuid4
from check_reference_editing import main

KNOWLEDGE_WORK="SELECT 'WORK|'||coalesce(sum(seq_tup_read+idx_tup_fetch),0) FROM pg_stat_xact_user_tables WHERE schemaname='market_data' AND relname IN('knowledge_events','market_knowledge_receipts');"


def check_small_point_scope(query,manifest):
    catalog,revision,tenant,workspace=[manifest[k] for k in ('catalogId','revisionId','tenantId','workspaceId')]
    pins=query(f"SELECT id FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}' AND observed_at<'2020-12-18' ORDER BY id LIMIT 72;").splitlines()
    assert pins
    original=pins[0]
    query(f"""INSERT INTO market_data.observations SELECT copied.* FROM market_data.observations o
    CROSS JOIN generate_series(1,4096)n
    CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
    'id',gen_random_uuid(),'source_event_id','synthetic_point_history_'||n,'source_revision_id','first','corrects_id',NULL,'received_at',clock_timestamp()))copied
    WHERE(o.catalog_id,o.id)=('{catalog}','{original}');""")
    def issue():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
    token=issue();context=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);COMMIT;")
    selected='ARRAY['+','.join("'"+pin+"'::uuid" for pin in pins)+']'
    for plan in ['force_generic_plan','force_custom_plan']:
        token=issue()
        lines=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SET LOCAL plan_cache_mode={plan};SET LOCAL statement_timeout='5s';SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context}');"+KNOWLEDGE_WORK+
            f"SELECT 'ROWS|'||count(*) FROM security.read_observation_pins('{catalog}','{revision}',{selected});"+KNOWLEDGE_WORK+"ROLLBACK;").splitlines()
        work=[int(line.split('|')[1]) for line in lines if line.startswith('WORK|')]
        assert 'ROWS|'+str(len(pins)) in lines,lines
        assert work[1]-work[0]<=2*len(pins)+64,(plan,len(pins),work[1]-work[0])
        print(f'PASS cold {plan}: {len(pins)} selected pins read {work[1]-work[0]} event/receipt tuples despite 4096 unrelated facts',flush=True)


def check_reference_version_scope(query,manifest):
    catalog,tenant,workspace,reference_catalog,reference_revision=[manifest[k] for k in ('catalogId','tenantId','workspaceId','referenceCatalogId','referenceRevisionId')]
    def issue():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
    def capture():
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issue()}');SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);COMMIT;")
    def seal(revision,reference):
        query(f"INSERT INTO market_data.revisions VALUES('{catalog}','{revision}','{reference_catalog}','{reference}','draft',0);"
              f"INSERT INTO market_data.revision_periods VALUES('{catalog}','{revision}','2020-12-01','2021-12-01');"
              f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{revision}');")
    def using(reference):
        return query(f"SELECT id FROM market_data.revisions WHERE(catalog_id,reference_catalog_id,reference_revision_id)=('{catalog}','{reference_catalog}','{reference}') ORDER BY id;").splitlines()
    earlier=capture()
    # Market revisions of the fixture's reference version, published after every fixture observation. Their ids sort
    # before the fixture's revisions, so a lookup that walks revisions in id order meets every later publication first.
    later=[f'00000000-0000-4000-8000-{number:012x}' for number in range(1,17)]
    for revision in later:seal(revision,reference_revision)
    assert using(reference_revision)[:len(later)]==later
    # A reference version that only a still later publication uses: the earlier view must not know it.
    unpublished='00000000-0000-4000-8000-0000000000aa'
    query(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{reference_catalog}','{unpublished}','synthetic_later_reference');"
          f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{reference_catalog}','{unpublished}');")
    seal('00000000-0000-4000-8000-0000000000ab',unpublished)
    events=int(query(f"SELECT count(*) FROM market_data.knowledge_events WHERE catalog_id='{catalog}';"))
    for view,context,published in [('before the later publications',earlier,False),('after them',capture(),True)]:
        for reference,visible in [(reference_revision,True),(unpublished,published)]:
            lines=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SET LOCAL statement_timeout='5s';SELECT security.use_context('{issue()}');SELECT security.use_market_context('{catalog}','{context}');"+KNOWLEDGE_WORK+
                f"SELECT 'VISIBLE|'||security.reference_version_visible('{reference_catalog}','{reference}');"+KNOWLEDGE_WORK+"ROLLBACK;").splitlines()
            work=[int(line.split('|')[1]) for line in lines if line.startswith('WORK|')]
            revisions=len(using(reference))
            assert f'VISIBLE|{str(visible).lower()}' in lines,(view,reference,lines)
            assert work[1]-work[0]<=2*revisions+64,(view,revisions,events,work[1]-work[0])
            print(f"PASS reference version is {'visible' if visible else 'unknown'} in a view captured {view}: using market revisions {revisions}, event/receipt tuples read {work[1]-work[0]}, knowledge events {events}",flush=True)


def run_checks(query,manifest):
    check_reference_version_scope(query,manifest)
    check_small_point_scope(query,manifest)
    catalog,source_revision,tenant,workspace,reference_catalog,reference_revision=[manifest[k] for k in ['catalogId','revisionId','tenantId','workspaceId','referenceCatalogId','referenceRevisionId']]
    # Exact lookups gain nothing from JIT, and loading LLVM on a cold host can consume the read's deadline.
    for signature in ['security.read_observation_facts(uuid,uuid,uuid[])','security.observation_visibility_scope(uuid,uuid,uuid[])']:
        assert query(f"SELECT 'jit=off'=ANY(proconfig) FROM pg_proc WHERE oid='{signature}'::regprocedure;")=='t',signature
    # Numeric inputs give the close distinct, valid series identities.
    revision,close_id=str(uuid4()),str(uuid4());count=32768
    original=query(f"SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) WHERE o.catalog_id='{catalog}' AND o.definition_revision_id='{source_revision}' AND s.contract_id IS NULL AND o.observed_at::date='2020-12-17' LIMIT 1;")
    query(f"INSERT INTO market_data.revisions VALUES('{catalog}','{revision}','{reference_catalog}','{reference_revision}','draft',0);INSERT INTO market_data.revision_periods VALUES('{catalog}','{revision}','2020-12-01','2021-12-01');")
    for first in range(1,count+1,4096):
        last=min(first+4095,count)
        query(f"""INSERT INTO market_data.series SELECT copied.* FROM market_data.series s JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id)=(s.catalog_id,s.revision_id,s.id) CROSS JOIN generate_series({first},{last})n CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||jsonb_build_object('revision_id','{revision}','id',md5('scale_series_'||n)::uuid,'code','scale_'||n))copied WHERE(o.catalog_id,o.id)=('{catalog}','{original}');""")
    query(f"ANALYZE market_data.series;UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{revision}';")
    for first in range(1,count+1,4096):
        last=min(first+4095,count)
        query(f"""INSERT INTO market_data.observations SELECT copied.* FROM market_data.observations o CROSS JOIN generate_series({first},{last})n CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id',md5('scale_observation_'||n)::uuid,'definition_revision_id','{revision}','series_id',md5('scale_series_'||n)::uuid,'source_event_id','synthetic_scale_selected_'||n,'source_revision_id','first','corrects_id',NULL,'received_at',clock_timestamp()))copied WHERE(o.catalog_id,o.id)=('{catalog}','{original}');""")
    actor=query("SELECT principal_id FROM security.identity_bindings WHERE subject='approver';")
    query(f"""BEGIN;
    INSERT INTO market_data.close_sessions VALUES('{catalog}','scale_close','Synthetic scaled close');
    INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
    VALUES('{catalog}','scale_close',1,'published',1,'{revision}','{reference_catalog}','{reference_revision}','test_utc_every_day','23:00','2020-12-01','2021-12-01','{actor}','{uuid4()}','{{}}','{actor}','{uuid4()}');
    INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,publication_order,publication_xid,session_code,session_version,business_date)
    VALUES('{catalog}','{close_id}','{revision}','{reference_catalog}','{reference_revision}','Synthetic scaled close','2020-12-17 23:00Z','retained_input','{actor}','{uuid4()}','{{}}',1,pg_current_xact_id(),'scale_close',1,'2020-12-17');
    INSERT INTO market_data.market_close_pins SELECT '{catalog}','{close_id}','{revision}',series_id,id,'variable' FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}';
    COMMIT;""")
    pins=json.loads(query(f"SELECT json_agg(id) FROM market_data.observations WHERE definition_revision_id='{revision}';"));array='ARRAY['+','.join("'"+p+"'::uuid" for p in pins)+']'
    def issue():return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")

    # Corrections share the complete fact identity; other events on the same series do not.
    corrections='ARRAY['+','.join("'"+pin+"'::uuid" for pin in pins[:16])+']'
    query(f"""INSERT INTO market_data.observations SELECT copied.* FROM market_data.observations o
    CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
    'id',gen_random_uuid(),'source_revision_id','second','corrects_id',o.id,'received_at',clock_timestamp()))copied
    WHERE o.catalog_id='{catalog}' AND o.id=ANY({corrections});
    INSERT INTO market_data.observations SELECT copied.* FROM market_data.observations o
    CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
    'id',gen_random_uuid(),'source_event_id','synthetic_unrelated_'||o.id,'source_revision_id','first','corrects_id',NULL,'received_at',clock_timestamp()))copied
    WHERE o.catalog_id='{catalog}' AND o.id=ANY({corrections});""")
    array+='||'+corrections
    token=issue();context=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);COMMIT;")
    def read(expression,plan,selected_context,deadline='5s'):
        token=issue()
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SET LOCAL plan_cache_mode={plan};SET LOCAL statement_timeout='{deadline}';SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{selected_context}');SELECT security.use_close_session('{catalog}','scale_close');{expression};DO $mode$BEGIN IF current_setting('plan_cache_mode')<>'{plan}' THEN RAISE EXCEPTION 'Fact read changed caller planning mode';END IF;END$mode$;ROLLBACK;")
    # Compare complete ordered rows against ordinary RLS, including duplicates and corrections.
    # The deadline covers the read and its delivery; the comparison happens here.
    for plan in ['force_generic_plan','force_custom_plan']:
        rows="SELECT to_jsonb(o) FROM "
        actual=read(rows+f"security.read_observation_facts('{catalog}','{revision}',{array}) o",plan,context).splitlines()
        expected=read(f"WITH anchors AS MATERIALIZED(SELECT DISTINCT series_id,source_code,source_event_id,observed_at,role FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}' AND id=ANY({array})) "+rows+f"(SELECT o.* FROM market_data.observations o JOIN anchors a USING(series_id,source_code,source_event_id,observed_at,role) WHERE o.catalog_id='{catalog}' AND o.definition_revision_id='{revision}' ORDER BY o.observed_at,o.received_at,o.id) o",plan,context,deadline='0').splitlines()
        assert actual==expected,(plan,len(actual),len(expected))
        assert len(actual)==count+16
        print(f'PASS {plan}: {count} distinct close pins match complete ordered native-RLS fact rows within the 5-second statement deadline',flush=True)

    # The selected close stays fixed while a later certified cutoff includes more
    # unrelated committed receipts. Exact event/receipt probes must not expand.
    one=f"ARRAY['{pins[0]}'::uuid]"
    selected='ARRAY['+','.join("'"+pin+"'::uuid" for pin in pins[:72])+']'
    before={}
    def measured(plan,selected_context,reader):
        requested,expected=(one,2) if reader=='read_observation_facts' else (selected,72)
        lines=read(KNOWLEDGE_WORK+f"SELECT 'ROWS|'||count(*) FROM security.{reader}('{catalog}','{revision}',{requested});"+KNOWLEDGE_WORK,plan,selected_context).splitlines()
        work=[int(line.split('|')[1]) for line in lines if line.startswith('WORK|')]
        assert 'ROWS|'+str(expected) in lines,lines
        return work[1]-work[0]
    readers=['read_observation_facts','read_observation_pins']
    for plan in ['force_generic_plan','force_custom_plan']:
        for reader in readers:before[plan,reader]=measured(plan,context,reader)
    query(f"""INSERT INTO market_data.observations SELECT copied.* FROM market_data.observations o
    CROSS JOIN generate_series(1,1024)n
    CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
    'id',gen_random_uuid(),'source_event_id','synthetic_known_receipt_'||n,'source_revision_id','first','corrects_id',NULL,'received_at',clock_timestamp()))copied
    WHERE(o.catalog_id,o.id)=('{catalog}','{pins[0]}');""")
    token=issue();context=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.capture_date_context('{catalog}','2020-12-17',NULL);COMMIT;")
    for plan in ['force_generic_plan','force_custom_plan']:
        for reader in readers:
            after=measured(plan,context,reader)
            assert after<=before[plan,reader]+64,(reader,plan,before[plan,reader],after)
            print(f'PASS {reader}/{plan}: selected event/receipt tuple work stays bounded {before[plan,reader]}/{after} after 1024 additional known receipts',flush=True)


if __name__=='__main__':
    main(checks=run_checks)
