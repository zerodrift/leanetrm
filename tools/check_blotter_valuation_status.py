"""D213: a close that cannot value some positions reports each one and leaves it out of valuation totals and risk runs.

The fixture is the seeded standard example with the custom-curve closes through Dec 31, 2020. By then the
seeded January future has expired, and the closes after Dec 18 pin neither the standard curves nor the
discount rates the monthly swaps need. The daily swap bundle publishes its prices and due fixings at Dec 31
only, so the daily swaps share their book totals with monthly swaps the close cannot value. One synthetic
future on the custom monthly NG curve, observed at every close, is booked here to do the same for futures.
"""
from collections import defaultdict
from fractions import Fraction
import json
from uuid import UUID, uuid4, uuid5
from check_portfolio_risk import financial, reconcile
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


def run_risk_checks(session, run_worker):
    """A risk run leaves out the positions its base close cannot value and lists them (D213).

    Each run shocks one exact contract. What it keeps must be the result of a run over exactly the positions
    the close values: their Blotter values at that close, the futures sensitivity, and the same numbers whichever
    other positions the selected books add or leave out. A queued run retains the same list.
    """
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS valuation status risk: '+name, flush=True)
    c = session; c.call('profile', {'profile': 'risk'})
    workspace = c.call('workspace')
    dec18, dec31 = initial_frame(workspace, '2020-12-18'), initial_frame(workspace, '2020-12-31')

    def blotter(frame):
        current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
        read = c.call('trading/blotter', {'context': current, 'view': TRADES})
        return read, {**current, 'knowledgeMode': 'retained', 'contextId': read['context']['contextId']}

    def catalog(context):
        return c.call('portfolio/risk/catalog', {'context': context, 'bookIds': [], 'portfolioIds': []})

    def key(item):
        return item['tradeId'], item['bindingId']

    def unvalued_rows(rows):
        """The positions a Blotter read reports unvalued, as a risk run lists them."""
        return sorted(({'tradeId': r['tradeId'], 'bindingId': r['bindingId'], 'tradeReference': r['tradeReference'],
                        'instrumentType': r['instrumentType'], 'reason': r['unavailableReason']}
                       for r in rows if r['unavailableReason'] is not None), key=key)

    def excluded(report):
        return sorted(report['excludedPositions'], key=key)

    def values(report):
        return {key(p): p['values'] for p in report['factors'][0]['positions']}

    early, early_context = blotter(dec18)
    choices = catalog(early_context)
    ng = next(f['factor'] for f in choices['factors'] if f['curves'] == ['ng_monthly'] and f['deliveryFrom'] == '2021-02-01')

    def request(context, factor, books=(), size='0.1'):
        return {'context': context, 'bookIds': list(books), 'portfolioIds': [], 'detail': 'holdings', 'stress': [],
                'convention': choices['convention'], 'numericalPolicy': choices['numericalPolicy'], 'pricingPolicy': choices['pricingPolicy'],
                'bumps': [{'factor': factor, 'size': size, 'convention': choices['bumpConvention']}]}

    late, late_context = blotter(dec31)
    rows = late['holdings']
    valued = [r for r in rows if r['unavailableReason'] is None]
    held = {key(r): r for r in valued}
    report = c.call('portfolio/risk', request(late_context, ng))
    check('a run at a close that cannot value every held position returns results for the nine it can value',
          len(rows) == 25 and len(valued) == 9 and report['work']['positionCount'] == 9)
    check('it lists exactly the positions the Blotter reports unvalued at that close, each with the same reason',
          len(report['excludedPositions']) == 16 and excluded(report) == unvalued_rows(rows))
    listed = {e['tradeReference'] for e in report['excludedPositions']}
    check('no position it lists is among its inputs or factor rows',
          not listed & {p['tradeReference'] for p in report['inputs']['positions']}
          and not listed & {p['tradeReference'] for f in report['factors'] for p in f['positions']})
    # Rows and book totals hold exactly the valued positions, and each total adds only its book's rows.
    reconcile(report, {'context': late['context'], 'holdings': valued}, check, 'Dec 31, 2020 risk')

    def alone(position, value):
        row = held[position]
        delta = Fraction(row['quoteMultiplier'])*Fraction(row['quantity']) if row['curve'] == 'ng_monthly' else Fraction(0)
        return (Fraction(value['base']) == Fraction(row['executionPnl'] if row['instrumentType'] == 'future' else row['presentValue'])
                and Fraction(value['delta']) == delta and Fraction(value['gamma']) == 0
                and Fraction(value['upPnl']) == delta/10 and Fraction(value['downPnl']) == -delta/10)
    check('each position it keeps has its Blotter value at that close and moves only with its own contract',
          sum(r['curve'] == 'ng_monthly' for r in valued) == 1 and all(alone(k, v) for k, v in values(report).items()))

    late_choices = catalog(late_context)
    check('the risk catalog at that close lists the same positions and offers the valued future\'s contract',
          excluded(late_choices) == excluded(report) and ng in [f['factor'] for f in late_choices['factors']])
    daily = next(f['factor'] for f in late_choices['factors'] if f['curves'] == ['daily_gas'])
    for book, factor in ((BOOKS[0], ng), (BOOKS[1], daily)):
        whole = report if factor == ng else c.call('portfolio/risk', request(late_context, factor))
        part = c.call('portfolio/risk', request(late_context, factor, [book]))
        check(f'a run over book {book} alone lists that book\'s unvalued positions and gives its others the same results',
              excluded(part) == unvalued_rows([r for r in rows if r['bookId'] == book])
              and values(part) == {k: v for k, v in values(whole).items() if held[k]['bookId'] == book})
    failure = c.call('portfolio/risk', request(late_context, ng, size='2000'), expected=422)
    check('a failure under a shock still fails the whole run rather than leaving a position out', failure['code'] == 'price_domain')

    job = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': request(late_context, ng)})
    run_worker()
    completed = c.call('portfolio/risk/jobs/'+job['id'])
    check('a queued run at that close retains the same list with the same results',
          completed['state'] == 'completed' and completed['result']['excludedPositions'] == report['excludedPositions']
          and financial(completed['result']) == financial(report))

    check('the risk catalog at a close that values every position lists none', choices['excludedPositions'] == [])
    full = c.call('portfolio/risk', request(early_context, ng))
    check('a run at that close lists none and keeps all 25 positions',
          full['excludedPositions'] == [] and full['work']['positionCount'] == 25 and len(early['holdings']) == 25)
    print(f'{passed} valuation status risk checks passed.', flush=True)
