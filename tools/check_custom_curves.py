"""Behavior checks for the authenticated custom-curve operations in an owned preview."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame


def check_generation_savers(query,catalog,curve,generation,token):
    """Each draft generation names who saved it (D260): a person, or the assistant with the turn and step that saved it.
    `curve` has a published version 1 and a draft version 2 at `generation`, each generation saved by the editor through the
    API; `token(profile)` issues a request context."""
    def refused(sql,errcode,profile=None):
        bind=f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token(profile)}');" if profile else ''
        query(f"""BEGIN;{bump}{bind}DO $$BEGIN {sql};
            RAISE EXCEPTION 'Expected a refused saver record' USING ERRCODE='XX000';
          EXCEPTION WHEN SQLSTATE '{errcode}' THEN NULL;END$$;ROLLBACK;""")
    editor,approver=(query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='{subject}';") for subject in ('editor','approver'))
    def row(version,generation,saved_by,turn='NULL',step='NULL',actor=editor):
        return (f"INSERT INTO market_data.custom_curve_generation_savers VALUES('{catalog}','{curve}',{version},{generation},'{saved_by}',"
                f"'{actor}',{turn},{step})")
    references=query("SELECT string_agg(confrelid::regclass||':'||condeferrable,',' ORDER BY confrelid::regclass::text) FROM pg_constraint "
                     "WHERE conrelid='market_data.custom_curve_generation_savers'::regclass AND contype='f';")
    assert references=='assistant.tool_steps:true,assistant.turns:false,market_data.custom_curve_versions:false,security.principals:false',references
    assert query("SELECT string_agg(a.attname,',' ORDER BY k.n) FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum,n) "
                 "JOIN pg_attribute a ON(a.attrelid,a.attnum)=(c.conrelid,k.attnum) "
                 "WHERE c.conrelid='market_data.custom_curve_generation_savers'::regclass AND c.contype='p';")=='catalog_id,curve_id,version,generation'
    turn=f"'{uuid4()}'"
    # Every generation the editor saved through the API, published or draft, is recorded as a person's.
    savers=query("SELECT string_agg(version||':'||generation||':'||saved_by||':'||(actor_id='"+editor+"')||':'||coalesce(turn_id::text||step,'-'),',' "
                 f"ORDER BY version,generation) FROM market_data.custom_curve_generation_savers WHERE(catalog_id,curve_id)=('{catalog}','{curve}');")
    assert savers==','.join([f'1:{g}:person:true:-' for g in (1,2)]+[f'2:{g}:person:true:-' for g in range(1,generation+1)]),savers
    # The checks below record a new generation, which no save has recorded yet.
    bump=f"UPDATE market_data.custom_curve_versions SET generation={generation+1} WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',2);"
    generation+=1
    # Only a person or the assistant saves, and the assistant's record alone names a turn and step.
    refused(row(2,generation,'robot'),'23514')
    refused(row(2,generation,'assistant'),'23514')
    refused(row(2,generation,'assistant',turn),'23514')
    refused(row(2,generation,'person',turn,'1'),'23514')
    # A record names the current generation of a draft, and never changes.
    refused(row(2,generation+1,'person'),'40001')
    refused(row(1,2,'person'),'55000')
    query(f"""BEGIN;{bump}{row(2,generation,'person')};DO $$BEGIN
        UPDATE market_data.custom_curve_generation_savers SET saved_by='person' WHERE(catalog_id,curve_id,version)=('{catalog}','{curve}',2);
        RAISE EXCEPTION 'Expected an immutable saver record' USING ERRCODE='XX000';
      EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;ROLLBACK;""")
    # Under a request context: an author records their own save as a person's; a reader, another actor or an assistant
    # record outside its turn's own agent context is refused.
    query(f"""BEGIN;{bump}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token('editor')}');{row(2,generation,'person')};
        DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_generation_savers WHERE(catalog_id,curve_id,version,generation,saved_by)
         =('{catalog}','{curve}',2,{generation},'person')) THEN RAISE EXCEPTION 'An author could not record their own save';END IF;END$$;ROLLBACK;""")
    refused(row(2,generation,'person'),'42501','viewer')
    refused(row(2,generation,'person',actor=approver),'42501','editor')
    refused(row(2,generation,'assistant',turn,'1'),'42501','editor')
    print('PASS custom-curve generation savers: a person or the assistant with its turn and step, the current draft generation only, '
          'immutable, exact foreign keys, and only an author recording their own save',flush=True)


def run_checks(url, query=None, manifest=None):
    client=PreviewClient(url)
    boot=client.bootstrap()
    client.call('profile',{'profile':'viewer'})
    catalog=client.call('custom-curves')
    assert not catalog['canEdit'] and not catalog['canPublish']
    # Each curve is named by its exact definition: market curves by rule and role, saved graphs by their output step.
    assert catalog['kindsVersion']=='curve_kinds_v1'
    source_kinds={row['code']:row['kind'] for row in catalog['sources']}
    assert (source_kinds['ng_monthly'],source_kinds['ng_daily'],source_kinds['ng_fixed_spread'],source_kinds['ng_fixed_bridge'])==('market_settlement','trader_mark','trader_mark','basis_spread'),source_kinds
    navigation=client.call('workspace/navigation')
    assert navigation['kindsVersion']=='curve_kinds_v1'
    navigation_kinds={row['code']:row['kind'] for row in navigation['curves']}
    assert all(navigation_kinds[code]==kind for code,kind in source_kinds.items()),navigation_kinds
    assert set(navigation_kinds.values())<={'market_settlement','trader_mark','basis_spread','scaled','interest_rate'},navigation_kinds
    templates={row['code']:row for row in catalog['templates']}
    assert {'ng_prompt','ng_balmo','ng_average'} <= templates.keys()
    frame=next(frame for frame in boot['frames'] if frame['at'].startswith('2020-12-18T16:00:00'))
    body={'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':templates['ng_prompt']['definition'],'layout':templates['ng_prompt'].get('layout',{})}
    prompt=client.call('custom-curves/preview',{'definition':body['definition'],'snapshotId':frame['id']})
    assert len(prompt['rows'])==1 and prompt['rows'][0][1]=='3.08', prompt['rows']
    assert prompt['trace']['numericalPolicy']=='exact_rational_connected_curves_v1', 'Preview trace does not identify its connected rational calculation policy'
    assert len(prompt['dependencies'])==1
    first=prompt['summary']['firstPeriod']
    assert prompt['summary']=={'points':1,'unavailablePoints':0,'firstPeriod':first,'lastPeriod':first,'unit':prompt['rows'][0][2]}, prompt['summary']
    assert first['from']==prompt['rows'][0][5] and first['to']>first['from'], (first,prompt['rows'][0])
    balmo=client.call('custom-curves/preview',{'definition':templates['ng_balmo']['definition'],'snapshotId':frame['id']})
    assert len(balmo['rows'])==1 and balmo['rows'][0][1]=='3.6175',balmo['rows']
    assert len(balmo['dependencies'])==14
    average=client.call('custom-curves/preview',{'definition':templates['ng_average']['definition'],'snapshotId':frame['id']})
    assert len(average['rows'])==12 and len(average['inputs'])==240 and average['dependencies']==[]
    assert average['summary']['points']==12 and average['summary']['firstPeriod']['from']<average['summary']['lastPeriod']['from'], average['summary']
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
    # A component is inspected without replacing the saved output or averaging its sample dates.
    history_request={'definition':body['definition'],'snapshotId':frame['id'],'node':'select',
        'policy':'raw_component_history_v1','dates':{'id':'window','kind':'dates','inputs':{},'parameters':{
            'mode':'prior_calendar','count':'24','includeContext':'true','sampling':'context_utc_time',
            'corrections':'exact_context_frame_history_latest_visible'}}}
    raw=client.call('custom-curves/component-history',history_request)
    assert raw['node']=='select' and raw['policy']=='raw_component_history_v1'
    raw=raw['preview']
    assert len(raw['rows'])==24 and raw['chart']['axis']=='observation'
    contracts_by_date={row[4]:row[5] for row in raw['rows']}
    assert contracts_by_date['2020-11-27']=='2020-12-01' and contracts_by_date['2020-11-28']=='2021-01-01',contracts_by_date
    assert len({row['contract']['id'] for row in raw['inputs']})==2
    prices_by_date={row[4]:row[1] for row in raw['rows']}
    assert prices_by_date['2020-11-27']=='3.034' and prices_by_date['2020-11-28']=='3.06',prices_by_date
    from_sample=client.call('custom-curves/component-history',{**history_request,'node':'sample'})
    assert from_sample['preview']['rows']==raw['rows']
    locked=deepcopy(history_request)
    next(n for n in locked['definition']['nodes'] if n['kind']=='select')['parameters']['binding']='context'
    locked=client.call('custom-curves/component-history',locked)['preview']
    assert {row[5] for row in locked['rows']}=={'2021-01-01'}
    mapped=deepcopy(history_request)
    mapped['definition']['nodes']=[{'id':'select','kind':'weighted_contracts','parameters':{'aggregation':'signed_sum_v1'},
        'inputs':{'source_1':'source'},'contractWeights':[{'outputPosition':1,'sourcePort':'source_1',
            'contractId':locked['inputs'][0]['contract']['id'],'weight':'1'}]} if n['id']=='select' else n for n in mapped['definition']['nodes']]
    mapped_history=client.call('custom-curves/component-history',mapped)['preview']
    assert [(r[1],r[4],r[5]) for r in mapped_history['rows']]==[(r[1],r[4],r[5]) for r in locked['rows']]

    assert client.call('custom-curves/component-history',{**history_request,'policy':'back_adjusted'},expected=422)['code']=='component_history'
    assert client.call('custom-curves/component-history',{**history_request,'node':'source'},expected=422)['code']=='component_history'
    assert client.call('custom-curves/preview',{'definition':body['definition'],'snapshotId':frame['id']})['rows']==prompt['rows']
    print('PASS component history: raw roll jumps, actual dated contracts, fixed-contract binding, read-only authoring and explicit policy refusals')
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
    # A person's save is recorded as theirs (D260); only the assistant's names a turn and step.
    assert saved['saver']=={'savedBy':'person'},saved['saver']
    replay=client.call('custom-curves/save',body)
    assert (replay['id'],replay['version'],replay['generation'])==(saved['id'],1,1)
    altered=deepcopy(body);altered['definition']['name']='Changed replay payload'
    client.call('custom-curves/save',altered,expected=409)
    changed={**body,'requestId':str(uuid4()),'id':saved['id'],'version':1,'generation':1,'expectedState':'draft'}
    changed['definition']={**body['definition'],'name':'Synthetic prompt definition'}
    updated=client.call('custom-curves/save',changed)
    assert updated['generation']==2 and updated['saver']=={'savedBy':'person'},updated
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
    final=client.call('custom-curves')
    assert next(r['kind'] for r in final['recipes'] if r['id']==saved['id'])=='prompt_contract'
    assert [row['kind'] for row in final['publishedSources'] if row['reference']['curveId']==saved['id']]==['prompt_contract'],final['publishedSources']
    client.call('market/current',{})
    saved_history={**history_request,'definition':published['definition'],'id':saved['id'],'version':1}
    assert client.call('custom-curves/component-history',saved_history)['preview']['rows']==raw['rows']
    mismatched=deepcopy(saved_history)
    next(n for n in mismatched['definition']['nodes'] if n['kind']=='select')['parameters']['count']='2'
    client.call('custom-curves/component-history',mismatched,expected=422)
    # A certified cutoff before publication must never discover the later graph.
    historical=client.call('market/context',{'effectiveOn':'2020-12-18','knownAt':before['context']['knownAt'],'mode':'at','selection':'latest_correction'})
    assert not any(r['id']==saved['id'] for r in client.call('custom-curves')['recipes'])
    retained_frame=initial_frame(client.call('workspace/navigation'),'2020-12-18')
    client.call('custom-curves/preview',{'definition':published['definition'],'snapshotId':retained_frame['id'],'id':saved['id'],'version':1},expected=422)
    client.call('custom-curves/save',{**body,'snapshotId':retained_frame['id'],'requestId':str(uuid4())},expected=403)
    client.call('market/current',{})
    client.call('profile',{'profile':'editor'})
    fork=client.call('custom-curves/save',{**body,'requestId':str(uuid4()),'id':saved['id'],'version':1,'generation':2,'expectedState':'published','definition':published['definition']})
    assert fork['version']==2 and fork['state']=='draft' and fork['saver']=={'savedBy':'person'}
    # A person's save of an unchanged definition is still a new generation; only the assistant's writes nothing.
    again=client.call('custom-curves/save',{**body,'requestId':str(uuid4()),'id':saved['id'],'version':2,'generation':1,'expectedState':'draft','definition':published['definition']})
    assert (again['version'],again['generation'],again['saver'])==(2,2,{'savedBy':'person'}),again
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
        check_generation_savers(query,cat,saved['id'],again['generation'],token)
        # A draft saved before savers were recorded reads as saved by no one known, never as a person's. The editor saves it
        # as a save did then, a copy of the published graph without a saver record; versions are never deleted, so this
        # valid draft stays in the preview.
        legacy=uuid4()
        children=['nodes','edges','node_sources','node_calendars','node_schedules','node_published_sources','contract_weights','panel_weights']
        def copied(table):
            columns=query(f"SELECT string_agg(column_name,',' ORDER BY ordinal_position) FROM information_schema.columns "
                          f"WHERE table_schema='market_data' AND table_name='custom_curve_{table}';").split(',')
            chosen=[f"'{legacy}'::uuid" if c=='curve_id' else c for c in columns]
            return (f"INSERT INTO market_data.custom_curve_{table}({','.join(columns)}) SELECT {','.join(chosen)} "
                    f"FROM market_data.custom_curve_{table} WHERE(catalog_id,curve_id,version)=('{cat}','{saved['id']}',1);")
        query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');
            INSERT INTO market_data.custom_curves VALUES('{cat}','{legacy}','synthetic_unrecorded');
            INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
             SELECT catalog_id,'{legacy}',1,1,'draft','Synthetic unrecorded saver',output_node,engine_version,layout
             FROM market_data.custom_curve_versions WHERE(catalog_id,curve_id,version)=('{cat}','{saved['id']}',1);
            {''.join(copied(table) for table in children)}COMMIT;""")
        recipes={r['id']:r for r in client.call('custom-curves')['recipes']}
        assert recipes[str(legacy)]['saver']=={'savedBy':None} and recipes[str(legacy)]['kind']=='prompt_contract',recipes[str(legacy)]
        # Reads say only who saved a generation (D260).
        assert recipes[saved['id']]['saver']=={'savedBy':'person'},recipes[saved['id']]
        assert query(f"SELECT count(*) FROM pg_tables WHERE schemaname='market_data' AND tablename LIKE 'custom_curve%' AND rowsecurity;")=='16'
    # A later correction can restate a working historical window, but never a retained earlier knowledge view.
    client.call('profile',{'profile':'editor'})
    observed=next(row for row in average['inputs'] if row['date']=='2020-12-01' and row['contract']['id']=='e0000000-0000-0000-0000-000000000102')
    past_frame=next(row for row in boot['frames'] if row['at'].startswith('2020-12-01T16:00:00'))
    client.call('corrections',{'snapshotId':past_frame['id'],'observationId':observed['observations'][0]['id'],'value':str(Decimal(observed['price']['value'])+1)})
    restated=client.call('custom-curves/preview',{'definition':templates['ng_average']['definition'],'snapshotId':frame['id']})
    assert Decimal(restated['rows'][0][1])-Decimal(average['rows'][0][1])==Decimal('.05')
    restated_history=client.call('custom-curves/component-history',history_request)['preview']
    raw_dec1=next(row for row in raw['rows'] if row[4]=='2020-12-01')
    revised_dec1=next(row for row in restated_history['rows'] if row[4]=='2020-12-01')
    assert Decimal(revised_dec1[1])-Decimal(raw_dec1[1])==1
    client.call('market/context',{'effectiveOn':'2020-12-18','knownAt':before['context']['knownAt'],'mode':'at','selection':'latest_correction'})
    earlier_frame=initial_frame(client.call('workspace/navigation'),'2020-12-18')
    earlier=client.call('custom-curves/preview',{'definition':templates['ng_average']['definition'],'snapshotId':earlier_frame['id']})
    assert earlier['rows']==average['rows']
    assert client.call('custom-curves/component-history',{**history_request,'snapshotId':earlier_frame['id']})['preview']['rows']==raw['rows']
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
