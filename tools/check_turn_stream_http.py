"""The live event stream over HTTP (docs/ai-assistant.md §8): admission, replay and resume, live text, the wire format,
one stream per unfinished turn, the endings of a stream and of the session it binds, against the loopback fake
provider; owned synthetic preview only. It opens no session: it streams in the administrator and trader sessions the
turn checks hand it, acts as other people by switching the administrator's profile, and sends the rest without a
cookie."""
import http.client
import json
import threading
import time
from urllib.parse import urlsplit
from urllib.request import HTTPCookieProcessor
from uuid import uuid4
from assistant_context import Contexts, holding
from assistant_fixture import Fixture
from check_agent_turn_http import closing, delta, opening
from check_provider_tests import Worker
from fake_model_provider import recording, stream, tool_use
from preview_client import initial_frame

ASK, STOP, GRANTS = 'assistant/turns/ask', 'assistant/turns/stop', 'assistant/grants'
REPORT = {'page': 'curves', 'selection': {'view': 'curve', 'curve': 'future'}}
ENDED = ('completed', 'failed', 'cancelled', 'interrupted')
STORED = ('accepted', 'step', 'text', 'usage', *ENDED)
CORS = ('access-control-allow-origin', 'access-control-allow-credentials', 'access-control-allow-headers', 'access-control-allow-methods')


def jar(client):
    return next(h for h in client.opener.handlers if isinstance(h, HTTPCookieProcessor)).cookiejar


def cookies(client):
    return '; '.join(f'{c.name}={c.value}' for c in jar(client))


class Stream:
    """A conversation's event stream requested as native EventSource requests it from the application's own page: with the
    session's cookie, `Sec-Fetch-Site: same-origin` and `Sec-Fetch-Mode: cors`, and no Origin. `headers` overrides any of
    them, leaving out those it sets to None."""

    def __init__(self, client, conversation, after, last_event_id=None, headers=None, timeout=25):
        address = urlsplit(client.url)
        self.connection = http.client.HTTPConnection(address.hostname, address.port, timeout=timeout)
        sent = {'Accept': 'text/event-stream', 'Cache-Control': 'no-cache', 'Sec-Fetch-Site': 'same-origin', 'Sec-Fetch-Mode': 'cors',
                'Sec-Fetch-Dest': 'empty', 'Cookie': cookies(client), 'Last-Event-ID': None if last_event_id is None else str(last_event_id),
                **(headers or {})}
        self.connection.request('GET', f'{client.api_prefix}assistant/conversations/{conversation}/events?after={after}',
                                headers={name: value for name, value in sent.items() if value is not None})
        self.response = self.connection.getresponse()
        self.status = self.response.status
        self.headers = {name.lower(): value for name, value in self.response.getheaders()}
        self.lines = []

    def refusal(self):
        """The JSON answer of a stream refused before it began."""
        body = json.loads(self.response.read())
        self.close()
        return body

    def frame(self):
        """The next frame: an event's fields with its data parsed, or a comment; None once the stream has ended."""
        fields, comment = {}, None
        while True:
            raw = self.response.readline()
            if not raw:
                return None
            line = raw.decode('utf-8').removesuffix('\n')
            self.lines.append(line)
            if line == '':
                if fields or comment is not None:
                    return {'comment': comment} if not fields else {**fields, 'data': json.loads(fields['data'])}
                continue
            if line.startswith(':'):
                comment = line[1:].strip()
                continue
            name, _, value = line.partition(':')
            assert name in ('id', 'event', 'data') and name not in fields, line
            fields[name] = value.removeprefix(' ')

    def until(self, predicate):
        """The frames up to and including the first that `predicate` accepts."""
        frames = []
        while True:
            frame = self.frame()
            assert frame is not None, ('The stream ended first', frames)
            frames.append(frame)
            if predicate(frame):
                return frames

    def rest(self):
        """Every frame until the stream ends, which it must do."""
        frames = []
        while (frame := self.frame()) is not None:
            frames.append(frame)
        self.close()
        return frames

    def close(self):
        self.connection.close()


def events(frames):
    """The frames that are events, without comments."""
    return [f for f in frames if 'event' in f]


def run_checks(url, query, psql, manifest, environment, fake, trap, admin, trader):
    assert not fake.script, 'An earlier suite left responses scripted.'
    contexts = Contexts(query, manifest)
    passed = 0
    stalled = []
    held = {client: dict((c.name, c.value) for c in jar(client)) for client in (admin, trader)}

    def check(name, condition=True, observed=None):
        nonlocal passed
        assert condition, name if observed is None else f'{name}: observed {observed}'
        passed += 1
        print('PASS ' + name, flush=True)

    stamp = uuid4().hex[:8]
    fixture = Fixture(admin, query, manifest, contexts.administrator, stamp, 'stream')
    fixture.qualify()
    model_name = f'model-stream-{stamp}'
    model = fixture.model(model_name)

    def agent(name, **limits):
        return fixture.publish(fixture.save(fixture.create(f'stream {name}'), model, tools=(), **limits))

    def grant(session, agent_state):
        return session.call(f'{GRANTS}/grant', {'operationId': str(uuid4()), 'agentId': agent_state['id'], 'version': 1})
    guide, hasty = agent('guide'), agent('hasty', elapsedSeconds=5)
    for agent_state in (guide, hasty):
        grant(trader, agent_state)

    def frame_of(session):
        return initial_frame(session.call('workspace/navigation'), '2020-12-18')

    def ask(session=trader, agent_state=None, conversation=None, question='What does this page show?'):
        return session.call(ASK, {'operationId': str(uuid4()), 'conversationId': conversation, 'agentId': (agent_state or guide)['id'], 'version': 1,
                                  'question': question, 'pageReport': REPORT,
                                  'knowledge': {'mode': 'current', 'contextId': None, 'frameId': frame_of(session)['id']}})['turn']

    def read(turn, session=trader):
        conversation = session.call(f"assistant/conversations/{turn['conversationId']}?after=0")
        return next(t for t in conversation['turns'] if t['id'] == turn['id']), [e for e in conversation['events'] if e['turnId'] == turn['id']]

    def settled(turn, session=trader, timeout=20):
        deadline = time.monotonic() + timeout
        while (current := read(turn, session))[0]['state'] not in ENDED:
            assert time.monotonic() < deadline, current
            time.sleep(.05)
        return current

    def stalling(text='Partial '):
        """A response that streams `text` and then waits until released."""
        release = threading.Event()
        stalled.append(release)
        fake.respond(stream(opening(model_name, text), release, delta('more.') + closing()))
        return release

    def texted(frame):
        return frame.get('event') == 'text'

    def sequence_of(frames):
        return [int(f['id']) for f in events(frames) if 'id' in f]

    def stored_as_streamed(turn, frames, session=trader):
        """Whether the streamed events are the turn's stored events after the first one streamed, in order, without a gap or
        a repeat, each with its stored kind and payload, its turn and its hidden marker, and the terminal event with whether
        the turn is incomplete."""
        current, stored = read(turn, session)
        streamed = [f for f in events(frames) if f['event'] in STORED]
        if not streamed:
            return False
        tail = [e for e in stored if e['sequence'] >= int(streamed[0]['id'])]
        return len(tail) == len(streamed) and all(
            (int(f['id']), f['event'], f['data']['payload'], f['data']['turnId'], f['data']['hidden']) == (e['sequence'], e['kind'], e['payload'], turn['id'], None)
            and (f['data'].get('incomplete') == current['incomplete'] if e['kind'] in ENDED else 'incomplete' not in f['data'])
            for f, e in zip(streamed, tail))

    def no_cors(response):
        return not any(name in response.headers for name in CORS)

    # A finished turn replays its stored events through its terminal event, and the stream then ends (§8).
    fake.respond(stream(recording(model_name, text='Ready.')))
    done = ask()
    settled(done)
    replay = Stream(trader, done['conversationId'], 0)
    frames = replay.rest()
    check('a stream of a finished turn opens with a comment and replays its events after the sequence, as stored, through its terminal '
          'event, and then ends; the terminal event says whether the turn is incomplete',
          replay.status == 200 and replay.headers['content-type'].startswith('text/event-stream') and replay.headers['cache-control'] == 'no-store'
          and no_cors(replay) and frames[0] == {'comment': 'open'} and [f['event'] for f in events(frames)] == ['accepted', 'text', 'usage', 'completed']
          and sequence_of(frames) == [1, 2, 3, 4] and stored_as_streamed(done, frames), frames)
    resumed = Stream(trader, done['conversationId'], 0, last_event_id=3).rest()
    beyond, current = (Stream(trader, done['conversationId'], after).rest() for after in (5, 4))
    check("Last-Event-ID overrides after; a sequence beyond the conversation's latest is refused replay_unavailable and one at the end of a "
          'finished turn turn_not_running, each a refused event without an id that ends the stream',
          [(f['event'], f['id']) for f in events(resumed)] == [('completed', '4')]
          and [f.get('event') for f in events(beyond)] == ['refused'] and events(beyond)[0]['data']['code'] == 'replay_unavailable'
          and 'id' not in events(beyond)[0] and events(current)[0]['data']['code'] == 'turn_not_running' and len(events(current)) == 1,
          (resumed, beyond, current))
    fake.respond(stream(recording(model_name, text='Again.')))
    again = ask(conversation=done['conversationId'], question='And again?')
    settled(again)
    earlier = Stream(trader, done['conversationId'], 3).rest()
    follow = Stream(trader, done['conversationId'], 4).rest()
    check("a stream serves the conversation's latest turn: a sequence before its first event is refused replay_unavailable, and one just "
          'before it replays that turn alone', events(earlier)[0]['data']['code'] == 'replay_unavailable'
          and [f['event'] for f in events(follow)] == ['accepted', 'text', 'usage', 'completed'] and stored_as_streamed(again, follow), (earlier, follow))

    # Admission (§8): EventSource sends the cookie and its Sec-Fetch headers but no anti-forgery header; anything else is
    # refused before the stream begins, and no answer carries CORS headers.
    conversation = done['conversationId']
    answers = {}
    for name, headers in (('no Sec-Fetch', {'Sec-Fetch-Site': None, 'Sec-Fetch-Mode': None}), ('another site', {'Sec-Fetch-Site': 'same-site'}),
                          ('typed address', {'Sec-Fetch-Site': 'none'}), ('no-cors', {'Sec-Fetch-Mode': 'no-cors'}),
                          ('navigation', {'Sec-Fetch-Mode': 'navigate'}), ('no cookie', {'Cookie': None}),
                          ('unknown session', {'Cookie': 'openetrm_preview=' + '0' * 64})):
        refused = Stream(trader, conversation, 0, headers=headers)
        answers[name] = (refused.status, refused.refusal()['code'], no_cors(refused), 'set-cookie' not in refused.headers)
    foreign = Stream(trader, conversation, 0, headers={'Origin': 'http://127.0.0.1:1'})
    foreign_answer = (foreign.status, no_cors(foreign))
    foreign.close()
    check('a stream needs Sec-Fetch-Site same-origin and Sec-Fetch-Mode cors, the session cookie and no other Origin, and is refused 403 '
          'otherwise, session_ended for a missing or unknown session, without CORS headers or a new session',
          all(answer[0] == 403 and answer[2] and answer[3] for answer in answers.values()) and foreign_answer == (403, True)
          and {name: answer[1] for name, answer in answers.items()} == {
              'no Sec-Fetch': 'access_denied', 'another site': 'access_denied', 'typed address': 'access_denied', 'no-cors': 'access_denied',
              'navigation': 'access_denied', 'no cookie': 'session_ended', 'unknown session': 'session_ended'}, (answers, foreign_answer))
    malformed = {after: Stream(trader, conversation, after) for after in ('x', '-1')}
    malformed['header'] = Stream(trader, conversation, 0, last_event_id='4x')
    malformed = {name: (refused.status, refused.refusal()['code']) for name, refused in malformed.items()}
    admin.call('profile', {'profile': 'risk'})
    other = Stream(admin, conversation, 0)
    other = (other.status, other.refusal()['code'])
    admin.call('profile', {'profile': 'admin'})
    unauthorized = Stream(admin, conversation, 0)
    unauthorized = (unauthorized.status, unauthorized.refusal()['code'])
    check('a stream refuses a sequence that does not parse and a negative one, another person\'s conversation as an unknown one, and a person '
          'without UseAgents', malformed == {'x': (400, 'request_format'), '-1': (422, 'turn_input'), 'header': (400, 'request_format')}
          and other == (404, 'conversation_not_found') and unauthorized == (403, 'access_denied'), (malformed, other, unauthorized))

    def anonymous(path, body=None):
        """A GET without a cookie, or a POST with the page's origin and request token from a session the server does not know."""
        connection = http.client.HTTPConnection(urlsplit(url).hostname, urlsplit(url).port, timeout=20)
        if body is None:
            connection.request('GET', '/api/' + path)
        else:
            unknown = '; '.join(f"{c.name}={'0' * 64 if c.name == 'openetrm_preview' else c.value}" for c in jar(trader))
            connection.request('POST', '/api/' + path, body=json.dumps(body), headers={
                'Origin': trader.url, 'Content-Type': 'application/json', 'X-Preview-CSRF': trader.token, 'Cookie': unknown})
        response = connection.getresponse()
        answer = (response.status, json.loads(response.read())['code'], 'set-cookie' not in {k.lower() for k, _ in response.getheaders()})
        connection.close()
        return answer
    routes = {path: anonymous(path) for path in ('assistant/conversations', f'assistant/conversations/{conversation}?after=0', 'assistant/turns/unfinished',
                                                 'assistant/agents', 'assistant/grants', 'workspace/navigation')}
    routes.update({path: anonymous(path, body) for path, body in (
        (STOP, {'turnId': str(uuid4())}), ('assistant/conversations/read', {'conversationId': conversation, 'through': 0}), ('profile', {'profile': 'trader'}))})
    check('without a session every assistant route answers session_ended, a command carrying the page\'s request token included, and pages '
          'keep access_denied', routes == {**{path: (403, 'session_ended', True) for path in routes if path.startswith('assistant/')},
                                           'workspace/navigation': (403, 'access_denied', True), 'profile': (403, 'access_denied', True)}, routes)

    # Live text (§8): a waiting stream holds no database connection and is sent each piece once it commits.
    release = stalling()
    live = ask()
    watching = Stream(trader, live['conversationId'], 0)
    opened = watching.until(texted)
    busy = []
    for _ in range(5):
        busy.append(query("SELECT count(*) FROM pg_stat_activity WHERE usename IN('preview_runtime','preview_authenticator') AND state<>'idle';"))
        time.sleep(.1)
    release.set()
    frames = opened + watching.rest()
    current, _ = read(live)
    check('a live stream sends each piece of text once it commits and the rest through the terminal event, in order without a gap or repeat, '
          'its text the stored answer; while it waits nothing holds a database transaction',
          busy == ['0'] * 5 and [f['event'] for f in events(frames)][-2:] == ['usage', 'completed'] and stored_as_streamed(live, frames)
          and ''.join(f['data']['payload']['text'] for f in events(frames) if f['event'] == 'text') == current['answer'] == 'Partial more.',
          (busy, frames))

    # Live steps (§7.3, §8): a step reaches a stream that is already live once it commits, while the call after it is still
    # open. The first call publishes its text, then waits before its tool request; the second waits before it answers.
    reader = fixture.publish(fixture.save(fixture.create('stream reader'), model, tools=('read_market_data',)))
    grant(trader, reader)
    reading = tool_use(model_name, [('toolu_synthetic_s1', 'read_market_data', {'view': 'curves', 'curve': None})], text='Looking. ')
    cut = reading.index('event: content_block_stop')
    requested, answering = threading.Event(), threading.Event()
    stalled.extend((requested, answering))
    fake.respond(stream(reading[:cut], requested, reading[cut:]), stream('', answering, recording(model_name, text='Read.')))
    stepping = ask(agent_state=reader)
    stepped = Stream(trader, stepping['conversationId'], 0, timeout=10)
    looked = stepped.until(texted)
    requested.set()
    try:
        reached = stepped.until(lambda frame: frame.get('event') == 'step')
    except TimeoutError:
        reached = None
    answering.set()
    finished = stepped.rest() if reached is not None else []
    stepped.close()
    settled(stepping)
    check('a live stream sends each step once it commits, while the call after it is still open, and the rest through the terminal event',
          reached is not None and 'text' not in [f['event'] for f in events(reached)] and events(finished)[-1]['event'] == 'completed'
          and stored_as_streamed(stepping, looked + reached + finished), (looked, reached, finished))

    # Model text cannot shape the stream (§8, §13): CR, LF and CRLF and what follows them arrive only inside JSON strings.
    injected = 'Line one\r\nevent: navigation\ndata: {"target":"elsewhere"}\nid: 999\n\nTail\r'
    fake.respond(stream(recording(model_name, text=injected)))
    shaped = ask()
    settled(shaped)
    shaping = Stream(trader, shaped['conversationId'], 0)
    frames = shaping.rest()
    check('an answer holding CR, LF, CRLF, event:, data: and id: lines and a blank line arrives only inside text events, byte for byte the stored '
          'answer, and every line of the stream is one of its own',
          [f['event'] for f in events(frames)][0] == 'accepted' and {f['event'] for f in events(frames)} <= set(STORED)
          and '999' not in {f.get('id') for f in frames} and stored_as_streamed(shaped, frames)
          and ''.join(f['data']['payload']['text'] for f in events(frames) if f['event'] == 'text') == read(shaped)[0]['answer'] == injected
          and all(line == '' or line.startswith((':', 'id: ', 'event: ', 'data: {')) for line in shaping.lines), shaping.lines)

    # Resume (§8): a reconnect names the last event it received, which overrides after, and gets exactly what followed,
    # live while the turn runs and replayed once it has ended.
    release = stalling()
    resuming = ask()
    dropped = Stream(trader, resuming['conversationId'], 0)
    seen = dropped.until(texted)
    dropped.close()
    last = sequence_of(seen)[-1]
    reconnected = Stream(trader, resuming['conversationId'], 0, last_event_id=last)
    opened = reconnected.frame()
    release.set()
    reconnected = [opened, *reconnected.rest()]
    settled(resuming)
    fresh = Stream(trader, resuming['conversationId'], last).rest()
    check('a stream that dropped resumes through Last-Event-ID, which overrides after, while its turn runs, or a new one through after once it '
          'has ended, each with exactly the events that followed', opened == {'comment': 'open'}
          and sequence_of(reconnected) == sequence_of(fresh) == list(range(last + 1, sequence_of(reconnected)[-1] + 1))
          and events(reconnected)[-1]['event'] == 'completed' and stored_as_streamed(resuming, seen + reconnected), (seen, reconnected, fresh))

    # One stream per unfinished turn (§8): a newer one, from any of the owner's sessions, sends the older replaced.
    release = stalling()
    shared = ask()
    first = Stream(trader, shared['conversationId'], 0)
    early = first.until(texted)
    second = Stream(trader, shared['conversationId'], sequence_of(early)[-1])
    replaced_first = first.rest()
    admin.call('profile', {'profile': 'trader'})
    third = Stream(admin, shared['conversationId'], sequence_of(early)[-1])
    replaced_second = second.rest()
    release.set()
    finishing = third.rest()
    admin.call('profile', {'profile': 'admin'})
    check("a newer stream of an unfinished turn, in the same session or another of its owner's, sends the older one replaced, without an id, "
          'and that one ends while the newer one continues to the terminal event',
          [f['event'] for f in events(replaced_first)] == ['replaced'] and events(replaced_first)[0]['data'] == {} and 'id' not in events(replaced_first)[0]
          and [f['event'] for f in events(replaced_second)] == ['replaced'] and stored_as_streamed(shared, early + finishing)
          and events(finishing)[-1]['event'] == 'completed', (replaced_first, replaced_second, finishing))

    # Stop (§8): the stream carries the turn to its cancelled ending.
    stalling()
    stopping = ask()
    stopped = Stream(trader, stopping['conversationId'], 0)
    early = stopped.until(texted)
    trader.call(STOP, {'turnId': stopping['id']})
    frames = early + stopped.rest()
    check('after Stop the stream sends the usage and the cancelled ending, which carries no code and says the published answer is incomplete',
          [f['event'] for f in events(frames)][-2:] == ['usage', 'cancelled'] and events(frames)[-1]['data']['payload'] == {}
          and events(frames)[-1]['data']['incomplete'] is True and stored_as_streamed(stopping, frames), frames)

    # The stream binds the session that opened it (§7.2, §8): a profile switch ends it, while the turn its owner asked in
    # another session goes on.
    release = stalling()
    bound = ask()
    admin.call('profile', {'profile': 'trader'})
    switched = Stream(admin, bound['conversationId'], 0)
    switched.until(texted)
    admin.call('profile', {'profile': 'admin'})
    frames = switched.rest()
    release.set()
    check("a profile switch ends the session's streams with refused session_ended, and a turn asked in another session goes on",
          [f['event'] for f in events(frames)] == ['refused'] and events(frames)[0]['data']['code'] == 'session_ended'
          and settled(bound)[0]['state'] == 'completed', frames)

    # The stream needs current UseAgents (§8): once its owner loses it, the turn fails and the stream is refused.
    login = f'synthetic_stream_{stamp}'
    admin.call('admin/create', {'operationId': str(uuid4()), 'login': login, 'name': 'Synthetic stream reader', 'kind': 'human', 'roles': ['trader']})
    admin.call('profile', {'profile': login})
    grant(admin, guide)
    release = stalling()
    losing = ask(admin)
    revoking = Stream(admin, losing['conversationId'], 0)
    revoking.until(texted)
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE subject='{login}');")
    release.set()
    frames = revoking.rest()
    admin.call('profile', {'profile': 'admin'})
    check('a stream whose owner loses UseAgents is refused access_denied once it reads again, after the turn failed access_changed',
          [f['event'] for f in events(frames)] == ['refused'] and events(frames)[0]['data']['code'] == 'access_denied'
          and query(f"SELECT state||':'||outcome FROM assistant.turns WHERE id='{losing['id']}';") == 'failed:access_changed', frames)

    # A worker whose generation ends (§7.4): its streams are refused, a stream needs the worker while another session holds
    # its lock, and the next generation interrupts the turn, whose stream then replays to that ending.
    release = stalling()
    orphan = ask()
    orphaned = Stream(trader, orphan['conversationId'], 0)
    early = orphaned.until(texted)
    successor = Worker(psql, environment, query, start=False)
    lost = orphaned.rest()
    blocked = Stream(trader, orphan['conversationId'], sequence_of(early)[-1])
    blocked = (blocked.status, blocked.refusal()['code'])
    successor.stop()
    recovered = Stream(trader, orphan['conversationId'], sequence_of(early)[-1]).rest()
    release.set()
    check('when the worker loses its generation its streams are refused agent_runtime_unavailable; while another session holds the lock a '
          "stream of the unfinished turn is refused 503, and once it is free the stream starts the next generation, which interrupts the turn, "
          'and replays to that ending', [f['event'] for f in events(lost)] == ['refused'] and events(lost)[0]['data']['code'] == 'agent_runtime_unavailable'
          and blocked == (503, 'agent_runtime_unavailable') and [f['event'] for f in events(recovered)][-2:] == ['usage', 'interrupted']
          and events(recovered)[-1]['data']['incomplete'] is True and stored_as_streamed(orphan, early + recovered), (lost, blocked, recovered))

    # Heartbeats and expiry (§7.5, §8): an idle stream writes a comment at least every 20 seconds, and one whose turn passes
    # its elapsed limit and the admission deadline without ending is refused stream_expired. Here the turn's row is held so
    # its ending cannot land.
    release = stalling()
    stuck = ask(agent_state=hasty)
    expiring = Stream(trader, stuck['conversationId'], 0, timeout=60)
    expiring.until(texted)
    seen_at = time.monotonic()

    def wait_out():
        frames, pings = [], []
        while (frame := expiring.frame()) is not None:
            frames.append(frame)
            if frame.get('comment') == 'ping':
                pings.append(time.monotonic())
        return frames, pings
    frames, pings = holding(psql, environment, f"SELECT 1 FROM assistant.turns WHERE id='{stuck['id']}' FOR UPDATE", wait_out)
    expiring.close()
    release.set()
    gaps = [later - earlier for earlier, later in zip([seen_at, *pings], pings)]
    check('an idle stream writes a ping comment within 20 seconds of its last write, and a turn that passes its elapsed limit and the admission '
          'deadline without ending has its stream refused stream_expired, after which the turn still ends',
          len(pings) >= 2 and all(13 < gap < 20 for gap in gaps) and [f['event'] for f in events(frames)] == ['refused']
          and events(frames)[0]['data']['code'] == 'stream_expired' and settled(stuck, timeout=30)[0]['outcome'] == 'limit_elapsed', (gaps, frames))

    for release in stalled:
        release.set()
    check('the stream checks opened no session, as the cookies of the sessions they were handed show, left no response scripted and sent '
          'no request through a proxy', {client: dict((c.name, c.value) for c in jar(client)) for client in (admin, trader)} == held
          and not fake.script and trap.connections == 0)
    print(f'{passed} HTTP stream checks passed.', flush=True)
    return admin, trader
