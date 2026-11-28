"""Page operations, explicit human approvals and versioned page guidance; newly owned synthetic preview only."""
import json
import time
import threading
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from assistant_context import Contexts, holding, waiting
from assistant_fixture import Fixture
from fake_model_provider import recording, stream, tool_use
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest, fake, trap, psql, environment, tracker):
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

    application = 'Synthetic installation instructions.'
    tool_guide = 'Synthetic operation discovery instructions.'
    for scope, text in (('application', application), ('tool:discover_page_operations', tool_guide)):
        admin.call(prompt_path + '/save', {'operationId': str(uuid4()), 'page': scope, 'expectedVersion': 0, 'instructions': text})

    owner_login = 'synthetic_assistant_admin_' + stamp
    owner = client('admin')
    owner.call('admin/create', {'operationId': str(uuid4()), 'login': owner_login, 'name': 'Synthetic assistant administrator', 'kind': 'human',
                               'roles': ['access_administrator', 'trader', 'market_data_editor', 'risk_analyst']})
    owner.call('profile', {'profile': owner_login})
    fixture = Fixture(admin, query, manifest, contexts.administrator, stamp, 'workspace')
    fixture.qualify()
    identifier = 'synthetic-workspace-' + stamp
    model = fixture.model(identifier)
    tools = ['present_walkthrough', 'read_page_digest', 'read_current_page', 'read_workspace_page', 'discover_page_operations', 'read_page_operation', 'propose_page_action', 'navigate']
    agent = fixture.publish(fixture.save(fixture.create('workspace operations'), model, tools=tools, roles=['trader'], toolSteps=12))
    owner.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': agent['id'], 'version': 1})
    # Consent for the new tool is tied to an administrator-selected version; an earlier workspace grant cannot widen.
    legacy = fixture.publish(fixture.save(fixture.create('digest consent'), model, tools=['read_workspace_page'], roles=['trader']))
    owner.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': legacy['id'], 'version': legacy['publishedVersion']})
    upgraded = fixture.publish(fixture.save(legacy, model, tools=['read_workspace_page', 'read_page_digest'], roles=['trader']))
    denied = owner.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': upgraded['id'], 'version': upgraded['publishedVersion'],
                       'question': 'Read this synthetic page.', 'pageReport': {'page': 'users_access', 'selection': {}},
                       'knowledge': {'mode': 'current', 'contextId': None, 'frameId': None}}, expected=403)
    check('adding the digest tool never silently widens an existing workspace grant', denied['code'] == 'grant_required')
    frame = initial_frame(owner.call('workspace/navigation'), '2020-12-18')

    fake.respond(stream(recording(identifier, text='Synthetic conceptual answer.')))
    conceptual = owner.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': agent['id'], 'version': 1,
        'question': 'Explain a synthetic concept without reading business data.', 'pageReport': {'page': 'users_access', 'selection': {}},
        'knowledge': {'mode': 'current', 'contextId': None, 'frameId': frame['id']}})['turn']
    deadline = time.monotonic() + 20
    while query(f"SELECT state FROM assistant.turns WHERE id='{conceptual['id']}';") not in ('completed', 'failed', 'cancelled', 'interrupted'):
        assert time.monotonic() < deadline, 'conceptual turn did not finish'
        time.sleep(.05)
    check('a text-only question neither copies market inputs nor captures a financial context',
          query(f"SELECT state='completed' AND pins IS NULL AND context_id IS NULL AND recorded_at IS NULL FROM assistant.turns WHERE id='{conceptual['id']}';") == 't')

    def ask(requests, page='users_access', session=None, state=None, frame_id=None, operational=False, report=None, knowledge=None, question=None, answer='Synthetic reviewed result.', timeout=20):
        session = session or owner
        state = state or agent
        before = len(fake.received)
        if requests:
            fake.respond(stream(tool_use(identifier, [(str(n), tool, data) for n, (tool, data) in enumerate(requests)])))
        fake.respond(stream(recording(identifier, text=answer)))
        turn = session.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': state['id'], 'version': 1,
                         'question': question or 'Inspect the selected page and prepare only the explicitly requested synthetic change.',
                         'pageReport': report or {'page': page, 'selection': {}}, 'knowledge': knowledge or {'mode': 'current', 'contextId': None, 'frameId': None if operational else frame_id or frame['id']}})['turn']
        deadline = time.monotonic() + timeout
        while True:
            conversation = session.call(f"assistant/conversations/{turn['conversationId']}?after=0")
            current = conversation['turns'][0]
            if current['state'] in ('completed', 'failed', 'cancelled', 'interrupted'):
                break
            assert time.monotonic() < deadline, current
            time.sleep(.05)
        assert current['state'] == 'completed', (current, conversation)
        calls = [json.loads(r['body']) for r in fake.received[before:]]
        results = [json.loads(part['content']) for part in calls[-1]['messages'][-1]['content'] if part['type'] == 'tool_result'] if requests else []
        return current, results, calls

    park_report = {'page': 'blotter', 'selection': {'view': 'trade', 'mode': None, 'bookIds': None, 'portfolioIds': None,
        'groupBy': None, 'measures': None, 'tradeId': str(uuid4()), 'instrumentType': 'physical_gas_park_loan', 'revision': 1},
        'layout': {'pane': {'details': True, 'charts': False}, 'poppedOut': False},
        'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'primaryMeasure': None},
        'focus': {'tradeIds': [], 'pivotCell': None}}
    park_turn, park_results, _ = ask([('read_current_page', {})], report=park_report)
    check('a selected Park and Loan admits questions and refuses its unqualified read without financial acquisition',
          park_results[0]['code'] == 'page_not_supported'
          and query(f"SELECT pins IS NULL AND context_id IS NULL AND recorded_at IS NULL FROM assistant.turns WHERE id='{park_turn['id']}';") == 't')

    turn, results, calls = ask([('read_current_page', {}), ('discover_page_operations', {'page': 'users_access', 'operation': 'admin.update_v1'})])
    check('an administrator page is read through its ordinary authorized directory', 'directory' in results[0] and 'data' in results[0]['directory'])
    check('operation discovery returns the actual handler request contract', results[1]['operations'][0]['input']['body']['contract'] == 'AdminUpdate')
    _, discovery_results, _ = ask([('discover_page_operations', {'page': 'all', 'operation': None})])
    discovery = {operation['id']: operation for operation in discovery_results[0]['operations']}
    check('full authorized discovery completes with exact identities and human-approved command effects',
          {'swaps.catalog_v1', 'swaps.price_v1', 'swaps.book_v1', 'admin.update_v1'} <= discovery.keys()
          and discovery['swaps.book_v1']['effect'] == 'requires_user_approval'
          and discovery['admin.update_v1']['effect'] == 'requires_user_approval'
          and discovery['swaps.catalog_v1']['effect'] == 'read_or_preview')
    check('only the admitted application, agent and page guidance reaches the provider',
          calls[0]['system'][0]['text'] == application + '\n\nSynthetic agent instructions.\n\n' + prompt['instructions'])
    check('tool guidance is explicitly configured and absent guidance remains empty',
          {t['name']: t['description'] for t in calls[0]['tools']} == {name: tool_guide if name == 'discover_page_operations' else '' for name in tools})
    check('a pure administrative read and broad discovery do not acquire market context',
          query(f"SELECT context_id IS NULL AND pins IS NULL FROM assistant.turns WHERE id='{turn['id']}';") == 't')
    check('application and tool guidance pins include explicit absence',
          query(f"SELECT count(*) FROM assistant.turn_instruction_pins WHERE turn_id='{turn['id']}';") == str(len(tools)+1)
          and query(f"SELECT version IS NULL FROM assistant.turn_instruction_pins WHERE turn_id='{turn['id']}' AND scope='tool:navigate';") == 't')
    check('the turn pins the exact page prompt version', query(f"SELECT version FROM assistant.turn_prompt_pins WHERE turn_id='{turn['id']}';") == '1')
    admin.call(prompt_path + '/save', {**prompt, 'operationId': str(uuid4()), 'expectedVersion': 1, 'instructions': 'Synthetic next guidance.'})
    check('later prompt edits do not alter an admitted turn', query(f"SELECT version FROM assistant.turn_prompt_pins WHERE turn_id='{turn['id']}';") == '1')

    # Edits during a provider wait affect the next question, including guidance that was absent at admission.
    release = threading.Event()
    before = len(fake.received)
    fake.respond(stream('', release, tool_use(identifier, [('pinned_prompt_read', 'discover_page_operations', {'page': 'users_access', 'operation': None})])),
                 stream(recording(identifier, text='Synthetic unchanged prompt answer.')))
    pending = owner.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': agent['id'], 'version': 1,
        'question': 'Inspect synthetic operation metadata.', 'pageReport': {'page': 'users_access', 'selection': {}},
        'knowledge': {'mode': 'current', 'contextId': None, 'frameId': None}})['turn']
    try:
        deadline = time.monotonic() + 20
        while len(fake.received) == before:
            assert time.monotonic() < deadline, 'provider did not receive the pending question'
            time.sleep(.05)
        for scope, version, text in (('application', 1, 'Synthetic revised application guidance.'),
                                     ('tool:discover_page_operations', 1, 'Synthetic revised discovery guidance.'),
                                     ('tool:navigate', 0, 'Synthetic newly configured navigation guidance.')):
            admin.call(prompt_path + '/save', {'operationId': str(uuid4()), 'page': scope, 'expectedVersion': version, 'instructions': text})
    finally:
        release.set()
    deadline = time.monotonic() + 20
    while query(f"SELECT state FROM assistant.turns WHERE id='{pending['id']}';") == 'running':
        assert time.monotonic() < deadline, 'pending question did not finish'
        time.sleep(.05)
    pending_calls = [json.loads(r['body']) for r in fake.received[before:]]
    check('application and tool guidance, including explicit absence, remain fixed across an in-flight edit',
          len(pending_calls) == 2 and pending_calls[0]['system'] == pending_calls[1]['system']
          and pending_calls[0]['tools'] == pending_calls[1]['tools']
          and pending_calls[1]['system'][0]['text'].startswith(application + '\n\n')
          and next(t['description'] for t in pending_calls[1]['tools'] if t['name'] == 'navigate') == '')

    for page in ('dashboard', 'vol_surfaces'):
        admin.call(prompt_path + '/save', {'operationId': str(uuid4()), 'page': page, 'expectedVersion': 0, 'instructions': 'Synthetic identity-only guidance.'})
        identity_turn, identity_results, _ = ask([('read_page_digest', {'mode': 'overview'}), ('read_current_page', {})], page=page)
        check(page + ' admits an empty report and pins guidance without inventing widget values',
              query(f"SELECT page||':'||version FROM assistant.turn_prompt_pins WHERE turn_id='{identity_turn['id']}';") == page + ':1'
              and identity_results[0]['result']['text'].startswith('Page: ' + page + ' |')
              and identity_results[0]['result']['text'].endswith('Selection: {}')
              and identity_results[0]['result']['targets'] == [] and identity_results[1]['code'] == 'page_not_supported')
    # Risk names the view it shows (D392): a report naming none is refused before admission, and the page read and digest name it.
    unnamed = owner.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': agent['id'], 'version': 1,
                                                 'question': 'Which Risk view is this?', 'pageReport': {'page': 'risk_jobs', 'selection': {}},
                                                 'knowledge': {'mode': 'current', 'contextId': None, 'frameId': None}}, expected=422)
    check('a Risk report naming no view is refused before admission', unnamed['code'] == 'page_report_invalid')
    _, risk_results, _ = ask([('read_workspace_page', {'page': 'risk_jobs'}), ('read_page_digest', {'mode': 'overview'})],
                             report={'page': 'risk_jobs', 'selection': {'view': 'var_es'}}, operational=True)
    check('the Risk page read and digest name the view the question was asked from',
          risk_results[0].get('selection') == {'view': 'var_es'} and '\nView: VaR / ES\n' in risk_results[1]['result']['text'])
    admin.call(prompt_path + '/save', {'operationId': str(uuid4()), 'page': 'crude_setup', 'expectedVersion': 0,
                                      'instructions': 'Synthetic delivery guidance: use native contextual field definitions.'})
    _, setup_results, _ = ask([('read_current_page', {})], page='crude_setup')
    setup = setup_results[0]['directory']['data']
    check('crude setup now reads contextual native definitions instead of an identity-only refusal',
          bool(setup['fieldDescriptions']) and setup['versions'] == [] and setup['proposals'] == [])
    missing_date = owner.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None,
        'agentId': agent['id'], 'version': 1, 'question': 'Inspect this facility.',
        'pageReport': {'page': 'facility_quantities', 'selection': {}},
        'knowledge': {'mode': 'current', 'contextId': None, 'frameId': frame['id']}}, expected=422)
    check('facility questions require their operating date rather than borrowing a financial frame', missing_date['code'] == 'snapshot_missing')
    listed = admin.call(prompt_path)['pages']
    check('the entire API page catalog is accepted by SQL prompt admission', all(query(f"SELECT assistant.is_prompt_scope('{p['id']}');") == 't' for p in listed))

    _, entry_results, _ = ask([('read_workspace_page', {'page': 'blotter_new_swap'})], page='blotter_new_swap')
    swap_operations = {op['id']: op for op in entry_results[0]['operations']}
    check('New swap discovers its native catalog, pricing and human-approved booking operation',
          {'swaps.catalog_v1', 'swaps.price_v1', 'swaps.book_v1'} <= swap_operations.keys()
          and swap_operations['swaps.book_v1']['effect'] == 'requires_user_approval'
          and not {'futures.book_v1', 'swaps.amend_v1', 'admin.update_v1'} & swap_operations.keys())
    check('an authorized workspace page read issues its own page handle', entry_results[0].get('target') == 'n1')
    _, empty_results, _ = ask([('read_workspace_page', {'page': 'blotter_risk_lens'}), ('read_workspace_page', {'page': 'blotter_new_swap'})], page='blotter_new_swap')
    check('a page without available operations issues no handle and consumes no numbering', 'target' not in empty_results[0] and empty_results[1].get('target') == 'n1')
    _, entry_plan_results, _ = ask([('read_workspace_page', {'page': 'blotter_new_swap'}),
                                    ('present_walkthrough', {'planJson': json.dumps([{'kind': 'go', 'target': 'n1'}, {'kind': 'pause', 'page': 'blotter_new_swap'}])})],
                                   page='blotter_new_swap')
    check('a workspace-issued New swap handle can be presented without a command', entry_plan_results[1].get('status') == 'presented')

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
        'curve': 'future', 'contractId': contract['id'], 'definitionRevisionId': contract['definitionRevisionId'], 'lots': '2', 'executionPrice': '3.1', 'memo': 'Synthetic approved-format check'})
    format_command = {'operation': 'trade-formats.save_v1', 'inputJson': json.dumps({'body': {'tradeId': booked['tradeId'], 'role': 'trade',
        'expectedRevision': 0, 'format': {'money': 2, 'price': 4, 'quantity': 3, 'factor': 6}}, 'parameters': {}})}
    format_turn, _, _ = ask([('propose_page_action', format_command)])
    format_reference = {'turnId': format_turn['id'], 'step': 1}
    first = owner.call('assistant/actions/authorize', format_reference)
    check('a command without a native request id replays its atomic approval outcome', owner.call('assistant/actions/authorize', format_reference) == first and first['revision'] == 1)
    check('the approval outcome commits once with the command', query(f"SELECT count(*) FROM assistant.action_outcomes WHERE turn_id='{format_turn['id']}';") == '1')

    # Digest values come from the ordinary page valuation, and an admitted retained ask never reads later corrections.
    digest_selection = {'view': 'blotter', 'mode': 'trades', 'bookIds': [offer['books'][0]['id']], 'portfolioIds': [],
                        'groupBy': [], 'measures': [], 'tradeId': None, 'instrumentType': None, 'revision': None}
    digest_report = {'page': 'blotter', 'selection': digest_selection,
                     'view': {**{k: digest_selection[k] for k in ('mode', 'bookIds', 'portfolioIds', 'groupBy')},
                              'measures': ['executionPnl'], 'primaryMeasure': 'executionPnl'}}
    controls_turn, controls_results, _ = ask([('read_page_digest', {'mode': 'controls'})], report=digest_report)
    controls = controls_results[0]['result']
    check('controls digest returns authorized native entry handles without portfolio values or record evidence',
          controls['text'].endswith('Screen controls only. Financial values were not read.')
          and 'Positions:' not in controls['text'] and 'Totals:' not in controls['text']
          and [t['label'] for t in controls['targets']] == ['Blotter', 'New trade', 'New futures trade', 'New swap']
          and all('tradeId' not in json.dumps(t) for t in controls['targets'])
          and len(controls['screens']) == 4)
    controls_plan = [{'kind': 'go', 'target': 'n4'}, {'kind': 'pause', 'page': 'blotter_new_swap'}]
    _, controls_presented, _ = ask([('read_page_digest', {'mode': 'controls'}), ('present_walkthrough', {'planJson': json.dumps(controls_plan)})], report=digest_report)
    check('controls metadata alone can open the native entry page without redundant discovery', controls_presented[1].get('status') == 'presented')
    _, control_selection, _ = ask([('read_page_digest', {'mode': 'controls'}), ('present_walkthrough', {'planJson': json.dumps([{'kind': 'select', 'page': 'blotter', 'focus': {'tradeIds': [booked['tradeId']], 'pivotCell': None}}])})], report=digest_report)
    check('controls metadata cannot certify a selected trade as server-read evidence', control_selection[1].get('code') == 'walkthrough_plan_invalid')
    digest_turn, digest_results, _ = ask([('read_page_digest', {'mode': 'overview'})], report=digest_report)
    digest = digest_results[0]
    check('the Blotter digest has ordered server counts, totals, extrema and issued targets',
          digest['operation'] == 'workspace.digest_v1' and 'Positions: future=1' in digest['result']['text']
          and digest['result']['text'].index('Book:') < digest['result']['text'].index('Largest:') < digest['result']['text'].index('Smallest:')
          and len(digest['result']['targets']) == 2 and len(digest['result']['text'].encode()) <= 4096)
    _, numbered, _ = ask([('read_workspace_page', {'page': 'blotter_new_swap'}), ('read_page_digest', {'mode': 'overview'}), ('read_workspace_page', {'page': 'blotter_new_future'})], report=digest_report)
    check('workspace and digest reads share one turn-local target sequence', numbered[0]['target'] == 'n1'
          and [t['handle'] for t in numbered[1]['result']['targets']] == ['n2', 'n3'] and numbered[2]['target'] == 'n4')
    plan = [{'kind': 'sort', 'page': 'blotter', 'column': 'presentValue', 'direction': 'desc'},
            {'kind': 'select', 'page': 'blotter', 'focus': {'tradeIds': [booked['tradeId']], 'pivotCell': None}},
            {'kind': 'say', 'page': 'blotter', 'message': 'review'}, {'kind': 'pause', 'page': 'blotter'}]
    shown, shown_results, _ = ask([('read_page_digest', {'mode': 'overview'}), ('present_walkthrough', {'planJson': json.dumps(plan)})], report=digest_report)
    stored = owner.call(f"assistant/conversations/{shown['conversationId']}?after=0")
    events = stored['events'];event = next(e for e in events if e['kind'] == 'walkthrough')
    check('a validated walkthrough is stored immediately after its tool step with no business effect',
          shown_results[1]['status'] == 'presented' and events[events.index(event)-1]['kind'] == 'step'
          and event['payload']['ordinal'] == 2 and event['payload']['plan']['steps'][0]['direction'] == 'desc')
    for label, invalid in [('unknown presentation kind', [{'kind':'book','page':'blotter'}]),
                           ('unknown navigation handle',[{'kind':'go','target':'n99'}]),
                           ('unread page',[{'kind':'pause','page':'jobs'}]),
                           ('oversized plan',[{'kind':'pause','page':'blotter'}]*13)]:
        invalid_turn, invalid_results, _ = ask([('read_page_digest', {'mode': 'overview'}), ('present_walkthrough', {'planJson':json.dumps(invalid)})], report=digest_report)
        denied_events=owner.call(f"assistant/conversations/{invalid_turn['conversationId']}?after=0")['events']
        check('walkthrough refuses '+label+' without publishing a partial plan', 'code' in invalid_results[1] and not any(e['kind']=='walkthrough' for e in denied_events))
    retained_context = query(f"SELECT context_id FROM assistant.turns WHERE id='{digest_turn['id']}';")
    pinned_lines = digest['result']['text'].split('Status:', 1)[1]
    _, curve_digest, _ = ask([('read_page_digest', {'mode': 'overview'})], report={'page': 'curves', 'selection': {'view': 'curves', 'curve': None}})
    check('curve digest reuses the authorized curve directory', 'Curves: ' in curve_digest[0]['result']['text'])

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

    owner.call('futures/book', {**trading, **trade_booking(), 'operationId': str(uuid4()), 'bookId': offer['books'][0]['id'],
        'curve': 'future', 'contractId': contract['id'], 'definitionRevisionId': contract['definitionRevisionId'], 'lots': '4', 'executionPrice': '3.2', 'memo': 'Synthetic post-pin fact'})
    owner.call(f"assistant/turns/{digest_turn['id']}/view", {'selection': 'latest_correction'})
    try:
        retained_frame = initial_frame(owner.call('workspace/navigation'), '2020-12-18')
        _, retained_digest, _ = ask([('read_page_digest', {'mode': 'overview'})], report=digest_report,
            knowledge={'mode': 'retained', 'contextId': retained_context, 'frameId': retained_frame['id']})
        check('a retained-context digest keeps the pre-correction server values',
              retained_digest[0]['result']['text'].split('Status:', 1)[1] == pinned_lines)
        _, refusal_digest, _ = ask([('read_page_digest', {'mode': 'overview'})], report={'page': 'jobs', 'selection': {'view': 'jobs', 'job': None, 'page': 0, 'before': None}},
            knowledge={'mode': 'retained', 'contextId': retained_context, 'frameId': None})
        check('digest retains the underlying current-only job refusal', refusal_digest[0]['code'] == 'jobs_current_only')
    finally:
        owner.call('market/current', {})
        frame = initial_frame(owner.call('workspace/navigation'), '2020-12-18')
    _, current_digest, _ = ask([('read_page_digest', {'mode': 'overview'})], report=digest_report)
    check('working digest includes later business facts while the retained digest keeps the original values',
          'Positions: future=2' in current_digest[0]['result']['text'] and current_digest[0]['result']['text'].split('Status:', 1)[1] != pinned_lines)
    # Each selected report is a separate tool operation. A long turn must not
    # accumulate the initiating HTTP request's browser read-reference quota.
    risk_agent=fixture.publish(fixture.save(fixture.create('independent report reads'),model,
        tools=['read_page_operation'],roles=['risk_analyst'],toolSteps=12))
    owner.call('assistant/grants/grant',{'operationId':str(uuid4()),'agentId':risk_agent['id'],'version':1})
    risk_jobs=[]
    for _ in range(9):
        selector={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
        risk_catalog=owner.call('portfolio/risk/catalog',{'context':selector,'bookIds':[],'portfolioIds':[]})
        factor=risk_catalog['factors'][0]
        request={'context':{**selector,'knowledgeMode':'retained','contextId':risk_catalog['context']['contextId']},
            'bookIds':[],'portfolioIds':[],'detail':'books','convention':risk_catalog['convention'],
            'numericalPolicy':risk_catalog['numericalPolicy'],'pricingPolicy':risk_catalog['pricingPolicy'],
            'bumps':[{'factor':factor['factor'],'size':'0.1','convention':risk_catalog['bumpConvention']}],'stress':[]}
        job=owner.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':request})
        risk_jobs.append(owner.call('portfolio/risk/jobs/'+job['id']+'/cancel',{}))
    assert len({job['context']['contextId'] for job in risk_jobs})==9
    report_turn,report_results,_=ask([('read_page_operation',{'operation':'portfolio.risk.jobs.id_v1',
        'inputJson':json.dumps({'body':None,'parameters':{'id':job['id']}})}) for job in risk_jobs],
        state=risk_agent,report={'page':'risk_jobs','selection':{'view':'reports'}},operational=True,timeout=90)
    check('one assistant turn reads nine independent selected report inputs without inheriting browser transport state',
          [result.get('data',{}).get('id') for result in report_results]==[job['id'] for job in risk_jobs]
          and query(f"SELECT context_id IS NULL AND pins IS NULL FROM assistant.turns WHERE id='{report_turn['id']}';")=='t')
    # A proposal's delegation can end while its ordinary command waits, without any role or epoch change. The final
    # guard must then roll back both the account mutation and the approval receipt rather than commit under the
    # human's lasting role. A term set to end on the clock before the request is sent can end before the first guard,
    # and that denial proves nothing, so the term keeps its length until the command is seen held past that guard.
    # A write then stands in for the clock: it moves the term's end to its own instant. The command holds the epoch,
    # which admits no control change until it ends, so the write skips the triggers, among them the one that advances
    # the epoch. Its instant follows the command's start, so a guard that read transaction time would commit and fail here.
    # The administrative directory names its user records; the exact expected head is read from the owned fixture.
    expected = int(query(f"SELECT version FROM security.workspace_memberships WHERE principal_id='{target['id']}';"))
    expiring_command = {'operation': 'admin.update_v1', 'inputJson': json.dumps({'body': {'operationId': str(uuid4()), 'id': target['id'], 'expectedVersion': expected,
        'name': 'Synthetic expired change', 'enabled': True, 'roles': ['assistant_prompt_editor']}, 'parameters': {}})}
    expires_turn, _, _ = ask([('propose_page_action', expiring_command)])
    delegation = query(f"SELECT delegation_id FROM assistant.turns WHERE id='{expires_turn['id']}';")
    previous_expiry = query(f"SELECT granted_to::text FROM security.agent_delegations WHERE id='{delegation}';")
    try:
        with ThreadPoolExecutor(max_workers=1) as executor:
            def blocked():
                future = executor.submit(owner.call, 'assistant/actions/authorize', {'turnId': expires_turn['id'], 'step': 1}, expected=403)
                try:
                    # An approval takes seconds to prepare its command before the command's transaction begins. The
                    # wait ends within the request's own 20 seconds, leaving it time to answer.
                    waiting(query, 1, 'security.apply_administration', timeout=15)
                except AssertionError:
                    if future.done():
                        raise AssertionError(('authorization returned before the held command', future.result()))
                    raise
                query("BEGIN;SET LOCAL session_replication_role=replica;"
                      f"UPDATE security.agent_delegations SET granted_to=clock_timestamp() WHERE id='{delegation}';COMMIT;")
                return future
            future = holding(psql, environment, f"SELECT principal_id FROM security.workspace_memberships WHERE principal_id='{target['id']}' FOR UPDATE", blocked)
            future.result(timeout=10)
        check('delegation expiry during a command rolls back the effect and approval', query(f"SELECT display_name FROM security.workspace_memberships WHERE principal_id='{target['id']}';") == 'Synthetic authorized prompt editor' and
              query(f"SELECT count(*) FROM assistant.action_authorizations WHERE turn_id='{expires_turn['id']}';") == '0')
    finally:
        query(f"UPDATE security.agent_delegations SET granted_to='{previous_expiry}'::timestamptz WHERE id='{delegation}';")

    principal = query(f"SELECT principal_id FROM security.identity_bindings WHERE subject='{owner_login}';")
    query(f"INSERT INTO automation.installation_operators VALUES('{principal}','ManageAllJobs',clock_timestamp()+interval '1 hour');")
    operator_turn, _, _ = ask([('read_current_page', {}), ('present_walkthrough', {'planJson': json.dumps([{'kind':'say','page':'users_access','message':'look_here'}])})])
    query(f"UPDATE automation.installation_operators SET expires_at=clock_timestamp()-interval '1 second' WHERE principal_id='{principal}';")
    hidden_read = owner.call(f"assistant/conversations/{operator_turn['conversationId']}?after=0")
    hidden = hidden_read['turns'][0]
    check('expired installation authority hides retained generic-page answers', hidden['hidden'] == 'access_changed')

    check('walkthrough replay hides the entire plan after source access changes', all(e['payload'] == {'hidden': 'access_changed'} for e in hidden_read['events'] if e['kind'] == 'walkthrough') and any(e['kind']=='walkthrough' for e in hidden_read['events']))

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
    # Exact close input sets are referenced, including a selected historical alias.
    from close_session_fixture import record as record_close
    source = next(f for f in owner.call('workspace')['frames'] if f['id'] == frame['id'])
    close = record_close(client('approver'), source, 'Synthetic assistant close')
    owner.call('workspace/navigation')
    close_report = {'page': 'curves', 'selection': {'view': 'curves', 'curve': None}}
    close_turn, close_results, _ = ask([('read_page_digest', {'mode': 'overview'})], report=close_report, frame_id=close['id'])
    check('requested close reads retain an exact foreign key and no duplicated source matrix',
          'Curves: ' in close_results[0]['result']['text']
          and json.loads(query(f"SELECT pins::text FROM assistant.turns WHERE id='{close_turn['id']}';")) == {'frameId': close['id'], 'closeId': close['id']}
          and query(f"SELECT close_id FROM assistant.turn_close_pins WHERE turn_id='{close_turn['id']}';") == close['id'])
    curve_agent = fixture.publish(fixture.save(fixture.create('recorded close curve'), model,
        tools=['read_custom_curves', 'prepare_custom_curve'], roles=['trader']))
    owner.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': curve_agent['id'], 'version': 1})
    definition = {'code': 'synthetic_close_proposal', 'name': 'Synthetic close proposal', 'output': 'out', 'nodes': [
        {'id': 'source', 'kind': 'source', 'parameters': {'curve': 'future'}, 'inputs': {}},
        {'id': 'select', 'kind': 'select', 'parameters': {'mode': 'positions', 'first': '1', 'count': '1', 'binding': 'context'}, 'inputs': {'source': 'source'}},
        {'id': 'dates', 'kind': 'dates', 'parameters': {'mode': 'context', 'sampling': 'context_utc_time', 'corrections': 'exact_context_frame_history_latest_visible'}, 'inputs': {}},
        {'id': 'sample', 'kind': 'sample', 'parameters': {}, 'inputs': {'selection': 'select', 'dates': 'dates'}},
        {'id': 'out', 'kind': 'output', 'parameters': {}, 'inputs': {'values': 'sample'}}]}
    formula = owner.call('custom-curves/format', {'definition': definition})['formula']
    prepared, prepared_results, _ = ask([('read_custom_curves', {}), ('prepare_custom_curve', {
        'code': definition['code'], 'name': definition['name'], 'engine': 'custom_curve_recipe_v2', 'formula': formula})],
        page='custom_curves', report={'page': 'custom_curves', 'selection': {'curve': None, 'unsaved': False}}, state=curve_agent, frame_id=close['id'])
    check('a recorded close supports requested curve preparation', prepared_results[1].get('result', {}).get('status') == 'proposed_draft')
    opened = owner.call('assistant/curve-proposal', {'turnId': prepared['id'], 'step': 2})
    check('reopening a recorded-close proposal binds its exact context once and returns the original unpriced draft',
          opened['status'] == 'unpriced_draft' and opened['definition'] == definition
          and opened['origin']['contextId'] == prepared['context']['contextId'] and opened['frameId'] == close['id'])
    try:
        owner.call(f"assistant/turns/{close_turn['id']}/view", {'selection': 'latest_correction'})
        historical = next(f for f in owner.call('workspace')['frames'] if f.get('originCloseId') == close['id'])
        retained_turn, _, _ = ask([('read_page_digest', {'mode': 'overview'})], report=close_report,
            knowledge={'mode': 'retained', 'contextId': close_turn['context']['contextId'], 'frameId': historical['id']})
        check('a historical close alias stays compact and uses the exact selected context',
              historical['id'] != close['id'] and retained_turn['context']['contextId'] == close_turn['context']['contextId']
              and json.loads(query(f"SELECT pins::text FROM assistant.turns WHERE id='{retained_turn['id']}';")) == {'frameId': historical['id'], 'closeId': close['id']})
    finally:
        owner.call('market/current', {})

    from check_assistant_history import run_checks as history_checks
    history_checks(ask, check, owner, trader, agent, query, stamp)
    from check_issue_report_assistant import run_checks as issue_report_checks
    issue_report_checks(ask, check, admin, owner, trader, tracker)
    check('no model request reaches configured proxy traps', trap.connections == 0)
    print(f'PASS {passed} assistant workspace integration checks', flush=True)
