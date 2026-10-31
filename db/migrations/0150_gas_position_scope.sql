-- A minimal position inventory for both interactive and leased risk readers.
-- Gas scenario valuation remains unsupported; these facts identify exact exclusions.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
CREATE FUNCTION security.gas_position_scope(catalog uuid,books uuid[],portfolios uuid[]) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE result jsonb;context uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(session_user='openetrm_risk_worker' AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'No gas position authority' USING ERRCODE='42501';END IF;
 IF books IS NULL OR portfolios IS NULL OR cardinality(books)>128 OR cardinality(portfolios)>128
 OR array_position(books,NULL) IS NOT NULL OR array_position(portfolios,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'gas_position_scope' USING ERRCODE='P0001';END IF;
 SELECT c.market_context_id INTO context FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN trading.contexts h ON(h.catalog_id,h.context_id)=(t.catalog_id,t.id)
 WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 PERFORM security.require_trading_history(catalog,context);
 WITH heads AS(
  SELECT DISTINCT ON(r.trade_id) r.* FROM trading.gas_trade_revisions r
  WHERE r.catalog_id=catalog AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
  ORDER BY r.trade_id,r.revision DESC
 )
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',r.trade_id,'binding_id',r.instrument_id,'book_id',r.book_id,'trade_reference',tr.reference_number) ORDER BY tr.reference_number,r.instrument_id),'[]'::jsonb)
 INTO result FROM heads r
 JOIN trading.gas_instruments i USING(catalog_id,instrument_id)
 JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
 JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
 JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
 WHERE(cardinality(books)=0 OR r.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
 AND security.trading_visible(i.catalog_id,i.publication_order,i.effective_at)
 AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at)
 AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)
 AND(i.composition AND security.gas_component_sources_visible(catalog,i.instrument_id) OR NOT i.composition AND EXISTS(
  SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
  AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_access(a.reference_catalog_id,'ReadMarketData')
  AND security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) AND security.gas_setup_visible(a.catalog_id,a.setup_id)));
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(session_user='openetrm_risk_worker' AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'Gas position authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.gas_position_scope(uuid,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_position_scope(uuid,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_position_scope(uuid,uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
