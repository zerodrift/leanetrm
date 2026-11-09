-- Current consumer: large close-pin correction reads in connected Blotter valuation.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$
DECLARE item record;body text;needle text;replacement text;expected integer;
BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.read_observation_facts(uuid,uuid,uuid[])','f8af0ba4424a04d4236696939ed371e2e1623de08d274f2d7d42df2113084cda',3),
  ('security.observation_visibility_scope(uuid,uuid,uuid[])','804b0cf88d3f589ca640363358faeb5f1c94e71ac693a0d4cffef7074c57895d',1)
 )v(signature,hash,grants) LOOP
  SELECT pg_get_functiondef(p.oid) INTO body FROM pg_proc p WHERE p.oid=item.signature::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.hash
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND p.proconfig=ARRAY['search_path=pg_catalog']
   AND p.proacl=CASE WHEN item.grants=3 THEN
    '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'::aclitem[]
    ELSE '{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[] END;
  IF body IS NULL THEN RAISE EXCEPTION 'fact_anchor_upgrade: unexpected helper %',item.signature;END IF;
  IF item.grants=3 THEN
   needle='JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id)=(requested_catalog,requested_revision,p.id)';
   replacement='CROSS JOIN LATERAL(SELECT candidate.* FROM market_data.observations candidate WHERE(candidate.catalog_id,candidate.definition_revision_id,candidate.id)=(requested_catalog,requested_revision,p.id) OFFSET 0)o';
   expected=1;
  ELSE
   needle='JOIN market_data.observations o ON o.id=p.id AND o.catalog_id=requested_catalog AND o.definition_revision_id=requested_revision';
   replacement='CROSS JOIN LATERAL(SELECT candidate.* FROM market_data.observations candidate WHERE(candidate.catalog_id,candidate.definition_revision_id,candidate.id)=(requested_catalog,requested_revision,p.id) OFFSET 0)o';
   expected=2;
  END IF;
  IF length(body)-length(replace(body,needle,''))<>expected*length(needle)
  THEN RAISE EXCEPTION 'fact_anchor_upgrade: unexpected point joins %',item.signature;END IF;
  EXECUTE replace(body,needle,replacement);
 END LOOP;
END $upgrade$;
COMMIT;
