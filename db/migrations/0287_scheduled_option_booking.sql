-- A service may book only the causal future of its exact admitted option-expiry
-- task. Ordinary human booking and existing command first outcomes are retained.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE expected record;p pg_proc;BEGIN
 FOR expected IN SELECT * FROM(VALUES
  ('security.stamp_trade_booking(uuid)','ee666ffd6a7aa41e5aa2fe628983fc74','timestamp with time zone',false,ARRAY['catalog']),
  ('security.record_trade_booking()','a3df2aaa549ef24cec6726877c45f4c3','trigger',false,NULL::text[]),
  ('lifecycle.execute_option_action(uuid,jsonb)','870e92f8454b980a3f826be80fab88b8','jsonb',true,ARRAY['catalog','body']),
  ('lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)','3841eda90b5bd3bc9a9e840dd6432511','jsonb',false,ARRAY['catalog','body']))v(signature,body,result,runtime,argument_names) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(expected.signature);
  IF NOT FOUND OR md5(p.prosrc)<>expected.body OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>to_regtype(expected.result)
   OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
   OR p.proargnames IS DISTINCT FROM expected.argument_names OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proacl::text IS DISTINCT FROM(CASE WHEN expected.runtime THEN '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' ELSE '{openetrm_security_owner=X/openetrm_security_owner}' END)
  THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected helper %',expected.signature;END IF;
 END LOOP;
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='trading.trades'::regclass AND tgname='record_booking'
  AND tgfoid='security.record_trade_booking()'::regprocedure AND tgtype=5 AND tgenabled='O' AND NOT tgisinternal
  AND tgconstraint=0 AND NOT tgdeferrable AND NOT tginitdeferred AND tgnargs=0 AND tgargs=''::bytea AND tgqual IS NULL)
 THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected helper booking trigger';END IF;
 IF(SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.booking_facts'::regclass AND conname='booking_facts_implementation_check')
  IS DISTINCT FROM $check$CHECK ((implementation = ANY (ARRAY['server_booking_utc_v1'::text, 'retained_commit_utc_v1'::text])))$check$
 THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected booking implementation constraint';END IF;
END$guard$;
ALTER TABLE trading.booking_facts DROP CONSTRAINT booking_facts_implementation_check;
ALTER TABLE trading.booking_facts ADD CONSTRAINT booking_facts_implementation_check CHECK(implementation IN('server_booking_utc_v1','retained_commit_utc_v1','scheduled_option_future_booking_v1'));
CREATE TABLE security.scheduled_option_booking_permits(
 context_id uuid PRIMARY KEY REFERENCES security.request_contexts(id),run_id uuid NOT NULL REFERENCES automation.runs(id),catalog_id uuid NOT NULL,
 option_trade_id uuid NOT NULL,option_revision integer NOT NULL,book_id uuid NOT NULL,expected_operation uuid NOT NULL,
 effective_at ref_data.instant NOT NULL,request jsonb NOT NULL,authority_until ref_data.instant NOT NULL,
 FOREIGN KEY(catalog_id,option_trade_id,option_revision) REFERENCES trading.futures_option_trade_revisions(catalog_id,trade_id,revision),
 CHECK(jsonb_typeof(request)='object')
);
ALTER TABLE security.scheduled_option_booking_permits OWNER TO openetrm_security_owner;
ALTER TABLE security.scheduled_option_booking_permits ENABLE ROW LEVEL SECURITY;
ALTER TABLE security.scheduled_option_booking_permits FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON security.scheduled_option_booking_permits TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TABLE trading.scheduled_option_bookings(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,run_id uuid NOT NULL REFERENCES automation.runs(id),job_id uuid NOT NULL REFERENCES automation.jobs(id),
 execution_context_id uuid NOT NULL REFERENCES automation.lifecycle_sources(context_id),owner_id uuid NOT NULL REFERENCES security.principals(id),
 service_id uuid NOT NULL REFERENCES security.principals(id),option_trade_id uuid NOT NULL,option_revision integer NOT NULL,event_id uuid NOT NULL,
 request jsonb NOT NULL,authority_until ref_data.instant NOT NULL,recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(run_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.booking_facts(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,event_id) REFERENCES trading.futures_option_events(catalog_id,id),
 FOREIGN KEY(catalog_id,option_trade_id,option_revision) REFERENCES trading.futures_option_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,run_id) REFERENCES lifecycle.commands(catalog_id,operation_id)
);
ALTER TABLE trading.scheduled_option_bookings OWNER TO openetrm_security_owner;
ALTER TABLE trading.scheduled_option_bookings ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.scheduled_option_bookings FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON trading.scheduled_option_bookings TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.scheduled_option_bookings FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION lifecycle.require_option_booking_permit(catalog uuid) RETURNS security.scheduled_option_booking_permits
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE q security.request_contexts;p security.scheduled_option_booking_permits;r automation.runs;j automation.jobs;profile automation.lifecycle_profiles;BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE runtime_login=session_user AND backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO p FROM security.scheduled_option_booking_permits WHERE context_id=q.id;
 IF p.context_id IS NULL OR p.catalog_id IS DISTINCT FROM catalog OR p.authority_until<=clock_timestamp()
  OR NOT EXISTS(SELECT 1 FROM security.principals WHERE id=q.actor_id AND kind='service' AND enabled)
 THEN RAISE EXCEPTION 'booking_human_required';END IF;
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO profile FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 IF(r.id,profile.catalog_id,profile.handler,profile.action,profile.trade_id,profile.option_revision)
  IS DISTINCT FROM(p.run_id,p.catalog_id,'listed_option','option.expiry',p.option_trade_id,p.option_revision)
  OR p.expected_operation IS DISTINCT FROM security.futures_option_event_operation(r.id)
 THEN RAISE EXCEPTION 'scheduled_option_booking_scope' USING ERRCODE='42501';END IF;
 PERFORM automation.require_lifecycle_action_scope(catalog,'option.expiry',p.request,true);
 PERFORM lifecycle.require_authority(catalog,'BookTrade',p.authority_until);
 RETURN p;
END$function$;
CREATE FUNCTION trading.seal_scheduled_option_booking() RETURNS trigger
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE r automation.runs;j automation.jobs;p automation.lifecycle_profiles;q security.request_contexts;e trading.futures_option_events;f trading.trade_revisions;facts trading.booking_facts;source automation.lifecycle_sources;selected lifecycle.option_profiles;BEGIN
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 SELECT * INTO q FROM security.request_contexts WHERE runtime_login=session_user AND backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=q.id;
 PERFORM automation.require_lifecycle_action_scope(NEW.catalog_id,'option.expiry',NEW.request,true);
 PERFORM lifecycle.require_authority(NEW.catalog_id,'BookTrade',NEW.authority_until);
 SELECT * INTO e FROM trading.futures_option_events WHERE(catalog_id,id)=(NEW.catalog_id,NEW.event_id);
 SELECT * INTO f FROM trading.trade_revisions WHERE(catalog_id,trade_id,revision)=(NEW.catalog_id,NEW.trade_id,1);
 SELECT * INTO facts FROM trading.booking_facts WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
 SELECT * INTO selected FROM lifecycle.option_profiles WHERE(catalog_id,trade_id,action,revision)=(p.catalog_id,p.trade_id,p.action,p.option_profile_revision);
 IF(r.id,j.id,q.id,j.owner_id,q.actor_id,p.trade_id,p.option_revision,p.catalog_id)
  IS DISTINCT FROM(NEW.run_id,NEW.job_id,NEW.execution_context_id,NEW.owner_id,NEW.service_id,NEW.option_trade_id,NEW.option_revision,NEW.catalog_id)
  OR source.context_id IS NULL OR e.id IS NULL OR f.trade_id IS NULL OR facts.trade_id IS NULL
  OR(e.request_id,e.trade_id,e.trade_revision,e.kind,e.futures_trade_id,e.actor_id) IS DISTINCT FROM(r.id,p.trade_id,p.option_revision,'expiry',NEW.trade_id,q.actor_id)
  OR(f.actor_id,f.operation_id,f.publication_order,f.effective_at) IS DISTINCT FROM(q.actor_id,security.futures_option_event_operation(r.id),e.publication_order,e.business_at)
  OR(facts.booker_id,facts.implementation,facts.publication_order) IS DISTINCT FROM(q.actor_id,'scheduled_option_future_booking_v1',e.publication_order)
  OR NOT EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a WHERE(a.catalog_id,a.trade_id,a.binding_id,a.policy_id,a.policy_revision,a.publication_order)=
    (NEW.catalog_id,NEW.trade_id,f.binding_id,selected.margin_policy_id,selected.margin_policy_revision,e.publication_order))
  OR NOT EXISTS(SELECT 1 FROM lifecycle.commands c WHERE(c.catalog_id,c.operation_id,c.actor_id,c.kind,c.request)=(NEW.catalog_id,NEW.run_id,NEW.service_id,'option.expiry',NEW.request))
 THEN RAISE EXCEPTION 'scheduled_option_booking_scope' USING ERRCODE='42501';END IF;
 RETURN NEW;
END$function$;
CREATE CONSTRAINT TRIGGER scheduled_option_booking_complete AFTER INSERT ON trading.scheduled_option_bookings DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.seal_scheduled_option_booking();

-- Exact guarded patches preserve both trigger/public function identities and all
-- ordinary human paths. Only the private, active causal permit admits a service.
DO $patch$DECLARE definition text;before text;after text;BEGIN
 definition=pg_get_functiondef('security.stamp_trade_booking(uuid)'::regprocedure);
 before=$old$ IF security.booking_actor(catalog)->>'kind'<>'human' THEN RAISE EXCEPTION 'booking_human_required' USING ERRCODE='P0001';END IF;$old$;
 after=$new$ IF security.booking_actor(catalog)->>'kind'<>'human' THEN PERFORM lifecycle.require_option_booking_permit(catalog);END IF;$new$;
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected stamp body';END IF;
 EXECUTE replace(definition,before,after);
 definition=pg_get_functiondef('security.record_trade_booking()'::regprocedure);
 before=$old$ IF NOT EXISTS(SELECT 1 FROM security.principals WHERE id=NEW.actor_id AND kind='human') THEN RAISE EXCEPTION 'booking_human_required' USING ERRCODE='P0001';END IF;$old$;
 after=$new$ IF NOT EXISTS(SELECT 1 FROM security.principals WHERE id=NEW.actor_id AND kind='human') THEN
  PERFORM lifecycle.require_option_booking_permit(NEW.catalog_id);
  IF NOT EXISTS(SELECT 1 FROM security.scheduled_option_booking_permits permit JOIN security.request_contexts context ON context.id=permit.context_id
   WHERE context.runtime_login=session_user AND context.backend_pid=pg_backend_pid() AND context.transaction_id=pg_current_xact_id_if_assigned()
   AND(permit.catalog_id,permit.expected_operation,permit.book_id,permit.effective_at)=(NEW.catalog_id,NEW.operation_id,NEW.book_id,NEW.effective_at)
   AND NEW.request->'optionEvent'=permit.request AND(NEW.request->>'optionEventOperation')::uuid=permit.run_id)
  THEN RAISE EXCEPTION 'scheduled_option_booking_scope' USING ERRCODE='42501';END IF;
 END IF;$new$;
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected record body';END IF;
 definition=replace(definition,before,after);
 before=$old$'server_booking_utc_v1',NEW.publication_order$old$;
 after=$new$CASE WHEN EXISTS(SELECT 1 FROM security.principals WHERE id=NEW.actor_id AND kind='human') THEN 'server_booking_utc_v1' ELSE 'scheduled_option_future_booking_v1' END,NEW.publication_order$new$;
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected record implementation';END IF;
 EXECUTE replace(definition,before,after);
 definition=pg_get_functiondef('lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)'::regprocedure);
 before=$old$result=security.futures_option_event_outcome(e);$old$;
 after=$new$result=security.futures_option_event_outcome(e)||jsonb_build_object('operationId',operation,'acknowledgedOperationId',e.request_id,'semanticAcknowledgement',true);$new$;
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'scheduled_option_booking_upgrade: unexpected acknowledgement body';END IF;
 EXECUTE replace(definition,before,after);
END$patch$;
CREATE OR REPLACE FUNCTION lifecycle.execute_option_action(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz=security.authority_until('BookTrade');q security.request_contexts;r automation.runs;j automation.jobs;p automation.lifecycle_profiles;
 original trading.futures_option_trade_revisions;contract market_data.contracts;result jsonb;e trading.futures_option_events;previous trading.scheduled_option_bookings;BEGIN
 PERFORM automation.require_lifecycle_action_scope(catalog,body->>'action',body,true);
 SELECT * INTO q FROM security.request_contexts WHERE runtime_login=session_user AND backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned();
 IF NOT EXISTS(SELECT 1 FROM security.principals WHERE id=q.actor_id AND kind='service') THEN RETURN lifecycle.execute_option_action_pre_schedule_scope(catalog,body);END IF;
 r=automation.lock_lifecycle_run();SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;SELECT * INTO p FROM automation.lifecycle_profiles WHERE id=j.lifecycle_profile_id;
 IF p.handler IS DISTINCT FROM 'listed_option' OR p.action IS DISTINCT FROM 'option.expiry' OR p.catalog_id IS DISTINCT FROM catalog
  OR until_at IS NULL OR NOT isfinite(until_at) OR until_at<=clock_timestamp() OR EXISTS(SELECT 1 FROM security.scheduled_option_booking_permits WHERE context_id=q.id)
 THEN RAISE EXCEPTION 'scheduled_option_booking_scope' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT original FROM trading.futures_option_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,p.trade_id,p.option_revision);
 SELECT * INTO STRICT contract FROM market_data.contracts WHERE(catalog_id,revision_id,id)=(catalog,original.definition_revision_id,original.contract_id);
 INSERT INTO security.scheduled_option_booking_permits VALUES(q.id,r.id,catalog,p.trade_id,p.option_revision,original.book_id,security.futures_option_event_operation(r.id),contract.expiry_at,body,until_at);
 result=lifecycle.execute_option_action_pre_schedule_scope(catalog,body);
 SELECT * INTO STRICT e FROM trading.futures_option_events WHERE(catalog_id,id)=(catalog,(result->>'eventId')::uuid);
 IF e.request_id=r.id AND e.futures_trade_id IS NOT NULL THEN
  SELECT * INTO previous FROM trading.scheduled_option_bookings WHERE(catalog_id,trade_id)=(catalog,e.futures_trade_id);
  IF previous.trade_id IS NULL THEN
   INSERT INTO trading.scheduled_option_bookings VALUES(catalog,e.futures_trade_id,r.id,j.id,q.id,j.owner_id,q.actor_id,p.trade_id,p.option_revision,e.id,body,until_at,clock_timestamp());
  ELSIF(previous.run_id,previous.event_id,previous.request) IS DISTINCT FROM(r.id,e.id,body) THEN RAISE EXCEPTION 'scheduled_option_booking_scope' USING ERRCODE='42501';END IF;
 END IF;
 DELETE FROM security.scheduled_option_booking_permits WHERE context_id=q.id;
 RETURN result;
END$function$;
DO $functions$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['lifecycle.require_option_booking_permit(uuid)'::regprocedure,'trading.seal_scheduled_option_booking()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker,openetrm_scheduler',f);
 END LOOP;
END$functions$;
COMMIT;
