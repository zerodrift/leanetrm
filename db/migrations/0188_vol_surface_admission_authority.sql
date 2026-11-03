-- A native fit retry can wait on another admission until its bound token expires.
-- Recheck current authority before returning a first outcome or committing reuse.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $upgrade$
DECLARE definition text=pg_get_functiondef('risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure);
 before text='RETURN risk.summary(job);';
 after text=$guard$
 IF security.has_permission('PublishMarketData') IS NOT TRUE
  OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
  OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN risk.summary(job);$guard$;
BEGIN
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>3
  OR position(after IN definition)<>0
 THEN RAISE EXCEPTION 'vol_surface_authority_upgrade_mismatch' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before,after);
END $upgrade$;
COMMIT;
