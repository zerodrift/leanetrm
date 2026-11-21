"""Mark-free unit qualification through ordinary and direct native publication."""
from copy import deepcopy
import json
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url, query, manifest, regional):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'editor'})
    dates=client.call('market/dates',dict(curve=None,timeZone='America/New_York',session='nyc_close'))
    frame=next(d for d in dates['dates'] if d['date']=='2020-12-18')['frames'][0]['id']
    catalog=client.call('custom-curves?snapshotId='+frame)
    sources={c['code']:c for c in catalog['sources']}
    # These exact regional source definitions use independent currency quotes.
    choices=json.loads(query("SELECT jsonb_object_agg(q.currency_code,jsonb_build_object('curve',c.code,'contract',m.contract_id)) FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code) JOIN LATERAL(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=(c.catalog_id,c.revision_id,c.code) ORDER BY ordinal LIMIT 1)m ON true WHERE c.catalog_id='"+manifest['catalogId']+"' AND c.revision_id='"+regional['definitionRevisionId']+"' AND q.currency_code IN('gbp','eur');"))
    assert len(choices)==2 and all(c['curve'] in sources for c in choices.values()),choices
    def node(id,kind,parameters=None,inputs=None,**extra):return dict(id=id,kind=kind,parameters=parameters or {},inputs=inputs or {},**extra)
    def graph(code,positions):
        return dict(code=code,name='Synthetic exact currency outputs',nodes=[
            node('gbp','source',dict(curve=choices['gbp']['curve'])),node('eur','source',dict(curve=choices['eur']['curve'])),
            node('mapping','weighted_contracts',dict(aggregation='signed_sum_v1'),dict(source_gbp='gbp',source_eur='eur'),contractWeights=[
                dict(outputPosition=positions[0],sourcePort='source_gbp',contractId=choices['gbp']['contract'],weight='1'),
                dict(outputPosition=positions[1],sourcePort='source_eur',contractId=choices['eur']['contract'],weight='1')]),
            node('dates','dates',dict(mode='prior_calendar',count='10',includeContext='true',sampling='context_utc_time',corrections='exact_context_frame_history_latest_visible')),
            node('sample','sample',inputs=dict(selection='mapping',dates='dates')),node('output','output',inputs=dict(values='sample'))],output='output')
    def save(definition):
        client.call('profile',{'profile':'editor'})
        return client.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=frame,definition=definition,layout={}))
    def publish(saved,expected=200):
        client.call('profile',{'profile':'approver'})
        return client.call('custom-curves/publish',dict(snapshotId=frame,requestId=str(uuid4()),id=saved['id'],version=saved['version'],generation=saved['generation']),expected=expected)
    bad=save(graph('currency_sum_'+uuid4().hex[:8],(1,1)))
    error=publish(bad,422)
    assert error['code'] in ('recipe_input','recipe_definition','recipe_units'),error
    # A direct call cannot bypass managed validation. The ordinary native operation still owns publication authority.
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','"+manifest['tenantId']+"','"+manifest['workspaceId']+"',NULL,'preview_runtime',120);")
    try:
        query("BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('"+token+"');SELECT security.publish_custom_curve('"+manifest['catalogId']+"','"+bad['id']+"',1,1);ROLLBACK;")
    except RuntimeError as error:assert 'recipe_units' in str(error),str(error)
    else:raise AssertionError('Direct native publication accepted incompatible currencies')
    good=save(graph('currency_positions_'+uuid4().hex[:8],(1,2)))
    request=dict(snapshotId=frame,requestId=str(uuid4()),id=good['id'],version=good['version'],generation=good['generation'])
    client.call('profile',{'profile':'approver'})
    published=client.call('custom-curves/publish',request)
    assert published['state']=='published'
    # A new session has no page-local snapshot, but an accepted command must
    # recover its original result under the same current actor authority.
    recovery=PreviewClient(url);recovery.bootstrap();recovery.call('profile',{'profile':'approver'})
    assert recovery.call('custom-curves/publish',request)['publicationOrder']==published['publicationOrder']
    assert recovery.call('custom-curves/publish',{**request,'snapshotId':str(uuid4())},409)['code']=='custom_curve_conflict'
    assert publish(good)['publicationOrder']==published['publicationOrder']
    assert publish({**good,'generation':good['generation']+1},409)['code']=='custom_curve_conflict'
    print('PASS derived currency publication: separate output currencies retained; mixed same-output sum rejected by API and native operation; missing historical marks are not required; replay and generation fences retained',flush=True)

    # More than64 operator frames used to exceed a naive recursive native checker.
    # Independent output units remain intact through exact published parents.
    parent=published
    for level in range(22):
        definition=dict(code='currency_chain_'+str(level)+'_'+uuid4().hex[:6],name='Synthetic unit inheritance chain',output='output',nodes=[
            node('parent','published_source',dict(selection='all_output_points_v1'),publishedSource=dict(catalogId=manifest['catalogId'],curveId=parent['id'],version=parent['version'],outputNode='output')),
            node('mean','mean',dict(axis='dates'),dict(values='parent')),node('output','output',inputs=dict(values='mean'))])
        parent=publish(save(definition))
        assert parent['state']=='published'
    print('PASS derived currency publication:22 nested published parents preserve independent units without native recursion or marks',flush=True)
