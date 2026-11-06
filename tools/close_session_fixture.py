"""Publish D334 close sessions and grants for owned synthetic checks."""
import hashlib
from uuid import uuid4
from preview_client import PreviewClient


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


def sql_session(query, manifest, code, calendar, cutoff, curves, logins, revision=None, effective=('2020-12-01', '2021-12-01')):
    """Publish a session over an exact definition revision directly, with grants, for checks that build their own bundle."""
    catalog, revision = manifest['catalogId'], revision or manifest['revisionId']
    owner = principal(query, manifest, 'approver')
    curve_rows = ','.join(f"('{catalog}','{code}',1,'{revision}','{c}')" for c in curves)
    grants = ''.join(f"INSERT INTO security.close_session_grants VALUES(gen_random_uuid(),'{principal(query, manifest, login)}','{manifest['tenantId']}','{manifest['workspaceId']}',"
                     f"'{catalog}','{code}','2020-01-01','9999-01-01',false,'{owner}',gen_random_uuid(),NULL,NULL);" for login in logins)
    query(f"""INSERT INTO market_data.close_sessions VALUES('{catalog}','{code}','Synthetic {code}');
    INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
     calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
    SELECT '{catalog}','{code}',1,'published',2,id,reference_catalog_id,reference_revision_id,'{calendar}','{cutoff}','{effective[0]}','{effective[1]}',
     '{owner}',gen_random_uuid(),'{{}}','{owner}',gen_random_uuid() FROM market_data.revisions WHERE(catalog_id,id)=('{catalog}','{revision}');
    INSERT INTO market_data.close_session_curves VALUES {curve_rows};{grants}""")


_published, _granted, _clients = set(), set(), {}


def record(who, frame, name, login='approver', request_id=None):
    """Record a close of a frame for its business day in a synthetic UTC session at the frame's time of day and curves,
    publishing that session and granting the recorder once per preview. Built-in closes keep their own sessions."""
    curves = sorted(frame['curveContracts'])
    cutoff = frame['at'][11:16]
    code = f"test_close_{cutoff.replace(':', '')}_{hashlib.sha256(','.join(curves).encode()).hexdigest()[:8]}"
    def client(profile):
        # One helper session per preview, switched between profiles: the preview caps the sessions a run may open.
        if who.url not in _clients:
            value = PreviewClient(who.url); value.bootstrap(); _clients[who.url] = value
        value = _clients[who.url]; value.call('profile', {'profile': profile}); return value
    if (who.url, code) not in _published:
        saved = client('editor').call('close-sessions/save', {'requestId': str(uuid4()), 'draft': draft(code, 'test_utc_every_day', cutoff, curves, ('2020-01-01', '2100-01-01'))})
        client('approver').call('close-sessions/publish', {'requestId': str(uuid4()), **{k: saved[k] for k in ('code', 'version', 'generation')}})
        _published.add((who.url, code))
    if (who.url, code, login) not in _granted:
        grant_member(client('admin'), login, code)
        _granted.add((who.url, code, login))
    return who.call('market/closes/register', {'requestId': request_id or str(uuid4()), 'snapshotId': frame['id'], 'name': name,
                                               'session': code, 'businessDate': frame['at'][:10]})


def grant_member(admin, login, session):
    """Grant a workspace member, found by login in the access directory, a session's recording authority."""
    member = next(m for m in admin.call('admin')['members'] if m['login'] == login)
    return admin.call('close-sessions/grants', {'requestId': str(uuid4()), 'principalId': member['id'], 'session': session,
                                                'grantedFrom': '2020-01-01T00:00:00Z', 'grantedTo': '9999-01-01T00:00:00Z'})
