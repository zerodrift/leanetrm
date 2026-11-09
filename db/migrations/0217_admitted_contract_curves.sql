-- Current consumer: admitting published contract curves into market revisions (D362), and the Curves list's gear on them.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT pg_advisory_xact_lock(730849102);

-- 1. An admitted curve pins the exact definition version it admits; a curve no definition admitted names none.
ALTER TABLE market_data.curves ADD COLUMN definition_id uuid,ADD COLUMN definition_version integer,
 ADD CONSTRAINT curve_definition_pin CHECK(num_nulls(definition_id,definition_version) IN(0,2)),
 ADD CONSTRAINT curve_definition_version_fk FOREIGN KEY(catalog_id,definition_id,definition_version) REFERENCES market_data.curve_definition_versions(catalog_id,definition_id,version);

-- 2. Admission requests: a retry with the same request id and payload returns its first receipt (D238).
CREATE TABLE market_data.contract_curve_admission_requests (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),
 request_id uuid NOT NULL,payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9A-F]{64}$'),
 revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.contract_curve_admission_requests
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
ALTER TABLE market_data.contract_curve_admission_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.contract_curve_admission_requests FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.contract_curve_admission_requests FOR SELECT TO openetrm_runtime
 USING(security.current_write_context() AND security.market_access(catalog_id,'ReadMarketData') AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY scoped_insert ON market_data.contract_curve_admission_requests FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY current_insert ON market_data.contract_curve_admission_requests AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context());
REVOKE ALL ON market_data.contract_curve_admission_requests FROM PUBLIC;
GRANT SELECT,INSERT ON market_data.contract_curve_admission_requests TO openetrm_runtime;
GRANT SELECT ON market_data.contract_curve_admission_requests TO openetrm_security_owner;

-- 3. D283's admission extends beyond absolute indexes: the database finds a saved date list's context-relative occurrence itself,
-- as DateOccurrences does. Generated schedules and business-day offsets stay unadmitted; the database has no generator for them.
CREATE FUNCTION market_data.date_list_occurrence(dates jsonb,selection text,context date,occurrence integer) RETURNS date
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH listed AS(SELECT ordinal-1 AS position,value::date AS day FROM jsonb_array_elements_text(dates) WITH ORDINALITY AS d(value,ordinal)),
 origin AS(SELECT CASE selection WHEN 'absolute_index' THEN 0
   WHEN 'on_or_after' THEN(SELECT min(position) FROM listed WHERE day>=context)
   WHEN 'context_month' THEN(SELECT min(position) FROM listed WHERE date_trunc('month',day::timestamp)=date_trunc('month',context::timestamp)) END AS position)
 SELECT l.day FROM listed l,origin o WHERE l.position=o.position+occurrence;
$$;
REVOKE ALL ON FUNCTION market_data.date_list_occurrence(jsonb,text,date,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.date_list_occurrence(jsonb,text,date,integer) TO openetrm_runtime;

DO $$DECLARE body text=pg_get_functiondef('market_data.guard_contract_date_admission()'::regprocedure);updated text;
 old_dates text=$old$ SELECT count(*),min(CASE WHEN f.role='start' THEN (s.definition->'dates'->>f.occurrence)::date END),
  min(CASE WHEN f.role='end' THEN (s.definition->'dates'->>f.occurrence)::date END),
  min(CASE WHEN f.role='last_trading' THEN (s.definition->'dates'->>f.occurrence)::date END)$old$;
 new_dates text=$new$ SELECT count(*),min(market_data.date_list_occurrence(s.definition->'dates',s.selection,ctx.effective_on,f.occurrence)) FILTER(WHERE f.role='start'),
  min(market_data.date_list_occurrence(s.definition->'dates',s.selection,ctx.effective_on,f.occurrence)) FILTER(WHERE f.role='end'),
  min(market_data.date_list_occurrence(s.definition->'dates',s.selection,ctx.effective_on,f.occurrence)) FILTER(WHERE f.role='last_trading')$new$;
 old_selection text=$old$ AND s.state='published' AND s.selection='absolute_index'$old$;
 new_selection text=$new$ AND s.state='published'$new$;
BEGIN
 IF length(body)-length(replace(body,old_dates,''))<>length(old_dates) OR length(body)-length(replace(body,old_selection,''))<>length(old_selection) THEN
  RAISE EXCEPTION 'contract_admission_upgrade: unexpected market_data.guard_contract_date_admission' USING ERRCODE='55000';
 END IF;
 updated=replace(replace(body,old_dates,new_dates),old_selection,new_selection);
 EXECUTE updated;
END$$;

-- 4. Sealing a revision checks each admitted contract curve: a published contract definition on the revision's reference revision,
-- whose terms the curve carries exactly, with as many members of the definition's kind and product as its window has. Members'
-- dates are verified when they are admitted, against the date mapping and close the admission records.
CREATE FUNCTION market_data.validate_contract_curve_definitions(cat uuid,rev uuid) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.curves c LEFT JOIN market_data.curve_definition_versions v
   ON(v.catalog_id,v.definition_id,v.version)=(c.catalog_id,c.definition_id,c.definition_version)
   LEFT JOIN market_data.curve_definitions d ON(d.catalog_id,d.id)=(c.catalog_id,c.definition_id)
  WHERE c.catalog_id=cat AND c.revision_id=rev AND c.definition_id IS NOT NULL AND(v.state IS DISTINCT FROM 'published' OR v.kind NOT IN('future','forward')
   OR(d.code,v.name,v.reference_catalog_id,v.reference_revision_id,v.commodity_code,v.location_code,v.quote_code,v.profile_code,v.settlement_target_code,v.price_rule,v.mark_role,v.mark_source_code)
    IS DISTINCT FROM(c.code::text,c.name,c.reference_catalog_id,c.reference_revision_id,c.commodity_code,c.location_code,c.quote_code,c.profile_code,c.settlement_target_code,c.rule,c.direct_role,c.direct_source_code)
   OR v.contract_count<>(SELECT count(*) FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code))
   OR EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
    LEFT JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code) AND(k.kind<>v.kind OR s.product_code IS DISTINCT FROM v.futures_product_code)))) THEN
  RAISE EXCEPTION 'Curves carry exactly the terms and contract count of the published contract definition they admit' USING ERRCODE='23514';
 END IF;
END;
$$;
CREATE FUNCTION market_data.guard_contract_curve_revision() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.state='sealed' AND OLD.state='draft' THEN PERFORM market_data.validate_contract_curve_definitions(NEW.catalog_id,NEW.id);END IF;
 RETURN NEW;
END;
$$;
CREATE TRIGGER guard_contract_curve_revision BEFORE UPDATE ON market_data.revisions FOR EACH ROW EXECUTE FUNCTION market_data.guard_contract_curve_revision();
REVOKE ALL ON FUNCTION market_data.validate_contract_curve_definitions(uuid,uuid),market_data.guard_contract_curve_revision() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.validate_contract_curve_definitions(uuid,uuid) TO openetrm_security_owner;
COMMIT;
