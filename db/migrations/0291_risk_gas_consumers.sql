-- Consumers: retained option and futures valuation. An unused action profile
-- does not add a permission to ordinary vanilla valuation; actual physical
-- continuation and native physical margin-state reads retain their Gas guard.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.needs_gas(risk.jobs)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_gas_consumers_upgrade: missing helper risk.needs_gas(risk.jobs)';END IF;
 IF md5(p.prosrc)<>'252ac70b8c7624924f853e305795579e' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0
  OR p.proargnames IS DISTINCT FROM ARRAY['job'] OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_gas_consumers_upgrade: unexpected helper risk.needs_gas(risk.jobs)';END IF;
END$guard$;
CREATE OR REPLACE FUNCTION risk.needs_gas(job risk.jobs) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF NULLIF(job.request#>'{pricingPolicy,gasOwned}','null'::jsonb) IS NOT NULL
  OR (CASE WHEN jsonb_typeof(job.request#>'{economic,projection,gasOwned}')='array'
    THEN jsonb_array_length(job.request#>'{economic,projection,gasOwned}')>0 ELSE false END) THEN RETURN true;END IF;
 IF NULLIF(job.request->'economic','null'::jsonb) IS NOT NULL
  OR NULLIF(job.request#>'{pricingPolicy,futuresOptions,physical}','null'::jsonb) IS NOT NULL THEN
  RETURN EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)s JOIN settlement_data.futures_margin_policies p
   ON(p.catalog_id,p.id,p.revision)=(job.catalog_id,s.policy_id,s.policy_revision) WHERE p.delivery_profile_id IS NOT NULL);
 END IF;
 -- An ordinary execution/vanilla valuation reads no continuation profile.
 -- Preserve this cheap boundary in RLS and worker eligibility checks.
 IF job.request#>>'{pricingPolicy,futuresConvention}' IS DISTINCT FROM 'native_margin_remaining_value_v1' THEN RETURN false;END IF;
 IF NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL AND NOT EXISTS(
  SELECT 1 FROM trading.trade_revisions r WHERE r.catalog_id=job.catalog_id AND job.request->'tradeIds' @> jsonb_build_array(r.trade_id)) THEN RETURN false;END IF;
 RETURN EXISTS(SELECT 1 FROM risk.lifecycle_scope_policies(job)s JOIN settlement_data.futures_margin_policies p
  ON(p.catalog_id,p.id,p.revision)=(job.catalog_id,s.policy_id,s.policy_revision)
  JOIN settlement_data.futures_margin_adoptions a ON(a.catalog_id,a.policy_id,a.policy_revision)=(p.catalog_id,p.id,p.revision)
  JOIN risk.lifecycle_scope_trades(job)o ON(o.trade_id,o.binding_id,o.book_id,o.kind)=(a.trade_id,a.binding_id,a.book_id,'future')
  JOIN trading.trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(job.catalog_id,o.trade_id,o.revision)
  WHERE p.delivery_profile_id IS NOT NULL AND r.effective_at<=job.valuation_at
   AND(NULLIF(job.request->'tradeIds','null'::jsonb) IS NULL OR job.request->'tradeIds' @> jsonb_build_array(o.trade_id)));
END$function$;
COMMIT;
