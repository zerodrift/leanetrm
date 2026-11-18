-- Entirely synthetic data for a disposable test database. Not a real catalog.
BEGIN;
CREATE TEMP TABLE fixture_context AS SELECT
  '10000000-0000-0000-0000-000000000001'::uuid AS catalog_id,
  '20000000-0000-0000-0000-000000000001'::uuid AS revision_id;
INSERT INTO ref_data.catalogs VALUES
  ('10000000-0000-0000-0000-000000000001', 'synthetic', 'Synthetic test catalog'),
  ('10000000-0000-0000-0000-000000000002', 'other_synthetic', 'Other synthetic catalog');
INSERT INTO ref_data.revisions (catalog_id,id,revision_key)
  SELECT catalog_id, revision_id, 'fixture_v1' FROM fixture_context;
INSERT INTO ref_data.revisions (catalog_id,id,revision_key) VALUES
  ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000002','fixture_v2'),
  ('10000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001','fixture_v1');
INSERT INTO ref_data.currencies (catalog_id,revision_id,code,display_code,name,display_minor_units)
  SELECT catalog_id,revision_id,'test_currency_a','TST','Synthetic currency A',2 FROM fixture_context;
INSERT INTO ref_data.dimensions (catalog_id,revision_id,code,mass_power,length_power,time_power,temperature_power,amount_power,current_power,luminous_power)
  SELECT catalog_id,revision_id,'energy',1,2,-2,0,0,0,0 FROM fixture_context;
INSERT INTO ref_data.dimensions (catalog_id,revision_id,code,mass_power,length_power,time_power,temperature_power,amount_power,current_power,luminous_power)
  SELECT catalog_id,revision_id,'power',1,2,-3,0,0,0,0 FROM fixture_context;
INSERT INTO ref_data.dimensions (catalog_id,revision_id,code,mass_power,length_power,time_power,temperature_power,amount_power,current_power,luminous_power)
  SELECT catalog_id,revision_id,'volume',0,3,0,0,0,0,0 FROM fixture_context;
INSERT INTO ref_data.units (catalog_id,revision_id,code,symbol,dimension_code)
  SELECT catalog_id,revision_id,'energy_a','EA','energy' FROM fixture_context;
INSERT INTO ref_data.units (catalog_id,revision_id,code,symbol,dimension_code)
  SELECT catalog_id,revision_id,'energy_b','EB','energy' FROM fixture_context;
INSERT INTO ref_data.units (catalog_id,revision_id,code,symbol,dimension_code)
  SELECT catalog_id,revision_id,'power_a','PA','power' FROM fixture_context;
INSERT INTO ref_data.units (catalog_id,revision_id,code,symbol,dimension_code)
  SELECT catalog_id,revision_id,'volume_a','VA','volume' FROM fixture_context;
INSERT INTO ref_data.unit_conversions (catalog_id,revision_id,from_unit_code,to_unit_code,dimension_code,factor_numerator,factor_denominator)
  SELECT catalog_id,revision_id,'energy_a','energy_b','energy',4,1 FROM fixture_context;
-- Commodity rows are built from named fields: this file also loads on schemas that precede the icon column, which ignore it.
INSERT INTO ref_data.commodities SELECT (jsonb_populate_record(NULL::ref_data.commodities,jsonb_build_object(
  'catalog_id',catalog_id,'revision_id',revision_id,'code','test_gas','name','Synthetic gas','quantity_dimension_code','energy','icon_code','flame'))).*
  FROM fixture_context;
INSERT INTO ref_data.commodities SELECT (jsonb_populate_record(NULL::ref_data.commodities,jsonb_build_object(
  'catalog_id',catalog_id,'revision_id',revision_id,'code','test_power','name','Synthetic power','quantity_dimension_code','energy','icon_code','bolt'))).*
  FROM fixture_context;
INSERT INTO ref_data.commodity_units (catalog_id,revision_id,commodity_code,unit_code,dimension_code)
  SELECT catalog_id,revision_id,'test_gas','energy_a','energy' FROM fixture_context;
INSERT INTO ref_data.commodity_units (catalog_id,revision_id,commodity_code,unit_code,dimension_code)
  SELECT catalog_id,revision_id,'test_gas','energy_b','energy' FROM fixture_context;
INSERT INTO ref_data.commodity_units (catalog_id,revision_id,commodity_code,unit_code,dimension_code)
  SELECT catalog_id,revision_id,'test_power','energy_a','energy' FROM fixture_context;
INSERT INTO ref_data.clock_rules (catalog_id,revision_id,code,kind,fixed_offset_seconds)
  SELECT catalog_id,revision_id,'test_utc','fixed_offset',0 FROM fixture_context;
INSERT INTO ref_data.calendars (catalog_id,revision_id,code,clock_code,weekend_mask,coverage_from,coverage_to)
  SELECT catalog_id,revision_id,'test_business','test_utc',96,DATE '2039-01-01',DATE '2042-01-01' FROM fixture_context;
INSERT INTO ref_data.calendar_days (catalog_id,revision_id,calendar_code,business_date,is_business_day,description)
  SELECT catalog_id,revision_id,'test_business',DATE '2040-01-02',false,'Synthetic closure' FROM fixture_context;
INSERT INTO ref_data.location_types (catalog_id,revision_id,code,description)
  SELECT catalog_id,revision_id,'test_hub','Synthetic hub type' FROM fixture_context;
INSERT INTO ref_data.locations (catalog_id,revision_id,code,name,type_code,clock_code)
  SELECT catalog_id,revision_id,'test_hub_a','Synthetic hub A','test_hub','test_utc' FROM fixture_context;
INSERT INTO ref_data.commodity_locations (catalog_id,revision_id,commodity_code,location_code)
  SELECT catalog_id,revision_id,'test_gas','test_hub_a' FROM fixture_context;
INSERT INTO ref_data.exchanges (catalog_id,revision_id,code,name)
  SELECT catalog_id,revision_id,'test_exchange','Synthetic exchange' FROM fixture_context;
INSERT INTO ref_data.clearing_houses (catalog_id,revision_id,code,name)
  SELECT catalog_id,revision_id,'test_clearing','Synthetic clearing house' FROM fixture_context;
INSERT INTO ref_data.exchange_clearing_houses (catalog_id,revision_id,exchange_code,clearing_house_code)
  SELECT catalog_id,revision_id,'test_exchange','test_clearing' FROM fixture_context;
INSERT INTO ref_data.interest_rate_definitions (catalog_id,revision_id,code,currency_code,calendar_code,day_count,compounding,implementation)
  SELECT catalog_id,revision_id,'test_interest','test_currency_a','test_business','actual_365_fixed','simple','openetrm_rates_v1' FROM fixture_context;
INSERT INTO ref_data.price_index_definitions (catalog_id,revision_id,code,commodity_code,location_code,currency_code,quote_unit_code,quote_multiplier,calendar_code)
  SELECT catalog_id,revision_id,'test_price','test_gas','test_hub_a','test_currency_a','energy_a',1,'test_business' FROM fixture_context;
INSERT INTO ref_data.sources (catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review)
  SELECT catalog_id,revision_id,'fixture','synthetic','synthetic:reference-fixture-v1',TIMESTAMPTZ '2040-01-01 00:00:00+00','Project-authored synthetic fixture','redistributable' FROM fixture_context;
INSERT INTO ref_data.futures_products (catalog_id,revision_id,code,name,exchange_code)
  SELECT catalog_id,revision_id,'placeholder','Empty synthetic product template','test_exchange' FROM fixture_context;
INSERT INTO ref_data.futures_products (catalog_id,revision_id,code,name,definition_state,exchange_code,clearing_house_code,commodity_code,location_code,quote_currency_code,lot_unit_code,quote_unit_code,lot_basis,contract_size,quote_multiplier,outright_tick_size,settlement_method,market_clock_code,trading_calendar_code,delivery_calendar_code,settlement_calendar_code)
  SELECT catalog_id,revision_id,'test_gas_future','Synthetic fixed-lot gas future','synthetic','test_exchange','test_clearing','test_gas','test_hub_a','test_currency_a','energy_a','energy_a','fixed_total',40,1,0.025,'cash','test_utc','test_business','test_business','test_business' FROM fixture_context;
INSERT INTO ref_data.product_evidence (catalog_id,revision_id,product_code,source_code,fact_group)
  SELECT catalog_id,revision_id,'test_gas_future','fixture','units' FROM fixture_context;
INSERT INTO ref_data.futures_series (catalog_id,revision_id,code,product_code,series_label)
  SELECT catalog_id,revision_id,'placeholder_series','placeholder','Unspecified synthetic series' FROM fixture_context;
INSERT INTO ref_data.futures_series (catalog_id,revision_id,code,product_code,series_label,definition_state,delivery_from,delivery_to)
  SELECT catalog_id,revision_id,'test_gas_204101','test_gas_future','2041-01','synthetic',DATE '2041-01-01',DATE '2041-02-01' FROM fixture_context;
INSERT INTO ref_data.futures_milestones (catalog_id,revision_id,series_code,milestone,value_state)
  SELECT catalog_id,revision_id,'placeholder_series','first_trading','unknown' FROM fixture_context;
INSERT INTO ref_data.futures_milestones (catalog_id,revision_id,series_code,milestone,value_state,business_date,event_at,clock_code,source_code)
  SELECT catalog_id,revision_id,'test_gas_204101','first_trading','known',DATE '2039-12-01',TIMESTAMPTZ '2039-12-01 15:30:00+00','test_utc','fixture' FROM fixture_context;
INSERT INTO ref_data.futures_milestones (catalog_id,revision_id,series_code,milestone,value_state,business_date,event_at,clock_code,source_code)
  SELECT catalog_id,revision_id,'test_gas_204101','last_trading','known',DATE '2040-12-28',TIMESTAMPTZ '2040-12-28 15:30:00+00','test_utc','fixture' FROM fixture_context;
INSERT INTO ref_data.futures_milestones (catalog_id,revision_id,series_code,milestone,value_state,source_code)
  SELECT catalog_id,revision_id,'test_gas_204101','first_notice','not_applicable','fixture' FROM fixture_context;
INSERT INTO ref_data.currencies VALUES
 ('10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000002','only_other_revision','REV','Synthetic revision currency',2),
 ('10000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000001','only_other_catalog','CAT','Synthetic catalog currency',2);
COMMIT;
