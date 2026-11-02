"""Independent HTTP checks for the bounded durable PPA consumer."""
from preview_client import PreviewClient


def run_checks(url, query, manifest, benchmark=False, api_pid=None, dotnet=None):
    client = PreviewClient(url)
    client.bootstrap()
    from check_market_navigation import run_checks as navigation_checks
    def navigation_check(name, condition=True):
        assert condition, name
        print('PASS enlarged PPA navigation: '+name, flush=True)
    navigation_checks(client, navigation_check, query, manifest)
    client.call('profile', {'profile': 'trader'})
    frame = next(f for f in client.call('workspace')['frames'] if not f.get('ppaBundle'))['id']
    client.bootstrap()
    catalog = client.call('ppa/catalog', {'snapshotId': frame})
    assert catalog['implementation'] == 'ppa_capabilities_v1'
    assert catalog['limits']['requestRows'] > 0
    assert catalog['limits']['slotOrdinalMax'] == 7
    assert {'nonlinear_contract_clauses', 'resampling', 'cross_revision_valuation',
            'partial_month_capacity_pricing', 'payment_grouping', 'accepted_settlement',
            'payment_recording', 'historical_session_valuation'} <= set(catalog.get('unsupportedFeatures', [])), \
        'The capability catalog must disclose unsupported operations before a caller constructs a request'
    print('PASS PPA bookings http: explicit capabilities and bounded request admission', flush=True)
    durable_checks(url,query,manifest,client,frame)
    fixture=valuation_checks(url,query,manifest,frame)
    composed_checks(fixture,query,manifest)
    from check_ppa_revisions_http import run_checks as revision_checks
    revision_checks(url,query,manifest,fixture)
    if benchmark:
        from benchmark_ppa_http import run_benchmark
        run_benchmark(fixture,query,api_pid,dotnet)


from ppa_demo_terms import profile_terms


def durable_checks(url,query,manifest,client,frame):
    from copy import deepcopy
    from fractions import Fraction
    from uuid import uuid4
    from exact_values import fraction
    from preview_fixture import trade_booking
    passed=0
    def check(name, condition):
        nonlocal passed
        assert condition,name
        passed+=1;print('PASS PPA bookings http: '+name,flush=True)
    def cutoff():return client.call('interval-series')['knownAt']
    start,end='2031-11-02T04:00:00Z','2031-11-03T05:00:00Z'
    terms=profile_terms(manifest)
    # Original independent gross arithmetic: Sunday fall-back has25off-peak hours.
    # Selected fixture offpeak mark40 plus explicit location adder1.25, strike40.
    editor=PreviewClient(url);editor.bootstrap();editor.call('profile',{'profile':'editor'});editor.bootstrap()
    editor.call('interval-series/revise',{'requestId':str(uuid4()),'seriesId':manifest['intervalCurves']['shapeSeries']['additive'],'expectedRevision':0,'reason':'Synthetic PPA zero shape','segments':[{'start':'2031-11-01T04:00:00Z','values':['0']*721}]})
    def validate(t=terms,**changes):
        body={'snapshotId':frame,'terms':t,'quantity':'80','coverageFrom':start,'coverageTo':end};body.update(changes)
        return client.call('ppa/validate',body)
    valid=validate()
    check('compiler returns exact economic identity without valuation inputs',len(valid['identity'])==64 and valid['capability']=='fixed_physical')
    unsupported_ordinal=deepcopy(terms)
    unsupported_ordinal['memberships'][0]['ordinal']=8
    unsupported_ordinal['memberships'][1]['side']['quantity']['slots'][0]['ordinal']=8
    invalid_ordinal=client.call('ppa/validate',{'snapshotId':frame,'terms':unsupported_ordinal,'quantity':'80','coverageFrom':start,'coverageTo':end},expected=422)
    check('validation refuses a slot ordinal that durable storage cannot retain',invalid_ordinal['code']=='ppa_side' and 'ppa_capabilities_v1' in invalid_ordinal['message'])
    def preview(t=terms,quantity='80',scope='draft',ids=None,known=None,expected=200,**changes):
        body={'snapshotId':frame,'definitionRevisionId':manifest['revisionId'],'referenceCatalogId':manifest['referenceCatalogId'],'referenceRevisionId':manifest['referenceRevisionId'],
            'scope':scope,'selectedIds':ids or [],'explicitSubset':scope=='trades','terms':t if scope=='draft' else None,'quantity':quantity if scope=='draft' else None,'coverageFrom':start if scope=='draft' else None,'coverageTo':end if scope=='draft' else None,
            'knownAt':known or cutoff(),'pendingActuals':'require_actuals_v1','detailMonths':['2031-11-01']}
        body.update(changes);return client.call('ppa/preview',body,expected=expected)
    result=preview()
    check('HTTP exact independent DST physical-minus-strike oracle',fraction(result['valueExcludingOverdue'])==Fraction(25*80*5,4) and fraction(result['completePv'])==2500)
    check('gross period summaries retain original payment dates and fifty native rows',sum(len(s['measures']['rows']) for s in result['bindings'][0]['sides'])==50 and len(result['bindings'][0]['sides'][0]['measures']['periods'])==1)
    operation=str(uuid4());body={'operationId':operation,'snapshotId':frame,'tradeId':None,'expectedRevision':None,'bookId':'d1000000-0000-0000-0000-000000000001','boundaryAt':None,'coverageFrom':start,'coverageTo':end,'quantity':'80','reason':'Synthetic booking','memo':'','booking':trade_booking(),'terms':terms}
    booked=client.call('ppa/book',body);trade=booked['tradeId'];old=cutoff()
    check('exact operation retry returns one durable trade',client.call('ppa/book',body)==booked and client.call('ppa/status',{'operationId':operation})==booked)
    stored=client.call('ppa/editor',{'snapshotId':frame,'tradeId':trade,'knownAt':old})
    check('typed storage terms round trip without changing economic identity',stored['instruments'][0]['terms']==terms and validate(stored['instruments'][0]['terms'])['identity']==valid['identity'])
    retained=preview(scope='trades',ids=[trade],known=old)
    check('stored HTTP valuation retains draft oracle and exact instrument identity',fraction(retained['valueExcludingOverdue'])==2500 and retained['bindings'][0]['identity']==valid['identity'])
    changed=deepcopy(body);changed.update(operationId=str(uuid4()),tradeId=trade,expectedRevision=1,boundaryAt='2031-11-02T06:00:00Z',coverageFrom='2031-11-02T06:00:00Z',quantity='60',terms=profile_terms(manifest,'50'),reason='Synthetic forward strike change')
    amended=client.call('ppa/amend',changed);now=cutoff();after=preview(scope='trades',ids=[trade],known=now)
    check('amendment retains prefix at original MW and strike and changes future tail',len(after['bindings'])==2 and fraction(after['valueExcludingOverdue'])==Fraction(2*80*5,4)+23*60*Fraction(-35,4))
    check('earlier certified knowledge preserves original binding and valuation',fraction(preview(scope='trades',ids=[trade],known=old)['valueExcludingOverdue'])==2500)
    old_pay=[s for b in after['bindings'] if b['quantity']=='80' for s in b['sides'] if s['coefficient']=='-1'][0]
    check('segment payment keeps original full-month date',all(r['valueAt'].startswith('2031-12-10T00:00:00') for r in old_pay['measures']['rows']))
    terminated=deepcopy(changed);terminated.update(operationId=str(uuid4()),expectedRevision=2,boundaryAt='2031-11-02T08:00:00Z',coverageFrom=None,coverageTo=None,quantity=None,terms=None,reason='Synthetic forward termination')
    client.call('ppa/terminate',terminated);ended=preview(scope='trades',ids=[trade])
    check('termination preserves prefix obligations and removes only forward tail',fraction(ended['valueExcludingOverdue'])==-850 and sum(len(s['measures']['rows']) for b in ended['bindings'] for s in b['sides'])==8)
    malformed=deepcopy(terms);malformed['memberships'][1]['side']['quantity']['slots']=[None]
    check('null quantity slot is structured refusal',preview(t=malformed,expected=422)['code']=='ppa_term')
    malformed=deepcopy(terms);malformed['memberships'][1]['side']['fixedPrice'].update(kind='stepped',price=None,steps=[None])
    check('null price step is structured refusal',preview(t=malformed,expected=422)['code']=='ppa_price_rule')
    check('contradictory subset scope refuses instead of narrowing a book',preview(scope='books',ids=[body['bookId']],explicitSubset=True,expected=422)['code']=='ppa_scope')
    check('unknown nested economics are malformed',preview(t={**terms,'inventedPolicy':'fallback'},expected=400)['code']=='request_format')
    check('detail months outside selected coverage add no financial rows',sum(len(s['measures']['rows']) for s in preview(detailMonths=['2031-01-01','2031-02-01','2031-03-01','2031-04-01','2031-05-01','2031-06-01','2031-07-01','2031-08-01','2031-09-01','2031-10-01','2031-11-01','2031-12-01'])['bindings'][0]['sides'])==50)
    return passed


def valuation_checks(url, query, manifest, frame):
    """Independent economic oracles through the public authorized operation."""
    from copy import deepcopy
    from datetime import timedelta
    from fractions import Fraction
    from benchmark_ppa_http import HttpFixture, terms, expected_value, midnight, instant
    from exact_values import fraction, instant as parse_instant
    from power_collection_fixture import Collector
    import json
    from pathlib import Path
    from ppa_expected_values import expected as independent_expected
    independent=independent_expected(json.loads((Path(__file__).resolve().parents[1]/'tests/fixtures/ppa-oracle-inputs.json').read_text()))
    fixture=HttpFixture(url,manifest,frame);passed=0
    config=manifest['ppaBookings']['workloads']['60']
    def check(name, condition):
        nonlocal passed
        assert condition,name
        passed+=1;print('PASS PPA bookings HTTP oracle: '+name,flush=True)
    def value(result):return fraction(result['valueExcludingOverdue'])
    def rows(result):return [r for b in result['bindings'] for s in b['sides'] for r in s['measures']['rows']]
    start,end='2031-11-02T04:00:00Z','2031-11-03T05:00:00Z'
    fixture.load(60,'2031-11-01','2031-12-01')
    selected=terms(manifest)
    result=fixture.preview(selected,start,end,detail=['2031-11-01'])
    # 25 elapsed off-peak hours x 40 MWh per hour. Gross sides remain
    # +40,000 physical -40,000 strike -2,000 charge +5,000 REC -1,000 REC cash.
    check('five gross sides independently reconcile on the 25-hour DST day',
          [fraction(s['measures']['valueExcludingOverdue']) for s in result['bindings'][0]['sides']]==[40000,-40000,-2000,5000,-1000] and value(result)==2000 and len(rows(result))==125)
    check('physical and certificate rows never invent cash lifecycle',all(r['lifecycle'] is None for r in rows(result) if r['kind'] in ('Delivery','Holding')))
    payments=[r for r in rows(result) if r['kind']=='Payment']
    check('payment direction changes money while economic quantities remain positive',len(payments)==75 and all(fraction(r['quantity'])==40 and fraction(r['amount'])<0 for r in payments))
    reverse=deepcopy(selected)
    for member in reverse['memberships']:member['coefficient']=str(-int(member['coefficient']))
    check('reversing every explicit coefficient negates five gross components once',value(fixture.preview(reverse,start,end))==-2000)
    check('80 MW plus 20 MW equals a single 100 MW holding',value(result)+value(fixture.preview(selected,start,end,quantity='20'))==value(fixture.preview(selected,start,end,quantity='100')))
    no_certificate=terms(manifest,five=False)
    indexed=deepcopy(no_certificate);indexed['memberships'][1].update(role='indexed_payment')
    indexed['memberships'][1]['side'].update(kind='indexed',fixedPrice=None,indexPrice={'curve':config['shaped'],'quantity':'test_power_quantity','multiplier':'1','indexMultiplier':'0.95', 'settlement': None,'adder':'2'})
    indexed_value=fixture.preview(indexed,start,end,detail=['2031-11-01'])
    check('index multiplier precedes adder and preserves both gross components',value(indexed_value)==0 and [fraction(s['measures']['valueExcludingOverdue']) for s in indexed_value['bindings'][0]['sides']]==[40000,-40000])
    indexed['memberships'][1]['side']['indexPrice'].update(indexMultiplier='1',adder='1')
    check('separately payable index adder is exact cash rather than netted exposure',value(fixture.preview(indexed,start,end))==-1000)
    source=manifest['ppa']['series']['test_asset_a_60']
    fixture.revise(source['forecast'],start,['0.25'])
    adjusted=terms(manifest,five=False);adjusted['normalizationAssets']=['test_asset_a']
    adjusted['memberships'][0]['side'].update(location='test_hub_a')
    adjusted['memberships'][0]['side']['quantity'].update(sources=['test_asset_a_60'],stage='delivered')
    adjusted_result=fixture.preview(adjusted,start,'2031-11-02T05:00:00Z',detail=['2031-11-01'])
    check('independent degradation and delivered-loss oracle scales0.2401perMW to19.208MWh',adjusted_result['bindings'][0]['sides'][0]['measures']['rows'][0]['quantity']==independent['plan_normalization/degraded_mwh'])
    # Two exact anniversaries, tested on one covered hour each. The base date
    # remains attached to full terms, rather than restarting at the segment.
    fixture.load(60,'2031-11-01','2033-11-01')
    escalated=terms(manifest,first='2031-11-01',last='2033-11-01',five=False)
    escalated['memberships'][1]['side']['fixedPrice'].update(kind='escalated',rate='0.025',baseDate='2029-11-15',basis='anniversary_step_v1')
    for first,last,price in [('2031-11-15T04:00:00Z','2031-11-15T05:00:00Z',Fraction(41)),('2031-11-15T05:00:00Z','2031-11-15T06:00:00Z',Fraction('42.025'))]:
        answer=fixture.preview(escalated,first,last,detail=['2031-11-01'])
        strike=answer['bindings'][0]['sides'][1]['measures']['rows'][0]
        check('anniversary compounding at exact operating-day boundary '+first,fraction(strike['amount'])==-40*price)
    stepped=deepcopy(escalated);stepped['memberships'][1]['side']['fixedPrice'].update(kind='stepped',price=None,rate=None,baseDate=None,basis=None,steps=[{'from':'2031-11-01','price':'40'},{'from':'2031-11-15','price':'50'}])
    check('explicit price step switches at its original operating-day boundary',fraction(fixture.preview(stepped,'2031-11-15T05:00:00Z','2031-11-15T06:00:00Z',detail=['2031-11-01'])['bindings'][0]['sides'][1]['measures']['rows'][0]['amount'])==-2000)
    limited=next(f for f in fixture.client.call('ppa/frames') if parse_instant(f['at'])==parse_instant(manifest['ppaBookings']['limitedAt']))
    original_frame=fixture.frame;fixture.frame=limited['id']
    try:
        limited_value=fixture.preview(terms(manifest,last='2032-11-01'),'2031-11-01T04:00:00Z','2031-12-01T05:00:00Z')
        check('selected coverage requires only its original certificate vintage, not later term vintages',value(limited_value)==313680 and limited_value['work']['rowEvaluations']==3605)
    finally:fixture.frame=original_frame
    twenty_four=terms(manifest,last='2033-11-01')
    first,last=(instant(midnight(day,config['zone'])) for day in ('2031-11-01','2033-11-01'))
    oracle,cells,_,_=expected_value('2031-11-01','2033-11-01',config['zone'])
    total=fixture.preview(twenty_four,first,last)
    check('24-month full HTTP oracle retains every native interval and five sides',value(total)==oracle and total['work']['rowEvaluations']==cells*5 and total['work']['unitValuations']==1 and total['work']['uniqueSideCalculations']==5)
    check('summary-only full-term response contains no unintended interval details',not rows(total) and len(total['bindings'][0]['sides'][0]['measures']['periods'])==24)
    check('actual selected detail row budget refuses a complete oversized operation',fixture.preview(twenty_four,first,last,detail=['2031-11-01','2031-12-01']+[f'2032-{m:02}-01' for m in range(1,11)],expected=422)['code']=='ppa_limit')
    # Native quarter-hour shaping is explicitly Central under the collected
    # source compatibility contract; there is no automatic Eastern fallback.
    fixture.load(15,'2031-11-01','2031-12-01')
    quarter=terms(manifest,15);quarter_start,quarter_end='2031-11-02T05:00:00Z','2031-11-03T06:00:00Z'
    q=fixture.preview(quarter,quarter_start,quarter_end,detail=['2031-11-01'])
    check('native quarter-hour DST schedule independently reconciles 500 gross rows',value(q)==2000 and len(rows(q))==500 and all(fraction(r['quantity'])==10 for r in rows(q)))
    quarter_config=manifest['ppaBookings']['workloads']['15']
    # Opposite offsets in the next off-peak hour keep the full block's shape
    # mean exactly zero, independently fixing the selected prices at10/20/30/40.
    fixture.revise(quarter_config['shape'],quarter_start,['-30','-20','-10','0','30','20','10','0'])
    fixture.revise(quarter_config['forecast'],quarter_start,['0.05','0.1','0.15','0.2'])
    covariance=terms(manifest,15,five=False);covariance['memberships'][1]['side']['fixedPrice']['price']='0'
    cov=fixture.preview(covariance,quarter_start,'2031-11-02T06:00:00Z',detail=['2031-11-01'])
    expected_covariance=independent['plan_covariance/amount']
    check('independent quarter-hour covariance oracle sums 1x10+2x20+3x30+4x40 to300',cov['valueExcludingOverdue']==expected_covariance and [fraction(r['amount']) for r in cov['bindings'][0]['sides'][0]['measures']['rows']]==[10,40,90,160])
    eastern=deepcopy(quarter);eastern['delivery']['clock']='synthetic_eastern'
    check('incompatible Eastern quarter-hour request refuses without changing provider or clock',fixture.preview(eastern,start,end,expected=422)['code']=='ppa_cadence')
    # Two independently authored source contributions use the sum of their
    # energy divided by the sum of contemporaneous capacity, never a mean of
    # individual per-asset factors. Both capacities halve at the declared switch.
    multi=terms(manifest,five=False)
    multi['normalizationAssets']=manifest['ppa']['assets']
    source_names=[f'{asset}_60' for asset in manifest['ppa']['assets']]
    q={'kind':'volume','sources':source_names,'stage':'generated','capacity':None,'energy':None,'hours':None,'slots':[],'assets':[]}
    multi['memberships'][0].update(role='indexed_payment')
    multi['memberships'][0]['side'].update(kind='indexed',quantity=deepcopy(q),location=None,indexPrice={'curve':config['shaped'],'quantity':'test_power_quantity','multiplier':'1','indexMultiplier':'1', 'settlement': None,'adder':'0'})
    multi['memberships'][1]['side']['quantity']=deepcopy(q)
    multi_first,multi_last='2031-11-15T04:00:00Z','2031-11-15T06:00:00Z'
    a_source,b_source=[manifest['ppa']['series'][name] for name in source_names]
    fixture.revise(a_source['forecast'],multi_first,['0.5','0.5'])
    missing_multi=fixture.preview(multi,multi_first,multi_last,expected=422)
    check('missing asset contribution refuses rather than becoming explicit zero',missing_multi['code']=='volume_missing')
    fixture.revise(b_source['forecast'],multi_first,['0','0'])
    zero_multi=fixture.preview(multi,multi_first,multi_last,detail=['2031-11-01'])
    check('explicit zero contribution retains both assets in the capacity denominator',all(fraction(r['quantity'])==Fraction(392,15) for r in rows(zero_multi)))
    fixture.revise(b_source['forecast'],multi_first,['0.25','0.25'])
    multi_value=fixture.preview(multi,multi_first,multi_last,detail=['2031-11-01'])
    check('multi-asset energy sum and capacity-version denominator independently reconcile',all(fraction(r['quantity'])==Fraction(98,3) for r in rows(multi_value)) and len(multi_value['receipt']['assetVersions'])==4)
    # Exact MW-day monthly capacity. The 25-hour DST day counts once.
    capacity=terms(manifest,five=False)
    capacity['memberships'][0].update(role='capacity_delivery')
    capacity['memberships'][0]['side'].update(kind='capacity',location=None,quantity={'kind':'accredited','sources':[],'stage':None,'capacity':None,'energy':None,'hours':None,'slots':[],'assets':[config['asset']]},attribute={'quantity':'test_ppa_capacity_days_quantity','conversion':None,'eligibility':None,'holding':None,'disposition':None,'rule':'mw_day_per_operating_day_v1'})
    capacity['memberships'][1].update(role='capacity_payment')
    capacity['memberships'][1]['side']['quantity']['slots']=[{'role':'capacity_delivery','ordinal':0}]
    capacity['memberships'][1]['side']['fixedPrice'].update(quantity='test_ppa_capacity_days_quantity',price='0')
    cap=fixture.preview(capacity,'2031-11-01T04:00:00Z','2031-12-01T05:00:00Z',detail=['2031-11-01'])
    check('capacity is 64 allocated MW times 30 operating days times four per MW-day',value(cap)==7680 and len(rows(cap))==2 and fraction(rows(cap)[0]['quantity'])==1920)
    check('capacity rejects an interior native-hour boundary',fixture.preview(capacity,start,end,expected=422)['code']=='ppa_segment_boundary')
    invalid=deepcopy(capacity);invalid['memberships'][1]['side']['fixedPrice']['quantity']='test_power_quantity'
    check('capacity payment cannot consume an MWh quote',fixture.preview(invalid,'2031-11-01T04:00:00Z','2031-12-01T05:00:00Z',expected=422)['code']=='ppa_quantity_unit')
    # Audited collected prices and interval revisions qualify delivered known,
    # estimated, corrected and overdue rows at explicit knowledge cutoffs.
    collector=Collector(query,manifest,{'energy':{'provider':'PJM','sourceId':manifest['intervalCurves']['source'],'clock':'synthetic_eastern'}})
    collector.publish('energy','2020-12-14',parse_instant('2020-12-14T05:00:00Z'),24,60,['40']*24,1)
    past_start,past_end='2020-12-14T05:00:00Z','2020-12-14T06:00:00Z'
    fixture.revise(config['forecast'],past_start,['0.25'])
    past=terms(manifest,first='2020-12-01',last='2021-01-01')
    fixture.revise(config['meter'],past_start,['50']);old=fixture.cutoff()
    actual=fixture.preview(past,past_start,past_end,known=old,detail=['2020-12-01'])
    check('meter 50 MWh divided by 200 MW basis times 80 MW remains exactly 20 MWh',all(fraction(r['quantity'])==20 for r in rows(actual)) and value(actual)==40)
    actual_payments=[r for r in rows(actual) if r['kind']=='Payment']
    check('known delivered quantity plus actual physical price is realised',len(actual_payments)==3 and all(r['economicClass']=='Realised' for r in actual_payments) and actual['receipt']['actuals'] and actual['receipt']['quantityRevisions'])
    fixture.revise(config['meter'],past_start,['60']);new=fixture.cutoff()
    check('eligible later correction changes quantity while original cutoff replays exactly',value(fixture.preview(past,past_start,past_end,known=new))==48 and value(fixture.preview(past,past_start,past_end,known=old))==40)
    overdue_frame=next(f['id'] for f in fixture.client.call('workspace')['frames'] if f['at'].startswith('2021-01-10T00:00:00'))
    overdue=fixture.preview(past,past_start,past_end,known=old,frame=overdue_frame,detail=['2020-12-01'])
    check('retained vintage certificates remain current holding value after cash is due',value(overdue)==100 and overdue['completePv'] is None and fraction(overdue['overdueKnownBalance'])==-860 and fraction(overdue['overdueEstimated'])==0)
    check('past physical value never becomes an overdue cash obligation',[(r['coverage'],r['lifecycle']) for r in rows(overdue) if r['kind'] in ('Delivery','Holding')]==[('DeliveredValue',None),('HoldingValue',None)])
    fixture.revise(config['meter'],past_start,[None]);missing=fixture.cutoff()
    estimated=fixture.preview(past,past_start,past_end,known=missing,frame=overdue_frame,pendingActuals='estimate_until_actual_v1',detail=['2020-12-01'])
    check('estimated overdue quantity remains separate from known balance and complete PV',value(estimated)==100 and fraction(estimated['overdueKnownBalance'])==0 and fraction(estimated['overdueEstimated'])==-860 and estimated['completePv'] is None)
    check('require-actuals refuses missing delivered meter before its explicit deadline',fixture.preview(past,past_start,past_end,known=missing,expected=422)['code']=='actuals_required')
    fixture.revise(config['forecast'],past_start,['0.25','0.25'])
    fixture.revise(config['meter'],past_start,['50',None])
    mixed=fixture.preview(past,past_start,'2020-12-14T07:00:00Z',frame=overdue_frame,pendingActuals='estimate_until_actual_v1',detail=['2020-12-01'])
    check('mixed known and estimated overdue balances remain disjoint beside certificate holdings',value(mixed)==200 and fraction(mixed['overdueKnownBalance'])==-860 and fraction(mixed['overdueEstimated'])==-860 and mixed['completePv'] is None)
    mixed_rows=rows(mixed)
    check('economic classes and coverage each reconcile every gross amount exactly once',sum(fraction(r['amount']) for r in mixed_rows)==sum(fraction(c['amount']) for s in mixed['bindings'][0]['sides'] for c in s['measures']['classes'])==sum(fraction(c['amount']) for s in mixed['bindings'][0]['sides'] for c in s['measures']['coverages']))
    fixture.revise(config['forecast'],'2020-12-15T05:00:00Z',['0.25'])
    fixture.revise(config['meter'],'2020-12-15T05:00:00Z',['50'])
    check('independent collected-price deadline refuses an absent due actual',fixture.preview(past,'2020-12-15T05:00:00Z','2020-12-15T06:00:00Z',expected=422)['code']=='interval_actual_missing')
    collector.publish('energy','2020-12-15',parse_instant('2020-12-15T05:00:00Z'),24,60,['40']*24,1)
    fixture.revise(config['forecast'],'2020-12-15T14:00:00Z',['0.25'])
    due=terms(manifest,five=False,first='2020-12-01',last='2021-01-01');due['memberships'][0]['side']['quantity']['sources']=[config['asset']+'_deadline']
    check('meter deadline equality refuses estimates rather than extrapolating',fixture.preview(due,'2020-12-15T14:00:00Z','2020-12-15T15:00:00Z',pendingActuals='estimate_until_actual_v1',expected=422)['code']=='meter_actual_missing')
    for mutation,code in [('implementation','ppa_term_unsupported'),('normalization','ppa_normalization'),('boundary','ppa_segment_boundary')]:
        bad=deepcopy(no_certificate);first,last=start,end
        if mutation=='implementation':bad['implementation']='unsupported_ppa'
        elif mutation=='normalization':bad['normalizationAssets']=[]
        else:first='2031-11-02T04:30:00Z'
        check('explicit '+mutation+' refusal is structured',fixture.preview(bad,first,last,expected=422)['code']==code)
    oversized=terms(manifest,last='2062-11-01')
    check('over-limit term refuses before valuation inputs are read',fixture.preview(oversized,start,end,expected=422)['code']=='ppa_size')
    virtual=deepcopy(no_certificate)
    volume=deepcopy(virtual['memberships'][0]['side']['quantity'])
    virtual['memberships'][0].update(role='indexed_payment')
    virtual['memberships'][0]['side'].update(kind='indexed',location=None,indexPrice={'curve':config['shaped'],'quantity':'test_power_quantity','multiplier':'1','indexMultiplier':'1', 'settlement': None,'adder':'1'})
    virtual['memberships'][1]['side']['quantity']=deepcopy(volume)
    basis=deepcopy(virtual);basis['memberships'][1].update(role='indexed_payment',ordinal=1)
    basis['memberships'][1]['side'].update(kind='indexed',fixedPrice=None,indexPrice={'curve':manifest['intervalCurves']['shaped'],'quantity':'test_power_quantity','multiplier':'1','indexMultiplier':'1', 'settlement': None,'adder':'0'})
    for name,contract,first,last,expected in [('virtual_fixed_for_floating',virtual,start,end,1000),('basis_cash',basis,start,end,1000),('indexed_physical',indexed,start,end,-1000),('capacity_agreement',capacity,'2031-11-01T04:00:00Z','2031-12-01T05:00:00Z',7680)]:
        validation=fixture.client.call('ppa/validate',{'snapshotId':fixture.frame,'terms':contract,'quantity':'80','coverageFrom':first,'coverageTo':last})
        draft=fixture.preview(contract,first,last)
        booked,_=fixture.book(contract,first,last)
        restored=fixture.client.call('ppa/editor',{'snapshotId':fixture.frame,'tradeId':booked['tradeId'],'knownAt':fixture.cutoff()})
        stored=fixture.preview(contract,scope='trades',ids=[booked['tradeId']])
        check(name+' compiler, exact durable terms and HTTP valuation round trip',validation['capability']==name and restored['instruments'][0]['terms']==contract and stored['bindings'][0]['identity']==validation['identity'] and value(draft)==value(stored)==expected)
    print(f'{passed} independent PPA HTTP economic checks passed',flush=True)
    return fixture


def composed_checks(fixture,query,manifest):
    """Independent mixed-side and direct-runtime admission checks; synthetic only."""
    from copy import deepcopy
    from fractions import Fraction
    from uuid import uuid4
    from benchmark_ppa_http import terms
    from check_booked_side_programs import runtime_query
    from exact_values import fraction
    import json
    fixture.load(60,'2031-11-01','2031-12-01')
    selected=terms(manifest);selected['implementation']='ppa_composed_instrument_v1'
    selected['memberships']=[selected['memberships'][0],selected['memberships'][3],selected['memberships'][2]]
    config=manifest['ppaBookings']['workloads']['60']
    capacity=deepcopy(selected['memberships'][0]);capacity.update(role='capacity_delivery')
    capacity['side'].update(kind='capacity',location=None,
        quantity=dict(kind='accredited',sources=[],stage=None,capacity=None,energy=None,hours=None,slots=[],assets=[config['asset']]),
        attribute=dict(quantity='test_ppa_capacity_days_quantity',conversion=None,eligibility=None,holding=None,disposition=None,rule='mw_day_per_operating_day_v1'))
    selected['memberships'].insert(2,capacity)
    first,last='2031-11-01T04:00:00Z','2031-12-01T05:00:00Z'
    # 721 hours,320 peak hours,40MWh/hour. Power60/40, REC5, fee-2;
    # capacity80MW*0.8*30days*4. No production evaluator constructs this oracle.
    expected=40*(43*721+20*320)+7680
    assert fraction(fixture.preview(selected,first,last)['valueExcludingOverdue'])==expected
    book=str(uuid4());query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic composed PPA check',1,'2020-01-01T00:00:00Z');")
    receipt,body=fixture.book(selected,first,last,book=book)
    assert fixture.client.call('ppa/book',body)==receipt
    known=fixture.cutoff()
    restored=fixture.client.call('ppa/editor',dict(snapshotId=fixture.frame,tradeId=receipt['tradeId'],knownAt=known))
    assert restored['instruments'][0]['terms']==selected
    assert fraction(fixture.preview(selected,scope='trades',ids=[receipt['tradeId']],known=known)['valueExcludingOverdue'])==expected
    wrong=deepcopy(selected);wrong['implementation']='unrecognized_composition_v1'
    refused=fixture.client.call('ppa/validate',dict(snapshotId=fixture.frame,terms=wrong,quantity='80',coverageFrom=first,coverageTo=last),expected=422)
    assert refused['code']=='ppa_term_unsupported'
    partial=fixture.client.call('ppa/validate',dict(snapshotId=fixture.frame,terms=selected,quantity='80',coverageFrom=first,coverageTo='2031-11-15T05:00:00Z'),expected=422)
    assert partial['code']=='ppa_segment_boundary'
    print('PASS composed PPA: independent four-side oracle, exact replay, retained terms and capacity boundaries',flush=True)

    context=dict(snapshotId=fixture.frame,convention='trading_workspace_v1',knowledgeMode='current')
    view=dict(mode='trades',bookIds=[book],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None,priceNumericalPolicy=None)
    workspace=fixture.client.call('trading/workspace',dict(context=context,view=view,bookingCatalog='directory',summaries=[]))
    rows=workspace['blotter']['holdings'];assert len(rows)==1 and Fraction(rows[0]['presentValue'])==expected
    retained={**context,'knowledgeMode':'retained','contextId':workspace['catalog']['context']['contextId']}
    assert fixture.client.call('trading/workspace',dict(context=retained,view=view,bookingCatalog='directory',summaries=[]))['blotter']['holdings'][0]['presentValue']==rows[0]['presentValue']
    fixture.client.call('profile',{'profile':'risk'})
    try:
        catalog=fixture.client.call('portfolio/risk/catalog',dict(context=retained,bookIds=[book],portfolioIds=[]))
        factor=next(f for f in catalog['factors'] if manifest['ppaBookings']['capacity'] in f['curves'])
        request=dict(context=retained,bookIds=[book],portfolioIds=[],bumps=[dict(factor=factor['factor'],size='0.1',convention=catalog['bumpConvention'])],convention=catalog['convention'],numericalPolicy=catalog['numericalPolicy'],pricingPolicy=catalog['pricingPolicy'],stress=[])
        report=fixture.client.call('portfolio/risk',request)
        positions=report['factors'][0]['positions'];assert len(positions)==1 and not report['excludedPositions']
        assert Fraction(positions[0]['values']['base'])==expected and Fraction(positions[0]['values']['upPnl'])==192
    finally:fixture.client.call('profile',{'profile':'trader'})
    print('PASS composed PPA: common Blotter, retained context and independent capacity shock',flush=True)

    wrong=deepcopy(selected);wrong['normalizationAssets']=['test_asset_a','test_asset_b'];wrong['memberships']=[]
    for ordinal,asset in enumerate(wrong['normalizationAssets']):
        member=deepcopy(capacity);member['ordinal']=ordinal;member['side']['quantity']['assets']=[asset];wrong['memberships'].append(member)
    assert fixture.client.call('ppa/validate',dict(snapshotId=fixture.frame,terms=wrong,quantity='80',coverageFrom=first,coverageTo=last),expected=422)['code']=='ppa_normalization'
    frame=next(f for f in fixture.client.call('workspace')['frames'] if f['id']==fixture.frame)
    command={k:v for k,v in body.items() if k!='operationId'};command.update(action='booked',valuationAt=frame['at'],effectiveAt=frame['at'],terms=wrong)
    encoded=json.dumps(command).replace("'","''")
    before=query('SELECT count(*) FROM trading.ppa_trade_revisions;')
    runtime_query(query,manifest,f"DO $check$ BEGIN BEGIN PERFORM security.save_ppa_booking('{manifest['catalogId']}','{uuid4()}','{encoded}'::jsonb);RAISE EXCEPTION 'subset_capacity_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'ppa_normalization' THEN RAISE;END IF;END;END $check$;",commit=False)
    assert query('SELECT count(*) FROM trading.ppa_trade_revisions;')==before
    print('PASS composed PPA: managed/direct-runtime capacity-set parity and complete rollback',flush=True)
