-- Bounded batch quantity reads preserve current authority and certified cutoffs.
BEGIN;
CREATE INDEX gas_quantity_commands_trade ON trading.gas_quantity_commands(catalog_id,trade_id);
CREATE INDEX gas_quantity_history ON trading.gas_quantity_revisions(catalog_id,series_id,publication_order DESC);
CREATE FUNCTION security.read_gas_quantity_rows(catalog uuid,trade uuid,mode text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE q security.request_contexts;t market_data.temporal_contexts;head integer;instrument uuid;result jsonb;
BEGIN
 -- No unbound/current-write shortcut is admitted by this read adapter.
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF q.market_catalog_id IS DISTINCT FROM catalog OR q.market_context_id IS NULL OR q.trading_as_of IS NULL OR mode IS NULL OR mode NOT IN('heads','history') THEN RAISE EXCEPTION 'gas_quantity_context';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 SELECT * INTO STRICT t FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,q.market_context_id);
 SELECT known_through INTO STRICT head FROM trading.contexts WHERE(catalog_id,context_id)=(catalog,t.id);
 SELECT r.instrument_id INTO STRICT instrument FROM trading.gas_trade_revisions r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
 WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND r.publication_order<=head AND k.known_from<=t.known_at AND r.effective_at<=q.trading_as_of;
 IF mode='heads' THEN
  SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.obligation_id,x.kind,x.day),'[]'::jsonb) INTO result FROM(
   SELECT r.*,s.obligation_id,s.kind,s.stage,s.source
   FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
   JOIN trading.gas_quantity_series s ON(s.catalog_id,s.obligation_id)=(o.catalog_id,o.id)
   JOIN trading.knowledge_receipts sk ON(sk.catalog_id,sk.publication_order)=(s.catalog_id,s.publication_order)
   CROSS JOIN LATERAL generate_series(0,m.delivery_to-m.delivery_from-1) d(n)
   CROSS JOIN LATERAL(SELECT r.* FROM trading.gas_quantity_revisions r
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
    WHERE(r.catalog_id,r.series_id,r.day)=(s.catalog_id,s.id,m.delivery_from+d.n)
    AND r.publication_order<=head AND k.known_from<=t.known_at AND r.effective_at<=q.trading_as_of
    ORDER BY r.revision DESC LIMIT 1) r
   WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,instrument)
   AND s.publication_order<=head AND sk.known_from<=t.known_at AND s.effective_at<=q.trading_as_of
  )x;
 ELSE
  SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.publication_order DESC),'[]'::jsonb) INTO result FROM(
   SELECT r.*,s.kind,o.id obligation_id,m.code side_code
   FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
   JOIN trading.gas_quantity_series s ON(s.catalog_id,s.obligation_id)=(o.catalog_id,o.id)
   JOIN trading.knowledge_receipts sk ON(sk.catalog_id,sk.publication_order)=(s.catalog_id,s.publication_order)
   CROSS JOIN LATERAL(SELECT r.* FROM trading.gas_quantity_revisions r
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
    WHERE(r.catalog_id,r.series_id)=(s.catalog_id,s.id) AND r.publication_order<=head AND k.known_from<=t.known_at AND r.effective_at<=q.trading_as_of
    ORDER BY r.publication_order DESC LIMIT 100) r
   WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,instrument)
   AND s.publication_order<=head AND sk.known_from<=t.known_at AND s.effective_at<=q.trading_as_of
   ORDER BY r.publication_order DESC LIMIT 100
  )x;
 END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 RETURN result;
END$$;
ALTER FUNCTION security.read_gas_quantity_rows(uuid,uuid,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_quantity_rows(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_quantity_rows(uuid,uuid,text) TO openetrm_runtime;
COMMIT;
