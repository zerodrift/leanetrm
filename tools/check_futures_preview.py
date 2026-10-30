"""Futures booking/holdings checks in the owned synthetic preview only."""
from preview_fixture import trade_booking
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4


def run_checks(Client, check):
    convention = 'contract_lot_execution_pnl_v2'
    trader = Client(); trader.bootstrap(); trader.call('profile', {'profile': 'trader'})
    source = trader.call('workspace')
    frame = next(f for f in source['frames'] if f['at'].startswith('2020-12-18'))
    view = {'snapshotId': frame['id'], 'convention': convention, 'knowledgeMode': 'current'}
    trader.call('futures/catalog', {**view, 'convention': 'fixed_total_cash_execution_pnl_v1'}, expected=422)
    catalog = trader.call('futures/catalog', view)
    check('futures catalog resolves actual curve members and two scoped books', len(catalog['books']) == 2 and len(catalog['contracts']) == 6)
    contract = next(c for c in catalog['contracts'] if c['deliveryFrom'] == '2021-01-01')
    check('booking terms include explicit fixed lot size, units, quote and trading interval', contract['contractSize'] == '40' and contract['lotBasis'] == 'fixed_total' and contract['settlementMethod'] == 'cash')
    check('booking exposes exact contract series quote and independently specified price rules',
          contract['contract']['id'] == contract['id'] and contract['series']['code'] and contract['quote']['code']
          and contract['priceUnit']['quantity'] == contract['lotQuantity']
          and contract['priceUnit']['multiplier'] == contract['quoteMultiplier']
          and contract['clearingIncrement'] and contract['minimumPrice'] and contract['maximumPrice'])
    check('futures selector metadata comes from linked commodity exchange and product references',
          all(c['commodity'] and c['commodityName'] and c['exchange'] and c['exchangeName'] and c['product'] for c in catalog['contracts']))
    baseline = trader.call('futures/holdings', view)
    check('empty holdings are explicit without a fabricated zero portfolio value', baseline['holdings'] == [] and baseline['totals'] == [])
    context = trader.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})['context']
    trader.call('market/current', {})
    request = {'operationId': str(uuid4()), **view, **trade_booking(), 'bookId': catalog['books'][0]['id'], 'curve': contract['curve'],
               'contractId': contract['id'], 'lots': '2', 'executionPrice': '2.9'}
    for field, value in [('contractSize', '80'), ('settlementMethod', 'physical'), ('quoteMultiplier', '100')]:
        trader.call('futures/book', {**request, field: value}, expected=400)
        check('booking cannot override shared contract term ' + field)
    first = trader.call('futures/book', request)
    check('booking retry returns the retained original result', trader.call('futures/book', request) == first)
    trader.call('futures/book', {**request, 'lots': '3'}, expected=409)
    check('changed economics cannot reuse an operation identity')
    second = trader.call('futures/book', {**request, 'operationId': str(uuid4()), 'lots': '1', 'executionPrice': '2.95'})
    third = trader.call('futures/book', {**request, 'operationId': str(uuid4()), 'bookId': catalog['books'][1]['id'], 'lots': '-1', 'executionPrice': '3.1'})
    rows = trader.call('futures/holdings', view)
    check('multiple books share one exact contract and pricing binding', len(rows['holdings']) == 3 and len({r['instrumentId'] for r in rows['holdings']}) == 1 and len({r['pricingBindingId'] for r in rows['holdings']}) == 1 and len({r['contractId'] for r in rows['holdings']}) == 1)
    check('trade prices and quantities stay independent with one evaluated market contract', sorted(r['executionPnl'] for r in rows['holdings']) == ['2', '4', '8'] and rows['evaluatedContracts'] == 1)
    check('execution P&L remains distinct from unprocessed settlement', all(r['settlement'] == 'unprocessed' for r in rows['holdings']) and sorted(r['executionPnl'] for r in rows['totals']) == ['10', '4'])
    check('current operation status survives another browser session', first == trader.call('futures/status', {'operationId': request['operationId']}))
    other = Client(); other.bootstrap(); other.call('profile', {'profile': 'trader'})
    other_frame = next(f for f in other.call('workspace')['frames'] if f['at'] == frame['at'])
    check('committed bookings are shared by authorized sessions', len(other.call('futures/holdings', {**view, 'snapshotId': other_frame['id']})['holdings']) == 3)
    for patch, code in [({'lots': '0'}, 'futures_lots'), ({'lots': '0.5'}, 'futures_lots'),
                        ({'executionPrice': '3.001'}, 'futures_execution'), ({'convention': ''}, 'futures_convention'),
                        ({'bookId': str(uuid4())}, 'futures_book')]:
        error = trader.call('futures/book', {**request, 'operationId': str(uuid4()), **patch}, expected=422)
        check('invalid futures booking rejects ' + code, error['code'] == code)
    viewer = Client(); viewer.bootstrap(); viewer.call('profile', {'profile': 'viewer'})
    viewer_frame = next(f for f in viewer.call('workspace')['frames'] if f['at'] == frame['at'])
    viewer.call('futures/catalog', {**view, 'snapshotId': viewer_frame['id']}, expected=403)
    viewer.call('futures/holdings', {**view, 'snapshotId': viewer_frame['id']}, expected=403)
    viewer.call('futures/book', {**request, 'snapshotId': viewer_frame['id']}, expected=403)
    viewer.call('futures/status', {'operationId': request['operationId']}, expected=403)
    check('market read authority alone cannot reveal or book trades or operation status')
    trader.call('futures/book', {**request, 'operationId': str(uuid4())}, headers={'X-Preview-CSRF': 'invalid'}, expected=400)
    check('booking uses the common CSRF boundary')
    trader.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': context['knownAt'], 'mode': 'at', 'selection': 'latest_correction'})
    historical = next(f for f in trader.call('workspace')['frames'] if f['at'] == frame['at'] and f.get('contextId'))
    check('a pre-booking certified cutoff cannot see later backdated trades', trader.call('futures/holdings', {**view, 'snapshotId': historical['id'], 'knowledgeMode': 'retained', 'contextId': context['contextId']})['holdings'] == [])
    trader.call('futures/book', {**request, 'snapshotId': historical['id']}, expected=422)
    check('historical business views remain read only')
    trader.call('market/current', {})
    concurrent = {**request, 'operationId': str(uuid4()), 'lots': '1', 'executionPrice': '3'}
    def book_once(_):
        c = Client(); c.bootstrap(); c.call('profile', {'profile': 'trader'})
        f = next(f for f in c.call('workspace')['frames'] if f['at'] == frame['at'])
        return c.call('futures/book', {**concurrent, 'snapshotId': f['id']})
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(book_once, range(2)))
    check('concurrent identical requests create one obligation', outcomes[0] == outcomes[1] and len(trader.call('futures/holdings', view)['holdings']) == 4)
    admin = Client(); admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    member = admin.call('admin/create', {'operationId': str(uuid4()), 'login': 'futures_other', 'name': 'Synthetic futures trader', 'kind': 'human', 'roles': ['trader']})
    other = admin
    admin = Client(); admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    other.call('profile', {'profile': 'futures_other'})
    other_frame = next(f for f in other.call('workspace')['frames'] if f['at'] == frame['at'])
    check('operation reconciliation does not disclose another actor outcome', other.call('futures/status', {'operationId': request['operationId']}).get('found') is False)
    other.call('futures/book', {**request, 'snapshotId': other_frame['id']}, expected=409)
    check('operation ownership cannot be transferred to another authorized actor')
    disabled = admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'], 'enabled': False, 'roles': ['trader']})
    other.call('futures/holdings', {**view, 'snapshotId': other_frame['id']}, expected=403)
    other.call('futures/status', {'operationId': request['operationId']}, expected=403)
    check('current revocation blocks trading reads and current request reconciliation')
    admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': disabled['version'], 'name': member['name'], 'enabled': True, 'roles': ['trader']})
    check('restored trading authority sees the unchanged committed obligations', len(other.call('futures/holdings', {**view, 'snapshotId': other_frame['id']})['holdings']) == 4)

    trade_view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    report=lambda selected:trader.call('futures/blotter',{'context':view,'view':selected})
    check('blotter reads individual trades with explicit portfolio and exact context',len(report(trade_view)['holdings'])==4 and all(r['portfolioId'] for r in report(trade_view)['holdings']))
    a=report({**trade_view,'bookIds':[catalog['books'][0]['id']]})
    check('book selection scopes trade rows and backend book totals',len(a['holdings'])==3 and len(a['totals'])==1 and a['totals'][0]['executionPnl']=='10')
    pivot={**trade_view,'mode':'pivot','groupBy':['book','contract'],'measures':['tradeCount','lots','quantity','executionPnl'],'pivotConvention':'futures_blotter_totals_v1'}
    result=report(pivot)
    check('pivot reconciles to exact trade totals without averaging execution prices',len(result['groups'])==2 and sorted(r['quantity'] for r in result['groups'])==['-40','160'] and sorted(r['executionPnl'] for r in result['groups'])==['10','4'])
    for patch in [{'groupBy':[]},{'groupBy':['book','book']},{'groupBy':['book'],'measures':['lots']},{'measures':['averagePrice']},{'bookIds':[str(uuid4())]},{'portfolioIds':[str(uuid4())]},{'pivotConvention':None}]:
        error=trader.call('futures/blotter',{'context':view,'view':{**pivot,**patch}},expected=422)
        check('ambiguous unsupported or unavailable blotter choices reject',error['code']=='blotter_view')
    viewer.call('futures/blotter',{'context':{**view,'snapshotId':viewer_frame['id']},'view':trade_view},expected=403)
    check('saved layout and report schema cannot confer trading read authority')
    trader.call('market/context',{'effectiveOn':'2020-12-18','knownAt':context['knownAt'],'mode':'at','selection':'latest_correction'})
    history_frame=next(f for f in trader.call('workspace')['frames'] if f['at']==frame['at'] and f.get('contextId'))
    history=trader.call('futures/blotter',{'context':{**view,'snapshotId':history_frame['id'],'knowledgeMode':'retained','contextId':context['contextId']},'view':pivot})
    check('pivot preserves the historical booking prefix rather than using current holdings',history['holdings']==[] and history['groups']==[])
    trader.call('market/current',{})
