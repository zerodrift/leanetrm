"""Side observation authoring through the same real draft and booking operations."""
from copy import deepcopy
from fractions import Fraction
from datetime import date, timedelta
from uuid import uuid4

from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest, psql=None, environment=None):
    if psql is not None:
        from check_side_observation_upgrade import run_checks as check_upgrade
        check_upgrade(query, psql, environment)
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'trader'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    catalog = client.call('swaps/catalog', context)
    from check_side_observation_options import run_checks as check_options
    check_options(client, context, catalog, manifest)
    source = next(index for index in catalog['indices'] if index['curve'] == 'daily_gas')
    specification = next(choice['specification'] for choice in source['quantityChoices'] if choice['unit'] == 'MMBtu (IT)')
    calendar = {'revision': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId']}, 'code': 'test_business'}
    rule = {'frequency': 'monthly', 'rule': 'day_of_following_month', 'days': None, 'dayOfMonth': 18,
            'dayBasis': None, 'shortMonth': 'reject', 'adjustment': 'unadjusted', 'calendar': None,
            'implementation': 'monthly_payment_date_rules_v1'}
    fixed = {'price': '3', 'currency': source['currencyCode'], 'deliveryFrom': '2021-01-01',
             'deliveryTo': '2021-02-01', 'paymentSchedule': rule}
    floating = {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': source['curve'], 'index': source['index'], 'currency': source['currencyCode'],
                'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': rule,
                'resetFrequency': 'daily', 'averagingMethod': 'observation_arithmetic',
                'observationSchedule': {'implementation': 'side_observation_schedule_v1', 'calendar': calendar, 'quantities': None}}
    draft = {'context': context, 'definitionRevisionId': source['definitionRevisionId'], 'commodity': source['commodity'],
             'quantity': {'value': '100', 'specification': specification}, 'fixedCoefficient': '-1',
             'discounting': False, 'fixed': fixed, 'floating': floating}
    priced = client.call('swaps/price', draft)
    assert Fraction(priced['valuation']['presentValue']) == Fraction(33, 2), 'The holiday-aware 20-observation mean must price independently to 16.5 USD'
    print('PASS side observations: authored observation-day mean uses the exact calendar and real draft pricer', flush=True)
    booking = {**context, 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'], **trade_booking(),
               'definitionRevisionId': source['definitionRevisionId'], 'commodity': source['commodity'],
               'quantity': '100', 'fixedCoefficient': '-1', 'discounting': False, 'fixed': fixed, 'floating': floating}
    _unknown_reference_fields(client, query, draft, booking)
    receipt = client.call('swaps/book', booking)
    detail = client.call('swaps/editor', {'context': context, 'tradeId': receipt['tradeId'], 'revision': None})
    assert detail['state']['terms']['floating']['observationSchedule'] == floating['observationSchedule']
    assert Fraction(detail['valuation']['presentValue']) == Fraction(33, 2)
    print('PASS side observations: booking retains and replays exact observation authoring and managed PV', flush=True)
    assert client.call('swaps/book', booking) == receipt, 'Exact new-mode booking replay must retain its control receipt'
    valued = detail['valuation']['sides'][1]['payments'][0]['observationSchedule']
    assert valued['denominator'] == '20' and len(valued['observations']) == len(valued['applications']) == 20
    assert all(row['lookup'] and row['fixing'] and row['expectedPublication'] for row in valued['observations'])
    assert all(row['fixingState'] == 'Projected' for row in valued['observations'])
    assert sum((Fraction(row['cashContribution']) for row in valued['applications']), Fraction()) == Fraction(633, 200)
    print('PASS side observations: actual valued SideSchedule retains normalization and exact application/price evidence', flush=True)
    # These independently specified target-to-observation day vectors include
    # December 31 and February 1; they must not be clipped to January.
    previous = [0, 0, 0, 4, 5, 6, 7, 8, 8, 8, 11, 12, 13, 14, 15, 15, 15, 18, 19, 20, 21, 22, 22, 22, 25, 26, 27, 28, 29, 29, 29]
    following = [4, 4, 4, 4, 5, 6, 7, 8, 11, 11, 11, 12, 13, 14, 15, 18, 18, 18, 19, 20, 21, 22, 25, 25, 25, 26, 27, 28, 29, 32, 32]
    for method, mapped, expected in [('calendar_days_previous', previous, Fraction(478, 31)), ('calendar_days_next', following, Fraction(514, 31))]:
        terms = {**floating, 'averagingMethod': method}
        result = client.call('swaps/price', {**draft, 'floating': terms})
        assert Fraction(result['valuation']['presentValue']) == expected
        command = {**booking, 'operationId': str(uuid4()), 'floating': terms}
        saved = client.call('swaps/book', command)
        retained = client.call('swaps/editor', {'context': context, 'tradeId': saved['tradeId'], 'revision': None})
        assert Fraction(retained['valuation']['presentValue']) == expected
        inspection = retained['valuation']['sides'][1]['payments'][0]['observationSchedule']
        observed_dates = [row['from'] for row in inspection['observations']]
        assert [observed_dates[row['observationOrdinal']] for row in inspection['applications']] == [str(date(2021, 1, 1)+timedelta(days=n-1)) for n in mapped]
        assert inspection['denominator'] == '31' and len(inspection['applications']) == 31
        expected_guard = '2020-12-31' if method == 'calendar_days_previous' else '2021-02-01'
        assert [row['from'] for row in inspection['observations'] if row['isGuard']] == [expected_guard]
        print('PASS side observations: '+method+' retains exact outside-month guards, represented days and rational booked PV', flush=True)
    quantities = [{'from': str(date(2021, 1, day)), 'to': str(date(2021, 1, day)+timedelta(days=1)),
                   'quantity': {'value': str(day), 'specification': specification}} for day in range(1, 32)]
    weighted = {**floating, 'averagingMethod': 'quantity_weighted', 'observationSchedule': {
        'implementation': 'side_observation_schedule_v1', 'calendar': None, 'quantities': quantities}}
    weighted_price = client.call('swaps/price', {**draft, 'floating': weighted})
    assert Fraction(weighted_price['valuation']['presentValue']) == 21
    weighted_booking = {**booking, 'operationId': str(uuid4()), 'floating': weighted}
    weighted_receipt = client.call('swaps/book', weighted_booking)
    weighted_detail = client.call('swaps/editor', {'context': context, 'tradeId': weighted_receipt['tradeId'], 'revision': None})
    assert Fraction(weighted_detail['valuation']['presentValue']) == 21
    assert weighted_detail['state']['terms']['floating']['observationSchedule'] == weighted['observationSchedule']
    weighted_view = weighted_detail['valuation']['sides'][1]['payments'][0]['observationSchedule']
    assert weighted_view['denominator'] == '496' and [row['weight'] for row in weighted_view['observations']] == [str(n) for n in range(1, 32)]
    assert not any(row['isGuard'] for row in weighted_view['observations'])
    print('PASS side observations: all positive typed daily quantities survive normalized storage, repricing and exact denominator evidence', flush=True)
    from check_swap_observation_reads import run_checks as check_reads
    check_reads(client, query, context, booking, weighted_receipt['tradeId'])
    _weighted_actuals_and_risk(client, query, context, draft, weighted, weighted_booking, weighted_receipt, weighted_price)
    from check_side_observation_composition import run_checks as check_composition
    check_composition(client, query, context, draft, weighted, weighted_booking)
    before_publications = query('SELECT count(*) FROM trading.publications;')
    invalid = []
    missing = deepcopy(floating); missing.pop('observationSchedule'); invalid.append(missing)
    missing_calendar = deepcopy(floating); missing_calendar['observationSchedule']['calendar'] = None; invalid.append(missing_calendar)
    wrong_revision = deepcopy(floating); wrong_revision['observationSchedule']['calendar']['revision']['revisionId'] = manifest['revisionId']; invalid.append(wrong_revision)
    unsupported = deepcopy(floating); unsupported['observationSchedule']['implementation'] = 'unsupported'; invalid.append(unsupported)
    for value in ('0', '-1'):
        bad = deepcopy(weighted); bad['observationSchedule']['quantities'][0]['quantity']['value'] = value; invalid.append(bad)
    for change in ('missing', 'duplicate', 'reversed', 'wrong_specification', 'calendar'):
        bad = deepcopy(weighted)
        if change == 'missing': bad['observationSchedule']['quantities'].pop()
        elif change == 'duplicate': bad['observationSchedule']['quantities'][1] = deepcopy(bad['observationSchedule']['quantities'][0])
        elif change == 'reversed': bad['observationSchedule']['quantities'].reverse()
        elif change == 'wrong_specification': bad['observationSchedule']['quantities'][0]['quantity']['specification']['revision']['revisionId'] = manifest['revisionId']
        else: bad['observationSchedule']['calendar'] = calendar
        invalid.append(bad)
    for bad in invalid:
        client.call('swaps/price', {**draft, 'floating': bad}, expected=422)
        client.call('swaps/book', {**booking, 'operationId': str(uuid4()), 'floating': bad}, expected=422)
    assert query('SELECT count(*) FROM trading.publications;') == before_publications
    print('PASS side observations: missing, conflicting, zero/negative, incomplete, duplicate and wrong-version terms fail atomically', flush=True)
    # Equivalent exact authoring shares one unit definition; changed weights do
    # not. Neither decision depends on a browser's labels or float arithmetic.
    equivalent = deepcopy(weighted_booking); equivalent['operationId'] = str(uuid4())
    for row in equivalent['floating']['observationSchedule']['quantities']:
        row['quantity']['value'] += '.0'
    shared = client.call('swaps/book', equivalent)
    assert shared['instrumentId'] == weighted_receipt['instrumentId']
    changed = deepcopy(weighted_booking)
    changed.update(operationId=str(uuid4()), tradeId=weighted_receipt['tradeId'], expectedRevision=1,
                   effectiveAt=catalog['asOf'], reason='Synthetic observation quantity correction')
    changed['floating']['observationSchedule']['quantities'][0]['quantity']['value'] = '2'
    amended = client.call('swaps/amend', changed)
    assert amended['instrumentId'] != weighted_receipt['instrumentId'] and amended['revision'] == 2
    current = client.call('swaps/editor', {'context': context, 'tradeId': weighted_receipt['tradeId'], 'revision': None})
    original = client.call('swaps/editor', {'context': context, 'tradeId': weighted_receipt['tradeId'], 'revision': 1})
    assert Fraction(current['valuation']['presentValue']) == Fraction(10417, 497)
    assert Fraction(original['valuation']['presentValue']) == 21 and original['state']['instrumentId'] == weighted_receipt['instrumentId']
    assert client.call('swaps/book', weighted_booking) == weighted_receipt
    conflict = deepcopy(weighted_booking); conflict['floating']['observationSchedule']['quantities'][0]['quantity']['value'] = '2'
    client.call('swaps/book', conflict, expected=409)
    print('PASS side observations: canonical typed plans share exact unit identity, changed weights append a new Side, and original receipts/revisions replay', flush=True)
    from check_side_observation_storage import run_checks as check_storage
    check_storage(query, manifest, booking, weighted_booking)
def _weighted_actuals_and_risk(client, query, context, draft, floating, booking, original, projected):
    """Pinned final facts replace only due resets; exact scenario PVs price the remaining quotes."""
    def risk(terms, price, first_unfixed, pv):
        retained = {**terms, 'context': {**terms['context'], 'knowledgeMode': 'retained',
                                        'contextId': price['context']['contextId']}}
        result = client.call('swaps/draft-risk', {'draft': retained, 'bumpPolicy': price['bumpPolicy'],
                                                'numericalPolicy': price['numericalPolicy']})
        expected_dates = {str(date(2021, 1, day)): day for day in range(first_unfixed, 32)}
        assert len(result['factors']) == len(expected_dates) and result['scenarioCount'] == 1+2*len(expected_dates)
        assert {row['factor']['deliveryFrom'] for row in result['factors']} == set(expected_dates)
        for row in result['factors']:
            # A one-dollar move in day d contributes 100*d/(1+...+31).
            delta = Fraction(100*expected_dates[row['factor']['deliveryFrom']], 496)
            movement = delta*Fraction(row['bump'])
            assert Fraction(row['basePv']) == pv and Fraction(row['delta']) == delta and Fraction(row['gamma']) == 0
            assert Fraction(row['upPnl']) == movement and Fraction(row['downPnl']) == -movement
            assert Fraction(row['upPv']) == pv+movement and Fraction(row['downPv']) == pv-movement
        return result

    before = query('SELECT count(*) FROM trading.publications;')
    projected_terms = {**draft, 'floating': floating}
    risk(projected_terms, projected, 1, Fraction(21))
    workspace = client.call('workspace')
    initial_ids = set(workspace['initialFrameIds'])
    def frame(at):
        matches = [row for row in workspace['frames'] if row['id'] in initial_ids and row['at'].startswith(at)]
        assert len(matches) == 1, 'Actual-fixing checks require one explicitly selected initial snapshot instant.'
        return matches[0]
    partial_context = {**context, 'snapshotId': frame('2021-01-15T08:00:00')['id']}
    partial_terms = {**projected_terms, 'context': partial_context}
    partial = client.call('swaps/price', partial_terms)
    assert Fraction(partial['valuation']['presentValue']) == Fraction(57, 2)
    inspection = partial['valuation']['sides'][1]['payments'][0]['observationSchedule']
    assert inspection['denominator'] == '496'
    assert [row['fixingState'] for row in inspection['observations']] == ['Actual']*15+['Projected']*16
    assert all(row['lineage'] for row in inspection['observations'])
    risk(partial_terms, partial, 16, Fraction(57, 2))
    assert query('SELECT count(*) FROM trading.publications;') == before, 'Draft scenario work must not book a trade.'
    print('PASS side observations: exact weighted scenarios retain 31 projected deltas, then freeze 15 actuals and shock only 16 unresolved quotes', flush=True)

    saved = client.call('swaps/book', {**booking, **partial_context, 'operationId': str(uuid4())})
    assert saved['instrumentId'] == original['instrumentId'], 'Actual market facts must not replace the unit instrument identity.'
    detail = client.call('swaps/editor', {'context': partial_context, 'tradeId': saved['tradeId'], 'revision': None})
    assert Fraction(detail['valuation']['presentValue']) == Fraction(57, 2)
    assert detail['state']['terms']['floating']['observationSchedule'] == floating['observationSchedule']
    assert detail['valuation']['sides'][1]['payments'][0]['observationSchedule'] == inspection
    before_missing = query('SELECT count(*) FROM trading.publications;')
    missing_context = {**context, 'snapshotId': frame('2021-01-15T09:00:00')['id']}
    period_query = {'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'definitionRevisionId': draft['definitionRevisionId'], 'commodity': draft['commodity'],
                    'curve': floating['curve'], 'index': floating['index'], 'resetFrequency': 'daily', 'averagingMethod': 'quantity_weighted'}
    partial_dates = client.call('swaps/observation-options', {**period_query, 'context': partial_context})
    assert any(p['deliveryFrom'] == '2021-01-01' for p in partial_dates['deliveryPeriods'])
    missing_dates = client.call('swaps/observation-options', {**period_query, 'context': missing_context})
    assert not any(p['deliveryFrom'] == '2021-01-01' for p in missing_dates['deliveryPeriods']), 'A month with a missing due actual fixing remained selectable.'
    missing_selection = client.call('swaps/observation-options', {**period_query, 'context': missing_context,
        'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01'})
    assert missing_selection['availability'] and missing_selection['availability']['code'] == 'index_fixing_missing', 'Selected observations must report the same missing actual as delivery availability.'
    assert not any(i['curve'] == floating['curve'] for i in missing_selection['indices']), 'An unpriceable selected month remained an offered index.'
    missing = client.call('swaps/price', {**projected_terms, 'context': missing_context}, expected=422)
    assert missing['code'] == 'index_fixing_missing'
    rejected = client.call('swaps/book', {**booking, **missing_context, 'operationId': str(uuid4())}, expected=422)
    assert rejected['code'] == 'index_fixing_missing'
    assert query('SELECT count(*) FROM trading.publications;') == before_missing
    print('PASS side observations: partially fixed booking reuses exact unit terms; an omitted due actual refuses price and booking atomically', flush=True)


def _unknown_reference_fields(client, query, draft, booking):
    """A submitted reference policy cannot disappear during JSON materialization."""
    before = query('SELECT count(*) FROM trading.publications;')
    for location in ('calendar', 'calendar_revision', 'quantity_specification', 'quantity_revision'):
        malformed = deepcopy(draft)
        if location.startswith('calendar'):
            malformed['floating']['averagingMethod'] = 'calendar_days_previous'
            selected = malformed['floating']['observationSchedule']['calendar']
            if location == 'calendar_revision':
                selected = selected['revision']
        else:
            malformed['floating']['averagingMethod'] = 'quantity_weighted'
            malformed['floating']['observationSchedule'] = {'implementation': 'side_observation_schedule_v1', 'calendar': None,
                'quantities': [{'from': str(date(2021, 1, day)), 'to': str(date(2021, 1, day)+timedelta(days=1)),
                                'quantity': {'value': '1', 'specification': deepcopy(draft['quantity']['specification'])}}
                               for day in range(1, 32)]}
            selected = malformed['floating']['observationSchedule']['quantities'][0]['quantity']['specification']
            if location == 'quantity_revision':
                selected = selected['revision']
        selected['boundaryPolicy'] = 'reject'
        client.call('swaps/price', malformed, expected=400)
        client.call('swaps/book', {**booking, 'operationId': str(uuid4()), 'floating': malformed['floating']}, expected=400)
    assert query('SELECT count(*) FROM trading.publications;') == before
    print('PASS side observations: unknown calendar and quantity-reference fields reject rather than silently dropping a submitted policy', flush=True)
