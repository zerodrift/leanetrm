"""Requests the API cannot read are refused before any handler runs, with a structured answer; owned preview only."""
import json
import secrets
import time
from uuid import uuid4
from preview_client import PreviewClient

# The request body limit the API sets (src/OpenEtrm.Api/Program.cs).
LIMIT = 32 * 1024


def unreadable(body, text, overflow):
    """The unreadable variants of a readable body, each with the status and code of its answer. `text` names one of the
    body's text members, whose value every variant carries; `overflow` is the body with a whole number beyond its
    member's range."""
    encoded = json.dumps(body)
    return [('a whole number beyond its range', overflow, 400, 'request_format'),
            ('broken JSON', encoded[:-1].encode(), 400, 'request_format'),
            ('an unknown member', {**body, 'unlisted': True}, 400, 'request_format'),
            ('a repeated member', (encoded[:-1] + ', ' + json.dumps({text: body[text]})[1:]).encode(), 400, 'request_format'),
            ('an escaped lone surrogate', {**body, text: body[text] + '\ud800'}, 400, 'request_format'),
            ('a body over 32 KiB', {**body, text: body[text] + 'x' * LIMIT}, 413, 'request_too_large')]


def is_refusal(answer, code, sent):
    """Whether an answer names only the code and a message, without echoing what was sent."""
    return isinstance(answer, dict) and set(answer) == {'code', 'message'} and answer['code'] == code and sent not in answer['message']


def one_message_per_code(answers):
    """Whether answers with the same code share one message, so no message depends on what was sent."""
    messages = {}
    for answer in answers:
        messages.setdefault(answer['code'], set()).add(answer['message'])
    return all(len(texts) == 1 for texts in messages.values())


def run_checks(url, log):
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    admin = PreviewClient(url)
    admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    # Each body is readable and its handler refuses it without administering anything, so a variant bound by mistake
    # would administer nothing either.
    key = {'operationId': str(uuid4()), 'vendor': 'acme', 'expectedVersion': 0, 'key': 'sk-synthetic-' + secrets.token_hex(16)}
    edit = {'operationId': str(uuid4()), 'modelId': str(uuid4()), 'expectedVersion': 1, 'outputLimitSource': 'https://example.invalid/limit'}
    save = {'operationId': str(uuid4()), 'agentId': str(uuid4()), 'modelId': str(uuid4()), 'description': 'Synthetic agent', 'limits': {'toolSteps': 8}}
    routes = (('settings/ai/providers/key', key, 'key', {**key, 'expectedVersion': 99999999999}),
              ('settings/ai/models/edit', edit, 'outputLimitSource', {**edit, 'expectedVersion': 99999999999}),
              ('settings/ai/agents/save', save, 'description', {**save, 'limits': {'toolSteps': 99999999999}}))
    logged, answers = log.read_bytes(), []
    for path, body, text, overflow in routes:
        for case, payload, status, code in unreadable(body, text, overflow):
            answers.append(admin.call(path, payload, expected=status))
            check(f'{path}: {case} is refused {status} {code}, echoing nothing', is_refusal(answers[-1], code, body[text]))
    answers.append(admin.call('bootstrap?includeAccess=unknown', expected=400))
    check('a query value that does not bind is refused 400 request_format', is_refusal(answers[-1], 'request_format', 'unknown'))
    check('answers with the same code share one message, whatever was sent', one_message_per_code(answers))
    # The console logger writes from its own thread.
    time.sleep(.5)
    check('a refused request adds nothing to the application log, so no character of a refused key reaches it', log.read_bytes() == logged)
    for path, body, _, _ in routes:
        check(f'{path}: the readable body reaches its handler, which refuses it', admin.call(path, body, expected=422)['code'] == 'model_admin_input')
    print(f'{passed} request refusal checks passed.', flush=True)
