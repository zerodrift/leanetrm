-- Wakeups carry no identifiers or financial data. Existing serialized
-- publication streams are the durable catch-up record; notifications are hints.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.notify_calculation_change() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN PERFORM pg_notify('openetrm_calculation_changes','');RETURN NULL;END $$;
REVOKE ALL ON FUNCTION security.notify_calculation_change() FROM PUBLIC;
CREATE TRIGGER calculation_change AFTER INSERT ON market_data.knowledge_events
FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();
CREATE TRIGGER calculation_change AFTER INSERT ON trading.publications
FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();
CREATE TRIGGER calculation_change AFTER UPDATE OF state ON market_data.date_schedule_versions
FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();
CREATE TRIGGER calculation_change AFTER UPDATE OF state ON market_data.custom_curve_versions
FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();
CREATE TRIGGER calculation_change AFTER UPDATE OF state ON market_data.contract_date_versions
FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();
CREATE TRIGGER calculation_change AFTER UPDATE ON security.epoch
FOR EACH STATEMENT EXECUTE FUNCTION security.notify_calculation_change();

CREATE FUNCTION security.calculation_head(catalog uuid) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'calculation_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_array(
  (SELECT coalesce(max(event_order),0) FROM market_data.knowledge_events WHERE catalog_id=catalog),
  (SELECT coalesce(max(publication_order),0) FROM market_data.date_schedule_versions WHERE catalog_id=catalog AND state='published'),
  CASE WHEN security.trading_access(catalog,'ReadTrades') THEN
   (SELECT coalesce(max(publication_order),0) FROM trading.publications WHERE catalog_id=catalog) ELSE NULL END,
  (SELECT coalesce(max(publication_order),0) FROM market_data.custom_curve_versions WHERE catalog_id=catalog AND state='published'),
  (SELECT coalesce(max(publication_order),0) FROM market_data.contract_date_versions WHERE catalog_id=catalog AND state='published'),
  (SELECT coalesce(jsonb_agg(code ORDER BY code),'[]'::jsonb) FROM security.permissions WHERE security.has_permission(code)),
  (SELECT coalesce(jsonb_agg(s.catalog_id ORDER BY s.catalog_id),'[]'::jsonb)
   FROM security.reference_catalog_scopes s JOIN security.request_contexts c USING(tenant_id,workspace_id)
   WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
    AND security.reference_access(s.catalog_id,'ReadMarketData')),
  (SELECT coalesce(jsonb_agg(s.catalog_id ORDER BY s.catalog_id),'[]'::jsonb)
   FROM security.market_catalog_scopes s JOIN security.request_contexts c USING(tenant_id,workspace_id)
   WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
    AND security.market_access(s.catalog_id,'ReadMarketData')))::text;
END $$;
ALTER FUNCTION security.calculation_head(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.calculation_head(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.calculation_head(uuid) TO openetrm_runtime;
COMMIT;
