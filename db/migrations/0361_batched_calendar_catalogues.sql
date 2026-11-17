-- Current consumer: market date availability. Keep each dated selector's
-- structured refusal without aborting the other candidates in the batch.
BEGIN;
CREATE FUNCTION security.read_request_catalogues(catalog uuid,days date[])
 RETURNS TABLE(effective_on date,publication jsonb,error text,denied boolean)
 LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog AS $$
BEGIN
 IF days IS NULL OR cardinality(days) NOT BETWEEN 1 AND 128 OR array_position(days,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'catalogue_context' USING ERRCODE='P0001';END IF;
 FOR effective_on IN SELECT DISTINCT d FROM unnest(days)d ORDER BY d LOOP
  publication=NULL;error=NULL;denied=false;
  BEGIN
   publication=security.read_request_catalogue(catalog,effective_on);
  EXCEPTION WHEN insufficient_privilege THEN
   -- A prefetched date is not a consumed choice. Return no protected payload;
   -- the caller raises the denial only if its limited walk reaches this date.
   publication=NULL;denied=true;
  WHEN SQLSTATE 'P0001' THEN
   IF SQLERRM NOT IN('catalogue_source','catalogue_context','catalogue_missing','catalogue_conflict','catalogue_publication') THEN RAISE;END IF;
   error=SQLERRM;
  END;
  RETURN NEXT;
 END LOOP;
 -- Source-specific refusals may be tagged, but global expiry never publishes
 -- even the earlier successful payloads of this native operation.
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
END;$$;
ALTER FUNCTION security.read_request_catalogues(uuid,date[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_request_catalogues(uuid,date[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_request_catalogues(uuid,date[]) TO openetrm_runtime;

-- Prefetch is not consumption. A limited calendar may stop before a close whose
-- authority ended after discovery. Keep its denial private until it is reached.
CREATE FUNCTION security.read_market_close_parts_results(catalog uuid,close_ids uuid[],expected_context uuid)
 RETURNS TABLE(close_id uuid,parts jsonb,denied boolean)
 LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE payload jsonb;item jsonb;
BEGIN
 IF close_ids IS NULL OR cardinality(close_ids) NOT BETWEEN 1 AND 128 OR array_position(close_ids,NULL) IS NOT NULL
 OR(SELECT count(DISTINCT id) FROM unnest(close_ids)id)<>cardinality(close_ids)
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 BEGIN
  payload=security.read_market_close_parts(catalog,close_ids,expected_context);
 EXCEPTION WHEN insufficient_privilege THEN payload=NULL;
 END;
 IF payload IS NOT NULL THEN
  FOR item IN SELECT value FROM jsonb_array_elements(payload) LOOP
   close_id=(item->>'close_id')::uuid;parts=item;denied=false;RETURN NEXT;
  END LOOP;
 ELSE
  -- Only the refusal path needs individual subtransactions. The ordinary path
  -- retains the native matrix reader's one batch and its complete authority checks.
  FOREACH close_id IN ARRAY close_ids LOOP
   parts=NULL;denied=false;
   BEGIN
    payload=security.read_market_close_parts(catalog,ARRAY[close_id],expected_context);
    parts=payload->0;
   EXCEPTION WHEN insufficient_privilege THEN denied=true;
   END;
   RETURN NEXT;
  END LOOP;
 END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
END;$$;
ALTER FUNCTION security.read_market_close_parts_results(uuid,uuid[],uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_market_close_parts_results(uuid,uuid[],uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_market_close_parts_results(uuid,uuid[],uuid) TO openetrm_runtime;
COMMIT;
