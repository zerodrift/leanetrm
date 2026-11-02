"""Selected-trade scope checks for the owned product fixtures; no production target."""
from uuid import uuid4


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
    print('PASS selected trade scope: ' + report['inputs']['positions'][0]['instrumentType'] + ', exact held bindings and base value' + ('; retained worker parity' if run_worker is not None else ''), flush=True)
