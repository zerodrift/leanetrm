"""Exercise-term booking, independent native admission and retained group times."""
from copy import deepcopy
from fractions import Fraction
import json
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
    curve_marks(c,query,manifest,context,fixed,base,plain,run_worker)
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
    pair=dict(**selected,operationId=str(uuid4()),reason='Synthetic gas curve correlation',firstCurve=point,secondCurve=other,
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
    print('PASS curve marks: current authority, replay, revision history, pinned absence, symmetric gas pair and held 2947.4419 call',flush=True)
