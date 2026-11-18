-- Current consumer: the existing hidden-day projection. Prove the common
-- all-visible result once per source; retain the original query otherwise.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$
DECLARE helper pg_proc;body text;changed text;permission text;finish text;
BEGIN
 SELECT * INTO helper FROM pg_proc WHERE oid='security.market_close_batch_visible(uuid,uuid[])'::regprocedure;
 IF encode(sha256(convert_to(helper.prosrc,'UTF8')),'hex')<>'9fc522aa374d8d8db657e718d74c0569bc9de2fcbf70ba21b6e0380664a53057'
  OR pg_get_userbyid(helper.proowner)<>'openetrm_security_owner' OR NOT helper.prosecdef OR helper.proisstrict OR helper.proleakproof
  OR helper.prolang<>(SELECT oid FROM pg_language WHERE lanname='plpgsql') OR helper.provolatile<>'v' OR helper.proparallel<>'u'
  OR helper.prokind<>'f' OR helper.proretset OR helper.prorettype<>'boolean'::regtype OR helper.prosupport<>0
  OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR(SELECT jsonb_agg(a::text ORDER BY a::text) FROM unnest(helper.proacl)a)
   IS DISTINCT FROM '["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb
 THEN RAISE EXCEPTION 'hidden_day_upgrade: unsupported batch admission';END IF;
 -- Hidden days have always been unbounded by the 128-frame discovery limit.
 -- Only this owner-only helper grows to that existing consumer's scope. The
 -- public close-parts reader retains its original 1..128 argument guard.
 changed=replace(helper.prosrc,'cardinality(close_ids) NOT BETWEEN 1 AND 128','cardinality(close_ids)=0');
 IF changed=helper.prosrc THEN RAISE EXCEPTION 'hidden_day_upgrade: unsupported private bound';END IF;
 EXECUTE 'CREATE OR REPLACE FUNCTION security.market_close_batch_visible(catalog uuid,close_ids uuid[]) RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $body$'||changed||'$body$';

 SELECT * INTO helper FROM pg_proc WHERE oid='security.close_session_hidden_days(uuid,text)'::regprocedure;
 IF encode(sha256(convert_to(helper.prosrc,'UTF8')),'hex')<>'ce49d01fa15b150ce585a337893a70f27f05c3800b11ce12487cd2710f751062'
  OR pg_get_userbyid(helper.proowner)<>'openetrm_security_owner' OR NOT helper.prosecdef OR helper.proisstrict OR helper.proleakproof
  OR helper.prolang<>(SELECT oid FROM pg_language WHERE lanname='plpgsql') OR helper.provolatile<>'s' OR helper.proparallel<>'u'
  OR helper.prokind<>'f' OR helper.proretset OR helper.prorettype<>'date[]'::regtype OR helper.prosupport<>0
  OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR(SELECT jsonb_agg(a::text ORDER BY a::text) FROM unnest(helper.proacl)a)
   IS DISTINCT FROM '["openetrm_runtime=X/openetrm_security_owner","openetrm_security_owner=X/openetrm_security_owner"]'::jsonb
 THEN RAISE EXCEPTION 'hidden_day_upgrade: unsupported hidden-day reader';END IF;
 permission=$old$IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;$old$;
 finish=$check$IF clock_timestamp()>=until_at OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
   AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
   AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;$check$;
 body=replace(helper.prosrc,E'\nBEGIN\n',E'\nDECLARE request security.request_contexts;until_at timestamptz;heads uuid[];result date[];\nBEGIN\n');
 body=replace(body,permission,permission||$fast$
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 until_at=security.authority_until('ReadMarketData');
 IF request.id IS NULL OR until_at IS NULL OR clock_timestamp()>=until_at
 THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 -- Choose known heads before checking their source authority. Inaccessible
 -- heads never revive prior closes; unknown heads contribute no hidden date.
 heads=ARRAY(SELECT head.id FROM(
  SELECT DISTINCT s.business_date FROM market_data.market_closes s WHERE(s.catalog_id,s.session_code)=(catalog,session))d
  CROSS JOIN LATERAL(SELECT security.close_in_force(catalog,session,d.business_date) id OFFSET 0)head
  WHERE head.id IS NOT NULL);
 IF cardinality(heads)=0 OR security.market_close_batch_visible(catalog,heads) THEN
  result=ARRAY[]::date[];
 $fast$||finish||E'\n RETURN result;\n END IF;');
 body=replace(body,'RETURN coalesce(','result=coalesce(');
 body=replace(body,E'\nEND;',E'\n'||finish||E'\nRETURN result;\nEND;');
 IF body=helper.prosrc OR strpos(body,'RETURN coalesce(')>0 OR strpos(body,'result=coalesce(')=0
 THEN RAISE EXCEPTION 'hidden_day_upgrade: unsupported projection';END IF;
 -- Keep STABLE and the existing select snapshot. A false or unsupported batch
 -- admission executes the unchanged per-day projection before the final check.
 EXECUTE 'CREATE OR REPLACE FUNCTION security.close_session_hidden_days(catalog uuid,session text) RETURNS date[] LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $body$'||body||'$body$';
END $upgrade$;
COMMIT;
