-- Native producers and certified independent-holding reads. Product commands
-- remain private until the policy-bound coordinator in 0266 admits them.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION security.require_gas_owned_read(catalog uuid,context uuid,at_time timestamptz,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_owned(catalog,false,false);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at OR
 security.trading_context_visible(catalog,context) IS NOT TRUE OR NOT EXISTS(
 SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
 AND q.runtime_login=session_user AND(q.market_catalog_id,q.market_context_id,q.trading_as_of)=(catalog,context,at_time) AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'gas_owned_context' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.read_gas_owned_holdings(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;cutoff timestamptz;day date;result jsonb;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);
 SELECT known_at,effective_on INTO STRICT cutoff,day FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF books IS NULL OR cardinality(books)>1000 OR EXISTS(SELECT 1 FROM unnest(books)b WHERE NOT EXISTS(
  SELECT 1 FROM trading.books k WHERE(k.catalog_id,k.id)=(catalog,b) AND security.trading_visible(k.catalog_id,k.publication_order,k.effective_at)))
 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND(cardinality(books)=0 OR a.book_id=ANY(books))
 AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at) AND(
  security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE OR
  security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 WITH accounts AS(SELECT a.* FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND(cardinality(books)=0 OR a.book_id=ANY(books))
  AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)),
 held AS(SELECT p.*,a.book_id,a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,
  a.quantity_code,a.location_code,a.currency_code,
  p.quantity-CASE p.kind
   WHEN 'owned_stock' THEN coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.lot_id)=(p.catalog_id,p.id)
    AND security.trading_visible(c.catalog_id,c.publication_order,c.effective_at)),0)
   WHEN 'loan_return' THEN -coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_repayments r JOIN trading.gas_owned_loans l ON(l.catalog_id,l.id)=(r.catalog_id,r.loan_id)
    JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(r.catalog_id,r.consumption_id) WHERE(l.catalog_id,l.return_position_id)=(p.catalog_id,p.id)
    AND security.trading_visible(c.catalog_id,c.publication_order,c.effective_at)),0)
   ELSE sign(p.quantity)*coalesce((SELECT sum(s.quantity) FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.position_id)=(p.catalog_id,p.id)
    AND security.trading_visible(s.catalog_id,s.publication_order,s.effective_at)),0) END remaining_quantity,
  EXISTS(SELECT 1 FROM trading.gas_owned_cashout_terms terms WHERE(terms.catalog_id,terms.position_id)=(p.catalog_id,p.id) AND terms.observed_at<=at_time) requires_cashout_determination,
  EXISTS(SELECT 1 FROM trading.gas_owned_evidence original JOIN trading.gas_owned_evidence corrected
   ON(corrected.catalog_id,corrected.account_id,corrected.source_key)=(original.catalog_id,original.account_id,original.source_key)
   WHERE(original.catalog_id,original.id)=(p.catalog_id,p.evidence_id) AND corrected.revision>original.revision
   AND security.trading_visible(corrected.catalog_id,corrected.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')) requires_reconciliation
  FROM trading.gas_owned_positions p JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  WHERE security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)),
 fees AS(SELECT f.*,p.account_id,a.book_id,a.definition_revision_id FROM trading.gas_owned_fees f
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  WHERE security.trading_visible(f.catalog_id,f.publication_order,f.effective_at)),
 payments AS(SELECT p.*,k.known_from known_at,first_value(p.id) OVER(PARTITION BY p.catalog_id,p.fee_id,p.business_key ORDER BY p.revision) allocation_id
  FROM trading.gas_owned_fee_payments p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  JOIN fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id)
  WHERE security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))
 SELECT jsonb_build_object('contextId',context,'asOf',at_time,'knownAt',cutoff,'effectiveOn',day,'authorityUntil',until_at,
  'positions',coalesce((SELECT jsonb_agg(to_jsonb(h)||jsonb_build_object('quantity',trim_scale(h.quantity)::text,'remaining_quantity',trim_scale(h.remaining_quantity)::text)
   ORDER BY h.book_id,h.id) FROM held h WHERE h.remaining_quantity<>0),'[]'::jsonb),
  'fees',coalesce((SELECT jsonb_agg(to_jsonb(f)||jsonb_build_object('amount',trim_scale(f.amount)::text) ORDER BY f.id) FROM fees f),'[]'::jsonb),
  'payments',coalesce((SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('amount',trim_scale(p.amount)::text) ORDER BY p.fee_id,p.business_key,p.revision) FROM payments p),'[]'::jsonb)) INTO result;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);RETURN result;
END$$;

CREATE FUNCTION trading.guard_gas_owned_publication() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=
  (NEW.catalog_id,NEW.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_owned_publication';END IF;
 RETURN NEW;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_owned_accounts','gas_owned_evidence','gas_owned_positions','gas_owned_reservations','gas_owned_releases',
  'gas_owned_consumptions','gas_owned_claim_settlements','gas_owned_fees','gas_owned_fee_payments'] LOOP
  EXECUTE format('CREATE TRIGGER native_publication BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication()',t);
 END LOOP;
END$$;

CREATE FUNCTION trading.guard_gas_owned_child() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE publication integer;other integer;BEGIN
 CASE TG_TABLE_NAME
  WHEN 'gas_owned_lots' THEN SELECT p.publication_order INTO STRICT publication FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.id);
  WHEN 'gas_owned_loans' THEN
   SELECT p.publication_order INTO STRICT publication FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.received_lot_id);
   SELECT p.publication_order INTO STRICT other FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.return_position_id);
   IF publication<>other THEN RAISE EXCEPTION 'gas_owned_publication';END IF;
  WHEN 'gas_owned_repayments' THEN SELECT p.publication_order INTO STRICT publication FROM trading.gas_owned_consumptions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.consumption_id);
  WHEN 'gas_owned_cashout_terms' THEN SELECT p.publication_order INTO STRICT publication FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.position_id);
 END CASE;
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(NEW.catalog_id,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_owned_publication';END IF;
 RETURN NEW;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_owned_lots','gas_owned_loans','gas_owned_repayments','gas_owned_cashout_terms'] LOOP
  EXECUTE format('CREATE TRIGGER native_child BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_child()',t);
 END LOOP;
END$$;

CREATE FUNCTION trading.check_gas_owned_integrity() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cat uuid=NEW.catalog_id;lot record;boundary timestamptz;reserved numeric;used numeric;BEGIN
 FOR lot IN SELECT p.*,l.title_at,l.available_at,a.kind account_kind FROM trading.gas_owned_lots l
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id,p.account_id)=(l.catalog_id,l.id,l.account_id)
  JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id) WHERE l.catalog_id=cat LOOP
  IF lot.kind<>'owned_stock' OR lot.quantity<=0 OR lot.account_kind<>'owned_inventory' THEN RAISE EXCEPTION 'gas_stock_context';END IF;
  SELECT coalesce(sum(c.quantity),0) INTO used FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.lot_id)=(cat,lot.id);
  IF used>lot.quantity THEN RAISE EXCEPTION 'gas_stock_capacity';END IF;
  IF EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id)
   WHERE(c.catalog_id,c.lot_id)=(cat,lot.id) AND(c.effective_at<lot.available_at OR e.account_id<>lot.account_id OR e.occurred_at<>c.effective_at OR e.quantity=0
    OR e.kind<>(CASE c.kind WHEN 'loss' THEN 'physical_loss' WHEN 'return' THEN 'loan_return' ELSE 'physical_delivery' END)))
  THEN RAISE EXCEPTION 'gas_stock_availability';END IF;
  IF EXISTS(SELECT 1 FROM trading.gas_owned_reservations r WHERE(r.catalog_id,r.lot_id)=(cat,lot.id) AND(
   r.effective_at<lot.title_at OR coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_consumptions c
    WHERE(c.catalog_id,c.reservation_id)=(r.catalog_id,r.id)),0)>r.quantity OR EXISTS(
    SELECT 1 FROM trading.gas_owned_consumptions c LEFT JOIN trading.gas_owned_releases released
     ON(released.catalog_id,released.reservation_id)=(r.catalog_id,r.id)
    WHERE(c.catalog_id,c.reservation_id)=(r.catalog_id,r.id) AND(c.lot_id<>r.lot_id OR c.effective_at<r.effective_at OR released.effective_at<=c.effective_at))))
  THEN RAISE EXCEPTION 'gas_stock_reservation';END IF;
  IF lot.title_at<>lot.effective_at OR EXISTS(SELECT 1 FROM trading.gas_owned_releases x JOIN trading.gas_owned_reservations r
   ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id) WHERE(r.catalog_id,r.lot_id)=(cat,lot.id) AND x.effective_at<r.effective_at)
  THEN RAISE EXCEPTION 'gas_stock_reservation';END IF;
  FOR boundary IN SELECT r.effective_at FROM trading.gas_owned_reservations r WHERE(r.catalog_id,r.lot_id)=(cat,lot.id)
   UNION SELECT c.effective_at FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.lot_id)=(cat,lot.id)
   UNION SELECT x.effective_at FROM trading.gas_owned_releases x JOIN trading.gas_owned_reservations r ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id)
    WHERE(r.catalog_id,r.lot_id)=(cat,lot.id) LOOP
   SELECT coalesce(sum(c.quantity),0) INTO used FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.lot_id)=(cat,lot.id) AND c.effective_at<=boundary;
   SELECT coalesce(sum(r.quantity-coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_consumptions c
    WHERE(c.catalog_id,c.reservation_id)=(r.catalog_id,r.id) AND c.effective_at<=boundary),0)),0) INTO reserved FROM trading.gas_owned_reservations r
   WHERE(r.catalog_id,r.lot_id)=(cat,lot.id) AND r.effective_at<=boundary
    AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_releases x WHERE(x.catalog_id,x.reservation_id)=(r.catalog_id,r.id) AND x.effective_at<=boundary);
   IF reserved>lot.quantity-used THEN RAISE EXCEPTION 'gas_stock_reservation';END IF;
  END LOOP;
 END LOOP;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_positions p JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id)
  WHERE p.catalog_id=cat AND p.kind<>'delivery_obligation' GROUP BY e.id,e.quantity HAVING sum(abs(p.quantity))>abs(e.quantity))
 THEN RAISE EXCEPTION 'gas_owned_evidence_capacity';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id)
  WHERE c.catalog_id=cat GROUP BY e.id,e.quantity HAVING sum(c.quantity)>abs(e.quantity)) THEN RAISE EXCEPTION 'gas_owned_evidence_capacity';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_loans l JOIN trading.gas_owned_positions stock ON(stock.catalog_id,stock.id)=(l.catalog_id,l.received_lot_id)
  JOIN trading.gas_owned_positions owed ON(owed.catalog_id,owed.id)=(l.catalog_id,l.return_position_id) WHERE l.catalog_id=cat AND(
   stock.quantity<>l.quantity OR owed.quantity<>-l.quantity OR stock.kind<>'owned_stock' OR owed.kind<>'loan_return' OR
   stock.origin_policy<>'title_transfer_loan_v1' OR owed.origin_policy<>'title_transfer_loan_v1' OR
   stock.account_id<>owed.account_id OR l.return_at<=stock.effective_at OR owed.due_at<>l.return_at OR
   coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_repayments r JOIN trading.gas_owned_consumptions c
    ON(c.catalog_id,c.id)=(r.catalog_id,r.consumption_id) WHERE(r.catalog_id,r.loan_id)=(l.catalog_id,l.id)),0)>l.quantity))
 THEN RAISE EXCEPTION 'gas_loan_return';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_repayments r JOIN trading.gas_owned_loans l ON(l.catalog_id,l.id)=(r.catalog_id,r.loan_id)
  JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(r.catalog_id,r.consumption_id)
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id)
  JOIN trading.gas_owned_positions origin ON(origin.catalog_id,origin.id)=(l.catalog_id,l.received_lot_id)
  WHERE r.catalog_id=cat AND(c.kind<>'return' OR p.account_id<>l.account_id OR c.effective_at<origin.effective_at)) THEN RAISE EXCEPTION 'gas_loan_return';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
  WHERE s.catalog_id=cat GROUP BY p.id,p.quantity HAVING sum(s.quantity)>abs(p.quantity)) THEN RAISE EXCEPTION 'gas_operator_capacity';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
  LEFT JOIN trading.gas_owned_positions stock ON(stock.catalog_id,stock.id)=(s.catalog_id,s.receipt_lot_id)
  LEFT JOIN trading.gas_owned_consumptions consumed ON(consumed.catalog_id,consumed.id)=(s.catalog_id,s.consumption_id)
  WHERE s.catalog_id=cat AND(p.kind NOT IN('operator_claim','delivery_obligation') OR s.effective_at<p.effective_at
   OR s.receipt_lot_id IS NOT NULL AND(p.quantity<=0 OR stock.quantity<>s.quantity OR stock.evidence_id<>s.evidence_id OR stock.effective_at<>s.effective_at)
   OR s.consumption_id IS NOT NULL AND(p.quantity>=0 OR consumed.kind<>'delivery' OR consumed.quantity<>s.quantity OR consumed.evidence_id<>s.evidence_id OR consumed.effective_at<>s.effective_at)
   OR s.source_observation_id IS NOT NULL AND(p.kind<>'operator_claim' OR p.settlement_mode<>'cashout_final_price_v1' OR NOT EXISTS(
    SELECT 1 FROM trading.gas_owned_cashout_terms t WHERE(t.catalog_id,t.position_id)=(p.catalog_id,p.id)))))
 THEN RAISE EXCEPTION 'gas_operator_settlement';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE p.catalog_id=cat AND(
  p.kind='owned_stock' AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_lots l WHERE(l.catalog_id,l.id)=(cat,p.id)) OR
  p.kind='loan_return' AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.return_position_id)=(cat,p.id)) OR
  p.origin_policy='title_transfer_loan_v1' AND p.kind='owned_stock' AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.received_lot_id)=(cat,p.id)) OR
  p.origin_policy='operator_balance_transfer_v1' AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.receipt_lot_id)=(cat,p.id)) OR
  p.kind='operator_claim' AND(p.settlement_mode='cashout_final_price_v1')<>EXISTS(SELECT 1 FROM trading.gas_owned_cashout_terms t WHERE(t.catalog_id,t.position_id)=(cat,p.id))))
 THEN RAISE EXCEPTION 'gas_owned_incomplete_effect';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(s.catalog_id,s.evidence_id)
  WHERE s.catalog_id=cat AND s.source_observation_id IS NOT NULL GROUP BY e.id,e.quantity HAVING sum(s.quantity)>abs(e.quantity))
 THEN RAISE EXCEPTION 'gas_owned_evidence_capacity';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
  JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  JOIN trading.gas_owned_cashout_terms t ON(t.catalog_id,t.position_id)=(p.catalog_id,p.id)
  JOIN market_data.observations o ON(o.catalog_id,o.id)=(s.catalog_id,s.source_observation_id)
  JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.definition_revision_id,p.mark_curve_code)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  LEFT JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(s.catalog_id,s.cash_id)
  CROSS JOIN LATERAL(SELECT coalesce(sum(prior.quantity),0) quantity FROM trading.gas_owned_claim_settlements prior
   WHERE(prior.catalog_id,prior.position_id)=(s.catalog_id,s.position_id) AND prior.publication_order<s.publication_order)prior_total
  WHERE s.catalog_id=cat AND(p.kind<>'operator_claim' OR p.settlement_mode<>'cashout_final_price_v1'
   OR p.due_at<>t.cash_due_at OR(t.reference_catalog_id,t.reference_revision_id) IS DISTINCT FROM(a.reference_catalog_id,a.reference_revision_id)
   OR(o.definition_revision_id,o.source_code,o.role,o.observed_at,o.publication_state)
    IS DISTINCT FROM(a.definition_revision_id,t.source_code,t.role,t.observed_at,'final')
   OR NOT EXISTS(SELECT 1 FROM market_data.series ms WHERE(ms.catalog_id,ms.revision_id,ms.id,ms.contract_id,ms.quote_code)=
    (cat,a.definition_revision_id,o.series_id,p.mark_contract_id,c.quote_code))
   OR coalesce(f.amount,0)<>sign(p.quantity)*(trading.crude_round_cash((prior_total.quantity+s.quantity)*o.value*q.multiplier,2)-trading.crude_round_cash(prior_total.quantity*o.value*q.multiplier,2))
   OR f.id IS NOT NULL AND(f.position_id,f.reference_catalog_id,f.reference_revision_id,f.currency_code,f.due_at)
    IS DISTINCT FROM(p.id,a.reference_catalog_id,a.reference_revision_id,a.currency_code,t.cash_due_at)))
 THEN RAISE EXCEPTION 'gas_operator_cashout';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_fee_payments p JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id)
  WHERE p.catalog_id=cat AND p.paid_at<f.effective_at) THEN RAISE EXCEPTION 'gas_owned_payment_origin';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_fees f WHERE f.catalog_id=cat AND abs(f.amount)<coalesce((
  SELECT sum(abs(h.amount)) FROM(SELECT DISTINCT ON(p.business_key)p.amount FROM trading.gas_owned_fee_payments p
   WHERE(p.catalog_id,p.fee_id)=(f.catalog_id,f.id) ORDER BY p.business_key,p.revision DESC)h),0))
 THEN RAISE EXCEPTION 'gas_owned_payment_capacity';END IF;
 RETURN NULL;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_owned_positions','gas_owned_lots','gas_owned_reservations','gas_owned_releases','gas_owned_consumptions','gas_owned_loans','gas_owned_repayments','gas_owned_claim_settlements','gas_owned_fees','gas_owned_fee_payments'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER owned_integrity AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_owned_integrity()',t);
 END LOOP;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz)'::regprocedure,
  'security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])'::regprocedure,
  'trading.guard_gas_owned_publication()'::regprocedure,'trading.guard_gas_owned_child()'::regprocedure,'trading.check_gas_owned_integrity()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.require_gas_owned_read(uuid,uuid,timestamptz,timestamptz),
 security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[]) TO openetrm_runtime;
COMMIT;
