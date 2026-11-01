-- Exact crude inventory and leased worker reads for retained portfolio valuation.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
-- The copied current-authority policies call this existing bound-request predicate.
GRANT EXECUTE ON FUNCTION security.trading_access(uuid,text) TO openetrm_risk_worker;

-- Workers retain all runtime authority/history predicates, plus their bound lease.
DO $$DECLARE item record;policy record;table_name text;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('ref_data',ARRAY['crude_grades','crude_delivery_points']),
  ('market_data',ARRAY['crude_valuation_assignments']),
  ('trading',ARRAY['crude_instruments','crude_memberships','crude_trade_revisions'])
 )tables(schema_name,names) LOOP
  FOREACH table_name IN ARRAY item.names LOOP
   IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname=item.schema_name AND tablename=table_name
    AND 'openetrm_runtime'=ANY(roles) AND cmd IN('SELECT','ALL') AND permissive='PERMISSIVE')
   THEN RAISE EXCEPTION 'crude_worker_policy_missing';END IF;
   FOR policy IN SELECT * FROM pg_policies WHERE schemaname=item.schema_name AND tablename=table_name
    AND 'openetrm_runtime'=ANY(roles) AND cmd IN('SELECT','ALL') LOOP
    EXECUTE format('CREATE POLICY %I ON %I.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)',
     'risk_worker_'||policy.policyname,policy.schemaname,policy.tablename,policy.permissive,policy.qual);
   END LOOP;
   EXECUTE format('CREATE POLICY risk_worker_service ON %I.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',item.schema_name,table_name);
   EXECUTE format('GRANT SELECT ON %I.%I TO openetrm_risk_worker',item.schema_name,table_name);
  END LOOP;
 END LOOP;
END$$;

CREATE FUNCTION security.crude_position_scope(catalog uuid,books uuid[],portfolios uuid[]) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE result jsonb;context uuid;complete boolean;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 OR(session_user='openetrm_risk_worker' AND NOT risk.worker_read())
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
  WHERE(cardinality(books)=0 OR r.book_id=ANY(books))
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
 OR(session_user='openetrm_risk_worker' AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'Crude position authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.crude_position_scope(uuid,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.crude_position_scope(uuid,uuid[],uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.crude_position_scope(uuid,uuid[],uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
