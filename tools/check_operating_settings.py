"""Versioned operating policies and personal presentation in an owned synthetic database."""
import json
from uuid import uuid4
from check_reference_editing import main


def run_checks(query, manifest):
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']

    def call(profile, sql, permission=None):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        admission = f"security.use_operating_policy_context('{token}','{permission}')" if permission else f"security.use_context('{token}')"
        return query(f"\\set VERBOSITY verbose\nSET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT {admission};{sql};COMMIT;")

    def refused(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)

    # Provisioning states the carried-forward operating baseline explicitly.
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    directory = json.loads(call('admin', "SELECT security.operating_policy_directory()::text"))
    policies = {p['kind']: p for p in directory['policies']}
    assert set(policies) == {'assistant', 'access'}
    assert policies['assistant']['current']['values']['workspaceTurns'] == 4
    assert policies['access']['current']['values']['sessionMinutes'] == 30
    original = policies['assistant']['current']
    operation = str(uuid4())
    changed = {**original['values'], 'workspaceTurns': 2}

    def save(op=operation, expected=original['version'], values=changed, profile='admin', kind='assistant'):
        payload = json.dumps(values).replace("'", "''")
        return json.loads(call(profile, f"SELECT security.save_operating_policy('{kind}','{op}',{expected},'{payload}'::jsonb)::text", 'ManageAgents' if kind == 'assistant' else 'ManageAccess'))

    saved = save()
    assert saved['version'] == original['version']+1 and saved['values'] == changed
    assert save() == saved
    refused(lambda: save(values={**changed, 'workspaceTurns': 3}), 'operating_policy_conflict')
    refused(lambda: save(op=str(uuid4())), 'operating_policy_conflict')
    assert save(op=str(uuid4()), expected=saved['version']) == saved, 'Identical facts must not create another revision'
    refused(lambda: save(op=str(uuid4()), expected=saved['version'], values={**changed, 'workspaceTurns': 5}), 'operating_policy_input')
    refused(lambda: save(op=str(uuid4()), expected=saved['version'], values={k:v for k,v in changed.items() if k!='workspaceTurns'}), 'operating_policy_input')
    refused(lambda: save(op=str(uuid4()), expected=saved['version'], profile='viewer'), '42501')
    # A successful request always replays its first outcome after later changes.
    newer = save(op=str(uuid4()), expected=saved['version'], values={**changed, 'workspaceTurns': 1})
    assert newer['version'] == saved['version']+1 and save() == saved
    assert query(f"SELECT count(*) FROM security.operating_policy_versions WHERE tenant_id='{tenant}' AND workspace_id='{workspace}' AND kind='assistant';") == '3'
    print('PASS operating settings: explicit baseline, complete bounded values, current authority, optimistic writes, no-op facts and retained request replay', flush=True)

    preferences = dict(version=1, timeZone='America/Chicago', browseBy='commodity', market=None, marketType='all', tab='prices',
                       sidebarCollapsed=False, libraryCollapsed=False, assistantHidden=False, assistantMinimized=False,
                       collapsedMenus=[], collapsedCurveGroups=[])
    owner = json.loads(call('admin', "SELECT presentation.user_preferences('api')::text"))
    assert owner['current'] is None
    preference_operation = str(uuid4())

    def prefer(profile='admin', op=preference_operation, expected=0, values=preferences):
        payload = json.dumps(values).replace("'", "''")
        return json.loads(call(profile, f"SELECT presentation.save_user_preferences('api','{op}',{expected},'{payload}'::jsonb,'{owner['ownerId']}')::text"))

    refused(lambda: prefer(profile='viewer'), 'user_preferences_owner_changed')
    first = prefer()
    assert first['version'] == 1 and first['preferences'] == preferences
    assert prefer() == first
    assert json.loads(call('viewer', "SELECT presentation.user_preferences('api')::text"))['current'] is None, 'Another human must not see the administrator preferences'
    assert json.loads(call('admin', "SELECT presentation.user_preferences('exchange')::text"))['current'] is None
    changed_preferences = {**preferences, 'timeZone': 'UTC'}
    second = prefer(op=str(uuid4()), expected=1, values=changed_preferences)
    assert second['version'] == 2 and prefer() == first
    assert prefer(op=str(uuid4()), expected=2, values=changed_preferences) == second
    refused(lambda: prefer(op=str(uuid4()), expected=1), 'user_preferences_conflict')
    refused(lambda: prefer(op=str(uuid4()), expected=2, values={**preferences, 'knownAt': '2040-01-01T00:00:00Z'}), 'user_preferences_input')
    refused(lambda: prefer(op=str(uuid4()), expected=2, values={**preferences, 'timeZone': 'Synthetic/Unknown'}), 'user_preferences_input')
    refused(lambda: prefer(op=str(uuid4()), expected=2, values={**preferences, 'collapsedMenus': ['synthetic-unknown']}), 'user_preferences_input')
    print('PASS user preferences: active-human ownership, namespace isolation, exact replay, no-op revisions, conflicts and financial-field refusal', flush=True)

    # Access-only administration and personal presentation need neither market
    # authority nor identity-management authority.
    access_only,role=str(uuid4()),str(uuid4())
    query(f"INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_policy_access','Synthetic access policy role');INSERT INTO security.role_permissions VALUES('{role}','ManageAccess');"
          f"INSERT INTO security.principals VALUES('{access_only}','human',true);INSERT INTO security.identity_bindings VALUES('https://preview.invalid','synthetic_access_only','{access_only}','human_interactive');"
          f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{access_only}','synthetic_access_only','Synthetic access-only human',true,1);"
          f"INSERT INTO security.role_grants VALUES('{uuid4()}','{access_only}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 hour',clock_timestamp()+interval '1 hour',false);")
    only=json.loads(call('synthetic_access_only','SELECT security.operating_policy_directory()::text'))
    assert [p['kind'] for p in only['policies']]==['access']
    refused(lambda:save(op=str(uuid4()),expected=saved['version'],profile='synthetic_access_only'),'42501')
    own=json.loads(call('synthetic_access_only',"SELECT presentation.user_preferences('api')::text"))
    personal=json.loads(call('synthetic_access_only',f"SELECT presentation.save_user_preferences('api','{uuid4()}',0,'{json.dumps(preferences)}'::jsonb,'{own['ownerId']}')::text"))
    assert personal['version']==1 and personal['preferences']==preferences
    print('PASS ManageAccess-only policy discovery and personal preferences require no market or identity-management authority',flush=True)

    access = policies['access']['current']
    access_values = {**access['values'], 'sessionMinutes': 5, 'failedAttempts': 2, 'lockoutMinutes': 3, 'passwordMinimum': 24, 'invitationHours': 2}
    configured_access = save(op=str(uuid4()), expected=access['version'], values=access_values, kind='access')
    account = str(uuid4())
    query(f"INSERT INTO security.principals VALUES('{account}','human',true);INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{account}','{account}','human_interactive');INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{account}','policy_human','Synthetic policy human',true,1);INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id,login,password_hash,credential_version) VALUES('{account}','https://preview.invalid','{tenant}','{workspace}','policy_human','synthetic_policy_hash',1);")
    def authenticate(sql):
        return query('SET SESSION AUTHORIZATION preview_authenticator;'+sql)
    session = uuid4().hex + uuid4().hex
    login = f"SELECT security.finish_hosted_login('{account}',1,'synthetic_policy_hash',true,'{session}','synthetic_policy_hash');"
    assert authenticate(login) == account
    assert query(f"SELECT expires_at-created_at=interval '5 minutes' FROM security.hosted_sessions WHERE token_digest='{session}';") == 't', 'The database session must use the configured lifetime exactly'
    expires = query(f"SELECT expires_at FROM security.hosted_sessions WHERE token_digest='{session}';")
    assert authenticate(login) == account and query(f"SELECT expires_at FROM security.hosted_sessions WHERE token_digest='{session}';") == expires
    invitation = uuid4().hex + uuid4().hex
    issued = json.loads(query(f"SELECT security.issue_hosted_invitation('{invitation}','{account}','reset')::text;"))
    assert json.loads(query(f"SELECT security.issue_hosted_invitation('{invitation}','{account}','reset')::text;")) == issued
    assert query(f"SELECT expires_at-created_at=interval '2 hours' FROM security.hosted_invitations i JOIN security.hosted_invitation_policy_pins p USING(token_digest) WHERE token_digest='{invitation}';") == 't'
    next_access = save(op=str(uuid4()), expected=configured_access['version'], values={**access_values, 'sessionMinutes': 3, 'passwordMinimum': 32}, kind='access')
    assert query(f"SELECT expires_at FROM security.hosted_sessions WHERE token_digest='{session}';") == expires
    assert authenticate(f"SELECT security.claim_hosted_invitation('{invitation}','policy_human','Synthetic policy human','new_hash',24,'{configured_access['revisionId']}') IS NULL;") == 't'
    assert authenticate(f"SELECT security.claim_hosted_invitation('{invitation}','policy_human','Synthetic policy human','new_hash',32,'{next_access['revisionId']}');") == account
    failed_token = uuid4().hex + uuid4().hex
    failed = f"SELECT security.finish_hosted_login('{account}',2,'new_hash',false,'{failed_token}','new_hash') IS NULL;"
    assert authenticate(failed) == 't' and authenticate(failed) == 't'
    assert query(f"SELECT failed_attempts FROM security.hosted_accounts WHERE principal_id='{account}';") == '1', 'A failed request retry must not count another attempt'
    second_failed = uuid4().hex + uuid4().hex
    assert authenticate(f"SELECT security.finish_hosted_login('{account}',2,'new_hash',false,'{second_failed}','new_hash') IS NULL;") == 't'
    assert query(f"SELECT a.failed_attempts=2 AND a.locked_until-r.recorded_at=interval '3 minutes' AND r.revision_id='{next_access['revisionId']}' FROM security.hosted_accounts a CROSS JOIN security.hosted_login_requests r WHERE a.principal_id='{account}' AND r.token_digest='{second_failed}';") == 't'
    print('PASS access policy: exact new-session lifetime, successful-login replay, invitation replay, fixed old expiry and enrollment policy recheck', flush=True)


if __name__ == '__main__':
    from check_settings_upgrade import before_migration
    main(run_checks,before_migration)
