-- Current consumer: payment discounting (D359). An interest-rate curve definition owns its currency, mark unit and conventions.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';

-- 1. Interest-rate definitions own their terms instead of naming an interest-rate specification.
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM market_data.curve_definition_versions WHERE kind='interest_rate') THEN
  RAISE EXCEPTION 'Interest-rate curve definitions without their own conventions exist; remove them in the schema window first' USING ERRCODE='55000';
 END IF;
END$$;
ALTER TABLE market_data.curve_definition_versions DROP COLUMN interest_rate_code;
ALTER TABLE market_data.curve_definition_versions DROP CONSTRAINT curve_definition_versions_interpolation_check;
ALTER TABLE market_data.curve_definition_versions
 ADD COLUMN currency_code text,
 ADD COLUMN mark_unit text CHECK(mark_unit IN('percent_per_year','decimal_per_year')),
 ADD COLUMN day_count text CHECK(day_count IN('actual_365_fixed','actual_360')),
 ADD COLUMN compounding text CHECK(compounding IN('simple','continuous','annual')),
 ADD COLUMN extrapolation text CHECK(extrapolation IN('none','flat_zero','flat_forward')),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 -- Contract curves interpolate 'none'; an interest-rate curve names one of its offered interpolations.
 ADD CHECK(CASE kind
  WHEN 'interest_rate' THEN interpolation IN('log_linear_discount','linear_zero')
   AND num_nulls(currency_code,mark_unit,day_count,compounding,extrapolation)=0
   AND num_nonnulls(commodity_code,location_code,quote_code,profile_code,futures_product_code,settlement_target_code,price_rule,mark_role,missing_price)=0
  ELSE interpolation='none' AND num_nonnulls(currency_code,mark_unit,day_count,compounding,extrapolation)=0
   AND num_nulls(commodity_code,location_code,quote_code,profile_code,settlement_target_code,price_rule,mark_role,missing_price)=0
   AND (kind='future')=(futures_product_code IS NOT NULL) END);
GRANT UPDATE(currency_code,mark_unit,day_count,compounding,extrapolation) ON market_data.curve_definition_versions TO openetrm_runtime;

-- 2. A rate curve is an admitted published interest-rate definition with one stable series per tenor label. Literal-maturity
-- rate curves remain readable until the cutover that removes them; a row is exactly one of the two shapes.
ALTER TABLE market_data.rate_curves ALTER COLUMN interest_rate_code DROP NOT NULL,ALTER COLUMN date_basis DROP NOT NULL,
 ADD COLUMN definition_id uuid,ADD COLUMN definition_version integer,
 ADD FOREIGN KEY(catalog_id,definition_id,definition_version) REFERENCES market_data.curve_definition_versions(catalog_id,definition_id,version),
 ADD CONSTRAINT rate_curve_shape CHECK(num_nulls(interest_rate_code,date_basis)=2 AND num_nulls(definition_id,definition_version)=0
  OR num_nulls(interest_rate_code,date_basis)=0 AND num_nulls(definition_id,definition_version)=2);
CREATE TABLE market_data.rate_curve_tenors (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 119),label text NOT NULL CHECK(length(label) BETWEEN 1 AND 80),series_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,curve_code,ordinal),UNIQUE(catalog_id,revision_id,curve_code,label),UNIQUE(catalog_id,revision_id,series_id),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.rate_curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id)
);
CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.rate_curve_tenors
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition();
ALTER TABLE market_data.rate_curve_tenors ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.rate_curve_tenors FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.rate_curve_tenors FOR SELECT TO openetrm_runtime USING(true);
CREATE POLICY current_authority ON market_data.rate_curve_tenors AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_access(catalog_id,'ReadMarketData'));
CREATE POLICY version_scope ON market_data.rate_curve_tenors AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.market_fact_visible(catalog_id,revision_id,NULL));
CREATE POLICY scoped_insert ON market_data.rate_curve_tenors FOR INSERT TO openetrm_runtime
 WITH CHECK(security.market_access(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY current_insert ON market_data.rate_curve_tenors AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context());
CREATE POLICY owner_access ON market_data.rate_curve_tenors TO openetrm_security_owner USING(true) WITH CHECK(true);
-- The risk worker reads tenors exactly as the runtime does (0073).
DO $$DECLARE p record;BEGIN
 FOR p IN SELECT * FROM pg_policies WHERE schemaname='market_data' AND tablename='rate_curve_tenors' AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' LOOP
  EXECUTE format('CREATE POLICY %I ON market_data.rate_curve_tenors AS %s FOR SELECT TO openetrm_risk_worker USING(%s)','risk_worker_'||p.policyname,p.permissive,p.qual);
 END LOOP;
END$$;
REVOKE ALL ON market_data.rate_curve_tenors FROM PUBLIC;
GRANT SELECT,INSERT ON market_data.rate_curve_tenors TO openetrm_runtime;
GRANT SELECT ON market_data.rate_curve_tenors TO openetrm_security_owner,openetrm_risk_worker;

-- Admission requests: a retry with the same request id and payload returns its first receipt (D238).
CREATE TABLE market_data.rate_curve_admission_requests (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),
 request_id uuid NOT NULL,payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9A-F]{64}$'),
 revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.rate_curves(catalog_id,revision_id,code)
);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.rate_curve_admission_requests
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
ALTER TABLE market_data.rate_curve_admission_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.rate_curve_admission_requests FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.rate_curve_admission_requests FOR SELECT TO openetrm_runtime
 USING(security.current_write_context() AND security.market_access(catalog_id,'ReadMarketData') AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
CREATE POLICY scoped_insert ON market_data.rate_curve_admission_requests FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context() AND actor_id=security.custom_curve_actor(catalog_id,'WriteMarketDefinitions'));
REVOKE ALL ON market_data.rate_curve_admission_requests FROM PUBLIC;
GRANT SELECT,INSERT ON market_data.rate_curve_admission_requests TO openetrm_runtime;
GRANT SELECT ON market_data.rate_curve_admission_requests TO openetrm_security_owner;

-- Sealing a revision checks every admitted rate curve: a published interest-rate definition on the revision's reference revision,
-- its tenors' series typed non-contract dimensionless values, and at least one tenor.
CREATE OR REPLACE FUNCTION market_data.validate_rate_revision(cat uuid,rev uuid) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.rate_curves r JOIN market_data.curves c
  ON(r.catalog_id,r.revision_id,r.code)=(c.catalog_id,c.revision_id,c.code) WHERE r.catalog_id=cat AND r.revision_id=rev) THEN
  RAISE EXCEPTION 'Curve names must be distinct across supported curve kinds' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.rate_curves r LEFT JOIN market_data.curve_definition_versions v
  ON(v.catalog_id,v.definition_id,v.version)=(r.catalog_id,r.definition_id,r.definition_version)
  WHERE r.catalog_id=cat AND r.revision_id=rev AND r.definition_id IS NOT NULL AND(v.state IS DISTINCT FROM 'published' OR v.kind<>'interest_rate'
   OR(v.reference_catalog_id,v.reference_revision_id)<>(r.reference_catalog_id,r.reference_revision_id) OR v.mark_source_code<>r.source_code)) THEN
  RAISE EXCEPTION 'Rate curves admit a published interest-rate definition on the same reference revision and source' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.rate_curves r WHERE r.catalog_id=cat AND r.revision_id=rev AND
  NOT EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(r.catalog_id,r.revision_id,r.code))
  AND NOT EXISTS(SELECT 1 FROM market_data.rate_curve_points p WHERE(p.catalog_id,p.revision_id,p.curve_code)=(r.catalog_id,r.revision_id,r.code))) THEN
  RAISE EXCEPTION 'Rate curves require their tenors or dated samples' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.rate_curves r WHERE r.catalog_id=cat AND r.revision_id=rev AND
  (r.definition_id IS NULL)=EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(r.catalog_id,r.revision_id,r.code))) THEN
  RAISE EXCEPTION 'Only admitted rate curves have tenors' USING ERRCODE='23514';
 END IF;
-- Literal-maturity samples keep their checks until the cutover removes them.
  IF EXISTS (SELECT 1 FROM market_data.rate_curve_points p JOIN market_data.rate_curve_points q
    ON (p.catalog_id,p.revision_id,p.series_id)=(q.catalog_id,q.revision_id,q.series_id)
    WHERE p.catalog_id=cat AND p.revision_id=rev AND p.maturity<>q.maturity) THEN
    RAISE EXCEPTION 'A rate series cannot represent different maturities' USING ERRCODE='23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.rate_curve_points p JOIN market_data.series s
    ON (p.catalog_id,p.revision_id,p.series_id)=(s.catalog_id,s.revision_id,s.id)
    LEFT JOIN ref_data.value_specifications v ON (s.reference_catalog_id,s.reference_revision_id,s.value_specification_code)=(v.catalog_id,v.revision_id,v.code)
    WHERE p.catalog_id=cat AND p.revision_id=rev AND (s.contract_id IS NOT NULL OR s.quote_code IS NOT NULL OR v.kind IS DISTINCT FROM 'dimensionless')) THEN
    RAISE EXCEPTION 'Rate samples require typed non-contract decimal rates' USING ERRCODE='23514';
  END IF;
  IF EXISTS (SELECT 1 FROM (SELECT maturity,lag(maturity) OVER(PARTITION BY curve_code ORDER BY ordinal) AS previous
    FROM market_data.rate_curve_points WHERE catalog_id=cat AND revision_id=rev) p WHERE maturity<=previous) THEN
    RAISE EXCEPTION 'Rate maturity order must increase' USING ERRCODE='23514';
  END IF;
 IF EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t JOIN market_data.series s ON(t.catalog_id,t.revision_id,t.series_id)=(s.catalog_id,s.revision_id,s.id)
  LEFT JOIN ref_data.value_specifications v ON(s.reference_catalog_id,s.reference_revision_id,s.value_specification_code)=(v.catalog_id,v.revision_id,v.code)
  WHERE t.catalog_id=cat AND t.revision_id=rev AND(s.contract_id IS NOT NULL OR s.quote_code IS NOT NULL OR v.kind IS DISTINCT FROM 'dimensionless')) THEN
  RAISE EXCEPTION 'Rate tenors require typed non-contract decimal series' USING ERRCODE='23514';
 END IF;
END;
$$;

COMMIT;
