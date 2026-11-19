-- Current consumer: the heat-rate call option ticket in New trade reports its own page identity.
-- A page identity admits prompts only; its report capability stays explicitly unsupported.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $page$
DECLARE helper pg_proc;definition text;before text:='''blotter_rec_forward'');';
BEGIN
 SELECT * INTO STRICT helper FROM pg_proc WHERE oid='assistant.is_prompt_page(text)'::regprocedure;
 IF md5(helper.prosrc)<>'d103445eddfb20c7aa2143ba8e4a4121'
  OR helper.proowner<>'openetrm_security_owner'::regrole OR helper.prosecdef OR helper.provolatile<>'i'
  OR helper.proisstrict OR helper.proleakproof OR helper.proparallel<>'u'
  OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR(SELECT array_agg(a::text ORDER BY a::text) FROM unnest(coalesce(helper.proacl,acldefault('f',helper.proowner)))a)
    IS DISTINCT FROM ARRAY['openetrm_security_owner=X/openetrm_security_owner']
 THEN RAISE EXCEPTION 'heat_rate_call_page_predecessor';END IF;
 definition=pg_get_functiondef(helper.oid);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'heat_rate_call_page_predecessor';END IF;
 EXECUTE replace(definition,before,'''blotter_rec_forward'',''blotter_new_heat_rate_call'');');
END$page$;
COMMIT;
