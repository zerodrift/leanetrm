"""Usage (docs/ai-assistant.md §5.3, §5.4, §9): ReadModelUsage; a report of model calls, tokens and exact cost by model,
user or agent over a half-open range of local dates in a named zone, a call falling on the local date of its start, with
provider tests as their own row; and its audited, listed export. Through the API and in SQL, over synthetic calls and
turns the check writes as the database owner at chosen past instants, since a call records its own start; owned
synthetic preview only."""
import json
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal, ROUND_HALF_EVEN, localcontext
from uuid import uuid4
from assistant_context import ISSUER, Contexts, check_guard, expect, holding, state, waiting
from assistant_fixture import AGENTS, MODELS, PROVIDERS, Fixture
from check_model_versions import command as model_command
from check_provider_tests import usage
from preview_client import PreviewClient

USAGE = 'settings/ai/usage'
ZONE = 'America/New_York'
EMPTY = '00000000-0000-0000-0000-000000000000'
# Synthetic rates per million tokens, as the catalog checks use; no vendor's real price ships (D150). The second set
# does not offer cache write.
RATES, UNOFFERED = ('1.234', '0.0123', '2.5', '9.87'), ('1.234', '0.0123', None, '9.87')
DIMENSIONS = ('uncachedInput', 'cacheRead', 'cacheWrite', 'output', 'reasoning')
AUDIT = state('administration_audit')
MARCH = ('2019-03-09', '2019-03-10', '2019-03-11')


def check_upgrade(query, migration):
    """Before 0075 applies: it refuses a changed helper or one whose privileges differ."""
    check_guard(query, migration, '0075', 'model_usage', (
        ("ALTER FUNCTION security.has_permission(text) SET work_mem='64kB';", 'security.has_permission(text)'),
        ('REVOKE EXECUTE ON FUNCTION security.has_permission(text) FROM openetrm_runtime;', 'security.has_permission(text)')))


def amount(value):
    """An exact amount as a report writes it: plain decimal text without trailing zeros."""
    return None if value is None else format(value.normalize(), 'f')


def total(values):
    """The sum of the amounts there are, or none when there are none."""
    present = [v for v in values if v is not None]
    return sum(present, Decimal(0)) if present else None


def ratio(part, whole, places, percent=False):
    """A ratio rounded once to `places` with ties to even, written with exactly those places; none without a positive whole."""
    if part is None or whole is None or whole <= 0:
        return None
    with localcontext() as exact:
        exact.prec = 100
        return format((part * (100 if percent else 1) / whole).quantize(Decimal(1).scaleb(-places), rounding=ROUND_HALF_EVEN), 'f')


class Call:
    """A synthetic call: its model with its price's currency and rates, its turn or none for a provider test, the local date
    it falls on in the March range in New York and, once closed, its outcome, usage status and per dimension a count
    reported, 'nb' not billed or None unknown."""

    def __init__(self, model, currency, rates, turn, day, outcome, status, counts):
        self.model, self.currency, self.rates, self.turn, self.day = model, currency, rates, turn, day
        self.outcome, self.status, self.counts = outcome, status, counts
        self.superseded = False

    @property
    def cost(self):
        """token_cost_v1 under a rate set without a tier, for the recognized usage of a closed call only."""
        if self.outcome is None or self.status != 'recognized':
            return None
        cost = Decimal(0)
        for rate, tokens in zip(self.rates, self.counts[:4]):
            if tokens in ('nb', 0):
                continue
            if rate is None:
                return None
            cost += Decimal(rate) * tokens
        return cost / 1000000

    @property
    def state(self):
        if self.outcome is None:
            return 'open'
        if self.status != 'recognized':
            return self.status
        return 'priced' if self.cost is not None else 'unpriced'

    def known(self, dimension):
        """A dimension's count, or None: an open call and unrecognized usage know none."""
        if self.outcome is None or self.status == 'unrecognized':
            return None
        count = self.counts[DIMENSIONS.index(dimension)]
        return 0 if count == 'nb' else count


def run_checks(url, query, psql, manifest, environment):
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True, observed=None):
        nonlocal passed
        assert condition, name if observed is None else f'{name}: observed {observed}'
        passed += 1
        print('PASS ' + name, flush=True)

    def principal(subject):
        return query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{ISSUER}' AND subject='{subject}';")

    def last(output):
        return [line for line in output.splitlines() if line.strip()][-1]

    check('only the access administrator role reads model usage',
          query("SELECT string_agg(r.code,',' ORDER BY r.code) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id "
                "WHERE p.permission='ReadModelUsage';") == 'access_administrator')
    stamp = uuid4().hex[:8]
    # One session signs in as the preview administrator or each persona in turn: a preview holds at most 64 sessions at
    # once, and the run's checks and browser scenarios share them.
    client = PreviewClient(url)
    client.bootstrap()

    def signed_in(profile='admin'):
        client.call('profile', {'profile': profile})
        return client
    # A role holding ReadModelUsage alone shows that the tab needs nothing else.
    query("INSERT INTO security.roles(id,code,name) VALUES(gen_random_uuid(),'synthetic_usage_only','Synthetic usage only');"
          "INSERT INTO security.role_permissions SELECT id,'ReadModelUsage' FROM security.roles WHERE code='synthetic_usage_only';")

    def persona(name, roles):
        login = f'synthetic_usage_{name}_{stamp}'
        signed_in().call('admin/create', {'operationId': str(uuid4()), 'login': login, 'name': f'Synthetic usage {name}', 'kind': 'human', 'roles': roles})
        return login
    reader, pricing = persona('reader', ['synthetic_usage_only']), persona('pricing', ['access_administrator', 'viewer'])
    # An earlier check grants the preview administrator market data, so the administrator without it is this check's own.
    administrator = persona('administrator', ['access_administrator'])
    ana, ben = persona('ana', ['trader']), persona('ben', ['trader'])

    def request(by='agent', zone=ZONE, start='2019-03-09', end='2019-03-12'):
        return {'zone': zone, 'from': start, 'to': end, 'by': by}

    # Each sends as the preview administrator unless given a session signed in as another persona.
    def report(session=None, **arguments):
        return (session or signed_in()).call(f'{USAGE}/report', request(**arguments))

    def export(session=None, operation=None, **arguments):
        return (session or signed_in()).call(f'{USAGE}/export', {'operationId': operation or str(uuid4()), **request(**arguments)})

    def refusal(path, body, status, code, session=None):
        answer = (session or signed_in()).call(path, body, expected=status)
        assert answer['code'] == code, (path, body, answer)
        return answer

    # Access (§5.4): the tab's own permission, never an agent's.
    for profile in ('viewer', 'trader'):
        session = signed_in(profile)
        for path, body in ((USAGE, None), (f'{USAGE}/report', request()), (f'{USAGE}/export', {'operationId': str(uuid4()), **request()})):
            refusal(path, body, 403, 'access_denied', session)
    check('a viewer and a trader neither read usage nor export it')
    session = signed_in(reader)
    directory = session.call(USAGE)
    check('an identity holding ReadModelUsage alone reads the Usage directory: what a report is grouped by and its longest range',
          directory['dimensions'] == ['model', 'user', 'agent'] and directory['maxDays'] == 366 and isinstance(directory['audit'], list), directory)
    for path in (MODELS, PROVIDERS, AGENTS):
        refusal(path, None, 403, 'access_denied', session)
    check('ReadModelUsage reads no model, provider or agent directory')
    contexts.delegated_agent(expect("PERFORM assistant.usage_report('UTC','2019-03-09','2019-03-12','model');", '42501'), permission='ReadModelUsage')
    contexts.delegated_agent(expect(f"PERFORM assistant.export_usage('{uuid4()}','UTC','2019-03-09','2019-03-12','model');", '42501'),
                             permission='ReadModelUsage')
    for helper in ("assistant.usage_figures(NULL,'UTC','2019-03-09','2019-03-12','model')", "assistant.token_cost_report_v1('share',1,2)",
                   'assistant.token_cost_display_v1(1)', 'assistant.usage_actor()', "assistant.usage_replay(NULL,gen_random_uuid(),'{}')"):
        contexts.runtime('admin', expect(f'PERFORM {helper};', '42501'))
    check("an agent never reads or exports usage, even under a delegation listing ReadModelUsage, and the runtime executes none of "
          "the report's internal helpers")

    # Every field is named and none defaulted (§9); a refusal records nothing.
    audit = query(AUDIT)
    for zone in ('Mars/Olympus', ZONE.lower(), 'UTC+5', 'EST5', '', ' ' + ZONE, ZONE + '\u0000', None):
        refusal(f'{USAGE}/report', request(zone=zone), 422, 'report_zone_invalid')
        refusal(f'{USAGE}/export', {'operationId': str(uuid4()), **request(zone=zone)}, 422, 'report_zone_invalid')
    unnamed = request()
    del unnamed['zone']
    refusal(f'{USAGE}/report', unnamed, 422, 'report_zone_invalid')
    check('a zone must be one the database names exactly; an unknown, differently cased, offset-only, blank, padded or absent zone '
          'is report_zone_invalid, never a default')
    # Names a database may list that are host or tzcode artifacts, not zones a reader names.
    for zone in ('localtime', 'posixrules', 'Factory', 'posix/America/New_York', 'right/America/New_York'):
        refusal(f'{USAGE}/report', request(zone=zone), 422, 'report_zone_invalid')
        refusal(f'{USAGE}/export', {'operationId': str(uuid4()), **request(zone=zone)}, 422, 'report_zone_invalid')
    check('localtime, posixrules, Factory and every posix/ or right/ name are report_zone_invalid, whether or not the database lists them')
    for start, end in (('2019-03-09', '2019-03-09'), ('2019-03-10', '2019-03-09'), ('2019-01-01', '2020-01-03'), ('2019-3-9', '2019-03-12'),
                       ('2019-02-30', '2019-03-12'), ('2019-03-09T00:00:00', '2019-03-12'), ('2019-03-09', None), (None, '2019-03-12')):
        refusal(f'{USAGE}/report', request(start=start, end=end), 422, 'report_range_invalid')
        refusal(f'{USAGE}/export', {'operationId': str(uuid4()), **request(start=start, end=end)}, 422, 'report_range_invalid')
    check('a range is two local dates written yyyy-MM-dd, the first before the second and at most 366 days apart, otherwise '
          'report_range_invalid')
    for by in ('currency', 'Model', '', None):
        refusal(f'{USAGE}/report', request(by=by), 422, 'model_admin_input')
    refusal(f'{USAGE}/export', {'operationId': EMPTY, **request()}, 422, 'model_admin_input')
    refusal(f'{USAGE}/export', request(), 422, 'model_admin_input')
    refusal(f'{USAGE}/report', {**request(), 'currency': 'usd'}, 400, 'request_format')
    check('a report names model, user or agent, an export its request identity, and neither names anything else')
    check('every refused report and export recorded nothing', query(AUDIT) == audit)
    year = report(start='2019-01-01', end='2020-01-02')
    check('a range of exactly 366 days is a report of 366 days',
          len(year['days']) == 366 and (year['days'][0]['date'], year['days'][-1]['date']) == ('2019-01-01', '2020-01-01'))

    # Synthetic usage (§5.1): a qualified provider and three priced models, the second offering no cache write and the third
    # priced in another reference revision's USD; a published agent on each; turns in every state; and calls in every usage
    # state, several at a local midnight, all in 2019, which no other check uses.
    fixture = Fixture(signed_in(), query, manifest, contexts.administrator, stamp, 'usage')
    fixture.qualify()
    catalog = manifest['referenceCatalogId']
    first, second = (catalog, manifest['referenceRevisionId'], 'usd'), (catalog, str(uuid4()), 'usd')
    query(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{second[1]}','usage_alternate_{stamp}');"
          f"INSERT INTO ref_data.currencies VALUES('{catalog}','{second[1]}','usd','USD','US Dollar',2);"
          f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{second[1]}');")

    def priced_model(name, currency, rates):
        added = signed_in().call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': f'model-usage-{name}-{stamp}',
                                              'displayName': f'Synthetic usage {name} model'})
        fixture.edit(added['id'])
        query(f"INSERT INTO assistant.price_revisions VALUES('{added['id']}',1,'rates','{currency[0]}','{currency[1]}','{currency[2]}',"
              + ','.join('NULL' if rate is None else f"'{rate}'" for rate in rates)
              + f",NULL,NULL,NULL,NULL,NULL,'2019-01-01',NULL,'{contexts.administrator}',clock_timestamp());")
        signed_in().call(f'{MODELS}/enable', model_command(added['id'], fixture.row_version(added['id'])))
        return added['id']
    one, two, three = priced_model('one', first, RATES), priced_model('two', first, UNOFFERED), priced_model('three', second, RATES)
    rates, currency = {one: RATES, two: UNOFFERED, three: RATES}, {one: first, two: first, three: second}
    guide, helper, alternate = (fixture.publish(fixture.save(fixture.create(f'usage {name}'), model_id))
                                for name, model_id in (('guide', one), ('helper', two), ('alternate', three)))
    tenant, workspace = contexts.tenant, contexts.workspace
    generation = query('SELECT generation FROM assistant.worker_generation;')
    provider, credential, terms = query(f"SELECT a.provider_id||','||a.credential_version||','||a.terms_version FROM assistant.models m "
                                        f"CROSS JOIN LATERAL assistant.current_attestation(m.provider_id) a WHERE m.id='{one}';").split(',')
    pinned = last(contexts.runtime('trader', f"SELECT security.capture_date_context('{manifest['catalogId']}','2020-12-18',NULL);"))
    cutoff = query(f"SELECT known_at FROM market_data.temporal_contexts WHERE id='{pinned}';")
    mark = f'usage-content-{stamp}'
    owners = {'ana': principal(ana), 'ben': principal(ben)}
    conversations, turns, calls = {}, {}, {}

    def turn(key, owner, agent_state, state, asked):
        """A turn of the owner's conversation with the agent's published version, in `state`, holding a question, page report
        and answer that no report may show."""
        if (owner, agent_state['id']) not in conversations:
            conversation, delegation = str(uuid4()), str(uuid4())
            agent_principal = query(f"SELECT principal_id FROM assistant.agents WHERE id='{agent_state['id']}';")
            query(f"INSERT INTO assistant.conversations VALUES('{conversation}','{tenant}','{workspace}','{owner}','api','{agent_state['id']}','2019-03-01',0);"
                  f"INSERT INTO security.agent_delegations VALUES('{delegation}','{agent_principal}','{owner}','{tenant}','{workspace}','2019-03-01','2019-04-01',true);")
            conversations[owner, agent_state['id']] = [conversation, delegation, 0]
        held = conversations[owner, agent_state['id']]
        held[2] += 1
        identity = str(uuid4())
        page = json.dumps({'page': 'curves', 'selection': {'curve': mark}})
        outcome = "'model_provider_overloaded'" if state == 'failed' else 'NULL'
        answer = f'Synthetic answer {mark}' if state == 'completed' else ''
        cancelled = f"'{asked}'::timestamptz+interval '1 second'" if state == 'cancelled' else 'NULL'
        ended = f"'{asked}'::timestamptz+interval '1 minute'" if state in ('completed', 'failed', 'cancelled', 'interrupted') else 'NULL'
        query(f"INSERT INTO assistant.turns VALUES('{identity}','{held[0]}','{tenant}','{workspace}',{held[2]},'{owner}','{uuid4()}','{agent_state['id']}',1,"
              f"false,'{held[1]}','Synthetic question {mark}','{page}','{manifest['catalogId']}','current',NULL,NULL,'{pinned}','{cutoff}',NULL,'{asked}',"
              f"ARRAY[]::uuid[],'turns_v1','{state}',{outcome},NULL,'{answer}',false,{generation},'{'0' * 64}','{'0' * 64}','Synthetic usage',"
              f"'{asked}'::timestamptz+interval '30 minutes','{asked}','{asked}',{cancelled},NULL,{ended});")
        turns[key] = (identity, owner, agent_state['id'], state)

    def call(key, model_id, start, day=None, turn_key=None, attempt=1, outcome='succeeded', status='recognized', counts=(1, 0, 0, 1, 0)):
        """A call of a turn, or a provider test, at `start`, which falls on `day` of the March range in New York."""
        identity = str(uuid4())
        columns = ('id,tenant_id,workspace_id,generation,provider_id,credential_version,terms_version,model_id,model_version,reasoning,max_output_tokens,'
                   'price_sequence,cost_method,started_at')
        common = (f"'{identity}','{tenant}','{workspace}',{generation},'{provider}',{credential},{terms},'{model_id}',{fixture.version_of(model_id)},"
                  f"'low',256,1,'token_cost_v1','{start}'")
        if turn_key:
            query(f"INSERT INTO assistant.model_calls({columns},turn_id,attempt,after_sequence) VALUES({common},'{turns[turn_key][0]}',{attempt},1);")
        else:
            query(f"INSERT INTO assistant.model_calls({columns},operation_id,actor_id,token_digest) VALUES({common},'{uuid4()}','{contexts.administrator}','{'0' * 64}');")
        if outcome is not None:
            numbers = ','.join('NULL' if c is None else '0' if c == 'nb' else str(c) for c in counts[:4])
            served = 'm.identifier' if outcome in ('succeeded', 'usage_unrecognized') else 'NULL'
            # A turn's call that completed records how it stopped, as close_turn_call requires; a test's call records neither.
            stop = "'end_turn'" if turn_key and outcome in ('succeeded', 'usage_unrecognized') else 'NULL'
            query(f"INSERT INTO assistant.model_call_outcomes SELECT c.id,c.started_at+interval '5 seconds','{outcome}',{served},'{status}',{usage(*counts)},"
                  f"CASE WHEN '{status}'='recognized' THEN assistant.token_cost_v1(r,{numbers}) END,{stop} FROM assistant.model_calls c "
                  "JOIN assistant.price_revisions r ON(r.model_id,r.sequence)=(c.model_id,c.price_sequence) JOIN assistant.models m ON m.id=c.model_id "
                  f"WHERE c.id='{identity}';")
        calls[key] = Call(model_id, currency[model_id], rates[model_id], turn_key, day, outcome, status, counts)
    turn('answered', owners['ana'], guide, 'completed', '2019-03-09T14:59:00Z')
    turn('retried', owners['ana'], guide, 'completed', '2019-03-10T14:59:00Z')
    turn('failed', owners['ben'], guide, 'failed', '2019-03-11T03:59:00Z')
    turn('unpriced', owners['ben'], helper, 'completed', '2019-03-11T11:59:00Z')
    # Asked late on 8 March in New York; its call, at midnight on the 9th, places it.
    turn('cancelled', owners['ben'], helper, 'cancelled', '2019-03-09T04:59:00Z')
    turn('interrupted', owners['ana'], alternate, 'interrupted', '2019-03-10T05:59:00Z')
    turn('running', owners['ana'], guide, 'running', '2019-03-11T12:59:00Z')
    call('answer', one, '2019-03-09T15:00:00Z', MARCH[0], 'answered', counts=(1000, 0, 0, 200, 50))
    call('transport', one, '2019-03-10T15:00:00Z', MARCH[1], 'retried', outcome='model_provider_transport', counts=(2000, 100, 0, 0, 0))
    call('retry', one, '2019-03-10T15:00:30Z', MARCH[1], 'retried', 2, counts=(2100, 100, 0, 300, None))
    # 23:59:59.999999 on the 10th in New York, then midnight on the 11th.
    call('overloaded', one, '2019-03-11T03:59:59.999999Z', MARCH[1], 'failed', outcome='model_provider_overloaded', status='unknown',
         counts=(500, 0, 'nb', None, None))
    call('failed retry', one, '2019-03-11T04:00:00Z', MARCH[2], 'failed', 2, outcome='model_provider_transport', counts=(300, 0, 0, 0, 0))
    call('not offered', two, '2019-03-11T12:00:00Z', MARCH[2], 'unpriced', counts=(400, 0, 10, 100, 0))
    call('unrecognized', two, '2019-03-09T05:00:00Z', MARCH[0], 'cancelled', outcome='usage_unrecognized', status='unrecognized', counts=(999, 0, 0, 999, 0))
    call('interrupted', three, '2019-03-10T06:00:00Z', MARCH[1], 'interrupted', outcome='interrupted', status='unknown', counts=(None,) * 5)
    call('open', one, '2019-03-11T13:00:00Z', MARCH[2], 'running', outcome=None)
    # Provider tests at the range's edges: 23:59:59 on the 8th, 23:59:59 on the 11th and midnight on the 12th in New York.
    call('test before', one, '2019-03-09T04:59:59Z', counts=(10, 0, 0, 10, 0))
    call('test last', one, '2019-03-12T03:59:59Z', MARCH[2], counts=(20, 0, 0, 20, 0))
    call('test after', one, '2019-03-12T04:00:00Z', counts=(30, 0, 0, 30, 0))
    call('test unoffered', two, '2019-03-10T07:00:00Z', MARCH[1], counts=(30, 0, 0, 30, 0))
    # Across the November fall back in New York and the September midnight gap in Santiago.
    for key, start in (('fall 1:30 EDT', '2019-11-03T05:30:00Z'), ('fall 1:30 EST', '2019-11-03T06:30:00Z'), ('fall last', '2019-11-04T04:59:59Z'),
                       ('fall next', '2019-11-04T05:00:00Z'), ('fall before', '2019-11-03T03:59:59Z'), ('gap before', '2019-09-08T03:59:59Z'),
                       ('gap first', '2019-09-08T04:00:00Z')):
        call(key, one, start)
    # Half an hour after the Azores' first midnight on the day it falls back, and in the first half hour of 2 July in CET.
    for key, start in (('azores first midnight', '2025-10-26T00:30:00Z'), ('cet summer', '2025-07-01T22:30:00Z')):
        call(key, one, start)

    def member(view, held):
        if held.turn is None:
            return None
        return {'model': held.model, 'user': turns[held.turn][1], 'agent': turns[held.turn][2]}[view]

    def figures(group, whole, tests=False):
        """What a cell holds, from its calls, as the report writes it without the amounts it shows."""
        tokens = {}
        for dimension in DIMENSIONS:
            known = [c.known(dimension) for c in group if c.known(dimension) is not None]
            tokens[dimension] = {'count': str(sum(known)) if known else None, 'unknownCalls': len(group) - len(known)}
        superseded = [c for c in group if c.superseded and c.cost is not None]
        cost = total(c.cost for c in group)
        states = {s: sum(c.state == s for c in group) for s in ('priced', 'unpriced', 'unknown', 'unrecognized', 'open')}
        result = {'calls': {'total': len(group), 'withoutCost': len(group) - states['priced'], **states},
                  'tokens': tokens, 'pricedCost': amount(cost), 'pricedShare': ratio(cost, whole, 2, True),
                  'superseded': {'calls': len(superseded), 'cost': amount(total(c.cost for c in superseded))}, 'turns': None}
        if not tests:
            turned = [c for c in group if c.turn is not None]

            def ended(*states):
                return len({c.turn for c in turned if turns[c.turn][3] in states})
            answers = [c for c in turned if turns[c.turn][3] == 'completed']
            lacking = {c.turn for c in answers if c.cost is None}
            answer_cost = total(c.cost for c in answers)
            result['turns'] = {'completed': ended('completed'), 'failed': ended('failed'), 'cancelled': ended('cancelled'),
                               'interrupted': ended('interrupted'), 'unfinished': ended('queued', 'running', 'cancel_requested'),
                               'other': ended('failed', 'cancelled', 'interrupted', 'queued', 'running', 'cancel_requested'),
                               'answersPricedCost': amount(answer_cost), 'answersWithoutCost': len(lacking),
                               'costPerAnswer': None if lacking else ratio(answer_cost, ended('completed'), 18),
                               'otherPricedCost': amount(total(c.cost for c in turned if turns[c.turn][3] != 'completed'))}
        return result

    def expected(view):
        """Per exact currency reference in the March range: the total, a row per member with provider tests as their own, and
        each day's cost."""
        result = {}
        for reference in {c.currency for c in calls.values() if c.day}:
            held = [c for c in calls.values() if c.day and c.currency == reference]
            whole = total(c.cost for c in held)
            rows = {}
            for c in held:
                rows.setdefault(member(view, c), []).append(c)
            daily = {}
            for day in MARCH:
                on = [c for c in held if c.day == day]
                daily[day] = {'pricedCost': amount(total(c.cost for c in on)) if on else '0', 'calls': len(on),
                              'withoutCost': sum(c.cost is None for c in on)}
            result[reference] = {'total': figures(held, whole), 'rows': {m: figures(group, whole, m is None) for m, group in rows.items()},
                                 'daily': daily}
        return result

    def plain(value):
        """Figures without the amounts shown beside each exact one."""
        if isinstance(value, dict):
            return value['amount'] if set(value) == {'amount', 'display'} else {k: plain(v) for k, v in value.items()}
        return [plain(v) for v in value] if isinstance(value, list) else value

    def sections(answer):
        """A report's sections by exact currency reference: the total, rows by member and each day's cost."""
        result = {}
        for section in answer['currencies']:
            reference = (section['currency']['catalogId'], section['currency']['revisionId'], section['currency']['code'])
            result[reference] = {'total': plain(section['total']),
                                 'rows': {row['member'].get('id'): plain({k: v for k, v in row.items() if k != 'member'}) for row in section['rows']},
                                 'daily': {d['date']: plain({k: v for k, v in d.items() if k != 'date'}) for d in section['daily']}}
        return result

    # The March range in New York spans the spring-forward day.
    march = {view: report(by=view) for view in ('agent', 'model', 'user')}
    check('a report states its zone, its range, its UTC bounds, what it is grouped by and the methods behind its amounts, and each day '
          'with its UTC bounds: the spring-forward day lasts 23 hours',
          [march['agent'][k] for k in ('reportFormat', 'zone', 'from', 'to', 'utcFrom', 'utcTo', 'by', 'costMethod', 'displayMethod', 'ratioMethod')] ==
          ['usage_report_v1', ZONE, '2019-03-09', '2019-03-12', '2019-03-09T05:00:00+00:00', '2019-03-12T04:00:00+00:00', 'agent', 'token_cost_v1',
           'token_cost_display_v1', 'token_cost_report_v1']
          and [(d['date'], d['from'], d['to']) for d in march['agent']['days']] == [
              ('2019-03-09', '2019-03-09T05:00:00+00:00', '2019-03-10T05:00:00+00:00'),
              ('2019-03-10', '2019-03-10T05:00:00+00:00', '2019-03-11T04:00:00+00:00'),
              ('2019-03-11', '2019-03-11T04:00:00+00:00', '2019-03-12T04:00:00+00:00')], march['agent'])
    for view in ('agent', 'model', 'user'):
        check(f'by {view}, per exact currency reference, each member and provider tests as their own row count calls by usage state, the '
              'known tokens of each dimension beside the calls that left it unknown, the cost of its priced calls beside the calls without '
              "a cost and that cost's share of the same-currency total's, and turns by terminal state with answers, their cost per answer and "
              "the other turns and their priced cost apart; each day has the cost of its priced calls, each call falling on the day whose "
              'bounds hold its start',
              sections(march[view]) == expected(view), (sections(march[view]), expected(view)))
    check('the views reconcile: by model, user and agent every currency has the same total',
          sections(march['model'])[first]['total'] == sections(march['user'])[first]['total'] == sections(march['agent'])[first]['total']
          and sections(march['model'])[second]['total'] == sections(march['user'])[second]['total'] == sections(march['agent'])[second]['total'])
    by_agent = sections(march['agent'])
    check('costs and ratios are exact: the guide costs 0.01160106, 0.00561543 an answer and 95.43 percent of its currency; tests cost 0.0005552',
          by_agent[first]['rows'][guide['id']]['pricedCost'] == '0.01160106'
          and by_agent[first]['rows'][guide['id']]['turns']['costPerAnswer'] == '0.005615430000000000'
          and by_agent[first]['rows'][guide['id']]['pricedShare'] == '95.43' and by_agent[first]['rows'][None]['pricedCost'] == '0.0005552'
          and by_agent[first]['total']['pricedShare'] == '100.00', by_agent[first])
    check('the cost of priced calls stands beside the calls without a cost, never withheld: the guide has two of six calls without one',
          by_agent[first]['rows'][guide['id']]['calls']['withoutCost'] == 2 and by_agent[first]['rows'][guide['id']]['calls']['open'] == 1,
          by_agent[first]['rows'][guide['id']]['calls'])
    check('an answer with a call that has no cost leaves its cell without a cost per answer, and a cell with no priced call has no cost '
          'and no share, never zero',
          by_agent[first]['rows'][helper['id']]['pricedCost'] is None and by_agent[first]['rows'][helper['id']]['pricedShare'] is None
          and by_agent[first]['rows'][helper['id']]['turns']['answersWithoutCost'] == 1
          and by_agent[first]['rows'][helper['id']]['turns']['costPerAnswer'] is None and by_agent[second]['total']['pricedCost'] is None)
    check('two currency references sharing a code are two totals, never added together', set(by_agent) == {first, second})
    shown = next(s for s in march['agent']['currencies'] if s['currency']['revisionId'] == first[1])
    guide_row = next(r for r in shown['rows'] if r['member'].get('id') == guide['id'])
    check('every amount is shown as token_cost_display_v1 gives it beside the exact amount',
          guide_row['pricedCost'] == {'amount': '0.01160106', 'display': '0.01160106'}
          and guide_row['turns']['costPerAnswer'] == {'amount': '0.005615430000000000', 'display': '0.00561543'}, guide_row)
    users = {r['member']['id']: r['member'] for s in march['user']['currencies'] for r in s['rows'] if r['member']['kind'] == 'user'}
    model_member = next(r['member'] for s in march['model']['currencies'] for r in s['rows'] if r['member'].get('id') == one)
    check('a row names its member and nothing it holds: a model by vendor, identifier and display name, a user by name and login, an '
          'agent by name; provider tests come last',
          users == {owners['ana']: {'kind': 'user', 'id': owners['ana'], 'name': 'Synthetic usage ana', 'login': ana},
                    owners['ben']: {'kind': 'user', 'id': owners['ben'], 'name': 'Synthetic usage ben', 'login': ben}}
          and model_member == {'kind': 'model', 'id': one, 'vendor': 'anthropic', 'identifier': f'model-usage-one-{stamp}', 'name': 'Synthetic usage one model'}
          and guide_row['member'] == {'kind': 'agent', 'id': guide['id'], 'name': f'Synthetic usage guide {stamp}'}
          and shown['rows'][-1]['member'] == {'kind': 'tests'}, (users, model_member, shown['rows'][-1]['member']))
    check('usage shows users, agents, models, counts and cost, never a question, answer or page report',
          all(mark not in json.dumps(answer) for answer in march.values()))

    # A price appended later never changes a recorded cost; Usage flags the calls a later revision supersedes (§5.3).
    query(f"INSERT INTO assistant.price_revisions VALUES('{one}',2,'rates','{first[0]}','{first[1]}','usd','9','9','9','9',NULL,NULL,NULL,NULL,NULL,"
          f"'2019-03-10T12:00:00Z','2019-03-10T18:00:00Z','{contexts.administrator}',clock_timestamp());")
    for key in ('transport', 'retry'):
        calls[key].superseded = True
    later = sections(report())
    check('a revision appended over recorded calls changes no cost; the calls it supersedes and their cost are counted apart',
          later == expected('agent') and later[first]['rows'][guide['id']]['superseded'] == {'calls': 2, 'cost': '0.00802286'}
          and later[first]['rows'][guide['id']]['pricedCost'] == '0.01160106', later[first])

    def daily_calls(answer):
        return {d['date']: d['calls'] for s in answer['currencies'] for d in s['daily']}
    fall = report(start='2019-11-02', end='2019-11-05')
    check('across the fall back in New York the day of the repeated hour lasts 25 hours and holds both 1:30s and its last instant',
          [(d['date'], d['from'], d['to']) for d in fall['days']] == [
              ('2019-11-02', '2019-11-02T04:00:00+00:00', '2019-11-03T04:00:00+00:00'),
              ('2019-11-03', '2019-11-03T04:00:00+00:00', '2019-11-04T05:00:00+00:00'),
              ('2019-11-04', '2019-11-04T05:00:00+00:00', '2019-11-05T05:00:00+00:00')]
          and daily_calls(fall) == {'2019-11-02': 1, '2019-11-03': 3, '2019-11-04': 1}, (fall['days'], daily_calls(fall)))
    gap = report(zone='America/Santiago', start='2019-09-07', end='2019-09-09')
    check("where Santiago's clocks skip midnight the day starts at the first instant it has, 01:00, and lasts 23 hours",
          [(d['date'], d['from'], d['to']) for d in gap['days']] == [
              ('2019-09-07', '2019-09-07T04:00:00+00:00', '2019-09-08T04:00:00+00:00'),
              ('2019-09-08', '2019-09-08T04:00:00+00:00', '2019-09-09T03:00:00+00:00')]
          and (gap['utcFrom'], gap['utcTo']) == ('2019-09-07T04:00:00+00:00', '2019-09-09T03:00:00+00:00')
          and daily_calls(gap) == {'2019-09-07': 1, '2019-09-08': 1}, (gap['days'], daily_calls(gap)))
    azores = report(zone='Atlantic/Azores', start='2025-10-25', end='2025-10-27')
    check('in the Azores, whose fall back repeats midnight, 26 October starts at its first midnight, 00:00Z, and lasts 25 hours, the 25th '
          'lasts 24, and a call at 00:30Z falls on the 26th as its bounds state',
          [(d['date'], d['from'], d['to']) for d in azores['days']] == [
              ('2025-10-25', '2025-10-25T00:00:00+00:00', '2025-10-26T00:00:00+00:00'),
              ('2025-10-26', '2025-10-26T00:00:00+00:00', '2025-10-27T01:00:00+00:00')]
          and daily_calls(azores) == {'2025-10-25': 0, '2025-10-26': 1}, (azores['days'], daily_calls(azores)))
    summer = report(zone='CET', start='2025-07-02', end='2025-07-03')
    check('CET is read as the zone, whose summer is at +02, never as the abbreviation at +01: 2 July starts at 22:00Z on the 1st and '
          'holds a call at 22:30Z',
          [(d['date'], d['from'], d['to']) for d in summer['days']] == [('2025-07-02', '2025-07-01T22:00:00+00:00', '2025-07-02T22:00:00+00:00')]
          and (summer['utcFrom'], summer['utcTo']) == ('2025-07-01T22:00:00+00:00', '2025-07-02T22:00:00+00:00')
          and daily_calls(summer) == {'2025-07-02': 1}, (summer['days'], daily_calls(summer)))
    apia = report(zone='Pacific/Apia', start='2011-12-29', end='2012-01-01')
    check('where Apia skipped 30 December 2011 that date is a day of no length, and the 31st starts where the 29th ends',
          [(d['date'], d['from'], d['to']) for d in apia['days']] == [
              ('2011-12-29', '2011-12-29T10:00:00+00:00', '2011-12-30T10:00:00+00:00'),
              ('2011-12-30', '2011-12-30T10:00:00+00:00', '2011-12-30T10:00:00+00:00'),
              ('2011-12-31', '2011-12-30T10:00:00+00:00', '2011-12-31T10:00:00+00:00')], apia['days'])
    # Goose Bay fell back from 00:01 to 23:01, repeating the hour that holds midnight, so the rule reads both midnight and
    # the instant before it at the later offset.
    goose = report(zone='America/Goose_Bay', start='2010-11-06', end='2010-11-08')
    check('where a fall back repeats both midnight and the instant before it, as in Goose Bay on 7 November 2010, the day starts at the '
          'later midnight, 04:00Z, and the 6th lasts 25 hours',
          [(d['date'], d['from'], d['to']) for d in goose['days']] == [
              ('2010-11-06', '2010-11-06T03:00:00+00:00', '2010-11-07T04:00:00+00:00'),
              ('2010-11-07', '2010-11-07T04:00:00+00:00', '2010-11-08T04:00:00+00:00')], goose['days'])

    # Labels come from the reader's own scoped read, as currency choices do (D124).
    unlabelled = [report(signed_in(name))['currencies'] for name in (reader, administrator)]
    check("a reader without market data, an access administrator included, sees no currency's label",
          all(len(listed) == 2 and all(s['label'] is None for s in listed) for listed in unlabelled), unlabelled)
    labelled = {s['currency']['revisionId']: s['label'] for s in report(signed_in(pricing))['currencies']}
    named = query(f"SELECT g.code||' '||r.revision_key FROM ref_data.catalogs g JOIN ref_data.revisions r ON r.catalog_id=g.id "
                  f"WHERE(r.catalog_id,r.id)=('{first[0]}','{first[1]}');")
    check('a reader of market data sees each currency labelled from its reference row, with the catalog and revision that tell two '
          'references sharing a code apart',
          labelled[first[1]]['displayCode'] == 'USD' and f"{labelled[first[1]]['catalogCode']} {labelled[first[1]]['revisionKey']}" == named
          and labelled[second[1]]['displayCode'] == 'USD' and labelled[second[1]]['revisionKey'] == f'usage_alternate_{stamp}', labelled)
    check('a report names each vendor', march['agent']['vendors'][0] == {'code': 'anthropic', 'name': 'Anthropic'}, march['agent']['vendors'])
    isolated = last(contexts.second_workspace(f"SELECT assistant.usage_report('{ZONE}','2019-03-09','2019-03-12','agent')->'currencies';"))
    check("a reader in another workspace sees none of this workspace's calls", isolated == '[]', isolated)

    # token_cost_report_v1 and token_cost_display_v1 (§5.3), over amounts built for the check.
    check('token_cost_report_v1 rounds a cost per answer once to 18 places and a share once to a 2-place percentage, ties to even, '
          'written with exactly those places, and has no ratio without a positive whole',
          [query(f"SELECT coalesce(assistant.token_cost_report_v1({a}),'none');") for a in (
              "'cost_per_answer',0.000000000000000003,2", "'cost_per_answer',0.000000000000000005,2", "'cost_per_answer',0.000000000000000001,2",
              "'cost_per_answer',7,3", "'cost_per_answer',1234567.5,1", "'share',1,8", "'share',1,800", "'share',135,100000", "'share',2,3",
              "'share',5,5", "'share',0,4", "'share',1,0", "'cost_per_answer',NULL,2")] ==
          ['0.000000000000000002', '0.000000000000000002', '0.000000000000000000', '2.333333333333333333', '1234567.500000000000000000',
           '12.50', '0.12', '0.14', '66.67', '100.00', '0.00', 'none', 'none'])
    check('token_cost_display_v1 groups the whole part by thousands and trims trailing zeros, rounding nothing, so a nonzero cost never '
          'shows as zero',
          [query(f'SELECT assistant.token_cost_display_v1({v});') for v in ('1234567.891', '0.000000000000000001', '1000', '999', '12.50', '0', '1000000.000100')]
          == ['1,234,567.891', '0.000000000000000001', '1,000', '999', '12.5', '0', '1,000,000.0001'])

    # Export (§9): recorded with the report it carries, listed, and repeated exactly.
    operation = str(uuid4())
    exported = export(operation=operation, by='user')
    recorded = json.loads(query(f"SELECT json_build_object('request',request,'after',after_state,'actor',actor_id,"
                                f"'ids',json_build_array(provider_id,model_id,agent_id,key_digest))::text FROM assistant.administration_audit "
                                f"WHERE operation_id='{operation}' AND action='export_usage';"))
    carried = {k: v for k, v in exported.items() if k != 'vendors'}
    carried['currencies'] = [{k: v for k, v in s.items() if k != 'label'} for s in carried['currencies']]
    check('an export carries the report and records it with its request against the reader, naming no provider, model, agent or key',
          recorded['after'] == carried and recorded['request'] == {'action': 'export_usage', 'operationId': operation, 'zone': ZONE,
                                                                   'from': '2019-03-09', 'to': '2019-03-12', 'by': 'user'}
          and recorded['actor'] == contexts.administrator and recorded['ids'] == [None, None, None, None]
          and sections(exported) == sections(report(by='user')) and mark not in json.dumps(recorded), recorded)
    administrator = query("SELECT display_name FROM security.workspace_memberships "
                          f"WHERE(tenant_id,workspace_id,principal_id)=('{tenant}','{workspace}','{contexts.administrator}');")
    listed = signed_in().call(USAGE)['audit'][0]
    check('the Usage tab lists the export first, with when it was made, its reader, zone, range and grouping',
          listed == {'operationId': operation, 'at': exported['generatedAt'], 'actor': administrator, 'zone': ZONE, 'from': '2019-03-09',
                     'to': '2019-03-12', 'by': 'user'}, listed)
    recorded_audit = query(AUDIT)
    check('repeating an export answers exactly as before and records nothing',
          export(operation=operation, by='user') == exported and query(AUDIT) == recorded_audit)
    refusal(f'{USAGE}/export', {'operationId': operation, **request(by='agent')}, 409, 'model_admin_conflict')
    refusal(f'{USAGE}/export', {'operationId': operation, **request(by='user')}, 409, 'model_admin_conflict', signed_in(pricing))
    refusal(f'{AGENTS}/create', {'operationId': operation, 'name': f'Synthetic usage reuse {stamp}'}, 409, 'model_admin_conflict')
    users_access = str(uuid4())
    signed_in('admin').call('admin/create', {'operationId': users_access, 'login': f'synthetic_usage_reuse_{stamp}', 'name': 'Synthetic usage reuse',
                                             'kind': 'human', 'roles': ['viewer']})
    refusal(f'{USAGE}/export', {'operationId': users_access, **request()}, 409, 'model_admin_conflict')
    check('a request identity used for another export, by another reader, or for another change in Settings › AI or Users & access is '
          'refused, recording nothing', query(AUDIT) == recorded_audit)
    concurrent = str(uuid4())
    sent = f"SELECT assistant.export_usage('{concurrent}','{ZONE}','2019-03-09','2019-03-12','agent');"
    with ThreadPoolExecutor(2) as pool:
        def overlap():
            both = [pool.submit(contexts.runtime, reader, sent) for _ in range(2)]
            waiting(query, 2, 'assistant.export_usage')
            return both
        both = holding(psql, environment, f"SELECT 1 FROM security.principals WHERE id='{principal(reader)}' FOR NO KEY UPDATE", overlap)
        answers = [last(future.result(timeout=20)) for future in both]
    check('two identical exports sent at once by one reader record one export, and both answer with its report',
          answers[0] == answers[1] and json.loads(answers[0])['generatedAt'] == json.loads(answers[1])['generatedAt']
          and query(f"SELECT count(*) FROM assistant.administration_audit WHERE operation_id='{concurrent}';") == '1', answers)
    session = signed_in(reader)
    by_reader = export(session)
    check('an identity holding ReadModelUsage alone exports, and the Usage tab lists its export',
          session.call(USAGE)['audit'][0]['at'] == by_reader['generatedAt'])
    query("INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,after_state,recorded_at) "
          f"SELECT '{tenant}','{workspace}',gen_random_uuid(),'{contexts.administrator}','export_usage',jsonb_build_object('action','export_usage',"
          f"'zone','UTC','from','2018-01-01','to','2018-01-02','by','model'),'{{}}',clock_timestamp()-make_interval(mins=>n) FROM generate_series(1,101) n;")
    newest = export(by='model')
    listing = signed_in().call(USAGE)['audit']
    latest = query("SELECT string_agg(operation_id::text,',' ORDER BY recorded_at DESC,operation_id) FROM(SELECT operation_id,recorded_at FROM "
                   f"assistant.administration_audit WHERE(tenant_id,workspace_id)=('{tenant}','{workspace}') AND action='export_usage' "
                   "ORDER BY recorded_at DESC,operation_id LIMIT 100)a;")
    check('the Usage tab lists only the latest 100 exports, newest first, and no other change',
          len(listing) == 100 and ','.join(e['operationId'] for e in listing) == latest and listing[0]['at'] == newest['generatedAt'])
    query(expect("UPDATE assistant.administration_audit SET recorded_at=recorded_at WHERE action='export_usage';", '55000'))
    query(expect("DELETE FROM assistant.administration_audit WHERE action='export_usage';", '55000'))
    for column, value in (('provider_id', provider), ('model_id', one), ('agent_id', guide['id']), ('key_digest', '0' * 64)):
        query(expect(f"INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,{column},request,after_state,"
                     f"recorded_at) VALUES('{tenant}','{workspace}',gen_random_uuid(),'{contexts.administrator}','export_usage','{value}','{{}}','{{}}',"
                     "clock_timestamp());", '23514'))
    check('row triggers refuse any update or delete of an export record, even from the owner, and check constraints refuse one naming a '
          'provider, model, agent or key')
    # Nothing this check wrote stays running.
    query("INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,usage_status,uncached_input_source,cache_read_source,cache_write_source,"
          "output_source,reasoning_source) SELECT id,clock_timestamp(),'interrupted','unknown','unknown','unknown','unknown','unknown','unknown' "
          f"FROM assistant.model_calls WHERE turn_id='{turns['running'][0]}';"
          f"UPDATE assistant.turns SET state='interrupted',ended_at=clock_timestamp() WHERE id='{turns['running'][0]}';")
    print(f'{passed} usage checks passed.', flush=True)
