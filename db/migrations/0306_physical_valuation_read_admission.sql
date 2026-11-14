-- Current consumers: selected physical option value and its optional setup
-- metadata. Read-only workers call only these fixed-permission boundaries;
-- neither raw authorization primitive gains another EXECUTE grant.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
DO $required$BEGIN
 IF to_regprocedure('security.authority_until(text)') IS NULL
  OR to_regprocedure('security.has_permission(text)') IS NULL
 THEN RAISE EXCEPTION 'physical_valuation_read_upgrade: required authorization kernel missing';END IF;
END$required$;

CREATE FUNCTION security.capture_physical_valuation_read() RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE original timestamptz;permitted boolean;BEGIN
 -- This must precede any current-permission epoch wait. A later grant cannot
 -- replace the original bound, including in an already-bound worker attempt.
 original=security.authority_until('ReadGasOwnedHoldings');
 permitted=security.has_permission('ReadGasOwnedHoldings');
 permitted=permitted IS TRUE AND original IS NOT NULL AND isfinite(original) AND original>clock_timestamp();
 RETURN jsonb_build_object('implementation','physical_valuation_read_admission_v1','allowed',permitted,'authorityUntil',original);
END$$;

CREATE FUNCTION security.require_physical_valuation_read(until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE permitted boolean;BEGIN
 permitted=security.has_permission('ReadGasOwnedHoldings');
 -- Test the clock after the current permission check can wait. Bound worker
 -- purpose, lease, actor/service and original expiry remain kernel guards.
 IF permitted IS NOT TRUE OR until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at
 THEN RAISE EXCEPTION 'physical_valuation_read_denied' USING ERRCODE='42501';END IF;
END$$;

-- Known option source facts have their own original reference/read cutoff and
-- exact admitted-source receipt. The action-runtime origin primitive remains
-- private to its existing callers.
CREATE FUNCTION lifecycle.require_option_history_read(catalog uuid,trade uuid,binding uuid,revision integer,
 context uuid,as_of timestamptz,business_date date,source_until timestamptz,history_until timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF job.request->>'convention' IS DISTINCT FROM 'portfolio_instrument_repricing_v1'
   OR NULLIF(job.request->'economic','null'::jsonb) IS NULL AND NULLIF(job.request#>'{pricingPolicy,futuresOptions,physical}','null'::jsonb) IS NULL
   OR(catalog,context,as_of,business_date) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at,(job.frame->>'businessDate')::date)
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)s WHERE(s.trade_id,s.binding_id,s.revision,s.kind)=($2,$3,$4,'option'))
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,source_until);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,history_until);
 IF NOT EXISTS(SELECT 1 FROM trading.futures_option_trade_revisions r
  WHERE(r.catalog_id,r.trade_id,r.option_instrument_id,r.revision)=($1,$2,$3,$4)
   AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at))
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM lifecycle.require_option_origin(catalog,trade,revision,history_until);
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,source_until);
 IF source_until IS NULL OR history_until IS NULL OR NOT isfinite(source_until) OR NOT isfinite(history_until)
  OR clock_timestamp()>=least(source_until,history_until)
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.capture_physical_valuation_read() OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_physical_valuation_read(timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_physical_valuation_read(),security.require_physical_valuation_read(timestamptz),
 lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz)
 FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.capture_physical_valuation_read(),security.require_physical_valuation_read(timestamptz),
 lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz)
 TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
