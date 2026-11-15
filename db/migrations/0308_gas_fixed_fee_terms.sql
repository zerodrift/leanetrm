-- A loan's stated fee survives independently of its gross cash memberships.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.gas_owned_fees ADD CONSTRAINT gas_fee_exact_origin UNIQUE(catalog_id,id,position_id,loan_id);
CREATE TABLE trading.gas_loan_fee_terms(
 catalog_id uuid NOT NULL,loan_id uuid NOT NULL,account_id uuid NOT NULL,position_id uuid NOT NULL,
 origin_request_id uuid NOT NULL,original_fee_id uuid,
 revision integer NOT NULL CHECK(revision>0),previous_revision integer,
 amount ref_data.exact_number NOT NULL CHECK(amount<=0),due_at ref_data.instant,effective_at ref_data.instant NOT NULL,
 implementation text NOT NULL CHECK(implementation IN('native_fixed_loan_fee_origin_v1','gross_timed_fixed_fee_adjustments_v1')),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,loan_id,revision),UNIQUE(catalog_id,loan_id,publication_order),
 UNIQUE NULLS NOT DISTINCT(catalog_id,loan_id,amount,due_at,effective_at),
 FOREIGN KEY(catalog_id,loan_id) REFERENCES trading.gas_owned_loans(catalog_id,id),
 FOREIGN KEY(catalog_id,account_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,origin_request_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id),
 FOREIGN KEY(catalog_id,original_fee_id,position_id,loan_id) REFERENCES trading.gas_owned_fees(catalog_id,id,position_id,loan_id),
 FOREIGN KEY(catalog_id,loan_id,previous_revision) REFERENCES trading.gas_loan_fee_terms(catalog_id,loan_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK((amount=0)=(due_at IS NULL)),
 CHECK(revision=1 AND previous_revision IS NULL AND implementation='native_fixed_loan_fee_origin_v1'
  OR revision>1 AND previous_revision IS NOT NULL AND previous_revision=revision-1 AND implementation='gross_timed_fixed_fee_adjustments_v1')
);
ALTER TABLE trading.gas_loan_fee_terms OWNER TO openetrm_security_owner;
ALTER TABLE trading.gas_loan_fee_terms ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_loan_fee_terms FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON trading.gas_loan_fee_terms TO openetrm_security_owner USING(true) WITH CHECK(true);
REVOKE ALL ON trading.gas_loan_fee_terms FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.capture_loan_fee_origin(command trading.gas_owned_commands) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE loan trading.gas_owned_loans;stock trading.gas_owned_positions;fee trading.gas_owned_fees;
 amount_value numeric;due_value timestamptz;counted integer;BEGIN
 -- A semantic acknowledgement is not the original accepted loan publication.
 IF command.request->>'action' IS DISTINCT FROM 'borrow' OR command.outcome->>'changed' IS DISTINCT FROM 'true'
  OR command.outcome->>'semanticAcknowledgement' IS DISTINCT FROM 'false' THEN RETURN;END IF;
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=
  (command.catalog_id,(command.outcome->>'id')::uuid,command.account_id);
 SELECT * INTO STRICT stock FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id,p.account_id)=
  (loan.catalog_id,loan.received_lot_id,loan.account_id);
 amount_value=trading.gas_owned_quantity(command.request#>>'{body,feeAmount}');
 due_value=CASE WHEN command.request#>>'{body,feeDueAt}' IS NULL THEN NULL ELSE trading.gas_owned_at(command.request#>>'{body,feeDueAt}') END;
 SELECT count(*) INTO counted FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.loan_id,f.position_id,f.publication_order)=
  (loan.catalog_id,loan.id,loan.return_position_id,stock.publication_order);
 SELECT * INTO fee FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.loan_id,f.position_id,f.publication_order)=
  (loan.catalog_id,loan.id,loan.return_position_id,stock.publication_order);
 IF amount_value>0 OR(amount_value=0)<>(due_value IS NULL) OR counted<>(CASE WHEN amount_value=0 THEN 0 ELSE 1 END)
  OR amount_value<>0 AND(fee.amount,fee.due_at,fee.effective_at,fee.actor_id) IS DISTINCT FROM(amount_value,due_value,stock.effective_at,stock.actor_id)
  OR command.actor_id<>stock.actor_id OR command.request#>>'{body,titleAt}' IS NULL
  OR trading.gas_owned_at(command.request#>>'{body,titleAt}')<>stock.effective_at
 THEN RAISE EXCEPTION 'gas_fee_terms_origin';END IF;
 INSERT INTO trading.gas_loan_fee_terms VALUES(loan.catalog_id,loan.id,loan.account_id,loan.return_position_id,command.request_id,fee.id,
  1,NULL,amount_value,due_value,stock.effective_at,'native_fixed_loan_fee_origin_v1',stock.actor_id,stock.publication_order);
END$$;
ALTER FUNCTION trading.capture_loan_fee_origin(trading.gas_owned_commands) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.capture_loan_fee_origin(trading.gas_owned_commands) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
DO $$DECLARE item trading.gas_owned_commands;BEGIN
 FOR item IN SELECT c.* FROM trading.gas_owned_commands c WHERE c.request->>'action'='borrow'
  AND c.outcome->>'changed'='true' AND c.outcome->>'semanticAcknowledgement'='false' LOOP
  PERFORM trading.capture_loan_fee_origin(item);
 END LOOP;
 IF(SELECT count(*) FROM trading.gas_loan_fee_terms)<>(SELECT count(*) FROM trading.gas_owned_loans)
 THEN RAISE EXCEPTION 'gas_fee_terms_upgrade: missing exact native loan origin';END IF;
END$$;
CREATE FUNCTION trading.seed_loan_fee_origin() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM trading.capture_loan_fee_origin(NEW);RETURN NEW;END$$;
ALTER FUNCTION trading.seed_loan_fee_origin() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.seed_loan_fee_origin() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TRIGGER initial_loan_fee_terms AFTER INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.seed_loan_fee_origin();
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_loan_fee_terms FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER native_publication BEFORE INSERT ON trading.gas_loan_fee_terms FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication();

CREATE FUNCTION security.read_gas_loan_fee_terms(catalog uuid,loan_id uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;loan trading.gas_owned_loans;owner trading.gas_owned_accounts;head trading.gas_loan_fee_terms;fee trading.gas_owned_fees;
 cash_count bigint;cash_bytes bigint;payment_count bigint;payment_bytes bigint;result jsonb;BEGIN
 until_at=security.gas_futures_delivery_discovery_authority(catalog);
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 SELECT * INTO loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id)=(catalog,loan_id);
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_fee_terms_source';END IF;
 SELECT * INTO head FROM trading.gas_loan_fee_terms t WHERE(t.catalog_id,t.loan_id)=($1,$2)
  AND trading.gas_owned_visible(catalog,t.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
  ORDER BY t.revision DESC LIMIT 1;
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_fee_terms_source';END IF;
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,loan.account_id)
  AND trading.gas_owned_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of);
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE OR NOT EXISTS(
   SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,loan.lender_id,loan.lender_revision)
    AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 SELECT * INTO fee FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=(catalog,head.original_fee_id);
 SELECT count(*),coalesce(sum(octet_length(convert_to(to_jsonb(f)::text,'UTF8'))),0) INTO cash_count,cash_bytes
  FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.loan_id)=(catalog,loan.id)
   AND trading.gas_owned_visible(catalog,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of);
 SELECT count(*),coalesce(sum(octet_length(convert_to(to_jsonb(p)::text,'UTF8'))),0) INTO payment_count,payment_bytes
  FROM trading.gas_owned_fee_payments p JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id)
   WHERE(f.catalog_id,f.loan_id)=(catalog,loan.id)
   AND trading.gas_owned_visible(catalog,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
   AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of);
 IF cash_count>16384 OR payment_count>16384 OR cash_bytes+payment_bytes+4*(cash_count+payment_count)+8192>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 SELECT jsonb_build_object('implementation','gas_fixed_loan_fee_terms_source_v1','loanId',loan.id,'accountId',owner.id,
  'contextId',context,'asOf',as_of,'authorityUntil',until_at,'rootFee',CASE WHEN fee.id IS NULL THEN 'null'::jsonb ELSE to_jsonb(fee) END,
  'originRequestId',head.origin_request_id,'positionId',loan.return_position_id,
  'terms',jsonb_build_object('revision',head.revision,'amount',trim_scale(head.amount)::text,'dueAt',head.due_at,
   'effectiveAt',head.effective_at,'implementation',head.implementation,'publicationOrder',head.publication_order),
  'cash',coalesce((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.publication_order,f.id) FROM trading.gas_owned_fees f
   WHERE(f.catalog_id,f.loan_id)=(catalog,loan.id)
    AND trading.gas_owned_visible(catalog,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)),'[]'),
  'payments',coalesce((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.fee_id,p.business_key,p.revision) FROM trading.gas_owned_fee_payments p
   JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id)
   WHERE(f.catalog_id,f.loan_id)=(catalog,loan.id)
    AND trading.gas_owned_visible(catalog,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
    AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)),'[]'),
  'currentPaid',(SELECT trim_scale(coalesce(sum(p.amount),0))::text FROM(
   SELECT DISTINCT ON(p.fee_id,p.business_key)p.amount FROM trading.gas_owned_fee_payments p
    JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id)
    WHERE(f.catalog_id,f.loan_id)=(catalog,loan.id)
     AND trading.gas_owned_visible(catalog,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
     AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
    ORDER BY p.fee_id,p.business_key,p.revision DESC)p)) INTO result;
 IF octet_length(convert_to(result::text,'UTF8'))>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.read_gas_loan_fee_terms(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_loan_fee_terms(uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.read_gas_loan_fee_terms(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;

INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 VALUES('gas.loan.amend_fee',false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings');
-- The deferred seal checks every ordinary account head. Only the new cash
-- proved by this exact loan amendment is excluded from its predecessor read.
DO $upgrade$DECLARE p pg_proc;body text;needle text;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('trading.require_gas_owned_head(uuid,uuid,uuid,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_fee_terms_upgrade: missing account head guard';END IF;
 IF md5(p.prosrc)<>'8922bc66eeed0088f983e222ddc3a409' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'void'::regtype
  OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR p.prosecdef
  OR p.proargnames IS DISTINCT FROM ARRAY['catalog','account','context','as_of'] OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL
  OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.provolatile<>'s' OR p.proparallel<>'u' OR p.procost<>100 OR p.prorows<>0
  OR p.probin IS NOT NULL OR p.prosqlbody IS NOT NULL OR p.prosupport<>0 OR p.protrftypes IS NOT NULL
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'gas_fee_terms_upgrade: unexpected account head guard';END IF;
 body=p.prosrc;
 needle='FROM trading.gas_owned_fees f JOIN trading.gas_owned_positions_current p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)';
 IF(length(body)-length(replace(body,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_fee_terms_upgrade: missing exact cash boundary';END IF;
 body=replace(body,needle,needle||' AND (own_publication IS NULL OR(f.loan_id,f.publication_order) IS DISTINCT FROM(own_loan,own_publication))');
 EXECUTE 'CREATE FUNCTION trading.require_gas_fee_account_head(catalog uuid,account uuid,context uuid,as_of timestamptz,own_loan uuid,own_publication integer) RETURNS void LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS '||quote_literal(body);
END$upgrade$;
ALTER FUNCTION trading.require_gas_fee_account_head(uuid,uuid,uuid,timestamptz,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_fee_account_head(uuid,uuid,uuid,timestamptz,uuid,integer) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TABLE trading.gas_loan_fee_changes(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,loan_id uuid NOT NULL,
 current_revision integer NOT NULL,acknowledged_revision integer NOT NULL,changed boolean NOT NULL,
 publication_order integer,read_publication_order integer NOT NULL,source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,loan_id,current_revision) REFERENCES trading.gas_loan_fee_terms(catalog_id,loan_id,revision),
 FOREIGN KEY(catalog_id,loan_id,acknowledged_revision) REFERENCES trading.gas_loan_fee_terms(catalog_id,loan_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,read_publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 CHECK(changed=(publication_order IS NOT NULL)),CHECK(NOT changed OR acknowledged_revision=current_revision),
 CHECK(publication_order IS NULL OR publication_order=read_publication_order+1)
);
CREATE TABLE trading.gas_loan_fee_cash_members(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal IN(1,2)),
 kind text NOT NULL CHECK(kind IN('reverse_prior_fee','revised_fee')),fee_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,operation_id,ordinal),UNIQUE(catalog_id,fee_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_loan_fee_changes(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,fee_id) REFERENCES trading.gas_owned_fees(catalog_id,id),
 CHECK((ordinal=1)=(kind='reverse_prior_fee'))
);

CREATE FUNCTION trading.gas_loan_fee_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE amount_value numeric;due_value timestamptz;BEGIN
 PERFORM trading.gas_owned_projection_keys(body,ARRAY['loanId','expectedRevision','amount','dueAt','effectiveAt','implementation','evidence'],ARRAY['dueAt']);
 IF jsonb_typeof(body->'loanId') IS DISTINCT FROM 'string' OR(body->>'loanId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'amount') IS DISTINCT FROM 'string' OR jsonb_typeof(body->'effectiveAt') IS DISTINCT FROM 'string'
  OR jsonb_typeof(body->'dueAt') NOT IN('string','null') OR NOT(body?'dueAt')
  OR body->>'implementation' IS DISTINCT FROM 'gross_timed_fixed_fee_adjustments_v1'
  OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 2000
 THEN RAISE EXCEPTION 'gas_fee_terms_input';END IF;
 amount_value=trading.gas_owned_quantity(body->>'amount');
 due_value=CASE WHEN body->>'dueAt' IS NULL THEN NULL ELSE trading.gas_owned_at(body->>'dueAt') END;
 IF amount_value>0 OR(amount_value=0)<>(due_value IS NULL) THEN RAISE EXCEPTION 'gas_fee_terms_input';END IF;
 RETURN trading.gas_owned_fact(body)||jsonb_build_object('evidence',btrim(body->>'evidence'));
END$$;
CREATE FUNCTION trading.require_gas_loan_fee_references(catalog uuid,account uuid,loan_id uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE owner trading.gas_owned_accounts;loan trading.gas_owned_loans;BEGIN
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=($1,$3,$2);
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,loan.lender_id,loan.lender_revision)
  )
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION trading.require_gas_loan_fee_context(catalog uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF context IS NULL OR as_of IS NULL OR NOT isfinite(as_of) OR NOT EXISTS(
  SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts c ON(c.catalog_id,c.context_id)=(t.catalog_id,t.id)
   WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of))
 THEN RAISE EXCEPTION 'gas_owned_context';END IF;
END$$;
CREATE FUNCTION trading.require_gas_loan_fee_source(catalog uuid,account uuid,loan_id uuid,context uuid,as_of timestamptz,
 policy uuid,policy_revision integer,own_publication integer) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE owner trading.gas_owned_accounts;loan trading.gas_owned_loans;binding trading.gas_owned_action_bindings;
 ancestors uuid[];books uuid[];BEGIN
 PERFORM trading.require_gas_loan_fee_context(catalog,context,as_of);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=($1,$3,$2);
 IF trading.gas_owned_visible(catalog,owner.publication_order,owner.effective_at,context,as_of) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,loan.lender_id,loan.lender_revision)
   AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of))
 THEN RAISE EXCEPTION 'gas_owned_context';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 PERFORM trading.require_gas_fee_account_head(catalog,account,context,as_of,loan.id,own_publication);
 IF EXISTS(SELECT 1 FROM trading.gas_loan_fee_terms t WHERE(t.catalog_id,t.account_id)=(catalog,account)
  AND(own_publication IS NULL OR(t.loan_id,t.publication_order) IS DISTINCT FROM(loan.id,own_publication))
  AND trading.gas_owned_visible(catalog,t.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE)
 THEN RAISE EXCEPTION 'gas_fee_terms_revision';END IF;
 SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,'gas.loan.amend_fee') ORDER BY b.revision DESC LIMIT 1;
 IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM(policy,policy_revision)
  OR trading.gas_owned_visible(catalog,binding.publication_order,binding.effective_at,context,as_of) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
 PERFORM lifecycle.require_policy(catalog,policy,policy_revision,'gas.loan.amend_fee',as_of,false);
 ancestors=trading.gas_owned_financial_ancestors(catalog,ARRAY[loan.received_lot_id,loan.return_position_id],context,as_of);
 SELECT coalesce(array_agg(DISTINCT a.book_id),'{}') INTO books FROM trading.gas_owned_accounts a JOIN trading.gas_owned_positions p ON(p.catalog_id,p.account_id)=(a.catalog_id,a.id)
  WHERE p.catalog_id=catalog AND p.id=ANY(ancestors);
 IF trading.gas_owned_applied_source_conflicts(catalog,context,as_of,books,ancestors)||
    trading.gas_owned_applied_observation_conflicts(catalog,context,as_of,books,ancestors)<>'[]'::jsonb
 THEN RAISE EXCEPTION 'gas_owned_pending_source_reconciliation';END IF;
END$$;
CREATE FUNCTION trading.gas_loan_fee_outcome(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('operationId',h.operation_id,'accountId',h.account_id,'loanId',h.loan_id,'action','loan_fee_amendment',
  'currency',a.currency_code,
  'changed',h.changed,'semanticAcknowledgement',NOT h.changed,'publicationOrder',h.publication_order,
  'acknowledgedRevision',h.acknowledged_revision,'currentRevision',h.current_revision,'currentAmount',trim_scale(t.amount)::text,
  'currentDueAt',t.due_at,'currentEffectiveAt',t.effective_at,
  'cashChanges',coalesce((SELECT jsonb_agg(jsonb_build_object('kind',m.kind,'feeId',f.id,'amount',trim_scale(f.amount)::text,'dueAt',f.due_at,'effectiveAt',f.effective_at) ORDER BY m.ordinal)
   FROM trading.gas_loan_fee_cash_members m JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(m.catalog_id,m.fee_id)
   WHERE(m.catalog_id,m.operation_id)=(h.catalog_id,h.operation_id)),'[]'))
 FROM trading.gas_loan_fee_changes h JOIN trading.gas_loan_fee_terms t ON(t.catalog_id,t.loan_id,t.revision)=(h.catalog_id,h.loan_id,h.current_revision)
 JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(h.catalog_id,h.account_id)
 WHERE(h.catalog_id,h.operation_id)=($1,$2)
$$;
CREATE FUNCTION security.amend_gas_loan_fee_terms(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,
 policy uuid,policy_revision integer,receipt_only boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;head trading.gas_loan_fee_terms;
 owner trading.gas_owned_accounts;acknowledged integer;publication integer;read_publication integer;fee_id uuid;outcome jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,'loan_fee_amendment',body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR account IS NULL OR context IS NULL OR policy IS NULL
  OR policy_revision IS NULL OR policy_revision<1 OR as_of IS NULL OR NOT isfinite(as_of) OR receipt_only IS NULL
 THEN RAISE EXCEPTION 'gas_fee_terms_input';END IF;
 body=trading.gas_loan_fee_fact(body);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 canonical=jsonb_build_object('action','loan_fee_amendment','accountId',account,'body',body,'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_revision,'automatic',false);
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 PERFORM trading.require_gas_loan_fee_references(catalog,account,(body->>'loanId')::uuid);
 IF NOT EXISTS(SELECT 1 FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision,p.action)=(catalog,policy,policy_revision,'gas.loan.amend_fee'))
 THEN RAISE EXCEPTION 'gas_owned_policy';END IF;
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome);END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO STRICT head FROM trading.gas_loan_fee_terms t WHERE(t.catalog_id,t.loan_id)=(catalog,(body->>'loanId')::uuid) ORDER BY revision DESC LIMIT 1;
 SELECT revision INTO acknowledged FROM trading.gas_loan_fee_terms t WHERE(t.catalog_id,t.loan_id,t.amount,t.effective_at)=
  (catalog,head.loan_id,trading.gas_owned_quantity(body->>'amount'),trading.gas_owned_at(body->>'effectiveAt'))
   AND t.due_at IS NOT DISTINCT FROM CASE WHEN body->>'dueAt' IS NULL THEN NULL ELSE trading.gas_owned_at(body->>'dueAt') END;
 IF acknowledged IS NULL AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF acknowledged IS NOT NULL THEN PERFORM trading.require_gas_loan_fee_context(catalog,context,as_of);END IF;
 SELECT max(p.publication_order) INTO read_publication FROM trading.publications p WHERE p.catalog_id=catalog;
 IF acknowledged IS NULL THEN
  PERFORM trading.require_gas_loan_fee_source(catalog,account,head.loan_id,context,as_of,policy,policy_revision,NULL);
  IF head.revision<>(body->>'expectedRevision')::integer OR trading.gas_owned_visible(catalog,head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR trading.gas_owned_at(body->>'effectiveAt')<head.effective_at OR trading.gas_owned_at(body->>'effectiveAt')>as_of
  THEN RAISE EXCEPTION 'gas_fee_terms_revision';END IF;
  publication=read_publication+1;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_loan_fee_terms VALUES(catalog,head.loan_id,account,head.position_id,head.origin_request_id,head.original_fee_id,
   head.revision+1,head.revision,trading.gas_owned_quantity(body->>'amount'),
   CASE WHEN body->>'dueAt' IS NULL THEN NULL ELSE trading.gas_owned_at(body->>'dueAt') END,trading.gas_owned_at(body->>'effectiveAt'),
   body->>'implementation',actor,publication);
  INSERT INTO trading.gas_loan_fee_changes VALUES(catalog,operation,account,head.loan_id,head.revision+1,head.revision+1,true,publication,read_publication,context,as_of,actor);
  SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
  IF head.amount<>0 THEN
   fee_id=gen_random_uuid();
   INSERT INTO trading.gas_owned_fees VALUES(catalog,fee_id,head.position_id,head.loan_id,-head.amount,
    owner.reference_catalog_id,owner.reference_revision_id,owner.currency_code,head.due_at,actor,publication,trading.gas_owned_at(body->>'effectiveAt'));
   INSERT INTO trading.gas_loan_fee_cash_members VALUES(catalog,operation,1,'reverse_prior_fee',fee_id);
  END IF;
  IF trading.gas_owned_quantity(body->>'amount')<>0 THEN
   fee_id=gen_random_uuid();
   INSERT INTO trading.gas_owned_fees VALUES(catalog,fee_id,head.position_id,head.loan_id,trading.gas_owned_quantity(body->>'amount'),
    owner.reference_catalog_id,owner.reference_revision_id,owner.currency_code,trading.gas_owned_at(body->>'dueAt'),actor,publication,trading.gas_owned_at(body->>'effectiveAt'));
   INSERT INTO trading.gas_loan_fee_cash_members VALUES(catalog,operation,2,'revised_fee',fee_id);
  END IF;
 ELSE
  INSERT INTO trading.gas_loan_fee_changes VALUES(catalog,operation,account,head.loan_id,head.revision,acknowledged,false,NULL,read_publication,context,as_of,actor);
 END IF;
 outcome=trading.gas_loan_fee_outcome(catalog,operation);
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome);END IF;RETURN outcome;
END$$;
DO $$DECLARE name text;f regprocedure;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_loan_fee_changes','gas_loan_fee_cash_members'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('REVOKE ALL ON trading.%I FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
 FOREACH f IN ARRAY ARRAY['trading.gas_loan_fee_fact(jsonb)'::regprocedure,'trading.require_gas_loan_fee_references(uuid,uuid,uuid)'::regprocedure,
  'trading.require_gas_loan_fee_context(uuid,uuid,timestamptz)'::regprocedure,
  'trading.require_gas_loan_fee_source(uuid,uuid,uuid,uuid,timestamptz,uuid,integer,integer)'::regprocedure,'trading.gas_loan_fee_outcome(uuid,uuid)'::regprocedure,
  'security.amend_gas_loan_fee_terms(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.amend_gas_loan_fee_terms(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean) TO openetrm_runtime;

CREATE FUNCTION trading.check_gas_loan_fee_change() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operation uuid;h trading.gas_loan_fee_changes;c trading.gas_owned_commands;loan trading.gas_owned_loans;
 owner trading.gas_owned_accounts;current_terms trading.gas_loan_fee_terms;acknowledged trading.gas_loan_fee_terms;previous trading.gas_loan_fee_terms;
 through_publication integer;counted integer;expected_count integer;cash record;expected_amount numeric;expected_due timestamptz;BEGIN
 IF TG_TABLE_NAME='gas_owned_commands' THEN
  IF NEW.request->>'action' IS DISTINCT FROM 'loan_fee_amendment' THEN RETURN NULL;END IF;
  operation=NEW.request_id;
 ELSE operation=NEW.operation_id;END IF;
 SELECT * INTO STRICT h FROM trading.gas_loan_fee_changes x WHERE(x.catalog_id,x.operation_id)=(NEW.catalog_id,operation);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(h.catalog_id,h.operation_id);
 PERFORM security.require_gas_owned_commit(h.catalog_id,c.authority_until,false,false);
 PERFORM trading.gas_owned_projection_keys(c.request,ARRAY['action','accountId','body','asOf','contextId','policyId','policyRevision','automatic'],ARRAY[]::text[]);
 IF jsonb_typeof(c.request->'accountId') IS DISTINCT FROM 'string' OR jsonb_typeof(c.request->'contextId') IS DISTINCT FROM 'string'
  OR jsonb_typeof(c.request->'asOf') IS DISTINCT FROM 'string' OR jsonb_typeof(c.request->'policyId') IS DISTINCT FROM 'string'
  OR jsonb_typeof(c.request->'policyRevision') IS DISTINCT FROM 'number' OR c.request->>'policyRevision'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(c.request->'automatic') IS DISTINCT FROM 'boolean'
  OR trading.gas_loan_fee_fact(c.request->'body') IS DISTINCT FROM c.request->'body'
  OR(c.actor_id,c.account_id,c.request->>'action',(c.request->>'accountId')::uuid,(c.request->>'contextId')::uuid,
   (c.request->>'asOf')::timestamptz,(c.request->>'automatic')::boolean,c.configuring,c.requires_jobs) IS DISTINCT FROM
   (h.actor_id,h.account_id,'loan_fee_amendment',h.account_id,h.source_context_id,h.as_of,false,false,false)
  OR(c.request#>>'{body,loanId}')::uuid<>h.loan_id
  OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision,p.action)=
   (h.catalog_id,(c.request->>'policyId')::uuid,(c.request->>'policyRevision')::integer,'gas.loan.amend_fee'))
 THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 PERFORM trading.require_gas_loan_fee_references(h.catalog_id,h.account_id,h.loan_id);
 PERFORM trading.require_gas_loan_fee_context(h.catalog_id,h.source_context_id,h.as_of);
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans x WHERE(x.catalog_id,x.id,x.account_id)=(h.catalog_id,h.loan_id,h.account_id);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(h.catalog_id,h.account_id);
 through_publication=coalesce(h.publication_order,h.read_publication_order);
 SELECT * INTO STRICT current_terms FROM trading.gas_loan_fee_terms x WHERE(x.catalog_id,x.loan_id)=(h.catalog_id,h.loan_id)
  AND x.publication_order<=through_publication ORDER BY revision DESC LIMIT 1;
 SELECT * INTO STRICT acknowledged FROM trading.gas_loan_fee_terms x WHERE(x.catalog_id,x.loan_id,x.revision)=(h.catalog_id,h.loan_id,h.acknowledged_revision);
 IF current_terms.revision<>h.current_revision OR acknowledged.publication_order>through_publication
  OR(acknowledged.amount,acknowledged.due_at,acknowledged.effective_at) IS DISTINCT FROM
   (trading.gas_owned_quantity(c.request#>>'{body,amount}'),CASE WHEN c.request#>>'{body,dueAt}' IS NULL THEN NULL ELSE trading.gas_owned_at(c.request#>>'{body,dueAt}') END,
    trading.gas_owned_at(c.request#>>'{body,effectiveAt}'))
  OR(current_terms.account_id,current_terms.position_id,acknowledged.account_id,acknowledged.position_id) IS DISTINCT FROM
   (h.account_id,loan.return_position_id,h.account_id,loan.return_position_id)
 THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 SELECT count(*) INTO counted FROM trading.gas_loan_fee_cash_members x WHERE(x.catalog_id,x.operation_id)=(h.catalog_id,h.operation_id);
 IF h.changed THEN
  IF h.current_revision<2 OR h.acknowledged_revision<>h.current_revision
   OR(current_terms.publication_order,current_terms.actor_id,current_terms.implementation) IS DISTINCT FROM(h.publication_order,h.actor_id,'gross_timed_fixed_fee_adjustments_v1')
   OR NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(h.catalog_id,h.publication_order,pg_current_xact_id()))
  THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
  SELECT * INTO STRICT previous FROM trading.gas_loan_fee_terms x WHERE(x.catalog_id,x.loan_id)=(h.catalog_id,h.loan_id)
   AND x.publication_order<h.publication_order ORDER BY revision DESC LIMIT 1;
  IF previous.revision+1<>current_terms.revision OR current_terms.previous_revision<>previous.revision
   OR previous.revision<>(c.request#>>'{body,expectedRevision}')::integer
   OR(current_terms.origin_request_id,current_terms.original_fee_id,current_terms.account_id,current_terms.position_id) IS DISTINCT FROM
    (previous.origin_request_id,previous.original_fee_id,previous.account_id,previous.position_id)
   OR current_terms.effective_at<previous.effective_at OR current_terms.effective_at>h.as_of
   OR trading.gas_owned_visible(h.catalog_id,previous.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
  expected_count=(CASE WHEN previous.amount=0 THEN 0 ELSE 1 END)+(CASE WHEN current_terms.amount=0 THEN 0 ELSE 1 END);
  -- This publication contains only this typed fee change and its proved cash.
  -- Explicitly exclude every current gas financial, availability and input
  -- family, including the shared futures/cash parents used by gas holdings.
  -- A new qualified financial family must extend this same closed boundary.
  IF counted<>expected_count OR counted<>(SELECT count(*) FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_instruments p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_trade_revisions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_quantity_series p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_quantity_revisions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_cash_determinations p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_service_accounts p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_service_versions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_nomination_requests p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_balance_statements p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_imbalance_agreements p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_operation_responses p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_nomination_applications p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_imbalance_applications p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_cash_payments p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_accounts p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_reservations p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_releases p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_consumptions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_fee_payments p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_action_bindings p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_mark_bindings p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_projection_profiles p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_terms p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_principal_runs p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_cash p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_payments p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantities p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batches p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_quantity_corrections p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_quantity_bundles p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_loan_quantities p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_loan_quantity_corrections p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.futures_margin_policies p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.futures_margin_policy_occurrences p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.futures_delivery_profiles p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.futures_margin_runs p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.futures_margin_determinations p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM settlement_data.cash_payment_revisions p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
  THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
  FOR cash IN SELECT m.ordinal,m.kind,f.* FROM trading.gas_loan_fee_cash_members m JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(m.catalog_id,m.fee_id)
   WHERE(m.catalog_id,m.operation_id)=(h.catalog_id,h.operation_id) LOOP
   expected_amount=CASE cash.ordinal WHEN 1 THEN -previous.amount ELSE current_terms.amount END;
   expected_due=CASE cash.ordinal WHEN 1 THEN previous.due_at ELSE current_terms.due_at END;
   IF expected_amount=0 OR(cash.position_id,cash.loan_id,cash.amount,cash.due_at,cash.effective_at,cash.actor_id,cash.publication_order,
     cash.reference_catalog_id,cash.reference_revision_id,cash.currency_code) IS DISTINCT FROM
    (loan.return_position_id,loan.id,expected_amount,expected_due,current_terms.effective_at,h.actor_id,h.publication_order,
     owner.reference_catalog_id,owner.reference_revision_id,owner.currency_code)
   THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
  END LOOP;
  -- Every self-excluded cash row is now independently proved. All other
  -- account, bank, quantity and pending-source heads remain strict.
  PERFORM trading.require_gas_loan_fee_source(h.catalog_id,h.account_id,h.loan_id,h.source_context_id,h.as_of,
   (c.request->>'policyId')::uuid,(c.request->>'policyRevision')::integer,h.publication_order);
 ELSE
  IF counted<>0 OR current_terms.publication_order>h.read_publication_order THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 END IF;
 IF c.outcome IS DISTINCT FROM trading.gas_loan_fee_outcome(h.catalog_id,h.operation_id) THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 PERFORM security.require_gas_owned_commit(h.catalog_id,c.authority_until,false,false);RETURN NULL;
END$$;

CREATE FUNCTION trading.check_gas_loan_fee_term() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE loan trading.gas_owned_loans;stock trading.gas_owned_positions;return_position trading.gas_owned_positions;
 owner trading.gas_owned_accounts;c trading.gas_owned_commands;fee trading.gas_owned_fees;counted integer;BEGIN
 IF NEW.revision>1 THEN
  IF NOT EXISTS(SELECT 1 FROM trading.gas_loan_fee_changes h WHERE(h.catalog_id,h.loan_id,h.current_revision,h.account_id,h.publication_order,h.actor_id,h.changed)=
   (NEW.catalog_id,NEW.loan_id,NEW.revision,NEW.account_id,NEW.publication_order,NEW.actor_id,true)) THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
  RETURN NULL;
 END IF;
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=(NEW.catalog_id,NEW.loan_id,NEW.account_id);
 SELECT * INTO STRICT stock FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,loan.received_lot_id);
 SELECT * INTO STRICT return_position FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,loan.return_position_id);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(NEW.catalog_id,NEW.account_id);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(NEW.catalog_id,NEW.origin_request_id);
 PERFORM security.require_gas_owned_commit(NEW.catalog_id,c.authority_until,c.configuring,c.requires_jobs);
 IF c.request->>'action' IS DISTINCT FROM 'borrow' OR c.outcome->'changed' IS DISTINCT FROM 'true'::jsonb
  OR c.outcome->'semanticAcknowledgement' IS DISTINCT FROM 'false'::jsonb OR(c.outcome->>'id')::uuid<>loan.id
  OR(NEW.position_id,NEW.actor_id,NEW.publication_order,NEW.effective_at) IS DISTINCT FROM
   (loan.return_position_id,stock.actor_id,stock.publication_order,stock.effective_at)
  OR(c.account_id,c.actor_id,return_position.account_id,return_position.actor_id,return_position.publication_order,return_position.effective_at) IS DISTINCT FROM
   (loan.account_id,stock.actor_id,loan.account_id,stock.actor_id,stock.publication_order,stock.effective_at)
  OR(NEW.amount,NEW.due_at,NEW.effective_at) IS DISTINCT FROM
   (trading.gas_owned_quantity(c.request#>>'{body,feeAmount}'),CASE WHEN c.request#>>'{body,feeDueAt}' IS NULL THEN NULL ELSE trading.gas_owned_at(c.request#>>'{body,feeDueAt}') END,
    trading.gas_owned_at(c.request#>>'{body,titleAt}'))
 THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 SELECT count(*) INTO counted FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.loan_id,f.publication_order)=(NEW.catalog_id,loan.id,NEW.publication_order);
 IF counted<>(CASE WHEN NEW.amount=0 THEN 0 ELSE 1 END) OR(NEW.amount=0)<>(NEW.original_fee_id IS NULL) THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 IF NEW.amount<>0 THEN
  SELECT * INTO STRICT fee FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=(NEW.catalog_id,NEW.original_fee_id);
  IF(fee.loan_id,fee.position_id,fee.amount,fee.due_at,fee.effective_at,fee.actor_id,fee.publication_order,
    fee.reference_catalog_id,fee.reference_revision_id,fee.currency_code) IS DISTINCT FROM
   (loan.id,loan.return_position_id,NEW.amount,NEW.due_at,NEW.effective_at,NEW.actor_id,NEW.publication_order,
    owner.reference_catalog_id,owner.reference_revision_id,owner.currency_code)
  THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 END IF;
 PERFORM security.require_gas_owned_commit(NEW.catalog_id,c.authority_until,c.configuring,c.requires_jobs);RETURN NULL;
END$$;
CREATE FUNCTION trading.check_gas_loan_fee_cash() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.loan_id IS NOT NULL AND NOT(
  EXISTS(SELECT 1 FROM trading.gas_loan_fee_terms t WHERE(t.catalog_id,t.loan_id,t.original_fee_id,t.revision,t.publication_order)=(NEW.catalog_id,NEW.loan_id,NEW.id,1,NEW.publication_order))
  OR EXISTS(SELECT 1 FROM trading.gas_loan_fee_cash_members m JOIN trading.gas_loan_fee_changes h ON(h.catalog_id,h.operation_id)=(m.catalog_id,m.operation_id)
   WHERE(m.catalog_id,m.fee_id,h.loan_id,h.publication_order,h.changed)=(NEW.catalog_id,NEW.id,NEW.loan_id,NEW.publication_order,true)))
 THEN RAISE EXCEPTION 'gas_fee_terms_seal';END IF;
 RETURN NULL;
END$$;
DO $$DECLARE f regprocedure;name text;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.check_gas_loan_fee_change()'::regprocedure,'trading.check_gas_loan_fee_term()'::regprocedure,'trading.check_gas_loan_fee_cash()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
 FOREACH name IN ARRAY ARRAY['gas_loan_fee_changes','gas_loan_fee_cash_members','gas_owned_commands'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER sealed_loan_fee_change AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_loan_fee_change()',name);
 END LOOP;
END$$;
CREATE CONSTRAINT TRIGGER sealed_loan_fee_term AFTER INSERT ON trading.gas_loan_fee_terms DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_loan_fee_term();
CREATE CONSTRAINT TRIGGER sealed_loan_fee_cash AFTER INSERT ON trading.gas_owned_fees DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_loan_fee_cash();
COMMIT;
