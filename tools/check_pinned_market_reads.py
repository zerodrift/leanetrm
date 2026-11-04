"""Verify ordinary workspace reads retain selected facts without loading the catalog."""
import json
from uuid import UUID,uuid5
from preview_client import PreviewClient


def run_checks(url, query, manifest):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    initial=client.call('workspace/navigation')
    seed=max((f for f in initial['frames'] if f['id'] in initial['initialFrameIds']),key=lambda f:f['at'])
    client.call('profile',{'profile':'approver'})
    for ordinal in range(12):
        request=str(uuid5(UUID(manifest['catalogId']),'anchored-read-close:'+str(ordinal)))
        body={'requestId':request,'snapshotId':seed['id'],'name':'Retained input '+str(ordinal)}
        recorded=client.call('market/closes/register',body)
        assert recorded==client.call('market/closes/register',body)
    client.call('profile',{'profile':'trader'})
    workspace=client.call('workspace')
    assert workspace['frameCatalog']['durableLimit']==8 and workspace['frameCatalog']['hasMoreDurableCloses']
    assert len([f for f in workspace['frames'] if f.get('originCloseId')])==8
    assert set(initial['initialFrameIds']).issubset({f['id'] for f in workspace['frames']})
    assert workspace['observationScope']=='visible_frame_fact_history'
    all_rows=json.loads(query(f"SELECT json_agg(row_to_json(o)) FROM market_data.observations o WHERE catalog_id='{manifest['catalogId']}' AND definition_revision_id='{manifest['revisionId']}';"))
    by_id={row['id']:row for row in all_rows}
    keys=('series_id','source_code','source_event_id','observed_at','role')
    def fact(row):return tuple(str(row[key]) for key in keys)
    anchors={oid for frame in workspace['frames'] for oid in frame['observationIds']}
    selected={fact(by_id[oid]) for oid in anchors}
    expected={row['id'] for row in all_rows if fact(row) in selected}
    assert expected and expected!={row['id'] for row in all_rows},'The fixture must contain unselected source facts.'
    assert {row['id'] for row in workspace['observations']}==expected
    frame=max((f for f in workspace['frames'] if f['id'] in workspace['initialFrameIds']),key=lambda f:f['at'])
    curve=manifest['example']['curve']
    priced=client.call('evaluate',{'snapshotId':frame['id'],'curve':curve})
    contract=priced['points'][0]['contractId']
    history=client.call('history',{'snapshotId':frame['id'],'curve':curve,'contractId':contract,'revisionPolicy':'exact_snapshot_pins_v1'})
    assert len(history['points'])==2 and all(p['value'] is not None for p in history['points'])
    for point in history['points']:
        direct=client.call('evaluate',{'snapshotId':point['snapshotId'],'curve':curve})
        price=next(p['value'] for p in direct['points'] if p['contractId']==contract)
        assert point['value']==price
    selected_id=frame['observationIds'][0]
    same=client.call('select-observation',{'snapshotId':frame['id'],'observationId':selected_id})
    assert same['frame']['observationIds']==frame['observationIds']
    client.call('market/context',{'effectiveOn':frame['at'][:10],'mode':'current','knownAt':None,'selection':'latest_correction'})
    historical=client.call('workspace')
    assert historical['context'] is not None and historical['observationScope']=='visible_frame_fact_history'
    print('PASS ordinary workspace facts, exact history, revision selection and captured-date reads use explicit anchors',flush=True)
