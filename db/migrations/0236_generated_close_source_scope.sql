-- The v1 generator and normalized report have one exact definition source.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION automation.require_generated_anchor_source(catalog uuid,anchor uuid,definition uuid)
 RETURNS void LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM market_data.market_close_sources s
  WHERE(s.catalog_id,s.close_id,s.definition_revision_id)=(catalog,anchor,definition))
 OR EXISTS(SELECT 1 FROM market_data.market_close_sources s
  WHERE(s.catalog_id,s.close_id)=(catalog,anchor) AND s.definition_revision_id<>definition)
 THEN RAISE EXCEPTION 'pilot_close_source_scope' USING ERRCODE='P0001';END IF;
END;$$;
ALTER FUNCTION automation.require_generated_anchor_source(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_generated_anchor_source(uuid,uuid,uuid) FROM PUBLIC;

DO $$DECLARE body text;before text;BEGIN
 body=pg_get_functiondef('automation.create_pilot_close_job(jsonb,jsonb)'::regprocedure);
 before=' p.id=gen_random_uuid();';
 IF (length(body)-length(replace(body,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'generated_close_admission_drift';END IF;
 EXECUTE replace(body,before,E' PERFORM automation.require_generated_anchor_source(anchor.catalog_id,anchor.id,anchor.definition_revision_id);\n'||before);

 body=pg_get_functiondef('automation.publish_pilot_close(jsonb,jsonb,jsonb)'::regprocedure);
 before=' IF security.close_in_force(anchor.catalog_id,anchor.session_code,anchor.business_date)<>anchor.id THEN RAISE EXCEPTION ''close_replaced'' USING ERRCODE=''P0001'';END IF;';
 IF (length(body)-length(replace(body,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'generated_close_anchor_drift';END IF;
 -- New writes use the current committed head under the publication lock.
 -- A certified preflight context may still legitimately see its older anchor.
 body=replace(body,before,$anchor$
 IF security.close_in_force(anchor.catalog_id,anchor.session_code,anchor.business_date) IS DISTINCT FROM anchor.id
 OR EXISTS(SELECT 1 FROM market_data.market_closes newer
  WHERE(newer.catalog_id,newer.session_code,newer.business_date)=(anchor.catalog_id,anchor.session_code,anchor.business_date)
  AND newer.publication_order>anchor.publication_order)
 THEN RAISE EXCEPTION 'close_replaced' USING ERRCODE='P0001';END IF;
 PERFORM automation.require_generated_anchor_source(p.catalog_id,p.prior_close_id,p.definition_revision_id);$anchor$);
 before=' PERFORM security.require_session_curves(p.catalog_id,p.session_code,day.session_version,members);';
 IF (length(body)-length(replace(body,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'generated_close_session_drift';END IF;
 EXECUTE replace(body,before,$guard$
 IF EXISTS(SELECT 1 FROM market_data.close_session_curves s
  WHERE(s.catalog_id,s.code,s.version)=(p.catalog_id,p.session_code,day.session_version)
  AND s.definition_revision_id<>p.definition_revision_id)
 THEN RAISE EXCEPTION 'pilot_close_source_scope' USING ERRCODE='P0001';END IF;
$guard$||before);
END$$;
COMMIT;
