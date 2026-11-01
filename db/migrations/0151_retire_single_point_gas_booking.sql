-- Physical gas is booked as a composition from the deal ticket (D281). Stored single-point
-- trades stay readable; their retired booking entry is no longer callable by the runtime.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
REVOKE EXECUTE ON FUNCTION security.book_physical_gas(uuid,jsonb,timestamptz) FROM openetrm_runtime;
COMMIT;
