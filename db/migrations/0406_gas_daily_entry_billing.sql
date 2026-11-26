-- Fixed daily entry billing over retained shares, quantity parts and native days.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE p pg_proc;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='security.record_gas_billing(uuid,jsonb,timestamptz,uuid,jsonb)'::regprocedure;
 IF encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>'76f92840ce8f226ef029fbfed04fe2f73dbfd8ad58a2db4bd661d2771d4f39e9'
  OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_billing_entry_upgrade: unexpected helper %',p.oid::regprocedure;END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.gas_cash_determinations'::regclass
  AND conname='gas_cash_determinations_profile_check' AND pg_get_constraintdef(oid)='CHECK ((profile = ''gas_final_daily_fixed_cash_v1''::text))')
 THEN RAISE EXCEPTION 'gas_billing_entry_upgrade: unexpected profile constraint';END IF;
END$guard$;
ALTER TABLE trading.gas_cash_determinations DROP CONSTRAINT gas_cash_determinations_profile_check;
ALTER TABLE trading.gas_cash_determinations ADD CONSTRAINT gas_cash_determinations_profile_check
 CHECK(profile IN('gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1'));

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
 p#>>'{context,convention}' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR p#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR p#>>'{context,contextId}' IS NOT NULL OR
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
END$function$;
COMMIT;
