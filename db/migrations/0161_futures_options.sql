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
 local_time time NOT NULL,clock_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,code),
 UNIQUE(catalog_id,revision_id,milestone,futures_product_code),UNIQUE(catalog_id,revision_id,milestone,option_product_code),
 CHECK((milestone='last_trading')=(futures_product_code IS NOT NULL) AND (milestone='option_expiry')=(option_product_code IS NOT NULL)),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,revision_id,futures_product_code) REFERENCES ref_data.futures_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,option_product_code) REFERENCES ref_data.futures_option_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code)
);

-- The instant of a product's milestone on a local date: the configured time on the configured clock. The local time
-- must exist and be unambiguous on that date.
CREATE FUNCTION ref_data.close_instant(catalog uuid,revision uuid,selected_milestone text,product text,day date) RETURNS timestamptz
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE t ref_data.trading_close_times;c ref_data.clock_rules;local_at timestamp;at timestamptz;
BEGIN
 SELECT * INTO t FROM ref_data.trading_close_times WHERE(catalog_id,revision_id,milestone)=(catalog,revision,selected_milestone)
  AND product=CASE selected_milestone WHEN 'last_trading' THEN futures_product_code ELSE option_product_code END;
 IF t.code IS NULL THEN RAISE EXCEPTION 'close_time_setting_missing';END IF;
 SELECT * INTO c FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(catalog,revision,t.clock_code);
 local_at=day+t.local_time;
 IF c.kind='fixed_offset' THEN RETURN (local_at AT TIME ZONE 'UTC')-make_interval(secs=>c.fixed_offset_seconds);END IF;
 at=local_at AT TIME ZONE c.iana_zone;
 IF (at AT TIME ZONE c.iana_zone)<>local_at OR ((at-interval '1 hour') AT TIME ZONE c.iana_zone)=local_at
  OR ((at+interval '1 hour') AT TIME ZONE c.iana_zone)=local_at THEN RAISE EXCEPTION 'close_time_local';END IF;
 RETURN at;
END$$;
REVOKE ALL ON FUNCTION ref_data.close_instant(uuid,uuid,text,text,date) FROM PUBLIC;

-- A known last-trading instant of a product with a configured close equals that close on its business date.
CREATE FUNCTION ref_data.require_configured_close() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE product text;
BEGIN
 IF NEW.milestone<>'last_trading' OR NEW.event_at IS NULL THEN RETURN NEW;END IF;
 SELECT s.product_code INTO product FROM ref_data.futures_series s WHERE(s.catalog_id,s.revision_id,s.code)=(NEW.catalog_id,NEW.revision_id,NEW.series_code);
 IF EXISTS(SELECT 1 FROM ref_data.trading_close_times t WHERE(t.catalog_id,t.revision_id,t.milestone,t.futures_product_code)=(NEW.catalog_id,NEW.revision_id,'last_trading',product))
  AND NEW.event_at<>ref_data.close_instant(NEW.catalog_id,NEW.revision_id,'last_trading',product,NEW.business_date) THEN RAISE EXCEPTION 'close_time_mismatch';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER require_configured_close BEFORE INSERT ON ref_data.futures_milestones FOR EACH ROW EXECUTE FUNCTION ref_data.require_configured_close();

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
COMMIT;
