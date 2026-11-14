-- Consumers: every new physical margin policy and adoption, including an
-- option's atomic generated-future adoption. Existing receipts and identical
-- saved facts do not insert rows and retain their first outcomes unchanged.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION settlement_data.require_qualified_physical_margin(p settlement_data.futures_margin_policies) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE method text;BEGIN
 SELECT product.settlement_method INTO STRICT method FROM market_data.contracts c
 JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)
 JOIN ref_data.futures_products product ON(product.catalog_id,product.revision_id,product.code)=(s.catalog_id,s.revision_id,s.product_code)
 WHERE(c.catalog_id,c.revision_id,c.id)=(p.catalog_id,p.definition_revision_id,p.contract_id);
 IF method='physical' AND(p.implementation<>'dated_futures_margin_policy_v2' OR p.delivery_profile_id IS NULL)
 THEN RAISE EXCEPTION 'futures_delivery_policy_required';END IF;
END$$;
CREATE FUNCTION settlement_data.guard_qualified_physical_policy() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM settlement_data.require_qualified_physical_margin(NEW);RETURN NEW;END$$;
CREATE FUNCTION settlement_data.guard_qualified_physical_adoption() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_margin_policies;BEGIN
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies s
 WHERE(s.catalog_id,s.id,s.revision)=(NEW.catalog_id,NEW.policy_id,NEW.policy_revision);
 PERFORM settlement_data.require_qualified_physical_margin(p);RETURN NEW;
END$$;
CREATE TRIGGER qualified_physical_policy BEFORE INSERT ON settlement_data.futures_margin_policies
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_qualified_physical_policy();
CREATE TRIGGER qualified_physical_adoption BEFORE INSERT ON settlement_data.futures_margin_adoptions
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_qualified_physical_adoption();

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['settlement_data.require_qualified_physical_margin(settlement_data.futures_margin_policies)'::regprocedure,
  'settlement_data.guard_qualified_physical_policy()'::regprocedure,'settlement_data.guard_qualified_physical_adoption()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
COMMIT;
