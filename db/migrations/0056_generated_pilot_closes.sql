-- Explicit generated pilot closes. No prices, profiles, identities or schedules
-- are installed. Generated marks are simulation data, never official settlement.
BEGIN;
ALTER TABLE market_data.observations ADD UNIQUE(catalog_id,id,definition_revision_id,series_id);
CREATE TABLE market_data.market_closes (
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 80),at ref_data.instant NOT NULL,
 kind text NOT NULL CHECK(kind IN('retained_input','generated_pilot')),
 actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,request jsonb NOT NULL,
 anchor_close_id uuid,run_id uuid REFERENCES automation.runs(id),generator text,preflight_context_id uuid,
 publication_order integer NOT NULL CHECK(publication_order>0),publication_xid xid8 NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,definition_revision_id),
 UNIQUE(catalog_id,actor_id,request_id),UNIQUE(run_id),UNIQUE(catalog_id,publication_order,publication_xid),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,anchor_close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,preflight_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 CHECK((kind='retained_input' AND anchor_close_id IS NULL AND run_id IS NULL AND generator IS NULL AND preflight_context_id IS NULL)
    OR(kind='generated_pilot' AND anchor_close_id IS NOT NULL AND run_id IS NOT NULL AND generator='seeded_clearing_tick_move_v1' AND preflight_context_id IS NOT NULL))
);
CREATE INDEX market_close_recent_frames ON market_data.market_closes(catalog_id,definition_revision_id,at DESC,id DESC);
CREATE TABLE market_data.market_close_pins (
 catalog_id uuid NOT NULL,close_id uuid NOT NULL,definition_revision_id uuid NOT NULL,series_id uuid NOT NULL,
 observation_id uuid NOT NULL,input_use text NOT NULL CHECK(input_use IN('variable','frozen')),
 PRIMARY KEY(catalog_id,close_id,series_id,input_use),UNIQUE(catalog_id,close_id,observation_id,input_use),
 FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,observation_id,definition_revision_id,series_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id,series_id)
);
CREATE TABLE market_data.market_close_members (
 catalog_id uuid NOT NULL,close_id uuid NOT NULL,definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 contract_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),
 PRIMARY KEY(catalog_id,close_id,curve_code,contract_id),UNIQUE(catalog_id,close_id,curve_code,ordinal),
 FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE TABLE market_data.market_close_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES market_data.market_closes(catalog_id,publication_order,publication_xid)
);
CREATE FUNCTION security.market_close_visible(catalog uuid,close_id uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.market_closes s
 WHERE(s.catalog_id,s.id)=(catalog,close_id)
 AND security.market_fact_visible(s.catalog_id,s.definition_revision_id,NULL)
 AND security.reference_version_visible(s.reference_catalog_id,s.reference_revision_id)
 AND NOT EXISTS(SELECT 1 FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id)=(s.catalog_id,s.id)
  AND NOT security.market_fact_visible(p.catalog_id,p.definition_revision_id,p.observation_id))
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.market_close_receipts r ON(r.catalog_id,r.publication_order,r.publication_xid)=(s.catalog_id,s.publication_order,s.publication_xid)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
 AND t.catalog_id=catalog AND r.known_from<=t.known_at)));
$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['market_closes','market_close_pins','market_close_members','market_close_receipts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_close BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_security_owner',t);
  IF t='market_closes' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_close_visible(catalog_id,id))',t);
  ELSIF t='market_close_receipts' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM market_data.market_closes s WHERE(s.catalog_id,s.publication_order)=(market_close_receipts.catalog_id,market_close_receipts.publication_order)))',t);
  ELSE
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_close_visible(catalog_id,close_id))',t);
  END IF;
 END LOOP;
END$$;
-- The named close readers qualify whole immutable closes once per boundary,
-- rather than repeating that complete check for every native pin under RLS.
-- Raw table policies remain unchanged. This operation cannot change context.
CREATE FUNCTION security.read_market_close_parts(catalog uuid,close_ids uuid[],expected_context uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE request security.request_contexts;payload jsonb;
BEGIN
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF request.id IS NULL OR request.expires_at<=clock_timestamp() OR request.market_context_id IS DISTINCT FROM expected_context
 OR(expected_context IS NOT NULL AND request.market_catalog_id IS DISTINCT FROM catalog) OR catalog IS NULL OR close_ids IS NULL
 OR cardinality(close_ids) NOT BETWEEN 1 AND 128 OR array_position(close_ids,NULL) IS NOT NULL
 OR(SELECT count(DISTINCT requested.id) FROM unnest(close_ids)requested(id))<>cardinality(close_ids)
 OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) LEFT JOIN market_data.market_closes s ON(s.catalog_id,s.id)=(catalog,requested.id)
 WHERE s.id IS NULL OR NOT security.market_close_visible(catalog,s.id))
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 SELECT jsonb_agg(jsonb_build_object('close_id',s.id,
  'pins',(SELECT jsonb_agg(jsonb_build_object('observation_id',p.observation_id,'input_use',p.input_use) ORDER BY p.series_id,p.input_use COLLATE "C")
   FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id)=(s.catalog_id,s.id)),
  'members',(SELECT jsonb_agg(jsonb_build_object('curve_code',m.curve_code,'contract_id',m.contract_id) ORDER BY m.curve_code COLLATE "C",m.ordinal)
   FROM market_data.market_close_members m WHERE(m.catalog_id,m.close_id)=(s.catalog_id,s.id))) ORDER BY s.id)
 INTO payload FROM market_data.market_closes s WHERE s.catalog_id=catalog AND s.id=ANY(close_ids);
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
 AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) WHERE NOT security.market_close_visible(catalog,requested.id))
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 RETURN payload;
END;$$;
CREATE FUNCTION security.register_market_close(catalog uuid,definition uuid,operation uuid,close_name text,close_at timestamptz,pins jsonb,members jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;r market_data.revisions;previous market_data.market_closes;payload jsonb;identity uuid;ordinal integer;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'PublishMarketData') THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition);
 IF NOT security.market_access(catalog,'PublishMarketData') OR NOT security.market_fact_visible(catalog,definition,NULL)
 OR NOT security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id) THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR close_name IS NULL OR length(btrim(close_name)) NOT BETWEEN 1 AND 80
 OR close_at IS NULL OR NOT isfinite(close_at) OR close_at>clock_timestamp() OR jsonb_typeof(pins) IS DISTINCT FROM 'array'
 OR jsonb_array_length(pins) NOT BETWEEN 1 AND 20000 OR jsonb_typeof(members) IS DISTINCT FROM 'array' OR jsonb_array_length(members) NOT BETWEEN 1 AND 20000
 THEN RAISE EXCEPTION 'pilot_close_input' USING ERRCODE='P0001';END IF;
 payload=jsonb_build_object('definition',definition,'name',close_name,'at',close_at,'pins',pins,'members',members);
 SELECT * INTO previous FROM market_data.market_closes WHERE catalog_id=catalog AND actor_id=c.actor_id AND request_id=operation;
 IF FOUND THEN
  IF previous.request<>payload THEN RAISE EXCEPTION 'pilot_close_conflict' USING ERRCODE='P0001';END IF;
  IF NOT security.market_close_visible(catalog,previous.id) THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
  RETURN previous.id;
 END IF;
 IF r.state IS DISTINCT FROM 'sealed' OR NOT EXISTS(SELECT 1 FROM market_data.revision_periods p WHERE(p.catalog_id,p.revision_id)=(catalog,definition)
 AND p.effective_from<=(close_at AT TIME ZONE 'UTC')::date AND(close_at AT TIME ZONE 'UTC')::date<p.effective_to)
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(pins) x("seriesId" uuid,"observationId" uuid,"inputUse" text)
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id,o.definition_revision_id,o.series_id)=(catalog,x."observationId",definition,x."seriesId")
 WHERE o.id IS NULL OR o.observed_at>close_at OR NOT security.market_fact_visible(catalog,definition,o.id)
 OR x."inputUse" IS NULL OR x."inputUse" NOT IN('variable','frozen') OR(x."inputUse"='frozen')<>(o.role IN('final_contract_settlement','index_fixing'))
 OR(x."inputUse"='frozen' AND o.publication_state<>'final'))
 THEN RAISE EXCEPTION 'pilot_close_inputs' USING ERRCODE='P0001';END IF;
 identity=gen_random_uuid();SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.market_closes WHERE catalog_id=catalog;
 INSERT INTO market_data.market_closes VALUES(catalog,identity,definition,r.reference_catalog_id,r.reference_revision_id,close_name,close_at,'retained_input',c.actor_id,operation,payload,NULL,NULL,NULL,NULL,ordinal,pg_current_xact_id());
 INSERT INTO market_data.market_close_pins SELECT catalog,identity,definition,x."seriesId",x."observationId",x."inputUse" FROM jsonb_to_recordset(pins)x("seriesId" uuid,"observationId" uuid,"inputUse" text);
 INSERT INTO market_data.market_close_members SELECT catalog,identity,definition,x.curve,x."contractId",x.ordinal FROM jsonb_to_recordset(members)x(curve text,"contractId" uuid,ordinal integer);
 IF NOT security.market_access(catalog,'PublishMarketData') THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 RETURN identity;
END;$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_pilot_close_context;
REVOKE ALL ON FUNCTION security.capture_pre_pilot_close_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.market_close_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT s.* FROM market_data.market_closes s LEFT JOIN market_data.market_close_receipts r USING(catalog_id,publication_order)
 WHERE s.catalog_id=catalog AND r.publication_order IS NULL ORDER BY s.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.market_close_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_pilot_close_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('market_close_visible','read_market_close_parts','register_market_close','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
-- Profiles contain typed relationships. JSON stores only the explicit scalar
-- numerical/model policy contract, not references to business objects.
CREATE TABLE automation.pilot_close_profiles (
 id uuid NOT NULL,version integer NOT NULL CHECK(version=1),tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,prior_close_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,
 seed integer NOT NULL CHECK(seed>=0),generator text NOT NULL CHECK(generator='seeded_clearing_tick_move_v1'),
 tick_basis text NOT NULL CHECK(tick_basis='clearing_increment'),parameter_policy text NOT NULL CHECK(parameter_policy='carry_exact_anchor_v1'),
 frozen_policy text NOT NULL CHECK(frozen_policy='carry_exact_anchor_frozen_facts_v1'),membership_policy text NOT NULL CHECK(membership_policy='retained_live_members_v1'),
 calendar_policy text NOT NULL CHECK(calendar_policy='every_utc_calendar_day_v1'),close_policy text NOT NULL CHECK(close_policy='utc_due_instant_v1'),
 valuation_policy jsonb NOT NULL CHECK(jsonb_typeof(valuation_policy)='object'
  AND valuation_policy-ARRAY['curveNumericalPolicy','riskNumericalPolicy','futuresConvention','swapConvention','rateNumericalPolicy','discountInterpolationPolicy','bumpConvention','bumpBasis','bumpTicks']='{}'::jsonb
  AND valuation_policy ?& ARRAY['curveNumericalPolicy','riskNumericalPolicy','futuresConvention','swapConvention','rateNumericalPolicy','discountInterpolationPolicy','bumpConvention','bumpBasis','bumpTicks']
  AND jsonb_typeof(valuation_policy->'curveNumericalPolicy')='string' AND jsonb_typeof(valuation_policy->'riskNumericalPolicy')='string'
  AND jsonb_typeof(valuation_policy->'futuresConvention')='string' AND jsonb_typeof(valuation_policy->'swapConvention')='string'
  AND jsonb_typeof(valuation_policy->'rateNumericalPolicy')='string' AND jsonb_typeof(valuation_policy->'discountInterpolationPolicy')='string'
  AND jsonb_typeof(valuation_policy->'bumpConvention')='string' AND jsonb_typeof(valuation_policy->'bumpBasis')='string' AND jsonb_typeof(valuation_policy->'bumpTicks')='number'),
 PRIMARY KEY(id,version),UNIQUE(id,version,tenant_id,workspace_id),UNIQUE(id,version,catalog_id,definition_revision_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id),
 FOREIGN KEY(catalog_id,prior_close_id,definition_revision_id) REFERENCES market_data.market_closes(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_close_books (
 profile_id uuid NOT NULL,profile_version integer NOT NULL,catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,book_id uuid NOT NULL,
 PRIMARY KEY(profile_id,profile_version,book_id),
 FOREIGN KEY(profile_id,profile_version,catalog_id,definition_revision_id) REFERENCES automation.pilot_close_profiles(id,version,catalog_id,definition_revision_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id)
);
CREATE TABLE automation.pilot_close_curves (
 profile_id uuid NOT NULL,profile_version integer NOT NULL,catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 PRIMARY KEY(profile_id,profile_version,curve_code),
 FOREIGN KEY(profile_id,profile_version,catalog_id,definition_revision_id) REFERENCES automation.pilot_close_profiles(id,version,catalog_id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
CREATE TABLE automation.pilot_close_moves (
 profile_id uuid NOT NULL,profile_version integer NOT NULL,catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 maximum_ticks integer NOT NULL CHECK(maximum_ticks BETWEEN 1 AND 10000),
 PRIMARY KEY(profile_id,profile_version,curve_code),
 FOREIGN KEY(profile_id,profile_version,catalog_id,definition_revision_id) REFERENCES automation.pilot_close_profiles(id,version,catalog_id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
ALTER TABLE automation.jobs ADD COLUMN pilot_profile_id uuid;
ALTER TABLE automation.jobs ADD COLUMN pilot_profile_version integer;
ALTER TABLE automation.jobs ADD FOREIGN KEY(pilot_profile_id,pilot_profile_version,tenant_id,workspace_id) REFERENCES automation.pilot_close_profiles(id,version,tenant_id,workspace_id);
ALTER TABLE automation.jobs DROP CONSTRAINT jobs_operation_check;
ALTER TABLE automation.jobs DROP CONSTRAINT jobs_check;
ALTER TABLE automation.jobs ADD CHECK(operation IN('worker_check','swarm_job','generated_pilot_close_v1'));
ALTER TABLE automation.jobs ADD CHECK(
 (operation='worker_check' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL)
 OR(operation='swarm_job' AND profile_id IS NOT NULL AND profile_version IS NOT NULL AND pilot_profile_id IS NULL AND pilot_profile_version IS NULL)
 OR(operation='generated_pilot_close_v1' AND profile_id IS NULL AND profile_version IS NULL AND pilot_profile_id IS NOT NULL AND pilot_profile_version IS NOT NULL AND(interval_seconds IS NULL OR interval_seconds=86400)));

-- A scheduler can issue only a short-lived SERVICE context for its actual
-- admitted run. Its dedicated runtime login is installed separately; neither
-- human impersonation nor a generic context issuer is granted to the worker.
CREATE TABLE automation.pilot_runtime_logins (
 worker_login name PRIMARY KEY REFERENCES automation.workers(login),runtime_login name NOT NULL UNIQUE,
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id)
);
CREATE TRIGGER guard_control_change BEFORE INSERT OR UPDATE OR DELETE ON automation.pilot_runtime_logins FOR EACH STATEMENT EXECUTE FUNCTION security.guard_control_change();
ALTER TABLE security.request_contexts ADD COLUMN pilot_publication boolean NOT NULL DEFAULT false;
CREATE TABLE automation.pilot_run_contexts (
 context_id uuid PRIMARY KEY REFERENCES security.request_contexts(id),run_id uuid NOT NULL REFERENCES automation.runs(id),
 fence bigint NOT NULL CHECK(fence>0),worker_id uuid NOT NULL,worker_login name NOT NULL REFERENCES automation.workers(login)
);
CREATE FUNCTION automation.pilot_permissions() RETURNS text[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT ARRAY['RunJobs','ReadMarketData','ReadTrades','RunValuation','RunScenario','ImportObservations','PublishMarketData'];
$$;
CREATE FUNCTION automation.pilot_worker_configured() RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;
BEGIN w=automation.worker();RETURN w.kind='scheduler' AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins WHERE worker_login=w.login);END;$$;
CREATE FUNCTION automation.pilot_worker_matches(tenant uuid,workspace uuid,catalog uuid,runtime_login name) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;
BEGIN
 w=automation.worker();
 RETURN w.kind='scheduler' AND(w.tenant_id,w.workspace_id)=(tenant,workspace)
 AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l WHERE(l.worker_login,l.catalog_id,l.runtime_login)=(w.login,catalog,pilot_worker_matches.runtime_login));
END;$$;
CREATE FUNCTION automation.pilot_service_eligible(j automation.jobs,w automation.workers) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT w.kind='scheduler' AND(j.tenant_id,j.workspace_id)=(w.tenant_id,w.workspace_id)
 AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l JOIN automation.pilot_close_profiles p
  ON(p.id,p.version)=(j.pilot_profile_id,j.pilot_profile_version) WHERE l.worker_login=w.login AND l.catalog_id=p.catalog_id)
 AND NOT EXISTS(SELECT 1 FROM unnest(automation.pilot_permissions())p
  WHERE NOT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,p,clock_timestamp()));
$$;
ALTER FUNCTION automation.authorized(automation.jobs) RENAME TO authorized_pre_pilot;
REVOKE ALL ON FUNCTION automation.authorized_pre_pilot(automation.jobs) FROM openetrm_runtime,openetrm_scheduler;
CREATE FUNCTION automation.pilot_authorized(j automation.jobs,service uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p automation.pilot_close_profiles;required_permission text;d security.agent_delegations;n timestamptz:=clock_timestamp();
BEGIN
 IF j.operation<>'generated_pilot_close_v1' OR NOT automation.authorized_pre_pilot(j) THEN RETURN false;END IF;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version,tenant_id,workspace_id)=(j.pilot_profile_id,j.pilot_profile_version,j.tenant_id,j.workspace_id);
 IF p.id IS NULL OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.catalog_id,j.tenant_id,j.workspace_id))
 OR NOT EXISTS(SELECT 1 FROM security.reference_catalog_scopes s WHERE(s.catalog_id,s.tenant_id,s.workspace_id)=(p.reference_catalog_id,j.tenant_id,j.workspace_id))
 OR NOT EXISTS(SELECT 1 FROM ref_data.sources s WHERE(s.catalog_id,s.revision_id,s.code)=(p.reference_catalog_id,p.reference_revision_id,p.source_code) AND s.kind='synthetic')
 THEN RETURN false;END IF;
 SELECT * INTO d FROM security.agent_delegations WHERE id=j.delegation_id;
 FOREACH required_permission IN ARRAY automation.pilot_permissions() LOOP
  IF NOT security.principal_has_permission(j.owner_id,j.tenant_id,j.workspace_id,required_permission,n)
  OR(service IS NOT NULL AND NOT security.principal_has_permission(service,j.tenant_id,j.workspace_id,required_permission,n))
  OR(j.delegation_id IS NOT NULL AND(NOT security.principal_has_permission(d.delegator_id,j.tenant_id,j.workspace_id,required_permission,n)
     OR NOT EXISTS(SELECT 1 FROM security.delegation_permissions dp WHERE dp.delegation_id=d.id AND dp.permission=required_permission)))
  THEN RETURN false;END IF;
 END LOOP;
 RETURN true;
END;$$;
CREATE FUNCTION automation.authorized(j automation.jobs) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT CASE WHEN j.operation='generated_pilot_close_v1' THEN automation.pilot_authorized(j,NULL) ELSE automation.authorized_pre_pilot(j) END;
$$;
CREATE FUNCTION automation.pilot_context_live(token uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM automation.pilot_run_contexts c JOIN automation.runs r ON r.id=c.run_id JOIN automation.jobs j ON j.id=r.job_id
 JOIN automation.workers w ON w.login=c.worker_login JOIN automation.pilot_runtime_logins l ON l.worker_login=w.login
 JOIN automation.pilot_close_profiles p ON(p.id,p.version,p.catalog_id)=(j.pilot_profile_id,j.pilot_profile_version,l.catalog_id)
 JOIN security.request_contexts q ON q.id=c.context_id JOIN security.principals sp ON sp.id=w.principal_id AND sp.enabled AND sp.kind='service'
 WHERE c.context_id=token AND(c.fence,c.worker_id,c.worker_login)=(r.fence,r.worker_id,r.worker_login)
 AND(q.actor_id,q.tenant_id,q.workspace_id,q.runtime_login)=(w.principal_id,w.tenant_id,w.workspace_id,l.runtime_login)
 AND w.kind='scheduler' AND r.state='running' AND j.state IN('active','paused') AND r.lease_until>clock_timestamp() AND r.deadline_at>clock_timestamp() AND j.expires_at>clock_timestamp()
 AND automation.pilot_authorized(j,w.principal_id));
$$;
CREATE FUNCTION automation.issue_pilot_context(run uuid,expected_fence bigint,worker uuid) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;login name;token uuid;role record;
BEGIN
 w=automation.worker();
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id WHERE runs.id=run FOR UPDATE OF jobs;
 SELECT * INTO r FROM automation.runs WHERE id=run FOR UPDATE;
 SELECT runtime_login INTO login FROM automation.pilot_runtime_logins WHERE worker_login=session_user;
 SELECT * INTO role FROM pg_roles WHERE rolname=login;
 IF login IS NULL OR role.oid IS NULL THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 IF role.rolsuper OR role.rolbypassrls OR role.rolcreatedb OR role.rolcreaterole OR NOT role.rolcanlogin
 OR NOT pg_has_role(login,'openetrm_runtime','MEMBER') OR pg_has_role(login,'openetrm_authenticator','MEMBER')
 OR pg_has_role(login,'openetrm_security_owner','MEMBER') OR pg_has_role(login,'openetrm_scheduler','MEMBER')
 OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace ns ON ns.oid=c.relnamespace WHERE ns.nspname NOT LIKE 'pg_%' AND ns.nspname<>'information_schema' AND pg_has_role(login,c.relowner,'MEMBER'))
 OR EXISTS(SELECT 1 FROM pg_namespace ns WHERE ns.nspname NOT LIKE 'pg_%' AND ns.nspname<>'information_schema' AND pg_has_role(login,ns.nspowner,'MEMBER'))
 OR EXISTS(SELECT 1 FROM pg_database db WHERE db.datname=current_database() AND pg_has_role(login,db.datdba,'MEMBER'))
 OR r.id IS NULL OR w.kind<>'scheduler' OR r.worker_login<>session_user OR r.worker_id IS DISTINCT FROM worker OR r.fence IS DISTINCT FROM expected_fence
 OR(w.tenant_id,w.workspace_id)<>(r.tenant_id,r.workspace_id) OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,w.principal_id)
 OR r.state<>'running' OR j.state NOT IN('active','paused') OR r.lease_until<=clock_timestamp() OR r.deadline_at<=clock_timestamp() OR j.expires_at<=clock_timestamp()
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 token=gen_random_uuid();
 INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,delegation_id,runtime_login,expires_at)
 VALUES(token,w.principal_id,w.tenant_id,w.workspace_id,NULL,login,least(r.lease_until,r.deadline_at,j.expires_at));
 INSERT INTO automation.pilot_run_contexts VALUES(token,r.id,r.fence,r.worker_id,r.worker_login);
 RETURN token;
END;$$;
CREATE FUNCTION security.has_pre_pilot_permission(permission text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts; kind text; d security.agent_delegations;
  checked_at timestamptz := clock_timestamp();
BEGIN
  SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
    AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND expires_at > checked_at;
  IF c.id IS NULL OR NOT security.principal_has_permission(c.actor_id,c.tenant_id,c.workspace_id,permission,checked_at) THEN RETURN false; END IF;
  SELECT p.kind INTO kind FROM security.principals p WHERE p.id=c.actor_id;
  IF kind='agent' THEN
    SELECT * INTO d FROM security.agent_delegations WHERE id=c.delegation_id;
    RETURN d.id IS NOT NULL AND d.agent_id=c.actor_id AND d.tenant_id=c.tenant_id AND d.workspace_id=c.workspace_id
      AND NOT d.revoked AND d.granted_from <= checked_at AND checked_at < d.granted_to
      AND security.principal_has_permission(d.delegator_id,c.tenant_id,c.workspace_id,permission,checked_at)
      AND security.principal_has_permission(d.delegator_id,c.tenant_id,c.workspace_id,'CreateDelegations',checked_at)
      AND EXISTS (SELECT 1 FROM security.delegation_permissions dp WHERE dp.delegation_id=d.id AND dp.permission=has_pre_pilot_permission.permission);
  END IF;
  RETURN c.delegation_id IS NULL;
END;
$$;
ALTER FUNCTION security.has_pre_pilot_permission(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.has_pre_pilot_permission(text) FROM PUBLIC;
CREATE OR REPLACE FUNCTION security.has_permission(permission text) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE request security.request_contexts;
BEGIN
 IF NOT security.has_pre_pilot_permission(permission) THEN RETURN false;END IF;
 SELECT q.* INTO request FROM security.request_contexts q JOIN automation.pilot_run_contexts p ON p.context_id=q.id
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF NOT FOUND THEN RETURN true;END IF;
 RETURN automation.pilot_context_live(request.id) AND(permission NOT IN('ImportObservations','PublishMarketData') OR request.pilot_publication);
END;
$$;
DO $$DECLARE t text;f record;BEGIN
 FOREACH t IN ARRAY ARRAY['pilot_close_profiles','pilot_close_books','pilot_close_curves','pilot_close_moves','pilot_runtime_logins','pilot_run_contexts'] LOOP
  EXECUTE format('ALTER TABLE automation.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE automation.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON automation.%I TO openetrm_security_owner',t);
  IF t<>'pilot_runtime_logins' THEN EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON automation.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);END IF;
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE(n.nspname='automation' AND p.proname IN('authorized','pilot_permissions','pilot_worker_configured','pilot_worker_matches','pilot_service_eligible','pilot_authorized','pilot_context_live','issue_pilot_context')) OR(n.nspname='security' AND p.proname='has_permission') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('issue_pilot_context','pilot_worker_configured','pilot_worker_matches') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_scheduler',f.signature);
  ELSIF f.proname='has_permission' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
CREATE FUNCTION security.require_bound_market_context(catalog uuid,context uuid,valuation timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.has_permission('ReadTrades') OR NOT security.has_permission('RunValuation') OR NOT security.has_permission('RunScenario')
 OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
 AND q.runtime_login=session_user AND(q.market_catalog_id,q.market_context_id)=(catalog,context) AND q.trading_as_of=valuation AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'pilot_context' USING ERRCODE='42501';END IF;
END;$$;
ALTER FUNCTION security.require_bound_market_context(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_bound_market_context(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.require_bound_market_context(uuid,uuid,timestamptz) TO openetrm_runtime;
CREATE FUNCTION automation.create_pilot_close_job(schedule jsonb,definition jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;p automation.pilot_close_profiles;
 anchor market_data.market_closes;n timestamptz;operation uuid;payload jsonb;response jsonb;
BEGIN
 c=automation.actor();
 IF NOT security.current_write_context() OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(schedule) IS DISTINCT FROM 'object' OR jsonb_typeof(definition) IS DISTINCT FROM 'object'
 OR schedule-ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion']<>'{}'::jsonb
 OR NOT(schedule ?& ARRAY['requestId','name','operation','dueAt','expiresAt','intervalSeconds','timeZone','misfire','overlap','timeoutSeconds','maximumAttempts','profileId','profileVersion'])
 OR definition-ARRAY['priorCloseId','bookIds','curveCodes','sourceCode','seed','leafMoves','generator','tickBasis','parameterPolicy','frozenPolicy','membershipPolicy','calendarPolicy','closePolicy','valuationPolicy']<>'{}'::jsonb
 OR NOT(definition ?& ARRAY['priorCloseId','bookIds','curveCodes','sourceCode','seed','leafMoves','generator','tickBasis','parameterPolicy','frozenPolicy','membershipPolicy','calendarPolicy','closePolicy','valuationPolicy'])
 THEN RAISE EXCEPTION 'pilot_profile';END IF;
 operation=(schedule->>'requestId')::uuid;payload=jsonb_build_object('schedule',schedule,'definition',definition);
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;c=automation.actor();n=clock_timestamp();
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,request_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  SELECT * INTO j FROM automation.jobs WHERE id=previous.job_id;
  IF previous.request<>payload THEN RAISE EXCEPTION 'job_conflict';END IF;
  IF NOT automation.pilot_authorized(j,NULL) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
  RETURN previous.response;
 END IF;
 SELECT * INTO anchor FROM market_data.market_closes WHERE id=(definition->>'priorCloseId')::uuid AND security.market_close_visible(catalog_id,id);
 IF anchor.id IS NULL OR NOT security.market_access(anchor.catalog_id,'ReadMarketData') OR NOT security.reference_version_visible(anchor.reference_catalog_id,anchor.reference_revision_id)
 OR jsonb_typeof(definition->'bookIds') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'bookIds') NOT BETWEEN 1 AND 128
 OR jsonb_typeof(definition->'curveCodes') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'curveCodes') NOT BETWEEN 1 AND 128
 OR jsonb_typeof(definition->'leafMoves') IS DISTINCT FROM 'array' OR jsonb_array_length(definition->'leafMoves') NOT BETWEEN 1 AND 128
 OR(SELECT count(*) FROM automation.jobs WHERE owner_id=c.actor_id AND(state IN('active','paused','blocked')
 OR EXISTS(SELECT 1 FROM automation.runs pending_run WHERE pending_run.job_id=jobs.id AND pending_run.state IN('running','cancellation_requested'))))>=64
 THEN RAISE EXCEPTION 'pilot_profile';END IF;
 p.id=gen_random_uuid();p.version=1;p.tenant_id=c.tenant_id;p.workspace_id=c.workspace_id;p.catalog_id=anchor.catalog_id;p.definition_revision_id=anchor.definition_revision_id;p.prior_close_id=anchor.id;
 p.reference_catalog_id=anchor.reference_catalog_id;p.reference_revision_id=anchor.reference_revision_id;p.source_code=definition->>'sourceCode';
 p.seed=(definition->>'seed')::integer;p.generator=definition->>'generator';p.tick_basis=definition->>'tickBasis';p.parameter_policy=definition->>'parameterPolicy';
 p.frozen_policy=definition->>'frozenPolicy';p.membership_policy=definition->>'membershipPolicy';p.calendar_policy=definition->>'calendarPolicy';p.close_policy=definition->>'closePolicy';p.valuation_policy=definition->'valuationPolicy';
 INSERT INTO automation.pilot_close_profiles SELECT(p).*;
 INSERT INTO automation.pilot_close_books SELECT p.id,p.version,p.catalog_id,p.definition_revision_id,v::uuid FROM jsonb_array_elements_text(definition->'bookIds')v;
 INSERT INTO automation.pilot_close_curves SELECT p.id,p.version,p.catalog_id,p.definition_revision_id,v FROM jsonb_array_elements_text(definition->'curveCodes')v;
 INSERT INTO automation.pilot_close_moves SELECT p.id,p.version,p.catalog_id,p.definition_revision_id,x.curve,x."maxTicks" FROM jsonb_to_recordset(definition->'leafMoves')x(curve text,"maxTicks" integer);
 j.id=gen_random_uuid();j.tenant_id=c.tenant_id;j.workspace_id=c.workspace_id;j.owner_id=c.actor_id;j.delegation_id=c.delegation_id;
 j.name=schedule->>'name';j.operation=schedule->>'operation';j.state='active';j.version=1;j.next_due_at=(schedule->>'dueAt')::timestamptz;j.expires_at=(schedule->>'expiresAt')::timestamptz;
 j.interval_seconds=(schedule->>'intervalSeconds')::integer;j.time_zone=schedule->>'timeZone';j.misfire=schedule->>'misfire';j.overlap_policy=schedule->>'overlap';
 j.timeout_seconds=(schedule->>'timeoutSeconds')::integer;j.maximum_attempts=(schedule->>'maximumAttempts')::integer;j.profile_id=(schedule->>'profileId')::uuid;j.profile_version=(schedule->>'profileVersion')::integer;
 j.pilot_profile_id=p.id;j.pilot_profile_version=p.version;j.created_at=n;j.last_reconciled_at='-infinity';
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR j.operation IS DISTINCT FROM 'generated_pilot_close_v1' OR j.expires_at<=n OR j.expires_at<=j.next_due_at OR j.next_due_at<=anchor.at
 OR j.expires_at>n+interval '366 days' OR j.next_due_at<n-interval '1 day' OR NOT automation.pilot_authorized(j,NULL)
 THEN RAISE EXCEPTION 'pilot_profile';END IF;
 INSERT INTO automation.jobs SELECT(j).*;
 response=automation.description(j)||jsonb_build_object('requestId',operation);INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,j.id,payload,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,'created',n);
 RETURN response;
END;$$;
CREATE FUNCTION automation.pilot_definition(j automation.jobs) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('priorCloseId',p.prior_close_id,'bookIds',(SELECT jsonb_agg(book_id ORDER BY book_id) FROM automation.pilot_close_books WHERE(profile_id,profile_version)=(p.id,p.version)),
 'curveCodes',(SELECT jsonb_agg(curve_code ORDER BY curve_code COLLATE "C") FROM automation.pilot_close_curves WHERE(profile_id,profile_version)=(p.id,p.version)),
 'sourceCode',p.source_code,'seed',p.seed,'leafMoves',(SELECT jsonb_agg(jsonb_build_object('curve',curve_code,'maxTicks',maximum_ticks) ORDER BY curve_code COLLATE "C") FROM automation.pilot_close_moves WHERE(profile_id,profile_version)=(p.id,p.version)),
 'generator',p.generator,'tickBasis',p.tick_basis,'parameterPolicy',p.parameter_policy,'frozenPolicy',p.frozen_policy,'membershipPolicy',p.membership_policy,
 'calendarPolicy',p.calendar_policy,'closePolicy',p.close_policy,'valuationPolicy',p.valuation_policy)
 FROM automation.pilot_close_profiles p WHERE(p.id,p.version)=(j.pilot_profile_id,j.pilot_profile_version);
$$;
ALTER FUNCTION automation.description(automation.jobs) RENAME TO description_pre_pilot;
REVOKE ALL ON FUNCTION automation.description_pre_pilot(automation.jobs) FROM openetrm_runtime,openetrm_scheduler;
CREATE FUNCTION automation.description(j automation.jobs) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT automation.description_pre_pilot(j)||jsonb_build_object('operationName',CASE j.operation
 WHEN 'worker_check' THEN 'Worker readiness' WHEN 'swarm_job' THEN 'Bounded workload' WHEN 'generated_pilot_close_v1' THEN 'Generated pilot close — futures execution P&L, swap PV and open risk' END,
 'pilotClose',(SELECT jsonb_build_object('id',s.id,'at',s.at,'state','marks_published') FROM market_data.market_closes s JOIN automation.runs r ON r.id=s.run_id WHERE r.job_id=j.id ORDER BY r.due_at DESC LIMIT 1));
$$;
CREATE FUNCTION automation.require_pilot_job_definition(job uuid) RETURNS automation.jobs
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE j automation.jobs;p automation.pilot_close_profiles;permission text;
BEGIN
 SELECT * INTO j FROM automation.jobs WHERE id=job;
 IF NOT security.current_write_context() OR j.operation IS DISTINCT FROM 'generated_pilot_close_v1' OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id)
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version)=(j.pilot_profile_id,j.pilot_profile_version);
 FOREACH permission IN ARRAY ARRAY['ReadMarketData','ReadTrades','RunValuation','RunScenario'] LOOP
  IF NOT security.has_permission(permission) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 IF p.id IS NULL OR NOT security.market_close_visible(p.catalog_id,p.prior_close_id) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 RETURN j;
END;$$;
CREATE FUNCTION automation.pilot_job_definition(job uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE j automation.jobs;current_job automation.jobs;payload jsonb;
BEGIN
 j=automation.require_pilot_job_definition(job);
 payload=automation.pilot_definition(j);
 current_job=automation.require_pilot_job_definition(job);
 IF(current_job.pilot_profile_id,current_job.pilot_profile_version) IS DISTINCT FROM(j.pilot_profile_id,j.pilot_profile_version)
 THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 RETURN payload;
END;$$;
CREATE FUNCTION automation.pilot_run_definition() RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;j automation.jobs;payload jsonb;
BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF q.id IS NULL OR NOT automation.pilot_context_live(q.id) OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs r ON r.job_id=jobs.id JOIN automation.pilot_run_contexts p ON p.run_id=r.id WHERE p.context_id=q.id;
 payload=automation.pilot_definition(j);
 IF q.id IS NULL OR NOT automation.pilot_context_live(q.id) OR NOT security.has_permission('RunJobs') THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 RETURN payload;
END;$$;
CREATE FUNCTION automation.lock_pilot_run() RETURNS automation.runs
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;j automation.jobs;r automation.runs;
BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT jobs.* INTO j FROM automation.jobs jobs JOIN automation.runs runs ON runs.job_id=jobs.id JOIN automation.pilot_run_contexts p ON p.run_id=runs.id
 WHERE p.context_id=q.id FOR UPDATE OF jobs;
 SELECT runs.* INTO r FROM automation.runs runs JOIN automation.pilot_run_contexts p ON p.run_id=runs.id WHERE p.context_id=q.id FOR UPDATE OF runs;
 IF q.id IS NULL OR q.expires_at<=clock_timestamp() OR NOT automation.pilot_context_live(q.id) OR NOT security.has_permission('RunJobs')
 THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
 RETURN r;
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='automation'
 AND p.proname IN('create_pilot_close_job','pilot_definition','description','require_pilot_job_definition','pilot_job_definition','pilot_run_definition','lock_pilot_run') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('create_pilot_close_job','pilot_job_definition','pilot_run_definition','lock_pilot_run') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
-- Extend the existing scheduler and cancellation state machine; work uses the
-- same jobs, occurrences, leases and replacement fences as readiness jobs.
CREATE OR REPLACE FUNCTION automation.control_job(request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;j automation.jobs;previous automation.commands;n timestamptz:=clock_timestamp();response jsonb;v_request_id uuid;action text;
BEGIN
 c=automation.actor();
 IF jsonb_typeof(request)<>'object' OR request-ARRAY['requestId','id','action','expectedVersion']<>'{}'::jsonb OR NOT(request ?& ARRAY['requestId','id','action','expectedVersion']) THEN RAISE EXCEPTION 'job_input';END IF;
 SELECT * INTO j FROM automation.jobs WHERE id=(request->>'id')::uuid;
 IF j.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 PERFORM 1 FROM security.principals WHERE id IN(c.actor_id,j.owner_id) ORDER BY id FOR UPDATE;
 SELECT * INTO j FROM automation.jobs WHERE id=(request->>'id')::uuid FOR UPDATE;
 c=automation.actor();n=clock_timestamp();
 IF j.id IS NULL OR NOT automation.can_view(j.tenant_id,j.workspace_id,j.owner_id) THEN RAISE EXCEPTION 'job_denied' USING ERRCODE='42501';END IF;
 v_request_id=(request->>'requestId')::uuid;action=request->>'action';
 SELECT * INTO previous FROM automation.commands WHERE(tenant_id,workspace_id,actor_id,commands.request_id)=(c.tenant_id,c.workspace_id,c.actor_id,v_request_id);
 IF FOUND THEN IF previous.request<>request THEN RAISE EXCEPTION 'job_conflict';END IF;RETURN previous.response;END IF;
 IF j.version IS DISTINCT FROM(request->>'expectedVersion')::integer THEN RAISE EXCEPTION 'job_conflict';END IF;
 IF action='pause' AND j.state='active' THEN j.state='paused';
 ELSIF action='resume' AND j.state='paused' AND j.expires_at>n AND automation.authorized(j) THEN j.state='active';
 ELSIF action='run_again' AND j.state IN('completed','blocked') AND j.expires_at>n AND automation.authorized(j)
 AND NOT EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN
 IF(SELECT count(*) FROM automation.jobs q WHERE q.owner_id=j.owner_id AND q.id<>j.id AND(q.state IN('active','paused','blocked') OR EXISTS(SELECT 1 FROM automation.runs r WHERE r.job_id=q.id AND r.state IN('running','cancellation_requested'))))>=64 THEN RAISE EXCEPTION 'job_limit';END IF;
 j.state='active';j.next_due_at=n;
 ELSIF action='cancel' AND j.state IN('active','paused','blocked') THEN
 j.state=CASE WHEN j.operation='swarm_job' AND EXISTS(SELECT 1 FROM automation.runs WHERE job_id=j.id AND state IN('running','cancellation_requested')) THEN 'cancellation_requested' ELSE 'cancelled' END;
 UPDATE automation.runs SET state=CASE WHEN j.operation<>'swarm_job' THEN 'cancelled' ELSE 'cancellation_requested' END,
 fence=fence+1,lease_until=n,finished_at=CASE WHEN j.operation<>'swarm_job' THEN n ELSE NULL END WHERE job_id=j.id AND state='running';
 ELSE RAISE EXCEPTION 'job_transition';END IF;
 UPDATE automation.jobs SET state=j.state,next_due_at=j.next_due_at,version=version+1 WHERE id=j.id RETURNING * INTO j;
 response=automation.description(j);
 INSERT INTO automation.commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,v_request_id,j.id,request,response,n);
 INSERT INTO automation.audit(job_id,actor_id,action,recorded_at) VALUES(j.id,c.actor_id,action,n);
 RETURN response;
END $$;

CREATE FUNCTION automation.claim_next(worker_id uuid,lease_seconds integer,pilot_enabled boolean) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE w automation.workers;j automation.jobs;r automation.runs;n timestamptz;response jsonb;
BEGIN
 w=automation.worker();n=clock_timestamp();
 IF worker_id IS NULL OR lease_seconds IS NULL OR lease_seconds NOT BETWEEN 5 AND 120 OR pilot_enabled IS NULL THEN RAISE EXCEPTION 'job_input';END IF;
 FOR j IN SELECT * FROM automation.jobs WHERE(tenant_id,workspace_id)=(w.tenant_id,w.workspace_id)
 AND ((w.kind='scheduler' AND(operation='worker_check' OR(operation='generated_pilot_close_v1' AND pilot_enabled AND automation.pilot_service_eligible(jobs,w)))) OR(w.kind='controller' AND operation='swarm_job'))
 AND((state='active' AND next_due_at<=n) OR(w.kind='controller' AND EXISTS(SELECT 1 FROM automation.runs pending_run WHERE pending_run.job_id=jobs.id AND pending_run.state IN('running','cancellation_requested'))))
 ORDER BY last_reconciled_at,next_due_at,id FOR UPDATE SKIP LOCKED LIMIT 32 LOOP
 n=clock_timestamp();
 IF j.operation='generated_pilot_close_v1' AND NOT automation.pilot_service_eligible(j,w) THEN CONTINUE;END IF;
 UPDATE automation.jobs SET last_reconciled_at=n WHERE id=j.id;
 IF j.operation='swarm_job' THEN
 SELECT * INTO r FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at FOR UPDATE;
 n=clock_timestamp();w=automation.worker();
 IF r.id IS NOT NULL AND r.state IN('running','cancellation_requested') THEN
 IF r.lease_until>n AND(r.worker_id IS DISTINCT FROM claim_next.worker_id OR r.worker_login<>session_user) THEN CONTINUE;END IF;
 UPDATE automation.runs SET fence=CASE WHEN r.lease_until<=n OR r.worker_id IS DISTINCT FROM claim_next.worker_id THEN fence+1 ELSE fence END,
 worker_id=claim_next.worker_id,worker_login=session_user,lease_until=n+make_interval(secs=>lease_seconds) WHERE id=r.id RETURNING * INTO r;
 RETURN automation.workload_claim(j,r);
 END IF;
 IF EXISTS(SELECT 1 FROM automation.node_commands q JOIN automation.clusters s ON s.id=q.cluster_id WHERE(s.tenant_id,s.workspace_id)=(w.tenant_id,w.workspace_id) AND q.state IN('queued','running','unknown')) THEN CONTINUE;END IF;
 END IF;
 IF NOT automation.authorized(j) THEN
 UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='authority_revoked' WHERE job_id=j.id AND state='running' AND j.operation<>'swarm_job';
 UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 IF j.expires_at<=n THEN
 UPDATE automation.runs SET state='failed',fence=fence+1,finished_at=n,error_code='job_expired' WHERE job_id=j.id AND state='running' AND j.operation<>'swarm_job';
 UPDATE automation.jobs SET state='expired',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 SELECT * INTO r FROM automation.runs WHERE job_id=j.id AND due_at=j.next_due_at FOR UPDATE;
 n=clock_timestamp();w=automation.worker();
 IF j.operation='generated_pilot_close_v1' AND(j.expires_at<=n OR NOT automation.pilot_service_eligible(j,w) OR NOT automation.pilot_authorized(j,NULL)) THEN CONTINUE;END IF;
 IF r.id IS NOT NULL THEN
 IF r.state<>'running' OR r.lease_until>n THEN CONTINUE;END IF;
 IF j.operation='swarm_job' THEN CONTINUE;END IF; -- Reconcile physical work before replacement.
 IF r.attempt>=j.maximum_attempts OR r.deadline_at<=n THEN
 UPDATE automation.runs SET state='failed',finished_at=n,error_code='lease_expired',fence=fence+1 WHERE id=r.id;
 UPDATE automation.jobs SET state='blocked',version=version+1 WHERE id=j.id;CONTINUE;END IF;
 UPDATE automation.runs SET fence=fence+1,attempt=attempt+1,worker_id=claim_next.worker_id,worker_login=session_user,lease_until=least(n+make_interval(secs=>lease_seconds),deadline_at,j.expires_at) WHERE id=r.id RETURNING * INTO r;
 ELSE
 INSERT INTO automation.runs(id,job_id,tenant_id,workspace_id,due_at,job_version,state,fence,attempt,worker_id,worker_login,lease_until,started_at,deadline_at,finished_at,error_code,service_id)
 VALUES(gen_random_uuid(),j.id,j.tenant_id,j.workspace_id,j.next_due_at,j.version,'running',1,1,worker_id,session_user,
 least(n+make_interval(secs=>lease_seconds),n+make_interval(secs=>j.timeout_seconds),j.expires_at),n,least(n+make_interval(secs=>j.timeout_seconds),j.expires_at),NULL,NULL,NULL) RETURNING * INTO r;
 END IF;
 INSERT INTO automation.audit(job_id,run_id,actor_id,action,recorded_at,fence) VALUES(j.id,r.id,w.principal_id,'claimed',n,r.fence);
 IF j.operation='swarm_job' THEN RETURN automation.workload_claim(j,r);END IF;
 response=jsonb_build_object('id',r.id,'jobId',j.id,'operation',j.operation,'fence',r.fence,'workerId',r.worker_id,'leaseUntil',r.lease_until,'deadlineAt',r.deadline_at,
 'dueAt',r.due_at,'intervalSeconds',j.interval_seconds,
 'profile',(SELECT to_jsonb(p)-ARRAY['enabled','tenant_id','workspace_id'] FROM automation.execution_profiles p WHERE(p.id,p.version)=(j.profile_id,j.profile_version)));
 RETURN response;
 END LOOP;
 RETURN NULL;
END $$;
ALTER FUNCTION automation.claim_next(uuid,integer,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.claim_next(uuid,integer,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.claim_next(uuid,integer,boolean) TO openetrm_scheduler;
CREATE OR REPLACE FUNCTION automation.claim_next(worker_id uuid,lease_seconds integer) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT automation.claim_next(worker_id,lease_seconds,false);
$$;
CREATE FUNCTION automation.publish_pilot_close(marks jsonb,carried jsonb,members jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;p automation.pilot_close_profiles;q security.request_contexts;
 previous market_data.market_closes;ordinal integer;identity uuid;mark_row record;observation uuid;payload jsonb;
BEGIN
 r=automation.lock_pilot_run();PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO j FROM automation.jobs WHERE id=r.job_id;
 SELECT * INTO p FROM automation.pilot_close_profiles WHERE(id,version)=(j.pilot_profile_id,j.pilot_profile_version);
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 payload=jsonb_build_object('profileId',p.id,'profileVersion',p.version,'seed',p.seed,'marks',marks,'carried',carried,'members',members);
 SELECT * INTO previous FROM market_data.market_closes WHERE run_id=r.id;
 IF FOUND THEN
  IF previous.request<>payload THEN RAISE EXCEPTION 'pilot_close_conflict';END IF;
  IF NOT security.market_close_visible(p.catalog_id,previous.id) THEN RAISE EXCEPTION 'job_fence' USING ERRCODE='42501';END IF;
  RETURN previous.id;
 END IF;
 PERFORM security.require_bound_market_context(p.catalog_id,q.market_context_id,r.due_at);
 IF jsonb_typeof(marks) IS DISTINCT FROM 'array' OR jsonb_array_length(marks) NOT BETWEEN 1 AND 20000
 OR jsonb_typeof(carried) IS DISTINCT FROM 'array' OR jsonb_array_length(carried)>20000
 OR jsonb_typeof(members) IS DISTINCT FROM 'array' OR jsonb_array_length(members) NOT BETWEEN 1 AND 20000
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(marks)x("seriesId" uuid,"anchorObservationId" uuid,"moveTicks" integer,value numeric)
 LEFT JOIN market_data.market_close_pins a ON(a.catalog_id,a.close_id,a.series_id,a.observation_id,a.input_use)=(p.catalog_id,p.prior_close_id,x."seriesId",x."anchorObservationId",'variable')
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
 LEFT JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(a.catalog_id,a.definition_revision_id,a.series_id)
 LEFT JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
 LEFT JOIN ref_data.quote_specifications qs ON(qs.catalog_id,qs.revision_id,qs.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
 LEFT JOIN ref_data.sources src ON(src.catalog_id,src.revision_id,src.code)=(o.reference_catalog_id,o.reference_revision_id,o.source_code)
 LEFT JOIN LATERAL(SELECT min(m.maximum_ticks) maximum_ticks,count(DISTINCT m.maximum_ticks) distinct_bounds FROM automation.pilot_close_moves m
  JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.curve_code)=(m.catalog_id,m.definition_revision_id,m.curve_code)
  JOIN market_data.curves cv ON(cv.catalog_id,cv.revision_id,cv.code)=(cm.catalog_id,cm.revision_id,cm.curve_code)
  WHERE(m.profile_id,m.profile_version)=(p.id,p.version) AND cv.rule='direct' AND cm.contract_id=s.contract_id AND cv.quote_code=s.quote_code)bounds ON true
 WHERE a.close_id IS NULL OR o.role NOT IN('trader_mark','daily_settlement') OR o.publication_state<>'final' OR src.kind<>'synthetic'
 OR k.expiry_at<=r.due_at OR x."moveTicks" IS NULL OR x.value IS NULL OR x.value<>o.value+x."moveTicks"*qs.clearing_increment OR o.source_code<>p.source_code
 OR bounds.maximum_ticks IS NULL OR bounds.distinct_bounds<>1 OR abs(x."moveTicks")>bounds.maximum_ticks)
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(carried)x("seriesId" uuid,"observationId" uuid,"inputUse" text)
 LEFT JOIN market_data.market_close_pins a ON(a.catalog_id,a.close_id,a.series_id,a.observation_id,a.input_use)=(p.catalog_id,p.prior_close_id,x."seriesId",x."observationId",x."inputUse")
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
 WHERE a.close_id IS NULL OR NOT(o.role='parameter' AND x."inputUse"='variable' OR o.role IN('final_contract_settlement','index_fixing') AND o.publication_state='final' AND x."inputUse"='frozen'))
 OR EXISTS(WITH expected AS(
  SELECT a.series_id,a.observation_id,a.input_use FROM market_data.market_close_pins a
  JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
  WHERE(a.catalog_id,a.close_id)=(p.catalog_id,p.prior_close_id) AND(o.role='parameter' OR a.input_use='frozen'))
  SELECT 1 FROM expected e FULL JOIN jsonb_to_recordset(carried)x("seriesId" uuid,"observationId" uuid,"inputUse" text)
  ON(e.series_id,e.observation_id,e.input_use)=(x."seriesId",x."observationId",x."inputUse")
  WHERE e.observation_id IS NULL OR x."observationId" IS NULL)
 OR EXISTS(SELECT 1 FROM automation.pilot_close_curves selected WHERE(selected.profile_id,selected.profile_version)=(p.id,p.version)
  AND NOT EXISTS(SELECT 1 FROM market_data.market_close_members a JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(a.catalog_id,a.definition_revision_id,a.contract_id)
   WHERE(a.catalog_id,a.close_id,a.curve_code)=(p.catalog_id,p.prior_close_id,selected.curve_code) AND k.expiry_at>r.due_at))
 OR EXISTS(WITH expected AS(
  SELECT a.curve_code AS curve,a.contract_id AS contract,(row_number() OVER(PARTITION BY a.curve_code ORDER BY a.ordinal)-1)::integer AS ordinal
  FROM automation.pilot_close_curves selected JOIN market_data.market_close_members a
   ON(a.catalog_id,a.close_id,a.curve_code)=(p.catalog_id,p.prior_close_id,selected.curve_code)
  JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(a.catalog_id,a.definition_revision_id,a.contract_id)
  WHERE(selected.profile_id,selected.profile_version)=(p.id,p.version) AND k.expiry_at>r.due_at)
  SELECT 1 FROM expected e FULL JOIN jsonb_to_recordset(members)x(curve text,"contractId" uuid,ordinal integer)
  ON(e.curve,e.contract)=(x.curve,x."contractId") WHERE e.ordinal IS DISTINCT FROM x.ordinal)
 THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 -- This is the sole transition from a preflight read view to publication. The
 -- run and global gate remain locked throughout the transaction. No generic
 -- context reset operation is exposed. The close retains the preflight view.
 UPDATE security.request_contexts SET market_catalog_id=NULL,market_context_id=NULL,trading_as_of=NULL,pilot_publication=true WHERE id=q.id;
 PERFORM automation.lock_pilot_run();
 identity=r.id;SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.market_closes WHERE catalog_id=p.catalog_id;
 INSERT INTO market_data.market_closes VALUES(p.catalog_id,identity,p.definition_revision_id,p.reference_catalog_id,p.reference_revision_id,
 'Generated pilot close',r.due_at,'generated_pilot',q.actor_id,r.id,payload,
 p.prior_close_id,r.id,p.generator,q.market_context_id,ordinal,pg_current_xact_id());
 FOR mark_row IN SELECT v.*,o.role FROM jsonb_to_recordset(marks)v("seriesId" uuid,"anchorObservationId" uuid,"moveTicks" integer,value numeric)
 JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,v."anchorObservationId") LOOP
  observation=gen_random_uuid();
  INSERT INTO market_data.observations VALUES(p.catalog_id,observation,p.definition_revision_id,mark_row."seriesId",p.reference_catalog_id,p.reference_revision_id,p.source_code,
   'pilot:'||r.id::text||':'||mark_row."seriesId"::text,p.generator,mark_row.role,'final',r.due_at,clock_timestamp(),mark_row.value,NULL);
  INSERT INTO market_data.market_close_pins VALUES(p.catalog_id,identity,p.definition_revision_id,mark_row."seriesId",observation,'variable');
 END LOOP;
 INSERT INTO market_data.market_close_pins SELECT p.catalog_id,identity,p.definition_revision_id,x."seriesId",x."observationId",x."inputUse" FROM jsonb_to_recordset(carried)x("seriesId" uuid,"observationId" uuid,"inputUse" text);
 INSERT INTO market_data.market_close_members SELECT p.catalog_id,identity,p.definition_revision_id,x.curve,x."contractId",x.ordinal FROM jsonb_to_recordset(members)x(curve text,"contractId" uuid,ordinal integer);
 UPDATE security.request_contexts SET pilot_publication=false WHERE id=q.id;
 PERFORM automation.lock_pilot_run();
 RETURN identity;
END;$$;
ALTER FUNCTION automation.publish_pilot_close(jsonb,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.publish_pilot_close(jsonb,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.publish_pilot_close(jsonb,jsonb,jsonb) TO openetrm_runtime;
GRANT INSERT ON market_data.observations TO openetrm_security_owner;
CREATE FUNCTION automation.pilot_close_commit() RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;s market_data.market_closes;
BEGIN
 r=automation.lock_pilot_run();SELECT * INTO s FROM market_data.market_closes WHERE run_id=r.id;
 IF NOT FOUND OR NOT security.market_close_visible(s.catalog_id,s.id) THEN RAISE EXCEPTION 'pilot_close_inputs';END IF;
 RETURN security.checked_commit_time(s.publication_xid);
END;$$;
ALTER FUNCTION automation.pilot_close_commit() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION automation.pilot_close_commit() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION automation.pilot_close_commit() TO openetrm_runtime;
COMMIT;
