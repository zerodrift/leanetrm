-- The contract-date library's read rules decide visibility once per statement (D282) instead of running the per-row
-- check for every version, contract and field. Write rules and explicit authority calls keep security.contract_date_visible.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SET LOCAL search_path=pg_catalog;

-- contract_date_visible(catalog, definition, version) for every version, under the bound request.
CREATE FUNCTION security.visible_contract_date_versions() RETURNS TABLE(catalog uuid, definition uuid, version integer)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cut record;working boolean=security.current_write_context();
BEGIN
 SELECT * INTO cut FROM security.request_market_cut();
 IF NOT working AND cut.catalog IS NULL THEN RETURN;END IF;
 RETURN QUERY
 WITH readable AS MATERIALIZED(SELECT security.market_catalogs('ReadMarketData') id),
  authoring AS MATERIALIZED(SELECT security.market_catalogs('WriteMarketDefinitions') id UNION SELECT security.market_catalogs('SealMarketDefinitions')),
  calendars AS MATERIALIZED(SELECT * FROM security.visible_reference_revisions()),
  -- contract_date_schedule_visible(catalog, schedule, version) for every schedule version.
  schedules AS MATERIALIZED(SELECT d.catalog_id,d.schedule_id,d.version FROM market_data.date_schedule_versions d
   WHERE d.state='published' AND d.catalog_id IN(SELECT r.id FROM readable r)
    AND(d.calendar_catalog_id IS NULL OR(d.calendar_catalog_id,d.calendar_revision_id) IN(SELECT c.catalog,c.revision FROM calendars c))
    AND(working OR(d.catalog_id=cut.catalog AND d.effective_from<=cut.effective_on AND cut.effective_on<d.effective_to
     AND EXISTS(SELECT 1 FROM market_data.knowledge_publications k
      WHERE(k.catalog_id,k.publication_order,k.publication_xid)=(d.catalog_id,d.publication_order,d.publication_xid) AND k.known_from<=cut.known_at))))
 SELECT v.catalog_id,v.definition_id,v.version FROM market_data.contract_date_versions v
  WHERE v.catalog_id IN(SELECT r.id FROM readable r)
   AND(v.state='published' OR v.catalog_id IN(SELECT a.id FROM authoring a))
   AND(working OR(v.catalog_id=cut.catalog AND EXISTS(SELECT 1 FROM market_data.contract_date_knowledge_receipts k
    WHERE(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid) AND k.known_from<=cut.known_at)))
   AND NOT EXISTS(SELECT 1 FROM market_data.contract_date_fields f
    WHERE(f.catalog_id,f.definition_id,f.version)=(v.catalog_id,v.definition_id,v.version) AND f.schedule_id IS NOT NULL
     AND(f.schedule_catalog_id,f.schedule_id,f.schedule_version) NOT IN(SELECT s.catalog_id,s.schedule_id,s.version FROM schedules s));
END$$;

-- The instant until which the statement's set holds. Reading needs ReadMarketData; a request that may author also holds
-- drafts by write or seal authority, so the set ends with the later of those. A published row can end early with it, never late.
CREATE FUNCTION security.contract_date_authority_until() RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until timestamptz=security.authority_until('ReadMarketData');authoring timestamptz;
BEGIN
 IF security.current_write_context() THEN
  authoring=greatest(security.authority_until('WriteMarketDefinitions'),security.authority_until('SealMarketDefinitions'));
  IF authoring>clock_timestamp() THEN until=least(until,authoring);END IF;
 END IF;
 RETURN until;
END$$;

-- A write takes its parent version's row lock once per field or contract. Through row security that lock would decide the
-- library's whole set each time, so an author takes it directly; the guard's per-row check after the lock stays the decision.
CREATE FUNCTION security.lock_contract_date_version(catalog uuid,definition uuid,requested_version integer) RETURNS text
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE parent text;
BEGIN
 IF NOT security.market_access(catalog,'WriteMarketDefinitions') OR NOT security.contract_date_visible(catalog,definition,requested_version) THEN RETURN NULL;END IF;
 SELECT v.state INTO parent FROM market_data.contract_date_versions v WHERE(v.catalog_id,v.definition_id,v.version)=(catalog,definition,requested_version) FOR UPDATE;
 RETURN parent;
END$$;
ALTER FUNCTION security.lock_contract_date_version(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.lock_contract_date_version(uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.lock_contract_date_version(uuid,uuid,integer) TO openetrm_runtime;
DO $$DECLARE body text=pg_get_functiondef('market_data.guard_contract_dates()'::regprocedure);source text;needle text;
BEGIN
 FOREACH source IN ARRAY ARRAY['OLD','NEW'] LOOP
  needle=format('SELECT state INTO parent_state FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=(%1$s.catalog_id,%1$s.definition_id,%1$s.version) FOR UPDATE;',source);
  IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'Unexpected contract-date guard implementation';END IF;
  body=replace(body,needle,format('IF current_user=pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid=TG_RELID)) THEN %1$s ELSE parent_state=security.lock_contract_date_version(%2$s.catalog_id,%2$s.definition_id,%2$s.version);END IF;',needle,source));
 END LOOP;
 EXECUTE body;
END$$;

DO $$ DECLARE f text;t text;p text; BEGIN
 FOREACH f IN ARRAY ARRAY['security.visible_contract_date_versions()','security.contract_date_authority_until()'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['contract_date_versions','contract_date_contracts','contract_date_fields'] LOOP
  FOREACH p IN ARRAY ARRAY['scoped_read','risk_worker_scoped_read'] LOOP
   -- The clock comes first so that a window read after the set cannot return drafts the set admitted before authority ended.
   EXECUTE format('ALTER POLICY %I ON market_data.%I USING(COALESCE(clock_timestamp() < (SELECT security.contract_date_authority_until()) AND (catalog_id,definition_id,version) IN (SELECT v.catalog,v.definition,v.version FROM security.visible_contract_date_versions() v),false))',p,t);
  END LOOP;
 END LOOP;
END $$;
COMMIT;
