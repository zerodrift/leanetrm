-- Each agent version states its time per read (D245): the longest one tool read may take, 1 to 30 seconds and never more
-- than its time per turn. A read holds the security epoch row FOR SHARE while it runs, so this hard maximum also bounds
-- how long a read can hold back an access-control change. A read past it ends its turn limit_tool_read; a read is
-- otherwise still bounded by its turn's deadline (limit_elapsed).
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Fail closed if a definition this migration replaces differs from the reviewed one, in its text or its privileges.
DO $$
DECLARE replaced record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR replaced IN SELECT * FROM(VALUES
  ('assistant.start_turn(uuid,bytea)','12aee658b85c82c92d36a1c7ed1904e094b672ce468c5678e64802a323c7d9d0',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']),
  ('assistant.agent_version_state(uuid,integer)','ec11d091851215da124b2013a836f37785866160fac154e4f0e35918c5fe00e3',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner']),
  ('assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer)',
   '54a5ea421c86cb48b57c33f04109aa86558044311eccd0530da61b08825a0090',
   ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner'])
 )v(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(replaced.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=replaced.hash
   AND p.proacl=replaced.acl::aclitem[])
  THEN RAISE EXCEPTION 'agent_read_limit: unexpected helper %',replaced.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
ALTER TABLE assistant.agent_versions ADD COLUMN max_read_seconds integer;
-- A version saved before this one ran under runtime sets whose ToolRead bounded each read at 5 seconds
-- within its turn's deadline; that bound becomes the version's own. The immutable guard is restored in this
-- transaction while the table's exclusive lock keeps out every other read and write.
ALTER TABLE assistant.agent_versions DISABLE TRIGGER immutable_agent_version;
UPDATE assistant.agent_versions SET max_read_seconds=least(5,max_elapsed_seconds);
ALTER TABLE assistant.agent_versions ENABLE TRIGGER immutable_agent_version;
ALTER TABLE assistant.agent_versions ALTER COLUMN max_read_seconds SET NOT NULL,
 ADD CHECK(max_read_seconds BETWEEN 1 AND 30 AND max_read_seconds<=max_elapsed_seconds);

-- As in 0066, with the version's time per read.
DROP FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer);
CREATE FUNCTION assistant.save_agent_version(operation uuid,agent uuid,expected integer,description text,instructions text,model uuid,
 model_version integer,reasoning text,caching boolean,tools text[],allowed_roles text[],tool_steps integer,input_tokens bigint,output_tokens integer,
 elapsed_seconds integer,read_seconds integer,retries integer,grant_days integer,instructions_limit integer,grant_days_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.agent_actor();previous jsonb;a assistant.agents;v assistant.model_versions;before_value jsonb;after_value jsonb;
 next_version integer;
 request jsonb:=jsonb_build_object('action','save_agent','operationId',operation,'agentId',agent,'expectedVersion',expected,'description',description,'instructions',instructions,
  'modelId',model,'modelVersion',model_version,'reasoning',reasoning,'caching',caching,'tools',to_jsonb(tools),'allowedRoles',to_jsonb(allowed_roles),
  'limits',jsonb_build_object('toolSteps',tool_steps,'inputTokens',input_tokens,'outputTokens',output_tokens,'elapsedSeconds',elapsed_seconds,'readSeconds',read_seconds,'retries',retries),
  'grantDays',grant_days);
BEGIN
 previous:=assistant.agent_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 -- The runtime set's bounds apply first, so instructions beyond them are too long whatever else they hold.
 IF instructions_limit IS NULL OR grant_days_limit IS NULL THEN RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';END IF;
 IF length(instructions)>instructions_limit THEN RAISE EXCEPTION 'instructions_too_long' USING ERRCODE='P0001';END IF;
 IF grant_days>grant_days_limit THEN RAISE EXCEPTION 'grant_duration_exceeded' USING ERRCODE='P0001';END IF;
 IF description IS NULL OR NOT(length(description) BETWEEN 1 AND 500 AND assistant.is_plain_text(description)) OR instructions IS NULL OR NOT assistant.is_instructions(instructions)
  OR model IS NULL OR model='00000000-0000-0000-0000-000000000000'::uuid OR model_version IS NULL OR reasoning IS NULL OR caching IS NULL
  OR NOT assistant.are_agent_tools(tools) OR allowed_roles IS NULL OR array_position(allowed_roles,NULL) IS NOT NULL
  OR cardinality(allowed_roles)<>(SELECT count(DISTINCT r) FROM unnest(allowed_roles)r)
  OR EXISTS(SELECT 1 FROM unnest(allowed_roles)r WHERE NOT EXISTS(SELECT 1 FROM security.roles x WHERE x.code=r))
  OR tool_steps IS NULL OR tool_steps NOT BETWEEN 1 AND 100 OR input_tokens IS NULL OR input_tokens NOT BETWEEN 1 AND 100000000
  OR output_tokens IS NULL OR output_tokens NOT BETWEEN 1 AND 10000000 OR elapsed_seconds IS NULL OR elapsed_seconds NOT BETWEEN 1 AND 3600
  OR read_seconds IS NULL OR read_seconds NOT BETWEEN 1 AND 30 OR read_seconds>elapsed_seconds
  OR retries IS NULL OR retries NOT BETWEEN 0 AND 10 OR grant_days IS NULL OR grant_days<1 THEN
  RAISE EXCEPTION 'model_admin_input' USING ERRCODE='P0001';
 END IF;
 a:=assistant.agent_for_change(c,agent,expected);
 PERFORM assistant.check_agent_model(c,model,model_version);
 SELECT x.* INTO v FROM assistant.model_versions x WHERE(x.model_id,x.version)=(model,model_version);
 IF NOT reasoning=ANY(v.reasoning_efforts) THEN RAISE EXCEPTION 'parameter_required' USING ERRCODE='P0001';END IF;
 IF output_tokens>v.output_limit THEN RAISE EXCEPTION 'model_limit_exceeded' USING ERRCODE='P0001';END IF;
 before_value:=assistant.agent_state(agent);
 next_version:=coalesce((SELECT max(x.version) FROM assistant.agent_versions x WHERE x.agent_id=agent),0)+1;
 INSERT INTO assistant.agent_versions(agent_id,version,description,instructions,model_id,model_version,reasoning,caching,tools,max_tool_steps,
  max_input_tokens,max_output_tokens,max_elapsed_seconds,max_read_seconds,max_retries,grant_days,saved_by,saved_at)
  VALUES(agent,next_version,description,instructions,model,model_version,reasoning,caching,tools,
  tool_steps,input_tokens,output_tokens,elapsed_seconds,read_seconds,retries,grant_days,c.actor_id,clock_timestamp());
 INSERT INTO assistant.agent_version_roles SELECT agent,next_version,r.id FROM security.roles r WHERE r.code=ANY(allowed_roles) ORDER BY r.code;
 UPDATE assistant.agents x SET version=x.version+1 WHERE x.id=agent;
 after_value:=assistant.agent_state(agent);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at,agent_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'save_agent',request,before_value,after_value,clock_timestamp(),agent);
 PERFORM assistant.agent_actor();
 RETURN after_value;
END $$;

-- As in 0066, with the version's time per read.
CREATE OR REPLACE FUNCTION assistant.agent_version_state(agent uuid,agent_version integer) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('version',v.version,'description',v.description,'instructions',v.instructions,
  'model',jsonb_build_object('id',m.id,'version',v.model_version,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,
   'current',v.model_version=(SELECT c.version FROM assistant.current_model_version(m.id) c)),
  'reasoning',v.reasoning,'caching',v.caching,'tools',coalesce((SELECT jsonb_agg(t ORDER BY t) FROM unnest(v.tools)t),'[]'::jsonb),
  'allowedRoles',coalesce((SELECT jsonb_agg(r.code ORDER BY r.code) FROM assistant.agent_version_roles a JOIN security.roles r ON r.id=a.role_id
   WHERE(a.agent_id,a.version)=(v.agent_id,v.version)),'[]'::jsonb),
  'limits',jsonb_build_object('toolSteps',v.max_tool_steps,'inputTokens',v.max_input_tokens,'outputTokens',v.max_output_tokens,
   'elapsedSeconds',v.max_elapsed_seconds,'readSeconds',v.max_read_seconds,'retries',v.max_retries),
  'grantDays',v.grant_days,'savedBy',s.display_name,'savedAt',v.saved_at)
 FROM assistant.agent_versions v JOIN assistant.agents g ON g.id=v.agent_id JOIN assistant.models m ON m.id=v.model_id
  JOIN assistant.providers p ON p.id=m.provider_id
  JOIN security.workspace_memberships s ON(s.tenant_id,s.workspace_id,s.principal_id)=(g.tenant_id,g.workspace_id,v.saved_by)
 WHERE(v.agent_id,v.version)=(agent,agent_version);
$$;

-- As in 0072, with the version's time per read.
CREATE OR REPLACE FUNCTION assistant.start_turn(turn uuid,token bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns:=assistant.fenced_turn(turn,token);v assistant.agent_versions;
BEGIN
 IF t.recorded_at IS NULL OR t.started_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 UPDATE assistant.turns x SET state='running',started_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
 SELECT x.* INTO v FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(t.agent_id,t.agent_version);
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'started',jsonb_build_object('instructions',v.instructions,'modelId',v.model_id,
  'modelVersion',v.model_version,'reasoning',v.reasoning,'caching',v.caching,'tools',to_jsonb(v.tools),
  'limits',jsonb_build_object('toolSteps',v.max_tool_steps,'inputTokens',v.max_input_tokens,'outputTokens',v.max_output_tokens,
   'elapsedSeconds',v.max_elapsed_seconds,'readSeconds',v.max_read_seconds,'retries',v.max_retries),
  'catalogId',t.catalog_id,'pins',t.pins,'delegationId',t.delegation_id,
  'agentPrincipal',(SELECT a.principal_id FROM assistant.agents a WHERE a.id=t.agent_id),
  'earlier',coalesce((SELECT jsonb_agg(jsonb_build_object('ordinal',e.ordinal,'question',e.question,'answer',e.answer,'incomplete',e.incomplete,
     'state',e.state,'outcome',e.outcome,'context',jsonb_build_object('mode',e.knowledge_mode,'cutoff',e.cutoff,'frameId',e.frame_id)) ORDER BY e.ordinal)
   FROM assistant.turns e WHERE e.id=ANY(t.included_turns)),'[]'::jsonb)));
END $$;

ALTER FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer) TO openetrm_runtime;
COMMIT;
