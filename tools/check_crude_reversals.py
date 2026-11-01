"""Independent immutable cash-record correction checks on the owned crude lifecycle."""
import json
from uuid import uuid4
from check_booked_side_programs import runtime_query
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest, booked):
    assert query("SELECT bool_and(scale(trading.crude_round_cash(x,2))<=2 AND trading.crude_round_cash(x,2)::ref_data.exact_number=y) FROM(VALUES(0.005::numeric,0::numeric),(-0.005,0),(0.015,0.02),(-0.015,-0.02)) AS cases(x,y);")=='t', 'Rounded cash must itself fit exact storage, without caller-side scale normalization'
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:'))
    context=dict(snapshotId=frame['id'],convention='physical_crude_fixed_volume_v1',knowledgeMode='current')
    trade=booked['tradeId']
    def read(ctx=context):return c.call('crude/operations/read',dict(context=ctx,tradeId=trade))
    def command(key,**fields):return dict(context=context,tradeId=trade,expectedRevision=1,expectedVersion=read()['version'],
        operationId=str(uuid4()),businessKey=key,profile='physical_crude_receipt_cash_v1',effectiveAt='2021-01-06T12:00:00Z',
        evidence='Synthetic correction of recorded cash evidence',**fields)
    def reversal(kind,key,target):
        body=command(key,reversalPolicy='crude_record_correction_v1',**{('paymentId' if kind=='payment/reverse' else 'invoiceId'):target})
        return body,c.call('crude/operations/'+kind,body)
    def invoice(receipt,key,amount):
        body=command(key,receiptId=receipt,component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
        preview=c.call('crude/operations/invoice/preview',body)
        assert preview['amount']==amount,preview
        result=c.call('crude/operations/invoice/issue',{**body,'previewHash':preview['previewHash']})
        return result
    original=read()
    retained={**context,'knowledgeMode':'retained','contextId':original['context']['contextId']}
    payment=next(p for p in original['payments'] if p['business_key']=='payment-owned-part')
    owned=next(i for i in original['invoices'] if i['business_key']=='invoice-owned')
    onward=next(i for i in original['invoices'] if i['business_key']=='invoice-onward')
    part=command('reverse-owned-part',paymentId=payment['id'],reversalPolicy='crude_record_correction_v1')
    for change in [dict(reversalPolicy=None),dict(paymentId=str(uuid4())),dict(amount='10000'),dict(effectiveAt='2021-01-04T12:00:00Z')]:
        c.call('crude/operations/payment/reverse',{**part,**change},expected=422)
    result=c.call('crude/operations/payment/reverse',part)
    assert c.call('crude/operations/payment/reverse',part)==result
    assert c.call('crude/operations/status',dict(operationId=part['operationId']))==result
    same=c.call('crude/operations/payment/reverse',{**part,'operationId':str(uuid4())})
    assert same['unchanged'] and same['eventId']==result['eventId']
    c.call('crude/operations/payment/reverse',command('duplicate-target',paymentId=payment['id'],reversalPolicy='crude_record_correction_v1'),expected=422)
    state=read();after_reversal={**context,'knowledgeMode':'retained','contextId':state['context']['contextId']}
    balance=next(i for i in state['balances']['invoices'] if i['invoiceId']==payment['invoice_id'])
    assert balance['paid']=='-18000' and balance['open']=='-10000',balance
    assert all(i['open']=='0' for i in read(retained)['balances']['invoices'])
    assert len(state['payments'])==5, 'Reversal must not erase the original payment record'
    replacement=c.call('crude/operations/payment',command('payment-owned-part',invoiceId=owned['id'],amount='9000',direction='pay'))
    assert replacement['eventId']!=payment['id']
    row=next(p for p in read()['payments'] if p['id']==replacement['eventId'])
    assert row['corrects_id']==payment['id'],row
    assert next(i for i in read()['balances']['invoices'] if i['invoiceId']==owned['id'])['open']=='-1000'
    reversal('payment/reverse','reverse-replacement',replacement['eventId'])
    c.call('crude/operations/invoice/void',command('cannot-void-earlier',invoiceId=owned['id'],reversalPolicy='crude_record_correction_v1'),expected=422)
    c.call('crude/operations/invoice/void',command('cannot-void-paid',invoiceId=onward['id'],reversalPolicy='crude_record_correction_v1'),expected=422)
    onward_payment=next(p for p in original['payments'] if p['business_key']=='payment-onward')
    reversal('payment/reverse','reverse-onward',onward_payment['id'])
    void_body,voided=reversal('invoice/void','void-onward',onward['id'])
    assert c.call('crude/operations/invoice/void',void_body)==voided
    c.call('crude/operations/payment',command('payment-voided',invoiceId=onward['id'],amount='1',direction='pay'),expected=422)
    remainder=next(p for p in original['payments'] if p['business_key']=='payment-owned-rest')
    reversal('payment/reverse','reverse-owned-rest',remainder['id'])
    reversal('invoice/void','void-owned',owned['id'])
    reissued=invoice(owned['receipt_id'],'invoice-owned','28000')
    new=next(i for i in read()['invoices'] if i['id']==reissued['eventId'])
    assert new['ordinal']==3 and new['previous_id'] is None and new['corrects_id']==owned['id'],new
    assert next(i for i in read(after_reversal)['balances']['invoices'] if i['invoiceId']==owned['id'])['open']=='-10000'
    # Both cash components must be inactive before correcting the accepted receipt.
    reversal('invoice/void','void-reissued',reissued['eventId'])
    for name in ['onward','owned']:
        fee=next(i for i in original['invoices'] if i['business_key']=='fee-'+name)
        paid=next(p for p in original['payments'] if p['invoice_id']==fee['id'])
        reversal('payment/reverse','reverse-fee-'+name,paid['id'])
        reversal('invoice/void','void-fee-'+name,fee['id'])
    correction=c.call('crude/operations/receipt',command('receipt-owned',quantity='399',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None))
    assert read()['balances']['remainingQuantity']=='1'
    corrected_invoice=invoice(correction['eventId'],'invoice-corrected-receipt','27930')
    final=read()
    assert final['balances']['acceptedQuantity']=='999' and final['balances']['ownedQuantity']=='399' and final['balances']['dispatchedQuantity']=='600'
    assert len(final['invoices'])==6 and len(final['payments'])==6
    active=next(i for i in final['balances']['invoices'] if i['invoiceId']==corrected_invoice['eventId'])
    assert active['amount']=='-27930' and active['paid']=='0' and active['open']=='-27930'
    cash=next(x for x in final['balances']['cash'] if x['component']=='consideration')
    assert (cash['original'],cash['invoiced'],cash['paid'],cash['outstanding'],cash['uninvoiced'])==('-70000','-27930','0','-70000','-42070'),cash
    assert c.call('crude/operations/payment/reverse',part)==result
    assert read(retained)['balances']['acceptedQuantity']=='1000'
    assert all(i['open']=='0' for i in read(retained)['balances']['invoices'])
    c.call('crude/operations/invoice/void',{**void_body,'context':retained,'operationId':str(uuid4())},expected=422)
    c.call('profile',{'profile':'risk'})
    c.call('crude/operations/payment/reverse',part,expected=403)
    c.call('profile',{'profile':'trader'})
    encoded=json.dumps(part).replace("'","''")
    def match_at(selected):
        return (f"SELECT security.use_market_context('{manifest['catalogId']}','{selected}');"
                f"SELECT security.use_trading_context('{manifest['catalogId']}','{frame['at']}');"
                f"SELECT security.crude_lifecycle_business_match('{manifest['catalogId']}','payment_reversal','{encoded}'::jsonb);")
    assert runtime_query(query,manifest,match_at(final['context']['contextId']),commit=False)=='t'
    assert runtime_query(query,manifest,match_at(retained['contextId']),commit=False)=='f', 'A certified context before reversal must not discover its later business fact'
    # Counterfactual qualification: removing only the temporal predicate exposes
    # the later fact. The function change is confined to this rolled-back transaction.
    definition=query("SELECT pg_get_functiondef('security.crude_lifecycle_business_match(uuid,text,jsonb)'::regprocedure);")
    guard=' AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)'
    assert definition.count(guard)==1
    assert runtime_query(query,manifest,match_at(retained['contextId']),prefix=definition.replace(guard,'')+';',commit=False)=='t'
    # An independent half-cent oracle exercises SQL cumulative allocation after
    # void/reissue with a still-active predecessor, not just integer cash.
    before_frame=initial_frame(c.call('workspace'),'2020-12-18')
    before={**context,'snapshotId':before_frame['id']}
    terms=c.call('crude/editor',dict(context=before,tradeId=booked['tradeId']))['terms']
    tiny=c.call('crude/book',{**terms,'operationId':str(uuid4()),'quantity':'1','fixedPrice':'0.01','handlingFee':None,'handlingFeeDirection':None})
    trade=tiny['tradeId']
    receipts=[c.call('crude/operations/receipt',command('half-receipt-'+str(n),quantity='0.5',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None)) for n in range(2)]
    first_half=invoice(receipts[0]['eventId'],'half-invoice-first','0')
    second_half=invoice(receipts[1]['eventId'],'half-invoice-second','0.01')
    reversal('invoice/void','void-half-second',second_half['eventId'])
    half_reissue=invoice(receipts[1]['eventId'],'half-invoice-reissued','0.01')
    small=read()
    row=next(i for i in small['invoices'] if i['id']==half_reissue['eventId'])
    assert row['ordinal']==3 and row['previous_id']==first_half['eventId'] and row['cumulative_quantity']=='1',row
    assert small['balances']['cash'][0]['invoiced']=='-0.01' and small['balances']['cash'][0]['uninvoiced']=='0'
    print('PASS crude half-cent correction: active predecessor retained, tail reissue preserves exact cumulative cash',flush=True)
    print('PASS crude record corrections: immutable payment reversals, invoice tail voids, exact reissue, receipt correction and retained historical balances',flush=True)
