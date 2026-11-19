"""Exact supplied-plan operating previews against an isolated synthetic server."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from exact_values import fraction
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace'), '2020-12-15')['id']
    route = 'physical/power-facilities/'
    catalog = client.call(route + 'catalog', {'snapshotId': frame})
    assert catalog['canSave'] and catalog['canPreview'], 'The selected synthetic editor must have operating-facility authority.'

    def one(rows, code):
        selected = [row for row in rows if row['code'] == code]
        assert len(selected) == 1, f'Expected the exact synthetic reference {code}.'
        return selected[0]

    power = one(catalog['powerQuantities'], 'test_power_megawatt')
    energy = one(catalog['energyQuantities'], 'test_power_quantity')
    right = one(catalog['rightQuantities'], 'test_resource_right_quantity')
    one(catalog['locations'], 'test_facility_a')
    one(catalog['clocks'], 'test_utc')
    quote = one(catalog['quotes'], 'test_power_outright')
    assert any(row['curveCode'] == 'local_power' and row['quoteCode'] == quote['code']
               for row in catalog['priceContracts'])
    terms = dict(catalog['pins'], kind='battery', facilityCode='test_facility_a', clockCode='test_utc',
                 intervalMinutes=60, powerQuantityCode=power['code'], energyQuantityCode=energy['code'],
                 fuelQuantityCode=None, battery=dict(maximumChargeMw='10', maximumDischargeMw='10',
                 minimumStoredMwh='0', maximumStoredMwh='10', chargeEfficiency='0.9', dischargeEfficiency='0.9',
                 meterBoundary='grid_side_ac', lossPolicy='zero_auxiliary_and_self_discharge', capacityPolicy='fixed',
                 simultaneousPolicy='prohibited', chargingOrigin='grid', throughputPolicy='uncapped', maximumThroughputMwh=None),
                 thermal=None, solar=None, implementation='structured_power_facility_v1')
    facility = dict(id=str(uuid4()), version=1, effectiveFrom='2031-01-01T00:00:00Z',
                    effectiveTo='2032-01-01T00:00:00Z', terms=terms)
    command = dict(snapshotId=frame, requestId=str(uuid4()), expectedVersion=0, facility=facility)
    receipt = client.call(route + 'save', command)
    assert receipt['changed'] and receipt['facility']['version'] == 1
    assert client.call(route + 'save', command) == receipt, 'An exact retry must return its original outcome.'
    replay_without_snapshot=deepcopy(command)
    replay_without_snapshot['snapshotId']=str(uuid4())
    assert client.call(route+'save',replay_without_snapshot)==receipt, 'An accepted command replays before fresh snapshot selection.'
    changed_retry=deepcopy(replay_without_snapshot)
    changed_retry['facility']['terms']['battery']['maximumChargeMw']='11'
    assert client.call(route+'save',changed_retry,expected=409)['code']=='power_facility_request_conflict'
    assert client.call(route + 'status', {'requestId': command['requestId']}) == receipt
    assert client.call(route + 'get', dict(snapshotId=frame, facilityId=facility['id'], version=1)) == receipt['facility']
    assert any(row['id'] == facility['id'] and row['version'] == 1 for row in client.call(route + 'list',
               dict(snapshotId=frame, selectionPolicy='all_known_versions_v1')))

    valuation = dict(perspective='resource_right_holder_v1', measure='remaining_operating_value_v1',
        currencyCode=quote['currencyCode'], rightQuantityCode=right['code'], discounting=False,
        paymentRule=dict(frequency='monthly', rule='days_after_period_end', days=18, dayOfMonth=None,
                         dayBasis='calendar_days', shortMonth=None, adjustment='unadjusted', calendar=None,
                         implementation=catalog['paymentImplementation']),
        energyPrice=dict(mode='fixed', quoteCode=quote['code'], fixedPrice='100', curveCode=None, contractId=None),
        chargingPrice=dict(mode='fixed', quoteCode=quote['code'], fixedPrice='20', curveCode=None, contractId=None),
        fuelPrice=None, fuelQuantityBasis=None, accessQuoteCode=None, accessPrice=None, accessFeeBasis=None,
        variablePrice=None, variableQuantityBasis=None, startupQuoteCode=None, startupPrice=None, startupFeeBasis=None)
    request = dict(snapshotId=frame, facilityId=facility['id'], facilityVersion=1,
        grid=dict(operatingDate='2031-01-01', firstInterval=1),
        batteryState=dict(at='2031-01-01T00:00:00Z', storedMwh='0', cumulativeGridThroughputMwh='0',
                          terminalMinimumMwh='0', terminalMaximumMwh='0'),
        batteryDispatch=[dict(chargeMw='10', dischargeMw='0', availablePowerFraction='1'),
                         dict(chargeMw='0', dischargeMw='8.1', availablePowerFraction='1')],
        thermalState=None, thermalDispatch=[], solarRight=None, solarDispatch=[], valuation=valuation)
    result = client.call(route + 'preview', request)
    assert fraction(result['valuation']['value']) == Fraction('8.1') * 100 - 10 * 20
    assert fraction(result['closingState']['quantities'][0]['quantity']) == 0
    assert [side['coefficient'] for side in result['valuation']['sides']] == ['1', '-1']
    assert all(row['evidenceId'] is None and row['knownAt'] is None
               for side in result['valuation']['sides'] for row in side['rows']), 'Manual scenarios have no fabricated publications.'
    repeated = client.call(route + 'preview', request)
    assert fraction(repeated['valuation']['value']) == fraction(result['valuation']['value']), 'Working reads may capture fresh knowledge but preserve the same numerical result.'
    assert result['contextId'], 'Every server calculation must identify its trusted captured context.'
    invalid = deepcopy(request)
    invalid['batteryDispatch'][0]['dischargeMw'] = '1'
    assert client.call(route + 'preview', invalid, expected=422)['code'] == 'battery_simultaneous'
    unknown = deepcopy(request)
    unknown['openingCostBasis'] = '10'
    assert client.call(route + 'preview', unknown, expected=400)['code'] == 'request_format'
    mismatch = deepcopy(command)
    mismatch.update(requestId=str(uuid4()), facility=deepcopy(facility))
    mismatch['facility']['id'] = str(uuid4())
    mismatch['facility']['terms']['referenceRevisionId'] = str(uuid4())
    assert client.call(route + 'save', mismatch, expected=422)['code'] == 'structured_power_pin'
    # Both other operating kinds use the same existing location identity and
    # separate explicitly selected operating-parameter extensions.
    thermal = deepcopy(facility)
    thermal.update(id=str(uuid4()))
    thermal['terms'].update(kind='thermal', battery=None, fuelQuantityCode='test_gas_quantity',
        thermal=dict(minimumOutputMw='0', maximumOutputMw='10', rampUpMwPerHour='100', rampDownMwPerHour='100',
                     startupMaximumOutputMw='10', shutdownMaximumOutputMw='10', minimumUpMinutes=0,
                     minimumDownMinutes=0, startupLeadMinutes=0, maximumStarts=3, consumptionHeatRate='1.5',
                     consumptionNoLoadFuelPerHour='0', consumptionStartupFuel='1'))
    client.call(route + 'save', dict(snapshotId=frame, requestId=str(uuid4()), expectedVersion=0, facility=thermal))
    thermal_request = deepcopy(request)
    thermal_request.update(facilityId=thermal['id'], batteryState=None, batteryDispatch=[],
        thermalState=dict(at='2031-01-01T00:00:00Z', commitment='offline', elapsedMinutes=60,
                          previousOutputMw='0', startsUsed=0, guaranteedHeatRate='2', guaranteedNoLoadFuelPerHour='1',
                          guaranteedStartupFuel='3', terminalPolicy='require_minimum_times_satisfied', numericalPolicy='thermal_exact_elapsed_quantity_v1'),
        thermalDispatch=[dict(commitment='online', outputMw='10', availableMw='10', startupInstructionAt='2031-01-01T00:00:00Z')])
    gas_quote = one(catalog['quotes'], 'test_gas_outright')
    thermal_request['valuation'].update(chargingPrice=None, fuelQuantityBasis='guaranteed',
        fuelPrice=dict(mode='fixed', quoteCode=gas_quote['code'], fixedPrice='10', curveCode=None, contractId=None))
    thermal_result = client.call(route + 'preview', thermal_request)
    assert fraction(thermal_result['valuation']['value']) == 10 * 100 - (10 * 2 + 1 + 3) * 10
    assert fraction(thermal_result['closingState']['quantities'][0]['quantity']) == 10
    thermal_request['valuation']['fuelQuantityBasis'] = 'consumption'
    assert fraction(client.call(route + 'preview', thermal_request)['valuation']['value']) == 10 * 100 - (10 * Fraction('1.5') + 1) * 10
    solar = deepcopy(facility)
    solar.update(id=str(uuid4()))
    solar['terms'].update(kind='solar', powerQuantityCode=None, battery=None, solar=dict(stage='delivered'))
    client.call(route + 'save', dict(snapshotId=frame, requestId=str(uuid4()), expectedVersion=0, facility=solar))
    solar_request = deepcopy(request)
    solar_request.update(facilityId=solar['id'], batteryState=None, batteryDispatch=[],
        solarRight=dict(forecastAssumption='hypothetical_supplied_forecast_v1', entitlementNumerator='1',
                        entitlementDenominator='2', deliveryBasis='as_produced', paymentBasis='produced_only'),
        solarDispatch=[dict(productionMwh='10', requiredDeliveryMwh=None)])
    solar_request['valuation']['chargingPrice'] = None
    solar_result = client.call(route + 'preview', solar_request)
    assert fraction(solar_result['valuation']['value']) == Fraction(10, 2) * 100
    assert fraction(next(q['quantity'] for q in solar_result['intervals'][0]['quantities'] if q['name'] == 'eligible_actual_energy')) == 0
    firm = deepcopy(solar_request)
    firm['solarRight'].update(deliveryBasis='fixed_profile', paymentBasis='delivery_obligation')
    firm['solarDispatch'][0]['requiredDeliveryMwh'] = '12'
    assert client.call(route + 'preview', firm, expected=422)['code'] == 'structured_power_unavailable'
    firm['valuation'] = None
    exposure = client.call(route + 'preview', firm)
    assert fraction(next(q['quantity'] for q in exposure['intervals'][0]['quantities'] if q['name'] == 'replacement_energy')) == 12 - Fraction(10, 2)

    # Select the server's exact retained context, rather than requesting a new
    # current-knowledge capture, before comparing complete immutable output.
    client.call('market/context/retained', dict(contextId=solar_result['contextId'], selection='latest_correction'))
    retained_frame = initial_frame(client.call('workspace'), '2020-12-15')['id']
    retained_request = deepcopy(solar_request)
    retained_request['snapshotId'] = retained_frame
    retained_result = client.call(route + 'preview', retained_request)
    assert retained_result == client.call(route + 'preview', retained_request)
    assert retained_result['contextId'] == solar_result['contextId']
    historical_command = deepcopy(command)
    historical_command.update(snapshotId=retained_frame, requestId=str(uuid4()))
    historical_error = client.call(route + 'save', historical_command, expected=422)
    assert historical_error['code'] == 'historical_read_only'
    client.call('market/current', {})
    from check_power_trades_http import run_checks as check_trades
    booked_thermal=deepcopy(thermal_request)
    booked_thermal['grid']['operatingDate']='2031-01-04'
    booked_thermal['thermalState']['at']='2031-01-04T00:00:00Z'
    booked_thermal['thermalDispatch'][0]['startupInstructionAt']='2031-01-04T00:00:00Z'
    booked_thermal['valuation']['fuelQuantityBasis']='guaranteed'
    booked_solar=deepcopy(solar_request)
    booked_solar['grid']['operatingDate']='2031-01-05'
    operating=check_trades(client,query,manifest,request,other_plans=[
        ('thermal',booked_thermal,760,{'energy_output':1000,'fuel_input':240}),
        ('solar',booked_solar,500,{'payment_energy':500})])
    from check_rec_http import run_checks as check_recs
    rec=check_recs(client,query,manifest)
    from check_heat_rate_call_http import run_checks as check_heat_rate_calls
    check_heat_rate_calls(client,query,manifest)
    from check_gas_storage_http import run_checks as check_gas_storage
    check_gas_storage(client,query,manifest)
    anonymous = PreviewClient(url)
    anonymous.bootstrap()
    anonymous.call(route + 'catalog', {'snapshotId': frame}, expected=403)
    print('PASS structured power HTTP: exact saved pins, idempotent receipts, carried battery losses, gross sides, scenario provenance and strict refusals', flush=True)
    return dict(operating=operating,rec=rec)
