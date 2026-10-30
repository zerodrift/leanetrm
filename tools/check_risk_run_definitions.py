"""Saved risk run definitions against a disposable synthetic preview."""
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url, query):
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)
    def client(profile):
        c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': profile}); return c
    risk = client('risk'); other_browser = client('risk'); trader = client('trader')
    colleague = dict(operationId=str(uuid4()), login='synthetic_risk_colleague', name='Synthetic risk colleague', kind='human', roles=['risk_analyst'])
    other_account = client('admin')
    other_account.call('admin/create', colleague)
    other_account.call('profile', {'profile': colleague['login']})

    frame = next(f for f in trader.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    catalog = trader.call('futures/catalog', {'snapshotId': frame['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'})
    books = sorted(b['id'] for b in catalog['books'])
    unit = next(c['priceUnit'] for c in catalog['contracts'])
    other_unit = {**unit, 'multiplier': '100'}
    size = lambda u, amount: dict(unit=u, size=amount)
    def save(session, status=200, **terms):
        body = dict(requestId=str(uuid4()), definitionId=None, expectedVersion=None, name='Gas desk shift', bookIds=books,
                    preset='held_curves', sizes=[size(unit, '0.01')])
        return session.call('risk/definitions', {**body, **terms}, expected=status)
    def refused(**terms):
        return save(risk, 422, **terms)['code'] == 'risk_definition'

    check('an account starts without definitions', risk.call('risk/definitions') == [])
    saved = save(risk)
    check('a definition is saved with exact books, preset and sizes',
          saved['version'] == 1 and saved['name'] == 'Gas desk shift' and saved['bookIds'] == books and saved['preset'] == 'held_curves'
          and saved['sizes'] == [dict(unit=unit, size='0.01')] and saved['knownFrom'] and other_browser.call('risk/definitions') == [saved])

    revised = save(risk, definitionId=saved['definitionId'], expectedVersion=1, name='Gas desk shift, wider', preset='held_contracts',
                   bookIds=books[:1], sizes=[size(unit, '0.02'), size(other_unit, '1.5')])
    listed = other_browser.call('risk/definitions')
    check('revising creates version 2 and keeps version 1 readable',
          revised['definitionId'] == saved['definitionId'] and revised['version'] == 2 and revised['preset'] == 'held_contracts'
          and revised['bookIds'] == books[:1] and len(revised['sizes']) == 2 and listed == [revised]
          and query(f"SELECT name||'|'||preset FROM risk.run_definition_versions WHERE definition_id='{saved['definitionId']}' AND version=1;") == 'Gas desk shift|held_curves'
          and query(f"SELECT count(*) FROM risk.run_definition_sizes WHERE definition_id='{saved['definitionId']}' AND version=1;") == '1')
    for statement in (f"UPDATE risk.run_definition_versions SET name='Renamed' WHERE definition_id='{saved['definitionId']}' AND version=1;",
                      f"DELETE FROM risk.run_definition_sizes WHERE definition_id='{saved['definitionId']}' AND version=1;"):
        try:
            query(statement)
        except RuntimeError:
            continue
        raise AssertionError('a stored version changed')
    check('stored versions cannot be updated or deleted')

    request_id = str(uuid4())
    first = save(risk, requestId=request_id, name='Retried')
    retried = save(risk, requestId=request_id, name='Retried')
    other_terms = save(risk, 409, requestId=request_id, name='Retried again')
    check('a retried save returns its first outcome; a reused request id with other terms is a conflict',
          retried == first and other_terms['code'] == 'risk_definition_request_conflict' and len(other_browser.call('risk/definitions')) == 2)

    stale = save(risk, 409, definitionId=saved['definitionId'], expectedVersion=1)
    unexpected = save(risk, 409, definitionId=saved['definitionId'], expectedVersion=3)
    check('a stale expected version is refused', stale['code'] == unexpected['code'] == 'risk_definition_conflict'
          and sorted(d['version'] for d in other_browser.call('risk/definitions')) == [1, 2])

    check('an unknown preset, empty books, duplicate unit or non-positive size is refused',
          all([refused(preset='all_curves'), refused(bookIds=[]), refused(bookIds=[books[0], books[0]]), refused(sizes=[]),
               refused(sizes=[size(unit, '0.01'), size(unit, '0.02')]), refused(sizes=[size(unit, '0')]), refused(sizes=[size(unit, '-1')]),
               refused(sizes=[size(unit, '1e3')]), refused(name=' '), refused(definitionId=saved['definitionId']),
               refused(expectedVersion=1), refused(sizes=[size({**unit, 'multiplier': '0'}, '1')]),
               refused(sizes=[size({**unit, 'currency': {**unit['currency'], 'code': 'no_such_currency'}}, '1')])])
          and len(other_browser.call('risk/definitions')) == 2)

    save(risk, 403, bookIds=[str(uuid4())])
    save(trader, 403)
    check('a book the account cannot trade-read, or an account without RunScenario, is refused')

    check('another account sees none of these definitions', other_account.call('risk/definitions') == []
)
    save(other_account, 409, definitionId=saved['definitionId'], expectedVersion=2)
    trader.call('risk/definitions', expected=403)
    print(f'{passed} risk run definition checks passed.', flush=True)
