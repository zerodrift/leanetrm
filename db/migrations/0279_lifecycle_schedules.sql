-- Current consumer: one source-derived native lifecycle occurrence at a
-- recorded close, under exact saved settings and fresh execution knowledge.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('automation.authorized(automation.jobs)');
 IF NOT FOUND OR md5(p.prosrc)<>'b0378540f9c7d8e5ba2a4d87dc72634f' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected helper automation.authorized(automation.jobs)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION automation.authorized(','FUNCTION automation.authorized_pre_lifecycle_jobs('),'authorized.','authorized_pre_lifecycle_jobs.');
END$guard$;
ALTER FUNCTION automation.authorized_pre_lifecycle_jobs(automation.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.authorized_pre_lifecycle_jobs(automation.jobs) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('automation.pilot_authorized(automation.jobs,uuid)');
 IF NOT FOUND OR md5(p.prosrc)<>'663e352f802caa75a052e0d9a9afc883' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected helper automation.pilot_authorized(automation.jobs,uuid)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION automation.pilot_authorized(','FUNCTION automation.pilot_authorized_pre_lifecycle_jobs('),'pilot_authorized.','pilot_authorized_pre_lifecycle_jobs.');
END$guard$;
ALTER FUNCTION automation.pilot_authorized_pre_lifecycle_jobs(automation.jobs,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.pilot_authorized_pre_lifecycle_jobs(automation.jobs,uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('automation.pilot_service_eligible(automation.jobs,automation.workers)');
 IF NOT FOUND OR md5(p.prosrc)<>'c56ead2527494b16725913babaefb29f' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected helper automation.pilot_service_eligible(automation.jobs,automation.workers)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION automation.pilot_service_eligible(','FUNCTION automation.pilot_service_eligible_pre_lifecycle_jobs('),'pilot_service_eligible.','pilot_service_eligible_pre_lifecycle_jobs.');
END$guard$;
ALTER FUNCTION automation.pilot_service_eligible_pre_lifecycle_jobs(automation.jobs,automation.workers) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.pilot_service_eligible_pre_lifecycle_jobs(automation.jobs,automation.workers) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.has_permission(text)');
 IF NOT FOUND OR md5(p.prosrc)<>'1bd44e273289f4d6ca9203df697b6bca' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected helper security.has_permission(text)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.has_permission(','FUNCTION security.has_permission_pre_lifecycle_jobs('),'has_permission.','has_permission_pre_lifecycle_jobs.');
END$guard$;
ALTER FUNCTION security.has_permission_pre_lifecycle_jobs(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.has_permission_pre_lifecycle_jobs(text) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.authority_until(text)');
 IF NOT FOUND OR md5(p.prosrc)<>'20c5f2a85dbc9bed7bb03acfe2231660' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'timestamptz'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected helper security.authority_until(text)';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.authority_until(','FUNCTION security.authority_until_pre_lifecycle_jobs('),'authority_until.','authority_until_pre_lifecycle_jobs.');
END$guard$;
ALTER FUNCTION security.authority_until_pre_lifecycle_jobs(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.authority_until_pre_lifecycle_jobs(text) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
CREATE TABLE automation.lifecycle_profiles(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,close_id uuid NOT NULL,
 handler text NOT NULL,action text NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,occurrence_id uuid NOT NULL,
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,
 knowledge_policy text NOT NULL CHECK(knowledge_policy='current_knowledge_at_execution_v1'),
 settings_policy text NOT NULL CHECK(settings_policy='source_known_exact_action_settings_v1'),
 input_policy text NOT NULL CHECK(input_policy='source_derived_only_no_cash_corrections_v1'),
 future_revision integer,option_revision integer,swap_revision integer,option_profile_revision integer,swap_profile_id uuid,swap_profile_revision integer,
 margin_policy_id uuid,margin_policy_revision integer,
 UNIQUE(id,tenant_id,workspace_id),UNIQUE(id,catalog_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id),
 FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,trade_id,future_revision) REFERENCES trading.trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,trade_id,option_revision) REFERENCES trading.futures_option_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,trade_id,swap_revision) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,trade_id,action,option_profile_revision) REFERENCES lifecycle.option_profiles(catalog_id,trade_id,action,revision),
 FOREIGN KEY(catalog_id,swap_profile_id,swap_profile_revision) REFERENCES lifecycle.swap_profiles(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,margin_policy_id,margin_policy_revision) REFERENCES settlement_data.futures_margin_policies(catalog_id,id,revision),
 CHECK((handler='futures' AND action='futures.margin' AND future_revision IS NOT NULL AND margin_policy_id IS NOT NULL AND margin_policy_revision IS NOT NULL
   AND option_revision IS NULL AND swap_revision IS NULL AND option_profile_revision IS NULL AND swap_profile_id IS NULL AND swap_profile_revision IS NULL)
 OR(handler='listed_option' AND action='option.expiry' AND option_revision IS NOT NULL AND option_profile_revision IS NOT NULL AND margin_policy_id IS NOT NULL AND margin_policy_revision IS NOT NULL
   AND future_revision IS NULL AND swap_revision IS NULL AND swap_profile_id IS NULL AND swap_profile_revision IS NULL)
 OR(handler='financial_swap' AND action='swap.determine' AND swap_revision IS NOT NULL AND swap_profile_id IS NOT NULL AND swap_profile_revision IS NOT NULL
   AND future_revision IS NULL AND option_revision IS NULL AND option_profile_revision IS NULL AND margin_policy_id IS NULL AND margin_policy_revision IS NULL))
);
CREATE TABLE automation.lifecycle_future_targets(
 profile_id uuid PRIMARY KEY,catalog_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,policy_id uuid NOT NULL,policy_revision integer NOT NULL,occurrence_id uuid NOT NULL,
 FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.lifecycle_profiles(id,catalog_id),
 FOREIGN KEY(catalog_id,trade_id,binding_id) REFERENCES settlement_data.futures_margin_adoptions(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision,occurrence_id) REFERENCES settlement_data.futures_margin_policy_occurrences(catalog_id,policy_id,policy_revision,id)
);
ALTER TABLE automation.jobs ADD COLUMN lifecycle_profile_id uuid;
ALTER TABLE automation.jobs ADD FOREIGN KEY(lifecycle_profile_id,tenant_id,workspace_id) REFERENCES automation.lifecycle_profiles(id,tenant_id,workspace_id);
DO $constraint$BEGIN
 IF (SELECT pg_get_expr(conbin,conrelid) FROM pg_constraint WHERE conrelid='automation.jobs'::regclass AND conname='jobs_operation_check') IS DISTINCT FROM $expected$(operation = ANY (ARRAY['worker_check'::text, 'swarm_job'::text, 'generated_pilot_close_v1'::text, 'close_book_risk_v1'::text]))$expected$
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected jobs constraint jobs_operation_check';END IF;
END$constraint$;
ALTER TABLE automation.jobs DROP CONSTRAINT jobs_operation_check;
DO $constraint$BEGIN
 IF (SELECT pg_get_expr(conbin,conrelid) FROM pg_constraint WHERE conrelid='automation.jobs'::regclass AND conname='jobs_check') IS DISTINCT FROM $expected$(((operation = 'worker_check'::text) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NULL)) OR ((operation = 'swarm_job'::text) AND (profile_id IS NOT NULL) AND (profile_version IS NOT NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NULL)) OR ((operation = 'generated_pilot_close_v1'::text) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NOT NULL) AND (pilot_profile_version IS NOT NULL) AND (close_risk_profile_id IS NULL) AND ((interval_seconds IS NULL) OR (interval_seconds = 86400))) OR ((operation = 'close_book_risk_v1'::text) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NOT NULL) AND (interval_seconds IS NULL)))$expected$
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected jobs constraint jobs_check';END IF;
END$constraint$;
ALTER TABLE automation.jobs DROP CONSTRAINT jobs_check;
ALTER TABLE automation.jobs ADD CONSTRAINT jobs_operation_check CHECK(operation IN('worker_check','swarm_job','generated_pilot_close_v1','close_book_risk_v1','lifecycle_action_v1'));
ALTER TABLE automation.jobs ADD CONSTRAINT jobs_check CHECK(
 (lifecycle_profile_id IS NULL AND(
 (operation='worker_check' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NULL)
 OR(operation='swarm_job' AND profile_id IS NOT NULL AND profile_version IS NOT NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NULL)
 OR(operation='generated_pilot_close_v1' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NOT NULL AND pilot_profile_version IS NOT NULL AND close_risk_profile_id IS NULL AND(interval_seconds IS NULL OR interval_seconds=86400))
 OR(operation='close_book_risk_v1' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NOT NULL AND interval_seconds IS NULL)))
 OR(operation='lifecycle_action_v1' AND lifecycle_profile_id IS NOT NULL AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL AND close_risk_profile_id IS NULL AND interval_seconds IS NULL AND timeout_seconds<=30));

CREATE TABLE automation.lifecycle_admissions(
 context_id uuid PRIMARY KEY REFERENCES automation.pilot_run_contexts(context_id),security_generation bigint NOT NULL,
 authority_until ref_data.instant NOT NULL,checked_at ref_data.instant NOT NULL,CHECK(checked_at<authority_until)
);
CREATE TABLE automation.lifecycle_sources(
 context_id uuid PRIMARY KEY REFERENCES automation.lifecycle_admissions(context_id),catalog_id uuid NOT NULL,source_context_id uuid NOT NULL,
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
DO $tables$DECLARE item text;BEGIN
 FOREACH item IN ARRAY ARRAY['lifecycle_profiles','lifecycle_future_targets','lifecycle_admissions','lifecycle_sources'] LOOP
  EXECUTE format('ALTER TABLE automation.%I OWNER TO openetrm_security_owner',item);
  EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',item);
  EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',item);
  EXECUTE format('CREATE POLICY owner_access ON automation.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',item);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',item);
 END LOOP;
END$tables$;

-- Exact product settings decide required capabilities, even before any gas
-- inventory exists. Service capabilities never widen an owner's task scope.
CREATE FUNCTION automation.lifecycle_permissions(profile uuid) RETURNS text[]
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE p automation.lifecycle_profiles;permissions text[];physical boolean;BEGIN
 SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=profile;
 IF p.id IS NULL THEN RETURN ARRAY[]::text[];END IF;
 permissions=ARRAY['RunJobs','ReadMarketData','ReadTrades',CASE WHEN p.handler='listed_option' THEN 'BookTrade' ELSE 'AmendTrade' END];
 SELECT delivery_profile_id IS NOT NULL INTO physical FROM settlement_data.futures_margin_policies m
  WHERE(m.catalog_id,m.id,m.revision)=(p.catalog_id,p.margin_policy_id,p.margin_policy_revision);
 IF physical THEN permissions=permissions||ARRAY['ReadGasOwnedHoldings']||CASE WHEN p.handler='futures' THEN ARRAY['RecordGasOwnedHoldings'] ELSE ARRAY[]::text[] END;END IF;
 RETURN permissions;
END$function$;
CREATE FUNCTION automation.lifecycle_authorized(j automation.jobs,service uuid,checked_at timestamptz) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE p automation.lifecycle_profiles;delegation security.agent_delegations;required_permission text;permissions text[];BEGIN
 IF j.operation IS DISTINCT FROM 'lifecycle_action_v1' OR NOT automation.authorized_pre_pilot(j) THEN RETURN false;END IF;
 SELECT * INTO p FROM automation.lifecycle_profiles WHERE(id,tenant_id,workspace_id)=(j.lifecycle_profile_id,j.tenant_id,j.workspace_id);
 IF p.id IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.catalog_id,j.tenant_id,j.workspace_id)) THEN RETURN false;END IF;
 permissions=automation.lifecycle_permissions(p.id);
 IF cardinality(permissions)<4 THEN RETURN false;END IF;
 IF j.delegation_id IS NOT NULL THEN
  SELECT * INTO delegation FROM security.agent_delegations d WHERE d.id=j.delegation_id;
  IF delegation.id IS NULL OR delegation.revoked OR(delegation.agent_id,delegation.tenant_id,delegation.workspace_id) IS DISTINCT FROM(j.owner_id,j.tenant_id,j.workspace_id)
   OR checked_at<delegation.granted_from OR checked_at>=delegation.granted_to
   OR NOT security.principal_has_permission(delegation.delegator_id,j.tenant_id,j.workspace_id,'CreateDelegations',checked_at) THEN RETURN false;END IF;
 END IF;
 FOREACH required_permission IN ARRAY permissions LOOP
  IF NOT security.principal_has_permission(j.owner_id,j.tenant_id,j.workspace_id,required_permission,checked_at)
   OR(service IS NOT NULL AND NOT security.principal_has_permission(service,j.tenant_id,j.workspace_id,required_permission,checked_at))
   OR(j.delegation_id IS NOT NULL AND(NOT security.principal_has_permission(delegation.delegator_id,j.tenant_id,j.workspace_id,required_permission,checked_at)
    OR NOT EXISTS(SELECT 1 FROM security.delegation_permissions d WHERE d.delegation_id=delegation.id AND d.permission=required_permission))) THEN RETURN false;END IF;
 END LOOP;
 RETURN true;
END$function$;
CREATE FUNCTION automation.lifecycle_until(j automation.jobs,service uuid,checked_at timestamptz) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz=j.expires_at;delegation security.agent_delegations;permission text;BEGIN
 IF NOT automation.lifecycle_authorized(j,service,checked_at) THEN RETURN '-infinity';END IF;
 IF j.delegation_id IS NOT NULL THEN
  SELECT * INTO delegation FROM security.agent_delegations d WHERE d.id=j.delegation_id;
  until_at=least(until_at,delegation.granted_to,security.grant_until(delegation.delegator_id,j.tenant_id,j.workspace_id,'CreateDelegations',checked_at));
 END IF;
 FOREACH permission IN ARRAY automation.lifecycle_permissions(j.lifecycle_profile_id) LOOP
  until_at=least(until_at,security.grant_until(j.owner_id,j.tenant_id,j.workspace_id,permission,checked_at));
  IF service IS NOT NULL THEN until_at=least(until_at,security.grant_until(service,j.tenant_id,j.workspace_id,permission,checked_at));END IF;
  IF j.delegation_id IS NOT NULL THEN until_at=least(until_at,security.grant_until(delegation.delegator_id,j.tenant_id,j.workspace_id,permission,checked_at));END IF;
 END LOOP;
 RETURN until_at;
END$function$;
CREATE OR REPLACE FUNCTION automation.authorized(j automation.jobs) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT CASE WHEN j.operation='lifecycle_action_v1' THEN automation.lifecycle_authorized(j,NULL,clock_timestamp()) ELSE automation.authorized_pre_lifecycle_jobs(j) END;
$function$;
CREATE OR REPLACE FUNCTION automation.pilot_authorized(j automation.jobs,service uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF j.operation='lifecycle_action_v1' THEN RETURN automation.lifecycle_authorized(j,service,clock_timestamp());END IF;
 RETURN automation.pilot_authorized_pre_lifecycle_jobs(j,service);
END$function$;
CREATE OR REPLACE FUNCTION automation.pilot_service_eligible(j automation.jobs,w automation.workers) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT CASE WHEN j.operation='lifecycle_action_v1' THEN w.kind='scheduler' AND(j.tenant_id,j.workspace_id)=(w.tenant_id,w.workspace_id)
  AND automation.lifecycle_authorized(j,w.principal_id,clock_timestamp()) AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l JOIN automation.lifecycle_profiles p
   ON p.id=j.lifecycle_profile_id WHERE l.worker_login=w.login AND l.catalog_id=p.catalog_id)
 ELSE automation.pilot_service_eligible_pre_lifecycle_jobs(j,w) END;
$function$;

CREATE FUNCTION automation.lifecycle_context_until(token uuid,permission text) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE q security.request_contexts;j automation.jobs;r automation.runs;w automation.workers;b automation.pilot_run_contexts;a automation.lifecycle_admissions;checked_at timestamptz=clock_timestamp();BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE id=token AND runtime_login=session_user AND backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO b FROM automation.pilot_run_contexts WHERE context_id=q.id;
 SELECT * INTO a FROM automation.lifecycle_admissions WHERE context_id=q.id;
 SELECT * INTO r FROM automation.runs WHERE id=b.run_id;
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 SELECT * INTO w FROM automation.workers WHERE login=b.worker_login;
 IF permission IS NULL OR q.id IS NULL OR a.context_id IS NULL OR j.operation IS DISTINCT FROM 'lifecycle_action_v1' OR NOT(permission=ANY(automation.lifecycle_permissions(j.lifecycle_profile_id)))
  OR(q.actor_id,q.tenant_id,q.workspace_id) IS DISTINCT FROM(w.principal_id,w.tenant_id,w.workspace_id)
  OR(b.fence,b.worker_id,b.worker_login) IS DISTINCT FROM(r.fence,r.worker_id,r.worker_login)
  OR a.security_generation IS DISTINCT FROM(SELECT generation FROM security.epoch)
  OR r.state<>'running' OR j.state NOT IN('active','paused') OR NOT automation.pilot_service_eligible(j,w)
  OR NOT EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l WHERE(l.worker_login,l.runtime_login)=(w.login,session_user))
 THEN RETURN '-infinity';END IF;
 RETURN least(q.expires_at,a.authority_until,r.lease_until,r.deadline_at,j.expires_at,automation.lifecycle_until(j,w.principal_id,checked_at));
END$function$;
CREATE OR REPLACE FUNCTION security.has_permission(permission text) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE token uuid;BEGIN
 SELECT q.id INTO token FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
 JOIN automation.runs r ON r.id=b.run_id JOIN automation.jobs j ON j.id=r.job_id
 WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND j.operation='lifecycle_action_v1';
 IF FOUND THEN RETURN automation.lifecycle_context_until(token,permission)>clock_timestamp();END IF;
 RETURN security.has_permission_pre_lifecycle_jobs(permission);
END$function$;
CREATE OR REPLACE FUNCTION security.authority_until(permission text) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE token uuid;BEGIN
 SELECT q.id INTO token FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
 JOIN automation.runs r ON r.id=b.run_id JOIN automation.jobs j ON j.id=r.job_id
 WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND j.operation='lifecycle_action_v1';
 IF FOUND THEN RETURN automation.lifecycle_context_until(token,permission);END IF;
 RETURN security.authority_until_pre_lifecycle_jobs(permission);
END$function$;

CREATE FUNCTION automation.require_lifecycle_settings(p automation.lifecycle_profiles) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF p.policy_revision IS DISTINCT FROM(SELECT max(revision) FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id)=(p.catalog_id,p.policy_id))
  OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action)=(p.catalog_id,p.policy_id,p.policy_revision,p.action))
 THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 IF p.handler='futures' THEN
  IF p.future_revision IS DISTINCT FROM(SELECT max(revision) FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id)=(p.catalog_id,p.trade_id))
   OR NOT EXISTS(SELECT 1 FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision,r.binding_id)=(p.catalog_id,p.trade_id,p.future_revision,p.binding_id))
   OR NOT EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a WHERE(a.catalog_id,a.trade_id,a.binding_id,a.policy_id,a.policy_revision)=
     (p.catalog_id,p.trade_id,p.binding_id,p.margin_policy_id,p.margin_policy_revision))
  THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 ELSIF p.handler='listed_option' THEN
  IF p.option_revision IS DISTINCT FROM(SELECT max(revision) FROM trading.futures_option_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(p.catalog_id,p.trade_id))
   OR p.option_profile_revision IS DISTINCT FROM(SELECT max(revision) FROM lifecycle.option_profiles s WHERE(s.catalog_id,s.trade_id,s.action)=(p.catalog_id,p.trade_id,p.action))
   OR p.occurrence_id IS DISTINCT FROM p.trade_id
   OR NOT EXISTS(SELECT 1 FROM lifecycle.option_profiles s JOIN trading.futures_option_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(s.catalog_id,s.trade_id,s.option_revision)
    WHERE(s.catalog_id,s.trade_id,s.action,s.revision,s.policy_id,s.policy_revision,s.margin_policy_id,s.margin_policy_revision,r.option_instrument_id)=
     (p.catalog_id,p.trade_id,p.action,p.option_profile_revision,p.policy_id,p.policy_revision,p.margin_policy_id,p.margin_policy_revision,p.binding_id))
  THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 ELSIF p.handler='financial_swap' THEN
  IF p.swap_revision IS DISTINCT FROM(SELECT max(revision) FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(p.catalog_id,p.trade_id))
   OR p.swap_profile_revision IS DISTINCT FROM(SELECT max(revision) FROM lifecycle.swap_profiles s WHERE(s.catalog_id,s.id)=(p.catalog_id,p.swap_profile_id))
   OR p.occurrence_id IS DISTINCT FROM lifecycle.swap_profile_occurrence(p.catalog_id,p.swap_profile_id,p.swap_profile_revision)
   OR NOT EXISTS(SELECT 1 FROM lifecycle.swap_profiles s JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(s.catalog_id,s.trade_id,s.origin_revision)
    WHERE(s.catalog_id,s.id,s.revision,s.trade_id,s.origin_revision,s.policy_id,s.policy_revision,r.binding_id)=
     (p.catalog_id,p.swap_profile_id,p.swap_profile_revision,p.trade_id,p.swap_revision,p.policy_id,p.policy_revision,p.binding_id))
  THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 ELSE RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
END$function$;

CREATE FUNCTION automation.create_lifecycle_job(catalog uuid,request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE c security.request_contexts;p automation.lifecycle_profiles;j automation.jobs;anchor market_data.market_closes;
 schedule jsonb=request->'schedule';definition jsonb=request->'definition';target jsonb=definition->'target';settings jsonb=definition->'settings';previous automation.commands;
 candidate jsonb;permission text;bounds jsonb='{}';until_at timestamptz;response jsonb;operation uuid;checked_at timestamptz=clock_timestamp();generation bigint;
BEGIN
 -- Candidates are captured before the first epoch/publication/principal wait.
 SELECT e.generation INTO generation FROM security.epoch e;
 FOREACH permission IN ARRAY ARRAY['RunJobs','ReadMarketData','ReadTrades','BookTrade','AmendTrade','ReadGasOwnedHoldings','RecordGasOwnedHoldings'] LOOP
  bounds=bounds||jsonb_build_object(permission,CASE WHEN security.has_permission(permission) THEN security.authority_until(permission) ELSE '-infinity'::timestamptz END);
 END LOOP;
 IF jsonb_typeof(request) IS DISTINCT FROM 'object' OR request-ARRAY['schedule','definition']<>'{}'::jsonb
  OR jsonb_typeof(schedule) IS DISTINCT FROM 'object' OR schedule-ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']<>'{}'::jsonb
  OR NOT(schedule ?& ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion'])
  OR jsonb_typeof(definition) IS DISTINCT FROM 'object' OR definition-ARRAY['closeId','target','action','occurrenceId','policyId','policyRevision','knowledgePolicy','settingsPolicy','inputPolicy','settings']<>'{}'::jsonb
  OR NOT(definition ?& ARRAY['closeId','target','action','occurrenceId','policyId','policyRevision','knowledgePolicy','settingsPolicy','inputPolicy','settings'])
  OR jsonb_typeof(target) IS DISTINCT FROM 'object' OR target-ARRAY['handler','scopeId','bindingId']<>'{}'::jsonb OR NOT(target ?& ARRAY['handler','scopeId','bindingId'])
  OR jsonb_typeof(settings) IS DISTINCT FROM 'object' OR settings-ARRAY['tradeRevision','optionProfileRevision','swapProfileId','swapProfileRevision','marginPolicyId','marginPolicyRevision']<>'{}'::jsonb
  OR NOT(settings ?& ARRAY['tradeRevision','optionProfileRevision','swapProfileId','swapProfileRevision','marginPolicyId','marginPolicyRevision'])
  OR schedule->>'operation' IS DISTINCT FROM 'lifecycle_action_v1' OR schedule->'intervalSeconds' IS DISTINCT FROM 'null'::jsonb
  OR schedule->'profileId' IS DISTINCT FROM 'null'::jsonb OR schedule->'profileVersion' IS DISTINCT FROM 'null'::jsonb
  OR definition->>'knowledgePolicy' IS DISTINCT FROM 'current_knowledge_at_execution_v1'
  OR definition->>'settingsPolicy' IS DISTINCT FROM 'source_known_exact_action_settings_v1'
  OR definition->>'inputPolicy' IS DISTINCT FROM 'source_derived_only_no_cash_corrections_v1'
  OR(schedule->>'timeoutSeconds')::integer NOT BETWEEN 1 AND 30 THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 operation=(schedule->>'requestId')::uuid;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 c=automation.actor();
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;
 IF generation IS DISTINCT FROM(SELECT e.generation FROM security.epoch e) THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 c=automation.actor();
 SELECT * INTO previous FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  IF previous.request IS DISTINCT FROM request THEN RAISE EXCEPTION 'job_conflict';END IF;
 ELSE
  SELECT * INTO previous FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND x.request-'schedule'=create_lifecycle_job.request-'schedule' AND(x.request->'schedule')-'requestId'=schedule-'requestId' ORDER BY x.committed_at,x.request_id LIMIT 1;
 END IF;
 IF previous.job_id IS NOT NULL THEN
  -- Acknowledgement precedes every mutable source/settings lookup. It never
  -- re-enrolls old settings or rewinds the current financial state.
  SELECT * INTO j FROM automation.jobs WHERE id=previous.job_id;
  IF NOT automation.authorized(j) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
  until_at=least(c.expires_at,j.expires_at);
  FOR permission IN SELECT unnest(automation.lifecycle_permissions(j.lifecycle_profile_id)) LOOP
   until_at=least(until_at,(bounds->>permission)::timestamptz);
  END LOOP;
  IF until_at<=clock_timestamp() THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
  IF previous.request_id<>operation THEN
   INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,request,previous.response,clock_timestamp());
   INSERT INTO automation.lifecycle_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,until_at);
  END IF;
  RETURN jsonb_build_object('outcome',previous.response,'authorityUntil',until_at);
 END IF;
 SELECT * INTO anchor FROM market_data.market_closes a WHERE(a.catalog_id,a.id)=(catalog,(definition->>'closeId')::uuid) AND security.market_close_visible(a.catalog_id,a.id);
 IF anchor.id IS NULL THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 p.id=gen_random_uuid();p.catalog_id=catalog;p.tenant_id=c.tenant_id;p.workspace_id=c.workspace_id;p.close_id=anchor.id;
 p.handler=target->>'handler';p.action=definition->>'action';p.trade_id=(target->>'scopeId')::uuid;p.binding_id=(target->>'bindingId')::uuid;
 p.occurrence_id=(definition->>'occurrenceId')::uuid;p.policy_id=(definition->>'policyId')::uuid;p.policy_revision=(definition->>'policyRevision')::integer;
 p.knowledge_policy=definition->>'knowledgePolicy';p.settings_policy=definition->>'settingsPolicy';p.input_policy=definition->>'inputPolicy';
 p.option_profile_revision=(settings->>'optionProfileRevision')::integer;p.swap_profile_id=(settings->>'swapProfileId')::uuid;p.swap_profile_revision=(settings->>'swapProfileRevision')::integer;
 p.margin_policy_id=(settings->>'marginPolicyId')::uuid;p.margin_policy_revision=(settings->>'marginPolicyRevision')::integer;
 IF(p.handler,p.action)=('futures','futures.margin') THEN
  p.future_revision=(settings->>'tradeRevision')::integer;
  SELECT m.definition_revision_id INTO p.definition_revision_id FROM settlement_data.futures_margin_adoptions a
   JOIN trading.trade_revisions r ON(r.catalog_id,r.trade_id,r.revision,r.binding_id)=(a.catalog_id,a.trade_id,p.future_revision,a.binding_id)
   JOIN settlement_data.futures_margin_policies m ON(m.catalog_id,m.id,m.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
   WHERE(a.catalog_id,a.trade_id,a.binding_id,a.policy_id,a.policy_revision)=(catalog,p.trade_id,p.binding_id,p.margin_policy_id,p.margin_policy_revision)
    AND EXISTS(SELECT 1 FROM settlement_data.futures_margin_policy_occurrences o WHERE(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(a.catalog_id,a.policy_id,a.policy_revision,p.occurrence_id) AND o.at<=anchor.at);
 ELSIF(p.handler,p.action)=('listed_option','option.expiry') THEN
  p.option_revision=(settings->>'tradeRevision')::integer;
  SELECT selected.definition_revision_id INTO p.definition_revision_id FROM lifecycle.option_profiles selected
   JOIN trading.futures_option_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(selected.catalog_id,selected.trade_id,selected.option_revision)
   JOIN market_data.contracts contract ON(contract.catalog_id,contract.revision_id,contract.id)=(r.catalog_id,r.definition_revision_id,r.contract_id)
   WHERE(selected.catalog_id,selected.trade_id,selected.action,selected.revision,selected.option_revision,selected.policy_id,selected.policy_revision,selected.margin_policy_id,selected.margin_policy_revision,r.option_instrument_id)=
    (catalog,p.trade_id,p.action,p.option_profile_revision,p.option_revision,p.policy_id,p.policy_revision,p.margin_policy_id,p.margin_policy_revision,p.binding_id)
    AND contract.expiry_at<=anchor.at;
 ELSIF(p.handler,p.action)=('financial_swap','swap.determine') THEN
  p.swap_revision=(settings->>'tradeRevision')::integer;
  SELECT r.definition_revision_id INTO p.definition_revision_id FROM lifecycle.swap_profiles selected
   JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(selected.catalog_id,selected.trade_id,selected.origin_revision)
   WHERE(selected.catalog_id,selected.id,selected.revision,selected.trade_id,selected.origin_revision,selected.action,selected.policy_id,selected.policy_revision,r.binding_id)=
    (catalog,p.swap_profile_id,p.swap_profile_revision,p.trade_id,p.swap_revision,p.action,p.policy_id,p.policy_revision,p.binding_id)
    AND lifecycle.swap_profile_occurrence(selected.catalog_id,selected.id,selected.revision)=p.occurrence_id;
 ELSE RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 IF p.definition_revision_id IS NULL OR p.definition_revision_id<>anchor.definition_revision_id THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 PERFORM automation.require_lifecycle_settings(p);
 IF NOT EXISTS(SELECT 1 FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action)=(catalog,p.policy_id,p.policy_revision,p.action)
   AND a.from_at<=anchor.at AND anchor.at<a.until_at) THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
  IF(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND(state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=jobs.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
  INSERT INTO automation.lifecycle_profiles SELECT(p).*;
  IF p.handler='futures' THEN INSERT INTO automation.lifecycle_future_targets VALUES(p.id,catalog,p.trade_id,p.binding_id,p.margin_policy_id,p.margin_policy_revision,p.occurrence_id);END IF;
  j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;
  j.name=schedule->>'name';j.operation=schedule->>'operation';j.state='active';j.version=1;j.next_due_at=settlement_data.futures_margin_utc_instant(schedule->>'dueAt');j.expires_at=settlement_data.futures_margin_utc_instant(schedule->>'expiresAt');
  j.interval_seconds=NULL;j.time_zone=schedule->>'timeZone';j.misfire=schedule->>'misfire';j.overlap_policy=schedule->>'overlap';j.timeout_seconds=(schedule->>'timeoutSeconds')::integer;j.maximum_attempts=(schedule->>'maximumAttempts')::integer;
  j.lifecycle_profile_id=p.id;j.created_at=clock_timestamp();j.last_reconciled_at='-infinity';
  IF j.expires_at<=clock_timestamp() OR j.expires_at<=j.next_due_at OR j.expires_at>clock_timestamp()+interval '366 days' OR j.next_due_at<clock_timestamp()-interval '1 day'
   OR NOT automation.authorized(j) THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
  INSERT INTO automation.jobs SELECT(j).*;
 until_at=least(c.expires_at,j.expires_at);
 FOR permission IN SELECT unnest(automation.lifecycle_permissions(j.lifecycle_profile_id)) LOOP
  until_at=least(until_at,(bounds->>permission)::timestamptz);
 END LOOP;
 IF until_at<=clock_timestamp() OR NOT automation.authorized(j) THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 response=automation.description(j)||jsonb_build_object('lifecycle',definition);
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,request,response,clock_timestamp());
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,'created',clock_timestamp());
 INSERT INTO automation.lifecycle_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,until_at);
 RETURN jsonb_build_object('outcome',response,'authorityUntil',until_at);
END$function$;

CREATE FUNCTION automation.claim_lifecycle_next(worker_id uuid,lease_seconds integer) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;BEGIN
 w=automation.worker();n=clock_timestamp();
 IF worker_id IS NULL OR worker_id='00000000-0000-0000-0000-000000000000'::uuid OR lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 120 THEN RAISE EXCEPTION 'job_input';END IF;
 IF w.kind<>'scheduler' THEN RETURN NULL;END IF;
 FOR j IN SELECT * FROM automation.jobs task WHERE(task.tenant_id,task.workspace_id)=(w.tenant_id,w.workspace_id)
  AND task.operation='lifecycle_action_v1' AND task.state='active' AND task.next_due_at<=n
  ORDER BY task.last_reconciled_at,task.next_due_at,task.id FOR UPDATE SKIP LOCKED LIMIT 32 LOOP
  n=clock_timestamp();
  IF NOT automation.pilot_service_eligible(j,w) THEN CONTINUE;END IF;
  IF j.expires_at<=n THEN
   UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='job_expired' WHERE job_id=j.id AND state='running';
   UPDATE automation.jobs SET state='expired',version=version+1 WHERE id=j.id;CONTINUE;
  END IF;
  UPDATE automation.jobs SET last_reconciled_at=n WHERE id=j.id;
  SELECT * INTO r FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at FOR UPDATE;
  n=clock_timestamp();w=automation.worker();
  IF NOT automation.pilot_service_eligible(j,w) OR j.expires_at<=n THEN CONTINUE;END IF;
  IF r.id IS NOT NULL THEN
   IF r.state<>'running' OR r.lease_until>n THEN CONTINUE;END IF;
   IF r.attempt>=j.maximum_attempts OR r.deadline_at<=n THEN
    UPDATE automation.runs SET state='failed',finished_at=n,error_code='lease_expired',fence=fence+1 WHERE id=r.id;
    UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;CONTINUE;
   END IF;
   UPDATE automation.runs SET fence=fence+1,attempt=attempt+1,worker_id=claim_lifecycle_next.worker_id,worker_login=session_user,
    lease_until=least(n+make_interval(secs=>lease_seconds),deadline_at,j.expires_at) WHERE id=r.id RETURNING * INTO r;
  ELSE
   INSERT INTO automation.runs(id,job_id,tenant_id,workspace_id,due_at,job_version,state,fence,attempt,worker_id,worker_login,lease_until,started_at,deadline_at)
    VALUES(gen_random_uuid(),j.id,j.tenant_id,j.workspace_id,j.next_due_at,j.version,'running',1,1,worker_id,session_user,
     least(n+make_interval(secs=>lease_seconds),n+make_interval(secs=>j.timeout_seconds),j.expires_at),n,least(n+make_interval(secs=>j.timeout_seconds),j.expires_at)) RETURNING * INTO r;
  END IF;
  INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,'claimed',n,r.fence);
  RETURN jsonb_build_object('id',r.id,'jobId',j.id,'operation',j.operation,'fence',r.fence,'workerId',r.worker_id,'leaseUntil',r.lease_until,'deadlineAt',r.deadline_at,
   'dueAt',r.due_at,'intervalSeconds',NULL,'profile',NULL);
 END LOOP;
 RETURN NULL;
END$function$;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('automation.issue_pilot_context(uuid,bigint,uuid)');
 IF NOT FOUND OR md5(p.prosrc)<>'f5d5a778e735e77f1b5ae2592dd9e9d0' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'uuid'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_scheduler=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_schedule_upgrade: unexpected helper automation.issue_pilot_context(uuid,bigint,uuid)';END IF;
END$guard$;
CREATE FUNCTION automation.issue_lifecycle_context(run uuid,expected_fence bigint,worker uuid) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE checked_at timestamptz=clock_timestamp();generation bigint;until_at timestamptz;j automation.jobs;r automation.runs;w automation.workers;token uuid;BEGIN
 -- Do not call worker(), which takes the epoch lock, until original grant
 -- candidates and the original generation have been retained without a wait.
 SELECT e.generation INTO generation FROM security.epoch e;
 SELECT * INTO w FROM automation.workers WHERE login=session_user;
 SELECT * INTO r FROM automation.runs WHERE id=run;
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 IF w.login IS NULL OR w.kind<>'scheduler' OR j.operation IS DISTINCT FROM 'lifecycle_action_v1'
  OR(r.worker_login,r.worker_id,r.fence) IS DISTINCT FROM(session_user,worker,expected_fence)
  OR r.state<>'running' OR j.state NOT IN('active','paused') OR(w.tenant_id,w.workspace_id) IS DISTINCT FROM(j.tenant_id,j.workspace_id)
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 until_at=least(r.lease_until,r.deadline_at,j.expires_at,automation.lifecycle_until(j,w.principal_id,checked_at));
 IF until_at<=clock_timestamp() THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 -- Reuse the established dedicated-login qualification and current claim fence.
 token=automation.issue_pilot_context(run,expected_fence,worker);
 IF generation IS DISTINCT FROM(SELECT e.generation FROM security.epoch e) OR until_at<=clock_timestamp()
 THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 UPDATE security.request_contexts SET expires_at=least(expires_at,until_at) WHERE id=token;
 INSERT INTO automation.lifecycle_admissions VALUES(token,generation,until_at,checked_at);
 RETURN token;
END$function$;

CREATE FUNCTION automation.lock_lifecycle_run() RETURNS automation.runs
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE token uuid;until_at timestamptz;r automation.runs;BEGIN
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 until_at=automation.lifecycle_context_until(token,'RunJobs');
 IF until_at<=clock_timestamp() THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 SELECT runs.* INTO r FROM automation.runs runs JOIN automation.pilot_run_contexts b ON b.run_id=runs.id WHERE b.context_id=token FOR UPDATE OF runs;
 IF r.id IS NULL OR until_at<=clock_timestamp() OR automation.lifecycle_context_until(token,'RunJobs')<=clock_timestamp()
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 RETURN r;
END$function$;
CREATE FUNCTION automation.lifecycle_definition(p automation.lifecycle_profiles) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT jsonb_build_object('closeId',p.close_id,'target',jsonb_build_object('handler',p.handler,'scopeId',p.trade_id,'bindingId',p.binding_id),
  'action',p.action,'occurrenceId',p.occurrence_id,'policyId',p.policy_id,'policyRevision',p.policy_revision,'knowledgePolicy',p.knowledge_policy,'settingsPolicy',p.settings_policy,'inputPolicy',p.input_policy,
  'settings',jsonb_build_object('tradeRevision',CASE p.handler WHEN 'futures' THEN p.future_revision WHEN 'listed_option' THEN p.option_revision WHEN 'financial_swap' THEN p.swap_revision END,
   'optionProfileRevision',p.option_profile_revision,'swapProfileId',p.swap_profile_id,'swapProfileRevision',p.swap_profile_revision,'marginPolicyId',p.margin_policy_id,'marginPolicyRevision',p.margin_policy_revision));
$function$;
CREATE FUNCTION automation.capture_lifecycle_source() RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;anchor market_data.market_closes;token uuid;source uuid;mode text;BEGIN
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 PERFORM automation.require_lifecycle_settings(p);
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO anchor FROM market_data.market_closes WHERE(catalog_id,id)=(p.catalog_id,p.close_id);
 SELECT source_context_id INTO source FROM automation.lifecycle_sources WHERE context_id=token;
 IF source IS NULL THEN
  source=security.capture_date_context(p.catalog_id,anchor.business_date,NULL);
  INSERT INTO automation.lifecycle_sources VALUES(token,p.catalog_id,source);
 END IF;
 SELECT a.mode INTO mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 PERFORM automation.lock_lifecycle_run();
 RETURN jsonb_build_object('definition',automation.lifecycle_definition(p),
  'context',jsonb_build_object('contextId',source,'asOf',anchor.at,'businessDate',anchor.business_date,'snapshotId',p.close_id),'mode',mode);
END$function$;

DO $functions$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='automation'
  AND p.proname IN('lifecycle_permissions','lifecycle_authorized','lifecycle_until','lifecycle_context_until','require_lifecycle_settings','create_lifecycle_job','claim_lifecycle_next','issue_lifecycle_context','lock_lifecycle_run','capture_lifecycle_source','lifecycle_definition') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler',f.signature);
  IF f.proname IN('create_lifecycle_job','lock_lifecycle_run','capture_lifecycle_source','lifecycle_definition') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
  IF f.proname IN('claim_lifecycle_next','issue_lifecycle_context') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_scheduler',f.signature);END IF;
 END LOOP;
END$functions$;

CREATE TABLE automation.lifecycle_commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL,request_id uuid NOT NULL,authority_until ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,actor_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id,actor_id,request_id) REFERENCES automation.commands(tenant_id,workspace_id,actor_id,request_id)
);
ALTER TABLE automation.lifecycle_commands OWNER TO openetrm_security_owner;
ALTER TABLE automation.lifecycle_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE automation.lifecycle_commands FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON automation.lifecycle_commands TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.lifecycle_commands FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION automation.require_lifecycle_job_command(catalog uuid,request jsonb,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE c security.request_contexts;command automation.commands;j automation.jobs;permission text;BEGIN
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at OR NOT security.current_write_context() THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 c=automation.actor();
 SELECT * INTO command FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=
  (c.tenant_id,c.workspace_id,c.actor_id,(require_lifecycle_job_command.request#>>'{schedule,requestId}')::uuid);
 SELECT * INTO j FROM automation.jobs WHERE id=command.job_id;
 IF command.request IS DISTINCT FROM request OR NOT automation.authorized(j)
  OR NOT EXISTS(SELECT 1 FROM automation.lifecycle_profiles p WHERE(p.id,p.catalog_id)=(j.lifecycle_profile_id,catalog))
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 FOREACH permission IN ARRAY automation.lifecycle_permissions(j.lifecycle_profile_id) LOOP
  IF NOT security.has_permission(permission) OR security.authority_until(permission)<=clock_timestamp() THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 END LOOP;
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
END$function$;
CREATE FUNCTION automation.seal_lifecycle_job_command() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE command automation.commands;catalog uuid;BEGIN
 SELECT * INTO command FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=(NEW.tenant_id,NEW.workspace_id,NEW.actor_id,NEW.request_id);
 SELECT p.catalog_id INTO catalog FROM automation.jobs j JOIN automation.lifecycle_profiles p ON p.id=j.lifecycle_profile_id WHERE j.id=command.job_id;
 PERFORM automation.require_lifecycle_job_command(catalog,command.request,NEW.authority_until);RETURN NEW;
END$function$;
ALTER FUNCTION automation.require_lifecycle_job_command(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION automation.seal_lifecycle_job_command() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_lifecycle_job_command(uuid,jsonb,timestamptz),automation.seal_lifecycle_job_command() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler;
GRANT EXECUTE ON FUNCTION automation.require_lifecycle_job_command(uuid,jsonb,timestamptz) TO openetrm_runtime;
CREATE CONSTRAINT TRIGGER lifecycle_job_complete AFTER INSERT ON automation.lifecycle_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.seal_lifecycle_job_command();
COMMIT;
