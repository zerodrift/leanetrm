-- Explicit owned-lot allocations and dispatch-backed sales cash use the retained ledger.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF(SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_proc WHERE oid='security.record_crude_lifecycle(uuid,text,jsonb,timestamptz,uuid)'::regprocedure)
 <>'606f9776eb3ff3a12b2ce06b225f226fdd45dcc351d4d8280c10b0f712a478cb' THEN RAISE EXCEPTION 'crude_inventory_predecessor';END IF;
END$$;
ALTER TABLE trading.crude_operational_events DROP CONSTRAINT crude_operational_events_kind_check;
ALTER TABLE trading.crude_operational_events ADD CHECK(kind IN('receipt','allocation','dispatch','invoice','payment','payment_reversal','invoice_void'));
ALTER TABLE trading.crude_operational_events DROP CONSTRAINT crude_operational_events_profile_check;
ALTER TABLE trading.crude_operational_events ADD CHECK(profile IN('physical_crude_receipt_cash_v1','physical_crude_sale_cash_v1'));
CREATE TABLE trading.crude_inventory_allocations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,receipt_id uuid NOT NULL,sale_trade_id uuid NOT NULL,sale_revision integer NOT NULL,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id,receipt_id),
 FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,receipt_id,trade_id) REFERENCES trading.crude_receipts(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,sale_trade_id,sale_revision) REFERENCES trading.crude_trade_revisions(catalog_id,trade_id,revision)
);
ALTER TABLE trading.crude_dispatches ADD COLUMN allocation_id uuid;
ALTER TABLE trading.crude_dispatches ADD FOREIGN KEY(catalog_id,allocation_id,trade_id,receipt_id) REFERENCES trading.crude_inventory_allocations(catalog_id,id,trade_id,receipt_id);
ALTER TABLE trading.crude_dispatches ADD UNIQUE(catalog_id,id,trade_id);
CREATE TABLE trading.crude_dispatch_sales(
 catalog_id uuid NOT NULL,dispatch_id uuid NOT NULL,purchase_trade_id uuid NOT NULL,sale_trade_id uuid NOT NULL,sale_revision integer NOT NULL,
 PRIMARY KEY(catalog_id,dispatch_id),UNIQUE(catalog_id,dispatch_id,sale_trade_id,sale_revision),
 FOREIGN KEY(catalog_id,dispatch_id,purchase_trade_id) REFERENCES trading.crude_dispatches(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,sale_trade_id,sale_revision) REFERENCES trading.crude_trade_revisions(catalog_id,trade_id,revision)
);
-- Retain an exact relationship for already recorded onward dispatches without
-- changing their original facts, effective times or knowledge publications.
INSERT INTO trading.crude_dispatch_sales
 SELECT d.catalog_id,d.id,d.trade_id,r.sale_trade_id,r.sale_revision FROM trading.crude_dispatches d
 JOIN trading.crude_receipts r ON(r.catalog_id,r.id,r.trade_id)=(d.catalog_id,d.receipt_id,d.trade_id);
ALTER TABLE trading.crude_invoices ALTER COLUMN receipt_id DROP NOT NULL;
ALTER TABLE trading.crude_invoices ADD COLUMN dispatch_id uuid;
ALTER TABLE trading.crude_invoices ADD COLUMN sale_revision integer;
ALTER TABLE trading.crude_invoices ADD CHECK((receipt_id IS NOT NULL)::integer+(dispatch_id IS NOT NULL)::integer=1);
ALTER TABLE trading.crude_invoices ADD CHECK((dispatch_id IS NULL)=(sale_revision IS NULL));
ALTER TABLE trading.crude_invoices ADD FOREIGN KEY(catalog_id,dispatch_id,trade_id,sale_revision) REFERENCES trading.crude_dispatch_sales(catalog_id,dispatch_id,sale_trade_id,sale_revision);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_inventory_allocations','crude_dispatch_sales'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades''))',t);
 END LOOP;
 CREATE POLICY history_scope ON trading.crude_inventory_allocations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.crude_operational_events e WHERE(e.catalog_id,e.id)=(crude_inventory_allocations.catalog_id,crude_inventory_allocations.id)));
 CREATE POLICY history_scope ON trading.crude_dispatch_sales AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.crude_operational_events e WHERE(e.catalog_id,e.id)=(crude_dispatch_sales.catalog_id,crude_dispatch_sales.dispatch_id)));
END$$;
CREATE FUNCTION security.require_crude_event_context(catalog uuid,event uuid,context uuid,at_time timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.crude_operational_events e JOIN trading.knowledge_receipts k USING(catalog_id,publication_order)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(catalog,context) JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE(e.catalog_id,e.id)=(catalog,event) AND e.effective_at<=at_time AND e.publication_order<=x.known_through AND k.known_from<=t.known_at)
 THEN RAISE EXCEPTION 'crude_lifecycle_context';END IF;
END$$;
CREATE FUNCTION security.require_crude_trade_context(catalog uuid,trade uuid,selected_revision integer,context uuid,at_time timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.crude_trade_revisions r JOIN trading.knowledge_receipts k USING(catalog_id,publication_order)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(catalog,context) JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,trade,selected_revision) AND r.effective_at<=at_time AND r.publication_order<=x.known_through AND k.known_from<=t.known_at)
 THEN RAISE EXCEPTION 'crude_lifecycle_context';END IF;
END$$;
-- Capacity may use only the complete reservation population covered by the
-- selected knowledge context, including reservations owned by another purchase.
CREATE FUNCTION trading.crude_sale_reservations(catalog uuid,trade uuid,selected_revision integer) RETURNS TABLE(id uuid,trade_id uuid)
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
  SELECT r.id,r.trade_id FROM trading.crude_receipts r
  WHERE(r.catalog_id,r.sale_trade_id,r.sale_revision)=(catalog,trade,selected_revision)
  AND NOT EXISTS(SELECT 1 FROM trading.crude_receipts newer WHERE(newer.catalog_id,newer.previous_id)=(r.catalog_id,r.id))
  UNION ALL SELECT a.id,a.trade_id FROM trading.crude_inventory_allocations a WHERE(a.catalog_id,a.sale_trade_id,a.sale_revision)=(catalog,trade,selected_revision)
$$;
REVOKE ALL ON FUNCTION trading.crude_sale_reservations(uuid,uuid,integer) FROM PUBLIC,openetrm_runtime;
GRANT EXECUTE ON FUNCTION trading.crude_sale_reservations(uuid,uuid,integer) TO openetrm_security_owner;
CREATE FUNCTION security.require_crude_sale_capacity_context(catalog uuid,trade uuid,selected_revision integer,context uuid,at_time timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source record;BEGIN
 FOR source IN SELECT * FROM trading.crude_sale_reservations(catalog,trade,selected_revision)
 LOOP
  PERFORM security.require_crude_event_context(catalog,source.id,context,at_time);
  PERFORM security.require_crude_lifecycle(catalog,source.trade_id,false);
 END LOOP;
END$$;
CREATE OR REPLACE FUNCTION trading.crude_operational_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body-ARRAY['context','operationId','expectedVersion','expectedRevision','previewHash','commandKind'];key text;BEGIN
 result=result||jsonb_build_object('effectiveAt',extract(epoch FROM (body->>'effectiveAt')::timestamptz),'businessKey',btrim(body->>'businessKey'),'evidence',btrim(body->>'evidence'));
 FOREACH key IN ARRAY ARRAY['tradeId','receiptId','invoiceId','paymentId','saleTradeId','allocationId','dispatchId'] LOOP
  IF body?key THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 IF body?'quantity' THEN result=result||jsonb_build_object('quantity',(body->>'quantity')::numeric);END IF;
 IF body?'amount' THEN result=result||jsonb_build_object('amount',(body->>'amount')::numeric);END IF;
 RETURN result;
END$$;
CREATE OR REPLACE FUNCTION security.record_crude_lifecycle(catalog uuid,command_kind text,body jsonb,as_of timestamptz,context uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid=(body->>'tradeId')::uuid;operation uuid=(body->>'operationId')::uuid;at_time timestamptz=(body->>'effectiveAt')::timestamptz;
 h trading.crude_trade_revisions;i trading.crude_instruments;a market_data.crude_valuation_assignments;s trading.crude_trade_revisions;si trading.crude_instruments;sa market_data.crude_valuation_assignments;
 prior trading.crude_operational_events;r trading.crude_receipts;inv trading.crude_invoices;last_invoice trading.crude_invoices;payment trading.crude_payments;next_ordinal integer;allocation trading.crude_inventory_allocations;dispatch trading.crude_dispatches;binding trading.crude_dispatch_sales;evidence_quantity numeric;sale_profile boolean;source record;
 actor uuid;publication integer;identity uuid=gen_random_uuid();version integer;old jsonb;outcome jsonb;fact jsonb;oldfact jsonb;
 amount numeric;qty numeric=(body->>'quantity')::numeric;used numeric;price numeric;direction integer;component_slot integer;places integer;currency text;refcatalog uuid;refrevision uuid;exact_amount numeric;rounded numeric;multiplier numeric;
BEGIN
 PERFORM security.require_crude_lifecycle(catalog,trade,true);PERFORM pg_advisory_xact_lock(730849102);PERFORM security.require_crude_lifecycle(catalog,trade,true);
 IF pg_column_size(body)>16384 OR jsonb_typeof(body)<>'object' OR jsonb_typeof(body->'context') IS DISTINCT FROM 'object'
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'context') k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId'))
 OR NOT(body?&ARRAY['context','tradeId','expectedRevision','expectedVersion','operationId','businessKey','profile','effectiveAt','evidence'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE NOT k=ANY(ARRAY['context','tradeId','expectedRevision','expectedVersion','operationId','businessKey','profile','effectiveAt','evidence']||CASE command_kind
 WHEN 'receipt' THEN ARRAY['quantity','titleTransferred','disposition','saleTradeId','saleRevision'] WHEN 'allocation' THEN ARRAY['receiptId','quantity','saleTradeId','saleRevision'] WHEN 'dispatch' THEN ARRAY['receiptId','quantity','titleTransferred','allocationId']
 WHEN 'invoice' THEN ARRAY[CASE WHEN body->>'profile'='physical_crude_sale_cash_v1' THEN 'dispatchId' ELSE 'receiptId' END,'component','decimalPlaces','roundingPolicy','previewHash'] WHEN 'payment' THEN ARRAY['invoiceId','amount','direction'] WHEN 'payment_reversal' THEN ARRAY['paymentId','reversalPolicy'] WHEN 'invoice_void' THEN ARRAY['invoiceId','reversalPolicy'] ELSE ARRAY[]::text[] END))
 OR NOT(body?&CASE command_kind WHEN 'receipt' THEN ARRAY['quantity','titleTransferred','disposition','saleTradeId','saleRevision'] WHEN 'allocation' THEN ARRAY['receiptId','quantity','saleTradeId','saleRevision'] WHEN 'dispatch' THEN ARRAY['receiptId','quantity','titleTransferred'] WHEN 'invoice' THEN ARRAY[CASE WHEN body->>'profile'='physical_crude_sale_cash_v1' THEN 'dispatchId' ELSE 'receiptId' END,'component','decimalPlaces','roundingPolicy','previewHash'] WHEN 'payment' THEN ARRAY['invoiceId','amount','direction'] WHEN 'payment_reversal' THEN ARRAY['paymentId','reversalPolicy'] WHEN 'invoice_void' THEN ARRAY['invoiceId','reversalPolicy'] ELSE ARRAY[]::text[] END)
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR jsonb_typeof(body->'expectedVersion') IS DISTINCT FROM 'number'
 OR(command_kind IN('receipt','dispatch') AND jsonb_typeof(body->'titleTransferred') IS DISTINCT FROM 'boolean')
 OR(command_kind='dispatch' AND body?'allocationId' AND(jsonb_typeof(body->'allocationId') IS DISTINCT FROM 'string' OR(body->>'allocationId')::uuid='00000000-0000-0000-0000-000000000000'::uuid))
 OR(command_kind='receipt' AND jsonb_typeof(body->'saleRevision') NOT IN('null','number'))
 OR(command_kind='allocation' AND jsonb_typeof(body->'saleRevision') IS DISTINCT FROM 'number')
 OR(command_kind='invoice' AND jsonb_typeof(body->'decimalPlaces') IS DISTINCT FROM 'number')
 OR(command_kind IN('payment_reversal','invoice_void') AND body->>'reversalPolicy' IS DISTINCT FROM 'crude_record_correction_v1')
 THEN RAISE EXCEPTION 'crude_lifecycle_input';END IF;
 old=security.crude_lifecycle_status(catalog,operation,body||jsonb_build_object('commandKind',command_kind));IF old?'eventId' THEN RETURN old;END IF;
 IF command_kind NOT IN('receipt','allocation','dispatch','invoice','payment','payment_reversal','invoice_void') OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid
 OR body->>'profile' IS NULL OR body->>'profile' NOT IN('physical_crude_receipt_cash_v1','physical_crude_sale_cash_v1') OR body#>>'{context,convention}' IS DISTINCT FROM 'physical_crude_fixed_volume_v1'
 OR body#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR body#>>'{context,contextId}' IS NOT NULL
 OR as_of IS NULL OR at_time IS NULL OR NOT isfinite(at_time) OR at_time>as_of
 OR body->>'effectiveAt' !~ '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.(\d{1,6}|\d{6}0))?Z$'
 OR length(btrim(body->>'businessKey')) NOT BETWEEN 1 AND 128 OR body->>'businessKey' IS NULL
 OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL
 OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date)) THEN RAISE EXCEPTION 'crude_lifecycle_input';END IF;
 SELECT * INTO h FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO i FROM trading.crude_instruments WHERE(catalog_id,instrument_id)=(catalog,h.instrument_id);
 SELECT * INTO a FROM market_data.crude_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,i.definition_revision_id,i.assignment_code);
 IF h.revision IS DISTINCT FROM(body->>'expectedRevision')::integer OR h.effective_at>at_time THEN RAISE EXCEPTION 'crude_conflict';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.curves v JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,v.quote_code) WHERE(v.catalog_id,v.revision_id,v.code)=(catalog,i.definition_revision_id,a.curve_code) AND q.multiplier=1) THEN RAISE EXCEPTION 'crude_lifecycle_profile';END IF;
 sale_profile=body->>'profile'='physical_crude_sale_cash_v1';
 IF (sale_profile AND(h.direction<>-1 OR i.consideration_direction<>1 OR command_kind IN('receipt','allocation','dispatch'))) OR
 (NOT sale_profile AND(h.direction<>1 OR i.consideration_direction<>-1)) OR i.fixed_price<=0 OR i.handling_fee<0 THEN RAISE EXCEPTION 'crude_lifecycle_profile';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,h.publication_order)
 WHERE(t.catalog_id,t.id)=(catalog,context) AND h.publication_order<=x.known_through AND k.known_from<=t.known_at) THEN RAISE EXCEPTION 'crude_lifecycle_context';END IF;
 SELECT coalesce(max(e.version),0) INTO version FROM trading.crude_operational_events e WHERE(e.catalog_id,e.trade_id)=(catalog,trade);
 SELECT * INTO prior FROM trading.crude_operational_events e WHERE(e.catalog_id,e.trade_id,e.kind,e.business_key)=(catalog,trade,command_kind,btrim(body->>'businessKey')) ORDER BY e.version DESC LIMIT 1;
 fact=trading.crude_operational_fact(body);
 IF prior.id IS NOT NULL THEN
  SELECT trading.crude_operational_fact(c.request) INTO oldfact
  FROM trading.crude_lifecycle_commands c WHERE(c.catalog_id,c.outcome->>'eventId')=(catalog,prior.id::text) LIMIT 1;
  IF fact=oldfact THEN
   outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'eventId',prior.id,'version',prior.version,'unchanged',true);
  ELSIF command_kind<>'receipt' THEN
   IF command_kind='invoice' AND EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,prior.id)) THEN NULL;
   ELSIF command_kind='payment' AND EXISTS(SELECT 1 FROM trading.crude_payment_reversals v WHERE(v.catalog_id,v.payment_id)=(catalog,prior.id)) THEN NULL;
   ELSE RAISE EXCEPTION 'crude_correction_requires_reversal';END IF;
  ELSIF EXISTS(SELECT 1 FROM trading.crude_inventory_allocations x WHERE(x.catalog_id,x.receipt_id)=(catalog,prior.id)) OR EXISTS(SELECT 1 FROM trading.crude_dispatches d WHERE(d.catalog_id,d.receipt_id)=(catalog,prior.id)) OR EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE(x.catalog_id,x.receipt_id)=(catalog,prior.id) AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id))) THEN RAISE EXCEPTION 'crude_correction_requires_reversal';END IF;
 END IF;
 IF outcome IS NULL THEN
 IF EXISTS(SELECT 1 FROM trading.crude_operational_events e WHERE(e.catalog_id,e.trade_id)=(catalog,trade) AND NOT EXISTS(
 SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,e.publication_order)
 WHERE(t.catalog_id,t.id)=(catalog,context) AND e.publication_order<=x.known_through AND k.known_from<=t.known_at)) THEN RAISE EXCEPTION 'crude_lifecycle_context';END IF;
 IF version IS DISTINCT FROM(body->>'expectedVersion')::integer THEN RAISE EXCEPTION 'crude_conflict';END IF;
 IF EXISTS(SELECT 1 FROM trading.crude_operational_events e WHERE(e.catalog_id,e.trade_id)=(catalog,trade) AND e.effective_at>at_time) THEN RAISE EXCEPTION 'crude_backdated_unsupported';END IF;
 IF version>=10000 THEN RAISE EXCEPTION 'crude_lifecycle_limit';END IF;
 IF command_kind='receipt' THEN
  IF qty IS NULL OR qty<=0 OR qty>=1e24 OR scale(trim_scale(qty))>18 OR (body->>'titleTransferred')::boolean IS DISTINCT FROM true
  OR body->>'disposition' NOT IN('owned_inventory','onward_delivery') OR body->>'disposition' IS NULL OR at_time<i.delivery_from OR at_time>=i.delivery_to THEN RAISE EXCEPTION 'crude_receipt_input';END IF;
  SELECT coalesce(sum(x.quantity),0) INTO used FROM trading.crude_receipts x WHERE(x.catalog_id,x.trade_id)=(catalog,trade) AND x.id IS DISTINCT FROM prior.id
  AND NOT EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(x.catalog_id,x.id));
  IF used+qty>h.quantity THEN RAISE EXCEPTION 'crude_receipt_capacity';END IF;
  IF body->>'disposition'='onward_delivery' THEN
   PERFORM security.require_crude_lifecycle(catalog,(body->>'saleTradeId')::uuid,false);
   SELECT * INTO s FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,(body->>'saleTradeId')::uuid,(body->>'saleRevision')::integer);
   SELECT * INTO si FROM trading.crude_instruments WHERE(catalog_id,instrument_id)=(catalog,s.instrument_id);
   SELECT * INTO sa FROM market_data.crude_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,si.definition_revision_id,si.assignment_code);
   IF s.trade_id IS NULL OR s.direction<>-1 OR(s.legal_entity_id,s.legal_entity_revision) IS DISTINCT FROM(h.legal_entity_id,h.legal_entity_revision)
   OR(sa.reference_catalog_id,sa.reference_revision_id,sa.commodity_code,sa.grade_code,sa.facility_code,sa.custody_point_code) IS DISTINCT FROM(a.reference_catalog_id,a.reference_revision_id,a.commodity_code,a.grade_code,a.facility_code,a.custody_point_code)
   OR at_time>=si.delivery_to OR s.effective_at>at_time THEN RAISE EXCEPTION 'crude_onward_contract';END IF;
   PERFORM security.require_crude_trade_context(catalog,s.trade_id,s.revision,context,at_time);
   PERFORM security.require_crude_sale_capacity_context(catalog,s.trade_id,s.revision,context,at_time);
   SELECT coalesce(sum(x.quantity),0) INTO used FROM trading.crude_receipts x WHERE(x.catalog_id,x.sale_trade_id,x.sale_revision)=(catalog,s.trade_id,s.revision) AND x.id IS DISTINCT FROM prior.id
   AND NOT EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(x.catalog_id,x.id));
   IF used+qty+coalesce((SELECT sum(x.quantity) FROM trading.crude_inventory_allocations x WHERE(x.catalog_id,x.sale_trade_id,x.sale_revision)=(catalog,s.trade_id,s.revision)),0)>s.quantity THEN RAISE EXCEPTION 'crude_onward_capacity';END IF;
  ELSIF body->>'saleTradeId' IS NOT NULL OR body->>'saleRevision' IS NOT NULL THEN RAISE EXCEPTION 'crude_receipt_input';END IF;
 ELSIF command_kind='allocation' THEN
  SELECT * INTO r FROM trading.crude_receipts WHERE(catalog_id,id,trade_id)=(catalog,(body->>'receiptId')::uuid,trade);
  IF r.id IS NULL OR r.disposition<>'owned_inventory' OR EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(catalog,r.id)) OR qty IS NULL OR qty<=0 THEN RAISE EXCEPTION 'crude_allocation_receipt';END IF;
  PERFORM security.require_crude_event_context(catalog,r.id,context,at_time);
  SELECT coalesce(sum(x.quantity),0) INTO used FROM trading.crude_inventory_allocations x WHERE(x.catalog_id,x.receipt_id)=(catalog,r.id);
  IF used+qty>r.quantity THEN RAISE EXCEPTION 'crude_allocation_capacity';END IF;
  PERFORM security.require_crude_lifecycle(catalog,(body->>'saleTradeId')::uuid,false);
  SELECT * INTO s FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,(body->>'saleTradeId')::uuid,(body->>'saleRevision')::integer);
  SELECT * INTO si FROM trading.crude_instruments WHERE(catalog_id,instrument_id)=(catalog,s.instrument_id);
  SELECT * INTO sa FROM market_data.crude_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,si.definition_revision_id,si.assignment_code);
  IF s.trade_id IS NULL OR s.direction<>-1 OR(s.legal_entity_id,s.legal_entity_revision) IS DISTINCT FROM(h.legal_entity_id,h.legal_entity_revision)
  OR(sa.reference_catalog_id,sa.reference_revision_id,sa.commodity_code,sa.grade_code,sa.facility_code,sa.custody_point_code) IS DISTINCT FROM(a.reference_catalog_id,a.reference_revision_id,a.commodity_code,a.grade_code,a.facility_code,a.custody_point_code)
  OR at_time>=si.delivery_to OR s.effective_at>at_time THEN RAISE EXCEPTION 'crude_onward_contract';END IF;
  PERFORM security.require_crude_trade_context(catalog,s.trade_id,s.revision,context,at_time);
   PERFORM security.require_crude_sale_capacity_context(catalog,s.trade_id,s.revision,context,at_time);
  IF si.consideration_direction<>1 OR si.fixed_price<=0 OR si.handling_fee<0 OR NOT EXISTS(SELECT 1 FROM market_data.curves v
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(sa.reference_catalog_id,sa.reference_revision_id,v.quote_code)
  WHERE(v.catalog_id,v.revision_id,v.code)=(catalog,si.definition_revision_id,sa.curve_code) AND q.multiplier=1) THEN RAISE EXCEPTION 'crude_sale_profile';END IF;
  SELECT coalesce(sum(x.quantity),0) INTO used FROM trading.crude_inventory_allocations x WHERE(x.catalog_id,x.sale_trade_id,x.sale_revision)=(catalog,s.trade_id,s.revision);
  SELECT used+coalesce(sum(x.quantity),0) INTO used FROM trading.crude_receipts x WHERE(x.catalog_id,x.sale_trade_id,x.sale_revision)=(catalog,s.trade_id,s.revision)
  AND NOT EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(x.catalog_id,x.id));
  IF used+qty>s.quantity THEN RAISE EXCEPTION 'crude_onward_capacity';END IF;
 ELSIF command_kind IN('dispatch','invoice') THEN
  IF sale_profile THEN
   SELECT * INTO binding FROM trading.crude_dispatch_sales x WHERE(x.catalog_id,x.dispatch_id,x.sale_trade_id,x.sale_revision)=(catalog,(body->>'dispatchId')::uuid,trade,h.revision);
   IF binding.dispatch_id IS NULL THEN RAISE EXCEPTION 'crude_sale_dispatch';END IF;
   PERFORM security.require_crude_lifecycle(catalog,binding.purchase_trade_id,false);
   PERFORM security.require_crude_event_context(catalog,binding.dispatch_id,context,at_time);
   SELECT * INTO dispatch FROM trading.crude_dispatches WHERE(catalog_id,id)=(catalog,binding.dispatch_id);
   evidence_quantity=dispatch.quantity;
  ELSE
  SELECT * INTO r FROM trading.crude_receipts WHERE(catalog_id,id,trade_id)=(catalog,(body->>'receiptId')::uuid,trade);
  IF r.id IS NULL OR EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(catalog,r.id)) THEN RAISE EXCEPTION 'crude_receipt_revision';END IF;
  PERFORM security.require_crude_event_context(catalog,r.id,context,at_time);
  evidence_quantity=r.quantity;
  END IF;
  IF command_kind='dispatch' THEN
   IF(body->>'titleTransferred')::boolean IS DISTINCT FROM true OR qty IS NULL OR qty<=0 THEN RAISE EXCEPTION 'crude_dispatch_input';END IF;
   IF body->>'allocationId' IS NOT NULL THEN
    SELECT * INTO allocation FROM trading.crude_inventory_allocations WHERE(catalog_id,id,trade_id,receipt_id)=(catalog,(body->>'allocationId')::uuid,trade,r.id);
    IF allocation.id IS NULL OR r.disposition<>'owned_inventory' THEN RAISE EXCEPTION 'crude_dispatch_allocation';END IF;
    PERFORM security.require_crude_event_context(catalog,allocation.id,context,at_time);
    SELECT * INTO s FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,allocation.sale_trade_id,allocation.sale_revision);
   ELSE
    IF r.disposition<>'onward_delivery' THEN RAISE EXCEPTION 'crude_dispatch_input';END IF;
    SELECT * INTO s FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,r.sale_trade_id,r.sale_revision);
   END IF;
   PERFORM security.require_crude_lifecycle(catalog,s.trade_id,false);
   PERFORM security.require_crude_trade_context(catalog,s.trade_id,s.revision,context,at_time);
   SELECT * INTO si FROM trading.crude_instruments WHERE(catalog_id,instrument_id)=(catalog,s.instrument_id);
   IF at_time<si.delivery_from OR at_time>=si.delivery_to THEN RAISE EXCEPTION 'crude_dispatch_window';END IF;
   IF(SELECT count(*) FROM trading.crude_dispatch_sales x WHERE(x.catalog_id,x.sale_trade_id,x.sale_revision)=(catalog,s.trade_id,s.revision))>=10000 THEN RAISE EXCEPTION 'crude_sale_delivery_limit';END IF;
   SELECT coalesce(sum(d.quantity),0) INTO used FROM trading.crude_dispatches d WHERE(d.catalog_id,d.receipt_id)=(catalog,r.id) AND d.allocation_id IS NOT DISTINCT FROM allocation.id;
   IF used+qty>coalesce(allocation.quantity,r.quantity) THEN RAISE EXCEPTION 'crude_dispatch_capacity';END IF;
  ELSE
   component_slot=CASE body->>'component' WHEN 'consideration' THEN 1 WHEN 'handling_fee' THEN 2 END;
   price=CASE component_slot WHEN 1 THEN i.fixed_price WHEN 2 THEN i.handling_fee END;direction=CASE component_slot WHEN 1 THEN i.consideration_direction WHEN 2 THEN i.handling_fee_direction END;
   IF component_slot IS NULL OR price IS NULL OR body->>'roundingPolicy' IS DISTINCT FROM 'crude_cumulative_receipt_cash_v1' THEN RAISE EXCEPTION 'crude_invoice_input';END IF;
   SELECT q.currency_code,c.display_minor_units,q.multiplier INTO currency,places,multiplier FROM market_data.curves v
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,v.quote_code)
   JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.currency_code)
   WHERE(v.catalog_id,v.revision_id,v.code)=(catalog,i.definition_revision_id,a.curve_code);
   IF places IS NULL OR places IS DISTINCT FROM(body->>'decimalPlaces')::integer THEN RAISE EXCEPTION 'crude_invoice_rounding';END IF;
   SELECT * INTO last_invoice FROM trading.crude_invoices x WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,component_slot) AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id)) ORDER BY ordinal DESC LIMIT 1;
   SELECT coalesce(max(x.ordinal),0)+1 INTO next_ordinal FROM trading.crude_invoices x WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,component_slot);
   used=coalesce(last_invoice.cumulative_quantity,0);exact_amount=evidence_quantity*price;
   IF used+evidence_quantity>h.quantity THEN RAISE EXCEPTION 'crude_invoice_capacity';END IF;
   rounded=trading.crude_round_cash((used+evidence_quantity)*price,places)-trading.crude_round_cash(used*price,places);
   IF EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE x.catalog_id=catalog AND x.slot=component_slot AND ((NOT sale_profile AND x.receipt_id=r.id) OR(sale_profile AND x.dispatch_id=dispatch.id)) AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id))) THEN RAISE EXCEPTION 'crude_invoice_exists';END IF;
  END IF;
 ELSIF command_kind='payment' THEN
  SELECT * INTO inv FROM trading.crude_invoices WHERE(catalog_id,id,trade_id)=(catalog,(body->>'invoiceId')::uuid,trade);
  amount=(body->>'amount')::numeric;direction=CASE body->>'direction' WHEN 'pay' THEN -1 WHEN 'receive' THEN 1 END;
  IF inv.id IS NULL OR EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,inv.id)) OR amount IS NULL OR amount<=0 OR scale(trim_scale(amount))>inv.decimal_places OR direction IS DISTINCT FROM inv.direction THEN RAISE EXCEPTION 'crude_payment_input';END IF;
  SELECT coalesce(sum(p.amount),0) INTO used FROM trading.crude_payments p WHERE(p.catalog_id,p.invoice_id)=(catalog,inv.id) AND NOT EXISTS(SELECT 1 FROM trading.crude_payment_reversals v WHERE(v.catalog_id,v.payment_id)=(catalog,p.id));
  IF used+amount>inv.amount THEN RAISE EXCEPTION 'crude_payment_capacity';END IF;
 ELSIF command_kind='payment_reversal' THEN
  SELECT * INTO payment FROM trading.crude_payments p WHERE(p.catalog_id,p.id,p.trade_id)=(catalog,(body->>'paymentId')::uuid,trade);
  IF payment.id IS NULL THEN RAISE EXCEPTION 'crude_reversal_target';END IF;
  IF EXISTS(SELECT 1 FROM trading.crude_payment_reversals v WHERE(v.catalog_id,v.payment_id)=(catalog,payment.id)) THEN RAISE EXCEPTION 'crude_record_already_reversed';END IF;
 ELSE
  SELECT * INTO inv FROM trading.crude_invoices x WHERE(x.catalog_id,x.id,x.trade_id)=(catalog,(body->>'invoiceId')::uuid,trade);
  IF inv.id IS NULL THEN RAISE EXCEPTION 'crude_reversal_target';END IF;
  IF EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,inv.id)) THEN RAISE EXCEPTION 'crude_record_already_reversed';END IF;
  IF EXISTS(SELECT 1 FROM trading.crude_payments p WHERE(p.catalog_id,p.invoice_id)=(catalog,inv.id) AND NOT EXISTS(SELECT 1 FROM trading.crude_payment_reversals v WHERE(v.catalog_id,v.payment_id)=(catalog,p.id))) THEN RAISE EXCEPTION 'crude_invoice_active_payments';END IF;
  IF EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,inv.slot) AND x.ordinal>inv.ordinal AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id))) THEN RAISE EXCEPTION 'crude_invoice_void_order';END IF;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 INSERT INTO trading.crude_operational_events VALUES(catalog,identity,trade,h.revision,version+1,command_kind,btrim(body->>'businessKey'),body->>'profile',at_time,btrim(body->>'evidence'),context,actor,publication,prior.id);
 IF command_kind='receipt' THEN
  INSERT INTO trading.crude_receipts VALUES(catalog,identity,trade,coalesce((SELECT revision FROM trading.crude_receipts WHERE(catalog_id,id)=(catalog,prior.id)),0)+1,prior.id,qty,true,body->>'disposition',(body->>'saleTradeId')::uuid,(body->>'saleRevision')::integer);
 ELSIF command_kind='allocation' THEN INSERT INTO trading.crude_inventory_allocations VALUES(catalog,identity,trade,r.id,s.trade_id,s.revision,qty);
 ELSIF command_kind='dispatch' THEN
  INSERT INTO trading.crude_dispatches VALUES(catalog,identity,trade,r.id,qty,true,allocation.id);
  INSERT INTO trading.crude_dispatch_sales VALUES(catalog,identity,trade,s.trade_id,s.revision);
 ELSIF command_kind='invoice' THEN INSERT INTO trading.crude_invoices VALUES(catalog,identity,trade,r.id,h.instrument_id,component_slot,next_ordinal,last_invoice.id,evidence_quantity,used+evidence_quantity,exact_amount,rounded,direction,a.reference_catalog_id,a.reference_revision_id,currency,places,'crude_cumulative_receipt_cash_v1',i.payment_at,dispatch.id,CASE WHEN sale_profile THEN h.revision END);
 ELSIF command_kind='payment' THEN INSERT INTO trading.crude_payments VALUES(catalog,identity,trade,inv.id,amount,direction);
 ELSIF command_kind='payment_reversal' THEN INSERT INTO trading.crude_payment_reversals VALUES(catalog,identity,trade,payment.id,'crude_record_correction_v1');
 ELSE INSERT INTO trading.crude_invoice_voids VALUES(catalog,identity,trade,inv.id,'crude_record_correction_v1');END IF;
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'eventId',identity,'version',version+1,'unchanged',false);
 END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 INSERT INTO trading.crude_lifecycle_commands VALUES(catalog,operation,trade,actor,body||jsonb_build_object('commandKind',command_kind),outcome);
 IF command_kind='receipt' AND body->>'saleTradeId' IS NOT NULL THEN PERFORM security.require_crude_lifecycle(catalog,(body->>'saleTradeId')::uuid,false);END IF;
 IF command_kind IN('dispatch','allocation') AND s.trade_id IS NOT NULL THEN PERFORM security.require_crude_lifecycle(catalog,s.trade_id,false);END IF;
 IF command_kind='allocation' OR(command_kind='receipt' AND body->>'saleTradeId' IS NOT NULL) THEN
  FOR source IN SELECT * FROM trading.crude_sale_reservations(catalog,(body->>'saleTradeId')::uuid,(body->>'saleRevision')::integer)
  LOOP PERFORM security.require_crude_lifecycle(catalog,source.trade_id,false);END LOOP;
 END IF;
 IF binding.purchase_trade_id IS NOT NULL THEN PERFORM security.require_crude_lifecycle(catalog,binding.purchase_trade_id,false);END IF;
 PERFORM security.require_crude_lifecycle(catalog,trade,true);RETURN outcome;
END$$;
ALTER FUNCTION security.read_crude_lifecycle(uuid,uuid) RENAME TO read_crude_receipt_cash;
REVOKE ALL ON FUNCTION security.read_crude_receipt_cash(uuid,uuid) FROM PUBLIC,openetrm_runtime;
CREATE FUNCTION security.read_crude_lifecycle(catalog uuid,trade uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;source uuid;sale_profile boolean;BEGIN
 result=security.read_crude_receipt_cash(catalog,trade);
 sale_profile=(result#>>'{head,direction}')::integer=-1;
 result=result||jsonb_build_object('profile',CASE WHEN sale_profile THEN 'physical_crude_sale_cash_v1' ELSE 'physical_crude_receipt_cash_v1' END,
 'allocations',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(a)||jsonb_build_object('quantity',trim_scale(a.quantity)::text) ORDER BY e.effective_at,e.publication_order,a.id)
 FROM trading.crude_inventory_allocations a JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id)
 WHERE a.catalog_id=catalog AND(CASE WHEN sale_profile THEN a.sale_trade_id=trade ELSE a.trade_id=trade END)
 AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb));
 IF sale_profile THEN
  result=jsonb_set(result,'{dispatches}',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(d)||to_jsonb(b)||jsonb_build_object('id',d.id,'quantity',trim_scale(d.quantity)::text) ORDER BY e.effective_at,e.publication_order,d.id)
  FROM trading.crude_dispatch_sales b JOIN trading.crude_dispatches d ON(d.catalog_id,d.id)=(b.catalog_id,b.dispatch_id)
  JOIN trading.crude_operational_events e ON(e.catalog_id,e.id)=(d.catalog_id,d.id)
  WHERE(b.catalog_id,b.sale_trade_id,b.sale_revision)=(catalog,trade,(result#>>'{head,revision}')::integer)
  AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb));
 END IF;
 -- Every selected cross-trade source must still be readable. Fail the complete
 -- read rather than showing a partial inventory or invoice evidence population.
 FOR source IN SELECT DISTINCT value FROM(
  SELECT (r->>'trade_id')::uuid value FROM jsonb_array_elements(result->'allocations') r
  UNION SELECT (r->>'sale_trade_id')::uuid FROM jsonb_array_elements(result->'allocations') r
  UNION SELECT (r->>'trade_id')::uuid FROM jsonb_array_elements(result->'dispatches') r
  UNION SELECT (r->>'sale_trade_id')::uuid FROM jsonb_array_elements(result->'receipts') r WHERE r->>'sale_trade_id' IS NOT NULL
 ) sources WHERE value IS NOT NULL LOOP PERFORM security.require_crude_lifecycle(catalog,source,false);END LOOP;
 PERFORM security.require_crude_lifecycle(catalog,trade,false);RETURN result;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='security' AND p.proname IN('require_crude_event_context','require_crude_trade_context','require_crude_sale_capacity_context','read_crude_lifecycle','record_crude_lifecycle') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.signature::text LIKE '%read_crude_lifecycle%' OR f.signature::text LIKE '%record_crude_lifecycle%' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
