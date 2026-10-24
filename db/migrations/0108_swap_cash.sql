BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE settlement_data.swap_cash_payments (
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,amount ref_data.exact_number NOT NULL CHECK(amount<>0),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 cash_at ref_data.instant NOT NULL,reference text NOT NULL CHECK(length(btrim(reference)) BETWEEN 1 AND 200),reverses_id uuid,
 context_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),UNIQUE(catalog_id,reverses_id),UNIQUE(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,reverses_id,trade_id) REFERENCES settlement_data.swap_cash_payments(catalog_id,id,trade_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.swap_payment_allocations (
 catalog_id uuid NOT NULL,payment_id uuid NOT NULL,trade_id uuid NOT NULL,determination_id uuid NOT NULL,
 amount ref_data.exact_number NOT NULL CHECK(amount<>0),allocation_policy text NOT NULL CHECK(allocation_policy='explicit_swap_contribution_allocation_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,payment_id),
 FOREIGN KEY(catalog_id,payment_id,trade_id) REFERENCES settlement_data.swap_cash_payments(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,determination_id,trade_id) REFERENCES settlement_data.swap_determinations(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_cash_payments','swap_payment_allocations'] LOOP
  EXECUTE format('ALTER TABLE settlement_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE settlement_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON settlement_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON settlement_data.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
ALTER FUNCTION settlement_data.swap_state(uuid,uuid) RENAME TO swap_determination_state;
REVOKE ALL ON FUNCTION settlement_data.swap_determination_state(uuid,uuid) FROM openetrm_runtime;
CREATE FUNCTION settlement_data.swap_state(catalog uuid,trade uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 result=settlement_data.swap_determination_state(catalog,trade);
 result=result||jsonb_build_object('payments',coalesce((SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('determination_id',a.determination_id,'allocation',trim_scale(a.amount)::text) ORDER BY p.publication_order)
  FROM settlement_data.swap_cash_payments p JOIN settlement_data.swap_payment_allocations a ON(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)
  WHERE(p.catalog_id,p.trade_id)=(catalog,trade) AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)),'[]'::jsonb));
 PERFORM settlement_data.require_swap_authority(catalog,false);PERFORM settlement_data.require_swap_reference(catalog,trade);RETURN result;
END$$;
CREATE FUNCTION settlement_data.record_swap_cash(catalog uuid,operation uuid,trade uuid,expected_revision integer,expected_version integer,
 context uuid,as_of timestamptz,determination uuid,amount numeric,ref_catalog uuid,ref_revision uuid,currency text,cash_at timestamptz,reference text,reverses uuid,payload jsonb)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old jsonb;head integer;version integer;d settlement_data.swap_determinations;o settlement_data.swap_occurrences;
 original settlement_data.swap_cash_payments;allocation settlement_data.swap_payment_allocations;balance numeric;
 actor uuid;publication integer;identity uuid;outcome jsonb;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,true);PERFORM pg_advisory_xact_lock(730849102);
 PERFORM settlement_data.require_swap_authority(catalog,true);
 old=settlement_data.swap_command_status(catalog,operation,payload);IF old IS NOT NULL THEN RETURN old;END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR amount IS NULL OR amount=0 OR cash_at IS NULL OR as_of IS NULL
  OR NOT isfinite(cash_at) OR cash_at>as_of OR cash_at<='0001-01-01 00:00:00+00'::timestamptz OR length(btrim(reference)) NOT BETWEEN 1 AND 200 OR reference IS NULL
  OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date))
 THEN RAISE EXCEPTION 'settlement_cash_input';END IF;
 SELECT max(revision) INTO head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade);
 IF head IS DISTINCT FROM expected_revision THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
 SELECT coalesce(max(sequence),0) INTO version FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade);
 IF version IS DISTINCT FROM expected_version THEN RAISE EXCEPTION 'settlement_version_conflict';END IF;
 IF EXISTS(SELECT 1 FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade) AND c.effective_at>as_of)
 THEN RAISE EXCEPTION 'settlement_backdated_unsupported';END IF;
 SELECT * INTO d FROM settlement_data.swap_determinations s WHERE(s.catalog_id,s.id,s.trade_id)=(catalog,determination,trade);
 SELECT * INTO o FROM settlement_data.swap_occurrences s WHERE(s.catalog_id,s.id)=(catalog,d.occurrence_id);
 IF d.id IS NULL OR d.effective_at>as_of OR cash_at<d.effective_at OR(o.reference_catalog_id,o.reference_revision_id,o.currency_code) IS DISTINCT FROM(ref_catalog,ref_revision,currency)
  OR scale(trim_scale(amount))>o.decimal_places OR security.reference_access(ref_catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'settlement_allocation';END IF;
 IF reverses IS NULL THEN
  SELECT d.contribution-coalesce(sum(a.amount),0) INTO balance FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.determination_id)=(catalog,determination);
  IF sign(amount)<>sign(balance) OR abs(amount)>abs(balance) THEN RAISE EXCEPTION 'settlement_allocation';END IF;
 ELSE
  SELECT * INTO original FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.id,p.trade_id)=(catalog,reverses,trade);
  SELECT * INTO allocation FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,reverses);
  IF original.id IS NULL OR original.reverses_id IS NOT NULL OR original.amount<>-amount OR allocation.determination_id<>determination
   OR cash_at<original.cash_at OR EXISTS(SELECT 1 FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.reverses_id)=(catalog,reverses))
  THEN RAISE EXCEPTION 'settlement_reversal';END IF;
 END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());identity=gen_random_uuid();
 INSERT INTO settlement_data.swap_cash_payments VALUES(catalog,identity,trade,amount,ref_catalog,ref_revision,currency,cash_at,btrim(reference),reverses,context,operation,actor,publication,cash_at);
 INSERT INTO settlement_data.swap_payment_allocations VALUES(catalog,identity,trade,determination,amount,'explicit_swap_contribution_allocation_v1',publication,cash_at);
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'paymentId',identity,'determinationId',determination,'amount',trim_scale(amount)::text,'version',version+1);
 INSERT INTO settlement_data.swap_commands VALUES(catalog,operation,trade,version+1,actor,payload,outcome,publication,as_of);
 PERFORM settlement_data.require_swap_authority(catalog,true);PERFORM settlement_data.require_swap_reference(catalog,trade);RETURN outcome;
END$$;
ALTER FUNCTION settlement_data.swap_state(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION settlement_data.record_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,uuid,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.swap_state(uuid,uuid),settlement_data.record_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,uuid,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.swap_state(uuid,uuid),settlement_data.record_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,uuid,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) TO openetrm_runtime;
GRANT USAGE ON SCHEMA settlement_data TO openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION settlement_data.swap_state(uuid,uuid) TO openetrm_risk_worker;
COMMIT;
