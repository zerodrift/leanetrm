-- The Providers directory states each provider's latest test as Settings › AI reads it, open or
-- closed, and whether it ran under the key and terms attested now, so the Providers tab shows its last
-- test and when a new key or terms have outdated it (docs/ai-assistant.md §9). Nothing else changes;
-- the replaced definition keeps its owner and grants.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority, state, attestation and test helpers the directory relies on and the definition this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.provider_state(uuid)','209cf47e0039768259964354779b37ee863554a06b4418a7888f3aeb77f312b0',false),
  ('assistant.current_attestation(uuid)','142978f0af07358286fbd725af2e4b8e073a04143401f30cef8db1452e78a51c',false),
  ('assistant.test_state(uuid)','ea3b69ba64f03e51825c454c28d73add1731e65a7423c0b30577ab9b6d87eba1',false),
  ('assistant.provider_directory()','45f238584689d0206821567681ad07558b54aefb755b440738fcbe5dbf36fe55',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'directory_tests: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- As in 0061, with each provider's latest test by its start, which a provider never tested lacks, and whether
-- the key and terms it ran under are those attested now. Equal starts resolve by call id, the same way every time.
-- Every model call is a test until turns record theirs (§12 step 5), which must then leave them out here.
CREATE OR REPLACE FUNCTION assistant.provider_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();
BEGIN
 RETURN jsonb_build_object(
  'mappings',coalesce((SELECT jsonb_object_agg(a.vendor,jsonb_build_object('mapping',m.mapping,'confirmedOn',m.confirmed_on,'sources',m.sources,'efforts',e.efforts))
   FROM assistant.adapter_mappings a JOIN assistant.confirmed_mappings m ON(m.vendor,m.mapping)=(a.vendor,a.mapping)
   JOIN assistant.mapping_efforts e ON(e.vendor,e.mapping)=(a.vendor,a.mapping)),'{}'::jsonb),
  'providers',coalesce((SELECT jsonb_agg(assistant.provider_state(p.id) ORDER BY p.vendor) FROM assistant.providers p WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'tests',coalesce((SELECT jsonb_object_agg(p.vendor,assistant.test_state(t.id)||jsonb_build_object('underCurrentAttestation',
    EXISTS(SELECT 1 FROM assistant.current_attestation(p.id) a WHERE(a.credential_version,a.terms_version)=(t.credential_version,t.terms_version))))
   FROM assistant.providers p
   CROSS JOIN LATERAL(SELECT x.id,x.credential_version,x.terms_version FROM assistant.model_calls x WHERE x.provider_id=p.id
    ORDER BY x.started_at DESC,x.id DESC LIMIT 1)t
   WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'{}'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,'vendor',a.vendor) ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,m.display_name actor,p.vendor FROM assistant.administration_audit a
    JOIN assistant.providers p ON p.id=a.provider_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND a.model_id IS NULL ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
COMMIT;
