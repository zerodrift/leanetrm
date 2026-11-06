-- One final native gas day and independently signed fixed cash membership.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.gas_cash_determinations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,
 cash_slot integer NOT NULL,obligation_id uuid NOT NULL,day date NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 1000),previous_id uuid,
 manual_revision_id uuid,facility_target_id uuid,
 profile text NOT NULL CHECK(profile='gas_final_daily_fixed_cash_v1'),
 selection_policy text NOT NULL CHECK(selection_policy IN('final_allocated_billing_v1','final_facility_allocated_billing_v1')),
 rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 exact_numerator numeric NOT NULL,exact_denominator numeric NOT NULL CHECK(exact_denominator>0),
 rounded_total ref_data.exact_number NOT NULL,contribution ref_data.exact_number NOT NULL,due_at ref_data.instant NOT NULL,
 reason text CHECK(reason IN('late_actuals','quantity_correction','allocation_correction')),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),actor_id uuid NOT NULL REFERENCES security.principals(id),
 context_id uuid NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,cash_slot,day,ordinal),UNIQUE(catalog_id,trade_id,cash_slot,day,id),
 UNIQUE(catalog_id,trade_id,cash_slot,day,manual_revision_id),UNIQUE(catalog_id,trade_id,cash_slot,day,facility_target_id),
 CHECK((ordinal=1)=(previous_id IS NULL)),CHECK((previous_id IS NULL)=(reason IS NULL)),
 CHECK((selection_policy='final_allocated_billing_v1' AND manual_revision_id IS NOT NULL AND facility_target_id IS NULL) OR
       (selection_policy='final_facility_allocated_billing_v1' AND manual_revision_id IS NULL AND facility_target_id IS NOT NULL)),
 CHECK(scale(exact_numerator)=0 AND scale(exact_denominator)=0 AND greatest(abs(exact_numerator),exact_denominator)<1e120),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,cash_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id),
 FOREIGN KEY(catalog_id,manual_revision_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,facility_target_id) REFERENCES trading.facility_adopted_targets(catalog_id,id),
 FOREIGN KEY(catalog_id,trade_id,cash_slot,day,previous_id) REFERENCES trading.gas_cash_determinations(catalog_id,trade_id,cash_slot,day,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_billing_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,determination_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL CHECK(pg_column_size(request)<=16384),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,determination_id) REFERENCES trading.gas_cash_determinations(catalog_id,id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_cash_determinations','gas_billing_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE POLICY scoped_read ON trading.gas_cash_determinations FOR SELECT TO openetrm_runtime USING(true);
CREATE POLICY current_authority ON trading.gas_cash_determinations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(
 security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND
 security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(
 SELECT 1 FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id,o.trade_id,o.instrument_id)=(gas_cash_determinations.catalog_id,gas_cash_determinations.obligation_id,gas_cash_determinations.trade_id,gas_cash_determinations.instrument_id)));
GRANT SELECT ON trading.gas_cash_determinations TO openetrm_runtime;

-- Ingress is shared by direct runtime calls and the managed adapter. Reject relative
-- dates, timezone inference and sub-microsecond coercion before PostgreSQL casts.
CREATE FUNCTION security.validate_gas_billing_request(body jsonb) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';c jsonb=p->'context';due text=body->>'correctionDueAt';key text;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['preview','operationId','previewHash','sourceId','expectedPreviousId','correctionDueAt','reason','evidence']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('preview','operationId','previewHash','sourceId','expectedPreviousId','correctionDueAt','reason','evidence')) OR
 jsonb_typeof(p) IS DISTINCT FROM 'object' OR NOT(p?&ARRAY['context','tradeId','expectedRevision','cashSlot','day','selectionPolicy','profile','decimalPlaces','roundingPolicy']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(p) k WHERE k NOT IN('context','tradeId','expectedRevision','cashSlot','day','selectionPolicy','profile','decimalPlaces','roundingPolicy')) OR
 jsonb_typeof(c) IS DISTINCT FROM 'object' OR NOT(c?&ARRAY['snapshotId','convention','knowledgeMode']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(c) k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId')) OR
 c->>'convention' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR c->>'knowledgeMode' IS DISTINCT FROM 'current' OR c->>'contextId' IS NOT NULL OR
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
END$$;
REVOKE ALL ON FUNCTION security.validate_gas_billing_request(jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.validate_gas_billing_request(jsonb) TO openetrm_security_owner;

CREATE FUNCTION security.gas_billing_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.gas_billing_commands;actor uuid;BEGIN
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM trading.gas_billing_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
 IF prior.operation_id IS NULL THEN RETURN NULL;END IF;
 IF prior.actor_id IS DISTINCT FROM actor OR payload IS NOT NULL AND prior.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,prior.trade_id,false);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
 RETURN prior.outcome;
END$$;
-- Exact semantic retries get a receipt too; they never reactivate an older fact.
CREATE FUNCTION security.replay_gas_billing(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old jsonb;prior trading.gas_billing_commands;fact trading.gas_cash_determinations;actor uuid;p jsonb=payload->'preview';BEGIN
 PERFORM security.validate_gas_billing_request(payload);
 PERFORM security.require_gas_quantity_trade(catalog,(payload#>>'{preview,tradeId}')::uuid,true);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_quantity_trade(catalog,(payload#>>'{preview,tradeId}')::uuid,true);
 old=security.gas_billing_status(catalog,operation,payload);IF old IS NOT NULL THEN RETURN old;END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR operation IS DISTINCT FROM(payload->>'operationId')::uuid THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.gas_billing_commands c WHERE c.catalog_id=catalog AND c.actor_id=actor AND c.request-'operationId'=payload-'operationId' LIMIT 1;
 IF FOUND THEN
  old=prior.outcome||jsonb_build_object('operationId',operation);
 ELSE
  SELECT * INTO fact FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id,d.cash_slot,d.day)=(catalog,(p->>'tradeId')::uuid,(p->>'cashSlot')::integer,(p->>'day')::date)
   AND coalesce(d.manual_revision_id,d.facility_target_id)=(payload->>'sourceId')::uuid;
  IF NOT FOUND THEN RETURN NULL;END IF;
  IF(fact.trade_revision,fact.profile,fact.selection_policy,fact.rounding_policy,fact.decimal_places,fact.reason,fact.evidence) IS DISTINCT FROM
   ((p->>'expectedRevision')::integer,p->>'profile',p->>'selectionPolicy',p->>'roundingPolicy',(p->>'decimalPlaces')::integer,payload->>'reason',btrim(payload->>'evidence'))
   OR(fact.previous_id IS NULL AND payload->>'correctionDueAt' IS NOT NULL)
   OR(fact.previous_id IS NOT NULL AND fact.due_at IS DISTINCT FROM(payload->>'correctionDueAt')::timestamptz)
   THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
  old=jsonb_build_object('operationId',operation,'tradeId',fact.trade_id,'determinationId',fact.id,'previousId',fact.previous_id,'amount',trim_scale(fact.rounded_total)::text,'contribution',trim_scale(fact.contribution)::text);
  prior.trade_id=fact.trade_id;prior.determination_id=fact.id;
 END IF;
 IF(SELECT count(*) FROM trading.gas_billing_commands WHERE(catalog_id,trade_id)=(catalog,prior.trade_id))>=10000 THEN RAISE EXCEPTION 'gas_billing_limit';END IF;
 INSERT INTO trading.gas_billing_commands VALUES(catalog,operation,prior.trade_id,prior.determination_id,actor,payload,old);
 PERFORM security.require_gas_quantity_trade(catalog,prior.trade_id,true);RETURN old;
END$$;

CREATE FUNCTION security.record_gas_billing(catalog uuid,body jsonb,as_of timestamptz,context uuid,calculation jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';trade uuid=(p->>'tradeId')::uuid;operation uuid=(body->>'operationId')::uuid;
 cash integer;flow_day date;policy text=p->>'selectionPolicy';places integer=(p->>'decimalPlaces')::integer;
 source uuid=(calculation->>'sourceId')::uuid;head trading.gas_trade_revisions;m trading.gas_component_terms;o trading.gas_delivery_obligations;
 a market_data.gas_valuation_assignments;quote ref_data.quote_specifications;prior trading.gas_cash_determinations;manual trading.gas_quantity_revisions;
 target trading.facility_adopted_targets;adoption trading.facility_quantity_adoptions;qty numeric;source_publication integer;source_effective timestamptz;
 finish timestamptz;valid_day boolean;due timestamptz;total numeric=(calculation->>'amount')::numeric;num numeric=(calculation->>'numerator')::numeric;den numeric=(calculation->>'denominator')::numeric;
 scaled numeric;whole numeric;expected numeric;actor uuid;publication integer;identity uuid=gen_random_uuid();old jsonb;result jsonb;
BEGIN
 PERFORM security.validate_gas_billing_request(body);
 cash=(p->>'cashSlot')::integer;flow_day=(p->>'day')::date;
 old=security.replay_gas_billing(catalog,operation,body);IF old IS NOT NULL THEN RETURN old;END IF;
 IF as_of IS NULL OR NOT isfinite(as_of) OR context IS NULL OR
 p->>'profile' IS DISTINCT FROM 'gas_final_daily_fixed_cash_v1' OR p->>'roundingPolicy' IS DISTINCT FROM 'currency_minor_units_to_even_v1' OR
 policy IS NULL OR policy NOT IN('final_allocated_billing_v1','final_facility_allocated_billing_v1') OR
 p#>>'{context,convention}' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR p#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR p#>>'{context,contextId}' IS NOT NULL OR
 places IS NULL OR places NOT BETWEEN 0 AND 12 OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL OR
 source IS DISTINCT FROM(body->>'sourceId')::uuid OR source IS NULL OR num IS NULL OR den IS NULL OR den<=0 OR total IS NULL THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(p->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,head.instrument_id,cash);
 SELECT * INTO o FROM trading.gas_delivery_obligations WHERE(catalog_id,trade_id,instrument_id,slot)=(catalog,trade,head.instrument_id,m.source_slot);
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
 IF source_effective>as_of OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,source_publication)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date) AND source_publication<=x.known_through AND k.known_from<=t.known_at) THEN RAISE EXCEPTION 'gas_billing_context';END IF;
 SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,m.definition_revision_id,m.assignment_code);
 SELECT q.* INTO STRICT quote FROM ref_data.quote_specifications q JOIN market_data.curves c ON(c.reference_catalog_id,c.reference_revision_id,c.quote_code)=(q.catalog_id,q.revision_id,q.code)
 WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,m.definition_revision_id,a.curve_code);
 IF NOT EXISTS(SELECT 1 FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id,c.code,c.display_minor_units)=(quote.catalog_id,quote.revision_id,quote.currency_code,places)) THEN RAISE EXCEPTION 'gas_billing_rounding';END IF;
 -- Integrity oracle: managed calculation must match the exact retained cash terms.
 IF num<>qty*m.price*quote.multiplier*m.direction*den THEN RAISE EXCEPTION 'gas_billing_amount';END IF;
 scaled=qty*m.price*quote.multiplier*m.direction*power(10::numeric,places);whole=trunc(scaled);
 expected=(whole+CASE WHEN abs(scaled-whole)>0.5 OR(abs(scaled-whole)=0.5 AND mod(abs(whole),2)=1) THEN sign(scaled) ELSE 0 END)/power(10::numeric,places);
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
END$$;
ALTER FUNCTION security.gas_billing_status(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.replay_gas_billing(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.record_gas_billing(uuid,jsonb,timestamptz,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_billing_status(uuid,uuid,jsonb),security.replay_gas_billing(uuid,uuid,jsonb),security.record_gas_billing(uuid,jsonb,timestamptz,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_billing_status(uuid,uuid,jsonb),security.replay_gas_billing(uuid,uuid,jsonb),security.record_gas_billing(uuid,jsonb,timestamptz,uuid,jsonb) TO openetrm_runtime;
-- Existing turns retain replay; new admission/execution requires this tool catalog.
DO $upgrade$
DECLARE signature text;definition text;before text:='runtime_set<>''turns_v17''';
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'gas_billing_runtime_guard';END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v18''');
 END LOOP;
END $upgrade$;
COMMIT;
