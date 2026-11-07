-- Current consumer: the in-term physical gas lifecycle (D359). New gas compositions retain whole gas-day
-- delivery and accrued payment programs; recorded payments allocate signed cash to exact retained
-- occurrences under their own knowledge receipts; an owner-only load admits a composition already in term.
-- Stored programs are never re-validated: trades booked earlier keep their future-delivery lifecycles.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('trading.require_side_program_content(uuid,uuid,bytea)','f533002f67994482960679d2cef0228f387a1656087c32766e3c672323f94406','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)','14ce43c25adba67bb7955aeff80204779803094a587f2f5217c03eab10debaea','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)','03f0ef8ed5b8cb2050f12ce1a9d923668b084bd792587472e800b8b3bb248e41','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.publish_admitted_side_schedules(uuid,uuid,uuid)','25a7e6bdd697ea58a4dccc25a012f431496d9591983e57145bd7d95cd43c26c5','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.write_gas_composition(uuid,jsonb,timestamp with time zone)','33b874fe8a269948522a770b3dd1312078d6b37aad64aa090d5493ea85c7697d','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.book_gas_composition(uuid,jsonb,timestamp with time zone)','71288ce7efbfbd2220ae3988fb63152d6bddb6650c2814cf379d19752ef18f30','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb),
  ('security.amend_gas_composition(uuid,jsonb,timestamp with time zone)','3087a4ee6a998b9ccb2346a6f3e0be4155dabfd175f263164d6fb8e25525270f','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb)
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature) AND p.proowner='openetrm_security_owner'::regrole
   AND to_jsonb(p.proacl)=helper.acl AND to_jsonb(p.proconfig)='["search_path=pg_catalog", "jit=off"]'::jsonb AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'in_term_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 IF to_regprocedure('security.write_gas_composition(uuid,jsonb,timestamp with time zone,boolean)') IS NOT NULL OR to_regclass('trading.gas_cash_payments') IS NOT NULL
 THEN RAISE EXCEPTION 'in_term_upgrade: unexpected private writer' USING ERRCODE='55000';END IF;
END$$;

-- Admission: new compositions select the started-delivery lifecycles; whole gas days are gas-only.
-- The shared writer gains an explicit started-term argument; the ticket and amendment paths pass false.
DO $patch$ DECLARE target text;definition text;c record;BEGIN
 FOREACH target IN ARRAY ARRAY['trading.require_side_program_content(uuid,uuid,bytea)',
  'trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)',
  'trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)',
  'security.publish_admitted_side_schedules(uuid,uuid,uuid)','security.write_gas_composition(uuid,jsonb,timestamp with time zone)',
  'security.book_gas_composition(uuid,jsonb,timestamp with time zone)','security.amend_gas_composition(uuid,jsonb,timestamp with time zone)'] LOOP
  definition=pg_get_functiondef(target::regprocedure);
  FOR c IN SELECT * FROM(VALUES
   ('trading.require_side_program_content(uuid,uuid,bytea)',$b$'future_gas_delivery_v1','future_crude_delivery_v1','future_lng_cargo_v1')$b$,
    $a$'future_gas_delivery_v1','future_crude_delivery_v1','future_lng_cargo_v1','whole_gas_day_delivery_v1','accrued_payment_v1')$a$),
   ('trading.require_side_program_content(uuid,uuid,bytea)',$b$(r.parameters->>'lifecycle'='undue_payment_v1') IS DISTINCT FROM(r.grid_ordinal IS NULL)$b$,
    $a$(r.parameters->>'lifecycle' IN('undue_payment_v1','accrued_payment_v1')) IS DISTINCT FROM(r.grid_ordinal IS NULL)$a$),
   ('trading.require_side_program_content(uuid,uuid,bytea)',$b$ IF document IS DISTINCT FROM trading.side_program_document(catalog,schedule)$b$,
    $a$ IF document->>'family'<>'gas' AND EXISTS(SELECT 1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'output_guard') AND parameters->>'lifecycle'='whole_gas_day_delivery_v1') THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 IF document IS DISTINCT FROM trading.side_program_document(catalog,schedule)$a$),
   ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)',
    $b$CASE WHEN slot=0 THEN 'future_gas_delivery_v1' ELSE 'undue_payment_v1' END$b$,$a$CASE WHEN slot=0 THEN 'whole_gas_day_delivery_v1' ELSE 'accrued_payment_v1' END$a$),
   ('trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)',$b$"lifecycle":"undue_payment_v1"$b$,$a$"lifecycle":"accrued_payment_v1"$a$),
   ('security.publish_admitted_side_schedules(uuid,uuid,uuid)',$b$  IF candidate.side_id IS NULL THEN RAISE EXCEPTION 'side_schedule_membership';END IF;$b$,
    $a$  IF candidate.side_id IS NULL THEN RAISE EXCEPTION 'side_schedule_membership';END IF;
  IF candidate.gas_side_id IS NULL AND EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.operation)=(catalog,item.schedule_id,'output_guard')
   AND r.parameters->>'lifecycle' IN('whole_gas_day_delivery_v1','accrued_payment_v1')) THEN RAISE EXCEPTION 'side_schedule_lifecycle';END IF;$a$),
   ('security.write_gas_composition(uuid,jsonb,timestamp with time zone)',$b$security.write_gas_composition(catalog uuid, body jsonb, valuation timestamp with time zone)$b$,
    $a$security.write_gas_composition(catalog uuid, body jsonb, valuation timestamp with time zone, allow_started boolean)$a$),
   ('security.write_gas_composition(uuid,jsonb,timestamp with time zone)',$b$IF valuation>=start_at THEN RAISE EXCEPTION 'gas_delivery_lifecycle';END IF;$b$,
    $a$IF valuation>=start_at AND allow_started IS NOT TRUE THEN RAISE EXCEPTION 'gas_delivery_lifecycle';END IF;$a$),
   ('security.book_gas_composition(uuid,jsonb,timestamp with time zone)',$b$security.write_gas_composition(catalog,body,valuation)$b$,$a$security.write_gas_composition(catalog,body,valuation,false)$a$),
   ('security.amend_gas_composition(uuid,jsonb,timestamp with time zone)',$b$security.write_gas_composition(catalog,body,valuation)$b$,$a$security.write_gas_composition(catalog,body,valuation,false)$a$)
  )v(signature,before_text,after_text) WHERE v.signature=target LOOP
   IF(length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'in_term_upgrade: unexpected definition %',target USING ERRCODE='55000';END IF;
   definition=replace(definition,c.before_text,c.after_text);
  END LOOP;
  EXECUTE definition;
 END LOOP;
END$patch$;
DROP FUNCTION security.write_gas_composition(uuid,jsonb,timestamptz);
ALTER FUNCTION security.write_gas_composition(uuid,jsonb,timestamptz,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.write_gas_composition(uuid,jsonb,timestamptz,boolean) FROM PUBLIC;

-- An administrator loads a composition already in its delivery term through the same admission.
-- The ticket keeps refusing started deliveries; there is no runtime grant.
CREATE FUNCTION security.load_gas_composition_in_term(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$BEGIN
 IF body ?| ARRAY['tradeId','expectedRevision','bindingId','reason','amendmentPolicy'] THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 RETURN security.write_gas_composition(catalog,body,as_of,true);
END$$;
ALTER FUNCTION security.load_gas_composition_in_term(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.load_gas_composition_in_term(uuid,jsonb,timestamptz) FROM PUBLIC;

-- A recorded payment: one signed cash amount of a held cash membership, effective when paid, known
-- from its publication's receipt. A changed value of the same business key is a new revision.
CREATE TABLE trading.gas_cash_payments(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL,
 business_key text NOT NULL CHECK(business_key~'^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,127}$'),revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,
 paid_at ref_data.instant NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 amount ref_data.exact_number NOT NULL CHECK(amount<>0),operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 request jsonb NOT NULL CHECK(pg_column_size(request)<=65536),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,trade_id,slot,business_key,revision),UNIQUE(catalog_id,trade_id,slot,business_key,id),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK(paid_at<=effective_at),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.instrument_side_schedules(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,trade_id,slot,business_key,previous_id) REFERENCES trading.gas_cash_payments(catalog_id,trade_id,slot,business_key,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- Each allocation names the retained payment/{occurrence}/value row of the payment's own membership schedule.
CREATE TABLE trading.gas_cash_payment_allocations(
 catalog_id uuid NOT NULL,payment_id uuid NOT NULL,occurrence integer NOT NULL CHECK(occurrence BETWEEN 0 AND 1199),
 schedule_id uuid NOT NULL,row_ordinal integer NOT NULL,amount ref_data.exact_number NOT NULL CHECK(amount<>0),
 PRIMARY KEY(catalog_id,payment_id,occurrence),
 FOREIGN KEY(catalog_id,payment_id) REFERENCES trading.gas_cash_payments(catalog_id,id),
 FOREIGN KEY(catalog_id,schedule_id,row_ordinal) REFERENCES trading.side_schedule_rows(catalog_id,schedule_id,ordinal)
);
-- Conservation and occurrence identity hold at commit for every writer, not only the recording function.
CREATE FUNCTION trading.require_gas_cash_payment() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p trading.gas_cash_payments;direction integer;BEGIN
 SELECT * INTO STRICT p FROM trading.gas_cash_payments WHERE(catalog_id,id)=(NEW.catalog_id,(to_jsonb(NEW)->>TG_ARGV[0])::uuid);
 SELECT m.direction INTO STRICT direction FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id,m.slot)=(p.catalog_id,p.instrument_id,p.slot);
 IF sign(p.amount)<>direction OR(SELECT sum(a.amount) FROM trading.gas_cash_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)) IS DISTINCT FROM p.amount OR
 EXISTS(SELECT 1 FROM trading.gas_cash_payment_allocations a LEFT JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot,s.schedule_id)=(p.catalog_id,p.instrument_id,p.slot,a.schedule_id)
  LEFT JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(a.catalog_id,a.schedule_id,a.row_ordinal)
  WHERE(a.catalog_id,a.payment_id)=(p.catalog_id,p.id) AND(sign(a.amount)<>direction OR s.schedule_id IS NULL OR
   (r.operation,r.local_key,r.payment_ordinal) IS DISTINCT FROM('output_guard','payment/'||a.occurrence||'/value',a.occurrence) OR r.grid_ordinal IS NOT NULL))
 THEN RAISE EXCEPTION 'gas_payment_allocation';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION trading.require_gas_cash_payment() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_cash_payment() FROM PUBLIC;
DO $$DECLARE t text;payment text;BEGIN
 FOR t,payment IN SELECT * FROM(VALUES('gas_cash_payments','id'),('gas_cash_payment_allocations','payment_id'))v LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE CONSTRAINT TRIGGER conserved_payment AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_gas_cash_payment(%L)',t,payment);
 END LOOP;
END$$;
-- Reads see a payment only under current authority and within the selected knowledge cutoff.
CREATE POLICY scoped_read ON trading.gas_cash_payments FOR SELECT TO openetrm_runtime USING(
 security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY scoped_read ON trading.gas_cash_payment_allocations FOR SELECT TO openetrm_runtime USING(EXISTS(
 SELECT 1 FROM trading.gas_cash_payments p WHERE(p.catalog_id,p.id)=(gas_cash_payment_allocations.catalog_id,gas_cash_payment_allocations.payment_id)));
GRANT SELECT ON trading.gas_cash_payments,trading.gas_cash_payment_allocations TO openetrm_runtime;

CREATE FUNCTION security.record_gas_cash_payment(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE trade uuid;operation uuid;slot_number integer;paid timestamptz;total numeric;key text;item jsonb;actor uuid;publication integer;identity uuid=gen_random_uuid();
 head trading.gas_trade_revisions;m trading.gas_component_terms;a market_data.gas_valuation_assignments;quote ref_data.quote_specifications;places integer;
 prior trading.gas_cash_payments;schedule uuid;guard trading.side_schedule_rows;day_end timestamptz;
BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','tradeId','expectedRevision','slot','businessKey','paidAt','currency','amount','allocations']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('operationId','tradeId','expectedRevision','slot','businessKey','paidAt','currency','amount','allocations')) OR
 jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR jsonb_typeof(body->'slot') IS DISTINCT FROM 'number' OR jsonb_typeof(body->'allocations') IS DISTINCT FROM 'array' OR
 jsonb_array_length(body->'allocations') NOT BETWEEN 1 AND 1200 OR EXISTS(SELECT 1 FROM unnest(ARRAY['operationId','tradeId','businessKey','paidAt','currency','amount']) k WHERE jsonb_typeof(body->k) IS DISTINCT FROM 'string') OR
 body->>'paidAt'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$' OR
 body->>'amount'!~'^-?[0-9]{1,24}([.][0-9]{1,18})?$' OR body->>'businessKey'!~'^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,127}$' OR
 EXISTS(SELECT 1 FROM jsonb_array_elements(body->'allocations') x WHERE jsonb_typeof(x) IS DISTINCT FROM 'object' OR NOT(x?&ARRAY['occurrence','amount']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(x) k WHERE k NOT IN('occurrence','amount')) OR jsonb_typeof(x->'occurrence') IS DISTINCT FROM 'number' OR
  jsonb_typeof(x->'amount') IS DISTINCT FROM 'string' OR x->>'amount'!~'^-?[0-9]{1,24}([.][0-9]{1,18})?$' OR x->>'occurrence'!~'^[0-9]{1,4}$') OR
 (SELECT count(DISTINCT x->>'occurrence') FROM jsonb_array_elements(body->'allocations') x)<>jsonb_array_length(body->'allocations') OR
 as_of IS NULL OR NOT isfinite(as_of) OR body->>'expectedRevision'!~'^[0-9]{1,3}$' OR body->>'slot'!~'^[0-9]{1,2}$'
 THEN RAISE EXCEPTION 'gas_payment_input';END IF;
 trade=(body->>'tradeId')::uuid;operation=(body->>'operationId')::uuid;slot_number=(body->>'slot')::integer;paid=(body->>'paidAt')::timestamptz;total=(body->>'amount')::numeric;key=body->>'businessKey';
 IF operation='00000000-0000-0000-0000-000000000000'::uuid OR NOT isfinite(paid) OR paid>as_of THEN RAISE EXCEPTION 'gas_payment_input';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.gas_cash_payments WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>actor OR prior.request IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_payment_conflict';END IF;
  RETURN jsonb_build_object('operationId',operation,'tradeId',prior.trade_id,'paymentId',prior.id,'revision',prior.revision,'previousId',prior.previous_id);
 END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(body->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'gas_payment_conflict';END IF;
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,head.instrument_id,slot_number);
 IF m.role IS NULL OR m.role NOT IN('consideration','fee','indexed_consideration') THEN RAISE EXCEPTION 'gas_payment_slot';END IF;
 SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,m.definition_revision_id,m.assignment_code);
 SELECT q.* INTO STRICT quote FROM ref_data.quote_specifications q JOIN market_data.curves c ON(c.reference_catalog_id,c.reference_revision_id,c.quote_code)=(q.catalog_id,q.revision_id,q.code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,m.definition_revision_id,a.curve_code);
 SELECT display_minor_units INTO STRICT places FROM ref_data.currencies WHERE(catalog_id,revision_id,code)=(quote.catalog_id,quote.revision_id,quote.currency_code);
 IF body->>'currency' IS DISTINCT FROM quote.currency_code::text THEN RAISE EXCEPTION 'gas_payment_currency';END IF;
 IF scale(total)>places OR sign(total)<>m.direction OR(SELECT sum((x->>'amount')::numeric) FROM jsonb_array_elements(body->'allocations') x)<>total OR
  EXISTS(SELECT 1 FROM jsonb_array_elements(body->'allocations') x WHERE scale((x->>'amount')::numeric)>places OR sign((x->>'amount')::numeric)<>m.direction)
 THEN RAISE EXCEPTION 'gas_payment_amount';END IF;
 -- No prepayment: each occurrence's first gas day has ended by the effective payment instant.
 SELECT schedule_id INTO STRICT schedule FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=(catalog,head.instrument_id,slot_number);
 FOR item IN SELECT value FROM jsonb_array_elements(body->'allocations') LOOP
  SELECT * INTO guard FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.local_key,r.operation,r.payment_ordinal)=(catalog,schedule,'payment/'||(item->>'occurrence')||'/value','output_guard',(item->>'occurrence')::integer) AND r.grid_ordinal IS NULL;
  SELECT g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond' INTO day_end
  FROM trading.instrument_side_schedules s JOIN trading.side_schedule_grids g USING(catalog_id,schedule_id)
  WHERE(s.catalog_id,s.instrument_id,s.slot)=(catalog,head.instrument_id,m.source_slot) AND g.operating_days=ARRAY[guard.delivery_from-date '0001-01-01'] AND cardinality(g.selection)=1;
  IF guard.ordinal IS NULL OR day_end IS NULL OR day_end>paid THEN RAISE EXCEPTION 'gas_payment_occurrence';END IF;
 END LOOP;
 -- The same fact recorded again returns its current revision; a changed value is the next revision.
 SELECT * INTO prior FROM trading.gas_cash_payments WHERE(catalog_id,trade_id,slot,business_key)=(catalog,trade,slot_number,key) ORDER BY revision DESC LIMIT 1;
 IF prior.id IS NOT NULL AND(prior.trade_revision,prior.paid_at,prior.amount)=(head.revision,paid,total) AND NOT EXISTS(
  (SELECT occurrence,amount FROM trading.gas_cash_payment_allocations WHERE(catalog_id,payment_id)=(catalog,prior.id)
   EXCEPT SELECT (x->>'occurrence')::integer,(x->>'amount')::numeric FROM jsonb_array_elements(body->'allocations') x) UNION ALL
  (SELECT (x->>'occurrence')::integer,(x->>'amount')::numeric FROM jsonb_array_elements(body->'allocations') x
   EXCEPT SELECT occurrence,amount FROM trading.gas_cash_payment_allocations WHERE(catalog_id,payment_id)=(catalog,prior.id)))
 THEN RETURN jsonb_build_object('operationId',operation,'tradeId',trade,'paymentId',prior.id,'revision',prior.revision,'previousId',prior.previous_id);END IF;
 IF(SELECT count(*) FROM trading.gas_cash_payments WHERE(catalog_id,trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_payment_limit';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.gas_cash_payments VALUES(catalog,identity,trade,head.revision,head.instrument_id,slot_number,key,coalesce(prior.revision,0)+1,prior.id,paid,
  quote.catalog_id,quote.revision_id,quote.currency_code,total,operation,actor,body,publication,as_of);
 INSERT INTO trading.gas_cash_payment_allocations SELECT catalog,identity,(x->>'occurrence')::integer,schedule,r.ordinal,(x->>'amount')::numeric
  FROM jsonb_array_elements(body->'allocations') x JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.local_key)=(catalog,schedule,'payment/'||(x->>'occurrence')||'/value');
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 RETURN jsonb_build_object('operationId',operation,'tradeId',trade,'paymentId',identity,'revision',coalesce(prior.revision,0)+1,'previousId',prior.id);
END$$;
ALTER FUNCTION security.record_gas_cash_payment(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_gas_cash_payment(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.record_gas_cash_payment(uuid,jsonb,timestamptz) TO openetrm_runtime;
COMMIT;
