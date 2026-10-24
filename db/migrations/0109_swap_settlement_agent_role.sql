-- Opt-in host-managed financial operations; existing page assistants retain
-- their reader role and read-only tool/runtime contracts. No principal or grant
-- is created here. Delegation still requires live matching agent/delegator rights.
BEGIN;
INSERT INTO security.roles(id,code,name) VALUES('a0000000-0000-0000-0000-000000000008','swap_settlement_operator','Swap settlement operator');
INSERT INTO security.role_permissions VALUES
 ('a0000000-0000-0000-0000-000000000008','ReadTrades'),
 ('a0000000-0000-0000-0000-000000000008','ReadMarketData'),
 ('a0000000-0000-0000-0000-000000000008','AmendTrade');
CREATE OR REPLACE FUNCTION security.guard_agent_roles() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT NEW.revoked AND(NEW.role_id IN('a0000000-0000-0000-0000-000000000007','a0000000-0000-0000-0000-000000000008'))
  IS DISTINCT FROM EXISTS(SELECT 1 FROM security.principals p WHERE p.id=NEW.principal_id AND p.kind='agent')
 THEN RAISE EXCEPTION 'agent_managed' USING ERRCODE='23514';END IF;
 RETURN NEW;
END$$;
-- Preserve the existing administration implementation and its audit/replay
-- contract while keeping both agent-only roles out of human role assignment.
DO $$DECLARE source text;changed text;BEGIN
 SELECT pg_get_functiondef('security.administration_directory()'::regprocedure) INTO source;
 changed=replace(source,'r.id<>''a0000000-0000-0000-0000-000000000007''',
  'r.id NOT IN(''a0000000-0000-0000-0000-000000000007'',''a0000000-0000-0000-0000-000000000008'')');
 IF changed=source THEN RAISE EXCEPTION 'Unexpected administration directory contract';END IF;EXECUTE changed;
 SELECT pg_get_functiondef('security.apply_administration(jsonb)'::regprocedure) INTO source;
 changed=replace(source,'''agent_reader''=ANY(coalesce(requested_roles,ARRAY[]::text[]))',
  'coalesce(requested_roles,ARRAY[]::text[]) && ARRAY[''agent_reader'',''swap_settlement_operator'']::text[]');
 IF changed=source THEN RAISE EXCEPTION 'Unexpected administration role contract';END IF;EXECUTE changed;
END$$;
COMMIT;
