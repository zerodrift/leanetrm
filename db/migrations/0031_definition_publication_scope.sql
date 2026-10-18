-- New publications require current access to their exact retained references.
-- Accepted publication retries return only their retained control ordinal.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SELECT pg_advisory_xact_lock(730849104);
CREATE OR REPLACE FUNCTION security.publish_tou_product(catalog uuid, product uuid, requested_version integer, expected_generation integer)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.tou_product_versions; next_order integer;
BEGIN
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing permission' USING ERRCODE='42501'; END IF;
 PERFORM pg_advisory_xact_lock(730849104);
 SELECT * INTO row FROM market_data.tou_product_versions WHERE catalog_id=catalog AND product_id=product AND version=requested_version FOR UPDATE;
 IF row.product_id IS NULL OR row.generation IS DISTINCT FROM expected_generation THEN RAISE EXCEPTION 'Stale TOU product version' USING ERRCODE='40001'; END IF;
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'Publishing authority expired during admission' USING ERRCODE='42501'; END IF;
 IF row.state='published' THEN RETURN row.publication_order; END IF;
 IF row.clock_catalog_id IS NOT NULL AND NOT security.reference_access(row.clock_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access for publication' USING ERRCODE='42501'; END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO next_order FROM market_data.tou_product_versions WHERE catalog_id=catalog;
 UPDATE market_data.tou_product_versions SET state='published',publication_order=next_order,published_at=clock_timestamp()
  WHERE catalog_id=catalog AND product_id=product AND version=requested_version;
 RETURN next_order;
END $$;

CREATE OR REPLACE FUNCTION security.publish_date_schedule(catalog uuid, schedule uuid, requested_version integer, expected_generation integer)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.date_schedule_versions; next_order integer;
BEGIN
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing permission' USING ERRCODE='42501'; END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO row FROM market_data.date_schedule_versions WHERE catalog_id=catalog AND schedule_id=schedule AND version=requested_version FOR UPDATE;
 IF row.schedule_id IS NULL OR row.generation IS DISTINCT FROM expected_generation THEN RAISE EXCEPTION 'Stale schedule version' USING ERRCODE='40001'; END IF;
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'Publishing authority expired during admission' USING ERRCODE='42501'; END IF;
 IF row.state='published' THEN RETURN row.publication_order; END IF;
 IF row.calendar_catalog_id IS NOT NULL AND NOT security.reference_access(row.calendar_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access for publication' USING ERRCODE='42501'; END IF;
 IF current_setting('track_commit_timestamp')<>'on' THEN RAISE EXCEPTION 'knowledge_tracking' USING ERRCODE='P0001'; END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO next_order FROM market_data.date_schedule_versions WHERE catalog_id=catalog;
 UPDATE market_data.date_schedule_versions SET state='published',publication_order=next_order,published_at=clock_timestamp(),publication_xid=pg_current_xact_id()
  WHERE catalog_id=catalog AND schedule_id=schedule AND version=requested_version;
 RETURN next_order;
END $$;
COMMIT;
