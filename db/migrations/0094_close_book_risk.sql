-- Existing-close risk retains admission pins and never publishes market facts.
BEGIN;
CREATE TABLE automation.close_risk_profiles(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,catalog_id uuid NOT NULL,
 close_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 context_id uuid NOT NULL,trading_known_through integer NOT NULL,known_at ref_data.instant NOT NULL,
 known_at_policy text NOT NULL CHECK(known_at_policy='valuation_instant_v1'),pending_actuals text NOT NULL CHECK(pending_actuals IN('require_actuals_v1','estimate_until_actual_v1')),
 coverage_policy text NOT NULL CHECK(coverage_policy='ppa_coverage_v1'),revision_policy text NOT NULL CHECK(revision_policy='exact_bundle_whole_operation_v1'),
 ppa_implementation text NOT NULL CHECK(ppa_implementation='ppa_capabilities_v1'),valuation_policy jsonb NOT NULL,
 declared_rows bigint NOT NULL CHECK(declared_rows>=0),declared_row_scenarios bigint NOT NULL CHECK(declared_row_scenarios BETWEEN 0 AND 2000000000),UNIQUE(id,catalog_id),UNIQUE(id,catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id),UNIQUE(id,tenant_id,workspace_id),
 FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,context_id) REFERENCES trading.contexts(catalog_id,context_id));
CREATE TABLE automation.close_risk_books(
 profile_id uuid NOT NULL,catalog_id uuid NOT NULL,book_id uuid NOT NULL,PRIMARY KEY(profile_id,book_id),
 FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.close_risk_profiles(id,catalog_id),FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id));
CREATE TABLE automation.close_risk_factors(
 profile_id uuid NOT NULL,catalog_id uuid NOT NULL,series_id uuid NOT NULL,observation_id uuid NOT NULL,quote_code ref_data.code NOT NULL,bump ref_data.exact_number NOT NULL CHECK(bump>0),
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 PRIMARY KEY(profile_id,series_id),FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.close_risk_profiles(id,catalog_id),
 FOREIGN KEY(catalog_id,observation_id) REFERENCES market_data.observations(catalog_id,id),
 FOREIGN KEY(profile_id,catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES automation.close_risk_profiles(id,catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code));
ALTER TABLE automation.jobs ADD COLUMN close_risk_profile_id uuid;
ALTER TABLE automation.jobs ADD FOREIGN KEY(close_risk_profile_id,tenant_id,workspace_id) REFERENCES automation.close_risk_profiles(id,tenant_id,workspace_id);
ALTER TABLE automation.jobs DROP CONSTRAINT jobs_operation_check;
ALTER TABLE automation.jobs DROP CONSTRAINT jobs_check;
ALTER TABLE automation.jobs ADD CHECK(operation IN('worker_check','swarm_job','generated_pilot_close_v1','close_book_risk_v1'));
ALTER TABLE automation.jobs ADD CHECK(
 (operation='worker_check' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NULL)
 OR(operation='swarm_job' AND profile_id IS NOT NULL AND profile_version IS NOT NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NULL)
 OR(operation='generated_pilot_close_v1' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NOT NULL AND pilot_profile_version IS NOT NULL AND close_risk_profile_id IS NULL AND(interval_seconds IS NULL OR interval_seconds=86400))
 OR(operation='close_book_risk_v1' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NOT NULL AND interval_seconds IS NULL));
CREATE FUNCTION automation.close_risk_authorized(j automation.jobs,service uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p automation.close_risk_profiles;required_permission text;d security.agent_delegations;n timestamptz:=clock_timestamp();
BEGIN
 IF j.operation<>'close_book_risk_v1' OR NOT automation.authorized_pre_pilot(j) THEN RETURN false;END IF;
 SELECT * INTO p FROM automation.close_risk_profiles WHERE(id,tenant_id,workspace_id)=(j.close_risk_profile_id,j.tenant_id,j.workspace_id);
 IF p.id IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.catalog_id,j.tenant_id,j.workspace_id))
 OR NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.reference_catalog_id,j.tenant_id,j.workspace_id)) THEN RETURN false;END IF;
 SELECT * INTO d FROM security.agent_delegations WHERE id=j.delegation_id;
 FOREACH required_permission IN ARRAY ARRAY['RunJobs','ReadMarketData','ReadTrades','RunValuation','RunScenario'] LOOP
  IF NOT security.principal_has_permission(j.owner_id,j.tenant_id,j.workspace_id,required_permission,n)
   OR(service IS NOT NULL AND NOT security.principal_has_permission(service,j.tenant_id,j.workspace_id,required_permission,n))
   OR(j.delegation_id IS NOT NULL AND(NOT security.principal_has_permission(d.delegator_id,j.tenant_id,j.workspace_id,required_permission,n)
    OR NOT EXISTS(SELECT 1 FROM security.delegation_permissions dp WHERE dp.delegation_id=d.id AND dp.permission=required_permission))) THEN RETURN false;END IF;
 END LOOP;
 RETURN true;
END;$$;
CREATE OR REPLACE FUNCTION automation.pilot_authorized(j automation.jobs,service uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p automation.pilot_close_profiles;required_permission text;d security.agent_delegations;n timestamptz:=clock_timestamp();
BEGIN
 IF j.operation='close_book_risk_v1' THEN RETURN automation.close_risk_authorized(j,service);END IF;
 IF j.operation<>'generated_pilot_close_v1' OR NOT automation.authorized_pre_pilot(j) THEN RETURN false;END IF;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version,tenant_id,workspace_id)=(j.pilot_profile_id,j.pilot_profile_version,j.tenant_id,j.workspace_id);
 IF p.id IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.catalog_id,j.tenant_id,j.workspace_id))
 OR NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.reference_catalog_id,j.tenant_id,j.workspace_id))
 OR NOT EXISTS(SELECT 1 FROM ref_data.sources s WHERE(s.catalog_id,s.revision_id,s.code)=(p.reference_catalog_id,p.reference_revision_id,p.source_code) AND s.kind='synthetic')
 THEN RETURN false;END IF;
 SELECT * INTO d FROM security.agent_delegations WHERE id=j.delegation_id;
 FOREACH required_permission IN ARRAY automation.pilot_permissions() LOOP
  IF NOT security.principal_has_permission(j.owner_id,j.tenant_id,j.workspace_id,required_permission,n)
  OR(service IS NOT NULL AND NOT security.principal_has_permission(service,j.tenant_id,j.workspace_id,required_permission,n))
  OR(j.delegation_id IS NOT NULL AND(NOT security.principal_has_permission(d.delegator_id,j.tenant_id,j.workspace_id,required_permission,n)
     OR NOT EXISTS(SELECT 1 FROM security.delegation_permissions dp WHERE dp.delegation_id=d.id AND dp.permission=required_permission)))
  THEN RETURN false;END IF;
 END LOOP;
 RETURN true;
END;$$;
CREATE OR REPLACE FUNCTION automation.authorized(j automation.jobs) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT CASE WHEN j.operation IN('generated_pilot_close_v1','close_book_risk_v1') THEN automation.pilot_authorized(j,NULL) ELSE automation.authorized_pre_pilot(j) END;
$$;
CREATE OR REPLACE FUNCTION automation.pilot_service_eligible(j automation.jobs,w automation.workers) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT w.kind='scheduler' AND(j.tenant_id,j.workspace_id)=(w.tenant_id,w.workspace_id)
 AND ((j.operation='close_book_risk_v1' AND automation.close_risk_authorized(j,w.principal_id) AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l JOIN automation.close_risk_profiles p ON p.id=j.close_risk_profile_id WHERE l.worker_login=w.login AND l.catalog_id=p.catalog_id)) OR(j.operation='generated_pilot_close_v1' AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l JOIN automation.pilot_close_profiles p
  ON(p.id,p.version)=(j.pilot_profile_id,j.pilot_profile_version) WHERE l.worker_login=w.login AND l.catalog_id=p.catalog_id)
 AND NOT EXISTS(SELECT 1 FROM unnest(automation.pilot_permissions())p
  WHERE NOT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,p,clock_timestamp()))));
$$;
CREATE OR REPLACE FUNCTION automation.pilot_context_live(token uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM automation.pilot_run_contexts c JOIN automation.runs r ON r.id=c.run_id JOIN automation.jobs j ON j.id=r.job_id
 JOIN automation.workers w ON w.login=c.worker_login JOIN automation.pilot_runtime_logins l ON l.worker_login=w.login

 JOIN security.request_contexts q ON q.id=c.context_id JOIN security.principals sp ON sp.id=w.principal_id AND sp.enabled AND sp.kind='service'
 WHERE c.context_id=token AND(c.fence,c.worker_id,c.worker_login)=(r.fence,r.worker_id,r.worker_login)
 AND(q.actor_id,q.tenant_id,q.workspace_id,q.runtime_login)=(w.principal_id,w.tenant_id,w.workspace_id,l.runtime_login)
 AND w.kind='scheduler' AND r.state='running' AND j.state IN('active','paused') AND r.lease_until>clock_timestamp() AND r.deadline_at>clock_timestamp() AND j.expires_at>clock_timestamp()
 AND automation.pilot_service_eligible(j,w) AND automation.pilot_authorized(j,w.principal_id));
$$;
CREATE FUNCTION automation.unsafe_pilot_ppa(j automation.jobs) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT j.operation='generated_pilot_close_v1' AND EXISTS(
 SELECT 1 FROM(SELECT DISTINCT ON(r.catalog_id,r.trade_id) r.catalog_id,r.trade_id,r.book_id FROM trading.ppa_trade_revisions r
 JOIN automation.pilot_close_profiles p ON(p.id,p.version,p.catalog_id)=(j.pilot_profile_id,j.pilot_profile_version,r.catalog_id)
 WHERE r.effective_at<=j.next_due_at ORDER BY r.catalog_id,r.trade_id,r.revision DESC) h
 JOIN automation.pilot_close_books b ON(b.profile_id,b.profile_version,b.catalog_id,b.book_id)=(j.pilot_profile_id,j.pilot_profile_version,h.catalog_id,h.book_id));
$$;
CREATE OR REPLACE FUNCTION automation.claim_next(worker_id uuid,lease_seconds integer,operations text[]) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;response jsonb;
BEGIN
 w=automation.worker();n=clock_timestamp();
 IF worker_id IS NULL OR lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 120 OR operations IS NULL OR cardinality(operations) NOT BETWEEN 1 AND 4 OR (SELECT count(DISTINCT o) FROM unnest(operations)o)<>cardinality(operations) OR EXISTS(SELECT 1 FROM unnest(operations) o WHERE o IS NULL OR o NOT IN('worker_check','swarm_job','generated_pilot_close_v1','close_book_risk_v1')) THEN RAISE EXCEPTION 'job_input';END IF;
 FOR j IN SELECT * FROM automation.jobs WHERE(tenant_id,workspace_id)=(w.tenant_id,w.workspace_id)
 AND operation=ANY(operations)
 AND ((w.kind='scheduler' AND(operation='worker_check' OR(operation IN('generated_pilot_close_v1','close_book_risk_v1') AND automation.pilot_service_eligible(jobs,w)))) OR(w.kind='controller' AND operation='swarm_job'))
 AND((state='active' AND next_due_at<=n) OR(w.kind='controller' AND EXISTS(SELECT 1 FROM automation.runs pending_run WHERE pending_run.job_id=jobs.id AND pending_run.state IN('running','cancellation_requested'))))
 ORDER BY last_reconciled_at,next_due_at,id FOR UPDATE SKIP LOCKED LIMIT 32 LOOP
 n=clock_timestamp();
 IF j.operation IN('generated_pilot_close_v1','close_book_risk_v1') AND NOT automation.pilot_service_eligible(j,w) THEN CONTINUE;END IF;
 IF automation.unsafe_pilot_ppa(j) THEN
  UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;
  UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='pilot_close_inputs' WHERE job_id=j.id AND state='running';
  CONTINUE;
 END IF;
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
 UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='authority_revoked' WHERE job_id=j.id AND state='running' AND j.operation<>'swarm_job';
 UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 IF j.expires_at<=n THEN
 UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='job_expired' WHERE job_id=j.id AND state='running' AND j.operation<>'swarm_job';
 UPDATE automation.jobs SET state='expired',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 SELECT * INTO r FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at FOR UPDATE;
 n=clock_timestamp();w=automation.worker();
 IF j.operation IN('generated_pilot_close_v1','close_book_risk_v1') AND(j.expires_at<=n OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,NULL)) THEN CONTINUE;END IF;
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
CREATE OR REPLACE FUNCTION automation.claim_next(worker_id uuid,lease_seconds integer,pilot_enabled boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF pilot_enabled IS NULL THEN RAISE EXCEPTION 'job_input';END IF;
 RETURN automation.claim_next(worker_id,lease_seconds,CASE WHEN pilot_enabled THEN ARRAY['worker_check','swarm_job','generated_pilot_close_v1'] ELSE ARRAY['worker_check','swarm_job'] END);
END;$$;
CREATE OR REPLACE FUNCTION security.ppa_read_scope(catalog uuid,scope text,ids uuid[],context uuid,as_of timestamptz,include_history boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE heads trading.ppa_trade_revisions[];r trading.ppa_trade_revisions;result jsonb;historical jsonb;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() AND NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND(q.market_catalog_id,q.market_context_id)=(catalog,context) AND q.expires_at>clock_timestamp()) THEN RAISE EXCEPTION 'ppa_historical' USING ERRCODE='P0001';END IF;
 IF scope IS NULL OR scope NOT IN('books','trades') OR ids IS NULL OR cardinality(ids) NOT BETWEEN 1 AND 256 OR include_history IS NULL OR
  EXISTS(SELECT 1 FROM unnest(ids) x WHERE x IS NULL OR x='00000000-0000-0000-0000-000000000000'::uuid) OR
  (SELECT count(DISTINCT x) FROM unnest(ids) x)<>cardinality(ids) OR (include_history AND(scope<>'trades' OR cardinality(ids)<>1))
 THEN RAISE EXCEPTION 'ppa_scope' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE t.catalog_id=catalog AND t.id=context AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 IF scope='books' AND(SELECT count(*) FROM trading.books b WHERE b.catalog_id=catalog AND b.id=ANY(ids) AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of))<>cardinality(ids)
 THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT coalesce(array_agg(h::trading.ppa_trade_revisions ORDER BY h.trade_id),ARRAY[]::trading.ppa_trade_revisions[]) INTO heads FROM(
  SELECT DISTINCT ON(t.trade_id) t.* FROM trading.ppa_trade_revisions t WHERE t.catalog_id=catalog
   AND security.ppa_visible_at(catalog,t.publication_order,t.effective_at,context,as_of) ORDER BY t.trade_id,t.revision DESC
 )h WHERE CASE WHEN scope='books' THEN h.book_id=ANY(ids) ELSE h.trade_id=ANY(ids) END;
 IF cardinality(heads)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF scope='trades' AND cardinality(heads)<>cardinality(ids) THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 END LOOP;
 IF(SELECT count(*) FROM trading.ppa_revision_bindings b JOIN unnest(heads) h ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision))>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF include_history THEN
  SELECT coalesce(jsonb_agg(to_jsonb(retained) ORDER BY retained.revision,retained.coverage_from,retained.binding_id),'[]'::jsonb) INTO historical FROM(
   SELECT b.* FROM trading.ppa_revision_bindings b JOIN trading.ppa_trade_revisions h
    ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision)
   WHERE h.catalog_id=catalog AND h.trade_id=ids[1]
    AND security.ppa_visible_at(catalog,h.publication_order,h.effective_at,context,as_of)
    AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of)
   ORDER BY b.revision,b.coverage_from,b.binding_id LIMIT 257
  )retained;
  IF jsonb_array_length(historical)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 ELSE historical='[]'::jsonb;END IF;
 SELECT jsonb_build_object('heads',to_jsonb(heads),'bindings',coalesce((SELECT jsonb_agg(to_jsonb(b) ORDER BY b.trade_id,b.coverage_from,b.binding_id)
  FROM trading.ppa_revision_bindings b JOIN unnest(heads) h ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision)),'[]'::jsonb),
  'historicalBindings',historical,'history',CASE WHEN include_history THEN coalesce((SELECT jsonb_agg(to_jsonb(h) ORDER BY h.revision) FROM trading.ppa_trade_revisions h
   WHERE h.catalog_id=catalog AND h.trade_id=ids[1] AND security.ppa_visible_at(catalog,h.publication_order,h.effective_at,context,as_of)),'[]'::jsonb) ELSE '[]'::jsonb END) INTO result;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Current reference access expired' USING ERRCODE='42501';END IF;
 END LOOP;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE OR REPLACE FUNCTION security.ppa_require_market(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,context uuid,valuation timestamptz,pins uuid[])
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t market_data.temporal_contexts;prefix integer;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'No current PPA market access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() AND NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND(q.market_catalog_id,q.market_context_id)=(catalog,context) AND q.expires_at>clock_timestamp()) THEN RAISE EXCEPTION 'ppa_historical' USING ERRCODE='P0001';END IF;
 SELECT c.* INTO t FROM market_data.temporal_contexts c
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(c.catalog_id,c.id)
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(c.catalog_id,c.id)
  WHERE c.catalog_id=catalog AND c.id=context AND c.effective_on=(valuation AT TIME ZONE 'UTC')::date;
 IF NOT FOUND OR valuation IS NULL OR NOT isfinite(valuation) OR pins IS NULL OR
  EXISTS(SELECT 1 FROM unnest(pins) p WHERE p IS NULL) OR(SELECT count(DISTINCT p) FROM unnest(pins) p)<>cardinality(pins)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 SELECT m.known_through INTO prefix FROM market_data.market_contexts m WHERE(m.catalog_id,m.context_id)=(catalog,context);
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN ref_data.revisions q ON(q.catalog_id,q.id)=(r.reference_catalog_id,r.reference_revision_id)
  WHERE(r.catalog_id,r.id)=(catalog,definition) AND(r.reference_catalog_id,r.reference_revision_id)=(ref_catalog,ref_revision) AND r.state='sealed' AND q.state='sealed')
 THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE e.catalog_id=catalog AND e.revision_id=definition AND e.observation_id IS NULL AND e.event_order<=prefix AND k.known_from<=t.known_at
   AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to)
 THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
 IF(SELECT count(*) FROM market_data.observations o
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE o.catalog_id=catalog AND o.definition_revision_id=definition AND o.id=ANY(pins)
   AND e.event_order<=prefix AND k.known_from<=t.known_at AND o.observed_at<=valuation)<>cardinality(pins)
 THEN RAISE EXCEPTION 'ppa_observation_knowledge' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'Current PPA market access expired' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION automation.close_risk_scope(catalog uuid,context uuid,valuation timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE rows jsonb;item jsonb;
BEGIN
 PERFORM security.require_bound_market_context(catalog,context,valuation);
 IF (SELECT count(*) FROM trading.books b WHERE b.catalog_id=catalog AND b.id=ANY(books) AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,valuation))<>cardinality(books) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 WITH futures AS(SELECT DISTINCT ON(r.trade_id) r.* FROM trading.trade_revisions r WHERE r.catalog_id=catalog AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,valuation) ORDER BY r.trade_id,r.revision DESC),
 swaps AS(SELECT DISTINCT ON(r.trade_id) r.* FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,valuation) ORDER BY r.trade_id,r.revision DESC),
 ppas AS(SELECT DISTINCT ON(r.trade_id) r.* FROM trading.ppa_trade_revisions r WHERE r.catalog_id=catalog AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,valuation) ORDER BY r.trade_id,r.revision DESC),
 all_bindings AS(
 SELECT 'future' AS kind,r.trade_id,r.revision,r.binding_id,r.instrument_id,r.book_id,m.definition_revision_id,m.reference_catalog_id,m.reference_revision_id,m.implementation FROM futures r LEFT JOIN trading.futures_revision_markets m ON(m.catalog_id,m.trade_id,m.revision)=(r.catalog_id,r.trade_id,r.revision) WHERE r.book_id=ANY(books)
 UNION ALL
 SELECT 'swap',r.trade_id,r.revision,r.binding_id,r.instrument_id,r.book_id,f.definition_revision_id,s.reference_catalog_id,s.reference_revision_id,i.implementation FROM swaps r LEFT JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) LEFT JOIN trading.swap_fixed_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.fixed_side_id) LEFT JOIN trading.swap_floating_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.floating_side_id) WHERE r.book_id=ANY(books)
 UNION ALL
 SELECT 'ppa',r.trade_id,r.revision,b.binding_id,b.instrument_id,r.book_id,b.definition_revision_id,b.reference_catalog_id,b.reference_revision_id,i.implementation FROM ppas r JOIN trading.ppa_revision_bindings b ON(b.catalog_id,b.trade_id,b.revision)=(r.catalog_id,r.trade_id,r.revision) JOIN trading.ppa_instruments i ON(i.catalog_id,i.instrument_id)=(b.catalog_id,b.instrument_id) WHERE r.book_id=ANY(books))
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY trade_id,binding_id),'[]') INTO rows FROM all_bindings x;
 FOR item IN SELECT * FROM jsonb_array_elements(rows) LOOP
  IF item->>'reference_catalog_id' IS NOT NULL AND NOT security.reference_access((item->>'reference_catalog_id')::uuid,'ReadMarketData') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 PERFORM security.require_bound_market_context(catalog,context,valuation);
 RETURN rows;
END;$$;
ALTER FUNCTION automation.close_risk_scope(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.close_risk_scope(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.close_risk_scope(uuid,uuid,timestamptz,uuid[]) TO openetrm_runtime;
CREATE FUNCTION automation.close_risk_replay(request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;previous automation.commands;j automation.jobs;
BEGIN
 c=automation.actor();IF NOT security.current_write_context() OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,(close_risk_replay.request->'schedule'->>'requestId')::uuid);
 IF NOT FOUND THEN RETURN NULL;END IF;
 SELECT * INTO j FROM automation.jobs WHERE id=previous.job_id;
 IF previous.request IS DISTINCT FROM request THEN RAISE EXCEPTION 'job_conflict';END IF;
 IF NOT automation.close_risk_authorized(j,NULL) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 RETURN previous.response;
END;$$;
CREATE FUNCTION automation.create_close_risk_job(request jsonb,context uuid,factors jsonb,declared_rows bigint,declared_row_scenarios bigint) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;p automation.close_risk_profiles;anchor market_data.market_closes;
 schedule jsonb:=request->'schedule';definition jsonb:=request->'definition';previous automation.commands;response jsonb;n timestamptz;operation uuid;
BEGIN
 c=automation.actor();IF NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(schedule) IS DISTINCT FROM 'object' OR jsonb_typeof(definition) IS DISTINCT FROM 'object'
 OR request-ARRAY['schedule','definition']<>'{}'::jsonb
 OR schedule-ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']<>'{}'::jsonb
 OR NOT(schedule ?& ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion'])
 OR definition-ARRAY['closeId','bookIds','knownAtPolicy','pendingActuals','coveragePolicy','revisionPolicy','ppaImplementation','valuationPolicy']<>'{}'::jsonb
 OR NOT(definition ?& ARRAY['closeId','bookIds','knownAtPolicy','pendingActuals','coveragePolicy','revisionPolicy','ppaImplementation','valuationPolicy'])
 OR jsonb_typeof(definition->'bookIds') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'bookIds') NOT BETWEEN 1 AND 128
 OR jsonb_typeof(definition->'valuationPolicy') IS DISTINCT FROM 'object'
 OR NOT(definition->'valuationPolicy' ?& ARRAY['curveNumericalPolicy','riskNumericalPolicy','futuresConvention','swapConvention','rateNumericalPolicy','discountInterpolationPolicy','bumpConvention','bumpBasis','bumpTicks'])
 OR definition->'valuationPolicy'->>'curveNumericalPolicy' IS DISTINCT FROM 'exact_18_fractional_abs_lt_1e24_v1'
 OR definition->'valuationPolicy'->>'riskNumericalPolicy' IS DISTINCT FROM 'exact_rational_portfolio_repricing_v1'
 OR definition->'valuationPolicy'->>'futuresConvention' IS DISTINCT FROM 'unprocessed_futures_execution_pnl_risk_v2'
 OR definition->'valuationPolicy'->>'swapConvention' IS DISTINCT FROM 'independent_side_financial_swap_v2'
 OR definition->'valuationPolicy'->>'rateNumericalPolicy' IS DISTINCT FROM 'simple_actual365fixed_rational_to_even_18_v1'
 OR definition->'valuationPolicy'->>'discountInterpolationPolicy' IS DISTINCT FROM 'log_discount_geometric_root_to_even_18_v1'
 OR definition->'valuationPolicy'->>'bumpConvention' IS DISTINCT FROM 'absolute_quote_input_symmetric_v1'
 OR jsonb_typeof(definition->'valuationPolicy'->'bumpTicks') IS DISTINCT FROM 'number'
 OR (definition->'valuationPolicy')-ARRAY['curveNumericalPolicy','riskNumericalPolicy','futuresConvention','swapConvention','rateNumericalPolicy','discountInterpolationPolicy','bumpConvention','bumpBasis','bumpTicks']<>'{}'::jsonb
 OR definition->'valuationPolicy'->>'bumpBasis' IS DISTINCT FROM 'trading_tick' OR (definition->'valuationPolicy'->>'bumpTicks')::integer NOT BETWEEN 1 AND 100
 THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 IF factors IS NULL OR jsonb_typeof(factors)<>'array' OR jsonb_array_length(factors)>4096 OR declared_rows IS NULL OR declared_rows<0 OR declared_row_scenarios IS NULL OR declared_row_scenarios<declared_rows OR declared_row_scenarios>2000000000 THEN RAISE EXCEPTION 'risk_size';END IF;
 operation=(schedule->>'requestId')::uuid;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;c=automation.actor();n=clock_timestamp();
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  SELECT * INTO j FROM automation.jobs WHERE id=previous.job_id;
  IF previous.request IS DISTINCT FROM request THEN RAISE EXCEPTION 'job_conflict';END IF;
  IF NOT automation.close_risk_authorized(j,NULL) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
  RETURN previous.response;
 END IF;
 SELECT * INTO anchor FROM market_data.market_closes WHERE id=(definition->>'closeId')::uuid AND security.market_close_visible(catalog_id,id);
 IF anchor.id IS NULL THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 PERFORM security.require_bound_market_context(anchor.catalog_id,context,anchor.at);
 IF(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND(state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=jobs.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
 p.id=gen_random_uuid();p.tenant_id=c.tenant_id;p.workspace_id=c.workspace_id;p.catalog_id=anchor.catalog_id;p.close_id=anchor.id;
 p.definition_revision_id=anchor.definition_revision_id;p.reference_catalog_id=anchor.reference_catalog_id;p.reference_revision_id=anchor.reference_revision_id;
 p.context_id=context;SELECT known_through INTO p.trading_known_through FROM trading.contexts WHERE(catalog_id,context_id)=(p.catalog_id,context);
 p.known_at=anchor.at;p.known_at_policy=definition->>'knownAtPolicy';p.pending_actuals=definition->>'pendingActuals';p.coverage_policy=definition->>'coveragePolicy';p.revision_policy=definition->>'revisionPolicy';p.ppa_implementation=definition->>'ppaImplementation';p.valuation_policy=definition->'valuationPolicy';p.declared_rows=create_close_risk_job.declared_rows;p.declared_row_scenarios=create_close_risk_job.declared_row_scenarios;
 INSERT INTO automation.close_risk_profiles SELECT(p).*;
 INSERT INTO automation.close_risk_books SELECT p.id,p.catalog_id,v::uuid FROM jsonb_array_elements_text(definition->'bookIds')v;
 INSERT INTO automation.close_risk_factors SELECT p.id,p.catalog_id,x."seriesId",x."observationId",x.quote,x.bump,p.definition_revision_id,p.reference_catalog_id,p.reference_revision_id FROM jsonb_to_recordset(factors)x("seriesId" uuid,"observationId" uuid,quote text,bump numeric);
 IF EXISTS(SELECT 1 FROM automation.close_risk_factors f WHERE f.profile_id=p.id AND NOT EXISTS(SELECT 1 FROM market_data.market_close_pins cp JOIN market_data.observations o ON(o.catalog_id,o.id)=(cp.catalog_id,cp.observation_id) WHERE(cp.catalog_id,cp.close_id,cp.observation_id,cp.input_use)=(p.catalog_id,p.close_id,f.observation_id,'variable') AND o.series_id=f.series_id)) THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;
 j.name=schedule->>'name';j.operation=schedule->>'operation';j.state='active';j.version=1;j.next_due_at=(schedule->>'dueAt')::timestamptz;j.expires_at=(schedule->>'expiresAt')::timestamptz;
 j.interval_seconds=(schedule->>'intervalSeconds')::integer;j.time_zone=schedule->>'timeZone';j.misfire=schedule->>'misfire';j.overlap_policy=schedule->>'overlap';
 j.timeout_seconds=(schedule->>'timeoutSeconds')::integer;j.maximum_attempts=(schedule->>'maximumAttempts')::integer;j.profile_id=(schedule->>'profileId')::uuid;j.profile_version=(schedule->>'profileVersion')::integer;
 j.close_risk_profile_id=p.id;j.created_at=n;j.last_reconciled_at='-infinity';
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR j.operation IS DISTINCT FROM 'close_book_risk_v1' OR j.interval_seconds IS NOT NULL OR j.expires_at<=n OR j.expires_at<=j.next_due_at
 OR j.expires_at>n+interval '366 days' OR j.next_due_at<n-interval '1 day' OR NOT automation.close_risk_authorized(j,NULL) THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 INSERT INTO automation.jobs SELECT(j).*;
 response=automation.description(j)||jsonb_build_object('requestId',operation,'admission',jsonb_build_object('closeId',p.close_id,'contextId',p.context_id,'knownAt',p.known_at,'tradingKnownThrough',p.trading_known_through,'factors',factors,'declaredRows',p.declared_rows,'declaredRowScenarios',p.declared_row_scenarios));
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,request,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,'created',n);
 PERFORM security.require_bound_market_context(anchor.catalog_id,context,anchor.at);
 RETURN response;
END;$$;
CREATE FUNCTION automation.close_risk_run_definition() RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;result jsonb;
BEGIN
 r=automation.lock_pilot_run();
 SELECT jsonb_build_object('definition',jsonb_build_object('closeId',p.close_id,'bookIds',(SELECT jsonb_agg(book_id ORDER BY book_id) FROM automation.close_risk_books WHERE profile_id=p.id),
 'knownAtPolicy',p.known_at_policy,'pendingActuals',p.pending_actuals,'coveragePolicy',p.coverage_policy,'revisionPolicy',p.revision_policy,'ppaImplementation',p.ppa_implementation,'valuationPolicy',p.valuation_policy),
 'declaredRows',p.declared_rows,'declaredRowScenarios',p.declared_row_scenarios,
 'context',jsonb_build_object('contextId',t.id,'effectiveOn',t.effective_on,'knownAt',t.known_at,'knownThrough',t.known_through),
 'factors',coalesce((SELECT jsonb_agg(jsonb_build_object('seriesId',series_id,'observationId',observation_id,'quote',quote_code,'bump',bump::text) ORDER BY series_id) FROM automation.close_risk_factors WHERE profile_id=p.id),'[]')) INTO result
 FROM automation.jobs j JOIN automation.close_risk_profiles p ON p.id=j.close_risk_profile_id JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(p.catalog_id,p.context_id) WHERE j.id=r.job_id AND j.operation='close_book_risk_v1';
 IF result IS NULL THEN RAISE EXCEPTION 'job_operation';END IF;
 r=automation.lock_pilot_run();
 RETURN result;
END;$$;
ALTER TABLE automation.pilot_reports DROP CONSTRAINT pilot_reports_check;
ALTER TABLE automation.pilot_reports ADD COLUMN mode text NOT NULL DEFAULT 'generated_close' CHECK(mode IN('generated_close','existing_close'));
ALTER TABLE automation.pilot_reports ADD CHECK(id=run_id AND(mode='existing_close' OR id=close_id));
ALTER TABLE automation.pilot_reports ADD UNIQUE(catalog_id,id,close_id);
-- Shared factor/rate receipts refer to their report's selected close explicitly.
-- Populate only the new relation; retained financial rows and identities stay intact.
CREATE TABLE automation.pilot_report_input_pins(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,close_id uuid NOT NULL,observation_id uuid NOT NULL,input_use text NOT NULL,
 PRIMARY KEY(catalog_id,report_id,observation_id,input_use),
 FOREIGN KEY(catalog_id,report_id,close_id) REFERENCES automation.pilot_reports(catalog_id,id,close_id),
 FOREIGN KEY(catalog_id,close_id,observation_id,input_use) REFERENCES market_data.market_close_pins(catalog_id,close_id,observation_id,input_use));
INSERT INTO automation.pilot_report_input_pins
 SELECT p.catalog_id,p.report_id,h.close_id,p.observation_id,p.input_use FROM(
 SELECT catalog_id,report_id,observation_id,input_use FROM automation.pilot_report_factors
 UNION SELECT catalog_id,report_id,observation_id,input_use FROM automation.pilot_report_discount_observations)p
 JOIN automation.pilot_reports h ON(h.catalog_id,h.id)=(p.catalog_id,p.report_id);
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT conrelid::regclass AS relation,conname FROM pg_constraint
 WHERE confrelid='market_data.market_close_pins'::regclass AND conrelid IN('automation.pilot_report_factors'::regclass,'automation.pilot_report_discount_observations'::regclass) LOOP
  EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I',item.relation,item.conname);
 END LOOP;
END$$;
ALTER TABLE automation.pilot_report_factors ADD FOREIGN KEY(catalog_id,report_id,observation_id,input_use) REFERENCES automation.pilot_report_input_pins(catalog_id,report_id,observation_id,input_use);
ALTER TABLE automation.pilot_report_discount_observations ADD FOREIGN KEY(catalog_id,report_id,observation_id,input_use) REFERENCES automation.pilot_report_input_pins(catalog_id,report_id,observation_id,input_use);

ALTER TABLE automation.pilot_report_models ADD COLUMN ppa_instrument_id uuid;
ALTER TABLE automation.pilot_report_models DROP CONSTRAINT pilot_report_models_check;
ALTER TABLE automation.pilot_report_models ADD CHECK(
 (future_contract_id IS NOT NULL AND future_series_code IS NOT NULL AND swap_instrument_id IS NULL AND ppa_instrument_id IS NULL AND id=security.futures_contract_id(reference_catalog_id,reference_revision_id,future_series_code)) OR
 (future_contract_id IS NULL AND future_series_code IS NULL AND swap_instrument_id IS NOT NULL AND ppa_instrument_id IS NULL AND id=swap_instrument_id) OR
 (future_contract_id IS NULL AND future_series_code IS NULL AND swap_instrument_id IS NULL AND ppa_instrument_id IS NOT NULL AND id=ppa_instrument_id));
ALTER TABLE automation.pilot_report_models ADD FOREIGN KEY(catalog_id,ppa_instrument_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES trading.ppa_instruments(catalog_id,instrument_id,definition_revision_id,reference_catalog_id,reference_revision_id);
ALTER TABLE automation.pilot_report_positions ADD COLUMN ppa_revision integer;
ALTER TABLE automation.pilot_report_positions ADD COLUMN coverage_from ref_data.instant;
ALTER TABLE automation.pilot_report_positions ADD COLUMN coverage_to ref_data.instant;
ALTER TABLE automation.pilot_report_positions DROP CONSTRAINT pilot_report_positions_check;
ALTER TABLE automation.pilot_report_positions ADD CHECK(num_nonnulls(future_revision,swap_revision,ppa_revision)=1);
ALTER TABLE automation.pilot_report_positions ADD CHECK((ppa_revision IS NULL AND coverage_from IS NULL AND coverage_to IS NULL) OR(ppa_revision IS NOT NULL AND coverage_from IS NOT NULL AND coverage_to IS NOT NULL AND coverage_from<coverage_to));
ALTER TABLE automation.pilot_report_positions DROP COLUMN measure;
ALTER TABLE automation.pilot_report_positions ADD COLUMN measure text GENERATED ALWAYS AS(CASE WHEN future_revision IS NOT NULL THEN 'futures_execution_pnl' WHEN swap_revision IS NOT NULL THEN 'swap_present_value' ELSE 'ppa_value_excluding_overdue' END) STORED;
ALTER TABLE trading.ppa_revision_bindings ADD UNIQUE(catalog_id,trade_id,revision,binding_id,instrument_id,publication_order,coverage_from,coverage_to,quantity);
ALTER TABLE trading.ppa_trade_revisions ADD UNIQUE(catalog_id,trade_id,revision,book_id);
ALTER TABLE automation.pilot_report_positions ADD FOREIGN KEY(catalog_id,trade_id,ppa_revision,binding_id,instrument_id,publication_order,coverage_from,coverage_to,quantity) REFERENCES trading.ppa_revision_bindings(catalog_id,trade_id,revision,binding_id,instrument_id,publication_order,coverage_from,coverage_to,quantity);
ALTER TABLE automation.pilot_report_positions ADD FOREIGN KEY(catalog_id,trade_id,ppa_revision,book_id) REFERENCES trading.ppa_trade_revisions(catalog_id,trade_id,revision,book_id);
ALTER TABLE automation.pilot_report_totals DROP CONSTRAINT pilot_report_totals_measure_check;
ALTER TABLE automation.pilot_report_totals ADD CHECK(measure IN('futures_execution_pnl','swap_present_value','ppa_value_excluding_overdue'));
ALTER TABLE automation.pilot_report_discounts ADD COLUMN ppa_side_id uuid;
ALTER TABLE automation.pilot_report_discounts DROP CONSTRAINT pilot_report_discounts_check;
ALTER TABLE automation.pilot_report_discounts ADD CHECK(num_nonnulls(fixed_side_id,floating_side_id,ppa_side_id)=1);
ALTER TABLE automation.pilot_report_discounts ADD FOREIGN KEY(catalog_id,instrument_id,ppa_side_id) REFERENCES trading.ppa_memberships(catalog_id,instrument_id,side_id);
CREATE TABLE automation.pilot_report_ppa_inputs(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,known_at ref_data.instant NOT NULL,knowledge_context uuid NOT NULL,pending_actuals text NOT NULL,coverage_policy text NOT NULL,
 PRIMARY KEY(catalog_id,report_id),FOREIGN KEY(catalog_id,report_id) REFERENCES automation.pilot_reports(catalog_id,id),
 FOREIGN KEY(catalog_id,knowledge_context) REFERENCES market_data.temporal_contexts(catalog_id,id));
CREATE TABLE automation.pilot_report_ppa_quantities(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,series_id uuid NOT NULL,revision integer NOT NULL,known_at ref_data.instant NOT NULL,read_context uuid NOT NULL,
 PRIMARY KEY(catalog_id,report_id,series_id,revision,read_context),FOREIGN KEY(catalog_id,report_id) REFERENCES automation.pilot_report_ppa_inputs(catalog_id,report_id),
 FOREIGN KEY(catalog_id,series_id,revision) REFERENCES market_data.interval_series_revisions(catalog_id,series_id,revision),
 FOREIGN KEY(catalog_id,read_context) REFERENCES market_data.temporal_contexts(catalog_id,id));
ALTER TABLE market_data.shaped_curves ADD UNIQUE(catalog_id,revision_id,code,shape_series_id);
CREATE TABLE automation.pilot_report_ppa_shapes(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,curve_code ref_data.code NOT NULL,definition_revision_id uuid NOT NULL,series_id uuid NOT NULL,revision integer NOT NULL,
 PRIMARY KEY(catalog_id,report_id,curve_code,series_id,revision),FOREIGN KEY(catalog_id,report_id) REFERENCES automation.pilot_report_ppa_inputs(catalog_id,report_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,series_id) REFERENCES market_data.shaped_curves(catalog_id,revision_id,code,shape_series_id),
 FOREIGN KEY(catalog_id,series_id,revision) REFERENCES market_data.interval_series_revisions(catalog_id,series_id,revision));
ALTER TABLE power_data.batches ADD UNIQUE(catalog_id,id);
CREATE TABLE automation.pilot_report_ppa_actuals(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),batch_id uuid NOT NULL,revision integer CHECK(revision>0),known_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,report_id,ordinal),UNIQUE NULLS NOT DISTINCT(catalog_id,report_id,batch_id,revision),FOREIGN KEY(catalog_id,report_id) REFERENCES automation.pilot_report_ppa_inputs(catalog_id,report_id),
 FOREIGN KEY(catalog_id,batch_id) REFERENCES power_data.batches(catalog_id,id));
CREATE TABLE automation.pilot_report_ppa_balances(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,book_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 known_n text NOT NULL,known_d text NOT NULL,estimated_n text NOT NULL,estimated_d text NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,report_id,book_id,currency_code),
 FOREIGN KEY(catalog_id,report_id) REFERENCES automation.pilot_report_ppa_inputs(catalog_id,report_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 CHECK(automation.exact_report_numbers(ARRAY[known_n,estimated_n],ARRAY[known_d,estimated_d])));
CREATE TABLE automation.pilot_report_ppa_assets(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,definition_revision_id uuid NOT NULL,asset_code ref_data.code NOT NULL,effective_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,report_id,asset_code,effective_from),FOREIGN KEY(catalog_id,report_id) REFERENCES automation.pilot_report_ppa_inputs(catalog_id,report_id),
 FOREIGN KEY(catalog_id,definition_revision_id,asset_code,effective_from) REFERENCES market_data.generation_asset_versions(catalog_id,revision_id,asset_code,effective_from));
CREATE OR REPLACE FUNCTION security.pilot_report_sources_visible(catalog uuid,report uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.has_permission('ReadTrades') AND NOT EXISTS(
 SELECT 1 FROM(
 SELECT p.catalog_id,p.publication_order,coalesce(f.effective_at,w.effective_at,v.effective_at) AS effective_at
 FROM automation.pilot_report_positions p
 LEFT JOIN trading.trade_revisions f ON(f.catalog_id,f.trade_id,f.revision)=(p.catalog_id,p.trade_id,p.future_revision)
 LEFT JOIN trading.swap_trade_revisions w ON(w.catalog_id,w.trade_id,w.revision)=(p.catalog_id,p.trade_id,p.swap_revision)
 LEFT JOIN trading.ppa_trade_revisions v ON(v.catalog_id,v.trade_id,v.revision)=(p.catalog_id,p.trade_id,p.ppa_revision)
 WHERE(p.catalog_id,p.report_id)=(s.catalog_id,s.id)
 UNION SELECT d.catalog_id,m.publication_order,m.effective_at FROM automation.pilot_report_discounts d
 JOIN trading.currency_discount_defaults m ON(m.catalog_id,m.id)=(d.catalog_id,d.mapping_id) WHERE(d.catalog_id,d.report_id)=(s.catalog_id,s.id)
 UNION SELECT b.catalog_id,b.publication_order,b.effective_at FROM automation.pilot_report_positions p
 JOIN trading.books b ON(b.catalog_id,b.id)=(p.catalog_id,p.book_id) WHERE(p.catalog_id,p.report_id)=(s.catalog_id,s.id)) p
 WHERE p.effective_at>c.at OR NOT EXISTS(SELECT 1 FROM security.request_contexts q
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND(q.market_context_id IS NULL OR EXISTS(
 SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id) AND p.publication_order<=x.known_through AND k.known_from<=t.known_at))))
 FROM automation.pilot_reports s JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(s.catalog_id,s.close_id) WHERE(s.catalog_id,s.id)=(catalog,report);
$$;
CREATE OR REPLACE FUNCTION automation.publish_pilot_report(expected_run uuid,payload jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;p automation.pilot_close_profiles;c market_data.market_closes;q security.request_contexts;
 previous automation.pilot_reports;ordinal integer;digest text;rp automation.close_risk_profiles;report_mode text;
BEGIN
 r=automation.lock_pilot_run();PERFORM pg_advisory_xact_lock(730849102);r=automation.lock_pilot_run();
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version)=(j.pilot_profile_id,j.pilot_profile_version);
 IF j.operation='close_book_risk_v1' THEN
  SELECT * INTO rp FROM automation.close_risk_profiles WHERE id=j.close_risk_profile_id;
  SELECT * INTO c FROM market_data.market_closes WHERE(catalog_id,id)=(rp.catalog_id,rp.close_id);
  report_mode='existing_close';
 ELSE
  IF automation.unsafe_pilot_ppa(j) THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
  SELECT * INTO c FROM market_data.market_closes WHERE(catalog_id,id,run_id,kind)=(p.catalog_id,r.id,r.id,'generated_pilot');
  report_mode='generated_close';
 END IF;
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF r.id IS DISTINCT FROM expected_run OR c.id IS NULL OR(payload->>'snapshotId')::uuid IS DISTINCT FROM c.id
 OR(payload->>'contextId')::uuid IS DISTINCT FROM q.market_context_id OR payload->'policy' IS DISTINCT FROM (CASE WHEN report_mode='existing_close' THEN rp.valuation_policy ELSE p.valuation_policy END)
 OR(report_mode='existing_close' AND q.market_context_id IS DISTINCT FROM rp.context_id)
 OR jsonb_typeof(payload->'models') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'models') NOT BETWEEN 1 AND 20000
 OR jsonb_typeof(payload->'positions') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'positions') NOT BETWEEN 1 AND 100000
 OR jsonb_typeof(payload->'factors') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'factors')>4096
 OR jsonb_typeof(payload->'totals') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'totals') NOT BETWEEN 1 AND 256
 OR jsonb_typeof(payload->'risk') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'risk')>1048576
 OR jsonb_typeof(payload->'discounts') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'discounts')>40000
 THEN RAISE EXCEPTION 'pilot_report_input' USING ERRCODE='P0001';END IF;
 PERFORM security.require_bound_market_context(c.catalog_id,q.market_context_id,c.at);
 IF NOT security.market_close_visible(c.catalog_id,c.id) THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
 digest=encode(sha256(convert_to(payload::text,'UTF8')),'hex');
 SELECT * INTO previous FROM automation.pilot_reports WHERE id=r.id;
 IF FOUND THEN
  IF previous.request_sha256<>digest THEN RAISE EXCEPTION 'pilot_report_conflict' USING ERRCODE='P0001';END IF;
  r=automation.lock_pilot_run();PERFORM security.require_bound_market_context(c.catalog_id,q.market_context_id,c.at);
  IF NOT security.market_close_visible(c.catalog_id,c.id) THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
  RETURN r.id;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM automation.pilot_reports WHERE catalog_id=c.catalog_id;
 INSERT INTO automation.pilot_reports VALUES(c.catalog_id,r.id,r.id,c.id,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,q.market_context_id,digest,payload->'policy',payload->'work',q.actor_id,ordinal,pg_current_xact_id(),report_mode);
 INSERT INTO automation.pilot_report_input_pins
 SELECT c.catalog_id,r.id,c.id,x.observation,x.input_use FROM(
 SELECT observation,'variable'::text input_use FROM jsonb_to_recordset(payload->'factors')f(observation uuid)
 UNION SELECT observation,input_use FROM jsonb_to_recordset(payload->'discountObservations')d(observation uuid,input_use text))x;
 INSERT INTO automation.pilot_report_models SELECT c.catalog_id,r.id,x.id,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.model,x.quantity,x.currency,x.future,x.swap,t.futures_series_code,x.ppa
 FROM jsonb_to_recordset(payload->'models')x(id uuid,model text,quantity text,currency text,future uuid,swap uuid,ppa uuid)
 LEFT JOIN market_data.contracts t ON(t.catalog_id,t.revision_id,t.id)=(c.catalog_id,c.definition_revision_id,x.future);
 INSERT INTO automation.pilot_report_dependencies SELECT c.catalog_id,r.id,x.model,c.definition_revision_id,x.curve,x.contract
 FROM jsonb_to_recordset(payload->'dependencies')x(model uuid,curve text,contract uuid);
 INSERT INTO automation.pilot_report_positions(catalog_id,report_id,ordinal,trade_id,future_revision,swap_revision,book_id,binding_id,instrument_id,publication_order,valuation_id,definition_revision_id,reference_catalog_id,reference_revision_id,quantity,quantity_code,ppa_revision,coverage_from,coverage_to)
 SELECT c.catalog_id,r.id,x.ordinal,x.trade,x.future,x.swap,x.book,x.binding,x.instrument,x.publication,x.valuation,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.quantity,x.unit,x.ppa,x.coverage_from,x.coverage_to
 FROM jsonb_to_recordset(payload->'positions')x(ordinal integer,trade uuid,future integer,swap integer,book uuid,binding uuid,instrument uuid,publication integer,valuation uuid,quantity numeric,unit text,ppa integer,coverage_from timestamptz,coverage_to timestamptz);
 INSERT INTO automation.pilot_report_factors SELECT c.catalog_id,r.id,x.ordinal,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.series,x.observation,'variable',x.contract,x.quote,x.currency,x.quantity,x.source,x.role,x.base,x.bump,x.tick
 FROM jsonb_to_recordset(payload->'factors')x(ordinal integer,series uuid,observation uuid,contract uuid,quote text,currency text,quantity text,source text,role text,base numeric,bump numeric,tick numeric);
 INSERT INTO automation.pilot_report_totals SELECT c.catalog_id,r.id,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.measure,x.book,x.currency,x.n,x.d
 FROM jsonb_to_recordset(payload->'totals')x(measure text,book uuid,currency text,n text,d text);
 INSERT INTO automation.pilot_report_risk SELECT c.catalog_id,r.id,x.measure,x.book,x.currency,x.factor,x.up_n,x.up_d,x.down_n,x.down_d,x.up_pnl_n,x.up_pnl_d,x.down_pnl_n,x.down_pnl_d,x.delta_n,x.delta_d,x.gamma_n,x.gamma_d
 FROM jsonb_to_recordset(payload->'risk')x(measure text,book uuid,currency text,factor integer,up_n text,up_d text,down_n text,down_d text,up_pnl_n text,up_pnl_d text,down_pnl_n text,down_pnl_d text,delta_n text,delta_d text,gamma_n text,gamma_d text);
 INSERT INTO automation.pilot_report_discounts SELECT c.catalog_id,r.id,x.ordinal,x.instrument,x.fixed,x.floating,x.payment_ordinal,x.payment_at,x.payment_date,x.enabled,x.factor,x.mapping,x.curve,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.currency,x.rate_policy,x.interpolation_policy,x.ppa
 FROM jsonb_to_recordset(payload->'discounts')x(ordinal integer,instrument uuid,fixed uuid,floating uuid,payment_ordinal integer,payment_at timestamptz,payment_date date,enabled boolean,factor numeric,mapping uuid,curve text,currency text,rate_policy text,interpolation_policy text,ppa uuid);
 INSERT INTO automation.pilot_report_discount_observations SELECT c.catalog_id,r.id,x.discount,x.ordinal,x.observation,x.input_use,x.maturity,x.annual_rate,x.factor,x.days
 FROM jsonb_to_recordset(payload->'discountObservations')x(discount integer,ordinal integer,observation uuid,input_use text,maturity date,annual_rate text,factor text,days integer);
 IF report_mode='generated_close' AND(EXISTS(SELECT 1 FROM automation.pilot_report_positions x WHERE x.report_id=r.id AND NOT EXISTS(SELECT 1 FROM automation.pilot_close_books b WHERE(b.profile_id,b.profile_version,b.book_id)=(p.id,p.version,x.book_id)))
 OR EXISTS(SELECT 1 FROM automation.pilot_report_totals x WHERE x.report_id=r.id AND NOT EXISTS(SELECT 1 FROM automation.pilot_close_books b WHERE(b.profile_id,b.profile_version,b.book_id)=(p.id,p.version,x.book_id)))
 ) THEN RAISE EXCEPTION 'pilot_report_books' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM automation.pilot_report_positions x JOIN automation.pilot_report_models m ON(m.catalog_id,m.report_id,m.id)=(x.catalog_id,x.report_id,x.valuation_id)
 WHERE x.report_id=r.id AND(m.quantity_code<>x.quantity_code OR
 (x.future_revision IS NOT NULL AND(m.future_contract_id IS NULL OR NOT EXISTS(SELECT 1 FROM trading.futures_revision_markets f
 WHERE(f.catalog_id,f.trade_id,f.revision,f.instrument_id)=(x.catalog_id,x.trade_id,x.future_revision,x.instrument_id) AND(f.definition_revision_id,f.contract_id)=(m.definition_revision_id,m.future_contract_id)))) OR
 (x.swap_revision IS NOT NULL AND m.swap_instrument_id IS DISTINCT FROM x.instrument_id) OR(x.ppa_revision IS NOT NULL AND m.ppa_instrument_id IS DISTINCT FROM x.instrument_id)))
 THEN RAISE EXCEPTION 'pilot_report_model' USING ERRCODE='P0001';END IF;
 IF report_mode='existing_close' AND EXISTS(
 WITH expected AS(SELECT x.kind,x.trade_id,x.revision,x.binding_id FROM jsonb_to_recordset(automation.close_risk_scope(c.catalog_id,rp.context_id,c.at,ARRAY(SELECT book_id FROM automation.close_risk_books WHERE profile_id=rp.id)))x(kind text,trade_id uuid,revision integer,binding_id uuid)),
 actual AS(SELECT CASE WHEN future_revision IS NOT NULL THEN 'future' WHEN swap_revision IS NOT NULL THEN 'swap' ELSE 'ppa' END AS kind,trade_id,coalesce(future_revision,swap_revision,ppa_revision) AS revision,binding_id FROM automation.pilot_report_positions WHERE report_id=r.id)
 (SELECT * FROM expected EXCEPT SELECT * FROM actual) UNION ALL(SELECT * FROM actual EXCEPT SELECT * FROM expected))
 THEN RAISE EXCEPTION 'pilot_report_input';END IF;
 IF report_mode='existing_close' AND(
 EXISTS(SELECT 1 FROM automation.pilot_report_positions x WHERE x.report_id=r.id AND NOT EXISTS(SELECT 1 FROM automation.close_risk_books b WHERE(b.profile_id,b.book_id)=(rp.id,x.book_id))) OR
 EXISTS(SELECT 1 FROM automation.pilot_report_totals x WHERE x.report_id=r.id AND NOT EXISTS(SELECT 1 FROM automation.close_risk_books b WHERE(b.profile_id,b.book_id)=(rp.id,x.book_id))) OR
 EXISTS((SELECT series_id,observation_id,quote_code,bump FROM automation.close_risk_factors WHERE profile_id=rp.id EXCEPT SELECT series_id,observation_id,quote_code,bump_size FROM automation.pilot_report_factors WHERE report_id=r.id) UNION ALL
 (SELECT series_id,observation_id,quote_code,bump_size FROM automation.pilot_report_factors WHERE report_id=r.id EXCEPT SELECT series_id,observation_id,quote_code,bump FROM automation.close_risk_factors WHERE profile_id=rp.id)))
 THEN RAISE EXCEPTION 'pilot_report_input';END IF;
 IF payload->'ppaReceipt' IS NOT NULL AND payload->'ppaReceipt'<>'null'::jsonb THEN
  IF report_mode<>'existing_close' OR(payload->'ppaReceipt'->>'knownAt')::timestamptz IS DISTINCT FROM rp.known_at OR(payload->'ppaReceipt'->>'knowledgeContext')::uuid IS DISTINCT FROM rp.context_id
   OR payload->'ppaReceipt'->>'pendingActuals' IS DISTINCT FROM rp.pending_actuals OR payload->'ppaReceipt'->>'coverage' IS DISTINCT FROM rp.coverage_policy THEN RAISE EXCEPTION 'pilot_report_input';END IF;
  INSERT INTO automation.pilot_report_ppa_inputs VALUES(c.catalog_id,r.id,rp.known_at,rp.context_id,rp.pending_actuals,rp.coverage_policy);
  INSERT INTO automation.pilot_report_ppa_quantities SELECT c.catalog_id,r.id,x.series,x.revision,x.known_at,x.context FROM jsonb_to_recordset(payload->'ppaReceipt'->'quantities')x(series uuid,revision integer,known_at timestamptz,context uuid);
  INSERT INTO automation.pilot_report_ppa_shapes SELECT c.catalog_id,r.id,x.curve,c.definition_revision_id,x.series,x.revision FROM jsonb_to_recordset(payload->'ppaReceipt'->'shapes')x(curve text,series uuid,revision integer);
  INSERT INTO automation.pilot_report_ppa_actuals SELECT c.catalog_id,r.id,(row_number() OVER(ORDER BY x.batch,x.revision)-1)::integer,x.batch,x.revision,x.known_at FROM jsonb_to_recordset(payload->'ppaReceipt'->'actuals')x(batch uuid,revision integer,known_at timestamptz);
  IF EXISTS(SELECT 1 FROM automation.pilot_report_ppa_actuals a WHERE a.report_id=r.id AND NOT EXISTS(SELECT 1 FROM power_data.batches b JOIN power_data.receipts k USING(sequence) JOIN power_data.prices v ON v.batch_id=b.id WHERE(b.catalog_id,b.id,k.known_at)=(a.catalog_id,a.batch_id,a.known_at) AND v.provider_revision IS NOT DISTINCT FROM a.revision)) THEN RAISE EXCEPTION 'pilot_report_input';END IF;
  IF EXISTS(SELECT 1 FROM automation.pilot_report_ppa_quantities a WHERE a.report_id=r.id AND NOT EXISTS(SELECT 1 FROM market_data.interval_series_revisions v JOIN market_data.interval_series_knowledge_receipts k USING(catalog_id,publication_order,publication_xid) WHERE(v.catalog_id,v.series_id,v.revision,k.known_from)=(a.catalog_id,a.series_id,a.revision,a.known_at))) THEN RAISE EXCEPTION 'pilot_report_input';END IF;
  INSERT INTO automation.pilot_report_ppa_assets SELECT c.catalog_id,r.id,c.definition_revision_id,x.asset,x.effective_from FROM jsonb_to_recordset(payload->'ppaReceipt'->'assets')x(asset text,effective_from timestamptz);
  INSERT INTO automation.pilot_report_ppa_balances SELECT c.catalog_id,r.id,x.book,x.currency,x.known_n,x.known_d,x.estimated_n,x.estimated_d,c.reference_catalog_id,c.reference_revision_id FROM jsonb_to_recordset(payload->'ppaBalances')x(book uuid,currency text,known_n text,known_d text,estimated_n text,estimated_d text);
 END IF;
 IF security.pilot_report_sources_visible(c.catalog_id,r.id) IS NOT TRUE
 THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
 r=automation.lock_pilot_run();PERFORM security.require_bound_market_context(c.catalog_id,q.market_context_id,c.at);
 RETURN r.id;
END;$$;
CREATE OR REPLACE FUNCTION automation.read_pilot_report(report uuid,expected_context uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 PERFORM automation.require_pilot_report_view(report,expected_context);
 -- One MVCC statement, fixed rowsets, no dynamic relation names. The definer
 -- avoids running the full source graph authority check for every output row.
 SELECT jsonb_build_object(
 'header',to_jsonb(s)||jsonb_build_object('close_name',c.name,'close_at',c.at,'close_kind',c.kind,'effective_on',t.effective_on,'known_through',t.known_through,'known_at',t.known_at),
 'books',coalesce((SELECT jsonb_agg(x ORDER BY x.id) FROM(SELECT DISTINCT b.id,b.name FROM automation.pilot_report_positions p JOIN trading.books b ON(b.catalog_id,b.id)=(p.catalog_id,p.book_id) WHERE(p.catalog_id,p.report_id)=(s.catalog_id,s.id))x),'[]'::jsonb),
 'positions',coalesce((SELECT jsonb_agg(x ORDER BY x.ordinal) FROM automation.pilot_report_positions x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'totals',coalesce((SELECT jsonb_agg(to_jsonb(x)||jsonb_build_object('currency_label',m.display_code) ORDER BY x.measure,x.book_id,x.currency_code) FROM automation.pilot_report_totals x JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(x.reference_catalog_id,x.reference_revision_id,x.currency_code) WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'factors',coalesce((SELECT jsonb_agg(to_jsonb(f)||jsonb_build_object('series_code',z.code,'delivery_from',d.delivery_from,'delivery_to',d.delivery_to,'unit_label',u.symbol,'currency_label',m.display_code,'multiplier',q.multiplier::text) ORDER BY f.ordinal)
 FROM automation.pilot_report_factors f JOIN market_data.series z ON(z.catalog_id,z.revision_id,z.id)=(f.catalog_id,f.definition_revision_id,f.series_id)
 JOIN market_data.contracts d ON(d.catalog_id,d.revision_id,d.id)=(f.catalog_id,f.definition_revision_id,f.contract_id)
 JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(f.reference_catalog_id,f.reference_revision_id,f.quote_code)
 JOIN ref_data.quantity_specifications v ON(v.catalog_id,v.revision_id,v.code)=(f.reference_catalog_id,f.reference_revision_id,f.quantity_code)
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(v.catalog_id,v.revision_id,v.unit_code)
 JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(f.reference_catalog_id,f.reference_revision_id,f.currency_code)
 WHERE(f.catalog_id,f.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'risk',coalesce((SELECT jsonb_agg(to_jsonb(x)||jsonb_build_object('currency_label',m.display_code) ORDER BY x.measure,x.book_id,x.currency_code,x.factor_ordinal)
 FROM automation.pilot_report_risk x JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(s.reference_catalog_id,s.reference_revision_id,x.currency_code) WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'models',coalesce((SELECT jsonb_agg(x ORDER BY x.id) FROM automation.pilot_report_models x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'dependencies',coalesce((SELECT jsonb_agg(x ORDER BY x.model_id,x.curve_code,x.contract_id) FROM automation.pilot_report_dependencies x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'discounts',coalesce((SELECT jsonb_agg(x ORDER BY x.ordinal) FROM automation.pilot_report_discounts x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'ppaInputs',(SELECT to_jsonb(x) FROM automation.pilot_report_ppa_inputs x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),
 'ppaQuantities',coalesce((SELECT jsonb_agg(x ORDER BY series_id,revision,read_context) FROM automation.pilot_report_ppa_quantities x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'ppaShapes',coalesce((SELECT jsonb_agg(x ORDER BY curve_code,series_id,revision) FROM automation.pilot_report_ppa_shapes x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'ppaActuals',coalesce((SELECT jsonb_agg(x ORDER BY batch_id) FROM automation.pilot_report_ppa_actuals x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'ppaAssets',coalesce((SELECT jsonb_agg(x ORDER BY asset_code,effective_from) FROM automation.pilot_report_ppa_assets x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'ppaBalances',coalesce((SELECT jsonb_agg(x ORDER BY book_id,currency_code) FROM automation.pilot_report_ppa_balances x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'discountObservations',coalesce((SELECT jsonb_agg(x ORDER BY x.discount_ordinal,x.ordinal) FROM automation.pilot_report_discount_observations x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb))
 INTO result FROM automation.pilot_reports s JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(s.catalog_id,s.close_id)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(s.catalog_id,s.context_id) WHERE s.id=report;
 PERFORM automation.require_pilot_report_view(report,expected_context);
 RETURN result;
END;$$;
CREATE OR REPLACE FUNCTION automation.finish_run(run_id uuid,expected_fence bigint,worker_id uuid,succeeded boolean,error_code text,next_due_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;
BEGIN
 PERFORM automation.finish_pre_pilot_report(run_id,expected_fence,worker_id,succeeded,error_code,next_due_at);
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=finish_run.run_id;
 IF j.operation IN('generated_pilot_close_v1','close_book_risk_v1') THEN
  w=automation.worker();
  SELECT * INTO r FROM automation.runs WHERE id=finish_run.run_id;
  IF r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM finish_run.worker_id OR r.fence IS DISTINCT FROM expected_fence
  OR r.lease_until<=clock_timestamp() OR r.deadline_at<=clock_timestamp() OR j.expires_at<=clock_timestamp()
  OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,w.principal_id)
  THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
  IF succeeded AND j.operation='generated_pilot_close_v1' AND NOT EXISTS(SELECT 1 FROM automation.pilot_reports s JOIN market_data.market_closes c ON(c.catalog_id,c.id,c.run_id)=(s.catalog_id,s.close_id,s.run_id)
   WHERE s.id=finish_run.run_id AND c.kind='generated_pilot') THEN RAISE EXCEPTION 'pilot_report_required' USING ERRCODE='42501';END IF;
  IF succeeded AND j.operation='close_book_risk_v1' AND NOT EXISTS(SELECT 1 FROM automation.pilot_reports s WHERE s.id=finish_run.run_id AND s.mode='existing_close') THEN RAISE EXCEPTION 'pilot_report_required' USING ERRCODE='42501';END IF;
 END IF;
END;$$;
CREATE OR REPLACE FUNCTION automation.pilot_run_definition() RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;j automation.jobs;payload jsonb;
BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF q.id IS NULL OR NOT automation.pilot_context_live(q.id) OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs r ON r.job_id=jobs.id JOIN automation.pilot_run_contexts p ON p.run_id=r.id WHERE p.context_id=q.id;
 IF j.operation<>'generated_pilot_close_v1' OR automation.unsafe_pilot_ppa(j) THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 payload=automation.pilot_definition(j);
 IF q.id IS NULL OR NOT automation.pilot_context_live(q.id) OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 RETURN payload;
END;$$;
CREATE OR REPLACE FUNCTION automation.publish_pilot_close(marks jsonb,carried jsonb,members jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;p automation.pilot_close_profiles;q security.request_contexts;
 previous market_data.market_closes;ordinal integer;identity uuid;mark_row record;observation uuid;payload jsonb;
BEGIN
 r=automation.lock_pilot_run();
 IF NOT EXISTS(SELECT 1 FROM automation.jobs job WHERE job.id=r.job_id AND job.operation='generated_pilot_close_v1' AND NOT automation.unsafe_pilot_ppa(job)) THEN RAISE EXCEPTION 'job_operation' USING ERRCODE='42501';END IF;PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version)=(j.pilot_profile_id,j.pilot_profile_version);
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 payload=jsonb_build_object('profileId',p.id,'profileVersion',p.version,'seed',p.seed,'marks',marks,'carried',carried,'members',members);
 SELECT * INTO previous FROM market_data.market_closes WHERE run_id=r.id;
 IF FOUND THEN
  IF previous.request<>payload THEN RAISE EXCEPTION 'pilot_close_conflict';END IF;
  IF NOT security.market_close_visible(p.catalog_id,previous.id) THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
  RETURN previous.id;
 END IF;
 PERFORM security.require_bound_market_context(p.catalog_id,q.market_context_id,r.due_at);
 IF jsonb_typeof(marks) IS DISTINCT FROM 'array' OR jsonb_array_length(marks) NOT BETWEEN 1 AND 20000
 OR jsonb_typeof(carried) IS DISTINCT FROM 'array' OR jsonb_array_length(carried)>20000
 OR jsonb_typeof(members) IS DISTINCT FROM 'array' OR jsonb_array_length(members) NOT BETWEEN 1 AND 20000
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(marks)x("seriesId" uuid,"anchorObservationId" uuid,"moveTicks" integer,value numeric)
 LEFT JOIN market_data.market_close_pins a ON(a.catalog_id,a.close_id,a.series_id,a.observation_id,a.input_use)=(p.catalog_id,p.prior_close_id,x."seriesId",x."anchorObservationId",'variable')
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
 LEFT JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(a.catalog_id,a.definition_revision_id,a.series_id)
 LEFT JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
 LEFT JOIN ref_data.quote_specifications qs ON(qs.catalog_id,qs.revision_id,qs.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
 LEFT JOIN ref_data.sources src ON(src.catalog_id,src.revision_id,src.code)=(o.reference_catalog_id,o.reference_revision_id,o.source_code)
 LEFT JOIN LATERAL(SELECT min(m.maximum_ticks) maximum_ticks,count(DISTINCT m.maximum_ticks) distinct_bounds FROM automation.pilot_close_moves m
  JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.curve_code)=(m.catalog_id,m.definition_revision_id,m.curve_code)
  JOIN market_data.curves cv ON(cv.catalog_id,cv.revision_id,cv.code)=(cm.catalog_id,cm.revision_id,cm.curve_code)
  WHERE(m.profile_id,m.profile_version)=(p.id,p.version) AND cv.rule='direct' AND cm.contract_id=s.contract_id AND cv.quote_code=s.quote_code)bounds ON true
 WHERE a.close_id IS NULL OR o.role NOT IN('trader_mark','daily_settlement') OR o.publication_state<>'final' OR src.kind<>'synthetic'
 OR k.expiry_at<=r.due_at OR x."moveTicks" IS NULL OR x.value IS NULL OR x.value<>o.value+x."moveTicks"*qs.clearing_increment OR o.source_code<>p.source_code
 OR bounds.maximum_ticks IS NULL OR bounds.distinct_bounds<>1 OR abs(x."moveTicks")>bounds.maximum_ticks)
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(carried)x("seriesId" uuid,"observationId" uuid,"inputUse" text)
 LEFT JOIN market_data.market_close_pins a ON(a.catalog_id,a.close_id,a.series_id,a.observation_id,a.input_use)=(p.catalog_id,p.prior_close_id,x."seriesId",x."observationId",x."inputUse")
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
 WHERE a.close_id IS NULL OR NOT(o.role='parameter' AND x."inputUse"='variable' OR o.role IN('final_contract_settlement','index_fixing') AND o.publication_state='final' AND x."inputUse"='frozen'))
 OR EXISTS(WITH expected AS(
  SELECT a.series_id,a.observation_id,a.input_use FROM market_data.market_close_pins a
  JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
  WHERE(a.catalog_id,a.close_id)=(p.catalog_id,p.prior_close_id) AND(o.role='parameter' OR a.input_use='frozen'))
  SELECT 1 FROM expected e FULL JOIN jsonb_to_recordset(carried)x("seriesId" uuid,"observationId" uuid,"inputUse" text)
  ON(e.series_id,e.observation_id,e.input_use)=(x."seriesId",x."observationId",x."inputUse")
  WHERE e.observation_id IS NULL OR x."observationId" IS NULL)
 OR EXISTS(SELECT 1 FROM automation.pilot_close_curves selected WHERE(selected.profile_id,selected.profile_version)=(p.id,p.version)
  AND NOT EXISTS(SELECT 1 FROM market_data.market_close_members a JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(a.catalog_id,a.definition_revision_id,a.contract_id)
   WHERE(a.catalog_id,a.close_id,a.curve_code)=(p.catalog_id,p.prior_close_id,selected.curve_code) AND k.expiry_at>r.due_at))
 OR EXISTS(WITH expected AS(
  SELECT a.curve_code AS curve,a.contract_id AS contract,(row_number() OVER(PARTITION BY a.curve_code ORDER BY a.ordinal)-1)::integer AS ordinal
  FROM automation.pilot_close_curves selected JOIN market_data.market_close_members a
   ON(a.catalog_id,a.close_id,a.curve_code)=(p.catalog_id,p.prior_close_id,selected.curve_code)
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(a.catalog_id,a.definition_revision_id,a.contract_id)
  WHERE(selected.profile_id,selected.profile_version)=(p.id,p.version) AND k.expiry_at>r.due_at)
  SELECT 1 FROM expected e FULL JOIN jsonb_to_recordset(members)x(curve text,"contractId" uuid,ordinal integer)
  ON(e.curve,e.contract)=(x.curve,x."contractId") WHERE e.ordinal IS DISTINCT FROM x.ordinal)
 THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 -- This is the sole transition from a preflight read view to publication. The
 -- run and global gate remain locked throughout the transaction. No generic
 -- context reset operation is exposed. The close retains the preflight view.
 UPDATE security.request_contexts SET market_catalog_id=NULL,market_context_id=NULL,trading_as_of=NULL,pilot_publication=true WHERE id=q.id;
 PERFORM automation.lock_pilot_run();
 identity=r.id;SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.market_closes WHERE catalog_id=p.catalog_id;
 INSERT INTO market_data.market_closes VALUES(p.catalog_id,identity,p.definition_revision_id,p.reference_catalog_id,p.reference_revision_id,
 'Generated pilot close',r.due_at,'generated_pilot',q.actor_id,r.id,payload,
 p.prior_close_id,r.id,p.generator,q.market_context_id,ordinal,pg_current_xact_id());
 FOR mark_row IN SELECT v.*,o.role FROM jsonb_to_recordset(marks)v("seriesId" uuid,"anchorObservationId" uuid,"moveTicks" integer,value numeric)
 JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,v."anchorObservationId") LOOP
  observation=gen_random_uuid();
  INSERT INTO market_data.observations VALUES(p.catalog_id,observation,p.definition_revision_id,mark_row."seriesId",p.reference_catalog_id,p.reference_revision_id,p.source_code,
   'pilot:'||r.id::text||':'||mark_row."seriesId"::text,p.generator,mark_row.role,'final',r.due_at,clock_timestamp(),mark_row.value,NULL);
  INSERT INTO market_data.market_close_pins VALUES(p.catalog_id,identity,p.definition_revision_id,mark_row."seriesId",observation,'variable');
 END LOOP;
 INSERT INTO market_data.market_close_pins SELECT p.catalog_id,identity,p.definition_revision_id,x."seriesId",x."observationId",x."inputUse" FROM jsonb_to_recordset(carried)x("seriesId" uuid,"observationId" uuid,"inputUse" text);
 INSERT INTO market_data.market_close_members SELECT p.catalog_id,identity,p.definition_revision_id,x.curve,x."contractId",x.ordinal FROM jsonb_to_recordset(members)x(curve text,"contractId" uuid,ordinal integer);
 UPDATE security.request_contexts SET pilot_publication=false WHERE id=q.id;
 PERFORM automation.lock_pilot_run();
 RETURN identity;
END;$$;
CREATE OR REPLACE FUNCTION market_data.open_interval_series_read(selected market_data.interval_series,first_at timestamptz,last_at timestamptz,cutoff timestamptz,
 OUT context_id uuid,OUT known integer[],OUT description jsonb)
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF (NOT security.current_write_context() AND NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id) WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.market_catalog_id=selected.catalog_id AND cutoff<=t.known_at AND q.expires_at>clock_timestamp())) OR selected.id IS NULL OR security.market_access(selected.catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_access(selected.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 IF first_at IS NULL OR last_at IS NULL OR cutoff IS NULL OR NOT isfinite(first_at) OR NOT isfinite(last_at) OR NOT isfinite(cutoff)
  OR first_at>=last_at OR cutoff>clock_timestamp()
  OR extract(epoch FROM last_at-first_at)>35136*60*selected.interval_minutes THEN RAISE EXCEPTION 'interval_series_window';END IF;
 -- Any stored interval counts, withdrawn or saved after the cutoff included. Every stored interval lasts
 -- the series' resolution, so one probe of the starts in the resolution before each end finds it.
 IF EXISTS(SELECT 1 FROM unnest(ARRAY[first_at,last_at]) b(at),LATERAL(SELECT 1 FROM market_data.interval_series_values v
   WHERE(v.catalog_id,v.series_id)=(selected.catalog_id,selected.id) AND v.starts_at>b.at-make_interval(mins=>selected.interval_minutes)
    AND v.starts_at<b.at AND v.ends_at>b.at LIMIT 1) inside)
 THEN RAISE EXCEPTION 'interval_series_grid';END IF;
 -- Series are not effective-dated: the retained context is the cutoff's alone, dated by its own UTC day.
 context_id=security.capture_date_context(selected.catalog_id,(cutoff AT TIME ZONE 'UTC')::date,cutoff);
 -- Certification can wait on the shared knowledge gate: recheck reference authority, as it rechecks market authority.
 IF security.reference_access(selected.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 SELECT coalesce(array_agg(r.revision ORDER BY r.revision),'{}') INTO known FROM market_data.interval_series_revisions r
 JOIN market_data.interval_series_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(r.catalog_id,r.publication_order,r.publication_xid)
 WHERE(r.catalog_id,r.series_id)=(selected.catalog_id,selected.id) AND k.known_from<=cutoff;
 description=jsonb_build_object('id',selected.id,'code',selected.code,'name',selected.name,'kind',selected.kind,
  'specification',coalesce(selected.quote_code,selected.value_code,selected.quantity_code),'clock',selected.clock_code,'intervalMinutes',selected.interval_minutes);
END$$;
-- This owner can read immutable metadata through every runtime RLS policy.
-- It is not a table owner or a runtime membership target and cannot bypass RLS.
CREATE ROLE openetrm_interval_metadata_reader NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE INHERIT NOBYPASSRLS;
GRANT openetrm_runtime TO openetrm_interval_metadata_reader;
ALTER POLICY current_read ON market_data.interval_series
 USING(security.current_write_context() OR current_user='openetrm_interval_metadata_reader');
CREATE FUNCTION market_data.read_interval_series_header(series uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected market_data.interval_series;
BEGIN
 SELECT * INTO selected FROM market_data.interval_series s WHERE s.id=series;
 -- A hidden required series must hide its source binding under the same RLS.
 IF selected.id IS NULL THEN RETURN 'null'::jsonb;END IF;
 IF NOT security.market_access(selected.catalog_id,'ReadMarketData') OR NOT security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 RETURN to_jsonb(selected);
END;$$;
-- The source's sealed definition pins both immutable series identities. A
-- bound historical read uses authorized metadata without exposing raw revisions.
DROP POLICY source_read ON market_data.volume_sources;
CREATE POLICY source_read ON market_data.volume_sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM market_data.generation_assets a
  WHERE(a.catalog_id,a.revision_id,a.code)=(volume_sources.catalog_id,volume_sources.revision_id,volume_sources.asset_code)
  AND (market_data.read_interval_series_header(volume_sources.forecast_series_id)->>'catalog_id')::uuid=a.catalog_id
  AND (market_data.read_interval_series_header(volume_sources.metered_series_id)->>'catalog_id')::uuid=a.catalog_id));
CREATE OR REPLACE FUNCTION power_data.read_prices(source uuid,first_at timestamptz,last_at timestamptz,cutoff timestamptz) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected power_data.sources;result jsonb;context_id uuid;
BEGIN
 SELECT * INTO selected FROM power_data.sources WHERE id=source;
 IF NOT security.current_write_context() AND NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id) WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.market_catalog_id=selected.catalog_id AND cutoff<=t.known_at AND q.expires_at>clock_timestamp()) THEN RAISE EXCEPTION 'collection_historical_context';END IF;
 IF selected.id IS NULL OR NOT security.market_access(selected.catalog_id,'ReadMarketData') OR NOT security.reference_access(selected.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 IF first_at IS NULL OR last_at IS NULL OR cutoff IS NULL OR NOT isfinite(first_at) OR NOT isfinite(last_at) OR NOT isfinite(cutoff) OR first_at>=last_at OR last_at-first_at>interval '31 days 1 hour' OR cutoff>clock_timestamp() THEN RAISE EXCEPTION 'collection_window';END IF;
 context_id=security.capture_date_context(selected.catalog_id,(first_at AT TIME ZONE 'UTC')::date,cutoff);
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.starts_at),'[]'::jsonb) INTO result FROM(
  SELECT DISTINCT ON(p.starts_at) p.starts_at,p.ends_at,p.price::text price,p.provider_revision,r.known_at,b.id batch_id
  FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id JOIN power_data.receipts r USING(sequence)
  WHERE p.source_id=source AND p.starts_at>=first_at AND p.starts_at<last_at AND p.ends_at<=last_at AND r.known_at<=cutoff ORDER BY p.starts_at,b.sequence DESC
 )p;
 IF NOT security.market_access(selected.catalog_id,'ReadMarketData') OR NOT security.reference_access(selected.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('source',to_jsonb(selected),'knownAt',cutoff,'contextId',context_id,'rows',result);
END$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_close_risk_context;
REVOKE ALL ON FUNCTION security.capture_pre_close_risk_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE token uuid;p automation.close_risk_profiles;q security.request_contexts;
BEGIN
 SELECT c.* INTO q FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 SELECT profile.* INTO p FROM automation.pilot_run_contexts rc JOIN automation.runs r ON r.id=rc.run_id JOIN automation.jobs j ON j.id=r.job_id JOIN automation.close_risk_profiles profile ON profile.id=j.close_risk_profile_id WHERE rc.context_id=q.id AND j.operation='close_book_risk_v1';
 IF FOUND THEN
  IF NOT automation.pilot_context_live(q.id) OR NOT security.market_access(catalog,'ReadMarketData') OR catalog<>p.catalog_id OR requested_at IS NULL OR requested_at>p.known_at THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
  SELECT t.id INTO token FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.effective_on,t.known_at)=(catalog,effective_date,requested_at);
  IF token IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
  IF NOT automation.pilot_context_live(q.id) OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
  RETURN token;
 END IF;
 RETURN security.capture_pre_close_risk_context(catalog,effective_date,requested_at);
END;$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_date_context(uuid,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['close_risk_profiles','close_risk_books','close_risk_factors','pilot_report_input_pins','pilot_report_ppa_inputs','pilot_report_ppa_quantities','pilot_report_ppa_shapes','pilot_report_ppa_actuals','pilot_report_ppa_assets','pilot_report_ppa_balances'] LOOP
  EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY owner_access ON automation.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON automation.%I TO openetrm_security_owner',t);
  IF t LIKE 'pilot_report_ppa_%%' THEN
   EXECUTE format('GRANT SELECT ON automation.%I TO openetrm_runtime',t);
   EXECUTE format('CREATE POLICY scoped_read ON automation.%I FOR SELECT TO openetrm_runtime USING(security.pilot_report_visible(catalog_id,report_id))',t);
  END IF;
 END LOOP;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE(n.nspname='automation' AND p.proname IN('close_risk_authorized','close_risk_replay','create_close_risk_job','close_risk_run_definition','unsafe_pilot_ppa','claim_next')) OR(n.nspname='market_data' AND p.proname='read_interval_series_header') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO %I',f.signature,CASE WHEN f.proname='read_interval_series_header' THEN 'openetrm_interval_metadata_reader' ELSE 'openetrm_security_owner' END);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname='claim_next' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_scheduler',f.signature);
  ELSIF f.proname IN('close_risk_replay','create_close_risk_job','close_risk_run_definition','read_interval_series_header') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
