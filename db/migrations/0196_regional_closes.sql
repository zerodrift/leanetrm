-- D319: a close belongs to an exact versioned close session and an explicit business day. For each session and business
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
 actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,code,version),UNIQUE(catalog_id,code,version,definition_revision_id),UNIQUE(catalog_id,actor_id,request_id),
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
-- exists twice on that day's local clock is refused (close_session_instant). Nothing moves the day or the time.
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
 IF clock.kind='fixed_offset' THEN RETURN(local-make_interval(secs=>clock.fixed_offset_seconds)) AT TIME ZONE 'UTC';END IF;
 -- The local time's instants under the offsets in force a day either side; exactly one must map back to it.
 base=local AT TIME ZONE 'UTC';
 SELECT array_agg(DISTINCT c) INTO candidates FROM(
  SELECT(local-((t AT TIME ZONE clock.iana_zone)-(t AT TIME ZONE 'UTC'))) AT TIME ZONE 'UTC' c
  FROM unnest(ARRAY[base-interval '1 day',base+interval '1 day'])t)x
 WHERE(c AT TIME ZONE clock.iana_zone)=local;
 IF coalesce(cardinality(candidates),0)<>1 THEN RAISE EXCEPTION 'close_session_instant' USING ERRCODE='P0001';END IF;
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
CREATE FUNCTION security.can_record_close(catalog uuid,session text) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'PublishMarketData') AND EXISTS(SELECT 1 FROM security.request_contexts c
  JOIN security.close_session_grants g ON(g.principal_id,g.tenant_id,g.workspace_id)=(c.actor_id,c.tenant_id,c.workspace_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.expires_at>clock_timestamp() AND(g.catalog_id,g.session_code)=(catalog,session) AND NOT g.revoked
  AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to);
$$;

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('close_session_instant','close_in_force','can_record_close') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname<>'close_session_instant' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
