"""Heat-rate call option over HTTP: managed booking, shared spread marks and the valued position."""
from copy import deepcopy
from uuid import uuid4
from fractions import Fraction
from preview_client import initial_frame
from preview_fixture import trade_booking


def run_checks(client, query, manifest):
    client.call('market/current', {})
    client.call('profile', {'profile': 'trader'})
    frame = initial_frame(client.call('workspace'), '2020-12-15')['id']
    context = dict(snapshotId=frame, convention='heat_rate_call_trade_v1', knowledgeMode='current', contextId=None)
    terms = dict(definitionRevisionId=manifest['revisionId'], referenceCatalogId=manifest['referenceCatalogId'],
        referenceRevisionId=manifest['referenceRevisionId'], quantity='50', capacityQuantity='test_power_megawatt',
        powerCurve='local_power', fuelCurve='local_gas', heatRate='7.5', strike='3',
        exerciseStyle='daily_automatic_cash_exercise_v1', model='spread_call_kirk_lognormal_v1', dayCount='act_365_fixed_instant_v1',
        deliveryFrom='2021-02-01', deliveryTo='2021-02-04', clock='test_utc', intervalMinutes=60,
        hours=dict(membership='all', touProduct=None, touVersion=None, calendar='test_business'),
        exerciseRule=dict(implementation='business_day_lead_local_cutoff_v1', leadBusinessDays=1, localCutoff='15:00', calendar='test_business'),
        paymentRule=dict(frequency='monthly', rule='days_after_period_end', days=10, dayOfMonth=None, dayBasis='calendar_days',
            shortMonth=None, adjustment='unadjusted', calendar=None, implementation='monthly_payment_date_rules_v1'))
    command = dict(context=context, operationId=str(uuid4()), bookId='d1000000-0000-0000-0000-000000000001', **trade_booking(),
        reason='Synthetic toll option', terms=terms)
    # The ticket's choices offer every term the booking below states, and nothing is offered to an account that may not book.
    choices = client.call('heat-rate-calls/catalog', context, timeout=60)
    curves = {curve['code']: curve for curve in choices['curves']}
    power, fuel = curves[terms['powerCurve']], curves[terms['fuelCurve']]
    assert power['commodity'] != fuel['commodity'] and power['currency'] == fuel['currency'] and (power['quantityUnit'], power['priceUnit']) == ('MWh', 'USD/MWh'), (power, fuel)
    assert dict(code=terms['capacityQuantity'], commodity=power['commodity'], unit='MW') in choices['capacityQuantities'], choices['capacityQuantities']
    assert (choices['definitionRevisionId'], choices['referenceCatalogId'], choices['referenceRevisionId']) == \
        (terms['definitionRevisionId'], terms['referenceCatalogId'], terms['referenceRevisionId']), choices
    assert terms['clock'] in choices['clocks'] and terms['hours']['calendar'] in choices['calendars'] and terms['intervalMinutes'] in choices['intervalMinutes'], choices
    assert choices['policies'] == dict(exerciseStyles=[terms['exerciseStyle']], models=[terms['model']], dayCounts=[terms['dayCount']],
        exerciseRules=[terms['exerciseRule']['implementation']]), choices['policies']
    assert choices['paymentOptions']['implementation'] == terms['paymentRule']['implementation'] and choices['memberships'] == ['all', 'peak', 'off'], choices
    assert choices['canBook'] is True and command['bookId'] in [book['id'] for book in choices['books']] and choices['legalEntities'] and choices['counterparties'], choices
    client.call('profile', {'profile': 'viewer'})
    client.call('heat-rate-calls/catalog', context, expected=403, timeout=60)
    client.call('profile', {'profile': 'trader'})
    print('PASS heat-rate call HTTP: the ticket is offered exactly the terms a booking states, and booking only to an account that may book', flush=True)
    before = int(query('SELECT count(*) FROM trading.trades;'))
    for change, status in [(lambda t: t.update(model='another_model_v1'), 422), (lambda t: t.update(heatRate='0'), 422),
                           (lambda t: t.update(fuelCurve='local_power'), 422), (lambda t: t.update(deliveryTo='2021-02-01'), 422),
                           (lambda t: t['exerciseRule'].update(localCutoff='3pm'), 422), (lambda t: t.update(deliveryTo='2022-02-01'), 422)]:
        refused = deepcopy(command); change(refused['terms'])
        client.call('heat-rate-calls/book', refused, expected=status, timeout=60)
    client.call('profile', {'profile': 'viewer'})
    client.call('heat-rate-calls/book', command, expected=403, timeout=60)
    client.call('profile', {'profile': 'trader'})
    assert int(query('SELECT count(*) FROM trading.trades;')) == before
    saved = client.call('heat-rate-calls/book', command, timeout=60)
    assert saved['changed'] and saved['revision'] == 1, saved
    assert client.call('heat-rate-calls/book', command, timeout=60) == saved
    assert client.call('heat-rate-calls/status', dict(operationId=command['operationId'])) == saved
    duplicate = deepcopy(command); duplicate['operationId'] = str(uuid4())
    repeated = client.call('heat-rate-calls/book', duplicate, timeout=60)
    assert not repeated['changed'] and repeated['tradeId'] == saved['tradeId'], repeated
    changed = deepcopy(command); changed['terms']['strike'] = '4'
    client.call('heat-rate-calls/book', changed, expected=409, timeout=60)
    assert int(query('SELECT count(*) FROM trading.trades;')) == before + 1
    selected = dict(context=context, tradeId=saved['tradeId'])
    held = client.call('heat-rate-calls/get', selected, timeout=60)
    position, exercises = held['position'], held['exercises']
    assert (position['instrumentType'], position['quantity'], position['unit'], position['heatRate'], position['strike'], position['direction']) == \
        ('heat_rate_call', '50', 'MW', '7.5', '3', 'buy'), position
    assert [(row['delivery_day'], row['energy_per_unit']) for row in exercises] == [(f'2021-02-0{day}', '24') for day in (1, 2, 3)], exercises
    assert exercises[0]['exercise_at'].startswith('2021-01-29T15:00:00') and exercises[1]['exercise_at'].startswith('2021-02-01T15:00:00'), exercises
    assert all(row['payment_at'].startswith('2021-03-10T00:00:00') for row in exercises), exercises
    assert position['presentValue'] is None and position['unavailableReason'] == 'heat_rate_call_marks', position
    print('PASS heat-rate call HTTP: the managed schedule books once, replays its outcome and lists an unmarked option as unpriced', flush=True)

    mark = lambda expected, power, fuel, rho: dict(month='2021-02', expectedRevision=expected, longVolatility=power, shortVolatility=fuel, correlation=rho)
    marks = dict(context=context, operationId=str(uuid4()), definitionRevisionId=manifest['revisionId'], longCurve='local_power', shortCurve='local_gas',
        reason='Synthetic desk marks', marks=[mark(0, '0.6', '0.45', '0.8')])
    client.call('spread-marks/record', marks, expected=403, timeout=60)
    client.call('profile', {'profile': 'editor'})
    invalid = deepcopy(marks); invalid['marks'][0]['correlation'] = '1.5'
    client.call('spread-marks/record', invalid, expected=422, timeout=60)
    recorded = client.call('spread-marks/record', marks, timeout=60)
    assert [(m['month'], m['revision'], m['changed']) for m in recorded['marks']] == [('2021-02', 1, True)], recorded
    assert client.call('spread-marks/record', marks, timeout=60) == recorded
    stale = dict(marks, operationId=str(uuid4()), marks=[mark(0, '0.9', '0.45', '0.8')])
    client.call('spread-marks/record', stale, expected=409, timeout=60)
    client.call('profile', {'profile': 'trader'})
    read = client.call('spread-marks/read', dict(context=context, definitionRevisionId=manifest['revisionId'], longCurve='local_power',
        shortCurve='local_gas', months=['2021-02', '2021-03']), timeout=60)['marks']
    assert [(m['month'], int(m['revision']), m['longVolatility'], m['shortVolatility'], m['correlation']) for m in read] == [('2021-02', 1, '0.6', '0.45', '0.8')], read
    valued = client.call('heat-rate-calls/get', selected, timeout=60)['position']
    assert valued['unavailableReason'] is None and Fraction(valued['presentValue']) > 0, valued
    client.call('profile', {'profile': 'editor'})
    raised = dict(marks, operationId=str(uuid4()), marks=[mark(1, '0.9', '0.45', '0.8')])
    assert client.call('spread-marks/record', raised, timeout=60)['marks'][0]['revision'] == 2
    repeated_mark = dict(marks, operationId=str(uuid4()), marks=[mark(2, '0.6', '0.45', '0.8')])
    receipt = client.call('spread-marks/record', repeated_mark, timeout=60)
    assert receipt['marks'] == [{**recorded['marks'][0], 'changed': False}], receipt
    client.call('profile', {'profile': 'trader'})
    revalued = client.call('heat-rate-calls/get', selected, timeout=60)['position']
    assert Fraction(revalued['presentValue']) > Fraction(valued['presentValue']), (valued['presentValue'], revalued['presentValue'])
    sold = deepcopy(command); sold['operationId'] = str(uuid4()); sold['terms']['quantity'] = '-50'
    written = client.call('heat-rate-calls/book', sold, timeout=60)
    assert written['changed'] and written['tradeId'] != saved['tradeId']
    short = client.call('heat-rate-calls/get', dict(context=context, tradeId=written['tradeId']), timeout=60)['position']
    assert short['direction'] == 'sell' and Fraction(short['presentValue']) == -Fraction(revalued['presentValue']), short
    workspace = dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current')
    facts = client.call('trading/positions', dict(context=workspace, bookIds=[command['bookId']], portfolioIds=[], offset=0, limit=64), timeout=60)['positions']
    listed = {p['tradeId']: (p['instrumentType'], p['bookedQuantity']) for p in facts}
    assert listed[saved['tradeId']] == ('heat_rate_call', '50') and listed[written['tradeId']] == ('heat_rate_call', '-50'), listed
    holding = client.call('trading/position', dict(context=workspace, tradeId=saved['tradeId'], bindingId=saved['instrumentId'], revision=1,
        instrumentType='heat_rate_call'), timeout=60)['holding']
    assert holding['presentValue'] == revalued['presentValue'] and holding['fixedPrice'] == '3' and holding['unavailableReason'] is None, holding
    print('PASS heat-rate call HTTP: marks need marking authority, a marked option is valued, a higher power volatility is worth more, a written option mirrors it and both are ordinary positions', flush=True)
    return dict(bought=saved['tradeId'], written=written['tradeId'])
