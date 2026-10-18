"""Trade attribution is revisioned business data, separate from authority."""
from preview_fixture import trade_booking
from uuid import uuid4
from preview_client import PreviewClient

def run_checks(url):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':'fixed_total_cash_execution_pnl_v1','knowledgeMode':'current'}
    catalog=c.call('futures/catalog',context);trader=catalog['bookingActor'];contract=catalog['contracts'][0]
    body={**context,'operationId':str(uuid4()),**trade_booking(), 'bookId':catalog['books'][0]['id'],'curve':contract['curve'],'contractId':contract['id'],'lots':'2','executionPrice':'3','memo':'Synthetic memo <text> =1+1'}
    result=c.call('futures/book',body);assert result==c.call('futures/book',body)
    detail=c.call('futures/editor',{'context':context,'tradeId':result['tradeId'],'revision':None})
    assert detail['state']['traderId']==trader['id'] and detail['state']['memo']==body['memo']
    amendment={**body,'operationId':str(uuid4()),'tradeId':result['tradeId'],'expectedRevision':1,'effectiveAt':catalog['asOf'],'reason':'Synthetic memo correction','amendmentConvention':'unprocessed_futures_booking_amendment_v1','memo':'Revised memo'}
    c.call('futures/amend-preview',amendment);changed=c.call('futures/amend',amendment)
    assert changed['revision']==2 and changed==c.call('futures/amend',amendment)
    old=c.call('futures/editor',{'context':context,'tradeId':result['tradeId'],'revision':1})
    assert old['state']['memo']==body['memo'] and not old['canAmend']
    c.call('futures/book',{**body,'operationId':str(uuid4()),'traderId':str(uuid4())},expected=422)
    c.call('futures/book',{**body,'operationId':str(uuid4()),'memo':'x'*4001},expected=422)
    c.call('profile',{'profile':'viewer'})
    c.call('futures/book',{**body,'operationId':str(uuid4())},expected=403)
    print('PASS trader and memo persist, amend without economic changes, retain history and never grant authority',flush=True)
