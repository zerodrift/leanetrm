-- A forward PPA amendment may end later than the retained coverage, from a boundary inside it or appended at its end.
-- Ending earlier remains a termination; every earlier binding keeps its coverage, quantity and terms.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE definition text=pg_get_functiondef('security.save_ppa_booking(uuid,uuid,jsonb)'::regprocedure);
 before text='boundary>=retained_end OR(action=''amended'' AND(first_at<>boundary OR last_at<>retained_end))';
BEGIN
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'ppa_extension_predecessor';END IF;
 EXECUTE replace(definition,before,
  'boundary>retained_end OR(action=''amended'' AND(first_at<>boundary OR last_at<retained_end)) OR(action=''terminated'' AND boundary=retained_end)');
END$$;
COMMIT;
