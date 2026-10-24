"""Generated date rules, commodity restrictions and retained schedule identities."""
from preview_fixture import trade_booking
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url,client=None):
    c=client or PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog=c.call('swaps/catalog',context)
    assert catalog['paymentOptions']['frequencies']==[{'value':'monthly','label':'Monthly'}]
    assert {r['value'] for r in catalog['paymentOptions']['rules']}=={'days_after_period_end','day_of_following_month'}
    assert catalog['paymentOptions']['implementation']=='monthly_payment_date_rules_v1'
    assert all(i['currency']=='USD' and i['currencyCode']=='usd' for i in catalog['indices'])
    assert 'references' in c.call('date-schedules/catalog',{'context':catalog['context']})
    index=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    lag={'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    following={**lag,'rule':'day_of_following_month','days':None,'dayOfMonth':18,'dayBasis':None,'shortMonth':'reject'}
    body={**context,'operationId':str(uuid4()),**trade_booking(), 'bookId':catalog['books'][0]['id'],'commodity':index['commodity'],
          'definitionRevisionId':index['definitionRevisionId'],'quantity':'100','fixedCoefficient':'-1','discounting':False,
          'fixed':{'price':'3','currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':lag},
          'floating':{'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01',
                      'paymentSchedule':following,'resetFrequency':'daily','averagingMethod':'arithmetic'}}
    passed=0
    def check(name,ok=True):
        nonlocal passed
        assert ok,name
        passed+=1;print('PASS schedule booking: '+name,flush=True)
    # A second session publishes after this editor captured its catalog. Neither
    # the picker names nor resolution may advance to that later knowledge.
    publisher=PreviewClient(url);publisher.bootstrap();publisher.call('profile',{'profile':'editor'})
    symbol='oe.late_'+uuid4().hex[:12]
    draft=publisher.call('date-schedules/save',{'definition':{'name':'Synthetic late anchors','symbol':symbol,'firstMonth':'2020-12','lastMonth':'2021-03','anchor':'day','day':1,'shortMonth':'reject','adjustment':'unadjusted','calendar':None},'calendar':next(c['reference'] for c in publisher.call('date-schedules')['calendars'] if c['reference']['code']=='test_business'),'selection':'context_month','effectiveFrom':'2020-12-01','effectiveTo':'2021-04-01'})
    publisher.call('profile',{'profile':'approver'})
    publisher.call('date-schedules/publish',{key:draft[key] for key in ['id','version','generation']})
    frozen=c.call('date-schedules/catalog',{'context':catalog['context']})
    latest=c.call('swaps/catalog',context)['context']
    current=c.call('date-schedules/catalog',{'context':latest})
    c.call('date-schedules/resolve',{'context':catalog['context'],'expression':symbol+'[1]'},expected=422)
    resolved=c.call('date-schedules/resolve',{'context':latest,'expression':symbol+'[1]'})
    check('catalog and resolver share the editor cutoff across a later publication in another session',not any(r['symbol']==symbol for r in frozen['references']) and any(r['symbol']==symbol for r in current['references']) and resolved['date']=='2021-01-01')
    preview=c.call('swaps/preview',body)['valuation']
    check('both rule inputs generate dates and price actual independent side tables',preview['presentValue']=='16' and all(s['payments'][0]['paymentDate']=='2021-02-18' for s in preview['sides']))
    check('commodity catalog originates from exact reference rows',any(row['code']==index['commodity'] and row['name'] for row in catalog['commodities']))
    booked=c.call('swaps/book',body);assert booked==c.call('swaps/book',body)
    detail=c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})
    check('stored rules survive booking independently with no authored payment timestamp',detail['state']['terms']['fixed']['paymentSchedule']==lag and detail['state']['terms']['floating']['paymentSchedule']==following and detail['state']['terms']['fixed']['paymentAt'] is None)
    for replacement in [{'commodity':'test_power'},{'commodity':None},{'fixed':{**body['fixed'],'paymentAt':'2021-02-18T12:00:00Z'}},{'floating':{**body['floating'],'resetFrequency':'weekly'}}, {'fixed':{**body['fixed'],'paymentSchedule':None}}]:
        c.call('swaps/preview',{**body,**replacement},expected=422)
    check('wrong commodity missing rules authored timestamps and unsupported sampling reject')
    c.call('swaps/preview',{**body,'floating':{**body['floating'],'paymentSchedule':{**following,'dayOfMonth':31}}},expected=422)
    clamped=c.call('swaps/preview',{**body,'floating':{**body['floating'],'paymentSchedule':{**following,'dayOfMonth':31,'shortMonth':'clamp'}}})['valuation']
    check('short-month policy is applied by the backend',clamped['sides'][1]['payments'][0]['paymentDate']=='2021-02-28')
    calendars=catalog['paymentCalendars'];assert calendars
    calendar=calendars[0]['code']
    adjusted=c.call('swaps/preview',{**body,'floating':{**body['floating'],'paymentSchedule':{**following,'dayOfMonth':28,'shortMonth':'reject','adjustment':'following','calendar':calendar}}})['valuation']
    check('business-day adjustment uses the pinned reference calendar',adjusted['sides'][1]['payments'][0]['paymentDate']=='2021-03-01')
    # Distinct rules that happen to produce the same date retain distinct side identity.
    other={**body,'operationId':str(uuid4()),'fixed':{**body['fixed'],'paymentSchedule':following}}
    other_booked=c.call('swaps/book',other)
    check('economic rule identity is preserved even when generated dates coincide',other_booked['instrumentId']!=booked['instrumentId'])
    amended=c.call('swaps/amend',{**body,'operationId':str(uuid4()),'tradeId':booked['tradeId'],'expectedRevision':1,'effectiveAt':catalog['asOf'],'reason':'Synthetic payment rule change','fixed':other['fixed']})
    old=c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':1})
    check('rule amendments append history without rewriting old schedules',amended['revision']==2 and old['state']['terms']['fixed']['paymentSchedule']==lag and not old['canAmend'])
    c.call('swaps/book',{**other,'operationId':str(uuid4()),'quantity':'200', 'bookId':catalog['books'][1]['id']})
    report=c.call('trading/blotter',{'context':{**context,'convention':'trading_workspace_v1'},'view':{'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
    holdings=[h for h in report['holdings'] if h['instrumentType']=='financial_swap']
    unique={h['instrumentId'] for h in holdings}
    check('request-local valuation work follows unique immutable swap inputs rather than trade rows',len(unique)<len(holdings) and report['work']['swapValuations']==len(unique) and report['work']['swapTrades']==len(holdings) and report['work']['supplementalMarketLoads']<=1)
    print(f'{passed} schedule booking checks passed.',flush=True)
