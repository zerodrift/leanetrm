"""Financial swap booking, retained history and independent side PV checks."""
from preview_fixture import trade_booking
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    w = c.call('workspace'); frame = next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    catalog = c.call('swaps/catalog', context)
    assert len(catalog['indices']) >= 2
    passed = 0
    def check(name, ok):
        nonlocal passed
        assert ok, name
        passed += 1; print('PASS swaps: '+name, flush=True)
    check('available gas and power indices expose supported monthly contracts', all(i['contracts'] for i in catalog['indices']))
    index = next(i for i in catalog['indices'] if i['commodity'] == 'test_gas')
    contract = index['contracts'][0]
    def rule(days):
        return {'frequency':'monthly','rule':'days_after_period_end','days':days,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    body = {**context, 'operationId': str(uuid4()), **trade_booking(), 'bookId': catalog['books'][0]['id'], 'definitionRevisionId': index['definitionRevisionId'], 'commodity':index['commodity'], 'quantity': '100', 'fixedCoefficient': '-1', 'discounting': True,
            'fixed': {'price': '3', 'currency': index['currencyCode'], 'deliveryFrom': contract['deliveryFrom'], 'deliveryTo': contract['deliveryTo'], 'paymentSchedule':rule(18)},
            'floating': {'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'], 'deliveryFrom': contract['deliveryFrom'], 'deliveryTo': contract['deliveryTo'],
                         'firstFixingAt': '2020-12-31T12:00:00Z', 'paymentSchedule':rule(46), 'resetFrequency':'monthly','averagingMethod':'none'}}
    preview = c.call('swaps/preview', body)
    result = c.call('swaps/book', body)
    retry = c.call('swaps/book', body)
    check('booking retries return the same trade instrument and initial event', result == retry and result['revision'] == 1)
    detail = c.call('swaps/editor', {'context': context, 'tradeId': result['tradeId'], 'revision': None})
    value = detail['valuation']
    check('retained side schedules reconcile exactly to managed preview PV', value['presentValue'] == preview['valuation']['presentValue'] and len(value['sides']) == 2)
    check('independent payment dates and DFs survive booking', value['sides'][0]['payments'][0]['paymentAt'] != value['sides'][1]['payments'][0]['paymentAt'] and value['sides'][0]['payments'][0]['discountFactor'] != value['sides'][1]['payments'][0]['discountFactor'])
    check('side cash amounts are normalized before signed PV and trade scaling', value['sides'][0]['payments'][0]['cashAmount'] == '3' and Decimal(value['sides'][1]['payments'][0]['cashAmount']) > 3)
    old = {'snapshotId': frame['id'], 'convention': context['convention'], 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
    c.call('swaps/editor', {'context': old, 'tradeId': result['tradeId'], 'revision': None}, expected=422)
    changed = {**body, 'quantity': '200'}
    c.call('swaps/book', changed, expected=409)
    check('historical knowledge excludes later booking and request identity rejects changed terms', True)
    amend = {**body, 'operationId': str(uuid4()), 'tradeId': result['tradeId'], 'expectedRevision': 1, 'effectiveAt': catalog['asOf'], 'reason': 'Synthetic quantity correction', 'quantity': '200'}
    for invalid in [{'effectiveAt': '2020-12-17T16:00:00Z'}, {'reason': ''}, {'expectedRevision': 99}, {'definitionRevisionId': catalog['indices'][-1]['definitionRevisionId']}]:
        c.call('swaps/preview', {**amend, **invalid}, expected=422)
    check('amendment preview enforces retained head, effective time, reason and exact definition eligibility', True)
    amended = c.call('swaps/amend', amend)
    latest = c.call('swaps/editor', {'context': context, 'tradeId': result['tradeId'], 'revision': None})
    earlier = c.call('swaps/editor', {'context': context, 'tradeId': result['tradeId'], 'revision': 1})
    check('latest-head amendment preserves old quantity and stable trade reference', amended['revision'] == 2 and latest['state']['quantity'] == '200' and earlier['state']['quantity'] == '100' and not earlier['canAmend'] and latest['state']['tradeReference'] == earlier['state']['tradeReference'])
    c.call('swaps/preview', {**amend, 'expectedRevision': 2, 'quantity': '200.0'}, expected=422)
    replacement = next(i for i in catalog['indices'] if i['commodity'] == 'test_power' and i['definitionRevisionId'] == body['definitionRevisionId'])
    c.call('swaps/preview', {**amend, 'expectedRevision': 2, 'floating': {**amend['floating'], 'curve': replacement['curve'], 'index': replacement['index']}}, expected=422)
    check('preview rejects equivalent terms and incompatible same-currency quote changes', True)
    c.call('swaps/amend', {**amend, 'operationId': str(uuid4())}, expected=409)
    c.call('swaps/book', {**body, 'operationId': str(uuid4()), 'floating': {**body['floating'], 'currency': 'unknown'}}, expected=422)
    c.call('swaps/book', {**body, 'operationId': str(uuid4()), 'floating': {**body['floating'], 'averaging': 'unknown'}}, expected=422)
    c.call('swaps/book', {**body, 'operationId': str(uuid4()), 'discounting': None}, expected=422)
    check('stale heads unsupported conventions currencies and missing policies reject', True)
    risk = PreviewClient(url); risk.bootstrap(); risk.call('profile', {'profile': 'risk'})
    risk.call('swaps/book', {**body, 'operationId': str(uuid4())}, expected=403)
    check('read-only risk authority cannot book', True)
    for curve, strike, expected in [('daily_gas', '3', '16'), ('daily_power', '40', '160')]:
        daily = next(i for i in catalog['indices'] if i['curve'] == curve)
        daily_body = {**body, 'operationId': str(uuid4()), 'definitionRevisionId': daily['definitionRevisionId'], 'commodity':daily['commodity'], 'discounting': False,
                      'fixed': {**body['fixed'], 'price': strike},
                      'floating': {**body['floating'], 'curve': daily['curve'], 'index': daily['index'], 'resetFrequency':'daily','averagingMethod':'arithmetic'}}
        booked = c.call('swaps/book', daily_body)
        view = c.call('swaps/editor', {'context': context, 'tradeId': booked['tradeId'], 'revision': None})['valuation']
        resets = view['sides'][1]['payments'][0]['resets']
        check(curve+' retains 31 exact daily resets and one monthly payment on each independent side', len(view['sides'][0]['payments']) == len(view['sides'][1]['payments']) == 1 and len(resets) == 31 and [r['from'] for r in resets] == [f'2021-01-{day:02d}' for day in range(1, 32)])
        check(curve+' daily reset mean and trade PV match independent arithmetic', Decimal(view['presentValue']) == Decimal(expected) and all(r['weight'] == '1' and r['fixingState'] == 'Projected' and r['lineage'] for r in resets))
        c.call('swaps/preview', {**daily_body, 'floating': {**daily_body['floating'], 'resetFrequency':'monthly','averagingMethod':'none'}}, expected=422)
    futures_context = {**context, 'convention': 'fixed_total_cash_execution_pnl_v1'}
    future_catalog = c.call('futures/catalog', futures_context)
    future = future_catalog['contracts'][0]
    future_body = {**futures_context, 'operationId': amend['operationId'], **trade_booking(), 'bookId': body['bookId'], 'curve': future['curve'], 'contractId': future['id'], 'lots': '1', 'executionPrice': '2.9'}
    c.call('futures/book', future_body, expected=409)
    future_body['operationId'] = str(uuid4())
    future_trade = c.call('futures/book', future_body)
    c.call('swaps/book', {**body, 'operationId': future_body['operationId']}, expected=409)
    c.call('swaps/amend', {**amend, 'operationId': future_body['operationId'], 'expectedRevision': 2}, expected=409)
    c.call('futures/amend', {**future_body, 'operationId': body['operationId'], 'tradeId': future_trade['tradeId'], 'expectedRevision': 1, 'effectiveAt': catalog['asOf'], 'reason': 'Synthetic collision check', 'lots': '2', 'amendmentConvention': 'unprocessed_futures_booking_amendment_v1'}, expected=409)
    check('operation identities cannot cross futures and swap booking/amendment namespaces', True)
    wide = {**body, 'operationId': str(uuid4()), 'discounting': False, 'fixed': {**body['fixed'], 'price': '1001.003'}}
    wide_preview = c.call('swaps/preview', wide)
    wide_booked = c.call('swaps/book', wide)
    check('fixed strikes do not inherit observed quote bounds or trading ticks', Decimal(wide_preview['valuation']['presentValue']) < -99000 and wide_booked['tradeId'])
    shared = {**context, 'convention': 'trading_workspace_v1'}
    trades = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
    blotter = c.call('trading/blotter', {'context': shared, 'view': trades})
    swaps = [h for h in blotter['holdings'] if h['instrumentType'] == 'financial_swap']
    futures = [h for h in blotter['holdings'] if h['instrumentType'] == 'future']
    check('shared blotter keeps swap PV separate from futures execution PnL', swaps and futures and all(h['executionPnl'] is None and h['presentValue'] is not None and h['executionPrice'] is None for h in swaps) and all(h['presentValue'] is None for h in futures))
    pivot = {**trades, 'mode': 'pivot', 'groupBy': ['book'], 'measures': ['tradeCount', 'quantity', 'presentValue', 'executionPnl'], 'pivotConvention': 'trading_blotter_totals_v1'}
    groups = c.call('trading/blotter', {'context': shared, 'view': pivot})['groups']
    check('shared pivots partition product units and preserve unavailable measures', all(g['executionPnl'] is None for g in groups if g['instrumentType'] == 'financial_swap') and all(g['presentValue'] is None for g in groups if g['instrumentType'] == 'future'))
    moved = {**amend, 'operationId': str(uuid4()), 'expectedRevision': 2, 'bookId': catalog['books'][1]['id'], 'reason': 'Synthetic book correction'}
    c.call('swaps/amend', moved)
    now = c.call('trading/blotter', {'context': shared, 'view': {**trades, 'bookIds': [body['bookId']]}})
    before_move = c.call('trading/blotter', {'context': {**shared, 'knowledgeMode': 'retained', 'contextId': blotter['context']['contextId']}, 'view': trades})
    check('book filters follow latest head while retained knowledge preserves earlier book membership', not any(h['tradeId'] == result['tradeId'] for h in now['holdings']) and any(h['tradeId'] == result['tradeId'] and h['bookId'] == body['bookId'] for h in before_move['holdings']))
    print(f'{passed} swap HTTP checks passed.', flush=True)
