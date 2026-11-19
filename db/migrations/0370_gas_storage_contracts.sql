-- Commercial gas storage contract (D432): retained entitlement, dated gas days, one idempotent booking
-- and a revisable injection and withdrawal plan (D433 proposals are recorded as plans).
-- A link to a physical facility, measured flows and settlement cash are separate operations.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.gas_storage_contracts(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,
 injection_curve_code ref_data.code NOT NULL,withdrawal_curve_code ref_data.code NOT NULL,quote_code ref_data.code NOT NULL,
 minimum_inventory ref_data.exact_number NOT NULL CHECK(minimum_inventory>=0 AND scale(minimum_inventory)<=18),
 maximum_inventory ref_data.exact_number NOT NULL CHECK(maximum_inventory<1e15 AND scale(maximum_inventory)<=18),
 injection_fuel ref_data.exact_number NOT NULL CHECK(injection_fuel>=0 AND injection_fuel<1 AND scale(injection_fuel)<=12),
 withdrawal_fuel ref_data.exact_number NOT NULL CHECK(withdrawal_fuel>=0 AND withdrawal_fuel<1 AND scale(withdrawal_fuel)<=12),
 ratchet_basis text NOT NULL CHECK(ratchet_basis='gas_day_opening_inventory_band_v1'),
 flow_policy text NOT NULL CHECK(flow_policy='single_direction_per_gas_day_v1'),
 fuel_basis text NOT NULL CHECK(fuel_basis='in_kind_fuel_at_storage_point_v1'),
 opening_inventory ref_data.exact_number NOT NULL CHECK(scale(opening_inventory)<=18),
 terminal_minimum ref_data.exact_number NOT NULL CHECK(scale(terminal_minimum)<=18),
 terminal_maximum ref_data.exact_number NOT NULL CHECK(scale(terminal_maximum)<=18),
 injection_charge ref_data.exact_number NOT NULL CHECK(injection_charge>=0 AND injection_charge<1e12 AND scale(injection_charge)<=18),
 withdrawal_charge ref_data.exact_number NOT NULL CHECK(withdrawal_charge>=0 AND withdrawal_charge<1e12 AND scale(withdrawal_charge)<=18),
 monthly_capacity_charge ref_data.exact_number NOT NULL CHECK(monthly_capacity_charge>=0 AND monthly_capacity_charge<1e12 AND scale(monthly_capacity_charge)<=18),
 term_from date NOT NULL,term_to date NOT NULL,clock_code ref_data.code NOT NULL,
 day_start_minute integer NOT NULL CHECK(day_start_minute BETWEEN 0 AND 1439),payment_rule_id uuid NOT NULL,
 terms jsonb NOT NULL CHECK(jsonb_typeof(terms)='object'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 UNIQUE(catalog_id,instrument_id,definition_revision_id,injection_curve_code,withdrawal_curve_code),
 CHECK(maximum_inventory>minimum_inventory AND opening_inventory BETWEEN minimum_inventory AND maximum_inventory),
 CHECK(terminal_minimum>=minimum_inventory AND terminal_maximum<=maximum_inventory AND terminal_minimum<=terminal_maximum),
 CHECK(term_from<term_to AND term_to-term_from<=1830),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,injection_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,withdrawal_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,payment_rule_id) REFERENCES trading.swap_payment_rules(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- Bands cover the contracted inventory range in order; each states the daily limits of a gas day that opens in it.
CREATE TABLE trading.gas_storage_ratchets(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 31),
 lower_inventory ref_data.exact_number NOT NULL CHECK(scale(lower_inventory)<=18),
 upper_inventory ref_data.exact_number NOT NULL CHECK(scale(upper_inventory)<=18),
 maximum_daily_injection ref_data.exact_number NOT NULL CHECK(maximum_daily_injection>=0 AND maximum_daily_injection<1e15 AND scale(maximum_daily_injection)<=18),
 maximum_daily_withdrawal ref_data.exact_number NOT NULL CHECK(maximum_daily_withdrawal>=0 AND maximum_daily_withdrawal<1e15 AND scale(maximum_daily_withdrawal)<=18),
 PRIMARY KEY(catalog_id,instrument_id,ordinal),CHECK(upper_inventory>lower_inventory),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_storage_contracts(catalog_id,instrument_id)
);
CREATE TABLE trading.gas_storage_days(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 1829),
 gas_day date NOT NULL,starts_at ref_data.instant NOT NULL,ends_at ref_data.instant NOT NULL,payment_at ref_data.instant NOT NULL,
 definition_revision_id uuid NOT NULL,injection_curve_code ref_data.code NOT NULL,injection_contract_id uuid NOT NULL,
 withdrawal_curve_code ref_data.code NOT NULL,withdrawal_contract_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,ordinal),UNIQUE(catalog_id,instrument_id,gas_day),
 CHECK(ends_at-starts_at IN(interval '23 hours',interval '24 hours',interval '25 hours') AND payment_at>=ends_at),
 FOREIGN KEY(catalog_id,instrument_id,definition_revision_id,injection_curve_code,withdrawal_curve_code)
  REFERENCES trading.gas_storage_contracts(catalog_id,instrument_id,definition_revision_id,injection_curve_code,withdrawal_curve_code),
 FOREIGN KEY(catalog_id,definition_revision_id,injection_curve_code,injection_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,withdrawal_curve_code,withdrawal_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
-- A held contract is one exclusive right: there is no quantity to scale it by.
CREATE TABLE trading.gas_storage_trade_revisions(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision=1),instrument_id uuid NOT NULL,book_id uuid NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_storage_contracts(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- A plan is the desk's schedule for the whole term. A changed plan is a new revision with its own knowledge time.
CREATE TABLE trading.gas_storage_plans(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),id uuid NOT NULL,operation_id uuid NOT NULL,
 source text NOT NULL CHECK(source IN('supplied','gas_storage_inventory_lattice_search_v1')),
 inventory_step ref_data.exact_number CHECK(inventory_step>0 AND scale(inventory_step)<=18),
 flows jsonb NOT NULL CHECK(jsonb_typeof(flows)='array'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,revision),UNIQUE(catalog_id,id),CHECK((source='supplied')=(inventory_step IS NULL)),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_storage_contracts(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_storage_plan_days(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,revision integer NOT NULL,ordinal integer NOT NULL,
 injection ref_data.exact_number NOT NULL CHECK(injection>=0 AND injection<1e15 AND scale(injection)<=18),
 withdrawal ref_data.exact_number NOT NULL CHECK(withdrawal>=0 AND withdrawal<1e15 AND scale(withdrawal)<=18),
 PRIMARY KEY(catalog_id,instrument_id,revision,ordinal),CHECK(injection=0 OR withdrawal=0),
 FOREIGN KEY(catalog_id,instrument_id,revision) REFERENCES trading.gas_storage_plans(catalog_id,instrument_id,revision),
 FOREIGN KEY(catalog_id,instrument_id,ordinal) REFERENCES trading.gas_storage_days(catalog_id,instrument_id,ordinal)
);
CREATE TABLE trading.gas_storage_requests(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,
 kind text NOT NULL CHECK(kind IN('booking','plan')),command jsonb NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,operation_id),FOREIGN KEY(catalog_id) REFERENCES market_data.catalogs(id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_storage_contracts','gas_storage_ratchets','gas_storage_days','gas_storage_trade_revisions','gas_storage_plans','gas_storage_plan_days','gas_storage_requests'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_content ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE FUNCTION security.gas_storage_scope(catalog uuid,writing boolean) RETURNS uuid
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR(writing AND(security.trading_access(catalog,'BookTrade') IS NOT TRUE OR security.current_write_context() IS NOT TRUE))
  OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND(writing OR risk.worker_read() IS NOT TRUE))
 THEN RAISE EXCEPTION 'gas_storage_denied' USING ERRCODE='42501';END IF;
 SELECT q.actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF actor IS NULL THEN RAISE EXCEPTION 'gas_storage_denied' USING ERRCODE='42501';END IF;
 RETURN actor;
END$$;
CREATE FUNCTION security.gas_storage_request_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;result jsonb;BEGIN
 actor=security.gas_storage_scope(catalog,false);
 SELECT r.outcome INTO result FROM trading.gas_storage_requests r WHERE(r.catalog_id,r.actor_id,r.operation_id)=(catalog,actor,operation);
 PERFORM security.gas_storage_scope(catalog,false);RETURN result;
END$$;
CREATE FUNCTION security.gas_storage_request_replay(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;r trading.gas_storage_requests;BEGIN
 actor=security.gas_storage_scope(catalog,false);
 SELECT * INTO r FROM trading.gas_storage_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,(body->>'operationId')::uuid);
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF r.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_storage_request_conflict';END IF;
 PERFORM security.gas_storage_scope(catalog,false);RETURN r.outcome;
END$$;
CREATE FUNCTION trading.gas_storage_readable(h trading.gas_storage_contracts) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT security.market_fact_visible(h.catalog_id,h.definition_revision_id,NULL) IS TRUE
  AND security.reference_access(h.reference_catalog_id,'ReadMarketData') IS TRUE
  AND security.reference_version_visible(h.reference_catalog_id,h.reference_revision_id) IS TRUE
$$;
-- An exact decimal as entered: the text is the number, never a quotient rebuilt from it.
CREATE FUNCTION trading.gas_storage_number(value text) RETURNS numeric LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$BEGIN
 IF value IS NULL OR length(value)>80 OR value !~ '^-?[0-9]+(\.[0-9]{1,18})?$' OR abs(value::numeric)>=1e24 THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 RETURN trim_scale(value::numeric);
END$$;

CREATE FUNCTION security.book_gas_storage(catalog uuid,body jsonb,prepared jsonb,valuation timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;book uuid;definition uuid;rc uuid;rr uuid;t jsonb;payment jsonb;item jsonb;facts jsonb;outcome jsonb;
 prior trading.gas_storage_requests;injection_quote ref_data.quote_specifications;withdrawal_quote ref_data.quote_specifications;
 low numeric;high numeric;opening numeric;terminal_low numeric;terminal_high numeric;injection_fuel numeric;withdrawal_fuel numeric;
 injection_fee numeric;withdrawal_fee numeric;demand numeric;band_low numeric;band_high numeric;preceding numeric;inject_limit numeric;withdraw_limit numeric;
 first_day date;end_day date;day date;starts timestamptz;ends timestamptz;previous_end timestamptz;paid timestamptz;month_paid timestamptz;previous_day date;
 injection_contract uuid;withdrawal_contract uuid;curve text;contract uuid;ordinal integer=0;publication integer;trade uuid;instrument uuid;payment_rule uuid;
 rows jsonb='[]';bands jsonb='[]';
BEGIN
 actor=security.gas_storage_scope(catalog,true);
 IF valuation IS NULL OR NOT isfinite(valuation) OR valuation>clock_timestamp() OR pg_column_size(body)>65536 OR pg_column_size(prepared)>1048576
  OR trading.rec_keys(body,ARRAY['operationId','convention','bookId','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','reason','terms']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE
   WHEN e.key='terms' THEN 'object' WHEN e.key IN('legalEntityRevision','counterpartyRevision') THEN 'number' ELSE 'string' END)
  OR body->>'convention' IS DISTINCT FROM 'gas_storage_trade_v1'
  OR body->>'legalEntityRevision' !~ '^[1-9][0-9]{0,9}$' OR body->>'counterpartyRevision' !~ '^[1-9][0-9]{0,9}$'
  OR nullif(btrim(body->>'reason'),'') IS NULL OR length(body->>'reason')>500
  OR trading.rec_keys(prepared,ARRAY['instrumentId','days']) IS NOT TRUE OR jsonb_typeof(prepared->'instrumentId') IS DISTINCT FROM 'string'
  OR jsonb_typeof(prepared->'days') IS DISTINCT FROM 'array' OR jsonb_array_length(prepared->'days') NOT BETWEEN 1 AND 1830
 THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 operation=(body->>'operationId')::uuid;book=(body->>'bookId')::uuid;instrument=(prepared->>'instrumentId')::uuid;
 IF '00000000-0000-0000-0000-000000000000'::uuid IN(operation,book,instrument) THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.gas_storage_scope(catalog,true);
 SELECT * INTO prior FROM trading.gas_storage_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN IF prior.kind<>'booking' OR prior.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_storage_request_conflict';END IF;RETURN prior.outcome;END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'gas_storage_request_conflict';END IF;

 t=body->'terms';payment=t->'paymentRule';
 IF trading.rec_keys(t,ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId','quantity','injectionCurve','withdrawalCurve',
   'minimumInventory','maximumInventory','ratchets','injectionFuel','withdrawalFuel','ratchetBasis','flowPolicy','fuelBasis',
   'openingInventory','terminalMinimum','terminalMaximum','injectionCharge','withdrawalCharge','monthlyCapacityCharge',
   'termFrom','termTo','clock','dayStartMinute','paymentRule']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(t)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE
   WHEN e.key='paymentRule' THEN 'object' WHEN e.key='ratchets' THEN 'array' WHEN e.key='dayStartMinute' THEN 'number' ELSE 'string' END)
  OR jsonb_array_length(t->'ratchets') NOT BETWEEN 1 AND 32
  OR trading.rec_keys(payment,ARRAY['frequency','rule','days','dayOfMonth','dayBasis','shortMonth','adjustment','calendar','implementation']) IS NOT TRUE
  OR t->>'termFrom' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR t->>'termTo' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  OR t->>'dayStartMinute' !~ '^(0|[1-9][0-9]{0,3})$' OR(t->>'dayStartMinute')::integer>1439
 THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 IF t->>'ratchetBasis' IS DISTINCT FROM 'gas_day_opening_inventory_band_v1' OR t->>'flowPolicy' IS DISTINCT FROM 'single_direction_per_gas_day_v1'
  OR t->>'fuelBasis' IS DISTINCT FROM 'in_kind_fuel_at_storage_point_v1' THEN RAISE EXCEPTION 'gas_storage_policy';END IF;
 low=trading.gas_storage_number(t->>'minimumInventory');high=trading.gas_storage_number(t->>'maximumInventory');
 opening=trading.gas_storage_number(t->>'openingInventory');terminal_low=trading.gas_storage_number(t->>'terminalMinimum');terminal_high=trading.gas_storage_number(t->>'terminalMaximum');
 injection_fuel=trading.gas_storage_number(t->>'injectionFuel');withdrawal_fuel=trading.gas_storage_number(t->>'withdrawalFuel');
 injection_fee=trading.gas_storage_number(t->>'injectionCharge');withdrawal_fee=trading.gas_storage_number(t->>'withdrawalCharge');demand=trading.gas_storage_number(t->>'monthlyCapacityCharge');
 IF low<0 OR high<=low OR high>=1e15 OR opening<low OR opening>high OR terminal_low<low OR terminal_high>high OR terminal_low>terminal_high
  OR injection_fuel<0 OR injection_fuel>=1 OR withdrawal_fuel<0 OR withdrawal_fuel>=1
  OR injection_fee<0 OR injection_fee>=1e12 OR withdrawal_fee<0 OR withdrawal_fee>=1e12 OR demand<0 OR demand>=1e12
 THEN RAISE EXCEPTION 'gas_storage_terms';END IF;
 -- The ratchet bands cover the contracted range exactly, in order, with no gap or overlap.
 preceding=low;
 FOR item IN SELECT value FROM jsonb_array_elements(t->'ratchets') LOOP
  IF trading.rec_keys(item,ARRAY['lower','upper','maximumDailyInjection','maximumDailyWithdrawal']) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(item)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM 'string') THEN RAISE EXCEPTION 'gas_storage_input';END IF;
  band_low=trading.gas_storage_number(item->>'lower');band_high=trading.gas_storage_number(item->>'upper');
  inject_limit=trading.gas_storage_number(item->>'maximumDailyInjection');withdraw_limit=trading.gas_storage_number(item->>'maximumDailyWithdrawal');
  IF band_low<>preceding OR band_high<=band_low OR inject_limit<0 OR inject_limit>=1e15 OR withdraw_limit<0 OR withdraw_limit>=1e15 THEN RAISE EXCEPTION 'gas_storage_ratchets';END IF;
  bands=bands||jsonb_build_array(jsonb_build_object('lower',trim_scale(band_low)::text,'upper',trim_scale(band_high)::text,
   'maximumDailyInjection',trim_scale(inject_limit)::text,'maximumDailyWithdrawal',trim_scale(withdraw_limit)::text));
  preceding=band_high;
 END LOOP;
 IF preceding<>high THEN RAISE EXCEPTION 'gas_storage_ratchets';END IF;
 first_day=(t->>'termFrom')::date;end_day=(t->>'termTo')::date;
 IF first_day>=end_day OR end_day-first_day>1830 OR first_day<DATE '1901-01-01' OR end_day>=DATE '2200-01-01'
  OR jsonb_array_length(prepared->'days')<>end_day-first_day THEN RAISE EXCEPTION 'gas_storage_terms';END IF;

 definition=(t->>'definitionRevisionId')::uuid;rc=(t->>'referenceCatalogId')::uuid;rr=(t->>'referenceRevisionId')::uuid;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=(catalog,definition,rc,rr))
  OR security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE OR security.reference_access(rc,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(rc,rr) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_storage_source' USING ERRCODE='42501';END IF;
 SELECT q.* INTO injection_quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id)=(catalog,definition,t->>'injectionCurve',rc,rr);
 SELECT q.* INTO withdrawal_quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id)=(catalog,definition,t->>'withdrawalCurve',rc,rr);
 -- Both flows are priced outright per unit of the contract's own quantity, under one quote specification.
 IF injection_quote.code IS NULL OR withdrawal_quote.code IS NULL OR injection_quote.code<>withdrawal_quote.code
  OR(injection_quote.kind,injection_quote.multiplier,injection_quote.quantity_code) IS DISTINCT FROM('outright',1::numeric,t->>'quantity')
  OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules c WHERE(c.catalog_id,c.revision_id,c.code)=(rc,rr,t->>'clock'))
 THEN RAISE EXCEPTION 'gas_storage_terms';END IF;

 -- Every gas day of the term keeps its exact bounds and the one contract that delivers it on each curve.
 FOR item IN SELECT value FROM jsonb_array_elements(prepared->'days') LOOP
  IF trading.rec_keys(item,ARRAY['gasDay','startsAt','endsAt','paymentAt','injectionContractId','withdrawalContractId']) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(item)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM 'string')
   OR item->>'gasDay' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN RAISE EXCEPTION 'gas_storage_input';END IF;
  day=(item->>'gasDay')::date;starts=trading.operating_instant(item->>'startsAt');ends=trading.operating_instant(item->>'endsAt');paid=trading.operating_instant(item->>'paymentAt');
  injection_contract=(item->>'injectionContractId')::uuid;withdrawal_contract=(item->>'withdrawalContractId')::uuid;
  IF day<>first_day+ordinal OR starts<=valuation OR starts<>coalesce(previous_end,starts) OR ends-starts NOT IN(interval '23 hours',interval '24 hours',interval '25 hours')
   OR abs(extract(epoch FROM starts-(day::timestamp AT TIME ZONE 'UTC')))>129600 OR paid<ends
   OR date_trunc('month',day)=date_trunc('month',previous_day) AND paid<>month_paid
  THEN RAISE EXCEPTION 'gas_storage_day';END IF;
  FOR curve,contract IN SELECT * FROM(VALUES(t->>'injectionCurve',injection_contract),(t->>'withdrawalCurve',withdrawal_contract))x LOOP
   IF(SELECT array_agg(k.id) FROM market_data.curve_members m JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,curve) AND k.delivery_from<=day AND day<k.delivery_to) IS DISTINCT FROM ARRAY[contract]
   THEN RAISE EXCEPTION 'gas_storage_contract';END IF;
  END LOOP;
  rows=rows||jsonb_build_array(jsonb_build_object('ordinal',ordinal,'gasDay',day,'startsAt',starts,'endsAt',ends,'paymentAt',paid,
   'injectionContractId',injection_contract,'withdrawalContractId',withdrawal_contract));
  previous_day=day;previous_end=ends;month_paid=paid;ordinal=ordinal+1;
 END LOOP;

 -- The business fact excludes the request identity and reason: the same contract in the same book for the same parties is one trade.
 facts=jsonb_build_object('bookId',book,'legalEntityId',(body->>'legalEntityId')::uuid,'legalEntityRevision',(body->>'legalEntityRevision')::integer,
  'counterpartyId',(body->>'counterpartyId')::uuid,'counterpartyRevision',(body->>'counterpartyRevision')::integer,
  'terms',trading.operating_canonical_value(t-'ratchets'),'ratchets',bands,'days',rows);
 SELECT jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'revision',r.revision,'instrumentId',r.instrument_id,'changed',false)
  INTO outcome FROM trading.gas_storage_trade_revisions r JOIN trading.gas_storage_contracts h USING(catalog_id,instrument_id)
  WHERE r.catalog_id=catalog AND h.terms=facts;
 IF FOUND THEN
  INSERT INTO trading.gas_storage_requests VALUES(catalog,actor,operation,'booking',body,outcome);
  PERFORM security.gas_storage_scope(catalog,true);RETURN outcome;
 END IF;
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date,
  'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision'),valuation);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=valuation AND p.effective_at<=valuation)
 THEN RAISE EXCEPTION 'gas_storage_book';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());trade=gen_random_uuid();
 payment_rule=security.retain_swap_payment_rule(catalog,rc,rr,payment,publication,valuation);
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.gas_storage_contracts VALUES(catalog,instrument,definition,rc,rr,t->>'quantity',injection_quote.currency_code,
  t->>'injectionCurve',t->>'withdrawalCurve',injection_quote.code,low,high,injection_fuel,withdrawal_fuel,t->>'ratchetBasis',t->>'flowPolicy',t->>'fuelBasis',
  opening,terminal_low,terminal_high,injection_fee,withdrawal_fee,demand,first_day,end_day,t->>'clock',(t->>'dayStartMinute')::integer,payment_rule,facts,publication,valuation);
 INSERT INTO trading.gas_storage_ratchets SELECT catalog,instrument,(x.n-1)::integer,(x.value->>'lower')::numeric,(x.value->>'upper')::numeric,
  (x.value->>'maximumDailyInjection')::numeric,(x.value->>'maximumDailyWithdrawal')::numeric FROM jsonb_array_elements(bands) WITH ORDINALITY x(value,n);
 INSERT INTO trading.gas_storage_days SELECT catalog,instrument,(x->>'ordinal')::integer,(x->>'gasDay')::date,(x->>'startsAt')::timestamptz,(x->>'endsAt')::timestamptz,
  (x->>'paymentAt')::timestamptz,definition,t->>'injectionCurve',(x->>'injectionContractId')::uuid,t->>'withdrawalCurve',(x->>'withdrawalContractId')::uuid
  FROM jsonb_array_elements(rows)x;
 INSERT INTO trading.trades VALUES(catalog,trade,book,operation,actor,body,publication,valuation);
 INSERT INTO trading.gas_storage_trade_revisions VALUES(catalog,trade,1,instrument,book,operation,actor,
  (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation);
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'revision',1,'instrumentId',instrument,'changed',true);
 INSERT INTO trading.gas_storage_requests VALUES(catalog,actor,operation,'booking',body,outcome);
 PERFORM security.gas_storage_scope(catalog,true);
 IF security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE OR security.reference_access(rc,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(rc,rr) IS NOT TRUE THEN RAISE EXCEPTION 'gas_storage_denied' USING ERRCODE='42501';END IF;
 RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range OR null_value_not_allowed
  OR check_violation OR foreign_key_violation THEN RAISE EXCEPTION 'gas_storage_input';
END$$;

-- Records the desk's plan for the whole term, before its first gas day begins. The same flows from the same source at the
-- same instant as the plan in force are the fact already recorded.
-- Feasibility under the ratchets is the managed plan operation's; this operation retains exact, complete, one-way daily flows.
CREATE FUNCTION security.record_gas_storage_plan(catalog uuid,body jsonb,effective timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;trade uuid;instrument uuid;source jsonb;item jsonb;outcome jsonb;prior trading.gas_storage_requests;head trading.gas_storage_plans;
 contract trading.gas_storage_contracts;days integer;ordinal integer=0;injected numeric;taken numeric;step numeric;flows jsonb='[]';publication integer;plan uuid;
BEGIN
 actor=security.gas_storage_scope(catalog,true);
 IF effective IS NULL OR NOT isfinite(effective) OR effective>clock_timestamp() OR pg_column_size(body)>1048576
  OR trading.rec_keys(body,ARRAY['operationId','convention','tradeId','expectedRevision','reason','source','days']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE
   WHEN e.key='days' THEN 'array' WHEN e.key='source' THEN 'object' WHEN e.key='expectedRevision' THEN 'number' ELSE 'string' END)
  OR body->>'convention' IS DISTINCT FROM 'gas_storage_plan_v1' OR body->>'expectedRevision' !~ '^(0|[1-9][0-9]{0,8})$'
  OR nullif(btrim(body->>'reason'),'') IS NULL OR length(body->>'reason')>500 OR jsonb_array_length(body->'days') NOT BETWEEN 1 AND 1830
 THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 source=body->'source';
 IF trading.rec_keys(source,ARRAY['kind','inventoryStep']) IS NOT TRUE OR jsonb_typeof(source->'kind') IS DISTINCT FROM 'string'
  OR source->>'kind' NOT IN('supplied','gas_storage_inventory_lattice_search_v1')
  OR(source->>'kind'='supplied')<>(jsonb_typeof(source->'inventoryStep')='null') OR jsonb_typeof(source->'inventoryStep') NOT IN('null','string')
 THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 IF source->>'kind'<>'supplied' THEN step=trading.gas_storage_number(source->>'inventoryStep');IF step<=0 THEN RAISE EXCEPTION 'gas_storage_input';END IF;END IF;
 operation=(body->>'operationId')::uuid;trade=(body->>'tradeId')::uuid;
 IF '00000000-0000-0000-0000-000000000000'::uuid IN(operation,trade) THEN RAISE EXCEPTION 'gas_storage_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.gas_storage_scope(catalog,true);
 SELECT * INTO prior FROM trading.gas_storage_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN IF prior.kind<>'plan' OR prior.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_storage_request_conflict';END IF;RETURN prior.outcome;END IF;
 SELECT h.* INTO contract FROM trading.gas_storage_trade_revisions r JOIN trading.gas_storage_contracts h USING(catalog_id,instrument_id)
  WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND r.effective_at<=effective;
 IF contract.instrument_id IS NULL OR trading.gas_storage_readable(contract) IS NOT TRUE THEN RAISE EXCEPTION 'gas_storage_unavailable' USING ERRCODE='42501';END IF;
 instrument=contract.instrument_id;
 SELECT count(*) INTO days FROM trading.gas_storage_days d WHERE(d.catalog_id,d.instrument_id)=(catalog,instrument);
 -- A plan is a schedule of flows still to come: once the term has started its days are measured, not planned.
 IF EXISTS(SELECT 1 FROM trading.gas_storage_days d WHERE(d.catalog_id,d.instrument_id,d.ordinal)=(catalog,instrument,0) AND d.starts_at<=effective)
 THEN RAISE EXCEPTION 'gas_storage_started';END IF;
 IF jsonb_array_length(body->'days')<>days THEN RAISE EXCEPTION 'gas_storage_plan';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'days') LOOP
  IF trading.rec_keys(item,ARRAY['gasDay','injection','withdrawal']) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(item)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM 'string')
   OR item->>'gasDay' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN RAISE EXCEPTION 'gas_storage_input';END IF;
  injected=trading.gas_storage_number(item->>'injection');taken=trading.gas_storage_number(item->>'withdrawal');
  IF(item->>'gasDay')::date<>contract.term_from+ordinal OR injected<0 OR taken<0 OR injected>=1e15 OR taken>=1e15 OR(injected>0 AND taken>0)
  THEN RAISE EXCEPTION 'gas_storage_plan';END IF;
  flows=flows||jsonb_build_array(jsonb_build_array(trim_scale(injected)::text,trim_scale(taken)::text));ordinal=ordinal+1;
 END LOOP;
 SELECT * INTO head FROM trading.gas_storage_plans p WHERE(p.catalog_id,p.instrument_id)=(catalog,instrument) ORDER BY p.revision DESC LIMIT 1;
 IF head.id IS NOT NULL AND head.flows=flows AND(head.source,head.inventory_step,head.effective_at) IS NOT DISTINCT FROM(source->>'kind',step,effective) THEN
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'planId',head.id,'revision',head.revision,'changed',false);
 ELSE
  IF coalesce(head.revision,0)<>(body->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_storage_plan_stale';END IF;
  IF head.effective_at>effective THEN RAISE EXCEPTION 'gas_storage_plan_time';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());plan=gen_random_uuid();
  INSERT INTO trading.gas_storage_plans VALUES(catalog,instrument,coalesce(head.revision,0)+1,plan,operation,source->>'kind',step,flows,actor,publication,effective);
  INSERT INTO trading.gas_storage_plan_days SELECT catalog,instrument,coalesce(head.revision,0)+1,(x.n-1)::integer,(x.value->>0)::numeric,(x.value->>1)::numeric
   FROM jsonb_array_elements(flows) WITH ORDINALITY x(value,n);
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'planId',plan,'revision',coalesce(head.revision,0)+1,'changed',true);
 END IF;
 INSERT INTO trading.gas_storage_requests VALUES(catalog,actor,operation,'plan',body,outcome);
 PERFORM security.gas_storage_scope(catalog,true);
 IF trading.gas_storage_readable(contract) IS NOT TRUE THEN RAISE EXCEPTION 'gas_storage_denied' USING ERRCODE='42501';END IF;
 RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range OR null_value_not_allowed
  OR check_violation OR foreign_key_violation THEN RAISE EXCEPTION 'gas_storage_input';
END$$;

CREATE FUNCTION security.gas_storage_position_scope(catalog uuid,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;result jsonb;complete boolean;BEGIN
 PERFORM security.gas_storage_scope(catalog,false);
 IF books IS NULL OR portfolios IS NULL OR cardinality(books)>128 OR cardinality(portfolios)>128
  OR array_position(books,NULL) IS NOT NULL OR array_position(portfolios,NULL) IS NOT NULL
  OR(trades IS NOT NULL AND(cardinality(trades) NOT BETWEEN 1 AND 256 OR array_position(trades,NULL) IS NOT NULL OR(SELECT count(DISTINCT x) FROM unnest(trades)x)<>cardinality(trades)))
 THEN RAISE EXCEPTION 'gas_storage_position_scope';END IF;
 SELECT c.market_context_id INTO context FROM security.request_contexts c JOIN trading.contexts t ON(t.catalog_id,t.context_id)=(c.market_catalog_id,c.market_context_id)
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);
 WITH held AS(SELECT r.* FROM trading.gas_storage_trade_revisions r JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id)
  JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE r.catalog_id=catalog AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
   AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at) AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)
   AND(cardinality(books)=0 OR r.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
   AND(trades IS NULL OR r.trade_id=ANY(trades)))
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',r.trade_id,'binding_id',r.instrument_id,'instrument_id',r.instrument_id,
   'definition_revision_id',h.definition_revision_id,'book_id',r.book_id,'revision',r.revision,'trade_reference',tr.reference_number) ORDER BY tr.reference_number),'[]'),
  coalesce(bool_and(tr.reference_number IS NOT NULL AND trading.gas_storage_readable(h)),true)
 INTO result,complete FROM held r JOIN trading.gas_storage_contracts h USING(catalog_id,instrument_id)
 LEFT JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id);
 IF complete IS NOT TRUE THEN RAISE EXCEPTION 'gas_storage_unavailable' USING ERRCODE='42501';END IF;
 PERFORM security.gas_storage_scope(catalog,false);RETURN result;
END$$;
-- A position carries its contract, ratchets and gas days, and the plan in force: the latest revision known and effective
-- under the selected context, or none.
CREATE FUNCTION security.read_gas_storage_positions(catalog uuid,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE inventory jsonb;result jsonb;BEGIN
 inventory=security.gas_storage_position_scope(catalog,books,portfolios,trades);
 SELECT coalesce(jsonb_agg(jsonb_build_object(
  'head',jsonb_build_object('trade_id',r.trade_id,'revision',r.revision,'instrument_id',r.instrument_id,'binding_id',r.instrument_id,'book_id',r.book_id,'definition_revision_id',h.definition_revision_id,
   'operation_id',r.operation_id,'quantity','1','publication_order',r.publication_order,'effective_at',r.effective_at,
   'legal_entity_id',r.legal_entity_id,'legal_entity_revision',r.legal_entity_revision,'legal_entity',le.name,
   'counterparty_id',r.counterparty_id,'counterparty_revision',r.counterparty_revision,'counterparty',cp.name,
   'trade_reference',tr.reference_number,'instrument_reference',ir.reference_number,'reason',t.request->>'reason',
   'trade_date',b.trade_date,'booker_id',b.booker_id,'booker_name',b.booker_name,'booker_login',b.booker_login,'executed_at',b.executed_at,
   'booking_implementation',b.implementation,'label_captured_at',b.label_captured_at),
  'terms',jsonb_build_object('definition_revision_id',h.definition_revision_id,'reference_catalog_id',h.reference_catalog_id,'reference_revision_id',h.reference_revision_id,
   'quantity_code',h.quantity_code,'unit',u.symbol,'commodity_code',q.commodity_code,'commodity',c.name,'currency_code',h.currency_code,'currency',currency.display_code,
   'injection_curve_code',h.injection_curve_code,'withdrawal_curve_code',h.withdrawal_curve_code,'quote_code',h.quote_code,
   'minimum_inventory',trim_scale(h.minimum_inventory)::text,'maximum_inventory',trim_scale(h.maximum_inventory)::text,
   'injection_fuel',trim_scale(h.injection_fuel)::text,'withdrawal_fuel',trim_scale(h.withdrawal_fuel)::text,
   'ratchet_basis',h.ratchet_basis,'flow_policy',h.flow_policy,'fuel_basis',h.fuel_basis,
   'opening_inventory',trim_scale(h.opening_inventory)::text,'terminal_minimum',trim_scale(h.terminal_minimum)::text,'terminal_maximum',trim_scale(h.terminal_maximum)::text,
   'injection_charge',trim_scale(h.injection_charge)::text,'withdrawal_charge',trim_scale(h.withdrawal_charge)::text,'monthly_capacity_charge',trim_scale(h.monthly_capacity_charge)::text,
   'term_from',h.term_from,'term_to',h.term_to,'clock_code',h.clock_code,'day_start_minute',h.day_start_minute,
   'payment_rule',to_jsonb(pay)-'catalog_id'-'publication_order'-'effective_at',
   'projection_curves',(SELECT jsonb_agg(DISTINCT x) FROM unnest(ARRAY[h.injection_curve_code::text,h.withdrawal_curve_code::text])x)),
  'ratchets',(SELECT jsonb_agg(jsonb_build_object('lower',trim_scale(b.lower_inventory)::text,'upper',trim_scale(b.upper_inventory)::text,
    'maximum_daily_injection',trim_scale(b.maximum_daily_injection)::text,'maximum_daily_withdrawal',trim_scale(b.maximum_daily_withdrawal)::text) ORDER BY b.ordinal)
   FROM trading.gas_storage_ratchets b WHERE(b.catalog_id,b.instrument_id)=(h.catalog_id,h.instrument_id)),
  'days',(SELECT jsonb_agg(jsonb_build_object('ordinal',d.ordinal,'gas_day',d.gas_day,'starts_at',d.starts_at,'ends_at',d.ends_at,'payment_at',d.payment_at,
    'injection_contract_id',d.injection_contract_id,'withdrawal_contract_id',d.withdrawal_contract_id) ORDER BY d.ordinal)
   FROM trading.gas_storage_days d WHERE(d.catalog_id,d.instrument_id)=(h.catalog_id,h.instrument_id)),
  'plan',(SELECT jsonb_build_object('plan_id',p.id,'revision',p.revision,'source',p.source,'inventory_step',trim_scale(p.inventory_step)::text,
    'effective_at',p.effective_at,'flows',p.flows)
   FROM trading.gas_storage_plans p WHERE(p.catalog_id,p.instrument_id)=(h.catalog_id,h.instrument_id)
    AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at) ORDER BY p.revision DESC LIMIT 1)) ORDER BY tr.reference_number),'[]')
 INTO result FROM trading.gas_storage_trade_revisions r JOIN trading.gas_storage_contracts h USING(catalog_id,instrument_id)
 JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
 JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
 JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
 JOIN trading.booking_facts b ON(b.catalog_id,b.trade_id)=(r.catalog_id,r.trade_id)
 JOIN trading.legal_entities le ON(le.catalog_id,le.id,le.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
 JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
 JOIN trading.swap_payment_rules pay ON(pay.catalog_id,pay.id)=(h.catalog_id,h.payment_rule_id)
 JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=(h.reference_catalog_id,h.reference_revision_id,h.currency_code)
 JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(h.reference_catalog_id,h.reference_revision_id,h.quantity_code)
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
 JOIN ref_data.commodities c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.commodity_code)
 WHERE r.catalog_id=catalog AND r.trade_id IN(SELECT(value->>'trade_id')::uuid FROM jsonb_array_elements(inventory));
 IF jsonb_array_length(result)<>jsonb_array_length(inventory) THEN RAISE EXCEPTION 'gas_storage_metadata';END IF;
 PERFORM security.gas_storage_position_scope(catalog,books,portfolios,trades);RETURN result;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('security','trading') AND p.proname LIKE '%gas_storage%' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.gas_storage_request_status(uuid,uuid),security.gas_storage_request_replay(uuid,jsonb),
 security.book_gas_storage(uuid,jsonb,jsonb,timestamptz),security.record_gas_storage_plan(uuid,jsonb,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.gas_storage_position_scope(uuid,uuid[],uuid[],uuid[]),security.read_gas_storage_positions(uuid,uuid[],uuid[],uuid[])
 TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
