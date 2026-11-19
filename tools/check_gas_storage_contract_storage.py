#!/usr/bin/env python3
"""Independent native gas storage contract booking, plan revision and position-read checks."""
from copy import deepcopy
from uuid import uuid4
import json
from check_reference_editing import main
from preview_fixture import fixture,identity,trading_fixture,trade_booking

BOOKED_AT,LATER='2020-12-15T16:00:00Z','2020-12-16T16:00:00Z'
# February 2021 on the synthetic curves: the third contract of a month is local gas.
GAS=identity('5',8)


def gas_day(day):
    return dict(gasDay=f'2021-02-0{day}',startsAt=f'2021-02-0{day}T09:00:00.000000Z',endsAt=f'2021-02-0{day+1}T09:00:00.000000Z',
        paymentAt='2021-03-20T00:00:00.000000Z',injectionContractId=GAS,withdrawalContractId=GAS)


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
        else:raise AssertionError('Accepted an invalid native gas storage operation: expected '+code)
    literal=lambda value:json.dumps(value).replace("'","''")
    def book(body,prepared):
        return f"SELECT security.book_gas_storage('{catalog}','{literal(body)}'::jsonb,'{literal(prepared)}'::jsonb,'{BOOKED_AT}');"
    record=lambda body,at=BOOKED_AT:f"SELECT security.record_gas_storage_plan('{catalog}','{literal(body)}'::jsonb,'{at}');"
    counts=lambda:query("SELECT concat_ws('/',(SELECT count(*) FROM trading.trades),(SELECT count(*) FROM trading.gas_storage_contracts),(SELECT count(*) FROM trading.gas_storage_ratchets),"
        "(SELECT count(*) FROM trading.gas_storage_days),(SELECT count(*) FROM trading.gas_storage_plans),(SELECT count(*) FROM trading.gas_storage_plan_days),"
        "(SELECT count(*) FROM trading.gas_storage_requests));")

    reject(f"SELECT security.gas_storage_request_status('{catalog}','{uuid4()}');",'42501','viewer')
    query(trading_fixture(catalog))
    assert query("SELECT assistant.is_prompt_page('blotter_new_gas_storage') AND assistant.is_prompt_page('blotter_new_heat_rate_call');")=='t'
    assert runtime(f"SELECT security.gas_storage_request_status('{catalog}','{uuid4()}');") in ('','null')
    for table in ('gas_storage_contracts','gas_storage_ratchets','gas_storage_days','gas_storage_plans'):
        reject(f'SELECT count(*) FROM trading.{table};','permission denied')
    print('PASS gas storage storage: receipts need trade authority and the retained tables are closed to the runtime role',flush=True)

    band=lambda lower,upper,injection,withdrawal:dict(lower=lower,upper=upper,maximumDailyInjection=injection,maximumDailyWithdrawal=withdrawal)
    terms=dict(definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        quantity='test_gas_quantity',injectionCurve='local_gas',withdrawalCurve='local_gas',minimumInventory='0',maximumInventory='100000',
        ratchets=[band('0','30000','2000','500'),band('30000','70000','1500','1000'),band('70000','100000','1000','1500')],
        injectionFuel='0.01',withdrawalFuel='0',ratchetBasis='gas_day_opening_inventory_band_v1',flowPolicy='single_direction_per_gas_day_v1',
        fuelBasis='in_kind_fuel_at_storage_point_v1',openingInventory='29000',terminalMinimum='0',terminalMaximum='100000',
        injectionCharge='0.02',withdrawalCharge='0.03',monthlyCapacityCharge='0.005',termFrom='2021-02-01',termTo='2021-02-04',clock='test_utc',dayStartMinute=540,
        paymentRule=dict(frequency='monthly',rule='days_after_period_end',days=20,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,
            adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1'))
    body=dict(operationId=str(uuid4()),convention='gas_storage_trade_v1',bookId='d1000000-0000-0000-0000-000000000001',**trade_booking(),
        reason='Synthetic storage contract',terms=terms)
    prepared=dict(instrumentId=str(uuid4()),days=[gas_day(1),gas_day(2),gas_day(3)])
    before=counts()
    def refuse(code,change,target='body'):
        changed_body,changed_prepared=deepcopy(body),deepcopy(prepared)
        change(changed_body if target=='body' else changed_prepared)
        reject(book(changed_body,changed_prepared),code)
        assert counts()==before,'A refused booking left facts behind: '+code
    refuse('gas_storage_input',lambda b:b.update(defaultPlan='idle'))
    refuse('gas_storage_input',lambda b:b.update(convention='another_trade_v1'))
    refuse('gas_storage_input',lambda b:b['terms'].update(maximumInventory=100000))
    refuse('gas_storage_input',lambda b:b['terms'].pop('withdrawalFuel'))
    refuse('gas_storage_input',lambda b:b['terms'].update(ratchets=[]))
    refuse('gas_storage_input',lambda b:b['terms']['ratchets'][0].update(interpolation='linear'))
    refuse('gas_storage_input',lambda b:b['terms'].update(dayStartMinute=1440))
    refuse('gas_storage_input',lambda b:b.update(reason=' '))
    print('PASS gas storage storage: an open, mistyped or incomplete command creates nothing',flush=True)
    refuse('gas_storage_policy',lambda b:b['terms'].update(ratchetBasis='interpolated_v1'))
    refuse('gas_storage_policy',lambda b:b['terms'].update(flowPolicy='netted_v1'))
    refuse('gas_storage_policy',lambda b:b['terms'].update(fuelBasis='cash_fuel_v1'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(openingInventory='100001'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(terminalMinimum='5',terminalMaximum='4'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(injectionFuel='1'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(withdrawalCharge='-0.01'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(termTo='2021-02-05'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(withdrawalCurve='local_power'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(injectionCurve='gas_basis',withdrawalCurve='gas_basis'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(quantity='test_power_quantity'))
    refuse('gas_storage_terms',lambda b:b['terms'].update(clock='absent_clock'))
    refuse('gas_storage_ratchets',lambda b:b['terms']['ratchets'].pop(1))
    refuse('gas_storage_ratchets',lambda b:b['terms']['ratchets'].pop(2))
    refuse('gas_storage_ratchets',lambda b:b['terms']['ratchets'][0].update(lower='1000'))
    refuse('gas_storage_ratchets',lambda b:b['terms']['ratchets'][1].update(maximumDailyInjection='-1'))
    refuse('42501',lambda b:b['terms'].update(definitionRevisionId=str(uuid4())))
    print('PASS gas storage storage: unselected policies, incoherent capacity, uncovered ratchet bands and unknown reference terms refuse',flush=True)
    refuse('gas_storage_input',lambda p:p['days'][0].update(defaultPrice='0'),'prepared')
    refuse('gas_storage_day',lambda p:p['days'].reverse(),'prepared')
    refuse('gas_storage_day',lambda p:p['days'][1].update(startsAt='2021-02-02T10:00:00.000000Z',endsAt='2021-02-03T10:00:00.000000Z'),'prepared')
    refuse('gas_storage_day',lambda p:p['days'][2].update(endsAt='2021-02-04T12:00:00.000000Z'),'prepared')
    refuse('gas_storage_day',lambda p:p['days'][1].update(paymentAt='2021-03-21T00:00:00.000000Z'),'prepared')
    refuse('gas_storage_day',lambda p:p['days'][2].update(paymentAt='2021-02-03T00:00:00.000000Z'),'prepared')
    refuse('gas_storage_contract',lambda p:p['days'][0].update(injectionContractId=identity('5',13)),'prepared')
    refuse('gas_storage_contract',lambda p:p['days'][2].update(withdrawalContractId=identity('5',10)),'prepared')
    started=deepcopy(body);started['terms'].update(termFrom='2020-12-15',termTo='2020-12-16')
    reject(book(started,dict(prepared,days=[dict(gas_day(1),gasDay='2020-12-15',startsAt='2020-12-15T09:00:00.000000Z',endsAt='2020-12-16T09:00:00.000000Z')])),'gas_storage_day')
    print('PASS gas storage storage: gas days must be the whole term in order, contiguous, ahead of booking, paid once per month and on the delivering contracts',flush=True)

    reject(book(body,prepared),'42501','viewer')
    assert counts()==before
    first=json.loads(runtime(book(body,prepared)))
    assert first['changed'] is True and first['revision']==1 and first['instrumentId']==prepared['instrumentId'] and first['operationId']==body['operationId'],first
    booked=counts()
    assert json.loads(runtime(book(body,prepared)))==first and counts()==booked
    assert json.loads(runtime(f"SELECT security.gas_storage_request_status('{catalog}','{body['operationId']}');"))==first
    assert json.loads(runtime(f"SELECT security.gas_storage_request_replay('{catalog}','{literal(body)}'::jsonb);"))==first
    changed=deepcopy(body);changed['terms']['maximumInventory']='90000'
    reject(book(changed,prepared),'gas_storage_request_conflict')
    again=deepcopy(body);again.update(operationId=str(uuid4()),reason='Entered twice');again['terms'].update(injectionFuel='0.010',maximumInventory='100000.0')
    second=json.loads(runtime(book(again,dict(prepared,instrumentId=str(uuid4())))))
    assert second=={**first,'operationId':again['operationId'],'changed':False},second
    assert query('SELECT count(*) FROM trading.gas_storage_trade_revisions;')=='1'
    print('PASS gas storage storage: one booking retains the contract, a retried command returns its first outcome and the same fact is not booked twice',flush=True)

    flow=lambda day,injection,withdrawal:dict(gasDay=f'2021-02-0{day}',injection=injection,withdrawal=withdrawal)
    plan=dict(operationId=str(uuid4()),convention='gas_storage_plan_v1',tradeId=first['tradeId'],expectedRevision=0,reason='Synthetic desk plan',
        source=dict(kind='supplied',inventoryStep=None),days=[flow(1,'2000','0'),flow(2,'1500','0'),flow(3,'0','1000')])
    held=counts()
    def refuse_plan(code,change):
        changed_plan=deepcopy(plan);change(changed_plan)
        reject(record(changed_plan),code)
        assert counts()==held,'A refused plan left facts behind: '+code
    reject(record(plan),'42501','viewer')
    refuse_plan('gas_storage_input',lambda p:p.update(assumedIdle=True))
    refuse_plan('gas_storage_input',lambda p:p['days'][0].update(injection=2000))
    refuse_plan('gas_storage_input',lambda p:p['source'].update(kind='another_search_v1'))
    refuse_plan('gas_storage_input',lambda p:p['source'].update(inventoryStep='500'))
    refuse_plan('gas_storage_input',lambda p:p['source'].update(kind='gas_storage_inventory_lattice_search_v1'))
    refuse_plan('gas_storage_plan',lambda p:p['days'].pop())
    refuse_plan('gas_storage_plan',lambda p:p['days'].reverse())
    refuse_plan('gas_storage_plan',lambda p:p['days'][0].update(withdrawal='1'))
    refuse_plan('gas_storage_plan',lambda p:p['days'][1].update(injection='-1'))
    refuse_plan('gas_storage_plan_stale',lambda p:p.update(expectedRevision=1))
    refuse_plan('42501',lambda p:p.update(tradeId=str(uuid4())))
    recorded=json.loads(runtime(record(plan)))
    assert recorded['changed'] is True and recorded['revision']==1 and recorded['tradeId']==first['tradeId'],recorded
    assert json.loads(runtime(record(plan)))==recorded
    same=dict(deepcopy(plan),operationId=str(uuid4()),expectedRevision=7);same['days'][0].update(injection='2000.0')
    assert json.loads(runtime(record(same)))=={**recorded,'operationId':same['operationId'],'changed':False}
    revised=dict(deepcopy(plan),operationId=str(uuid4()),expectedRevision=1,source=dict(kind='gas_storage_inventory_lattice_search_v1',inventoryStep='990'),
        days=[flow(1,'1000','0'),flow(2,'0','0'),flow(3,'0','990')])
    reject(record(dict(revised,expectedRevision=0)),'gas_storage_plan_stale')
    reject(record(revised,'2020-12-14T16:00:00Z'),'42501')
    second_plan=json.loads(runtime(record(revised,LATER)))
    assert second_plan['changed'] is True and second_plan['revision']==2,second_plan
    reject(record(dict(deepcopy(plan),operationId=str(uuid4()),expectedRevision=2)),'gas_storage_plan_time')
    assert query('SELECT count(*) FROM trading.gas_storage_plans;')=='2' and query('SELECT count(*) FROM trading.gas_storage_plan_days;')=='6'
    print('PASS gas storage storage: a plan covers every gas day one way, replays its outcome, is not recorded twice and revises only from its expected head, never before it',flush=True)

    for statement in ("UPDATE trading.gas_storage_ratchets SET maximum_daily_injection=1e9","DELETE FROM trading.gas_storage_plan_days",
                      "UPDATE trading.gas_storage_contracts SET maximum_inventory=1e12","UPDATE trading.gas_storage_plans SET flows='[]'"):
        try:query(statement+';')
        except RuntimeError:pass
        else:raise AssertionError('A retained gas storage fact was rewritten: '+statement)
    def read(day,trades='NULL',actor='trader'):
        context=runtime(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
        return json.loads(runtime(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{day}T23:59:59.999999Z');"
            f"SELECT security.read_gas_storage_positions('{catalog}',ARRAY[]::uuid[],ARRAY[]::uuid[],{trades});",actor))
    one=read('2020-12-15',f"ARRAY['{first['tradeId']}']::uuid[]")
    assert len(one)==1
    head,retained,bands,days,in_force=one[0]['head'],one[0]['terms'],one[0]['ratchets'],one[0]['days'],one[0]['plan']
    assert(head['quantity'],head['book_id'],head['reason'],head['instrument_id'],head['legal_entity'],head['counterparty'])==\
        ('1',body['bookId'],'Synthetic storage contract',prepared['instrumentId'],'Synthetic entity 1','Synthetic party 1'),head
    assert(retained['quantity_code'],retained['quote_code'],retained['currency_code'],retained['minimum_inventory'],retained['maximum_inventory'],retained['injection_fuel'],
        retained['withdrawal_fuel'],retained['opening_inventory'],retained['terminal_minimum'],retained['terminal_maximum'],retained['injection_charge'],
        retained['withdrawal_charge'],retained['monthly_capacity_charge'],retained['term_from'],retained['term_to'],retained['clock_code'],retained['day_start_minute'],
        retained['payment_rule']['days'],retained['projection_curves'])==\
        ('test_gas_quantity','test_gas_outright','usd','0','100000','0.01','0','29000','0','100000','0.02','0.03','0.005','2021-02-01','2021-02-04','test_utc',540,20,['local_gas']),retained
    assert bands==[dict(lower=lower,upper=upper,maximum_daily_injection=injection,maximum_daily_withdrawal=withdrawal) for lower,upper,injection,withdrawal in
        (('0','30000','2000','500'),('30000','70000','1500','1000'),('70000','100000','1000','1500'))],bands
    assert[(row['ordinal'],row['gas_day'],row['injection_contract_id'],row['withdrawal_contract_id']) for row in days]==[(n,f'2021-02-0{n+1}',GAS,GAS) for n in range(3)],days
    assert days[0]['starts_at'].startswith('2021-02-01T09:00:00') and days[2]['ends_at'].startswith('2021-02-04T09:00:00') and days[1]['payment_at'].startswith('2021-03-20T00:00:00'),days
    assert(in_force['revision'],in_force['source'],in_force['inventory_step'],in_force['flows'])==(1,'supplied',None,[['2000','0'],['1500','0'],['0','1000']]),in_force
    later=read('2020-12-16')[0]['plan']
    assert(later['revision'],later['source'],later['inventory_step'],later['flows'])==(2,'gas_storage_inventory_lattice_search_v1','990',[['1000','0'],['0','0'],['0','990']]),later
    try:runtime(f"SELECT security.read_gas_storage_positions('{catalog}',ARRAY[]::uuid[],ARRAY[]::uuid[],NULL);")
    except RuntimeError as error:assert 'knowledge_coverage' in str(error),str(error)
    else:raise AssertionError('Read gas storage positions without a selected knowledge context')
    assert read('2020-12-14')==[]
    print('PASS gas storage storage: positions return the contract, ratchets, gas days and the plan in force under the selected context, and nothing before the booking instant',flush=True)


if __name__=='__main__':
    main(checks=run_checks,fixture_factory=lambda root:fixture(root))
