"""Issue reports through authenticated HTTP against a loopback synthetic tracker."""
import json
from pathlib import Path
from uuid import uuid4
from fake_issue_tracker import TOKEN
from preview_client import PreviewClient

TITLE = 'Synthetic totals differ after an amendment'
BODY = 'Steps: open a synthetic trade, amend it, compare the totals.\nExpected: equal totals.'


def run_checks(url, query, tracker, log):
    answers = []

    def client(profile):
        value = PreviewClient(url)
        value.bootstrap()
        value.call('profile', {'profile': profile})
        return value

    def call(who, path, body=None, expected=200):
        answers.append(who.call(path, body, expected=expected))
        return answers[-1]

    admin, viewer = client('admin'), client('viewer')

    def submit(who=viewer, op=None, expected=200, **changes):
        return call(who, 'issue-reports', {'operationId': op or str(uuid4()), 'kind': 'bug', 'title': TITLE, 'body': BODY, **changes}, expected)

    def resolve(report, attempt, resolution, who=viewer, expected=200):
        return call(who, 'issue-reports/resolve', {'id': report['id'], 'expectedAttempt': attempt, 'resolution': resolution}, expected)

    def reconcile(report, who=viewer, expected=200):
        return call(who, 'issue-reports/reconcile', {'id': report['id']}, expected)

    def refused(response, code):
        assert response['code'] == code, response

    def created():
        return len(tracker.issues)

    def later(report):
        # Stands for the two minutes after which the earlier exchange can no longer be completing.
        query(f"UPDATE assistant.issue_reports SET dispatch_admitted_at=dispatch_admitted_at-interval '3 minutes' WHERE id='{report['id']}';")

    # Without a saved policy nothing is submitted and the tracker hears nothing.
    assert call(viewer, 'issue-reports') == {'repository': tracker.repository, 'reports': {'mode': None, 'submissions': []}}
    refused(submit(expected=409), 'issue_reporting_disabled')
    assert tracker.requests == []
    assert call(admin, 'settings/issue-reports') == {'repository': tracker.repository, 'policy': {'current': None, 'history': []}}
    call(viewer, 'settings/issue-reports', expected=403)
    policy = lambda expected, mode, limit, who=admin, status=200: call(who, 'settings/issue-reports', {'operationId': str(uuid4()), 'expectedVersion': expected, 'mode': mode, 'dailyLimit': limit}, status)
    policy(0, 'review_required', 9, who=viewer, status=403)
    refused(policy(0, 'automatic', 9, status=422), 'issue_report_policy_input')
    refused(policy(0, None, 9, status=422), 'issue_report_policy_input')
    assert policy(0, 'review_required', 9) == {'version': 1, 'mode': 'review_required', 'dailyLimit': 9}
    refused(policy(0, 'disabled', None, status=409), 'issue_report_policy_version_conflict')
    # A person whose roles do not include the permission reads and submits nothing.
    bystander = client('admin')
    call(bystander, 'admin/create', {'operationId': str(uuid4()), 'login': 'synthetic_report_bystander', 'name': 'Synthetic bystander', 'kind': 'human', 'roles': ['gas_operator']})
    bystander.call('profile', {'profile': 'synthetic_report_bystander'})
    call(bystander, 'issue-reports', expected=403)
    submit(who=bystander, expected=403)
    assert tracker.requests == []

    # A submission creates exactly one issue with the reviewed text, and every identical retry answers with it.
    operation = str(uuid4())
    first = submit(op=operation)
    assert first['state'] == 'submitted' and first['attempt'] == 1 and first['issueNumber'] == 1 and first['issueUrl'] == f'https://github.com/{tracker.repository}/issues/1'
    issue = tracker.issues[0]
    assert issue['title'] == TITLE and issue['body'].startswith('Kind: bug\n\n'+BODY+'\n\n<!-- report ') and issue['body'].endswith(' -->')
    assert submit(op=operation) == first and created() == 1
    refused(submit(op=operation, title='A different synthetic title', expected=409), 'issue_report_conflict')
    for invalid in (dict(kind='praise'), dict(kind=None), dict(title='x'*121), dict(title=None), dict(title='   '), dict(body=None), dict(body='nul \x00 character')):
        refused(submit(expected=422, **invalid), 'issue_report_input')
    call(viewer, 'issue-reports', {'operationId': str(uuid4()), 'kind': 'bug', 'title': TITLE, 'body': BODY, 'repository': 'elsewhere/tracker'}, expected=400)
    assert created() == 1, 'A refused request reaches no tracker'
    print('PASS issue report submission: off until configured, permission required, one issue with the reviewed text, replay, conflict and bounded input', flush=True)

    # An answer that proves nothing was created settles as failed.
    tracker.behaviour = 'refuse'
    failed = submit()
    assert failed['state'] == 'failed' and failed['errorCode'] == 'tracker_refused' and failed['issueNumber'] is None and created() == 1

    # An unavailable tracker leaves the outcome uncertain. A retry sends nothing; a lookup that finds nothing says so.
    tracker.behaviour = 'unavailable'
    operation = str(uuid4())
    unknown = submit(op=operation)
    assert unknown['state'] == 'uncertain' and unknown['errorCode'] == 'outcome_unknown'
    sent = len(tracker.requests)
    assert submit(op=operation) == unknown and len(tracker.requests) == sent
    assert reconcile(unknown) == {'report': unknown, 'lookup': 'absent'}
    refused(reconcile(unknown, expected=409), 'issue_report_wait')
    reconcile(unknown, who=admin, expected=404)
    resolve(unknown, 1, 'resend', who=admin, expected=404)
    refused(resolve(unknown, 3, 'resend', expected=409), 'issue_report_fence')
    refused(resolve(unknown, 1, 'guess', expected=422), 'issue_report_input')
    # The person's explicit resend waits out the earlier exchange, looks again, then creates under the next attempt.
    tracker.behaviour = 'create'
    refused(resolve(unknown, 1, 'resend', expected=409), 'issue_report_wait')
    assert created() == 1
    later(unknown)
    resent = resolve(unknown, 1, 'resend')
    assert resent['state'] == 'submitted' and resent['attempt'] == 2 and resent['issueNumber'] == 2 and created() == 2
    assert resolve(unknown, 1, 'resend') == resent and created() == 2, 'The same decision again answers with what it already did'

    # An issue created while its answer was lost is found by its marker, not created again.
    tracker.behaviour = 'lose'
    lost = submit()
    assert lost['state'] == 'uncertain' and created() == 3
    tracker.behaviour = 'create'
    found = reconcile(lost)
    assert found['lookup'] == 'found' and found['report']['state'] == 'submitted' and found['report']['issueNumber'] == 3 and created() == 3
    assert reconcile(lost) == {'report': found['report'], 'lookup': 'not_needed'}
    tracker.behaviour = 'lose'
    again = submit()
    tracker.behaviour = 'create'
    later(again)
    resent = resolve(again, 1, 'resend')
    assert resent['state'] == 'submitted' and resent['issueNumber'] == 4 and created() == 4, 'A resend after a lost answer must find the existing issue'
    # A resend that cannot look in the tracker proves nothing: the report stays unconfirmed, and its issue is still found.
    tracker.behaviour = 'lose'
    hidden = submit()
    assert hidden['state'] == 'uncertain' and created() == 5
    tracker.behaviour, tracker.token_status = 'create', 401
    later(hidden)
    unproven = resolve(hidden, 1, 'resend')
    assert unproven['state'] == 'uncertain' and unproven['attempt'] == 2 and created() == 5
    tracker.token_status = 201
    found = reconcile(hidden)
    assert found['lookup'] == 'found' and found['report']['state'] == 'submitted' and found['report']['issueNumber'] == 5 and created() == 5
    # Giving up is explicit and final.
    tracker.behaviour = 'unavailable'
    stuck = submit()
    tracker.behaviour = 'create'
    abandoned = resolve(stuck, 1, 'abandon')
    assert abandoned['state'] == 'abandoned' and resolve(stuck, 1, 'abandon') == abandoned and created() == 5
    refused(resolve(stuck, 1, 'resend', expected=409), 'issue_report_transition')
    mine = call(viewer, 'issue-reports')['reports']
    assert mine['mode'] == 'review_required' and [r['state'] for r in mine['submissions']] == ['abandoned', 'submitted', 'submitted', 'submitted', 'submitted', 'failed', 'submitted']
    assert call(admin, 'issue-reports')['reports']['submissions'] == [], "One person's reports are not another's"
    print('PASS uncertain outcomes: no blind second issue, lookup by marker, a resend that waits and never fails unproven, explicit abandon, owner only', flush=True)

    # The daily bound and a disabled policy stop new requests before the tracker hears of them.
    assert policy(1, 'review_required', 7) == {'version': 2, 'mode': 'review_required', 'dailyLimit': 7}
    sent = len(tracker.requests)
    refused(submit(expected=429), 'issue_report_limit')
    policy(2, 'disabled', None)
    refused(submit(expected=409), 'issue_reporting_disabled')
    assert len(tracker.requests) == sent and created() == 5
    assert call(viewer, 'issue-reports')['reports']['mode'] == 'disabled'
    # The installation token reaches neither an answer nor the application log.
    assert TOKEN not in json.dumps(answers) and TOKEN not in Path(log).read_text()
    print('PASS issue report bounds: daily limit, disabled policy and no credential in any answer or the application log', flush=True)
