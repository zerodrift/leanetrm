"""Turns in the database (docs/ai-assistant.md §5.1, §7.1, §7.2, §7.4, §7.5, §7.6, §8): an ask in the person's own
context, admission, the pinned-context record and the reserve, the worker's writes under the turn's token, a turn's
call and its retries, cancellation and the sources that request it, recovery at a worker start, the owner's reads and
the Providers directory's tests; in SQL, playing the asking request and the worker; owned synthetic preview only."""
import hashlib
import json
import re
import secrets
import subprocess
import threading
import time
from uuid import uuid4
from assistant_context import ISSUER, WINDOW, Contexts, check_guard, expect, holding, refused, waiting
from assistant_fixture import AGENTS, MODELS, Fixture
from check_model_versions import command as model_command
from check_provider_tests import Worker, bytea, usage
from preview_client import PreviewClient

GRANTS = 'assistant/grants'
EFFECTIVE = '2020-12-18'
REPORT = {'page': 'curves', 'selection': {'curve': 'test_curve'}}
# Usage by dimension for close_turn_call: nothing sent bills nothing; unknown once anything was sent.
NOTHING, UNKNOWN = ('nb',) * 5, (None,) * 5
# Every row an ask or a worker write touches.
RECORDS = ("SELECT md5(string_agg(value,E'\\n' ORDER BY value)) FROM("
           "SELECT row_to_json(k)::text value FROM assistant.conversations k UNION ALL "
           "SELECT row_to_json(t)::text FROM assistant.turns t UNION ALL "
           "SELECT row_to_json(e)::text FROM assistant.turn_events e UNION ALL "
           "SELECT row_to_json(c)::text FROM assistant.model_calls c WHERE c.turn_id IS NOT NULL UNION ALL "
           "SELECT row_to_json(o)::text FROM assistant.model_call_outcomes o JOIN assistant.model_calls c ON c.id=o.call_id WHERE c.turn_id IS NOT NULL)s;")
HELPERS = ('security.has_permission(text)', 'security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)',
           'security.capture_date_context(uuid,date,timestamp with time zone)', 'security.market_access(uuid,text)', 'assistant.actor()',
           'assistant.is_instructions(text)', 'assistant.is_display_name(text)', 'assistant.grant_actor(boolean)',
           'assistant.may_use(security.request_contexts,assistant.agents,integer)', 'assistant.version_permissions(uuid,integer)',
           'assistant.covering_grant(security.request_contexts,uuid,integer)', 'assistant.model_qualified(uuid)', 'assistant.provider_qualified(uuid)',
           'assistant.current_attestation(uuid)', 'assistant.current_model_version(uuid)', 'assistant.price_in_force(uuid,timestamp with time zone)',
           'assistant.holds_worker_lock(integer)', 'assistant.token_cost_v1(assistant.price_revisions,bigint,bigint,bigint,bigint)',
           'assistant.check_model_call()', 'assistant.check_model_call_outcome()', 'assistant.provider_state(uuid)', 'assistant.test_state(uuid)',
           'assistant.close_test(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text)', 'assistant.start_worker()',
           'assistant.provider_directory()')


def check_upgrade(query, migration):
    """Before 0069 applies: it refuses a changed helper or replaced definition, or one whose privileges differ."""
    check_guard(query, migration, '0069', 'agent_turns', (
        *((f"ALTER FUNCTION {helper} SET work_mem='64kB';", helper) for helper in HELPERS),
        ('GRANT EXECUTE ON FUNCTION assistant.may_use(security.request_contexts,assistant.agents,integer) TO openetrm_runtime;',
         'assistant.may_use(security.request_contexts,assistant.agents,integer)'),
        ('REVOKE EXECUTE ON FUNCTION assistant.start_worker() FROM openetrm_runtime;', 'assistant.start_worker()')))


ASK = ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,'
       'integer,integer,integer)')
ASK_HELPERS = ('assistant.grant_actor(boolean)', 'assistant.is_instructions(text)', 'assistant.is_display_name(text)', 'assistant.holds_worker_lock(integer)',
               'assistant.turn_state(assistant.turns)', 'assistant.turn_event(assistant.turns,text,jsonb)', 'assistant.end_turn_as(assistant.turns,text,text)',
               'assistant.ask_authority(security.request_contexts,assistant.agents,integer)', 'assistant.call_blocker(uuid,integer,timestamp with time zone)')


def check_admitted_upgrade(query, migration):
    """Before 0071 applies: it refuses a changed ask or helper the ask relies on, or one whose privileges differ."""
    check_guard(query, migration, '0071', 'admitted_asks', (
        *((f"ALTER FUNCTION {helper} SET work_mem='64kB';", helper) for helper in (*ASK_HELPERS, ASK)),
        ('GRANT EXECUTE ON FUNCTION assistant.turn_state(assistant.turns) TO openetrm_runtime;', 'assistant.turn_state(assistant.turns)'),
        (f'REVOKE EXECUTE ON FUNCTION {ASK} FROM openetrm_runtime;', ASK)))


def literal(value):
    return 'NULL' if value is None else "'" + str(value).replace("'", "''") + "'"


def lapsed_in(failure, function):
    """Whether a verbose error output is a lapsed authority that `function`'s own recheck refused."""
    return bool(failure and '42501' in failure and re.search(
        rf'function assistant\.grant_actor\(boolean\) line \d+ at RAISE\n(?:(?!PL/pgSQL function).*\n)*PL/pgSQL function assistant\.{function}\(', failure))


class Turn:
    """A turn's fencing token, held only by the process that asked."""

    def __init__(self):
        self.token = secrets.token_bytes(32)
        self.digest = hashlib.sha256(self.token).hexdigest()
        self.id = None


def run_checks(url, query, psql, manifest, environment):
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True, observed=None):
        nonlocal passed
        assert condition, name if observed is None else f'{name}: observed {observed}'
        passed += 1
        print('PASS ' + name, flush=True)

    def client(profile):
        session = PreviewClient(url)
        session.bootstrap(); session.call('profile', {'profile': profile})
        return session

    stamp = uuid4().hex[:8]

    def login(name):
        return f'synthetic_turn_{name}_{stamp}'

    def principal(subject):
        return query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{ISSUER}' AND subject='{subject}';")

    def persona(name, roles):
        # A session can sign in only as the accounts it created.
        session = client('admin')
        session.call('admin/create', {'operationId': str(uuid4()), 'login': login(name), 'name': f'Synthetic turn {name}', 'kind': 'human', 'roles': roles})
        session.call('profile', {'profile': login(name)})
        return session
    admin, trader, risk = (client(profile) for profile in ('admin', 'trader', 'risk'))
    analyst, curator = persona('analyst', ['risk_analyst']), persona('curator', ['market_data_editor', 'access_administrator'])
    catalog = manifest['catalogId']

    # A qualified provider, a priced model and a published agent the trader has granted.
    fixture = Fixture(admin, query, manifest, contexts.administrator, stamp, 'turn')
    fixture.qualify()
    usable = fixture.model(f'model-turn-usable-{stamp}')

    def agent(name, model_id=None, **limits):
        return fixture.publish(fixture.save(fixture.create(f'turn {name}'), model_id or usable, **limits))

    def grant(session, agent_state):
        return session.call(f'{GRANTS}/grant', {'operationId': str(uuid4()), 'agentId': agent_state['id'], 'version': 1})
    guide = agent('guide')
    covering = grant(trader, guide)
    worker = Worker(psql, environment, query)

    def answer(output):
        return json.loads([line for line in output.splitlines() if line.strip()][-1])

    def as_person(profile, sql):
        return answer(contexts.runtime(profile, sql))

    def as_worker(sql):
        return answer(query(f'SET SESSION AUTHORIZATION preview_runtime;{sql}'))

    def issued(subject):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'{ISSUER}','{subject}','human_interactive','{contexts.tenant}','{contexts.workspace}',NULL,'preview_runtime',60);")

    def ask_sql(turn, profile='trader', operation=None, scope='api', conversation=None, agent_id=None, version=1, question='What does this page show?',
                report=None, mode='current', context=None, frame=None, generation=None, limit=4000, workspace=4, deadline=30):
        # The asking process names the catalog, the session binding, the token's digest under its worker generation and
        # the runtime set it applies: its version, the question bound, the workspace's turns and the admission deadline.
        return (f"assistant.ask_turn({literal(operation or uuid4())},'{scope}',{literal(conversation)},{literal(agent_id or guide['id'])},{version},"
                f"{literal(question)},{literal(json.dumps(REPORT if report is None else report))}::jsonb,'{catalog}','{mode}',{literal(context)},"
                f"{literal(frame)},'{'c' * 64}',{literal(profile)},clock_timestamp()+interval '30 minutes','{turn.digest}',"
                f"{worker.generation if generation is None else generation},'turns_v1',{limit},{workspace},{deadline})")

    def ask(turn, profile='trader', **request):
        asked = as_person(profile, f'SELECT {ask_sql(turn, profile, **request)}::text;')
        if 'turn' in asked:
            turn.id = asked['turn']['id']
        return asked

    def refusal(profile, code, **request):
        sql = f'PERFORM {ask_sql(Turn(), profile, **request)};'
        contexts.runtime(profile, expect(sql, code) if code == '42501' else refused(sql, code))

    def pin_sql(turn, pins=None, reserve=64, token=None):
        return (f"assistant.pin_turn('{turn.id}',{bytea(token or turn.token)},'{EFFECTIVE}',"
                f"{'NULL' if pins is None else literal(json.dumps(pins)) + '::jsonb'},{reserve})")

    def pin(turn, profile='trader', **arguments):
        return as_person(profile, f'SELECT {pin_sql(turn, **arguments)}::text;')

    def worker_sql(operation, turn, *arguments, token=None):
        return f"assistant.{operation}('{turn.id}',{bytea(token or turn.token)}{''.join(',' + argument for argument in arguments)})"

    def start(turn):
        return as_worker(f"SELECT {worker_sql('start_turn', turn)}::text;")

    def open_call(turn):
        return as_worker(f"SELECT {worker_sql('open_turn_call', turn)}::text;")

    def append(turn, chunk):
        return as_worker(f"SELECT {worker_sql('append_turn_text', turn, literal(chunk))}::text;")

    def end(turn, ending='completed', code=None):
        return as_worker(f"SELECT {worker_sql('end_turn', turn, literal(ending), literal(code))}::text;")

    def write_refused(code, operation, turn, *arguments, token=None):
        query('SET SESSION AUTHORIZATION preview_runtime;' + refused(f'PERFORM {worker_sql(operation, turn, *arguments, token=token)};', code))

    def worker_refuses(operation, turn, *arguments, token=None):
        write_refused('agent_runtime_unavailable', operation, turn, *arguments, token=token)

    def close_call(turn, opened, outcome='succeeded', counts=(120, 0, 0, 40, 0), status='recognized', token=None):
        # A response that completed names the identifier served. Answers whether this close recorded the outcome.
        served = literal(opened['identifier']) if outcome in ('succeeded', 'usage_unrecognized') else 'NULL'
        return query(f"SET SESSION AUTHORIZATION preview_runtime;SELECT assistant.close_turn_call('{opened['call']}',{bytea(token or turn.token)},"
                     f"'{outcome}',{served},'{status}',{usage(*counts)});") == 't'

    def stop(turn, profile='trader', scope='api'):
        return as_person(profile, f"SELECT assistant.stop_turn('{scope}','{turn.id}')::text;")['turn']

    def state(turn):
        return query(f"SELECT state||':'||coalesce(outcome,'-') FROM assistant.turns WHERE id='{turn.id}';")

    def kinds(turn):
        return query(f"SELECT string_agg(kind,',' ORDER BY sequence) FROM assistant.turn_events WHERE turn_id='{turn.id}';")

    def closed(call):
        return query(f"SELECT outcome||':'||usage_status FROM assistant.model_call_outcomes WHERE call_id='{call}';")

    def usage_of(turn):
        return json.loads(query(f"SELECT payload::text FROM assistant.turn_events WHERE turn_id='{turn.id}' AND kind='usage';"))

    def dimensions(turn):
        """Per usage dimension, the count its calls reported and how many calls left it unknown."""
        summary = usage_of(turn)
        return [(summary[d]['count'], summary[d]['unknownCalls']) for d in ('uncachedInput', 'cacheRead', 'cacheWrite', 'output', 'reasoning')]

    def ran(turn, profile='trader', **request):
        """An admitted, pinned and started turn."""
        asked = ask(turn, profile, **request)
        assert 'turn' in asked, asked
        pin(turn, profile)
        started = start(turn)
        assert 'started' in started, started
        return started

    def contexts_count():
        return int(query(f"SELECT count(*) FROM market_data.temporal_contexts WHERE catalog_id='{catalog}';"))

    def current(agent_state):
        return next(a for a in admin.call(AGENTS)['agents'] if a['id'] == agent_state['id'])

    def opened_after(change, turn):
        """The turn's next call in a transaction that first makes `change` and then rolls back."""
        return answer(query(f"BEGIN;{change}SET SESSION AUTHORIZATION preview_runtime;SELECT {worker_sql('open_turn_call', turn)}::text;ROLLBACK;"))

    def waits(work, operation):
        """Starts `work` in a thread and returns once it waits for a lock in `operation`, with the thread and its answer."""
        answers = {}
        thread = threading.Thread(target=lambda: answers.update(answer=work()))
        thread.start()
        waiting(query, 1, operation)
        return thread, answers
    trader_principal, trader_role = principal('trader'), query("SELECT id FROM security.roles WHERE code='trader';")
    provider_id = query(f"SELECT provider_id FROM assistant.models WHERE id='{usable}';")

    # Ask (§7.1, §8): a refused ask writes nothing and names what stops it.
    records = query(RECORDS)
    for request in ({'operation': '00000000-0000-0000-0000-000000000000'}, {'scope': 'elsewhere'}, {'version': 0}, {'question': ''},
                    {'question': ' padded'}, {'report': ['not', 'an', 'object']}, {'report': {'page': 'x' * 8200}}, {'mode': 'retained'},
                    {'context': str(uuid4())}, {'limit': 0}, {'workspace': 0}, {'deadline': 0}):
        refusal('trader', 'turn_input', **request)
    for ends in ("clock_timestamp()-interval '1 second'", "'infinity'", "'9999-12-31 00:00:00+00'"):
        contexts.runtime('trader', refused(f"PERFORM {ask_sql(Turn())};".replace("clock_timestamp()+interval '30 minutes'", ends), 'turn_input'))
    refusal('trader', 'question_too_long', question='x' * 4001)
    check('an ask names a request identity, a scope, the agent version, a question within its bound, a page report object of at most 8 KiB, '
          'the knowledge to pin, a session ending after now and before 9999-12-31 00:00 UTC, and its runtime set', query(RECORDS) == records)
    refusal('viewer', '42501')
    refusal('editor', 'agent_not_allowed')
    refusal('trader', 'agent_not_allowed', agent_id=str(uuid4()))
    refusal('trader', 'agent_not_allowed', version=2)
    draft = fixture.save(guide, usable)
    refusal('trader', 'agent_not_allowed', version=2)
    refusal(login('curator'), 'grant_permission_missing', version=2)
    refusal('risk', 'grant_required')
    refusal('trader', 'conversation_not_found', conversation=str(uuid4()))
    refusal('trader', 'agent_runtime_unavailable', generation=worker.generation - 1)
    check("an ask needs UseAgents, a version the person may use, every permission it needs, a covering grant, a conversation of theirs and "
          "the worker's current generation", query(RECORDS) == records and draft['newest']['version'] == 2)

    # A turn from ask to completion (§7.4).
    first = Turn()
    asked = ask(first)
    conversation = asked['turn']['conversationId']
    check('an admitted ask creates the conversation and the queued turn under the covering grant, with its accepted event, and is answered '
          'as admitted', asked.get('admitted') is True and asked['turn']['state'] == 'queued' and asked['turn']['ordinal'] == 1 and asked['turn']['version'] == 1 and not asked['turn']['draft']
          and query(f"SELECT delegation_id||' '||cardinality(included_turns)||' '||generation||' '||token_digest||' '||runtime_set FROM assistant.turns "
                    f"WHERE id='{first.id}';") == f"{covering['id']} 0 {worker.generation} {first.digest} turns_v1"
          and kinds(first) == 'accepted', asked)
    records = query(RECORDS)
    operation = query(f"SELECT operation_id FROM assistant.turns WHERE id='{first.id}';")
    replayed = ask(Turn(), operation=operation)
    for request in ({'question': 'Something else?'}, {'conversation': conversation}, {'scope': 'exchange'}, {'mode': 'retained', 'context': str(uuid4())}):
        refusal('trader', 'turn_conflict', operation=operation, **request)
    check('repeating the ask answers with its turn, not as admitted, and writes nothing; any other ask under its identity conflicts',
          replayed == {'turn': asked['turn']} and query(RECORDS) == records, replayed)
    write_refused('turn_state_conflict', 'start_turn', first)
    contexts.runtime('viewer', expect(f'PERFORM {pin_sql(first)};', '42501'))
    for profile, token in (('risk', None), ('trader', secrets.token_bytes(32))):
        contexts.runtime(profile, refused(f'PERFORM {pin_sql(first, token=token)};', 'agent_runtime_unavailable'))
    check('a turn starts only once pinned, and only the asking person presenting its token pins it', query(RECORDS) == records)
    before = contexts_count()
    contexts.runtime('trader', refused(f'PERFORM {pin_sql(first, reserve=4096 - before)};', 'knowledge_budget_reserved'))
    check("an ask over working data is refused while fewer than the reserve of the catalog's retained contexts would remain",
          query(RECORDS) == records and contexts_count() == before)
    pinned = pin(first, reserve=4095 - before)['turn']
    record = query(f"SELECT t.context_id||' '||(t.cutoff=c.known_at)||' '||(t.pins IS NULL) FROM assistant.turns t "
                   f"JOIN market_data.temporal_contexts c ON(c.catalog_id,c.id)=(t.catalog_id,t.context_id) WHERE t.id='{first.id}';")
    check('working data is captured once into the pinned record with its cutoff, leaving the reserve',
          pinned['context']['contextId'] == record.split()[0] and record.endswith('true true') and contexts_count() == before + 1, record)
    contexts.runtime('trader', refused(f'PERFORM {pin_sql(first)};', 'turn_state_conflict'))
    check('the pinned record is written once', contexts_count() == before + 1)
    started = start(first)
    check('the worker starts the pinned turn and is given the version, its limits, the question and no earlier turn',
          started['turn']['state'] == 'running' and started['started']['instructions'] == 'Quote only values you read.'
          and started['started']['limits'] == {'toolSteps': 8, 'inputTokens': 200000, 'outputTokens': 2048, 'elapsedSeconds': 120, 'retries': 2}
          and started['started']['modelId'] == usable and started['started']['earlier'] == [] and started['turn']['question'] == 'What does this page show?',
          started)
    write_refused('turn_state_conflict', 'start_turn', first)
    for operation_name, arguments in (('start_turn', ()), ('open_turn_call', ()), ('append_turn_text', ("'text'",)), ('end_turn', ("'completed'", 'NULL')),
                                      ('end_turn_session', ())):
        worker_refuses(operation_name, first, *arguments, token=secrets.token_bytes(32))
    check("every worker write presents the turn's token", state(first) == 'running:-')
    write_refused('turn_state_conflict', 'end_turn', first, "'completed'", 'NULL')
    opened = open_call(first)['opened']
    attestation = query(f"SELECT credential_version||' '||terms_version FROM assistant.current_attestation('{provider_id}');")
    check("a running turn's call pins the model's capability version, the attested key and terms and the price in force, as its first attempt "
          "after the conversation's latest event", opened['attempt'] == 1 and opened['identifier'] == f'model-turn-usable-{stamp}'
          and opened['maxOutputTokens'] == 2048 and str(opened['credentialVersion']) == attestation.split()[0]
          and query(f"SELECT turn_id||' '||attempt||' '||model_version||' '||credential_version||' '||terms_version||' '||price_sequence||' '||after_sequence"
                    f"||' '||(operation_id IS NULL AND actor_id IS NULL AND token_digest IS NULL) FROM assistant.model_calls WHERE id='{opened['call']}';")
          == f"{first.id} 1 {fixture.version_of(usable)} {attestation} 1 1 true", opened)
    write_refused('turn_state_conflict', 'open_turn_call', first)
    write_refused('turn_state_conflict', 'end_turn', first, "'failed'", "'limit_elapsed'")
    for chunk in ('', 'x' * 2049):
        write_refused('turn_input', 'append_turn_text', first, literal(chunk))
    check('one call at a time, no turn ends while it is open, and answer text comes in chunks of 1 to 2,048 bytes')
    query('SET SESSION AUTHORIZATION preview_runtime;' + refused(
        f"PERFORM assistant.close_test('{opened['call']}',{bytea(first.token)},'succeeded','model-turn-usable-{stamp}','recognized',{usage(1, 0, 0, 1, 0)});",
        'agent_runtime_unavailable'))
    check("a provider test's close refuses a turn's call, even with the turn's token", closed(opened['call']) == '')
    appended = append(first, 'The page shows ')
    append(first, 'test_curve.')
    records = query(RECORDS)
    for target, token in ((opened, secrets.token_bytes(32)), ({'call': str(uuid4()), 'identifier': None}, None)):
        query('SET SESSION AUTHORIZATION preview_runtime;' + refused(
            f"PERFORM assistant.close_turn_call('{target['call']}',{bytea(token or first.token)},'succeeded','model-turn-usable-{stamp}',"
            f"'recognized',{usage(120, 0, 0, 40, 0)});", 'agent_runtime_unavailable'))
    check("a close without the turn's token, or of an unknown call, is refused and writes nothing", query(RECORDS) == records)
    closes = (close_call(first, opened), close_call(first, opened, 'model_provider_transport', UNKNOWN, 'unknown'))
    ended = end(first)['turn']
    summary = usage_of(first)
    check("a completed turn keeps its answer and closes with its usage and cost, then its terminal event; a second close records nothing",
          closes == (True, False) and appended['sequence'] == 2 and ended['state'] == 'completed' and ended['answer'] == 'The page shows test_curve.'
          and not ended['incomplete'] and closed(opened['call']) == 'succeeded:recognized'
          and query(f"SELECT string_agg(kind||':'||sequence,',' ORDER BY sequence) FROM assistant.turn_events WHERE conversation_id='{conversation}';")
          == 'accepted:1,text:2,text:3,usage:4,completed:5'
          and summary['calls'] == 1 and summary['uncachedInput'] == {'count': 120, 'unknownCalls': 0} and summary['output'] == {'count': 40, 'unknownCalls': 0}
          and summary['cost'] == [{'currency': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId'], 'code': 'usd'},
                                   'amount': '0.00054288', 'unpricedCalls': 0}], summary)
    write_refused('turn_state_conflict', 'append_turn_text', first, "'more'")
    write_refused('turn_state_conflict', 'end_turn', first, "'failed'", "'limit_elapsed'")
    write_refused('turn_state_conflict', 'open_turn_call', first)
    check('an ended turn takes no further write, and Stop answers it as it ended', stop(first)['state'] == 'completed')

    # An identical ask sent while its original still runs waits for it and answers with its turn; one whose authority
    # lapses while it waits is refused at its recheck.
    original, operation = Turn(), str(uuid4())
    thread, answers = holding(psql, environment, f"SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issued('trader')}');"
                              f"SELECT {ask_sql(original, operation=operation)}", lambda: waits(lambda: ask(Turn(), operation=operation), 'ask_turn'),
                              ending='COMMIT')
    thread.join(timeout=15)
    original.id = answers['answer']['turn']['id']
    check('an identical ask sent while its original still runs waits for it and answers with its turn, not as admitted',
          query(f"SELECT count(*)||' '||max(token_digest) FROM assistant.turns WHERE operation_id='{operation}';") == f'1 {original.digest}'
          and 'admitted' not in answers['answer'], answers)
    lapsed = contexts.expiring(psql, environment, 'context', f"SELECT 1 FROM security.principals WHERE id='{trader_principal}' FOR NO KEY UPDATE",
                               f'SELECT {ask_sql(Turn(), operation=operation)};', subject='trader')
    check('a repeat whose authority lapses while it waits for its original is refused at its recheck', lapsed_in(lapsed, 'ask_turn'), lapsed)
    end(original, 'failed', 'request_cancelled')

    # A follow-up includes every earlier turn (§7.6); a conversation is its owner's, in its scope, with its agent, which an
    # ask checks before admission (§8), so its refusal never waits for the workspace's admission.
    other_guide = agent('other guide')
    grant(trader, other_guide)
    admission = f"SELECT pg_advisory_xact_lock(730849161,hashtext('{contexts.tenant}{contexts.workspace}'))"

    def unadmitted(profile, code, **request):
        contexts.runtime(profile, "SET LOCAL lock_timeout='1s';" + refused(f'PERFORM {ask_sql(Turn(), profile, **request)};', code))
    holding(psql, environment, admission, lambda: (
        unadmitted('trader', 'conversation_not_found', conversation=str(uuid4())),
        unadmitted('trader', 'conversation_agent_mismatch', conversation=conversation, agent_id=other_guide['id']),
        unadmitted('trader', 'conversation_not_found', conversation=conversation, scope='exchange'),
        unadmitted('risk', 'conversation_not_found', conversation=conversation)))
    follow = Turn()
    followed = ran(follow, conversation=conversation, question='And the cutoff?')
    check('a follow-up in the same conversation includes the earlier turn with its answer, pinned context and ending, and an unknown '
          "conversation, another person's, one in another scope or one with another agent is refused before admission",
          followed['turn']['ordinal'] == 2 and [(e['ordinal'], e['answer'], e['state']) for e in followed['started']['earlier']]
          == [(1, 'The page shows test_curve.', 'completed')] and followed['started']['earlier'][0]['context']['mode'] == 'current', followed)

    # Admission (§7.4): one active turn per person and the workspace's limit, once admission has ended what is stuck;
    # those endings stand when the ask is then refused.
    quitter = persona('quitter', ['trader'])
    for session in (risk, analyst, quitter):
        grant(session, guide)
    busy = ask(Turn(), conversation=conversation, question='Another one?')
    held = Turn()
    admitted = ask(held, 'risk')
    capped = ask(Turn(), login('analyst'), workspace=2)
    check('a person with an active turn is refused another, and an ask past the workspace limit is refused, counting every active turn',
          busy == {'refused': 'turn_active'} and 'turn' in admitted and capped == {'refused': 'workspace_turn_limit'}, (busy, capped))
    time.sleep(1.2)
    late = Turn()
    admitted = ask(late, login('analyst'), workspace=2, deadline=1)
    contexts.runtime('risk', refused(f'PERFORM {pin_sql(held)};', 'turn_state_conflict'))
    check('admission fails a turn still unpinned at the deadline with ask_incomplete, freeing its slot, and its record is then refused',
          'turn' in admitted and state(held) == 'failed:ask_incomplete' and kinds(held) == 'accepted,usage,failed' and state(follow) == 'running:-', admitted)
    quitting = Turn()
    ask(quitting, login('quitter'))
    stop(quitting, login('quitter'))
    quick = agent('quick guide', elapsedSeconds=1)
    grant(risk, quick)
    stuck = Turn()
    ran(stuck, 'risk', agent_id=quick['id'])
    time.sleep(2.2)
    kept = ask(Turn(), deadline=1)
    check('admission interrupts a turn past its elapsed limit and the deadline, fails one still unpinned and ends cancelled one whose cancel '
          'was requested before its record, and those endings stand when the ask is then refused',
          kept == {'refused': 'turn_active'} and state(stuck) == 'interrupted:-' and kinds(stuck) == 'accepted,usage,interrupted'
          and state(late) == 'failed:ask_incomplete' and state(quitting) == 'cancelled:-' and state(follow) == 'running:-', kept)

    # A turn's call (§7.5): retried only after a transport failure or overload, as the next attempt within the
    # version's retries, while its input is known and it published nothing; tools bring further calls.
    attempts = []
    for outcome in ('model_provider_transport', 'model_provider_overloaded', 'model_provider_overloaded'):
        attempts.append(open_call(follow)['opened'])
        close_call(follow, attempts[-1], outcome, NOTHING)
    write_refused('retry_not_allowed', 'open_turn_call', follow)
    write_refused('turn_state_conflict', 'end_turn', follow, "'completed'", 'NULL')
    end(follow, 'failed', 'model_provider_overloaded')
    check("a transport failure or overload is retried as the next attempt up to the version's retries, and a turn completes only after its "
          'call completed', [a['attempt'] for a in attempts] == [1, 2, 3] and state(follow) == 'failed:model_provider_overloaded')
    for outcome, counts, status in (('model_provider_rate_limited', NOTHING, 'recognized'), ('model_provider_transport', UNKNOWN, 'unknown')):
        turn = Turn()
        ran(turn)
        close_call(turn, open_call(turn)['opened'], outcome, counts, status)
        write_refused('retry_not_allowed', 'open_turn_call', turn)
        end(turn, 'failed', outcome)
    unknown = usage_of(turn)
    check('no other failure is retried, nor one whose input is unknown, whose counts stay unknown and leave the cost unpriced',
          unknown['uncachedInput'] == {'count': 0, 'unknownCalls': 1} and unknown['cost'][0]['amount'] is None and unknown['cost'][0]['unpricedCalls'] == 1,
          unknown)
    published = Turn()
    ran(published)
    opened = open_call(published)['opened']
    append(published, 'Partial ')
    close_call(published, opened, 'model_provider_transport', NOTHING)
    write_refused('turn_state_conflict', 'append_turn_text', published, "'late'")
    write_refused('retry_not_allowed', 'open_turn_call', published)
    failed = end(published, 'failed', 'model_provider_stream_interrupted')['turn']
    check('text comes only from the call in flight, a failed call that published text is not retried, and a turn failing after text '
          'keeps it, marked incomplete',
          failed['incomplete'] and failed['answer'] == 'Partial ' and failed['outcome'] == 'model_provider_stream_interrupted', failed)
    unrecognized = Turn()
    ran(unrecognized)
    close_call(unrecognized, open_call(unrecognized)['opened'], 'usage_unrecognized', (120, 0, 0, 40, 0), 'unrecognized')
    write_refused('retry_not_allowed', 'open_turn_call', unrecognized)
    completed = end(unrecognized)['turn']
    summary = usage_of(unrecognized)
    check("no call follows a response with unrecognized usage, which completes its turn, and a turn's usage never counts unrecognized "
          'usage, leaving its cost unpriced', completed['state'] == 'completed'
          and all(summary[d] == {'count': 0, 'unknownCalls': 1} for d in ('uncachedInput', 'cacheRead', 'cacheWrite', 'output', 'reasoning'))
          and summary['cost'][0]['amount'] is None, summary)
    summed = Turn()
    ran(summed)
    close_call(summed, open_call(summed)['opened'], 'model_provider_overloaded', (100, 50, 25, 0, 0))
    close_call(summed, open_call(summed)['opened'])
    write_refused('retry_not_allowed', 'open_turn_call', summed)
    end(summed)
    summary = usage_of(summed)
    check("no call follows a completed response, and a turn's usage sums each dimension over its calls and its cost per currency",
          summary['calls'] == 2 and [summary[d]['count'] for d in ('uncachedInput', 'cacheRead', 'cacheWrite', 'output')] == [220, 50, 25, 40]
          and summary['cost'][0]['amount'] == '0.000729395', summary)
    bounded_agent = agent('bounded', inputTokens=100)
    grant(trader, bounded_agent)
    bounded = Turn()
    ran(bounded, agent_id=bounded_agent['id'])
    close_call(bounded, open_call(bounded)['opened'], 'model_provider_overloaded', (60, 20, 20, 5, 0))
    close_call(bounded, open_call(bounded)['opened'], 'model_provider_transport', (0, 0, 1, 0, 0))
    over = open_call(bounded)
    check("a retry opens while the input its earlier attempts reported is within the version's limit, and once that input exceeds it "
          'the turn ends limit_input_tokens instead', 'opened' not in over and state(bounded) == 'failed:limit_input_tokens', over)
    callless = Turn()
    ran(callless)
    write_refused('turn_state_conflict', 'end_turn', callless, "'completed'", 'NULL')
    end(callless, 'failed', 'request_cancelled')
    check('a turn without a call never completes')

    # What stops a call (§7.1): a changed capability version, a price withdrawn, an unqualified model or provider.
    def subject(name):
        """A model of its own and an agent on it, granted to the trader."""
        model_id = fixture.model(f'model-turn-{name}-{stamp}')
        agent_state = agent(name, model_id)
        grant(trader, agent_state)
        return model_id, agent_state
    changing, changing_agent = subject('changing')
    changed = Turn()
    ran(changed, agent_id=changing_agent['id'])
    fixture.edit(changing)
    refused_call = open_call(changed)
    refusal('trader', 'model_changed', agent_id=changing_agent['id'])
    check("the call after the model's capability version changed opens nothing and ends the turn model_changed, and an ask is refused so",
          'opened' not in refused_call and state(changed) == 'failed:model_changed', refused_call)
    withdrawn, withdrawn_agent = subject('withdrawn')
    unpriced = Turn()
    ran(unpriced, agent_id=withdrawn_agent['id'])
    query(f"INSERT INTO assistant.price_revisions VALUES('{withdrawn}',2,'withdrawal',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,"
          f"'2020-06-01',NULL,'{contexts.administrator}',clock_timestamp());")
    refused_call = open_call(unpriced)
    refusal('trader', 'model_unpriced', agent_id=withdrawn_agent['id'])
    check('the call after its price was withdrawn opens nothing and ends the turn model_unpriced, and an ask is refused so',
          'opened' not in refused_call and state(unpriced) == 'failed:model_unpriced', refused_call)
    for setup, code in (("DELETE FROM assistant.adapter_mappings WHERE vendor='anthropic';", 'model_unqualified'),
                        (f"INSERT INTO assistant.credentials SELECT '{provider_id}',max(version)+1,'\\x{'00' * 12}','\\x{'00' * 8}','\\x{'00' * 16}','0009',"
                         f"'{contexts.administrator}',clock_timestamp() FROM assistant.credentials WHERE provider_id='{provider_id}';", 'model_provider_unqualified')):
        contexts.rolled_back(setup, 'trader', 'human_interactive', contexts.workspace, 'NULL', refused(f'PERFORM {ask_sql(Turn())};', code))
    check("an ask is refused while the model's mapping or the provider's current key is unqualified")

    # Each part of the owner's current authority (§7.1), lost on its own in a transaction that then rolls back, ends the
    # turn access_changed at its next call, though nothing requested its cancel.
    checked = Turn()
    ran(checked)
    guide_principal = query(f"SELECT principal_id FROM assistant.agents WHERE id='{guide['id']}';")
    membership = ("UPDATE security.workspace_memberships SET enabled=false WHERE(tenant_id,workspace_id,principal_id)="
                  f"('{contexts.tenant}','{contexts.workspace}','{{}}');")
    stand_in = str(uuid4())
    losses = {f'their {permission}': f"DELETE FROM security.role_permissions WHERE role_id='{trader_role}' AND permission='{permission}';"
              for permission in ('UseAgents', 'CreateDelegations', 'ReadTrades')}
    losses |= {
        'an allowed role, every permission kept': (
            f"INSERT INTO security.roles(id,code,name) VALUES('{stand_in}','synthetic_turn_stand_in','Synthetic stand-in');"
            f"INSERT INTO security.role_permissions SELECT '{stand_in}',permission FROM security.role_permissions WHERE role_id='{trader_role}';"
            f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{trader_principal}','{stand_in}','{contexts.tenant}','{contexts.workspace}',{WINDOW},false);"
            f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{trader_principal}' AND role_id='{trader_role}';"),
        'their principal': f"UPDATE security.principals SET enabled=false WHERE id='{trader_principal}';",
        'their membership': membership.format(trader_principal),
        "the grant's term": f"UPDATE security.agent_delegations SET granted_to=clock_timestamp() WHERE id=(SELECT delegation_id FROM assistant.turns WHERE id='{checked.id}');",
        "the agent's membership": membership.format(guide_principal)}
    outcomes = {loss: opened_after(change, checked) for loss, change in losses.items()}
    check('the call after its owner lost any part of their authority opens nothing and ends the turn access_changed: their UseAgents, '
          'CreateDelegations or a permission the version needs, an allowed role, their principal or membership, the term of its grant, '
          "the agent's membership", 'opened' in opened_after('', checked)
          and all('opened' not in outcome and outcome['turn']['outcome'] == 'access_changed' for outcome in outcomes.values())
          and state(checked) == 'running:-', outcomes)
    end(checked, 'failed', 'request_cancelled')

    # A draft turn runs while its owner manages agents, and a later save never fails it; nor does a later Publish fail a
    # published version's turn.
    manager = persona('manager', ['trader', 'access_administrator'])
    manager.call(f'{GRANTS}/grant', {'operationId': str(uuid4()), 'agentId': guide['id'], 'version': 2})
    drafted = Turn()
    started = ran(drafted, login('manager'), version=2)
    fixture.save(current(guide), usable)
    opened = open_call(drafted)['opened']
    close_call(drafted, opened, 'model_provider_overloaded', NOTHING)
    unmanaged = opened_after("DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='access_administrator') "
                             "AND permission='ManageAgents';", drafted)
    end(drafted, 'failed', 'request_cancelled')
    evolving = agent('evolving')
    grant(trader, evolving)
    published_turn = Turn()
    ran(published_turn, agent_id=evolving['id'])
    fixture.publish(fixture.save(current(evolving), usable))
    kept = open_call(published_turn)
    close_call(published_turn, kept['opened'], 'model_provider_overloaded', NOTHING)
    end(published_turn, 'failed', 'request_cancelled')
    check("a draft turn is admitted to a person who manages agents and runs while they do, after a later save, and a later Publish never "
          "fails a published version's turn", started['turn']['draft'] and 'opened' not in unmanaged
          and unmanaged['turn']['outcome'] == 'access_changed' and 'opened' in kept, (started['turn'], unmanaged, kept))

    # The publication recheck (§7.1): an append waits for a revocation in progress and, once it commits, publishes
    # nothing; the worker then closes its call with the usage it has and ends the turn.
    def muted(name, revocation):
        session = persona(name, ['trader'])
        granted = grant(session, guide)
        turn = Turn()
        ran(turn, login(name))
        opened = open_call(turn)['opened']
        append(turn, 'Before ')
        thread, answers = holding(psql, environment, 'SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE',
                                  lambda: waits(lambda: append(turn, 'after.'), 'append_turn_text'), ending=revocation(granted) + ';COMMIT')
        thread.join(timeout=15)
        return turn, opened, answers['answer']
    lost, opened, refusal_answer = muted('silenced', lambda granted: f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{principal(login('silenced'))}'")
    silenced = principal(login('silenced'))
    query(f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{silenced}','{trader_role}','{contexts.tenant}','{contexts.workspace}',{WINDOW},false);")
    again, unstopped = append(lost, 'after.'), stop(lost, login('silenced'))
    # The stream ended normally, and the worker asks to complete.
    closes = close_call(lost, opened, counts=(120, 0, 0, 40, None))
    ended = end(lost)['turn']
    check('an append waiting for a role revocation in progress publishes nothing once it commits and answers refused access_changed, and '
          'the turn records it: with the role granted again a later append is refused too and a Stop requests nothing, and the turn fails '
          'access_changed whatever the worker asks, keeping the text published before, marked incomplete, and the usage the call reported',
          refusal_answer.get('refused') == 'access_changed' and 'sequence' not in refusal_answer and refusal_answer['turn']['state'] == 'running'
          and again.get('refused') == 'access_changed' and 'sequence' not in again and unstopped['state'] == 'running' and closes
          and (ended['state'], ended['outcome'], ended['answer'], ended['incomplete']) == ('failed', 'access_changed', 'Before ', True)
          and dimensions(lost) == [(120, 0), (0, 0), (0, 0), (40, 0), (0, 1)], (refusal_answer, again, unstopped, ended, usage_of(lost)))
    revoked, opened, cancelled_answer = muted('revoked', lambda granted: f"UPDATE security.agent_delegations SET revoked=true WHERE id='{granted['id']}'")
    close_call(revoked, opened, 'cancelled', (120, 0, 0, None, None), 'unknown')
    ended = end(revoked, 'failed', 'model_provider_transport')['turn']
    check('an append waiting for a grant revocation in progress, which cancels its turn, publishes nothing once it commits and answers the '
          'cancel requested, and the turn ends cancelled access_changed whatever the worker reports',
          'sequence' not in cancelled_answer and cancelled_answer['turn']['state'] == 'cancel_requested'
          and (ended['state'], ended['outcome'], ended['answer']) == ('cancelled', 'access_changed', 'Before '), (cancelled_answer, ended))
    grant(persona('finishing', ['trader']), guide)
    finishing = Turn()
    ran(finishing, login('finishing'))
    opened = open_call(finishing)['opened']
    append(finishing, 'Done.')
    close_call(finishing, opened)
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{principal(login('finishing'))}';")
    finished = end(finishing)['turn']
    check('a turn whose call completed completes after its owner lost their role, as its terminal event carries no business data and '
          'skips the publication recheck', (finished['state'], finished['answer']) == ('completed', 'Done.'), finished)
    # A refused first append leaves no text to stop a retry, so the next open meets the recorded refusal first.
    grant(persona('refusing', ['trader']), bounded_agent)
    refusing = Turn()
    ran(refusing, login('refusing'), agent_id=bounded_agent['id'])
    opened = open_call(refusing)['opened']
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{principal(login('refusing'))}';")
    hidden = append(refusing, 'Hidden.')
    close_call(refusing, opened, 'model_provider_overloaded', (60, 20, 21, 0, 0))
    reopened = open_call(refusing)
    check('after a refused first append, the next open fails the turn access_changed, though the call closed retryable over the input limit',
          hidden.get('refused') == 'access_changed' and 'opened' not in reopened and state(refusing) == 'failed:access_changed', (hidden, reopened))

    # Cancellation (§7.4, §7.5): Stop and every source that cancels move a turn to cancel requested and tell the worker;
    # no content is published after it, the worker closes its call with the usage it has, and its next write ends the
    # turn cancelled.
    listener = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    listener.stdin.write('LISTEN assistant_turn_cancel;\n\\echo LISTENING\n'); listener.stdin.flush()
    while listener.stdout.readline().strip() != 'LISTENING':
        assert listener.poll() is None, listener.stderr.read()
    stopped = Turn()
    ran(stopped)
    opened = open_call(stopped)['opened']
    for profile, scope, turn_id in (('risk', 'api', stopped.id), ('trader', 'api', str(uuid4())), ('trader', 'exchange', stopped.id)):
        contexts.runtime(profile, refused(f"PERFORM assistant.stop_turn('{scope}','{turn_id}');", 'turn_not_found'))
    requested = stop(stopped)
    unpublished = append(stopped, 'More.')
    write_refused('turn_state_conflict', 'end_turn', stopped, "'completed'", 'NULL')
    write_refused('turn_state_conflict', 'start_turn', stopped)
    close_call(stopped, opened, 'cancelled', (120, 0, 0, None, None), 'unknown')
    ended = end(stopped)['turn']
    check("Stop is its owner's alone, in the scope, and requests the cancel once; no text is published after it; once the worker closed the "
          'call, its next write ends the turn cancelled without a code, whatever it asked, counting what the call reported and no more',
          requested['state'] == 'cancel_requested' and requested['cancelRequestedAt'] and 'sequence' not in unpublished
          and unpublished['turn']['state'] == 'cancel_requested' and stop(stopped) == ended and (ended['state'], ended['outcome']) == ('cancelled', None)
          and closed(opened['call']) == 'cancelled:unknown' and kinds(stopped) == 'accepted,usage,cancelled'
          and dimensions(stopped) == [(120, 0), (0, 0), (0, 0), (0, 1), (0, 1)], (ended, usage_of(stopped)))
    early = Turn()
    ask(early)
    stop(early)
    unpinned = pin(early)['turn']
    later = Turn()
    ask(later); pin(later); stop(later)
    unstarted = start(later)
    check('a record write or start that finds the cancel requested ends the turn cancelled instead',
          (unpinned['state'], unpinned['context']['contextId']) == ('cancelled', None) and 'started' not in unstarted
          and unstarted['turn']['state'] == 'cancelled', (unpinned, unstarted))
    ending = Turn()
    ran(ending)
    requested = as_worker(f"SELECT {worker_sql('end_turn_session', ending)}::text;")['turn']
    refused_call = open_call(ending)
    check("the asking session's end requests the cancel, and the next write ends the turn cancelled with session_ended",
          requested['state'] == 'cancel_requested' and 'opened' not in refused_call and state(ending) == 'cancelled:session_ended', refused_call)
    cancels = [stopped.id, early.id, later.id, ending.id]

    def cancelled_by(source, agent_id=None):
        """Runs `source` while a trader turn runs: the cancel it requests and how the worker's next call then ends the turn."""
        turn = Turn()
        ran(turn, agent_id=agent_id)
        source()
        requested = query(f"SELECT state||':'||coalesce(cancel_code,'-') FROM assistant.turns WHERE id='{turn.id}';")
        refused_call = open_call(turn)
        cancels.append(turn.id)
        return requested, f"{refused_call['turn']['state']}:{refused_call['turn']['outcome']}", 'opened' in refused_call
    _, retiring = subject('retiring')
    disabled, disabling = subject('disabling')
    raced_model, racing = subject('racing')
    observed = {'Disable of its provider': cancelled_by(lambda: fixture.provider_change('disable'))}
    refusal('trader', 'model_provider_disabled')
    fixture.provider_change('enable')
    observed["a key replacing its provider's"] = cancelled_by(fixture.replace_key)
    refusal('trader', 'model_provider_disabled')
    fixture.qualify()
    bystander = Turn()
    ran(bystander, 'risk')
    observed['Disable of its model'] = cancelled_by(
        lambda: admin.call(f'{MODELS}/disable', model_command(disabled, fixture.row_version(disabled))), disabling['id'])
    refusal('trader', 'model_disabled', agent_id=disabling['id'])
    observed['Retire of its agent'] = cancelled_by(lambda: admin.call(f'{AGENTS}/retire', {
        'operationId': str(uuid4()), 'agentId': retiring['id'], 'expectedVersion': current(retiring)['version']}), retiring['id'])
    refusal('trader', 'agent_retired', agent_id=retiring['id'])
    observed['Revoke of its grant'] = cancelled_by(lambda: trader.call(f'{GRANTS}/revoke', {'operationId': str(uuid4()), 'grantId': covering['id']}))
    refusal('trader', 'grant_required')
    grant(trader, guide)
    observed['a grant replacing its own'] = cancelled_by(lambda: grant(trader, guide))
    codes = {'Disable of its provider': 'model_provider_disabled', "a key replacing its provider's": 'model_provider_disabled',
             'Disable of its model': 'model_disabled', 'Retire of its agent': 'agent_retired', 'Revoke of its grant': 'access_changed',
             'a grant replacing its own': 'access_changed'}
    check('Disable of the provider or a key replacing its own, Disable of the model, Retire of the agent and Revoke or replacement of the '
          'grant each request the cancel of a turn they affect with their code, the next call ends it cancelled so and opens nothing, and '
          'while the cause stands an ask is refused for it',
          all(observed[name] == (f'cancel_requested:{code}', f'cancelled:{code}', False) for name, code in codes.items()), observed)
    check('a source cancels only the turns it affects', state(bystander) == 'running:-')
    end(bystander, 'failed', 'request_cancelled')
    opening = Turn()
    ran(opening, agent_id=racing['id'])
    thread, answers = holding(psql, environment, f"SELECT 1 FROM assistant.models WHERE id='{raced_model}' FOR UPDATE",
                              lambda: waits(lambda: open_call(opening), 'open_turn_call'),
                              ending=f"UPDATE assistant.models SET version=version+1,enabled=false WHERE id='{raced_model}';COMMIT")
    thread.join(timeout=15)
    cancels.append(opening.id)
    check("a call opening while its model is switched off waits for the switch, then ends the turn cancelled model_disabled",
          'opened' not in answers['answer'] and state(opening) == 'cancelled:model_disabled', answers)
    listener.stdin.write('SELECT 1;\n\\echo HEARD\n'); listener.stdin.close()
    heard = re.findall(r'Asynchronous notification "assistant_turn_cancel" with payload "([0-9a-f-]+)"', listener.stdout.read())
    listener.wait(timeout=10)
    check('the worker is told of each cancel request once, by a notification naming the turn', sorted(heard) == sorted(cancels), (heard, cancels))

    # A retained context (§7.2) is pinned as named, with an As of frame's pins, and nothing is captured.
    retained = query(f"SELECT context_id FROM assistant.turns WHERE id='{first.id}';")
    frame = str(uuid4())
    pins = {'at': '2020-12-18T12:00:00Z', 'curveContracts': {'test_curve': []}, 'frameId': frame, 'observationIds': []}
    as_of = Turn()
    ask(as_of, mode='retained', context=retained, frame=frame)
    for wrong in (None, {**pins, 'frameId': str(uuid4())}, {key: value for key, value in pins.items() if key != 'at'}, {**pins, 'extra': True},
                  {**pins, 'observationIds': {}}, {**pins, 'curveContracts': []}):
        contexts.runtime('trader', refused(f'PERFORM {pin_sql(as_of, pins=wrong)};', 'turn_input'))
    before = contexts_count()
    pinned = pin(as_of, pins=pins)['turn']
    check("a retained context is pinned as named with the As of frame's pins, capturing nothing",
          (pinned['context']['contextId'], pinned['context']['frameId']) == (retained, frame) and contexts_count() == before
          and json.loads(query(f"SELECT pins::text FROM assistant.turns WHERE id='{as_of.id}';")) == pins, pinned)
    end(as_of, 'failed', 'request_cancelled')
    plain = Turn()
    ask(plain, mode='retained', context=retained)
    contexts.runtime('trader', refused(f'PERFORM {pin_sql(plain, pins=pins)};', 'turn_input'))
    pinned = pin(plain)['turn']
    end(plain, 'failed', 'request_cancelled')
    missing = Turn()
    ask(missing, mode='retained', context=str(uuid4()))
    contexts.runtime('trader', refused(f'PERFORM {pin_sql(missing)};', 'temporal_context'))
    end(missing, 'failed', 'request_cancelled')
    guarded = Turn()
    ask(guarded, mode='retained', context=retained)
    contexts.rolled_back(f"DELETE FROM security.role_permissions WHERE role_id='{trader_role}' AND permission='ReadMarketData';", 'trader',
                         'human_interactive', contexts.workspace, 'NULL', expect(f'PERFORM {pin_sql(guarded)};', '42501'))
    end(guarded, 'failed', 'request_cancelled')
    check("pins belong only to a turn with a frame, and a retained context must be one of the catalog's, which the person may read now",
          pinned['context']['contextId'] == retained and pinned['context']['frameId'] is None)

    # Reads (§8): a person reads and marks only their own conversations, in their scope.
    def listed(profile, scope='api'):
        return as_person(profile, f"SELECT assistant.conversations('{scope}')::text;")['conversations']
    owned = set(query(f"SELECT string_agg(id::text,' ') FROM assistant.conversations WHERE owner_id='{trader_principal}' AND scope='api';").split())
    latest = query(f"SELECT e.conversation_id FROM assistant.turn_events e JOIN assistant.conversations k ON k.id=e.conversation_id "
                   f"WHERE k.owner_id='{trader_principal}' AND k.scope='api' ORDER BY e.recorded_at DESC LIMIT 1;")
    mine = listed('trader')
    check("a person's conversations are theirs in the scope, latest activity first, each with its agent and latest turn",
          {k['id'] for k in mine} == owned and mine[0]['id'] == latest and not owned & {k['id'] for k in listed('risk')}
          and next(k for k in mine if k['id'] == conversation)['latestTurn']['id'] == follow.id
          and next(k for k in mine if k['id'] == conversation)['agentName'] == f'Synthetic turn guide {stamp}', mine[0])
    last = int(query(f"SELECT max(sequence) FROM assistant.turn_events WHERE conversation_id='{conversation}';"))
    one = as_person('trader', f"SELECT assistant.conversation('api','{conversation}',3)::text;")
    check('one conversation answers its turns in order and its events after a sequence',
          [t['id'] for t in one['turns']] == [first.id, follow.id] and [e['sequence'] for e in one['events']] == list(range(4, last + 1))
          and one['events'][0]['kind'] == 'usage', one['events'][:2])
    for profile, sql in (('risk', f"PERFORM assistant.conversation('api','{conversation}',0);"),
                         ('trader', f"PERFORM assistant.conversation('exchange','{conversation}',0);"),
                         ('risk', f"PERFORM assistant.mark_read('api','{conversation}',1);")):
        contexts.runtime(profile, refused(sql, 'conversation_not_found'))
    for sql in (f"PERFORM assistant.mark_read('api','{conversation}',-1);", f"PERFORM assistant.mark_read('api','{conversation}',{last + 1});",
                "PERFORM assistant.conversations('elsewhere');", f"PERFORM assistant.conversation('elsewhere','{conversation}',0);",
                "PERFORM assistant.unfinished_turn(NULL);", f"PERFORM assistant.mark_read('elsewhere','{conversation}',1);",
                f"PERFORM assistant.stop_turn('elsewhere','{first.id}');"):
        contexts.runtime('trader', refused(sql, 'turn_input'))
    marked = [as_person('trader', f"SELECT assistant.mark_read('api','{conversation}',{through})::text;")['conversation']['readThrough']
              for through in (3, 1)]
    unread = next(k for k in listed('trader') if k['id'] == conversation)['unread']
    as_person('trader', f"SELECT assistant.mark_read('api','{conversation}',{last})::text;")
    check('a person marks their conversation read through a sequence it holds, never backward, and it is unread while later events exist',
          marked == [3, 3] and unread and not next(k for k in listed('trader') if k['id'] == conversation)['unread'], marked)
    exchanged = Turn()
    ask(exchanged, scope='exchange')
    unfinished = {scope: as_person('trader', f"SELECT assistant.unfinished_turn('{scope}')::text;")['turn'] for scope in ('api', 'exchange')}
    in_exchange = listed('trader', 'exchange')
    end(exchanged, 'failed', 'request_cancelled')
    check("each scope keeps its own conversations and unfinished turn",
          unfinished['api'] is None and unfinished['exchange']['id'] == exchanged.id and [k['id'] for k in in_exchange] == [unfinished['exchange']['conversationId']]
          and unfinished['exchange']['conversationId'] not in {k['id'] for k in listed('trader')}, unfinished)

    # The Providers directory's latest test (0064) leaves turn calls out.
    directory = as_person('admin', 'SELECT assistant.provider_directory()::text;')
    calls = f"FROM assistant.model_calls WHERE provider_id='{provider_id}' ORDER BY started_at DESC,id DESC LIMIT 1"
    latest_test = query(f"SELECT id FROM(SELECT id,started_at FROM assistant.model_calls WHERE provider_id='{provider_id}' AND turn_id IS NULL)c "
                        "ORDER BY started_at DESC,id DESC LIMIT 1;")
    check("the Providers directory shows a provider's latest test, never a later turn call",
          query(f'SELECT turn_id IS NOT NULL {calls};') == 't' and directory['tests'].get('anthropic', {}).get('id') == (latest_test or None), directory['tests'])

    # Records (§5.1) never move backward, even for the owner.
    rewriting = Turn()
    ran(rewriting)
    opened = open_call(rewriting)['opened']
    escaped = as_worker(f"SELECT {worker_sql('append_turn_text', rewriting, 'repeat(chr(1),2048)')}::text;")
    query(f"UPDATE assistant.turns SET publication_refused_at=clock_timestamp() WHERE id='{rewriting.id}';")
    for statement in (f"UPDATE assistant.turns SET answer='Changed' WHERE id='{rewriting.id}';",
                      f"UPDATE assistant.turns SET state='queued' WHERE id='{rewriting.id}';",
                      f"UPDATE assistant.turns SET publication_refused_at=NULL WHERE id='{rewriting.id}';"):
        query(expect(statement, '55000'))
    query(expect(f"UPDATE assistant.turns SET state='completed',ended_at=clock_timestamp() WHERE id='{rewriting.id}';", '23514'))
    close_call(rewriting, opened, 'cancelled', UNKNOWN, 'unknown')
    end(rewriting, 'failed', 'request_cancelled')
    check('a chunk of 2,048 bytes that escape the most fits its event, and a turn whose publication was refused fails access_changed '
          'whatever ending the worker reports', escaped['sequence'] > 0 and state(rewriting) == 'failed:access_changed')
    for statement in (f"UPDATE assistant.turn_events SET payload='{{}}' WHERE conversation_id='{conversation}' AND sequence=1;",
                      f"DELETE FROM assistant.turn_events WHERE conversation_id='{conversation}';",
                      f"UPDATE assistant.conversations SET read_through=read_through-1 WHERE id='{conversation}';",
                      f"UPDATE assistant.conversations SET agent_id='{other_guide['id']}' WHERE id='{conversation}';",
                      f"DELETE FROM assistant.conversations WHERE id='{conversation}';",
                      f"UPDATE assistant.turns SET state='running',ended_at=NULL WHERE id='{first.id}';",
                      f"UPDATE assistant.turns SET question='Changed?' WHERE id='{first.id}';",
                      f"UPDATE assistant.turns SET answer='Rewritten.' WHERE id='{first.id}';",
                      f"UPDATE assistant.turns SET ended_at=clock_timestamp() WHERE id='{first.id}';",
                      f"DELETE FROM assistant.turns WHERE id='{first.id}';"):
        query(expect(statement, '55000'))
    check("events never change, a conversation changes only what its owner read and only forward, a running turn's answer only grows, a "
          'refused publication stays recorded and its turn never completes, and a turn never moves backward or changes once ended')

    # A record write counts the reserve only after a capture in progress commits.
    reserving = Turn()
    ask(reserving)
    occupied = contexts_count()
    thread, answers = holding(psql, environment, f"SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issued('risk')}');"
                              f"SELECT security.capture_date_context('{catalog}','{EFFECTIVE}',NULL)",
                              lambda: waits(lambda: contexts.runtime('trader', refused(f'PERFORM {pin_sql(reserving, reserve=4095 - occupied)};',
                                                                                        'knowledge_budget_reserved')), 'pin_turn'), ending='COMMIT')
    thread.join(timeout=15)
    check('a record write waiting for a capture in progress counts it once committed, so the reserve holds',
          'answer' in answers and contexts_count() == occupied + 1 and state(reserving) == 'queued:-', answers)

    # An ask or record write whose authority lapses while it waits is refused at its own recheck, writing nothing.
    records, before = query(RECORDS), contexts_count()
    lapsed = contexts.expiring(psql, environment, 'context', admission, f'SELECT {ask_sql(Turn())};', held=('security.principals', 'RowShareLock'),
                               subject='trader')
    lapsed_agent = contexts.expiring(psql, environment, 'context', f"SELECT 1 FROM assistant.agents WHERE id='{guide['id']}' FOR UPDATE",
                                     f'SELECT {ask_sql(Turn())};', held=('assistant.turns', 'RowShareLock'), subject='trader')
    lapsed_pin = contexts.expiring(psql, environment, 'context', 'SELECT pg_advisory_xact_lock(730849102)', f'SELECT {pin_sql(reserving)};',
                                   held=('assistant.turns', 'RowShareLock'), subject='trader')
    unwritten = query(RECORDS) == records and contexts_count() == before
    end(reserving, 'failed', 'request_cancelled')
    retained_wait = Turn()
    ask(retained_wait, mode='retained', context=retained)
    records = query(RECORDS)
    lapsed_retained = contexts.expiring(psql, environment, 'context', f"SELECT 1 FROM assistant.turns WHERE id='{retained_wait.id}' FOR UPDATE",
                                        f'SELECT {pin_sql(retained_wait)};', subject='trader')
    check('an ask waiting for admission or for its agent and a record write waiting for its turn or to capture are refused at their own '
          'recheck once their authority lapses, writing nothing',
          lapsed_in(lapsed, 'ask_turn') and lapsed_in(lapsed_agent, 'ask_turn') and lapsed_in(lapsed_pin, 'pin_turn')
          and lapsed_in(lapsed_retained, 'pin_turn') and unwritten and query(RECORDS) == records, (lapsed, lapsed_agent, lapsed_pin, lapsed_retained))
    end(retained_wait, 'failed', 'request_cancelled')

    # Recovery (§7.4): a worker start waits for a worker write in flight, then interrupts every turn an earlier
    # generation left queued, running or cancel requested, closing its open call.
    raced, running, stopping = Turn(), Turn(), Turn()
    ask(raced)
    ran(running, login('analyst'))
    opened = open_call(running)['opened']
    append(running, 'Early ')
    ran(stopping, 'risk')
    stop(stopping, 'risk')

    def race():
        thread, answers = waits(lambda: pin(raced), 'pin_turn')
        worker.stop()
        successor = Worker(psql, environment, query, start=False)
        successor.send('SELECT assistant.start_worker();')
        waiting(query, 1, 'assistant.start_worker')
        return thread, answers, successor
    thread, answers, successor = holding(psql, environment, 'SELECT pg_advisory_xact_lock(730849102)', race)
    thread.join(timeout=15)
    successor.generation = int(successor.receive())
    worker = successor
    interrupted = json.loads(query(f"SELECT row_to_json(t)::text FROM assistant.turns t WHERE id='{running.id}';"))
    check('a worker start waits for a write in flight, then interrupts every turn an earlier generation left unfinished, once, closing its '
          'open call and keeping its published text marked incomplete',
          answers['answer']['turn']['context']['contextId'] and state(raced) == 'interrupted:-'
          and (interrupted['state'], interrupted['answer'], interrupted['incomplete']) == ('interrupted', 'Early ', True)
          and closed(opened['call']) == 'interrupted:unknown' and state(stopping) == 'interrupted:-'
          and kinds(running) == 'accepted,text,usage,interrupted', interrupted)
    worker_refuses('end_turn', running, "'failed'", "'limit_elapsed'")
    refusal('trader', 'agent_runtime_unavailable', generation=worker.generation - 1)
    orphan = Turn()
    ran(orphan)
    worker.stop()
    worker_refuses('open_turn_call', orphan)
    refusal('risk', 'agent_runtime_unavailable')
    check('while no session holds the worker lock, no ask or worker write is accepted', state(orphan) == 'running:-')
    worker = Worker(psql, environment, query)
    check("the next worker interrupts that turn", state(orphan) == 'interrupted:-')
    worker.stop()
    print(f'{passed} turn checks passed.', flush=True)
    return passed
