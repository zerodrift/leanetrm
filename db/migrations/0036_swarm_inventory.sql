-- Registered infrastructure and typed availability commands, never raw Docker.
BEGIN;
CREATE TABLE automation.clusters(
 id text PRIMARY KEY CHECK(id~'^[a-z0-9]{12,64}$'),name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),
 controller_login name NOT NULL UNIQUE REFERENCES automation.workers(login),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,enabled boolean NOT NULL,
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id),UNIQUE(tenant_id,workspace_id)
);
CREATE TABLE automation.nodes(
 cluster_id text NOT NULL REFERENCES automation.clusters(id),id text NOT NULL CHECK(id~'^[a-z0-9]{12,64}$'),version bigint NOT NULL CHECK(version>=0),
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80),role text NOT NULL CHECK(role IN('worker','manager')),state text NOT NULL,
 availability text NOT NULL CHECK(availability IN('active','pause','drain')),cpu_nano bigint NOT NULL CHECK(cpu_nano>=0),memory_bytes bigint NOT NULL CHECK(memory_bytes>=0),
 workload_node boolean NOT NULL,observed_at timestamptz NOT NULL,PRIMARY KEY(cluster_id,id)
);
CREATE TABLE automation.node_commands(
 id uuid PRIMARY KEY,cluster_id text NOT NULL REFERENCES automation.clusters(id),node_id text NOT NULL,node_version bigint NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),delegation_id uuid REFERENCES security.agent_delegations(id),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 request_id uuid NOT NULL,availability text NOT NULL CHECK(availability IN('active','pause','drain')),
 state text NOT NULL CHECK(state IN('queued','running','applied','failed','unknown','reconciled')),created_at timestamptz NOT NULL,
 worker_id uuid,fence bigint NOT NULL,lease_until timestamptz,error_code text,
 dispatch_admitted_at timestamptz,resolution_actor_id uuid REFERENCES security.principals(id),effective_version bigint,effective_availability text,
 UNIQUE(actor_id,request_id)
);
CREATE TABLE automation.node_resolutions(
 request_id uuid PRIMARY KEY,command_id uuid NOT NULL REFERENCES automation.node_commands(id),actor_id uuid NOT NULL REFERENCES security.principals(id),
 node_version bigint NOT NULL,availability text NOT NULL,requested_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX one_cluster_maintenance ON automation.node_commands(cluster_id) WHERE state IN('queued','running','unknown');
CREATE TRIGGER guard_control_change BEFORE INSERT OR UPDATE OR DELETE ON automation.clusters FOR EACH STATEMENT EXECUTE FUNCTION security.guard_control_change();

CREATE FUNCTION automation.controller(cluster_id text) RETURNS automation.workers
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;
BEGIN
 w=automation.worker();
 IF w.kind<>'controller' OR NOT EXISTS(SELECT 1 FROM automation.clusters c WHERE c.id=cluster_id AND c.controller_login=session_user AND(c.tenant_id,c.workspace_id)=(w.tenant_id,w.workspace_id) AND c.enabled)
 THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 RETURN w;
END $$;
CREATE FUNCTION automation.infrastructure_allowed(actor uuid,delegation uuid,tenant uuid,workspace uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE k text;d security.agent_delegations;n timestamptz:=clock_timestamp();
BEGIN
 SELECT p.kind INTO k FROM security.principals p JOIN security.workspace_memberships m ON m.principal_id=p.id AND(m.tenant_id,m.workspace_id)=(tenant,workspace) WHERE p.id=actor AND p.enabled AND m.enabled;
 IF k IS NULL THEN RETURN false;END IF;
 IF k IN('human','service') AND delegation IS NULL AND security.principal_has_permission(actor,tenant,workspace,'ManageInfrastructure',n) THEN RETURN EXISTS(SELECT 1 FROM automation.installation_operators WHERE principal_id=actor AND capability='ManageInfrastructure' AND expires_at>n);END IF;
 -- Agent infrastructure delegation remains unsupported until its full
 -- cluster-wide mandate and approval contract is qualified.
 RETURN false;
END $$;
CREATE FUNCTION automation.infrastructure_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;
BEGIN
 c=automation.actor();
 SELECT jsonb_build_object('state',CASE WHEN EXISTS(SELECT 1 FROM automation.clusters WHERE(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id) AND enabled) THEN 'configured' ELSE 'not_configured' END,
 'observedAt',(SELECT max(nd.observed_at) FROM automation.nodes nd JOIN automation.clusters cl ON cl.id=nd.cluster_id WHERE(cl.tenant_id,cl.workspace_id)=(c.tenant_id,c.workspace_id) AND cl.enabled),
 'nodes',coalesce((SELECT jsonb_agg(jsonb_build_object('clusterId',n.cluster_id,'id',n.id,'version',n.version,'name',n.name,'role',n.role,'state',n.state,'availability',n.availability,
 'cpuNano',n.cpu_nano,'memoryBytes',n.memory_bytes,'observedAt',n.observed_at,'stale',n.observed_at<clock_timestamp()-interval '30 seconds',
 'actions',CASE WHEN n.role='worker' AND n.workload_node AND n.state='ready' AND n.observed_at>=clock_timestamp()-interval '30 seconds'
 AND automation.infrastructure_allowed(c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id) AND NOT EXISTS(SELECT 1 FROM automation.node_commands q WHERE q.cluster_id=n.cluster_id AND q.state IN('queued','running','unknown')) AND NOT EXISTS(SELECT 1 FROM automation.runs r WHERE(r.tenant_id,r.workspace_id)=(c.tenant_id,c.workspace_id) AND r.launch_prepared AND NOT r.service_removed) THEN jsonb_build_array('pause','active','drain') ELSE '[]'::jsonb END) ORDER BY n.name,n.id)
 FROM automation.nodes n JOIN automation.clusters s ON s.id=n.cluster_id WHERE(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id) AND s.enabled),'[]'::jsonb),
 'commands',coalesce((SELECT jsonb_agg(jsonb_build_object('id',x.id,'nodeId',x.node_id,'availability',x.availability,'state',x.state,'errorCode',x.error_code,'createdAt',x.created_at,
 'canResolve',x.state='unknown' AND automation.infrastructure_allowed(c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id)) ORDER BY x.created_at DESC)
 FROM(SELECT * FROM automation.node_commands WHERE(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id) AND(actor_id=c.actor_id OR automation.infrastructure_allowed(c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id)) ORDER BY created_at DESC LIMIT 50)x),'[]'::jsonb)) INTO result;
 RETURN result;
END $$;
CREATE FUNCTION automation.observe_nodes(cluster_id text,nodes jsonb) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;x jsonb;n timestamptz;
BEGIN
 w=automation.controller(cluster_id);n=clock_timestamp();
 IF jsonb_typeof(nodes)<>'array' OR jsonb_array_length(nodes)>1024 THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 DELETE FROM automation.nodes WHERE nodes.cluster_id=observe_nodes.cluster_id;
 FOR x IN SELECT value FROM jsonb_array_elements(nodes) LOOP
 INSERT INTO automation.nodes VALUES(cluster_id,x->>'id',(x->>'version')::bigint,x->>'name',x->>'role',x->>'state',x->>'availability',
 (x->>'cpuNano')::bigint,(x->>'memoryBytes')::bigint,(x->>'workloadNode')::boolean,n);
 END LOOP;
END $$;
CREATE FUNCTION automation.request_node_command(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;node automation.nodes;previous automation.node_commands;x automation.node_commands;n timestamptz;
BEGIN
 c=automation.actor();
 IF jsonb_typeof(request)<>'object' OR request-ARRAY['requestId','clusterId','nodeId','expectedVersion','availability']<>'{}'::jsonb
 OR NOT(request ?& ARRAY['requestId','clusterId','nodeId','expectedVersion','availability']) THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;
 c=automation.actor();n=clock_timestamp();
 IF NOT automation.infrastructure_allowed(c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO previous FROM automation.node_commands WHERE actor_id=c.actor_id AND request_id=(request->>'requestId')::uuid;
 IF FOUND THEN
 IF(previous.cluster_id,previous.node_id,previous.node_version,previous.availability) IS DISTINCT FROM(request->>'clusterId',request->>'nodeId',(request->>'expectedVersion')::bigint,request->>'availability') THEN RAISE EXCEPTION 'infrastructure_conflict';END IF;
 RETURN to_jsonb(previous)-ARRAY['actor_id','delegation_id','tenant_id','workspace_id','worker_id'];END IF;
 SELECT nd.* INTO node FROM automation.nodes nd JOIN automation.clusters s ON s.id=nd.cluster_id
 WHERE(nd.cluster_id,nd.id)=(request->>'clusterId',request->>'nodeId') AND(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id) AND s.enabled FOR UPDATE OF s;
 c=automation.actor();n=clock_timestamp();
 IF NOT automation.infrastructure_allowed(c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM automation.runs r WHERE(r.tenant_id,r.workspace_id)=(c.tenant_id,c.workspace_id) AND r.launch_prepared AND NOT r.service_removed) THEN RAISE EXCEPTION 'infrastructure_busy';END IF;
 IF node.id IS NULL OR node.version IS DISTINCT FROM(request->>'expectedVersion')::bigint OR node.observed_at<n-interval '30 seconds' OR node.role<>'worker' OR NOT node.workload_node OR node.state<>'ready' THEN RAISE EXCEPTION 'infrastructure_conflict';END IF;
 INSERT INTO automation.node_commands(id,cluster_id,node_id,node_version,actor_id,delegation_id,tenant_id,workspace_id,request_id,availability,state,created_at,worker_id,fence,lease_until,error_code)
 VALUES(gen_random_uuid(),node.cluster_id,node.id,node.version,c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id,(request->>'requestId')::uuid,request->>'availability','queued',n,NULL,1,NULL,NULL) RETURNING * INTO x;
 RETURN to_jsonb(x)-ARRAY['actor_id','delegation_id','tenant_id','workspace_id','worker_id'];
END $$;
CREATE FUNCTION automation.claim_node_command(cluster_id text,worker_id uuid,lease_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;x automation.node_commands;n timestamptz;
BEGIN
 w=automation.controller(cluster_id);
 IF lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 120 OR worker_id IS NULL THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 SELECT * INTO x FROM automation.node_commands WHERE node_commands.cluster_id=claim_node_command.cluster_id AND(state='queued' OR(state='running' AND lease_until<=clock_timestamp())) FOR UPDATE SKIP LOCKED;
 IF x.id IS NULL THEN RETURN NULL;END IF;
 w=automation.controller(cluster_id);n=clock_timestamp();
 IF x.state='running' THEN UPDATE automation.node_commands SET state='unknown',error_code='outcome_unknown' WHERE id=x.id;RETURN NULL;END IF;
 IF NOT automation.infrastructure_allowed(coalesce(x.resolution_actor_id,x.actor_id),CASE WHEN x.resolution_actor_id IS NULL THEN x.delegation_id ELSE NULL END,x.tenant_id,x.workspace_id) THEN UPDATE automation.node_commands SET state=CASE WHEN x.resolution_actor_id IS NULL THEN 'failed' ELSE 'unknown' END,error_code='authority_revoked' WHERE id=x.id;RETURN NULL;END IF;
 UPDATE automation.node_commands SET state='running',worker_id=claim_node_command.worker_id,fence=fence+1,lease_until=n+make_interval(secs=>lease_seconds) WHERE id=x.id RETURNING * INTO x;
 RETURN jsonb_build_object('id',x.id,'nodeId',x.node_id,'expectedVersion',coalesce(x.effective_version,x.node_version),'availability',coalesce(x.effective_availability,x.availability),'fence',x.fence,'workerId',x.worker_id,'leaseUntil',x.lease_until,'isResolution',x.resolution_actor_id IS NOT NULL);
END $$;
CREATE FUNCTION automation.complete_node_command(command_id uuid,worker_id uuid,expected_fence bigint,outcome text,error_code text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE x automation.node_commands;w automation.workers;n timestamptz;
BEGIN
 w=automation.worker();
 SELECT * INTO x FROM automation.node_commands WHERE id=command_id FOR UPDATE;
 w=automation.controller(x.cluster_id);n=clock_timestamp();
 IF x.worker_id IS DISTINCT FROM complete_node_command.worker_id OR x.fence IS DISTINCT FROM expected_fence OR x.state<>'running' OR x.lease_until<=n OR(x.dispatch_admitted_at IS NULL AND outcome IS DISTINCT FROM 'failed') THEN RAISE EXCEPTION 'infrastructure_fence' USING ERRCODE='42501';END IF;
 IF outcome IS NULL OR outcome NOT IN('applied','failed','unknown') OR(error_code IS NOT NULL AND error_code!~'^[a-z][a-z0-9_]{0,63}$') THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 UPDATE automation.node_commands SET state=CASE WHEN x.resolution_actor_id IS NOT NULL THEN CASE WHEN outcome='applied' THEN 'reconciled' ELSE 'unknown' END ELSE outcome END,error_code=complete_node_command.error_code WHERE id=command_id;
END $$;
CREATE FUNCTION automation.worker_kind() RETURNS text LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$SELECT(automation.worker()).kind$$;
CREATE FUNCTION automation.workload_claim(j automation.jobs,r automation.runs) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',r.id,'jobId',j.id,'operation',j.operation,'fence',r.fence,'workerId',r.worker_id,'leaseUntil',r.lease_until,'deadlineAt',r.deadline_at,
 'dueAt',r.due_at,'intervalSeconds',j.interval_seconds,'serviceId',r.service_id,'launchPrepared',r.launch_prepared,
 'mustStop',j.state NOT IN('active','paused') OR r.state='cancellation_requested' OR r.deadline_at<=clock_timestamp() OR j.expires_at<=clock_timestamp() OR NOT automation.authorized(j),
 'profile',(SELECT to_jsonb(p)-ARRAY['enabled','tenant_id','workspace_id'] FROM automation.execution_profiles p WHERE(p.id,p.version)=(j.profile_id,j.profile_version)));
$$;
CREATE FUNCTION automation.authorize_workload(run_id uuid,worker_id uuid,expected_fence bigint,stopping boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;cluster text;
BEGIN
 w=automation.worker();
 SELECT id INTO cluster FROM automation.clusters WHERE(tenant_id,workspace_id)=(w.tenant_id,w.workspace_id) AND controller_login=session_user FOR UPDATE;
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run_id FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run_id FOR UPDATE;
 w=automation.controller(cluster);n=clock_timestamp();
 IF r.id IS NULL OR(j.tenant_id,j.workspace_id)<>(w.tenant_id,w.workspace_id) OR r.worker_id IS DISTINCT FROM authorize_workload.worker_id OR r.worker_login<>session_user OR r.fence IS DISTINCT FROM expected_fence
 OR r.lease_until<=n OR r.state NOT IN('running','cancellation_requested') OR stopping IS NULL THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF NOT stopping THEN
 IF r.launch_prepared THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF j.state NOT IN('active','paused') OR r.state<>'running' OR r.deadline_at<=n OR j.expires_at<=n OR NOT automation.authorized(j)
 OR EXISTS(SELECT 1 FROM automation.node_commands WHERE cluster_id=cluster AND state IN('queued','running','unknown')) THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF NOT r.launch_prepared THEN
 UPDATE automation.runs SET launch_prepared=true WHERE id=r.id;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,'launch_admitted',n,r.fence);
 END IF;
 END IF;
END $$;
CREATE FUNCTION automation.record_service(run_id uuid,worker_id uuid,expected_fence bigint,service_id text,observation text DEFAULT 'running') RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;r automation.runs;n timestamptz;
BEGIN
 w=automation.worker();SELECT * INTO r FROM automation.runs WHERE id=run_id FOR UPDATE;w=automation.worker();n=clock_timestamp();
 IF w.kind<>'controller' OR r.id IS NULL OR(r.tenant_id,r.workspace_id)<>(w.tenant_id,w.workspace_id) OR r.worker_login<>session_user
 OR r.worker_id IS DISTINCT FROM record_service.worker_id OR r.fence IS DISTINCT FROM expected_fence OR r.lease_until<=n OR r.state NOT IN('running','cancellation_requested')
 OR(service_id IS NOT NULL AND(service_id!~'^[a-z0-9]{12,64}$' OR NOT r.launch_prepared)) OR(r.service_id IS NOT NULL AND service_id IS NOT NULL AND r.service_id<>record_service.service_id) OR observation IS NULL OR observation NOT IN('pending','unknown','stopping','running','completed','failed','stopped') THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 UPDATE automation.runs SET service_id=coalesce(record_service.service_id,r.service_id),observation_state=observation WHERE id=run_id;
END $$;
CREATE FUNCTION automation.stop_workload(run_id uuid,worker_id uuid,expected_fence bigint) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;
BEGIN
 w=automation.worker();
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run_id FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run_id FOR UPDATE;w=automation.worker();n=clock_timestamp();
 IF w.kind<>'controller' OR r.id IS NULL OR(r.tenant_id,r.workspace_id)<>(w.tenant_id,w.workspace_id) OR r.worker_login<>session_user
 OR r.worker_id IS DISTINCT FROM stop_workload.worker_id OR r.fence IS DISTINCT FROM expected_fence OR r.lease_until<=n OR r.state NOT IN('running','cancellation_requested')
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 UPDATE automation.runs SET state=CASE WHEN j.state IN('cancelled','cancellation_requested') THEN 'cancelled' ELSE 'failed' END,finished_at=n,error_code=CASE WHEN j.state IN('cancelled','cancellation_requested') THEN NULL ELSE 'workload_stopped' END,service_removed=true WHERE id=run_id;
 UPDATE automation.jobs SET state=CASE WHEN state IN('cancelled','cancellation_requested') THEN 'cancelled' ELSE 'blocked' END,version=version+1 WHERE id=j.id;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,'termination_confirmed',n,r.fence);
END $$;
CREATE FUNCTION automation.cleanup_workload(run_id uuid,service_id text,completed boolean) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;
BEGIN
 w=automation.worker();IF w.kind<>'controller' THEN RAISE EXCEPTION 'worker_denied' USING ERRCODE='42501';END IF;
 IF completed IS NULL THEN RAISE EXCEPTION 'job_input';END IF;
 IF run_id IS NULL AND NOT completed THEN
 SELECT runs.* INTO r FROM automation.runs runs WHERE(runs.tenant_id,runs.workspace_id)=(w.tenant_id,w.workspace_id) AND state IN('succeeded','failed','cancelled') AND runs.service_id IS NOT NULL AND NOT service_removed ORDER BY cleanup_checked_at,finished_at,id LIMIT 1 FOR UPDATE SKIP LOCKED;
 w=automation.worker();
 IF r.id IS NOT NULL THEN UPDATE automation.runs SET cleanup_checked_at=clock_timestamp() WHERE id=r.id;END IF;
 ELSE
 SELECT * INTO r FROM automation.runs WHERE id=run_id FOR UPDATE;
 w=automation.worker();
 IF r.id IS NULL OR(r.tenant_id,r.workspace_id)<>(w.tenant_id,w.workspace_id) OR r.state NOT IN('succeeded','failed','cancelled') OR r.service_id IS DISTINCT FROM cleanup_workload.service_id OR NOT completed THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 UPDATE automation.runs SET service_removed=true WHERE id=r.id;RETURN NULL;
 END IF;
 IF r.id IS NULL THEN RETURN NULL;END IF;
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 RETURN automation.workload_claim(j,r);
END $$;
CREATE FUNCTION automation.authorize_node_command(command_id uuid,worker_id uuid,expected_fence bigint) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE x automation.node_commands;w automation.workers;n timestamptz;
BEGIN
 w=automation.worker();
 SELECT * INTO x FROM automation.node_commands WHERE id=command_id FOR UPDATE;
 w=automation.controller(x.cluster_id);n=clock_timestamp();
 IF x.state<>'running' OR x.worker_id IS DISTINCT FROM authorize_node_command.worker_id OR x.fence IS DISTINCT FROM expected_fence OR x.lease_until<=n
 OR x.dispatch_admitted_at IS NOT NULL OR NOT automation.infrastructure_allowed(coalesce(x.resolution_actor_id,x.actor_id),CASE WHEN x.resolution_actor_id IS NULL THEN x.delegation_id ELSE NULL END,x.tenant_id,x.workspace_id) THEN RAISE EXCEPTION 'infrastructure_fence' USING ERRCODE='42501';END IF;
 -- The committed dispatch admission is serialized with revocation. Submission
 -- of this exact version-fenced command may finish later; it is never replayed.
 UPDATE automation.node_commands SET dispatch_admitted_at=n WHERE id=x.id;
END $$;
CREATE FUNCTION automation.resolve_node_command(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;x automation.node_commands;node automation.nodes;previous automation.node_resolutions;n timestamptz;
BEGIN
 c=automation.actor();
 IF jsonb_typeof(request)<>'object' OR request-ARRAY['requestId','id','expectedVersion']<>'{}'::jsonb OR NOT(request ?& ARRAY['requestId','id','expectedVersion']) THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 SELECT * INTO x FROM automation.node_commands WHERE id=(request->>'id')::uuid FOR UPDATE;
 c=automation.actor();n=clock_timestamp();
 IF x.id IS NULL OR(x.tenant_id,x.workspace_id)<>(c.tenant_id,c.workspace_id) OR NOT automation.infrastructure_allowed(c.actor_id,c.delegation_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO previous FROM automation.node_resolutions WHERE request_id=(request->>'requestId')::uuid;
 IF FOUND THEN IF(previous.actor_id,previous.command_id,previous.node_version) IS DISTINCT FROM(c.actor_id,x.id,(request->>'expectedVersion')::bigint) THEN RAISE EXCEPTION 'infrastructure_conflict';END IF;RETURN jsonb_build_object('id',x.id,'state',x.state);END IF;
 SELECT * INTO node FROM automation.nodes WHERE(cluster_id,id)=(x.cluster_id,x.node_id);
 IF x.state<>'unknown' OR node.id IS NULL OR node.version IS DISTINCT FROM(request->>'expectedVersion')::bigint OR node.observed_at<n-interval '30 seconds' OR node.state<>'ready' THEN RAISE EXCEPTION 'infrastructure_conflict';END IF;
 INSERT INTO automation.node_resolutions VALUES((request->>'requestId')::uuid,x.id,c.actor_id,node.version,node.availability,n);
 -- Preserve original request provenance. A new conditional update preserves
 -- observed availability and advances Docker's node version, fencing all late
 -- submissions with the original expected version before maintenance resumes.
 UPDATE automation.node_commands SET state='queued',resolution_actor_id=c.actor_id,effective_version=node.version,effective_availability=node.availability,dispatch_admitted_at=NULL,worker_id=NULL,lease_until=NULL,fence=fence+1,error_code=NULL WHERE id=x.id;
 RETURN jsonb_build_object('id',x.id,'state','queued');
END $$;

DO $$DECLARE t text;f record;BEGIN
 FOREACH t IN ARRAY ARRAY['clusters','nodes','node_commands','node_resolutions'] LOOP
 EXECUTE format('ALTER TABLE automation.%I OWNER TO openetrm_security_owner',t);
 EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t);
 EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='automation' LOOP
 EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION automation.infrastructure_directory(),automation.request_node_command(jsonb) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION automation.resolve_node_command(jsonb) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION automation.worker_kind(),automation.observe_nodes(text,jsonb),automation.claim_node_command(text,uuid,integer),automation.complete_node_command(uuid,uuid,bigint,text,text) TO openetrm_scheduler;
GRANT EXECUTE ON FUNCTION automation.authorize_node_command(uuid,uuid,bigint) TO openetrm_scheduler;
GRANT EXECUTE ON FUNCTION automation.authorize_workload(uuid,uuid,bigint,boolean),automation.record_service(uuid,uuid,bigint,text,text),automation.stop_workload(uuid,uuid,bigint),automation.cleanup_workload(uuid,text,boolean) TO openetrm_scheduler;
COMMIT;
