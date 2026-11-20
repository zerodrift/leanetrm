"""Opt-in personal interaction search against an owned synthetic assistant fixture."""
import json
from uuid import uuid4


def run_checks(ask, check, owner, trader, agent, query, stamp):
    def search_preference(enabled):
        saved = owner.call('settings/preferences')
        current = saved['current']
        preferences = current['preferences'] if current else dict(
            version=1, timeZone='UTC', browseBy='commodity', market=None, marketType='all', tab='prices',
            sidebarCollapsed=False, libraryCollapsed=False, assistantHidden=False, assistantMinimized=False,
            collapsedMenus=[], collapsedCurveGroups=[])
        command = {'operationId': str(uuid4()), 'ownerId': saved['ownerId'],
                   'expectedVersion': current['version'] if current else 0,
                   'preferences': {**preferences, 'assistantSearchHistory': enabled}}
        result = owner.call('settings/preferences', command)
        check('a saved history preference is owner-bound and retries return its first outcome',
              result == owner.call('settings/preferences', command)
              and result['preferences']['assistantSearchHistory'] is enabled)

    def replay(turn):
        operation = query(f"SELECT operation_id FROM assistant.turns WHERE id='{turn['id']}';")
        original = owner.call(f"assistant/conversations/{turn['conversationId']}?after=0")['turns'][0]
        return {'operationId': operation, 'conversationId': None, 'agentId': agent['id'], 'version': 1,
                'question': original['question'], 'pageReport': original['pageReport'],
                'knowledge': {'mode': 'current', 'contextId': None, 'frameId': original['context']['frameId']}}

    phrase = 'synthetic_history_' + stamp
    source, _, calls = ask([], question=phrase + ' walkthrough', answer='Synthetic saved answer: ' + phrase)
    check('a person without a saved opt-in is not offered history search',
          source['searchHistory'] is False and 'search_chat_history' not in {t['name'] for t in calls[0]['tools']})
    search_preference(True)
    grant = trader.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': agent['id'], 'version': 1})
    try:
        foreign, _, _ = ask([], session=trader, page='settings_ai', question=phrase + ' other person')
    finally:
        trader.call('assistant/grants/revoke', {'operationId': str(uuid4()), 'grantId': grant['id']})
    request = [('search_chat_history', {'query': phrase, 'before': None})]
    searched, results, calls = ask(request)
    matches = results[0]['matches']
    check('opted-in search finds the owner’s earlier questions and answers across conversations',
          [m['turnId'] for m in matches] == [source['id']] and phrase in matches[0]['answerExcerpt'])
    check('history search never discloses another person’s interaction', foreign['id'] not in json.dumps(results))
    check('history search uses the admitting owner’s saved preference',
          'search_chat_history' in {t['name'] for t in calls[0]['tools']} and searched['searchHistory'] is True and foreign['searchHistory'] is False)
    check('the history search records its versioned read operation', query(
        f"SELECT operation FROM assistant.tool_steps WHERE turn_id='{searched['id']}' AND tool='search_chat_history';") == 'assistant.history_search_v1')

    search_preference(False)
    retried = owner.call('assistant/turns/ask', replay(searched))['turn']
    check('an opted-in ask retry preserves its original choice after the preference is disabled',
          retried['id'] == searched['id'] and retried['searchHistory'] is True)
    legacy = {**replay(searched), 'searchHistory': True}
    check('a pre-upgrade ask payload can still reconcile its original opted-in outcome',
          owner.call('assistant/turns/ask', legacy)['turn']['id'] == searched['id'])
    check('a legacy retry cannot replace the pinned history choice',
          owner.call('assistant/turns/ask', {**legacy, 'searchHistory': False}, expected=409)['code'] == 'turn_conflict')
    unadmitted = str(uuid4())
    check('a legacy history choice cannot admit a new question',
          owner.call('assistant/turns/ask', {**legacy, 'operationId': unadmitted}, expected=422)['code'] == 'turn_input'
          and query(f"SELECT count(*) FROM assistant.turns WHERE operation_id='{unadmitted}';") == '0')
    foreign_operation = query(f"SELECT operation_id FROM assistant.turns WHERE id='{foreign['id']}';")
    check('legacy reconciliation cannot use another person’s request identity',
          owner.call('assistant/turns/ask', {**legacy, 'operationId': foreign_operation}, expected=422)['code'] == 'turn_input')
    disabled, _, calls = ask([])
    check('a saved opt-out prevents history search on the next question',
          disabled['searchHistory'] is False and 'search_chat_history' not in {t['name'] for t in calls[0]['tools']})
    search_preference(True)
    retried = owner.call('assistant/turns/ask', replay(disabled))['turn']
    check('an opted-out ask retry cannot gain search authority from a later preference',
          retried['id'] == disabled['id'] and retried['searchHistory'] is False)
    check('a pre-upgrade opted-out retry also retains its original outcome',
          owner.call('assistant/turns/ask', {**replay(disabled), 'searchHistory': False})['turn']['searchHistory'] is False)
    check('retries retain exactly one immutable search option per turn', query(
        f"SELECT count(*) FROM assistant.turn_history_options WHERE turn_id IN ('{searched['id']}','{disabled['id']}');") == '2')

    _, results, _ = ask([('search_chat_history', {'query': '%_' + stamp, 'before': None})])
    check('search text uses literal matching, including wildcard characters', results[0]['matches'] == [])
    _, results, _ = ask([('search_chat_history', {'query': phrase, 'before': foreign['id']})])
    check('a foreign search cursor is refused without disclosing its contents', results[0]['code'] == 'tool_arguments_invalid')

    page_phrase = 'synthetic_paged_' + stamp
    expected = []
    for number in range(11):
        item, _, _ = ask([], question=f'{page_phrase} question {number}', answer='x' * 700 + page_phrase + 'z' * 700)
        expected.insert(0, item['id'])
    _, results, _ = ask([('search_chat_history', {'query': page_phrase, 'before': None})])
    first = results[0]
    _, results, _ = ask([('search_chat_history', {'query': page_phrase, 'before': first['nextBefore']})])
    second = results[0]
    check('bounded keyset pages cover matching interactions once in a stable order',
          len(first['matches']) == 10 and len(second['matches']) == 1 and second['nextBefore'] is None
          and [m['turnId'] for m in first['matches'] + second['matches']] == expected)
    check('long replies return explicitly labelled excerpts centered on the match',
          all(m['answerTruncated'] and len(m['answerExcerpt']) == 500 and page_phrase in m['answerExcerpt'] for m in first['matches']))

    # A history hit carries its original authority into the searching turn. Removing that right must hide both.
    private, _, _ = ask([('read_current_page', {})], question=phrase + ' restricted administration', operational=True)
    retained, results, _ = ask(request)
    check('authorized history includes original context and clearly bounded excerpts',
          any(m['turnId'] == private['id'] and m['context']['mode'] == 'operational' for m in results[0]['matches'])
          and all(len(m['questionExcerpt']) <= 500 and len(m['answerExcerpt']) <= 500 for m in results[0]['matches']))
    principal = query(f"SELECT owner_id FROM assistant.turns WHERE id='{source['id']}';")
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{principal}' AND role_id=(SELECT id FROM security.roles WHERE code='access_administrator');")
    hidden = owner.call(f"assistant/conversations/{retained['conversationId']}?after=0")['turns'][0]
    check('revoking a retrieved source permission hides the answer that used history', hidden['hidden'] == 'access_changed')
    _, results, _ = ask(request)
    check('new searches omit inaccessible questions and answers', private['id'] not in json.dumps(results) and retained['id'] not in json.dumps(results))
