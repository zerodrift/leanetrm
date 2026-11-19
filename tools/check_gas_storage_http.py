"""Gas storage contract over HTTP: managed booking, the plan in force, a proposed plan and the valued position."""
from copy import deepcopy
from uuid import uuid4
from fractions import Fraction
from preview_client import initial_frame
from preview_fixture import trade_booking


def run_checks(client, query, manifest):
    client.call('market/current', {})
    client.call('profile', {'profile': 'trader'})
    frame = initial_frame(client.call('workspace'), '2020-12-15')['id']
    context = dict(snapshotId=frame, convention='gas_storage_trade_v1', knowledgeMode='current', contextId=None)
    band = lambda lower, upper, injection, withdrawal: dict(lower=lower, upper=upper, maximumDailyInjection=injection, maximumDailyWithdrawal=withdrawal)
    terms = dict(definitionRevisionId=manifest['revisionId'], referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'],
        quantity='test_gas_quantity', injectionCurve='local_gas', withdrawalCurve='local_gas', minimumInventory='0', maximumInventory='100000',
        ratchets=[band('0', '30000', '2000', '500'), band('30000', '70000', '1500', '1000'), band('70000', '100000', '1000', '1500')],
        injectionFuel='0.01', withdrawalFuel='0', ratchetBasis='gas_day_opening_inventory_band_v1', flowPolicy='single_direction_per_gas_day_v1',
        fuelBasis='in_kind_fuel_at_storage_point_v1', openingInventory='29000', terminalMinimum='0', terminalMaximum='100000',
        injectionCharge='0.02', withdrawalCharge='0.03', monthlyCapacityCharge='0.005', termFrom='2021-02-01', termTo='2021-02-04', clock='test_utc', dayStartMinute=540,
        paymentRule=dict(frequency='monthly', rule='days_after_period_end', days=20, dayOfMonth=None, dayBasis='calendar_days', shortMonth=None,
            adjustment='unadjusted', calendar=None, implementation='monthly_payment_date_rules_v1'))
    command = dict(context=context, operationId=str(uuid4()), bookId='d1000000-0000-0000-0000-000000000001', **trade_booking(),
        reason='Synthetic storage contract', terms=terms)

    # The ticket's choices offer every reference term the booking below states.
    choices = client.call('gas-storage/catalog', context, timeout=60)
    curve = {row['code']: row for row in choices['curves']}[terms['injectionCurve']]
    assert (curve['quantity'], curve['priceUnit']) == (terms['quantity'], 'USD/MMBtu (IT)') and any(c['from'] <= '2021-02-01' < c['to'] for c in curve['contracts']), curve
    assert terms['clock'] in choices['clocks'] and choices['canBook'] is True and choices['policies'] == dict(ratchetBases=[terms['ratchetBasis']],
        flowPolicies=[terms['flowPolicy']], fuelBases=[terms['fuelBasis']], planSearches=['gas_storage_inventory_lattice_search_v1']), choices
    assert (choices['definitionRevisionId'], choices['referenceRevisionId'], choices['payment']['implementation']) == \
        (terms['definitionRevisionId'], terms['referenceRevisionId'], terms['paymentRule']['implementation']), choices

    before = int(query('SELECT count(*) FROM trading.trades;'))
    for change, status in [(lambda t: t.update(ratchetBasis='interpolated_v1'), 422), (lambda t: t['ratchets'].pop(1), 422), (lambda t: t.update(openingInventory='100001'), 422),
                           (lambda t: t.update(withdrawalCurve='local_power'), 422), (lambda t: t.update(termTo='2021-02-01'), 422), (lambda t: t.update(termTo='2031-02-01'), 422),
                           (lambda t: t['paymentRule'].update(dayBasis='business_days', calendar='test_business'), 422), (lambda t: t.update(injectionCharge='-1'), 422)]:
        refused = deepcopy(command); change(refused['terms'])
        client.call('gas-storage/book', refused, expected=status, timeout=60)
    client.call('profile', {'profile': 'viewer'})
    client.call('gas-storage/book', command, expected=403, timeout=60)
    client.call('profile', {'profile': 'trader'})
    assert int(query('SELECT count(*) FROM trading.trades;')) == before
    saved = client.call('gas-storage/book', command, timeout=60)
    assert saved['changed'] and saved['revision'] == 1, saved
    assert client.call('gas-storage/book', command, timeout=60) == saved
    assert client.call('gas-storage/status', dict(operationId=command['operationId'])) == saved
    duplicate = deepcopy(command); duplicate['operationId'] = str(uuid4())
    assert client.call('gas-storage/book', duplicate, timeout=60) == {**saved, 'operationId': duplicate['operationId'], 'changed': False}
    assert int(query('SELECT count(*) FROM trading.trades;')) == before + 1
    selected = dict(context=context, tradeId=saved['tradeId'])
    held = client.call('gas-storage/get', selected, timeout=60)
    position = held['position']
    assert (position['instrumentType'], position['quantity'], position['unit'], position['presentValue'], position['unavailableReason']) == \
        ('gas_storage', '100000', 'MMBtu (IT)', None, 'gas_storage_plan'), position
    assert [(row['gas_day'], row['starts_at'][:19], row['payment_at'][:10]) for row in held['days']] == \
        [(f'2021-02-0{day}', f'2021-02-0{day}T09:00:00', '2021-03-20') for day in (1, 2, 3)], held['days']
    assert len(held['ratchets']) == 3 and held['plan'] is None and held['path'] is None and held['terms']['monthly_capacity_charge'] == '0.005', held
    print('PASS gas storage HTTP: the managed gas days book once, replay their outcome, and a contract without a plan is listed as unpriced', flush=True)

    flow = lambda day, injection, withdrawal: dict(gasDay=f'2021-02-0{day}', injection=injection, withdrawal=withdrawal)
    plan = dict(context=context, operationId=str(uuid4()), tradeId=saved['tradeId'], expectedRevision=0, reason='Synthetic desk plan',
        source=dict(kind='supplied', inventoryStep=None), days=[flow(1, '2000', '0'), flow(2, '1500', '0'), flow(3, '0', '1000')])
    # The second day opens above 30,000, where the contract allows 1,500 in: the ratchet refuses the plan and nothing is recorded.
    over = deepcopy(plan); over['days'][1]['injection'] = '2000'
    client.call('gas-storage/plan', over, expected=422, timeout=60)
    client.call('gas-storage/plan', dict(deepcopy(plan), days=plan['days'][:2]), expected=422, timeout=60)
    client.call('profile', {'profile': 'viewer'})
    client.call('gas-storage/plan', plan, expected=403, timeout=60)
    client.call('profile', {'profile': 'trader'})
    assert query('SELECT count(*) FROM trading.gas_storage_plans;') == '0'
    recorded = client.call('gas-storage/plan', plan, timeout=60)
    assert (recorded['revision'], recorded['changed'], recorded['tradeId']) == (1, True, saved['tradeId']), recorded
    assert client.call('gas-storage/plan', plan, timeout=60) == recorded
    client.call('gas-storage/plan', dict(deepcopy(plan), operationId=str(uuid4()), days=[flow(1, '0', '0'), flow(2, '0', '0'), flow(3, '0', '0')]), expected=409, timeout=60)

    proposal = client.call('gas-storage/propose', dict(context=context, tradeId=saved['tradeId'], inventoryStep='500'), timeout=60)
    cost, value = Fraction(proposal['prices'][0]['injectionCost']), Fraction(proposal['prices'][0]['withdrawalValue'])
    forward = cost - Fraction('0.02')
    assert forward > 1 and value == forward - Fraction('0.03') and all((Fraction(p['injectionCost']), Fraction(p['withdrawalValue'])) == (cost, value) for p in proposal['prices']), proposal['prices']
    valued = client.call('gas-storage/get', selected, timeout=60)
    # 1,000 sold and 3,500 bought at the forward, commodity charges on both, and 100,000 of capacity at 0.005 for the month.
    expected = Fraction(1000 - 3500) * forward - 3500 * Fraction('0.02') - 1000 * Fraction('0.03') - 100000 * Fraction('0.005')
    assert valued['position']['unavailableReason'] is None and Fraction(valued['position']['presentValue']) == expected, (valued['position']['presentValue'], expected)
    assert [(d['gasDay'], d['band'], d['opening'], d['closing'], d['maximumDailyInjection']) for d in valued['path']] == \
        [('2021-02-01', 0, '29000', '30980', '2000'), ('2021-02-02', 1, '30980', '32465', '1500'), ('2021-02-03', 1, '32465', '31465', '1500')], valued['path']
    print('PASS gas storage HTTP: a plan the ratchets refuse records nothing, an admitted plan is valued at its flows and charges exactly, and its inventory path is returned', flush=True)

    # Every day has one forward, so buying to sell later cannot pay: with inventory free to leave, the best plan sells the 500 a day the lowest band allows.
    assert (proposal['implementation'], proposal['inventoryStep'], proposal['objectiveBasis']) == ('gas_storage_inventory_lattice_search_v1', '500', 'forward_net_of_commodity_charges_undiscounted_v1'), proposal
    assert [(d['injection'], d['withdrawal'], d['closing']) for d in proposal['path']] == [('0', '500', '28500'), ('0', '500', '28000'), ('0', '500', '27500')], proposal['path']
    assert Fraction(proposal['objective']) == 1500 * value, (proposal['objective'], value)
    adopted = dict(context=context, operationId=str(uuid4()), tradeId=saved['tradeId'], expectedRevision=1, reason='Adopt the proposed plan',
        source=dict(kind=proposal['implementation'], inventoryStep=proposal['inventoryStep']),
        days=[dict(gasDay=d['gasDay'], injection=d['injection'], withdrawal=d['withdrawal']) for d in proposal['path']])
    assert client.call('gas-storage/plan', adopted, timeout=60)['revision'] == 2
    revalued = client.call('gas-storage/get', selected, timeout=60)
    assert Fraction(revalued['position']['presentValue']) == 1500 * value - 100000 * Fraction('0.005') and revalued['plan']['source'] == proposal['implementation'], revalued['position']
    assert Fraction(revalued['position']['presentValue']) > expected
    workspace = dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current')
    facts = client.call('trading/positions', dict(context=workspace, bookIds=[command['bookId']], portfolioIds=[], offset=0, limit=64), timeout=60)['positions']
    assert {p['tradeId']: (p['instrumentType'], p['bookedQuantity']) for p in facts}[saved['tradeId']] == ('gas_storage', '100000'), facts
    holding = client.call('trading/position', dict(context=workspace, tradeId=saved['tradeId'], bindingId=saved['instrumentId'], revision=1,
        instrumentType='gas_storage'), timeout=60)['holding']
    assert holding['presentValue'] == revalued['position']['presentValue'] and holding['planRevision'] == '2' and holding['unavailableReason'] is None, holding
    print('PASS gas storage HTTP: the proposed plan is the lattice optimum at the snapshot forwards, is adopted as the plan in force and revalues the ordinary position', flush=True)
