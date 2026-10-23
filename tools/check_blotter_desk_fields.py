"""Blotter rows state their commodity, swap direction and mark minus price, pivots group by commodity, and the
desk gets its summary groupings and curve shapes from single reads.

The fixture is the seeded standard example: eight futures and sixteen swaps, all valued at the Dec 18, 2020 close
this check reads. The seeding books only pay-fixed swaps, so the check also books a receive-fixed swap and a
floating/floating swap, amends the first, and reads every direction back against the terms each trade was booked
with. Positions a close cannot value are covered by the valuation-status check.

The summaries and curve shapes are compared with the reads they replace: the same views read on their own through
`trading/blotter`, and `evaluate` for each curve. The money-totals convention (D216) adds present value and execution P&L
across quantity units within one currency and instrument type; its totals are compared with exact sums of the rows.
"""
from collections import defaultdict
from fractions import Fraction
import json
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from seed_preview_positions import BOOKS
from seed_preview_swaps import FIXED_COEFFICIENT

SWAP_CONVENTION = 'independent_side_financial_swap_v2'
TOTALS, MONEY_TOTALS = 'trading_blotter_totals_v1', 'trading_blotter_money_totals_v1'
TRADES = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
BY_COMMODITY = {**TRADES, 'mode': 'pivot', 'groupBy': ['commodity'], 'measures': ['tradeCount', 'quantity'],
                'pivotConvention': TOTALS}
# What the desk shows from one valuation: tile totals, tile splits and book group headers, the position mix, and the
# charts and market group headers.
DESK_SUMMARIES = [{**TRADES, 'mode': 'pivot', 'groupBy': dimensions, 'measures': ['presentValue', 'executionPnl', 'tradeCount', 'quantity'],
                   'pivotConvention': TOTALS}
                  for dimensions in (['instrumentType'], ['book'], ['commodity', 'instrumentType'], ['market'])]
# Any price measure makes a pivot state the price unit of each market.
PRICE_UNITS = {**BY_COMMODITY, 'groupBy': ['market'], 'measures': ['markMax']}
# D216: the money-totals convention keeps a pivot's dimensions, instrument type and currency apart, and adds across quantity units.
MONEY_MEASURES = ['presentValue', 'executionPnl', 'tradeCount']
MONEY_FIELDS = {'instrumentType', 'currency', 'currencyReference', 'unvaluedPositions', *MONEY_MEASURES}
MEAN_POLICY = 'arithmetic_price_mean_to_even_18_v1'


def money_view(*dimensions):
    """A pivot of money totals and trade counts, grouped by the dimensions."""
    return {**TRADES, 'mode': 'pivot', 'groupBy': list(dimensions), 'measures': MONEY_MEASURES, 'pivotConvention': MONEY_TOTALS}


# Money totals beside quantity pivots: the tile totals and splits as money, the position mix and the markets per quantity unit.
MIXED_SUMMARIES = [money_view('instrumentType'), money_view('book'), *DESK_SUMMARIES[2:]]
# A fixed side's membership coefficient; the floating side takes the opposite sign.
DIRECTIONS = {'-1': 'pay_fixed', '1': 'receive_fixed'}
SEEDED_DIRECTION = DIRECTIONS[FIXED_COEFFICIENT]


def partition(item):
    """What a commodity pivot keeps apart: commodity, instrument type, quantity unit and currency."""
    return (item['commodity']['code'], item['instrumentType'], json.dumps(item['quantitySpecification'], sort_keys=True),
            json.dumps(item['currencyReference'], sort_keys=True))


def exact(value):
    return None if value is None else Fraction(value)


def total(rows, field):
    """The exact sum of a measure over valued rows; None when none is valued or the measure does not apply."""
    values = [r[field] for r in rows if r['unavailableReason'] is None]
    return None if not values or None in values else sum(map(Fraction, values))


def unvalued(rows):
    return len({r['tradeId'] for r in rows if r['unavailableReason'] is not None})


def money_key(item, field=None):
    """What a money group keeps apart: its dimension, instrument type and exact currency; never the quantity unit."""
    return (None if field is None else item[field], item['instrumentType'], json.dumps(item['currencyReference'], sort_keys=True))


def added(amounts):
    """The exact sum of the amounts a set of groups state; None when none states one."""
    known = [Fraction(a) for a in amounts if a is not None]
    return sum(known) if known else None


def check_money_totals(read, check, rows, when):
    """Money pivots add the valued positions of one instrument type and currency across quantity units, and nothing else (D216).

    `read` gives the report of a view at the close the rows were read at. The expectations come from the rows alone.
    """
    # Each pivot's dimension, the row field and the group field that name it, and what its groups state beside the money fields.
    pivots = (('instrument type', 'instrumentType', None, None, set()), ('book', 'book', 'bookId', 'bookId', {'bookId', 'book'}),
              ('market', 'market', 'curve', 'market', {'market'}))
    reports = {}
    for label, dimension, row_field, group_field, stated in pivots:
        reports[dimension] = groups = read(money_view(dimension))['groups']
        expected = defaultdict(list)
        for r in rows:
            expected[money_key(r, row_field)].append(r)
        actual = {money_key(g, group_field): g for g in groups}
        name = f'{when}: the {label} money pivot'
        check(name+' has one group for each combination of its dimension, instrument type and currency, whatever the quantity units',
              len(actual) == len(groups) and actual.keys() == expected.keys())
        check(name+' adds the valued positions of each group exactly, and states null where none is valued or the measure does not apply',
              all(exact(g[m]) == total(expected[k], m) for k, g in actual.items() for m in ('presentValue', 'executionPnl')))
        check(name+' counts the distinct trades of each group, and those it leaves out',
              all(g['tradeCount'] == len({r['tradeId'] for r in expected[k]}) and g['unvaluedPositions'] == unvalued(expected[k]) for k, g in actual.items()))
        check(name+' states its type, currency and measures, and no quantity unit or specification',
              all(set(g) == MONEY_FIELDS | stated for g in groups))
    by_type = {money_key(g): g for g in reports['instrumentType']}
    for label, dimension, *_ in pivots[1:]:
        check(f'{when}: {label} money totals add up to the instrument type totals, exactly',
              all(added(g[m] for g in reports[dimension] if money_key(g) == k) == exact(t[m]) for k, t in by_type.items() for m in ('presentValue', 'executionPnl')))
    check(f'{when}: book trade counts add up to the instrument type counts, as each trade is held in one book',
          all(sum(g[m] for g in reports['book'] if money_key(g) == k) == t[m] for k, t in by_type.items() for m in ('tradeCount', 'unvaluedPositions')))


def check_money_example(read, check, rows):
    """The seeded example holds swaps in more than one quantity unit and futures, all in USD."""
    groups = read(money_view('instrumentType'))['groups']
    check('the example has one swap group and one future group in its one currency, USD',
          len(groups) == 2 and {(g['instrumentType'], g['currency']) for g in groups} == {('financial_swap', 'USD'), ('future', 'USD')})
    swaps = [r for r in rows if r['instrumentType'] == 'financial_swap']
    units = defaultdict(list)
    for r in swaps:
        units[json.dumps(r['quantitySpecification'], sort_keys=True)].append(r)
    swap_pv = exact(next(g for g in groups if g['instrumentType'] == 'financial_swap')['presentValue'])
    check('the swaps are held in more than one quantity unit, and the swap present value is the exact sum over all of them',
          len(units) > 1 and swap_pv == sum(Fraction(r['presentValue']) for r in swaps) and all(total(rs, 'presentValue') != swap_pv for rs in units.values()))


def check_money_refusals(c, check, context):
    """Under the money convention a view or a summary asks for money and trade counts only; the rest is refused (D216)."""
    asked = {'quantity': {}, 'lots': {}, 'markMax': {}, 'fixedPriceMin': {}, 'executionPriceMean': {'priceNumericalPolicy': MEAN_POLICY}}
    everything = {**TRADES, 'mode': 'pivot', 'groupBy': ['contract'], 'measures': ['presentValue', *asked], 'pivotConvention': TOTALS,
                  'priceNumericalPolicy': MEAN_POLICY}
    check('the totals convention still accepts quantity, lots and price statistics beside money',
          c.call('trading/blotter', {'context': context, 'view': everything})['groups'])

    def refused(name, path, body):
        check(name, c.call(path, {'context': context, **body}, expected=422)['code'] == 'blotter_view')
    for measure, changes in asked.items():
        refused(f'the money convention refuses {measure}', 'trading/blotter', {'view': {**money_view('contract'), 'measures': ['presentValue', measure], **changes}})
    quantity = {**money_view('book'), 'measures': ['presentValue', 'quantity']}
    refused('a workspace read refuses quantity in a money view', 'trading/workspace', {'view': quantity})
    refused('a workspace read refuses quantity in a money summary', 'trading/workspace', {'view': TRADES, 'summaries': [quantity]})
    refused('a money view of an unknown convention is refused', 'trading/blotter', {'view': {**money_view('book'), 'pivotConvention': MONEY_TOTALS+'_v2'}})
    refused('a money view takes no price policy, which only price statistics use', 'trading/blotter', {'view': {**money_view('book'), 'priceNumericalPolicy': MEAN_POLICY}})


def check_summaries(c, check, context):
    """The desk's groupings come from the valuation of its own read, and equal the same views read on their own."""
    request = {'context': context, 'view': TRADES, 'bookingCatalog': 'directory'}
    plain = c.call('trading/workspace', request)
    desk = c.call('trading/workspace', {**request, 'summaries': DESK_SUMMARIES})
    on_their_own = [c.call('trading/blotter', {'context': context, 'view': view}) for view in DESK_SUMMARIES]
    check('a workspace read that asks for no summaries returns none, whether it omits them or lists none',
          plain['summaries'] == [] == c.call('trading/workspace', {**request, 'summaries': []})['summaries'])
    check('the four desk summaries come back in request order, each as its view and its groups',
          [set(s) for s in desk['summaries']] == [{'view', 'groups'}]*4 and [s['view'] for s in desk['summaries']] == [r['view'] for r in on_their_own])
    check('each summary has exactly the groups its view gives when read on its own',
          all(s['groups'] for s in desk['summaries']) and [s['groups'] for s in desk['summaries']] == [r['groups'] for r in on_their_own])
    check('asking for summaries adds no valuation: the rows, totals and work counts are those of the read without them',
          all(desk['blotter'][f] == plain['blotter'][f] for f in ('holdings', 'totals', 'work', 'evaluatedContracts', 'unvaluedPositions')))

    scope = [BOOKS[0]]
    book_a = {**request, 'view': {**TRADES, 'bookIds': scope}}
    narrowed = c.call('trading/workspace', {**book_a, 'summaries': [{**s, 'bookIds': scope} for s in DESK_SUMMARIES]})
    own = [c.call('trading/blotter', {'context': context, 'view': {**s, 'bookIds': scope}}) for s in DESK_SUMMARIES]
    check('summaries of a read scoped to one book are that book\'s groups, read on their own',
          {r['bookId'] for r in narrowed['blotter']['holdings']} == set(scope)
          and [s['groups'] for s in narrowed['summaries']] == [r['groups'] for r in own] and narrowed['summaries'] != desk['summaries'])

    def refused(name, summaries, view=TRADES):
        error = c.call('trading/workspace', {**request, 'view': view, 'summaries': summaries}, expected=422)
        check(name, error['code'] == 'blotter_view')
    refused('a fifth summary is refused', [*DESK_SUMMARIES, DESK_SUMMARIES[1]])
    refused('a summary that is not a pivot is refused', [TRADES])
    refused('a summary grouped by an unsupported dimension is refused', [{**DESK_SUMMARIES[0], 'groupBy': ['trader']}])
    refused('a summary without a pivot convention is refused', [{**DESK_SUMMARIES[0], 'pivotConvention': None}])
    refused('a summary of an unknown pivot convention is refused', [{**DESK_SUMMARIES[0], 'pivotConvention': MONEY_TOTALS+'_v2'}])
    refused('a summary of other books than its read is refused', [{**DESK_SUMMARIES[0], 'bookIds': scope}])
    refused('a summary of other portfolios than its read is refused', [{**DESK_SUMMARIES[0], 'portfolioIds': [str(uuid4())]}])
    refused('a summary does not widen a read scoped to one book', [DESK_SUMMARIES[0]], book_a['view'])
    portfolio = plain['catalog']['books'][0]['portfolio_id']
    refused('a summary that repeats a book its read names once is refused, as the Blotter refuses a repeated book',
            [{**DESK_SUMMARIES[0], 'bookIds': scope*2}], book_a['view'])
    refused('a summary that repeats a portfolio its read names once is refused, as the Blotter refuses a repeated portfolio',
            [{**DESK_SUMMARIES[0], 'portfolioIds': [portfolio]*2}], {**TRADES, 'portfolioIds': [portfolio]})

    # Money totals (D216) are summaries like any other: they take the same limits and add no valuation.
    mixed = c.call('trading/workspace', {**request, 'summaries': MIXED_SUMMARIES})
    mixed_alone = [c.call('trading/blotter', {'context': context, 'view': view}) for view in MIXED_SUMMARIES]
    check('four summaries of both pivot conventions come back in request order, each as its view and the groups that view gives on its own',
          [s['view'] for s in mixed['summaries']] == [r['view'] for r in mixed_alone] and all(s['groups'] for s in mixed['summaries'])
          and [s['groups'] for s in mixed['summaries']] == [r['groups'] for r in mixed_alone])
    check('and they add no valuation: the rows, totals and work counts are those of the read without them',
          all(mixed['blotter'][f] == plain['blotter'][f] for f in ('holdings', 'totals', 'work', 'evaluatedContracts', 'unvaluedPositions')))
    check('a money pivot is also the view of a workspace read, with the groups it gives on its own',
          c.call('trading/workspace', {**request, 'view': MIXED_SUMMARIES[0]})['blotter']['groups'] == mixed_alone[0]['groups'])
    narrowed_mix = c.call('trading/workspace', {**book_a, 'summaries': [{**s, 'bookIds': scope} for s in MIXED_SUMMARIES]})
    check('summaries of both conventions of a read scoped to one book are that book\'s groups, read on their own',
          [s['groups'] for s in narrowed_mix['summaries']]
          == [c.call('trading/blotter', {'context': context, 'view': {**s, 'bookIds': scope}})['groups'] for s in MIXED_SUMMARIES]
          and narrowed_mix['summaries'] != mixed['summaries'])
    refused('a fifth summary is refused whichever convention it takes', [*MIXED_SUMMARIES, money_view('market')])
    refused('a money summary of other books than its read is refused', [{**MIXED_SUMMARIES[0], 'bookIds': scope}])
    refused('a money summary of other portfolios than its read is refused', [{**MIXED_SUMMARIES[0], 'portfolioIds': [str(uuid4())]}])
    refused('a money summary that repeats a book its read names once is refused', [{**MIXED_SUMMARIES[0], 'bookIds': scope*2}], book_a['view'])


def check_historical_summaries(c, check):
    """Under a selected historical context the summaries are grouped from the rows read at that context."""
    selected = c.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})['context']
    try:
        past = initial_frame(c.call('workspace'), '2020-12-18')
        context = {'snapshotId': past['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'retained', 'contextId': selected['contextId']}
        read = c.call('trading/workspace', {'context': context, 'view': TRADES, 'bookingCatalog': 'directory', 'summaries': DESK_SUMMARIES})
        on_their_own = [c.call('trading/blotter', {'context': context, 'view': view}) for view in DESK_SUMMARIES]
        check('under a selected historical context, the summaries are those of the rows read at that context',
              read['blotter']['context']['contextId'] == selected['contextId'] == read['catalog']['context']['contextId'] and read['blotter']['holdings']
              and [s['groups'] for s in read['summaries']] == [r['groups'] for r in on_their_own] and all(s['groups'] for s in read['summaries']))
    finally:
        c.call('market/current', {})


def check_curve_shapes(c, check, frame, context, held):
    """One read prices every requested curve exactly as /evaluate does for it, at the same pin and under the same permission,
    and states each curve the pinned data cannot read with the code /evaluate raises for it."""
    def shapes(body, **options):
        return c.call('market/curve-shapes', {'snapshotId': frame['id'], **body}, **options)

    def evaluated_points(snapshot, curve):
        return [{'contractId': p['contractId'], 'deliveryFrom': p['deliveryFrom'], 'deliveryTo': p['deliveryTo'], 'price': p['value'],
                 'unavailableReason': None} for p in c.call('evaluate', {'snapshotId': snapshot, 'curve': curve})['points']]

    read = shapes({'curves': held})
    check('one read returns each curve asked, in request order, as its code, unit, points and no unavailable reason; working data names no context',
          set(read) == {'curves'} and [s['curve'] for s in read['curves']] == held
          and all(set(s) == {'curve', 'unit', 'points', 'unavailableReason'} and s['unavailableReason'] is None for s in read['curves']))
    units = {g['market']: g['priceUnit'] for g in c.call('trading/blotter', {'context': context, 'view': PRICE_UNITS})['groups']}
    for shape in read['curves']:
        expected = evaluated_points(frame['id'], shape['curve'])
        check(f"{shape['curve']}: every point is the contract, delivery period and exact price /evaluate gives, in its order",
              expected and shape['points'] == expected)
        check(f"{shape['curve']}: its unit is the price unit the Blotter names for the market", shape['unit'] == units[shape['curve']])

    # A curve the pinned data cannot read is stated unavailable, with the code its own /evaluate read raises, and the
    # curves beside it are still shaped. A rate curve has no contract prices to shape, so /evaluate's rates are not its shape.
    unreadable = ['unlisted', 'daily_gas', 'interest_rates']
    mixed = shapes({'curves': ['future', *unreadable, 'base_forward']})['curves']
    by_curve = {s['curve']: s for s in read['curves']}
    check('curves the pinned data cannot read are stated unavailable with no unit or points, and do not fail the curves beside them',
          [s['curve'] for s in mixed] == ['future', *unreadable, 'base_forward'] and mixed[0] == by_curve['future'] and mixed[4] == by_curve['base_forward']
          and mixed[1:4] == [{'curve': k, 'unit': None, 'points': None, 'unavailableReason': 'missing_curve'} for k in unreadable])
    check('an unavailable curve states the code its own /evaluate read raises',
          [c.call('evaluate', {'snapshotId': frame['id'], 'curve': k}, expected=422)['code'] for k in ('unlisted', 'daily_gas')] == ['missing_curve']*2)
    many = shapes({'curves': [f'curve_{i}' for i in range(64)]})['curves']
    check('64 curves are within the limit; ones the pinned data does not define are each stated unavailable',
          len(many) == 64 and all(s['unit'] is None and s['points'] is None and s['unavailableReason'] == 'missing_curve' for s in many))

    def refused(name, body, code):
        check(name, shapes(body, expected=422)['code'] == code)
    refused('a request for 65 curves is refused', {'curves': [f'curve_{i}' for i in range(65)]}, 'curve_shapes')
    refused('a request for no curves is refused', {'curves': []}, 'curve_shapes')
    refused('a request that omits its curves is refused', {}, 'curve_shapes')
    refused('a repeated curve is refused', {'curves': ['future', 'future']}, 'curve_shapes')
    refused('a blank curve code is refused', {'curves': ['future', ' ']}, 'curve_shapes')
    unknown = str(uuid4())
    check('an unknown snapshot is refused as /evaluate refuses it',
          c.call('market/curve-shapes', {'snapshotId': unknown, 'curves': ['future']}, expected=422)['code']
          == c.call('evaluate', {'snapshotId': unknown, 'curve': 'future'}, expected=422)['code'] == 'snapshot_missing')

    # A profile that cannot read market data is refused by both reads alike.
    c.call('profile', {'profile': 'admin'})
    try:
        check('a profile that cannot read market data is refused as /evaluate refuses it',
              shapes({'curves': ['future']}, expected=403)['code'] == c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'future'}, expected=403)['code'] == 'access_denied')
    finally:
        c.call('profile', {'profile': 'trader'})

    # Under a selected historical context both reads take the context's own frames, and refuse the working data's.
    c.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})
    try:
        past = initial_frame(c.call('workspace'), '2020-12-18')
        retained = c.call('market/curve-shapes', {'snapshotId': past['id'], 'curves': held})
        check('under a selected historical context, the curves are priced at that context\'s frame exactly as /evaluate prices them',
              past['id'] != frame['id'] and [s['curve'] for s in retained['curves']] == held
              and all(s['points'] and s['points'] == evaluated_points(past['id'], s['curve']) for s in retained['curves']))
        check('and the selected context is stated exactly as /evaluate states it',
              set(retained) == {'curves', 'context'} and retained['context']['contextId']
              and retained['context'] == c.call('evaluate', {'snapshotId': past['id'], 'curve': held[0]})['context'])
        check('under a selected historical context, a frame of the working data is refused by both reads',
              shapes({'curves': held}, expected=422)['code'] == c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'future'}, expected=422)['code'] == 'snapshot_missing')
    finally:
        c.call('market/current', {})
    check('returning to working data prices the same curves again', shapes({'curves': held}) == read)


def run_checks(url):
    passed = 0
    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS desk fields: '+name, flush=True)
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    swap_context = {'snapshotId': frame['id'], 'convention': SWAP_CONVENTION, 'knowledgeMode': 'current'}
    desk_context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    catalog = c.call('swaps/catalog', swap_context)

    # The commodity of each curve, read independently of the Blotter. Navigation lists the curves of the primary
    # definition revision; the swap catalog lists those of every swap market, with the commodity names.
    navigation = c.call('workspace/navigation')
    primary = navigation['definitionRevision']['revisionId']
    categories = {curve['code']: curve['category'] for curve in navigation['curves']}
    names = {commodity['code']: commodity['name'] for commodity in catalog['commodities']}
    swap_commodities = {(i['definitionRevisionId'], i['curve']): i['commodity'] for i in catalog['indices']}
    stored = {}
    check('navigation and the swap catalog agree about the commodity of every curve of the primary definition revision they both list',
          all(commodity == categories[curve]['code'] for (revision, curve), commodity in swap_commodities.items() if revision == primary))

    def read():
        return c.call('trading/blotter', {'context': desk_context, 'view': TRADES})['holdings']

    def booked_direction(row):
        """The direction a swap revision was booked with, from the terms the swap editor reads from storage."""
        key = (row['tradeId'], row['revision'])
        if key not in stored:
            stored[key] = c.call('swaps/editor', {'context': swap_context, 'tradeId': row['tradeId'], 'revision': int(row['revision'])})['state']['terms']
        terms = stored[key]
        return 'multi_side' if terms['swapType'] == 'financial_floating_floating' else DIRECTIONS[terms['fixedCoefficient']]

    def curve_commodity(row):
        """The commodity code of a row's curve, from the directory that lists the curve."""
        if row['definitionRevisionId'] == primary:
            return categories[row['curve']]['code']
        return swap_commodities[(row['definitionRevisionId'], row['curve'])]

    def expected_difference(row):
        """The exact mark less the executed or fixed price; None where a row has no such pair."""
        price = row['executionPrice'] if row['instrumentType'] == 'future' else row['fixedPrice']
        return None if row['unavailableReason'] or row['mark'] is None or price is None else Fraction(row['mark']) - Fraction(price)

    def stated_difference(row):
        return None if row['markMinusPrice'] is None else Fraction(row['markMinusPrice'])

    def field_checks(rows, when):
        futures = [r for r in rows if r['instrumentType'] == 'future']
        swaps = [r for r in rows if r['instrumentType'] == 'financial_swap']
        check(when+': every row names its commodity as a code and a name', all(set(r.get('commodity') or ()) == {'code', 'name'} for r in rows))
        check(when+': a position has the commodity its curve has in navigation, or in the swap catalog for a swap market navigation does not list',
              futures and {r['curve'] for r in swaps} >= {'base_forward', 'hub_power', 'daily_gas', 'daily_power'}
              and all(r['commodity'] == {'code': curve_commodity(r), 'name': names[curve_commodity(r)]} for r in rows))
        check(when+': gas and power positions carry their own commodities', {r['commodity']['code'] for r in rows} >= {'test_gas', 'test_power'})
        check(when+': a future has no direction', futures and all('direction' in r and r['direction'] is None for r in futures))
        check(when+': a swap states the direction it was booked with', swaps and all(r.get('direction') == booked_direction(r) for r in swaps))
        check(when+': mark minus price is the exact mark less the executed or fixed price, and absent where they do not pair',
              all('markMinusPrice' in r and stated_difference(r) == expected_difference(r) for r in rows))
        check(when+': every future states it, and so does every monthly swap with a mark and a fixed price',
              all(r['markMinusPrice'] is not None for r in futures)
              and all(r['markMinusPrice'] is not None for r in swaps if r['curve'] in ('base_forward', 'hub_power') and r['fixedPrice'] is not None))
        check(when+': a daily-average swap has no single mark, so no mark minus price',
              all(r['mark'] is None and r['markMinusPrice'] is None for r in swaps if r['curve'] in ('daily_gas', 'daily_power')))

    rows = read()
    check('the seeded example holds eight futures and sixteen swaps', len(rows) == 24)
    field_checks(rows, 'seeded')
    check(f'the swaps the example seeds state the direction its seeding books, {SEEDED_DIRECTION}',
          {r['direction'] for r in rows if r['instrumentType'] == 'financial_swap'} == {SEEDED_DIRECTION})

    # Two swaps the seeding does not book: receive fixed, and two floating sides, which have no fixed leg to pay or receive.
    payment = catalog['entryTemplate']['payment']
    base = next(i for i in catalog['indices'] if i['curve'] == 'base_forward')
    local = next(i for i in catalog['indices'] if i['curve'] == 'local_gas')

    def floating(index):
        return {'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01',
                'deliveryTo': '2021-02-01', 'paymentSchedule': payment, 'resetFrequency': 'monthly', 'averagingMethod': 'none'}
    common = {**swap_context, **trade_booking(), 'definitionRevisionId': base['definitionRevisionId'], 'commodity': base['commodity'],
              'bookId': BOOKS[0], 'quantity': '1000'}
    receive = {**common, 'operationId': str(uuid4()), 'fixedCoefficient': '1', 'discounting': True, 'floating': floating(base),
               'fixed': {'price': '3.5', 'currency': base['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': payment}}
    both_floating = {**common, 'operationId': str(uuid4()), 'swapType': 'financial_floating_floating', 'firstCoefficient': '-1', 'fixedCoefficient': None,
                     'fixed': None, 'discounting': False, 'firstFloating': {**floating(base), 'spread': '0'}, 'floating': {**floating(local), 'spread': '0'}}
    received, paired = c.call('swaps/book', receive), c.call('swaps/book', both_floating)
    rows = read()
    held = {r['tradeId']: r for r in rows}
    check('a receive-fixed swap states receive fixed and a floating/floating swap states multi-side',
          held[received['tradeId']]['direction'] == 'receive_fixed' and held[paired['tradeId']]['direction'] == 'multi_side')
    check('the floating/floating swap has a mark but no fixed price, so no mark minus price',
          held[paired['tradeId']]['mark'] is not None and held[paired['tradeId']]['fixedPrice'] is None and held[paired['tradeId']]['markMinusPrice'] is None)
    check('the receive-fixed swap states its mark less its fixed price of 3.5',
          stated_difference(held[received['tradeId']]) == Fraction(held[received['tradeId']]['mark']) - Fraction('3.5'))
    field_checks(rows, 'with a receive-fixed and a floating/floating swap')

    # A row states its trade's latest revision, so an amendment that changes the direction changes the row.
    c.call('swaps/amend', {**receive, 'operationId': str(uuid4()), 'tradeId': received['tradeId'], 'expectedRevision': 1,
                           'effectiveAt': catalog['asOf'], 'reason': 'Synthetic direction change', 'fixedCoefficient': '-1'})
    rows = read()
    amended = {r['tradeId']: r for r in rows}[received['tradeId']]
    check('amending a swap to pay fixed changes the direction its latest revision states',
          amended['revision'] == '2' and amended['direction'] == booked_direction(amended) == 'pay_fixed')
    workspace = c.call('trading/workspace', {'context': desk_context, 'view': TRADES, 'bookingCatalog': 'directory'})['blotter']['holdings']
    fields = ('commodity', 'direction', 'markMinusPrice')
    check('the trading workspace read carries the same fields as the Blotter read',
          {(r['tradeId'], r['bindingId']): [r[f] for f in fields] for r in workspace} == {(r['tradeId'], r['bindingId']): [r[f] for f in fields] for r in rows})

    report = c.call('trading/blotter', {'context': desk_context, 'view': BY_COMMODITY})
    partitions = defaultdict(list)
    for row in rows:
        partitions[partition(row)].append(row)
    groups = {partition(g): g for g in report['groups']}
    check('a commodity pivot has one group per commodity, instrument type, unit and currency',
          len(groups) == len(report['groups']) and groups.keys() == partitions.keys() and {k[0] for k in groups} == {'test_gas', 'test_power'})
    check('each group carries its commodity and counts the trades of its rows',
          all(g['commodity'] == partitions[k][0]['commodity'] and (g['unit'], g['currency']) == (partitions[k][0]['unit'], partitions[k][0]['currency'])
              and g['tradeCount'] == len({r['tradeId'] for r in partitions[k]}) for k, g in groups.items()))
    check('each group adds the quantities of its rows exactly',
          all(Fraction(g['quantity']) == sum(Fraction(r['quantity']) for r in partitions[k]) for k, g in groups.items()))
    check('the groups count every trade once', sum(g['tradeCount'] for g in groups.values()) == len({r['tradeId'] for r in rows}))

    # What the desk reads for its tiles, charts and sparklines. Both are compared with the reads they replace, under
    # the one context the commodity pivot was read at, so they see the same data.
    retained = {**desk_context, 'knowledgeMode': 'retained', 'contextId': report['context']['contextId']}

    def read_money(view):
        return c.call('trading/blotter', {'context': retained, 'view': view})
    check_money_totals(read_money, check, rows, 'seeded example')
    check_money_example(read_money, check, rows)
    check_money_refusals(c, check, retained)
    check_summaries(c, check, retained)
    check_historical_summaries(c, check)
    held = sorted({r['curve'] for r in rows if r['definitionRevisionId'] == primary})
    check('the example holds futures and swaps on curves of its primary definition revision', {'base_forward', 'future', 'hub_power'} <= set(held))
    check_curve_shapes(c, check, frame, retained, held)
    print(f'{passed} desk field checks passed.', flush=True)
