"""The assistant drafts an issue report and the person authorizes it; part of the assistant workspace check."""
import json
from uuid import uuid4

TITLE = 'Synthetic totals differ after an amendment'
BODY = 'Steps: open a synthetic trade, amend it, compare the totals.'
READ, SUBMIT = 'issue-reports_v1', 'issue-reports_command_v1'


def run_checks(ask, check, admin, owner, trader, tracker):
    def proposal(title=TITLE):
        return {'operation': SUBMIT, 'inputJson': json.dumps({'body': {'operationId': str(uuid4()), 'kind': 'bug', 'title': title, 'body': BODY}, 'parameters': {}})}

    def policy(expected, mode, limit):
        return admin.call('settings/issue-reports', {'operationId': str(uuid4()), 'expectedVersion': expected, 'mode': mode, 'dailyLimit': limit})

    policy(0, 'review_required', 5)
    _, results, _ = ask([('discover_page_operations', {'page': 'issue_reports', 'operation': None})], operational=True)
    found = {operation['id']: operation for operation in results[0]['operations']}
    check('issue reports are discoverable from any page as one read and one command that needs the person\'s approval',
          list(found) == [READ, SUBMIT] and found[READ]['effect'] == 'read_or_preview' and found[SUBMIT]['effect'] == 'requires_user_approval')
    _, results, _ = ask([('discover_page_operations', {'page': 'issue_reports', 'operation': SUBMIT})], operational=True)
    contract = results[0]['operations'][0]['input']['body']
    check('the command\'s contract is the ordinary request: a kind, a title and a body, and no repository',
          contract['contract'] == 'SubmitIssueReportRequest' and sorted(contract['properties']) == ['body', 'kind', 'operationId', 'title'] and contract['additionalProperties'] is False)
    _, results, _ = ask([('read_page_operation', proposal())], operational=True)
    check('the assistant cannot submit an issue report itself', results[0]['code'] == 'approval_required' and tracker.requests == [])
    for closed in ('issue-reports.resolve_v1', 'issue-reports.reconcile_v1', 'settings.issue-reports_v1', 'settings.issue-reports_command_v1'):
        _, results, _ = ask([('propose_page_action', {'operation': closed, 'inputJson': '{"body":null,"parameters":{}}'})], operational=True)
        check(f'{closed} stays outside the assistant catalog', results[0]['code'] == 'tool_arguments_invalid')

    # The draft is retained with the turn; nothing reaches the tracker until the person authorizes that exact text.
    proposed, results, _ = ask([('propose_page_action', proposal())], operational=True)
    check('a drafted issue report sends nothing', results[0]['status'] == 'awaiting_user_approval' and tracker.requests == [])
    reference = {'turnId': proposed['id'], 'step': 1}
    review = owner.call('assistant/actions/review', reference)
    check('Review shows the exact report the tracker would receive',
          review['operation'] == SUBMIT and {k: review['body'][k] for k in ('kind', 'title', 'body')} == {'kind': 'bug', 'title': TITLE, 'body': BODY}
          and owner.call('assistant/actions/review', reference) == review and tracker.requests == [])
    trader.call('assistant/actions/review', reference, expected=404)
    trader.call('assistant/actions/authorize', reference, expected=404)
    outcome = owner.call('assistant/actions/authorize', reference)
    issue = tracker.issues[0]
    check('the person\'s authorization creates one issue with the reviewed text and answers with its number and link',
          outcome['state'] == 'submitted' and outcome['issueNumber'] == 1 and outcome['issueUrl'] == issue['html_url']
          and issue['title'] == TITLE and issue['body'].startswith('Kind: bug\n\n'+BODY+'\n\n<!-- report '))
    check('authorizing the same draft again answers with the first outcome and creates nothing',
          owner.call('assistant/actions/authorize', reference) == outcome and len(tracker.issues) == 1)
    _, results, _ = ask([('read_page_operation', {'operation': READ, 'inputJson': '{"body":null,"parameters":{}}'})], operational=True)
    reports = results[0]['data']['reports']
    check('the assistant reads the person\'s own reports through the ordinary operation',
          reports['mode'] == 'review_required' and [r['issueNumber'] for r in reports['submissions']] == [1])

    # A draft never outlives the policy: with reporting disabled, authorizing it sends nothing.
    second, _, _ = ask([('propose_page_action', proposal('A second synthetic report'))], operational=True)
    policy(1, 'disabled', None)
    sent = len(tracker.requests)
    refused = owner.call('assistant/actions/authorize', {'turnId': second['id'], 'step': 1}, expected=409)
    check('a draft authorized after reporting was disabled is refused and sends nothing',
          refused['code'] == 'issue_reporting_disabled' and len(tracker.requests) == sent and len(tracker.issues) == 1)
