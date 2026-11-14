-- Current consumer: economic portfolio projection to the next source-known
-- configured close. Existing session versions receive a new, real migration
-- publication; their original publication time is never invented or backdated.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE market_data.lifecycle_close_session_publications(
 catalog_id uuid NOT NULL,session_code ref_data.code NOT NULL,session_version integer NOT NULL,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,session_code,session_version),
 FOREIGN KEY(catalog_id,session_code,session_version) REFERENCES market_data.close_session_versions(catalog_id,code,version),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE market_data.lifecycle_close_session_publications OWNER TO openetrm_security_owner;
ALTER TABLE market_data.lifecycle_close_session_publications ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.lifecycle_close_session_publications FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON market_data.lifecycle_close_session_publications TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.lifecycle_close_session_publications
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION market_data.guard_lifecycle_close_session_link() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order)=(NEW.catalog_id,NEW.publication_order)
  AND p.publication_xid=pg_current_xact_id()) OR NOT EXISTS(SELECT 1 FROM market_data.close_session_versions s
  WHERE(s.catalog_id,s.code,s.version,s.state)=(NEW.catalog_id,NEW.session_code,NEW.session_version,'published'))
 THEN RAISE EXCEPTION 'projection_horizon_evidence';END IF;
 RETURN NEW;
END$function$;
ALTER FUNCTION market_data.guard_lifecycle_close_session_link() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_lifecycle_close_session_link() FROM PUBLIC;
CREATE TRIGGER publication_transaction BEFORE INSERT ON market_data.lifecycle_close_session_publications
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_lifecycle_close_session_link();

DO $link$DECLARE item record;publication integer;BEGIN
 FOR item IN SELECT DISTINCT catalog_id FROM market_data.close_session_versions WHERE state='published' LOOP
  SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=item.catalog_id;
  INSERT INTO trading.publications VALUES(item.catalog_id,publication,pg_current_xact_id());
  INSERT INTO market_data.lifecycle_close_session_publications
   SELECT catalog_id,code,version,publication FROM market_data.close_session_versions WHERE catalog_id=item.catalog_id AND state='published';
 END LOOP;
END$link$;

CREATE FUNCTION market_data.link_lifecycle_close_session_publication() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE publication integer;BEGIN
 IF NEW.state<>'published' OR TG_OP='UPDATE' AND OLD.state='published' THEN RETURN NEW;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=NEW.catalog_id;
 INSERT INTO trading.publications VALUES(NEW.catalog_id,publication,pg_current_xact_id());
 INSERT INTO market_data.lifecycle_close_session_publications VALUES(NEW.catalog_id,NEW.code,NEW.version,publication);
 RETURN NEW;
END$function$;
ALTER FUNCTION market_data.link_lifecycle_close_session_publication() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.link_lifecycle_close_session_publication() FROM PUBLIC;
CREATE TRIGGER lifecycle_publication AFTER INSERT OR UPDATE OF state ON market_data.close_session_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.link_lifecycle_close_session_publication();

CREATE FUNCTION security.read_lifecycle_projection_horizon(catalog uuid,session text,opening_version integer,context uuid,
 as_of timestamptz,business_date date) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE cut market_data.temporal_contexts;q security.request_contexts;prefix integer;until_at timestamptz;
 v market_data.close_session_versions;opening market_data.close_session_versions;cal ref_data.calendars;clock ref_data.clock_rules;
 day date;last_day date;is_open boolean;at timestamptz;offset_seconds integer;publication integer;known timestamptz;versions jsonb;result jsonb;
 used_versions integer[]='{}';calendars jsonb='[]';
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 until_at=security.admit_trading_business_source(catalog,context,as_of,business_date);
 SELECT * INTO STRICT q FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
  AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF q.market_context_id IS NULL THEN PERFORM security.use_market_context(catalog,context);
 ELSIF(q.market_catalog_id,q.market_context_id) IS DISTINCT FROM(catalog,context)
 THEN RAISE EXCEPTION 'projection_horizon_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT cut FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 SELECT x.known_through INTO STRICT prefix FROM trading.contexts x WHERE(x.catalog_id,x.context_id)=(catalog,context);
 SELECT s.* INTO opening FROM market_data.close_session_versions s
 JOIN market_data.lifecycle_close_session_publications p ON(p.catalog_id,p.session_code,p.session_version)=(s.catalog_id,s.code,s.version)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(s.catalog_id,s.code,s.version,s.state)=(catalog,session,opening_version,'published')
  AND p.publication_order<=prefix AND k.known_from<=cut.known_at;
 IF NOT FOUND THEN RAISE EXCEPTION 'projection_horizon_evidence';END IF;
 SELECT s.* INTO v FROM market_data.close_session_versions s
 JOIN market_data.lifecycle_close_session_publications p ON(p.catalog_id,p.session_code,p.session_version)=(s.catalog_id,s.code,s.version)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(s.catalog_id,s.code,s.state)=(catalog,session,'published') AND s.effective_from<=business_date AND business_date<s.effective_to
  AND p.publication_order<=prefix AND k.known_from<=cut.known_at ORDER BY s.effective_from DESC LIMIT 1;
 IF NOT FOUND OR v.version<>opening_version THEN RAISE EXCEPTION 'projection_horizon_version';END IF;
 SELECT max(s.effective_to),jsonb_agg(jsonb_build_object('catalogId',s.catalog_id,'code',s.code,'version',s.version,
  'definitionRevisionId',s.definition_revision_id,'referenceCatalogId',s.reference_catalog_id,'referenceRevisionId',s.reference_revision_id,
  'calendarCode',s.calendar_code,'cutoff',s.cutoff_local,'effectiveFrom',s.effective_from,'effectiveTo',s.effective_to,
  'publicationOrder',p.publication_order,'knownFrom',k.known_from)
  ORDER BY s.effective_from,s.version) INTO last_day,versions
 FROM market_data.close_session_versions s
 JOIN market_data.lifecycle_close_session_publications p ON(p.catalog_id,p.session_code,p.session_version)=(s.catalog_id,s.code,s.version)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(s.catalog_id,s.code,s.state)=(catalog,session,'published') AND p.publication_order<=prefix AND k.known_from<=cut.known_at;
 day=business_date;
 WHILE day<last_day LOOP
  SELECT s.* INTO v
  FROM market_data.close_session_versions s
  JOIN market_data.lifecycle_close_session_publications p ON(p.catalog_id,p.session_code,p.session_version)=(s.catalog_id,s.code,s.version)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE(s.catalog_id,s.code,s.state)=(catalog,session,'published') AND s.effective_from<=day AND day<s.effective_to
   AND p.publication_order<=prefix AND k.known_from<=cut.known_at ORDER BY s.effective_from DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'projection_horizon_coverage';END IF;
  SELECT p.publication_order,k.known_from INTO STRICT publication,known FROM market_data.lifecycle_close_session_publications p
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.session_code,p.session_version)=(v.catalog_id,v.code,v.version);
  IF security.reference_access(v.reference_catalog_id,'ReadMarketData') IS NOT TRUE
   OR security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id) IS NOT TRUE
   OR security.market_fact_visible(v.catalog_id,v.definition_revision_id,NULL) IS NOT TRUE
  THEN RAISE EXCEPTION 'projection_horizon_denied' USING ERRCODE='42501';END IF;
  SELECT * INTO STRICT cal FROM ref_data.calendars c WHERE(c.catalog_id,c.revision_id,c.code)=(v.reference_catalog_id,v.reference_revision_id,v.calendar_code);
  IF NOT v.version=ANY(used_versions) THEN
   SELECT * INTO STRICT clock FROM ref_data.clock_rules r WHERE(r.catalog_id,r.revision_id,r.code)=(cal.catalog_id,cal.revision_id,cal.clock_code);
   calendars=calendars||jsonb_build_array(jsonb_build_object('sessionVersion',v.version,'calendarTerms',to_jsonb(cal),'clockTerms',to_jsonb(clock),
    'days',coalesce((SELECT jsonb_agg(jsonb_build_object('date',d.business_date,'open',d.is_business_day) ORDER BY d.business_date)
     FROM ref_data.calendar_days d WHERE(d.catalog_id,d.revision_id,d.calendar_code)=(cal.catalog_id,cal.revision_id,cal.code)),'[]'::jsonb)));
   used_versions=array_append(used_versions,v.version);
  END IF;
  IF day<cal.coverage_from OR day>=cal.coverage_to THEN RAISE EXCEPTION 'projection_horizon_coverage';END IF;
  SELECT d.is_business_day INTO is_open FROM ref_data.calendar_days d
   WHERE(d.catalog_id,d.revision_id,d.calendar_code,d.business_date)=(cal.catalog_id,cal.revision_id,cal.code,day);
  is_open=coalesce(is_open,(cal.weekend_mask&(1<<(extract(isodow FROM day)::integer-1)))=0);
  IF is_open THEN
   at=security.close_session_instant(catalog,session,v.version,day);
   IF at>as_of THEN
    SELECT * INTO STRICT clock FROM ref_data.clock_rules r WHERE(r.catalog_id,r.revision_id,r.code)=(cal.catalog_id,cal.revision_id,cal.clock_code);
    offset_seconds=CASE WHEN clock.kind='fixed_offset' THEN clock.fixed_offset_seconds
     ELSE extract(epoch FROM((at AT TIME ZONE clock.iana_zone)-(at AT TIME ZONE 'UTC')))::integer END;
    result=jsonb_build_object('implementation','source_known_effective_session_schedule_v1','contextId',context,'knownAt',cut.known_at,
     'openingAt',as_of,'businessDate',business_date,'openingSession',jsonb_build_object('catalogId',catalog,'code',session,'version',opening_version),
     'session',jsonb_build_object('catalogId',catalog,'code',session,'version',v.version),'publicationOrder',publication,'knownFrom',known,
     'calendar',jsonb_build_object('catalogId',cal.catalog_id,'revisionId',cal.revision_id,'code',cal.code),
     'calendarTerms',to_jsonb(cal),'clockTerms',to_jsonb(clock),'calendarEvidence',calendars,
     'versions',(SELECT jsonb_agg(e.value ORDER BY(e.value->>'effectiveFrom')::date,(e.value->>'version')::integer)
       FROM jsonb_array_elements(versions)e(value) WHERE(e.value->>'version')::integer=ANY(used_versions)),
     'authorityUntil',until_at,
     'closes',jsonb_build_array(jsonb_build_object('date',day,'instant',at,'offsetSeconds',offset_seconds)));
    PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,until_at);RETURN result;
   END IF;
  END IF;
  day=day+1;
 END LOOP;
 RAISE EXCEPTION 'projection_horizon_coverage';
END$function$;
ALTER FUNCTION security.read_lifecycle_projection_horizon(uuid,text,integer,uuid,timestamptz,date) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_lifecycle_projection_horizon(uuid,text,integer,uuid,timestamptz,date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_lifecycle_projection_horizon(uuid,text,integer,uuid,timestamptz,date) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
