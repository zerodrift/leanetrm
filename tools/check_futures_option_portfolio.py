"""Independent oracles for futures options in the Blotter and portfolio risk of the owned exchange example: one book holds
a CL future, a long ON call, a short LO put and the deep in-the-money ON series that settles at intrinsic, each option
with its premium paid up front. Expected values come from the catalog's settlement prices, the public ON/LO multipliers
and the fixture's synthetic USD zero rate under simple ACT/365F compounding, never from the pricer's own output."""
import os
import platform
import time
from datetime import date
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient

OPTION = 'futures_option_premium_v1'
TRADING = 'trading_workspace_v1'
FALLBACK_SERIES, FALLBACK_MODE = 'nymex_on_20261201_c2.00', 'option_settlement_at_intrinsic_v1'
# The fixture's synthetic USD zero rate at the premium payment date, simple compounding on ACT/365F (exchange_fixture.py).
PAYMENT_DATE, PAYMENT_RATE = '2026-10-19', Fraction('0.04')
TIMEOUT = 300


def run_checks(url, query, manifest, *, run_worker):
    base = PreviewClient(url); base.bootstrap()
    c = PreviewClient(url, 'exchange'); c.opener = base.opener; c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frames = c.call('workspace')['frames']
    # Booked and exercised at the first close, valued at the last, and left without one settlement at a later close of the first day.
    first, frame = (next(f for f in frames if f['at'].startswith(day)) for day in ('2026-10-13T16', '2026-10-16'))
    missing_frame = next(f for f in frames if f['at'].replace('+00:00', 'Z') == manifest['missingSettlement']['at'])
    booking = {'snapshotId': first['id'], 'knowledgeMode': 'current'}
    current = {'snapshotId': frame['id'], 'knowledgeMode': 'current'}

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual}'
        print('PASS ' + name, flush=True)

    catalog = c.call('options/catalog', {**current, 'convention': OPTION})
    options = {o['series']: o for o in catalog['options']}
    navigation = c.call('workspace/navigation')
    check('the curve directory lists no all-option settlement curve', not any(curve['code'] in ('nymex_on', 'nymex_lo') for curve in navigation['curves']))
    mixed = f"('{manifest['catalogId']}','{manifest['revisionId']}','nymex_ng','{catalog['options'][0]['contractId']}',99)"
    query(f"ALTER TABLE market_data.curve_members DISABLE TRIGGER USER;INSERT INTO market_data.curve_members VALUES{mixed};ALTER TABLE market_data.curve_members ENABLE TRIGGER USER;")
    try:
        refused = c.call('workspace/navigation', expected=422)
    finally:
        query("ALTER TABLE market_data.curve_members DISABLE TRIGGER USER;"
              f"DELETE FROM market_data.curve_members WHERE (catalog_id,revision_id,curve_code,contract_id,ordinal)={mixed};ALTER TABLE market_data.curve_members ENABLE TRIGGER USER;")
    check('a curve mixing an option contract with futures fails the directory read instead of disappearing from it', refused['code'] == 'market_navigation', refused)
    check('the option catalog states each series\' exchange and commodity from reference data',
          all(o['exchange'] == {'code': 'nymex', 'name': 'NYMEX'} and o['commodity']['code'] == ('test_gas' if o['product'] == 'nymex_on' else 'test_crude')
              and o['commodity']['name'] for o in options.values()), catalog['options'][0])
    # The long ON call is the series the later close leaves without a settlement; no earlier check books it.
    on = options[manifest['missingSettlement']['series']]
    lo = next(o for o in catalog['options'] if o['product'] == 'nymex_lo' and o['right'] == 'put' and o['underlyingSeries'] == 'nymex_cl_20270101')
    deep = options[FALLBACK_SERIES]
    book = catalog['books'][0]['id']
    parties = {'legalEntityId': catalog['legalEntities'][0]['id'], 'legalEntityRevision': catalog['legalEntities'][0]['revision'],
               'counterpartyId': catalog['counterparties'][0]['id'], 'counterpartyRevision': catalog['counterparties'][0]['revision']}

    def book_option(option, lots, premium):
        return c.call('options/book', {'operationId': str(uuid4()), **booking, 'convention': OPTION, 'bookId': book, 'series': option['series'],
                                       'lots': lots, 'premium': premium, 'premiumPaymentDate': PAYMENT_DATE,
                                       'premiumStyle': 'premium_paid_up_front', **parties})
    futures_context = {**booking, 'convention': 'contract_lot_execution_pnl_v2'}
    crude = next(k for k in c.call('futures/catalog', futures_context)['contracts'] if k['curve'] == 'nymex_cl' and k['deliveryFrom'] == '2026-12-01')
    future = c.call('futures/book', {'operationId': str(uuid4()), **futures_context, 'bookId': book, 'curve': 'nymex_cl',
                                     'contractId': crude['id'], 'lots': '1', 'executionPrice': crude['mark'], **parties})
    held = {'on': (book_option(on, '3', '0.125'), on, 3), 'lo': (book_option(lo, '-2', '1.25'), lo, -2), 'deep': (book_option(deep, '1', '1.000'), deep, 1)}
    exercised = c.call('options/exercise', {'operationId': str(uuid4()), **booking, 'convention': OPTION, 'tradeId': held['on'][0]['tradeId'], 'lots': '1'})
    # One lot of the short LO put is assigned at the last close: earlier closes still hold it open, without the future it booked.
    assigned = c.call('options/assignment', {'operationId': str(uuid4()), **current, 'convention': OPTION, 'tradeId': held['lo'][0]['tradeId'], 'lots': '1'})
    open_lots = {'on': 2, 'lo': -1, 'deep': 1}

    def blotter(context):
        view = dict(mode='trades', bookIds=[book], portfolioIds=[], groupBy=[], measures=[], pivotConvention=None)
        return c.call('trading/blotter', dict(context={**context, 'convention': TRADING}, view=view), timeout=TIMEOUT)
    started = time.monotonic()
    read = blotter(current)
    blotter_seconds = time.monotonic() - started
    rows = {(r['tradeId'], r['bindingId']): r for r in read['holdings']}

    def option_row(key):
        booked = held[key][0]
        return rows[(booked['tradeId'], booked['optionInstrumentId'])]

    def premium_row(key):
        booked = held[key][0]
        return rows[(booked['tradeId'], booked['premiumInstrumentId'])]
    for key, (booked, option, lots) in held.items():
        row = option_row(key)
        multiplier, increment = Fraction(option['multiplier']), Fraction(option['premiumTick'])
        expected = Fraction(option['settlementPrice']) * multiplier * open_lots[key]
        check(f'the {key} option values at settlement × multiplier × its {open_lots[key]} open lots, within half an increment per lot',
              row['unavailableReason'] is None and abs(Fraction(row['presentValue']) - expected) <= multiplier * increment / 2 * abs(open_lots[key]),
              (row['presentValue'], expected))
        check(f'the {key} option row shows its series terms, booked lots, settlement and lifecycle from the backend',
              (row['instrumentType'], row['right'], row['strike'], row['expiryAt'], row['underlyingSeries'], row['lots'], row['settlement']) ==
              ('futures_option', option['right'], option['strike'], option['expiryAt'], option['underlyingSeries'], str(lots), option['settlementPrice']), row)
    check('solved options show their calibrated volatility; the deep in-the-money series is in the declared intrinsic mode',
          [option_row(k)['calibrationMode'] for k in ('on', 'lo', 'deep')] == ['solved', 'solved', FALLBACK_MODE]
          and option_row('on')['impliedVolatility'] and option_row('lo')['impliedVolatility'] and option_row('deep')['impliedVolatility'] is None,
          [(option_row(k)['calibrationMode'], option_row(k)['impliedVolatility']) for k in held])
    factor = 1 / (1 + PAYMENT_RATE * (date.fromisoformat(PAYMENT_DATE) - date.fromisoformat(frame['at'][:10])).days / 365)
    for key, (booked, option, lots) in held.items():
        premium = premium_row(key)
        amount = Fraction(booked_premium(query, manifest, booked['premiumInstrumentId']))
        expected = (-1 if lots > 0 else 1) * amount * factor
        check(f'the {key} premium is its paid-up-front amount discounted from {PAYMENT_DATE} on the selected USD default curve',
              premium['unavailableReason'] is None and abs(Fraction(premium['presentValue']) - expected) <= abs(amount) * Fraction(1, 10**15),
              (premium['presentValue'], float(expected)))
    linked = option_row('on')
    check('exercised and assigned lots leave the option and link to the futures they booked, which the Blotter lists with their lots',
          (linked['openLots'], linked['lifecycleState'], linked['linkedFuturesTradeIds']) == ('2', 'partially_closed', [exercised['futuresTradeId']])
          and any(r['tradeId'] == exercised['futuresTradeId'] and r['instrumentType'] == 'future' and r['lots'] == '1' for r in read['holdings'])
          and (option_row('lo')['openLots'], option_row('lo')['linkedFuturesTradeIds']) == ('-1', [assigned['futuresTradeId']])
          and any(r['tradeId'] == assigned['futuresTradeId'] and r['lots'] == '1' for r in read['holdings']),
          (linked['openLots'], linked['lifecycleState'], linked['linkedFuturesTradeIds']))
    positions = c.call('trading/positions', dict(context={**current, 'convention': TRADING}, bookIds=[book], portfolioIds=[], offset=0, limit=64))
    check('the position facts list each option and premium binding of the book, which no longer refuses option trades',
          {(held[k][0]['tradeId'], held[k][0][f]) for k in held for f in ('optionInstrumentId', 'premiumInstrumentId')} <=
          {(p['tradeId'], p['bindingId']) for p in positions['positions'] if p['instrumentType'] == 'futures_option'}, positions['total'])

    # Portfolio risk under each held option's underlying futures factor.
    retained = {**current, 'convention': TRADING, 'knowledgeMode': 'retained', 'contextId': read['context']['contextId']}
    c.call('profile', {'profile': 'risk'})
    risk_catalog = c.call('portfolio/risk/catalog', dict(context=retained, bookIds=[book], portfolioIds=[]), timeout=TIMEOUT)

    def underlying(option):
        series = option['underlyingSeries']
        curve, month = series.rsplit('_', 1)
        return next(f for f in risk_catalog['factors'] if f['curves'] == [curve] and f['deliveryFrom'] == f'{month[:4]}-{month[4:6]}-{month[6:]}')
    factors = {key: underlying(option) for key, (_, option, _) in held.items()}
    request = dict(context=retained, bookIds=[book], portfolioIds=[],
                   bumps=[dict(factor=f['factor'], size='0.01' if f['curves'] == ['nymex_cl'] else '0.001', convention=risk_catalog['bumpConvention'])
                          for f in {f['id']: f for f in factors.values()}.values()],
                   convention=risk_catalog['convention'], numericalPolicy=risk_catalog['numericalPolicy'], pricingPolicy=risk_catalog['pricingPolicy'], stress=[])
    started = time.monotonic()
    report = c.call('portfolio/risk', request, timeout=TIMEOUT)
    report_seconds = time.monotonic() - started

    def position(key, component='optionInstrumentId'):
        booked = held[key][0]
        factor_id = factors[key]['id']
        result = next(r for r in report['factors'] if r['factor']['id'] == factor_id)
        return next(p for p in result['positions'] if p['tradeId'] == booked['tradeId'] and p['bindingId'] == booked[component])
    check('a long ON call has positive delta to its underlying NG futures factor', Fraction(position('on')['values']['delta']) > 0, position('on')['values'])
    check('a short LO put has positive delta to its underlying CL futures factor', Fraction(position('lo')['values']['delta']) > 0, position('lo')['values'])
    deep_risk = position('deep')
    check('the intrinsic-mode position is exercised in scenarios: delta is one underlying contract per lot and gamma is zero',
          deep_risk['calibrationMode'] == FALLBACK_MODE and Fraction(deep_risk['values']['delta']) == Fraction(deep['multiplier'])
          and Fraction(deep_risk['values']['gamma']) == 0, deep_risk)
    check('premiums do not move with futures shocks', all(Fraction(position(k, 'premiumInstrumentId')['values']['delta']) == 0 for k in held))
    check('the report counts the positions in the intrinsic mode and records each series\' calibration under the held-volatility policy',
          report['fallbackPositions'] >= 1 and report['scenarioPolicy'] == 'option_implied_volatility_held_v1' and
          {r['series']['code']: r['mode'] for r in report['optionCalibrations']}.get(FALLBACK_SERIES) == FALLBACK_MODE and
          {r['series']['code'] for r in report['optionCalibrations']} >= {on['series'], lo['series'], FALLBACK_SERIES}, report.get('optionCalibrations'))

    job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request), timeout=TIMEOUT)
    started = time.monotonic()
    worker_peak = run_worker(measure=True)
    worker_seconds = time.monotonic() - started
    completed = c.call('portfolio/risk/jobs/' + job['id'])
    check('a queued risk job replays to exactly the immediate report, including its calibration receipts',
          completed['state'] == 'completed' and all(completed['result'][k] == report[k] for k in
                                                    ('factors', 'excludedPositions', 'optionCalibrations', 'scenarioPolicy', 'fallbackPositions', 'optionWork')),
          {k: v for k, v in completed.items() if k != 'result'})
    work = report['optionWork']
    print('WORKLOAD futures option risk: '
          f"{work['positions']} option positions, {work['uniqueSeries']} unique series ({work['solvedSeries']} solved, {work['fallbackSeries']} intrinsic), "
          f"{len(request['bumps'])} bumps ({1 + 2 * len(request['bumps'])} scenarios), {work['calibrationTreePrices']} calibration tree prices, "
          f"{report['work']['unitPricerCalls']} unit pricer calls across every held instrument; immediate report {report_seconds:.2f} s, Blotter {blotter_seconds:.2f} s, "
          f"queued worker {worker_seconds:.2f} s with a sampled peak resident memory of {worker_peak / 1024:.0f} MiB; "
          f"host {platform.platform()} {platform.machine()} {os.cpu_count()} CPUs", flush=True)

    # The later close of the first day pins no settlement of the long ON call's series: only that option is left out.
    c.call('profile', {'profile': 'trader'})
    later = blotter({'snapshotId': missing_frame['id'], 'knowledgeMode': 'current'})
    missing = next(r for r in later['holdings'] if r['tradeId'] == held['on'][0]['tradeId'] and r['bindingId'] == held['on'][0]['optionInstrumentId'])
    check('the Blotter shows the option without a settlement as unvalued with option_settlement_missing, never zero',
          missing['unavailableReason'] == 'option_settlement_missing' and missing['presentValue'] is None, missing)
    earlier = next(r for r in later['holdings'] if r['tradeId'] == held['lo'][0]['tradeId'] and r['bindingId'] == held['lo'][0]['optionInstrumentId'])
    check('a close before the assignment holds all of the short put\'s lots open and lists neither the link nor the future it booked',
          (earlier['openLots'], earlier['lifecycleState'], earlier['linkedFuturesTradeIds']) == ('-2', 'open', []) and earlier['unavailableReason'] is None
          and not any(r['tradeId'] == assigned['futuresTradeId'] for r in later['holdings']), earlier)
    c.call('profile', {'profile': 'risk'})
    later_context = {**retained, 'snapshotId': missing_frame['id'], 'contextId': later['context']['contextId']}
    excluded = c.call('portfolio/risk/catalog', dict(context=later_context, bookIds=[book], portfolioIds=[]), timeout=TIMEOUT)['excludedPositions']
    mine = {(e['tradeId'], e['bindingId'], e['reason']) for e in excluded if e['tradeId'] in {h[0]['tradeId'] for h in held.values()}}
    check('portfolio risk leaves out only that option, with option_settlement_missing, and still values its premium and the rest of the book',
          mine == {(held['on'][0]['tradeId'], held['on'][0]['optionInstrumentId'], 'option_settlement_missing')}, excluded)
    partial = c.call('portfolio/risk', {**request, 'context': later_context}, timeout=TIMEOUT)
    check('the run with the exclusion lists it and values the long ON call\'s premium',
          any(e['tradeId'] == held['on'][0]['tradeId'] for e in partial['excludedPositions']) and
          any(p['tradeId'] == held['on'][0]['tradeId'] and p['bindingId'] == held['on'][0]['premiumInstrumentId']
              for f in partial['factors'] for p in f['positions']))
    c.call('profile', {'profile': 'trader'})
    defaults = query(f"SELECT coalesce(json_agg(d),'[]')::text FROM trading.currency_discount_defaults d WHERE catalog_id='{manifest['catalogId']}';")
    query(f"ALTER TABLE trading.currency_discount_defaults DISABLE TRIGGER USER;DELETE FROM trading.currency_discount_defaults WHERE catalog_id='{manifest['catalogId']}';"
          "ALTER TABLE trading.currency_discount_defaults ENABLE TRIGGER USER;")
    try:
        undiscounted = c.call('trading/blotter', dict(context={**current, 'convention': TRADING}, view=dict(mode='trades', bookIds=[book], portfolioIds=[],
                              groupBy=[], measures=[], pivotConvention=None)), expected=422, timeout=TIMEOUT)
    finally:
        query("ALTER TABLE trading.currency_discount_defaults DISABLE TRIGGER USER;INSERT INTO trading.currency_discount_defaults "
              "SELECT * FROM json_populate_recordset(NULL::trading.currency_discount_defaults,'" + defaults.replace("'", "''") + "'::json);"
              "ALTER TABLE trading.currency_discount_defaults ENABLE TRIGGER USER;")
    check('without a selected USD discount curve the options and premiums are a structured error, never undiscounted',
          undiscounted['code'] == 'discount_curve_missing', undiscounted)
    return future


def booked_premium(query, manifest, instrument):
    return query(f"SELECT trim_scale(amount) FROM trading.cash_payment_instruments WHERE catalog_id='{manifest['catalogId']}' AND instrument_id='{instrument}';")

