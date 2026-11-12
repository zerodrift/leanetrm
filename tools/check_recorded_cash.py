"""Owned currency-cash checks: actual payment, revision replay, history and native expiry fences."""
import json
import threading
import time
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient

POLICY='explicit_paid_cash_nominal_due_v1'


def run_checks(url,query,manifest,verify_persistence=None):
    base=PreviewClient(url);base.bootstrap()
    c=PreviewClient(url,'exchange');c.opener=base.opener;c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-16T16'))
    context={'snapshotId':frame['id'],'knowledgeMode':'current','convention':'futures_option_premium_v1'}
    catalog=c.call('options/catalog',context);option=next(o for o in catalog['options'] if o['product']=='nymex_lo' and o['right']=='call')
    book=catalog['books'][0]['id'];cat=manifest['catalogId'];date=frame['at'][:10]
    parties={k:catalog[items][0][name] for k,items,name in [('legalEntityId','legalEntities','id'),('legalEntityRevision','legalEntities','revision'),('counterpartyId','counterparties','id'),('counterpartyRevision','counterparties','revision')]}
    terms={'operationId':str(uuid4()),**context,'bookId':book,'series':option['series'],'definitionRevisionId':option['definitionRevisionId'],
           'side':'buy','lots':'2','premium':'1','premiumPaymentDate':date,'premiumStyle':'premium_paid_up_front',**parties}
    booked=c.call('options/book',terms);owed=-Decimal(option['multiplier'])*2

    def premium(trade=booked,knowledge=None):
        view={'mode':'trades','bookIds':[book],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
        rows=c.call('trading/blotter',{'context':{**context,'convention':'trading_workspace_v1',**(knowledge or {})},'view':view})['holdings']
        return next(r for r in rows if r['tradeId']==trade['tradeId'] and r['bindingId']==trade['premiumInstrumentId'])

    row=premium();assert row['cashState']=='due_unpaid' and Decimal(row['presentValue'])==owed,row
    command={'snapshotId':frame['id'],'knowledgeMode':'current','operationId':str(uuid4()),'allocationId':str(uuid4()),'tradeId':booked['tradeId'],
             'bindingId':booked['premiumInstrumentId'],'expectedRevision':0,'paidAt':frame['at'],'amount':format(owed/4,'f'),'policy':POLICY}
    first=c.call('cash-payments/record',command);assert first['revision']==1
    row=premium();assert row['cashState']=='part_paid' and Decimal(row['presentValue'])==owed*Decimal('.75'),row
    old=c.call('options/catalog',context)['context']['contextId']
    spelling={**command,'operationId':command['operationId'].upper(),'paidAt':frame['at'].replace('+00:00','Z'),'amount':format(owed/4,'.3f')}
    assert c.call('cash-payments/record',spelling)==first
    same=c.call('cash-payments/record',{**spelling,'operationId':str(uuid4())});assert same['allocationId']==first['allocationId'] and same['revision']==1
    changed={**command,'operationId':str(uuid4()),'expectedRevision':1,'amount':format(owed/2,'f')}
    assert c.call('cash-payments/record',changed)['revision']==2
    assert Decimal(premium()['presentValue'])==owed/2
    publications=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{cat}';")
    for expected in (0,2):
        repeated={**command,'operationId':str(uuid4()),'expectedRevision':expected}
        original=c.call('cash-payments/record',repeated)
        assert original['revision']==1 and c.call('cash-payments/record',repeated)==original
        assert Decimal(premium()['presentValue'])==owed/2
    assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{cat}';")==publications
    assert query(f"SELECT max(revision) FROM settlement_data.cash_payment_revisions WHERE catalog_id='{cat}' AND allocation_id='{command['allocationId']}';")=='2'
    stale=c.call('cash-payments/record',{**command,'operationId':str(uuid4()),'amount':format(owed*Decimal('.3'),'f')},expected=409)
    assert stale['code']=='cash_payment_revision'
    conflict=c.call('cash-payments/record',{**command,'amount':format(owed/2,'f')},expected=409)
    assert conflict['code']=='cash_payment_conflict'
    invalid=c.call('cash-payments/record',{**command,'operationId':str(uuid4()),'policy':'unselected'},expected=422)
    assert invalid['code']=='cash_payment_input'
    retained=premium(knowledge={'knowledgeMode':'retained','contextId':old})
    assert retained['cashState']=='part_paid' and Decimal(retained['presentValue'])==owed*Decimal('.75'),retained
    rest={**changed,'operationId':str(uuid4()),'allocationId':str(uuid4()),'expectedRevision':0}
    assert c.call('cash-payments/record',rest)['revision']==1
    row=premium();assert row['cashState']=='paid' and Decimal(row['presentValue'])==0,row
    other=c.call('options/book',{**terms,'operationId':str(uuid4())})
    assert other['premiumInstrumentId']==booked['premiumInstrumentId']
    assert Decimal(premium(other)['presentValue'])==owed and premium(other)['cashState']=='due_unpaid'
    c.call('profile',{'profile':'risk'});c.call('cash-payments/record',{**rest,'operationId':str(uuid4())},expected=403);c.call('profile',{'profile':'trader'})
    assert query(f"SELECT count(*) FROM settlement_data.cash_payment_revisions WHERE catalog_id='{cat}' AND allocation_id='{command['allocationId']}';")=='2'
    print('PASS recorded cash: due is unpaid; partial/full facts, normalized replay, revisions, historical cutoffs and shared-unit isolation.',flush=True)

    def quote(value):return "'"+str(value).replace("'","''")+"'"
    def token(seconds):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://exchange-preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',{seconds});")
    bound=token(30)
    mismatch=str(uuid4())
    guard=query(f"""BEGIN;SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{bound}');
        SELECT security.use_market_context('{cat}','{old}');SELECT security.use_trading_context('{cat}','{frame['at']}');
        SELECT security.require_cash_payment_read('{cat}','{old}','{frame['at']}');
        DO $check$ BEGIN BEGIN PERFORM security.require_cash_payment_read('{cat}','{mismatch}','{frame['at']}');
        RAISE EXCEPTION 'Mismatched cash context was accepted';EXCEPTION WHEN insufficient_privilege THEN NULL;END;
        BEGIN PERFORM security.require_cash_payment_read('{cat}','{old}','{date}T15:00:00Z');
        RAISE EXCEPTION 'Mismatched cash instant was accepted';EXCEPTION WHEN insufficient_privilege THEN NULL;END;
        END $check$;SELECT 'cash_context_checked';ROLLBACK;""")
    assert 'cash_context_checked' in guard
    print('PASS cash history requires the actual bound context and exact valuation instant.',flush=True)
    native_keys=['operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy']
    def expiry_check(body,label):
        owner_name='cash_owner_'+uuid4().hex;waiter_name='cash_waiter_'+uuid4().hex;failures=[];result=[]
        def own():
            try:query(f"BEGIN;SET application_name={quote(owner_name)};SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(4);COMMIT;")
            except BaseException as error:failures.append(error)
        owner=threading.Thread(target=own);owner.start();deadline=time.monotonic()+2
        while query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name={quote(owner_name)} AND l.locktype='advisory' AND l.granted;")!='1':
            assert time.monotonic()<deadline,'Owned publication lock not acquired';time.sleep(.02)
        bound=token(2);payload={k:body[k] for k in native_keys}
        def call():
            try:
                result.append(query(f"""BEGIN;SET application_name={quote(waiter_name)};SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{bound}');
                DO $check$ BEGIN BEGIN PERFORM security.record_cash_payment('{cat}',{quote(json.dumps(payload))}::jsonb,{quote(frame['at'])}::timestamptz);
                RAISE EXCEPTION 'Expired cash authority was accepted';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END $check$;
                SELECT 'expiry_checked';ROLLBACK;"""))
            except BaseException as error:failures.append(error)
        caller=threading.Thread(target=call);caller.start();waiting=False;deadline=time.monotonic()+1.5
        while time.monotonic()<deadline:
            waiting=query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name={quote(waiter_name)} AND l.locktype='advisory' AND NOT l.granted;")=='1'
            if waiting:break
            time.sleep(.02)
        caller.join(8);owner.join(8)
        assert waiting and not caller.is_alive() and not owner.is_alive() and not failures and result and 'expiry_checked' in result[0],(label,waiting,failures,result)
    expiry_check(command,'exact operation replay')
    no_op={**changed,'operationId':str(uuid4()),'expectedRevision':0}
    expiry_check(no_op,'new command for the same recorded fact')
    assert query(f"SELECT count(*) FROM settlement_data.cash_payment_commands WHERE(catalog_id,operation_id)=('{cat}','{no_op['operationId']}');")=='0'
    print('PASS native cash authority expires safely while exact replay and fact no-op wait for the publication lock.',flush=True)
    # A trade opened and paid inside the interval has no opening holdings. Its cash must survive in full P&L.
    c.call('profile',{'profile':'risk'})
    policies=c.call('portfolio/pnl/policies')
    earlier=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-13T16'))
    cutoff=c.call('options/catalog',context)['context']['knownAt']
    endpoint=lambda f:dict(snapshotId=f['id'],convention='trading_workspace_v1',knowledgeMode='at')
    request=dict(opening=endpoint(earlier),closing=endpoint(frame),knownAt=cutoff,bookIds=[book],portfolioIds=[],tradeIds=[booked['tradeId']],
                 accountingPolicy=policies['accounting'],attributionPolicy=policies['attribution'],factorPolicy=policies['factors'],time=dict(mode=policies['timeModes'][0]['id'],policy=policies['clockPolicy'],clock=policies['clocks'][0]))
    run=c.call('portfolio/pnl/run',request,timeout=300)
    assert len(json.dumps(dict(requestId=str(uuid4()),name='Close reconciliation',artifact=run['artifact'])).encode())<32768
    report=run['report'];assert report['version']=='trading_pnl_report_v1' and report['positions']
    assert report['engineVersion'] and report['valuationPolicy']
    from fractions import Fraction
    value=lambda m:Fraction(int(m['numerator']),int(m['denominator']))
    total=report['totals'][0]['values']
    assert value(total['openingValue'])==0 and value(total['cash'])==Fraction(owed),total
    assert value(total['economicPnl'])==value(total['closingValue'])+Fraction(owed),total
    assert {p['state'] for p in report['positions']}=={'new'}
    save=dict(requestId=str(uuid4()),name='Close reconciliation',artifact=run['artifact'])
    saved=c.call('portfolio/pnl/save',save);assert saved['report']==report
    conflict=c.call('portfolio/pnl/save',{**save,'name':'Changed name'},expected=409)
    assert conflict['code']=='pnl_save_conflict'
    assert c.call('portfolio/pnl/save',save)==saved
    no_op=c.call('portfolio/pnl/save',{**save,'requestId':str(uuid4())});assert no_op==saved
    restored=c.call('portfolio/pnl/saved/'+report['id']);assert restored['report']==report and restored['resultHash']==saved['resultHash']
    assert any(r['id']==report['id'] for r in c.call('portfolio/pnl/saved'))
    c.call('portfolio/pnl/save',{**save,'requestId':str(uuid4()),'artifact':run['artifact'][:-8]+'AAAAAAAA'},expected=422)
    c.call('profile',{'profile':'trader'});c.call('portfolio/pnl/saved/'+report['id'],expected=403);c.call('profile',{'profile':'risk'})
    if verify_persistence:verify_persistence(dict(id=report['id'],report=report,command=save))
    print('PASS full P&L: new/paid in-term trade, signed cash, exact displayed save/read, command replay, same-fact no-op, tamper and foreign-owner refusal.',flush=True)

    # A continuing solved option exercises the shared price/clock path at both
    # closes, rather than proving only the cash/new-position branch above.
    c.call('profile',{'profile':'trader'})
    early_context={**context,'snapshotId':earlier['id']}
    early_catalog=c.call('options/catalog',early_context)
    on=next(o for o in early_catalog['options'] if o['series']==manifest['missingSettlement']['series'])
    early_parties={k:early_catalog[items][0][name] for k,items,name in [('legalEntityId','legalEntities','id'),('legalEntityRevision','legalEntities','revision'),('counterpartyId','counterparties','id'),('counterpartyRevision','counterparties','revision')]}
    held=c.call('options/book',dict(operationId=str(uuid4()),**early_context,bookId=book,series=on['series'],definitionRevisionId=on['definitionRevisionId'],
                                  side='buy',lots='1',premium='1',premiumPaymentDate=earlier['at'][:10],premiumStyle='premium_paid_up_front',**early_parties))
    cutoff=c.call('options/catalog',context)['context']['knownAt']
    c.call('profile',{'profile':'risk'})
    request={**request,'knownAt':cutoff,'tradeIds':[held['tradeId']]}
    continuing=c.call('portfolio/pnl/run',request,timeout=300)['report']
    assert {p['state'] for p in continuing['positions']}=={'continuing'}
    assert continuing['factors'] and continuing['time']['status']=='complete',continuing['time']
    assert continuing['time']['totals'] and any(value(t['value'])!=0 for t in continuing['time']['totals'])
    assert any(p['contributions'] for p in continuing['positions'])
    assert continuing['sensitivities'] and all('estimate' in c and 'units' in c for p in continuing['positions'] for c in p['contributions'])
    rolled=c.call('portfolio/pnl/run',{**request,'time':dict(mode='opening_to_closing_clock_v1',policy=policies['clockPolicy'],clock=None)},timeout=300)['report']
    assert rolled['time']['status']=='complete' and rolled['time']['totals'],rolled['time']
    assert all(p['clock']['at']==frame['at'] for p in rolled['time']['partitions'])
    print('PASS continuing option P&L: shared price Greeks, one-calendar-day decay and exact endpoint clock roll with retained opening inputs.',flush=True)
    reference=manifest['referenceCatalogId']
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}' AND tenant_id='{manifest['tenantId']}' AND workspace_id='{manifest['workspaceId']}';")
    try:
        c.call('portfolio/pnl/saved/'+report['id'],expected=403)
        c.call('portfolio/pnl/save',save,expected=403)
        assert not c.call('portfolio/pnl/saved')
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');")
    assert c.call('portfolio/pnl/saved/'+report['id'])['report']==report
    print('PASS saved P&L source revocation: reads, retries and library visibility recheck current exact reference access.',flush=True)
