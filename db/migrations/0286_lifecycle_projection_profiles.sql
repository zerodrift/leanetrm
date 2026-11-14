-- Read-only native configuration source for actual managed economic workers.
-- Existing context and receipt table permissions are unchanged.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION security.read_lifecycle_projection_profiles(catalog uuid,kind text,trade uuid,origin_revision integer,
 context uuid,as_of timestamptz,business_date date,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;prefix integer;cut timestamptz;rows jsonb;item jsonb;BEGIN
 IF kind IS NULL OR kind NOT IN('listed_option','financial_swap') OR origin_revision IS NULL OR origin_revision<1 THEN RAISE EXCEPTION 'projection_profile_source';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF(catalog,context,as_of,business_date) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at,(job.frame->>'businessDate')::date)
   OR NOT EXISTS(SELECT 1 FROM risk.lifecycle_scope_trades(job)s WHERE(s.trade_id,s.revision,s.kind)=
    (trade,origin_revision,CASE read_lifecycle_projection_profiles.kind WHEN 'listed_option' THEN 'option' ELSE 'swap' END))
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,until_at);
 SELECT x.known_through,t.known_at INTO STRICT prefix,cut FROM trading.contexts x JOIN market_data.temporal_contexts t
  ON(t.catalog_id,t.id)=(x.catalog_id,x.context_id) WHERE(x.catalog_id,x.context_id,t.effective_on)=(catalog,context,business_date);
 IF kind='listed_option' THEN
  SELECT coalesce(jsonb_agg(jsonb_build_object('profile',to_jsonb(s),'policy',to_jsonb(p),'occurrences',
   (SELECT jsonb_agg(to_jsonb(o) ORDER BY o.at,o.phase) FROM settlement_data.futures_margin_policy_occurrences o
     WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(p.catalog_id,p.id,p.revision)))),'[]'::jsonb) INTO rows
  FROM(SELECT s.*,k.known_from AS profile_known_at FROM lifecycle.option_profiles s JOIN trading.knowledge_receipts k
    ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
    WHERE(s.catalog_id,s.trade_id,s.action)=(catalog,trade,'option.expiry') AND s.publication_order<=prefix AND k.known_from<=cut
    ORDER BY s.revision DESC LIMIT 1)s
  JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(s.catalog_id,s.margin_policy_id,s.margin_policy_revision)
  WHERE s.option_revision=read_lifecycle_projection_profiles.origin_revision;
  FOR item IN SELECT value FROM jsonb_array_elements(rows) LOOP
   PERFORM security.require_lifecycle_projection_profile(catalog,kind,trade,NULL,(item#>>'{profile,revision}')::integer,context,as_of,business_date,until_at);
  END LOOP;
 ELSE
  SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY s.membership_ordinal,s.payment_ordinal),'[]'::jsonb) INTO rows
  FROM(SELECT DISTINCT ON(s.id)s.*,k.known_from FROM lifecycle.swap_profiles s JOIN trading.knowledge_receipts k
    ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
    WHERE(s.catalog_id,s.trade_id,s.action)=(catalog,trade,'swap.determine') AND s.publication_order<=prefix AND k.known_from<=cut
    ORDER BY s.id,s.revision DESC)s WHERE s.origin_revision=read_lifecycle_projection_profiles.origin_revision;
  FOR item IN SELECT value FROM jsonb_array_elements(rows) LOOP
   PERFORM security.require_lifecycle_projection_profile(catalog,kind,trade,(item->>'id')::uuid,(item->>'revision')::integer,context,as_of,business_date,until_at);
  END LOOP;
 END IF;
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,until_at);
 RETURN rows;
END$function$;
ALTER FUNCTION security.read_lifecycle_projection_profiles(uuid,text,uuid,integer,uuid,timestamptz,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_lifecycle_projection_profiles(uuid,text,uuid,integer,uuid,timestamptz,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_lifecycle_projection_profiles(uuid,text,uuid,integer,uuid,timestamptz,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
