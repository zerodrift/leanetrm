-- A commodity names its icon in reference data (D424). The icon is required and comes from a
-- fixed vocabulary; nothing infers it from a commodity's code or name after this migration.
-- Stop runtime admission and drain calls before this schema upgrade.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

ALTER TABLE ref_data.commodities ADD COLUMN icon_code text;
-- Reference rows are immutable, so the guard is lifted for this one statement only. It carries
-- each existing commodity over to the picture the browser drew for its code until now.
ALTER TABLE ref_data.commodities DISABLE TRIGGER guard_definition;
UPDATE ref_data.commodities SET icon_code=CASE WHEN code IN('gas','test_gas') THEN 'flame'
 WHEN code IN('electricity','test_power') THEN 'bolt' ELSE 'cube' END;
ALTER TABLE ref_data.commodities ENABLE TRIGGER guard_definition;
ALTER TABLE ref_data.commodities ALTER COLUMN icon_code SET NOT NULL,
 ADD CONSTRAINT commodities_icon_code_check CHECK(icon_code IN('flame','bolt','drop','tanker','leaf','cube'));

-- The reference import admits only gas and electricity; it now states each one's icon.
DO $upgrade$
DECLARE definition text;needle text;
BEGIN
 definition=pg_get_functiondef('security.import_reference_capture(uuid,uuid,jsonb)'::regprocedure);
 needle=$$CASE key WHEN 'gas' THEN 'Natural gas' ELSE 'Electricity' END,'energy');$$;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'commodity_icon_upgrade';END IF;
 EXECUTE replace(definition,needle,$$CASE key WHEN 'gas' THEN 'Natural gas' ELSE 'Electricity' END,'energy',CASE key WHEN 'gas' THEN 'flame' ELSE 'bolt' END);$$);
END$upgrade$;
COMMIT;
