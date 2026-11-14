-- Current consumer: the shared lifecycle adapter for one owned scheduled
-- occurrence. Ordinary runtime operations retain their native authorization.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.capture_action_authority(uuid,text,jsonb)');
 IF NOT FOUND OR md5(p.prosrc)<>'b21409d18cce316f0494cbeb577c797b' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'timestamp with time zone'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope_upgrade: unexpected helper lifecycle.capture_action_authority(uuid,text,jsonb)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.capture_action_authority(','FUNCTION lifecycle.capture_action_authority_pre_schedule_scope('),'capture_action_authority.','capture_action_authority_pre_schedule_scope.');
END$guard$;
ALTER FUNCTION lifecycle.capture_action_authority_pre_schedule_scope(uuid,text,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.capture_action_authority_pre_schedule_scope(uuid,text,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.option_proposal(uuid,jsonb)');
 IF NOT FOUND OR md5(p.prosrc)<>'0b36a9cc45c9cea4e2590f2ab4b7586e' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope_upgrade: unexpected helper lifecycle.option_proposal(uuid,jsonb)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.option_proposal(','FUNCTION lifecycle.option_proposal_pre_schedule_scope('),'option_proposal.','option_proposal_pre_schedule_scope.');
END$guard$;
ALTER FUNCTION lifecycle.option_proposal_pre_schedule_scope(uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.option_proposal_pre_schedule_scope(uuid,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.execute_option_action(uuid,jsonb)');
 IF NOT FOUND OR md5(p.prosrc)<>'3841eda90b5bd3bc9a9e840dd6432511' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope_upgrade: unexpected helper lifecycle.execute_option_action(uuid,jsonb)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.execute_option_action(','FUNCTION lifecycle.execute_option_action_pre_schedule_scope('),'execute_option_action.','execute_option_action_pre_schedule_scope.');
END$guard$;
ALTER FUNCTION lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)');
 IF NOT FOUND OR md5(p.prosrc)<>'01b812a37651887ad470b3ad69877872' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope_upgrade: unexpected helper lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.record_native_action(','FUNCTION lifecycle.record_native_action_pre_schedule_scope('),'record_native_action.','record_native_action_pre_schedule_scope.');
END$guard$;
ALTER FUNCTION lifecycle.record_native_action_pre_schedule_scope(uuid,jsonb,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.record_native_action_pre_schedule_scope(uuid,jsonb,jsonb,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.command_status(uuid,uuid,text,jsonb,text,timestamptz)');
 IF NOT FOUND OR md5(p.prosrc)<>'6bfc6df364cdcebfa73d56e2db5ddf8c' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope_upgrade: unexpected helper lifecycle.command_status(uuid,uuid,text,jsonb,text,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.command_status(','FUNCTION lifecycle.command_status_pre_schedule_scope('),'command_status.','command_status_pre_schedule_scope.');
END$guard$;
ALTER FUNCTION lifecycle.command_status_pre_schedule_scope(uuid,uuid,text,jsonb,text,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.command_status_pre_schedule_scope(uuid,uuid,text,jsonb,text,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)');
 IF NOT FOUND OR md5(p.prosrc)<>'521270674a5b65a292fa146777810a95' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope_upgrade: unexpected helper lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION lifecycle.finish_command(','FUNCTION lifecycle.finish_command_pre_schedule_scope('),'finish_command.','finish_command_pre_schedule_scope.');
END$guard$;
ALTER FUNCTION lifecycle.finish_command_pre_schedule_scope(uuid,uuid,text,jsonb,text,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.finish_command_pre_schedule_scope(uuid,uuid,text,jsonb,text,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
CREATE FUNCTION automation.require_lifecycle_action_scope(catalog uuid,kind text,body jsonb,writing boolean) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE token uuid;r automation.runs;j automation.jobs;p automation.lifecycle_profiles;source automation.lifecycle_sources;anchor market_data.market_closes;mode text;BEGIN
 SELECT q.id INTO token FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  JOIN automation.runs occurrence ON occurrence.id=b.run_id JOIN automation.jobs task ON task.id=occurrence.job_id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND task.operation='lifecycle_action_v1';
 IF NOT FOUND THEN RETURN;END IF;
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=token;
 SELECT * INTO anchor FROM market_data.market_closes WHERE(catalog_id,id)=(p.catalog_id,p.close_id);
 SELECT policy.mode INTO mode FROM lifecycle.action_policies policy WHERE(policy.catalog_id,policy.id,policy.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 PERFORM automation.require_lifecycle_settings(p);
 IF source.context_id IS NULL OR catalog IS DISTINCT FROM p.catalog_id OR kind IS DISTINCT FROM p.action OR body->>'action' IS DISTINCT FROM p.action
  OR(body->>'operationId')::uuid IS DISTINCT FROM r.id OR(body->>'policyId')::uuid IS DISTINCT FROM p.policy_id OR(body->>'policyRevision')::integer IS DISTINCT FROM p.policy_revision
  OR(body#>>'{target,handler}') IS DISTINCT FROM p.handler OR(body#>>'{target,scopeId}')::uuid IS DISTINCT FROM p.trade_id OR(body#>>'{target,bindingId}')::uuid IS DISTINCT FROM p.binding_id
  OR(body#>>'{context,contextId}')::uuid IS DISTINCT FROM source.source_context_id OR(body#>>'{context,asOf}')::timestamptz IS DISTINCT FROM anchor.at
  OR(body#>>'{context,businessDate}')::date IS DISTINCT FROM anchor.business_date OR mode NOT IN('review','automatic') OR mode IS NULL
  OR(body->>'automatic')::boolean IS DISTINCT FROM(mode='automatic') OR(writing AND mode<>'automatic')
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 IF p.handler='futures' THEN
  IF(body#>>'{selection,expectedRevision}')::integer IS DISTINCT FROM p.future_revision OR(body#>>'{selection,throughOccurrence}')::uuid IS DISTINCT FROM p.occurrence_id
   OR body#>'{selection,correctionDue}' IS DISTINCT FROM '[]'::jsonb THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 ELSIF p.handler='listed_option' THEN
  IF(body#>>'{selection,profileRevision}')::integer IS DISTINCT FROM p.option_profile_revision OR(body#>>'{selection,evidenceId}')::uuid IS DISTINCT FROM p.trade_id
   OR body#>'{selection,lots}' IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 ELSIF p.handler='financial_swap' THEN
  IF(body#>>'{selection,profileId}')::uuid IS DISTINCT FROM p.swap_profile_id OR(body#>>'{selection,profileRevision}')::integer IS DISTINCT FROM p.swap_profile_revision
   OR(body#>>'{selection,snapshotId}')::uuid IS DISTINCT FROM p.close_id OR(body#>>'{selection,determination,preview,expectedRevision}')::integer IS DISTINCT FROM p.swap_revision
   OR body#>'{selection,determination,correctionDueAt}' IS DISTINCT FROM 'null'::jsonb OR body#>'{selection,payment}' IS DISTINCT FROM 'null'::jsonb
  THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 ELSE RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
END$function$;
ALTER FUNCTION automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
CREATE OR REPLACE FUNCTION lifecycle.capture_action_authority(catalog uuid, kind text, body jsonb)
 RETURNS timestamp with time zone
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,kind,body,false);
 RETURN lifecycle.capture_action_authority_pre_schedule_scope(catalog,kind,body);
END$function$;
CREATE OR REPLACE FUNCTION lifecycle.option_proposal(catalog uuid, body jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,body->>'action',body,false);
 RETURN lifecycle.option_proposal_pre_schedule_scope(catalog,body);
END$function$;
CREATE OR REPLACE FUNCTION lifecycle.execute_option_action(catalog uuid, body jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,body->>'action',body,true);
 RETURN lifecycle.execute_option_action_pre_schedule_scope(catalog,body);
END$function$;
CREATE OR REPLACE FUNCTION lifecycle.record_native_action(catalog uuid, body jsonb, outcome jsonb, until_at timestamp with time zone)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,body->>'action',body,true);
 RETURN lifecycle.record_native_action_pre_schedule_scope(catalog,body,outcome,until_at);
END$function$;
CREATE OR REPLACE FUNCTION lifecycle.command_status(catalog uuid, operation uuid, kind text, body jsonb, permission text, until_at timestamp with time zone)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,kind,body,false);
 RETURN lifecycle.command_status_pre_schedule_scope(catalog,operation,kind,body,permission,until_at);
END$function$;
CREATE OR REPLACE FUNCTION lifecycle.finish_command(catalog uuid, operation uuid, kind text, body jsonb, permission text, until_at timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,kind,body,true);
 PERFORM lifecycle.finish_command_pre_schedule_scope(catalog,operation,kind,body,permission,until_at);
END$function$;
CREATE FUNCTION automation.require_lifecycle_command_result() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE token uuid;r automation.runs;result automation.lifecycle_results;BEGIN
 SELECT q.id INTO token FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
 JOIN automation.runs occurrence ON occurrence.id=b.run_id JOIN automation.jobs task ON task.id=occurrence.job_id
 WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND task.operation='lifecycle_action_v1';
 IF NOT FOUND THEN RETURN NEW;END IF;
 r=automation.lock_lifecycle_run();
 SELECT * INTO result FROM automation.lifecycle_results WHERE run_id=r.id;
 IF(result.execution_context_id,result.catalog_id,result.command_id,result.mode) IS DISTINCT FROM(token,NEW.catalog_id,NEW.operation_id,'automatic')
  OR NEW.operation_id IS DISTINCT FROM r.id OR result.outcome IS DISTINCT FROM NEW.outcome
 THEN RAISE EXCEPTION 'lifecycle_schedule_result_required';END IF;
 PERFORM automation.require_lifecycle_action_scope(NEW.catalog_id,NEW.kind,NEW.request,true);RETURN NEW;
END$function$;
ALTER FUNCTION automation.require_lifecycle_command_result() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_lifecycle_command_result() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
CREATE CONSTRAINT TRIGGER lifecycle_task_result_required AFTER INSERT ON lifecycle.commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.require_lifecycle_command_result();
COMMIT;
