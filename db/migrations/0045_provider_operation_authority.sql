-- Revalidate current provider authority after each explicit row wait.
-- Retain migration 0043 and the granted operation's identity and privileges.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.replace_credential(uuid,assistant.vendor,integer,uuid,integer,bytea,bytea,bytea,text,text)',
   'f4c821f77a5765d399ff2e692f0ff0dce65d8d2f962d151358ea7dfbea1e7441',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'provider_operation_authority: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
CREATE OR REPLACE FUNCTION assistant.replace_credential(operation uuid,vendor assistant.vendor,expected_version integer,provider uuid,credential_version integer,
 nonce bytea,ciphertext bytea,tag bytea,last_four text,key_digest text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();p assistant.providers;replay jsonb;before jsonb;after jsonb;n timestamptz;
BEGIN
 -- One actor's commands run in order, so a retry that overlaps its original replays its outcome.
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 -- A row wait can outlive the grant or request context, including on a retry.
 c:=assistant.actor();
 replay=assistant.replayed_key(c,operation,vendor,expected_version,key_digest);
 IF replay IS NOT NULL THEN
  PERFORM assistant.actor();
  RETURN replay;
 END IF;
 SELECT * INTO p FROM assistant.providers WHERE(tenant_id,workspace_id,providers.vendor)=(c.tenant_id,c.workspace_id,replace_credential.vendor) FOR UPDATE;
 -- Assignment preserves the SELECT's FOUND value for the first-key branch.
 c:=assistant.actor();
 IF NOT FOUND THEN
  IF expected_version<>0 OR credential_version<>1 THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
  -- Another administrator adding this vendor's first key at once makes this a unique violation.
  INSERT INTO assistant.providers(id,tenant_id,workspace_id,vendor,version) VALUES(provider,c.tenant_id,c.workspace_id,vendor,1);
 ELSE
  IF p.id<>provider OR p.version<>expected_version
   OR credential_version<>coalesce((SELECT max(k.version) FROM assistant.credentials k WHERE k.provider_id=p.id),0)+1 THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
  before=assistant.provider_state(p.id);
  UPDATE assistant.providers SET version=version+1 WHERE id=p.id;
 END IF;
 n=clock_timestamp();
 INSERT INTO assistant.credentials VALUES(provider,credential_version,nonce,ciphertext,tag,last_four,c.actor_id,n);
 after=assistant.provider_state(provider);
 INSERT INTO assistant.administration_audit VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'replace_key',provider,
  assistant.key_request(vendor,expected_version),key_digest,before,after,n);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
COMMIT;
