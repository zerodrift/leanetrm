-- An editor can see a retained mapping or curve whose later version uses a different
-- dependency. Check the stored lineage when admitting a draft to either family.
BEGIN;
SET LOCAL search_path=pg_catalog;

CREATE FUNCTION market_data.guard_contract_date_draft_head() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS (SELECT 1 FROM market_data.contract_date_versions v
            WHERE (v.catalog_id,v.definition_id)=(NEW.catalog_id,NEW.definition_id) AND v.version>NEW.version)
 THEN RAISE EXCEPTION 'contract_date_conflict'; END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION market_data.guard_contract_date_draft_head() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_contract_date_draft_head() FROM PUBLIC;
CREATE TRIGGER current_draft_head AFTER INSERT OR UPDATE OF generation ON market_data.contract_date_versions
 FOR EACH ROW WHEN (NEW.state='draft') EXECUTE FUNCTION market_data.guard_contract_date_draft_head();

CREATE FUNCTION market_data.guard_custom_curve_draft_head() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS (SELECT 1 FROM market_data.custom_curve_versions v
            WHERE (v.catalog_id,v.curve_id)=(NEW.catalog_id,NEW.curve_id) AND v.version>NEW.version)
 THEN RAISE EXCEPTION 'custom_curve_conflict'; END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION market_data.guard_custom_curve_draft_head() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_custom_curve_draft_head() FROM PUBLIC;
CREATE TRIGGER current_draft_head AFTER INSERT OR UPDATE OF generation ON market_data.custom_curve_versions
 FOR EACH ROW WHEN (NEW.state='draft') EXECUTE FUNCTION market_data.guard_custom_curve_draft_head();
COMMIT;
