-- Settlement completion and contract bridges aggregate the existing authorized
-- interval reader. Workers must retain their current attempt fence around the
-- same exact trade, reference and child-provenance checks as interactive reads.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
ALTER FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer)
 RENAME TO read_swap_power_quantity_intervals_pre_worker;
REVOKE ALL ON FUNCTION security.read_swap_power_quantity_intervals_pre_worker(uuid,uuid,integer)
 FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.read_swap_power_quantity_intervals(catalog uuid,trade uuid,revision_number integer)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 PERFORM risk.require_worker_attempt();
 result=security.read_swap_power_quantity_intervals_pre_worker(catalog,trade,revision_number);
 PERFORM risk.require_worker_attempt();
 RETURN result;
END$$;
ALTER FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer)
 TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
