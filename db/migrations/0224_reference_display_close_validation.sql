-- A display edit copies the exact reference closure. Newly copied futures rows
-- have no planner statistics: filtering mismatches before joining their series
-- can repeat every clock lookup for every series, timing out a label-only save.
-- Resolve the product's milestones first, then validate each matched row once.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);

DO $$BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid='ref_data.require_close_reproduces_milestones()'::regprocedure
  AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')='6261161eb112a47ad3ebed3203eea0f7f59806e0218c10ce7b5c0bdf52fb6512'
  AND p.proowner=(SELECT relowner FROM pg_class WHERE oid='ref_data.trading_close_times'::regclass)
  AND coalesce(p.proacl,acldefault('f',p.proowner))=acldefault('f',p.proowner)
  AND p.proconfig=ARRAY['search_path=pg_catalog'] AND p.provolatile='v' AND NOT p.prosecdef
  AND NOT p.proisstrict AND NOT p.proleakproof AND p.proparallel='u'
 ) THEN RAISE EXCEPTION 'reference_close_validation_predecessor';END IF;
END$$;

CREATE OR REPLACE FUNCTION ref_data.require_close_reproduces_milestones() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.milestone='last_trading' AND EXISTS(
  WITH milestones AS MATERIALIZED(
   SELECT m.clock_code,m.event_at,m.business_date
   FROM ref_data.futures_series s JOIN ref_data.futures_milestones m
    ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'last_trading')
   WHERE(s.catalog_id,s.revision_id,s.product_code)=(NEW.catalog_id,NEW.revision_id,NEW.futures_product_code)
    AND m.event_at IS NOT NULL
  )
  SELECT 1 FROM milestones m
  WHERE m.clock_code IS DISTINCT FROM NEW.clock_code
   OR m.event_at<>ref_data.local_instant(NEW.catalog_id,NEW.revision_id,NEW.clock_code,NEW.local_time,m.business_date)
 ) THEN RAISE EXCEPTION 'close_time_mismatch';END IF;
 RETURN NEW;
END$$;
COMMIT;
