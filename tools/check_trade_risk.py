"""Selected-trade scope checks for the owned product fixtures; no production target."""
from uuid import uuid4
from fractions import Fraction
from decimal import Decimal


def selected_scope(client, request, trade, whole, *, run_worker=None, bindings=None, timeout=60):
    catalog = client.call('portfolio/risk/catalog', {key: request[key] for key in ('context', 'bookIds', 'portfolioIds')} | {'tradeIds': [trade]}, timeout=timeout)
    factor = catalog['factors'][0]
    selected = {**request, 'tradeIds': [trade], 'pricingPolicy': catalog['pricingPolicy'],
                'bumps': [{'factor': factor['factor'], 'size': request['bumps'][0]['size'], 'convention': catalog['bumpConvention']}]}
    report = client.call('portfolio/risk', selected, timeout=timeout)
    expected = {p['bindingId']: p['values']['base'] for p in whole['factors'][0]['positions'] if p['tradeId'] == trade}
    actual = report['factors'][0]['positions']
    assert report['tradeIds'] == [trade] and {p['tradeId'] for p in report['inputs']['positions']} == {trade}
    assert actual and {p['bindingId']: p['values']['base'] for p in actual} == expected
    if bindings is not None:
        assert {p['bindingId'] for p in actual} == set(bindings), 'Selecting a trade includes every instrument binding'
    if run_worker is not None:
        job = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': selected}, timeout=timeout)
        assert job['scope']['tradeIds'] == [trade]
        run_worker()
        done = client.call('portfolio/risk/jobs/' + job['id'], timeout=timeout)
        assert done['state'] == 'completed', done.get('error')
        assert done['scope']['tradeIds'] == [trade] and done['result']['tradeIds'] == [trade]
        assert done['result']['factors'] == report['factors'] and done['result']['excludedPositions'] == report['excludedPositions']
    joint_scope(client, selected, report, run_worker=run_worker, timeout=timeout)
    print('PASS selected trade scope: ' + report['inputs']['positions'][0]['instrumentType'] + ', exact held bindings and base value' + ('; retained worker parity' if run_worker is not None else ''), flush=True)


def joint_scope(client, selected, oracle, *, run_worker=None, timeout=60):
    """Shared named/FD PV oracles exercise every selected product, including nonlinear options."""
    bump = selected['bumps'][0]
    shifts = [{'factor': bump['factor'], 'size': size, 'convention': 'absolute_curve_coordinate_parent_additive_v1'}
              for size in (bump['size'], format(-Decimal(bump['size']), 'f'))]
    zero = {'absolute': '0', 'relative': '0'}
    contracts = [
        {'convention': 'portfolio_joint_risk_v1', 'scenarios': {'id': 'synthetic_product_stress', 'version': '1',
            'convention': 'independent_joint_absolute_curve_scenarios_v1', 'scenarios': [
                {'id': direction, 'label': direction, 'shifts': [shift]} for direction, shift in zip(('up', 'down'), shifts)]}, 'derivatives': None},
        {'convention': 'portfolio_joint_risk_v1', 'scenarios': None, 'derivatives': {
            'convention': 'exact_central_half_nominal_double_v1', 'axes': [
                {'id': 'selected', 'bump': bump, 'deltaTolerance': zero, 'gammaTolerance': zero}], 'mixed': []}}]
    expected = {(p['tradeId'], p['bindingId'], p['measure']): p['values'] for p in oracle['factors'][0]['positions']}
    for contract in contracts:
        request = {**selected, 'bumps': [], 'joint': contract}
        result = client.call('portfolio/risk', request, timeout=timeout)
        assert 'factors' not in result and result['tradeIds'] == selected['tradeIds']
        assert {p['tradeId'] for p in result['inputs']['positions']} == set(selected['tradeIds'])
        for sample in result['joint']['samples']:
            assert len(sample['positions']) == len(expected)
            for p in sample['positions']:
                v = expected[p['tradeId'], p['bindingId'], p['measure']]
                assert Fraction(p['values']['base']) == Fraction(v['base'])
                assert Fraction(p['values']['value']) == Fraction(v[sample['id']])
                assert Fraction(p['values']['loss']) == -Fraction(v[sample['id'] + 'Pnl'])
        for derivative in result['joint']['derivatives']:
            assert len(derivative['positions']) == len(expected)
            field = 'delta' if derivative['formula'] == 'central_first_difference_v1' else 'gamma'
            for p in derivative['positions']:
                assert Fraction(p['values']['nominal']) == Fraction(expected[p['tradeId'], p['bindingId'], p['measure']][field])
                assert isinstance(p['values']['stable'], bool) and p['values']['unit']
        if run_worker is not None:
            job = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': request}, timeout=timeout)
            run_worker()
            done = client.call('portfolio/risk/jobs/' + job['id'], timeout=timeout)
            assert done['state'] == 'completed', done.get('error')
            assert done['scope']['tradeIds'] == selected['tradeIds'] and done['result']['tradeIds'] == selected['tradeIds']
            for key in ('samples', 'derivatives', 'baseTotals'):
                assert done['result']['joint'][key] == result['joint'][key]
    print('PASS joint named/Greek selected-trade PV oracles: ' + oracle['inputs']['positions'][0]['instrumentType'] +
          ('; retained worker parity' if run_worker is not None else ''), flush=True)
