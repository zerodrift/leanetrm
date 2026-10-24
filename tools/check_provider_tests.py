"""Provider Test records: exact cost, opening and closing a test under the agent worker's generation, and their
recovery; owned synthetic preview only. The checks take the agent-worker lock over from whichever session holds it,
as another preview process would, and give it up at the end."""
import hashlib
import json
import secrets
import subprocess
import time
from uuid import uuid4
from assistant_context import ISSUER, Contexts, check_guard, expect, holding, refused, refused_at, state, waiting
from check_model_qualification import command as provider_command, terms
from check_model_versions import capabilities

MODELS, PROVIDERS = 'settings/ai/models', 'settings/ai/providers'
EMPTY = '00000000-0000-0000-0000-000000000000'
KEY = 'sk-synthetic-provider-test-key-Zq7v'
# Synthetic rates per million tokens, as the catalog checks use; no vendor's real price ships (D150).
RATES = '1.234,0.0123,2.5,9.87'
LOCK = 730849160
HOLDERS = f"SELECT pid FROM pg_locks WHERE locktype='advisory' AND classid=0 AND objid={LOCK} AND objsubid=1 AND granted"
RECORDS = state('model_calls', 'model_call_outcomes')


def released(query):
    """Returns once no session holds the agent-worker lock; a backend lets go of it after its client has gone."""
    deadline = time.monotonic() + 5
    while query(f'SELECT count(*) FROM({HOLDERS})h;') != '0':
        assert time.monotonic() < deadline, 'The agent-worker lock was not released'
        time.sleep(.05)


def check_upgrade(query, migration):
    check_guard(query, migration, '0062', 'provider_tests',
                (("ALTER FUNCTION assistant.price_in_force(uuid,timestamp with time zone) SET work_mem='64kB';",
                  'assistant.price_in_force(uuid,timestamp with time zone)'),
                 ('GRANT EXECUTE ON FUNCTION assistant.model_qualified(uuid) TO openetrm_runtime;', 'assistant.model_qualified(uuid)')))


def check_start_upgrade(query, migration):
    check_guard(query, migration, '0065', 'worker_start_time',
                (("ALTER FUNCTION assistant.start_worker() SET work_mem='64kB';", 'assistant.start_worker()'),
                 ('GRANT EXECUTE ON FUNCTION assistant.holds_worker_lock(integer) TO openetrm_runtime;', 'assistant.holds_worker_lock(integer)')))


def check_directory_upgrade(query, migration):
    check_guard(query, migration, '0064', 'directory_tests',
                (("ALTER FUNCTION assistant.current_attestation(uuid) SET work_mem='64kB';", 'assistant.current_attestation(uuid)'),
                 ("ALTER FUNCTION assistant.test_state(uuid) SET work_mem='64kB';", 'assistant.test_state(uuid)'),
                 ("ALTER FUNCTION assistant.provider_directory() SET work_mem='64kB';", 'assistant.provider_directory()'),
                 ('GRANT EXECUTE ON FUNCTION assistant.provider_state(uuid) TO openetrm_runtime;', 'assistant.provider_state(uuid)')))


class Worker:
    """A session holding the agent-worker lock, as another preview process would: it ends the current holder's
    connection, takes the lock and, unless told not to, starts the next generation."""

    def __init__(self, psql, environment, query, start=True):
        self.query = query
        query(f'SELECT pg_terminate_backend(pid) FROM({HOLDERS})h;')
        released(query)
        self.process = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.run('SET SESSION AUTHORIZATION preview_runtime;')
        assert self.run(f'SELECT pg_try_advisory_lock({LOCK});') == 't', 'Another session took the agent-worker lock'
        self.pid = int(self.run('SELECT pg_backend_pid();'))
        self.generation = int(self.run('SELECT assistant.start_worker();')) if start else None

    def run(self, sql):
        self.send(sql)
        return self.receive()

    def send(self, sql):
        self.process.stdin.write(f'{sql}\n\\echo WORKER_DONE\n')
        self.process.stdin.flush()

    def receive(self):
        lines = []
        while (line := self.process.stdout.readline()).strip() != 'WORKER_DONE':
            assert line, self.process.stderr.read()
            lines.append(line.strip())
        return '\n'.join(lines)

    def stop(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            self.process.wait(timeout=10)
        released(self.query)


def bytea(value):
    """`value` as SQL that reads the same whatever the session's standard_conforming_strings."""
    return f"decode('{value.hex()}','hex')"


def usage(*dimensions):
    """close_test's ten usage arguments: per dimension a count, 'nb' for not billed, or None for unknown."""
    return ','.join('NULL,\'unknown\'' if d is None else "0,'not_billed'" if d == 'nb' else f"{d},'reported'" for d in dimensions)


def run_checks(query, psql, manifest, environment, admin):
    """`admin` is a session of the preview administrator, shared with the other modules."""
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    for table in ('worker_generation', 'model_calls', 'model_call_outcomes'):
        contexts.runtime('admin', expect(f'PERFORM 1 FROM assistant.{table};', '42501'))
    check('the runtime reads worker generations, model calls and their outcomes only through the worker and Test operations')
    check('the directory and the worker start keep the owner and grants their guards verified through 0064 and 0065',
          query("SELECT count(*) FROM pg_proc p WHERE p.oid IN('assistant.provider_directory()'::regprocedure,'assistant.start_worker()'::regprocedure)"
                " AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND cardinality(p.proacl)=2"
                " AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']::aclitem[];") == '2')

    # token_cost_v1 over revisions built for the check, never stored: exact rates per million tokens, and an
    # optional tier that prices every dimension once uncached input, cache read and cache write exceed it.
    def cost(rates, counts, tier='NULL,NULL,NULL,NULL,NULL'):
        revision = (f"ROW(gen_random_uuid(),1,'rates',gen_random_uuid(),gen_random_uuid(),'usd',{rates},{tier},now(),NULL,"
                    'gen_random_uuid(),now())::assistant.price_revisions')
        return query(f"SELECT coalesce(assistant.token_cost_v1({revision},{counts})::text,'unpriced');")

    tier, partial_rates = '5000,2.468,0.0246,5,19.74', '1.111,0.0111,NULL,7.77'
    check('token_cost_v1 prices each dimension exactly at its rate per million tokens',
          cost(RATES, '1000,2000,3000,4000') == '0.0482386' and cost(RATES, '0,0,0,0') == '0')
    check('a prompt at the tier threshold keeps the first rate set, and output never counts toward it',
          cost(RATES, '1000,2000,2000,100', tier) == '0.0072456' and cost(RATES, '1000,2000,2000,999999', tier) == '9.87624873')
    check('cache tokens can carry a prompt over the tier threshold, and then the tier prices every dimension, output included',
          cost(RATES, '1000,2000,2001,100', tier) == '0.0144962')
    check('tokens in a dimension that is not offered leave the call unpriced, and none there cost nothing, with and without a tier',
          cost(partial_rates, '500,0,0,50') == '0.000944' and cost(partial_rates, '500,0,1,50') == 'unpriced'
          and cost(partial_rates, '50,51,0,10', '100,2.222,0.0222,NULL,15.54') == '0.0002676322'
          and cost(partial_rates, '50,51,1,10', '100,2.222,0.0222,NULL,15.54') == 'unpriced')
    check('an unknown count never yields a partial cost', cost(RATES, 'NULL,2000,3000,4000') == 'unpriced')
    check('costs are exact to 18 places at any magnitude, so a nonzero cost is never zero',
          cost('0.000000000001,0,0,1', '1,0,0,0') == '0.000000000000000001'
          and cost('0.000000000001,0,0,999999999999.999999999999', '1,0,0,1000000000000') == '999999999999999999.999999000000000001')

    query('SET SESSION AUTHORIZATION preview_runtime;' + refused('PERFORM assistant.start_worker();', 'agent_runtime_unavailable'))
    check('a session without the agent-worker lock cannot start a worker generation')
    before = int(query('SELECT generation FROM assistant.worker_generation;'))
    worker = Worker(psql, environment, query)
    check('the session holding the agent-worker lock starts the next generation and is recorded as its worker',
          worker.generation == before + 1 and query('SELECT worker_pid FROM assistant.worker_generation;') == str(worker.pid))
    check('no other session takes the agent-worker lock while it is held',
          query(f'SET SESSION AUTHORIZATION preview_runtime;SELECT pg_try_advisory_lock({LOCK});') == 'f')
    query('SET SESSION AUTHORIZATION preview_runtime;' + refused('PERFORM assistant.start_worker();', 'agent_runtime_unavailable'))
    check('nor does another session start a generation while it is held')
    for statement in ('UPDATE assistant.worker_generation SET generation=generation+2;', 'UPDATE assistant.worker_generation SET generation=generation;',
                      'DELETE FROM assistant.worker_generation;'):
        query(expect(statement, '55000'))
    check('even for the owner, the worker generation advances one at a time and is never removed')

    admin.call('admin/create', {'operationId': str(uuid4()), 'login': 'provider_tester', 'name': 'Synthetic provider tester',
                                'kind': 'human', 'roles': ['access_administrator']})
    tester_id = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='provider_tester';")

    def provider():
        return next(p for p in admin.call(PROVIDERS)['providers'] if p['vendor'] == 'anthropic')

    def requalify():
        recorded = admin.call(f'{PROVIDERS}/terms', terms('anthropic', provider()['version']))
        return admin.call(f'{PROVIDERS}/attest', provider_command('anthropic', recorded['version']))

    check('the earlier checks left the Messages provider with a key', provider()['credential'] is not None)
    attested = requalify()

    def add_model(identifier, **facts):
        added = admin.call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': identifier,
                                             'displayName': 'Synthetic tested model'})
        if facts:
            admin.call(f'{MODELS}/edit', capabilities(added['id'], 1, **facts))
        return added['id']

    usd = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','usd'"

    def price(model, sequence, rates=RATES, tier='NULL,NULL,NULL,NULL,NULL', starts='2020-01-01', ends='NULL'):
        currency = usd if rates != 'withdrawal' else 'NULL,NULL,NULL'
        kind, rates = ('withdrawal', 'NULL,NULL,NULL,NULL') if rates == 'withdrawal' else ('rates', rates)
        query(f"INSERT INTO assistant.price_revisions VALUES('{model}',{sequence},'{kind}',{currency},{rates},{tier},'{starts}',{ends},"
              f"'{contexts.administrator}',clock_timestamp());")

    tested = add_model('model-synthetic-tested', reasoningEfforts=['low', 'high'], acceptedServed=['model-synthetic-tested-served'])
    price(tested, 1)
    small = add_model('model-synthetic-small', outputLimit=100, reasoningEfforts=['low'])
    price(small, 1, starts='2020-01-01', ends="'2021-01-01'")
    price(small, 2, rates='0.617,0.0062,1.25,4.935', starts='2021-01-01')
    price(small, 3, rates='7.777,0.0777,7.777,7.777', starts='2099-01-01')
    unversioned = add_model('model-synthetic-unversioned')
    price(unversioned, 1)
    unpriced = add_model('model-synthetic-unpriced', reasoningEfforts=['low'])
    withdrawn = add_model('model-synthetic-withdrawn', reasoningEfforts=['low'])
    price(withdrawn, 1)
    price(withdrawn, 2, rates='withdrawal', starts='2020-06-01')
    scheduled = add_model('model-synthetic-scheduled', reasoningEfforts=['low'])
    price(scheduled, 1, starts='2099-01-01')
    lapsed = add_model('model-synthetic-lapsed', reasoningEfforts=['low'])
    price(lapsed, 1, ends="'2021-01-01'")
    partial = add_model('model-synthetic-partial', reasoningEfforts=['low'])
    price(partial, 1, rates=partial_rates)
    tiered = add_model('model-synthetic-tiered', reasoningEfforts=['low'])
    revised = add_model('model-synthetic-revised', reasoningEfforts=['low', 'high'])
    admin.call(f'{MODELS}/edit', capabilities(revised, 2, reasoningEfforts=['high'], outputLimit=128))
    price(revised, 1)
    price(tiered, 1, tier=tier)

    token = secrets.token_bytes(32)
    digest = hashlib.sha256(token).hexdigest()

    def open_sql(model, operation=None, reasoning="'low'", limit='256', key_digest=None, generation=None):
        return (f"assistant.open_test({operation or repr(str(uuid4()))},{model if model == 'NULL' else repr(model)},{reasoning},{limit},"
                f"{key_digest or repr(digest)},{worker.generation if generation is None else generation})")

    def opened(model, profile='admin', **arguments):
        return json.loads(contexts.runtime(profile, f'SELECT {open_sql(model, **arguments)}::text;').splitlines()[-1])

    def refusal(model, code, profile='admin', **arguments):
        contexts.runtime(profile, refused(f'PERFORM {open_sql(model, **arguments)};', code))
        return True

    def close(call, outcome="'succeeded'", served="'model-synthetic-tested'", status="'recognized'", counts=usage(1000, 2000, 3000, 4000, 500),
              with_token=None):
        presented = 'NULL' if with_token == 'NULL' else bytea(with_token or token)
        return query('SET SESSION AUTHORIZATION preview_runtime;'
                     f"SELECT assistant.close_test('{call}',{presented},{outcome},{served},{status},{counts});")

    def record(call):
        return json.loads(query(f"SELECT assistant.test_state('{call}')::text;"))

    unchanged = query(RECORDS)
    check('a test needs a request identity, a model, an output limit of 1 to 10,000,000 tokens and a token digest',
          refusal(tested, 'model_admin_input', operation='NULL') and refusal(tested, 'model_admin_input', operation=repr(EMPTY))
          and refusal('NULL', 'model_admin_input') and all(refusal(tested, 'model_admin_input', limit=limit) for limit in ('0', '10000001', 'NULL'))
          and all(refusal(tested, 'model_admin_input', key_digest=d) for d in ('NULL', "'abc'", repr(digest.upper()))))
    check("a new test without a generation, or under any but the worker's current one, is refused before anything about the model",
          refusal(tested, 'agent_runtime_unavailable', generation='NULL')
          and refusal(tested, 'agent_runtime_unavailable', generation=worker.generation - 1)
          and refusal(tested, 'agent_runtime_unavailable', generation=worker.generation + 1)
          and refusal(unversioned, 'agent_runtime_unavailable', generation=worker.generation - 1))
    contexts.second_workspace(refused(f'PERFORM {open_sql(tested)};', 'model_admin_conflict'))
    check("an unknown model is a conflict, and so is another workspace's", refusal(str(uuid4()), 'model_admin_conflict'))
    check('a model without a capability version cannot be tested, whatever else is wrong', refusal(unversioned, 'model_unqualified', reasoning='NULL'))
    check("reasoning must be one of the model version's efforts", all(refusal(tested, 'parameter_required', reasoning=r) for r in ('NULL', "'medium'", "'turbo'")))
    check('a model without a rate set in force at the start is unpriced: none recorded, withdrawn, not yet in force or ended',
          all(refusal(model, 'model_unpriced') for model in (unpriced, withdrawn, scheduled, lapsed))
          and refusal(unpriced, 'parameter_required', reasoning="'medium'"))
    unattested = admin.call(f'{PROVIDERS}/terms', terms('anthropic', attested['version']))
    check('a model on a provider whose current key and terms are unattested cannot be tested, before its parameters are checked, '
          'and an unqualified model is refused as that first',
          not unattested['qualified'] and refusal(tested, 'model_provider_unqualified') and refusal(tested, 'model_provider_unqualified', reasoning="'turbo'")
          and refusal(unversioned, 'model_unqualified'))
    attested = admin.call(f'{PROVIDERS}/attest', provider_command('anthropic', unattested['version']))
    contexts.runtime('viewer', expect(f'PERFORM {open_sql(tested)};', '42501'))
    contexts.delegated_agent(expect(f'PERFORM {open_sql(tested)};', '42501'))
    query('SET SESSION AUTHORIZATION preview_runtime;' + expect(f'PERFORM {open_sql(tested)};', '42501'))
    check('the database itself refuses a test to a viewer, to an agent under a current delegation and to a session without a request context')
    check('refused tests record nothing', query(RECORDS) == unchanged)

    operation = str(uuid4())
    first = opened(tested, operation=repr(operation))['opened']
    call = first['call']
    stored = json.loads(query(f"SELECT row_to_json(c)::text FROM assistant.model_calls c WHERE id='{call}';"))
    provider_id = query(f"SELECT provider_id FROM assistant.models WHERE id='{tested}';")
    credential = json.loads(query("SELECT json_build_object('version',version,'nonce',encode(nonce,'hex'),'ciphertext',encode(ciphertext,'hex'),"
                                  f"'tag',encode(tag,'hex'))::text FROM assistant.credentials WHERE provider_id='{provider_id}' ORDER BY version DESC LIMIT 1;"))
    check("a test opens in the administrator's context and returns what the handler sends: the identifier, the accepted served "
          "identifiers, the mapping, its parameters and the attested key, sealed",
          first == {'call': call, 'identifier': 'model-synthetic-tested', 'acceptedServed': ['model-synthetic-tested-served'],
                    'mapping': 'messages_v1', 'reasoning': 'low', 'maxOutputTokens': 256, 'provider': provider_id, 'vendor': 'anthropic',
                    'credentialVersion': credential['version'], 'nonce': credential['nonce'], 'ciphertext': credential['ciphertext'], 'tag': credential['tag']})
    check('the record pins the current capability version, the attested key and terms, the price in force at its start, the cost method, '
          'the worker generation, the administrator and the token digest',
          (stored['operation_id'], stored['actor_id'], stored['token_digest'], stored['generation'], stored['model_version'],
           stored['credential_version'], stored['terms_version'], stored['price_sequence'], stored['cost_method'], stored['reasoning'],
           stored['max_output_tokens'])
          == (operation, contexts.administrator, digest, worker.generation, 1, attested['attestation']['credentialVersion'],
              attested['attestation']['termsVersion'], 1, 'token_cost_v1', 'low', 256))
    limited = opened(small)['opened']
    check("the output limit is the test's own unless the model's is lower, and the pin is the highest revision in force at the start",
          limited['maxOutputTokens'] == 100
          and query(f"SELECT price_sequence FROM assistant.model_calls WHERE id='{limited['call']}';") == '2')
    other_token = secrets.token_bytes(32)
    foreign = opened(tested, key_digest=repr(hashlib.sha256(other_token).hexdigest()))['opened']['call']
    current = opened(revised, reasoning="'high'")['opened']
    check("a test pins the model's current capability version, under its efforts and output limit",
          current['maxOutputTokens'] == 128 and refusal(revised, 'parameter_required', reasoning="'low'")
          and query(f"SELECT model_version FROM assistant.model_calls WHERE id='{current['call']}';") == '2')
    check('an open test is in progress: a retry opens nothing',
          refusal(tested, 'test_in_progress', operation=repr(operation)) and query(f"SELECT count(*) FROM assistant.model_calls WHERE operation_id='{operation}';") == '1')
    check('a test request identity cannot be reused for another model, other reasoning or by another administrator',
          refusal(small, 'model_admin_conflict', operation=repr(operation)) and refusal(tested, 'model_admin_conflict', operation=repr(operation), reasoning="'high'")
          and refusal(tested, 'model_admin_conflict', profile='provider_tester', operation=repr(operation)))

    # A revision appended while the test is open covers its start, yet the call keeps its pin. Later tests pin it.
    price(tested, 2, rates='12.34,0.123,25,98.7')
    opened_records = query(RECORDS)
    for target, presented in ((call, bytea(secrets.token_bytes(32))), (call, 'NULL'), (call, bytea(bytes([token[0] ^ 1]) + token[1:])),
                              (foreign, bytea(token)), (str(uuid4()), bytea(token))):
        query('SET SESSION AUTHORIZATION preview_runtime;' + refused(
            f"PERFORM assistant.close_test('{target}',{presented},'succeeded','model-synthetic-tested','recognized',{usage(1, 1, 1, 1, 1)});",
            'agent_runtime_unavailable'))
    check("a close without the test's own token, or for an unknown call, is refused and writes nothing", query(RECORDS) == opened_records)
    close(foreign, with_token=other_token)
    check("each test's own token closes it", record(foreign)['outcome'] == 'succeeded')
    close(call)
    closed = record(call)
    check('the token closes the test without a request context, recording its outcome, served identifier and usage by dimension',
          (closed['outcome'], closed['served'], closed['usage']['status'], closed['mapping'])
          == ('succeeded', 'model-synthetic-tested', 'recognized', 'messages_v1')
          and [closed['usage'][d] for d in ('uncachedInput', 'cacheRead', 'cacheWrite', 'output', 'reasoning')]
          == [{'count': n, 'source': 'reported'} for n in (1000, 2000, 3000, 4000, 500)]
          and closed['closedAt'] >= closed['startedAt'] and closed['testedBy'] and closed['operationId'] == operation)
    check('the cost is exact under the revision pinned at the start, never one appended while the test was open',
          closed['priceSequence'] == 1 and closed['cost'] == {'method': 'token_cost_v1', 'status': 'priced', 'amount': '0.0482386',
                                                             'currency': {'catalogId': manifest['referenceCatalogId'],
                                                                          'revisionId': manifest['referenceRevisionId'], 'code': 'usd'}})
    close(call, outcome="'model_provider_transport'", served='NULL', status="'unknown'", counts=usage(None, None, None, None, None))
    check('a test closes once: a later close changes nothing', record(call) == closed)
    check('a retry of a closed test returns its outcome and opens nothing, with or without a worker generation',
          opened(tested, operation=repr(operation)) == {'test': closed} == opened(tested, operation=repr(operation), generation='NULL')
          and query(f"SELECT count(*) FROM assistant.model_calls WHERE operation_id='{operation}';") == '1')

    def outcome(model, served="'model-synthetic-tested'", **closing):
        test = opened(model)['opened']['call']
        close(test, served=served, **closing)
        return record(test)

    accepted = outcome(tested, served="'model-synthetic-tested-served'")
    unrecognized = outcome(tested, outcome="'usage_unrecognized'", status="'unrecognized'", counts=usage(1000, 2000, 3000, None, None))
    unknown = outcome(tested, outcome="'limit_elapsed'", served='NULL', status="'unknown'", counts=usage(None, None, None, None, None))
    tier_unknown = outcome(tiered, outcome="'request_cancelled'", served='NULL', status="'unknown'", counts=usage(None, 2000, 3000, None, None))
    not_offered = outcome(partial, served="'model-synthetic-partial'", counts=usage(500, 0, 5, 50, None))
    nothing_sent = outcome(tested, outcome="'model_provider_transport'", served='NULL', counts=usage('nb', 'nb', 'nb', 'nb', 'nb'))
    mismatch = outcome(tested, outcome="'model_mismatch'", served="'model-synthetic-other'", status="'unrecognized'", counts=usage(None, None, None, None, None))
    check('a served identifier the version accepts is priced like the requested one, and reasoning the vendor does not report stays unknown',
          accepted['cost']['status'] == 'priced' and accepted['served'] == 'model-synthetic-tested-served'
          and not_offered['usage']['reasoning'] == {'count': None, 'source': 'unknown'})
    check('unrecognized and unknown usage store no cost, never a partial one, with and without a tier',
          [(r['cost']['status'], r['cost']['amount']) for r in (unrecognized, unknown, tier_unknown, mismatch)]
          == [('usage_unrecognized', None), ('usage_unknown', None), ('usage_unknown', None), ('usage_unrecognized', None)])
    check('tokens in a dimension the pinned revision does not offer store no cost',
          (not_offered['cost']['status'], not_offered['cost']['amount']) == ('cost_unpriced', None))
    check('a test that reached no vendor bills nothing it did not send', nothing_sent['cost']['amount'] == '0'
          and [nothing_sent['usage'][d] for d in ('uncachedInput', 'output')] == [{'count': 0, 'source': 'not_billed'}] * 2)
    check("a served identifier the version neither requests nor accepts is a mismatch, whose usage is unrecognized",
          (mismatch['outcome'], mismatch['served'], mismatch['usage']['status']) == ('model_mismatch', 'model-synthetic-other', 'unrecognized'))

    # Owner-level rules for outcomes, tried against one open call; every attempt is refused and it stays open. The cost
    # trigger runs before the table's own checks, so each attempt states the cost token_cost_v1 gives for its counts
    # unless the cost is what it breaks.
    pending = opened(tested)['opened']['call']

    def outcome_row(**changes):
        values = {'call_id': f"'{pending}'", 'closed_at': 'clock_timestamp()', 'outcome': "'succeeded'", 'served': "'model-synthetic-tested'",
                  'usage_status': "'recognized'", 'uncached_input': '1000', 'uncached_input_source': "'reported'", 'cache_read': '2000',
                  'cache_read_source': "'reported'", 'cache_write': '3000', 'cache_write_source': "'reported'", 'output': '4000',
                  'output_source': "'reported'", 'reasoning': '500', 'reasoning_source': "'reported'", 'cost': '0.482386', **changes}
        return f"INSERT INTO assistant.model_call_outcomes({','.join(values)}) VALUES({','.join(values.values())});"
    query(f'BEGIN;{outcome_row()}ROLLBACK;')
    for statement in (outcome_row(uncached_input='NULL', cost='NULL'), outcome_row(reasoning='NULL'), outcome_row(output_source="'unknown'"),
                      outcome_row(cache_read_source="'not_billed'"), outcome_row(uncached_input='-1', cost='0.47003366'),
                      outcome_row(output_source="'estimated'"), outcome_row(usage_status="'partial'", outcome="'limit_elapsed'", cost='NULL'),
                      outcome_row(uncached_input='NULL', uncached_input_source="'unknown'", cost='NULL'),
                      outcome_row(reasoning='4001'), outcome_row(usage_status="'unrecognized'", outcome="'succeeded'", cost='NULL'),
                      outcome_row(usage_status="'unrecognized'", outcome="'usage_unrecognized'"),
                      outcome_row(outcome="'model_mismatch'", served="'model-synthetic-other'"), outcome_row(outcome="'model_refused'", usage_status="'recognized'", served="'model-synthetic-other'"),
                      outcome_row(outcome="'model_mismatch'", served="'bad identifier'", usage_status="'unrecognized'", cost='NULL'),
                      outcome_row(outcome="'model_mismatch'", served="'model-synthetic-tested'", usage_status="'unrecognized'", cost='NULL'),
                      outcome_row(served='NULL'), outcome_row(outcome="'model_overjoyed'"),
                      outcome_row(cost='0.482385'), outcome_row(cost='NULL'), outcome_row(cost='0.4823860')):
        query(expect(statement, '23514'))
    query(expect(f"INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,usage_status,uncached_input_source,cache_read_source,"
                 f"cache_write_source,output_source,reasoning_source) VALUES('{uuid4()}',now(),'interrupted','unknown','unknown','unknown','unknown',"
                 "'unknown','unknown');", '23503'))
    check('even for the owner, an outcome keeps each count and its source together, recognized usage knows every priced dimension and '
          'no more reasoning than output, a success is recognized and served, only a mismatch names an identifier the version does not accept, '
          'and its cost is exactly token_cost_v1 of its pinned revision, or none when it cannot be priced',
          query(f"SELECT count(*) FROM assistant.model_call_outcomes WHERE call_id='{pending}';") == '0')

    def call_row(**changes):
        values = {'id': 'gen_random_uuid()', 'tenant_id': f"'{contexts.tenant}'", 'workspace_id': f"'{contexts.workspace}'",
                  'operation_id': 'gen_random_uuid()', 'actor_id': f"'{contexts.administrator}'", 'token_digest': repr(digest),
                  'generation': str(worker.generation), 'provider_id': repr(provider_id), 'credential_version': str(stored['credential_version']),
                  'terms_version': str(stored['terms_version']), 'model_id': repr(tested), 'model_version': '1', 'reasoning': "'low'",
                  'max_output_tokens': '256', 'price_sequence': '1', 'cost_method': "'token_cost_v1'", 'started_at': 'clock_timestamp()', **changes}
        return f"INSERT INTO assistant.model_calls({','.join(values)}) VALUES({','.join(values.values())});"
    query(f'BEGIN;{call_row()}ROLLBACK;')
    for statement, code in ((call_row(reasoning="'medium'"), '23514'), (call_row(max_output_tokens='64001'), '23514'),
                            (call_row(model_id=repr(withdrawn), price_sequence='2'), '23514'), (call_row(cost_method="'token_cost_v2'"), '23514'),
                            (call_row(token_digest="'abc'"), '23514'), (call_row(generation='0'), '23514'), (call_row(model_version='2'), '23503'),
                            (call_row(terms_version='999'), '23503'), (call_row(price_sequence='9'), '23503'),
                            (call_row(workspace_id=repr(str(uuid4()))), '23503')):
        query(expect(statement, code))
    check("even for the owner, a call names an effort its version accepts, an output limit within the version's, a rate set of its own model, "
          'the attested key and terms and its own model version')
    for table in ('model_calls', 'model_call_outcomes'):
        for statement in (f'UPDATE assistant.{table} SET call_id=call_id;' if table == 'model_call_outcomes' else f'UPDATE assistant.{table} SET id=id;',
                          f'DELETE FROM assistant.{table};'):
            query(expect(statement, '55000'))
    check('model calls and their outcomes are immutable')

    # A test whose administrator loses the permission while it runs still records its usage; the token, not the
    # administrator's authority, closes it.
    revoked = opened(tested, profile='provider_tester')['opened']['call']
    saved = query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to))::text FROM security.role_grants WHERE principal_id='{tester_id}';")
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp() WHERE principal_id='{tester_id}';")
    try:
        close(revoked)
        contexts.runtime('provider_tester', expect(f'PERFORM {open_sql(tested)};', '42501'))
        check('a test whose administrator lost the permission mid-test still records its usage and cost',
              record(revoked)['outcome'] == 'succeeded' and record(revoked)['cost']['amount'] == '0.482386')
    finally:
        for grant in json.loads(saved):
            query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")

    # Recovery: a later worker closes every test an earlier generation left open, once, as interrupted with unknown usage.
    abandoned_operation = str(uuid4())
    abandoned = opened(tested, operation=repr(abandoned_operation))['opened']['call']
    finished = record(call)
    worker.stop()
    check("once its worker's connection ends, a generation opens no more tests", refusal(tested, 'agent_runtime_unavailable'))
    holder = Worker(psql, environment, query, start=False)
    try:
        check('nor does it once another session holds the lock without starting a generation', refusal(tested, 'agent_runtime_unavailable'))
    finally:
        holder.stop()
    worker = Worker(psql, environment, query)
    interrupted = record(abandoned)
    check('the next worker closes each test an earlier generation left open as interrupted, with every dimension unknown and no cost',
          (interrupted['outcome'], interrupted['served'], interrupted['usage']['status'], interrupted['cost']['status'])
          == ('interrupted', None, 'unknown', 'usage_unknown')
          and all(interrupted['usage'][d] == {'count': None, 'source': 'unknown'} for d in ('uncachedInput', 'cacheRead', 'cacheWrite', 'output', 'reasoning'))
          and record(pending)['outcome'] == 'interrupted' and record(call) == finished)
    worker = Worker(psql, environment, query)
    close(abandoned)
    check('recovery closes a test once: a further worker and a late close from the original handler change nothing',
          record(abandoned) == interrupted and query(f"SELECT count(*) FROM assistant.model_call_outcomes WHERE call_id='{abandoned}';") == '1')
    check('a retry of an interrupted test returns its outcome', opened(tested, operation=repr(abandoned_operation)) == {'test': interrupted})

    def background(statement):
        """Runs `statement` in the preview administrator's context in a session of its own, returning that session."""
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'{ISSUER}','admin','human_interactive','{contexts.tenant}','{contexts.workspace}',NULL,'preview_runtime',60);")
        session = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        session.stdin.write(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;")
        # communicate() collects the result; Python 3.12's would fail flushing the closed input, so the session drops it.
        session.stdin.close(); session.stdin = None
        return session

    # A key replaced while a test is opening holds the provider row; the open waits for it, then finds the new key
    # unattested, so it never pins an attestation that is no longer current.
    replacing = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    replacing.stdin.write(f"BEGIN;SELECT id FROM assistant.providers WHERE id='{provider_id}' FOR UPDATE;\n\\echo LOCKED\n")
    replacing.stdin.flush()
    while replacing.stdout.readline().strip() != 'LOCKED':
        assert replacing.poll() is None, replacing.stderr.read()
    opener = background(refused(f'PERFORM {open_sql(tested)};', 'model_provider_unqualified'))
    waiting(query, 1, 'assistant.open_test')
    replacing.stdin.write(f"INSERT INTO assistant.credentials SELECT '{provider_id}',max(version)+1,'\\x{'00' * 12}','\\x{'00' * 8}','\\x{'00' * 16}',"
                          f"'0009','{contexts.administrator}',clock_timestamp() FROM assistant.credentials WHERE provider_id='{provider_id}';"
                          f"UPDATE assistant.providers SET version=version+1,enabled=false WHERE id='{provider_id}';COMMIT;\n")
    replacing.stdin.close()
    replacing.wait(timeout=10)
    _, errors = opener.communicate(timeout=10)
    check('a test opening while its key is replaced waits for the replacement, then refuses the new, unattested key', opener.returncode == 0)
    # No key file opens the inserted key, so a key the preview can open replaces it before the provider qualifies again.
    admin.call(f'{PROVIDERS}/key', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': provider()['version'], 'key': KEY})
    requalify()

    # A worker that starts while a test is opening, after the open passed its generation check, waits for the open to
    # commit and then closes the test, so recovery never misses a call its generation's worker left behind.
    racing = str(uuid4())

    def race():
        opener = background(f'SELECT {open_sql(tested, operation=repr(racing))}::text;')
        waiting(query, 1, 'assistant.open_test')
        worker.stop()
        successor = Worker(psql, environment, query, start=False)
        successor.send('SELECT assistant.start_worker();')
        waiting(query, 1, 'assistant.start_worker')
        return opener, successor
    opener, successor = holding(psql, environment, f"SELECT id FROM assistant.models WHERE id='{tested}' FOR UPDATE", race)
    _, errors = opener.communicate(timeout=10)
    assert opener.returncode == 0, errors
    successor.generation = int(successor.receive())
    worker = successor
    raced = query(f"SELECT id FROM assistant.model_calls WHERE operation_id='{racing}';")
    check('a worker that starts while a test is opening waits for the open to commit, then closes that test as interrupted, never before it started',
          record(raced)['outcome'] == 'interrupted' and query('SELECT o.closed_at>=c.started_at FROM assistant.model_calls c '
                                                             f"JOIN assistant.model_call_outcomes o ON o.call_id=c.id WHERE c.id='{raced}';") == 't')

    # The open rechecks its authority after its principal and row waits and after its writes; each case names the
    # frame that must refuse it once the request context has expired.
    rows = query(RECORDS)
    statement = f'SELECT {open_sql(tested)};'
    for wait, lock, site, held in (
            ('its principal', f"SELECT id FROM security.principals WHERE id='{contexts.administrator}' FOR NO KEY UPDATE",
             r'replayed_test\(.*\) line \d+ at assignment', None),
            ('its model row', f"SELECT id FROM assistant.models WHERE id='{tested}' FOR UPDATE",
             r'open_test\(.*\) line \d+ at assignment', ('security.principals', 'RowShareLock')),
            ('its record write', f"SELECT sequence FROM assistant.price_revisions WHERE model_id='{tested}' AND sequence=2 FOR UPDATE",
             r'open_test\(.*\) line \d+ at PERFORM', ('assistant.model_calls', 'RowExclusiveLock'))):
        failure = contexts.expiring(psql, environment, 'context', lock, statement, held=held)
        check(f'a test waiting for {wait} with an expired request context is refused at its recheck',
              refused_at(failure, '42501', rf'function assistant\.{site}'))
    check('an expired test records nothing', query(RECORDS) == rows)

    # The Providers directory shows each provider's latest test as Settings › AI reads it, open or closed.
    def directory():
        return json.loads(contexts.runtime('admin', 'SELECT assistant.provider_directory()::text;').splitlines()[-1])
    latest = opened(tested)['opened']['call']
    running, open_record = directory()['tests'], record(latest)
    close(latest)
    closed = directory()['tests']
    isolated = json.loads(contexts.second_workspace('SELECT assistant.provider_directory()::text;').splitlines()[-1])
    requalify()
    check("the Providers directory shows each provider's latest test, open and then closed, none for a provider never tested, "
          "and none of this workspace's to another",
          running == {'anthropic': open_record | {'underCurrentAttestation': True}} and open_record['outcome'] is None
          and closed == {'anthropic': record(latest) | {'underCurrentAttestation': True}} and record(latest)['outcome'] == 'succeeded'
          and isolated['tests'] == {})
    check('it states whether that test ran under the key and terms attested now, which new terms end',
          directory()['tests']['anthropic']['underCurrentAttestation'] is False)
    worker.stop()
    print(f'{passed} provider test checks passed.', flush=True)
    return passed
