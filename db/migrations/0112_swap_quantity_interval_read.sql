BEGIN;
-- Read one retained trade revision as a unit. Interval rows are immutable children
-- of that revision, so repeated authority resolution per hourly cell is unnecessary.
CREATE FUNCTION security.read_swap_power_quantity_intervals(catalog uuid,trade uuid,revision_number integer)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE retained trading.swap_trade_revisions;result jsonb;ref_catalog uuid;ref_revision uuid;BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE THEN
  RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';
 END IF;
 SELECT * INTO retained FROM trading.swap_trade_revisions r
  WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,trade,revision_number);
 IF NOT FOUND OR security.trading_visible(catalog,retained.publication_order,retained.effective_at) IS NOT TRUE THEN
  RAISE EXCEPTION 'No retained trade access' USING ERRCODE='42501';
 END IF;
 SELECT a.reference_catalog_id,a.reference_revision_id INTO ref_catalog,ref_revision
 FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,revision_number) ORDER BY a.slot LIMIT 1;
 IF ref_catalog IS NULL OR security.reference_version_visible(ref_catalog,ref_revision) IS NOT TRUE THEN
  RAISE EXCEPTION 'No exact reference access' USING ERRCODE='42501';
 END IF;
 -- Refuse malformed child provenance rather than bypassing its row policies.
 IF EXISTS(SELECT 1 FROM trading.swap_quantity_authoring a
  WHERE(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,revision_number)
   AND(a.publication_order,a.effective_at,a.reference_catalog_id,a.reference_revision_id)
    IS DISTINCT FROM(retained.publication_order,retained.effective_at,ref_catalog,ref_revision))
 OR EXISTS(SELECT 1 FROM trading.swap_power_quantity_intervals p
  WHERE(p.catalog_id,p.trade_id,p.revision)=(catalog,trade,revision_number)
   AND(p.publication_order,p.effective_at,p.reference_catalog_id,p.reference_revision_id)
    IS DISTINCT FROM(retained.publication_order,retained.effective_at,ref_catalog,ref_revision))
 OR EXISTS(SELECT 1 FROM trading.swap_quantity_amounts q
  WHERE(q.catalog_id,q.trade_id,q.revision)=(catalog,trade,revision_number)
   AND(q.publication_order,q.effective_at,q.reference_catalog_id,q.reference_revision_id)
    IS DISTINCT FROM(retained.publication_order,retained.effective_at,ref_catalog,ref_revision))
 THEN RAISE EXCEPTION 'scheduled_quantity_provenance';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('slot',p.slot,'period',p.period_ordinal,'ordinal',p.ordinal,
  'start',p.starts_at,'end',p.ends_at,'operatingDate',p.operating_date,
  'energyNumerator',p.energy_numerator::text,'energyDenominator',p.energy_denominator::text,
  'energyQuantity',q.energy_quantity_code) ORDER BY p.slot,p.period_ordinal,p.ordinal),'[]'::jsonb)
 INTO result FROM trading.swap_power_quantity_intervals p JOIN trading.swap_quantity_amounts q
  ON(q.catalog_id,q.trade_id,q.revision,q.slot,q.ordinal)=(p.catalog_id,p.trade_id,p.revision,p.slot,p.period_ordinal)
 WHERE(p.catalog_id,p.trade_id,p.revision)=(catalog,trade,revision_number);
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR
  security.trading_visible(catalog,retained.publication_order,retained.effective_at) IS NOT TRUE OR
  security.reference_version_visible(ref_catalog,ref_revision) IS NOT TRUE
 THEN RAISE EXCEPTION 'Retained quantity authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) TO openetrm_runtime;
COMMIT;
