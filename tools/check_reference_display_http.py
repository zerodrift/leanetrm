"""Qualify display authoring through the same authorized operations as the UI."""
from urllib.parse import urlencode
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url,query):
    client=PreviewClient(url);client.bootstrap()
    client.call('profile',{'profile':'editor'})
    # A label-only schema change must reach the actual editor read. This catches
    # a separately maintained API allowlist even when its initial labels match.
    definition_sql="SELECT pg_get_functiondef('security.reference_display_editor_fields(text)'::regprocedure);"
    definition=query(definition_sql)
    assert definition.count("'Display symbol'")==1,'The qualified descriptor needs one unit-symbol label'
    try:
        query(definition.replace("'Display symbol'","'Schema-driven symbol'",1))
        descriptor=client.call('reference/editor?table=units')['fields']
        assert descriptor==[{'key':'symbol','label':'Schema-driven symbol','kind':'text','nullable':False,'maxLength':80}], 'The editor must use database field metadata rather than a duplicated API policy.'
    finally:
        query(definition)
        assert query(definition_sql)==definition,'The owned field descriptor was not restored exactly'
    for replacement in ["'absentBound',maximum", "'maxLength',0", "'maxLength','invalid'"]:
        try:
            assert definition.count("'maxLength',maximum")==1
            query(definition.replace("'maxLength',maximum",replacement,1))
            assert client.call('reference/editor?table=units',expected=422)['code']=='reference_edit', 'Invalid field bounds must not become a default editor policy'
        finally:
            query(definition)
            assert query(definition_sql)==definition
    assert client.call('reference/editor?table=units')['fields'][0]['label']=='Display symbol'
    before=client.call('workspace/catalog')
    point=client.call('reference/editor?table=locations&code=test_facility_a')['item']
    assert point['latitude']=='12.345678' and point['longitude']=='-45.678912','Editable location coordinates must cross JSON as exact decimal strings.'
    frame=next(f for f in before['frames'] if f['at'].startswith('2020-12-18'))
    price_request={'snapshotId':frame['id'],'curve':'local_power'}
    price=client.call('evaluate',price_request)
    historical=client.call('market/context',{'effectiveOn':'2020-12-18','mode':'current','selection':'latest_correction'})['context']
    client.call('market/current',{})
    # The commit saves and applies in one transaction: it needs both grants.
    editor=client.call('reference/editor?table=units')
    assert not editor['canEdit'] and 'canApply' not in editor, 'Write without Seal cannot commit a display edit'
    assert client.call('reference/editor?table=units&code=megawatt_hour')['item']['code']=='megawatt_hour'
    assert client.call('reference/editor?table=units&code=',expected=422)['code']=='reference_edit'
    source=editor['sourceRevision']
    original=next(r for r in client.call('reference/catalog?table=units')['rows'] if r['code']=='megawatt_hour')['symbol']
    edits=lambda:query('SELECT count(*) FROM ref_data.display_edits;')
    commit={'operationId':str(uuid4()),'table':'units','code':'megawatt_hour','sourceRevisionId':source['revisionId'],'baseRevisionId':editor['baseRevisionId'],
            'expectedVersion':editor['displayVersion'],'values':{'symbol':original+' sample'}}
    recorded=edits()
    assert client.call('reference/commit',commit,expected=403)['code']=='access_denied'
    assert edits()==recorded and client.call('workspace/catalog')['referenceDisplay']==before['referenceDisplay'], 'A denied commit must not retain its draft'
    # The session token that reference/commit accepted is scoped to matched API
    # endpoints, so its refusal proves the retired paths no longer map.
    for retired in ['reference/save','reference/apply']:
        assert client.call(retired,commit,expected=400)['code']=='request_token'
    client.call('profile',{'profile':'approver'})
    assert not client.call('reference/editor?table=units')['canEdit'], 'Seal without Write cannot commit a display edit'
    grant="INSERT INTO security.role_permissions SELECT id,'WriteReferenceData' FROM security.roles WHERE code='market_data_approver';"
    revoke="DELETE FROM security.role_permissions WHERE permission='WriteReferenceData' AND role_id=(SELECT id FROM security.roles WHERE code='market_data_approver');"
    query(grant)
    try:
        committer=client.call('reference/editor?table=units')
        assert committer['canEdit'] and committer['baseRevisionId']==editor['baseRevisionId'] and committer['displayVersion']==editor['displayVersion']
        for symbol in ['x'*81,'\U0001f4a1'*81,'\u00a0','\u2003','\u202f','\u3000','x\x85','\x01']:
            assert client.call('reference/commit',{**commit,'operationId':str(uuid4()),'values':{'symbol':symbol}},expected=422)['code']=='reference_edit'
        assert client.call('reference/commit',{**commit,'operationId':str(uuid4()),'values':{'factor_numerator':'2'}},expected=422)['code']=='reference_edit'
        assert edits()==recorded
        committed=client.call('reference/commit',commit)
        assert committed=={'revisionId':commit['operationId'],'displayRevisionId':commit['operationId'],'version':editor['displayVersion']+1}
        recorded=edits()
        assert client.call('reference/commit',commit)==committed and edits()==recorded, 'An exact retry returns its first outcome and writes nothing'
        assert client.call('reference/commit',{**commit,'values':{'symbol':original+' other'}},expected=409)['code']=='reference_conflict'
        stale={**commit,'operationId':str(uuid4()),'baseRevisionId':committed['displayRevisionId'],'values':{'symbol':original+' stale'}}
        assert client.call('reference/commit',stale,expected=409)['code']=='reference_conflict'
        assert edits()==recorded, 'A stale version must roll back its saved draft'
        current=client.call('workspace/catalog')
        assert current['references']==before['references'] and current['definitions']==before['definitions']
        display=current['unitReferenceDisplay']
        assert display['sourceRevision']==source and display['displayRevision']['revisionId']==committed['revisionId']
        assert display['references']['units']==[{'code':'megawatt_hour','symbol':original+' sample'}]
        history=client.call('reference/editor?table=units')['versions']
        assert history[0]['revision_id']==committed['revisionId'] and all(v['state']=='sealed' for v in history), 'History lists applied display versions only'
        converted=client.call('units/convert',{'value':'2','from':'test_power_quantity','to':'test_power_quantity','decimalPlaces':None,'tieRule':None})
        assert converted['value']=='2' and converted['formatted']=='2 '+original+' sample'
        assert converted['target']['revision']==source
        after_price=client.call('evaluate',price_request)
        assert after_price['points']==price['points'] and after_price['numericalPolicy']==price['numericalPolicy']
        client.call('market/context',{'effectiveOn':historical['effectiveOn'],'knownAt':historical['knownAt'],'mode':'at','selection':'latest_correction'})
        past=client.call('workspace/catalog')
        assert past['referenceDisplay'] is None and past['unitReferenceDisplay'] is None
        assert not client.call('reference/editor?table=units')['canEdit']
        assert client.call('reference/commit',{**stale,'expectedVersion':committed['version']},expected=422)['code']=='historical_read_only'
        client.call('market/current',{})
        assert edits()==recorded

        def commit_metadata(table,code,values):
            client.call('profile',{'profile':'approver'})
            draft=client.call('reference/editor?table='+table)
            return client.call('reference/commit',{'operationId':str(uuid4()),'table':table,'code':code,'sourceRevisionId':draft['sourceRevision']['revisionId'],
                'baseRevisionId':draft['baseRevisionId'],'expectedVersion':draft['displayVersion'],'values':values})

        # Restore through another committed version; immutable audit records stay retained.
        commit_metadata('units','megawatt_hour',{'symbol':original})
        assert client.call('workspace/catalog')['unitReferenceDisplay']['references']==before['unitReferenceDisplay']['references']
        supplementary='\U0001f4a1'*80
        commit_metadata('units','megawatt_hour',{'symbol':supplementary})
        converted=client.call('units/convert',{'value':'2','from':'test_power_quantity','to':'test_power_quantity','decimalPlaces':None,'tieRule':None})
        assert converted['value']=='2' and converted['formatted']=='2 '+supplementary
        assert converted['target']['revision']==source, 'Unicode display formatting changed the exact quantity reference'
        commit_metadata('units','megawatt_hour',{'symbol':original})

        precise={'name':'Synthetic precise facility','latitude':'12.123456789012345678','longitude':'-45.987654321098765432'}
        commit_metadata('locations','test_facility_a',precise)
        displayed=client.call('workspace/catalog')['referenceDisplay']['references']['locations']
        selected=next(r for r in displayed if r['code']=='test_facility_a')
        assert selected['latitude']==precise['latitude'] and selected['longitude']==precise['longitude'],'Applied display coordinates must preserve exact decimal strings too.'
        selected=client.call('reference/editor?table=locations&code=test_facility_a')['item']
        commit_metadata('locations','test_facility_a',{'name':'Synthetic renamed facility','latitude':selected['latitude'],'longitude':selected['longitude']})
        renamed=client.call('reference/editor?table=locations&code=test_facility_a')['item']
        assert renamed['latitude']==precise['latitude'] and renamed['longitude']==precise['longitude']
        commit_metadata('locations','test_facility_a',{key:point[key] for key in ['name','latitude','longitude']})

        def navigation_categories():
            client.call('profile',{'profile':'viewer'})
            return {curve['code']:curve['category'] for curve in client.call('workspace/navigation')['curves']}
        listed=navigation_categories()
        assert None in listed.values(),'The fixture needs an interest-rate curve, which has no commodity'
        power=next(category for category in listed.values() if category and category['code']=='test_power')
        commit_metadata('commodities','test_power',{'name':'Synthetic relabelled power'})
        relabelled={**power,'name':'Synthetic relabelled power'}
        assert navigation_categories()=={code:relabelled if category==power else category for code,category in listed.items()},'A commodity label must rename only its own curves'
        commit_metadata('commodities','test_power',{'name':power['name']})
        assert navigation_categories()==listed
        print('PASS reference HTTP: an applied commodity label renames only its own curves; interest-rate curves have no commodity',flush=True)

        product=client.call('reference/catalog?table=futures_products')['rows'][0]
        def product_display():
            return client.call('reference/futures-product?'+urlencode({'code':product['code']}))['referenceDisplay']
        assert product_display()['references'].get('futures_products') is None
        commit_metadata('futures_products',product['code'],{'name':'Synthetic relabelled future'})
        assert product_display()['references']['futures_products']==[{'code':product['code'],'name':'Synthetic relabelled future'}], 'The product read must show its committed display label'
        commit_metadata('futures_products',product['code'],{'name':product['name']})
        assert product_display()['references'].get('futures_products') is None
    finally:
        query(revoke)
    client.call('profile',{'profile':'approver'})
    assert not client.call('reference/editor?table=units')['canEdit']
    client.call('reference/commit',{**commit,'operationId':str(uuid4())},expected=403)
    client.call('profile',{'profile':'viewer'})
    assert not client.call('reference/editor?table=units')['canEdit']
    client.call('reference/commit',{**commit,'operationId':str(uuid4())},expected=403)
    print('PASS reference HTTP: one-step display commit needs Write and Seal, replays its first outcome, rejects conflicting and stale requests atomically, keeps unit identities and PV, original historical metadata, product display labels and retired save/apply routes',flush=True)
