-- Page-assistant tool steps (docs/ai-assistant.md §7.1–§7.3, §7.5, §7.6, §8). A call that ends requesting tools is
-- answered by one step per request, in order, before the tool loop's next call opens, and each read a step ran joins
-- the turn's source set. The source-access check evaluates that set in the agent's authority before every call and
-- publication; the replay recheck evaluates it in the owner's own context before stored content is sent again, and
-- decides which earlier turns an ask includes. A call's close records how it stopped, so a turn completes only once its
-- last call ended the turn, and the pinned-context record keeps an As of frame's exact pins. Step results are not
-- stored until cards read them.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority, access, capture and turn helpers tool steps rely on and the definitions this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',true),
  ('security.has_pre_pilot_permission(text)','ba2b9b5fbaaf8eadea6a60a149bc5b04d63774721cb8b788911cd29bd41cb436',false),
  ('security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)','43e35b62e3ad9ea86286250b208edf56f3dd7ddce12801f4288d75b98d7506bb',false),
  ('security.market_access(uuid,text)','804fc62e6057b1b2d528e3493d761aa52d613a766a9087c90fcd6313b5832383',true),
  ('security.reference_access(uuid,text)','697d1e75ddba8a9a7fbdfa948951d3c6760e445f6f681ba307c1c937568c0828',true),
  ('security.trading_access(uuid,text)','422b9a2343e622a55ea85cdc707c17762b3be087228d5a653a895875639bb5cb',true),
  ('security.capture_date_context(uuid,date,timestamp with time zone)','9c03d5f9761ebc717308683a74a5eb57391750daed9c336585e6837998777540',true),
  ('assistant.check_model_call_outcome()','06df5785d70e862d57110eb727f79123a6acc4f00cfacbaafe6b4164c461f6da',false),
  ('assistant.close_test(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text)','320963191f2e8cf957b6fdabfdf100440326a84aac69eda4868f884de50eecaf',true),
  ('assistant.grant_actor(boolean)','4aaf7c789d860df03d222e891b74008dafb6aae71d01a77b17219c35621a7503',false),
  ('assistant.is_instructions(text)','9637ce303efb18669266c94083fb9cfb66810143e47f15ca12b66e2c698ca59e',false),
  ('assistant.is_display_name(text)','0fa2f8b4c9a0999d2098f85f44b63ea90a29d615855648f79d8361f11dd90952',false),
  ('assistant.are_agent_tools(text[])','f2cc8a152599d7af31757be2dfce216bb6269f84fa4039f9718db7e2ba16b883',false),
  ('assistant.holds_worker_lock(integer)','b38b1bead5ffe6cd25cb6311d8f335b2d09f0be46bd505beea9584aca7f9da13',false),
  ('assistant.price_in_force(uuid,timestamp with time zone)','c1ab2055ec2a37b760c0fb8e15ef9d47ff444eddeac4ced4ad7fe11b28bf391b',false),
  ('assistant.current_attestation(uuid)','142978f0af07358286fbd725af2e4b8e073a04143401f30cef8db1452e78a51c',false),
  ('assistant.token_cost_v1(assistant.price_revisions,bigint,bigint,bigint,bigint)','f12a32ec82dbfa8b328ad456bd0564a6b93e6b8a0e1f57980948c3f43d36c2da',false),
  ('assistant.turn_state(assistant.turns)','c72f829f3c6541582b6a5d428d7a7a3dcbb002096807e7e4161d4b683c2b174e',false),
  ('assistant.turn_event(assistant.turns,text,jsonb)','2eaeaf1f25ffb0df425e16de67dc120f292a4555e323f61d38cae90e0c882f67',false),
  ('assistant.end_turn_as(assistant.turns,text,text)','d83ac178978b9ead8b7f34d801534279104608489a7d5193a170a040d61ce3d5',false),
  ('assistant.request_cancel(uuid,text)','0873236232fc1946610027563db6ec784997daf24e959d3282d08e9a484005d9',false),
  ('assistant.unclosed_call(uuid)','e027c4e8d99ffda89af66b6e95167bf335929929f8a61d069cd6ddcac7ff391f',false),
  ('assistant.call_blocker(uuid,integer,timestamp with time zone)','218d413e89c6ce399b5a17fb4f2b185fd74b1d74667df939c346860eca42f0ae',false),
  ('assistant.turn_authorized(assistant.turns)','d42e6b9389fad6033cfc6526e2c68d17f70a09b4a07c3e940d6842bcd2e311a9',false),
  ('assistant.fenced_turn(uuid,bytea)','11646406f0f2eedc699b5bdf3cfa6f4fcb7dbe04b35c9a6ef17f320094b0f968',false),
  ('assistant.ask_authority(security.request_contexts,assistant.agents,integer)','65155d6d5f29c006c835dc4e9396b27fdad4332a54586c1a37abb38fe881a619',false),
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,integer,integer,integer)','d1afdec3d8781476de4960c87a9b11de9e88d57f38325ab1f1dbe5e0b4097254',true),
  ('assistant.pin_turn(uuid,bytea,date,jsonb,integer)','a40d4cbeb2e87fbaf88f7d4b39d8be9d2084140664df927839ec5da7b42371cc',true),
  ('assistant.start_turn(uuid,bytea)','100ebad76438ba498015e72246b69cf6c6f4587dd016d7685fc0bace03762d2f',true),
  ('assistant.open_turn_call(uuid,bytea)','619a43c503a903d366cf166b4200c96a4f8b605c49b50cb0ec9eb1dde0cd4770',true),
  ('assistant.close_turn_call(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text)','1293c284838af9acf5c2aea0c933336de3327606b1081516717e9320903a58b2',true),
  ('assistant.append_turn_text(uuid,bytea,text)','e3454d3ff40e090c7edf5e52bbd40eda9f08f1d1b047bbc36b6383ad31affbe7',true),
  ('assistant.end_turn(uuid,bytea,text,text)','3d9621ae94864c8caff621821212eb9717d2c207f6f32326fd6bebd363856c17',true),
  ('assistant.stop_turn(text,uuid)','8dcaac2bc4aa951e48a40035e61e1295b4e5817fd3590a862b2464d2790bc03c',true),
  ('assistant.conversations(text)','6a5873115979868f2e6e1cb4ee16a3b0a7ebc3126d599f626ffa793dcab6e94c',true),
  ('assistant.conversation(text,uuid,bigint)','5db483287d69a58fb778912a8d8a8e1d28f9d4fb8cc2ea97e570617e48a603ba',true),
  ('assistant.unfinished_turn(text)','878b7ad4ed7be9c3272dd6e89e556902ccc11e88f197eccf68ab7cdf87cd678c',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'tool_steps: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 -- The new tables' immutability; the function belongs to whoever ran the earlier migrations, so only its definition is pinned.
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('market_data.guard_knowledge_receipt()')
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='a376e86bfb72c2f4fd49e1659c29ae0df0503c2f98c7b096a92fb11cbac5122d') THEN
  RAISE EXCEPTION 'tool_steps: unexpected helper %','market_data.guard_knowledge_receipt()' USING ERRCODE='55000';
 END IF;
END$$;

-- How a call that completed stopped (§7.4, §7.5): ending its turn, requesting tools, and how many, or at its output
-- limit. A test's call records neither. A step names its turn's own call.
ALTER TABLE assistant.model_call_outcomes ADD COLUMN stop text CHECK(stop IN('end_turn','tool_use','output_limit')),
 ADD COLUMN tool_requests integer CHECK(tool_requests>0),
 ADD CHECK(stop IS NULL OR outcome IN('succeeded','usage_unrecognized')),
 ADD CHECK((stop IS NOT DISTINCT FROM 'tool_use')=(tool_requests IS NOT NULL));
ALTER TABLE assistant.model_calls ADD UNIQUE(id,turn_id);
-- Each step is published as an event of its own (§8).
ALTER TABLE assistant.turn_events DROP CONSTRAINT turn_events_kind_check,
 ADD CONSTRAINT turn_events_kind_check CHECK(kind IN('accepted','text','step','usage','completed','failed','cancelled','interrupted'));

-- Tool steps (§7.3): each answers, in order, a request of the call that asked for it, numbered across the turn. A read
-- names its operation and the instant it read; a refusal before any read names neither. The request's input is kept
-- when it is an object of at most 8 KiB, which only a refusal of its arguments may lack; the code names a refusal or
-- a failure, and none a result.
CREATE TABLE assistant.tool_steps(
 turn_id uuid NOT NULL REFERENCES assistant.turns(id),ordinal integer NOT NULL CHECK(ordinal>0),
 call_id uuid NOT NULL,tool text NOT NULL CHECK(assistant.are_agent_tools(ARRAY[tool])),
 operation text CHECK(operation~'^[a-z][a-z0-9_]{0,63}([.][a-z][a-z0-9_]{0,63})*$'),
 input jsonb CHECK(jsonb_typeof(input)='object' AND octet_length(input::text)<=8192),code text CHECK(code~'^[a-z][a-z0-9_]{0,63}$'),
 read_at ref_data.instant,recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(turn_id,ordinal),
 FOREIGN KEY(call_id,turn_id) REFERENCES assistant.model_calls(id,turn_id),
 CHECK((operation IS NULL)=(read_at IS NULL) AND NOT read_at>recorded_at),
 CHECK(input IS NOT NULL OR code IS NOT DISTINCT FROM 'tool_arguments_invalid')
);
CREATE INDEX tool_steps_call ON assistant.tool_steps(call_id);
CREATE TRIGGER immutable_tool_step BEFORE UPDATE OR DELETE ON assistant.tool_steps FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
-- What a read ran under (§7.1, §7.3): an access function, the permission it checked and the catalog it read, market data
-- and reference data reads of ReadMarketData, trades and parties reads of their own permission over a market catalog.
CREATE TABLE assistant.tool_read_access(
 turn_id uuid NOT NULL,step integer NOT NULL,access text NOT NULL,permission text NOT NULL REFERENCES security.permissions(code),
 market_catalog_id uuid REFERENCES market_data.catalogs(id),reference_catalog_id uuid REFERENCES ref_data.catalogs(id),
 FOREIGN KEY(turn_id,step) REFERENCES assistant.tool_steps(turn_id,ordinal),
 UNIQUE NULLS NOT DISTINCT(turn_id,step,access,permission,market_catalog_id,reference_catalog_id),
 CHECK(CASE access
  WHEN 'market_access' THEN permission='ReadMarketData' AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
  WHEN 'reference_access' THEN permission='ReadMarketData' AND market_catalog_id IS NULL AND reference_catalog_id IS NOT NULL
  WHEN 'trading_access' THEN permission IN('ReadTrades','ReadTradeParties') AND market_catalog_id IS NOT NULL AND reference_catalog_id IS NULL
  ELSE false END)
);
CREATE TRIGGER immutable_tool_read_access BEFORE UPDATE OR DELETE ON assistant.tool_read_access FOR EACH ROW
 EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- Whether a request context issued to a turn's agent under the turn's grant would hold a permission now (§7.1), as
-- security.has_pre_pilot_permission answers in one, evaluated without it so that worker writes can recheck the turn's
-- sources: at one instant, the agent's principal and membership enabled with the permission, the grant current and
-- listing it, and its delegator holding it and CreateDelegations. An agent's context is never a pilot run's.
CREATE FUNCTION assistant.agent_permission(t assistant.turns,permission text) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM(SELECT clock_timestamp() at)n CROSS JOIN assistant.agents a CROSS JOIN security.agent_delegations d
  WHERE a.id=t.agent_id AND d.id=t.delegation_id AND d.agent_id=a.principal_id AND(d.tenant_id,d.workspace_id)=(t.tenant_id,t.workspace_id)
   AND NOT d.revoked AND d.granted_from<=n.at AND n.at<d.granted_to
   AND security.principal_has_permission(a.principal_id,t.tenant_id,t.workspace_id,agent_permission.permission,n.at)
   AND security.principal_has_permission(d.delegator_id,t.tenant_id,t.workspace_id,agent_permission.permission,n.at)
   AND security.principal_has_permission(d.delegator_id,t.tenant_id,t.workspace_id,'CreateDelegations',n.at)
   AND EXISTS(SELECT 1 FROM security.delegation_permissions p WHERE p.delegation_id=d.id AND p.permission=agent_permission.permission));
$$;
-- What security.<access>(catalog,permission) answers in that context for the reads a source names (§7.1): market and
-- reference data read with ReadMarketData in a catalog of the workspace's scope, and trades or parties read with their
-- permission while the market catalog's data is readable too.
CREATE FUNCTION assistant.agent_access(t assistant.turns,access text,permission text,market_catalog uuid,reference_catalog uuid) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT CASE access
  WHEN 'market_access' THEN permission='ReadMarketData' AND assistant.agent_permission(t,permission)
   AND EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE s.catalog_id=market_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))
  WHEN 'reference_access' THEN permission='ReadMarketData' AND assistant.agent_permission(t,permission)
   AND EXISTS(SELECT 1 FROM security.reference_catalog_scopes s WHERE s.catalog_id=reference_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))
  WHEN 'trading_access' THEN assistant.agent_permission(t,permission) AND assistant.agent_permission(t,'ReadMarketData')
   AND EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE s.catalog_id=market_catalog AND(s.tenant_id,s.workspace_id)=(t.tenant_id,t.workspace_id))
  ELSE false END;
$$;
-- A JSON array of canonical uuid strings, as the API writes them.
CREATE FUNCTION assistant.is_uuid_array(a jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN jsonb_typeof(a) IS DISTINCT FROM 'array' THEN false ELSE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(a) e
  WHERE jsonb_typeof(e)<>'string' OR e#>>'{}'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') END;
$$;
-- One access a read ran under, as the worker names it: exactly an access function, a catalog id and a permission that
-- tool_read_access accepts, in a catalog of that access's kind.
CREATE FUNCTION assistant.is_read_access(a jsonb) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN jsonb_typeof(a) IS DISTINCT FROM 'object' THEN false
  WHEN(SELECT array_agg(k ORDER BY k COLLATE "C") FROM jsonb_object_keys(a) k)<>ARRAY['access','catalogId','permission']
   OR NOT assistant.is_uuid_array(jsonb_build_array(a->'catalogId')) THEN false
  ELSE CASE a->>'access'
   WHEN 'market_access' THEN a->>'permission'='ReadMarketData' AND EXISTS(SELECT 1 FROM market_data.catalogs c WHERE c.id=(a->>'catalogId')::uuid)
   WHEN 'reference_access' THEN a->>'permission'='ReadMarketData' AND EXISTS(SELECT 1 FROM ref_data.catalogs c WHERE c.id=(a->>'catalogId')::uuid)
   WHEN 'trading_access' THEN a->>'permission' IN('ReadTrades','ReadTradeParties')
    AND EXISTS(SELECT 1 FROM market_data.catalogs c WHERE c.id=(a->>'catalogId')::uuid)
   ELSE false END END;
$$;
-- A turn's source set (§7.1, §7.6): what its reads ran under and, through every turn it included, what theirs did.
CREATE FUNCTION assistant.turn_sources(turn uuid) RETURNS TABLE(access text,permission text,market_catalog_id uuid,reference_catalog_id uuid)
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH RECURSIVE reached(id) AS(SELECT turn_sources.turn
  UNION SELECT i.id FROM reached r JOIN assistant.turns t ON t.id=r.id CROSS JOIN LATERAL unnest(t.included_turns) i(id))
 SELECT DISTINCT a.access,a.permission,a.market_catalog_id,a.reference_catalog_id FROM reached r JOIN assistant.tool_read_access a ON a.turn_id=r.id;
$$;
-- The source-access check (§7.1): every source passes in the authority of a context issued to the turn's agent under
-- its grant now.
CREATE FUNCTION assistant.sources_accessible(t assistant.turns) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT NOT EXISTS(SELECT 1 FROM assistant.turn_sources(t.id) s
  WHERE NOT assistant.agent_access(t,s.access,s.permission,s.market_catalog_id,s.reference_catalog_id));
$$;
-- The replay recheck (§7.1): every source passes its access function in the caller's own request context, which needs
-- no grant and ignores agent, model and provider state.
CREATE FUNCTION assistant.sources_readable(turn uuid) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT NOT EXISTS(SELECT 1 FROM assistant.turn_sources(sources_readable.turn) s WHERE NOT CASE s.access
  WHEN 'market_access' THEN security.market_access(s.market_catalog_id,s.permission)
  WHEN 'reference_access' THEN security.reference_access(s.reference_catalog_id,s.permission)
  ELSE security.trading_access(s.market_catalog_id,s.permission) END);
$$;
-- A turn as its owner reads it (§7.1, §8), saying whether the replay recheck hid its answer: once the recheck fails, its
-- answer is hidden as access_changed.
CREATE FUNCTION assistant.owner_turn(t assistant.turns) RETURNS jsonb
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT assistant.turn_state(t)||CASE WHEN assistant.sources_readable(t.id) THEN '{"hidden":null}'::jsonb
  ELSE '{"answer":"","hidden":"access_changed"}'::jsonb END;
$$;

-- Records a running turn's next step for the worker (§7.1, §7.3, §7.5): it answers the next request of the turn's last
-- call, which stopped requesting tools, while all that call's requests fit within the version's tool steps
-- (limit_tool_steps otherwise, recording none). A read names its operation, the instant it read, at or after that
-- call's close, and every access it ran under, which join the turn's source set even when the read then failed. Like
-- an append it takes the epoch row FOR SHARE before the turn, and it then rechecks the owner's current authority and,
-- in the agent's, every source: once either fails it publishes nothing, records the refusal and answers refused
-- access_changed, as it does for every later step. Once the turn's cancel is requested it records nothing. Its event
-- carries no business data: the step's number and tool, and the code of a refusal or failure.
CREATE FUNCTION assistant.record_tool_step(turn uuid,token bytea,tool text,operation text,input jsonb,code text,read_at timestamptz,access jsonb)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns;v assistant.agent_versions;last assistant.model_calls;ended assistant.model_call_outcomes;earlier integer;answered integer;
 number integer;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 t:=assistant.fenced_turn(turn,token);
 SELECT x.* INTO v FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(t.agent_id,t.agent_version);
 IF tool IS NULL OR NOT tool=ANY(v.tools) OR code!~'^[a-z][a-z0-9_]{0,63}$'
  OR(CASE WHEN input IS NULL THEN code IS DISTINCT FROM 'tool_arguments_invalid' ELSE jsonb_typeof(input)<>'object' OR octet_length(input::text)>8192 END)
  OR(CASE WHEN operation IS NULL THEN read_at IS NOT NULL OR access IS NOT NULL
   ELSE operation!~'^[a-z][a-z0-9_]{0,63}([.][a-z][a-z0-9_]{0,63})*$' OR read_at IS NULL OR read_at>clock_timestamp()
    OR jsonb_typeof(access) IS DISTINCT FROM 'array' END) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF operation IS NOT NULL AND(jsonb_array_length(access)=0 OR EXISTS(SELECT 1 FROM jsonb_array_elements(access) a WHERE assistant.is_read_access(a) IS NOT TRUE)) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF t.state NOT IN('running','cancel_requested') OR assistant.unclosed_call(t.id) IS NOT NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(t));END IF;
 SELECT x.* INTO last FROM assistant.model_calls x WHERE x.turn_id=t.id ORDER BY x.attempt DESC LIMIT 1;
 SELECT o.* INTO ended FROM assistant.model_call_outcomes o WHERE o.call_id=last.id;
 SELECT count(*) FILTER(WHERE s.call_id<>last.id),count(*) FILTER(WHERE s.call_id=last.id) INTO earlier,answered
  FROM assistant.tool_steps s WHERE s.turn_id=t.id;
 IF ended.stop IS DISTINCT FROM 'tool_use' OR answered>=ended.tool_requests THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF earlier+ended.tool_requests>v.max_tool_steps THEN RAISE EXCEPTION 'limit_tool_steps';END IF;
 IF read_at<ended.closed_at THEN RAISE EXCEPTION 'turn_input';END IF;
 IF t.publication_refused_at IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(t),'refused','access_changed');END IF;
 number:=earlier+answered+1;
 INSERT INTO assistant.tool_steps VALUES(t.id,number,last.id,tool,operation,input,code,read_at,clock_timestamp());
 INSERT INTO assistant.tool_read_access SELECT DISTINCT t.id,number,a->>'access',a->>'permission',
   CASE WHEN a->>'access'<>'reference_access' THEN(a->>'catalogId')::uuid END,CASE WHEN a->>'access'='reference_access' THEN(a->>'catalogId')::uuid END
  FROM jsonb_array_elements(coalesce(access,'[]')) a;
 IF NOT assistant.turn_authorized(t) OR NOT assistant.sources_accessible(t) THEN
  UPDATE assistant.turns x SET publication_refused_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
  RETURN jsonb_build_object('turn',assistant.turn_state(t),'refused','access_changed');
 END IF;
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'step',number,'sequence',assistant.turn_event(t,'step',
  jsonb_build_object('ordinal',number,'tool',tool)||CASE WHEN code IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('code',code) END));
END $$;

-- As in 0071, with the earlier turns included only while their owner can still read every source they used and the
-- covering grant lists every permission those sources needed (§7.6): each withheld turn is named by a step after the
-- accepted event, and the ordinal counts every earlier turn. The included turns' sources pass the source-access check
-- before the turn is admitted (access_changed), and both answers show the turn as its owner reads it.
CREATE OR REPLACE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
 catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
 token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous assistant.turns;w assistant.worker_generation;k assistant.conversations;
 a assistant.agents;delegation uuid;blocker text;n timestamptz;t assistant.turns;stale assistant.turns;earlier uuid[];withheld integer[];number integer;
 skipped integer;
 zero constant uuid:='00000000-0000-0000-0000-000000000000';
BEGIN
 IF operation IS NULL OR operation=zero OR scope IS NULL OR scope NOT IN('api','exchange') OR conversation=zero OR agent IS NULL OR agent=zero
  OR agent_version IS NULL OR agent_version<1 OR question IS NULL OR NOT assistant.is_instructions(question) OR page_report IS NULL
  OR jsonb_typeof(page_report)<>'object' OR octet_length(page_report::text)>8192 OR catalog IS NULL OR knowledge_mode IS NULL
  OR knowledge_mode NOT IN('current','retained') OR(knowledge_mode='retained')<>(retained_context IS NOT NULL) OR retained_context=zero OR frame=zero
  OR session_digest IS NULL OR session_digest!~'^[0-9a-f]{64}$' OR profile IS NULL OR NOT assistant.is_display_name(profile)
  OR session_ends_at IS NULL OR session_ends_at<=clock_timestamp() OR session_ends_at>='9999-12-31 00:00:00+00'
  OR token_digest IS NULL OR token_digest!~'^[0-9a-f]{64}$' OR runtime_set IS NULL OR runtime_set!~'^[a-z][a-z0-9_]{0,63}$'
  OR question_limit IS NULL OR question_limit<1 OR workspace_turns IS NULL OR workspace_turns<1 OR admission_seconds IS NULL OR admission_seconds<1 THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF length(question)>question_limit THEN RAISE EXCEPTION 'question_too_long';END IF;
 -- The person's principal row serializes their asks, so an identical ask sent while its original still runs
 -- waits for it.
 PERFORM 1 FROM security.principals x WHERE x.id=c.actor_id FOR NO KEY UPDATE;
 SELECT x.* INTO previous FROM assistant.turns x WHERE(x.owner_id,x.operation_id)=(c.actor_id,operation);
 -- The wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.grant_actor(true);
 IF FOUND THEN
  IF(previous.tenant_id,previous.workspace_id)<>(c.tenant_id,c.workspace_id)
   OR(SELECT x.scope FROM assistant.conversations x WHERE x.id=previous.conversation_id)<>ask_turn.scope
   OR(CASE WHEN conversation IS NULL THEN previous.ordinal<>1 ELSE previous.conversation_id<>conversation OR previous.ordinal=1 END)
   OR(previous.agent_id,previous.agent_version,previous.question,previous.page_report,previous.catalog_id,previous.knowledge_mode)
    IS DISTINCT FROM(agent,agent_version,question,page_report,catalog,knowledge_mode)
   OR(previous.requested_context,previous.frame_id) IS DISTINCT FROM(retained_context,frame) THEN
   RAISE EXCEPTION 'turn_conflict';
  END IF;
  RETURN jsonb_build_object('turn',assistant.owner_turn(previous));
 END IF;
 SELECT x.* INTO w FROM assistant.worker_generation x FOR SHARE;
 IF generation IS NULL OR w.generation<>generation OR NOT assistant.holds_worker_lock(w.worker_pid) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 -- A conversation's owner, scope and agent never change, so it is checked before admission (§8).
 IF conversation IS NOT NULL THEN
  SELECT x.* INTO k FROM assistant.conversations x
   WHERE x.id=conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,ask_turn.scope);
  IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
  IF k.agent_id<>agent THEN RAISE EXCEPTION 'conversation_agent_mismatch';END IF;
 END IF;
 -- Admission (§7.4): one workspace at a time. What it ends, in one pass in id order, as every writer of several turns
 -- locks them: a turn still without its record at the deadline (ask_incomplete, or cancelled once its cancel was
 -- requested), and any turn past its elapsed limit plus the deadline (interrupted).
 PERFORM pg_advisory_xact_lock(730849161,hashtext(c.tenant_id::text||c.workspace_id::text));
 n:=clock_timestamp();
 FOR stale IN SELECT x.* FROM assistant.turns x JOIN assistant.agent_versions y ON(y.agent_id,y.version)=(x.agent_id,x.agent_version)
  WHERE(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) AND x.state IN('queued','running','cancel_requested')
   AND(x.recorded_at IS NULL AND x.asked_at+make_interval(secs=>admission_seconds)<=n
    OR x.asked_at+make_interval(secs=>y.max_elapsed_seconds+admission_seconds)<=n) ORDER BY x.id FOR UPDATE OF x LOOP
  IF stale.recorded_at IS NOT NULL THEN PERFORM assistant.end_turn_as(stale,'interrupted',NULL);
  ELSIF stale.state='cancel_requested' THEN PERFORM assistant.end_turn_as(stale,'cancelled',stale.cancel_code);
  ELSE PERFORM assistant.end_turn_as(stale,'failed','ask_incomplete');
  END IF;
 END LOOP;
 -- Save and Publish lock the agent's row, so the version stands as checked until the turn commits.
 SELECT x.* INTO a FROM assistant.agents x WHERE x.id=agent AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR SHARE;
 -- The waits can outlive the authority, which is checked from here on.
 c:=assistant.grant_actor(true);
 delegation:=assistant.ask_authority(c,a,agent_version);
 n:=clock_timestamp();
 blocker:=assistant.call_blocker(a.id,agent_version,n);
 IF blocker IS NOT NULL THEN RAISE EXCEPTION '%',blocker;END IF;
 IF EXISTS(SELECT 1 FROM assistant.turns x WHERE(x.tenant_id,x.workspace_id,x.owner_id)=(c.tenant_id,c.workspace_id,c.actor_id)
   AND x.state IN('queued','running','cancel_requested')) THEN
  RETURN jsonb_build_object('refused','turn_active');
 END IF;
 IF(SELECT count(*) FROM assistant.turns x WHERE(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) AND x.state IN('queued','running','cancel_requested'))
   >=workspace_turns THEN
  RETURN jsonb_build_object('refused','workspace_turn_limit');
 END IF;
 IF k.id IS NULL THEN
  INSERT INTO assistant.conversations VALUES(gen_random_uuid(),c.tenant_id,c.workspace_id,c.actor_id,scope,a.id,n,0) RETURNING * INTO k;
 END IF;
 SELECT coalesce(array_agg(x.id ORDER BY x.ordinal) FILTER(WHERE x.included),ARRAY[]::uuid[]),
   coalesce(array_agg(x.ordinal ORDER BY x.ordinal) FILTER(WHERE NOT x.included),ARRAY[]::integer[]),count(*)+1
  INTO earlier,withheld,number
  FROM(SELECT y.id,y.ordinal,assistant.sources_readable(y.id) AND NOT EXISTS(SELECT 1 FROM assistant.turn_sources(y.id) s
     WHERE NOT EXISTS(SELECT 1 FROM security.delegation_permissions p WHERE p.delegation_id=delegation AND p.permission=s.permission)) included
   FROM assistant.turns y WHERE y.conversation_id=k.id)x;
 INSERT INTO assistant.turns VALUES(gen_random_uuid(),k.id,c.tenant_id,c.workspace_id,number,c.actor_id,operation,a.id,agent_version,
  agent_version>coalesce(a.published_version,0),delegation,question,page_report,catalog,knowledge_mode,retained_context,frame,NULL,NULL,NULL,NULL,
  earlier,runtime_set,'queued',NULL,NULL,'',false,generation,token_digest,session_digest,profile,session_ends_at,n,NULL,NULL,NULL,NULL) RETURNING * INTO t;
 IF NOT assistant.sources_accessible(t) THEN RAISE EXCEPTION 'access_changed';END IF;
 PERFORM assistant.turn_event(t,'accepted',jsonb_build_object('ordinal',t.ordinal,'agentId',t.agent_id,'version',t.agent_version));
 FOREACH skipped IN ARRAY withheld LOOP
  PERFORM assistant.turn_event(t,'step',jsonb_build_object('withheld',skipped));
 END LOOP;
 RETURN jsonb_build_object('turn',assistant.owner_turn(t),'admitted',true);
END $$;

-- As in 0069, with an As of frame's exact pins (§7.2): besides its instant, observation ids and curve-contract
-- selections, its name, the observations it froze among them, the swap market selections it names and whether it
-- resolves the latest correction, so that tool reads rebuild the page's frame; and answering the turn as its owner
-- reads it.
CREATE OR REPLACE FUNCTION assistant.pin_turn(turn uuid,token bytea,effective_date date,pins jsonb,reserve integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);t assistant.turns;x market_data.temporal_contexts;context uuid;
BEGIN
 t:=assistant.fenced_turn(turn,token);
 -- The wait can outlive the authority.
 c:=assistant.grant_actor(true);
 IF t.owner_id<>c.actor_id OR(t.tenant_id,t.workspace_id)<>(c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 IF t.recorded_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.owner_turn(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF(t.frame_id IS NULL)<>(pins IS NULL) OR pins IS NOT NULL AND(jsonb_typeof(pins)<>'object' OR pins->>'frameId' IS DISTINCT FROM t.frame_id::text
   OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(pins) key)<>ARRAY['at','curveContracts','frameId','frozenObservationIds','name',
    'observationIds','resolvesLatestCorrection','swapMarketSelections']
   OR jsonb_typeof(pins->'at')<>'string' OR pins->>'at'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$'
   OR jsonb_typeof(pins->'name')<>'string' OR btrim(pins->>'name')=''
   OR NOT assistant.is_uuid_array(pins->'observationIds') OR NOT assistant.is_uuid_array(pins->'frozenObservationIds')
   OR jsonb_typeof(pins->'curveContracts')<>'object' OR jsonb_typeof(pins->'swapMarketSelections') NOT IN('object','null')
   OR jsonb_typeof(pins->'resolvesLatestCorrection')<>'boolean' OR octet_length(pins::text)>65536) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF pins IS NOT NULL AND(EXISTS(SELECT 1 FROM jsonb_array_elements_text(pins->'frozenObservationIds') f WHERE NOT(pins->'observationIds')?f)
   OR EXISTS(SELECT 1 FROM jsonb_each(pins->'curveContracts') e WHERE e.key='' OR NOT assistant.is_uuid_array(e.value))
   OR jsonb_typeof(pins->'swapMarketSelections')='object' AND EXISTS(SELECT 1 FROM jsonb_each(pins->'swapMarketSelections') e
    WHERE NOT assistant.is_uuid_array(jsonb_build_array(e.key)) OR NOT assistant.is_uuid_array(e.value))) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 -- The frame's instant is one this database reads.
 BEGIN
  PERFORM(pins->>'at')::timestamptz;
 EXCEPTION WHEN datetime_field_overflow OR invalid_datetime_format OR invalid_time_zone_displacement_value THEN RAISE EXCEPTION 'turn_input';
 END;
 IF t.knowledge_mode='current' THEN
  IF effective_date IS NULL OR reserve IS NULL OR reserve<0 THEN RAISE EXCEPTION 'turn_input';END IF;
  -- Every capture certifies under this lock, held to commit, so the count cannot move before this one does.
  PERFORM pg_advisory_xact_lock(730849102);
  c:=assistant.grant_actor(true);
  IF(SELECT count(*) FROM market_data.temporal_contexts y WHERE y.catalog_id=t.catalog_id)>=4096-reserve THEN RAISE EXCEPTION 'knowledge_budget_reserved';END IF;
  context:=security.capture_date_context(t.catalog_id,effective_date,NULL);
 ELSE
  IF NOT security.market_access(t.catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current market read authority' USING ERRCODE='42501';END IF;
  context:=t.requested_context;
 END IF;
 SELECT y.* INTO x FROM market_data.temporal_contexts y WHERE(y.catalog_id,y.id)=(t.catalog_id,context);
 IF x.id IS NULL THEN RAISE EXCEPTION 'temporal_context';END IF;
 UPDATE assistant.turns y SET context_id=x.id,cutoff=x.known_at,pins=pin_turn.pins,recorded_at=clock_timestamp() WHERE y.id=t.id RETURNING y.* INTO t;
 RETURN jsonb_build_object('turn',assistant.owner_turn(t));
END $$;

-- As in 0069, also answering what tool reads need (§7.1, §7.2): the catalog, the pins, the turn's grant and its agent's
-- principal, in whose request context every read runs.
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
   'elapsedSeconds',v.max_elapsed_seconds,'retries',v.max_retries),
  'catalogId',t.catalog_id,'pins',t.pins,'delegationId',t.delegation_id,
  'agentPrincipal',(SELECT a.principal_id FROM assistant.agents a WHERE a.id=t.agent_id),
  'earlier',coalesce((SELECT jsonb_agg(jsonb_build_object('ordinal',e.ordinal,'question',e.question,'answer',e.answer,'incomplete',e.incomplete,
     'state',e.state,'outcome',e.outcome,'context',jsonb_build_object('mode',e.knowledge_mode,'cutoff',e.cutoff,'frameId',e.frame_id)) ORDER BY e.ordinal)
   FROM assistant.turns e WHERE e.id=ANY(t.included_turns)),'[]'::jsonb)));
END $$;

-- As in 0069, with the tool loop (§7.3, §7.5): after its first call a turn opens the next once a step answered each
-- request of a call that stopped requesting tools, or as a retry of a transport failure or overload whose input is
-- known and that published nothing, while the turn's failed calls, each retried, are within the version's retries
-- (retry_not_allowed otherwise). Before every call after the first, any earlier call's input not known ends the turn
-- limit_input_unverifiable, and the input they reported past the version's limit ends it limit_input_tokens; nothing is
-- estimated, so the call that crossed it was sent and billed. The open takes the epoch row FOR SHARE first, as a
-- publication does, so it waits for a control change in progress and then sees it, and the owner's authority and the
-- turn's sources in the agent's are checked before every call (access_changed).
CREATE OR REPLACE FUNCTION assistant.open_turn_call(turn uuid,token bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE v assistant.agent_versions;m assistant.models;t assistant.turns;last assistant.model_calls;ended assistant.model_call_outcomes;blocker text;
 r assistant.price_revisions;s assistant.attestations;k assistant.credentials;vendor assistant.vendor;call uuid:=gen_random_uuid();n timestamptz;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 SELECT y.* INTO v FROM assistant.turns x JOIN assistant.agent_versions y ON(y.agent_id,y.version)=(x.agent_id,x.agent_version) WHERE x.id=open_turn_call.turn;
 SELECT y.* INTO m FROM assistant.models y WHERE y.id=v.model_id FOR SHARE;
 PERFORM 1 FROM assistant.providers y WHERE y.id=m.provider_id FOR SHARE;
 t:=assistant.fenced_turn(turn,token);
 IF assistant.unclosed_call(t.id) IS NOT NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF t.state<>'running' THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.publication_refused_at IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','access_changed')));END IF;
 SELECT x.* INTO last FROM assistant.model_calls x WHERE x.turn_id=t.id ORDER BY x.attempt DESC LIMIT 1;
 IF last.id IS NOT NULL THEN
  SELECT o.* INTO ended FROM assistant.model_call_outcomes o WHERE o.call_id=last.id;
  IF ended.stop='tool_use' THEN
   IF(SELECT count(*) FROM assistant.tool_steps x WHERE x.call_id=last.id)<ended.tool_requests THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
  ELSIF ended.outcome NOT IN('model_provider_transport','model_provider_overloaded') OR ended.usage_status<>'recognized'
   OR(SELECT count(*) FROM assistant.model_calls x JOIN assistant.model_call_outcomes o ON o.call_id=x.id WHERE x.turn_id=t.id AND o.stop IS NULL)>v.max_retries
   OR EXISTS(SELECT 1 FROM assistant.turn_events e WHERE e.conversation_id=t.conversation_id AND e.sequence>last.after_sequence AND e.kind IN('text','step')) THEN
   RAISE EXCEPTION 'retry_not_allowed';
  END IF;
  IF EXISTS(SELECT 1 FROM assistant.model_calls x JOIN assistant.model_call_outcomes o ON o.call_id=x.id WHERE x.turn_id=t.id AND o.usage_status<>'recognized') THEN
   RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','limit_input_unverifiable')));
  END IF;
  IF(SELECT sum(o.uncached_input)+sum(o.cache_read)+sum(o.cache_write) FROM assistant.model_calls x JOIN assistant.model_call_outcomes o ON o.call_id=x.id
    WHERE x.turn_id=t.id)>v.max_input_tokens THEN
   RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','limit_input_tokens')));
  END IF;
 END IF;
 IF NOT assistant.turn_authorized(t) OR NOT assistant.sources_accessible(t) THEN
  RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','access_changed')));
 END IF;
 n:=clock_timestamp();
 blocker:=assistant.call_blocker(t.agent_id,t.agent_version,n);
 IF blocker IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed',blocker)));END IF;
 r:=assistant.price_in_force(m.id,n);
 SELECT x.* INTO s FROM assistant.current_attestation(m.provider_id) x;
 SELECT x.* INTO k FROM assistant.credentials x WHERE(x.provider_id,x.version)=(s.provider_id,s.credential_version);
 SELECT p.vendor INTO vendor FROM assistant.providers p WHERE p.id=m.provider_id;
 INSERT INTO assistant.model_calls(id,tenant_id,workspace_id,operation_id,actor_id,token_digest,generation,provider_id,credential_version,terms_version,
  model_id,model_version,reasoning,max_output_tokens,price_sequence,cost_method,started_at,turn_id,attempt,after_sequence)
  VALUES(call,t.tenant_id,t.workspace_id,NULL,NULL,NULL,t.generation,s.provider_id,s.credential_version,s.terms_version,m.id,v.model_version,v.reasoning,
   v.max_output_tokens,r.sequence,'token_cost_v1',n,t.id,coalesce(last.attempt,0)+1,
   (SELECT max(e.sequence) FROM assistant.turn_events e WHERE e.conversation_id=t.conversation_id));
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'opened',jsonb_build_object('call',call,'attempt',coalesce(last.attempt,0)+1,
  'identifier',m.identifier,'acceptedServed',(SELECT x.accepted_served FROM assistant.model_versions x WHERE(x.model_id,x.version)=(m.id,v.model_version)),
  'mapping',(SELECT x.mapping FROM assistant.model_versions x WHERE(x.model_id,x.version)=(m.id,v.model_version)),
  'reasoning',v.reasoning,'maxOutputTokens',v.max_output_tokens,'caching',v.caching,'provider',s.provider_id,'vendor',vendor,
  'credentialVersion',k.version,'nonce',encode(k.nonce,'hex'),'ciphertext',encode(k.ciphertext,'hex'),'tag',encode(k.tag,'hex')));
END $$;

-- As in 0069, also recording how a call that completed stopped: ending its turn, requesting tools, and how many, or at
-- its output limit; a call that did not complete names neither.
DROP FUNCTION assistant.close_turn_call(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text);
CREATE FUNCTION assistant.close_turn_call(call uuid,token bytea,outcome text,served text,usage_status text,
 uncached_input bigint,uncached_input_source text,cache_read bigint,cache_read_source text,cache_write bigint,cache_write_source text,
 output bigint,output_source text,reasoning bigint,reasoning_source text,stop text,tool_requests integer) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE k assistant.model_calls;
BEGIN
 SELECT x.* INTO k FROM assistant.model_calls x JOIN assistant.turns t ON t.id=x.turn_id
  WHERE x.id=close_turn_call.call AND token IS NOT NULL AND encode(sha256(token),'hex')=t.token_digest;
 IF k.id IS NULL THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 IF(outcome IN('succeeded','usage_unrecognized')) IS DISTINCT FROM(stop IS NOT NULL) OR stop NOT IN('end_turn','tool_use','output_limit')
  OR(stop IS NOT DISTINCT FROM 'tool_use')<>(tool_requests IS NOT NULL) OR tool_requests<1 THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,served,usage_status,uncached_input,uncached_input_source,cache_read,cache_read_source,
   cache_write,cache_write_source,output,output_source,reasoning,reasoning_source,cost,stop,tool_requests)
  VALUES(k.id,clock_timestamp(),outcome,served,usage_status,uncached_input,uncached_input_source,cache_read,cache_read_source,cache_write,cache_write_source,
   output,output_source,reasoning,reasoning_source,
   CASE WHEN usage_status='recognized' THEN assistant.token_cost_v1((SELECT r FROM assistant.price_revisions r WHERE(r.model_id,r.sequence)=(k.model_id,k.price_sequence)),
    uncached_input,cache_read,cache_write,output) END,stop,tool_requests)
 ON CONFLICT(call_id) DO NOTHING;
 RETURN FOUND;
END $$;

-- As in 0069, with the publication recheck also evaluating the turn's sources in the agent's authority (§7.1).
CREATE OR REPLACE FUNCTION assistant.append_turn_text(turn uuid,token bytea,chunk text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 t:=assistant.fenced_turn(turn,token);
 IF chunk IS NULL OR chunk='' OR octet_length(chunk)>2048 THEN RAISE EXCEPTION 'turn_input';END IF;
 IF t.state NOT IN('running','cancel_requested') OR assistant.unclosed_call(t.id) IS NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(t));END IF;
 IF t.publication_refused_at IS NULL AND NOT(assistant.turn_authorized(t) AND assistant.sources_accessible(t)) THEN
  UPDATE assistant.turns x SET publication_refused_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
 END IF;
 IF t.publication_refused_at IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(t),'refused','access_changed');END IF;
 UPDATE assistant.turns x SET answer=x.answer||chunk WHERE x.id=t.id RETURNING x.* INTO t;
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'sequence',assistant.turn_event(t,'text',jsonb_build_object('text',chunk)));
END $$;

-- As in 0069, a turn completing only once its last call completed by ending the turn: not while a step must answer a
-- tool request, nor after its output limit.
CREATE OR REPLACE FUNCTION assistant.end_turn(turn uuid,token bytea,ending text,code text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns:=assistant.fenced_turn(turn,token);
BEGIN
 IF ending IS NULL OR ending NOT IN('completed','failed') OR(ending='completed')<>(code IS NULL) OR code!~'^[a-z][a-z0-9_]{0,63}$' THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF t.state NOT IN('queued','running','cancel_requested') OR assistant.unclosed_call(t.id) IS NOT NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF t.publication_refused_at IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','access_changed')));END IF;
 IF ending='failed' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed',code)));END IF;
 IF t.state<>'running' OR NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o
   WHERE o.call_id=(SELECT x.id FROM assistant.model_calls x WHERE x.turn_id=t.id ORDER BY x.attempt DESC LIMIT 1) AND o.stop='end_turn') THEN
  RAISE EXCEPTION 'turn_state_conflict';
 END IF;
 RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'completed',NULL)));
END $$;

-- As in 0069, answering the turn as its owner reads it.
CREATE OR REPLACE FUNCTION assistant.stop_turn(scope text,turn uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);t assistant.turns;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO t FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
  WHERE x.id=stop_turn.turn AND(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,stop_turn.scope) FOR UPDATE OF x;
 IF t.id IS NULL THEN RAISE EXCEPTION 'turn_not_found';END IF;
 PERFORM assistant.request_cancel(t.id,NULL);
 RETURN jsonb_build_object('turn',assistant.owner_turn((SELECT x FROM assistant.turns x WHERE x.id=t.id)));
END $$;

-- As in 0069, each conversation's latest turn as its owner reads it.
CREATE OR REPLACE FUNCTION assistant.conversations(scope text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 RETURN jsonb_build_object('conversations',coalesce((SELECT jsonb_agg(jsonb_build_object('id',k.id,'agentId',k.agent_id,'agentName',a.name,
    'createdAt',k.created_at,'latestSequence',e.sequence,'readThrough',k.read_through,'unread',e.sequence>k.read_through,
    'latestTurn',assistant.owner_turn(t)) ORDER BY e.recorded_at DESC,k.id)
  FROM assistant.conversations k JOIN assistant.agents a ON a.id=k.agent_id
   CROSS JOIN LATERAL(SELECT x.sequence,x.recorded_at FROM assistant.turn_events x WHERE x.conversation_id=k.id ORDER BY x.sequence DESC LIMIT 1)e
   CROSS JOIN LATERAL(SELECT x FROM assistant.turns x WHERE x.conversation_id=k.id ORDER BY x.ordinal DESC LIMIT 1)l(t)
  WHERE(k.tenant_id,k.workspace_id,k.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,conversations.scope)),'[]'::jsonb));
END $$;
-- As in 0069, each turn as its owner reads it; a turn whose answer is hidden has its text events hidden as well.
CREATE OR REPLACE FUNCTION assistant.conversation(scope text,conversation uuid,after bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);k assistant.conversations;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') OR after IS NULL OR after<0 THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO k FROM assistant.conversations x
  WHERE x.id=conversation.conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,conversation.scope);
 IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
 RETURN(WITH shown AS(SELECT t.id,t.ordinal,assistant.owner_turn(t) turn FROM assistant.turns t WHERE t.conversation_id=k.id)
  SELECT jsonb_build_object('conversation',jsonb_build_object('id',k.id,'agentId',k.agent_id,'createdAt',k.created_at,'readThrough',k.read_through),
   'turns',coalesce((SELECT jsonb_agg(s.turn ORDER BY s.ordinal) FROM shown s),'[]'::jsonb),
   'events',coalesce((SELECT jsonb_agg(jsonb_build_object('sequence',e.sequence,'turnId',e.turn_id,'kind',e.kind,
      'payload',CASE WHEN e.kind='text' AND s.turn->>'hidden' IS NOT NULL THEN jsonb_build_object('hidden',s.turn->'hidden') ELSE e.payload END,
      'at',e.recorded_at) ORDER BY e.sequence)
    FROM assistant.turn_events e JOIN shown s ON s.id=e.turn_id WHERE e.conversation_id=k.id AND e.sequence>after),'[]'::jsonb)));
END $$;
-- As in 0069, the unfinished turn as its owner reads it.
CREATE OR REPLACE FUNCTION assistant.unfinished_turn(scope text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 RETURN jsonb_build_object('turn',(SELECT assistant.owner_turn(t) FROM assistant.turns t JOIN assistant.conversations k ON k.id=t.conversation_id
  WHERE(t.tenant_id,t.workspace_id,t.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,unfinished_turn.scope)
   AND t.state IN('queued','running','cancel_requested')));
END $$;

DO $$DECLARE t text;f text;BEGIN
 FOREACH t IN ARRAY ARRAY['tool_steps','tool_read_access'] LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t);
 END LOOP;
 FOREACH f IN ARRAY ARRAY['assistant.agent_permission(assistant.turns,text)','assistant.agent_access(assistant.turns,text,text,uuid,uuid)',
   'assistant.is_uuid_array(jsonb)','assistant.is_read_access(jsonb)','assistant.turn_sources(uuid)','assistant.sources_accessible(assistant.turns)','assistant.sources_readable(uuid)',
   'assistant.owner_turn(assistant.turns)','assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb)',
   'assistant.close_turn_call(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text,text,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.record_tool_step(uuid,bytea,text,text,jsonb,text,timestamptz,jsonb),
 assistant.close_turn_call(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text,text,integer) TO openetrm_runtime;
COMMIT;
