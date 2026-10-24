-- Qualify exact immutable close pins as one temporal set. Current authority is
-- rechecked at both boundaries; no permission or context decision is cached.
BEGIN;
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
  IF NOT FOUND OR temporal.catalog_id IS DISTINCT FROM catalog THEN RETURN false;END IF;
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
COMMIT;
