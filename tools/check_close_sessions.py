"""D319 close sessions are authored, published and granted through the API, in an owned synthetic preview."""
from uuid import uuid4
from preview_client import PreviewClient


def client(url, profile):
    value = PreviewClient(url); value.bootstrap(); value.call('profile', {'profile': profile}); return value


def publish(editor, approver, draft):
    """Save a draft and publish it; returns the published version."""
    saved = editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': draft})
    return approver.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: saved[k] for k in ('code', 'version', 'generation')}})


def draft(code, calendar, cutoff='17:30', curves=('future',), effective=('2020-12-01', '2021-12-01')):
    return {'code': code, 'name': f'Synthetic {code}', 'calendarCode': calendar, 'cutoff': cutoff, 'curves': list(curves),
            'effectiveFrom': effective[0], 'effectiveTo': effective[1]}


def grant(admin, query, manifest, login, session):
    principal = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{manifest['tenantId']}','{manifest['workspaceId']}','{login}');")
    return admin.call('close-sessions/grants', {'requestId': str(uuid4()), 'principalId': principal, 'session': session,
                                                'grantedFrom': '2020-01-01T00:00:00Z', 'grantedTo': '9999-01-01T00:00:00Z'})


def run_checks(url, query, manifest):
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
    # An overlapping version is a valid draft; publishing it would give one business day two session versions.
    overlap = editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': draft('test_london', 'test_london_days', effective=('2021-06-01', '2022-06-01'))})
    assert overlap['version'] == 2
    assert approver.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: overlap[k] for k in ('code', 'version', 'generation')}},
                         expected=422)['code'] == 'close_session'
    editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': {**london, 'unexpected': 1}}, expected=400)
    print('PASS a session refuses overlapping versions, unknown curves or calendars and malformed cutoffs', flush=True)

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
