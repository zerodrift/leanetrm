-- New fits retain exact option time and axis implementation inputs. Old revisions
-- remain immutable and readable by their existing consumers. An explicitly
-- requested fit under the new input policy records a new revision, while repeated
-- fits under that same policy retain the ordinary determinism/idempotency guard.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$DECLARE body text;before text;BEGIN
 body=pg_get_functiondef('trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,uuid,timestamptz,uuid[],jsonb,text)'::regprocedure);
 before='AND r.content->''sensitivities''=surface->''sensitivities''';
 IF(length(body)-length(replace(body,before,'')))/length(before)<>1 THEN
  RAISE EXCEPTION 'vol_surface_axis_inputs_drift: record identity';
 END IF;
 EXECUTE replace(body,before,before||' AND r.content->''plotInputs'' IS NOT DISTINCT FROM surface->''plotInputs''');
END$upgrade$;
COMMIT;
