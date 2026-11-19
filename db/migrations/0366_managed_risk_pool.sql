-- One installation-approved existing Swarm service per cluster. No roles receive these permissions automatically.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

-- Preserve the approved worker admission, heartbeat and inventory contracts,
-- including the direct execution privileges of each public entry point.
DO $guard$
DECLARE predecessor record; p pg_proc;
BEGIN
 FOR predecessor IN SELECT * FROM (VALUES
  ('risk.claim(integer,text)','4cd88b3b67a7ebfae66178078bad63ca',ARRAY['lease_seconds','engine_version'],'jsonb','openetrm_risk_worker'),
  ('risk.heartbeat(uuid,uuid,integer)','81c395214624d8d083769eec0efc3e58',ARRAY['job_id','token','lease_seconds'],'text','openetrm_risk_worker'),
  ('automation.observe_nodes(text,jsonb)','1dc454f54741e19b33853c60cc9a98f6',ARRAY['cluster_id','nodes'],'void','openetrm_scheduler')
 ) AS helpers(signature,digest,arguments,returns,grantee)
 LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(predecessor.signature);
  IF NOT FOUND OR md5(p.prosrc)<>predecessor.digest OR p.proargnames IS DISTINCT FROM predecessor.arguments
   OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>predecessor.returns::regtype
   OR p.prokind<>'f' OR p.proretset OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.pronargdefaults<>0
   OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
   OR (SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proacl::text IS DISTINCT FROM format('{openetrm_security_owner=X/openetrm_security_owner,%s=X/openetrm_security_owner}',predecessor.grantee)
  THEN RAISE EXCEPTION 'managed_pool_upgrade: unexpected helper %',predecessor.signature; END IF;
 END LOOP;
 IF to_regprocedure('risk.claim_unmanaged(integer,text)') IS NOT NULL
  OR to_regprocedure('risk.heartbeat_before_pool(uuid,uuid,integer)') IS NOT NULL
 THEN RAISE EXCEPTION 'managed_pool_upgrade: helper alias already exists'; END IF;
END$guard$;

INSERT INTO security.permissions VALUES ('ScaleWorkerPool'),('ViewWorkerDiagnostics');
CREATE TABLE automation.risk_pools(
 cluster_id text PRIMARY KEY REFERENCES automation.clusters(id), worker_login name UNIQUE NOT NULL REFERENCES risk.workers(login),
 service_id text NOT NULL CHECK(service_id ~ '^[a-z0-9]{12,64}$'), approved_spec jsonb NOT NULL CHECK(jsonb_typeof(approved_spec)='object'),
 maximum integer NOT NULL CHECK(maximum BETWEEN 1 AND 2),desired integer NOT NULL DEFAULT 0 CHECK(desired BETWEEN 0 AND maximum),
 observed integer CHECK(observed>=0), generation bigint NOT NULL DEFAULT 0, requested_by uuid REFERENCES security.principals(id),
 reconciled bigint, diagnostic text, observed_at timestamptz);
CREATE TABLE automation.pool_requests(
 actor uuid REFERENCES security.principals(id),request_id uuid,cluster_id text REFERENCES automation.risk_pools(cluster_id),
 expected bigint NOT NULL, desired integer NOT NULL, PRIMARY KEY(actor,request_id));
CREATE TABLE automation.pool_tasks(
 cluster_id text REFERENCES automation.risk_pools(cluster_id),task_id text,node_id text NOT NULL,slot integer NOT NULL CHECK(slot BETWEEN 1 AND 2),
 engine text,seen timestamptz, fresh_until timestamptz, job_id uuid REFERENCES risk.jobs(id),attempt_number integer, PRIMARY KEY(cluster_id,task_id));
CREATE TABLE automation.pool_logs(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,cluster_id text REFERENCES automation.clusters(id),stream text NOT NULL,
 source text NOT NULL,message text NOT NULL,at timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE INDEX ON automation.pool_logs(cluster_id,stream,id DESC);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['risk_pools','pool_requests','pool_tasks','pool_logs'] LOOP
 EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t);
 EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t);
 EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON automation.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;
GRANT USAGE ON SEQUENCE automation.pool_logs_id_seq TO openetrm_security_owner;
CREATE FUNCTION automation.pool_log(cluster text,stream_name text,source_name text,msg text) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 INSERT INTO automation.pool_logs(cluster_id,stream,source,message) VALUES(cluster,stream_name,source_name,msg);
 DELETE FROM automation.pool_logs WHERE cluster_id=cluster AND stream=stream_name AND id <=
 (SELECT id FROM automation.pool_logs WHERE cluster_id=cluster AND stream=stream_name ORDER BY id DESC OFFSET 200 LIMIT 1);
END$$;
CREATE FUNCTION automation.can_scale(who uuid,tenant uuid,workspace uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.principal_has_permission(who,tenant,workspace,'ScaleWorkerPool',clock_timestamp()) AND EXISTS(
 SELECT FROM automation.installation_operators o JOIN security.principals p ON p.id=o.principal_id
 WHERE o.principal_id=who AND p.enabled AND p.kind IN('human','service') AND o.capability='ManageInfrastructure' AND o.expires_at>clock_timestamp());
$$;
CREATE FUNCTION automation.pool_directory() RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;p automation.risk_pools;BEGIN
 c=automation.actor();
 SELECT rp.* INTO p FROM automation.risk_pools rp JOIN automation.clusters cl ON cl.id=rp.cluster_id
 JOIN risk.workers w ON w.login=rp.worker_login AND (w.tenant_id,w.workspace_id)=(cl.tenant_id,cl.workspace_id)
 WHERE cl.enabled AND (cl.tenant_id,cl.workspace_id)=(c.tenant_id,c.workspace_id);
 IF NOT FOUND THEN RETURN 'null'::jsonb;END IF;
 RETURN jsonb_build_object('clusterId',p.cluster_id,'maximum',p.maximum,'desired',p.desired,'observed',p.observed,'observedAt',p.observed_at,
 'generation',p.generation,'diagnostic',p.diagnostic,'canScale',c.delegation_id IS NULL AND security.has_permission('ScaleWorkerPool') AND automation.can_scale(c.actor_id,c.tenant_id,c.workspace_id),
 'canReadLogs',security.has_permission('ViewWorkerDiagnostics'),'tasks',coalesce((SELECT jsonb_agg(jsonb_build_object(
 'id',t.task_id,'node',coalesce(n.name,t.node_id),'engine',t.engine,'seen',t.seen,'state',CASE
 WHEN t.seen IS NULL THEN 'starting' WHEN t.fresh_until<clock_timestamp() THEN 'stale'
 WHEN t.job_id IS NOT NULL THEN 'busy' WHEN p.desired=0 OR p.reconciled IS DISTINCT FROM p.generation THEN 'draining' ELSE 'idle' END)
 ORDER BY t.slot,t.task_id) FROM automation.pool_tasks t LEFT JOIN automation.nodes n ON(n.cluster_id,n.id)=(t.cluster_id,t.node_id)
 WHERE t.cluster_id=p.cluster_id),'[]'));
END$$;
CREATE FUNCTION automation.pool_logs_read(kind text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;BEGIN
 c=automation.actor();
 IF NOT security.has_permission('ViewWorkerDiagnostics') THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 IF kind NOT IN('worker','node') THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.id DESC),'[]') INTO result FROM(SELECT l.id,l.source,l.message,l.at
 FROM automation.pool_logs l JOIN automation.clusters cl ON cl.id=l.cluster_id WHERE cl.enabled AND(cl.tenant_id,cl.workspace_id)=(c.tenant_id,c.workspace_id)
 AND l.stream=kind ORDER BY l.id DESC LIMIT 100)x;
 IF NOT security.has_permission('ViewWorkerDiagnostics') THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION automation.scale_pool(cluster text,request uuid,expected bigint,replicas integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;p automation.risk_pools;old automation.pool_requests;BEGIN
 c=automation.actor();
 SELECT rp.* INTO p FROM automation.risk_pools rp JOIN automation.clusters cl ON cl.id=rp.cluster_id
 WHERE cl.id=cluster AND cl.enabled AND(cl.tenant_id,cl.workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE OF rp;
 IF NOT FOUND OR NOT EXISTS(SELECT FROM risk.workers w WHERE w.login=p.worker_login AND(w.tenant_id,w.workspace_id)=(c.tenant_id,c.workspace_id)) OR c.delegation_id IS NOT NULL OR NOT security.has_permission('ScaleWorkerPool') OR NOT automation.can_scale(c.actor_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'infrastructure_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO old FROM automation.pool_requests WHERE actor=c.actor_id AND request_id=request;
 IF FOUND THEN
 IF(old.cluster_id,old.expected,old.desired) IS DISTINCT FROM(cluster,expected,replicas) THEN RAISE EXCEPTION 'infrastructure_conflict';END IF;
 ELSE
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR replicas IS NULL OR replicas NOT BETWEEN 0 AND p.maximum THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 IF expected IS DISTINCT FROM p.generation THEN RAISE EXCEPTION 'infrastructure_conflict';END IF;
 INSERT INTO automation.pool_requests VALUES(c.actor_id,request,cluster,expected,replicas);
 UPDATE automation.risk_pools SET generation=generation+1,desired=replicas,requested_by=c.actor_id,diagnostic='pending' WHERE cluster_id=cluster;
 END IF;
 RETURN jsonb_build_object('requestId',request,'generation',expected+1,'desired',replicas);
END$$;
-- The restricted login selects its pool only through its installation registration.
-- Keep dependent objects bound to the public entry points. Copy the verified
-- implementations to owner-only helpers, then replace each public body in place.
DO $copy$
BEGIN
 EXECUTE replace(pg_get_functiondef('risk.claim(integer,text)'::regprocedure),
  'CREATE OR REPLACE FUNCTION risk.claim(', 'CREATE FUNCTION risk.claim_unmanaged(');
 EXECUTE replace(pg_get_functiondef('risk.heartbeat(uuid,uuid,integer)'::regprocedure),
  'CREATE OR REPLACE FUNCTION risk.heartbeat(', 'CREATE FUNCTION risk.heartbeat_before_pool(');
END$copy$;
ALTER FUNCTION risk.claim_unmanaged(integer,text) OWNER TO openetrm_security_owner;
ALTER FUNCTION risk.heartbeat_before_pool(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.claim_unmanaged(integer,text),risk.heartbeat_before_pool(uuid,uuid,integer) FROM PUBLIC,openetrm_risk_worker;
DO $access$
BEGIN
 IF EXISTS(SELECT FROM pg_proc p CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
  WHERE p.oid IN('risk.claim_unmanaged(integer,text)'::regprocedure,'risk.heartbeat_before_pool(uuid,uuid,integer)'::regprocedure)
   AND a.grantee<>p.proowner)
 THEN RAISE EXCEPTION 'managed_pool_upgrade: unexpected helper grant'; END IF;
END$access$;
CREATE OR REPLACE FUNCTION risk.claim(lease_seconds integer,engine_version text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT FROM automation.risk_pools WHERE worker_login=session_user) THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN risk.claim_unmanaged(lease_seconds,engine_version);
END$$;
CREATE FUNCTION risk.pool_claim(seconds integer,engine text,task text,node text,slot_number integer,poll integer) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p automation.risk_pools;t automation.pool_tasks;receipt jsonb;w risk.workers;BEGIN
 w=risk.worker();
 SELECT rp.* INTO p FROM automation.risk_pools rp JOIN automation.clusters cl ON cl.id=rp.cluster_id WHERE rp.worker_login=session_user
 AND cl.enabled AND(cl.tenant_id,cl.workspace_id)=(w.tenant_id,w.workspace_id) FOR SHARE OF rp;
 IF NOT FOUND OR poll NOT BETWEEN 1 AND 60 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO t FROM automation.pool_tasks WHERE cluster_id=p.cluster_id AND task_id=task AND node_id=node AND slot=slot_number FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF t.seen IS NULL THEN PERFORM automation.pool_log(p.cluster_id,'worker',task,'Worker connected');END IF;
 IF t.job_id IS NOT NULL THEN RETURN NULL;END IF;

 IF p.reconciled=p.generation AND p.observed_at>clock_timestamp()-interval '2 minutes' AND slot_number<=p.desired THEN receipt=risk.claim_unmanaged(seconds,engine);END IF;
 IF receipt IS NOT NULL THEN PERFORM automation.pool_log(p.cluster_id,'worker',task,'Claimed risk job');END IF;
 UPDATE automation.pool_tasks SET seen=clock_timestamp(),fresh_until=clock_timestamp()+make_interval(secs=>greatest(seconds,poll*2+5)),
 engine=left(pool_claim.engine,256),attempt_number=(receipt->>'attempt')::integer,job_id=(receipt->>'jobId')::uuid WHERE cluster_id=p.cluster_id AND task_id=task;
 RETURN receipt;
END$$;
CREATE FUNCTION risk.pool_app_result(task text,job uuid,attempt_number integer) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p automation.risk_pools;j risk.jobs;t automation.pool_tasks;msg text;BEGIN
 PERFORM risk.worker();
 SELECT * INTO p FROM automation.risk_pools WHERE worker_login=session_user;
 SELECT * INTO t FROM automation.pool_tasks WHERE cluster_id=p.cluster_id AND task_id=task AND job_id=job AND pool_tasks.attempt_number=pool_app_result.attempt_number FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL;END IF;
 SELECT * INTO j FROM risk.jobs WHERE id=job AND attempt=pool_app_result.attempt_number AND state IN('completed','failed','cancelled');
 msg='Risk worker finished attempt '||attempt_number||' · '||coalesce(j.state,'stopped without publication')||coalesce(' ('||j.error_code||')','');
 PERFORM automation.pool_log(p.cluster_id,'worker',task,msg);
 UPDATE automation.pool_tasks SET job_id=NULL,attempt_number=NULL WHERE cluster_id=p.cluster_id AND task_id=task;
 RETURN msg;
END$$;
CREATE OR REPLACE FUNCTION risk.heartbeat(job_id uuid,token uuid,lease_seconds integer) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE state text;BEGIN
 state=risk.heartbeat_before_pool(job_id,token,lease_seconds);
 UPDATE automation.pool_tasks t SET seen=clock_timestamp(),fresh_until=clock_timestamp()+make_interval(secs=>lease_seconds)
 FROM automation.risk_pools p WHERE p.cluster_id=t.cluster_id AND p.worker_login=session_user AND t.job_id=heartbeat.job_id AND t.attempt_number=(SELECT j.attempt FROM risk.jobs j WHERE j.id=heartbeat.job_id);
 RETURN state;
END$$;
CREATE FUNCTION automation.pool_config(cluster text) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM automation.controller(cluster);
 RETURN (SELECT jsonb_build_object('service',service_id,'spec',approved_spec,'generation',generation) FROM automation.risk_pools rp WHERE rp.cluster_id=cluster AND EXISTS(SELECT FROM automation.clusters cl JOIN risk.workers w ON(w.tenant_id,w.workspace_id)=(cl.tenant_id,cl.workspace_id) WHERE cl.id=cluster AND w.login=rp.worker_login));
END$$;
CREATE FUNCTION automation.inspect_pool(cluster text,service text,expected bigint,replicas integer,tasks jsonb,matched boolean,applied_generation text) RETURNS integer
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;p automation.risk_pools;x jsonb;reason text;BEGIN
 w=automation.controller(cluster);
 SELECT * INTO p FROM automation.risk_pools WHERE cluster_id=cluster AND service_id=service AND generation=expected FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF matched IS NOT TRUE THEN
 UPDATE automation.risk_pools SET reconciled=NULL,diagnostic='service_spec_changed',observed_at=clock_timestamp() WHERE cluster_id=cluster;RETURN NULL;
 END IF;
 IF jsonb_typeof(tasks) IS DISTINCT FROM 'array' OR jsonb_array_length(tasks)>256 THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 FOR x IN SELECT value FROM jsonb_array_elements(tasks) LOOP
 IF x->>'state'='running' THEN
 INSERT INTO automation.pool_tasks(cluster_id,task_id,node_id,slot) VALUES(cluster,x->>'id',x->>'node',(x->>'slot')::integer) ON CONFLICT DO NOTHING;
 END IF;
 END LOOP;
 DELETE FROM automation.pool_tasks t WHERE t.cluster_id=cluster AND (t.job_id IS NULL OR EXISTS(SELECT FROM jsonb_array_elements(tasks) a
 WHERE a->>'id'=t.task_id AND a->>'state' IN('shutdown','failed','complete','rejected'))) AND NOT EXISTS(SELECT FROM jsonb_array_elements(tasks) a
 WHERE a->>'id'=t.task_id AND a->>'state' NOT IN('shutdown','failed','complete','rejected','remove'));
 IF EXISTS(SELECT FROM automation.pool_tasks t WHERE t.cluster_id=cluster GROUP BY t.slot HAVING count(*)>1)
 OR EXISTS(SELECT FROM automation.pool_tasks t WHERE t.cluster_id=cluster GROUP BY t.node_id HAVING count(*)>1)
 THEN reason='overlapping_tasks';
 ELSIF p.requested_by IS NULL THEN reason='awaiting_request';
 ELSIF NOT automation.can_scale(p.requested_by,w.tenant_id,w.workspace_id) AND p.reconciled IS DISTINCT FROM expected THEN reason='authority_changed';
 ELSIF (replicas<>p.desired OR applied_generation IS DISTINCT FROM expected::text) AND NOT automation.can_scale(p.requested_by,w.tenant_id,w.workspace_id) THEN reason='authority_changed';
 ELSIF replicas>p.desired AND EXISTS(SELECT FROM automation.pool_tasks t WHERE t.cluster_id=cluster AND t.job_id IS NOT NULL) THEN reason='draining';
 ELSIF replicas<>p.desired OR applied_generation IS DISTINCT FROM expected::text THEN reason='applying';
 END IF;
 UPDATE automation.risk_pools SET observed=replicas,observed_at=clock_timestamp(),diagnostic=reason,
 reconciled=CASE WHEN reason IS NULL THEN generation END WHERE cluster_id=cluster;
 RETURN CASE WHEN reason='applying' THEN p.desired END;
END$$;

-- Diff two complete observed inventories. Repeated controller polls log only changes.
CREATE OR REPLACE FUNCTION automation.observe_nodes(cluster_id text,nodes jsonb) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE x record;BEGIN
 PERFORM automation.controller(cluster_id);
 PERFORM 1 FROM automation.clusters c WHERE c.id=cluster_id FOR NO KEY UPDATE;
 IF jsonb_typeof(nodes) IS DISTINCT FROM 'array' OR jsonb_array_length(nodes)>1024 THEN RAISE EXCEPTION 'infrastructure_input';END IF;
 FOR x IN SELECT coalesce(a.id,b.id) AS id,a.state AS old_state,a.availability AS old_avail,b.state,b.availability
 FROM(SELECT n.* FROM automation.nodes n WHERE n.cluster_id=observe_nodes.cluster_id)a
 FULL JOIN jsonb_to_recordset(nodes)b(id text,state text,availability text) USING(id)
 WHERE(a.state,a.availability) IS DISTINCT FROM(b.state,b.availability) LOOP
 PERFORM automation.pool_log(cluster_id,'node',x.id,coalesce(x.old_state||' / '||x.old_avail,'First observed')||' → '||
 coalesce(x.state||' / '||x.availability,'Absent from inventory'));
 END LOOP;
 DELETE FROM automation.nodes n WHERE n.cluster_id=observe_nodes.cluster_id;
 INSERT INTO automation.nodes
 SELECT cluster_id,item->>'id',(item->>'version')::bigint,item->>'name',item->>'role',item->>'state',item->>'availability',
 (item->>'cpuNano')::bigint,(item->>'memoryBytes')::bigint,(item->>'workloadNode')::boolean,statement_timestamp()
 FROM jsonb_array_elements(nodes)item;
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE
 (n.nspname='automation' AND p.proname IN('can_scale','pool_log','pool_config','inspect_pool','pool_directory','pool_logs_read','scale_pool','observe_nodes'))
 OR(n.nspname='risk' AND p.proname IN('claim','pool_claim','pool_app_result','heartbeat')) LOOP
 EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
 EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION automation.pool_directory(),automation.scale_pool(text,uuid,bigint,integer),automation.pool_logs_read(text) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION automation.observe_nodes(text,jsonb),automation.pool_config(text),automation.inspect_pool(text,text,bigint,integer,jsonb,boolean,text) TO openetrm_scheduler;
GRANT EXECUTE ON FUNCTION risk.pool_app_result(text,uuid,integer),risk.pool_claim(integer,text,text,text,integer,integer),risk.claim(integer,text),risk.heartbeat(uuid,uuid,integer) TO openetrm_risk_worker;
COMMIT;
