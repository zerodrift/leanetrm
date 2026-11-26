-- One operating-clock billing producer, shared by reviewed and scheduled actions.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $guard$ DECLARE f record;p pg_proc;BEGIN
 FOR f IN SELECT * FROM(VALUES
 ('security.validate_gas_billing_request(jsonb)','0d76c6b9c96f873379ea00b24d1976d6f5f38676ec3360739a7de998fb90acfa',false),
 ('security.record_gas_billing(uuid,jsonb,timestamp with time zone,uuid,jsonb)','86b43e7204b2531645820eaa263b9a9e253bf33e0c1779f8968eb55c552e230b',true),
 ('lifecycle.finish_command_pre_schedule_scope(uuid,uuid,text,jsonb,text,timestamp with time zone)','15e730e9176af739d49f3012c6bba3f23b30a1495eef0c217c4c98b7dd21c183',true))v(signature,digest,secured) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=f.signature::regprocedure;
  IF encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>f.digest
   OR p.prosecdef IS DISTINCT FROM f.secured OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proowner<>(CASE WHEN f.secured THEN 'openetrm_security_owner'::regrole ELSE current_user::regrole END)
   OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
   OR EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.privilege_type<>'EXECUTE' OR a.is_grantable OR a.grantor<>p.proowner
    OR a.grantee NOT IN(p.proowner,'openetrm_security_owner'::regrole,CASE WHEN f.signature LIKE 'security.record_gas_billing(%' THEN 'openetrm_runtime'::regrole ELSE p.proowner END))
   OR (SELECT count(*) FROM aclexplode(p.proacl))<>(CASE WHEN f.signature LIKE 'lifecycle.finish_command_%' THEN 1 ELSE 2 END)
  THEN RAISE EXCEPTION 'gas_billing_lifecycle_upgrade: unexpected helper %',p.oid::regprocedure;END IF;
 END LOOP;
END$guard$;
INSERT INTO lifecycle.action_kinds VALUES('gas.bill_daily',true,'AmendTrade','AmendTrade');

CREATE OR REPLACE FUNCTION security.validate_gas_billing_request(body jsonb)
 RETURNS void
 LANGUAGE plpgsql
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE p jsonb=body->'preview';c jsonb=p->'context';due text=body->>'correctionDueAt';key text;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['preview','operationId','previewHash','sourceId','expectedPreviousId','correctionDueAt','reason','evidence']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('preview','operationId','previewHash','sourceId','expectedPreviousId','correctionDueAt','reason','evidence')) OR
 jsonb_typeof(p) IS DISTINCT FROM 'object' OR NOT(p?&ARRAY['context','tradeId','expectedRevision','cashSlot','day','selectionPolicy','profile','decimalPlaces','roundingPolicy']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(p) k WHERE k NOT IN('context','tradeId','expectedRevision','cashSlot','day','selectionPolicy','profile','decimalPlaces','roundingPolicy')) OR
 jsonb_typeof(c) IS DISTINCT FROM 'object' OR c->>'knowledgeMode' IS DISTINCT FROM 'current' OR NOT(
  c->>'convention'='physical_gas_fixed_quantity_v4' AND c?&ARRAY['snapshotId','convention','knowledgeMode']
   AND NOT EXISTS(SELECT 1 FROM jsonb_object_keys(c) k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId')) AND c->>'contextId' IS NULL
  OR c->>'convention'='gas_processing_instant_utc_v1' AND c?&ARRAY['convention','businessDate','evaluationAt','knowledgeMode','retained']
   AND NOT EXISTS(SELECT 1 FROM jsonb_object_keys(c) k WHERE k NOT IN('convention','businessDate','evaluationAt','knowledgeMode','retained')) AND c->'retained'='null'::jsonb
   AND c->>'businessDate'~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' AND(c->>'businessDate')::date::text=c->>'businessDate'
   AND c->>'evaluationAt'~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}|[.][0-9]{6}0)?(Z|[+]00:00)$'
   AND(c->>'evaluationAt')::timestamptz<timestamptz '9999-12-31T00:00:00Z'
   AND((c->>'evaluationAt')::timestamptz AT TIME ZONE 'UTC')::date=(c->>'businessDate')::date
 ) IS TRUE OR
 jsonb_typeof(p->'day') IS DISTINCT FROM 'string' OR p->>'day'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR
 (p->>'day')::date::text IS DISTINCT FROM p->>'day' OR
 due IS NOT NULL AND(due!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}|[.][0-9]{6}0)?(Z|[+]00:00)$' OR
 NOT isfinite(due::timestamptz) OR due::timestamptz>=timestamptz '9999-12-31T00:00:00Z') THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 FOREACH key IN ARRAY ARRAY['operationId','previewHash','sourceId','evidence'] LOOP
  IF jsonb_typeof(body->key) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['expectedPreviousId','correctionDueAt','reason'] LOOP
  IF jsonb_typeof(body->key) NOT IN('string','null') THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['cashSlot','expectedRevision','decimalPlaces'] LOOP
  IF jsonb_typeof(p->key) IS DISTINCT FROM 'number' THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 END LOOP;
END$function$
;
CREATE OR REPLACE FUNCTION security.record_gas_billing(catalog uuid, body jsonb, as_of timestamp with time zone, context uuid, calculation jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE p jsonb=body->'preview';trade uuid=(p->>'tradeId')::uuid;operation uuid=(body->>'operationId')::uuid;
 cash integer;flow_day date;policy text=p->>'selectionPolicy';places integer=(p->>'decimalPlaces')::integer;
 source uuid=(calculation->>'sourceId')::uuid;head trading.gas_trade_revisions;m trading.gas_component_terms;o trading.gas_delivery_obligations;
 a market_data.gas_valuation_assignments;quote ref_data.quote_specifications;prior trading.gas_cash_determinations;manual trading.gas_quantity_revisions;
 target trading.facility_adopted_targets;adoption trading.facility_quantity_adoptions;qty numeric;source_publication integer;source_effective timestamptz;
 finish timestamptz;valid_day boolean;due timestamptz;total numeric=(calculation->>'amount')::numeric;num numeric=(calculation->>'numerator')::numeric;den numeric=(calculation->>'denominator')::numeric;
 scaled numeric;whole numeric;expected numeric;quantity_n numeric;quantity_d numeric;cap_n numeric;cap_d numeric;remainder numeric;actor uuid;publication integer;identity uuid=gen_random_uuid();old jsonb;result jsonb;
BEGIN
 PERFORM security.validate_gas_billing_request(body);
 cash=(p->>'cashSlot')::integer;flow_day=(p->>'day')::date;
 old=security.replay_gas_billing(catalog,operation,body);IF old IS NOT NULL THEN RETURN old;END IF;
 IF as_of IS NULL OR NOT isfinite(as_of) OR context IS NULL OR
 (p->>'profile' IN('gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1')) IS NOT TRUE OR p->>'roundingPolicy' IS DISTINCT FROM 'currency_minor_units_to_even_v1' OR
 policy IS NULL OR policy NOT IN('final_allocated_billing_v1','final_facility_allocated_billing_v1') OR
 p#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR
 (p#>>'{context,convention}'='gas_processing_instant_utc_v1' AND(
  (p#>>'{context,evaluationAt}')::timestamptz IS DISTINCT FROM as_of OR(p#>>'{context,businessDate}')::date IS DISTINCT FROM(as_of AT TIME ZONE 'UTC')::date)) OR
 places IS NULL OR places NOT BETWEEN 0 AND 12 OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL OR
 source IS DISTINCT FROM(body->>'sourceId')::uuid OR source IS NULL OR num IS NULL OR den IS NULL OR den<=0 OR total IS NULL THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(p->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,head.instrument_id,cash);
 SELECT * INTO o FROM trading.gas_delivery_obligations WHERE(catalog_id,trade_id,instrument_id,slot)=(catalog,trade,head.instrument_id,m.source_slot);
 IF EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e
  WHERE(e.catalog_id,e.instrument_id)=(catalog,head.instrument_id) AND e.slot IN(m.slot,m.source_slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_members e WHERE(e.catalog_id,e.instrument_id,e.slot)=(catalog,head.instrument_id,m.slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_rights e WHERE(e.catalog_id,e.instrument_id,e.physical_slot)=(catalog,head.instrument_id,m.source_slot))
  THEN RAISE EXCEPTION 'gas_billing_rights';END IF;
 IF p->>'profile'='gas_final_daily_fixed_cash_v1' AND(m.share_n IS DISTINCT FROM m.share_d OR m.part_kind IS NOT NULL OR
  NOT EXISTS(SELECT 1 FROM trading.gas_component_terms s WHERE(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.source_slot)
   AND(s.delivery_from,s.delivery_to)=(m.delivery_from,m.delivery_to))) THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 IF flow_day<m.delivery_from OR flow_day>=m.delivery_to THEN RAISE EXCEPTION 'gas_billing_interval';END IF;
 IF m.role NOT IN('consideration','fee') OR m.role IS NULL OR m.stage IS DISTINCT FROM 'delivery' OR o.id IS NULL THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 SELECT true,g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond' INTO valid_day,finish
 FROM trading.instrument_side_schedules s JOIN trading.side_schedule_grids g USING(catalog_id,schedule_id)
 WHERE(s.catalog_id,s.instrument_id,s.slot)=(catalog,head.instrument_id,o.slot) AND g.operating_days=ARRAY[flow_day-date '0001-01-01'] AND cardinality(g.selection)=1;
 IF valid_day IS DISTINCT FROM true OR finish>as_of THEN RAISE EXCEPTION 'gas_billing_interval';END IF;
 IF policy='final_allocated_billing_v1' THEN
  SELECT r.* INTO manual FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
  WHERE(s.catalog_id,s.obligation_id,s.kind,s.source,s.stage,r.day)=(catalog,o.id,'allocated','manual_daily_v1','delivery',flow_day) ORDER BY r.revision DESC LIMIT 1;
  IF manual.id IS DISTINCT FROM source OR manual.status IS DISTINCT FROM 'final' THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  qty=manual.quantity;source_publication=manual.publication_order;source_effective=manual.effective_at;
 ELSE
  SELECT h.* INTO target FROM trading.facility_adopted_targets h JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(h.catalog_id,h.ledger_id)
  WHERE(h.catalog_id,h.obligation_id,g.day)=(catalog,o.id,flow_day) ORDER BY h.revision DESC LIMIT 1;
  IF target.id IS DISTINCT FROM source OR target.state IS DISTINCT FROM 'final' THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  SELECT * INTO STRICT adoption FROM trading.facility_quantity_adoptions WHERE(catalog_id,id)=(catalog,target.adoption_id);
  SELECT quantity INTO STRICT qty FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(catalog,target.plan_id,o.id);
  source_publication=adoption.publication_order;source_effective=adoption.effective_at;
 END IF;
 IF source_effective>as_of OR NOT EXISTS(SELECT 1 FROM security.operation_knowledge t JOIN security.operation_trading x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,source_publication)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date) AND source_publication<=x.known_through AND k.known_from<=t.known_at) THEN RAISE EXCEPTION 'gas_billing_context';END IF;
 SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,m.definition_revision_id,m.assignment_code);
 SELECT q.* INTO STRICT quote FROM ref_data.quote_specifications q JOIN market_data.curves c ON(c.reference_catalog_id,c.reference_revision_id,c.quote_code)=(q.catalog_id,q.revision_id,q.code)
 WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,m.definition_revision_id,a.curve_code);
 IF NOT EXISTS(SELECT 1 FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id,c.code,c.display_minor_units)=(quote.catalog_id,quote.revision_id,quote.currency_code,places)) THEN RAISE EXCEPTION 'gas_billing_rounding';END IF;
 -- Independent rational integrity oracle. Levels use the held trade basis;
 -- final quantity already includes the physical membership and must not be rescaled.
 quantity_n=qty;quantity_d=1;
 IF p->>'profile'='gas_final_daily_entry_cash_v1' THEN
  IF m.part_kind IS NOT NULL THEN
   quantity_n=greatest(0,CASE m.part_kind WHEN 'band' THEN qty*m.part_lower_d-m.part_lower_n*head.quantity
    WHEN 'shortfall' THEN m.part_lower_n*head.quantity-qty*m.part_lower_d END);
   quantity_d=m.part_lower_d;
   IF m.part_upper_n IS NOT NULL THEN
    cap_n=(m.part_upper_n*m.part_lower_d-m.part_lower_n*m.part_upper_d)*head.quantity;
    cap_d=m.part_upper_d*m.part_lower_d;
    IF quantity_n*cap_d>cap_n*quantity_d THEN quantity_n=cap_n;quantity_d=cap_d;END IF;
   END IF;
  END IF;
  quantity_n=quantity_n*m.share_n;quantity_d=quantity_d*m.share_d;
 END IF;
 IF num*quantity_d<>quantity_n*m.price*quote.multiplier*m.direction*den THEN RAISE EXCEPTION 'gas_billing_amount';END IF;
 -- Divide only after cross-product equality, retaining the exact remainder for ties.
 scaled=abs(num)*power(10::numeric,places);whole=div(scaled,den);remainder=mod(scaled,den);
 expected=sign(num)*(whole+CASE WHEN remainder*2>den OR(remainder*2=den AND mod(whole,2)=1) THEN 1 ELSE 0 END)/power(10::numeric,places);
 IF total<>expected THEN RAISE EXCEPTION 'gas_billing_amount';END IF;
 SELECT * INTO prior FROM trading.gas_cash_determinations WHERE(catalog_id,trade_id,cash_slot,day)=(catalog,trade,cash,flow_day) ORDER BY ordinal DESC LIMIT 1;
 IF prior.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR prior.effective_at>as_of THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 IF prior.id IS NULL THEN
  IF body->>'reason' IS NOT NULL OR body->>'correctionDueAt' IS NOT NULL THEN RAISE EXCEPTION 'gas_billing_correction';END IF;due=m.payment_at;
 ELSE
  IF (prior.selection_policy,prior.rounding_policy,prior.decimal_places,prior.instrument_id) IS DISTINCT FROM(policy,p->>'roundingPolicy',places,head.instrument_id) OR
   body->>'reason' IS NULL OR body->>'reason' NOT IN('late_actuals','quantity_correction','allocation_correction') OR body->>'correctionDueAt' IS NULL THEN RAISE EXCEPTION 'gas_billing_correction';END IF;
  due=(body->>'correctionDueAt')::timestamptz;
  IF NOT isfinite(due) THEN RAISE EXCEPTION 'gas_billing_correction';END IF;
 END IF;
 IF(SELECT count(*) FROM trading.gas_billing_commands WHERE(catalog_id,trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_billing_limit';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.gas_cash_determinations VALUES(catalog,identity,trade,head.revision,head.instrument_id,cash,o.id,flow_day,coalesce(prior.ordinal,0)+1,prior.id,
 CASE WHEN policy='final_allocated_billing_v1' THEN source END,CASE WHEN policy='final_facility_allocated_billing_v1' THEN source END,
 p->>'profile',policy,p->>'roundingPolicy',places,quote.catalog_id,quote.revision_id,quote.currency_code,num,den,total,total-coalesce(prior.rounded_total,0),due,body->>'reason',btrim(body->>'evidence'),actor,context,publication,as_of);
 result=jsonb_build_object('operationId',operation,'tradeId',trade,'determinationId',identity,'previousId',prior.id,'amount',trim_scale(total)::text,'contribution',trim_scale(total-coalesce(prior.rounded_total,0))::text);
 INSERT INTO trading.gas_billing_commands VALUES(catalog,operation,trade,identity,actor,body,result);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);RETURN result;
END$function$
;
CREATE OR REPLACE FUNCTION lifecycle.finish_command_pre_schedule_scope(catalog uuid, operation uuid, kind text, body jsonb, permission text, until_at timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE actor uuid;c lifecycle.commands;p lifecycle.action_policies;BEGIN
 IF kind='gas.bill_daily' THEN
  IF permission IS DISTINCT FROM 'AmendTrade' THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
  actor=lifecycle.require_authority(catalog,permission,until_at);
  SELECT * INTO c FROM lifecycle.commands t WHERE(t.catalog_id,t.operation_id)=(catalog,operation);
  IF c.operation_id IS NULL OR(c.actor_id,c.kind,c.request) IS DISTINCT FROM(actor,kind,body) THEN RAISE EXCEPTION 'lifecycle_command_conflict';END IF;
  SELECT * INTO p FROM lifecycle.action_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,c.policy_id,c.policy_revision);
  IF p.id IS NULL OR p.action IS DISTINCT FROM kind OR(body->>'automatic')::boolean AND(p.mode<>'automatic' OR security.has_permission('RunJobs') IS NOT TRUE)
   THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
  IF security.gas_billing_status(catalog,operation,lifecycle.gas_billing_native_request(body)) IS DISTINCT FROM c.outcome
   THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;
  PERFORM lifecycle.require_authority(catalog,permission,until_at);RETURN;
 END IF;
 IF kind NOT IN('swap.net','swap_net_profile') THEN PERFORM lifecycle.finish_command_pre_net(catalog,operation,kind,body,permission,until_at);RETURN;END IF;
 IF permission IS DISTINCT FROM 'AmendTrade' THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 actor=lifecycle.require_authority(catalog,permission,until_at);
 SELECT * INTO c FROM lifecycle.commands t WHERE(t.catalog_id,t.operation_id)=(catalog,operation);
 IF c.operation_id IS NULL OR(c.actor_id,c.kind,c.request) IS DISTINCT FROM(actor,kind,body) THEN RAISE EXCEPTION 'lifecycle_command_conflict';END IF;
 SELECT * INTO p FROM lifecycle.action_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,c.policy_id,c.policy_revision);
 IF p.id IS NULL OR p.action IS DISTINCT FROM 'swap.net' OR p.mode NOT IN('manual','review') THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 IF kind='swap_net_profile' THEN
  PERFORM lifecycle.require_swap_net_reference(catalog,(body->>'tradeId')::uuid,(c.outcome->>'profileId')::uuid,(c.outcome->>'revision')::integer);
 ELSE
  PERFORM settlement_data.swap_command_status(catalog,operation,lifecycle.swap_net_native_request(body));
  PERFORM lifecycle.require_swap_net_reference(catalog,(body#>>'{target,scopeId}')::uuid,(body#>>'{selection,profileId}')::uuid,(body#>>'{selection,profileRevision}')::integer);
 END IF;
 PERFORM lifecycle.require_authority(catalog,permission,until_at);
END$function$
;

CREATE FUNCTION lifecycle.gas_billing_native_request(body jsonb) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('preview',body#>'{selection,billing}'||jsonb_build_object('context',jsonb_build_object(
  'convention',body#>'{selection,processingConvention}','businessDate',body#>'{context,businessDate}',
  'evaluationAt',body#>'{context,asOf}','knowledgeMode','current','retained',NULL)),
  'operationId',body->'operationId','previewHash',body->'previewHash','sourceId',body#>'{selection,sourceId}',
  'expectedPreviousId',body#>'{selection,expectedPreviousId}','correctionDueAt',body#>'{selection,correctionDueAt}',
  'reason',body#>'{selection,reason}','evidence',body#>'{selection,evidence}')
$$;
ALTER FUNCTION lifecycle.gas_billing_native_request(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.gas_billing_native_request(jsonb) FROM PUBLIC;

CREATE FUNCTION lifecycle.require_gas_billing_original(catalog uuid,body jsonb,scheduled_id uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid=(body#>>'{target,scopeId}')::uuid;instrument uuid=(body#>>'{target,bindingId}')::uuid;
 cash integer=(body#>>'{selection,billing,cashSlot}')::integer;flow_day date=(body#>>'{selection,billing,day}')::date;
 at timestamptz=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');context uuid=(body#>>'{context,contextId}')::uuid;
 obligation uuid;scheduled trading.gas_quantity_revisions;allocated trading.gas_quantity_revisions;target trading.facility_adopted_targets;
 adoption trading.facility_quantity_adoptions;contract trading.facility_gas_target;final_quantity numeric;prefix integer;cut timestamptz;
 final_publication integer;final_effective timestamptz;BEGIN
 IF body#>'{selection,expectedPreviousId}' IS DISTINCT FROM 'null'::jsonb OR
 EXISTS(SELECT 1 FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id,d.cash_slot,d.day)=(catalog,trade,cash,flow_day))
 THEN RAISE EXCEPTION 'gas_billing_automatic_correction';END IF;
 SELECT o.id INTO obligation FROM trading.gas_component_terms m JOIN trading.gas_delivery_obligations o
  ON(o.catalog_id,o.instrument_id,o.slot)=(m.catalog_id,m.instrument_id,m.source_slot)
  WHERE(m.catalog_id,m.instrument_id,m.slot)=(catalog,instrument,cash) AND o.trade_id=trade;
 SELECT x.known_through,t.known_at INTO prefix,cut FROM security.operation_knowledge t JOIN security.operation_trading x
  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(at AT TIME ZONE 'UTC')::date);
 SELECT r.* INTO scheduled FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
  WHERE(s.catalog_id,s.obligation_id,s.kind,s.source,s.stage,r.day)=(catalog,obligation,'scheduled','manual_daily_v1','delivery',flow_day) ORDER BY r.revision DESC LIMIT 1;
 IF scheduled_id IS NULL OR scheduled.id IS DISTINCT FROM scheduled_id OR scheduled.status IS DISTINCT FROM 'provisional'
  OR scheduled.effective_at>at OR prefix IS NULL OR scheduled.publication_order>prefix
  OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,scheduled.publication_order) AND k.known_from<=cut)
 THEN RAISE EXCEPTION 'gas_billing_reconciliation';END IF;
 IF body#>>'{selection,billing,selectionPolicy}'='final_allocated_billing_v1' THEN
  SELECT r.* INTO allocated FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
   WHERE(s.catalog_id,s.obligation_id,s.kind,s.source,s.stage,r.day)=(catalog,obligation,'allocated','manual_daily_v1','delivery',flow_day) ORDER BY r.revision DESC LIMIT 1;
  IF allocated.id IS DISTINCT FROM(body#>>'{selection,sourceId}')::uuid OR allocated.status IS DISTINCT FROM 'final' THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  final_quantity=allocated.quantity;final_publication=allocated.publication_order;final_effective=allocated.effective_at;
 ELSIF body#>>'{selection,billing,selectionPolicy}'='final_facility_allocated_billing_v1' THEN
  SELECT h.* INTO target FROM trading.facility_adopted_targets h JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(h.catalog_id,h.ledger_id)
   WHERE(h.catalog_id,h.obligation_id,g.day)=(catalog,obligation,flow_day) ORDER BY h.revision DESC LIMIT 1;
  IF target.id IS DISTINCT FROM(body#>>'{selection,sourceId}')::uuid OR target.state IS DISTINCT FROM 'final' THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  SELECT * INTO STRICT adoption FROM trading.facility_quantity_adoptions WHERE(catalog_id,id)=(catalog,target.adoption_id);
  SELECT quantity INTO STRICT final_quantity FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(catalog,target.plan_id,obligation);
  final_publication=adoption.publication_order;final_effective=adoption.effective_at;
 ELSE RAISE EXCEPTION 'gas_billing_policy';END IF;
 SELECT (x.target).* INTO contract FROM security.resolve_facility_gas_targets(catalog,ARRAY[obligation],ARRAY[flow_day],at)x;
 IF contract.trade_id IS DISTINCT FROM trade OR contract.instrument_id IS DISTINCT FROM instrument OR contract.native_to>at OR
  contract.quantity_d IS NULL OR contract.quantity_d<=0 OR scheduled.quantity*contract.quantity_d IS DISTINCT FROM contract.quantity_n OR
  final_quantity IS DISTINCT FROM scheduled.quantity OR final_effective>at OR final_publication>prefix
  OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,final_publication) AND k.known_from<=cut)
 THEN RAISE EXCEPTION 'gas_billing_reconciliation';END IF;
END$$;
ALTER FUNCTION lifecycle.require_gas_billing_original(uuid,jsonb,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.require_gas_billing_original(uuid,jsonb,uuid) FROM PUBLIC;

CREATE FUNCTION lifecycle.validate_gas_billing_action(body jsonb) RETURNS jsonb
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE s jsonb=body->'selection';native_body jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'])
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'))
  OR jsonb_typeof(s) IS DISTINCT FROM 'object' OR NOT(s?&ARRAY['billing','processingConvention','scheduledPolicy','sourceId','expectedPreviousId','correctionDueAt','reason','evidence'])
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(s) k WHERE k NOT IN('billing','processingConvention','scheduledPolicy','sourceId','expectedPreviousId','correctionDueAt','reason','evidence'))
  OR jsonb_typeof(body->'automatic') IS DISTINCT FROM 'boolean' OR body->>'action' IS DISTINCT FROM 'gas.bill_daily'
  OR body#>>'{target,handler}' IS DISTINCT FROM 'gas_billing' OR s->>'processingConvention' IS DISTINCT FROM 'gas_processing_instant_utc_v1'
  OR s->>'scheduledPolicy' IS DISTINCT FROM 'manual_scheduled_quantity_v1'
  OR body#>>'{target,scopeId}' IS DISTINCT FROM s#>>'{billing,tradeId}'
  OR body->>'previewHash'!~'^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 IF jsonb_typeof(body->'context') IS DISTINCT FROM 'object' OR (body->'context')-ARRAY['contextId','asOf','businessDate']<>'{}'::jsonb
  OR NOT((body->'context')?&ARRAY['contextId','asOf','businessDate'])
  OR jsonb_typeof(body->'target') IS DISTINCT FROM 'object' OR (body->'target')-ARRAY['handler','scopeId','bindingId']<>'{}'::jsonb
  OR NOT((body->'target')?&ARRAY['handler','scopeId','bindingId'])
  OR coalesce((body#>>'{context,contextId}')::uuid,'00000000-0000-0000-0000-000000000000'::uuid)='00000000-0000-0000-0000-000000000000'::uuid
  OR coalesce((body#>>'{target,bindingId}')::uuid,'00000000-0000-0000-0000-000000000000'::uuid)='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(body->'policyRevision') IS DISTINCT FROM 'number' OR(body->>'policyRevision')::integer<1
 THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 native_body=lifecycle.gas_billing_native_request(body);
 PERFORM security.validate_gas_billing_request(native_body);RETURN native_body;
END$$;
ALTER FUNCTION lifecycle.validate_gas_billing_action(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.validate_gas_billing_action(jsonb) FROM PUBLIC;

CREATE FUNCTION lifecycle.replay_gas_billing(catalog uuid,body jsonb,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE native_body jsonb;operation uuid=(body->>'operationId')::uuid;actor uuid;prior jsonb;p lifecycle.action_policies;at timestamptz;BEGIN
 native_body=lifecycle.validate_gas_billing_action(body);
 IF until_at IS NULL OR NOT isfinite(until_at) THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 until_at=least(until_at,lifecycle.capture_action_authority_pre_schedule_scope(catalog,'gas.bill_daily',body));
 PERFORM automation.require_lifecycle_action_scope(catalog,'gas.bill_daily',body,true);
 actor=lifecycle.require_authority(catalog,'AmendTrade',until_at);
 prior=lifecycle.command_status(catalog,operation,'gas.bill_daily',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 -- Financial fact acknowledgements precede mutable sources, rights and heads.
 -- The selected retained policy still identifies the receipt; present authority
 -- and the original admission deadline govern its disclosure and commitment.
 prior=security.replay_gas_billing(catalog,operation,native_body);IF prior IS NULL THEN RETURN NULL;END IF;
 at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');
 SELECT * INTO p FROM lifecycle.action_policies a WHERE(a.catalog_id,a.id,a.revision,a.action)=
  (catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,'gas.bill_daily');
 IF p.id IS NULL OR at<p.from_at OR at>=p.until_at OR(body->>'automatic')::boolean AND(p.mode<>'automatic' OR security.has_permission('RunJobs') IS NOT TRUE)
 THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.id,d.instrument_id)=
  (catalog,(prior->>'determinationId')::uuid,(body#>>'{target,bindingId}')::uuid)) THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;
 INSERT INTO lifecycle.commands(catalog_id,operation_id,actor_id,kind,request,outcome,policy_id,policy_revision,authority_until)
 VALUES(catalog,operation,actor,'gas.bill_daily',body,prior,p.id,p.revision,until_at);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN prior;
END$$;
ALTER FUNCTION lifecycle.replay_gas_billing(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.replay_gas_billing(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION lifecycle.replay_gas_billing(uuid,jsonb,timestamptz) TO openetrm_runtime;

CREATE FUNCTION lifecycle.execute_gas_billing(catalog uuid,body jsonb,until_at timestamptz,calculation jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operation uuid=(body->>'operationId')::uuid;actor uuid;prior jsonb;native_body jsonb;outcome jsonb;p lifecycle.action_policies;
 at timestamptz;context uuid;head trading.gas_trade_revisions;s jsonb=body->'selection';BEGIN
 IF until_at IS NULL OR NOT isfinite(until_at) THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 until_at=least(until_at,lifecycle.capture_action_authority_pre_schedule_scope(catalog,'gas.bill_daily',body));
 PERFORM automation.require_lifecycle_action_scope(catalog,'gas.bill_daily',body,true);
 actor=lifecycle.require_authority(catalog,'AmendTrade',until_at);
 prior=lifecycle.replay_gas_billing(catalog,body,until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 native_body=lifecycle.validate_gas_billing_action(body);
 at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');context=(body#>>'{context,contextId}')::uuid;
 p=lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,'gas.bill_daily',at,(body->>'automatic')::boolean);
 IF(body->>'automatic')::boolean AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,(body#>>'{target,scopeId}')::uuid,false);
 SELECT * INTO head FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,(body#>>'{target,scopeId}')::uuid) ORDER BY r.revision DESC LIMIT 1;
 IF head.instrument_id IS DISTINCT FROM(body#>>'{target,bindingId}')::uuid THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 IF(body->>'automatic')::boolean THEN PERFORM lifecycle.require_gas_billing_original(catalog,body,(calculation->>'scheduledSourceId')::uuid);END IF;
 outcome=security.record_gas_billing(catalog,native_body,at,context,calculation);
 INSERT INTO lifecycle.commands(catalog_id,operation_id,actor_id,kind,request,outcome,policy_id,policy_revision,authority_until)
 VALUES(catalog,operation,actor,'gas.bill_daily',body,outcome,p.id,p.revision,until_at);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN outcome;
END$$;
ALTER FUNCTION lifecycle.execute_gas_billing(uuid,jsonb,timestamptz,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.execute_gas_billing(uuid,jsonb,timestamptz,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION lifecycle.execute_gas_billing(uuid,jsonb,timestamptz,jsonb) TO openetrm_runtime;

-- A scoped task cannot use the public billing producer to bypass its selected
-- lifecycle action or commit a financial result without the terminal receipt.
CREATE FUNCTION automation.require_gas_billing_action_receipt() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c lifecycle.commands;BEGIN
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  JOIN automation.runs r ON r.id=b.run_id JOIN automation.jobs j ON j.id=r.job_id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
   AND j.operation='lifecycle_action_v1') THEN RETURN NEW;END IF;
 SELECT * INTO c FROM lifecycle.commands x WHERE(x.catalog_id,x.operation_id)=(NEW.catalog_id,NEW.operation_id);
 IF c.kind IS DISTINCT FROM 'gas.bill_daily' OR c.actor_id IS DISTINCT FROM NEW.actor_id OR c.outcome IS DISTINCT FROM NEW.outcome
  OR lifecycle.gas_billing_native_request(c.request) IS DISTINCT FROM NEW.request THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;
 PERFORM automation.require_lifecycle_action_scope(c.catalog_id,c.kind,c.request,true);RETURN NEW;
END$$;
ALTER FUNCTION automation.require_gas_billing_action_receipt() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_gas_billing_action_receipt() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER gas_billing_lifecycle_receipt AFTER INSERT ON trading.gas_billing_commands
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION automation.require_gas_billing_action_receipt();
CREATE FUNCTION security.read_gas_billing_days(catalog uuid,trade uuid,instrument uuid,cash integer,context uuid,as_of timestamptz,
 from_day date,to_day date,after_day date,maximum integer) RETURNS TABLE(native_day date,finish timestamptz)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE m trading.gas_component_terms;until_at timestamptz;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF from_day IS NULL OR to_day IS NULL OR from_day>=to_day OR to_day-from_day>366 OR maximum IS NULL OR maximum NOT BETWEEN 1 AND 9
  OR context IS NULL OR as_of IS NULL THEN RAISE EXCEPTION 'gas_billing_scope';END IF;
 IF instrument IS DISTINCT FROM(SELECT r.instrument_id FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(r.catalog_id,r.publication_order,r.effective_at,context,as_of) ORDER BY r.revision DESC LIMIT 1) THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 -- Retained content is private. Reuse its certified owner/context admission;
 -- exposing a bounded day index does not grant direct access to program tables.
 PERFORM security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of);
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,cash);
 IF m.role NOT IN('consideration','fee','indexed_consideration') OR m.role IS NULL THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 RETURN QUERY SELECT g.operating_days[1]+date '0001-01-01',g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond'
  FROM trading.instrument_side_schedules s JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id)=(s.catalog_id,s.schedule_id)
  WHERE(s.catalog_id,s.instrument_id,s.slot)=(catalog,instrument,m.source_slot) AND cardinality(g.operating_days)=1 AND cardinality(g.selection)=1
   AND g.operating_days[1]+date '0001-01-01'>=greatest(from_day,m.delivery_from)
   AND g.operating_days[1]+date '0001-01-01'<least(to_day,m.delivery_to)
   AND(after_day IS NULL OR g.operating_days[1]+date '0001-01-01'>after_day)
  ORDER BY g.operating_days[1] LIMIT maximum;
 PERFORM lifecycle.require_read(catalog,until_at);
END$$;
ALTER FUNCTION security.read_gas_billing_days(uuid,uuid,uuid,integer,uuid,timestamptz,date,date,date,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_billing_days(uuid,uuid,uuid,integer,uuid,timestamptz,date,date,date,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_billing_days(uuid,uuid,uuid,integer,uuid,timestamptz,date,date,date,integer) TO openetrm_runtime;
CREATE FUNCTION lifecycle.seal_gas_billing_action() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM lifecycle.finish_command(NEW.catalog_id,NEW.operation_id,NEW.kind,NEW.request,'AmendTrade',NEW.authority_until);
 RETURN NEW;
END$$;
ALTER FUNCTION lifecycle.seal_gas_billing_action() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION lifecycle.seal_gas_billing_action() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER gas_billing_action_authority AFTER INSERT ON lifecycle.commands
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN(NEW.kind='gas.bill_daily') EXECUTE FUNCTION lifecycle.seal_gas_billing_action();
COMMIT;
