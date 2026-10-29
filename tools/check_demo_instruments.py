"""Independent mixed-instrument acceptance in an owned disposable preview."""
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient
from seed_demo_instruments import seed_heat_rates, seed_ppas
from seed_preview_positions import BOOKS
from seed_preview_ppa import seed_ppa


def run_checks(url, manifest, query, *, run_worker, owned_root, run_id, psql, environment):
    seed_ppa(url, manifest)
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', {'profile': 'trader'})
    ppas = seed_ppas(c, manifest)
    assert len(ppas) == 5 and seed_ppas(c, manifest) == ppas
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    context = dict(snapshotId=frame['id'], convention='trading_workspace_v1', knowledgeMode='current')
    view = dict(mode='trades', bookIds=[BOOKS[1]], portfolioIds=[], groupBy=[], measures=[], pivotConvention=None, priceNumericalPolicy=None)
    only = c.call('trading/workspace', dict(context=context, view=view, bookingCatalog='directory', summaries=[]))
    assert len(only['blotter']['holdings']) == 5 and all(r['instrumentType'] == 'ppa' for r in only['blotter']['holdings'])
    print('PASS PPA-only directory cannot take the empty futures/swap shortcut', flush=True)
    original_context = {**context, 'knowledgeMode': 'retained', 'contextId': only['catalog']['context']['contextId']}
    from seed_preview_positions import seed_positions
    from seed_preview_swaps import seed_swaps
    seed_positions(url, run_id, owned_root)
    seed_swaps(url, run_id, owned_root)
    heat = seed_heat_rates(c)
    assert len(heat) == 8 and seed_heat_rates(c) == heat
    workspace = c.call('trading/workspace', dict(context=context, view=view, bookingCatalog='directory', summaries=[]))
    rows = workspace['blotter']['holdings']
    assert len(rows) == 33 and {r['instrumentType'] for r in rows} == {'future', 'financial_swap', 'heat_rate', 'ppa'}
    print('PASS all eight heat-rate and five PPA bindings appear in the shared blotter', flush=True)
    from check_position_facts import run_checks as check_facts
    check_facts(c,context,view,rows,query,psql,environment,owned_root)
    from check_position_details import run_checks as check_details
    check_details(c,context,view,rows,query,original_context,owned_root)
    assert all((r['executionPnl'] if r['instrumentType'] == 'future' else r['presentValue']) is not None for r in rows)
    ppa_rows = [r for r in rows if r['instrumentType'] == 'ppa']
    assert all(r['measure'] == 'ppa_value_excluding_overdue' and r['pendingActuals'] == 'estimate_until_actual_v1' and r['overdueKnown'] == '0' for r in ppa_rows)
    context.update(knowledgeMode='retained', contextId=workspace['catalog']['context']['contextId'])
    c.call('profile', {'profile': 'risk'})
    earlier = c.call('portfolio/risk/catalog', dict(context=original_context, bookIds=[BOOKS[1]], portfolioIds=[]))
    assert {m['measure'] for m in earlier['measures']} == {'ppa_value_excluding_overdue'}
    print('PASS retained PPA-only context excludes later futures, swaps and heat-rate bookings', flush=True)
    catalog = c.call('portfolio/risk/catalog', dict(context=context, bookIds=[BOOKS[1]], portfolioIds=[]))
    factor = next(f for f in catalog['factors'] if 'future' in f['curves'] and f['deliveryFrom'] == '2021-01-01')
    request = dict(context=context, bookIds=[BOOKS[1]], portfolioIds=[], bumps=[dict(factor=factor['factor'], size='0.1', convention=catalog['bumpConvention'])],
                   convention=catalog['convention'], numericalPolicy=catalog['numericalPolicy'], pricingPolicy=catalog['pricingPolicy'], stress=[])
    report = c.call('portfolio/risk', request)
    positions = report['factors'][0]['positions']
    assert len(positions) == 33 and not report['excludedPositions']
    assert {p['bindingId']: Fraction(p['values']['base']) for p in positions} == {r['bindingId']: Fraction(r['executionPnl'] if r['instrumentType'] == 'future' else r['presentValue']) for r in rows}
    first = next(r for r in rows if r['tradeId'] == heat[0]['tradeId'])
    moved = next(p for p in positions if p['bindingId'] == first['bindingId'])
    assert Fraction(moved['values']['upPnl']) == -700 and Fraction(moved['values']['gamma']) == 0
    assert all(Fraction(p['values']['upPnl']) == 0 for p in positions if p['instrumentType'] == 'ppa')
    assert report['ppaMeasures'] and report['inputs']['ppaReceipt']['pending'] == 'estimate_until_actual_v1'
    print('PASS shared risk values every binding, preserves PPA measures and reprices the explicit heat rate', flush=True)
    job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request))
    run_worker()
    completed = c.call('portfolio/risk/jobs/' + job['id'])
    assert completed['state'] == 'completed', completed
    assert completed['result']['factors'] == report['factors']
    print('PASS retained worker reproduces the mixed report with identical factors and exact values', flush=True)

    from check_ppa_risk_custody import run_checks as custody
    custody(c, query, manifest, request, ppas[0]['tradeId'])
