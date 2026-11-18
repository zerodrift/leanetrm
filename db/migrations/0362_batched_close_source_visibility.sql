-- Current consumer: the existing bounded native close-parts reader. Share
-- exact-source admission within its statement; retain its payload and callers.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';

CREATE FUNCTION security.market_close_batch_visible(catalog uuid,close_ids uuid[]) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE request security.request_contexts;cut record;source record;until_at timestamptz;
BEGIN
 -- A changed scalar contract remains authoritative. This fallback executes
 -- under the same definer authority as the original close-parts predicate.
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('security.market_close_visible(uuid,uuid)')
  AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')='9b6cc5c36494a42edef9d616fc4709544f1f427ba281eb547f854443cde1115d'
  AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef AND NOT p.proisstrict AND NOT p.proleakproof
  AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='plpgsql')
  AND p.provolatile='v' AND p.proparallel='u' AND p.prokind='f' AND NOT p.proretset AND p.prorettype='boolean'::regtype
  AND p.prosupport=0 AND p.proconfig=ARRAY['search_path=pg_catalog']
  AND to_jsonb(p.proacl)='["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner"]'::jsonb)
 THEN RETURN NOT EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) WHERE NOT security.market_close_visible(catalog,requested.id));END IF;
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 until_at=security.authority_until('ReadMarketData');
 IF request.id IS NULL OR until_at IS NULL OR clock_timestamp()>=until_at OR request.expires_at<=clock_timestamp()
  OR catalog IS NULL OR close_ids IS NULL OR cardinality(close_ids) NOT BETWEEN 1 AND 128 OR array_position(close_ids,NULL) IS NOT NULL
 THEN RETURN false;END IF;
 IF(SELECT count(*) FROM market_data.market_closes s WHERE s.catalog_id=$1 AND s.id=ANY($2))<>cardinality(close_ids)
  OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) WHERE NOT EXISTS(
   SELECT 1 FROM market_data.market_close_sources x WHERE x.catalog_id=$1 AND x.close_id=requested.id))
 THEN RETURN false;END IF;
 FOR source IN SELECT DISTINCT x.definition_revision_id,x.reference_catalog_id,x.reference_revision_id
  FROM market_data.market_close_sources x WHERE x.catalog_id=$1 AND x.close_id=ANY($2) LOOP
  IF security.market_fact_visible(catalog,source.definition_revision_id,NULL) IS NOT TRUE
   OR security.reference_version_visible(source.reference_catalog_id,source.reference_revision_id) IS NOT TRUE
  THEN RETURN false;END IF;
 END LOOP;
 IF request.market_context_id IS NOT NULL THEN
  SELECT t.catalog_id,t.effective_on,t.known_at,m.known_through INTO cut FROM market_data.temporal_contexts t
   JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
   WHERE(t.catalog_id,t.id)=(request.market_catalog_id,request.market_context_id);
  IF NOT FOUND OR cut.catalog_id IS DISTINCT FROM catalog THEN RETURN false;END IF;
  IF EXISTS(SELECT 1 FROM market_data.market_closes s WHERE s.catalog_id=$1 AND s.id=ANY($2)
   AND(s.business_date>cut.effective_on OR NOT EXISTS(SELECT 1 FROM market_data.market_close_receipts r
    WHERE(r.catalog_id,r.publication_order,r.publication_xid)=(s.catalog_id,s.publication_order,s.publication_xid)
    AND r.known_from<=cut.known_at)))
  THEN RETURN false;END IF;
  -- Preserve every pin, including supplemental sources and unrelated retained
  -- pins. Current mode deliberately skips this historical certification join.
  IF EXISTS(SELECT 1 FROM market_data.market_close_pins p WHERE p.catalog_id=$1 AND p.close_id=ANY($2)
   AND NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e
    JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(e.catalog_id,e.revision_id)
    JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
    WHERE(e.catalog_id,e.revision_id,e.observation_id)=(p.catalog_id,p.definition_revision_id,p.observation_id)
    AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at
    AND v.effective_from<=cut.effective_on AND cut.effective_on<v.effective_to
    AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC')))
  THEN RETURN false;END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
  AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 THEN RETURN false;END IF;
 FOR source IN SELECT DISTINCT x.definition_revision_id,x.reference_catalog_id,x.reference_revision_id
  FROM market_data.market_close_sources x WHERE x.catalog_id=$1 AND x.close_id=ANY($2) LOOP
  IF security.market_fact_visible(catalog,source.definition_revision_id,NULL) IS NOT TRUE
   OR security.reference_version_visible(source.reference_catalog_id,source.reference_revision_id) IS NOT TRUE
  THEN RETURN false;END IF;
 END LOOP;
 RETURN clock_timestamp()<until_at AND security.market_access(catalog,'ReadMarketData') IS TRUE;
END;$$;
ALTER FUNCTION security.market_close_batch_visible(uuid,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.market_close_batch_visible(uuid,uuid[]) FROM PUBLIC;

-- Replace only the two existing admission predicates. This upgrade refuses a
-- reader that no longer has the reviewed body; no payload implementation is
-- copied or silently substituted over an independently changed reader.
DO $upgrade$
DECLARE original text;changed text;before_guard text;after_guard text;
BEGIN
 SELECT pg_get_functiondef(p.oid) INTO original FROM pg_proc p
  WHERE p.oid='security.read_market_close_parts(uuid,uuid[],uuid)'::regprocedure
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='plpgsql')
   AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')='2ec4fe1e9a167de6ca1a6b6cdc90d4c6cea315cb5db7f19ca3e7b9d381e0a2dd'
   AND NOT p.proisstrict AND NOT p.proleakproof AND p.provolatile='v' AND p.proparallel='u'
   AND p.prokind='f' AND NOT p.proretset AND p.prorettype='jsonb'::regtype AND p.prosupport=0
   AND p.proconfig=ARRAY['search_path=pg_catalog']
   AND to_jsonb(p.proacl)='["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner"]'::jsonb;
 IF original IS NULL THEN RAISE EXCEPTION 'close_batch_upgrade: unsupported close reader';END IF;
 before_guard=$old$OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) LEFT JOIN market_data.market_closes s ON(s.catalog_id,s.id)=(catalog,requested.id)
 WHERE s.id IS NULL OR NOT security.market_close_visible(catalog,s.id))$old$;
 after_guard=$old$OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) WHERE NOT security.market_close_visible(catalog,requested.id))$old$;
 IF length(original)-length(replace(original,before_guard,''))<>length(before_guard)
  OR length(original)-length(replace(original,after_guard,''))<>length(after_guard)
 THEN RAISE EXCEPTION 'close_batch_upgrade: unsupported admission predicates';END IF;
 changed=replace(replace(original,before_guard,$new$OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) LEFT JOIN market_data.market_closes s ON(s.catalog_id,s.id)=(catalog,requested.id)
 WHERE s.id IS NULL) OR security.market_close_batch_visible(catalog,close_ids) IS NOT TRUE$new$),
  after_guard,'OR security.market_close_batch_visible(catalog,close_ids) IS NOT TRUE');
 IF changed=original OR strpos(changed,before_guard)>0 OR strpos(changed,after_guard)>0
  OR length(changed)-length(replace(changed,'security.market_close_batch_visible(catalog,close_ids)',''))<>2*length('security.market_close_batch_visible(catalog,close_ids)')
 THEN RAISE EXCEPTION 'close_batch_upgrade: unsupported admission predicates';END IF;
 EXECUTE changed;
END $upgrade$;
COMMIT;
