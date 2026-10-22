-- Renewable ownership for read-only existing-close risk only. Deadlines,
-- admission pins and legacy worker contracts remain unchanged.
BEGIN;
CREATE FUNCTION automation.renew_close_risk_lease(run uuid,expected_fence bigint,worker uuid,lease_seconds integer) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;until_at timestamptz;
BEGIN
 w=automation.worker();
 IF lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 120 THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run FOR UPDATE;
 w=automation.worker();n=clock_timestamp();
 IF r.id IS NULL OR j.operation<>'close_book_risk_v1' OR w.kind<>'scheduler'
 OR r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM worker OR r.fence IS DISTINCT FROM expected_fence
 OR(w.tenant_id,w.workspace_id)<>(r.tenant_id,r.workspace_id) OR r.state<>'running' OR j.state NOT IN('active','paused')
 OR r.lease_until<=n OR r.deadline_at<=n OR j.expires_at<=n
 OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,w.principal_id)
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 until_at=least(greatest(r.lease_until,n+make_interval(secs=>lease_seconds)),r.deadline_at,j.expires_at);
 UPDATE automation.runs SET lease_until=until_at WHERE id=r.id;
 RETURN until_at;
END;$$;
-- No job/run locks during preparation. Every business read still checks the
-- live claim and current authority; binding holds only the authorization fence.
CREATE FUNCTION automation.check_close_risk_run() RETURNS automation.runs
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;r automation.runs;
BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF q.id IS NULL OR q.expires_at<=clock_timestamp() OR NOT automation.pilot_context_live(q.id) OR NOT security.has_permission('RunJobs')
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 SELECT runs.* INTO r FROM automation.runs runs JOIN automation.pilot_run_contexts p ON p.run_id=runs.id JOIN automation.jobs j ON j.id=runs.job_id
 WHERE p.context_id=q.id AND j.operation='close_book_risk_v1';
 IF r.id IS NULL THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 RETURN r;
END;$$;
CREATE OR REPLACE FUNCTION automation.issue_pilot_context(run uuid,expected_fence bigint,worker uuid) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;login name;token uuid;role record;
BEGIN
 w=automation.worker();
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run FOR UPDATE;
 SELECT runtime_login INTO login FROM automation.pilot_runtime_logins WHERE worker_login=session_user;
 SELECT * INTO role FROM pg_roles WHERE rolname=login;
 IF login IS NULL OR role.oid IS NULL THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF role.rolsuper OR role.rolbypassrls OR role.rolcreatedb OR role.rolcreaterole OR NOT role.rolcanlogin
 OR NOT pg_has_role(login,'openetrm_runtime','MEMBER') OR pg_has_role(login,'openetrm_authenticator','MEMBER')
 OR pg_has_role(login,'openetrm_security_owner','MEMBER') OR pg_has_role(login,'openetrm_scheduler','MEMBER')
 OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace ns ON ns.oid=c.relnamespace WHERE ns.nspname NOT LIKE 'pg_%' AND ns.nspname<>'information_schema' AND pg_has_role(login,c.relowner,'MEMBER'))
 OR EXISTS(SELECT 1 FROM pg_namespace ns WHERE ns.nspname NOT LIKE 'pg_%' AND ns.nspname<>'information_schema' AND pg_has_role(login,ns.nspowner,'MEMBER'))
 OR EXISTS(SELECT 1 FROM pg_database db WHERE db.datname=current_database() AND pg_has_role(login,db.datdba,'MEMBER'))
 OR r.id IS NULL OR w.kind<>'scheduler' OR r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM worker OR r.fence IS DISTINCT FROM expected_fence
 OR(w.tenant_id,w.workspace_id)<>(r.tenant_id,r.workspace_id) OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,w.principal_id)
 OR r.state<>'running' OR j.state NOT IN('active','paused') OR r.lease_until<=clock_timestamp() OR r.deadline_at<=clock_timestamp() OR j.expires_at<=clock_timestamp()
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 token=gen_random_uuid();
 INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,delegation_id,runtime_login,expires_at)
 VALUES(token,w.principal_id,w.tenant_id,w.workspace_id,NULL,login,CASE WHEN j.operation='close_book_risk_v1' THEN least(r.deadline_at,j.expires_at) ELSE least(r.lease_until,r.deadline_at,j.expires_at) END);
 INSERT INTO automation.pilot_run_contexts VALUES(token,r.id,r.fence,r.worker_id,r.worker_login);
 RETURN token;
END;$$;
CREATE OR REPLACE FUNCTION automation.close_risk_run_definition() RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;result jsonb;
BEGIN
 r=automation.check_close_risk_run();
 SELECT jsonb_build_object('definition',jsonb_build_object('closeId',p.close_id,'bookIds',(SELECT jsonb_agg(book_id ORDER BY book_id) FROM automation.close_risk_books WHERE profile_id=p.id),
 'knownAtPolicy',p.known_at_policy,'pendingActuals',p.pending_actuals,'coveragePolicy',p.coverage_policy,'revisionPolicy',p.revision_policy,'ppaImplementation',p.ppa_implementation,'valuationPolicy',p.valuation_policy),
 'declaredRows',p.declared_rows,'declaredRowScenarios',p.declared_row_scenarios,
 'context',jsonb_build_object('contextId',t.id,'effectiveOn',t.effective_on,'knownAt',t.known_at,'knownThrough',t.known_through),
 'factors',coalesce((SELECT jsonb_agg(jsonb_build_object('seriesId',series_id,'observationId',observation_id,'quote',quote_code,'bump',bump::text) ORDER BY series_id) FROM automation.close_risk_factors WHERE profile_id=p.id),'[]')) INTO result
 FROM automation.jobs j JOIN automation.close_risk_profiles p ON p.id=j.close_risk_profile_id JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(p.catalog_id,p.context_id) WHERE j.id=r.job_id AND j.operation='close_book_risk_v1';
 IF result IS NULL THEN RAISE EXCEPTION 'job_operation';END IF;
 r=automation.check_close_risk_run();
 RETURN result;
END;$$;

-- Only the validated publication wrapper can complete this operation.
CREATE FUNCTION automation.complete_close_risk() RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;n timestamptz;actor uuid;
BEGIN
 r=automation.lock_pilot_run();
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 IF j.operation<>'close_book_risk_v1' OR j.interval_seconds IS NOT NULL
 OR NOT EXISTS(SELECT 1 FROM automation.pilot_reports p WHERE p.id=r.id AND p.run_id=r.id AND p.mode='existing_close')
 THEN RAISE EXCEPTION 'pilot_report_required' USING ERRCODE='42501';END IF;
 SELECT principal_id INTO actor FROM automation.workers WHERE login=r.worker_login;
 n=clock_timestamp();
 IF r.lease_until<=n OR r.deadline_at<=n OR j.expires_at<=n THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 UPDATE automation.runs SET state='succeeded',finished_at=n,error_code=NULL WHERE id=r.id;
 UPDATE automation.jobs SET state='completed',version=version+1 WHERE id=j.id;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,actor,'succeeded',n,r.fence);
END;$$;
ALTER FUNCTION automation.publish_pilot_report(uuid,jsonb) RENAME TO publish_pilot_report_pre_renewal;
REVOKE ALL ON FUNCTION automation.publish_pilot_report_pre_renewal(uuid,jsonb) FROM PUBLIC,openetrm_runtime;
CREATE FUNCTION automation.publish_pilot_report(expected_run uuid,payload jsonb) RETURNS uuid
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE existing_close boolean;result uuid;
BEGIN
 SELECT j.operation='close_book_risk_v1' INTO existing_close FROM security.request_contexts q
 JOIN automation.pilot_run_contexts p ON p.context_id=q.id JOIN automation.runs r ON r.id=p.run_id JOIN automation.jobs j ON j.id=r.job_id
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF existing_close THEN
  PERFORM automation.check_close_risk_run();
  -- Wait in the publication queue without holding claim rows needed by renewal
  -- or cancellation. The inner publication rechecks ownership after this wait.
  PERFORM pg_advisory_xact_lock(730849102);
 END IF;
 result=automation.publish_pilot_report_pre_renewal(expected_run,payload);
 IF existing_close THEN PERFORM automation.complete_close_risk();END IF;
 RETURN result;
END;$$;
-- A read-only acknowledgement of an exact committed occurrence. This cannot
-- grant ownership, extend a lease, publish a report or change terminal state.
CREATE FUNCTION automation.close_risk_completed(run uuid,expected_fence bigint,worker uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;
BEGIN
 w=automation.worker();
 RETURN EXISTS(SELECT 1 FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id JOIN automation.pilot_reports p ON p.run_id=r.id AND p.id=r.id
 WHERE r.id=run AND r.fence=expected_fence AND r.worker_id=worker AND r.worker_login=session_user
 AND(r.tenant_id,r.workspace_id)=(w.tenant_id,w.workspace_id) AND w.kind='scheduler'
 AND j.operation='close_book_risk_v1' AND r.state='succeeded' AND p.mode='existing_close'
 AND automation.pilot_service_eligible(j,w) AND automation.pilot_authorized(j,w.principal_id));
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='automation' AND p.proname IN('renew_close_risk_lease','check_close_risk_run','complete_close_risk','close_risk_completed','publish_pilot_report') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('renew_close_risk_lease','close_risk_completed') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_scheduler',f.signature);
  ELSIF f.proname IN('check_close_risk_run','publish_pilot_report') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
CREATE OR REPLACE FUNCTION automation.control_job(request jsonb) RETURNS jsonb
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
 -- Success-first cancellation is outcome reconciliation, not a mutation of
 -- the observed version. Other actions and command-key conflicts are unchanged.
 IF action='cancel' AND j.operation='close_book_risk_v1' AND j.state='completed'
 AND (request->>'expectedVersion')::integer>0
 AND EXISTS(SELECT 1 FROM automation.runs r JOIN automation.pilot_reports p ON p.run_id=r.id AND p.id=r.id WHERE r.job_id=j.id AND r.state='succeeded' AND p.mode='existing_close') THEN
  response=automation.description(j);
  INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,v_request_id,j.id,request,response,n);
  RETURN response;
 END IF;
 IF j.version IS DISTINCT FROM(request->>'expectedVersion')::integer THEN RAISE EXCEPTION 'job_conflict';END IF;
 IF action='pause' AND j.state='active' THEN j.state='paused';
 ELSIF action='resume' AND j.state='paused' AND j.expires_at>n AND automation.authorized(j) THEN j.state='active';
 ELSIF action='run_again' AND j.state IN('completed','blocked') AND j.expires_at>n AND automation.authorized(j)
 AND NOT EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN
 IF(SELECT count(*) FROM automation.jobs q WHERE q.owner_id=j.owner_id AND q.id<>j.id AND(q.state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=q.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
 j.state='active';j.next_due_at=n;
 ELSIF action='cancel' AND j.state IN('active','paused','blocked') THEN
 j.state=CASE WHEN j.operation='swarm_job' AND EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN 'cancellation_requested' ELSE 'cancelled' END;
 UPDATE automation.runs SET state=CASE WHEN j.operation<>'swarm_job' THEN 'cancelled' ELSE 'cancellation_requested' END,
 fence=fence+1,lease_until=n,finished_at=CASE WHEN j.operation<>'swarm_job' THEN n ELSE NULL END WHERE job_id=j.id AND state='running';
 ELSE RAISE EXCEPTION 'job_transition';END IF;
 UPDATE automation.jobs SET state=j.state,next_due_at=j.next_due_at,version=version+1 WHERE id=j.id RETURNING * INTO j;
 response=automation.description(j);
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,v_request_id,j.id,request,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,action,n);
 RETURN response;
END $$;

COMMIT;
