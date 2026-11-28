-- Issue reporting is off until an administrator saves an explicit policy revision. A request is retained once
-- per person and request identity. Its dispatch to the external tracker is admitted once, in its own commit, under
-- the policy and authority current at that moment. An outcome that cannot be confirmed stays uncertain, and nothing
-- is sent again until the person decides to resend it.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;

INSERT INTO security.permissions VALUES('SubmitIssueReports'),('ManageIssueReporting');
DO $$
DECLARE submitters text[]=ARRAY['viewer','trader','risk_analyst','market_data_editor','market_data_approver','access_administrator'];
 missing text;
BEGIN
 -- An installation without one of these roles stops here instead of receiving part of the grant.
 SELECT string_agg(w.code,', ' ORDER BY w.code) INTO missing FROM unnest(submitters) w(code)
 WHERE NOT EXISTS(SELECT 1 FROM security.roles r WHERE r.code=w.code);
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'issue_report_roles' USING DETAIL='Missing installed roles: '||missing;END IF;
 INSERT INTO security.role_permissions SELECT r.id,'SubmitIssueReports' FROM security.roles r WHERE r.code=ANY(submitters);
 INSERT INTO security.role_permissions SELECT r.id,'ManageIssueReporting' FROM security.roles r WHERE r.code='access_administrator';
END$$;

-- A policy revision is complete: submission is off with no bound, or on with an explicit daily bound per person.
CREATE FUNCTION assistant.is_issue_report_policy(mode text,daily_limit integer) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(mode='disabled' AND daily_limit IS NULL OR mode='review_required' AND daily_limit BETWEEN 1 AND 50,false);
$$;
-- Text a person can read as written: at least one visible character, no control characters and no characters
-- that reorder or break the line invisibly. A title is one line; a body may also hold tabs and line breaks.
CREATE FUNCTION assistant.is_issue_report_content(kind text,title text,body text,repository text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(kind IN('bug','request','question')
  AND length(title) BETWEEN 1 AND 120 AND title!~'[\u0001-\u001F\u007F-\u009F\u061C\u200E\u200F\u2028-\u202E\u2066-\u2069]'
  AND length(body) BETWEEN 1 AND 4000 AND body!~'[\u0001-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u061C\u200E\u200F\u2028-\u202E\u2066-\u2069]'
  AND title~'[^\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200D\u202F\u205F\u2060\u3000\uFEFF]'
  AND body~'[^\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200D\u202F\u205F\u2060\u3000\uFEFF]'
  AND repository~'^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$' AND repository!~'/[.]{1,2}$',false);
$$;
-- The tracker's own page for one issue of one repository. The tracker may spell the repository in another case.
CREATE FUNCTION assistant.is_issue_report_link(repository text,number bigint,url text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(number>0 AND lower(url)=lower('https://github.com/'||repository||'/issues/'||number),false);
$$;
CREATE TABLE assistant.issue_report_policy_versions(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,version integer NOT NULL CHECK(version>0),
 mode text NOT NULL,daily_limit integer,CHECK(assistant.is_issue_report_policy(mode,daily_limit)),
 actor_id uuid NOT NULL REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,version),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TABLE assistant.issue_report_policy_commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 expected_version integer NOT NULL,mode text NOT NULL,daily_limit integer,version integer NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,actor_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id,version) REFERENCES assistant.issue_report_policy_versions
);
-- One row per request. The marker is the opaque value reconciliation looks for in the tracker; it identifies
-- no person, workspace or installation. The attempt fences each dispatch's outcome.
CREATE TABLE assistant.issue_reports(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 kind text NOT NULL,title text NOT NULL,body text NOT NULL,repository text NOT NULL,CHECK(assistant.is_issue_report_content(kind,title,body,repository)),
 policy_version integer NOT NULL,marker uuid NOT NULL UNIQUE,
 state text NOT NULL CHECK(state IN('pending','dispatching','submitted','failed','uncertain','abandoned')),
 attempt integer NOT NULL CHECK(attempt>=0),dispatch_admitted_at timestamptz,
 issue_number bigint,issue_url text,CHECK(issue_number IS NULL AND issue_url IS NULL OR assistant.is_issue_report_link(repository,issue_number,issue_url)),
 error_code text CHECK(error_code~'^[a-z][a-z0-9_]{0,63}$'),requested_at timestamptz NOT NULL,settled_at timestamptz,lookup_admitted_at timestamptz,
 UNIQUE(tenant_id,workspace_id,actor_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id,policy_version) REFERENCES assistant.issue_report_policy_versions,
 CHECK((state='submitted')=(issue_number IS NOT NULL)),
 CHECK((state IN('submitted','failed','abandoned'))=(settled_at IS NOT NULL)),
 -- Pending means never sent: a report that was ever dispatched does not return to it.
 CHECK((attempt>0)=(dispatch_admitted_at IS NOT NULL) AND(attempt=0)=(state='pending' OR state='abandoned' AND dispatch_admitted_at IS NULL))
);
CREATE INDEX issue_reports_by_person ON assistant.issue_reports(tenant_id,workspace_id,actor_id,requested_at DESC);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['issue_report_policy_versions','issue_report_policy_commands','issue_reports'] LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON assistant.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['issue_report_policy_versions','issue_report_policy_commands'] LOOP
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

-- The caller is an active human member acting for themselves; an agent's delegated context is refused.
CREATE FUNCTION assistant.issue_report_actor(permission text) RETURNS security.request_contexts LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);
BEGIN
 IF permission IS NOT NULL AND NOT security.has_permission(permission) THEN RAISE EXCEPTION 'No current issue report permission' USING ERRCODE='42501';END IF;
 RETURN c;
END$$;
CREATE FUNCTION assistant.issue_report_policy(tenant uuid,workspace uuid) RETURNS assistant.issue_report_policy_versions LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT v FROM assistant.issue_report_policy_versions v WHERE(v.tenant_id,v.workspace_id)=(tenant,workspace) ORDER BY v.version DESC LIMIT 1;
$$;
CREATE FUNCTION assistant.issue_report_policy_view(v assistant.issue_report_policy_versions) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('version',v.version,'mode',v.mode,'dailyLimit',v.daily_limit);
$$;
CREATE FUNCTION assistant.issue_report_view(x assistant.issue_reports) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',x.id,'requestId',x.request_id,'kind',x.kind,'title',x.title,'repository',x.repository,'state',x.state,'attempt',x.attempt,
  'issueNumber',x.issue_number,'issueUrl',x.issue_url,'errorCode',x.error_code,'requestedAt',x.requested_at,'settledAt',x.settled_at,
  -- What the person may still decide about it, so no caller restates the state rules.
  'decisions',CASE x.state WHEN 'uncertain' THEN '["look","resend","abandon"]'::jsonb WHEN 'pending' THEN '["abandon"]'::jsonb ELSE '[]'::jsonb END);
$$;
-- A dispatch admitted more than two minutes ago with no recorded outcome may or may not have reached the tracker.
-- The sender's whole exchange is bounded well inside that window.
CREATE FUNCTION assistant.own_issue_report(c security.request_contexts,report uuid) RETURNS assistant.issue_reports LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE x assistant.issue_reports;
BEGIN
 SELECT * INTO x FROM assistant.issue_reports s WHERE s.id=report AND(s.tenant_id,s.workspace_id,s.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id) FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'issue_report_not_found';END IF;
 IF x.state='dispatching' AND x.dispatch_admitted_at<=clock_timestamp()-interval '2 minutes' THEN
  UPDATE assistant.issue_reports SET state='uncertain',error_code='outcome_unknown' WHERE id=x.id RETURNING * INTO x;
 END IF;
 RETURN x;
END$$;

CREATE FUNCTION assistant.issue_report_policy_directory() RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor('ManageIssueReporting');head assistant.issue_report_policy_versions;
BEGIN
 head:=assistant.issue_report_policy(c.tenant_id,c.workspace_id);
 RETURN jsonb_build_object('current',CASE WHEN head.version IS NOT NULL THEN assistant.issue_report_policy_view(head) END,
  'history',coalesce((SELECT jsonb_agg(assistant.issue_report_policy_view(v)||jsonb_build_object('recordedAt',v.recorded_at) ORDER BY v.version DESC)
   FROM(SELECT * FROM assistant.issue_report_policy_versions WHERE(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id) ORDER BY version DESC LIMIT 20)v),'[]'::jsonb));
END$$;
CREATE FUNCTION assistant.save_issue_report_policy(request uuid,expected integer,setting text,bound integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor('ManageIssueReporting');previous assistant.issue_report_policy_commands;head assistant.issue_report_policy_versions;
BEGIN
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR expected IS NULL OR expected<0 OR NOT assistant.is_issue_report_policy(setting,bound) THEN
  RAISE EXCEPTION 'issue_report_policy_input';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(c.workspace_id::text||':issue_report_policy',0));
 c:=assistant.issue_report_actor('ManageIssueReporting');
 SELECT * INTO previous FROM assistant.issue_report_policy_commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,request);
 IF FOUND THEN
  IF(previous.expected_version,previous.mode,previous.daily_limit) IS DISTINCT FROM(expected,setting,bound) THEN RAISE EXCEPTION 'issue_report_policy_conflict';END IF;
  RETURN(SELECT assistant.issue_report_policy_view(v) FROM assistant.issue_report_policy_versions v WHERE(v.tenant_id,v.workspace_id,v.version)=(c.tenant_id,c.workspace_id,previous.version));
 END IF;
 head:=assistant.issue_report_policy(c.tenant_id,c.workspace_id);
 IF coalesce(head.version,0)<>expected THEN RAISE EXCEPTION 'issue_report_policy_version_conflict';END IF;
 -- Saving the facts already in force records the request and no new revision.
 IF head.version IS NULL OR(head.mode,head.daily_limit) IS DISTINCT FROM(setting,bound) THEN
  INSERT INTO assistant.issue_report_policy_versions VALUES(c.tenant_id,c.workspace_id,expected+1,setting,bound,c.actor_id,clock_timestamp()) RETURNING * INTO head;
 END IF;
 INSERT INTO assistant.issue_report_policy_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,request,expected,setting,bound,head.version);
 c:=assistant.issue_report_actor('ManageIssueReporting');
 RETURN assistant.issue_report_policy_view(head);
END$$;

CREATE FUNCTION assistant.my_issue_reports() RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor('SubmitIssueReports');
BEGIN
 PERFORM assistant.own_issue_report(c,s.id) FROM assistant.issue_reports s
  WHERE(s.tenant_id,s.workspace_id,s.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id) AND s.state='dispatching';
 RETURN jsonb_build_object('mode',(assistant.issue_report_policy(c.tenant_id,c.workspace_id)).mode,
  'submissions',coalesce((SELECT jsonb_agg(assistant.issue_report_view(s) ORDER BY s.requested_at DESC,s.id)
   FROM(SELECT * FROM assistant.issue_reports WHERE(tenant_id,workspace_id,actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
    ORDER BY requested_at DESC,id LIMIT 25)s),'[]'::jsonb));
END$$;
CREATE FUNCTION assistant.request_issue_report(request uuid,category text,subject text,description text,tracker text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor('SubmitIssueReports');previous assistant.issue_reports;head assistant.issue_report_policy_versions;
BEGIN
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR NOT assistant.is_issue_report_content(category,subject,description,tracker) THEN
  RAISE EXCEPTION 'issue_report_input';
 END IF;
 -- One person's requests are admitted in order, so the identity check and the daily bound see every earlier one.
 PERFORM pg_advisory_xact_lock(hashtextextended(c.workspace_id::text||':issue_report_request:'||c.actor_id::text,0));
 c:=assistant.issue_report_actor('SubmitIssueReports');
 SELECT * INTO previous FROM assistant.issue_reports s WHERE(s.tenant_id,s.workspace_id,s.actor_id,s.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,request);
 IF FOUND THEN
  -- The request is the person's kind, title and body. The repository is the installation's, so a later change of
  -- it does not turn a retry into a conflict: the retry answers with the report as retained.
  IF(previous.kind,previous.title,previous.body) IS DISTINCT FROM(category,subject,description) THEN RAISE EXCEPTION 'issue_report_conflict';END IF;
  RETURN assistant.issue_report_view(assistant.own_issue_report(c,previous.id));
 END IF;
 head:=assistant.issue_report_policy(c.tenant_id,c.workspace_id);
 IF head.version IS NULL OR head.mode='disabled' THEN RAISE EXCEPTION 'issue_reporting_disabled';END IF;
 -- The bound counts every request of the last 24 hours that was sent or may still be; one given up before it
 -- was ever sent is not counted.
 IF(SELECT count(*) FROM assistant.issue_reports s WHERE(s.tenant_id,s.workspace_id,s.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND s.requested_at>clock_timestamp()-interval '24 hours' AND NOT(s.state='abandoned' AND s.attempt=0))>=head.daily_limit THEN
  RAISE EXCEPTION 'issue_report_limit';
 END IF;
 INSERT INTO assistant.issue_reports(id,tenant_id,workspace_id,actor_id,request_id,kind,title,body,repository,policy_version,marker,state,attempt,requested_at)
  VALUES(gen_random_uuid(),c.tenant_id,c.workspace_id,c.actor_id,request,category,subject,description,tracker,head.version,gen_random_uuid(),'pending',0,clock_timestamp())
  RETURNING * INTO previous;
 RETURN assistant.issue_report_view(previous);
END$$;
-- Moves one report into flight under the next attempt, for a person who may submit now under a policy that is
-- on now, and returns what the sender needs. Each caller runs it in its own transaction immediately before the
-- external request.
CREATE FUNCTION assistant.dispatch_issue_report(c security.request_contexts,x assistant.issue_reports) RETURNS jsonb LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE head assistant.issue_report_policy_versions:=assistant.issue_report_policy(c.tenant_id,c.workspace_id);
BEGIN
 IF head.version IS NULL OR head.mode='disabled' THEN RAISE EXCEPTION 'issue_reporting_disabled';END IF;
 UPDATE assistant.issue_reports SET state='dispatching',attempt=attempt+1,dispatch_admitted_at=clock_timestamp(),error_code=NULL WHERE id=x.id RETURNING * INTO x;
 RETURN jsonb_build_object('id',x.id,'attempt',x.attempt,'marker',x.marker,'repository',x.repository,'kind',x.kind,'title',x.title,'body',x.body,
  'requestedAt',x.requested_at);
END$$;
-- The first send of a retained request. Null means there is nothing to send: the report is settled,
-- unconfirmed or already in flight.
CREATE FUNCTION assistant.admit_issue_report_dispatch(report uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor(NULL);x assistant.issue_reports;
BEGIN
 x:=assistant.own_issue_report(c,report);
 c:=assistant.issue_report_actor('SubmitIssueReports');
 IF x.state<>'pending' THEN RETURN NULL;END IF;
 RETURN assistant.dispatch_issue_report(c,x);
END$$;
-- The person's explicit decision to send an unconfirmed report again, admitted in the same step so a refusal
-- leaves the report unconfirmed. It names the attempt it follows: asked again after it took effect it admits
-- nothing. It waits until the earlier exchange can no longer be completing, so its lookup sees what that
-- exchange created.
CREATE FUNCTION assistant.admit_issue_report_resend(report uuid,expected integer) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor(NULL);x assistant.issue_reports;
BEGIN
 IF expected IS NULL OR expected<1 THEN RAISE EXCEPTION 'issue_report_input';END IF;
 x:=assistant.own_issue_report(c,report);
 IF x.attempt=expected+1 THEN RETURN NULL;END IF;
 IF x.attempt<>expected THEN RAISE EXCEPTION 'issue_report_fence';END IF;
 IF x.state<>'uncertain' THEN RAISE EXCEPTION 'issue_report_transition';END IF;
 c:=assistant.issue_report_actor('SubmitIssueReports');
 IF x.dispatch_admitted_at>clock_timestamp()-interval '2 minutes' THEN RAISE EXCEPTION 'issue_report_wait';END IF;
 RETURN assistant.dispatch_issue_report(c,x);
END$$;
-- Records what the tracker answered for one attempt. An outcome is a fact about a request already sent, so it
-- is recorded for its owner whatever the policy or their permissions have since become.
CREATE FUNCTION assistant.complete_issue_report(report uuid,expected integer,outcome text,number bigint,url text,code text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor(NULL);x assistant.issue_reports;
BEGIN
 IF expected IS NULL OR expected<1 OR NOT coalesce(
   outcome='submitted' AND number IS NOT NULL AND url IS NOT NULL AND code IS NULL
   OR outcome IN('failed','uncertain') AND number IS NULL AND url IS NULL AND code~'^[a-z][a-z0-9_]{0,63}$',false) THEN
  RAISE EXCEPTION 'issue_report_input';
 END IF;
 x:=assistant.own_issue_report(c,report);
 IF outcome='submitted' AND NOT assistant.is_issue_report_link(x.repository,number,url) THEN RAISE EXCEPTION 'issue_report_input';END IF;
 IF(x.attempt,x.state,x.issue_number,x.issue_url) IS NOT DISTINCT FROM(expected,outcome,number,url) AND(outcome='submitted' OR x.error_code=code) THEN
  RETURN assistant.issue_report_view(x);
 END IF;
 IF x.attempt<>expected OR x.state NOT IN('dispatching','uncertain') THEN RAISE EXCEPTION 'issue_report_fence';END IF;
 UPDATE assistant.issue_reports SET state=outcome,issue_number=number,issue_url=url,error_code=code,
  settled_at=CASE WHEN outcome<>'uncertain' THEN clock_timestamp() END WHERE id=x.id RETURNING * INTO x;
 RETURN assistant.issue_report_view(x);
END$$;
CREATE FUNCTION assistant.issue_report_status(report uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 RETURN assistant.issue_report_view(assistant.own_issue_report(assistant.issue_report_actor(NULL),report));
END$$;
-- Admits one lookup in the tracker for a report whose outcome is not confirmed, and returns what it needs; null
-- for any other report. A lookup calls the tracker, so it needs the permission and is admitted once a minute.
CREATE FUNCTION assistant.admit_issue_report_lookup(report uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor(NULL);x assistant.issue_reports;
BEGIN
 x:=assistant.own_issue_report(c,report);
 c:=assistant.issue_report_actor('SubmitIssueReports');
 IF x.state<>'uncertain' THEN RETURN NULL;END IF;
 IF x.lookup_admitted_at>clock_timestamp()-interval '1 minute' THEN RAISE EXCEPTION 'issue_report_wait';END IF;
 UPDATE assistant.issue_reports SET lookup_admitted_at=clock_timestamp() WHERE id=x.id;
 RETURN jsonb_build_object('id',x.id,'attempt',x.attempt,'marker',x.marker,'repository',x.repository,'requestedAt',x.requested_at);
END$$;
-- The person's explicit decision to give up a report that is not settled. It names the attempt it follows, so
-- the same decision again changes nothing.
CREATE FUNCTION assistant.abandon_issue_report(report uuid,expected integer) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.issue_report_actor(NULL);x assistant.issue_reports;
BEGIN
 IF expected IS NULL OR expected<0 THEN RAISE EXCEPTION 'issue_report_input';END IF;
 x:=assistant.own_issue_report(c,report);
 IF x.attempt<>expected THEN RAISE EXCEPTION 'issue_report_fence';END IF;
 IF x.state='abandoned' THEN RETURN assistant.issue_report_view(x);END IF;
 IF x.state NOT IN('pending','uncertain') THEN RAISE EXCEPTION 'issue_report_transition';END IF;
 UPDATE assistant.issue_reports SET state='abandoned',settled_at=clock_timestamp() WHERE id=x.id RETURNING * INTO x;
 RETURN assistant.issue_report_view(x);
END$$;

DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['assistant.is_issue_report_policy(text,integer)','assistant.is_issue_report_content(text,text,text,text)',
  'assistant.is_issue_report_link(text,bigint,text)','assistant.issue_report_actor(text)','assistant.issue_report_policy(uuid,uuid)',
  'assistant.issue_report_policy_view(assistant.issue_report_policy_versions)','assistant.issue_report_view(assistant.issue_reports)',
  'assistant.own_issue_report(security.request_contexts,uuid)','assistant.dispatch_issue_report(security.request_contexts,assistant.issue_reports)',
  'assistant.issue_report_policy_directory()','assistant.save_issue_report_policy(uuid,integer,text,integer)','assistant.my_issue_reports()',
  'assistant.request_issue_report(uuid,text,text,text,text)','assistant.admit_issue_report_dispatch(uuid)','assistant.admit_issue_report_resend(uuid,integer)',
  'assistant.complete_issue_report(uuid,integer,text,bigint,text,text)','assistant.issue_report_status(uuid)','assistant.admit_issue_report_lookup(uuid)',
  'assistant.abandon_issue_report(uuid,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION assistant.issue_report_policy_directory(),assistant.save_issue_report_policy(uuid,integer,text,integer),assistant.my_issue_reports(),
 assistant.request_issue_report(uuid,text,text,text,text),assistant.admit_issue_report_dispatch(uuid),assistant.admit_issue_report_resend(uuid,integer),
 assistant.complete_issue_report(uuid,integer,text,bigint,text,text),assistant.issue_report_status(uuid),assistant.admit_issue_report_lookup(uuid),
 assistant.abandon_issue_report(uuid,integer) TO openetrm_runtime;
COMMIT;
