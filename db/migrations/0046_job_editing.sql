-- Amend supported operational schedules without changing execution identity or active runs.
BEGIN;
CREATE FUNCTION automation.edit_reason(j automation.jobs) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF j.state NOT IN('active','paused') THEN RETURN 'This job is terminal or blocked; its retained terms are read-only.';END IF;
 IF j.expires_at<=clock_timestamp() THEN RETURN 'This job has expired; its retained terms are read-only.';END IF;
 IF EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND(state IN('running','cancellation_requested') OR(launch_prepared AND NOT service_removed)))
 THEN RETURN 'A run is executing or awaiting cleanup. Refresh after it finishes before editing.';END IF;
 IF NOT automation.authorized(j) THEN RETURN 'The owner no longer has current execution authority for this job.';END IF;
 RETURN NULL;
END $$;
CREATE OR REPLACE FUNCTION automation.description(j automation.jobs) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',j.id,'name',j.name,'operation',j.operation,'state',j.state,'version',j.version,'dueAt',j.next_due_at,'expiresAt',j.expires_at,
 'intervalSeconds',j.interval_seconds,'timeZone',j.time_zone,'misfire',j.misfire,'overlap',j.overlap_policy,
 'timeoutSeconds',j.timeout_seconds,'maximumAttempts',j.maximum_attempts,'profileId',j.profile_id,'profileVersion',j.profile_version,
 'profileName',(SELECT name FROM automation.execution_profiles WHERE(id,version)=(j.profile_id,j.profile_version)),
 'canEdit',automation.edit_reason(j) IS NULL,'editReason',automation.edit_reason(j),'owner',m.display_name,'mine',m.principal_id=(automation.actor()).actor_id,
 'actions',CASE WHEN j.state='active' THEN jsonb_build_array('pause','cancel') WHEN j.state='paused' THEN jsonb_build_array('resume','cancel')
 WHEN j.state IN('completed','blocked') AND j.expires_at>clock_timestamp() AND automation.authorized(j) AND NOT EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN CASE WHEN j.state='blocked' THEN jsonb_build_array('run_again','cancel') ELSE jsonb_build_array('run_again') END
 WHEN j.state='blocked' THEN jsonb_build_array('cancel') ELSE '[]'::jsonb END,
 'runs',coalesce((SELECT jsonb_agg(jsonb_build_object('id',r.id,'state',r.state,'startedAt',r.started_at,'finishedAt',r.finished_at,'attempt',r.attempt,'observation',r.observation_state,'errorCode',r.error_code) ORDER BY r.started_at DESC)
 FROM(SELECT * FROM automation.runs WHERE job_id=j.id ORDER BY started_at DESC LIMIT 20)r),'[]'::jsonb))
 FROM security.workspace_memberships m WHERE(m.tenant_id,m.workspace_id,m.principal_id)=(j.tenant_id,j.workspace_id,j.owner_id);
$$;

CREATE FUNCTION automation.update_job(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz;response jsonb;v_request_id uuid;
BEGIN
 c=automation.actor();
 IF jsonb_typeof(request) IS DISTINCT FROM 'object'
 OR request-ARRAY['requestId','id','expectedVersion','name','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts']<>'{}'::jsonb
 OR NOT(request ?& ARRAY['requestId','id','expectedVersion','name','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts']) THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT * INTO j FROM automation.jobs WHERE id=(request->>'id')::uuid;
 IF j.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 -- Same lock order as control/create: capacity and run admission cannot race an amendment.
 PERFORM 1 FROM security.principals WHERE id IN(c.actor_id,j.owner_id) ORDER BY id FOR UPDATE;
 SELECT * INTO j FROM automation.jobs WHERE id=(request->>'id')::uuid FOR UPDATE;
 c=automation.actor();n=clock_timestamp();
 IF j.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) OR NOT automation.authorized(j)
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 v_request_id=(request->>'requestId')::uuid;
 IF v_request_id IS NULL OR v_request_id='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,commands.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,v_request_id);
 IF FOUND THEN IF previous.request<>request THEN RAISE EXCEPTION 'job_conflict';END IF;RETURN previous.response;END IF;
 IF j.version IS DISTINCT FROM(request->>'expectedVersion')::integer THEN RAISE EXCEPTION 'job_conflict';END IF;
 IF automation.edit_reason(j) IS NOT NULL THEN RAISE EXCEPTION 'job_transition';END IF;
 j.name=request->>'name';j.next_due_at=(request->>'dueAt')::timestamptz;j.expires_at=(request->>'expiresAt')::timestamptz;
 j.interval_seconds=(request->>'intervalSeconds')::integer;j.time_zone=request->>'timeZone';j.misfire=request->>'misfire';j.overlap_policy=request->>'overlap';
 j.timeout_seconds=(request->>'timeoutSeconds')::integer;j.maximum_attempts=(request->>'maximumAttempts')::integer;
 IF j.name IS NULL OR length(btrim(j.name)) NOT BETWEEN 1 AND 80 OR j.next_due_at IS NULL OR NOT isfinite(j.next_due_at)
 OR j.expires_at IS NULL OR NOT isfinite(j.expires_at) OR j.expires_at<=n OR j.expires_at<=j.next_due_at OR j.expires_at>n+interval '366 days'
 OR j.next_due_at<n-interval '1 day' OR j.time_zone IS DISTINCT FROM 'UTC' OR j.misfire IS DISTINCT FROM 'run_once' OR j.overlap_policy IS DISTINCT FROM 'forbid'
 OR(j.interval_seconds IS NOT NULL AND j.interval_seconds NOT BETWEEN 60 AND 31536000)
 OR j.timeout_seconds IS NULL OR j.timeout_seconds NOT BETWEEN 1 AND 3600 OR j.maximum_attempts IS NULL OR j.maximum_attempts NOT BETWEEN 1 AND 3
 OR(j.operation='swarm_job' AND j.maximum_attempts<>1) THEN RAISE EXCEPTION 'job_input';END IF;
 IF EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at) THEN RAISE EXCEPTION 'job_input';END IF;
 UPDATE automation.jobs SET name=j.name,next_due_at=j.next_due_at,expires_at=j.expires_at,interval_seconds=j.interval_seconds,
 time_zone=j.time_zone,misfire=j.misfire,overlap_policy=j.overlap_policy,timeout_seconds=j.timeout_seconds,maximum_attempts=j.maximum_attempts,version=version+1
 WHERE id=j.id RETURNING * INTO j;
 c=automation.actor();
 IF NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) OR NOT automation.authorized(j)
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 response=automation.description(j);
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,v_request_id,j.id,request,response,clock_timestamp());
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,'updated',clock_timestamp());
 RETURN response;
END $$;
ALTER FUNCTION automation.edit_reason(automation.jobs) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.update_job(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.edit_reason(automation.jobs),automation.update_job(jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.update_job(jsonb) TO openetrm_runtime;
COMMIT;
