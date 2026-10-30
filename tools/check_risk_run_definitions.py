"""Saved risk run definitions against a disposable synthetic preview."""
import json
from uuid import uuid4
from preview_client import PreviewClient, initial_frame


def client(url, profile):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': profile}); return c


def account(url, login, roles, session=None):
    """A session of a new synthetic account, created by an administrator: a new session, or the given one switched to them."""
    if session is None:
        session = client(url, 'admin')
    else:
        session.call('profile', {'profile': 'admin'})
    session.call('admin/create', dict(operationId=str(uuid4()), login=login, name='Synthetic ' + login.replace('_', ' '), kind='human', roles=roles))
    session.call('profile', {'profile': login})
    return session


def run_checks(url, query, browsers):
    """browsers: two sessions of the preview, which these checks switch to their own users."""
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)
    # The second browser of the account later becomes the other account, then the trader.
    risk, other_browser = browsers
    for session in browsers:
        session.call('profile', {'profile': 'risk'})

    frame = next(f for f in risk.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    catalog = risk.call('futures/catalog', {'snapshotId': frame['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'})
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

    other_account = account(url, 'synthetic_risk_colleague', ['risk_analyst'], other_browser)
    check('another account sees none of these definitions', other_account.call('risk/definitions') == [])
    save(other_account, 409, definitionId=saved['definitionId'], expectedVersion=2)

    trader = other_account
    trader.call('profile', {'profile': 'trader'})
    save(risk, 403, bookIds=[str(uuid4())])
    save(trader, 403)
    trader.call('risk/definitions', expected=403)
    check('a book the account cannot trade-read, or an account without RunScenario, is refused')

    print(f'{passed} risk run definition checks passed.', flush=True)


def revoke_trade_reads(query, login):
    """Replace an account's grants with a synthetic role holding the same permissions minus ReadTrades."""
    principal = query(f"SELECT principal_id FROM security.identity_bindings WHERE subject='{login}';")
    role = str(uuid4())
    query(f"""BEGIN;
        INSERT INTO security.roles VALUES('{role}','synthetic_risk_without_trades_{login}','Synthetic risk analyst without trade reads ({login})');
        INSERT INTO security.role_permissions SELECT DISTINCT '{role}'::uuid,p.permission FROM security.role_permissions p
         JOIN security.role_grants g ON g.role_id=p.role_id WHERE g.principal_id='{principal}' AND NOT g.revoked AND p.permission<>'ReadTrades';
        INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{role}',tenant_id,workspace_id,clock_timestamp(),granted_to,false
         FROM security.role_grants WHERE principal_id='{principal}' AND NOT revoked LIMIT 1;
        UPDATE security.role_grants SET revoked=true WHERE principal_id='{principal}' AND role_id<>'{role}';
        COMMIT;""")


def run_now_checks(url, query, run_worker):
    """Run now of saved definitions and their latest completed runs, against the risk fixture's seeded futures and swap holdings."""
    passed = 0
    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)
    def contexts(session):
        """A session's current context of the fixture's 2020-12-18 snapshot, the context it retains and the books holding positions."""
        blotter = {'snapshotId': initial_frame(session.call('workspace'), '2020-12-18')['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
        current = session.call('portfolio/risk/catalog', {'context': blotter, 'bookIds': [], 'portfolioIds': []})
        return blotter, {**blotter, 'knowledgeMode': 'retained', 'contextId': current['context']['contextId']}, sorted(b['bookId'] for b in current['books'])
    risk = client(url, 'risk')
    blotter, retained, books = contexts(risk)
    held = risk.call('portfolio/risk/catalog', {'context': retained, 'bookIds': books, 'portfolioIds': []})
    targets = {t['id']: t for t in held['scenarioTargets']}
    units = {f['unit']: f['priceUnit'] for f in held['factors'] + held['availableFactors']}
    labels = sorted({targets[t]['unit'] for t in held['heldContractTargets']})
    amounts = {label: f'0.{index + 1}' for index, label in enumerate(labels)}
    size = lambda label, amount: dict(unit=units[label], size=amount)
    def save(session, preset, sizes):
        return session.call('risk/definitions', dict(requestId=str(uuid4()), definitionId=None, expectedVersion=None, name='Held shift',
                                                     bookIds=books, preset=preset, sizes=sizes))
    def run(session, definition, status=200, context=retained, **terms):
        body = dict(requestId=str(uuid4()), version=definition['version'], context=context)
        return session.call(f"risk/definitions/{definition['definitionId']}/run", {**body, **terms}, expected=status)
    jobs = lambda: int(query('SELECT count(*) FROM risk.jobs;'))
    assert len(held['heldContractTargets']) > 16 and len(labels) > 1, 'The risk fixture holds more than sixteen contracts in more than one price unit'

    shocked = save(risk, 'held_contracts', [size(label, amounts[label]) for label in labels])
    queued = jobs()
    request_id = str(uuid4())
    first = run(risk, shocked, requestId=request_id)
    bumps = [bump for job in first['jobs'] for bump in json.loads(query(f"SELECT request->'bumps' FROM risk.jobs WHERE id='{job['jobId']}';"))]
    check('run now queues the held targets of the definition\'s books at the retained context',
          bumps == [dict(factor=targets[t]['factor'], size=amounts[targets[t]['unit']], convention=held['bumpConvention']) for t in held['heldContractTargets']]
          and first['definitionId'] == shocked['definitionId'] and first['version'] == shocked['version']
          and query(f"SELECT definition_id||'|'||version||'|'||context_id FROM risk.definition_runs WHERE run_id='{first['runId']}';")
          == f"{shocked['definitionId']}|{shocked['version']}|{retained['contextId']}")
    parts = [risk.call('portfolio/risk/jobs/' + job['jobId']) for job in first['jobs']]
    queued += len(parts)
    check('parts hold at most sixteen bumps and share one run id',
          [p['shocks'] for p in parts] == [len(bumps[i:i + 16]) for i in range(0, len(bumps), 16)]
          and [p['run'] for p in parts] == [dict(id=first['runId'], part=i + 1, parts=len(parts)) for i in range(len(parts))]
          and [job['part'] for job in first['jobs']] == list(range(1, len(parts) + 1)) and jobs() == queued)
    other = save(risk, 'held_curves', [size(label, '0.1') for label in labels])
    retried = run(risk, shocked, requestId=request_id)
    check('a retried run returns its first outcome; its request id for another run is a conflict',
          retried['runId'] == first['runId'] and retried['jobs'] == first['jobs'] and jobs() == queued
          and run(risk, other, 409, requestId=request_id)['code'] == 'risk_definition_request_conflict')

    # Sizes are saved against the exact price unit a catalog target states; Run now resolves them by the same unit, so a definition
    # saved from the targets' units alone is sized for everything the preset shocks.
    by_target = [targets[t]['priceUnit'] for t in held['heldContractTargets']]
    check('every catalog target states its exact price unit, the same as the factor it shocks',
          all('priceUnit' in t for t in held['scenarioTargets'])
          and all(units[t['unit']] == t['priceUnit'] for t in held['scenarioTargets'] if t['unit'] in units)
          and run(risk, save(risk, 'held_contracts', [dict(unit=u, size='0.1') for u in {json.dumps(u, sort_keys=True): u for u in by_target}.values()]))['jobs'])
    queued = jobs()

    refusal = run(risk, save(risk, 'held_contracts', [size(labels[0], '0.1')]), 422)
    check('a held price unit without a size is refused and queues nothing',
          refusal['code'] == 'risk_definition_size' and all(label in refusal['message'] for label in labels[1:]) and jobs() == queued)
    check('a non-retained context is refused', run(risk, shocked, 422, context=blotter)['code'] == 'risk_job_context' and jobs() == queued)

    # An account runs its definition, then loses trade reads while keeping every other permission: its books are no longer readable.
    runner = account(url, 'synthetic_definition_runner', ['risk_analyst'])
    definition = save(runner, 'held_curves', [size(label, '0.1') for label in labels])
    _, runner_context, _ = contexts(runner)
    before = run(runner, definition, context=runner_context)
    queued = jobs()
    revoke_trade_reads(query, 'synthetic_definition_runner')
    check('a definition book the account can no longer read refuses the run',
          before['jobs'] and run(runner, definition, 403, context=runner_context)['code'] == 'access_denied' and jobs() == queued
          and query(f"SELECT count(*) FROM risk.definition_runs WHERE definition_id='{definition['definitionId']}';") == '1')
    print(f'{passed} risk run definition run-now checks passed.', flush=True)

    # The latest completed run of a definition, at runs valued at the fixture's successive closes.
    passed = 0
    def drain():
        """Run the worker until no part of a definition's run is queued or running; it claims older work first."""
        for _ in range(60):
            if query("SELECT count(*) FROM risk.definition_run_jobs p JOIN risk.jobs j ON j.id=p.job_id WHERE j.state IN('queued','running');") == '0':
                return
            run_worker()
        raise AssertionError('Definition runs did not finish')
    def at(session, day):
        """A session's current context of the fixture's snapshot of one day, and the context that read retains."""
        current = {'snapshotId': initial_frame(session.call('workspace'), day)['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
        read = session.call('portfolio/risk/catalog', {'context': current, 'bookIds': [], 'portfolioIds': []})
        return current, {**current, 'knowledgeMode': 'retained', 'contextId': read['context']['contextId']}
    def latest(session, definition, context, status=200):
        return session.call(f"risk/definitions/{definition['definitionId']}/latest", {'context': context}, expected=status)
    def ran(session, definition, day):
        return run(session, definition, context=at(session, day)[1])['runId']
    def valued(day):
        return initial_frame(risk.call('workspace'), day)['at']

    # The access change above failed every part queued before it, the first run's included.
    rerun = run(risk, shocked, context=at(risk, '2020-12-18')[1]); drain()
    shown = latest(risk, shocked, at(risk, '2020-12-18')[0])
    parts = [risk.call('portfolio/risk/jobs/' + job['jobId']) for job in rerun['jobs']]
    check('the latest completed run at or before the effective time is chosen, its parts read in order as job reports',
          len(parts) > 1 and shown['state'] == 'latest' and shown['run']['runId'] == rerun['runId'] and shown['run']['version'] == shocked['version']
          and shown['run']['parts'] == parts and [p['run']['part'] for p in parts] == list(range(1, len(parts) + 1))
          and all(p['state'] == 'completed' and p['result']['factors'] for p in parts)
          and shown['newer'] is None and shown['incomplete'] is None and first['runId'] not in json.dumps(shown)
          and latest(risk, shocked, retained)['state'] == 'none')

    daily = save(risk, 'held_curves', [size(label, '0.1') for label in labels])
    on15 = ran(risk, daily, '2020-12-15'); drain()
    _, before16 = at(risk, '2020-12-16'); _, before17 = at(risk, '2020-12-17')
    on16 = ran(risk, daily, '2020-12-16'); drain()
    past = latest(risk, daily, before16)
    check('a run known after the knowledge cutoff is not visible',
          past['state'] == 'latest' and past['run']['runId'] == on15 and past['run']['valuationAt'] == valued('2020-12-15')
          and latest(risk, daily, at(risk, '2020-12-16')[0])['run']['runId'] == on16)
    earlier = latest(risk, daily, at(risk, '2020-12-15')[0])
    hidden = latest(risk, daily, before17)
    check('a newer completed run beyond the selected effective time is stated',
          earlier['state'] == 'newer_available' and earlier['run']['runId'] == on15
          and earlier['newer'] == dict(runId=on16, valuationAt=valued('2020-12-16'))
          and hidden['state'] == 'latest' and hidden['run']['runId'] == on15 and hidden['newer'] is None and on16 not in json.dumps(hidden))

    on17 = ran(risk, daily, '2020-12-17')
    _, while_queued = at(risk, '2020-12-17')
    queued_now = latest(risk, daily, at(risk, '2020-12-17')[0])
    drain()
    queued_then = latest(risk, daily, while_queued)
    check('a queued run is never shown in a past context',
          queued_now['run']['runId'] == on16 and queued_now['incomplete'] is None and on17 not in json.dumps(queued_now)
          and queued_then['run']['runId'] == on16 and on17 not in json.dumps(queued_then)
          and latest(risk, daily, at(risk, '2020-12-17')[0])['run']['runId'] == on17)

    cancelled = run(risk, daily, context=at(risk, '2020-12-17')[1])
    risk.call(f"portfolio/risk/jobs/{cancelled['jobs'][0]['jobId']}/cancel", {})
    drain()
    after_cancel = latest(risk, daily, at(risk, '2020-12-17')[0])
    check('a run with a failed or cancelled part is not completed',
          after_cancel['run']['runId'] == on17
          and after_cancel['incomplete'] == dict(runId=cancelled['runId'], valuationAt=valued('2020-12-17'), failedParts=[1]))

    again = ran(risk, daily, '2020-12-17'); drain()
    tie = latest(risk, daily, at(risk, '2020-12-17')[0], 409)
    check('two completed runs at the same valuation time are refused',
          tie['code'] == 'risk_run_tie' and tie['runs'] == [dict(runId=r, valuationAt=valued('2020-12-17')) for r in (on17, again)])

    reader = account(url, 'synthetic_definition_reader', ['risk_analyst'])
    readable = save(reader, 'held_curves', [size(label, '0.1') for label in labels])
    ran(reader, readable, '2020-12-18'); drain()
    current = at(reader, '2020-12-18')[0]
    seen = latest(reader, readable, current)
    revoke_trade_reads(query, 'synthetic_definition_reader')
    check('lost book access refuses the read',
          seen['state'] == 'latest' and latest(reader, readable, current, 403)['code'] == 'access_denied')
    print(f'{passed} latest risk run checks passed.', flush=True)
