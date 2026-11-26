"""Synthetic native operating-right admission in an owned disposable database."""
import json
from uuid import uuid4


def run_checks(query, manifest, psql=None, environment=None):
    catalog = manifest['catalogId']
    from preview_fixture import trade_booking, trading_fixture
    query(trading_fixture(catalog))
    passed = 0
    def check(label, condition=True):
        nonlocal passed
        assert condition, label
        passed += 1
        print('PASS operating trades: ' + label, flush=True)
    check('a read grant made by hand before the migration is kept, and the migration installs the rest',
        query("SELECT count(*) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id "
              "WHERE p.permission='ReadPhysical' AND r.code IN('risk_analyst','trader');")=='2')
    def runtime(sql, actor='trader'):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','{actor}','human_interactive','{manifest['tenantId']}',"
            f"'{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return ('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'
                f"SELECT security.use_context('{token}');" + sql + 'COMMIT;')
    def reject(sql, code, auth=False, actor='trader'):
        try:
            query(runtime(sql, actor) if auth else sql)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Accepted invalid operating booking: ' + code)
    def literal(v):
        return "'" + json.dumps(v).replace("'", "''") + "'::jsonb"
    reject(f"SELECT security.book_operating_right('{catalog}','{{}}'::jsonb,'{{}}'::jsonb,'2020-12-15T12:00:00Z');", 'operating_booking_input', True)
    check('malformed native command cannot book an operating right')
    payment=query("SELECT trading.operating_payment_at('2041-01-01T00:00:00Z',18);")
    check('native monthly payment uses the last calendar day plus the selected lag',payment.startswith('2041-02-18 00:00:00'))
    params = dict(minimumOutputMw='2',maximumOutputMw='10',rampUpMwPerHour='10',rampDownMwPerHour='10',startupMaximumOutputMw='5',shutdownMaximumOutputMw='5',
        minimumUpMinutes=60,minimumDownMinutes=60,startupLeadMinutes=0,maximumStarts=4,consumptionHeatRate='8',consumptionNoLoadFuelPerHour='1',consumptionStartupFuel='2')
    terms=dict(kind='thermal',definitionCatalogId=catalog,definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],
        referenceRevisionId=manifest['referenceRevisionId'],facilityCode='test_facility_a',clockCode='test_utc',intervalMinutes=60,powerQuantityCode='test_power_megawatt',
        energyQuantityCode='test_power_quantity',fuelQuantityCode='test_gas_quantity',battery=None,thermal=params,solar=None,implementation='structured_power_facility_v1')
    facility=dict(id=str(uuid4()),version=1,effectiveFrom='2020-01-01T00:00:00Z',effectiveTo='2022-01-01T00:00:00Z',terms=terms)
    query(runtime(f"SELECT physical.save_power_operating_facility('{catalog}','{uuid4()}',0,{literal(facility)});", 'editor'))
    plan=dict(snapshotId=str(uuid4()),facilityId=facility['id'],facilityVersion=1,grid=dict(operatingDate='2021-01-01',firstInterval=1),
        batteryState=None,batteryDispatch=[],solarRight=None,solarDispatch=[],thermalState=dict(at='2021-01-01T00:00:00Z',commitment='offline',elapsedMinutes=60,
        previousOutputMw='0',startsUsed=0,guaranteedHeatRate='7',guaranteedNoLoadFuelPerHour='0',guaranteedStartupFuel='0',terminalPolicy='require_minimum_times_satisfied',numericalPolicy='thermal_exact_elapsed_quantity_v1'),
        thermalDispatch=[dict(commitment='online',outputMw='5',availableMw='10',startupInstructionAt='2021-01-01T00:00:00Z')],valuation=dict(fuelQuantityBasis='guaranteed'))
    def evaluate(p=plan):
        return f"SELECT trading.operating_plan_admission(f,{literal(p)},'2020-12-15T12:00:00Z') FROM physical.power_operating_facility_versions f WHERE(catalog_id,facility_id,version)=('{catalog}','{facility['id']}',1);"
    rows=json.loads(query(evaluate()))
    check('independent admission derives five MWh and thirty-five guaranteed fuel units',
        {r['name']:(r['numerator'],r['denominator']) for r in rows}=={'energy':('5','1'),'fuel':('35','1')})
    changed=json.loads(json.dumps(plan));changed['valuation']['fuelQuantityBasis']='consumption'
    check('actual consumption coefficients stay distinct from contract-guaranteed fuel', next(r for r in json.loads(query(evaluate(changed))) if r['name']=='fuel')['numerator']=='43')
    changed=json.loads(json.dumps(plan));changed['thermalDispatch'][0]['outputMw']='6'
    reject(evaluate(changed),'operating_thermal_start');check('startup output limits are enforced independently of managed preparation')
    changed=json.loads(json.dumps(plan));changed['thermalState']['elapsedMinutes']=59
    reject(evaluate(changed),'operating_thermal_start');check('minimum downtime survives the initial-state boundary')
    changed=json.loads(json.dumps(plan));changed['thermalDispatch'][0]['availableMw']='4'
    reject(evaluate(changed),'operating_thermal_output');check('available capacity is an explicit per-interval limit')
    changed=json.loads(json.dumps(plan));changed['thermalDispatch'].append(dict(commitment='online',outputMw='10',availableMw='10',startupInstructionAt=None))
    result=json.loads(query(evaluate(changed)));check('ordered native intervals integrate exact elapsed energy',sum(int(r['numerator']) for r in result if r['name']=='energy')==15)
    changed=json.loads(json.dumps(plan));changed['thermalDispatch'][0]['startupInstructionAt']=None
    reject(evaluate(changed),'operating_thermal_start');check('a start cannot silently acquire an instruction timestamp')
    # The same admission boundary checks exact battery storage, losses and terminal policy.
    battery=json.loads(json.dumps(facility));battery['id']=str(uuid4());battery['terms'].update(kind='battery',intervalMinutes=15,fuelQuantityCode=None,thermal=None,
        battery=dict(maximumChargeMw='10',maximumDischargeMw='10',minimumStoredMwh='0',maximumStoredMwh='20',chargeEfficiency='0.9',dischargeEfficiency='0.9',
        meterBoundary='grid_side_ac',lossPolicy='zero_auxiliary_and_self_discharge',capacityPolicy='fixed',simultaneousPolicy='prohibited',chargingOrigin='grid',
        throughputPolicy='grid_charge_plus_discharge',maximumThroughputMwh='25'))
    query(runtime(f"SELECT physical.save_power_operating_facility('{catalog}','{uuid4()}',0,{literal(battery)});", 'editor'))
    bp=dict(snapshotId=str(uuid4()),facilityId=battery['id'],facilityVersion=1,grid=dict(operatingDate='2021-01-01',firstInterval=1),
        batteryState=dict(at='2021-01-01T00:00:00Z',storedMwh='0',cumulativeGridThroughputMwh='0',terminalMinimumMwh='0',terminalMaximumMwh='0'),
        batteryDispatch=[dict(chargeMw='10',dischargeMw='0',availablePowerFraction='1'),dict(chargeMw='0',dischargeMw='8.1',availablePowerFraction='1')],
        thermalState=None,thermalDispatch=[],solarRight=None,solarDispatch=[],valuation={})
    def selected_evaluate(p, selected):
        return f"SELECT trading.operating_plan_admission(f,{literal(p)},'2020-12-15T12:00:00Z') FROM physical.power_operating_facility_versions f WHERE(catalog_id,facility_id,version)=('{catalog}','{selected}',1);"
    br=json.loads(query(selected_evaluate(bp,battery['id'])))
    check('quarter-hour battery storage uses both efficiencies and exact elapsed energy',
        [(r['numerator'],r['denominator']) for r in br if r['name']=='energy']==[('0','1'),('81','40')])
    changed=json.loads(json.dumps(bp));changed['batteryDispatch'][1]['dischargeMw']='1'
    reject(selected_evaluate(changed,battery['id']),'operating_battery_terminal');check('terminal battery inventory cannot become an ignored input')
    changed=json.loads(json.dumps(bp));changed['batteryDispatch'][0]['dischargeMw']='1'
    reject(selected_evaluate(changed,battery['id']),'operating_battery_limit');check('simultaneous charge and discharge is refused')
    changed=json.loads(json.dumps(bp));changed['batteryState']['cumulativeGridThroughputMwh']='24'
    reject(selected_evaluate(changed,battery['id']),'operating_battery_state');check('retained cumulative throughput consumes the facility cap')
    changed=json.loads(json.dumps(bp));changed['batteryDispatch'][0]['chargeMw']=10
    reject(selected_evaluate(changed,battery['id']),'operating_booking_number');check('raw numeric scalars cannot replace required exact decimal strings')
    solar=json.loads(json.dumps(facility));solar['id']=str(uuid4());solar['terms'].update(kind='solar',powerQuantityCode=None,fuelQuantityCode=None,thermal=None,solar=dict(stage='delivered'))
    query(runtime(f"SELECT physical.save_power_operating_facility('{catalog}','{uuid4()}',0,{literal(solar)});", 'editor'))
    sp=dict(snapshotId=str(uuid4()),facilityId=solar['id'],facilityVersion=1,grid=dict(operatingDate='2021-01-01',firstInterval=1),batteryState=None,batteryDispatch=[],thermalState=None,thermalDispatch=[],
        solarRight=dict(forecastAssumption='hypothetical_supplied_forecast_v1',entitlementNumerator='1',entitlementDenominator='2',deliveryBasis='as_produced',paymentBasis='produced_only'),
        solarDispatch=[dict(productionMwh='10',requiredDeliveryMwh=None)],valuation={})
    sr=json.loads(query(selected_evaluate(sp,solar['id'])))
    check('solar entitlement retains exact five MWh physical and payment quantities',all(r['numerator']=='5' and r['denominator']=='1' for r in sr))
    changed=json.loads(json.dumps(sp));changed['solarRight']['deliveryBasis']='fixed_profile'
    reject(selected_evaluate(changed,solar['id']),'operating_booking_policy');check('unpriced solar firming is refused in the booked profile')
    changed=json.loads(json.dumps(plan));changed['thermalState']['at']='2020-12-31T19:00:00-05:00'
    reject(evaluate(changed),'operating_booking_grid');check('raw boundary state requires explicit UTC notation')
    # The complete command deliberately has no staged programs. Scalar admission
    # must distinguish malformed financial inputs before reaching that boundary.
    command=dict(operationId=str(uuid4()),tradeId=None,expectedRevision=0,
        bookId='d1000000-0000-0000-0000-000000000001',**trade_booking(),
        reason='Synthetic native scalar admission',convention='supplied_operating_right_trade_v1',plan=json.loads(json.dumps(plan)))
    command['plan']['valuation']=dict(perspective='resource_right_holder_v1',measure='remaining_operating_value_v1',
        currencyCode='usd',rightQuantityCode='test_resource_right_quantity',discounting=False,
        paymentRule=dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',
            shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1'),
        energyPrice=dict(mode='fixed',quoteCode='test_power_outright',fixedPrice='100',curveCode=None,contractId=None),
        fuelPrice=dict(mode='fixed',quoteCode='test_gas_outright',fixedPrice='10',curveCode=None,contractId=None),
        chargingPrice=None,fuelQuantityBasis='guaranteed',variablePrice=None,variableQuantityBasis=None,
        accessPrice=None,accessQuoteCode=None,accessFeeBasis=None,startupPrice=None,startupQuoteCode=None,startupFeeBasis=None)
    prepared=dict(instrumentId=str(uuid4()),members=[dict(slot=0,name='energy_output'),dict(slot=1,name='fuel_input')])
    def book(body):
        return f"SELECT security.book_operating_right('{catalog}',{literal(body)},{literal(prepared)},'2020-12-15T12:00:00Z');"
    reject(book(command),'operating_schedule',True)
    check('a complete financial command reaches the explicit missing-stage boundary')
    for actor in ('risk','editor','viewer','approver','admin'):
        reject(book(command),'operating_booking_denied',True,actor)
    check('only the installed booking role reaches operating-right admission')
    without_read=("BEGIN;DELETE FROM security.role_permissions WHERE permission='ReadPhysical' AND role_id='a0000000-0000-0000-0000-000000000002';"
        +runtime(book(command)).removeprefix('BEGIN;').removesuffix('COMMIT;')+'ROLLBACK;')
    reject(without_read,'operating_facility_unavailable')
    check('a booker without Physical read authority cannot select an operating facility')
    mutations=[('fixed price',lambda v:v['energyPrice'].update(fixedPrice=100)),
        ('variable fee',lambda v:v.update(variablePrice=1,variableQuantityBasis='energy_output')),
        ('access fee',lambda v:v.update(accessPrice=2,accessQuoteCode='test_resource_right_quote',accessFeeBasis='preview_horizon_once_v1')),
        ('currency reference',lambda v:v.update(currencyCode=1)),
        ('right quantity reference',lambda v:v.update(rightQuantityCode=1)),
        ('access quote reference',lambda v:v.update(accessPrice='2',accessQuoteCode=1,accessFeeBasis='preview_horizon_once_v1')),
        ('price mode',lambda v:v['energyPrice'].update(mode=1)),
        ('price quote reference',lambda v:v['energyPrice'].update(quoteCode=1)),
        ('flat curve reference',lambda v:v['energyPrice'].update(mode='selected_contract_flat_v1',fixedPrice=None,curveCode=1,contractId=str(uuid4()))),
        ('flat contract reference',lambda v:v['energyPrice'].update(mode='selected_contract_flat_v1',fixedPrice=None,curveCode='local_power',contractId=1)),
        ('unknown price field',lambda v:v['energyPrice'].update(fallback=None))]
    for label,mutate in mutations:
        malformed=json.loads(json.dumps(command));mutate(malformed['plan']['valuation'])
        reject(book(malformed),'operating_booking_price',True)
        check('closed '+label+' shape refuses before schedule staging')
    reject(f"SET SESSION AUTHORIZATION preview_runtime;SELECT * FROM trading.power_operating_instruments;",'permission denied for table power_operating_instruments');check('raw operating instruments have no runtime table bypass')
    from copy import deepcopy
    composed=deepcopy(command)
    financial=composed['plan']['valuation']
    def side(key,source,price,coefficient,days):
        return dict(key=key,name='Synthetic '+key,quantitySource=source,coefficient=coefficient,kind='payment',price=deepcopy(price),paymentRule=dict(financial['paymentRule'],days=days))
    composed['plan']['valuation']=None
    composed['plan']['composition']=dict(implementation='operating_side_composition_v2',perspective=financial['perspective'],measure=financial['measure'],
        currencyCode=financial['currencyCode'],rightQuantityCode=financial['rightQuantityCode'],discounting=False,sides=[
        side('sale','energy_output',financial['energyPrice'],'1',18),side('fuel','guaranteed_fuel',financial['fuelPrice'],'-1',25),
        side('fee','energy_output',dict(financial['energyPrice'],fixedPrice='2'),'-1',7)])
    reject(book(composed),'operating_schedule',True)
    check('explicit repeated-source Side composition reaches the missing-stage boundary without inventing slots or signs')
    result=json.loads(query(evaluate(composed['plan'])))
    check('one thermal plan retains distinct guaranteed and consumption fuel sources for composed Sides',
        {r['name']:r['numerator'] for r in result}=={'energy':'5','guaranteed_fuel':'35','consumption_fuel':'43'})
    for label,mutate in [
        ('duplicate Side key',lambda v:v['sides'][1].update(key='sale')),
        ('undeclared Side coefficient',lambda v:v['sides'][0].update(coefficient='0')),
        ('missing payment rule',lambda v:v['sides'][0].update(paymentRule=None)),
        ('incompatible source',lambda v:v['sides'][0].update(quantitySource='charging_energy')),
        ('extra contract field',lambda v:v.update(fallback='0'))]:
        malformed=deepcopy(composed);mutate(malformed['plan']['composition'])
        reject(book(malformed),'operating_composition',True);check('native composition rejects '+label)
    malformed=deepcopy(composed);malformed['plan']['composition']['sides'][0]['name']='\t'
    reject(book(malformed),'operating_composition',True)
    check('native Side labels refuse whitespace-only names like managed authoring')
    for name,valid in [('Synthetic output',True),('\u00a0Synthetic output',False),('Synthetic output\u3000',False),('\U0001f31e'*40,True),('\U0001f31e'*41,False)]:
        assert query(f"SELECT trading.operating_side_name_valid('{name}');")==('t' if valid else 'f')
    check('native Side labels preserve Unicode and match managed whitespace/UTF-16 length bounds')
    return passed


def before_migration(query,migration):
    if migration.name=='0389_operating_role_grants.sql':
        # An installation may already have granted the read capability by hand; the migration keeps that grant.
        query("INSERT INTO security.role_permissions SELECT id,'ReadPhysical' FROM security.roles WHERE code='risk_analyst';")
    if migration.name=='0356_operating_side_composition.sql':
        from check_fact_anchor_upgrade import run_checks
        run_checks(query,migration,number='0356',guard='operating_composition_upgrade',signatures=(
            'trading.operating_plan_admission(physical.power_operating_facility_versions,jsonb,timestamp with time zone)',
            'trading.require_operating_program_shape(uuid,uuid,integer,uuid)',
            'security.book_operating_right(uuid,jsonb,jsonb,timestamp with time zone)',
            'trading.request_side_calendar_policy(jsonb,integer)'))


if __name__ == '__main__':
    from check_reference_editing import main
    main(checks=run_checks,before_migration=before_migration)
