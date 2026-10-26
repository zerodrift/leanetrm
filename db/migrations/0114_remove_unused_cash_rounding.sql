-- Retire the disconnected reference catalog, preserving executable cash policies.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL row_security=off;
SELECT pg_advisory_xact_lock(730849102);
LOCK TABLE ref_data.cash_rounding_conventions IN ACCESS EXCLUSIVE MODE;
DO $$
DECLARE body text;updated text;
BEGIN
 IF current_setting('transaction_isolation')<>'read committed' THEN
  RAISE EXCEPTION 'Reference retirement requires READ COMMITTED' USING ERRCODE='25000';
 END IF;
 IF EXISTS(SELECT 1 FROM ref_data.cash_rounding_conventions) THEN
  RAISE EXCEPTION 'Retained cash-rounding reference rows require explicit retirement before this upgrade' USING ERRCODE='55000';
 END IF;
 body:=pg_get_functiondef('security.reference_display_tables()'::regprocedure);
 updated:=replace(body,'''cash_rounding_conventions'',','');
 IF updated=body THEN RAISE EXCEPTION 'Cash-rounding retirement requires the expected display table catalog';END IF;
 EXECUTE updated;
END$$;
-- No CASCADE: any unexpected dependency must stop the upgrade.
DROP TABLE ref_data.cash_rounding_conventions;
COMMIT;
