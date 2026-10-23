-- Bounded PPA authoring choices and complete cross-actor retained history.
-- Read-only consumers; no new tables, economic terms, write permissions or RLS changes.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);

CREATE OR REPLACE FUNCTION security.ppa_read_scope(catalog uuid,scope text,ids uuid[],context uuid,as_of timestamptz,include_history boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE heads trading.ppa_trade_revisions[];r trading.ppa_trade_revisions;result jsonb;historical jsonb;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'ppa_historical' USING ERRCODE='P0001';END IF;
 IF scope IS NULL OR scope NOT IN('books','trades') OR ids IS NULL OR cardinality(ids) NOT BETWEEN 1 AND 256 OR include_history IS NULL OR
  EXISTS(SELECT 1 FROM unnest(ids) x WHERE x IS NULL OR x='00000000-0000-0000-0000-000000000000'::uuid) OR
  (SELECT count(DISTINCT x) FROM unnest(ids) x)<>cardinality(ids) OR (include_history AND(scope<>'trades' OR cardinality(ids)<>1))
 THEN RAISE EXCEPTION 'ppa_scope' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE t.catalog_id=catalog AND t.id=context AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 IF scope='books' AND(SELECT count(*) FROM trading.books b WHERE b.catalog_id=catalog AND b.id=ANY(ids) AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of))<>cardinality(ids)
 THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT coalesce(array_agg(h::trading.ppa_trade_revisions ORDER BY h.trade_id),ARRAY[]::trading.ppa_trade_revisions[]) INTO heads FROM(
  SELECT DISTINCT ON(t.trade_id) t.* FROM trading.ppa_trade_revisions t WHERE t.catalog_id=catalog
   AND security.ppa_visible_at(catalog,t.publication_order,t.effective_at,context,as_of) ORDER BY t.trade_id,t.revision DESC
 )h WHERE CASE WHEN scope='books' THEN h.book_id=ANY(ids) ELSE h.trade_id=ANY(ids) END;
 IF cardinality(heads)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF scope='trades' AND cardinality(heads)<>cardinality(ids) THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 END LOOP;
 IF(SELECT count(*) FROM trading.ppa_revision_bindings b JOIN unnest(heads) h ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision))>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF include_history THEN
  SELECT coalesce(jsonb_agg(to_jsonb(retained) ORDER BY retained.revision,retained.coverage_from,retained.binding_id),'[]'::jsonb) INTO historical FROM(
   SELECT b.* FROM trading.ppa_revision_bindings b JOIN trading.ppa_trade_revisions h
    ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision)
   WHERE h.catalog_id=catalog AND h.trade_id=ids[1]
    AND security.ppa_visible_at(catalog,h.publication_order,h.effective_at,context,as_of)
    AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of)
   ORDER BY b.revision,b.coverage_from,b.binding_id LIMIT 257
  )retained;
  IF jsonb_array_length(historical)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 ELSE historical='[]'::jsonb;END IF;
 SELECT jsonb_build_object('heads',to_jsonb(heads),'bindings',coalesce((SELECT jsonb_agg(to_jsonb(b) ORDER BY b.trade_id,b.coverage_from,b.binding_id)
  FROM trading.ppa_revision_bindings b JOIN unnest(heads) h ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision)),'[]'::jsonb),
  'historicalBindings',historical,'history',CASE WHEN include_history THEN coalesce((SELECT jsonb_agg(to_jsonb(h) ORDER BY h.revision) FROM trading.ppa_trade_revisions h
   WHERE h.catalog_id=catalog AND h.trade_id=ids[1] AND security.ppa_visible_at(catalog,h.publication_order,h.effective_at,context,as_of)),'[]'::jsonb) ELSE '[]'::jsonb END) INTO result;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Current reference access expired' USING ERRCODE='42501';END IF;
 END LOOP;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;

CREATE FUNCTION security.ppa_authoring_directory(catalog uuid,context uuid,as_of timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE books jsonb;entities jsonb;parties jsonb;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'ppa_historical' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE t.catalog_id=catalog AND t.id=context AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 SELECT coalesce(jsonb_agg(to_jsonb(b) ORDER BY b.name,b.id),'[]'::jsonb) INTO books FROM(
  SELECT b.id,b.name,b.portfolio_id,p.name AS portfolio FROM trading.books b
   JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE b.catalog_id=catalog AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of)
   AND security.ppa_visible_at(catalog,p.publication_order,p.effective_at,context,as_of)
  ORDER BY b.name,b.id LIMIT 257
 )b;
 SELECT coalesce(jsonb_agg(to_jsonb(e) ORDER BY e.code,e.id),'[]'::jsonb) INTO entities FROM(
  SELECT DISTINCT ON(id) id,revision,code,name FROM trading.legal_entities e
  WHERE e.catalog_id=catalog AND security.ppa_visible_at(catalog,e.publication_order,e.effective_at,context,as_of)
  ORDER BY id,revision DESC LIMIT 257
 )e;
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.code,p.id),'[]'::jsonb) INTO parties FROM(
  SELECT DISTINCT ON(id) id,revision,code,name FROM trading.counterparties p
  WHERE p.catalog_id=catalog AND security.ppa_visible_at(catalog,p.publication_order,p.effective_at,context,as_of)
  ORDER BY id,revision DESC LIMIT 257
 )p;
 IF jsonb_array_length(books)>256 OR jsonb_array_length(entities)>256 OR jsonb_array_length(parties)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('books',books,'legalEntities',entities,'counterparties',parties);
END$$;
ALTER FUNCTION security.ppa_authoring_directory(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.ppa_authoring_directory(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.ppa_authoring_directory(uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
