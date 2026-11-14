-- Current consumer: ordinary full native future PV reads an adopted physical
-- continuation profile. The selected v2 measure requires the same conditional
-- Gas authority as the established native state reader; cash futures do not.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;definition text;changed text;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('risk.needs_gas(risk.jobs)');
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_full_futures_gas_upgrade: missing helper risk.needs_gas(risk.jobs)';END IF;
 IF md5(p.prosrc)<>'f8255bf25d55722a076a0766f80405f9' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'boolean'::regtype OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0
  OR p.proargnames IS DISTINCT FROM ARRAY['job'] OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'risk_full_futures_gas_upgrade: unexpected helper risk.needs_gas(risk.jobs)';END IF;
 definition=pg_get_functiondef(p.oid);
 changed=replace(definition,$old$IF job.request#>>'{pricingPolicy,futuresConvention}' IS DISTINCT FROM 'native_margin_remaining_value_v1' THEN RETURN false;END IF;$old$,$new$IF coalesce(job.request#>>'{pricingPolicy,futuresConvention}','') NOT IN('native_margin_remaining_value_v1','native_futures_economic_remaining_value_v2') THEN RETURN false;END IF;$new$);
 IF changed=definition THEN RAISE EXCEPTION 'risk_full_futures_gas_upgrade: missing exact consumer boundary';END IF;
 EXECUTE changed;
END$guard$;
COMMIT;
