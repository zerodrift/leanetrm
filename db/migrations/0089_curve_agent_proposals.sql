-- Curve preparation is a bounded read. It grants no authoring or risk authority.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Fail closed if an installed authority helper differs from the reviewed contract.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.agent_tools()','2bbf2767ce1432bcf18cd7f657d2c613bea2a4e274992a7fc1c5385ebd2fdbd0'),
  ('assistant.is_navigation_target(assistant.turns,jsonb)','65b92ac539c7d3bfbe13edd9bdd3491800010e2e17ec6d5fc1ff1ea5dd6bc793'),
  ('assistant.grant_actor(boolean)','4aaf7c789d860df03d222e891b74008dafb6aae71d01a77b17219c35621a7503'),
  ('assistant.sources_readable(uuid)','c77f51e6257b2af498f7618396e971ffc0b4aa233cdbfc708066f50d541b0379'),
  ('assistant.turn_state(assistant.turns)','c72f829f3c6541582b6a5d428d7a7a3dcbb002096807e7e4161d4b683c2b174e')
 )v(signature,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl=ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[])
  THEN RAISE EXCEPTION 'curve_proposals: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
CREATE OR REPLACE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades']),
  ('read_custom_curves',ARRAY['ReadMarketData']),('prepare_custom_curve',ARRAY['ReadMarketData'])
$$;
-- Retain the existing target validation and add a typed proposal reference to a
-- successful preparation step of this same turn, never a model-supplied graph.
DO $upgrade$
DECLARE definition text;before text;after text;
BEGIN
 definition:=pg_get_functiondef('assistant.is_navigation_target(assistant.turns,jsonb)'::regprocedure);
 before:='''curves'',''contract_definitions'',''schedules'',''power_tous'',''reference_data'',''parties'',''blotter''';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'curve_proposals: unexpected target pages' USING ERRCODE='55000';END IF;
 definition:=replace(definition,before,before||',''custom_curves''');
 before:='WHEN ''filter'' THEN jsonb_typeof(target->''selection'')=''object'' AND octet_length((target->''selection'')::text)<=8192';
 after:=before||E'\n    WHEN ''proposal'' THEN target->>''page''=''custom_curves'' AND jsonb_typeof(target->''selection'')=''object''\n'
  ||E'     AND (SELECT array_agg(k ORDER BY k COLLATE "C") FROM jsonb_object_keys(target->''selection'') k)=ARRAY[''step'',''turnId'']\n'
  ||E'     AND target->''selection''->>''turnId''=t.id::text AND EXISTS(SELECT 1 FROM assistant.tool_steps s\n'
  ||E'       WHERE s.turn_id=t.id AND to_jsonb(s.ordinal)=target->''selection''->''step'' AND s.tool=''prepare_custom_curve''\n'
  ||E'        AND s.operation=''custom_curves.prepare_v1'' AND s.code IS NULL)';
 IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'curve_proposals: unexpected target selection contract' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before,after);
END $upgrade$;

CREATE FUNCTION assistant.curve_proposal(scope text,turn uuid,step integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);t assistant.turns;arguments jsonb;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') OR step IS NULL OR step<1 THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO t FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
  WHERE x.id=curve_proposal.turn AND(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,curve_proposal.scope);
 IF t.id IS NULL THEN RAISE EXCEPTION 'turn_not_found';END IF;
 IF NOT assistant.sources_readable(t.id) THEN RAISE EXCEPTION 'The proposal sources are no longer readable' USING ERRCODE='42501';END IF;
 SELECT s.input INTO arguments FROM assistant.tool_steps s WHERE(s.turn_id,s.ordinal,s.tool,s.operation)=
  (t.id,curve_proposal.step,'prepare_custom_curve','custom_curves.prepare_v1') AND s.code IS NULL;
 IF arguments IS NULL OR arguments->>'engine' IS DISTINCT FROM 'custom_curve_recipe_v2' OR t.pins IS NULL
 THEN RAISE EXCEPTION 'turn_input';END IF;
 RETURN jsonb_build_object('input',arguments,'context',assistant.turn_state(t)->'context','pins',t.pins);
END$$;
ALTER FUNCTION assistant.curve_proposal(text,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.curve_proposal(text,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION assistant.curve_proposal(text,uuid,integer) TO openetrm_runtime;
COMMIT;
