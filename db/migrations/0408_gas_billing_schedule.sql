-- Exact gas operating sources reuse lifecycle tasks and occurrence-owned inputs.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $guard$ DECLARE f record;p pg_proc;BEGIN
 FOR f IN SELECT * FROM(VALUES
 ('automation.lifecycle_definition(automation.lifecycle_profiles)','78c42b3e0b6f015230803f88657fc73f7328b8485541f4ebddf5e9eacf64734a','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','s'),
 ('automation.require_lifecycle_settings(automation.lifecycle_profiles)','57be0565f38de3a18534b5ca64f67a4635844dcf33d7b8a2b7066ec84d9ff081','{openetrm_security_owner=X/openetrm_security_owner}','v'),
 ('automation.create_lifecycle_job(uuid,jsonb)','8583d6ad975133d964f17d24b4c7eee4513cf90b623542bfa81dd4a3f5934474','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.admit_occurrence_inputs()','376f3277d71abb4909d58397ed735bb66d4f269ddb7083a572feb5ce033af255','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.capture_lifecycle_source()','540d3e8e214653ce7aedeb20d6dbed48f2438c0ae066d24f68fb7cf1826659f6','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}','v'),
 ('automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean)','d0ba7ad2a8d9ebfe5fcd1845d2a2f32c9af64cc69e77a27c38865b8f6eeb5702','{openetrm_security_owner=X/openetrm_security_owner}','v'))v(signature,digest,acl,volatility) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=f.signature::regprocedure;
  IF encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>f.digest
   OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM f.acl
   OR NOT p.prosecdef OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proisstrict OR p.proleakproof
   OR p.provolatile<>f.volatility OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_billing_schedule_upgrade: unexpected helper %',p.oid::regprocedure;END IF;
 END LOOP;
END$guard$;
DO $guard$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='automation.lifecycle_profiles'::regclass AND conname='lifecycle_profiles_check' AND pg_get_constraintdef(oid)='CHECK ((((handler = ''futures''::text) AND (action = ''futures.margin''::text) AND (future_revision IS NOT NULL) AND (margin_policy_id IS NOT NULL) AND (margin_policy_revision IS NOT NULL) AND (option_revision IS NULL) AND (swap_revision IS NULL) AND (option_profile_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL)) OR ((handler = ''listed_option''::text) AND (action = ''option.expiry''::text) AND (option_revision IS NOT NULL) AND (option_profile_revision IS NOT NULL) AND (margin_policy_id IS NOT NULL) AND (margin_policy_revision IS NOT NULL) AND (future_revision IS NULL) AND (swap_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL)) OR ((handler = ''financial_swap''::text) AND (action = ''swap.determine''::text) AND (swap_revision IS NOT NULL) AND (swap_profile_id IS NOT NULL) AND (swap_profile_revision IS NOT NULL) AND (future_revision IS NULL) AND (option_revision IS NULL) AND (option_profile_revision IS NULL) AND (margin_policy_id IS NULL) AND (margin_policy_revision IS NULL))))') THEN RAISE EXCEPTION 'gas_billing_schedule_upgrade: unexpected source constraint';END IF;END$guard$;
ALTER TABLE automation.lifecycle_profiles ADD COLUMN gas_revision integer;
ALTER TABLE automation.lifecycle_profiles ALTER COLUMN close_id DROP NOT NULL;
ALTER TABLE automation.lifecycle_profiles ADD FOREIGN KEY(catalog_id,trade_id,gas_revision) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision);
ALTER TABLE automation.lifecycle_profiles ADD FOREIGN KEY(catalog_id,definition_revision_id) REFERENCES market_data.revisions(catalog_id,id);
ALTER TABLE automation.lifecycle_profiles ADD UNIQUE(id,catalog_id,trade_id,binding_id,gas_revision);
CREATE TABLE automation.lifecycle_gas_sources(
 profile_id uuid PRIMARY KEY,catalog_id uuid NOT NULL,trade_id uuid NOT NULL,instrument_id uuid NOT NULL,trade_revision integer NOT NULL,
 cash_slot integer NOT NULL,day date NOT NULL CHECK(isfinite(day)),processing_convention text NOT NULL CHECK(processing_convention='gas_processing_instant_utc_v1'),
 business_date date NOT NULL CHECK(isfinite(business_date)),evaluation_at ref_data.instant NOT NULL,
 selection_policy text NOT NULL CHECK(selection_policy IN('final_allocated_billing_v1','final_facility_allocated_billing_v1')),
 billing_profile text NOT NULL CHECK(billing_profile IN('gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1')),
 decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),
 scheduled_policy text NOT NULL CHECK(scheduled_policy='manual_scheduled_quantity_v1'),
 CHECK(business_date=(evaluation_at AT TIME ZONE 'UTC')::date),
 FOREIGN KEY(profile_id,catalog_id,trade_id,instrument_id,trade_revision) REFERENCES automation.lifecycle_profiles(id,catalog_id,trade_id,binding_id,gas_revision),
 FOREIGN KEY(catalog_id,instrument_id,cash_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot));
ALTER TABLE automation.lifecycle_gas_sources ENABLE ROW LEVEL SECURITY;
ALTER TABLE automation.lifecycle_gas_sources FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON automation.lifecycle_gas_sources TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT ON automation.lifecycle_gas_sources TO openetrm_security_owner;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.lifecycle_gas_sources FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION automation.gas_billing_source(profile uuid) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('cashSlot',g.cash_slot,'day',g.day,'processingConvention',g.processing_convention,'businessDate',g.business_date,
  'evaluationAt',to_char(g.evaluation_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'selectionPolicy',g.selection_policy,'billingProfile',g.billing_profile,'decimalPlaces',g.decimal_places,'roundingPolicy',g.rounding_policy,'scheduledPolicy',g.scheduled_policy)
 FROM automation.lifecycle_gas_sources g WHERE g.profile_id=profile
$$;
ALTER FUNCTION automation.gas_billing_source(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.gas_billing_source(uuid) FROM PUBLIC;

CREATE FUNCTION automation.require_lifecycle_gas_source(p automation.lifecycle_profiles,g automation.lifecycle_gas_sources) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE m trading.gas_component_terms;o trading.gas_delivery_obligations;contract trading.facility_gas_target;BEGIN
 PERFORM security.require_gas_quantity_trade(p.catalog_id,p.trade_id,false);
 IF g.profile_id IS DISTINCT FROM p.id OR(g.catalog_id,g.trade_id,g.instrument_id,g.trade_revision) IS DISTINCT FROM(p.catalog_id,p.trade_id,p.binding_id,p.gas_revision)
  OR p.gas_revision IS DISTINCT FROM(SELECT max(revision) FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(p.catalog_id,p.trade_id))
  OR NOT EXISTS(SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(p.catalog_id,p.trade_id,p.gas_revision,p.binding_id) AND r.effective_at<=g.evaluation_at)
  OR p.occurrence_id IS DISTINCT FROM(substr(encode(sha256(convert_to('gas_daily_billing_v1/'||p.catalog_id::text||'/'||p.trade_id::text||'/'||g.cash_slot::text||'/'||to_char(g.day,'YYYY-MM-DD'),'UTF8')),'hex'),1,32)::uuid)
 THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(p.catalog_id,p.binding_id,g.cash_slot);
 SELECT * INTO o FROM trading.gas_delivery_obligations WHERE(catalog_id,trade_id,instrument_id,slot)=(p.catalog_id,p.trade_id,p.binding_id,m.source_slot);
 IF m.definition_revision_id IS DISTINCT FROM p.definition_revision_id OR m.stage IS DISTINCT FROM 'delivery' OR m.role NOT IN('consideration','fee')
  OR m.slot IS NULL OR g.day<m.delivery_from OR g.day>=m.delivery_to OR o.id IS NULL
  OR EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e WHERE(e.catalog_id,e.instrument_id)=(p.catalog_id,p.binding_id) AND e.slot IN(m.slot,m.source_slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_members e WHERE(e.catalog_id,e.instrument_id,e.slot)=(p.catalog_id,p.binding_id,m.slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_rights e WHERE(e.catalog_id,e.instrument_id,e.physical_slot)=(p.catalog_id,p.binding_id,m.source_slot))
 THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 IF g.billing_profile='gas_final_daily_fixed_cash_v1' AND(m.share_n<>m.share_d OR m.part_kind IS NOT NULL OR
  NOT EXISTS(SELECT 1 FROM trading.gas_component_terms s WHERE(s.catalog_id,s.instrument_id,s.slot,s.delivery_from,s.delivery_to)=(m.catalog_id,m.instrument_id,m.source_slot,m.delivery_from,m.delivery_to)))
 THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.gas_valuation_assignments a JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,c.quote_code)
  JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=(q.catalog_id,q.revision_id,q.currency_code)
  WHERE(a.catalog_id,a.revision_id,a.code,currency.display_minor_units)=(m.catalog_id,m.definition_revision_id,m.assignment_code,g.decimal_places))
 THEN RAISE EXCEPTION 'gas_billing_rounding';END IF;
 SELECT (x.target).* INTO contract FROM security.resolve_facility_gas_targets(p.catalog_id,ARRAY[o.id],ARRAY[g.day],g.evaluation_at)x;
 IF contract.trade_id IS DISTINCT FROM p.trade_id OR contract.native_to IS NULL OR contract.native_to>g.evaluation_at THEN RAISE EXCEPTION 'gas_billing_interval';END IF;
END$$;
ALTER FUNCTION automation.require_lifecycle_gas_source(automation.lifecycle_profiles,automation.lifecycle_gas_sources) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_lifecycle_gas_source(automation.lifecycle_profiles,automation.lifecycle_gas_sources) FROM PUBLIC;
ALTER TABLE automation.lifecycle_profiles DROP CONSTRAINT lifecycle_profiles_check;
ALTER TABLE automation.lifecycle_profiles ADD CONSTRAINT lifecycle_profiles_check CHECK ((gas_revision IS NULL AND close_id IS NOT NULL AND (((handler = 'futures'::text) AND (action = 'futures.margin'::text) AND (future_revision IS NOT NULL) AND (margin_policy_id IS NOT NULL) AND (margin_policy_revision IS NOT NULL) AND (option_revision IS NULL) AND (swap_revision IS NULL) AND (option_profile_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL)) OR ((handler = 'listed_option'::text) AND (action = 'option.expiry'::text) AND (option_revision IS NOT NULL) AND (option_profile_revision IS NOT NULL) AND (margin_policy_id IS NOT NULL) AND (margin_policy_revision IS NOT NULL) AND (future_revision IS NULL) AND (swap_revision IS NULL) AND (swap_profile_id IS NULL) AND (swap_profile_revision IS NULL)) OR ((handler = 'financial_swap'::text) AND (action = 'swap.determine'::text) AND (swap_revision IS NOT NULL) AND (swap_profile_id IS NOT NULL) AND (swap_profile_revision IS NOT NULL) AND (future_revision IS NULL) AND (option_revision IS NULL) AND (option_profile_revision IS NULL) AND (margin_policy_id IS NULL) AND (margin_policy_revision IS NULL)))) OR (handler='gas_billing' AND action='gas.bill_daily' AND gas_revision IS NOT NULL AND close_id IS NULL AND future_revision IS NULL AND option_revision IS NULL AND swap_revision IS NULL AND option_profile_revision IS NULL AND swap_profile_id IS NULL AND swap_profile_revision IS NULL AND margin_policy_id IS NULL AND margin_policy_revision IS NULL));

CREATE OR REPLACE FUNCTION automation.lifecycle_definition(p automation.lifecycle_profiles)
 RETURNS jsonb
 LANGUAGE sql
 STABLE SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
 SELECT jsonb_build_object('closeId',p.close_id,'target',jsonb_build_object('handler',p.handler,'scopeId',p.trade_id,'bindingId',p.binding_id),
  'action',p.action,'occurrenceId',p.occurrence_id,'policyId',p.policy_id,'policyRevision',p.policy_revision,'knowledgePolicy',p.knowledge_policy,'settingsPolicy',p.settings_policy,'inputPolicy',p.input_policy,
  'settings',jsonb_build_object('tradeRevision',CASE p.handler WHEN 'futures' THEN p.future_revision WHEN 'listed_option' THEN p.option_revision WHEN 'financial_swap' THEN p.swap_revision WHEN 'gas_billing' THEN p.gas_revision END,
   'optionProfileRevision',p.option_profile_revision,'swapProfileId',p.swap_profile_id,'swapProfileRevision',p.swap_profile_revision,'marginPolicyId',p.margin_policy_id,'marginPolicyRevision',p.margin_policy_revision)||CASE WHEN p.handler='gas_billing' THEN jsonb_build_object('gasSource',automation.gas_billing_source(p.id)) ELSE '{}'::jsonb END);
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

CREATE OR REPLACE FUNCTION automation.create_lifecycle_job(catalog uuid, request jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE gas automation.lifecycle_gas_sources;g jsonb=request#>'{definition,settings,gasSource}';processing_at timestamptz;c security.request_contexts;p automation.lifecycle_profiles;j automation.jobs;anchor market_data.market_closes;
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
  IF(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND(state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=jobs.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
  INSERT INTO automation.lifecycle_profiles SELECT(p).*;
  IF p.handler='gas_billing' THEN
   INSERT INTO automation.lifecycle_gas_sources SELECT(gas).*;
   PERFORM automation.require_lifecycle_settings(p);
  END IF;
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
END$function$
;

CREATE OR REPLACE FUNCTION automation.admit_occurrence_inputs()
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE r automation.runs;j automation.jobs;catalog uuid;day date;policy text;source security.operation_read_coordinates;
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
  IF day IS NULL THEN RAISE EXCEPTION 'occurrence_inputs_required';END IF;
 ELSE RAISE EXCEPTION 'occurrence_inputs_required' USING ERRCODE='P0001';END IF;
 IF r.input_catalog_id IS NULL THEN
  PERFORM security.resolve_operation_context(catalog,day,NULL);
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
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;anchor market_data.market_closes;gas automation.lifecycle_gas_sources;token uuid;source uuid;mode text;BEGIN
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 PERFORM automation.require_lifecycle_settings(p);
 SELECT q.id INTO token FROM security.request_contexts q WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO anchor FROM market_data.market_closes WHERE(catalog_id,id)=(p.catalog_id,p.close_id);
 SELECT * INTO gas FROM automation.lifecycle_gas_sources WHERE profile_id=p.id;
 source=(automation.occurrence_read_inputs()).id;
 SELECT a.mode INTO mode FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision)=(p.catalog_id,p.policy_id,p.policy_revision);
 PERFORM automation.lock_lifecycle_run();
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

COMMIT;
