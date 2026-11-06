-- D334: a close belongs to an exact versioned close session and an explicit business day. For each session and business
-- day the latest recorded close is in force; earlier ones remain as history. Recording needs a per-session grant.
-- Existing closes are retired, not mapped: this migration requires an empty close history.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM market_data.market_closes) THEN RAISE EXCEPTION 'regional_closes_requires_empty_close_history';END IF;
END$$;

CREATE TABLE market_data.close_sessions(
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 80),
 PRIMARY KEY(catalog_id,code)
);
-- A version pins one definition revision, so its curves are exact; its clock is its calendar's clock.
CREATE TABLE market_data.close_session_versions(
 catalog_id uuid NOT NULL,code ref_data.code NOT NULL,version integer NOT NULL CHECK(version>0),
 state text NOT NULL CHECK(state IN('draft','published')),generation integer NOT NULL CHECK(generation>0),
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 calendar_code ref_data.code NOT NULL,cutoff_local time NOT NULL CHECK(cutoff_local=date_trunc('minute',cutoff_local)),
 effective_from date NOT NULL,effective_to date NOT NULL CHECK(isfinite(effective_from) AND isfinite(effective_to) AND effective_from<effective_to),
 actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,request jsonb NOT NULL,
 publisher_id uuid REFERENCES security.principals(id),publish_request_id uuid,CHECK((state='published')=(publisher_id IS NOT NULL AND publish_request_id IS NOT NULL)),
 PRIMARY KEY(catalog_id,code,version),UNIQUE(catalog_id,publisher_id,publish_request_id),UNIQUE(catalog_id,code,version,definition_revision_id),UNIQUE(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,code) REFERENCES market_data.close_sessions(catalog_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code)
);
CREATE TABLE market_data.close_session_curves(
 catalog_id uuid NOT NULL,code ref_data.code NOT NULL,version integer NOT NULL,definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,code,version,curve_code),
 FOREIGN KEY(catalog_id,code,version,definition_revision_id) REFERENCES market_data.close_session_versions(catalog_id,code,version,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
CREATE TABLE security.close_session_grants(
 id uuid PRIMARY KEY,principal_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,
 catalog_id uuid NOT NULL,session_code ref_data.code NOT NULL,
 granted_from ref_data.instant NOT NULL,granted_to timestamptz NOT NULL,revoked boolean NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,UNIQUE(actor_id,request_id),
 revoker_id uuid REFERENCES security.principals(id),revoke_request_id uuid,CHECK(revoked=(revoker_id IS NOT NULL AND revoke_request_id IS NOT NULL)),
 UNIQUE(revoker_id,revoke_request_id),
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(catalog_id,session_code) REFERENCES market_data.close_sessions(catalog_id,code),
 CHECK(isfinite(granted_to) AND granted_to<=TIMESTAMPTZ '9999-12-31 00:00:00+00' AND granted_from<granted_to)
);

ALTER TABLE market_data.market_closes
 ADD COLUMN session_code ref_data.code NOT NULL,ADD COLUMN session_version integer NOT NULL,ADD COLUMN business_date date NOT NULL,
 ADD FOREIGN KEY(catalog_id,session_code,session_version,definition_revision_id)
  REFERENCES market_data.close_session_versions(catalog_id,code,version,definition_revision_id);
CREATE INDEX market_close_in_force ON market_data.market_closes(catalog_id,session_code,business_date,publication_order DESC);

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['close_sessions','close_session_versions','close_session_curves'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData''))',t);
  EXECUTE format('CREATE POLICY owner_all ON market_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
 ALTER TABLE security.close_session_grants OWNER TO openetrm_security_owner;
 ALTER TABLE security.close_session_grants ENABLE ROW LEVEL SECURITY;
 ALTER TABLE security.close_session_grants FORCE ROW LEVEL SECURITY;
 CREATE POLICY owner_all ON security.close_session_grants TO openetrm_security_owner USING(true) WITH CHECK(true);
END$$;

-- The UTC instant of a session's cutoff on one business day, on the clock of the session's calendar. A day outside the
-- version's range, outside its calendar or not a business day is refused (close_session); a cutoff that does not exist or
-- exists twice on that day's local clock, or falls on another UTC date, is refused (close_session_instant). Nothing moves the
-- day or the time.
CREATE FUNCTION security.close_session_instant(catalog uuid,session text,session_version integer,business_date date) RETURNS timestamptz
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v market_data.close_session_versions;cal ref_data.calendars;clock ref_data.clock_rules;open boolean;
 local timestamp;base timestamptz;candidates timestamptz[];
BEGIN
 SELECT * INTO v FROM market_data.close_session_versions s WHERE(s.catalog_id,s.code,s.version)=(catalog,session,session_version) AND s.state='published';
 IF NOT FOUND OR business_date IS NULL OR business_date<v.effective_from OR business_date>=v.effective_to THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT * INTO cal FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code)=(v.reference_catalog_id,v.reference_revision_id,v.calendar_code);
 IF business_date<cal.coverage_from OR business_date>=cal.coverage_to THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT d.is_business_day INTO open FROM ref_data.calendar_days d
  WHERE(d.catalog_id,d.revision_id,d.calendar_code,d.business_date)=(cal.catalog_id,cal.revision_id,cal.code,close_session_instant.business_date);
 IF NOT coalesce(open,(cal.weekend_mask&(1<<(extract(isodow FROM business_date)::integer-1)))=0) THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT * INTO clock FROM ref_data.clock_rules r WHERE(r.catalog_id,r.revision_id,r.code)=(cal.catalog_id,cal.revision_id,cal.clock_code);
 local=business_date+v.cutoff_local;
 IF clock.kind='fixed_offset' THEN candidates=ARRAY[(local-make_interval(secs=>clock.fixed_offset_seconds)) AT TIME ZONE 'UTC'];
 ELSE
  -- The local time's instants under the offsets in force a day either side; exactly one must map back to it.
  base=local AT TIME ZONE 'UTC';
  SELECT array_agg(DISTINCT c) INTO candidates FROM(
   SELECT(local-((t AT TIME ZONE clock.iana_zone)-(t AT TIME ZONE 'UTC'))) AT TIME ZONE 'UTC' c
   FROM unnest(ARRAY[base-interval '1 day',base+interval '1 day'])t)x
  WHERE(c AT TIME ZONE clock.iana_zone)=local;
 END IF;
 -- Every valuation reads a close on its business day's UTC date, so a cutoff that falls on another UTC date is refused.
 IF coalesce(cardinality(candidates),0)<>1 OR(candidates[1] AT TIME ZONE 'UTC')::date<>business_date
 THEN RAISE EXCEPTION 'close_session_instant' USING ERRCODE='P0001';END IF;
 RETURN candidates[1];
END;$$;

-- The close in force for a session and business day: the highest publication order among all closes recorded for the
-- pair, whoever reads it. Readers that cannot see it are refused by their read, never served an older close.
CREATE FUNCTION security.close_in_force(catalog uuid,session text,business_date date) RETURNS uuid
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 SELECT s.id INTO result FROM market_data.market_closes s
 WHERE(s.catalog_id,s.session_code,s.business_date)=(catalog,session,close_in_force.business_date) ORDER BY s.publication_order DESC LIMIT 1;
 RETURN result;
END;$$;

-- Whether the request's actor may record, and so replace, a close for the session: publishing authority and a live grant.
-- A close is a person's sign-off, so a delegated request records none.
CREATE FUNCTION security.can_record_close(catalog uuid,session text) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'PublishMarketData') AND EXISTS(SELECT 1 FROM security.request_contexts c
  JOIN security.close_session_grants g ON(g.principal_id,g.tenant_id,g.workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.delegation_id IS NULL AND c.expires_at>clock_timestamp() AND(g.catalog_id,g.session_code)=(catalog,session) AND NOT g.revoked
  AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to);
$$;

-- A published session version and its curves never change; a draft changes only by being published.
CREATE FUNCTION market_data.guard_close_session() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='UPDATE' AND TG_TABLE_NAME='close_session_versions' AND OLD.state='draft' AND NEW.state='published'
 AND(to_jsonb(NEW)-ARRAY['state','generation','publisher_id','publish_request_id'])=(to_jsonb(OLD)-ARRAY['state','generation','publisher_id','publish_request_id'])
 THEN RETURN NEW;END IF;
 RAISE EXCEPTION 'close_session_immutable' USING ERRCODE='55000';
END;$$;
CREATE TRIGGER immutable_session BEFORE UPDATE OR DELETE ON market_data.close_session_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_close_session();
CREATE TRIGGER immutable_session BEFORE UPDATE OR DELETE ON market_data.close_session_curves FOR EACH ROW EXECUTE FUNCTION market_data.guard_close_session();
CREATE TRIGGER immutable_session BEFORE UPDATE OR DELETE ON market_data.close_sessions FOR EACH ROW EXECUTE FUNCTION market_data.guard_close_session();

CREATE FUNCTION security.close_session_json(catalog uuid,session text,session_version integer) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('code',v.code,'name',s.name,'version',v.version,'state',v.state,'generation',v.generation,
  'calendarCode',v.calendar_code,'clockCode',c.clock_code,'cutoff',to_char(v.cutoff_local,'HH24:MI'),
  'curves',(SELECT jsonb_agg(k.curve_code ORDER BY k.curve_code COLLATE "C") FROM market_data.close_session_curves k
   WHERE(k.catalog_id,k.code,k.version)=(v.catalog_id,v.code,v.version)),
  'effectiveFrom',v.effective_from,'effectiveTo',v.effective_to)
 FROM market_data.close_session_versions v JOIN market_data.close_sessions s USING(catalog_id,code)
 JOIN ref_data.calendars c ON(c.catalog_id,c.revision_id,c.code)=(v.reference_catalog_id,v.reference_revision_id,v.calendar_code)
 WHERE(v.catalog_id,v.code,v.version)=(catalog,session,session_version);
$$;

-- The published version governing a session business day: the one with the latest start that covers it (D334). A later
-- version takes over from its own start, so a session can change its cutoff or curves without a new code.
CREATE FUNCTION security.close_session_version_on(catalog uuid,session text,business_date date) RETURNS integer
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT v.version FROM market_data.close_session_versions v
 WHERE(v.catalog_id,v.code,v.state)=(catalog,session,'published') AND v.effective_from<=business_date AND business_date<v.effective_to
  AND security.market_access(catalog,'ReadMarketData')
 ORDER BY v.effective_from DESC LIMIT 1;
$$;

CREATE FUNCTION security.close_session_catalog(catalog uuid) RETURNS jsonb
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN coalesce((SELECT jsonb_agg(security.close_session_json(catalog,v.code,v.version) ORDER BY v.code COLLATE "C",v.version)
  FROM market_data.close_session_versions v WHERE v.catalog_id=catalog),'[]'::jsonb);
END;$$;

-- Saves a new draft version of a session over the catalog's exact definition revision. A retried request returns its
-- first outcome; the same request with other content is a conflict.
CREATE FUNCTION security.save_close_session(catalog uuid,definition uuid,operation uuid,draft jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;r market_data.revisions;previous market_data.close_session_versions;next_version integer;
 session text;cutoff time;curves text[];
BEGIN
 IF NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT * INTO previous FROM market_data.close_session_versions WHERE catalog_id=catalog AND actor_id=c.actor_id AND request_id=operation;
 IF FOUND THEN
  IF previous.request<>draft OR previous.definition_revision_id<>definition THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.close_session_json(catalog,previous.code,previous.version);
 END IF;
 session=draft->>'code';
 IF jsonb_typeof(draft) IS DISTINCT FROM 'object' OR session IS NULL OR session!~'^[a-z][a-z0-9_]{0,63}$'
 OR jsonb_typeof(draft->'name') IS DISTINCT FROM 'string' OR length(btrim(draft->>'name')) NOT BETWEEN 1 AND 80
 OR coalesce(draft->>'cutoff','')!~'^([01][0-9]|2[0-3]):[0-5][0-9]$' OR jsonb_typeof(draft->'curves') IS DISTINCT FROM 'array'
 OR jsonb_array_length(draft->'curves') NOT BETWEEN 1 AND 512
 OR coalesce(draft->>'effectiveFrom','')!~'^\d{4}-\d{2}-\d{2}$' OR coalesce(draft->>'effectiveTo','')!~'^\d{4}-\d{2}-\d{2}$'
 OR(draft->>'effectiveFrom')::date>=(draft->>'effectiveTo')::date THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT array_agg(DISTINCT x) INTO curves FROM jsonb_array_elements_text(draft->'curves')x;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition) AND state='sealed';
 IF NOT FOUND OR cardinality(curves)<>jsonb_array_length(draft->'curves')
 OR EXISTS(SELECT 1 FROM unnest(curves)k WHERE NOT EXISTS(SELECT 1 FROM market_data.curves m WHERE(m.catalog_id,m.revision_id,m.code)=(catalog,definition,k)))
 OR NOT EXISTS(SELECT 1 FROM ref_data.calendars cal WHERE(cal.catalog_id,cal.revision_id,cal.code)=(r.reference_catalog_id,r.reference_revision_id,draft->>'calendarCode'))
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM market_data.close_sessions s WHERE(s.catalog_id,s.code)=(catalog,session) AND s.name<>draft->>'name')
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 INSERT INTO market_data.close_sessions VALUES(catalog,session,draft->>'name') ON CONFLICT DO NOTHING;
 SELECT coalesce(max(version),0)+1 INTO next_version FROM market_data.close_session_versions WHERE(catalog_id,code)=(catalog,session);
 INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
  calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request)
 VALUES(catalog,session,next_version,'draft',1,definition,r.reference_catalog_id,r.reference_revision_id,draft->>'calendarCode',(draft->>'cutoff')::time,
  (draft->>'effectiveFrom')::date,(draft->>'effectiveTo')::date,c.actor_id,operation,draft);
 INSERT INTO market_data.close_session_curves SELECT catalog,session,next_version,definition,k FROM unnest(curves)k;
 IF NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN security.close_session_json(catalog,session,next_version);
END;$$;

-- Publishes a draft at its expected generation. Published versions of one session never cover the same business day.
CREATE FUNCTION security.publish_close_session(catalog uuid,operation uuid,session text,session_version integer,expected_generation integer) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;v market_data.close_session_versions;
BEGIN
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO v FROM market_data.close_session_versions s WHERE(s.catalog_id,s.code,s.version)=(catalog,session,session_version) FOR UPDATE;
 IF NOT FOUND OR operation IS NULL THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 IF v.state='published' THEN
  IF(v.publisher_id,v.publish_request_id)<>(c.actor_id,operation) OR v.generation<>expected_generation+1 THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.close_session_json(catalog,session,session_version);
 END IF;
 IF v.generation<>expected_generation OR EXISTS(SELECT 1 FROM market_data.close_session_versions o WHERE(o.catalog_id,o.code)=(catalog,session) AND o.publisher_id=c.actor_id AND o.publish_request_id=operation)
 THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
 -- A version supersedes the published ones it overlaps from its own start; one starting no later than them would leave a day
 -- with two governing versions.
 IF EXISTS(SELECT 1 FROM market_data.close_session_versions o WHERE(o.catalog_id,o.code)=(catalog,session) AND o.state='published'
  AND o.effective_from<v.effective_to AND v.effective_from<o.effective_to AND o.effective_from>=v.effective_from) THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 UPDATE market_data.close_session_versions SET state='published',generation=v.generation+1,publisher_id=c.actor_id,publish_request_id=operation
 WHERE(catalog_id,code,version)=(catalog,session,session_version);
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN security.close_session_json(catalog,session,session_version);
END;$$;

CREATE FUNCTION security.close_grant_json(g security.close_session_grants) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',g.id,'principalId',g.principal_id,'session',g.session_code,'grantedFrom',g.granted_from,'grantedTo',g.granted_to,'revoked',g.revoked);
$$;
CREATE FUNCTION security.require_close_administration() RETURNS security.request_contexts
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 IF NOT security.has_permission('ManageAccess') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND administration;
 IF NOT FOUND OR c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN c;
END;$$;
-- Grants one workspace member the authority to record, and so replace, one session's closes.
CREATE FUNCTION security.grant_close_session(catalog uuid,operation uuid,principal uuid,session text,granted_from timestamptz,granted_to timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts=security.require_close_administration();g security.close_session_grants;
BEGIN
 SELECT * INTO g FROM security.close_session_grants WHERE(actor_id,request_id)=(c.actor_id,operation);
 IF FOUND THEN
  IF(g.catalog_id,g.principal_id,g.session_code,g.granted_from,g.granted_to)<>(catalog,principal,session,granted_from,granted_to) THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.close_grant_json(g);
 END IF;
 IF operation IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.close_sessions s WHERE(s.catalog_id,s.code)=(catalog,session))
 OR NOT EXISTS(SELECT 1 FROM security.workspace_memberships m WHERE(m.tenant_id,m.workspace_id,m.principal_id)=(c.tenant_id,c.workspace_id,principal) AND m.enabled)
 OR NOT EXISTS(SELECT 1 FROM security.market_catalog_scopes m WHERE(m.tenant_id,m.workspace_id,m.catalog_id)=(c.tenant_id,c.workspace_id,catalog))
 OR granted_from IS NULL OR granted_to IS NULL OR granted_from>=granted_to THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 INSERT INTO security.close_session_grants VALUES(gen_random_uuid(),principal,c.tenant_id,c.workspace_id,catalog,session,granted_from,granted_to,false,c.actor_id,operation,NULL,NULL)
 RETURNING * INTO g;
 RETURN security.close_grant_json(g);
END;$$;
CREATE FUNCTION security.revoke_close_session_grant(operation uuid,grant_id uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts=security.require_close_administration();g security.close_session_grants;
BEGIN
 SELECT * INTO g FROM security.close_session_grants WHERE id=grant_id AND(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE;
 IF NOT FOUND OR operation IS NULL THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 IF g.revoked THEN
  IF(g.revoker_id,g.revoke_request_id)<>(c.actor_id,operation) THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.close_grant_json(g);
 END IF;
 UPDATE security.close_session_grants SET revoked=true,revoker_id=c.actor_id,revoke_request_id=operation WHERE id=grant_id RETURNING * INTO g;
 RETURN security.close_grant_json(g);
END;$$;
CREATE FUNCTION security.close_session_grants() RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 IF NOT security.has_permission('ManageAccess') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 RETURN coalesce((SELECT jsonb_agg(security.close_grant_json(g) ORDER BY g.session_code,g.granted_from,g.id) FROM security.close_session_grants g
  WHERE(g.tenant_id,g.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb);
END;$$;

-- A close carries exactly its session version's curves. The refusal names them in its message, after the code.
CREATE FUNCTION security.require_session_curves(catalog uuid,session text,session_version integer,members jsonb) RETURNS void
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE missing text;extra text;
BEGIN
 SELECT string_agg(k.curve_code,', ' ORDER BY k.curve_code COLLATE "C") INTO missing FROM market_data.close_session_curves k
 WHERE(k.catalog_id,k.code,k.version)=(catalog,session,session_version) AND NOT EXISTS(SELECT 1 FROM jsonb_to_recordset(members)x(curve text) WHERE x.curve=k.curve_code);
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'close_incomplete: missing curves %',missing USING ERRCODE='P0001';END IF;
 SELECT string_agg(DISTINCT x.curve,', ' ORDER BY x.curve) INTO extra FROM jsonb_to_recordset(members)x(curve text)
 WHERE NOT EXISTS(SELECT 1 FROM market_data.close_session_curves k WHERE(k.catalog_id,k.code,k.version,k.curve_code)=(catalog,session,session_version,x.curve));
 IF extra IS NOT NULL THEN RAISE EXCEPTION 'close_outside_session: curves outside the session %',extra USING ERRCODE='P0001';END IF;
END;$$;

-- Records a close for one session and business day from an exact frame. The frame must be at the session cutoff on that
-- day and carry exactly the session's curves; recording needs a grant for the session. Retries return the first close.
DROP FUNCTION security.register_market_close(uuid,uuid,uuid,text,timestamptz,jsonb,jsonb);
CREATE FUNCTION security.register_market_close(catalog uuid,definition uuid,operation uuid,close_name text,session text,business_date date,
 frame_at timestamptz,pins jsonb,members jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;r market_data.revisions;v market_data.close_session_versions;previous market_data.market_closes;
 payload jsonb;identity uuid;ordinal integer;close_at timestamptz;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'PublishMarketData') THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.can_record_close(catalog,session) THEN RAISE EXCEPTION 'close_grant' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition);
 IF NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id)
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR close_name IS NULL OR length(btrim(close_name)) NOT BETWEEN 1 AND 80
 OR frame_at IS NULL OR NOT isfinite(frame_at) OR jsonb_typeof(pins) IS DISTINCT FROM 'array'
 OR jsonb_array_length(pins) NOT BETWEEN 1 AND 20000 OR jsonb_typeof(members) IS DISTINCT FROM 'array' OR jsonb_array_length(members) NOT BETWEEN 1 AND 20000
 THEN RAISE EXCEPTION 'pilot_close_input' USING ERRCODE='P0001';END IF;
 payload=jsonb_build_object('definition',definition,'name',close_name,'session',session,'businessDate',business_date,'at',frame_at,'pins',pins,'members',members);
 SELECT * INTO previous FROM market_data.market_closes WHERE catalog_id=catalog AND actor_id=c.actor_id AND request_id=operation;
 IF FOUND THEN
  IF previous.request<>payload THEN RAISE EXCEPTION 'pilot_close_conflict' USING ERRCODE='P0001';END IF;
  IF NOT security.market_close_visible(catalog,previous.id) THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
  RETURN previous.id;
 END IF;
 SELECT * INTO v FROM market_data.close_session_versions s
  WHERE(s.catalog_id,s.code,s.version)=(catalog,session,security.close_session_version_on(catalog,session,business_date));
 IF NOT FOUND OR v.definition_revision_id<>definition THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 close_at=security.close_session_instant(catalog,session,v.version,business_date);
 IF frame_at<>close_at OR close_at>clock_timestamp() THEN RAISE EXCEPTION 'close_session_instant' USING ERRCODE='P0001';END IF;
 PERFORM security.require_session_curves(catalog,session,v.version,members);
 IF r.state IS DISTINCT FROM 'sealed' OR NOT EXISTS(SELECT 1 FROM market_data.revision_periods p WHERE(p.catalog_id,p.revision_id)=(catalog,definition)
 AND p.effective_from<=business_date AND business_date<p.effective_to)
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(pins) x("seriesId" uuid,"observationId" uuid,"inputUse" text)
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id,o.definition_revision_id,o.series_id)=(catalog,x."observationId",definition,x."seriesId")
 WHERE o.id IS NULL OR o.observed_at>close_at OR NOT security.market_fact_visible(catalog,definition,o.id)
 OR x."inputUse" IS NULL OR x."inputUse" NOT IN('variable','frozen') OR(x."inputUse"='frozen')<>(o.role IN('final_contract_settlement','index_fixing'))
 OR(x."inputUse"='frozen' AND o.publication_state<>'final'))
 THEN RAISE EXCEPTION 'pilot_close_inputs' USING ERRCODE='P0001';END IF;
 identity=gen_random_uuid();SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.market_closes WHERE catalog_id=catalog;
 INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,
  publication_order,publication_xid,session_code,session_version,business_date)
 VALUES(catalog,identity,definition,r.reference_catalog_id,r.reference_revision_id,close_name,close_at,'retained_input',c.actor_id,operation,payload,
  ordinal,pg_current_xact_id(),session,v.version,business_date);
 INSERT INTO market_data.market_close_pins SELECT catalog,identity,definition,x."seriesId",x."observationId",x."inputUse" FROM jsonb_to_recordset(pins)x("seriesId" uuid,"observationId" uuid,"inputUse" text);
 INSERT INTO market_data.market_close_members SELECT catalog,identity,definition,x.curve,x."contractId",x.ordinal FROM jsonb_to_recordset(members)x(curve text,"contractId" uuid,ordinal integer);
 IF NOT security.market_access(catalog,'PublishMarketData') OR NOT security.can_record_close(catalog,session) THEN RAISE EXCEPTION 'close_grant' USING ERRCODE='42501';END IF;
 RETURN identity;
END;$$;

-- A session business day's visible recorded closes, newest first: the close in force and the history it replaced, with who
-- recorded each and when. History is for reading; nothing here selects a replaced close.
CREATE FUNCTION security.close_history(catalog uuid,session text,business_date date) RETURNS jsonb
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;current uuid=security.close_in_force(catalog,session,business_date);rows jsonb;
BEGIN
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',s.id,'name',s.name,'at',s.at,'recordedBy',m.display_name,
  'recordedAt',security.checked_commit_time(s.publication_xid),'inForce',s.id=current) ORDER BY s.publication_order DESC),'[]'::jsonb) INTO rows
 FROM market_data.market_closes s LEFT JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(c.tenant_id,c.workspace_id,s.actor_id)
 WHERE(s.catalog_id,s.session_code,s.business_date)=(catalog,session,close_history.business_date) AND security.market_close_visible(catalog,s.id);
 RETURN jsonb_build_object('session',session,'businessDate',business_date,
  'inForce',(SELECT r FROM jsonb_array_elements(rows)r WHERE(r->>'inForce')::boolean),
  'replaced',coalesce((SELECT jsonb_agg(r) FROM jsonb_array_elements(rows)r WHERE NOT(r->>'inForce')::boolean),'[]'::jsonb));
END;$$;

-- Always latest (D334, a recorded exception to the knowledge cutoff): under a historical context the close in force for a
-- session and business day is visible even if it, or its pins, became known after the context's cutoff. Its definition and
-- reference revisions must still be visible, and its pins must lie in revision periods covering the context's date. Every
-- other close keeps the exact knowledge rules of 0100.
CREATE OR REPLACE FUNCTION security.market_close_visible(catalog uuid,close_id uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected market_data.market_closes;request security.request_contexts;
 temporal market_data.temporal_contexts;known integer;latest boolean;
BEGIN
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF request.id IS NULL OR request.expires_at<=clock_timestamp() THEN RETURN false;END IF;
 SELECT * INTO selected FROM market_data.market_closes s WHERE(s.catalog_id,s.id)=(market_close_visible.catalog,market_close_visible.close_id);
 IF NOT FOUND THEN RETURN false;END IF;
 IF security.market_fact_visible(catalog,selected.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) IS NOT TRUE THEN RETURN false;END IF;
 IF request.market_context_id IS NOT NULL THEN
  SELECT * INTO temporal FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(request.market_catalog_id,request.market_context_id);
  IF NOT FOUND OR temporal.catalog_id IS DISTINCT FROM catalog THEN RETURN false;END IF;
  SELECT m.known_through INTO known FROM market_data.market_contexts m WHERE(m.catalog_id,m.context_id)=(temporal.catalog_id,temporal.id);
  IF NOT FOUND THEN RETURN false;END IF;
  latest=selected.id=(SELECT s.id FROM market_data.market_closes s WHERE(s.catalog_id,s.session_code,s.business_date)=(selected.catalog_id,selected.session_code,selected.business_date)
   ORDER BY s.publication_order DESC LIMIT 1);
  IF latest THEN
   -- A view never reaches a business day after its own date.
   IF selected.business_date>temporal.effective_on THEN RETURN false;END IF;
   IF EXISTS(SELECT 1 FROM market_data.market_close_pins p JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
    WHERE(p.catalog_id,p.close_id)=(market_close_visible.catalog,market_close_visible.close_id) AND(o.observed_at>selected.at OR NOT EXISTS(SELECT 1 FROM market_data.revision_periods v
     WHERE(v.catalog_id,v.revision_id)=(p.catalog_id,p.definition_revision_id) AND v.effective_from<=temporal.effective_on AND temporal.effective_on<v.effective_to)))
   THEN RETURN false;END IF;
  ELSE
   IF NOT EXISTS(SELECT 1 FROM market_data.market_close_receipts r
    WHERE(r.catalog_id,r.publication_order,r.publication_xid)=(selected.catalog_id,selected.publication_order,selected.publication_xid)
    AND r.known_from<=temporal.known_at) THEN RETURN false;END IF;
   IF EXISTS(SELECT 1 FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id)=(market_close_visible.catalog,market_close_visible.close_id)
    AND NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e
     JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
     JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(e.catalog_id,e.revision_id)
     JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
     WHERE(e.catalog_id,e.revision_id,e.observation_id)=(p.catalog_id,p.definition_revision_id,p.observation_id)
     AND e.event_order<=known AND k.known_from<=temporal.known_at
     AND v.effective_from<=temporal.effective_on AND temporal.effective_on<v.effective_to
     AND o.observed_at<((temporal.effective_on+1)::timestamp AT TIME ZONE 'UTC')))
   THEN RETURN false;END IF;
  END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
  AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 OR security.market_fact_visible(catalog,selected.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) IS NOT TRUE THEN RETURN false;END IF;
 RETURN true;
END;$$;
-- The close session a request reads (D334). "Always latest" releases only the pins of that session's closes in force, so one
-- region's correction never becomes another region's knowledge. A request binds at most one session.
ALTER TABLE security.request_contexts ADD COLUMN close_session ref_data.code;
CREATE FUNCTION security.use_close_session(catalog uuid,session text) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT EXISTS(SELECT 1 FROM market_data.close_sessions s WHERE(s.catalog_id,s.code)=(catalog,session))
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 UPDATE security.request_contexts SET close_session=session WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND(close_session IS NULL OR close_session=session);
 IF NOT FOUND THEN RAISE EXCEPTION 'A request reads exactly one close session' USING ERRCODE='42501';END IF;
END;$$;

-- The pinned observations of the bound session's closes in force for the context's date are visible whatever its knowledge
-- cutoff: the prices "always latest" reads (D334). Every other observation keeps the 0158 rules exactly.
CREATE OR REPLACE FUNCTION security.visible_observations() RETURNS TABLE(catalog uuid, revision uuid, observation uuid)
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
   AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC')
 UNION
 SELECT s.catalog_id,s.definition_revision_id,p.observation_id
  FROM market_data.market_closes s
  JOIN market_data.market_close_pins p ON(p.catalog_id,p.close_id)=(s.catalog_id,s.id)
  JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
  JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(s.catalog_id,s.definition_revision_id)
  WHERE s.catalog_id=cut.catalog AND s.business_date=cut.effective_on AND o.observed_at<=s.at
   AND s.session_code=(SELECT c.close_session FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
    AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user)
   AND v.effective_from<=cut.effective_on AND cut.effective_on<v.effective_to
   AND s.id=(SELECT l.id FROM market_data.market_closes l WHERE(l.catalog_id,l.session_code,l.business_date)=(s.catalog_id,s.session_code,s.business_date)
    ORDER BY l.publication_order DESC LIMIT 1)
   -- The close in force must itself be visible: its definition and reference revisions under the context (spec design call).
   AND security.market_close_visible(s.catalog_id,s.id);
END$$;
-- The per-row check keeps 0158's contract with visible_observations(): it releases exactly the same pinned prices of the bound
-- session's closes in force (D334), and otherwise keeps the 0037 rules.
CREATE OR REPLACE FUNCTION security.market_fact_visible(catalog uuid,revision uuid,observation uuid)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET plan_cache_mode=force_generic_plan AS $$
DECLARE session text;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RETURN false; END IF;
 IF NOT EXISTS(
  SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
   AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN RETURN true; END IF;
 IF EXISTS(
  SELECT 1 FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN LATERAL (
   SELECT event.* FROM market_data.knowledge_events event
   WHERE observation IS NULL AND event.catalog_id=t.catalog_id AND event.revision_id=revision AND event.observation_id IS NULL
   UNION ALL
   SELECT event.* FROM market_data.knowledge_events event
   WHERE observation IS NOT NULL AND event.catalog_id=t.catalog_id AND event.revision_id=revision AND event.observation_id=observation
  ) e ON true
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND t.catalog_id=catalog
   AND e.event_order<=m.known_through AND k.known_from<=t.known_at
   AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to
   AND(observation IS NULL OR EXISTS(SELECT 1 FROM market_data.observations o
    WHERE o.catalog_id=catalog AND o.id=observation AND o.observed_at<((t.effective_on+1)::timestamp AT TIME ZONE 'UTC')))
 ) THEN RETURN true; END IF;
 -- Only a request that reads a session releases its later pins; every other request keeps the 0037 decision unchanged.
 IF observation IS NULL THEN RETURN false; END IF;
 SELECT c.close_session INTO session FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 IF session IS NULL THEN RETURN false; END IF;
 RETURN EXISTS(
  SELECT 1 FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN market_data.market_close_pins p ON(p.catalog_id,p.observation_id)=(t.catalog_id,observation)
  JOIN market_data.market_closes s ON(s.catalog_id,s.id)=(p.catalog_id,p.close_id)
  JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
  JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(s.catalog_id,s.definition_revision_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
   AND t.catalog_id=catalog AND s.definition_revision_id=revision AND s.session_code=session
   AND s.business_date=t.effective_on AND o.observed_at<=s.at
   AND v.effective_from<=t.effective_on AND t.effective_on<v.effective_to
   AND s.id=(SELECT l.id FROM market_data.market_closes l WHERE(l.catalog_id,l.session_code,l.business_date)=(s.catalog_id,s.session_code,s.business_date)
    ORDER BY l.publication_order DESC LIMIT 1)
   AND security.market_close_visible(s.catalog_id,s.id));
END;
$$;
CREATE INDEX market_close_pin_observations ON market_data.market_close_pins(catalog_id,observation_id);

-- Whether the close was recorded after the bound historical context's knowledge cutoff; null in current working data.
CREATE FUNCTION security.close_recorded_after_cutoff(catalog uuid,close_id uuid) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT CASE WHEN q.market_context_id IS NULL THEN NULL ELSE NOT EXISTS(SELECT 1 FROM market_data.market_closes s
  JOIN market_data.market_close_receipts r ON(r.catalog_id,r.publication_order,r.publication_xid)=(s.catalog_id,s.publication_order,s.publication_xid)
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
  WHERE(s.catalog_id,s.id)=(catalog,close_id) AND r.known_from<=t.known_at) END
 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
$$;

-- A generated close is recorded for its profile's session, which the profile names explicitly (D334).
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM automation.pilot_close_profiles) THEN RAISE EXCEPTION 'regional_closes_requires_empty_close_history';END IF;
END$$;
ALTER TABLE automation.pilot_close_profiles ADD COLUMN session_code ref_data.code NOT NULL,
 ADD FOREIGN KEY(catalog_id,session_code) REFERENCES market_data.close_sessions(catalog_id,code);
DO $$DECLARE signature text='automation.create_pilot_close_job(jsonb,jsonb)';definition text;before text;BEGIN
 definition=pg_get_functiondef(signature::regprocedure);
 FOREACH before IN ARRAY ARRAY['definition-ARRAY[''priorCloseId'',''bookIds''','(definition ?& ARRAY[''priorCloseId'',''bookIds'''] LOOP
  IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'regional closes: unexpected pilot job definition';END IF;
  definition=replace(definition,before,replace(before,'[''priorCloseId''','[''session'',''priorCloseId'''));
 END LOOP;
 before:='p.valuation_policy=definition->''valuationPolicy'';';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'regional closes: unexpected pilot profile assignment';END IF;
 definition=replace(definition,before,before||'p.session_code=definition->>''session'';');
 -- A replaced close is not offered for new work: no job is admitted on it as an anchor.
 before:=' THEN RAISE EXCEPTION ''pilot_profile'';END IF;
 p.id=gen_random_uuid();';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'regional closes: unexpected pilot anchor check';END IF;
 EXECUTE replace(definition,before,' THEN RAISE EXCEPTION ''pilot_profile'';END IF;
 IF security.close_in_force(anchor.catalog_id,anchor.session_code,anchor.business_date)<>anchor.id THEN RAISE EXCEPTION ''close_replaced'' USING ERRCODE=''P0001'';END IF;
 p.id=gen_random_uuid();');
END$$;

DO $$DECLARE signature text='automation.pilot_definition(automation.jobs)';definition text;before text='jsonb_build_object(''priorCloseId'',p.prior_close_id,';BEGIN
 definition=pg_get_functiondef(signature::regprocedure);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'regional closes: unexpected pilot definition reader';END IF;
 EXECUTE replace(definition,before,'jsonb_build_object(''session'',p.session_code,''priorCloseId'',p.prior_close_id,');
END$$;

-- The session version and business day whose cutoff is exactly an instant: the day before, of or after its UTC date.
-- None or several is refused (close_session_instant); nothing picks the nearest day.
CREATE FUNCTION security.close_session_day(catalog uuid,session text,at timestamptz,OUT session_version integer,OUT business_date date)
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidate date;v market_data.close_session_versions;matches integer=0;
BEGIN
 FOR candidate IN SELECT (at AT TIME ZONE 'UTC')::date+k FROM generate_series(-1,1)k LOOP
  SELECT * INTO v FROM market_data.close_session_versions s
   WHERE(s.catalog_id,s.code,s.version)=(catalog,session,security.close_session_version_on(catalog,session,candidate));
  CONTINUE WHEN NOT FOUND;
  BEGIN
   IF security.close_session_instant(catalog,session,v.version,candidate)=at THEN matches=matches+1;session_version=v.version;business_date=candidate;END IF;
  EXCEPTION WHEN SQLSTATE 'P0001' THEN NULL;
  END;
 END LOOP;
 IF matches<>1 THEN RAISE EXCEPTION 'close_session_instant' USING ERRCODE='P0001';END IF;
END;$$;

CREATE OR REPLACE FUNCTION automation.publish_pilot_close(marks jsonb,carried jsonb,members jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r automation.runs;j automation.jobs;p automation.pilot_close_profiles;q security.request_contexts;
 previous market_data.market_closes;ordinal integer;identity uuid;mark_row record;observation uuid;payload jsonb;
 anchor market_data.market_closes;day record;
BEGIN
 r=automation.lock_pilot_run();
 IF NOT EXISTS(SELECT 1 FROM automation.jobs job WHERE job.id=r.job_id AND job.operation='generated_pilot_close_v1' AND NOT automation.unsafe_pilot_ppa(job)) THEN RAISE EXCEPTION 'job_operation' USING ERRCODE='42501';END IF;PERFORM pg_advisory_xact_lock(730849102);
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
 -- D334: a generated close belongs to its profile's session, on the business day whose cutoff is the due instant. It never
 -- replaces a recorded close, never follows a replaced anchor and needs the service's grant for the session.
 SELECT * INTO anchor FROM market_data.market_closes WHERE(catalog_id,id)=(p.catalog_id,p.prior_close_id);
 IF security.close_in_force(anchor.catalog_id,anchor.session_code,anchor.business_date)<>anchor.id THEN RAISE EXCEPTION 'close_replaced' USING ERRCODE='P0001';END IF;
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
 -- The generated close's session day (D334), once its inputs are qualified.
 SELECT * INTO day FROM security.close_session_day(p.catalog_id,p.session_code,r.due_at);
 IF NOT EXISTS(SELECT 1 FROM market_data.close_session_versions v WHERE(v.catalog_id,v.code,v.version,v.definition_revision_id)=(p.catalog_id,p.session_code,day.session_version,p.definition_revision_id))
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 PERFORM security.require_session_curves(p.catalog_id,p.session_code,day.session_version,members);
 IF EXISTS(SELECT 1 FROM market_data.market_closes s WHERE(s.catalog_id,s.session_code,s.business_date)=(p.catalog_id,p.session_code,day.business_date))
 THEN RAISE EXCEPTION 'close_exists' USING ERRCODE='P0001';END IF;
 -- This is the sole transition from a preflight read view to publication. The
 -- run and global gate remain locked throughout the transaction. No generic
 -- context reset operation is exposed. The close retains the preflight view.
 UPDATE security.request_contexts SET market_catalog_id=NULL,market_context_id=NULL,trading_as_of=NULL,pilot_publication=true WHERE id=q.id;
 PERFORM automation.lock_pilot_run();
 -- Recording authority is a current write: it is checked once the preflight view is released.
 IF NOT security.can_record_close(p.catalog_id,p.session_code) THEN RAISE EXCEPTION 'close_grant' USING ERRCODE='42501';END IF;
 identity=r.id;SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.market_closes WHERE catalog_id=p.catalog_id;
 INSERT INTO market_data.market_closes VALUES(p.catalog_id,identity,p.definition_revision_id,p.reference_catalog_id,p.reference_revision_id,
 'Generated pilot close',r.due_at,'generated_pilot',q.actor_id,r.id,payload,
 p.prior_close_id,r.id,p.generator,q.market_context_id,ordinal,pg_current_xact_id(),p.session_code,day.session_version,day.business_date);
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

-- Each person's chosen close session, remembered so a page opens on that session's newest close (D334). Nothing chooses a
-- session for a person without a choice; the header asks. Changing the choice replaces it.
CREATE TABLE presentation.close_session_choices(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,principal_id uuid NOT NULL,catalog_id uuid NOT NULL,session_code ref_data.code NOT NULL,
 chosen_at ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,principal_id,catalog_id),
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 FOREIGN KEY(catalog_id,session_code) REFERENCES market_data.close_sessions(catalog_id,code)
);
ALTER TABLE presentation.close_session_choices OWNER TO openetrm_security_owner;
ALTER TABLE presentation.close_session_choices ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentation.close_session_choices FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON presentation.close_session_choices TO openetrm_security_owner USING(true) WITH CHECK(true);
-- The request actor's choice for the catalog, or null when they have not chosen one.
CREATE FUNCTION security.close_session_choice(catalog uuid) RETURNS text
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT x.session_code FROM security.request_contexts c JOIN presentation.close_session_choices x
  ON(x.tenant_id,x.workspace_id,x.principal_id,x.catalog_id)=(c.tenant_id,c.workspace_id,c.actor_id,catalog)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND security.market_access(catalog,'ReadMarketData');
$$;
-- Records the actor's choice of a published session they can read. Choosing the session already chosen changes nothing.
CREATE FUNCTION security.choose_close_session(catalog uuid,session text) RETURNS text
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.delegation_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM market_data.close_session_versions v WHERE(v.catalog_id,v.code,v.state)=(catalog,session,'published'))
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 INSERT INTO presentation.close_session_choices VALUES(c.tenant_id,c.workspace_id,c.actor_id,catalog,session,clock_timestamp())
 ON CONFLICT(tenant_id,workspace_id,principal_id,catalog_id) DO UPDATE SET session_code=excluded.session_code,chosen_at=excluded.chosen_at
  WHERE close_session_choices.session_code IS DISTINCT FROM excluded.session_code;
 RETURN session;
END;$$;

-- The business days of a session whose close in force the reader cannot see (D334): the calendar names them, never offering
-- the close it replaced in their place.
CREATE FUNCTION security.close_session_hidden_days(catalog uuid,session text) RETURNS date[]
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN coalesce((SELECT array_agg(d.business_date ORDER BY d.business_date) FROM(
  SELECT DISTINCT s.business_date FROM market_data.market_closes s WHERE(s.catalog_id,s.session_code)=(catalog,session))d
  WHERE NOT security.market_close_visible(catalog,security.close_in_force(catalog,session,d.business_date))),ARRAY[]::date[]);
END;$$;

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('close_session_instant','close_in_force','can_record_close','close_session_json','close_session_catalog','save_close_session','publish_close_session',
 'close_grant_json','require_close_administration','grant_close_session','revoke_close_session_grant','close_session_grants','register_market_close','close_history','market_close_visible','close_recorded_after_cutoff','close_session_day','require_session_curves','close_session_choice','choose_close_session','use_close_session','close_session_hidden_days','close_session_version_on') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname NOT IN('close_session_instant','close_session_json','close_grant_json','require_close_administration','close_session_day','require_session_curves') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
