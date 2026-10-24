-- Preserve current authority and certified temporal visibility while using the
-- existing exact observation and bundle-publication indexes.
-- An explicit current-authority check lets unbound reads return before planning
-- the historical branch. Bound reads retain the complete certified lookup.
-- Reuse generic execution plans locally; current authorization and expiry are
-- still checked on every call, including when a caller requests custom plans.
BEGIN;
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

-- A reference version is justified by a visible definition-bundle publication.
-- Specialize that NULL-observation case without re-entering the general helper.
CREATE OR REPLACE FUNCTION security.reference_version_visible(catalog uuid,revision uuid)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET plan_cache_mode=force_generic_plan AS $$
BEGIN
 IF security.reference_access(catalog,'ReadMarketData') IS NOT TRUE THEN RETURN false; END IF;
 IF NOT EXISTS(
  SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN RETURN true; END IF;
 RETURN EXISTS(
  SELECT 1 FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN market_data.revisions r ON r.reference_catalog_id=catalog AND r.reference_revision_id=revision AND r.catalog_id=t.catalog_id
  JOIN market_data.knowledge_events e ON e.catalog_id=r.catalog_id AND e.revision_id=r.id AND e.observation_id IS NULL
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
   AND security.market_access(r.catalog_id,'ReadMarketData')
   AND e.event_order<=m.known_through AND k.known_from<=t.known_at
   AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to
 );
END;
$$;
COMMIT;
