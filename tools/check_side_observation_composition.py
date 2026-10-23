"""Independent observation rules on both sides of one financial swap."""
from collections import Counter
from copy import deepcopy
from datetime import date, timedelta
from fractions import Fraction
from uuid import uuid4


def run_checks(client, query, context, draft, weighted, booking):
    first = {**weighted, 'spread': '0.1'}
    second = {**draft['floating'], 'averagingMethod': 'calendar_days_previous', 'spread': '-0.02'}
    terms = {**draft, 'fixed': None, 'fixedCoefficient': None, 'swapType': 'financial_floating_floating',
             'firstCoefficient': '1', 'firstFloating': first, 'floating': second}
    price = client.call('swaps/price', terms)
    # Receive 3.21+0.10, pay (3+478/3100)-0.02, on 100 units.
    expected = Fraction(545, 31)
    assert Fraction(price['valuation']['presentValue']) == expected
    left, right = [side['payments'][0]['observationSchedule'] for side in price['valuation']['sides']]
    assert (left['denominator'], right['denominator']) == ('496', '31')
    assert not any(row['isGuard'] for row in left['observations'])
    assert [row['from'] for row in right['observations'] if row['isGuard']] == ['2020-12-31']
    assert [row['weight'] for row in left['observations']] == [str(day) for day in range(1, 32)]
    assert len(left['applications']) == len(right['applications']) == 31
    previous = Counter([0, 0, 0, 4, 5, 6, 7, 8, 8, 8, 11, 12, 13, 14, 15, 15, 15,
                        18, 19, 20, 21, 22, 22, 22, 25, 26, 27, 28, 29, 29, 29])
    _risk(client, terms, price, expected, previous, range(0, 32))
    command = {**booking, 'operationId': str(uuid4()), 'fixed': None, 'fixedCoefficient': None,
               'swapType': terms['swapType'], 'firstCoefficient': '1', 'firstFloating': first, 'floating': second}
    saved = client.call('swaps/book', command)
    retained = client.call('swaps/editor', {'context': context, 'tradeId': saved['tradeId'], 'revision': None})
    assert Fraction(retained['valuation']['presentValue']) == expected
    assert retained['state']['terms']['firstFloating']['observationSchedule'] == first['observationSchedule']
    assert retained['state']['terms']['floating']['observationSchedule'] == second['observationSchedule']
    assert [side['payments'][0]['observationSchedule'] for side in retained['valuation']['sides']] == [left, right]
    assert client.call('swaps/book', command) == saved
    print('PASS composed observations: each floating side retains its own calendar/quantity plan, guard, exact spread and 32 shared quote scenarios', flush=True)

    changed = deepcopy(command)
    changed.update(operationId=str(uuid4()), tradeId=saved['tradeId'], expectedRevision=1,
                   effectiveAt=price['asOf'], reason='Synthetic first-side quantity correction')
    changed['firstFloating']['observationSchedule']['quantities'][0]['quantity']['value'] = '2'
    amended = client.call('swaps/amend', changed)
    current = client.call('swaps/editor', {'context': context, 'tradeId': saved['tradeId'], 'revision': None})
    original = client.call('swaps/editor', {'context': context, 'tradeId': saved['tradeId'], 'revision': 1})
    assert amended['instrumentId'] != saved['instrumentId'] and amended['revision'] == 2
    assert Fraction(current['valuation']['presentValue']) == Fraction(10417, 497)-Fraction(478, 31)+12
    assert Fraction(original['valuation']['presentValue']) == expected
    assert current['valuation']['sides'][0]['sideId'] != original['valuation']['sides'][0]['sideId']
    assert current['valuation']['sides'][1]['sideId'] == original['valuation']['sides'][1]['sideId']
    assert current['state']['terms']['floating'] == original['state']['terms']['floating']
    assert client.call('swaps/book', command) == saved
    print('PASS composed observations: changing only the first plan appends its side while the second side and original revision/replay remain exact', flush=True)

    workspace = client.call('workspace')
    initial = set(workspace['initialFrameIds'])
    matches = [frame for frame in workspace['frames'] if frame['id'] in initial and frame['at'].startswith('2021-01-15T08:00:00')]
    assert len(matches) == 1
    partial_terms = {**terms, 'context': {**context, 'snapshotId': matches[0]['id']}}
    partial = client.call('swaps/price', partial_terms)
    assert Fraction(partial['valuation']['presentValue']) == Fraction(501, 62)
    _risk(client, partial_terms, partial, Fraction(501, 62), previous, range(16, 32))
    assert sum(row['fixingState'] == 'Actual' for row in partial['valuation']['sides'][0]['payments'][0]['observationSchedule']['observations']) == 15
    print('PASS composed observations: both sides consume exact actual fixings and only their 16 unresolved shared quote factors are shocked', flush=True)

    before = query('SELECT count(*) FROM trading.publications;')
    for slot in ('firstFloating', 'floating'):
        invalid = deepcopy(command)
        invalid['operationId'] = str(uuid4())
        if slot == 'firstFloating':
            invalid[slot]['observationSchedule']['quantities'][0]['quantity']['value'] = '0'
        else:
            invalid[slot]['observationSchedule']['calendar'] = None
        client.call('swaps/book', invalid, expected=422)
    assert query('SELECT count(*) FROM trading.publications;') == before
    print('PASS composed observations: missing calendar or nonpositive quantity on either side fails booking atomically', flush=True)


def _risk(client, terms, price, expected, previous, days):
    retained = {**terms, 'context': {**terms['context'], 'knowledgeMode': 'retained', 'contextId': price['context']['contextId']}}
    result = client.call('swaps/draft-risk', {'draft': retained, 'bumpPolicy': price['bumpPolicy'],
                                            'numericalPolicy': price['numericalPolicy']})
    required = {str(date(2021, 1, 1)+timedelta(days=day-1)): day for day in days}
    assert len(result['factors']) == len(required) and result['scenarioCount'] == 1+2*len(required)
    assert {row['factor']['deliveryFrom'] for row in result['factors']} == set(required)
    for row in result['factors']:
        day = required[row['factor']['deliveryFrom']]
        delta = 100*(Fraction(day, 496)-Fraction(previous[day], 31))
        bump = delta*Fraction(row['bump'])
        assert Fraction(row['basePv']) == expected and Fraction(row['gamma']) == 0 and Fraction(row['delta']) == delta
        assert Fraction(row['upPv']) == expected+bump and Fraction(row['downPv']) == expected-bump
