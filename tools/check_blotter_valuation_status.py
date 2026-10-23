"""D213: a close that cannot value some positions reports each one and leaves it out of valuation totals.

The fixture is the seeded standard example with the custom-curve closes through Dec 31, 2020. By then the
seeded January future has expired, and the closes after Dec 18 pin neither the standard curves nor the
discount rates the monthly swaps need. The daily swap bundle publishes its prices and due fixings at Dec 31
only, so the daily swaps share their book totals with monthly swaps the close cannot value. One synthetic
future on the custom monthly NG curve, observed at every close, is booked here to do the same for futures.
"""
from collections import defaultdict
from fractions import Fraction
import json
from uuid import UUID, uuid5
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from seed_preview_positions import BOOKS

REASONS = ('source_expired', 'missing_observation')
TRADES = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
BY_BOOK = {**TRADES, 'mode': 'pivot', 'groupBy': ['book'], 'measures': ['presentValue', 'executionPnl', 'tradeCount'],
           'pivotConvention': 'trading_blotter_totals_v1'}
BOOKED = ('instrumentType', 'bookId', 'curve', 'contractId', 'deliveryFrom', 'deliveryTo', 'lots', 'quantity', 'unit',
          'quantitySpecification', 'currencyReference', 'currency', 'quoteMultiplier', 'executionPrice', 'fixedPrice', 'revision')


def total(rows, field):
    """The exact sum of a measure over valued rows; None when none is valued or the measure does not apply."""
    values = [r[field] for r in rows if r['unavailableReason'] is None]
    return None if not values or None in values else sum(map(Fraction, values))


def exact(value):
    return None if value is None else Fraction(value)


def unvalued(rows):
    return len({r['tradeId'] for r in rows if r['unavailableReason'] is not None})


def run_checks(url, session=None):
    passed = 0
    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS valuation status: '+name, flush=True)
    c = session or PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    workspace = c.call('workspace')
    dec18, dec31 = initial_frame(workspace, '2020-12-18'), initial_frame(workspace, '2020-12-31')
    futures = {'convention': workspace['futuresConvention'], 'knowledgeMode': 'current'}
    catalog = c.call('futures/catalog', {**futures, 'snapshotId': dec18['id']})
    ng = next(k for k in catalog['contracts'] if k['curve'] == 'ng_monthly' and k['deliveryFrom'] == '2021-02-01')
    booked = c.call('futures/book', {**futures, **trade_booking(), 'snapshotId': dec18['id'], 'bookId': BOOKS[0], 'curve': 'ng_monthly',
                                     'contractId': ng['id'], 'lots': '3', 'executionPrice': '3.05',
                                     'operationId': str(uuid5(UUID('e1000000-0000-0000-0000-000000000003'), 'valuation_status_v1:ng_monthly:2021-02'))})

    def context(frame):
        return {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}

    def report(frame, view):
        return c.call('trading/blotter', {'context': context(frame), 'view': view})

    def partitions(rows):
        groups = defaultdict(list)
        for r in rows:
            groups[(r['bookId'], r['instrumentType'], json.dumps(r['quantitySpecification'], sort_keys=True),
                    json.dumps(r['currencyReference'], sort_keys=True))].append(r)
        return groups

    def check_totals(name, rows, read):
        expected = partitions(rows)
        actual = {(g['bookId'], g['instrumentType'], json.dumps(g['quantitySpecification'], sort_keys=True),
                   json.dumps(g['currencyReference'], sort_keys=True)): g for g in read['groups']}
        check(name+': book totals keep instrument types, units and currencies apart', actual.keys() == expected.keys())
        check(name+': PV and execution P&L add only valued positions; trades count every booked position',
              all(exact(g['presentValue']) == total(expected[k], 'presentValue') and exact(g['executionPnl']) == total(expected[k], 'executionPnl')
                  and g['tradeCount'] == len({r['tradeId'] for r in expected[k]}) and g['unvaluedPositions'] == unvalued(expected[k])
                  for k, g in actual.items()))
        futures_rows = [r for r in rows if r['instrumentType'] == 'future']
        books = {(r['book'], r['currency']) for r in futures_rows}
        check(name+': futures book totals add valued execution P&L and count the positions they leave out',
              len(read['totals']) == len(books) and all(
                  exact(t['executionPnl']) == total(selected, 'executionPnl') and t['unvaluedPositions'] == unvalued(selected)
                  for t in read['totals'] for selected in [[r for r in futures_rows if (r['book'], r['currency']) == (t['book'], t['currency'])]]))
        check(name+': the report counts every position it leaves out', read['unvaluedPositions'] == unvalued(rows))

    late = c.call('trading/workspace', {'context': context(dec31), 'view': TRADES, 'bookingCatalog': 'directory'})['blotter']
    rows = late['holdings']
    check('the Blotter reads a close that cannot value every position', len(rows) == 25)
    check('every position states whether the close values it, with a position-specific code',
          all(r['unavailableReason'] in (None, *REASONS) for r in rows))
    check('positions the close cannot value have blank mark, P&L and PV, never zero',
          all(r['mark'] is None and r['executionPnl'] is None and r['presentValue'] is None for r in rows if r['unavailableReason']))
    expired = [r for r in rows if r['curve'] == 'future']
    check('the January future, expired on Dec 28, 2020, is reported expired rather than failing the read',
          len(expired) == 8 and all(r['unavailableReason'] == 'source_expired' for r in expired))
    monthly = [r for r in rows if r['curve'] in ('base_forward', 'hub_power')]
    check('monthly swaps without their discount rates at this close are reported as missing observations',
          len(monthly) == 8 and all(r['unavailableReason'] == 'missing_observation' for r in monthly))
    daily = [r for r in rows if r['curve'] in ('daily_gas', 'daily_power')]
    check('daily swaps whose bundle publishes this close are valued',
          len(daily) == 8 and all(r['unavailableReason'] is None and r['presentValue'] is not None for r in daily))
    held = next(r for r in rows if r['tradeId'] == booked['tradeId'])
    point = next(p for p in c.call('evaluate', {'snapshotId': dec31['id'], 'curve': 'ng_monthly'})['points'] if p['deliveryFrom'] == '2021-02-01')
    check('a valued future keeps the price its curve builds without the expired neighbour',
          held['unavailableReason'] is None and Fraction(held['mark']) == Fraction(point['value']))
    check('its execution P&L is exact against that price',
          Fraction(held['executionPnl']) == (Fraction(held['mark'])-Fraction('3.05'))*Fraction(held['quoteMultiplier'])*Fraction(held['quantity']))
    check('only the valued futures market counts as an evaluated price', late['evaluatedContracts'] == 1)
    early = {(r['tradeId'], r['bindingId']): r for r in report(dec18, TRADES)['holdings']}
    check('positions keep their booked facts when the close cannot value them',
          early.keys() == {(r['tradeId'], r['bindingId']) for r in rows} and all(
              all(r[f] == early[(r['tradeId'], r['bindingId'])][f] for f in BOOKED) for r in rows))
    states = [{r['unavailableReason'] is None for r in selected} for selected in partitions(rows).values()]
    check('three book totals at this close mix valued positions with positions they leave out, and one values none',
          states.count({True, False}) == 3 and states.count({False}) == 1)
    check_totals('Dec 31, 2020', rows, {**report(dec31, BY_BOOK), 'totals': late['totals'], 'unvaluedPositions': late['unvaluedPositions']})

    contract_view = {**TRADES, 'mode': 'pivot', 'groupBy': ['book', 'contract'], 'measures': ['tradeCount', 'lots', 'quantity', 'executionPnl'],
                     'pivotConvention': 'futures_blotter_totals_v1'}
    futures_report = c.call('futures/blotter', {'context': {**futures, 'snapshotId': dec31['id']}, 'view': contract_view})
    def futures_group(g):
        selected = [r for r in rows if r['instrumentType'] == 'future' and (r['bookId'], r['contractId']) == (g['bookId'], g['contractId'])]
        return (g['tradeCount'] == len({r['tradeId'] for r in selected}) and Fraction(g['lots']) == sum(Fraction(r['lots']) for r in selected)
                and Fraction(g['quantity']) == sum(Fraction(r['quantity']) for r in selected)
                and exact(g['executionPnl']) == total(selected, 'executionPnl') and g['unvaluedPositions'] == unvalued(selected))
    check('the futures pivot counts lots and quantity of every position and execution P&L of valued ones',
          len(futures_report['groups']) == 3 and all(futures_group(g) for g in futures_report['groups']))

    valued = c.call('trading/workspace', {'context': context(dec18), 'view': TRADES, 'bookingCatalog': 'directory'})['blotter']
    check('Dec 18, 2020 values every position', len(valued['holdings']) == 25 and all(r['unavailableReason'] is None for r in valued['holdings']))
    check_totals('Dec 18, 2020', valued['holdings'], {**report(dec18, BY_BOOK), 'totals': valued['totals'], 'unvaluedPositions': valued['unvaluedPositions']})
    print(f'{passed} valuation status checks passed.', flush=True)
    return c
