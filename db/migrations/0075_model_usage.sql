-- Page-assistant Usage (docs/ai-assistant.md §5.1, §5.3, §5.4, §9): ReadModelUsage, held by the access administrator,
-- reads model calls, tokens and exact cost by model, user or agent over a half-open range of local dates in a zone the
-- reader names, and an export records the report it carries in the model administration audit, which the Usage tab
-- lists. A call falls on the day of the zone whose bounds hold its start, as usage_figures states them; provider tests
-- are their own row in every view, so the views reconcile. Totals are per exact currency reference and never added
-- across references. token_cost_report_v1 gives a report's ratios and token_cost_display_v1 the amounts it shows. A
-- report shows users, agents, models, counts and cost, never content.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority helper a report relies on.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('security.has_permission(text)','db9e2e0741d351b4680be995a7b2de6f6019bf011ce9c240b252b845802dc9bf',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'model_usage: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- ReadModelUsage (§5.4): the Usage tab and its audited export.
INSERT INTO security.permissions VALUES('ReadModelUsage');
INSERT INTO security.role_permissions SELECT id,'ReadModelUsage' FROM security.roles WHERE code='access_administrator';
-- A usage export names no provider, model, agent or key.
ALTER TABLE assistant.administration_audit ADD CONSTRAINT administration_audit_usage_export
 CHECK(action<>'export_usage' OR num_nonnulls(provider_id,model_id,agent_id,key_digest)=0);
-- A report reads a workspace's calls by their start.
CREATE INDEX model_calls_started ON assistant.model_calls(tenant_id,workspace_id,started_at);

-- The bound request context of a human or service principal that currently holds ReadModelUsage. Agents never read
-- usage, so a delegated context is refused.
CREATE FUNCTION assistant.usage_actor() RETURNS security.request_contexts
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.id IS NULL OR c.delegation_id IS NOT NULL OR NOT security.has_permission('ReadModelUsage') THEN
  RAISE EXCEPTION 'No current model usage permission' USING ERRCODE='42501';
 END IF;
 RETURN c;
END $$;
-- What a report is grouped by, and its longest range in days: any calendar year.
CREATE FUNCTION assistant.usage_dimensions() RETURNS text[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT ARRAY['model','user','agent'] $$;
CREATE FUNCTION assistant.usage_days() RETURNS integer LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT 366 $$;

-- token_cost_display_v1 (§5.3): an exact cost as Usage shows it, its whole part grouped by thousands and its trailing
-- zeros trimmed. Nothing is rounded, so a nonzero cost never shows as zero.
CREATE FUNCTION assistant.token_cost_display_v1(cost numeric) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog SET standard_conforming_strings=on AS $$
 SELECT regexp_replace(split_part(t,'.',1),'(\d)(?=(\d{3})+$)','\1,','g')||coalesce('.'||nullif(split_part(t,'.',2),''),'')
 FROM(SELECT trim_scale(cost)::text t)x;
$$;
-- token_cost_report_v1 (§5.3): a report's ratio of exact amounts, rounded once to its places with ties to even and
-- written with exactly those places, or none without a positive whole: a cost per answer, the answers' cost over their
-- number, to 18 places; a share, a cost over its same-currency total, as a percentage to 2.
CREATE FUNCTION assistant.token_cost_report_v1(ratio text,part numeric,whole numeric) RETURNS text
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE places integer:=CASE ratio WHEN 'cost_per_answer' THEN 18 WHEN 'share' THEN 2 END;scaled numeric;q numeric;r numeric;digits text;
BEGIN
 IF places IS NULL OR part<0 THEN RAISE EXCEPTION 'token_cost_report_v1: no ratio % of %',ratio,part USING ERRCODE='22023';END IF;
 IF part IS NULL OR NOT whole>0 THEN RETURN NULL;END IF;
 -- Quotient and remainder are exact, so the one rounding is too.
 scaled:=part*CASE ratio WHEN 'share' THEN 100 ELSE 1 END*('1e'||places)::numeric;
 q:=div(scaled,whole);r:=scaled-q*whole;
 IF 2*r>whole OR 2*r=whole AND mod(q,2)=1 THEN q:=q+1;END IF;
 -- At least one whole digit; lpad never shortens, given the longer length.
 digits:=lpad(q::text,greatest(length(q::text),places+1),'0');
 RETURN left(digits,-places)||'.'||right(digits,places);
END $$;
-- An exact cost with the display token_cost_display_v1 gives it; none without one.
CREATE FUNCTION assistant.usage_amount(cost numeric) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN cost IS NOT NULL THEN jsonb_build_object('amount',trim_scale(cost)::text,'display',assistant.token_cost_display_v1(cost)) END;
$$;
-- A row's member in the reader's workspace, named and never described by anything it holds: a model by vendor,
-- identifier and display name, a user by name and login, an agent by name; provider tests are one row of their own.
CREATE FUNCTION assistant.usage_member(c security.request_contexts,kind text,member uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE kind WHEN 'tests' THEN jsonb_build_object('kind','tests')
  WHEN 'model' THEN(SELECT jsonb_build_object('kind','model','id',m.id,'vendor',p.vendor,'identifier',m.identifier,'name',m.display_name)
   FROM assistant.models m JOIN assistant.providers p ON p.id=m.provider_id WHERE m.id=usage_member.member)
  WHEN 'user' THEN(SELECT jsonb_build_object('kind','user','id',w.principal_id,'name',w.display_name,'login',w.login_handle) FROM security.workspace_memberships w
   WHERE(w.tenant_id,w.workspace_id,w.principal_id)=(c.tenant_id,c.workspace_id,usage_member.member))
  WHEN 'agent' THEN(SELECT jsonb_build_object('kind','agent','id',a.id,'name',a.name) FROM assistant.agents a WHERE a.id=usage_member.member) END;
$$;

-- A Usage report (§9) for a reader: the calls of the reader's workspace that start on the days [from_date, to_date)
-- of a zone the database names exactly, never localtime, posixrules, Factory or a posix/ or right/ name, which are
-- host or tzcode artifacts it may also list. Each day is the half-open interval from its start to the next day's
-- start, and a call falls on the day that holds its start, so the range holds exactly the calls between the UTC
-- bounds it states. A day starts at the earlier of its midnight and the instant after the previous date's last
-- microsecond, each read in the zone as PostgreSQL resolves local times: a skipped time takes the offset before the
-- transition, a repeated one the offset after it. That is the first instant the zone's clocks show the date, except
-- where a transition's skipped or repeated time strictly contains midnight in the tzdata release the database uses;
-- release 2026c has such dates only before 2011. Where the clock moves back across midnight, no single interval
-- holds a date's instants, so such a day ends where the next one starts. A date the zone skips is a day of no
-- length. The zone's own rules give every bound, never an abbreviation that shares its name. Per exact currency
-- reference, a row per model, user or agent and one for provider tests, then the total, each count calls by usage
-- state, sum each dimension's known tokens beside the calls that left it unknown, and sum the cost of its priced
-- calls beside the number without a cost, with that cost's share of the total's and the cost of priced calls a
-- later revision supersedes. Unrecognized usage leaves every dimension unknown, as a turn's usage counts it, and
-- without a priced call there is no cost, never zero. A turn counts through its calls in the cell, by terminal
-- state; answers are its completed turns, priced per answer only while each of their calls there has a cost, and
-- the other turns and the cost of their priced calls stand apart. Each day carries its UTC bounds and, per
-- currency, the cost of its priced calls, its calls and those without a cost.
CREATE FUNCTION assistant.usage_figures(c security.request_contexts,report_zone text,from_date date,to_date date,dimension text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE starts timestamptz[];span integer;
BEGIN
 IF report_zone IS NULL OR report_zone IN('localtime','posixrules','Factory') OR starts_with(report_zone,'posix/') OR starts_with(report_zone,'right/')
  OR NOT EXISTS(SELECT 1 FROM pg_timezone_names z WHERE z.name=report_zone) THEN RAISE EXCEPTION 'report_zone_invalid';END IF;
 IF from_date IS NULL OR to_date IS NULL OR NOT isfinite(from_date) OR NOT isfinite(to_date) OR to_date<=from_date OR to_date-from_date>assistant.usage_days()
 THEN RAISE EXCEPTION 'report_range_invalid';END IF;
 IF dimension IS NULL OR NOT dimension=ANY(assistant.usage_dimensions()) THEN RAISE EXCEPTION 'model_admin_input';END IF;
 span:=to_date-from_date;
 -- Each date's start, from_date's to to_date's, read in the zone: the earlier of its midnight and the instant after the
 -- previous date's last microsecond. The function's SET clause restores UTC on exit; every instant it renders is UTC.
 PERFORM set_config('TimeZone',report_zone,true);
 SELECT array_agg(least(d::timestamptz,(d::timestamp-interval '1 microsecond')::timestamptz+interval '1 microsecond') ORDER BY d)
  INTO starts FROM(SELECT from_date+n AS d FROM generate_series(0,span) n)g;
 PERFORM set_config('TimeZone','UTC',true);
 RETURN(WITH days AS(
   SELECT from_date+i-1 AS day,starts[i] AS day_start,starts[i+1] AS day_end FROM generate_series(1,span) i),
  calls AS(
   -- A call falls on the day whose bounds hold its start: width_bucket counts the day starts at or before it.
   SELECT x.id,x.turn_id,from_date+width_bucket(x.started_at,starts)-1 AS day,
    r.currency_catalog_id AS catalog,r.currency_revision_id AS revision,r.currency_code AS code,
    CASE WHEN o.call_id IS NULL THEN 'open' WHEN o.usage_status<>'recognized' THEN o.usage_status WHEN o.cost IS NULL THEN 'unpriced' ELSE 'priced' END AS status,
    o.cost,
    -- A priced call is superseded once a revision appended after the one it pinned is in force at its start.
    CASE WHEN o.cost IS NOT NULL THEN EXISTS(SELECT 1 FROM assistant.price_revisions p WHERE p.model_id=x.model_id AND p.sequence>x.price_sequence
     AND p.effective_from<=x.started_at AND(p.effective_to IS NULL OR x.started_at<p.effective_to)) END AS superseded,
    CASE WHEN o.usage_status<>'unrecognized' THEN o.uncached_input END AS uncached_input,CASE WHEN o.usage_status<>'unrecognized' THEN o.cache_read END AS cache_read,
    CASE WHEN o.usage_status<>'unrecognized' THEN o.cache_write END AS cache_write,CASE WHEN o.usage_status<>'unrecognized' THEN o.output END AS output,
    CASE WHEN o.usage_status<>'unrecognized' THEN o.reasoning END AS reasoning,t.state,
    CASE WHEN x.turn_id IS NULL THEN 'tests' ELSE dimension END AS kind,
    CASE WHEN x.turn_id IS NOT NULL THEN CASE dimension WHEN 'model' THEN x.model_id WHEN 'user' THEN t.owner_id ELSE t.agent_id END END AS member
   FROM assistant.model_calls x JOIN assistant.price_revisions r ON(r.model_id,r.sequence)=(x.model_id,x.price_sequence)
    LEFT JOIN assistant.model_call_outcomes o ON o.call_id=x.id LEFT JOIN assistant.turns t ON t.id=x.turn_id
   WHERE(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) AND x.started_at>=starts[1] AND x.started_at<starts[span+1]),
  cells AS(
   SELECT catalog,revision,code,kind,member,GROUPING(kind,member)<>0 AS whole,count(*) AS calls,count(*) FILTER(WHERE status='priced') AS priced,
    count(*) FILTER(WHERE status='unpriced') AS unpriced,count(*) FILTER(WHERE status='unknown') AS unknown,
    count(*) FILTER(WHERE status='unrecognized') AS unrecognized,count(*) FILTER(WHERE status='open') AS open,sum(cost) AS cost,
    count(*) FILTER(WHERE superseded) AS superseded_calls,sum(cost) FILTER(WHERE superseded) AS superseded_cost,
    sum(uncached_input) AS uncached_input,count(uncached_input) AS uncached_input_known,sum(cache_read) AS cache_read,count(cache_read) AS cache_read_known,
    sum(cache_write) AS cache_write,count(cache_write) AS cache_write_known,sum(output) AS output,count(output) AS output_known,
    sum(reasoning) AS reasoning,count(reasoning) AS reasoning_known,
    count(DISTINCT turn_id) FILTER(WHERE state='completed') AS completed,count(DISTINCT turn_id) FILTER(WHERE state='failed') AS failed,
    count(DISTINCT turn_id) FILTER(WHERE state='cancelled') AS cancelled,count(DISTINCT turn_id) FILTER(WHERE state='interrupted') AS interrupted,
    count(DISTINCT turn_id) FILTER(WHERE state IN('queued','running','cancel_requested')) AS unfinished,
    count(DISTINCT turn_id) FILTER(WHERE state<>'completed') AS other,
    sum(cost) FILTER(WHERE state='completed') AS answer_cost,count(DISTINCT turn_id) FILTER(WHERE state='completed' AND cost IS NULL) AS answers_without_cost,
    sum(cost) FILTER(WHERE state<>'completed') AS other_cost
   FROM calls GROUP BY GROUPING SETS((catalog,revision,code,kind,member),(catalog,revision,code))),
  figures AS(
   SELECT w.catalog,w.revision,w.code,w.kind,w.member,w.whole,jsonb_build_object(
     'calls',jsonb_build_object('total',w.calls,'priced',w.priced,'withoutCost',w.calls-w.priced,'unpriced',w.unpriced,'unknown',w.unknown,
      'unrecognized',w.unrecognized,'open',w.open),
     'tokens',jsonb_build_object('uncachedInput',jsonb_build_object('count',w.uncached_input::text,'unknownCalls',w.calls-w.uncached_input_known),
      'cacheRead',jsonb_build_object('count',w.cache_read::text,'unknownCalls',w.calls-w.cache_read_known),
      'cacheWrite',jsonb_build_object('count',w.cache_write::text,'unknownCalls',w.calls-w.cache_write_known),
      'output',jsonb_build_object('count',w.output::text,'unknownCalls',w.calls-w.output_known),
      'reasoning',jsonb_build_object('count',w.reasoning::text,'unknownCalls',w.calls-w.reasoning_known)),
     'pricedCost',assistant.usage_amount(w.cost),'pricedShare',assistant.token_cost_report_v1('share',w.cost,t.cost),
     'superseded',jsonb_build_object('calls',w.superseded_calls,'cost',assistant.usage_amount(w.superseded_cost)),
     'turns',CASE WHEN w.kind IS DISTINCT FROM 'tests' THEN jsonb_build_object('completed',w.completed,'failed',w.failed,'cancelled',w.cancelled,
      'interrupted',w.interrupted,'unfinished',w.unfinished,'other',w.other,'answersPricedCost',assistant.usage_amount(w.answer_cost),
      'answersWithoutCost',w.answers_without_cost,
      'costPerAnswer',CASE WHEN w.answers_without_cost=0 THEN(SELECT jsonb_build_object('amount',v,'display',assistant.token_cost_display_v1(v::numeric))
        FROM assistant.token_cost_report_v1('cost_per_answer',w.answer_cost,w.completed) v WHERE v IS NOT NULL) END,
      'otherPricedCost',assistant.usage_amount(w.other_cost)) END) AS figures
   FROM cells w JOIN cells t ON(t.catalog,t.revision,t.code)=(w.catalog,w.revision,w.code) AND t.whole),
  daily AS(
   SELECT k.catalog,k.revision,k.code,jsonb_agg(jsonb_build_object('date',d.day,'pricedCost',CASE WHEN x.calls IS NULL THEN assistant.usage_amount(0)
     ELSE assistant.usage_amount(x.cost) END,'calls',coalesce(x.calls,0),'withoutCost',coalesce(x.without_cost,0)) ORDER BY d.day) AS days
   FROM(SELECT DISTINCT catalog,revision,code FROM calls)k CROSS JOIN days d
    LEFT JOIN(SELECT catalog,revision,code,day,sum(cost) AS cost,count(*) AS calls,count(*) FILTER(WHERE status<>'priced') AS without_cost
     FROM calls GROUP BY catalog,revision,code,day)x ON(x.catalog,x.revision,x.code,x.day)=(k.catalog,k.revision,k.code,d.day)
   GROUP BY k.catalog,k.revision,k.code)
  SELECT jsonb_build_object('reportFormat','usage_report_v1','zone',report_zone,'from',from_date,'to',to_date,'utcFrom',starts[1],'utcTo',starts[span+1],
   'by',dimension,'generatedAt',clock_timestamp(),'costMethod','token_cost_v1','displayMethod','token_cost_display_v1','ratioMethod','token_cost_report_v1',
   'days',(SELECT jsonb_agg(jsonb_build_object('date',day,'from',day_start,'to',day_end) ORDER BY day) FROM days),
   'currencies',coalesce((SELECT jsonb_agg(jsonb_build_object('currency',jsonb_build_object('catalogId',f.catalog,'revisionId',f.revision,'code',f.code),
     'total',f.figures,
     'rows',(SELECT jsonb_agg(jsonb_build_object('member',m.member)||g.figures ORDER BY g.kind='tests',lower(m.member->>'name' COLLATE "C"),g.member)
      FROM figures g CROSS JOIN LATERAL(SELECT assistant.usage_member(c,g.kind,g.member) AS member)m
      WHERE(g.catalog,g.revision,g.code)=(f.catalog,f.revision,f.code) AND NOT g.whole),
     'daily',(SELECT y.days FROM daily y WHERE(y.catalog,y.revision,y.code)=(f.catalog,f.revision,f.code))) ORDER BY f.catalog,f.revision,f.code COLLATE "C")
    FROM figures f WHERE f.whole),'[]'::jsonb)));
END $$;

-- The Usage tab (§9): what a report is grouped by, its longest range and the latest 100 exports.
CREATE FUNCTION assistant.usage_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.usage_actor();
BEGIN
 RETURN jsonb_build_object('dimensions',to_jsonb(assistant.usage_dimensions()),'maxDays',assistant.usage_days(),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'zone',a.request->'zone',
    'from',a.request->'from','to',a.request->'to','by',a.request->'by') ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.request,m.display_name actor FROM assistant.administration_audit a
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND a.action='export_usage' ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
-- A report for the reader, recording nothing.
CREATE FUNCTION assistant.usage_report(report_zone text,from_date date,to_date date,dimension text) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.usage_figures(assistant.usage_actor(),report_zone,from_date,to_date,dimension);
$$;
-- An export's earlier outcome under the same request identity: the report it recorded if the same reader sent the same
-- request, a conflict if the identity was used for any other administrative change in Settings › AI or Users & access,
-- otherwise none. The reader's principal row serializes their commands, so an identical export sent while its original
-- still runs waits for it and answers as it does.
CREATE FUNCTION assistant.usage_replay(c security.request_contexts,operation uuid,request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE previous assistant.administration_audit;
BEGIN
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'model_admin_input';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 SELECT * INTO previous FROM assistant.administration_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation);
 -- The wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.usage_actor();
 IF FOUND THEN
  IF previous.actor_id<>c.actor_id OR previous.request<>request THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
  RETURN previous.after_state;
 END IF;
 IF EXISTS(SELECT 1 FROM security.administration_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation)) THEN
  RAISE EXCEPTION 'model_admin_conflict';
 END IF;
 RETURN NULL;
END $$;
-- Export (§9): the report a CSV carries, recorded with its request in the model administration audit when it was made.
-- A repeat answers with the recorded report and records nothing.
CREATE FUNCTION assistant.export_usage(operation uuid,report_zone text,from_date date,to_date date,dimension text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.usage_actor();previous jsonb;report jsonb;
 request jsonb:=jsonb_build_object('action','export_usage','operationId',operation,'zone',report_zone,'from',from_date,'to',to_date,'by',dimension);
BEGIN
 previous:=assistant.usage_replay(c,operation,request);
 IF previous IS NOT NULL THEN RETURN previous;END IF;
 report:=assistant.usage_figures(c,report_zone,from_date,to_date,dimension);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,request,before_state,after_state,recorded_at)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'export_usage',request,NULL,report,(report->>'generatedAt')::timestamptz);
 -- Storage waits can also outlive the authority checked above.
 PERFORM assistant.usage_actor();
 RETURN report;
END $$;

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION assistant.usage_directory(),assistant.usage_report(text,date,date,text),assistant.export_usage(uuid,text,date,date,text)
 TO openetrm_runtime;
COMMIT;
