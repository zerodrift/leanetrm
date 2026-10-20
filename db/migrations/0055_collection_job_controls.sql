-- Scoped operational controls for existing delivery-price collection jobs.
BEGIN;
CREATE FUNCTION power_data.collector_eligible(actor power_data.collectors) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT actor.login IS NOT NULL
 AND EXISTS(SELECT 1 FROM security.market_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id))
 AND EXISTS(SELECT 1 FROM security.principals p JOIN security.workspace_memberships m ON m.principal_id=p.id WHERE p.id=actor.principal_id AND p.kind='service' AND p.enabled AND m.enabled AND(m.tenant_id,m.workspace_id)=(actor.tenant_id,actor.workspace_id))
 AND security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,'ImportObservations',clock_timestamp())
 AND security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,'ReadMarketData',clock_timestamp());
$$;
CREATE OR REPLACE FUNCTION power_data.worker_catalog() RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE worker power_data.collectors;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO worker FROM power_data.collectors WHERE login=session_user;
 IF NOT power_data.collector_eligible(worker) THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 RETURN worker.catalog_id;
END$$;
CREATE FUNCTION power_data.sources_eligible(selected power_data.jobs) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor power_data.collectors;members integer;accepted integer;
BEGIN
 SELECT * INTO actor FROM power_data.collectors WHERE(login,catalog_id)=(selected.collector_login,selected.catalog_id);
 SELECT count(*) INTO members FROM power_data.job_sources WHERE job_id=selected.id;
 SELECT count(*) INTO accepted FROM power_data.job_sources m JOIN power_data.sources s ON s.id=m.source_id
 JOIN ref_data.revisions v ON(v.catalog_id,v.id)=(s.reference_catalog_id,s.reference_revision_id) AND v.state='sealed'
 JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(s.reference_catalog_id,s.reference_revision_id,s.currency_code) AND c.display_code='USD'
 JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quantity_code) AND q.dimension_code='energy'
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code) AND u.symbol='MWh'
 JOIN ref_data.clock_rules clock ON(clock.catalog_id,clock.revision_id,clock.code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND clock.kind='iana' AND clock.iana_zone=CASE s.provider WHEN 'PJM' THEN 'America/New_York' ELSE 'America/Chicago' END
 JOIN security.reference_catalog_scopes scope ON(scope.catalog_id,scope.tenant_id,scope.workspace_id)=(s.reference_catalog_id,actor.tenant_id,actor.workspace_id)
 WHERE m.job_id=selected.id AND s.catalog_id=selected.catalog_id AND s.provider=selected.provider
 AND security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,'ReadMarketData',clock_timestamp());
 RETURN members>0 AND members=accepted;
END$$;
CREATE OR REPLACE FUNCTION power_data.validate_sources(job uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor power_data.collectors;selected power_data.jobs;
BEGIN
 PERFORM power_data.worker_catalog();SELECT * INTO actor FROM power_data.collectors WHERE login=session_user;
 SELECT * INTO selected FROM power_data.jobs WHERE id=job AND collector_login=session_user AND catalog_id=actor.catalog_id AND enabled;
 IF selected.id IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=(selected.catalog_id,actor.tenant_id,actor.workspace_id)) THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 IF NOT power_data.sources_eligible(selected) THEN RAISE EXCEPTION 'collection_reference_scope' USING ERRCODE='42501';END IF;
END$$;
CREATE TABLE power_data.job_commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL,request_id uuid NOT NULL,
 job_id uuid NOT NULL,catalog_id uuid NOT NULL,request jsonb NOT NULL,before_terms jsonb NOT NULL,response jsonb NOT NULL,recorded_at timestamptz NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,actor_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(job_id,catalog_id) REFERENCES power_data.jobs(id,catalog_id)
);
ALTER TABLE power_data.job_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE power_data.job_commands FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON power_data.job_commands TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT ALL ON power_data.job_commands TO openetrm_security_owner;
CREATE TRIGGER immutable_fact BEFORE UPDATE OR DELETE ON power_data.job_commands FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION power_data.job_settings() RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT '{"minimumDayOffset":-31,"maximumDayOffset":-1,"maximumNameLength":160}'::jsonb;
$$;
CREATE FUNCTION power_data.job_visible(selected power_data.jobs) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 c=automation.actor();
 RETURN EXISTS(SELECT 1 FROM power_data.collectors actor JOIN security.market_catalog_scopes scope ON(scope.catalog_id,scope.tenant_id,scope.workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id)
 WHERE(actor.login,actor.catalog_id)=(selected.collector_login,selected.catalog_id) AND(actor.tenant_id,actor.workspace_id)=(c.tenant_id,c.workspace_id))
 AND(security.has_permission('ManageJobs') OR security.market_access(selected.catalog_id,'ReadMarketData'));
END$$;
CREATE FUNCTION power_data.job_edit_reason(selected power_data.jobs) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor power_data.collectors;
BEGIN
 IF NOT security.has_permission('ManageJobs') THEN RETURN 'Collection schedule editing requires job-management permission.';END IF;
 IF NOT selected.enabled THEN RETURN 'This collection job is disabled by its operator.';END IF;
 SELECT * INTO actor FROM power_data.collectors WHERE(login,catalog_id)=(selected.collector_login,selected.catalog_id);
 IF NOT power_data.collector_eligible(actor) THEN RETURN 'The registered collection service lacks current execution authority.';END IF;
 IF NOT power_data.sources_eligible(selected) THEN RETURN 'The configured collection sources need operator qualification.';END IF;
 IF EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND classid=0 AND objid=730849110 AND objsubid=1 AND granted AND pid<>pg_backend_pid())
 THEN RETURN 'The collection worker is active. Refresh after it finishes before editing.';END IF;
 RETURN NULL;
END$$;
CREATE FUNCTION power_data.job_description(selected power_data.jobs) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE reason text;result jsonb;
BEGIN
 reason=power_data.job_edit_reason(selected);
 SELECT jsonb_build_object('id',selected.id,'generation',selected.generation,'name',selected.name,'provider',selected.provider,
 'operation','delivery_price_collection','serviceName',m.display_name,'enabled',selected.enabled,'scheduleEnabled',selected.schedule_enabled,
 'state',CASE WHEN NOT selected.enabled THEN 'disabled' WHEN selected.schedule_enabled THEN 'scheduled' ELSE 'paused' END,
 'cron',selected.cron,'scheduleZone',selected.schedule_zone,'cadence','Daily at 08:00 UTC','fromDay',selected.from_day,'throughDay',selected.through_day,
 'sourceCount',(SELECT count(*) FROM power_data.job_sources WHERE job_id=selected.id),'canEdit',reason IS NULL,'editReason',reason,
 'lastAttempt',(SELECT jsonb_build_object('startedAt',a.started_at,'finishedAt',a.finished_at,'state',a.state,'errorCode',a.error_code) FROM power_data.attempts a WHERE a.job_id=selected.id ORDER BY a.started_at DESC,a.id DESC LIMIT 1)) INTO result
 FROM power_data.collectors actor JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(actor.tenant_id,actor.workspace_id,actor.principal_id)
 WHERE(actor.login,actor.catalog_id)=(selected.collector_login,selected.catalog_id);
 RETURN result;
END$$;
CREATE FUNCTION power_data.job_directory(page integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM automation.actor();
 IF page IS NULL OR page NOT BETWEEN 0 AND 10000 THEN RAISE EXCEPTION 'job_input';END IF;
 RETURN jsonb_build_object('page',page,'total',(SELECT count(*) FROM power_data.jobs j WHERE power_data.job_visible(j)),
 'jobs',coalesce((SELECT jsonb_agg(power_data.job_description(j) ORDER BY j.provider,j.name,j.id) FROM
 (SELECT * FROM power_data.jobs j WHERE power_data.job_visible(j) ORDER BY j.provider,j.name,j.id OFFSET page*100 LIMIT 100)j),'[]'::jsonb),'settings',power_data.job_settings());
END$$;
CREATE FUNCTION power_data.update_job(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;selected power_data.jobs;previous power_data.job_commands;before_terms jsonb;response jsonb;settings jsonb;token uuid;
BEGIN
 c=automation.actor();
 IF NOT security.has_permission('ManageJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(request) IS DISTINCT FROM 'object'
 OR request-ARRAY['requestId','id','expectedGeneration','name','scheduleEnabled','fromDay','throughDay']<>'{}'::jsonb
 OR NOT(request ?& ARRAY['requestId','id','expectedGeneration','name','scheduleEnabled','fromDay','throughDay'])
 OR jsonb_typeof(request->'scheduleEnabled') IS DISTINCT FROM 'boolean'
 OR jsonb_typeof(request->'fromDay') IS DISTINCT FROM 'number' OR jsonb_typeof(request->'throughDay') IS DISTINCT FROM 'number'
 OR jsonb_typeof(request->'name') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'job_input';END IF;
 token=(request->>'requestId')::uuid;
 IF token IS NULL OR token='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT * INTO selected FROM power_data.jobs WHERE id=(request->>'id')::uuid;
 IF selected.id IS NULL OR NOT power_data.job_visible(selected) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF NOT pg_try_advisory_xact_lock(730849110) THEN RAISE EXCEPTION 'job_transition';END IF;
 SELECT * INTO selected FROM power_data.jobs WHERE id=(request->>'id')::uuid FOR UPDATE;
 c=automation.actor();
 IF selected.id IS NULL OR NOT power_data.job_visible(selected) OR NOT security.has_permission('ManageJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF power_data.job_edit_reason(selected) IS NOT NULL THEN RAISE EXCEPTION 'job_transition';END IF;
 SELECT * INTO previous FROM power_data.job_commands WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,token);
 IF FOUND THEN IF previous.request<>request THEN RAISE EXCEPTION 'job_conflict';END IF;RETURN previous.response;END IF;
 IF selected.generation IS DISTINCT FROM(request->>'expectedGeneration')::uuid THEN RAISE EXCEPTION 'job_conflict';END IF;
 settings=power_data.job_settings();
 IF length(btrim(request->>'name')) NOT BETWEEN 1 AND(settings->>'maximumNameLength')::integer
 OR(request->>'fromDay')::integer NOT BETWEEN(settings->>'minimumDayOffset')::integer AND(settings->>'maximumDayOffset')::integer
 OR(request->>'throughDay')::integer NOT BETWEEN(request->>'fromDay')::integer AND(settings->>'maximumDayOffset')::integer
 THEN RAISE EXCEPTION 'job_input';END IF;
 before_terms=power_data.job_description(selected);
 UPDATE power_data.jobs SET name=request->>'name',schedule_enabled=(request->>'scheduleEnabled')::boolean,
 from_day=(request->>'fromDay')::integer,through_day=(request->>'throughDay')::integer WHERE id=selected.id RETURNING * INTO selected;
 c=automation.actor();
 IF NOT power_data.job_visible(selected) OR NOT security.has_permission('ManageJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF power_data.job_edit_reason(selected) IS NOT NULL THEN RAISE EXCEPTION 'job_transition';END IF;
 response=jsonb_build_object('requestId',token,'id',selected.id,'generation',selected.generation,'name',selected.name,'scheduleEnabled',selected.schedule_enabled,'fromDay',selected.from_day,'throughDay',selected.through_day);
 INSERT INTO power_data.job_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,token,selected.id,selected.catalog_id,request,before_terms,response,clock_timestamp());
 RETURN response;
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT oid::regprocedure FROM pg_proc WHERE pronamespace='power_data'::regnamespace AND proname IN('collector_eligible','sources_eligible','job_settings','job_visible','job_edit_reason','job_description','job_directory','update_job') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION power_data.job_directory(integer),power_data.update_job(jsonb) TO openetrm_runtime;
COMMIT;
