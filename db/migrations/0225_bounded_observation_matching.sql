-- Current consumer: selected correction history under a recorded Blotter close.
-- Drive close, knowledge-event and receipt matching by admitted candidates; a
-- generic plan must not expand them across unrelated pins or knowledge history.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$
DECLARE item record;body text;needle text;replacement text;
BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.observation_visibility_scope(uuid,uuid,uuid[])','d85457a07db98f78287b420dd96be6ec92e9f5a3c54e93526ab989b24372fbc2',1),
  ('security.read_observation_facts(uuid,uuid,uuid[])','49c91d39620a7d838240d2138ef479ccbc63612329ee5a02b0c22f1a92cac7e7',3)
 )v(signature,hash,grants) LOOP
  SELECT pg_get_functiondef(p.oid) INTO body FROM pg_proc p WHERE p.oid=item.signature::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.hash
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND p.proconfig=ARRAY['search_path=pg_catalog'] AND p.provolatile='v'
   AND p.proacl=CASE WHEN item.grants=3 THEN
    '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'::aclitem[]
    ELSE '{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[] END;
  IF body IS NULL THEN RAISE EXCEPTION 'close_pin_upgrade: unexpected helper %',item.signature;END IF;
  IF item.grants=1 THEN
   needle=$before$FROM readable_close s
  JOIN market_data.market_close_pins p ON(p.catalog_id,p.close_id)=(s.catalog_id,s.id)
  JOIN candidates o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
  WHERE o.observed_at<=s.at$before$;
   replacement=$after$FROM readable_close s JOIN candidates o ON o.catalog_id=s.catalog_id
  CROSS JOIN LATERAL(
   SELECT p.observation_id FROM market_data.market_close_pins p
   WHERE(p.catalog_id,p.close_id,p.observation_id)=(s.catalog_id,s.id,o.id)
   OFFSET 0
  )p
  WHERE o.observed_at<=s.at$after$;
   IF length(body)-length(replace(body,needle,''))<>length(needle)
   THEN RAISE EXCEPTION 'close_pin_upgrade: unexpected close join';END IF;
   body=replace(body,needle,replacement);
   needle=$before$FROM candidates o
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)$before$;
   replacement=$after$FROM candidates o
  CROSS JOIN LATERAL(
   SELECT e.* FROM market_data.knowledge_events e
   WHERE(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
    AND e.event_order<=cut.known_through OFFSET 0
  )e
  CROSS JOIN LATERAL(
   SELECT k.* FROM market_data.market_knowledge_receipts k
   WHERE(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    AND k.known_from<=cut.known_at OFFSET 0
  )k$after$;
   IF length(body)-length(replace(body,needle,''))<>length(needle)
   THEN RAISE EXCEPTION 'close_pin_upgrade: unexpected knowledge join';END IF;
   body=replace(body,needle,replacement);
  ELSE
   -- One deduplicated requested-id set also gates visible anchors. Large arrays
   -- must not be searched linearly for every authorized correction row.
   needle='WITH anchors AS MATERIALIZED(';
   replacement='WITH requested AS MATERIALIZED(SELECT DISTINCT unnest(requested_pins) id), anchors AS MATERIALIZED(';
   IF length(body)-length(replace(body,needle,''))<>length(needle)
   THEN RAISE EXCEPTION 'close_pin_upgrade: unexpected anchor declaration';END IF;
   body=replace(body,needle,replacement);
   needle='FROM(SELECT DISTINCT unnest(requested_pins) id)p';
   IF length(body)-length(replace(body,needle,''))<>length(needle)
   THEN RAISE EXCEPTION 'close_pin_upgrade: unexpected requested ids';END IF;
   body=replace(body,needle,'FROM requested p');
   needle='FROM authorized o WHERE o.id=ANY(requested_pins)';
   IF length(body)-length(replace(body,needle,''))<>length(needle)
   THEN RAISE EXCEPTION 'close_pin_upgrade: unexpected anchor membership';END IF;
   body=replace(body,needle,'FROM authorized o JOIN requested p ON p.id=o.id');
  END IF;
  EXECUTE body;
  -- Exact lookups gain nothing from JIT; loading LLVM on a cold host can consume the read's deadline.
  EXECUTE format('ALTER FUNCTION %s SET jit=off',item.signature);
 END LOOP;
END $upgrade$;
COMMIT;
