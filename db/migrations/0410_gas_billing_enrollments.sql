-- Gas enrollment owns scope and scan progress; jobs/runs remain the execution owner.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $guard$ DECLARE f record;p pg_proc;BEGIN
 FOR f IN SELECT * FROM(VALUES
 ('automation.lifecycle_definition(automation.lifecycle_profiles)','9c50ac2bb4401d7bd25591a6a67b95a3e734764d7d49fd62c98acdfe2b391620','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','s'),
 ('automation.require_lifecycle_settings(automation.lifecycle_profiles)','895c4498022d07de2acb432352608d10f5b10860618e3679634055be893cc416','{openetrm_security_owner=X/openetrm_security_owner}','v'),
 ('automation.admit_occurrence_inputs()','d60600d834f6e66b4b6bec8bf009d7861ef8743adf580157dca7005ae61ca105','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.capture_lifecycle_source()','b915ce9ebc9d8fb40d6a4addec4e5474d3bafc545fb1b6a5a3545d220614ba13','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean)','5a3d4ea99e49627c54cfe8b9dd7490fb98e0ad7790d768ff58c12b2ee7544698','{openetrm_security_owner=X/openetrm_security_owner}','v'),
 ('automation.claim_lifecycle_next(uuid,integer)','49d76bc4cd1e0fc07c75eb327a48e20f81b1e4b72a151eb293bb5389450a9a35','{openetrm_security_owner=X/openetrm_security_owner,openetrm_scheduler=X/openetrm_security_owner}','v'),
 ('automation.publish_lifecycle_result(text,jsonb,jsonb)','e87596c77cf1a6ffe6f520b68e537823153f82585c426f8165a94b1a8a2ee683','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.finish_lifecycle_result()','45e02a7d694c22ed86a452c1968ae5c98ad9f3adf69a259b878d23016fc5f60e','{openetrm_security_owner=X/openetrm_security_owner}','v'),
 ('automation.create_lifecycle_job(uuid,jsonb)','ed18c49d1c5e1e53d6db6de867481762ad59aeea4ddd500ca15d2ffd30b58f5f','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.lifecycle_job_result(uuid)','7f2570687823453ea52a4749caf935c004fd837f4948bd72241e15bb658359f7','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('security.trading_access(uuid,text)','422b9a2343e622a55ea85cdc707c17762b3be087228d5a653a895875639bb5cb','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}','v'),
 ('settlement_data.require_futures_margin_authority(uuid,text,timestamp with time zone)','47bffc1556e61661b78632cca3d761126099dbddb20a9e178670e9058347d70e','{openetrm_security_owner=X/openetrm_security_owner}','v'),
 ('automation.create_job(jsonb)','440c5246b406dbebeac87a4b96457e51e307516b6819df07edeaaba64e9228fe','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.update_job(jsonb)','1c821c2a0b2a9716597002dd13a7219a600b6d05f49b3e595fdbf5721d36771a','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.control_job(jsonb)','f22809db751805e8202598045594444636ba5cd958e85f131ea1d5cd2c53f760','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.create_pilot_close_job(jsonb,jsonb)','19bd3294e316ef7b2caa6ba14c723b4d9b3c5e03219f65437415502efd8a481b','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.create_close_risk_job(jsonb,uuid,jsonb,bigint,bigint)','352d53e375cfb1ab121685cd41c15ecfbae3507edff7c8e9b7de237dbfcabce8','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'))v(signature,digest,acl,volatility) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=f.signature::regprocedure;
  IF encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>f.digest OR p.proowner<>'openetrm_security_owner'::regrole
   OR p.proacl::text IS DISTINCT FROM f.acl OR NOT p.prosecdef OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proisstrict OR p.proleakproof OR p.provolatile<>f.volatility OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_billing_enrollment_upgrade: unexpected helper %',p.oid::regprocedure;END IF;
 END LOOP;
END$guard$;
DO $guard$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='automation.jobs'::regclass AND conname='jobs_check' AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='da04e05a619db9551396ab7d575d735bf93fe64c7a8ce8df158fb1ef4ce08b7d') THEN RAISE EXCEPTION 'gas_billing_enrollment_upgrade: unexpected jobs_check';END IF;END$guard$;
DO $guard$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='automation.lifecycle_profiles'::regclass AND conname='lifecycle_profiles_check' AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='f17f7c51e810d0ac8530addee39f7af61b4a8554b7aa8e32d5eb030b8555743d') THEN RAISE EXCEPTION 'gas_billing_enrollment_upgrade: unexpected lifecycle_profiles_check';END IF;END$guard$;
DO $guard$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='automation.lifecycle_results'::regclass AND conname='lifecycle_results_check' AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='f1878e552a0ede5ad781fcc0a291e1ec98f82484b0bac6b92c1e7bfcb4cb4781') THEN RAISE EXCEPTION 'gas_billing_enrollment_upgrade: unexpected lifecycle_results_check';END IF;END$guard$;
DO $guard$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='automation.lifecycle_results'::regclass AND conname='lifecycle_results_mode_check' AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='a54f91715c7077e4cd8544987cabf516ddd1d3cc9f9c44dc2776f95aac82f1e3') THEN RAISE EXCEPTION 'gas_billing_enrollment_upgrade: unexpected lifecycle_results_mode_check';END IF;END$guard$;


CREATE TABLE automation.gas_billing_enrollments(
 id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),profile_id uuid NOT NULL UNIQUE,
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,mode text NOT NULL CHECK(mode IN('review','automatic')),
 from_day date NOT NULL,to_day date NOT NULL,CHECK(isfinite(from_day) AND isfinite(to_day) AND from_day<to_day AND to_day-from_day<=366),
 discovery_policy text NOT NULL CHECK(discovery_policy='gas_billing_scope_reassessment_v1'),
 selection_policy text NOT NULL CHECK(selection_policy IN('final_allocated_billing_v1','final_facility_allocated_billing_v1')),
 billing_profile text NOT NULL CHECK(billing_profile IN('gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1')),
 rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),
 scheduled_policy text NOT NULL CHECK(scheduled_policy='manual_scheduled_quantity_v1'),
 processing_convention text NOT NULL CHECK(processing_convention='gas_processing_instant_utc_v1'),
 readiness_delay_seconds integer NOT NULL CHECK(readiness_delay_seconds BETWEEN 0 AND 86400),
 page_limit integer NOT NULL CHECK(page_limit BETWEEN 1 AND 8),dispatch_limit integer NOT NULL CHECK(dispatch_limit BETWEEN 1 AND page_limit),
 child_timeout_seconds integer NOT NULL CHECK(child_timeout_seconds BETWEEN 1 AND 30),
 child_maximum_attempts integer NOT NULL CHECK(child_maximum_attempts BETWEEN 1 AND 3),
 PRIMARY KEY(id,revision),UNIQUE(profile_id,catalog_id),
 FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.lifecycle_profiles(id,catalog_id),
 FOREIGN KEY(job_id) REFERENCES automation.jobs(id) DEFERRABLE INITIALLY DEFERRED);
CREATE TABLE automation.gas_billing_enrollment_books(
 profile_id uuid NOT NULL,catalog_id uuid NOT NULL,book_id uuid NOT NULL,
 PRIMARY KEY(profile_id,book_id),FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.gas_billing_enrollments(profile_id,catalog_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id));

-- The root is an existing occurrence with D435 input coordinates. No runtime
-- context or session token is retained to resume a scan or dispatch a child.
CREATE TABLE automation.gas_billing_scan_progress(
 profile_id uuid PRIMARY KEY REFERENCES automation.gas_billing_enrollments(profile_id),
 source_run_id uuid REFERENCES automation.runs(id),completed_source_run_id uuid REFERENCES automation.runs(id),
 after_trade_id uuid,after_cash_slot integer,after_day date,
 CHECK((after_trade_id IS NULL AND after_cash_slot IS NULL AND after_day IS NULL) OR
  (after_trade_id IS NOT NULL AND after_cash_slot>=0 AND after_day IS NOT NULL AND source_run_id IS NOT NULL)));
CREATE TABLE automation.gas_billing_assessments(
 run_id uuid NOT NULL REFERENCES automation.runs(id),profile_id uuid NOT NULL,catalog_id uuid NOT NULL,
 trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,cash_slot integer NOT NULL,day date NOT NULL,
 state text NOT NULL CHECK(state IN('not_due','waiting_for_final','unsupported_terms','input_refused','review_required','ready','already_determined','needs_correction')),
 manual_revision_id uuid,facility_target_id uuid,scheduled_revision_id uuid,previous_id uuid,
 detail jsonb NOT NULL CHECK(jsonb_typeof(detail)='object' AND pg_column_size(detail)<=16384),
 PRIMARY KEY(run_id,trade_id,cash_slot,day),FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.gas_billing_enrollments(profile_id,catalog_id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,cash_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,manual_revision_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,facility_target_id) REFERENCES trading.facility_adopted_targets(catalog_id,id),
 FOREIGN KEY(catalog_id,scheduled_revision_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,trade_id,cash_slot,day,previous_id) REFERENCES trading.gas_cash_determinations(catalog_id,trade_id,cash_slot,day,id),
 CHECK(manual_revision_id IS NULL OR facility_target_id IS NULL));
CREATE TABLE automation.gas_billing_children(
 job_id uuid PRIMARY KEY REFERENCES automation.jobs(id),profile_id uuid NOT NULL,catalog_id uuid NOT NULL,
 assessment_run_id uuid NOT NULL,source_run_id uuid NOT NULL REFERENCES automation.runs(id),
 trade_id uuid NOT NULL,cash_slot integer NOT NULL,day date NOT NULL,
 manual_revision_id uuid,facility_target_id uuid,scheduled_revision_id uuid,
 FOREIGN KEY(assessment_run_id,trade_id,cash_slot,day) REFERENCES automation.gas_billing_assessments(run_id,trade_id,cash_slot,day),
 FOREIGN KEY(profile_id,catalog_id) REFERENCES automation.gas_billing_enrollments(profile_id,catalog_id),
 FOREIGN KEY(catalog_id,manual_revision_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,facility_target_id) REFERENCES trading.facility_adopted_targets(catalog_id,id),
 FOREIGN KEY(catalog_id,scheduled_revision_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 UNIQUE NULLS NOT DISTINCT(profile_id,trade_id,cash_slot,day,manual_revision_id,facility_target_id,scheduled_revision_id));
CREATE TABLE automation.gas_billing_scan_results(
 run_id uuid PRIMARY KEY REFERENCES automation.runs(id),profile_id uuid NOT NULL REFERENCES automation.gas_billing_enrollments(profile_id),
 source_run_id uuid NOT NULL REFERENCES automation.runs(id),
 page jsonb NOT NULL CHECK(jsonb_typeof(page)='object' AND pg_column_size(page)<=262144),
 dispatched integer NOT NULL CHECK(dispatched BETWEEN 0 AND 8),backpressure boolean NOT NULL,
 recorded_at ref_data.instant NOT NULL,UNIQUE(run_id,profile_id));
CREATE TABLE automation.gas_billing_dispatch_results(
 run_id uuid NOT NULL,trade_id uuid NOT NULL,cash_slot integer NOT NULL,day date NOT NULL,
 state text NOT NULL CHECK(state IN('queued','existing_job','deferred','stale_inputs')),reason text,job_id uuid REFERENCES automation.jobs(id),
 PRIMARY KEY(run_id,trade_id,cash_slot,day),
 FOREIGN KEY(run_id,trade_id,cash_slot,day) REFERENCES automation.gas_billing_assessments(run_id,trade_id,cash_slot,day),
 CHECK((state IN('queued','existing_job'))=(job_id IS NOT NULL)),CHECK((state IN('deferred','stale_inputs'))=(reason IS NOT NULL)));
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_billing_enrollments','gas_billing_enrollment_books','gas_billing_scan_progress',
  'gas_billing_assessments','gas_billing_children','gas_billing_scan_results','gas_billing_dispatch_results'] LOOP
  EXECUTE format('ALTER TABLE automation.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON automation.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  IF t<>'gas_billing_scan_progress' THEN
   EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  END IF;
 END LOOP;
END$$;

CREATE FUNCTION automation.gas_billing_discovery_context() RETURNS boolean
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  JOIN automation.runs r ON r.id=b.run_id JOIN automation.jobs j ON j.id=r.job_id
  JOIN automation.lifecycle_profiles p ON p.id=j.lifecycle_profile_id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
   AND p.handler='gas_billing_scope')
$$;
CREATE FUNCTION automation.require_interactive_job_context() RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned())
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION automation.gas_billing_enrollment_source(profile uuid) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('bookIds',(SELECT jsonb_agg(b.book_id ORDER BY b.book_id) FROM automation.gas_billing_enrollment_books b WHERE b.profile_id=e.profile_id),
  'from',e.from_day,'to',e.to_day,'discoveryPolicy',e.discovery_policy,'selectionPolicy',e.selection_policy,
  'billingProfile',e.billing_profile,'roundingPolicy',e.rounding_policy,'scheduledPolicy',e.scheduled_policy,
  'processingConvention',e.processing_convention,'knowledgePolicy',p.knowledge_policy,
  'policyId',p.policy_id,'policyRevision',p.policy_revision,'mode',e.mode,'readinessDelaySeconds',e.readiness_delay_seconds,
  'pageLimit',e.page_limit,'dispatchLimit',e.dispatch_limit,'childTimeoutSeconds',e.child_timeout_seconds,'childMaximumAttempts',e.child_maximum_attempts)
 FROM automation.gas_billing_enrollments e JOIN automation.lifecycle_profiles p ON p.id=e.profile_id WHERE e.profile_id=profile
$$;
CREATE FUNCTION automation.require_gas_billing_enrollment(p automation.lifecycle_profiles) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE e automation.gas_billing_enrollments;BEGIN
 SELECT * INTO e FROM automation.gas_billing_enrollments WHERE profile_id=p.id;
 IF e.profile_id IS NULL OR e.revision IS DISTINCT FROM(SELECT max(revision) FROM automation.gas_billing_enrollments old WHERE old.id=e.id)
  OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action,a.mode)=(p.catalog_id,p.policy_id,p.policy_revision,'gas.bill_daily',e.mode))
 THEN RAISE EXCEPTION 'gas_billing_enrollment_changed';END IF;
END$$;

CREATE FUNCTION automation.save_gas_billing_enrollment(catalog uuid,request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;p automation.lifecycle_profiles;e automation.gas_billing_enrollments;
 previous automation.commands;old automation.gas_billing_enrollments;source jsonb=request->'source';schedule jsonb=request->'schedule';
 operation uuid;books uuid[];book uuid;permission text;until_at timestamptz;generation bigint;response jsonb;
 bounds jsonb='{}';at timestamptz;context uuid;
BEGIN
 SELECT x.generation INTO generation FROM security.epoch x;
 FOREACH permission IN ARRAY ARRAY['RunJobs','ReadMarketData','ReadTrades','AmendTrade'] LOOP
  bounds=bounds||jsonb_build_object(permission,CASE WHEN security.has_permission(permission) THEN security.authority_until(permission) ELSE '-infinity'::timestamptz END);
 END LOOP;
 IF jsonb_typeof(request) IS DISTINCT FROM 'object' OR request-ARRAY['schedule','enrollmentId','expectedRevision','jobId','expectedJobVersion','source']<>'{}'::jsonb
  OR NOT(request?&ARRAY['schedule','enrollmentId','expectedRevision','jobId','expectedJobVersion','source'])
  OR jsonb_typeof(schedule) IS DISTINCT FROM 'object'
  OR schedule-ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']<>'{}'::jsonb
  OR NOT(schedule?&ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion'])
  OR schedule->>'operation' IS DISTINCT FROM 'lifecycle_action_v1' OR schedule->'profileId' IS DISTINCT FROM 'null'::jsonb OR schedule->'profileVersion' IS DISTINCT FROM 'null'::jsonb
 THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 operation=(schedule->>'requestId')::uuid;c=automation.actor();
 IF EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned())
 THEN RAISE EXCEPTION 'gas_billing_enrollment_scope' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR NOT security.current_write_context()
  OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.trading_access(catalog,'ReadTrades')
  OR NOT security.has_permission('RunJobs') OR NOT security.has_permission('AmendTrade')
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;
 IF generation IS DISTINCT FROM(SELECT x.generation FROM security.epoch x) THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 SELECT * INTO previous FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  IF previous.request IS DISTINCT FROM request THEN RAISE EXCEPTION 'job_conflict';END IF;
 ELSE
  SELECT * INTO previous FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND x.request-'schedule'=save_gas_billing_enrollment.request-'schedule' AND(x.request->'schedule')-'requestId'=schedule-'requestId'
   ORDER BY x.committed_at,x.request_id LIMIT 1;
 END IF;
 IF previous.job_id IS NOT NULL THEN
  SELECT * INTO j FROM automation.jobs WHERE id=previous.job_id;
  until_at=least(c.expires_at,j.expires_at,(bounds->>'RunJobs')::timestamptz,(bounds->>'ReadMarketData')::timestamptz,(bounds->>'ReadTrades')::timestamptz,(bounds->>'AmendTrade')::timestamptz);
  IF previous.request_id<>operation THEN
   INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,request,previous.response,clock_timestamp());
   INSERT INTO automation.lifecycle_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,until_at);
  END IF;
  PERFORM automation.require_lifecycle_job_command(catalog,request,until_at);
  RETURN jsonb_build_object('outcome',previous.response,'authorityUntil',until_at);
 END IF;
 IF jsonb_typeof(source) IS DISTINCT FROM 'object' OR source-ARRAY['bookIds','from','to','discoveryPolicy','selectionPolicy','billingProfile','roundingPolicy','scheduledPolicy','processingConvention','knowledgePolicy','policyId','policyRevision','mode','readinessDelaySeconds','pageLimit','dispatchLimit','childTimeoutSeconds','childMaximumAttempts']<>'{}'::jsonb
  OR NOT(source?&ARRAY['bookIds','from','to','discoveryPolicy','selectionPolicy','billingProfile','roundingPolicy','scheduledPolicy','processingConvention','knowledgePolicy','policyId','policyRevision','mode','readinessDelaySeconds','pageLimit','dispatchLimit','childTimeoutSeconds','childMaximumAttempts'])
  OR jsonb_typeof(source->'bookIds') IS DISTINCT FROM 'array' OR jsonb_array_length(source->'bookIds') NOT BETWEEN 1 AND 64
 THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 FOREACH permission IN ARRAY ARRAY['policyRevision','readinessDelaySeconds','pageLimit','dispatchLimit','childTimeoutSeconds','childMaximumAttempts'] LOOP
  IF jsonb_typeof(source->permission) IS DISTINCT FROM 'number' OR(source->>permission)!~'^[0-9]+$' THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 END LOOP;
 IF source->>'from'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR source->>'to'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 SELECT array_agg(value::uuid ORDER BY value::uuid) INTO books FROM jsonb_array_elements_text(source->'bookIds');
 IF array_position(books,NULL) IS NOT NULL OR cardinality(books)<>(SELECT count(DISTINCT b) FROM unnest(books)b) THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 e.id=(request->>'enrollmentId')::uuid;e.revision=(request->>'expectedRevision')::integer+1;e.catalog_id=catalog;
 IF e.id IS NULL OR e.id='00000000-0000-0000-0000-000000000000'::uuid OR e.revision IS NULL OR e.revision<1 THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 SELECT * INTO old FROM automation.gas_billing_enrollments prior WHERE prior.id=e.id ORDER BY revision DESC LIMIT 1;
 IF (old.id IS NULL)<>(e.revision=1) OR old.id IS NOT NULL AND(old.catalog_id<>catalog OR old.revision<>e.revision-1)
 THEN RAISE EXCEPTION 'gas_billing_enrollment_changed';END IF;
 IF old.id IS NOT NULL THEN
  SELECT * INTO j FROM automation.jobs WHERE id=old.job_id FOR UPDATE;
  IF j.id IS DISTINCT FROM(request->>'jobId')::uuid OR j.version IS DISTINCT FROM(request->>'expectedJobVersion')::integer
   OR j.owner_id<>c.actor_id OR j.state NOT IN('active','paused','blocked','completed') OR NOT automation.authorized(j)
  THEN RAISE EXCEPTION 'job_conflict';END IF;
  PERFORM 1 FROM automation.jobs child JOIN automation.gas_billing_children link ON link.job_id=child.id
   WHERE link.profile_id=j.lifecycle_profile_id ORDER BY child.id FOR UPDATE OF child;
  IF EXISTS(SELECT 1 FROM automation.runs r WHERE(r.job_id=j.id OR r.job_id IN(SELECT job_id FROM automation.gas_billing_children WHERE profile_id=j.lifecycle_profile_id))
   AND r.state IN('running','cancellation_requested')) THEN RAISE EXCEPTION 'gas_billing_enrollment_running';END IF;
  UPDATE automation.jobs child SET state='cancelled',version=version+1 WHERE child.id IN(SELECT job_id FROM automation.gas_billing_children WHERE profile_id=j.lifecycle_profile_id)
   AND child.state IN('active','paused','blocked');
 ELSE
  IF request->'jobId' IS DISTINCT FROM 'null'::jsonb OR request->'expectedJobVersion' IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;

  j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;j.version=0;j.created_at=clock_timestamp();
 END IF;
 IF(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND id<>j.id AND(state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=jobs.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
 j.name=schedule->>'name';j.operation='lifecycle_action_v1';j.state='active';j.version=j.version+1;
 j.next_due_at=settlement_data.futures_margin_utc_instant(schedule->>'dueAt');j.expires_at=settlement_data.futures_margin_utc_instant(schedule->>'expiresAt');
 j.interval_seconds=(schedule->>'intervalSeconds')::integer;j.time_zone=schedule->>'timeZone';j.misfire=schedule->>'misfire';j.overlap_policy=schedule->>'overlap';
 j.timeout_seconds=(schedule->>'timeoutSeconds')::integer;j.maximum_attempts=(schedule->>'maximumAttempts')::integer;j.last_reconciled_at='-infinity';
 IF j.expires_at<=clock_timestamp() OR j.expires_at>clock_timestamp()+interval '366 days' OR j.next_due_at<clock_timestamp()-interval '1 day' THEN RAISE EXCEPTION 'job_input';END IF;
 p.id=gen_random_uuid();p.tenant_id=j.tenant_id;p.workspace_id=j.workspace_id;p.catalog_id=catalog;p.handler='gas_billing_scope';p.action='gas.bill_daily';
 p.policy_id=(source->>'policyId')::uuid;p.policy_revision=(source->>'policyRevision')::integer;p.knowledge_policy=source->>'knowledgePolicy';
 p.settings_policy='source_known_exact_action_settings_v1';p.input_policy='source_derived_only_no_cash_corrections_v1';
 IF p.policy_revision IS DISTINCT FROM(SELECT max(revision) FROM lifecycle.action_policies WHERE(catalog_id,id)=(catalog,p.policy_id))
  OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action,a.mode)=(catalog,p.policy_id,p.policy_revision,'gas.bill_daily',source->>'mode')
   AND a.from_at<=j.next_due_at AND j.next_due_at<a.until_at) THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 at=j.next_due_at;context=security.resolve_operation_context(catalog,(at AT TIME ZONE 'UTC')::date,NULL);
 FOREACH book IN ARRAY books LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.books b WHERE(b.catalog_id,b.id)=(catalog,book) AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,at))
  THEN RAISE EXCEPTION 'gas_billing_scope_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 e.profile_id=p.id;e.job_id=j.id;e.mode=source->>'mode';e.from_day=(source->>'from')::date;e.to_day=(source->>'to')::date;
 e.discovery_policy=source->>'discoveryPolicy';e.selection_policy=source->>'selectionPolicy';e.billing_profile=source->>'billingProfile';
 e.rounding_policy=source->>'roundingPolicy';e.scheduled_policy=source->>'scheduledPolicy';e.processing_convention=source->>'processingConvention';
 e.readiness_delay_seconds=(source->>'readinessDelaySeconds')::integer;e.page_limit=(source->>'pageLimit')::integer;e.dispatch_limit=(source->>'dispatchLimit')::integer;
 e.child_timeout_seconds=(source->>'childTimeoutSeconds')::integer;e.child_maximum_attempts=(source->>'childMaximumAttempts')::integer;
 INSERT INTO automation.lifecycle_profiles SELECT(p).*;
 INSERT INTO automation.gas_billing_enrollments SELECT(e).*;
 INSERT INTO automation.gas_billing_enrollment_books SELECT p.id,catalog,b FROM unnest(books)b;
 INSERT INTO automation.gas_billing_scan_progress(profile_id) VALUES(p.id);
 j.lifecycle_profile_id=p.id;
 IF NOT automation.authorized(j) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF old.id IS NULL THEN INSERT INTO automation.jobs SELECT(j).*;
 ELSE UPDATE automation.jobs SET name=j.name,state=j.state,version=j.version,next_due_at=j.next_due_at,expires_at=j.expires_at,
  interval_seconds=j.interval_seconds,time_zone=j.time_zone,misfire=j.misfire,overlap_policy=j.overlap_policy,timeout_seconds=j.timeout_seconds,
  maximum_attempts=j.maximum_attempts,lifecycle_profile_id=p.id,last_reconciled_at=j.last_reconciled_at WHERE id=j.id;END IF;
 until_at=least(c.expires_at,j.expires_at,(bounds->>'RunJobs')::timestamptz,(bounds->>'ReadMarketData')::timestamptz,(bounds->>'ReadTrades')::timestamptz,(bounds->>'AmendTrade')::timestamptz);
 response=automation.description(j)||jsonb_build_object('enrollmentId',e.id,'enrollmentRevision',e.revision,'source',automation.gas_billing_enrollment_source(p.id));
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,request,response,clock_timestamp());
 INSERT INTO automation.lifecycle_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,until_at);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,CASE WHEN old.id IS NULL THEN 'created' ELSE 'updated' END,clock_timestamp());
 PERFORM automation.require_lifecycle_job_command(catalog,request,until_at);
 RETURN jsonb_build_object('outcome',response,'authorityUntil',until_at);
END$$;

CREATE FUNCTION automation.gas_billing_scan_source(run uuid) RETURNS uuid
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT CASE WHEN p.handler='gas_billing_scope' THEN coalesce(progress.source_run_id,r.id) ELSE child.source_run_id END
 FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id JOIN automation.lifecycle_profiles p ON p.id=j.lifecycle_profile_id
 LEFT JOIN automation.gas_billing_scan_progress progress ON progress.profile_id=p.id
 LEFT JOIN automation.gas_billing_children child ON child.job_id=j.id WHERE r.id=run
$$;
CREATE FUNCTION automation.lock_gas_billing_owner() RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE token uuid;owner uuid;job uuid;until_at timestamptz;BEGIN
 SELECT q.id,j.owner_id,j.id INTO token,owner,job FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  JOIN automation.runs r ON r.id=b.run_id JOIN automation.jobs j ON j.id=r.job_id JOIN automation.lifecycle_profiles p ON p.id=j.lifecycle_profile_id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND p.handler IN('gas_billing_scope','gas_billing');
 IF token IS NULL THEN RETURN;END IF;
 until_at=automation.lifecycle_context_until(token,'RunJobs');
 IF until_at<=clock_timestamp() THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 -- Match authority changes and interactive controls: epoch, owner, job, run.
 -- The original issuer's deadline remains fixed throughout every wait.
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 PERFORM 1 FROM security.principals WHERE id=owner FOR UPDATE;
 PERFORM 1 FROM automation.jobs WHERE id=job FOR UPDATE;
 IF until_at<=clock_timestamp() OR automation.lifecycle_context_until(token,'RunJobs')<=clock_timestamp()
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION automation.gas_billing_dispatch_parent(catalog uuid,definition jsonb) RETURNS automation.jobs
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE parent automation.jobs;r automation.runs;p automation.lifecycle_profiles;e automation.gas_billing_enrollments;
 a automation.gas_billing_assessments;root automation.runs;g jsonb=definition#>'{settings,gasSource}';
BEGIN
 SELECT job.* INTO parent FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  JOIN automation.runs run ON run.id=b.run_id JOIN automation.jobs job ON job.id=run.job_id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
   AND job.operation='lifecycle_action_v1';
 IF parent.id IS NULL THEN RETURN parent;END IF;
 r=automation.lock_lifecycle_run();SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=parent.lifecycle_profile_id;
 PERFORM automation.require_lifecycle_settings(p);
 IF p.handler<>'gas_billing_scope' OR parent.state<>'active' THEN RAISE EXCEPTION 'gas_billing_dispatch_scope' USING ERRCODE='42501';END IF;
 SELECT * INTO e FROM automation.gas_billing_enrollments WHERE profile_id=p.id;
 SELECT * INTO root FROM automation.runs WHERE id=automation.gas_billing_scan_source(r.id);
 SELECT * INTO a FROM automation.gas_billing_assessments WHERE run_id=r.id
  AND(trade_id,cash_slot,day)=((definition#>>'{target,scopeId}')::uuid,(g->>'cashSlot')::integer,(g->>'day')::date);
 IF catalog IS DISTINCT FROM p.catalog_id OR a.run_id IS NULL OR a.previous_id IS NOT NULL OR coalesce(a.manual_revision_id,a.facility_target_id) IS NULL
  OR a.state NOT IN('ready','review_required') OR (e.mode='automatic' AND a.state<>'ready')
  OR definition#>>'{target,handler}' IS DISTINCT FROM 'gas_billing'
  OR(definition#>>'{target,bindingId}')::uuid IS DISTINCT FROM a.instrument_id
  OR(definition#>>'{settings,tradeRevision}')::integer IS DISTINCT FROM a.trade_revision
  OR(definition->>'policyId')::uuid IS DISTINCT FROM p.policy_id OR(definition->>'policyRevision')::integer IS DISTINCT FROM p.policy_revision
  OR g->>'selectionPolicy' IS DISTINCT FROM e.selection_policy OR g->>'billingProfile' IS DISTINCT FROM e.billing_profile
  OR g->>'roundingPolicy' IS DISTINCT FROM e.rounding_policy OR g->>'scheduledPolicy' IS DISTINCT FROM e.scheduled_policy
  OR g->>'processingConvention' IS DISTINCT FROM e.processing_convention
  OR(g->>'evaluationAt')::timestamptz IS DISTINCT FROM root.due_at OR(g->>'businessDate')::date IS DISTINCT FROM root.input_business_on
  OR(a.detail->>'nativeEnd')::timestamptz+make_interval(secs=>e.readiness_delay_seconds)>root.due_at
 THEN RAISE EXCEPTION 'gas_billing_dispatch_scope' USING ERRCODE='42501';END IF;
 PERFORM automation.require_gas_billing_candidate_current(e,a);
 RETURN parent;
END$$;
CREATE FUNCTION automation.gas_billing_scan_outcome(result automation.gas_billing_scan_results) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('sourceRunId',result.source_run_id,'page',result.page,'dispatched',result.dispatched,
  'backpressure',result.backpressure,'state','partial_assessment',
  'enrollment',(SELECT jsonb_build_object('id',e.id,'revision',e.revision,'mode',e.mode) FROM automation.gas_billing_enrollments e WHERE e.profile_id=result.profile_id),
  'dispatchResults',coalesce((SELECT jsonb_agg(to_jsonb(d) ORDER BY trade_id,cash_slot,day) FROM automation.gas_billing_dispatch_results d WHERE d.run_id=result.run_id),'[]'::jsonb))
$$;
CREATE FUNCTION automation.require_gas_billing_scan_result(run uuid,outcome jsonb) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result automation.gas_billing_scan_results;r automation.runs;j automation.jobs;BEGIN
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 SELECT * INTO result FROM automation.gas_billing_scan_results WHERE run_id=run;
 IF r.id IS DISTINCT FROM run OR j.state<>'active' OR result.run_id IS NULL OR result.profile_id<>j.lifecycle_profile_id
  OR automation.gas_billing_scan_outcome(result) IS DISTINCT FROM outcome
  OR EXISTS(SELECT 1 FROM lifecycle.commands c WHERE c.operation_id=run)
 THEN RAISE EXCEPTION 'gas_billing_scan_result';END IF;
END$$;

CREATE FUNCTION automation.require_gas_billing_assessment(e automation.gas_billing_enrollments,a automation.gas_billing_assessments,
 source security.operation_read_coordinates,at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE m trading.gas_component_terms;physical trading.gas_component_terms;h trading.gas_trade_revisions;o uuid;
 scheduled trading.gas_quantity_revisions;allocated trading.gas_quantity_revisions;prior trading.gas_cash_determinations;
 target trading.facility_adopted_targets;quote ref_data.quote_specifications;prefix integer;qty numeric;scheduled_id uuid;final_id uuid;
 state text;contract_n numeric;contract_d numeric;num numeric;den numeric;cap_n numeric;cap_d numeric;scaled numeric;whole numeric;rem numeric;amount numeric;
 heads integer;any_final boolean;places integer;
BEGIN
 SELECT known_through INTO STRICT prefix FROM security.operation_trading WHERE(catalog_id,context_id)=(a.catalog_id,source.id);
 SELECT * INTO STRICT h FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id,revision,instrument_id)=(a.catalog_id,a.trade_id,a.trade_revision,a.instrument_id);
 SELECT * INTO STRICT m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(a.catalog_id,a.instrument_id,a.cash_slot);
 SELECT * INTO STRICT physical FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,m.source_slot);
 SELECT id INTO STRICT o FROM trading.gas_delivery_obligations WHERE(catalog_id,trade_id,instrument_id,slot)=(m.catalog_id,a.trade_id,m.instrument_id,m.source_slot);
 SELECT q.* INTO STRICT quote FROM market_data.gas_valuation_assignments assignment
  JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(assignment.catalog_id,assignment.revision_id,assignment.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code);
 SELECT display_minor_units INTO STRICT places FROM ref_data.currencies WHERE(catalog_id,revision_id,code)=(quote.catalog_id,quote.revision_id,quote.currency_code);
 IF(a.detail#>>'{selection,decimalPlaces}')::integer IS DISTINCT FROM places THEN RAISE EXCEPTION 'gas_billing_scan_assessment';END IF;
 IF(a.detail->>'nativeEnd')::timestamptz+make_interval(secs=>e.readiness_delay_seconds)>at THEN state='not_due';
 ELSIF m.role NOT IN('consideration','fee') OR m.stage<>'delivery'
  OR EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries r WHERE(r.catalog_id,r.instrument_id)=(m.catalog_id,m.instrument_id) AND r.slot IN(m.slot,m.source_slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_members r WHERE(r.catalog_id,r.instrument_id,r.slot)=(m.catalog_id,m.instrument_id,m.slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_rights r WHERE(r.catalog_id,r.instrument_id,r.physical_slot)=(m.catalog_id,m.instrument_id,m.source_slot))
  OR(e.billing_profile='gas_final_daily_fixed_cash_v1' AND(m.share_n<>m.share_d OR m.part_kind IS NOT NULL OR(m.delivery_from,m.delivery_to) IS DISTINCT FROM(physical.delivery_from,physical.delivery_to)))
 THEN state='unsupported_terms';
 ELSE
  SELECT r.* INTO scheduled FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
   WHERE(s.catalog_id,s.obligation_id,s.kind,s.source,s.stage,r.day)=(a.catalog_id,o,'scheduled','manual_daily_v1','delivery',a.day)
    AND r.publication_order<=prefix AND k.known_from<=source.known_at AND r.effective_at<=at ORDER BY r.revision DESC LIMIT 1;
  IF scheduled.status='provisional' THEN scheduled_id=scheduled.id;END IF;
  IF e.selection_policy='final_allocated_billing_v1' THEN
   SELECT r.* INTO allocated FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
    WHERE(s.catalog_id,s.obligation_id,s.kind,s.source,s.stage,r.day)=(a.catalog_id,o,'allocated','manual_daily_v1','delivery',a.day)
     AND r.publication_order<=prefix AND k.known_from<=source.known_at AND r.effective_at<=at ORDER BY r.revision DESC LIMIT 1;
   IF allocated.status='final' THEN final_id=allocated.id;qty=allocated.quantity;END IF;
  ELSE
   SELECT count(*),bool_or(state='final') INTO heads,any_final FROM(
    SELECT DISTINCT ON(t.ledger_id) t.state FROM trading.facility_adopted_targets t
    JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(t.catalog_id,t.ledger_id)
    JOIN trading.facility_quantity_adoptions adoption ON(adoption.catalog_id,adoption.id)=(t.catalog_id,t.adoption_id)
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(adoption.catalog_id,adoption.publication_order)
    WHERE(t.catalog_id,t.obligation_id,g.day)=(a.catalog_id,o,a.day)
     AND adoption.publication_order<=prefix AND k.known_from<=source.known_at AND adoption.effective_at<=at
    ORDER BY t.ledger_id,t.revision DESC) visible;
   IF heads<=1 THEN
   SELECT t.* INTO target FROM trading.facility_adopted_targets t
    JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(t.catalog_id,t.ledger_id)
    JOIN trading.facility_quantity_adoptions adoption ON(adoption.catalog_id,adoption.id)=(t.catalog_id,t.adoption_id)
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(adoption.catalog_id,adoption.publication_order)
    WHERE(t.catalog_id,t.obligation_id,g.day)=(a.catalog_id,o,a.day)
     AND adoption.publication_order<=prefix AND k.known_from<=source.known_at AND adoption.effective_at<=at ORDER BY t.revision DESC LIMIT 1;
   IF target.state='final' THEN
    final_id=target.id;
    SELECT quantity INTO STRICT qty FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(a.catalog_id,target.plan_id,o);
   END IF;
   END IF;
  END IF;
  IF heads>1 THEN state=CASE WHEN any_final THEN 'input_refused' ELSE 'waiting_for_final' END;scheduled_id=NULL;
  ELSIF final_id IS NULL THEN state='waiting_for_final';scheduled_id=NULL;
  ELSE
   -- Exact owned schedule, held basis and physical membership. This read does
   -- not select a newer trade revision when a later revision exists.
   SELECT(q.parameters->>'numerator')::numeric*h.quantity*physical.ratio_n,(q.parameters->>'denominator')::numeric*physical.ratio_d
    INTO STRICT contract_n,contract_d FROM trading.instrument_side_schedules s
    JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.measure)=(s.catalog_id,s.schedule_id,'schedule_quantity')
    JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
    JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(q.catalog_id,q.schedule_id,q.grid_ordinal)
    WHERE(s.catalog_id,s.instrument_id,s.slot)=(a.catalog_id,a.instrument_id,m.source_slot) AND q.operation='quantity_literal'
     AND g.operating_days=ARRAY[a.day-date '0001-01-01'] AND cardinality(g.selection)=1 AND g.lengths=ARRAY[1];
   SELECT d.* INTO prior FROM trading.gas_cash_determinations d JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
    WHERE(d.catalog_id,d.trade_id,d.cash_slot,d.day)=(a.catalog_id,a.trade_id,a.cash_slot,a.day)
     AND d.publication_order<=prefix AND k.known_from<=source.known_at AND d.effective_at<=at ORDER BY d.ordinal DESC LIMIT 1;
   state=CASE WHEN coalesce(prior.manual_revision_id,prior.facility_target_id)=final_id THEN 'already_determined'
    WHEN prior.id IS NOT NULL THEN 'needs_correction'
    WHEN scheduled_id IS NOT NULL AND scheduled.quantity*contract_d=contract_n AND qty=scheduled.quantity THEN 'ready' ELSE 'review_required' END;
   SELECT q.* INTO STRICT quote FROM market_data.gas_valuation_assignments assignment
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(assignment.catalog_id,assignment.revision_id,assignment.curve_code)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
    WHERE(assignment.catalog_id,assignment.revision_id,assignment.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code);
   SELECT display_minor_units INTO STRICT places FROM ref_data.currencies WHERE(catalog_id,revision_id,code)=(quote.catalog_id,quote.revision_id,quote.currency_code);
   num=qty;den=1;
   IF e.billing_profile='gas_final_daily_entry_cash_v1' THEN
    IF m.part_kind IS NOT NULL THEN
     num=greatest(0,CASE m.part_kind WHEN 'band' THEN qty*m.part_lower_d-m.part_lower_n*h.quantity WHEN 'shortfall' THEN m.part_lower_n*h.quantity-qty*m.part_lower_d END);den=m.part_lower_d;
     IF m.part_upper_n IS NOT NULL THEN
      cap_n=(m.part_upper_n*m.part_lower_d-m.part_lower_n*m.part_upper_d)*h.quantity;cap_d=m.part_upper_d*m.part_lower_d;
      IF num*cap_d>cap_n*den THEN num=cap_n;den=cap_d;END IF;
     END IF;
    END IF;
    num=num*m.share_n;den=den*m.share_d;
   END IF;
   num=num*m.price*quote.multiplier*m.direction;scaled=abs(num)*power(10::numeric,places);whole=div(scaled,den);rem=mod(scaled,den);
   amount=sign(num)*(whole+CASE WHEN rem*2>den OR(rem*2=den AND mod(whole,2)=1) THEN 1 ELSE 0 END)/power(10::numeric,places);
   IF(a.detail#>>'{selection,decimalPlaces}')::integer IS DISTINCT FROM places OR a.detail->>'currency' IS DISTINCT FROM quote.currency_code::text
   THEN RAISE EXCEPTION 'gas_billing_scan_assessment';END IF;
  END IF;
 END IF;
 IF a.state IS DISTINCT FROM state OR coalesce(a.manual_revision_id,a.facility_target_id) IS DISTINCT FROM final_id
  OR a.scheduled_revision_id IS DISTINCT FROM scheduled_id OR a.previous_id IS DISTINCT FROM prior.id
  OR(a.detail->>'amount')::numeric IS DISTINCT FROM amount
  OR a.detail->>'currency' IS DISTINCT FROM (CASE WHEN amount IS NOT NULL THEN quote.currency_code::text END)
 THEN RAISE EXCEPTION 'gas_billing_scan_assessment';END IF;
END$$;

CREATE FUNCTION automation.require_gas_billing_candidate_current(e automation.gas_billing_enrollments,a automation.gas_billing_assessments) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE current_head integer;obligation uuid;final_id uuid;scheduled_id uuid;BEGIN
 SELECT max(revision) INTO current_head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(a.catalog_id,a.trade_id);
 IF current_head IS DISTINCT FROM a.trade_revision THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 SELECT o.id INTO STRICT obligation FROM trading.gas_component_terms m JOIN trading.gas_delivery_obligations o
  ON(o.catalog_id,o.trade_id,o.instrument_id,o.slot)=(m.catalog_id,a.trade_id,m.instrument_id,m.source_slot)
  WHERE(m.catalog_id,m.instrument_id,m.slot)=(a.catalog_id,a.instrument_id,a.cash_slot);
 SELECT CASE WHEN r.status='provisional' THEN r.id END INTO scheduled_id FROM trading.gas_quantity_revisions r JOIN trading.gas_quantity_series s ON(s.catalog_id,s.id)=(r.catalog_id,r.series_id)
  WHERE(s.catalog_id,s.obligation_id,s.kind,s.stage,s.source,r.day)=(a.catalog_id,obligation,'scheduled','delivery','manual_daily_v1',a.day)
  ORDER BY r.revision DESC LIMIT 1;
 IF e.selection_policy='final_allocated_billing_v1' THEN
  SELECT r.id INTO final_id FROM trading.gas_quantity_revisions r JOIN trading.gas_quantity_series s ON(s.catalog_id,s.id)=(r.catalog_id,r.series_id)
   WHERE(s.catalog_id,s.obligation_id,s.kind,s.stage,s.source,r.day)=(a.catalog_id,obligation,'allocated','delivery','manual_daily_v1',a.day)
   ORDER BY r.revision DESC LIMIT 1;
 ELSE
  IF(SELECT count(DISTINCT t.ledger_id) FROM trading.facility_adopted_targets t JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(t.catalog_id,t.ledger_id)
   WHERE(t.catalog_id,t.obligation_id,g.day)=(a.catalog_id,obligation,a.day))>1 THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  SELECT t.id INTO final_id FROM trading.facility_adopted_targets t JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(t.catalog_id,t.ledger_id)
   WHERE(t.catalog_id,t.obligation_id,g.day)=(a.catalog_id,obligation,a.day) ORDER BY t.revision DESC LIMIT 1;
 END IF;
 IF(final_id,scheduled_id) IS DISTINCT FROM(coalesce(a.manual_revision_id,a.facility_target_id),a.scheduled_revision_id)
 THEN RAISE EXCEPTION 'gas_billing_source';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_cash_determinations WHERE(catalog_id,trade_id,cash_slot,day)=(a.catalog_id,a.trade_id,a.cash_slot,a.day))
 THEN RAISE EXCEPTION 'gas_billing_already_determined';END IF;
END$$;

CREATE FUNCTION automation.publish_gas_billing_page(page jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;e automation.gas_billing_enrollments;root automation.runs;
 progress automation.gas_billing_scan_progress;source security.operation_read_coordinates;books uuid[];cursor jsonb;expected jsonb;
 item jsonb;candidate jsonb;selection jsonb;definition jsonb;request jsonb;admitted jsonb;child automation.gas_billing_children;
 a automation.gas_billing_assessments;result automation.gas_billing_scan_results;available integer;dispatched integer=0;blocked boolean=false;index integer=0;
BEGIN
 -- Retain only the native-checked projection. Review explanations and full
 -- reconciliation are recomputed by the managed business operation.
 page=jsonb_build_object('context',jsonb_build_object('contextId',page#>'{context,contextId}'),
  'evaluationAt',page->'evaluationAt','hasMore',page->'hasMore','next',page->'next',
  'items',(SELECT coalesce(jsonb_agg(jsonb_build_object(
   'selection',jsonb_build_object('tradeId',x#>'{selection,tradeId}','expectedRevision',x#>'{selection,expectedRevision}',
    'cashSlot',x#>'{selection,cashSlot}','day',x#>'{selection,day}','selectionPolicy',x#>'{selection,selectionPolicy}',
    'profile',x#>'{selection,profile}','decimalPlaces',x#>'{selection,decimalPlaces}','roundingPolicy',x#>'{selection,roundingPolicy}'),
   'bindingId',x->'bindingId','bookId',x->'bookId','occurrenceId',x->'occurrenceId','state',x->'state',
   'amount',x->'amount','currency',x->'currency','sourceId',x->'sourceId','previousId',x->'previousId',
   'nativeEnd',x->'nativeEnd','scheduledSourceId',x->'scheduledSourceId') ORDER BY ordinal),'[]'::jsonb)
    FROM jsonb_array_elements(page->'items') WITH ORDINALITY AS item(x,ordinal)));
 PERFORM automation.lock_gas_billing_owner();
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id FOR UPDATE;
 SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 IF p.handler IS DISTINCT FROM 'gas_billing_scope' OR j.state<>'active' THEN RAISE EXCEPTION 'gas_billing_dispatch_scope' USING ERRCODE='42501';END IF;
 PERFORM automation.require_lifecycle_settings(p);
 SELECT * INTO e FROM automation.gas_billing_enrollments WHERE profile_id=p.id;
 SELECT * INTO progress FROM automation.gas_billing_scan_progress WHERE profile_id=p.id FOR UPDATE;
 SELECT * INTO root FROM automation.runs WHERE id=automation.gas_billing_scan_source(r.id);
 source=automation.occurrence_read_inputs();
 IF(root.input_catalog_id,root.input_business_on,root.input_known_at,root.input_system_id,root.input_timeline)
   IS DISTINCT FROM(r.input_catalog_id,r.input_business_on,r.input_known_at,r.input_system_id,r.input_timeline)
 THEN RAISE EXCEPTION 'occurrence_inputs_conflict';END IF;
 SELECT * INTO result FROM automation.gas_billing_scan_results WHERE run_id=r.id;
 IF FOUND THEN
  IF result.page IS DISTINCT FROM page THEN RAISE EXCEPTION 'job_conflict';END IF;
  RETURN automation.gas_billing_scan_outcome(result);
 END IF;
 SELECT array_agg(book_id ORDER BY book_id) INTO books FROM automation.gas_billing_enrollment_books WHERE profile_id=p.id;
 cursor=CASE WHEN progress.after_trade_id IS NOT NULL THEN jsonb_build_object('tradeId',progress.after_trade_id,'cashSlot',progress.after_cash_slot,'day',progress.after_day) END;
 expected=security.read_gas_billing_page(p.catalog_id,books,source.id,root.due_at,e.from_day,e.to_day,cursor,e.page_limit);
 IF jsonb_typeof(page) IS DISTINCT FROM 'object' OR jsonb_typeof(page->'items') IS DISTINCT FROM 'array'
  OR jsonb_array_length(page->'items')<>jsonb_array_length(expected->'rows') OR page->'hasMore' IS DISTINCT FROM expected->'hasMore'
  OR page->'next' IS DISTINCT FROM expected->'next' OR(page->>'evaluationAt')::timestamptz IS DISTINCT FROM root.due_at
  OR(page#>>'{context,contextId}')::uuid IS DISTINCT FROM source.id
 THEN RAISE EXCEPTION 'gas_billing_scan_page';END IF;
 PERFORM 1 FROM security.principals WHERE id=j.owner_id FOR UPDATE;
 SELECT greatest(0,64-count(*)) INTO available FROM automation.jobs task WHERE task.owner_id=j.owner_id AND(task.state IN('active','paused','blocked')
  OR EXISTS(SELECT 1 FROM automation.runs active WHERE active.job_id=task.id AND active.state IN('running','cancellation_requested')));
 FOR item IN SELECT value FROM jsonb_array_elements(page->'items') LOOP
  candidate=expected->'rows'->index;index=index+1;selection=item->'selection';
  IF(selection->>'tradeId')::uuid IS DISTINCT FROM(candidate->>'trade_id')::uuid
   OR(selection->>'expectedRevision')::integer IS DISTINCT FROM(candidate->>'revision')::integer
   OR(selection->>'cashSlot')::integer IS DISTINCT FROM(candidate->>'slot')::integer
   OR(selection->>'day')::date IS DISTINCT FROM(candidate->>'native_day')::date
   OR(item->>'bindingId')::uuid IS DISTINCT FROM(candidate->>'instrument_id')::uuid
   OR(item->>'bookId')::uuid IS DISTINCT FROM(candidate->>'book_id')::uuid
   OR(item->>'nativeEnd')::timestamptz IS DISTINCT FROM(candidate->>'finish')::timestamptz
   OR selection->>'selectionPolicy' IS DISTINCT FROM e.selection_policy OR selection->>'profile' IS DISTINCT FROM e.billing_profile
   OR selection->>'roundingPolicy' IS DISTINCT FROM e.rounding_policy
   OR(item->>'occurrenceId')::uuid IS DISTINCT FROM substr(encode(sha256(convert_to('gas_daily_billing_v1/'||p.catalog_id::text||'/'||(candidate->>'trade_id')||'/'||(candidate->>'slot')||'/'||(candidate->>'native_day'),'UTF8')),'hex'),1,32)::uuid
  THEN RAISE EXCEPTION 'gas_billing_scan_page';END IF;
  a.run_id=r.id;a.profile_id=p.id;a.catalog_id=p.catalog_id;a.trade_id=(selection->>'tradeId')::uuid;
  a.trade_revision=(selection->>'expectedRevision')::integer;a.instrument_id=(item->>'bindingId')::uuid;
  a.cash_slot=(selection->>'cashSlot')::integer;a.day=(selection->>'day')::date;a.state=item->>'state';a.detail=item;
  a.manual_revision_id=CASE WHEN e.selection_policy='final_allocated_billing_v1' THEN(item->>'sourceId')::uuid END;
  a.facility_target_id=CASE WHEN e.selection_policy='final_facility_allocated_billing_v1' THEN(item->>'sourceId')::uuid END;
  a.scheduled_revision_id=(item->>'scheduledSourceId')::uuid;a.previous_id=(item->>'previousId')::uuid;
  IF(a.detail->>'nativeEnd')::timestamptz+make_interval(secs=>e.readiness_delay_seconds)>root.due_at AND a.state<>'not_due'
  THEN RAISE EXCEPTION 'gas_billing_scan_page';END IF;
  PERFORM automation.require_gas_billing_assessment(e,a,source,root.due_at);
  INSERT INTO automation.gas_billing_assessments SELECT(a).*;
  IF a.state NOT IN('ready','review_required') OR(e.mode='automatic' AND a.state<>'ready') THEN CONTINUE;END IF;
  SELECT * INTO child FROM automation.gas_billing_children old WHERE(old.profile_id,old.trade_id,old.cash_slot,old.day)=(p.id,a.trade_id,a.cash_slot,a.day)
   AND(old.manual_revision_id,old.facility_target_id,old.scheduled_revision_id) IS NOT DISTINCT FROM(a.manual_revision_id,a.facility_target_id,a.scheduled_revision_id);
  IF child.job_id IS NOT NULL THEN
   INSERT INTO automation.gas_billing_dispatch_results VALUES(r.id,a.trade_id,a.cash_slot,a.day,'existing_job',NULL,child.job_id);CONTINUE;
  END IF;
  BEGIN PERFORM automation.require_gas_billing_candidate_current(e,a);
  EXCEPTION WHEN raise_exception THEN
   IF SQLERRM NOT IN('lifecycle_schedule_settings','gas_billing_source','gas_billing_already_determined') THEN RAISE;END IF;
   INSERT INTO automation.gas_billing_dispatch_results VALUES(r.id,a.trade_id,a.cash_slot,a.day,'stale_inputs',SQLERRM,NULL);CONTINUE;
  END;
  IF dispatched>=e.dispatch_limit OR dispatched>=available THEN
   INSERT INTO automation.gas_billing_dispatch_results VALUES(r.id,a.trade_id,a.cash_slot,a.day,'deferred',
    CASE WHEN dispatched>=available THEN 'owner_quota' ELSE 'dispatch_limit' END,NULL);
   blocked=true;CONTINUE;
  END IF;
  definition=jsonb_build_object('closeId',NULL,'target',jsonb_build_object('handler','gas_billing','scopeId',a.trade_id,'bindingId',a.instrument_id),
   'action','gas.bill_daily','occurrenceId',item->'occurrenceId','policyId',p.policy_id,'policyRevision',p.policy_revision,
   'knowledgePolicy',p.knowledge_policy,'settingsPolicy',p.settings_policy,'inputPolicy',p.input_policy,
   'settings',jsonb_build_object('tradeRevision',a.trade_revision,'optionProfileRevision',NULL,'swapProfileId',NULL,'swapProfileRevision',NULL,'marginPolicyId',NULL,'marginPolicyRevision',NULL,
    'gasSource',jsonb_build_object('cashSlot',a.cash_slot,'day',a.day,'processingConvention',e.processing_convention,'businessDate',root.input_business_on,
     'evaluationAt',to_char(root.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'selectionPolicy',e.selection_policy,'billingProfile',e.billing_profile,
     'decimalPlaces',selection->'decimalPlaces','roundingPolicy',e.rounding_policy,'scheduledPolicy',e.scheduled_policy)));
  request=jsonb_build_object('definition',definition,'schedule',jsonb_build_object('requestId',gen_random_uuid(),'name','Daily gas billing',
   'operation','lifecycle_action_v1','dueAt',to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
   'expiresAt',to_char(j.expires_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'intervalSeconds',NULL,'timeZone','UTC','misfire','run_once','overlap','forbid',
   'timeoutSeconds',e.child_timeout_seconds,'maximumAttempts',e.child_maximum_attempts,'profileId',NULL,'profileVersion',NULL));
  -- A product refusal affects this target only. Permission/fence failures abort
  -- the page, preserving its cursor and previously committed pages.
  BEGIN
   admitted=automation.create_lifecycle_job_scoped(p.catalog_id,request,r.id);
   INSERT INTO automation.gas_billing_dispatch_results VALUES(r.id,a.trade_id,a.cash_slot,a.day,'queued',NULL,(admitted#>>'{outcome,id}')::uuid);
   dispatched=dispatched+1;
  EXCEPTION WHEN raise_exception THEN
   IF SQLERRM NOT IN('lifecycle_schedule_settings','gas_billing_profile','gas_billing_rounding','gas_billing_interval','gas_billing_source','gas_billing_already_determined') THEN RAISE;END IF;
   INSERT INTO automation.gas_billing_dispatch_results VALUES(r.id,a.trade_id,a.cash_slot,a.day,'stale_inputs',SQLERRM,NULL);
  END;
 END LOOP;
 result.run_id=r.id;result.profile_id=p.id;result.source_run_id=root.id;result.page=page;
 result.dispatched=dispatched;result.backpressure=blocked;result.recorded_at=clock_timestamp();
 INSERT INTO automation.gas_billing_scan_results SELECT(result).*;
 IF NOT blocked THEN
  UPDATE automation.gas_billing_scan_progress SET
   source_run_id=CASE WHEN(expected->>'hasMore')::boolean THEN root.id END,
   completed_source_run_id=CASE WHEN(expected->>'hasMore')::boolean THEN completed_source_run_id ELSE root.id END,
   after_trade_id=(expected#>>'{next,tradeId}')::uuid,after_cash_slot=(expected#>>'{next,cashSlot}')::integer,after_day=(expected#>>'{next,day}')::date
   WHERE profile_id=p.id;
 ELSE UPDATE automation.gas_billing_scan_progress SET source_run_id=root.id WHERE profile_id=p.id;END IF;
 PERFORM automation.require_gas_billing_scan_result(r.id,automation.gas_billing_scan_outcome(result));
 RETURN automation.gas_billing_scan_outcome(result);
END$$;

CREATE FUNCTION automation.gas_billing_scope_report(job uuid) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('enrollmentId',e.id,'revision',e.revision,'state','partial_assessment',
  'scanSourceRunId',progress.source_run_id,'completedSourceRunId',progress.completed_source_run_id,
  'counts',coalesce((SELECT jsonb_object_agg(state,total) FROM(
    SELECT state,count(*) total FROM(
     SELECT DISTINCT ON(a.trade_id,a.cash_slot,a.day) a.state FROM automation.gas_billing_assessments a
      JOIN automation.runs r ON r.id=a.run_id WHERE a.profile_id=e.profile_id
      ORDER BY a.trade_id,a.cash_slot,a.day,r.due_at DESC) latest GROUP BY state) counts),'{}'::jsonb),
  'childJobs',(SELECT count(*) FROM automation.gas_billing_children WHERE profile_id=e.profile_id),
  'executed',(SELECT count(DISTINCT bill.id) FROM automation.gas_billing_children child JOIN automation.runs r ON r.job_id=child.job_id
    JOIN automation.lifecycle_results result ON result.run_id=r.id
    JOIN trading.gas_cash_determinations bill ON(bill.catalog_id,bill.id,bill.trade_id,bill.cash_slot,bill.day)=
      (e.catalog_id,(result.outcome->>'determinationId')::uuid,child.trade_id,child.cash_slot,child.day)
    WHERE child.profile_id=e.profile_id AND result.mode='automatic'),
  'proposals',(SELECT count(*) FROM automation.gas_billing_children child JOIN automation.runs r ON r.job_id=child.job_id
    JOIN automation.lifecycle_results result ON result.run_id=r.id WHERE child.profile_id=e.profile_id AND result.mode='review'),
  'recentChildren',coalesce((SELECT jsonb_agg(row_to_json(x)) FROM(SELECT child.job_id,child.trade_id,child.cash_slot,child.day,task.state
    FROM automation.gas_billing_children child JOIN automation.jobs task ON task.id=child.job_id
    WHERE child.profile_id=e.profile_id ORDER BY task.created_at DESC,child.job_id LIMIT 100)x),'[]'::jsonb))
 FROM automation.jobs j JOIN automation.gas_billing_enrollments e ON e.profile_id=j.lifecycle_profile_id
 JOIN automation.gas_billing_scan_progress progress ON progress.profile_id=e.profile_id WHERE j.id=job
$$;
CREATE FUNCTION automation.seal_gas_billing_scan() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM automation.require_gas_billing_scan_result(NEW.run_id,automation.gas_billing_scan_outcome(NEW));
 IF NOT EXISTS(SELECT 1 FROM automation.lifecycle_results result WHERE result.run_id=NEW.run_id AND result.mode='discovery'
  AND result.outcome=automation.gas_billing_scan_outcome(NEW)) THEN RAISE EXCEPTION 'lifecycle_schedule_result_required';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER gas_billing_scan_complete AFTER INSERT ON automation.gas_billing_scan_results
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.seal_gas_billing_scan();
CREATE FUNCTION automation.seal_gas_billing_child() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;BEGIN
 r=automation.lock_lifecycle_run();
 IF r.id IS DISTINCT FROM NEW.assessment_run_id OR NOT EXISTS(SELECT 1 FROM automation.lifecycle_results result
  WHERE result.run_id=r.id AND result.mode='discovery')
  OR NOT EXISTS(SELECT 1 FROM automation.gas_billing_scan_results result WHERE(result.run_id,result.profile_id,result.source_run_id)=(r.id,NEW.profile_id,NEW.source_run_id))
 THEN RAISE EXCEPTION 'lifecycle_schedule_result_required';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER gas_billing_child_complete AFTER INSERT ON automation.gas_billing_children
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.seal_gas_billing_child();
CREATE FUNCTION automation.require_gas_billing_job_family() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p automation.lifecycle_profiles;BEGIN
 IF NEW.operation<>'lifecycle_action_v1' THEN RETURN NEW;END IF;
 SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=NEW.lifecycle_profile_id;
 IF p.handler='gas_billing_scope' THEN
  IF NOT EXISTS(SELECT 1 FROM automation.gas_billing_enrollments e WHERE(e.profile_id,e.job_id)=(p.id,NEW.id)) THEN RAISE EXCEPTION 'gas_billing_enrollment';END IF;
 ELSIF NEW.interval_seconds IS NOT NULL THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER gas_billing_job_family AFTER INSERT OR UPDATE ON automation.jobs
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.require_gas_billing_job_family();

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='automation' AND p.proname IN('gas_billing_enrollment_source','require_gas_billing_enrollment','save_gas_billing_enrollment',
   'gas_billing_scan_source','lock_gas_billing_owner','gas_billing_dispatch_parent','gas_billing_scan_outcome','require_gas_billing_scan_result','publish_gas_billing_page',
   'seal_gas_billing_scan','seal_gas_billing_child','require_gas_billing_job_family','gas_billing_scope_report',
   'require_gas_billing_assessment','require_gas_billing_candidate_current','gas_billing_discovery_context','require_interactive_job_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler',f.signature);
  IF f.proname IN('save_gas_billing_enrollment','publish_gas_billing_page') THEN
   EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
  END IF;
 END LOOP;
END$$;

ALTER TABLE automation.jobs DROP CONSTRAINT jobs_check;
ALTER TABLE automation.jobs ADD CONSTRAINT jobs_check CHECK((((lifecycle_profile_id IS NULL) AND (((operation = 'worker_check'::text) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NULL)) OR ((operation = 'swarm_job'::text) AND (profile_id IS NOT NULL) AND (profile_version IS NOT NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NULL)) OR ((operation = 'generated_pilot_close_v1'::text) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NOT NULL) AND (pilot_profile_version IS NOT NULL) AND (close_risk_profile_id IS NULL) AND ((interval_seconds IS NULL) OR (interval_seconds = 86400))) OR ((operation = 'close_book_risk_v1'::text) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NOT NULL) AND (interval_seconds IS NULL)))) OR ((operation = 'lifecycle_action_v1'::text) AND (lifecycle_profile_id IS NOT NULL) AND (profile_id IS NULL) AND (profile_version IS NULL) AND (pilot_profile_id IS NULL) AND (pilot_profile_version IS NULL) AND (close_risk_profile_id IS NULL) AND (timeout_seconds <= 30))));
ALTER TABLE automation.lifecycle_profiles DROP CONSTRAINT lifecycle_profiles_check;
ALTER TABLE automation.lifecycle_profiles ADD CONSTRAINT lifecycle_profiles_check CHECK(((definition_revision_id IS NOT NULL AND trade_id IS NOT NULL AND binding_id IS NOT NULL AND occurrence_id IS NOT NULL) AND (((gas_revision IS NULL) AND (close_id IS NOT NULL) AND (((handler = 'futures'::text) AND (action = 'futures.margin'::text) AND (future_revision IS NOT NULL) AND (margin_policy_id IS NOT NULL) AND (margin_policy_revision IS NOT NULL) AND (option_revision IS NULL) AND (swap_revision IS NULL) AND (option_profile_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL)) OR ((handler = 'listed_option'::text) AND (action = 'option.expiry'::text) AND (option_revision IS NOT NULL) AND (option_profile_revision IS NOT NULL) AND (margin_policy_id IS NOT NULL) AND (margin_policy_revision IS NOT NULL) AND (future_revision IS NULL) AND (swap_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL)) OR ((handler = 'financial_swap'::text) AND (action = 'swap.determine'::text) AND (swap_revision IS NOT NULL) AND (swap_profile_id IS NOT NULL) AND (swap_profile_revision IS NOT NULL) AND (future_revision IS NULL) AND (option_revision IS NULL) AND (option_profile_revision IS NULL) AND (margin_policy_id IS NULL) AND (margin_policy_revision IS NULL)))) OR ((handler = 'gas_billing'::text) AND (action = 'gas.bill_daily'::text) AND (gas_revision IS NOT NULL) AND (close_id IS NULL) AND (future_revision IS NULL) AND (option_revision IS NULL) AND (swap_revision IS NULL) AND (option_profile_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL) AND (margin_policy_id IS NULL) AND (margin_policy_revision IS NULL)))) OR (handler='gas_billing_scope' AND action='gas.bill_daily' AND definition_revision_id IS NULL AND close_id IS NULL AND trade_id IS NULL AND binding_id IS NULL AND occurrence_id IS NULL AND gas_revision IS NULL AND future_revision IS NULL AND option_revision IS NULL AND swap_revision IS NULL AND option_profile_revision IS NULL AND swap_profile_id IS NULL AND swap_profile_revision IS NULL AND margin_policy_id IS NULL AND margin_policy_revision IS NULL));
ALTER TABLE automation.lifecycle_results DROP CONSTRAINT lifecycle_results_check;
ALTER TABLE automation.lifecycle_results ADD CONSTRAINT lifecycle_results_check CHECK((((mode = 'manual'::text) AND (proposal IS NULL) AND (outcome IS NULL) AND (command_id IS NULL)) OR ((mode = 'review'::text) AND (jsonb_typeof(proposal) = 'object'::text) AND (outcome IS NULL) AND (command_id IS NULL)) OR ((mode = 'automatic'::text) AND (proposal IS NULL) AND (jsonb_typeof(outcome) = 'object'::text) AND (command_id = run_id))) OR (mode='discovery' AND proposal IS NULL AND jsonb_typeof(outcome)='object' AND command_id IS NULL));
ALTER TABLE automation.lifecycle_results DROP CONSTRAINT lifecycle_results_mode_check;
ALTER TABLE automation.lifecycle_results ADD CONSTRAINT lifecycle_results_mode_check CHECK(mode IN('manual','review','automatic','discovery'));
ALTER TABLE automation.lifecycle_profiles ALTER COLUMN definition_revision_id DROP NOT NULL;
ALTER TABLE automation.lifecycle_profiles ALTER COLUMN trade_id DROP NOT NULL;
ALTER TABLE automation.lifecycle_profiles ALTER COLUMN binding_id DROP NOT NULL;
ALTER TABLE automation.lifecycle_profiles ALTER COLUMN occurrence_id DROP NOT NULL;

CREATE OR REPLACE FUNCTION automation.create_lifecycle_job_scoped(catalog uuid, request jsonb, parent_run uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE gas automation.lifecycle_gas_sources;g jsonb=request#>'{definition,settings,gasSource}';processing_at timestamptz;c security.request_contexts;p automation.lifecycle_profiles;j automation.jobs;anchor market_data.market_closes;
 schedule jsonb=request->'schedule';definition jsonb=request->'definition';target jsonb=definition->'target';settings jsonb=definition->'settings';previous automation.commands;
 candidate jsonb;permission text;bounds jsonb='{}';until_at timestamptz;response jsonb;operation uuid;checked_at timestamptz=clock_timestamp();generation bigint;parent automation.jobs;enrollment automation.gas_billing_enrollments;assessment automation.gas_billing_assessments;
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
  OR jsonb_typeof(settings) IS DISTINCT FROM 'object' OR settings-ARRAY['tradeRevision','optionProfileRevision','swapProfileId','swapProfileRevision','marginPolicyId','marginPolicyRevision','gasSource']<>'{}'::jsonb
  OR NOT(settings ?& ARRAY['tradeRevision','optionProfileRevision','swapProfileId','swapProfileRevision','marginPolicyId','marginPolicyRevision'])
  OR schedule->>'operation' IS DISTINCT FROM 'lifecycle_action_v1' OR schedule->'intervalSeconds' IS DISTINCT FROM 'null'::jsonb
  OR schedule->'profileId' IS DISTINCT FROM 'null'::jsonb OR schedule->'profileVersion' IS DISTINCT FROM 'null'::jsonb
  OR definition->>'knowledgePolicy' IS DISTINCT FROM 'current_knowledge_at_execution_v1'
  OR definition->>'settingsPolicy' IS DISTINCT FROM 'source_known_exact_action_settings_v1'
  OR definition->>'inputPolicy' IS DISTINCT FROM 'source_derived_only_no_cash_corrections_v1'
  OR(schedule->>'timeoutSeconds')::integer NOT BETWEEN 1 AND 30 THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 IF target->>'handler'='gas_billing' THEN
  IF jsonb_typeof(g) IS DISTINCT FROM 'object' OR NOT(g?&ARRAY['cashSlot','day','processingConvention','businessDate','evaluationAt','selectionPolicy','billingProfile','decimalPlaces','roundingPolicy','scheduledPolicy'])
   OR g-ARRAY['cashSlot','day','processingConvention','businessDate','evaluationAt','selectionPolicy','billingProfile','decimalPlaces','roundingPolicy','scheduledPolicy']<>'{}'::jsonb
   OR definition->'closeId' IS DISTINCT FROM 'null'::jsonb
   OR g->>'day'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR(g->>'day')::date::text IS DISTINCT FROM g->>'day'
   OR g->>'businessDate'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR(g->>'businessDate')::date::text IS DISTINCT FROM g->>'businessDate'
   OR jsonb_typeof(g->'cashSlot') IS DISTINCT FROM 'number' OR jsonb_typeof(g->'decimalPlaces') IS DISTINCT FROM 'number'
  THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 ELSIF settings?'gasSource' THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 operation=(schedule->>'requestId')::uuid;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 c=automation.actor();
 IF parent_run IS NOT NULL AND (automation.lock_lifecycle_run()).id IS DISTINCT FROM parent_run THEN RAISE EXCEPTION 'gas_billing_dispatch_scope' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 PERFORM 1 FROM security.principals WHERE id=CASE WHEN parent_run IS NULL THEN c.actor_id ELSE(SELECT job.owner_id FROM automation.runs run JOIN automation.jobs job ON job.id=run.job_id WHERE run.id=parent_run) END FOR UPDATE;
 IF generation IS DISTINCT FROM(SELECT e.generation FROM security.epoch e) THEN RAISE EXCEPTION 'lifecycle_schedule_authority' USING ERRCODE='42501';END IF;
 c=automation.actor();
 SELECT * INTO previous FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  IF previous.request IS DISTINCT FROM request THEN RAISE EXCEPTION 'job_conflict';END IF;
 ELSE
  SELECT * INTO previous FROM automation.commands x WHERE(x.tenant_id,x.workspace_id,x.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND x.request-'schedule'=create_lifecycle_job_scoped.request-'schedule' AND(x.request->'schedule')-'requestId'=schedule-'requestId' ORDER BY x.committed_at,x.request_id LIMIT 1;
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
 IF target->>'handler'<>'gas_billing' THEN
  SELECT * INTO anchor FROM market_data.market_closes a WHERE(a.catalog_id,a.id)=(catalog,(definition->>'closeId')::uuid) AND security.market_close_visible(a.catalog_id,a.id);
  IF anchor.id IS NULL THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
  processing_at=anchor.at;
 END IF;
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
  SELECT anchor.definition_revision_id INTO p.definition_revision_id FROM lifecycle.swap_profiles selected
   JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(selected.catalog_id,selected.trade_id,selected.origin_revision)
   WHERE(selected.catalog_id,selected.id,selected.revision,selected.trade_id,selected.origin_revision,selected.action,selected.policy_id,selected.policy_revision,r.binding_id)=
    (catalog,p.swap_profile_id,p.swap_profile_revision,p.trade_id,p.swap_revision,p.action,p.policy_id,p.policy_revision,p.binding_id)
    AND automation.lifecycle_swap_occurrence(p)=p.occurrence_id;
 ELSIF(p.handler,p.action)=('gas_billing','gas.bill_daily') THEN
  p.gas_revision=(settings->>'tradeRevision')::integer;
  gas.profile_id=p.id;gas.catalog_id=catalog;gas.trade_id=p.trade_id;gas.instrument_id=p.binding_id;gas.trade_revision=p.gas_revision;
  gas.cash_slot=(g->>'cashSlot')::integer;gas.day=(g->>'day')::date;gas.processing_convention=g->>'processingConvention';
  gas.business_date=(g->>'businessDate')::date;gas.evaluation_at=settlement_data.futures_margin_utc_instant(g->>'evaluationAt');
  gas.selection_policy=g->>'selectionPolicy';gas.billing_profile=g->>'billingProfile';gas.decimal_places=(g->>'decimalPlaces')::integer;
  gas.rounding_policy=g->>'roundingPolicy';gas.scheduled_policy=g->>'scheduledPolicy';processing_at=gas.evaluation_at;
  SELECT m.definition_revision_id INTO p.definition_revision_id FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id,m.slot)=(catalog,p.binding_id,gas.cash_slot);
 ELSE RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 IF p.definition_revision_id IS NULL OR(p.handler<>'gas_billing' AND p.definition_revision_id<>anchor.definition_revision_id) THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 IF p.handler<>'gas_billing' THEN PERFORM automation.require_lifecycle_settings(p);END IF;
 IF NOT EXISTS(SELECT 1 FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action)=(catalog,p.policy_id,p.policy_revision,p.action)
   AND a.from_at<=processing_at AND processing_at<a.until_at) THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
  IF parent_run IS NOT NULL THEN
   parent=automation.gas_billing_dispatch_parent(catalog,definition);
   SELECT * INTO enrollment FROM automation.gas_billing_enrollments WHERE profile_id=parent.lifecycle_profile_id;
   SELECT * INTO assessment FROM automation.gas_billing_assessments WHERE run_id=parent_run AND(trade_id,cash_slot,day)=(p.trade_id,gas.cash_slot,gas.day);
   IF parent.id IS NULL OR enrollment.profile_id IS NULL OR assessment.run_id IS NULL
    OR(schedule->>'timeoutSeconds')::integer IS DISTINCT FROM enrollment.child_timeout_seconds
    OR(schedule->>'maximumAttempts')::integer IS DISTINCT FROM enrollment.child_maximum_attempts
    OR(schedule->>'expiresAt')::timestamptz IS DISTINCT FROM parent.expires_at
    OR(schedule->>'dueAt')::timestamptz>clock_timestamp()
    OR(SELECT count(*) FROM automation.gas_billing_children WHERE assessment_run_id=parent_run)>=enrollment.dispatch_limit
   THEN RAISE EXCEPTION 'gas_billing_dispatch_scope' USING ERRCODE='42501';END IF;
  END IF;
  IF parent.id IS NOT NULL THEN PERFORM 1 FROM security.principals WHERE id=parent.owner_id FOR UPDATE;END IF;
  IF(SELECT count(*) FROM automation.jobs WHERE owner_id=coalesce(parent.owner_id,c.actor_id) AND(state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=jobs.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
  INSERT INTO automation.lifecycle_profiles SELECT(p).*;
  IF p.handler='gas_billing' THEN
   INSERT INTO automation.lifecycle_gas_sources SELECT(gas).*;
   PERFORM automation.require_lifecycle_settings(p);
  END IF;
  IF p.handler='futures' THEN INSERT INTO automation.lifecycle_future_targets VALUES(p.id,catalog,p.trade_id,p.binding_id,p.margin_policy_id,p.margin_policy_revision,p.occurrence_id);END IF;
  j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;
  IF parent.id IS NOT NULL THEN j.owner_id=parent.owner_id;j.delegation_id=parent.delegation_id;END IF;
  j.name=schedule->>'name';j.operation=schedule->>'operation';j.state='active';j.version=1;j.next_due_at=settlement_data.futures_margin_utc_instant(schedule->>'dueAt');j.expires_at=settlement_data.futures_margin_utc_instant(schedule->>'expiresAt');
  j.interval_seconds=NULL;j.time_zone=schedule->>'timeZone';j.misfire=schedule->>'misfire';j.overlap_policy=schedule->>'overlap';j.timeout_seconds=(schedule->>'timeoutSeconds')::integer;j.maximum_attempts=(schedule->>'maximumAttempts')::integer;
  j.lifecycle_profile_id=p.id;j.created_at=clock_timestamp();j.last_reconciled_at='-infinity';
  IF j.expires_at<=clock_timestamp() OR j.expires_at<=j.next_due_at OR j.expires_at>clock_timestamp()+interval '366 days' OR j.next_due_at<clock_timestamp()-interval '1 day'
   OR NOT automation.authorized(j) THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
  INSERT INTO automation.jobs SELECT(j).*;
  IF parent_run IS NOT NULL THEN
   INSERT INTO automation.gas_billing_children VALUES(j.id,enrollment.profile_id,catalog,parent_run,automation.gas_billing_scan_source(parent_run),
    assessment.trade_id,assessment.cash_slot,assessment.day,assessment.manual_revision_id,assessment.facility_target_id,assessment.scheduled_revision_id);
  END IF;
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
END$function$
;
ALTER FUNCTION automation.create_lifecycle_job_scoped(uuid,jsonb,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.create_lifecycle_job_scoped(uuid,jsonb,uuid) FROM PUBLIC,openetrm_runtime,openetrm_scheduler,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION automation.lifecycle_definition(p automation.lifecycle_profiles)
 RETURNS jsonb
 LANGUAGE sql
 STABLE SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
 SELECT CASE WHEN p.handler='gas_billing_scope' THEN jsonb_build_object('kind',p.handler,'source',automation.gas_billing_enrollment_source(p.id)) ELSE jsonb_build_object('closeId',p.close_id,'target',jsonb_build_object('handler',p.handler,'scopeId',p.trade_id,'bindingId',p.binding_id),
  'action',p.action,'occurrenceId',p.occurrence_id,'policyId',p.policy_id,'policyRevision',p.policy_revision,'knowledgePolicy',p.knowledge_policy,'settingsPolicy',p.settings_policy,'inputPolicy',p.input_policy,
  'settings',jsonb_build_object('tradeRevision',CASE p.handler WHEN 'futures' THEN p.future_revision WHEN 'listed_option' THEN p.option_revision WHEN 'financial_swap' THEN p.swap_revision WHEN 'gas_billing' THEN p.gas_revision END,
   'optionProfileRevision',p.option_profile_revision,'swapProfileId',p.swap_profile_id,'swapProfileRevision',p.swap_profile_revision,'marginPolicyId',p.margin_policy_id,'marginPolicyRevision',p.margin_policy_revision)||CASE WHEN p.handler='gas_billing' THEN jsonb_build_object('gasSource',automation.gas_billing_source(p.id)) ELSE '{}'::jsonb END) END;
$function$
;

CREATE OR REPLACE FUNCTION automation.require_lifecycle_settings(p automation.lifecycle_profiles)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE gas automation.lifecycle_gas_sources;BEGIN
 IF p.policy_revision IS DISTINCT FROM(SELECT max(revision) FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id)=(p.catalog_id,p.policy_id))
  OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action)=(p.catalog_id,p.policy_id,p.policy_revision,p.action))
 THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 IF p.handler='gas_billing_scope' THEN PERFORM automation.require_gas_billing_enrollment(p);RETURN;
 ELSIF p.handler='futures' THEN
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
   OR p.occurrence_id IS DISTINCT FROM automation.lifecycle_swap_occurrence(p)
   OR NOT EXISTS(SELECT 1 FROM lifecycle.swap_profiles s JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(s.catalog_id,s.trade_id,s.origin_revision)
    WHERE(s.catalog_id,s.id,s.revision,s.trade_id,s.origin_revision,s.policy_id,s.policy_revision,r.binding_id)=
     (p.catalog_id,p.swap_profile_id,p.swap_profile_revision,p.trade_id,p.swap_revision,p.policy_id,p.policy_revision,p.binding_id))
  THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 ELSIF p.handler='gas_billing' THEN
  SELECT * INTO gas FROM automation.lifecycle_gas_sources WHERE profile_id=p.id;
  PERFORM automation.require_lifecycle_gas_source(p,gas);
 ELSE RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 IF p.handler='financial_swap' THEN PERFORM automation.require_lifecycle_swap_sources(p);END IF;
END$function$
;

CREATE OR REPLACE FUNCTION automation.admit_occurrence_inputs()
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE r automation.runs;j automation.jobs;catalog uuid;day date;policy text;source security.operation_read_coordinates;root automation.runs;
BEGIN
 r=automation.lock_pilot_run();SELECT * INTO STRICT j FROM automation.jobs WHERE id=r.job_id;
 IF j.operation='generated_pilot_close_v1' THEN
  SELECT p.catalog_id INTO STRICT catalog FROM automation.pilot_close_profiles p WHERE(p.id,p.version)=(j.pilot_profile_id,j.pilot_profile_version);
  day=(r.due_at AT TIME ZONE 'UTC')::date;policy='current_at_first_admission_v1';
 ELSIF j.operation='lifecycle_action_v1' THEN
  r=automation.lock_lifecycle_run();
  SELECT p.catalog_id,CASE WHEN p.handler='gas_billing' THEN g.business_date ELSE m.business_date END,p.knowledge_policy INTO STRICT catalog,day,policy FROM automation.lifecycle_profiles p
   LEFT JOIN market_data.market_closes m ON(m.catalog_id,m.id)=(p.catalog_id,p.close_id)
   LEFT JOIN automation.lifecycle_gas_sources g ON g.profile_id=p.id WHERE p.id=j.lifecycle_profile_id;
  SELECT * INTO root FROM automation.runs WHERE id=automation.gas_billing_scan_source(r.id);
  IF root.id IS NOT NULL THEN day=coalesce(root.input_business_on,(root.due_at AT TIME ZONE 'UTC')::date);END IF;
  IF day IS NULL THEN RAISE EXCEPTION 'occurrence_inputs_required';END IF;
 ELSE RAISE EXCEPTION 'occurrence_inputs_required' USING ERRCODE='P0001';END IF;
 IF r.input_catalog_id IS NULL THEN
  IF root.id IS NOT NULL AND root.id<>r.id THEN
   IF root.input_catalog_id IS DISTINCT FROM catalog OR root.input_known_at IS NULL THEN RAISE EXCEPTION 'occurrence_inputs_required';END IF;
   PERFORM security.resolve_operation_context(catalog,day,root.input_known_at);
   IF NOT EXISTS(SELECT 1 FROM security.operation_read_inputs i WHERE(i.catalog_id,i.effective_on,i.known_at,i.system_id,i.timeline)=(catalog,day,root.input_known_at,root.input_system_id,root.input_timeline)) THEN RAISE EXCEPTION 'occurrence_inputs_conflict';END IF;
  ELSE PERFORM security.resolve_operation_context(catalog,day,NULL);END IF;
  SELECT s.* INTO STRICT source FROM security.operation_read_inputs s WHERE(s.catalog_id,s.effective_on)=(catalog,day);
  UPDATE automation.runs SET input_catalog_id=catalog,input_business_on=day,input_known_at=source.known_at,
   input_system_id=source.system_id,input_timeline=source.timeline,input_policy=policy WHERE id=r.id;
 ELSIF(r.input_catalog_id,r.input_business_on,r.input_policy) IS DISTINCT FROM(catalog,day,policy)
 THEN RAISE EXCEPTION 'occurrence_inputs_conflict' USING ERRCODE='P0001';END IF;
 PERFORM automation.lock_pilot_run();
END$function$
;

CREATE OR REPLACE FUNCTION automation.capture_lifecycle_source()
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;anchor market_data.market_closes;gas automation.lifecycle_gas_sources;token uuid;source uuid;mode text;root automation.runs;progress automation.gas_billing_scan_progress;BEGIN
 PERFORM automation.lock_gas_billing_owner();
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 PERFORM automation.require_lifecycle_settings(p);
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO anchor FROM market_data.market_closes WHERE(catalog_id,id)=(p.catalog_id,p.close_id);
 SELECT * INTO gas FROM automation.lifecycle_gas_sources WHERE profile_id=p.id;
 source=(automation.occurrence_read_inputs()).id;
 SELECT a.mode INTO mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 PERFORM automation.lock_lifecycle_run();
 IF p.handler='gas_billing_scope' THEN
  SELECT * INTO root FROM automation.runs WHERE id=automation.gas_billing_scan_source(r.id);
  SELECT * INTO progress FROM automation.gas_billing_scan_progress WHERE profile_id=p.id;
  RETURN jsonb_build_object('discovery',automation.gas_billing_enrollment_source(p.id),'sourceRunId',root.id,
   'context',jsonb_build_object('contextId',source,'asOf',root.due_at,'businessDate',root.input_business_on,'snapshotId',NULL),
   'after',CASE WHEN progress.after_trade_id IS NOT NULL THEN jsonb_build_object('tradeId',progress.after_trade_id,'cashSlot',progress.after_cash_slot,'day',progress.after_day) END);
 END IF;
 RETURN jsonb_build_object('definition',automation.lifecycle_definition(p),
  'context',jsonb_build_object('contextId',source,'asOf',CASE WHEN p.handler='gas_billing' THEN gas.evaluation_at ELSE anchor.at END,'businessDate',CASE WHEN p.handler='gas_billing' THEN gas.business_date ELSE anchor.business_date END,'snapshotId',p.close_id),'mode',mode);
END$function$
;

CREATE OR REPLACE FUNCTION automation.require_lifecycle_action_scope(catalog uuid, kind text, body jsonb, writing boolean)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE token uuid;r automation.runs;j automation.jobs;p automation.lifecycle_profiles;source security.operation_read_coordinates;anchor market_data.market_closes;gas automation.lifecycle_gas_sources;at timestamptz;business_date date;mode text;BEGIN
 SELECT q.id INTO token FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  JOIN automation.runs occurrence ON occurrence.id=b.run_id JOIN automation.jobs task ON task.id=occurrence.job_id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND task.operation='lifecycle_action_v1';
 IF NOT FOUND THEN RETURN;END IF;
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 IF p.handler='gas_billing_scope' THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 source=automation.occurrence_read_inputs();
 IF p.handler='gas_billing' THEN
  SELECT * INTO gas FROM automation.lifecycle_gas_sources WHERE profile_id=p.id;at=gas.evaluation_at;business_date=gas.business_date;
 ELSE
  SELECT * INTO anchor FROM market_data.market_closes WHERE(catalog_id,id)=(p.catalog_id,p.close_id);at=anchor.at;business_date=anchor.business_date;
 END IF;
 SELECT policy.mode INTO mode FROM lifecycle.action_policies policy WHERE(policy.catalog_id,policy.id,policy.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 PERFORM automation.require_lifecycle_settings(p);
 IF source.id IS NULL OR catalog IS DISTINCT FROM p.catalog_id OR kind IS DISTINCT FROM p.action OR body->>'action' IS DISTINCT FROM p.action
  OR(body->>'operationId')::uuid IS DISTINCT FROM r.id OR(body->>'policyId')::uuid IS DISTINCT FROM p.policy_id OR(body->>'policyRevision')::integer IS DISTINCT FROM p.policy_revision
  OR(body#>>'{target,handler}') IS DISTINCT FROM p.handler OR(body#>>'{target,scopeId}')::uuid IS DISTINCT FROM p.trade_id OR(body#>>'{target,bindingId}')::uuid IS DISTINCT FROM p.binding_id
  OR(body#>>'{context,contextId}')::uuid IS DISTINCT FROM source.id OR(body#>>'{context,asOf}')::timestamptz IS DISTINCT FROM at
  OR(body#>>'{context,businessDate}')::date IS DISTINCT FROM business_date OR mode NOT IN('review','automatic') OR mode IS NULL
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
 ELSIF p.handler='gas_billing' THEN
  IF body#>'{selection,billing}' IS DISTINCT FROM jsonb_build_object('tradeId',p.trade_id,'expectedRevision',p.gas_revision,'cashSlot',gas.cash_slot,'day',gas.day,
    'selectionPolicy',gas.selection_policy,'profile',gas.billing_profile,'decimalPlaces',gas.decimal_places,'roundingPolicy',gas.rounding_policy)
   OR body#>>'{selection,processingConvention}' IS DISTINCT FROM gas.processing_convention OR body#>>'{selection,scheduledPolicy}' IS DISTINCT FROM gas.scheduled_policy
   OR body#>'{selection,expectedPreviousId}' IS DISTINCT FROM 'null'::jsonb OR body#>'{selection,correctionDueAt}' IS DISTINCT FROM 'null'::jsonb
   OR body#>'{selection,reason}' IS DISTINCT FROM 'null'::jsonb
  THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 ELSE RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
END$function$
;

CREATE OR REPLACE FUNCTION automation.claim_lifecycle_next(worker_id uuid, lease_seconds integer)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
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
   'dueAt',r.due_at,'intervalSeconds',j.interval_seconds,'profile',NULL);
 END LOOP;
 RETURN NULL;
END$function$
;

CREATE OR REPLACE FUNCTION automation.publish_lifecycle_result(mode text, proposal jsonb, outcome jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;token uuid;source security.operation_read_coordinates;saved_mode text;BEGIN
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 PERFORM automation.require_lifecycle_settings(p);
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 source=automation.occurrence_read_inputs();
 SELECT a.mode INTO saved_mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 IF p.handler='gas_billing_scope' THEN saved_mode='discovery';END IF;
 IF source.id IS NULL OR saved_mode IS DISTINCT FROM mode OR source.catalog_id IS DISTINCT FROM p.catalog_id OR EXISTS(SELECT 1 FROM automation.lifecycle_results WHERE run_id=r.id)
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 IF mode='discovery' THEN PERFORM automation.require_gas_billing_scan_result(r.id,outcome);END IF;
 IF mode='review' THEN PERFORM automation.require_lifecycle_review(p,r,source,proposal);END IF;
 INSERT INTO automation.lifecycle_results VALUES(r.id,token,p.catalog_id,source.id,mode,proposal,outcome,CASE WHEN mode='automatic' THEN r.id END,clock_timestamp());
 RETURN jsonb_build_object('runId',r.id,'mode',mode,'sourceContextId',source.id,'state',CASE mode WHEN 'manual' THEN 'manual_required' WHEN 'review' THEN 'review_required' ELSE 'executed' END);
END$function$
;

CREATE OR REPLACE FUNCTION automation.finish_lifecycle_result()
 RETURNS trigger
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;token uuid;source security.operation_read_coordinates;command lifecycle.commands;body jsonb;cycle_at timestamptz;BEGIN
 -- Product writers queue their native financial seals before the shared
 -- command. This result is inserted last and transitions the occurrence only
 -- after those seals and the required-result link have passed at commit.
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 source=automation.occurrence_read_inputs();
 PERFORM automation.require_lifecycle_settings(p);
 IF(r.id,token,p.catalog_id,source.id) IS DISTINCT FROM(NEW.run_id,NEW.execution_context_id,NEW.catalog_id,NEW.source_context_id)
  OR NEW.mode IS DISTINCT FROM(CASE WHEN p.handler='gas_billing_scope' THEN 'discovery' ELSE (SELECT a.mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision)) END)
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 IF NEW.mode='discovery' THEN PERFORM automation.require_gas_billing_scan_result(r.id,NEW.outcome);END IF;
 IF NEW.mode='review' THEN PERFORM automation.require_lifecycle_review(p,r,source,NEW.proposal);END IF;
 IF NEW.mode<>'automatic' AND EXISTS(SELECT 1 FROM lifecycle.commands c WHERE(c.catalog_id,c.operation_id)=(NEW.catalog_id,r.id))
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 IF NEW.mode='automatic' THEN
  SELECT * INTO command FROM lifecycle.commands c WHERE(c.catalog_id,c.operation_id)=(NEW.catalog_id,NEW.command_id);body=command.request;
  IF command.operation_id IS NULL OR(command.kind,command.policy_id,command.policy_revision) IS DISTINCT FROM(p.action,p.policy_id,p.policy_revision)
   OR command.actor_id IS DISTINCT FROM(SELECT actor_id FROM security.request_contexts WHERE id=token)
   OR command.outcome IS DISTINCT FROM NEW.outcome OR(body#>>'{operationId}')::uuid IS DISTINCT FROM r.id
   OR(body#>>'{context,contextId}')::uuid IS DISTINCT FROM source.id
   OR(body#>>'{target,scopeId}')::uuid IS DISTINCT FROM p.trade_id OR(body#>>'{target,bindingId}')::uuid IS DISTINCT FROM p.binding_id OR body#>>'{target,handler}' IS DISTINCT FROM p.handler
  THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 END IF;
 UPDATE automation.runs SET state='succeeded',finished_at=clock_timestamp(),error_code=NULL WHERE id=r.id;
 IF p.handler='gas_billing_scope' AND EXISTS(SELECT 1 FROM automation.gas_billing_scan_progress WHERE profile_id=p.id AND source_run_id IS NOT NULL) THEN
  UPDATE automation.jobs SET state='active',version=version+1,next_due_at=clock_timestamp()+
   CASE WHEN(SELECT backpressure FROM automation.gas_billing_scan_results WHERE run_id=r.id) THEN interval '60 seconds' ELSE interval '1 second' END WHERE id=j.id;
 ELSIF p.handler='gas_billing_scope' AND j.interval_seconds IS NOT NULL THEN
  SELECT root.due_at INTO STRICT cycle_at FROM automation.gas_billing_scan_results scan JOIN automation.runs root ON root.id=scan.source_run_id WHERE scan.run_id=r.id;
  UPDATE automation.jobs SET state='active',version=version+1,
   next_due_at=cycle_at+(floor(extract(epoch FROM(clock_timestamp()-cycle_at))/j.interval_seconds)+1)*j.interval_seconds*interval '1 second' WHERE id=j.id;
 ELSE UPDATE automation.jobs SET state='completed',version=version+1 WHERE id=j.id;END IF;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,(SELECT actor_id FROM security.request_contexts WHERE id=token),'completed',clock_timestamp(),r.fence);
 RETURN NEW;
END$function$
;

CREATE OR REPLACE FUNCTION automation.create_lifecycle_job(catalog uuid,request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned())
 THEN RAISE EXCEPTION 'lifecycle_schedule_scope' USING ERRCODE='42501';END IF;
 RETURN automation.create_lifecycle_job_scoped(catalog,request,NULL);
END$$;;

CREATE OR REPLACE FUNCTION automation.lifecycle_job_result(job uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
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
  'results',coalesce((SELECT jsonb_agg(jsonb_build_object('runId',r.run_id,'sourceContextId',r.source_context_id,'knownAt',(SELECT t.known_at FROM LATERAL security.knowledge_from_inputs(r.catalog_id,r.source_business_on,r.source_known_at,r.source_system_id,r.source_timeline) t WHERE(t.catalog_id,t.id)=(r.catalog_id,r.source_context_id)),'mode',r.mode,'proposal',r.proposal,'outcome',r.outcome,'recordedAt',r.recorded_at) ORDER BY r.recorded_at,r.run_id)
   FROM (SELECT saved.* FROM automation.lifecycle_results saved JOIN automation.runs occurrence ON occurrence.id=saved.run_id WHERE occurrence.job_id=j.id ORDER BY saved.recorded_at DESC,saved.run_id LIMIT 100) r),'[]'::jsonb)) INTO result;
 IF p.handler='gas_billing_scope' THEN result=result||jsonb_build_object('scope',automation.gas_billing_scope_report(j.id));END IF;
 PERFORM automation.require_lifecycle_job_result(job,until_at);
 RETURN jsonb_build_object('outcome',result,'authorityUntil',until_at);
END$function$
;

CREATE OR REPLACE FUNCTION security.trading_access(catalog uuid, permission text)
 RETURNS boolean
 LANGUAGE sql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
 SELECT security.has_permission(permission) AND security.market_access(catalog,'ReadMarketData') AND (permission NOT IN('AmendTrade','BookTrade') OR NOT automation.gas_billing_discovery_context());
$function$
;

CREATE OR REPLACE FUNCTION settlement_data.require_futures_margin_authority(catalog uuid, permission text, until_at timestamp with time zone)
 RETURNS uuid
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE actor uuid;bound uuid;hosted text;BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT q.actor_id,q.market_context_id,q.hosted_session_digest INTO actor,bound,hosted FROM security.request_contexts q
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF permission IN('AmendTrade','BookTrade') AND automation.gas_billing_discovery_context() THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
 IF until_at IS NULL OR clock_timestamp()>=until_at OR actor IS NULL OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.has_permission(permission) IS NOT TRUE
  OR(permission='AmendTrade' AND security.trading_access(catalog,'ReadTrades') IS NOT TRUE)
 THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
 IF hosted IS NOT NULL THEN PERFORM security.require_hosted_session();END IF;
 IF bound IS NOT NULL OR security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 RETURN actor;
END$function$
;

CREATE OR REPLACE FUNCTION automation.create_job(request jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz:=clock_timestamp();v_request_id uuid;response jsonb;
BEGIN
 PERFORM automation.require_interactive_job_context();
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
END $function$
;

CREATE OR REPLACE FUNCTION automation.update_job(request jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz;response jsonb;v_request_id uuid;
BEGIN
 PERFORM automation.require_interactive_job_context();
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
END $function$
;

CREATE OR REPLACE FUNCTION automation.control_job(request jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz:=clock_timestamp();response jsonb;v_request_id uuid;action text;
BEGIN
 PERFORM automation.require_interactive_job_context();
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
END $function$
;

CREATE OR REPLACE FUNCTION automation.create_pilot_close_job(schedule jsonb, definition jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;p automation.pilot_close_profiles;
 anchor market_data.market_closes;n timestamptz;operation uuid;payload jsonb;response jsonb;
BEGIN
 PERFORM automation.require_interactive_job_context();
 c=automation.actor();
 IF NOT security.current_write_context() OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(schedule) IS DISTINCT FROM 'object' OR jsonb_typeof(definition) IS DISTINCT FROM 'object'
 OR schedule-ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']<>'{}'::jsonb
 OR NOT(schedule ?& ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion'])
 OR definition-ARRAY['session','priorCloseId','bookIds','curveCodes','sourceCode','seed','leafMoves','generator','tickBasis','parameterPolicy','frozenPolicy','membershipPolicy','calendarPolicy','closePolicy','valuationPolicy']<>'{}'::jsonb
 OR NOT(definition ?& ARRAY['session','priorCloseId','bookIds','curveCodes','sourceCode','seed','leafMoves','generator','tickBasis','parameterPolicy','frozenPolicy','membershipPolicy','calendarPolicy','closePolicy','valuationPolicy'])
 THEN RAISE EXCEPTION 'pilot_profile';END IF;
 operation=(schedule->>'requestId')::uuid;payload=jsonb_build_object('schedule',schedule,'definition',definition);
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;c=automation.actor();n=clock_timestamp();
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  SELECT * INTO j FROM automation.jobs WHERE id=previous.job_id;
  IF previous.request<>payload THEN RAISE EXCEPTION 'job_conflict';END IF;
  IF NOT automation.pilot_authorized(j,NULL) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
  RETURN previous.response;
 END IF;
 SELECT * INTO anchor FROM market_data.market_closes WHERE id=(definition->>'priorCloseId')::uuid AND security.market_close_visible(catalog_id,id);
 IF anchor.id IS NULL OR NOT security.market_access(anchor.catalog_id,'ReadMarketData') OR NOT security.reference_version_visible(anchor.reference_catalog_id,anchor.reference_revision_id)
 OR jsonb_typeof(definition->'bookIds') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'bookIds') NOT BETWEEN 1 AND 128
 OR jsonb_typeof(definition->'curveCodes') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'curveCodes') NOT BETWEEN 1 AND 128
 OR jsonb_typeof(definition->'leafMoves') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'leafMoves') NOT BETWEEN 1 AND 128
 OR(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND(state IN('active','paused','blocked')
 OR EXISTS(SELECT 1 FROM automation.runs pending_run WHERE pending_run.job_id=jobs.id AND pending_run.state IN('running','cancellation_requested'))))>=64
 THEN RAISE EXCEPTION 'pilot_profile';END IF;
 IF security.close_in_force(anchor.catalog_id,anchor.session_code,anchor.business_date)<>anchor.id THEN RAISE EXCEPTION 'close_replaced' USING ERRCODE='P0001';END IF;
 PERFORM automation.require_generated_anchor_source(anchor.catalog_id,anchor.id,anchor.definition_revision_id);
 p.id=gen_random_uuid();p.version=1;p.tenant_id=c.tenant_id;p.workspace_id=c.workspace_id;p.catalog_id=anchor.catalog_id;p.definition_revision_id=anchor.definition_revision_id;p.prior_close_id=anchor.id;
 p.reference_catalog_id=anchor.reference_catalog_id;p.reference_revision_id=anchor.reference_revision_id;p.source_code=definition->>'sourceCode';
 p.seed=(definition->>'seed')::integer;p.generator=definition->>'generator';p.tick_basis=definition->>'tickBasis';p.parameter_policy=definition->>'parameterPolicy';
 p.frozen_policy=definition->>'frozenPolicy';p.membership_policy=definition->>'membershipPolicy';p.calendar_policy=definition->>'calendarPolicy';p.close_policy=definition->>'closePolicy';p.valuation_policy=definition->'valuationPolicy';p.session_code=definition->>'session';
 INSERT INTO automation.pilot_close_profiles SELECT(p).*;
 INSERT INTO automation.pilot_close_books SELECT p.id,p.version,p.catalog_id,p.definition_revision_id,v::uuid FROM jsonb_array_elements_text(definition->'bookIds')v;
 INSERT INTO automation.pilot_close_curves SELECT p.id,p.version,p.catalog_id,p.definition_revision_id,v FROM jsonb_array_elements_text(definition->'curveCodes')v;
 INSERT INTO automation.pilot_close_moves SELECT p.id,p.version,p.catalog_id,p.definition_revision_id,x.curve,x."maxTicks" FROM jsonb_to_recordset(definition->'leafMoves')x(curve text,"maxTicks" integer);
 j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;
 j.name=schedule->>'name';j.operation=schedule->>'operation';j.state='active';j.version=1;j.next_due_at=(schedule->>'dueAt')::timestamptz;j.expires_at=(schedule->>'expiresAt')::timestamptz;
 j.interval_seconds=(schedule->>'intervalSeconds')::integer;j.time_zone=schedule->>'timeZone';j.misfire=schedule->>'misfire';j.overlap_policy=schedule->>'overlap';
 j.timeout_seconds=(schedule->>'timeoutSeconds')::integer;j.maximum_attempts=(schedule->>'maximumAttempts')::integer;j.profile_id=(schedule->>'profileId')::uuid;j.profile_version=(schedule->>'profileVersion')::integer;
 j.pilot_profile_id=p.id;j.pilot_profile_version=p.version;j.created_at=n;j.last_reconciled_at='-infinity';
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR j.operation IS DISTINCT FROM 'generated_pilot_close_v1' OR j.expires_at<=n OR j.expires_at<=j.next_due_at OR j.next_due_at<=anchor.at
 OR j.expires_at>n+interval '366 days' OR j.next_due_at<n-interval '1 day' OR NOT automation.pilot_authorized(j,NULL)
 THEN RAISE EXCEPTION 'pilot_profile';END IF;
 INSERT INTO automation.jobs SELECT(j).*;
 response=automation.description(j)||jsonb_build_object('requestId',operation);INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,payload,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,'created',n);
 RETURN response;
END;$function$
;

CREATE OR REPLACE FUNCTION automation.create_close_risk_job(request jsonb, context uuid, factors jsonb, declared_rows bigint, declared_row_scenarios bigint)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE c security.request_contexts;j automation.jobs;p automation.close_risk_profiles;anchor market_data.market_closes;
 schedule jsonb:=request->'schedule';definition jsonb:=request->'definition';previous automation.commands;response jsonb;n timestamptz;operation uuid;
BEGIN
 PERFORM automation.require_interactive_job_context();
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
 OR definition->'valuationPolicy'->>'rateNumericalPolicy' IS DISTINCT FROM 'rate_curve_lattice30_to_even_18_v1'
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
 p.context_id=context;SELECT known_through INTO p.trading_known_through FROM security.operation_trading WHERE(catalog_id,context_id)=(p.catalog_id,context);
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
END;$function$
;
COMMIT;
