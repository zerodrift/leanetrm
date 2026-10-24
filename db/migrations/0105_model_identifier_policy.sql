-- Explicit approval of provider aliases on immutable model capability versions.
-- Existing versions and the legacy 18-argument command retain their fixed-identifier contract.
-- New clients must choose a policy; neither policy permits unlisted served identifiers.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
DO $$ BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('assistant.model_state(uuid)')
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef=false AND p.provolatile='s'
   AND p.proconfig=ARRAY['search_path=pg_catalog'] AND cardinality(p.proacl)=1
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')='803445876d2908909a82f0609067ec376691605d685ee4cf51abf177b7e79909')
  THEN RAISE EXCEPTION 'model_identifier_policy: unexpected helper model_state' USING ERRCODE='55000';END IF;
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('assistant.edit_model(uuid,uuid,integer,integer,text,date,text[],text,date,text,date,text[],text,date,text,date,text,date)')
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef=true AND p.provolatile='v'
   AND p.proconfig=ARRAY['search_path=pg_catalog','TimeZone=UTC'] AND cardinality(p.proacl)=2
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]
   AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')='2fcdd4344ed65f3ac81b6d607187eccd8d694931c23091b26e38781b8477acc8')
  THEN RAISE EXCEPTION 'model_identifier_policy: unexpected helper edit_model' USING ERRCODE='55000';END IF;
END $$;
-- Appending a column preserves the legacy command's positional insertion: PostgreSQL fills
-- its omitted trailing value from this compatibility default. Existing rows are fixed.
ALTER TABLE assistant.model_versions ADD COLUMN identifier_policy text NOT NULL DEFAULT 'fixed'
 CHECK(identifier_policy IN('fixed','alias'));
CREATE OR REPLACE FUNCTION assistant.model_state(model uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',m.id,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,'version',m.version,
  'priceSequence',(SELECT count(*) FROM assistant.price_revisions r WHERE r.model_id=m.id),
  'enabled',m.enabled,'qualified',assistant.model_qualified(m.id),
  'capability',(SELECT jsonb_build_object('version',v.version,'mapping',v.mapping,'identifierPolicy',v.identifier_policy,
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

CREATE FUNCTION assistant.edit_model(operation uuid,model uuid,expected_version integer,
 output_limit integer,output_limit_source text,output_limit_checked_on date,
 reasoning_efforts text[],reasoning_source text,reasoning_checked_on date,
 identifier_stability_source text,identifier_stability_checked_on date,
 accepted_served text[],accepted_served_source text,accepted_served_checked_on date,
 tools_source text,tools_checked_on date,streaming_source text,streaming_checked_on date,identifier_policy text) RETURNS jsonb
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
 request=request||jsonb_build_object('identifierPolicy',identifier_policy);
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 -- An accepted served identifier is a vendor model identifier (§6.1).
 IF EXISTS(SELECT 1 FROM unnest(accepted_served) i WHERE assistant.is_model_identifier(i) IS NOT TRUE) THEN RAISE EXCEPTION 'model_identifier_invalid';END IF;
 IF identifier_policy IS NULL OR identifier_policy NOT IN('fixed','alias') OR model IS NULL OR expected_version IS NULL OR expected_version<1 OR output_limit IS NULL OR output_limit NOT BETWEEN 1 AND 10000000
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
  tools_source,tools_checked_on,streaming_source,streaming_checked_on,c.actor_id,n,identifier_policy);
 UPDATE assistant.models x SET version=x.version+1 WHERE x.id=m.id;
 after=assistant.model_state(m.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at,model_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'edit_model',m.provider_id,request,before,after,n,m.id);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
ALTER FUNCTION assistant.edit_model(uuid,uuid,integer,integer,text,date,text[],text,date,text,date,text[],text,date,text,date,text,date,text)
 OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.edit_model(uuid,uuid,integer,integer,text,date,text[],text,date,text,date,text[],text,date,text,date,text,date,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.edit_model(uuid,uuid,integer,integer,text,date,text[],text,date,text,date,text[],text,date,text,date,text,date,text)
 TO openetrm_runtime;
COMMIT;
