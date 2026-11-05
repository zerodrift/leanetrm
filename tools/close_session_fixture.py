"""Publish D319 close sessions and grants for owned synthetic checks."""
from uuid import uuid4


def draft(code, calendar, cutoff='17:30', curves=('future',), effective=('2020-12-01', '2021-12-01')):
    return {'code': code, 'name': f'Synthetic {code}', 'calendarCode': calendar, 'cutoff': cutoff, 'curves': list(curves),
            'effectiveFrom': effective[0], 'effectiveTo': effective[1]}


def publish(editor, approver, body):
    """Save a draft and publish it; returns the published version."""
    saved = editor.call('close-sessions/save', {'requestId': str(uuid4()), 'draft': body})
    return approver.call('close-sessions/publish', {'requestId': str(uuid4()), **{k: saved[k] for k in ('code', 'version', 'generation')}})


def principal(query, manifest, login):
    return query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{manifest['tenantId']}','{manifest['workspaceId']}','{login}');")


def grant(admin, query, manifest, login, session):
    return admin.call('close-sessions/grants', {'requestId': str(uuid4()), 'principalId': principal(query, manifest, login), 'session': session,
                                                'grantedFrom': '2020-01-01T00:00:00Z', 'grantedTo': '9999-01-01T00:00:00Z'})
