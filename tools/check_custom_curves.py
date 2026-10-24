"""Behavior checks for the authenticated custom-curve operations in an owned preview."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame


def run_checks(url, query=None, manifest=None):
    client=PreviewClient(url)
    boot=client.bootstrap()
    client.call('profile',{'profile':'viewer'})
    catalog=client.call('custom-curves')
    assert not catalog['canEdit'] and not catalog['canPublish']
    templates={row['code']:row for row in catalog['templates']}
    assert {'ng_prompt','ng_balmo','ng_average'} <= templates.keys()
    frame=next(frame for frame in boot['frames'] if frame['at'].startswith('2020-12-18T16:00:00'))
    body={'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':templates['ng_prompt']['definition'],'layout':templates['ng_prompt'].get('layout',{})}
    prompt=client.call('custom-curves/preview',{'definition':body['definition'],'snapshotId':frame['id']})
    assert len(prompt['rows'])==1 and prompt['rows'][0][1]=='3.08', prompt['rows']
    assert prompt['trace']['numericalPolicy']=='exact_rational_connected_curves_v1', 'Preview trace does not identify its connected rational calculation policy'
    assert len(prompt['dependencies'])==1
    balmo=client.call('custom-curves/preview',{'definition':templates['ng_balmo']['definition'],'snapshotId':frame['id']})
    assert len(balmo['rows'])==1 and balmo['rows'][0][1]=='3.6175',balmo['rows']
    assert len(balmo['dependencies'])==14
    average=client.call('custom-curves/preview',{'definition':templates['ng_average']['definition'],'snapshotId':frame['id']})
    assert len(average['rows'])==12 and len(average['inputs'])==240 and average['dependencies']==[]
    panel=deepcopy(templates['ng_average']['definition'])
    panel['nodes']=[node for node in panel['nodes'] if node['kind']!='mean']
    for node in panel['nodes']:
        if node['kind']=='select':node['parameters']['count']='2'
        if node['kind']=='dates':node['parameters']['count']='2'
        if node['kind']=='output':node['inputs']['values']='sample'
    history=client.call('custom-curves/preview',{'definition':panel,'snapshotId':frame['id']})
    assert len(history['rows'])==4
    assert history['chart']['axis']=='observation' and len(history['chart']['series'])==2,history['chart']
    assert all(len(series['points'])==2 and len({p['x'] for p in series['points']})==2 for series in history['chart']['series'])
    compound=deepcopy(templates['ng_prompt']['definition'])
    next(node for node in compound['nodes'] if node['kind']=='source')['parameters']['curve']='ng_fixed_location'
    fixed=client.call('custom-curves/preview',{'definition':compound,'snapshotId':frame['id']})
    assert fixed['rows'][0][1]=='4.15',fixed['rows']
    assert len(fixed['dependencies'])==2 and len(fixed['inputs'][0]['observations'])==3
    compound_mean=deepcopy(templates['ng_average']['definition'])
    next(node for node in compound_mean['nodes'] if node['kind']=='source')['parameters']['curve']='ng_fixed_location'
    next(node for node in compound_mean['nodes'] if node['kind']=='select')['parameters']['count']='1'
    next(node for node in compound_mean['nodes'] if node['kind']=='dates')['parameters']['count']='2'
    fixed_mean=client.call('custom-curves/preview',{'definition':compound_mean,'snapshotId':frame['id']})
    assert fixed_mean['rows'][0][1]=='4' and fixed_mean['dependencies']==[],fixed_mean
    missing_final_frame=next(row for row in boot['frames'] if row['at'].startswith('2020-12-17T16:00:00'))
    client.call('custom-curves/preview',{'definition':compound,'snapshotId':missing_final_frame['id']},expected=422)
    if query and manifest:
        # Two independent final events cannot become an implicit latest-time
        # choice. The exact context frame still retains its one supplied pin.
        query(f"""INSERT INTO market_data.observations
            SELECT catalog_id,'{uuid4()}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
             source_code,'custom_frozen_second_final','1',role,publication_state,observed_at+interval '1 minute',
             clock_timestamp(),4,NULL FROM market_data.observations
            WHERE catalog_id='{manifest['catalogId']}' AND id='f0000000-0000-0000-0000-000000900000';""")
        client.call('custom-curves/preview',{'definition':compound_mean,'snapshotId':frame['id']},expected=422)
        assert client.call('custom-curves/preview',{'definition':compound,'snapshotId':frame['id']})['rows']==fixed['rows']
    formula=client.call('custom-curves/format',{'definition':body['definition']})['formula']
    compiled=client.call('custom-curves/compile',{'formula':formula,'code':body['definition']['code'],'name':body['definition']['name'],'snapshotId':frame['id']})['definition']
    assert compiled==body['definition']
    sql_definition=deepcopy(templates['ng_average']['definition'])
    sql_mean=next(node for node in sql_definition['nodes'] if node['kind']=='mean')
    sql_mean['kind']='sql_transform';sql_mean['parameters']={'sqlTransformCode':'sql_mean_dates_v1'}
    client.call('custom-curves/preview',{'definition':sql_definition,'snapshotId':frame['id']},expected=422)
    client.call('custom-curves/save',body,expected=403)
    before=client.call('date-schedules/context',{'effectiveOn':'2020-12-18','mode':'current','knownAt':None})
    client.call('profile',{'profile':'editor'})
    catalog=client.call('custom-curves')
    assert catalog['canEdit'] and not catalog['canPublish']
    saved=client.call('custom-curves/save',body)
    assert saved['state']=='draft' and saved['version']==1 and saved['generation']==1
    replay=client.call('custom-curves/save',body)
    assert (replay['id'],replay['version'],replay['generation'])==(saved['id'],1,1)
    altered=deepcopy(body);altered['definition']['name']='Changed replay payload'
    client.call('custom-curves/save',altered,expected=409)
    changed={**body,'requestId':str(uuid4()),'id':saved['id'],'version':1,'generation':1,'expectedState':'draft'}
    changed['definition']={**body['definition'],'name':'Synthetic prompt definition'}
    updated=client.call('custom-curves/save',changed)
    assert updated['generation']==2
    client.call('custom-curves/save',{**changed,'requestId':str(uuid4())},expected=409)
    publish={'requestId':str(uuid4()),'id':saved['id'],'version':1,'generation':2}
    client.call('custom-curves/publish',publish,expected=403)
    client.call('profile',{'profile':'approver'})
    published=client.call('custom-curves/publish',publish)
    assert published['state']=='published'
    assert client.call('custom-curves/publish',publish)['publicationOrder']==published['publicationOrder']
    if query and manifest:
        # Check both exact receipt relationships independently of runtime RLS.
        # A permission gate alone must not admit dangling bundle/version pins.
        for revision,curve,version in [(f"'{uuid4()}'::uuid",'curve_id','version'),
                                       ('definition_revision_id',f"'{uuid4()}'::uuid",'version'),
                                       ('definition_revision_id','curve_id','64')]:
            query(f"""BEGIN;DO $$BEGIN
                INSERT INTO market_data.custom_curve_requests
                 SELECT catalog_id,actor_id,'{uuid4()}',operation,payload,response,{revision},{curve},{version}
                 FROM market_data.custom_curve_requests WHERE catalog_id='{manifest['catalogId']}' AND request_id='{publish['requestId']}';
                RAISE EXCEPTION 'Expected exact receipt foreign key rejection' USING ERRCODE='XX000';
              EXCEPTION WHEN foreign_key_violation THEN NULL;END$$;ROLLBACK;""")
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        query(f"""BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';
            SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
            DO $$BEGIN IF EXISTS(SELECT 1 FROM market_data.custom_curve_requests WHERE catalog_id='{manifest['catalogId']}' AND request_id='{publish['requestId']}') THEN
              RAISE EXCEPTION 'Retained full-content response leaked after reference authority revocation';END IF;END$$;ROLLBACK;""")
    if query and manifest:
        scope=manifest['referenceCatalogId']
        access=query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{scope}' RETURNING tenant_id,workspace_id,access_mode;")
        try:client.call('custom-curves/publish',publish,expected=403)
        finally:
            tenant,workspace,mode=access.split('|')
            query(f"INSERT INTO security.reference_catalog_scopes VALUES('{scope}','{tenant}','{workspace}','{mode}');")
        assert client.call('custom-curves/publish',publish)['publicationOrder']==published['publicationOrder']
    client.call('profile',{'profile':'viewer'})
    assert any(r['id']==saved['id'] for r in client.call('custom-curves')['recipes'])
    # A certified cutoff before publication must never discover the later graph.
    historical=client.call('market/context',{'effectiveOn':'2020-12-18','knownAt':before['context']['knownAt'],'mode':'at','selection':'latest_correction'})
    assert not any(r['id']==saved['id'] for r in client.call('custom-curves')['recipes'])
    retained_frame=initial_frame(client.call('workspace/navigation'),'2020-12-18')
    client.call('custom-curves/preview',{'definition':published['definition'],'snapshotId':retained_frame['id'],'id':saved['id'],'version':1},expected=422)
    client.call('custom-curves/save',{**body,'snapshotId':retained_frame['id'],'requestId':str(uuid4())},expected=403)
    client.call('market/current',{})
    client.call('profile',{'profile':'editor'})
    fork=client.call('custom-curves/save',{**body,'requestId':str(uuid4()),'id':saved['id'],'version':1,'generation':2,'expectedState':'published','definition':published['definition']})
    assert fork['version']==2 and fork['state']=='draft'
    if query and manifest:
        cat=manifest['catalogId']
        assert query(f"SELECT count(*) FROM market_data.custom_curve_node_sources WHERE catalog_id='{cat}' AND curve_id='{saved['id']}' AND version=1;")=='1'
        def token(profile='editor'):
            return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        # Moving a relationship from a published parent to a draft is still a mutation of the published graph.
        query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');
            DELETE FROM market_data.custom_curve_node_sources WHERE(catalog_id,curve_id,version)=('{cat}','{saved['id']}',2);
            DO $$BEGIN
              UPDATE market_data.custom_curve_node_sources SET version=2 WHERE(catalog_id,curve_id,version)=('{cat}','{saved['id']}',1);
              RAISE EXCEPTION 'Expected immutable relationship rejection' USING ERRCODE='XX000';
            EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;ROLLBACK;""")
        query(f"""BEGIN;DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';
            SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('viewer')}');
            DO $$BEGIN IF EXISTS(SELECT 1 FROM market_data.custom_curve_versions WHERE catalog_id='{cat}') THEN
              RAISE EXCEPTION 'Saved references leaked after current reference revocation';END IF;END$$;ROLLBACK;""")
        query(f"""BEGIN;DELETE FROM security.market_catalog_scopes WHERE catalog_id='{cat}';
            SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('viewer')}');
            DO $$BEGIN IF EXISTS(SELECT 1 FROM market_data.custom_curve_versions WHERE catalog_id='{cat}') THEN
              RAISE EXCEPTION 'Saved graph leaked after current source scope revocation';END IF;END$$;ROLLBACK;""")
        assert query(f"SELECT count(*) FROM pg_tables WHERE schemaname='market_data' AND tablename LIKE 'custom_curve%' AND rowsecurity;")=='15'
    # A later correction can restate a working historical window, but never a retained earlier knowledge view.
    client.call('profile',{'profile':'editor'})
    observed=next(row for row in average['inputs'] if row['date']=='2020-12-01' and row['contract']['id']=='e0000000-0000-0000-0000-000000000102')
    past_frame=next(row for row in boot['frames'] if row['at'].startswith('2020-12-01T16:00:00'))
    client.call('corrections',{'snapshotId':past_frame['id'],'observationId':observed['observations'][0]['id'],'value':str(Decimal(observed['price']['value'])+1)})
    restated=client.call('custom-curves/preview',{'definition':templates['ng_average']['definition'],'snapshotId':frame['id']})
    assert Decimal(restated['rows'][0][1])-Decimal(average['rows'][0][1])==Decimal('.05')
    client.call('market/context',{'effectiveOn':'2020-12-18','knownAt':before['context']['knownAt'],'mode':'at','selection':'latest_correction'})
    earlier_frame=initial_frame(client.call('workspace/navigation'),'2020-12-18')
    earlier=client.call('custom-curves/preview',{'definition':templates['ng_average']['definition'],'snapshotId':earlier_frame['id']})
    assert earlier['rows']==average['rows']
    if query and manifest:
        temporal_context=client.call('custom-curves')['context']['contextId']
        query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');
            SELECT security.use_market_context('{cat}','{temporal_context}');
            DO $$BEGIN IF EXISTS(SELECT 1 FROM market_data.custom_curve_requests WHERE catalog_id='{cat}') THEN
             RAISE EXCEPTION 'Future mutation receipts leaked into historical context';END IF;
             BEGIN
               INSERT INTO market_data.custom_curves VALUES('{cat}','{uuid4()}','historical_forgery');
               RAISE EXCEPTION 'Historical graph mutation was admitted' USING ERRCODE='XX000';
             EXCEPTION WHEN insufficient_privilege THEN NULL;END;
            END$$;ROLLBACK;""")
    client.call('market/current',{})
    print('PASS custom curves: prompt/BALMO/12-tenor averages, frozen-source basis chains, exact final pins and ambiguous-final rejection, typed formula parity and removed SQL rejection, exact request replay with current content authority and receipt FKs, optimistic publication, immutable normalized relationships, current scope revocation, certified cutoff visibility and correction replay',flush=True)

    if query and manifest:
        from curve_agent_fixture import prepare as check_exact_schedule
        check_exact_schedule(url, query, manifest)
