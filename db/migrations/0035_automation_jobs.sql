-- Durable scoped jobs. No workers, identities, images or schedules are seeded.
BEGIN;
CREATE ROLE openetrm_scheduler NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE SCHEMA automation AUTHORIZATION openetrm_security_owner;
INSERT INTO security.permissions VALUES('RunJobs'),('ManageJobs'),('LaunchWorkloads'),('ManageInfrastructure');
INSERT INTO security.role_permissions SELECT id,'RunJobs' FROM security.roles
 WHERE code IN('viewer','trader','risk_analyst','market_data_editor','market_data_approver','access_administrator');
INSERT INTO security.role_permissions SELECT id,'ManageJobs' FROM security.roles WHERE code='access_administrator';

CREATE TABLE automation.workers(
 login name PRIMARY KEY,principal_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 kind text NOT NULL CHECK(kind IN('scheduler','controller')),
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id)
);
CREATE TABLE automation.installation_operators(
 principal_id uuid NOT NULL REFERENCES security.principals(id),capability text NOT NULL CHECK(capability IN('ManageAllJobs','ManageInfrastructure')),
 expires_at timestamptz NOT NULL CHECK(isfinite(expires_at)),PRIMARY KEY(principal_id,capability)
);
CREATE TABLE automation.execution_profiles(
 id uuid NOT NULL,version integer NOT NULL CHECK(version>0),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),image text NOT NULL CHECK(image~'^[a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64}$'),
 network_id text NOT NULL CHECK(network_id~'^[a-z0-9]{12,64}$'),node_label text NOT NULL CHECK(node_label~'^[a-z][a-z0-9_-]{0,39}$'),
 cpu_nano bigint NOT NULL CHECK(cpu_nano BETWEEN 10000000 AND 8000000000),memory_bytes bigint NOT NULL CHECK(memory_bytes BETWEEN 16777216 AND 17179869184),
 process_limit integer NOT NULL CHECK(process_limit BETWEEN 8 AND 1024),enabled boolean NOT NULL,protocol text NOT NULL CHECK(protocol='bounded_exit_v1'),
 architecture text NOT NULL CHECK(architecture IN('amd64','arm64')),variant text NOT NULL CHECK(variant IN('','v8')),CHECK(architecture<>'amd64' OR variant=''),
 PRIMARY KEY(id,version),UNIQUE(id,version,tenant_id,workspace_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TABLE automation.jobs(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,owner_id uuid NOT NULL,delegation_id uuid REFERENCES security.agent_delegations(id),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),operation text NOT NULL CHECK(operation IN('worker_check','swarm_job')),
 state text NOT NULL CHECK(state IN('active','paused','cancelled','cancellation_requested','completed','expired','blocked')),version integer NOT NULL CHECK(version>0),
 next_due_at timestamptz NOT NULL CHECK(isfinite(next_due_at)),expires_at timestamptz NOT NULL CHECK(isfinite(expires_at)),
 interval_seconds integer CHECK(interval_seconds BETWEEN 60 AND 31536000),time_zone text NOT NULL CHECK(time_zone='UTC'),
 misfire text NOT NULL CHECK(misfire='run_once'),overlap_policy text NOT NULL CHECK(overlap_policy='forbid'),
 timeout_seconds integer NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 3600),maximum_attempts integer NOT NULL CHECK(maximum_attempts BETWEEN 1 AND 3),
 profile_id uuid,profile_version integer,created_at timestamptz NOT NULL,last_reconciled_at timestamptz NOT NULL DEFAULT '-infinity',
 FOREIGN KEY(tenant_id,workspace_id,owner_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(profile_id,profile_version,tenant_id,workspace_id) REFERENCES automation.execution_profiles(id,version,tenant_id,workspace_id),
 CHECK((operation='worker_check' AND profile_id IS NULL AND profile_version IS NULL) OR(operation='swarm_job' AND profile_id IS NOT NULL AND profile_version IS NOT NULL)),
 UNIQUE(id,tenant_id,workspace_id)
);
CREATE INDEX jobs_due ON automation.jobs(next_due_at,id) WHERE state='active';
CREATE TABLE automation.runs(
 id uuid PRIMARY KEY,job_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,due_at timestamptz NOT NULL,job_version integer NOT NULL,
 state text NOT NULL CHECK(state IN('running','succeeded','failed','cancelled','cancellation_requested')),
 fence bigint NOT NULL CHECK(fence>0),attempt integer NOT NULL CHECK(attempt>0),worker_id uuid NOT NULL,worker_login name NOT NULL,
 lease_until timestamptz NOT NULL,started_at timestamptz NOT NULL,deadline_at timestamptz NOT NULL,finished_at timestamptz,error_code text,
 observation_state text CHECK(observation_state IN('pending','unknown','stopping','running','completed','failed','stopped')),cleanup_checked_at timestamptz NOT NULL DEFAULT '-infinity',
 service_id text,launch_prepared boolean NOT NULL DEFAULT false,service_removed boolean NOT NULL DEFAULT false,CHECK(service_id IS NULL OR service_id~'^[a-z0-9]{12,64}$'),
 FOREIGN KEY(job_id,tenant_id,workspace_id) REFERENCES automation.jobs(id,tenant_id,workspace_id),UNIQUE(job_id,due_at)
);
CREATE INDEX runs_recovery ON automation.runs(lease_until) WHERE state IN('running','cancellation_requested');
CREATE TABLE automation.commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 job_id uuid NOT NULL REFERENCES automation.jobs(id),request jsonb NOT NULL,response jsonb NOT NULL,committed_at timestamptz NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,actor_id,request_id)
);
CREATE TABLE automation.audit(
 sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,job_id uuid NOT NULL REFERENCES automation.jobs(id),run_id uuid REFERENCES automation.runs(id),
 actor_id uuid NOT NULL REFERENCES security.principals(id),action text NOT NULL,recorded_at timestamptz NOT NULL,fence bigint
);
CREATE TRIGGER guard_control_change BEFORE INSERT OR UPDATE OR DELETE ON automation.workers FOR EACH STATEMENT EXECUTE FUNCTION security.guard_control_change();
CREATE TRIGGER guard_control_change BEFORE INSERT OR UPDATE OR DELETE ON automation.installation_operators FOR EACH STATEMENT EXECUTE FUNCTION security.guard_control_change();
CREATE TRIGGER guard_control_change BEFORE INSERT OR UPDATE OR DELETE ON automation.execution_profiles FOR EACH STATEMENT EXECUTE FUNCTION security.guard_control_change();
CREATE FUNCTION automation.guard_profile() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR(to_jsonb(NEW)-'enabled') IS DISTINCT FROM(to_jsonb(OLD)-'enabled') THEN RAISE EXCEPTION 'profile_immutable';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER immutable_profile BEFORE UPDATE OR DELETE ON automation.execution_profiles FOR EACH ROW EXECUTE FUNCTION automation.guard_profile();

CREATE FUNCTION automation.actor() RETURNS security.request_contexts
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts; k text; n timestamptz:=clock_timestamp();
BEGIN
 SELECT r.* INTO c FROM security.request_contexts r
 JOIN security.principals p ON p.id=r.actor_id AND p.enabled
 JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(r.tenant_id,r.workspace_id,r.actor_id) AND m.enabled
 WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>n;
 IF c.id IS NULL THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 SELECT kind INTO k FROM security.principals WHERE id=c.actor_id;
 IF k='agent' AND NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 RETURN c;
END $$;
CREATE FUNCTION automation.operator(capability text) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM automation.installation_operators o JOIN security.request_contexts c ON c.actor_id=o.principal_id
 JOIN security.principals p ON p.id=c.actor_id AND p.enabled AND p.kind IN('human','service')
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.expires_at>clock_timestamp()
 AND o.capability=operator.capability AND o.expires_at>clock_timestamp());
$$;
CREATE FUNCTION automation.can_view(tenant uuid,workspace uuid,owner_id uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 c=automation.actor();
 RETURN automation.operator('ManageAllJobs') OR ((c.tenant_id,c.workspace_id)=(tenant,workspace) AND(c.actor_id=owner_id OR security.has_permission('ManageJobs')));
EXCEPTION WHEN insufficient_privilege THEN RETURN false;
END $$;
CREATE FUNCTION automation.authorized(j automation.jobs) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d security.agent_delegations;k text;n timestamptz:=clock_timestamp();
BEGIN
 IF NOT security.principal_has_permission(j.owner_id,j.tenant_id,j.workspace_id,'RunJobs',n) THEN RETURN false;END IF;
 IF j.operation='swarm_job' AND(NOT security.principal_has_permission(j.owner_id,j.tenant_id,j.workspace_id,'LaunchWorkloads',n) OR NOT EXISTS(
 SELECT 1 FROM automation.execution_profiles p WHERE(p.id,p.version,p.tenant_id,p.workspace_id)=(j.profile_id,j.profile_version,j.tenant_id,j.workspace_id) AND p.enabled)) THEN RETURN false;END IF;
 SELECT kind INTO k FROM security.principals WHERE id=j.owner_id;
 IF k='agent' THEN
 SELECT * INTO d FROM security.agent_delegations WHERE id=j.delegation_id;
 RETURN d.id IS NOT NULL AND d.agent_id=j.owner_id AND(d.tenant_id,d.workspace_id)=(j.tenant_id,j.workspace_id) AND NOT d.revoked
 AND d.granted_from<=n AND n<d.granted_to AND security.principal_has_permission(d.delegator_id,j.tenant_id,j.workspace_id,'RunJobs',n)
 AND security.principal_has_permission(d.delegator_id,j.tenant_id,j.workspace_id,'CreateDelegations',n)
 AND EXISTS(SELECT 1 FROM security.delegation_permissions WHERE delegation_id=d.id AND permission='RunJobs')
 AND(j.operation<>'swarm_job' OR(security.principal_has_permission(d.delegator_id,j.tenant_id,j.workspace_id,'LaunchWorkloads',n)
 AND EXISTS(SELECT 1 FROM security.delegation_permissions WHERE delegation_id=d.id AND permission='LaunchWorkloads')));
 END IF;
 RETURN j.delegation_id IS NULL;
END $$;
CREATE FUNCTION automation.description(j automation.jobs) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',j.id,'name',j.name,'operation',j.operation,'state',j.state,'version',j.version,'dueAt',j.next_due_at,'expiresAt',j.expires_at,
 'intervalSeconds',j.interval_seconds,'owner',m.display_name,'mine',m.principal_id=(automation.actor()).actor_id,
 'actions',CASE WHEN j.state='active' THEN jsonb_build_array('pause','cancel') WHEN j.state='paused' THEN jsonb_build_array('resume','cancel')
 WHEN j.state IN('completed','blocked') AND j.expires_at>clock_timestamp() AND automation.authorized(j) AND NOT EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN CASE WHEN j.state='blocked' THEN jsonb_build_array('run_again','cancel') ELSE jsonb_build_array('run_again') END
 WHEN j.state='blocked' THEN jsonb_build_array('cancel') ELSE '[]'::jsonb END,
 'runs',coalesce((SELECT jsonb_agg(jsonb_build_object('id',r.id,'state',r.state,'startedAt',r.started_at,'finishedAt',r.finished_at,'attempt',r.attempt,'observation',r.observation_state,'errorCode',r.error_code) ORDER BY r.started_at DESC)
 FROM(SELECT * FROM automation.runs WHERE job_id=j.id ORDER BY started_at DESC LIMIT 20)r),'[]'::jsonb))
 FROM security.workspace_memberships m WHERE(m.tenant_id,m.workspace_id,m.principal_id)=(j.tenant_id,j.workspace_id,j.owner_id);
$$;
CREATE FUNCTION automation.job_list(page integer DEFAULT 0) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;
BEGIN
 c=automation.actor();
 IF page IS NULL OR page NOT BETWEEN 0 AND 10000 THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT jsonb_build_object('page',page,'total',(SELECT count(*) FROM automation.jobs WHERE automation.can_view(tenant_id,workspace_id,owner_id)),
 'jobs',coalesce((SELECT jsonb_agg(automation.description(j) ORDER BY j.created_at DESC,j.id) FROM
 (SELECT * FROM automation.jobs WHERE automation.can_view(tenant_id,workspace_id,owner_id) ORDER BY created_at DESC,id OFFSET page*100 LIMIT 100)j),'[]'::jsonb),
 'profiles',coalesce((SELECT jsonb_agg(jsonb_build_object('id',id,'version',version,'name',name)) FROM automation.execution_profiles
 WHERE(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id) AND enabled AND security.has_permission('LaunchWorkloads')),'[]'::jsonb),
 'canCreate',security.has_permission('RunJobs'),'manageAll',security.has_permission('ManageJobs') OR automation.operator('ManageAllJobs'),
 'observedAt',clock_timestamp(),'infrastructure',jsonb_build_object('state','not_configured','nodes','[]'::jsonb)) INTO result;
 RETURN result;
END $$;

CREATE FUNCTION automation.create_job(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz:=clock_timestamp();v_request_id uuid;response jsonb;
BEGIN
 c=automation.actor();IF NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(request)<>'object' OR request-ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']<>'{}'::jsonb
 OR NOT(request ?& ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']) THEN RAISE EXCEPTION 'job_input';END IF;
 v_request_id=(request->>'requestId')::uuid;
 -- Serialize an actor's commands/quotas. This is not a caller-chosen lock key.
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;
 c=automation.actor();n=clock_timestamp();
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,commands.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,v_request_id);
 IF FOUND THEN IF previous.request<>request THEN RAISE EXCEPTION 'job_conflict';END IF;RETURN previous.response;END IF;
 IF(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND(state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs pending_run WHERE pending_run.job_id=jobs.id AND pending_run.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
 j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;
 j.name=request->>'name';j.operation=request->>'operation';j.state='active';j.version=1;j.next_due_at=(request->>'dueAt')::timestamptz;j.expires_at=(request->>'expiresAt')::timestamptz;
 j.interval_seconds=(request->>'intervalSeconds')::integer;j.time_zone=request->>'timeZone';j.misfire=request->>'misfire';j.overlap_policy=request->>'overlap';
 j.timeout_seconds=(request->>'timeoutSeconds')::integer;j.maximum_attempts=(request->>'maximumAttempts')::integer;j.profile_id=(request->>'profileId')::uuid;j.profile_version=(request->>'profileVersion')::integer;j.created_at=n;j.last_reconciled_at='-infinity';
 IF j.expires_at<=n OR j.expires_at<=j.next_due_at OR j.expires_at>n+interval '366 days' OR j.next_due_at<n-interval '1 day' OR NOT automation.authorized(j)
 OR(j.operation='swarm_job' AND(NOT security.has_permission('LaunchWorkloads') OR j.maximum_attempts<>1)) THEN RAISE EXCEPTION 'job_input';END IF;
 INSERT INTO automation.jobs SELECT(j).*;
 response=automation.description(j);
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,v_request_id,j.id,request,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,'created',n);
 RETURN response;
END $$;
CREATE FUNCTION automation.control_job(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz:=clock_timestamp();response jsonb;v_request_id uuid;action text;
BEGIN
 c=automation.actor();
 IF jsonb_typeof(request)<>'object' OR request-ARRAY['requestId','id','action','expectedVersion']<>'{}'::jsonb OR NOT(request ?& ARRAY['requestId','id','action','expectedVersion']) THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT * INTO j FROM automation.jobs WHERE id=(request->>'id')::uuid;
 IF j.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.principals WHERE id IN(c.actor_id,j.owner_id) ORDER BY id FOR UPDATE;
 SELECT * INTO j FROM automation.jobs WHERE id=(request->>'id')::uuid FOR UPDATE;
 c=automation.actor();n=clock_timestamp();
 IF j.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 v_request_id=(request->>'requestId')::uuid;action=request->>'action';
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,commands.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,v_request_id);
 IF FOUND THEN IF previous.request<>request THEN RAISE EXCEPTION 'job_conflict';END IF;RETURN previous.response;END IF;
 IF j.version IS DISTINCT FROM(request->>'expectedVersion')::integer THEN RAISE EXCEPTION 'job_conflict';END IF;
 IF action='pause' AND j.state='active' THEN j.state='paused';
 ELSIF action='resume' AND j.state='paused' AND j.expires_at>n AND automation.authorized(j) THEN j.state='active';
 ELSIF action='run_again' AND j.state IN('completed','blocked') AND j.expires_at>n AND automation.authorized(j)
 AND NOT EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN
 IF(SELECT count(*) FROM automation.jobs q WHERE q.owner_id=j.owner_id AND q.id<>j.id AND(q.state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=q.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
 j.state='active';j.next_due_at=n;
 ELSIF action='cancel' AND j.state IN('active','paused','blocked') THEN
 j.state=CASE WHEN j.operation='swarm_job' AND EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN 'cancellation_requested' ELSE 'cancelled' END;
 UPDATE automation.runs SET state=CASE WHEN j.operation='worker_check' THEN 'cancelled' ELSE 'cancellation_requested' END,
 fence=fence+1,lease_until=n,finished_at=CASE WHEN j.operation='worker_check' THEN n ELSE NULL END WHERE job_id=j.id AND state='running';
 ELSE RAISE EXCEPTION 'job_transition';END IF;
 UPDATE automation.jobs SET state=j.state,next_due_at=j.next_due_at,version=version+1 WHERE id=j.id RETURNING * INTO j;
 response=automation.description(j);
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,v_request_id,j.id,request,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,action,n);
 RETURN response;
END $$;

CREATE FUNCTION automation.worker() RETURNS automation.workers
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 SELECT r.* INTO w FROM automation.workers r JOIN security.principals p ON p.id=r.principal_id AND p.kind='service' AND p.enabled WHERE r.login=session_user;
 IF w.login IS NULL OR NOT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,'RunJobs',clock_timestamp()) THEN RAISE EXCEPTION 'worker_denied' USING ERRCODE='42501';END IF;
 RETURN w;
END $$;
CREATE FUNCTION automation.claim_next(worker_id uuid,lease_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;response jsonb;
BEGIN
 w=automation.worker();n=clock_timestamp();
 IF worker_id IS NULL OR lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 120 THEN RAISE EXCEPTION 'job_input';END IF;
 FOR j IN SELECT * FROM automation.jobs WHERE(tenant_id,workspace_id)=(w.tenant_id,w.workspace_id)
 AND operation=CASE WHEN w.kind='scheduler' THEN 'worker_check' ELSE 'swarm_job' END
 AND((state='active' AND next_due_at<=n) OR(w.kind='controller' AND EXISTS(SELECT 1 FROM automation.runs pending_run WHERE pending_run.job_id=jobs.id AND pending_run.state IN('running','cancellation_requested'))))
 ORDER BY last_reconciled_at,next_due_at,id FOR UPDATE SKIP LOCKED LIMIT 32 LOOP
 n=clock_timestamp();
 UPDATE automation.jobs SET last_reconciled_at=n WHERE id=j.id;
 IF j.operation='swarm_job' THEN
 SELECT * INTO r FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at FOR UPDATE;
 n=clock_timestamp();w=automation.worker();
 IF r.id IS NOT NULL AND r.state IN('running','cancellation_requested') THEN
 IF r.lease_until>n AND(r.worker_id IS DISTINCT FROM claim_next.worker_id OR r.worker_login<>session_user) THEN CONTINUE;END IF;
 UPDATE automation.runs SET fence=CASE WHEN r.lease_until<=n OR r.worker_id IS DISTINCT FROM claim_next.worker_id THEN fence+1 ELSE fence END,
 worker_id=claim_next.worker_id,worker_login=session_user,lease_until=n+make_interval(secs=>lease_seconds) WHERE id=r.id RETURNING * INTO r;
 RETURN automation.workload_claim(j,r);
 END IF;
 IF EXISTS(SELECT 1 FROM automation.node_commands q JOIN automation.clusters s ON s.id=q.cluster_id WHERE(s.tenant_id,s.workspace_id)=(w.tenant_id,w.workspace_id) AND q.state IN('queued','running','unknown')) THEN CONTINUE;END IF;
 END IF;
 IF NOT automation.authorized(j) THEN
 UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='authority_revoked' WHERE job_id=j.id AND state='running' AND j.operation='worker_check';
 UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 IF j.expires_at<=n THEN
 UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='job_expired' WHERE job_id=j.id AND state='running' AND j.operation='worker_check';
 UPDATE automation.jobs SET state='expired',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 SELECT * INTO r FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at FOR UPDATE;
 n=clock_timestamp();w=automation.worker();
 IF r.id IS NOT NULL THEN
 IF r.state<>'running' OR r.lease_until>n THEN CONTINUE;END IF;
 IF j.operation='swarm_job' THEN CONTINUE;END IF; -- Reconcile physical work before replacement.
 IF r.attempt>=j.maximum_attempts OR r.deadline_at<=n THEN
 UPDATE automation.runs SET state='failed',finished_at=n,error_code='lease_expired',fence=fence+1 WHERE id=r.id;
 UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 UPDATE automation.runs SET fence=fence+1,attempt=attempt+1,worker_id=claim_next.worker_id,worker_login=session_user,lease_until=least(n+make_interval(secs=>lease_seconds),deadline_at,j.expires_at) WHERE id=r.id RETURNING * INTO r;
 ELSE
 INSERT INTO automation.runs(id,job_id,tenant_id,workspace_id,due_at,job_version,state,fence,attempt,worker_id,worker_login,lease_until,started_at,deadline_at,finished_at,error_code,service_id)
 VALUES(gen_random_uuid(),j.id,j.tenant_id,j.workspace_id,j.next_due_at,j.version,'running',1,1,worker_id,session_user,
 least(n+make_interval(secs=>lease_seconds),n+make_interval(secs=>j.timeout_seconds),j.expires_at),n,least(n+make_interval(secs=>j.timeout_seconds),j.expires_at),NULL,NULL,NULL) RETURNING * INTO r;
 END IF;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,'claimed',n,r.fence);
 IF j.operation='swarm_job' THEN RETURN automation.workload_claim(j,r);END IF;
 response=jsonb_build_object('id',r.id,'jobId',j.id,'operation',j.operation,'fence',r.fence,'workerId',r.worker_id,'leaseUntil',r.lease_until,'deadlineAt',r.deadline_at,
 'dueAt',r.due_at,'intervalSeconds',j.interval_seconds,
 'profile',(SELECT to_jsonb(p)-ARRAY['enabled','tenant_id','workspace_id'] FROM automation.execution_profiles p WHERE(p.id,p.version)=(j.profile_id,j.profile_version)));
 RETURN response;
 END LOOP;
 RETURN NULL;
END $$;
CREATE FUNCTION automation.finish_run(run_id uuid,expected_fence bigint,worker_id uuid,succeeded boolean,error_code text,next_due_at timestamptz) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;
BEGIN
 w=automation.worker();n=clock_timestamp();
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run_id FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run_id FOR UPDATE;
 w=automation.worker();n=clock_timestamp();
 IF r.id IS NULL OR(w.tenant_id,w.workspace_id)<>(r.tenant_id,r.workspace_id) OR r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM finish_run.worker_id
 OR r.fence IS DISTINCT FROM expected_fence OR r.lease_until<=n OR r.deadline_at<=n OR r.state<>'running' OR j.state NOT IN('active','paused') OR j.expires_at<=n OR NOT automation.authorized(j)
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF succeeded IS NULL OR(succeeded AND error_code IS NOT NULL) OR(NOT succeeded AND(error_code IS NULL OR error_code!~'^[a-z][a-z0-9_]{0,63}$')) THEN RAISE EXCEPTION 'job_input';END IF;
 IF succeeded AND j.interval_seconds IS NOT NULL AND(next_due_at IS NULL OR NOT isfinite(next_due_at)
 OR next_due_at<=n OR next_due_at>n+make_interval(secs=>j.interval_seconds)
 OR mod(extract(epoch FROM(next_due_at-j.next_due_at)),j.interval_seconds)<>0) THEN RAISE EXCEPTION 'job_clock';END IF;
 IF j.interval_seconds IS NULL AND next_due_at IS NOT NULL THEN RAISE EXCEPTION 'job_input';END IF;
 UPDATE automation.runs SET state=CASE WHEN succeeded THEN 'succeeded' ELSE 'failed' END,finished_at=n,error_code=finish_run.error_code WHERE id=run_id;
 IF NOT succeeded THEN UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;
 ELSIF j.interval_seconds IS NULL THEN UPDATE automation.jobs SET state='completed',version=version+1 WHERE id=j.id;
 ELSE UPDATE automation.jobs SET next_due_at=finish_run.next_due_at,version=version+1 WHERE id=j.id;END IF;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,CASE WHEN succeeded THEN 'succeeded' ELSE 'failed' END,n,r.fence);
END $$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='automation' LOOP
 EXECUTE format('ALTER TABLE automation.%I OWNER TO openetrm_security_owner',t.tablename);
 EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t.tablename);
 EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='automation' LOOP
 EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
 EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
CREATE POLICY job_read ON automation.jobs FOR SELECT TO openetrm_runtime USING(automation.can_view(tenant_id,workspace_id,owner_id));
CREATE POLICY run_read ON automation.runs FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM automation.jobs j WHERE j.id=job_id));
CREATE POLICY command_read ON automation.commands FOR SELECT TO openetrm_runtime USING(actor_id=(automation.actor()).actor_id AND(tenant_id,workspace_id)=((automation.actor()).tenant_id,(automation.actor()).workspace_id));
GRANT USAGE ON SCHEMA automation TO openetrm_runtime,openetrm_scheduler;
GRANT SELECT ON automation.jobs,automation.runs TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION automation.can_view(uuid,uuid,uuid),automation.job_list(integer),automation.create_job(jsonb),automation.control_job(jsonb) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION automation.claim_next(uuid,integer),automation.finish_run(uuid,bigint,uuid,boolean,text,timestamptz) TO openetrm_scheduler;
COMMIT;
