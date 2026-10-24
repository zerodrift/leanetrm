-- Page-assistant turns (docs/ai-assistant.md §5.1, §7.1, §7.2, §7.4, §7.5, §7.6, §8): conversations, turns and their
-- events, and the model calls a turn opens. A person asks in their own context; admission, the turn's pinned-context
-- record and every later write follow §7.4, the worker's writes presenting the turn's fencing token under the current
-- worker generation. Stop, the session's end, Disable, Replace key, Record terms, Retire and revocation request a
-- turn's cancel; a worker start interrupts what an earlier generation left running. Tools, the stream and Usage come
-- later: until tools record reads, a turn reads no workspace data, so its answer needs no replay recheck, and it makes
-- one call, retried only after a transport failure or overload.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority, capture, model, pricing and worker helpers turns rely on and the definitions this migration
-- replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',true),
  ('security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)','43e35b62e3ad9ea86286250b208edf56f3dd7ddce12801f4288d75b98d7506bb',false),
  ('security.capture_date_context(uuid,date,timestamp with time zone)','9c03d5f9761ebc717308683a74a5eb57391750daed9c336585e6837998777540',true),
  ('security.market_access(uuid,text)','804fc62e6057b1b2d528e3493d761aa52d613a766a9087c90fcd6313b5832383',true),
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.is_instructions(text)','9637ce303efb18669266c94083fb9cfb66810143e47f15ca12b66e2c698ca59e',false),
  ('assistant.is_display_name(text)','0fa2f8b4c9a0999d2098f85f44b63ea90a29d615855648f79d8361f11dd90952',false),
  ('assistant.grant_actor(boolean)','4aaf7c789d860df03d222e891b74008dafb6aae71d01a77b17219c35621a7503',false),
  ('assistant.may_use(security.request_contexts,assistant.agents,integer)','3f1f0c04f2e874c4d517599a8edc25733a584a0070634e1fdae7285fbb53203b',false),
  ('assistant.version_permissions(uuid,integer)','ae32a446bae2c29754854db841c853ad15bfd4913e135aba9536301b5e480f98',false),
  ('assistant.covering_grant(security.request_contexts,uuid,integer)','f32031a4f7d87018361ddfcefab04d97ff8f049c76168de93168df89df4128ff',false),
  ('assistant.model_qualified(uuid)','bcbf74756334e92c969281f24fd98362f369f499c285bdc5cdcdf92e8d4fb73a',false),
  ('assistant.provider_qualified(uuid)','784555e5e2dd89746140ef3ab3a1a87cc06cb7b4482b4dfcaa0ff0a113d0be18',false),
  ('assistant.current_attestation(uuid)','142978f0af07358286fbd725af2e4b8e073a04143401f30cef8db1452e78a51c',false),
  ('assistant.current_model_version(uuid)','bc966aa2132643261ac9bdf28fc7fd3094e296f4da6617b2a69b89f4678464a2',false),
  ('assistant.price_in_force(uuid,timestamp with time zone)','c1ab2055ec2a37b760c0fb8e15ef9d47ff444eddeac4ced4ad7fe11b28bf391b',false),
  ('assistant.holds_worker_lock(integer)','b38b1bead5ffe6cd25cb6311d8f335b2d09f0be46bd505beea9584aca7f9da13',false),
  ('assistant.token_cost_v1(assistant.price_revisions,bigint,bigint,bigint,bigint)','f12a32ec82dbfa8b328ad456bd0564a6b93e6b8a0e1f57980948c3f43d36c2da',false),
  ('assistant.check_model_call()','225ebc24edc3b644d730fc33d607d9bba16bc4bbee0ce342b44508c570e21dca',false),
  ('assistant.check_model_call_outcome()','06df5785d70e862d57110eb727f79123a6acc4f00cfacbaafe6b4164c461f6da',false),
  ('assistant.provider_state(uuid)','209cf47e0039768259964354779b37ee863554a06b4418a7888f3aeb77f312b0',false),
  ('assistant.test_state(uuid)','ea3b69ba64f03e51825c454c28d73add1731e65a7423c0b30577ab9b6d87eba1',false),
  ('assistant.close_test(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text)','8ca1329e65be8276ea26a35d55a1ddb8baeb525d985145e59fa51f280b722a2f',true),
  ('assistant.start_worker()','c602ae09dd466321d681b64026232baa0f61f32fbf01938aae3f24bcca83ed2b',true),
  ('assistant.provider_directory()','01e4fc1c1e5cff7c1f7cad431ad6da7ce27c90b75264005aaf615ae441845c42',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'agent_turns: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- Conversations (§5.1): one person's, in one scope, with one agent; only the sequence its owner has read through
-- changes, and only forward.
CREATE TABLE assistant.conversations(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,owner_id uuid NOT NULL,
 scope text NOT NULL CHECK(scope IN('api','exchange')),agent_id uuid NOT NULL,created_at ref_data.instant NOT NULL,
 read_through bigint NOT NULL CHECK(read_through>=0),
 UNIQUE(id,tenant_id,workspace_id),
 FOREIGN KEY(tenant_id,workspace_id,owner_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(agent_id,tenant_id,workspace_id) REFERENCES assistant.agents(id,tenant_id,workspace_id)
);
CREATE FUNCTION assistant.guard_conversation() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR(NEW.id,NEW.tenant_id,NEW.workspace_id,NEW.owner_id,NEW.scope,NEW.agent_id,NEW.created_at)
   IS DISTINCT FROM(OLD.id,OLD.tenant_id,OLD.workspace_id,OLD.owner_id,OLD.scope,OLD.agent_id,OLD.created_at)
  OR NEW.read_through<OLD.read_through THEN
  RAISE EXCEPTION 'A conversation changes only what its owner has read, and only forward' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_conversation BEFORE UPDATE OR DELETE ON assistant.conversations FOR EACH ROW EXECUTE FUNCTION assistant.guard_conversation();

-- Turns (§5.1, §7.2, §7.4): the question to one exact agent version under the grant that covered it at ask, the page
-- report, the knowledge mode asked for and the pinned-context record written once, the earlier turns included, the
-- runtime set, the fencing-token digest and worker generation, the session binding, and a state that only moves
-- forward. The version is immutable, so its limits are the turn's. A published answer that does not complete stays,
-- marked incomplete.
CREATE TABLE assistant.turns(
 id uuid PRIMARY KEY,conversation_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>0),
 owner_id uuid NOT NULL,operation_id uuid NOT NULL,agent_id uuid NOT NULL,agent_version integer NOT NULL,draft boolean NOT NULL,
 delegation_id uuid NOT NULL REFERENCES security.agent_delegations(id),
 question text NOT NULL CHECK(assistant.is_instructions(question)),
 page_report jsonb NOT NULL CHECK(jsonb_typeof(page_report)='object' AND octet_length(page_report::text)<=8192),
 catalog_id uuid NOT NULL,knowledge_mode text NOT NULL CHECK(knowledge_mode IN('current','retained')),requested_context uuid,frame_id uuid,
 context_id uuid,cutoff timestamptz,pins jsonb,recorded_at ref_data.instant,
 included_turns uuid[] NOT NULL CHECK(array_position(included_turns,NULL) IS NULL),
 runtime_set text NOT NULL CHECK(runtime_set~'^[a-z][a-z0-9_]{0,63}$'),
 state text NOT NULL CHECK(state IN('queued','running','cancel_requested','completed','failed','cancelled','interrupted')),
 outcome text CHECK(outcome~'^[a-z][a-z0-9_]{0,63}$'),
 cancel_code text CHECK(cancel_code IN('model_provider_disabled','model_disabled','agent_retired','access_changed','session_ended')),
 answer text NOT NULL,incomplete boolean NOT NULL,
 generation bigint NOT NULL CHECK(generation>0),token_digest text NOT NULL CHECK(token_digest~'^[0-9a-f]{64}$'),
 session_digest text NOT NULL CHECK(session_digest~'^[0-9a-f]{64}$'),profile text NOT NULL CHECK(assistant.is_display_name(profile)),
 session_ends_at ref_data.instant NOT NULL,
 asked_at ref_data.instant NOT NULL,started_at ref_data.instant,cancel_requested_at ref_data.instant,publication_refused_at ref_data.instant,
 ended_at ref_data.instant,
 UNIQUE(owner_id,operation_id),UNIQUE(conversation_id,ordinal),
 FOREIGN KEY(conversation_id,tenant_id,workspace_id) REFERENCES assistant.conversations(id,tenant_id,workspace_id),
 FOREIGN KEY(agent_id,agent_version) REFERENCES assistant.agent_versions(agent_id,version),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 CHECK((knowledge_mode='retained')=(requested_context IS NOT NULL)),
 CHECK(num_nulls(context_id,cutoff,recorded_at) IN(0,3) AND(pins IS NULL OR context_id IS NOT NULL)),
 CHECK(context_id IS NULL OR(frame_id IS NULL)=(pins IS NULL)),
 -- Only a pinned turn starts, and only a started one runs or completes.
 CHECK(state NOT IN('running','completed') OR context_id IS NOT NULL AND started_at IS NOT NULL),
 CHECK((state IN('completed','failed','cancelled','interrupted'))=(ended_at IS NOT NULL)),
 CHECK(CASE state WHEN 'failed' THEN outcome IS NOT NULL WHEN 'cancelled' THEN outcome IS NOT DISTINCT FROM cancel_code ELSE outcome IS NULL END),
 CHECK(state NOT IN('cancel_requested','cancelled') AND cancel_code IS NULL OR cancel_requested_at IS NOT NULL),
 -- A turn whose publication was refused can only fail access_changed, or be interrupted.
 CHECK(publication_refused_at IS NULL OR state IN('running','interrupted') OR state='failed' AND outcome='access_changed'),
 CHECK(NOT incomplete OR state<>'completed' AND answer<>'')
);
CREATE INDEX turns_active ON assistant.turns(tenant_id,workspace_id) WHERE state IN('queued','running','cancel_requested');
CREATE FUNCTION assistant.guard_turn() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE'
  OR(NEW.id,NEW.conversation_id,NEW.tenant_id,NEW.workspace_id,NEW.ordinal,NEW.owner_id,NEW.operation_id,NEW.agent_id,NEW.agent_version,NEW.draft,
     NEW.delegation_id,NEW.question,NEW.page_report,NEW.catalog_id,NEW.knowledge_mode,NEW.requested_context,NEW.frame_id,NEW.included_turns,
     NEW.runtime_set,NEW.generation,NEW.token_digest,NEW.session_digest,NEW.profile,NEW.session_ends_at,NEW.asked_at)
   IS DISTINCT FROM(OLD.id,OLD.conversation_id,OLD.tenant_id,OLD.workspace_id,OLD.ordinal,OLD.owner_id,OLD.operation_id,OLD.agent_id,OLD.agent_version,
     OLD.draft,OLD.delegation_id,OLD.question,OLD.page_report,OLD.catalog_id,OLD.knowledge_mode,OLD.requested_context,OLD.frame_id,OLD.included_turns,
     OLD.runtime_set,OLD.generation,OLD.token_digest,OLD.session_digest,OLD.profile,OLD.session_ends_at,OLD.asked_at)
  OR OLD.state IN('completed','failed','cancelled','interrupted')
  OR OLD.state<>NEW.state AND(OLD.state,NEW.state) NOT IN(('queued','running'),('queued','cancel_requested'),('queued','failed'),('queued','interrupted'),
   ('running','cancel_requested'),('running','completed'),('running','failed'),('running','interrupted'),
   ('cancel_requested','cancelled'),('cancel_requested','interrupted'))
  OR OLD.recorded_at IS NOT NULL AND(NEW.context_id,NEW.cutoff,NEW.pins,NEW.recorded_at) IS DISTINCT FROM(OLD.context_id,OLD.cutoff,OLD.pins,OLD.recorded_at)
  OR OLD.started_at IS NOT NULL AND NEW.started_at IS DISTINCT FROM OLD.started_at
  OR OLD.cancel_requested_at IS NOT NULL AND(NEW.cancel_requested_at,NEW.cancel_code) IS DISTINCT FROM(OLD.cancel_requested_at,OLD.cancel_code)
  OR(OLD.publication_refused_at IS NOT NULL OR OLD.state<>'running') AND NEW.publication_refused_at IS DISTINCT FROM OLD.publication_refused_at
  OR left(NEW.answer,length(OLD.answer))<>OLD.answer OR NEW.answer<>OLD.answer AND OLD.state<>'running' THEN
  RAISE EXCEPTION 'A turn only moves forward' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_turn BEFORE UPDATE OR DELETE ON assistant.turns FOR EACH ROW EXECUTE FUNCTION assistant.guard_turn();

-- Turn events (§8): each conversation's ordered stream, appended once and never changed.
CREATE TABLE assistant.turn_events(
 conversation_id uuid NOT NULL REFERENCES assistant.conversations(id),sequence bigint NOT NULL CHECK(sequence>0),
 turn_id uuid NOT NULL REFERENCES assistant.turns(id),
 kind text NOT NULL CHECK(kind IN('accepted','text','usage','completed','failed','cancelled','interrupted')),
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object' AND octet_length(payload::text)<=16384),
 recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(conversation_id,sequence)
);
CREATE TRIGGER immutable_turn_event BEFORE UPDATE OR DELETE ON assistant.turn_events FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- A model call is a test's or a turn's (§5.1): a test names its request identity, administrator and token; a turn's
-- call names its turn, its attempt and its conversation's latest event when it opened, and the turn's token
-- authenticates it. A cancelled turn's call ends cancelled.
ALTER TABLE assistant.model_calls ALTER COLUMN operation_id DROP NOT NULL,ALTER COLUMN actor_id DROP NOT NULL,ALTER COLUMN token_digest DROP NOT NULL,
 ADD COLUMN turn_id uuid REFERENCES assistant.turns(id),ADD COLUMN attempt integer CHECK(attempt>0),ADD COLUMN after_sequence bigint CHECK(after_sequence>0),
 ADD UNIQUE(turn_id,attempt),
 ADD CHECK(CASE WHEN turn_id IS NULL THEN num_nulls(operation_id,actor_id,token_digest)=0 AND num_nonnulls(attempt,after_sequence)=0
  ELSE num_nonnulls(operation_id,actor_id,token_digest)=0 AND num_nulls(attempt,after_sequence)=0 END);
ALTER TABLE assistant.model_call_outcomes DROP CONSTRAINT model_call_outcomes_outcome_check,
 ADD CONSTRAINT model_call_outcomes_outcome_check CHECK(outcome IN('succeeded','interrupted','cancelled','limit_elapsed','request_cancelled',
  'usage_unrecognized','model_refused','model_mismatch','context_window_exceeded','model_provider_authentication','model_provider_rate_limited',
  'model_provider_overloaded','model_provider_invalid_request','model_provider_transport','model_provider_response_too_large'));
-- The Providers directory's latest test per provider (0064), which leaves turn calls out.
CREATE INDEX model_calls_latest_test ON assistant.model_calls(provider_id,started_at DESC,id DESC) WHERE turn_id IS NULL;

-- A turn as its owner and the worker read it.
CREATE FUNCTION assistant.turn_state(t assistant.turns) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('id',t.id,'conversationId',t.conversation_id,'ordinal',t.ordinal,'agentId',t.agent_id,'version',t.agent_version,
  'draft',t.draft,'question',t.question,'pageReport',t.page_report,'state',t.state,'outcome',t.outcome,'answer',t.answer,'incomplete',t.incomplete,
  'context',jsonb_build_object('mode',t.knowledge_mode,'requestedContextId',t.requested_context,'frameId',t.frame_id,'contextId',t.context_id,
   'cutoff',t.cutoff,'recordedAt',t.recorded_at),
  'askedAt',t.asked_at,'startedAt',t.started_at,'cancelRequestedAt',t.cancel_requested_at,'endedAt',t.ended_at);
$$;
-- A turn's usage and cost (§5.3, §6.2, §8). Each dimension's count sums the final values its calls reported, next to
-- how many calls left it unknown; unrecognized usage leaves every dimension unknown. Cost is per exact currency
-- reference, never added across references, and unpriced while any call in that currency has none.
CREATE FUNCTION assistant.turn_usage(turn uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH calls AS(SELECT o.*,o.call_id IS NOT NULL AND o.usage_status<>'unrecognized' known,r.currency_catalog_id,r.currency_revision_id,r.currency_code
   FROM assistant.model_calls c JOIN assistant.price_revisions r ON(r.model_id,r.sequence)=(c.model_id,c.price_sequence)
   LEFT JOIN assistant.model_call_outcomes o ON o.call_id=c.id WHERE c.turn_id=turn_usage.turn)
 SELECT jsonb_build_object('calls',(SELECT count(*) FROM calls),
  'uncachedInput',(SELECT jsonb_build_object('count',coalesce(sum(uncached_input) FILTER(WHERE known),0),
    'unknownCalls',count(*) FILTER(WHERE NOT known OR uncached_input IS NULL)) FROM calls),
  'cacheRead',(SELECT jsonb_build_object('count',coalesce(sum(cache_read) FILTER(WHERE known),0),
    'unknownCalls',count(*) FILTER(WHERE NOT known OR cache_read IS NULL)) FROM calls),
  'cacheWrite',(SELECT jsonb_build_object('count',coalesce(sum(cache_write) FILTER(WHERE known),0),
    'unknownCalls',count(*) FILTER(WHERE NOT known OR cache_write IS NULL)) FROM calls),
  'output',(SELECT jsonb_build_object('count',coalesce(sum(output) FILTER(WHERE known),0),
    'unknownCalls',count(*) FILTER(WHERE NOT known OR output IS NULL)) FROM calls),
  'reasoning',(SELECT jsonb_build_object('count',coalesce(sum(reasoning) FILTER(WHERE known),0),
    'unknownCalls',count(*) FILTER(WHERE NOT known OR reasoning IS NULL)) FROM calls),
  'cost',coalesce((SELECT jsonb_agg(jsonb_build_object('currency',jsonb_build_object('catalogId',x.currency_catalog_id,'revisionId',x.currency_revision_id,
     'code',x.currency_code),'amount',CASE WHEN x.unpriced=0 THEN x.amount::text END,'unpricedCalls',x.unpriced)
    ORDER BY x.currency_catalog_id,x.currency_revision_id,x.currency_code)
   FROM(SELECT currency_catalog_id,currency_revision_id,currency_code,trim_scale(coalesce(sum(cost),0)) amount,count(*) FILTER(WHERE cost IS NULL) unpriced
    FROM calls GROUP BY currency_catalog_id,currency_revision_id,currency_code)x),'[]'::jsonb));
$$;
-- Appends a turn's next event to its conversation. Every append happens under the turn's row lock, and a conversation's
-- turns run one at a time, so the next sequence is the conversation's latest plus one.
CREATE FUNCTION assistant.turn_event(t assistant.turns,kind text,payload jsonb) RETURNS bigint
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 INSERT INTO assistant.turn_events SELECT t.conversation_id,coalesce(max(e.sequence),0)+1,t.id,turn_event.kind,turn_event.payload,clock_timestamp()
  FROM assistant.turn_events e WHERE e.conversation_id=t.conversation_id RETURNING sequence;
$$;
-- Ends a turn (§7.4), atomically and once: a published answer that does not complete is marked incomplete, and the
-- turn's usage and cost precede its terminal event. The worker closes its calls before it ends a turn, so only
-- recovery finds a call still open, which closes as interrupted with unknown usage.
CREATE FUNCTION assistant.end_turn_as(t assistant.turns,ending text,code text) RETURNS assistant.turns
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE n timestamptz:=clock_timestamp();
BEGIN
 INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,usage_status,uncached_input_source,cache_read_source,cache_write_source,output_source,reasoning_source)
  SELECT c.id,n,'interrupted','unknown','unknown','unknown','unknown','unknown','unknown' FROM assistant.model_calls c
  WHERE c.turn_id=t.id AND NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=c.id)
 ON CONFLICT(call_id) DO NOTHING;
 UPDATE assistant.turns x SET state=ending,outcome=code,ended_at=n,incomplete=ending<>'completed' AND x.answer<>'' WHERE x.id=t.id RETURNING x.* INTO t;
 PERFORM assistant.turn_event(t,'usage',assistant.turn_usage(t.id));
 PERFORM assistant.turn_event(t,ending,CASE WHEN code IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('code',code) END);
 RETURN t;
END $$;
-- The narrow cancel (§7.4): a queued or running turn moves to cancel requested, naming why when a source other than
-- its owner's Stop asked, and the worker is told. Any other turn is left as it is, as is one whose publication was
-- refused, which fails access_changed: the first cause stands.
CREATE FUNCTION assistant.request_cancel(turn uuid,code text) RETURNS void
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
BEGIN
 UPDATE assistant.turns x SET state='cancel_requested',cancel_code=code,cancel_requested_at=clock_timestamp()
  WHERE x.id=turn AND x.state IN('queued','running') AND x.publication_refused_at IS NULL;
 IF FOUND THEN PERFORM pg_notify('assistant_turn_cancel',turn::text);END IF;
END $$;
-- The call a turn has open, if any; the worker closes it before it ends the turn.
CREATE FUNCTION assistant.unclosed_call(turn uuid) RETURNS uuid
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT c.id FROM assistant.model_calls c WHERE c.turn_id=unclosed_call.turn AND NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=c.id);
$$;

-- What stops a call on an agent version's model at an instant (§7.1), as the first failing of: the version's model
-- version still the model's current one, the model enabled, qualified and priced, and its provider enabled and
-- qualified; null when nothing does. An ask refuses a retired agent itself, and Retire disables the agent's
-- principal, which a running turn's authority requires.
CREATE FUNCTION assistant.call_blocker(agent uuid,agent_version integer,instant timestamptz) RETURNS text
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE v assistant.agent_versions;m assistant.models;p assistant.providers;
BEGIN
 SELECT x.* INTO v FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(call_blocker.agent,call_blocker.agent_version);
 SELECT x.* INTO m FROM assistant.models x WHERE x.id=v.model_id;
 SELECT x.* INTO p FROM assistant.providers x WHERE x.id=m.provider_id;
 RETURN CASE
  WHEN v.model_version IS DISTINCT FROM(SELECT c.version FROM assistant.current_model_version(m.id) c) THEN 'model_changed'
  WHEN NOT m.enabled THEN 'model_disabled'
  WHEN NOT assistant.model_qualified(m.id) THEN 'model_unqualified'
  WHEN(assistant.price_in_force(m.id,instant)).kind IS DISTINCT FROM 'rates' THEN 'model_unpriced'
  WHEN NOT p.enabled THEN 'model_provider_disabled'
  WHEN NOT assistant.provider_qualified(p.id) THEN 'model_provider_unqualified' END;
END $$;
-- The owner's current authority over a running turn (§7.1), evaluated without the owner's request context:
-- UseAgents, CreateDelegations and every permission the version needs held now, which also needs the owner's principal
-- and membership enabled; the version still the owner's to use as it was at ask (an allowed role for a published
-- version, ManageAgents for a draft; a later Publish or save never fails it); the turn's grant unrevoked and
-- unexpired; the agent principal and membership enabled.
CREATE FUNCTION assistant.turn_authorized(t assistant.turns) RETURNS boolean
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT NOT EXISTS(SELECT 1 FROM unnest(ARRAY['UseAgents','CreateDelegations']||assistant.version_permissions(t.agent_id,t.agent_version)) x(permission)
   WHERE NOT security.principal_has_permission(t.owner_id,t.tenant_id,t.workspace_id,x.permission,clock_timestamp()))
  AND CASE WHEN t.draft THEN security.principal_has_permission(t.owner_id,t.tenant_id,t.workspace_id,'ManageAgents',clock_timestamp())
   ELSE EXISTS(SELECT 1 FROM assistant.agent_version_roles r JOIN security.role_grants g ON g.role_id=r.role_id
    WHERE(r.agent_id,r.version)=(t.agent_id,t.agent_version) AND(g.principal_id,g.tenant_id,g.workspace_id)=(t.owner_id,t.tenant_id,t.workspace_id)
     AND NOT g.revoked AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to) END
  AND EXISTS(SELECT 1 FROM security.agent_delegations d WHERE d.id=t.delegation_id AND NOT d.revoked
   AND d.granted_from<=clock_timestamp() AND clock_timestamp()<d.granted_to)
  AND EXISTS(SELECT 1 FROM assistant.agents a JOIN security.principals p ON p.id=a.principal_id JOIN security.workspace_memberships m
    ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.principal_id) WHERE a.id=t.agent_id AND p.enabled AND m.enabled);
$$;
-- The turn a worker write names, locked, when the write presents the turn's token while the generation that admitted
-- it is still the current one of the worker holding the lock (§7.4); otherwise agent_runtime_unavailable. A worker
-- start waits for the write to commit.
CREATE FUNCTION assistant.fenced_turn(turn uuid,token bytea) RETURNS assistant.turns
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE w assistant.worker_generation;t assistant.turns;
BEGIN
 SELECT x.* INTO w FROM assistant.worker_generation x FOR SHARE;
 SELECT x.* INTO t FROM assistant.turns x WHERE x.id=fenced_turn.turn FOR UPDATE;
 IF t.id IS NULL OR token IS NULL OR encode(sha256(token),'hex')<>t.token_digest OR t.generation<>w.generation
  OR NOT assistant.holds_worker_lock(w.worker_pid) THEN
  RAISE EXCEPTION 'agent_runtime_unavailable';
 END IF;
 RETURN t;
END $$;
-- A person's authority to ask an agent version (§7.1): the version exists, the agent is active, the version is theirs
-- to use, they hold every permission it needs, and a grant covers it, which it answers.
CREATE FUNCTION assistant.ask_authority(c security.request_contexts,a assistant.agents,agent_version integer) RETURNS uuid
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE missing text;delegation uuid;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM assistant.agent_versions x WHERE(x.agent_id,x.version)=(a.id,ask_authority.agent_version)) THEN
  RAISE EXCEPTION 'agent_not_allowed';
 END IF;
 IF a.retired_at IS NOT NULL THEN RAISE EXCEPTION 'agent_retired';END IF;
 IF NOT assistant.may_use(c,a,agent_version) THEN RAISE EXCEPTION 'agent_not_allowed';END IF;
 SELECT p INTO missing FROM unnest(assistant.version_permissions(a.id,agent_version)) p WHERE NOT security.has_permission(p) ORDER BY p COLLATE "C" LIMIT 1;
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'grant_permission_missing' USING HINT=missing;END IF;
 delegation:=assistant.covering_grant(c,a.id,agent_version);
 IF delegation IS NULL THEN RAISE EXCEPTION 'grant_required';END IF;
 RETURN delegation;
END $$;

-- Ask (§7.1, §7.4, §8), in the person's own context: the request names the agent version, an existing conversation of
-- theirs in this scope or none, the question, the page report and the knowledge to pin; the asking process names the
-- catalog, the session binding, the turn's token digest under its worker generation and the runtime set it applies.
-- A request identity is the person's: a repeat naming the same conversation, or none for a first ask, and the same
-- request answers with the turn its original admitted, as it stands; any other is turn_conflict. Admission, under the
-- workspace's advisory lock, first ends what is stuck, judged by the asking runtime set's deadline, and those endings
-- stand even when the ask is then refused; the person's authority is checked after every wait, as for every call and
-- with a covering grant, before the person's active turn and the workspace's limit are counted. An earlier turn's
-- answer carries no workspace data until tools record reads, so every earlier turn of the conversation is included.
CREATE FUNCTION assistant.ask_turn(operation uuid,scope text,conversation uuid,agent uuid,agent_version integer,question text,page_report jsonb,
 catalog uuid,knowledge_mode text,retained_context uuid,frame uuid,session_digest text,profile text,session_ends_at timestamptz,
 token_digest text,generation bigint,runtime_set text,question_limit integer,workspace_turns integer,admission_seconds integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);previous assistant.turns;w assistant.worker_generation;k assistant.conversations;
 a assistant.agents;delegation uuid;blocker text;n timestamptz;t assistant.turns;stale assistant.turns;earlier uuid[];
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
  RETURN jsonb_build_object('turn',assistant.turn_state(previous));
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
 SELECT coalesce(array_agg(x.id ORDER BY x.ordinal),ARRAY[]::uuid[]) INTO earlier FROM assistant.turns x WHERE x.conversation_id=k.id;
 INSERT INTO assistant.turns VALUES(gen_random_uuid(),k.id,c.tenant_id,c.workspace_id,cardinality(earlier)+1,c.actor_id,operation,a.id,agent_version,
  agent_version>coalesce(a.published_version,0),delegation,question,page_report,catalog,knowledge_mode,retained_context,frame,NULL,NULL,NULL,NULL,
  earlier,runtime_set,'queued',NULL,NULL,'',false,generation,token_digest,session_digest,profile,session_ends_at,n,NULL,NULL,NULL,NULL) RETURNING * INTO t;
 PERFORM assistant.turn_event(t,'accepted',jsonb_build_object('ordinal',t.ordinal,'agentId',t.agent_id,'version',t.agent_version));
 RETURN jsonb_build_object('turn',assistant.turn_state(t));
END $$;

-- The pinned-context record (§7.2), written once by the asking request in the person's context with the turn's
-- token: a turn over working data captures once, refusing while fewer than the runtime set's reserve of the catalog's
-- 4096 retained contexts would remain after it, which keeps them for page captures; a turn over a retained context
-- pins it as named, for a person who may read the catalog's market data. With an As of frame the record keeps the
-- frame's pins. A turn whose cancel was requested ends cancelled instead.
CREATE FUNCTION assistant.pin_turn(turn uuid,token bytea,effective_date date,pins jsonb,reserve integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);t assistant.turns;x market_data.temporal_contexts;context uuid;
BEGIN
 t:=assistant.fenced_turn(turn,token);
 -- The wait can outlive the authority.
 c:=assistant.grant_actor(true);
 IF t.owner_id<>c.actor_id OR(t.tenant_id,t.workspace_id)<>(c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 IF t.recorded_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF(t.frame_id IS NULL)<>(pins IS NULL) OR pins IS NOT NULL AND(jsonb_typeof(pins)<>'object' OR pins->>'frameId' IS DISTINCT FROM t.frame_id::text
   OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(pins) key)<>ARRAY['at','curveContracts','frameId','observationIds']
   OR jsonb_typeof(pins->'observationIds')<>'array' OR jsonb_typeof(pins->'curveContracts')<>'object' OR octet_length(pins::text)>65536) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
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
 RETURN jsonb_build_object('turn',assistant.turn_state(t));
END $$;

-- Starts a pinned, queued turn for the worker (§7.4); one whose cancel was requested ends cancelled instead. Answers
-- what the turn sends (§7.6): the version's instructions, model and limits, the question and each earlier turn it
-- includes, with its pinned context and how it ended.
CREATE FUNCTION assistant.start_turn(turn uuid,token bytea) RETURNS jsonb
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
  'earlier',coalesce((SELECT jsonb_agg(jsonb_build_object('ordinal',e.ordinal,'question',e.question,'answer',e.answer,'incomplete',e.incomplete,
     'state',e.state,'outcome',e.outcome,'context',jsonb_build_object('mode',e.knowledge_mode,'cutoff',e.cutoff,'frameId',e.frame_id)) ORDER BY e.ordinal)
   FROM assistant.turns e WHERE e.id=ANY(t.included_turns)),'[]'::jsonb)));
END $$;

-- Opens a running turn's call for the worker (§7.1, §7.5): its first, or a retry of a transport failure or overload
-- whose input is known and that published nothing, within the version's retries (retry_not_allowed otherwise); a retry
-- after the attempts' reported input exceeded the version's limit ends the turn limit_input_tokens. Tools bring further
-- calls. The model and its provider are locked before the turn, as their Disable, Replace key, Record terms, edits and
-- prices lock them before they cancel its turns, so an open waits for those and then sees them. Once its calls are
-- closed, a refused publication ends the turn access_changed before the retry rules and the input limit; after them,
-- the owner's current authority (access_changed) and then what stops a call end the turn rather than open. The call
-- pins the model's current capability version, the attested key and terms and the price in force at its start, and the
-- conversation's latest event; the answer carries what the adapter sends, the key still sealed. A turn whose cancel was
-- requested ends cancelled instead.
CREATE FUNCTION assistant.open_turn_call(turn uuid,token bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE v assistant.agent_versions;m assistant.models;t assistant.turns;last assistant.model_calls;ended assistant.model_call_outcomes;blocker text;
 r assistant.price_revisions;s assistant.attestations;k assistant.credentials;vendor assistant.vendor;call uuid:=gen_random_uuid();n timestamptz;
BEGIN
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
  -- Every earlier attempt was also a failure retried, so the attempts made are one more than the retries used.
  IF ended.outcome NOT IN('model_provider_transport','model_provider_overloaded') OR ended.usage_status<>'recognized' OR last.attempt>v.max_retries
   OR EXISTS(SELECT 1 FROM assistant.turn_events e WHERE e.conversation_id=t.conversation_id AND e.sequence>last.after_sequence AND e.kind='text') THEN
   RAISE EXCEPTION 'retry_not_allowed';
  END IF;
  -- The limit holds against the input the earlier attempts reported, each known since it was retried; nothing is
  -- estimated, so the attempt that crossed it was sent and billed (§7.5).
  IF(SELECT sum(o.uncached_input)+sum(o.cache_read)+sum(o.cache_write) FROM assistant.model_calls x JOIN assistant.model_call_outcomes o ON o.call_id=x.id
    WHERE x.turn_id=t.id)>v.max_input_tokens THEN
   RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','limit_input_tokens')));
  END IF;
 END IF;
 IF NOT assistant.turn_authorized(t) THEN RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'failed','access_changed')));END IF;
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
-- Closes a turn's call once, for the worker presenting the turn's token, in any state and without a request context,
-- so accounting finishes after a revocation, a session end or the turn's own end: its outcome, served identifier and
-- usage by dimension, priced under its pinned revision. It answers whether it recorded the outcome; a close after the
-- call closed, by the worker or by recovery, records nothing.
CREATE FUNCTION assistant.close_turn_call(call uuid,token bytea,outcome text,served text,usage_status text,
 uncached_input bigint,uncached_input_source text,cache_read bigint,cache_read_source text,cache_write bigint,cache_write_source text,
 output bigint,output_source text,reasoning bigint,reasoning_source text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE k assistant.model_calls;
BEGIN
 SELECT x.* INTO k FROM assistant.model_calls x JOIN assistant.turns t ON t.id=x.turn_id
  WHERE x.id=close_turn_call.call AND token IS NOT NULL AND encode(sha256(token),'hex')=t.token_digest;
 IF k.id IS NULL THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 INSERT INTO assistant.model_call_outcomes VALUES(k.id,clock_timestamp(),outcome,served,usage_status,uncached_input,uncached_input_source,
  cache_read,cache_read_source,cache_write,cache_write_source,output,output_source,reasoning,reasoning_source,
  CASE WHEN usage_status='recognized' THEN assistant.token_cost_v1((SELECT r FROM assistant.price_revisions r WHERE(r.model_id,r.sequence)=(k.model_id,k.price_sequence)),
   uncached_input,cache_read,cache_write,output) END)
 ON CONFLICT(call_id) DO NOTHING;
 RETURN FOUND;
END $$;
-- Appends answer text from the call in flight for the worker (§7.1, §7.5). It takes the epoch row FOR SHARE before
-- the turn, as every request does, so it waits for a control change in progress, and then rechecks the owner's
-- current authority: once a revocation has committed it publishes nothing, records the refusal and answers refused
-- access_changed, as it does every later append, so the turn can only fail access_changed or be interrupted. Once
-- the turn's cancel is requested it publishes nothing either. Either way the worker stops the call, closes it with the
-- usage it has and ends the turn. A chunk of at most 2,048 bytes always fits its event as escaped JSON.
CREATE FUNCTION assistant.append_turn_text(turn uuid,token bytea,chunk text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 t:=assistant.fenced_turn(turn,token);
 IF chunk IS NULL OR chunk='' OR octet_length(chunk)>2048 THEN RAISE EXCEPTION 'turn_input';END IF;
 IF t.state NOT IN('running','cancel_requested') OR assistant.unclosed_call(t.id) IS NULL THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.turn_state(t));END IF;
 IF t.publication_refused_at IS NULL AND NOT assistant.turn_authorized(t) THEN
  UPDATE assistant.turns x SET publication_refused_at=clock_timestamp() WHERE x.id=t.id RETURNING x.* INTO t;
 END IF;
 IF t.publication_refused_at IS NOT NULL THEN RETURN jsonb_build_object('turn',assistant.turn_state(t),'refused','access_changed');END IF;
 UPDATE assistant.turns x SET answer=x.answer||chunk WHERE x.id=t.id RETURNING x.* INTO t;
 RETURN jsonb_build_object('turn',assistant.turn_state(t),'sequence',assistant.turn_event(t,'text',jsonb_build_object('text',chunk)));
END $$;
-- Ends a turn for the worker once every call it opened is closed (§7.4): cancelled once its cancel was requested, and
-- failed access_changed once a publication was refused, whatever the worker asked; otherwise failed, from queued or
-- running, with the worker's code, or completed only while running after its call completed. Its terminal event
-- carries no business data, so it skips the publication recheck (§7.1).
CREATE FUNCTION assistant.end_turn(turn uuid,token bytea,ending text,code text) RETURNS jsonb
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
 IF t.state<>'running' OR NOT EXISTS(SELECT 1 FROM assistant.model_calls x JOIN assistant.model_call_outcomes o ON o.call_id=x.id
   WHERE x.turn_id=t.id AND o.outcome IN('succeeded','usage_unrecognized')) THEN
  RAISE EXCEPTION 'turn_state_conflict';
 END IF;
 RETURN jsonb_build_object('turn',assistant.turn_state(assistant.end_turn_as(t,'completed',NULL)));
END $$;
-- Requests a turn's cancel for the asking process when its session ends or its profile switches (§7.2).
CREATE FUNCTION assistant.end_turn_session(turn uuid,token bytea) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE t assistant.turns:=assistant.fenced_turn(turn,token);
BEGIN
 PERFORM assistant.request_cancel(t.id,'session_ended');
 RETURN jsonb_build_object('turn',assistant.turn_state((SELECT x FROM assistant.turns x WHERE x.id=t.id)));
END $$;
-- Stop (§7.4, §8): the owner requests the cancel of their own turn in the scope; anyone else's turn, or one in another
-- scope, is answered as an unknown one.
CREATE FUNCTION assistant.stop_turn(scope text,turn uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);t assistant.turns;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO t FROM assistant.turns x JOIN assistant.conversations k ON k.id=x.conversation_id
  WHERE x.id=stop_turn.turn AND(x.tenant_id,x.workspace_id,x.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,stop_turn.scope) FOR UPDATE OF x;
 IF t.id IS NULL THEN RAISE EXCEPTION 'turn_not_found';END IF;
 PERFORM assistant.request_cancel(t.id,NULL);
 RETURN jsonb_build_object('turn',assistant.turn_state((SELECT x FROM assistant.turns x WHERE x.id=t.id)));
END $$;

-- The sources that cancel a turn (§7.5), as triggers so every writer is covered: its provider switched off (Disable,
-- Replace key, Record terms), its model switched off, its agent's principal disabled (Retire, before its grants are
-- revoked) and its grant revoked (Revoke, a replacing grant). Each locks the turns it cancels in id order.
CREATE FUNCTION assistant.cancel_turns() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE t uuid;
BEGIN
 FOR t IN SELECT x.id FROM assistant.turns x WHERE x.state IN('queued','running') AND CASE TG_TABLE_NAME
   WHEN 'providers' THEN EXISTS(SELECT 1 FROM assistant.agent_versions v JOIN assistant.models m ON m.id=v.model_id
    WHERE(v.agent_id,v.version)=(x.agent_id,x.agent_version) AND m.provider_id=NEW.id)
   WHEN 'models' THEN EXISTS(SELECT 1 FROM assistant.agent_versions v WHERE(v.agent_id,v.version)=(x.agent_id,x.agent_version) AND v.model_id=NEW.id)
   WHEN 'principals' THEN EXISTS(SELECT 1 FROM assistant.agents a WHERE a.id=x.agent_id AND a.principal_id=NEW.id)
   WHEN 'agent_delegations' THEN x.delegation_id=NEW.id END ORDER BY x.id LOOP
  PERFORM assistant.request_cancel(t,TG_ARGV[0]);
 END LOOP;
 RETURN NULL;
END $$;
CREATE TRIGGER cancel_turns AFTER UPDATE OF enabled ON assistant.providers FOR EACH ROW WHEN(OLD.enabled AND NOT NEW.enabled)
 EXECUTE FUNCTION assistant.cancel_turns('model_provider_disabled');
CREATE TRIGGER cancel_turns AFTER UPDATE OF enabled ON assistant.models FOR EACH ROW WHEN(OLD.enabled AND NOT NEW.enabled)
 EXECUTE FUNCTION assistant.cancel_turns('model_disabled');
CREATE TRIGGER cancel_turns AFTER UPDATE OF enabled ON security.principals FOR EACH ROW WHEN(OLD.enabled AND NOT NEW.enabled AND NEW.kind='agent')
 EXECUTE FUNCTION assistant.cancel_turns('agent_retired');
CREATE TRIGGER cancel_turns AFTER UPDATE OF revoked ON security.agent_delegations FOR EACH ROW WHEN(NOT OLD.revoked AND NEW.revoked)
 EXECUTE FUNCTION assistant.cancel_turns('access_changed');

-- As in 0065, with every turn an earlier generation left unfinished interrupted once, after its open calls closed.
CREATE OR REPLACE FUNCTION assistant.start_worker() RETURNS bigint
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE w assistant.worker_generation;t assistant.turns;
BEGIN
 IF NOT assistant.holds_worker_lock(pg_backend_pid()) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 PERFORM 1 FROM assistant.worker_generation x FOR UPDATE;
 UPDATE assistant.worker_generation x SET generation=x.generation+1,worker_pid=pg_backend_pid(),started_at=clock_timestamp() RETURNING x.* INTO w;
 INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,usage_status,uncached_input_source,cache_read_source,cache_write_source,output_source,reasoning_source)
  SELECT c.id,w.started_at,'interrupted','unknown','unknown','unknown','unknown','unknown','unknown' FROM assistant.model_calls c
  WHERE c.generation<w.generation AND NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=c.id)
 ON CONFLICT(call_id) DO NOTHING;
 FOR t IN SELECT x.* FROM assistant.turns x WHERE x.generation<w.generation AND x.state IN('queued','running','cancel_requested') ORDER BY x.id FOR UPDATE LOOP
  PERFORM assistant.end_turn_as(t,'interrupted',NULL);
 END LOOP;
 RETURN w.generation;
END $$;
-- As in 0064, with each provider's latest test leaving turn calls out.
CREATE OR REPLACE FUNCTION assistant.provider_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();
BEGIN
 RETURN jsonb_build_object(
  'mappings',coalesce((SELECT jsonb_object_agg(a.vendor,jsonb_build_object('mapping',m.mapping,'confirmedOn',m.confirmed_on,'sources',m.sources,'efforts',e.efforts))
   FROM assistant.adapter_mappings a JOIN assistant.confirmed_mappings m ON(m.vendor,m.mapping)=(a.vendor,a.mapping)
   JOIN assistant.mapping_efforts e ON(e.vendor,e.mapping)=(a.vendor,a.mapping)),'{}'::jsonb),
  'providers',coalesce((SELECT jsonb_agg(assistant.provider_state(p.id) ORDER BY p.vendor) FROM assistant.providers p WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'tests',coalesce((SELECT jsonb_object_agg(p.vendor,assistant.test_state(t.id)||jsonb_build_object('underCurrentAttestation',
    EXISTS(SELECT 1 FROM assistant.current_attestation(p.id) a WHERE(a.credential_version,a.terms_version)=(t.credential_version,t.terms_version))))
   FROM assistant.providers p
   CROSS JOIN LATERAL(SELECT x.id,x.credential_version,x.terms_version FROM assistant.model_calls x WHERE x.provider_id=p.id AND x.turn_id IS NULL
    ORDER BY x.started_at DESC,x.id DESC LIMIT 1)t
   WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'{}'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,'vendor',a.vendor) ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,m.display_name actor,p.vendor FROM assistant.administration_audit a
    JOIN assistant.providers p ON p.id=a.provider_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND a.model_id IS NULL ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;

-- As in 0062, for a test's call only: a turn's call has no test token and closes with its turn's.
CREATE OR REPLACE FUNCTION assistant.close_test(call uuid,token bytea,outcome text,served text,usage_status text,
 uncached_input bigint,uncached_input_source text,cache_read bigint,cache_read_source text,cache_write bigint,cache_write_source text,
 output bigint,output_source text,reasoning bigint,reasoning_source text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c assistant.model_calls;
BEGIN
 SELECT * INTO c FROM assistant.model_calls x WHERE x.id=call AND x.turn_id IS NULL;
 IF NOT FOUND OR token IS NULL OR encode(sha256(token),'hex')<>c.token_digest THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 INSERT INTO assistant.model_call_outcomes VALUES(c.id,clock_timestamp(),outcome,served,usage_status,uncached_input,uncached_input_source,
  cache_read,cache_read_source,cache_write,cache_write_source,output,output_source,reasoning,reasoning_source,
  CASE WHEN usage_status='recognized' THEN assistant.token_cost_v1((SELECT r FROM assistant.price_revisions r WHERE(r.model_id,r.sequence)=(c.model_id,c.price_sequence)),
   uncached_input,cache_read,cache_write,output) END)
 ON CONFLICT(call_id) DO NOTHING;
END $$;

-- The owner's conversations in a scope (§8), newest activity first, each with its agent, latest turn and whether it
-- holds events after the sequence its owner has read through.
CREATE FUNCTION assistant.conversations(scope text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 RETURN jsonb_build_object('conversations',coalesce((SELECT jsonb_agg(jsonb_build_object('id',k.id,'agentId',k.agent_id,'agentName',a.name,
    'createdAt',k.created_at,'latestSequence',e.sequence,'readThrough',k.read_through,'unread',e.sequence>k.read_through,
    'latestTurn',assistant.turn_state(t)) ORDER BY e.recorded_at DESC,k.id)
  FROM assistant.conversations k JOIN assistant.agents a ON a.id=k.agent_id
   CROSS JOIN LATERAL(SELECT x.sequence,x.recorded_at FROM assistant.turn_events x WHERE x.conversation_id=k.id ORDER BY x.sequence DESC LIMIT 1)e
   CROSS JOIN LATERAL(SELECT x FROM assistant.turns x WHERE x.conversation_id=k.id ORDER BY x.ordinal DESC LIMIT 1)l(t)
  WHERE(k.tenant_id,k.workspace_id,k.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,conversations.scope)),'[]'::jsonb));
END $$;
-- One of the owner's conversations (§8): its turns and its events after a sequence.
CREATE FUNCTION assistant.conversation(scope text,conversation uuid,after bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);k assistant.conversations;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') OR after IS NULL OR after<0 THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO k FROM assistant.conversations x
  WHERE x.id=conversation.conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,conversation.scope);
 IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
 RETURN jsonb_build_object('conversation',jsonb_build_object('id',k.id,'agentId',k.agent_id,'createdAt',k.created_at,'readThrough',k.read_through),
  'turns',coalesce((SELECT jsonb_agg(assistant.turn_state(t) ORDER BY t.ordinal) FROM assistant.turns t WHERE t.conversation_id=k.id),'[]'::jsonb),
  'events',coalesce((SELECT jsonb_agg(jsonb_build_object('sequence',e.sequence,'turnId',e.turn_id,'kind',e.kind,'payload',e.payload,'at',e.recorded_at)
    ORDER BY e.sequence) FROM assistant.turn_events e WHERE e.conversation_id=k.id AND e.sequence>after),'[]'::jsonb));
END $$;
-- The owner's unfinished turn in a scope, if any (§8).
CREATE FUNCTION assistant.unfinished_turn(scope text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 RETURN jsonb_build_object('turn',(SELECT assistant.turn_state(t) FROM assistant.turns t JOIN assistant.conversations k ON k.id=t.conversation_id
  WHERE(t.tenant_id,t.workspace_id,t.owner_id,k.scope)=(c.tenant_id,c.workspace_id,c.actor_id,unfinished_turn.scope)
   AND t.state IN('queued','running','cancel_requested')));
END $$;
-- Marks one of the owner's conversations read through a sequence it holds; never backward.
CREATE FUNCTION assistant.mark_read(scope text,conversation uuid,through bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(false);k assistant.conversations;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'turn_input';END IF;
 SELECT x.* INTO k FROM assistant.conversations x
  WHERE x.id=mark_read.conversation AND(x.tenant_id,x.workspace_id,x.owner_id,x.scope)=(c.tenant_id,c.workspace_id,c.actor_id,mark_read.scope)
  FOR NO KEY UPDATE;
 IF k.id IS NULL THEN RAISE EXCEPTION 'conversation_not_found';END IF;
 IF through IS NULL OR through<0 OR through>(SELECT coalesce(max(e.sequence),0) FROM assistant.turn_events e WHERE e.conversation_id=k.id) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 UPDATE assistant.conversations x SET read_through=greatest(x.read_through,through) WHERE x.id=k.id RETURNING x.* INTO k;
 RETURN jsonb_build_object('conversation',jsonb_build_object('id',k.id,'readThrough',k.read_through));
END $$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' AND tablename IN('conversations','turns','turn_events') LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamptz,text,bigint,text,integer,integer,integer),
 assistant.pin_turn(uuid,bytea,date,jsonb,integer),assistant.start_turn(uuid,bytea),assistant.open_turn_call(uuid,bytea),
 assistant.close_turn_call(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text),
 assistant.append_turn_text(uuid,bytea,text),assistant.end_turn(uuid,bytea,text,text),assistant.end_turn_session(uuid,bytea),
 assistant.stop_turn(text,uuid),assistant.conversations(text),assistant.conversation(text,uuid,bigint),assistant.unfinished_turn(text),
 assistant.mark_read(text,uuid,bigint) TO openetrm_runtime;
COMMIT;
