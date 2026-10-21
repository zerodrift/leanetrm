-- Interval curves (docs/ppa-design.md §6, phase B). A contract can carry one exact UTC delivery
-- interval with its market clock's operating date and local labels, so a directly marked hourly or
-- quarter-hour price is an ordinary observation of an ordinary forward. A block curve crosses its
-- date-only constituents with one TOU membership, and a shaped curve calibrates block marks onto one
-- shape series. An interval curve's collected actual prices become due by an explicit rule. Sealing
-- a bundle refuses interval contracts, blocks, shaped curves and due rules that break these
-- contracts; each rule names itself in the refusal's detail.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE market_data.contract_intervals (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,contract_id uuid NOT NULL,
 starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,operating_date date NOT NULL,
 hour_beginning smallint NOT NULL CHECK(hour_beginning BETWEEN 0 AND 23),
 minute smallint NOT NULL CHECK(minute BETWEEN 0 AND 59),
 occurrence smallint NOT NULL CHECK(occurrence IN(1,2)),
 PRIMARY KEY(catalog_id,revision_id,contract_id),
 FOREIGN KEY(catalog_id,revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 CHECK(ends_at-starts_at IN(interval '5 minutes',interval '15 minutes',interval '30 minutes',interval '60 minutes'))
);
COMMENT ON TABLE market_data.contract_intervals IS
 'One exact UTC delivery interval per interval contract, with the clock''s operating date, local hour, minute and repeated-hour occurrence.';

CREATE TABLE market_data.curve_blocks (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 membership text NOT NULL CHECK(membership IN('all','peak','off')),
 tou_product_id uuid,tou_version integer,
 PRIMARY KEY(catalog_id,revision_id,curve_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,tou_product_id,tou_version) REFERENCES market_data.tou_product_versions(catalog_id,product_id,version),
 CHECK((membership='all')=(tou_product_id IS NULL) AND (tou_product_id IS NULL)=(tou_version IS NULL))
);
COMMENT ON TABLE market_data.curve_blocks IS
 'A block curve: its date-only constituents crossed with all hours, or the peak or off-peak hours of one published TOU product version, on its delivery profile''s clock, holiday calendar and grid.';

CREATE TABLE market_data.shaped_curves (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 80 AND btrim(name)=name),
 method text NOT NULL CHECK(method IN('additive','multiplicative')),
 calibration_policy text NOT NULL CHECK(calibration_policy='duration_weighted_block_mean_v1'),
 operator_version text NOT NULL CHECK(operator_version='block_shape_v1'),
 shape_series_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES market_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,shape_series_id) REFERENCES market_data.interval_series(catalog_id,id)
);
COMMENT ON TABLE market_data.shaped_curves IS
 'An interval curve whose prices calibrate its block marks onto one shape series by an exact method, policy and operator version.';

CREATE TABLE market_data.shaped_curve_blocks (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,shaped_curve_code ref_data.code NOT NULL,block_curve_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,shaped_curve_code,block_curve_code),
 FOREIGN KEY(catalog_id,revision_id,shaped_curve_code) REFERENCES market_data.shaped_curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,block_curve_code) REFERENCES market_data.curve_blocks(catalog_id,revision_id,curve_code)
);
COMMENT ON TABLE market_data.shaped_curve_blocks IS
 'The block curves whose marks partition a shaped curve''s delivery.';

CREATE TABLE market_data.curve_actual_rules (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 anchor text NOT NULL CHECK(anchor IN('interval_start','interval_end')),
 offset_minutes integer NOT NULL CHECK(offset_minutes BETWEEN 0 AND 10080),
 rule_version text NOT NULL CHECK(rule_version='interval_actual_due_v1'),
 PRIMARY KEY(catalog_id,revision_id,curve_code),
 FOREIGN KEY(catalog_id,revision_id,curve_code) REFERENCES market_data.curve_physical_price_sources(catalog_id,revision_id,curve_code)
);
COMMENT ON TABLE market_data.curve_actual_rules IS
 'When each interval''s collected actual price from the curve''s bound source is due: its start or end plus an offset.';

-- Runs beside, never instead of, validate_revision when a draft is sealed. Every rule that reads a
-- curve's reference rows, TOU versions, series or sources requires a matching visible row, so rows
-- hidden from the sealer refuse the seal.
CREATE FUNCTION market_data.validate_interval_curve_definitions() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.contract_intervals i
   JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(i.catalog_id,i.revision_id,i.contract_id)
   WHERE(i.catalog_id,i.revision_id)=(NEW.catalog_id,NEW.id)
    AND(c.kind<>'forward' OR c.delivery_from<>i.operating_date OR c.delivery_to<>i.operating_date+1 OR c.expiry_at<>i.ends_at))
 OR EXISTS(SELECT 1 FROM market_data.curve_members m
   LEFT JOIN market_data.contract_intervals i ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(m.catalog_id,m.revision_id)=(NEW.catalog_id,NEW.id)
   GROUP BY m.curve_code HAVING count(i.contract_id) NOT IN(0,count(*)))
 OR EXISTS(SELECT 1 FROM market_data.curve_members m
   JOIN market_data.contract_intervals i ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(m.catalog_id,m.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
    SELECT 1 FROM market_data.curves c JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    WHERE(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code) AND i.ends_at-i.starts_at=make_interval(mins=>p.interval_minutes)))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='Interval contracts are forwards on their operating date expiring at their interval end, alone in their curve and as long as its delivery profile''s interval.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.shaped_curves s JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.catalog_id,s.revision_id,s.code)
   WHERE(s.catalog_id,s.revision_id)=(NEW.catalog_id,NEW.id))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',DETAIL='A shaped curve code must differ from every curve code.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.curve_blocks b WHERE(b.catalog_id,b.revision_id)=(NEW.catalog_id,NEW.id) AND(NOT EXISTS(
    SELECT 1 FROM market_data.curves c JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    WHERE(c.catalog_id,c.revision_id,c.code)=(b.catalog_id,b.revision_id,b.curve_code) AND c.rule='direct' AND p.interval_minutes IN(5,15,30,60)) OR EXISTS(
    SELECT 1 FROM market_data.curve_members m JOIN market_data.contract_intervals i ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(b.catalog_id,b.revision_id,b.curve_code))))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='A block curve is a direct curve of date-only constituents on a 5, 15, 30 or 60 minute delivery profile.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.shaped_curves s
   LEFT JOIN market_data.shaped_curve_blocks x ON(x.catalog_id,x.revision_id,x.shaped_curve_code)=(s.catalog_id,s.revision_id,s.code)
   LEFT JOIN market_data.curve_blocks b ON(b.catalog_id,b.revision_id,b.curve_code)=(x.catalog_id,x.revision_id,x.block_curve_code)
   WHERE(s.catalog_id,s.revision_id)=(NEW.catalog_id,NEW.id) GROUP BY s.code
   HAVING NOT coalesce(count(b.curve_code)=1 AND bool_and(b.membership='all') OR count(b.curve_code)=2
    AND count(*) FILTER(WHERE b.membership='peak')=1 AND count(*) FILTER(WHERE b.membership='off')=1
    AND count(DISTINCT b.tou_product_id)=1 AND count(DISTINCT b.tou_version)=1,false))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='A shaped curve has one all-hours block, or the peak and off-peak blocks of one TOU product version.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.shaped_curve_blocks x
   JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(x.catalog_id,x.revision_id,x.block_curve_code)
   WHERE(x.catalog_id,x.revision_id)=(NEW.catalog_id,NEW.id) GROUP BY x.shaped_curve_code
   HAVING count(DISTINCT(c.quote_code,c.commodity_code,c.location_code,c.profile_code))>1)
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='The blocks of a shaped curve share their quote, commodity, location and delivery profile.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.curve_blocks b WHERE(b.catalog_id,b.revision_id)=(NEW.catalog_id,NEW.id) AND b.membership<>'all' AND NOT EXISTS(
    SELECT 1 FROM market_data.curves c JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    JOIN market_data.tou_product_versions v ON(v.catalog_id,v.product_id,v.version)=(b.catalog_id,b.tou_product_id,b.tou_version)
    WHERE(c.catalog_id,c.revision_id,c.code)=(b.catalog_id,b.revision_id,b.curve_code) AND v.state='published'
     AND(v.clock_catalog_id,v.clock_revision_id,v.clock_code)=(c.reference_catalog_id,c.reference_revision_id,p.clock_code)))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='A peak or off-peak block is bound to a published TOU product version on its delivery profile''s clock.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.shaped_curves s
   JOIN market_data.shaped_curve_blocks x ON(x.catalog_id,x.revision_id,x.shaped_curve_code)=(s.catalog_id,s.revision_id,s.code)
   WHERE(s.catalog_id,s.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
    SELECT 1 FROM market_data.curves c JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    JOIN market_data.interval_series r ON(r.catalog_id,r.id)=(s.catalog_id,s.shape_series_id)
    WHERE(c.catalog_id,c.revision_id,c.code)=(x.catalog_id,x.revision_id,x.block_curve_code)
     AND(r.reference_catalog_id,r.reference_revision_id,r.clock_code,r.interval_minutes)=(c.reference_catalog_id,c.reference_revision_id,p.clock_code,p.interval_minutes)
     AND CASE s.method WHEN 'additive' THEN r.kind='additive_price_shape' AND r.quote_code=c.quote_code
      ELSE r.kind='multiplicative_price_shape' END))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='A shaped curve''s series is an additive shape in its blocks'' quote or a multiplicative shape, on their reference version, clock and grid.';
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.curve_actual_rules r WHERE(r.catalog_id,r.revision_id)=(NEW.catalog_id,NEW.id) AND(NOT EXISTS(
    SELECT 1 FROM market_data.curve_members m JOIN market_data.contract_intervals i ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(r.catalog_id,r.revision_id,r.curve_code)) OR NOT EXISTS(
    SELECT 1 FROM market_data.curves c JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    JOIN market_data.curve_physical_price_sources g ON(g.catalog_id,g.revision_id,g.curve_code)=(c.catalog_id,c.revision_id,c.code)
    JOIN power_data.sources s ON(s.id,s.catalog_id,s.reference_catalog_id,s.reference_revision_id)=(g.source_id,g.catalog_id,g.reference_catalog_id,g.reference_revision_id)
    WHERE(c.catalog_id,c.revision_id,c.code)=(r.catalog_id,r.revision_id,r.curve_code) AND s.clock_code=p.clock_code AND s.minutes=p.interval_minutes)))
 THEN RAISE EXCEPTION 'interval_curve_definition' USING ERRCODE='23514',
  DETAIL='An actual-due rule binds an interval curve whose collected-price source shares its clock and interval length.';
 END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER validate_interval_curve_definitions BEFORE UPDATE OF state ON market_data.revisions
 FOR EACH ROW WHEN(OLD.state='draft' AND NEW.state='sealed') EXECUTE FUNCTION market_data.validate_interval_curve_definitions();
REVOKE ALL ON FUNCTION market_data.validate_interval_curve_definitions() FROM PUBLIC;

-- Definition rows, immutable once sealed; runtime reads them under the same policies as the other market definition tables.
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['contract_intervals','curve_blocks','shaped_curves','shaped_curve_blocks','curve_actual_rules'] LOOP
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
COMMIT;
