"""Qualify delivery-source metadata through authenticated HTTP in the owned fixture."""
from uuid import uuid4
from datetime import datetime, timedelta, timezone
import json


def run_checks(client, query, manifest):
    r, rr = manifest['referenceCatalogId'], manifest['referenceRevisionId']
    catalog, _ = client.call('/api/power-imports')
    source = next(s for s in catalog['sources'] if s['name'] == 'Synthetic delivery prices')
    assert source['commodityKey'] == f'{r}:{rr}:test_power'
    assert source['commodityName'] == query(f"SELECT name FROM ref_data.commodities WHERE(catalog_id,revision_id,code)=('{r}','{rr}','test_power');")
    assert source['locationKey'] == f'{r}:{rr}:test_hub_a'
    assert source['locationName'] == query(f"SELECT name FROM ref_data.locations WHERE(catalog_id,revision_id,code)=('{r}','{rr}','test_hub_a');")
    assert source['provider'] == 'PJM' and source['marketName'] == 'Day-ahead'
    assert source['priceUnit'] == 'USD/MWh'
    assert source['availableDates'] == ['2026-11-02'] and source['availableThrough'] == {'2026-11-02': '2026-11-02'}
    assert source['marketClock'].startswith('America/New_York · tzdb ')
    attempt = next(a for a in catalog['attempts'] if a['jobName'] == 'Synthetic paused schedule' and a['fromDate'] == '2026-11-02' and a['throughDate'] == '2026-11-02')
    assert attempt['provider'] == 'PJM' and attempt['fromDate'] == '2026-11-02'
    assert attempt['throughDate'] == '2026-11-02' and attempt['finishedAt'] is not None
    # Identical codes in a different retained reference revision must not supply
    # names or units for the source's exact foreign keys.
    shadow, alternate, alternate_catalog = str(uuid4()), str(uuid4()), str(uuid4())
    query(f"INSERT INTO ref_data.catalogs VALUES('{alternate_catalog}','synthetic_delivery_metadata','Synthetic alternate reference metadata');INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{alternate_catalog}','{shadow}','synthetic_delivery_metadata');INSERT INTO security.reference_catalog_scopes VALUES('{alternate_catalog}','{manifest['tenantId']}','{manifest['workspaceId']}','read_only');")
    replacements={('commodities','name'):('test_power',"'Synthetic alternate commodity'"),
                  ('locations','name'):('test_hub_a',"'Synthetic alternate location'"),
                  ('currencies','display_code'):('usd',"'XTS'"),('units','symbol'):('megawatt_hour',"'AltMWh'"),
                  ('clock_rules','kind'):('synthetic_eastern',"'fixed_offset'"),
                  ('clock_rules','fixed_offset_seconds'):('synthetic_eastern','-18000'),
                  ('clock_rules','iana_zone'):('synthetic_eastern','NULL'),('clock_rules','tzdb_version'):('synthetic_eastern','NULL')}
    for table in ['dimensions','currencies','units','commodities','measurement_bases','quantity_specifications','clock_rules','location_types','locations']:
        columns = query(f"SELECT string_agg(quote_ident(column_name),',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema='ref_data' AND table_name='{table}';")
        values=[]
        for column in columns.split(','):
            change=replacements.get((table,column))
            values.append(f"'{alternate_catalog}'::uuid" if column=='catalog_id' else f"'{shadow}'::uuid" if column=='revision_id' else f"CASE WHEN code='{change[0]}' THEN {change[1]} ELSE {column} END" if change else column)
        selected=','.join(values)
        query(f"INSERT INTO ref_data.{table}({columns}) SELECT {selected} FROM ref_data.{table} WHERE(catalog_id,revision_id)=('{r}','{rr}');")
    query(f"""UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{alternate_catalog}','{shadow}');
    INSERT INTO power_data.sources SELECT '{alternate}',catalog_id,'Synthetic alternate metadata source',provider,dataset,market,'synthetic-alternate-point',minutes,'{alternate_catalog}','{shadow}',location_code,clock_code,currency_code,quantity_code FROM power_data.sources WHERE id='{source['id']}';""")
    try:
        after, _ = client.call('/api/power-imports')
        original = next(s for s in after['sources'] if s['id'] == source['id'])
        assert not any(s['id'] == alternate for s in after['sources']), 'A configured source without prices became selectable'
        empty, _ = client.call('/api/power-imports/period', {'sourceId':alternate,'fromDate':'2026-11-02','throughDate':'2026-11-02','knownAt':after['knownAt']})
        assert empty['rows'] == []
        other = empty['source']
        assert original == source, 'A different reference revision changed exact source metadata'
        assert other['commodityKey'] == f'{alternate_catalog}:{shadow}:test_power'
        assert other['commodityName'] == 'Synthetic alternate commodity'
        assert other['locationName'] == 'Synthetic alternate location' and other['priceUnit'] == 'XTS/AltMWh'
        assert other['marketClock'] == 'UTC−05:00'
        try:
            query(f"UPDATE power_data.sources SET reference_revision_id='{shadow}',reference_catalog_id='{alternate_catalog}' WHERE id='{source['id']}';")
        except RuntimeError as error:
            assert 'Knowledge receipts are immutable' in str(error)
        else:
            raise AssertionError('A retained delivery source accepted different exact units and references')
    finally:
        query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{alternate_catalog}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    result, _ = client.call('/api/power-imports/period', {'sourceId':source['id'],'fromDate':'2026-11-02','throughDate':'2026-11-02','knownAt':catalog['knownAt']})
    assert result['priceUnit'] == 'USD/MWh' and len(result['rows']) == 24
    # A normal collector correction retains all observed decimal places. History
    # display must not silently apply an unrelated four-place rounding policy.
    precise = '-17.125456789012345678'
    start = datetime(2026,11,2,5,tzinfo=timezone.utc)
    payload = json.dumps([{'sourceId':source['id'],'rows':[
        {'start':(start+timedelta(hours=h)).isoformat(),'end':(start+timedelta(hours=h+1)).isoformat(),'price':precise,'revision':2}
        for h in range(24)]}])
    actor, job = str(uuid4()), str(uuid4())
    query(f"""CREATE ROLE synthetic_history_collector LOGIN IN ROLE openetrm_collector;
    INSERT INTO security.principals VALUES('{actor}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{manifest['tenantId']}','{manifest['workspaceId']}','{actor}','synthetic_history_collector','Synthetic history collector',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{actor}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='market_data_editor';
    INSERT INTO power_data.collectors VALUES('synthetic_history_collector','{actor}','{manifest['tenantId']}','{manifest['workspaceId']}','{manifest['catalogId']}');
    INSERT INTO power_data.jobs VALUES('{job}',gen_random_uuid(),'Synthetic precise history correction','synthetic_history_collector','{manifest['catalogId']}','PJM',true,false,'0 8 * * *','UTC',-7,-1);
    INSERT INTO power_data.job_sources VALUES('{job}','{source['id']}','{manifest['catalogId']}');""")
    def collect():
        query(f"SET SESSION AUTHORIZATION synthetic_history_collector;SELECT power_data.worker_lock();DO $$DECLARE attempt uuid;BEGIN attempt=power_data.begin_attempt('{job}','2026-11-02','2026-11-02');PERFORM power_data.finish_attempt(attempt,'{payload}'::jsonb,NULL);END$$;")
        query(f"SELECT power_data.certify('{manifest['catalogId']}');")
    collect()
    latest, _ = client.call('/api/power-imports')
    period = {'sourceId':source['id'],'fromDate':'2026-11-02','throughDate':'2026-11-02'}
    exact, _ = client.call('/api/power-imports/period', {**period,'knownAt':latest['knownAt']})
    assert len(exact['rows']) == 24 and all(row['price']['value'] == precise for row in exact['rows'])
    assert all(row['formattedPrice'] == precise for row in exact['rows']), 'Delivery history display rounded an exact observed price without an explicit policy'
    assert 'overlays' not in exact
    # The earlier cutoff still reads every interval as it was known then.
    def facts(read): return [(row['start'], row['price']['value'], row['batchId'], row['knownAt']) for row in read['rows']]
    earlier, _ = client.call('/api/power-imports/period', {**period,'knownAt':catalog['knownAt']})
    assert facts(earlier) == facts(result) and {row['price']['value'] for row in earlier['rows']} == {'-17.125'}, 'A later correction changed a read at an earlier cutoff'
    assert earlier['summary']['average'] == '-17.125'
    # D229: collecting the same prices again publishes nothing new.
    batches = query('SELECT count(*) FROM power_data.batches;')
    collect()
    assert query(f"SELECT state FROM power_data.attempts WHERE job_id='{job}' ORDER BY started_at DESC LIMIT 1;") == 'succeeded'
    assert query('SELECT count(*) FROM power_data.batches;') == batches, 'An identical collection published a new batch'
    repeated, _ = client.call('/api/power-imports')
    again, _ = client.call('/api/power-imports/period', {**period,'knownAt':repeated['knownAt']})
    assert facts(again) == facts(exact), 'An identical collection changed the knowledge time of recorded prices'
    # One synthetic source has prices, through 2026-11-02 only.
    assert repeated['opening'] == {'sourceId':source['id'],'fromDate':'2026-11-02','throughDate':'2026-11-02','policy':'newest_available_delivery_day_v1'}, repeated['opening']
    newest = query("SELECT state||'|'||from_date||'|'||through_date||'|'||coalesce(error_code,'-') FROM power_data.attempts ORDER BY started_at DESC,id LIMIT 1;")
    latest_attempt = repeated['collection']['latestAttempt']
    assert '|'.join([latest_attempt['state'],latest_attempt['fromDate'],latest_attempt['throughDate'],latest_attempt['errorCode'] or '-']) == newest
    assert repeated['collection']['jobs'] == int(query('SELECT count(*) FROM power_data.jobs;'))
    assert repeated['collection']['enabledJobs'] == int(query('SELECT count(*) FROM power_data.jobs WHERE enabled;'))
    first_hour = exact['rows'][0]['start']
    assert exact['summary'] == {'intervals':24,'expectedIntervals':24,'missingIntervals':0,'low':{'price':precise,'start':first_hour},'high':{'price':precise,'start':first_hour},
                                'average':precise,'latestKnownAt':exact['rows'][0]['knownAt'],'policy':'exact_rational_half_even_18_v1'}, exact['summary']
    ranged, _ = client.call('/api/power-imports/period', {**period,'knownAt':repeated['knownAt'],'overlays':[{'code':'rolling_range_v1','parameters':{'window':2}}]})
    overlay = ranged['overlays'][0]
    assert overlay['policy'] == 'exact_rational_half_even_18_v1' and [v['at'] for v in overlay['series']['high']] == [row['start'] for row in exact['rows']]
    assert [v['value'] for v in overlay['series']['high']] == [None] + [precise] * 23 and overlay['series']['low'] == overlay['series']['high']
    # The chart states a point per expected interval: every published hour, then each hour of 2026-11-03, which has no prices.
    assert exact['chart'] == {'points':[{'at':row['start'],'value':precise,'unavailableReason':None} for row in exact['rows']]}, exact.get('chart')
    two_days, _ = client.call('/api/power-imports/period', {**period,'throughDate':'2026-11-03','knownAt':repeated['knownAt']})
    points = two_days['chart']['points']
    assert len(two_days['rows']) == 24 and two_days['summary']['expectedIntervals'] == 48 and len(points) == 48, two_days['summary']
    assert points[:24] == exact['chart']['points'] and all(p['value'] is None and p['unavailableReason'] == 'not_published_at_cutoff' for p in points[24:]), points[24:]
    assert [datetime.fromisoformat(p['at'].replace('Z','+00:00')) for p in points[24:]] == [start+timedelta(hours=24+h) for h in range(24)]
    refusal, _ = client.call('/api/power-imports/period', {**period,'knownAt':repeated['knownAt'],'overlays':[{'code':'simple_moving_average_v1','parameters':{}}]}, expected=422)
    assert refusal['code'] == 'overlay_parameter'
    scope_mode=query(f"SELECT access_mode FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{r}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    assert scope_mode in ('owner','read_only')
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{r}','{manifest['tenantId']}','{manifest['workspaceId']}');")
    try:
        denied, _ = client.call('/api/power-imports')
        assert denied['sources'] == [], 'Revoked reference scope leaked source metadata'
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{r}','{manifest['tenantId']}','{manifest['workspaceId']}','{scope_mode}');")
    client.call('/api/market/context', {'effectiveOn':'2020-12-18','mode':'current','selection':'latest_correction'})
    client.call('/api/power-imports', expected=422)
    client.call('/api/market/current', {})
    print('PASS HTTP delivery metadata: exact commodity/location/clock/unit references and observed prices, attempts, scope revocation and historical rejection', flush=True)
    print('PASS HTTP delivery periods: reads as known at each cutoff, an identical collection publishes nothing, newest-day opening, collection outcome, exact summary, a chart point per expected interval and period overlays', flush=True)
