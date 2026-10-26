-- PPA reads use the same retained portfolio context and restricted risk worker.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
GRANT USAGE ON SCHEMA power_data TO openetrm_risk_worker;
DO $$DECLARE item record;p record;t text;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('market_data',ARRAY['generation_assets','generation_asset_versions','volume_sources','location_valuation_assignments','curve_actual_rules','shaped_curve_actual_rules','curve_physical_price_sources']),
  ('power_data',ARRAY['sources'])
 )v(schema_name,tables) LOOP
  FOREACH t IN ARRAY item.tables LOOP
   IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname=item.schema_name AND tablename=t AND 'openetrm_runtime'=ANY(roles) AND cmd IN('SELECT','ALL') AND permissive='PERMISSIVE') THEN RAISE EXCEPTION 'ppa_worker_policy_missing';END IF;
   FOR p IN SELECT * FROM pg_policies WHERE schemaname=item.schema_name AND tablename=t AND 'openetrm_runtime'=ANY(roles) AND cmd IN('SELECT','ALL') LOOP
    EXECUTE format('CREATE POLICY %I ON %I.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)','risk_worker_'||p.policyname,p.schemaname,p.tablename,p.permissive,p.qual);
   END LOOP;
   EXECUTE format('CREATE POLICY risk_worker_service ON %I.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',item.schema_name,t);
   EXECUTE format('GRANT SELECT ON %I.%I TO openetrm_risk_worker',item.schema_name,t);
  END LOOP;
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION
 security.ppa_read_scope(uuid,text,uuid[],uuid,timestamptz,boolean),security.read_ppa_terms(uuid,uuid),
 security.ppa_require_market(uuid,uuid,uuid,uuid,uuid,timestamptz,uuid[]),
 security.ppa_tou_versions(uuid,uuid,uuid,uuid,uuid,timestamptz,jsonb),
 security.ppa_discount_default(uuid,uuid,uuid,text,uuid,timestamptz,uuid),
 market_data.read_interval_series_header(uuid),market_data.read_interval_series(uuid,timestamptz,timestamptz,timestamptz),
 market_data.interval_series_changes(uuid,timestamptz,timestamptz,timestamptz),
 power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz)
 TO openetrm_risk_worker;

CREATE TABLE risk.worker_attempt_contexts (
 context_id uuid PRIMARY KEY REFERENCES security.request_contexts(id) ON DELETE CASCADE,
 job_id uuid NOT NULL REFERENCES risk.jobs(id),lease_token uuid NOT NULL
);
ALTER TABLE risk.worker_attempt_contexts ENABLE ROW LEVEL SECURITY;
ALTER TABLE risk.worker_attempt_contexts FORCE ROW LEVEL SECURITY;
REVOKE ALL ON risk.worker_attempt_contexts FROM PUBLIC;
GRANT SELECT,INSERT ON risk.worker_attempt_contexts TO openetrm_security_owner;
ALTER FUNCTION risk.bind(uuid,uuid) RENAME TO bind_pre_ppa_context;
REVOKE ALL ON FUNCTION risk.bind_pre_ppa_context(uuid,uuid) FROM openetrm_risk_worker;
CREATE FUNCTION risk.bind(job_id uuid,token uuid) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;BEGIN
 context=risk.bind_pre_ppa_context(job_id,token);
 PERFORM risk.owned_attempt(job_id,token,false,false);
 INSERT INTO risk.worker_attempt_contexts VALUES(context,job_id,token);RETURN context;
END$$;
ALTER FUNCTION risk.bind(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.bind(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.bind(uuid,uuid) TO openetrm_risk_worker;

-- A worker may only look up interval contexts admitted with its retained job.
-- It cannot certify newly published knowledge while loading a queued request.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_ppa_portfolio_context;
REVOKE ALL ON FUNCTION security.capture_pre_ppa_portfolio_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;j risk.jobs;bound_lease uuid;token uuid;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  IF NOT risk.worker_read() OR NOT security.market_access(catalog,'ReadMarketData') OR catalog IS DISTINCT FROM q.market_catalog_id THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  SELECT b.lease_token INTO bound_lease FROM risk.worker_attempt_contexts b WHERE b.context_id=q.id;
  SELECT jobs.* INTO j FROM risk.worker_attempt_contexts b JOIN risk.jobs jobs ON jobs.id=b.job_id WHERE b.context_id=q.id;
  IF NOT FOUND OR j.catalog_id IS DISTINCT FROM catalog OR j.context_id IS DISTINCT FROM q.market_context_id OR j.actor_id IS DISTINCT FROM q.actor_id THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
  PERFORM risk.owned_attempt(j.id,bound_lease,false,false);
  SELECT t.id INTO token FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.effective_on,t.known_at)=(catalog,effective_date,requested_at)
   AND t.id IN(SELECT value::uuid FROM jsonb_array_elements_text(j.frame->'ppaReadContexts'));
  IF token IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
  PERFORM risk.owned_attempt(j.id,bound_lease,false,false);RETURN token;
 END IF;
 RETURN security.capture_pre_ppa_portfolio_context(catalog,effective_date,requested_at);
END$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_date_context(uuid,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION market_data.frozen_observation_knowledge(catalog uuid,definition uuid,pins uuid[],context uuid,cutoff timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.market_fact_visible(catalog,definition,NULL)
  OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id,t.known_at)=(catalog,context,cutoff))
 THEN RAISE EXCEPTION 'No market access' USING ERRCODE='42501';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',o.id,'known_from',k.known_from)),'[]') INTO result FROM market_data.observations o
 JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
 JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
 JOIN market_data.market_contexts c ON c.catalog_id=e.catalog_id AND c.context_id=context
 WHERE o.catalog_id=catalog AND o.definition_revision_id=definition AND o.id=ANY(pins) AND e.event_order<=c.known_through AND k.known_from<=cutoff
  AND security.market_fact_visible(o.catalog_id,o.definition_revision_id,o.id);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Market access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION market_data.frozen_observation_knowledge(uuid,uuid,uuid[],uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.frozen_observation_knowledge(uuid,uuid,uuid[],uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.frozen_observation_knowledge(uuid,uuid,uuid[],uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

-- Definer readers check both represented-user authority and the exact current
-- worker attempt. Their owners cannot use RLS bypass to preserve revoked service rights.
CREATE FUNCTION risk.require_worker_attempt() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE binding risk.worker_attempt_contexts;BEGIN
 IF NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN;END IF;
 SELECT b.* INTO binding FROM risk.worker_attempt_contexts b JOIN security.request_contexts q ON q.id=b.context_id
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF NOT FOUND OR NOT risk.worker_read() THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 PERFORM risk.owned_attempt(binding.job_id,binding.lease_token,false,false);
END$$;
ALTER FUNCTION risk.require_worker_attempt() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.require_worker_attempt() FROM PUBLIC;
DO $wrappers$DECLARE signature regprocedure;p record;arguments text;result_type text;original text;call_arguments text;body text;definition text;header text;BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'security.ppa_read_scope(uuid,text,uuid[],uuid,timestamptz,boolean)'::regprocedure,
  'security.read_ppa_terms(uuid,uuid)'::regprocedure,
  'security.ppa_require_market(uuid,uuid,uuid,uuid,uuid,timestamptz,uuid[])'::regprocedure,
  'security.ppa_tou_versions(uuid,uuid,uuid,uuid,uuid,timestamptz,jsonb)'::regprocedure,
  'security.ppa_discount_default(uuid,uuid,uuid,text,uuid,timestamptz,uuid)'::regprocedure,
  'market_data.read_interval_series_header(uuid)'::regprocedure,
  'market_data.read_interval_series(uuid,timestamptz,timestamptz,timestamptz)'::regprocedure,
  'market_data.interval_series_changes(uuid,timestamptz,timestamptz,timestamptz)'::regprocedure,
  'power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz)'::regprocedure,
  'market_data.frozen_observation_knowledge(uuid,uuid,uuid[],uuid,timestamptz)'::regprocedure
 ] LOOP
  SELECT n.nspname,proc.proname,proc.pronargs,proc.proretset,pg_get_userbyid(proc.proowner) AS owner INTO STRICT p FROM pg_proc proc JOIN pg_namespace n ON n.oid=proc.pronamespace WHERE proc.oid=signature;
  arguments=pg_get_function_arguments(signature);result_type=pg_get_function_result(signature);
  IF result_type NOT IN('jsonb','void','SETOF jsonb') THEN RAISE EXCEPTION 'ppa_worker_reader_shape';END IF;
  original=p.proname||'_pre_portfolio_worker';
  SELECT string_agg('$'||n,',' ORDER BY n) INTO call_arguments FROM generate_series(1,p.pronargs) n;
  definition=pg_get_functiondef(signature);header=format('CREATE OR REPLACE FUNCTION %I.%I(',p.nspname,p.proname);
  IF position(header IN definition)<>1 THEN RAISE EXCEPTION 'ppa_worker_reader_shape';END IF;
  EXECUTE replace(definition,header,format('CREATE OR REPLACE FUNCTION %I.%I(',p.nspname,original));
  EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO %I',p.nspname,original,pg_get_function_identity_arguments(signature),p.owner);
  EXECUTE format('REVOKE ALL ON FUNCTION %I.%I(%s) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',p.nspname,original,pg_get_function_identity_arguments(signature));
  EXECUTE format('GRANT EXECUTE ON FUNCTION %I.%I(%s) TO openetrm_security_owner',p.nspname,original,pg_get_function_identity_arguments(signature));
  IF result_type='void' THEN
   body=format('BEGIN PERFORM risk.require_worker_attempt();PERFORM %I.%I(%s);PERFORM risk.require_worker_attempt();END',p.nspname,original,call_arguments);
  ELSIF p.proretset THEN
   body=format('DECLARE result jsonb[];BEGIN PERFORM risk.require_worker_attempt();SELECT coalesce(array_agg(value),ARRAY[]::jsonb[]) INTO result FROM %I.%I(%s) value;PERFORM risk.require_worker_attempt();RETURN QUERY SELECT * FROM unnest(result);END',p.nspname,original,call_arguments);
  ELSE
   body=format('DECLARE result jsonb;BEGIN PERFORM risk.require_worker_attempt();result=%I.%I(%s);PERFORM risk.require_worker_attempt();RETURN result;END',p.nspname,original,call_arguments);
  END IF;
  EXECUTE format('CREATE OR REPLACE FUNCTION %I.%I(%s) RETURNS %s LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS %L',p.nspname,p.proname,arguments,result_type,body);
  EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO openetrm_security_owner',p.nspname,p.proname,pg_get_function_identity_arguments(signature));
  EXECUTE format('REVOKE ALL ON FUNCTION %I.%I(%s) FROM PUBLIC',p.nspname,p.proname,pg_get_function_identity_arguments(signature));
  EXECUTE format('GRANT EXECUTE ON FUNCTION %I.%I(%s) TO openetrm_runtime,openetrm_risk_worker',p.nspname,p.proname,pg_get_function_identity_arguments(signature));
 END LOOP;
END$wrappers$;
COMMIT;
