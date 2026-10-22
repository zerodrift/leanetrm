"""An exact older schedule whose calendar is absent from the selected schedule view."""
import json
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame


def prepare(url, query, manifest, client=None):
    if client is None:
        client = PreviewClient(url)
        client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace/navigation'), '2020-12-18')
    catalog, revision, market_revision = str(uuid4()), str(uuid4()), str(uuid4())
    query(f"""BEGIN;
      INSERT INTO ref_data.catalogs VALUES('{catalog}','synthetic_older_schedule','Synthetic older schedule calendar');
      INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{revision}','v1');
      INSERT INTO ref_data.clock_rules VALUES('{catalog}','{revision}','utc','fixed_offset',0,NULL,NULL);
      INSERT INTO ref_data.calendars VALUES('{catalog}','{revision}','synthetic_days','utc',0,'2020-01-01','2022-01-01');
      UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{catalog}';
      INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');
      INSERT INTO market_data.revisions VALUES('{manifest['catalogId']}','{market_revision}','{catalog}','{revision}','draft',0);
      INSERT INTO market_data.revision_periods VALUES('{manifest['catalogId']}','{market_revision}','2020-01-01','2022-01-01');
      UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{manifest['catalogId']}','{market_revision}');
      COMMIT;""")
    schedule_terms = {'definition': {'name': 'Synthetic retained schedule', 'symbol': 'oe.agent_retained', 'firstMonth': '2020-12',
        'lastMonth': '2020-12', 'anchor': 'day', 'day': 18, 'shortMonth': 'reject', 'adjustment': 'unadjusted', 'calendar': None},
        'calendar': {'catalogId': catalog, 'revisionId': revision, 'code': 'synthetic_days'}, 'selection': 'absolute_index',
        'effectiveFrom': '2020-12-18', 'effectiveTo': '2020-12-19'}
    # Storage supports exact foreign schedule calendars; current UI authoring
    # offers its workspace catalog only. Install a valid typed draft fixture,
    # then validate and publish it through the ordinary business operation.
    schedule_id = str(uuid4())
    definition_json = json.dumps(schedule_terms['definition']).replace("'", "''")
    query(f"""BEGIN;
      INSERT INTO market_data.date_schedules VALUES('{manifest['catalogId']}','{schedule_id}','oe.agent_retained');
      INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,engine_version,calendar_catalog_id,calendar_revision_id,calendar_code)
       VALUES('{manifest['catalogId']}','{schedule_id}',1,1,'draft','{definition_json}'::jsonb,'absolute_index','2020-12-18','2020-12-19','date_schedule_v2','{catalog}','{revision}','synthetic_days');
      COMMIT;""")
    before = client.call('date-schedules/context', {'effectiveOn': '2020-12-18', 'knownAt': None, 'mode': 'current'})['context']
    client.call('profile', {'profile': 'approver'})
    schedule = client.call('date-schedules/publish', {'id': schedule_id, 'version': 1, 'generation': 1})
    definition = {'code': 'synthetic_retained_schedule', 'name': 'Synthetic retained schedule curve', 'output': 'out', 'nodes': [
        {'id': 'source', 'kind': 'source', 'parameters': {'curve': 'future'}, 'inputs': {}},
        {'id': 'select', 'kind': 'select', 'parameters': {'mode': 'positions', 'first': '1', 'count': '1', 'binding': 'context'}, 'inputs': {'source': 'source'}},
        {'id': 'dates', 'kind': 'dates', 'parameters': {'mode': 'schedule', 'schedule': schedule['id'] + ':1', 'first': '0', 'count': '1',
         'sampling': 'context_utc_time', 'corrections': 'exact_context_frame_history_latest_visible'}, 'inputs': {}},
        {'id': 'sample', 'kind': 'sample', 'parameters': {}, 'inputs': {'selection': 'select', 'dates': 'dates'}},
        {'id': 'out', 'kind': 'output', 'parameters': {}, 'inputs': {'values': 'sample'}}]}
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace/navigation'), '2020-12-18')
    saved = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': definition, 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    client.call('custom-curves/publish', {'requestId': str(uuid4()), **{k: saved[k] for k in ('id', 'version', 'generation')}})
    client.call('profile', {'profile': 'editor'})
    current_calendar = next(c['reference'] for c in client.call('date-schedules')['calendars'] if c['reference']['code'] == 'test_business')
    successor = client.call('date-schedules/save', {**schedule_terms, 'definition': {**schedule_terms['definition'], 'day': 17}, 'calendar': current_calendar, 'expectedState': 'published',
        **{k: schedule[k] for k in ('id', 'version', 'generation')}})
    client.call('profile', {'profile': 'approver'})
    client.call('date-schedules/publish', {k: successor[k] for k in ('id', 'version', 'generation')})
    frame = initial_frame(client.call('workspace/navigation'), '2020-12-18')
    pinned = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': definition, 'id': saved['id'], 'version': saved['version']})
    native = client.call('evaluate', {'snapshotId': frame['id'], 'curve': 'future'})
    price = pinned['points'][0]['price']
    assert pinned['points'][0]['dates'] == ['2020-12-18'], pinned
    assert Fraction(int(price['numerator']), int(price['denominator'])) == Fraction(native['points'][0]['value'])
    client.call('market/context/retained', {'contextId': before['contextId'], 'selection': 'latest_correction'})
    earlier = initial_frame(client.call('workspace/navigation'), '2020-12-18')
    refused = client.call('custom-curves/preview', {'snapshotId': earlier['id'], 'definition': definition}, expected=422)
    assert refused['code'] == 'schedule_context', refused
    client.call('market/context', {'effectiveOn': '2020-12-17', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    outside = initial_frame(client.call('workspace/navigation'), '2020-12-17')
    refused = client.call('custom-curves/preview', {'snapshotId': outside['id'], 'definition': definition}, expected=422)
    assert refused['code'] == 'schedule_context', refused
    client.call('market/current', {})
    print('PASS exact recipe schedule: overlapping successor changes active dates, pinned curve retains its original date and price', flush=True)
    return catalog, schedule['id']
