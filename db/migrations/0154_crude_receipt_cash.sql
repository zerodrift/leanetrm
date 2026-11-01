-- Explicit accepted standard-volume receipts allocate, rather than replace, booked cash.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.crude_operational_events(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,version integer NOT NULL CHECK(version>0),
 kind text NOT NULL CHECK(kind IN('receipt','dispatch','invoice','payment')),business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),
 profile text NOT NULL CHECK(profile='physical_crude_receipt_cash_v1'),effective_at ref_data.instant NOT NULL,evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,version),UNIQUE(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision) REFERENCES trading.crude_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.crude_receipts(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),title_transferred boolean NOT NULL CHECK(title_transferred),
 disposition text NOT NULL CHECK(disposition IN('owned_inventory','onward_delivery')),sale_trade_id uuid,sale_revision integer,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),UNIQUE(catalog_id,previous_id),
 CHECK((disposition='onward_delivery')=(sale_trade_id IS NOT NULL)),CHECK((sale_trade_id IS NULL)=(sale_revision IS NULL)),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,previous_id,trade_id) REFERENCES trading.crude_receipts(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,sale_trade_id,sale_revision) REFERENCES trading.crude_trade_revisions(catalog_id,trade_id,revision)
);
CREATE TABLE trading.crude_dispatches(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,receipt_id uuid NOT NULL,quantity ref_data.exact_number NOT NULL CHECK(quantity>0),
 title_transferred boolean NOT NULL CHECK(title_transferred),PRIMARY KEY(catalog_id,id),
 FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,receipt_id,trade_id) REFERENCES trading.crude_receipts(catalog_id,id,trade_id)
);
CREATE TABLE trading.crude_invoices(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,receipt_id uuid NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL CHECK(slot IN(1,2)),
 ordinal integer NOT NULL CHECK(ordinal>0),previous_id uuid,quantity ref_data.exact_number NOT NULL CHECK(quantity>0),cumulative_quantity ref_data.exact_number NOT NULL CHECK(cumulative_quantity>0),
 exact_amount numeric NOT NULL CHECK(abs(exact_amount)<1e48 AND scale(exact_amount)<=36),amount ref_data.exact_number NOT NULL,direction integer NOT NULL CHECK(direction IN(-1,1)),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),
 rounding_policy text NOT NULL CHECK(rounding_policy='crude_cumulative_receipt_cash_v1'),due_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),UNIQUE(catalog_id,receipt_id,slot),UNIQUE(catalog_id,trade_id,slot,ordinal),UNIQUE(catalog_id,id,trade_id,slot),
 FOREIGN KEY(catalog_id,previous_id,trade_id,slot) REFERENCES trading.crude_invoices(catalog_id,id,trade_id,slot),
 FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,receipt_id,trade_id) REFERENCES trading.crude_receipts(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.crude_memberships(catalog_id,instrument_id,slot),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code)
);
CREATE TABLE trading.crude_payments(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,invoice_id uuid NOT NULL,amount ref_data.exact_number NOT NULL CHECK(amount>0),direction integer NOT NULL CHECK(direction IN(-1,1)),
 PRIMARY KEY(catalog_id,id),FOREIGN KEY(catalog_id,id,trade_id) REFERENCES trading.crude_operational_events(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,invoice_id,trade_id) REFERENCES trading.crude_invoices(catalog_id,id,trade_id)
);
CREATE TABLE trading.crude_lifecycle_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL CHECK(pg_column_size(request)<=16384),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id)
);
CREATE FUNCTION security.require_crude_lifecycle(catalog uuid,trade uuid,writing boolean) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR(writing AND security.trading_access(catalog,'AmendTrade') IS NOT TRUE) THEN RAISE EXCEPTION 'crude_lifecycle_denied' USING ERRCODE='42501';END IF;
 IF writing AND NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.crude_trade_revisions r JOIN trading.crude_instruments i USING(catalog_id,instrument_id)
 JOIN market_data.crude_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
 WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND(security.current_write_context() OR security.trading_visible(r.catalog_id,r.publication_order,r.effective_at))
 AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id)
 AND security.reference_access(a.reference_catalog_id,'ReadMarketData') AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order))) THEN RAISE EXCEPTION 'crude_lifecycle_trade';END IF;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_operational_events','crude_receipts','crude_dispatches','crude_invoices','crude_payments','crude_lifecycle_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_operational_events','crude_receipts','crude_dispatches','crude_invoices','crude_payments'] LOOP
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades''))',t);
  IF t='crude_operational_events' THEN
   EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  ELSE
   EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.crude_operational_events e WHERE(e.catalog_id,e.id)=(%I.catalog_id,%I.id)))',t,t,t);
  END IF;
 END LOOP;
 CREATE POLICY command_private ON trading.crude_lifecycle_commands AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);
END$$;
CREATE FUNCTION security.crude_lifecycle_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old trading.crude_lifecycle_commands;actor uuid;BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.trading_access(catalog,'AmendTrade') IS NOT TRUE THEN RAISE EXCEPTION 'crude_lifecycle_denied' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO old FROM trading.crude_lifecycle_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF old.actor_id IS DISTINCT FROM actor THEN
   IF payload IS NULL THEN RETURN jsonb_build_object('found',false);END IF;
   RAISE EXCEPTION 'crude_conflict';
  END IF;
  IF payload IS NOT NULL AND old.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'crude_conflict';END IF;
  PERFORM security.require_crude_lifecycle(catalog,old.trade_id,false);RETURN old.outcome;
 END IF;RETURN jsonb_build_object('found',false);
END$$;
CREATE FUNCTION security.read_crude_lifecycle(catalog uuid,trade uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;head jsonb;context uuid;BEGIN
 SELECT c.market_context_id INTO context FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN trading.contexts h ON(h.catalog_id,h.context_id)=(t.catalog_id,t.id)
 WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);
 PERFORM security.require_crude_lifecycle(catalog,trade,false);
 SELECT to_jsonb(r)||to_jsonb(i)||jsonb_build_object('request',t.request,'reference_catalog_id',a.reference_catalog_id,'reference_revision_id',a.reference_revision_id,
 'currency_code',q.currency_code,'quantity_code',q.quantity_code,'quote_multiplier',trim_scale(q.multiplier)::text,'decimal_places',c.display_minor_units,'quantity',trim_scale(r.quantity)::text,'fixed_price',trim_scale(i.fixed_price)::text,'handling_fee',trim_scale(i.handling_fee)::text,
 'quantity_unit',qs.unit_code,'quantity_basis',qs.basis_code,'quantity_dimension',qs.dimension_code,'commodity_code',qs.commodity_code,'grade_code',a.grade_code,'facility_code',a.facility_code,'custody_point_code',a.custody_point_code)
 INTO head FROM trading.crude_trade_revisions r JOIN trading.crude_instruments i USING(catalog_id,instrument_id) JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
 JOIN market_data.crude_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
 JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(a.catalog_id,a.revision_id,a.curve_code)
 JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,v.quote_code)
 JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.currency_code)
 JOIN ref_data.quantity_specifications qs ON(qs.catalog_id,qs.revision_id,qs.code)=(q.catalog_id,q.revision_id,q.quantity_code)
 WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at);
 result=jsonb_build_object('tradeId',trade,'head',head,'profile','physical_crude_receipt_cash_v1','roundingPolicy','crude_cumulative_receipt_cash_v1',
 'version',coalesce((SELECT max(version) FROM trading.crude_operational_events e WHERE(e.catalog_id,e.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),0),
 'canWrite',security.trading_access(catalog,'AmendTrade'));
 result=result||jsonb_build_object('receipts',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(r)||jsonb_build_object('quantity',trim_scale(r.quantity)::text) ORDER BY e.version)
 FROM trading.crude_receipts r JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id) WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb),
 'dispatches',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(d)||jsonb_build_object('quantity',trim_scale(d.quantity)::text) ORDER BY e.version)
 FROM trading.crude_dispatches d JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id) WHERE(d.catalog_id,d.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb),
 'invoices',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(i)||jsonb_build_object('quantity',trim_scale(i.quantity)::text,'cumulative_quantity',trim_scale(i.cumulative_quantity)::text,'exact_amount',trim_scale(i.exact_amount)::text,'amount',trim_scale(i.amount)::text) ORDER BY e.version)
 FROM trading.crude_invoices i JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id) WHERE(i.catalog_id,i.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb),
 'payments',coalesce((SELECT jsonb_agg(to_jsonb(e)||to_jsonb(p)||jsonb_build_object('amount',trim_scale(p.amount)::text) ORDER BY e.version)
 FROM trading.crude_payments p JOIN trading.crude_operational_events e USING(catalog_id,id,trade_id) WHERE(p.catalog_id,p.trade_id)=(catalog,trade) AND security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)),'[]'::jsonb));
 result=result||jsonb_build_object('sales',coalesce((SELECT jsonb_agg(to_jsonb(r)||jsonb_build_object('quantity',trim_scale(r.quantity)::text,'delivery_from',i.delivery_from,'delivery_to',i.delivery_to,'grade_code',a.grade_code,'facility_code',a.facility_code,'custody_point_code',a.custody_point_code) ORDER BY r.trade_id)
 FROM trading.crude_trade_revisions r JOIN trading.crude_instruments i USING(catalog_id,instrument_id)
 JOIN market_data.crude_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
 WHERE r.catalog_id=catalog AND r.direction=-1 AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
 AND(r.legal_entity_id,r.legal_entity_revision)=((head->>'legal_entity_id')::uuid,(head->>'legal_entity_revision')::integer)
 AND(a.reference_catalog_id,a.reference_revision_id,a.grade_code,a.facility_code,a.custody_point_code)=((head->>'reference_catalog_id')::uuid,(head->>'reference_revision_id')::uuid,head->>'grade_code',head->>'facility_code',head->>'custody_point_code')
 AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL)),'[]'::jsonb));
 PERFORM security.require_crude_lifecycle(catalog,trade,false);RETURN result;
END$$;
CREATE FUNCTION trading.crude_round_cash(value numeric,places integer) RETURNS numeric LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN abs(value*power(10::numeric,places)-trunc(value*power(10::numeric,places)))=0.5
 THEN (trunc(value*power(10::numeric,places))+CASE WHEN mod(abs(trunc(value*power(10::numeric,places))),2)=1 THEN sign(value) ELSE 0 END)/power(10::numeric,places)
 ELSE round(value,places) END
$$;
CREATE FUNCTION trading.crude_operational_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body-ARRAY['context','operationId','expectedVersion','expectedRevision','previewHash','commandKind'];key text;BEGIN
 result=result||jsonb_build_object('effectiveAt',extract(epoch FROM (body->>'effectiveAt')::timestamptz),'businessKey',btrim(body->>'businessKey'),'evidence',btrim(body->>'evidence'));
 FOREACH key IN ARRAY ARRAY['tradeId','receiptId','invoiceId','saleTradeId'] LOOP
  IF body?key THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 IF body?'quantity' THEN result=result||jsonb_build_object('quantity',(body->>'quantity')::numeric);END IF;
 IF body?'amount' THEN result=result||jsonb_build_object('amount',(body->>'amount')::numeric);END IF;
 RETURN result;
END$$;
CREATE FUNCTION security.record_crude_lifecycle(catalog uuid,command_kind text,body jsonb,as_of timestamptz,context uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid=(body->>'tradeId')::uuid;operation uuid=(body->>'operationId')::uuid;at_time timestamptz=(body->>'effectiveAt')::timestamptz;
 h trading.crude_trade_revisions;i trading.crude_instruments;a market_data.crude_valuation_assignments;s trading.crude_trade_revisions;si trading.crude_instruments;sa market_data.crude_valuation_assignments;
 prior trading.crude_operational_events;r trading.crude_receipts;inv trading.crude_invoices;last_invoice trading.crude_invoices;
 actor uuid;publication integer;identity uuid=gen_random_uuid();version integer;old jsonb;outcome jsonb;fact jsonb;oldfact jsonb;
 amount numeric;qty numeric=(body->>'quantity')::numeric;used numeric;price numeric;direction integer;component_slot integer;places integer;currency text;refcatalog uuid;refrevision uuid;exact_amount numeric;rounded numeric;multiplier numeric;
BEGIN
 PERFORM security.require_crude_lifecycle(catalog,trade,true);PERFORM pg_advisory_xact_lock(730849102);PERFORM security.require_crude_lifecycle(catalog,trade,true);
 IF pg_column_size(body)>16384 OR jsonb_typeof(body)<>'object' OR jsonb_typeof(body->'context') IS DISTINCT FROM 'object'
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'context') k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId'))
 OR NOT(body?&ARRAY['context','tradeId','expectedRevision','expectedVersion','operationId','businessKey','profile','effectiveAt','evidence'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE NOT k=ANY(ARRAY['context','tradeId','expectedRevision','expectedVersion','operationId','businessKey','profile','effectiveAt','evidence']||CASE command_kind
 WHEN 'receipt' THEN ARRAY['quantity','titleTransferred','disposition','saleTradeId','saleRevision'] WHEN 'dispatch' THEN ARRAY['receiptId','quantity','titleTransferred']
 WHEN 'invoice' THEN ARRAY['receiptId','component','decimalPlaces','roundingPolicy','previewHash'] WHEN 'payment' THEN ARRAY['invoiceId','amount','direction'] ELSE ARRAY[]::text[] END))
 OR NOT(body?&CASE command_kind WHEN 'receipt' THEN ARRAY['quantity','titleTransferred','disposition','saleTradeId','saleRevision'] WHEN 'dispatch' THEN ARRAY['receiptId','quantity','titleTransferred'] WHEN 'invoice' THEN ARRAY['receiptId','component','decimalPlaces','roundingPolicy','previewHash'] WHEN 'payment' THEN ARRAY['invoiceId','amount','direction'] ELSE ARRAY[]::text[] END)
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR jsonb_typeof(body->'expectedVersion') IS DISTINCT FROM 'number'
 OR(command_kind IN('receipt','dispatch') AND jsonb_typeof(body->'titleTransferred') IS DISTINCT FROM 'boolean')
 OR(command_kind='receipt' AND jsonb_typeof(body->'saleRevision') NOT IN('null','number'))
 OR(command_kind='invoice' AND jsonb_typeof(body->'decimalPlaces') IS DISTINCT FROM 'number')
 THEN RAISE EXCEPTION 'crude_lifecycle_input';END IF;
 old=security.crude_lifecycle_status(catalog,operation,body||jsonb_build_object('commandKind',command_kind));IF old?'eventId' THEN RETURN old;END IF;
 IF command_kind NOT IN('receipt','dispatch','invoice','payment') OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid
 OR body->>'profile' IS DISTINCT FROM 'physical_crude_receipt_cash_v1' OR body#>>'{context,convention}' IS DISTINCT FROM 'physical_crude_fixed_volume_v1'
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
 IF h.direction<>1 OR i.consideration_direction<>-1 OR i.fixed_price<=0 OR i.handling_fee<0 THEN RAISE EXCEPTION 'crude_lifecycle_profile';END IF;
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
  ELSIF command_kind<>'receipt' THEN RAISE EXCEPTION 'crude_correction_requires_reversal';
  ELSIF EXISTS(SELECT 1 FROM trading.crude_dispatches d WHERE(d.catalog_id,d.receipt_id)=(catalog,prior.id)) OR EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE(x.catalog_id,x.receipt_id)=(catalog,prior.id)) THEN RAISE EXCEPTION 'crude_correction_requires_reversal';END IF;
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
   SELECT coalesce(sum(x.quantity),0) INTO used FROM trading.crude_receipts x WHERE(x.catalog_id,x.sale_trade_id,x.sale_revision)=(catalog,s.trade_id,s.revision) AND x.id IS DISTINCT FROM prior.id
   AND NOT EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(x.catalog_id,x.id));
   IF used+qty>s.quantity THEN RAISE EXCEPTION 'crude_onward_capacity';END IF;
  ELSIF body->>'saleTradeId' IS NOT NULL OR body->>'saleRevision' IS NOT NULL THEN RAISE EXCEPTION 'crude_receipt_input';END IF;
 ELSIF command_kind IN('dispatch','invoice') THEN
  SELECT * INTO r FROM trading.crude_receipts WHERE(catalog_id,id,trade_id)=(catalog,(body->>'receiptId')::uuid,trade);
  IF r.id IS NULL OR EXISTS(SELECT 1 FROM trading.crude_receipts y WHERE(y.catalog_id,y.previous_id)=(catalog,r.id)) THEN RAISE EXCEPTION 'crude_receipt_revision';END IF;
  IF command_kind='dispatch' THEN
   IF r.disposition<>'onward_delivery' OR(body->>'titleTransferred')::boolean IS DISTINCT FROM true OR qty IS NULL OR qty<=0 THEN RAISE EXCEPTION 'crude_dispatch_input';END IF;
   PERFORM security.require_crude_lifecycle(catalog,r.sale_trade_id,false);
   SELECT * INTO s FROM trading.crude_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,r.sale_trade_id,r.sale_revision);
   SELECT * INTO si FROM trading.crude_instruments WHERE(catalog_id,instrument_id)=(catalog,s.instrument_id);
   IF at_time<si.delivery_from OR at_time>=si.delivery_to THEN RAISE EXCEPTION 'crude_dispatch_window';END IF;
   SELECT coalesce(sum(d.quantity),0) INTO used FROM trading.crude_dispatches d WHERE(d.catalog_id,d.receipt_id)=(catalog,r.id);
   IF used+qty>r.quantity THEN RAISE EXCEPTION 'crude_dispatch_capacity';END IF;
  ELSE
   component_slot=CASE body->>'component' WHEN 'consideration' THEN 1 WHEN 'handling_fee' THEN 2 END;
   price=CASE component_slot WHEN 1 THEN i.fixed_price WHEN 2 THEN i.handling_fee END;direction=CASE component_slot WHEN 1 THEN i.consideration_direction WHEN 2 THEN i.handling_fee_direction END;
   IF component_slot IS NULL OR price IS NULL OR body->>'roundingPolicy' IS DISTINCT FROM 'crude_cumulative_receipt_cash_v1' THEN RAISE EXCEPTION 'crude_invoice_input';END IF;
   SELECT q.currency_code,c.display_minor_units,q.multiplier INTO currency,places,multiplier FROM market_data.curves v
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,v.quote_code)
   JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.currency_code)
   WHERE(v.catalog_id,v.revision_id,v.code)=(catalog,i.definition_revision_id,a.curve_code);
   IF places IS NULL OR places IS DISTINCT FROM(body->>'decimalPlaces')::integer THEN RAISE EXCEPTION 'crude_invoice_rounding';END IF;
   SELECT * INTO last_invoice FROM trading.crude_invoices x WHERE(x.catalog_id,x.trade_id,x.slot)=(catalog,trade,component_slot) ORDER BY ordinal DESC LIMIT 1;
   used=coalesce(last_invoice.cumulative_quantity,0);exact_amount=r.quantity*price;
   rounded=trading.crude_round_cash((used+r.quantity)*price,places)-trading.crude_round_cash(used*price,places);
   IF EXISTS(SELECT 1 FROM trading.crude_invoices x WHERE(x.catalog_id,x.receipt_id,x.slot)=(catalog,r.id,component_slot)) THEN RAISE EXCEPTION 'crude_invoice_exists';END IF;
  END IF;
 ELSE
  SELECT * INTO inv FROM trading.crude_invoices WHERE(catalog_id,id,trade_id)=(catalog,(body->>'invoiceId')::uuid,trade);
  amount=(body->>'amount')::numeric;direction=CASE body->>'direction' WHEN 'pay' THEN -1 WHEN 'receive' THEN 1 END;
  IF inv.id IS NULL OR amount IS NULL OR amount<=0 OR scale(trim_scale(amount))>inv.decimal_places OR direction IS DISTINCT FROM inv.direction THEN RAISE EXCEPTION 'crude_payment_input';END IF;
  SELECT coalesce(sum(p.amount),0) INTO used FROM trading.crude_payments p WHERE(p.catalog_id,p.invoice_id)=(catalog,inv.id);
  IF used+amount>inv.amount THEN RAISE EXCEPTION 'crude_payment_capacity';END IF;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 INSERT INTO trading.crude_operational_events VALUES(catalog,identity,trade,h.revision,version+1,command_kind,btrim(body->>'businessKey'),'physical_crude_receipt_cash_v1',at_time,btrim(body->>'evidence'),context,actor,publication);
 IF command_kind='receipt' THEN
  INSERT INTO trading.crude_receipts VALUES(catalog,identity,trade,coalesce((SELECT revision FROM trading.crude_receipts WHERE(catalog_id,id)=(catalog,prior.id)),0)+1,prior.id,qty,true,body->>'disposition',(body->>'saleTradeId')::uuid,(body->>'saleRevision')::integer);
 ELSIF command_kind='dispatch' THEN INSERT INTO trading.crude_dispatches VALUES(catalog,identity,trade,r.id,qty,true);
 ELSIF command_kind='invoice' THEN INSERT INTO trading.crude_invoices VALUES(catalog,identity,trade,r.id,h.instrument_id,component_slot,coalesce(last_invoice.ordinal,0)+1,last_invoice.id,r.quantity,used+r.quantity,exact_amount,rounded,direction,a.reference_catalog_id,a.reference_revision_id,currency,places,'crude_cumulative_receipt_cash_v1',i.payment_at);
 ELSE INSERT INTO trading.crude_payments VALUES(catalog,identity,trade,inv.id,amount,direction);END IF;
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'eventId',identity,'version',version+1,'unchanged',false);
 END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 INSERT INTO trading.crude_lifecycle_commands VALUES(catalog,operation,trade,actor,body||jsonb_build_object('commandKind',command_kind),outcome);
 IF command_kind='receipt' AND body->>'saleTradeId' IS NOT NULL THEN PERFORM security.require_crude_lifecycle(catalog,(body->>'saleTradeId')::uuid,false);END IF;
 IF command_kind='dispatch' AND r.sale_trade_id IS NOT NULL THEN PERFORM security.require_crude_lifecycle(catalog,r.sale_trade_id,false);END IF;
 PERFORM security.require_crude_lifecycle(catalog,trade,true);RETURN outcome;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('require_crude_lifecycle','crude_lifecycle_status','read_crude_lifecycle','record_crude_lifecycle') LOOP
 EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END LOOP;
END$$;
COMMIT;
