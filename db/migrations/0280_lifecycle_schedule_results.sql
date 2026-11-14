-- Current consumer: reviewed source-only lifecycle proposals and atomic
-- financial occurrence completion for the separately registered scheduler.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE automation.lifecycle_results(
 run_id uuid PRIMARY KEY REFERENCES automation.runs(id),execution_context_id uuid NOT NULL REFERENCES automation.lifecycle_sources(context_id),
 catalog_id uuid NOT NULL,source_context_id uuid NOT NULL,mode text NOT NULL CHECK(mode IN('manual','review','automatic')),
 proposal jsonb,outcome jsonb,command_id uuid,recorded_at ref_data.instant NOT NULL,
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,command_id) REFERENCES lifecycle.commands(catalog_id,operation_id),
 CHECK((mode='manual' AND proposal IS NULL AND outcome IS NULL AND command_id IS NULL)
  OR(mode='review' AND jsonb_typeof(proposal)='object' AND outcome IS NULL AND command_id IS NULL)
  OR(mode='automatic' AND proposal IS NULL AND jsonb_typeof(outcome)='object' AND command_id=run_id))
);
ALTER TABLE automation.lifecycle_results OWNER TO openetrm_security_owner;
ALTER TABLE automation.lifecycle_results ENABLE ROW LEVEL SECURITY;
ALTER TABLE automation.lifecycle_results FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON automation.lifecycle_results TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.lifecycle_results FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION automation.require_lifecycle_review(p automation.lifecycle_profiles,r automation.runs,source automation.lifecycle_sources,proposal jsonb) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE request jsonb=proposal->'request';review jsonb=proposal->'review';offer jsonb=review->'offer';BEGIN
 IF jsonb_typeof(proposal) IS DISTINCT FROM 'object' OR proposal-ARRAY['request','review']<>'{}'::jsonb OR NOT(proposal ?& ARRAY['request','review'])
  OR jsonb_typeof(request) IS DISTINCT FROM 'object' OR request-ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection']<>'{}'::jsonb
  OR NOT(request ?& ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'])
  OR jsonb_typeof(review) IS DISTINCT FROM 'object' OR review-ARRAY['hash','offer','details']<>'{}'::jsonb OR NOT(review ?& ARRAY['hash','offer','details'])
  OR jsonb_typeof(review->'hash') IS DISTINCT FROM 'string' OR coalesce(review->>'hash'!~'^[0-9a-f]{64}$',true)
  OR jsonb_typeof(request->'previewHash') IS DISTINCT FROM 'string' OR request->>'previewHash' IS DISTINCT FROM review->>'hash'
  OR(request->>'operationId')::uuid IS DISTINCT FROM r.id OR(request#>>'{context,contextId}')::uuid IS DISTINCT FROM source.source_context_id
  OR(request#>>'{context,snapshotId}')::uuid IS DISTINCT FROM p.close_id
  OR jsonb_typeof(offer) IS DISTINCT FROM 'object' OR offer->'target' IS DISTINCT FROM request->'target' OR offer->>'action' IS DISTINCT FROM p.action
  OR(offer->>'occurrenceId')::uuid IS DISTINCT FROM p.occurrence_id OR offer->'eligible' IS DISTINCT FROM 'true'::jsonb OR jsonb_typeof(review->'details') IS DISTINCT FROM 'object'
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 PERFORM automation.require_lifecycle_action_scope(p.catalog_id,p.action,request,false);
END$function$;
ALTER FUNCTION automation.require_lifecycle_review(automation.lifecycle_profiles,automation.runs,automation.lifecycle_sources,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_lifecycle_review(automation.lifecycle_profiles,automation.runs,automation.lifecycle_sources,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
CREATE FUNCTION automation.publish_lifecycle_result(mode text,proposal jsonb,outcome jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;token uuid;source automation.lifecycle_sources;saved_mode text;BEGIN
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 PERFORM automation.require_lifecycle_settings(p);
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=token;
 SELECT a.mode INTO saved_mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 IF source.context_id IS NULL OR saved_mode IS DISTINCT FROM mode OR source.catalog_id IS DISTINCT FROM p.catalog_id OR EXISTS(SELECT 1 FROM automation.lifecycle_results WHERE run_id=r.id)
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 IF mode='review' THEN PERFORM automation.require_lifecycle_review(p,r,source,proposal);END IF;
 INSERT INTO automation.lifecycle_results VALUES(r.id,token,p.catalog_id,source.source_context_id,mode,proposal,outcome,CASE WHEN mode='automatic' THEN r.id END,clock_timestamp());
 RETURN jsonb_build_object('runId',r.id,'mode',mode,'sourceContextId',source.source_context_id,'state',CASE mode WHEN 'manual' THEN 'manual_required' WHEN 'review' THEN 'review_required' ELSE 'executed' END);
END$function$;
CREATE FUNCTION automation.finish_lifecycle_result() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;token uuid;source automation.lifecycle_sources;command lifecycle.commands;body jsonb;BEGIN
 -- Product writers queue their native financial seals before the shared
 -- command. This result is inserted last and transitions the occurrence only
 -- after those seals and the required-result link have passed at commit.
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=token;
 PERFORM automation.require_lifecycle_settings(p);
 IF(r.id,token,p.catalog_id,source.source_context_id) IS DISTINCT FROM(NEW.run_id,NEW.execution_context_id,NEW.catalog_id,NEW.source_context_id)
  OR NEW.mode IS DISTINCT FROM(SELECT a.mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision))
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 IF NEW.mode='review' THEN PERFORM automation.require_lifecycle_review(p,r,source,NEW.proposal);END IF;
 IF NEW.mode<>'automatic' AND EXISTS(SELECT 1 FROM lifecycle.commands c WHERE(c.catalog_id,c.operation_id)=(NEW.catalog_id,r.id))
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 IF NEW.mode='automatic' THEN
  SELECT * INTO command FROM lifecycle.commands c WHERE(c.catalog_id,c.operation_id)=(NEW.catalog_id,NEW.command_id);body=command.request;
  IF command.operation_id IS NULL OR(command.kind,command.policy_id,command.policy_revision) IS DISTINCT FROM(p.action,p.policy_id,p.policy_revision)
   OR command.actor_id IS DISTINCT FROM(SELECT actor_id FROM security.request_contexts WHERE id=token)
   OR command.outcome IS DISTINCT FROM NEW.outcome OR(body#>>'{operationId}')::uuid IS DISTINCT FROM r.id
   OR(body#>>'{context,contextId}')::uuid IS DISTINCT FROM source.source_context_id
   OR(body#>>'{target,scopeId}')::uuid IS DISTINCT FROM p.trade_id OR(body#>>'{target,bindingId}')::uuid IS DISTINCT FROM p.binding_id OR body#>>'{target,handler}' IS DISTINCT FROM p.handler
  THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 END IF;
 UPDATE automation.runs SET state='succeeded',finished_at=clock_timestamp(),error_code=NULL WHERE id=r.id;
 UPDATE automation.jobs SET state='completed',version=version+1 WHERE id=j.id;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,(SELECT actor_id FROM security.request_contexts WHERE id=token),'completed',clock_timestamp(),r.fence);
 RETURN NEW;
END$function$;
CREATE CONSTRAINT TRIGGER lifecycle_run_complete AFTER INSERT ON automation.lifecycle_results DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.finish_lifecycle_result();
CREATE FUNCTION automation.lifecycle_completed(run uuid,expected_fence bigint,worker uuid) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE w automation.workers;BEGIN
 w=automation.worker();
 RETURN EXISTS(SELECT 1 FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id JOIN automation.lifecycle_results result ON result.run_id=r.id
  WHERE(r.id,r.fence,r.worker_id,r.worker_login,r.tenant_id,r.workspace_id)=(run,expected_fence,worker,session_user,w.tenant_id,w.workspace_id)
  AND r.state='succeeded' AND j.operation='lifecycle_action_v1');
END$function$;
CREATE FUNCTION automation.require_lifecycle_job_result(job uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE j automation.jobs;p automation.lifecycle_profiles;BEGIN
 IF until_at IS NULL OR NOT isfinite(until_at) OR until_at<=clock_timestamp() THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO j FROM automation.jobs WHERE id=job;
 SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 IF j.operation IS DISTINCT FROM 'lifecycle_action_v1' OR p.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) OR NOT automation.authorized(j)
  OR NOT security.trading_access(p.catalog_id,'ReadTrades') OR NOT security.market_access(p.catalog_id,'ReadMarketData')
  OR('ReadGasOwnedHoldings'=ANY(automation.lifecycle_permissions(p.id)) AND NOT security.has_permission('ReadGasOwnedHoldings'))
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF until_at<=clock_timestamp() THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
END$function$;
CREATE FUNCTION automation.lifecycle_job_result(job uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE j automation.jobs;p automation.lifecycle_profiles;result jsonb;until_at timestamptz;gas_until timestamptz;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 gas_until=CASE WHEN security.has_permission('ReadGasOwnedHoldings') THEN security.authority_until('ReadGasOwnedHoldings') ELSE '-infinity'::timestamptz END;
 PERFORM automation.require_lifecycle_job_result(job,until_at);
 SELECT * INTO j FROM automation.jobs WHERE id=job;
 IF j.operation IS DISTINCT FROM 'lifecycle_action_v1' OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) OR NOT automation.authorized(j)
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 IF 'ReadGasOwnedHoldings'=ANY(automation.lifecycle_permissions(p.id)) THEN until_at=least(until_at,gas_until);END IF;
 PERFORM automation.require_lifecycle_job_result(job,until_at);
 SELECT jsonb_build_object('job',automation.description(j),'close',(SELECT jsonb_build_object('id',c.id,'name',c.name,'at',c.at,'businessDate',c.business_date) FROM market_data.market_closes c WHERE(c.catalog_id,c.id)=(p.catalog_id,p.close_id)),'definition',automation.lifecycle_definition(p),
  'results',coalesce((SELECT jsonb_agg(jsonb_build_object('runId',r.run_id,'sourceContextId',r.source_context_id,'knownAt',(SELECT t.known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(r.catalog_id,r.source_context_id)),'mode',r.mode,'proposal',r.proposal,'outcome',r.outcome,'recordedAt',r.recorded_at) ORDER BY r.recorded_at,r.run_id)
   FROM automation.lifecycle_results r JOIN automation.runs occurrence ON occurrence.id=r.run_id WHERE occurrence.job_id=j.id),'[]'::jsonb)) INTO result;
 PERFORM automation.require_lifecycle_job_result(job,until_at);
 RETURN jsonb_build_object('outcome',result,'authorityUntil',until_at);
END$function$;
DO $functions$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='automation'
  AND p.proname IN('publish_lifecycle_result','finish_lifecycle_result','lifecycle_completed','lifecycle_job_result','require_lifecycle_job_result') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler',f.signature);
  IF f.proname IN('publish_lifecycle_result','lifecycle_job_result','require_lifecycle_job_result') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
  IF f.proname='lifecycle_completed' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_scheduler',f.signature);END IF;
 END LOOP;
END$functions$;
COMMIT;
