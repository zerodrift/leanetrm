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
    return {key: value for key, value in report.items() if key not in UI_CAPABILITIES}


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
               'bumps': [{'seriesId': factor['seriesId'], 'quote': factor['quote'], 'size': '0.1',
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
    unheld_request = {**request, 'bumps': [{**request['bumps'][0], 'seriesId': unheld['seriesId'], 'quote': unheld['quote']}]}
    untouched = risk.call('portfolio/risk', unheld_request)
    check('an available unheld factor returns explicit zero risk for every holding without extra unit valuations',
          untouched['work']['unitPricerCalls'] == untouched['work']['uniqueValuations']
          and len(untouched['factors'][0]['positions']) == len(rows)
          and all(row['values']['base'] == row['values']['up'] == row['values']['down']
                  and all(Fraction(row['values'][field]) == 0 for field in ('upPnl', 'downPnl', 'delta', 'gamma'))
                  for row in untouched['factors'][0]['positions']))
    irrelevant_stress = {'seriesId': unheld['seriesId'], 'quote': unheld['quote'], 'size': '0.2',
                         'convention': catalog['stressConvention']}
    unheld_stress = risk.call('portfolio/risk', {**request, 'stress': [irrelevant_stress]})
    check('a temporary unheld-factor stress preserves the explicit scenario while leaving held financial values unchanged',
          unheld_stress['scenario']['depth'] == 1
          and unheld_stress['factors'][0]['positions'] == result['factors'][0]['positions']
          and unheld_stress['factors'][0]['totals'] == result['factors'][0]['totals'])
    daily = risk.call('portfolio/risk', {**request, 'bumps': [{**request['bumps'][0],
                      'seriesId': daily_factor['seriesId'], 'quote': daily_factor['quote']}]})
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
    shift = {'seriesId': factor['seriesId'], 'quote': factor['quote'], 'size': '0.2', 'convention': catalog['stressConvention']}
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
          risk.call('portfolio/risk', parent_request) == parent
          and risk.call('portfolio/risk', request) == result
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
    check('mixed immediate and worker results match exact holdings, totals, contexts, market receipts and work', financial(report) == financial(parent))
    recent = risk.call('portfolio/risk/jobs')
    check('mixed queued receipts remain discoverable without embedding financial results',
          any(item['id'] == job['id'] and item['requestId'] == operation['requestId'] for item in recent)
          and all(item.get('result') is None for item in recent))
    stopped = risk.call('portfolio/risk/jobs/' + job['id'] + '/cancel', {})
    check('completed mixed risk remains readable after a later cancellation request', stopped['state'] == 'completed' and financial(stopped['result']) == financial(parent))
    trader = Client(); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    trader.call('portfolio/risk/jobs/' + job['id'], expected=403)
    trader.call('portfolio/risk/jobs', expected=403)
    check('mixed risk receipts and history require the same current scenario authority')
    risk.call('market/current', {})
    legacy_context = {**retained, 'convention': 'fixed_total_cash_execution_pnl_v1'}
    legacy_catalog = risk.call('futures/risk/catalog', {'context': legacy_context, 'bookIds': [], 'portfolioIds': []})
    legacy_request = {key: value for key, value in request.items() if key != 'pricingPolicy'}
    legacy_request.update(context=legacy_context, convention=legacy_catalog['convention'], numericalPolicy=legacy_catalog['numericalPolicy'])
    legacy = risk.call('futures/risk/jobs', {'requestId': str(uuid4()), 'request': legacy_request})
    check('generic job history excludes legacy futures report contracts',
          all(item['id'] != legacy['id'] and item['convention'] == catalog['convention']
              for item in risk.call('portfolio/risk/jobs')))
    risk.call('portfolio/risk/jobs/' + legacy['id'], expected=422)
    risk.call('portfolio/risk/jobs/' + legacy['id'] + '/cancel', {}, expected=422)
    check('a mismatched generic read or cancellation leaves a legacy job untouched',
          risk.call('futures/risk/jobs/' + legacy['id'])['state'] == 'queued')
    cancelled = risk.call('futures/risk/jobs/' + legacy['id'] + '/cancel', {})
    check('legacy jobs remain inspectable and cancellable through their original operation', cancelled['state'] == 'cancelled')
