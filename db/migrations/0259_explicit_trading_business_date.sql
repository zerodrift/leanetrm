-- Current consumers: lifecycle actions and retained portfolio frames whose
-- selected market business day differs from the UTC date of their instant.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE security.request_contexts ADD COLUMN trading_business_date date;
ALTER TABLE security.request_contexts
 ADD COLUMN business_source_catalog_id uuid,ADD COLUMN business_source_context_id uuid,
 ADD COLUMN business_source_as_of ref_data.instant,ADD COLUMN business_source_on date,
 ADD COLUMN business_source_until ref_data.instant,
 ADD CONSTRAINT request_business_source_complete CHECK(
  num_nonnulls(business_source_catalog_id,business_source_context_id,business_source_as_of,business_source_on,business_source_until) IN(0,5)),
 ADD CONSTRAINT request_business_source_market FOREIGN KEY(business_source_catalog_id,business_source_context_id)
  REFERENCES market_data.temporal_contexts(catalog_id,id),
 ADD CONSTRAINT request_business_source_trades FOREIGN KEY(business_source_catalog_id,business_source_context_id)
  REFERENCES trading.contexts(catalog_id,context_id);

CREATE FUNCTION security.use_trading_context(catalog uuid,as_of timestamptz,business_date date) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE context uuid;BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF as_of IS NULL OR NOT isfinite(as_of) OR business_date IS NULL OR NOT isfinite(business_date)
 THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 SELECT t.id INTO context FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.expires_at>clock_timestamp() AND t.catalog_id=catalog AND t.effective_on=business_date AND c.trading_as_of IS NULL;
 IF NOT FOUND THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);
 UPDATE security.request_contexts SET trading_as_of=as_of,trading_business_date=business_date
 WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
END$function$;

-- The two-argument admission retains its UTC-day contract. An extended source
-- check accepts another day only under an exact explicitly admitted live scope.
CREATE FUNCTION security.trading_context_day_matches(catalog uuid,context uuid,as_of timestamptz) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT security.market_access(catalog,'ReadMarketData') IS TRUE AND as_of IS NOT NULL AND isfinite(as_of) AND EXISTS(
 SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context) AND(
  t.effective_on=(as_of AT TIME ZONE 'UTC')::date OR EXISTS(
   SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
   AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
   AND((q.market_catalog_id,q.market_context_id,q.trading_as_of,q.trading_business_date)=(catalog,context,as_of,t.effective_on)
    OR(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on)=(catalog,context,as_of,t.effective_on)
     AND q.business_source_until>clock_timestamp()))));
$function$;

CREATE FUNCTION security.require_admitted_business_source(catalog uuid,context uuid,as_of timestamptz,business_date date,until_at timestamptz)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t
  ON(t.catalog_id,t.id)=(q.business_source_catalog_id,q.business_source_context_id)
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
  AND(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on,q.business_source_until)=
   (catalog,context,as_of,business_date,until_at) AND q.expires_at>clock_timestamp() AND t.effective_on=business_date)
 THEN RAISE EXCEPTION 'trading_source_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_trading_history(catalog,context);
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
END$function$;

-- A command may retain one exact source independently of its restored write
-- scope. A second source is a separate operation; an identical admission never
-- extends the original deadline, including across successor grants.
CREATE FUNCTION security.admit_trading_business_source(catalog uuid,context uuid,as_of timestamptz,business_date date)
 RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz;q security.request_contexts;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT * INTO STRICT q FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
  AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF as_of IS NULL OR NOT isfinite(as_of) OR business_date IS NULL OR NOT isfinite(business_date) OR NOT EXISTS(
  SELECT 1 FROM market_data.temporal_contexts t JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,business_date))
 THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);
 IF q.business_source_catalog_id IS NOT NULL THEN
  IF(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on) IS DISTINCT FROM(catalog,context,as_of,business_date)
  THEN RAISE EXCEPTION 'trading_source_conflict';END IF;
  until_at=q.business_source_until;
 ELSE
  UPDATE security.request_contexts SET business_source_catalog_id=catalog,business_source_context_id=context,
   business_source_as_of=as_of,business_source_on=business_date,business_source_until=until_at WHERE id=q.id;
 END IF;
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,until_at);RETURN until_at;
END$function$;
ALTER FUNCTION security.use_trading_context(uuid,timestamptz,date) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.trading_context_day_matches(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.admit_trading_business_source(uuid,uuid,timestamptz,date) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_admitted_business_source(uuid,uuid,timestamptz,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.use_trading_context(uuid,timestamptz,date),security.trading_context_day_matches(uuid,uuid,timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.admit_trading_business_source(uuid,uuid,timestamptz,date),security.require_admitted_business_source(uuid,uuid,timestamptz,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.use_trading_context(uuid,timestamptz,date),security.trading_context_day_matches(uuid,uuid,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.admit_trading_business_source(uuid,uuid,timestamptz,date),security.require_admitted_business_source(uuid,uuid,timestamptz,date,timestamptz) TO openetrm_runtime;

DO $patch$DECLARE item record;f pg_proc;definition text;
 needle constant text='(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE ''UTC'')::date)';
 replacement constant text='(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of)';
BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('require_futures_margin_source','require_futures_margin_source(uuid,uuid,timestamptz,uuid)','void','b1100f5b4e4cf81047347b27f27fc6f7',false),
  ('determine_swap','determine_swap(uuid,uuid,uuid,integer,integer,uuid,timestamptz,integer,date,date,timestamptz,uuid,uuid,text,integer,timestamptz,text,text,text,numeric,numeric,numeric,jsonb,jsonb,timestamptz)','jsonb','66d1e417d838b13b6af5546de9b0fc04',true),
  ('apply_swap_cash','apply_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb,text)','jsonb','7900b7d94d0e629f9e616b1eb1634285',false))v(name,signature,result_type,source_hash,runtime_execute)
 LOOP
  SELECT p.* INTO f FROM pg_proc p WHERE p.oid=to_regprocedure('settlement_data.'||item.signature);
  IF NOT FOUND THEN RAISE EXCEPTION 'trading_business_date_upgrade: unexpected helper settlement_data.%',item.signature;END IF;
  IF md5(f.prosrc)<>item.source_hash OR NOT f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
   OR f.prorettype<>item.result_type::regtype OR f.prokind<>'f' OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql'
   OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
   OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS DISTINCT FROM item.runtime_execute
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a
    WHERE a.grantor<>f.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
     OR a.grantee<>f.proowner AND(NOT item.runtime_execute OR a.grantee<>'openetrm_runtime'::regrole))
   OR (length(f.prosrc)-length(replace(f.prosrc,needle,'')))/length(needle)<>1
  THEN RAISE EXCEPTION 'trading_business_date_upgrade: unexpected helper settlement_data.%',item.signature;END IF;
  definition=pg_get_functiondef(f.oid);EXECUTE replace(definition,needle,replacement);
 END LOOP;
END$patch$;
COMMIT;
