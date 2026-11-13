-- Current consumers: currency cash-instrument PV and its recorded-cash P&L projection.
-- A due date never records payment. Paid cash is an immutable, knowledge-dated allocation to a held membership.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.cash_payment_memberships(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,instrument_id uuid NOT NULL,book_id uuid NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.cash_payment_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- Retain original membership and publication coordinates; this records no payment or opening balance.
INSERT INTO trading.cash_payment_memberships
 SELECT catalog_id,trade_id,premium_instrument_id,premium_instrument_id,book_id,publication_order,effective_at
 FROM trading.futures_option_trade_revisions WHERE revision=1;
ALTER TABLE trading.futures_option_trade_revisions ADD CONSTRAINT premium_cash_membership
 FOREIGN KEY(catalog_id,trade_id,premium_instrument_id) REFERENCES trading.cash_payment_memberships(catalog_id,trade_id,binding_id);

CREATE TABLE settlement_data.cash_payment_revisions(
 catalog_id uuid NOT NULL,allocation_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),trade_id uuid NOT NULL,binding_id uuid NOT NULL,
 paid_at ref_data.instant NOT NULL,amount ref_data.exact_number NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,allocation_id,revision),
 UNIQUE(catalog_id,allocation_id,paid_at,amount),
 FOREIGN KEY(catalog_id,trade_id,binding_id) REFERENCES trading.cash_payment_memberships(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE INDEX cash_payment_binding_history ON settlement_data.cash_payment_revisions(catalog_id,trade_id,binding_id,allocation_id,revision);
CREATE TABLE settlement_data.cash_payment_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL,
 allocation_id uuid NOT NULL,revision integer NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,allocation_id,revision) REFERENCES settlement_data.cash_payment_revisions(catalog_id,allocation_id,revision)
);
DO $$DECLARE schema_name text;table_name text;BEGIN
 FOR schema_name,table_name IN SELECT * FROM(VALUES('trading','cash_payment_memberships'),('settlement_data','cash_payment_revisions'),('settlement_data','cash_payment_commands'))x LOOP
  EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',schema_name,table_name);
  EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',schema_name,table_name);
  EXECUTE format('CREATE POLICY owner_access ON %I.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',schema_name,table_name);
  EXECUTE format('GRANT SELECT,INSERT ON %I.%I TO openetrm_security_owner',schema_name,table_name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON %I.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',schema_name,table_name);
 END LOOP;
END$$;
CREATE POLICY scoped_read ON trading.cash_payment_memberships FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(
 security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND
 security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(
 SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(cash_payment_memberships.catalog_id,cash_payment_memberships.instrument_id)));
CREATE POLICY scoped_read ON settlement_data.cash_payment_revisions FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(
 security.trading_visible(catalog_id,publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') AND EXISTS(
 SELECT 1 FROM trading.cash_payment_memberships m WHERE(m.catalog_id,m.trade_id,m.binding_id)=(cash_payment_revisions.catalog_id,cash_payment_revisions.trade_id,cash_payment_revisions.binding_id)));
CREATE POLICY worker_service ON trading.cash_payment_memberships AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE POLICY worker_service ON settlement_data.cash_payment_revisions AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
GRANT SELECT ON trading.cash_payment_memberships,settlement_data.cash_payment_revisions TO openetrm_runtime,openetrm_risk_worker;

-- Shared native booking helper. Storage-family admission owns the membership's economic terms and quantity.
CREATE FUNCTION security.register_cash_payment_membership(catalog uuid,trade uuid,binding uuid,instrument uuid,publication integer,effective timestamptz)
 RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE owner_book uuid;BEGIN
 SELECT book_id INTO STRICT owner_book FROM trading.trades WHERE(catalog_id,id)=(catalog,trade);
 INSERT INTO trading.cash_payment_memberships VALUES(catalog,trade,binding,instrument,owner_book,publication,effective);
END$$;
ALTER FUNCTION security.register_cash_payment_membership(uuid,uuid,uuid,uuid,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.register_cash_payment_membership(uuid,uuid,uuid,uuid,integer,timestamptz) FROM PUBLIC;
DO $patch$ DECLARE definition text;marker text=' INSERT INTO trading.futures_option_trade_revisions VALUES(catalog,trade,1,book,';BEGIN
 definition=pg_get_functiondef('security.book_futures_option(uuid,uuid,uuid,uuid,text,numeric,numeric,date,text,timestamp with time zone,timestamp with time zone,text,uuid,integer,text,jsonb)'::regprocedure);
 IF length(definition)-length(replace(definition,marker,''))<>length(marker) THEN RAISE EXCEPTION 'cash_membership_upgrade: booking contract changed';END IF;
 definition=replace(definition,marker,' PERFORM security.register_cash_payment_membership(catalog,trade,premium_instrument,premium_instrument,ordinal,executed);'||chr(10)||marker);
 EXECUTE definition;
END $patch$;

CREATE FUNCTION security.require_cash_payment_read(catalog uuid,context uuid,valuation timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.trading_context_visible(catalog,context) IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
  AND(q.market_catalog_id,q.market_context_id)=(catalog,context) AND q.trading_as_of=valuation AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'cash_payment_context' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.require_cash_payment_read(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_cash_payment_read(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.require_cash_payment_read(uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.require_cash_payment_write(catalog uuid,trade uuid,binding uuid,as_of timestamptz) RETURNS trading.cash_payment_memberships
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE context security.request_contexts;membership trading.cash_payment_memberships;BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO context FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF context.id IS NULL OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'cash_payment_denied' USING ERRCODE='42501';END IF;
 IF context.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 SELECT m.* INTO membership FROM trading.cash_payment_memberships m JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(m.catalog_id,m.instrument_id)
 WHERE(m.catalog_id,m.trade_id,m.binding_id)=(catalog,trade,binding) AND m.effective_at<=as_of
 AND security.reference_access(i.reference_catalog_id,'ReadMarketData') IS TRUE AND security.reference_version_visible(i.reference_catalog_id,i.reference_revision_id) IS TRUE;
 IF membership.trade_id IS NULL THEN RAISE EXCEPTION 'cash_payment_denied' USING ERRCODE='42501';END IF;
 RETURN membership;
END$$;
ALTER FUNCTION security.require_cash_payment_write(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_cash_payment_write(uuid,uuid,uuid,timestamptz) FROM PUBLIC;

CREATE FUNCTION security.record_cash_payment(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;allocation uuid;trade uuid;binding uuid;expected integer;paid timestamptz;amount_value numeric;
 previous settlement_data.cash_payment_revisions;fact settlement_data.cash_payment_revisions;command settlement_data.cash_payment_commands;
 publication integer;outcome jsonb;canonical jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy')) OR
  EXISTS(SELECT 1 FROM unnest(ARRAY['operationId','allocationId','tradeId','bindingId','paidAt','amount','policy']) k WHERE jsonb_typeof(body->k) IS DISTINCT FROM 'string') OR
  jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$' OR
  body->>'policy' IS DISTINCT FROM 'explicit_paid_cash_nominal_due_v1' OR
  body->>'paidAt'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$' OR
  body->>'amount'!~'^-?[0-9]{1,20}([.][0-9]{1,18})?$' OR as_of IS NULL OR NOT isfinite(as_of)
 THEN RAISE EXCEPTION 'cash_payment_input';END IF;
 operation=(body->>'operationId')::uuid;allocation=(body->>'allocationId')::uuid;trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;
 expected=(body->>'expectedRevision')::integer;paid=(body->>'paidAt')::timestamptz;amount_value=(body->>'amount')::numeric;
 IF operation='00000000-0000-0000-0000-000000000000'::uuid OR allocation='00000000-0000-0000-0000-000000000000'::uuid OR NOT isfinite(paid) OR paid>as_of
 THEN RAISE EXCEPTION 'cash_payment_input';END IF;
 canonical=jsonb_build_object('operationId',operation,'allocationId',allocation,'tradeId',trade,'bindingId',binding,'expectedRevision',expected,
  'paidAtUtc',paid AT TIME ZONE 'UTC','amount',amount_value,'policy',body->>'policy');
 PERFORM security.require_cash_payment_write(catalog,trade,binding,as_of);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_cash_payment_write(catalog,trade,binding,as_of);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO command FROM settlement_data.cash_payment_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF command.actor_id<>actor OR command.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'cash_payment_conflict';END IF;
  outcome=jsonb_build_object('operationId',operation,'allocationId',command.allocation_id,'revision',command.revision,'tradeId',trade,'bindingId',binding);
 ELSE
  SELECT * INTO previous FROM settlement_data.cash_payment_revisions WHERE(catalog_id,allocation_id)=(catalog,allocation) ORDER BY revision DESC LIMIT 1;
  IF previous.allocation_id IS NOT NULL AND(previous.trade_id,previous.binding_id) IS DISTINCT FROM(trade,binding) THEN RAISE EXCEPTION 'cash_payment_conflict';END IF;
  SELECT * INTO fact FROM settlement_data.cash_payment_revisions WHERE(catalog_id,allocation_id,paid_at,amount)=(catalog,allocation,paid,amount_value);
  IF fact.allocation_id IS NOT NULL THEN
   INSERT INTO settlement_data.cash_payment_commands VALUES(catalog,operation,actor,canonical,allocation,fact.revision);
   outcome=jsonb_build_object('operationId',operation,'allocationId',allocation,'revision',fact.revision,'tradeId',trade,'bindingId',binding);
  ELSE
   IF expected<>coalesce(previous.revision,0) THEN RAISE EXCEPTION 'cash_payment_revision';END IF;
   SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   INSERT INTO settlement_data.cash_payment_revisions VALUES(catalog,allocation,expected+1,trade,binding,paid,amount_value,actor,publication);
   INSERT INTO settlement_data.cash_payment_commands VALUES(catalog,operation,actor,canonical,allocation,expected+1);
   outcome=jsonb_build_object('operationId',operation,'allocationId',allocation,'revision',expected+1,'tradeId',trade,'bindingId',binding);
  END IF;
 END IF;
 -- Every outcome, including exact replay and fact no-op, is reauthorized after all waits and writes.
 PERFORM security.require_cash_payment_write(catalog,trade,binding,as_of);
 RETURN outcome;
END$$;
ALTER FUNCTION security.record_cash_payment(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_cash_payment(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.record_cash_payment(uuid,jsonb,timestamptz) TO openetrm_runtime;
-- Complete known currency-cash history is independent of endpoint holdings.
-- This explicit report operation retains future-effective allocations and retired
-- owners; it never presents those owners as already-held positions.
CREATE FUNCTION security.require_cash_ledger_scope(catalog uuid,context uuid,valuation timestamptz,books uuid[],trades uuid[])
 RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_cash_payment_read(catalog,context,valuation);
 IF security.has_permission('RunScenario') IS NOT TRUE OR books IS NULL OR cardinality(books)=0 OR
  cardinality(books)<>(SELECT count(DISTINCT b) FROM unnest(books)b) OR
  EXISTS(SELECT 1 FROM unnest(books)b WHERE b IS NULL OR NOT EXISTS(SELECT 1 FROM trading.books k
   WHERE(k.catalog_id,k.id)=(catalog,b) AND security.trading_visible(k.catalog_id,k.publication_order,k.effective_at)))
 THEN RAISE EXCEPTION 'cash_ledger_denied' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM trading.cash_payment_memberships m
  JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(m.catalog_id,m.instrument_id)
  JOIN settlement_data.cash_payment_revisions p ON(p.catalog_id,p.trade_id,p.binding_id)=(m.catalog_id,m.trade_id,m.binding_id)
  WHERE m.catalog_id=catalog AND m.book_id=ANY(books) AND(trades IS NULL OR m.trade_id=ANY(trades))
   AND security.trading_visible(catalog,m.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
   AND security.trading_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
   AND(security.reference_access(i.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR
       security.reference_version_visible(i.reference_catalog_id,i.reference_revision_id) IS NOT TRUE))
 THEN RAISE EXCEPTION 'cash_ledger_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.require_cash_ledger_scope(uuid,uuid,timestamptz,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_cash_ledger_scope(uuid,uuid,timestamptz,uuid[],uuid[]) FROM PUBLIC;
CREATE FUNCTION security.read_cash_payment_ledger(catalog uuid,context uuid,valuation timestamptz,books uuid[],trades uuid[])
 RETURNS SETOF jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_cash_ledger_scope(catalog,context,valuation,books,trades);
 RETURN QUERY SELECT jsonb_build_object('trade_id',m.trade_id,'binding_id',m.binding_id,'book_id',m.book_id,
  'reference_catalog_id',i.reference_catalog_id,'reference_revision_id',i.reference_revision_id,'currency_code',i.currency_code,
  'allocation_id',p.allocation_id,'revision',p.revision,'paid_at',p.paid_at,'known_at',k.known_from,'amount',trim_scale(p.amount)::text)
 FROM trading.cash_payment_memberships m JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(m.catalog_id,m.instrument_id)
 JOIN settlement_data.cash_payment_revisions p ON(p.catalog_id,p.trade_id,p.binding_id)=(m.catalog_id,m.trade_id,m.binding_id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE m.catalog_id=catalog AND m.book_id=ANY(books) AND(trades IS NULL OR m.trade_id=ANY(trades))
  AND security.trading_visible(catalog,m.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
  AND security.trading_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
 ORDER BY p.allocation_id,p.revision LIMIT 20001;
 PERFORM security.require_cash_ledger_scope(catalog,context,valuation,books,trades);
END$$;
ALTER FUNCTION security.read_cash_payment_ledger(uuid,uuid,timestamptz,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_cash_payment_ledger(uuid,uuid,timestamptz,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_cash_payment_ledger(uuid,uuid,timestamptz,uuid[],uuid[]) TO openetrm_runtime;

COMMIT;
