-- Page-assistant model capability versions and model Enable and Disable (docs/ai-assistant.md
-- §5.1, §6.1, §6.3, §9). A capability version records, each with its source and checked date,
-- the model's output limit, the reasoning efforts it accepts, that its identifier is not
-- repointed, the identifiers its vendor may report as served, and that it supports tool calling
-- and streaming; it also records the usage mapping it was validated under, whose stated efforts
-- bound its own. A model is qualified while its current version was validated under the mapping
-- its vendor's adapter applies, the adapter the conformance suite exercises. Only Enable enables a
-- qualified model, and only on an enabled, qualified provider. Edit records the next version and
-- leaves the model's state alone: a running turn stops at its next call once its version is no
-- longer current (§7.1). Versions and stated efforts are immutable; Edit, Enable and Disable
-- advance the model row's version. Like 0051, each command rechecks the actor's current authority
-- after its principal and model row waits, before it replays an earlier outcome, and after its
-- writes, and every definer this migration creates renders instants in UTC. Every list a version
-- or confirmed mapping holds, sources included, is one-dimensional, numbered from 1 and distinct.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority, replay and qualification helpers these operations rely on and the definitions this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.replayed(security.request_contexts,uuid,jsonb)','848b43fd8f72b0787471d91379f5b9f570daa271fcc669459a04d73f0fb54efd',false),
  ('assistant.provider_qualified(uuid)','784555e5e2dd89746140ef3ab3a1a87cc06cb7b4482b4dfcaa0ff0a113d0be18',false),
  ('assistant.is_model_identifier(text)','68d215ea50a0bcdcb0b01a3729d827e49060b53d1fec00d00dd8e69cdddd8749',false),
  ('assistant.is_source(text)','90ea58cf5a0ebfc6b96753118737b1cee49c4bf6ca3cbf9967f387d7b77486e4',false),
  ('assistant.is_checked_on(date,timestamp with time zone)','134b428f7281ba60c796f3d44d58e50c4ab4bade2c0f6bd3c529455f3469e845',false),
  ('assistant.are_sources(text[])','0dbf4c2c6f5ff5d44332aaddfc0c84dddfa4ee714c0abbc3ce0b5fe920a3382f',false),
  ('assistant.model_state(uuid)','912843830f98bb906e58a157387042cf635a190a0ca927e9edc15fe058684e75',false)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'model_versions: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- A list as the API sends it and JSON renders it: one dimension numbered from 1, or empty, with no
-- null or repeated element.
CREATE FUNCTION assistant.is_distinct_list(items text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(array_ndims(items)=1 AND array_lower(items,1)=1,cardinality(items)=0)
  AND cardinality(items)=(SELECT count(DISTINCT i) FROM unnest(items) i)
$$;
CREATE OR REPLACE FUNCTION assistant.are_sources(sources text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT cardinality(sources)>0 AND assistant.is_distinct_list(sources) AND NOT EXISTS(SELECT 1 FROM unnest(sources) s WHERE assistant.is_source(s) IS NOT TRUE)
$$;
-- A replaced rule does not recheck stored rows; adding the constraint again checks every confirmation.
ALTER TABLE assistant.confirmed_mappings DROP CONSTRAINT confirmed_mappings_sources_check,
 ADD CONSTRAINT confirmed_mappings_sources_check CHECK(assistant.are_sources(sources));
-- Reasoning efforts: a non-empty list of distinct lowercase words.
CREATE FUNCTION assistant.are_efforts(efforts text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT cardinality(efforts)>0 AND assistant.is_distinct_list(efforts) AND NOT EXISTS(SELECT 1 FROM unnest(efforts) e WHERE e!~'^[a-z]{1,16}$')
$$;
-- The identifiers a vendor may report as served for a model: at most 16 distinct model identifiers.
CREATE FUNCTION assistant.are_identifiers(identifiers text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT cardinality(identifiers)<=16 AND assistant.is_distinct_list(identifiers)
  AND NOT EXISTS(SELECT 1 FROM unnest(identifiers) i WHERE assistant.is_model_identifier(i) IS NOT TRUE)
$$;

-- The reasoning efforts a confirmed usage mapping's adapter sends (§6.1): adapter constants stated
-- with the mapping version and, like it, never changed. messages_v1 always requests adaptive
-- thinking with one of them. An adapter applies only a mapping whose efforts are stated.
CREATE TABLE assistant.mapping_efforts(
 vendor assistant.vendor NOT NULL,mapping text NOT NULL,efforts text[] NOT NULL CHECK(assistant.are_efforts(efforts)),
 PRIMARY KEY(vendor,mapping),
 FOREIGN KEY(vendor,mapping) REFERENCES assistant.confirmed_mappings(vendor,mapping)
);
CREATE TRIGGER immutable_mapping_efforts BEFORE UPDATE OR DELETE ON assistant.mapping_efforts FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
INSERT INTO assistant.mapping_efforts VALUES('anthropic','messages_v1',ARRAY['low','medium','high','xhigh','max']);
ALTER TABLE assistant.adapter_mappings ADD FOREIGN KEY(vendor,mapping) REFERENCES assistant.mapping_efforts(vendor,mapping);

-- A model's capability versions. Each names its model's own provider and that provider's vendor by
-- foreign key, and a mapping of that vendor whose efforts are stated.
ALTER TABLE assistant.providers ADD UNIQUE(id,vendor);
ALTER TABLE assistant.models ADD UNIQUE(id,provider_id);
CREATE TABLE assistant.model_versions(
 model_id uuid NOT NULL,version integer NOT NULL CHECK(version>0),provider_id uuid NOT NULL,
 vendor assistant.vendor NOT NULL,mapping text NOT NULL,
 output_limit integer NOT NULL CHECK(output_limit BETWEEN 1 AND 10000000),
 output_limit_source text NOT NULL CHECK(assistant.is_source(output_limit_source)),
 output_limit_checked_on date NOT NULL,
 reasoning_efforts text[] NOT NULL CHECK(assistant.are_efforts(reasoning_efforts)),
 reasoning_source text NOT NULL CHECK(assistant.is_source(reasoning_source)),
 reasoning_checked_on date NOT NULL,
 identifier_stability_source text NOT NULL CHECK(assistant.is_source(identifier_stability_source)),
 identifier_stability_checked_on date NOT NULL,
 accepted_served text[] NOT NULL CHECK(assistant.are_identifiers(accepted_served)),
 accepted_served_source text NOT NULL CHECK(assistant.is_source(accepted_served_source)),
 accepted_served_checked_on date NOT NULL,
 tools_source text NOT NULL CHECK(assistant.is_source(tools_source)),
 tools_checked_on date NOT NULL,
 streaming_source text NOT NULL CHECK(assistant.is_source(streaming_source)),
 streaming_checked_on date NOT NULL,
 recorded_by uuid NOT NULL REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 -- Each fact was checked on a calendar date that had begun somewhere when the version was recorded.
 CHECK(assistant.is_checked_on(output_limit_checked_on,recorded_at) AND assistant.is_checked_on(reasoning_checked_on,recorded_at)
  AND assistant.is_checked_on(identifier_stability_checked_on,recorded_at) AND assistant.is_checked_on(accepted_served_checked_on,recorded_at)
  AND assistant.is_checked_on(tools_checked_on,recorded_at) AND assistant.is_checked_on(streaming_checked_on,recorded_at)),
 PRIMARY KEY(model_id,version),
 FOREIGN KEY(model_id,provider_id) REFERENCES assistant.models(id,provider_id),
 FOREIGN KEY(provider_id,vendor) REFERENCES assistant.providers(id,vendor),
 FOREIGN KEY(vendor,mapping) REFERENCES assistant.mapping_efforts(vendor,mapping)
);
CREATE TRIGGER immutable_model_version BEFORE UPDATE OR DELETE ON assistant.model_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
-- What a version's own columns cannot show: its efforts are ones its mapping states, and its
-- accepted served identifiers exclude its model's own. Both sides are immutable, so this holds once
-- inserted; a missing mapping or model is left to the foreign keys.
CREATE FUNCTION assistant.check_model_version() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM assistant.mapping_efforts e WHERE(e.vendor,e.mapping)=(NEW.vendor,NEW.mapping) AND NOT(NEW.reasoning_efforts<@e.efforts))
  OR EXISTS(SELECT 1 FROM assistant.models m WHERE m.id=NEW.model_id AND m.identifier=ANY(NEW.accepted_served))
 THEN RAISE EXCEPTION 'model_versions: efforts its mapping does not state, or its own identifier as served' USING ERRCODE='23514';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER stated_capabilities BEFORE INSERT ON assistant.model_versions FOR EACH ROW EXECUTE FUNCTION assistant.check_model_version();
-- A model starts disabled.
ALTER TABLE assistant.models ADD COLUMN enabled boolean NOT NULL DEFAULT false;
-- A model change, capability changes included, names its model and that model's own provider, and no key.
ALTER TABLE assistant.administration_audit DROP CONSTRAINT administration_audit_model_change,
 ADD CONSTRAINT administration_audit_model_change CHECK(
  (action IN('add_model','rename_model','add_price','withdraw_price','edit_model','enable_model','disable_model'))=(model_id IS NOT NULL)
  AND(model_id IS NULL OR provider_id IS NOT NULL AND key_digest IS NULL));

-- The model's current capability version, if it has one.
CREATE FUNCTION assistant.current_model_version(model uuid) RETURNS SETOF assistant.model_versions
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT v.* FROM assistant.model_versions v WHERE v.model_id=model ORDER BY v.version DESC LIMIT 1;
$$;
-- Whether Enable may enable the model (§6.3): its current capability version, which confirms tool
-- calling and streaming, was validated under the usage mapping its vendor's adapter applies.
CREATE FUNCTION assistant.model_qualified(model uuid) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM assistant.current_model_version(model) v JOIN assistant.adapter_mappings a ON(a.vendor,a.mapping)=(v.vendor,v.mapping));
$$;
CREATE OR REPLACE FUNCTION assistant.model_state(model uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',m.id,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,'version',m.version,
  'priceSequence',(SELECT count(*) FROM assistant.price_revisions r WHERE r.model_id=m.id),
  'enabled',m.enabled,'qualified',assistant.model_qualified(m.id),
  'capability',(SELECT jsonb_build_object('version',v.version,'mapping',v.mapping,
    'outputLimit',v.output_limit,'outputLimitSource',v.output_limit_source,'outputLimitCheckedOn',v.output_limit_checked_on,
    'reasoningEfforts',v.reasoning_efforts,'reasoningSource',v.reasoning_source,'reasoningCheckedOn',v.reasoning_checked_on,
    'identifierStabilitySource',v.identifier_stability_source,'identifierStabilityCheckedOn',v.identifier_stability_checked_on,
    'acceptedServed',v.accepted_served,'acceptedServedSource',v.accepted_served_source,'acceptedServedCheckedOn',v.accepted_served_checked_on,
    'toolsSource',v.tools_source,'toolsCheckedOn',v.tools_checked_on,'streamingSource',v.streaming_source,'streamingCheckedOn',v.streaming_checked_on,
    'recordedBy',w.display_name,'recordedAt',v.recorded_at)
   FROM assistant.current_model_version(m.id) v
   JOIN security.workspace_memberships w ON(w.tenant_id,w.workspace_id,w.principal_id)=(m.tenant_id,m.workspace_id,v.recorded_by)))
 FROM assistant.models m JOIN assistant.providers p ON p.id=m.provider_id WHERE m.id=model;
$$;
-- Records the model's next capability version for the acting administrator, validated under the
-- usage mapping its vendor's adapter applies and the efforts that mapping states. The model's
-- enabled state is unchanged.
CREATE FUNCTION assistant.edit_model(operation uuid,model uuid,expected_version integer,
 output_limit integer,output_limit_source text,output_limit_checked_on date,
 reasoning_efforts text[],reasoning_source text,reasoning_checked_on date,
 identifier_stability_source text,identifier_stability_checked_on date,
 accepted_served text[],accepted_served_source text,accepted_served_checked_on date,
 tools_source text,tools_checked_on date,streaming_source text,streaming_checked_on date) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();m assistant.models;vendor assistant.vendor;mapping text;stated text[];
 replay jsonb;before jsonb;after jsonb;n timestamptz;
 request jsonb:=jsonb_build_object('action','edit_model','model',model,'expectedVersion',expected_version,
  'outputLimit',output_limit,'outputLimitSource',output_limit_source,'outputLimitCheckedOn',output_limit_checked_on,
  'reasoningEfforts',reasoning_efforts,'reasoningSource',reasoning_source,'reasoningCheckedOn',reasoning_checked_on,
  'identifierStabilitySource',identifier_stability_source,'identifierStabilityCheckedOn',identifier_stability_checked_on,
  'acceptedServed',accepted_served,'acceptedServedSource',accepted_served_source,'acceptedServedCheckedOn',accepted_served_checked_on,
  'toolsSource',tools_source,'toolsCheckedOn',tools_checked_on,'streamingSource',streaming_source,'streamingCheckedOn',streaming_checked_on);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 -- An accepted served identifier is a vendor model identifier (§6.1).
 IF EXISTS(SELECT 1 FROM unnest(accepted_served) i WHERE assistant.is_model_identifier(i) IS NOT TRUE) THEN RAISE EXCEPTION 'model_identifier_invalid';END IF;
 IF model IS NULL OR expected_version IS NULL OR expected_version<1 OR output_limit IS NULL OR output_limit NOT BETWEEN 1 AND 10000000
  OR assistant.are_efforts(reasoning_efforts) IS NOT TRUE OR assistant.are_identifiers(accepted_served) IS NOT TRUE
  OR(assistant.is_source(output_limit_source) AND assistant.is_source(reasoning_source) AND assistant.is_source(identifier_stability_source)
   AND assistant.is_source(accepted_served_source) AND assistant.is_source(tools_source) AND assistant.is_source(streaming_source)
   AND assistant.is_checked_on(output_limit_checked_on,clock_timestamp()) AND assistant.is_checked_on(reasoning_checked_on,clock_timestamp())
   AND assistant.is_checked_on(identifier_stability_checked_on,clock_timestamp()) AND assistant.is_checked_on(accepted_served_checked_on,clock_timestamp())
   AND assistant.is_checked_on(tools_checked_on,clock_timestamp()) AND assistant.is_checked_on(streaming_checked_on,clock_timestamp())) IS NOT TRUE
 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 SELECT * INTO m FROM assistant.models x WHERE x.id=edit_model.model AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE;
 -- The row wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND OR m.version<>expected_version THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 SELECT p.vendor,a.mapping,e.efforts INTO vendor,mapping,stated FROM assistant.providers p
  LEFT JOIN assistant.adapter_mappings a ON a.vendor=p.vendor LEFT JOIN assistant.mapping_efforts e ON(e.vendor,e.mapping)=(a.vendor,a.mapping)
  WHERE p.id=m.provider_id;
 -- Without an adapter that applies a confirmed mapping, nothing in this release interprets the facts.
 IF mapping IS NULL THEN RAISE EXCEPTION 'model_mapping_unconfirmed';END IF;
 IF NOT(reasoning_efforts<@stated) OR m.identifier=ANY(accepted_served) THEN RAISE EXCEPTION 'model_admin_input';END IF;
 before=assistant.model_state(m.id);
 n=clock_timestamp();
 INSERT INTO assistant.model_versions VALUES(m.id,coalesce((SELECT max(v.version) FROM assistant.model_versions v WHERE v.model_id=m.id),0)+1,
  m.provider_id,vendor,mapping,output_limit,output_limit_source,output_limit_checked_on,reasoning_efforts,reasoning_source,reasoning_checked_on,
  identifier_stability_source,identifier_stability_checked_on,accepted_served,accepted_served_source,accepted_served_checked_on,
  tools_source,tools_checked_on,streaming_source,streaming_checked_on,c.actor_id,n);
 UPDATE assistant.models x SET version=x.version+1 WHERE x.id=m.id;
 after=assistant.model_state(m.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at,model_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'edit_model',m.provider_id,request,before,after,n,m.id);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
-- Enable or Disable for the acting administrator. Only a qualified model on an enabled, qualified
-- provider is enabled.
CREATE FUNCTION assistant.switch_model(operation uuid,model uuid,expected_version integer,enable boolean) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();m assistant.models;p assistant.providers;replay jsonb;before jsonb;after jsonb;
 action text:=CASE WHEN enable THEN 'enable_model' ELSE 'disable_model' END;
 request jsonb:=jsonb_build_object('action',action,'model',model,'expectedVersion',expected_version);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF model IS NULL OR expected_version IS NULL OR expected_version<1 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 SELECT * INTO m FROM assistant.models x WHERE x.id=switch_model.model AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE;
 -- The row wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND OR m.version<>expected_version OR m.enabled=enable THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 IF enable THEN
  IF NOT assistant.model_qualified(m.id) THEN RAISE EXCEPTION 'model_unqualified';END IF;
  SELECT * INTO p FROM assistant.providers x WHERE x.id=m.provider_id;
  IF NOT assistant.provider_qualified(p.id) THEN RAISE EXCEPTION 'model_provider_unqualified';END IF;
  IF NOT p.enabled THEN RAISE EXCEPTION 'model_provider_disabled';END IF;
 END IF;
 before=assistant.model_state(m.id);
 UPDATE assistant.models x SET version=x.version+1,enabled=enable WHERE x.id=m.id;
 after=assistant.model_state(m.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at,model_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,action,m.provider_id,request,before,after,clock_timestamp(),m.id);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
CREATE FUNCTION assistant.enable_model(operation uuid,model uuid,expected_version integer) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.switch_model(operation,model,expected_version,true);
$$;
CREATE FUNCTION assistant.disable_model(operation uuid,model uuid,expected_version integer) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.switch_model(operation,model,expected_version,false);
$$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' AND tablename IN('mapping_efforts','model_versions') LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.edit_model(uuid,uuid,integer,integer,text,date,text[],text,date,text,date,text[],text,date,text,date,text,date),
 assistant.enable_model(uuid,uuid,integer),assistant.disable_model(uuid,uuid,integer) TO openetrm_runtime;
COMMIT;
