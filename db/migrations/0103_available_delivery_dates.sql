-- Delivery-date selectors consume only scoped, certified operating-day coverage.
BEGIN;
CREATE FUNCTION power_data.available_dates(cutoff timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected power_data.sources;days jsonb;result jsonb='[]'::jsonb;
BEGIN
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'collection_historical_context';END IF;
 IF cutoff IS NULL OR NOT isfinite(cutoff) OR cutoff>clock_timestamp() THEN RAISE EXCEPTION 'collection_window';END IF;
 FOR selected IN SELECT s.* FROM power_data.sources s
 WHERE security.market_access(s.catalog_id,'ReadMarketData') AND security.reference_access(s.reference_catalog_id,'ReadMarketData')
 ORDER BY s.provider,s.market,s.name LOOP
  -- Resolve the same knowledge boundary that subsequent price reads consume.
  PERFORM security.capture_date_context(selected.catalog_id,(cutoff AT TIME ZONE 'UTC')::date,cutoff);
  IF NOT security.market_access(selected.catalog_id,'ReadMarketData') OR NOT security.reference_access(selected.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
  SELECT coalesce(jsonb_agg(d.operating_date ORDER BY d.operating_date),'[]'::jsonb) INTO days
  FROM power_data.operating_days d
  WHERE (d.reference_catalog_id,d.reference_revision_id,d.clock_code)=(selected.reference_catalog_id,selected.reference_revision_id,selected.clock_code)
  AND EXISTS(SELECT 1 FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id JOIN power_data.receipts r USING(sequence)
    WHERE p.source_id=selected.id AND p.starts_at=d.starts_at AND p.ends_at<=d.ends_at AND r.known_at<=cutoff);
  -- Successful batches certify whole operating days. A configured source or a
  -- failed collection attempt alone does not make a date selectable.
  IF NOT security.market_access(selected.catalog_id,'ReadMarketData') OR NOT security.reference_access(selected.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
  IF jsonb_array_length(days)>0 THEN result=result||jsonb_build_array(to_jsonb(selected)||jsonb_build_object('availableDates',days));END IF;
 END LOOP;
 RETURN result;
END$$;
ALTER FUNCTION power_data.available_dates(timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION power_data.available_dates(timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION power_data.available_dates(timestamptz) TO openetrm_runtime;
COMMIT;
