-- Full record corrections retain originals and never imply a bank refund.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.crude_operational_events DROP CONSTRAINT crude_operational_events_kind_check;
ALTER TABLE trading.crude_operational_events ADD CHECK(kind IN('receipt','dispatch','invoice','payment','payment_reversal','invoice_void'));
ALTER TABLE trading.crude_operational_events ADD UNIQUE(catalog_id,id,trade_id,kind);
ALTER TABLE trading.crude_operational_events ADD COLUMN corrects_id uuid;
ALTER TABLE trading.crude_operational_events ADD FOREIGN KEY(catalog_id,corrects_id,trade_id,kind) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id,kind);
ALTER TABLE trading.crude_payments ADD UNIQUE(catalog_id,id,trade_id);
-- Active receipt/component uniqueness is checked under the publication lock;
-- original voided invoices remain retained with their original unique ordinal.
ALTER TABLE trading.crude_invoices DROP CONSTRAINT crude_invoices_catalog_id_receipt_id_slot_key;
CREATE TABLE trading.crude_payment_reversals(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,payment_id uuid NOT NULL,
 reversal_policy text NOT NULL CHECK(reversal_policy='crude_record_correction_v1'),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,payment_id),
 FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,payment_id,trade_id) REFERENCES trading.crude_payments(catalog_id,id,trade_id)
);
CREATE TABLE trading.crude_invoice_voids(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,invoice_id uuid NOT NULL,
 reversal_policy text NOT NULL CHECK(reversal_policy='crude_record_correction_v1'),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,invoice_id),
 FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,invoice_id,trade_id) REFERENCES trading.crude_invoices(catalog_id,id,trade_id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_payment_reversals','crude_invoice_voids'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades''))',t);
  EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.crude_operational_events e WHERE(e.catalog_id,e.id)=(%I.catalog_id,%I.id)))',t,t,t);
 END LOOP;
END$$;
-- Refuse drift rather than patch an unqualified predecessor.
DO $migration$
DECLARE definition text=pg_get_functiondef('security.record_crude_lifecycle(uuid,text,jsonb,timestamptz,uuid)'::regprocedure);source text;old text;replacement text;
BEGIN
 SELECT prosrc INTO source FROM pg_proc WHERE oid='security.record_crude_lifecycle(uuid,text,jsonb,timestamptz,uuid)'::regprocedure;
 IF encode(sha256(convert_to(source,'UTF8')),'hex')<>'cf42b7224c74255a7dac2d3a83099149499b03ea95d39f0b863c559c3d3027e0' THEN RAISE EXCEPTION 'crude_reversal_predecessor';END IF;
 old=$old$last_invoice trading.crude_invoices;$old$;replacement=$new$last_invoice trading.crude_invoices;payment trading.crude_payments;next_ordinal integer;$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$'receipt','dispatch','invoice','payment'$old$;replacement=$new$'receipt','dispatch','invoice','payment','payment_reversal','invoice_void'$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$WHEN 'payment' THEN ARRAY['invoiceId','amount','direction']$old$;replacement=$new$WHEN 'payment' THEN ARRAY['invoiceId','amount','direction'] WHEN 'payment_reversal' THEN ARRAY['paymentId','reversalPolicy'] WHEN 'invoice_void' THEN ARRAY['invoiceId','reversalPolicy']$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>2 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$ OR(command_kind='invoice' AND jsonb_typeof(body->'decimalPlaces') IS DISTINCT FROM 'number')$old$;replacement=$new$ OR(command_kind='invoice' AND jsonb_typeof(body->'decimalPlaces') IS DISTINCT FROM 'number')
 OR(command_kind IN('payment_reversal','invoice_void') AND body->>'reversalPolicy' IS DISTINCT FROM 'crude_record_correction_v1')$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$  ELSIF command_kind<>'receipt' THEN RAISE EXCEPTION 'crude_correction_requires_reversal';$old$;replacement=$new$  ELSIF command_kind<>'receipt' THEN
   IF command_kind='invoice' AND EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,prior.id)) THEN NULL;
   ELSIF command_kind='payment' AND EXISTS(SELECT 1 FROM trading.crude_payment_reversals v WHERE(v.catalog_id,v.payment_id)=(catalog,prior.id)) THEN NULL;
   ELSE RAISE EXCEPTION 'crude_correction_requires_reversal';END IF;$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE(x.catalog_id,x.receipt_id)=(catalog,prior.id))$old$;replacement=$new$EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE(x.catalog_id,x.receipt_id)=(catalog,prior.id) AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id)))$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,component_slot) ORDER BY ordinal DESC LIMIT 1;$old$;replacement=$new$WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,component_slot) AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id)) ORDER BY ordinal DESC LIMIT 1;
   SELECT coalesce(max(x.ordinal),0)+1 INTO next_ordinal FROM trading.crude_invoices x WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,component_slot);$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$WHERE(x.catalog_id,x.receipt_id,x.slot)=(catalog,r.id,component_slot))$old$;replacement=$new$WHERE(x.catalog_id,x.receipt_id,x.slot)=(catalog,r.id,component_slot) AND NOT EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,x.id)))$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$ ELSE
  SELECT * INTO inv FROM trading.crude_invoices$old$;replacement=$new$ ELSIF command_kind='payment' THEN
  SELECT * INTO inv FROM trading.crude_invoices$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$IF inv.id IS NULL OR amount IS NULL$old$;replacement=$new$IF inv.id IS NULL OR EXISTS(SELECT 1 FROM trading.crude_invoice_voids v WHERE(v.catalog_id,v.invoice_id)=(catalog,inv.id)) OR amount IS NULL$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$WHERE(p.catalog_id,p.invoice_id)=(catalog,inv.id);$old$;replacement=$new$WHERE(p.catalog_id,p.invoice_id)=(catalog,inv.id) AND NOT EXISTS(SELECT 1 FROM trading.crude_payment_reversals v WHERE(v.catalog_id,v.payment_id)=(catalog,p.id));$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$  IF used+amount>inv.amount THEN RAISE EXCEPTION 'crude_payment_capacity';END IF;
 END IF;$old$;replacement=$new$  IF used+amount>inv.amount THEN RAISE EXCEPTION 'crude_payment_capacity';END IF;
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
 END IF;$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$context,actor,publication);$old$;replacement=$new$context,actor,publication,prior.id);$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$coalesce(last_invoice.ordinal,0)+1,last_invoice.id$old$;replacement=$new$next_ordinal,last_invoice.id$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 old=$old$ ELSE INSERT INTO trading.crude_payments VALUES(catalog,identity,trade,inv.id,amount,direction);END IF;$old$;replacement=$new$ ELSIF command_kind='payment' THEN INSERT INTO trading.crude_payments VALUES(catalog,identity,trade,inv.id,amount,direction);
 ELSIF command_kind='payment_reversal' THEN INSERT INTO trading.crude_payment_reversals VALUES(catalog,identity,trade,payment.id,'crude_record_correction_v1');
 ELSE INSERT INTO trading.crude_invoice_voids VALUES(catalog,identity,trade,inv.id,'crude_record_correction_v1');END IF;$new$;
 IF (length(definition)-length(replace(definition,old,'')))/length(old)<>1 THEN RAISE EXCEPTION 'crude_reversal_patch';END IF;
 definition=replace(definition,old,replacement);
 EXECUTE definition;
END$migration$;
-- Numeric division in the half-even branch can retain non-economic trailing
-- zeros beyond the exact-number domain's scale. Normalize without rounding again.
CREATE OR REPLACE FUNCTION trading.crude_round_cash(value numeric,places integer) RETURNS numeric LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trim_scale(CASE WHEN abs(value*power(10::numeric,places)-trunc(value*power(10::numeric,places)))=0.5
 THEN (trunc(value*power(10::numeric,places))+CASE WHEN mod(abs(trunc(value*power(10::numeric,places))),2)=1 THEN sign(value) ELSE 0 END)/power(10::numeric,places)
 ELSE round(value,places) END)
$$;
-- UUID equivalence also applies to the newly supported payment correction target.
CREATE OR REPLACE FUNCTION trading.crude_operational_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body-ARRAY['context','operationId','expectedVersion','expectedRevision','previewHash','commandKind'];key text;BEGIN
 result=result||jsonb_build_object('effectiveAt',extract(epoch FROM (body->>'effectiveAt')::timestamptz),'businessKey',btrim(body->>'businessKey'),'evidence',btrim(body->>'evidence'));
 FOREACH key IN ARRAY ARRAY['tradeId','receiptId','invoiceId','paymentId','saleTradeId'] LOOP
  IF body?key THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 IF body?'quantity' THEN result=result||jsonb_build_object('quantity',(body->>'quantity')::numeric);END IF;
 IF body?'amount' THEN result=result||jsonb_build_object('amount',(body->>'amount')::numeric);END IF;
 RETURN result;
END$$;
CREATE FUNCTION security.crude_lifecycle_business_match(catalog uuid,command_kind text,body jsonb) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior uuid;result boolean;BEGIN
 PERFORM security.require_crude_lifecycle(catalog,(body->>'tradeId')::uuid,false);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE THEN RAISE EXCEPTION 'crude_lifecycle_denied' USING ERRCODE='42501';END IF;
 SELECT e.id INTO prior FROM trading.crude_operational_events e
 WHERE(e.catalog_id,e.trade_id,e.kind,e.business_key)=(catalog,(body->>'tradeId')::uuid,command_kind,btrim(body->>'businessKey'))
 AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at) ORDER BY e.version DESC LIMIT 1;
 SELECT EXISTS(SELECT 1 FROM trading.crude_lifecycle_commands c WHERE(c.catalog_id,c.outcome->>'eventId')=(catalog,prior::text)
 AND trading.crude_operational_fact(c.request)=trading.crude_operational_fact(body)) INTO result;
 RETURN result;
END$$;
ALTER FUNCTION security.read_crude_lifecycle(uuid,uuid) RENAME TO read_crude_lifecycle_original;
REVOKE ALL ON FUNCTION security.read_crude_lifecycle_original(uuid,uuid) FROM PUBLIC,openetrm_runtime;
CREATE FUNCTION security.read_crude_lifecycle(catalog uuid,trade uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 result=security.read_crude_lifecycle_original(catalog,trade);
 result=result||jsonb_build_object(
 'reversalPolicy','crude_record_correction_v1',
 'paymentReversals',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(r) ORDER BY e.version) FROM trading.crude_payment_reversals r
 JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id) WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb),
 'invoiceVoids',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(v) ORDER BY e.version) FROM trading.crude_invoice_voids v
 JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id) WHERE(v.catalog_id,v.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb));
 result=jsonb_set(result,'{payments}',coalesce((SELECT jsonb_agg(p||jsonb_build_object('active',NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'paymentReversals') r WHERE r->>'payment_id'=p->>'id'),
 'canReverse',NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'paymentReversals') r WHERE r->>'payment_id'=p->>'id')) ORDER BY (p->>'version')::integer)
 FROM jsonb_array_elements(result->'payments') p),'[]'::jsonb));
 result=jsonb_set(result,'{invoices}',coalesce((SELECT jsonb_agg(i||jsonb_build_object(
 'active',NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'invoiceVoids') v WHERE v->>'invoice_id'=i->>'id'),
 'canVoid',NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'invoiceVoids') v WHERE v->>'invoice_id'=i->>'id')
 AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'payments') p WHERE p->>'invoice_id'=i->>'id' AND(p->>'active')::boolean)
 AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'invoices') later WHERE later->>'slot'=i->>'slot' AND(later->>'ordinal')::integer>(i->>'ordinal')::integer
 AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'invoiceVoids') v WHERE v->>'invoice_id'=later->>'id')))
 ORDER BY(i->>'version')::integer) FROM jsonb_array_elements(result->'invoices') i),'[]'::jsonb));
 PERFORM security.require_crude_lifecycle(catalog,trade,false);RETURN result;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='security' AND p.proname IN('crude_lifecycle_business_match','read_crude_lifecycle') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
COMMIT;
