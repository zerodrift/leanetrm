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

-- 2. A rate curve is an admitted published interest-rate definition with one stable series per tenor label. The revision pins
-- the definition version and carries its terms, so valuation reads them from the revision itself; sealing checks they are equal.
-- Literal-maturity rate curves are removed in section 4; a hosted window removes them first.
ALTER TABLE market_data.rate_curves ALTER COLUMN interest_rate_code DROP NOT NULL,ALTER COLUMN date_basis DROP NOT NULL,
 ADD COLUMN definition_id uuid,ADD COLUMN definition_version integer,ADD COLUMN currency_code text,ADD COLUMN mark_unit text,
 ADD COLUMN day_count text,ADD COLUMN compounding text,ADD COLUMN interpolation text,ADD COLUMN extrapolation text,
 ADD COLUMN mapping_definition_id uuid,ADD COLUMN mapping_version integer,
 ADD FOREIGN KEY(catalog_id,definition_id,definition_version) REFERENCES market_data.curve_definition_versions(catalog_id,definition_id,version),
 ADD FOREIGN KEY(catalog_id,mapping_definition_id,mapping_version) REFERENCES market_data.contract_date_versions(catalog_id,definition_id,version),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 ADD CONSTRAINT rate_curve_shape CHECK(num_nulls(interest_rate_code,date_basis)=2
   AND num_nulls(definition_id,definition_version,currency_code,mark_unit,day_count,compounding,interpolation,extrapolation,mapping_definition_id,mapping_version)=0
  OR num_nulls(interest_rate_code,date_basis)=0
   AND num_nonnulls(definition_id,definition_version,currency_code,mark_unit,day_count,compounding,interpolation,extrapolation,mapping_definition_id,mapping_version)=0);
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
-- Tenors take exactly the policies and grants dated rate samples carry (scoped and temporal reads, scoped and current writes,
-- the risk worker's reads), so the core market definition tables keep one read and write contract.
DO $$DECLARE p record;r record;BEGIN
 FOR p IN SELECT * FROM pg_policies WHERE schemaname='market_data' AND tablename='rate_curve_points' LOOP
  EXECUTE format('CREATE POLICY %I ON market_data.rate_curve_tenors AS %s FOR %s TO %s%s%s',p.policyname,p.permissive,p.cmd,
   (SELECT string_agg(quote_ident(role),',') FROM unnest(p.roles) role),
   CASE WHEN p.qual IS NULL THEN '' ELSE ' USING('||p.qual||')' END,CASE WHEN p.with_check IS NULL THEN '' ELSE ' WITH CHECK('||p.with_check||')' END);
 END LOOP;
 FOR r IN SELECT grantee,privilege_type FROM information_schema.role_table_grants WHERE table_schema='market_data' AND table_name='rate_curve_points' LOOP
  EXECUTE format('GRANT %s ON market_data.rate_curve_tenors TO %I',r.privilege_type,r.grantee);
 END LOOP;
END$$;
REVOKE ALL ON market_data.rate_curve_tenors FROM PUBLIC;

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
CREATE POLICY current_insert ON market_data.rate_curve_admission_requests AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.current_write_context());
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
  WHERE r.catalog_id=cat AND r.revision_id=rev AND(v.state IS DISTINCT FROM 'published' OR v.kind<>'interest_rate'
   OR(v.reference_catalog_id,v.reference_revision_id,v.mark_source_code,v.name,v.currency_code,v.mark_unit,v.day_count,v.compounding,v.interpolation,
      v.extrapolation,v.mapping_definition_id,v.mapping_version)
    IS DISTINCT FROM(r.reference_catalog_id,r.reference_revision_id,r.source_code,r.name,r.currency_code,r.mark_unit,r.day_count,r.compounding,r.interpolation,
      r.extrapolation,r.mapping_definition_id,r.mapping_version))) THEN
  RAISE EXCEPTION 'Rate curves carry exactly the terms of the published interest-rate definition they admit' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.rate_curves r WHERE r.catalog_id=cat AND r.revision_id=rev AND
  NOT EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(r.catalog_id,r.revision_id,r.code))) THEN
  RAISE EXCEPTION 'Rate curves require their tenors' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t JOIN market_data.series s ON(t.catalog_id,t.revision_id,t.series_id)=(s.catalog_id,s.revision_id,s.id)
  LEFT JOIN ref_data.value_specifications v ON(s.reference_catalog_id,s.reference_revision_id,s.value_specification_code)=(v.catalog_id,v.revision_id,v.code)
  WHERE t.catalog_id=cat AND t.revision_id=rev AND(s.contract_id IS NOT NULL OR s.quote_code IS NOT NULL OR v.kind IS DISTINCT FROM 'dimensionless')) THEN
  RAISE EXCEPTION 'Rate tenors require typed non-contract decimal series' USING ERRCODE='23514';
 END IF;
END;
$$;

-- 3. Payment discounting in risk jobs resolves a rate curve's pinned date list (D359): the risk worker reads the contract-date
-- library as the runtime does, under its own live service authority (0076's pattern; schedules are already readable).
DO $$DECLARE item text;p record;BEGIN
 FOREACH item IN ARRAY ARRAY['contract_date_definitions','contract_date_versions','contract_date_contracts','contract_date_fields',
  'contract_date_knowledge_receipts'] LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='market_data' AND tablename=item AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' AND permissive='PERMISSIVE')
  THEN RAISE EXCEPTION 'risk_worker_policy_missing: market_data.%',item;END IF;
  FOR p IN SELECT * FROM pg_policies WHERE schemaname='market_data' AND tablename=item AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' LOOP
   EXECUTE format('CREATE POLICY %I ON market_data.%I AS %s FOR SELECT TO openetrm_risk_worker USING(%s)','risk_worker_'||p.policyname,item,p.permissive,p.qual);
  END LOOP;
  EXECUTE format('CREATE POLICY risk_worker_service ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',item);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_risk_worker',item);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.contract_date_visible(uuid,uuid,integer),security.contract_date_schedule_visible(uuid,uuid,integer) TO openetrm_risk_worker;

-- Close-risk jobs pin the rate numerical policy their valuations use: rate curves now compute under RateCurveMath's policy.
DO $$DECLARE d text=pg_get_functiondef('automation.create_close_risk_job(jsonb,uuid,jsonb,bigint,bigint)'::regprocedure);BEGIN
 IF (length(d)-length(replace(d,'simple_actual365fixed_rational_to_even_18_v1','')))/length('simple_actual365fixed_rational_to_even_18_v1')<>1 THEN
  RAISE EXCEPTION 'rate_policy_upgrade: unexpected automation.create_close_risk_job' USING ERRCODE='55000';
 END IF;
 EXECUTE replace(d,'simple_actual365fixed_rational_to_even_18_v1','rate_curve_lattice30_to_even_18_v1');
END$$;

-- 4. Literal-maturity rate curves are removed (D359): a rate curve's pillars come only from its definition's date list.
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM market_data.rate_curve_points) OR EXISTS(SELECT 1 FROM market_data.rate_curves WHERE definition_id IS NULL) THEN
  RAISE EXCEPTION 'literal_rate_curves: remove dated rate curves, their discount defaults and observations before this migration' USING ERRCODE='55000';
 END IF;
END$$;
DROP TABLE market_data.rate_curve_points;
ALTER TABLE market_data.rate_curves DROP CONSTRAINT rate_curve_shape,DROP COLUMN interest_rate_code,DROP COLUMN date_basis,
 ALTER COLUMN definition_id SET NOT NULL,ALTER COLUMN definition_version SET NOT NULL,ALTER COLUMN currency_code SET NOT NULL,
 ALTER COLUMN mark_unit SET NOT NULL,ALTER COLUMN day_count SET NOT NULL,ALTER COLUMN compounding SET NOT NULL,ALTER COLUMN interpolation SET NOT NULL,
 ALTER COLUMN extrapolation SET NOT NULL,ALTER COLUMN mapping_definition_id SET NOT NULL,ALTER COLUMN mapping_version SET NOT NULL;
-- The curve owns its interpolation; a currency's default names only the curve.
ALTER TABLE trading.currency_discount_defaults DROP COLUMN interpolation_policy;

COMMIT;
