"""Opt-in personal interaction search against an owned synthetic assistant fixture."""
import json
from uuid import uuid4


def run_checks(ask, check, owner, trader, agent, query, stamp):
    phrase = 'synthetic_history_' + stamp
    source, _, _ = ask([], question=phrase + ' walkthrough', answer='Synthetic saved answer: ' + phrase)
    grant = trader.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': agent['id'], 'version': 1})
    try:
        foreign, _, _ = ask([], session=trader, page='settings_ai', question=phrase + ' other person')
    finally:
        trader.call('assistant/grants/revoke', {'operationId': str(uuid4()), 'grantId': grant['id']})
    request = [('search_chat_history', {'query': phrase, 'before': None})]
    searched, results, calls = ask(request, search_history=True)
    matches = results[0]['matches']
    check('opted-in search finds the owner’s earlier questions and answers across conversations',
          [m['turnId'] for m in matches] == [source['id']] and phrase in matches[0]['answerExcerpt'])
    check('history search never discloses another person’s interaction', foreign['id'] not in json.dumps(results))
    check('history search is offered only on the question that opted in',
          'search_chat_history' in {t['name'] for t in calls[0]['tools']} and searched['searchHistory'] is True and source['searchHistory'] is False)
    check('the history search records its versioned read operation', query(
        f"SELECT operation FROM assistant.tool_steps WHERE turn_id='{searched['id']}' AND tool='search_chat_history';") == 'assistant.history_search_v1')

    _, _, calls = ask([])
    check('an unchecked question does not offer the history tool', 'search_chat_history' not in {t['name'] for t in calls[0]['tools']})
    operation = query(f"SELECT operation_id FROM assistant.turns WHERE id='{searched['id']}';")
    original = owner.call(f"assistant/conversations/{searched['conversationId']}?after=0")['turns'][0]
    replay = {'operationId': operation, 'conversationId': None, 'agentId': agent['id'], 'version': 1,
              'question': original['question'], 'pageReport': original['pageReport'],
              'knowledge': {'mode': 'current', 'contextId': None, 'frameId': original['context']['frameId']}, 'searchHistory': True}
    check('an identical ask retry preserves consent and returns the original turn', owner.call('assistant/turns/ask', replay)['turn']['id'] == searched['id'])
    check('a retry cannot change history consent', owner.call('assistant/turns/ask', {**replay, 'searchHistory': False}, expected=409)['code'] == 'turn_conflict')

    _, results, _ = ask([('search_chat_history', {'query': '%_' + stamp, 'before': None})], search_history=True)
    check('search text uses literal matching, including wildcard characters', results[0]['matches'] == [])
    _, results, _ = ask([('search_chat_history', {'query': phrase, 'before': foreign['id']})], search_history=True)
    check('a foreign search cursor is refused without disclosing its contents', results[0]['code'] == 'tool_arguments_invalid')

    page_phrase = 'synthetic_paged_' + stamp
    expected = []
    for number in range(11):
        item, _, _ = ask([], question=f'{page_phrase} question {number}', answer='x' * 700 + page_phrase + 'z' * 700)
        expected.insert(0, item['id'])
    _, results, _ = ask([('search_chat_history', {'query': page_phrase, 'before': None})], search_history=True)
    first = results[0]
    _, results, _ = ask([('search_chat_history', {'query': page_phrase, 'before': first['nextBefore']})], search_history=True)
    second = results[0]
    check('bounded keyset pages cover matching interactions once in a stable order',
          len(first['matches']) == 10 and len(second['matches']) == 1 and second['nextBefore'] is None
          and [m['turnId'] for m in first['matches'] + second['matches']] == expected)
    check('long replies return explicitly labelled excerpts centered on the match',
          all(m['answerTruncated'] and len(m['answerExcerpt']) == 500 and page_phrase in m['answerExcerpt'] for m in first['matches']))

    # A history hit carries its original authority into the searching turn. Removing that right must hide both.
    private, _, _ = ask([('read_current_page', {})], question=phrase + ' restricted administration', operational=True)
    retained, results, _ = ask(request, search_history=True)
    check('authorized history includes original context and clearly bounded excerpts',
          any(m['turnId'] == private['id'] and m['context']['mode'] == 'operational' for m in results[0]['matches'])
          and all(len(m['questionExcerpt']) <= 500 and len(m['answerExcerpt']) <= 500 for m in results[0]['matches']))
    principal = query(f"SELECT owner_id FROM assistant.turns WHERE id='{source['id']}';")
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{principal}' AND role_id=(SELECT id FROM security.roles WHERE code='access_administrator');")
    hidden = owner.call(f"assistant/conversations/{retained['conversationId']}?after=0")['turns'][0]
    check('revoking a retrieved source permission hides the answer that used history', hidden['hidden'] == 'access_changed')
    _, results, _ = ask(request, search_history=True)
    check('new searches omit inaccessible questions and answers', private['id'] not in json.dumps(results) and retained['id'] not in json.dumps(results))
