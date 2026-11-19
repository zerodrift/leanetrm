"""HTTP checks of volatility surfaces fitted as jobs on the retained risk-job queue and read from their stored revisions, in
the owned exchange example. A synthetic role gives the trader PublishMarketData, so the trader may fit; the risk analyst
reads market data and trades but may not. Fit jobs run in the risk worker exactly as portfolio risk jobs do."""
import json
import threading
import time
from datetime import datetime, timezone
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient

OPTION = 'futures_option_premium_v1'
MODEL = 'smile_quadratic_log_moneyness_v1'
SOLVER_POLICY = 'american_futures_option_crr_v1'
SENSITIVITY_POLICY = 'option_sensitivities_layers_v1'
UTC = {'code': 'UTC', 'kind': 'fixed_offset', 'fixedOffsetSeconds': 0, 'ianaZone': None, 'tzdbVersion': None}
# The selection a fit states in full. The example's frames are working snapshots, so each fit names its clock.
SENSITIVITIES = {'policy': SENSITIVITY_POLICY, 'bump': {'ticks': 1, 'priceSteps': 2, 'absoluteTolerance': '0.000001', 'deltaRelativeTolerance': '0.15',
                                                        'gammaRelativeTolerance': '0.1'}, 'volatilityBump': '0.01', 'clock': UTC}
PRODUCT = 'nymex_on'
# The product the failing and revoked fits use, so that neither leaves a stored surface behind.
UNFITTED = 'nymex_lo'
TIMEOUT = 120
# The close fitted here; the storage checks record their synthetic revisions at another business date.
DAY = '2026-10-16'


def run_checks(url, query, manifest, *, run_worker, worker_login):
    def sign_in():
        base = PreviewClient(url); base.bootstrap()
        client = PreviewClient(url, 'exchange'); client.opener = base.opener; client.bootstrap()
        client.call('profile', {'profile': 'trader'})
        return client

    c = sign_in()
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    R = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    M = f"'{catalog}','{manifest['revisionId']}'"
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith(DAY))

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual}'
        print('PASS ' + name, flush=True)

    # The storage qualifier retained this revision with plot metadata and no fitted arrays.
    first_at = datetime.fromisoformat(manifest['frames'][0]['at'])
    navigation = c.call('workspace/navigation')
    legacy, = [f for f in navigation['frames'] if f['id'] in navigation['initialFrameIds']
               and not f.get('close') and datetime.fromisoformat(f['at']) == first_at]
    legacy_request = {'context': {'snapshotId': legacy['id'], 'convention': OPTION, 'knowledgeMode': 'current'},
                      'product': PRODUCT, 'model': MODEL}
    offer = c.call('vol-surfaces/axes', legacy_request)
    refused = c.call('vol-surfaces/read', {**legacy_request, 'axis': 'log_moneyness_v1', 'points': ['0']}, expected=422)
    check('a retained revision without fitted arrays requires an explicit refit and supplies no projected value',
          offer['snapshotId'] == legacy['id'] and offer['requiresRefit'] and refused['code'] == 'vol_axis_inputs_missing')

    def read(product=PRODUCT, snapshot=None, definition=None, **context):
        return c.call('vol-surfaces/read', {'context': {'snapshotId': snapshot or frame['id'], 'convention': OPTION, 'knowledgeMode': 'current', **context},
                                            'product': product, 'model': MODEL, 'axis': 'log_moneyness_v1', 'points': ['-0.1','0','0.1'], **({'definitionRevisionId': definition} if definition else {})}, timeout=TIMEOUT)

    def selection_at(snapshot):
        """The selection a fit at this frame states: a recorded close counts its day on its convention's clock and names
        none; a working snapshot names one."""
        at_close = any(f['id'] == snapshot and f.get('close') for f in c.call('workspace')['frames'])
        return {**SENSITIVITIES, 'clock': None} if at_close else SENSITIVITIES

    def fit(product=PRODUCT, snapshot=None, request=None, model=MODEL, expected=200, sensitivities=None):
        snapshot = snapshot or frame['id']
        return c.call('vol-surfaces/fit', {'requestId': request or str(uuid4()), 'snapshotId': snapshot, 'product': product, 'model': model,
                                           'sensitivities': sensitivities or selection_at(snapshot)}, expected=expected, timeout=TIMEOUT)

    def job(job_id):
        return json.loads(query(f"SELECT json_build_object('state',state,'errorCode',error_code,'result',result)::text FROM risk.jobs WHERE id='{job_id}';"))

    def revisions(product):
        return int(query(f"SELECT count(*) FROM trading.vol_surface_revisions WHERE catalog_id='{catalog}' AND product_code='{product}' AND effective_on='{DAY}';"))

    def fresh_scenario():
        # Each independent group starts a new sign-in: widened fits can outlast
        # the preview's 30-minute session on CI. Never renew an authority-loss
        # scenario or retry a denial; re-resolve the exact original input pins.
        nonlocal c, frame
        previous, old_frame = read(), frame
        c = sign_in()
        page = c.call('workspace')
        def inputs(f):
            return (f['at'], f['businessDate'], f['ppaBundle'], sorted(f['observationIds']),
                    sorted((curve, sorted(contracts)) for curve, contracts in f['curveContracts'].items()))
        candidates = [f for f in page['frames'] if f['id'] in page['initialFrameIds'] and inputs(f) == inputs(old_frame)]
        assert len(candidates) == 1, 'A fresh scenario needs exactly the original source, valuation and input pins.'
        frame = candidates[0]
        refused = c.call('vol-surfaces/read', {'context': {'snapshotId': old_frame['id'], 'convention': OPTION, 'knowledgeMode': 'current'},
                                             'product': PRODUCT, 'model': MODEL, 'axis': 'log_moneyness_v1', 'points': ['0']}, expected=422)
        # The backend binds the exact definition and its private source/frozen
        # pins; those internal fields are intentionally absent from page JSON.
        restored = read(definition=previous['definitionRevisionId'])
        check('a fresh sign-in rejects the prior session frame and reads the same retained fit through its exact new alias',
              frame['id'] != old_frame['id'] and refused['code'] == 'snapshot_missing'
              and restored['definitionRevisionId'] == previous['definitionRevisionId']
              and restored['revision']['id'] == previous['revision']['id'] and restored['surface'] == previous['surface']
              and restored['job']['id'] == previous['job']['id'])

    # The fitter: the trader, with only PublishMarketData added by a synthetic role.
    trader = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','trader');")
    role, grant = str(uuid4()), str(uuid4())
    query(f"""INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_surface_fitter','Synthetic surface fitter');
    INSERT INTO security.role_permissions VALUES('{role}','PublishMarketData');
    INSERT INTO security.role_grants VALUES('{grant}','{trader}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);""")

    c.call('profile', {'profile': 'risk'})
    unfitted = read()
    check('not fitted before any fit', (unfitted['state'], unfitted['revision'], unfitted['job'], unfitted['surface'], unfitted['model'], unfitted['solverPolicy'])
          == ('notFitted', None, None, None, MODEL, SOLVER_POLICY), unfitted)
    refused = fit(expected=403)
    check('reader without PublishMarketData sees canFit false and cannot fit', unfitted['canFit'] is False and refused['code'] == 'access_denied', refused)

    c.call('profile', {'profile': 'trader'})
    check('a fitter sees canFit true', read()['canFit'] is True)
    check('unknown model is refused at submit', fit(model='smile_unknown_v1', expected=422)['code'] == 'vol_model_unknown')
    offered = read()['sensitivities']
    check('the read offers the sensitivity selection and the clocks a working snapshot may name, and fills nothing in',
          offered['offered'] == {k: v for k, v in SENSITIVITIES.items() if k != 'clock'} and UTC in offered['clocks'] and offered['clockRequired'] is True, offered)
    unstated = [c.call('vol-surfaces/fit', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'product': PRODUCT, 'model': MODEL, 'sensitivities': selection},
                       expected=422, timeout=TIMEOUT)['code'] for selection in (
        None, {**SENSITIVITIES, 'clock': None}, {**SENSITIVITIES, 'policy': 'option_sensitivities_unknown_v1'}, {**SENSITIVITIES, 'volatilityBump': '0.02'},
        {**SENSITIVITIES, 'bump': {**SENSITIVITIES['bump'], 'priceSteps': 3}})]
    check('a fit that does not state a supported sensitivity selection and its clock is refused at submit',
          unstated == ['option_sensitivity_policy'] * 5 and read()['job'] is None, unstated)
    base_read={'context':{'snapshotId':frame['id'],'convention':OPTION,'knowledgeMode':'current'},'product':PRODUCT,'model':MODEL,'axis':'quick_delta_v1'}
    check('surface points are explicit and invalid JSON values are structured refusals',
          all(c.call('vol-surfaces/read',{**base_read,**extra},expected=422)['code']=='vol_axis_points'
              for extra in ({},{'points':[]},{'points':['0']},{'points':['1']},{'points':['NaN']},{'points':['0.5','0.50']},{'points':[0.5]})))
    request = str(uuid4())
    first = fit(request=request)
    check('same request id returns the same job', first['state'] == 'queued' and fit(request=request) == first, first)
    check('a second submit while queued returns the queued job', fit() == first and fit(request=request) == first)
    run_worker()
    completed = job(first['jobId'])
    fitted = read()
    surface = fitted['surface']
    check('fit job completes and read returns the stored surface',
          completed['state'] == 'completed' and fitted['state'] == 'fitted' and fitted['canFit'] is True
          and fitted['revision']['id'] == completed['result']['revision'] and completed['result']['created'] is True
          and fitted['revision']['actor'] == trader and fitted['revision']['knownFrom'] and fitted['revision']['inputsRevised'] is False
          and fitted['job'] == {'id': first['jobId'], 'state': 'completed', 'errorCode': None}
          and surface['modelId'] == MODEL and surface['series'] and surface['expiries'] and fitted['snapshotId'] == frame['id']
          and fitted['context']['effectiveOn'] == DAY, (completed, fitted['revision'], fitted['job']))
    measured = completed['result']
    check('the completed job records its series, solves and elapsed time',
          measured['series'] == len(surface['series']) and measured['solves'] == sum(s['series']['outcome'] == 'solved' for s in surface['series']) > 0
          and measured['elapsedMs'] > 0, measured)
    check('the stored surface has exact decimal text and named option rights',
          all(isinstance(s['series']['strike'], str) and s['series']['right'] in ('call', 'put') for s in surface['series'])
          and all(isinstance(v, str) for s in surface['series'] if s['series']['outcome'] == 'solved' for v in (s['series']['marketVolatility'],)),
          surface['series'][0])
    print(f"WORKLOAD volatility surface fit {PRODUCT}: {measured['series']} series, {measured['solves']} solved, {measured['elapsedMs']} ms in the risk worker", flush=True)

    # Each calibrated series' value and sensitivities, as the fit stored them under the stated selection.
    measures = ('delta', 'gamma', 'vega', 'theta')
    solved = [s for s in surface['series'] if s['series']['outcome'] == 'solved']
    intrinsic = [s for s in surface['series'] if s['series']['outcome'] == 'at_intrinsic']
    stated = surface['sensitivities']
    day = datetime.fromisoformat(frame['at']).timestamp() + 86400
    check('the surface states the selection its analytics were computed under and the day its theta runs to',
          (stated['policy'], stated['ticks'], stated['priceSteps'], stated['volatilityBump'], stated['clockSource'], stated['clockZone'])
          == (SENSITIVITY_POLICY, 1, 2, '0.01', 'request', 'UTC') and datetime.fromisoformat(stated['dayTo']).timestamp() == day, stated)
    check('every calibrated series has analytics, each measure a figure or the code of why there is none',
          solved and intrinsic and all(s['analytics'] and all((s['analytics'][m]['value'] is None) != (s['analytics'][m]['missing'] is None) for m in measures)
                                       for s in solved + intrinsic)
          and all(s['analytics'] is None for s in surface['series'] if s['series']['outcome'] not in ('solved', 'at_intrinsic')),
          [s for s in solved + intrinsic if not s['analytics']][:2])
    check('a series valued at intrinsic has its settlement as its value and no sensitivities',
          all(Decimal(s['analytics']['value']) == Decimal(s['series']['settlement']) and s['analytics']['priceBump'] is None
              and all(s['analytics'][m] == {'value': None, 'missing': 'option_at_intrinsic'} for m in measures) for s in intrinsic), intrinsic[:1])

    def figure(row, measure):
        return None if row['analytics'][measure]['value'] is None else Decimal(row['analytics'][measure]['value'])
    absent = {}
    for row in solved:
        for measure in measures:
            if figure(row, measure) is None:
                absent[row['analytics'][measure]['missing']] = absent.get(row['analytics'][measure]['missing'], 0) + 1
    print(f"WORKLOAD option analytics {PRODUCT}: {len(solved)} solved series, {measured['analyticsTreePrices']} tree prices, {measured['analyticsMs']} ms, "
          f"missing measures by code {absent}", flush=True)
    check('a solved series is valued within one option tick of its settlement',
          all(abs(Decimal(s['analytics']['value']) - Decimal(s['series']['settlement'])) <= Decimal('0.001') for s in solved),
          [(s['series']['code'], s['analytics']['value'], s['series']['settlement']) for s in solved][:3])
    signs = [(s['series']['code'], m) for s in solved for m, holds in (
        ('delta', lambda v, s=s: 0 < v < 1 if s['series']['right'] == 'call' else -1 < v < 0), ('gamma', lambda v: v > 0), ('vega', lambda v: v > 0),
        ('theta', lambda v: v < 0)) if figure(s, m) is not None and not holds(figure(s, m))]
    check('stored sensitivities have the sign their measure must have', not signs and measured['analyticsTreePrices'] > 0 and measured['analyticsMs'] >= 0, signs[:5])
    # Preserve the original near-money parity qualification. Farther wings can
    # have material early-exercise/clearing-rounding effects under discounting;
    # every added ON series still has the price/sign checks above.
    original_calls={f'{PRODUCT}_{month}_c{Decimal("3.25")+i*Decimal("0.05")+n*Decimal("0.05")}'
                    for i,month in enumerate(('20261201','20270101','20270201','20270301','20270401'))
                    for n in (-6,-4,-2,0,2,4,6)}
    pairs = {}
    for row in solved:
        pairs.setdefault((row['series']['expiryAt'], row['series']['strike']), {})[row['series']['right']] = row
    both = [p for p in pairs.values() if len(p) == 2 and all(figure(r, m) is not None for r in p.values() for m in ('delta', 'gamma', 'vega'))]
    qualified = [p for p in both if p['call']['series']['code'] in original_calls]
    parity = [(p['call']['series']['code'], figure(p['call'], 'delta') - figure(p['put'], 'delta')) for p in qualified
              if abs(figure(p['call'], 'delta') - figure(p['put'], 'delta') - 1) > Decimal('0.05')
              or abs(figure(p['call'], 'vega') - figure(p['put'], 'vega')) > Decimal('0.05') * figure(p['call'], 'vega')]
    check('the original near-money call and put pairs agree in their sensitivities', len(qualified) == 35 and not parity, (len(qualified), len(pairs), parity[:5]))
    check('most solved series have every sensitivity', sum(absent.values()) * 10 <= len(solved) * 4, absent)

    # Booked option positions are scaled from these stored analytics; the read prices nothing.
    held = c.call('options/positions', {'context': {'snapshotId': frame['id'], 'convention': OPTION, 'knowledgeMode': 'current'}, 'model': MODEL}, timeout=TIMEOUT)
    rows = [(group, row) for group in held['groups'] for row in group['rows']]
    figures = ('value',) + measures
    check('position delta and gamma are stated in the unit the underlying is quoted per, never the option contract',
          rows and {(group['product'], group['quantityUnit']) for group, _ in rows} <= {('nymex_on', 'MMBtu (IT)'), ('nymex_lo', 'bbl (42 US gal)')},
          sorted({(g['product'], g['quantityUnit']) for g, _ in rows}))
    check('a group states the selection its surface was fitted under and the day its thetas run to',
          all(group['sensitivities'] == {k: stated[k] for k in ('policy', 'ticks', 'priceSteps', 'volatilityBump', 'clockSource', 'clockZone', 'dayTo')}
              for group, _ in rows if group['product'] == PRODUCT), [g['sensitivities'] for g, _ in rows][:1])
    check('booked option positions read with their series\' stored analytics, or the reason there are none',
          held['model'] == MODEL and all(row['state'] in ('analysed', 'unavailable', 'notCalibrated')
                                         and all((row[m]['amount'] is None) != (row[m]['missing'] is None) for m in figures) for _, row in rows)
          and all(row['state'] != 'notCalibrated' for group, row in rows if group['product'] == PRODUCT)
          and all(row[m]['missing'] == 'not_calibrated' for group, row in rows if group['product'] != PRODUCT for m in figures)
          and all(set(total) == {'currency', 'value', 'theta'} for total in held['totals']), held)
    print(f"WORKLOAD option positions: {len(rows)} positions in {len(held['groups'])} underlying futures", flush=True)
    check('an unknown model is refused for positions',
          c.call('options/positions', {'context': {'snapshotId': frame['id'], 'convention': OPTION, 'knowledgeMode': 'current'}, 'model': 'smile_unknown_v1'},
                 expected=422)['code'] == 'vol_model_unknown')

    # A strategy of legs on one expiry. Its setup is offered from the stored surface; its analysis is sent what was offered,
    # calibrates the legs as valuation does, and books and stores nothing.
    context = {'snapshotId': frame['id'], 'convention': OPTION, 'knowledgeMode': 'current'}
    by_series = {s['series']['code']: s for s in surface['series']}
    def at_the_money(expiry):
        return {s['series']['right'] for s in solved if s['series']['expiryAt'] == expiry['expiryAt'] and s['series']['strike'] == expiry['atTheMoneyStrike']}
    live = next((e for e in surface['expiries'] if e['status'] == 'fitted' and at_the_money(e) == {'call', 'put'}), None)
    check('an expiry has a solved call and put at its at-the-money strike', live is not None, [e['expiryAt'] for e in surface['expiries']])
    setup = c.call('options/strategy/setup', {'context': context, 'product': PRODUCT, 'model': MODEL, 'expiryAt': live['expiryAt']}, timeout=TIMEOUT)
    templates = {t['id']: t for t in setup['templates']}
    straddle, offered = templates['straddle'], setup['offered']
    check('a strategy setup offers each template\'s legs at the series\' settlements, or the code of why it has none, from the stored surface',
          setup['state'] == 'fitted' and list(templates) == ['call_spread', 'put_spread', 'straddle', 'strangle', 'iron_condor', 'butterfly', 'risk_reversal']
          and all((t['legs'] is None) != (t['refusal'] is None) for t in setup['templates'])
          and all(t['refusal']['code'] in ('strategy_template_strike', 'strategy_template_series') for t in setup['templates'] if t['refusal'])
          and [(leg['right'], leg['strike'], leg['side'], leg['lots']) for leg in straddle['legs']] == [(right, live['atTheMoneyStrike'], 'buy', 1) for right in ('call', 'put')]
          and all(leg['entryPrice'] == by_series[leg['series']]['series']['settlement'] for t in setup['templates'] if t['legs'] for leg in t['legs'])
          and setup['atTheMoneyStrike'] == live['atTheMoneyStrike'] and setup['underlyingSeries'] == live['underlyingCode']
          and setup['templateRule'] == 'listed_strike_offsets_from_at_the_money_v1', setup['templates'])
    check('the setup offers a price range, the days to expiry on the surface\'s clock and the policies an analysis names',
          setup['clock'] == {'source': 'surface', 'zone': 'UTC'} and offered['sensitivities']['clock']['code'] == 'UTC'
          and offered['sensitivities']['policy'] == SENSITIVITY_POLICY and offered['expiryRule'] == 'option_expiry_exercise_value_v1'
          and datetime.fromisoformat(offered['instants'][0]) == datetime.fromisoformat(frame['at']) and offered['selectedInstants'] == offered['instants'][:1]
          and all(datetime.fromisoformat(b).timestamp() - datetime.fromisoformat(a).timestamp() == 86400 for a, b in zip(offered['instants'], offered['instants'][1:]))
          and datetime.fromisoformat(offered['instants'][-1]) < datetime.fromisoformat(offered['instantsBefore']) <= datetime.fromisoformat(live['expiryAt'])
          and Decimal(offered['prices']['lower']) < Decimal(live['atTheMoneyStrike']) < Decimal(offered['prices']['upper']) and offered['prices']['points'] == 21
          and setup['limits'] == {'legs': 4, 'points': 41, 'curves': 3, 'lots': 100000, 'volatilityShiftPoints': 100}, offered)

    def analyse(legs, expected=200, **changes):
        return c.call('options/strategy/analyze', {'context': context, 'product': PRODUCT, 'expiryAt': live['expiryAt'],
                                                   'legs': [{'series': leg['series'], 'side': leg['side'], 'lots': leg['lots'], 'entryPrice': leg['entryPrice']} for leg in legs],
                                                   'prices': {**offered['prices'], 'points': 11}, 'instants': offered['selectedInstants'], 'volatilityShiftPoints': '0',
                                                   'valuation': offered['valuation'], 'sensitivities': offered['sensitivities'], 'expiryRule': offered['expiryRule'], **changes},
                      expected=expected, timeout=TIMEOUT)
    stored = revisions(PRODUCT)
    analysis = analyse(straddle['legs'])
    strike, paid = Decimal(live['atTheMoneyStrike']), sum(Decimal(leg['entryPrice']) for leg in straddle['legs'])
    multiplier = Decimal(analysis['legs'][0]['multiplier'])
    expiry = analysis['expiry']
    check('a straddle\'s payoff at expiry is exact: its premium, its largest loss at the strike, no bound on its profit and a breakeven either side',
          expiry['rule'] == 'option_expiry_exercise_value_v1' and Decimal(expiry['netPremium']['amount']) == paid * multiplier and expiry['netPremium']['direction'] == 'debit'
          and expiry['maximumProfit'] == {'amount': None, 'bounded': False} and Decimal(expiry['maximumLoss']['amount']) == -paid * multiplier and expiry['maximumLoss']['bounded']
          and [(Decimal(b['lower']), Decimal(b['upper'])) for b in expiry['breakevens']] == [(strike - paid, strike - paid), (strike + paid, strike + paid)]
          and next(Decimal(p['profitAndLoss']) for p in expiry['curve'] if Decimal(p['price']) == strike) == -paid * multiplier, expiry)
    check('each leg is calibrated as its series was fitted, and the strategy\'s value is its legs\' times multiplier and lots',
          all(leg['state'] == 'solved' and leg['reason'] is None and leg['impliedVolatility'] == by_series[leg['series']]['series']['marketVolatility']
              and leg['settlement'] is not None and leg['delta']['value'] is not None
              and (figure(by_series[leg['series']], 'delta') is None or abs(Decimal(leg['delta']['value']) - figure(by_series[leg['series']], 'delta')) <= Decimal('0.02'))
              for leg in analysis['legs'])
          and Decimal(analysis['strategy']['value']['amount']) == sum(Decimal(leg['value']) * multiplier for leg in analysis['legs'])
          and all((analysis['strategy'][m]['amount'] is None) != (analysis['strategy'][m]['missing'] is None) for m in figures)
          and (analysis['currency'], analysis['quantityUnit'], analysis['underlyingSeries']) == ('USD', 'MMBtu (IT)', live['underlyingCode']), (analysis['legs'], analysis['strategy']))
    dated = analysis['dated']
    check('the dated curve at the valuation instant values the legs at each price point, and passes near the premium at the settled futures price',
          dated['state'] == 'valued' and len(dated['curves']) == 1 and datetime.fromisoformat(dated['curves'][0]['at']) == datetime.fromisoformat(frame['at'])
          and len(dated['curves'][0]['points']) == 11 and all(Decimal(p['value']) - paid * multiplier == Decimal(p['profitAndLoss']) for p in dated['curves'][0]['points'])
          and all(Decimal(p['profitAndLoss']) >= next(Decimal(q['profitAndLoss']) for q in expiry['curve'] if q['price'] == p['price']) for p in dated['curves'][0]['points']), dated)
    check('the analysis echoes what it was asked and the policies it ran under, with its limits and the work done',
          analysis['request'] == {'prices': {**offered['prices'], 'points': 11}, 'instants': [dated['curves'][0]['at']], 'volatilityShiftPoints': '0'}
          and analysis['policies']['valuation'] == offered['valuation'] and analysis['policies']['sensitivities']['policy'] == SENSITIVITY_POLICY
          and analysis['policies']['sensitivities']['clockZone'] == 'UTC' and analysis['policies']['expiryRule'] == offered['expiryRule']
          and analysis['policies']['volatilityShift'] == {'convention': 'absolute_annualized_volatility_parent_additive_v1', 'size': '0'}
          and analysis['limits'] == setup['limits'] and analysis['work']['calibrationTreePrices'] > 0 and analysis['work']['valuationTreePrices'] >= 22
          and analysis['work']['treePrices'] == analysis['work']['calibrationTreePrices'] + analysis['work']['valuationTreePrices']
          and analysis['work']['curveSets'] == 4 and analysis['context']['effectiveOn'] == DAY, (analysis['request'], analysis['policies'], analysis['work']))
    print(f"WORKLOAD option strategy analysis: 2 legs, 11 points, 1 curve, {analysis['work']['calibrationTreePrices']} calibration and "
          f"{analysis['work']['valuationTreePrices']} valuation tree prices in {analysis['work']['elapsedMs']} ms", flush=True)
    shifted = analyse(straddle['legs'], volatilityShiftPoints='5')
    check('a volatility shift raises a long straddle\'s dated curve, never lowering a point, and leaves its payoff at expiry alone',
          shifted['dated']['state'] == 'valued' and shifted['policies']['volatilityShift']['size'] == '0.05' and shifted['expiry'] == expiry
          and all(Decimal(a['profitAndLoss']) >= Decimal(b['profitAndLoss']) for a, b in zip(shifted['dated']['curves'][0]['points'], dated['curves'][0]['points']))
          and any(Decimal(a['profitAndLoss']) > Decimal(b['profitAndLoss']) for a, b in zip(shifted['dated']['curves'][0]['points'], dated['curves'][0]['points'])),
          shifted['dated'])
    other = next((s for s in solved if s['series']['expiryAt'] != live['expiryAt']), None)
    leg = straddle['legs'][0]
    for code, changes in (('strategy_limit', {'legs': [{**leg, 'series': f'synthetic_{n}'} for n in range(5)]}), ('strategy_limit', {'prices': {**offered['prices'], 'points': 42}}),
                          ('strategy_limit', {'instants': offered['instants'][:4]} if len(offered['instants']) >= 4 else {'prices': {**offered['prices'], 'points': 42}}),
                          ('strategy_leg_series', {'legs': [{**leg, 'series': 'synthetic_unlisted'}]}), ('strategy_curve_instant', {'instants': [offered['instantsBefore']]}),
                          ('strategy_price_range', {'prices': {**offered['prices'], 'lower': '0'}}), ('strategy_price_range', {'prices': {**offered['prices'], 'points': 4, 'upper': str(Decimal(offered['prices']['lower']) + 1)}}),
                          ('option_policy', {'valuation': {**offered['valuation'], 'calibrationFallback': 'none'}}), ('strategy_expiry_rule', {'expiryRule': 'synthetic_other_rule'}),
                          ('option_sensitivity_policy', {'sensitivities': {**offered['sensitivities'], 'clock': None}}),
                          *((('strategy_expiry', {'legs': [leg, {**leg, 'series': other['series']['code']}]}),) if other else ())):
        refused = analyse(changes.pop('legs', straddle['legs']), expected=422, **changes)
        check(f'an analysis beyond a limit or outside the offer is refused, not cut down: {code}', refused['code'] == code, refused)
    unvalued = next((s for s in intrinsic if s['series']['expiryAt'] == live['expiryAt'] and s['series']['settlement'] is not None), None)
    if unvalued:
        mixed = analyse([leg, {'series': unvalued['series']['code'], 'side': 'sell', 'lots': 1, 'entryPrice': unvalued['series']['settlement']}])
        check('a leg valued at intrinsic is said on its row and refuses the dated curves; the payoff at expiry is still stated',
              mixed['dated'] == {'state': 'refused', 'reason': 'option_at_intrinsic', 'message': mixed['dated']['message'], 'curves': []} and mixed['dated']['message']
              and [row['state'] for row in mixed['legs']] == ['solved', 'at_intrinsic'] and mixed['legs'][1]['reason'] == 'option_at_intrinsic'
              and mixed['legs'][1]['delta'] == {'value': None, 'missing': 'option_at_intrinsic'} and mixed['legs'][0]['delta']['value'] is not None
              and mixed['strategy']['delta'] == {'amount': None, 'missing': 'incomplete'} and mixed['strategy']['value']['amount'] is not None
              and len(mixed['expiry']['curve']) >= 11 and mixed['expiry']['netPremium']['direction'] in ('debit', 'credit', 'none'), mixed)
    check('a strategy analysis stores no surface revision', revisions(PRODUCT) == stored, revisions(PRODUCT))

    refit = fit()
    run_worker()
    again = job(refit['jobId'])
    check('refit is a no-op', refit['jobId'] != first['jobId'] and again['state'] == 'completed'
          and (again['result']['revision'], again['result']['created']) == (fitted['revision']['id'], False) and revisions(PRODUCT) == 1, again)

    # One option settlement the stored surface pinned, revised by the market data editor.
    revised = query(f"SELECT o.id||'|'||trim_scale(o.value) FROM trading.vol_surface_inputs i JOIN market_data.observations o ON(o.catalog_id,o.id)=(i.catalog_id,i.observation_id) "
                    f"JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
                    f"JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id) "
                    f"WHERE i.revision_id='{fitted['revision']['id']}' AND k.futures_option_series_code IS NOT NULL ORDER BY o.id LIMIT 1;").split('|')
    c.call('profile', {'profile': 'editor'})
    corrected = c.call('corrections', {'snapshotId': frame['id'], 'observationId': revised[0], 'value': str(Decimal(revised[1]) + Decimal('0.001'))})
    c.call('profile', {'profile': 'trader'})
    flagged = read()
    check('revised settlement flags the stored surface', flagged['state'] == 'fitted' and flagged['revision']['inputsRevised'] is True
          and flagged['revision']['id'] == fitted['revision']['id'] and flagged['surface'] == surface, flagged['revision'])

    renewed = fit(snapshot=corrected['frame']['id'])
    run_worker()
    recorded = job(renewed['jobId'])
    current = read(snapshot=corrected['frame']['id'])
    earlier = read(knowledgeMode='retained', contextId=flagged['context']['contextId'])
    check('refit after revision records a new revision', recorded['state'] == 'completed' and recorded['result']['created'] is True
          and current['revision']['id'] == recorded['result']['revision'] != fitted['revision']['id'] and current['revision']['inputsRevised'] is False
          and revisions(PRODUCT) == 2, (recorded, current['revision']))
    check('the old cutoff still reads the old revision', earlier['revision']['id'] == fitted['revision']['id'] and earlier['surface'] == surface, earlier['revision'])

    # One discount-curve point the stored surface pinned, revised by the market data editor: the fit discounts each series to
    # its expiry, so the point is one of its inputs.
    rate = query(f"SELECT o.id||'|'||trim_scale(o.value) FROM trading.vol_surface_inputs i JOIN market_data.observations o ON(o.catalog_id,o.id)=(i.catalog_id,i.observation_id) "
                 f"JOIN market_data.rate_curve_tenors p ON(p.catalog_id,p.revision_id,p.series_id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
                 f"WHERE i.revision_id='{current['revision']['id']}' ORDER BY o.id LIMIT 1;")
    check('the stored surface pins the discount-curve points it used', rate != '', current['revision'])
    rate = rate.split('|')
    c.call('profile', {'profile': 'editor'})
    discounted = c.call('corrections', {'snapshotId': corrected['frame']['id'], 'observationId': rate[0], 'value': str(Decimal(rate[1]) + Decimal('0.0025'))})
    c.call('profile', {'profile': 'trader'})
    stale = read(snapshot=corrected['frame']['id'])
    check('a revised discount-curve point flags the stored surface', stale['revision']['inputsRevised'] is True
          and stale['revision']['id'] == current['revision']['id'], stale['revision'])
    rediscounted = fit(snapshot=discounted['frame']['id'])
    run_worker()
    rerun = job(rediscounted['jobId'])
    latest = read(snapshot=discounted['frame']['id'])
    check('refit after a discount-curve revision records a new revision', rerun['state'] == 'completed' and rerun['result']['created'] is True
          and latest['revision']['id'] == rerun['result']['revision'] != current['revision']['id'] and latest['revision']['inputsRevised'] is False
          and latest['surface'] != current['surface'] and revisions(PRODUCT) == 3, (rerun, latest['revision']))

    c.call('market/context', {'effectiveOn': DAY, 'knownAt': latest['context']['knownAt'], 'mode': 'at', 'selection': 'latest_correction'})
    try:
        alias = next(f for f in c.call('workspace')['frames'] if f['at'] == frame['at'])
        historical = fit(snapshot=alias['id'], expected=422)
    finally:
        c.call('market/current', {})
    check('historical context cannot fit', historical['code'] == 'historical_read_only', historical)

    # The submit operation itself admits only a context captured from current knowledge: one captured at an earlier cutoff,
    # or a current capture that a later capture has superseded, is refused with historical_read_only.
    retained = json.loads(query(f"SELECT json_build_object('request',request,'frame',frame,'revision',revision_id)::text FROM risk.jobs WHERE id='{first['jobId']}';"))

    def admission(capture):
        """Captures contexts in one transaction, binds the last one named `bound`, and submits a fit at it."""
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        body = (f"DECLARE bound uuid;later uuid;BEGIN {capture}PERFORM security.use_market_context('{catalog}',bound);"
                f"PERFORM security.use_trading_context('{catalog}','{frame['at']}');"
                f"PERFORM risk.submit_vol_surface_fit('{catalog}','{uuid4()}',bound,'{retained['revision']}','{frame['at']}',"
                f"'{json.dumps(retained['request'])}'::jsonb,jsonb_set('{json.dumps(retained['frame'])}'::jsonb,'{{contextId}}',to_jsonb(bound::text)));"
                "RAISE EXCEPTION 'admitted';EXCEPTION WHEN SQLSTATE 'P0001' THEN PERFORM set_config('check.outcome',SQLERRM,true);END")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
                     f"DO $check$ {body} $check$;SELECT current_setting('check.outcome');ROLLBACK;").splitlines()[-1]
    current_capture = f"bound=security.capture_date_context('{catalog}','{DAY}',NULL);"
    check('the submit operation admits a context captured from current knowledge', admission(current_capture) == 'admitted')
    check('the submit operation refuses a context captured at an earlier cutoff',
          admission(f"bound=security.capture_date_context('{catalog}','{DAY}','{fitted['context']['knownAt']}');") == 'historical_read_only')
    check('the submit operation refuses a current capture a later capture superseded',
          admission(current_capture + f"later=security.capture_date_context('{catalog}','{DAY}',NULL);") == 'historical_read_only')

    # A context captured in an earlier transaction stays current only until the next certification: an admitted submit holds the
    # knowledge clock, so a certification waits for it to commit rather than superseding the context it was admitted at.
    def as_trader(statements):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statements}").splitlines()[-1]
    earlier = as_trader(f"SELECT security.capture_date_context('{catalog}','{DAY}',NULL);COMMIT;")
    # psql sends each statement on its own, so the holder is found by its application name while it sleeps after the submit.
    held = {}

    def hold():
        try:
            held['admitted'] = as_trader(
                "SET LOCAL application_name='vol_surface_held_submit';"
                f"SELECT security.use_market_context('{catalog}','{earlier}');SELECT security.use_trading_context('{catalog}','{frame['at']}');"
                f"SELECT risk.submit_vol_surface_fit('{catalog}','{uuid4()}','{earlier}','{retained['revision']}','{frame['at']}','{json.dumps(retained['request'])}'::jsonb,"
                f"jsonb_set('{json.dumps(retained['frame'])}'::jsonb,'{{contextId}}',to_jsonb('{earlier}'::text)))->>'jobId';SELECT pg_sleep(3);ROLLBACK;SELECT 'held';")
        except Exception as error:
            held['error'] = error
    holding = threading.Thread(target=hold)
    holding.start()
    deadline = time.monotonic() + 10
    while query("SELECT count(*) FROM pg_stat_activity WHERE application_name='vol_surface_held_submit' AND wait_event='PgSleep';") == '0':
        assert time.monotonic() < deadline and holding.is_alive(), held
        time.sleep(.05)
    certification = as_trader("SET LOCAL lock_timeout='500ms';DO $check$ BEGIN "
                              f"PERFORM security.capture_date_context('{catalog}','{DAY}',NULL);PERFORM set_config('check.outcome','certified',true);"
                              "EXCEPTION WHEN lock_not_available THEN PERFORM set_config('check.outcome','waited',true);END $check$;"
                              "SELECT current_setting('check.outcome');ROLLBACK;")
    holding.join()
    check('a certification waits for an admitted submit to commit', certification == 'waited' and held == {'admitted': 'held'}, (certification, held))

    # The close that leaves one option underlying unsettled, fitted for both products at its own business date.
    gap = manifest['missingSettlement']
    unpinned = next(f for f in c.call('workspace')['frames'] if f['at'].replace('+00:00', 'Z') == gap['at'])
    examples = {}
    for product in (PRODUCT, UNFITTED):
        submitted = fit(product=product, snapshot=unpinned['id'])
        run_worker()
        examples[product] = (job(submitted['jobId']), read(product=product, snapshot=unpinned['id'])['surface'])

    def shows_smile(completed, example):
        if completed['state'] != 'completed' or not example:
            return False
        gaps = [e['status'] for e in example['expiries'] if e['underlyingCode'] == gap['underlying']]
        moves = [Decimal(s['richCheap']) for s in example['series'] if s['richCheap'] is not None]
        return (all(e['status'] == 'fitted' for e in example['expiries'] if e['underlyingCode'] != gap['underlying'])
                and gaps in ([], ['vol_fit_insufficient_strikes'])
                and any(m > Decimal('0.005') for m in moves) and any(m < Decimal('-0.005') for m in moves))
    check('example surface shows rich and cheap', all(shows_smile(*examples[p]) for p in examples)
          and any(e['underlyingCode'] == gap['underlying'] for _, s in examples.values() if s for e in s['expiries']),
          {p: (j['state'], j['errorCode'], s and [(e['underlyingCode'], e['status']) for e in s['expiries']]) for p, (j, s) in examples.items()})

    # Every listed series of the other product moved past its expiry: the fit then has no live series to calibrate.
    terms = query(f"SELECT coalesce(json_agg(json_build_object('code',s.code,'day',s.last_trading_date,'at',s.expiry_at,'contract',k.expiry_at)),'[]')::text "
                  f"FROM ref_data.futures_option_series s JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)=(s.catalog_id,s.revision_id,s.code) "
                  f"WHERE (s.catalog_id,s.revision_id)=({R}) AND s.product_code='{UNFITTED}' AND (k.catalog_id,k.revision_id)=({M});")

    def expiries(rows):
        query("ALTER TABLE ref_data.futures_option_series DISABLE TRIGGER USER;ALTER TABLE market_data.contracts DISABLE TRIGGER USER;"
              + ''.join(f"UPDATE ref_data.futures_option_series SET last_trading_date='{row['day']}',expiry_at='{row['at']}' WHERE (catalog_id,revision_id,code)=({R},'{row['code']}');"
                        f"UPDATE market_data.contracts SET expiry_at='{row['contract']}' WHERE (catalog_id,revision_id,futures_option_series_code)=({M},'{row['code']}');" for row in rows)
              + "ALTER TABLE ref_data.futures_option_series ENABLE TRIGGER USER;ALTER TABLE market_data.contracts ENABLE TRIGGER USER;")
    original = json.loads(terms)
    assert original
    # The other product's first expiry moved before an earlier close, fitted at that close's own business date so that no
    # surface is left at the close the later checks read: its series are listed as expired, and the others still solve.
    lapsed_day = min(row['day'] for row in original)
    lapsed = [{**row, 'day': '2026-10-13', 'at': '2026-10-13T18:30:00Z', 'contract': '2026-10-13T18:30:00Z'} for row in original if row['day'] == lapsed_day]
    close = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-14'))
    expiries(lapsed)
    try:
        partial = fit(product=UNFITTED, snapshot=close['id'])
        run_worker()
    finally:
        expiries(original)
    listed = read(product=UNFITTED, snapshot=close['id'])
    gone = {row['code'] for row in lapsed}
    shown = [s['series'] for s in (listed['surface'] or {'series': []})['series']]
    check('an expired series is listed with option_expired', job(partial['jobId'])['state'] == 'completed'
          and sorted(s['code'] for s in shown) == sorted(row['code'] for row in original)
          and all((s['outcome'], s['settlement'], s['marketVolatility']) == ('option_expired', None, None) for s in shown if s['code'] in gone)
          and any(s['outcome'] == 'solved' for s in shown if s['code'] not in gone)
          # The expiry whose every series expired is labelled so, and only live series' strikes make up the axis.
          and [e['status'] for e in listed['surface']['expiries'] if e['expiryAt'].replace('+00:00', 'Z') == lapsed[0]['at']] == ['option_expired']
          and listed['surface']['strikeAxis'] == sorted({s['strike'] for s in shown if s['code'] not in gone}, key=Decimal),
          (job(partial['jobId']), [(s['code'], s['outcome']) for s in shown if s['code'] in gone]))
    expiries([{**row, 'day': '2026-10-15', 'at': '2026-10-15T18:30:00Z', 'contract': '2026-10-15T18:30:00Z'} for row in original])
    try:
        failing = fit(product=UNFITTED)
        run_worker()
    finally:
        expiries(original)
    failed = read(product=UNFITTED)
    check('a failing fit finishes failed with its code', job(failing['jobId'])['state'] == 'failed' and failed['state'] == 'notFitted'
          and failed['job'] == {'id': failing['jobId'], 'state': 'failed', 'errorCode': 'option_expired'} and revisions(UNFITTED) == 0, failed)

    # The completion of a claimed attempt, called as the worker login. Its digest is one a recording would refuse with
    # vol_surface_input, so a refusal other than the fence's or the authority recheck's means the recording was reached first.
    fenced = fit()
    engine = query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{fenced['jobId']}';")
    # A short lease, so that the attempt left fenced below expires and is failed within this check.
    claim = json.loads(query(f"SET SESSION AUTHORIZATION {worker_login};SELECT risk.claim(10,'{engine}')::text;"))
    pinned = query(f"SELECT observation_id FROM risk.job_observations WHERE job_id='{fenced['jobId']}' ORDER BY observation_id LIMIT 1;")

    def finish(token):
        try:
            query(f"SET SESSION AUTHORIZATION {worker_login};SELECT risk.finish_vol_surface_fit('{fenced['jobId']}','{token}','{{{pinned}}}',"
                  "'{\"synthetic\":\"surface\",\"sensitivities\":{}}','not_a_digest','{\"analyticsMs\":1,\"analyticsTreePrices\":1,\"elapsedMs\":1,\"series\":1,\"solves\":1}');")
        except RuntimeError as error:
            return str(error)
        return 'completed'
    wrong = finish(uuid4())
    check('a completion with another lease token is fenced and records nothing', claim['jobId'] == fenced['jobId']
          and 'risk_job_fence' in wrong and revisions(PRODUCT) == 3 and job(fenced['jobId'])['state'] == 'running', wrong)
    query(f"UPDATE security.role_grants SET revoked=true WHERE id='{grant}';")
    lost = finish(claim['leaseToken'])
    check('a completion after the fitter lost PublishMarketData is refused and records nothing',
          'risk_job_authority_changed' in lost and revisions(PRODUCT) == 3 and job(fenced['jobId'])['state'] == 'running', lost)
    # Once its lease expires, the next claim fails the fenced attempt through the queue's own authority-changed path.
    time.sleep(max(0, (datetime.fromisoformat(claim['leaseExpiresAt']) - datetime.now(timezone.utc)).total_seconds()) + 0.5)
    query(f"SET SESSION AUTHORIZATION {worker_login};SELECT risk.claim(10,'{engine}');")
    check('the fenced attempt is failed once its lease expires', (job(fenced['jobId'])['state'], job(fenced['jobId'])['errorCode'])
          == ('failed', 'risk_job_authority_changed') and revisions(PRODUCT) == 3, job(fenced['jobId']))
    grant = str(uuid4())
    query(f"INSERT INTO security.role_grants VALUES('{grant}','{trader}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);")

    # A surface belongs to the exact close it was fitted at (D356). The latest close's frame, recorded as the close of two
    # synthetic sessions with the same cutoff: a fit at one session's close is not the other's, nor the working snapshot's at the
    # same instant, and a close that replaces it starts without one. No member chooses these sessions, so the browser checks
    # that follow still open on working snapshots.
    fresh_scenario()
    from seed_session_closes import publish_session, record_close
    window = [{'curves': sorted(frame['curveContracts']), 'cutoff': frame['at'][11:16], 'from': '2026-10-13', 'to': '2026-10-17'}]
    for session in ('synthetic_east', 'synthetic_west'):
        publish_session(query, manifest, session, f'Synthetic {session[10:]} close', window, calendar='source_business_days')
    east, west = (record_close(query, manifest, session, 1, frame) for session in ('synthetic_east', 'synthetic_west'))

    def close_frame(client, close):
        return next(f for f in client.call('workspace')['frames'] if f.get('originCloseId') == close)

    def library(client, snapshot, product=None):
        return client.call('vol-surfaces/library', {'context': {'snapshotId': snapshot, 'convention': OPTION, 'knowledgeMode': 'current'}, 'model': MODEL,
                                                    'offset': 0, **({'product': product} if product else {})}, timeout=TIMEOUT)
    at_east, at_west = close_frame(c, east), close_frame(c, west)
    check('each recorded close is its own frame naming its session', (at_east['close']['session'], at_west['close']['session']) == ('synthetic_east', 'synthetic_west')
          and at_east['id'] != at_west['id'] and at_east['at'] == at_west['at'] == frame['at'], (at_east.get('close'), at_west.get('close')))
    before_close = read(snapshot=at_east['id'])
    working = read()
    check('a close starts without the working snapshot\'s surface at the same instant', before_close['state'] == 'notFitted' and before_close['job'] is None
          and working['state'] == 'fitted', (before_close['state'], working['state']))
    offered_east = before_close['sensitivities']
    check('a recorded close counts its day on its convention\'s clock: the read asks for none and a fit that names one is refused',
          offered_east['clockRequired'] is False and fit(snapshot=at_east['id'], sensitivities=SENSITIVITIES, expected=422)['code'] == 'option_sensitivity_policy'
          and read(snapshot=at_east['id'])['job'] is None, offered_east)
    eastern = fit(snapshot=at_east['id'])
    run_worker()
    close_stated = read(snapshot=at_east['id'])['surface']['sensitivities']
    check('a surface fitted at a recorded close states its convention\'s clock and the day its theta runs to',
          close_stated['clockSource'] == 'close_convention' and close_stated['clockZone']
          and datetime.fromisoformat(close_stated['dayTo']) > datetime.fromisoformat(at_east['at']), close_stated)
    fitted_east, unfitted_west = read(snapshot=at_east['id']), read(snapshot=at_west['id'])
    check('a fit at one session\'s close is that close\'s alone', fitted_east['state'] == 'fitted' and fitted_east['job']['id'] == eastern['jobId']
          and unfitted_west['state'] == 'notFitted' and unfitted_west['job'] is None and read()['revision']['id'] == working['revision']['id'],
          (fitted_east['state'], unfitted_west, read()['revision']))
    # Another sign-in reads the same close and sees the same surface.
    signed_in = PreviewClient(url); signed_in.bootstrap()
    stranger = PreviewClient(url, 'exchange'); stranger.opener = signed_in.opener; stranger.bootstrap()
    stranger.call('profile', {'profile': 'risk'})
    seen = stranger.call('vol-surfaces/read', {'context': {'snapshotId': close_frame(stranger, east)['id'], 'convention': OPTION, 'knowledgeMode': 'current'},
                                               'product': PRODUCT, 'model': MODEL, 'axis': 'log_moneyness_v1', 'points': ['0']}, timeout=TIMEOUT)
    check('another sign-in reads the same surface at that close', seen['state'] == 'fitted' and seen['revision']['id'] == fitted_east['revision']['id'], seen['state'])
    # A historical view sees the close under a session-scoped alias id, and still reads the surface fitted at that close.
    view = c.call('market/context', {'effectiveOn': DAY, 'knownAt': datetime.now(timezone.utc).isoformat(), 'mode': 'at', 'selection': 'latest_correction'})['context']
    try:
        alias = close_frame(c, east)
        aliased = read(snapshot=alias['id'], knowledgeMode='retained', contextId=view['contextId'])
    finally:
        c.call('market/current', {})
    check('a historical view reads the close under its own alias id and sees the surface fitted there',
          alias['id'] != east and aliased['state'] == 'fitted' and aliased['revision']['id'] == fitted_east['revision']['id'], (alias['id'], aliased['state']))
    listed = library(c, at_west['id'], PRODUCT)['surfaces']
    eastern_row = next((row for row in listed if row['closeId'] == east), None)
    check('the library lists the surface with its close, session and business day, in force',
          eastern_row is not None and eastern_row['close'] == {'session': 'synthetic_east', 'businessDate': DAY, 'inForce': True}
          and eastern_row['revision'] == fitted_east['revision']['id'] and eastern_row['product'] == PRODUCT
          # The working snapshot's location lists its newest revision, the refit after the corrections, as a frame pinning them reads it.
          and any(row['closeId'] is None and row['revision'] == latest['revision']['id'] and not row['here'] for row in listed)
          and eastern_row['here'] is False
          and all(row['product'] == PRODUCT for row in listed) and all(row['businessDate'] <= DAY for row in listed), listed)
    first = library(c, at_east['id'])['surfaces'][0]
    check('at the close itself, its own surface is marked here and listed first', (first['closeId'], first['here'], first['revision']) == (east, True, fitted_east['revision']['id']), first)
    whole = library(c, at_west['id'])
    check('the whole library lists every product\'s surfaces, newest business date first',
          {row['product'] for row in whole['surfaces']} >= {PRODUCT, UNFITTED} and [row['businessDate'] for row in whole['surfaces']] == sorted((row['businessDate'] for row in whole['surfaces']), reverse=True)
          and whole['canFit'] is True and whole['hasMore'] is False, whole)
    check('a library read names a known model', c.call('vol-surfaces/library', {'context': {'snapshotId': at_west['id'], 'convention': OPTION, 'knowledgeMode': 'current'},
                                                                              'model': 'smile_unknown_v1', 'offset': 0}, expected=422)['code'] == 'vol_model_unknown')
    # A recording names a close only with inputs the close pins: a working fit cannot be attributed to a close at its instant.
    def attributed(inputs):
        located = json.dumps({'referenceCatalogId': manifest['referenceCatalogId'], 'referenceRevisionId': manifest['referenceRevisionId'], 'code': PRODUCT})
        pinned = 'ARRAY[' + ','.join(f"'{o}'" for o in inputs) + ']::uuid[]'
        return query(f"BEGIN;DO $check$ BEGIN PERFORM trading.record_vol_surface_revision('{catalog}','{trader}','{uuid4()}','{located}'::jsonb,'{DAY}','{MODEL}',"
                     f"'{SOLVER_POLICY}','{uuid4()}','{west}','{frame['at']}',{pinned},'{{\"synthetic\":\"attribution\",\"sensitivities\":{{}}}}'::jsonb,'{'0' * 64}');"
                     "RAISE EXCEPTION 'recorded';EXCEPTION WHEN SQLSTATE 'P0001' THEN PERFORM set_config('check.outcome',SQLERRM,true);END $check$;"
                     "SELECT current_setting('check.outcome');ROLLBACK;").splitlines()[-1]
    inside = query(f"SELECT observation_id FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}','{west}') ORDER BY observation_id LIMIT 1;")
    outside = query(f"SELECT o.id FROM market_data.observations o WHERE o.catalog_id='{catalog}' AND NOT EXISTS(SELECT 1 FROM market_data.market_close_pins p "
                    f"WHERE(p.catalog_id,p.close_id,p.observation_id)=(o.catalog_id,'{west}',o.id)) ORDER BY o.id LIMIT 1;")
    check('a recording at a close is refused for an input the close does not pin, and admitted for one it does',
          attributed([outside]) == 'vol_surface_input' and attributed([inside]) == 'recorded', (attributed([outside]), attributed([inside])))
    # Discover the fitted close in the new session while it is still in force.
    # The same session then proves replacement preserves that retained history.
    fresh_scenario()
    at_east, at_west = close_frame(c, east), close_frame(c, west)
    replacement = record_close(query, manifest, 'synthetic_east', 1, frame)
    at_replacement = close_frame(c, replacement)
    replaced = library(c, at_replacement['id'], PRODUCT)['surfaces']
    check('the close that replaces it starts without a surface, and the library names the replaced one as history',
          read(snapshot=at_replacement['id'])['state'] == 'notFitted'
          and next(row for row in replaced if row['closeId'] == east)['close']['inForce'] is False, replaced)
    # A replaced close is history (D334): a read of it still names its own surface, which no other close shows.
    history = read(snapshot=at_east['id'])
    check('the replaced close still reads its own surface as history', history['state'] == 'fitted' and history['revision']['id'] == fitted_east['revision']['id'], history['state'])
    refit_replaced = fit(snapshot=at_east['id'], expected=422)
    check('a calibration at the replaced close is new work and refused', refit_replaced['code'] == 'close_replaced', refit_replaced)

    # The same reference product can have a second exact definition source.
    # Keep the original close inputs and publish the option role on the new part.
    second_definition = str(uuid4())
    original_definition = manifest['revisionId']
    copies = []
    for table in ('revision_periods', 'contracts', 'curves', 'series', 'curve_members',
                  'rate_curves', 'rate_curve_tenors'):
        copies.append(f"""
        INSERT INTO market_data.{table}
        SELECT (jsonb_populate_record(NULL::market_data.{table},
            to_jsonb(r)||jsonb_build_object('revision_id','{second_definition}'))).*
        FROM market_data.{table} r WHERE(r.catalog_id,r.revision_id)=({M});""")
    query(f"""
    BEGIN;
    INSERT INTO market_data.revisions VALUES('{catalog}','{second_definition}',{R},'draft',0);
    {''.join(copies)}
    UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{second_definition}');
    INSERT INTO market_data.observations
    SELECT (jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
        'id',gen_random_uuid(),'definition_revision_id','{second_definition}',
        'source_event_id','source_copy:'||o.id::text,'received_at',clock_timestamp()))).*
    FROM market_data.market_close_pins p JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
    WHERE(p.catalog_id,p.close_id)=('{catalog}','{west}');
    WITH publication AS (
        INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id()
        FROM trading.publications WHERE catalog_id='{catalog}' RETURNING publication_order
    )
    INSERT INTO trading.currency_discount_defaults
    SELECT (jsonb_populate_record(NULL::trading.currency_discount_defaults,to_jsonb(d)||jsonb_build_object(
        'id',gen_random_uuid(),'definition_revision_id','{second_definition}','publication_order',p.publication_order))).*
    FROM trading.currency_discount_defaults d CROSS JOIN publication p
    WHERE(d.catalog_id,d.definition_revision_id,d.currency_code)=('{catalog}','{original_definition}','usd');
    COMMIT;
    """)
    copied_pins = json.loads(query(f"SELECT jsonb_agg(id ORDER BY id) FROM market_data.observations "
                                   f"WHERE(catalog_id,definition_revision_id)=('{catalog}','{second_definition}');"))
    mixed = record_close(query, manifest, 'synthetic_west', 1, frame,
                         retained=[(second_definition, {'observationIds': copied_pins, 'curveContracts': {}})])
    c.call('profile', {'profile': 'approver'})
    prior = c.call('catalogue')
    original = {k: prior[k] for k in ('effectiveFrom', 'effectiveTo', 'curves', 'roles')}
    role_name = 'reference:futures_option_products'
    added = {**original, 'roles': {**original['roles'], role_name: {
        **original['roles'][role_name], 'definitionRevisionId': second_definition}}}

    def publish_choices(definition, expected):
        return c.call('catalogue/publish', {'requestId': str(uuid4()), 'expectedHead': expected, 'definition': definition})

    changed = publish_choices(added, prior['id'])
    try:
        c.call('profile', {'profile': 'trader'})
        held_primary = read(snapshot=at_east['id'], definition=original_definition)
        check('an exact held product reads its earlier source after the catalogue changes',
              held_primary['definitionRevisionId'] == original_definition
              and held_primary['revision']['id'] == fitted_east['revision']['id'], held_primary['state'])
        held_catalogue = c.call(f"reference/catalog?table=futures_option_products&code={PRODUCT}"
                                f"&snapshotId={at_replacement['id']}&definitionRevisionId={original_definition}")
        check('exact held product metadata follows the named definition',
              held_catalogue['definitionRevisionId'] == original_definition and held_catalogue['rows'][0]['code'] == PRODUCT)
        absent = c.call(f"reference/catalog?table=futures_option_products&code={PRODUCT}"
                        f"&snapshotId={at_replacement['id']}&definitionRevisionId={second_definition}", expected=422)
        check('an exact directory read requires the source at the selected close', absent['code'] == 'market_inputs_missing')
        at_mixed = close_frame(c, mixed)
        secondary = fit(snapshot=at_mixed['id'])
        run_worker()
        fitted_secondary = read(snapshot=at_mixed['id'])
        check('the secondary-source worker completes', fitted_secondary['state'] == 'fitted', job(secondary['jobId']))
        stored_id = fitted_secondary['revision']['id']
        source = query(f"SELECT definition_revision_id FROM trading.vol_surface_revisions WHERE id='{stored_id}';")
        stored_pins = json.loads(query(f"SELECT jsonb_agg(observation_id) FROM trading.vol_surface_inputs WHERE revision_id='{stored_id}';"))
        library_head = library(c, at_mixed['id'], PRODUCT)['surfaces'][0]
        check('a mixed close fits, stores and reads the published secondary option source',
              source == second_definition and fitted_secondary['state'] == 'fitted'
              and fitted_secondary['job']['id'] == secondary['jobId']
              and stored_pins and set(stored_pins) <= set(copied_pins)
              and (library_head['closeId'], library_head['revision'], library_head['here']) == (mixed, stored_id, True),
              fitted_secondary['job'])
    finally:
        c.call('profile', {'profile': 'approver'})
        publish_choices(original, changed['id'])
        c.call('profile', {'profile': 'trader'})

    # The fitter loses PublishMarketData after submitting and before the worker runs; this also restores the trader's own roles.
    revoked = fit(product=UNFITTED)
    query(f"UPDATE security.role_grants SET revoked=true WHERE id='{grant}';")
    run_worker()
    after = read(product=UNFITTED)
    check('revoked fitter\'s job records nothing', after['state'] == 'notFitted' and after['canFit'] is False and revisions(UNFITTED) == 0
          and after['job'] == {'id': revoked['jobId'], 'state': 'failed', 'errorCode': 'risk_job_authority_changed'}, after)


def grant_browser_fitter(query, manifest):
    """Gives the trader PublishMarketData again, through the synthetic fitter role run_checks created, for the Vol surfaces
    browser checks, which fit as the trader in a risk worker the preview serves for them."""
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    query(f"""INSERT INTO security.role_grants SELECT gen_random_uuid(),m.principal_id,r.id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false
    FROM security.workspace_memberships m,security.roles r
    WHERE (m.tenant_id,m.workspace_id,m.login_handle)=('{tenant}','{workspace}','trader') AND r.code='synthetic_surface_fitter';""")
