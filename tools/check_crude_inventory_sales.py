"""Independent owned-lot disposal and sales cash oracles for the disposable preview."""
import json
from uuid import uuid4
from check_booked_side_programs import runtime_query
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest, booked):
    c=PreviewClient(url); c.bootstrap(); c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace')
    before=dict(snapshotId=initial_frame(workspace,'2020-12-18')['id'],convention='physical_crude_fixed_volume_v1',knowledgeMode='current')
    context={**before,'snapshotId':next(f['id'] for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:'))}
    terms=c.call('crude/editor',dict(context=before,tradeId=booked['tradeId']))['terms']
    purchase=c.call('crude/book',{**terms,'operationId':str(uuid4())})['tradeId']
    sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'direction':'deliver','considerationDirection':'receive','quantity':'600','fixedPrice':'80','handlingFee':None,'handlingFeeDirection':None})['tradeId']
    def read(trade=purchase,ctx=context):return c.call('crude/operations/read',dict(context=ctx,tradeId=trade))
    def command(key,trade=purchase,**extra):
        return dict(context=context,tradeId=trade,expectedRevision=1,expectedVersion=read(trade)['version'],operationId=str(uuid4()),businessKey=key,
                    profile='physical_crude_sale_cash_v1' if trade==sale else 'physical_crude_receipt_cash_v1',effectiveAt='2021-01-05T12:00:00Z',evidence='Synthetic certified lot disposal and sales cash evidence',**extra)
    receipt=c.call('crude/operations/receipt',command('inventory-receipt',quantity='1000',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None))
    purchase_cash=read()['balances']['cash']
    allocation_body=command('allocate-six-hundred',receiptId=receipt['eventId'],quantity='600',saleTradeId=sale,saleRevision=1)
    allocation_context=read()['context']['contextId']
    encoded=json.dumps(allocation_body).replace("'","''")
    native=f"security.record_crude_lifecycle('{manifest['catalogId']}','allocation','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{allocation_context}')"
    runtime_query(query,manifest,f"DO $test$ BEGIN IF NOT ({native}?'eventId') THEN RAISE EXCEPTION 'allocation_not_recorded';END IF;END $test$;",commit=False)
    allocation=c.call('crude/operations/allocation',allocation_body)
    assert c.call('crude/operations/allocation',allocation_body)==allocation
    assert c.call('crude/operations/status',{'operationId':allocation_body['operationId']})==allocation
    same=c.call('crude/operations/allocation',{**allocation_body,'operationId':str(uuid4()),'quantity':'600.000'})
    assert same['unchanged'] and same['eventId']==allocation['eventId']
    c.call('crude/operations/allocation',{**allocation_body,'quantity':'599'},expected=409)
    for extra in [dict(quantity='601'),dict(quantity='0'),dict(saleTradeId=purchase),dict(saleRevision=2),dict(receiptId=str(uuid4()))]:
        c.call('crude/operations/allocation',{**command('invalid-allocation',receiptId=receipt['eventId'],quantity='1',saleTradeId=sale,saleRevision=1),**extra},expected=422)
    c.call('crude/operations/receipt',command('inventory-receipt',quantity='999',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None),expected=422)
    companion=c.call('crude/book',{**terms,'operationId':str(uuid4())})['tradeId']
    extra_receipt=c.call('crude/operations/receipt',command('other-owned-lot',trade=companion,quantity='1',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None))
    c.call('crude/operations/allocation',command('cross-purchase-capacity',trade=companion,receiptId=extra_receipt['eventId'],quantity='1',saleTradeId=sale,saleRevision=1),expected=422)
    c.call('crude/operations/receipt',command('legacy-reservation-capacity',trade=companion,quantity='1',titleTransferred=True,disposition='onward_delivery',saleTradeId=sale,saleRevision=1),expected=422)
    other_sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'direction':'deliver','considerationDirection':'receive','fixedPrice':'81','handlingFee':None,'handlingFeeDirection':None})['tradeId']
    c.call('crude/operations/allocation',command('owned-lot-capacity',receiptId=receipt['eventId'],quantity='401',saleTradeId=other_sale,saleRevision=1),expected=422)
    for price,direction in [('0','receive'),('-1','receive'),('80','pay')]:
        invalid_sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'direction':'deliver','considerationDirection':direction,'fixedPrice':price,'handlingFee':None,'handlingFeeDirection':None})['tradeId']
        c.call('crude/operations/allocation',command('unsupported-sale-profile',receiptId=receipt['eventId'],quantity='1',saleTradeId=invalid_sale,saleRevision=1),expected=422)

    reserved=read(); retained={**context,'knowledgeMode':'retained','contextId':reserved['context']['contextId']}
    assert reserved['balances']['ownedInventoryQuantity']=='400' and reserved['balances']['reservedQuantity']=='600'
    assert reserved['balances']['ownedQuantity']=='1000'
    dispatch_base=dict(receiptId=receipt['eventId'],allocationId=allocation['eventId'],titleTransferred=True)
    c.call('crude/operations/dispatch',command('dispatch-too-much',quantity='601',**dispatch_base),expected=422)
    dispatched=[]
    for n,quantity in enumerate(['250','350']):
        body=command('dispatch-'+str(n),quantity=quantity,**dispatch_base)
        result=c.call('crude/operations/dispatch',body); dispatched.append(result['eventId'])
        assert c.call('crude/operations/dispatch',body)==result
    physical=read()['balances']
    assert (physical['ownedInventoryQuantity'],physical['ownedQuantity'],physical['reservedQuantity'],physical['dispatchedQuantity'])==('400','400','0','600'),physical
    assert physical['cash']==purchase_cash, 'Sales and dispatch must not discharge purchase cash'
    assert read(ctx=retained)['balances']['ownedQuantity']=='1000'
    sales_before=read(sale); sale_retained={**context,'knowledgeMode':'retained','contextId':sales_before['context']['contextId']}
    invoices=[]
    for n,(dispatch,amount) in enumerate(zip(dispatched,['20000','28000'])):
        body=command('sale-invoice-'+str(n),trade=sale,dispatchId=dispatch,component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
        preview=c.call('crude/operations/invoice/preview',body)
        assert preview['amount']==amount and preview['direction']=='receive',preview
        c.call('crude/operations/invoice/issue',{**body,'previewHash':'0'*64},expected=409)
        issued={**body,'previewHash':preview['previewHash']}
        result=c.call('crude/operations/invoice/issue',issued);invoices.append(result['eventId'])
        assert c.call('crude/operations/invoice/issue',issued)==result
    for change in [dict(amount='20000.01',direction='receive'),dict(amount='1',direction='pay'),dict(amount='0.001',direction='receive')]:
        c.call('crude/operations/payment',command('invalid-sale-cash',trade=sale,invoiceId=invoices[0],**change),expected=422)
    part=command('sale-part-payment',trade=sale,invoiceId=invoices[0],amount='10000',direction='receive')
    payment=c.call('crude/operations/payment',part)
    assert c.call('crude/operations/payment',part)==payment
    assert next(i for i in read(sale)['balances']['invoices'] if i['invoiceId']==invoices[0])['open']=='10000'
    c.call('crude/operations/payment',command('sale-rest-payment',trade=sale,invoiceId=invoices[0],amount='10000',direction='receive'))
    c.call('crude/operations/payment',command('sale-second-payment',trade=sale,invoiceId=invoices[1],amount='28000',direction='receive'))
    final=read(sale)
    cash=next(x for x in final['balances']['cash'] if x['component']=='consideration')
    assert (cash['original'],cash['invoiced'],cash['paid'],cash['outstanding'],cash['uninvoiced'])==('48000','48000','48000','0','0'),cash
    assert all(i['state']=='Paid' and i['open']=='0' for i in final['balances']['invoices'])
    assert read(sale,sale_retained)['invoices']==[]
    assert read()['balances']['cash']==purchase_cash
    c.call('profile',{'profile':'risk'});assert read(sale)['invoices']
    c.call('crude/operations/payment',part,expected=403)
    c.call('profile',{'profile':'trader'})
    print('PASS crude inventory sales: explicit lot reservation, split title dispatch, unchanged purchase cash, sales invoices, partial collection, replay, history and authority',flush=True)
    # Cash corrections on a sale use the same immutable history and component chain.
    paid_context={**context,'knowledgeMode':'retained','contextId':final['context']['contextId']}
    second_payment=next(p for p in final['payments'] if p['business_key']=='sale-second-payment')
    reversal=command('reverse-sale-second',trade=sale,paymentId=second_payment['id'],reversalPolicy='crude_record_correction_v1')
    reversed_cash=c.call('crude/operations/payment/reverse',reversal)
    assert c.call('crude/operations/payment/reverse',reversal)==reversed_cash
    c.call('crude/operations/invoice/void',command('void-sale-second',trade=sale,invoiceId=invoices[1],reversalPolicy='crude_record_correction_v1'))
    reissue=command('sale-reissue-second',trade=sale,dispatchId=dispatched[1],component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
    reviewed=c.call('crude/operations/invoice/preview',reissue)
    assert reviewed['amount']=='28000' and reviewed['priorInvoiceId']==invoices[0]
    replacement=c.call('crude/operations/invoice/issue',{**reissue,'previewHash':reviewed['previewHash']})
    state=read(sale)
    assert next(i for i in state['invoices'] if i['id']==replacement['eventId'])['ordinal']==3
    assert next(i for i in state['balances']['invoices'] if i['invoiceId']==replacement['eventId'])['open']=='28000'
    assert all(i['state']=='Paid' for i in read(sale,paid_context)['balances']['invoices'])
    assert read()['balances']['ownedQuantity']=='400'
    # Separate purchases and sales retain independent signed fee cash. Exact
    # half-cent invoices must sum to the final contractual cent, including reissue.
    small_purchase=c.call('crude/book',{**terms,'operationId':str(uuid4()),'quantity':'1','fixedPrice':'0.01','handlingFee':None,'handlingFeeDirection':None})['tradeId']
    small_sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'quantity':'1','direction':'deliver','considerationDirection':'receive','fixedPrice':'0.01','handlingFee':'0.02','handlingFeeDirection':'pay'})['tradeId']
    def small_command(key,trade,**extra):
        body=command(key,trade=trade,**extra)
        body['profile']='physical_crude_sale_cash_v1' if trade==small_sale else 'physical_crude_receipt_cash_v1'
        return body
    small_receipt=c.call('crude/operations/receipt',small_command('small-lot',small_purchase,quantity='1',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None))
    small_allocation=c.call('crude/operations/allocation',small_command('small-allocation',small_purchase,receiptId=small_receipt['eventId'],quantity='1',saleTradeId=small_sale,saleRevision=1))
    half_dispatches=[]
    for n in range(2):
        half_dispatches.append(c.call('crude/operations/dispatch',small_command('half-dispatch-'+str(n),small_purchase,receiptId=small_receipt['eventId'],allocationId=small_allocation['eventId'],quantity='0.5',titleTransferred=True))['eventId'])
    for component,amounts,direction in [('consideration',['0','0.01'],'receive'),('handling_fee',['0.01','0.01'],'pay')]:
        issued=[]
        for n,(dispatch,amount) in enumerate(zip(half_dispatches,amounts)):
            body=small_command(component+'-half-'+str(n),small_sale,dispatchId=dispatch,component=component,decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
            preview=c.call('crude/operations/invoice/preview',body)
            assert preview['amount']==amount and preview['direction']==direction,preview
            issued.append(c.call('crude/operations/invoice/issue',{**body,'previewHash':preview['previewHash']})['eventId'])
        if component=='consideration':
            c.call('crude/operations/payment',small_command('cannot-pay-zero',small_sale,invoiceId=issued[0],amount='0.01',direction='receive'),expected=422)
            c.call('crude/operations/invoice/void',small_command('cannot-void-first-half',small_sale,invoiceId=issued[0],reversalPolicy='crude_record_correction_v1'),expected=422)
            c.call('crude/operations/invoice/void',small_command('void-last-half',small_sale,invoiceId=issued[1],reversalPolicy='crude_record_correction_v1'))
            body=small_command('reissue-last-half',small_sale,dispatchId=half_dispatches[1],component=component,decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
            preview=c.call('crude/operations/invoice/preview',body)
            assert preview['amount']=='0.01' and preview['priorInvoiceId']==issued[0]
            c.call('crude/operations/invoice/issue',{**body,'previewHash':preview['previewHash']})
        else:
            for n,invoice in enumerate(issued):c.call('crude/operations/payment',small_command('pay-fee-'+str(n),small_sale,invoiceId=invoice,amount='0.01',direction='pay'))
    small=read(small_sale)['balances']
    fee=next(x for x in small['cash'] if x['component']=='handling_fee')
    assert (fee['original'],fee['invoiced'],fee['paid'],fee['outstanding'])==('-0.02','-0.02','-0.02','0'),fee
    assert read(small_purchase)['balances']['ownedQuantity']=='0'
    print('PASS crude sales corrections: immutable payment reversal, tail void/reissue, historical paid state, half-cent cumulative amounts and independently payable fee',flush=True)
    # One sale draws from two independently owned purchase lots. A stale source
    # context has room numerically but must not consume another purchase's new reservation.
    multi_sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'quantity':'1000','direction':'deliver','considerationDirection':'receive','fixedPrice':'80','handlingFee':None,'handlingFeeDirection':None})['tradeId']
    purchases=[c.call('crude/book',{**terms,'operationId':str(uuid4()),'handlingFee':None,'handlingFeeDirection':None})['tradeId'] for _ in range(2)]
    def multi_command(key,trade,**extra):
        body=command(key,trade=trade,**extra)
        body['profile']='physical_crude_sale_cash_v1' if trade==multi_sale else 'physical_crude_receipt_cash_v1'
        return body
    lots=[c.call('crude/operations/receipt',multi_command('multi-lot',p,quantity=q,titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None))['eventId'] for p,q in zip(purchases,['400','600'])]
    stale=read(purchases[1]);stale_context=stale['context']['contextId']
    first=c.call('crude/operations/allocation',multi_command('multi-reserve',purchases[0],receiptId=lots[0],quantity='400',saleTradeId=multi_sale,saleRevision=1))
    stale_body=multi_command('stale-cross-purchase',purchases[1],receiptId=lots[1],quantity='100',saleTradeId=multi_sale,saleRevision=1)
    encoded=json.dumps(stale_body).replace("'","''")
    statement=f"security.record_crude_lifecycle('{manifest['catalogId']}','allocation','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{stale_context}')"
    runtime_query(query,manifest,f"DO $test$ BEGIN PERFORM {statement};RAISE EXCEPTION 'unseen_source_reservation_accepted';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'crude_lifecycle_context' THEN RAISE;END IF;END $test$;",commit=False)
    old_sale=read(multi_sale);old_sale_context={**context,'knowledgeMode':'retained','contextId':old_sale['context']['contextId']}
    second=c.call('crude/operations/allocation',multi_command('multi-reserve',purchases[1],receiptId=lots[1],quantity='600',saleTradeId=multi_sale,saleRevision=1))
    deliveries=[]
    for n,(p,lot,reservation,qty) in enumerate(zip(purchases,lots,[first,second],['400','600'])):
        deliveries.append(c.call('crude/operations/dispatch',multi_command('multi-dispatch',p,receiptId=lot,allocationId=reservation['eventId'],quantity=qty,titleTransferred=True))['eventId'])
    for n,(delivery,amount) in enumerate(zip(deliveries,['32000','48000'])):
        body=multi_command('multi-invoice-'+str(n),multi_sale,dispatchId=delivery,component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
        preview=c.call('crude/operations/invoice/preview',body)
        assert preview['amount']==amount
        c.call('crude/operations/invoice/issue',{**body,'previewHash':preview['previewHash']})
    multi=read(multi_sale)
    assert len(multi['allocations'])==2 and len(multi['dispatches'])==2
    assert next(x for x in multi['balances']['cash'] if x['component']=='consideration')['invoiced']=='80000'
    historic=read(multi_sale,old_sale_context)
    assert len(historic['allocations'])==1 and historic['dispatches']==[] and historic['invoices']==[]
    query(f"CREATE POLICY inventory_source_hidden ON trading.crude_trade_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(trade_id<>'{purchases[0]}'::uuid);")
    try:c.call('crude/operations/read',dict(context=context,tradeId=multi_sale),expected=422)
    finally:query('DROP POLICY inventory_source_hidden ON trading.crude_trade_revisions;')
    # Onward dispatches recorded before inventory allocation support remain valid
    # sales evidence; they use their immutable exact dispatch-to-sale relationship.
    original=read(booked['tradeId'])
    onward=next(r for r in original['receipts'] if r['disposition']=='onward_delivery')
    legacy_sale=onward['sale_trade_id']
    legacy_dispatch=next(d for d in original['dispatches'] if d['receipt_id']==onward['id'])
    body=command('legacy-sale-invoice',trade=legacy_sale,dispatchId=legacy_dispatch['id'],component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
    body['profile']='physical_crude_sale_cash_v1'
    preview=c.call('crude/operations/invoice/preview',body)
    assert preview['amount']==str(int(legacy_dispatch['quantity'])*80)
    c.call('crude/operations/invoice/issue',{**body,'previewHash':preview['previewHash']})
    print('PASS crude multi-lot sales: exact two-purchase dispatch evidence, shared cumulative invoices, stale cross-source refusal, historical completeness, hidden source refusal and legacy onward dispatch',flush=True)
    # A profile/evidence mismatch must be a structured refusal, never a server exception.
    wrong=command('wrong-sale-profile',trade=sale,receiptId=dispatched[0],component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
    wrong['profile']='physical_crude_receipt_cash_v1'
    c.call('crude/operations/invoice/preview',wrong,expected=422)
    wrong=command('wrong-purchase-profile',dispatchId=dispatched[0],component='consideration',decimalPlaces=2,roundingPolicy='crude_cumulative_receipt_cash_v1')
    wrong['profile']='physical_crude_sale_cash_v1'
    c.call('crude/operations/invoice/preview',wrong,expected=422)
    # Competing purchases cannot reserve more than one sale's remaining capacity.
    from concurrent.futures import ThreadPoolExecutor
    from threading import Barrier
    race_sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'quantity':'1000','direction':'deliver','considerationDirection':'receive','fixedPrice':'80','handlingFee':None,'handlingFeeDirection':None})['tradeId']
    race_purchases=[c.call('crude/book',{**terms,'operationId':str(uuid4())})['tradeId'] for _ in range(2)]
    race_receipts=[c.call('crude/operations/receipt',command('race-lot',trade=p,quantity='600',titleTransferred=True,disposition='owned_inventory',saleTradeId=None,saleRevision=None))['eventId'] for p in race_purchases]
    race_commands=[command('race-reserve',trade=p,receiptId=r,quantity='600',saleTradeId=race_sale,saleRevision=1) for p,r in zip(race_purchases,race_receipts)]
    clients=[PreviewClient(url),PreviewClient(url)]
    for n,client in enumerate(clients):
        client.bootstrap();client.call('profile',{'profile':'trader'})
        own=client.call('workspace')
        race_commands[n]['context']={**context,'snapshotId':next(f['id'] for f in own['frames'] if f['at'].startswith('2021-01-15T08:'))}
    start=Barrier(2)
    def compete(index):
        start.wait(timeout=10)
        try:return clients[index].call('crude/operations/allocation',race_commands[index])
        except AssertionError as error:
            message=str(error)
            assert 'got 422:' in message and any(code in message for code in ['crude_onward_capacity','crude_lifecycle_context']),message
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(compete,[0,1]))
    assert sum(result is not None for result in results)==1,results
    assert query(f"SELECT sum(quantity)::text FROM trading.crude_inventory_allocations WHERE sale_trade_id='{race_sale}';")=='600'
    # Rollback-only generated dispatch evidence proves the aggregate sale limit
    # at its exact boundary without 10,000 HTTP operations or retained test rows.
    bound_sale=c.call('crude/book',{**terms,'operationId':str(uuid4()),'quantity':'1000','direction':'deliver','considerationDirection':'receive','fixedPrice':'80','handlingFee':None,'handlingFeeDirection':None})['tradeId']
    bound_purchases=[c.call('crude/book',{**terms,'operationId':str(uuid4())})['tradeId'] for _ in range(3)]
    bound_receipts=[c.call('crude/operations/receipt',command('bound-lot',trade=p,quantity='1',titleTransferred=True,disposition='onward_delivery',saleTradeId=bound_sale,saleRevision=1))['eventId'] for p in bound_purchases]
    bound_body=command('bound-dispatch',trade=bound_purchases[2],receiptId=bound_receipts[2],quantity='0.1',titleTransferred=True)
    bound_context=read(bound_purchases[2])['context']['contextId'];catalog=manifest['catalogId']
    actor=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    encoded=json.dumps(bound_body).replace("'","''")
    native=f"security.record_crude_lifecycle('{catalog}','dispatch','{encoded}'::jsonb,'2021-01-15T08:00:00Z','{bound_context}')"
    explicit_null={**bound_body,'allocationId':None}
    c.call('crude/operations/dispatch',explicit_null,expected=422)
    null_encoded=json.dumps(explicit_null).replace("'","''")
    null_native=f"security.record_crude_lifecycle('{catalog}','dispatch','{null_encoded}'::jsonb,'2021-01-15T08:00:00Z','{bound_context}')"
    runtime_query(query,manifest,f"DO $test$ BEGIN PERFORM {null_native};RAISE EXCEPTION 'null_allocation_id_accepted';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'crude_lifecycle_input' THEN RAISE;END IF;END $test$;",commit=False)
    def bound_prefix(count):return f"""
      CREATE TEMP TABLE generated_dispatches AS SELECT n,md5('synthetic-dispatch-bound-'||n)::uuid id,
       CASE WHEN n<=5000 THEN '{bound_purchases[0]}'::uuid ELSE '{bound_purchases[1]}'::uuid END trade,
       CASE WHEN n<=5000 THEN '{bound_receipts[0]}'::uuid ELSE '{bound_receipts[1]}'::uuid END receipt,
       CASE WHEN n<=5000 THEN n+1 ELSE n-5000+1 END version,
       (SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')+n publication
      FROM generate_series(1,{count}) n;
      INSERT INTO trading.publications SELECT '{catalog}',publication,pg_current_xact_id() FROM generated_dispatches;
      INSERT INTO trading.crude_operational_events(catalog_id,id,trade_id,trade_revision,version,kind,business_key,profile,effective_at,evidence,context_id,actor_id,publication_order)
       SELECT '{catalog}',id,trade,1,version,'dispatch','synthetic-bound-'||n,'physical_crude_receipt_cash_v1','2021-01-05T12:00:00Z','Synthetic exact-boundary dispatch fixture','{bound_context}','{actor}',publication FROM generated_dispatches;
      INSERT INTO trading.crude_dispatches SELECT '{catalog}',id,trade,receipt,0.0001,true,NULL FROM generated_dispatches;
      INSERT INTO trading.crude_dispatch_sales SELECT '{catalog}',id,trade,'{bound_sale}',1 FROM generated_dispatches;
    """
    runtime_query(query,manifest,f"DO $test$ BEGIN IF NOT({native}?'eventId') THEN RAISE EXCEPTION 'last_supported_dispatch_refused';END IF;END $test$;",prefix=bound_prefix(9999),commit=False)
    runtime_query(query,manifest,f"DO $test$ BEGIN PERFORM {native};RAISE EXCEPTION 'aggregate_dispatch_limit_missing';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'crude_sale_delivery_limit' THEN RAISE;END IF;END $test$;",prefix=bound_prefix(10000),commit=False)
    print('PASS crude allocation concurrency: one accepted reservation and no oversell; exact 10,000 sale-dispatch bound preserves readable state',flush=True)
