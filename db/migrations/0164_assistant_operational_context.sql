-- Account and operational assistance does not require financial data access or fabricate a market snapshot.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN unnest(ARRAY['UseAgents','CreateDelegations']) p
 WHERE code='access_administrator' ON CONFLICT DO NOTHING;
CREATE OR REPLACE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades']),
  ('read_custom_curves',ARRAY['ReadMarketData']),('prepare_custom_curve',ARRAY['ReadMarketData']),('read_jobs',ARRAY['ReadJobs']),
  ('save_custom_curve_draft',ARRAY['ReadMarketData','WriteMarketDefinitions']),
  ('read_workspace_page',ARRAY['UseAssistantWorkspace']),('discover_page_operations',ARRAY['UseAssistantWorkspace']),
  ('read_page_operation',ARRAY['UseAssistantWorkspace']),('propose_page_action',ARRAY['UseAssistantWorkspace'])
$$;
ALTER TABLE assistant.turns DROP CONSTRAINT turns_knowledge_mode_check,
 ADD CONSTRAINT turns_knowledge_mode_check CHECK(knowledge_mode IN('current','retained','operational')),
 ADD CONSTRAINT turns_operational_context CHECK(knowledge_mode<>'operational' OR frame_id IS NULL AND requested_context IS NULL AND context_id IS NULL AND pins IS NULL
  AND page_report->>'page' IN('users_access','settings_ai','jobs','risk_jobs','delivery_prices','interval_series'));
DO $upgrade$
DECLARE c record;definition text;before text;signature text;found_pin boolean:=false;found_start boolean:=false;
BEGIN
 FOR c IN SELECT conname,pg_get_constraintdef(oid) definition FROM pg_constraint WHERE conrelid='assistant.turns'::regclass AND contype='c' LOOP
  IF c.definition LIKE '%num_nulls(context_id, cutoff, recorded_at)%' THEN
   EXECUTE format('ALTER TABLE assistant.turns DROP CONSTRAINT %I,ADD CONSTRAINT %I CHECK((knowledge_mode=''operational'' AND context_id IS NULL AND cutoff IS NOT NULL AND recorded_at IS NOT NULL AND pins IS NULL) OR %s)',
    c.conname,c.conname,substring(c.definition FROM 7));found_pin:=true;
  ELSIF c.definition LIKE '%context_id IS NOT NULL%' AND c.definition LIKE '%started_at IS NOT NULL%' THEN
   definition:=replace(c.definition,'context_id IS NOT NULL','(context_id IS NOT NULL OR knowledge_mode=''operational'')');
   EXECUTE format('ALTER TABLE assistant.turns DROP CONSTRAINT %I,ADD CONSTRAINT %I %s',c.conname,c.conname,definition);found_start:=true;
  END IF;
 END LOOP;
 IF NOT found_pin OR NOT found_start THEN RAISE EXCEPTION 'operational_context: unexpected turn constraints';END IF;
 signature:='assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer)';
 definition:=pg_get_functiondef(signature::regprocedure);before:='knowledge_mode NOT IN(''current'',''retained'')';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'operational_context: unexpected ask';END IF;
 EXECUTE replace(definition,before,'knowledge_mode NOT IN(''current'',''retained'',''operational'')');
 signature:='assistant.action_proposal(text,uuid,integer)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='t.knowledge_mode<>''current''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'operational_context: unexpected proposal';END IF;
 EXECUTE replace(definition,before,'t.knowledge_mode NOT IN(''current'',''operational'')');
 -- Catalog relationships contribute sources only when the actor has actually been granted market access.
 signature:='assistant.workspace_sources()';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHERE(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id)';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>2 THEN RAISE EXCEPTION 'operational_context: unexpected catalog sources';END IF;
 EXECUTE replace(definition,before,before||' AND security.has_permission(''ReadMarketData'')');
END $upgrade$;

-- Generic reads are explicitly delegated as UseAssistantWorkspace and then use the human's normal read operation.
-- Their catalog source predicates must recheck that same human authority, rather than require a legacy read-tool grant.
ALTER TABLE assistant.tool_read_access DROP CONSTRAINT tool_read_access_check,ADD CONSTRAINT tool_read_access_check CHECK(CASE access
 WHEN 'market_access' THEN permission='ReadMarketData' AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
 WHEN 'reference_access' THEN permission='ReadMarketData' AND market_catalog_id IS NULL AND reference_catalog_id IS NOT NULL
 WHEN 'workspace_market_access' THEN permission='ReadMarketData' AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_reference_access' THEN permission='ReadMarketData' AND market_catalog_id IS NULL AND reference_catalog_id IS NOT NULL
 WHEN 'trading_access' THEN permission IN('ReadTrades','ReadTradeParties') AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
 WHEN 'job_access' THEN permission='ReadJobs' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_access' THEN market_catalog_id IS NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_installation_jobs' THEN permission='UseAssistantWorkspace' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
 WHEN 'workspace_installation_infrastructure' THEN permission='UseAssistantWorkspace' AND market_catalog_id IS NULL AND reference_catalog_id IS NULL
 ELSE false END);
DO $$DECLARE definition text;before text;signature text;BEGIN
 signature:='assistant.workspace_sources()';definition:=pg_get_functiondef(signature::regprocedure);
 definition:=replace(definition,'''market_access'',''ReadMarketData''','''workspace_market_access'',''ReadMarketData''');
 definition:=replace(definition,'''reference_access'',''ReadMarketData''','''workspace_reference_access'',''ReadMarketData''');EXECUTE definition;
 signature:='assistant.agent_access(assistant.turns,text,text,uuid,uuid)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHEN ''workspace_access'' THEN';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'operational_context: unexpected workspace access';END IF;
 EXECUTE replace(definition,before,
  'WHEN ''workspace_market_access'' THEN assistant.agent_permission(t,''UseAssistantWorkspace'') AND security.principal_has_permission(t.owner_id,t.tenant_id,t.workspace_id,''ReadMarketData'',clock_timestamp())'
  ||' AND EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE s.catalog_id=market_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))'
  ||E'\n  WHEN ''workspace_reference_access'' THEN assistant.agent_permission(t,''UseAssistantWorkspace'') AND security.principal_has_permission(t.owner_id,t.tenant_id,t.workspace_id,''ReadMarketData'',clock_timestamp())'
  ||' AND EXISTS(SELECT 1 FROM security.reference_catalog_scopes s WHERE s.catalog_id=reference_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))'
  ||E'\n  '||before);
 signature:='assistant.is_read_access(jsonb)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHEN ''market_access'' THEN';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'operational_context: unexpected source shape';END IF;
 EXECUTE replace(definition,before,
  'WHEN ''workspace_market_access'' THEN a->>''permission''=''ReadMarketData'' AND EXISTS(SELECT 1 FROM market_data.catalogs c WHERE c.id=(a->>''catalogId'')::uuid)'
  ||E'\n   WHEN ''workspace_reference_access'' THEN a->>''permission''=''ReadMarketData'' AND EXISTS(SELECT 1 FROM ref_data.catalogs c WHERE c.id=(a->>''catalogId'')::uuid)'
  ||E'\n   '||before);
 signature:='assistant.sources_readable(uuid)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='WHEN ''workspace_access'' THEN';
 EXECUTE replace(definition,before,'WHEN ''workspace_market_access'' THEN security.market_access(s.market_catalog_id,s.permission)'
  ||E'\n  WHEN ''workspace_reference_access'' THEN security.reference_access(s.reference_catalog_id,s.permission)'||E'\n  '||before);
 signature:='assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb,jsonb)';definition:=pg_get_functiondef(signature::regprocedure);
 before:='a->>''access''<>''reference_access''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'operational_context: unexpected market source insert';END IF;
 definition:=replace(definition,before,'a->>''access'' NOT IN(''reference_access'',''workspace_reference_access'')');
 before:='a->>''access''=''reference_access''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'operational_context: unexpected reference source insert';END IF;
 EXECUTE replace(definition,before,'a->>''access'' IN(''reference_access'',''workspace_reference_access'')');
END$$;

ALTER FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb,integer) RENAME TO pin_market_turn;
REVOKE ALL ON FUNCTION assistant.pin_market_turn(uuid,bytea,date,jsonb,integer) FROM openetrm_runtime;
DO $$DECLARE definition text;BEGIN
 definition:=pg_get_functiondef('assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)'::regprocedure);
 IF position('pin_turn.' in definition)=0 THEN RAISE EXCEPTION 'operational_context: unexpected market pin parameters';END IF;
 EXECUTE replace(definition,'pin_turn.','pin_market_turn.');
END$$;
CREATE FUNCTION assistant.pin_turn(turn uuid,token bytea,effective_date date,pins jsonb,reserve integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t assistant.turns:=assistant.fenced_turn(turn,token);c security.request_contexts:=assistant.grant_actor(true);
BEGIN
 IF t.knowledge_mode<>'operational' THEN RETURN assistant.pin_market_turn(turn,token,effective_date,pins,reserve);END IF;
 IF(t.owner_id,t.tenant_id,t.workspace_id)<>(c.actor_id,c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'No turn ownership' USING ERRCODE='42501';END IF;
 IF t.recorded_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') OR pins IS NOT NULL OR effective_date IS NOT NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 UPDATE assistant.turns x SET cutoff=t.asked_at,recorded_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
 RETURN jsonb_build_object('turn',assistant.turn_state(t));
END$$;
ALTER FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.pin_turn(uuid,bytea,date,jsonb,integer) TO openetrm_runtime;
COMMIT;
