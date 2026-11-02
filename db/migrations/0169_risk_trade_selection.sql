-- Trade subsets use the same complete-source and current-authority checks as whole books.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE OR REPLACE FUNCTION security.gas_position_scope(catalog uuid,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE result jsonb;context uuid;complete boolean;
BEGIN
 IF trades IS NOT NULL AND(cardinality(trades) NOT BETWEEN 1 AND 256 OR array_position(trades,NULL) IS NOT NULL
 OR '00000000-0000-0000-0000-000000000000'::uuid=ANY(trades) OR(SELECT count(DISTINCT id) FROM unnest(trades) id)<>cardinality(trades))
 THEN RAISE EXCEPTION 'risk_trade_scope' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
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
 ), scoped AS(
  SELECT r.* FROM heads r
  JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
  JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(trades IS NULL OR r.trade_id=ANY(trades)) AND(cardinality(books)=0 OR r.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
  AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at)
  AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)
 )
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',r.trade_id,'binding_id',r.instrument_id,'book_id',r.book_id,'trade_reference',tr.reference_number) ORDER BY tr.reference_number,r.instrument_id),'[]'::jsonb),
  coalesce(bool_and(i.instrument_id IS NOT NULL AND tr.reference_number IS NOT NULL AND
   security.trading_visible(i.catalog_id,i.publication_order,i.effective_at) AND
   (i.composition AND security.gas_component_sources_visible(catalog,i.instrument_id) OR NOT i.composition AND
    a.code IS NOT NULL AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_access(a.reference_catalog_id,'ReadMarketData') AND
    security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) AND security.gas_setup_visible(a.catalog_id,a.setup_id))),true)
 INTO result,complete FROM scoped r
 LEFT JOIN trading.gas_instruments i USING(catalog_id,instrument_id)
 LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
 LEFT JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code);
 IF complete IS NOT TRUE THEN RAISE EXCEPTION 'No complete visible gas position sources' USING ERRCODE='42501';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'Gas position authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.gas_position_scope(uuid,uuid[],uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_position_scope(uuid,uuid[],uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_position_scope(uuid,uuid[],uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION security.gas_position_scope(catalog uuid,books uuid[],portfolios uuid[]) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
 SELECT security.gas_position_scope(catalog,books,portfolios,NULL::uuid[])
$$;

CREATE FUNCTION security.crude_position_scope(catalog uuid,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE result jsonb;context uuid;complete boolean;
BEGIN
 IF trades IS NOT NULL AND(cardinality(trades) NOT BETWEEN 1 AND 256 OR array_position(trades,NULL) IS NOT NULL
 OR '00000000-0000-0000-0000-000000000000'::uuid=ANY(trades) OR(SELECT count(DISTINCT id) FROM unnest(trades) id)<>cardinality(trades))
 THEN RAISE EXCEPTION 'risk_trade_scope' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'No crude position authority' USING ERRCODE='42501';END IF;
 IF books IS NULL OR portfolios IS NULL OR cardinality(books)>128 OR cardinality(portfolios)>128
 OR array_position(books,NULL) IS NOT NULL OR array_position(portfolios,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'crude_position_scope';END IF;
 SELECT c.market_context_id INTO context FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN trading.contexts h ON(h.catalog_id,h.context_id)=(t.catalog_id,t.id)
 WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid()
 AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);

 WITH heads AS(
  SELECT DISTINCT ON(r.trade_id) r.* FROM trading.crude_trade_revisions r
  WHERE r.catalog_id=catalog AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
  ORDER BY r.trade_id,r.revision DESC
 ), scoped AS(
  SELECT r.* FROM heads r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
  JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(trades IS NULL OR r.trade_id=ANY(trades)) AND(cardinality(books)=0 OR r.book_id=ANY(books))
  AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
  AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at)
  AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)
 )
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',r.trade_id,'binding_id',r.instrument_id,
  'instrument_id',r.instrument_id,'definition_revision_id',i.definition_revision_id,'book_id',r.book_id,
  'revision',r.revision,'trade_reference',tr.reference_number) ORDER BY tr.reference_number,r.instrument_id),'[]'::jsonb),
  coalesce(bool_and(i.instrument_id IS NOT NULL AND tr.reference_number IS NOT NULL AND
   security.trading_visible(i.catalog_id,i.publication_order,i.effective_at) AND
   a.code IS NOT NULL AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND
   security.reference_access(a.reference_catalog_id,'ReadMarketData') AND
   security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id)),true)
 INTO result,complete FROM scoped r
 LEFT JOIN trading.crude_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
 LEFT JOIN market_data.crude_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(i.catalog_id,i.definition_revision_id,i.assignment_code)
 LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id);
 -- Missing or inaccessible retained metadata must never erase a held position.
 IF complete IS NOT TRUE THEN RAISE EXCEPTION 'No complete visible crude position sources' USING ERRCODE='42501';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'Crude position authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.crude_position_scope(uuid,uuid[],uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.crude_position_scope(uuid,uuid[],uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.crude_position_scope(uuid,uuid[],uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION security.crude_position_scope(catalog uuid,books uuid[],portfolios uuid[]) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
 SELECT security.crude_position_scope(catalog,books,portfolios,NULL::uuid[])
$$;

CREATE FUNCTION trading.read_swap_revisions(catalog uuid,trade uuid,latest_only boolean,books uuid[],trades uuid[]) RETURNS SETOF jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET jit=off AS $$
WITH visible_revisions AS MATERIALIZED(
   SELECT r.* FROM trading.swap_trade_revisions r
   WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2) AND($5::uuid[] IS NULL OR r.trade_id=ANY($5::uuid[]))
  ), latest_revisions AS MATERIALIZED(
   SELECT DISTINCT ON(catalog_id,trade_id) * FROM visible_revisions WHERE $3::boolean
   ORDER BY catalog_id,trade_id,revision DESC
  ), selected_revisions AS MATERIALIZED(
   SELECT * FROM visible_revisions WHERE NOT $3::boolean
   UNION ALL
   SELECT * FROM latest_revisions WHERE cardinality($4::uuid[])=0 OR book_id=ANY($4::uuid[])
  ), projected AS MATERIALIZED(
   -- Resolve only the selected exact revisions before projecting their terms.
   SELECT p.* FROM selected_revisions r
   CROSS JOIN LATERAL(SELECT p.* FROM trading.swap_revision_projection p
    WHERE(p.catalog_id,p.trade_id,p.revision,p.binding_id)=
     (r.catalog_id,r.trade_id,r.revision,r.binding_id) OFFSET 0)p
  ), rendered AS MATERIALIZED(
   SELECT to_jsonb(v)||jsonb_build_object('recorded_at',k.known_from) AS payload,r.catalog_id,r.trade_id,r.revision,r.binding_id
   FROM selected_revisions r
   JOIN projected v ON(v.catalog_id,v.trade_id,v.revision,v.binding_id)=(r.catalog_id,r.trade_id,r.revision,r.binding_id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
  )
  SELECT payload::jsonb FROM(
   SELECT payload::text,trade_id,revision FROM rendered v
   UNION ALL
   SELECT json_build_object('__missing_swap_revision',true)::text,NULL::uuid,NULL::integer
  WHERE $3::boolean AND EXISTS(
   SELECT 1 FROM selected_revisions required WHERE NOT EXISTS(
    SELECT 1 FROM rendered actual WHERE(actual.catalog_id,actual.trade_id,actual.revision,actual.binding_id)=
     (required.catalog_id,required.trade_id,required.revision,required.binding_id)))
  )result ORDER BY trade_id,revision
$$;
ALTER FUNCTION trading.read_swap_revisions(uuid,uuid,boolean,uuid[],uuid[]) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.read_swap_revisions(uuid,uuid,boolean,uuid[],uuid[]) SET join_collapse_limit=1;
REVOKE ALL ON FUNCTION trading.read_swap_revisions(uuid,uuid,boolean,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.read_swap_revisions(uuid,uuid,boolean,uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION trading.read_swap_revisions(catalog uuid,trade uuid,latest_only boolean,books uuid[]) RETURNS SETOF jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET jit=off SET join_collapse_limit=1 AS $$
 SELECT * FROM trading.read_swap_revisions(catalog,trade,latest_only,books,NULL::uuid[])
$$;
-- The worker can discover exact PPA trade identities without gaining direct
-- SELECT access to PPA booking rows, terms or quantities. The existing head
-- visibility rules retain the bound history and current authority; every worker
-- call requires its active claim. Reference labels use the caller's RLS.
CREATE FUNCTION security.risk_ppa_trade_heads(catalog uuid,books uuid[],trades uuid[]) RETURNS SETOF jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE context uuid;result jsonb;complete boolean;
BEGIN
 PERFORM risk.require_worker_attempt();
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'No risk trade identity authority' USING ERRCODE='42501';END IF;
 IF books IS NULL OR cardinality(books) NOT BETWEEN 1 AND 128 OR array_position(books,NULL) IS NOT NULL
 OR(SELECT count(DISTINCT id) FROM unnest(books) id)<>cardinality(books)
 OR trades IS NOT NULL AND(cardinality(trades) NOT BETWEEN 1 AND 256 OR array_position(trades,NULL) IS NOT NULL
 OR '00000000-0000-0000-0000-000000000000'::uuid=ANY(trades) OR(SELECT count(DISTINCT id) FROM unnest(trades) id)<>cardinality(trades))
 THEN RAISE EXCEPTION 'risk_trade_scope' USING ERRCODE='P0001';END IF;
 SELECT c.market_context_id INTO context FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN trading.contexts h ON(h.catalog_id,h.context_id)=(t.catalog_id,t.id)
 WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
 AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 PERFORM security.require_trading_history(catalog,context);
 WITH heads AS(
  SELECT DISTINCT ON(r.trade_id) r.* FROM trading.ppa_trade_revisions r
  WHERE r.catalog_id=catalog AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
  ORDER BY r.trade_id,r.revision DESC
 )
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',h.trade_id,'book_id',h.book_id,'revision',h.revision))
  FILTER(WHERE security.reference_access(h.reference_catalog_id,'ReadMarketData')),'[]'::jsonb),
  coalesce(bool_and(security.reference_access(h.reference_catalog_id,'ReadMarketData')),true)
 INTO result,complete
 FROM heads h JOIN trading.books b ON(b.catalog_id,b.id)=(h.catalog_id,h.book_id)
 JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
 WHERE h.book_id=ANY(books) AND(trades IS NULL OR h.trade_id=ANY(trades))
 AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at)
 AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'Risk trade identity authority expired' USING ERRCODE='42501';END IF;
 PERFORM risk.require_worker_attempt();
 RETURN QUERY SELECT value FROM jsonb_array_elements(result);
 -- Preserve opaque scope incompleteness without revealing unauthorized IDs.
 -- Directory readers can still choose authorized trades; whole-book risk refuses.
 IF NOT complete THEN RETURN NEXT jsonb_build_object('__unavailable_ppa_identity',true);END IF;
END$$;
ALTER FUNCTION security.risk_ppa_trade_heads(uuid,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.risk_ppa_trade_heads(uuid,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.risk_ppa_trade_heads(uuid,uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
