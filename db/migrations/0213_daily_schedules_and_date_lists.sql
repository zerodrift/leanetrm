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
-- Date lists (engine date_list_v1) are contract-date mappings with one 'date' field and no trading cutoff per row.
ALTER TABLE market_data.contract_date_versions DROP CONSTRAINT contract_date_versions_engine_version_check;
ALTER TABLE market_data.contract_date_versions ADD CONSTRAINT contract_date_versions_engine_version_check CHECK(engine_version IN('contract_dates_v1','date_list_v1'));
ALTER TABLE market_data.contract_date_contracts ALTER COLUMN cutoff_utc DROP NOT NULL;
ALTER TABLE market_data.contract_date_fields DROP CONSTRAINT contract_date_fields_role_check;
-- A thirty-year calendar-day tenor is occurrence 10957, so occurrences take five digits.
ALTER TABLE market_data.contract_date_fields DROP CONSTRAINT contract_date_fields_check;
ALTER TABLE market_data.contract_date_fields ADD CONSTRAINT contract_date_fields_check CHECK(
 (literal_date IS NOT NULL AND isfinite(literal_date) AND literal_date>=DATE '0001-01-01' AND literal_date<DATE '9999-12-31'
  AND schedule_catalog_id IS NULL AND schedule_id IS NULL AND schedule_version IS NULL AND occurrence IS NULL AND business_days IS NULL)
 OR(literal_date IS NULL AND schedule_catalog_id IS NOT NULL AND schedule_catalog_id=catalog_id AND schedule_id IS NOT NULL AND schedule_version IS NOT NULL
  AND occurrence IS NOT NULL AND occurrence BETWEEN 0 AND 99999 AND (business_days IS NULL OR business_days BETWEEN -10000 AND 10000 AND business_days<>0)));
ALTER TABLE market_data.contract_date_fields ADD CONSTRAINT contract_date_fields_role_check CHECK(role IN('start','end','last_trading','date'));
-- Publication requires each row's complete field set for its engine: a cutoff and start, end and last trading for contracts;
-- no cutoff and exactly one date for a date list.
CREATE OR REPLACE FUNCTION security.publish_contract_dates(catalog uuid,definition uuid,requested_version integer,expected_generation integer) RETURNS integer
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row market_data.contract_date_versions;ordinal integer;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No current publishing authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO row FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version) FOR UPDATE;
 IF NOT FOUND OR row.generation IS DISTINCT FROM expected_generation THEN RAISE EXCEPTION 'Stale definition generation' USING ERRCODE='40001';END IF;
 IF NOT security.contract_date_visible(catalog,definition,requested_version) OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'Publishing authority expired' USING ERRCODE='42501';END IF;
 IF row.state='published' THEN RETURN row.publication_order;END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.contract_date_contracts WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version))
 OR EXISTS(SELECT 1 FROM market_data.contract_date_contracts c WHERE(c.catalog_id,c.definition_id,c.version)=(catalog,definition,requested_version)
  AND (CASE row.engine_version WHEN 'date_list_v1' THEN c.cutoff_utc IS NOT NULL ELSE c.cutoff_utc IS NULL END
   OR (SELECT array_agg(f.role ORDER BY f.role) FROM market_data.contract_date_fields f WHERE(f.catalog_id,f.definition_id,f.version,f.ordinal)=(c.catalog_id,c.definition_id,c.version,c.ordinal))
    IS DISTINCT FROM (CASE row.engine_version WHEN 'date_list_v1' THEN ARRAY['date'] ELSE ARRAY['end','last_trading','start'] END))) THEN
  RAISE EXCEPTION 'Incomplete definition fields' USING ERRCODE='23514';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.contract_date_versions WHERE catalog_id=catalog;
 UPDATE market_data.contract_date_versions SET state='published',publication_order=ordinal,published_at=clock_timestamp(),publication_xid=pg_current_xact_id()
 WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version);
 RETURN ordinal;
END;$$;
COMMIT;
