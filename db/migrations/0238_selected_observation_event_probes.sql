-- Resolve each selected observation's event before its certified receipt. A
-- generic plan must not enumerate unrelated receipts before matching identity.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$
DECLARE definition text;
 before_text text=$before$   JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)$before$;
 after_text text=$after$   CROSS JOIN LATERAL(
    SELECT event.* FROM market_data.knowledge_events event
    WHERE(event.catalog_id,event.revision_id,event.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
    OFFSET 0
   )e$after$;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc p
  WHERE p.oid='security.observation_visibility_scope(uuid,uuid,uuid[])'::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='62d89bd8da225c24c8d2f1c06f7bba5314deccd5633884bc7dd6825647d8758d'
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND p.proconfig=ARRAY['search_path=pg_catalog','jit=off'] AND p.provolatile='v'
   AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[])
 THEN RAISE EXCEPTION 'observation_event_upgrade: unexpected helper security.observation_visibility_scope(uuid,uuid,uuid[])';END IF;
 definition=pg_get_functiondef('security.observation_visibility_scope(uuid,uuid,uuid[])'::regprocedure);
 IF(length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1
 THEN RAISE EXCEPTION 'observation_event_upgrade: unexpected selected event lookup';END IF;
 EXECUTE replace(definition,before_text,after_text);
END $upgrade$;
COMMIT;
