BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE settlement_data.swap_payment_allocations DROP CONSTRAINT swap_payment_allocations_pkey,
 ADD PRIMARY KEY(catalog_id,payment_id,determination_id),
 DROP CONSTRAINT swap_payment_allocations_allocation_policy_check,
 ADD CHECK(allocation_policy IN('explicit_swap_contribution_allocation_v1','explicit_same_due_swap_net_v1'));
ALTER TABLE settlement_data.swap_cash_payments ADD COLUMN record_kind text NOT NULL DEFAULT 'cash_movement_v1';
ALTER TABLE settlement_data.swap_cash_payments ALTER COLUMN record_kind DROP DEFAULT,
 DROP CONSTRAINT swap_cash_payments_amount_check,
 ADD CHECK((record_kind='cash_movement_v1' AND amount<>0) OR(record_kind='zero_net_discharge_v1' AND amount=0));
CREATE OR REPLACE FUNCTION settlement_data.swap_state(catalog uuid,trade uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 result=settlement_data.swap_determination_state(catalog,trade);
 result=result||jsonb_build_object('payments',coalesce((SELECT jsonb_agg(to_jsonb(p)||
  jsonb_build_object('determination_id',CASE WHEN (SELECT count(*) FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(p.catalog_id,p.id))=1
   THEN(SELECT a.determination_id FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)) END,
  'allocation',CASE WHEN (SELECT count(*) FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(p.catalog_id,p.id))=1 THEN trim_scale(p.amount)::text END) ORDER BY p.publication_order)
  FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.trade_id)=(catalog,trade)
   AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)),'[]'::jsonb),
 'allocations',coalesce((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.publication_order,a.determination_id)
  FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
   AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)),'[]'::jsonb));
 PERFORM settlement_data.require_swap_authority(catalog,false);PERFORM settlement_data.require_swap_reference(catalog,trade);RETURN result;
END$$;
CREATE FUNCTION settlement_data.apply_swap_cash(catalog uuid,operation uuid,trade uuid,expected_revision integer,expected_version integer,
 context uuid,as_of timestamptz,lines jsonb,amount numeric,ref_catalog uuid,ref_revision uuid,currency text,cash_at timestamptz,reference text,reverses uuid,payload jsonb,policy text)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old jsonb;head integer;version integer;d settlement_data.swap_determinations;o settlement_data.swap_occurrences;
 original settlement_data.swap_cash_payments;balance numeric;line record;common jsonb;terms jsonb;origin trading.swap_trade_revisions;current_head trading.swap_trade_revisions;
 actor uuid;publication integer;identity uuid;outcome jsonb;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,true);PERFORM pg_advisory_xact_lock(730849102);
 PERFORM settlement_data.require_swap_authority(catalog,true);
 old=settlement_data.swap_command_status(catalog,operation,payload);IF old IS NOT NULL THEN RETURN old;END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR amount IS NULL OR cash_at IS NULL OR as_of IS NULL
  OR NOT isfinite(cash_at) OR cash_at>as_of OR cash_at<='0001-01-01 00:00:00+00'::timestamptz OR length(btrim(reference)) NOT BETWEEN 1 AND 200 OR reference IS NULL
  OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date))
 THEN RAISE EXCEPTION 'settlement_cash_input';END IF;
 IF lines IS NULL OR jsonb_typeof(lines)<>'array' OR policy IS NULL OR policy NOT IN('explicit_swap_contribution_allocation_v1','explicit_same_due_swap_net_v1')
 THEN RAISE EXCEPTION 'settlement_net_allocation';END IF;
 IF jsonb_array_length(lines) NOT BETWEEN 1 AND 256 OR (policy='explicit_swap_contribution_allocation_v1' AND (jsonb_array_length(lines)<>1 OR amount=0))
  OR(policy='explicit_same_due_swap_net_v1' AND jsonb_array_length(lines)<2)
  OR(SELECT count(DISTINCT x.determination) FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric))<>jsonb_array_length(lines)
  OR(SELECT sum(x.amount) FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric)) IS DISTINCT FROM amount
 THEN RAISE EXCEPTION 'settlement_net_allocation';END IF;
 SELECT * INTO current_head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 head=current_head.revision;
 IF head IS DISTINCT FROM expected_revision THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
 SELECT coalesce(max(sequence),0) INTO version FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade);
 IF version IS DISTINCT FROM expected_version THEN RAISE EXCEPTION 'settlement_version_conflict';END IF;
 IF EXISTS(SELECT 1 FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade) AND c.effective_at>as_of)
 THEN RAISE EXCEPTION 'settlement_backdated_unsupported';END IF;
 IF reverses IS NOT NULL THEN
  SELECT * INTO original FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.id,p.trade_id)=(catalog,reverses,trade);
  IF original.id IS NULL OR original.reverses_id IS NOT NULL OR original.amount<>-amount OR cash_at<original.cash_at
   OR(original.reference_catalog_id,original.reference_revision_id,original.currency_code) IS DISTINCT FROM(ref_catalog,ref_revision,currency)
   OR EXISTS(SELECT 1 FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.reverses_id)=(catalog,reverses))
   OR EXISTS((SELECT a.determination_id,-a.amount,a.allocation_policy FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,reverses)
     EXCEPT SELECT x.determination,x.amount,policy FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric))
    UNION ALL(SELECT x.determination,x.amount,policy FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric)
     EXCEPT SELECT a.determination_id,-a.amount,a.allocation_policy FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,reverses)))
  THEN RAISE EXCEPTION 'settlement_reversal';END IF;
 END IF;
 FOR line IN SELECT * FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric) LOOP
  SELECT * INTO d FROM settlement_data.swap_determinations s WHERE(s.catalog_id,s.id,s.trade_id)=(catalog,line.determination,trade);
  SELECT * INTO o FROM settlement_data.swap_occurrences s WHERE(s.catalog_id,s.id)=(catalog,d.occurrence_id);
  IF d.id IS NULL OR d.effective_at>as_of OR cash_at<d.effective_at OR(o.reference_catalog_id,o.reference_revision_id,o.currency_code) IS DISTINCT FROM(ref_catalog,ref_revision,currency)
   OR line.amount IS NULL OR line.amount=0 OR scale(trim_scale(line.amount))>o.decimal_places OR security.reference_access(ref_catalog,'ReadMarketData') IS NOT TRUE
  THEN RAISE EXCEPTION 'settlement_allocation';END IF;
  IF reverses IS NULL THEN
   SELECT d.contribution-coalesce(sum(a.amount),0) INTO balance FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.determination_id)=(catalog,line.determination);
   IF sign(line.amount)<>sign(balance) OR abs(line.amount)>abs(balance) THEN RAISE EXCEPTION 'settlement_allocation';END IF;
  END IF;
  IF policy='explicit_same_due_swap_net_v1' THEN
   SELECT * INTO origin FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,trade,o.origin_revision);
   IF(origin.legal_entity_id,origin.legal_entity_revision,origin.counterparty_id,origin.counterparty_revision)
    IS DISTINCT FROM(current_head.legal_entity_id,current_head.legal_entity_revision,current_head.counterparty_id,current_head.counterparty_revision)
   THEN RAISE EXCEPTION 'settlement_net_eligibility';END IF;
   terms=jsonb_build_array(origin.legal_entity_id,origin.legal_entity_revision,origin.counterparty_id,origin.counterparty_revision,
    o.reference_catalog_id,o.reference_revision_id,o.currency_code,o.decimal_places,o.rounding_policy,o.due_policy,o.recognition_policy,o.correction_policy,d.due_at);
   IF common IS NOT NULL AND common<>terms THEN RAISE EXCEPTION 'settlement_net_eligibility';END IF;common=terms;
  END IF;
 END LOOP;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());identity=gen_random_uuid();
 INSERT INTO settlement_data.swap_cash_payments VALUES(catalog,identity,trade,amount,ref_catalog,ref_revision,currency,cash_at,btrim(reference),reverses,context,operation,actor,publication,cash_at,
  CASE WHEN amount=0 THEN 'zero_net_discharge_v1' ELSE 'cash_movement_v1' END);
 INSERT INTO settlement_data.swap_payment_allocations SELECT catalog,identity,trade,x.determination,x.amount,policy,publication,cash_at FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric);
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'paymentId',identity,'amount',trim_scale(amount)::text,'version',version+1,'allocations',lines,
  'recordKind',CASE WHEN amount=0 THEN 'zero_net_discharge_v1' ELSE 'cash_movement_v1' END);
 IF jsonb_array_length(lines)=1 THEN outcome=outcome||jsonb_build_object('determinationId',lines->0->>'determination');END IF;
 INSERT INTO settlement_data.swap_commands VALUES(catalog,operation,trade,version+1,actor,payload,outcome,publication,as_of);
 PERFORM settlement_data.require_swap_authority(catalog,true);PERFORM settlement_data.require_swap_reference(catalog,trade);RETURN outcome;
END$$;
CREATE OR REPLACE FUNCTION settlement_data.record_swap_cash(catalog uuid,operation uuid,trade uuid,expected_revision integer,expected_version integer,
 context uuid,as_of timestamptz,determination uuid,amount numeric,ref_catalog uuid,ref_revision uuid,currency text,cash_at timestamptz,reference text,reverses uuid,payload jsonb)
 RETURNS jsonb LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT settlement_data.apply_swap_cash(catalog,operation,trade,expected_revision,expected_version,context,as_of,
  jsonb_build_array(jsonb_build_object('determination',determination,'amount',amount)),amount,ref_catalog,ref_revision,currency,cash_at,reference,reverses,payload,'explicit_swap_contribution_allocation_v1');
$$;
CREATE FUNCTION settlement_data.record_swap_net(catalog uuid,operation uuid,trade uuid,expected_revision integer,expected_version integer,
 context uuid,as_of timestamptz,lines jsonb,amount numeric,ref_catalog uuid,ref_revision uuid,currency text,cash_at timestamptz,reference text,reverses uuid,payload jsonb)
 RETURNS jsonb LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT settlement_data.apply_swap_cash(catalog,operation,trade,expected_revision,expected_version,context,as_of,lines,amount,ref_catalog,ref_revision,currency,cash_at,reference,reverses,payload,'explicit_same_due_swap_net_v1');
$$;
ALTER FUNCTION settlement_data.apply_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.apply_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb,text) FROM PUBLIC;
ALTER FUNCTION settlement_data.record_swap_net(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.record_swap_net(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.record_swap_net(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) TO openetrm_runtime;
COMMIT;
