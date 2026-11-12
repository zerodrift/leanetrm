"""Flat power graph checks against an owned synthetic database."""
from copy import deepcopy
from uuid import uuid4
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest, *, booked_roll=False):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    catalog = client.call('custom-curves')
    assert 'power_intervals' in {op['kind'] for op in catalog['operators']}
    product = next(p for p in catalog['powerProducts'] if p['code'] == 'pjm')
    # The physical consumer must admit an exact TOU proved by a published graph,
    # even when no sealed native curve_block references that product.
    fresh=client.call('tou-products/save',dict(code='synthetic_graph_peak',name='Synthetic graph weekday peak',
        clock=product['clock'],peakStartHour=7,peakEndHour=23,peakSaturday=False))
    client.call('profile',{'profile':'approver'})
    product=client.call('tou-products/publish',dict(id=fresh['id'],version=fresh['version'],generation=fresh['generation']))
    client.call('profile',{'profile':'editor'})
    following=client.call('tou-products/save',dict(id=product['id'],version=1,generation=product['generation'],expectedState='published',
        code=product['code'],name=product['name'],clock=product['clock'],peakStartHour=8,peakEndHour=20,peakSaturday=False))
    assert following['state']=='draft' and following['version']==2
    manifest['powerGraphProductDraftVersion']=2
    assert query(f"SELECT count(*) FROM market_data.curve_blocks WHERE tou_product_id='{product['id']}';")=='0'
    def node(id, kind, parameters=None, inputs=None):
        return dict(id=id, kind=kind, parameters=parameters or {}, inputs=inputs or {})
    definition = dict(code='synthetic_flat_power', name='Synthetic flat power intervals', output='result', nodes=[
        node('peak', 'source', {'curve': 'test_power_peak_product_monthly'}),
        node('off', 'source', {'curve': 'test_power_offpeak_product_monthly'}),
        node('hours', 'power_intervals', {
            'implementation': 'flat_forward_only_v1', 'profileMapping': 'exact_native_grid_terms_v1',
            'product': product['id'], 'productVersion': str(product['version']),
            'profile': 'test_hourly_eastern', 'market': 'DA', 'from': '2031-11-01', 'to': '2031-12-01'},
            {'peak': 'peak', 'offPeak': 'off'}),
        node('result', 'output', inputs={'values': 'hours'})])
    proof_definition=deepcopy(definition);proof_definition.update(code='synthetic_graph_product_proof',name='Synthetic graph product proof')
    proof=client.call('custom-curves/save',dict(definition=proof_definition,snapshotId=frame['id'],requestId=str(uuid4()),layout={}))
    from check_power_product_resolution import request as product_request
    client.call('profile',{'profile':'trader'})
    manifest['powerGraphProductBeforeKnownAt']=client.call('interval-series')['knownAt']
    unpublished=product_request(manifest,frame['id'],manifest['powerGraphProductBeforeKnownAt'],'peak',product,'2031-11-01T04:00:00Z','2031-12-01T05:00:00Z')
    assert client.call('ppa/preview',unpublished,expected=422)['code']=='ppa_tou_knowledge'
    client.call('profile',{'profile':'approver'})
    client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=proof['id'],version=1,generation=proof['generation']))
    client.call('profile',{'profile':'editor'})
    # Distinct daily delivery periods can expire together over a weekend.
    date_context=client.call('date-schedules/context',dict(effectiveOn='2020-12-18',knownAt=None,mode='current'))['context']
    weekend=dict(requestId=str(uuid4()),code='synthetic_daily_weekend',name='Synthetic shared weekend cutoff',context=date_context,contracts=[
        dict(label='Saturday',start='2020-12-19',end='2020-12-20',lastTrading='2020-12-18',cutoffUtc='23:00'),
        dict(label='Sunday',start='2020-12-20',end='2020-12-21',lastTrading='2020-12-18',cutoffUtc='23:00')])
    shared=client.call('date-schedules/contracts/save',weekend)
    assert shared==client.call('date-schedules/contracts/save',weekend)
    backwards=deepcopy(weekend);backwards.update(requestId=str(uuid4()),code='synthetic_backwards_cutoff')
    backwards['contracts'][1]['lastTrading']='2020-12-17'
    client.call('date-schedules/contracts/save',backwards,expected=422)
    stitched=deepcopy(definition);stitched.update(code='synthetic_daily_monthly',name='Synthetic daily then monthly forwards')
    operator=stitched['nodes'][2]
    operator['parameters'].update(implementation='daily_then_monthly_forward_v1',**{'from':'2031-11-20','cutover':'2031-12-01','to':'2032-01-01'})
    operator['inputs'].update(dailyPeak='daily_peak',dailyOffPeak='daily_off')
    stitched['nodes'][2:2]=[node('daily_peak','source',{'curve':'test_power_peak_daily'}),node('daily_off','source',{'curve':'test_power_offpeak_daily'})]
    daily_body=dict(definition=stitched,snapshotId=frame['id'])
    daily_preview=client.call('custom-curves/preview',daily_body)
    assert daily_preview['summary']['points']==daily_preview['summary']['anchors']==24,daily_preview['summary']
    assert daily_preview['summary']['intervals']==1008
    assert daily_preview['summary']['firstPeriod']=={'from':'2031-11-20','to':'2031-11-21'}
    assert daily_preview['summary']['lastPeriod']=={'from':'2031-12-01','to':'2032-01-01'}
    rows=daily_preview['rows'];assert len(rows)==24
    for day in ('2031-11-23','2031-11-27'):
        assert [(r[4],r[2],r[7]) for r in rows if r[0]==day]==[('Peak','65','16'),('Off-peak','42','8')]
    assert [(r[4],r[2]) for r in rows if r[6]=='Monthly']==[('Peak','70'),('Off-peak','45')]
    invalid=deepcopy(daily_body);invalid['definition']['nodes'][4]['parameters']['cutover']='2031-12-02'
    client.call('custom-curves/preview',invalid,expected=422)
    daily_saved=client.call('custom-curves/save',dict(daily_body,requestId=str(uuid4()),layout={}))
    def retained_sources(peak,daily):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
        UPDATE market_data.custom_curve_node_sources SET source_curve_code=CASE node_id WHEN 'peak' THEN '{peak}' ELSE '{daily}' END
        WHERE(catalog_id,curve_id,version)=('{manifest['catalogId']}','{daily_saved['id']}',1) AND node_id IN('peak','daily_peak');COMMIT;""")
    retained_sources('test_power_flat_monthly','test_power_peak_product_monthly')
    client.call('profile',{'profile':'approver'})
    refused=client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=daily_saved['id'],version=1,generation=daily_saved['generation']),expected=422)
    assert refused['code']=='power_interval_period',refused
    retained_sources('test_power_peak_product_monthly','test_power_peak_daily')
    daily_published=client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=daily_saved['id'],version=1,generation=daily_saved['generation']))
    assert daily_published['state']=='published'
    client.call('profile',{'profile':'editor'})
    product_rows=[]
    for suffix,daily,monthly in [('peak','test_power_peak_daily','test_power_peak_product_monthly'),('off','test_power_offpeak_daily','test_power_offpeak_product_monthly')]:
        def identity_map(id,source,contracts):
            return dict(node(id,'weighted_contracts',{'aggregation':'signed_sum_v1'},{'source':source}),contractWeights=[dict(outputPosition=i+1,sourcePort='source',contractId=c['id'],weight='1') for i,c in enumerate(contracts)])
        d=next(s for s in catalog['sources'] if s['code']==daily)['contracts']
        m=[c for c in next(s for s in catalog['sources'] if s['code']==monthly)['contracts'] if '2031-12-01'<=c['deliveryFrom']<'2032-01-01']
        child=dict(code='synthetic_product_'+suffix,name='Synthetic product '+suffix,output='result',nodes=[
            node('daily','source',{'curve':daily}),node('monthly','source',{'curve':monthly}),identity_map('daily_map','daily',d),identity_map('monthly_map','monthly',m),
            node('dates','dates',{'mode':'context','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}),
            node('daily_values','sample',inputs={'selection':'daily_map','dates':'dates'}),node('monthly_values','sample',inputs={'selection':'monthly_map','dates':'dates'}),
            node('splice','splice',{'from':'2031-11-20','cutover':'2031-12-01','to':'2032-01-01','implementation':'whole_delivery_intervals_v1'},{'before':'daily_values','after':'monthly_values'}),node('result','output',inputs={'values':'splice'})])
        child_body=dict(definition=child,snapshotId=frame['id'])
        child_preview=client.call('custom-curves/preview',child_body)
        assert child_preview['summary']['points']==12
        assert child_preview['chart']['axisName']=='Delivery date' and child_preview['columns'][0]=='Delivery from'
        assert child_preview['rows'][0][0:2]==['2031-11-20','2031-11-21']
        if suffix=='peak':
            # A lookup can give daily output cells backed by one monthly native
            # contract. Its monthly source dates must not label those daily cells.
            expanded=deepcopy(child);expanded.update(code='synthetic_expanded_splice',name='Synthetic daily expansion')
            november=next(c for c in next(s for s in catalog['sources'] if s['code']==monthly)['contracts'] if c['deliveryFrom']=='2031-11-01')
            mapped=next(n for n in expanded['nodes'] if n['id']=='monthly_map')
            mapped['contractWeights'].append(dict(outputPosition=2,sourcePort='source',contractId=november['id'],weight='1'))
            expanded['nodes'] += [node('daily_dates','dates',{'mode':'delivery_range','from':'2031-11-20','to':'2031-12-01','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}),
                node('expanded','lookup',{'axis':'delivery_start','method':'containing_delivery','maxGapDays':'31','leftBoundary':'reject','rightBoundary':'reject','implementation':'asset_curve_lookup_v1','output':'daily_delivery_v1'},{'values':'monthly_values','dates':'daily_dates'})]
            next(n for n in expanded['nodes'] if n['id']=='splice')['inputs']['before']='expanded'
            expanded['nodes']=[n for n in expanded['nodes'] if n['id'] not in ('daily','daily_map','daily_values')]
            expanded_preview=client.call('custom-curves/preview',dict(definition=expanded,snapshotId=frame['id']))
            assert expanded_preview['summary']['points']==12 and expanded_preview['columns'][0]=='Point'
        saved_child=client.call('custom-curves/save',dict(child_body,requestId=str(uuid4()),layout={}))
        client.call('profile',{'profile':'approver'})
        saved_child=client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=saved_child['id'],version=1,generation=saved_child['generation']))
        product_rows.append(saved_child)
        client.call('profile',{'profile':'editor'})
    lookup=deepcopy(stitched);lookup.update(code='synthetic_product_lookup',name='Synthetic saved product lookup')
    terms=lookup['nodes'][4]['parameters'];terms['implementation']='saved_product_forwards_v1';del terms['cutover']
    imports=[dict(node(id,'published_source',{'selection':'all_output_points_v1'}),publishedSource=dict(catalogId=manifest['catalogId'],curveId=c['id'],version=1,outputNode='result')) for id,c in zip(('peak','off'),product_rows)]
    lookup['nodes']=[*imports,node('hours','power_intervals',terms,{'peak':'peak','offPeak':'off'}),node('result','output',inputs={'values':'hours'})]
    lookup_body=dict(definition=lookup,snapshotId=frame['id'])
    lookup_preview=client.call('custom-curves/preview',lookup_body)
    assert lookup_preview['rows']==daily_preview['rows'] and lookup_preview['summary']==daily_preview['summary']
    saved_lookup=client.call('custom-curves/save',dict(lookup_body,requestId=str(uuid4()),layout={}))
    # A malformed retained draft must refuse at SQL publication too.
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
    INSERT INTO market_data.custom_curve_edges VALUES('{manifest['catalogId']}','{saved_lookup['id']}',1,'peak','extra','off');
    RESET SESSION AUTHORIZATION;
    DO $$BEGIN
     UPDATE market_data.custom_curve_versions SET state='published' WHERE(catalog_id,curve_id,version)=('{manifest['catalogId']}','{saved_lookup['id']}',1);
     RAISE EXCEPTION 'Expected saved product port refusal' USING ERRCODE='XX000';
    EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'power_interval_inputs' THEN RAISE;END IF;END$$;ROLLBACK;""")
    bad=deepcopy(product_rows[0]['definition']);bad.update(code='synthetic_weighted_product',name='Synthetic nontransparent product')
    weighted=next(n for n in bad['nodes'] if n['id']=='daily_map');weighted['contractWeights'][0]['weight']='2'
    bad_saved=client.call('custom-curves/save',dict(definition=bad,snapshotId=frame['id'],requestId=str(uuid4()),layout={}))
    client.call('profile',{'profile':'approver'})
    bad_saved=client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=bad_saved['id'],version=1,generation=bad_saved['generation']))
    client.call('profile',{'profile':'editor'})
    invalid=deepcopy(lookup_body);invalid['definition']['nodes'][0]['publishedSource']['curveId']=bad_saved['id']
    client.call('custom-curves/preview',invalid,expected=422)
    client.call('profile',{'profile':'approver'})
    client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=saved_lookup['id'],version=1,generation=saved_lookup['generation']))
    client.call('profile',{'profile':'editor'})
    from check_power_booked_roll import daily_price_shape
    daily_price_shape(client,query,manifest,frame,lookup_body)
    print('PASS saved power products: two standalone splices, exact published inputs, identical native prices/ownership and saved lookup publication',flush=True)
    print('PASS daily/monthly HTTP: shared weekend cutoff, decreasing cutoff refusal, 24 native daily/monthly points over 1008 hours, holiday16/8, saved publication',flush=True)
    body = dict(definition=definition, snapshotId=frame['id'])
    preview = client.call('custom-curves/preview', body)
    assert len(preview['rows']) == 721, preview.get('summary')
    assert preview['summary']['anchors'] == 2
    assert preview['summary']['peakIntervals'] == 304
    assert preview['summary']['offPeakIntervals'] == 417
    assert sum(1 for r in preview['rows'] if r[2] == '60') == 304
    assert sum(1 for r in preview['rows'] if r[2] == '40') == 417
    saved = client.call('custom-curves/save', dict(body, requestId=str(uuid4()), layout={}))
    assert next(n for n in saved['definition']['nodes'] if n['id'] == 'hours')['parameters'] == definition['nodes'][2]['parameters']
    malformed = deepcopy(body)
    del malformed['definition']['nodes'][2]['parameters']['implementation']
    client.call('custom-curves/preview', malformed, expected=422)
    client.call('profile', {'profile': 'approver'})
    published = client.call('custom-curves/publish', dict(requestId=str(uuid4()), id=saved['id'], version=saved['version'], generation=saved['generation']))
    assert published['state'] == 'published'
    replay = client.call('custom-curves/preview', dict(body, id=saved['id'], version=saved['version']))
    assert replay['rows'] == preview['rows']
    old_known = client.call('interval-series')['knownAt']
    binding = dict(requestId=str(uuid4()), definitionRevisionId=manifest['revisionId'], location='test_hub_a', market='DA',
                   deliveryFrom='2031-11-01T04:00:00Z', deliveryTo='2031-12-01T05:00:00Z', curveId=saved['id'],
                   policy='follow_location_activation_v1', reason='Synthetic physical power baseline',
                   segments=[dict(effectiveFrom='2020-01-01', effectiveTo='2032-01-01', version=1, outputNode='result')])
    activated = client.call('power-location-bindings', binding)
    effective = client.call('custom-curves/effective-preview', dict(curveId=saved['id'], snapshotId=frame['id'], policy='complete_effective_timeline_v1'))
    assert effective['preview']['rows'] == preview['rows']
    assert client.call('power-location-bindings', binding) == activated
    # A direct SQL retry must recheck publishing authority after a read waits,
    # even when the actor still has permission to inspect the existing result.
    import json
    prior=json.loads(query(f"SELECT jsonb_build_object('actor',actor_id,'payload',payload) FROM market_data.power_location_binding_requests WHERE(catalog_id,request_id)=('{manifest['catalogId']}','{binding['requestId']}');"))
    encoded="'"+json.dumps(prior['payload']).replace("'","''")+"'::jsonb"
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    query(f"""BEGIN;
     DO $delay$DECLARE body text;BEGIN body=pg_get_functiondef('security.read_custom_curve_activation(uuid,uuid,integer)'::regprocedure);
      IF length(body)-length(replace(body,E'BEGIN\n',''))<>length(E'BEGIN\n') THEN RAISE EXCEPTION 'Expected one reader body';END IF;
      EXECUTE replace(body,E'BEGIN\n',E'BEGIN\n PERFORM pg_sleep(3);\n');END$delay$;
     INSERT INTO security.role_grants SELECT gen_random_uuid(),'{prior['actor']}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '5 minutes',false FROM security.roles WHERE code='viewer';
     UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id='{prior['actor']}' AND NOT revoked
      AND role_id IN(SELECT role_id FROM security.role_permissions WHERE permission='SealMarketDefinitions');
     SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
     DO $retry$DECLARE p jsonb:={encoded};started timestamptz:=clock_timestamp();BEGIN
      PERFORM security.publish_power_location('{manifest['catalogId']}',(p->>'definition')::uuid,p->>'location',p->>'market',(p->>'from')::timestamptz,(p->>'to')::timestamptz,
       (p->>'curve')::uuid,'{binding['requestId']}',p->>'policy',p->'activation');
      RAISE EXCEPTION 'Expired publishing authority returned a retry outcome' USING ERRCODE='XX000';
     EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'power_location_denied' THEN RAISE;END IF;
      IF clock_timestamp()-started<interval '2.9 seconds' THEN RAISE EXCEPTION 'The delayed result reader was not exercised';END IF;END$retry$;ROLLBACK;""")
    print('PASS power binding retry refuses publishing authority that expires during result inspection',flush=True)
    assert client.call('power-location-bindings', dict(binding, requestId=str(uuid4()))) == activated
    conflict = dict(binding, requestId=str(uuid4()), market='RT')
    client.call('power-location-bindings', conflict, expected=422)
    from ppa_demo_terms import profile_terms
    from exact_values import fraction
    client.call('profile', {'profile': 'trader'})
    def physical(known):
        return dict(snapshotId=frame['id'], definitionRevisionId=manifest['revisionId'], referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'], scope='draft', selectedIds=[], explicitSubset=False,
                    terms=profile_terms(manifest), quantity='1', coverageFrom=binding['deliveryFrom'], coverageTo=binding['deliveryTo'],
                    knownAt=known, pendingActuals='require_actuals_v1', detailMonths=['2031-11-01'])
    before = client.call('ppa/preview', physical(old_known), expected=422)
    assert before['code'] == 'location_valuation_missing', before
    current_known = client.call('interval-series')['knownAt']
    valued = client.call('ppa/preview', physical(current_known))
    assert fraction(valued['valueExcludingOverdue']) == 6080, valued['valueExcludingOverdue']
    evidence = valued['receipt']['powerLocations']
    assert len(evidence) == 1 and evidence[0]['output']['curveId'] == saved['id'] and evidence[0]['output']['version'] == 1
    assert evidence[0]['activation']['revision'] == 1 and evidence[0]['policy'] == 'follow_location_activation_v1'
    print('PASS physical Side HTTP: ordinary profile/linked fixed Side cash 6080, exact assignment evidence, idempotent binding, historical absence and conflicting market refusal', flush=True)
    client.call('profile', {'profile': 'editor'})
    revised = deepcopy(definition)
    revised['name'] = 'Synthetic flat power revised'
    draft = client.call('custom-curves/save', dict(body, definition=revised, requestId=str(uuid4()), layout={}, id=saved['id'], version=1, generation=published['generation'], expectedState='published'))
    # Direct SQL bypasses managed validation; publication must still refuse it.
    non_power = query(f"SELECT code FROM market_data.curves WHERE revision_id='{manifest['revisionId']}' AND commodity_code<>'test_power' ORDER BY code LIMIT 1;")
    assert non_power
    token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
        UPDATE market_data.custom_curve_node_sources SET source_curve_code='{non_power}'
         WHERE(catalog_id,curve_id,version,node_id)=('{manifest['catalogId']}','{saved['id']}',2,'off');
        RESET SESSION AUTHORIZATION;
        DO $$BEGIN
         UPDATE market_data.custom_curve_versions SET state='published' WHERE(catalog_id,curve_id,version)=('{manifest['catalogId']}','{saved['id']}',2);
         RAISE EXCEPTION 'Expected mixed-unit publication refusal' USING ERRCODE='XX000';
        EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'power_interval_units' THEN RAISE;END IF;END$$;ROLLBACK;""")
    client.call('profile', {'profile': 'approver'})
    second_publication = client.call('custom-curves/publish', dict(requestId=str(uuid4()), id=saved['id'], version=2, generation=draft['generation']))
    client.call('custom-curves/activation', dict(requestId=str(uuid4()), curveId=saved['id'], expectedRevision=1,
                policy='complete_effective_timeline_v1', reason='Synthetic version change',
                segments=[dict(effectiveFrom='2020-01-01', effectiveTo='2032-01-01', version=2, outputNode='result')]))
    client.call('profile', {'profile': 'trader'})
    old_result = client.call('ppa/preview', physical(current_known))
    fresh_result = client.call('ppa/preview', physical(client.call('interval-series')['knownAt']))
    assert old_result['receipt']['powerLocations'][0]['output']['version'] == 1
    assert fresh_result['receipt']['powerLocations'][0]['output']['version'] == 2
    assert fresh_result['receipt']['powerLocations'][0]['activation']['revision'] == 2
    assert fraction(old_result['valueExcludingOverdue']) == fraction(fresh_result['valueExcludingOverdue']) == 6080
    client.call('profile', {'profile': 'viewer'})
    client.call('power-location-bindings', dict(binding, requestId=str(uuid4())), expected=403)
    print('PASS flat power history/authority: SQL mixed-unit publication refusal, old/current activation versions, exact PV parity and unauthorized write refusal', flush=True)
    stored = query("SELECT count(*) FROM market_data.custom_curve_node_power_intervals WHERE curve_id='" + saved['id'] + "';")
    assert stored == '2', stored
    print('PASS flat power HTTP: saved exact TOU/profile bindings, 721 DST intervals, 304/417 partition, live forward prices and explicit policy refusal', flush=True)

    from check_power_product_resolution import forward_products,actual_products
    forward_products(client,query,manifest,frame,product)
    if booked_roll:
        from check_power_booked_roll import booked_forward,in_term_booking,shaped_forward
        in_term_booking(client,query,manifest,frame,product)
        booked_forward(client,manifest,frame,product)
        shaped_forward(client,manifest,frame,product)
    actual_products(client,query,manifest,frame,product)
    from check_power_output_settlement import run_checks as settlement_checks
    settlement_checks(client,query,manifest,frame,revised,second_publication,binding)
