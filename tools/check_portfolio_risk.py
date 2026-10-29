"""Mixed portfolio risk checks in the caller's disposable synthetic preview."""
from collections import Counter, defaultdict
from copy import deepcopy
from fractions import Fraction
import json
import re
from uuid import uuid4

from preview_client import initial_frame


AMOUNTS = ('base', 'up', 'down', 'upPnl', 'downPnl', 'delta', 'gamma')
VIEW = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
        'measures': [], 'pivotConvention': None}
UI_CAPABILITIES = {'canRunDealRisk', 'canBook', 'canAmendTrades', 'bookingUnavailableReason'}


def financial(report):
    result = deepcopy({key: value for key, value in report.items() if key not in UI_CAPABILITIES})
    # Numerical memoization changes physical work across process/cache histories.
    # Keep financial evidence, topology, scenarios and unit-pricer counts exact.
    def physical(work, fields):
        for field in fields:
            value = work.pop(field)
            assert type(value) is int and value >= 0, (field, value)
    physical(result['work'], ('rebuiltCurveNodes', 'reusedCurveNodes'))
    for scenario in result['connectedScenarios']:
        physical(scenario['work'], ('evaluatedNodes', 'reusedReads', 'affectedNodes', 'traversedAffectedEdges'))
    return result


def identity(row):
    return row['tradeId'], row['bindingId']


def partition(row):
    values = row['values']
    return (row['measure'], row['bookId'],
            json.dumps(values['currencyReference'], sort_keys=True),
            values['deltaUnit'], values['gammaUnit'])


def rounded(value, places):
    """Independent exact nearest-even display oracle; no floating point."""
    scale = 10 ** places
    whole, remainder = divmod(abs(value.numerator) * scale, value.denominator)
    if remainder * 2 > value.denominator or remainder * 2 == value.denominator and whole % 2:
        whole += 1
    return Fraction(-whole if value < 0 else whole, scale)


def reconcile(report, holdings, check, label):
    rows = {identity(row): row for row in holdings['holdings']}
    check(label + ' retains every selected binding and the shared financial context',
          report['context'] == holdings['context']
          and report['work']['positionCount'] == len(rows)
          and {identity(row) for row in report['inputs']['positions']} == set(rows)
          and len(report['inputs']['positions']) == len(rows))
    check(label + ' retains every main and supplemental market with its exact observation pins',
          len(report['inputs']['markets']) > 1
          and all(market['observations'] and market['snapshotId'] and market['definitions']
                  for market in report['inputs']['markets']))
    check(label + ' declares a display-only rounding policy beside exact amounts',
          report['displayPolicy'] == {'implementation': 'risk_decimal_display_v1',
                                      'decimalPlaces': 12, 'tieRule': 'to_even'})
    for factor in report['factors']:
        positions = factor['positions']
        check(label + ' contains exactly one row per holding under each common factor',
              len(positions) == len(rows) and {identity(row) for row in positions} == set(rows))
        sums = defaultdict(lambda: {field: Fraction(0) for field in AMOUNTS})
        h = Fraction(factor['bump'])
        for position in positions:
            values = {field: Fraction(position['values'][field]) for field in AMOUNTS}
            for field in AMOUNTS:
                sums[partition(position)][field] += values[field]
            assert values['upPnl'] == values['up'] - values['base'], label + ' up change'
            assert values['downPnl'] == values['down'] - values['base'], label + ' down change'
            assert values['delta'] == (values['up'] - values['down']) / (2 * h), label + ' delta'
            assert values['gamma'] == (values['up'] + values['down'] - 2 * values['base']) / (h * h), label + ' gamma'
        totals = factor['totals']
        check(label + ' exact book totals reconcile holdings without merging valuation measures or currencies',
              len({partition(row) for row in totals}) == len(totals)
              and {partition(row) for row in totals} == set(sums)
              and all({field: Fraction(row['values'][field]) for field in AMOUNTS} == sums[partition(row)]
                      for row in totals))
        for row in [*positions, *totals]:
            assert row['measureLabel'] and row['values']['currency'], label + ' measure and currency labels'
            for field in AMOUNTS:
                display = row['values']['display'][field]
                assert isinstance(display, str) and re.fullmatch(r'-?\d+(?:\.\d+)?', display), label + ' decimal display'
                assert Fraction(display) == rounded(Fraction(row['values'][field]), 12), label + ' exact display rounding'
        check(label + ' base/up/down and derivative values remain exact while display uses declared precision')


def run_checks(Client, check, run_worker):
    risk = Client(); risk.bootstrap(); risk.call('profile', {'profile': 'risk'})
    frame = initial_frame(risk.call('workspace'), '2020-12-18')
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    holdings = risk.call('trading/blotter', {'context': context, 'view': VIEW})
    check('portfolio fixture contains all eight futures and sixteen monthly/daily swaps',
          Counter(row['instrumentType'] for row in holdings['holdings'])
          == {'future': 8, 'financial_swap': 16}
          and len({row['definitionRevisionId'] for row in holdings['holdings']}) > 1)
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': holdings['context']['contextId']}
    catalog = risk.call('portfolio/risk/catalog', {'context': retained, 'bookIds': [], 'portfolioIds': []})
    check('portfolio factor discovery uses the same retained trading context and explicit pricing policies',
          catalog['context'] == holdings['context']
          and catalog['convention'] == 'portfolio_instrument_repricing_v1'
          and catalog['numericalPolicy'] == 'exact_rational_portfolio_repricing_v1'
          and catalog['pricingPolicy'])
    factor = next(f for f in catalog['factors'] if 'future' in f['curves'] and f['deliveryFrom'] == '2021-01-01')
    daily_factor = next(f for f in catalog['factors'] if 'daily_gas' in f['curves'] and f['deliveryFrom'] == '2021-01-01')
    request = {'context': retained, 'bookIds': [], 'portfolioIds': [], 'detail': 'holdings',
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
               'pricingPolicy': catalog['pricingPolicy'], 'stress': [],
               'bumps': [{'factor': factor['factor'], 'size': '0.1',
                          'convention': catalog['bumpConvention']}]}
    result = risk.call('portfolio/risk', request)
    reconcile(result, holdings, check, 'Mixed portfolio risk')
    rows = {identity(row): row for row in holdings['holdings']}
    check('portfolio risk keeps futures execution P&L separate from swap PV',
          {row['measure'] for row in result['factors'][0]['totals']}
          == {'futures_execution_pnl', 'swap_present_value'})
    for position in result['factors'][0]['positions']:
        holding = rows[identity(position)]
        expected = holding['executionPnl'] if holding['instrumentType'] == 'future' else holding['presentValue']
        assert Fraction(position['values']['base']) == Fraction(expected), 'Base risk must use normal holding valuation'
        if holding['instrumentType'] == 'future':
            assert Fraction(position['values']['delta']) == Fraction(holding['quantity']), 'Independent futures quantity oracle'
    check('unchanged risk bases reconcile normal futures P&L and swap PV without an alternate pricing formula')
    declared = {f['seriesId'] for f in catalog['factors']}
    unheld = next(f for f in catalog['availableFactors'] if f['seriesId'] not in declared)
    unheld_request = {**request, 'bumps': [{**request['bumps'][0], 'factor': unheld['factor']}]}
    untouched = risk.call('portfolio/risk', unheld_request)
    check('an available unheld factor returns explicit zero risk for every holding without extra unit valuations',
          untouched['work']['unitPricerCalls'] == untouched['work']['uniqueValuations']
          and len(untouched['factors'][0]['positions']) == len(rows)
          and all(row['values']['base'] == row['values']['up'] == row['values']['down']
                  and all(Fraction(row['values'][field]) == 0 for field in ('upPnl', 'downPnl', 'delta', 'gamma'))
                  for row in untouched['factors'][0]['positions']))
    irrelevant_stress = {'factor': unheld['factor'], 'size': '0.2',
                         'convention': catalog['stressConvention']}
    unheld_stress = risk.call('portfolio/risk', {**request, 'stress': [irrelevant_stress]})
    check('a temporary unheld-factor stress preserves the explicit scenario while leaving held financial values unchanged',
          unheld_stress['scenario']['depth'] == 1
          and unheld_stress['factors'][0]['positions'] == result['factors'][0]['positions']
          and unheld_stress['factors'][0]['totals'] == result['factors'][0]['totals'])
    daily = risk.call('portfolio/risk', {**request, 'bumps': [{**request['bumps'][0],
                      'factor': daily_factor['factor']}]})
    reconcile(daily, holdings, check, 'Daily supplemental quote risk')
    affected = []
    for position in daily['factors'][0]['positions']:
        holding = rows[identity(position)]
        values = position['values']
        if holding['curve'] == 'daily_gas':
            affected.append(position)
            expected = Fraction(holding['quantity']) / 31
            assert Fraction(values['delta']) == expected, 'One of 31 equally weighted daily observations'
            assert Fraction(values['upPnl']) == expected / 10 and Fraction(values['downPnl']) == -expected / 10
        else:
            assert all(Fraction(values[field]) == 0 for field in ('upPnl', 'downPnl', 'delta', 'gamma')), 'Unrelated holdings retain explicit zero risk'
            assert values['base'] == values['up'] == values['down'], 'Unrelated holding values stay unchanged'
    check('one daily quote shocks its four held swaps with exact quantity/31 risk and leaves other holdings unchanged',
          len(affected) == 4 and any('/' in row['values']['delta'] for row in affected))
    summary = risk.call('portfolio/risk', {**request, 'detail': 'books'})
    check('mixed book-only results reconcile the complete holding report',
          summary['detail'] == 'Books' and summary['factors'][0]['positions'] == []
          and summary['factors'][0]['totals'] == result['factors'][0]['totals'])
    shift = {'factor': factor['factor'], 'size': '0.2', 'convention': catalog['stressConvention']}
    parent_request = {**request, 'stress': [shift]}
    parent = risk.call('portfolio/risk', parent_request)
    child = risk.call('portfolio/risk', {**request, 'stress': [shift, {**shift, 'size': '-0.05'}]})
    reconcile(child, holdings, check, 'Nested mixed risk')
    check('mixed nested contexts preserve distinct parents under the original financial pins',
          parent['scenario']['depth'] == 1 and child['scenario']['depth'] == 2
          and child['scenario']['parentId'] == parent['scenario']['id']
          and parent['context'] == child['context'] == result['context'])
    originals = {identity(row): row for row in result['factors'][0]['positions']}
    for position in child['factors'][0]['positions']:
        holding = rows[identity(position)]
        if holding['instrumentType'] == 'future':
            assert Fraction(position['values']['base']) == Fraction(originals[identity(position)]['values']['base']) + Fraction(holding['quantity']) * Fraction('0.15')
    check('leaving nested stresses restores exact parent and original results without modifying normal valuations',
          financial(risk.call('portfolio/risk', parent_request)) == financial(parent)
          and financial(risk.call('portfolio/risk', request)) == financial(result)
          and risk.call('trading/blotter', {'context': retained, 'view': VIEW}) == holdings)
    for field in ('pricingPolicy', 'convention', 'numericalPolicy'):
        invalid = deepcopy(request); del invalid[field]
        risk.call('portfolio/risk', invalid, expected=422)
    invalid_policy = deepcopy(request); del invalid_policy['pricingPolicy']['swapConvention']
    risk.call('portfolio/risk', invalid_policy, expected=422)
    risk.call('portfolio/risk', {**request, 'context': {**retained, 'convention': 'fixed_total_cash_execution_pnl_v1'}}, expected=422)
    check('generic risk rejects missing policies and a product-specific context instead of supplying financial defaults')
    operation = {'requestId': str(uuid4()), 'request': parent_request}
    job = risk.call('portfolio/risk/jobs', operation)
    check('mixed queued risk captures all holdings under the same retained context',
          job['state'] == 'queued' and job['context'] == parent['context'])
    check('mixed queue retries retain the original run identity', risk.call('portfolio/risk/jobs', operation)['id'] == job['id'])
    risk.call('portfolio/risk/jobs', {**operation, 'request': request}, expected=409)
    risk.call('market/context', {'effectiveOn': '2020-12-17', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    run_worker()
    completed = risk.call('portfolio/risk/jobs/' + job['id'])
    check('mixed worker completes despite a later UI date selection', completed['state'] == 'completed' and completed['context'] == parent['context'])
    report = completed['result']
    check('mixed immediate and worker results match exact holdings, totals, contexts, market receipts and unit pricing work', financial(report) == financial(parent))
    recent = risk.call('portfolio/risk/jobs')
    check('mixed queued receipts remain discoverable without embedding financial results',
          any(item['id'] == job['id'] and item['requestId'] == operation['requestId'] for item in recent)
          and all(item.get('result') is None for item in recent))
    stopped = risk.call('portfolio/risk/jobs/' + job['id'] + '/cancel', {})
    check('completed mixed risk remains readable after a later cancellation request', stopped['state'] == 'completed' and stopped['result'] == report)
    trader = Client(); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    trader.call('portfolio/risk/jobs/' + job['id'], expected=403)
    trader.call('portfolio/risk/jobs', expected=403)
    check('mixed risk receipts and history require the same current scenario authority')
    risk.call('market/current', {})
    invalid = deepcopy(request)
    invalid['convention'] = 'shared_input_repricing_v1'
    failure = risk.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': invalid}, expected=422)
    check('retired futures-only report conventions cannot enter the portfolio queue', failure['code'] == 'risk_convention')
    check('job history exposes only the current portfolio operation contract',
          all(item['convention'] == catalog['convention'] for item in risk.call('portfolio/risk/jobs')))


def run_supplemental_scope(Client, check, query, manifest, run_worker):
    """One held supplemental partition must not require an unheld primary graph."""
    from preview_fixture import trade_booking

    book_id = str(uuid4())
    catalog_id = manifest['catalogId']
    query(f"""BEGIN;
        SELECT 1 FROM security.epoch FOR UPDATE;
        WITH publication AS (
          INSERT INTO trading.publications
          SELECT '{catalog_id}',coalesce(max(publication_order),0)+1,pg_current_xact_id()
          FROM trading.publications WHERE catalog_id='{catalog_id}'
          RETURNING publication_order
        )
        INSERT INTO trading.books
          SELECT '{catalog_id}','{book_id}','d0000000-0000-0000-0000-000000000001',
            'Supplemental scope',publication_order,'2020-01-01T00:00:00Z' FROM publication;
        COMMIT;""")
    client = Client(); client.bootstrap(); client.call('profile', {'profile': 'trader'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    swap_context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2',
                    'knowledgeMode': 'current'}
    swap_catalog = client.call('swaps/catalog', swap_context)
    index = next(i for i in swap_catalog['indices'] if i['curve'] == 'daily_gas')
    assert index['definitionRevisionId'] != manifest['revisionId']
    assert any(b['id'] == book_id for b in swap_catalog['books'])
    payment = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 0,
               'dayOfMonth': None, 'dayBasis': 'calendar_days', 'shortMonth': None,
               'adjustment': 'unadjusted', 'calendar': None, 'implementation': 'monthly_payment_date_rules_v1'}
    delivery = {'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01'}
    terms = {**swap_context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': book_id,
             'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'],
             'quantity': '100', 'fixedCoefficient': '-1', 'discounting': False,
             'fixed': {'price': '3', 'currency': index['currencyCode'], **delivery, 'paymentSchedule': payment},
             'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'],
                          **delivery, 'paymentSchedule': payment, 'resetFrequency': 'daily', 'averagingMethod': 'arithmetic'}}
    booked = client.call('swaps/book', terms)
    client.call('profile', {'profile': 'risk'})
    context = {**swap_context, 'convention': 'trading_workspace_v1'}
    holdings = client.call('trading/blotter', {'context': context, 'view': {**VIEW, 'bookIds': [book_id]}})
    assert len(holdings['holdings']) == 1 and holdings['holdings'][0]['tradeId'] == booked['tradeId']
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': holdings['context']['contextId']}
    catalog = client.call('portfolio/risk/catalog', {'context': retained, 'bookIds': [book_id], 'portfolioIds': []})
    factor = next(f for f in catalog['factors'] if 'daily_gas' in f['curves'] and f['deliveryFrom'] == '2021-01-01')
    request = {'context': retained, 'bookIds': [book_id], 'portfolioIds': [], 'detail': 'holdings', 'stress': [],
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
               'pricingPolicy': catalog['pricingPolicy'],
               'bumps': [{'factor': factor['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}]}
    report = client.call('portfolio/risk', request)
    check('supplemental-only book retains its held graph without inventing a primary valuation partition',
          report['snapshotId'] == frame['id'] and report['context'] == holdings['context']
          and report['work']['positionCount'] == 1
          and {m['definitions']['revisionId'] for m in report['inputs']['markets']} == {index['definitionRevisionId']}
          and {m['revisionId'] for m in report['inputs']['connectedMarkets']} == {index['definitionRevisionId']})
    position = report['factors'][0]['positions'][0]
    values = position['values']
    check('supplemental-only daily risk uses normal PV and an independent quantity/31 sensitivity',
          position['tradeId'] == booked['tradeId'] and position['measure'] == 'swap_present_value'
          and Fraction(values['base']) == Fraction(holdings['holdings'][0]['presentValue'])
          and Fraction(values['delta']) == Fraction(100, 31) and Fraction(values['gamma']) == 0
          and Fraction(values['upPnl']) == Fraction(10, 31) and Fraction(values['downPnl']) == -Fraction(10, 31))
    operation = {'requestId': str(uuid4()), 'request': request}
    job = client.call('portfolio/risk/jobs', operation)
    assert client.call('portfolio/risk/jobs', operation)['id'] == job['id']
    run_worker()
    completed = client.call('portfolio/risk/jobs/' + job['id'])
    check('supplemental-only immediate and queued results preserve the same complete financial manifest',
          completed['state'] == 'completed' and financial(completed['result']) == financial(report))
