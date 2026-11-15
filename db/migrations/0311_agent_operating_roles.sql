-- Agent versions explicitly select a qualified additional operating role.
-- The base reader and delegator allowed-role contracts remain separate.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

DO $upgrade$
DECLARE spec record;p record;BEGIN
 IF to_regprocedure('assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer,text)') IS NOT NULL
 THEN RAISE EXCEPTION 'agent_operating_role_upgrade: existing operating-role entry point';END IF;
 FOR spec IN SELECT * FROM(VALUES
  ('assistant.agent_directory()','4758e36eaf310111d4443ad1d4d1fbcb','plpgsql','jsonb',true,'v',ARRAY['search_path=pg_catalog','TimeZone=UTC'],NULL::text[],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'),
  ('assistant.agent_version_state(uuid,integer)','037c8c060280dc75e4d3a773b87f8104','sql','jsonb',false,'s',ARRAY['search_path=pg_catalog'],ARRAY['agent','agent_version'],
   '{openetrm_security_owner=X/openetrm_security_owner}'),
  ('assistant.version_permissions(uuid,integer)','319e640a57f7acf4b18ee746b4ffc72d','sql','text[]',false,'s',ARRAY['search_path=pg_catalog'],ARRAY['agent','agent_version'],
   '{openetrm_security_owner=X/openetrm_security_owner}'),
  ('assistant.publish_agent(uuid,uuid,integer)','a8eed9c324c9c91e6f340fd082340925','plpgsql','jsonb',true,'v',ARRAY['search_path=pg_catalog','TimeZone=UTC'],ARRAY['operation','agent','expected'],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'),
  ('assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer)',
   '03e1c80a294e8f10f43380bc0836624e','plpgsql','jsonb',true,'v',ARRAY['search_path=pg_catalog','TimeZone=UTC'],
   ARRAY['operation','agent','expected','description','instructions','model','model_version','reasoning','caching','tools','allowed_roles','tool_steps','input_tokens',
    'output_tokens','elapsed_seconds','read_seconds','retries','history_turns','grant_days','instructions_limit','grant_days_limit'],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}')
 )v(signature,source_hash,language,result_type,definer,volatility,config,arg_names,acl) LOOP
  SELECT f.*,l.lanname INTO p FROM pg_proc f JOIN pg_language l ON l.oid=f.prolang WHERE f.oid=to_regprocedure(spec.signature);
  IF NOT FOUND OR md5(p.prosrc) IS DISTINCT FROM spec.source_hash OR p.lanname<>spec.language
   OR p.proowner<>'openetrm_security_owner'::regrole OR p.prosecdef IS DISTINCT FROM spec.definer
   OR p.prorettype<>spec.result_type::regtype OR p.prokind<>'f' OR p.proretset OR p.proisstrict OR p.proleakproof
   OR p.provolatile::text<>spec.volatility OR p.proparallel<>'u' OR p.proconfig IS DISTINCT FROM spec.config
   OR p.proargnames IS DISTINCT FROM spec.arg_names OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.provariadic<>0
   OR p.pronargs<>coalesce(cardinality(spec.arg_names),0) OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
   OR p.probin IS NOT NULL OR p.prosqlbody IS NOT NULL OR p.procost<>100 OR p.prorows<>0 OR p.prosupport<>0 OR p.protrftypes IS NOT NULL
   OR p.proacl IS DISTINCT FROM spec.acl::aclitem[]
  THEN RAISE EXCEPTION 'agent_operating_role_upgrade: %',spec.signature;END IF;
 END LOOP;
END$upgrade$;

-- NULL records the reader-only contract of the retained legacy save signature.
ALTER TABLE assistant.agent_versions ADD COLUMN operating_role_id uuid REFERENCES security.roles(id),
 ADD CONSTRAINT agent_operating_role CHECK(operating_role_id IS NULL OR operating_role_id='a0000000-0000-0000-0000-000000000010');

CREATE OR REPLACE FUNCTION assistant.version_permissions(agent uuid,agent_version integer) RETURNS text[]
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(array_agg(DISTINCT needed.permission COLLATE "C" ORDER BY needed.permission COLLATE "C"),ARRAY[]::text[])
 FROM assistant.agent_versions v CROSS JOIN LATERAL(
  SELECT p permission FROM unnest(v.tools)t JOIN assistant.agent_tools()a ON a.code=t CROSS JOIN LATERAL unnest(a.permissions)p
  UNION ALL SELECT p FROM unnest(ARRAY['ReadGasOwnedHoldings','ReadMarketData','ReadTrades','RecordGasOwnedHoldings'])p
   WHERE v.operating_role_id='a0000000-0000-0000-0000-000000000010'
 )needed WHERE(v.agent_id,v.version)=(version_permissions.agent,version_permissions.agent_version);
$$;

DO $extend$
DECLARE signature text;definition text;needle text;replacement text;BEGIN
 signature='assistant.agent_version_state(uuid,integer)';definition=pg_get_functiondef(signature::regprocedure);
 needle='''reasoning'',v.reasoning';
 replacement='''operatingRole'',CASE WHEN v.operating_role_id IS NULL THEN ''none'' ELSE (SELECT r.code FROM security.roles r WHERE r.id=v.operating_role_id) END,'||needle;
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'agent_operating_role_upgrade: %',signature;END IF;
 EXECUTE replace(definition,needle,replacement);
 signature='assistant.agent_directory()';definition=pg_get_functiondef(signature::regprocedure);
 needle='''roles'',(SELECT jsonb_agg';
 replacement=$catalog$'operatingRoles',jsonb_build_array(
   jsonb_build_object('code','none','name','No additional operating role','permissions','[]'::jsonb),
   (SELECT jsonb_build_object('code',r.code,'name',r.name,'permissions',
     (SELECT jsonb_agg(rp.permission ORDER BY rp.permission) FROM security.role_permissions rp WHERE rp.role_id=r.id))
    FROM security.roles r WHERE r.id='a0000000-0000-0000-0000-000000000010')),
  $catalog$||needle;
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'agent_operating_role_upgrade: %',signature;END IF;
 EXECUTE replace(definition,needle,replacement);
 -- The original 21-argument entry point retains its exact reader-only request/replay contract.
 signature='assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer)';
 definition=pg_get_functiondef(signature::regprocedure);
 FOR needle,replacement IN SELECT * FROM(VALUES
  ('grant_days_limit integer)','grant_days_limit integer, operating_role text)'),
  ('''grantDays'',grant_days);','''grantDays'',grant_days,''operatingRole'',operating_role);'),
  ('a:=assistant.agent_for_change(c,agent,expected);',
   'IF operating_role IS NULL OR operating_role NOT IN(''none'',''gas_lifecycle_operator'') THEN RAISE EXCEPTION ''model_admin_input'' USING ERRCODE=''P0001'';END IF;'||E'\n '||
   'a:=assistant.agent_for_change(c,agent,expected);'),
  ('max_input_tokens,max_output_tokens,max_elapsed_seconds,max_read_seconds,max_retries,history_turns,grant_days,saved_by,saved_at)',
   'max_input_tokens,max_output_tokens,max_elapsed_seconds,max_read_seconds,max_retries,history_turns,grant_days,saved_by,saved_at,operating_role_id)'),
  ('tool_steps,input_tokens,output_tokens,elapsed_seconds,read_seconds,retries,history_turns,grant_days,c.actor_id,clock_timestamp());',
   'tool_steps,input_tokens,output_tokens,elapsed_seconds,read_seconds,retries,history_turns,grant_days,c.actor_id,clock_timestamp(),'||
   'CASE WHEN operating_role=''gas_lifecycle_operator'' THEN ''a0000000-0000-0000-0000-000000000010''::uuid ELSE NULL END);')
 )patch(old_value,new_value) LOOP
  IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'agent_operating_role_upgrade: %',signature;END IF;
  definition=replace(definition,needle,replacement);
 END LOOP;
 EXECUTE definition;
END$extend$;

CREATE FUNCTION assistant.apply_operating_role(c security.request_contexts,a assistant.agents,newest assistant.agent_versions,operation uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE prior_role uuid;active_gas integer;at timestamptz;before_value jsonb;BEGIN
 IF(c.tenant_id,c.workspace_id) IS DISTINCT FROM(a.tenant_id,a.workspace_id) OR newest.agent_id IS DISTINCT FROM a.id
  OR EXISTS(SELECT 1 FROM security.role_grants g WHERE(g.principal_id,g.tenant_id,g.workspace_id)=(a.principal_id,c.tenant_id,c.workspace_id)
   AND NOT g.revoked AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to
   AND g.role_id NOT IN('a0000000-0000-0000-0000-000000000007','a0000000-0000-0000-0000-000000000010'))
 THEN RAISE EXCEPTION 'agent_operating_role_conflict' USING ERRCODE='P0001';END IF;
 SELECT v.operating_role_id INTO prior_role FROM assistant.agent_versions v WHERE(v.agent_id,v.version)=(a.id,a.published_version);
 SELECT count(*) INTO active_gas FROM security.role_grants g
  WHERE(g.principal_id,g.tenant_id,g.workspace_id,g.role_id)=(a.principal_id,c.tenant_id,c.workspace_id,'a0000000-0000-0000-0000-000000000010')
   AND NOT g.revoked AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to;
 IF prior_role IS NOT DISTINCT FROM newest.operating_role_id
  AND active_gas=(CASE WHEN newest.operating_role_id IS NULL THEN 0 ELSE 1 END) THEN RETURN;END IF;
 IF NOT c.administration THEN RAISE EXCEPTION 'admission_required' USING ERRCODE='P0001';END IF;
 PERFORM assistant.agent_actor();
 at=clock_timestamp();before_value=security.member_description(c.tenant_id,c.workspace_id,a.principal_id);
 INSERT INTO assistant.grant_audit SELECT c.tenant_id,c.workspace_id,operation,g.delegation_id,'replaced',g.delegator_id,g.agent_id,g.agent_version,
   g.model_id,g.model_version,g.permissions,g.expires_at,c.actor_id,at
  FROM security.agent_delegations d JOIN assistant.grant_audit g ON g.delegation_id=d.id AND g.outcome='granted'
  WHERE(d.tenant_id,d.workspace_id,d.agent_id)=(c.tenant_id,c.workspace_id,a.principal_id) AND NOT d.revoked;
 UPDATE security.agent_delegations d SET revoked=true
  WHERE(d.tenant_id,d.workspace_id,d.agent_id)=(c.tenant_id,c.workspace_id,a.principal_id) AND NOT d.revoked;
 UPDATE security.role_grants g SET revoked=true
  WHERE(g.principal_id,g.tenant_id,g.workspace_id,g.role_id)=(a.principal_id,c.tenant_id,c.workspace_id,'a0000000-0000-0000-0000-000000000010') AND NOT g.revoked;
 IF newest.operating_role_id IS NOT NULL THEN
  INSERT INTO security.role_grants VALUES(gen_random_uuid(),a.principal_id,newest.operating_role_id,c.tenant_id,c.workspace_id,at,TIMESTAMPTZ '9999-12-31 00:00:00+00',false);
 END IF;
 INSERT INTO security.administration_audit VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,a.principal_id,at,
  jsonb_build_object('action','publish_agent_operating_role','operationId',operation,'agentId',a.id,'version',newest.version),
  before_value,security.member_description(c.tenant_id,c.workspace_id,a.principal_id));
 PERFORM assistant.agent_actor();
END$$;

DO $publish$
DECLARE definition text;needle text;BEGIN
 definition=pg_get_functiondef('assistant.publish_agent(uuid,uuid,integer)'::regprocedure);
 needle='UPDATE assistant.agents x SET version=x.version+1,published_version=newest.version WHERE x.id=agent;';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'agent_operating_role_upgrade: publish';END IF;
 EXECUTE replace(definition,needle,'PERFORM assistant.apply_operating_role(c,a,newest,operation);'||E'\n '||needle);
END$publish$;

ALTER FUNCTION assistant.apply_operating_role(security.request_contexts,assistant.agents,assistant.agent_versions,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.apply_operating_role(security.request_contexts,assistant.agents,assistant.agent_versions,uuid) FROM PUBLIC;
ALTER FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.save_agent_version(uuid,uuid,integer,text,text,uuid,integer,text,boolean,text[],text[],integer,bigint,integer,integer,integer,integer,integer,integer,integer,integer,text) TO openetrm_runtime;
COMMIT;
