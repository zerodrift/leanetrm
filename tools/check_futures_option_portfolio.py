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
from check_futures_option_lifecycle import side_lots

OPTION = 'futures_option_premium_v1'
TRADING = 'trading_workspace_v1'
FALLBACK_SERIES, FALLBACK_MODE = 'nymex_on_20261201_c2.00', 'option_settlement_at_intrinsic_v1'
# The fixture's synthetic USD zero rate at the premium payment date, simple compounding on ACT/365F (exchange_fixture.py).
PAYMENT_DATE, PAYMENT_RATE = '2026-10-19', Fraction('0.04')
# A payment date between the first and last closes, before the fixture's first rate pillar: such cash is only read once settled.
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
    check('the option catalog describes each underlying future its series exercise into once, with its delivery period',
          len(described) == len(catalog['underlyings']) == len({o['underlyingSeries'] for o in options.values()})
          and all(described[o['underlyingSeries']]['deliveryFrom'] < described[o['underlyingSeries']]['deliveryTo'] for o in options.values())
          and described['nymex_ng_20261201'] == {'series': 'nymex_ng_20261201', 'deliveryFrom': '2026-12-01', 'deliveryTo': '2027-01-01'}, catalog['underlyings'])
    # The long ON call is the series the later close leaves without a settlement; no earlier check books it.
    on = options[manifest['missingSettlement']['series']]
    lo = next(o for o in catalog['options'] if o['product'] == 'nymex_lo' and o['right'] == 'put' and o['underlyingSeries'] == 'nymex_cl_20270101')
    deep = options[FALLBACK_SERIES]
    book = catalog['books'][0]['id']
    parties = {'legalEntityId': catalog['legalEntities'][0]['id'], 'legalEntityRevision': catalog['legalEntities'][0]['revision'],
               'counterpartyId': catalog['counterparties'][0]['id'], 'counterpartyRevision': catalog['counterparties'][0]['revision']}

    def book_option(option, lots, premium, into=book, paid=PAYMENT_DATE):
        return c.call('options/book', {'operationId': str(uuid4()), **booking, 'convention': OPTION, 'bookId': into, 'series': option['series'],
                                       **side_lots(lots), 'premium': premium, 'premiumPaymentDate': paid,
                                       'premiumStyle': 'premium_paid_up_front', **parties})
    futures_context = {**booking, 'convention': 'contract_lot_execution_pnl_v2'}
    crude = next(k for k in c.call('futures/catalog', futures_context)['contracts'] if k['curve'] == 'nymex_cl' and k['deliveryFrom'] == '2026-12-01')
    future = c.call('futures/book', {'operationId': str(uuid4()), **futures_context, 'bookId': book, 'curve': 'nymex_cl',
                                     'contractId': crude['id'], 'lots': '1', 'executionPrice': crude['mark'], **parties})
    held = {'on': (book_option(on, '3', '0.125'), on, 3), 'lo': (book_option(lo, '-2', '1.25'), lo, -2), 'deep': (book_option(deep, '1', '1.000'), deep, 1)}
    # A series this check alone holds, later moved past its expiry without a recorded expiry.
    lapsing = next(o for o in catalog['options'] if o['product'] == 'nymex_lo' and o['right'] == 'call' and o['underlyingSeries'] == 'nymex_cl_20270201')
    unexpired = book_option(lapsing, '1', '1.25')
    # In the other book, a premium paid on the day after the first close: settled at the last close.
    other_book = catalog['books'][-1]['id']
    paid = book_option(lapsing, '1', '1.25', into=other_book, paid=SETTLED_DATE)
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
           settled['measureLabel']) == ('settled', paid_cash, SETTLED_DATE, 0, None, 'Option value excluding settled premium cash'), settled)
    c.call('profile', {'profile': 'risk'})
    other_context = {**retained, 'contextId': other['context']['contextId']}
    other_catalog = c.call('portfolio/risk/catalog', dict(context=other_context, bookIds=[other_book], portfolioIds=[]), timeout=TIMEOUT)
    lapsing_factor = next(f for f in other_catalog['factors'] if f['curves'] == ['nymex_cl'] and f['deliveryFrom'] == '2027-02-01')
    other_report = c.call('portfolio/risk', dict(request, context=other_context, bookIds=[other_book], pricingPolicy=other_catalog['pricingPolicy'],
                          bumps=[dict(factor=lapsing_factor['factor'], size='0.01', convention=other_catalog['bumpConvention'])]), timeout=TIMEOUT)
    settled_risk = next(p for p in other_report['factors'][0]['positions'] if p['tradeId'] == paid['tradeId'] and p['bindingId'] == paid['premiumInstrumentId'])
    check('portfolio risk holds the settled premium at zero under that measure and does not list it as excluded',
          Fraction(settled_risk['values']['base']) == 0 and settled_risk['measureLabel'] == 'Option value excluding settled premium cash'
          and not any(e['tradeId'] == paid['tradeId'] for e in other_report['excludedPositions']), settled_risk)

    # A held series whose expiry instant passes with no recorded expiry stays visibly unprocessed: option_expired, no value.
    R, M = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'", f"'{manifest['catalogId']}','{manifest['revisionId']}'"
    terms = query(f"SELECT concat_ws('|',s.last_trading_date,s.expiry_at,k.expiry_at) FROM ref_data.futures_option_series s JOIN market_data.contracts k "
                  f"ON(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)=(s.catalog_id,s.revision_id,s.code) "
                  f"WHERE (s.catalog_id,s.revision_id,s.code)=({R},'{lapsing['series']}') AND (k.catalog_id,k.revision_id)=({M});").split('|')

    def expire_at(day, series_at, contract_at):
        query("ALTER TABLE ref_data.futures_option_series DISABLE TRIGGER USER;ALTER TABLE market_data.contracts DISABLE TRIGGER USER;"
              f"UPDATE ref_data.futures_option_series SET last_trading_date='{day}',expiry_at='{series_at}' WHERE (catalog_id,revision_id,code)=({R},'{lapsing['series']}');"
              f"UPDATE market_data.contracts SET expiry_at='{contract_at}' WHERE (catalog_id,revision_id,futures_option_series_code)=({M},'{lapsing['series']}');"
              "ALTER TABLE ref_data.futures_option_series ENABLE TRIGGER USER;ALTER TABLE market_data.contracts ENABLE TRIGGER USER;")
    # The configured 14:30 New York close on the day before the last close.
    expire_at('2026-10-15', '2026-10-15T18:30:00Z', '2026-10-15T18:30:00Z')
    try:
        c.call('profile', {'profile': 'trader'})
        lapsed_read = blotter(current)
        c.call('profile', {'profile': 'risk'})
        lapsed_context = {**retained, 'contextId': lapsed_read['context']['contextId']}
        lapsed_excluded = c.call('portfolio/risk/catalog', dict(context=lapsed_context, bookIds=[book], portfolioIds=[]), timeout=TIMEOUT)['excludedPositions']
    finally:
        expire_at(*terms)
    lapsed = next(r for r in lapsed_read['holdings'] if r['tradeId'] == unexpired['tradeId'] and r['bindingId'] == unexpired['optionInstrumentId'])
    check('the Blotter shows a held option past its expiry with no recorded expiry as option_expired and no value',
          (lapsed['unavailableReason'], lapsed['presentValue'], lapsed['openLots']) == ('option_expired', None, '1'), lapsed)
    check('portfolio risk lists it as excluded with option_expired and keeps its premium',
          (unexpired['tradeId'], unexpired['optionInstrumentId'], 'option_expired') in {(e['tradeId'], e['bindingId'], e['reason']) for e in lapsed_excluded}
          and not any(e['tradeId'] == unexpired['tradeId'] and e['bindingId'] == unexpired['premiumInstrumentId'] for e in lapsed_excluded), lapsed_excluded)
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


def booked_premium(query, manifest, trade):
    return query(f"SELECT trim_scale(premium) FROM trading.futures_option_trade_revisions WHERE catalog_id='{manifest['catalogId']}' AND trade_id='{trade}';")

