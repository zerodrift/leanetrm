"""Owned currency-cash checks: actual payment, revision replay, history and native expiry fences."""
import json
import re
import threading
import time
from datetime import datetime
from decimal import Decimal
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient

POLICY='explicit_paid_cash_nominal_due_v1'
GREEKS={'central_first_difference_v1':'Delta','central_diagonal_second_difference_v1':'Gamma','central_mixed_second_difference_v1':'Cross gamma'}


def value(money):
    """An exact report amount; the report states no display text of its own."""
    assert set(money)=={'amount','currency'},money
    return Fraction(money['amount'])


def effect_matches(effect,terms):
    """A Greek effect over these estimates: no amount when none is available, otherwise the sum of the available ones."""
    missing=[k for k in terms if k['amount'] is None]
    assert effect['unavailableEstimates']==len(missing) and all(k['unavailableReason'] for k in missing),effect
    if terms and len(missing)==len(terms):assert effect['amount'] is None,effect
    else:assert value(effect['amount'])==sum((value(k['amount']) for k in terms if k['amount'] is not None),Fraction(0)),effect
    return len(missing)


def check_option_strip(report,opening,closing,months):
    """One call on each of a few underlying futures months of two curves, held across both closes."""
    held=len(months);factors=report['factors'];assert len(factors)==held
    # Each factor is named by its futures curve and delivery month, and moved between two exact prices. Factors read one curve
    # after the other, here by the curves' tickers, which sort as their codes do, and each curve by delivery month.
    labels=[f['label'] for f in factors];named=[label.rsplit(' · ',2) for label in labels]
    assert sorted(month for _,_,month in named)==sorted(months) and len({name for name,_,_ in named})==2,labels
    assert named==sorted(named,key=lambda factor:(factor[1],datetime.strptime(factor[2],'%b %Y'))),labels
    for f in factors:
        assert f['unavailableReason'] is None and Fraction(f['change']['price'])==Fraction(f['closing']['price'])-Fraction(f['opening']['price']) and Fraction(f['priceTick'])>0,f
    # No holding prices two of these futures together, so no cross-factor repricing is requested.
    assert report['sensitivities'] and not any(s['mixed'] for s in report['sensitivities'])
    assert {p['state'] for p in report['positions']}=={'continuing'} and len(report['positions'])==2*held
    # Holdings read by trade reference, then by instrument reference.
    references=[(p['tradeReference'],p['instrumentReference']) for p in report['positions']];assert references==sorted(references),references
    # Each option lists estimates for its own underlying alone; premium cash prices no factor.
    assert sorted(len({k['axes'][0] for k in p['contributions']}) for p in report['positions'])==[0]*held+[1]*held
    estimates=[k for p in report['positions'] for k in p['contributions']];assert all(k['label']==GREEKS[k['formula']] for k in estimates)
    total,=report['totals'];steps={s['label']:s for s in total['bridge']}
    assert [(s['label'],s['kind']) for s in total['bridge']]==[('Delta','greek'),('Gamma','greek'),('Cross gamma','greek'),('Residual','residual'),('Cash','cash'),
                                                              ('Prior cash corrections','adjustment'),('Transfers','adjustment')]
    # An effect with no available estimate has no amount; it is never shown as zero and moves nothing in the bridge.
    unavailable=0
    for formula,label in GREEKS.items():
        step=steps[label];unavailable+=effect_matches(step,[k for k in estimates if k['formula']==formula])
        if step['amount'] is None:assert step['start']==step['end'],step
    assert unavailable,'The option strip no longer exercises an unavailable Greek estimate.'
    assert total['values']['unavailableEstimates']==unavailable and total['greekCoverage']=='partial',total['values']
    assert value(total['bridge'][-1]['end'])==value(total['values']['economicPnl'])
    # What Greeks explain is the sum of the available effects. A holding states its own: an amount, or none when it has
    # estimates and none of them is available.
    assert value(total['values']['explained'])==sum(value(steps[label]['amount']) for label in GREEKS.values() if steps[label]['amount'] is not None)
    for p in report['positions']:
        assert (p['values']['explained'] is None)==(bool(p['contributions']) and all(k['amount'] is None for k in p['contributions'])),p['values']
    # Every factor states what it explains by itself, and the factors' deltas add up to the bridge's.
    effects={e['axis']:e for e in total['factorEffects']};assert list(effects)==[f['axis'] for f in factors]
    for axis,effect in effects.items():
        for name,formula in (('delta','central_first_difference_v1'),('gamma','central_diagonal_second_difference_v1')):
            effect_matches(effect[name],[k for k in estimates if k['formula']==formula and k['axes']==[axis]])
    assert sum(value(e['delta']['amount']) for e in effects.values())==value(steps['Delta']['amount'])
    # Each endpoint states its rate-curve definition once; each payment names that curve and its endpoint snapshot.
    for side,at in (('opening',opening),('closing',closing)):
        evidence=report['discounts'][side];curve,=evidence['curves']
        assert len(evidence['payments'])==held and curve['definition']['tenors'],evidence['curves']
        assert all(p['input']['snapshotId']==at['id'] and p['input']['mappingId']==curve['mappingId'] and p['input']['pillars'] and
                   'snapshot' not in p['input'] and 'definition' not in p['input'] for p in evidence['payments']),evidence['payments'][:1]


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
    ny_clock,=[clock for clock in policies['clocks'] if clock['ianaZone']=='America/New_York']
    earlier=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-13T16'))
    cutoff=c.call('options/catalog',context)['context']['knownAt']
    endpoint=lambda f:dict(snapshotId=f['id'],convention='trading_workspace_v1',knowledgeMode='at')
    request=dict(opening=endpoint(earlier),closing=endpoint(frame),knownAt=cutoff,bookIds=[book],portfolioIds=[],tradeIds=[booked['tradeId']],
                 accountingPolicy=policies['accounting'],attributionPolicy=policies['attribution'],factorPolicy=policies['factors'],time=dict(mode='one_calendar_day_model_decay_v1',policy=policies['clockPolicy'],clock=ny_clock))
    run=c.call('portfolio/pnl/run',request,timeout=300);assert run['saveRefusal'] is None
    assert len(json.dumps(dict(requestId=str(uuid4()),name='Close reconciliation',artifact=run['artifact'])).encode())<32768
    report=run['report'];assert report['version']=='trading_pnl_report_v2' and report['positions']
    assert report['engineVersion'] and report['valuationPolicy']
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
    # The saved list describes each report: when it was saved, the closes it compares and its scope.
    listed=c.call('portfolio/pnl/saved');entry,=[r for r in listed['reports'] if r['id']==report['id']]
    assert not listed['more'] and entry['name']=='Close reconciliation' and entry['savedAt'] and entry['openingAt']==earlier['at'] and entry['closingAt']==frame['at'],entry
    assert entry['books']==[catalog['books'][0]['name']] and entry['tradeSelection']=='Selected trades',entry
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

    # One report explains holdings priced on two curves: the centre call of the first product's first two listed underlying
    # months and of the other product's first, booked at the opening close. A run values each held option nine times, at both
    # closes, under six price samples and under one clock sample, all inside one request: the strip is kept to what the
    # request limit admits on a slow host.
    c.call('profile',{'profile':'trader'})
    calls={}
    for o in early_catalog['options']:
        if o['right']=='call':calls.setdefault(o['product'],{}).setdefault(o['underlyingSeries'],[]).append(o)
    strip,months=[],[]
    for position,product in enumerate(sorted(calls)):
        for underlying in sorted(calls[product])[:2 if position==0 else 1]:
            listed=calls[product][underlying];centre=sorted(listed,key=lambda o:Decimal(o['strike']))[len(listed)//2]
            months.append(datetime.strptime(underlying.rsplit('_',1)[1],'%Y%m%d').strftime('%b %Y'))
            strip.append(c.call('options/book',dict(operationId=str(uuid4()),**early_context,bookId=book,series=centre['series'],definitionRevisionId=centre['definitionRevisionId'],
                                                   side='buy',lots='1',premium='1',premiumPaymentDate=earlier['at'][:10],premiumStyle='premium_paid_up_front',**early_parties)))
    assert len(strip)==3,len(strip)
    cutoff=c.call('options/catalog',context)['context']['knownAt']
    c.call('profile',{'profile':'risk'})
    wide=c.call('portfolio/pnl/run',{**request,'knownAt':cutoff,'tradeIds':[t['tradeId'] for t in strip]},timeout=300)
    strip_report=wide['report'];check_option_strip(strip_report,earlier,frame,months)
    assert wide['saveRefusal'] is None
    kept=c.call('portfolio/pnl/save',dict(requestId=str(uuid4()),name='Option strip',artifact=wide['artifact']));assert kept['report']==strip_report
    assert c.call('portfolio/pnl/saved/'+strip_report['id'])['report']==strip_report
    print('PASS multi-curve option P&L: three underlyings on two curves in one saved report, no cross-factor work without a joint dependent, and no amount for an effect without an available estimate.',flush=True)
    reference=manifest['referenceCatalogId']
    query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}' AND tenant_id='{manifest['tenantId']}' AND workspace_id='{manifest['workspaceId']}';")
    try:
        c.call('portfolio/pnl/saved/'+report['id'],expected=403)
        c.call('portfolio/pnl/save',save,expected=403)
        assert not c.call('portfolio/pnl/saved')['reports']
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{manifest['tenantId']}','{manifest['workspaceId']}','owner');")
    assert c.call('portfolio/pnl/saved/'+report['id'])['report']==report
    print('PASS saved P&L source revocation: reads, retries and library visibility recheck current exact reference access.',flush=True)

    # Futures on two delivery months of each listed family, held at the opening close, are more factors than one report
    # explains: a run over them is refused before pricing, with its count.
    c.call('profile',{'profile':'trader'})
    futures_context={**early_context,'convention':manifest['futuresConvention']}
    families={}
    for contract in sorted(c.call('futures/catalog',futures_context)['contracts'],key=lambda listed:listed['deliveryFrom']):families.setdefault(contract['curve'],[]).append(contract)
    chosen=[contract for curve in sorted(families) for contract in families[curve][:2]];assert len(chosen)>16,len(chosen)
    futures=[c.call('futures/book',dict(operationId=str(uuid4()),**futures_context,bookId=book,curve=contract['curve'],contractId=contract['id'],
                                        definitionRevisionId=contract['definitionRevisionId'],lots='1',executionPrice=str(Decimal(contract['mark'])-Decimal(contract['tick'])),**early_parties))
             for contract in chosen]
    cutoff=c.call('options/catalog',context)['context']['knownAt']
    c.call('profile',{'profile':'risk'})
    refused=c.call('portfolio/pnl/run',{**request,'knownAt':cutoff,'tradeIds':[future['tradeId'] for future in futures]},expected=422)
    counted=re.fullmatch(r'This scope has (\d+) price factors\. The opening-Greeks explanation covers at most 16; choose a smaller scope\.',refused['message'])
    assert refused['code']=='pnl_factor_limit' and counted and int(counted.group(1))>16,refused
    print('PASS P&L factor limit: a scope priced on more factors than one report explains is refused before pricing, with its count.',flush=True)
