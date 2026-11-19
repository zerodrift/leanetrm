-- Current consumer: the gas storage contract ticket in New trade reports its own page identity.
-- A page identity admits prompts only; its report capability stays explicitly unsupported.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $page$
DECLARE helper pg_proc;definition text;before text:='''blotter_new_heat_rate_call'');';
BEGIN
 SELECT * INTO STRICT helper FROM pg_proc WHERE oid='assistant.is_prompt_page(text)'::regprocedure;
 IF md5(helper.prosrc)<>'c7d7060431884b32b4bd72557e77379b'
  OR helper.proowner<>'openetrm_security_owner'::regrole OR helper.prosecdef OR helper.provolatile<>'i'
  OR helper.proisstrict OR helper.proleakproof OR helper.proparallel<>'u'
  OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR(SELECT array_agg(a::text ORDER BY a::text) FROM unnest(coalesce(helper.proacl,acldefault('f',helper.proowner)))a)
    IS DISTINCT FROM ARRAY['openetrm_security_owner=X/openetrm_security_owner']
 THEN RAISE EXCEPTION 'gas_storage_page_predecessor';END IF;
 definition=pg_get_functiondef(helper.oid);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'gas_storage_page_predecessor';END IF;
 EXECUTE replace(definition,before,'''blotter_new_heat_rate_call'',''blotter_new_gas_storage'');');
END$page$;
COMMIT;
