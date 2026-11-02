-- Listed options on exact futures series: sourced rule versions, option products, explicit option series and the
-- configured local close-of-trading times from which last-trading and option-expiry instants are derived. A
-- futures-option market contract names its exact option series. Facts a source does not state stay unknown.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SET LOCAL search_path=pg_catalog;

-- One versioned, sourced exchange rule. A rule that names a time of day records its clock, or that the source
-- does not state one; no instant is derived from a rule whose clock is unknown.
CREATE TABLE ref_data.futures_option_rules (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 kind text NOT NULL CHECK(kind IN('exercise','expiry','automatic_exercise','exercise_notice')),
 version integer NOT NULL CHECK(version>0),
 description text NOT NULL CHECK(length(btrim(description)) BETWEEN 1 AND 500),
 clock_state text NOT NULL CHECK(clock_state IN('not_applicable','known','unknown')),
 clock_code ref_data.code,
 source_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,code,kind),
 CHECK((clock_state='known')=(clock_code IS NOT NULL)),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);

-- An option product: one option contract (lot_quantity_code) exercises into one contract of the underlying
-- futures product; multiplier converts one unit of the premium quote into currency per option contract.
ALTER TABLE ref_data.futures_products ADD UNIQUE(catalog_id,revision_id,code,exchange_code);
CREATE TABLE ref_data.futures_option_products (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 300),
 definition_state text NOT NULL CHECK(definition_state IN('candidate','synthetic')),
 exchange_code ref_data.code NOT NULL,underlying_product_code ref_data.code NOT NULL,
 quote_code ref_data.code NOT NULL,lot_quantity_code ref_data.code NOT NULL,
 multiplier ref_data.exact_number NOT NULL CHECK(multiplier>0),
 exercise_style text NOT NULL CONSTRAINT futures_option_products_exercise_style_check CHECK(exercise_style='american'),
 settlement_method text NOT NULL CHECK(settlement_method='exercise_into_future'),
 premium_style text NOT NULL CHECK(premium_style IN('premium_paid_up_front','futures_style_margin')),
 contrary_instructions_allowed boolean NOT NULL,
 strike_interval ref_data.exact_number NOT NULL CHECK(strike_interval>0),
 strike_interval_source_code ref_data.code NOT NULL,
 exercise_rule_code ref_data.code NOT NULL,exercise_rule_kind text NOT NULL DEFAULT 'exercise' CHECK(exercise_rule_kind='exercise'),
 expiry_rule_code ref_data.code NOT NULL,expiry_rule_kind text NOT NULL DEFAULT 'expiry' CHECK(expiry_rule_kind='expiry'),
 automatic_exercise_rule_code ref_data.code NOT NULL,
 automatic_exercise_rule_kind text NOT NULL DEFAULT 'automatic_exercise' CHECK(automatic_exercise_rule_kind='automatic_exercise'),
 exercise_notice_rule_code ref_data.code NOT NULL,
 exercise_notice_rule_kind text NOT NULL DEFAULT 'exercise_notice' CHECK(exercise_notice_rule_kind='exercise_notice'),
 PRIMARY KEY(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id,underlying_product_code,exchange_code)
  REFERENCES ref_data.futures_products(catalog_id,revision_id,code,exchange_code),
 FOREIGN KEY(catalog_id,revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,lot_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,strike_interval_source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,exercise_rule_code,exercise_rule_kind) REFERENCES ref_data.futures_option_rules(catalog_id,revision_id,code,kind),
 FOREIGN KEY(catalog_id,revision_id,expiry_rule_code,expiry_rule_kind) REFERENCES ref_data.futures_option_rules(catalog_id,revision_id,code,kind),
 FOREIGN KEY(catalog_id,revision_id,automatic_exercise_rule_code,automatic_exercise_rule_kind)
  REFERENCES ref_data.futures_option_rules(catalog_id,revision_id,code,kind),
 FOREIGN KEY(catalog_id,revision_id,exercise_notice_rule_code,exercise_notice_rule_kind)
  REFERENCES ref_data.futures_option_rules(catalog_id,revision_id,code,kind)
);

-- A product's configured local time of day for one milestone, on an explicit clock. Instants are derived from a
-- sourced date and this setting; a product without one has no derived instant (close_time_setting_missing).
CREATE TABLE ref_data.trading_close_times (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 version integer NOT NULL CHECK(version>0),
 milestone text NOT NULL CHECK(milestone IN('last_trading','option_expiry')),
 futures_product_code ref_data.code,option_product_code ref_data.code,
 local_time time NOT NULL CONSTRAINT trading_close_times_local_time_check CHECK(extract(microseconds FROM local_time)::bigint%1000000=0),clock_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,milestone,futures_product_code),UNIQUE(catalog_id,revision_id,milestone,option_product_code),
 CHECK((milestone='last_trading')=(futures_product_code IS NOT NULL) AND (milestone='option_expiry')=(option_product_code IS NOT NULL)),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id,futures_product_code) REFERENCES ref_data.futures_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,option_product_code) REFERENCES ref_data.futures_option_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);

-- A local date and time on a reference clock as an instant. The local time must exist and be unambiguous on that date.
CREATE FUNCTION ref_data.local_instant(catalog uuid,revision uuid,clock text,local_time time,day date) RETURNS timestamptz
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE c ref_data.clock_rules;local_at timestamp=day+local_time;at timestamptz;
BEGIN
 SELECT * INTO c FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(catalog,revision,clock);
 IF c.kind='fixed_offset' THEN RETURN (local_at AT TIME ZONE 'UTC')-make_interval(secs=>c.fixed_offset_seconds);END IF;
 at=local_at AT TIME ZONE c.iana_zone;
 IF (at AT TIME ZONE c.iana_zone)<>local_at OR ((at-interval '1 hour') AT TIME ZONE c.iana_zone)=local_at
  OR ((at+interval '1 hour') AT TIME ZONE c.iana_zone)=local_at THEN RAISE EXCEPTION 'close_time_local';END IF;
 RETURN at;
END$$;

-- The instant of a product's milestone on a local date: its configured time on its configured clock.
CREATE FUNCTION ref_data.close_instant(catalog uuid,revision uuid,selected_milestone text,product text,day date) RETURNS timestamptz
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE t ref_data.trading_close_times;
BEGIN
 SELECT * INTO t FROM ref_data.trading_close_times WHERE(catalog_id,revision_id,milestone)=(catalog,revision,selected_milestone)
  AND product=CASE selected_milestone WHEN 'last_trading' THEN futures_product_code ELSE option_product_code END;
 IF t.code IS NULL THEN RAISE EXCEPTION 'close_time_setting_missing';END IF;
 RETURN ref_data.local_instant(catalog,revision,t.clock_code,t.local_time,day);
END$$;
REVOKE ALL ON FUNCTION ref_data.local_instant(uuid,uuid,text,time,date),ref_data.close_instant(uuid,uuid,text,text,date) FROM PUBLIC;
-- Every role that writes reference rows runs the triggers that derive and check these instants.
GRANT EXECUTE ON FUNCTION ref_data.local_instant(uuid,uuid,text,time,date),ref_data.close_instant(uuid,uuid,text,text,date)
 TO openetrm_runtime,openetrm_security_owner;

-- A known last-trading instant of a product with a configured close is that close on its business date, on its clock.
CREATE FUNCTION ref_data.require_configured_close() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE t ref_data.trading_close_times;
BEGIN
 IF NEW.milestone<>'last_trading' OR NEW.event_at IS NULL THEN RETURN NEW;END IF;
 SELECT c.* INTO t FROM ref_data.futures_series s JOIN ref_data.trading_close_times c
  ON(c.catalog_id,c.revision_id,c.milestone,c.futures_product_code)=(s.catalog_id,s.revision_id,'last_trading',s.product_code)
  WHERE(s.catalog_id,s.revision_id,s.code)=(NEW.catalog_id,NEW.revision_id,NEW.series_code);
 IF t.code IS NULL THEN RETURN NEW;END IF;
 IF NEW.clock_code IS DISTINCT FROM t.clock_code OR
  NEW.event_at<>ref_data.local_instant(NEW.catalog_id,NEW.revision_id,t.clock_code,t.local_time,NEW.business_date) THEN RAISE EXCEPTION 'close_time_mismatch';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_configured_close BEFORE INSERT ON ref_data.futures_milestones FOR EACH ROW EXECUTE FUNCTION ref_data.require_configured_close();

-- A last-trading close configured after its product's milestones must reproduce every known instant, whatever the
-- insertion order. An option series cannot precede its option-expiry close, which its own insert requires.
CREATE FUNCTION ref_data.require_close_reproduces_milestones() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.milestone='last_trading' AND EXISTS(SELECT 1 FROM ref_data.futures_series s JOIN ref_data.futures_milestones m
   ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'last_trading')
   WHERE(s.catalog_id,s.revision_id,s.product_code)=(NEW.catalog_id,NEW.revision_id,NEW.futures_product_code) AND m.event_at IS NOT NULL
   AND (m.clock_code IS DISTINCT FROM NEW.clock_code OR m.event_at<>ref_data.local_instant(NEW.catalog_id,NEW.revision_id,NEW.clock_code,NEW.local_time,m.business_date)))
 THEN RAISE EXCEPTION 'close_time_mismatch';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_reproduced_milestones BEFORE INSERT ON ref_data.trading_close_times FOR EACH ROW EXECUTE FUNCTION ref_data.require_close_reproduces_milestones();

-- One explicit option series; its exact key is the instrument identity. Its expiry instant is the product's configured
-- option-expiry time on the sourced expiry date. No exercise-instruction instant is derived from a rule without a clock.
CREATE TABLE ref_data.futures_option_series (
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,code ref_data.code NOT NULL,
 product_code ref_data.code NOT NULL,underlying_series_code ref_data.code NOT NULL,
 option_right text NOT NULL CHECK(option_right IN('call','put')),
 strike ref_data.exact_number NOT NULL CHECK(strike>0),
 definition_state text NOT NULL CHECK(definition_state IN('candidate','synthetic')),
 last_trading_date date NOT NULL CHECK(isfinite(last_trading_date) AND last_trading_date>=DATE '0001-01-01' AND last_trading_date<DATE '9999-12-31'),
 expiry_at ref_data.instant NOT NULL,
 instruction_cutoff_at ref_data.instant,
 source_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,product_code,underlying_series_code,option_right,strike),
 CHECK(instruction_cutoff_at IS NULL OR instruction_cutoff_at<=expiry_at),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id,product_code) REFERENCES ref_data.futures_option_products(catalog_id,revision_id,code),
 CONSTRAINT futures_option_series_underlying_fkey FOREIGN KEY(catalog_id,revision_id,underlying_series_code)
  REFERENCES ref_data.futures_series(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);

-- The underlying is a series of the product's own futures product; the strike lies on both the futures tick and
-- the strike interval; the option expires at its configured time, before its future. A missing underlying is left to
-- the foreign key.
CREATE FUNCTION ref_data.require_futures_option_series() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE p ref_data.futures_option_products;f ref_data.futures_products;s ref_data.futures_series;m ref_data.futures_milestones;
BEGIN
 SELECT * INTO p FROM ref_data.futures_option_products WHERE(catalog_id,revision_id,code)=(NEW.catalog_id,NEW.revision_id,NEW.product_code);
 SELECT * INTO s FROM ref_data.futures_series WHERE(catalog_id,revision_id,code)=(NEW.catalog_id,NEW.revision_id,NEW.underlying_series_code);
 IF p.code IS NULL OR s.code IS NULL THEN RETURN NEW;END IF;
 SELECT * INTO f FROM ref_data.futures_products WHERE(catalog_id,revision_id,code)=(s.catalog_id,s.revision_id,s.product_code);
 IF f.code<>p.underlying_product_code THEN RAISE EXCEPTION 'option_underlying';END IF;
 IF f.outright_tick_size IS NULL OR NEW.strike%f.outright_tick_size<>0 OR NEW.strike%p.strike_interval<>0 THEN RAISE EXCEPTION 'option_strike';END IF;
 IF NEW.expiry_at<>ref_data.close_instant(NEW.catalog_id,NEW.revision_id,'option_expiry',p.code,NEW.last_trading_date) THEN RAISE EXCEPTION 'close_time_mismatch';END IF;
 SELECT * INTO m FROM ref_data.futures_milestones WHERE(catalog_id,revision_id,series_code,milestone)=(s.catalog_id,s.revision_id,s.code,'last_trading');
 IF m.value_state IS DISTINCT FROM 'known' OR m.event_at IS NULL OR NEW.last_trading_date>=m.business_date OR NEW.expiry_at>=m.event_at
  THEN RAISE EXCEPTION 'option_expiry';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_series BEFORE INSERT ON ref_data.futures_option_series FOR EACH ROW EXECUTE FUNCTION ref_data.require_futures_option_series();

-- The same immutability, row security and grants as ref_data.futures_products.
DO $$DECLARE t text;p record;g record;BEGIN
 FOREACH t IN ARRAY ARRAY['futures_option_rules','futures_option_products','trading_close_times','futures_option_series'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON ref_data.%I FOR EACH ROW EXECUTE FUNCTION ref_data.guard_definition()',t);
  EXECUTE format('ALTER TABLE ref_data.%I ENABLE ROW LEVEL SECURITY, FORCE ROW LEVEL SECURITY',t);
  FOR p IN SELECT polname,polpermissive,polcmd,(SELECT string_agg(quote_ident(r::regrole::text),',') FROM unnest(polroles) r) roles,
   pg_get_expr(polqual,polrelid) qual,pg_get_expr(polwithcheck,polrelid) checked FROM pg_policy WHERE polrelid='ref_data.futures_products'::regclass LOOP
   EXECUTE format('CREATE POLICY %I ON ref_data.%I AS %s FOR %s TO %s%s%s',p.polname,t,CASE WHEN p.polpermissive THEN 'PERMISSIVE' ELSE 'RESTRICTIVE' END,
    CASE p.polcmd WHEN 'r' THEN 'SELECT' WHEN 'a' THEN 'INSERT' WHEN 'w' THEN 'UPDATE' WHEN 'd' THEN 'DELETE' ELSE 'ALL' END,p.roles,
    CASE WHEN p.qual IS NULL THEN '' ELSE format(' USING(%s)',p.qual) END,CASE WHEN p.checked IS NULL THEN '' ELSE format(' WITH CHECK(%s)',p.checked) END);
  END LOOP;
  FOR g IN SELECT a.grantee::regrole::text grantee,a.privilege_type FROM pg_class c,aclexplode(c.relacl) a
   WHERE c.oid='ref_data.futures_products'::regclass AND a.grantee<>c.relowner LOOP
   EXECUTE format('GRANT %s ON ref_data.%I TO %I',g.privilege_type,t,g.grantee);
  END LOOP;
 END LOOP;
END$$;

-- Display-only reference revisions keep option definitions and close configuration, after the futures rows they name.
CREATE OR REPLACE FUNCTION security.reference_display_tables() RETURNS text[]
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT ARRAY['dimensions','currencies','units','commodities','commodity_units','measurement_bases','quantity_specifications',
 'unit_conversions','clock_rules','calendars','calendar_days','location_types','locations','commodity_locations','pipeline_zones','pipeline_delivery_points','gas_delivery_conventions','gas_meter_assignments','crude_grades','crude_delivery_points','exchanges',
 'clearing_houses','exchange_clearing_houses','interest_rate_definitions','price_index_definitions','sources','delivery_profiles',
 'value_specifications','quote_specifications','settlement_targets','futures_products','futures_series',
 'futures_milestones','futures_product_terms','futures_lot_resolutions','futures_basis_settlements','futures_execution_policies','product_evidence',
 'futures_option_rules','futures_option_products','trading_close_times','futures_option_series'];
$$;

-- A futures-option contract names exactly its option series; a future names exactly its futures series.
ALTER TABLE market_data.contracts DROP CONSTRAINT contracts_kind_check,DROP CONSTRAINT contracts_check1,
 ADD COLUMN futures_option_series_code ref_data.code,
 ADD CHECK(kind IN('future','forward','future_option')),
 ADD CONSTRAINT contracts_series_kind_check CHECK((kind='future')=(futures_series_code IS NOT NULL) AND (kind='future_option')=(futures_option_series_code IS NOT NULL)),
 ADD UNIQUE(catalog_id,revision_id,futures_option_series_code),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,futures_option_series_code)
  REFERENCES ref_data.futures_option_series(catalog_id,revision_id,code);

-- 0040's validation, plus: an option contract matches its option series' expiry instant and its underlying's
-- delivery period, and lies only on direct daily-settlement outright curves quoted in its product's premium quote.
CREATE OR REPLACE FUNCTION market_data.validate_revision(cat uuid, rev uuid) RETURNS void
LANGUAGE plpgsql SET search_path = pg_catalog AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM market_data.revisions m JOIN ref_data.revisions r
      ON (r.catalog_id,r.id) = (m.reference_catalog_id,m.reference_revision_id)
      WHERE m.catalog_id=cat AND m.id=rev AND r.state='sealed') THEN
    RAISE EXCEPTION 'Exact reference revision must be sealed' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curves c
      WHERE c.catalog_id=cat AND c.revision_id=rev AND (
        NOT EXISTS (SELECT 1 FROM market_data.curve_members m WHERE
          (m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)) OR
        (SELECT count(*) FROM market_data.curve_inputs i WHERE
          (i.catalog_id,i.revision_id,i.curve_code)=(c.catalog_id,c.revision_id,c.code))
          <> CASE c.rule WHEN 'direct' THEN 0 WHEN 'add' THEN 2 ELSE 3 END OR
        (c.rule='scale_add') <> EXISTS (SELECT 1 FROM market_data.curve_inputs h WHERE
          (h.catalog_id,h.revision_id,h.curve_code)=(c.catalog_id,c.revision_id,c.code) AND h.slot='multiplier'))) THEN
    RAISE EXCEPTION 'Curve members, slots or multiplier configuration are incomplete' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_inputs i
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(i.catalog_id,i.revision_id,i.curve_code)
      JOIN market_data.curves s ON (s.catalog_id,s.revision_id,s.code)=(i.catalog_id,i.revision_id,i.source_curve_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quote_specifications sq ON (sq.catalog_id,sq.revision_id,sq.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
      WHERE i.catalog_id=cat AND i.revision_id=rev AND (
        q.kind <> 'outright' OR c.profile_code <> s.profile_code OR
        (i.slot='spread' AND (sq.kind <> 'basis' OR
          (q.quantity_code,q.currency_code,q.multiplier) <> (sq.quantity_code,sq.currency_code,sq.multiplier))) OR
        (i.slot='base' AND (sq.kind <> 'outright' OR (c.rule='add' AND
          (q.quantity_code,q.currency_code,q.multiplier) <> (sq.quantity_code,sq.currency_code,sq.multiplier)))) OR
        (SELECT count(*) FROM market_data.constituent_mappings m WHERE
          (m.catalog_id,m.revision_id,m.curve_code,m.slot)=(i.catalog_id,i.revision_id,i.curve_code,i.slot)) <>
        (SELECT count(*) FROM market_data.curve_members m WHERE
          (m.catalog_id,m.revision_id,m.curve_code)=(i.catalog_id,i.revision_id,i.curve_code)))) THEN
    RAISE EXCEPTION 'Incompatible quote/profile types or incomplete mappings' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_members a
      JOIN market_data.curve_members b ON (b.catalog_id,b.revision_id,b.contract_id)=(a.catalog_id,a.revision_id,a.contract_id)
        AND a.curve_code < b.curve_code
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
      JOIN market_data.curves d ON (d.catalog_id,d.revision_id,d.code)=(b.catalog_id,b.revision_id,b.curve_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quote_specifications z ON (z.catalog_id,z.revision_id,z.code)=(d.reference_catalog_id,d.reference_revision_id,d.quote_code)
      WHERE a.catalog_id=cat AND a.revision_id=rev AND
        (c.commodity_code,c.location_code,c.profile_code,q.quantity_code,q.currency_code,q.multiplier)
        <> (d.commodity_code,d.location_code,d.profile_code,z.quantity_code,z.currency_code,z.multiplier)) THEN
    RAISE EXCEPTION 'Contract membership cannot change common economic specifications' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.constituent_mappings m
      JOIN market_data.contracts t ON (t.catalog_id,t.revision_id,t.id)=(m.catalog_id,m.revision_id,m.target_contract_id)
      JOIN market_data.contracts s ON (s.catalog_id,s.revision_id,s.id)=(m.catalog_id,m.revision_id,m.source_contract_id)
      JOIN market_data.curve_inputs i ON (i.catalog_id,i.revision_id,i.curve_code,i.slot)=(m.catalog_id,m.revision_id,m.curve_code,m.slot)
      JOIN market_data.curves sc ON (sc.catalog_id,sc.revision_id,sc.code)=(m.catalog_id,m.revision_id,m.source_curve_code)
      WHERE m.catalog_id=cat AND m.revision_id=rev AND (
        (t.delivery_from,t.delivery_to) <> (s.delivery_from,s.delivery_to) OR
        (i.expiry_rule='freeze_final_future' AND (s.kind <> 'future' OR sc.rule <> 'direct')))) THEN
    RAISE EXCEPTION 'Unsupported delivery mapping or frozen-final source' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (WITH RECURSIVE walk AS (
      SELECT curve_code AS root, source_curve_code AS node, ARRAY[curve_code] AS visited,
        source_curve_code=curve_code AS cycle FROM market_data.curve_inputs
        WHERE catalog_id=cat AND revision_id=rev
      UNION ALL
      SELECT w.root,i.source_curve_code,w.visited || w.node,i.source_curve_code=ANY(w.visited || w.node)
        FROM walk w JOIN market_data.curve_inputs i ON i.curve_code=w.node
        WHERE i.catalog_id=cat AND i.revision_id=rev AND NOT w.cycle)
      SELECT 1 FROM walk WHERE cycle) THEN
    RAISE EXCEPTION 'Curve dependency cycle' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_inputs m
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
      JOIN market_data.curve_inputs b ON (b.catalog_id,b.revision_id,b.curve_code)=(m.catalog_id,m.revision_id,m.curve_code) AND b.slot='base'
      JOIN market_data.curves source ON (source.catalog_id,source.revision_id,source.code)=(b.catalog_id,b.revision_id,b.source_curve_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quote_specifications sq ON (sq.catalog_id,sq.revision_id,sq.code)=(source.reference_catalog_id,source.reference_revision_id,source.quote_code)
      LEFT JOIN market_data.series series ON (series.catalog_id,series.revision_id,series.id)=(m.catalog_id,m.revision_id,m.source_series_id)
      LEFT JOIN ref_data.value_specifications v ON (v.catalog_id,v.revision_id,v.code)=
        (c.reference_catalog_id,c.reference_revision_id,COALESCE(m.value_specification_code,series.value_specification_code))
      WHERE m.catalog_id=cat AND m.revision_id=rev AND m.slot='multiplier' AND (
        v.code IS NULL OR (q.currency_code,q.multiplier) <> (sq.currency_code,sq.multiplier) OR
        (v.kind='quantity_ratio' AND (sq.quantity_code,q.quantity_code) <> (v.numerator_quantity_code,v.denominator_quantity_code)) OR
        (v.kind='dimensionless' AND sq.quantity_code <> q.quantity_code) OR
        (m.constant_value IS NOT NULL AND (m.constant_value < v.minimum OR m.constant_value > v.maximum)))) THEN
    RAISE EXCEPTION 'Multiplier value/type is incompatible with curve inputs/output' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_members member
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(member.catalog_id,member.revision_id,member.curve_code)
      WHERE c.catalog_id=cat AND c.revision_id=rev AND c.rule='direct' AND NOT EXISTS (
        SELECT 1 FROM market_data.series s WHERE (s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=
          (c.catalog_id,c.revision_id,member.contract_id,c.quote_code))) THEN
    RAISE EXCEPTION 'Direct constituents require exact price series' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.contracts k
      JOIN ref_data.futures_series s ON (s.catalog_id,s.revision_id,s.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)
      WHERE k.catalog_id=cat AND k.revision_id=rev AND (s.definition_state='placeholder' OR
        (s.delivery_from,s.delivery_to) IS DISTINCT FROM (k.delivery_from,k.delivery_to) OR
        NOT EXISTS (SELECT 1 FROM ref_data.futures_milestones x WHERE
          (x.catalog_id,x.revision_id,x.series_code)=(s.catalog_id,s.revision_id,s.code)
          AND x.milestone='last_trading' AND x.value_state='known' AND x.event_at=k.expiry_at))) THEN
    RAISE EXCEPTION 'Futures constituent must match a specified dated series' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.contracts k
      JOIN ref_data.futures_option_series o ON (o.catalog_id,o.revision_id,o.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)
      JOIN ref_data.futures_series u ON (u.catalog_id,u.revision_id,u.code)=(o.catalog_id,o.revision_id,o.underlying_series_code)
      WHERE k.catalog_id=cat AND k.revision_id=rev AND (o.expiry_at<>k.expiry_at OR
        (u.delivery_from,u.delivery_to) IS DISTINCT FROM (k.delivery_from,k.delivery_to))) THEN
    RAISE EXCEPTION 'Futures option constituent must match its listed series' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curve_members m
      JOIN market_data.contracts k ON (k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
      JOIN market_data.curves c ON (c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
      JOIN ref_data.settlement_targets t ON (t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quantity_specifications z ON (z.catalog_id,z.revision_id,z.code)=(q.catalog_id,q.revision_id,q.quantity_code)
      JOIN ref_data.delivery_profiles dp ON (dp.catalog_id,dp.revision_id,dp.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
      LEFT JOIN ref_data.futures_series s ON (s.catalog_id,s.revision_id,s.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)
      LEFT JOIN ref_data.futures_products p ON (p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
      LEFT JOIN ref_data.futures_option_series os ON (os.catalog_id,os.revision_id,os.code)=(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)
      LEFT JOIN ref_data.futures_option_products op ON (op.catalog_id,op.revision_id,op.code)=(os.catalog_id,os.revision_id,os.product_code)
      WHERE m.catalog_id=cat AND m.revision_id=rev AND (
        (t.kind='future_series' AND k.kind NOT IN ('future','future_option')) OR
        (k.kind='future_option' AND (t.kind <> 'future_series' OR q.kind <> 'outright' OR c.rule <> 'direct' OR
          c.direct_role IS DISTINCT FROM 'daily_settlement' OR c.quote_code <> op.quote_code)) OR
        (k.kind='future' AND (p.definition_state='placeholder' OR t.kind <> 'future_series' OR q.kind <> CASE WHEN EXISTS(SELECT 1 FROM ref_data.futures_basis_settlements b WHERE(b.catalog_id,b.revision_id,b.series_code)=(s.catalog_id,s.revision_id,s.code)) THEN 'basis' ELSE 'outright' END OR
          NOT EXISTS(SELECT 1 FROM ref_data.futures_product_terms terms WHERE(terms.catalog_id,terms.revision_id,terms.product_code,terms.profile_code)=(p.catalog_id,p.revision_id,p.code,c.profile_code)) OR
          c.rule <> 'direct' OR (c.direct_role IS DISTINCT FROM 'daily_settlement' AND NOT EXISTS(SELECT 1 FROM ref_data.futures_execution_policies policy WHERE(policy.catalog_id,policy.revision_id,policy.series_code,policy.price_role,policy.price_source_code)=(s.catalog_id,s.revision_id,s.code,c.direct_role,c.direct_source_code))) OR
          (p.location_code IS NOT NULL AND p.location_code <> c.location_code) OR
          p.market_clock_code <> dp.clock_code OR p.delivery_calendar_code <> dp.calendar_code OR
          (p.commodity_code,p.quote_currency_code,p.quote_unit_code,p.quote_multiplier,p.outright_tick_size)
          IS DISTINCT FROM (c.commodity_code,q.currency_code,z.unit_code,q.multiplier,q.trading_tick))))) THEN
    RAISE EXCEPTION 'Constituent conflicts with common curve/futures specifications' USING ERRCODE = '23514';
  END IF;
  IF EXISTS (SELECT 1 FROM market_data.curves c
      JOIN ref_data.settlement_targets t ON (t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
      JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quantity_specifications z ON (z.catalog_id,z.revision_id,z.code)=(q.catalog_id,q.revision_id,q.quantity_code)
      JOIN ref_data.price_index_definitions x ON (x.catalog_id,x.revision_id)=(t.catalog_id,t.revision_id)
        AND x.code IN (t.index_a_code,t.index_b_code)
      WHERE c.catalog_id=cat AND c.revision_id=rev AND
        (x.commodity_code,x.currency_code,x.quote_unit_code,x.quote_multiplier)
        <> (c.commodity_code,q.currency_code,z.unit_code,q.multiplier)) THEN
    RAISE EXCEPTION 'Settlement index target has incompatible commodity/quote units' USING ERRCODE = '23514';
  END IF;
END;
$$;

-- Option bookings. A trade holds one signed lot quantity of the exact option series' instrument, shared by every trade
-- in that series as a future's is, and its own premium cash instrument: premium × multiplier × |lots| in the premium
-- quote's currency, paid for a long and received for a short, on the booked payment date. Booking facts, parties and
-- the trader annotation follow futures bookings.
CREATE FUNCTION security.futures_option_contract_id(ref_catalog uuid,ref_revision uuid,series text) RETURNS uuid
 LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT substr(encode(sha256(convert_to('futures_option_v1:'||ref_catalog::text||':'||ref_revision::text||':'||series,'UTF8')),'hex'),1,32)::uuid;
$$;
CREATE TABLE trading.futures_option_references (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,series_code ref_data.code NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 CHECK(instrument_id=security.futures_option_contract_id(reference_catalog_id,reference_revision_id,series_code)),
 FOREIGN KEY(catalog_id,instrument_id,publication_order,effective_at) REFERENCES trading.instruments(catalog_id,id,publication_order,effective_at),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,series_code) REFERENCES ref_data.futures_option_series(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.cash_payment_instruments (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 direction text NOT NULL CHECK(direction IN('pay','receive')),amount ref_data.exact_number NOT NULL CHECK(amount>=0),
 payment_date date NOT NULL CHECK(payment_date BETWEEN DATE '1901-01-01' AND DATE '2199-12-31'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,publication_order,effective_at) REFERENCES trading.instruments(catalog_id,id,publication_order,effective_at),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.futures_option_trade_revisions (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision=1),book_id uuid NOT NULL,
 definition_revision_id uuid NOT NULL,contract_id uuid NOT NULL,
 option_instrument_id uuid NOT NULL,premium_instrument_id uuid NOT NULL CHECK(premium_instrument_id<>option_instrument_id),
 lots ref_data.exact_number NOT NULL CHECK(lots<>0 AND lots=trunc(lots) AND abs(lots)<=100000),
 premium ref_data.exact_number NOT NULL CHECK(premium>=0),premium_style text NOT NULL CHECK(premium_style='premium_paid_up_front'),
 implementation text NOT NULL CHECK(implementation='futures_option_premium_v1'),execution_at ref_data.instant NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 trader_id uuid,trader_revision integer,memo text CHECK(length(memo)<=4000),trade_date date NOT NULL,
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,premium_instrument_id),
 CHECK((trader_id IS NULL)=(trader_revision IS NULL)),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(catalog_id,option_instrument_id) REFERENCES trading.futures_option_references(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,premium_instrument_id) REFERENCES trading.cash_payment_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,trader_id,trader_revision) REFERENCES trading.traders(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- The same immutability, row security and grants as the crude booking tables.
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['futures_option_references','cash_payment_instruments','futures_option_trade_revisions'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY current_authority ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_access(catalog_id,''ReadTrades''))',t);
  EXECUTE format('CREATE POLICY history_scope ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;

-- The first outcome of an option booking operation, for its own actor.
CREATE FUNCTION security.futures_option_operation_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r trading.futures_option_trade_revisions;actor uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No option read access' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF actor IS NULL THEN RAISE EXCEPTION 'No option request context' USING ERRCODE='42501';END IF;
 SELECT * INTO r FROM trading.futures_option_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation) AND actor_id=actor;
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false);END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Option read authority expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'revision',r.revision,
  'optionInstrumentId',r.option_instrument_id,'premiumInstrumentId',r.premium_instrument_id);
END$$;

-- Books one option trade under book_futures' authority, attribution and request-identity rules (D238): only premium
-- paid up front, signed whole lots, a premium on the product's premium tick within its quote, an explicit payment date,
-- and execution before the option's expiry instant.
CREATE FUNCTION security.book_futures_option(catalog uuid,operation uuid,book uuid,revision uuid,series text,lots numeric,premium numeric,
 premium_payment_date date,premium_style text,valuation timestamptz,executed timestamptz,implementation text,trader uuid,trader_revision integer,memo text,booking jsonb)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.trades;request_value jsonb;ordinal integer;definition_from timestamptz;
 contract_row market_data.contracts;series_row ref_data.futures_option_series;product ref_data.futures_option_products;q ref_data.quote_specifications;
 option_instrument uuid;premium_instrument uuid;trade uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'No booking access' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'Booking access expired' USING ERRCODE='42501';END IF;
 IF executed IS NOT NULL OR trader IS NOT NULL OR trader_revision IS NOT NULL OR booking->>'tradeDate' IS NOT NULL THEN RAISE EXCEPTION 'trade_booking_read_only' USING ERRCODE='P0001';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR valuation IS NULL OR NOT isfinite(valuation) OR implementation IS DISTINCT FROM 'futures_option_premium_v1'
 THEN RAISE EXCEPTION 'option_booking_input' USING ERRCODE='P0001';END IF;
 request_value=jsonb_build_object('book',book,'revision',revision,'series',series,'lots',lots,'premium',premium,'premiumPaymentDate',premium_payment_date,
  'premiumStyle',premium_style,'valuation',valuation AT TIME ZONE 'UTC','implementation',implementation,'memo',memo,'booking',booking,'bookingFactsPolicy','server_booking_utc_v1');
 SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request IS DISTINCT FROM request_value OR NOT EXISTS(SELECT 1 FROM trading.futures_option_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation))
  THEN RAISE EXCEPTION 'option_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.futures_option_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR
 EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR EXISTS(SELECT 1 FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) OR
 EXISTS(SELECT 1 FROM trading.crude_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'option_conflict' USING ERRCODE='P0001';END IF;
 IF premium_style='futures_style_margin' THEN RAISE EXCEPTION 'option_unsupported' USING ERRCODE='P0001';END IF;
 IF premium_style IS DISTINCT FROM 'premium_paid_up_front' OR lots IS NULL OR lots=0 OR lots<>trunc(lots) OR abs(lots)>100000 OR
  premium IS NULL OR premium<0 OR premium_payment_date IS NULL OR premium_payment_date NOT BETWEEN DATE '1901-01-01' AND DATE '2199-12-31'
 THEN RAISE EXCEPTION 'option_booking_input' USING ERRCODE='P0001';END IF;
 -- The exact listed option of this market revision, in the reference revision the market pins.
 SELECT k.* INTO contract_row FROM market_data.contracts k JOIN market_data.revisions m ON(m.catalog_id,m.id)=(k.catalog_id,k.revision_id)
  WHERE(k.catalog_id,k.revision_id,k.kind,k.futures_option_series_code)=(catalog,revision,'future_option',series)
  AND(k.reference_catalog_id,k.reference_revision_id)=(m.reference_catalog_id,m.reference_revision_id) AND m.state='sealed';
 SELECT * INTO series_row FROM ref_data.futures_option_series WHERE(catalog_id,revision_id,code)=(contract_row.reference_catalog_id,contract_row.reference_revision_id,series);
 SELECT * INTO product FROM ref_data.futures_option_products WHERE(catalog_id,revision_id,code)=(series_row.catalog_id,series_row.revision_id,series_row.product_code);
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(product.catalog_id,product.revision_id,product.quote_code);
 -- Read under the booker's own market and reference authority, as crude bookings are: a series the booker cannot see is
 -- refused exactly as one that does not exist.
 IF contract_row.id IS NULL OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.market_fact_visible(catalog,revision,NULL) OR
  NOT security.reference_access(contract_row.reference_catalog_id,'ReadMarketData') OR
  NOT security.reference_version_visible(contract_row.reference_catalog_id,contract_row.reference_revision_id) THEN RAISE EXCEPTION 'option_definition' USING ERRCODE='P0001';END IF;
 IF series_row.code IS NULL OR product.code IS NULL OR q.code IS NULL OR contract_row.expiry_at<>series_row.expiry_at
 THEN RAISE EXCEPTION 'option_definition' USING ERRCODE='P0001';END IF;
 IF product.premium_style='futures_style_margin' THEN RAISE EXCEPTION 'option_unsupported' USING ERRCODE='P0001';END IF;
 IF product.premium_style<>premium_style OR valuation>=series_row.expiry_at OR premium<q.minimum OR premium>q.maximum OR mod(premium,q.trading_tick)<>0
 THEN RAISE EXCEPTION 'option_booking_input' USING ERRCODE='P0001';END IF;
 SELECT(effective_from::timestamp AT TIME ZONE 'UTC') INTO definition_from FROM market_data.revision_periods
  WHERE(catalog_id,revision_id)=(catalog,revision) AND effective_from<=(valuation AT TIME ZONE 'UTC')::date AND(valuation AT TIME ZONE 'UTC')::date<effective_to;
 IF definition_from IS NULL THEN RAISE EXCEPTION 'option_definition' USING ERRCODE='P0001';END IF;
 executed=valuation;
 PERFORM security.require_trade_annotation(catalog,trader,trader_revision,memo,executed);
 PERFORM security.require_trade_parties(catalog,booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date),executed);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=executed AND p.effective_at<=executed) THEN RAISE EXCEPTION 'option_book' USING ERRCODE='P0001';END IF;
 IF(SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'option_limit' USING ERRCODE='P0001';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,ordinal,pg_current_xact_id());
 option_instrument=security.futures_option_contract_id(series_row.catalog_id,series_row.revision_id,series_row.code);
 IF NOT EXISTS(SELECT 1 FROM trading.futures_option_references WHERE(catalog_id,instrument_id)=(catalog,option_instrument)) THEN
  INSERT INTO trading.instruments VALUES(catalog,option_instrument,ordinal,definition_from);
  INSERT INTO trading.futures_option_references VALUES(catalog,option_instrument,series_row.catalog_id,series_row.revision_id,series_row.code,ordinal,definition_from);
 END IF;
 premium_instrument=gen_random_uuid();trade=gen_random_uuid();
 INSERT INTO trading.instruments VALUES(catalog,premium_instrument,ordinal,executed);
 INSERT INTO trading.cash_payment_instruments VALUES(catalog,premium_instrument,q.catalog_id,q.revision_id,q.currency_code,
  CASE WHEN lots>0 THEN 'pay' ELSE 'receive' END,premium*product.multiplier*abs(lots),premium_payment_date,ordinal,executed);
 booking=booking||jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date);
 INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request_value,ordinal,executed);
 INSERT INTO trading.futures_option_trade_revisions VALUES(catalog,trade,1,book,revision,contract_row.id,option_instrument,premium_instrument,lots,premium,premium_style,
  implementation,executed,operation,c.actor_id,trader,trader_revision,memo,(booking->>'tradeDate')::date,(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,
  (booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer,ordinal,executed);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') OR NOT security.market_access(catalog,'ReadMarketData') OR
  NOT security.reference_access(contract_row.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Booking access expired' USING ERRCODE='42501';END IF;
 RETURN security.futures_option_operation_status(catalog,operation);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('futures_option_contract_id','futures_option_operation_status','book_futures_option') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname<>'futures_option_contract_id' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;

-- The page assistant reads the option catalog under the agent reader's market and trading read authority. It books
-- nothing: booking facts need a human booker, as for futures, and no agent tool needs BookTrade.
DO $$BEGIN
 IF(SELECT array_agg(code||':'||array_to_string(permissions,',') ORDER BY code) FROM assistant.agent_tools())IS DISTINCT FROM
  ARRAY['navigate:','prepare_custom_curve:ReadMarketData','read_current_page:','read_custom_curves:ReadMarketData','read_jobs:ReadJobs',
   'read_market_data:ReadMarketData','read_reference_data:ReadMarketData','read_schedules:ReadMarketData','read_trade_parties:ReadMarketData,ReadTradeParties',
   'read_trades:ReadMarketData,ReadTrades','save_custom_curve_draft:ReadMarketData,WriteMarketDefinitions']
 THEN RAISE EXCEPTION 'futures_options: unexpected agent tools' USING ERRCODE='55000';END IF;
END$$;
CREATE OR REPLACE FUNCTION assistant.agent_tools() RETURNS TABLE(code text,permissions text[]) LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 VALUES('navigate',ARRAY[]::text[]),('read_current_page',ARRAY[]::text[]),('read_market_data',ARRAY['ReadMarketData']),
  ('read_reference_data',ARRAY['ReadMarketData']),('read_schedules',ARRAY['ReadMarketData']),
  ('read_trade_parties',ARRAY['ReadMarketData','ReadTradeParties']),('read_trades',ARRAY['ReadMarketData','ReadTrades']),
  ('futures_option_catalog',ARRAY['ReadMarketData','ReadTrades']),
  ('read_custom_curves',ARRAY['ReadMarketData']),('prepare_custom_curve',ARRAY['ReadMarketData']),('read_jobs',ARRAY['ReadJobs']),
  ('save_custom_curve_draft',ARRAY['ReadMarketData','WriteMarketDefinitions'])
$$;
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM assistant.agent_tools() WHERE 'BookTrade'=ANY(permissions) OR 'AmendTrade'=ANY(permissions))
  OR EXISTS(SELECT 1 FROM security.role_permissions r JOIN security.roles o ON o.id=r.role_id WHERE o.code='agent_reader' AND r.permission IN('BookTrade','AmendTrade'))
 THEN RAISE EXCEPTION 'futures_options: agents cannot book' USING ERRCODE='55000';END IF;
END$$;
COMMIT;
