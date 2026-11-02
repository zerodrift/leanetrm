-- New verified derivations, never retroactive provenance for retained contracts.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT pg_advisory_xact_lock(730849102);

-- A mapping can use one exact schedule in hundreds of fields. Visibility of
-- that source is identical for each field; still recheck each distinct source
-- under the current request, without caching authority between rows or reads.
DO $$DECLARE body text;updated text;needle text=$old$SELECT 1 FROM market_data.contract_date_fields f WHERE(f.catalog_id,f.definition_id,f.version)=(catalog,definition,requested_version)
   AND f.schedule_id IS NOT NULL AND NOT security.contract_date_schedule_visible(f.schedule_catalog_id,f.schedule_id,f.schedule_version)$old$;
BEGIN
 body=pg_get_functiondef('security.contract_date_visible(uuid,uuid,integer)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'Unexpected contract-date visibility implementation';END IF;
 updated=replace(body,needle,$new$SELECT 1 FROM(SELECT DISTINCT schedule_catalog_id,schedule_id,schedule_version FROM market_data.contract_date_fields
   WHERE(catalog_id,definition_id,version)=(catalog,definition,requested_version) AND schedule_id IS NOT NULL) f
   WHERE NOT security.contract_date_schedule_visible(f.schedule_catalog_id,f.schedule_id,f.schedule_version)$new$);
 EXECUTE updated;
END$$;

CREATE TABLE market_data.contract_date_admissions (
 catalog_id uuid NOT NULL,id uuid NOT NULL,revision_id uuid NOT NULL,contract_id uuid NOT NULL,
 mapping_id uuid NOT NULL,mapping_version integer NOT NULL,ordinal integer NOT NULL,
 context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_xid xid8 NOT NULL DEFAULT pg_current_xact_id(),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,publication_xid),
 UNIQUE(catalog_id,revision_id,contract_id,mapping_id,mapping_version,ordinal),
 FOREIGN KEY(catalog_id,revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(catalog_id,mapping_id,mapping_version,ordinal) REFERENCES market_data.contract_date_contracts(catalog_id,definition_id,version,ordinal),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
COMMENT ON TABLE market_data.contract_date_admissions IS
 'An exact contract verified against a published date-list mapping now. Does not assert how an earlier contract was originally constructed.';
CREATE TABLE market_data.contract_date_admission_receipts (
 catalog_id uuid NOT NULL,admission_id uuid NOT NULL,publication_xid xid8 NOT NULL,known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,admission_id),
 FOREIGN KEY(catalog_id,admission_id,publication_xid) REFERENCES market_data.contract_date_admissions(catalog_id,id,publication_xid)
);
CREATE TABLE market_data.contract_date_materialization_requests (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),
 request_id uuid NOT NULL,payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9A-F]{64}$'),
 PRIMARY KEY(catalog_id,actor_id,request_id)
);
CREATE TABLE market_data.contract_date_materialization_items (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL,request_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 119),
 admission_id uuid NOT NULL,created boolean NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id,ordinal),
 FOREIGN KEY(catalog_id,actor_id,request_id) REFERENCES market_data.contract_date_materialization_requests(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,admission_id) REFERENCES market_data.contract_date_admissions(catalog_id,id)
);

CREATE FUNCTION security.contract_date_admission_visible(catalog uuid,admission uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.contract_date_admissions a JOIN market_data.revisions r ON(r.catalog_id,r.id)=(a.catalog_id,a.revision_id)
 WHERE(a.catalog_id,a.id)=(catalog,admission) AND security.market_fact_visible(catalog,a.revision_id,NULL)
 AND security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id)
 AND security.contract_date_visible(catalog,a.mapping_id,a.mapping_version)
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN market_data.contract_date_admission_receipts k ON k.catalog_id=t.catalog_id AND k.admission_id=admission AND k.known_from<=t.known_at
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog)));
$$;

-- This initial admission contract is deliberately bounded to absolute, explicit
-- date lists. The database checks stored values, without another generator.
CREATE FUNCTION market_data.guard_contract_date_admission() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE ctx market_data.temporal_contexts;target market_data.contracts;cutoff time;start_date date;end_date date;last_date date;field_count integer;
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Immutable contract-date admission' USING ERRCODE='55000';END IF;
 IF NOT security.current_write_context() OR NOT security.market_access(NEW.catalog_id,'WriteMarketDefinitions')
 OR NEW.actor_id IS DISTINCT FROM security.custom_curve_actor(NEW.catalog_id,'WriteMarketDefinitions')
 OR NEW.publication_xid<>pg_current_xact_id() THEN RAISE EXCEPTION 'No current admission authority' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO STRICT ctx FROM market_data.temporal_contexts WHERE(catalog_id,id)=(NEW.catalog_id,NEW.context_id);
 SELECT * INTO STRICT target FROM market_data.contracts WHERE(catalog_id,revision_id,id)=(NEW.catalog_id,NEW.revision_id,NEW.contract_id);
 IF NOT security.reference_version_visible(target.reference_catalog_id,target.reference_revision_id)
 OR NOT security.contract_date_visible(NEW.catalog_id,NEW.mapping_id,NEW.mapping_version) THEN RAISE EXCEPTION 'No source access' USING ERRCODE='42501';END IF;
 SELECT c.cutoff_utc INTO cutoff FROM market_data.contract_date_contracts c
 JOIN market_data.contract_date_versions v ON(v.catalog_id,v.definition_id,v.version)=(c.catalog_id,c.definition_id,c.version)
 JOIN market_data.contract_date_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
 WHERE(c.catalog_id,c.definition_id,c.version,c.ordinal)=(NEW.catalog_id,NEW.mapping_id,NEW.mapping_version,NEW.ordinal)
 AND v.state='published' AND k.known_from<=ctx.known_at;
 IF NOT FOUND THEN RAISE EXCEPTION 'Mapping is unavailable in the admitted context' USING ERRCODE='23514';END IF;
 SELECT count(*),min(CASE WHEN f.role='start' THEN (s.definition->'dates'->>f.occurrence)::date END),
  min(CASE WHEN f.role='end' THEN (s.definition->'dates'->>f.occurrence)::date END),
  min(CASE WHEN f.role='last_trading' THEN (s.definition->'dates'->>f.occurrence)::date END)
 INTO field_count,start_date,end_date,last_date FROM market_data.contract_date_fields f
 JOIN market_data.date_schedule_versions s ON(s.catalog_id,s.schedule_id,s.version)=(f.schedule_catalog_id,f.schedule_id,f.schedule_version)
 JOIN market_data.knowledge_publications k ON(k.catalog_id,k.publication_order,k.publication_xid)=(s.catalog_id,s.publication_order,s.publication_xid)
 WHERE(f.catalog_id,f.definition_id,f.version,f.ordinal)=(NEW.catalog_id,NEW.mapping_id,NEW.mapping_version,NEW.ordinal)
 AND f.literal_date IS NULL AND f.business_days IS NULL AND s.state='published' AND s.selection='absolute_index'
 AND s.engine_version='date_schedule_v3' AND jsonb_typeof(s.definition->'dates')='array'
 AND s.effective_from<=ctx.effective_on AND ctx.effective_on<s.effective_to AND k.known_from<=ctx.known_at
 AND s.publication_order<=ctx.known_through;
 IF field_count<>3 OR target.delivery_from IS DISTINCT FROM start_date OR target.delivery_to IS DISTINCT FROM end_date
 OR target.expiry_at IS DISTINCT FROM ((last_date+cutoff) AT TIME ZONE 'UTC') THEN
  RAISE EXCEPTION 'Contract terms do not match exact source occurrences' USING ERRCODE='23514';
 END IF;
 IF target.kind='future' AND NOT EXISTS(SELECT 1 FROM ref_data.futures_series s JOIN ref_data.futures_milestones m
  ON(m.catalog_id,m.revision_id,m.series_code)=(s.catalog_id,s.revision_id,s.code)
  WHERE(s.catalog_id,s.revision_id,s.code)=(target.reference_catalog_id,target.reference_revision_id,target.futures_series_code)
  AND s.definition_state<>'placeholder' AND(s.delivery_from,s.delivery_to)=(start_date,end_date)
  AND m.milestone='last_trading' AND m.value_state='known' AND m.event_at=target.expiry_at) THEN
  RAISE EXCEPTION 'Futures dates conflict with exact source terms' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER guard_admission BEFORE INSERT OR UPDATE OR DELETE ON market_data.contract_date_admissions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_contract_date_admission();

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['contract_date_admissions','contract_date_admission_receipts','contract_date_materialization_requests','contract_date_materialization_items'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner',t);
  IF t<>'contract_date_admission_receipts' THEN
   EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',t);
  END IF;
  IF t<>'contract_date_admissions' THEN
   EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  END IF;
 END LOOP;
END$$;
CREATE POLICY scoped_read ON market_data.contract_date_admissions FOR SELECT TO openetrm_runtime
 USING(security.contract_date_admission_visible(catalog_id,id));
CREATE POLICY scoped_insert ON market_data.contract_date_admissions FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND security.market_access(catalog_id,'WriteMarketDefinitions') AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY scoped_read ON market_data.contract_date_admission_receipts FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM market_data.contract_date_admissions a WHERE(a.catalog_id,a.id)=(contract_date_admission_receipts.catalog_id,admission_id)));
CREATE POLICY scoped_read ON market_data.contract_date_materialization_requests FOR SELECT TO openetrm_runtime
 USING(security.current_write_context() AND security.market_access(catalog_id,'ReadMarketData') AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY scoped_insert ON market_data.contract_date_materialization_requests FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY scoped_read ON market_data.contract_date_materialization_items FOR SELECT TO openetrm_runtime
 USING(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions')
 AND EXISTS(SELECT 1 FROM market_data.contract_date_admissions a WHERE(a.catalog_id,a.id)=(contract_date_materialization_items.catalog_id,admission_id)));
CREATE POLICY scoped_insert ON market_data.contract_date_materialization_items FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions')
 AND EXISTS(SELECT 1 FROM market_data.contract_date_admissions a WHERE(a.catalog_id,a.id)=(contract_date_materialization_items.catalog_id,admission_id)));
GRANT INSERT ON market_data.contract_date_admissions,market_data.contract_date_materialization_requests,market_data.contract_date_materialization_items TO openetrm_runtime;
GRANT INSERT ON market_data.contract_date_admission_receipts TO openetrm_security_owner;

-- Certify the admission's own commit. Earlier mapping or contract publication
-- times must never make a later verification visible to an earlier context.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_contract_admission_context;
REVOKE ALL ON FUNCTION security.capture_pre_contract_admission_context(uuid,date,timestamptz) FROM openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;known timestamptz;last_known timestamptz;certified timestamptz;
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN security.capture_pre_contract_admission_context(catalog,effective_date,requested_at);END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No temporal access' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT max(known_from) INTO last_known FROM market_data.contract_date_admission_receipts WHERE catalog_id=catalog;
 FOR r IN SELECT a.id,a.publication_xid,security.checked_commit_time(a.publication_xid) AS committed_at
  FROM market_data.contract_date_admissions a LEFT JOIN market_data.contract_date_admission_receipts k
  ON(k.catalog_id,k.admission_id)=(a.catalog_id,a.id) WHERE a.catalog_id=catalog AND k.admission_id IS NULL ORDER BY committed_at,a.id LOOP
  known=r.committed_at;
  IF known<last_known OR known<=certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.contract_date_admission_receipts VALUES(catalog,r.id,r.publication_xid,known);last_known=known;
 END LOOP;
 RETURN security.capture_pre_contract_admission_context(catalog,effective_date,requested_at);
END$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.contract_date_admission_visible(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_date_context(uuid,date,timestamptz),security.contract_date_admission_visible(uuid,uuid),market_data.guard_contract_date_admission() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.contract_date_admission_visible(uuid,uuid) TO openetrm_runtime;
COMMIT;
