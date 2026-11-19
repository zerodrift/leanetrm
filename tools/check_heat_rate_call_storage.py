#!/usr/bin/env python3
"""Independent native heat-rate call option booking, replay and position-read checks."""
from copy import deepcopy
from uuid import uuid4
import json
from check_reference_editing import main
from check_workspace_as_of import read_trading_workspace
from preview_fixture import fixture,identity,trading_fixture,trade_booking

BOOKED_AT='2020-12-15T16:00:00Z'
# February 2021 on the synthetic curves: the fifth contract of a month is local power, the third local gas.
POWER,FUEL=identity('5',10),identity('5',8)


def exercise(day,exercised,energy='24'):
    return dict(deliveryDay=day,exerciseAt=exercised,paymentAt='2021-03-10T00:00:00.000000Z',energyPerUnit=energy,
        powerContractId=POWER,fuelContractId=FUEL)


def run_checks(query,manifest):
    catalog=manifest['catalogId']
    def runtime(sql,actor='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','{actor}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return query('\\set VERBOSITY verbose\nBEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'+f"SELECT security.use_context('{token}');"+sql+'COMMIT;')
    def reject(sql,code,actor='trader'):
        try:runtime(sql,actor)
        except RuntimeError as error:
            assert code in str(error),str(error)
        else:raise AssertionError('Accepted an invalid native heat-rate call operation: expected '+code)
    literal=lambda value:json.dumps(value).replace("'","''")
    def book(body,prepared):
        return f"SELECT security.book_heat_rate_call('{catalog}','{literal(body)}'::jsonb,'{literal(prepared)}'::jsonb,'{BOOKED_AT}');"
    counts=lambda:query('SELECT(SELECT count(*) FROM trading.trades)||\'/\'||(SELECT count(*) FROM trading.heat_rate_call_instruments)'
        '||\'/\'||(SELECT count(*) FROM trading.heat_rate_call_exercises)||\'/\'||(SELECT count(*) FROM trading.heat_rate_call_booking_requests);')

    reject(f"SELECT security.heat_rate_call_booking_status('{catalog}','{uuid4()}');",'42501','viewer')
    query(trading_fixture(catalog))
    assert query("SELECT assistant.is_prompt_page('blotter_new_heat_rate_call') AND NOT assistant.is_prompt_page('synthetic_unlisted_page');")=='t'
    assert runtime(f"SELECT security.heat_rate_call_booking_status('{catalog}','{uuid4()}');") in ('','null')
    reject('SELECT count(*) FROM trading.heat_rate_call_instruments;','permission denied')
    reject('SELECT count(*) FROM trading.heat_rate_call_exercises;','permission denied')
    print('PASS heat-rate call storage: receipts need trade authority and the retained tables are closed to the runtime role',flush=True)

    terms=dict(definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        quantity='50',capacityQuantity='test_power_quantity',powerCurve='local_power',fuelCurve='local_gas',heatRate='7.5',strike='3',
        exerciseStyle='daily_automatic_cash_exercise_v1',model='spread_call_kirk_lognormal_v1',dayCount='act_365_fixed_instant_v1',
        deliveryFrom='2021-02-01',deliveryTo='2021-02-04',clock='test_utc',intervalMinutes=60,
        hours=dict(membership='all',touProduct=None,touVersion=None,calendar='test_business'),
        exerciseRule=dict(implementation='business_day_lead_local_cutoff_v1',leadBusinessDays=1,localCutoff='15:00',calendar='test_business'),
        paymentRule=dict(frequency='monthly',rule='days_after_period_end',days=10,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,
            adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1'))
    body=dict(operationId=str(uuid4()),convention='heat_rate_call_trade_v1',bookId='d1000000-0000-0000-0000-000000000001',**trade_booking(),
        reason='Synthetic toll option',terms=terms)
    prepared=dict(instrumentId=str(uuid4()),exercises=[exercise('2021-02-01','2021-01-29T15:00:00.000000Z'),
        exercise('2021-02-02','2021-02-01T15:00:00.000000Z'),exercise('2021-02-03','2021-02-02T15:00:00.000000Z')])
    before=counts()
    def refuse(code,change,target='body'):
        changed_body,changed_prepared=deepcopy(body),deepcopy(prepared)
        change(changed_body if target=='body' else changed_prepared)
        reject(book(changed_body,changed_prepared),code)
        assert counts()==before,'A refused booking left facts behind: '+code
    refuse('heat_rate_call_input',lambda b:b.update(unexpectedFallback='0'))
    refuse('heat_rate_call_input',lambda b:b.update(convention='another_trade_v1'))
    refuse('heat_rate_call_input',lambda b:b['terms'].update(quantity=50))
    refuse('heat_rate_call_input',lambda b:b['terms'].pop('dayCount'))
    refuse('heat_rate_call_input',lambda b:b['terms']['exerciseRule'].update(leadBusinessDays=0))
    refuse('heat_rate_call_input',lambda b:b['terms']['exerciseRule'].update(localCutoff='3pm'))
    refuse('heat_rate_call_input',lambda b:b.update(reason=' '))
    refuse('heat_rate_call_input',lambda b:b['terms'].update(strike='3e0'))
    print('PASS heat-rate call storage: an open, mistyped or incomplete command creates nothing',flush=True)
    refuse('heat_rate_call_policy',lambda b:b['terms'].update(model='another_model_v1'))
    refuse('heat_rate_call_policy',lambda b:b['terms'].update(exerciseStyle='hourly_exercise_v1'))
    refuse('heat_rate_call_policy',lambda b:b['terms']['hours'].update(membership='peak'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(quantity='0'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(heatRate='0'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(strike='-0.01'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(deliveryTo='2021-02-01'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(fuelCurve='local_power'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(fuelCurve='gas_basis'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(capacityQuantity='test_gas_quantity'))
    refuse('heat_rate_call_terms',lambda b:b['terms'].update(clock='absent_clock'))
    refuse('heat_rate_call_terms',lambda b:b['terms']['exerciseRule'].update(calendar='absent_calendar'))
    refuse('42501',lambda b:b['terms'].update(definitionRevisionId=str(uuid4())))
    refuse('heat_rate_call_input',lambda b:b['terms']['paymentRule'].update(days=None))
    print('PASS heat-rate call storage: unselected policies, non-outright or same-commodity curves and unknown reference terms refuse',flush=True)
    refuse('heat_rate_call_input',lambda p:p.update(exercises=[]),'prepared')
    refuse('heat_rate_call_input',lambda p:p['exercises'][0].update(defaultPrice='0'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'].reverse(),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][2].update(deliveryDay='2021-02-04'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][0].update(exerciseAt='2020-12-15T16:00:00.000000Z'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][1].update(exerciseAt='2021-01-28T15:00:00.000000Z'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][0].update(exerciseAt='2021-02-02T00:00:00.000000Z'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][1].update(paymentAt='2021-03-11T00:00:00.000000Z'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][0].update(paymentAt='2021-01-01T00:00:00.000000Z'),'prepared')
    refuse('heat_rate_call_exercise',lambda p:p['exercises'][0].update(energyPerUnit='0'),'prepared')
    refuse('heat_rate_call_contract',lambda p:p['exercises'][0].update(powerContractId=identity('5',15)),'prepared')
    refuse('heat_rate_call_contract',lambda p:p['exercises'][0].update(fuelContractId=POWER),'prepared')
    print('PASS heat-rate call storage: exercises must be ordered, ahead of booking, inside the period, paid once per month and on the delivering contracts',flush=True)

    reject(book(body,prepared),'42501','viewer')
    assert counts()==before
    first=json.loads(runtime(book(body,prepared)))
    assert first['changed'] is True and first['revision']==1 and first['instrumentId']==prepared['instrumentId'] and first['operationId']==body['operationId'],first
    booked=counts()
    assert json.loads(runtime(book(body,prepared)))==first and counts()==booked
    assert json.loads(runtime(f"SELECT security.heat_rate_call_booking_status('{catalog}','{body['operationId']}');"))==first
    assert json.loads(runtime(f"SELECT security.heat_rate_call_booking_replay('{catalog}','{literal(body)}'::jsonb);"))==first
    changed=deepcopy(body);changed['terms']['strike']='4'
    reject(book(changed,prepared),'heat_rate_call_request_conflict')
    reject(f"SELECT security.heat_rate_call_booking_replay('{catalog}','{literal(changed)}'::jsonb);",'heat_rate_call_request_conflict')
    print('PASS heat-rate call storage: one booking retains the trade and a retried command returns its first outcome',flush=True)
    again=deepcopy(body);again.update(operationId=str(uuid4()),reason='Entered twice');again['terms'].update(strike='3.00',heatRate='7.50')
    other=dict(prepared,instrumentId=str(uuid4()))
    second=json.loads(runtime(book(again,other)))
    assert second=={**first,'operationId':again['operationId'],'changed':False},second
    assert query('SELECT count(*) FROM trading.heat_rate_call_trade_revisions;')=='1' and query('SELECT count(*) FROM trading.heat_rate_call_booking_requests;')=='2'
    # A small exact term keeps its digits: 0.01 is not stored through an inexact quotient.
    sold=deepcopy(body);sold.update(operationId=str(uuid4()));sold['terms'].update(quantity='-50',strike='0.01')
    third=json.loads(runtime(book(sold,other)))
    assert third['changed'] is True and third['tradeId']!=first['tradeId'] and third['instrumentId']==other['instrumentId'],third
    print('PASS heat-rate call storage: the same fact under a new request is a no-op and a different quantity is a second trade',flush=True)

    for statement in ("UPDATE trading.heat_rate_call_exercises SET energy_per_unit=1","DELETE FROM trading.heat_rate_call_trade_revisions",
                      "UPDATE trading.heat_rate_call_instruments SET strike=0"):
        try:query(statement+';')
        except RuntimeError:pass
        else:raise AssertionError('A retained heat-rate call fact was rewritten: '+statement)
    context=runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-15',NULL);")
    selected=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','2020-12-15T23:59:59.999999Z');"
    read=lambda trades='NULL',actor='trader':json.loads(runtime(selected+f"SELECT security.read_heat_rate_call_positions('{catalog}',ARRAY[]::uuid[],ARRAY[]::uuid[],{trades});",actor))
    held=read()
    assert sorted(row['head']['trade_id'] for row in held)==sorted([first['tradeId'],third['tradeId']]),held
    one=read(f"ARRAY['{first['tradeId']}']::uuid[]")
    assert len(one)==1
    head,retained,rows=one[0]['head'],one[0]['terms'],one[0]['exercises']
    assert(head['quantity'],head['book_id'],head['reason'],head['instrument_id'],head['legal_entity'],head['counterparty'])==\
        ('50',body['bookId'],'Synthetic toll option',prepared['instrumentId'],'Synthetic entity 1','Synthetic party 1'),head
    assert(retained['heat_rate'],retained['strike'],retained['power_curve_code'],retained['fuel_curve_code'],retained['power_quote_code'],retained['fuel_quote_code'],
        retained['currency_code'],retained['delivery_from'],retained['delivery_to'],retained['exercise_local_cutoff'],retained['exercise_lead_business_days'],
        retained['hours_membership'],retained['payment_rule']['days'],retained['projection_curves'])==\
        ('7.5','3','local_power','local_gas','test_power_outright','test_gas_outright','usd','2021-02-01','2021-02-04','15:00',1,'all',10,
         ['local_power','local_gas']),retained
    assert[(row['ordinal'],row['delivery_day'],row['energy_per_unit'],row['power_contract_id'],row['fuel_contract_id']) for row in rows]==\
        [(n,f'2021-02-0{n+1}','24',POWER,FUEL) for n in range(3)],rows
    assert rows[0]['exercise_at'].startswith('2021-01-29T15:00:00') and rows[2]['payment_at'].startswith('2021-03-10T00:00:00'),rows
    try:runtime(f"SELECT security.read_heat_rate_call_positions('{catalog}',ARRAY[]::uuid[],ARRAY[]::uuid[],NULL);")
    except RuntimeError as error:assert 'knowledge_coverage' in str(error),str(error)
    else:raise AssertionError('Read heat-rate call positions without a selected knowledge context')
    earlier=runtime("SELECT security.capture_date_context('"+catalog+"','2020-12-14',NULL);")
    before_booking=f"SELECT security.use_market_context('{catalog}','{earlier}');SELECT security.use_trading_context('{catalog}','2020-12-14T23:59:59.999999Z');"
    assert json.loads(runtime(before_booking+f"SELECT security.read_heat_rate_call_positions('{catalog}',ARRAY[]::uuid[],ARRAY[]::uuid[],NULL);"))==[]
    selected_trade=f"SELECT security.read_heat_rate_call_positions('{catalog}',ARRAY[]::uuid[],ARRAY[]::uuid[],ARRAY['{first['tradeId']}']::uuid[]);"
    assert read_trading_workspace(query,runtime,catalog,'2020-12-15T23:59:59.999999Z',selected_trade)==one
    assert read_trading_workspace(query,runtime,catalog,'2020-12-14T23:59:59.999999Z',selected_trade)==[]
    print('PASS heat-rate call storage: positions return the retained terms and exercises under a selected context, and nothing before the booking instant',flush=True)


if __name__=='__main__':
    main(checks=run_checks,fixture_factory=lambda root:fixture(root))
