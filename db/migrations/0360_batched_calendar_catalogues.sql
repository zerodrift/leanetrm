-- Current consumer: market date availability. Keep each dated selector's
-- structured refusal without aborting the other candidates in the batch.
BEGIN;
CREATE FUNCTION security.read_request_catalogues(catalog uuid,days date[])
 RETURNS TABLE(effective_on date,publication jsonb,error text)
 LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog AS $$
BEGIN
 IF days IS NULL OR cardinality(days) NOT BETWEEN 1 AND 128 OR array_position(days,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'catalogue_context' USING ERRCODE='P0001';END IF;
 FOR effective_on IN SELECT DISTINCT d FROM unnest(days)d ORDER BY d LOOP
  publication=NULL;error=NULL;
  BEGIN
   publication=security.read_request_catalogue(catalog,effective_on);
  EXCEPTION WHEN SQLSTATE 'P0001' THEN
   IF SQLERRM NOT IN('catalogue_source','catalogue_context','catalogue_missing','catalogue_conflict','catalogue_publication') THEN RAISE;END IF;
   error=SQLERRM;
  END;
  RETURN NEXT;
 END LOOP;
END;$$;
ALTER FUNCTION security.read_request_catalogues(uuid,date[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_request_catalogues(uuid,date[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_request_catalogues(uuid,date[]) TO openetrm_runtime;
COMMIT;
