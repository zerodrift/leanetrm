"""Joint swing value through the retained trading and risk routes."""
import json
from copy import deepcopy
from datetime import datetime
from decimal import Decimal, localcontext
from difflib import unified_diff
from itertools import islice
from uuid import uuid4

from check_gas_swing_numerics import day, fixture, reference
from preview_client import PreviewClient, initial_frame

DAYS = ['2021-01-31', '2021-02-01', '2021-02-02']
AT = '2020-12-18T16:00:00Z'


def assert_replay(field, queued, live):
    if queued!=live:
        def lines(value):
            return json.dumps(value,indent=2,sort_keys=True).splitlines()
        diff = unified_diff(lines(live),lines(queued),fromfile='synchronous',tofile='queued',n=1)
        raise AssertionError(field+' replay mismatch\n'+'\n'.join(islice(diff,10)))


def upgraded_book_checks(c, ctx, book, swing, companion):
    shared = {**ctx,'convention':'trading_workspace_v1'}
    view = dict(mode='trades',bookIds=[book],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)
    response = c.call('trading/blotter',dict(context=shared,view=view))
    holdings = {h['tradeId']:h for h in response['holdings']}
    assert response['unvaluedPositions']==1 and holdings[companion['tradeId']]['presentValue']=='300'
    affected = holdings[swing['tradeId']]
    assert affected['presentValue'] is None and affected['unavailableReason']=='swing_model_scope',affected
    pivot = c.call('trading/blotter',dict(context=shared,view={**view,'mode':'pivot','groupBy':['book'],
        'measures':['presentValue','tradeCount'],'pivotConvention':'trading_blotter_money_totals_v1'}))
    assert sum(g['tradeCount'] for g in pivot['groups'])==2 and sum(g['unvaluedPositions'] for g in pivot['groups'])==1
    assert sum(Decimal(g['presentValue']) for g in pivot['groups'] if g['presentValue'] is not None)==300
    risk = PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    scope = dict(context=shared,bookIds=[book],portfolioIds=[],tradeIds=None)
    cat = risk.call('portfolio/risk/catalog',scope)
    assert [(p['tradeId'],p['reason']) for p in cat['excludedPositions']]==[(swing['tradeId'],'swing_model_scope')]
    result = risk.call('portfolio/risk',dict(scope,context={**shared,'knowledgeMode':'retained','contextId':cat['context']['contextId']},
        detail='holdings',convention=cat['convention'],numericalPolicy=cat['numericalPolicy'],pricingPolicy=cat['pricingPolicy'],
        stress=[],bumps=[dict(factor=cat['factors'][0]['factor'],size='0.001',convention=cat['bumpConvention'])]))
    assert result['excludedPositions']==cat['excludedPositions']
    factor, = result['factors']
    position, = factor['positions']
    assert position['tradeId']==companion['tradeId'] and position['values']['base']==factor['totals'][0]['values']['base']=='300'
    print('PASS populated 0404 overlapping swing and ordinary companion retain sealed programs across 0405: one row-local refusal and valued subtotal 300 on Blotter and risk',flush=True)


def route_checks(c, query, manifest, context, template, run_worker):
    risk = PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    writer = PreviewClient(c.url)
    writer.bootstrap()
    writer.call('profile',dict(profile='editor'))
    book = str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic swing routes',publication_order,effective_at FROM trading.books LIMIT 1;")

    def book_strip(prefix):
        body = deepcopy(template)
        body.update(bookId=book,operationId=str(uuid4()))
        body['physicalSides'][0]['deliveryPoint'] = prefix + '_point'
        body['swingRights'][0].update(totalMinimum='2400', totalMaximum='2700')
        return body, c.call('gas/composition/book', body)

    gain = book_strip('synthetic_intrinsic')
    loss = book_strip('synthetic_mandatory')
    stochastic = book_strip('synthetic_intrinsic')
    from check_gas_pricing import staged_booking
    staged_booking(query,manifest,gain[0],{**loss[0],'operationId':str(uuid4())},'gas_schedule_assignment')
    plain = deepcopy(gain[0])
    plain.pop('swingRights')
    assert c.call('gas/composition/book',{**plain,'operationId':str(uuid4())},expected=422)['code']=='gas_input'
    plain['physicalSides'][0]['deliveryPoint'] = 'synthetic_swing_overlap_point'
    companion = c.call('gas/composition/book',{**plain,'operationId':str(uuid4())})
    assert c.call('gas/composition/editor',dict(context=context,tradeId=companion['tradeId']))['valuation']['presentValue']=='300'
    view = dict(mode='trades',bookIds=[book],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)

    def shared(ctx):
        return {**ctx,'convention':'trading_workspace_v1'}

    def read(trade, ctx):
        return c.call('gas/swing/read', dict(context=ctx,tradeId=trade['tradeId'],instrumentId=trade['instrumentId']))

    coordinates = {}
    def pin(trade,ctx):
        selected = read(trade,ctx)['context']
        coordinates[selected['contextId']] = dict(effectiveOn=selected['effectiveOn'],knownAt=selected['knownAt'],mode='at')
        return {**ctx,'contextId':selected['contextId'],'knowledgeMode':'retained'}

    def mark(curve, month, value, previous=0):
        return writer.call('volatility-marks/record',dict(context=context,definitionRevisionId=template['definitionRevisionId'],
            curve=curve,operationId=str(uuid4()),reason='Synthetic swing volatility',
            marks=[dict(month=month,volatility=value,expectedRevision=previous)]))['marks'][0]

    def routes(case,ctx,expected=None,reason=None):
        body,trade = case
        position = dict(context=shared(ctx),tradeId=trade['tradeId'],bindingId=trade['instrumentId'],revision=1,instrumentType='physical_gas')
        preview = dict(body,snapshotId=ctx['snapshotId'],previewTradeId=trade['tradeId'],knowledgeMode=ctx['knowledgeMode'])
        if 'contextId' in ctx:
            preview['previewContextId'] = ctx['contextId']
        status = 422 if reason else 200
        preview = c.call('gas/composition/preview',preview,expected=status)
        editor = c.call('gas/composition/editor',dict(context=ctx,tradeId=trade['tradeId']),expected=status)
        single = c.call('trading/position',position)['holding']
        blotter = c.call('trading/blotter',dict(context=shared(ctx),view=view))
        held = next(h for h in blotter['holdings'] if h['tradeId']==trade['tradeId'])
        pages = [c.call('trading/side-schedules',dict(position=position,sideSlot=s,contribution=None,offset=0,limit=8))['page'] for s in (0,1)]
        if reason:
            assert preview['code']==editor['code']==held['unavailableReason']==single['unavailableReason']==reason
            assert all(r['lifecycle']['unavailableReason']==reason for p in pages for r in p['rows']),pages
            assert held['presentValue'] is None and blotter['unvaluedPositions']>0
            assert next(h for h in blotter['holdings'] if h['tradeId']==companion['tradeId'])['presentValue'] is not None
            return
        editor = editor['valuation']
        check_value(editor['presentValue'],expected)
        for projection in (preview, single, held):
            assert projection['presentValue']==editor['presentValue']
            assert projection['swingRights']==editor['swingRights']
        right, = editor['swingRights']
        assert editor['schedule']['swingRights']==[right] and all(p['swingRights']==[right] for p in pages)
        assert sum(Decimal(s['presentValue']) for s in editor['sides'])+Decimal(right['counted'])==Decimal(editor['presentValue'])
        physical = pages[0]['rows']
        for item,row in zip(right['days'],physical,strict=True):
            assert row['quantityState']==item['state'] and row['quantity']==(item['selectedQuantity'] or '0')
        for row in (r for r in editor['schedule']['rows'] if r['level']=='day'):
            item = next(i for i in right['days'] if i['day']==row['key'])
            assert ('contingent' in row['cells']['gas.amount']['sources'])==(item['state']=='undecided')
        return right

    def check_value(actual,expected):
        if isinstance(expected,int):
            assert Decimal(actual)==expected,(actual,expected)
        else:
            assert abs(Decimal(actual)-expected)<Decimal('1e-8'),(actual,expected)

    def job(case,ctx,expected,*,reason=None,bumpsize='0.001',shift=None,after_submit=None,factor_day=DAYS[0]):
        trade = case[1]['tradeId']
        if ctx.get('contextId'):
            restored = risk.call('date-schedules/context',coordinates[ctx['contextId']])['context']
            assert restored['contextId']==ctx['contextId']
        scope = dict(context=shared(ctx),bookIds=[],portfolioIds=[],tradeIds=[trade,companion['tradeId']])
        cat = risk.call('portfolio/risk/catalog',scope)
        assert [(x['tradeId'],x['reason']) for x in cat['excludedPositions']]==([(trade,reason)] if reason else []),cat['excludedPositions']
        curve = 'synthetic_swing_overlap_location' if reason else case[0]['physicalSides'][0]['deliveryPoint'].replace('_point','_location')
        factor = next(f['factor'] for f in cat['factors'] if curve in f['curves'] and f['deliveryFrom']==('2021-01-01' if reason else factor_day))
        req = dict(scope,context={**shared(ctx),'contextId':cat['context']['contextId'],'knowledgeMode':'retained'},
                   detail='holdings',convention=cat['convention'],numericalPolicy=cat['numericalPolicy'],pricingPolicy=cat['pricingPolicy'],
                   stress=[],bumps=[dict(factor=factor,size=bumpsize,convention=cat['bumpConvention'])])
        if shift:
            req['bumps'] = []
            req['joint'] = dict(convention=cat['joint']['convention'],derivatives=None,scenarios=dict(id='swing-vol',version='1',
                convention=cat['joint']['scenarioConvention'],scenarios=[dict(id=label,label=label,shifts=[],volatility=[dict(
                inputId=shift,size=size,convention=cat['joint']['volatilityShiftConvention'])]) for label,size in [('up','0.01'),('down','-0.01')]]))
        synchronous = risk.call('portfolio/risk',req)
        job_id = risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=req))['id']
        if after_submit:
            after_submit()
        run_worker()
        result = risk.call('portfolio/risk/jobs/'+job_id)
        assert result['state']=='completed',result
        result = result['result']
        if shift:
            for field in ('kind','request','baseTotals','samples','axes','derivatives'):
                assert_replay('joint.'+field,result['joint'][field],synchronous['joint'][field])
            for queued,live in zip(result['joint']['partitions'],synchronous['joint']['partitions'],strict=True):
                # Base graph preparation work can differ between a warm HTTP process and a cold worker.
                counters = {'evaluatedNodes','reusedReads','affectedNodes','traversedAffectedEdges','evaluatedWork'}
                for partition in (queued,live):
                    work = partition['graphWork']
                    assert work.keys()==counters and all(isinstance(n,int) and n>=0 for n in work.values()),work
                for field in queued.keys() | live.keys():
                    if field!='graphWork':
                        assert_replay('partition.'+field,queued[field],live[field])
        else:
            assert_replay('factors',result['factors'],synchronous['factors'])
        if reason:
            assert result['excludedPositions']==cat['excludedPositions']
            included, = result['factors'][0]['positions']
            assert included['tradeId']==companion['tradeId'] and included['values']['base']=='300'
            return
        if shift:
            prices = []
            for sample,size in zip(result['joint']['samples'],('0.01','-0.01'),strict=True):
                assert sample['volatility']==[dict(inputId=shift,size=size,convention=cat['joint']['volatilityShiftConvention'])]
                position = next(p for p in sample['positions'] if p['tradeId']==trade)
                check_value(position['values']['base'],expected)
                base_right, = position['swingRights']['base']
                shifted_right, = position['swingRights']['value']
                assert base_right['pricing']==shifted_right['pricing'] and base_right['days']==shifted_right['days']
                for original,selected in zip(base_right['marks'],shifted_right['marks'],strict=True):
                    assert original['revisionId']==selected['revisionId']
                    difference = Decimal(selected['volatility'])-Decimal(original['volatility'])
                    assert difference==(Decimal(size) if original['revisionId']==shift else 0)
                prices.append(position['values']['value'])
            return prices
        factor_row, = result['factors']
        assert factor_row['bump']==bumpsize
        row = next(p for p in factor_row['positions'] if p['tradeId']==trade)
        check_value(row['values']['base'],expected)
        for lane in ('base','up','down'):
            assert sum(Decimal(e[lane]) for e in row['entries'])+Decimal(row['swingRights'][lane][0]['counted'])==Decimal(row['values'][lane])
        return row

    prior = pin(gain[1],context)
    routes(gain,prior,reason='exercise_marks')
    for case,prefix,value in [(gain,'synthetic_intrinsic',480),(loss,'synthetic_mandatory',-450)]:
        curve = prefix + '_location'
        mark(curve,'2021-01','0')
        routes(case,context,reason='exercise_marks')
        if prefix=='synthetic_intrinsic':
            job(case,context,None,reason='exercise_marks')
        mark(curve,'2021-02','0')
        right = routes(case,context,value)
        plan = ['900','700','1100'] if value==480 else ['1000','700','700']
        assert [d['intrinsicQuantity'] for d in right['days']]==plan
        assert right['committedQuantity']=='0' and right['intrinsic']==str(value)
        row = job(case,context,value)
        assert row['swingRights']['base']==[right]
        print(f'PASS exact swing {value} in preview, trade editor, position, schedule, Blotter and actual queued worker',flush=True)
    routes(gain,prior,reason='exercise_marks')
    job(gain,prior,None,reason='exercise_marks')

    def at(day):
        return {**context,'snapshotId':initial_frame(c.call('workspace'),day)['id']}

    def elect(case, index, quantity, effective=AT):
        trade = case[1]
        heads = read(trade,context)['heads']
        return c.call('gas/swing/record', dict(context=context,tradeId=trade['tradeId'],instrumentId=trade['instrumentId'],
            expectedRevision=1,right=0,operationId=str(uuid4()),expectedHeads=[{k:h[k] for k in ('day','revisionId')} for h in heads],
            elections=[dict(day=DAYS[index],quantity=quantity,exercisingParty='us',submittedAt=AT,effectiveAt=effective,
                            evidence='Synthetic accepted swing order')]))['elections'][0]['revisionId']

    initial = pin(gain[1],context)
    accepted = elect(gain,2,'1100')
    reserved = pin(gain[1],context)
    correction = elect(gain,2,'700','2021-01-01T08:00:00Z')
    right = routes(gain,context,480)
    assert right['committedLots']==11 and right['counted']=='40' and right['days'][2]['revisionId']==accepted
    assert job(gain,context,480)['values']['delta']=='900'
    future = at('2021-01-01')
    right = routes(gain,future,360)
    assert right['committedLots']==7 and right['days'][2]['revisionId']==correction
    assert job(gain,future,360)['values']['delta']=='1100'
    assert routes(gain,reserved,480)['days'][2]['revisionId']==accepted
    assert job(gain,reserved,480)['swingRights']['base'][0]['committedLots']==11
    assert routes(gain,initial,480)['committedLots']==0
    for bump in ('0.01','0.0001'):
        row = job(gain,initial,480,bumpsize=bump)
        check_value(row['values']['up'],Decimal(480)+900*Decimal(bump))
        check_value(row['values']['down'],Decimal(480)-900*Decimal(bump))
        assert row['values']['delta']=='900'

    expired = at('2021-01-31')
    routes(gain,expired,reason='swing_determination')
    job(gain,expired,None,reason='swing_determination')
    print('PASS swing 480 and mandatory -450 through preview, editor, position, schedule, Blotter and leased risk; early D3, both clocks, deltas 900/1100 and elapsed-trade isolation',flush=True)

    policies = risk.call('portfolio/pnl/policies')
    def endpoint(ctx):
        return dict(snapshotId=ctx['snapshotId'],convention='trading_workspace_v1',knowledgeMode='at')
    def pnl_values(trade, opening, closing, known_at, refusal=None):
        request = dict(opening=endpoint(opening),closing=endpoint(closing),knownAt=known_at,
            bookIds=[],portfolioIds=[],tradeIds=[trade['tradeId']],accountingPolicy=policies['accounting'],
            attributionPolicy=policies['attribution'],factorPolicy=policies['factors'],factorBump=policies['factorBump']['offered'],
            time=dict(mode='opening_to_closing_clock_v1',policy=policies['clockPolicy'],clock=None))
        result = risk.call('portfolio/pnl/run',request,timeout=300,expected=(200,422) if refusal else 200)
        if refusal:
            assert risk.last_status==422 and result.get('code')==refusal and 'report' not in result,(risk.last_status,result.get('code'),refusal)
            return
        return result['report']['totals'][0]['values']

    for case,opening,closing,movement in [(loss,'-450','-450','0'),(gain,'480','360','-120')]:
        pnl = pnl_values(case[1],context,future,read(case[1],future)['context']['knownAt'])
        assert (pnl['openingValue']['amount'],pnl['closingValue']['amount'],pnl['economicPnl']['amount'])==(opening,closing,movement),pnl
        assert pnl['cash']['amount']=='0' and not pnl['incomplete'],pnl
    print('PASS mandatory swing P&L endpoints -450/-450; early correction 480/360, zero cash and -120 P&L',flush=True)

    curve = 'synthetic_intrinsic_location'
    jan = mark(curve,'2021-01','0.33',1)
    feb = mark(curve,'2021-02','0.71',1)
    clock = datetime.fromisoformat('2021-01-15T08:00:00Z')
    workspace = c.call('workspace')
    frame, = [f for f in workspace['frames'] if f['id'] in workspace['initialFrameIds'] and datetime.fromisoformat(f['at'])==clock]
    stochastic_at = {**context,'snapshotId':frame['id']}
    dates = [datetime.fromisoformat(d+'T03:15:00Z') for d in DAYS]

    def oracle(january='0.33', january_forward='320', calculation_at=clock):
        with localcontext() as precision:
            precision.prec = 80
            inputs = [day(forward,-300,vol,int((deadline-calculation_at).total_seconds())) for deadline,forward,vol in
                      zip(dates,[january_forward,'280','340'],[january,'0.71','0.71'],strict=True)]
            return reference(fixture(inputs,7,11,24,27),21600)

    opening_clock = datetime.fromisoformat('2021-01-01T08:00:00Z')
    def pricing_pnl(cutoff, january):
        pnl = pnl_values(stochastic[1],future,stochastic_at,cutoff)
        start, end = oracle(january,calculation_at=opening_clock), oracle(january)
        for field,value in [('openingValue',start),('closingValue',end),('economicPnl',end-start)]:
            check_value(pnl[field]['amount'],value)
        assert pnl['cash']['amount']=='0' and not pnl['incomplete'],pnl
        return pnl

    missing_cutoff = coordinates[prior['contextId']]['knownAt']
    pnl_values(stochastic[1],future,stochastic_at,missing_cutoff,'exercise_marks')
    expected = oracle()
    right = routes(stochastic,stochastic_at,expected)
    assert [m['revisionId'] for m in right['marks']]==[jan['markId'],feb['markId']]
    assert [m['volatility'] for m in right['marks']]==['0.33','0.71']
    assert [d['forward'] for d in right['days']]==['3.2','2.8','3.4']
    assert right['pricing']['numericalPolicy']=='swing_trinomial_6h_half_even_18_v1' and right['work']['timeSteps']>0
    row = job(stochastic,stochastic_at,expected)
    for lane,fwd in [('up','320.1'),('down','319.9')]:
        check_value(row['values'][lane],oracle(january_forward=fwd))
    half = job(stochastic,stochastic_at,expected,bumpsize='0.0005')
    for lane,fwd in [('up','320.05'),('down','319.95')]:
        check_value(half['values'][lane],oracle(january_forward=fwd))
    assert abs(Decimal(half['values']['delta'])-Decimal(row['values']['delta']))<Decimal('0.00000001')
    historical = pin(stochastic[1],stochastic_at)
    historical_cutoff = coordinates[historical['contextId']]['knownAt']
    original_pnl = pricing_pnl(historical_cutoff,'0.33')
    up,down = job(stochastic,historical,expected,shift=jan['markId'],after_submit=lambda:mark(curve,'2021-01','0.50',2))
    check_value(up,oracle('0.34'))
    check_value(down,oracle('0.32'))
    # Increasing January volatility alone narrows its difference from February in this shared-budget fixture.
    assert Decimal(up)<expected<Decimal(down)
    assert routes(stochastic,historical,expected)==right
    routes(stochastic,stochastic_at,oracle('0.50'))
    current_cutoff = read(stochastic[1],stochastic_at)['context']['knownAt']
    current_pnl = pricing_pnl(current_cutoff,'0.50')
    assert pricing_pnl(historical_cutoff,'0.33')==original_pnl
    pnl_values(stochastic[1],future,stochastic_at,missing_cutoff,'exercise_marks')
    print(f"PASS P&L missing cutoff {missing_cutoff} refuses, unequal marks at {historical_cutoff}: "
          f"{original_pnl['openingValue']['amount']}/{original_pnl['closingValue']['amount']}, January revision at {current_cutoff}: "
          f"{current_pnl['openingValue']['amount']}/{current_pnl['closingValue']['amount']}, old cutoff replays",flush=True)
    print(f"PASS unequal monthly marks, PV {row['values']['base']}, point up/down {row['values']['up']}/{row['values']['down']}, "
          f"volatility up/down {up}/{down}, pinned worker replay, finite-grid delta {row['values']['delta']}",flush=True)

    one = deepcopy(template)
    one.update(bookId=book,operationId=str(uuid4()))
    one['physicalSides'][0].update(deliveryPoint='synthetic_swing_call_point',quantity='10000',
        deliveryFrom='2021-01-18',deliveryTo='2021-01-19')
    one['swingRights'][0].update(increment='10000',dailyMinimum='0',dailyMaximum='10000',
        totalMinimum='0',totalMaximum='10000',leadHours=8)
    single = one,c.call('gas/composition/book',one)
    mark('synthetic_swing_call_location','2021-01','0.5')
    with localcontext() as precision:
        precision.prec = 80
        expected = reference(fixture([day(32000,-30000,'0.5',30*86400)]),21600)
    right = routes(single,context,expected)
    assert right['intrinsic']=='2000' and right['work']['timeSteps']==120
    job(single,context,expected,factor_day='2021-01-01')
    print(f'PASS one-day 30-day-expiry swing through all routes and worker; independent 80-digit oracle {expected:.12f}',flush=True)


def reported_cash_checks(c, query, manifest, context, trade):
    from check_booked_side_programs import runtime_query
    from check_gas_imbalance import browser_authority
    from preview_fixture import CURRENCY

    browser_authority(query,manifest)
    cut = {**context,'snapshotId':initial_frame(c.call('workspace'),'2021-01-31')['id']}
    ids = {key:trade[key] for key in ('tradeId','instrumentId')}
    days = ['2021-01-13','2021-01-14','2021-01-15']

    def elections(amounts):
        heads = c.call('gas/swing/read',dict(context=context,**ids))['heads']
        return c.call('gas/swing/record',dict(context=context,**ids,operationId=str(uuid4()),expectedRevision=1,right=0,
            expectedHeads=[{k:h[k] for k in ('day','revisionId')} for h in heads],
            elections=[dict(day=day,quantity=amount,exercisingParty='us',submittedAt=AT,effectiveAt=AT,
                evidence='Synthetic swing decision') for day,amount in zip(days,amounts)]))

    def write(kind,day,details):
        proposal = dict(context=cut,tradeId=ids['tradeId'],expectedRevision=1,kind=kind,day=day,details=details)
        preview = c.call('gas/operations/preview',proposal)
        return c.call('gas/operations/'+('configure' if kind=='service' else 'record'),
            dict(preview=proposal,operationId=str(uuid4()),previewHash=preview['previewHash']))

    elections(['1000','1000','1000'])
    obligation = c.call('gas/quantities/read',dict(context=cut,tradeId=ids['tradeId'],selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    account = write('service',days[0],dict(obligationId=obligation,holderRole='legal_entity',
        externalReference='synthetic-swing-cashout',expectedPreviousId=None,validFrom=days[0],validTo='2021-01-16',
        active=True,statementPolicy='final_statement_balance_v1',termsReference='Synthetic account',evidence='Synthetic service'))
    state = c.call('gas/operations/read',dict(context=cut,tradeId=ids['tradeId'],day=days[0]))['state']
    version = next(a['version_id'] for a in state['accounts'] if a['id']==account['accountId'])
    for day,balance in zip(days,['300','-200']):
        write('statement',day,dict(accountId=account['accountId'],serviceVersionId=version,externalReference='synthetic-'+day,
            expectedPreviousId=None,balance=balance,status='final',reportedAt='2021-01-16T00:00:00Z',
            includedMovementId=None,evidence='Synthetic signed imbalance'))
    settlement = dict(operationId=str(uuid4()),tradeId=ids['tradeId'],expectedRevision=1,sourceSlot=0,month='2021-01-01',
        paidAt='2021-01-17T00:00:00Z',currency=CURRENCY,amount='300',toUs=[dict(occurrence=0,amount='500')],
        byUs=[dict(occurrence=1,amount='-200')])
    wire = json.dumps(settlement).replace("'", "''")
    runtime_query(query,manifest,f"SELECT security.record_gas_cash_settlement('{manifest['catalogId']}','{wire}'::jsonb,'2021-01-31T08:00:00Z');")

    def value():
        v = c.call('gas/composition/editor',dict(context=cut,tradeId=ids['tradeId']))['valuation']
        assert v['incomplete'] and v['unassessedDays']==1
        assert [s['presentValue'] for s in v['sides'][2:]]==['490','-420','-2']
        rows = { (r['entry'],r['day']):r for r in v['imbalanceRows'] }
        for key,amount,paid in [(('balance.to_us',days[0]),'990','500'),(('balance.by_us',days[1]),'-620','-200')]:
            assert (rows[key]['amount'],rows[key]['paid'])==(amount,paid)
        assert rows['balance.to_us',days[2]]['amount'] is None
        return v

    original = value()
    elections(['1100'])
    corrected = value()
    assert corrected['imbalanceRows']==original['imbalanceRows'] and corrected['sides'][4]==original['sides'][4]
    assert Decimal(corrected['presentValue'])-Decimal(original['presentValue'])==-300
    print('PASS same-source swing correction retains statement amounts 990/-620, payments 500/-200, one unassessed day and independent premium -2',flush=True)
