-- A stated operator balance supplies two ordinary daily index payments.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE expected record;p pg_proc;
BEGIN
 FOR expected IN SELECT * FROM(VALUES
 ('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','e606d14d36e54e5bfb517de83f125d1164a47720e5e6ba311c679705e809cafe','{openetrm_security_owner=X/openetrm_security_owner}'::text,ARRAY['search_path=pg_catalog','jit=off']::text[],true,'v'),
 ('trading.request_side_calendar_policy(jsonb,integer)','a6809825fc3d219392cd0382d8d4d56bd4a5930533b5b0ae2508dbaa5bf3c7e5','{openetrm_security_owner=X/openetrm_security_owner}'::text,ARRAY['search_path=pg_catalog']::text[],false,'i'),
 ('security.record_gas_cash_payment(uuid,jsonb,timestamptz)','aeb78c416a636c50e1ed43ca269e6c0a6a9c5baf1a9c23677bfda7b49c5e1e59','{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'::text,ARRAY['search_path=pg_catalog','jit=off']::text[],true,'v'),
 ('security.publish_admitted_side_schedules(uuid,uuid,uuid)','5ee5a46ac5ddf81912b7466053fa2f826ca1b36e722756d8c43cf09cb9c9f884','{openetrm_security_owner=X/openetrm_security_owner}'::text,ARRAY['search_path=pg_catalog','jit=off']::text[],true,'v')
 )v(signature,digest,acl,config,definer,volatility) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(expected.signature);
  IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>expected.digest OR
   p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM expected.acl OR
   p.proconfig IS DISTINCT FROM expected.config OR p.prosecdef<>expected.definer OR p.provolatile<>expected.volatility OR
   p.proisstrict OR p.proleakproof OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_imbalance_upgrade: unexpected helper %',expected.signature;END IF;
 END LOOP;
END$guard$;

ALTER TABLE trading.gas_component_terms
 ADD COLUMN balance_side text,ADD COLUMN cashout_profile text,ADD COLUMN statement_basis text,
 ADD COLUMN account_netting text,ADD COLUMN statement_policy text,
 DROP CONSTRAINT gas_component_terms_role_check,
 ADD CONSTRAINT gas_component_terms_role_check CHECK(role IN('physical','consideration','fee','indexed_consideration','charge','imbalance_cashout'));
DO $constraint$
DECLARE definition text;
BEGIN
 SELECT pg_get_constraintdef(oid) INTO STRICT definition FROM pg_constraint
  WHERE conrelid='trading.gas_component_terms'::regclass AND conname='gas_component_terms_check2';
 IF (length(definition)-length(replace(definition,'role = ''indexed_consideration''::text','')))/length('role = ''indexed_consideration''::text')<>1
 THEN RAISE EXCEPTION 'gas_imbalance_upgrade: unexpected entry constraint';END IF;
 ALTER TABLE trading.gas_component_terms DROP CONSTRAINT gas_component_terms_check2;
 EXECUTE 'ALTER TABLE trading.gas_component_terms ADD CONSTRAINT gas_component_terms_check2 '||
  replace(definition,'role = ''indexed_consideration''::text','role IN (''indexed_consideration'',''imbalance_cashout'')');
END$constraint$;
ALTER TABLE trading.gas_component_terms ADD CONSTRAINT gas_cashout_terms CHECK((CASE WHEN role='imbalance_cashout' THEN
 cashout_profile='daily_index_cashout_v1' AND statement_basis IN('daily_imbalance_v1','month_to_date_balance_v1') AND
 account_netting IN('net_accounts_v1','each_account_v1') AND statement_policy IN('final_statement_balance_v1','reported_statement_balance_v1') AND
 direction=CASE balance_side WHEN 'owed_to_us' THEN 1 WHEN 'owed_by_us' THEN -1 END AND stage='delivery' AND share_n=1 AND share_d=1 AND part_kind IS NULL
 ELSE num_nonnulls(balance_side,cashout_profile,statement_basis,account_netting,statement_policy)=0 END) IS TRUE);
CREATE UNIQUE INDEX gas_cashout_direction ON trading.gas_component_terms(catalog_id,instrument_id,source_slot,balance_side) WHERE role='imbalance_cashout';

-- Expand only the explicit pair authoring term. Generated payment entries pass
-- the existing independent index, quantity, date and retained-program admission.
CREATE FUNCTION trading.gas_cashout_entries(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE pairs jsonb=coalesce(nullif(body->'imbalanceCashouts','null'),'[]');pair jsonb;owed boolean;result jsonb='[]';field text;
BEGIN
 IF jsonb_typeof(pairs) IS DISTINCT FROM 'array' OR jsonb_array_length(pairs)>3 OR
  jsonb_typeof(body->'cashSides') IS DISTINCT FROM 'array' OR jsonb_typeof(body->'physicalSides') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF pairs<>'[]' AND EXISTS(SELECT 1 FROM jsonb_array_elements(body->'physicalSides') p WHERE p->'link' IS DISTINCT FROM 'null'::jsonb)
 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 IF (SELECT count(DISTINCT p->>'side') FROM jsonb_array_elements(pairs) p)<>jsonb_array_length(pairs) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 FOR pair IN SELECT value FROM jsonb_array_elements(pairs) LOOP
  IF jsonb_typeof(pair) IS DISTINCT FROM 'object' OR NOT(pair ?& ARRAY['code','profile','side','index','spreadOwedToUs','spreadOwedByUs','paymentDay','statementPolicy','statementBasis','accountNetting','calendarPolicy']) OR
   EXISTS(SELECT 1 FROM jsonb_object_keys(pair) k WHERE k NOT IN('code','profile','side','index','spreadOwedToUs','spreadOwedByUs','paymentDay','statementPolicy','statementBasis','accountNetting','calendarPolicy')) OR
   pair->>'code' !~ '^[a-z][a-z0-9_.-]{0,57}$' OR pair->>'profile' IS DISTINCT FROM 'daily_index_cashout_v1' OR
   (pair->>'statementBasis' IN('daily_imbalance_v1','month_to_date_balance_v1')) IS NOT TRUE OR
   (pair->>'accountNetting' IN('net_accounts_v1','each_account_v1')) IS NOT TRUE OR
   (pair->>'statementPolicy' IN('final_statement_balance_v1','reported_statement_balance_v1')) IS NOT TRUE OR
   jsonb_typeof(pair->'paymentDay') IS DISTINCT FROM 'number' OR (pair->>'paymentDay' ~ '^([1-9]|1[0-9]|2[0-8])$') IS NOT TRUE OR
   (SELECT count(*) FROM jsonb_array_elements(body->'physicalSides') p WHERE p->>'code'=pair->>'side')<>1 OR
   jsonb_typeof(pair->'calendarPolicy') IS DISTINCT FROM 'object'
  THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  FOREACH field IN ARRAY ARRAY['code','side','index','spreadOwedToUs','spreadOwedByUs'] LOOP
   IF jsonb_typeof(pair->field) IS DISTINCT FROM 'string' OR length(btrim(pair->>field))=0 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  END LOOP;
  FOREACH field IN ARRAY ARRAY['spreadOwedToUs','spreadOwedByUs'] LOOP
   IF pair->>field !~ '^-?[0-9]{1,12}([.][0-9]{1,18})?$' OR abs((pair->>field)::numeric)>=1e12 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  END LOOP;
  FOREACH owed IN ARRAY ARRAY[true,false] LOOP
   result=result||jsonb_build_array(jsonb_build_object('code',pair->>'code'||CASE WHEN owed THEN '.to_us' ELSE '.by_us' END,
    'role','indexed_consideration','direction',CASE WHEN owed THEN 'receive' ELSE 'pay' END,'source',pair->'side','stage','delivery',
    'price',NULL,'tariffCode',NULL,'paymentAt',NULL,'share','1','calendarPolicy',pair->'calendarPolicy',
    'indexed',jsonb_build_object('index',pair->'index','observationConvention','named_index_daily_v1','paymentConvention','next_month_day_utc_v1',
     'paymentDay',pair->'paymentDay','spread',pair->CASE WHEN owed THEN 'spreadOwedToUs' ELSE 'spreadOwedByUs' END,'forecastCurve',NULL)));
  END LOOP;
 END LOOP;
 RETURN result;
END$$;
ALTER FUNCTION trading.gas_cashout_entries(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_cashout_entries(jsonb) FROM PUBLIC;

CREATE FUNCTION trading.require_gas_cashout_pair() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE m trading.gas_component_terms;s trading.gas_component_terms;other trading.gas_component_terms;i trading.gas_index_cash_terms;j trading.gas_index_cash_terms;
BEGIN
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(NEW.catalog_id,NEW.instrument_id,NEW.slot);
 IF m.role<>'imbalance_cashout' THEN RETURN NULL;END IF;
 SELECT * INTO s FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,m.source_slot);
 SELECT * INTO other FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,source_slot,role)=(m.catalog_id,m.instrument_id,m.source_slot,'imbalance_cashout') AND balance_side<>m.balance_side;
 SELECT * INTO i FROM trading.gas_index_cash_terms WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,m.slot);
 SELECT * INTO j FROM trading.gas_index_cash_terms WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,other.slot);
 IF s.role IS DISTINCT FROM 'physical' OR s.source_slot IS NOT NULL OR other.slot IS NULL OR
  EXISTS(SELECT 1 FROM trading.gas_component_terms p WHERE(p.catalog_id,p.instrument_id,p.role)=(m.catalog_id,m.instrument_id,'physical') AND p.source_slot IS NOT NULL) OR
  (m.delivery_from,m.delivery_to,m.ratio_n,m.ratio_d) IS DISTINCT FROM(s.delivery_from,s.delivery_to,s.ratio_n,s.ratio_d) OR
  (m.cashout_profile,m.statement_basis,m.account_netting,m.statement_policy) IS DISTINCT FROM(other.cashout_profile,other.statement_basis,other.account_netting,other.statement_policy) OR
  i.observation_convention IS DISTINCT FROM 'named_index_daily_v1' OR i.payment_convention IS DISTINCT FROM 'next_month_day_utc_v1' OR
  (i.index_code,i.payment_day,i.payment_convention,i.observation_convention) IS DISTINCT FROM(j.index_code,j.payment_day,j.payment_convention,j.observation_convention)
 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION trading.require_gas_cashout_pair() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_cashout_pair() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER gas_cashout_pair AFTER INSERT ON trading.gas_component_terms DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.require_gas_cashout_pair();
CREATE CONSTRAINT TRIGGER gas_cashout_pair AFTER INSERT ON trading.gas_index_cash_terms DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.require_gas_cashout_pair();

DO $patch$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$'exerciseRights','chargeSides'))$before$,$after$'exerciseRights','chargeSides','imbalanceCashouts'))$after$),
 ($before$physical=body->'physicalSides';cash=body->'cashSides';$before$,$after$physical=body->'physicalSides';cash=body->'cashSides'||trading.gas_cashout_entries(body);$after$),
 ($before$unit_den numeric;$before$,$after$unit_den numeric;balance_terms jsonb;$after$),
 ($before$  IF NOT(item ?& ARRAY['code','role','direction','source','stage','price','tariffCode','paymentAt'])$before$,$after$  balance_terms=body->'imbalanceCashouts'->((position-jsonb_array_length(physical)-jsonb_array_length(body->'cashSides'))/2);
  IF position<jsonb_array_length(physical)+jsonb_array_length(body->'cashSides') THEN balance_terms=NULL;END IF;
  IF NOT(item ?& ARRAY['code','role','direction','source','stage','price','tariffCode','paymentAt'])$after$),
 ($before$delivery_to,share_n,share_d,part_kind,part_lower_n,part_lower_d,part_upper_n,part_upper_d)$before$,$after$delivery_to,share_n,share_d,part_kind,part_lower_n,part_lower_d,part_upper_n,part_upper_d,balance_side,cashout_profile,statement_basis,account_netting,statement_policy)$after$),
 ($before$VALUES(catalog,instrument,position,item->>'code',item->>'role',sign$before$,$after$VALUES(catalog,instrument,position,item->>'code',CASE WHEN balance_terms IS NULL THEN item->>'role' ELSE 'imbalance_cashout' END,sign$after$),
 ($before$first_day,last_day,sn,sd,part,ln,ld,un,ud);$before$,$after$first_day,last_day,sn,sd,part,ln,ld,un,ud,CASE WHEN balance_terms IS NOT NULL THEN CASE sign WHEN 1 THEN 'owed_to_us' ELSE 'owed_by_us' END END,
   balance_terms->>'profile',balance_terms->>'statementBasis',balance_terms->>'accountNetting',balance_terms->>'statementPolicy');$after$),
 ($before$ PERFORM trading.save_gas_exercise_rights(catalog,instrument,operation,body->'exerciseRights');$before$,$after$ IF EXISTS(SELECT 1 FROM jsonb_array_elements(coalesce(nullif(body->'exerciseRights','null'),'[]')) r
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.role)=(catalog,instrument,'imbalance_cashout')
   AND r->'entries' ? m.code) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 PERFORM trading.save_gas_exercise_rights(catalog,instrument,operation,body->'exerciseRights');$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1
   THEN RAISE EXCEPTION 'gas_imbalance_upgrade: patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

DO $patch$
DECLARE definition text=pg_get_functiondef('trading.request_side_calendar_policy(jsonb,integer)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$  ELSIF command ? 'chargeSides' AND slot_number>=jsonb_array_length(command->'physicalSides')+jsonb_array_length(command->'cashSides') THEN
   selected=command->'chargeSides'->(slot_number-jsonb_array_length(command->'physicalSides')-jsonb_array_length(command->'cashSides'))->'calendarPolicy';
  ELSE selected=command->'cashSides'->(slot_number-jsonb_array_length(command->'physicalSides'))->'calendarPolicy';END IF;$before$,$after$  ELSIF slot_number<jsonb_array_length(command->'physicalSides')+jsonb_array_length(command->'cashSides') THEN
   selected=command->'cashSides'->(slot_number-jsonb_array_length(command->'physicalSides'))->'calendarPolicy';
  ELSIF slot_number<jsonb_array_length(command->'physicalSides')+jsonb_array_length(command->'cashSides')+
   2*jsonb_array_length(coalesce(nullif(command->'imbalanceCashouts','null'),'[]')) THEN
   selected=command->'imbalanceCashouts'->((slot_number-jsonb_array_length(command->'physicalSides')-jsonb_array_length(command->'cashSides'))/2)->'calendarPolicy';
  ELSE selected=command->'chargeSides'->(slot_number-jsonb_array_length(command->'physicalSides')-jsonb_array_length(command->'cashSides')-
   2*jsonb_array_length(coalesce(nullif(command->'imbalanceCashouts','null'),'[]')))->'calendarPolicy';END IF;$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1
   THEN RAISE EXCEPTION 'gas_imbalance_upgrade: patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

DO $patch$
DECLARE definition text=pg_get_functiondef('security.record_gas_cash_payment(uuid,jsonb,timestamptz)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$m.role NOT IN('consideration','fee','indexed_consideration','charge')$before$,$after$m.role NOT IN('consideration','fee','indexed_consideration','charge','imbalance_cashout')$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1
   THEN RAISE EXCEPTION 'gas_imbalance_upgrade: patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

DO $patch$
DECLARE definition text=pg_get_functiondef('security.publish_admitted_side_schedules(uuid,uuid,uuid)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$CASE m.role WHEN 'physical' THEN 'gas' WHEN 'indexed_consideration' THEN 'floating' ELSE 'fixed' END$before$,$after$CASE m.role WHEN 'physical' THEN 'gas' WHEN 'indexed_consideration' THEN 'floating' WHEN 'imbalance_cashout' THEN 'floating' ELSE 'fixed' END$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1
   THEN RAISE EXCEPTION 'gas_imbalance_upgrade: patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

-- One external net settlement owns its immutable directional payment facts.
CREATE TABLE trading.gas_cash_settlements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,
 source_slot integer NOT NULL,month date NOT NULL CHECK(isfinite(month) AND extract(day FROM month)=1),
 paid_at ref_data.instant NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,amount ref_data.exact_number NOT NULL,
 to_us_payment_id uuid,by_us_payment_id uuid,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,instrument_id,source_slot,month),
 CHECK(num_nonnulls(to_us_payment_id,by_us_payment_id)>0),CHECK(to_us_payment_id IS DISTINCT FROM by_us_payment_id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id,source_slot) REFERENCES trading.gas_component_terms(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,to_us_payment_id) REFERENCES trading.gas_cash_payments(catalog_id,id),
 FOREIGN KEY(catalog_id,by_us_payment_id) REFERENCES trading.gas_cash_payments(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code)
);
CREATE TABLE trading.gas_cash_settlement_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,settlement_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 request jsonb NOT NULL CHECK(pg_column_size(request)<=65536),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,settlement_id) REFERENCES trading.gas_cash_settlements(catalog_id,id)
);
DO $tables$
DECLARE name text;
BEGIN
 FOREACH name IN ARRAY ARRAY['gas_cash_settlements','gas_cash_settlement_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$tables$;
CREATE POLICY scoped_read ON trading.gas_cash_settlements FOR SELECT TO openetrm_runtime USING(
 security.trading_access(catalog_id,'ReadTrades') AND security.market_access(catalog_id,'ReadMarketData') AND
 security.trading_visible(catalog_id,publication_order,paid_at) AND EXISTS(
  SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.instrument_id)=(gas_cash_settlements.catalog_id,gas_cash_settlements.trade_id,gas_cash_settlements.instrument_id)));
-- Workers consume the scoped payment/balance readers, never settlement-table rows.
GRANT SELECT ON trading.gas_cash_settlements TO openetrm_runtime;

CREATE FUNCTION trading.require_gas_cash_settlement() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE total numeric;counted integer;
BEGIN
 SELECT sum(p.amount),count(*) INTO total,counted FROM trading.gas_cash_payments p
 WHERE p.catalog_id=NEW.catalog_id AND p.id IN(NEW.to_us_payment_id,NEW.by_us_payment_id);
 IF total IS DISTINCT FROM NEW.amount OR counted<>num_nonnulls(NEW.to_us_payment_id,NEW.by_us_payment_id) OR EXISTS(
  SELECT 1 FROM trading.gas_cash_payments p JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(p.catalog_id,p.instrument_id,p.slot)
  WHERE p.catalog_id=NEW.catalog_id AND p.id IN(NEW.to_us_payment_id,NEW.by_us_payment_id) AND(
   (p.trade_id,p.trade_revision,p.instrument_id,p.paid_at,p.reference_catalog_id,p.reference_revision_id,p.currency_code,m.source_slot,m.role,m.balance_side) IS DISTINCT FROM
   (NEW.trade_id,NEW.trade_revision,NEW.instrument_id,NEW.paid_at,NEW.reference_catalog_id,NEW.reference_revision_id,NEW.currency_code,NEW.source_slot,'imbalance_cashout',CASE WHEN p.id=NEW.to_us_payment_id THEN 'owed_to_us' ELSE 'owed_by_us' END)
   OR EXISTS(SELECT 1 FROM trading.gas_cash_payment_allocations a JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(a.catalog_id,a.schedule_id,a.row_ordinal)
    WHERE(a.catalog_id,a.payment_id)=(p.catalog_id,p.id) AND (r.delivery_from<NEW.month OR r.delivery_to>(NEW.month+interval '1 month')::date))))
 THEN RAISE EXCEPTION 'gas_settlement_amount';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION trading.require_gas_cash_settlement() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_cash_settlement() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER conserved_settlement AFTER INSERT ON trading.gas_cash_settlements DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.require_gas_cash_settlement();
CREATE FUNCTION trading.guard_gas_settled_payment() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.previous_id IS NOT NULL AND EXISTS(SELECT 1 FROM trading.gas_cash_settlements s
  WHERE s.catalog_id=NEW.catalog_id AND NEW.previous_id IN(s.to_us_payment_id,s.by_us_payment_id))
 THEN RAISE EXCEPTION 'gas_settlement_conflict';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION trading.guard_gas_settled_payment() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.guard_gas_settled_payment() FROM PUBLIC;
CREATE TRIGGER settled_payment BEFORE INSERT ON trading.gas_cash_payments FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_settled_payment();

CREATE FUNCTION security.record_gas_cash_settlement(catalog uuid,body jsonb,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE actor uuid;trade uuid;operation uuid;source integer;month_start date;paid timestamptz;net numeric;publication integer;
 currency_payment trading.gas_cash_payments;head trading.gas_trade_revisions;prior trading.gas_cash_settlements;command trading.gas_cash_settlement_commands;
 identity uuid=gen_random_uuid();received uuid;disbursed uuid;owed boolean;entry trading.gas_component_terms;allocations jsonb;
 gross numeric;payment jsonb;result jsonb;old_allocations jsonb;same_fact boolean=true;
BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body ?& ARRAY['operationId','tradeId','expectedRevision','sourceSlot','month','paidAt','currency','amount','toUs','byUs']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('operationId','tradeId','expectedRevision','sourceSlot','month','paidAt','currency','amount','toUs','byUs')) OR
  jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR (body->>'expectedRevision' ~ '^[1-9][0-9]{0,2}$') IS NOT TRUE OR
  jsonb_typeof(body->'sourceSlot') IS DISTINCT FROM 'number' OR (body->>'sourceSlot' ~ '^[0-3]$') IS NOT TRUE OR
  jsonb_typeof(body->'toUs') IS DISTINCT FROM 'array' OR jsonb_typeof(body->'byUs') IS DISTINCT FROM 'array' OR
  jsonb_array_length(body->'toUs')+jsonb_array_length(body->'byUs') NOT BETWEEN 1 AND 62 OR
  EXISTS(SELECT 1 FROM unnest(ARRAY['operationId','tradeId','month','paidAt','currency','amount']) k WHERE jsonb_typeof(body->k) IS DISTINCT FROM 'string') OR
  (body->>'month' ~ '^[0-9]{4}-[0-9]{2}-01$') IS NOT TRUE OR (body->>'amount' ~ '^-?[0-9]{1,24}([.][0-9]{1,18})?$') IS NOT TRUE OR
  (body->>'paidAt' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$') IS NOT TRUE OR
  as_of IS NULL OR NOT isfinite(as_of)
 THEN RAISE EXCEPTION 'gas_settlement_input';END IF;
 trade=(body->>'tradeId')::uuid;operation=(body->>'operationId')::uuid;source=(body->>'sourceSlot')::integer;
 month_start=(body->>'month')::date;paid=(body->>'paidAt')::timestamptz;net=(body->>'amount')::numeric;
 IF operation='00000000-0000-0000-0000-000000000000'::uuid OR paid>as_of THEN RAISE EXCEPTION 'gas_settlement_input';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts
  WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO command FROM trading.gas_cash_settlement_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF command.actor_id<>actor OR command.request IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_settlement_conflict';END IF;
  RETURN command.outcome;
 END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(body->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'gas_settlement_conflict';END IF;
 SELECT * INTO prior FROM trading.gas_cash_settlements WHERE(catalog_id,trade_id,instrument_id,source_slot,month)=(catalog,trade,head.instrument_id,source,month_start);
 IF prior.id IS NOT NULL THEN
  same_fact=(prior.paid_at,prior.currency_code,prior.amount)=(paid,body->>'currency',net);
  identity=prior.id;
 END IF;
 FOREACH owed IN ARRAY ARRAY[true,false] LOOP
  allocations=body->CASE WHEN owed THEN 'toUs' ELSE 'byUs' END;
  SELECT * INTO entry FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,source_slot,role,balance_side)=
   (catalog,head.instrument_id,source,'imbalance_cashout',CASE WHEN owed THEN 'owed_to_us' ELSE 'owed_by_us' END);
  IF entry.slot IS NULL OR month_start>=entry.delivery_to OR(month_start+interval '1 month')::date<=entry.delivery_from THEN RAISE EXCEPTION 'gas_settlement_input';END IF;
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(allocations) a WHERE jsonb_typeof(a) IS DISTINCT FROM 'object' OR
   NOT(a ?& ARRAY['occurrence','amount']) OR a-ARRAY['occurrence','amount']<>'{}' OR
   jsonb_typeof(a->'occurrence') IS DISTINCT FROM 'number' OR(a->>'occurrence' ~ '^[0-9]{1,4}$') IS NOT TRUE OR
   jsonb_typeof(a->'amount') IS DISTINCT FROM 'string' OR(a->>'amount' ~ '^-?[0-9]{1,24}([.][0-9]{1,18})?$') IS NOT TRUE)
  THEN RAISE EXCEPTION 'gas_settlement_input';END IF;
  IF prior.id IS NOT NULL THEN
   SELECT coalesce(jsonb_agg(jsonb_build_object('occurrence',a.occurrence,'amount',trim_scale(a.amount)::text) ORDER BY a.occurrence),'[]') INTO old_allocations
    FROM trading.gas_cash_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,CASE WHEN owed THEN prior.to_us_payment_id ELSE prior.by_us_payment_id END);
   IF old_allocations IS DISTINCT FROM(SELECT coalesce(jsonb_agg(jsonb_build_object('occurrence',(a->>'occurrence')::integer,'amount',trim_scale((a->>'amount')::numeric)::text) ORDER BY (a->>'occurrence')::integer),'[]') FROM jsonb_array_elements(allocations) a)
   THEN same_fact=false;END IF;
   CONTINUE;
  END IF;
  IF allocations='[]' THEN CONTINUE;END IF;
  SELECT sum((a->>'amount')::numeric) INTO gross FROM jsonb_array_elements(allocations) a;
  payment=security.record_gas_cash_payment(catalog,jsonb_build_object('operationId',gen_random_uuid(),'tradeId',trade,'expectedRevision',head.revision,
   'slot',entry.slot,'businessKey','cashout/'||identity::text,'paidAt',body->'paidAt','currency',body->'currency','amount',trim_scale(gross)::text,'allocations',allocations),as_of);
  IF owed THEN received=(payment->>'paymentId')::uuid;ELSE disbursed=(payment->>'paymentId')::uuid;END IF;
 END LOOP;
 IF prior.id IS NOT NULL AND NOT same_fact THEN RAISE EXCEPTION 'gas_settlement_conflict';END IF;
 IF prior.id IS NULL THEN
  IF net IS DISTINCT FROM(SELECT sum(p.amount) FROM trading.gas_cash_payments p WHERE p.catalog_id=catalog AND p.id IN(received,disbursed)) THEN RAISE EXCEPTION 'gas_settlement_amount';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  SELECT * INTO STRICT currency_payment FROM trading.gas_cash_payments WHERE(catalog_id,id)=(catalog,coalesce(received,disbursed));
  INSERT INTO trading.gas_cash_settlements VALUES(catalog,identity,trade,head.revision,head.instrument_id,source,month_start,paid,currency_payment.reference_catalog_id,currency_payment.reference_revision_id,body->>'currency',net,received,disbursed,actor,publication);
 ELSE received=prior.to_us_payment_id;disbursed=prior.by_us_payment_id;END IF;
 result=jsonb_build_object('operationId',operation,'tradeId',trade,'settlementId',identity,'toUsPaymentId',received,'byUsPaymentId',disbursed);
 INSERT INTO trading.gas_cash_settlement_commands VALUES(catalog,operation,identity,actor,body,result);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);
 RETURN result;
END$$;
ALTER FUNCTION security.record_gas_cash_settlement(uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_gas_cash_settlement(uuid,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.record_gas_cash_settlement(uuid,jsonb,timestamptz) TO openetrm_runtime;
-- Balance evidence and cash accounting share the same exact financial boundary.
CREATE FUNCTION security.require_gas_cashout_binding(catalog uuid,trade uuid,binding uuid) RETURNS trading.gas_trade_revisions
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $function$
DECLARE q security.request_contexts;head trading.gas_trade_revisions;job risk.jobs;
BEGIN
 SELECT * INTO q FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF q.backend_pid IS NULL OR q.market_catalog_id IS DISTINCT FROM catalog OR q.market_context_id IS NULL OR q.trading_as_of IS NULL
  OR NOT EXISTS(SELECT 1 FROM security.operation_trading t WHERE(t.catalog_id,t.context_id)=(catalog,q.market_context_id))
 THEN RAISE EXCEPTION 'gas_balance_context' USING ERRCODE='42501';END IF;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_balance_denied' USING ERRCODE='42501';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  IF NOT risk.worker_read() THEN RAISE EXCEPTION 'gas_balance_denied' USING ERRCODE='42501';END IF;
  job=risk.bound_lifecycle_reader();
  IF (catalog,q.market_context_id,q.trading_as_of) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at)
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 SELECT r.* INTO head FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) ORDER BY r.revision DESC LIMIT 1;
 IF head.trade_id IS NULL OR head.instrument_id IS DISTINCT FROM binding
  OR NOT EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,binding)
   AND i.composition AND security.trading_visible(i.catalog_id,i.publication_order,i.effective_at))
  OR security.gas_component_sources_visible(catalog,binding) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_balance_binding' USING ERRCODE='42501';END IF;
 IF job.id IS NOT NULL AND(job.request->'bookIds' @> jsonb_build_array(head.book_id) IS NOT TRUE
  OR NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL AND job.request->'tradeIds' @> jsonb_build_array(trade) IS NOT TRUE)
 THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 RETURN head;
END$function$;
ALTER FUNCTION security.require_gas_cashout_binding(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_cashout_binding(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.require_gas_cashout_binding(uuid,uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.read_gas_valuation_balances(catalog uuid,trade uuid,binding uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $function$
DECLARE q security.request_contexts;head trading.gas_trade_revisions;result jsonb;
BEGIN
 head=security.require_gas_cashout_binding(catalog,trade,binding);
 SELECT * INTO STRICT q FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND expires_at>clock_timestamp();
 WITH sources AS MATERIALIZED(
  SELECT DISTINCT o.slot,o.id obligation_id,m.delivery_from,m.delivery_to
  FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m
   ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
  WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,binding) AND EXISTS(
   SELECT 1 FROM trading.gas_component_terms p WHERE(p.catalog_id,p.instrument_id,p.source_slot,p.role)=(catalog,binding,o.slot,'imbalance_cashout'))
 ), accounts AS MATERIALIZED(
  SELECT a.*,v.id service_version_id,v.valid_from,v.valid_to,v.active,v.evidence service_evidence
  FROM sources o JOIN trading.gas_service_accounts a ON(a.catalog_id,a.origin_obligation_id,a.trade_id)=(catalog,o.obligation_id,trade)
  JOIN LATERAL(SELECT v.* FROM trading.gas_service_versions v WHERE(v.catalog_id,v.account_id)=(a.catalog_id,a.id)
   AND security.trading_visible(v.catalog_id,v.publication_order,v.effective_at) ORDER BY v.revision DESC LIMIT 1)v ON true
  WHERE a.holder_role='legal_entity' AND(a.legal_entity_id,a.legal_entity_revision)=(head.legal_entity_id,head.legal_entity_revision)
   AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)
 ), days AS(
  SELECT o.slot,o.obligation_id,o.delivery_from+n AS day FROM sources o CROSS JOIN LATERAL generate_series(0,o.delivery_to-o.delivery_from-1)n
 ), day_accounts AS(
  SELECT d.slot,d.day,coalesce(jsonb_agg(jsonb_build_object('accountId',a.id,'serviceVersionId',a.service_version_id,
   'serviceEvidence',a.service_evidence,'referenceCatalogId',a.reference_catalog_id,'referenceRevisionId',a.reference_revision_id,
   'quantityCode',a.quantity_code,'statement',statement.value,'movements',coalesce(movement.value,'[]')) ORDER BY a.id) FILTER(WHERE a.id IS NOT NULL),'[]') accounts
  FROM days d LEFT JOIN accounts a ON a.origin_obligation_id=d.obligation_id AND a.active AND a.valid_from<=d.day AND d.day<a.valid_to
  LEFT JOIN LATERAL(
   SELECT jsonb_build_object('id',s.id,'status',s.status,'balance',trim_scale(s.balance)::text,'includedMovementId',s.included_movement_id,
    'evidence',s.evidence,'effectiveAt',s.effective_at,'knownAt',k.known_from,'revision',s.revision,'serviceVersionId',s.service_version_id) value
   FROM trading.gas_balance_statements s JOIN trading.knowledge_receipts k USING(catalog_id,publication_order)
   WHERE(s.catalog_id,s.account_id,s.day)=(catalog,a.id,d.day) AND security.trading_visible(s.catalog_id,s.publication_order,s.effective_at)
   ORDER BY s.revision DESC LIMIT 1
  ) statement ON true
  LEFT JOIN LATERAL(
   SELECT jsonb_agg(jsonb_build_object('id',m.id,'ordinal',m.ordinal,'quantity',trim_scale(m.quantity)::text,
    'applicationId',m.application_id,'effectiveAt',app.effective_at,'externalEffectiveAt',m.external_effective_at) ORDER BY m.ordinal) value
   FROM trading.gas_account_movements m JOIN trading.gas_imbalance_applications app ON(app.catalog_id,app.id)=(m.catalog_id,m.application_id)
   WHERE(m.catalog_id,m.account_id,m.day)=(catalog,a.id,d.day) AND security.trading_visible(app.catalog_id,app.publication_order,app.effective_at)
    AND m.external_effective_at<=q.trading_as_of
  ) movement ON true GROUP BY d.slot,d.day
 ), slots AS(SELECT slot,jsonb_agg(jsonb_build_object('day',day,'accounts',accounts) ORDER BY day) days FROM day_accounts GROUP BY slot)
 SELECT coalesce(jsonb_agg(jsonb_build_object('slot',slot,'days',days) ORDER BY slot),'[]') INTO result FROM slots;
 -- Recheck current authority and the active lease immediately before disclosure.
 PERFORM security.require_gas_cashout_binding(catalog,trade,binding);
 RETURN result;
END$function$;
ALTER FUNCTION security.read_gas_valuation_balances(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_valuation_balances(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_valuation_balances(uuid,uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;


COMMIT;
