-- Page-assistant provider qualification (docs/ai-assistant.md §5.1, §6.3, §9). A provider is
-- qualified when its vendor's adapter applies a usage mapping confirmed from published
-- documentation, an administrator has recorded the vendor's terms, and an administrator has
-- attested the provider's current credential and terms versions. Enable alone enables a
-- qualified provider; Attest never does. Replace key and new terms each leave the current pair
-- unattested and disable the provider in the same transaction. Mappings, terms versions and
-- attestations are immutable; Record terms, Attest, Enable and Disable advance the provider
-- row's version. Like 0051, each command rechecks the actor's current authority after its
-- principal and provider row waits, before it replays an earlier outcome, and after its
-- writes, and every definer this migration creates or replaces renders instants in UTC.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority and replay helpers these operations rely on and the definitions this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.replayed(security.request_contexts,uuid,jsonb)','848b43fd8f72b0787471d91379f5b9f570daa271fcc669459a04d73f0fb54efd',false),
  ('assistant.replayed_key(security.request_contexts,uuid,assistant.vendor,integer,text)','a8d2b1c095c203d0c7341f3d61b585a3ac098b792ef39a80151f501ecd0bad99',false),
  ('assistant.key_request(assistant.vendor,integer)','120f2908374e7e20658bd94554a1adde8b63816153581e26a3abc4e7e63a04e6',false),
  ('assistant.provider_state(uuid)','4aeeabb41f34de59c21aa51b671e15cce94f6cf0bba621e5cb3b009ee7f797fa',false),
  ('assistant.provider_directory()','e2c802c1546681fbb420808eb08f56f6f7e5c67be792d654900d6356723234ca',true),
  ('assistant.replace_credential(uuid,assistant.vendor,integer,uuid,integer,bytea,bytea,bytea,text,text)',
   '7d4295028510364de390e67bf8269804ec656f66958c9b2dcf0a67bd71b5f5f7',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'provider_qualification: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- Text an administrator records from a vendor's terms: 1 to 1000 characters without surrounding
-- spaces, control characters, bidirectional controls or line separators. The classes are spelled
-- out so the rule never depends on the database's locale.
CREATE FUNCTION assistant.is_terms_text(t text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT length(t) BETWEEN 1 AND 1000
 AND t!~'[\x01-\x1f\x7f-\x9f\u2028\u2029\u202a-\u202e\u2066-\u2069\ufeff]'
 AND t!~'^[ \u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]|[ \u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]$' $$;
-- A published source: an https URL naming a host, without user information or a port, in at most
-- 2048 visible ASCII characters.
CREATE FUNCTION assistant.is_source(s text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT length(s)<=2048
 AND s~'^https://[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*([/?#][!-~]*)?$' $$;
CREATE FUNCTION assistant.are_sources(sources text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT cardinality(sources)>0 AND NOT EXISTS(SELECT 1 FROM unnest(sources) s WHERE assistant.is_source(s) IS NOT TRUE)
$$;
-- Whether a source can have been checked on `d` by `at`: a calendar date that has begun
-- somewhere by then, so no administrator's today is refused (UTC+14 is the earliest zone).
CREATE FUNCTION assistant.is_checked_on(d date,at timestamptz) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT isfinite(d) AND d<=((at AT TIME ZONE 'UTC')+interval '14 hours')::date $$;

-- The usage mapping versions (§6.2) confirmed from their vendors' published documentation, with
-- the date of each confirmation and the pages it was made against. Only a reviewed migration adds
-- one, and none is removed.
CREATE TABLE assistant.confirmed_mappings(
 vendor assistant.vendor NOT NULL,mapping text NOT NULL CHECK(mapping~'^[a-z][a-z0-9_]{0,39}$'),
 confirmed_on date NOT NULL CHECK(isfinite(confirmed_on)),sources text[] NOT NULL CHECK(assistant.are_sources(sources)),
 PRIMARY KEY(vendor,mapping)
);
-- The confirmed mapping each vendor's adapter applies in this release. A release that changes an
-- adapter's mapping confirms the new version and repoints its vendor here in the same migration;
-- a vendor without a row, whose adapter's mapping is unconfirmed or which has no adapter, stays
-- unqualified.
CREATE TABLE assistant.adapter_mappings(
 vendor assistant.vendor PRIMARY KEY,mapping text NOT NULL,
 FOREIGN KEY(vendor,mapping) REFERENCES assistant.confirmed_mappings(vendor,mapping)
);
INSERT INTO assistant.confirmed_mappings VALUES('anthropic','messages_v1','2026-10-20',ARRAY[
 'https://platform.claude.com/docs/en/api/messages','https://platform.claude.com/docs/en/api/errors',
 'https://platform.claude.com/docs/en/api/service-tiers','https://platform.claude.com/docs/en/api/versioning',
 'https://platform.claude.com/docs/en/build-with-claude/streaming','https://platform.claude.com/docs/en/build-with-claude/thinking',
 'https://platform.claude.com/docs/en/build-with-claude/effort','https://platform.claude.com/docs/en/build-with-claude/fast-mode',
 'https://platform.claude.com/docs/en/build-with-claude/prompt-caching','https://platform.claude.com/docs/en/build-with-claude/context-windows',
 'https://platform.claude.com/docs/en/build-with-claude/handling-stop-reasons',
 'https://platform.claude.com/docs/en/build-with-claude/refusals-and-fallback']);
INSERT INTO assistant.adapter_mappings VALUES('anthropic','messages_v1');
-- Vendor terms as an administrator records them from the vendor's published documents: the
-- account type they apply to, and that type's retention terms, training-use terms and whether
-- the account owner can view content, each with its source and the date it was checked. Each
-- recording is a new version, and the highest is current.
CREATE TABLE assistant.provider_terms(
 provider_id uuid NOT NULL REFERENCES assistant.providers(id),version integer NOT NULL CHECK(version>0),
 account_type text NOT NULL CHECK(assistant.is_terms_text(account_type)),
 retention text NOT NULL CHECK(assistant.is_terms_text(retention)),retention_source text NOT NULL CHECK(assistant.is_source(retention_source)),
 retention_checked_on date NOT NULL CHECK(isfinite(retention_checked_on)),
 training text NOT NULL CHECK(assistant.is_terms_text(training)),training_source text NOT NULL CHECK(assistant.is_source(training_source)),
 training_checked_on date NOT NULL CHECK(isfinite(training_checked_on)),
 owner_access text NOT NULL CHECK(assistant.is_terms_text(owner_access)),owner_access_source text NOT NULL CHECK(assistant.is_source(owner_access_source)),
 owner_access_checked_on date NOT NULL CHECK(isfinite(owner_access_checked_on)),
 recorded_by uuid NOT NULL REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(provider_id,version)
);
-- An administrator's attestation that the account the provider's key belongs to is of the
-- recorded type, does not permit training on submitted content and has no data-sharing opt-in.
-- It binds the credential and terms versions it was made for, so a newer key or newer terms
-- leave the provider without a current attestation.
CREATE TABLE assistant.attestations(
 provider_id uuid NOT NULL,credential_version integer NOT NULL,terms_version integer NOT NULL,
 attested_by uuid NOT NULL REFERENCES security.principals(id),attested_at ref_data.instant NOT NULL,
 PRIMARY KEY(provider_id,credential_version,terms_version),
 FOREIGN KEY(provider_id,credential_version) REFERENCES assistant.credentials(provider_id,version),
 FOREIGN KEY(provider_id,terms_version) REFERENCES assistant.provider_terms(provider_id,version)
);
CREATE TRIGGER immutable_mapping BEFORE UPDATE OR DELETE ON assistant.confirmed_mappings FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_terms BEFORE UPDATE OR DELETE ON assistant.provider_terms FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_attestation BEFORE UPDATE OR DELETE ON assistant.attestations FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
-- A provider starts disabled.
ALTER TABLE assistant.providers ADD COLUMN enabled boolean NOT NULL DEFAULT false;
-- A qualification change names its provider and no key.
ALTER TABLE assistant.administration_audit ADD CONSTRAINT administration_audit_provider_change
 CHECK(action NOT IN('record_terms','attest','enable_provider','disable_provider') OR provider_id IS NOT NULL AND key_digest IS NULL);

-- The attestation of the provider's current credential and terms versions, if one was made.
CREATE FUNCTION assistant.current_attestation(provider uuid) RETURNS SETOF assistant.attestations
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT a.* FROM assistant.attestations a WHERE a.provider_id=provider
  AND a.credential_version=(SELECT max(k.version) FROM assistant.credentials k WHERE k.provider_id=provider)
  AND a.terms_version=(SELECT max(t.version) FROM assistant.provider_terms t WHERE t.provider_id=provider);
$$;
-- Whether Enable may enable the provider (§6.3): its vendor's adapter applies a confirmed usage
-- mapping in this release, and its current credential and terms versions are attested.
CREATE FUNCTION assistant.provider_qualified(provider uuid) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM assistant.providers p JOIN assistant.adapter_mappings m ON m.vendor=p.vendor WHERE p.id=provider)
  AND EXISTS(SELECT 1 FROM assistant.current_attestation(provider));
$$;
-- The provider's state with its current credential's last four characters, never the key, and
-- its current terms and attestation.
CREATE OR REPLACE FUNCTION assistant.provider_state(provider uuid) RETURNS jsonb
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('vendor',p.vendor,'version',p.version,'enabled',p.enabled,'qualified',assistant.provider_qualified(p.id),'credential',
  (SELECT jsonb_build_object('version',k.version,'lastFour',k.last_four,'setBy',m.display_name,'setAt',k.set_at)
   FROM assistant.credentials k JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(p.tenant_id,p.workspace_id,k.set_by)
   WHERE k.provider_id=p.id ORDER BY k.version DESC LIMIT 1),
  'terms',(SELECT jsonb_build_object('version',t.version,'accountType',t.account_type,
    'retention',t.retention,'retentionSource',t.retention_source,'retentionCheckedOn',t.retention_checked_on,
    'training',t.training,'trainingSource',t.training_source,'trainingCheckedOn',t.training_checked_on,
    'ownerAccess',t.owner_access,'ownerAccessSource',t.owner_access_source,'ownerAccessCheckedOn',t.owner_access_checked_on,
    'recordedBy',m.display_name,'recordedAt',t.recorded_at)
   FROM assistant.provider_terms t JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(p.tenant_id,p.workspace_id,t.recorded_by)
   WHERE t.provider_id=p.id ORDER BY t.version DESC LIMIT 1),
  'attestation',(SELECT jsonb_build_object('credentialVersion',a.credential_version,'termsVersion',a.terms_version,'attestedBy',m.display_name,'attestedAt',a.attested_at)
   FROM assistant.current_attestation(p.id) a JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(p.tenant_id,p.workspace_id,a.attested_by)))
 FROM assistant.providers p WHERE p.id=provider;
$$;
-- The mapping each vendor's adapter applies belongs to the release, so it is listed by vendor
-- whether or not anything is administered for that vendor.
CREATE OR REPLACE FUNCTION assistant.provider_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();
BEGIN
 RETURN jsonb_build_object(
  'mappings',coalesce((SELECT jsonb_object_agg(a.vendor,jsonb_build_object('mapping',m.mapping,'confirmedOn',m.confirmed_on,'sources',m.sources))
   FROM assistant.adapter_mappings a JOIN assistant.confirmed_mappings m ON(m.vendor,m.mapping)=(a.vendor,a.mapping)),'{}'::jsonb),
  'providers',coalesce((SELECT jsonb_agg(assistant.provider_state(p.id) ORDER BY p.vendor) FROM assistant.providers p WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,'vendor',a.vendor) ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,m.display_name actor,p.vendor FROM assistant.administration_audit a
    JOIN assistant.providers p ON p.id=a.provider_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND a.model_id IS NULL ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
-- As in 0045, and a new key also disables an existing provider: it leaves the current pair
-- unattested, so the provider stays off until it is attested and enabled again.
CREATE OR REPLACE FUNCTION assistant.replace_credential(operation uuid,vendor assistant.vendor,expected_version integer,provider uuid,credential_version integer,
 nonce bytea,ciphertext bytea,tag bytea,last_four text,key_digest text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
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
  UPDATE assistant.providers SET version=version+1,enabled=false WHERE id=p.id;
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
-- Records the vendor's terms as the provider's next terms version for the acting
-- administrator. A vendor's first terms create its provider at version 1 without a credential.
CREATE FUNCTION assistant.record_terms(operation uuid,vendor assistant.vendor,expected_version integer,account_type text,
 retention text,retention_source text,retention_checked_on date,training text,training_source text,training_checked_on date,
 owner_access text,owner_access_source text,owner_access_checked_on date) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();p assistant.providers;replay jsonb;before jsonb;after jsonb;n timestamptz;
 request jsonb:=jsonb_build_object('action','record_terms','vendor',vendor,'expectedVersion',expected_version,'accountType',account_type,
  'retention',retention,'retentionSource',retention_source,'retentionCheckedOn',retention_checked_on,
  'training',training,'trainingSource',training_source,'trainingCheckedOn',training_checked_on,
  'ownerAccess',owner_access,'ownerAccessSource',owner_access_source,'ownerAccessCheckedOn',owner_access_checked_on);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF vendor IS NULL OR expected_version IS NULL OR expected_version<0
  OR(assistant.is_terms_text(account_type) AND assistant.is_terms_text(retention) AND assistant.is_terms_text(training) AND assistant.is_terms_text(owner_access)
   AND assistant.is_source(retention_source) AND assistant.is_source(training_source) AND assistant.is_source(owner_access_source)
   AND assistant.is_checked_on(retention_checked_on,clock_timestamp()) AND assistant.is_checked_on(training_checked_on,clock_timestamp())
   AND assistant.is_checked_on(owner_access_checked_on,clock_timestamp())) IS NOT TRUE
 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 SELECT * INTO p FROM assistant.providers x WHERE(x.tenant_id,x.workspace_id,x.vendor)=(c.tenant_id,c.workspace_id,record_terms.vendor) FOR UPDATE;
 -- The row wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND THEN
  IF expected_version<>0 THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
  -- Another administrator administering this vendor first at once makes this a unique violation.
  INSERT INTO assistant.providers(id,tenant_id,workspace_id,vendor,version) VALUES(gen_random_uuid(),c.tenant_id,c.workspace_id,vendor,1) RETURNING * INTO p;
 ELSE
  IF p.version<>expected_version THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
  before=assistant.provider_state(p.id);
  -- The new terms leave the current pair unattested, so the provider stays off until it is attested and enabled again.
  UPDATE assistant.providers x SET version=x.version+1,enabled=false WHERE x.id=p.id;
 END IF;
 n=clock_timestamp();
 INSERT INTO assistant.provider_terms VALUES(p.id,coalesce((SELECT max(t.version) FROM assistant.provider_terms t WHERE t.provider_id=p.id),0)+1,
  account_type,retention,retention_source,retention_checked_on,training,training_source,training_checked_on,
  owner_access,owner_access_source,owner_access_checked_on,c.actor_id,n);
 after=assistant.provider_state(p.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'record_terms',p.id,request,before,after,n);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
-- Attests the provider's current credential and terms versions for the acting administrator.
-- It never enables the provider.
CREATE FUNCTION assistant.attest(operation uuid,vendor assistant.vendor,expected_version integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();p assistant.providers;credential integer;terms integer;replay jsonb;before jsonb;after jsonb;n timestamptz;
 request jsonb:=jsonb_build_object('action','attest','vendor',vendor,'expectedVersion',expected_version);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF vendor IS NULL OR expected_version IS NULL OR expected_version<0 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 SELECT * INTO p FROM assistant.providers x WHERE(x.tenant_id,x.workspace_id,x.vendor)=(c.tenant_id,c.workspace_id,attest.vendor) FOR UPDATE;
 -- The row wait can outlive the authority.
 c:=assistant.actor();
 SELECT max(k.version) INTO credential FROM assistant.credentials k WHERE k.provider_id=p.id;
 SELECT max(t.version) INTO terms FROM assistant.provider_terms t WHERE t.provider_id=p.id;
 -- An attestation needs a key and terms to bind, and binds each pair once.
 IF coalesce(p.version,0)<>expected_version OR credential IS NULL OR terms IS NULL
  OR EXISTS(SELECT 1 FROM assistant.attestations a WHERE(a.provider_id,a.credential_version,a.terms_version)=(p.id,credential,terms))
 THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 before=assistant.provider_state(p.id);
 UPDATE assistant.providers x SET version=x.version+1 WHERE x.id=p.id;
 n=clock_timestamp();
 INSERT INTO assistant.attestations VALUES(p.id,credential,terms,c.actor_id,n);
 after=assistant.provider_state(p.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'attest',p.id,request,before,after,n);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
-- Enable or Disable for the acting administrator. Only a qualified provider is enabled.
CREATE FUNCTION assistant.switch_provider(operation uuid,vendor assistant.vendor,expected_version integer,enable boolean) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();p assistant.providers;replay jsonb;before jsonb;after jsonb;
 action text:=CASE WHEN enable THEN 'enable_provider' ELSE 'disable_provider' END;
 request jsonb:=jsonb_build_object('action',action,'vendor',vendor,'expectedVersion',expected_version);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF vendor IS NULL OR expected_version IS NULL OR expected_version<0 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 SELECT * INTO p FROM assistant.providers x WHERE(x.tenant_id,x.workspace_id,x.vendor)=(c.tenant_id,c.workspace_id,switch_provider.vendor) FOR UPDATE;
 -- The row wait can outlive the authority.
 c:=assistant.actor();
 -- A vendor with nothing administered has no provider row: version 0, disabled and unqualified.
 IF coalesce(p.version,0)<>expected_version OR coalesce(p.enabled,false)=enable THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 IF enable AND NOT assistant.provider_qualified(p.id) THEN RAISE EXCEPTION 'model_provider_unqualified';END IF;
 before=assistant.provider_state(p.id);
 UPDATE assistant.providers x SET version=x.version+1,enabled=enable WHERE x.id=p.id;
 after=assistant.provider_state(p.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,action,p.id,request,before,after,clock_timestamp());
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
CREATE FUNCTION assistant.enable_provider(operation uuid,vendor assistant.vendor,expected_version integer) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.switch_provider(operation,vendor,expected_version,true);
$$;
CREATE FUNCTION assistant.disable_provider(operation uuid,vendor assistant.vendor,expected_version integer) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.switch_provider(operation,vendor,expected_version,false);
$$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' AND tablename IN('confirmed_mappings','adapter_mappings','provider_terms','attestations') LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.record_terms(uuid,assistant.vendor,integer,text,text,text,date,text,text,date,text,text,date),
 assistant.attest(uuid,assistant.vendor,integer),assistant.enable_provider(uuid,assistant.vendor,integer),
 assistant.disable_provider(uuid,assistant.vendor,integer) TO openetrm_runtime;
COMMIT;
