BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

ALTER TABLE ref_data.futures_option_products
 ADD COLUMN multiplier_basis text NOT NULL DEFAULT 'fixed',
 ALTER COLUMN multiplier DROP NOT NULL,
 ADD CHECK ((multiplier_basis='fixed' AND multiplier IS NOT NULL AND multiplier>0)
         OR (multiplier_basis='underlying_lot' AND multiplier IS NULL));
ALTER TABLE ref_data.futures_option_series ADD COLUMN last_trading_at ref_data.instant,
 ADD CHECK (last_trading_at IS NULL OR last_trading_at<=expiry_at);
ALTER TABLE ref_data.futures_option_rules ADD COLUMN early_exercise_by time,
 ADD CHECK (early_exercise_by IS NULL OR (kind='exercise_notice' AND clock_state='known' AND early_exercise_by<TIME '24:00'));

-- The energy delivered by one peak future is dated. Use its sourced lot in the
-- premium quote's units for the cash payment, position and exercise economics.
CREATE FUNCTION ref_data.futures_option_multiplier(catalog uuid,revision uuid,series text)
 RETURNS numeric LANGUAGE plpgsql STABLE STRICT SET search_path=pg_catalog AS $$
DECLARE result numeric;
BEGIN
 SELECT CASE WHEN p.multiplier_basis='fixed' THEN p.multiplier
             WHEN f.lot_basis='per_delivery_hour' AND l.implementation='published_contract_size_v1'
               AND l.amount=f.contract_size*l.factor AND q.kind='outright'
               AND (q.quantity_code,q.currency_code,q.multiplier)=(l.quantity_code,f.quote_currency_code,f.quote_multiplier)
               AND (u.commodity_code,u.unit_code)=(f.commodity_code,f.quote_unit_code)
             THEN l.amount*f.quote_multiplier END INTO result
 FROM ref_data.futures_option_series s
 JOIN ref_data.futures_option_products p ON (p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
 JOIN ref_data.futures_series us ON (us.catalog_id,us.revision_id,us.code)=(s.catalog_id,s.revision_id,s.underlying_series_code)
 JOIN ref_data.futures_products f ON (f.catalog_id,f.revision_id,f.code)=(us.catalog_id,us.revision_id,us.product_code)
 JOIN ref_data.quote_specifications q ON (q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,p.quote_code)
 JOIN ref_data.quantity_specifications u ON (u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
 LEFT JOIN ref_data.futures_lot_resolutions l ON (l.catalog_id,l.revision_id,l.series_code)=(us.catalog_id,us.revision_id,us.code)
 WHERE (s.catalog_id,s.revision_id,s.code)=(catalog,revision,series);
 IF result IS NULL THEN RAISE EXCEPTION 'option_multiplier' USING ERRCODE='P0001'; END IF;
 RETURN trim_scale(result)::ref_data.exact_number;
END$$;
REVOKE ALL ON FUNCTION ref_data.futures_option_multiplier(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ref_data.futures_option_multiplier(uuid,uuid,text) TO openetrm_runtime,openetrm_risk_worker,openetrm_security_owner;

CREATE FUNCTION ref_data.require_option_multiplier() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE clock_code ref_data.code;
BEGIN
 PERFORM ref_data.futures_option_multiplier(NEW.catalog_id,NEW.revision_id,NEW.code);
 IF NEW.last_trading_at IS NOT NULL THEN
  SELECT c.clock_code INTO STRICT clock_code FROM ref_data.trading_close_times c
  WHERE (c.catalog_id,c.revision_id,c.option_product_code,c.milestone)=(NEW.catalog_id,NEW.revision_id,NEW.product_code,'option_expiry');
  IF NEW.last_trading_at<ref_data.local_instant(NEW.catalog_id,NEW.revision_id,clock_code,'00:00',NEW.last_trading_date)
  THEN RAISE EXCEPTION 'option_trading_date'; END IF;
 END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER require_option_multiplier AFTER INSERT OR UPDATE ON ref_data.futures_option_series
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ref_data.require_option_multiplier();

-- Some venues allow early exercise up to a local daily deadline, and contrary
-- instructions only on expiration day. Assignment is a clearing fact.
CREATE FUNCTION ref_data.require_option_notice(p ref_data.futures_option_products,s ref_data.futures_option_series,action text,at_time timestamptz)
 RETURNS void LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE notice ref_data.futures_option_rules; clock ref_data.clock_rules; day date;
BEGIN
 SELECT * INTO STRICT notice FROM ref_data.futures_option_rules r
 WHERE (r.catalog_id,r.revision_id,r.code)=(p.catalog_id,p.revision_id,p.exercise_notice_rule_code);
 IF notice.early_exercise_by IS NULL OR action='assignment' THEN RETURN; END IF;
 SELECT * INTO STRICT clock FROM ref_data.clock_rules c
 WHERE (c.catalog_id,c.revision_id,c.code)=(notice.catalog_id,notice.revision_id,notice.clock_code);
 day=CASE WHEN clock.kind='fixed_offset' THEN ((at_time AT TIME ZONE 'UTC')+make_interval(secs=>clock.fixed_offset_seconds))::date
          ELSE (at_time AT TIME ZONE clock.iana_zone)::date END;
 IF action='exercise' AND day<s.last_trading_date AND
    at_time>ref_data.local_instant(p.catalog_id,p.revision_id,notice.clock_code,notice.early_exercise_by,day)
 THEN RAISE EXCEPTION 'option_exercise_window'; END IF;
 IF action IN ('abandon_instruction','exercise_instruction') AND day<>s.last_trading_date
 THEN RAISE EXCEPTION 'option_instruction_window'; END IF;
END$$;
REVOKE ALL ON FUNCTION ref_data.require_option_notice(ref_data.futures_option_products,ref_data.futures_option_series,text,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION ref_data.require_option_notice(ref_data.futures_option_products,ref_data.futures_option_series,text,timestamptz) TO openetrm_security_owner;

-- Exchange trading hours and the local peak delivery block may use different
-- clocks. Both calendars must identify those clocks on a delivery-hour future.
DO $$DECLARE definition text;
 anchor text='p.market_clock_code <> dp.clock_code OR p.delivery_calendar_code <> dp.calendar_code';
 replacement text=$predicate$(p.market_clock_code <> dp.clock_code AND NOT (
            p.lot_basis='per_delivery_hour' AND dp.quantity_basis='per_delivery_hour'
            AND EXISTS (SELECT 1 FROM ref_data.calendars tc
              WHERE (tc.catalog_id,tc.revision_id,tc.code,tc.clock_code)=
                    (p.catalog_id,p.revision_id,p.trading_calendar_code,p.market_clock_code))
            AND EXISTS (SELECT 1 FROM ref_data.calendars dc
              WHERE (dc.catalog_id,dc.revision_id,dc.code,dc.clock_code)=
                    (p.catalog_id,p.revision_id,p.delivery_calendar_code,dp.clock_code))))
          OR p.delivery_calendar_code <> dp.calendar_code$predicate$;
BEGIN
 definition=pg_get_functiondef('market_data.validate_revision(uuid,uuid)'::regprocedure);
 IF length(definition)-length(replace(definition,anchor,''))<>length(anchor)
 THEN RAISE EXCEPTION 'future_delivery_validation_changed'; END IF;
 EXECUTE replace(definition,anchor,replacement);
END$$;

-- Keep idempotent booking reconciliation ahead of both admission checks.
DO $$DECLARE definition text; old_cash text='-premium*product.multiplier*lots'; old_end text='valuation>=series_row.expiry_at';
BEGIN
 definition=pg_get_functiondef('security.book_futures_option(uuid,uuid,uuid,uuid,text,numeric,numeric,date,text,timestamptz,timestamptz,text,uuid,integer,text,jsonb)'::regprocedure);
 IF length(definition)-length(replace(definition,old_cash,''))<>length(old_cash)
 OR length(definition)-length(replace(definition,old_end,''))<>length(old_end)
 THEN RAISE EXCEPTION 'option_booking_definition_changed'; END IF;
 definition=replace(definition,old_cash,'-premium*ref_data.futures_option_multiplier(series_row.catalog_id,series_row.revision_id,series_row.code)*lots');
 EXECUTE replace(definition,old_end,'valuation>=coalesce(series_row.last_trading_at,series_row.expiry_at)');
END$$;
DO $$DECLARE signature regprocedure; definition text; anchor text; addition text;
BEGIN
 FOR signature,anchor,addition IN SELECT * FROM (VALUES
  ('lifecycle.option_proposal_base(uuid,jsonb)'::regprocedure,'IF kind IN(''exercise'',''assignment'') THEN',
   'PERFORM ref_data.require_option_notice(product,s,kind,at);'),
  ('security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)'::regprocedure,
   'IF event_at<r.execution_at OR security.futures_option_market_date',
   'PERFORM ref_data.require_option_notice(product,series_row,event_kind,event_at);'),
  ('security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)'::regprocedure,
   'IF event_at<r.execution_at THEN RAISE EXCEPTION ''option_instruction_window''',
   'PERFORM ref_data.require_option_notice(product,series_row,event_kind,event_at);')
 ) v(fn,anchor,addition) LOOP
  definition=pg_get_functiondef(signature);
  IF length(definition)-length(replace(definition,anchor,''))<>length(anchor)
  THEN RAISE EXCEPTION 'option_notice_definition_changed'; END IF;
  EXECUTE replace(definition,anchor,addition||E'\n  '||anchor);
 END LOOP;
END$$;
COMMIT;
