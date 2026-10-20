-- Exact immutable reports are a separate publication after generated marks.
-- No profiles, observations, reports or schedules are seeded by this migration.
-- Stop runtime/worker admission and drain in-flight calls before schema upgrade.
BEGIN;
ALTER TABLE trading.trade_revisions ADD UNIQUE(catalog_id,trade_id,revision,book_id,binding_id,instrument_id,publication_order);
ALTER TABLE trading.swap_trade_revisions ADD UNIQUE(catalog_id,trade_id,revision,book_id,binding_id,instrument_id,publication_order);
ALTER TABLE trading.swap_instruments ADD UNIQUE(catalog_id,instrument_id,fixed_side_id);
ALTER TABLE trading.swap_instruments ADD UNIQUE(catalog_id,instrument_id,floating_side_id);
ALTER TABLE trading.currency_discount_defaults ADD UNIQUE(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,currency_code,curve_code);
ALTER TABLE market_data.series ADD UNIQUE(catalog_id,revision_id,id,contract_id,quote_code);
ALTER TABLE market_data.observations ADD UNIQUE(catalog_id,id,definition_revision_id,series_id,source_code,role,value);
ALTER TABLE ref_data.quote_specifications ADD UNIQUE(catalog_id,revision_id,code,currency_code,quantity_code);
CREATE TABLE automation.pilot_reports(
 catalog_id uuid NOT NULL,id uuid NOT NULL PRIMARY KEY,run_id uuid NOT NULL UNIQUE REFERENCES automation.runs(id),
 close_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 context_id uuid NOT NULL,request_sha256 text NOT NULL CHECK(request_sha256~'^[0-9a-f]{64}$'),policy jsonb NOT NULL CHECK(jsonb_typeof(policy)='object'),work jsonb NOT NULL CHECK(jsonb_typeof(work)='object'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL CHECK(publication_order>0),publication_xid xid8 NOT NULL,
 CHECK(id=run_id AND id=close_id),UNIQUE(catalog_id,id),UNIQUE(catalog_id,id,definition_revision_id),
 UNIQUE(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),UNIQUE(catalog_id,publication_order,publication_xid),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id)
);
CREATE TABLE automation.pilot_report_models(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,model_version text NOT NULL CHECK(length(model_version) BETWEEN 1 AND 100),
 quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,future_contract_id uuid,swap_instrument_id uuid,future_series_code ref_data.code,
 PRIMARY KEY(catalog_id,report_id,id),UNIQUE(catalog_id,report_id,id,definition_revision_id),UNIQUE(catalog_id,report_id,id,quantity_code),
 FOREIGN KEY(catalog_id,report_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES automation.pilot_reports(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 CHECK((future_contract_id IS NOT NULL AND future_series_code IS NOT NULL AND swap_instrument_id IS NULL AND id=security.futures_contract_id(reference_catalog_id,reference_revision_id,future_series_code)) OR
       (future_contract_id IS NULL AND future_series_code IS NULL AND swap_instrument_id IS NOT NULL AND id=swap_instrument_id)),
 FOREIGN KEY(catalog_id,definition_revision_id,future_contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,future_contract_id,reference_catalog_id,reference_revision_id,future_series_code) REFERENCES market_data.contracts(catalog_id,revision_id,id,reference_catalog_id,reference_revision_id,futures_series_code),
 FOREIGN KEY(catalog_id,swap_instrument_id) REFERENCES trading.swap_instruments(catalog_id,instrument_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_report_dependencies(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,model_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 curve_code ref_data.code NOT NULL,contract_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,report_id,model_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,report_id,model_id,definition_revision_id) REFERENCES automation.pilot_report_models(catalog_id,report_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE TABLE automation.pilot_report_positions(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),trade_id uuid NOT NULL,
 future_revision integer,swap_revision integer,book_id uuid NOT NULL,binding_id uuid NOT NULL,instrument_id uuid NOT NULL,publication_order integer NOT NULL,
 valuation_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 quantity ref_data.exact_number NOT NULL CHECK(quantity<>0),quantity_code ref_data.code NOT NULL,
 measure text GENERATED ALWAYS AS(CASE WHEN future_revision IS NOT NULL THEN 'futures_execution_pnl' ELSE 'swap_present_value' END) STORED,
 PRIMARY KEY(catalog_id,report_id,ordinal),UNIQUE(catalog_id,report_id,trade_id,binding_id),CHECK(num_nonnulls(future_revision,swap_revision)=1),
 FOREIGN KEY(catalog_id,report_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES automation.pilot_reports(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,report_id,valuation_id) REFERENCES automation.pilot_report_models(catalog_id,report_id,id),
 FOREIGN KEY(catalog_id,report_id,valuation_id,quantity_code) REFERENCES automation.pilot_report_models(catalog_id,report_id,id,quantity_code),
 FOREIGN KEY(catalog_id,trade_id,future_revision,book_id,binding_id,instrument_id,publication_order) REFERENCES trading.trade_revisions(catalog_id,trade_id,revision,book_id,binding_id,instrument_id,publication_order),
 FOREIGN KEY(catalog_id,trade_id,future_revision) REFERENCES trading.futures_revision_markets(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,trade_id,swap_revision,book_id,binding_id,instrument_id,publication_order) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision,book_id,binding_id,instrument_id,publication_order),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_report_factors(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,series_id uuid NOT NULL,observation_id uuid NOT NULL,
 input_use text NOT NULL CHECK(input_use='variable'),contract_id uuid NOT NULL,quote_code ref_data.code NOT NULL,
 currency_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 role text NOT NULL CHECK(role IN('trader_mark','daily_settlement')),base_value ref_data.exact_number NOT NULL,
 bump_size ref_data.exact_number NOT NULL CHECK(bump_size>0),trading_tick ref_data.exact_number NOT NULL CHECK(trading_tick>0),
 PRIMARY KEY(catalog_id,report_id,ordinal),UNIQUE(catalog_id,report_id,series_id,input_use),
 FOREIGN KEY(catalog_id,report_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES automation.pilot_reports(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,report_id,observation_id,input_use) REFERENCES market_data.market_close_pins(catalog_id,close_id,observation_id,input_use),
 FOREIGN KEY(catalog_id,observation_id,definition_revision_id,series_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id,series_id),
 FOREIGN KEY(catalog_id,observation_id,definition_revision_id,series_id,source_code,role,base_value) REFERENCES market_data.observations(catalog_id,id,definition_revision_id,series_id,source_code,role,value),
 FOREIGN KEY(catalog_id,definition_revision_id,series_id,contract_id,quote_code) REFERENCES market_data.series(catalog_id,revision_id,id,contract_id,quote_code),
 FOREIGN KEY(catalog_id,definition_revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code,currency_code,quantity_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code,currency_code,quantity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_report_totals(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 measure text NOT NULL CHECK(measure IN('futures_execution_pnl','swap_present_value')),book_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 numerator text NOT NULL CHECK(length(numerator)<=4096 AND numerator~'^-?[0-9]+$'),denominator text NOT NULL CHECK(length(denominator)<=4096 AND denominator~'^[1-9][0-9]*$'),
 PRIMARY KEY(catalog_id,report_id,measure,book_id,currency_code),
 FOREIGN KEY(catalog_id,report_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES automation.pilot_reports(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_report_risk(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,measure text NOT NULL,book_id uuid NOT NULL,currency_code ref_data.code NOT NULL,factor_ordinal integer NOT NULL,
 up_n text NOT NULL,up_d text NOT NULL,down_n text NOT NULL,down_d text NOT NULL,up_pnl_n text NOT NULL,up_pnl_d text NOT NULL,
 down_pnl_n text NOT NULL,down_pnl_d text NOT NULL,delta_n text NOT NULL,delta_d text NOT NULL,gamma_n text NOT NULL,gamma_d text NOT NULL,
 PRIMARY KEY(catalog_id,report_id,measure,book_id,currency_code,factor_ordinal),
 FOREIGN KEY(catalog_id,report_id,measure,book_id,currency_code) REFERENCES automation.pilot_report_totals(catalog_id,report_id,measure,book_id,currency_code),
 FOREIGN KEY(catalog_id,report_id,factor_ordinal) REFERENCES automation.pilot_report_factors(catalog_id,report_id,ordinal)
);
CREATE FUNCTION automation.exact_report_numbers(numerators text[],denominators text[]) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT cardinality(numerators)=cardinality(denominators) AND NOT EXISTS(SELECT 1 FROM unnest(numerators) x WHERE x IS NULL OR length(x)>4096 OR x!~'^-?[0-9]+$')
 AND NOT EXISTS(SELECT 1 FROM unnest(denominators) x WHERE x IS NULL OR length(x)>4096 OR x!~'^[1-9][0-9]*$');
$$;
ALTER TABLE automation.pilot_report_risk ADD CHECK(automation.exact_report_numbers(ARRAY[up_n,down_n,up_pnl_n,down_pnl_n,delta_n,gamma_n],ARRAY[up_d,down_d,up_pnl_d,down_pnl_d,delta_d,gamma_d]));
CREATE TABLE automation.pilot_report_discounts(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),instrument_id uuid NOT NULL,fixed_side_id uuid,floating_side_id uuid,
 payment_ordinal integer NOT NULL CHECK(payment_ordinal>=0),payment_at ref_data.instant NOT NULL,payment_date date NOT NULL,enabled boolean NOT NULL,
 factor ref_data.exact_number NOT NULL CHECK(factor>0),mapping_id uuid,curve_code ref_data.code,
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 rate_policy text,interpolation_policy text,
 PRIMARY KEY(catalog_id,report_id,ordinal),CHECK(num_nonnulls(fixed_side_id,floating_side_id)=1),
 CHECK((enabled AND mapping_id IS NOT NULL AND curve_code IS NOT NULL AND rate_policy IS NOT NULL AND interpolation_policy IS NOT NULL) OR
       (NOT enabled AND mapping_id IS NULL AND curve_code IS NULL AND rate_policy IS NULL AND interpolation_policy IS NULL AND factor=1)),
 FOREIGN KEY(catalog_id,report_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES automation.pilot_reports(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,report_id,instrument_id) REFERENCES automation.pilot_report_models(catalog_id,report_id,id),
 FOREIGN KEY(catalog_id,instrument_id,fixed_side_id) REFERENCES trading.swap_instruments(catalog_id,instrument_id,fixed_side_id),
 FOREIGN KEY(catalog_id,instrument_id,floating_side_id) REFERENCES trading.swap_instruments(catalog_id,instrument_id,floating_side_id),
 FOREIGN KEY(catalog_id,mapping_id,definition_revision_id,reference_catalog_id,reference_revision_id,currency_code,curve_code) REFERENCES trading.currency_discount_defaults(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,currency_code,curve_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_report_discount_observations(
 catalog_id uuid NOT NULL,report_id uuid NOT NULL,discount_ordinal integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),observation_id uuid NOT NULL,
 input_use text NOT NULL CHECK(input_use IN('variable','frozen')),maturity date NOT NULL,annual_rate text NOT NULL,discount_factor text NOT NULL,days integer NOT NULL,
 PRIMARY KEY(catalog_id,report_id,discount_ordinal,ordinal),
 FOREIGN KEY(catalog_id,report_id,discount_ordinal) REFERENCES automation.pilot_report_discounts(catalog_id,report_id,ordinal),
 FOREIGN KEY(catalog_id,report_id,observation_id,input_use) REFERENCES market_data.market_close_pins(catalog_id,close_id,observation_id,input_use)
);
CREATE TABLE automation.pilot_report_receipts(
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES automation.pilot_reports(catalog_id,publication_order,publication_xid)
);
CREATE FUNCTION security.pilot_report_sources_visible(catalog uuid,report uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.has_permission('ReadTrades') AND NOT EXISTS(
 SELECT 1 FROM(
 SELECT p.catalog_id,p.publication_order,coalesce(f.effective_at,w.effective_at) AS effective_at
 FROM automation.pilot_report_positions p
 LEFT JOIN trading.trade_revisions f ON(f.catalog_id,f.trade_id,f.revision)=(p.catalog_id,p.trade_id,p.future_revision)
 LEFT JOIN trading.swap_trade_revisions w ON(w.catalog_id,w.trade_id,w.revision)=(p.catalog_id,p.trade_id,p.swap_revision)
 WHERE(p.catalog_id,p.report_id)=(s.catalog_id,s.id)
 UNION SELECT d.catalog_id,m.publication_order,m.effective_at FROM automation.pilot_report_discounts d
 JOIN trading.currency_discount_defaults m ON(m.catalog_id,m.id)=(d.catalog_id,d.mapping_id) WHERE(d.catalog_id,d.report_id)=(s.catalog_id,s.id)
 UNION SELECT b.catalog_id,b.publication_order,b.effective_at FROM automation.pilot_report_positions p
 JOIN trading.books b ON(b.catalog_id,b.id)=(p.catalog_id,p.book_id) WHERE(p.catalog_id,p.report_id)=(s.catalog_id,s.id)) p
 WHERE p.effective_at>c.at OR NOT EXISTS(SELECT 1 FROM security.request_contexts q
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND(q.market_context_id IS NULL OR EXISTS(
 SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id) AND p.publication_order<=x.known_through AND k.known_from<=t.known_at))))
 FROM automation.pilot_reports s JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(s.catalog_id,s.close_id) WHERE(s.catalog_id,s.id)=(catalog,report);
$$;
CREATE FUNCTION security.pilot_report_visible(catalog uuid,report uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND security.has_permission('ReadTrades')
 AND security.has_permission('RunValuation') AND security.has_permission('RunScenario') AND EXISTS(
 SELECT 1 FROM automation.pilot_reports s JOIN automation.runs r ON r.id=s.run_id JOIN automation.jobs j ON j.id=r.job_id
 JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(s.catalog_id,s.close_id)
 WHERE(s.catalog_id,s.id)=(catalog,report) AND automation.can_view(j.tenant_id,j.workspace_id,j.owner_id)
 AND security.market_close_visible(s.catalog_id,s.close_id) AND security.pilot_report_sources_visible(s.catalog_id,s.id)
 AND NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND(q.market_catalog_id<>catalog OR t.effective_on<>(c.at AT TIME ZONE 'UTC')::date OR NOT EXISTS(SELECT 1 FROM automation.pilot_report_receipts k
 WHERE(k.catalog_id,k.publication_order,k.publication_xid)=(s.catalog_id,s.publication_order,s.publication_xid) AND k.known_from<=t.known_at))));
$$;
CREATE FUNCTION automation.require_pilot_report_view(report uuid,expected_context uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN automation.pilot_reports s ON s.id=report
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND q.expires_at>clock_timestamp() AND q.market_context_id IS NOT DISTINCT FROM expected_context AND security.pilot_report_visible(s.catalog_id,s.id))
 THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
END;$$;
CREATE FUNCTION automation.read_pilot_report(report uuid,expected_context uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 PERFORM automation.require_pilot_report_view(report,expected_context);
 -- One MVCC statement, fixed rowsets, no dynamic relation names. The definer
 -- avoids running the full source graph authority check for every output row.
 SELECT jsonb_build_object(
 'header',to_jsonb(s)||jsonb_build_object('close_name',c.name,'close_at',c.at,'close_kind',c.kind,'effective_on',t.effective_on,'known_through',t.known_through,'known_at',t.known_at),
 'books',coalesce((SELECT jsonb_agg(x ORDER BY x.id) FROM(SELECT DISTINCT b.id,b.name FROM automation.pilot_report_positions p JOIN trading.books b ON(b.catalog_id,b.id)=(p.catalog_id,p.book_id) WHERE(p.catalog_id,p.report_id)=(s.catalog_id,s.id))x),'[]'::jsonb),
 'positions',coalesce((SELECT jsonb_agg(x ORDER BY x.ordinal) FROM automation.pilot_report_positions x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'totals',coalesce((SELECT jsonb_agg(to_jsonb(x)||jsonb_build_object('currency_label',m.display_code) ORDER BY x.measure,x.book_id,x.currency_code) FROM automation.pilot_report_totals x JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(x.reference_catalog_id,x.reference_revision_id,x.currency_code) WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'factors',coalesce((SELECT jsonb_agg(to_jsonb(f)||jsonb_build_object('series_code',z.code,'delivery_from',d.delivery_from,'delivery_to',d.delivery_to,'unit_label',u.symbol,'currency_label',m.display_code,'multiplier',q.multiplier::text) ORDER BY f.ordinal)
 FROM automation.pilot_report_factors f JOIN market_data.series z ON(z.catalog_id,z.revision_id,z.id)=(f.catalog_id,f.definition_revision_id,f.series_id)
 JOIN market_data.contracts d ON(d.catalog_id,d.revision_id,d.id)=(f.catalog_id,f.definition_revision_id,f.contract_id)
 JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(f.reference_catalog_id,f.reference_revision_id,f.quote_code)
 JOIN ref_data.quantity_specifications v ON(v.catalog_id,v.revision_id,v.code)=(f.reference_catalog_id,f.reference_revision_id,f.quantity_code)
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(v.catalog_id,v.revision_id,v.unit_code)
 JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(f.reference_catalog_id,f.reference_revision_id,f.currency_code)
 WHERE(f.catalog_id,f.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'risk',coalesce((SELECT jsonb_agg(to_jsonb(x)||jsonb_build_object('currency_label',m.display_code) ORDER BY x.measure,x.book_id,x.currency_code,x.factor_ordinal)
 FROM automation.pilot_report_risk x JOIN ref_data.currencies m ON(m.catalog_id,m.revision_id,m.code)=(s.reference_catalog_id,s.reference_revision_id,x.currency_code) WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'models',coalesce((SELECT jsonb_agg(x ORDER BY x.id) FROM automation.pilot_report_models x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'dependencies',coalesce((SELECT jsonb_agg(x ORDER BY x.model_id,x.curve_code,x.contract_id) FROM automation.pilot_report_dependencies x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'discounts',coalesce((SELECT jsonb_agg(x ORDER BY x.ordinal) FROM automation.pilot_report_discounts x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb),
 'discountObservations',coalesce((SELECT jsonb_agg(x ORDER BY x.discount_ordinal,x.ordinal) FROM automation.pilot_report_discount_observations x WHERE(x.catalog_id,x.report_id)=(s.catalog_id,s.id)),'[]'::jsonb))
 INTO result FROM automation.pilot_reports s JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(s.catalog_id,s.close_id)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(s.catalog_id,s.context_id) WHERE s.id=report;
 PERFORM automation.require_pilot_report_view(report,expected_context);
 RETURN result;
END;$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['pilot_reports','pilot_report_models','pilot_report_dependencies','pilot_report_positions','pilot_report_factors','pilot_report_totals','pilot_report_risk','pilot_report_discounts','pilot_report_discount_observations','pilot_report_receipts'] LOOP
  EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_report BEFORE UPDATE OR DELETE ON automation.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('GRANT SELECT ON automation.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON automation.%I TO openetrm_security_owner',t);
  IF t='pilot_reports' THEN
   EXECUTE format('CREATE POLICY scoped_read ON automation.%I FOR SELECT TO openetrm_runtime USING(security.pilot_report_visible(catalog_id,id))',t);
  ELSIF t='pilot_report_receipts' THEN
   EXECUTE format('CREATE POLICY scoped_read ON automation.%I FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM automation.pilot_reports s WHERE(s.catalog_id,s.publication_order)=(pilot_report_receipts.catalog_id,pilot_report_receipts.publication_order)))',t);
  ELSE
   EXECUTE format('CREATE POLICY scoped_read ON automation.%I FOR SELECT TO openetrm_runtime USING(security.pilot_report_visible(catalog_id,report_id))',t);
  END IF;
 END LOOP;
END$$;
CREATE FUNCTION automation.pilot_report_exists(expected_run uuid) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;found_report boolean;
BEGIN
 r=automation.lock_pilot_run();
 IF r.id IS DISTINCT FROM expected_run THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM automation.pilot_reports s WHERE s.id=r.id AND NOT security.market_close_visible(s.catalog_id,s.close_id))
 THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
 SELECT EXISTS(SELECT 1 FROM automation.pilot_reports s WHERE s.id=r.id) INTO found_report;
 r=automation.lock_pilot_run();
 RETURN found_report;
END;$$;
CREATE FUNCTION automation.publish_pilot_report(expected_run uuid,payload jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;p automation.pilot_close_profiles;c market_data.market_closes;q security.request_contexts;
 previous automation.pilot_reports;ordinal integer;digest text;
BEGIN
 r=automation.lock_pilot_run();PERFORM pg_advisory_xact_lock(730849102);r=automation.lock_pilot_run();
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version)=(j.pilot_profile_id,j.pilot_profile_version);
 SELECT * INTO c FROM market_data.market_closes WHERE(catalog_id,id,run_id,kind)=(p.catalog_id,r.id,r.id,'generated_pilot');
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF r.id IS DISTINCT FROM expected_run OR c.id IS NULL OR(payload->>'snapshotId')::uuid IS DISTINCT FROM r.id
 OR(payload->>'contextId')::uuid IS DISTINCT FROM q.market_context_id OR payload->'policy' IS DISTINCT FROM p.valuation_policy
 OR jsonb_typeof(payload->'models') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'models') NOT BETWEEN 1 AND 20000
 OR jsonb_typeof(payload->'positions') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'positions') NOT BETWEEN 1 AND 100000
 OR jsonb_typeof(payload->'factors') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'factors')>4096
 OR jsonb_typeof(payload->'totals') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'totals') NOT BETWEEN 1 AND 256
 OR jsonb_typeof(payload->'risk') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'risk')>1048576
 OR jsonb_typeof(payload->'discounts') IS DISTINCT FROM 'array' OR jsonb_array_length(payload->'discounts')>40000
 THEN RAISE EXCEPTION 'pilot_report_input' USING ERRCODE='P0001';END IF;
 PERFORM security.require_bound_market_context(c.catalog_id,q.market_context_id,c.at);
 IF NOT security.market_close_visible(c.catalog_id,c.id) THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
 digest=encode(sha256(convert_to(payload::text,'UTF8')),'hex');
 SELECT * INTO previous FROM automation.pilot_reports WHERE id=r.id;
 IF FOUND THEN
  IF previous.request_sha256<>digest THEN RAISE EXCEPTION 'pilot_report_conflict' USING ERRCODE='P0001';END IF;
  r=automation.lock_pilot_run();PERFORM security.require_bound_market_context(c.catalog_id,q.market_context_id,c.at);
  IF NOT security.market_close_visible(c.catalog_id,c.id) THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
  RETURN r.id;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM automation.pilot_reports WHERE catalog_id=c.catalog_id;
 INSERT INTO automation.pilot_reports VALUES(c.catalog_id,r.id,r.id,r.id,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,q.market_context_id,digest,payload->'policy',payload->'work',q.actor_id,ordinal,pg_current_xact_id());
 INSERT INTO automation.pilot_report_models SELECT c.catalog_id,r.id,x.id,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.model,x.quantity,x.currency,x.future,x.swap,t.futures_series_code
 FROM jsonb_to_recordset(payload->'models')x(id uuid,model text,quantity text,currency text,future uuid,swap uuid)
 LEFT JOIN market_data.contracts t ON(t.catalog_id,t.revision_id,t.id)=(c.catalog_id,c.definition_revision_id,x.future);
 INSERT INTO automation.pilot_report_dependencies SELECT c.catalog_id,r.id,x.model,c.definition_revision_id,x.curve,x.contract
 FROM jsonb_to_recordset(payload->'dependencies')x(model uuid,curve text,contract uuid);
 INSERT INTO automation.pilot_report_positions(catalog_id,report_id,ordinal,trade_id,future_revision,swap_revision,book_id,binding_id,instrument_id,publication_order,valuation_id,definition_revision_id,reference_catalog_id,reference_revision_id,quantity,quantity_code)
 SELECT c.catalog_id,r.id,x.ordinal,x.trade,x.future,x.swap,x.book,x.binding,x.instrument,x.publication,x.valuation,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.quantity,x.unit
 FROM jsonb_to_recordset(payload->'positions')x(ordinal integer,trade uuid,future integer,swap integer,book uuid,binding uuid,instrument uuid,publication integer,valuation uuid,quantity numeric,unit text);
 INSERT INTO automation.pilot_report_factors SELECT c.catalog_id,r.id,x.ordinal,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.series,x.observation,'variable',x.contract,x.quote,x.currency,x.quantity,x.source,x.role,x.base,x.bump,x.tick
 FROM jsonb_to_recordset(payload->'factors')x(ordinal integer,series uuid,observation uuid,contract uuid,quote text,currency text,quantity text,source text,role text,base numeric,bump numeric,tick numeric);
 INSERT INTO automation.pilot_report_totals SELECT c.catalog_id,r.id,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.measure,x.book,x.currency,x.n,x.d
 FROM jsonb_to_recordset(payload->'totals')x(measure text,book uuid,currency text,n text,d text);
 INSERT INTO automation.pilot_report_risk SELECT c.catalog_id,r.id,x.measure,x.book,x.currency,x.factor,x.up_n,x.up_d,x.down_n,x.down_d,x.up_pnl_n,x.up_pnl_d,x.down_pnl_n,x.down_pnl_d,x.delta_n,x.delta_d,x.gamma_n,x.gamma_d
 FROM jsonb_to_recordset(payload->'risk')x(measure text,book uuid,currency text,factor integer,up_n text,up_d text,down_n text,down_d text,up_pnl_n text,up_pnl_d text,down_pnl_n text,down_pnl_d text,delta_n text,delta_d text,gamma_n text,gamma_d text);
 INSERT INTO automation.pilot_report_discounts SELECT c.catalog_id,r.id,x.ordinal,x.instrument,x.fixed,x.floating,x.payment_ordinal,x.payment_at,x.payment_date,x.enabled,x.factor,x.mapping,x.curve,c.definition_revision_id,c.reference_catalog_id,c.reference_revision_id,x.currency,x.rate_policy,x.interpolation_policy
 FROM jsonb_to_recordset(payload->'discounts')x(ordinal integer,instrument uuid,fixed uuid,floating uuid,payment_ordinal integer,payment_at timestamptz,payment_date date,enabled boolean,factor numeric,mapping uuid,curve text,currency text,rate_policy text,interpolation_policy text);
 INSERT INTO automation.pilot_report_discount_observations SELECT c.catalog_id,r.id,x.discount,x.ordinal,x.observation,x.input_use,x.maturity,x.annual_rate,x.factor,x.days
 FROM jsonb_to_recordset(payload->'discountObservations')x(discount integer,ordinal integer,observation uuid,input_use text,maturity date,annual_rate text,factor text,days integer);
 IF EXISTS(SELECT 1 FROM automation.pilot_report_positions x WHERE x.report_id=r.id AND NOT EXISTS(SELECT 1 FROM automation.pilot_close_books b WHERE(b.profile_id,b.profile_version,b.book_id)=(p.id,p.version,x.book_id)))
 OR EXISTS(SELECT 1 FROM automation.pilot_report_totals x WHERE x.report_id=r.id AND NOT EXISTS(SELECT 1 FROM automation.pilot_close_books b WHERE(b.profile_id,b.profile_version,b.book_id)=(p.id,p.version,x.book_id)))
 THEN RAISE EXCEPTION 'pilot_report_books' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM automation.pilot_report_positions x JOIN automation.pilot_report_models m ON(m.catalog_id,m.report_id,m.id)=(x.catalog_id,x.report_id,x.valuation_id)
 WHERE x.report_id=r.id AND(m.quantity_code<>x.quantity_code OR
 (x.future_revision IS NOT NULL AND(m.future_contract_id IS NULL OR NOT EXISTS(SELECT 1 FROM trading.futures_revision_markets f
 WHERE(f.catalog_id,f.trade_id,f.revision,f.instrument_id)=(x.catalog_id,x.trade_id,x.future_revision,x.instrument_id) AND(f.definition_revision_id,f.contract_id)=(m.definition_revision_id,m.future_contract_id)))) OR
 (x.swap_revision IS NOT NULL AND m.swap_instrument_id IS DISTINCT FROM x.instrument_id)))
 THEN RAISE EXCEPTION 'pilot_report_model' USING ERRCODE='P0001';END IF;
 IF security.pilot_report_sources_visible(c.catalog_id,r.id) IS NOT TRUE
 THEN RAISE EXCEPTION 'pilot_report_denied' USING ERRCODE='42501';END IF;
 r=automation.lock_pilot_run();PERFORM security.require_bound_market_context(c.catalog_id,q.market_context_id,c.at);
 RETURN r.id;
END;$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_pilot_report_context;
REVOKE ALL ON FUNCTION security.capture_pre_pilot_report_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM automation.pilot_report_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT s.* FROM automation.pilot_reports s LEFT JOIN automation.pilot_report_receipts r USING(catalog_id,publication_order)
 WHERE s.catalog_id=catalog AND r.publication_order IS NULL ORDER BY s.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO automation.pilot_report_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_pilot_report_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;
ALTER FUNCTION automation.finish_run(uuid,bigint,uuid,boolean,text,timestamptz) RENAME TO finish_pre_pilot_report;
REVOKE ALL ON FUNCTION automation.finish_pre_pilot_report(uuid,bigint,uuid,boolean,text,timestamptz) FROM openetrm_scheduler;
-- Preserve the legacy body with its explicit parameter labels after renaming.
CREATE OR REPLACE FUNCTION automation.finish_pre_pilot_report(run_id uuid,expected_fence bigint,worker_id uuid,succeeded boolean,error_code text,next_due_at timestamptz) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;
BEGIN
 w=automation.worker();n=clock_timestamp();
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run_id FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run_id FOR UPDATE;
 w=automation.worker();n=clock_timestamp();
 IF r.id IS NULL OR(w.tenant_id,w.workspace_id)<>(r.tenant_id,r.workspace_id) OR r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM finish_pre_pilot_report.worker_id
 OR r.fence IS DISTINCT FROM expected_fence OR r.lease_until<=n OR r.deadline_at<=n OR r.state<>'running' OR j.state NOT IN('active','paused') OR j.expires_at<=n OR NOT automation.authorized(j)
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF succeeded IS NULL OR(succeeded AND error_code IS NOT NULL) OR(NOT succeeded AND(error_code IS NULL OR error_code!~'^[a-z][a-z0-9_]{0,63}$')) THEN RAISE EXCEPTION 'job_input';END IF;
 IF succeeded AND j.interval_seconds IS NOT NULL AND(next_due_at IS NULL OR NOT isfinite(next_due_at)
 OR next_due_at<=n OR next_due_at>n+make_interval(secs=>j.interval_seconds)
 OR mod(extract(epoch FROM(next_due_at-j.next_due_at)),j.interval_seconds)<>0) THEN RAISE EXCEPTION 'job_clock';END IF;
 IF j.interval_seconds IS NULL AND next_due_at IS NOT NULL THEN RAISE EXCEPTION 'job_input';END IF;
 UPDATE automation.runs SET state=CASE WHEN succeeded THEN 'succeeded' ELSE 'failed' END,finished_at=n,error_code=finish_pre_pilot_report.error_code WHERE id=run_id;
 IF NOT succeeded THEN UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;
 ELSIF j.interval_seconds IS NULL THEN UPDATE automation.jobs SET state='completed',version=version+1 WHERE id=j.id;
 ELSE UPDATE automation.jobs SET next_due_at=finish_pre_pilot_report.next_due_at,version=version+1 WHERE id=j.id;END IF;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,CASE WHEN succeeded THEN 'succeeded' ELSE 'failed' END,n,r.fence);
END $$;
CREATE FUNCTION automation.finish_run(run_id uuid,expected_fence bigint,worker_id uuid,succeeded boolean,error_code text,next_due_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;
BEGIN
 PERFORM automation.finish_pre_pilot_report(run_id,expected_fence,worker_id,succeeded,error_code,next_due_at);
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=finish_run.run_id;
 IF j.operation='generated_pilot_close_v1' THEN
  w=automation.worker();
  SELECT * INTO r FROM automation.runs WHERE id=finish_run.run_id;
  IF r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM finish_run.worker_id OR r.fence IS DISTINCT FROM expected_fence
  OR r.lease_until<=clock_timestamp() OR r.deadline_at<=clock_timestamp() OR j.expires_at<=clock_timestamp()
  OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,w.principal_id)
  THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
  IF succeeded AND NOT EXISTS(SELECT 1 FROM automation.pilot_reports s JOIN market_data.market_closes c ON(c.catalog_id,c.id,c.run_id)=(s.catalog_id,s.close_id,s.run_id)
   WHERE s.id=finish_run.run_id AND c.kind='generated_pilot') THEN RAISE EXCEPTION 'pilot_report_required' USING ERRCODE='42501';END IF;
 END IF;
END;$$;
ALTER FUNCTION automation.description(automation.jobs) RENAME TO description_pre_pilot_report;
REVOKE ALL ON FUNCTION automation.description_pre_pilot_report(automation.jobs) FROM openetrm_runtime,openetrm_scheduler;
CREATE FUNCTION automation.description(j automation.jobs) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT automation.description_pre_pilot_report(j)||jsonb_build_object('pilotReport',
 (SELECT jsonb_build_object('runId',s.run_id,'state','report_published') FROM automation.pilot_reports s JOIN automation.runs r ON r.id=s.run_id
 WHERE r.job_id=j.id AND security.pilot_report_visible(s.catalog_id,s.id) ORDER BY r.due_at DESC,r.id LIMIT 1));
$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname,n.nspname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE(n.nspname='security' AND p.proname IN('pilot_report_visible','pilot_report_sources_visible','capture_date_context')) OR(n.nspname='automation' AND p.proname IN('publish_pilot_report','pilot_report_exists','read_pilot_report','require_pilot_report_view','finish_run','description')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname='finish_run' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_scheduler',f.signature);
  ELSIF f.proname NOT IN('description','pilot_report_sources_visible') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
