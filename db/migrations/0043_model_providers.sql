-- Page-assistant model providers and write-only credentials (docs/ai-assistant.md §5).
-- No provider, credential or key material is seeded. The API seals each key itself, so the
-- database never receives it: it keeps only nonce, ciphertext, tag and last four characters,
-- and the audit a redaction marker with a keyed retry digest. Providers stay unqualified
-- until their adapters ship, so none can be enabled and no enabled state is stored yet.
BEGIN;
CREATE SCHEMA assistant AUTHORIZATION openetrm_security_owner;
INSERT INTO security.permissions VALUES('ManageModelProviders');
INSERT INTO security.role_permissions SELECT id,'ManageModelProviders' FROM security.roles WHERE code='access_administrator';

CREATE DOMAIN assistant.vendor AS text CHECK(VALUE IN('anthropic','openai','google','xai','meta'));
CREATE TABLE assistant.providers(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,vendor assistant.vendor NOT NULL,
 version integer NOT NULL CHECK(version>0),
 UNIQUE(tenant_id,workspace_id,vendor),UNIQUE(id,tenant_id,workspace_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TABLE assistant.credentials(
 provider_id uuid NOT NULL REFERENCES assistant.providers(id),version integer NOT NULL CHECK(version>0),
 nonce bytea NOT NULL CHECK(octet_length(nonce)=12),ciphertext bytea NOT NULL CHECK(octet_length(ciphertext) BETWEEN 8 AND 512),
 tag bytea NOT NULL CHECK(octet_length(tag)=16),last_four text NOT NULL CHECK(last_four~'^[!-~]{4}$'),
 set_by uuid NOT NULL REFERENCES security.principals(id),set_at ref_data.instant NOT NULL,
 PRIMARY KEY(provider_id,version)
);
CREATE TABLE assistant.administration_audit(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 action text NOT NULL CHECK(action~'^[a-z_]{1,40}$'),provider_id uuid,
 request jsonb NOT NULL,key_digest text CHECK(key_digest~'^[0-9a-f]{64}$'),before_state jsonb,after_state jsonb NOT NULL,
 recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,operation_id),FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id),
 -- An entry names a provider of its own workspace; actions without a provider leave it empty.
 FOREIGN KEY(provider_id,tenant_id,workspace_id) REFERENCES assistant.providers(id,tenant_id,workspace_id),
 CHECK(action<>'replace_key' OR provider_id IS NOT NULL AND key_digest IS NOT NULL)
);
CREATE INDEX administration_audit_recent ON assistant.administration_audit(tenant_id,workspace_id,recorded_at DESC,operation_id);
CREATE TRIGGER immutable_credential BEFORE UPDATE OR DELETE ON assistant.credentials FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_audit BEFORE UPDATE OR DELETE ON assistant.administration_audit FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION assistant.guard_provider() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR(NEW.id,NEW.tenant_id,NEW.workspace_id,NEW.vendor) IS DISTINCT FROM(OLD.id,OLD.tenant_id,OLD.workspace_id,OLD.vendor) OR NEW.version<>OLD.version+1 THEN
  RAISE EXCEPTION 'A model provider keeps its identity and advances one version at a time' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_provider BEFORE UPDATE OR DELETE ON assistant.providers FOR EACH ROW EXECUTE FUNCTION assistant.guard_provider();

-- Internal helpers run with their callers' rights; only the three entry points below are
-- security definers callable by the runtime.
-- The bound request context of a human or service principal that currently holds
-- ManageModelProviders. Agents never administer providers or keys, so a delegated context is refused.
CREATE FUNCTION assistant.actor() RETURNS security.request_contexts
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.id IS NULL OR c.delegation_id IS NOT NULL OR NOT security.has_permission('ManageModelProviders') THEN
  RAISE EXCEPTION 'No current model provider administration permission' USING ERRCODE='42501';
 END IF;
 RETURN c;
END $$;
-- The provider's state with its current credential's last four characters, never the key.
CREATE FUNCTION assistant.provider_state(provider uuid) RETURNS jsonb
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('vendor',p.vendor,'version',p.version,'credential',
  (SELECT jsonb_build_object('version',k.version,'lastFour',k.last_four,'setBy',m.display_name,'setAt',k.set_at)
   FROM assistant.credentials k JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(p.tenant_id,p.workspace_id,k.set_by)
   WHERE k.provider_id=p.id ORDER BY k.version DESC LIMIT 1))
 FROM assistant.providers p WHERE p.id=provider;
$$;
CREATE FUNCTION assistant.provider_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();
BEGIN
 RETURN jsonb_build_object(
  'providers',coalesce((SELECT jsonb_agg(assistant.provider_state(p.id) ORDER BY p.vendor) FROM assistant.providers p WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,'vendor',a.vendor) ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,m.display_name actor,p.vendor FROM assistant.administration_audit a
    JOIN assistant.providers p ON p.id=a.provider_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
-- The audited form of a key replacement: the key itself is only ever a redaction marker.
CREATE FUNCTION assistant.key_request(vendor assistant.vendor,expected_version integer) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('action','replace_key','vendor',vendor,'expectedVersion',expected_version,'key','redacted');
$$;
-- The original outcome of an identical retry, null for a new operation; any other reuse conflicts.
CREATE FUNCTION assistant.replayed_key(c security.request_contexts,operation uuid,vendor assistant.vendor,expected_version integer,key_digest text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE previous assistant.administration_audit;
BEGIN
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR vendor IS NULL OR expected_version IS NULL OR expected_version<0
  OR key_digest IS NULL OR key_digest!~'^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'model_admin_input';END IF;
 SELECT * INTO previous FROM assistant.administration_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation);
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF previous.actor_id<>c.actor_id OR previous.request<>assistant.key_request(vendor,expected_version) OR previous.key_digest IS DISTINCT FROM key_digest THEN
  RAISE EXCEPTION 'model_admin_conflict';
 END IF;
 RETURN previous.after_state;
END $$;
-- Where the next credential will be stored, so the API can seal the key for exactly that
-- provider identity and version before it calls replace_credential. Writes nothing.
CREATE FUNCTION assistant.credential_slot(operation uuid,vendor assistant.vendor,expected_version integer,key_digest text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();p assistant.providers;replay jsonb;
BEGIN
 replay=assistant.replayed_key(c,operation,vendor,expected_version,key_digest);
 IF replay IS NOT NULL THEN RETURN jsonb_build_object('replayed',replay);END IF;
 SELECT * INTO p FROM assistant.providers WHERE(tenant_id,workspace_id,providers.vendor)=(c.tenant_id,c.workspace_id,credential_slot.vendor);
 IF coalesce(p.version,0)<>expected_version THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 RETURN jsonb_build_object('providerId',coalesce(p.id,gen_random_uuid()),
  'credentialVersion',coalesce((SELECT max(k.version) FROM assistant.credentials k WHERE k.provider_id=p.id),0)+1);
END $$;
CREATE FUNCTION assistant.replace_credential(operation uuid,vendor assistant.vendor,expected_version integer,provider uuid,credential_version integer,
 nonce bytea,ciphertext bytea,tag bytea,last_four text,key_digest text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();p assistant.providers;replay jsonb;before jsonb;after jsonb;n timestamptz;
BEGIN
 -- One actor's commands run in order, so a retry that overlaps its original replays its outcome.
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 replay=assistant.replayed_key(c,operation,vendor,expected_version,key_digest);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 SELECT * INTO p FROM assistant.providers WHERE(tenant_id,workspace_id,providers.vendor)=(c.tenant_id,c.workspace_id,replace_credential.vendor) FOR UPDATE;
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
 RETURN after;
END $$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
ALTER DOMAIN assistant.vendor OWNER TO openetrm_security_owner;
GRANT USAGE ON SCHEMA assistant TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION assistant.provider_directory(),assistant.credential_slot(uuid,assistant.vendor,integer,text),
 assistant.replace_credential(uuid,assistant.vendor,integer,uuid,integer,bytea,bytea,bytea,text,text) TO openetrm_runtime;
COMMIT;
