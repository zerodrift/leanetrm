BEGIN;
-- A hidden receipt is an existing command, never a new request. Only its owner
-- may resolve it, and current exact-source authority precedes payload comparison.
CREATE FUNCTION security.read_custom_curve_request(catalog uuid,requested uuid,permission text,payload jsonb)
 RETURNS TABLE(operation text,payload_matches boolean,response jsonb)
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;receipt market_data.custom_curve_requests;
BEGIN
 IF permission NOT IN('ReadMarketData','WriteMarketDefinitions','SealMarketDefinitions')
 OR security.current_write_context() IS NOT TRUE
 THEN RAISE EXCEPTION 'custom_curve_denied' USING ERRCODE='42501';END IF;
 actor=security.custom_curve_actor(catalog,permission);
 IF actor IS NULL THEN RAISE EXCEPTION 'custom_curve_denied' USING ERRCODE='42501';END IF;
 SELECT q.* INTO receipt FROM market_data.custom_curve_requests q
 WHERE(q.catalog_id,q.actor_id,q.request_id)=(catalog,actor,requested);
 IF NOT FOUND THEN RETURN;END IF;
 IF security.custom_curve_source_visible(catalog,receipt.definition_revision_id) IS NOT TRUE
 OR security.custom_curve_visible(catalog,receipt.curve_id,receipt.version) IS NOT TRUE
 THEN RAISE EXCEPTION 'custom_curve_denied' USING ERRCODE='42501';END IF;
 RETURN QUERY SELECT receipt.operation,receipt.payload=payload,receipt.response;
END$$;
ALTER FUNCTION security.read_custom_curve_request(uuid,uuid,text,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_custom_curve_request(uuid,uuid,text,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_custom_curve_request(uuid,uuid,text,jsonb) TO openetrm_runtime;
COMMIT;
