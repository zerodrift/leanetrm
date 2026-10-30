"""Independent dated-observation risk and retained-worker cash oracles."""
from fractions import Fraction as F
from uuid import uuid4

from check_blotter_valuation_status import risk_catalog, risk_request
from preview_fixture import CAT, identity


def run(c, context, saved, partial_saved, exact_saved, query, run_worker, settlement=None):
    c.call('profile', {'profile': 'risk'})
    frames = c.call('workspace')['frames']
    trades = {saved['tradeId'], partial_saved['tradeId'], exact_saved['tradeId']}

    def report(at, days, correction=0):
        frame = next(f for f in frames if f['at'].startswith(at))
        selected = {**context, 'snapshotId': frame['id'], 'convention': 'trading_workspace_v1'}
        catalog = risk_catalog(c, selected)
        excluded = {r['tradeId']: r['reason'] for r in catalog['excludedPositions']}
        assert not trades.intersection(excluded), excluded
        factors = [f for f in catalog['factors'] if 'future' in f['curves'] and f['deliveryFrom'] in ('2021-02-01', '2021-03-01')]
        if not any(days.values()):
            factors = catalog['availableFactors'][:1]
        assert factors and (not any(days.values()) or len(factors) == 2), factors
        pinned = {**selected, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
        request = risk_request(catalog, pinned, factors[0]['factor'], size='0.1')
        request['bumps'] = [dict(factor=f['factor'], size='0.1', convention=catalog['bumpConvention']) for f in factors]
        value = c.call('portfolio/risk', request)
        for item in value['factors']:
            expected = F(days[item['factor']['deliveryFrom']]) if any(days.values()) else F(0)
            for trade in (saved['tradeId'], partial_saved['tradeId']):
                p = next(p for p in item['positions'] if p['tradeId'] == trade)['values']
                assert F(p['delta']) == expected and F(p['gamma']) == 0, p
                assert F(p['upPnl']) == expected / 10 and F(p['downPnl']) == -expected / 10, p
                if any(days.values()):
                    fixed_days = 31 - sum(days.values())
                    actual = sum(range(11, 11 + fixed_days)) + (10 if query is not None else 0) + (1 if settlement is not None else 0) + correction
                    current_mark = F(25) if at == '2021-01-15T16:00:00' else F('3.05')
                    forecast = days['2021-02-01'] * current_mark + days['2021-03-01'] * F('3.10')
                    assert F(p['base']) == actual + forecast, p
        return selected, request, value

    # Quantity 31 cancels the 31-observation average: each still-live daily row
    # contributes exactly one unit of exposure. Past samples are constants.
    selected, request, value = report('2021-01-15T08:00:00', {'2021-02-01': 13, '2021-03-01': 4})
    _, equality_request, equality_value = report('2021-01-15T16:00:00', {'2021-02-01': 12, '2021-03-01': 4})
    if run_worker is not None:
        operation = dict(requestId=str(uuid4()), request=request)
        queued = c.call('portfolio/risk/jobs', operation)
        assert c.call('portfolio/risk/jobs', operation)['id'] == queued['id']
        equality_job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=equality_request))
        if query is not None:
            assert query(f"""SELECT EXISTS(SELECT 1 FROM risk.job_curve_samples
                WHERE job_id='{equality_job['id']}' AND kind='contract_observation'
                GROUP BY revision_id,snapshot_id,source_curve,contract_id
                HAVING bool_or(freeze_observation) AND bool_or(NOT freeze_observation));""").strip() == 't'
            from check_contract_observation_risk_sql import run as check_sql
            check_sql(query, queued['id'])
            # A later-known preliminary correction invalidates a current actual,
            # but cannot rewrite the admitted job's certified source observations.
            correction = str(uuid4())
            previous = identity('7', 8700002)
            query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,
                reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,
                role,publication_state,observed_at,received_at,value,corrects_id)
                SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,
                reference_revision_id,source_code,source_event_id,'{correction}',role,'preliminary',
                observed_at,clock_timestamp(),value+7,id FROM market_data.observations
                WHERE catalog_id='{CAT}' AND id='{previous}';""")
            current = risk_catalog(c, selected)
            excluded = {r['tradeId']: r['reason'] for r in current['excludedPositions']}
            assert all(excluded.get(trade) == 'missing_observation' for trade in trades), excluded
            if settlement is not None:
                from check_contract_observation_settlement import check_unavailable_redetermination
                check_unavailable_redetermination(c, settlement)
                cash_context = {**settlement['currentContext'], 'convention': 'trading_workspace_v1'}
                cash_catalog = risk_catalog(c, cash_context)
                cash_excluded = {p['tradeId']: p['reason'] for p in cash_catalog['excludedPositions']}
                assert settlement['paidTradeId'] not in cash_excluded and settlement['carryTradeId'] not in cash_excluded, cash_excluded
        run_worker()
        finished = c.call('portfolio/risk/jobs/' + queued['id'])
        assert finished['state'] == 'completed', finished
        assert finished['result']['factors'] == value['factors']
        assert c.call('portfolio/risk', request)['factors'] == value['factors']
        run_worker()
        equality_finished = c.call('portfolio/risk/jobs/' + equality_job['id'])
        assert equality_finished['state'] == 'completed', equality_finished
        assert equality_finished['result']['factors'] == equality_value['factors']
        if query is not None:
            final = str(uuid4())
            query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,
                reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,
                role,publication_state,observed_at,received_at,value,corrects_id)
                SELECT catalog_id,'{final}',definition_revision_id,series_id,reference_catalog_id,
                reference_revision_id,source_code,source_event_id,'{final}',role,'final',
                observed_at,clock_timestamp(),value,id FROM market_data.observations
                WHERE catalog_id='{CAT}' AND id='{correction}';""")
            _, _, revised = report('2021-01-15T08:00:00', {'2021-02-01': 13, '2021-03-01': 4}, correction=7)
            for original, updated in zip(value['factors'], revised['factors'], strict=True):
                before = next(p['values'] for p in original['positions'] if p['tradeId'] == saved['tradeId'])
                after = next(p['values'] for p in updated['positions'] if p['tradeId'] == saved['tradeId'])
                assert F(after['base']) == F(before['base']) + 7
    _, _, fixed = report('2021-02-01T08:00:00', {'2021-02-01': 0, '2021-03-01': 0})
    if settlement is not None:
        for factor in fixed['factors']:
            for trade, expected in ((settlement['paidTradeId'], settlement['correctedBase']),
                                    (settlement['carryTradeId'], settlement['carryBase'])):
                amount = next(p['values'] for p in factor['positions'] if p['tradeId'] == trade)
                assert F(amount['base']) == F(expected), amount
                assert F(amount['delta']) == F(amount['gamma']) == 0, amount
        paid_context = {**settlement['paidContext'], 'convention': 'trading_workspace_v1'}
        paid_catalog = risk_catalog(c, paid_context)
        paid_request = risk_request(paid_catalog, paid_context, paid_catalog['availableFactors'][0]['factor'])
        paid_value = c.call('portfolio/risk', paid_request)
        for factor in paid_value['factors']:
            amount = next(p['values'] for p in factor['positions'] if p['tradeId'] == settlement['paidTradeId'])
            assert all(F(amount[name]) == 0 for name in ('base', 'up', 'down', 'delta', 'gamma')), amount
    c.call('profile', {'profile': 'trader'})
    print('PASS dated observation risk: historical/current fixing freeze, future-only native exposure, actual-only zero delta, retained job pins and correction replay', flush=True)
