-- The page assistant reads Jobs & Infrastructure (docs/devops-assistant.md §2–§3, D231, D233): ReadJobs, held by the
-- human roles that run jobs and by the agent reader role, reads the jobs its reader owns, or for an agent the jobs its
-- delegator owns, with their runs, audit actions and command receipts. It never reads another person's job, a job
-- manager's or operator's included, and it changes nothing: the agent reader role still holds no permission that
-- creates, changes or cancels a job.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Fail closed if a helper this migration relies on or replaces differs from the reviewed contract.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.agent_tools()','4a99dfbecfec5a07e99883fcab75cf1ae0b2f58a6479cc586cb30a987f4bc4e5'),
  ('assistant.agent_access(assistant.turns,text,text,uuid,uuid)','be38e907f1f71fb603b570ac39eef39e8c470d9dbf5d54174c86f1b8b75948d3'),
  ('assistant.is_read_access(jsonb)','c1704bb394f64b7dbdf7cc503a667468abb6a49d148dafc17c41d5c0232eefb4'),
  ('assistant.sources_readable(uuid)','c77f51e6257b2af498f7618396e971ffc0b4aa233cdbfc708066f50d541b0379'),
  ('assistant.is_navigation_target(assistant.turns,jsonb)','ddfe1f1d3e3fc5c8d168d645641ccd9f36581dfe8b7cf43233d396a38f914f16'),
  ('automation.edit_reason(automation.jobs)','8630d86b7258bc610b2aca1f9eed875292f18338b2f5c2746b75ee5c329e8001')
 )v(signature,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl=ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[])
  THEN RAISE EXCEPTION 'job_reads: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='assistant.tool_read_access'::regclass AND conname='tool_read_access_check' AND contype='c')
 THEN RAISE EXCEPTION 'job_reads: unexpected helper %','assistant.tool_read_access_check' USING ERRCODE='55000';END IF;
END$$;

INSERT INTO security.permissions VALUES('ReadJobs');
INSERT INTO security.role_permissions SELECT id,'ReadJobs' FROM security.roles
 WHERE code IN('viewer','trader','risk_analyst','market_data_editor','market_data_approver','access_administrator','agent_reader');

CREATE OR REPLACE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades']),
  ('read_custom_curves',ARRAY['ReadMarketData']),('prepare_custom_curve',ARRAY['ReadMarketData']),('read_jobs',ARRAY['ReadJobs'])
$$;

-- A job read runs under ReadJobs over its workspace's jobs, in no catalog: a job's owner never changes, so the
-- permission in the workspace is all a recheck needs.
ALTER TABLE assistant.tool_read_access DROP CONSTRAINT tool_read_access_check,ADD CONSTRAINT tool_read_access_check CHECK(CASE access
  WHEN 'market_access' THEN permission='ReadMarketData' AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
  WHEN 'reference_access' THEN permission='ReadMarketData' AND market_catalog_id IS NULL AND reference_catalog_id IS NOT NULL
  WHEN 'trading_access' THEN permission IN('ReadTrades','ReadTradeParties') AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
  WHEN 'job_access' THEN permission='ReadJobs' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
  ELSE false END);
CREATE OR REPLACE FUNCTION assistant.agent_access(t assistant.turns,access text,permission text,market_catalog uuid,reference_catalog uuid) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT CASE access
  WHEN 'market_access' THEN permission='ReadMarketData' AND assistant.agent_permission(t,permission)
   AND EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE s.catalog_id=market_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))
  WHEN 'reference_access' THEN permission='ReadMarketData' AND assistant.agent_permission(t,permission)
   AND EXISTS(SELECT 1 FROM security.reference_catalog_scopes s WHERE s.catalog_id=reference_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))
  WHEN 'trading_access' THEN assistant.agent_permission(t,permission) AND assistant.agent_permission(t,'ReadMarketData')
   AND EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE s.catalog_id=market_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))
  WHEN 'job_access' THEN permission='ReadJobs' AND market_catalog IS NULL AND reference_catalog IS NULL AND assistant.agent_permission(t,permission)
  ELSE false END;
$$;
CREATE OR REPLACE FUNCTION assistant.is_read_access(a jsonb) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN jsonb_typeof(a) IS DISTINCT FROM 'object' THEN false
  WHEN(SELECT array_agg(k ORDER BY k COLLATE "C") FROM jsonb_object_keys(a) k)<>ARRAY['access','catalogId','permission'] THEN false
  WHEN a->>'access'='job_access' THEN a->>'permission'='ReadJobs' AND jsonb_typeof(a->'catalogId')='null'
  WHEN NOT assistant.is_uuid_array(jsonb_build_array(a->'catalogId')) THEN false
  ELSE CASE a->>'access'
   WHEN 'market_access' THEN a->>'permission'='ReadMarketData' AND EXISTS(SELECT 1 FROM market_data.catalogs c WHERE c.id=(a->>'catalogId')::uuid)
   WHEN 'reference_access' THEN a->>'permission'='ReadMarketData' AND EXISTS(SELECT 1 FROM ref_data.catalogs c WHERE c.id=(a->>'catalogId')::uuid)
   WHEN 'trading_access' THEN a->>'permission' IN('ReadTrades','ReadTradeParties')
    AND EXISTS(SELECT 1 FROM market_data.catalogs c WHERE c.id=(a->>'catalogId')::uuid)
   ELSE false END END;
$$;
CREATE OR REPLACE FUNCTION assistant.sources_readable(turn uuid) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT NOT EXISTS(SELECT 1 FROM assistant.turn_sources(sources_readable.turn) s WHERE NOT CASE s.access
  WHEN 'market_access' THEN security.market_access(s.market_catalog_id,s.permission)
  WHEN 'reference_access' THEN security.reference_access(s.reference_catalog_id,s.permission)
  WHEN 'job_access' THEN security.has_permission(s.permission)
  ELSE security.trading_access(s.market_catalog_id,s.permission) END);
$$;

-- Navigation opens Jobs & Infrastructure, as the page or a job's history.
DO $upgrade$
DECLARE definition text;before text;
BEGIN
 definition:=pg_get_functiondef('assistant.is_navigation_target(assistant.turns,jsonb)'::regprocedure);
 before:='''parties'',''blotter'',''custom_curves''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'job_reads: unexpected target pages' USING ERRCODE='55000';END IF;
 definition:=replace(definition,before,before||',''jobs''');
 before:='WHEN ''record'' THEN target->>''page'' IN(''curves'',''blotter'')';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'job_reads: unexpected record pages' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before,'WHEN ''record'' THEN target->>''page'' IN(''curves'',''blotter'',''jobs'')');
END $upgrade$;

-- A job's owner and scope never change: the trigger refuses any update that changes them, the table owner's included,
-- though a superuser can disable triggers. A job read's access is the reader's ReadJobs in the workspace, which holds
-- only while the jobs it read stay the reader's.
CREATE FUNCTION automation.guard_job_owner() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF(NEW.owner_id,NEW.tenant_id,NEW.workspace_id) IS DISTINCT FROM(OLD.owner_id,OLD.tenant_id,OLD.workspace_id) THEN
  RAISE EXCEPTION 'job_owner_immutable' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER immutable_job_owner BEFORE UPDATE ON automation.jobs FOR EACH ROW EXECUTE FUNCTION automation.guard_job_owner();

-- The reader of a job read: a bound request context holding ReadJobs, which for an agent also requires its current
-- delegation listing it and its delegator holding it, and the person whose jobs it reads, the delegator for an agent.
CREATE FUNCTION automation.job_reader(OUT tenant_id uuid,OUT workspace_id uuid,OUT owner_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 SELECT r.* INTO c FROM security.request_contexts r
  JOIN security.principals p ON p.id=r.actor_id AND p.enabled
  JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(r.tenant_id,r.workspace_id,r.actor_id) AND m.enabled
  WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF c.id IS NULL OR NOT security.has_permission('ReadJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 tenant_id:=c.tenant_id;workspace_id:=c.workspace_id;
 owner_id:=CASE WHEN c.delegation_id IS NULL THEN c.actor_id
  ELSE(SELECT d.delegator_id FROM security.agent_delegations d WHERE d.id=c.delegation_id AND d.agent_id=c.actor_id) END;
 IF owner_id IS NULL THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
END $$;

-- A run as its owner's reads show it: its schedule, attempt, state, times, error code with the explanation kept for it,
-- and the observed state of its work, never its service, worker, fence or lease.
CREATE FUNCTION automation.owned_run(r automation.runs) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',r.id,'dueAt',r.due_at,'jobVersion',r.job_version,'attempt',r.attempt,'state',r.state,'startedAt',r.started_at,
  'deadlineAt',r.deadline_at,'finishedAt',r.finished_at,'errorCode',r.error_code,'error',CASE r.error_code
   WHEN 'authority_revoked' THEN 'The owner''s or service''s authority to run this job ended during the run.'
   WHEN 'job_expired' THEN 'The job expired before the run finished.'
   WHEN 'lease_expired' THEN 'The worker running it stopped renewing its claim, so the run was ended for recovery.'
   WHEN 'worker_interrupted' THEN 'The worker stopped before the run finished.'
   WHEN 'outcome_unknown' THEN 'The outcome of the launched work could not be confirmed.'
   WHEN 'pilot_close_inputs' THEN 'The run''s selected close inputs were unavailable or incomplete.'
   WHEN 'risk_job_authority_changed' THEN 'Authority the risk calculation needs changed during the run.' END,
  'observation',r.observation_state,'launchPrepared',r.launch_prepared,'serviceRemoved',r.service_removed);
$$;
-- A job as its owner's reads show it: its terms, state and version, whether it can be edited now and why not, and its
-- latest run.
CREATE FUNCTION automation.owned_job(j automation.jobs) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',j.id,'name',j.name,'operation',j.operation,'state',j.state,'version',j.version,'dueAt',j.next_due_at,
  'expiresAt',j.expires_at,'intervalSeconds',j.interval_seconds,'timeZone',j.time_zone,'misfire',j.misfire,'overlap',j.overlap_policy,
  'timeoutSeconds',j.timeout_seconds,'maximumAttempts',j.maximum_attempts,
  'profileName',(SELECT p.name FROM automation.execution_profiles p WHERE(p.id,p.version)=(j.profile_id,j.profile_version)),'profileVersion',j.profile_version,
  'createdAt',j.created_at,'canEdit',automation.edit_reason(j) IS NULL,'editReason',automation.edit_reason(j),
  'latestRun',(SELECT automation.owned_run(r) FROM automation.runs r WHERE r.job_id=j.id ORDER BY r.started_at DESC,r.id LIMIT 1));
$$;

-- Who acted on a job, as its owner's reads name them: the owner, an agent, a service or another person, never by name.
CREATE FUNCTION automation.job_actor(owner_id uuid,actor_id uuid) RETURNS text
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN actor_id=owner_id THEN 'you' ELSE(SELECT CASE p.kind WHEN 'agent' THEN 'agent' WHEN 'service' THEN 'service' ELSE 'another_person' END
  FROM security.principals p WHERE p.id=actor_id) END;
$$;

-- What a command receipt carried, as its owner's reads show it: whether it created, updated or controlled the job, and
-- its schedule terms and target, nested under a schedule for a close's creation. Only these named terms are read, so
-- a request identity, at any depth, and a close's financial definition never are.
CREATE FUNCTION automation.receipt_terms(request jsonb) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('kind',CASE WHEN terms ? 'action' THEN 'control' WHEN terms ? 'id' THEN 'update' ELSE 'create' END)
  ||coalesce((SELECT jsonb_object_agg(k,v) FROM jsonb_each(terms) t(k,v) WHERE k IN('name','operation','dueAt','expiresAt','intervalSeconds','timeZone',
   'misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion','id','action','expectedVersion')),'{}'::jsonb)
 FROM(SELECT CASE WHEN jsonb_typeof(request->'schedule')='object' THEN request->'schedule' ELSE request END terms)r;
$$;

-- The reader's own jobs, newest first, 25 to a page: a row with its latest run takes about 1.1 KB, so a page stays well
-- within one tool result's 64 KiB.
CREATE FUNCTION automation.owned_jobs(page integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;
BEGIN
 r:=automation.job_reader();
 IF page IS NULL OR page NOT BETWEEN 0 AND 10000 THEN RAISE EXCEPTION 'job_input';END IF;
 RETURN jsonb_build_object('page',page,'total',(SELECT count(*) FROM automation.jobs j WHERE(j.tenant_id,j.workspace_id,j.owner_id)=(r.tenant_id,r.workspace_id,r.owner_id)),
  'jobs',coalesce((SELECT jsonb_agg(automation.owned_job(j) ORDER BY j.created_at DESC,j.id) FROM(SELECT * FROM automation.jobs x
   WHERE(x.tenant_id,x.workspace_id,x.owner_id)=(r.tenant_id,r.workspace_id,r.owner_id) ORDER BY x.created_at DESC,x.id OFFSET page*25 LIMIT 25)j),'[]'::jsonb));
END $$;

-- One of the reader's jobs with its history, newest first, sized so a history fits one tool result: its latest 25
-- runs; its audit actions before an audit sequence, 100 at a time, which record finished runs' outcomes; and its latest 25
-- command receipts, by their named terms. Another person's job reads like an
-- unknown one (job_not_found).
CREATE FUNCTION automation.owned_job_history(job uuid,before bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;j automation.jobs;
BEGIN
 r:=automation.job_reader();
 IF job IS NULL OR before<1 THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT x.* INTO j FROM automation.jobs x WHERE x.id=owned_job_history.job AND(x.tenant_id,x.workspace_id,x.owner_id)=(r.tenant_id,r.workspace_id,r.owner_id);
 IF j.id IS NULL THEN RAISE EXCEPTION 'job_not_found';END IF;
 RETURN jsonb_build_object('job',automation.owned_job(j),
  'runs',coalesce((SELECT jsonb_agg(automation.owned_run(x) ORDER BY x.started_at DESC,x.id) FROM(SELECT * FROM automation.runs y WHERE y.job_id=j.id
   ORDER BY y.started_at DESC,y.id LIMIT 25)x),'[]'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('sequence',a.sequence,'action',a.action,'recordedAt',a.recorded_at,'runId',a.run_id,
    'actor',automation.job_actor(j.owner_id,a.actor_id)) ORDER BY a.sequence DESC)
   FROM(SELECT * FROM automation.audit z WHERE z.job_id=j.id AND(before IS NULL OR z.sequence<before) ORDER BY z.sequence DESC LIMIT 100)a),'[]'::jsonb),
  'commands',coalesce((SELECT jsonb_agg(jsonb_build_object('committedAt',c.committed_at,'actor',automation.job_actor(j.owner_id,c.actor_id),
    'request',automation.receipt_terms(c.request)) ORDER BY c.committed_at DESC,c.request_id)
   FROM(SELECT * FROM automation.commands w WHERE w.job_id=j.id ORDER BY w.committed_at DESC,w.request_id LIMIT 25)c),'[]'::jsonb));
END $$;

ALTER FUNCTION automation.job_reader() OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.owned_run(automation.runs) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.job_actor(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.receipt_terms(jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.guard_job_owner() OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.owned_job(automation.jobs) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.owned_jobs(integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.owned_job_history(uuid,bigint) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.job_reader(),automation.owned_run(automation.runs),automation.job_actor(uuid,uuid),automation.receipt_terms(jsonb),
 automation.guard_job_owner(),automation.owned_job(automation.jobs),
 automation.owned_jobs(integer),automation.owned_job_history(uuid,bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.owned_jobs(integer),automation.owned_job_history(uuid,bigint) TO openetrm_runtime;
COMMIT;
