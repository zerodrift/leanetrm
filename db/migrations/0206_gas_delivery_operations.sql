-- Explicit manual operator-service sources, requests and offsetting agreements.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO security.permissions VALUES('ReadGasOperations'),('RecordGasOperations'),('ConfigureGasServices');
INSERT INTO security.roles VALUES(gen_random_uuid(),'gas_operator','Gas delivery operations'),(gen_random_uuid(),'gas_service_admin','Gas service setup');
INSERT INTO security.role_permissions SELECT r.id,p FROM security.roles r CROSS JOIN unnest(ARRAY['ReadMarketData','ReadTrades','AmendTrade','ReadGasOperations','RecordGasOperations'])p WHERE r.code='gas_operator';
INSERT INTO security.role_permissions SELECT r.id,p FROM security.roles r CROSS JOIN unnest(ARRAY['ReadMarketData','ReadTrades','ReadGasOperations','ConfigureGasServices'])p WHERE r.code='gas_service_admin';
-- No existing principal receives these new independent capabilities.
CREATE TABLE trading.gas_service_accounts(
 catalog_id uuid NOT NULL,id uuid NOT NULL,origin_obligation_id uuid NOT NULL,trade_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 quantity_code ref_data.code NOT NULL,quote_code ref_data.code NOT NULL,
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 holder_role text NOT NULL CHECK(holder_role IN('legal_entity','counterparty')),external_reference text NOT NULL CHECK(length(btrim(external_reference)) BETWEEN 1 AND 100),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,origin_obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code) REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code,product_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- Anchor, unit, zone/product and reference revision are witnesses, not fresh pools.
CREATE UNIQUE INDEX gas_service_pool ON trading.gas_service_accounts(catalog_id,reference_catalog_id,pipeline_code,holder_role,(CASE holder_role WHEN 'legal_entity' THEN legal_entity_id ELSE counterparty_id END),external_reference);
CREATE TABLE trading.gas_service_versions(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,
 valid_from date NOT NULL,valid_to date NOT NULL,active boolean NOT NULL,statement_policy text NOT NULL CHECK(statement_policy IN('final_statement_balance_v1','reported_statement_balance_v1')),
 terms_reference text NOT NULL CHECK(length(btrim(terms_reference)) BETWEEN 1 AND 300),evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,account_id,id),UNIQUE(catalog_id,account_id,revision),CHECK(valid_from<valid_to),CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_service_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,account_id,previous_id) REFERENCES trading.gas_service_versions(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE TABLE trading.gas_nomination_requests(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,service_version_id uuid NOT NULL,day date NOT NULL,
 external_reference text NOT NULL CHECK(length(btrim(external_reference)) BETWEEN 1 AND 100),revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,
 quantity numeric NOT NULL CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),submitted_at ref_data.instant NOT NULL,
 channel text NOT NULL CHECK(channel IN('portal','edi','email','phone','manual_import')),reason text NOT NULL CHECK(length(btrim(reason)) BETWEEN 1 AND 300),evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 nomination_id uuid NOT NULL,base_confirmed_id uuid,base_scheduled_id uuid,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,account_id,day,external_reference,id),UNIQUE(catalog_id,account_id,day,external_reference,revision),CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,account_id,service_version_id) REFERENCES trading.gas_service_versions(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,account_id,day,external_reference,previous_id) REFERENCES trading.gas_nomination_requests(catalog_id,account_id,day,external_reference,id),
 FOREIGN KEY(catalog_id,nomination_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),FOREIGN KEY(catalog_id,base_confirmed_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),FOREIGN KEY(catalog_id,base_scheduled_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE TABLE trading.gas_balance_statements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,service_version_id uuid NOT NULL,day date NOT NULL,revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,
 external_reference text NOT NULL CHECK(length(btrim(external_reference)) BETWEEN 1 AND 100),reported_at ref_data.instant NOT NULL,
 status text NOT NULL CHECK(status IN('provisional','final','withdrawn')),balance numeric CHECK(abs(balance)<1e24 AND scale(balance)<=18),included_movement_id uuid,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,account_id,day,id),UNIQUE(catalog_id,account_id,day,revision),CHECK((revision=1)=(previous_id IS NULL)),CHECK((status='withdrawn')=(balance IS NULL)),
 FOREIGN KEY(catalog_id,account_id,service_version_id) REFERENCES trading.gas_service_versions(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,account_id,day,previous_id) REFERENCES trading.gas_balance_statements(catalog_id,account_id,day,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE TABLE trading.gas_imbalance_agreements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,buyer_account_id uuid NOT NULL,buyer_version_id uuid NOT NULL,seller_account_id uuid NOT NULL,seller_version_id uuid NOT NULL,day date NOT NULL,
 external_reference text NOT NULL CHECK(length(btrim(external_reference)) BETWEEN 1 AND 100),quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),price numeric NOT NULL CHECK(abs(price)<1e12 AND scale(price)<=18),
 agreed_at ref_data.instant NOT NULL,due_at ref_data.instant NOT NULL,decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),profile text NOT NULL CHECK(profile='paired_offsetting_imbalance_v1'),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,buyer_account_id,seller_account_id,day,external_reference),CHECK(buyer_account_id<>seller_account_id),CHECK(due_at>=agreed_at),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,buyer_account_id,buyer_version_id) REFERENCES trading.gas_service_versions(catalog_id,account_id,id),FOREIGN KEY(catalog_id,seller_account_id,seller_version_id) REFERENCES trading.gas_service_versions(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
-- Two current consumers share the same external-response storage contract.
CREATE TABLE trading.gas_operation_responses(
 catalog_id uuid NOT NULL,id uuid NOT NULL,nomination_id uuid,imbalance_id uuid,side text NOT NULL CHECK(side IN('counterparty','pipeline','request')),
 decision text NOT NULL CHECK(decision IN('approved','rejected','withdrawn')),quantity numeric CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),
 external_reference text NOT NULL CHECK(length(btrim(external_reference)) BETWEEN 1 AND 100),responded_at ref_data.instant NOT NULL,evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),CHECK(num_nonnulls(nomination_id,imbalance_id)=1),CHECK((revision=1)=(previous_id IS NULL)),CHECK((decision='approved')=(quantity IS NOT NULL)),CHECK((side='request')=(decision='withdrawn')),
 UNIQUE(catalog_id,nomination_id,side,id),UNIQUE(catalog_id,nomination_id,side,revision),UNIQUE(catalog_id,imbalance_id,side,id),UNIQUE(catalog_id,imbalance_id,side,revision),
 FOREIGN KEY(catalog_id,nomination_id) REFERENCES trading.gas_nomination_requests(catalog_id,id),FOREIGN KEY(catalog_id,imbalance_id) REFERENCES trading.gas_imbalance_agreements(catalog_id,id),
 FOREIGN KEY(catalog_id,nomination_id,side,previous_id) REFERENCES trading.gas_operation_responses(catalog_id,nomination_id,side,id),FOREIGN KEY(catalog_id,imbalance_id,side,previous_id) REFERENCES trading.gas_operation_responses(catalog_id,imbalance_id,side,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE TABLE trading.gas_nomination_applications(
 catalog_id uuid NOT NULL,id uuid NOT NULL,nomination_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 1000),previous_id uuid,counterparty_response_id uuid NOT NULL,pipeline_response_id uuid NOT NULL,
 confirmed_id uuid NOT NULL,scheduled_id uuid NOT NULL,evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,nomination_id,id),UNIQUE(catalog_id,nomination_id,ordinal),UNIQUE(catalog_id,nomination_id,counterparty_response_id,pipeline_response_id),CHECK((ordinal=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,nomination_id) REFERENCES trading.gas_nomination_requests(catalog_id,id),FOREIGN KEY(catalog_id,nomination_id,previous_id) REFERENCES trading.gas_nomination_applications(catalog_id,nomination_id,id),
 FOREIGN KEY(catalog_id,counterparty_response_id) REFERENCES trading.gas_operation_responses(catalog_id,id),FOREIGN KEY(catalog_id,pipeline_response_id) REFERENCES trading.gas_operation_responses(catalog_id,id),
 FOREIGN KEY(catalog_id,confirmed_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),FOREIGN KEY(catalog_id,scheduled_id) REFERENCES trading.gas_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE TABLE trading.gas_imbalance_applications(
 catalog_id uuid NOT NULL,id uuid NOT NULL,agreement_id uuid NOT NULL,counterparty_response_id uuid NOT NULL,pipeline_response_id uuid NOT NULL,buyer_statement_id uuid NOT NULL,seller_statement_id uuid NOT NULL,
 exact_numerator numeric NOT NULL CHECK(scale(exact_numerator)=0 AND abs(exact_numerator)<1e120),exact_denominator numeric NOT NULL CHECK(scale(exact_denominator)=0 AND exact_denominator>0 AND exact_denominator<1e120),cash_amount ref_data.exact_number NOT NULL,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,agreement_id),
 FOREIGN KEY(catalog_id,agreement_id) REFERENCES trading.gas_imbalance_agreements(catalog_id,id),FOREIGN KEY(catalog_id,counterparty_response_id) REFERENCES trading.gas_operation_responses(catalog_id,id),FOREIGN KEY(catalog_id,pipeline_response_id) REFERENCES trading.gas_operation_responses(catalog_id,id),
 FOREIGN KEY(catalog_id,buyer_statement_id) REFERENCES trading.gas_balance_statements(catalog_id,id),FOREIGN KEY(catalog_id,seller_statement_id) REFERENCES trading.gas_balance_statements(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE TABLE trading.gas_account_movements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,day date NOT NULL,application_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 10000),previous_id uuid,
 quantity numeric NOT NULL CHECK(quantity<>0 AND abs(quantity)<1e24 AND scale(quantity)<=18),external_effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,account_id,day,id),UNIQUE(catalog_id,account_id,day,ordinal),UNIQUE(catalog_id,account_id,application_id),CHECK((ordinal=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_service_accounts(catalog_id,id),FOREIGN KEY(catalog_id,application_id) REFERENCES trading.gas_imbalance_applications(catalog_id,id),
 FOREIGN KEY(catalog_id,account_id,day,previous_id) REFERENCES trading.gas_account_movements(catalog_id,account_id,day,id)
);
ALTER TABLE trading.gas_balance_statements ADD FOREIGN KEY(catalog_id,account_id,day,included_movement_id) REFERENCES trading.gas_account_movements(catalog_id,account_id,day,id);
CREATE TABLE trading.gas_operation_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),kind text NOT NULL,
 service_version_id uuid,nomination_id uuid,response_id uuid,nomination_application_id uuid,statement_id uuid,imbalance_id uuid,imbalance_application_id uuid,
 request jsonb NOT NULL CHECK(pg_column_size(request)<=32768),outcome jsonb NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),CHECK(num_nonnulls(service_version_id,nomination_id,response_id,nomination_application_id,statement_id,imbalance_id,imbalance_application_id)=1),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),FOREIGN KEY(catalog_id,service_version_id) REFERENCES trading.gas_service_versions(catalog_id,id),FOREIGN KEY(catalog_id,nomination_id) REFERENCES trading.gas_nomination_requests(catalog_id,id),
 FOREIGN KEY(catalog_id,response_id) REFERENCES trading.gas_operation_responses(catalog_id,id),FOREIGN KEY(catalog_id,nomination_application_id) REFERENCES trading.gas_nomination_applications(catalog_id,id),FOREIGN KEY(catalog_id,statement_id) REFERENCES trading.gas_balance_statements(catalog_id,id),
 FOREIGN KEY(catalog_id,imbalance_id) REFERENCES trading.gas_imbalance_agreements(catalog_id,id),FOREIGN KEY(catalog_id,imbalance_application_id) REFERENCES trading.gas_imbalance_applications(catalog_id,id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_service_accounts','gas_service_versions','gas_nomination_requests','gas_balance_statements','gas_imbalance_agreements','gas_operation_responses','gas_nomination_applications','gas_imbalance_applications','gas_account_movements','gas_operation_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION security.require_gas_operations(catalog uuid,kind text) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF security.trading_access(catalog,'ReadGasOperations') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR
 (kind='service' AND security.trading_access(catalog,'ConfigureGasServices') IS NOT TRUE) OR
 (kind NOT IN('read','service') AND(security.trading_access(catalog,'RecordGasOperations') IS NOT TRUE OR security.trading_access(catalog,'AmendTrade') IS NOT TRUE)) THEN RAISE EXCEPTION 'gas_operations_denied' USING ERRCODE='42501';END IF;
 IF kind<>'read' AND NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
END$$;
CREATE FUNCTION security.gas_service_visible(catalog uuid,account uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.trading_access(catalog,'ReadGasOperations') AND security.trading_access(catalog,'ReadTrades') AND security.market_access(catalog,'ReadMarketData') AND EXISTS(
 SELECT 1 FROM trading.gas_service_accounts a JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(a.catalog_id,a.origin_obligation_id)
 JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(o.catalog_id,o.trade_id,o.origin_revision,o.instrument_id)
 WHERE(a.catalog_id,a.id)=(catalog,account) AND(security.current_write_context() OR security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)) AND (
  NOT security.current_write_context() AND security.gas_component_sources_visible(catalog,r.instrument_id) OR
  security.current_write_context() AND NOT EXISTS(SELECT 1 FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,r.instrument_id) AND NOT EXISTS(
   SELECT 1 FROM market_data.gas_valuation_assignments v JOIN trading.gas_instruments i ON(i.catalog_id,i.instrument_id)=(catalog,r.instrument_id)
   WHERE(v.catalog_id,v.revision_id,v.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code) AND security.market_fact_visible(v.catalog_id,v.revision_id,NULL) AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id) AND security.gas_setup_visible(catalog,i.gas_setup_id)))))
$$;
CREATE FUNCTION security.gas_imbalance_visible(catalog uuid,agreement uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_imbalance_agreements t WHERE(t.catalog_id,t.id)=(catalog,agreement) AND security.gas_service_visible(catalog,t.buyer_account_id) AND security.gas_service_visible(catalog,t.seller_account_id))
$$;
CREATE FUNCTION security.gas_nomination_visible(catalog uuid,nomination uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_nomination_requests n WHERE(n.catalog_id,n.id)=(catalog,nomination) AND security.gas_service_visible(catalog,n.account_id))
$$;
CREATE FUNCTION security.gas_movement_visible(catalog uuid,application uuid,account uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.gas_service_visible(catalog,account) AND EXISTS(SELECT 1 FROM trading.gas_imbalance_applications a JOIN trading.gas_account_movements m ON(m.catalog_id,m.application_id)=(a.catalog_id,a.id) WHERE(a.catalog_id,a.id,m.account_id)=(catalog,application,account) AND security.trading_visible(catalog,a.publication_order,a.effective_at))
$$;
DO $$DECLARE t text;condition text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_service_accounts','gas_service_versions','gas_nomination_requests','gas_balance_statements','gas_imbalance_agreements','gas_operation_responses','gas_nomination_applications','gas_imbalance_applications','gas_account_movements'] LOOP
  condition=CASE t WHEN 'gas_service_accounts' THEN 'security.gas_service_visible(catalog_id,id)' WHEN 'gas_imbalance_agreements' THEN 'security.gas_imbalance_visible(catalog_id,id)'
   WHEN 'gas_operation_responses' THEN '(nomination_id IS NOT NULL AND security.gas_nomination_visible(catalog_id,nomination_id) OR imbalance_id IS NOT NULL AND security.gas_imbalance_visible(catalog_id,imbalance_id))'
   WHEN 'gas_nomination_applications' THEN 'security.gas_nomination_visible(catalog_id,nomination_id)' WHEN 'gas_imbalance_applications' THEN 'security.gas_imbalance_visible(catalog_id,agreement_id)'
   ELSE 'security.gas_service_visible(catalog_id,account_id)' END;
  IF t<>'gas_account_movements' THEN condition=condition||' AND security.trading_visible(catalog_id,publication_order,effective_at)';
  ELSE condition=condition||' AND security.gas_movement_visible(catalog_id,application_id,account_id)';END IF;
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(%s)',t,condition);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
 END LOOP;
END$$;
CREATE FUNCTION security.gas_service_source(catalog uuid,obligation uuid,flow_day date,as_of timestamptz) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 PERFORM security.require_gas_operations(catalog,'read');
 SELECT to_jsonb(t)||jsonb_build_object('counterparty_id',r.counterparty_id,'counterparty_revision',r.counterparty_revision,'quote_code',q.code,'currency_code',q.currency_code,'multiplier',q.multiplier::text,'decimal_places',cur.display_minor_units) INTO result
 FROM security.facility_gas_target(catalog,obligation,flow_day,as_of)t
 JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(catalog,t.trade_id,t.trade_revision)
 JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(catalog,obligation)
 JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
 JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code)
 JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
 JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code,q.quantity_code)=(t.reference_catalog_id,t.reference_revision_id,c.quote_code,t.quantity_code)
 JOIN ref_data.currencies cur ON(cur.catalog_id,cur.revision_id,cur.code)=(q.catalog_id,q.revision_id,q.currency_code);
 IF result IS NULL THEN RAISE EXCEPTION 'gas_operations_scope';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,(result->>'trade_id')::uuid,false);
 PERFORM security.require_gas_operations(catalog,'read');RETURN result;
END$$;
CREATE FUNCTION security.require_gas_service(catalog uuid,account uuid) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid;BEGIN
 IF security.gas_service_visible(catalog,account) IS NOT TRUE THEN RAISE EXCEPTION 'gas_operations_denied' USING ERRCODE='42501';END IF;
 SELECT trade_id INTO STRICT trade FROM trading.gas_service_accounts WHERE(catalog_id,id)=(catalog,account);
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
END$$;

CREATE FUNCTION security.gas_operations_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.gas_operation_commands;actor uuid;BEGIN
 PERFORM security.require_gas_operations(catalog,'read');
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.gas_operation_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 PERFORM security.require_gas_operations(catalog,'read');
 IF prior.operation_id IS NULL THEN RETURN NULL;END IF;
 IF prior.actor_id IS DISTINCT FROM actor OR payload IS NOT NULL AND prior.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
 PERFORM security.require_gas_operation_result(catalog,prior);RETURN prior.outcome;
END$$;
-- Native reads retain the runtime role and all restrictive source/temporal policies.
CREATE FUNCTION security.read_gas_operations(catalog uuid,trade uuid,flow_day date) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 PERFORM security.require_gas_operations(catalog,'read');PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 result=jsonb_build_object(
 'accounts',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.id),'[]') FROM(SELECT a.*,v.id version_id,v.revision,v.valid_from,v.valid_to,v.active,v.statement_policy,v.terms_reference,v.evidence version_evidence FROM trading.gas_service_accounts a JOIN LATERAL(SELECT * FROM trading.gas_service_versions v WHERE(v.catalog_id,v.account_id)=(a.catalog_id,a.id) ORDER BY revision DESC LIMIT 1)v ON true WHERE a.catalog_id=catalog)x),
 'nominations',(SELECT coalesce(jsonb_agg((to_jsonb(n)||jsonb_build_object('quantity',trim_scale(n.quantity)::text)) ORDER BY n.id),'[]') FROM trading.gas_nomination_requests n JOIN trading.gas_service_accounts a ON(a.catalog_id,a.id)=(n.catalog_id,n.account_id) WHERE(a.catalog_id,a.trade_id,n.day)=(catalog,trade,flow_day)),
 'statements',(SELECT coalesce(jsonb_agg((to_jsonb(s)||jsonb_build_object('balance',trim_scale(s.balance)::text)) ORDER BY s.account_id,s.revision),'[]') FROM trading.gas_balance_statements s WHERE(s.catalog_id,s.day)=(catalog,flow_day)),
 'agreements',(SELECT coalesce(jsonb_agg((to_jsonb(t)||jsonb_build_object('quantity',trim_scale(t.quantity)::text,'price',trim_scale(t.price)::text)) ORDER BY t.id),'[]') FROM trading.gas_imbalance_agreements t WHERE(t.catalog_id,t.trade_id,t.day)=(catalog,trade,flow_day)),
 'responses',(SELECT coalesce(jsonb_agg((to_jsonb(r)||jsonb_build_object('quantity',trim_scale(r.quantity)::text)) ORDER BY r.id),'[]') FROM trading.gas_operation_responses r WHERE r.catalog_id=catalog AND(r.nomination_id IN(SELECT n.id FROM trading.gas_nomination_requests n JOIN trading.gas_service_accounts a ON(a.catalog_id,a.id)=(n.catalog_id,n.account_id) WHERE(a.trade_id,n.day)=(trade,flow_day)) OR r.imbalance_id IN(SELECT t.id FROM trading.gas_imbalance_agreements t WHERE(t.trade_id,t.day)=(trade,flow_day)))),
 'nominationApplications',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY a.nomination_id,a.ordinal),'[]') FROM trading.gas_nomination_applications a JOIN trading.gas_nomination_requests n ON(n.catalog_id,n.id)=(a.catalog_id,a.nomination_id) JOIN trading.gas_service_accounts s ON(s.catalog_id,s.id)=(n.catalog_id,n.account_id) WHERE(a.catalog_id,s.trade_id,n.day)=(catalog,trade,flow_day)),
 'imbalanceApplications',(SELECT coalesce(jsonb_agg((to_jsonb(a)||jsonb_build_object('cash_amount',trim_scale(a.cash_amount)::text)) ORDER BY a.id),'[]') FROM trading.gas_imbalance_applications a JOIN trading.gas_imbalance_agreements t ON(t.catalog_id,t.id)=(a.catalog_id,a.agreement_id) WHERE(a.catalog_id,t.trade_id,t.day)=(catalog,trade,flow_day)),
 'movements',(SELECT coalesce(jsonb_agg((to_jsonb(m)||jsonb_build_object('quantity',trim_scale(m.quantity)::text)) ORDER BY m.account_id,m.ordinal),'[]') FROM trading.gas_account_movements m WHERE(m.catalog_id,m.day)=(catalog,flow_day)),
 'heads',(SELECT coalesce(jsonb_agg(h.value||jsonb_build_object('quantity',h.value->>'quantity') ORDER BY h.ordinal),'[]') FROM jsonb_array_elements(security.read_gas_quantity_rows(catalog,trade,'heads')) WITH ORDINALITY h(value,ordinal)));
 PERFORM security.require_gas_operations(catalog,'read');RETURN result;
END$$;


CREATE FUNCTION security.gas_operations_context(catalog uuid,context uuid,as_of timestamptz,publication integer,effective timestamptz) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF effective>as_of OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,publication) WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date) AND publication<=x.known_through AND k.known_from<=t.known_at) THEN RAISE EXCEPTION 'gas_operations_context';END IF;
END$$;
CREATE FUNCTION security.gas_operations_anchor_context(catalog uuid,obligation uuid,as_of timestamptz,context uuid) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.gas_operations_context(catalog,context,as_of,r.publication_order,r.effective_at)
 FROM trading.gas_delivery_obligations o JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(o.catalog_id,o.trade_id,o.origin_revision,o.instrument_id) WHERE(o.catalog_id,o.id)=(catalog,obligation);
 PERFORM security.gas_operations_context(catalog,context,as_of,p.publication_order,p.effective_at)
 FROM trading.gas_delivery_obligations o JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id)=(o.catalog_id,o.instrument_id) JOIN trading.side_schedule_publications p ON(p.catalog_id,p.schedule_id)=(s.catalog_id,s.schedule_id) WHERE(o.catalog_id,o.id)=(catalog,obligation);
END$$;
CREATE FUNCTION security.gas_operations_quantity_context(catalog uuid,obligation uuid,flow_day date,as_of timestamptz,context uuid) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.gas_operations_context(catalog,context,as_of,h.publication_order,h.effective_at) FROM(
 SELECT DISTINCT ON(s.kind)r.publication_order,r.effective_at FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id) WHERE(s.catalog_id,s.obligation_id,r.day)=(catalog,obligation,flow_day) AND s.kind IN('nominated','confirmed','scheduled') ORDER BY s.kind,r.revision DESC)h;
END$$;
CREATE FUNCTION security.gas_operations_keys(value jsonb,keys text[]) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF jsonb_typeof(value) IS DISTINCT FROM 'object' OR NOT(value?&keys) OR EXISTS(SELECT 1 FROM jsonb_object_keys(value) k WHERE NOT(k=ANY(keys))) THEN RAISE EXCEPTION 'gas_operations_input';END IF;
END$$;
CREATE FUNCTION security.validate_gas_operations(body jsonb) RETURNS void LANGUAGE plpgsql IMMUTABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';d jsonb=p->'details';field_name text;v text;keys text[];BEGIN
 PERFORM security.gas_operations_keys(body,ARRAY['preview','operationId','previewHash']);
 PERFORM security.gas_operations_keys(p,ARRAY['context','tradeId','expectedRevision','day','kind','details']);
 IF jsonb_typeof(p->'context') IS DISTINCT FROM 'object' OR NOT(p->'context'?&ARRAY['snapshotId','convention','knowledgeMode']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(p->'context') k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId')) OR p#>>'{context,convention}' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' THEN RAISE EXCEPTION 'gas_operations_input';END IF;
 keys=CASE p->>'kind'
 WHEN 'service' THEN ARRAY['obligationId','holderRole','externalReference','expectedPreviousId','validFrom','validTo','active','statementPolicy','termsReference','evidence']
 WHEN 'nomination' THEN ARRAY['accountId','serviceVersionId','externalReference','expectedPreviousId','quantity','submittedAt','channel','reason','evidence']
 WHEN 'response' THEN ARRAY['nominationId','imbalanceId','side','decision','quantity','externalReference','respondedAt','expectedPreviousId','evidence']
 WHEN 'nomination_apply' THEN ARRAY['nominationId','counterpartyResponseId','pipelineResponseId','expectedPreviousId','profile','evidence']
 WHEN 'statement' THEN ARRAY['accountId','serviceVersionId','externalReference','expectedPreviousId','balance','status','reportedAt','includedMovementId','evidence']
 WHEN 'imbalance' THEN ARRAY['buyerAccountId','buyerVersionId','sellerAccountId','sellerVersionId','externalReference','quantity','price','agreedAt','dueAt','decimalPlaces','roundingPolicy','profile','evidence']
 WHEN 'imbalance_apply' THEN ARRAY['imbalanceId','evidence'] ELSE NULL END;
 IF keys IS NULL THEN RAISE EXCEPTION 'gas_operations_input';END IF;
 PERFORM security.gas_operations_keys(d,keys);
 FOREACH field_name IN ARRAY ARRAY['day','validFrom','validTo'] LOOP
  v=CASE field_name WHEN 'day' THEN p->>field_name ELSE d->>field_name END;
  IF (field_name='day' OR d?field_name) AND(v IS NULL OR v!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR v::date::text IS DISTINCT FROM v) THEN RAISE EXCEPTION 'gas_operations_input';END IF;
 END LOOP;
 FOREACH field_name IN ARRAY ARRAY['submittedAt','respondedAt','reportedAt','agreedAt','dueAt'] LOOP
  IF d?field_name THEN v=d->>field_name;IF v IS NULL OR v!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}|[.][0-9]{6}0)?(Z|[+]00:00)$' OR NOT isfinite(v::timestamptz) OR v::timestamptz>=timestamptz '9999-12-31T00:00:00Z' THEN RAISE EXCEPTION 'gas_operations_input';END IF;END IF;
 END LOOP;
 FOR field_name IN SELECT jsonb_object_keys(d) LOOP
  IF field_name IN('active') THEN
   IF jsonb_typeof(d->field_name) IS DISTINCT FROM 'boolean' THEN RAISE EXCEPTION 'gas_operations_input';END IF;
  ELSIF field_name='decimalPlaces' THEN
   IF jsonb_typeof(d->field_name) IS DISTINCT FROM 'number' OR (d->>field_name)::numeric<>trunc((d->>field_name)::numeric) THEN RAISE EXCEPTION 'gas_operations_input';END IF;
  ELSIF d->field_name='null'::jsonb THEN
   IF field_name NOT IN('expectedPreviousId','nominationId','imbalanceId','includedMovementId','quantity','balance') OR field_name='quantity' AND(p->>'kind'<>'response' OR d->>'decision'='approved') OR field_name='balance' AND d->>'status'<>'withdrawn' THEN RAISE EXCEPTION 'gas_operations_input';END IF;
  ELSE
   IF jsonb_typeof(d->field_name) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_operations_input';END IF;
   IF right(field_name,2)='Id' AND(d->>field_name)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_operations_input';END IF;
   IF field_name IN('quantity','balance','price') AND (d->>field_name!~'^-?[0-9]+([.][0-9]+)?$' OR NOT(abs((d->>field_name)::numeric)<1e24) OR scale(trim_scale((d->>field_name)::numeric))>18) THEN RAISE EXCEPTION 'gas_operations_input';END IF;
  END IF;
 END LOOP;
 IF jsonb_typeof(body->'operationId') IS DISTINCT FROM 'string' OR (body->>'operationId')::uuid='00000000-0000-0000-0000-000000000000'::uuid OR jsonb_typeof(body->'previewHash') IS DISTINCT FROM 'string' OR length(body->>'previewHash')<>64 OR jsonb_typeof(p->'expectedRevision') IS DISTINCT FROM 'number' OR jsonb_typeof(d->'evidence') IS DISTINCT FROM 'string' OR length(btrim(d->>'evidence')) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'gas_operations_input';END IF;
END$$;
CREATE FUNCTION security.require_gas_operation_result(catalog uuid,receipt trading.gas_operation_commands) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account uuid;nomination uuid;agreement uuid;BEGIN
 PERFORM security.require_gas_operations(catalog,receipt.kind);PERFORM security.require_gas_quantity_trade(catalog,receipt.trade_id,false);
 IF receipt.service_version_id IS NOT NULL THEN SELECT account_id INTO account FROM trading.gas_service_versions WHERE(catalog_id,id)=(catalog,receipt.service_version_id);END IF;
 IF receipt.statement_id IS NOT NULL THEN SELECT account_id INTO account FROM trading.gas_balance_statements WHERE(catalog_id,id)=(catalog,receipt.statement_id);END IF;
 nomination=receipt.nomination_id;agreement=receipt.imbalance_id;
 IF receipt.response_id IS NOT NULL THEN SELECT nomination_id,imbalance_id INTO nomination,agreement FROM trading.gas_operation_responses WHERE(catalog_id,id)=(catalog,receipt.response_id);END IF;
 IF receipt.nomination_application_id IS NOT NULL THEN SELECT nomination_id INTO nomination FROM trading.gas_nomination_applications WHERE(catalog_id,id)=(catalog,receipt.nomination_application_id);END IF;
 IF receipt.imbalance_application_id IS NOT NULL THEN SELECT agreement_id INTO agreement FROM trading.gas_imbalance_applications WHERE(catalog_id,id)=(catalog,receipt.imbalance_application_id);END IF;
 IF account IS NOT NULL THEN PERFORM security.require_gas_service(catalog,account);END IF;
 IF nomination IS NOT NULL AND NOT security.gas_nomination_visible(catalog,nomination) OR agreement IS NOT NULL AND NOT security.gas_imbalance_visible(catalog,agreement) THEN RAISE EXCEPTION 'gas_operations_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_operations(catalog,receipt.kind);
END$$;
-- Typed canonical facts for semantic idempotence; receipt payloads themselves
-- remain exact transport records. External references/evidence are case sensitive.
CREATE FUNCTION security.gas_operations_fact(d jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=d-'expectedPreviousId';field_name text;value text;BEGIN
 FOR field_name,value IN SELECT key,j.value FROM jsonb_each_text(result)j LOOP
  IF value IS NULL THEN CONTINUE;END IF;
  IF field_name IN('quantity','balance','price') THEN result=jsonb_set(result,ARRAY[field_name],to_jsonb(trim_scale(value::numeric)::text));
  ELSIF right(field_name,2)='Id' THEN result=jsonb_set(result,ARRAY[field_name],to_jsonb(value::uuid::text));
  ELSIF field_name IN('submittedAt','respondedAt','reportedAt','agreedAt','dueAt') THEN result=jsonb_set(result,ARRAY[field_name],to_jsonb(to_char(value::timestamptz AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')));END IF;
 END LOOP;RETURN result;
END$$;
CREATE FUNCTION security.replay_gas_operations(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';prior trading.gas_operation_commands;actor uuid;old jsonb;operation uuid;BEGIN
 PERFORM security.validate_gas_operations(body);operation=(body->>'operationId')::uuid;
 IF p#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR p#>>'{context,contextId}' IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM security.require_gas_operations(catalog,p->>'kind');PERFORM security.require_gas_quantity_trade(catalog,(p->>'tradeId')::uuid,false);
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.require_gas_operations(catalog,p->>'kind');
 old=security.gas_operations_status(catalog,operation,body);IF old IS NOT NULL THEN RETURN old;END IF;
 SELECT * INTO prior FROM trading.gas_operation_commands c WHERE(c.catalog_id,c.trade_id,c.kind,c.effective_at)=(catalog,(p->>'tradeId')::uuid,p->>'kind',as_of) AND c.request#>>'{preview,day}'=p->>'day' AND security.gas_operations_fact(c.request#>'{preview,details}')=security.gas_operations_fact(p->'details') ORDER BY operation_id LIMIT 1;
 IF NOT FOUND THEN RETURN NULL;END IF;
 PERFORM security.require_gas_operation_result(catalog,prior);
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 prior.operation_id=operation;prior.actor_id=actor;prior.request=body;prior.outcome=prior.outcome||jsonb_build_object('operationId',operation,'unchanged',true);
 INSERT INTO trading.gas_operation_commands SELECT prior.*;
 PERFORM security.require_gas_operation_result(catalog,prior);RETURN prior.outcome;
END$$;
CREATE FUNCTION security.gas_operations_head(catalog uuid,obligation uuid,flow_day date,requested_kind text) RETURNS uuid LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT r.id FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id) WHERE(s.catalog_id,s.obligation_id,r.day,s.kind)=(catalog,obligation,flow_day,requested_kind) ORDER BY r.revision DESC LIMIT 1
$$;
CREATE FUNCTION security.gas_operations_version(catalog uuid,account uuid,version uuid,flow_day date,as_of timestamptz,context uuid,adopt boolean DEFAULT true) RETURNS trading.gas_service_accounts LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE a trading.gas_service_accounts;v trading.gas_service_versions;BEGIN
 PERFORM security.require_gas_service(catalog,account);
 SELECT * INTO STRICT a FROM trading.gas_service_accounts WHERE(catalog_id,id)=(catalog,account);
 SELECT * INTO v FROM trading.gas_service_versions WHERE(catalog_id,account_id)=(catalog,account) AND(adopt OR id=version) ORDER BY revision DESC LIMIT 1;
 IF v.id IS DISTINCT FROM version OR adopt AND(NOT v.active OR flow_day<v.valid_from OR flow_day>=v.valid_to) THEN RAISE EXCEPTION 'gas_operations_service';END IF;
 PERFORM security.gas_operations_context(catalog,context,as_of,v.publication_order,v.effective_at);
 PERFORM security.gas_operations_anchor_context(catalog,a.origin_obligation_id,as_of,context);RETURN a;
END$$;
CREATE FUNCTION security.gas_operations_balance(catalog uuid,account uuid,flow_day date,statement uuid,movement uuid,as_of timestamptz,context uuid) RETURNS numeric LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s trading.gas_balance_statements;m trading.gas_account_movements;v trading.gas_service_versions;included integer=0;result numeric;BEGIN
 PERFORM security.require_gas_service(catalog,account);
 SELECT * INTO s FROM trading.gas_balance_statements WHERE(catalog_id,account_id,day)=(catalog,account,flow_day) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO v FROM trading.gas_service_versions WHERE(catalog_id,account_id)=(catalog,account) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO m FROM trading.gas_account_movements WHERE(catalog_id,account_id,day)=(catalog,account,flow_day) ORDER BY ordinal DESC LIMIT 1;
 IF s.id IS NULL OR s.id IS DISTINCT FROM statement OR m.id IS DISTINCT FROM movement OR s.status='withdrawn' OR v.statement_policy='final_statement_balance_v1' AND s.status<>'final' THEN RAISE EXCEPTION 'gas_operations_balance';END IF;
 PERFORM security.gas_operations_context(catalog,context,as_of,s.publication_order,s.effective_at);
 IF s.included_movement_id IS NOT NULL THEN SELECT ordinal INTO STRICT included FROM trading.gas_account_movements WHERE(catalog_id,account_id,day,id)=(catalog,account,flow_day,s.included_movement_id);END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_account_movements mov JOIN trading.gas_imbalance_applications a ON(a.catalog_id,a.id)=(mov.catalog_id,mov.application_id) WHERE(mov.catalog_id,mov.account_id,mov.day)=(catalog,account,flow_day) AND(a.effective_at>as_of OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,a.publication_order) WHERE(t.catalog_id,t.id)=(catalog,context) AND a.publication_order<=x.known_through AND k.known_from<=t.known_at))) THEN RAISE EXCEPTION 'gas_operations_context';END IF;
 SELECT s.balance+coalesce(sum(quantity),0) INTO result FROM trading.gas_account_movements WHERE(catalog_id,account_id,day)=(catalog,account,flow_day) AND ordinal>included;
 RETURN result;
END$$;
CREATE FUNCTION security.record_gas_operations(catalog uuid,body jsonb,as_of timestamptz,context uuid,calculation jsonb) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb=body->'preview';d jsonb=p->'details';kind text=p->>'kind';trade uuid;operation uuid;flow_day date;actor uuid;publication integer;identity uuid=gen_random_uuid();old jsonb;result jsonb;receipt trading.gas_operation_commands;
 a trading.gas_service_accounts;b trading.gas_service_accounts;previous trading.gas_service_versions;source jsonb;other_source jsonb;
 n trading.gas_nomination_requests;nr trading.gas_nomination_requests;prior_s trading.gas_balance_statements;r trading.gas_operation_responses;cp trading.gas_operation_responses;pipe trading.gas_operation_responses;app trading.gas_nomination_applications;t trading.gas_imbalance_agreements;head trading.gas_trade_revisions;
 confirmed uuid;scheduled uuid;nom_head uuid;child uuid;native jsonb;projection jsonb;qty numeric;buy_balance numeric;sell_balance numeric;cash numeric;scaled numeric;whole numeric;expected numeric;places integer;ms trading.gas_account_movements;account uuid;included trading.gas_account_movements;
BEGIN
 PERFORM security.validate_gas_operations(body);trade=(p->>'tradeId')::uuid;operation=(body->>'operationId')::uuid;flow_day=(p->>'day')::date;
 old=security.replay_gas_operations(catalog,body,as_of);IF old IS NOT NULL THEN RETURN old;END IF;
 IF as_of IS NULL OR NOT isfinite(as_of) OR context IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts ctx JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(ctx.catalog_id,ctx.id) WHERE(ctx.catalog_id,ctx.id,ctx.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date)) THEN RAISE EXCEPTION 'gas_operations_context';END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(p->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
 PERFORM security.gas_operations_context(catalog,context,as_of,head.publication_order,head.effective_at);
 IF(SELECT count(*) FROM trading.gas_operation_commands WHERE(catalog_id,trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_operations_limit';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 -- Every publication and all native projections participate in this transaction.
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 receipt.catalog_id=catalog;receipt.operation_id=operation;receipt.trade_id=trade;receipt.actor_id=actor;receipt.kind=kind;receipt.request=body;receipt.effective_at=as_of;
 IF kind='service' THEN
  source=security.gas_service_source(catalog,(d->>'obligationId')::uuid,flow_day,as_of);
  PERFORM security.gas_operations_anchor_context(catalog,(d->>'obligationId')::uuid,as_of,context);
  IF(source->>'trade_id')::uuid<>trade OR d->>'holderRole' NOT IN('legal_entity','counterparty') OR d->>'holderRole' IS NULL OR jsonb_typeof(d->'active') IS DISTINCT FROM 'boolean' THEN RAISE EXCEPTION 'gas_operations_service';END IF;
  SELECT * INTO a FROM trading.gas_service_accounts WHERE(catalog_id,reference_catalog_id,pipeline_code,holder_role,external_reference)=(catalog,(source->>'reference_catalog_id')::uuid,source->>'pipeline_code',d->>'holderRole',d->>'externalReference') AND CASE holder_role WHEN 'legal_entity' THEN legal_entity_id ELSE counterparty_id END=(source->>(CASE d->>'holderRole' WHEN 'legal_entity' THEN 'legal_entity_id' ELSE 'counterparty_id' END))::uuid;
  IF a.id IS NULL THEN
   IF d->>'expectedPreviousId' IS NOT NULL THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
   INSERT INTO trading.gas_service_accounts VALUES(catalog,gen_random_uuid(),(d->>'obligationId')::uuid,trade,(source->>'reference_catalog_id')::uuid,(source->>'reference_revision_id')::uuid,source->>'pipeline_code',source->>'zone_code',source->>'point_code',source->>'product_code',source->>'quantity_code',source->>'quote_code',(source->>'legal_entity_id')::uuid,(source->>'legal_entity_revision')::integer,(source->>'counterparty_id')::uuid,(source->>'counterparty_revision')::integer,d->>'holderRole',d->>'externalReference',publication,as_of,actor) RETURNING * INTO a;
  ELSE
   IF(a.origin_obligation_id,a.trade_id) IS DISTINCT FROM((d->>'obligationId')::uuid,trade) THEN RAISE EXCEPTION 'gas_operations_pool_continuity';END IF;
  END IF;
  SELECT * INTO previous FROM trading.gas_service_versions WHERE(catalog_id,account_id)=(catalog,a.id) ORDER BY revision DESC LIMIT 1;
  IF previous.id IS DISTINCT FROM(d->>'expectedPreviousId')::uuid OR previous.effective_at>as_of THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
  IF previous.id IS NOT NULL THEN PERFORM security.gas_operations_context(catalog,context,as_of,previous.publication_order,previous.effective_at);END IF;
  INSERT INTO trading.gas_service_versions VALUES(catalog,identity,a.id,coalesce(previous.revision,0)+1,previous.id,(d->>'validFrom')::date,(d->>'validTo')::date,(d->>'active')::boolean,d->>'statementPolicy',d->>'termsReference',d->>'evidence',publication,as_of,context,actor);
  receipt.service_version_id=identity;result=jsonb_build_object('accountId',a.id);
 ELSIF kind IN('nomination','statement') THEN
  a=security.gas_operations_version(catalog,(d->>'accountId')::uuid,(d->>'serviceVersionId')::uuid,flow_day,as_of,context);
  IF a.trade_id<>trade THEN RAISE EXCEPTION 'gas_operations_scope';END IF;
  source=security.gas_service_source(catalog,a.origin_obligation_id,flow_day,as_of);
  IF kind='nomination' THEN
   PERFORM security.gas_operations_quantity_context(catalog,a.origin_obligation_id,flow_day,as_of,context);
   IF(d->>'submittedAt')::timestamptz>as_of OR d->>'quantity' IS NULL THEN RAISE EXCEPTION 'gas_operations_nomination';END IF;
   SELECT * INTO nr FROM trading.gas_nomination_requests WHERE(catalog_id,account_id,day,external_reference)=(catalog,a.id,flow_day,d->>'externalReference') ORDER BY revision DESC LIMIT 1;
   IF nr.id IS DISTINCT FROM(d->>'expectedPreviousId')::uuid OR nr.effective_at>as_of THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
   confirmed=security.gas_operations_head(catalog,a.origin_obligation_id,flow_day,'confirmed');scheduled=security.gas_operations_head(catalog,a.origin_obligation_id,flow_day,'scheduled');nom_head=security.gas_operations_head(catalog,a.origin_obligation_id,flow_day,'nominated');
   IF confirmed IS DISTINCT FROM(calculation->>'confirmedHead')::uuid OR scheduled IS DISTINCT FROM(calculation->>'scheduledHead')::uuid OR nom_head IS DISTINCT FROM(calculation->>'nominatedHead')::uuid THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
   child=gen_random_uuid();native=jsonb_build_object('context',p->'context','tradeId',trade,'expectedRevision',head.revision,'operationId',child,'obligationId',a.origin_obligation_id,'kind','nominated','status','provisional','day',flow_day,'quantity',d->>'quantity','evidence','Recorded nomination projection; receipt '||child::text,'expectedPreviousId',nom_head);
   projection=security.record_gas_daily_quantity(catalog,native,as_of,context);
   INSERT INTO trading.gas_nomination_requests VALUES(catalog,identity,a.id,(d->>'serviceVersionId')::uuid,flow_day,d->>'externalReference',coalesce(nr.revision,0)+1,nr.id,(d->>'quantity')::numeric,(d->>'submittedAt')::timestamptz,d->>'channel',d->>'reason',d->>'evidence',(projection->>'revisionId')::uuid,confirmed,scheduled,publication,as_of,context,actor);
   receipt.nomination_id=identity;
  ELSE
   SELECT * INTO prior_s FROM trading.gas_balance_statements WHERE(catalog_id,account_id,day)=(catalog,a.id,flow_day) ORDER BY revision DESC LIMIT 1;
   IF prior_s.id IS DISTINCT FROM(d->>'expectedPreviousId')::uuid OR prior_s.effective_at>as_of OR(d->>'reportedAt')::timestamptz>as_of OR as_of<(source->>'native_to')::timestamptz THEN RAISE EXCEPTION 'gas_operations_statement';END IF;
   IF d->>'includedMovementId' IS NOT NULL THEN
    SELECT * INTO included FROM trading.gas_account_movements WHERE(catalog_id,account_id,day,id)=(catalog,a.id,flow_day,(d->>'includedMovementId')::uuid);
    IF included.id IS NULL OR included.external_effective_at>(d->>'reportedAt')::timestamptz THEN RAISE EXCEPTION 'gas_operations_statement';END IF;
    PERFORM security.gas_operations_context(catalog,context,as_of,x.publication_order,x.effective_at) FROM trading.gas_imbalance_applications x WHERE(x.catalog_id,x.id)=(catalog,included.application_id);
   END IF;
   INSERT INTO trading.gas_balance_statements VALUES(catalog,identity,a.id,(d->>'serviceVersionId')::uuid,flow_day,coalesce(prior_s.revision,0)+1,prior_s.id,d->>'externalReference',(d->>'reportedAt')::timestamptz,d->>'status',(d->>'balance')::numeric,(d->>'includedMovementId')::uuid,d->>'evidence',publication,as_of,context,actor);
   receipt.statement_id=identity;
  END IF;
 ELSIF kind IN('response','nomination_apply','imbalance_apply') THEN
  IF d->>'nominationId' IS NOT NULL THEN
   SELECT * INTO STRICT n FROM trading.gas_nomination_requests WHERE(catalog_id,id)=(catalog,(d->>'nominationId')::uuid);
   a=security.gas_operations_version(catalog,n.account_id,n.service_version_id,flow_day,as_of,context,kind<>'response');
   IF a.trade_id<>trade OR n.day<>flow_day THEN RAISE EXCEPTION 'gas_operations_scope';END IF;
   PERFORM security.gas_operations_context(catalog,context,as_of,n.publication_order,n.effective_at);
  ELSE
   SELECT * INTO STRICT t FROM trading.gas_imbalance_agreements WHERE(catalog_id,id)=(catalog,(d->>'imbalanceId')::uuid);
   a=security.gas_operations_version(catalog,t.buyer_account_id,t.buyer_version_id,flow_day,as_of,context,kind<>'response');b=security.gas_operations_version(catalog,t.seller_account_id,t.seller_version_id,flow_day,as_of,context,kind<>'response');
   IF t.trade_id<>trade OR t.day<>flow_day THEN RAISE EXCEPTION 'gas_operations_scope';END IF;
   PERFORM security.gas_operations_context(catalog,context,as_of,t.publication_order,t.effective_at);
  END IF;
  IF kind='response' THEN
   IF num_nonnulls(d->>'nominationId',d->>'imbalanceId')<>1 OR(d->>'respondedAt')::timestamptz>as_of OR(d->>'respondedAt')::timestamptz<coalesce(n.submitted_at,t.agreed_at) THEN RAISE EXCEPTION 'gas_operations_response';END IF;
   SELECT * INTO r FROM trading.gas_operation_responses WHERE catalog_id=catalog AND nomination_id IS NOT DISTINCT FROM n.id AND imbalance_id IS NOT DISTINCT FROM t.id AND side=d->>'side' ORDER BY revision DESC LIMIT 1;
   IF r.id IS DISTINCT FROM(d->>'expectedPreviousId')::uuid OR r.effective_at>as_of THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
   INSERT INTO trading.gas_operation_responses VALUES(catalog,identity,n.id,t.id,d->>'side',d->>'decision',(d->>'quantity')::numeric,d->>'externalReference',(d->>'respondedAt')::timestamptz,d->>'evidence',coalesce(r.revision,0)+1,r.id,publication,as_of,context,actor);receipt.response_id=identity;
  ELSE
   SELECT * INTO cp FROM trading.gas_operation_responses WHERE catalog_id=catalog AND nomination_id IS NOT DISTINCT FROM n.id AND imbalance_id IS NOT DISTINCT FROM t.id AND side='counterparty' ORDER BY revision DESC LIMIT 1;
   SELECT * INTO pipe FROM trading.gas_operation_responses WHERE catalog_id=catalog AND nomination_id IS NOT DISTINCT FROM n.id AND imbalance_id IS NOT DISTINCT FROM t.id AND side='pipeline' ORDER BY revision DESC LIMIT 1;
   IF cp.id IS NULL OR pipe.id IS NULL OR cp.id IS DISTINCT FROM(calculation->>'counterpartyId')::uuid OR pipe.id IS DISTINCT FROM(calculation->>'pipelineId')::uuid OR cp.decision<>'approved' OR pipe.decision<>'approved' OR EXISTS(SELECT 1 FROM trading.gas_operation_responses WHERE catalog_id=catalog AND nomination_id IS NOT DISTINCT FROM n.id AND imbalance_id IS NOT DISTINCT FROM t.id AND side='request') THEN RAISE EXCEPTION 'gas_operations_acknowledgment';END IF;
   PERFORM security.gas_operations_context(catalog,context,as_of,cp.publication_order,cp.effective_at);PERFORM security.gas_operations_context(catalog,context,as_of,pipe.publication_order,pipe.effective_at);
   IF kind='nomination_apply' THEN
    PERFORM security.gas_operations_quantity_context(catalog,a.origin_obligation_id,flow_day,as_of,context);
    IF cp.id IS DISTINCT FROM(d->>'counterpartyResponseId')::uuid OR pipe.id IS DISTINCT FROM(d->>'pipelineResponseId')::uuid THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
    IF n.id IS NULL OR d->>'profile' IS DISTINCT FROM 'manual_confirmed_ceiling_v1' OR pipe.quantity>cp.quantity OR pipe.quantity>n.quantity THEN RAISE EXCEPTION 'gas_operations_nomination';END IF;
    SELECT * INTO app FROM trading.gas_nomination_applications WHERE(catalog_id,nomination_id)=(catalog,n.id) ORDER BY ordinal DESC LIMIT 1;
    IF app.id IS DISTINCT FROM(d->>'expectedPreviousId')::uuid OR app.id IS DISTINCT FROM(calculation->>'previousApplicationId')::uuid OR security.gas_operations_head(catalog,a.origin_obligation_id,flow_day,'nominated') IS DISTINCT FROM n.nomination_id OR security.gas_operations_head(catalog,a.origin_obligation_id,flow_day,'confirmed') IS DISTINCT FROM coalesce(app.confirmed_id,n.base_confirmed_id) OR security.gas_operations_head(catalog,a.origin_obligation_id,flow_day,'scheduled') IS DISTINCT FROM coalesce(app.scheduled_id,n.base_scheduled_id) THEN RAISE EXCEPTION 'gas_operations_conflict';END IF;
    child=gen_random_uuid();native=jsonb_build_object('context',p->'context','tradeId',trade,'expectedRevision',head.revision,'obligationId',a.origin_obligation_id,'status','provisional','day',flow_day,'evidence','Accepted scheduling projection; receipt '||child::text);
    projection=security.record_gas_daily_quantity(catalog,native||jsonb_build_object('operationId',child,'kind','confirmed','quantity',cp.quantity::text,'expectedPreviousId',coalesce(app.confirmed_id,n.base_confirmed_id)),as_of,context);confirmed=(projection->>'revisionId')::uuid;
    child=gen_random_uuid();projection=security.record_gas_daily_quantity(catalog,native||jsonb_build_object('operationId',child,'kind','scheduled','quantity',pipe.quantity::text,'expectedPreviousId',coalesce(app.scheduled_id,n.base_scheduled_id),'evidence','Accepted scheduling projection; receipt '||child::text),as_of,context);scheduled=(projection->>'revisionId')::uuid;
    INSERT INTO trading.gas_nomination_applications VALUES(catalog,identity,n.id,coalesce(app.ordinal,0)+1,app.id,cp.id,pipe.id,confirmed,scheduled,d->>'evidence',publication,as_of,context,actor);receipt.nomination_application_id=identity;
   ELSE
    IF t.id IS NULL OR cp.quantity<>t.quantity OR pipe.quantity<>t.quantity OR EXISTS(SELECT 1 FROM trading.gas_imbalance_applications WHERE(catalog_id,agreement_id)=(catalog,t.id)) THEN RAISE EXCEPTION 'gas_operations_imbalance';END IF;
    buy_balance=security.gas_operations_balance(catalog,a.id,flow_day,(calculation->>'buyerStatementId')::uuid,(calculation->>'buyerMovementId')::uuid,as_of,context);sell_balance=security.gas_operations_balance(catalog,b.id,flow_day,(calculation->>'sellerStatementId')::uuid,(calculation->>'sellerMovementId')::uuid,as_of,context);
    IF buy_balance>=0 OR sell_balance<=0 OR t.quantity>sell_balance OR t.quantity> -buy_balance THEN RAISE EXCEPTION 'gas_operations_balance';END IF;
    source=security.gas_service_source(catalog,CASE WHEN a.holder_role='legal_entity' THEN a.origin_obligation_id ELSE b.origin_obligation_id END,flow_day,as_of);places=t.decimal_places;
    cash=t.quantity*t.price*(source->>'multiplier')::numeric*CASE WHEN a.holder_role='legal_entity' THEN -1 ELSE 1 END;scaled=cash*power(10::numeric,places);whole=trunc(scaled);expected=(whole+CASE WHEN abs(scaled-whole)>0.5 OR(abs(scaled-whole)=0.5 AND mod(abs(whole),2)=1) THEN sign(scaled) ELSE 0 END)/power(10::numeric,places);
    IF NOT(abs((calculation->>'numerator')::numeric)<1e120 AND (calculation->>'denominator')::numeric>0 AND (calculation->>'denominator')::numeric<1e120 AND scale((calculation->>'numerator')::numeric)=0 AND scale((calculation->>'denominator')::numeric)=0) OR(calculation->>'numerator') IS NULL OR(calculation->>'denominator') IS NULL THEN RAISE EXCEPTION 'gas_operations_amount';END IF;
    IF(calculation->>'cash')::numeric IS DISTINCT FROM expected OR(calculation->>'numerator')::numeric IS NULL OR(calculation->>'denominator')::numeric<=0 OR(calculation->>'numerator')::numeric<>cash*(calculation->>'denominator')::numeric THEN RAISE EXCEPTION 'gas_operations_amount';END IF;
    INSERT INTO trading.gas_imbalance_applications VALUES(catalog,identity,t.id,cp.id,pipe.id,(calculation->>'buyerStatementId')::uuid,(calculation->>'sellerStatementId')::uuid,(calculation->>'numerator')::numeric,(calculation->>'denominator')::numeric,expected,d->>'evidence',publication,as_of,context,actor);receipt.imbalance_application_id=identity;
    FOREACH account IN ARRAY ARRAY[a.id,b.id] LOOP
     SELECT * INTO ms FROM trading.gas_account_movements WHERE(catalog_id,account_id,day)=(catalog,account,flow_day) ORDER BY ordinal DESC LIMIT 1;
     INSERT INTO trading.gas_account_movements VALUES(catalog,gen_random_uuid(),account,flow_day,identity,coalesce(ms.ordinal,0)+1,ms.id,CASE WHEN account=a.id THEN t.quantity ELSE -t.quantity END,greatest(cp.responded_at,pipe.responded_at));
    END LOOP;
    result=jsonb_build_object('cash',trim_scale(expected)::text,'currency',source->>'currency_code','buyerBalance',trim_scale(buy_balance+t.quantity)::text,'sellerBalance',trim_scale(sell_balance-t.quantity)::text,'scope','nonportfolio_unposted_commercial_cash');
   END IF;
  END IF;
 ELSIF kind='imbalance' THEN
  a=security.gas_operations_version(catalog,(d->>'buyerAccountId')::uuid,(d->>'buyerVersionId')::uuid,flow_day,as_of,context);b=security.gas_operations_version(catalog,(d->>'sellerAccountId')::uuid,(d->>'sellerVersionId')::uuid,flow_day,as_of,context);
  IF a.id=b.id OR a.holder_role=b.holder_role OR(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.product_code,a.quantity_code) IS DISTINCT FROM(b.reference_catalog_id,b.reference_revision_id,b.pipeline_code,b.zone_code,b.product_code,b.quantity_code) OR a.legal_entity_id<>b.legal_entity_id OR(CASE WHEN a.holder_role='legal_entity' THEN a.trade_id ELSE b.trade_id END)<>trade THEN RAISE EXCEPTION 'gas_operations_scope';END IF;
  source=security.gas_service_source(catalog,a.origin_obligation_id,flow_day,as_of);other_source=security.gas_service_source(catalog,b.origin_obligation_id,flow_day,as_of);
  IF(source->>'native_from',source->>'native_to') IS DISTINCT FROM(other_source->>'native_from',other_source->>'native_to') OR(d->>'agreedAt')::timestamptz<(source->>'native_to')::timestamptz OR(d->>'agreedAt')::timestamptz>as_of THEN RAISE EXCEPTION 'gas_operations_imbalance';END IF;
  buy_balance=security.gas_operations_balance(catalog,a.id,flow_day,(calculation->>'buyerStatementId')::uuid,(calculation->>'buyerMovementId')::uuid,as_of,context);sell_balance=security.gas_operations_balance(catalog,b.id,flow_day,(calculation->>'sellerStatementId')::uuid,(calculation->>'sellerMovementId')::uuid,as_of,context);qty=(d->>'quantity')::numeric;
  IF qty IS NULL OR qty<=0 OR buy_balance>=0 OR sell_balance<=0 OR qty>sell_balance OR qty> -buy_balance THEN RAISE EXCEPTION 'gas_operations_balance';END IF;
  IF a.holder_role<>'legal_entity' THEN source=other_source;END IF;
  IF(d->>'decimalPlaces')::integer IS DISTINCT FROM(source->>'decimal_places')::integer THEN RAISE EXCEPTION 'gas_operations_rounding';END IF;
  INSERT INTO trading.gas_imbalance_agreements VALUES(catalog,identity,trade,a.id,(d->>'buyerVersionId')::uuid,b.id,(d->>'sellerVersionId')::uuid,flow_day,d->>'externalReference',qty,(d->>'price')::numeric,(d->>'agreedAt')::timestamptz,(d->>'dueAt')::timestamptz,(d->>'decimalPlaces')::integer,d->>'roundingPolicy',d->>'profile',d->>'evidence',publication,as_of,context,actor);receipt.imbalance_id=identity;
 ELSE RAISE EXCEPTION 'gas_operations_input';END IF;
 result=coalesce(result,'{}')||jsonb_build_object('operationId',operation,'tradeId',trade,'kind',kind,'id',identity,'unchanged',false);
 receipt.outcome=result;INSERT INTO trading.gas_operation_commands SELECT receipt.*;
 PERFORM security.require_gas_operation_result(catalog,receipt);RETURN result;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON(n.oid=p.pronamespace) WHERE n.nspname='security' AND p.proname IN('require_gas_operations','gas_service_visible','gas_imbalance_visible','gas_nomination_visible','gas_service_source','require_gas_service','gas_movement_visible','gas_operations_status','read_gas_operations','gas_operations_context','gas_operations_anchor_context','gas_operations_quantity_context','gas_operations_keys','validate_gas_operations','require_gas_operation_result','gas_operations_fact','replay_gas_operations','gas_operations_head','gas_operations_version','gas_operations_balance','record_gas_operations') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_security_owner',f.signature);
  IF f.proname IN('require_gas_operations','gas_service_visible','gas_imbalance_visible','gas_nomination_visible','gas_service_source','gas_movement_visible','validate_gas_operations','read_gas_operations','gas_operations_status','replay_gas_operations','record_gas_operations') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
-- Existing turns retain replay; new admission/execution requires this tool catalog.
DO $upgrade$
DECLARE signature text;definition text;before text:='runtime_set<>''turns_v18''';
BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)',
  'assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb,jsonb)'
 ] LOOP
  definition:=pg_get_functiondef(signature::regprocedure);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'gas_operations_runtime_guard';END IF;
  EXECUTE replace(definition,before,'runtime_set<>''turns_v19''');
 END LOOP;
END $upgrade$;
COMMIT;
