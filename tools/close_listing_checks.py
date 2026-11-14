"""Current close choices can retire without changing retained financial facts."""
import json
from uuid import uuid4
from close_session_fixture import sql_session


def check_listings(query, runtime, reject, literal, manifest, close, close_request, pins, members):
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    code = 'test_source_inputs'
    def listing(request, expected, name, listed, login='trader'):
        return json.loads(runtime(f"SELECT security.publish_close_session_listing('{catalog}','{request}','{code}',1,{expected},'{name}',{str(listed).lower()})::text;", login))
    request = str(uuid4())
    first = listing(request, 0, 'Synthetic current close', True)
    assert first['listed'] and first['version'] == 1
    assert listing(str(uuid4()), 0, 'Synthetic current close', True) == first
    frame = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18'))
    capture_request = str(uuid4())
    snapshot_id = str(uuid4())
    def capture(request, name):
        return json.loads(runtime(f"SELECT security.capture_market_close('{catalog}','{revision}','{request}','{snapshot_id}','{name}','{code}','2020-12-18','exact_selected_inputs_v1','{frame['at']}',{literal(pins)},{literal(members)})::text;"))
    captured = capture(capture_request, 'Synthetic exact sources')
    assert captured['id'] == close
    reject('close_denied', lambda: listing(str(uuid4()), 1, 'Synthetic hidden close', False, 'viewer'))
    runtime(f"SELECT security.choose_close_session('{catalog}','{code}');")
    draft = dict(code=code, name='Synthetic '+code, calendarCode='test_utc_every_day', cutoff='16:00',
                 curves=['future'], effectiveFrom='2020-12-18', effectiveTo='2020-12-19')
    save_request = str(uuid4())
    def save(request):
        return json.loads(runtime(f"SELECT security.save_close_session('{catalog}','{revision}','{request}',{literal(draft)})::text;"))
    saved = save(save_request)
    retired = listing(str(uuid4()), 1, 'Synthetic hidden close', False)
    assert retired['version'] == 2 and not retired['listed']
    assert listing(request, 0, 'Synthetic current close', True) == first, 'A listing retry lost its first outcome'
    reject('close_conflict', lambda: listing(request, 0, 'Changed request', True))
    assert save(save_request) == saved, 'Retirement broke an accepted draft retry'
    assert capture(capture_request, 'Synthetic exact sources') == captured, 'Retirement broke an accepted capture retry'
    reject('close_session', lambda: capture(str(uuid4()), 'Synthetic changed capture'))
    reject('close_session', lambda: save(str(uuid4())))
    reject('close_session', lambda: runtime(f"SELECT security.publish_close_session('{catalog}','{uuid4()}','{code}',{saved['version']},{saved['generation']});"))
    assert runtime(f"SELECT security.close_session_choice('{catalog}');") == '', 'A retired remembered choice stayed available'
    reject('close_session', lambda: runtime(f"SELECT security.choose_close_session('{catalog}','{code}');"))
    choices = json.loads(runtime(f"SELECT security.close_session_catalog('{catalog}')::text;"))
    assert all(c['code'] != code for c in choices), 'A retired convention stayed in the picker'
    # The listing is current presentation metadata. Exact retained facts remain
    # authorized through their existing reader, without a substituted close.
    retained = json.loads(runtime(f"SELECT security.read_market_close_parts('{catalog}',ARRAY['{close}']::uuid[],NULL)::text;"))
    assert retained[0]['close_id'] == close
    def record(request, name):
        return runtime(f"SELECT security.register_market_close('{catalog}','{revision}','{request}','{name}','{code}','2020-12-18','{frame['at']}',{literal(pins)},{literal(members)});")
    assert record(close_request, 'Synthetic exact sources') == close, 'Retirement broke an accepted close retry'
    reject('close_session', lambda: record(str(uuid4()), 'Synthetic changed close'))
    assert query(f"SELECT count(*) FROM presentation.close_session_listings WHERE catalog_id='{catalog}' AND code='{code}';") == '2'
    reject('close_denied', lambda: runtime(f"SELECT security.close_session_is_listed('{uuid4()}','{code}');"))
    assert query(f"SELECT count(*) FROM presentation.close_session_listing_receipts WHERE catalog_id='{catalog}' AND code='{code}';") == '2'
    earlier = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-17'))
    active = 'test_current_inputs'
    sql_session(query, manifest, active, 'test_utc_every_day', earlier['at'][11:16], ['future'], ['trader'])
    ids = ','.join("'"+i+"'" for i in earlier['observationIds'])
    active_pins = json.loads(query(f"SELECT json_agg(json_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,'observationId',id,'inputUse','variable')) FROM market_data.observations WHERE catalog_id='{catalog}' AND id IN({ids});"))
    active_members = [m for m in members if m['definitionRevisionId'] == revision]
    active_close = runtime(f"SELECT security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic active close','{active}','2020-12-17','{earlier['at']}',{literal(active_pins)},{literal(active_members)});")
    print('PASS close listing: retirement hides current choices, refuses new writes, preserves retained inputs and first outcomes')
    return active_close
