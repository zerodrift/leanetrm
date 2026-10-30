-- One executable futures execution contract. Existing sealed policies and booked
-- histories remain unchanged; obsolete conventions and missing policies refuse.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE OR REPLACE FUNCTION security.futures_execution_start(catalog uuid,revision uuid,series text,implementation text,mark_role text,mark_source text)
RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s ref_data.futures_series;p ref_data.futures_products;m ref_data.futures_milestones;
 policy ref_data.futures_execution_policies;clock ref_data.clock_rules;date_boundary timestamptz;midnight_candidates bigint;
BEGIN
 IF NOT security.reference_access(catalog,'ReadMarketData') OR NOT security.reference_version_visible(catalog,revision) THEN RAISE EXCEPTION 'futures_execution_policy' USING ERRCODE='42501';END IF;
 IF implementation IS NULL OR implementation<>'contract_lot_execution_pnl_v2' THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 SELECT * INTO s FROM ref_data.futures_series WHERE(catalog_id,revision_id,code)=(catalog,revision,series);
 SELECT * INTO p FROM ref_data.futures_products WHERE(catalog_id,revision_id,code)=(catalog,revision,s.product_code);
 SELECT * INTO m FROM ref_data.futures_milestones WHERE(catalog_id,revision_id,series_code,milestone)=(catalog,revision,series,'first_trading');
 IF coalesce(p.definition_state,'') NOT IN('candidate','synthetic') OR coalesce(s.definition_state,'') NOT IN('candidate','synthetic') OR m.series_code IS NULL THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 SELECT * INTO policy FROM ref_data.futures_execution_policies ep WHERE(ep.catalog_id,ep.revision_id,ep.series_code,ep.implementation)=(catalog,revision,series,futures_execution_start.implementation);
 IF NOT FOUND THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 IF policy.price_role IS DISTINCT FROM mark_role OR policy.price_source_code IS DISTINCT FROM mark_source OR
  (m.value_state='known' AND m.event_at IS NOT NULL AND policy.eligible_from<m.event_at) OR
  NOT EXISTS(SELECT 1 FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(catalog,revision,policy.price_source_code)) OR
  NOT EXISTS(SELECT 1 FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(catalog,revision,policy.evidence_source_code)) OR
  (policy.eligibility_rule='source_first_trade_instant_v1' AND(m.value_state IS DISTINCT FROM 'known' OR policy.eligible_from IS DISTINCT FROM m.event_at))
 THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 IF policy.eligibility_rule='after_source_first_trade_date_v1' THEN
  IF m.value_state IS DISTINCT FROM 'known' OR m.event_at IS NOT NULL OR m.business_date IS NULL THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
  SELECT * INTO clock FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(catalog,revision,m.clock_code);
  IF clock.kind='fixed_offset' AND policy.source_date_offset_seconds=clock.fixed_offset_seconds AND policy.source_date_tzdb_version IS NULL THEN NULL;
  ELSIF clock.kind='iana' AND clock.tzdb_version IS NOT NULL AND policy.source_date_tzdb_version IS NOT DISTINCT FROM clock.tzdb_version THEN NULL;
  ELSE RAISE EXCEPTION 'futures_execution_policy';END IF;
  -- Retained, qualified offset proof avoids recomputing with database-host tzdb.
  -- Managed qualification checks it against the exact pinned reference clock.
  date_boundary=((m.business_date+1)::timestamp AT TIME ZONE 'UTC')-make_interval(secs=>policy.source_date_offset_seconds);
  -- Additional consistency rejection only: never use database tzdb to replace
  -- the explicitly retained bound or offset. A rules disagreement fails closed.
  IF clock.kind='iana' THEN
   IF(date_boundary AT TIME ZONE clock.iana_zone) IS DISTINCT FROM (m.business_date+1)::timestamp THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
   -- Every representable policy offset is minute-aligned within fourteen hours.
   -- Reject a repeated midnight rather than choosing either of its UTC instants.
   SELECT count(*) INTO midnight_candidates FROM generate_series(-840,840) AS candidate(minutes)
   WHERE((((m.business_date+1)::timestamp AT TIME ZONE 'UTC')-make_interval(mins=>candidate.minutes)) AT TIME ZONE clock.iana_zone)=(m.business_date+1)::timestamp;
   IF midnight_candidates<>1 THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
  END IF;
  IF policy.eligible_from<date_boundary THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 ELSIF policy.eligibility_rule='explicit_utc_bound_v1' AND m.value_state='known' AND m.event_at IS NULL THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 RETURN policy.eligible_from;
END$$;
ALTER FUNCTION security.futures_execution_start(uuid,uuid,text,text,text,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.futures_execution_start(uuid,uuid,text,text,text,text) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.prepare_futures_binding(catalog uuid,book uuid,revision uuid,curve text,contract uuid,
 valuation timestamptz,executed timestamptz,lots numeric,execution numeric,implementation text,ordinal integer)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q ref_data.quote_specifications;product ref_data.futures_products;series_row ref_data.futures_series;
 contract_row market_data.contracts;curve_row market_data.curves;definition_from timestamptz;starts timestamptz;ends timestamptz;side uuid;instrument uuid;
BEGIN
 IF executed IS NULL OR NOT isfinite(executed) OR valuation IS NULL OR NOT isfinite(valuation) OR (implementation IS NULL OR implementation<>'contract_lot_execution_pnl_v2')
 THEN RAISE EXCEPTION 'futures_input' USING ERRCODE='P0001'; END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=executed AND p.effective_at<=executed) THEN RAISE EXCEPTION 'futures_book' USING ERRCODE='P0001'; END IF;
 IF lots IS NULL OR lots<>trunc(lots) OR lots=0 OR abs(lots)>100000 THEN RAISE EXCEPTION 'futures_lots' USING ERRCODE='P0001'; END IF;
 SELECT * INTO contract_row FROM market_data.contracts WHERE catalog_id=catalog AND revision_id=revision AND id=contract;
 SELECT * INTO curve_row FROM market_data.curves WHERE catalog_id=catalog AND revision_id=revision AND code=curve;
 IF contract_row.kind IS DISTINCT FROM 'future' OR curve_row.rule IS DISTINCT FROM 'direct' OR coalesce(curve_row.direct_role,'') NOT IN('daily_settlement','trader_mark') OR
  NOT EXISTS(SELECT 1 FROM market_data.curve_members WHERE catalog_id=catalog AND revision_id=revision AND curve_code=curve AND contract_id=contract) THEN RAISE EXCEPTION 'futures_definition' USING ERRCODE='P0001'; END IF;
 IF NOT security.reference_access(contract_row.reference_catalog_id,'ReadMarketData') OR NOT security.reference_version_visible(contract_row.reference_catalog_id,contract_row.reference_revision_id) THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501'; END IF;
 SELECT p.* INTO product FROM ref_data.futures_series s JOIN ref_data.futures_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
 WHERE(s.catalog_id,s.revision_id,s.code)=(contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code) AND s.definition_state IN('synthetic','candidate');
 IF coalesce(product.definition_state,'') NOT IN('synthetic','candidate') OR product.settlement_method NOT IN('cash','physical') OR
   (product.lot_basis='fixed_total' AND product.lot_unit_code IS DISTINCT FROM product.quote_unit_code)
 THEN RAISE EXCEPTION 'futures_convention' USING ERRCODE='P0001'; END IF;
 SELECT * INTO series_row FROM ref_data.futures_series WHERE(catalog_id,revision_id,code)=(contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code);
 IF series_row.delivery_from IS DISTINCT FROM contract_row.delivery_from OR series_row.delivery_to IS DISTINCT FROM contract_row.delivery_to OR
  curve_row.commodity_code IS DISTINCT FROM product.commodity_code OR curve_row.location_code IS DISTINCT FROM product.location_code OR
  NOT EXISTS(SELECT 1 FROM ref_data.futures_product_terms t WHERE(t.catalog_id,t.revision_id,t.product_code,t.profile_code)=(product.catalog_id,product.revision_id,product.code,curve_row.profile_code))
 THEN RAISE EXCEPTION 'futures_definition' USING ERRCODE='P0001'; END IF;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(curve_row.reference_catalog_id,curve_row.reference_revision_id,curve_row.quote_code);
 IF EXISTS(SELECT 1 FROM ref_data.futures_basis_settlements b
   JOIN ref_data.futures_series benchmark ON(benchmark.catalog_id,benchmark.revision_id,benchmark.code)=(b.catalog_id,b.revision_id,b.benchmark_series_code)
   JOIN ref_data.futures_products bp ON(bp.catalog_id,bp.revision_id,bp.code)=(benchmark.catalog_id,benchmark.revision_id,benchmark.product_code)
   JOIN ref_data.price_index_definitions i ON(i.catalog_id,i.revision_id,i.code)=(b.catalog_id,b.revision_id,b.index_code)
   WHERE(b.catalog_id,b.revision_id,b.series_code)=(series_row.catalog_id,series_row.revision_id,series_row.code) AND
   (product.settlement_method<>'cash' OR EXISTS(SELECT 1 FROM ref_data.futures_product_terms t WHERE(t.catalog_id,t.revision_id,t.product_code)=(product.catalog_id,product.revision_id,product.code) AND t.index_code IS NOT NULL) OR (benchmark.delivery_from,benchmark.delivery_to) IS DISTINCT FROM(series_row.delivery_from,series_row.delivery_to) OR
    (bp.commodity_code,bp.quote_currency_code,bp.quote_unit_code,bp.quote_multiplier) IS DISTINCT FROM(product.commodity_code,product.quote_currency_code,product.quote_unit_code,product.quote_multiplier) OR
    (i.commodity_code,i.location_code,i.currency_code,i.quote_unit_code,i.quote_multiplier) IS DISTINCT FROM(product.commodity_code,product.location_code,product.quote_currency_code,product.quote_unit_code,product.quote_multiplier) OR
    EXISTS(SELECT 1 FROM ref_data.futures_basis_settlements bb WHERE(bb.catalog_id,bb.revision_id,bb.series_code)=(benchmark.catalog_id,benchmark.revision_id,benchmark.code))))
 THEN RAISE EXCEPTION 'futures_definition' USING ERRCODE='P0001'; END IF;
 IF q.kind IS DISTINCT FROM (CASE WHEN EXISTS(SELECT 1 FROM ref_data.futures_basis_settlements b WHERE(b.catalog_id,b.revision_id,b.series_code)=(series_row.catalog_id,series_row.revision_id,series_row.code)) THEN 'basis' ELSE 'outright' END) OR
  q.currency_code IS DISTINCT FROM product.quote_currency_code OR q.multiplier IS DISTINCT FROM product.quote_multiplier OR q.trading_tick IS DISTINCT FROM product.outright_tick_size OR
  (SELECT count(*) FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,commodity_code,unit_code)=(q.catalog_id,q.revision_id,product.commodity_code,product.quote_unit_code))<>1 OR
  NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code,commodity_code,unit_code)=(q.catalog_id,q.revision_id,q.quantity_code,product.commodity_code,product.quote_unit_code))
 THEN RAISE EXCEPTION 'futures_convention' USING ERRCODE='P0001'; END IF;
 IF product.lot_basis='fixed_total' THEN
  IF EXISTS(SELECT 1 FROM ref_data.futures_lot_resolutions WHERE(catalog_id,revision_id,series_code)=(series_row.catalog_id,series_row.revision_id,series_row.code)) THEN RAISE EXCEPTION 'futures_lot' USING ERRCODE='P0001'; END IF;
 ELSE
  IF NOT EXISTS(SELECT 1 FROM ref_data.futures_lot_resolutions r WHERE(r.catalog_id,r.revision_id,r.series_code,r.quantity_code)=(series_row.catalog_id,series_row.revision_id,series_row.code,q.quantity_code)
    AND r.amount=product.contract_size*r.factor AND
    (r.implementation='published_contract_size_v1' OR(r.implementation='calendar_delivery_days_v1' AND product.lot_basis='per_delivery_day' AND r.factor=series_row.delivery_to-series_row.delivery_from)))
  THEN RAISE EXCEPTION 'futures_lot' USING ERRCODE='P0001'; END IF;
 END IF;
 starts=security.futures_execution_start(contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code,implementation,curve_row.direct_role,curve_row.direct_source_code);
 SELECT event_at INTO ends FROM ref_data.futures_milestones WHERE(catalog_id,revision_id,series_code,milestone)=(contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code,'last_trading') AND value_state='known';
 IF ends IS DISTINCT FROM contract_row.expiry_at THEN RAISE EXCEPTION 'futures_definition' USING ERRCODE='P0001'; END IF;
 IF starts IS NULL OR executed<starts OR executed>=ends OR executed>valuation OR valuation<starts OR valuation>=ends OR execution IS NULL OR execution<q.minimum OR execution>q.maximum OR mod(execution,product.outright_tick_size)<>0 THEN RAISE EXCEPTION 'futures_execution' USING ERRCODE='P0001'; END IF;
 SELECT (effective_from::timestamp AT TIME ZONE 'UTC') INTO definition_from FROM market_data.revision_periods WHERE catalog_id=catalog AND revision_id=revision AND effective_from<=(executed AT TIME ZONE 'UTC')::date AND(executed AT TIME ZONE 'UTC')::date<effective_to;
 IF definition_from IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE catalog_id=catalog AND id=revision AND state='sealed') THEN RAISE EXCEPTION 'futures_definition' USING ERRCODE='P0001'; END IF;
 instrument=security.futures_contract_id(contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code);
 IF EXISTS(SELECT 1 FROM trading.instruments i WHERE i.catalog_id=catalog AND i.id=instrument) THEN
  IF NOT EXISTS(SELECT 1 FROM trading.futures_contract_references f WHERE f.catalog_id=catalog AND f.instrument_id=instrument
   AND(f.reference_catalog_id,f.reference_revision_id,f.series_code)=(contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code))
  THEN RAISE EXCEPTION 'futures_identity' USING ERRCODE='P0001'; END IF;
 ELSE
  INSERT INTO trading.instruments VALUES(catalog,instrument,ordinal,definition_from);
  INSERT INTO trading.futures_contract_references VALUES(catalog,instrument,contract_row.reference_catalog_id,contract_row.reference_revision_id,contract_row.futures_series_code,ordinal,definition_from);
 END IF;
 RETURN instrument;
END $$;

COMMIT;
