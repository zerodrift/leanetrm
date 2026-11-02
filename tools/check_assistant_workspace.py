"""Page operations, explicit human approvals and versioned page guidance; newly owned synthetic preview only."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from assistant_context import Contexts, holding, waiting
from assistant_fixture import Fixture
from fake_model_provider import recording, stream, tool_use
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest, fake, trap, psql, environment):
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    def client(profile):
        result = PreviewClient(url)
        result.bootstrap()
        result.call('profile', {'profile': profile})
        return result

    admin, trader = client('admin'), client('trader')
    stamp = uuid4().hex[:8]
    contexts = Contexts(query, manifest)
    prompt_path = 'settings/ai/page-prompts'
    prompt = {'operationId': str(uuid4()), 'page': 'users_access', 'expectedVersion': 0,
              'instructions': 'Synthetic page guidance: explain the exact account and permission changes before proposing them.'}
    saved = admin.call(prompt_path + '/save', prompt)
    check('page prompt saves one immutable version and retries return it', saved['version'] == 1 and admin.call(prompt_path + '/save', prompt) == saved)
    same = admin.call(prompt_path + '/save', {**prompt, 'operationId': str(uuid4()), 'expectedVersion': 1})
    check('identical page guidance is a no-op', same['version'] == 1)
    check('stale prompt versions refuse', admin.call(prompt_path + '/save', {**prompt, 'operationId': str(uuid4()), 'instructions': 'Different guidance.'}, expected=409)['code'] == 'page_prompt_version_conflict')
    trader.call(prompt_path, expected=403)
    editor_login = 'synthetic_prompt_' + stamp
    editor = client('admin')
    target = editor.call('admin/create', {'operationId': str(uuid4()), 'login': editor_login, 'name': 'Synthetic prompt editor', 'kind': 'human', 'roles': ['assistant_prompt_editor']})
    editor.call('profile', {'profile': editor_login})
    check('a delegated prompt editor reads guidance without agent administration', editor.call(prompt_path)['configuration']['prompts'][0]['version'] == 1)
    editor.call('settings/ai/agents', expected=403)
    editor.call(prompt_path + '/save', {'operationId': str(uuid4()), 'page': 'risk_jobs', 'expectedVersion': 0, 'instructions': 'Synthetic risk guidance.'})

    owner_login = 'synthetic_assistant_admin_' + stamp
    owner = client('admin')
    owner.call('admin/create', {'operationId': str(uuid4()), 'login': owner_login, 'name': 'Synthetic assistant administrator', 'kind': 'human',
                               'roles': ['access_administrator', 'trader', 'market_data_editor', 'risk_analyst']})
    owner.call('profile', {'profile': owner_login})
    fixture = Fixture(admin, query, manifest, contexts.administrator, stamp, 'workspace')
    fixture.qualify()
    identifier = 'synthetic-workspace-' + stamp
    model = fixture.model(identifier)
    tools = ['read_current_page', 'read_workspace_page', 'discover_page_operations', 'read_page_operation', 'propose_page_action', 'navigate']
    agent = fixture.publish(fixture.save(fixture.create('workspace operations'), model, tools=tools, roles=['trader'], toolSteps=12))
    owner.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': agent['id'], 'version': 1})
    frame = initial_frame(owner.call('workspace/navigation'), '2020-12-18')

    def ask(requests, page='users_access', session=None, state=None, frame_id=None, operational=False):
        session = session or owner
        state = state or agent
        before = len(fake.received)
        fake.respond(stream(tool_use(identifier, [(str(n), tool, data) for n, (tool, data) in enumerate(requests)])), stream(recording(identifier, text='Synthetic reviewed result.')))
        turn = session.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': state['id'], 'version': 1,
                         'question': 'Inspect the selected page and prepare only the explicitly requested synthetic change.',
                         'pageReport': {'page': page, 'selection': {}}, 'knowledge': {'mode': 'current', 'contextId': None, 'frameId': None if operational else frame_id or frame['id']}})['turn']
        deadline = time.monotonic() + 20
        while True:
            conversation = session.call(f"assistant/conversations/{turn['conversationId']}?after=0")
            current = conversation['turns'][0]
            if current['state'] in ('completed', 'failed', 'cancelled', 'interrupted'):
                break
            assert time.monotonic() < deadline, current
            time.sleep(.05)
        assert current['state'] == 'completed', (current, conversation)
        calls = [json.loads(r['body']) for r in fake.received[before:]]
        results = [json.loads(part['content']) for part in calls[-1]['messages'][-1]['content'] if part['type'] == 'tool_result']
        return current, results, calls

    turn, results, calls = ask([('read_current_page', {}), ('discover_page_operations', {'page': 'users_access', 'operation': 'admin.update_v1'})])
    check('an administrator page is read through its ordinary authorized directory', 'directory' in results[0] and 'data' in results[0]['directory'])
    check('operation discovery returns the actual handler request contract', results[1]['operations'][0]['input']['body']['contract'] == 'AdminUpdate')
    check('the admitted page guidance reaches the provider after the fixed policy', prompt['instructions'] in calls[0]['system'][0]['text'])
    check('the turn pins the exact page prompt version', query(f"SELECT version FROM assistant.turn_prompt_pins WHERE turn_id='{turn['id']}';") == '1')
    admin.call(prompt_path + '/save', {**prompt, 'operationId': str(uuid4()), 'expectedVersion': 1, 'instructions': 'Synthetic next guidance.'})
    check('later prompt edits do not alter an admitted turn', query(f"SELECT version FROM assistant.turn_prompt_pins WHERE turn_id='{turn['id']}';") == '1')

    command = {'operation': 'admin.update_v1', 'inputJson': json.dumps({'body': {'operationId': str(uuid4()), 'id': target['id'], 'expectedVersion': target['version'],
                'name': 'Synthetic authorized prompt editor', 'enabled': True, 'roles': ['assistant_prompt_editor']}, 'parameters': {}})}
    refusal_turn, results, _ = ask([('read_page_operation', command)])
    check('a write is refused by the read tool before execution', results[0]['code'] == 'approval_required')
    before = query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{target['id']}';")
    proposed, results, _ = ask([('propose_page_action', command)])
    check('a model proposal saves no business change', results[0]['status'] == 'awaiting_user_approval' and query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{target['id']}';") == before)
    reference = {'turnId': proposed['id'], 'step': 1}
    review = owner.call('assistant/actions/review', reference)
    check('Review returns the exact retained change with a stable request identity', review['body']['name'] == 'Synthetic authorized prompt editor' and owner.call('assistant/actions/review', reference) == review)
    trader.call('assistant/actions/review', reference, expected=404)
    outcome = owner.call('assistant/actions/authorize', reference)
    check('a separate human authorization executes the ordinary operation', query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{target['id']}';") == 'Synthetic authorized prompt editor')
    check('retrying authorization returns the first outcome', owner.call('assistant/actions/authorize', reference) == outcome)
    check('approval is retained once with the effect', query(f"SELECT count(*) FROM assistant.action_authorizations WHERE turn_id='{proposed['id']}';") == '1')

    trading = {'snapshotId': frame['id'], 'convention': 'contract_lot_execution_pnl_v2', 'knowledgeMode': 'current'}
    offer = owner.call('futures/catalog', trading)
    contract = next(c for c in offer['contracts'] if c['curve'] == 'future')
    booked = owner.call('futures/book', {**trading, **trade_booking(), 'operationId': str(uuid4()), 'bookId': offer['books'][0]['id'],
        'curve': 'future', 'contractId': contract['id'], 'lots': '2', 'executionPrice': '3.1', 'memo': 'Synthetic approved-format check'})
    format_command = {'operation': 'trade-formats.save_v1', 'inputJson': json.dumps({'body': {'tradeId': booked['tradeId'], 'role': 'trade',
        'expectedRevision': 0, 'format': {'money': 2, 'price': 4, 'quantity': 3, 'factor': 6}}, 'parameters': {}})}
    format_turn, _, _ = ask([('propose_page_action', format_command)])
    format_reference = {'turnId': format_turn['id'], 'step': 1}
    first = owner.call('assistant/actions/authorize', format_reference)
    check('a command without a native request id replays its atomic approval outcome', owner.call('assistant/actions/authorize', format_reference) == first and first['revision'] == 1)
    check('the approval outcome commits once with the command', query(f"SELECT count(*) FROM assistant.action_outcomes WHERE turn_id='{format_turn['id']}';") == '1')

    # Legacy publication-prefix contexts and binder aliases cannot escape the question's exact certificate.
    _, discovery, _ = ask([('discover_page_operations', {'page': 'schedules', 'operation': 'date-schedules.catalog_v1'})])
    certificate = discovery[0]['context']
    bad_context = {'operation': 'date-schedules.catalog_v1', 'inputJson': json.dumps({'body': {'context': {**certificate, 'contextId': None, 'effectiveOn': '2021-01-01'}}, 'parameters': {}})}
    _, refused, _ = ask([('read_page_operation', bad_context)])
    check('a logical-prefix schedule context cannot replace the pinned certificate', refused[0]['code'] == 'operation_context_mismatch')
    omitted = json.loads(bad_context['inputJson']); omitted['body']['context'].pop('knownThrough')
    _, refused, _ = ask([('read_page_operation', {**bad_context, 'inputJson': json.dumps(omitted)})])
    check('omitting a nullable context member cannot bypass the pin', refused[0]['code'] == 'operation_context_mismatch')

    observation = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18'))['observationIds'][0]
    correction = {'operation': 'corrections_v1', 'inputJson': json.dumps({'body': {'snapshotId': frame['id'], 'observationId': observation, 'value': '3.14'}, 'parameters': {}})}
    correction_turn, _, _ = ask([('propose_page_action', correction)])
    correction_reference = {'turnId': correction_turn['id'], 'step': 1}
    first_correction = owner.call('assistant/actions/authorize', correction_reference)
    check('retrying a correction replays the observation and frame identities', owner.call('assistant/actions/authorize', correction_reference) == first_correction)

    # A proposal can expire while its ordinary command waits, without any role or epoch change. Its final guard must
    # roll back both the account mutation and the approval receipt rather than commit under the human's lasting role.
    # The administrative directory names its user records; the exact expected head is read from the owned fixture.
    expected = int(query(f"SELECT version FROM security.workspace_memberships WHERE principal_id='{target['id']}';"))
    expiring_command = {'operation': 'admin.update_v1', 'inputJson': json.dumps({'body': {'operationId': str(uuid4()), 'id': target['id'], 'expectedVersion': expected,
        'name': 'Synthetic expired change', 'enabled': True, 'roles': ['assistant_prompt_editor']}, 'parameters': {}})}
    expires_turn, _, _ = ask([('propose_page_action', expiring_command)])
    delegation = query(f"SELECT delegation_id FROM assistant.turns WHERE id='{expires_turn['id']}';")
    previous_expiry = query(f"SELECT granted_to::text FROM security.agent_delegations WHERE id='{delegation}';")
    query(f"UPDATE security.agent_delegations SET granted_to=clock_timestamp()+interval '3 seconds' WHERE id='{delegation}';")
    try:
        with ThreadPoolExecutor(max_workers=1) as executor:
            def blocked():
                future = executor.submit(owner.call, 'assistant/actions/authorize', {'turnId': expires_turn['id'], 'step': 1}, expected=403)
                waiting(query, 1, 'security.apply_administration')
                time.sleep(3.1)
                return future
            future = holding(psql, environment, f"SELECT principal_id FROM security.workspace_memberships WHERE principal_id='{target['id']}' FOR UPDATE", blocked)
            future.result(timeout=10)
        check('delegation expiry during a command rolls back the effect and approval', query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{target['id']}';") == 'Synthetic authorized prompt editor' and
              query(f"SELECT count(*) FROM assistant.action_authorizations WHERE turn_id='{expires_turn['id']}';") == '0')
    finally:
        query(f"UPDATE security.agent_delegations SET granted_to='{previous_expiry}'::timestamptz WHERE id='{delegation}';")

    principal = query(f"SELECT principal_id FROM security.identity_bindings WHERE subject='{owner_login}';")
    query(f"INSERT INTO automation.installation_operators VALUES('{principal}','ManageAllJobs',clock_timestamp()+interval '1 hour');")
    operator_turn, _, _ = ask([('read_current_page', {})])
    query(f"UPDATE automation.installation_operators SET expires_at=clock_timestamp()-interval '1 second' WHERE principal_id='{principal}';")
    hidden = owner.call(f"assistant/conversations/{operator_turn['conversationId']}?after=0")['turns'][0]
    check('expired installation authority hides retained generic-page answers', hidden['hidden'] == 'access_changed')

    admin_agent = fixture.publish(fixture.save(fixture.create('administration only'), model, tools=tools, roles=['access_administrator'], toolSteps=12))
    admin.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': admin_agent['id'], 'version': 1})
    admin.call('workspace/navigation', expected=403)
    admin_turn, results, _ = ask([('read_current_page', {}), ('navigate', {'page': 'settings_ai', 'target': None})], session=admin, state=admin_agent, operational=True)
    check('an administrator without financial rights can read and navigate administrative pages', 'directory' in results[0] and results[1]['target']['page'] == 'settings_ai')
    check('operational questions retain no invented market context or snapshot', query(f"SELECT knowledge_mode||':'||(context_id IS NULL)::text||':'||(pins IS NULL)::text FROM assistant.turns WHERE id='{admin_turn['id']}';") == 'operational:true:true')
    admin_proposal, _, _ = ask([('propose_page_action', expiring_command)], session=admin, state=admin_agent, operational=True)
    admin_reference = {'turnId': admin_proposal['id'], 'step': 1}
    admin.call('assistant/actions/authorize', admin_reference)
    check('an administrator authorizes an exact proposal without gaining market access', query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{target['id']}';") == 'Synthetic expired change')
    admin.call('workspace/navigation', expected=403)

    # Discovery itself is scoped; no secret replacement or arbitrary request primitive enters a model contract.
    _, results, _ = ask([('discover_page_operations', {'page': 'settings_ai', 'operation': 'settings.ai.providers.key_v1'})])
    check('provider credential replacement is absent from the assistant catalog', results[0]['operations'] == [])
    check('no model request reaches configured proxy traps', trap.connections == 0)
    print(f'PASS {passed} assistant workspace integration checks', flush=True)
