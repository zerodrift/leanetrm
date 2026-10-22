"""The page assistant under --hosted-pilot (docs/ai-assistant.md §11): Settings › AI under the installation's credential key
files, a grant, an ask whose read runs on the hosted agent pools, and the stream through the hosted TLS proxy, against the
loopback fake provider; each turn bound to its account's sign-in session, which signing out, a password reset and
disabling the member revoke. Owned synthetic hosted check only."""
import hashlib
import http.client
import os
import secrets
import threading
import time
from urllib.parse import urlsplit
from uuid import uuid4
from assistant_fixture import Fixture
from check_agent_turn_http import closing, delta, opening
from check_turn_stream_http import Stream, events
from fake_model_provider import recording, stream, tool_use
from preview_client import initial_frame

REPORT = {'page': 'curves', 'selection': {'view': 'curve', 'curve': 'future'}}


class HostedStream(Stream):
    """A conversation's event stream as native EventSource requests it from the hosted page: through the TLS proxy, with the
    signed-in browser's cookies, `Sec-Fetch-Site: same-origin` and `Sec-Fetch-Mode: cors`, and no Origin."""

    def __init__(self, client, origin, tls, conversation, after=0):
        address = urlsplit(origin)
        self.connection = http.client.HTTPSConnection(address.hostname, address.port, context=tls, timeout=25)
        self.connection.request('GET', f'/api/assistant/conversations/{conversation}/events?after={after}', headers={
            'Accept': 'text/event-stream', 'Cache-Control': 'no-cache', 'Sec-Fetch-Site': 'same-origin', 'Sec-Fetch-Mode': 'cors',
            'Sec-Fetch-Dest': 'empty', 'Cookie': '; '.join(f'{c.name}={c.value}' for c in client.cookies)})
        self.response = self.connection.getresponse()
        self.status = self.response.status
        self.headers = {name.lower(): value for name, value in self.response.getheaders()}
        self.lines = []


class Api:
    """A signed-in hosted client as the assistant fixture calls one: paths under /api/, answers only."""

    def __init__(self, client):
        self.client = client

    def call(self, path, body=None, expected=200):
        return self.client.call('/api/' + path, body, expected=expected)[0]


def run_checks(Client, provision, query, manifest, fake, trap, origin, tls, keys, password):
    """`Client` signs in over the hosted proxy, `provision` invites and claims an account with `password`, and `keys` names
    the installation's two credential key files."""
    passed = 0

    def check(name, condition=True, observed=None):
        nonlocal passed
        assert condition, name if observed is None else f'{name}: observed {observed}'
        passed += 1
        print('PASS ' + name, flush=True)

    def admitted(work):
        """Runs a sign-in or claim, which share the installation's limit of 12 a minute (HostedAuthentication): one refused
        429 is tried again once that minute has passed."""
        deadline = time.monotonic() + 75
        while True:
            try:
                return work()
            except AssertionError as error:
                if ', 429,' not in str(error) or time.monotonic() > deadline:
                    raise
            time.sleep(5)

    def signed_in(login):
        client = Client()
        admitted(lambda: client.login(login, password))
        client.bootstrap()
        return client

    def state(turn):
        return query(f"SELECT state||':'||coalesce(outcome,'-') FROM assistant.turns WHERE id='{turn}';")

    def ended(turn, timeout=15):
        deadline = time.monotonic() + timeout
        while (current := state(turn)).split(':')[0] not in ('completed', 'failed', 'cancelled', 'interrupted'):
            assert time.monotonic() < deadline, current
            time.sleep(.05)
        return current

    def received(count, timeout=10):
        """Returns once the fake provider has received `count` requests."""
        deadline = time.monotonic() + timeout
        while len(fake.received) < count:
            assert time.monotonic() < deadline, len(fake.received)
            time.sleep(.05)

    owner_login, bystander_login, resetting_login, disabled_login = 'assistant_owner', 'assistant_bystander', 'assistant_resetting', 'assistant_disabled'
    owner = admitted(lambda: provision(owner_login, ['trader', 'access_administrator']))
    principals = {login: admitted(lambda: provision(login, ['trader'])) for login in (bystander_login, resetting_login, disabled_login)}
    administrator = signed_in(owner_login)
    api = Api(administrator)

    # The installation's key files seal provider keys (§5.2, §11): one moved away leaves the key unsealed and unaudited.
    moved, stored = keys[0] + '.moved', query('SELECT count(*) FROM assistant.credentials;')
    os.rename(keys[0], moved)
    try:
        refused, _ = administrator.call('/api/settings/ai/providers/key', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': 0,
                                                                           'key': 'sk-synthetic-hosted-key-0000'}, expected=422)
    finally:
        os.rename(moved, keys[0])
    check("a hosted Replace key without the installation's key file is credential_unavailable and stores nothing",
          refused['code'] == 'credential_unavailable' and query('SELECT count(*) FROM assistant.credentials;') == stored, refused)

    stamp = uuid4().hex[:8]
    fixture = Fixture(api, query, manifest, owner, stamp, 'hosted')
    fixture.qualify()
    identifier = f'model-hosted-{stamp}'
    guide = fixture.publish(fixture.save(fixture.create('hosted guide'), fixture.model(identifier), tools=('read_current_page', 'read_market_data')))

    def grant(client):
        client.call('/api/assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': guide['id'], 'version': 1})

    def ask(client):
        frame = initial_frame(client.call('/api/workspace/navigation')[0], '2020-12-18')
        return client.call('/api/assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': guide['id'], 'version': 1,
                                                        'question': 'What does this page show?', 'pageReport': REPORT,
                                                        'knowledge': {'mode': 'current', 'contextId': None, 'frameId': frame['id']}})[0]['turn']

    def stalled(client):
        """A turn of `client`'s that has streamed some text and waits until its release is set."""
        release = threading.Event()
        reached = len(fake.received)
        fake.respond(stream(opening(identifier, 'Partial '), release, delta('more.') + closing()))
        turn = ask(client)
        received(reached + 1)
        return turn, release

    grant(administrator)
    reached = len(fake.received)
    fake.respond(stream(tool_use(identifier, [('toolu_hosted_read', 'read_current_page', {})])), stream(recording(identifier, text='Hosted answer.')))
    answered = ask(administrator)
    outcome = ended(answered['id'])
    steps = query(f"SELECT string_agg(kind||':'||coalesce(payload->>'code','-'),',' ORDER BY sequence) FROM assistant.turn_events "
                  f"WHERE turn_id='{answered['id']}' AND kind IN('step','text','completed');")
    bound = query(f"SELECT t.session_ends_at=s.expires_at AND t.owner_id=s.principal_id FROM assistant.turns t "
                  f"JOIN security.hosted_sessions s ON s.token_digest=t.session_digest WHERE t.id='{answered['id']}';")
    check('a hosted ask runs its read on the agent pools in the agent\'s own context and is answered through the check endpoint alone, '
          'bound to the sign-in session it was asked under and ending with it',
          outcome == 'completed:-' and steps.startswith('step:-,') and steps.endswith(',completed:-') and bound == 't'
          and len(fake.received) == reached + 2 and trap.connections == 0, (outcome, steps, bound, trap.connections))

    # Signing out cancels the session's turn session_ended and ends its stream at once; another account's turn runs on.
    bystander = signed_in(bystander_login)
    grant(bystander)
    signing_out = signed_in(owner_login)
    turn, release = stalled(signing_out)
    watching = HostedStream(signing_out, origin, tls, turn['conversationId'])
    streamed = watching.until(lambda frame: frame.get('event') == 'text')
    other, other_release = stalled(bystander)
    # While the generation row is held, the worker can record nothing, so only the sign-out itself can end the stream.
    holder = threading.Thread(target=query, args=('BEGIN;SELECT 1 FROM assistant.worker_generation FOR UPDATE;SELECT pg_sleep(5) /* held_generation */;COMMIT;',))
    holder.start()
    deadline = time.monotonic() + 5
    while query("SELECT count(*) FROM pg_stat_activity WHERE query LIKE '%held_generation%' AND pid<>pg_backend_pid();") != '1':
        assert time.monotonic() < deadline, 'The generation row was not held'
        time.sleep(.05)
    signing_out.call('/api/logout', {})
    rest = watching.rest()
    unfinished = state(turn['id'])
    holder.join(timeout=15)
    cancelled = ended(turn['id'])
    running = state(other['id'])
    release.set(); other_release.set()
    check('signing out of the pilot ends the stream of the turn its session asked with session_ended at once, before the turn has ended, '
          'and cancels that turn session_ended, while a turn another account asked runs on to its answer',
          watching.status == 200 and events(streamed)[-1]['event'] == 'text' and events(rest)[-1]['event'] == 'refused'
          and events(rest)[-1]['data']['code'] == 'session_ended' and unfinished == 'cancel_requested:-' and cancelled == 'cancelled:session_ended'
          and running == 'running:-' and ended(other['id']) == 'completed:-', (events(rest)[-1:], unfinished, cancelled, running))

    # A password reset revokes the account's sign-in sessions, and so cancels their turns.
    resetting = signed_in(resetting_login)
    grant(resetting)
    turn, release = stalled(resetting)
    token = secrets.token_hex(32)
    query("INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) "
          f"VALUES('{hashlib.sha256(token.encode()).hexdigest()}','{principals[resetting_login]}','reset',clock_timestamp()+interval '1 hour');")
    claimant = Client()
    _, headers = admitted(lambda: claimant.call('/auth/claim', {
        'invitation': token, 'login': resetting_login, 'name': 'Synthetic hosted reset', 'password': password + ' reset',
        '__RequestVerificationToken': claimant.form_token()}, form=True, expected=302))
    reset = ended(turn['id'])
    release.set()
    check('a password reset cancels the turn of every session it revokes session_ended',
          headers['Location'] == '/auth?claimed=1' and reset == 'cancelled:session_ended', reset)

    # Disabling the member in Users & access revokes its sign-in sessions too.
    disabled = signed_in(disabled_login)
    grant(disabled)
    turn, release = stalled(disabled)
    administration = signed_in(owner_login)
    member = next(m for m in administration.call('/api/admin')[0]['members'] if m['id'] == principals[disabled_login])
    administration.call('/api/admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'],
                                              'enabled': False, 'roles': member['roles']})
    disabling = ended(turn['id'])
    release.set()
    check('disabling a member in Users & access cancels the turn its session asked session_ended', disabling == 'cancelled:session_ended', disabling)

    # An application session belongs to the sign-in session it was opened under: after another sign-in it is never used again.
    earlier = signed_in(owner_login)
    workspace = next(c for c in earlier.cookies if c.name == '__Host-openetrm_workspace')
    earlier.call('/api/logout', {})
    later = signed_in(owner_login)
    later.cookies.set_cookie(workspace)
    replayed, _ = later.call('/api/assistant/turns/unfinished', expected=403)
    later.bootstrap()
    fresh = next(c for c in later.cookies if c.name == '__Host-openetrm_workspace')
    reached = len(fake.received)
    fake.respond(stream(recording(identifier, text='Later answer.')))
    asked = ask(later)
    rebound = query(f"SELECT s.revoked_at IS NULL AND s.principal_id=t.owner_id FROM assistant.turns t "
                    f"JOIN security.hosted_sessions s ON s.token_digest=t.session_digest WHERE t.id='{asked['id']}';")
    check('an application session opened under an earlier sign-in is refused session_ended on assistant routes, and the next bootstrap '
          'opens one bound to the current sign-in session, under which asks are answered',
          replayed['code'] == 'session_ended' and fresh.value != workspace.value and ended(asked['id']) == 'completed:-' and rebound == 't'
          and len(fake.received) == reached + 1, (replayed, rebound))
    print(f'{passed} hosted assistant checks passed.', flush=True)
    return passed
