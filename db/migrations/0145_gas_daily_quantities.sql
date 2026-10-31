-- Trade-owned daily delivery evidence. Booked Side programs are never changed.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.gas_trade_revisions ADD UNIQUE(catalog_id,trade_id,revision,instrument_id);
CREATE TABLE trading.gas_delivery_obligations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,origin_revision integer NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,slot),
 FOREIGN KEY(catalog_id,trade_id,origin_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot)
);
CREATE TABLE trading.gas_quantity_series(
 catalog_id uuid NOT NULL,id uuid NOT NULL,obligation_id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('nominated','confirmed','scheduled','allocated')),
 source text NOT NULL CHECK(source='manual_daily_v1'),stage text NOT NULL CHECK(stage='delivery'),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,obligation_id,kind),
 FOREIGN KEY(catalog_id,obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id)
);
CREATE TABLE trading.gas_quantity_revisions(
 catalog_id uuid NOT NULL,id uuid NOT NULL,series_id uuid NOT NULL,day date NOT NULL,previous_id uuid,revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),
 status text NOT NULL CHECK(status IN('provisional','final','withdrawn')),quantity numeric CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,series_id,day,revision),UNIQUE(catalog_id,series_id,day,id),
 CHECK((status='withdrawn')=(quantity IS NULL)),CHECK((revision=1 AND previous_id IS NULL)OR(revision>1 AND previous_id IS NOT NULL)),
 FOREIGN KEY(catalog_id,series_id) REFERENCES trading.gas_quantity_series(catalog_id,id),
 FOREIGN KEY(catalog_id,series_id,day,previous_id) REFERENCES trading.gas_quantity_revisions(catalog_id,series_id,day,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_quantity_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL CHECK(pg_column_size(request)<=8192),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id)
);
CREATE FUNCTION trading.capture_gas_delivery_obligations() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 INSERT INTO trading.gas_delivery_obligations SELECT NEW.catalog_id,gen_random_uuid(),NEW.trade_id,NEW.revision,NEW.instrument_id,m.slot FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id,m.role)=(NEW.catalog_id,NEW.instrument_id,'physical');
 RETURN NEW;
END$$;
ALTER FUNCTION trading.capture_gas_delivery_obligations() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.capture_gas_delivery_obligations() FROM PUBLIC;
CREATE TRIGGER capture_gas_delivery_obligations AFTER INSERT ON trading.gas_trade_revisions FOR EACH ROW EXECUTE FUNCTION trading.capture_gas_delivery_obligations();
INSERT INTO trading.gas_delivery_obligations SELECT r.catalog_id,gen_random_uuid(),r.trade_id,r.revision,r.instrument_id,m.slot FROM trading.gas_trade_revisions r JOIN trading.gas_component_terms m USING(catalog_id,instrument_id) WHERE m.role='physical';
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_delivery_obligations','gas_quantity_series','gas_quantity_revisions','gas_quantity_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE POLICY scoped_read ON trading.gas_delivery_obligations FOR SELECT TO openetrm_runtime USING(
 EXISTS(SELECT 1 FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) WHERE(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(gas_delivery_obligations.catalog_id,gas_delivery_obligations.trade_id,gas_delivery_obligations.origin_revision,gas_delivery_obligations.instrument_id)));
CREATE POLICY scoped_read ON trading.gas_quantity_series FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(SELECT 1 FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(gas_quantity_series.catalog_id,gas_quantity_series.obligation_id)));
CREATE POLICY scoped_read ON trading.gas_quantity_revisions FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(SELECT 1 FROM trading.gas_quantity_series s WHERE(s.catalog_id,s.id)=(gas_quantity_revisions.catalog_id,gas_quantity_revisions.series_id)));
CREATE POLICY current_authority ON trading.gas_delivery_obligations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(
 security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND EXISTS(
 SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(gas_delivery_obligations.catalog_id,gas_delivery_obligations.trade_id,gas_delivery_obligations.origin_revision,gas_delivery_obligations.instrument_id)
 AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) AND security.gas_component_sources_visible(r.catalog_id,r.instrument_id)));
CREATE POLICY current_authority ON trading.gas_quantity_series AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(SELECT 1 FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(gas_quantity_series.catalog_id,gas_quantity_series.obligation_id)));
CREATE POLICY current_authority ON trading.gas_quantity_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(SELECT 1 FROM trading.gas_quantity_series s WHERE(s.catalog_id,s.id)=(gas_quantity_revisions.catalog_id,gas_quantity_revisions.series_id)));
GRANT SELECT ON trading.gas_delivery_obligations,trading.gas_quantity_series,trading.gas_quantity_revisions TO openetrm_runtime;
CREATE FUNCTION security.require_gas_quantity_trade(catalog uuid,trade uuid,writing boolean) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR(writing AND security.trading_access(catalog,'AmendTrade') IS NOT TRUE) THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
 IF writing AND NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id) WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND i.composition AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order))
 AND(security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) OR security.current_write_context())
 AND NOT EXISTS(SELECT 1 FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,r.instrument_id) AND NOT EXISTS(
  SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code)
  AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) AND security.gas_setup_visible(catalog,i.gas_setup_id)))) THEN RAISE EXCEPTION 'gas_quantity_trade';END IF;
END$$;
CREATE FUNCTION security.gas_quantity_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.gas_quantity_commands;actor uuid;BEGIN
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.trading_access(catalog,'AmendTrade') IS NOT TRUE THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM trading.gas_quantity_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR payload IS NOT NULL AND prior.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'gas_quantity_conflict';END IF;
  PERFORM security.require_gas_quantity_trade(catalog,prior.trade_id,false);RETURN prior.outcome;
 END IF;
 RETURN jsonb_build_object('found',false);
END$$;
CREATE FUNCTION security.record_gas_daily_quantity(catalog uuid,body jsonb,as_of timestamptz,context uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid=(body->>'tradeId')::uuid;operation uuid=(body->>'operationId')::uuid;obligation uuid=(body->>'obligationId')::uuid;selected_kind text=body->>'kind';status text=body->>'status';flow_day date=(body->>'day')::date;
 expected uuid=(body->>'expectedPreviousId')::uuid;amount numeric=(body->>'quantity')::numeric;o trading.gas_delivery_obligations;s trading.gas_quantity_series;prior trading.gas_quantity_revisions;head trading.gas_trade_revisions;actor uuid;publication integer;identity uuid;old jsonb;outcome jsonb;finish timestamptz;found_day boolean;
BEGIN
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);PERFORM pg_advisory_xact_lock(730849102);PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 old=security.gas_quantity_status(catalog,operation,body);IF old?'revisionId' THEN RETURN old;END IF;
 IF(SELECT count(*) FROM trading.gas_quantity_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_quantity_limit';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR as_of IS NULL OR NOT isfinite(as_of)
 OR jsonb_typeof(body)<>'object' OR NOT(body?&ARRAY['context','tradeId','expectedRevision','operationId','obligationId','kind','status','day','quantity','evidence','expectedPreviousId']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('context','tradeId','expectedRevision','operationId','obligationId','kind','status','day','quantity','evidence','expectedPreviousId'))
 OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date))
 OR body#>>'{context,convention}' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR body#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR body#>>'{context,contextId}' IS NOT NULL
 OR selected_kind IS NULL OR selected_kind NOT IN('nominated','confirmed','scheduled','allocated') OR status IS NULL OR status NOT IN('provisional','final','withdrawn')
 OR status='final' AND selected_kind<>'allocated' OR(status='withdrawn')<>(amount IS NULL) OR amount<0 OR amount>=1e24 OR scale(trim_scale(amount))>18 OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.effective_at>as_of OR head.revision IS DISTINCT FROM(body->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_quantity_conflict';END IF;
 SELECT * INTO o FROM trading.gas_delivery_obligations WHERE(catalog_id,id,trade_id,instrument_id)=(catalog,obligation,trade,head.instrument_id);
 IF o.id IS NULL THEN RAISE EXCEPTION 'gas_quantity_obligation';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,head.publication_order)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND head.publication_order<=x.known_through AND k.known_from<=t.known_at)
 OR EXISTS(SELECT 1 FROM trading.instrument_side_schedules m JOIN trading.side_schedule_publications p USING(catalog_id,schedule_id)
  WHERE(m.catalog_id,m.instrument_id)=(catalog,o.instrument_id) AND NOT EXISTS(
   SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,p.publication_order)
   WHERE(t.catalog_id,t.id)=(catalog,context) AND p.publication_order<=x.known_through AND k.known_from<=t.known_at)) THEN RAISE EXCEPTION 'gas_quantity_context';END IF;
 SELECT true,g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond' INTO found_day,finish
 FROM trading.instrument_side_schedules m JOIN trading.side_schedule_grids g USING(catalog_id,schedule_id)
 JOIN trading.side_schedule_publications p USING(catalog_id,schedule_id)
 WHERE(m.catalog_id,m.instrument_id,m.slot)=(catalog,o.instrument_id,o.slot) AND g.operating_days=ARRAY[flow_day-date '0001-01-01'] AND cardinality(g.selection)=1 AND p.effective_at<=as_of AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order));
 IF found_day IS DISTINCT FROM true OR status='final' AND finish>as_of THEN RAISE EXCEPTION 'gas_quantity_interval';END IF;
 SELECT * INTO s FROM trading.gas_quantity_series WHERE(catalog_id,obligation_id,gas_quantity_series.kind)=(catalog,obligation,selected_kind);
 IF s.id IS NULL THEN
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_quantity_series VALUES(catalog,gen_random_uuid(),obligation,selected_kind,'manual_daily_v1','delivery',publication,as_of) RETURNING * INTO s;
 END IF;
 IF s.effective_at>as_of THEN RAISE EXCEPTION 'gas_quantity_conflict';END IF;
 SELECT * INTO prior FROM trading.gas_quantity_revisions r WHERE(r.catalog_id,r.series_id,r.day)=(catalog,s.id,flow_day) ORDER BY revision DESC LIMIT 1;
 IF prior.effective_at>as_of THEN RAISE EXCEPTION 'gas_quantity_conflict';END IF;
 -- Equal business facts are no-ops even under a fresh operation identity.
 IF prior.id IS NOT NULL AND(prior.status,prior.quantity,prior.evidence) IS NOT DISTINCT FROM(status,amount,btrim(body->>'evidence')) THEN
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'obligationId',obligation,'revisionId',prior.id,'revision',prior.revision,'unchanged',true);
 ELSE
  IF prior.id IS DISTINCT FROM expected OR status='withdrawn' AND(prior.id IS NULL OR prior.status='withdrawn') OR prior.effective_at>as_of THEN RAISE EXCEPTION 'gas_quantity_conflict';END IF;
  IF(SELECT count(*) FROM trading.gas_quantity_revisions r JOIN trading.gas_quantity_series x ON(x.catalog_id,x.id)=(r.catalog_id,r.series_id) JOIN trading.gas_delivery_obligations y ON(y.catalog_id,y.id)=(x.catalog_id,x.obligation_id) WHERE(y.catalog_id,y.trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_quantity_limit';END IF;
  IF publication IS NULL THEN
   SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  END IF;identity=gen_random_uuid();
  SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  INSERT INTO trading.gas_quantity_revisions VALUES(catalog,identity,s.id,flow_day,prior.id,coalesce(prior.revision,0)+1,status,amount,btrim(body->>'evidence'),operation,actor,publication,as_of,context);
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'obligationId',obligation,'revisionId',identity,'revision',coalesce(prior.revision,0)+1,'unchanged',false);
 END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 INSERT INTO trading.gas_quantity_commands VALUES(catalog,operation,trade,actor,body,outcome);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);RETURN outcome;
END$$;
ALTER FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.gas_quantity_status(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.record_gas_daily_quantity(uuid,jsonb,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean),security.gas_quantity_status(uuid,uuid,jsonb),security.record_gas_daily_quantity(uuid,jsonb,timestamptz,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.require_gas_quantity_trade(uuid,uuid,boolean),security.gas_quantity_status(uuid,uuid,jsonb),security.record_gas_daily_quantity(uuid,jsonb,timestamptz,uuid) TO openetrm_runtime;
COMMIT;
