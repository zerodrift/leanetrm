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
        assert refused['code']=='risk_contract',refused
    finally:
        query('DROP POLICY physical_ops_hide_gas_metadata ON trading.gas_instruments;')
    assert client.call('physical/operations/directory',request)['positions']==directory['positions']
    print('PASS physical operations directory: retained metadata, no PV fields, hidden exact sources refuse without erasing held positions',flush=True)


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
