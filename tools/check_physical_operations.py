"""Metadata-only physical selection must not erase an authorized held instrument."""
def directory_checks(client, context, query, expected_trade, manifest):
    request=dict(context={**context,'convention':'trading_workspace_v1'},bookIds=[],portfolioIds=[],offset=0,limit=64)
    directory=client.call('physical/operations/directory',request)
    assert any(p['tradeId']==expected_trade for p in directory['positions'])
    assert all(p['instrumentType'] in ('physical_gas','physical_crude','physical_lng') for p in directory['positions'])
    assert all(not any(k in p for k in ('presentValue','mark','executionPnl')) for p in directory['positions'])
    selected=next(p for p in directory['positions'] if p['tradeId']==expected_trade)
    details=client.call('trading/gas',dict(context=request['context'],tradeId=selected['tradeId'],bindingId=selected['bindingId'],revision=selected['revision'],instrumentType='physical_gas'))
    # Native decimal terms cross the presentation boundary as exact text, including
    # quantities whose significant digits exceed the browser number format.
    for name in ('quantity','fixed_price','usage_fee'):
        assert details['head'][name] is None or isinstance(details['head'][name],str),name
    for term in details['components']:
        for name in ('independent_quantity','fraction','price'):
            assert term[name] is None or isinstance(term[name],str),name
    quantities=client.call('gas/quantities/read',dict(context={**context,'convention':'physical_gas_fixed_quantity_v4'},tradeId=selected['tradeId'],selectionPolicy='operational_projection_v1',includeHistory=False))
    delivery=quantities['delivery']
    assert delivery['tradeId']==selected['tradeId'] and delivery['bindingId']==selected['bindingId'] and delivery['revision']==selected['revision']
    assert len(delivery['rows'])==len(quantities['rows'])
    for interval,native in zip(delivery['rows'],quantities['rows']):
        assert interval['key']==native['obligationId']+':'+native['day']
        assert interval['origin']['obligationId']==native['obligationId']
        assert interval['quantitySpecification']==native['quantitySpecification']
        assert interval['start']==native['start'] and interval['end']==native['end']
        assert interval['selectedTrack']==native['kind'] and interval['unit']
        assert [(h['id'],h['status'],h['quantity']) for h in interval['heads']]==[(h['id'],h['status'],h['quantity']) for h in native['heads']]
    retained={**request,'context':{**request['context'],'knowledgeMode':'retained','contextId':directory['context']['contextId']}}
    assert client.call('physical/operations/directory',retained)['positions']==directory['positions']
    from check_physical_delivery_authority import check
    check(client,'gas/quantities/read',dict(context={**context,'convention':'physical_gas_fixed_quantity_v4'},tradeId=selected['tradeId'],selectionPolicy='operational_projection_v1',includeHistory=False),query,manifest)
    # Reproduce a source join disappearing while ordinary trade/source permissions
    # remain: an inventory check must refuse instead of returning an empty directory.
    query('CREATE POLICY physical_ops_hide_gas_metadata ON trading.gas_instruments AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);')
    try:
        refused=client.call('physical/operations/directory',request,expected=422)
        assert refused['code']=='position_facts',refused
    finally:
        query('DROP POLICY physical_ops_hide_gas_metadata ON trading.gas_instruments;')
    assert client.call('physical/operations/directory',request)['positions']==directory['positions']
    # A desk lists its own instrument type; no desk lists every physical trade, exactly as before.
    assert directory['nextOffset'] is None,'this fixture must fit one page for the comparison below'
    for desk,kind in (('gas','physical_gas'),('crude','physical_crude'),('lng','physical_lng')):
        scoped=client.call('physical/operations/directory',{**request,'desk':desk})
        assert scoped['positions']==[p for p in directory['positions'] if p['instrumentType']==kind],desk
        assert scoped['total']==len(scoped['positions']) and scoped['nextOffset'] is None,desk
    assert client.call('physical/operations/directory',{**request,'desk':None})['positions']==directory['positions']
    refused=client.call('physical/operations/directory',{**request,'desk':'power'},expected=422)
    assert refused['code']=='position_desk',refused
    print('PASS physical operations directory: retained metadata, no PV fields, hidden exact sources refuse without erasing held positions; a desk lists only its own trades',flush=True)


def contract_delivery_checks(client, path, request, quantity, start, end, capability, query, manifest):
    """Whole-window products expose their retained total, never a fabricated daily split."""
    from datetime import datetime
    from uuid import uuid4
    response=client.call(path,request)
    view=response['delivery']
    assert view['version']=='physical_delivery_view_v1'
    assert (view['tradeId'],view['bindingId'],view['revision'])==(request['tradeId'],request['bindingId'],request['revision'])
    assert view['periodLabel']=='Delivery window' and view['selectionPolicy']=='contractual_schedule_v1'
    assert view['tracks']==[] and len(view['rows'])==1
    row=view['rows'][0]
    assert row['contractual']==quantity and row['selected']==quantity
    assert row['selectedTrack']=='contractual' and row['sourceStatus']=='contractual' and row['heads']==[]
    instant=lambda value:datetime.fromisoformat(value.replace('Z','+00:00'))
    assert instant(row['start'])==instant(start) and instant(row['end'])==instant(end)
    assert row['origin']['slot']==0 and row['origin']['cell']==0 and row['origin']['obligationId'] is None
    assert all(row['origin'][key] for key in ('sideId','scheduleId','programDigest','root'))
    assert row['unit'] and row['location']['revision']['revisionId'] and row['quantitySpecification']['revision']['revisionId']
    assert next(c for c in view['capabilities'] if c['code']==capability)
    retained={**request,'context':{**request['context'],'knowledgeMode':'retained','contextId':response['context']['contextId']}}
    assert client.call(path,retained)['delivery']==view
    assert client.call(path,{**request,'revision':request['revision']+1},expected=422)['code']=='position_revision'
    client.call(path,{**request,'bindingId':str(uuid4())},expected=422)
    from check_physical_delivery_authority import check
    check(client,path,request,query,manifest)
    print('PASS physical delivery contract: retained window, exact quantity/source/reference scope, capabilities and stale selection refusal',flush=True)


def january_delivery_frame(workspace):
    """The scenarios explicitly operate at the original January 15 08:00 input."""
    frames=[f for f in workspace['frames'] if f['id'] in workspace['initialFrameIds'] and f['at'].startswith('2021-01-15T08:')]
    assert len(frames)==1,'Select one exact original January delivery scenario frame'
    return frames[0]['id']


def mixed_directory_checks(client, query):
    """A mixed book keeps every holding, but this delivery page lists only its native consumers."""
    from preview_client import initial_frame
    from preview_fixture import gas_purchase
    context = dict(snapshotId=initial_frame(client.call('workspace'), '2020-12-18')['id'],
                   convention='physical_gas_fixed_quantity_v4', knowledgeMode='current')
    catalog = client.call('gas/catalog', context)
    booked = []
    for quantity in ('1234', '2345'):
        command = gas_purchase(context, catalog)
        command['physicalSides'][0]['quantity'] = quantity
        booked.append(client.call('gas/composition/book', command))
    request = dict(context={**context, 'convention':'trading_workspace_v1'},
                   bookIds=[], portfolioIds=[], offset=0, limit=64)
    all_facts = client.call('trading/positions', request)
    assert all_facts['nextOffset'] is None
    assert {'operating_right', 'rec_forward', 'heat_rate_call', 'gas_storage'} <= {
        row['instrumentType'] for row in all_facts['positions']}
    retained = {**request['context'], 'knowledgeMode':'retained', 'contextId':all_facts['context']['contextId']}
    expected = [row for row in all_facts['positions'] if row['instrumentType'] in
                ('physical_gas', 'physical_crude', 'physical_lng')]
    assert {(row['tradeId'], row['bindingId']) for row in expected} == {
        (row['tradeId'], row['instrumentId']) for row in booked}
    rows, offset = [], 0
    while True:
        page = client.call('physical/operations/directory', {**request, 'context':retained, 'offset':offset, 'limit':1})
        assert page['context'] == all_facts['context'] and page['asOf'] == all_facts['asOf']
        assert page['total'] == len(expected) and len(page['positions']) == 1, page
        rows.extend(page['positions'])
        if page['nextOffset'] is None:
            break
        assert page['nextOffset'] == len(rows)
        offset = page['nextOffset']
    assert rows == expected, (rows, expected)
    empty = client.call('physical/operations/directory', {**request, 'context':retained, 'offset':len(rows), 'limit':1})
    assert empty['positions'] == [] and empty['total'] == len(rows) and empty['nextOffset'] is None
    assert client.call('trading/positions', {**request, 'context':retained})['positions'] == all_facts['positions']
    print('PASS physical directory: mixed-product Blotter preserved, exact delivery bindings, retained pagination and totals', flush=True)
    # The delivery page must not consume authority for an unrelated product reader.
    reader = 'security.read_gas_storage_positions(uuid,uuid[],uuid[],uuid[])'
    assert query(f"SELECT has_function_privilege('openetrm_runtime','{reader}','EXECUTE');") == 't'
    query(f'REVOKE EXECUTE ON FUNCTION {reader} FROM openetrm_runtime;')
    try:
        refused = client.call('trading/positions', {**request, 'context':retained}, expected=403)
        assert refused['code'] == 'access_denied', refused
        assert client.call('physical/operations/directory', {**request, 'context':retained})['positions'] == expected
    finally:
        query(f'GRANT EXECUTE ON FUNCTION {reader} TO openetrm_runtime;')
    assert client.call('trading/positions', {**request, 'context':retained})['positions'] == all_facts['positions']
    print('PASS physical directory: an unavailable storage reader refuses full positions without blocking native delivery', flush=True)
