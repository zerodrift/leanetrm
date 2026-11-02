"""Synthetic checks for service-only collection and commit-certified corrections."""
from uuid import uuid4
from datetime import timedelta
import json


def malformed_report_window(today):
    # Keep the live worker's calendar separate from the fixed Eastern DST oracles.
    offset = -1
    while (today + timedelta(days=offset)).isoformat() in ('2026-11-01', '2026-11-02'):
        offset -= 1
    return today + timedelta(days=offset), offset


def run_checks(query,manifest,dotnet,root,environment):
    assert query("SELECT to_regclass('power_data.sources') IS NOT NULL;")=='t'
    assert query("SELECT has_function_privilege('openetrm_runtime','power_data.begin_attempt(uuid,date,date)','EXECUTE');")=='f'
    assert query("SELECT has_table_privilege('openetrm_runtime','power_data.prices','INSERT');")=='f'
    assert query("SELECT has_table_privilege('openetrm_collector','power_data.prices','INSERT');")=='f'
    c,r,rr,t,w=[manifest[k] for k in ['catalogId','referenceCatalogId','referenceRevisionId','tenantId','workspaceId']]
    actor,source,job=[str(uuid4()) for _ in range(3)]
    query(f"""CREATE ROLE synthetic_collector LOGIN IN ROLE openetrm_collector;
    INSERT INTO security.principals VALUES('{actor}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{t}','{w}','{actor}','synthetic_collector','Synthetic collector',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{actor}',id,'{t}','{w}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='market_data_editor';
    INSERT INTO power_data.collectors VALUES('synthetic_collector','{actor}','{t}','{w}','{c}');
    INSERT INTO power_data.sources VALUES('{source}','{c}','Synthetic delivery prices','PJM','da_hrl_lmps','DA','51288',60,'{r}','{rr}','test_hub_a','synthetic_eastern','usd','test_power_quantity');
    INSERT INTO power_data.jobs VALUES('{job}',gen_random_uuid(),'Synthetic paused schedule','synthetic_collector','{c}','PJM',true,false,'0 8 * * *','UTC',-7,-1);
    INSERT INTO power_data.job_sources VALUES('{job}','{source}','{c}');""")
    query(f"INSERT INTO power_data.operating_days VALUES('{r}','{rr}','synthetic_eastern','2026-11-02','2026-11-02T05:00:00Z','2026-11-03T05:00:00Z','{'a'*64}');")
    from datetime import datetime,timedelta,timezone
    first_at=datetime(2026,11,2,5,tzinfo=timezone.utc)
    def payload(price):return json.dumps([{'sourceId':source,'rows':[{'start':(first_at+timedelta(hours=h)).isoformat(),'end':(first_at+timedelta(hours=h+1)).isoformat(),'price':price,'revision':1} for h in range(24)]}])
    def publish(price,extra=''):
        return query(f"""SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();
        DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');{extra}
        PERFORM power_data.finish_attempt(a,'{payload(price)}'::jsonb,NULL);END$$;""")
    # The job's scheduler remains paused while an explicit operator run is supported.
    publish('-17.125')
    assert query("SELECT count(*) FROM power_data.batches;")=='1'
    ordinary=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{t}','{w}',NULL,'preview_runtime',60);")
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{ordinary}');SELECT security.capture_date_context('{c}','2026-11-02',NULL);COMMIT;")
    query(f"SELECT power_data.certify('{c}');")
    first=query('SELECT known_at::text FROM power_data.receipts ORDER BY sequence LIMIT 1;')
    def available(cutoff):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{t}','{w}',NULL,'preview_runtime',60);")
        value=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT power_data.available_dates('{cutoff}')::text;COMMIT;")
        return json.loads(value)
    # Availability comes from certified complete price days, never attempt ranges.
    empty_source=str(uuid4())
    query(f"INSERT INTO power_data.sources SELECT '{empty_source}',catalog_id,'Synthetic source without prices',provider,dataset,market,'NO_DATA',minutes,reference_catalog_id,reference_revision_id,location_code,clock_code,currency_code,quantity_code FROM power_data.sources WHERE id='{source}';")
    dates=available(first)
    assert len(dates)==1 and dates[0]['id']==source and dates[0]['availableDates']==['2026-11-02'],dates
    before=query(f"SELECT ('{first}'::timestamptz-interval '1 microsecond')::text;")
    assert available(before)==[]
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{t}','{w}',NULL,'preview_runtime',60);")
    hidden=query(f"BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{r}' AND tenant_id='{t}' AND workspace_id='{w}';SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');SELECT power_data.available_dates('{first}')::text;ROLLBACK;")
    assert json.loads(hidden)==[]
    publish('0')
    query(f"SELECT power_data.certify('{c}');")
    second=query('SELECT known_at::text FROM power_data.receipts ORDER BY sequence DESC LIMIT 1;')
    publish('-17.125')
    query(f"SELECT power_data.certify('{c}');")
    assert query('SELECT count(*) FROM power_data.batches;')=='3'
    def read(cutoff):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{t}','{w}',NULL,'preview_runtime',60);")
        value=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT power_data.read_prices('{source}','2026-11-02T05:00:00Z','2026-11-03T05:00:00Z','{cutoff}')::text;COMMIT;")
        return json.loads(value)['rows'][0]['price']
    assert read(first)=='-17.125'
    assert read(second)=='0'
    latest=query('SELECT known_at::text FROM power_data.receipts ORDER BY sequence DESC LIMIT 1;')
    assert read(latest)=='-17.125'
    # D238: collecting identical prices again succeeds and publishes nothing.
    publish('-17.125')
    assert query(f"SELECT state FROM power_data.attempts WHERE job_id='{job}' ORDER BY started_at DESC LIMIT 1;")=='succeeded'
    assert query('SELECT count(*) FROM power_data.batches;')=='3'
    # A changed price, or a changed provider revision, is a new revision of that interval alone.
    changed=json.loads(payload('-17.125'));changed[0]['rows'][3]['price']='-17.50';changed[0]['rows'][7]['revision']=2
    revised=query(f"""BEGIN;SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();
    DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');PERFORM power_data.finish_attempt(a,'{json.dumps(changed)}'::jsonb,NULL);END$$;
    RESET SESSION AUTHORIZATION;
    SELECT string_agg(to_char(p.starts_at AT TIME ZONE 'UTC','HH24')||'='||p.price::text||'/'||p.provider_revision,',' ORDER BY p.starts_at) FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id WHERE b.sequence=(SELECT max(sequence) FROM power_data.batches);ROLLBACK;""")
    assert revised.splitlines()[-1]=='08=-17.50/1,12=-17.125/2',revised
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{t}','{w}',NULL,'preview_runtime',60);")
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT power_data.read_prices('{source}','2026-10-02T04:00:00Z','2026-11-02T05:00:00Z','{latest}') IS NOT NULL;COMMIT;")
    # Independent calendar oracles: PJM 25-hour fall day and ERCOT 92/100 quarter-hours.
    query(f"INSERT INTO power_data.operating_days VALUES('{r}','{rr}','synthetic_eastern','2026-11-01','2026-11-01T04:00:00Z','2026-11-02T05:00:00Z','{'a'*64}');")
    fall_start=datetime(2026,11,1,4,tzinfo=timezone.utc)
    fall_payload=json.dumps([{'sourceId':source,'rows':[{'start':(fall_start+timedelta(hours=h)).isoformat(),'end':(fall_start+timedelta(hours=h+1)).isoformat(),'price':'-3.125','revision':1} for h in range(25)]}])
    query(f"SET SESSION AUTHORIZATION synthetic_collector;BEGIN;SELECT power_data.worker_lock();DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{job}','2026-11-01','2026-11-01');PERFORM power_data.finish_attempt(a,'{fall_payload}'::jsonb,NULL);END$$;ROLLBACK;")
    for day,start,count in [('2026-03-08',datetime(2026,3,8,6,tzinfo=timezone.utc),92),('2026-11-01',datetime(2026,11,1,5,tzinfo=timezone.utc),100)]:
        ercot_source,ercot_job=str(uuid4()),str(uuid4())
        rows=[{'start':(start+timedelta(minutes=15*i)).isoformat(),'end':(start+timedelta(minutes=15*(i+1))).isoformat(),'price':'0','revision':None} for i in range(count)]
        values=json.dumps([{'sourceId':ercot_source,'rows':rows}])
        query(f"""BEGIN;
        INSERT INTO power_data.sources VALUES('{ercot_source}','{c}','Synthetic ERCOT settlement prices','ERCOT','np6-905-cd/spp_node_zone_hub','RT','HB_NORTH',15,'{r}','{rr}','test_hub_a','synthetic_central','usd','test_power_quantity');
        INSERT INTO power_data.jobs VALUES('{ercot_job}',gen_random_uuid(),'Synthetic ERCOT check','synthetic_collector','{c}','ERCOT',true,false,'0 8 * * *','UTC',-7,-1);
        INSERT INTO power_data.job_sources VALUES('{ercot_job}','{ercot_source}','{c}');
        INSERT INTO power_data.operating_days VALUES('{r}','{rr}','synthetic_central','{day}','{start.isoformat()}','{(start+timedelta(minutes=15*count)).isoformat()}','{'a'*64}');
        SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{ercot_job}','{day}','{day}');PERFORM power_data.finish_attempt(a,'{values}'::jsonb,NULL);END$$;ROLLBACK;""")
    # Malformed/duplicate input must roll back the whole batch, including attempt state.
    query(f"""SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();DO $$DECLARE a uuid;BEGIN
    a=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');
    BEGIN PERFORM power_data.finish_attempt(a,'[]'::jsonb,NULL);RAISE EXCEPTION 'Accepted empty batch';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'collection_input' THEN RAISE;END IF;END;
    PERFORM power_data.finish_attempt(a,NULL,'synthetic_failure');END$$;""")
    assert query('SELECT count(*) FROM power_data.batches;')=='3'
    def reject_raw(body,code):
        try:query(body);raise AssertionError('Accepted invalid raw collection operation')
        except RuntimeError as error:assert code in str(error),str(error)
    reject_raw(f"BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{r}' AND tenant_id='{t}' AND workspace_id='{w}';SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();SELECT power_data.begin_attempt('{job}','2026-11-02','2026-11-02');COMMIT;",'collection_reference_scope')
    assert query(f"BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{r}' AND tenant_id='{t}' AND workspace_id='{w}';SET SESSION AUTHORIZATION synthetic_collector;SELECT count(*) FROM power_data.sources;ROLLBACK;")=='0'
    invalid=json.loads(payload('2'));invalid[0]['rows']=invalid[0]['rows'][:-1]
    reject_raw(f"SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');PERFORM power_data.finish_attempt(a,'{json.dumps(invalid)}'::jsonb,NULL);END$$;",'collection_coverage')
    invalid=json.loads(payload('2'));invalid[0]['rows'][0]['revision']=None
    reject_raw(f"SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');PERFORM power_data.finish_attempt(a,'{json.dumps(invalid)}'::jsonb,NULL);END$$;",'collection_input')
    reject_raw(f"BEGIN;UPDATE market_data.knowledge_clocks SET timeline_id=timeline_id+1 WHERE catalog_id='{c}';SELECT power_data.certify('{c}');COMMIT;",'knowledge_timeline')
    # Even a valid old commit timestamp cannot enter an already certified cutoff.
    stale=str(uuid4())
    reject_raw(f"""BEGIN;
    INSERT INTO power_data.attempts(id,job_id,generation,worker_pid,security_generation,from_date,through_date,started_at,state,finished_at,error_code,catalog_id)
    SELECT '{stale}',job_id,generation,worker_pid,security_generation,from_date,through_date,started_at,state,finished_at,error_code,catalog_id FROM power_data.attempts WHERE state='succeeded' LIMIT 1;
    INSERT INTO power_data.batches(id,catalog_id,publication_xid) SELECT '{stale}','{c}',publication_xid FROM power_data.batches ORDER BY sequence LIMIT 1;
    SELECT power_data.certify('{c}');COMMIT;""",'knowledge_clock')
    assert query('SELECT count(*) FROM power_data.batches;')=='3'
    reject_raw(f"BEGIN;DELETE FROM security.market_catalog_scopes WHERE catalog_id='{c}';SET SESSION AUTHORIZATION synthetic_collector;SELECT count(*) FROM power_data.sources;COMMIT;",'collection_denied')
    reject_raw(f"""BEGIN;
    CREATE FUNCTION public.synthetic_price_delay() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN PERFORM pg_sleep(0.025);RETURN NEW;END$$;
    CREATE TRIGGER synthetic_price_delay BEFORE INSERT ON power_data.prices FOR EACH ROW EXECUTE FUNCTION public.synthetic_price_delay();
    UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.3 seconds' WHERE principal_id='{actor}';
    SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();
    DO $$DECLARE a uuid;BEGIN a=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');PERFORM power_data.finish_attempt(a,'{payload('9')}'::jsonb,NULL);END$$;COMMIT;""",'collection_denied')
    # A generation change fences an attempt while the same backend still holds its worker lock.
    try:
        query(f"""SET SESSION AUTHORIZATION synthetic_collector;SELECT power_data.worker_lock();
SELECT power_data.begin_attempt('{job}','2026-11-02','2026-11-02') AS fence_token \\gset
RESET SESSION AUTHORIZATION;
UPDATE power_data.jobs SET enabled=false WHERE id='{job}';UPDATE power_data.jobs SET enabled=true WHERE id='{job}';
SET SESSION AUTHORIZATION synthetic_collector;
SELECT power_data.finish_attempt(:'fence_token','{payload('5')}'::jsonb,NULL);""")
        raise AssertionError('Stale job generation published')
    except RuntimeError as error:assert 'collection_fence' in str(error)
    # Run the real managed worker with an injected, network-free malformed report.
    import os,subprocess
    from pathlib import Path
    from preview_fixture import installed_tzdb
    worker_day,worker_offset=malformed_report_window(datetime.now(timezone.utc).date())
    directory=root/'collector-check';directory.mkdir(mode=0o700)
    request=directory/'calendar.json';calendar_sql=directory/'calendar.sql'
    request.write_text(json.dumps({'referenceCatalogId':r,'referenceRevisionId':rr,'from':worker_day.isoformat(),'through':worker_day.isoformat(),'clocks':[{'code':'synthetic_eastern','kind':'iana','fixedOffsetSeconds':None,'ianaZone':'America/New_York','tzdbVersion':installed_tzdb()}]}))
    repository=Path(__file__).resolve().parents[1]
    subprocess.run([str(dotnet),str(repository/'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll'),'--render-power-days',str(request),str(calendar_sql)],cwd=repository,env=environment,check=True,timeout=30)
    query(calendar_sql.read_text());query(f"UPDATE power_data.jobs SET from_day={worker_offset},through_day={worker_offset} WHERE id='{job}';INSERT INTO power_data.request_gates VALUES('PJM',120,clock_timestamp());")
    connection=directory/'connection';connection.write_text(f'Host={root};Port=55439;Database=postgres;Username=synthetic_collector;SSL Mode=Disable');connection.chmod(0o600)
    config=directory/'collection.json';config.write_text(json.dumps({'connectionFile':str(connection),'providers':[{'provider':'PJM','baseUrl':'https://provider.synthetic.invalid/api','apiKey':'synthetic-key','requestsPerMinute':120,'tokenUrl':None,'clientId':None,'tokenScope':None,'accountId':None,'apiSecret':None}],'pjmPageSize':50,'ercotPageSize':1000,'maximumRows':100000,'timeoutSeconds':10}));config.chmod(0o600)
    project='tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj'
    subprocess.run([str(dotnet),'restore',project,'--locked-mode'],cwd=repository,env=environment,check=True,timeout=60,stdout=subprocess.DEVNULL)
    result=subprocess.run([str(dotnet),'run','--no-restore','--project',project,'--','--power-collection'],cwd=repository,env={**environment,'OPENETRM_COLLECTION_CONFIG':str(config)},text=True,capture_output=True,timeout=60)
    assert result.returncode==1 and 'COLLECTION_FAILED PJM' in result.stdout,(result.returncode,result.stdout,result.stderr)
    assert query(f"SELECT state||':'||error_code FROM power_data.attempts WHERE job_id='{job}' ORDER BY started_at DESC LIMIT 1;")=='failed:power_import'
    assert query('SELECT count(*) FROM power_data.batches;')=='3'
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{actor}';")
    query("SET SESSION AUTHORIZATION synthetic_collector;DO $$BEGIN PERFORM power_data.worker_lock();RAISE EXCEPTION 'Disabled service admitted';EXCEPTION WHEN insufficient_privilege THEN NULL;END$$;")
    assert query('SELECT count(*) FROM power_data.batches;')=='3'
    print('PASS power collection: service-only writes, paused schedule, atomic batch rejection, retained A-to-B-to-A corrections, certified cutoffs and disabled service denial',flush=True)
