"""Agent administration (docs/ai-assistant.md §5.1, §5.4, §7.5, §9): the agent permissions and the agent reader role,
agents and their versions, and create, save, Publish and Retire through the API; owned synthetic preview only."""
import json
import threading
from uuid import uuid4
from assistant_context import ISSUER, Contexts, check_guard, expect, holding, migration_body, refused, state, waiting
from check_model_qualification import command as provider_command, terms
from check_model_versions import SECOND_MAPPING, capabilities, command as model_command
from check_provider_tests import KEY
from preview_client import PreviewClient

AGENTS, MODELS, PROVIDERS = 'settings/ai/agents', 'settings/ai/models', 'settings/ai/providers'
READER = 'a0000000-0000-0000-0000-000000000007'
VIEWER = 'a0000000-0000-0000-0000-000000000001'
# Synthetic rates per million tokens, as the catalog checks use; no vendor's real price ships (D150).
RATES = '1.234,0.0123,2.5,9.87'
RECORDS = state('agents', 'agent_versions', 'agent_version_roles', 'administration_audit')
AGENT_RECORDS = state('agents', 'agent_versions', 'agent_version_roles')
EMPTY = '00000000-0000-0000-0000-000000000000'
WINDOW = "clock_timestamp()-interval '1 day','9999-12-31'"


def check_upgrade(query, migration):
    """Before 0066 applies: it refuses to start while an agent principal holds a role, and refuses changed helpers."""
    body = migration_body(migration)
    tenant, workspace, agent = uuid4(), uuid4(), uuid4()
    held = (f"INSERT INTO security.tenants VALUES('{tenant}','synthetic_agent_upgrade');"
            f"INSERT INTO security.workspaces VALUES('{tenant}','{workspace}','synthetic_agent_upgrade');"
            f"INSERT INTO security.principals VALUES('{agent}','agent',true);"
            f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{agent}','upgrade_agent','Synthetic upgrade agent',true,1);"
            f"INSERT INTO security.role_grants VALUES('{uuid4()}','{agent}','{VIEWER}','{tenant}','{workspace}',{WINDOW},")
    try:
        query(f'BEGIN;{held}false);{body}ROLLBACK;')
    except RuntimeError as error:
        assert f'agent_administration: agent principal {agent} holds a role' in str(error), error
    else:
        raise AssertionError('Migration 0066 applied while an agent principal held a role')
    # A revoked grant is history, not a role held.
    query(f'BEGIN;{held}true);{body}ROLLBACK;')
    print('PASS migration 0066 refuses to start while an agent principal holds a current role, and never revokes one', flush=True)
    check_guard(query, migration, '0066', 'agent_administration', (
        ("ALTER FUNCTION security.apply_administration(jsonb) SET work_mem='64kB';", 'security.apply_administration(jsonb)'),
        ("ALTER FUNCTION security.administration_directory() SET work_mem='64kB';", 'security.administration_directory()'),
        ('GRANT EXECUTE ON FUNCTION security.member_description(uuid,uuid,uuid) TO openetrm_runtime;', 'security.member_description(uuid,uuid,uuid)'),
        ("ALTER FUNCTION security.use_context(uuid) SET work_mem='64kB';", 'security.use_context(uuid)'),
        ('REVOKE EXECUTE ON FUNCTION security.has_permission(text) FROM openetrm_runtime;', 'security.has_permission(text)'),
        ("ALTER FUNCTION security.use_administration_context(uuid) SET work_mem='64kB';", 'security.use_administration_context(uuid)'),
        ('GRANT EXECUTE ON FUNCTION assistant.model_qualified(uuid) TO openetrm_runtime;', 'assistant.model_qualified(uuid)'),
        ("ALTER FUNCTION assistant.provider_qualified(uuid) SET work_mem='64kB';", 'assistant.provider_qualified(uuid)'),
        ('GRANT EXECUTE ON FUNCTION assistant.current_attestation(uuid) TO openetrm_runtime;', 'assistant.current_attestation(uuid)'),
        ("ALTER FUNCTION security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone) SET work_mem='64kB';",
         'security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)'),
        ('GRANT EXECUTE ON FUNCTION assistant.price_in_force(uuid,timestamp with time zone) TO openetrm_runtime;',
         'assistant.price_in_force(uuid,timestamp with time zone)'),
        ("ALTER FUNCTION assistant.current_model_version(uuid) SET work_mem='64kB';", 'assistant.current_model_version(uuid)'),
        ('GRANT EXECUTE ON FUNCTION assistant.is_display_name(text) TO openetrm_runtime;', 'assistant.is_display_name(text)'),
        ("ALTER FUNCTION assistant.is_plain_text(text) SET work_mem='64kB';", 'assistant.is_plain_text(text)')))


def run_checks(url, query, psql, manifest, environment):
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    def roles_holding(permission):
        return query("SELECT string_agg(r.code,',' ORDER BY r.code) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id "
                     f"WHERE p.permission='{permission}';")

    check('only the access administrator role manages agents', roles_holding('ManageAgents') == 'access_administrator')
    check('the trader, risk analyst and market data editor roles use agents, and each may delegate',
          roles_holding('UseAgents') == 'market_data_editor,risk_analyst,trader'
          and query("SELECT bool_and(EXISTS(SELECT 1 FROM security.role_permissions d WHERE d.role_id=p.role_id AND d.permission='CreateDelegations')) "
                    "FROM security.role_permissions p WHERE p.permission='UseAgents';") == 't')
    check('the agent reader role holds exactly ReadMarketData, ReadTrades and ReadTradeParties',
          query(f"SELECT r.code||' '||r.name||' '||string_agg(p.permission,',' ORDER BY p.permission) FROM security.roles r JOIN security.role_permissions p "
                f"ON p.role_id=r.id WHERE r.id='{READER}' GROUP BY r.code,r.name;") == 'agent_reader Agent reader ReadMarketData,ReadTradeParties,ReadTrades')
    # The guard holds even for the owner: a human never holds the agent reader role, and an agent holds no other.
    human, agent = contexts.administrator, uuid4()
    member = (f"INSERT INTO security.principals VALUES('{agent}','agent',true);"
              f"INSERT INTO security.workspace_memberships VALUES('{contexts.tenant}','{contexts.workspace}','{agent}','guard_agent','Synthetic guard agent',true,1);")

    def granted(principal, role, revoked='false'):
        return f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{principal}','{role}','{contexts.tenant}','{contexts.workspace}',{WINDOW},{revoked});"
    for setup, statement in (('', granted(human, READER)), (member, granted(agent, VIEWER)),
                             (member + granted(agent, READER), f"UPDATE security.role_grants SET role_id='{VIEWER}' WHERE principal_id='{agent}';"),
                             ('', f"UPDATE security.role_grants SET role_id='{READER}' WHERE principal_id='{human}';"),
                             # A revoked grant breaking the rule is harmless history, and stays revoked.
                             (member + granted(agent, VIEWER, 'true'), f"UPDATE security.role_grants SET revoked=false WHERE principal_id='{agent}';"),
                             (granted(human, READER, 'true'), f"UPDATE security.role_grants SET revoked=false WHERE role_id='{READER}' AND principal_id='{human}';")):
        query(f'BEGIN;{setup}{expect(statement, "23514")}ROLLBACK;')
    # An agent's grant of another role from before 0066, expired so the migration started, can still be revoked.
    query(f"BEGIN;{member}ALTER TABLE security.role_grants DISABLE TRIGGER agent_roles;"
          f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{agent}','{VIEWER}','{contexts.tenant}','{contexts.workspace}',"
          "clock_timestamp()-interval '2 days',clock_timestamp()-interval '1 day',false);"
          f"ALTER TABLE security.role_grants ENABLE TRIGGER agent_roles;UPDATE security.role_grants SET revoked=true WHERE principal_id='{agent}';ROLLBACK;")
    check('even for the owner, only an agent principal holds the agent reader role, and an agent principal holds no other role; '
          'an earlier grant breaking the rule can still be revoked')

    admin = PreviewClient(url)
    admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    viewer = PreviewClient(url)
    viewer.bootstrap(); viewer.call('profile', {'profile': 'viewer'})

    def refusal(path, body, status, code):
        answer = admin.call(path, body, expected=status)
        assert answer['code'] == code, (path, answer)
        return answer

    def listed(agent_id):
        return next(a for a in admin.call(AGENTS)['agents'] if a['id'] == agent_id)

    def create(name, operation=None):
        return admin.call(f'{AGENTS}/create', {'operationId': operation or str(uuid4()), 'name': name})

    # A qualified, enabled provider and models in each state a save tells apart (§7.1). The provider gets back
    # the enabled state it had.
    def provider():
        return next(p for p in admin.call(PROVIDERS)['providers'] if p['vendor'] == 'anthropic')

    def provider_change(action):
        return admin.call(f'{PROVIDERS}/{action}', provider_command('anthropic', provider()['version']))
    restore_provider = provider()['enabled']
    # The suite's own key, the one the loopback provider expects, and terms, attested.
    admin.call(f'{PROVIDERS}/key', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': provider()['version'], 'key': KEY})
    admin.call(f'{PROVIDERS}/terms', terms('anthropic', provider()['version']))
    provider_change('attest'); provider_change('enable')
    usd = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','usd'"

    def row_version(model_id):
        return int(query(f"SELECT version FROM assistant.models WHERE id='{model_id}';"))

    def version_of(model_id):
        return int(query(f"SELECT max(version) FROM assistant.model_versions WHERE model_id='{model_id}';"))

    def model(identifier, priced=True, enabled=True, versions=1):
        added = admin.call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': identifier,
                                             'displayName': f'Synthetic agent model {identifier[-12:]}'})
        for _ in range(versions):
            admin.call(f'{MODELS}/edit', capabilities(added['id'], row_version(added['id']), outputLimit=4096, reasoningEfforts=['low', 'high']))
        if priced:
            query(f"INSERT INTO assistant.price_revisions VALUES('{added['id']}',1,'rates',{usd},{RATES},NULL,NULL,NULL,NULL,NULL,'2020-01-01',NULL,"
                  f"'{contexts.administrator}',clock_timestamp());")
        if enabled:
            admin.call(f'{MODELS}/enable', model_command(added['id'], row_version(added['id'])))
        return added['id']

    stamp = uuid4().hex[:8]
    usable = model(f'model-agent-usable-{stamp}')
    changed = model(f'model-agent-changed-{stamp}', versions=2)
    disabled = model(f'model-agent-disabled-{stamp}', enabled=False)
    unpriced = model(f'model-agent-unpriced-{stamp}', priced=False)

    def save_body(agent_state, **changes):
        body = {'operationId': str(uuid4()), 'agentId': agent_state['id'], 'expectedVersion': agent_state['version'],
                'description': 'Answers questions about the page it is opened on.',
                'instructions': 'Quote only values you read.\nSay when something is unavailable.',
                'modelId': usable, 'modelVersion': version_of(usable), 'reasoning': 'low', 'caching': False,
                'tools': ['read_current_page', 'read_market_data', 'read_trades', 'navigate'], 'allowedRoles': ['trader', 'risk_analyst'],
                'limits': {'toolSteps': 8, 'inputTokens': 200000, 'outputTokens': 2048, 'elapsedSeconds': 120, 'retries': 2}, 'grantDays': 28}
        for key, value in changes.items():
            if key in body['limits']:
                body['limits'][key] = value
            else:
                body[key] = value
        return body

    def literal(value):
        if value is None:
            return 'NULL'
        if isinstance(value, bool):
            return str(value).lower()
        if isinstance(value, int):
            return str(value)
        if isinstance(value, list):
            return f"ARRAY[{','.join(literal(item) for item in value)}]::text[]"
        return "'" + value.replace("'", "''") + "'"

    def sql_save(body, instructions_limit=6000, grant_days_limit=90):
        """The save a body names, as the API sends it, with the runtime set's bounds or others."""
        limits = body['limits']
        values = (body['operationId'], body['agentId'], body['expectedVersion'], body['description'], body['instructions'], body['modelId'],
                  body['modelVersion'], body['reasoning'], body['caching'], body['tools'], body['allowedRoles'], limits['toolSteps'],
                  limits['inputTokens'], limits['outputTokens'], limits['elapsedSeconds'], limits['retries'], body['grantDays'],
                  instructions_limit, grant_days_limit)
        return f"assistant.save_agent_version({','.join(literal(value) for value in values)})"

    # Create.
    unchanged = query(RECORDS)
    for name in ('', ' Padded', 'Padded ', 'Tab\tname', 'x' * 81, None):
        refusal(f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': name}, 422, 'model_admin_input')
    refusal(f'{AGENTS}/create', {'operationId': '00000000-0000-0000-0000-000000000000', 'name': 'Synthetic agent'}, 422, 'model_admin_input')
    admin.call(f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': 'Synthetic agent', 'principal': str(uuid4())}, expected=400)
    check('an agent needs a request identity and a display name of 1 to 80 characters without space at either end or control characters, '
          'and a create names nothing else', query(RECORDS) == unchanged)
    operation = str(uuid4())
    guide = create(f'Synthetic page guide {stamp}', operation)
    principal = query(f"SELECT principal_id FROM assistant.agents WHERE id='{guide['id']}';")
    check('a created agent is active at version 1, with no version saved or published',
          guide['name'] == f'Synthetic page guide {stamp}' and guide['version'] == 1 and guide['state'] == 'active'
          and guide['publishedVersion'] is None and guide['newest'] is None and guide['published'] is None)
    check('creating an agent makes an enabled agent principal, bound at the manifest issuer by its own id with an agent credential, '
          'a member of the workspace under its name, and an agent reader',
          query(f"SELECT kind||' '||enabled FROM security.principals WHERE id='{principal}';") == 'agent true'
          and query(f"SELECT issuer||' '||subject||' '||login_kind FROM security.identity_bindings WHERE principal_id='{principal}';")
          == f'{ISSUER} {principal} agent_credential'
          and query(f"SELECT login_handle||'|'||display_name||'|'||enabled FROM security.workspace_memberships WHERE principal_id='{principal}' "
                    f"AND (tenant_id,workspace_id)=('{contexts.tenant}','{contexts.workspace}');")
          == f"agent_{principal.replace('-', '')}|Synthetic page guide {stamp}|true"
          and query(f"SELECT string_agg(role_id::text,',') FROM security.role_grants WHERE principal_id='{principal}' AND NOT revoked "
                    "AND granted_from<=clock_timestamp() AND clock_timestamp()<granted_to;") == READER)
    check('the create is recorded once in the Users & access audit and once in the agent audit',
          query(f"SELECT count(*) FROM security.administration_audit WHERE operation_id='{operation}' AND target_id='{principal}' "
                "AND before_state IS NULL AND after_state->>'kind'='agent';") == '1'
          and query(f"SELECT count(*) FROM assistant.administration_audit WHERE operation_id='{operation}' AND action='create_agent' "
                    f"AND agent_id='{guide['id']}';") == '1')
    records = query(RECORDS)
    check('repeating the identical create returns the same agent and writes nothing', create(f'Synthetic page guide {stamp}', operation) == guide
          and query(RECORDS) == records)
    refusal(f'{AGENTS}/create', {'operationId': operation, 'name': f'Another guide {stamp}'}, 409, 'model_admin_conflict')
    users_operation = str(uuid4())
    admin.call('admin/create', {'operationId': users_operation, 'login': f'synthetic_agent_suite_{stamp}', 'name': 'Synthetic agent suite reader',
                                'kind': 'human', 'roles': ['viewer']})
    records = query(RECORDS)
    refusal(f'{AGENTS}/create', {'operationId': users_operation, 'name': f'Another guide {stamp}'}, 409, 'model_admin_conflict')
    check('a request identity already used for another request, including a Users & access change, is refused', query(RECORDS) == records)
    refusal(f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': f'SYNTHETIC page GUIDE {stamp}'}, 409, 'model_admin_conflict')
    check('an agent name is unique in the workspace, whatever its case', query(RECORDS) == records)

    def issue(subject, login_kind):
        return query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                     f"'{ISSUER}','{subject}','{login_kind}','{contexts.tenant}','{contexts.workspace}',NULL,'preview_runtime',60);")

    def unissued(subject, login_kind):
        try:
            issue(subject, login_kind)
        except RuntimeError as error:
            assert 'Unbound or disabled identity' in str(error), error
        else:
            raise AssertionError(f'A request context was issued to {subject} as {login_kind}')
    issue(principal, 'agent_credential')
    for subject, login_kind in ((principal, 'human_interactive'), (principal, 'service_credential'),
                                (f"agent_{principal.replace('-', '')}", 'human_interactive')):
        unissued(subject, login_kind)
    check('an agent principal is issued request contexts by its agent credential alone, never as a person or a service')

    # Control-table writes take the exclusive admission only to write (§5.4): while another request is admitted,
    # refusals and repeats answer at once, and a create that writes waits for it.
    def admitted(work):
        return holding(psql, environment, 'SELECT 1 FROM security.epoch WHERE id=true FOR SHARE', work)

    def blocked_create():
        answers, racer = {}, PreviewClient(url)
        racer.bootstrap(); racer.call('profile', {'profile': 'admin'})
        thread = threading.Thread(target=lambda: answers.update(created=racer.call(
            f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': f'Synthetic waiting agent {stamp}'})))
        thread.start()
        waiting(query, 1, 'use_agent_admission')
        answers['waited'] = 'created' not in answers
        return thread, answers
    quick = admitted(lambda: (refusal(f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': ' Padded'}, 422, 'model_admin_input'),
                              create(f'Synthetic page guide {stamp}', operation)))
    thread, answers = admitted(blocked_create)
    thread.join(timeout=15)
    check('a refused or repeated create answers while another request is admitted, and a create that writes waits for exclusive admission',
          quick[1] == guide and answers['waited'] and answers['created']['name'] == f'Synthetic waiting agent {stamp}')
    waiting_agent = answers['created']
    contexts.runtime('admin', refused(f"PERFORM assistant.create_agent('{uuid4()}','Synthetic shared agent {stamp}');", 'admission_required'))
    check('outside exclusive admission a create that passes every check is refused with admission_required')

    # The 256-member limit counts agents (§5.4), in agent administration and in Users & access alike.
    members = int(query(f"SELECT count(*) FROM security.workspace_memberships WHERE (tenant_id,workspace_id)=('{contexts.tenant}','{contexts.workspace}');"))

    def filler(count):
        return ''.join(f"INSERT INTO security.principals VALUES('{p}','human',true);"
                       f"INSERT INTO security.workspace_memberships VALUES('{contexts.tenant}','{contexts.workspace}','{p}','filler_{p.hex}','Synthetic filler',true,1);"
                       for p in (uuid4() for _ in range(count)))

    def admission(function):
        return f"SET SESSION AUTHORIZATION preview_runtime;SELECT {function}('{issue('admin', 'human_interactive')}');"
    query(f"BEGIN;{filler(256 - members)}{admission('assistant.use_agent_admission')}"
          + refused(f"PERFORM assistant.create_agent('{uuid4()}','Synthetic agent over the limit');", 'admin_limit') + 'ROLLBACK;')
    filler_agent = uuid4()
    over = {'action': 'create', 'operationId': str(uuid4()), 'login': 'synthetic_over_limit', 'name': 'Synthetic account over the limit',
            'kind': 'human', 'roles': ['viewer']}
    query(f"BEGIN;{filler(255 - members)}INSERT INTO security.principals VALUES('{filler_agent}','agent',true);"
          f"INSERT INTO security.workspace_memberships VALUES('{contexts.tenant}','{contexts.workspace}','{filler_agent}','filler_agent','Synthetic filler agent',true,1);"
          f"{admission('security.use_administration_context')}"
          + refused(f'PERFORM security.apply_administration({literal(json.dumps(over))}::jsonb);', 'admin_limit') + 'ROLLBACK;')
    check('the 256-member limit counts agents, so neither agent administration nor Users & access adds a member beyond it')

    # Save.
    body = save_body(guide)
    first = admin.call(f'{AGENTS}/save', body)
    newest = first['newest']
    check('a save names every field and creates the next version as an unpublished draft, advancing the agent',
          first['version'] == 2 and first['publishedVersion'] is None and first['published'] is None and newest['version'] == 1
          and newest['description'] == body['description'] and newest['instructions'] == body['instructions']
          and newest['model']['id'] == usable and newest['model']['version'] == version_of(usable) and newest['model']['current'] is True
          and newest['reasoning'] == 'low' and newest['caching'] is False
          and newest['tools'] == sorted(body['tools']) and newest['allowedRoles'] == ['risk_analyst', 'trader']
          and newest['limits'] == body['limits'] and newest['grantDays'] == 28 and newest['savedBy'] and newest['savedAt'])
    records = query(RECORDS)
    check('repeating the identical save returns the same agent and writes nothing',
          admin.call(f'{AGENTS}/save', body) == first and query(RECORDS) == records)
    refusal(f'{AGENTS}/save', {**body, 'description': 'Another description.'}, 409, 'model_admin_conflict')
    refusal(f'{AGENTS}/save', {**save_body(first), 'operationId': users_operation}, 409, 'model_admin_conflict')
    refusal(f'{AGENTS}/save', save_body(guide), 409, 'model_admin_conflict')
    check('a save under a request identity used for another request, Users & access included, or a stale agent version is refused',
          query(RECORDS) == records)

    # An identical save sent while its original still runs, held here at the agent's row, waits for it and answers as
    # it does: the actor's principal row serializes the actor's commands.
    def overlapping(body):
        answers = {}

        def send(key):
            client = PreviewClient(url)
            client.bootstrap(); client.call('profile', {'profile': 'admin'})
            answers[key] = client.call(f'{AGENTS}/save', body)

        def race():
            threads = [threading.Thread(target=send, args=(key,)) for key in ('original', 'repeat')]
            for count, thread in enumerate(threads, 1):
                thread.start()
                waiting(query, count, 'save_agent_version')
            return threads
        for thread in holding(psql, environment, f"SELECT 1 FROM assistant.agents WHERE id='{body['agentId']}' FOR UPDATE", race):
            thread.join(timeout=15)
        return answers
    raced = overlapping(save_body(first))
    check('an identical save sent while its original still runs waits for it and answers as it does',
          'original' in raced and raced.get('repeat') == raced['original'] and raced['original']['version'] == first['version'] + 1)
    first, records = raced['original'], query(RECORDS)

    def refused_save(code, status=422, **changes):
        return refusal(f'{AGENTS}/save', save_body(first, **changes), status, code)
    for field in ('description', 'instructions', 'modelVersion', 'reasoning', 'caching', 'tools', 'allowedRoles', 'limits',
                  'grantDays', 'expectedVersion', 'toolSteps', 'inputTokens', 'outputTokens', 'elapsedSeconds', 'retries'):
        refused_save('model_admin_input', **{field: None})
    # An identity that does not bind makes the body a bad request (§5.2).
    for field in ('operationId', 'agentId', 'modelId'):
        admin.call(f'{AGENTS}/save', save_body(first, **{field: None}), expected=400)
    admin.call(f'{AGENTS}/save', {**save_body(first), 'temperature': 0.5}, expected=400)
    admin.call(f'{AGENTS}/save', save_body(first, limits={**save_body(first)['limits'], 'turns': 1}), expected=400)
    check('a save names every field, and nothing else', query(RECORDS) == records)
    for text in ('', ' Leading space', 'Trailing space ', 'Line\nbreak', 'x' * 501):
        refused_save('model_admin_input', description=text)
    for text in ('', ' Leading space', 'Trailing line\n', 'Carriage\rreturn', 'Tab\tcharacter', 'Bidi \u202e override', 'Zero width \ufeff'):
        refused_save('model_admin_input', instructions=text)
    for length in (6001, 32001):
        refused_save('instructions_too_long', instructions='x' * length)
    lines = admin.call(f'{AGENTS}/save', save_body(first, instructions='Line one.\n\n  - indented line three', description='x' * 500))
    longest_body = save_body(lines, instructions='x' * 6000)
    longest = admin.call(f'{AGENTS}/save', longest_body)
    check('a description is plain text of 1 to 500 characters, and instructions are lines of up to 6,000 characters without control '
          'or formatting characters but line feeds, and no space at either end',
          lines['newest']['instructions'] == 'Line one.\n\n  - indented line three' and len(longest['newest']['instructions']) == 6000)
    # The database applies the bounds the API passes after a repeat's replay, so a repeat answers as before under any.
    records = query(RECORDS)
    replayed = json.loads(contexts.runtime('admin', f'SELECT {sql_save(longest_body, 10, 1)}::text;').splitlines()[-1])
    check("a repeat answers as before whatever the runtime set's bounds, and writes nothing", replayed == longest and query(RECORDS) == records)
    for bounds, code in (((5999, 90), 'instructions_too_long'), ((6000, 27), 'grant_duration_exceeded'), ((None, 90), 'model_admin_input'),
                         ((6000, None), 'model_admin_input')):
        contexts.runtime('admin', refused(f"PERFORM {sql_save({**longest_body, 'operationId': str(uuid4())}, *bounds)};", code))
    check('a new save is refused beyond the bounds the API passes, and without them')
    current = longest
    for limits in ({'toolSteps': 0}, {'toolSteps': 101}, {'inputTokens': 0}, {'inputTokens': 100000001}, {'elapsedSeconds': 0},
                   {'elapsedSeconds': 3601}, {'retries': -1}, {'retries': 11}, {'outputTokens': 0}, {'grantDays': 0}):
        refused_save('model_admin_input', expectedVersion=current['version'], **limits)
    refused_save('model_limit_exceeded', expectedVersion=current['version'], outputTokens=4097)
    refused_save('grant_duration_exceeded', expectedVersion=current['version'], grantDays=91)
    bounds = admin.call(f'{AGENTS}/save', save_body(current, toolSteps=100, inputTokens=100000000, outputTokens=4096, elapsedSeconds=3600,
                                                     retries=10, grantDays=90))
    check("limits stay within their bounds, output within the model version's limit, and the grant within the runtime set's duration",
          bounds['newest']['limits'] == {'toolSteps': 100, 'inputTokens': 100000000, 'outputTokens': 4096, 'elapsedSeconds': 3600, 'retries': 10}
          and bounds['newest']['grantDays'] == 90)
    current = bounds
    for reasoning in ('medium', ''):
        refused_save('parameter_required', expectedVersion=current['version'], reasoning=reasoning)
    check('the reasoning effort is one the model version records')
    for tools in (['read_everything'], ['navigate', 'navigate'], ['show_card']):
        refused_save('model_admin_input', expectedVersion=current['version'], tools=tools)
    for roles in (['trader', 'trader'], ['synthetic_role']):
        refused_save('model_admin_input', expectedVersion=current['version'], allowedRoles=roles)
    check('tools come from the closed set and roles from the role list, each named once')
    lacking = refused_save('agent_role_lacks_permission', expectedVersion=current['version'], allowedRoles=['trader', 'viewer'])
    check('an allowed role must hold UseAgents, and the refusal names the role and the permission, also as fields',
          'Viewer' in lacking['message'] and 'UseAgents' in lacking['message'] and (lacking['role'], lacking['permission']) == ('Viewer', 'UseAgents'))
    lacking = refused_save('agent_role_lacks_permission', expectedVersion=current['version'], allowedRoles=['market_data_editor'])
    check("an allowed role must hold every permission the version's tools need",
          'Market data editor' in lacking['message'] and 'ReadTrades' in lacking['message']
          and (lacking['role'], lacking['permission']) == ('Market data editor', 'ReadTrades'))
    refused_save('agent_role_lacks_permission', expectedVersion=current['version'], allowedRoles=['agent_reader'])
    check('the agent reader role is never an allowed role')
    narrower = admin.call(f'{AGENTS}/save', save_body(current, tools=['read_market_data'], allowedRoles=['market_data_editor']))
    tool_free = admin.call(f'{AGENTS}/save', save_body(narrower, tools=[], allowedRoles=[]))
    check("a version whose tools need only what its roles hold saves, and so does one that switches no tool on or allows no role yet",
          narrower['newest']['allowedRoles'] == ['market_data_editor'] and tool_free['newest']['tools'] == []
          and tool_free['newest']['allowedRoles'] == [])
    current = tool_free

    # The model and provider checks, in §7.1's order; the exact version shown must still be current.
    agent_records = query(AGENT_RECORDS)
    refused_save('model_changed', 409, expectedVersion=current['version'], modelId=changed, modelVersion=version_of(changed) - 1)
    refused_save('model_disabled', 409, expectedVersion=current['version'], modelId=disabled, modelVersion=version_of(disabled))
    refused_save('model_unpriced', 409, expectedVersion=current['version'], modelId=unpriced, modelVersion=version_of(unpriced))
    refused_save('model_admin_conflict', 409, expectedVersion=current['version'], modelId=str(uuid4()))
    refused_save('model_admin_conflict', 409, expectedVersion=current['version'], agentId=str(uuid4()))
    refused_save('model_admin_input', expectedVersion=current['version'], modelId=EMPTY)
    refused_save('model_admin_input', expectedVersion=current['version'], agentId=EMPTY)
    # A release that repoints the adapter's mapping leaves the model's version unqualified; new provider terms recorded
    # directly leave the enabled provider without a current attestation. Both roll back with the check.
    contexts.rolled_back(SECOND_MAPPING + "UPDATE assistant.adapter_mappings SET mapping='synthetic_messages_v2' WHERE vendor='anthropic';",
                         'admin', 'human_interactive', contexts.workspace, 'NULL', refused(f'PERFORM {sql_save(save_body(current))};', 'model_unqualified'))
    provider_id = query("SELECT id FROM assistant.providers WHERE vendor='anthropic' AND (tenant_id,workspace_id)="
                        f"('{contexts.tenant}','{contexts.workspace}');")
    contexts.rolled_back(f"INSERT INTO assistant.provider_terms SELECT provider_id,version+1,account_type,retention,retention_source,retention_checked_on,"
                         "training,training_source,training_checked_on,owner_access,owner_access_source,owner_access_checked_on,recorded_by,clock_timestamp() "
                         f"FROM assistant.provider_terms WHERE provider_id='{provider_id}' ORDER BY version DESC LIMIT 1;",
                         'admin', 'human_interactive', contexts.workspace, 'NULL', refused(f'PERFORM {sql_save(save_body(current))};', 'model_provider_unqualified'))
    provider_change('disable')
    refused_save('model_provider_disabled', 409, expectedVersion=current['version'])
    provider_change('enable')
    check('a save names a known agent and the current version of a known, enabled, qualified, priced model on an enabled, qualified '
          'provider, or says which of these it is not, writing nothing', query(AGENT_RECORDS) == agent_records)

    # Publish.
    publish = {'operationId': str(uuid4()), 'agentId': guide['id'], 'expectedVersion': current['version']}
    published = admin.call(f'{AGENTS}/publish', publish)
    check('Publish publishes the newest version and advances the agent',
          published['publishedVersion'] == current['newest']['version'] and published['published'] == current['newest']
          and published['version'] == current['version'] + 1)
    records = query(RECORDS)
    check('repeating the identical Publish returns the same agent and writes nothing',
          admin.call(f'{AGENTS}/publish', publish) == published and query(RECORDS) == records)
    # Publish and Retire name the same fields, so a repeat matches the action too.
    refusal(f'{AGENTS}/retire', publish, 409, 'model_admin_conflict')
    check("a Retire under a Publish's request identity is refused, not answered as the Publish",
          listed(guide['id'])['state'] == 'active' and query(RECORDS) == records)
    refusal(f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': guide['id'], 'expectedVersion': published['version']}, 409, 'model_admin_conflict')
    refusal(f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': guide['id'], 'expectedVersion': current['version']}, 409, 'model_admin_conflict')
    check('Publish needs the current agent version and a version newer than the published one', query(RECORDS) == records)
    draft = admin.call(f'{AGENTS}/save', save_body(published))
    admin.call(f'{MODELS}/disable', model_command(usable, row_version(usable)))
    refusal(f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': guide['id'], 'expectedVersion': draft['version']}, 409, 'model_disabled')
    admin.call(f'{MODELS}/enable', model_command(usable, row_version(usable)))
    check('Publish rechecks the model and provider', listed(guide['id'])['publishedVersion'] == published['publishedVersion'])
    for statement in (f"UPDATE assistant.agents SET published_version=NULL,version=version+1 WHERE id='{guide['id']}';",
                      f"UPDATE assistant.agents SET published_version=published_version-1,version=version+1 WHERE id='{guide['id']}';",
                      f"UPDATE assistant.agents SET name='Renamed',version=version+1 WHERE id='{guide['id']}';",
                      f"UPDATE assistant.agents SET principal_id=gen_random_uuid(),version=version+1 WHERE id='{guide['id']}';",
                      f"UPDATE assistant.agents SET version=version+2 WHERE id='{guide['id']}';",
                      f"UPDATE assistant.agent_versions SET reasoning='high' WHERE agent_id='{guide['id']}';",
                      f"DELETE FROM assistant.agent_version_roles WHERE agent_id='{guide['id']}';",
                      f"DELETE FROM assistant.agents WHERE id='{guide['id']}';"):
        query(expect(statement, '55000'))
    check('even for the owner, an agent keeps its identity, name and principal, its published version only moves forward, '
          'and its versions and allowed roles are immutable')

    def version_row(agent_id, reasoning='low', output=2048):
        return (f"INSERT INTO assistant.agent_versions VALUES('{agent_id}',1000,'Synthetic description.','Synthetic instructions.','{usable}',"
                f"{version_of(usable)},'{reasoning}',false,ARRAY[]::text[],8,200000,{output},120,2,28,'{contexts.administrator}',clock_timestamp());")

    def agent_row(principal_id, version=1, published='NULL', retired='NULL'):
        return (f"INSERT INTO assistant.agents VALUES(gen_random_uuid(),'{contexts.tenant}','{contexts.workspace}','{principal_id}',"
                f"'Synthetic guarded agent',{version},{published},{retired});")
    elsewhere, other_principal, other_agent = uuid4(), uuid4(), uuid4()
    other = (f"INSERT INTO security.workspaces VALUES('{contexts.tenant}','{elsewhere}','synthetic_agent_guard');"
             f"INSERT INTO security.principals VALUES('{other_principal}','agent',true);"
             f"INSERT INTO security.workspace_memberships VALUES('{contexts.tenant}','{elsewhere}','{other_principal}','guard_agent','Synthetic guard agent',true,1);"
             f"INSERT INTO assistant.agents VALUES('{other_agent}','{contexts.tenant}','{elsewhere}','{other_principal}','Synthetic other agent',1,NULL,NULL);")
    for setup, statement, code in (('', version_row(guide['id'], reasoning='medium'), '23514'), ('', version_row(guide['id'], output=4097), '23514'),
                                   (other, version_row(other_agent), '23514'), ('', agent_row(contexts.administrator), '55000'),
                                   (member, agent_row(agent, version=2), '55000'), (member, agent_row(agent, published=1), '55000'),
                                   (member, agent_row(agent, retired='clock_timestamp()'), '55000')):
        query(f'BEGIN;{setup}{expect(statement, code)}ROLLBACK;')
    query(f"BEGIN;{version_row(guide['id'])}{other}{member}{agent_row(agent)}ROLLBACK;")
    check("even for the owner, a version names a model of its agent's workspace with an effort and an output limit its model version "
          'allows, and an agent starts at version 1 with an agent principal, unpublished and active')

    # A model change after publication marks the agent until a newer version is published.
    marked = admin.call(f'{AGENTS}/save', save_body(draft, modelId=changed, modelVersion=version_of(changed)))
    marked = admin.call(f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': guide['id'], 'expectedVersion': marked['version']})
    admin.call(f'{MODELS}/edit', capabilities(changed, row_version(changed), outputLimit=4096, reasoningEfforts=['low', 'high']))
    entry = listed(guide['id'])
    check("an agent whose published version's model version is no longer current says so",
          entry['published']['model']['current'] is False and entry['publishedVersion'] == marked['publishedVersion'])

    # Directory.
    listing = admin.call(AGENTS)
    offered = {m['id'] for m in listing['models']}
    check("the Agents directory offers the current version of each enabled, qualified, priced model on an enabled, qualified provider, "
          "the roles that use agents, and the tools with the permissions they need",
          usable in offered and changed in offered and not offered & {disabled, unpriced}
          and next(m for m in listing['models'] if m['id'] == usable)['version'] == version_of(usable)
          and [r['code'] for r in listing['roles']] == ['market_data_editor', 'risk_analyst', 'trader']
          and {t['code']: t['permissions'] for t in listing['tools']} == {
              'navigate': [], 'read_current_page': [], 'read_market_data': ['ReadMarketData'], 'read_reference_data': ['ReadMarketData'],
              'read_schedules': ['ReadMarketData'], 'read_trade_parties': ['ReadMarketData', 'ReadTradeParties'],
              'read_trades': ['ReadMarketData', 'ReadTrades']})
    check("the directory names every supported vendor and the runtime set's bounds on instructions and grants",
          listing['vendors'] == [{'code': 'anthropic', 'name': 'Anthropic'}, {'code': 'openai', 'name': 'OpenAI'}, {'code': 'google', 'name': 'Google'},
                                 {'code': 'xai', 'name': 'xAI'}, {'code': 'meta', 'name': 'Meta'}]
          and listing['bounds'] == {'instructions': 6000, 'grantDays': 90})
    check('the directory lists each agent once, and its latest changes newest first',
          sum(a['id'] == guide['id'] for a in listing['agents']) == 1
          and [a['action'] for a in listing['audit'] if a['agent'] == guide['name']][:2] == ['publish_agent', 'save_agent'])

    # Retire.
    before = listed(waiting_agent['id'])
    retired_principal = query(f"SELECT principal_id FROM assistant.agents WHERE id='{waiting_agent['id']}';")
    issue(retired_principal, 'agent_credential')
    retire = {'operationId': str(uuid4()), 'agentId': waiting_agent['id'], 'expectedVersion': before['version']}
    retired = admin.call(f'{AGENTS}/retire', retire)
    check('Retire ends the agent permanently: its principal and membership are disabled',
          retired['state'] == 'retired' and retired['version'] == before['version'] + 1
          and query(f"SELECT enabled FROM security.principals WHERE id='{retired_principal}';") == 'f'
          and query(f"SELECT enabled FROM security.workspace_memberships WHERE principal_id='{retired_principal}';") == 'f')
    unissued(retired_principal, 'agent_credential')
    check('a retired agent, issued request contexts until its Retire, is issued none')
    records = query(RECORDS)
    check('repeating the identical Retire returns the same agent and writes nothing',
          admin.call(f'{AGENTS}/retire', retire) == retired and query(RECORDS) == records)
    refusal(f'{AGENTS}/publish', retire, 409, 'model_admin_conflict')
    check("a Publish under a Retire's request identity is refused, not answered as the Retire", query(RECORDS) == records)
    for path, command in ((f'{AGENTS}/retire', {'operationId': str(uuid4()), 'agentId': waiting_agent['id'], 'expectedVersion': retired['version']}),
                          (f'{AGENTS}/save', save_body(retired)),
                          (f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': waiting_agent['id'], 'expectedVersion': retired['version']})):
        refusal(path, command, 409, 'agent_retired')
    check('a retired agent cannot be retired, saved or published again', query(RECORDS) == records)
    check('the Users & access audit records the Retire as the agent losing access',
          query(f"SELECT count(*) FROM security.administration_audit WHERE operation_id='{retire['operationId']}' "
                "AND (before_state->>'enabled')::boolean AND NOT (after_state->>'enabled')::boolean;") == '1')
    blocked_retire = admitted(lambda: refusal(f'{AGENTS}/retire', {'operationId': str(uuid4()), 'agentId': waiting_agent['id'],
                                                                   'expectedVersion': retired['version']}, 409, 'agent_retired'))
    second = create(f'Synthetic second retired agent {stamp}')

    def waiting_retire():
        answers, racer = {}, PreviewClient(url)
        racer.bootstrap(); racer.call('profile', {'profile': 'admin'})
        thread = threading.Thread(target=lambda: answers.update(retired=racer.call(
            f'{AGENTS}/retire', {'operationId': str(uuid4()), 'agentId': second['id'], 'expectedVersion': second['version']})))
        thread.start()
        waiting(query, 1, 'use_agent_admission')
        answers['waited'] = 'retired' not in answers
        return thread, answers
    thread, answers = admitted(waiting_retire)
    thread.join(timeout=15)
    check('a refused Retire answers while another request is admitted, and a Retire that writes waits for exclusive admission',
          blocked_retire['code'] == 'agent_retired' and answers['waited'] and answers['retired']['state'] == 'retired')

    # Authority.
    records = query(RECORDS)
    for path, command in ((AGENTS, None), (f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': f'Viewer agent {stamp}'})):
        viewer.call(path, command, expected=403)
    check('agent administration needs ManageAgents', query(RECORDS) == records)
    latest = listed(guide['id'])
    for statement in ('PERFORM assistant.agent_directory();', f"PERFORM assistant.create_agent('{uuid4()}','Delegated agent {stamp}');",
                      f'PERFORM {sql_save(save_body(latest))};', f"PERFORM assistant.publish_agent('{uuid4()}','{guide['id']}',{latest['version']});",
                      f"PERFORM assistant.retire_agent('{uuid4()}','{guide['id']}',{latest['version']});"):
        contexts.delegated_agent(expect(statement, '42501'), permission='ManageAgents')
    check('an agent never administers agents, even under a current delegation of ManageAgents')
    isolated = json.loads(contexts.second_workspace('SELECT assistant.agent_directory()::text;').splitlines()[-1])
    check("another workspace's directory lists none of this workspace's agents, models or agent changes",
          not {a['id'] for a in isolated['agents']} & {guide['id'], waiting_agent['id'], second['id']}
          and not {m['id'] for m in isolated['models']} & offered
          and not {a['operationId'] for a in isolated['audit']} & {a['operationId'] for a in listing['audit']})

    if not restore_provider:
        provider_change('disable')
    print(f'{passed} agent administration checks passed.', flush=True)
    return passed
