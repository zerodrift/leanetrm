"""Joint swing value through the retained trading and risk routes."""
import json
from copy import deepcopy
from datetime import datetime
from decimal import Decimal, localcontext
from difflib import unified_diff
from fractions import Fraction
from itertools import islice
from uuid import uuid4

from gas_swing_reference import day, fixture, reference
from preview_client import PreviewClient, initial_frame

DAYS = ['2021-01-31', '2021-02-01', '2021-02-02']
AT = '2020-12-18T16:00:00Z'


def assert_replay(field, queued, live):
    if queued!=live:
        def lines(value):
            return json.dumps(value,indent=2,sort_keys=True).splitlines()
        diff = unified_diff(lines(live),lines(queued),fromfile='synchronous',tofile='queued',n=1)
        raise AssertionError(field+' replay mismatch\n'+'\n'.join(islice(diff,10)))


def pnl_report(risk, policies, trade, opening, closing, known_at, refusal=None):
    def endpoint(ctx):
        return dict(snapshotId=ctx['snapshotId'],convention='trading_workspace_v1',knowledgeMode='at')
    request = dict(opening=endpoint(opening),closing=endpoint(closing),knownAt=known_at,
        bookIds=[],portfolioIds=[],tradeIds=[trade['tradeId']],accountingPolicy=policies['accounting'],
        attributionPolicy=policies['attribution'],factorPolicy=policies['factors'],factorBump=policies['factorBump']['offered'],
        time=dict(mode='opening_to_closing_clock_v1',policy=policies['clockPolicy'],clock=None))
    result = risk.call('portfolio/pnl/run',request,timeout=300,expected=(200,422) if refusal else 200)
    if refusal:
        assert risk.last_status==422 and result.get('code')==refusal and 'report' not in result,(risk.last_status,result.get('code'),refusal)
        return
    return result['report']


def pnl_values(risk, policies, trade, opening, closing, known_at, refusal=None):
    report = pnl_report(risk, policies, trade, opening, closing, known_at, refusal)
    if report is not None:
        return report['totals'][0]['values']


def book_coverage_case(c, context, body):
    """A missed Jan 30 blocks Jan 31, while Feb 1 has its own monthly budget."""
    body = deepcopy(body)
    body['operationId'] = str(uuid4())
    body['physicalSides'][0].update(deliveryFrom='2021-01-30',deliveryTo='2021-02-02',deliveryPoint='synthetic_swing_long_point')
    body['swingRights'][0].update(leadHours=0,limitPeriod='delivery_month',totalBasis='per_day_v1',
        totalMinimum=None,totalMaximum='1100')
    trade = c.call('gas/composition/book',body)
    ids = {k:trade[k] for k in ('tradeId','instrumentId')}
    for quantity,evidence in [('1000','Synthetic Jan 30 election'),(None,'Synthetic Jan 30 withdrawal')]:
        heads = c.call('gas/swing/read',dict(context=context,**ids))['heads']
        decision = dict(day='2021-01-30',quantity=quantity,exercisingParty='us',submittedAt=AT,effectiveAt=AT,evidence=evidence)
        outcome = c.call('gas/swing/record',dict(context=context,**ids,operationId=str(uuid4()),expectedRevision=1,right=0,
            expectedHeads=[{k:h[k] for k in ('day','revisionId')} for h in heads],elections=[decision]))
    cut = dict(context,snapshotId=initial_frame(c.call('workspace'),'2021-01-31')['id'])
    position = dict(tradeId=trade['tradeId'],bindingId=trade['instrumentId'],revision=1)
    reference = c.call('trading/position',dict(context=dict(cut,convention='trading_workspace_v1'),**position,instrumentType='physical_gas'))['holding']['tradeReference']
    return dict(**position,tradeReference=reference,withdrawal=outcome['elections'][0]['revisionId']),cut


def check_coverage_ticket(c, fixture, context, *, priced):
    selection = {k:fixture[k] for k in ('tradeId','bindingId','revision')}
    ticket = c.call('tickets/trade',dict(context=context,**selection))
    days = ticket['actions']['recordSwing']['days']
    expected = [('2021-01-30','missing_election'),('2021-01-31','period_unassessed'),('2021-02-01','undecided')]
    assert [(d['day'],d.get('state')) for d in days]==expected,[(d['day'],d.get('state')) for d in days]
    assert days[0]['effectiveRevision']==days[0]['head']==fixture['withdrawal'] and days[0]['evidence']=='Synthetic Jan 30 withdrawal'
    assert all(d['electedQuantity'] is None for d in days)
    assert [(p['missingDays'],p['blockedDays']) for p in ticket['actions']['recordSwing']['periods']]==[(1,1),(0,0)]
    if priced:
        assert ticket['scheduleRefusal'] is None
        right,=ticket['swingRights']
        assert [(d['day'],d['state']) for d in right['days']]==expected
        assert right['days'][0]['revisionId']==fixture['withdrawal'] and right['days'][0]['commercialEvidence']==days[0]['evidence']
    else:
        assert ticket['scheduleRefusal']['code']=='exercise_marks' and not ticket['swingRights'] and ticket['schedule'] is None
    print(f"PASS {'priced' if priced else 'unpriced'} booked swing has a withdrawn missed day, its blocked January peer and an open February day",flush=True)


def paid_coverage_view(c, context, trade, cutoff, *, withdrawn):
    """The single January payment follows the elected Jan 2 amount through both clocks."""
    workspace=c.call('workspace')
    opening,closing=[dict(context,snapshotId=initial_frame(workspace,day)['id']) for day in ('2021-01-01','2021-01-31')]
    missing=31 if withdrawn else 30
    values=('0','0','-1') if withdrawn else ('100','-2999','-3100')
    risk=PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    for ctx,day,subtotal in zip((opening,closing),('2021-01-01','2021-01-31'),values):
        selected=c.call('date-schedules/context',dict(effectiveOn=day,knownAt=cutoff,mode='at'))['context']
        held=dict(ctx,knowledgeMode='retained',contextId=selected['contextId'])
        ticket=c.call('tickets/trade',dict(context=held,tradeId=trade['tradeId'],bindingId=trade['instrumentId'],revision=1))
        editor=c.call('gas/composition/editor',dict(context=held,tradeId=trade['tradeId']))['valuation']
        assert editor['presentValue']==subtotal,(day,editor['presentValue'],subtotal)
        right,=ticket['swingRights']
        for report in (ticket,editor,editor['schedule']):
            assert report['incomplete'] and report['unassessedDays']==missing,(day,report['unassessedDays'])
        assert [s['unassessedDays'] for s in editor['sides']]==[missing,missing],(day,'entry missing counts',editor['sides'][0]['unassessedDays'],editor['sides'][1]['unassessedDays'],missing)
        assert right['counted'] is None and right['comparison'] is None and right['unassessedDays']==missing
        position=dict(context=dict(held,convention='trading_workspace_v1'),tradeId=trade['tradeId'],bindingId=trade['instrumentId'],
                      revision=1,instrumentType='physical_gas')
        row,=c.call('trading/side-schedules',dict(position=position,sideSlot=1,contribution=None,offset=0,limit=1))['page']['rows']
        if day=='2021-01-31':
            assert (row['accrued'],row['paid'],row['outstanding'])==((None,'-1',None) if withdrawn else ('-3000','-1','-2999')),row
            assert row['lifecycle']['cash']==('UnassessedPaid' if withdrawn else 'PartPaid'),row
        reasons=set(row['lifecycle']['unavailableReason'].split(','))
        assert reasons==({'missing_election','period_unassessed'} if day=='2021-01-01' else {'missing_election'}),row
        if day=='2021-01-01' and not withdrawn:
            assert row['quantityAdjustment']['ratio']=='1/31' and row['remaining']=='-3000',row
            flow,=ticket['measures']['expectedCash']
            assert (flow['amount'],flow['unassessedDays'])==('-3000',missing),flow
    result=pnl_values(risk,risk.call('portfolio/pnl/policies'),trade,opening,closing,cutoff)
    assert (result['openingUnassessedDays'],result['closingUnassessedDays'],result['incomplete'])==(missing,missing,True),result
    assert tuple(result[k]['amount'] for k in ('openingValue','closingValue','economicPnl'))==values,result
    assert result['cash']['amount']=='-1',result
    return result


def check_graph_work(work):
    counters = {'evaluatedNodes','reusedReads','affectedNodes','traversedAffectedEdges','evaluatedWork'}
    assert work.keys()==counters and all(type(n) is int and n>=0 for n in work.values()),work


def check_swing_display_lanes(position, lanes):
    for lane in lanes:
        right, = position['swingRights'][lane]
        assert right['counted'] is None or isinstance(right['counted'], str), lane
        assert type(right['unassessedDays']) is int and right['unassessedDays'] >= 0, lane
        assert type(right['work']['operations']) is int and right['work']['operations'] >= 0, lane


def check_reserved_lanes(row, amounts, revision):
    for lane, amount in amounts.items():
        assert Fraction(row['values'][lane])==Fraction(amount),(lane,row['values'],amount)
        day = row['swingRights'][lane][0]['days'][2]
        assert day['revisionId']==revision,(lane,day,revision)


def check_portfolio_groups(trades, groups):
    # This gas book has a single instrument type and currency. Its money pivot partitions by portfolio and measure.
    expected = {}
    for holding in trades:
        expected.setdefault((holding['portfolioId'], holding['measure']), []).append(holding)
    keys = [(group['portfolioId'], group['measure']) for group in groups]
    assert expected and len(keys)==len(expected) and set(keys)==expected.keys(), (keys, list(expected))
    for key, group in zip(keys, groups):
        holdings = expected[key]
        assert group['tradeCount']==len({h['tradeId'] for h in holdings}), group
        assert group.get('unassessedDays',0)==sum(h['unassessedDays'] for h in holdings), group
        assessed = [Fraction(h['presentValue']) for h in holdings if h['presentValue'] is not None]
        subtotal = group['presentValue']
        assert (Fraction(subtotal) if subtotal is not None else None)==(sum(assessed) if assessed else None), group


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
    print('PASS sealed monthly-only fixture and ordinary companion evaluated under the strict point rule: one row-local refusal and valued subtotal 300 on Blotter and risk',flush=True)


def route_checks(c, query, manifest, context, template, run_worker, book, cashout):
    risk = PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    writer = PreviewClient(c.url)
    writer.bootstrap()
    writer.call('profile',dict(profile='editor'))
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
        if not ctx.get('contextId'):
            ctx=pin(trade,ctx)
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
        pages = [c.call('trading/side-schedules',dict(position=position,sideSlot=s,contribution=None,offset=0,limit=8))['page'] for s in range(len(body['physicalSides'])+len(body['cashSides']))]
        if reason:
            assert preview['code']==editor['code']==held['unavailableReason']==single['unavailableReason']==reason
            assert all(r['lifecycle']['unavailableReason']==reason for p in pages for r in p['rows']),pages
            assert held['presentValue'] is None and blotter['unvaluedPositions']>0
            assert next(h for h in blotter['holdings'] if h['tradeId']==companion['tradeId'])['presentValue'] is not None
            return
        editor = editor['valuation']
        ticket = c.call('tickets/trade',dict(context=ctx,tradeId=trade['tradeId'],bindingId=trade['instrumentId'],revision=1))
        for result in (preview,single,held,ticket,editor['schedule']):
            assert (result['incomplete'],result['unassessedDays'])==(editor['incomplete'],editor['unassessedDays']),result.keys()
        assert ticket['swingRights']==editor['swingRights']

        check_value(editor['presentValue'],expected)
        for projection in (preview, single, held):
            assert projection['presentValue']==editor['presentValue']
            assert_replay('route swing evidence',projection['swingRights'],editor['swingRights'])
        right, = editor['swingRights']
        if right['modeledDays']:
            assert right['knownAt'] and right['forwardInputs']
            assert all(source['market']['contract']['id'] and source['observations'] for source in right['forwardInputs'])
        assert editor['schedule']['swingRights']==[right]
        assert all(page.get('swingRights',[])==([right] if slot in right['slots'] else []) for slot,page in enumerate(pages))
        assert datetime.fromisoformat(right['knownAt'])==datetime.fromisoformat(coordinates[ctx['contextId']]['knownAt'])
        assert sum(Fraction(s['presentValue']) for s in editor['sides'])+Fraction(right['counted'] or '0')==Fraction(editor['presentValue'])
        physical = pages[0]['rows']
        for item,row in zip(right['days'],physical,strict=True):
            state=item['state']
            assert (row['quantityState'],row['quantity'])==(state,item['selectedQuantity']),(row['quantityState'],row['quantity'],item)
        for row in (r for r in editor['schedule']['rows'] if r['level']=='day'):
            item = next((i for i in right['days'] if i['day']==row['key']),None)
            if item is None: continue  # An independent Side can cover other gas days.
            assert ('contingent' in row['cells']['gas.amount']['sources'])==(item['state']=='undecided')
        return right

    def check_value(actual,expected):
        if isinstance(expected,(int,Fraction)):
            assert Fraction(actual)==expected,(actual,expected)
        else:
            assert abs(Decimal(actual)-expected)<Decimal('1e-8'),(actual,expected)

    def job(case,ctx,expected,*,reason=None,bumpsize='0.001',shift=None,after_submit=None,factor_day=DAYS[0],peer=None,cashout_report=None):
        trade = case[1]['tradeId']
        if ctx.get('contextId'):
            restored = risk.call('date-schedules/context',coordinates[ctx['contextId']])['context']
            assert restored['contextId']==ctx['contextId']
        scope = dict(context=shared(ctx),bookIds=[],portfolioIds=[],tradeIds=[trade,companion['tradeId']]+([peer[0]['tradeId']] if peer else [])+([cashout['tradeId']] if cashout_report else []))
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
        if after_submit:
            rerun_id=risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=req))['id']
            assert rerun_id!=job_id
            run_worker()
            rerun=risk.call('portfolio/risk/jobs/'+rerun_id)
            assert rerun['state']=='completed',rerun
            for field in ('snapshotId','scenario','modelContracts','inputs','joint'):
                assert_replay('retained worker '+field,rerun['result'][field],result[field])
            print(f"PASS a second leased job retains original snapshot {result['snapshotId']}, valuation identities, monthly marks and full joint scenario results",flush=True)
        for field in ('snapshotId','scenario','modelContracts','inputs','unassessedDays','incomplete'):
            assert_replay(field,result[field],synchronous[field])
        for queued,live in zip(result['connectedScenarios'],synchronous['connectedScenarios'],strict=True):
            for field in (queued.keys() | live.keys())-{'work'}:
                assert_replay('market scenario '+field,queued[field],live[field])
            for work in (queued['work'],live['work']):
                check_graph_work(work)
            for field in queued['work'].keys()-{'evaluatedNodes'}:
                assert_replay('market graph '+field,queued['work'][field],live['work'][field])
        if shift:
            for field in ('kind','request','baseTotals','samples','axes','derivatives'):
                assert_replay('joint.'+field,result['joint'][field],synchronous['joint'][field])
            for queued,live in zip(result['joint']['partitions'],synchronous['joint']['partitions'],strict=True):
                # Base graph preparation work can differ between a warm HTTP process and a cold worker.
                for partition in (queued,live):
                    work = partition['graphWork']
                    check_graph_work(work)
                    for name in work.keys() - {'evaluatedNodes'}:
                        assert work[name]==0,(name,work)
                print(f"PASS swing graph preparation evaluated nodes: worker {queued['graphWork']['evaluatedNodes']}, "
                      f"HTTP {live['graphWork']['evaluatedNodes']}; reused reads, affected nodes, traversed edges and evaluated work all zero", flush=True)
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
                check_swing_display_lanes(position,('base','value'))
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
        check_swing_display_lanes(row,('base','up','down'))
        check_value(row['values']['base'],expected)
        if peer:
            other=next(p for p in factor_row['positions'] if p['tradeId']==peer[0]['tradeId'])
            assert other['bindingId']==peer[0]['instrumentId']!=row['bindingId']
            check_reserved_lanes(other,peer[1],peer[2])
        for lane in ('base','up','down'):
            assert sum(Fraction(e[lane]) for e in row['entries'])+Fraction(row['swingRights'][lane][0]['counted'] or '0')==Fraction(row['values'][lane])
        assert row['unassessedDays']==row['swingRights']['base'][0]['unassessedDays']
        if cashout_report:
            other=next(p for p in factor_row['positions'] if p['tradeId']==cashout['tradeId'])
            assert other['values']['base']==cashout_report['presentValue']
            assert other['unassessedDays']==cashout_report['unassessedDays']==1
            assert [e['unassessedDays'] for e in other['entries']]==[1,1,1,1,0]
            reasons={(e['sourceSlot'],e['day'],e['owner'],e['reason']) for entry in other['entries'] for e in entry['missingAssessments']}
            statement=next(r for r in cashout_report['imbalanceRows'] if r['day']=='2021-01-15' and r['entry']=='balance.to_us')
            assert {(slot,day,reason) for slot,day,owner,reason in reasons}=={
                (0,'2021-01-15','missing_election'),(0,'2021-01-15',statement['reason'])},reasons
            assert len({owner for _,_,owner,_ in reasons})==2
            summary=risk.call('portfolio/risk',dict(req,detail='books'))
            book_factor,=summary['factors']
            assert book_factor['positions']==[] and summary['unassessedDays']==3
            assert_replay('book-only totals',book_factor['totals'],factor_row['totals'])
            for total in book_factor['totals']:
                members=[p for p in factor_row['positions'] if (p['bookId'],p['measure'])==(total['bookId'],total['measure'])]
                assert total['unassessedDays']==sum(p['unassessedDays'] for p in members)
                for lane in ('base','up','down'):
                    assert Fraction(total['values'][lane])==sum(Fraction(p['values'][lane]) for p in members)
            print('PASS mixed book: two missing right days plus one shared source/day with separate swing and statement reasons, exact book-only and worker totals',flush=True)
        assert result['unassessedDays']==row['unassessedDays']+(1 if cashout_report else 0)
        assert result['incomplete']==row['incomplete']==bool(row['unassessedDays'])
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
        priced=pin(case[1],context)
        right = routes(case,priced,value)
        plan = ['900','700','1100'] if value==480 else ['1000','700','700']
        assert [d['plannedQuantity'] for d in right['days']]==plan
        assert right['periods'][0]['elected']=='0' and right['comparison']['amount']==str(value)
        row = job(case,priced,value)
        assert row['swingRights']['base']==[right]
        print(f'PASS exact swing {value} in preview, trade editor, position, schedule, Blotter and actual queued worker',flush=True)
    routes(gain,prior,reason='exercise_marks')
    job(gain,prior,None,reason='exercise_marks')

    def at(day):
        return {**context,'snapshotId':initial_frame(c.call('workspace'),day)['id']}

    def elect(trade, index, quantity, effective=AT):
        heads = read(trade,context)['heads']
        return c.call('gas/swing/record', dict(context=context,tradeId=trade['tradeId'],instrumentId=trade['instrumentId'],
            expectedRevision=1,right=0,operationId=str(uuid4()),expectedHeads=[{k:h[k] for k in ('day','revisionId')} for h in heads],
            elections=[dict(day=heads[index]['day'],quantity=quantity,exercisingParty='us',submittedAt=AT,effectiveAt=effective,
                            evidence='Synthetic accepted swing order')]))['elections'][0]['revisionId']

    # The two identical contracts reserve different off-grid quantities. Price them together.
    left,right_pair=[book_strip('synthetic_intrinsic') for _ in range(2)]
    left_fact=elect(left[1],2,'775')
    right_fact=elect(right_pair[1],2,'1025')
    both=pin(left[1],context)
    left_report=routes(left,both,390)
    right_report=routes(right_pair,both,450)
    assert left_report['days'][2]['revisionId']==left_fact and right_report['days'][2]['revisionId']==right_fact
    # At the January bump the optimal January quantities are 1100 and 900 respectively.
    # The three-day fixed payment and the elected February delivery do not move.
    pair_result=job(left,both,390,peer=(right_pair[1],dict(base='450',up='450.9',down='449.1'),right_fact))
    check_reserved_lanes(pair_result,dict(base='390',up='391.1',down='388.9'),left_fact)
    assert pair_result['swingRights']['base']==[left_report]
    print('PASS identical contracts reserve 775 and 1025 on different bindings; base/up/down reconcile to 390/391.1/388.9 and 450/450.9/449.1',flush=True)

    # The independent period-total Side has one third of its volume left on Jan 31.
    for scale in (1,10):
        body=deepcopy(gain[0])
        source=body['physicalSides'][0]
        source['quantity']=str(3*scale)
        residual=dict(source,code='residual',quantity=str(scale),quantityBasis='period_total_equal_days',
            deliveryPoint='synthetic_swing_overlap_point',deliveryFrom='2021-01-28',deliveryTo='2021-01-31')
        body['physicalSides'].append(residual)
        body['swingRights'][0].update(dailyMinimum=str(2*scale),dailyMaximum=str(4*scale),planningStep=str(scale),
                                     totalMinimum=None,totalMaximum=None,totalBasis=None)
        body['operationId']=str(uuid4())
        parcel=body,c.call('gas/composition/book',body)
        elect(parcel[1],0,str(Decimal('2.5')*scale))
        held=pin(parcel[1],at('2021-01-31'))
        expected=Fraction(41*scale,15)
        value=routes(parcel,held,expected)
        row=job(parcel,held,expected)
        cutoff=coordinates[held['contextId']]['knownAt']
        pnl=pnl_values(risk,risk.call('portfolio/pnl/policies'),parcel[1],context,held,cutoff)
        assert Fraction(pnl['openingValue']['amount'])==Fraction(24*scale,5) and Fraction(pnl['closingValue']['amount'])==expected,pnl
        assert Fraction(pnl['economicPnl']['amount'])==expected-Fraction(24*scale,5) and pnl['cash']['amount']=='0',pnl
        assert row['values']['base']==str(expected) and row['entries'][1]['base']==str(Fraction(31*scale,30)),row
        assert Fraction(value['days'][0]['electedQuantity'])==Fraction(5*scale,2)
        print(f"PASS B={3*scale}, elected five sixths; exact independent remaining Side {row['entries'][1]['base']}, trade {expected}",flush=True)

    previously_elected=elect(loss[1],0,'1000')
    previous=pin(loss[1],context)
    withdrawn=elect(loss[1],0,None)
    selected=pin(loss[1],context)
    report=routes(loss,selected,-450)
    withdrawal_day=report['days'][0]
    assert (withdrawal_day['revisionId'],withdrawal_day['commercialEvidence'],withdrawal_day['electedQuantity'],withdrawal_day['selectedQuantity'],withdrawal_day['state'])==(
        withdrawn,'Synthetic accepted swing order',None,None,'undecided'),withdrawal_day
    assert report['days'][1]['revisionId'] is None and report['days'][1]['commercialEvidence'] is None
    assert routes(loss,previous,-450)['days'][0]['revisionId']==previously_elected
    for lane in job(loss,selected,-450)['swingRights'].values():
        assert lane[0]['days'][0]['revisionId']==withdrawn and lane[0]['days'][0]['commercialEvidence']==withdrawal_day['commercialEvidence']
    ticket=c.call('tickets/trade',dict(context=selected,tradeId=loss[1]['tradeId'],bindingId=loss[1]['instrumentId'],revision=1))
    assert ticket['swingRights'][0]['days'][0]==withdrawal_day
    assert ticket['actions']['recordSwing']['days'][0]['effectiveRevision']==withdrawn
    print('PASS selected commercial withdrawal and prior election on held preview, editor, schedules, positions, ticket and leased risk lanes',flush=True)

    initial = pin(gain[1],context)
    accepted = elect(gain[1],2,'1100')
    reserved = pin(gain[1],context)
    correction = elect(gain[1],2,'700','2021-01-01T08:00:00Z')
    right = routes(gain,context,480)
    assert right['periods'][0]['elected']=='1100' and right['counted']=='40' and right['days'][2]['revisionId']==accepted
    assert job(gain,context,480)['values']['delta']=='900'
    future = at('2021-01-01')
    right = routes(gain,future,360)
    assert right['periods'][0]['elected']=='700' and right['days'][2]['revisionId']==correction
    assert job(gain,future,360)['values']['delta']=='1100'
    assert routes(gain,reserved,480)['days'][2]['revisionId']==accepted
    assert job(gain,reserved,480)['swingRights']['base'][0]['periods'][0]['elected']=='1100'
    assert routes(gain,initial,480)['periods'][0]['elected']=='0'
    for bump in ('0.01','0.0001'):
        row = job(gain,initial,480,bumpsize=bump)
        check_value(row['values']['up'],Decimal(480)+900*Decimal(bump))
        check_value(row['values']['down'],Decimal(480)-900*Decimal(bump))
        assert row['values']['delta']=='900'

    switched=job(gain,initial,480,bumpsize='0.3')
    assert [d['plannedQuantity'] for d in switched['swingRights']['up'][0]['days']]==['1100','700','900']
    assert [d['plannedQuantity'] for d in switched['swingRights']['down'][0]['days']]==['700','700','1100']
    assert (switched['values']['up'],switched['values']['down'])==('770','230'),switched
    print('PASS 0.3 dated price shocks change the optimal plans and independently reconcile to 770 and 230',flush=True)

    expired = at('2021-01-31')
    unresolved=routes(gain,expired,280)
    assert unresolved['counted'] is None and unresolved['unassessedDays']==2
    elect(cashout,2,None)
    pinned_expired=pin(gain[1],expired)
    balance=c.call('gas/composition/editor',dict(context=pinned_expired,tradeId=cashout['tradeId']))['valuation']
    assert balance['unassessedDays']==1 and [s['unassessedDays'] for s in balance['sides']]==[1,1,1,1,0],balance['sides']
    assert [s['presentValue'] for s in balance['sides'][2:]]==['490','-420','-2']
    job(gain,pinned_expired,280,cashout_report=balance)
    trades=c.call('trading/blotter',dict(context=shared(pinned_expired),view=view))['holdings']
    pivot_view=dict(view,mode='pivot',groupBy=['portfolio'],measures=['presentValue','tradeCount'],
                    pivotConvention='trading_blotter_money_totals_v1')
    groups=c.call('trading/blotter',dict(context=shared(pinned_expired),view=pivot_view))['groups']
    check_portfolio_groups(trades,groups)
    job(gain,expired,280)
    print('PASS swing 480 and mandatory -450 through preview, editor, position, schedule, Blotter and leased risk; early D3, both clocks, deltas 900/1100 and elapsed-trade isolation',flush=True)

    policies = risk.call('portfolio/pnl/policies')
    clock = datetime.fromisoformat('2021-01-15T08:00:00Z')
    workspace = c.call('workspace')
    frame, = [f for f in workspace['frames'] if f['id'] in workspace['initialFrameIds'] and datetime.fromisoformat(f['at'])==clock]
    stochastic_at = {**context,'snapshotId':frame['id']}
    # At the earlier close the Jan 13 election and balance are both missing.
    # The later close includes the statement and its settled cash.
    elect(cashout,0,None)
    cutoff=read(cashout,expired)['context']['knownAt']
    report=pnl_report(risk,policies,cashout,stochastic_at,expired,cutoff)
    position,=report['positions']
    values=position['values']
    assert report['totals'][0]['values']==values and values['cash']['amount']=='300',values
    for endpoint,business_day,ctx in [('opening','2021-01-15',stochastic_at),('closing','2021-01-31',expired)]:
        held=c.call('date-schedules/context',dict(effectiveOn=business_day,knownAt=cutoff,mode='at'))['context']
        editor=c.call('gas/composition/editor',dict(context=dict(ctx,knowledgeMode='retained',contextId=held['contextId']),
            tradeId=cashout['tradeId']))['valuation']
        assert values[endpoint+'Value']['amount']==editor['presentValue']
        assert values[endpoint+'UnassessedDays']==editor['unassessedDays']
    assert Fraction(values['economicPnl']['amount'])==Fraction(values['closingValue']['amount'])-Fraction(values['openingValue']['amount'])+300
    def missing(endpoint):
        details=position[endpoint+'MissingAssessments']
        keys=[(d['sourceSlot'],d['day'],d['owner'],d['ownerOrdinal'],d['reason']) for d in details]
        assert len(keys)==len(set(keys)) and len({k[:2] for k in keys})==values[endpoint+'UnassessedDays'],keys
        return {k[2:] for k in keys if k[:2]==(0,'2021-01-13')}
    assert missing('opening')=={('swing_right',0,'missing_election'),('reported_balance',0,'statement_unavailable')},position
    assert missing('closing')=={('swing_right',0,'missing_election')},position
    assert not any(k.endswith('MissingAssessments') for k in report['totals'][0].keys()|values.keys())
    print('PASS authorized P&L position retains both missing Jan 13 owners at opening, the remaining swing reason at closing and 300 settled cash',flush=True)
    for case,opening,closing,movement in [(loss,'-450','-450','0'),(gain,'480','360','-120')]:
        pnl = pnl_values(risk,policies,case[1],context,future,read(case[1],future)['context']['knownAt'])
        assert (pnl['openingValue']['amount'],pnl['closingValue']['amount'],pnl['economicPnl']['amount'])==(opening,closing,movement),pnl
        assert pnl['cash']['amount']=='0' and not pnl['incomplete'],pnl
    print('PASS mandatory swing P&L endpoints -450/-450; early correction 480/360, zero cash and -120 P&L',flush=True)

    curve = 'synthetic_intrinsic_location'
    jan = mark(curve,'2021-01','0.33',1)
    feb = mark(curve,'2021-02','0.71',1)
    dates = [datetime.fromisoformat(d+'T03:15:00Z') for d in DAYS]

    def oracle(january='0.33', january_forward='320', calculation_at=clock):
        with localcontext() as precision:
            precision.prec = 80
            inputs = [day(forward,-300,vol,int((deadline-calculation_at).total_seconds())) for deadline,forward,vol in
                      zip(dates,[january_forward,'280','340'],[january,'0.71','0.71'],strict=True)]
            return reference(fixture(inputs,7,11,24,27),21600)

    opening_clock = datetime.fromisoformat('2021-01-01T08:00:00Z')
    def pricing_pnl(cutoff, january):
        pnl = pnl_values(risk,policies,stochastic[1],future,stochastic_at,cutoff)
        start, end = oracle(january,calculation_at=opening_clock), oracle(january)
        for field,value in [('openingValue',start),('closingValue',end),('economicPnl',end-start)]:
            check_value(pnl[field]['amount'],value)
        assert pnl['cash']['amount']=='0' and not pnl['incomplete'],pnl
        return pnl

    missing_cutoff = coordinates[prior['contextId']]['knownAt']
    pnl_values(risk,policies,stochastic[1],future,stochastic_at,missing_cutoff,'exercise_marks')
    expected = oracle()
    historical = pin(stochastic[1],stochastic_at)
    right = routes(stochastic,historical,expected)
    assert [m['revisionId'] for m in right['marks']]==[jan['markId'],feb['markId']]
    assert [m['volatility'] for m in right['marks']]==['0.33','0.71']
    assert [d['forward'] for d in right['days']]==['3.2','2.8','3.4']
    assert right['pricing']['numericalPolicy']=='swing_trinomial_6h_unit_reference_v2' and right['work']['factorSteps']>0
    row = job(stochastic,stochastic_at,expected)
    for lane,fwd in [('up','320.1'),('down','319.9')]:
        check_value(row['values'][lane],oracle(january_forward=fwd))
    half = job(stochastic,stochastic_at,expected,bumpsize='0.0005')
    for lane,fwd in [('up','320.05'),('down','319.95')]:
        check_value(half['values'][lane],oracle(january_forward=fwd))
    assert abs(Decimal(half['values']['delta'])-Decimal(row['values']['delta']))<Decimal('0.00000001')
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
    pnl_values(risk,policies,stochastic[1],future,stochastic_at,missing_cutoff,'exercise_marks')
    print(f"PASS P&L missing cutoff {missing_cutoff} refuses, unequal marks at {historical_cutoff}: "
          f"{original_pnl['openingValue']['amount']}/{original_pnl['closingValue']['amount']}, January revision at {current_cutoff}: "
          f"{current_pnl['openingValue']['amount']}/{current_pnl['closingValue']['amount']}, old cutoff replays",flush=True)
    print(f"PASS unequal monthly marks, PV {row['values']['base']}, point up/down {row['values']['up']}/{row['values']['down']}, "
          f"volatility up/down {up}/{down}, pinned worker replay, finite-grid delta {row['values']['delta']}",flush=True)

    # Both admissible 1,000% marks drive far-tail prices beyond the finite lattice magnitude
    # over the December-to-February horizon, while the marked January cutoff still replays.
    mark(curve,'2021-01','10',3)
    mark(curve,'2021-02','10',2)
    magnitude=pin(stochastic[1],context)
    routes(stochastic,magnitude,reason='swing_numerical_scope')
    job(stochastic,magnitude,None,reason='swing_numerical_scope')
    assert routes(stochastic,historical,expected)==right
    pnl_values(risk,policies,stochastic[1],context,stochastic_at,coordinates[magnitude['contextId']]['knownAt'],'swing_numerical_scope')
    print('PASS two admitted 10.0 volatility marks exceed numerical magnitude on the open horizon; prior pinned prices remain available',flush=True)

    one = deepcopy(template)
    one.update(bookId=book,operationId=str(uuid4()))
    one['physicalSides'][0].update(deliveryPoint='synthetic_swing_call_point',quantity='10000',
        deliveryFrom='2021-01-18',deliveryTo='2021-01-19')
    one['swingRights'][0].update(planningStep='10000',dailyMinimum='0',dailyMaximum='10000',
        totalMinimum='0',totalMaximum='10000',leadHours=8)
    single = one,c.call('gas/composition/book',one)
    mark('synthetic_swing_call_location','2021-01','0.5')
    with localcontext() as precision:
        precision.prec = 80
        expected = reference(fixture([day(32000,-30000,'0.5',30*86400)]),21600)
    right = routes(single,context,expected)
    assert right['comparison']['amount']=='2000' and right['work']['factorSteps']==120
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

    def write(kind,day,details,*,selected=cut):
        proposal = dict(context=selected,tradeId=ids['tradeId'],expectedRevision=1,kind=kind,day=day,details=details)
        preview = c.call('gas/operations/preview',proposal)
        return c.call('gas/operations/'+('configure' if kind=='service' else 'record'),
            dict(preview=proposal,operationId=str(uuid4()),previewHash=preview['previewHash']))

    elections(['1000','1000','1000'])
    obligation = c.call('gas/quantities/read',dict(context=cut,tradeId=ids['tradeId'],selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    account = write('service',days[0],dict(obligationId=obligation,holderRole='legal_entity',
        externalReference='synthetic-swing-cashout',expectedPreviousId=None,validFrom=days[0],validTo='2021-01-16',
        active=True,statementPolicy='final_statement_balance_v1',termsReference='Synthetic account',evidence='Synthetic service'),selected=context)
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
