-- A knowledge cutoff governs recorded closes and every price pin. Current
-- authority still governs access; no newer close or price bypasses that cutoff.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE OR REPLACE FUNCTION security.market_fact_visible(catalog uuid,revision uuid,observation uuid)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET plan_cache_mode=force_generic_plan AS $$
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RETURN false; END IF;
 IF NOT EXISTS(
  SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN RETURN true; END IF;
 RETURN EXISTS(
  SELECT 1 FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN LATERAL (
   SELECT event.* FROM market_data.knowledge_events event
   WHERE observation IS NULL AND event.catalog_id=t.catalog_id AND event.revision_id=revision AND event.observation_id IS NULL
   UNION ALL
   SELECT event.* FROM market_data.knowledge_events event
   WHERE observation IS NOT NULL AND event.catalog_id=t.catalog_id AND event.revision_id=revision AND event.observation_id=observation
  ) e ON true
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND t.catalog_id=catalog
   AND e.event_order<=m.known_through AND k.known_from<=t.known_at
   AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to
   AND(observation IS NULL OR EXISTS(SELECT 1 FROM market_data.observations o
    WHERE o.catalog_id=catalog AND o.id=observation AND o.observed_at<((t.effective_on+1)::timestamp AT TIME ZONE 'UTC')))
 );
END;
$$;

CREATE OR REPLACE FUNCTION security.market_close_visible(catalog uuid,close_id uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected market_data.market_closes;request security.request_contexts;
 temporal market_data.temporal_contexts;known integer;
BEGIN
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF request.id IS NULL OR request.expires_at<=clock_timestamp() THEN RETURN false;END IF;
 SELECT * INTO selected FROM market_data.market_closes s WHERE(s.catalog_id,s.id)=(market_close_visible.catalog,market_close_visible.close_id);
 IF NOT FOUND THEN RETURN false;END IF;
 IF security.market_fact_visible(catalog,selected.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) IS NOT TRUE THEN RETURN false;END IF;
 IF request.market_context_id IS NOT NULL THEN
  SELECT * INTO temporal FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(request.market_catalog_id,request.market_context_id);
  IF NOT FOUND OR temporal.catalog_id IS DISTINCT FROM catalog OR selected.business_date>temporal.effective_on THEN RETURN false;END IF;
  SELECT m.known_through INTO known FROM market_data.market_contexts m WHERE(m.catalog_id,m.context_id)=(temporal.catalog_id,temporal.id);
  IF NOT FOUND THEN RETURN false;END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.market_close_receipts r
   WHERE(r.catalog_id,r.publication_order,r.publication_xid)=(selected.catalog_id,selected.publication_order,selected.publication_xid)
   AND r.known_from<=temporal.known_at) THEN RETURN false;END IF;
  -- An absent or ineligible pin fails the entire close. Match the certified
  -- observation publication exactly, including its publication transaction.
  IF EXISTS(SELECT 1 FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id)=(market_close_visible.catalog,market_close_visible.close_id)
   AND NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e
    JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(e.catalog_id,e.revision_id)
    JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
    WHERE(e.catalog_id,e.revision_id,e.observation_id)=(p.catalog_id,p.definition_revision_id,p.observation_id)
    AND e.event_order<=known AND k.known_from<=temporal.known_at
    AND v.effective_from<=temporal.effective_on AND temporal.effective_on<v.effective_to
    AND o.observed_at<((temporal.effective_on+1)::timestamp AT TIME ZONE 'UTC')))
  THEN RETURN false;END IF;
 END IF;
 -- Current mode deliberately has no historical receipt requirement. Both modes
 -- retain fresh authority and the identical request/context through completion.
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
  AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 OR security.market_fact_visible(catalog,selected.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) IS NOT TRUE THEN RETURN false;END IF;
 RETURN true;
END;$$;
-- Both bounded and bulk observation reads enforce the same certified prefix.
-- Point candidates retain the bounded lateral driver qualified by 0223.
CREATE OR REPLACE FUNCTION security.observation_visibility_scope(requested_catalog uuid,requested_revision uuid,requested_pins uuid[])
 RETURNS TABLE(catalog uuid,revision uuid,observation uuid)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE cut record;
BEGIN
 IF requested_pins IS NULL AND(requested_catalog IS NOT NULL OR requested_revision IS NOT NULL)
 OR requested_pins IS NOT NULL AND(requested_catalog IS NULL OR requested_revision IS NULL)
 THEN RAISE EXCEPTION 'An observation read names all exact inputs or the bulk scope' USING ERRCODE='22023';END IF;
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN
  IF requested_pins IS NULL THEN
   RETURN QUERY SELECT o.catalog_id,o.definition_revision_id,o.id FROM market_data.observations o
    WHERE o.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'));
  ELSE
   RETURN QUERY SELECT o.catalog_id,o.definition_revision_id,o.id FROM(SELECT DISTINCT unnest(requested_pins) id)p
    CROSS JOIN LATERAL(SELECT candidate.* FROM market_data.observations candidate
     WHERE(candidate.catalog_id,candidate.definition_revision_id,candidate.id)=(requested_catalog,requested_revision,p.id) OFFSET 0)o
    WHERE o.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'));
  END IF;
  RETURN;
 END IF;
 SELECT * INTO cut FROM security.request_market_cut();
 IF cut.catalog IS NULL OR cut.catalog NOT IN(SELECT security.market_catalogs('ReadMarketData'))
 OR requested_catalog IS NOT NULL AND requested_catalog<>cut.catalog THEN RETURN;END IF;
 IF requested_pins IS NULL THEN
  RETURN QUERY SELECT DISTINCT e.catalog_id,e.revision_id,e.observation_id FROM market_data.knowledge_events e
   JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
   JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
   JOIN market_data.observations o ON(o.catalog_id,o.id)=(e.catalog_id,e.observation_id)
   WHERE e.catalog_id=cut.catalog AND e.observation_id IS NOT NULL
    AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at
    AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to
    AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC');
 ELSE
  RETURN QUERY WITH candidates AS MATERIALIZED(
   SELECT o.catalog_id,o.definition_revision_id,o.id,o.observed_at FROM(SELECT DISTINCT unnest(requested_pins) id)p
   CROSS JOIN LATERAL(SELECT candidate.* FROM market_data.observations candidate
    WHERE(candidate.catalog_id,candidate.definition_revision_id,candidate.id)=(requested_catalog,requested_revision,p.id) OFFSET 0)o)
  SELECT DISTINCT o.catalog_id,o.definition_revision_id,o.id FROM candidates o
   JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
   JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
   JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
   WHERE e.event_order<=cut.known_through AND k.known_from<=cut.known_at
    AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to
    AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC');
 END IF;
END;$$;
CREATE OR REPLACE FUNCTION security.close_in_force(catalog uuid,session text,business_date date) RETURNS uuid
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result uuid;request security.request_contexts;cutoff timestamptz;effective date;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO request FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
 AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 IF request.market_context_id IS NOT NULL THEN
  SELECT known_at,effective_on INTO cutoff,effective FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,request.market_context_id);
  IF NOT FOUND OR request.market_catalog_id IS DISTINCT FROM catalog THEN RAISE EXCEPTION 'close_context' USING ERRCODE='P0001';END IF;
  IF business_date>effective THEN RETURN NULL;END IF;
 END IF;
 -- Select the known head before checking access to its inputs. An inaccessible
 -- head must never revive an older close.
 SELECT s.id INTO result FROM market_data.market_closes s
 WHERE(s.catalog_id,s.session_code,s.business_date)=(catalog,session,close_in_force.business_date)
 AND(cutoff IS NULL OR EXISTS(SELECT 1 FROM market_data.market_close_receipts r
  WHERE(r.catalog_id,r.publication_order,r.publication_xid)=(s.catalog_id,s.publication_order,s.publication_xid) AND r.known_from<=cutoff))
 ORDER BY s.publication_order DESC LIMIT 1;
 RETURN result;
END;$$;
CREATE OR REPLACE FUNCTION security.close_session_hidden_days(catalog uuid,session text) RETURNS date[]
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN coalesce((SELECT array_agg(d.business_date ORDER BY d.business_date) FROM(
  SELECT DISTINCT s.business_date FROM market_data.market_closes s WHERE(s.catalog_id,s.session_code)=(catalog,session))d
  CROSS JOIN LATERAL(SELECT security.close_in_force(catalog,session,d.business_date) id OFFSET 0)head
  WHERE head.id IS NOT NULL AND NOT security.market_close_visible(catalog,head.id)),ARRAY[]::date[]);
END;$$;
DROP FUNCTION security.close_recorded_after_cutoff(uuid,uuid);
COMMIT;
