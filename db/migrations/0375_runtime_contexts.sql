-- Runtime-only financial coordinates. Populated installations require the
-- explicitly selected reset/reseed procedure; no old identifier is converted.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='60s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM market_data.temporal_contexts)
 OR EXISTS(SELECT 1 FROM risk.pending_clock_attestations)
 OR EXISTS(SELECT 1 FROM security.request_contexts q WHERE
  num_nonnulls(q.calendar_cut,q.market_catalog_id,q.market_context_id,q.trading_as_of,q.trading_business_date,
   q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on,q.business_source_until)>0)
 THEN RAISE EXCEPTION 'runtime_context_reset_required' USING ERRCODE='55000';END IF;
END$$;

CREATE TYPE security.operation_read_coordinates AS (
 catalog_id uuid,id uuid,effective_on date,known_at timestamptz,schedule_prefix integer,
 coverage_from timestamptz,market_prefix integer,catalogue_prefix integer,trading_prefix integer,
 certified_through timestamptz,system_id text,timeline integer
);
ALTER TABLE security.request_contexts ADD COLUMN read_sources security.operation_read_coordinates[],
 ADD CONSTRAINT operation_read_source_bound CHECK(read_sources IS NULL OR cardinality(read_sources)<=4096);
CREATE FUNCTION security.require_read_sources_released() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=NEW.id AND
  num_nonnulls(q.read_sources,q.calendar_cut,q.market_catalog_id,q.market_context_id,q.trading_as_of,q.trading_business_date,
   q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on,q.business_source_until)>0)
 THEN RAISE EXCEPTION 'read_source_not_released' USING ERRCODE='23514';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION security.require_read_sources_released() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_read_sources_released() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER read_sources_released AFTER INSERT OR UPDATE ON security.request_contexts
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.require_read_sources_released();

-- These values exist only in the calling transaction. There is no persistent
-- arm, owner union or identifier lookup registry behind any projection.
CREATE VIEW security.operation_read_inputs AS
 SELECT s.* FROM security.request_contexts q CROSS JOIN LATERAL unnest(q.read_sources) s
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
 AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
 AND security.market_access(s.catalog_id,'ReadMarketData') IS TRUE
 AND(q.market_context_id IS NULL OR s.catalog_id=q.market_catalog_id AND s.known_at<=
  (SELECT p.known_at FROM unnest(q.read_sources) p WHERE(p.catalog_id,p.id)=(q.market_catalog_id,q.market_context_id)));
CREATE VIEW security.operation_knowledge WITH(security_invoker=true) AS
 SELECT catalog_id,id,effective_on,known_at,schedule_prefix AS known_through,coverage_from,certified_through
 FROM security.operation_read_inputs;
CREATE VIEW security.operation_markets WITH(security_invoker=true) AS
 SELECT catalog_id,id AS context_id,market_prefix AS known_through FROM security.operation_read_inputs;
CREATE VIEW security.operation_catalogues WITH(security_invoker=true) AS
 SELECT catalog_id,id AS context_id,catalogue_prefix AS known_through FROM security.operation_read_inputs;
CREATE VIEW security.operation_trading WITH(security_invoker=true) AS
 SELECT catalog_id,id AS context_id,trading_prefix AS known_through FROM security.operation_read_inputs;
ALTER VIEW security.operation_read_inputs OWNER TO openetrm_security_owner;
ALTER VIEW security.operation_knowledge OWNER TO openetrm_security_owner;
ALTER VIEW security.operation_markets OWNER TO openetrm_security_owner;
ALTER VIEW security.operation_catalogues OWNER TO openetrm_security_owner;
ALTER VIEW security.operation_trading OWNER TO openetrm_security_owner;
GRANT SELECT ON security.operation_read_inputs,security.operation_knowledge,
 security.operation_markets,security.operation_catalogues,security.operation_trading
 TO openetrm_security_owner,openetrm_runtime,openetrm_risk_worker;

-- Private derivation for an already authorized owner. It reads immutable fact
-- journals, never another owner's coordinates or a saved context object.
CREATE FUNCTION security.coordinates_from_inputs(catalog uuid,day date,cutoff timestamptz,
 system_id text,timeline integer) RETURNS security.operation_read_coordinates
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c market_data.knowledge_clocks;result security.operation_read_coordinates;
BEGIN
 SELECT * INTO STRICT c FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog;
 IF day IS NULL OR day<DATE '0001-01-01' OR day>=DATE '9999-12-31'
 OR cutoff IS NULL OR NOT isfinite(cutoff) OR system_id IS NULL OR timeline IS NULL
 OR c.system_identifier IS DISTINCT FROM system_id OR c.timeline_id IS DISTINCT FROM timeline
 OR system_id IS DISTINCT FROM (pg_control_system()).system_identifier::text
 OR timeline IS DISTINCT FROM (pg_control_checkpoint()).timeline_id
 OR current_setting('track_commit_timestamp')<>'on' OR pg_is_in_recovery()
 OR c.coverage_from IS NULL OR cutoff NOT BETWEEN c.coverage_from AND c.certified_through
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 SELECT catalog,security.workspace_context_id(catalog,day,cutoff),day,cutoff,
  coalesce((SELECT max(k.publication_order) FROM market_data.knowledge_publications k WHERE k.catalog_id=catalog AND k.known_from<=cutoff),0),
  c.coverage_from,
  coalesce((SELECT max(k.event_order) FROM market_data.market_knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff),0),
  coalesce((SELECT max(k.publication_order) FROM market_data.catalogue_knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff),0),
  coalesce((SELECT max(k.publication_order) FROM trading.knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff),0),
  c.certified_through,c.system_identifier,c.timeline_id INTO result;
 RETURN result;
END$$;
ALTER FUNCTION security.coordinates_from_inputs(uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.coordinates_from_inputs(uuid,date,timestamptz,text,integer) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.register_read_source(catalog uuid,identifier uuid,day date,
 cutoff timestamptz,prefix integer,system_id text,timeline integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;source security.operation_read_coordinates;
 previous security.operation_read_coordinates;parent security.operation_read_coordinates;j risk.jobs;lease uuid;
BEGIN
 SELECT * INTO STRICT q FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
 AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'read_source_denied' USING ERRCODE='42501';END IF;
 source=security.coordinates_from_inputs(catalog,day,cutoff,system_id,timeline);
 IF source.id IS DISTINCT FROM identifier OR source.schedule_prefix IS DISTINCT FROM prefix
 THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 IF q.market_context_id IS NOT NULL THEN
  SELECT s.* INTO STRICT parent FROM unnest(q.read_sources) s
  WHERE(s.catalog_id,s.id)=(q.market_catalog_id,q.market_context_id);
  IF catalog IS DISTINCT FROM parent.catalog_id OR cutoff>parent.known_at
  THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  SELECT b.lease_token INTO lease FROM risk.worker_attempt_contexts b WHERE b.context_id=q.id;
  SELECT jobs.* INTO STRICT j FROM risk.worker_attempt_contexts b JOIN risk.jobs jobs ON jobs.id=b.job_id WHERE b.context_id=q.id;
  PERFORM risk.owned_attempt(j.id,lease,false,false);
  IF catalog IS DISTINCT FROM j.catalog_id OR parent.id IS NULL OR cutoff>parent.known_at
   OR(identifier IS DISTINCT FROM parent.id AND(j.frame->'ppaReadContexts' @> jsonb_build_array(identifier)) IS NOT TRUE)
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 END IF;
 IF EXISTS(SELECT 1 FROM automation.pilot_run_contexts p WHERE p.context_id=q.id)
  AND automation.pilot_context_live(q.id) IS NOT TRUE
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 SELECT s.* INTO previous FROM unnest(q.read_sources) s WHERE(s.catalog_id,s.id)=(catalog,identifier);
 IF FOUND THEN
  IF (previous.effective_on,previous.known_at,previous.schedule_prefix,previous.market_prefix,
      previous.catalogue_prefix,previous.trading_prefix,previous.system_id,previous.timeline)
   IS DISTINCT FROM(source.effective_on,source.known_at,source.schedule_prefix,source.market_prefix,
      source.catalogue_prefix,source.trading_prefix,source.system_id,source.timeline)
  THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
  RETURN;
 END IF;
 IF coalesce(cardinality(q.read_sources),0)>=4096 THEN RAISE EXCEPTION 'read_source_limit' USING ERRCODE='P0001';END IF;
 UPDATE security.request_contexts r SET read_sources=array_append(r.read_sources,source) WHERE r.id=q.id;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'read_source_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.register_read_source(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.register_read_source(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.register_read_source(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime,openetrm_risk_worker;

-- Owner fields and complete consumer changes follow below. The final assertion
-- refuses installation if a runtime definition still requires a retired store.

-- Certify immutable publication journals under their shared commit gate.
-- This operation creates no financial-context record.
CREATE FUNCTION security.certify_knowledge(catalog uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $certify$
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
DECLARE certify_or_capture_date_row record;certify_or_capture_date_last_commit timestamptz;certify_or_capture_date_commit_at timestamptz;certify_or_capture_date_certified timestamptz;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certify_or_capture_date_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO certify_or_capture_date_last_commit FROM market_data.catalogue_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR certify_or_capture_date_row IN SELECT p.* FROM market_data.catalogue_publications p LEFT JOIN market_data.catalogue_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE p.catalog_id=catalog AND k.publication_order IS NULL ORDER BY p.publication_order LOOP
  certify_or_capture_date_commit_at=security.checked_commit_time(certify_or_capture_date_row.publication_xid);
  IF certify_or_capture_date_commit_at<certify_or_capture_date_last_commit OR certify_or_capture_date_commit_at<=certify_or_capture_date_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.catalogue_knowledge_receipts VALUES(catalog,certify_or_capture_date_row.publication_order,certify_or_capture_date_row.publication_xid,certify_or_capture_date_commit_at);certify_or_capture_date_last_commit=certify_or_capture_date_commit_at;
 END LOOP;
 DECLARE catalogue_row record;catalogue_last_commit timestamptz;catalogue_commit_at timestamptz;catalogue_certified timestamptz;
BEGIN
 -- The risk worker captures through the earlier chain, as it has since 0117; only runtime captures certify publications.
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO catalogue_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO catalogue_last_commit FROM market_data.curve_definition_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR catalogue_row IN SELECT v.* FROM market_data.curve_definition_versions v LEFT JOIN market_data.curve_definition_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE v.catalog_id=catalog AND v.state='published' AND k.publication_order IS NULL ORDER BY v.publication_order LOOP
  catalogue_commit_at=security.checked_commit_time(catalogue_row.publication_xid);
  IF catalogue_commit_at<catalogue_last_commit OR catalogue_commit_at<=catalogue_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.curve_definition_knowledge_receipts VALUES(catalog,catalogue_row.publication_order,catalogue_row.publication_xid,catalogue_commit_at);
  catalogue_last_commit=catalogue_commit_at;
 END LOOP;
 DECLARE curve_definitions_r record;curve_definitions_known timestamptz;curve_definitions_last_known timestamptz;curve_definitions_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No temporal access' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO curve_definitions_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT max(known_from) INTO curve_definitions_last_known FROM market_data.crude_setup_receipts WHERE catalog_id=catalog;
 FOR curve_definitions_r IN SELECT v.* FROM market_data.crude_setup_versions v LEFT JOIN market_data.crude_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id)
  WHERE v.catalog_id=catalog AND v.state='published' AND k.setup_id IS NULL ORDER BY v.publication_order LOOP
  curve_definitions_known=security.checked_commit_time(curve_definitions_r.publication_xid);
  IF curve_definitions_known<curve_definitions_last_known OR curve_definitions_known<=curve_definitions_certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.crude_setup_receipts VALUES(catalog,curve_definitions_r.id,curve_definitions_r.publication_xid,curve_definitions_known);curve_definitions_last_known=curve_definitions_known;
 END LOOP;
 DECLARE crude_setup_r record;crude_setup_known timestamptz;crude_setup_last_known timestamptz;crude_setup_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No temporal access' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO crude_setup_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT max(known_from) INTO crude_setup_last_known FROM market_data.contract_date_admission_receipts WHERE catalog_id=catalog;
 FOR crude_setup_r IN SELECT a.id,a.publication_xid,security.checked_commit_time(a.publication_xid) AS committed_at
  FROM market_data.contract_date_admissions a LEFT JOIN market_data.contract_date_admission_receipts k
  ON(k.catalog_id,k.admission_id)=(a.catalog_id,a.id) WHERE a.catalog_id=catalog AND k.admission_id IS NULL ORDER BY committed_at,a.id LOOP
  crude_setup_known=crude_setup_r.committed_at;
  IF crude_setup_known<crude_setup_last_known OR crude_setup_known<=crude_setup_certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.contract_date_admission_receipts VALUES(catalog,crude_setup_r.id,crude_setup_r.publication_xid,crude_setup_known);crude_setup_last_known=crude_setup_known;
 END LOOP;
 DECLARE contract_admission_r record;contract_admission_known timestamptz;contract_admission_last_known timestamptz;contract_admission_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No temporal access' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO contract_admission_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT max(known_from) INTO contract_admission_last_known FROM market_data.gas_setup_receipts WHERE catalog_id=catalog;
 FOR contract_admission_r IN SELECT v.* FROM market_data.gas_setup_versions v LEFT JOIN market_data.gas_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id) WHERE v.catalog_id=catalog AND v.state='published' AND k.setup_id IS NULL ORDER BY v.publication_order LOOP
  contract_admission_known=security.checked_commit_time(contract_admission_r.publication_xid);
  IF contract_admission_known<contract_admission_last_known OR contract_admission_known<=contract_admission_certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.gas_setup_receipts VALUES(catalog,contract_admission_r.id,contract_admission_r.publication_xid,contract_admission_known);contract_admission_last_known=contract_admission_known;
 END LOOP;
 DECLARE gas_setup_row record;gas_setup_last_commit timestamptz;gas_setup_commit_at timestamptz;gas_setup_certified timestamptz;
BEGIN
  IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
  PERFORM pg_advisory_xact_lock(730849102);
  SELECT certified_through INTO gas_setup_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
  SELECT known_from INTO gas_setup_last_commit FROM risk.definition_run_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
  FOR gas_setup_row IN SELECT p.* FROM risk.definition_run_publications p LEFT JOIN risk.definition_run_receipts r USING(catalog_id,publication_order)
  WHERE p.catalog_id=catalog AND r.publication_order IS NULL ORDER BY p.publication_order LOOP
   gas_setup_commit_at=security.checked_commit_time(gas_setup_row.publication_xid);
   IF gas_setup_commit_at<gas_setup_last_commit OR gas_setup_commit_at<=gas_setup_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
   INSERT INTO risk.definition_run_receipts VALUES(catalog,gas_setup_row.publication_order,gas_setup_row.publication_xid,gas_setup_commit_at);gas_setup_last_commit=gas_setup_commit_at;
  END LOOP;
 DECLARE close_risk_row record;close_risk_last_commit timestamptz;close_risk_commit_at timestamptz;close_risk_certified timestamptz;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO close_risk_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO close_risk_last_commit FROM settlement_data.meter_finality_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR close_risk_row IN SELECT h.* FROM settlement_data.meter_finality_heads h LEFT JOIN settlement_data.meter_finality_receipts k USING(catalog_id,publication_order)
  WHERE h.catalog_id=catalog AND k.publication_order IS NULL ORDER BY h.publication_order LOOP
  close_risk_commit_at=security.checked_commit_time(close_risk_row.publication_xid);
  IF close_risk_commit_at<close_risk_last_commit OR close_risk_commit_at<=close_risk_certified OR close_risk_commit_at<close_risk_row.effective_at THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO settlement_data.meter_finality_receipts VALUES(catalog,close_risk_row.publication_order,close_risk_row.publication_xid,close_risk_commit_at);
  close_risk_last_commit=close_risk_commit_at;
 END LOOP;
 DECLARE meter_finality_row record;meter_finality_last_commit timestamptz;meter_finality_commit_at timestamptz;meter_finality_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO meter_finality_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO meter_finality_last_commit FROM market_data.custom_curve_activation_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR meter_finality_row IN SELECT p.* FROM market_data.custom_curve_activation_plans p LEFT JOIN market_data.custom_curve_activation_receipts k
  USING(catalog_id,publication_order) WHERE p.catalog_id=catalog AND k.publication_order IS NULL ORDER BY p.publication_order LOOP
  meter_finality_commit_at=security.checked_commit_time(meter_finality_row.publication_xid);
  IF meter_finality_commit_at<meter_finality_last_commit OR meter_finality_commit_at<=meter_finality_certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.custom_curve_activation_receipts VALUES(catalog,meter_finality_row.publication_order,meter_finality_row.publication_xid,meter_finality_commit_at);meter_finality_last_commit=meter_finality_commit_at;
 END LOOP;
 DECLARE curve_activation_row record;curve_activation_last_commit timestamptz;curve_activation_commit_at timestamptz;curve_activation_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO curve_activation_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO curve_activation_last_commit FROM market_data.interval_series_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR curve_activation_row IN SELECT r.* FROM market_data.interval_series_revisions r LEFT JOIN market_data.interval_series_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE r.catalog_id=catalog AND k.publication_order IS NULL ORDER BY r.publication_order LOOP
  curve_activation_commit_at=security.checked_commit_time(curve_activation_row.publication_xid);
  IF curve_activation_commit_at<curve_activation_last_commit OR curve_activation_commit_at<=curve_activation_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.interval_series_knowledge_receipts VALUES(catalog,curve_activation_row.publication_order,curve_activation_row.publication_xid,curve_activation_commit_at);curve_activation_last_commit=curve_activation_commit_at;
 END LOOP;
 DECLARE interval_series_row record;interval_series_last_commit timestamptz;interval_series_commit_at timestamptz;interval_series_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO interval_series_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO interval_series_last_commit FROM automation.pilot_report_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR interval_series_row IN SELECT s.* FROM automation.pilot_reports s LEFT JOIN automation.pilot_report_receipts r USING(catalog_id,publication_order)
 WHERE s.catalog_id=catalog AND r.publication_order IS NULL ORDER BY s.publication_order LOOP
  interval_series_commit_at=security.checked_commit_time(interval_series_row.publication_xid);
  IF interval_series_commit_at<interval_series_last_commit OR interval_series_commit_at<=interval_series_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO automation.pilot_report_receipts VALUES(catalog,interval_series_row.publication_order,interval_series_row.publication_xid,interval_series_commit_at);interval_series_last_commit=interval_series_commit_at;
 END LOOP;
 DECLARE pilot_report_row record;pilot_report_last_commit timestamptz;pilot_report_commit_at timestamptz;pilot_report_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO pilot_report_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO pilot_report_last_commit FROM market_data.market_close_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR pilot_report_row IN SELECT s.* FROM market_data.market_closes s LEFT JOIN market_data.market_close_receipts r USING(catalog_id,publication_order)
 WHERE s.catalog_id=catalog AND r.publication_order IS NULL ORDER BY s.publication_order LOOP
  pilot_report_commit_at=security.checked_commit_time(pilot_report_row.publication_xid);
  IF pilot_report_commit_at<pilot_report_last_commit OR pilot_report_commit_at<=pilot_report_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.market_close_receipts VALUES(catalog,pilot_report_row.publication_order,pilot_report_row.publication_xid,pilot_report_commit_at);pilot_report_last_commit=pilot_report_commit_at;
 END LOOP;
 DECLARE pilot_close_row record;pilot_close_last_commit timestamptz;pilot_close_commit_at timestamptz;pilot_close_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO pilot_close_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO pilot_close_last_commit FROM market_data.contract_date_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR pilot_close_row IN SELECT v.* FROM market_data.contract_date_versions v LEFT JOIN market_data.contract_date_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE v.catalog_id=catalog AND v.state='published' AND k.publication_order IS NULL ORDER BY v.publication_order LOOP
  pilot_close_commit_at=security.checked_commit_time(pilot_close_row.publication_xid);
  IF pilot_close_commit_at<pilot_close_last_commit OR pilot_close_commit_at<=pilot_close_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.contract_date_knowledge_receipts VALUES(catalog,pilot_close_row.publication_order,pilot_close_row.publication_xid,pilot_close_commit_at);
  pilot_close_last_commit=pilot_close_commit_at;
 END LOOP;
 DECLARE contract_dates_row record;contract_dates_last_commit timestamptz;contract_dates_commit_at timestamptz;contract_dates_certified timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read permission' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO contract_dates_certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO contract_dates_last_commit FROM market_data.custom_curve_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR contract_dates_row IN SELECT v.* FROM market_data.custom_curve_versions v LEFT JOIN market_data.custom_curve_knowledge_receipts k USING(catalog_id,publication_order) WHERE v.catalog_id=catalog AND v.state='published' AND k.publication_order IS NULL ORDER BY v.publication_order LOOP
  contract_dates_commit_at=security.checked_commit_time(contract_dates_row.publication_xid);
  IF contract_dates_commit_at<contract_dates_last_commit OR contract_dates_commit_at<=contract_dates_certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.custom_curve_knowledge_receipts VALUES(catalog,contract_dates_row.publication_order,contract_dates_row.publication_xid,contract_dates_commit_at);
  contract_dates_last_commit=contract_dates_commit_at;
 END LOOP;
 BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'collection_denied' USING ERRCODE='42501';END IF;
 PERFORM power_data.certify(catalog);
 DECLARE noncollection_date_clock market_data.knowledge_clocks; noncollection_date_row record; noncollection_date_commit_at timestamptz; noncollection_date_last_commit timestamptz;  noncollection_date_cutoff timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read permission' USING ERRCODE='42501'; END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO noncollection_date_clock FROM market_data.knowledge_clocks WHERE catalog_id=catalog FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001'; END IF;
 SELECT known_from INTO noncollection_date_last_commit FROM trading.knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR noncollection_date_row IN SELECT * FROM trading.publications WHERE catalog_id=catalog AND publication_order>noncollection_date_clock.trading_certified_head ORDER BY publication_order LOOP
  IF noncollection_date_row.publication_order<>noncollection_date_clock.trading_certified_head+1 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001'; END IF;
  noncollection_date_commit_at=security.checked_commit_time(noncollection_date_row.publication_xid);
  IF noncollection_date_commit_at<noncollection_date_last_commit OR (noncollection_date_clock.certified_through IS NOT NULL AND noncollection_date_commit_at<=noncollection_date_clock.certified_through) THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001'; END IF;
  INSERT INTO trading.knowledge_receipts VALUES(catalog,noncollection_date_row.publication_order,noncollection_date_row.publication_xid,noncollection_date_commit_at);
  noncollection_date_clock.trading_certified_head=noncollection_date_row.publication_order;noncollection_date_last_commit=noncollection_date_commit_at;
 END LOOP;
 DECLARE market_and_schedule_cutoff timestamptz;market_and_schedule_last_commit timestamptz;
BEGIN
 PERFORM security.certify_market_publications(catalog);
 DECLARE schedule_clock market_data.knowledge_clocks; schedule_row record; schedule_commit_at timestamptz; schedule_last_commit timestamptz; schedule_captured_at timestamptz; schedule_cutoff timestamptz;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read permission' USING ERRCODE='42501'; END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO schedule_clock FROM market_data.knowledge_clocks WHERE catalog_id=catalog FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001'; END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired during admission' USING ERRCODE='42501'; END IF;
 IF current_setting('track_commit_timestamp')<>'on' THEN RAISE EXCEPTION 'knowledge_tracking' USING ERRCODE='P0001'; END IF;
 IF pg_is_in_recovery() OR schedule_clock.system_identifier IS DISTINCT FROM (pg_control_system()).system_identifier::text OR schedule_clock.timeline_id IS DISTINCT FROM (pg_control_checkpoint()).timeline_id THEN RAISE EXCEPTION 'knowledge_timeline' USING ERRCODE='P0001'; END IF;
 IF schedule_clock.coverage_from IS NULL THEN schedule_clock.coverage_from=security.checked_commit_time(schedule_clock.origin_xid); END IF;
 SELECT known_from INTO schedule_last_commit FROM market_data.knowledge_publications WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 schedule_last_commit=coalesce(schedule_last_commit,schedule_clock.coverage_from);
 FOR schedule_row IN SELECT publication_order,publication_xid FROM market_data.date_schedule_versions
   WHERE catalog_id=catalog AND state='published' AND publication_order>schedule_clock.certified_head ORDER BY publication_order LOOP
  IF schedule_row.publication_order<>schedule_clock.certified_head+1 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001'; END IF;
  schedule_commit_at=security.checked_commit_time(schedule_row.publication_xid);
  -- A later commit must never enter an already certified knowledge interval.
  IF schedule_commit_at<schedule_last_commit OR (schedule_clock.certified_through IS NOT NULL AND schedule_commit_at<=schedule_clock.certified_through) THEN
   RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';
  END IF;
  INSERT INTO market_data.knowledge_publications VALUES(catalog,schedule_row.publication_order,schedule_row.publication_xid,schedule_commit_at);
  schedule_clock.certified_head=schedule_row.publication_order;schedule_last_commit=schedule_commit_at;
 END LOOP;
 schedule_captured_at=clock_timestamp();
 IF schedule_captured_at<schedule_last_commit OR (schedule_clock.certified_through IS NOT NULL AND schedule_captured_at<schedule_clock.certified_through) THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001'; END IF;
 schedule_cutoff=schedule_captured_at;
 IF NOT isfinite(schedule_cutoff) OR schedule_cutoff<schedule_clock.coverage_from OR schedule_cutoff>schedule_captured_at OR schedule_captured_at>=TIMESTAMPTZ '9999-12-31 00:00:00+00' THEN
  RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';
 END IF;
 UPDATE market_data.knowledge_clocks SET coverage_from=schedule_clock.coverage_from,certified_through=schedule_captured_at,certified_head=schedule_clock.certified_head WHERE catalog_id=catalog;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'Temporal authority expired during certification' USING ERRCODE='42501';END IF;
END;
 SELECT known_from INTO market_and_schedule_last_commit FROM market_data.market_knowledge_receipts WHERE catalog_id=catalog ORDER BY event_order DESC LIMIT 1;
 IF market_and_schedule_last_commit>(SELECT certified_through FROM market_data.knowledge_clocks WHERE catalog_id=catalog) THEN RAISE EXCEPTION 'knowledge_clock';END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'Temporal authority expired during certification' USING ERRCODE='42501';END IF;
END;
 IF noncollection_date_last_commit>(SELECT certified_through FROM market_data.knowledge_clocks WHERE catalog_id=catalog) THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001'; END IF;
 UPDATE market_data.knowledge_clocks SET trading_certified_head=noncollection_date_clock.trading_certified_head WHERE catalog_id=catalog;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'Temporal authority expired during certification' USING ERRCODE='42501';END IF;
END;
END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;

END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;

END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;

END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;

END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;

END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'curve_activation_denied' USING ERRCODE='42501';END IF;

END;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;

END;
END;
END;
END;
END;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;

END;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'Temporal authority expired during certification' USING ERRCODE='42501';END IF;
END;
END$certify$;
ALTER FUNCTION security.certify_knowledge(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.certify_knowledge(uuid) FROM PUBLIC;

CREATE FUNCTION security.record_owner_read_inputs() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE fields jsonb=to_jsonb(NEW);prior jsonb;source security.operation_read_coordinates;
 catalog uuid=(fields->>TG_ARGV[0])::uuid;identifier uuid=(fields->>TG_ARGV[1])::uuid;stem text=TG_ARGV[2];proof jsonb;revalidating boolean=false;
BEGIN
 IF TG_OP='UPDATE' THEN
  prior=to_jsonb(OLD);
  IF fields->TG_ARGV[1] IS NOT DISTINCT FROM prior->TG_ARGV[1] THEN
   IF (fields->(stem||'_business_on'),fields->(stem||'_known_at'),fields->(stem||'_system_id'),fields->(stem||'_timeline'))
    IS DISTINCT FROM(prior->(stem||'_business_on'),prior->(stem||'_known_at'),prior->(stem||'_system_id'),prior->(stem||'_timeline'))
   THEN RAISE EXCEPTION 'read_inputs_conflict' USING ERRCODE='P0001';END IF;
   RETURN NEW;
  END IF;
  IF prior->>TG_ARGV[1] IS NOT NULL THEN
   -- A new draft generation owns its newly validated inputs. Published facts
   -- and every other owner keep their original correlation and full tuple.
   IF identifier IS NOT NULL AND TG_TABLE_SCHEMA='market_data'
    AND TG_TABLE_NAME IN('contract_date_versions','curve_definition_versions')
    AND prior->>'state'='draft' AND fields->>'state'='draft'
    AND (fields->>'generation')::integer=(prior->>'generation')::integer+1
   THEN revalidating=true;
   ELSE RAISE EXCEPTION 'read_inputs_conflict' USING ERRCODE='P0001';END IF;
  END IF;
 END IF;
 IF identifier IS NULL THEN RETURN NEW;END IF;
 SELECT s.* INTO STRICT source FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(catalog,identifier);
 proof=jsonb_build_object(stem||'_business_on',source.effective_on,stem||'_known_at',source.known_at,
  stem||'_system_id',source.system_id,stem||'_timeline',source.timeline);
 IF EXISTS(SELECT 1 FROM jsonb_each(proof) p WHERE fields->p.key IS NOT NULL AND fields->p.key<>'null'::jsonb AND fields->p.key IS DISTINCT FROM p.value
  AND(NOT revalidating OR fields->p.key IS DISTINCT FROM prior->p.key))
 THEN RAISE EXCEPTION 'read_inputs_conflict' USING ERRCODE='P0001';END IF;
 NEW=jsonb_populate_record(NEW,proof);
 RETURN NEW;
EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'read_inputs_required' USING ERRCODE='P0001';
END$$;
ALTER FUNCTION security.record_owner_read_inputs() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_owner_read_inputs() FROM PUBLIC;

-- Each existing fact/run owns its explicit temporal inputs. These fields are
-- not independently addressable context objects; their UUID remains correlation
-- only and is checked against the complete coordinates on every new fact.
DO $owner_inputs$
DECLARE owner record;owner_exists boolean;
BEGIN
 FOR owner IN SELECT * FROM(VALUES
 ('market_data.contract_date_versions','catalog_id','validation_context_id','validation'),
 ('market_data.market_closes','catalog_id','preflight_context_id','preflight'),
 ('automation.pilot_reports','catalog_id','context_id','input'),
 ('assistant.turns','catalog_id','context_id','input'),
 ('risk.jobs','catalog_id','context_id','input'),
 ('automation.close_risk_profiles','catalog_id','context_id','input'),
 ('automation.pilot_report_ppa_quantities','catalog_id','read_context','read'),
 ('automation.pilot_report_ppa_inputs','catalog_id','knowledge_context','knowledge'),
 ('settlement_data.swap_determinations','catalog_id','context_id','input'),
 ('settlement_data.swap_cash_payments','catalog_id','context_id','input'),
 ('trading.gas_transport_capacity_versions','catalog_id','source_context_id','source'),
 ('trading.history_boundaries','catalog_id','context_id','input'),
 ('risk.definition_runs','catalog_id','context_id','input'),
 ('trading.gas_quantity_revisions','catalog_id','context_id','input'),
 ('trading.crude_operational_events','catalog_id','context_id','input'),
 ('market_data.contract_date_admissions','catalog_id','context_id','input'),
 ('risk.history_clock_captures','catalog_id','context_id','input'),
 ('trading.gas_cash_determinations','catalog_id','context_id','input'),
 ('trading.gas_nomination_requests','catalog_id','context_id','input'),
 ('trading.gas_service_versions','catalog_id','context_id','input'),
 ('trading.gas_balance_statements','catalog_id','context_id','input'),
 ('trading.gas_imbalance_agreements','catalog_id','context_id','input'),
 ('trading.gas_operation_responses','catalog_id','context_id','input'),
 ('trading.gas_imbalance_applications','catalog_id','context_id','input'),
 ('trading.gas_nomination_applications','catalog_id','context_id','input'),
 ('market_data.curve_definition_versions','catalog_id','validation_context_id','validation'),
 ('trading.gas_transport_capacity_plans','catalog_id','source_context_id','source'),
 ('trading.gas_transport_capacity_commands','catalog_id','source_context_id','source'),
 ('trading.gas_transport_routes','catalog_id','source_context_id','source'),
 ('trading.gas_transport_route_plans','catalog_id','source_context_id','source'),
 ('trading.gas_transport_route_commands','catalog_id','source_context_id','source'),
 ('trading.gas_transport_financial_states','catalog_id','source_context_id','source'),
 ('trading.gas_transport_financial_marks','catalog_id','source_context_id','source'),
 ('trading.gas_transport_financial_commands','catalog_id','source_context_id','source'),
 ('market_data.point_trader_mark_commands','catalog_id','source_context_id','source'),
 ('trading.delivery_route_allocation_plans','catalog_id','source_context_id','source'),
 ('risk.saved_pnl_reports','catalog_id','opening_context_id','opening'),
 ('risk.saved_pnl_reports','catalog_id','closing_context_id','closing'),
 ('settlement_data.futures_margin_adoptions','catalog_id','source_context_id','source'),
 ('settlement_data.futures_margin_policies','catalog_id','source_context_id','source'),
 ('settlement_data.futures_margin_commands','catalog_id','source_context_id','source'),
 ('settlement_data.futures_margin_runs','catalog_id','source_context_id','source'),
 ('settlement_data.futures_margin_run_commands','catalog_id','source_context_id','source'),
 ('settlement_data.futures_delivery_profiles','catalog_id','source_context_id','source'),
 ('trading.gas_futures_delivery_principal_runs','catalog_id','source_context_id','source'),
 ('automation.lifecycle_results','catalog_id','source_context_id','source'),
 ('trading.gas_futures_quantity_batches','catalog_id','source_context_id','source'),
 ('trading.gas_owned_quantity_corrections','catalog_id','source_context_id','source'),
 ('trading.gas_quantity_bundles','catalog_id','source_context_id','source'),
 ('trading.gas_loan_quantity_corrections','catalog_id','source_context_id','source'),
 ('trading.gas_loan_fee_changes','catalog_id','source_context_id','source'),
 ('trading.gas_operator_quantity_corrections','catalog_id','source_context_id','source'),
 ('trading.storage_facility_commands','catalog_id','source_context_id','source'),
 ('trading.gas_park_loan_fee_determinations','catalog_id','source_context_id','source'),
 ('trading.gas_park_custody_evidence','catalog_id','source_context_id','source'),
 ('trading.gas_park_custody_acceptances','catalog_id','source_context_id','source'),
 ('trading.gas_park_custody_returns','catalog_id','source_context_id','source'),
 ('assistant.turns','catalog_id','requested_context','requested')
 )v(relation,catalog_column,correlation_column,stem) LOOP
  IF EXISTS(SELECT 1 FROM pg_class c WHERE c.oid=owner.relation::regclass AND c.relkind<>'r')
  THEN RAISE EXCEPTION 'runtime_context_owner_shape: %',owner.relation;END IF;
  EXECUTE format('SELECT EXISTS(SELECT 1 FROM %s)',owner.relation) INTO owner_exists;
  IF owner_exists THEN RAISE EXCEPTION 'runtime_context_reset_required: %',owner.relation USING ERRCODE='55000';END IF;
  EXECUTE format('ALTER TABLE %s ADD COLUMN %I date,ADD COLUMN %I timestamptz,ADD COLUMN %I text,ADD COLUMN %I integer',
   owner.relation,owner.stem||'_business_on',owner.stem||'_known_at',owner.stem||'_system_id',owner.stem||'_timeline');
  EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I CHECK ((%I IS NULL AND %I IS NULL AND %I IS NULL AND %I IS NULL AND %I IS NULL) OR '
   ||'(%I IS NOT NULL AND %I IS NOT NULL AND %I IS NOT NULL AND %I IS NOT NULL AND %I IS NOT NULL '
   ||'AND isfinite(%I) AND isfinite(%I) AND %I>0 AND %I=security.workspace_context_id(%I,%I,%I)))',
   owner.relation,owner.stem||'_read_coordinates',owner.correlation_column,owner.stem||'_business_on',owner.stem||'_known_at',owner.stem||'_system_id',owner.stem||'_timeline',
   owner.correlation_column,owner.stem||'_business_on',owner.stem||'_known_at',owner.stem||'_system_id',owner.stem||'_timeline',
   owner.stem||'_business_on',owner.stem||'_known_at',owner.stem||'_timeline',owner.correlation_column,owner.catalog_column,owner.stem||'_business_on',owner.stem||'_known_at');
  EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE OF %I,%I,%I,%I,%I ON %s FOR EACH ROW EXECUTE FUNCTION security.record_owner_read_inputs(%L,%L,%L)',
   'aaa_'||owner.stem||'_read_inputs',owner.correlation_column,owner.stem||'_business_on',owner.stem||'_known_at',
   owner.stem||'_system_id',owner.stem||'_timeline',owner.relation,owner.catalog_column,owner.correlation_column,owner.stem);

 END LOOP;
END$owner_inputs$;

-- An explicit request resolves coordinates and registers only transaction-local
-- values. A saved owner is reconstructed through its own declared input fields.
CREATE FUNCTION security.resolve_read_cut(catalog uuid,day date,requested_at timestamptz,day_end boolean)
RETURNS TABLE(id uuid,known_at timestamptz,known_through integer,coverage_from timestamptz,
 certified_through timestamptz,system_id text,timeline integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;c market_data.knowledge_clocks;selected timestamptz;
 parent security.operation_read_coordinates;source security.operation_read_coordinates;
BEGIN
 SELECT * INTO q FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
 AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF q.id IS NULL THEN
  IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  RAISE EXCEPTION 'read_source_denied' USING ERRCODE='42501';
 END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'read_source_denied' USING ERRCODE='42501';END IF;
 IF day IS NULL OR day_end IS NULL OR day_end AND requested_at IS NULL
 THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 IF q.market_context_id IS NOT NULL THEN
  SELECT s.* INTO STRICT parent FROM unnest(q.read_sources) s
  WHERE(s.catalog_id,s.id)=(q.market_catalog_id,q.market_context_id);
  IF catalog IS DISTINCT FROM parent.catalog_id OR requested_at IS NULL
  THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
  selected=CASE WHEN day_end THEN least(requested_at,parent.known_at) ELSE requested_at END;
  IF selected>parent.known_at THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
  source=security.coordinates_from_inputs(catalog,day,selected,parent.system_id,parent.timeline);
 ELSE
  IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
  THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  PERFORM security.certify_knowledge(catalog);
  SELECT * INTO STRICT c FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog;
  selected=CASE WHEN day_end THEN least(requested_at,c.certified_through) ELSE coalesce(requested_at,c.certified_through) END;
  source=security.coordinates_from_inputs(catalog,day,selected,c.system_identifier,c.timeline_id);
 END IF;
 PERFORM security.register_read_source(catalog,source.id,source.effective_on,source.known_at,
  source.schedule_prefix,source.system_id,source.timeline);
 RETURN QUERY SELECT source.id,source.known_at,source.schedule_prefix,source.coverage_from,
  source.certified_through,source.system_id,source.timeline;
END$$;
ALTER FUNCTION security.resolve_read_cut(uuid,date,timestamptz,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.resolve_read_cut(uuid,date,timestamptz,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.resolve_read_cut(uuid,date,timestamptz,boolean) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.resolve_operation_context(catalog uuid,day date,requested_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result uuid;
BEGIN
 SELECT c.id INTO STRICT result FROM security.resolve_read_cut(catalog,day,requested_at,false) c;
 RETURN result;
END$$;
ALTER FUNCTION security.resolve_operation_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.resolve_operation_context(uuid,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.resolve_operation_context(uuid,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION security.use_market_context(catalog uuid,context uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source security.operation_read_coordinates;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'Unavailable read coordinates' USING ERRCODE='42501';END IF;
 SELECT s.* INTO source FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(catalog,context);
 IF source.id IS NULL THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 UPDATE security.request_contexts q SET market_catalog_id=catalog,market_context_id=context,
  calendar_cut=ROW(source.effective_on,source.known_at,source.schedule_prefix,source.coverage_from,
   source.market_prefix,source.catalogue_prefix)::security.calendar_read_coordinates
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
  AND q.runtime_login=session_user AND q.market_context_id IS NULL AND q.expires_at>clock_timestamp();
 IF NOT FOUND THEN RAISE EXCEPTION 'Read coordinates must bind exactly once' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.bind_operation_read(catalog uuid,context_id uuid,day date,
 cutoff timestamptz,prefix integer,system_id text,timeline integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.register_read_source(catalog,context_id,day,cutoff,prefix,system_id,timeline);
 IF EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
  AND q.expires_at>clock_timestamp() AND(q.market_catalog_id,q.market_context_id)=(catalog,context_id))
 THEN RETURN;END IF;
 PERFORM security.use_market_context(catalog,context_id);
END$$;
ALTER FUNCTION security.bind_operation_read(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.bind_operation_read(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.bind_operation_read(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE VIEW security.bound_calendar_context WITH(security_invoker=true) AS
 SELECT s.catalog_id,s.id,s.effective_on,s.known_at,s.schedule_prefix,s.coverage_from,s.market_prefix,s.catalogue_prefix
 FROM security.request_contexts q CROSS JOIN LATERAL unnest(q.read_sources) s
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
 AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
 AND(s.catalog_id,s.id)=(q.market_catalog_id,q.market_context_id);

-- Review savepoints and final commits use the same native seals. The release
-- constraint is deliberately excluded until the operation is actually finished.
CREATE FUNCTION security.check_operation_seals() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE seals text;
BEGIN
 SELECT string_agg(format('%I.%I',n.nspname,c.conname),',' ORDER BY n.nspname,c.conname) INTO seals
 FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace
 WHERE c.condeferrable AND NOT(c.conrelid='security.request_contexts'::regclass AND c.conname='read_sources_released');
 IF seals IS NOT NULL THEN EXECUTE 'SET CONSTRAINTS '||seals||' IMMEDIATE';END IF;
END$$;
ALTER FUNCTION security.check_operation_seals() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.check_operation_seals() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.check_operation_seals() TO openetrm_runtime,openetrm_risk_worker;

-- Financial seals run with the exact operation inputs still available. Only the
-- release seal waits until the financial checks and occurrence completion finish.
CREATE FUNCTION security.finish_operation_read() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.check_operation_seals();
 UPDATE security.request_contexts SET read_sources=NULL,calendar_cut=NULL,market_catalog_id=NULL,market_context_id=NULL,
 trading_as_of=NULL,trading_business_date=NULL,business_source_catalog_id=NULL,business_source_context_id=NULL,
 business_source_as_of=NULL,business_source_on=NULL,business_source_until=NULL
 WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SET CONSTRAINTS ALL IMMEDIATE;
END$$;
ALTER FUNCTION security.finish_operation_read() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.finish_operation_read() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.finish_operation_read() TO openetrm_runtime,openetrm_risk_worker;


CREATE FUNCTION security.knowledge_from_inputs(catalog uuid,day date,cutoff timestamptz,system_id text,timeline integer)
RETURNS SETOF security.operation_knowledge LANGUAGE sql ROWS 1 SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT s.catalog_id,s.id,s.effective_on,s.known_at,s.schedule_prefix,s.coverage_from,s.certified_through FROM security.coordinates_from_inputs($1,$2,$3,$4,$5) s
$$;
ALTER FUNCTION security.knowledge_from_inputs(uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.knowledge_from_inputs(uuid,date,timestamptz,text,integer) FROM PUBLIC;
CREATE FUNCTION security.market_from_inputs(catalog uuid,day date,cutoff timestamptz,system_id text,timeline integer)
RETURNS SETOF security.operation_markets LANGUAGE sql ROWS 1 SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT s.catalog_id,s.id,s.market_prefix FROM security.coordinates_from_inputs($1,$2,$3,$4,$5) s
$$;
ALTER FUNCTION security.market_from_inputs(uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.market_from_inputs(uuid,date,timestamptz,text,integer) FROM PUBLIC;
CREATE FUNCTION security.catalogue_from_inputs(catalog uuid,day date,cutoff timestamptz,system_id text,timeline integer)
RETURNS SETOF security.operation_catalogues LANGUAGE sql ROWS 1 SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT s.catalog_id,s.id,s.catalogue_prefix FROM security.coordinates_from_inputs($1,$2,$3,$4,$5) s
$$;
ALTER FUNCTION security.catalogue_from_inputs(uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.catalogue_from_inputs(uuid,date,timestamptz,text,integer) FROM PUBLIC;
CREATE FUNCTION security.trading_from_inputs(catalog uuid,day date,cutoff timestamptz,system_id text,timeline integer)
RETURNS SETOF security.operation_trading LANGUAGE sql ROWS 1 SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT s.catalog_id,s.id,s.trading_prefix FROM security.coordinates_from_inputs($1,$2,$3,$4,$5) s
$$;
ALTER FUNCTION security.trading_from_inputs(uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.trading_from_inputs(uuid,date,timestamptz,text,integer) FROM PUBLIC;

-- Retarget the installed consumers without changing their financial algorithms.
-- Full predecessor bodies, owners and grants are checked; owner-provenance
-- predicates are changed only by the explicit per-consumer replacements below.
DO $consumers$
DECLARE change record;p pg_proc;definition text;rewrite jsonb;relation record;actual text;
BEGIN
 FOR change IN SELECT * FROM(VALUES
('lifecycle.read_swap_profile(uuid,uuid,text,uuid,integer,uuid,timestamp with time zone,date)','8cf49cee905bd278a5a10da6b24c87c4f5f3c78b2ee9e6cc1aa07a1fe562a03b','[]'),
('lifecycle.read_option_known_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamp with time zone,date)','d0eae6ddda9098cf9b029b8ec1d0df516c9206f407866ad9eb0a09cbbc51cf57','[]'),
('lifecycle.read_swap_payment_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamp with time zone,date)','182cc7869edc3f42a8a22f16247c9accdf81ceaaa221442a4aea41d2e3f67700','[]'),
('lifecycle.option_proposal_base(uuid,jsonb)','d1e094f3c4e994ea75d749fb14d433bdcdd52415776d3aa142e48a05e9954b05','[]'),
('lifecycle.swap_net_profile_document(uuid,uuid,uuid,integer,uuid,timestamp with time zone,date)','685a8ffd902d75084be5b1e46df111aa88c98be2671460ed7624a3787b11801a','[]'),
('lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)','3de93fa704564f875c5aef56d3f97e1ab4cd85c48426c3c49928c1b40836c772','[]'),
('physical.power_operating_context(uuid,uuid,timestamp with time zone,date)','d5b2b1df99d6fd65876b7300d8d4d0b159d0f07f6d4cd91aa14886b1a783d59e','[]'),
('physical.power_operating_visible(physical.power_operating_facility_versions,uuid)','618d3f3f9da8ef567c71125d7a5b02ac1475c90a3044de830f34eada676dba1e','[]'),
('security.market_fact_visible(uuid,uuid,uuid)','b76a000db83d213fc2f3e247ca8d80549f84f9ae29aecb7243c6bbf28092e300','[]'),
('security.price_publication_visible(uuid,uuid,integer)','c05ab27eba2e02965bdd2adcec39a42d985c25edc910ede4d318c7b722be9c9a','[]'),
('security.use_trading_context(uuid,timestamp with time zone)','0a5db704dbb5cf4f8c4522b3a96a13328564efa4207fc98f904882dbc58f2c39','[]'),
('market_data.price_publication_event_visible(uuid,integer)','6f418a32e7fc7eb9563338b8532fcb34b0f23c66b5a3638d3e2bba8369ec309f','[]'),
('security.trade_party_versions(uuid,text,timestamp with time zone)','eedd0280c500f72e9386bc435c50cbcd002939f54d466d27342139d082cb6804','[]'),
('security.require_frame_inputs(uuid,uuid,jsonb)','3ec28a070bef70aa111175e13a8476092ee7403e35c1cbb589f7072ec90b6871','[]'),
('security.read_vol_surface(uuid,uuid,jsonb,date,text,uuid,timestamp with time zone,uuid[])','08031ba4503e6012d094032d6e4c464533c05a9b9275629960dec48a5dcce221','[]'),
('security.vol_surface_library(uuid,uuid,uuid,uuid,text,text,uuid,timestamp with time zone,uuid[],integer,integer)','eea9c6358eafb2605a2fe75a808788da74844da39d7ac4a3f06854694588c1f1','[]'),
('power_data.read_prices_pre_portfolio_worker(uuid,timestamp with time zone,timestamp with time zone,timestamp with time zone,timestamp with time zone)','a6638deff48937edd99cd5a3ca2bdc93c8dc295bab138b8eca6aa5425340d1c1','[]'),
('security.read_power_locations(uuid,uuid,uuid,jsonb)','2ecd147009cafd23c954e157783324e97e283589c4e63184d4fa25a370bdea71','[]'),
('trading.gas_operator_reconciliation_rows(uuid,uuid,uuid,timestamp with time zone,boolean)','09402231f30df45d25bbea25fc9b9205d364386779bc955526913d02013f3dfd','[]'),
('security.contract_date_schedule_visible(uuid,uuid,integer)','bdd823d1d88cf0470af4c97000930bb1e5921dbbcdb49fc447a975f686849ddd','[]'),
('security.contract_date_visible(uuid,uuid,integer)','c953b02d26acbb2efcae13a688578c177db976e62280fe964e4af9d141e246e1','[]'),
('security.market_close_visible(uuid,uuid)','4851de3759859b3cd8c4fab91eaa645e0eba570d9235d509171b4e9563a50c1f','[]'),
('settlement_data.adopt_futures_margin(uuid,jsonb)','b0777a666629c4d9d96813c88266b55259685e9633479245de1094ae6bb256c3','[]'),
('security.trading_context_day_matches(uuid,uuid,timestamp with time zone)','254f00e550b5261091e243e488dd65f3c2cd70df3818eec5ccc5c5d82eb38a38','[]'),
('settlement_data.futures_known_history_contents(uuid,uuid,uuid,uuid,timestamp with time zone,jsonb)','8d807747be16315fd2dc6ad66e4a9c1a2c1470960ff9a1f0218be7a2299da1a4','[]'),
('market_data.open_interval_series_read(market_data.interval_series,timestamp with time zone,timestamp with time zone,timestamp with time zone)','fbdb1a539701d343b2b89b8f5954d3e2f722c2dc4679e168000cb8a97cab3afd','[]'),
('settlement_data.determine_swap(uuid,uuid,uuid,integer,integer,uuid,timestamp with time zone,integer,date,date,timestamp with time zone,uuid,uuid,text,integer,timestamp with time zone,text,text,text,numeric,numeric,numeric,jsonb,jsonb,timestamp with time zone)','f643442b1696f2713cd5b7f656d4e57a61b32f0ef8d6d2d2b6e84a275ae4acfa','[]'),
('trading.gas_transport_capacity_cut(uuid,uuid)','a7134218672da4fbd84648308a5cd8c3a03bf725044b4523afb19ea5aa4559a2','[]'),
('trading.gas_transport_capacity_known(uuid,integer,uuid)','2e2561814ce8c9eb9ca8e3d45ba9103673eec91c7cc122822afdec387c9d9d29','[]'),
('trading.gas_owned_visible(uuid,integer,timestamp with time zone,uuid,timestamp with time zone)','6225d446d8da9c1d75667c05e947873b8ab25632ea5806164014f15abd023336','[]'),
('trading.gas_owned_cashout_price(uuid,uuid,uuid,timestamp with time zone,uuid)','cd6eda2c110ac3ce909d58ba60a52c3b240eb7d1766eb3b097b1822146d9a525','[]'),
('assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)','251cdf82de9cd0b0aae1773b7bebc1ca961c86093c788dd84cbc34f9e0b39c67','[]'),
('security.gas_owned_review_hash(uuid,uuid,text,jsonb,timestamp with time zone,uuid,uuid,integer,boolean)','9b3a205b4dceea60669b06bbc2ad90c6df39797517447662d3f79a25ef9a1f3d','[]'),
('security.read_gas_transport_capacity(uuid,uuid)','09123d864b63989ef736067599f6528b7f26ba2d2434835735637e48d11980ff','[]'),
('security.read_gas_owned_projection_choices(uuid,uuid,timestamp with time zone,uuid[])','3b8cb9ff28fca0b06d6cab7d67997543bcc8a4b555f17b5879509775c142dca3','[]'),
('settlement_data.save_futures_delivery_profile(uuid,jsonb)','8894432abde099cb39b434241bba830672a5cf2687805163be4f1e765db91b92','[]'),
('security.read_custom_curve_activation(uuid,uuid,integer)','747d86d57ba03a48e9328885cf475f72e04458ba0a55a61921d54b570b880600','[]'),
('settlement_data.read_futures_margin_origin(uuid,uuid,uuid,uuid,timestamp with time zone)','c82167d9f121e723cd0568f25f51dfaa4b0ebe28ec557b52cdb3516148aaf8bc','[]'),
('settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamp with time zone)','3361c29b77140dfd287294ff4f35e81ab0c0f15f91fb5f3b6bc5827f2f6318bd','[]'),
('automation.close_risk_run_definition()','d7bfb87085ee282d75d7b8ad01819639dbf381c757cce43640ab8833f109f241','[["JOIN security.operation_knowledge t ON(t.catalog_id,t.id)=(p.catalog_id,p.context_id) WHERE j.id=r.job_id AND j.operation=''close_book_risk_v1''", "JOIN LATERAL security.knowledge_from_inputs(p.catalog_id,p.input_business_on,p.input_known_at,p.input_system_id,p.input_timeline) t ON(t.catalog_id,t.id)=(p.catalog_id,p.context_id) WHERE j.id=r.job_id AND j.operation=''close_book_risk_v1''"]]'),
('automation.create_close_risk_job(jsonb,uuid,jsonb,bigint,bigint)','4b1bb3dd06bd0419984898633017d2ebad7268b4ed986ada3ac84ab773f95b87','[]'),
('settlement_data.read_futures_delivery_final(uuid,uuid,uuid,timestamp with time zone)','45d2b5897e891c23926114ff70891c356d8f788613428e3d43972d89b20e47f1','[]'),
('settlement_data.require_futures_delivery_head(uuid,uuid,uuid)','9473ac03bf70d2bce8a6c0ebd7f8110dc4298286bb75990ea87dd40dded12c75','[]'),
('security.ppa_visible_at(uuid,integer,timestamp with time zone,uuid,timestamp with time zone)','ef6bdb562691280e3ab8b633cc93c5a3005a9b7b912805d711ab43a8b550dee2','[]'),
('security.ppa_authoring_directory(uuid,uuid,timestamp with time zone)','0090b9f7b9b152812bff28b788ad39e796de77eb4c3c9549f5d7a6cc4e885fd2','[]'),
('settlement_data.publish_meter_finality(uuid,uuid,text,text,timestamp with time zone,timestamp with time zone,integer,text,timestamp with time zone,uuid,uuid,text)','9f5981cfe281bc5f904d36bd21c94c889f8196a145fbb7ee13c905b51119aa5a','[]'),
('settlement_data.read_meter_finality(uuid,uuid,text,text,timestamp with time zone,timestamp with time zone,uuid,timestamp with time zone)','6a94439509f97feba44bb270d205ef8a503e5a7a4fdaaf5465a4571982eeca9a','[]'),
('trading.gas_transport_financial_known(uuid,integer,uuid)','59f22068e62c410932ce570fd6649f1cdc6cb7c28820fcc348f591c611d2bbe1','[]'),
('trading.gas_transport_financial_market_known(uuid,uuid,uuid,uuid)','f73dfc869c0628fde7a0de398713817e830419ed5321673b0a08db17306950c5','[]'),
('trading.require_gas_transport_financial_source(uuid,jsonb,uuid,timestamp with time zone,timestamp with time zone,uuid)','c30a79a68b8ac0fe68d9be10a5881f898477be5c97b2f5cad6f581d3f607a635','[]'),
('trading.gas_transport_financial_mark_document(trading.gas_transport_financial_marks)','b4d202e577d82764425ecd406f8d18879704bef47a342071fdb1824bb4d4a827','[]'),
('trading.gas_transport_financial_state_document(trading.gas_transport_financial_states)','7946e851e7c051df416024fbca9e021406635e9c57e27517130a3e37d8376e34','[["JOIN security.operation_knowledge x ON(x.catalog_id,x.id)=(s.catalog_id,s.source_context_id)\n LEFT ", "JOIN LATERAL security.knowledge_from_inputs(s.catalog_id,s.source_business_on,s.source_known_at,s.source_system_id,s.source_timeline) x ON(x.catalog_id,x.id)=(s.catalog_id,s.source_context_id)\n LEFT "]]'),
('settlement_data.read_futures_delivery_principal(uuid,uuid,uuid,uuid,integer,uuid,timestamp with time zone)','80c5a6df9e10cefc11803cf70d482dcdc29319280616013b94e6e025ebd06a06','[]'),
('power_data.available_dates(timestamp with time zone)','50e845ac299a1c329152e6d7978668c666b2c777a2164e7e53906e7107f64215','[]'),
('security.read_gas_transport_financial(uuid,uuid,uuid,timestamp with time zone)','f11428c571f95f2edce41adfc4795b7e4bcad62ae6e30f3738b5958c83189341','[]'),
('risk.bind(uuid,uuid)','3e72cb051cdc325ed92b55c4301ca9daefcff0a8c7005ccf0e7ea9cea179aa46','[["FROM security.operation_knowledge t WHERE(t.catalog_id,t.id,t.effective_on)=(job.catalog_id,job.context_id,day))\n ", "FROM LATERAL security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t WHERE(t.catalog_id,t.id,t.effective_on)=(job.catalog_id,job.context_id,day))\n "]]'),
('settlement_data.apply_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamp with time zone,jsonb,numeric,uuid,uuid,text,timestamp with time zone,text,uuid,jsonb,text)','4bbda0d14ec2fc4847c7ac56cb35c69dcaa5d38e10e9e1fd49b5bc021defeb30','[]'),
('security.ppa_read_scope_pre_portfolio_worker(uuid,text,uuid[],uuid,timestamp with time zone,boolean)','6636425cec8a2df6d009501988d24538cb1df6081b3f3b778e0340f7dcd47a3e','[]'),
('security.ppa_require_market_pre_portfolio_worker(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,uuid[])','a972d20c2e42a85b39f0b551a0bde88c499e72b0921c19a3557c4a8937191f41','[]'),
('market_data.point_trader_mark_known(uuid,uuid,uuid,uuid)','3707a9ac52b6eab9642210ce40c96b21371d82c097d8dec252fdca7b1e8d0850','[]'),
('market_data.frozen_observation_knowledge_pre_portfolio_worker(uuid,uuid,uuid[],uuid,timestamp with time zone)','ddeca3152f4f432a45444f2774e15de0cfd4dcbe45c684f2818d6a97461fa246','[]'),
('risk.lifecycle_scope_policies(risk.jobs)','cad2635be39dd77fedf16ea51aeea589aa44fe825254bbd369d5967a464688cf','[["JOIN security.operation_trading x\n  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id)),\n owners AS MATERIALIZED(", "JOIN LATERAL security.trading_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) x\n  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id)),\n owners AS MATERIALIZED("]]'),
('security.point_trader_mark_setup(uuid,uuid,timestamp with time zone,uuid,uuid,uuid,jsonb)','0e84244ff3f58fea2bed22a987c20d78a995e053a08e57283f3f3df12eb3d332','[]'),
('security.require_gas_transport_financial_presence(uuid,uuid,timestamp with time zone,uuid[],uuid[],timestamp with time zone)','b35d8795775c848ccb95bb7fff4d5bedc253eb1e1c1de662520da2d247afc119','[]'),
('security.admit_trading_business_source(uuid,uuid,timestamp with time zone,date)','d0cbbf5946c78b4978ada87cf3b8febf097a8dec899ea171841c077cf3a3bfec','[]'),
('security.require_admitted_business_source(uuid,uuid,timestamp with time zone,date,timestamp with time zone)','c0ecc440417a6bd95afb99f5cf42db20c45a7c3c94626d3dcf8b2488c37b9cf4','[]'),
('settlement_data.read_futures_margin_state_pre_risk(uuid,uuid,uuid,uuid,timestamp with time zone)','86cb55fd100c77d1a51bb263463c0ce56c3d38da2e6cced59d78150651edd52c','[]'),
('trading.verify_delivery_route_allocation(uuid,uuid)','e201fac1b2fc9135254e397d9d8456e72ad58b0d14f2e49559af461db5ad256b','[["FROM security.operation_knowledge t\n  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,p.source_context_id,(p.effective_at AT TIME ZONE ''UTC'')::date))\n ", "FROM LATERAL security.knowledge_from_inputs(p.catalog_id,p.source_business_on,p.source_known_at,p.source_system_id,p.source_timeline) t\n  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,p.source_context_id,(p.effective_at AT TIME ZONE ''UTC'')::date))\n "]]'),
('settlement_data.require_futures_margin_read_pre_risk(uuid,uuid,timestamp with time zone,timestamp with time zone)','ad493bb790e2278f198e57b3f0fe9db9314f2551354b306b1eb4c1f984961660','[]'),
('settlement_data.read_futures_known_linked_history_pre_risk(uuid,uuid,uuid,uuid,timestamp with time zone,uuid)','6336425c2d8251088e3f9912d7417f9001a8314e24c1c3cca8e833b7062e2837','[]'),
('risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamp with time zone,jsonb,jsonb)','60d1d3667efbf9ca6adbc65521ad72b8b0ad0ff5d900ab4638f8ce342882dda3','[]'),
('security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamp with time zone)','ce0ebf4f63e37c6795e4744b63079a67dc1210da44e2b40ed4942a3add4789b3','[["JOIN security.operation_trading c ON(c.catalog_id,c.context_id)=(job.catalog_id,job.context_id) ", "JOIN LATERAL security.trading_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) c ON(c.catalog_id,c.context_id)=(job.catalog_id,job.context_id) "]]'),
('security.record_delivery_route_allocation(uuid,uuid,jsonb,timestamp with time zone,uuid)','d0849508e949b6e724159b0e313bd678cc720f03cecc35f28138c10c7bcd3658','[]'),
('trading.record_history_boundary(uuid,uuid,uuid)','4a3f98146de3526db6220eff4ea4679a4f03a2842cb585863b6b9b068938d807','[]'),
('security.require_trading_history(uuid,uuid)','1b16aca03b155cdddc2e906d97bb40408e6430e95190c3d5ccbd2a86d94dcf7b','[["JOIN security.operation_knowledge floor ON(floor.catalog_id,floor.id)=(b.catalog_id,b.context_id)\n  ", "JOIN LATERAL security.knowledge_from_inputs(b.catalog_id,b.input_business_on,b.input_known_at,b.input_system_id,b.input_timeline) floor ON(floor.catalog_id,floor.id)=(b.catalog_id,b.context_id)\n  "], ["JOIN security.operation_trading floor_trades ON(floor_trades.catalog_id,floor_trades.context_id)=(b.catalog_id,b.context_id)\n  ", "JOIN LATERAL security.trading_from_inputs(b.catalog_id,b.input_business_on,b.input_known_at,b.input_system_id,b.input_timeline) floor_trades ON(floor_trades.catalog_id,floor_trades.context_id)=(b.catalog_id,b.context_id)\n  "]]'),
('security.lifecycle_projection_sessions(uuid,uuid,timestamp with time zone,date)','ad41a760b2a7395d503b261c2aeb94e62078a333747978c7cdce4e98ee07750e','[]'),
('security.read_lifecycle_projection_horizon_pre_risk(uuid,text,integer,uuid,timestamp with time zone,date)','c3da7ee13522175e3b72f655147f1a0aed684cbd304ddb7d70ada33024fcab13','[]'),
('security.market_close_batch_visible(uuid,uuid[])','3a7450df83bf865fe16a27edece116ba372f0b01d419870ff6884a535ee6d197','[]'),
('risk.definition_run_history(uuid,uuid,uuid)','40a4e0bbf804a1249f034a48032d7c7e19146728182e21c088f304b02e29f768','[]'),
('automation.capture_lifecycle_source()','8e9b3c1dec59491cac0538e03df275b09ea46c07980aa436b5614c5f9aaa6dc3','[]'),
('security.gas_setup_visible(uuid,uuid)','e00deb6e710698ab81f473886f4004b03ae644f2b2710c95102f53fde9a3d66a','[]'),
('security.record_gas_daily_quantity(uuid,jsonb,timestamp with time zone,uuid)','08c0f1aee9e1a7f569ed224fb112172f5dfd704b53bb7f9b20120c061e0b251a','[]'),
('security.read_gas_quantity_rows(uuid,uuid,text)','f469993671e7ad744e41062188a7ca385232ed806601ff1ac54ed8def85bfd77','[]'),
('automation.lifecycle_job_result(uuid)','28806cdfebe388819b7c45d395cc04615c2a87c2e70dbd916c7d2b032df25a43','[["FROM security.operation_knowledge t WHERE(t.catalog_id,t.id)=(r.catalog_id,r.source_context_id)),''mode'',r.mode,''proposal'',r.proposal,''outcome'',r.outcome,''recordedAt'',r.recorded_at) ", "FROM LATERAL security.knowledge_from_inputs(r.catalog_id,r.source_business_on,r.source_known_at,r.source_system_id,r.source_timeline) t WHERE(t.catalog_id,t.id)=(r.catalog_id,r.source_context_id)),''mode'',r.mode,''proposal'',r.proposal,''outcome'',r.outcome,''recordedAt'',r.recorded_at) "]]'),
('security.read_crude_lifecycle_original(uuid,uuid)','ef53e757f86855abf71abfebe0301a047de17c468465ceaac4e8e0e630e96682','[]'),
('security.request_market_cut()','1b122c725de1e162838d3c518d686a2334789e2956388f6ecdadedaaf726ee11','[]'),
('security.record_crude_lifecycle(uuid,text,jsonb,timestamp with time zone,uuid)','92ab12e118e05a3388efaa1385f14f516238a0951baf0c8ed7c3b7eade69b976','[]'),
('security.require_crude_event_context(uuid,uuid,uuid,timestamp with time zone)','f201aeee37c984f96177a6fc8a8ee83eb32f6b77ec8aa341ca113572eae42928','[]'),
('security.heat_rate_call_position_scope(uuid,uuid[],uuid[],uuid[])','6143e401df1bce3f8a72b3af4ae6f3f0f700365090cd974bc61ffe12c159b8f7','[]'),
('security.visible_trading_publications()','5dcc6ea064b1cc9fdce7274b90d61ed2226de8f75f5fbad13e44a2b839d40f5d','[]'),
('security.read_spread_marks(uuid,uuid,text,text,date[])','d1612c7b5f910a9f0c798eb2197ef6115ab0280e55d7eadd95cfa6724af6a536','[]'),
('market_data.guard_contract_date_admission()','3b2e007e9a388e34b1fd2ec481b3e39f1179fc6c43b723e71e4906d264e3408b','[]'),
('security.contract_date_admission_visible(uuid,uuid)','db671d021cf7b60aae0cd632e2d6acc52a5ecc62150a7262355242e1a1947a23','[]'),
('security.curve_definition_visible(uuid,uuid,integer)','fed41d0496ba7cb6354cf425a69abb30dff2f497e2f5a4cdfbedb4d58493a6f1','[]'),
('security.custom_curve_visible(uuid,uuid,integer)','d5949dd14d97b1ff2291d005f56b5954245bcd610286b94cc2b4d31ddd28adaa','[]'),
('security.read_catalogue(uuid,uuid)','2aa95ff233b6b6c6f891e593d35bc961055998de596358037f99d9ea943f5bca','[]'),
('security.read_request_catalogue(uuid,date)','380116edd825a9026e6e75cfc33a2155a69fc85d25be8afffe4eb1d5c19406d0','[]'),
('security.reference_version_visible(uuid,uuid)','bca6620bbe82c283f995f93cc841d0d1a83e1dde6ec597e62ec33fc62545b1b3','[]'),
('security.resolve_crude_delivery(uuid,jsonb,timestamp with time zone,timestamp with time zone,timestamp with time zone)','be1997ea87c2f9ed632a47650882b8ecb25f4e7ee64205666d0962144ce058a7','[]'),
('security.workspace_read_cut(uuid,date,timestamp with time zone,boolean)','4e9218a4a906184756aa5fc80ac6093f16de59d0419d2f317c0e47f467e20661','[]'),
('security.bind_calendar_coordinates(uuid,uuid,date,timestamp with time zone,integer,text,integer)','c220358957507ce23803b5fe72111dda896f7b54693bab636a2b9a5761bdc263','[]'),
('risk.needs_gas_pre_gas_park_custody(risk.jobs)','58acc219d4697848c462cb56ab4bd9e6cbdb788d4e21fbf32e2e6094207012c8','[["JOIN security.operation_trading x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))\n  ", "JOIN LATERAL security.trading_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))\n  "]]'),
('security.finish_workspace_read()','95c28905ad1603e55b4698582aa22028a21d2e0c1fc8e74c10b575bb70d63690','[]'),
('assistant.pin_turn(uuid,bytea,date,jsonb,integer)','c068b80dda5a5e8b510966568bfa844f5f428b321664eb6405a3bb9af1e3a844','[["FROM security.operation_knowledge x\n     WHERE x.id=t.requested_context AND x.catalog_id=t.catalog_id AND x.effective_on=(t.page_report->>''businessDate'')::date)\n   ", "FROM LATERAL security.knowledge_from_inputs(t.catalog_id,t.requested_business_on,t.requested_known_at,t.requested_system_id,t.requested_timeline) x\n     WHERE x.id=t.requested_context AND x.catalog_id=t.catalog_id AND x.effective_on=(t.page_report->>''businessDate'')::date)\n   "]]'),
('automation.read_pilot_report(uuid,uuid)','d35be6f51e64aac964ee9e9dbae1c0bf9fddb1ba648f3eeffee4fe2515814175','[["JOIN security.operation_knowledge t ON(t.catalog_id,t.id)=(s.catalog_id,s.context_id) WHERE s.id=report", "JOIN LATERAL security.knowledge_from_inputs(s.catalog_id,s.input_business_on,s.input_known_at,s.input_system_id,s.input_timeline) t ON(t.catalog_id,t.id)=(s.catalog_id,s.context_id) WHERE s.id=report"]]'),
('market_data.read_price_publications(uuid)','a01335a0ea0c48d86ae3383dc5d13483ba76b3089abd520e3495f0fbecb63096','[]'),
('risk.lifecycle_scope_trades(risk.jobs)','5a6325126fc552346b07b43b9b7991c0c98bb69c0271c9d4e6a6deda9a372598','[["JOIN security.operation_trading x\n  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id)),\n future_heads AS(", "JOIN LATERAL security.trading_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) x\n  ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id)),\n future_heads AS("]]'),
('security.crude_position_scope(uuid,uuid[],uuid[],uuid[])','1350afe103d7f8f2e4f60eccc7d7703fa0033c5c5453fe4bba15522a40083d87','[]'),
('security.crude_setup_visible(uuid,uuid)','13e73247bc55ccc2629eec5f84058952762dbc253cd1a734f44761f7fbec742d','[]'),
('security.current_crude_setups(uuid)','26b044355bd7c98f8dbe4fa43cdba9817058faec6f67b2bd9fe3629efddf8e45','[]'),
('security.gas_physical_journal_source(uuid,uuid,timestamp with time zone,uuid[],uuid[])','7573a355e0b1134fbeef84f387aebd4796d6eebf400daf5f195e541d53684164','[]'),
('security.gas_position_scope(uuid,uuid[],uuid[],uuid[])','8c296716946bb48e6159dbaae0c02c3eaec47d32edefd0a51b12fdd83dc944d5','[]'),
('security.gas_storage_position_scope(uuid,uuid[],uuid[],uuid[])','5da8b4888da2358096eab49cac6a01db9fe61ca572e04ddcf6358fb2cde161f4','[]'),
('security.gas_transport_financial_capacity_facts(uuid,uuid,timestamp with time zone,uuid)','47c784e60907484efcfd50be5bc04a615960ec7baffad6e334d7646496e16503','[]'),
('security.lng_position_scope(uuid,uuid[],uuid[],uuid[])','f34f56d6ca3ec2548c49118a8a0b1a22683f838ff28b57514694c84bf1cf8802','[]'),
('security.pilot_report_sources_visible(uuid,uuid)','834b830a1c9a35dfadcad25a76c09a707947ec687d7880d2e2f8d1c185d31938','[]'),
('security.pilot_report_visible(uuid,uuid)','f15a8b9832df9d6f491db9e74edf4fb31fcbc9b5d3437e3d30af94b13d57dadb','[]'),
('security.read_gas_physical_known_attachments(uuid,uuid,timestamp with time zone,uuid[],jsonb)','f4de371722ef11185aca61e2811135dca371bac333d9ad62f022670979404a50','[]'),
('security.ppa_tou_versions_pre_portfolio_worker(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,jsonb)','79fec774ed0b2b71a37b1971f539de4d1c810295db4ed943180551a5c2f9411d','[]'),
('security.read_gas_owned_holdings_original(uuid,uuid,timestamp with time zone,uuid[])','983dd126a44753d76cfecac111b894db1e6b505640c7e29c6d597df0627c1fe9','[]'),
('security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamp with time zone,uuid[])','66521e9a6d5e9e242f78356becb25169b1b67fd9cf90f31c37a3d8e18ea839f5','[]'),
('security.read_gas_owned_operations(uuid,uuid,timestamp with time zone,uuid)','bea0232fdf0424a93a603d98ba19eb905b48c166bf9eaff22517cb64d3e8f4eb','[]'),
('security.read_gas_transport_route(uuid,uuid)','feabc1fc9c7988c1925649b1daaffc81758ed3bf1533d0ba0115025febf2e5eb','[]'),
('security.read_lifecycle_projection_final_pre_final_scope(uuid,uuid,text,text,uuid,timestamp with time zone,uuid,timestamp with time zone,date)','062d48e29018ec299dff1e19b8618e6b6743af6b8ed7b258e89b9ff914422327','[]'),
('security.read_lifecycle_projection_profiles(uuid,text,uuid,integer,uuid,timestamp with time zone,date,timestamp with time zone)','7df54defc07de507c9139ab461e29d5509a7dd036c0927c1287f212ab371c1f3','[]'),
('security.read_rec_inventory(uuid,uuid,uuid,timestamp with time zone)','7c7616b6e03441ebec219236ad24eeaac5efb114c05ccd2157dc74aba3692b97','[]'),
('security.record_gas_park_custody(uuid,uuid,uuid,text,jsonb,timestamp with time zone,uuid,uuid,integer,boolean)','9de0c76f848a233266cb545e18bcb59caa24620c0dd025635de9ba497e89e933','[]'),
('security.require_crude_trade_context(uuid,uuid,integer,uuid,timestamp with time zone)','23c834f6f078616532aa4143b23894c9ca9b507d61ff5a1f2fa13f1870982f15','[]'),
('security.require_gas_park_custody_read(uuid,uuid,timestamp with time zone,timestamp with time zone,boolean)','583d7cf9b11f7ee62778d4635b05adead425d04433e7d1374b496d080a970ade','[]'),
('security.require_gas_transport_financial_read(uuid,uuid,timestamp with time zone,timestamp with time zone)','b0b1468c5ebbfd1cf1597def139c1998762eec8583798a6ffd3c95287e72b2a1','[]'),
('security.require_lifecycle_projection_profile_pre_risk(uuid,text,uuid,uuid,integer,uuid,timestamp with time zone,date,timestamp with time zone)','aba328225f4a05e6814b4b0da1d4cec6375cfc0ca3443d60ea1cac353da45e85','[]'),
('security.require_point_trader_mark_read(uuid,uuid,timestamp with time zone,uuid,uuid,uuid,timestamp with time zone)','38f4fe3de2429abf83485166b3d58303ccf83c85267e707f7a96746fe4f1bce6','[]'),
('security.risk_ppa_trade_heads(uuid,uuid[],uuid[])','2387e584767b7e65f296e54d14ce6ee0155fdb7b9c15872a4056cdbe2af87c56','[]'),
('risk.require_cash_accumulation(risk.jobs)','066054a150599fd022433871c94f8fae8e99b2851488e0aea8357a3c92e39cc5','[["FROM security.operation_knowledge t WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))\n   OR source->>''selectionPolicy'' IS DISTINCT ", "FROM LATERAL security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))\n   OR source->>''selectionPolicy'' IS DISTINCT "]]'),
('risk.retain_statistical_history(risk.jobs,jsonb)','22d0873b1519f94d529002b7d80cb2f770cdf60c3cdce182d2c35ae2bd7e6496','[["JOIN security.operation_knowledge t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)\n  WHERE(m.catalog_id,m.context_id)=(job.catalog_id,job.context_id)", "JOIN LATERAL security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)\n  WHERE(m.catalog_id,m.context_id)=(job.catalog_id,job.context_id)"]]'),
('security.trading_visible(uuid,integer,timestamp with time zone)','1065a38938e8a7700104688f1e572b668dce6e3ca4a88915001c0191066daaad','[]'),
('security.use_trading_context(uuid,timestamp with time zone,date)','c21e9f8d7e40deb876cc49ba4026f84abd6b520914a2cccbd04f03591f8f5d11','[]'),
('settlement_data.futures_delivery_profile_document(uuid,uuid,integer,uuid,timestamp with time zone)','cbc7fb52c0928fd75154566fb2ba48c2129ede4a1a03faf590f022db1d3acf5c','[]'),
('settlement_data.futures_margin_policy_visible(uuid,integer)','2796f96e80c4e5854b681c2b6ab75b69acf0fa513bba10e6f24d741a6b8c6a1a','[]'),
('settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamp with time zone)','4fef9be1246ac43ba0207d156e0f796c9a6b69a50b32a379f0131a8e6c36f2bc','[]'),
('settlement_data.require_futures_margin_delivery(settlement_data.futures_margin_policies)','4615b2e77adaff2512e2cfc4223c7d894336a07a2aae71c883845cd7e91c7d5a','[]'),
('settlement_data.require_futures_margin_source(uuid,uuid,timestamp with time zone,uuid)','801b77304e6b0209953b87869457f6883daf5ba35da8c3e6a7379b69bf54418b','[]'),
('trading.gas_owned_applied_observation_conflicts(uuid,uuid,timestamp with time zone,uuid[],uuid[])','14c43fcb8864005f20fa8efe22bfa7df0fb06fcfb0295dd94a083aaba1a5ccee','[]'),
('trading.require_gas_loan_fee_context(uuid,uuid,timestamp with time zone)','22acfb4079bd6b605ad92948361eba95daa0bc0926c32a5cae76222143870572','[]'),
('security.read_gas_owned_profile_holdings(uuid,uuid,timestamp with time zone,uuid[])','65dae1fe3e9436d9f858e27605777ad8d887ff314af89c26bba4eff9aceaae5d','[]'),
('risk.store_history_clock(uuid,uuid,jsonb,jsonb)','72985b07e4313fba1b963360a5520a3e2f6d120bf26385fb84d3b40e73feff50','[]'),
('risk.capture_history_clock(uuid,uuid,jsonb,jsonb)','05d1773004bf08df8b88442e850aac120eabc9c2faf9c5cd650dd3b1c06b952e','[]'),
('security.close_in_force(uuid,text,date)','0d8e2aed111120d85a3bde90540dfef63129b7af0dc47f9ef28a728c6af5ccfd','[]'),
('security.record_gas_billing(uuid,jsonb,timestamp with time zone,uuid,jsonb)','1bee6c3bf8b5749aaab860a90f226968339d78366b472778f152369594227ce4','[]'),
('security.gas_operations_balance(uuid,uuid,date,uuid,uuid,timestamp with time zone,uuid)','89d5b97183672cdc2aa4f91b1ae760a29dcae75c30353b4e762a61a6fcc5f49c','[]'),
('security.record_gas_operations(uuid,jsonb,timestamp with time zone,uuid,jsonb)','ed637a7409ddd187ec6277af4340be78dc4ac8fdfcc51df3a4e39681fc771df6','[["JOIN security.operation_trading x ON(x.catalog_id,x.context_id)=(ctx.catalog_id,ctx.id) WHERE(ctx.catalog_id,ctx.id,ctx.effective_on)=(catalog,context,(as_of AT TIME ZONE ''UTC'')::date)) ", "JOIN LATERAL security.trading_from_inputs(x.catalog_id,x.input_business_on,x.input_known_at,x.input_system_id,x.input_timeline) x ON(x.catalog_id,x.context_id)=(ctx.catalog_id,ctx.id) WHERE(ctx.catalog_id,ctx.id,ctx.effective_on)=(catalog,context,(as_of AT TIME ZONE ''UTC'')::date)) "]]'),
('trading.require_gas_owned_quantity_amendment(uuid,uuid,jsonb,uuid,timestamp with time zone,uuid,integer)','9e0bd6c319070e88e2a9829575c28a275709d42afa8bead4d927f01eab3613c1','[]'),
('security.gas_operations_context(uuid,uuid,timestamp with time zone,integer,timestamp with time zone)','993abcb7a9213c9e58bb454d15c32f2883fd9a781084d34b913b326f2ce602e8','[]'),
('settlement_data.futures_margin_projection_source_rows(settlement_data.futures_margin_policies,uuid,timestamp with time zone,timestamp with time zone)','4c2c584c55fa4cfac0ded8894de177af68763858c06170b961692f9e71c697ea','[]'),
('settlement_data.read_futures_delivery_settings(uuid,uuid,timestamp with time zone)','37807a6e7c4acb13447ce7f7c5f11ad884184c2612de6fd323bb2927fb775e25','[]'),
('security.record_gas_owned(uuid,uuid,text,uuid,jsonb,timestamp with time zone,uuid,uuid,integer,boolean)','54a4fa22fa1df7691faa32398a6af016f11d302a7716cedddcf5fbeb87cb3d4e','[]'),
('security.side_calendar_cutoff(uuid,uuid)','3edbfc54b28ad745db5c7393ca0fa9bcbbde7b14aef26669d00149e58c26ad2b','[]'),
('security.require_gas_park_loan_presence(uuid,uuid,timestamp with time zone,uuid[],uuid[],timestamp with time zone)','d06cbe0106933ee5bf89d887bee10e01cc89ce32292362fe7cff15bbda1586fd','[]'),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','7e4e7f39516f36d769ff227ffed52fdffa4e3f55a8eab1b03056b4b94121781a','[]'),
('security.operating_position_scope(uuid,uuid[],uuid[],uuid[])','d75bf8137a161d7077bf34a58a73d90dd5f385db838fb2ece8ebc11e128165ba','[]'),
('security.rec_forward_position_scope(uuid,uuid[],uuid[],uuid[])','497ac7a989640961995fedd6fb09ee45fbb3da0c5b990d63ca1ea6f51aa8fed6','[]'),
('security.read_storage_facility(uuid,uuid)','7279ce15a6505c170bdda890db383add4f0a6537cfb9f0192b530b15129c42cc','[]'),
('trading.require_storage_facility_source(uuid,uuid,jsonb,uuid,timestamp with time zone,integer)','418bf7aa564ae22076487757e3b43d85c7a9929b02507d576c24e7e0afe3b76b','[]'),
('security.read_gas_park_loan_principal_v1(uuid,uuid,uuid,timestamp with time zone)','19e36b354e65c8a913bae746471ab35fe19da7f3b36aba6f28e9b49056ed6529','[]'),
('security.record_gas_balance_fee(uuid,uuid,uuid,jsonb,timestamp with time zone,uuid,timestamp with time zone)','b54a8701051b2ca3ccb40541b7100b49606ba56cc7aa2b67ebbdfbfed5b1cd7b','[]'),
('security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamp with time zone,uuid,boolean)','a8128989dba3b89fcbc5fbdb8f3099f7de83e232f03f69ed3c329141ce341149','[]'),
('trading.gas_balance_fee_accrual(uuid,uuid,uuid,timestamp with time zone,timestamp with time zone)','7df5267bca16239eda475ce27765fde14788e03520ae0ab19b5b3428f64b81b0','[]'),
('trading.validate_gas_balance_fee_determination(uuid,uuid)','dd714b60cc1591eaec7cafe02e5a7dbed8c9ae45c8f233a11aeed69a39376985','[["FROM security.operation_knowledge t WHERE(t.catalog_id,t.id)=(catalog,fact.source_context_id)", "FROM LATERAL security.knowledge_from_inputs(fact.catalog_id,fact.source_business_on,fact.source_known_at,fact.source_system_id,fact.source_timeline) t WHERE(t.catalog_id,t.id)=(catalog,fact.source_context_id)"], ["FROM security.operation_trading x WHERE(x.catalog_id,x.context_id)=(catalog,fact.source_context_id) AND x.known_through>=fact.publication_order)\n  OR NOT trading.gas_owned_visible(catalog,binding.publication_order,binding.effective_at,fact.source_context_id,as_of)\n ", "FROM LATERAL security.trading_from_inputs(fact.catalog_id,fact.source_business_on,fact.source_known_at,fact.source_system_id,fact.source_timeline) x WHERE(x.catalog_id,x.context_id)=(catalog,fact.source_context_id) AND x.known_through>=fact.publication_order)\n  OR NOT trading.gas_owned_visible(catalog,binding.publication_order,binding.effective_at,fact.source_context_id,as_of)\n "]]'),
('risk.needs_gas_park_custody(risk.jobs,boolean)','8325cbc7fdbb86ae4a1cbc556f9456579fbd452f0856ef5d8b2879f2998dee2c','[["JOIN security.operation_trading x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)\n  WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))\n  ", "JOIN LATERAL security.trading_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)\n  WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))\n  "]]'),
('security.read_gas_park_custody(uuid,uuid,uuid,timestamp with time zone)','c0930d30e707eae05c89466155e9698c76fecd36ca2fa5a884ca083f85f8c82c','[]'),
('trading.require_gas_park_custody_receipt(uuid,uuid)','8f6f092fdd87c484398aece6a8c359619e391eaadb266cdb29c77ce793aa99e1','[]'),
('security.record_gas_park_custody_evidence(uuid,uuid,uuid,jsonb,timestamp with time zone,uuid)','ecb2a7980e4c758507e617a0141e9da3c0a63b1ef343d554876a0c9da8efad6d','[]')
 )v(signature,expected,owner_changes) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=change.signature::regprocedure;
  definition=pg_get_functiondef(p.oid);
  IF change.signature IN('trading.record_history_boundary(uuid,uuid,uuid)','market_data.guard_contract_date_admission()') THEN
   IF p.proowner<>current_user::regrole OR p.prosecdef THEN RAISE EXCEPTION 'runtime_context_creator_owner: %',change.signature;END IF;
   actual=encode(sha256(convert_to(definition||'<migration_creator>'||replace(coalesce(p.proacl::text,''),current_user,'<migration_creator>'),'UTF8')),'hex');
  ELSE actual=encode(sha256(convert_to(definition||p.proowner::regrole::text||coalesce(p.proacl::text,''),'UTF8')),'hex');END IF;
  IF actual<>change.expected THEN RAISE EXCEPTION 'runtime_context_consumer_changed: %',change.signature USING ERRCODE='55000';END IF;
  FOR relation IN SELECT * FROM(VALUES
   ('market_data.temporal_contexts','security.operation_knowledge'),('security.read_temporal_contexts','security.operation_knowledge'),
   ('market_data.market_contexts','security.operation_markets'),('security.read_market_contexts','security.operation_markets'),
   ('market_data.catalogue_contexts','security.operation_catalogues'),('security.read_catalogue_contexts','security.operation_catalogues'),
   ('trading.contexts','security.operation_trading'),('security.read_trading_contexts','security.operation_trading'),
   ('security.operation_read_sources','security.operation_read_inputs'),('security.capture_date_context(','security.resolve_operation_context(')
  )v(original,replacement) LOOP definition=replace(definition,relation.original,relation.replacement);END LOOP;
  FOR rewrite IN SELECT * FROM jsonb_array_elements(change.owner_changes::jsonb) LOOP
   IF position(rewrite->>0 in definition)=0 THEN RAISE EXCEPTION 'runtime_context_owner_predicate: %',change.signature;END IF;
   definition=replace(definition,rewrite->>0,rewrite->>1);
  END LOOP;
  EXECUTE definition;
 END LOOP;
END$consumers$;

-- Source-owning readers and pre-bind permission qualification derive their
-- values from those owners, not from whether a request projection exists.
DO $owner_consumers$
DECLARE change record;definition text;
BEGIN
 FOR change IN SELECT * FROM(VALUES
('risk.lifecycle_scope_policies(risk.jobs)','FROM security.operation_knowledge t','FROM security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t'),
('risk.lifecycle_scope_trades(risk.jobs)','FROM security.operation_knowledge t','FROM security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t'),
('risk.needs_gas_pre_gas_park_custody(risk.jobs)','FROM security.operation_knowledge t','FROM security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t'),
('risk.needs_gas_park_custody(risk.jobs,boolean)','FROM security.operation_knowledge t','FROM security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t'),
('risk.retain_statistical_history(risk.jobs,jsonb)','FROM security.operation_markets m','FROM security.market_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) m'),
('security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamp with time zone)','JOIN security.operation_knowledge t','JOIN security.knowledge_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline) t'),
('security.record_gas_operations(uuid,jsonb,timestamp with time zone,uuid,jsonb)','JOIN LATERAL security.trading_from_inputs(x.catalog_id,x.input_business_on,x.input_known_at,x.input_system_id,x.input_timeline) x','JOIN security.operation_trading x'),
('settlement_data.require_futures_margin_delivery(settlement_data.futures_margin_policies)','FROM security.operation_knowledge t','FROM security.knowledge_from_inputs(p.catalog_id,p.source_business_on,p.source_known_at,p.source_system_id,p.source_timeline) t'),
('settlement_data.require_futures_margin_delivery(settlement_data.futures_margin_policies)','JOIN security.operation_trading x','JOIN security.trading_from_inputs(p.catalog_id,p.source_business_on,p.source_known_at,p.source_system_id,p.source_timeline) x'),
('trading.gas_transport_financial_mark_document(trading.gas_transport_financial_marks)','FROM security.operation_knowledge x','FROM security.knowledge_from_inputs(m.catalog_id,m.source_business_on,m.source_known_at,m.source_system_id,m.source_timeline) x'),
('risk.bind(uuid,uuid)','context_token uuid=gen_random_uuid();day date;','context_token uuid=gen_random_uuid();day date;source security.operation_read_coordinates;'),
('risk.bind(uuid,uuid)',' day=(job.frame->>''businessDate'')::date;',' source=security.coordinates_from_inputs(job.catalog_id,job.input_business_on,job.input_known_at,job.input_system_id,job.input_timeline);
 day=(job.frame->>''businessDate'')::date;'),
('risk.bind(uuid,uuid)','INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,delegation_id,runtime_login,expires_at)','INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,delegation_id,runtime_login,expires_at,read_sources)'),
('risk.bind(uuid,uuid)','VALUES(context_token,job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,session_user,least(job.lease_expires_at,candidate_until));','VALUES(context_token,job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,session_user,least(job.lease_expires_at,candidate_until),ARRAY[source]);'),
('risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb)',' IF FOUND THEN
  PERFORM security.store_workspace_source($1,$2,source.effective_on,source.known_at,source.schedule_prefix);
 END IF;',''),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','   reserve:=(assistant.turn_operating_limits(t.id)->>''retainedContextReserve'')::integer;
',''),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','   IF(SELECT count(*) FROM security.operation_knowledge WHERE catalog_id=t.catalog_id)>=4096-reserve THEN RAISE EXCEPTION ''knowledge_budget_reserved'';END IF;
',''),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','result jsonb;reserve integer;','result jsonb;'),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','assistant.pin_turn(turn,token,effective_date,pins,NULL)','assistant.pin_turn(turn,token,effective_date,pins)')
 )v(signature,original,replacement) LOOP
  definition=pg_get_functiondef(change.signature::regprocedure);
  IF position(change.original in definition)=0 THEN RAISE EXCEPTION 'runtime_context_owner_consumer: %',change.signature;END IF;
  EXECUTE replace(definition,change.original,change.replacement);
 END LOOP;
END$owner_consumers$;

-- New turn operations have no reserve parameter or saved-context path.
CREATE FUNCTION assistant.pin_market_turn(turn uuid, token bytea, effective_date date, pins jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET "TimeZone" TO 'UTC'
AS $function$
DECLARE c security.request_contexts:=assistant.grant_actor(true);t assistant.turns;x security.operation_knowledge;context uuid;operating jsonb;
BEGIN
 t:=assistant.fenced_turn(turn,token);
 -- The wait can outlive the authority.
 c:=assistant.grant_actor(true);
 IF t.owner_id<>c.actor_id OR(t.tenant_id,t.workspace_id)<>(c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 operating:=assistant.turn_operating_limits(t.id);
 IF pins IS NOT NULL AND octet_length(pins::text)>(operating->>'pinBytes')::integer THEN RAISE EXCEPTION 'turn_input';END IF;
 IF t.recorded_at IS NOT NULL OR (t.state NOT IN('queued','cancel_requested') AND NOT(t.state='running' AND t.runtime_set IN('turns_v22','turns_v23'))) THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.owner_turn(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF(t.frame_id IS NULL)<>(pins IS NULL) OR pins IS NOT NULL AND(jsonb_typeof(pins)<>'object' OR pins->>'frameId' IS DISTINCT FROM t.frame_id::text
   OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(pins) key)<>ARRAY['at','businessDate','close','curveContracts','frameId','frozenObservationIds','name',
    'observationIds','originCloseId','ppaBundle','resolvesLatestCorrection','source','sourceInputs','swapMarketSelections']
   OR jsonb_typeof(pins->'at')<>'string' OR pins->>'at'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$'
   OR jsonb_typeof(pins->'name')<>'string' OR btrim(pins->>'name')=''
   OR NOT assistant.is_uuid_array(pins->'observationIds') OR NOT assistant.is_uuid_array(pins->'frozenObservationIds')
   OR jsonb_typeof(pins->'curveContracts')<>'object' OR jsonb_typeof(pins->'swapMarketSelections') NOT IN('object','null')
   OR jsonb_typeof(pins->'resolvesLatestCorrection')<>'boolean' OR octet_length(pins::text)>262144) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF pins IS NOT NULL AND(EXISTS(SELECT 1 FROM jsonb_array_elements_text(pins->'frozenObservationIds') f WHERE NOT(pins->'observationIds')?f)
   OR EXISTS(SELECT 1 FROM jsonb_each(pins->'curveContracts') e WHERE e.key='' OR NOT assistant.is_uuid_array(e.value))
   OR jsonb_typeof(pins->'swapMarketSelections')='object' AND EXISTS(SELECT 1 FROM jsonb_each(pins->'swapMarketSelections') e
    WHERE NOT assistant.is_uuid_array(jsonb_build_array(e.key)) OR NOT assistant.is_uuid_array(e.value))) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 -- The frame's instant is one this database reads.
 BEGIN
  PERFORM(pins->>'at')::timestamptz;
 EXCEPTION WHEN datetime_field_overflow OR invalid_datetime_format OR invalid_time_zone_displacement_value THEN RAISE EXCEPTION 'turn_input';
 END;
 IF t.knowledge_mode='current' THEN
  IF effective_date IS NULL THEN RAISE EXCEPTION 'turn_input';END IF;
  -- Recheck current authority after waiting on the shared publication gate.
  PERFORM pg_advisory_xact_lock(730849102);
  c:=assistant.grant_actor(true);
  context:=security.resolve_operation_context(t.catalog_id,effective_date,NULL);
 ELSE
  IF NOT security.market_access(t.catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current market read authority' USING ERRCODE='42501';END IF;
  context:=t.requested_context;
 END IF;
 SELECT y.* INTO x FROM security.operation_knowledge y WHERE(y.catalog_id,y.id)=(t.catalog_id,context);
 IF x.id IS NULL THEN RAISE EXCEPTION 'temporal_context';END IF;
 IF pins IS NOT NULL AND(jsonb_typeof(pins->'businessDate') IS DISTINCT FROM 'string'
 OR pins->>'businessDate'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 OR(pins->>'businessDate')::date IS DISTINCT FROM x.effective_on) THEN RAISE EXCEPTION 'turn_input';END IF;
 UPDATE assistant.turns y SET context_id=x.id,cutoff=x.known_at,pins=pin_market_turn.pins,recorded_at=clock_timestamp() WHERE y.id=t.id RETURNING y.* INTO t;
 RETURN jsonb_build_object('turn',assistant.owner_turn(t));
END $function$;

CREATE FUNCTION assistant.pin_turn(turn uuid, token bytea, effective_date date, pins jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE t assistant.turns:=assistant.fenced_turn(turn,token);c security.request_contexts:=assistant.grant_actor(true);
BEGIN

 IF t.page_report ? 'businessDate' THEN
  IF(t.owner_id,t.tenant_id,t.workspace_id) IS DISTINCT FROM(c.actor_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
  IF t.page_report->>'page' IS DISTINCT FROM 'facility_quantities' OR jsonb_typeof(t.page_report->'businessDate')<>'string'
   OR t.page_report->>'businessDate'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR t.frame_id IS NOT NULL OR pins IS NOT NULL
   OR to_char((t.page_report->>'businessDate')::date,'YYYY-MM-DD')<>t.page_report->>'businessDate'
   OR t.knowledge_mode='current' AND effective_date IS DISTINCT FROM (t.page_report->>'businessDate')::date
   OR t.knowledge_mode='retained' AND NOT EXISTS(SELECT 1 FROM LATERAL security.knowledge_from_inputs(t.catalog_id,t.requested_business_on,t.requested_known_at,t.requested_system_id,t.requested_timeline) x
     WHERE x.id=t.requested_context AND x.catalog_id=t.catalog_id AND x.effective_on=(t.page_report->>'businessDate')::date)
   THEN RAISE EXCEPTION 'turn_input';END IF;
 ELSIF t.page_report->>'page'='facility_quantities' THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF t.knowledge_mode<>'operational' THEN RETURN assistant.pin_market_turn(turn,token,effective_date,pins);END IF;

 IF(t.owner_id,t.tenant_id,t.workspace_id)<>(c.actor_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'No turn ownership' USING ERRCODE='42501';END IF;
 IF t.recorded_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') OR pins IS NOT NULL OR effective_date IS NOT NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 UPDATE assistant.turns x SET cutoff=t.asked_at,recorded_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
 RETURN jsonb_build_object('turn',assistant.turn_state(t));
END$function$;
ALTER FUNCTION assistant.pin_market_turn(uuid,bytea,date,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.pin_market_turn(uuid,bytea,date,jsonb) FROM PUBLIC;
ALTER FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb) TO openetrm_runtime;

-- Displaying an owner's provenance does not register or save a context. Actual
-- financial use must separately bind the reconstructed value in its operation.
CREATE FUNCTION security.read_owner_coordinates(catalog uuid,day date,cutoff timestamptz,system_id text,timeline integer)
RETURNS SETOF security.operation_knowledge LANGUAGE plpgsql ROWS 1 SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;parent security.operation_read_coordinates;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'read_source_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
 AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF q.market_context_id IS NOT NULL THEN
  SELECT s.* INTO STRICT parent FROM unnest(q.read_sources) s WHERE(s.catalog_id,s.id)=(q.market_catalog_id,q.market_context_id);
  IF catalog IS DISTINCT FROM parent.catalog_id OR cutoff>parent.known_at
  THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 END IF;
 RETURN QUERY SELECT * FROM security.knowledge_from_inputs(catalog,day,cutoff,system_id,timeline);
END$$;
ALTER FUNCTION security.read_owner_coordinates(uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_owner_coordinates(uuid,date,timestamptz,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_owner_coordinates(uuid,date,timestamptz,text,integer) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.register_owner_coordinates(catalog uuid,identifier uuid,day date,cutoff timestamptz,system_id text,timeline integer)
RETURNS security.operation_read_coordinates LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source security.operation_read_coordinates;
BEGIN
 source=security.coordinates_from_inputs(catalog,day,cutoff,system_id,timeline);
 IF source.id IS DISTINCT FROM identifier THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 PERFORM security.register_read_source(catalog,identifier,day,cutoff,source.schedule_prefix,system_id,timeline);
 RETURN source;
END$$;
ALTER FUNCTION security.register_owner_coordinates(uuid,uuid,date,timestamptz,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.register_owner_coordinates(uuid,uuid,date,timestamptz,text,integer) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.calendar_read_cut(catalog uuid)
RETURNS TABLE(known_at timestamptz,known_through integer,system_id text,timeline integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_calendar_reader(catalog);
 PERFORM security.certify_knowledge(catalog);
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'calendar_denied' USING ERRCODE='42501';END IF;
 RETURN QUERY SELECT k.certified_through,k.certified_head,k.system_identifier,k.timeline_id
 FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog AND k.certified_through IS NOT NULL;
END$$;
GRANT EXECUTE ON FUNCTION security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_risk_worker;
-- Owner CHECK constraints validate their correlation from explicit coordinates.
GRANT EXECUTE ON FUNCTION security.workspace_context_id(uuid,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;


-- Turn reads reconstruct the admitted inputs only under the exact fenced turn,
-- current agent delegation or its undelegated human owner. Auth issuance itself
-- remains independent of financial input selection.
CREATE FUNCTION assistant.register_turn_inputs(turn uuid,token bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t assistant.turns;q security.request_contexts;agent uuid;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 t=assistant.fenced_turn(turn,token);
 SELECT * INTO STRICT q FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
 AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.expires_at>clock_timestamp();
 SELECT a.principal_id INTO STRICT agent FROM assistant.agents a WHERE a.id=t.agent_id;
 IF(q.tenant_id,q.workspace_id) IS DISTINCT FROM(t.tenant_id,t.workspace_id)
 OR (CASE WHEN(q.actor_id,q.delegation_id) IS NOT DISTINCT FROM(agent,t.delegation_id) THEN false
  WHEN q.actor_id=t.owner_id AND q.delegation_id IS NULL THEN(assistant.grant_actor(true)).id IS DISTINCT FROM q.id
  ELSE true END)
 OR t.state<>'running' OR t.recorded_at IS NULL OR t.knowledge_mode='operational'
 OR NOT assistant.turn_authorized(t) OR security.market_access(t.catalog_id,'ReadMarketData') IS NOT TRUE
 OR t.session_ends_at<=clock_timestamp()
 OR t.asked_at+make_interval(secs=>(SELECT max_elapsed_seconds FROM assistant.agent_versions WHERE(agent_id,version)=(t.agent_id,t.agent_version)))<=clock_timestamp()
 THEN RAISE EXCEPTION 'agent_runtime_unavailable' USING ERRCODE='42501';END IF;
 PERFORM security.register_owner_coordinates(t.catalog_id,t.context_id,t.input_business_on,t.input_known_at,t.input_system_id,t.input_timeline);
END$$;
ALTER FUNCTION assistant.register_turn_inputs(uuid,bytea) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.register_turn_inputs(uuid,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.register_turn_inputs(uuid,bytea) TO openetrm_runtime;

-- A logical scheduled occurrence owns its first selection independently of a
-- process, lease attempt or request token. Admission commits before product work.
ALTER TABLE automation.runs ADD COLUMN input_catalog_id uuid REFERENCES market_data.catalogs(id),
 ADD COLUMN input_business_on date,ADD COLUMN input_known_at timestamptz,
 ADD COLUMN input_system_id text,ADD COLUMN input_timeline integer,ADD COLUMN input_policy text,
 ADD CONSTRAINT occurrence_inputs_complete CHECK(
  input_catalog_id IS NULL AND input_business_on IS NULL AND input_known_at IS NULL AND input_system_id IS NULL AND input_timeline IS NULL AND input_policy IS NULL
  OR input_catalog_id IS NOT NULL AND input_business_on IS NOT NULL AND input_known_at IS NOT NULL AND isfinite(input_known_at)
   AND input_system_id IS NOT NULL AND btrim(input_system_id)<>'' AND input_timeline IS NOT NULL AND input_timeline>0
   AND input_business_on>=DATE '0001-01-01' AND input_business_on<DATE '9999-12-31' AND input_policy IS NOT NULL AND input_policy IN('current_at_first_admission_v1','current_knowledge_at_execution_v1'));
CREATE FUNCTION automation.guard_occurrence_inputs() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF OLD.input_catalog_id IS NOT NULL AND
  (NEW.input_catalog_id,NEW.input_business_on,NEW.input_known_at,NEW.input_system_id,NEW.input_timeline,NEW.input_policy)
  IS DISTINCT FROM(OLD.input_catalog_id,OLD.input_business_on,OLD.input_known_at,OLD.input_system_id,OLD.input_timeline,OLD.input_policy)
 THEN RAISE EXCEPTION 'occurrence_inputs_immutable' USING ERRCODE='55000';END IF;
 RETURN NEW;
END$$;
REVOKE ALL ON FUNCTION automation.guard_occurrence_inputs() FROM PUBLIC;
CREATE TRIGGER occurrence_inputs_immutable BEFORE UPDATE OF input_catalog_id,input_business_on,input_known_at,input_system_id,input_timeline,input_policy
 ON automation.runs FOR EACH ROW EXECUTE FUNCTION automation.guard_occurrence_inputs();
CREATE FUNCTION automation.admit_occurrence_inputs() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;catalog uuid;day date;policy text;source security.operation_read_coordinates;
BEGIN
 r=automation.lock_pilot_run();SELECT * INTO STRICT j FROM automation.jobs WHERE id=r.job_id;
 IF j.operation='generated_pilot_close_v1' THEN
  SELECT p.catalog_id INTO STRICT catalog FROM automation.pilot_close_profiles p WHERE(p.id,p.version)=(j.pilot_profile_id,j.pilot_profile_version);
  day=(r.due_at AT TIME ZONE 'UTC')::date;policy='current_at_first_admission_v1';
 ELSIF j.operation='lifecycle_action_v1' THEN
  r=automation.lock_lifecycle_run();
  SELECT p.catalog_id,m.business_date,p.knowledge_policy INTO STRICT catalog,day,policy FROM automation.lifecycle_profiles p
   JOIN market_data.market_closes m ON(m.catalog_id,m.id)=(p.catalog_id,p.close_id) WHERE p.id=j.lifecycle_profile_id;
 ELSE RAISE EXCEPTION 'occurrence_inputs_required' USING ERRCODE='P0001';END IF;
 IF r.input_catalog_id IS NULL THEN
  PERFORM security.resolve_operation_context(catalog,day,NULL);
  SELECT s.* INTO STRICT source FROM security.operation_read_inputs s WHERE(s.catalog_id,s.effective_on)=(catalog,day);
  UPDATE automation.runs SET input_catalog_id=catalog,input_business_on=day,input_known_at=source.known_at,
   input_system_id=source.system_id,input_timeline=source.timeline,input_policy=policy WHERE id=r.id;
 ELSIF(r.input_catalog_id,r.input_business_on,r.input_policy) IS DISTINCT FROM(catalog,day,policy)
 THEN RAISE EXCEPTION 'occurrence_inputs_conflict' USING ERRCODE='P0001';END IF;
 PERFORM automation.lock_pilot_run();
END$$;
ALTER FUNCTION automation.admit_occurrence_inputs() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.admit_occurrence_inputs() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.admit_occurrence_inputs() TO openetrm_runtime;
CREATE FUNCTION automation.occurrence_read_inputs() RETURNS security.operation_read_coordinates
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;source security.operation_read_coordinates;
BEGIN
 r=automation.lock_pilot_run();
 IF r.input_catalog_id IS NULL OR r.input_policy IS NULL
 THEN RAISE EXCEPTION 'occurrence_inputs_required' USING ERRCODE='P0001';END IF;
 source=security.coordinates_from_inputs(r.input_catalog_id,r.input_business_on,r.input_known_at,r.input_system_id,r.input_timeline);
 PERFORM security.register_read_source(source.catalog_id,source.id,source.effective_on,source.known_at,source.schedule_prefix,source.system_id,source.timeline);
 PERFORM automation.lock_pilot_run();RETURN source;
END$$;
ALTER FUNCTION automation.occurrence_read_inputs() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.occurrence_read_inputs() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.occurrence_read_inputs() TO openetrm_runtime;
CREATE OR REPLACE FUNCTION automation.require_lifecycle_review(p automation.lifecycle_profiles, r automation.runs, source security.operation_read_coordinates, proposal jsonb)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE request jsonb=proposal->'request';review jsonb=proposal->'review';offer jsonb=review->'offer';BEGIN
 IF jsonb_typeof(proposal) IS DISTINCT FROM 'object' OR proposal-ARRAY['request','review']<>'{}'::jsonb OR NOT(proposal ?& ARRAY['request','review'])
  OR jsonb_typeof(request) IS DISTINCT FROM 'object' OR request-ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection']<>'{}'::jsonb
  OR NOT(request ?& ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'])
  OR jsonb_typeof(review) IS DISTINCT FROM 'object' OR review-ARRAY['hash','offer','details']<>'{}'::jsonb OR NOT(review ?& ARRAY['hash','offer','details'])
  OR jsonb_typeof(review->'hash') IS DISTINCT FROM 'string' OR coalesce(review->>'hash'!~'^[0-9a-f]{64}$',true)
  OR jsonb_typeof(request->'previewHash') IS DISTINCT FROM 'string' OR request->>'previewHash' IS DISTINCT FROM review->>'hash'
  OR(request->>'operationId')::uuid IS DISTINCT FROM r.id OR(request#>>'{context,contextId}')::uuid IS DISTINCT FROM source.id
  OR(request#>>'{context,snapshotId}')::uuid IS DISTINCT FROM p.close_id
  OR jsonb_typeof(offer) IS DISTINCT FROM 'object' OR offer->'target' IS DISTINCT FROM request->'target' OR offer->>'action' IS DISTINCT FROM p.action
  OR(offer->>'occurrenceId')::uuid IS DISTINCT FROM p.occurrence_id OR offer->'eligible' IS DISTINCT FROM 'true'::jsonb OR jsonb_typeof(review->'details') IS DISTINCT FROM 'object'
 THEN RAISE EXCEPTION 'lifecycle_schedule_result';END IF;
 PERFORM automation.require_lifecycle_action_scope(p.catalog_id,p.action,request,false);
END$function$
;
ALTER FUNCTION automation.require_lifecycle_review(automation.lifecycle_profiles,automation.runs,security.operation_read_coordinates,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.require_lifecycle_review(automation.lifecycle_profiles,automation.runs,security.operation_read_coordinates,jsonb) FROM PUBLIC;

DO $reconstruction$
DECLARE change record;definition text;
BEGIN
 FOR change IN SELECT * FROM(VALUES
('assistant.pin_market_turn(uuid,bytea,date,jsonb)','  context:=t.requested_context;','  PERFORM security.register_owner_coordinates(t.catalog_id,t.requested_context,t.requested_business_on,t.requested_known_at,t.requested_system_id,t.requested_timeline);
  context:=t.requested_context;'),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)',' IF t.recorded_at IS NOT NULL THEN
',' IF t.recorded_at IS NOT NULL THEN
  PERFORM security.register_owner_coordinates(t.catalog_id,t.context_id,t.input_business_on,t.input_known_at,t.input_system_id,t.input_timeline);
'),
('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','  ELSE context:=t.requested_context;END IF;','  ELSE PERFORM security.register_owner_coordinates(t.catalog_id,t.requested_context,t.requested_business_on,t.requested_known_at,t.requested_system_id,t.requested_timeline); context:=t.requested_context;END IF;'),
('assistant.curve_proposal(text,uuid,integer)',' RETURN jsonb_build_object(''input'',arguments',' PERFORM security.register_owner_coordinates(t.catalog_id,t.context_id,t.input_business_on,t.input_known_at,t.input_system_id,t.input_timeline);
 RETURN jsonb_build_object(''input'',arguments'),
('automation.capture_lifecycle_source()',' SELECT source_context_id INTO source FROM automation.lifecycle_sources WHERE context_id=token;
 IF source IS NULL THEN
  source=security.resolve_operation_context(p.catalog_id,anchor.business_date,NULL);
  INSERT INTO automation.lifecycle_sources VALUES(token,p.catalog_id,source);
 END IF;',' source=(automation.occurrence_read_inputs()).id;'),
('automation.publish_lifecycle_result(text,jsonb,jsonb)','source automation.lifecycle_sources','source security.operation_read_coordinates'),
('automation.publish_lifecycle_result(text,jsonb,jsonb)','SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=token;','source=automation.occurrence_read_inputs();'),
('automation.publish_lifecycle_result(text,jsonb,jsonb)','source.source_context_id','source.id'),
('automation.publish_lifecycle_result(text,jsonb,jsonb)','source.context_id','source.id'),
('automation.finish_lifecycle_result()','source automation.lifecycle_sources','source security.operation_read_coordinates'),
('automation.finish_lifecycle_result()','SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=token;','source=automation.occurrence_read_inputs();'),
('automation.finish_lifecycle_result()','source.source_context_id','source.id'),
('automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean)','source automation.lifecycle_sources','source security.operation_read_coordinates'),
('automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean)','SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=token;','source=automation.occurrence_read_inputs();'),
('automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean)','source.source_context_id','source.id'),
('automation.require_lifecycle_action_scope(uuid,text,jsonb,boolean)','source.context_id','source.id')
 )v(signature,original,replacement) LOOP
  definition=pg_get_functiondef(change.signature::regprocedure);
  IF position(change.original in definition)=0 THEN RAISE EXCEPTION 'runtime_input_reconstruction: %',change.signature;END IF;
  EXECUTE replace(definition,change.original,change.replacement);
 END LOOP;
END$reconstruction$;
DO $$DECLARE definition text;
BEGIN
 definition=pg_get_functiondef('trading.seal_scheduled_option_booking()'::regprocedure);
 IF position('source automation.lifecycle_sources' in definition)=0 THEN RAISE EXCEPTION 'runtime_scheduled_booking_inputs';END IF;
 definition=replace(definition,'source automation.lifecycle_sources','source security.operation_read_coordinates');
 definition=replace(definition,'SELECT * INTO source FROM automation.lifecycle_sources WHERE context_id=q.id;','source=automation.occurrence_read_inputs();');
 definition=replace(definition,'source.context_id IS NULL','source.id IS NULL');
 EXECUTE definition;
END$$;
DROP FUNCTION automation.require_lifecycle_review(automation.lifecycle_profiles,automation.runs,automation.lifecycle_sources,jsonb);
ALTER TABLE automation.lifecycle_results DROP CONSTRAINT lifecycle_results_execution_context_id_fkey,
 ADD CONSTRAINT lifecycle_result_authority FOREIGN KEY(execution_context_id) REFERENCES automation.lifecycle_admissions(context_id);
ALTER TABLE trading.scheduled_option_bookings DROP CONSTRAINT scheduled_option_bookings_execution_context_id_fkey,
 ADD CONSTRAINT scheduled_option_booking_authority FOREIGN KEY(execution_context_id) REFERENCES automation.lifecycle_admissions(context_id);
DROP TABLE automation.lifecycle_sources;


DO $$DECLARE definition text;
BEGIN
 definition=pg_get_functiondef('security.record_gas_park_loan_principal_v1(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)'::regprocedure);
 IF position('SET CONSTRAINTS ALL IMMEDIATE' in definition)=0 THEN RAISE EXCEPTION 'runtime_input_review_seals';END IF;
 EXECUTE replace(definition,'SET CONSTRAINTS ALL IMMEDIATE','PERFORM security.check_operation_seals()');
END$$;


DO $owner_receipts$
DECLARE change record;definition text;
BEGIN
 FOR change IN SELECT * FROM(VALUES
('risk.summary_pre_economic_lifecycle(risk.jobs)','''contextId'',job.context_id,','''contextId'',job.context_id,''sourceInputs'',jsonb_build_object(''businessOn'',job.input_business_on,''knownAt'',job.input_known_at,''systemId'',job.input_system_id,''timeline'',job.input_timeline),'),
('security.point_trader_mark_status(uuid,uuid)','''request'',prior.request,','''sourceInputs'',jsonb_build_object(''businessOn'',prior.source_business_on,''knownAt'',prior.source_known_at,''systemId'',prior.source_system_id,''timeline'',prior.source_timeline),''request'',prior.request,'),
('security.operating_policy_fields(text)',' {"key":"retainedContextReserve","label":"Contexts reserved for pages","min":1,"max":64,"initial":64},
','')
 )v(signature,original,replacement) LOOP
  definition=pg_get_functiondef(change.signature::regprocedure);
  IF position(change.original in definition)=0 THEN RAISE EXCEPTION 'runtime_owner_receipt: %',change.signature;END IF;
  EXECUTE replace(definition,change.original,change.replacement);
 END LOOP;
END$owner_receipts$;

-- The installed FK inventory must match the released schema. Every actual owner
-- now has complete immutable input fields; transient auth bindings have none.
DO $retire_relationships$
DECLARE edge record;
BEGIN
 FOR edge IN SELECT c.* FROM pg_constraint c WHERE c.contype='f' AND c.confrelid IN(
  'market_data.temporal_contexts'::regclass,'market_data.market_contexts'::regclass,
  'market_data.catalogue_contexts'::regclass,'trading.contexts'::regclass)
 LOOP
  IF NOT EXISTS(SELECT 1 FROM(VALUES
   ('market_data.market_contexts','market_contexts_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.contexts','contexts_catalog_id_context_id_fkey','market_data.market_contexts'),
   ('market_data.contract_date_versions','contract_date_versions_catalog_id_validation_context_id_fkey','market_data.temporal_contexts'),
   ('market_data.market_closes','market_closes_catalog_id_preflight_context_id_fkey','market_data.temporal_contexts'),
   ('automation.pilot_reports','pilot_reports_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('assistant.turns','turns_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('risk.jobs','jobs_catalog_id_context_id_fkey','trading.contexts'),
   ('automation.close_risk_profiles','close_risk_profiles_catalog_id_context_id_fkey','trading.contexts'),
   ('automation.pilot_report_ppa_quantities','pilot_report_ppa_quantities_catalog_id_read_context_fkey','market_data.temporal_contexts'),
   ('automation.pilot_report_ppa_inputs','pilot_report_ppa_inputs_catalog_id_knowledge_context_fkey','market_data.temporal_contexts'),
   ('settlement_data.swap_determinations','swap_determinations_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.swap_cash_payments','swap_cash_payments_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_capacity_versions','gas_transport_capacity_versio_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.history_boundaries','history_boundaries_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.history_boundaries','history_boundaries_catalog_id_context_id_fkey1','trading.contexts'),
   ('risk.definition_runs','definition_runs_catalog_id_context_id_fkey','trading.contexts'),
   ('trading.gas_quantity_revisions','gas_quantity_revisions_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.crude_operational_events','crude_operational_events_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('market_data.contract_date_admissions','contract_date_admissions_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('risk.history_clock_captures','history_clock_captures_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_cash_determinations','gas_cash_determinations_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_nomination_requests','gas_nomination_requests_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_service_versions','gas_service_versions_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_balance_statements','gas_balance_statements_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_imbalance_agreements','gas_imbalance_agreements_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_operation_responses','gas_operation_responses_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_imbalance_applications','gas_imbalance_applications_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_nomination_applications','gas_nomination_applications_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('market_data.curve_definition_versions','curve_definition_versions_catalog_id_validation_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_capacity_plans','gas_transport_capacity_plans_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_capacity_commands','gas_transport_capacity_comman_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_routes','gas_transport_routes_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_routes','gas_transport_routes_catalog_id_source_context_id_fkey1','trading.contexts'),
   ('trading.gas_transport_route_plans','gas_transport_route_plans_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_route_plans','gas_transport_route_plans_catalog_id_source_context_id_fkey1','trading.contexts'),
   ('trading.gas_transport_route_commands','gas_transport_route_commands_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_route_commands','gas_transport_route_commands_catalog_id_source_context_id_fkey1','trading.contexts'),
   ('trading.gas_transport_financial_states','gas_transport_financial_state_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('market_data.catalogue_contexts','catalogue_contexts_catalog_id_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_financial_marks','gas_transport_financial_marks_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_transport_financial_commands','gas_transport_financial_comma_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('market_data.point_trader_mark_commands','point_trader_mark_commands_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.delivery_route_allocation_plans','delivery_route_allocation_pla_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.delivery_route_allocation_plans','delivery_route_allocation_pl_catalog_id_source_context_id_fkey1','trading.contexts'),
   ('risk.saved_pnl_reports','saved_pnl_reports_catalog_id_opening_context_id_fkey','market_data.temporal_contexts'),
   ('risk.saved_pnl_reports','saved_pnl_reports_catalog_id_closing_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.futures_margin_adoptions','futures_margin_adoptions_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.futures_margin_policies','futures_margin_policies_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.futures_margin_commands','futures_margin_commands_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.futures_margin_runs','futures_margin_runs_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.futures_margin_run_commands','futures_margin_run_commands_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('settlement_data.futures_delivery_profiles','futures_delivery_profiles_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_futures_delivery_principal_runs','gas_futures_delivery_principa_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('automation.lifecycle_results','lifecycle_results_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_futures_quantity_batches','gas_futures_quantity_batches_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_owned_quantity_corrections','gas_owned_quantity_correction_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_quantity_bundles','gas_quantity_bundles_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_loan_quantity_corrections','gas_loan_quantity_corrections_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_loan_fee_changes','gas_loan_fee_changes_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_operator_quantity_corrections','gas_operator_quantity_correct_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.storage_facility_commands','storage_facility_commands_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_park_loan_fee_determinations','gas_park_loan_fee_determinati_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_park_custody_evidence','gas_park_custody_evidence_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_park_custody_acceptances','gas_park_custody_acceptances_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('trading.gas_park_custody_returns','gas_park_custody_returns_catalog_id_source_context_id_fkey','market_data.temporal_contexts'),
   ('security.request_contexts','request_contexts_market_catalog_id_market_context_id_fkey','market_data.market_contexts'),
   ('security.request_contexts','request_business_source_trades','trading.contexts'),
   ('security.request_contexts','request_business_source_market','market_data.temporal_contexts')
  )v(child,constraint_name,parent) WHERE(edge.conrelid,edge.conname,edge.confrelid)=(v.child::regclass,v.constraint_name::name,v.parent::regclass))
  THEN RAISE EXCEPTION 'runtime_context_unknown_owner: %',edge.conname;END IF;
  EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I',edge.conrelid::regclass,edge.conname);
 END LOOP;
 FOR edge IN SELECT tgrelid,tgname FROM pg_trigger WHERE tgfoid='security.retain_referenced_workspace_cut()'::regprocedure
 LOOP EXECUTE format('DROP TRIGGER %I ON %s',edge.tgname,edge.tgrelid::regclass);END LOOP;
END$retire_relationships$;
DROP FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb,integer);
DROP FUNCTION assistant.pin_market_turn(uuid,bytea,date,jsonb,integer);
DROP FUNCTION security.capture_schedule_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_market_and_schedule_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_noncollection_date_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_custom_curve_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_contract_dates_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_pilot_close_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_pilot_report_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_interval_series_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_curve_activation_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.certify_or_capture_date_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_date_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_ppa_portfolio_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_close_risk_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_meter_finality_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_definition_run_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_gas_setup_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_contract_admission_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_crude_setup_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_curve_definitions_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.capture_pre_catalogue_context(uuid,date,timestamp with time zone);
DROP FUNCTION security.store_schedule_cut(uuid,date,timestamp with time zone,integer,timestamp with time zone,timestamp with time zone);
DROP FUNCTION security.retain_referenced_workspace_cut();
DROP FUNCTION security.finish_workspace_read();
DROP FUNCTION security.bind_calendar_coordinates(uuid,uuid,date,timestamp with time zone,integer,text,integer);
DROP FUNCTION security.workspace_read_cut(uuid,date,timestamp with time zone,boolean);
DROP FUNCTION security.bind_workspace_read(uuid,uuid,date,timestamp with time zone,integer,text,integer);
DROP FUNCTION security.bind_calendar_read(uuid,uuid,date,timestamp with time zone,integer,text,integer);
DROP FUNCTION security.store_workspace_source(uuid,uuid,date,timestamp with time zone,integer);
DROP FUNCTION risk.capture_history_clock(uuid,uuid,jsonb,jsonb);
DROP FUNCTION security.retain_workspace_write_source(uuid,uuid,date,timestamp with time zone,integer,text,integer);
DROP VIEW security.read_trading_contexts;
DROP VIEW security.read_catalogue_contexts;
DROP VIEW security.read_market_contexts;
DROP VIEW security.read_temporal_contexts;
DROP TABLE trading.contexts;
DROP TABLE market_data.catalogue_contexts;
DROP TABLE market_data.market_contexts;
DROP TABLE market_data.temporal_contexts;

DO $complete$
DECLARE obsolete text;
BEGIN
 SELECT string_agg(p.oid::regprocedure::text,', ' ORDER BY p.oid::regprocedure::text) INTO obsolete
 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname NOT IN('pg_catalog','information_schema') AND
 p.prosrc ~ '(market_data\.(temporal_contexts|market_contexts|catalogue_contexts)|trading\.contexts|security\.read_(temporal|market|catalogue|trading)_contexts|automation\.lifecycle_sources)';
 IF obsolete IS NOT NULL THEN RAISE EXCEPTION 'runtime_context_incomplete: %',obsolete;END IF;
END$complete$;


-- The HTTP command owns its original admission request. Exact retries resolve
-- this receipt before reopening tab-local transport or preparing financial work.
-- Native prepared jobs have their own request contract and no HTTP submission.
ALTER TABLE risk.jobs ADD COLUMN http_submission jsonb,
 ADD CONSTRAINT http_submission_object CHECK(http_submission IS NULL OR jsonb_typeof(http_submission)='object');
CREATE OR REPLACE FUNCTION risk.guard_inputs() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR OLD.http_submission IS NOT NULL AND NEW.http_submission IS DISTINCT FROM OLD.http_submission
 OR(to_jsonb(NEW)-ARRAY['state','attempt','worker_login','lease_token','lease_expires_at','attempt_security_generation','started_at','finished_at','result','error_code','http_submission'])
 IS DISTINCT FROM(to_jsonb(OLD)-ARRAY['state','attempt','worker_login','lease_token','lease_expires_at','attempt_security_generation','started_at','finished_at','result','error_code','http_submission'])
 THEN RAISE EXCEPTION 'risk_job_immutable' USING ERRCODE='55000';END IF;
 RETURN NEW;
END$$;
CREATE FUNCTION risk.replay_submission(catalog uuid,operation uuid,submission jsonb) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;job risk.jobs;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 SELECT * INTO c FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF c.id IS NULL OR security.has_permission('RunScenario') IS NOT TRUE
 OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid
 OR jsonb_typeof(submission) IS DISTINCT FROM 'object' OR pg_column_size(submission)>1000000
 THEN RAISE EXCEPTION 'risk_job_input' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(c.tenant_id::text||c.workspace_id::text||c.actor_id::text||operation::text,730849136));
 SELECT * INTO job FROM risk.jobs WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF job.catalog_id IS DISTINCT FROM catalog OR job.delegation_id IS DISTINCT FROM c.delegation_id OR job.http_submission IS DISTINCT FROM submission
 THEN RAISE EXCEPTION 'risk_job_conflict' USING ERRCODE='P0001';END IF;
 job=risk.owner(job.id);RETURN job.id;
END$$;
ALTER FUNCTION risk.replay_submission(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.replay_submission(uuid,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.replay_submission(uuid,uuid,jsonb) TO openetrm_runtime;
CREATE FUNCTION risk.submit_portfolio(catalog uuid,operation uuid,context uuid,revision uuid,valuation_at timestamptz,request jsonb,frame jsonb,submission jsonb)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE existing uuid;receipt jsonb;
BEGIN
 existing=risk.replay_submission(catalog,operation,submission);
 IF existing IS NOT NULL THEN RETURN risk.summary(risk.owner(existing));END IF;
 receipt=risk.submit(catalog,operation,context,revision,valuation_at,request,frame);
 UPDATE risk.jobs SET http_submission=submission WHERE id=(receipt->>'jobId')::uuid AND http_submission IS NULL;
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_job_conflict' USING ERRCODE='P0001';END IF;
 RETURN receipt;
END$$;
ALTER FUNCTION risk.submit_portfolio(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.submit_portfolio(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.submit_portfolio(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb,jsonb) TO openetrm_runtime;


-- A new policy is validated before INSERT. Construct its owner input fields
-- from this operation before passing that row to the shared policy validator.
DO $$DECLARE definition text;original text=' p.source_context_id=context;p.actor_id=actor;';
BEGIN
 definition=pg_get_functiondef('settlement_data.save_futures_margin_policy(uuid,jsonb)'::regprocedure);
 IF position(original in definition)=0 THEN RAISE EXCEPTION 'runtime_margin_policy_input_constructor';END IF;
 EXECUTE replace(definition,original,original||'
 SELECT s.effective_on,s.known_at,s.system_id,s.timeline
 INTO p.source_business_on,p.source_known_at,p.source_system_id,p.source_timeline
 FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(catalog,context);
 IF NOT FOUND THEN RAISE EXCEPTION ''read_inputs_required'' USING ERRCODE=''P0001'';END IF;');
END$$;


-- Reopen the recorded inputs of one owned question. The turn id authorizes an
-- owner read; its correlation never serves as a generic saved-context key.
CREATE FUNCTION assistant.turn_read_inputs(scope text,turn uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;t assistant.turns;
BEGIN
 c=assistant.grant_actor(false);
 SELECT x.* INTO t FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
 WHERE x.id=turn_read_inputs.turn AND(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,turn_read_inputs.scope);
 IF t.id IS NULL OR NOT assistant.sources_readable(t.id) THEN RAISE EXCEPTION 'turn_not_found' USING ERRCODE='P0001';END IF;
 IF t.recorded_at IS NULL OR t.knowledge_mode='operational' OR t.input_known_at IS NULL
 THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 RETURN jsonb_build_object('catalogId',t.catalog_id,'contextId',t.context_id,'businessOn',t.input_business_on,'knownAt',t.input_known_at,'systemId',t.input_system_id,'timeline',t.input_timeline);
END$$;
ALTER FUNCTION assistant.turn_read_inputs(text,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.turn_read_inputs(text,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.turn_read_inputs(text,uuid) TO openetrm_runtime;


DO $$DECLARE definition text;original text=' r=automation.check_close_risk_run();';
BEGIN
 definition=pg_get_functiondef('automation.close_risk_run_definition()'::regprocedure);
 IF position(original in definition)=0 THEN RAISE EXCEPTION 'runtime_close_job_inputs';END IF;
 -- Both pre- and post-read fences reconstruct the same admitted profile inputs.
 EXECUTE replace(definition,original,original||'
 PERFORM security.register_owner_coordinates(p.catalog_id,p.context_id,p.input_business_on,p.input_known_at,p.input_system_id,p.input_timeline)
 FROM automation.jobs j JOIN automation.close_risk_profiles p ON p.id=j.close_risk_profile_id
 WHERE j.id=r.job_id AND j.operation=''close_book_risk_v1'';');
END$$;

-- Generated marks use the occurrence's first admitted inputs. Their report has
-- a distinct declared cutoff: the generated close's actual commit, on the same
-- original database lineage. Native producers enforce both policies themselves.
DO $generated_inputs$
DECLARE change record;p pg_proc;definition text;original text;
BEGIN
 FOR change IN SELECT * FROM(VALUES
  ('automation.publish_pilot_close(jsonb,jsonb,jsonb)','cd73482351a17e519eddf818824ec7d1e9ee94e7cbd8d1b75a3964ee256b0738',
   ' IF r.input_catalog_id IS NULL THEN RAISE EXCEPTION ''occurrence_inputs_required'' USING ERRCODE=''P0001'';END IF;
 IF r.input_policy IS DISTINCT FROM ''current_at_first_admission_v1'' OR NOT EXISTS(
  SELECT 1 FROM security.operation_read_inputs s
  WHERE(s.catalog_id,s.id)=(q.market_catalog_id,q.market_context_id)
  AND(s.catalog_id,s.effective_on,s.known_at,s.system_id,s.timeline)=
     (r.input_catalog_id,r.input_business_on,r.input_known_at,r.input_system_id,r.input_timeline)
  AND s.catalog_id=p.catalog_id)
 THEN RAISE EXCEPTION ''occurrence_inputs_conflict'' USING ERRCODE=''P0001'';END IF;
'),
  ('automation.publish_pilot_report_pre_renewal(uuid,jsonb)','af66a828a98e104c9c82cefa2079f7ef3e8b55c815836bdf901ca9b3ed22be9f',
   ' IF report_mode=''generated_close'' THEN
  IF r.input_catalog_id IS NULL THEN RAISE EXCEPTION ''occurrence_inputs_required'' USING ERRCODE=''P0001'';END IF;
  IF r.input_policy IS DISTINCT FROM ''current_at_first_admission_v1'' OR r.input_catalog_id IS DISTINCT FROM c.catalog_id
   OR NOT EXISTS(SELECT 1 FROM security.operation_read_inputs s
    WHERE(s.catalog_id,s.id)=(q.market_catalog_id,q.market_context_id)
    AND(s.catalog_id,s.effective_on,s.known_at,s.system_id,s.timeline)=
       (c.catalog_id,c.business_date,security.checked_commit_time(c.publication_xid),r.input_system_id,r.input_timeline))
  THEN RAISE EXCEPTION ''pilot_report_source'' USING ERRCODE=''P0001'';END IF;
 END IF;
')
 )v(signature,expected,guard) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=change.signature::regprocedure;
  definition=pg_get_functiondef(p.oid);
  IF encode(sha256(convert_to(definition||p.proowner::regrole::text||coalesce(p.proacl::text,''),'UTF8')),'hex')<>change.expected
  THEN RAISE EXCEPTION 'runtime_generated_producer_changed: %',change.signature USING ERRCODE='55000';END IF;
  original=' SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;';
  IF position(original in definition)=0 THEN RAISE EXCEPTION 'runtime_generated_producer_shape: %',change.signature;END IF;
  EXECUTE replace(definition,original,original||E'\n'||change.guard);
 END LOOP;
END$generated_inputs$;

-- Each issuer admission has its own consumption key. Repeated captures in one
-- runtime transaction must not wait on that transaction's prior uncommitted
-- deletion, including when their clock evidence is identical.
DO $$DECLARE p pg_proc;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb)'::regprocedure;
 IF encode(sha256(convert_to(pg_get_functiondef(p.oid)||p.proowner::regrole::text||coalesce(p.proacl::text,''),'UTF8')),'hex')<>
  'b29bd0081603ec5fded226102d5d9c7b88eaf5ad1cc1167cf7cf8c941749bb61'
 THEN RAISE EXCEPTION 'runtime_clock_issuer_changed' USING ERRCODE='55000';END IF;
END$$;
ALTER TABLE risk.pending_clock_attestations DROP CONSTRAINT pending_clock_attestations_pkey,
 ADD COLUMN id uuid NOT NULL DEFAULT gen_random_uuid(),ADD PRIMARY KEY(id);
DROP FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb);
CREATE FUNCTION risk.attest_workspace_clock(pid integer,transaction_id xid8,catalog uuid,context_id uuid,calendar jsonb,clock jsonb) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE issued uuid;
BEGIN
 IF NOT pg_has_role(session_user,'openetrm_authenticator','MEMBER') OR pg_has_role(session_user,'openetrm_runtime','MEMBER')
 OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RAISE EXCEPTION 'risk_capture_issuer' USING ERRCODE='42501';END IF;
 IF pg_xact_status($2) IS DISTINCT FROM 'in progress' OR NOT EXISTS(SELECT 1 FROM pg_locks l WHERE l.pid=$1 AND l.granted
  AND l.locktype='transactionid' AND l.mode='ExclusiveLock' AND l.transactionid=$2::xid)
 THEN RAISE EXCEPTION 'risk_capture_input';END IF;
 DELETE FROM risk.pending_clock_attestations a WHERE pg_xact_status(a.transaction_id) IS DISTINCT FROM 'in progress';
 INSERT INTO risk.pending_clock_attestations(backend_pid,transaction_id,proof)
 VALUES($1,$2,risk.clock_attestation($3,$4,$5,$6)) RETURNING id INTO issued;
 RETURN issued;
END$$;
ALTER FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) TO openetrm_authenticator;

DROP FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb);
CREATE FUNCTION risk.capture_workspace_clock(catalog uuid,context_id uuid,calendar jsonb,clock jsonb,attestation uuid) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result uuid;
BEGIN
 IF security.market_access($1,'ReadMarketData') IS NOT TRUE OR security.trading_access($1,'ReadTrades') IS NOT TRUE
 OR security.has_permission('RunScenario') IS NOT TRUE OR NOT EXISTS(SELECT 1 FROM security.request_contexts q
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
  AND q.expires_at>clock_timestamp() AND (q.market_catalog_id,q.market_context_id)=($1,$2))
 THEN RAISE EXCEPTION 'risk_capture_input' USING ERRCODE='42501';END IF;
 DELETE FROM risk.pending_clock_attestations a WHERE a.id=$5 AND a.backend_pid=pg_backend_pid()
 AND a.transaction_id=pg_current_xact_id_if_assigned() AND a.proof=risk.clock_attestation($1,$2,$3,$4);
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_capture_issuer' USING ERRCODE='42501';END IF;
 result=risk.store_history_clock($1,$2,$3,$4);
 IF security.market_access($1,'ReadMarketData') IS NOT TRUE OR security.trading_access($1,'ReadTrades') IS NOT TRUE
 OR security.has_permission('RunScenario') IS NOT TRUE THEN RAISE EXCEPTION 'risk_capture_input' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb,uuid) TO openetrm_runtime;

-- A new command recording an already-admitted operating fact keeps that fact's
-- original inputs, rather than replacing them with the new request's cutoff.
DO $$DECLARE change record;p pg_proc;definition text;
BEGIN
 FOR change IN SELECT * FROM(VALUES
  ('security.record_storage_facility(uuid,uuid,jsonb,timestamptz,jsonb,uuid)',
   '3639e72ebe12798b75781ffd96652cddc3bb9937f8627003f1405e8f6c66c8af',
   'INSERT INTO trading.storage_facility_commands VALUES(catalog,actor,operation,facility,jsonb_set(payload,''{contextId}'',to_jsonb(prior.source_context_id)),fact_key,result,deadline,prior.source_context_id);'),
  ('security.record_gas_transport_capacity(uuid,uuid,jsonb,timestamptz,jsonb,uuid)',
   'b777be0a3493bb7dc676786fdf0095ff03e0a1c51bf4a9e61abaea2ab82ec0c7',
   'source_context=prior.source_context_id;payload=jsonb_set(payload,''{contextId}'',to_jsonb(source_context));'),
  ('security.record_gas_transport_route(uuid,uuid,jsonb,timestamptz,jsonb,uuid)',
   '262c251ec5b936e2b4f0ddafb8325ad4b62ac4d301be9452362bee66c61eeeb6',
   'source_context=prior.source_context_id;payload=jsonb_set(payload,''{contextId}'',to_jsonb(source_context));')
 )v(signature,expected,original) LOOP
 SELECT * INTO STRICT p FROM pg_proc WHERE oid=change.signature::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition||p.proowner::regrole::text||coalesce(p.proacl::text,''),'UTF8')),'hex')<>change.expected
  OR position(change.original in definition)=0
 THEN RAISE EXCEPTION 'runtime_fact_consumer_changed: %',change.signature USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,change.original,
  'PERFORM security.register_owner_coordinates(catalog,prior.source_context_id,prior.source_business_on,prior.source_known_at,prior.source_system_id,prior.source_timeline);'||E'\n'||change.original);
 END LOOP;
END$$;

COMMIT;
