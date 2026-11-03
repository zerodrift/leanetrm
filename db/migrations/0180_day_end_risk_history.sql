-- End-of-day calibration retains both local-midnight boundaries. Existing
-- exact-close receipts and requests keep their original contracts and identity.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;
DO $$DECLARE definition text;changed text;BEGIN
 definition=pg_get_functiondef('risk.capture_history_clock(uuid,uuid,jsonb,jsonb)'::regprocedure);
 changed=replace(definition,'clock->>''resolution'' IS DISTINCT FROM ''captured_calendar_session_closes_v1''',
  '(clock->>''resolution'' IS NULL OR clock->>''resolution'' NOT IN(''captured_calendar_session_closes_v1'',''captured_calendar_day_windows_v1''))');
 IF changed=definition THEN RAISE EXCEPTION 'day_end_requires_known_capture_kernel';END IF;
 definition=changed;
 changed=replace(definition,'OR clock#>''{reference,revision}'' IS DISTINCT FROM calendar#>''{reference,revision}''',
  E'OR(clock->>''resolution''=''captured_calendar_day_windows_v1'' AND(clock->>''closeTime'' IS DISTINCT FROM ''end_of_day''\n  OR jsonb_typeof(clock->''dayWindows'') IS DISTINCT FROM ''array'' OR jsonb_array_length(clock->''dayWindows'')<>jsonb_array_length(calendar->''businessDates'')))\n  OR clock#>''{reference,revision}'' IS DISTINCT FROM calendar#>''{reference,revision}''');
 IF changed=definition THEN RAISE EXCEPTION 'day_end_requires_known_capture_reference_guard';END IF;EXECUTE changed;
 definition=pg_get_functiondef('risk.retain_parametric(risk.jobs)'::regprocedure);
 changed=replace(definition,'clock->>''resolution'' IS DISTINCT FROM ''captured_calendar_session_closes_v1''',
  E'NOT coalesce(((clock->>''resolution''=''captured_calendar_session_closes_v1'' AND plan#>>''{policy,alignment}''=''exact_session_close_v1'' AND NOT(plan->''policy'' ? ''priceTypes''))\n  OR(clock->>''resolution''=''captured_calendar_day_windows_v1'' AND clock->>''closeTime''=''end_of_day''\n  AND plan#>>''{policy,alignment}''=''daily_latest_visible_session_end_v1''\n  AND plan#>>''{policy,regimes}''=''ordered_price_types_fixed_series_source_unit_contract_v1''\n  AND jsonb_typeof(plan#>''{policy,priceTypes}'')=''array'' AND jsonb_array_length(plan#>''{policy,priceTypes}'') BETWEEN 1 AND 7)),false)');
 IF changed=definition THEN RAISE EXCEPTION 'day_end_requires_known_retention_kernel';END IF;EXECUTE changed;
END$$;
COMMIT;
