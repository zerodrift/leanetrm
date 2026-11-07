-- Current consumer: payment discounting (D359). An interest-rate curve definition owns its currency, mark unit and conventions.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';

-- 1. Interest-rate definitions own their terms instead of naming an interest-rate specification.
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM market_data.curve_definition_versions WHERE kind='interest_rate') THEN
  RAISE EXCEPTION 'Interest-rate curve definitions without their own conventions exist; remove them in the schema window first' USING ERRCODE='55000';
 END IF;
END$$;
ALTER TABLE market_data.curve_definition_versions DROP COLUMN interest_rate_code;
ALTER TABLE market_data.curve_definition_versions DROP CONSTRAINT curve_definition_versions_interpolation_check;
ALTER TABLE market_data.curve_definition_versions
 ADD COLUMN currency_code text,
 ADD COLUMN mark_unit text CHECK(mark_unit IN('percent_per_year','decimal_per_year')),
 ADD COLUMN day_count text CHECK(day_count IN('actual_365_fixed','actual_360')),
 ADD COLUMN compounding text CHECK(compounding IN('simple','continuous','annual')),
 ADD COLUMN extrapolation text CHECK(extrapolation IN('none','flat_zero','flat_forward')),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 -- Contract curves interpolate 'none'; an interest-rate curve names one of its offered interpolations.
 ADD CHECK(CASE kind
  WHEN 'interest_rate' THEN interpolation IN('log_linear_discount','linear_zero')
   AND num_nulls(currency_code,mark_unit,day_count,compounding,extrapolation)=0
   AND num_nonnulls(commodity_code,location_code,quote_code,profile_code,futures_product_code,settlement_target_code,price_rule,mark_role,missing_price)=0
  ELSE interpolation='none' AND num_nonnulls(currency_code,mark_unit,day_count,compounding,extrapolation)=0
   AND num_nulls(commodity_code,location_code,quote_code,profile_code,settlement_target_code,price_rule,mark_role,missing_price)=0
   AND (kind='future')=(futures_product_code IS NOT NULL) END);
GRANT UPDATE(currency_code,mark_unit,day_count,compounding,extrapolation) ON market_data.curve_definition_versions TO openetrm_runtime;

COMMIT;
