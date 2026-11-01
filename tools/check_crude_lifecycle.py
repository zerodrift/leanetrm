"""Independent physical/cash lifecycle qualification in an owned disposable database."""
import json
from decimal import Decimal
from uuid import uuid4
from check_booked_side_programs import runtime_query
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest, booked):
    ties=query("SELECT trim_scale(trading.crude_round_cash(value,2)) FROM (VALUES(1,0.005::numeric),(2,0.015),(3,1.005),(4,1.015),(5,-0.005),(6,-0.015),(7,-1.005),(8,-1.015)) AS oracle(ordinal,value) ORDER BY ordinal;")
    assert ties.splitlines()==['0','0.02','1','1.02','0','-0.02','-1','-1.02'], ties
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', {'profile': 'trader'})
    workspace = c.call('workspace')
    before = dict(snapshotId=initial_frame(workspace, '2020-12-18')['id'], convention='physical_crude_fixed_volume_v1', knowledgeMode='current')
    frame = next(f for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:'))
    context = {**before, 'snapshotId': frame['id']}
    trade = booked['tradeId']
    terms = c.call('crude/editor', dict(context=before, tradeId=trade))['terms']
    sale = c.call('crude/book', {**terms, 'operationId': str(uuid4()), 'direction': 'deliver',
        'considerationDirection': 'receive', 'quantity': '600', 'fixedPrice': '80',
        'handlingFee': None, 'handlingFeeDirection': None})

    companion = c.call('crude/book', {**terms, 'operationId': str(uuid4())})

    def read(ctx=context, selected=trade):
        return c.call('crude/operations/read', dict(context=ctx, tradeId=selected))

    def command(key, at='2021-01-05T12:00:00Z', **extra):
        return dict(context=context, tradeId=trade, expectedRevision=1, expectedVersion=read()['version'],
            operationId=str(uuid4()), businessKey=key, profile='physical_crude_receipt_cash_v1',
            effectiveAt=at, evidence='Synthetic independently accepted custody and cash evidence', **extra)

    def record(kind, key, **extra):
        return c.call('crude/operations/'+kind, command(key, **extra))

    empty = read()
    assert empty['version'] == 0
    assert empty['receipts'] == [] and empty['invoices'] == [] and empty['payments'] == []
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': empty['context']['contextId']}
    owned = command('receipt-owned', quantity='390', titleTransferred=True, disposition='owned_inventory', saleTradeId=None, saleRevision=None)
    for change in [dict(profile=None), dict(titleTransferred=False), dict(disposition=None), dict(quantity='0'),
                   dict(quantity='1001'), dict(effectiveAt='2021-02-01T00:00:00Z'), dict(saleTradeId=sale['tradeId'],saleRevision=1)]:
        c.call('crude/operations/receipt', {**owned, **change}, expected=422)
    first = c.call('crude/operations/receipt', owned)
    assert c.call('crude/operations/receipt', owned) == first
    assert c.call('crude/operations/status', dict(operationId=owned['operationId'])) == first
    c.call('crude/operations/receipt', {**owned, 'quantity': '391'}, expected=409)
    same = c.call('crude/operations/receipt', {**owned, 'operationId': str(uuid4()), 'expectedVersion': read()['version']})
    assert same['unchanged'] and same['eventId'] == first['eventId']
    equivalent=c.call('crude/operations/receipt',{**owned,'operationId':str(uuid4()),'expectedVersion':read()['version'],'quantity':'390.000','effectiveAt':'2021-01-05T12:00:00.000000Z'})
    assert equivalent['unchanged'] and equivalent['eventId']==first['eventId'], 'Equivalent numeric/instant facts must not create another revision'
    after_first = read()
    assert after_first['balances']['acceptedQuantity']=='390' and after_first['balances']['remainingQuantity']=='610'
    assert after_first['balances']['ownedInventoryQuantity']=='390'
    assert after_first['balances']['cash'][0]['uninvoiced']=='-70000'
    first_context = {**context, 'knowledgeMode':'retained', 'contextId':after_first['context']['contextId']}
    corrected = c.call('crude/operations/receipt', {**owned, 'operationId': str(uuid4()), 'expectedVersion': after_first['version'], 'quantity': '400'})
    assert corrected['eventId'] != first['eventId']
    assert c.call('crude/operations/receipt', owned) == first, 'Command replay must return its original revision after correction'
    assert read(retained)['receipts'] == []
    assert [r['quantity'] for r in read(first_context)['receipts']] == ['390']
    onward_body = command('receipt-onward', quantity='600', titleTransferred=True, disposition='onward_delivery', saleTradeId=sale['tradeId'], saleRevision=1)
    onward = c.call('crude/operations/receipt', onward_body)
    c.call('crude/operations/receipt',{**onward_body,'operationId':str(uuid4()),'tradeId':companion['tradeId'],'expectedVersion':0,'businessKey':'cross-purchase-overallocation','quantity':'1'},expected=422)
    assert read()['balances']['ownedQuantity']=='1000' and read()['balances']['reservedQuantity']=='600'
    c.call('crude/operations/receipt', command('excess-receipt',quantity='1',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None),expected=422)
    c.call('crude/operations/dispatch',command('inventory-is-not-dispatch',receiptId=corrected['eventId'],quantity='1',titleTransferred=True),expected=422)
    c.call('crude/operations/dispatch',command('excess-dispatch',receiptId=onward['eventId'],quantity='601',titleTransferred=True),expected=422)
    dispatched = record('dispatch','dispatch-part',receiptId=onward['eventId'],quantity='250',titleTransferred=True)
    record('dispatch','dispatch-remainder',receiptId=onward['eventId'],quantity='350',titleTransferred=True)
    c.call('crude/operations/receipt',{**onward_body,'operationId':str(uuid4()),'expectedVersion':read()['version'],'quantity':'599'},expected=422)

    def invoice(receipt, component, expected_amount, key):
        body=command(key,receiptId=receipt,component=component,decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
        preview=c.call('crude/operations/invoice/preview',body)
        assert Decimal(preview['amount']) == Decimal(expected_amount), preview
        assert preview['direction'] == ('pay' if component=='consideration' else 'receive')
        assert preview['dueAt'].startswith('2021-02-01T00:00:00'), preview
        assert c.call('crude/operations/invoice/issue',{**body,'previewHash':'0'*64},expected=409)['code']=='crude_invoice_preview'
        issued={**body,'previewHash':preview['previewHash']}
        result=c.call('crude/operations/invoice/issue',issued)
        assert c.call('crude/operations/invoice/issue',issued)==result
        repeated=c.call('crude/operations/invoice/issue',{**issued,'operationId':str(uuid4())})
        assert repeated['unchanged'] and repeated['eventId']==result['eventId']
        return result

    inv_owned=invoice(corrected['eventId'],'consideration','28000','invoice-owned')
    inv_onward=invoice(onward['eventId'],'consideration','42000','invoice-onward')
    fee_owned=invoice(corrected['eventId'],'handling_fee','40','fee-owned')
    fee_onward=invoice(onward['eventId'],'handling_fee','60','fee-onward')
    c.call('crude/operations/payment',command('wrong-direction',invoiceId=inv_owned['eventId'],amount='1',direction='receive'),expected=422)
    c.call('crude/operations/payment',command('overpay',invoiceId=inv_owned['eventId'],amount='28000.01',direction='pay'),expected=422)
    c.call('crude/operations/payment',command('precision',invoiceId=inv_owned['eventId'],amount='0.001',direction='pay'),expected=422)
    part=command('payment-owned-part',invoiceId=inv_owned['eventId'],amount='10000',direction='pay')
    paid=c.call('crude/operations/payment',part)
    assert c.call('crude/operations/payment',part)==paid
    partial=next(i for i in read()['balances']['invoices'] if i['invoiceId']==inv_owned['eventId'])
    assert partial['state']=='PartPaid' and partial['open']=='-18000'
    record('payment','payment-owned-rest',invoiceId=inv_owned['eventId'],amount='18000',direction='pay')
    record('payment','payment-onward',invoiceId=inv_onward['eventId'],amount='42000',direction='pay')
    record('payment','payment-fee-owned',invoiceId=fee_owned['eventId'],amount='40',direction='receive')
    record('payment','payment-fee-onward',invoiceId=fee_onward['eventId'],amount='60',direction='receive')
    final=read()
    assert final['balances']['remainingQuantity']=='0' and final['balances']['ownedQuantity']=='400' and final['balances']['reservedQuantity']=='0'
    assert all(i['state']=='Paid' and i['open']=='0' for i in final['balances']['invoices'])
    assert all(i['outstanding']=='0' and i['uninvoiced']=='0' for i in final['balances']['cash'])
    assert len(final['invoices'])==4 and len(final['payments'])==5 and len(final['dispatches'])==2
    assert sum(Decimal(p['amount'])*p['direction'] for p in final['payments'])==Decimal('-69900')
    assert sum(Decimal(d['quantity']) for d in final['dispatches'])==600
    assert c.call('crude/editor',dict(context=before,tradeId=trade))['valuation']['presentValue']=='10100'
    c.call('crude/editor',dict(context=context,tradeId=trade),expected=422)
    assert read(retained)['version']==0, 'Old knowledge must not see later facts at the same business instant'
    c.call('crude/operations/receipt',{**owned,'context':retained,'operationId':str(uuid4())},expected=422)
    c.call('profile',{'profile':'risk'})
    assert read()['receipts']
    c.call('crude/operations/payment',part,expected=403)
    c.call('profile',{'profile':'trader'})
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    grants=query(f"SELECT string_agg(quote_literal(id::text),',') FROM security.role_grants WHERE principal_id='{principal}' AND NOT revoked;")
    try:
        query(f"UPDATE security.role_grants SET revoked=true WHERE id::text IN({grants});")
        c.call('crude/operations/read',dict(context=context,tradeId=trade),expected=403)
        c.call('crude/operations/status',dict(operationId=part['operationId']),expected=403)
    finally:
        query(f"UPDATE security.role_grants SET revoked=false WHERE id::text IN({grants});")
    runtime_query(query,manifest,f"""DO $test$ BEGIN
      PERFORM security.read_crude_lifecycle('{manifest['catalogId']}','{trade}');
      RAISE EXCEPTION 'unbound_crude_read_succeeded';
      EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'knowledge_coverage' THEN RAISE;END IF;
    END $test$;""",commit=False)
    query("CREATE POLICY lifecycle_hidden_grade ON ref_data.crude_grades AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);")
    try:
        c.call('crude/operations/read',dict(context=context,tradeId=trade),expected=422)
    finally:
        query("DROP POLICY lifecycle_hidden_grade ON ref_data.crude_grades;")
    # Prove the native boundary accepts the otherwise identical idempotent fact,
    # then reject payload defects without relying on an incidental quantity conflict.
    valid={**owned,'operationId':str(uuid4()),'expectedVersion':final['version'],'quantity':'400'}
    def native_statement(payload):
        encoded=json.dumps(payload).replace("'","''")
        return f"security.record_crude_lifecycle('{manifest['catalogId']}','receipt','{encoded}'::jsonb,'2021-01-15T16:00:00Z','{final['context']['contextId']}')"
    runtime_query(query,manifest,f"DO $test$ BEGIN IF NOT ({native_statement(valid)}->>'unchanged')::boolean THEN RAISE EXCEPTION 'native_noop_failed';END IF;END $test$;",commit=False)
    for changed in [{**valid,'context':{**valid['context'],'snapshotId':'x'*20000}},
                    {**valid,'context':{**valid['context'],'unrecognizedPolicy':True}}]:
        runtime_query(query,manifest,f"""DO $test$ BEGIN
          PERFORM {native_statement(changed)};
          RAISE EXCEPTION 'unsupported_native_payload_succeeded';
          EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'crude_lifecycle_input' THEN RAISE;END IF;
        END $test$;""",commit=False)
    print('PASS crude lifecycle: partial receipts, both dispositions, title dispatch, cash components, partial payments, retained history and current authority',flush=True)
