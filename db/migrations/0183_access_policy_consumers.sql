-- Admission pins current access policy; historical sessions/invitations keep
-- their original expiry and are not relabeled with an invented prior policy.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Refuse drift in the reviewed helper bodies, ownership or execution privileges.
DO $$DECLARE replaced record;BEGIN
 PERFORM 1 FROM security.epoch WHERE id FOR UPDATE;
 FOR replaced IN SELECT * FROM(VALUES
  ('security.claim_hosted_invitation(text,text,text,text)','40dd8273da34850e95f9a2626c8875b24d01fb4a77841811a47acb20cb7db690',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_authenticator=X/openetrm_security_owner']),
  ('security.finish_hosted_login(uuid,bigint,text,boolean,text,text)','fd0fbc1eb6eeb2dc1a164a69dfec5dda7c0195b6893a7a38671bfa2ca1811fd7',ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_authenticator=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(replaced.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=replaced.hash
   AND p.proacl=replaced.acl::aclitem[])
  THEN RAISE EXCEPTION 'operating_policy_upgrade: unexpected helper %',replaced.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
CREATE TABLE security.hosted_session_policy_pins(
 token_digest text PRIMARY KEY REFERENCES security.hosted_sessions(token_digest),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 kind text NOT NULL DEFAULT 'access' CHECK(kind='access'),revision_id uuid NOT NULL,
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions
);
CREATE TABLE security.hosted_invitation_policy_pins(
 token_digest text PRIMARY KEY REFERENCES security.hosted_invitations(token_digest),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 kind text NOT NULL DEFAULT 'access' CHECK(kind='access'),revision_id uuid NOT NULL,created_at ref_data.instant NOT NULL,
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions
);
CREATE TABLE security.hosted_login_requests(
 token_digest text PRIMARY KEY CHECK(token_digest~'^[0-9a-f]{64}$'),input_digest text NOT NULL CHECK(input_digest~'^[0-9a-f]{64}$'),
 outcome_id uuid REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 tenant_id uuid,workspace_id uuid,kind text CHECK(kind='access'),revision_id uuid,
 FOREIGN KEY(tenant_id,workspace_id,kind,revision_id) REFERENCES security.operating_policy_versions MATCH FULL
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['hosted_session_policy_pins','hosted_invitation_policy_pins','hosted_login_requests'] LOOP
  EXECUTE format('ALTER TABLE security.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE security.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE security.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON security.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON security.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION security.hosted_access_policy(tenant uuid,workspace uuid) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.operating_policy_state(security.operating_policy_current(tenant,workspace,'access'));
$$;
CREATE FUNCTION security.invitation_access_policy(digest text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account security.hosted_accounts;
BEGIN
 SELECT a.* INTO account FROM security.hosted_invitations i JOIN security.hosted_accounts a ON a.principal_id=i.principal_id
 WHERE i.token_digest=digest AND i.consumed_at IS NULL AND i.revoked_at IS NULL AND i.expires_at>clock_timestamp()
 AND security.hosted_account_active(a.principal_id);
 IF account.principal_id IS NULL THEN RETURN NULL;END IF;
 RETURN security.hosted_access_policy(account.tenant_id,account.workspace_id)||jsonb_build_object('tenantId',account.tenant_id,'workspaceId',account.workspace_id);
END$$;
CREATE FUNCTION security.issue_hosted_invitation(digest text,account_id uuid,purpose text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account security.hosted_accounts;previous security.hosted_invitations;policy security.operating_policy_versions;n timestamptz;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 IF digest IS NULL OR digest!~'^[0-9a-f]{64}$' OR purpose IS NULL OR purpose NOT IN('claim','reset') THEN RAISE EXCEPTION 'access_policy_input';END IF;
 SELECT * INTO account FROM security.hosted_accounts WHERE principal_id=account_id FOR UPDATE;
 IF account.principal_id IS NULL OR NOT security.hosted_account_active(account_id) THEN RAISE EXCEPTION 'access_policy_input';END IF;
 SELECT * INTO previous FROM security.hosted_invitations i WHERE i.token_digest=digest;
 IF FOUND THEN
  IF(previous.principal_id,previous.purpose) IS DISTINCT FROM(account_id,purpose) THEN RAISE EXCEPTION 'access_policy_conflict';END IF;
  RETURN jsonb_build_object('expiresAt',previous.expires_at);
 END IF;
 policy:=security.operating_policy_current(account.tenant_id,account.workspace_id,'access');n:=clock_timestamp();
 INSERT INTO security.hosted_invitations VALUES(digest,account_id,purpose,n+make_interval(hours=>(policy.settings->>'invitationHours')::integer),NULL,NULL);
 INSERT INTO security.hosted_invitation_policy_pins VALUES(digest,account.tenant_id,account.workspace_id,'access',policy.revision_id,n);
 RETURN jsonb_build_object('expiresAt',n+make_interval(hours=>(policy.settings->>'invitationHours')::integer));
END$$;

-- The authenticator supplies verified password work; every retry retains the
-- same outcome, and a new session records one precise expiry under one policy.
CREATE OR REPLACE FUNCTION security.finish_hosted_login(account_id uuid,expected_version bigint,expected_hash text,verified boolean,session_digest text,replacement_hash text DEFAULT NULL) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account security.hosted_accounts;policy security.operating_policy_versions;previous security.hosted_login_requests;
 input_digest text;n timestamptz;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF session_digest IS NULL OR session_digest!~'^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'access_policy_input';END IF;
 input_digest:=encode(sha256(convert_to(jsonb_build_array(account_id,expected_version,expected_hash,verified,replacement_hash)::text,'UTF8')),'hex');
 PERFORM pg_advisory_xact_lock(hashtextextended(':hosted_login:'||session_digest,0));
 SELECT * INTO previous FROM security.hosted_login_requests r WHERE r.token_digest=session_digest;
 IF FOUND THEN
  IF previous.input_digest<>input_digest THEN RAISE EXCEPTION 'access_policy_conflict';END IF;
  IF previous.outcome_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM security.hosted_session_info(session_digest)) THEN RETURN NULL;END IF;
  RETURN previous.outcome_id;
 END IF;
 SELECT * INTO account FROM security.hosted_accounts WHERE principal_id=account_id FOR UPDATE;
 IF NOT FOUND OR NOT security.hosted_account_active(account_id) OR account.credential_version<>expected_version OR account.password_hash IS DISTINCT FROM expected_hash
  OR(account.locked_until IS NOT NULL AND account.locked_until>clock_timestamp()) THEN
  INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(CASE WHEN FOUND THEN account.principal_id END,'login',false);
  INSERT INTO security.hosted_login_requests VALUES(session_digest,input_digest,NULL,clock_timestamp(),NULL,NULL,NULL,NULL);
  RETURN NULL;
 END IF;
 policy:=security.operating_policy_current(account.tenant_id,account.workspace_id,'access');
 IF verified IS DISTINCT FROM true THEN
  n:=clock_timestamp();
  UPDATE security.hosted_accounts SET failed_attempts=failed_attempts+1,
   locked_until=CASE WHEN failed_attempts+1>=(policy.settings->>'failedAttempts')::integer THEN n+make_interval(mins=>(policy.settings->>'lockoutMinutes')::integer) END
   WHERE principal_id=account_id;
  INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(account_id,'login',false);
  INSERT INTO security.hosted_login_requests VALUES(session_digest,input_digest,NULL,n,account.tenant_id,account.workspace_id,'access',policy.revision_id);
  RETURN NULL;
 END IF;
 UPDATE security.hosted_accounts SET failed_attempts=0,locked_until=NULL,password_hash=coalesce(replacement_hash,password_hash) WHERE principal_id=account_id;
 n:=clock_timestamp();
 INSERT INTO security.hosted_sessions(token_digest,principal_id,credential_version,created_at,expires_at)
 VALUES(session_digest,account_id,account.credential_version,n,n+make_interval(mins=>(policy.settings->>'sessionMinutes')::integer));
 INSERT INTO security.hosted_session_policy_pins VALUES(session_digest,account.tenant_id,account.workspace_id,'access',policy.revision_id);
 INSERT INTO security.hosted_login_requests VALUES(session_digest,input_digest,account_id,n,account.tenant_id,account.workspace_id,'access',policy.revision_id);
 INSERT INTO security.hosted_auth_audit(principal_id,event,accepted) VALUES(account_id,'login',true);
 RETURN account_id;
END$$;

-- Keep the existing atomic claim implementation owner-only. New claims must
-- prove which enrollment policy the authenticator used during password work.
ALTER FUNCTION security.claim_hosted_invitation(text,text,text,text) RENAME TO claim_hosted_invitation_unchecked;
DO $$DECLARE definition text;BEGIN
 definition:=pg_get_functiondef('security.claim_hosted_invitation_unchecked(text,text,text,text)'::regprocedure);
 IF position('claim_hosted_invitation.display_name' in definition)=0 THEN RAISE EXCEPTION 'access_policy_upgrade: unexpected claim definition';END IF;
 EXECUTE replace(definition,'claim_hosted_invitation.display_name','claim_hosted_invitation_unchecked.display_name');
END$$;
REVOKE ALL ON FUNCTION security.claim_hosted_invitation_unchecked(text,text,text,text) FROM PUBLIC,openetrm_authenticator,openetrm_runtime;
CREATE FUNCTION security.claim_hosted_invitation(digest text,desired_login text,display_name text,hash text,password_length integer,expected_policy uuid) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE policy jsonb;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 policy:=security.invitation_access_policy(digest);
 IF policy IS NULL OR expected_policy IS NULL OR(policy->>'revisionId')::uuid<>expected_policy OR password_length IS NULL
 OR password_length NOT BETWEEN (policy->'values'->>'passwordMinimum')::integer AND 1024 THEN RETURN NULL;END IF;
 RETURN security.claim_hosted_invitation_unchecked(digest,desired_login,display_name,hash);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('hosted_access_policy','invitation_access_policy','issue_hosted_invitation','finish_hosted_login','claim_hosted_invitation') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.hosted_access_policy(uuid,uuid),security.invitation_access_policy(text),security.claim_hosted_invitation(text,text,text,text,integer,uuid) TO openetrm_authenticator;
COMMIT;
