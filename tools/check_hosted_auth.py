"""Local-account contracts exercised only inside the caller's disposable database."""
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor
import time


def run_checks(query, manifest):
    account = str(uuid4())
    issuer = 'https://identity.synthetic.invalid/local'
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    invite, session = '1' * 64, '2' * 64
    query(f"""INSERT INTO security.principals VALUES('{account}','human',true);
      INSERT INTO security.identity_bindings VALUES('{issuer}','{account}','{account}','human_interactive');
      INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{account}','pending_{uuid4().hex[:12]}','Pending invitation',true,1);
      INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id) VALUES('{account}','{issuer}','{tenant}','{workspace}');
      INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) VALUES('{invite}','{account}','claim',clock_timestamp()+interval '1 hour');""")
    def auth(sql):
        return query('SET SESSION AUTHORIZATION preview_authenticator;' + sql)
    assert auth(f"SELECT security.claim_hosted_invitation('{invite}','synthetic_login','Synthetic human','private_hash_v1');") == account
    assert auth(f"SELECT count(*) FROM security.hosted_login_lookup('synthetic_login');") == '1'
    assert auth(f"SELECT security.claim_hosted_invitation('{invite}','attacker','Different human','replaced_hash') IS NULL;") == 't'
    assert query(f"SELECT password_hash='private_hash_v1' FROM security.hosted_accounts WHERE principal_id='{account}';") == 't'
    assert auth(f"SELECT security.finish_hosted_login('{account}',1,'wrong_hash',true,'{session}') IS NULL;") == 't'
    assert auth(f"SELECT security.finish_hosted_login('{account}',1,'private_hash_v1',true,'{session}');") == account
    assert auth(f"SELECT subject='{account}' AND login='synthetic_login' FROM security.hosted_session_info('{session}');") == 't'
    capability=auth(f"SELECT security.issue_hosted_context('{session}','preview_runtime');")
    assert query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{capability}');SELECT security.require_hosted_session();SELECT true;COMMIT;")=='t'
    assert query("SELECT NOT has_table_privilege('preview_runtime','security.hosted_accounts','SELECT') AND NOT has_function_privilege('preview_runtime','security.claim_hosted_invitation(text,text,text,text)','EXECUTE');") == 't'
    auth(f"SELECT security.revoke_hosted_session('{session}');")
    assert auth(f"SELECT count(*) FROM security.hosted_session_info('{session}');") == '0'
    other_session = '3' * 64
    assert auth(f"SELECT security.finish_hosted_login('{account}',1,'private_hash_v1',true,'{other_session}');") == account
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{account}';UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{account}';")
    assert auth(f"SELECT count(*) FROM security.hosted_session_info('{other_session}');") == '0'
    for _ in range(5):
        assert auth(f"SELECT security.finish_hosted_login('{account}',1,'private_hash_v1',false,'{'4' * 64}') IS NULL;") == 't'
    assert auth(f"SELECT security.finish_hosted_login('{account}',1,'private_hash_v1',true,'{'5' * 64}') IS NULL;") == 't'
    assert query(f"SELECT failed_attempts=5 AND locked_until>clock_timestamp() FROM security.hosted_accounts WHERE principal_id='{account}';") == 't'
    expired = '6' * 64
    query(f"INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) VALUES('{expired}','{account}','reset',clock_timestamp()-interval '1 second');")
    assert auth(f"SELECT security.claim_hosted_invitation('{expired}','synthetic_login','Synthetic human','new_hash') IS NULL;") == 't'
    reset = '7' * 64
    query(f"INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) VALUES('{reset}','{account}','reset',clock_timestamp()+interval '1 hour');")
    assert auth(f"SELECT security.claim_hosted_invitation('{reset}','synthetic_login','Synthetic human','new_hash');") == account
    assert auth(f"SELECT security.finish_hosted_login('{account}',1,'private_hash_v1',true,'{'8' * 64}') IS NULL;") == 't'
    assert auth(f"SELECT security.finish_hosted_login('{account}',2,'new_hash',true,'{'8' * 64}');") == account
    assert query(f"SELECT count(*)>0 FROM security.hosted_auth_audit WHERE principal_id='{account}';") == 't'
    # These use separate psql connections. Database lock witnesses establish
    # admission, rather than relying on a particular thread's sleep timing.
    concurrent_account=str(uuid4());concurrent_token='9'*64
    query(f"INSERT INTO security.principals VALUES('{concurrent_account}','human',true);INSERT INTO security.identity_bindings VALUES('{issuer}','{concurrent_account}','{concurrent_account}','human_interactive');INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{concurrent_account}','concurrent_pending','Synthetic invitation',true,1);INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id) VALUES('{concurrent_account}','{issuer}','{tenant}','{workspace}');INSERT INTO security.hosted_invitations(token_digest,principal_id,purpose,expires_at) VALUES('{concurrent_token}','{concurrent_account}','claim',clock_timestamp()+interval '1 hour');")
    with ThreadPoolExecutor(max_workers=2) as pool:
        claims=[pool.submit(auth,f"SELECT security.claim_hosted_invitation('{concurrent_token}','concurrent_{n}','Synthetic claimant','hash_{n}');") for n in range(2)]
        results=[future.result(timeout=10) for future in claims]
    assert results.count(concurrent_account)==1 and results.count('')==1
    winner=results.index(concurrent_account)
    assert query(f"SELECT login='concurrent_{winner}' AND password_hash='hash_{winner}' FROM security.hosted_accounts WHERE principal_id='{concurrent_account}';")=='t'
    fenced_session='a'*64
    assert auth(f"SELECT security.finish_hosted_login('{account}',2,'new_hash',true,'{fenced_session}');")==account
    stale=auth(f"SELECT security.issue_hosted_context('{fenced_session}','preview_runtime');")
    auth(f"SELECT security.revoke_hosted_session('{fenced_session}');")
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{stale}');DO $$ BEGIN PERFORM security.require_hosted_session();RAISE EXCEPTION 'Revoked session admitted';EXCEPTION WHEN insufficient_privilege THEN NULL;END $$;ROLLBACK;")
    live_session='b'*64
    assert auth(f"SELECT security.finish_hosted_login('{account}',2,'new_hash',true,'{live_session}');")==account
    admitted=auth(f"SELECT security.issue_hosted_context('{live_session}','preview_runtime');")
    barrier=uuid4().int%2**30
    def wait_for(sql):
        deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            if query(sql)=='t':return
            time.sleep(.01)
        raise AssertionError('Expected PostgreSQL lock witness did not appear')
    with ThreadPoolExecutor(max_workers=2) as pool:
        reader=pool.submit(query,f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{admitted}');SELECT security.require_hosted_session();SELECT pg_advisory_xact_lock({barrier});SELECT pg_sleep(1);SELECT security.require_hosted_session();SELECT true;COMMIT;")
        wait_for(f"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid={barrier} AND granted);")
        logout=pool.submit(auth,f"SELECT security.revoke_hosted_session('{live_session}');")
        wait_for("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%revoke_hosted_session%');")
        assert reader.result(timeout=10).strip()=='t';logout.result(timeout=10)
    assert auth(f"SELECT count(*) FROM security.hosted_session_info('{live_session}');")=='0'
    print('PASS concurrent hosted authentication: one invitation winner, pre-admission logout rejects stale capability, admitted transaction fences logout, subsequent session denied',flush=True)
    print('PASS hosted SQL authentication: single-use claim, immutable subject, password-generation race, runtime isolation, logout, disable/re-enable, persistent lockout and reset', flush=True)
