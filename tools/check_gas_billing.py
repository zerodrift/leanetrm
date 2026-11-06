from check_physical_operations import january_delivery_frame
"""Synthetic native-day cash determinations, immutable corrections and recovery."""
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query=None, manifest=None):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace')
    before={'snapshotId':initial_frame(workspace,'2020-12-18')['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    after={**before,'snapshotId':january_delivery_frame(workspace)}
    catalog=c.call('gas/catalog',before)
    point=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    terms={**before,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':point['definitionRevisionId'],'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
      'physicalSides':[{'code':'receipt','pipeline':point['pipeline'],'zone':point['zone'],'deliveryPoint':point['deliveryPoint'],'product':point['product'],'direction':'receive','deliveryFrom':'2021-01-01','deliveryTo':'2021-01-03','quantityBasis':'per_operating_day','quantity':'100','link':None}],
      'cashSides':[{'code':'payment','role':'consideration','direction':'pay','source':'receipt','stage':'delivery','price':'3','tariffCode':None,'paymentAt':'2021-02-02T00:00:00Z'},
                   {'code':'fee','role':'fee','direction':'receive','source':'receipt','stage':'delivery','price':'0.1','tariffCode':None,'paymentAt':'2021-02-02T00:00:00Z'}]}
    booked=c.call('gas/composition/book',terms);trade=booked['tradeId']
    original_pv=c.call('gas/composition/editor',dict(context=before,tradeId=trade))['valuation']['presentValue']
    view=c.call('gas/quantities/read',{'context':after,'tradeId':trade,'selectionPolicy':'operational_projection_v1'})
    obligation=view['rows'][0]['obligationId']
    def quantity(amount, previous=None):
        return c.call('gas/quantities/record',dict(context=after,tradeId=trade,expectedRevision=1,operationId=str(uuid4()),obligationId=obligation,kind='allocated',status='final',day='2021-01-01',quantity=amount,evidence='Synthetic final allocation',expectedPreviousId=previous))
    preview=dict(context=after,tradeId=trade,expectedRevision=1,cashSlot=1,day='2021-01-01',selectionPolicy='final_allocated_billing_v1',profile='gas_final_daily_fixed_cash_v1',decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    c.call('gas/billing/preview',preview,expected=422)
    allocation=quantity('100')
    first=c.call('gas/billing/preview',preview)
    assert first['amount']=='-300' and first['contribution']=='-300' and first['previousId'] is None
    request=dict(preview=preview,operationId=str(uuid4()),previewHash=first['previewHash'],sourceId=first['sourceId'],expectedPreviousId=None,correctionDueAt=None,reason=None,evidence='Synthetic original billing review')
    original=c.call('gas/billing/determine',request)
    assert c.call('gas/billing/determine',request)==original
    history=c.call('gas/billing/read',dict(context=after,tradeId=trade))
    retained={**after,'knowledgeMode':'retained','contextId':history['context']['contextId']}
    assert len(history['determinations'])==1
    stale=c.call('gas/billing/preview',preview)
    correction=quantity('98',allocation['revisionId'])
    assert c.call('gas/billing/determine',{**request,'operationId':str(uuid4())})['determinationId']==original['determinationId']
    c.call('gas/billing/determine',{**request,'operationId':str(uuid4()),'previewHash':stale['previewHash'],'expectedPreviousId':original['determinationId'],'reason':'quantity_correction','correctionDueAt':'2021-03-02T00:00:00Z','evidence':'Synthetic stale correction review'},expected=409)
    second=c.call('gas/billing/preview',preview)
    assert second['amount']=='-294' and second['contribution']=='6' and second['previousId']==original['determinationId']
    adjust=dict(preview=preview,operationId=str(uuid4()),previewHash=second['previewHash'],sourceId=second['sourceId'],expectedPreviousId=original['determinationId'],correctionDueAt='2021-03-02T00:00:00Z',reason='quantity_correction',evidence='Synthetic corrected allocation statement')
    c.call('gas/billing/determine',{**adjust,'reason':None},expected=422)
    from concurrent.futures import ThreadPoolExecutor
    other=PreviewClient(url);other.bootstrap();other.call('profile',{'profile':'trader'})
    other_workspace=other.call('workspace')
    other_context={**after,'snapshotId':january_delivery_frame(other_workspace)}
    other_preview={**preview,'context':other_context}
    other_review=other.call('gas/billing/preview',other_preview)
    clients=[c,other]
    concurrent=[adjust,{**adjust,'operationId':str(uuid4()),'preview':other_preview,'previewHash':other_review['previewHash']}]
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes=list(pool.map(lambda pair:pair[0].call('gas/billing/determine',pair[1]),zip(clients,concurrent)))
    updated=outcomes[0]
    assert outcomes[1]['determinationId']==updated['determinationId']
    assert c.call('gas/billing/determine',adjust)==updated
    assert c.call('gas/billing/determine',request)==original
    assert c.call('gas/billing/status',dict(operationId=adjust['operationId']))==updated
    assert c.call('gas/billing/determine',{**adjust,'operationId':str(uuid4())})['determinationId']==updated['determinationId']
    refreshed=c.call('gas/billing/preview',preview)
    assert c.call('gas/billing/determine',{**adjust,'operationId':str(uuid4()),'previewHash':refreshed['previewHash'],'expectedPreviousId':updated['determinationId']})['determinationId']==updated['determinationId']
    c.call('gas/billing/determine',{**adjust,'evidence':'different payload'},expected=409)
    current=c.call('gas/billing/read',dict(context=after,tradeId=trade))
    assert [r['rounded_total'] for r in current['determinations']]==['-300','-294']
    assert [r['contribution'] for r in current['determinations']]==['-300','6']
    assert len(c.call('gas/billing/read',dict(context=retained,tradeId=trade))['determinations'])==1
    c.call('gas/billing/determine',{**adjust,'operationId':str(uuid4()),'preview':{**preview,'context':retained}},expected=422)
    c.call('gas/billing/preview',{**preview,'day':'2021-01-02'},expected=422)
    c.call('gas/billing/preview',{**preview,'decimalPlaces':3},expected=422)
    fee=c.call('gas/billing/preview',{**preview,'cashSlot':2})
    assert fee['amount']=='9.8' and fee['contribution']=='9.8'
    if query:
        import json
        principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
        booking_permissions=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='BookTrade' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
        grant=str(uuid4())
        try:
            query(f"DELETE FROM security.role_permissions WHERE permission='BookTrade' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
            fee_request=dict(preview={**preview,'cashSlot':2},operationId=str(uuid4()),previewHash=fee['previewHash'],sourceId=fee['sourceId'],expectedPreviousId=None,correctionDueAt=None,reason=None,evidence='Synthetic amend-only billing review')
            assert c.call('gas/billing/determine',fee_request)['amount']=='9.8'
            editor=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='editor';")
            query(f"INSERT INTO security.role_grants VALUES('{grant}','{editor}','a0000000-0000-0000-0000-000000000002','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);")
            other.call('profile',{'profile':'editor'})
            duplicate={**request,'operationId':str(uuid4()),'preview':{**preview,'context':other_context}}
            assert other.call('gas/billing/determine',duplicate)['determinationId']==original['determinationId']
        finally:
            encoded=booking_permissions.replace("'","''")
            query(f"DELETE FROM security.role_grants WHERE id='{grant}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded}'::jsonb) ON CONFLICT DO NOTHING;")
    zero=quantity('0',correction['revisionId'])
    p=c.call('gas/billing/preview',preview)
    assert p['amount']=='0' and p['contribution']=='294'
    if query:
        from check_gas_billing_authority import check as authority_check
        pending={**adjust,'operationId':str(uuid4()),'previewHash':p['previewHash'],'sourceId':p['sourceId'],'expectedPreviousId':updated['determinationId'],'evidence':'Synthetic pending zero correction'}
        authority_check(query,manifest,adjust,pending,dict(sourceId=p['sourceId'],amount='0',numerator='0',denominator='1'),p['context']['contextId'])
        from check_booked_side_programs import runtime_query
        malformed=[]
        for due in ['2021-03-02 00:00:00','2021-03-02T00:00:00.0000001Z','2021-03-02T24:00:00Z']:
            malformed.append({**pending,'correctionDueAt':due})
        malformed.extend([{**pending,'preview':{**preview,'day':'today'}},{**pending,'extra':'unreviewed'},
            {k:v for k,v in pending.items() if k!='expectedPreviousId'},
            {**pending,'preview':{**preview,'decimalPlaces':'2'}}])
        calc=json.dumps(dict(sourceId=p['sourceId'],amount='0',numerator='0',denominator='1')).replace("'","''")
        for body in malformed:
            encoded=json.dumps({**body,'operationId':str(uuid4())}).replace("'","''")
            runtime_query(query,manifest,f"DO $$BEGIN BEGIN PERFORM security.record_gas_billing('{manifest['catalogId']}','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{p['context']['contextId']}','{calc}'::jsonb);RAISE EXCEPTION 'malformed_billing_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_billing_input' THEN RAISE;END IF;END;END$$;")

        from check_booked_side_programs import runtime_query
        wide='CREATE POLICY billing_test_wide ON trading.gas_cash_determinations FOR SELECT TO openetrm_runtime USING(true);'
        runtime_query(query,manifest,"DO $$BEGIN IF EXISTS(SELECT 1 FROM trading.gas_cash_determinations) THEN RAISE EXCEPTION 'billing_authority_bypassed';END IF;END$$;",profile='viewer',prefix=wide,commit=False)
        query("DO $$BEGIN UPDATE trading.gas_cash_determinations SET rounded_total=42;RAISE EXCEPTION 'mutable billing';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;")
    assert c.call('gas/composition/editor',dict(context=before,tradeId=trade))['valuation']['presentValue']==original_pv
    c.call('profile',{'profile':'risk'})
    c.call('gas/billing/determine',adjust,expected=403)
    print('PASS gas billing: final day, missing evidence, 300→294 correction, fee sign, zero, history, stale review, retries, semantic duplicates and current authority')
    return dict(tradeId=trade,bindingId=query(f"SELECT instrument_id FROM trading.gas_trade_revisions WHERE trade_id='{trade}' AND revision=1;"),context=after,originalId=original['determinationId'],correctionId=updated['determinationId'])
