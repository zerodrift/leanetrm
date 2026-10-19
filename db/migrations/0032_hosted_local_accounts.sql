-- Private invitation-based local authentication. No users or credentials are seeded.
BEGIN;
CREATE TABLE security.hosted_accounts (
 principal_id uuid PRIMARY KEY REFERENCES security.principals(id),
 issuer text NOT NULL CHECK(issuer LIKE 'https://%'),
 tenant_id uuid NOT NULL, workspace_id uuid NOT NULL,
 login text UNIQUE CHECK(login ~ '^[a-z][a-z0-9_]{0,47}$'),
 password_hash text, credential_version bigint NOT NULL DEFAULT 0 CHECK(credential_version>=0),
 failed_attempts integer NOT NULL DEFAULT 0 CHECK(failed_attempts>=0), locked_until timestamptz,
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 CHECK((login IS NULL)=(password_hash IS NULL))
);
CREATE TABLE security.hosted_invitations (
 token_digest text PRIMARY KEY CHECK(token_digest ~ '^[0-9a-f]{64}$'),
 principal_id uuid NOT NULL REFERENCES security.hosted_accounts(principal_id),
 purpose text NOT NULL CHECK(purpose IN('claim','reset')),
 expires_at ref_data.instant NOT NULL, consumed_at timestamptz, revoked_at timestamptz
);
CREATE TABLE security.hosted_sessions (
 token_digest text PRIMARY KEY CHECK(token_digest ~ '^[0-9a-f]{64}$'),
 principal_id uuid NOT NULL REFERENCES security.hosted_accounts(principal_id),
 credential_version bigint NOT NULL,
 created_at ref_data.instant NOT NULL DEFAULT clock_timestamp(),
 expires_at ref_data.instant NOT NULL, revoked_at timestamptz,
 CHECK(created_at<expires_at)
);
CREATE INDEX hosted_sessions_account ON security.hosted_sessions(principal_id) WHERE revoked_at IS NULL;
ALTER TABLE security.request_contexts ADD COLUMN hosted_session_digest text REFERENCES security.hosted_sessions(token_digest);
CREATE TABLE security.hosted_auth_audit (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 at ref_data.instant NOT NULL DEFAULT clock_timestamp(),
 principal_id uuid REFERENCES security.principals(id),
 event text NOT NULL CHECK(event IN('claim','reset','login','logout','disabled')),
 accepted boolean NOT NULL
);
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['hosted_accounts','hosted_invitations','hosted_sessions','hosted_auth_audit'] LOOP
  EXECUTE format('ALTER TABLE security.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE security.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON security.%I TO openetrm_security_owner',t);
 END LOOP;
END $$;
GRANT USAGE ON SEQUENCE security.hosted_auth_audit_id_seq TO openetrm_security_owner;

CREATE FUNCTION security.hosted_account_active(account uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM security.hosted_accounts a
  JOIN security.principals p ON p.id=a.principal_id AND p.enabled AND p.kind='human'
  JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.principal_id) AND m.enabled
  JOIN security.identity_bindings b ON b.issuer=a.issuer AND b.subject=a.principal_id::text AND b.principal_id=a.principal_id AND b.login_kind='human_interactive'
  WHERE a.principal_id=account);
$$;

CREATE FUNCTION security.claim_hosted_invitation(digest text, desired_login text, display_name text, hash text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE invitation security.hosted_invitations; account security.hosted_accounts;
BEGIN
 -- Name/membership changes share the existing administrative authorization fence.
 PERFORM 1 FROM security.epoch WHERE id FOR UPDATE;
 SELECT * INTO invitation FROM security.hosted_invitations WHERE token_digest=digest FOR UPDATE;
 IF NOT FOUND OR invitation.consumed_at IS NOT NULL OR invitation.revoked_at IS NOT NULL OR invitation.expires_at<=clock_timestamp() THEN
  INSERT INTO security.hosted_auth_audit(event,accepted) VALUES('claim',false); RETURN NULL; END IF;
 SELECT * INTO account FROM security.hosted_accounts WHERE principal_id=invitation.principal_id FOR UPDATE;
 IF NOT security.hosted_account_active(account.principal_id) OR desired_login IS NULL OR desired_login !~ '^[a-z][a-z0-9_]{0,47}$'
  OR display_name IS NULL OR hash IS NULL OR length(btrim(display_name)) NOT BETWEEN 1 AND 80 OR length(hash) NOT BETWEEN 1 AND 2048
  OR invitation.purpose='claim' AND account.password_hash IS NOT NULL
  OR invitation.purpose='reset' AND account.login IS DISTINCT FROM desired_login THEN
  INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(account.principal_id,'claim',false); RETURN NULL; END IF;
 UPDATE security.hosted_accounts SET login=desired_login,password_hash=hash,credential_version=credential_version+1,failed_attempts=0,locked_until=NULL WHERE principal_id=account.principal_id;
 UPDATE security.workspace_memberships SET login_handle=desired_login,display_name=btrim(claim_hosted_invitation.display_name),version=version+1 WHERE(tenant_id,workspace_id,principal_id)=(account.tenant_id,account.workspace_id,account.principal_id);
 UPDATE security.hosted_invitations SET consumed_at=clock_timestamp() WHERE token_digest=digest;
 UPDATE security.hosted_invitations SET revoked_at=clock_timestamp() WHERE principal_id=account.principal_id AND token_digest<>digest AND consumed_at IS NULL AND revoked_at IS NULL;
 UPDATE security.hosted_sessions SET revoked_at=clock_timestamp() WHERE principal_id=account.principal_id AND revoked_at IS NULL;
 INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(account.principal_id,invitation.purpose,true);
 RETURN account.principal_id;
EXCEPTION WHEN unique_violation THEN
 INSERT INTO security.hosted_auth_audit(event,accepted) VALUES('claim',false); RETURN NULL;
END $$;

CREATE FUNCTION security.hosted_login_lookup(desired_login text)
RETURNS TABLE(principal_id uuid,password_hash text,credential_version bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT a.principal_id,a.password_hash,a.credential_version FROM security.hosted_accounts a
 WHERE a.login=desired_login AND a.password_hash IS NOT NULL AND security.hosted_account_active(a.principal_id)
 AND(a.locked_until IS NULL OR a.locked_until<=clock_timestamp());
$$;

CREATE FUNCTION security.finish_hosted_login(account_id uuid, expected_version bigint, expected_hash text, verified boolean, session_digest text, replacement_hash text DEFAULT NULL) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account security.hosted_accounts;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id FOR SHARE;
 SELECT * INTO account FROM security.hosted_accounts WHERE principal_id=account_id FOR UPDATE;
 IF NOT FOUND OR NOT security.hosted_account_active(account_id) OR account.credential_version<>expected_version OR account.password_hash IS DISTINCT FROM expected_hash
  OR(account.locked_until IS NOT NULL AND account.locked_until>clock_timestamp()) THEN
  INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(CASE WHEN FOUND THEN account.principal_id END,'login',false);
  RETURN NULL;
 END IF;
 IF verified IS DISTINCT FROM true THEN
  UPDATE security.hosted_accounts SET failed_attempts=failed_attempts+1,locked_until=CASE WHEN failed_attempts+1>=5 THEN clock_timestamp()+interval '10 minutes' END WHERE principal_id=account_id;
  INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(account_id,'login',false);
  RETURN NULL;
 END IF;
 UPDATE security.hosted_accounts SET failed_attempts=0,locked_until=NULL,password_hash=coalesce(replacement_hash,password_hash) WHERE principal_id=account_id;
 INSERT INTO security.hosted_sessions(token_digest,principal_id,credential_version,expires_at) VALUES(session_digest,account_id,account.credential_version,clock_timestamp()+interval '30 minutes');
 INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(account_id,'login',true);
 RETURN account_id;
END $$;

CREATE FUNCTION security.hosted_session_info(digest text)
RETURNS TABLE(principal_id uuid,issuer text,subject text,login text,name text,tenant_id uuid,workspace_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT a.principal_id,a.issuer,a.principal_id::text,a.login,m.display_name,a.tenant_id,a.workspace_id
 FROM security.hosted_sessions s JOIN security.hosted_accounts a ON a.principal_id=s.principal_id AND a.credential_version=s.credential_version
 JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.principal_id)
 WHERE s.token_digest=digest AND s.revoked_at IS NULL AND s.expires_at>clock_timestamp() AND security.hosted_account_active(a.principal_id);
$$;
CREATE FUNCTION security.revoke_hosted_session(digest text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE target uuid;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id FOR UPDATE;
 UPDATE security.hosted_sessions SET revoked_at=clock_timestamp() WHERE token_digest=digest AND revoked_at IS NULL RETURNING principal_id INTO target;
 IF FOUND THEN INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(target,'logout',true); END IF;
END $$;
CREATE FUNCTION security.issue_hosted_context(digest text,runtime_login name) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account record; capability uuid;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id FOR SHARE;
 SELECT * INTO account FROM security.hosted_session_info(digest);
 IF NOT FOUND THEN RAISE EXCEPTION 'The authenticated session is unavailable' USING ERRCODE='42501'; END IF;
 capability=security.issue_context(account.issuer,account.subject,'human_interactive',account.tenant_id,account.workspace_id,NULL,runtime_login,60);
 UPDATE security.request_contexts SET hosted_session_digest=digest WHERE id=capability;
 RETURN capability;
END $$;
CREATE FUNCTION security.require_hosted_session() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE bound security.request_contexts;
BEGIN
 SELECT * INTO bound FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF bound.id IS NULL OR bound.hosted_session_digest IS NULL OR NOT EXISTS(SELECT 1 FROM security.hosted_session_info(bound.hosted_session_digest) a
  WHERE(a.principal_id,a.issuer,a.subject,a.tenant_id,a.workspace_id)=(bound.actor_id,bound.identity_issuer,bound.identity_subject,bound.tenant_id,bound.workspace_id))
 THEN RAISE EXCEPTION 'The authenticated session is unavailable' USING ERRCODE='42501'; END IF;
END $$;
CREATE FUNCTION security.revoke_disabled_hosted_sessions() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE target uuid;
BEGIN
 IF NOT NEW.enabled THEN
  IF TG_TABLE_NAME='principals' THEN target=NEW.id; ELSE target=NEW.principal_id; END IF;
  UPDATE security.hosted_sessions SET revoked_at=clock_timestamp() WHERE principal_id=target AND revoked_at IS NULL;
  INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) SELECT target,'disabled',true WHERE EXISTS(SELECT 1 FROM security.hosted_accounts WHERE principal_id=target);
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER revoke_disabled_hosted_member AFTER UPDATE OF enabled ON security.workspace_memberships FOR EACH ROW EXECUTE FUNCTION security.revoke_disabled_hosted_sessions();
CREATE TRIGGER revoke_disabled_hosted_principal AFTER UPDATE OF enabled ON security.principals FOR EACH ROW EXECUTE FUNCTION security.revoke_disabled_hosted_sessions();

DO $$ DECLARE f regprocedure; BEGIN
 FOR f IN SELECT oid::regprocedure FROM pg_proc WHERE pronamespace='security'::regnamespace AND proname IN('hosted_account_active','claim_hosted_invitation','hosted_login_lookup','finish_hosted_login','hosted_session_info','revoke_hosted_session','revoke_disabled_hosted_sessions','issue_hosted_context','require_hosted_session') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION security.claim_hosted_invitation(text,text,text,text),security.hosted_login_lookup(text),security.finish_hosted_login(uuid,bigint,text,boolean,text,text),security.hosted_session_info(text),security.revoke_hosted_session(text) TO openetrm_authenticator;
GRANT EXECUTE ON FUNCTION security.issue_hosted_context(text,name) TO openetrm_authenticator;
GRANT EXECUTE ON FUNCTION security.require_hosted_session() TO openetrm_runtime;
COMMIT;
