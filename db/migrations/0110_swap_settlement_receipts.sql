BEGIN;
-- Determined balances have trade-owned discount keys, not reusable side IDs.
ALTER TABLE automation.pilot_report_discounts ADD COLUMN settlement_determination_id uuid,
 ADD FOREIGN KEY(catalog_id,settlement_determination_id) REFERENCES settlement_data.swap_determinations(catalog_id,id);
DO $$DECLARE key_name text;BEGIN
 SELECT conname INTO STRICT key_name FROM pg_constraint WHERE conrelid='automation.pilot_report_discounts'::regclass AND contype='c'
  AND pg_get_constraintdef(oid) LIKE '%num_nonnulls(fixed_side_id, first_floating_side_id, floating_side_id, ppa_side_id, scheduled_first_side_id, scheduled_second_side_id)%';
 EXECUTE format('ALTER TABLE automation.pilot_report_discounts DROP CONSTRAINT %I',key_name);
END$$;
ALTER TABLE automation.pilot_report_discounts ADD CHECK(num_nonnulls(fixed_side_id,first_floating_side_id,floating_side_id,ppa_side_id,scheduled_first_side_id,scheduled_second_side_id,settlement_determination_id)=1);
CREATE FUNCTION settlement_data.guard_report_determination() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN
 IF NEW.settlement_determination_id IS NOT NULL AND NOT EXISTS(
  SELECT 1 FROM settlement_data.swap_determinations d JOIN automation.pilot_report_positions p
   ON(p.catalog_id,p.trade_id)=(d.catalog_id,d.trade_id)
  WHERE(d.catalog_id,d.id)=(NEW.catalog_id,NEW.settlement_determination_id)
   AND(p.report_id,p.instrument_id)=(NEW.report_id,NEW.instrument_id)
   AND d.due_at=NEW.payment_at AND NEW.payment_ordinal=0
   AND security.trading_visible(d.catalog_id,d.publication_order,d.effective_at))
 THEN RAISE EXCEPTION 'settlement_report_scope';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION settlement_data.guard_report_determination() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.guard_report_determination() FROM PUBLIC;
CREATE TRIGGER settlement_report_determination BEFORE INSERT ON automation.pilot_report_discounts
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_report_determination();
DO $$DECLARE signature regprocedure='automation.publish_pilot_report_pre_renewal(uuid,jsonb)'::regprocedure;definition text;before text;BEGIN
 definition=pg_get_functiondef(signature);before='x.currency,x.rate_policy,x.interpolation_policy,x.ppa,x.first_floating,x.scheduled_first,x.scheduled_second';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report discount writer';END IF;
 definition=replace(definition,before,before||',x.settlement');
 before='currency text,rate_policy text,interpolation_policy text,ppa uuid,first_floating uuid,scheduled_first uuid,scheduled_second uuid)';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report discount parser';END IF;
 EXECUTE replace(definition,before,'currency text,rate_policy text,interpolation_policy text,ppa uuid,first_floating uuid,scheduled_first uuid,scheduled_second uuid,settlement uuid)');
END$$;
DO $$DECLARE definition text;before text=' UNION SELECT b.catalog_id,b.publication_order,b.effective_at FROM automation.pilot_report_positions p';BEGIN
 definition=pg_get_functiondef('security.pilot_report_sources_visible(uuid,uuid)'::regprocedure);
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'Unexpected report source visibility';END IF;
 EXECUTE replace(definition,before,' UNION SELECT d.catalog_id,v.publication_order,v.effective_at FROM automation.pilot_report_discounts d
 JOIN settlement_data.swap_determinations v ON(v.catalog_id,v.id)=(d.catalog_id,d.settlement_determination_id)
 WHERE(d.catalog_id,d.report_id)=(s.catalog_id,s.id)'||before);
END$$;
COMMIT;
