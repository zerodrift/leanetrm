-- Current consumer: rolling date-list mappings (rate tenors) resolved from a generated daily schedule.
-- Daily schedules (engine date_schedule_v4) store only their rule; their dates are generated when resolved.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
ALTER TABLE market_data.date_schedule_versions DROP CONSTRAINT schedule_engine_calendar;
ALTER TABLE market_data.date_schedule_versions ADD CONSTRAINT schedule_engine_calendar CHECK (
 (engine_version='date_schedule_v1' AND calendar_catalog_id IS NULL AND calendar_revision_id IS NULL AND calendar_code IS NULL AND jsonb_typeof(definition->'calendar')='object')
 OR (engine_version='date_schedule_v2' AND calendar_catalog_id IS NOT NULL AND calendar_revision_id IS NOT NULL AND calendar_code IS NOT NULL AND coalesce(jsonb_typeof(definition->'calendar'),'null')='null')
 OR (engine_version='date_schedule_v3' AND coalesce(jsonb_typeof(definition->'dates'),'null')='array' AND coalesce(jsonb_typeof(definition->'calendar'),'null')='null'
 AND ((calendar_catalog_id IS NULL AND calendar_revision_id IS NULL AND calendar_code IS NULL) OR (calendar_catalog_id IS NOT NULL AND calendar_revision_id IS NOT NULL AND calendar_code IS NOT NULL)))
 OR (engine_version='date_schedule_v4' AND jsonb_typeof(definition->'daily')='object' AND coalesce(jsonb_typeof(definition->'dates'),'null')='null'
 AND coalesce(jsonb_typeof(definition->'calendar'),'null')='null' AND selection='on_or_after'
 AND ((definition->'daily'->>'days'='business' AND calendar_catalog_id IS NOT NULL AND calendar_revision_id IS NOT NULL AND calendar_code IS NOT NULL)
  OR (definition->'daily'->>'days'='calendar' AND ((calendar_catalog_id IS NULL AND calendar_revision_id IS NULL AND calendar_code IS NULL)
   OR (calendar_catalog_id IS NOT NULL AND calendar_revision_id IS NOT NULL AND calendar_code IS NOT NULL))))));
COMMIT;
