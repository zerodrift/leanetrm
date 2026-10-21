"""Provider Test over HTTP against the loopback fake provider: the one request a test sends, its outcomes, the
deadline and an abort, retries, losing the agent worker, a permission lost mid-test, and egress; owned synthetic
preview only. Runs first among the provider test checks, while the agent worker the API started still holds the lock."""
import json
import os
import signal
import stat
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request
from uuid import uuid4
from assistant_context import holding
from check_model_qualification import command as provider_command, terms
from check_model_versions import capabilities
from check_provider_tests import HOLDERS, KEY, RATES, Worker, released
from fake_model_provider import USAGE, event, recording, status, stream
from preview_client import PreviewClient

TEST = 'settings/ai/providers/test'
CLOSE = 'assistant.close_test(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text)'
MODEL = 'model-synthetic-http'
INSTRUCTION = "This is a connectivity test from OpenETRM's page assistant settings. It carries no workspace data."
PROMPT = 'Reply with the single word: ready.'
UNKNOWN = {'count': None, 'source': 'unknown'}
DIMENSIONS = ('uncachedInput', 'cacheRead', 'cacheWrite', 'output', 'reasoning')


def run_checks(url, query, psql, manifest, environment, log, fake, trap):
    passed = 0
    responses = []

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    def session(profile='admin'):
        client = PreviewClient(url)
        client.bootstrap()
        client.call('profile', {'profile': profile})
        return client

    admin = session()

    def provider():
        return next(p for p in admin.call('settings/ai/providers')['providers'] if p['vendor'] == 'anthropic')

    # A known synthetic key, so the checks can look for it wherever it must not be.
    keyed = admin.call('settings/ai/providers/key', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': provider()['version'], 'key': KEY})
    recorded = admin.call('settings/ai/providers/terms', terms('anthropic', keyed['version']))
    admin.call('settings/ai/providers/attest', provider_command('anthropic', recorded['version']))
    efforts = provider()['mapping']['efforts']

    def add_model(identifier, efforts, price=True):
        added = admin.call('settings/ai/models/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': identifier,
                                                      'displayName': 'Synthetic HTTP model'})
        admin.call('settings/ai/models/edit', capabilities(added['id'], 1, reasoningEfforts=efforts, acceptedServed=[f'{identifier}-served']))
        if price:
            query(f"INSERT INTO assistant.price_revisions VALUES('{added['id']}',1,'rates','{manifest['referenceCatalogId']}',"
                  f"'{manifest['referenceRevisionId']}','usd',{RATES},NULL,NULL,NULL,NULL,NULL,'2020-01-01',NULL,"
                  f"(SELECT principal_id FROM security.identity_bindings WHERE subject='admin'),clock_timestamp());")
        return added['id']

    model = add_model(MODEL, efforts)
    low_only = add_model('model-synthetic-http-low', ['low'])
    unpriced = add_model('model-synthetic-http-unpriced', ['low'], price=False)

    def test(reasoning='low', operation=None, client=None, expected=200, target=None):
        body = {'operationId': operation or str(uuid4()), 'modelId': target or model, 'reasoning': reasoning}
        response = (client or admin).call(TEST, body, expected=expected)
        responses.append(json.dumps(response))
        return response

    def sent():
        return len(fake.received)

    def generation():
        return int(query('SELECT generation FROM assistant.worker_generation;'))

    def logged(line, since):
        """Whether the API logs `line` past the first `since` characters of its log, which its logger writes shortly."""
        deadline = time.monotonic() + 5
        while line not in log.read_text(errors='replace')[since:]:
            if time.monotonic() > deadline:
                return False
            time.sleep(.05)
        return True

    def mark():
        return len(log.read_text(errors='replace'))

    before, requests = generation(), sent()
    check('before serving, the API took the agent-worker lock and started a generation',
          before > 0 and query('SELECT worker_pid FROM assistant.worker_generation;') == query(f'SELECT pid FROM({HOLDERS})h;'))
    fake.respond(stream(recording(MODEL)))
    first = test()
    check("a test runs under the generation the API's worker started", generation() == before and first['outcome'] == 'succeeded')
    request = fake.received[-1]
    headers = dict(request['headers'])
    check("a test sends exactly one request, to the check endpoint's Messages path, with the key only in its own header",
          sent() == requests + 1 and (request['method'], request['path']) == ('POST', '/v1/messages')
          and sorted(headers) == ['anthropic-version', 'content-length', 'content-type', 'host', 'x-api-key']
          and headers['x-api-key'] == KEY and headers['anthropic-version'] == '2023-06-01' and KEY[-4:].encode() not in request['body'])
    check('the request is the fixed synthetic prompt without workspace data or tools, within the test output limit and at the chosen effort',
          json.loads(request['body']) == {'model': MODEL, 'max_tokens': 256, 'stream': True, 'system': [{'type': 'text', 'text': INSTRUCTION}],
                                          'messages': [{'role': 'user', 'content': [{'type': 'text', 'text': PROMPT}]}],
                                          'thinking': {'type': 'adaptive', 'display': 'omitted'}, 'output_config': {'effort': 'low'},
                                          'service_tier': 'standard_only', 'inference_geo': 'global'})
    check('a well-formed response that ends normally with recognized usage succeeds, and its record shows usage and an exact cost',
          (first['served'], first['usage']['status'], first['cost']['status'], first['cost']['amount']) == (MODEL, 'recognized', 'priced', '0.000114744')
          and [first['usage'][d] for d in DIMENSIONS]
          == [{'count': 21, 'source': 'reported'}, {'count': 0, 'source': 'reported'}, {'count': 0, 'source': 'reported'},
              {'count': 9, 'source': 'reported'}, {'count': 0, 'source': 'reported'}]
          and first['maxOutputTokens'] == 256 and first['reasoning'] == 'low' and first['identifier'] == MODEL)
    requests = sent()
    check('a retry of a finished test returns its outcome and sends nothing', test(operation=first['operationId']) == first and sent() == requests)
    listed = {p['vendor']: p for p in admin.call('settings/ai/providers')['providers']}
    check("the Providers tab lists each shipped adapter's endpoint, here the check endpoint marked as one, and each provider's last test",
          (listed['anthropic']['endpoint'], listed['anthropic']['lastTest']) == ({'url': f'{fake.url}/v1/messages', 'check': True},
                                                                                 first | {'underCurrentAttestation': True})
          and all((p['endpoint'], p['lastTest']) == (None, None) for vendor, p in listed.items() if vendor != 'anthropic'))

    def outcome(response, **expected):
        fake.respond(response)
        result = test()
        return all(result[key] == value for key, value in expected.items()) and result

    limited = outcome(stream(recording(MODEL, stop='max_tokens')), outcome='succeeded')
    accepted = outcome(stream(recording(f'{MODEL}-served')), outcome='succeeded', served=f'{MODEL}-served')
    check('a response that ends at the output limit, or is served by an identifier the version accepts, succeeds', limited and accepted)
    refused = outcome(stream(recording(MODEL, stop='refusal', text=None)), outcome='model_refused')
    check('a refusal before any output fails the test with unrecognized usage and no cost',
          refused and (refused['usage']['status'], refused['cost']['amount']) == ('unrecognized', None))
    mismatch = outcome(stream(recording('model-synthetic-other')), outcome='model_mismatch', served='model-synthetic-other')
    check('a served identifier the version neither requests nor accepts fails the test as a mismatch with unrecognized usage',
          mismatch and mismatch['usage']['status'] == 'unrecognized')
    unrecognized = outcome(stream(recording(MODEL, usage={'input_tokens': 21, 'cache_creation_input_tokens': 0, 'cache_read_input_tokens': 0,
                                                          'output_tokens': 9, 'service_tier': 'priority'})), outcome='usage_unrecognized')
    check('a response whose usage cannot be read as the mapping documents fails the test, storing no cost',
          unrecognized and unrecognized['cost']['status'] == 'usage_unrecognized')
    errors = {401: 'model_provider_authentication', 429: 'model_provider_rate_limited', 529: 'model_provider_overloaded', 500: 'model_provider_overloaded'}
    check('provider errors fail the test with their classification and unknown usage',
          all((r := outcome(status(code, {'type': 'error', 'error': {'type': 'synthetic_error', 'message': 'Synthetic ' + KEY[-4:]}}), outcome=expected))
              and all(r['usage'][d] == UNKNOWN for d in DIMENSIONS) for code, expected in errors.items()))
    def unknown(result):
        return result and all(result['usage'][d] == UNKNOWN for d in DIMENSIONS) and result['cost']['status'] == 'usage_unknown'

    check('a request the provider refuses as too long, or otherwise invalid, fails the test with that class and unknown usage',
          unknown(outcome(status(400, {'type': 'error', 'error': {'type': 'invalid_request_error', 'message': 'prompt is too long: 9 tokens > 1 maximum'}}),
                          outcome='context_window_exceeded'))
          and unknown(outcome(status(400, {'type': 'error', 'error': {'type': 'invalid_request_error', 'message': 'Synthetic invalid request'}}),
                              outcome='model_provider_invalid_request')))
    check('a response that stops at the context window fails the test with that class, billed as reported',
          (r := outcome(stream(recording(MODEL, stop='model_context_window_exceeded')), outcome='context_window_exceeded'))
          and r['cost']['amount'] == '0.000114744')
    tool = (event('message_start', {'type': 'message_start', 'message': {'id': 'msg_synthetic_test', 'type': 'message', 'role': 'assistant', 'model': MODEL,
                                                                     'content': [], 'stop_reason': None, 'stop_sequence': None,
                                                                     'usage': {'input_tokens': 21, 'cache_creation_input_tokens': 0,
                                                                               'cache_read_input_tokens': 0, 'output_tokens': 1}}})
            + event('content_block_start', {'type': 'content_block_start', 'index': 0,
                                            'content_block': {'type': 'tool_use', 'id': 'toolu_synthetic', 'name': 'read_trades', 'input': {}}})
            + event('content_block_stop', {'type': 'content_block_stop', 'index': 0})
            + event('message_delta', {'type': 'message_delta', 'delta': {'stop_reason': 'tool_use', 'stop_sequence': None}, 'usage': {**USAGE}})
            + event('message_stop', {'type': 'message_stop'}))
    check('a request for a tool the test never offered fails the test as a transport failure, billed as reported',
          (r := outcome(stream(tool), outcome='model_provider_transport')) and r['cost']['amount'] == '0.000114744')
    check('a malformed served identifier is a mismatch whose identifier is not kept, with unrecognized usage',
          (r := outcome(stream(recording('bad identifier!')), outcome='model_mismatch', served=None)) and r['usage']['status'] == 'unrecognized'
          and r['cost']['amount'] is None)
    check('a stream the mapping cannot read fails the test as a transport failure with unknown usage',
          unknown(outcome(stream('event: message_start\ndata: {"type":"message_start",\n\n'), outcome='model_provider_transport', served=None)))
    check('a response past the byte bound fails the test as too large, with unknown usage',
          unknown(outcome(stream(':' + 'x' * (4 << 20) + '\n\n'), outcome='model_provider_response_too_large')))
    check('a redirect is not followed: the test fails with unknown usage and its target is never reached',
          unknown(outcome(status(307, headers=(('Location', trap.url + '/v1/messages'),)), outcome='model_provider_transport'))
          and trap.connections == 0)

    for effort in efforts:
        fake.respond(stream(recording(MODEL)))
        result = test(reasoning=effort)
        check(f"a test at the stated effort '{effort}' passes the adapter's own effort check and sends it",
              result['outcome'] == 'succeeded' and json.loads(fake.received[-1]['body'])['output_config'] == {'effort': effort})

    requests = sent()
    viewer = session('viewer')
    refusals = [test(reasoning='medium', target=low_only, expected=422), test(target=unpriced, expected=409), test(target=str(uuid4()), expected=409),
                test(reasoning=None, expected=422), viewer.call(TEST, {'operationId': str(uuid4()), 'modelId': model, 'reasoning': 'low'}, expected=403),
                admin.call(TEST, {'operationId': str(uuid4()), 'modelId': model, 'reasoning': 'low', 'temperature': 1}, expected=400),
                test(operation='00000000-0000-0000-0000-000000000000', expected=422), test(target='00000000-0000-0000-0000-000000000000', expected=422),
                test(reasoning='lo\u0000w', expected=422)]
    check('a test the database or the boundary refuses sends nothing, with its documented code',
          [r and r.get('code') for r in refusals]
          == ['parameter_required', 'model_unpriced', 'model_admin_conflict', 'parameter_required', 'access_denied', 'request_format', 'model_admin_input',
              'model_admin_input', 'model_admin_input']
          and sent() == requests)

    key_file = Path(environment['OPENETRM_PREVIEW_CREDENTIAL_KEY'])
    mode, calls = stat.S_IMODE(key_file.stat().st_mode), query('SELECT count(*) FROM assistant.model_calls;')
    key_file.chmod(0o644)
    try:
        unopened = test(expected=422)
    finally:
        key_file.chmod(mode)
    check("a test whose key the preview's key file cannot open is refused before anything is recorded or sent",
          unopened['code'] == 'credential_unavailable' and sent() == requests and query('SELECT count(*) FROM assistant.model_calls;') == calls)

    def finished(operation, limit=15):
        """The outcome a retry returns once the test is closed."""
        client, deadline = session(), time.monotonic() + limit
        while True:
            try:
                return client.call(TEST, {'operationId': operation, 'modelId': model, 'reasoning': 'low'})
            except AssertionError as error:
                assert 'test_in_progress' in str(error) and time.monotonic() < deadline, error
                time.sleep(.2)

    def started():
        deadline = time.monotonic() + 5
        while sent() == requests:
            assert time.monotonic() < deadline, 'The test never reached the fake provider'
            time.sleep(.02)

    opening = event('message_start', {'type': 'message_start', 'message': {'id': 'msg_synthetic_test', 'type': 'message', 'role': 'assistant',
                                                                           'model': MODEL, 'content': [], 'stop_reason': None, 'stop_sequence': None,
                                                                           'usage': {'input_tokens': 21, 'cache_creation_input_tokens': 0,
                                                                                     'cache_read_input_tokens': 0, 'output_tokens': 1}}})
    rest = recording(MODEL).split('event: content_block_start', 1)[1]
    rest = 'event: content_block_start' + rest

    # A stalled provider: the test closes at its deadline, and a retry meanwhile is in progress. A second test in the
    # same session waits for the first, so the request deadline cuts it short: that is its elapsed limit too, not an
    # abort. No retry sends again.
    release, operation, queued_operation, requests = threading.Event(), str(uuid4()), str(uuid4()), sent()
    fake.respond(stream(opening, release, rest), stream(opening, release, rest))
    with ThreadPoolExecutor(2) as pool:
        began = time.monotonic()
        running = pool.submit(test, operation=operation)
        started()
        queued = pool.submit(test, operation=queued_operation, expected=503)
        retry = session()
        in_progress = retry.call(TEST, {'operationId': operation, 'modelId': model, 'reasoning': 'low'}, expected=409)
        stalled = running.result(timeout=30)
        took = time.monotonic() - began
        cut = queued.result(timeout=30)
        release.set()
    check('a retry while a test runs is in progress and sends nothing', in_progress['code'] == 'test_in_progress')
    check('a stalled test closes at its deadline with unknown usage and no cost',
          stalled['outcome'] == 'limit_elapsed' and all(stalled['usage'][d] == UNKNOWN for d in DIMENSIONS)
          and stalled['cost']['status'] == 'usage_unknown' and 9 < took < 15)
    check("a test the request deadline cuts short records its elapsed limit, not an abort, and its answer is unknown until a retry",
          cut['code'] == 'model_admin_outcome_unknown' and finished(queued_operation)['outcome'] == 'limit_elapsed')
    check('a retry of the stalled test returns its outcome and never sends a second request',
          retry.call(TEST, {'operationId': operation, 'modelId': model, 'reasoning': 'low'}) == stalled and sent() == requests + 2)

    # A close the database cannot take at once is retried until it lands: the answer is in progress meanwhile, and a
    # retry then returns the outcome with the usage the handler read.
    release, operation, requests = threading.Event(), str(uuid4()), sent()
    fake.respond(stream(opening, release, rest))
    with ThreadPoolExecutor(1) as pool:
        running = pool.submit(test, operation=operation, expected=409)
        started()
        call = query(f"SELECT id FROM assistant.model_calls WHERE operation_id='{operation}';")

        def held():
            release.set()
            return running.result(timeout=30)
        delayed = holding(psql, environment, f"SELECT id FROM assistant.model_calls WHERE id='{call}' FOR UPDATE", held)
    landed = finished(operation)
    check('a close the database cannot take at once is retried until it lands, keeping the usage the handler read',
          delayed['code'] == 'test_in_progress' and landed['outcome'] == 'succeeded' and landed['cost']['amount'] == '0.000114744')

    # An administrator who leaves while the test runs: the test closes cancelled, and a retry returns that.
    release, operation, requests = threading.Event(), str(uuid4()), sent()
    fake.respond(stream(opening, release, rest))
    body = json.dumps({'operationId': operation, 'modelId': model, 'reasoning': 'low'}).encode()
    leaving = Request(f'{url}/api/{TEST}', data=body, method='POST',
                      headers={'Origin': url, 'Content-Type': 'application/json', 'X-Preview-CSRF': admin.token})
    try:
        admin.opener.open(leaving, timeout=2)
        raise AssertionError('The aborted test answered before its client left')
    except HTTPError:
        raise
    except (TimeoutError, URLError):
        pass
    # The provider stays stalled until the test is closed, so only the abort can have closed it.
    deadline = time.monotonic() + 8
    while query(f"SELECT count(*) FROM assistant.model_call_outcomes o JOIN assistant.model_calls c ON c.id=o.call_id WHERE c.operation_id='{operation}';") != '1':
        assert time.monotonic() < deadline, 'The aborted test was not closed'
        time.sleep(.1)
    release.set()
    answer = session().call(TEST, {'operationId': operation, 'modelId': model, 'reasoning': 'low'})
    check('a test whose request is aborted closes as cancelled with unknown usage, and its retry returns that',
          answer['outcome'] == 'request_cancelled' and all(answer['usage'][d] == UNKNOWN for d in DIMENSIONS) and sent() == requests + 1)

    # An administrator who loses the permission while the test runs: its usage is still recorded, and the answer,
    # which is read under the administrator's current authority, is refused.
    tester = session()
    tester.call('admin/create', {'operationId': str(uuid4()), 'login': 'provider_http_tester', 'name': 'Synthetic HTTP tester', 'kind': 'human',
                                 'roles': ['access_administrator']})
    tester.call('profile', {'profile': 'provider_http_tester'})
    tester_id = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='provider_http_tester';")
    release, operation, requests = threading.Event(), str(uuid4()), sent()
    fake.respond(stream(opening, release, rest))
    with ThreadPoolExecutor(1) as pool:
        running = pool.submit(test, operation=operation, client=tester, expected=403)
        started()
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp() WHERE principal_id='{tester_id}';")
        release.set()
        denied = running.result(timeout=30)
    stored = json.loads(query(f"SELECT assistant.test_state(id)::text FROM assistant.model_calls WHERE operation_id='{operation}';"))
    check('a test whose administrator loses the permission while it runs still records its outcome and usage, and the answer is refused',
          denied['code'] == 'access_denied' and (stored['outcome'], stored['cost']['amount']) == ('succeeded', '0.000114744'))

    # No request runs a command on the worker's connection, which only holds the lock and listens: with its backend
    # stopped, a test still runs under the generation, and the worker keeps its lock.
    holder = int(query(f'SELECT pid FROM({HOLDERS})h;'))
    fake.respond(stream(recording(MODEL)))
    os.kill(holder, signal.SIGSTOP)
    try:
        outcome = test()['outcome']
    finally:
        os.kill(holder, signal.SIGCONT)
    check("no request waits for the worker's connection: with its backend stopped, a test still runs under the generation, and the "
          "worker keeps its lock", outcome == 'succeeded' and int(query(f'SELECT pid FROM({HOLDERS})h;')) == holder)

    # A close that fails other than transiently is logged, and the answer is what a retry returns: the test stays in
    # progress until the next worker start, below, closes it.
    unclosed, requests, since = str(uuid4()), sent(), mark()
    fake.respond(stream(recording(MODEL)))
    query(f'REVOKE EXECUTE ON FUNCTION {CLOSE} FROM openetrm_runtime;')
    try:
        stuck = test(operation=unclosed, expected=409)
    finally:
        query(f'GRANT EXECUTE ON FUNCTION {CLOSE} TO openetrm_runtime;')
    check('a close that fails other than transiently is logged, and the test stays in progress',
          stuck['code'] == 'test_in_progress' and logged('A provider test could not be closed: PostgresException', since)
          and test(operation=unclosed, expected=409)['code'] == 'test_in_progress' and sent() == requests + 1)

    # Another session takes the agent-worker lock while a test runs, as another preview process would: the test is
    # closed as interrupted, the API refuses tests while it holds the lock, and acquires it again once it ends.
    release, operation, requests = threading.Event(), str(uuid4()), sent()
    fake.respond(stream(opening, release, rest))
    with ThreadPoolExecutor(1) as pool:
        running = pool.submit(test, operation=operation)
        started()
        worker = Worker(psql, environment, query)
        release.set()
        interrupted = running.result(timeout=30)
    check("a worker that takes over closes the running test as interrupted, and the original handler's late close changes nothing",
          interrupted['outcome'] == 'interrupted' and all(interrupted['usage'][d] == UNKNOWN for d in DIMENSIONS) and sent() == requests + 1)
    try:
        check('its start also closes the test whose close failed as interrupted', test(operation=unclosed)['outcome'] == 'interrupted')
        requests = sent()
        check('while another session holds the agent-worker lock, tests are refused and send nothing',
              test(expected=503)['code'] == 'agent_runtime_unavailable' and sent() == requests)
        check("meanwhile a retry of a finished test still returns its outcome, and a viewer is refused for want of permission",
              test(operation=first['operationId']) == first and viewer.call(TEST, {'operationId': str(uuid4()), 'modelId': model, 'reasoning': 'low'},
                                                                         expected=403)['code'] == 'access_denied')
        # A worker start that fails after taking the lock leaves no lock behind, so a later request can start one.
        query('REVOKE EXECUTE ON FUNCTION assistant.start_worker() FROM openetrm_runtime;')
    finally:
        worker.stop()
    try:
        since = mark()
        failed = test(expected=503)
        replayed = test(operation=first['operationId'])
        released(query)
    finally:
        query('GRANT EXECUTE ON FUNCTION assistant.start_worker() TO openetrm_runtime;')
    check('a worker start that fails is logged, refuses the test and releases the lock it took, sending nothing, yet a retry still replays',
          failed['code'] == 'agent_runtime_unavailable' and logged('The agent worker could not start: PostgresException', since)
          and replayed == first and sent() == requests)
    # The lock is free and a start would now succeed, so only the order of the checks keeps a viewer from starting one.
    unstarted = generation()
    viewer.call(TEST, {'operationId': str(uuid4()), 'modelId': model, 'reasoning': 'low'}, expected=403)
    check("a viewer's refused request takes no lock and starts no generation",
          generation() == unstarted and query(f'SELECT count(*) FROM({HOLDERS})h;') == '0')
    fake.respond(stream(recording(MODEL)))
    check('once that session ends, the next test takes the lock again', test()['outcome'] == 'succeeded')

    # A release whose adapter sends fewer efforts than the database states for its mapping: the test is refused before
    # anything is recorded or sent. The extra stated effort is the owner's, for this check only, and is removed after.
    def stated(change):
        query('BEGIN;ALTER TABLE assistant.mapping_efforts DISABLE TRIGGER immutable_mapping_efforts;'
              f"UPDATE assistant.mapping_efforts SET efforts={change} WHERE(vendor,mapping)=('anthropic','messages_v1');"
              'ALTER TABLE assistant.mapping_efforts ENABLE TRIGGER immutable_mapping_efforts;COMMIT;')
    stated("efforts||'synthetic'::text")
    try:
        drifted, requests = add_model('model-synthetic-http-drift', ['synthetic']), sent()
        refused = test(reasoning='synthetic', target=drifted, expected=422)
    finally:
        stated("array_remove(efforts,'synthetic')")
    check("an effort the database states but the adapter does not send is refused before anything is recorded or sent",
          refused['code'] == 'parameter_required' and sent() == requests
          and query(f"SELECT count(*) FROM assistant.model_calls WHERE model_id='{drifted}';") == '0'
          and provider()['mapping']['efforts'] == efforts)
    # The model takes a stated effort again, so no later check meets an effort its mapping no longer states.
    admin.call('settings/ai/models/edit', capabilities(drifted, 2, reasoningEfforts=['low'], acceptedServed=['model-synthetic-http-drift-served']))

    time.sleep(.2)
    check('no request ever reached a proxy named in the environment or a redirect target', trap.connections == 0)
    check('the key appears in no answer, no other request part and no log line',
          not any(KEY[-4:] in r for r in responses) and not any(KEY[-4:].encode() in r['body'] for r in fake.received)
          and KEY[-4:] not in log.read_text(errors='replace'))
    print(f'{passed} provider test HTTP checks passed.', flush=True)
    return passed
