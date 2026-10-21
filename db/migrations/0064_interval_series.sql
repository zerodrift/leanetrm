-- Audited interval series (docs/ppa-design.md §5, phase A). A series pins its kind, its exact
-- kind-compatible reference specification, its clock and its resolution. Each revision appends an
-- immutable delta of per-interval values, where a null value withdraws an interval, and records its
-- actor, reason and request identity. Revisions are certified on the shared knowledge gate, so a
-- revision is known from its commit time. Runtime writes only through the definer operations below,
-- which replay a retried request, recheck current authority after their writes and render instants
-- in UTC; reads return the values and per-interval changes known at an explicit cutoff.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE market_data.interval_series (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),id uuid NOT NULL,code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80 AND btrim(name)=name),
 kind text NOT NULL CHECK(kind IN('additive_price_shape','multiplicative_price_shape','capacity_factor_forecast','metered_energy')),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 quote_code ref_data.code,value_code ref_data.code,quantity_code ref_data.code,clock_code ref_data.code NOT NULL,
 interval_minutes integer NOT NULL CHECK(interval_minutes IN(5,15,30,60)),
 actor_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,created_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(id),UNIQUE(catalog_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,value_code) REFERENCES ref_data.value_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 CHECK((quote_code IS NOT NULL)=(kind='additive_price_shape')
  AND(value_code IS NOT NULL)=(kind IN('multiplicative_price_shape','capacity_factor_forecast'))
  AND(quantity_code IS NOT NULL)=(kind='metered_energy'))
);
CREATE TABLE market_data.interval_series_revisions (
 catalog_id uuid NOT NULL,series_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_revision integer,
 actor_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 reason text NOT NULL CHECK(length(reason) BETWEEN 1 AND 500 AND reason !~ '^[[:space:]]*$'),
 request_id uuid NOT NULL,interval_count integer NOT NULL CHECK(interval_count BETWEEN 1 AND 200000),
 publication_order integer NOT NULL CHECK(publication_order>0),publication_xid xid8 NOT NULL,recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,series_id,revision),UNIQUE(catalog_id,publication_order),UNIQUE(catalog_id,publication_order,publication_xid),
 FOREIGN KEY(catalog_id,series_id) REFERENCES market_data.interval_series(catalog_id,id),
 FOREIGN KEY(catalog_id,series_id,previous_revision) REFERENCES market_data.interval_series_revisions(catalog_id,series_id,revision),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 CHECK((revision=1 AND previous_revision IS NULL) OR(revision>1 AND previous_revision=revision-1))
);
CREATE TABLE market_data.interval_series_values (
 catalog_id uuid NOT NULL,series_id uuid NOT NULL,revision integer NOT NULL,
 starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,value ref_data.exact_number,
 PRIMARY KEY(catalog_id,series_id,revision,starts_at),CHECK(ends_at>starts_at),
 FOREIGN KEY(catalog_id,series_id,revision) REFERENCES market_data.interval_series_revisions(catalog_id,series_id,revision)
);
CREATE INDEX interval_series_value_history ON market_data.interval_series_values(catalog_id,series_id,starts_at,revision);
CREATE TABLE market_data.interval_series_requests (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 operation text NOT NULL CHECK(operation IN('define','revise')),request_sha256 text NOT NULL CHECK(request_sha256 ~ '^[0-9a-f]{64}$'),
 response jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id)
);
CREATE TABLE market_data.interval_series_knowledge_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES market_data.interval_series_revisions(catalog_id,publication_order,publication_xid)
);
COMMENT ON TABLE market_data.interval_series IS
 'One interval series definition: kind, exact kind-compatible reference specification, clock and resolution; owner bindings belong to later phases.';
COMMENT ON TABLE market_data.interval_series_revisions IS
 'Immutable delta revisions of one series, each with its actor, reason, request identity and catalog publication order.';
COMMENT ON TABLE market_data.interval_series_values IS
 'The intervals one revision sets, keyed by exact UTC start; a null value withdraws the interval.';
COMMENT ON TABLE market_data.interval_series_requests IS
 'The retained receipt of each define or revise request, by catalog, actor and request identity.';
COMMENT ON TABLE market_data.interval_series_knowledge_receipts IS
 'Commit-certified knowledge time of each interval series revision publication.';

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['interval_series','interval_series_revisions','interval_series_values','interval_series_requests','interval_series_knowledge_receipts'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON market_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_security_owner',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime',t);
  IF t='interval_series' THEN
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData'')'
    ' AND security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  ELSIF t IN('interval_series_revisions','interval_series_values') THEN
   -- A revision or value is visible only with its definition's exact reference version.
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData'')'
    ' AND(catalog_id,series_id) IN(SELECT s.catalog_id,s.id FROM market_data.interval_series s))',t);
  ELSE
   EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData''))',t);
  END IF;
 END LOOP;
END$$;

CREATE FUNCTION market_data.define_interval_series(request uuid,request_sha256 text,catalog uuid,reference_catalog uuid,reference_revision uuid,
 code text,name text,kind text,specification text,clock text,interval_minutes integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts;stored market_data.interval_series_requests;series uuid=gen_random_uuid();result jsonb;
BEGIN
 IF NOT security.current_write_context() OR security.market_access(catalog,'WriteMarketDefinitions') IS NOT TRUE
  OR security.reference_access(reference_catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts r
 WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO stored FROM market_data.interval_series_requests r WHERE(r.catalog_id,r.actor_id,r.request_id)=(catalog,c.actor_id,request);
 IF FOUND THEN
  IF stored.operation<>'define' OR stored.request_sha256 IS DISTINCT FROM define_interval_series.request_sha256 THEN RAISE EXCEPTION 'interval_series_conflict';END IF;
  RETURN stored.response;
 END IF;
 IF pg_input_is_valid(define_interval_series.code,'ref_data.code') IS NOT TRUE
  OR EXISTS(SELECT 1 FROM market_data.interval_series s WHERE(s.catalog_id,s.code)=(catalog,define_interval_series.code)) THEN RAISE EXCEPTION 'interval_series_code';END IF;
 -- The exact pin is a sealed reference revision holding the clock and a specification of the kind's dimension.
 IF NOT EXISTS(SELECT 1 FROM ref_data.revisions r WHERE(r.catalog_id,r.id,r.state)=(reference_catalog,reference_revision,'sealed'))
  OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules r WHERE(r.catalog_id,r.revision_id,r.code)=(reference_catalog,reference_revision,clock))
  OR NOT(CASE define_interval_series.kind
   WHEN 'additive_price_shape' THEN EXISTS(SELECT 1 FROM ref_data.quote_specifications q
    WHERE(q.catalog_id,q.revision_id,q.code)=(reference_catalog,reference_revision,specification))
   WHEN 'multiplicative_price_shape' THEN EXISTS(SELECT 1 FROM ref_data.value_specifications v
    WHERE(v.catalog_id,v.revision_id,v.code,v.kind)=(reference_catalog,reference_revision,specification,'dimensionless'))
   WHEN 'capacity_factor_forecast' THEN EXISTS(SELECT 1 FROM ref_data.value_specifications v
    WHERE(v.catalog_id,v.revision_id,v.code)=(reference_catalog,reference_revision,specification))
   WHEN 'metered_energy' THEN EXISTS(SELECT 1 FROM ref_data.quantity_specifications q
    WHERE(q.catalog_id,q.revision_id,q.code,q.dimension_code)=(reference_catalog,reference_revision,specification,'energy'))
   ELSE false END)
 THEN RAISE EXCEPTION 'interval_series_definition' USING ERRCODE='23514';END IF;
 INSERT INTO market_data.interval_series VALUES(catalog,series,define_interval_series.code,define_interval_series.name,define_interval_series.kind,
  reference_catalog,reference_revision,
  CASE WHEN define_interval_series.kind='additive_price_shape' THEN specification END,
  CASE WHEN define_interval_series.kind IN('multiplicative_price_shape','capacity_factor_forecast') THEN specification END,
  CASE WHEN define_interval_series.kind='metered_energy' THEN specification END,
  clock,define_interval_series.interval_minutes,c.actor_id,c.tenant_id,c.workspace_id,clock_timestamp());
 IF security.market_access(catalog,'WriteMarketDefinitions') IS NOT TRUE OR security.reference_access(reference_catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 result=jsonb_build_object('requestId',request,'seriesId',series,'revision',0,'intervals',0);
 INSERT INTO market_data.interval_series_requests VALUES(catalog,c.actor_id,request,'define',define_interval_series.request_sha256,result);
 RETURN result;
END$$;

CREATE FUNCTION market_data.append_interval_series_revision(series uuid,request uuid,request_sha256 text,expected integer,reason text,
 starts timestamptz[],ends timestamptz[],vals text[]) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE selected market_data.interval_series;c security.request_contexts;stored market_data.interval_series_requests;
 bounds ref_data.value_specifications;head integer;total integer=cardinality(starts);ordinal integer;result jsonb;
BEGIN
 SELECT * INTO selected FROM market_data.interval_series s WHERE s.id=series;
 IF NOT security.current_write_context() OR selected.id IS NULL OR security.market_access(selected.catalog_id,'ImportObservations') IS NOT TRUE
  OR security.reference_access(selected.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts r
 WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO stored FROM market_data.interval_series_requests r WHERE(r.catalog_id,r.actor_id,r.request_id)=(selected.catalog_id,c.actor_id,request);
 IF FOUND THEN
  IF stored.operation<>'revise' OR stored.request_sha256 IS DISTINCT FROM append_interval_series_revision.request_sha256
   OR stored.response->>'seriesId' IS DISTINCT FROM selected.id::text THEN RAISE EXCEPTION 'interval_series_conflict';END IF;
  RETURN stored.response;
 END IF;
 SELECT coalesce(max(r.revision),0) INTO head FROM market_data.interval_series_revisions r WHERE(r.catalog_id,r.series_id)=(selected.catalog_id,selected.id);
 IF expected IS DISTINCT FROM head THEN RAISE EXCEPTION 'interval_series_revision_conflict';END IF;
 SELECT * INTO bounds FROM ref_data.value_specifications v
 WHERE(v.catalog_id,v.revision_id,v.code)=(selected.reference_catalog_id,selected.reference_revision_id,selected.value_code);
 -- Every interval has the series' duration and no other interval starts inside it, in this revision
 -- or stored at another start, withdrawn or not: a stored interval changes only at its exact start.
 -- Values are canonical exact decimals, within the value specification's bounds when the series has one.
 IF total IS NULL OR total NOT BETWEEN 1 AND 200000 OR cardinality(ends) IS DISTINCT FROM total OR cardinality(vals) IS DISTINCT FROM total
  OR EXISTS(SELECT 1 FROM(SELECT u.s,u.e,u.v,lead(u.s) OVER(ORDER BY u.s) following FROM unnest(starts,ends,vals) u(s,e,v)) u
   WHERE CASE WHEN u.s IS NULL OR u.e IS NULL OR NOT isfinite(u.s) OR NOT isfinite(u.e) THEN true
    WHEN u.e-u.s<>make_interval(mins=>selected.interval_minutes) OR u.following<u.e THEN true
    WHEN u.v IS NULL THEN false
    WHEN u.v!~'^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$' OR NOT pg_input_is_valid(u.v,'ref_data.exact_number') THEN true
    ELSE bounds.code IS NOT NULL AND u.v::numeric NOT BETWEEN bounds.minimum AND bounds.maximum END)
  -- One bounded value_history probe per new interval; LIMIT keeps the planner from scanning stored values per new row.
  OR EXISTS(SELECT 1 FROM unnest(starts,ends) u(s,e),LATERAL(SELECT 1 FROM market_data.interval_series_values v
   WHERE(v.catalog_id,v.series_id)=(selected.catalog_id,selected.id) AND v.starts_at>u.s-make_interval(mins=>selected.interval_minutes)
    AND v.starts_at<u.e AND v.starts_at<>u.s LIMIT 1) overlap)
 THEN RAISE EXCEPTION 'interval_series_values';END IF;
 SELECT coalesce(max(r.publication_order),0)+1 INTO ordinal FROM market_data.interval_series_revisions r WHERE r.catalog_id=selected.catalog_id;
 INSERT INTO market_data.interval_series_revisions VALUES(selected.catalog_id,selected.id,head+1,nullif(head,0),c.actor_id,c.tenant_id,c.workspace_id,
  reason,request,total,ordinal,pg_current_xact_id(),clock_timestamp());
 INSERT INTO market_data.interval_series_values
 SELECT selected.catalog_id,selected.id,head+1,u.s,u.e,u.v::ref_data.exact_number FROM unnest(starts,ends,vals) u(s,e,v);
 IF security.market_access(selected.catalog_id,'ImportObservations') IS NOT TRUE OR security.reference_access(selected.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 result=jsonb_build_object('requestId',request,'seriesId',selected.id,'revision',head+1,'intervals',total);
 INSERT INTO market_data.interval_series_requests VALUES(selected.catalog_id,c.actor_id,request,'revise',append_interval_series_revision.request_sha256,result);
 RETURN result;
END$$;

-- Shared by both readers: current authority, a bounded window, the certified context and the revisions known at the cutoff.
CREATE FUNCTION market_data.open_interval_series_read(selected market_data.interval_series,first_at timestamptz,last_at timestamptz,cutoff timestamptz,
 OUT context_id uuid,OUT known integer[],OUT description jsonb)
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE seconds numeric;
BEGIN
 IF NOT security.current_write_context() OR selected.id IS NULL OR security.market_access(selected.catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_access(selected.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'interval_series_denied' USING ERRCODE='42501';END IF;
 IF first_at IS NULL OR last_at IS NULL OR cutoff IS NULL OR NOT isfinite(first_at) OR NOT isfinite(last_at) OR NOT isfinite(cutoff)
  OR first_at>=last_at OR cutoff>clock_timestamp() THEN RAISE EXCEPTION 'interval_series_window';END IF;
 seconds=extract(epoch FROM last_at-first_at);
 IF mod(seconds,60*selected.interval_minutes)<>0 OR seconds>35136*60*selected.interval_minutes THEN RAISE EXCEPTION 'interval_series_window';END IF;
 context_id=security.capture_date_context(selected.catalog_id,(first_at AT TIME ZONE 'UTC')::date,cutoff);
 SELECT coalesce(array_agg(r.revision ORDER BY r.revision),'{}') INTO known FROM market_data.interval_series_revisions r
 JOIN market_data.interval_series_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(r.catalog_id,r.publication_order,r.publication_xid)
 WHERE(r.catalog_id,r.series_id)=(selected.catalog_id,selected.id) AND k.known_from<=cutoff;
 description=jsonb_build_object('id',selected.id,'code',selected.code,'name',selected.name,'kind',selected.kind,
  'specification',coalesce(selected.quote_code,selected.value_code,selected.quantity_code),'clock',selected.clock_code,'intervalMinutes',selected.interval_minutes);
END$$;

CREATE FUNCTION market_data.read_interval_series(series uuid,first_at timestamptz,last_at timestamptz,cutoff timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE selected market_data.interval_series;opened record;listed jsonb;
BEGIN
 SELECT * INTO selected FROM market_data.interval_series s WHERE s.id=series;
 SELECT * INTO opened FROM market_data.open_interval_series_read(selected,first_at,last_at,cutoff);
 -- The latest known revision of each interval decides it; a withdrawal leaves no row.
 SELECT coalesce(jsonb_agg(jsonb_build_object('start',v.starts_at,'end',v.ends_at,'value',v.value::text,'revision',v.revision) ORDER BY v.starts_at),'[]')
 INTO listed FROM(SELECT DISTINCT ON(v.starts_at) v.starts_at,v.ends_at,v.value,v.revision FROM market_data.interval_series_values v
  WHERE(v.catalog_id,v.series_id)=(selected.catalog_id,selected.id) AND v.revision=ANY(opened.known)
   AND v.starts_at>=first_at AND v.starts_at<last_at AND v.ends_at<=last_at ORDER BY v.starts_at,v.revision DESC) v
 WHERE v.value IS NOT NULL;
 RETURN jsonb_build_object('series',opened.description,'knownAt',cutoff,'contextId',opened.context_id,
  'head',coalesce(opened.known[cardinality(opened.known)],0),'rows',listed);
END$$;

CREATE FUNCTION market_data.interval_series_changes(series uuid,first_at timestamptz,last_at timestamptz,cutoff timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE selected market_data.interval_series;opened record;total bigint;listed jsonb;summary jsonb;
BEGIN
 SELECT * INTO selected FROM market_data.interval_series s WHERE s.id=series;
 SELECT * INTO opened FROM market_data.open_interval_series_read(selected,first_at,last_at,cutoff);
 WITH audit AS(
  SELECT r.revision,r.reason,r.request_id,r.interval_count,k.known_from,m.display_name actor FROM market_data.interval_series_revisions r
  JOIN market_data.interval_series_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(r.catalog_id,r.publication_order,r.publication_xid)
  JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(r.tenant_id,r.workspace_id,r.actor_id)
  WHERE(r.catalog_id,r.series_id)=(selected.catalog_id,selected.id) AND r.revision=ANY(opened.known)),
 history AS(
  SELECT v.starts_at,v.ends_at,v.revision,v.value,lag(v.revision) OVER w previous_revision,lag(v.value) OVER w previous_value
  FROM market_data.interval_series_values v
  WHERE(v.catalog_id,v.series_id)=(selected.catalog_id,selected.id) AND v.revision=ANY(opened.known)
   AND v.starts_at>=first_at AND v.starts_at<last_at AND v.ends_at<=last_at
  WINDOW w AS(PARTITION BY v.starts_at ORDER BY v.revision) LIMIT 100001)
 SELECT(SELECT count(*) FROM history),
  (SELECT coalesce(jsonb_agg(jsonb_build_object('start',h.starts_at,'end',h.ends_at,'revision',h.revision,'previousRevision',h.previous_revision,
    'previousValue',h.previous_value::text,'value',h.value::text,'actor',a.actor,'reason',a.reason,'knownFrom',a.known_from,'requestId',a.request_id)
    ORDER BY h.revision DESC,h.starts_at),'[]') FROM history h JOIN audit a USING(revision)),
  (SELECT coalesce(jsonb_agg(jsonb_build_object('revision',a.revision,'actor',a.actor,'reason',a.reason,'knownFrom',a.known_from,'requestId',a.request_id,
    'intervals',a.interval_count,'intervalsInWindow',w.intervals) ORDER BY a.revision DESC),'[]')
   FROM audit a JOIN(SELECT h.revision,count(*) intervals FROM history h GROUP BY h.revision) w USING(revision))
 INTO total,listed,summary;
 IF total>100000 THEN RAISE EXCEPTION 'interval_series_window';END IF;
 RETURN jsonb_build_object('series',opened.description,'knownAt',cutoff,'revisions',summary,'rows',listed);
END$$;

-- Certify this stream under the same transaction gate and cutoff as schedules and market data.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_interval_series_context;
REVOKE ALL ON FUNCTION security.capture_pre_interval_series_context(uuid,date,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.interval_series_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT r.* FROM market_data.interval_series_revisions r LEFT JOIN market_data.interval_series_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE r.catalog_id=catalog AND k.publication_order IS NULL ORDER BY r.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.interval_series_knowledge_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_interval_series_context(catalog,effective_date,requested_at);
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Temporal authority expired' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE(n.nspname='security' AND p.proname='capture_date_context') OR(n.nspname='market_data' AND p.proname IN('define_interval_series',
  'append_interval_series_revision','open_interval_series_read','read_interval_series','interval_series_changes')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname<>'open_interval_series_read' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
