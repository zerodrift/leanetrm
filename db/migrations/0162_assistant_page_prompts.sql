-- Page instructions are immutable, workspace-scoped configuration. Only an explicitly privileged human saves
-- them; every turn pins the exact version admitted at ask, independently of later edits.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
INSERT INTO security.permissions VALUES('ManageAssistantPrompts');
INSERT INTO security.roles(id,code,name) VALUES('a0000000-0000-0000-0000-000000000009','assistant_prompt_editor','Assistant prompt editor');
INSERT INTO security.role_permissions SELECT id,'ManageAssistantPrompts' FROM security.roles
 WHERE code IN('access_administrator','assistant_prompt_editor');

CREATE FUNCTION assistant.is_prompt_page(page text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT page IN('curves','contract_definitions','schedules','power_tous','reference_data','parties','blotter','custom_curves','jobs','ppa',
  'blotter_risk','blotter_risk_lens','blotter_new_trade','blotter_new_future','blotter_new_swap','risk_jobs','delivery_prices',
  'interval_series','interval_curves','users_access','settings_ai','blotter_new_ppa','blotter_new_gas','blotter_new_crude','gas_setup','physical_gas','physical_crude');
$$;
CREATE TABLE assistant.page_prompt_versions(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,page text NOT NULL CHECK(assistant.is_prompt_page(page)),
 version integer NOT NULL CHECK(version>0),instructions text NOT NULL CHECK(length(instructions)<=6000 AND position(chr(1) in instructions)=0),
 actor_id uuid NOT NULL REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,page,version),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TABLE assistant.page_prompt_commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 page text NOT NULL,expected_version integer NOT NULL,instructions text NOT NULL,version integer NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,actor_id,request_id),
 FOREIGN KEY(tenant_id,workspace_id,page,version) REFERENCES assistant.page_prompt_versions
);
CREATE TABLE assistant.turn_prompt_pins(
 turn_id uuid PRIMARY KEY REFERENCES assistant.turns(id),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,page text NOT NULL,
 version integer CHECK(version>0),
 FOREIGN KEY(tenant_id,workspace_id,page,version) REFERENCES assistant.page_prompt_versions
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['page_prompt_versions','page_prompt_commands','turn_prompt_pins'] LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON assistant.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON assistant.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION assistant.pin_page_prompt() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT assistant.is_prompt_page(NEW.page_report->>'page') THEN RAISE EXCEPTION 'page_report_invalid';END IF;
 INSERT INTO assistant.turn_prompt_pins SELECT NEW.id,NEW.tenant_id,NEW.workspace_id,NEW.page_report->>'page',
  (SELECT max(v.version) FROM assistant.page_prompt_versions v
   WHERE(v.tenant_id,v.workspace_id,v.page)=(NEW.tenant_id,NEW.workspace_id,NEW.page_report->>'page'));
 RETURN NEW;
END$$;
CREATE TRIGGER page_prompt AFTER INSERT ON assistant.turns FOR EACH ROW EXECUTE FUNCTION assistant.pin_page_prompt();

CREATE FUNCTION assistant.page_prompt_directory() RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);result jsonb;
BEGIN
 IF NOT security.has_permission('ManageAssistantPrompts') THEN RAISE EXCEPTION 'No prompt administration permission' USING ERRCODE='42501';END IF;
 SELECT jsonb_build_object('maximumLength',6000,'prompts',coalesce(jsonb_agg(to_jsonb(v)-'tenant_id'-'workspace_id' ORDER BY page,version DESC),'[]'::jsonb))
 INTO result FROM assistant.page_prompt_versions v WHERE(v.tenant_id,v.workspace_id)=(c.tenant_id,c.workspace_id);
 RETURN result;
END$$;
CREATE FUNCTION assistant.save_page_prompt(request uuid,page text,expected integer,instructions text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);previous assistant.page_prompt_commands;head assistant.page_prompt_versions;next integer;
BEGIN
 IF NOT security.has_permission('ManageAssistantPrompts') THEN RAISE EXCEPTION 'No prompt administration permission' USING ERRCODE='42501';END IF;
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR NOT coalesce(assistant.is_prompt_page(page),false)
  OR expected IS NULL OR expected<0 OR instructions IS NULL OR length(instructions)>6000 THEN RAISE EXCEPTION 'page_prompt_input';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(c.workspace_id::text||':assistant_page_prompt:'||page,0));
 IF NOT security.has_permission('ManageAssistantPrompts') THEN RAISE EXCEPTION 'Prompt administration permission expired' USING ERRCODE='42501';END IF;
 SELECT * INTO previous FROM assistant.page_prompt_commands x
  WHERE(x.tenant_id,x.workspace_id,x.actor_id,x.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,request);
 IF FOUND THEN
  IF(previous.page,previous.expected_version,previous.instructions) IS DISTINCT FROM(page,expected,instructions) THEN RAISE EXCEPTION 'page_prompt_conflict';END IF;
  RETURN jsonb_build_object('page',page,'version',previous.version);
 END IF;
 SELECT * INTO head FROM assistant.page_prompt_versions v WHERE(v.tenant_id,v.workspace_id,v.page)=(c.tenant_id,c.workspace_id,save_page_prompt.page) ORDER BY version DESC LIMIT 1;
 IF coalesce(head.version,0)<>expected THEN RAISE EXCEPTION 'page_prompt_version_conflict';END IF;
 IF head.version IS NOT NULL AND head.instructions=instructions THEN next:=head.version;
 ELSE
  next:=coalesce(head.version,0)+1;
  INSERT INTO assistant.page_prompt_versions VALUES(c.tenant_id,c.workspace_id,page,next,instructions,c.actor_id,clock_timestamp());
 END IF;
 INSERT INTO assistant.page_prompt_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,request,page,expected,instructions,next);
 IF NOT security.has_permission('ManageAssistantPrompts') THEN RAISE EXCEPTION 'Prompt administration permission expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('page',page,'version',next);
END$$;

-- Keep the qualified start behavior intact and add the ask-time prompt pin to its worker-only answer.
ALTER FUNCTION assistant.start_turn(uuid,bytea) RENAME TO start_turn_without_page_prompt;
REVOKE ALL ON FUNCTION assistant.start_turn_without_page_prompt(uuid,bytea) FROM openetrm_runtime;
CREATE FUNCTION assistant.start_turn(turn uuid,token bytea) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb:=assistant.start_turn_without_page_prompt(turn,token);pin assistant.turn_prompt_pins;instructions text;
BEGIN
 IF result ? 'started' THEN
  SELECT * INTO pin FROM assistant.turn_prompt_pins p WHERE p.turn_id=turn;
  SELECT v.instructions INTO instructions FROM assistant.page_prompt_versions v
   WHERE(v.tenant_id,v.workspace_id,v.page,v.version)=(pin.tenant_id,pin.workspace_id,pin.page,pin.version);
  result:=jsonb_set(result,'{started}',(result->'started')||jsonb_build_object('pagePrompt',coalesce(instructions,''),'pagePromptVersion',coalesce(pin.version,0)));
 END IF;
 RETURN result;
END$$;
DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['assistant.is_prompt_page(text)','assistant.pin_page_prompt()','assistant.page_prompt_directory()',
  'assistant.save_page_prompt(uuid,text,integer,text)','assistant.start_turn(uuid,bytea)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION assistant.page_prompt_directory(),assistant.save_page_prompt(uuid,text,integer,text),assistant.start_turn(uuid,bytea) TO openetrm_runtime;
COMMIT;
