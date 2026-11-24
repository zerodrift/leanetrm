"""Exercise-term booking, independent native admission and retained group times."""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from fractions import Fraction
import json
import time
from uuid import uuid4
from check_gas_pricing import request, staged_booking
from preview_client import PreviewClient
from synthetic_side_calendars import side_calendar_policy


def run_checks(url,query,manifest,run_worker):
    c=PreviewClient(url)
    c.bootstrap()
    c.call('profile',dict(profile='trader'))
    context,base=request(c)
    base['physicalSides'][0].pop('quantityPolicy')
    monthly,daily=base['cashSides'][:2]
    base['cashSides']=[{**monthly,'part':None,'direction':'receive'},{**daily,'part':None}]
    right=dict(entries=['above','gas'],period='delivery_day',holder='us',settlement='physical',leadHours=6)

    def book(body):
        saved=c.call('gas/composition/book',body)
        assert c.call('gas/composition/book',body)==saved
        return saved

    def rows(saved,table,fields,order):
        result=query(f"SELECT coalesce(jsonb_agg(jsonb_build_array({fields}) ORDER BY {order}),'[]') FROM trading.gas_exercise_{table} WHERE instrument_id='{saved['instrumentId']}';")
        return json.loads(result)

    plain=book(base)
    assert rows(plain,'rights','ordinal','ordinal')==[]
    body={**base,'operationId':str(uuid4()),'exerciseRights':[right]}
    saved=book(body)
    assert rows(saved,'rights','ordinal,period,holder,settlement,model,day_count,time_rule,lead_hours','ordinal')==[
        [0,'delivery_day','us','physical','spread_call_kirk_lognormal_v1','act_365_fixed_instant_v1','lead_before_first_gas_day_v1',6]]
    assert rows(saved,'right_entries','ordinal,slot','entry_ordinal')==[[0,2],[0,0]]
    assert rows(saved,'groups','period_from,period_to,exercise_at','period_from')==[
        [f'2021-01-{n}',f'2021-01-{n+1}',f'2021-01-{n-1}T18:00:00+00:00'] for n in (13,14,15)]
    assert c.call('gas/composition/editor',dict(context=context,tradeId=saved['tradeId']),expected=422)['code']=='exercise_marks'
    assert c.call('gas/composition/preview',body,expected=422)['code']=='exercise_marks'

    cash_right=dict(entries=['base','above'],period='delivery_month',holder='counterparty',settlement='cash_automatic',leadHours=None)
    cash=book({**base,'operationId':str(uuid4()),'exerciseRights':[cash_right]})
    assert rows(cash,'groups','period_from,period_to,exercise_at','period_from')==[['2021-01-13','2021-01-16','2021-01-15T12:00:00+00:00']]
    assert rows(cash,'rights','time_rule,lead_hours','ordinal')==[[None,None]]

    # A later daily fixing on the second entry decides the truncated group on each side of month end.
    crossing=deepcopy(base)
    path=next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    crossing['physicalSides'][0].update({k:path[k] for k in ('pipeline','zone','deliveryPoint','product')},
                                        deliveryFrom='2021-01-31',deliveryTo='2021-02-03')
    crossing['operationId']=str(uuid4())
    crossing['exerciseRights']=[cash_right]
    cross=book(crossing)
    assert rows(cross,'groups','period_from,period_to,exercise_at','period_from')==[
        ['2021-01-31','2021-02-01','2021-01-31T12:00:00+00:00'],['2021-02-01','2021-02-03','2021-02-02T12:00:00+00:00']]

    # A contract-only payment can be controlled while another independent owner remains ordinary.
    outside=deepcopy(base)
    outside.update(operationId=str(uuid4()),exerciseRights=[cash_right])
    outside['physicalSides'].append({**outside['physicalSides'][0],'code':'ordinary','quantity':'9'})
    outside_saved=book(outside)
    assert c.call('gas/composition/editor',dict(context=context,tradeId=outside_saved['tradeId']),expected=422)['code']=='exercise_marks'

    for table in ('rights','right_entries','groups'):
        try:query(f"BEGIN;DELETE FROM trading.gas_exercise_{table} WHERE instrument_id='{saved['instrumentId']}';ROLLBACK;")
        except RuntimeError as error:assert 'Gas components are immutable' in str(error),error
        else:raise AssertionError('Exercise terms must be immutable')
    # Runtime reads require the parent Instrument to be visible under the active trading context.
    deny=query(f"SET SESSION AUTHORIZATION preview_runtime;SELECT trading.gas_exercise_terms('{manifest['catalogId']}','{saved['instrumentId']}')::text;")
    assert deny=='[]',deny

    def reject(original,rights):
        invalid={**original,'operationId':str(uuid4()),'exerciseRights':rights}
        assert c.call('gas/composition/book',invalid,expected=422)['code']=='gas_composition_input'
        staged_booking(query,manifest,original,invalid,'gas_composition_input')

    for rights in ([right,right],[{**right,'entries':['above']}],[{**cash_right,'leadHours':0}],
                   [{**right,'entries':['missing']}],[{**right,'entries':['gas','gas']}],
                   [{**cash_right,'entries':['gas','above']}],[{**right,'leadHours':None}],
                   [{**right,'period':'monthly'}],[{**right,'holder':'either'}],[{**right,'leadHours':2147483647}]):
        reject(base,rights)

    linked=deepcopy(base)
    linked['operationId']=str(uuid4())
    linked['physicalSides'].append({**linked['physicalSides'][0],'code':'linked','direction':'deliver','quantity':None,
                                   'link':dict(source='gas',formula='none',fraction='0')})
    book(linked)
    reject(linked,[right])
    linked_policy=deepcopy(linked)
    linked_policy.update(operationId=str(uuid4()),exerciseRights=[right])
    linked_policy['physicalSides'][0]['quantityPolicy']='delivery_schedule_v1'
    assert c.call('gas/composition/book',linked_policy,expected=422)['code']=='gas_composition_input'
    staged_booking(query,manifest,linked,linked_policy,'gas_composition_input')

    shorter=deepcopy(base)
    shorter['operationId']=str(uuid4())
    shorter['cashSides'][1]['deliveryFrom']='2021-01-14'
    book(shorter)
    reject(shorter,[right])

    fixed=deepcopy(base)
    fixed.update(operationId=str(uuid4()),profile='fixed_gas_composition_v1')
    calendar=fixed['cashSides'][0]['calendarPolicy']['payment']['calendar']
    fixed['cashSides']=[dict(code='fixed',role='consideration',direction='pay',source='gas',stage='delivery',share='1',
                             price='3',paymentAt='2021-02-10T00:00:00Z',tariffCode=None,
                             calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=False,payment=True))]
    book(fixed)
    reject(fixed,[{**cash_right,'entries':['fixed']}])
    election_checks(c,query,manifest,context,fixed,cash,plain,run_worker)
    curve_marks(c,query,manifest,context,fixed,base,plain,run_worker)
    explicit_empty={**fixed,'operationId':str(uuid4()),'exerciseRights':[]}
    original=book(explicit_empty)
    ticket=c.call('tickets/trade',dict(context=context,tradeId=original['tradeId'],bindingId=original['instrumentId'],revision=1))
    assert 'exerciseRights' not in ticket['draft'] and 'recordExercise' not in ticket['actions']
    assert c.call('tickets/book',dict(context=context,draft=ticket['draft'],definitionRevisionId=ticket['definitionRevisionId'],
                                     operationId=explicit_empty['operationId']))==original
    print('PASS no-right ticket replays the original explicit empty-array command',flush=True)
    print('PASS gas exercise: API and SQL admission, exact physical and latest fixing times, immutable terms, replay and unpriced valuation',flush=True)

def curve_marks(c,query,manifest,context,fixed,base,plain,run_worker):
    point='synthetic_exercise_location'
    other='synthetic_entry_daily'
    definition=fixed['definitionRevisionId']
    selected=dict(context=context,definitionRevisionId=definition)
    mark=dict(**selected,operationId=str(uuid4()),reason='Synthetic January volatility',curve=point,
              marks=[dict(month='2021-01',expectedRevision=0,volatility='0.5')])
    c.call('volatility-marks/record',mark,expected=403)
    call=deepcopy(fixed)
    path=next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_exercise_point')
    call['physicalSides'][0].update({k:path[k] for k in ('pipeline','zone','deliveryPoint','product')},
                                   deliveryFrom='2021-01-16',deliveryTo='2021-01-19')
    call.update(operationId=str(uuid4()),exerciseRights=[dict(entries=['gas','fixed'],period='delivery_day',holder='us',
                                                              settlement='physical',leadHours=8)])
    saved=c.call('gas/composition/book',call)
    def editor(ctx,expected=200):
        return c.call('gas/composition/editor',dict(context=ctx,tradeId=saved['tradeId']),expected=expected)
    assert editor(context,422)['code']=='exercise_marks'
    shared={**context,'convention':'trading_workspace_v1'}
    def holding(trade):
        return c.call('trading/position',dict(context=shared,tradeId=trade['tradeId'],bindingId=trade['instrumentId'],
                      revision=1,instrumentType='physical_gas'))['holding']
    risk=PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[plain['tradeId'],saved['tradeId']])
    excluded=risk.call('portfolio/risk/catalog',scope)['excludedPositions']
    assert [(r['tradeId'],r['reason']) for r in excluded]==[(saved['tradeId'],'exercise_marks')],excluded
    assert holding(saved)['unavailableReason']=='exercise_marks'
    assert holding(plain)['presentValue'] is not None
    pin=c.call('gas/quantities/read',dict(context=context,tradeId=saved['tradeId'],selectionPolicy='operational_projection_v1'))['context']
    prior={**context,'contextId':pin['contextId'],'knowledgeMode':'retained'}
    read=dict(**selected,curves=[other,point],months=['2021-01'])
    writer=PreviewClient(c.url)
    writer.bootstrap()
    writer.call('profile',dict(profile='editor'))
    first=writer.call('volatility-marks/record',mark)
    assert first['marks'][0]['revision']==1 and first['marks'][0]['changed']
    assert writer.call('volatility-marks/record',mark)==first
    assert editor(prior,422)['code']=='exercise_marks'
    historical=c.call('curve-marks/read',{**read,'context':prior})
    assert historical['volatilities']==historical['correlations']==[],historical
    value=Fraction(editor(context)['valuation']['presentValue'])
    assert Fraction(c.call('gas/composition/preview',call)['presentValue'])==value
    two=deepcopy(call)
    two['physicalSides'][0]['deliveryTo']='2021-01-18'
    third=value-Fraction(c.call('gas/composition/preview',two)['presentValue'])
    assert round(third*10000)==29474419,third
    assert Fraction(holding(saved)['presentValue'])==value
    priced={**scope,'tradeIds':[saved['tradeId']]}
    catalog=risk.call('portfolio/risk/catalog',priced)
    assert not catalog['excludedPositions']
    request={**priced,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
             'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
             'bumps':[dict(factor=f['factor'],size='0.1',convention=catalog['bumpConvention']) for f in catalog['factors'][:1]]}
    report=risk.call('portfolio/risk',request)
    assert Fraction(report['factors'][0]['positions'][0]['values']['base'])==value
    job=risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))

    raised={**mark,'operationId':str(uuid4()),'marks':[dict(month='2021-01',expectedRevision=1,volatility='0.6')]}
    assert writer.call('volatility-marks/record',raised)['marks'][0]['revision']==2
    run_worker()
    finished=risk.call('portfolio/risk/jobs/'+job['id'])
    assert finished['state']=='completed' and finished['result']['factors']==report['factors'],finished
    assert c.call('volatility-marks/record',mark,expected=403)
    assert writer.call('volatility-marks/record',{**raised,'operationId':mark['operationId']},expected=409)['code']=='curve_mark_request_conflict'
    assert writer.call('volatility-marks/record',{**mark,'operationId':str(uuid4()),'marks':[dict(month='2021-01',expectedRevision=0,volatility='0.7')]},expected=409)['code']=='curve_mark_stale'
    assert writer.call('volatility-marks/record',mark)==first
    assert writer.call('volatility-marks/record',{**mark,'operationId':str(uuid4())})['marks']==[{**first['marks'][0],'changed':False}]
    pair=dict(**selected,operationId=mark['operationId'],reason='Synthetic gas curve correlation',firstCurve=point,secondCurve=other,
              marks=[dict(month='2021-01',expectedRevision=0,correlation='0.7')])
    # A zero projected payment still selects its curve. Only PV knows it needs no volatility or correlation.
    def spread(spread_amount):
        spread=deepcopy(base)
        spread['physicalSides'][0].update({key:path[key] for key in ('pipeline','zone','deliveryPoint','product')})
        spread['cashSides']=[spread['cashSides'][1]]
        spread['cashSides'][0]['indexed']['spread']=spread_amount
        spread.update(operationId=str(uuid4()),exerciseRights=[dict(entries=['gas','above'],period='delivery_day',holder='us',
                                                                   settlement='physical',leadHours=6)])
        return c.call('gas/composition/book',spread)
    zero=spread('-3.20')
    positive=spread('0')
    checked={**scope,'tradeIds':[zero['tradeId'],positive['tradeId']]}
    def exclusions():
        return [(r['tradeId'],r['reason']) for r in risk.call('portfolio/risk/catalog',checked)['excludedPositions']]
    assert exclusions()==[(positive['tradeId'],'exercise_marks')]
    assert Fraction(holding(zero)['presentValue'])==96000
    zero_scope={**scope,'tradeIds':[zero['tradeId']]}
    zero_catalog=risk.call('portfolio/risk/catalog',zero_scope)
    point_factor,=[f for f in zero_catalog['factors'] if f['curves']==[point]]
    zero_request={**request,**zero_scope,'context':{**shared,'knowledgeMode':'retained','contextId':zero_catalog['context']['contextId']},
                  'bumps':[dict(factor=point_factor['factor'],size='0.1',convention=zero_catalog['bumpConvention'])]}
    work=risk.call('portfolio/risk',zero_request)['work']
    assert (work['scenarioCount'],work['unitScenarioCandidates'],work['unitPricerCalls'],work['reusedUnitScenarioValues'])==(3,3,3,0),work
    assert (work['preparationUnitPricerCalls'],work['totalUnitPricerCalls'])==(1,4),work
    print('PASS gas exercise work: 3 scenario calls, 0 reused unit values, 1 preparation call and 4 total calls',flush=True)
    writer.call('volatility-marks/record',{**mark,'operationId':str(uuid4()),'curve':other})
    assert exclusions()==[(positive['tradeId'],'exercise_marks')]
    rho=writer.call('correlation-marks/record',pair)['marks'][0]
    reverse={**pair,'firstCurve':other,'secondCurve':point,'operationId':str(uuid4())}
    assert writer.call('correlation-marks/record',{**reverse,'operationId':pair['operationId']})['marks']==[rho]
    assert writer.call('correlation-marks/record',reverse)['marks']==[{**rho,'changed':False}]
    writer.call('correlation-marks/record',{**reverse,'secondCurve':other,'operationId':str(uuid4())},expected=422)
    assert exclusions()==[]
    current=c.call('curve-marks/read',read)
    assert [(v['curve'],v['volatility'],v['revision']) for v in current['volatilities']]==sorted([(point,'0.6',2),(other,'0.5',1)])
    assert [(r['firstCurve'],r['secondCurve'],r['correlation'],r['revision']) for r in current['correlations']]==[(*sorted([point,other]),'0.7',1)]
    correction={**reverse,'operationId':str(uuid4()),'marks':[dict(month='2021-01',expectedRevision=1,correlation='-0.25')]}
    assert writer.call('correlation-marks/record',correction)['marks'][0]['revision']==2
    assert writer.call('correlation-marks/record',pair)['marks']==[rho]
    revised=c.call('curve-marks/read',read)
    assert [(r['correlation'],r['revision']) for r in revised['correlations']]==[('-0.25',2)],revised
    # The latest publication is known now, but a mark effective one minute after this valuation is ineligible.
    from check_booked_side_programs import runtime_query
    cat=manifest['catalogId']
    command={key:value for key,value in raised.items() if key!='context'}
    command.update(operationId=str(uuid4()),convention='curve_marks_log_return_held_v1',
                   marks=[dict(month='2021-01',expectedRevision=2,volatility='0.8')])
    runtime_query(query,manifest,f"SELECT security.record_volatility_marks('{cat}','{json.dumps(command)}'::jsonb,'2020-12-18T16:01:00Z');",profile='editor')
    earlier=c.call('curve-marks/read',read)
    assert earlier['volatilities']==current['volatilities'] and earlier['correlations']==revised['correlations'],earlier
    assert query("SELECT count(DISTINCT (first_curve_code,second_curve_code,bucket_month)) FROM trading.correlation_marks;")=='1'

    for login in ('preview_runtime','preview_risk_worker'):
        try:query(f"SET SESSION AUTHORIZATION {login};SELECT security.read_curve_marks('{cat}','{definition}',ARRAY['{point}'],ARRAY[DATE '2021-01-01']);")
        except RuntimeError as error:assert 'denied' in str(error),error
        else:raise AssertionError('A mark read requires an authorized transaction and a bound context')
    for curve in ('synthetic_worked_location','synthetic_entry_worked_daily'):
        writer.call('volatility-marks/record',{**mark,'operationId':str(uuid4()),'curve':curve})
    worked_routes(c,query,manifest,context,call,base,run_worker)
    print('PASS curve marks: current authority, replay, revision history, pinned absence, symmetric gas pair and held 2947.4419 call',flush=True)


def election_checks(c,query,manifest,context,fixed,cash,plain,run_worker):
    from check_booked_side_programs import runtime_query
    cat=manifest['catalogId']
    at='2020-12-18T16:00:00Z'
    call=deepcopy(fixed)
    path=next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_exercise_point')
    call['physicalSides'][0].update({k:path[k] for k in ('pipeline','zone','deliveryPoint','product')})
    call['physicalSides'][0]['deliveryTo']='2021-01-15'
    call.update(operationId=str(uuid4()),exerciseRights=[dict(entries=['gas','fixed'],period='delivery_day',holder='us',
                                                              settlement='physical',leadHours=8)])
    saved=c.call('gas/composition/book',call)
    selected=dict(tradeId=saved['tradeId'],instrumentId=saved['instrumentId'])
    pin=c.call('gas/quantities/read',dict(context=context,tradeId=saved['tradeId'],selectionPolicy='operational_projection_v1'))['context']
    prior={**context,'knowledgeMode':'retained','contextId':pin['contextId']}
    def read(ctx=context,**kwargs):
        return c.call('gas/exercise/read',dict(context=ctx,**selected),**kwargs)
    def row(day,outcome,previous=None,effective=at):
        return dict(right=0,fromDate=day,outcome=outcome,expectedPreviousId=previous,effectiveAt=effective,evidence='Synthetic group instruction')
    first_day,second_day='2021-01-13','2021-01-14'
    first,second=row(first_day,'exercised'),row(second_day,'abandoned')
    def command(*entries):
        return dict(context=context,**selected,expectedRevision=1,operationId=str(uuid4()),elections=entries)
    def record(body,**options):
        return c.call('gas/exercise/record',body,**options)
    assert read()['elections']==[]
    batch=command(first,second)
    recorded=record(batch)
    d1,d2=[r['revisionId'] for r in recorded['elections']]
    def ticket(ctx):
        return c.call('tickets/trade',dict(context=ctx,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1))
    unpriced=ticket(prior)
    assert unpriced['schedule'] is None and unpriced['scheduleRefusal']['code']=='exercise_marks'
    assert not unpriced['actions']['recordExercise']['allowed']
    priced=ticket(context)
    action=priced['actions']['recordExercise']
    assert action['allowed'] and action['label']=='Record exercise' and action['readContextId']==priced['context']['contextId']
    assert priced['groups'][0]['state']=='exercised'
    invalid=deepcopy(priced['draft'])
    invalid['exerciseRights'][0].update(right_sides=['fixed'],right_settlement='cash_automatic')
    invalid['exerciseRights'][0].pop('right_lead_hours')
    error=c.call('tickets/preview',dict(context=context,draft=invalid),expected=422)
    assert (error['code'],error.get('termRow'),error.get('field'))==('gas_composition_input',0,'right_sides'),error
    selection=dict(context=context,readContextId=pin['contextId'],**selected,expectedRevision=1,
                   operationId=str(uuid4()),right=0,firstPeriod=first_day,lastPeriod=second_day,
                   outcome='abandoned',evidence='Synthetic range decision',effectiveAt=at)
    def prepare(selection,**options):
        return c.call('gas/exercise/prepare',selection,**options)
    stale=prepare(selection)['command']
    assert [e['expectedPreviousId'] for e in stale['elections']]==[None,None],stale
    assert record(stale,expected=409)['code']=='exercise_election_conflict'
    now=read()
    fresh=prepare({**selection,'readContextId':now['context']['contextId']})['command']
    assert [e['expectedPreviousId'] for e in fresh['elections']]==[d1,d2]
    assert [e['fromDate'] for e in fresh['elections']]==[first_day,second_day]
    assert prepare({**selection,'readContextId':None},expected=422)['code']=='exercise_election'
    assert prepare({**selection,'instrumentId':cash['instrumentId'],'tradeId':cash['tradeId']},expected=422)['code']=='exercise_election'
    assert record(batch)==recorded
    assert all(r['unchanged'] for r in record(command(first,second))['elections'])
    assert [r['outcome'] for r in read()['elections']]==['exercised','abandoned']
    assert read(prior)['elections']==[]
    def editor(ctx=context,expected=200):
        return c.call('gas/composition/editor',dict(context=ctx,tradeId=saved['tradeId']),expected=expected)
    assert editor(prior,422)['code']=='exercise_marks'
    present=Fraction(editor()['valuation']['presentValue'])
    assert present==2000,present
    assert Fraction(c.call('gas/composition/preview',{**call,'previewTradeId':saved['tradeId']})['presentValue'])==present
    assert c.call('gas/composition/preview',call,expected=422)['code']=='exercise_marks'
    shared={**context,'convention':'trading_workspace_v1'}
    position=c.call('trading/position',dict(context=shared,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1,instrumentType='physical_gas'))
    assert Fraction(position['holding']['presentValue'])==present
    risk=PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile',dict(profile='risk'))
    risk.call('gas/exercise/record',batch,expected=403)
    read_only=risk.call('tickets/trade',dict(context=context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1))
    assert not read_only['actions']['recordExercise']['allowed']
    risk.call('gas/exercise/prepare',{**selection,'readContextId':read_only['context']['contextId']},expected=403)
    wire=json.dumps({k:v for k,v in batch.items() if k!='context'})
    runtime_query(query,manifest,f"""DO $probe$ BEGIN BEGIN PERFORM security.record_gas_exercise_elections('{cat}','{wire}','{at}');
      RAISE EXCEPTION 'AmendTrade was bypassed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$probe$;""",profile='risk')
    record({**batch,'context':prior},expected=422)
    assert record({**batch,'elections':[second]},expected=409)['code']=='exercise_election_conflict'
    # A native command temporarily reads under a certified cutoff; on return the calling write scope is still current.
    pending=json.dumps({**json.loads(wire),'operationId':str(uuid4()),'elections':[row(first_day,'abandoned',d1)]})
    runtime_query(query,manifest,f"""DO $probe$ DECLARE first_receipt jsonb=security.record_gas_exercise_elections('{cat}','{pending}','{at}');BEGIN
      IF security.record_gas_exercise_elections('{cat}','{pending}','{at}') IS DISTINCT FROM first_receipt
      THEN RAISE EXCEPTION 'Native replay changed';END IF;END$probe$;""",commit=False)
    replay_probe=f"""DO $probe$ BEGIN BEGIN PERFORM security.record_gas_exercise_elections('{cat}','{wire}','{at}');
      RAISE EXCEPTION 'Replay after revocation';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$probe$;"""
    runtime_query(query,manifest,replay_probe,commit=False,prefix="DELETE FROM security.role_permissions WHERE permission='AmendTrade';")
    for invalid in (command({**first,'expectedPreviousId':d1,'evidence':'\t\r\n'}),command(row(first_day,'unknown',d1)),command(row('2021-01-16','exercised')),
                    command(row(first_day,'exercised',d1,'2021-01-13T01:00:00Z')),command(first,first),
                    {**command(first),'tradeId':cash['tradeId'],'instrumentId':cash['instrumentId']}):
        assert record(invalid,expected=422)['code']=='exercise_election'
    assert record(command(row(first_day,'abandoned',d1),row(second_day,'exercised',str(uuid4()))),expected=409)['code']=='exercise_election_conflict'
    assert read()['elections'][0]['revisionId']==d1
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[saved['tradeId']])
    catalog=risk.call('portfolio/risk/catalog',scope)
    assert not catalog['excludedPositions']
    request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
             'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
             'bumps':[dict(factor=catalog['factors'][0]['factor'],size='0.1',convention=catalog['bumpConvention'])]}
    report=risk.call('portfolio/risk',request)
    assert Fraction(report['factors'][0]['positions'][0]['values']['base'])==present
    job=risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))
    def worker(sql):
        return query('SET SESSION AUTHORIZATION preview_risk_worker;'+sql)
    def native(trade):
        return f"security.read_gas_exercise_elections('{cat}','{trade['tradeId']}','{trade['instrumentId']}')"
    def denied(sql):
        worker(f"DO $probe$ BEGIN BEGIN PERFORM {sql};RAISE EXCEPTION 'Election scope bypassed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$probe$;")
    denied(native(saved))
    engine=query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{job['id']}';")
    # Roll back the probe's claim. The real worker consumes the same queued job below.
    worker(f"""BEGIN;DO $probe$ DECLARE lease jsonb=risk.claim(30,'{engine}');BEGIN
      PERFORM risk.bind((lease->>'jobId')::uuid,(lease->>'leaseToken')::uuid);
      IF {native(saved)}#>>'{{elections,0,revisionId}}' IS DISTINCT FROM '{d1}' THEN RAISE EXCEPTION 'Missing leased election';END IF;
      BEGIN PERFORM {native(plain)};RAISE EXCEPTION 'Out-of-job trade disclosed';
      EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'risk_job_scope' THEN RAISE;END IF;END;END$probe$;ROLLBACK;""")
    changed=record(command(row(first_day,'abandoned',d1)))['elections'][0]
    assert changed['revisionId']!=d1 and changed['revision']==2 and Fraction(editor()['valuation']['presentValue'])==0
    assert record(batch)==recorded
    replayed=record(command(first))['elections'][0]
    assert replayed['unchanged'] and replayed['revisionId']==d1 and read()['elections'][0]['revisionId']==changed['revisionId']
    run_worker()
    completed=risk.call('portfolio/risk/jobs/'+job['id'])
    assert completed['state']=='completed' and completed['result']['factors']==report['factors'],completed
    withdrawn=record(command(row(first_day,None,changed['revisionId'])))['elections'][0]
    assert read()['elections'][0]['outcome'] is None and editor(context,422)['code']=='exercise_marks'
    unavailable=ticket(context)
    assert unavailable['scheduleRefusal']['code']=='exercise_marks' and unavailable['actions']['recordExercise']['allowed']
    after_withdrawal=prepare({**selection,'readContextId':unavailable['actions']['recordExercise']['readContextId'],'lastPeriod':first_day})['command']
    assert after_withdrawal['elections'][0]['expectedPreviousId']==withdrawn['revisionId']
    reinstated={**row(first_day,'exercised',withdrawn['revisionId']),'evidence':'Synthetic subsequent instruction'}
    active=record(command(reinstated))['elections'][0]
    future='2020-12-18T16:01:00Z'
    pending=record(command(row(first_day,'abandoned',active['revisionId'],future),row(second_day,None,d2,'2020-12-18T16:02:00Z')))
    now=read()
    assert [r['outcome'] for r in now['elections']]==['exercised','abandoned'] and now['nextElectionEffectiveAt'].startswith(future[:-1]),now
    correction={**selection,'readContextId':now['context']['contextId'],'effectiveAt':'2020-12-18T16:03:00Z'}
    command_at_future=prepare(correction)['command']
    assert [e['expectedPreviousId'] for e in command_at_future['elections']]==[e['revisionId'] for e in pending['elections']]
    assert prepare({**correction,'effectiveAt':at},expected=422)['code']=='exercise_election'
    assert Fraction(editor()['valuation']['presentValue'])==present
    # Rebind a certified read at each exact effective instant on the same business day.
    for minute,expected,next_at in [('01',['abandoned','abandoned'],'16:02'),('02',['abandoned',None],None)]:
        runtime_query(query,manifest,f"""DO $probe$ DECLARE ctx uuid=security.resolve_operation_context('{cat}','2020-12-18',NULL);value jsonb;BEGIN
          PERFORM security.use_market_context('{cat}',ctx);PERFORM security.use_trading_context('{cat}','2020-12-18T16:{minute}:00Z');
          value={native(saved)};IF value#>>'{{elections,0,outcome}}' IS DISTINCT FROM '{expected[0]}' OR value#>'{{elections,1,outcome}}' IS DISTINCT FROM '{json.dumps(expected[1])}'::jsonb
          OR (value->>'nextElectionEffectiveAt')::timestamptz IS DISTINCT FROM {("TIMESTAMPTZ '2020-12-18T"+next_at+":00Z'") if next_at else 'NULL::timestamptz'}
          THEN RAISE EXCEPTION 'Wrong effective election: %',value;END IF;END$probe$;""")
    ticket_authority_expiry(risk,query,manifest,context,saved,pending['elections'][0]['revisionId'])
    before_amend=read()['context']
    old_context={**context,'contextId':before_amend['contextId'],'knowledgeMode':'retained'}
    same=amend_rights(query,manifest,saved,call['exerciseRights'],at)
    assert same['unchanged'] and same['revision']==1 and same['instrumentId']==saved['instrumentId']
    assert Fraction(editor()['valuation']['presentValue'])==present
    revised=amend_rights(query,manifest,saved,[{**call['exerciseRights'][0],'holder':'counterparty'}],at)
    assert not revised['unchanged'] and revised['revision']==2 and revised['instrumentId']!=saved['instrumentId']
    assert c.call('gas/exercise/read',dict(context=context,tradeId=saved['tradeId'],instrumentId=revised['instrumentId']))['elections']==[]
    read(expected=403)
    assert [r['revisionId'] for r in read(old_context)['elections']]==[active['revisionId'],d2]
    historical=editor(old_context)
    assert historical['terms']['exerciseRights']==call['exerciseRights'] and Fraction(historical['valuation']['presentValue'])==present
    assert editor(context,422)['code']=='exercise_marks'
    assert record(batch)==recorded
    print('PASS gas elections: atomic revisions, withdrawals, exact effective and knowledge cuts, held PV without marks, worker lease and pinned replay',flush=True)


def ticket_authority_expiry(risk,query,manifest,context,saved,pending_head):
    request=dict(context=context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1)
    role,grant=uuid4(),uuid4()
    name='exercise_expiry_'+role.hex
    table='trading.facility_gas_allocations'
    try:
        query(f"""BEGIN;INSERT INTO security.roles VALUES('{role}','{name}','Synthetic expiring exercise authority');
          INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');
          INSERT INTO security.role_grants SELECT '{grant}',principal_id,'{role}','{manifest['tenantId']}','{manifest['workspaceId']}',
           clock_timestamp()-interval '1 day',clock_timestamp()+interval '3.5 seconds',false FROM security.identity_bindings
           WHERE issuer='https://preview.invalid' AND subject='risk';COMMIT;""")
        with ThreadPoolExecutor(max_workers=2) as pool:
            # Facility allocations are read after the authoring heads have been materialized.
            holder=pool.submit(query,f"SET application_name='{name}';BEGIN;LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep_until((SELECT granted_to FROM security.role_grants WHERE id='{grant}'));COMMIT;")
            deadline=time.monotonic()+3
            while query(f"SELECT EXISTS(SELECT FROM pg_stat_activity WHERE application_name='{name}' AND wait_event='PgSleep');")!='t':
                assert time.monotonic()<deadline,'Facility lock holder did not start'
                time.sleep(.03)
            reading=pool.submit(risk.call,'tickets/trade',request,expected=(200,403))
            deadline=time.monotonic()+5
            while query(f"""SELECT EXISTS(SELECT FROM pg_locks l JOIN pg_stat_activity a USING(pid),security.role_grants g
              WHERE g.id='{grant}' AND clock_timestamp()<g.granted_to AND NOT l.granted AND l.relation='{table}'::regclass
              AND a.query LIKE '%security.read_trade_facility_allocations%');""")!='t':
                assert not reading.done() and time.monotonic()<deadline,'Ticket did not reach the facility read with live amendment authority'
                time.sleep(.03)
            holder.result(timeout=12)
            result=reading.result(timeout=10)
            periods=result.get('actions',{}).get('recordExercise',{}).get('periods',[])
            assert all(p['previousId']!=pending_head for p in periods),'Booked ticket disclosed a future election head after AmendTrade expired'
            assert risk.last_status==403 and result['code']=='access_denied' and 'actions' not in result,result
        action=risk.call('tickets/trade',request)['actions']['recordExercise']
        assert not action['allowed'] and action['periods']==[] and action['readContextId'] is None,action
    finally:
        query(f"""BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{role}';
          DELETE FROM security.roles WHERE id='{role}';COMMIT;""")
    print('PASS booked exercise ticket rechecks expiring authority before publishing future election heads',flush=True)


def amend_rights(query,manifest,saved,rights,at):
    """Stage the unchanged retained Sides through the native API and amend just the right terms."""
    from check_booked_side_programs import runtime_query
    cat=manifest['catalogId']
    trade=saved['tradeId']
    inst=saved['instrumentId']
    op=str(uuid4())
    body=json.loads(query(f"SELECT request FROM trading.trades WHERE(catalog_id,id)=('{cat}','{trade}');"))
    body.update(operationId=op,tradeId=trade,bindingId=inst,expectedRevision=1,reason='Synthetic exercise terms',
                amendmentPolicy='future_gas_composition_replace_v1',exerciseRights=rights)
    headers=json.loads(query(f"""SELECT jsonb_agg(jsonb_build_object('family',s.family,'definitionRevisionId',s.definition_revision_id,
      'referenceCatalogId',s.reference_catalog_id,'referenceRevisionId',s.reference_revision_id,'economicIdentity',s.economic_identity,
      'pricingBindingIdentity',s.pricing_binding_identity,'schemaVersion',h.schema_version,'generatorVersion',h.generator_version,
      'manifest',encode(h.content_hash,'hex'),'rowCount',h.row_count,'edgeCount',h.edge_count,'bindingCount',h.binding_count,
      'rootCount',h.root_count,'portCount',h.port_count,'gridCount',h.grid_count) ORDER BY m.slot)
      FROM trading.instrument_side_schedules m JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(m.catalog_id,m.schedule_id)
      JOIN trading.sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE(m.catalog_id,m.instrument_id)=('{cat}','{inst}');"""))
    def literal(value):
        return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    command=literal(body)
    stage=''.join(f"SELECT security.begin_side_schedule('{cat}','{op}',{slot},{command},'AmendTrade','{trade}',1,{literal(header)});"
                  for slot,header in enumerate(headers))
    result=runtime_query(query,manifest,stage+f"""SELECT security.seal_side_schedule_request('{cat}','{op}','[]');
      SELECT security.lock_side_schedule_request('{cat}','{op}');
      WITH amended AS MATERIALIZED(SELECT security.amend_gas_composition('{cat}',{command},'{at}') result)
      SELECT result FROM amended WHERE CASE WHEN (result->>'unchanged')::boolean THEN true
       ELSE security.publish_operation_side_schedules('{cat}','{op}')=(result->>'instrumentId')::uuid END;""")
    return json.loads(result.splitlines()[-1])


def publish_daily_point(c,context,source):
    c.call('profile',dict(profile='editor'))
    inputs=next(s for s in c.call('custom-curves')['sources'] if s['code']==source)
    contracts=[r for r in inputs['contracts'] if '2021-01-16'<=r['deliveryFrom']<='2021-01-18']
    assert len(contracts)==3
    graph=dict(code='worked_point_daily',name='Synthetic daily point forecast',output='output',nodes=[
        dict(id='source',kind='source',parameters=dict(curve=source),inputs={}),
        dict(id='daily',kind='weighted_contracts',parameters=dict(aggregation='signed_sum_v1'),inputs=dict(price='source'),
             contractWeights=[dict(outputPosition=i,sourcePort='price',contractId=r['id'],weight='1') for i,r in enumerate(contracts,1)]),
        dict(id='dates',kind='dates',parameters=dict(mode='context',sampling='context_utc_time',corrections='exact_context_frame_history_latest_visible'),inputs={}),
        dict(id='sample',kind='sample',parameters={},inputs=dict(selection='daily',dates='dates')),
        dict(id='output',kind='output',parameters={},inputs=dict(values='sample'))])
    saved=c.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=context['snapshotId'],definition=graph,layout={}))
    c.call('profile',dict(profile='approver'))
    published=c.call('custom-curves/publish',dict(requestId=str(uuid4()),snapshotId=context['snapshotId'],id=saved['id'],version=saved['version'],generation=saved['generation']))
    c.call('profile',dict(profile='trader'))
    return published


def worked_routes(c,query,manifest,context,call,base,run_worker):
    from check_booked_side_programs import runtime_query
    from preview_client import initial_frame
    source='synthetic_entry_worked_daily'
    published=publish_daily_point(c,context,source)
    body=deepcopy(call)
    book=str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic exercise routes',publication_order,effective_at FROM trading.books LIMIT 1;")
    body.update(operationId=str(uuid4()),bookId=book)
    body['physicalSides'][0]['deliveryPoint']='synthetic_worked_point'
    body['physicalSides'][0]['forecastCurve']=dict(catalogId=manifest['catalogId'],curveId=published['id'],version=published['version'],
        outputNode='output',position=None,selection='containing_delivery_interval_v1',implementation='published_custom_curve_projection_v1')
    def book_trade(terms):
        return c.call('gas/composition/book',terms)
    held=book_trade(body)
    sold=deepcopy(body)
    sold.update(operationId=str(uuid4()))
    sold['exerciseRights'][0]['holder']='counterparty'
    written=book_trade(sold)
    pin=c.call('gas/exercise/read',dict(context=context,tradeId=held['tradeId'],instrumentId=held['instrumentId']))['context']
    prior={**context,'knowledgeMode':'retained','contextId':pin['contextId']}
    def elect(trade,day,outcome,at,previous=None):
        return c.call('gas/exercise/record',dict(context=context,tradeId=trade['tradeId'],instrumentId=trade['instrumentId'],expectedRevision=1,
            operationId=str(uuid4()),elections=[dict(right=0,fromDate=day,outcome=outcome,effectiveAt=at,expectedPreviousId=previous,evidence='Synthetic worked election')]))['elections'][0]
    def initial_elections(trade):
        first=elect(trade,'2021-01-16','exercised','2020-12-18T16:00:00Z')
        elect(trade,'2021-01-17','abandoned','2020-12-18T16:00:00Z')
        return first['revisionId']
    initial_elections(written)
    first=initial_elections(held)
    # Known at the opening close, effective at the next one.
    elect(held,'2021-01-16','abandoned','2021-01-01T08:00:00Z',first)
    def shared(ctx):
        return {**ctx,'convention':'trading_workspace_v1'}
    def blotter(ctx):
        view=dict(mode='trades',bookIds=[book],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)
        return {h['tradeId']:h for h in c.call('trading/blotter',dict(context=shared(ctx),view=view))['holdings']}
    def projections(terms,trade,ctx):
        preview=c.call('gas/composition/preview',{**terms,'snapshotId':ctx['snapshotId'],'previewTradeId':trade['tradeId'],'knowledgeMode':ctx['knowledgeMode'],
            **({'previewContextId':ctx['contextId']} if 'contextId' in ctx else {})})
        editor=c.call('gas/composition/editor',dict(context=ctx,tradeId=trade['tradeId']))['valuation']
        holding=blotter(ctx)[trade['tradeId']]
        assert preview['groups']==editor['groups']==holding['groups']
        assert preview['presentValue']==editor['presentValue']==holding['presentValue']
        return editor
    def at(day):
        return {**context,'snapshotId':initial_frame(c.call('workspace'),day)['id']}
    def finite(value):
        return round(Fraction(value)*10000)
    value=projections(body,held,context)
    groups=value['groups']
    assert [(g['right'],g['from'],g['to'],g['state'],Fraction(g['forwardSum']),finite(g['counted'])) for g in groups]==[
        (0,'2021-01-16','2021-01-17','exercised',1000,10000000),(0,'2021-01-17','2021-01-18','abandoned',-500,0),
        (0,'2021-01-18','2021-01-19','undecided',2000,29474419)],groups
    assert finite(value['presentValue'])==39474419
    assert groups[0]['inputs'] is None and groups[1]['inputs'] is None
    assert value['schedule']['groups']==groups
    model=groups[2]['inputs']
    assert (Fraction(model['receives']),Fraction(model['pays']),Fraction(model['certain']),Fraction(model['receivesVolatility']))==(32000,0,-30000,Fraction('0.5'))
    assert model['paysVolatility'] is None and model['correlation'] is None and abs(Fraction(model['years'])-Fraction(30,365))<Fraction(1,10**28)
    check_worked_schedule(c,shared(context),held,value)
    assert finite(projections(sold,written,context)['groups'][2]['counted'])==-9474419
    historical=projections(body,held,prior)
    assert [g['state'] for g in historical['groups']]==['undecided']*3
    future=at('2021-01-01')
    later=projections(body,held,future)
    assert [g['state'] for g in later['groups']]==['abandoned','abandoned','undecided']

    risk=PreviewClient(c.url);risk.bootstrap();risk.call('profile',dict(profile='risk'))
    def request_for(trade,ctx,extra=()):
        scope=dict(context=shared(ctx),bookIds=[],portfolioIds=[],tradeIds=[trade['tradeId'],*extra])
        cat=risk.call('portfolio/risk/catalog',scope)
        assert not cat['excludedPositions'],cat['excludedPositions']
        return cat,{**scope,'context':{**shared(ctx),'contextId':cat['context']['contextId'],'knowledgeMode':'retained'},'detail':'holdings',
            'convention':cat['convention'],'numericalPolicy':cat['numericalPolicy'],'pricingPolicy':cat['pricingPolicy'],'stress':[]}
    def factor_result(trade,ctx,expected,custom=False,extra=()):
        cat,req=request_for(trade,ctx,extra)
        factor=next(f for f in cat['factors'] if not custom or f['deliveryFrom']=='2021-01-18' and source in f['curves'])
        req['bumps']=[dict(factor=factor['factor'],size='0.0001',convention=cat['bumpConvention'])]
        result=risk.call('portfolio/risk',req)
        row=next(p for p in result['factors'][0]['positions'] if p['tradeId']==trade['tradeId'])
        assert row['groups']['base']==expected['groups'] and row['values']['base']==expected['presentValue']
        for lane in ['base','up','down']:
            assert sum(Fraction(e[lane]) for e in row['entries'])+sum(Fraction(g[lane]) for g in row.get('exerciseContributions',[]))==Fraction(row['values'][lane])
        return cat,req,result,row
    cat,req,result,row=factor_result(held,context,value,True)
    assert Fraction(row['groups']['up'][2]['counted'])>Fraction(groups[2]['counted'])>Fraction(row['groups']['down'][2]['counted'])
    assert len(row['exerciseContributions'])==1 and row['exerciseContributions'][0]['from']=='2021-01-18'
    job=risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=req))
    vol_job,samples=check_volatility_scenarios(risk,query,cat,req,value)
    restored=risk.call('date-schedules/context',dict(effectiveOn=pin['effectiveOn'],knownAt=pin['knownAt'],mode='at'))['context']
    assert restored['contextId']==prior['contextId']
    factor_result(held,prior,historical,True)
    factor_result(held,future,later,True)
    factor_result(written,context,projections(sold,written,context),True)
    # The worker must recover the submitted input ID and value after a new mark is published.
    c.call('profile',dict(profile='editor'))
    c.call('volatility-marks/record',dict(context=context,definitionRevisionId=body['definitionRevisionId'],
        operationId=str(uuid4()),reason='Synthetic revised point volatility',curve='synthetic_worked_location',
        marks=[dict(month='2021-01',expectedRevision=1,volatility='0.55')]))
    c.call('profile',dict(profile='trader'))
    for queued,expected,key in [(job,result['factors'],'factors'),(vol_job,samples,'joint')]:
        run_worker()
        finished=risk.call('portfolio/risk/jobs/'+queued['id'])
        assert finished['state']=='completed',finished
        got=finished['result'][key]
        assert (got['samples'] if key=='joint' else got)==expected
    # A plain zero-valued physical quantity source leaves the standalone index payment and its cap observable.
    cap=deepcopy(base);cap.update(bookId=book,operationId=str(uuid4()))
    cap['physicalSides'][0].update(deliveryPoint='synthetic_cap_point',deliveryFrom='2021-01-18',deliveryTo='2021-01-19')
    daily=deepcopy(base['cashSides'][1]);daily.update(code='ordinary',direction='pay',part=None)
    daily['indexed'].update(index=source,spread='0')
    cap['cashSides']=[daily,{**daily,'code':'receive','direction':'receive'},{**body['cashSides'][0],'price':'3.50'}]
    cap['exerciseRights']=[dict(entries=['receive','fixed'],period='delivery_day',holder='us',settlement='cash_automatic',leadHours=None)]
    cap_trade=book_trade(cap)
    cap_value=projections(cap,cap_trade,context)
    assert finite(cap_value['groups'][0]['counted'])==7754967 and finite(cap_value['presentValue'])==-312245033
    factor_result(cap_trade,context,cap_value)
    deadline=at('2021-01-17')
    expired=blotter(deadline)
    assert expired[held['tradeId']]['unavailableReason']=='exercise_determination' and expired[held['tradeId']]['presentValue'] is None
    assert Fraction(expired[cap_trade['tradeId']]['presentValue'])==-35000
    expired_ticket=c.call('tickets/trade',dict(context=deadline,tradeId=held['tradeId'],bindingId=held['instrumentId'],revision=1))
    assert expired_ticket['scheduleRefusal']['code']=='exercise_determination' and expired_ticket['actions']['recordExercise']['allowed']
    prepared=c.call('gas/exercise/prepare',dict(context=deadline,readContextId=expired_ticket['actions']['recordExercise']['readContextId'],
        tradeId=held['tradeId'],instrumentId=held['instrumentId'],expectedRevision=1,operationId=str(uuid4()),right=0,
        firstPeriod='2021-01-18',lastPeriod='2021-01-18',outcome='exercised',effectiveAt='2020-12-18T16:00:00Z',evidence='Synthetic dated instruction'))
    assert prepared['command']['elections'][0]['fromDate']=='2021-01-18'
    catalog=risk.call('portfolio/risk/catalog',dict(context=shared(deadline),bookIds=[],portfolioIds=[],tradeIds=[held['tradeId'],cap_trade['tradeId']]))
    assert [(r['tradeId'],r['reason']) for r in catalog['excludedPositions']]==[(held['tradeId'],'exercise_determination')]
    decided=projections(cap,cap_trade,deadline)
    assert [(g['state'],Fraction(g['counted']),g['inputs']) for g in decided['groups']]==[('exercised',3000,None)]
    factor_result(cap_trade,deadline,decided)
    paid=at('2021-01-31')
    amount=dict(operationId=str(uuid4()),tradeId=cap_trade['tradeId'],expectedRevision=1,slot=3,businessKey='synthetic-cap-remittance',
        paidAt='2021-01-23T00:00:00Z',currency='usd',amount='-500',allocations=[dict(occurrence=0,amount='-500')])
    runtime_query(query,manifest,f"SELECT security.record_gas_cash_payment('{manifest['catalogId']}','{json.dumps(amount)}'::jsonb,'2021-01-31T08:00:00Z');")
    after=projections(cap,cap_trade,paid)
    assert Fraction(after['groups'][0]['counted'])==3000 and Fraction(after['presentValue'])==-34500
    assert Fraction(after['sides'][3]['presentValue'])==-34500 and Fraction(after['measures']['realizedUnsettled'])==-34500
    # A still-live companion supplies the report axis after the cap is entirely fixed and delivered.
    ordinary=deepcopy(call);ordinary.pop('exerciseRights')
    ordinary.update(bookId=book,operationId=str(uuid4()));ordinary['physicalSides'][0].update(deliveryPoint='synthetic_gas_point_a',deliveryFrom='2021-03-01',deliveryTo='2021-03-03')
    ordinary['cashSides'][0]['paymentAt']='2021-04-10T00:00:00Z'
    companion=book_trade(ordinary)
    factor_result(cap_trade,paid,after,extra=[companion['tradeId']])
    from check_portfolio_risk import book_supplemental_swap
    _,swap=book_supplemental_swap(c,{'id':context['snapshotId']},book,manifest)
    for selected,ctx,expected in [(held,context,projections(body,held,context)),(cap_trade,at('2021-01-24'),after)]:
        cat,req=request_for(selected,ctx,[swap['tradeId']])
        other=next(f for f in cat['factors'] if 'daily_gas' in f['curves'] and f['deliveryFrom']=='2021-01-29')
        req['bumps']=[dict(factor=other['factor'],size='0.0001',convention=cat['bumpConvention'])]
        report=risk.call('portfolio/risk',req)
        work=report['work']
        assert (work['unitScenarioCandidates'],work['unitPricerCalls'],work['reusedUnitScenarioValues'])==(6,4,2),work
        unchanged=next(p for p in report['factors'][0]['positions'] if p['tradeId']==selected['tradeId'])
        for lane in ['base','up','down']:
            assert unchanged['groups'][lane]==expected['groups']
            assert sum(Fraction(e[lane]) for e in unchanged['entries'])+sum(Fraction(g[lane]) for g in unchanged.get('exerciseContributions',[]))==Fraction(expected['presentValue'])
    receipt=c.call('gas/exercise/record',prepared['command'])
    assert c.call('gas/exercise/record',prepared['command'])==receipt
    assert receipt['elections'][0]['revision']==1 and not receipt['elections'][0]['unchanged']
    print('PASS exercise ticket: a backdated instruction prepared at the elapsed deadline records once and replays',flush=True)
    print('PASS worked gas exercise: held groups, contingent schedule provenance, signed scenarios, payment reconciliation, temporal elections and leased risk',flush=True)


def check_worked_schedule(c,position_context,held,value):
    groups=value['groups']
    days={r['key']:r for r in value['schedule']['rows'] if r['level']=='day'}
    for g in groups:
        assert ('contingent' in days[g['from']]['cells']['gas.amount']['sources'])==(g['state']=='undecided')
    position=dict(context=position_context,tradeId=held['tradeId'],bindingId=held['instrumentId'],revision=1,instrumentType='physical_gas')
    for slot in (0,1):
        page=c.call('trading/side-schedules',dict(position=position,sideSlot=slot,contribution=None,offset=0,limit=8))['page']
        assert page['groups']==groups
        if slot==0:
            for row,g in zip(page['rows'],groups,strict=True):
                status='contingent' if g['state']=='undecided' else g['state']
                assert row['exercisePeriods']==[{'right':0,'from':g['from'],'to':g['to'],'status':status}]
        else:
            [row]=page['rows']
            assert [period['status'] for period in row['exercisePeriods']]==['exercised','abandoned','contingent']
    [month]=[r for r in value['schedule']['rows'] if r['level']=='month']
    assert [period['status'] for period in month['cells']['fixed.amount']['exercisePeriods']]==['exercised','abandoned','contingent']


def check_volatility_scenarios(risk,query,cat,req,value):
    groups=value['groups']
    [vol]=cat['volatilityInputs']
    convention=cat['joint']['volatilityShiftConvention']
    scenarios=[dict(id=code,label=code,shifts=[],volatility=[dict(inputId=vol['id'],size=size,convention=convention)]) for code,size in [('raised','0.01'),('lowered','-0.01')]]
    def joint(samples):
        return {**req,'bumps':[],'joint':dict(convention=cat['joint']['convention'],derivatives=None,
            scenarios=dict(id='worked-volatility',version='1',convention=cat['joint']['scenarioConvention'],scenarios=samples))}
    joint_request=joint(scenarios)
    samples=risk.call('portfolio/risk',joint_request)['joint']['samples']
    assert Fraction(samples[0]['positions'][0]['values']['value'])>Fraction(value['presentValue'])>Fraction(samples[1]['positions'][0]['values']['value'])
    for sample,volatility in zip(samples,['0.51','0.49']):
        assert sample['positions'][0]['groups']['base']==groups
        assert sample['positions'][0]['groups']['value'][2]['inputs']['receivesVolatility']==volatility
    vol_job=risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=joint_request))
    def native_sample(sample,allowed):
        command=json.dumps(joint([sample])).replace("'","''")
        try:query(f"SELECT risk.axis_commands('{command}'::jsonb);")
        except RuntimeError as error:assert not allowed and 'risk_joint_input' in str(error),error
        else:assert allowed,sample
    native_sample(scenarios[0],True)
    for shifts in [[],[None],{},scenarios[0]['volatility']*2,[{**scenarios[0]['volatility'][0],'inputId':'invalid'}],
                   [{**scenarios[0]['volatility'][0],'size':'1e-4'}],
                   [{**scenarios[0]['volatility'][0],'convention':'other'}],
                   [dict(inputId=str(uuid4()),size='0.01',convention=convention) for _ in range(17)]]:
        native_sample({**scenarios[0],'volatility':shifts},False)
    for change in [dict(inputId=str(uuid4())),dict(convention='other'),dict(size='x'),dict(size='-0.51')]:
        invalid=deepcopy(scenarios[:1]);invalid[0]['volatility'][0].update(change)
        risk.call('portfolio/risk',joint(invalid),expected=422)
    duplicate=deepcopy(scenarios[:1]);duplicate[0]['volatility']*=2
    risk.call('portfolio/risk',joint(duplicate),expected=422)
    return vol_job,samples
