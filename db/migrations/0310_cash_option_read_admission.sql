-- Current consumer: the explicitly selected cash-option reference strategy
-- reads its exact retained native option history without physical Gas authority.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;definition text;changed text;old_boundary text;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'cash_option_read_upgrade: missing native history guard';END IF;
 IF md5(p.prosrc)<>'34f3cb153ed4c648f1a96eb238598ee6' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'void'::regtype OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0
  OR p.proargnames IS DISTINCT FROM ARRAY['catalog','trade','binding','revision','context','as_of','business_date','source_until','history_until']
  OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u' OR p.procost<>100 OR p.prorows<>0
  OR p.probin IS NOT NULL OR p.prosqlbody IS NOT NULL OR p.prosupport<>0 OR p.protrftypes IS NOT NULL
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'cash_option_read_upgrade: unexpected helper lifecycle.require_option_history_read(uuid,uuid,uuid,integer,uuid,timestamptz,date,timestamptz,timestamptz)';END IF;
 old_boundary=$old$OR NULLIF(job.request->'economic','null'::jsonb) IS NULL AND NULLIF(job.request#>'{pricingPolicy,futuresOptions,physical}','null'::jsonb) IS NULL$old$;
 definition=pg_get_functiondef(p.oid);
 IF(length(definition)-length(replace(definition,old_boundary,'')))/length(old_boundary)<>1
 THEN RAISE EXCEPTION 'cash_option_read_upgrade: missing exact consumer boundary';END IF;
 changed=replace(definition,old_boundary,$new$OR NULLIF(job.request->'economic','null'::jsonb) IS NULL
    AND NULLIF(job.request#>'{pricingPolicy,futuresOptions,physical}','null'::jsonb) IS NULL
    AND NOT coalesce(job.request#>>'{pricingPolicy,futuresOptions,cashFunding,implementation}'='cash_futures_option_reference_value_v1'
      AND EXISTS(SELECT 1 FROM jsonb_array_elements(CASE
       WHEN jsonb_typeof(job.request#>'{pricingPolicy,futuresOptions,cashFunding,options}')='array'
       THEN job.request#>'{pricingPolicy,futuresOptions,cashFunding,options}' ELSE '[]'::jsonb END) selected
       WHERE selected->>'tradeId'=trade::text AND selected->>'bindingId'=binding::text
        AND selected#>>'{model,implementation}'='cash_futures_option_reference_strategy_v1'
        AND selected#>>'{model,funding}'='flat_reference_to_next_saved_margin_due_v1'),false)$new$);
 EXECUTE changed;
END$guard$;
COMMIT;
