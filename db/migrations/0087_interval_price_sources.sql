-- Explicit collected actuals and final-settlement anchor policies for shaped interval prices.
-- Drain runtime admission and the collection worker before this schema upgrade.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT pg_advisory_xact_lock(730849110);
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Preserve the 0063 compatibility contract in one invoker-rights predicate. The caller must
-- still see every exact curve, reference and source row; this function grants no authority.
CREATE FUNCTION market_data.collected_source_fits(catalog uuid,revision uuid,curve ref_data.code,
 reference_catalog uuid,reference_revision uuid,source uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT EXISTS(
  SELECT 1 FROM market_data.curves c
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=
   (c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=
   (q.catalog_id,q.revision_id,q.quantity_code)
  JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=
   (q.catalog_id,q.revision_id,q.currency_code)
  JOIN power_data.sources s ON(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
   (source,catalog,reference_catalog,reference_revision)
  JOIN ref_data.clock_rules clock ON(clock.catalog_id,clock.revision_id,clock.code)=
   (s.reference_catalog_id,s.reference_revision_id,s.clock_code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,revision,curve)
   AND q.kind='outright' AND q.multiplier=1 AND currency.display_code='USD'
   AND u.dimension_code='energy' AND u.unit_code='megawatt_hour' AND u.commodity_code=c.commodity_code
   AND(s.currency_code,s.quantity_code,s.location_code)=(q.currency_code,q.quantity_code,c.location_code)
   AND clock.kind='iana' AND clock.iana_zone=CASE s.provider WHEN 'PJM' THEN 'America/New_York' WHEN 'ERCOT' THEN 'America/Chicago' END
 )
$$;
REVOKE ALL ON FUNCTION market_data.collected_source_fits(uuid,uuid,ref_data.code,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.collected_source_fits(uuid,uuid,ref_data.code,uuid,uuid,uuid)
 TO openetrm_runtime,openetrm_security_owner;
CREATE OR REPLACE FUNCTION market_data.validate_curve_physical_price_source() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT market_data.collected_source_fits(NEW.catalog_id,NEW.revision_id,NEW.curve_code,
   NEW.reference_catalog_id,NEW.reference_revision_id,NEW.source_id)
 THEN RAISE EXCEPTION 'physical_source_definition: exact power quote, location, units and source clock must agree' USING ERRCODE='23514';END IF;
 RETURN NEW;
END$$;

-- Existing bindings had only period semantics. Preserve that meaning during migration, but
-- require every newly authored binding to declare its role explicitly.
ALTER TABLE market_data.shaped_curve_blocks ADD COLUMN anchor_role text NOT NULL DEFAULT 'period'
 CHECK(anchor_role IN('period','balance'));
ALTER TABLE market_data.shaped_curve_blocks ALTER COLUMN anchor_role DROP DEFAULT;

CREATE TABLE market_data.shaped_curve_actual_rules (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_id uuid NOT NULL,
 anchor text NOT NULL CHECK(anchor IN('interval_start','interval_end')),
 offset_minutes integer NOT NULL CHECK(offset_minutes BETWEEN 0 AND 10080),
 rule_version text NOT NULL CHECK(rule_version='interval_actual_due_v1'),
 PRIMARY KEY(catalog_id,revision_id,curve_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.shaped_curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(source_id,catalog_id,reference_catalog_id,reference_revision_id)
  REFERENCES power_data.sources(id,catalog_id,reference_catalog_id,reference_revision_id)
);
COMMENT ON TABLE market_data.shaped_curve_actual_rules IS
 'Exact collected interval source and due rule for a shaped curve. Collected observations replace their cells without recalibrating the remaining modeled prices.';
CREATE TABLE market_data.curve_expiry_rules (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 rule_version text NOT NULL CHECK(rule_version='freeze_final_settlement_v1'),
 PRIMARY KEY(catalog_id,revision_id,curve_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code)
);
COMMENT ON TABLE market_data.curve_expiry_rules IS
 'Opt-in frozen final-settlement anchor for a direct date-only shaped block. Observation role, final state, exact source, expiry and cutoffs are qualified by the managed consumer; no last-quote fallback.';

-- Extend only the period partition check. All interval, block quote/profile/shape and marked
-- actual rules stay intact. Refuse an unexpected prior validator rather than rewrite it broadly.
DO $$
DECLARE definition text;
 old_join constant text='LEFT JOIN market_data.shaped_curve_blocks x ON(x.catalog_id,x.revision_id,x.shaped_curve_code)=(s.catalog_id,s.revision_id,s.code)';
BEGIN
 definition=pg_get_functiondef('market_data.validate_interval_curve_definitions()'::regprocedure);
 IF length(definition)-length(replace(definition,old_join,''))<>length(old_join) THEN
  RAISE EXCEPTION 'Unexpected interval curve period partition predicate';
 END IF;
 EXECUTE replace(definition,old_join,old_join||' AND x.anchor_role=''period''');
END$$;

CREATE FUNCTION market_data.validate_interval_price_sources() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.shaped_curve_actual_rules r
   WHERE(r.catalog_id,r.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
    SELECT 1 FROM market_data.shaped_curve_blocks b
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(b.catalog_id,b.revision_id,b.block_curve_code)
    JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    JOIN power_data.sources s ON(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
     (r.source_id,r.catalog_id,r.reference_catalog_id,r.reference_revision_id)
    WHERE(b.catalog_id,b.revision_id,b.shaped_curve_code,b.anchor_role)=(r.catalog_id,r.revision_id,r.curve_code,'period')
     AND market_data.collected_source_fits(c.catalog_id,c.revision_id,c.code,r.reference_catalog_id,r.reference_revision_id,r.source_id)
     AND(s.clock_code,s.minutes)=(p.clock_code,p.interval_minutes)))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='A shaped actual rule requires a compatible collected source on its period blocks'' exact reference version, clock and interval length.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.curve_expiry_rules r
   WHERE(r.catalog_id,r.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
    SELECT 1 FROM market_data.curves c
    JOIN market_data.curve_blocks b ON(b.catalog_id,b.revision_id,b.curve_code)=(c.catalog_id,c.revision_id,c.code)
    JOIN market_data.shaped_curve_blocks x ON(x.catalog_id,x.revision_id,x.block_curve_code)=(b.catalog_id,b.revision_id,b.curve_code)
    WHERE(c.catalog_id,c.revision_id,c.code)=(r.catalog_id,r.revision_id,r.curve_code) AND c.rule='direct'
     AND NOT EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.contract_intervals i
      ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
      WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code))))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='An expiry rule binds only a direct date-only block consumed by a shaped curve.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.shaped_curve_blocks x
   JOIN market_data.curve_blocks b ON(b.catalog_id,b.revision_id,b.curve_code)=(x.catalog_id,x.revision_id,x.block_curve_code)
   WHERE(x.catalog_id,x.revision_id)=(NEW.catalog_id,NEW.id) AND x.anchor_role='balance' AND NOT EXISTS(
    SELECT 1 FROM market_data.shaped_curve_blocks y
    JOIN market_data.curve_blocks p ON(p.catalog_id,p.revision_id,p.curve_code)=(y.catalog_id,y.revision_id,y.block_curve_code)
    WHERE(y.catalog_id,y.revision_id,y.shaped_curve_code,y.anchor_role)=(x.catalog_id,x.revision_id,x.shaped_curve_code,'period')
     AND(p.membership,p.tou_product_id,p.tou_version) IS NOT DISTINCT FROM(b.membership,b.tou_product_id,b.tou_version)))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='A balance block must match a period block''s TOU product version and membership.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.shaped_curve_blocks x
   JOIN market_data.curve_blocks b ON(b.catalog_id,b.revision_id,b.curve_code)=(x.catalog_id,x.revision_id,x.block_curve_code)
   JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(b.catalog_id,b.revision_id,b.curve_code)
   JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(x.catalog_id,x.revision_id)=(NEW.catalog_id,NEW.id) AND x.anchor_role='balance' AND 1<>(
    SELECT count(*) FROM market_data.shaped_curve_blocks y
    JOIN market_data.curve_blocks p ON(p.catalog_id,p.revision_id,p.curve_code)=(y.catalog_id,y.revision_id,y.block_curve_code)
    JOIN market_data.curve_members n ON(n.catalog_id,n.revision_id,n.curve_code)=(p.catalog_id,p.revision_id,p.curve_code)
    JOIN market_data.contracts parent ON(parent.catalog_id,parent.revision_id,parent.id)=(n.catalog_id,n.revision_id,n.contract_id)
    WHERE(y.catalog_id,y.revision_id,y.shaped_curve_code,y.anchor_role)=(x.catalog_id,x.revision_id,x.shaped_curve_code,'period')
     AND(p.membership,p.tou_product_id,p.tou_version) IS NOT DISTINCT FROM(b.membership,b.tou_product_id,b.tou_version)
     AND k.delivery_from>=parent.delivery_from AND k.delivery_from<parent.delivery_to AND k.delivery_to=parent.delivery_to))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='Each balance constituent starts on an operating date inside exactly one matching period constituent and ends at that period''s end.';
 END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER validate_interval_price_sources BEFORE UPDATE OF state ON market_data.revisions
 FOR EACH ROW WHEN(OLD.state='draft' AND NEW.state='sealed') EXECUTE FUNCTION market_data.validate_interval_price_sources();
REVOKE ALL ON FUNCTION market_data.validate_interval_price_sources() FROM PUBLIC;

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['shaped_curve_actual_rules','curve_expiry_rules'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY temporal_read ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.market_fact_visible(catalog_id,revision_id,NULL))',t);
  EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK(true)',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context() AND security.market_access(catalog_id,''WriteMarketDefinitions''))',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;
-- Retain the source/reference authority restrictions of the marked mapping as well as the
-- common definition policies. An extra permissive policy cannot bypass either restriction.
CREATE POLICY reference_read ON market_data.shaped_curve_actual_rules AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY source_read ON market_data.shaped_curve_actual_rules AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM power_data.sources s WHERE(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
  (shaped_curve_actual_rules.source_id,shaped_curve_actual_rules.catalog_id,
   shaped_curve_actual_rules.reference_catalog_id,shaped_curve_actual_rules.reference_revision_id)));
CREATE POLICY source_insert ON market_data.shaped_curve_actual_rules AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(security.reference_access(reference_catalog_id,'ReadMarketData')
  AND EXISTS(SELECT 1 FROM power_data.sources s WHERE(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=
   (shaped_curve_actual_rules.source_id,shaped_curve_actual_rules.catalog_id,
    shaped_curve_actual_rules.reference_catalog_id,shaped_curve_actual_rules.reference_revision_id)));
CREATE POLICY reference_read ON market_data.curve_expiry_rules AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(EXISTS(SELECT 1 FROM market_data.curves c
  WHERE(c.catalog_id,c.revision_id,c.code)=(curve_expiry_rules.catalog_id,curve_expiry_rules.revision_id,curve_expiry_rules.curve_code)
   AND security.reference_version_visible(c.reference_catalog_id,c.reference_revision_id)));
CREATE POLICY reference_insert ON market_data.curve_expiry_rules AS RESTRICTIVE FOR INSERT TO openetrm_runtime
 WITH CHECK(EXISTS(SELECT 1 FROM market_data.curves c
  WHERE(c.catalog_id,c.revision_id,c.code)=(curve_expiry_rules.catalog_id,curve_expiry_rules.revision_id,curve_expiry_rules.curve_code)
   AND security.reference_access(c.reference_catalog_id,'ReadMarketData')));
-- The shared curve loader also serves the dedicated connected-risk worker.
-- Preserve every runtime SELECT predicate and add its current service fence.
DO $$DECLARE p record;BEGIN
 FOR p IN SELECT * FROM pg_policies WHERE schemaname='market_data' AND tablename='curve_expiry_rules'
  AND 'openetrm_runtime'=ANY(roles) AND cmd='SELECT' LOOP
  EXECUTE format('CREATE POLICY %I ON market_data.curve_expiry_rules AS %s FOR SELECT TO openetrm_risk_worker USING(%s)',
   'risk_worker_'||p.policyname,p.permissive,p.qual);
 END LOOP;
END$$;
CREATE POLICY risk_worker_service ON market_data.curve_expiry_rules AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
GRANT SELECT ON market_data.curve_expiry_rules TO openetrm_risk_worker;
COMMIT;
