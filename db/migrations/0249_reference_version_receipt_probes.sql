-- Resolve each candidate market revision's bundle publication, and then that
-- publication's certified receipt, by key. A generic plan must not enumerate
-- the catalog's unrelated publications or receipts to decide whether a
-- reference version is visible.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$
DECLARE definition text;
 before_text text=$before$  JOIN market_data.knowledge_events e ON e.catalog_id=r.catalog_id AND e.revision_id=r.id AND e.observation_id IS NULL
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)$before$;
 after_text text=$after$  CROSS JOIN LATERAL(
   SELECT event.* FROM market_data.knowledge_events event
   WHERE event.catalog_id=r.catalog_id AND event.revision_id=r.id AND event.observation_id IS NULL
   OFFSET 0
  )e
  CROSS JOIN LATERAL(
   SELECT receipt.* FROM market_data.market_knowledge_receipts receipt
   WHERE(receipt.catalog_id,receipt.event_order,receipt.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
   OFFSET 0
  )k$after$;
BEGIN
 definition=pg_get_functiondef('security.reference_version_visible(uuid,uuid)'::regprocedure);
 IF NOT EXISTS(SELECT 1 FROM pg_proc p
  WHERE p.oid='security.reference_version_visible(uuid,uuid)'::regprocedure
   AND encode(sha256(convert_to(definition,'UTF8')),'hex')='28471f04e0812cefddb0c188010db6952ceeae4efd880ddb33e2b2b0d32a3b82'
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND p.proconfig=ARRAY['search_path=pg_catalog','plan_cache_mode=force_generic_plan'] AND p.provolatile='v'
   AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'::aclitem[])
 THEN RAISE EXCEPTION 'reference_receipt_upgrade: unexpected helper security.reference_version_visible(uuid,uuid)';END IF;
 IF(length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1
 THEN RAISE EXCEPTION 'reference_receipt_upgrade: unexpected publication and receipt lookup';END IF;
 EXECUTE replace(definition,before_text,after_text);
END $upgrade$;
COMMIT;
