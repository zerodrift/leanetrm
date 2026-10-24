-- Verify the selected eligibility boundary against the actual declared zone/date.
-- Retain source facts; reject conflicting receipts rather than inventing instants.
BEGIN;
CREATE OR REPLACE FUNCTION security.futures_eligible_from(catalog uuid,revision uuid,series text,implementation text,mark_role text,mark_source text)
RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s ref_data.futures_series;p ref_data.futures_products;m ref_data.futures_milestones;
 a ref_data.futures_execution_admissions;c ref_data.clock_rules;expected timestamptz;
BEGIN
 IF NOT security.reference_access(catalog,'ReadMarketData') OR NOT security.reference_version_visible(catalog,revision) THEN RAISE EXCEPTION 'futures_admission' USING ERRCODE='42501';END IF;
 SELECT * INTO s FROM ref_data.futures_series WHERE(catalog_id,revision_id,code)=(catalog,revision,series);
 SELECT * INTO p FROM ref_data.futures_products WHERE(catalog_id,revision_id,code)=(catalog,revision,s.product_code);
 SELECT * INTO m FROM ref_data.futures_milestones WHERE(catalog_id,revision_id,series_code,milestone)=(catalog,revision,series,'first_trading') AND value_state='known';
 IF p.definition_state='synthetic' AND s.definition_state='synthetic' AND mark_role='daily_settlement' AND m.event_at IS NOT NULL THEN
  IF implementation IS NULL OR implementation NOT IN('fixed_total_cash_execution_pnl_v1','contract_lot_execution_pnl_v2') OR EXISTS(
   SELECT 1 FROM ref_data.futures_execution_admissions admitted
   WHERE(admitted.catalog_id,admitted.revision_id,admitted.series_code,admitted.implementation)=(catalog,revision,series,'contract_lot_execution_pnl_v2')
    AND(admitted.eligibility_rule IS DISTINCT FROM 'exchange_first_trade_instant_v1' OR admitted.eligible_from IS DISTINCT FROM m.event_at))
  THEN RAISE EXCEPTION 'futures_admission';END IF;
  RETURN m.event_at;
 END IF;
 SELECT * INTO a FROM ref_data.futures_execution_admissions WHERE(catalog_id,revision_id,series_code,futures_execution_admissions.implementation)=(catalog,revision,series,futures_eligible_from.implementation);
 IF coalesce(p.definition_state,'')<>'candidate' OR coalesce(s.definition_state,'')<>'candidate'
 OR implementation IS DISTINCT FROM 'contract_lot_execution_pnl_v2' OR mark_role IS DISTINCT FROM 'trader_mark'
 OR a.usage_policy IS DISTINCT FROM 'generated_prices_sample_trades_v1' OR a.execution_venue IS DISTINCT FROM 'central_limit_order_book'
 OR NOT EXISTS(SELECT 1 FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(catalog,revision,mark_source) AND kind='synthetic' AND locator LIKE 'generated:pilot-close-v1/%' AND content_sha256 IS NOT NULL)
 OR NOT EXISTS(SELECT 1 FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(catalog,revision,a.qualification_source_code) AND kind='manual_entry' AND content_sha256 IS NOT NULL)
 THEN RAISE EXCEPTION 'futures_admission';END IF;
 IF a.eligibility_rule='exchange_first_trade_instant_v1' THEN
  IF m.event_at IS NULL OR a.eligible_from IS DISTINCT FROM m.event_at THEN RAISE EXCEPTION 'futures_admission';END IF;
 ELSIF a.eligibility_rule='after_source_first_trade_date_v1' THEN
  SELECT * INTO c FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(catalog,revision,m.clock_code);
  IF m.event_at IS NOT NULL OR m.business_date IS NULL OR m.business_date<DATE '2000-01-01' OR m.business_date>=DATE '2100-01-01' THEN RAISE EXCEPTION 'futures_admission';END IF;
  IF a.eligibility_offset_seconds IS NULL THEN RAISE EXCEPTION 'futures_admission';END IF;
  IF c.kind='fixed_offset' AND c.fixed_offset_seconds=0 AND a.eligibility_offset_seconds=0 AND a.eligibility_tzdb_version IS NULL THEN NULL;
  ELSIF c.kind='iana' AND c.iana_zone IN('America/New_York','America/Chicago','America/Los_Angeles') AND c.tzdb_version IS NOT NULL AND a.eligibility_tzdb_version IS NOT DISTINCT FROM c.tzdb_version THEN NULL;
  ELSE RAISE EXCEPTION 'futures_admission';END IF;
  IF c.kind='iana' THEN
   expected=((m.business_date+1)::timestamp AT TIME ZONE c.iana_zone);
   IF a.eligibility_offset_seconds IS DISTINCT FROM
    extract(epoch FROM ((m.business_date+1)::timestamp-(expected AT TIME ZONE 'UTC')))::integer
   THEN RAISE EXCEPTION 'futures_admission';END IF;
  ELSE
   expected=((m.business_date+1)::timestamp AT TIME ZONE 'UTC');
  END IF;
  IF a.eligible_from IS DISTINCT FROM expected THEN RAISE EXCEPTION 'futures_admission';END IF;
 ELSE RAISE EXCEPTION 'futures_admission';END IF;
 RETURN a.eligible_from;
END$$;
COMMIT;
