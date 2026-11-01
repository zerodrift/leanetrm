-- Read rules decide visibility and authority once per statement (D282) instead of running the per-row visibility
-- and authority chain for every row. Each set function returns exactly the arguments for which the per-row function
-- it replaces in read rules returns true, under the request bound to this transaction. A cursor keeps the sets its
-- first fetch decided until it closes, within the request's authority deadline.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SET LOCAL search_path=pg_catalog;

-- The latest end of the principal's grants that give a permission and are in force at an instant; -infinity without
-- one. Only authority_until calls it: it reads any principal's grants, so no client role may execute it.
CREATE FUNCTION security.grant_until(actor uuid,tenant uuid,workspace uuid,permission text,checked timestamptz) RETURNS timestamptz
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT coalesce(max(g.granted_to),'-infinity') FROM security.principals p JOIN security.workspace_memberships m ON m.principal_id=p.id
 JOIN security.role_grants g ON(g.principal_id,g.tenant_id,g.workspace_id)=(m.principal_id,m.tenant_id,m.workspace_id)
 JOIN security.role_permissions rp ON rp.role_id=g.role_id
 WHERE p.id=actor AND p.enabled AND m.enabled AND m.tenant_id=tenant AND m.workspace_id=workspace
  AND NOT g.revoked AND g.granted_from<=checked AND checked<g.granted_to AND rp.permission=grant_until.permission;
$$;

-- The instant until which the bound request's authority for a permission holds: the earliest of every time bound the
-- per-row checks test. For any request: the token's expiry and the grants that give the permission; for an agent, its
-- delegation and its delegator's grants, CreateDelegations included. For a pilot run, also its lease, deadline and job
-- expiry, and, for every permission the run's operation requires, the job owner's, the scheduler service's and the job
-- delegator's grants with the job delegation's window. Each row compares the clock with this instant, so authority that
-- ends within a statement denies the rows read after it. Overlapping later grants are not chained: the cutoff may come
-- earlier, never later.
CREATE FUNCTION security.authority_until(permission text) RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;d security.agent_delegations;checked timestamptz=clock_timestamp();until timestamptz;
 run record;jd security.agent_delegations;required text;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.id IS NULL THEN RETURN '-infinity';END IF;
 until=least(c.expires_at,security.grant_until(c.actor_id,c.tenant_id,c.workspace_id,permission,checked));
 IF c.delegation_id IS NOT NULL THEN
  SELECT * INTO d FROM security.agent_delegations WHERE id=c.delegation_id;
  until=least(until,coalesce(d.granted_to,'-infinity'),security.grant_until(d.delegator_id,c.tenant_id,c.workspace_id,permission,checked),
   security.grant_until(d.delegator_id,c.tenant_id,c.workspace_id,'CreateDelegations',checked));
 END IF;
 SELECT j.*,r.lease_until,r.deadline_at,w.principal_id service INTO run FROM automation.pilot_run_contexts p
  JOIN automation.runs r ON r.id=p.run_id JOIN automation.jobs j ON j.id=r.job_id JOIN automation.workers w ON w.login=p.worker_login
  WHERE p.context_id=c.id;
 IF NOT FOUND THEN RETURN until;END IF;
 until=least(until,run.lease_until,run.deadline_at,run.expires_at);
 IF run.delegation_id IS NOT NULL THEN
  SELECT * INTO jd FROM security.agent_delegations WHERE id=run.delegation_id;
  until=least(until,coalesce(jd.granted_to,'-infinity'),security.grant_until(jd.delegator_id,run.tenant_id,run.workspace_id,'CreateDelegations',checked));
 END IF;
 FOREACH required IN ARRAY CASE run.operation WHEN 'close_book_risk_v1' THEN ARRAY['RunJobs','ReadMarketData','ReadTrades','RunValuation','RunScenario']
  ELSE automation.pilot_permissions() END LOOP
  until=least(until,security.grant_until(run.owner_id,run.tenant_id,run.workspace_id,required,checked),
   security.grant_until(run.service,run.tenant_id,run.workspace_id,required,checked));
  IF run.delegation_id IS NOT NULL THEN
   until=least(until,security.grant_until(jd.delegator_id,run.tenant_id,run.workspace_id,required,checked));
  END IF;
 END LOOP;
 RETURN until;
END$$;

-- The request's catalogs for a permission: market_access(catalog, permission) for every catalog.
CREATE FUNCTION security.market_catalogs(permission text) RETURNS SETOF uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.has_permission(permission) IS NOT TRUE OR NOT(permission='ReadMarketData' OR security.current_write_context()) THEN RETURN;END IF;
 RETURN QUERY SELECT s.catalog_id FROM security.request_contexts c JOIN security.market_catalog_scopes s ON(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
END$$;

-- reference_access(catalog, permission) for every catalog.
CREATE FUNCTION security.reference_catalogs(permission text) RETURNS SETOF uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.has_permission(permission) IS NOT TRUE OR NOT(permission='ReadMarketData' OR security.current_write_context()) THEN RETURN;END IF;
 RETURN QUERY SELECT s.catalog_id FROM security.request_contexts c JOIN security.reference_catalog_scopes s ON(s.tenant_id,s.workspace_id)=(c.tenant_id,c.workspace_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
   AND(permission='ReadMarketData' OR s.access_mode='owner');
END$$;

-- trading_access(catalog, permission) for every catalog.
CREATE FUNCTION security.trading_catalogs(permission text) RETURNS SETOF uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.has_permission(permission) IS NOT TRUE THEN RETURN;END IF;
 RETURN QUERY SELECT security.market_catalogs('ReadMarketData');
END$$;

-- The bound request's market cut, read once so the set queries below join events against constants: the
-- planner cannot know a request binds one context, and joining every retained context costs far more.
CREATE FUNCTION security.request_market_cut() RETURNS TABLE(catalog uuid, known_at timestamptz, effective_on date, known_through integer)
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT t.catalog_id,t.known_at,t.effective_on,m.known_through FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
$$;

-- reference_version_visible(catalog, revision) for every pair.
CREATE FUNCTION security.visible_reference_revisions() RETURNS TABLE(catalog uuid, revision uuid)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cut record;
BEGIN
 IF NOT EXISTS(
  SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN
  RETURN QUERY SELECT r.catalog_id,r.id FROM ref_data.revisions r WHERE r.catalog_id IN(SELECT security.reference_catalogs('ReadMarketData'));
  RETURN;
 END IF;
 SELECT * INTO cut FROM security.request_market_cut();
 IF cut.catalog IS NULL THEN RETURN;END IF;
 RETURN QUERY SELECT DISTINCT r.reference_catalog_id,r.reference_revision_id
  FROM market_data.revisions r
  JOIN market_data.knowledge_events e ON e.catalog_id=r.catalog_id AND e.revision_id=r.id AND e.observation_id IS NULL
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE r.catalog_id=cut.catalog
   AND r.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'))
   AND r.reference_catalog_id IN(SELECT security.reference_catalogs('ReadMarketData'))
   AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at
   AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to;
END$$;

-- market_fact_visible(catalog, revision, NULL) for every pair.
CREATE FUNCTION security.visible_market_revisions() RETURNS TABLE(catalog uuid, revision uuid)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cut record;
BEGIN
 IF NOT EXISTS(
  SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN
  RETURN QUERY SELECT r.catalog_id,r.id FROM market_data.revisions r WHERE r.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'));
  RETURN;
 END IF;
 SELECT * INTO cut FROM security.request_market_cut();
 IF cut.catalog IS NULL OR cut.catalog NOT IN(SELECT security.market_catalogs('ReadMarketData')) THEN RETURN;END IF;
 RETURN QUERY SELECT DISTINCT e.catalog_id,e.revision_id
  FROM market_data.knowledge_events e
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE e.catalog_id=cut.catalog AND e.observation_id IS NULL
   AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at
   AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to;
END$$;

-- trading_visible(catalog, ordinal, effective_at) is: (catalog, ordinal) in this set and effective_at <= the request's trading as-of.
CREATE FUNCTION security.visible_trading_publications() RETURNS TABLE(catalog uuid, ordinal integer)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 RETURN QUERY SELECT k.catalog_id,k.publication_order
  FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON k.catalog_id=t.catalog_id
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
   AND t.catalog_id IN(SELECT security.trading_catalogs('ReadTrades'))
   AND k.publication_order<=x.known_through AND k.known_from<=t.known_at;
END$$;
CREATE FUNCTION security.request_trading_as_of() RETURNS timestamptz
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT c.trading_as_of FROM security.request_contexts c
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
$$;

-- market_fact_visible(catalog, revision, observation) for every observation.
CREATE FUNCTION security.visible_observations() RETURNS TABLE(catalog uuid, revision uuid, observation uuid)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cut record;
BEGIN
 IF NOT EXISTS(
  SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN
  RETURN QUERY SELECT o.catalog_id,o.definition_revision_id,o.id FROM market_data.observations o WHERE o.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'));
  RETURN;
 END IF;
 SELECT * INTO cut FROM security.request_market_cut();
 IF cut.catalog IS NULL OR cut.catalog NOT IN(SELECT security.market_catalogs('ReadMarketData')) THEN RETURN;END IF;
 RETURN QUERY SELECT DISTINCT e.catalog_id,e.revision_id,e.observation_id
  FROM market_data.knowledge_events e
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  JOIN market_data.observations o ON(o.catalog_id,o.id)=(e.catalog_id,e.observation_id)
  WHERE e.catalog_id=cut.catalog AND e.observation_id IS NOT NULL
   AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at
   AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to
   AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC');
END$$;

-- side_schedule_visible(catalog, schedule) for every schedule, from the sets above rather than per-schedule checks.
-- Called directly, it lists the identities of the schedules the request may see, including any without readable rows.
CREATE FUNCTION security.visible_side_schedules() RETURNS TABLE(catalog uuid, schedule uuid)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE as_of timestamptz=security.request_trading_as_of();
BEGIN
 RETURN QUERY
 WITH pubs AS MATERIALIZED(SELECT * FROM security.visible_trading_publications()),
  revs AS MATERIALIZED(SELECT * FROM security.visible_reference_revisions()),
  refs AS MATERIALIZED(SELECT security.reference_catalogs('ReadMarketData') id),
  mkts AS MATERIALIZED(SELECT security.market_catalogs('ReadMarketData') id)
 SELECT p.catalog_id,p.schedule_id FROM trading.side_schedule_publications p
  JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(p.catalog_id,p.schedule_id)
  JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE EXISTS(SELECT 1 FROM trading.instrument_side_schedules m JOIN trading.instruments i ON(i.catalog_id,i.id)=(m.catalog_id,m.instrument_id)
    JOIN pubs v ON(v.catalog,v.ordinal)=(i.catalog_id,i.publication_order)
    WHERE(m.catalog_id,m.schedule_id)=(p.catalog_id,p.schedule_id) AND i.effective_at<=as_of)
   AND EXISTS(SELECT 1 FROM revs r WHERE(r.catalog,r.revision)=(s.reference_catalog_id,s.reference_revision_id))
   AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id)=(p.catalog_id,p.schedule_id)
    AND CASE a.kind WHEN 'reference' THEN a.authority_catalog_id NOT IN(SELECT id FROM refs) ELSE a.authority_catalog_id NOT IN(SELECT id FROM mkts) END);
END$$;

DO $$ DECLARE f text; BEGIN
 FOREACH f IN ARRAY ARRAY['security.market_catalogs(text)','security.reference_catalogs(text)','security.trading_catalogs(text)',
  'security.request_market_cut()','security.visible_reference_revisions()','security.visible_market_revisions()','security.visible_trading_publications()','security.request_trading_as_of()',
  'security.visible_observations()','security.visible_side_schedules()','security.authority_until(text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
 ALTER FUNCTION security.grant_until(uuid,uuid,uuid,text,timestamptz) OWNER TO openetrm_security_owner;
 REVOKE ALL ON FUNCTION security.grant_until(uuid,uuid,uuid,text,timestamptz) FROM PUBLIC;
END $$;

-- Every read rule that calls one of the per-row checks asks the statement's set instead; write rules keep
-- their per-row checks. The rules are printed and rewritten in UTC so their timestamp constants keep their
-- instants, and the migration fails if any read rule still calls a per-row check.
SET LOCAL TimeZone='UTC';
SET LOCAL DateStyle='ISO, YMD';
DO $$ DECLARE p record; e text; BEGIN
 FOR p IN SELECT polname,polrelid::regclass t,pg_get_expr(polqual,polrelid) q FROM pg_policy
  WHERE polcmd IN('r','*') AND pg_get_expr(polqual,polrelid) ~ 'security\.(reference_version_visible|market_fact_visible|trading_visible|market_access|reference_access|trading_access|side_schedule_visible)\(' LOOP
  e=regexp_replace(p.q,'security\.market_fact_visible\(([a-z_.]+), ([a-z_.]+), ([a-z_.]+)\)',
    'COALESCE((\1, \2, \3) IN (SELECT v.catalog, v.revision, v.observation FROM security.visible_observations() v) AND clock_timestamp() < (SELECT security.authority_until(''ReadMarketData'')), false)','g');
  e=regexp_replace(e,'security\.side_schedule_visible\(([a-z_.]+), ([a-z_.]+)\)',
    'COALESCE((\1, \2) IN (SELECT v.catalog, v.schedule FROM security.visible_side_schedules() v) AND clock_timestamp() < (SELECT security.authority_until(''ReadTrades'')) AND clock_timestamp() < (SELECT security.authority_until(''ReadMarketData'')), false)','g');
  p.q=e;
  e=regexp_replace(p.q,'security\.reference_version_visible\(([a-z_.]+), ([a-z_.]+)\)',
    'COALESCE((\1, \2) IN (SELECT v.catalog, v.revision FROM security.visible_reference_revisions() v) AND clock_timestamp() < (SELECT security.authority_until(''ReadMarketData'')), false)','g');
  e=regexp_replace(e,'security\.market_fact_visible\(([a-z_.]+), ([a-z_.]+), NULL::uuid\)',
    'COALESCE((\1, \2) IN (SELECT v.catalog, v.revision FROM security.visible_market_revisions() v) AND clock_timestamp() < (SELECT security.authority_until(''ReadMarketData'')), false)','g');
  e=regexp_replace(e,'security\.trading_visible\(([a-z_.]+), ([a-z_.]+), (\([a-z_.]+\)::timestamp with time zone|''[^'']*''::timestamp with time zone|[a-z_.]+)\)',
    'COALESCE((\1, \2) IN (SELECT v.catalog, v.ordinal FROM security.visible_trading_publications() v) AND \3 <= (SELECT security.request_trading_as_of()) AND clock_timestamp() < (SELECT security.authority_until(''ReadTrades'')) AND clock_timestamp() < (SELECT security.authority_until(''ReadMarketData'')), false)','g');
  e=regexp_replace(e,'security\.trading_access\(([a-z_.]+), (''[A-Za-z]+''::text)\)',
    'COALESCE(\1 IN (SELECT security.trading_catalogs(\2)) AND clock_timestamp() < (SELECT security.authority_until(\2)) AND clock_timestamp() < (SELECT security.authority_until(''ReadMarketData'')), false)','g');
  e=regexp_replace(e,'security\.(market|reference)_access\(([a-z_.]+), (''[A-Za-z]+''::text)\)',
    'COALESCE(\2 IN (SELECT security.\1_catalogs(\3)) AND clock_timestamp() < (SELECT security.authority_until(\3)), false)','g');
  EXECUTE format('ALTER POLICY %I ON %s USING (%s)',p.polname,p.t,e);
 END LOOP;
 IF EXISTS(SELECT 1 FROM pg_policy WHERE polcmd IN('r','*') AND pg_get_expr(polqual,polrelid) ~ '(reference_version_visible|market_fact_visible|trading_visible|market_access|reference_access|trading_access|side_schedule_visible)\(')
 THEN RAISE EXCEPTION 'A read rule still calls a per-row visibility or authority check';END IF;
END $$;
COMMIT;
