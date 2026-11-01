"""Independent mixed-portfolio crude risk oracles in an owned disposable database."""
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, receipt, *, run_worker):
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', {'profile': 'trader'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    context = dict(snapshotId=frame['id'], convention='trading_workspace_v1', knowledgeMode='current')
    positions = c.call('trading/positions', dict(context=context, bookIds=[], portfolioIds=[], offset=0, limit=64))
    crude = next(p for p in positions['positions'] if p['tradeId'] == receipt['tradeId'])
    book = crude['bookId']

    future_context = {**context, 'convention': 'contract_lot_execution_pnl_v2'}
    futures = c.call('futures/catalog', future_context)
    contract = next(k for k in futures['contracts'] if k['curve'] == 'future')
    future = c.call('futures/book', {**future_context, **trade_booking(), 'operationId': str(uuid4()),
        'bookId': book, 'curve': contract['curve'], 'contractId': contract['id'], 'lots': '1', 'executionPrice': contract['mark']})

    gas_context = {**context, 'convention': 'physical_gas_fixed_quantity_v4'}
    gas_catalog = c.call('gas/catalog', gas_context)
    location = next(p for p in gas_catalog['locations'] if p['deliveryPoint'] == 'synthetic_gas_point_a')
    cash = lambda code, role, price: {'code': code, 'role': role, 'direction': 'pay', 'source': 'physical', 'stage': 'delivery',
        'price': price, 'tariffCode': None, 'paymentAt': '2021-02-01T00:00:00Z'}
    gas = c.call('gas/composition/book', {**gas_context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': book,
        'gasSetupId': gas_catalog['gasSetupId'], 'definitionRevisionId': location['definitionRevisionId'],
        'discounting': False, 'profile': 'fixed_gas_composition_v1',
        'physicalSides': [{'code': 'physical', 'pipeline': location['pipeline'], 'zone': location['zone'], 'deliveryPoint': location['deliveryPoint'],
            'product': location['product'], 'direction': 'receive', 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01',
            'quantityBasis': 'period_total_equal_days', 'quantity': '1000', 'link': None}],
        'cashSides': [cash('cash', 'consideration', '3'), cash('fee', 'fee', '0.1')]})

    view = dict(mode='trades', bookIds=[book], portfolioIds=[], groupBy=[], measures=[], pivotConvention=None)
    blotter = c.call('trading/blotter', dict(context=context, view=view))
    held = {row['tradeId']: row for row in blotter['holdings']}
    assert {receipt['tradeId'], future['tradeId'], gas['tradeId']} <= held.keys()
    assert Fraction(held[receipt['tradeId']]['presentValue']) == 10100
    assert Fraction(held[gas['tradeId']]['presentValue']) == 100
    assert Fraction(held[future['tradeId']]['executionPnl']) == 0
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': blotter['context']['contextId']}

    c.call('profile', {'profile': 'risk'})
    catalog = c.call('portfolio/risk/catalog', dict(context=retained, bookIds=[book], portfolioIds=[]))
    assert any(x['tradeId'] == gas['tradeId'] and x['reason'] == 'physical_gas_risk_unavailable' for x in catalog['excludedPositions'])
    assert not any(x['tradeId'] == receipt['tradeId'] for x in catalog['excludedPositions'])
    crude_factor = next(f for f in catalog['factors'] if 'synthetic_crude_forward' in f['curves'])
    future_factor = next(f for f in catalog['factors'] if 'future' in f['curves'])
    request = dict(context=retained, bookIds=[book], portfolioIds=[], bumps=[
        dict(factor=crude_factor['factor'], size='1', convention=catalog['bumpConvention']),
        dict(factor=future_factor['factor'], size='1', convention=catalog['bumpConvention'])],
        convention=catalog['convention'], numericalPolicy=catalog['numericalPolicy'], pricingPolicy=catalog['pricingPolicy'], stress=[])
    report = c.call('portfolio/risk', request)
    crude_risk = next(p for p in report['factors'][0]['positions'] if p['tradeId'] == receipt['tradeId'])
    assert crude_risk['measure'] == 'crude_contractual_present_value'
    assert {key: Fraction(crude_risk['values'][key]) for key in ('base', 'up', 'down', 'delta', 'gamma')} == {
        'base': 10100, 'up': 11100, 'down': 9100, 'delta': 1000, 'gamma': 0}
    untouched_future = next(p for p in report['factors'][0]['positions'] if p['tradeId'] == future['tradeId'])
    untouched_crude = next(p for p in report['factors'][1]['positions'] if p['tradeId'] == receipt['tradeId'])
    assert Fraction(untouched_future['values']['upPnl']) == Fraction(untouched_crude['values']['upPnl']) == 0
    assert not any(p['tradeId'] == gas['tradeId'] for factor in report['factors'] for p in factor['positions'])
    stressed = c.call('portfolio/risk', {**request, 'stress': [dict(factor=crude_factor['factor'], size='2', convention=catalog['stressConvention'])]})
    stressed_crude = next(p for p in stressed['factors'][0]['positions'] if p['tradeId'] == receipt['tradeId'])
    assert Fraction(stressed_crude['values']['base']) == 12100
    assert c.call('portfolio/risk', request)['factors'] == report['factors'], 'Stress must not mutate the base market'

    job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request))
    run_worker()
    completed = c.call('portfolio/risk/jobs/' + job['id'])
    assert completed['state'] == 'completed', completed
    assert completed['result']['factors'] == report['factors']
    assert completed['result']['excludedPositions'] == report['excludedPositions']
    print('PASS mixed crude/futures/gas: exact retained PV, independent shocks and stress, explicit gas exclusions, queued replay parity', flush=True)
