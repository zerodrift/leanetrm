"""Qualify display authoring through the same authorized operations as the UI."""
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
    editor=client.call('reference/editor?table=units')
    assert editor['canEdit'] and not editor['canApply']
    assert client.call('reference/editor?table=units&code=megawatt_hour')['item']['code']=='megawatt_hour'
    assert client.call('reference/editor?table=units&code=',expected=422)['code']=='reference_edit'
    source=editor['sourceRevision']
    original=next(r for r in client.call('reference/catalog?table=units')['rows'] if r['code']=='megawatt_hour')['symbol']
    save={'operationId':str(uuid4()),'table':'units','code':'megawatt_hour','sourceRevisionId':source['revisionId'],'baseRevisionId':editor['baseRevisionId'],'values':{'symbol':original+' sample'}}
    for symbol in ['x'*81,'\U0001f4a1'*81,'\u00a0','\u2003','\u202f','\u3000','x\x85','\x01']:
        invalid={**save,'operationId':str(uuid4()),'values':{'symbol':symbol}}
        assert client.call('reference/save',invalid,expected=422)['code']=='reference_edit'
    assert client.call('reference/save',{**save,'values':{'factor_numerator':'2'}},expected=422)['code']=='reference_edit'
    saved=client.call('reference/save',save)
    assert client.call('reference/save',save)==saved
    assert client.call('workspace/catalog')['referenceDisplay']==before['referenceDisplay']
    apply={'operationId':str(uuid4()),'table':'units','sourceRevisionId':source['revisionId'],'revisionId':saved['revisionId'],'expectedVersion':editor['displayVersion']}
    client.call('reference/apply',apply,expected=403)
    client.call('profile',{'profile':'approver'})
    applied=client.call('reference/apply',apply)
    assert client.call('reference/apply',apply)==applied
    current=client.call('workspace/catalog')
    assert current['references']==before['references'] and current['definitions']==before['definitions']
    display=current['unitReferenceDisplay']
    assert display['sourceRevision']==source and display['displayRevision']['revisionId']==saved['revisionId']
    assert display['references']['units']==[{'code':'megawatt_hour','symbol':original+' sample'}]
    converted=client.call('units/convert',{'value':'2','from':'test_power_quantity','to':'test_power_quantity','decimalPlaces':None,'tieRule':None})
    assert converted['value']=='2' and converted['formatted']=='2 '+original+' sample'
    assert converted['target']['revision']==source
    after_price=client.call('evaluate',price_request)
    assert after_price['points']==price['points'] and after_price['numericalPolicy']==price['numericalPolicy']
    client.call('reference/apply',{**apply,'operationId':str(uuid4())},expected=409)
    client.call('market/context',{'effectiveOn':historical['effectiveOn'],'knownAt':historical['knownAt'],'mode':'at','selection':'latest_correction'})
    past=client.call('workspace/catalog')
    assert past['referenceDisplay'] is None and past['unitReferenceDisplay'] is None
    assert not client.call('reference/editor?table=units')['canApply']
    assert client.call('reference/apply',{**apply,'operationId':str(uuid4()),'expectedVersion':applied['version']},expected=422)['code']=='historical_read_only'
    client.call('market/current',{})
    # Restore through another reviewed version; immutable audit records stay retained.
    client.call('profile',{'profile':'editor'})
    editor=client.call('reference/editor?table=units')
    restored=client.call('reference/save',{**save,'operationId':str(uuid4()),'baseRevisionId':editor['baseRevisionId'],'values':{'symbol':original}})
    client.call('profile',{'profile':'approver'})
    client.call('reference/apply',{**apply,'operationId':str(uuid4()),'revisionId':restored['revisionId'],'expectedVersion':editor['displayVersion']})
    assert client.call('workspace/catalog')['unitReferenceDisplay']['references']==before['unitReferenceDisplay']['references']

    def apply_metadata(table,code,values):
        client.call('profile',{'profile':'editor'})
        draft=client.call('reference/editor?table='+table)
        saved=client.call('reference/save',{'operationId':str(uuid4()),'table':table,'code':code,'sourceRevisionId':draft['sourceRevision']['revisionId'],'baseRevisionId':draft['baseRevisionId'],'values':values})
        client.call('profile',{'profile':'approver'})
        client.call('reference/apply',{'operationId':str(uuid4()),'table':table,'sourceRevisionId':draft['sourceRevision']['revisionId'],'revisionId':saved['revisionId'],'expectedVersion':draft['displayVersion']})

    supplementary='\U0001f4a1'*80
    apply_metadata('units','megawatt_hour',{'symbol':supplementary})
    converted=client.call('units/convert',{'value':'2','from':'test_power_quantity','to':'test_power_quantity','decimalPlaces':None,'tieRule':None})
    assert converted['value']=='2' and converted['formatted']=='2 '+supplementary
    assert converted['target']['revision']==source, 'Unicode display formatting changed the exact quantity reference'
    apply_metadata('units','megawatt_hour',{'symbol':original})

    precise={'name':'Synthetic precise facility','latitude':'12.123456789012345678','longitude':'-45.987654321098765432'}
    apply_metadata('locations','test_facility_a',precise)
    displayed=client.call('workspace/catalog')['referenceDisplay']['references']['locations']
    selected=next(r for r in displayed if r['code']=='test_facility_a')
    assert selected['latitude']==precise['latitude'] and selected['longitude']==precise['longitude'],'Applied display coordinates must preserve exact decimal strings too.'
    selected=client.call('reference/editor?table=locations&code=test_facility_a')['item']
    apply_metadata('locations','test_facility_a',{'name':'Synthetic renamed facility','latitude':selected['latitude'],'longitude':selected['longitude']})
    renamed=client.call('reference/editor?table=locations&code=test_facility_a')['item']
    assert renamed['latitude']==precise['latitude'] and renamed['longitude']==precise['longitude']
    apply_metadata('locations','test_facility_a',{key:point[key] for key in ['name','latitude','longitude']})
    client.call('profile',{'profile':'viewer'})
    assert not client.call('reference/editor?table=units')['canEdit']
    client.call('reference/save',{**save,'operationId':str(uuid4())},expected=403)
    print('PASS reference HTTP: immutable display save/replay/review/apply, exact unit identities and PV unchanged, stale selection rejected, historical original metadata, current authority and reviewed restore',flush=True)
