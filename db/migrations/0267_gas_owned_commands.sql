-- Explicit account configuration, source observations and policy-bound gas
-- holding operations. No operator balance is treated as titled stock.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 SELECT code,false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings' FROM unnest(ARRAY[
 'gas.owned.receive_title','gas.owned.reserve','gas.owned.release','gas.owned.dispatch','gas.owned.loss',
 'gas.operator.record','gas.operator.settle','gas.loan.borrow','gas.loan.repay','gas.futures.delivery','gas.cash.record_payment'])code;
CREATE TABLE trading.gas_owned_action_bindings(
 catalog_id uuid NOT NULL,account_id uuid NOT NULL,action text NOT NULL REFERENCES lifecycle.action_kinds(code),revision integer NOT NULL CHECK(revision>0),
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,account_id,action,revision),FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.gas_owned_action_bindings OWNER TO openetrm_security_owner;
ALTER TABLE trading.gas_owned_action_bindings ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_owned_action_bindings FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON trading.gas_owned_action_bindings TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_owned_action_bindings FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER native_publication BEFORE INSERT ON trading.gas_owned_action_bindings FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication();

CREATE FUNCTION trading.gas_owned_visible(catalog uuid,publication integer,effective_at timestamptz,context uuid,as_of timestamptz)
 RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,publication)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of)
   AND publication<=x.known_through AND k.known_from<=t.known_at AND effective_at<=as_of)
$$;
CREATE FUNCTION trading.require_gas_owned_head(catalog uuid,account uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM(
  SELECT a.publication_order,a.effective_at FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account)
  UNION ALL SELECT e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00' FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id)=(catalog,account)
  UNION ALL SELECT p.publication_order,p.effective_at FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT c.publication_order,c.effective_at FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(c.catalog_id,c.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_owned_reservations r JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(r.catalog_id,r.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT x.publication_order,x.effective_at FROM trading.gas_owned_releases x JOIN trading.gas_owned_reservations r ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id)
   JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(r.catalog_id,r.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT s.publication_order,s.effective_at FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT f.publication_order,f.effective_at FROM trading.gas_owned_fees f JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00' FROM trading.gas_owned_fee_payments x JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(x.catalog_id,x.fee_id)
   JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
 )h WHERE h.effective_at<=as_of AND trading.gas_owned_visible(catalog,h.publication_order,h.effective_at,context,as_of) IS NOT TRUE)
 THEN RAISE EXCEPTION 'gas_owned_context';END IF;
END$$;

CREATE FUNCTION trading.gas_owned_keys(body jsonb,keys text[]) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&keys) OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE NOT(k=ANY(keys)))
 OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE e.value='null'::jsonb AND e.key<>ALL(ARRAY[
  'counterpartyId','counterpartyRevision','feeDueAt','reservationId','ownedAccountId','lotId','availableAt','sourceObservationId','cashout']))
 THEN RAISE EXCEPTION 'gas_owned_input';END IF;
END$$;
CREATE FUNCTION trading.gas_owned_quantity(value text) RETURNS numeric LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE number numeric;BEGIN
 IF value IS NULL OR value!~'^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$' OR length(value)>64 THEN RAISE EXCEPTION 'gas_owned_quantity';END IF;
 number=value::numeric;IF abs(number)>=1e24 THEN RAISE EXCEPTION 'gas_owned_quantity';END IF;RETURN number;
END$$;
CREATE FUNCTION trading.gas_owned_at(value text) RETURNS timestamptz LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE instant timestamptz;BEGIN
 IF value IS NULL OR value!~'^\d{4}-\d{2}-\d{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,6})?Z$' THEN RAISE EXCEPTION 'gas_owned_instant';END IF;
 instant=value::timestamptz;IF NOT isfinite(instant) OR instant>='9999-12-31 00:00:00+00'::timestamptz THEN RAISE EXCEPTION 'gas_owned_instant';END IF;RETURN instant;
END$$;
-- Business equivalence uses native values, not formatting of JSON operands.
CREATE FUNCTION trading.gas_owned_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body;key text;BEGIN
 IF body?'expectedRevision' THEN
  IF jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR (body->>'expectedRevision')::numeric<0
   OR(body->>'expectedRevision')::numeric>2147483647 OR trunc((body->>'expectedRevision')::numeric)<>(body->>'expectedRevision')::numeric
  THEN RAISE EXCEPTION 'gas_owned_input';END IF;
  result=result||jsonb_build_object('expectedRevision',((body->>'expectedRevision')::numeric)::integer);
 END IF;
 FOREACH key IN ARRAY ARRAY['quantity','amount','feeAmount'] LOOP
  IF key='quantity' AND body?'referenceCatalogId' THEN CONTINUE;END IF;
  IF body->>key IS NOT NULL THEN result=result||jsonb_build_object(key,trim_scale(trading.gas_owned_quantity(body->>key))::text);END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['bookId','legalEntityId','counterpartyId','referenceCatalogId','referenceRevisionId','definitionRevisionId','markContractId',
 'evidenceId','agreementId','returnMarkContractId','lenderId','lotId','reservationId','loanId','feeId','policyId','positionId','ownedAccountId','sourceObservationId'] LOOP
  IF body->>key IS NOT NULL THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['effectiveAt','occurredAt','titleAt','availableAt','returnAt','dueAt','feeDueAt','paidAt','observedAt','cashDueAt'] LOOP
  IF body->>key IS NOT NULL THEN result=result||jsonb_build_object(key,to_char(trading.gas_owned_at(body->>key) AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));END IF;
 END LOOP;
 IF jsonb_typeof(body->'cashout')='object' THEN result=result||jsonb_build_object('cashout',trading.gas_owned_fact(body->'cashout'));END IF;
 RETURN result;
END$$;

CREATE FUNCTION trading.gas_owned_outcome(catalog uuid,account uuid,action text,body jsonb,outcome jsonb,acknowledged boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE payment trading.gas_owned_fee_payments;evidence trading.gas_owned_evidence;binding trading.gas_owned_action_bindings;current_version integer;BEGIN
 current_version=(outcome->>'revision')::integer;
 IF action='fee_payment' THEN
  SELECT * INTO STRICT payment FROM trading.gas_owned_fee_payments p
   WHERE(p.catalog_id,p.fee_id,p.business_key)=(catalog,(body->>'feeId')::uuid,body->>'businessKey') ORDER BY p.revision DESC LIMIT 1;
  current_version=payment.revision;outcome=outcome||jsonb_build_object('currentAmount',trim_scale(payment.amount)::text,'currentPaidAt',payment.paid_at);
 ELSIF action='evidence' THEN
  SELECT * INTO STRICT evidence FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,account,body->>'sourceKey') ORDER BY e.revision DESC LIMIT 1;
  current_version=evidence.revision;outcome=outcome||jsonb_build_object('currentQuantity',trim_scale(evidence.quantity)::text,'currentOccurredAt',evidence.occurred_at,'currentEvidenceId',evidence.id);
 ELSIF action='bind_policy' THEN
  SELECT * INTO STRICT binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,body->>'action') ORDER BY b.revision DESC LIMIT 1;
  current_version=binding.revision;outcome=outcome||jsonb_build_object('currentPolicyId',binding.policy_id,'currentPolicyRevision',binding.policy_revision);
 END IF;
 RETURN outcome||jsonb_build_object('acknowledgedRevision',(outcome->>'revision')::integer,'currentRevision',current_version,'semanticAcknowledgement',acknowledged);
END$$;
CREATE FUNCTION security.require_gas_owned_write(catalog uuid,configuring boolean,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_owned(catalog,true,configuring);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION trading.guard_gas_owned_terms() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE a trading.gas_owned_accounts;r market_data.revisions;q ref_data.quote_specifications;e trading.gas_owned_evidence;loc text;BEGIN
 IF TG_TABLE_NAME='gas_owned_accounts' THEN a=NEW;ELSE SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.account_id);END IF;
 SELECT * INTO STRICT r FROM market_data.revisions x WHERE(x.catalog_id,x.id)=(a.catalog_id,a.definition_revision_id);
 SELECT c.location_code INTO STRICT loc FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=
  (a.catalog_id,a.definition_revision_id,CASE WHEN TG_TABLE_NAME='gas_owned_accounts' THEN a.mark_curve_code ELSE NEW.mark_curve_code END);
 SELECT quote.* INTO STRICT q FROM market_data.curves c JOIN ref_data.quote_specifications quote
  ON(quote.catalog_id,quote.revision_id,quote.code)=(r.reference_catalog_id,r.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.definition_revision_id,CASE WHEN TG_TABLE_NAME='gas_owned_accounts' THEN a.mark_curve_code ELSE NEW.mark_curve_code END);
 IF(a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.currency_code) IS DISTINCT FROM(r.reference_catalog_id,r.reference_revision_id,q.quantity_code,q.currency_code)
 OR q.kind<>'outright' OR loc<>a.location_code THEN RAISE EXCEPTION 'gas_holding_unit';END IF;
 IF TG_TABLE_NAME='gas_owned_positions' THEN
  IF NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=
   (a.catalog_id,a.definition_revision_id,NEW.mark_curve_code,NEW.mark_contract_id)) THEN RAISE EXCEPTION 'gas_owned_mark';END IF;
  SELECT * INTO STRICT e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.evidence_id);
  IF e.account_id<>a.id OR e.occurred_at>NEW.effective_at OR
   NEW.kind='owned_stock' AND(a.kind<>'owned_inventory' OR NEW.quantity<=0 OR e.quantity<=0 OR e.kind NOT IN('title_receipt','loan_agreement')) OR
   NEW.kind='operator_claim' AND(a.kind<>'operator_balance' OR e.kind<>'operator_statement') OR
   NEW.kind='loan_return' AND(a.kind<>'owned_inventory' OR NEW.quantity>=0 OR e.kind<>'loan_agreement') OR
   NEW.kind='owned_stock' AND NEW.settlement_mode<>'owned_stock_v1' OR
   NEW.kind='loan_return' AND NEW.settlement_mode<>'return_quantity_v1' OR
   NEW.kind='operator_claim' AND NEW.settlement_mode NOT IN('return_quantity_v1','cashout_final_price_v1') OR
   NEW.kind='delivery_obligation' AND(NEW.settlement_mode<>'physical_delivery_v1' OR e.kind<>'futures_final')
  THEN RAISE EXCEPTION 'gas_owned_source';END IF;
 END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER exact_owned_terms BEFORE INSERT ON trading.gas_owned_accounts FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_terms();
CREATE TRIGGER exact_owned_terms BEFORE INSERT ON trading.gas_owned_positions FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_terms();

CREATE FUNCTION trading.gas_owned_cashout_price(catalog uuid,requested_position uuid,observation uuid,at_time timestamptz,context uuid) RETURNS numeric
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE p trading.gas_owned_positions;a trading.gas_owned_accounts;t trading.gas_owned_cashout_terms;s market_data.observations;
 quote text;cutoff timestamptz;prefix integer;BEGIN
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(catalog,requested_position);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,p.account_id);
 SELECT * INTO STRICT t FROM trading.gas_owned_cashout_terms x WHERE(x.catalog_id,x.position_id)=(catalog,requested_position);
 SELECT * INTO s FROM market_data.observations x WHERE(x.catalog_id,x.id)=(catalog,observation);
 SELECT c.quote_code INTO STRICT quote FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,a.definition_revision_id,p.mark_curve_code);
 SELECT c.known_at,m.known_through INTO STRICT cutoff,prefix FROM market_data.temporal_contexts c
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(c.catalog_id,c.id) WHERE(c.catalog_id,c.id)=(catalog,context);
 IF s.id IS NULL OR(s.definition_revision_id,s.source_code,s.role,s.observed_at,s.publication_state)
  IS DISTINCT FROM(a.definition_revision_id,t.source_code,t.role,t.observed_at,'final') OR at_time<t.observed_at
  OR NOT EXISTS(SELECT 1 FROM market_data.series x WHERE(x.catalog_id,x.revision_id,x.id,x.contract_id,x.quote_code)=
   (catalog,a.definition_revision_id,s.series_id,p.mark_contract_id,quote))
  OR(SELECT count(*) FROM(SELECT x.id FROM market_data.observations x
   JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(x.catalog_id,x.id)
   JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
   WHERE(x.catalog_id,x.definition_revision_id,x.series_id,x.source_code,x.role,x.observed_at)=
    (catalog,a.definition_revision_id,s.series_id,t.source_code,t.role,t.observed_at) AND e.event_order<=prefix AND k.known_from<=cutoff
   AND NOT EXISTS(SELECT 1 FROM market_data.observations n JOIN market_data.knowledge_events ne ON(ne.catalog_id,ne.observation_id)=(n.catalog_id,n.id)
    JOIN market_data.market_knowledge_receipts nk ON(nk.catalog_id,nk.event_order,nk.publication_xid)=(ne.catalog_id,ne.event_order,ne.publication_xid)
    WHERE(n.catalog_id,n.corrects_id)=(x.catalog_id,x.id) AND ne.event_order<=prefix AND nk.known_from<=cutoff))h
   HAVING count(*)=1 AND bool_and(h.id=s.id)) IS DISTINCT FROM 1
 THEN RAISE EXCEPTION 'gas_operator_final_source';END IF;
 RETURN s.value*(SELECT q.multiplier FROM ref_data.quote_specifications q WHERE(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,quote));
END$$;

CREATE FUNCTION trading.apply_gas_owned_financial(catalog uuid,account uuid,action text,body jsonb,as_of timestamptz,context uuid,actor uuid,publication integer)
 RETURNS jsonb LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE a trading.gas_owned_accounts;owned trading.gas_owned_accounts;e trading.gas_owned_evidence;agreement trading.gas_owned_evidence;
 lot trading.gas_owned_positions;claim trading.gas_owned_positions;loan trading.gas_owned_loans;reservation trading.gas_owned_reservations;
 fee_row trading.gas_owned_fees;payment trading.gas_owned_fee_payments;cashout trading.gas_owned_cashout_terms;
 qty numeric;amount_value numeric;at_time timestamptz;due timestamptz;price numeric;prior_qty numeric;
 id uuid=gen_random_uuid();other uuid=gen_random_uuid();loan_id uuid=gen_random_uuid();version integer=1;BEGIN
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 IF action IN('receive_title','operator_claim','borrow') THEN
  IF action='receive_title' THEN
   PERFORM trading.gas_owned_keys(body,ARRAY['evidenceId','quantity','titleAt','availableAt','originPolicy','carrying']);
   at_time=trading.gas_owned_at(body->>'titleAt');due=trading.gas_owned_at(body->>'availableAt');
   IF body->>'originPolicy'<>'opening_owned_stock_v1' OR a.kind<>'owned_inventory' OR due<at_time THEN RAISE EXCEPTION 'gas_owned_title';END IF;
  ELSIF action='operator_claim' THEN
   PERFORM trading.gas_owned_keys(body,ARRAY['evidenceId','quantity','effectiveAt','dueAt','carrying','settlementMode','cashout']);
   at_time=trading.gas_owned_at(body->>'effectiveAt');due=trading.gas_owned_at(body->>'dueAt');
   IF a.kind<>'operator_balance' OR body->>'settlementMode' NOT IN('return_quantity_v1','cashout_final_price_v1')
    OR(body->>'settlementMode'='return_quantity_v1')<>(body->'cashout'='null'::jsonb)
   THEN RAISE EXCEPTION 'gas_operator_account';END IF;
  ELSE
   PERFORM trading.gas_owned_keys(body,ARRAY['agreementId','evidenceId','quantity','titleAt','availableAt','returnAt','returnMarkCurve','returnMarkContractId','carrying','feeAmount','feeDueAt','lenderId','lenderRevision']);
   at_time=trading.gas_owned_at(body->>'titleAt');due=trading.gas_owned_at(body->>'returnAt');
   SELECT * INTO agreement FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(body->>'agreementId')::uuid,account);
   IF a.kind<>'owned_inventory' OR agreement.kind IS DISTINCT FROM 'loan_agreement' OR due<=at_time OR agreement.occurred_at>at_time
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence n WHERE(n.catalog_id,n.account_id,n.source_key)=(agreement.catalog_id,agreement.account_id,agreement.source_key) AND n.revision>agreement.revision)
   OR NOT EXISTS(SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'lenderId')::uuid,(body->>'lenderRevision')::integer)
    AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of))
   OR trading.gas_owned_at(body->>'availableAt')<at_time THEN RAISE EXCEPTION 'gas_loan_terms';END IF;
   amount_value=trading.gas_owned_quantity(body->>'feeAmount');
   IF amount_value>0 OR amount_value<>0 AND body->>'feeDueAt' IS NULL OR amount_value=0 AND body->>'feeDueAt' IS NOT NULL
   THEN RAISE EXCEPTION 'gas_loan_fee';END IF;
  END IF;
  qty=trading.gas_owned_quantity(body->>'quantity');
  SELECT * INTO e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(body->>'evidenceId')::uuid,account);
  IF e.id IS NULL OR qty=0 OR action<>'operator_claim' AND qty<0 OR abs(qty)>abs(e.quantity) OR e.occurred_at>at_time OR at_time>as_of
   OR action='operator_claim' AND(e.kind<>'operator_statement' OR sign(qty)<>sign(e.quantity))
   OR action<>'operator_claim' AND(e.kind<>'title_receipt' OR e.quantity<=0)
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence newer WHERE(newer.catalog_id,newer.account_id,newer.source_key)=(e.catalog_id,e.account_id,e.source_key) AND newer.revision>e.revision)
  THEN RAISE EXCEPTION 'gas_owned_evidence';END IF;
  IF EXISTS(SELECT 1 FROM trading.gas_owned_positions p JOIN trading.gas_owned_evidence source ON(source.catalog_id,source.id)=(p.catalog_id,p.evidence_id)
   WHERE(source.catalog_id,source.account_id,source.source_key)=(e.catalog_id,e.account_id,e.source_key)) THEN RAISE EXCEPTION 'gas_owned_source_already_applied';END IF;
  IF action='borrow' AND(qty>agreement.quantity OR EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.evidence_id,p.kind)=(catalog,agreement.id,'loan_return')))
  THEN RAISE EXCEPTION 'gas_loan_terms';END IF;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_owned_positions VALUES(catalog,id,account,CASE action WHEN 'operator_claim' THEN 'operator_claim' ELSE 'owned_stock' END,qty,e.id,
   CASE action WHEN 'operator_claim' THEN 'operator_balance_claim_v1' WHEN 'borrow' THEN 'title_transfer_loan_v1' ELSE 'opening_owned_stock_v1' END,
   a.definition_revision_id,a.mark_curve_code,a.mark_contract_id,CASE WHEN action='operator_claim' THEN due ELSE NULL END,
   CASE WHEN action='borrow' THEN 'nominal_mark_v1' ELSE body->>'carrying' END,
   CASE WHEN action='operator_claim' THEN body->>'settlementMode' ELSE 'owned_stock_v1' END,actor,publication,at_time);
  IF action<>'operator_claim' THEN INSERT INTO trading.gas_owned_lots VALUES(catalog,id,account,at_time,
   CASE WHEN action='borrow' THEN trading.gas_owned_at(body->>'availableAt') ELSE due END);END IF;
  IF action='operator_claim' AND body->>'settlementMode'='cashout_final_price_v1' THEN
   PERFORM trading.gas_owned_keys(body->'cashout',ARRAY['source','role','observedAt','cashDueAt','rounding']);
   IF NOT EXISTS(SELECT 1 FROM ref_data.sources s WHERE(s.catalog_id,s.revision_id,s.code)=(a.reference_catalog_id,a.reference_revision_id,body->'cashout'->>'source'))
    OR due<>trading.gas_owned_at(body->'cashout'->>'cashDueAt')
   THEN RAISE EXCEPTION 'gas_operator_cashout';END IF;
   INSERT INTO trading.gas_owned_cashout_terms VALUES(catalog,id,a.reference_catalog_id,a.reference_revision_id,body->'cashout'->>'source',body->'cashout'->>'role',
    trading.gas_owned_at(body->'cashout'->>'observedAt'),trading.gas_owned_at(body->'cashout'->>'cashDueAt'),body->'cashout'->>'rounding');
  END IF;
  IF action='borrow' THEN
   INSERT INTO trading.gas_owned_positions VALUES(catalog,other,account,'loan_return',-qty,agreement.id,'title_transfer_loan_v1',
    a.definition_revision_id,body->>'returnMarkCurve',(body->>'returnMarkContractId')::uuid,due,body->>'carrying','return_quantity_v1',actor,publication,at_time);
   INSERT INTO trading.gas_owned_loans VALUES(catalog,loan_id,account,id,other,qty,due,'fungible_gas_title_transfer_return_v1',(body->>'lenderId')::uuid,(body->>'lenderRevision')::integer);
   IF amount_value<>0 THEN INSERT INTO trading.gas_owned_fees VALUES(catalog,gen_random_uuid(),other,loan_id,amount_value,
    a.reference_catalog_id,a.reference_revision_id,a.currency_code,trading.gas_owned_at(body->>'feeDueAt'),actor,publication,at_time);END IF;
   RETURN jsonb_build_object('id',loan_id,'stockId',id,'returnId',other,'revision',1,'effect',jsonb_build_object(
    'kind','borrow','stockQuantity',trim_scale(qty)::text,'returnQuantity',trim_scale(-qty)::text,'quantitySpecification',a.quantity_code,
    'returnAt',due,'feeAmount',trim_scale(amount_value)::text,'currency',a.currency_code,'feeDueAt',body->'feeDueAt'));
  END IF;
 ELSIF action='reserve' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['lotId','businessKey','quantity','effectiveAt']);
  SELECT * INTO lot FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id,p.account_id,p.kind)=(catalog,(body->>'lotId')::uuid,account,'owned_stock');
  qty=trading.gas_owned_quantity(body->>'quantity');at_time=trading.gas_owned_at(body->>'effectiveAt');
  IF lot.id IS NULL OR qty<=0 OR at_time<lot.effective_at OR at_time>as_of THEN RAISE EXCEPTION 'gas_stock_reservation';END IF;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_owned_reservations VALUES(catalog,id,lot.id,body->>'businessKey',qty,actor,publication,at_time);
 ELSIF action='release' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['reservationId','effectiveAt']);
  SELECT r.* INTO reservation FROM trading.gas_owned_reservations r JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(r.catalog_id,r.lot_id)
   WHERE(r.catalog_id,r.id,p.account_id)=(catalog,(body->>'reservationId')::uuid,account);
  at_time=trading.gas_owned_at(body->>'effectiveAt');
  IF reservation.id IS NULL OR at_time<reservation.effective_at OR at_time>as_of THEN RAISE EXCEPTION 'gas_stock_reservation';END IF;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_owned_releases VALUES(catalog,reservation.id,actor,publication,at_time);id=reservation.id;
 ELSIF action IN('dispatch','loss','repay') THEN
  PERFORM trading.gas_owned_keys(body,CASE WHEN action='repay' THEN ARRAY['loanId','lotId','evidenceId','quantity','reservationId','effectiveAt']
   ELSE ARRAY['lotId','evidenceId','quantity','reservationId','effectiveAt'] END);
  SELECT * INTO lot FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id,p.account_id,p.kind)=(catalog,(body->>'lotId')::uuid,account,'owned_stock');
  SELECT * INTO e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(body->>'evidenceId')::uuid,account);
  qty=trading.gas_owned_quantity(body->>'quantity');at_time=trading.gas_owned_at(body->>'effectiveAt');
  IF lot.id IS NULL OR e.id IS NULL OR qty<=0 OR qty>abs(e.quantity) OR at_time>as_of OR at_time<>e.occurred_at
   OR e.kind<>(CASE action WHEN 'loss' THEN 'physical_loss' WHEN 'repay' THEN 'loan_return' ELSE 'physical_delivery' END)
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence newer WHERE(newer.catalog_id,newer.account_id,newer.source_key)=(e.catalog_id,e.account_id,e.source_key) AND newer.revision>e.revision)
  THEN RAISE EXCEPTION 'gas_owned_evidence';END IF;
  IF action='repay' THEN
   SELECT * INTO loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=(catalog,(body->>'loanId')::uuid,account);
   IF loan.id IS NULL THEN RAISE EXCEPTION 'gas_loan_return';END IF;
  END IF;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_owned_consumptions VALUES(catalog,id,lot.id,CASE action WHEN 'loss' THEN 'loss' WHEN 'repay' THEN 'return' ELSE 'delivery' END,
   qty,e.id,(body->>'reservationId')::uuid,actor,publication,at_time);
  IF action='repay' THEN INSERT INTO trading.gas_owned_repayments VALUES(catalog,other,loan.id,id);id=other;END IF;
 ELSIF action='operator_settle' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['positionId','quantity','ownedAccountId','lotId','reservationId','evidenceId','effectiveAt','availableAt','sourceObservationId']);
  SELECT * INTO claim FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id,p.account_id,p.kind)=(catalog,(body->>'positionId')::uuid,account,'operator_claim');
  qty=trading.gas_owned_quantity(body->>'quantity');at_time=trading.gas_owned_at(body->>'effectiveAt');
  SELECT * INTO owned FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,(body->>'ownedAccountId')::uuid);
  SELECT * INTO e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(catalog,(body->>'evidenceId')::uuid);
  IF e.id IS NULL OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence n
   WHERE(n.catalog_id,n.account_id,n.source_key)=(e.catalog_id,e.account_id,e.source_key) AND n.revision>e.revision)
  THEN RAISE EXCEPTION 'gas_owned_evidence';END IF;
  IF claim.id IS NULL OR qty<=0 OR at_time<claim.effective_at OR at_time>as_of THEN RAISE EXCEPTION 'gas_operator_settlement';END IF;
  IF claim.settlement_mode='cashout_final_price_v1' THEN
   IF body->>'ownedAccountId' IS NOT NULL OR body->>'lotId' IS NOT NULL OR body->>'reservationId' IS NOT NULL OR body->>'availableAt' IS NOT NULL
    OR e.account_id IS DISTINCT FROM a.id OR e.kind IS DISTINCT FROM 'cashout_determination' OR e.occurred_at<>at_time OR qty>abs(e.quantity)
   THEN RAISE EXCEPTION 'gas_operator_settlement';END IF;
   SELECT * INTO STRICT cashout FROM trading.gas_owned_cashout_terms t WHERE(t.catalog_id,t.position_id)=(catalog,claim.id);
   price=trading.gas_owned_cashout_price(catalog,claim.id,(body->>'sourceObservationId')::uuid,at_time,context);
   IF EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.position_id)=(catalog,claim.id)
    AND s.source_observation_id IS DISTINCT FROM(body->>'sourceObservationId')::uuid) THEN RAISE EXCEPTION 'gas_operator_reconciliation';END IF;
   SELECT coalesce(sum(s.quantity),0) INTO prior_qty FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.position_id)=(catalog,claim.id);
   amount_value=sign(claim.quantity)*(trading.crude_round_cash((prior_qty+qty)*price,2)-trading.crude_round_cash(prior_qty*price,2));
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   IF amount_value<>0 THEN INSERT INTO trading.gas_owned_fees VALUES(catalog,other,claim.id,NULL,amount_value,a.reference_catalog_id,a.reference_revision_id,a.currency_code,cashout.cash_due_at,actor,publication,at_time);ELSE other=NULL;END IF;
   INSERT INTO trading.gas_owned_claim_settlements VALUES(catalog,id,claim.id,qty,NULL,NULL,other,e.id,(body->>'sourceObservationId')::uuid,actor,publication,at_time);
   RETURN jsonb_build_object('id',id,'cashId',other,'revision',1,'effect',jsonb_build_object('kind','cashout',
    'performedQuantity',trim_scale(qty)::text,'quantitySpecification',a.quantity_code,'cashAmount',trim_scale(amount_value)::text,
    'currency',a.currency_code,'cashDueAt',cashout.cash_due_at));
  END IF;
  PERFORM trading.require_gas_owned_head(catalog,owned.id,context,as_of);
  IF claim.settlement_mode<>'return_quantity_v1'
   OR owned.kind IS DISTINCT FROM 'owned_inventory' OR e.account_id IS DISTINCT FROM owned.id OR at_time<>e.occurred_at OR qty>abs(e.quantity)
   OR(owned.legal_entity_id,owned.legal_entity_revision,owned.reference_catalog_id,owned.reference_revision_id,owned.quantity_code,owned.location_code,owned.currency_code)
    IS DISTINCT FROM(a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.location_code,a.currency_code)
   OR NOT trading.gas_owned_visible(owned.catalog_id,owned.publication_order,owned.effective_at,context,as_of)
   OR security.market_fact_visible(catalog,owned.definition_revision_id,NULL) IS NOT TRUE
   OR body->>'sourceObservationId' IS NOT NULL THEN RAISE EXCEPTION 'gas_operator_settlement';END IF;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  IF claim.quantity>0 THEN
   IF e.kind<>'title_receipt' OR body->>'lotId' IS NOT NULL OR body->>'reservationId' IS NOT NULL
    OR trading.gas_owned_at(body->>'availableAt')<at_time THEN RAISE EXCEPTION 'gas_operator_settlement';END IF;
   INSERT INTO trading.gas_owned_positions VALUES(catalog,other,owned.id,'owned_stock',qty,e.id,'operator_balance_transfer_v1',
    owned.definition_revision_id,owned.mark_curve_code,owned.mark_contract_id,NULL,'nominal_mark_v1','owned_stock_v1',actor,publication,at_time);
   INSERT INTO trading.gas_owned_lots VALUES(catalog,other,owned.id,at_time,trading.gas_owned_at(body->>'availableAt'));
   INSERT INTO trading.gas_owned_claim_settlements VALUES(catalog,id,claim.id,qty,other,NULL,NULL,e.id,NULL,actor,publication,at_time);
  ELSE
   SELECT * INTO lot FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id,p.account_id,p.kind)=(catalog,(body->>'lotId')::uuid,owned.id,'owned_stock');
   IF lot.id IS NULL OR e.kind<>'physical_delivery' OR body->>'availableAt' IS NOT NULL THEN RAISE EXCEPTION 'gas_operator_settlement';END IF;
   INSERT INTO trading.gas_owned_consumptions VALUES(catalog,other,lot.id,'delivery',qty,e.id,(body->>'reservationId')::uuid,actor,publication,at_time);
   INSERT INTO trading.gas_owned_claim_settlements VALUES(catalog,id,claim.id,qty,NULL,other,NULL,e.id,NULL,actor,publication,at_time);
  END IF;
 ELSIF action='fee_payment' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['feeId','businessKey','expectedRevision','paidAt','amount','evidence']);
  SELECT f.* INTO fee_row FROM trading.gas_owned_fees f JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id)
   WHERE(f.catalog_id,f.id,p.account_id)=(catalog,(body->>'feeId')::uuid,account);
  amount_value=trading.gas_owned_quantity(body->>'amount');at_time=trading.gas_owned_at(body->>'paidAt');
  IF at_time<fee_row.effective_at THEN RAISE EXCEPTION 'gas_owned_payment_origin';END IF;
  SELECT * INTO payment FROM trading.gas_owned_fee_payments p WHERE(p.catalog_id,p.fee_id,p.business_key)=(catalog,fee_row.id,body->>'businessKey') ORDER BY revision DESC LIMIT 1;
  IF fee_row.id IS NULL OR amount_value=0 OR sign(amount_value)<>sign(fee_row.amount) OR at_time>as_of
   OR coalesce(payment.revision,0) IS DISTINCT FROM(body->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_owned_payment';END IF;
  version=coalesce(payment.revision,0)+1;INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_owned_fee_payments VALUES(catalog,id,fee_row.id,body->>'businessKey',version,at_time,amount_value,body->>'evidence',actor,publication);
 ELSE RAISE EXCEPTION 'gas_owned_action';END IF;
 RETURN jsonb_build_object('id',id,'revision',version,'effect',jsonb_strip_nulls(jsonb_build_object('kind',action,
  'performedQuantity',CASE WHEN qty IS NOT NULL THEN trim_scale(qty)::text END,'quantitySpecification',a.quantity_code,
  'cashAmount',CASE WHEN amount_value IS NOT NULL THEN trim_scale(amount_value)::text END,'currency',a.currency_code,'effectiveAt',at_time)));
END$$;

CREATE FUNCTION security.record_gas_owned(catalog uuid,operation uuid,action text,account uuid,body jsonb,as_of timestamptz,
 context uuid,policy uuid,policy_version integer,automatic boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;until_at timestamptz;configuring boolean=action IN('account','bind_policy');requires_jobs boolean=automatic;canonical jsonb;
 prior trading.gas_owned_commands;same_fact trading.gas_owned_commands;a trading.gas_owned_accounts;previous_evidence trading.gas_owned_evidence;
 binding trading.gas_owned_action_bindings;event_id uuid;publication integer;version integer;quantity numeric;at_time timestamptz;
 code text;outcome jsonb;changed boolean=true;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'),
  security.authority_until('RecordGasOwnedHoldings'));
 IF configuring THEN until_at=least(until_at,security.authority_until('ConfigureGasOwnedHoldings'));END IF;
 IF automatic IS TRUE THEN until_at=least(until_at,security.authority_until('RunJobs'));END IF;
 IF action='bind_policy' AND EXISTS(SELECT 1 FROM lifecycle.action_policies p
  WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer) AND p.mode='automatic')
 THEN requires_jobs=true;until_at=least(until_at,security.authority_until('RunJobs'));END IF;
 PERFORM security.require_gas_owned_write(catalog,configuring,until_at);
 IF requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR account IS NULL OR account='00000000-0000-0000-0000-000000000000'::uuid
 OR automatic IS NULL OR context IS NULL OR context='00000000-0000-0000-0000-000000000000'::uuid
 OR action IS NULL OR action IN('account','bind_policy','evidence') AND(policy IS NOT NULL OR policy_version IS NOT NULL OR automatic)
 OR action NOT IN('account','bind_policy','evidence') AND(policy IS NULL OR policy='00000000-0000-0000-0000-000000000000'::uuid OR policy_version IS NULL OR policy_version<1)
 OR as_of IS NULL OR NOT isfinite(as_of) OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>16384
 THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 body=trading.gas_owned_fact(body);
 IF action IN('fee_payment','evidence') AND NOT(body?'expectedRevision') THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 canonical=jsonb_build_object('action',action,'accountId',account,'body',body,'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_version,'automatic',automatic);
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 PERFORM security.require_gas_owned_write(catalog,configuring,until_at);
 IF requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
  IF security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE
   OR security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
  RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_owned_write(catalog,configuring,until_at);
 IF requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 IF action<>'account' AND(a.id IS NULL OR security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE) THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 IF action<>'account' THEN
  SELECT * INTO same_fact FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.account_id)=(catalog,account)
   AND c.request->>'action'=action AND((c.request->'body')-'expectedRevision')=(body-'expectedRevision') ORDER BY c.request_id LIMIT 1;
  IF FOUND THEN
   outcome=trading.gas_owned_outcome(catalog,account,action,body,same_fact.outcome||jsonb_build_object('operationId',operation,'changed',false),true);
   INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,until_at,configuring,requires_jobs);
   PERFORM security.require_gas_owned_write(catalog,configuring,until_at);
   IF requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;RETURN outcome;
  END IF;
 END IF;
 IF context IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of))
 THEN RAISE EXCEPTION 'gas_owned_context';END IF;
 IF a.id IS NOT NULL THEN
  IF trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_context';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,a.definition_revision_id);
  IF action NOT IN('evidence','bind_policy') THEN PERFORM trading.require_gas_owned_head(catalog,account,context,as_of);END IF;
 END IF;
 code=CASE action WHEN 'receive_title' THEN 'gas.owned.receive_title' WHEN 'reserve' THEN 'gas.owned.reserve' WHEN 'release' THEN 'gas.owned.release'
  WHEN 'dispatch' THEN 'gas.owned.dispatch' WHEN 'loss' THEN 'gas.owned.loss' WHEN 'operator_claim' THEN 'gas.operator.record'
  WHEN 'operator_settle' THEN 'gas.operator.settle' WHEN 'borrow' THEN 'gas.loan.borrow' WHEN 'repay' THEN 'gas.loan.repay'
  WHEN 'fee_payment' THEN 'gas.cash.record_payment' ELSE NULL END;
 IF code IS NOT NULL THEN
  SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,code) ORDER BY revision DESC LIMIT 1;
  IF binding.policy_id IS DISTINCT FROM policy OR binding.policy_revision IS DISTINCT FROM policy_version THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
  PERFORM lifecycle.require_policy(catalog,policy,policy_version,code,as_of,automatic);
 ELSIF automatic OR action NOT IN('account','bind_policy','evidence','fee_payment') THEN RAISE EXCEPTION 'gas_owned_action';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 event_id=gen_random_uuid();version=1;
 IF action='account' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['code','kind','bookId','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','referenceCatalogId','referenceRevisionId',
   'quantity','location','definitionRevisionId','markCurve','markContractId','currency','titlePolicy','lossPolicy','effectiveAt']);
  at_time=trading.gas_owned_at(body->>'effectiveAt');
  IF a.id IS NOT NULL THEN
   IF(a.code,a.kind,a.book_id,a.legal_entity_id,a.legal_entity_revision,a.counterparty_id,a.counterparty_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.location_code,
    a.definition_revision_id,a.mark_curve_code,a.mark_contract_id,a.currency_code,a.title_policy,a.loss_policy,a.effective_at) IS DISTINCT FROM
    (body->>'code',body->>'kind',(body->>'bookId')::uuid,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,
     (body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'quantity',body->>'location',(body->>'definitionRevisionId')::uuid,
     body->>'markCurve',(body->>'markContractId')::uuid,body->>'currency',body->>'titlePolicy',body->>'lossPolicy',at_time)
   THEN RAISE EXCEPTION 'gas_owned_account_immutable';END IF;
   event_id=a.id;changed=false;
  ELSE
   PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,(body->>'definitionRevisionId')::uuid);
   IF NOT EXISTS(SELECT 1 FROM trading.legal_entities p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer)
    AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of)) OR body->>'counterpartyId' IS NOT NULL AND NOT EXISTS(
    SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer)
    AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of)) THEN RAISE EXCEPTION 'gas_owned_context';END IF;
   IF at_time>as_of OR NOT EXISTS(SELECT 1 FROM trading.books b WHERE(b.catalog_id,b.id)=(catalog,(body->>'bookId')::uuid)
    AND trading.gas_owned_visible(b.catalog_id,b.publication_order,b.effective_at,context,as_of)) THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   INSERT INTO trading.gas_owned_accounts VALUES(catalog,account,body->>'code',body->>'kind',(body->>'bookId')::uuid,(body->>'legalEntityId')::uuid,
    (body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,
    (body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'quantity',body->>'location',
    (body->>'definitionRevisionId')::uuid,body->>'markCurve',(body->>'markContractId')::uuid,body->>'currency',body->>'titlePolicy',body->>'lossPolicy',actor,publication,at_time);
   event_id=account;
  END IF;
 ELSIF action='bind_policy' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['action','policyId','policyRevision','effectiveAt']);code=body->>'action';at_time=trading.gas_owned_at(body->>'effectiveAt');
  IF code NOT LIKE 'gas.%' OR at_time>as_of THEN RAISE EXCEPTION 'gas_owned_policy';END IF;
  PERFORM lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,code,at_time,false);
  SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,code) ORDER BY revision DESC LIMIT 1;
  IF(binding.policy_id,binding.policy_revision,binding.effective_at) IS NOT DISTINCT FROM((body->>'policyId')::uuid,(body->>'policyRevision')::integer,at_time)
  THEN changed=false;version=binding.revision;event_id=account;
  ELSE
   IF EXISTS(SELECT 1 FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer) AND p.mode='automatic')
    AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
   version=coalesce(binding.revision,0)+1;INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   INSERT INTO trading.gas_owned_action_bindings VALUES(catalog,account,code,version,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,actor,publication,at_time);
  END IF;
 ELSIF action='evidence' THEN
  PERFORM trading.gas_owned_keys(body,ARRAY['sourceKey','kind','quantity','occurredAt','evidence','expectedRevision']);
  quantity=trading.gas_owned_quantity(body->>'quantity');at_time=trading.gas_owned_at(body->>'occurredAt');
  SELECT * INTO previous_evidence FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.account_id,x.source_key)=(catalog,account,body->>'sourceKey') ORDER BY revision DESC LIMIT 1;
  IF(previous_evidence.kind,previous_evidence.quantity,previous_evidence.occurred_at,previous_evidence.evidence) IS NOT DISTINCT FROM(body->>'kind',quantity,at_time,body->>'evidence')
  THEN event_id=previous_evidence.id;version=previous_evidence.revision;changed=false;
  ELSE
   IF coalesce(previous_evidence.revision,0) IS DISTINCT FROM(body->>'expectedRevision')::integer OR at_time>as_of THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
   version=coalesce(previous_evidence.revision,0)+1;INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   INSERT INTO trading.gas_owned_evidence VALUES(catalog,event_id,account,body->>'sourceKey',version,previous_evidence.id,body->>'kind',quantity,at_time,body->>'evidence',actor,publication);
  END IF;
 ELSE
  outcome=trading.apply_gas_owned_financial(catalog,account,action,body,as_of,context,actor,publication);
  event_id=(outcome->>'id')::uuid;version=(outcome->>'revision')::integer;
 END IF;
 outcome=coalesce(outcome,'{}'::jsonb)||jsonb_build_object('operationId',operation,'accountId',account,'id',event_id,'revision',version,'changed',changed,'action',action);
 outcome=trading.gas_owned_outcome(catalog,account,action,body,outcome,NOT changed);
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,until_at,configuring,requires_jobs);
 PERFORM security.require_gas_owned_write(catalog,configuring,until_at);
 IF requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;RETURN outcome;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.gas_owned_visible(uuid,integer,timestamptz,uuid,timestamptz)'::regprocedure,
  'trading.require_gas_owned_head(uuid,uuid,uuid,timestamptz)'::regprocedure,
  'trading.gas_owned_keys(jsonb,text[])'::regprocedure,'trading.gas_owned_quantity(text)'::regprocedure,
  'trading.gas_owned_at(text)'::regprocedure,'trading.gas_owned_fact(jsonb)'::regprocedure,'trading.guard_gas_owned_terms()'::regprocedure,
  'trading.gas_owned_outcome(uuid,uuid,text,jsonb,jsonb,boolean)'::regprocedure,
  'trading.gas_owned_cashout_price(uuid,uuid,uuid,timestamptz,uuid)'::regprocedure,
  'trading.apply_gas_owned_financial(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer)'::regprocedure,
  'security.require_gas_owned_write(uuid,boolean,timestamptz)'::regprocedure,
  'security.record_gas_owned(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
CREATE FUNCTION trading.finish_gas_owned_command() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_owned_write(NEW.catalog_id,NEW.configuring,NEW.authority_until);
 IF NEW.requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION trading.finish_gas_owned_command() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.finish_gas_owned_command() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER command_admission AFTER INSERT ON trading.gas_owned_commands DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.finish_gas_owned_command();
GRANT EXECUTE ON FUNCTION security.record_gas_owned(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean) TO openetrm_runtime;
COMMIT;
