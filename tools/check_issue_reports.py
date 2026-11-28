"""Issue report policy, retained reports and uncertain delivery in an owned synthetic database."""
import json
import time
from uuid import uuid4
from check_reference_editing import main

REPOSITORY = 'synthetic-owner/synthetic-tracker'


def link(number, repository=REPOSITORY):
    return f'https://github.com/{repository}/issues/{number}'


def run_checks(query, manifest):
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']

    def call(profile, sql, kind='human_interactive'):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','{kind}','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"\\set VERBOSITY verbose\nSET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql};COMMIT;")

    def read(profile, sql):
        return json.loads(call(profile, sql))

    def optional(profile, sql):
        value = call(profile, sql)
        return json.loads(value) if value else None

    def refused(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)

    def text(value):
        return "'"+value.replace("'", "''")+"'"

    def principal(login, permissions, seconds=3600, kind='human', binding='human_interactive'):
        identity, role = str(uuid4()), str(uuid4())
        grants = ''.join(f"INSERT INTO security.role_permissions VALUES('{role}','{p}');" for p in permissions)
        query(f"INSERT INTO security.roles(id,code,name) VALUES('{role}','{login}_role','Synthetic {login} role');{grants}"
              f"INSERT INTO security.principals VALUES('{identity}','{kind}',true);INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{login}','{identity}','{binding}');"
              f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{identity}','{login}','Synthetic {login}',true,1);"
              f"INSERT INTO security.role_grants VALUES('{uuid4()}','{identity}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 hour',clock_timestamp()+interval '{seconds} seconds',false);")

    # The six human role templates may submit; only the access administrator configures; agents hold neither.
    holders = lambda permission: set(query(f"SELECT string_agg(r.code,',' ORDER BY r.code) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id WHERE p.permission='{permission}';").split(','))
    assert holders('SubmitIssueReports') == {'viewer', 'trader', 'risk_analyst', 'market_data_editor', 'market_data_approver', 'access_administrator'}
    assert holders('ManageIssueReporting') == {'access_administrator'}

    def policy(profile='admin'):
        return read(profile, "SELECT assistant.issue_report_policy_directory()::text")

    def save(mode, limit, expected, op=None, profile='admin'):
        return read(profile, f"SELECT assistant.save_issue_report_policy('{op or uuid4()}',{expected},{text(mode)},{'NULL' if limit is None else limit})::text")

    def request(profile='viewer', op=None, kind='bug', title='Synthetic totals differ after an amendment', body='Steps: open a synthetic trade, amend it, compare the totals.', repository=REPOSITORY, binding='human_interactive'):
        return json.loads(call(profile, f"SELECT assistant.request_issue_report('{op or uuid4()}',{text(kind)},{text(title)},{text(body)},{text(repository)})::text", binding))

    def admit(report, profile='viewer'):
        return optional(profile, f"SELECT assistant.admit_issue_report_dispatch('{report}')::text")

    def resend(report, attempt, profile='viewer'):
        return optional(profile, f"SELECT assistant.admit_issue_report_resend('{report}',{attempt})::text")

    def lookup(report, profile='viewer'):
        return optional(profile, f"SELECT assistant.admit_issue_report_lookup('{report}')::text")

    def complete(report, attempt, outcome, number=None, url=None, error=None, profile='viewer'):
        nullable = lambda v: 'NULL' if v is None else (str(v) if isinstance(v, int) else text(v))
        return read(profile, f"SELECT assistant.complete_issue_report('{report}',{attempt},{text(outcome)},{nullable(number)},{nullable(url)},{nullable(error)})::text")

    def status(report, profile='viewer'):
        return read(profile, f"SELECT assistant.issue_report_status('{report}')::text")

    def abandon(report, attempt, profile='viewer'):
        return read(profile, f"SELECT assistant.abandon_issue_report('{report}',{attempt})::text")

    def age(report, minutes):
        query(f"UPDATE assistant.issue_reports SET dispatch_admitted_at=dispatch_admitted_at-interval '{minutes} minutes',"
              f"lookup_admitted_at=lookup_admitted_at-interval '{minutes} minutes' WHERE id='{report}';")

    rows = lambda: int(query(f"SELECT count(*) FROM assistant.issue_reports WHERE tenant_id='{tenant}' AND workspace_id='{workspace}';"))

    # An absent policy enables nothing, and nothing is retained for a refused request.
    assert policy() == {'current': None, 'history': []}
    refused(lambda: request(), 'issue_reporting_disabled')
    assert read('viewer', "SELECT assistant.my_issue_reports()::text") == {'mode': None, 'submissions': []}
    assert rows() == 0

    # The policy is a complete explicit revision: a mode, and a daily bound exactly when submission is on.
    refused(lambda: save('review_required', None, 0), 'issue_report_policy_input')
    refused(lambda: save('disabled', 5, 0), 'issue_report_policy_input')
    refused(lambda: save('review_required', 0, 0), 'issue_report_policy_input')
    refused(lambda: save('review_required', 51, 0), 'issue_report_policy_input')
    refused(lambda: save('automatic', 5, 0), 'issue_report_policy_input')
    refused(lambda: save('review_required', 5, 0, profile='viewer'), '42501')
    refused(lambda: policy('viewer'), '42501')
    operation = str(uuid4())
    first = save('review_required', 4, 0, op=operation)
    assert first == {'version': 1, 'mode': 'review_required', 'dailyLimit': 4}
    assert save('review_required', 4, 0, op=operation) == first
    refused(lambda: save('disabled', None, 0, op=operation), 'issue_report_policy_conflict')
    refused(lambda: save('disabled', None, 0), 'issue_report_policy_version_conflict')
    assert save('review_required', 4, 1) == first, 'Identical facts must not create another revision'
    assert policy()['current'] == first and len(policy()['history']) == 1
    print('PASS report policy: absent means off, complete explicit revisions, administrator only, replay, conflict and no-op facts', flush=True)

    # A request is retained once per identity; its first outcome answers every identical retry.
    for invalid in (dict(kind='praise'), dict(title=''), dict(title='   '), dict(title='x'*121), dict(title='line\nbreak'), dict(title='reversed ‮ text'),
                    dict(body=''), dict(body=' \n\t'), dict(body='x'*4001), dict(body='bell \x07'),
                    dict(repository='not a repository'), dict(repository='owner/name/extra'), dict(repository='owner/..'), dict(repository='owner/name\n')):
        refused(lambda invalid=invalid: request(**invalid), 'issue_report_input')
    principal('synthetic_report_outsider', ['ReadMarketData'])
    refused(lambda: request(profile='synthetic_report_outsider'), '42501')
    # Only a person acting for themselves submits: a service identity holding the permission is refused.
    principal('synthetic_report_service', ['SubmitIssueReports'], kind='service', binding='service_credential')
    refused(lambda: request(profile='synthetic_report_service', binding='service_credential'), '42501')
    assert rows() == 0
    operation = str(uuid4())
    report = request(op=operation)
    assert report['state'] == 'pending' and report['attempt'] == 0 and report['issueNumber'] is None and report['repository'] == REPOSITORY
    assert request(op=operation) == report and rows() == 1
    refused(lambda: request(op=operation, title='A different synthetic title'), 'issue_report_conflict')
    refused(lambda: request(op=operation, repository='synthetic-owner/another-tracker'), 'issue_report_conflict')
    # Another person cannot read, dispatch, look up or settle it, and learns nothing from its identity.
    for work in (lambda: status(report['id'], 'admin'), lambda: admit(report['id'], 'admin'), lambda: complete(report['id'], 1, 'submitted', 7, link(7), profile='admin'),
                 lambda: abandon(report['id'], 0, 'admin'), lambda: resend(report['id'], 1, 'admin'), lambda: lookup(report['id'], 'admin')):
        refused(work, 'issue_report_not_found')
    assert request(profile='admin', op=operation)['id'] != report['id'], 'A request identity is scoped to its submitting person'

    # Dispatch is admitted once, in its own commit, immediately before the external call.
    refused(lambda: complete(report['id'], 1, 'submitted', 7, link(7)), 'issue_report_fence')
    claim = admit(report['id'])
    assert claim['attempt'] == 1 and claim['repository'] == REPOSITORY and claim['kind'] == 'bug' and claim['title'] == 'Synthetic totals differ after an amendment' and claim['requestedAt']
    assert len(claim['marker']) == 36 and claim['marker'] != report['id'], 'The correlation marker is opaque and separate from every other identity'
    assert admit(report['id']) is None, 'A second dispatch must not be admitted while the first may be in flight'
    assert status(report['id'])['state'] == 'dispatching' and request(op=operation)['state'] == 'dispatching'
    refused(lambda: complete(report['id'], 2, 'submitted', 7, link(7)), 'issue_report_fence')
    refused(lambda: complete(report['id'], 1, 'submitted', None, None), 'issue_report_input')
    refused(lambda: complete(report['id'], 1, 'failed', None, None, None), 'issue_report_input')
    refused(lambda: complete(report['id'], 1, 'failed', 7, link(7), 'tracker_refused'), 'issue_report_input')
    refused(lambda: complete(report['id'], 1, 'finished', 7, link(7)), 'issue_report_input')
    # A confirmed issue is this repository's own page for that number, nothing else.
    for elsewhere in (link(8), link(7, 'synthetic-owner/another-tracker'), 'https://elsewhere.invalid/issues/7', link(7)+'?next=/issues/7'):
        refused(lambda elsewhere=elsewhere: complete(report['id'], 1, 'submitted', 7, elsewhere), 'issue_report_input')
    done = complete(report['id'], 1, 'submitted', 7, link(7))
    assert done['state'] == 'submitted' and done['issueNumber'] == 7 and done['issueUrl'] == link(7) and done['settledAt']
    assert complete(report['id'], 1, 'submitted', 7, link(7)) == done, 'Recording the same fact again returns it'
    refused(lambda: complete(report['id'], 1, 'submitted', 8, link(8)), 'issue_report_fence')
    refused(lambda: complete(report['id'], 1, 'failed', None, None, 'tracker_refused'), 'issue_report_fence')
    assert request(op=operation) == done and admit(report['id']) is None
    refused(lambda: resend(report['id'], 1), 'issue_report_transition')
    print('PASS report requests: bounded input, a person only, one retained request per identity, single dispatch admission and a settled first outcome', flush=True)

    # A request the tracker did not create is a settled outcome with a recognized code; it is never sent again.
    refusal = request()
    refused(lambda: complete(refusal['id'], admit(refusal['id'])['attempt'], 'failed', None, None, 'Not A Code'), 'issue_report_input')
    assert complete(refusal['id'], 1, 'failed', None, None, 'tracker_refused')['errorCode'] == 'tracker_refused'
    assert admit(refusal['id']) is None
    refused(lambda: resend(refusal['id'], 1), 'issue_report_transition')

    # A lost response is uncertain. Nothing is sent again until the person decides, and not while the first
    # exchange could still be completing.
    lost = request()
    assert admit(lost['id'])['attempt'] == 1
    uncertain = complete(lost['id'], 1, 'uncertain', None, None, 'outcome_unknown')
    assert uncertain['state'] == 'uncertain' and uncertain['settledAt'] is None
    assert admit(lost['id']) is None
    refused(lambda: resend(lost['id'], 1), 'issue_report_wait')
    refused(lambda: resend(lost['id'], 3), 'issue_report_fence')
    refused(lambda: resend(lost['id'], 0), 'issue_report_input')
    # A lookup is admitted for an unconfirmed report only, by a current submitter, at most once a minute.
    sought = lookup(lost['id'])
    assert sought['attempt'] == 1 and len(sought['marker']) == 36 and sought['repository'] == REPOSITORY and sought['requestedAt']
    refused(lambda: lookup(lost['id']), 'issue_report_wait')
    assert lookup(report['id']) is None, 'Only an unconfirmed report is looked up'
    # Reconciliation that finds the issue settles the same attempt.
    found = complete(lost['id'], 1, 'submitted', 9, link(9))
    assert found['state'] == 'submitted' and found['issueNumber'] == 9
    # A dispatch whose process stopped is uncertain once its bounded window has passed, not pending.
    stopped = request()
    earlier = admit(stopped['id'])
    assert earlier['attempt'] == 1 and status(stopped['id'])['state'] == 'dispatching'
    age(stopped['id'], 3)
    assert status(stopped['id'])['state'] == 'uncertain' and admit(stopped['id']) is None
    # The daily bound counts every retained request of the person.
    refused(lambda: request(), 'issue_report_limit')
    assert request(profile='admin')['state'] == 'pending', "One person's bound does not consume another's"
    # An explicit resend admits the next attempt at once; the same decision again admits nothing more.
    second = resend(stopped['id'], 1)
    assert second['attempt'] == 2 and second['marker'] == earlier['marker'], 'Every attempt carries the one marker reconciliation looks for'
    assert resend(stopped['id'], 1) is None and status(stopped['id'])['state'] == 'dispatching' and status(stopped['id'])['attempt'] == 2
    refused(lambda: complete(stopped['id'], 1, 'submitted', 10, link(10)), 'issue_report_fence')
    assert complete(stopped['id'], 2, 'uncertain', None, None, 'outcome_unknown')['state'] == 'uncertain'
    refused(lambda: abandon(stopped['id'], 1), 'issue_report_fence')
    abandoned = abandon(stopped['id'], 2)
    assert abandoned['state'] == 'abandoned' and abandoned['settledAt'] and abandon(stopped['id'], 2) == abandoned
    assert admit(stopped['id']) is None and lookup(stopped['id']) is None
    refused(lambda: resend(stopped['id'], 2), 'issue_report_transition')
    mine = read('viewer', "SELECT assistant.my_issue_reports()::text")
    assert mine['mode'] == 'review_required' and [t['state'] for t in mine['submissions']] == ['abandoned', 'submitted', 'failed', 'submitted']
    assert all('body' not in t and 'marker' not in t for t in mine['submissions'])
    assert query("SELECT count(*) FROM assistant.issue_reports WHERE state='pending' AND attempt>0;") == '0', 'A report that was ever sent is never shown as not sent'
    print('PASS uncertain delivery: no second dispatch while one may have succeeded, a quiet period before a resend, bounded lookups, stale dispatch becomes uncertain, explicit resend and abandon', flush=True)

    # Policy and authority are checked again at dispatch; a completed change stops what was not yet sent.
    principal('synthetic_report_expiring', ['SubmitIssueReports'], seconds=2)
    expiring = request(profile='synthetic_report_expiring')
    time.sleep(2.5)
    refused(lambda: admit(expiring['id'], 'synthetic_report_expiring'), '42501')
    assert query(f"SELECT state,attempt FROM assistant.issue_reports WHERE id='{expiring['id']}';") == 'pending|0'
    principal('synthetic_report_steady', ['SubmitIssueReports'])
    waiting = request(profile='synthetic_report_steady')
    unconfirmed = request(profile='synthetic_report_steady')
    admit(unconfirmed['id'], 'synthetic_report_steady')
    complete(unconfirmed['id'], 1, 'uncertain', None, None, 'outcome_unknown', profile='synthetic_report_steady')
    age(unconfirmed['id'], 3)
    off = save('disabled', None, 1)
    assert off == {'version': 2, 'mode': 'disabled', 'dailyLimit': None}
    refused(lambda: admit(waiting['id'], 'synthetic_report_steady'), 'issue_reporting_disabled')
    refused(lambda: request(profile='synthetic_report_steady'), 'issue_reporting_disabled')
    assert status(waiting['id'], 'synthetic_report_steady')['state'] == 'pending'
    assert abandon(waiting['id'], 0, 'synthetic_report_steady')['state'] == 'abandoned'
    # A refused resend changes nothing: the report stays unconfirmed and can still be looked up.
    refused(lambda: resend(unconfirmed['id'], 1, 'synthetic_report_steady'), 'issue_reporting_disabled')
    assert status(unconfirmed['id'], 'synthetic_report_steady')['state'] == 'uncertain'
    assert lookup(unconfirmed['id'], 'synthetic_report_steady')['attempt'] == 1
    # A dispatch already admitted is a fact: its outcome is still recorded after the policy changes.
    held = read('admin', "SELECT assistant.my_issue_reports()::text")
    assert held['mode'] == 'disabled' and [t['state'] for t in held['submissions']] == ['pending', 'pending']
    save('review_required', 2, 2)
    # A request given up before it was ever sent no longer counts toward the person's bound.
    refused(lambda: request(profile='admin'), 'issue_report_limit')
    abandon(held['submissions'][1]['id'], 0, 'admin')
    later = request(profile='admin')
    late = admit(held['submissions'][0]['id'], 'admin')
    save('disabled', None, 3)
    assert complete(held['submissions'][0]['id'], late['attempt'], 'submitted', 11, link(11), profile='admin')['state'] == 'submitted'
    # Each report retains the policy revision that admitted its request.
    assert query(f"SELECT policy_version FROM assistant.issue_reports WHERE id='{later['id']}';") == '3'
    assert query(f"SELECT policy_version FROM assistant.issue_reports WHERE id='{report['id']}';") == '1'
    assert query("SELECT count(*) FROM assistant.issue_report_policy_versions;") == '4'
    refused(lambda: query("UPDATE assistant.issue_report_policy_versions SET mode='review_required' WHERE version=2;"), 'immutable')
    # The runtime login reaches the tables and the helpers only through the operations.
    for direct in ("SELECT count(*) FROM assistant.issue_reports", "DELETE FROM assistant.issue_report_policy_versions", "SELECT assistant.issue_report_actor(NULL)",
                   f"SELECT assistant.issue_report_policy('{tenant}','{workspace}')", "SELECT assistant.is_issue_report_policy('disabled',NULL)"):
        refused(lambda direct=direct: query(f"SET SESSION AUTHORIZATION preview_runtime;{direct};"), 'permission denied')
    refused(lambda: query("\\set VERBOSITY verbose\nSET SESSION AUTHORIZATION preview_runtime;SELECT assistant.my_issue_reports();"), '42501')
    print('PASS dispatch authority: expired permission and a disabled policy stop unsent work and refuse a resend unchanged, admitted dispatches keep their outcome, revisions are immutable, no direct access', flush=True)


if __name__ == '__main__':
    main(run_checks)
