"""Shared scenario checks in the owner's disposable synthetic fixture only."""
from decimal import Decimal
from uuid import uuid4


def run_checks(Client, check):
    risk = Client(); risk.bootstrap(); risk.call('profile', {'profile': 'risk'})
    workspace = risk.call('workspace')
    frame = next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'contract_lot_execution_pnl_v2', 'knowledgeMode': 'current'}
    holdings = risk.call('futures/holdings', context)
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': holdings['context']['contextId']}
    risk_context = {**retained, 'convention': 'trading_workspace_v1'}
    for missing in [{}, {'context': None, 'bookIds': [], 'portfolioIds': []}]:
        failure = risk.call('portfolio/risk/catalog', missing, expected=422)
        check('factor discovery rejects missing market context explicitly', failure['code'] == 'risk_context')
    catalog = risk.call('portfolio/risk/catalog', {'context': risk_context, 'bookIds': [], 'portfolioIds': []})
    held_contracts = {h['contractId'] for h in holdings['holdings']}
    check('factor discovery includes every held future alongside the selected portfolio dependencies',
          held_contracts <= {f['contractId'] for f in catalog['factors']} and len(catalog['availableFactors']) > len(catalog['factors']))
    factor = next(f for f in catalog['factors'] if 'future' in f['curves'] and f['deliveryFrom'] == '2021-01-01')
    request = {'context': risk_context, 'bookIds': [], 'portfolioIds': [], 'bumps': [{'factor': factor['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']}],
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'], 'pricingPolicy': catalog['pricingPolicy'], 'stress': []}
    before = risk.call('evaluate', {'snapshotId': frame['id'], 'curve': 'future'})
    result = risk.call('portfolio/risk', request)
    check('risk pins the same historical context and independent observation receipt', result['context'] == holdings['context'] and result['factors'][0]['factor']['observation'] == factor['observation'])
    check('risk names execution P&L and retains explicit bump and model policies', all(p['measure'] == 'futures_execution_pnl' for p in result['factors'][0]['positions'] if p['instrumentType'] == 'future') and result['factors'][0]['bump'] == '0.1' and any(m['modelVersion'] == 'fixed_total_open_mark_v1' for m in result['modelContracts']))
    summary = risk.call('portfolio/risk', {**request, 'detail': 'books'})
    check('book-only risk reconciles to holding detail without the row matrix',
          summary['detail'] == 'Books' and summary['factors'][0]['positions'] == [] and summary['factors'][0]['totals'] == result['factors'][0]['totals'])
    broad_bumps = [{'factor': f['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']} for f in catalog['availableFactors'][:17]]
    assert len(broad_bumps) == 17
    broad = risk.call('portfolio/risk', {**request, 'detail': 'books', 'bumps': broad_bumps})
    check('book-only API streams a broad factor vector with one shared base',
          len(broad['factors']) == 17 and broad['work']['scenarioCount'] == 35 and all(f['positions'] == [] for f in broad['factors']))
    risk.call('portfolio/risk', {**request, 'detail': 'holdings', 'bumps': broad_bumps}, expected=422)
    risk.call('portfolio/risk', {**request, 'detail': 'unknown'}, expected=422)
    check('risk rejects unknown detail and bounds detailed row matrices')
    terms = next(c for c in risk.call('futures/catalog', retained)['contracts'] if c['id'] == factor['contractId'] and c['curve'] == 'future')
    check('risk retains exact instrument pins for the declared future contract and execution policies',
          any(p['instrumentType'] == 'future' and p['definitionRevisionId'] == terms['contract']['revision']['revisionId'] for p in result['inputs']['positions'])
          and terms['lotBasis'] == 'fixed_total' and terms['settlementMethod'] == 'cash'
          and terms['tick'] and terms['clearingIncrement'] and terms['minimumPrice'] and terms['maximumPrice'])
    rows = {(r['tradeId'], r['bindingId']): r for r in holdings['holdings']}
    for p in result['factors'][0]['positions']:
        if p['instrumentType'] != 'future': continue
        h = rows[(p['tradeId'], p['bindingId'])]
        quantity = Decimal(h['quantity'])
        check('shared shock reconciles each holding to an independent signed quantity oracle',
              Decimal(p['values']['base']) == Decimal(h['executionPnl']) and Decimal(p['values']['upPnl']) == quantity / 10 and Decimal(p['values']['downPnl']) == -quantity / 10 and Decimal(p['values']['delta']) == quantity and p['values']['gamma'] == '0')
    check('shared held instruments reuse unit valuations across repeated future quantities', 3 <= result['work']['unitPricerCalls'] <= 3 * result['work']['uniqueValuations'] and result['work']['scenarioCount'] == 3 and result['work']['repeatedInstrumentReferences'] >= len(holdings['holdings']) - 1)
    check('risk changes no live quote or booked holding', risk.call('evaluate', {'snapshotId': frame['id'], 'curve': 'future'}) == before and risk.call('futures/holdings', retained) == holdings)
    again = risk.call('portfolio/risk', request)
    check('identical pinned risk requests are deterministic without cross-request caches', again == result)
    shift = {'factor': factor['factor'], 'size': '0.2',
             'convention': catalog['stressConvention']}
    outer = risk.call('portfolio/risk', {**request, 'stress': [shift]})
    inner = risk.call('portfolio/risk', {**request, 'stress': [shift, {**shift, 'size': '-0.05'}]})
    check('nested scenario risk inherits the exact shared historical context',
          outer['context'] == result['context'] == inner['context'])
    for original, parent, child in zip(result['factors'][0]['positions'], outer['factors'][0]['positions'], inner['factors'][0]['positions']):
        if original['instrumentType'] != 'future': continue
        quantity = Decimal(rows[(original['tradeId'], original['bindingId'])]['quantity'])
        check('nested additive shocks reprice relative to the stressed parent with independent cash oracle',
              Decimal(parent['values']['base']) == Decimal(original['values']['base']) + quantity * Decimal('0.2')
              and Decimal(child['values']['base']) == Decimal(original['values']['base']) + quantity * Decimal('0.15')
              and child['values']['upPnl'] == original['values']['upPnl'])
    check('child pricing preserves both parent and original market',
          risk.call('portfolio/risk', {**request, 'stress': [shift]}) == outer
          and risk.call('portfolio/risk', request) == result)
    for stress in [[{**shift, 'convention': 'relative'}], [{**shift, 'size': '0'}], [shift] * 17, [None]]:
        risk.call('portfolio/risk', {**request, 'stress': stress}, expected=422)
        check('stress composition rejects unsupported conventions, zero shifts and excessive depth')
    selected = risk.call('portfolio/risk', {**request, 'bookIds': [holdings['holdings'][0]['bookId']]})
    check('risk scope uses selected books before presentation filters', {p['bookId'] for p in selected['factors'][0]['totals']} == {holdings['holdings'][0]['bookId']} and all(p['bookId'] == holdings['holdings'][0]['bookId'] for p in selected['factors'][0]['positions']))
    other = next(f for f in catalog['availableFactors'] if 'power_basis' in f['curves'])
    unrelated = risk.call('portfolio/risk', {**request, 'bumps': [{**request['bumps'][0], 'factor': other['factor']}]})
    check('an unrelated power input retains explicit zero sensitivity for every held future', all(p['values']['delta'] == '0' for p in unrelated['factors'][0]['positions'] if p['instrumentType'] == 'future'))
    for patch in [{'convention': None}, {'numericalPolicy': None}, {'bumps': []}, {'bumps': [None]}, {'bookIds': [str(uuid4())]}, {'bumps': [{**request['bumps'][0], 'size': '0'}]}, {'bumps': [{**request['bumps'][0], 'convention': 'relative'}]}, {'bumps': [{**request['bumps'][0], 'factor': {**factor['factor'], 'coordinate': 'correlation'}}]}, {'bumps': request['bumps'] * 2}]:
        risk.call('portfolio/risk', {**request, **patch}, expected=422)
        check('risk rejects missing ambiguous or unsupported financial choices')
    trader = Client(); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    trader_frame = next(f for f in trader.call('workspace')['frames'] if f['at'] == frame['at'])
    trader_context = {**context, 'snapshotId': trader_frame['id'], 'convention': 'trading_workspace_v1'}
    trader.call('portfolio/risk/catalog', {'context': trader_context, 'bookIds': [], 'portfolioIds': []}, expected=403)
    trader.call('portfolio/risk', {**request, 'context': trader_context}, expected=403)
    check('trading permission does not silently grant scenario execution')
    admin = Client(); admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    member = next(m for m in admin.call('admin')['members'] if m['login'] == 'risk')
    changed = admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'], 'enabled': True, 'roles': ['trader']})
    risk.call('portfolio/risk', request, expected=403)
    check('revoking scenario authority blocks a retained historical risk request')
    admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': changed['version'], 'name': member['name'], 'enabled': True, 'roles': ['risk_analyst']})
    check('restoring scenario authority leaves retained inputs and values unchanged', risk.call('portfolio/risk', request) == result)
