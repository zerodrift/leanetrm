-- Page-assistant provider Test records (docs/ai-assistant.md §5.1, §5.3, §7.1, §7.4, §9). A Test opens a model
-- call in its administrator's request context under the agent worker's current generation. Before any byte is sent,
-- the call pins the model's current capability version, the provider's attested key and terms, and the price
-- revision in force at its start. The handler holding the test's token closes it once, without a request context,
-- with its outcome, usage by dimension and exact cost (token_cost_v1). One agent worker per database holds advisory
-- lock 730849160. Each worker start advances the generation and closes, once, every call an earlier generation left
-- open, as interrupted with unknown usage. Calls and outcomes are immutable. Like 0051, the open rechecks the actor's
-- authority after its principal and row waits, before it replays an earlier outcome, and after its writes, and every
-- definer this migration creates renders instants in UTC.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority, qualification and pricing helpers these operations rely on.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.provider_qualified(uuid)','784555e5e2dd89746140ef3ab3a1a87cc06cb7b4482b4dfcaa0ff0a113d0be18',false),
  ('assistant.current_attestation(uuid)','142978f0af07358286fbd725af2e4b8e073a04143401f30cef8db1452e78a51c',false),
  ('assistant.model_qualified(uuid)','bcbf74756334e92c969281f24fd98362f369f499c285bdc5cdcdf92e8d4fb73a',false),
  ('assistant.current_model_version(uuid)','bc966aa2132643261ac9bdf28fc7fd3094e296f4da6617b2a69b89f4678464a2',false),
  ('assistant.price_in_force(uuid,timestamp with time zone)','c1ab2055ec2a37b760c0fb8e15ef9d47ff444eddeac4ced4ad7fe11b28bf391b',false),
  ('assistant.is_model_identifier(text)','68d215ea50a0bcdcb0b01a3729d827e49060b53d1fec00d00dd8e69cdddd8749',false)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'provider_tests: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- The agent-worker generation (§7.4): one row for the database, advanced by each worker start, naming the backend of
-- the worker that started it.
CREATE TABLE assistant.worker_generation(
 id boolean PRIMARY KEY DEFAULT true CHECK(id),generation bigint NOT NULL CHECK(generation>=0),
 worker_pid integer,started_at ref_data.instant,
 CHECK((generation=0)=(worker_pid IS NULL) AND(worker_pid IS NULL)=(started_at IS NULL))
);
INSERT INTO assistant.worker_generation VALUES(true,0,NULL,NULL);
CREATE FUNCTION assistant.guard_worker_generation() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR NEW.id IS DISTINCT FROM OLD.id OR NEW.generation<>OLD.generation+1 THEN
  RAISE EXCEPTION 'The agent-worker generation advances one at a time' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_worker_generation BEFORE UPDATE OR DELETE ON assistant.worker_generation FOR EACH ROW EXECUTE FUNCTION assistant.guard_worker_generation();
-- Whether the backend `pid` holds the agent-worker lock. A backend connects to one database, which scopes its
-- advisory locks.
CREATE FUNCTION assistant.holds_worker_lock(pid integer) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM pg_locks l WHERE l.locktype='advisory' AND l.classid=0 AND l.objid=730849160 AND l.objsubid=1 AND l.granted
  AND l.pid=holds_worker_lock.pid);
$$;

-- A model call (§5.1); for now a provider Test, opened in its administrator's context and closed by the handler that
-- holds its token. It names the model's exact capability version, the attested key and terms, the price revision
-- pinned at its start and the method that prices it. Its requested identifier, accepted served identifiers and usage
-- mapping are those of that version.
CREATE TABLE assistant.model_calls(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 token_digest text NOT NULL CHECK(token_digest~'^[0-9a-f]{64}$'),generation bigint NOT NULL CHECK(generation>0),
 provider_id uuid NOT NULL,credential_version integer NOT NULL,terms_version integer NOT NULL,
 model_id uuid NOT NULL,model_version integer NOT NULL,
 reasoning text NOT NULL,max_output_tokens integer NOT NULL CHECK(max_output_tokens BETWEEN 1 AND 10000000),
 price_sequence integer NOT NULL,cost_method text NOT NULL CHECK(cost_method='token_cost_v1'),started_at ref_data.instant NOT NULL,
 UNIQUE(tenant_id,workspace_id,operation_id),
 FOREIGN KEY(model_id,provider_id,tenant_id,workspace_id) REFERENCES assistant.models(id,provider_id,tenant_id,workspace_id),
 FOREIGN KEY(model_id,model_version) REFERENCES assistant.model_versions(model_id,version),
 FOREIGN KEY(provider_id,credential_version,terms_version) REFERENCES assistant.attestations(provider_id,credential_version,terms_version),
 FOREIGN KEY(model_id,price_sequence) REFERENCES assistant.price_revisions(model_id,sequence)
);
CREATE TRIGGER immutable_model_call BEFORE UPDATE OR DELETE ON assistant.model_calls FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
-- What a call's own columns cannot show, for the owner too: its version accepts its reasoning effort and allows its
-- output limit, and its pinned revision is a rate set. Each side is immutable, so this holds once inserted; a missing
-- version or revision is left to the foreign keys.
CREATE FUNCTION assistant.check_model_call() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM assistant.model_versions v WHERE(v.model_id,v.version)=(NEW.model_id,NEW.model_version)
   AND(NOT NEW.reasoning=ANY(v.reasoning_efforts) OR NEW.max_output_tokens>v.output_limit))
  OR EXISTS(SELECT 1 FROM assistant.price_revisions r WHERE(r.model_id,r.sequence)=(NEW.model_id,NEW.price_sequence) AND r.kind<>'rates')
 THEN RAISE EXCEPTION 'model_calls: an effort or output limit its version does not allow, or a pinned revision without rates' USING ERRCODE='23514';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER allowed_call BEFORE INSERT ON assistant.model_calls FOR EACH ROW EXECUTE FUNCTION assistant.check_model_call();

-- token_cost_v1 (§5.3): the exact cost of a call's tokens under a rate set. Rates are per million tokens. When uncached
-- input, cache read and cache write together exceed the tier threshold, the tier's rates price every dimension, output
-- included. Tokens in a dimension the set does not offer leave the call unpriced (null), and an unknown count never
-- yields a partial cost. Reasoning is part of output and never priced separately. numeric keeps every cost exact, to
-- 18 places, at any magnitude.
CREATE FUNCTION assistant.token_cost_v1(r assistant.price_revisions,uncached_input bigint,cache_read bigint,cache_write bigint,output bigint) RETURNS numeric
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN bool_or(d.tokens IS NULL OR d.tokens>0 AND d.rate IS NULL) THEN NULL ELSE trim_scale(coalesce(sum(d.tokens*d.rate),0)*0.000001) END
 FROM(SELECT r.tier_threshold IS NOT NULL AND uncached_input::numeric+cache_read+cache_write>r.tier_threshold AS tiered)t,
  LATERAL(VALUES(uncached_input,CASE WHEN t.tiered THEN r.tier_uncached_input ELSE r.uncached_input END),
   (cache_read,CASE WHEN t.tiered THEN r.tier_cache_read ELSE r.cache_read END),
   (cache_write,CASE WHEN t.tiered THEN r.tier_cache_write ELSE r.cache_write END),
   (output,CASE WHEN t.tiered THEN r.tier_output ELSE r.output END))d(tokens,rate);
$$;
-- One dimension's count (§6.2): reported by the vendor, a structural zero that was not billed, or unknown, which has
-- no count and never becomes zero.
CREATE FUNCTION assistant.is_token_count(tokens bigint,source text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(CASE source WHEN 'reported' THEN tokens>=0 WHEN 'not_billed' THEN tokens=0 WHEN 'unknown' THEN tokens IS NULL END,false)
$$;
-- A call's outcome, written once: by the handler holding its token, or as interrupted by the next worker. Recognized
-- usage knows every priced dimension and reports no more reasoning than output. A success is recognized and served.
CREATE TABLE assistant.model_call_outcomes(
 call_id uuid PRIMARY KEY REFERENCES assistant.model_calls(id),closed_at ref_data.instant NOT NULL,
 outcome text NOT NULL CHECK(outcome IN('succeeded','interrupted','limit_elapsed','request_cancelled','usage_unrecognized','model_refused',
  'model_mismatch','context_window_exceeded','model_provider_authentication','model_provider_rate_limited','model_provider_overloaded',
  'model_provider_invalid_request','model_provider_transport','model_provider_response_too_large')),
 served text CHECK(assistant.is_model_identifier(served)),
 usage_status text NOT NULL CHECK(usage_status IN('recognized','unrecognized','unknown')),
 uncached_input bigint,uncached_input_source text NOT NULL,cache_read bigint,cache_read_source text NOT NULL,
 cache_write bigint,cache_write_source text NOT NULL,output bigint,output_source text NOT NULL,reasoning bigint,reasoning_source text NOT NULL,
 -- As token_cost_v1 gives it, without trailing zeros.
 cost numeric CHECK(scale(cost)=min_scale(cost)),
 CHECK(assistant.is_token_count(uncached_input,uncached_input_source) AND assistant.is_token_count(cache_read,cache_read_source)
  AND assistant.is_token_count(cache_write,cache_write_source) AND assistant.is_token_count(output,output_source)
  AND assistant.is_token_count(reasoning,reasoning_source)),
 CHECK(usage_status<>'recognized' OR num_nulls(uncached_input,cache_read,cache_write,output)=0 AND NOT reasoning>output),
 CHECK(outcome<>'succeeded' OR usage_status='recognized' AND served IS NOT NULL),
 CHECK(outcome NOT IN('model_mismatch','usage_unrecognized') OR usage_status='unrecognized')
);
CREATE TRIGGER immutable_model_call_outcome BEFORE UPDATE OR DELETE ON assistant.model_call_outcomes FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
-- What an outcome's own columns cannot show, for the owner too: a served identifier its call neither requested nor
-- accepts appears exactly with a mismatch, and the cost is the call's cost method applied to its pinned revision and
-- counts, so only recognized usage is priced and unrecognized or unknown usage stores no cost, never a partial one. A
-- missing call is left to the foreign key.
CREATE FUNCTION assistant.check_model_call_outcome() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE c assistant.model_calls;allowed text[];r assistant.price_revisions;
BEGIN
 SELECT * INTO c FROM assistant.model_calls x WHERE x.id=NEW.call_id;
 IF NOT FOUND THEN RETURN NEW;END IF;
 SELECT m.identifier||v.accepted_served INTO allowed FROM assistant.models m JOIN assistant.model_versions v ON(v.model_id,v.version)=(m.id,c.model_version)
  WHERE m.id=c.model_id;
 SELECT * INTO r FROM assistant.price_revisions x WHERE(x.model_id,x.sequence)=(c.model_id,c.price_sequence);
 IF(NEW.outcome='model_mismatch')=(NEW.served=ANY(allowed))
  OR NEW.cost IS DISTINCT FROM(CASE WHEN NEW.usage_status='recognized' THEN assistant.token_cost_v1(r,NEW.uncached_input,NEW.cache_read,NEW.cache_write,NEW.output) END)
 THEN RAISE EXCEPTION 'model_call_outcomes: a served identifier its call does not accept without a mismatch, or a cost its method does not give' USING ERRCODE='23514';END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER priced_outcome BEFORE INSERT ON assistant.model_call_outcomes FOR EACH ROW EXECUTE FUNCTION assistant.check_model_call_outcome();

-- Starts the next agent-worker generation for the session holding the agent-worker lock, and closes once, as
-- interrupted with unknown usage, every call an earlier generation left open (§7.4).
CREATE FUNCTION assistant.start_worker() RETURNS bigint
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE w assistant.worker_generation;
BEGIN
 IF NOT assistant.holds_worker_lock(pg_backend_pid()) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 UPDATE assistant.worker_generation x SET generation=x.generation+1,worker_pid=pg_backend_pid(),started_at=clock_timestamp() RETURNING x.* INTO w;
 INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,usage_status,uncached_input_source,cache_read_source,cache_write_source,output_source,reasoning_source)
  SELECT c.id,w.started_at,'interrupted','unknown','unknown','unknown','unknown','unknown','unknown' FROM assistant.model_calls c
  WHERE c.generation<w.generation AND NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=c.id)
 ON CONFLICT(call_id) DO NOTHING;
 RETURN w.generation;
END $$;
-- A Test record as Settings › AI reads it: the call and, once it is closed, its outcome, usage and cost.
CREATE FUNCTION assistant.test_state(call uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',c.id,'operationId',c.operation_id,'vendor',p.vendor,'modelId',c.model_id,'identifier',m.identifier,
  'capabilityVersion',c.model_version,'mapping',v.mapping,'reasoning',c.reasoning,'maxOutputTokens',c.max_output_tokens,
  'credentialVersion',c.credential_version,'termsVersion',c.terms_version,'priceSequence',c.price_sequence,
  'testedBy',w.display_name,'startedAt',c.started_at,'closedAt',o.closed_at,'outcome',o.outcome,'served',o.served,
  'usage',CASE WHEN o.call_id IS NOT NULL THEN jsonb_build_object('status',o.usage_status,
   'uncachedInput',jsonb_build_object('count',o.uncached_input,'source',o.uncached_input_source),
   'cacheRead',jsonb_build_object('count',o.cache_read,'source',o.cache_read_source),
   'cacheWrite',jsonb_build_object('count',o.cache_write,'source',o.cache_write_source),
   'output',jsonb_build_object('count',o.output,'source',o.output_source),
   'reasoning',jsonb_build_object('count',o.reasoning,'source',o.reasoning_source)) END,
  'cost',CASE WHEN o.call_id IS NOT NULL THEN jsonb_build_object('method',c.cost_method,
   'status',CASE o.usage_status WHEN 'unknown' THEN 'usage_unknown' WHEN 'unrecognized' THEN 'usage_unrecognized'
    ELSE CASE WHEN o.cost IS NULL THEN 'cost_unpriced' ELSE 'priced' END END,
   'amount',o.cost::text,'currency',jsonb_build_object('catalogId',r.currency_catalog_id,'revisionId',r.currency_revision_id,'code',r.currency_code)) END)
 FROM assistant.model_calls c JOIN assistant.models m ON m.id=c.model_id JOIN assistant.providers p ON p.id=c.provider_id
  JOIN assistant.model_versions v ON(v.model_id,v.version)=(c.model_id,c.model_version)
  JOIN assistant.price_revisions r ON(r.model_id,r.sequence)=(c.model_id,c.price_sequence)
  JOIN security.workspace_memberships w ON(w.tenant_id,w.workspace_id,w.principal_id)=(c.tenant_id,c.workspace_id,c.actor_id)
  LEFT JOIN assistant.model_call_outcomes o ON o.call_id=c.id
 WHERE c.id=call;
$$;
-- Runs one actor's commands in order, so a retry that overlaps its original waits for it. After that wait and the
-- lookup it rechecks the actor's authority, then returns the test an identical request by the same actor opened, or
-- null for a new request; any other reuse of the request identity conflicts.
CREATE FUNCTION assistant.replayed_test(c security.request_contexts,operation uuid,model uuid,reasoning text) RETURNS assistant.model_calls
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE previous assistant.model_calls;
BEGIN
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'model_admin_input';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 SELECT * INTO previous FROM assistant.model_calls x WHERE(x.tenant_id,x.workspace_id,x.operation_id)=(c.tenant_id,c.workspace_id,operation);
 -- Assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF previous.actor_id<>c.actor_id OR(previous.model_id,previous.reasoning) IS DISTINCT FROM(model,reasoning) THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 RETURN previous;
END $$;
-- Opens a provider Test for the acting administrator (§9): one fixed synthetic prompt to a model on a qualified
-- provider, either of which may be disabled. The call pins the model's current capability version, the provider's
-- attested key and terms and the price revision in force at its start, under the current worker generation, and the
-- result carries what the handler sends, the attested key still sealed. A retry returns a closed test's outcome,
-- with or without a worker generation; an open test is in progress. Neither sends anything.
CREATE FUNCTION assistant.open_test(operation uuid,model uuid,reasoning text,output_limit integer,token_digest text,generation bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();previous assistant.model_calls;w assistant.worker_generation;m assistant.models;
 v assistant.model_versions;a assistant.attestations;k assistant.credentials;r assistant.price_revisions;vendor assistant.vendor;
 call uuid:=gen_random_uuid();n timestamptz;
BEGIN
 previous:=assistant.replayed_test(c,operation,model,reasoning);
 IF previous.id IS NOT NULL THEN
  IF NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=previous.id) THEN RAISE EXCEPTION 'test_in_progress';END IF;
  RETURN jsonb_build_object('test',assistant.test_state(previous.id));
 END IF;
 IF model IS NULL OR output_limit IS NULL OR output_limit NOT BETWEEN 1 AND 10000000 OR token_digest IS NULL OR token_digest!~'^[0-9a-f]{64}$'
 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 -- A worker start waits for this open to commit, then closes the call unless its handler already has.
 SELECT * INTO w FROM assistant.worker_generation FOR SHARE;
 IF generation IS NULL OR w.generation<>generation OR NOT assistant.holds_worker_lock(w.worker_pid) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 SELECT x.* INTO m FROM assistant.models x JOIN assistant.providers p ON p.id=x.provider_id
  WHERE x.id=open_test.model AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR SHARE;
 -- The row waits can outlive the authority; assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 IF NOT assistant.model_qualified(m.id) THEN RAISE EXCEPTION 'model_unqualified';END IF;
 IF NOT assistant.provider_qualified(m.provider_id) THEN RAISE EXCEPTION 'model_provider_unqualified';END IF;
 SELECT * INTO v FROM assistant.current_model_version(m.id);
 IF reasoning IS NULL OR NOT reasoning=ANY(v.reasoning_efforts) THEN RAISE EXCEPTION 'parameter_required';END IF;
 n:=clock_timestamp();
 r:=assistant.price_in_force(m.id,n);
 IF r.kind IS DISTINCT FROM 'rates' THEN RAISE EXCEPTION 'model_unpriced';END IF;
 SELECT * INTO a FROM assistant.current_attestation(m.provider_id);
 SELECT * INTO k FROM assistant.credentials x WHERE(x.provider_id,x.version)=(a.provider_id,a.credential_version);
 SELECT p.vendor INTO vendor FROM assistant.providers p WHERE p.id=m.provider_id;
 INSERT INTO assistant.model_calls VALUES(call,c.tenant_id,c.workspace_id,operation,c.actor_id,token_digest,generation,
  a.provider_id,a.credential_version,a.terms_version,m.id,v.version,reasoning,least(output_limit,v.output_limit),r.sequence,'token_cost_v1',n);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN jsonb_build_object('opened',jsonb_build_object('call',call,'identifier',m.identifier,'acceptedServed',v.accepted_served,'mapping',v.mapping,
  'reasoning',reasoning,'maxOutputTokens',least(output_limit,v.output_limit),'provider',a.provider_id,'vendor',vendor,'credentialVersion',k.version,
  'nonce',encode(k.nonce,'hex'),'ciphertext',encode(k.ciphertext,'hex'),'tag',encode(k.tag,'hex')));
END $$;
-- Closes a test once for the handler holding its token, with or without the authority that opened it (§7.4, §9): its
-- outcome, served identifier and usage by dimension, priced under its pinned revision. A close after the test is
-- closed, by its handler or by recovery, changes nothing.
CREATE FUNCTION assistant.close_test(call uuid,token bytea,outcome text,served text,usage_status text,
 uncached_input bigint,uncached_input_source text,cache_read bigint,cache_read_source text,cache_write bigint,cache_write_source text,
 output bigint,output_source text,reasoning bigint,reasoning_source text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c assistant.model_calls;
BEGIN
 SELECT * INTO c FROM assistant.model_calls x WHERE x.id=call;
 IF NOT FOUND OR token IS NULL OR encode(sha256(token),'hex')<>c.token_digest THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 INSERT INTO assistant.model_call_outcomes VALUES(c.id,clock_timestamp(),outcome,served,usage_status,uncached_input,uncached_input_source,
  cache_read,cache_read_source,cache_write,cache_write_source,output,output_source,reasoning,reasoning_source,
  CASE WHEN usage_status='recognized' THEN assistant.token_cost_v1((SELECT r FROM assistant.price_revisions r WHERE(r.model_id,r.sequence)=(c.model_id,c.price_sequence)),
   uncached_input,cache_read,cache_write,output) END)
 ON CONFLICT(call_id) DO NOTHING;
END $$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' AND tablename IN('worker_generation','model_calls','model_call_outcomes') LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.start_worker(),assistant.open_test(uuid,uuid,text,integer,text,bigint),
 assistant.close_test(uuid,bytea,text,text,text,bigint,text,bigint,text,bigint,text,bigint,text,bigint,text) TO openetrm_runtime;
COMMIT;
