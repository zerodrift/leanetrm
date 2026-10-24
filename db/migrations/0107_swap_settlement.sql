-- Trade-owned financial swap cash. Reusable instruments remain immutable economics.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.swap_trade_revisions ADD UNIQUE(catalog_id,trade_id,revision,binding_id);
CREATE TABLE settlement_data.swap_occurrences (
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,origin_revision integer NOT NULL,origin_binding_id uuid NOT NULL,
 membership_ordinal integer NOT NULL CHECK(membership_ordinal IN(0,1)),delivery_from date NOT NULL,delivery_to date NOT NULL,
 contractual_due ref_data.instant NOT NULL,completion_at ref_data.instant NOT NULL,
 completion_policy text NOT NULL CHECK(completion_policy='financial_swap_period_completion_v1'),
 completion_source text NOT NULL CHECK(completion_source IN('date_only_utc_v1','retained_quantity_interval_end_v1')),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),
 due_policy text NOT NULL CHECK(due_policy='swap_due_nominal_v1'),recognition_policy text NOT NULL CHECK(recognition_policy='swap_completed_period_trading_pnl_v1'),
 correction_policy text NOT NULL CHECK(correction_policy='swap_explicit_correction_due_v1'),contract_hash text NOT NULL CHECK(length(contract_hash)=64),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),UNIQUE(catalog_id,trade_id,membership_ordinal,delivery_from,delivery_to),
 CHECK(delivery_from<delivery_to),
 FOREIGN KEY(catalog_id,trade_id,origin_revision,origin_binding_id) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision,binding_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.swap_determinations (
 catalog_id uuid NOT NULL,id uuid NOT NULL,occurrence_id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,
 previous_id uuid,ordinal integer NOT NULL CHECK(ordinal>0),rounded_total ref_data.exact_number NOT NULL,contribution ref_data.exact_number NOT NULL,
 exact_numerator numeric NOT NULL,exact_denominator numeric NOT NULL CHECK(exact_denominator>0),due_at ref_data.instant NOT NULL,
 context_id uuid NOT NULL,input_hash text NOT NULL CHECK(length(input_hash)=64),evidence jsonb NOT NULL CHECK(pg_column_size(evidence)<=1048576),
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),UNIQUE(catalog_id,occurrence_id,id),UNIQUE(catalog_id,occurrence_id,ordinal),UNIQUE(catalog_id,operation_id),
 CHECK(scale(exact_numerator)=0 AND scale(exact_denominator)=0 AND length(exact_numerator::text)<=1234 AND length(exact_denominator::text)<=1234),
 CHECK((ordinal=1 AND previous_id IS NULL) OR(ordinal>1 AND previous_id IS NOT NULL)),
 FOREIGN KEY(catalog_id,occurrence_id,trade_id) REFERENCES settlement_data.swap_occurrences(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,occurrence_id,previous_id) REFERENCES settlement_data.swap_determinations(catalog_id,occurrence_id,id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.swap_commands (
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,sequence integer NOT NULL CHECK(sequence BETWEEN 1 AND 10000),
 actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL CHECK(pg_column_size(request)<=65536),outcome jsonb NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,trade_id,sequence),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_occurrences','swap_determinations','swap_commands'] LOOP
  EXECUTE format('ALTER TABLE settlement_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE settlement_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON settlement_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON settlement_data.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION settlement_data.require_swap_authority(catalog uuid,writing boolean) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR(writing AND security.trading_access(catalog,'AmendTrade') IS NOT TRUE)
 THEN RAISE EXCEPTION 'settlement_denied' USING ERRCODE='42501';END IF;
 IF writing AND NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
END$$;
CREATE FUNCTION settlement_data.swap_command_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old settlement_data.swap_commands;actor uuid;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,true);
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO old FROM settlement_data.swap_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND AND(old.actor_id IS DISTINCT FROM actor OR old.request IS DISTINCT FROM payload) THEN RAISE EXCEPTION 'settlement_operation_conflict';END IF;
 PERFORM settlement_data.require_swap_authority(catalog,true);
 IF old.trade_id IS NOT NULL THEN PERFORM settlement_data.require_swap_reference(catalog,old.trade_id);END IF;
 RETURN old.outcome;
END$$;
CREATE FUNCTION settlement_data.swap_state(catalog uuid,trade uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,false);
 IF NOT EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)) THEN RAISE EXCEPTION 'settlement_trade';END IF;
 SELECT jsonb_build_object(
  'version',coalesce((SELECT max(sequence) FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade) AND security.trading_visible(c.catalog_id,c.publication_order,c.effective_at)),0),
  'occurrences',coalesce((SELECT jsonb_agg(to_jsonb(o) ORDER BY membership_ordinal,delivery_from) FROM settlement_data.swap_occurrences o WHERE(o.catalog_id,o.trade_id)=(catalog,trade) AND security.trading_visible(o.catalog_id,o.publication_order,o.effective_at)),'[]'::jsonb),
  'determinations',coalesce((SELECT jsonb_agg(to_jsonb(d) ORDER BY d.occurrence_id,d.ordinal) FROM settlement_data.swap_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade) AND security.trading_visible(d.catalog_id,d.publication_order,d.effective_at)),'[]'::jsonb)) INTO result;
 PERFORM settlement_data.require_swap_authority(catalog,false);
 PERFORM settlement_data.require_swap_reference(catalog,trade);
 RETURN result;
END$$;
CREATE FUNCTION settlement_data.swap_operation_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;actor uuid;trade uuid;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,false);
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT outcome,c.trade_id INTO result,trade FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id)=(catalog,operation,actor);
 PERFORM settlement_data.require_swap_authority(catalog,false);
 IF trade IS NOT NULL THEN PERFORM settlement_data.require_swap_reference(catalog,trade);END IF;
 RETURN coalesce(result||jsonb_build_object('found',true),jsonb_build_object('found',false));
END$$;
CREATE FUNCTION settlement_data.determine_swap(catalog uuid,operation uuid,trade uuid,expected_revision integer,expected_version integer,
 context uuid,as_of timestamptz,membership integer,period_from date,period_to date,contractual_due timestamptz,
 ref_catalog uuid,ref_revision uuid,currency text,places integer,completion_at timestamptz,completion_policy text,contract_hash text,input_hash text,total numeric,
 numerator numeric,denominator numeric,evidence jsonb,payload jsonb,correction_due timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE old jsonb;head trading.swap_trade_revisions;occ settlement_data.swap_occurrences;prior settlement_data.swap_determinations;
 actor uuid;publication integer;version integer;identity uuid;due timestamptz;delta numeric;outcome jsonb;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,true);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM settlement_data.require_swap_authority(catalog,true);
 old=settlement_data.swap_command_status(catalog,operation,payload);IF old IS NOT NULL THEN RETURN old;END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR as_of IS NULL OR NOT isfinite(as_of)
  OR context IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
    WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date)) OR membership IS NULL OR membership NOT IN(0,1)
  OR period_from IS NULL OR period_to IS NULL OR period_from>=period_to OR total IS NULL OR places IS NULL
  OR NOT EXISTS(SELECT 1 FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id,c.code,c.display_minor_units)=(ref_catalog,ref_revision,currency,places))
  OR security.reference_access(ref_catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'settlement_input';END IF;
 SELECT * INTO head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM expected_revision OR head.effective_at>as_of THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
 IF completion_policy IS DISTINCT FROM 'financial_swap_period_completion_v1' OR completion_at IS DISTINCT FROM
  settlement_data.swap_completion_at(catalog,trade,head.revision,head.instrument_id,membership,period_from,period_to)
 THEN RAISE EXCEPTION 'settlement_completion';END IF;
 SELECT coalesce(max(sequence),0) INTO version FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade);
 IF version IS DISTINCT FROM expected_version THEN RAISE EXCEPTION 'settlement_version_conflict';END IF;
 IF EXISTS(SELECT 1 FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade) AND c.effective_at>as_of)
 THEN RAISE EXCEPTION 'settlement_backdated_unsupported';END IF;
 SELECT * INTO occ FROM settlement_data.swap_occurrences o WHERE(o.catalog_id,o.trade_id,o.membership_ordinal,o.delivery_from,o.delivery_to)=(catalog,trade,membership,period_from,period_to);
 IF occ.id IS NOT NULL AND(occ.contract_hash,occ.reference_catalog_id,occ.reference_revision_id,occ.currency_code,occ.decimal_places,occ.contractual_due,occ.completion_at,occ.completion_policy)
  IS DISTINCT FROM(contract_hash,ref_catalog,ref_revision,currency,places,contractual_due,completion_at,completion_policy) THEN RAISE EXCEPTION 'settlement_continuity';END IF;
 IF occ.id IS NOT NULL THEN
  SELECT * INTO prior FROM settlement_data.swap_determinations d WHERE(d.catalog_id,d.occurrence_id)=(catalog,occ.id) ORDER BY ordinal DESC LIMIT 1;
  IF prior.input_hash=input_hash THEN RAISE EXCEPTION 'settlement_no_change';END IF;
 END IF;
 IF prior.id IS NULL THEN
  IF correction_due IS NOT NULL THEN RAISE EXCEPTION 'settlement_correction_due';END IF;due=contractual_due;delta=total;
 ELSE
  IF correction_due IS NULL THEN RAISE EXCEPTION 'settlement_correction_due';END IF;due=correction_due;delta=total-prior.rounded_total;
 END IF;
 IF due IS NULL OR NOT isfinite(due) OR due>='9999-12-31 00:00:00+00'::timestamptz OR due<='0001-01-01 00:00:00+00'::timestamptz THEN RAISE EXCEPTION 'settlement_correction_due';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 IF occ.id IS NULL THEN
  occ.id=gen_random_uuid();
  INSERT INTO settlement_data.swap_occurrences VALUES(catalog,occ.id,trade,head.revision,head.binding_id,membership,period_from,period_to,contractual_due,completion_at,completion_policy,
   settlement_data.swap_completion_source(catalog,trade,head.revision,head.instrument_id,membership),
   ref_catalog,ref_revision,currency,places,'currency_minor_units_to_even_v1','swap_due_nominal_v1','swap_completed_period_trading_pnl_v1','swap_explicit_correction_due_v1',contract_hash,publication,as_of);
 END IF;
 identity=gen_random_uuid();
 INSERT INTO settlement_data.swap_determinations VALUES(catalog,identity,occ.id,trade,head.revision,prior.id,coalesce(prior.ordinal,0)+1,total,delta,
  numerator,denominator,due,context,input_hash,evidence,operation,actor,publication,as_of);
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'occurrenceId',occ.id,'determinationId',identity,'previousId',prior.id,
  'total',trim_scale(total)::text,'adjustment',trim_scale(delta)::text,'dueAt',due,'version',version+1);
 INSERT INTO settlement_data.swap_commands VALUES(catalog,operation,trade,version+1,actor,payload,outcome,publication,as_of);
 PERFORM settlement_data.require_swap_authority(catalog,true);
 PERFORM settlement_data.require_swap_reference(catalog,trade);
 RETURN outcome;
END$$;
CREATE FUNCTION settlement_data.require_swap_reference(catalog uuid,trade uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM settlement_data.swap_occurrences o WHERE(o.catalog_id,o.trade_id)=(catalog,trade)
  AND(security.current_write_context() OR security.trading_visible(o.catalog_id,o.publication_order,o.effective_at))
  AND security.reference_version_visible(o.reference_catalog_id,o.reference_revision_id) IS NOT TRUE)
 THEN RAISE EXCEPTION 'settlement_denied' USING ERRCODE='42501';END IF;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='settlement_data' AND p.proname IN('require_swap_authority','require_swap_reference','swap_command_status','swap_state','swap_operation_status','determine_swap') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname NOT IN('require_swap_authority','require_swap_reference') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;

-- Resolve payment membership from immutable exact references. Compare absolute
-- quantities by cross multiplication so strip normalization never breaks continuity.
CREATE FUNCTION settlement_data.swap_payment_terms(catalog uuid,instrument uuid,quantity numeric,membership integer,period_from date,period_to date)
 RETURNS TABLE(child uuid,coefficient numeric,numerator numeric,denominator numeric) LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH i AS(SELECT * FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument)),
 members AS(
  SELECT CASE WHEN membership=0 THEN coalesce(i.fixed_side_id,i.first_floating_side_id) ELSE i.floating_side_id END child,
   1::numeric n,1::numeric d FROM i WHERE i.scheduled_first_id IS NULL
  UNION ALL
  SELECT coalesce(p.fixed_side_id,p.floating_side_id),p.quantity_numerator*CASE WHEN membership=1 THEN i.second_quantity_numerator ELSE 1 END,
   p.quantity_denominator*CASE WHEN membership=1 THEN i.second_quantity_denominator ELSE 1 END
  FROM i JOIN trading.swap_scheduled_periods p ON p.catalog_id=i.catalog_id AND p.side_id=CASE WHEN membership=0 THEN i.scheduled_first_id ELSE i.scheduled_second_id END
 )
 SELECT m.child,i.fixed_coefficient*CASE WHEN membership=0 THEN 1 ELSE -1 END,m.n*quantity,m.d FROM members m CROSS JOIN i
 LEFT JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(catalog,m.child)
 LEFT JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(catalog,m.child)
 WHERE(coalesce(f.delivery_from,s.delivery_from),coalesce(f.delivery_to,s.delivery_to))=(period_from,period_to)
$$;
CREATE FUNCTION settlement_data.guard_swap_amendment() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE o settlement_data.swap_occurrences;origin trading.swap_trade_revisions;old record;replacement record;BEGIN
 IF NEW.previous_revision IS NULL THEN RETURN NEW;END IF;
 FOR o IN SELECT * FROM settlement_data.swap_occurrences WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id) LOOP
  SELECT * INTO STRICT origin FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=(o.catalog_id,o.trade_id,o.origin_revision);
  IF(origin.book_id,origin.legal_entity_id,origin.legal_entity_revision,origin.counterparty_id,origin.counterparty_revision)
   IS DISTINCT FROM(NEW.book_id,NEW.legal_entity_id,NEW.legal_entity_revision,NEW.counterparty_id,NEW.counterparty_revision)
  THEN RAISE EXCEPTION 'settlement_amendment_unsupported';END IF;
  SELECT * INTO STRICT old FROM settlement_data.swap_payment_terms(o.catalog_id,origin.instrument_id,origin.quantity,o.membership_ordinal,o.delivery_from,o.delivery_to);
  SELECT * INTO replacement FROM settlement_data.swap_payment_terms(o.catalog_id,NEW.instrument_id,NEW.quantity,o.membership_ordinal,o.delivery_from,o.delivery_to);
  IF replacement.child IS NULL OR replacement.child<>old.child OR replacement.coefficient<>old.coefficient
   OR replacement.numerator*old.denominator<>old.numerator*replacement.denominator
  THEN RAISE EXCEPTION 'settlement_amendment_unsupported';END IF;
 END LOOP;
 RETURN NEW;
END$$;
ALTER FUNCTION settlement_data.swap_payment_terms(uuid,uuid,numeric,integer,date,date) OWNER TO openetrm_security_owner;
ALTER FUNCTION settlement_data.guard_swap_amendment() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.swap_payment_terms(uuid,uuid,numeric,integer,date,date),settlement_data.guard_swap_amendment() FROM PUBLIC;
CREATE TRIGGER determined_swap_continuity BEFORE INSERT ON trading.swap_trade_revisions FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_swap_amendment();

CREATE FUNCTION settlement_data.swap_completion_at(catalog uuid,trade uuid,revision integer,instrument uuid,membership integer,period_from date,period_to date)
 RETURNS timestamptz LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE i trading.swap_instruments;basis text;period integer;child uuid;completed timestamptz;BEGIN
 SELECT * INTO STRICT i FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF i.scheduled_first_id IS NULL THEN RETURN period_to::timestamp AT TIME ZONE 'UTC';END IF;
 SELECT a.basis INTO basis FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision,a.slot)=(catalog,trade,swap_completion_at.revision,membership);
 IF basis IS NULL THEN RAISE EXCEPTION 'settlement_completion';END IF;
 IF basis IN('period_total','calendar_day_rate','contract_total') THEN RETURN period_to::timestamp AT TIME ZONE 'UTC';END IF;
 IF basis<>'selected_interval_power' THEN RAISE EXCEPTION 'settlement_completion';END IF;
 SELECT t.child INTO STRICT child FROM settlement_data.swap_payment_terms(catalog,instrument,1,membership,period_from,period_to)t;
 SELECT p.ordinal INTO STRICT period FROM trading.swap_scheduled_periods p WHERE p.catalog_id=catalog
  AND p.side_id=CASE WHEN membership=0 THEN i.scheduled_first_id ELSE i.scheduled_second_id END AND coalesce(p.fixed_side_id,p.floating_side_id)=child;
 SELECT max(p.ends_at) INTO completed FROM trading.swap_power_quantity_intervals p WHERE(p.catalog_id,p.trade_id,p.revision,p.slot,p.period_ordinal)=(catalog,trade,swap_completion_at.revision,membership,period);
 IF completed IS NULL THEN RAISE EXCEPTION 'settlement_completion';END IF;RETURN completed;
END$$;
CREATE FUNCTION settlement_data.guard_swap_completion() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE o settlement_data.swap_occurrences;BEGIN
 IF NEW.previous_revision IS NULL THEN RETURN NEW;END IF;
 FOR o IN SELECT * FROM settlement_data.swap_occurrences WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id) LOOP
  IF o.completion_at IS DISTINCT FROM settlement_data.swap_completion_at(NEW.catalog_id,NEW.trade_id,NEW.revision,NEW.instrument_id,o.membership_ordinal,o.delivery_from,o.delivery_to)
   OR o.completion_source IS DISTINCT FROM settlement_data.swap_completion_source(NEW.catalog_id,NEW.trade_id,NEW.revision,NEW.instrument_id,o.membership_ordinal)
  THEN RAISE EXCEPTION 'settlement_amendment_unsupported';END IF;
 END LOOP;
 PERFORM settlement_data.require_swap_authority(NEW.catalog_id,true);
 PERFORM settlement_data.require_swap_reference(NEW.catalog_id,NEW.trade_id);
 RETURN NEW;
END$$;
ALTER FUNCTION settlement_data.swap_completion_at(uuid,uuid,integer,uuid,integer,date,date) OWNER TO openetrm_security_owner;
ALTER FUNCTION settlement_data.guard_swap_completion() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.swap_completion_at(uuid,uuid,integer,uuid,integer,date,date),settlement_data.guard_swap_completion() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER determined_swap_completion AFTER INSERT ON trading.swap_trade_revisions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_swap_completion();
CREATE FUNCTION settlement_data.swap_completion_source(catalog uuid,trade uuid,revision integer,instrument uuid,membership integer) RETURNS text
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE scheduled boolean;basis text;BEGIN
 SELECT scheduled_first_id IS NOT NULL INTO STRICT scheduled FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF NOT scheduled THEN RETURN 'date_only_utc_v1';END IF;
 SELECT a.basis INTO basis FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision,a.slot)=(catalog,trade,swap_completion_source.revision,membership);
 IF basis='selected_interval_power' THEN RETURN 'retained_quantity_interval_end_v1';END IF;
 IF basis IN('period_total','calendar_day_rate','contract_total') THEN RETURN 'date_only_utc_v1';END IF;
 RAISE EXCEPTION 'settlement_completion';
END$$;
ALTER FUNCTION settlement_data.swap_completion_source(uuid,uuid,integer,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.swap_completion_source(uuid,uuid,integer,uuid,integer) FROM PUBLIC;
COMMIT;
