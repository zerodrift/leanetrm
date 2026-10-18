"""Independently check weekday demo holdings in the owner's synthetic preview."""
from datetime import datetime
from decimal import Decimal
from preview_client import PreviewClient
from seed_preview_positions import seed_positions


def run_checks(url, run_id, owned_root):
    passed = 0
    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1; print('PASS seeded preview: '+name, flush=True)
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'trader'})
    workspace = client.call('workspace')
    frames = sorted((f for f in workspace['frames'] if f['id'] in workspace['initialFrameIds']), key=lambda f: f['at'])
    check('all sample market dates are weekdays', all(datetime.fromisoformat(f['at']).weekday() < 5 for f in frames))
    check('the standard example contains December 15 through 18 only', [f['at'][:10] for f in frames] == ['2020-12-15','2020-12-16','2020-12-17','2020-12-18'])
    view = {'snapshotId': frames[0]['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'}
    earlier = client.call('futures/holdings', view)['context']
    first = seed_positions(url, run_id, owned_root)
    second = seed_positions(url, run_id, owned_root)
    ids = {r['tradeId'] for r in first}
    check('repeated seeding returns the same eight committed trades', first == second and len(ids) == 8)
    oracle = [('2','1.6','3.2'),('4','2.6','10.4'),('6','2.6','13.6'),('8','6','2')]
    for frame, (count, a, b) in zip(frames, oracle, strict=True):
        rows = [r for r in client.call('futures/holdings', {**view, 'snapshotId': frame['id']})['holdings'] if r['tradeId'] in ids]
        totals = {book: sum((Decimal(r['executionPnl']) for r in rows if r['book'] == book), Decimal(0)) for book in ['Book A','Book B']}
        check(frame['at'][:10]+' shows its independently priced cumulative weekday trades', len(rows) == int(count) and totals == {'Book A':Decimal(a),'Book B':Decimal(b)})
        check(frame['at'][:10]+' preserves two books and one shared dated contract', {r['book'] for r in rows} == {'Book A','Book B'} and len({r['instrumentId'] for r in rows}) == 1 and len({r['pricingBindingId'] for r in rows}) == 1)
    historical = client.call('futures/holdings', {**view, 'knowledgeMode':'retained', 'contextId':earlier['contextId']})
    check('new backdated demo trades do not leak into an earlier knowledge receipt', not any(r['tradeId'] in ids for r in historical['holdings']))
    check('all demo economic effective dates are weekdays and settlement remains unprocessed', all(datetime.fromisoformat(r['economicEffectiveAt']).weekday() < 5 and r['settlement'] == 'unprocessed' for r in rows))
    print(f'{passed} seeded preview checks passed.', flush=True)
