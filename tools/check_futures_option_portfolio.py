"""Independent oracles for futures options in the Blotter and portfolio risk of the owned exchange example: one book holds
a CL future, a long ON call, a short LO put and the deep in-the-money ON series that settles at intrinsic, each option
with its premium paid up front. Expected values come from the catalog's settlement prices, the public ON/LO multipliers
and the fixture's synthetic USD zero rate under simple ACT/365F compounding, never from the pricer's own output. The
reference for an option's delta and gamma is an independent binary64 tree with early exercise at the futures price,
volatility and discount its calibration receipt records."""
import os
import platform
import time
from datetime import date, datetime, timedelta
from decimal import ROUND_CEILING, ROUND_HALF_EVEN, Decimal, localcontext
from fractions import Fraction
from math import exp, sqrt
from uuid import uuid4
from preview_client import PreviewClient
from check_futures_option_lifecycle import side_lots

OPTION = 'futures_option_premium_v1'
TRADING = 'trading_workspace_v1'
FALLBACK_SERIES, FALLBACK_MODE = 'nymex_on_20261201_c2.00', 'option_settlement_at_intrinsic_v1'
REBUILT, CENTRED = 'american_futures_option_crr_v1', 'american_futures_option_leisen_reimer_v1'
# The fixture's synthetic USD zero rate at the premium payment date, simple compounding on ACT/365F (exchange_fixture.py).
PAYMENT_DATE, PAYMENT_RATE = '2026-10-19', Fraction('0.04')
# A payment date between the first and last closes, before the fixture's first rate pillar: the curve's t_0 anchor discounts it (D359).
SETTLED_DATE = '2026-10-14'
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
    described = {u['series']: u for u in catalog['underlyings']}
    check('the option catalog describes each underlying future its series exercise into once, with its delivery period and product name',
          len(described) == len(catalog['underlyings']) == len({o['underlyingSeries'] for o in options.values()})
          and all(described[o['underlyingSeries']]['deliveryFrom'] < described[o['underlyingSeries']]['deliveryTo'] for o in options.values())
          and described['nymex_ng_20261201'] == {'definitionRevisionId': manifest['revisionId'], 'series': 'nymex_ng_20261201', 'name': 'NYMEX Henry Hub Natural Gas · NG', 'deliveryFrom': '2026-12-01', 'deliveryTo': '2027-01-01'}, catalog['underlyings'])
    # The long ON call is the series the later close leaves without a settlement; no earlier check books it.
    on = options[manifest['missingSettlement']['series']]
    lo = next(o for o in catalog['options'] if o['product'] == 'nymex_lo' and o['right'] == 'put' and o['underlyingSeries'] == 'nymex_cl_20270101')
    deep = options[FALLBACK_SERIES]
    book = catalog['books'][0]['id']
    from lifecycle_option_client import LifecycleOptionClient
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings'])p WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadTrades','ReadGasOwnedHoldings'])p WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    action_clients={product:LifecycleOptionClient(c,query,manifest,issuer='https://exchange-preview.invalid',context={**booking,'convention':OPTION},as_of=first['at'],business_date='2026-10-13',
        from_at='2026-10-01T00:00:00Z',until_at='2030-01-01T00:00:00Z',mark_curve=curve,carrying='currency_curve_v1')
        for product,curve in [('nymex_on','nymex_ng'),('nymex_lo','nymex_cl')]}
    parties = {'legalEntityId': catalog['legalEntities'][0]['id'], 'legalEntityRevision': catalog['legalEntities'][0]['revision'],
               'counterpartyId': catalog['counterparties'][0]['id'], 'counterpartyRevision': catalog['counterparties'][0]['revision']}

    def book_option(option, lots, premium, into=book, paid=PAYMENT_DATE):
        return action_clients[option['product']].call('options/book', {'operationId': str(uuid4()), **booking, 'convention': OPTION, 'bookId': into, 'series': option['series'], 'definitionRevisionId': option['definitionRevisionId'],
                                       **side_lots(lots), 'premium': premium, 'premiumPaymentDate': paid,
                                       'premiumStyle': 'premium_paid_up_front', **parties})
    futures_context = {**booking, 'convention': 'contract_lot_execution_pnl_v2'}
    crude = next(k for k in c.call('futures/catalog', futures_context)['contracts'] if k['curve'] == 'nymex_cl' and k['deliveryFrom'] == '2026-12-01')
    future = c.call('futures/book', {'operationId': str(uuid4()), **futures_context, 'bookId': book, 'curve': 'nymex_cl',
                                     'contractId': crude['id'], 'definitionRevisionId': crude['definitionRevisionId'], 'lots': '1', 'executionPrice': crude['mark'], **parties})
    held = {'on': (book_option(on, '3', '0.125'), on, 3), 'lo': (book_option(lo, '-2', '1.25'), lo, -2), 'deep': (book_option(deep, '1', '1.000'), deep, 1)}
    # Another independent live series exercises the ordinary calibration scope.
    lapsing = next(o for o in catalog['options'] if o['product'] == 'nymex_lo' and o['right'] == 'call' and o['underlyingSeries'] == 'nymex_cl_20270201')
    book_option(lapsing, '1', '1.25')
    # In the other book, a premium paid on the day after the first close: settled at the last close.
    other_book = catalog['books'][-1]['id']
    paid = book_option(lapsing, '1', '1.25', into=other_book, paid=SETTLED_DATE)
    c.call('cash-payments/record',{'snapshotId':frame['id'],'knowledgeMode':'current','operationId':str(uuid4()),'allocationId':str(uuid4()),
           'tradeId':paid['tradeId'],'bindingId':paid['premiumInstrumentId'],'expectedRevision':0,'paidAt':SETTLED_DATE+'T00:00:00Z',
           'amount':format(-Decimal('1.25')*Decimal(lapsing['multiplier']),'f'),'policy':'explicit_paid_cash_nominal_due_v1'})
    exercised = action_clients['nymex_on'].call('options/exercise', {'operationId': str(uuid4()), **booking, 'convention': OPTION, 'tradeId': held['on'][0]['tradeId'], 'lots': '1'})
    # One lot of the short LO put is assigned at the last close: earlier closes still hold it open, without the future it booked.
    assigned = action_clients['nymex_lo'].call('options/assignment', {'operationId': str(uuid4()), **current, 'convention': OPTION, 'tradeId': held['lo'][0]['tradeId'], 'lots': '1'})
    open_lots = {'on': 2, 'lo': -1, 'deep': 1}
    exercised_trade = held['on'][0]['tradeId']
    linked = c.call('options/holding', {'context': {**booking, 'convention': OPTION}, 'tradeId': exercised_trade})['events']
    named = [e['futuresTradeReference'] for e in linked if e.get('futuresTradeId') == exercised['futuresTradeId']]
    check('the holding\'s events name the future an exercise booked by its trade reference',
          len(named) == 1 and named[0].startswith('TR-'), (exercised, linked))

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

    check('the exercised option\'s row lists the futures trade it booked by its reference',
          option_row('on')['linkedFuturesTrades'] == named, option_row('on'))

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
        check(f'the {key} option row names its product and its underlying future by name, not by code',
              bool(row['productName']) and row['productName'] != row['product'] and row['underlyingName'] == described[option['underlyingSeries']]['name'], row)
        check(f'the {key} option row shows its series terms, booked lots, settlement and lifecycle from the backend',
              (row['instrumentType'], row['right'], row['strike'], row['expiryAt'], row['underlyingSeries'], row['lots'], row['settlement']) ==
              ('futures_option', option['right'], option['strike'], option['expiryAt'], option['underlyingSeries'], str(lots), option['settlementPrice']), row)
    contrary = {'allowed': False, 'reason': 'This option product does not accept exercise or abandon instructions; expiry follows its automatic exercise rule.'}
    side = {'allowed': False, 'reason': 'Exercise and instructions apply to long holdings; assignment applies to short holdings.'}
    allowed = {'allowed': True, 'reason': None}
    check('each option row states the lifecycle actions open to its holding, each with the backend\'s reason when it is not',
          [option_row(k)['actions'] for k in ('on', 'lo')] == [{'exercise': allowed, 'assignment': side, 'instruction': contrary},
                                                               {'exercise': side, 'assignment': allowed, 'instruction': contrary}]
          and [option_row(k)['closableLots'] for k in ('on', 'lo', 'deep')] == ['2', '1', '1']
          and all(premium_row(k)['actions'] is None and premium_row(k)['closableLots'] is None for k in held),
          [(option_row(k)['actions'], option_row(k)['closableLots']) for k in held])
    check('each valued option row states its value per contract, which its open lots scale to its present value',
          all(Fraction(option_row(k)['unitPresentValue']) * open_lots[k] == Fraction(option_row(k)['presentValue']) for k in held)
          and all(premium_row(k)['unitPresentValue'] is None for k in held), [option_row(k)['unitPresentValue'] for k in held])
    check('solved options show their calibrated volatility; the deep in-the-money series is in the declared intrinsic mode',
          [option_row(k)['calibrationMode'] for k in ('on', 'lo', 'deep')] == ['solved', 'solved', FALLBACK_MODE]
          and option_row('on')['impliedVolatility'] and option_row('lo')['impliedVolatility'] and option_row('deep')['impliedVolatility'] is None,
          [(option_row(k)['calibrationMode'], option_row(k)['impliedVolatility']) for k in held])
    factor = 1 / (1 + PAYMENT_RATE * (date.fromisoformat(PAYMENT_DATE) - date.fromisoformat(frame['at'][:10])).days / 365)
    for key, (booked, option, lots) in held.items():
        premium = premium_row(key)
        quantity = -Fraction(booked_premium(query, manifest, booked['tradeId'])) * Fraction(option['multiplier']) * lots
        check(f'the {key} premium is pending cash: premium × multiplier × lots USD of the unit cash instrument, '
              f'paid for a long and received for a short, discounted from {PAYMENT_DATE} on the selected USD default curve',
              (premium['cashState'], Fraction(premium['quantity']), premium['unit'], premium['premiumPaymentDate'], premium['unavailableReason'])
              == ('pending', quantity, 'USD', PAYMENT_DATE, None)
              and abs(Fraction(premium['presentValue']) - quantity * factor) <= abs(quantity) * Fraction(1, 10**15),
              (premium['presentValue'], float(quantity * factor)))
    pivot = c.call('trading/blotter', dict(context={**current, 'convention': TRADING}, view=dict(mode='pivot', bookIds=[book], portfolioIds=[],
                   groupBy=['instrumentType'], measures=['quantity'], pivotConvention='trading_blotter_totals_v1')), timeout=TIMEOUT)['groups']
    option_groups = [g for g in pivot if g['instrumentType'] == 'futures_option']
    check('option lots and premium cash total by their own units, so no futures-option quantity is blank',
          option_groups and all(g['quantity'] is not None for g in option_groups) and any(g['unit'] == 'USD' for g in option_groups), option_groups)
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

    def tick(factor):
        return Decimal('0.01') if factor['curves'] == ['nymex_cl'] else Decimal('0.001')

    def request_with(sizes, policy=REBUILT):
        pricing = {**risk_catalog['pricingPolicy'], 'futuresOptions': {**risk_catalog['pricingPolicy']['futuresOptions'], 'numericalPolicy': policy}}
        return dict(context=retained, bookIds=[book], portfolioIds=[],
                    bumps=[dict(factor=f['factor'], size=plain(sizes.get(f['id'], tick(f))), convention=risk_catalog['bumpConvention'])
                           for f in {f['id']: f for f in factors.values()}.values()],
                    convention=risk_catalog['convention'], numericalPolicy=risk_catalog['numericalPolicy'], pricingPolicy=pricing, stress=[])
    check('the risk catalog states the tree rebuilt around the futures price as its option numerical policy',
          risk_catalog['pricingPolicy']['futuresOptions']['numericalPolicy'] == REBUILT, risk_catalog['pricingPolicy'])
    # A solved option's value bends once per price step of its 400-step lattice, so one up and one down move state its delta and
    # gamma only across two steps (D395). The two solved holdings' factors are shocked by that; the intrinsic one's by a tick.
    spanning = {factors[k]['id']: two_steps(rebuilt_price_step(Decimal(factors[k]['value']), Decimal(option_row(k)['impliedVolatility']), years(frame, held[k][1])),
                                            tick(factors[k])) for k in ('on', 'lo')}
    def too_small(target, size, smallest, declaring=None):
        """The refusal's sentence for one shock: the target, its size and the smallest shock that spans two price steps."""
        return f"{target}: a shock of {plain(size)} is smaller than that for {declaring or 'it'}; shock by at least {plain(smallest)}."
    refused = c.call('portfolio/risk', request_with({}), expected=422, timeout=TIMEOUT)
    check('a one-tick shock of a factor that prices a solved option is refused, naming each such factor and the smallest shock that spans two of its price steps',
          refused['code'] == 'risk_bump' and all(too_small(factors[k]['label'], tick(factors[k]), spanning[factors[k]['id']]) in refused['message'] for k in ('on', 'lo'))
          and factors['deep']['label'] + ':' not in refused['message'], refused)
    under = c.call('portfolio/risk', request_with({**spanning, factors['on']['id']: spanning[factors['on']['id']] - tick(factors['on'])}), expected=422, timeout=TIMEOUT)
    check('a shock one tick under two price steps is refused for that factor alone',
          under['code'] == 'risk_bump' and factors['on']['label'] + ':' in under['message'] and factors['lo']['label'] + ':' not in under['message'], under)
    # A whole-curve shock moves every contract on the curve, the long call's underlying among them.
    gas_curve = next(t for t in risk_catalog['scenarioTargets'] if t['kind'] == 'parallel' and t['coordinate'] == 'market_quote'
                     and t['factor']['target']['curve']['code'] == 'nymex_ng')
    whole_curve = c.call('portfolio/risk', {**request_with({}), 'bumps': [dict(factor=gas_curve['factor'], size='0.001', convention=risk_catalog['bumpConvention'])]},
                         expected=422, timeout=TIMEOUT)
    check('a whole-curve shock under two price steps of an option priced on that curve is refused, naming the curve and the contract whose option declares the step',
          whole_curve['code'] == 'risk_bump' and too_small(gas_curve['label'], Decimal('0.001'), spanning[factors['on']['id']], factors['on']['label']) in whole_curve['message'], whole_curve)
    # A nested stress moves the price the step was declared at. The rebuilt tree's step is a fixed share of the futures price, so
    # two steps are wider at a price lifted by 1.00 and the shock that was enough is refused.
    lifted = [dict(factor=factors['on']['factor'], size='1', convention=risk_catalog['stressConvention'])]
    lifted_spanning = two_steps(rebuilt_price_step(Decimal(factors['on']['value']) + 1, Decimal(option_row('on')['impliedVolatility']), years(frame, held['on'][1])), tick(factors['on']))
    under_stress = c.call('portfolio/risk', {**request_with(spanning), 'stress': lifted}, expected=422, timeout=TIMEOUT)
    check('under a nested stress the shock must span two price steps at the stressed price',
          under_stress['code'] == 'risk_bump' and lifted_spanning > spanning[factors['on']['id']]
          and too_small(factors['on']['label'], spanning[factors['on']['id']], lifted_spanning) in under_stress['message']
          and factors['lo']['label'] + ':' not in under_stress['message'], under_stress)
    request = request_with(spanning)
    started = time.monotonic()
    report = c.call('portfolio/risk', request, timeout=TIMEOUT)
    report_seconds = time.monotonic() - started
    from check_trade_risk import selected_scope
    selected_scope(c, request, held['on'][0]['tradeId'], report, run_worker=run_worker,
                   bindings=[held['on'][0]['optionInstrumentId'], held['on'][0]['premiumInstrumentId']], timeout=TIMEOUT)

    def position(key, component='optionInstrumentId', run=None):
        booked = held[key][0]
        factor_id = factors[key]['id']
        result = next(r for r in (run or report)['factors'] if r['factor']['id'] == factor_id)
        return next(p for p in result['positions'] if p['tradeId'] == booked['tradeId'] and p['bindingId'] == booked[component])
    check('a long ON call has positive delta to its underlying NG futures factor', Fraction(position('on')['values']['delta']) > 0, position('on')['values'])
    check('a short LO put has positive delta to its underlying CL futures factor', Fraction(position('lo')['values']['delta']) > 0, position('lo')['values'])

    def greeks_off(run, key, lift=0):
        """The option's delta, in contracts, and gamma, as a share, from the independent tree at the run's own calibration receipt
        and its base futures price, lifted by any nested stress."""
        option, values = held[key][1], position(key, run=run)['values']
        receipt = next(r for r in run['optionCalibrations'] if r['series']['code'] == option['series'])
        delta, gamma = american_greeks(option['right'], float(receipt['baseFutures']) + lift, float(option['strike']), float(receipt['volatility']),
                                       float(years(frame, option)), float(receipt['discountToExpiry']))
        contracts = open_lots[key] * float(option['multiplier'])
        return float(Fraction(values['delta'])) / contracts - delta, float(Fraction(values['gamma'])) / (contracts * gamma) - 1
    for key in ('on', 'lo'):
        measured = greeks_off(report, key)
        check(f'at two price steps the {key} option\'s delta is within 0.3 points of an independent tree at its held volatility, and its gamma within 1.5%',
              abs(measured[0]) <= 0.003 and abs(measured[1]) <= 0.015, measured)
    stressed = c.call('portfolio/risk', {**request_with({**spanning, factors['on']['id']: lifted_spanning}), 'stress': lifted}, timeout=TIMEOUT)
    measured = greeks_off(stressed, 'on', lift=1)
    check('at two price steps of the stressed price the on option\'s delta is within 0.3 points of the independent tree there, and its gamma within 1.5%',
          abs(measured[0]) <= 0.003 and abs(measured[1]) <= 0.015, measured)
    deep_risk = position('deep')
    check('the intrinsic-mode position is exercised in scenarios: delta is one underlying contract per lot and gamma is zero',
          deep_risk['calibrationMode'] == FALLBACK_MODE and Fraction(deep_risk['values']['delta']) == Fraction(deep['multiplier'])
          and Fraction(deep_risk['values']['gamma']) == 0, deep_risk)
    check('pending premiums hold their discounted Blotter value in risk, do not move with futures shocks and state no calibration mode, which every option position states',
          all(Fraction(position(k, 'premiumInstrumentId')['values']['delta']) == 0 and 'calibrationMode' not in position(k, 'premiumInstrumentId')
              and Fraction(position(k, 'premiumInstrumentId')['values']['base']) == Fraction(premium_row(k)['presentValue']) for k in held)
          and all(position(k)['calibrationMode'] in ('solved', FALLBACK_MODE) for k in held))
    check('the report counts the positions in the intrinsic mode and records each series\' calibration under the held-volatility policy',
          report['fallbackPositions'] >= 1 and report['scenarioPolicy'] == 'option_implied_volatility_held_v1' and
          {r['series']['code']: r['mode'] for r in report['optionCalibrations']}.get(FALLBACK_SERIES) == FALLBACK_MODE and
          {r['series']['code'] for r in report['optionCalibrations']} >= {on['series'], lo['series'], FALLBACK_SERIES}, report.get('optionCalibrations'))

    job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request), timeout=TIMEOUT)
    started = time.monotonic()
    worker_peak = run_worker(measure=True)
    worker_seconds = time.monotonic() - started
    completed = c.call('portfolio/risk/jobs/' + job['id'])
    # Named fields that legitimately differ: the read envelope the HTTP report adds (context, asOf, canBook, canRunDealRisk)
    # and the curve-graph work counters, because a retained job rebuilds its admitted graph from the job's manifest. Every
    # value, receipt, exclusion, scenario identity and option work count must be identical.
    def comparable(result):
        kept = {k: v for k, v in result.items() if k not in ('context', 'asOf', 'canBook', 'canRunDealRisk', 'work', 'connectedScenarios')}
        return {**kept, 'connectedScenarios': [{k: v for k, v in s.items() if k != 'work'} for s in result['connectedScenarios']],
                'pricerCalls': (result['work']['unitPricerCalls'], result['work']['uniqueValuations'], result['work']['scenarioCount'])}
    replayed = comparable(completed['result']) if completed['state'] == 'completed' else None
    expected = comparable(report)
    check('a queued risk job replays to exactly the immediate report, every value, receipt, exclusion and work count but the graph-rebuild counters',
          replayed == expected, {k: (replayed.get(k), expected.get(k)) for k in expected if replayed.get(k) != expected.get(k)} if replayed else completed)
    work = report['optionWork']
    print('WORKLOAD futures option risk: '
          f"{work['positions']} option positions, {work['uniqueSeries']} unique series ({work['solvedSeries']} solved, {work['fallbackSeries']} intrinsic), "
          f"{len(request['bumps'])} bumps ({1 + 2 * len(request['bumps'])} scenarios), {work['calibrationTreePrices']} calibration tree prices, "
          f"{work['scenarioTreePrices']} scenario tree prices ({report['work']['unitPricerCalls']} unit pricer calls across every held instrument); immediate report {report_seconds:.2f} s, Blotter {blotter_seconds:.2f} s, "
          f"queued worker {worker_seconds:.2f} s with a sampled peak resident memory of {worker_peak / 1024:.0f} MiB; "
          f"host {platform.platform()} {platform.machine()} {os.cpu_count()} CPUs", flush=True)

    # The same book on the strike-centred tree, which the request selects by its option numerical policy (D396). Its level
    # spacing is within 1% of the rebuilt tree's step and its volatilities within 0.5%, so a shock 2% wider than the rebuilt
    # tree's two steps is taken; the receipts it returns give the exact spacing.
    wider = {factor: two_steps(size * Decimal('1.02') / 2, tick(next(f for f in factors.values() if f['id'] == factor))) for factor, size in spanning.items()}
    started = time.monotonic()
    centred = c.call('portfolio/risk', request_with(wider, CENTRED), timeout=TIMEOUT)
    centred_seconds = time.monotonic() - started
    solved = {r['series']['code']: r for r in centred['optionCalibrations'] if r['mode'] == 'solved'}
    rebuilt_volatility = {r['series']['code']: r['volatility'] for r in report['optionCalibrations'] if r['mode'] == 'solved'}
    check('a request that names the strike-centred policy is calibrated and valued under it: every receipt and option model names it, '
          'and each series solves to a volatility within 0.5% of the rebuilt tree\'s',
          {r['numericalPolicy'] for r in centred['optionCalibrations']} == {CENTRED} and {r['numericalPolicy'] for r in report['optionCalibrations']} == {REBUILT}
          and CENTRED in {m['modelVersion'] for m in centred['modelContracts']} and REBUILT not in {m['modelVersion'] for m in centred['modelContracts']}
          and solved.keys() == rebuilt_volatility.keys() and all(abs(Decimal(solved[k]['volatility']) / Decimal(rebuilt_volatility[k]) - 1) <= Decimal('0.005') for k in solved),
          [(k, solved[k]['volatility'], rebuilt_volatility[k]) for k in solved])
    for key in ('on', 'lo'):
        option = held[key][1]
        base = Fraction(position(key, run=centred)['values']['base'])
        check(f'on the strike-centred tree the {key} option is still worth its settlement within half an increment per lot',
              abs(base - Fraction(option['settlementPrice']) * Fraction(option['multiplier']) * open_lots[key])
              <= Fraction(option['multiplier']) * Fraction(option['premiumTick']) / 2 * abs(open_lots[key]), base)
    # Under discounting the strike-centred tree declares its level spacing as its price step, so the same refusal applies to it.
    centred_spanning = {factors[k]['id']: two_steps(centred_price_step(Decimal(solved[held[k][1]['series']]['baseFutures']), Decimal(held[k][1]['strike']),
                                                                       Decimal(solved[held[k][1]['series']]['volatility']), years(frame, held[k][1])), tick(factors[k]))
                        for k in ('on', 'lo')}
    refused = c.call('portfolio/risk', request_with({}, CENTRED), expected=422, timeout=TIMEOUT)
    check('on the strike-centred tree a one-tick shock is refused too, naming the smallest shock that spans two of its level spacings',
          refused['code'] == 'risk_bump' and all(too_small(factors[k]['label'], tick(factors[k]), centred_spanning[factors[k]['id']]) in refused['message'] for k in ('on', 'lo')), refused)
    centred = c.call('portfolio/risk', request_with(centred_spanning, CENTRED), timeout=TIMEOUT)
    for key in ('on', 'lo'):
        measured = greeks_off(centred, key)
        check(f'at two of its price steps the {key} option\'s delta on the strike-centred tree is within 0.3 points of the independent tree, and its gamma within 1.5%',
              abs(measured[0]) <= 0.003 and abs(measured[1]) <= 0.015, measured)
    centred_job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request_with(centred_spanning, CENTRED)), timeout=TIMEOUT)
    run_worker()
    centred_replay = c.call('portfolio/risk/jobs/' + centred_job['id'])
    check('a queued job that names the strike-centred policy replays to the immediate report under it',
          centred_replay['state'] == 'completed' and comparable(centred_replay['result']) == comparable(centred),
          centred_replay.get('errorCode', centred_replay['state']))
    check('a job read says whether its run was valued under the pricing policy the catalog states: the first job was, the strike-centred one was not',
          (completed['underStatedPricingPolicy'], centred_replay['underStatedPricingPolicy']) == (True, False),
          (completed.get('underStatedPricingPolicy'), centred_replay.get('underStatedPricingPolicy')))
    print('WORKLOAD futures option risk on the strike-centred tree: '
          f"{centred['optionWork']['calibrationTreePrices']} calibration tree prices, {centred['optionWork']['scenarioTreePrices']} scenario tree prices; "
          f"immediate report {centred_seconds:.2f} s against {report_seconds:.2f} s on the rebuilt tree", flush=True)

    # The later close of the first day pins no settlement of the long ON call's series: only that option is left out.
    c.call('profile', {'profile': 'trader'})
    later = blotter({'snapshotId': missing_frame['id'], 'knowledgeMode': 'current'})
    missing = next(r for r in later['holdings'] if r['tradeId'] == held['on'][0]['tradeId'] and r['bindingId'] == held['on'][0]['optionInstrumentId'])
    check('the Blotter shows the option without a settlement as unvalued with option_settlement_missing, never zero',
          missing['unavailableReason'] == 'option_settlement_missing' and missing['presentValue'] is None, missing)
    earlier = next(r for r in later['holdings'] if r['tradeId'] == held['lo'][0]['tradeId'] and r['bindingId'] == held['lo'][0]['optionInstrumentId'])
    check('a close before the assignment holds all of the short put\'s lots open and lists neither the link nor the future it booked',
          (earlier['openLots'], earlier['lifecycleState'], earlier['linkedFuturesTradeIds']) == ('-2', 'open', [])
          and not any(r['tradeId'] == assigned['futuresTradeId'] for r in later['holdings']), earlier)
    check('at that close the short put\'s underlying future has no settlement: option_underlying_missing and no value',
          (earlier['unavailableReason'], earlier['presentValue']) == ('option_underlying_missing', None), earlier)
    c.call('profile', {'profile': 'risk'})
    later_context = {**retained, 'snapshotId': missing_frame['id'], 'contextId': later['context']['contextId']}
    excluded = c.call('portfolio/risk/catalog', dict(context=later_context, bookIds=[book], portfolioIds=[]), timeout=TIMEOUT)['excludedPositions']
    mine = {(e['tradeId'], e['bindingId'], e['reason']) for e in excluded if e['tradeId'] in {h[0]['tradeId'] for h in held.values()}}
    check('portfolio risk leaves out only those options, with their structured codes, and still values their premiums and the rest of the book',
          mine == {(held['on'][0]['tradeId'], held['on'][0]['optionInstrumentId'], 'option_settlement_missing'),
                   (held['lo'][0]['tradeId'], held['lo'][0]['optionInstrumentId'], 'option_underlying_missing')}, excluded)
    partial = c.call('portfolio/risk', {**request, 'context': later_context,
                                        'bumps': [b for b in request['bumps'] if b['factor'] != factors['lo']['factor']]}, timeout=TIMEOUT)
    check('the run with the exclusion lists it and values the long ON call\'s premium',
          any(e['tradeId'] == held['on'][0]['tradeId'] for e in partial['excludedPositions']) and
          any(p['tradeId'] == held['on'][0]['tradeId'] and p['bindingId'] == held['on'][0]['premiumInstrumentId']
              for f in partial['factors'] for p in f['positions']))

    # Premium cash on and after its payment date is settled: shown with its amount and date, worth nothing further, never left out.
    c.call('profile', {'profile': 'trader'})
    other = c.call('trading/blotter', dict(context={**current, 'convention': TRADING}, view=dict(mode='trades', bookIds=[other_book], portfolioIds=[],
                   groupBy=[], measures=[], pivotConvention=None)), timeout=TIMEOUT)
    settled = next(r for r in other['holdings'] if r['tradeId'] == paid['tradeId'] and r['bindingId'] == paid['premiumInstrumentId'])
    paid_cash = -Fraction(booked_premium(query, manifest, paid['tradeId'])) * Fraction(lapsing['multiplier'])
    check('premium cash paid before the close is settled: its amount and payment date, a zero value under the measure that excludes settled cash',
          (settled['cashState'], Fraction(settled['quantity']), settled['premiumPaymentDate'], Fraction(settled['presentValue']), settled['unavailableReason'],
           settled['measureLabel']) == ('paid', paid_cash, SETTLED_DATE, 0, None, 'Option value excluding settled premium cash'), settled)
    c.call('profile', {'profile': 'risk'})
    other_context = {**retained, 'contextId': other['context']['contextId']}
    other_catalog = c.call('portfolio/risk/catalog', dict(context=other_context, bookIds=[other_book], portfolioIds=[]), timeout=TIMEOUT)
    lapsing_factor = next(f for f in other_catalog['factors'] if f['curves'] == ['nymex_cl'] and f['deliveryFrom'] == '2027-02-01')
    lapsing_row = next(r for r in other['holdings'] if r['tradeId'] == paid['tradeId'] and r['bindingId'] == paid['optionInstrumentId'])
    other_report = c.call('portfolio/risk', dict(request, context=other_context, bookIds=[other_book], pricingPolicy=other_catalog['pricingPolicy'],
                          bumps=[dict(factor=lapsing_factor['factor'], convention=other_catalog['bumpConvention'], size=plain(two_steps(rebuilt_price_step(
                              Decimal(lapsing_factor['value']), Decimal(lapsing_row['impliedVolatility']), years(frame, lapsing)), Decimal('0.01'))))]), timeout=TIMEOUT)
    settled_risk = next(p for p in other_report['factors'][0]['positions'] if p['tradeId'] == paid['tradeId'] and p['bindingId'] == paid['premiumInstrumentId'])
    check('portfolio risk holds the settled premium at zero under that measure and does not list it as excluded',
          Fraction(settled_risk['values']['base']) == 0 and settled_risk['measureLabel'] == 'Option value excluding settled premium cash'
          and not any(e['tradeId'] == paid['tradeId'] for e in other_report['excludedPositions']), settled_risk)

    # At the first close that premium is still pending and its payment date lies before the curve's first pillar (Oct 19 at 4%
    # simple ACT/365F). D359 anchors the curve at t_0 with factor 1, so the day's factor is the sixth root of the pillar's.
    c.call('profile', {'profile': 'trader'})
    early = c.call('trading/blotter', dict(context={**booking, 'convention': TRADING}, view=dict(mode='trades', bookIds=[other_book], portfolioIds=[],
                   groupBy=[], measures=[], pivotConvention=None)), timeout=TIMEOUT)
    early_rows = {r['bindingId']: r for r in early['holdings'] if r['tradeId'] == paid['tradeId']}
    pending, covered_option = early_rows[paid['premiumInstrumentId']], early_rows[paid['optionInstrumentId']]
    with localcontext() as exact_context:
        exact_context.prec = 60
        anchored = ((1 / (1 + Decimal('0.04') * 6 / 365)).ln() / 6).exp().quantize(Decimal('1e-18'), ROUND_HALF_EVEN)
    check('a pending premium dated before the curve\'s first pillar is discounted from the t_0 anchor, and its option is valued',
          (pending['cashState'], pending['unavailableReason']) == ('pending', None)
          and Fraction(pending['presentValue']) == paid_cash * Fraction(anchored)
          and covered_option['unavailableReason'] is None and covered_option['presentValue'] is not None, early_rows)
    c.call('profile', {'profile': 'risk'})
    early_excluded = c.call('portfolio/risk/catalog', dict(context={**retained, 'snapshotId': first['id'], 'contextId': early['context']['contextId']}, bookIds=[other_book],
                            portfolioIds=[]), timeout=TIMEOUT)['excludedPositions']
    check('portfolio risk leaves out none of that trade',
          not any(e['tradeId'] == paid['tradeId'] for e in early_excluded), early_excluded)

    # Genuine historical unprocessed-expiry Blotter and risk coverage lives in
    # check_lifecycle_option_expiries.py, using a separately sealed synthetic
    # right and its actual expiry instant. Accepted reference rows stay immutable.
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


def years(frame, option):
    """The option's time to expiry at the frame under act_365_fixed_instant_v1: exact elapsed time over a 365-day year."""
    elapsed = (datetime.fromisoformat(option['expiryAt']) - datetime.fromisoformat(frame['at'])) // timedelta(microseconds=1)
    with localcontext() as exact_context:
        exact_context.prec = 50
        return Decimal(elapsed) / (365 * 86400 * 10**6)


def rebuilt_price_step(futures, volatility, expiry_years, steps=400):
    """The price step of the tree rebuilt around the futures price: futures × (exp(2 × volatility × √(years ÷ steps)) − 1) (futures-options.md)."""
    with localcontext() as exact_context:
        exact_context.prec = 50
        return futures * ((2 * volatility * (expiry_years / steps).sqrt()).exp() - 1)


def centred_price_step(futures, strike, volatility, expiry_years, steps=401):
    """The price step of the strike-centred tree under discounting, its level spacing: futures × (u ÷ d − 1), where u = h(d1) ÷ h(d2),
    d = (1 − h(d1)) ÷ (1 − h(d2)) and h is the Peizer-Pratt inversion (futures-options.md)."""
    with localcontext() as exact_context:
        exact_context.prec = 50

        def inversion(deviations):
            scaled = deviations / (steps + Decimal(1) / 3 + Decimal('0.1') / (steps + 1))
            half = (1 - (-scaled * scaled * (steps + Decimal(1) / 6)).exp()).sqrt() / 2
            return Decimal('0.5') + (half if deviations >= 0 else -half)
        width = volatility * expiry_years.sqrt()
        d1 = (futures / strike).ln() / width + width / 2
        tilted, probability = inversion(d1), inversion(d1 - width)
        return futures * (tilted * (1 - probability) / (probability * (1 - tilted)) - 1)


def plain(value):
    """A decimal as the backend prints one: no exponent and no trailing zeros."""
    return format(value.normalize(), 'f')


def two_steps(step, tick):
    """Two price steps in whole trading ticks."""
    with localcontext() as exact_context:
        exact_context.prec = 50
        return (2 * step / tick).to_integral_value(rounding=ROUND_CEILING) * tick


def american_greeks(right, futures, strike, volatility, expiry_years, discount, steps=1000):
    """Delta and gamma per unit of quote from an independent binary64 Cox-Ross-Rubinstein tree with early exercise. The tree starts
    two steps before the valuation instant, so the futures price and both its neighbours are nodes, and the Greeks are read from
    those three nodes rather than from repricing."""
    spread = volatility * sqrt(expiry_years / steps)
    up, step_discount = (1 - exp(-spread)) / (exp(spread) - exp(-spread)), discount ** (1 / steps)
    # The exercise value at each level: level steps + 2 + j is the futures price times exp(j × spread).
    prices = [futures * exp((level - steps - 2) * spread) for level in range(2 * steps + 5)]
    payoffs = [max(price - strike if right == 'call' else strike - price, 0.0) for price in prices]
    values = [payoffs[2 * (steps + 2 - node)] for node in range(steps + 3)]
    for step in range(steps + 1, 1, -1):
        values = [max(step_discount * (up * values[node] + (1 - up) * values[node + 1]), payoffs[step - 2 * node + steps + 2]) for node in range(step + 1)]
    above, below = prices[steps + 4], prices[steps]
    return (values[0] - values[2]) / (above - below), ((values[0] - values[1]) / (above - futures) - (values[1] - values[2]) / (futures - below)) / ((above - below) / 2)


def booked_premium(query, manifest, trade):
    return query(f"SELECT trim_scale(premium) FROM trading.futures_option_trade_revisions WHERE catalog_id='{manifest['catalogId']}' AND trade_id='{trade}';")
