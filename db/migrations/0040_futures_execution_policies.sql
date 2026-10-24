-- Execution qualification is exact-version reference configuration. Source
-- facts, contract identity and observation provenance remain independent.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE ref_data.futures_execution_policies(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,series_code ref_data.code NOT NULL,
 implementation text NOT NULL CHECK(implementation IN('fixed_total_cash_execution_pnl_v1','contract_lot_execution_pnl_v2')),
 eligibility_rule text NOT NULL CHECK(eligibility_rule IN('source_first_trade_instant_v1','after_source_first_trade_date_v1','explicit_utc_bound_v1')),
 eligible_from ref_data.instant NOT NULL,
 source_date_offset_seconds integer CHECK(abs(source_date_offset_seconds)<=50400 AND mod(source_date_offset_seconds,60)=0),
 source_date_tzdb_version text,
 CHECK((eligibility_rule='after_source_first_trade_date_v1' AND source_date_offset_seconds IS NOT NULL) OR
       (eligibility_rule<>'after_source_first_trade_date_v1' AND source_date_offset_seconds IS NULL AND source_date_tzdb_version IS NULL)),
 price_role text NOT NULL CHECK(price_role IN('daily_settlement','trader_mark')),
 price_source_code ref_data.code NOT NULL,evidence_source_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,series_code,implementation),
 FOREIGN KEY(catalog_id,revision_id,series_code) REFERENCES ref_data.futures_series(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,price_source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,evidence_source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);
COMMENT ON TABLE ref_data.futures_execution_policies IS 'Qualified execution eligibility and selected mark source/role, independent of instrument identity or generated/official observations. Does not enable VM, final settlement or delivery.';
-- Re-express already-retained public admissions before freezing the new table.
-- Their exact series keys, source dates, instants and original receipts stay put.
-- Historical source-name checks belong only to this legacy-format conversion.
CREATE TEMP TABLE futures_policy_source_upgrade ON COMMIT DROP AS
 SELECT DISTINCT a.catalog_id,a.revision_id,a.series_code,a.implementation,c.direct_source_code AS price_source_code
 FROM ref_data.futures_execution_admissions a
 JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)=(a.catalog_id,a.revision_id,a.series_code) AND k.kind='future'
 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k.catalog_id,k.revision_id,k.id)
 JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
 JOIN ref_data.sources price ON(price.catalog_id,price.revision_id,price.code)=(a.catalog_id,a.revision_id,c.direct_source_code)
 JOIN ref_data.sources evidence ON(evidence.catalog_id,evidence.revision_id,evidence.code)=(a.catalog_id,a.revision_id,a.qualification_source_code)
 WHERE(c.reference_catalog_id,c.reference_revision_id)=(a.catalog_id,a.revision_id)
 AND c.rule='direct' AND c.direct_role='trader_mark'
 AND price.kind='synthetic' AND price.locator LIKE 'generated:pilot-close-v1/%' AND price.content_sha256 IS NOT NULL
 AND evidence.kind='manual_entry' AND evidence.content_sha256 IS NOT NULL;
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM ref_data.futures_execution_admissions a
  LEFT JOIN futures_policy_source_upgrade source ON(source.catalog_id,source.revision_id,source.series_code,source.implementation)=(a.catalog_id,a.revision_id,a.series_code,a.implementation)
  GROUP BY a.catalog_id,a.revision_id,a.series_code,a.implementation HAVING count(source.price_source_code)<>1)
 THEN RAISE EXCEPTION 'futures_execution_policy_migration: retained qualification needs one exact qualified price source' USING ERRCODE='23514';END IF;
END$$;
INSERT INTO ref_data.futures_execution_policies
 SELECT a.catalog_id,a.revision_id,a.series_code,a.implementation,
  CASE a.eligibility_rule WHEN 'exchange_first_trade_instant_v1' THEN 'source_first_trade_instant_v1' ELSE a.eligibility_rule END,
  a.eligible_from,a.eligibility_offset_seconds,a.eligibility_tzdb_version,'trader_mark',source.price_source_code,a.qualification_source_code
 FROM ref_data.futures_execution_admissions a JOIN futures_policy_source_upgrade source
 ON(source.catalog_id,source.revision_id,source.series_code,source.implementation)=(a.catalog_id,a.revision_id,a.series_code,a.implementation);

CREATE TRIGGER frozen_definition BEFORE INSERT OR UPDATE OR DELETE ON ref_data.futures_execution_policies FOR EACH ROW EXECUTE FUNCTION ref_data.guard_definition();
ALTER TABLE ref_data.futures_execution_policies ENABLE ROW LEVEL SECURITY;
ALTER TABLE ref_data.futures_execution_policies FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON ref_data.futures_execution_policies FOR SELECT TO openetrm_runtime USING(security.reference_access(catalog_id,'ReadMarketData') AND security.reference_version_visible(catalog_id,revision_id));
GRANT SELECT ON ref_data.futures_execution_policies TO openetrm_runtime,openetrm_security_owner;

CREATE FUNCTION security.futures_execution_start(catalog uuid,revision uuid,series text,implementation text,mark_role text,mark_source text)
RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s ref_data.futures_series;p ref_data.futures_products;m ref_data.futures_milestones;
 policy ref_data.futures_execution_policies;clock ref_data.clock_rules;date_boundary timestamptz;midnight_candidates bigint;
BEGIN
 IF NOT security.reference_access(catalog,'ReadMarketData') OR NOT security.reference_version_visible(catalog,revision) THEN RAISE EXCEPTION 'futures_execution_policy' USING ERRCODE='42501';END IF;
 IF implementation IS NULL OR implementation NOT IN('fixed_total_cash_execution_pnl_v1','contract_lot_execution_pnl_v2') THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 SELECT * INTO s FROM ref_data.futures_series WHERE(catalog_id,revision_id,code)=(catalog,revision,series);
 SELECT * INTO p FROM ref_data.futures_products WHERE(catalog_id,revision_id,code)=(catalog,revision,s.product_code);
 SELECT * INTO m FROM ref_data.futures_milestones WHERE(catalog_id,revision_id,series_code,milestone)=(catalog,revision,series,'first_trading');
 IF coalesce(p.definition_state,'') NOT IN('candidate','synthetic') OR coalesce(s.definition_state,'') NOT IN('candidate','synthetic') OR m.series_code IS NULL THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
 SELECT * INTO policy FROM ref_data.futures_execution_policies ep WHERE(ep.catalog_id,ep.revision_id,ep.series_code,ep.implementation)=(catalog,revision,series,futures_execution_start.implementation);
 -- Retain the existing synthetic exact-instant convention when no policy exists.
 IF NOT FOUND THEN
  IF p.definition_state='synthetic' AND s.definition_state='synthetic' AND mark_role='daily_settlement' AND m.value_state='known' AND m.event_at IS NOT NULL THEN
   IF EXISTS(SELECT 1 FROM ref_data.futures_execution_policies ep WHERE(ep.catalog_id,ep.revision_id,ep.series_code)=(catalog,revision,series)
    AND(ep.eligibility_rule<>'source_first_trade_instant_v1' OR ep.eligible_from IS DISTINCT FROM m.event_at)) THEN RAISE EXCEPTION 'futures_execution_policy';END IF;
   RETURN m.event_at;
  END IF;
  RAISE EXCEPTION 'futures_execution_policy';
 END IF;
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
 IF executed IS NULL OR NOT isfinite(executed) OR valuation IS NULL OR NOT isfinite(valuation) OR (implementation IS NULL OR implementation NOT IN('fixed_total_cash_execution_pnl_v1','contract_lot_execution_pnl_v2'))
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
   (implementation='fixed_total_cash_execution_pnl_v1' AND(product.settlement_method<>'cash' OR product.lot_basis<>'fixed_total')) OR
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
  (implementation='fixed_total_cash_execution_pnl_v1' AND q.kind<>'outright') OR q.currency_code IS DISTINCT FROM product.quote_currency_code OR q.multiplier IS DISTINCT FROM product.quote_multiplier OR q.trading_tick IS DISTINCT FROM product.outright_tick_size OR
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

CREATE OR REPLACE FUNCTION market_data.validate_revision(cat uuid, rev uuid) RETURNS void
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM market_data.revisions m JOIN ref_data.revisions r
      ON (r.catalog_id,r.id) = (m.reference_catalog_id,m.reference_revision_id)
      WHERE m.catalog_id=cat AND m.id=rev AND r.state='sealed') THEN
    RAISE EXCEPTION 'Exact reference revision must be sealed' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curves c
      WHERE c.catalog_id=cat AND c.revision_id=rev AND (
        NOT EXISTS (SELECT 1 FROM market_data.curve_members m WHERE
          (m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)) OR
        (SELECT count(*) FROM market_data.curve_inputs i WHERE
          (i.catalog_id,i.revision_id,i.curve_code)=(c.catalog_id,c.revision_id,c.code))
          <> CASE c.rule WHEN 'direct' THEN 0 WHEN 'add' THEN 2 ELSE 3 END OR
        (c.rule='scale_add') <> EXISTS (SELECT 1 FROM market_data.curve_inputs h WHERE
          (h.catalog_id,h.revision_id,h.curve_code)=(c.catalog_id,c.revision_id,c.code) AND h.slot='multiplier'))) THEN
    RAISE EXCEPTION 'Curve members, slots or multiplier configuration are incomplete' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_inputs i
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(i.catalog_id,i.revision_id,i.curve_code)
      JOIN market_data.curves s ON (s.catalog_id,s.revision_id,s.code)=(i.catalog_id,i.revision_id,i.source_curve_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quote_specifications sq ON (sq.catalog_id,sq.revision_id,sq.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
      WHERE i.catalog_id=cat AND i.revision_id=rev AND (
        q.kind <> 'outright' OR c.profile_code <> s.profile_code OR
        (i.slot='spread' AND (sq.kind <> 'basis' OR
          (q.quantity_code,q.currency_code,q.multiplier) <> (sq.quantity_code,sq.currency_code,sq.multiplier))) OR
        (i.slot='base' AND (sq.kind <> 'outright' OR (c.rule='add' AND
          (q.quantity_code,q.currency_code,q.multiplier) <> (sq.quantity_code,sq.currency_code,sq.multiplier)))) OR
        (SELECT count(*) FROM market_data.constituent_mappings m WHERE
          (m.catalog_id,m.revision_id,m.curve_code,m.slot)=(i.catalog_id,i.revision_id,i.curve_code,i.slot)) <>
        (SELECT count(*) FROM market_data.curve_members m WHERE
          (m.catalog_id,m.revision_id,m.curve_code)=(i.catalog_id,i.revision_id,i.curve_code)))) THEN
    RAISE EXCEPTION 'Incompatible quote/profile types or incomplete mappings' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_members a
      JOIN market_data.curve_members b ON (b.catalog_id,b.revision_id,b.contract_id)=(a.catalog_id,a.revision_id,a.contract_id)
        AND a.curve_code < b.curve_code
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
      JOIN market_data.curves d ON (d.catalog_id,d.revision_id,d.code)=(b.catalog_id,b.revision_id,b.curve_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quote_specifications z ON (z.catalog_id,z.revision_id,z.code)=(d.reference_catalog_id,d.reference_revision_id,d.quote_code)
      WHERE a.catalog_id=cat AND a.revision_id=rev AND
        (c.commodity_code,c.location_code,c.profile_code,q.quantity_code,q.currency_code,q.multiplier)
        <> (d.commodity_code,d.location_code,d.profile_code,z.quantity_code,z.currency_code,z.multiplier)) THEN
    RAISE EXCEPTION 'Contract membership cannot change common economic specifications' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.constituent_mappings m
      JOIN market_data.contracts t ON (t.catalog_id,t.revision_id,t.id)=(m.catalog_id,m.revision_id,m.target_contract_id)
      JOIN market_data.contracts s ON (s.catalog_id,s.revision_id,s.id)=(m.catalog_id,m.revision_id,m.source_contract_id)
      JOIN market_data.curve_inputs i ON (i.catalog_id,i.revision_id,i.curve_code,i.slot)=(m.catalog_id,m.revision_id,m.curve_code,m.slot)
      JOIN market_data.curves sc ON (sc.catalog_id,sc.revision_id,sc.code)=(m.catalog_id,m.revision_id,m.source_curve_code)
      WHERE m.catalog_id=cat AND m.revision_id=rev AND (
        (t.delivery_from,t.delivery_to) <> (s.delivery_from,s.delivery_to) OR
        (i.expiry_rule='freeze_final_future' AND (s.kind <> 'future' OR sc.rule <> 'direct')))) THEN
    RAISE EXCEPTION 'Unsupported delivery mapping or frozen-final source' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (WITH RECURSIVE walk AS (
      SELECT curve_code AS root, source_curve_code AS node, ARRAY[curve_code] AS visited,
        source_curve_code=curve_code AS cycle FROM market_data.curve_inputs
        WHERE catalog_id=cat AND revision_id=rev
      UNION ALL
      SELECT w.root,i.source_curve_code,w.visited || w.node,i.source_curve_code=ANY(w.visited || w.node)
        FROM walk w JOIN market_data.curve_inputs i ON i.curve_code=w.node
        WHERE i.catalog_id=cat AND i.revision_id=rev AND NOT w.cycle)
      SELECT 1 FROM walk WHERE cycle) THEN
    RAISE EXCEPTION 'Curve dependency cycle' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_inputs m
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
      JOIN market_data.curve_inputs b ON (b.catalog_id,b.revision_id,b.curve_code)=(m.catalog_id,m.revision_id,m.curve_code) AND b.slot='base'
      JOIN market_data.curves source ON (source.catalog_id,source.revision_id,source.code)=(b.catalog_id,b.revision_id,b.source_curve_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quote_specifications sq ON (sq.catalog_id,sq.revision_id,sq.code)=(source.reference_catalog_id,source.reference_revision_id,source.quote_code)
      LEFT JOIN market_data.series series ON (series.catalog_id,series.revision_id,series.id)=(m.catalog_id,m.revision_id,m.source_series_id)
      LEFT JOIN ref_data.value_specifications v ON (v.catalog_id,v.revision_id,v.code)=
        (c.reference_catalog_id,c.reference_revision_id,COALESCE(m.value_specification_code,series.value_specification_code))
      WHERE m.catalog_id=cat AND m.revision_id=rev AND m.slot='multiplier' AND (
        v.code IS NULL OR (q.currency_code,q.multiplier) <> (sq.currency_code,sq.multiplier) OR
        (v.kind='quantity_ratio' AND (sq.quantity_code,q.quantity_code) <> (v.numerator_quantity_code,v.denominator_quantity_code)) OR
        (v.kind='dimensionless' AND sq.quantity_code <> q.quantity_code) OR
        (m.constant_value IS NOT NULL AND (m.constant_value < v.minimum OR m.constant_value > v.maximum)))) THEN
    RAISE EXCEPTION 'Multiplier value/type is incompatible with curve inputs/output' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_members member
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(member.catalog_id,member.revision_id,member.curve_code)
      WHERE c.catalog_id=cat AND c.revision_id=rev AND c.rule='direct' AND NOT EXISTS (
        SELECT 1 FROM market_data.series s WHERE (s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=
          (c.catalog_id,c.revision_id,member.contract_id,c.quote_code))) THEN
    RAISE EXCEPTION 'Direct constituents require exact price series' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.contracts k
      JOIN ref_data.futures_series s ON (s.catalog_id,s.revision_id,s.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)
      WHERE k.catalog_id=cat AND k.revision_id=rev AND (s.definition_state='placeholder' OR
        (s.delivery_from,s.delivery_to) IS DISTINCT FROM (k.delivery_from,k.delivery_to) OR
        NOT EXISTS (SELECT 1 FROM ref_data.futures_milestones x WHERE
          (x.catalog_id,x.revision_id,x.series_code)=(s.catalog_id,s.revision_id,s.code)
          AND x.milestone='last_trading' AND x.value_state='known' AND x.event_at=k.expiry_at))) THEN
    RAISE EXCEPTION 'Futures constituent must match a specified dated series' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_members m
      JOIN market_data.contracts k ON (k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
      JOIN ref_data.settlement_targets t ON (t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quantity_specifications z ON (z.catalog_id,z.revision_id,z.code)=(q.catalog_id,q.revision_id,q.quantity_code)
      JOIN ref_data.delivery_profiles dp ON (dp.catalog_id,dp.revision_id,dp.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
      LEFT JOIN ref_data.futures_series s ON (s.catalog_id,s.revision_id,s.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)
      LEFT JOIN ref_data.futures_products p ON (p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
      WHERE m.catalog_id=cat AND m.revision_id=rev AND (
        (t.kind='future_series' AND k.kind <> 'future') OR
        (k.kind='future' AND (p.definition_state='placeholder' OR t.kind <> 'future_series' OR q.kind <> CASE WHEN EXISTS(SELECT 1 FROM ref_data.futures_basis_settlements b WHERE(b.catalog_id,b.revision_id,b.series_code)=(s.catalog_id,s.revision_id,s.code)) THEN 'basis' ELSE 'outright' END OR
          NOT EXISTS(SELECT 1 FROM ref_data.futures_product_terms terms WHERE(terms.catalog_id,terms.revision_id,terms.product_code,terms.profile_code)=(p.catalog_id,p.revision_id,p.code,c.profile_code)) OR
          c.rule <> 'direct' OR (c.direct_role IS DISTINCT FROM 'daily_settlement' AND NOT EXISTS(SELECT 1 FROM ref_data.futures_execution_policies policy WHERE(policy.catalog_id,policy.revision_id,policy.series_code,policy.price_role,policy.price_source_code)=(s.catalog_id,s.revision_id,s.code,c.direct_role,c.direct_source_code))) OR
          (p.location_code IS NOT NULL AND p.location_code <> c.location_code) OR
          p.market_clock_code <> dp.clock_code OR p.delivery_calendar_code <> dp.calendar_code OR
          (p.commodity_code,p.quote_currency_code,p.quote_unit_code,p.quote_multiplier,p.outright_tick_size)
          IS DISTINCT FROM (c.commodity_code,q.currency_code,z.unit_code,q.multiplier,q.trading_tick))))) THEN
    RAISE EXCEPTION 'Constituent conflicts with common curve/futures specifications' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curves c
      JOIN ref_data.settlement_targets t ON (t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quantity_specifications z ON (z.catalog_id,z.revision_id,z.code)=(q.catalog_id,q.revision_id,q.quantity_code)
      JOIN ref_data.price_index_definitions x ON (x.catalog_id,x.revision_id)=(t.catalog_id,t.revision_id)
        AND x.code IN (t.index_a_code,t.index_b_code)
      WHERE c.catalog_id=cat AND c.revision_id=rev AND
        (x.commodity_code,x.currency_code,x.quote_unit_code,x.quote_multiplier)
        <> (c.commodity_code,q.currency_code,z.unit_code,q.multiplier)) THEN
    RAISE EXCEPTION 'Settlement index target has incompatible commodity/quote units' USING ERRCODE = '23514';
  END IF;
END;
$$;
COMMIT;
