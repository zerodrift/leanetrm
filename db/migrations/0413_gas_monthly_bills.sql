-- Monthly gas bills and adjustment notes over one trade's daily cash determinations (D462).
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

-- A document covers determinations that already exist. Ordinal one is the bill; each later ordinal is an adjustment
-- note that names its predecessor. The two totals are sums of line amounts and are never netted.
CREATE TABLE trading.gas_bills(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,
 month date NOT NULL CHECK(isfinite(month) AND extract(day FROM month)=1),
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 1000),previous_id uuid,
 scope_policy text NOT NULL CHECK(scope_policy='gas_trade_month_gross_v1'),
 amount_policy text NOT NULL CHECK(amount_policy='sum_of_daily_determinations_v1'),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 pay_total ref_data.exact_number NOT NULL,receive_total ref_data.exact_number NOT NULL,
 -- The SHA-256 of the document's determination identities in line order: what a request must have reviewed.
 content_digest text NOT NULL CHECK(content_digest~'^[0-9a-f]{64}$'),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,month,ordinal),UNIQUE(catalog_id,id,trade_id,month),UNIQUE(catalog_id,previous_id),
 CHECK((ordinal=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,previous_id,trade_id,month) REFERENCES trading.gas_bills(catalog_id,id,trade_id,month),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- One line is one determination and carries its contribution. A determination is on at most one document.
CREATE TABLE trading.gas_bill_lines(
 catalog_id uuid NOT NULL,bill_id uuid NOT NULL,trade_id uuid NOT NULL,month date NOT NULL,
 determination_id uuid NOT NULL,cash_slot integer NOT NULL,day date NOT NULL,amount ref_data.exact_number NOT NULL,
 PRIMARY KEY(catalog_id,bill_id,determination_id),UNIQUE(catalog_id,determination_id),
 CHECK(day>=month AND day<(month+interval '1 month')::date),
 FOREIGN KEY(catalog_id,bill_id,trade_id,month) REFERENCES trading.gas_bills(catalog_id,id,trade_id,month),
 FOREIGN KEY(catalog_id,trade_id,cash_slot,day,determination_id) REFERENCES trading.gas_cash_determinations(catalog_id,trade_id,cash_slot,day,id)
);
CREATE TABLE trading.gas_bill_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,bill_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL CHECK(pg_column_size(request)<=16384),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,bill_id) REFERENCES trading.gas_bills(catalog_id,id)
);
-- Documents are read and written through the functions below; the runtime has no direct access.
DO $tables$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_bills','gas_bill_lines','gas_bill_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$tables$;

-- Whatever inserted it, a committed document has lines, follows its predecessor, and states the digest and the two
-- directional totals of exactly those lines.
CREATE FUNCTION trading.require_conserved_gas_bill() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ids uuid[];pay numeric;receive numeric;BEGIN
 SELECT array_agg(d.id ORDER BY d.cash_slot,d.day,d.ordinal),coalesce(sum(l.amount) FILTER(WHERE m.direction=-1),0),coalesce(sum(l.amount) FILTER(WHERE m.direction=1),0)
  INTO ids,pay,receive FROM trading.gas_bill_lines l
  JOIN trading.gas_cash_determinations d ON(d.catalog_id,d.id)=(l.catalog_id,l.determination_id) AND d.contribution=l.amount
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(d.catalog_id,d.instrument_id,d.cash_slot)
  WHERE(l.catalog_id,l.bill_id)=(NEW.catalog_id,NEW.id);
 IF ids IS NULL OR cardinality(ids)<>(SELECT count(*) FROM trading.gas_bill_lines l WHERE(l.catalog_id,l.bill_id)=(NEW.catalog_id,NEW.id))
  OR(NEW.pay_total,NEW.receive_total) IS DISTINCT FROM(pay,receive)
  OR NEW.content_digest IS DISTINCT FROM encode(sha256(convert_to(array_to_string(ids,','),'UTF8')),'hex')
  OR NEW.previous_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_bills b WHERE(b.catalog_id,b.id)=(NEW.catalog_id,NEW.previous_id) AND b.ordinal=NEW.ordinal-1)
  THEN RAISE EXCEPTION 'gas_bill_integrity';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION trading.require_conserved_gas_bill() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_conserved_gas_bill() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER conserved_gas_bill AFTER INSERT ON trading.gas_bills
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_conserved_gas_bill();

-- gas_trade_month_gross_v1: the payments of one retained instrument a bill covers. A billable payment is one final
-- billing can determine; every other payment is named with the reason it is not covered.
-- Late-bound, as the billing functions are: an installation may create the swing tables after this migration.
CREATE FUNCTION security.gas_bill_payments(catalog uuid,instrument uuid)
 RETURNS TABLE(slot integer,code text,role text,direction integer,reason text)
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN
 RETURN QUERY SELECT m.slot,m.code::text,m.role,m.direction,
  CASE WHEN m.role NOT IN('consideration','fee','indexed_consideration') THEN 'payment_kind'
   WHEN m.stage IS DISTINCT FROM 'delivery' THEN 'stage'
   WHEN EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e WHERE(e.catalog_id,e.instrument_id)=(m.catalog_id,m.instrument_id) AND e.slot IN(m.slot,m.source_slot))
    OR EXISTS(SELECT 1 FROM trading.gas_swing_members e WHERE(e.catalog_id,e.instrument_id,e.slot)=(m.catalog_id,m.instrument_id,m.slot))
    OR EXISTS(SELECT 1 FROM trading.gas_swing_rights e WHERE(e.catalog_id,e.instrument_id,e.physical_slot)=(m.catalog_id,m.instrument_id,m.source_slot)) THEN 'rights'
   WHEN m.role='indexed_consideration' AND NOT EXISTS(SELECT 1 FROM trading.gas_index_cash_terms t
    WHERE(t.catalog_id,t.instrument_id,t.slot)=(m.catalog_id,m.instrument_id,m.slot)
     AND t.observation_convention IN('named_index_daily_v1','named_index_monthly_v1','point_publications_v1')) THEN 'index_convention'
  END
 FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND m.role<>'physical';
END$$;
ALTER FUNCTION security.gas_bill_payments(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_bill_payments(uuid,uuid) FROM PUBLIC;

-- The retained gas days of each billable payment's physical source that lie inside the payment's own dates.
CREATE FUNCTION security.gas_bill_expected_days(catalog uuid,instrument uuid) RETURNS TABLE(slot integer,day date)
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT m.slot,g.operating_days[1]+date '0001-01-01'
 FROM security.gas_bill_payments(catalog,instrument) p
 JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(catalog,instrument,p.slot)
 JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.source_slot)
 JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id)=(s.catalog_id,s.schedule_id)
 WHERE p.reason IS NULL AND cardinality(g.operating_days)=1 AND cardinality(g.selection)=1
  AND g.operating_days[1]+date '0001-01-01'>=m.delivery_from AND g.operating_days[1]+date '0001-01-01'<m.delivery_to
$$;
ALTER FUNCTION security.gas_bill_expected_days(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_bill_expected_days(uuid,uuid) FROM PUBLIC;

-- One trade's bill scope, determinations and documents at the selected business time and knowledge cutoff. A
-- determination counts as documented only by a document visible in the same view.
CREATE FUNCTION security.read_gas_bill_inputs(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_trade_revisions;until_at timestamptz;payments jsonb;expected jsonb;determinations jsonb;documents jsonb;later jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of) ORDER BY r.revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RAISE EXCEPTION 'gas_bill_conflict';END IF;
 PERFORM security.read_instrument_side_schedule_headers(catalog,h.instrument_id,context,as_of);
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.slot),'[]'::jsonb) INTO payments FROM security.gas_bill_payments(catalog,h.instrument_id)p;
 SELECT coalesce(jsonb_agg(to_jsonb(e) ORDER BY e.slot,e.day),'[]'::jsonb) INTO expected FROM security.gas_bill_expected_days(catalog,h.instrument_id)e;
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',d.id,'cash_slot',d.cash_slot,'day',d.day,'ordinal',d.ordinal,'instrument_id',d.instrument_id,
   'contribution',trim_scale(d.contribution)::text,'rounded_total',trim_scale(d.rounded_total)::text,'due_at',d.due_at,'reason',d.reason,
   'reference_catalog_id',d.reference_catalog_id,'reference_revision_id',d.reference_revision_id,'currency_code',d.currency_code,
   'direction',(SELECT m.direction FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id,m.slot)=(d.catalog_id,d.instrument_id,d.cash_slot)),
   'documented',EXISTS(SELECT 1 FROM trading.gas_bill_lines l JOIN trading.gas_bills b ON(b.catalog_id,b.id)=(l.catalog_id,l.bill_id)
    WHERE(l.catalog_id,l.determination_id)=(d.catalog_id,d.id) AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of)))
   ORDER BY d.cash_slot,d.day,d.ordinal),'[]'::jsonb) INTO determinations
  FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade)
   AND security.ppa_visible_at(catalog,d.publication_order,d.effective_at,context,as_of);
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',b.id,'month',b.month,'ordinal',b.ordinal,'previous_id',b.previous_id,'trade_revision',b.trade_revision,
   'scope_policy',b.scope_policy,'amount_policy',b.amount_policy,'currency_code',b.currency_code,'content_digest',b.content_digest,
   'pay_total',trim_scale(b.pay_total)::text,'receive_total',trim_scale(b.receive_total)::text,'evidence',b.evidence,'effective_at',b.effective_at,
   'lines',(SELECT jsonb_agg(jsonb_build_object('determination_id',l.determination_id,'cash_slot',l.cash_slot,'day',l.day,'amount',trim_scale(l.amount)::text)
     ORDER BY l.cash_slot,l.day,d.ordinal) FROM trading.gas_bill_lines l JOIN trading.gas_cash_determinations d ON(d.catalog_id,d.id)=(l.catalog_id,l.determination_id)
     WHERE(l.catalog_id,l.bill_id)=(b.catalog_id,b.id))) ORDER BY b.month,b.ordinal),'[]'::jsonb) INTO documents
  FROM trading.gas_bills b WHERE(b.catalog_id,b.trade_id)=(catalog,trade)
   AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of);
 -- Months with a document this view's knowledge holds but its business time precedes. The writer sees such a document,
 -- so this view cannot issue for the month.
 SELECT coalesce(jsonb_agg(DISTINCT b.month),'[]'::jsonb) INTO later FROM trading.gas_bills b WHERE(b.catalog_id,b.trade_id)=(catalog,trade)
  AND b.effective_at>as_of AND security.ppa_visible_at(catalog,b.publication_order,as_of,context,as_of);
 PERFORM lifecycle.require_read(catalog,until_at);
 RETURN jsonb_build_object('header',jsonb_build_object('trade_id',h.trade_id,'revision',h.revision,'instrument_id',h.instrument_id),
  'payments',payments,'expected',expected,'determinations',determinations,'documents',documents,'later_months',later);
END$$;
ALTER FUNCTION security.read_gas_bill_inputs(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_bill_inputs(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_bill_inputs(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;

-- Ingress is shared by direct runtime calls and the managed adapter.
CREATE FUNCTION security.validate_gas_bill_request(body jsonb) RETURNS void
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';c jsonb=p->'context';key text;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['preview','operationId','previewHash','contentDigest','expectedPreviousId','evidence']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('preview','operationId','previewHash','contentDigest','expectedPreviousId','evidence')) OR
  jsonb_typeof(p) IS DISTINCT FROM 'object' OR NOT(p?&ARRAY['context','tradeId','expectedRevision','month','scopePolicy']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(p) k WHERE k NOT IN('context','tradeId','expectedRevision','month','scopePolicy')) OR
  jsonb_typeof(c) IS DISTINCT FROM 'object' OR c->>'knowledgeMode' IS DISTINCT FROM 'current' OR
  c->>'convention' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR NOT(c?&ARRAY['snapshotId','convention','knowledgeMode']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(c) k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId')) OR c->>'contextId' IS NOT NULL OR
  jsonb_typeof(p->'month') IS DISTINCT FROM 'string' OR p->>'month'!~'^[0-9]{4}-[0-9]{2}-01$' OR(p->>'month')::date::text IS DISTINCT FROM p->>'month' OR
  jsonb_typeof(p->'expectedRevision') IS DISTINCT FROM 'number' OR p->>'scopePolicy' IS DISTINCT FROM 'gas_trade_month_gross_v1' OR
  body->>'previewHash'!~'^[0-9a-f]{64}$' OR body->>'contentDigest'!~'^[0-9a-f]{64}$' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'gas_bill_input';END IF;
 FOREACH key IN ARRAY ARRAY['operationId','previewHash','contentDigest','evidence'] LOOP
  IF jsonb_typeof(body->key) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_bill_input';END IF;
 END LOOP;
 IF jsonb_typeof(body->'expectedPreviousId') NOT IN('string','null') OR jsonb_typeof(p->'tradeId') IS DISTINCT FROM 'string' OR
  body->>'operationId'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' OR p->>'tradeId'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' OR
  body->>'expectedPreviousId'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN RAISE EXCEPTION 'gas_bill_input';END IF;
END$$;
REVOKE ALL ON FUNCTION security.validate_gas_bill_request(jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.validate_gas_bill_request(jsonb) TO openetrm_security_owner;

CREATE FUNCTION security.gas_bill_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.gas_bill_commands;actor uuid;BEGIN
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_bill_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM trading.gas_bill_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_bill_denied' USING ERRCODE='42501';END IF;
 IF prior.operation_id IS NULL THEN RETURN NULL;END IF;
 IF prior.actor_id IS DISTINCT FROM actor OR payload IS NOT NULL AND prior.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'gas_bill_conflict';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,prior.trade_id,false);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_bill_denied' USING ERRCODE='42501';END IF;
 RETURN prior.outcome;
END$$;

-- A retried request returns its first outcome. A fresh request that reviewed the very lines of the document already
-- following its predecessor returns that document and receives its own receipt. A review of any other lines conflicts.
CREATE FUNCTION security.replay_gas_bill(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old jsonb;prior trading.gas_bill_commands;fact trading.gas_bills;actor uuid;p jsonb=payload->'preview';trade uuid;BEGIN
 PERFORM security.validate_gas_bill_request(payload);trade=(p->>'tradeId')::uuid;
 -- Issuing is a reviewed operation. A scheduled run's context cannot issue a document.
 IF EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_run_contexts b ON b.context_id=q.id
  WHERE q.runtime_login=session_user AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned())
  THEN RAISE EXCEPTION 'gas_bill_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 old=security.gas_bill_status(catalog,operation,payload);IF old IS NOT NULL THEN RETURN old;END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR operation IS DISTINCT FROM(payload->>'operationId')::uuid THEN RAISE EXCEPTION 'gas_bill_input';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.gas_bill_commands c WHERE c.catalog_id=catalog AND c.actor_id=actor AND c.request-'operationId'=payload-'operationId' LIMIT 1;
 IF FOUND THEN
  old=prior.outcome||jsonb_build_object('operationId',operation);
 ELSE
  SELECT * INTO fact FROM trading.gas_bills b WHERE(b.catalog_id,b.trade_id,b.month)=(catalog,trade,(p->>'month')::date)
   AND b.previous_id IS NOT DISTINCT FROM(payload->>'expectedPreviousId')::uuid;
  IF NOT FOUND THEN RETURN NULL;END IF;
  IF(fact.trade_revision,fact.scope_policy,fact.content_digest,fact.evidence) IS DISTINCT FROM
   ((p->>'expectedRevision')::integer,p->>'scopePolicy',payload->>'contentDigest',btrim(payload->>'evidence'))
   THEN RAISE EXCEPTION 'gas_bill_conflict';END IF;
  old=jsonb_build_object('operationId',operation,'tradeId',fact.trade_id,'billId',fact.id,'previousId',fact.previous_id,'ordinal',fact.ordinal,
   'payTotal',trim_scale(fact.pay_total)::text,'receiveTotal',trim_scale(fact.receive_total)::text);
  prior.trade_id=fact.trade_id;prior.bill_id=fact.id;
 END IF;
 IF(SELECT count(*) FROM trading.gas_bill_commands WHERE(catalog_id,trade_id)=(catalog,prior.trade_id))>=10000 THEN RAISE EXCEPTION 'gas_bill_limit';END IF;
 INSERT INTO trading.gas_bill_commands VALUES(catalog,operation,prior.trade_id,prior.bill_id,actor,payload,old);
 PERFORM security.require_gas_quantity_trade(catalog,prior.trade_id,true);RETURN old;
END$$;

-- The writer assembles the document itself: every determination of the trade and month the operation's context
-- knows and no document carries. It requires those lines and their two totals to be the reviewed ones.
CREATE FUNCTION security.issue_gas_bill(catalog uuid,body jsonb,as_of timestamptz,context uuid,calculation jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';trade uuid;operation uuid;bill_month date;head trading.gas_trade_revisions;prior trading.gas_bills;
 lines uuid[];digest text;pay numeric;receive numeric;currencies integer;currency record;actor uuid;publication integer;identity uuid=gen_random_uuid();old jsonb;result jsonb;BEGIN
 PERFORM security.validate_gas_bill_request(body);
 trade=(p->>'tradeId')::uuid;operation=(body->>'operationId')::uuid;bill_month=(p->>'month')::date;
 old=security.replay_gas_bill(catalog,operation,body);IF old IS NOT NULL THEN RETURN old;END IF;
 IF as_of IS NULL OR NOT isfinite(as_of) OR context IS NULL
  OR calculation->>'payTotal' IS NULL OR calculation->>'receiveTotal' IS NULL THEN RAISE EXCEPTION 'gas_bill_input';END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(p->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'gas_bill_conflict';END IF;
 SELECT * INTO prior FROM trading.gas_bills WHERE(catalog_id,trade_id,month)=(catalog,trade,bill_month) ORDER BY ordinal DESC LIMIT 1;
 IF prior.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR prior.effective_at>as_of THEN RAISE EXCEPTION 'gas_bill_conflict';END IF;
 IF NOT EXISTS(SELECT 1 FROM security.gas_bill_expected_days(catalog,head.instrument_id) e
  WHERE e.day>=bill_month AND e.day<(bill_month+interval '1 month')::date) THEN RAISE EXCEPTION 'gas_bill_scope';END IF;
 -- A document is issued only over a complete month: every expected payment day has a determination this context knows.
 IF EXISTS(SELECT 1 FROM security.gas_bill_expected_days(catalog,head.instrument_id) e
  WHERE e.day>=bill_month AND e.day<(bill_month+interval '1 month')::date AND NOT EXISTS(
   SELECT 1 FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id,d.cash_slot,d.day)=(catalog,trade,e.slot,e.day)
    AND security.ppa_visible_at(catalog,d.publication_order,d.effective_at,context,as_of))) THEN RAISE EXCEPTION 'gas_bill_incomplete';END IF;
 -- Billing a month that holds determinations of an earlier instrument is separate work.
 IF EXISTS(SELECT 1 FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade)
  AND d.day>=bill_month AND d.day<(bill_month+interval '1 month')::date AND d.instrument_id<>head.instrument_id) THEN RAISE EXCEPTION 'gas_bill_revision';END IF;
 SELECT array_agg(d.id ORDER BY d.cash_slot,d.day,d.ordinal),coalesce(sum(d.contribution) FILTER(WHERE m.direction=-1),0),
  coalesce(sum(d.contribution) FILTER(WHERE m.direction=1),0),count(DISTINCT(d.reference_catalog_id,d.reference_revision_id,d.currency_code))
  INTO lines,pay,receive,currencies
 FROM trading.gas_cash_determinations d JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(d.catalog_id,d.instrument_id,d.cash_slot)
 WHERE(d.catalog_id,d.trade_id)=(catalog,trade) AND d.day>=bill_month AND d.day<(bill_month+interval '1 month')::date
  AND security.ppa_visible_at(catalog,d.publication_order,d.effective_at,context,as_of)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_bill_lines l WHERE(l.catalog_id,l.determination_id)=(d.catalog_id,d.id));
 IF lines IS NULL THEN RAISE EXCEPTION 'gas_bill_empty';END IF;
 IF currencies<>1 THEN RAISE EXCEPTION 'gas_bill_input';END IF;
 digest=encode(sha256(convert_to(array_to_string(lines,','),'UTF8')),'hex');
 IF digest IS DISTINCT FROM body->>'contentDigest' THEN RAISE EXCEPTION 'gas_bill_conflict';END IF;
 IF pay IS DISTINCT FROM(calculation->>'payTotal')::numeric OR receive IS DISTINCT FROM(calculation->>'receiveTotal')::numeric THEN RAISE EXCEPTION 'gas_bill_amount';END IF;
 SELECT d.reference_catalog_id,d.reference_revision_id,d.currency_code INTO STRICT currency FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.id)=(catalog,lines[1]);
 IF prior.id IS NOT NULL AND(prior.reference_catalog_id,prior.reference_revision_id,prior.currency_code) IS DISTINCT FROM
  (currency.reference_catalog_id,currency.reference_revision_id,currency.currency_code) THEN RAISE EXCEPTION 'gas_bill_input';END IF;
 IF(SELECT count(*) FROM trading.gas_bill_commands WHERE(catalog_id,trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_bill_limit';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.gas_bills VALUES(catalog,identity,trade,head.revision,head.instrument_id,bill_month,coalesce(prior.ordinal,0)+1,prior.id,
  p->>'scopePolicy','sum_of_daily_determinations_v1',currency.reference_catalog_id,currency.reference_revision_id,currency.currency_code,
  pay,receive,digest,btrim(body->>'evidence'),actor,publication,as_of);
 INSERT INTO trading.gas_bill_lines SELECT catalog,identity,trade,bill_month,d.id,d.cash_slot,d.day,d.contribution
  FROM trading.gas_cash_determinations d WHERE d.catalog_id=catalog AND d.id=ANY(lines);
 result=jsonb_build_object('operationId',operation,'tradeId',trade,'billId',identity,'previousId',prior.id,'ordinal',coalesce(prior.ordinal,0)+1,
  'payTotal',trim_scale(pay)::text,'receiveTotal',trim_scale(receive)::text);
 INSERT INTO trading.gas_bill_commands VALUES(catalog,operation,trade,identity,actor,body,result);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);RETURN result;
END$$;
ALTER FUNCTION security.gas_bill_status(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.replay_gas_bill(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.issue_gas_bill(uuid,jsonb,timestamptz,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_bill_status(uuid,uuid,jsonb),security.replay_gas_bill(uuid,uuid,jsonb),security.issue_gas_bill(uuid,jsonb,timestamptz,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_bill_status(uuid,uuid,jsonb),security.replay_gas_bill(uuid,uuid,jsonb),security.issue_gas_bill(uuid,jsonb,timestamptz,uuid,jsonb) TO openetrm_runtime;
COMMIT;
