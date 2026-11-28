-- Existing Facilities subviews and Curve definitions identify their own page.
-- Prompt admission only: no business reader, navigation target or new grant.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $pages$
DECLARE definition text;marker text:='''blotter_new_gas_storage''';
BEGIN
 SELECT pg_get_functiondef(p.oid) INTO STRICT definition FROM pg_proc p WHERE p.oid='assistant.is_prompt_page(text)'::regprocedure
  AND md5(p.prosrc)='25da9e88f3d0e2414a29f765e4876456' AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND NOT p.prosecdef AND p.provolatile='i' AND p.prokind='f' AND p.proparallel='u' AND NOT p.proisstrict AND NOT p.proleakproof AND NOT p.proretset
  AND p.provariadic=0 AND p.proargmodes IS NULL AND p.proargnames=ARRAY['page'] AND p.prorettype='boolean'::regtype
  AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='sql') AND p.proconfig=ARRAY['search_path=pg_catalog']
  AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
 IF(length(definition)-length(replace(definition,marker,'')))/length(marker)<>1
 THEN RAISE EXCEPTION 'assistant_page_identity_upgrade: unexpected helper assistant.is_prompt_page(text)';END IF;
 EXECUTE replace(definition,marker,marker||',''facility_outlook'',''facility_trades'',''curve_definitions''');
EXCEPTION WHEN no_data_found THEN RAISE EXCEPTION 'assistant_page_identity_upgrade: unexpected helper assistant.is_prompt_page(text)';
END$pages$;
COMMIT;
