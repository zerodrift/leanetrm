-- The Blotter's browser reuse check consumes this head. It is not an access grant or a temporal cutoff.
-- Track committed input publications, not captured contexts/receipts: reads must not invalidate other reads.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.blotter_head(catalog uuid) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result text;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'blotter_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts
 WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.id IS NULL THEN RAISE EXCEPTION 'blotter_denied' USING ERRCODE='42501';END IF;
 result=jsonb_build_array('blotter_head_v1',c.actor_id,c.tenant_id,c.workspace_id,
  security.calculation_head(catalog)::jsonb,
  (SELECT generation FROM security.epoch WHERE id),
  (SELECT coalesce(max(sequence),0) FROM power_data.batches WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM market_data.market_closes WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM automation.pilot_reports WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM market_data.interval_series_revisions WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM settlement_data.meter_finality_heads WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM risk.definition_run_publications WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM market_data.gas_setup_versions WHERE catalog_id=catalog AND state='published'),
  (SELECT count(*) FROM market_data.contract_date_admissions WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM market_data.crude_setup_versions WHERE catalog_id=catalog AND state='published'),
  (SELECT coalesce(sum(revision::bigint),0) FROM presentation.trade_amount_formats WHERE catalog_id=catalog),
  (SELECT count(*) FROM presentation.reference_display_versions WHERE tenant_id=c.tenant_id AND workspace_id=c.workspace_id
    AND security.reference_access(catalog_id,'ReadMarketData')))::text;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'blotter_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END $$;
ALTER FUNCTION security.blotter_head(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.blotter_head(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.blotter_head(uuid) TO openetrm_runtime;
COMMIT;
