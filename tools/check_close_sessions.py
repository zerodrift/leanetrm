"""D334 close sessions are authored, published and granted through the API, in an owned synthetic preview."""
from uuid import uuid4
from preview_client import PreviewClient
from close_session_fixture import draft, grant, publish


def client(url, profile):
    value = PreviewClient(url); value.bootstrap(); value.call('profile', {'profile': profile}); return value


def run_checks(url, query, manifest):
    def issue():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    viewer, editor, approver, admin = (client(url, p) for p in ('viewer', 'editor', 'approver', 'admin'))
    london = draft('test_london', 'test_london_days')
    body = {'requestId': str(uuid4()), 'draft': london}
    saved = editor.call('close-sessions/save', body)
    assert saved == editor.call('close-sessions/save', body), 'A retried save created another version'
    assert (saved['code'], saved['version']) == ('test_london', 1)
    assert editor.call('close-sessions/save', {**body, 'draft': {**london, 'cutoff': '18:00'}}, expected=409)['code'] == 'close_conflict'
    viewer.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': london}, expected=403)
    editor.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: saved[k] for k in ('code', 'version', 'generation')}}, expected=403)
    published = approver.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: saved[k] for k in ('code', 'version', 'generation')}})
    assert published['state'] == 'published'
    publish(editor, approver, draft('test_new_york', 'test_new_york_days'))
    sessions = {s['code']: s for s in viewer.call('close-sessions/catalog')['sessions'] if s['state'] == 'published'}
    assert sessions['test_london'] == {'code': 'test_london', 'name': 'Synthetic test_london', 'version': 1, 'state': 'published', 'generation': 2,
                                       'calendarCode': 'test_london_days', 'clockCode': 'test_london_clock', 'cutoff': '17:30',
                                       'curves': ['future'], 'effectiveFrom': '2020-12-01', 'effectiveTo': '2021-12-01'}, sessions['test_london']
    assert sessions['test_new_york']['clockCode'] == 'test_new_york_clock'
    print('PASS close sessions are saved and published explicitly, idempotently and only by their authorities', flush=True)

    for bad in [draft('test_bad_curve', 'test_london_days', curves=('not_a_curve',)),
                draft('test_bad_calendar', 'not_a_calendar'),
                draft('test_bad_cutoff', 'test_london_days', cutoff='17:30:15'),
                draft('test_no_curves', 'test_london_days', curves=())]:
        assert editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': bad}, expected=422)['code'] == 'close_session', bad
    # A later version takes over from its own start: it supersedes the published one from then on. One starting no later than
    # a published version's start would leave a day with two versions, and is refused.
    later = editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': draft('test_london', 'test_london_days', cutoff='18:00', effective=('2021-06-01', '2100-01-01'))})
    assert later['version'] == 2
    approver.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: later[k] for k in ('code', 'version', 'generation')}})
    governing = lambda day: query(f"""SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issue()}');
      SELECT coalesce(security.close_session_version_on('{manifest['catalogId']}','test_london','{day}')::text,'none');COMMIT;""").splitlines()[-1]
    assert (governing('2021-05-31'), governing('2021-06-01'), governing('2020-11-30')) == ('1', '2', 'none')
    early = editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': draft('test_london', 'test_london_days', effective=('2020-12-01', '2021-03-01'))})
    assert approver.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: early[k] for k in ('code', 'version', 'generation')}},
                         expected=422)['code'] == 'close_session'
    editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': {**london, 'unexpected': 1}}, expected=400)
    print('PASS a later session version takes over from its start; versions starting no later, unknown curves or calendars and malformed cutoffs are refused', flush=True)

    first = grant(admin, query, manifest, 'approver', 'test_london')
    assert first['session'] == 'test_london' and not first['revoked']
    editor.call('close-sessions/grants', {'requestId': str(uuid4()), 'principalId': first['principalId'], 'session': 'test_london',
                                          'grantedFrom': '2020-01-01T00:00:00Z', 'grantedTo': '9999-01-01T00:00:00Z'}, expected=403)
    listed = admin.call('close-sessions/grants/list')['grants']
    assert any(g['id'] == first['id'] for g in listed)
    revoke = {'requestId': str(uuid4()), 'grantId': first['id']}
    revoked = admin.call('close-sessions/grants/revoke', revoke)
    assert revoked['revoked'] and revoked == admin.call('close-sessions/grants/revoke', revoke)
    print('PASS per-session recording grants are administered explicitly and idempotently', flush=True)
