-- Uploaded and generated date lists share the published schedule resolver.
BEGIN;
ALTER TABLE market_data.date_schedule_versions DROP CONSTRAINT date_schedule_versions_selection_check;
ALTER TABLE market_data.date_schedule_versions ADD CHECK(selection IN ('absolute_index','context_month','on_or_after'));
ALTER TABLE market_data.date_schedule_versions DROP CONSTRAINT schedule_engine_calendar;
ALTER TABLE market_data.date_schedule_versions ADD CONSTRAINT schedule_engine_calendar CHECK (
 (engine_version='date_schedule_v1' AND calendar_catalog_id IS NULL AND calendar_revision_id IS NULL AND calendar_code IS NULL AND jsonb_typeof(definition->'calendar')='object')
 OR (engine_version='date_schedule_v2' AND calendar_catalog_id IS NOT NULL AND calendar_revision_id IS NOT NULL AND calendar_code IS NOT NULL AND coalesce(jsonb_typeof(definition->'calendar'),'null')='null')
 OR (engine_version='date_schedule_v3' AND coalesce(jsonb_typeof(definition->'dates'),'null')='array' AND coalesce(jsonb_typeof(definition->'calendar'),'null')='null'
 AND ((calendar_catalog_id IS NULL AND calendar_revision_id IS NULL AND calendar_code IS NULL) OR (calendar_catalog_id IS NOT NULL AND calendar_revision_id IS NOT NULL AND calendar_code IS NOT NULL))));
COMMIT;
