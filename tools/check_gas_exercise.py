"""Exercise-term booking, independent native admission and retained group times."""
from copy import deepcopy
import json
from uuid import uuid4
from check_gas_pricing import request, staged_booking
from preview_client import PreviewClient
from synthetic_side_calendars import side_calendar_policy


def run_checks(url,query,manifest):
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
    print('PASS gas exercise: API and SQL admission, exact physical and latest fixing times, immutable terms, replay and unpriced valuation',flush=True)
