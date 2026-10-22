-- Shared, explicitly verified meter evidence only. No cash lifecycle or role grants.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
CREATE SCHEMA settlement_data;
GRANT USAGE ON SCHEMA settlement_data TO openetrm_runtime,openetrm_security_owner;
INSERT INTO security.permissions VALUES('VerifySettlementMeters');

-- Relationships retain exact source, reading, unit and interval pins. Chain identity
-- excludes definition/reading revision, so a cloned definition cannot reset withdrawal.
ALTER TABLE market_data.volume_sources ADD UNIQUE(catalog_id,revision_id,code,asset_code,metered_series_id,metered_stage);
CREATE TABLE settlement_data.meter_finality_heads (
 catalog_id uuid NOT NULL,asset_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 stage text NOT NULL CHECK(stage IN('generated','delivered')),starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,
 id uuid NOT NULL,sequence integer NOT NULL CHECK(sequence>0),previous_id uuid,
 definition_revision_id uuid NOT NULL,series_id uuid NOT NULL,reading_revision integer NOT NULL,
 state text NOT NULL CHECK(state IN('final','suspended','withdrawn')),effective_at ref_data.instant NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,clock_code ref_data.code NOT NULL,operating_date date NOT NULL,
 authority_checked_at ref_data.instant NOT NULL,authority_epoch bigint NOT NULL,
 represented_principal_id uuid NOT NULL REFERENCES security.principals(id),
 authority_permission text NOT NULL CHECK(authority_permission='VerifySettlementMeters'),
 actor_id uuid NOT NULL,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,delegation_id uuid REFERENCES security.agent_delegations(id),
 request_id uuid NOT NULL,reason text NOT NULL CHECK(length(reason) BETWEEN 1 AND 500 AND reason !~ '^[[:space:]]*$'),
 publication_order integer NOT NULL CHECK(publication_order>0),publication_xid xid8 NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(id),UNIQUE(catalog_id,publication_order),UNIQUE(catalog_id,publication_order,publication_xid),
 UNIQUE(catalog_id,asset_code,source_code,stage,starts_at,ends_at,sequence),
 UNIQUE(catalog_id,asset_code,source_code,stage,starts_at,ends_at,id),
 FOREIGN KEY(catalog_id,asset_code,source_code,stage,starts_at,ends_at,previous_id)
  REFERENCES settlement_data.meter_finality_heads(catalog_id,asset_code,source_code,stage,starts_at,ends_at,id),
 FOREIGN KEY(catalog_id,definition_revision_id,source_code,asset_code,series_id,stage)
  REFERENCES market_data.volume_sources(catalog_id,revision_id,code,asset_code,metered_series_id,metered_stage),
 FOREIGN KEY(catalog_id,series_id,reading_revision,starts_at)
  REFERENCES market_data.interval_series_values(catalog_id,series_id,revision,starts_at),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code,operating_date)
  REFERENCES power_data.operating_days(reference_catalog_id,reference_revision_id,clock_code,operating_date),
 FOREIGN KEY(tenant_id,workspace_id,actor_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id),
 CHECK(starts_at<ends_at AND effective_at>=ends_at),CHECK((sequence=1)=(previous_id IS NULL))
);
CREATE TABLE settlement_data.meter_finality_requests (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 payload jsonb NOT NULL,head_id uuid NOT NULL,response jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),FOREIGN KEY(catalog_id,head_id) REFERENCES settlement_data.meter_finality_heads(catalog_id,id)
);
CREATE TABLE settlement_data.meter_finality_receipts (
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES settlement_data.meter_finality_heads(catalog_id,publication_order,publication_xid)
);
COMMENT ON TABLE settlement_data.meter_finality_heads IS
 'verified_meter_head_v1: immutable published assertion for one whole-asset source-stage native cell. Not a cash determination. Current authority is checked separately from retained verifier evidence.';
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['meter_finality_heads','meter_finality_requests','meter_finality_receipts'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('ALTER TABLE settlement_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE settlement_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON settlement_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON settlement_data.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;

-- Insert below the existing close-risk admission shortcut. Its outer function,
-- original identities, retained cutoff reuse and worker fences stay untouched.
ALTER FUNCTION security.capture_pre_close_risk_context(uuid,date,timestamptz) RENAME TO capture_pre_meter_finality_context;
CREATE FUNCTION security.capture_pre_close_risk_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM settlement_data.meter_finality_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT h.* FROM settlement_data.meter_finality_heads h LEFT JOIN settlement_data.meter_finality_receipts k USING(catalog_id,publication_order)
  WHERE h.catalog_id=catalog AND k.publication_order IS NULL ORDER BY h.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified OR commit_at<row.effective_at THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO settlement_data.meter_finality_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);
  last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_meter_finality_context(catalog,effective_date,requested_at);
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 RETURN token;
END$$;
ALTER FUNCTION security.capture_pre_close_risk_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_pre_close_risk_context(uuid,date,timestamptz) FROM PUBLIC,openetrm_runtime;
REVOKE ALL ON FUNCTION security.capture_pre_meter_finality_context(uuid,date,timestamptz) FROM PUBLIC,openetrm_runtime;

-- Current scoped access is independent of historical publication eligibility.
-- Exact old definitions remain usable for the original delivery cell after their
-- effective period ends. No current-definition substitution is performed.
CREATE FUNCTION settlement_data.meter_source(catalog uuid,definition uuid,source text,stage text,first_at timestamptz,last_at timestamptz,cutoff timestamptz)
 RETURNS market_data.volume_sources LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE s market_data.volume_sources;day power_data.operating_days;
BEGIN
 SELECT * INTO s FROM market_data.volume_sources v WHERE(v.catalog_id,v.revision_id,v.code)=(catalog,definition,source);
 IF s.code IS NULL OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR security.reference_access(s.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 IF first_at IS NULL OR last_at IS NULL OR NOT isfinite(first_at) OR NOT isfinite(last_at)
  OR s.metered_stage IS DISTINCT FROM stage OR s.interval_minutes NOT IN(15,60)
  OR last_at-first_at<>make_interval(mins=>s.interval_minutes)
 THEN RAISE EXCEPTION 'meter_finality_reading';END IF;
 IF(SELECT count(*) FROM power_data.operating_days d WHERE(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=
  (s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.starts_at<=first_at AND last_at<=d.ends_at)<>1
 THEN RAISE EXCEPTION 'meter_finality_calendar';END IF;
 SELECT * INTO day FROM power_data.operating_days d WHERE(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=
  (s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.starts_at<=first_at AND last_at<=d.ends_at;
 IF day.operating_date IS NULL OR mod(extract(epoch FROM first_at-day.starts_at),60*s.interval_minutes)<>0
 THEN RAISE EXCEPTION 'meter_finality_calendar';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(r.catalog_id,r.id)
    JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id)=(r.catalog_id,r.id) AND e.observation_id IS NULL
    JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    WHERE(r.catalog_id,r.id,r.state)=(catalog,definition,'sealed') AND k.known_from<=cutoff
     AND p.effective_from<=day.operating_date AND day.operating_date<p.effective_to)
 THEN RAISE EXCEPTION 'meter_finality_definition';END IF;
 RETURN s;
END$$;

CREATE FUNCTION settlement_data.publish_meter_finality(catalog uuid,definition uuid,source text,stage text,first_at timestamptz,last_at timestamptz,
 reading_revision integer,state text,effective_at timestamptz,predecessor uuid,request uuid,reason text) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE s market_data.volume_sources;c security.request_contexts;old settlement_data.meter_finality_requests;
 head settlement_data.meter_finality_heads;payload jsonb;result jsonb;identity uuid=gen_random_uuid();ordinal integer;
 context uuid;cutoff timestamptz;day power_data.operating_days;reading market_data.interval_series_values;raw_known timestamptz;
BEGIN
 IF NOT security.current_write_context() OR security.market_access(catalog,'VerifySettlementMeters') IS NOT TRUE
  OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 payload=jsonb_build_object('definition',definition,'source',source,'stage',stage,'start',first_at,'end',last_at,
  'readingRevision',reading_revision,'state',state,'effectiveAt',effective_at,'predecessor',predecessor,'reason',reason);
 PERFORM pg_advisory_xact_lock(730849102);
 -- Recheck after gate waits, before replay as well as before a new publication.
 IF security.market_access(catalog,'VerifySettlementMeters') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO s FROM market_data.volume_sources v WHERE(v.catalog_id,v.revision_id,v.code)=(catalog,definition,source);
 IF s.code IS NULL OR security.reference_access(s.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO old FROM settlement_data.meter_finality_requests r WHERE(r.catalog_id,r.actor_id,r.request_id)=(catalog,c.actor_id,request);
 IF FOUND THEN
  IF old.payload IS DISTINCT FROM payload THEN RAISE EXCEPTION 'meter_finality_command_conflict';END IF;
  RETURN old.response;
 END IF;
 IF request IS NULL OR reading_revision IS NULL OR reading_revision<1 OR state IS NULL OR state NOT IN('final','suspended','withdrawn')
  OR effective_at IS NULL OR NOT isfinite(effective_at) OR effective_at<last_at OR effective_at>clock_timestamp()
  OR reason IS NULL OR length(reason) NOT BETWEEN 1 AND 500 OR reason ~ '^[[:space:]]*$'
 THEN RAISE EXCEPTION 'meter_finality_input';END IF;
 context=security.capture_date_context(catalog,(effective_at AT TIME ZONE 'UTC')::date,NULL);
 SELECT known_at INTO cutoff FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context);
 s=settlement_data.meter_source(catalog,definition,source,stage,first_at,last_at,cutoff);
 SELECT * INTO reading FROM market_data.interval_series_values v WHERE(v.catalog_id,v.series_id,v.revision,v.starts_at,v.ends_at)=
  (catalog,s.metered_series_id,reading_revision,first_at,last_at);
 SELECT k.known_from INTO raw_known FROM market_data.interval_series_revisions r JOIN market_data.interval_series_knowledge_receipts k
  USING(catalog_id,publication_order,publication_xid) WHERE(r.catalog_id,r.series_id,r.revision)=(catalog,s.metered_series_id,reading_revision);
 IF reading.value IS NULL OR reading.value<0 OR raw_known IS NULL OR raw_known>cutoff THEN RAISE EXCEPTION 'meter_finality_reading';END IF;
 SELECT * INTO head FROM settlement_data.meter_finality_heads h WHERE(h.catalog_id,h.asset_code,h.source_code,h.stage,h.starts_at,h.ends_at)=
  (catalog,s.asset_code,source,publish_meter_finality.stage,first_at,last_at) ORDER BY h.sequence DESC LIMIT 1;
 IF predecessor IS DISTINCT FROM head.id THEN RAISE EXCEPTION 'meter_finality_predecessor';END IF;
 IF head.effective_at>effective_at THEN RAISE EXCEPTION 'meter_finality_effective_order';END IF;
 SELECT coalesce(max(h.publication_order),0)+1 INTO ordinal FROM settlement_data.meter_finality_heads h WHERE h.catalog_id=catalog;
 IF(SELECT count(*) FROM power_data.operating_days d WHERE(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=
  (s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.starts_at<=first_at AND last_at<=d.ends_at)<>1
 THEN RAISE EXCEPTION 'meter_finality_calendar';END IF;
 SELECT * INTO day FROM power_data.operating_days d WHERE(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=
  (s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.starts_at<=first_at AND last_at<=d.ends_at;
 INSERT INTO settlement_data.meter_finality_heads VALUES(catalog,s.asset_code,source,stage,first_at,last_at,identity,coalesce(head.sequence,0)+1,head.id,
  definition,s.metered_series_id,reading_revision,state,effective_at,
  s.reference_catalog_id,s.reference_revision_id,s.clock_code,day.operating_date,clock_timestamp(),
  (SELECT generation FROM security.epoch WHERE id),coalesce((SELECT delegator_id FROM security.agent_delegations WHERE id=c.delegation_id),c.actor_id),
  'VerifySettlementMeters',c.actor_id,c.tenant_id,c.workspace_id,c.delegation_id,request,reason,ordinal,pg_current_xact_id());
 result=jsonb_build_object('headId',identity,'sequence',coalesce(head.sequence,0)+1,'previousId',head.id,'readingRevision',reading_revision,
  'seriesId',s.metered_series_id,'state',state,'effectiveAt',effective_at,'requestId',request);
 INSERT INTO settlement_data.meter_finality_requests VALUES(catalog,c.actor_id,request,payload,identity,result);
 IF security.market_access(catalog,'VerifySettlementMeters') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR security.reference_access(s.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;

CREATE FUNCTION settlement_data.read_meter_finality(catalog uuid,definition uuid,source text,stage text,first_at timestamptz,last_at timestamptz,context uuid,as_of timestamptz)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE s market_data.volume_sources;head settlement_data.meter_finality_heads;cutoff timestamptz;known timestamptz;raw_known timestamptz;value text;
BEGIN
 SELECT t.known_at INTO cutoff FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
  AND q.expires_at>clock_timestamp() AND(q.market_catalog_id,q.market_context_id)=(catalog,context) AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date;
 IF cutoff IS NULL OR as_of IS NULL OR NOT isfinite(as_of) THEN RAISE EXCEPTION 'meter_finality_context' USING ERRCODE='42501';END IF;
 s=settlement_data.meter_source(catalog,definition,source,stage,first_at,last_at,cutoff);
 -- Select head first, then inspect state and exact pins. Never filter to an older
 -- final or requested definition revision before selecting the authoritative head.
 SELECT h.* INTO head FROM settlement_data.meter_finality_heads h JOIN settlement_data.meter_finality_receipts k USING(catalog_id,publication_order,publication_xid)
 WHERE(h.catalog_id,h.asset_code,h.source_code,h.stage,h.starts_at,h.ends_at)=(catalog,s.asset_code,source,read_meter_finality.stage,first_at,last_at)
  AND h.effective_at<=as_of AND k.known_from<=cutoff ORDER BY h.sequence DESC LIMIT 1;
 IF head.id IS NULL THEN RAISE EXCEPTION 'meter_finality_missing';END IF;
 IF head.definition_revision_id<>definition OR head.series_id<>s.metered_series_id THEN RAISE EXCEPTION 'meter_finality_pins';END IF;
 SELECT known_from INTO known FROM settlement_data.meter_finality_receipts WHERE(catalog_id,publication_order)=(catalog,head.publication_order);
 SELECT k.known_from,v.value::text INTO raw_known,value FROM market_data.interval_series_revisions r
 JOIN market_data.interval_series_knowledge_receipts k USING(catalog_id,publication_order,publication_xid)
 JOIN market_data.interval_series_values v USING(catalog_id,series_id,revision)
 WHERE(r.catalog_id,r.series_id,r.revision,v.starts_at,v.ends_at)=(catalog,head.series_id,head.reading_revision,first_at,last_at);
 IF raw_known IS NULL OR raw_known>known OR raw_known>cutoff OR value IS NULL THEN RAISE EXCEPTION 'meter_finality_reading';END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.reference_access(s.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('headId',head.id,'sequence',head.sequence,'previousId',head.previous_id,'state',head.state,
  'definitionRevisionId',definition,'asset',s.asset_code,'source',source,'stage',stage,'seriesId',head.series_id,'readingRevision',head.reading_revision,
  'referenceCatalogId',s.reference_catalog_id,'referenceRevisionId',s.reference_revision_id,
  'quantityCode',(SELECT quantity_code FROM market_data.interval_series WHERE(catalog_id,id)=(catalog,head.series_id)),
  'start',first_at,'end',last_at,'effectiveAt',head.effective_at,'knownFrom',known,'readingKnownFrom',raw_known,
  'verifier',jsonb_build_object('actorId',head.actor_id,'representedPrincipalId',head.represented_principal_id,
   'delegationId',head.delegation_id,'tenantId',head.tenant_id,'workspaceId',head.workspace_id,
   'permission',head.authority_permission,'checkedAt',head.authority_checked_at,'epoch',head.authority_epoch),
  'operatingDate',head.operating_date,'clock',head.clock_code,
  'clockFingerprint',(SELECT clock_fingerprint FROM power_data.operating_days WHERE(reference_catalog_id,reference_revision_id,clock_code,operating_date)=
   (head.reference_catalog_id,head.reference_revision_id,head.clock_code,head.operating_date)),
  'contextId',context,'asOf',as_of,'knownAt',cutoff,'value',CASE WHEN head.state='final' THEN value END);
END$$;
-- Reuse the raw primary key. Adding a covering endpoint index to the shared raw
-- table would change legacy overlap-probe plans. The raw row and head are both
-- immutable, so an insertion check proves endpoint equality permanently.
CREATE FUNCTION settlement_data.check_meter_reading_endpoint() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE raw_end timestamptz;
BEGIN
 SELECT v.ends_at INTO raw_end FROM market_data.interval_series_values v
 WHERE(v.catalog_id,v.series_id,v.revision,v.starts_at)=(NEW.catalog_id,NEW.series_id,NEW.reading_revision,NEW.starts_at);
 IF raw_end IS DISTINCT FROM NEW.ends_at THEN RAISE EXCEPTION 'meter_finality_reading' USING ERRCODE='23514';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER meter_reading_endpoint BEFORE INSERT ON settlement_data.meter_finality_heads
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_meter_reading_endpoint();

-- Deferred defense for ordinary operation transactions. Current authority is also
-- checked after all publication waits; epoch SHARE serializes revocation through
-- commit. PostgreSQL callers can force deferred constraints early, so this is not
-- a claim of an arbitrary-SQL physical-COMMIT-time authorization hook.
CREATE FUNCTION settlement_data.check_meter_publication_authority() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.market_access(NEW.catalog_id,'VerifySettlementMeters') IS NOT TRUE
  OR security.market_access(NEW.catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_access(NEW.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
   AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
   AND(q.actor_id,q.tenant_id,q.workspace_id)=(NEW.actor_id,NEW.tenant_id,NEW.workspace_id)
   AND q.delegation_id IS NOT DISTINCT FROM NEW.delegation_id)
 THEN RAISE EXCEPTION 'meter_finality_denied' USING ERRCODE='42501';END IF;
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER meter_publication_authority AFTER INSERT ON settlement_data.meter_finality_heads
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION settlement_data.check_meter_publication_authority();

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='settlement_data' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('publish_meter_finality','read_meter_finality') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
