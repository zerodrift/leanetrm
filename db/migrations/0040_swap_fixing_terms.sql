-- Versioned fixing-aware side terms. Existing rows retain their original model.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.swap_fixing_schedules (
 catalog_id uuid NOT NULL,id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,
 implementation text NOT NULL CHECK(implementation='index_actual_projected_v1'),revision_policy text NOT NULL CHECK(revision_policy='latest_eligible_final_by_context_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,PRIMARY KEY(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_reset_fixing_boundaries (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 365),
 observation_from date NOT NULL,observation_to date NOT NULL,fixing_at ref_data.instant NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,PRIMARY KEY(catalog_id,schedule_id,ordinal),
 CHECK(observation_from<observation_to),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.swap_fixing_schedules(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.swap_floating_sides ADD COLUMN fixing_schedule_id uuid,
 ADD FOREIGN KEY(catalog_id,fixing_schedule_id) REFERENCES trading.swap_fixing_schedules(catalog_id,id);
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_sides_implementation_check;
ALTER TABLE trading.swap_floating_sides ADD CHECK(implementation IN('prefixing_index_projection_v1','index_actual_projected_v1')),
 ADD CHECK((fixing_schedule_id IS NULL)=(implementation='prefixing_index_projection_v1'));
ALTER TABLE trading.swap_floating_sides DROP CONSTRAINT swap_floating_economic_key;
ALTER TABLE trading.swap_floating_sides ADD CONSTRAINT swap_floating_economic_key UNIQUE NULLS NOT DISTINCT
 (catalog_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,index_code,quote_code,
 delivery_from,delivery_to,first_fixing_at,payment_at,averaging,implementation,payment_rule_id,spread,fixing_schedule_id);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_fixing_schedules','swap_reset_fixing_boundaries'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END $$;
CREATE FUNCTION security.retain_swap_fixing_schedule(catalog uuid,ref_catalog uuid,ref_revision uuid,body jsonb,expected_rows jsonb,first_fixing timestamptz,payment timestamptz,publication_number integer,from_at timestamptz)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE found_id uuid;new_id uuid;n integer;
BEGIN
 IF body IS NULL OR body='null'::jsonb THEN RETURN NULL;END IF;
 IF body->>'implementation' IS DISTINCT FROM 'index_actual_projected_v1' OR body->>'revisionPolicy' IS DISTINCT FROM 'latest_eligible_final_by_context_v1' OR
  body->>'source' IS NULL OR jsonb_typeof(body->'boundaries') IS DISTINCT FROM 'array' OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('implementation','revisionPolicy','source','boundaries')) THEN RAISE EXCEPTION 'index_fixing_rule';END IF;
 n=jsonb_array_length(expected_rows);
 IF n NOT BETWEEN 1 AND 366 OR jsonb_array_length(body->'boundaries')<>n OR
  EXISTS(SELECT 1 FROM jsonb_array_elements(body->'boundaries') WITH ORDINALITY b(v,n) FULL JOIN jsonb_array_elements(expected_rows) WITH ORDINALITY e(v,n) USING(n)
   WHERE b.v IS NULL OR e.v IS NULL OR(b.v->>'from')::date IS DISTINCT FROM(e.v->>'from')::date OR(b.v->>'to')::date IS DISTINCT FROM(e.v->>'to')::date OR
    b.v->>'fixingAt' IS NULL OR(b.v->>'fixingAt')::timestamptz>=payment OR EXISTS(SELECT 1 FROM jsonb_object_keys(b.v)k WHERE k NOT IN('from','to','fixingAt'))) OR
  (SELECT min((b->>'fixingAt')::timestamptz) FROM jsonb_array_elements(body->'boundaries')b) IS DISTINCT FROM first_fixing THEN RAISE EXCEPTION 'index_fixing_rule';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.sources s WHERE(s.catalog_id,s.revision_id,s.code)=(ref_catalog,ref_revision,body->>'source')) THEN RAISE EXCEPTION 'index_fixing_definition';END IF;
 SELECT s.id INTO found_id FROM trading.swap_fixing_schedules s WHERE s.catalog_id=catalog AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.source_code=body->>'source' AND s.revision_policy=body->>'revisionPolicy'
  AND(SELECT count(*) FROM trading.swap_reset_fixing_boundaries b WHERE(b.catalog_id,b.schedule_id)=(catalog,s.id))=n
  AND NOT EXISTS(SELECT 1 FROM trading.swap_reset_fixing_boundaries b JOIN jsonb_array_elements(body->'boundaries') WITH ORDINALITY e(v,n) ON b.ordinal=e.n-1
   WHERE(b.catalog_id,b.schedule_id)=(catalog,s.id) AND(b.observation_from,b.observation_to,b.fixing_at) IS DISTINCT FROM((e.v->>'from')::date,(e.v->>'to')::date,(e.v->>'fixingAt')::timestamptz));
 IF found_id IS NOT NULL THEN RETURN found_id;END IF;
 new_id=gen_random_uuid();INSERT INTO trading.swap_fixing_schedules VALUES(catalog,new_id,ref_catalog,ref_revision,body->>'source','index_actual_projected_v1',body->>'revisionPolicy',publication_number,from_at);
 INSERT INTO trading.swap_reset_fixing_boundaries SELECT catalog,new_id,(e.n-1)::integer,(e.v->>'from')::date,(e.v->>'to')::date,(e.v->>'fixingAt')::timestamptz,publication_number,from_at FROM jsonb_array_elements(body->'boundaries') WITH ORDINALITY e(v,n);
 RETURN new_id;
END $$;

CREATE FUNCTION security.require_swap_fixing_amendment(catalog uuid,instrument uuid,first_body jsonb,float_body jsonb,valuation timestamptz)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s record;replacement jsonb;
BEGIN
 FOR s IN SELECT fs.*,occurrence.slot,occurrence.replacement FROM trading.swap_instruments i
  CROSS JOIN LATERAL(VALUES(0,i.first_floating_side_id,first_body),(1,i.floating_side_id,float_body))occurrence(slot,side_id,replacement)
  JOIN trading.swap_floating_sides fs ON(fs.catalog_id,fs.id)=(i.catalog_id,occurrence.side_id)
  WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument) AND fs.fixing_schedule_id IS NOT NULL LOOP
  replacement=s.replacement;
  IF EXISTS(SELECT 1 FROM trading.swap_reset_fixing_boundaries b WHERE(b.catalog_id,b.schedule_id)=(catalog,s.fixing_schedule_id) AND b.fixing_at<=valuation) AND(
   replacement->>'index' IS DISTINCT FROM s.index_code OR
   NOT EXISTS(SELECT 1 FROM trading.swap_fixing_schedules fs WHERE(fs.catalog_id,fs.id)=(catalog,s.fixing_schedule_id) AND(fs.source_code,fs.revision_policy,fs.implementation)=(replacement->'fixingTerms'->>'source',replacement->'fixingTerms'->>'revisionPolicy',replacement->'fixingTerms'->>'implementation')) OR
   EXISTS(SELECT 1 FROM trading.swap_reset_fixing_boundaries b WHERE(b.catalog_id,b.schedule_id)=(catalog,s.fixing_schedule_id) AND b.fixing_at<=valuation AND
    NOT EXISTS(SELECT 1 FROM jsonb_array_elements(replacement->'fixingTerms'->'boundaries')r WHERE((r->>'from')::date,(r->>'to')::date,(r->>'fixingAt')::timestamptz)=(b.observation_from,b.observation_to,b.fixing_at)))) THEN RAISE EXCEPTION 'index_fixing_amendment';END IF;
 END LOOP;
END $$;
CREATE OR REPLACE FUNCTION security.retain_floating_swap_side(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cv market_data.curves;q ref_data.quote_specifications;target ref_data.settlement_targets;k market_data.contracts;
 side uuid;rule uuid;fix_schedule uuid;model_version text;expected_rows jsonb;starts date;ends date;fixing timestamptz;payment timestamptz;sampling text;spread_value numeric;
BEGIN
 IF body IS NULL OR jsonb_typeof(body)<>'object' OR body->>'paymentAt' IS NOT NULL OR body->>'averaging' IS NOT NULL OR body->'paymentSchedule' IS NULL OR body->>'spread' IS NULL
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('curve','index','currency','deliveryFrom','deliveryTo','firstFixingAt','paymentAt','averaging','paymentSchedule','resetFrequency','averagingMethod','spread','fixingTerms')) THEN RAISE EXCEPTION 'swap_input';END IF;
 starts=(body->>'deliveryFrom')::date;ends=(body->>'deliveryTo')::date;fixing=CASE WHEN body->'fixingTerms' IS NOT NULL AND body->'fixingTerms'<>'null'::jsonb THEN(SELECT min((b->>'fixingAt')::timestamptz) FROM jsonb_array_elements(body->'fixingTerms'->'boundaries')b) ELSE(body->>'firstFixingAt')::timestamptz END;spread_value=(body->>'spread')::numeric;
 IF body->>'firstFixingAt' IS NOT NULL AND(body->>'firstFixingAt')::timestamptz IS DISTINCT FROM fixing THEN RAISE EXCEPTION 'index_fixing_rule';END IF;
 sampling=CASE WHEN(body->>'resetFrequency',body->>'averagingMethod')=('monthly','none') THEN 'monthly_index_price' WHEN(body->>'resetFrequency',body->>'averagingMethod')=('daily','arithmetic') THEN 'calendar_day_arithmetic' END;
 payment=security.swap_payment_date(ref_catalog,ref_revision,starts,ends,body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
 IF starts IS NULL OR ends IS NULL OR NOT isfinite(starts) OR NOT isfinite(ends) OR extract(day FROM starts)<>1 OR ends IS DISTINCT FROM(starts+INTERVAL '1 month')::date OR sampling IS NULL OR
  fixing IS NULL OR NOT isfinite(fixing) OR payment IS NULL OR (coalesce(body->'fixingTerms','null'::jsonb)='null'::jsonb AND valuation>=fixing) OR valuation>=payment OR fixing>=payment OR
  spread_value IS NULL OR abs(spread_value)>=1e24 OR scale(spread_value)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
 SELECT * INTO cv FROM market_data.curves WHERE catalog_id=catalog AND revision_id=definition AND code=body->>'curve' AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.quote_code);
 SELECT * INTO target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.settlement_target_code);
 IF cv.commodity_code IS DISTINCT FROM commodity OR q.kind IS DISTINCT FROM 'outright' OR target.kind IS DISTINCT FROM 'index' OR target.index_a_code IS DISTINCT FROM body->>'index' OR q.currency_code IS DISTINCT FROM body->>'currency' OR
  NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,body->>'index') AND(i.currency_code,i.commodity_code,i.location_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,cv.location_code,u.unit_code,q.multiplier)) THEN RAISE EXCEPTION 'swap_definition';END IF;
 IF(SELECT count(*) FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
  WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
   (sampling='monthly_index_price' AND(c.delivery_from,c.delivery_to)=(starts,ends) OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends))<>(CASE WHEN sampling='monthly_index_price' THEN 1 ELSE ends-starts END) OR
  EXISTS(SELECT 1 FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
    (sampling='monthly_index_price' AND(c.delivery_from,c.delivery_to)=(starts,ends) OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends) AND valuation>=c.expiry_at AND(coalesce(body->'fixingTerms','null'::jsonb)='null'::jsonb OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'fixingTerms'->'boundaries')b WHERE(b->>'from')::date=c.delivery_from AND(b->>'to')::date=c.delivery_to AND(b->>'fixingAt')::timestamptz>valuation))) OR
  sampling='calendar_day_arithmetic' AND(SELECT count(DISTINCT c.delivery_from) FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends)<>ends-starts THEN RAISE EXCEPTION 'swap_projection';END IF;
 IF sampling='monthly_index_price' THEN
  SELECT c.* INTO STRICT k FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND(c.delivery_from,c.delivery_to)=(starts,ends);
 END IF;
 SELECT jsonb_agg(jsonb_build_object('from',c.delivery_from,'to',c.delivery_to) ORDER BY c.delivery_from,c.id) INTO expected_rows
 FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
 WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND(sampling='monthly_index_price' AND(c.delivery_from,c.delivery_to)=(starts,ends) OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends);
 fix_schedule=security.retain_swap_fixing_schedule(catalog,ref_catalog,ref_revision,body->'fixingTerms',expected_rows,fixing,payment,publication_number,from_at);
 model_version=CASE WHEN fix_schedule IS NULL THEN 'prefixing_index_projection_v1' ELSE 'index_actual_projected_v1' END;
 IF fix_schedule IS NOT NULL AND EXISTS(SELECT 1 FROM trading.swap_reset_fixing_boundaries b JOIN trading.swap_fixing_schedules fs ON(fs.catalog_id,fs.id)=(b.catalog_id,b.schedule_id)
  WHERE(b.catalog_id,b.schedule_id)=(catalog,fix_schedule) AND b.fixing_at<=valuation AND NOT EXISTS(SELECT 1 FROM market_data.index_fixings f
   WHERE f.catalog_id=catalog AND(f.reference_catalog_id,f.reference_revision_id,f.index_code,f.quote_code,f.source_code,f.observation_from,f.observation_to)=(ref_catalog,ref_revision,body->>'index',q.code,fs.source_code,b.observation_from,b.observation_to) AND f.publication_state='final' AND f.published_at<=valuation)) THEN RAISE EXCEPTION 'index_fixing_required';END IF;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 SELECT id INTO side FROM trading.swap_floating_sides s WHERE s.catalog_id=catalog AND s.definition_revision_id=definition AND s.curve_code=cv.code AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.index_code=body->>'index' AND s.quote_code=q.code AND s.delivery_from=starts AND s.delivery_to=ends AND s.first_fixing_at=fixing AND s.payment_at=payment AND s.averaging=sampling AND s.payment_rule_id=rule AND s.spread=spread_value AND s.fixing_schedule_id IS NOT DISTINCT FROM fix_schedule;
 IF side IS NULL THEN
  side=gen_random_uuid();
  INSERT INTO trading.swap_floating_sides VALUES(catalog,side,definition,cv.code,k.id,ref_catalog,ref_revision,body->>'index',q.code,starts,ends,fixing,payment,sampling,model_version,publication_number,from_at,rule,spread_value,fix_schedule);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,side,(row_number() OVER(ORDER BY c.delivery_from,c.id)-1)::integer,definition,cv.code,c.id,1,publication_number,from_at
   FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND
    (sampling='monthly_index_price' AND c.id=k.id OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends);
 END IF;
 IF EXISTS(SELECT 1 FROM(SELECT p.ordinal,p.contract_id,p.weight FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(catalog,side)) retained FULL JOIN
  (SELECT(row_number() OVER(ORDER BY c.delivery_from,c.id)-1)::integer AS ordinal,c.id AS contract_id,1::numeric AS weight FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=cv.code AND c.kind='forward' AND(sampling='monthly_index_price' AND c.id=k.id OR sampling='calendar_day_arithmetic' AND c.delivery_to=c.delivery_from+1 AND starts<=c.delivery_from AND c.delivery_from<ends)) expected USING(ordinal)
  WHERE retained.ordinal IS NULL OR expected.ordinal IS NULL OR(retained.contract_id,retained.weight) IS DISTINCT FROM(expected.contract_id,expected.weight)) THEN RAISE EXCEPTION 'swap_projection';END IF;
 RETURN side;
END $$;

CREATE OR REPLACE FUNCTION security.save_composed_swap(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,
 fixed_body jsonb,first_body jsonb,float_body jsonb,quantity numeric,coefficient numeric,discounting boolean,valuation timestamptz,effective timestamptz,reason text,implementation text,memo text,booking jsonb)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;request_value jsonb;permission text;publication_number integer;
 first_side uuid;floating_side uuid;fixed_side uuid;instrument uuid;binding uuid;from_at timestamptz;executed timestamptz;q ref_data.quote_specifications;other_quote ref_data.quote_specifications;rule uuid;payment timestamptz;price numeric;
BEGIN
 permission=CASE WHEN trade IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR implementation IS DISTINCT FROM 'independent_side_financial_swap_v2' OR
  (fixed_body IS NULL)=(first_body IS NULL) OR discounting IS NULL OR coefficient IS NULL OR coefficient NOT IN(-1,1) OR quantity IS NULL OR quantity<=0 OR abs(quantity)>=1e24 OR scale(quantity)>18 OR
  valuation IS NULL OR effective IS NULL OR NOT isfinite(valuation) OR NOT isfinite(effective) OR effective>valuation OR booking->>'tradeDate' IS NOT NULL THEN RAISE EXCEPTION 'swap_input';END IF;
 request_value=jsonb_build_object('trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,'commodity',commodity,
  'fixed',fixed_body||jsonb_build_object('price',(fixed_body->>'price')::numeric),'firstFloating',first_body||jsonb_build_object('spread',(first_body->>'spread')::numeric),'floating',float_body||jsonb_build_object('spread',(float_body->>'spread')::numeric),'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation,'effective',effective,'reason',reason,'implementation',implementation,'memo',memo,'booking',booking,'schema','independent_swap_sides_v1');
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request<>request_value THEN RAISE EXCEPTION 'swap_conflict';END IF;
  RETURN security.swap_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation) THEN RAISE EXCEPTION 'swap_conflict';END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 executed=valuation;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade';END IF;
  IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
  IF head.revision>=256 OR effective IS DISTINCT FROM valuation OR effective<head.effective_at OR reason IS NULL OR length(btrim(reason)) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'swap_amendment';END IF;
  IF EXISTS(SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
   LEFT JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id) LEFT JOIN trading.swap_floating_sides a ON(a.catalog_id,a.id)=(i.catalog_id,i.first_floating_side_id)
   WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND((s.fixing_schedule_id IS NULL AND valuation>=s.first_fixing_at) OR valuation>=s.payment_at OR valuation>=f.payment_at OR (a.fixing_schedule_id IS NULL AND valuation>=a.first_fixing_at) OR valuation>=a.payment_at OR (f.id IS NULL)<>(fixed_body IS NULL))) THEN RAISE EXCEPTION 'swap_amendment';END IF;
  PERFORM security.require_swap_fixing_amendment(catalog,head.instrument_id,first_body,float_body,valuation);
  executed=head.execution_at;
  booking=booking||jsonb_build_object('tradeDate',coalesce(head.trade_date,(SELECT f.trade_date FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(catalog,trade))));
 ELSE
  IF effective IS DISTINCT FROM executed OR expected IS NOT NULL OR(SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'swap_limit';END IF;
  booking=booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date);
 END IF;
 PERFORM security.require_trade_annotation(catalog,head.trader_id,head.trader_revision,memo,executed);
 PERFORM security.require_trade_parties(catalog,booking,executed);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=executed AND p.effective_at<=executed) THEN RAISE EXCEPTION 'swap_book';END IF;
 SELECT effective_from::timestamp AT TIME ZONE 'UTC' INTO from_at FROM market_data.revision_periods WHERE catalog_id=catalog AND revision_id=definition AND effective_from<=(executed AT TIME ZONE 'UTC')::date AND(executed AT TIME ZONE 'UTC')::date<effective_to;
 IF from_at IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE catalog_id=catalog AND id=definition AND state='sealed') THEN RAISE EXCEPTION 'swap_definition';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication_number FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication_number,pg_current_xact_id());
 floating_side=security.retain_floating_swap_side(catalog,definition,ref_catalog,ref_revision,commodity,float_body,valuation,publication_number,from_at);
 SELECT q1.* INTO q FROM trading.swap_floating_sides s JOIN ref_data.quote_specifications q1 ON(q1.catalog_id,q1.revision_id,q1.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) WHERE(s.catalog_id,s.id)=(catalog,floating_side);
 IF first_body IS NOT NULL THEN
  first_side=security.retain_floating_swap_side(catalog,definition,ref_catalog,ref_revision,commodity,first_body,valuation,publication_number,from_at);
  SELECT q1.* INTO other_quote FROM trading.swap_floating_sides s JOIN ref_data.quote_specifications q1 ON(q1.catalog_id,q1.revision_id,q1.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) WHERE(s.catalog_id,s.id)=(catalog,first_side);
  IF(other_quote.currency_code,other_quote.quantity_code) IS DISTINCT FROM(q.currency_code,q.quantity_code) THEN RAISE EXCEPTION 'swap_definition';END IF;
 ELSE
  IF fixed_body->>'paymentAt' IS NOT NULL OR fixed_body->>'currency' IS DISTINCT FROM q.currency_code THEN RAISE EXCEPTION 'swap_input';END IF;
  price=(fixed_body->>'price')::numeric;
  IF price IS NULL OR abs(price)>=1e24 OR scale(price)>18 THEN RAISE EXCEPTION 'swap_input';END IF;
  payment=security.swap_payment_date(ref_catalog,ref_revision,(fixed_body->>'deliveryFrom')::date,(fixed_body->>'deliveryTo')::date,fixed_body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';
  IF payment IS NULL OR valuation>=payment THEN RAISE EXCEPTION 'swap_input';END IF;
  rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,fixed_body->'paymentSchedule',publication_number,from_at);
  SELECT id INTO fixed_side FROM trading.swap_fixed_sides s WHERE s.catalog_id=catalog AND s.reference_catalog_id=ref_catalog AND s.reference_revision_id=ref_revision AND s.quote_code=q.code AND s.delivery_from=(fixed_body->>'deliveryFrom')::date AND s.delivery_to=(fixed_body->>'deliveryTo')::date AND s.payment_at=payment AND s.fixed_price=price AND s.payment_rule_id=rule;
  IF fixed_side IS NULL THEN
   fixed_side=gen_random_uuid();INSERT INTO trading.swap_fixed_sides VALUES(catalog,fixed_side,ref_catalog,ref_revision,q.code,(fixed_body->>'deliveryFrom')::date,(fixed_body->>'deliveryTo')::date,payment,price,'fixed_total_payment_v1',publication_number,from_at,rule);
  END IF;
 END IF;
 IF trade IS NOT NULL AND EXISTS(SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id) WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND(s.reference_catalog_id,s.reference_revision_id,s.quote_code) IS DISTINCT FROM(ref_catalog,ref_revision,q.code)) THEN RAISE EXCEPTION 'swap_amendment';END IF;
 SELECT i.instrument_id INTO instrument FROM trading.swap_instruments i WHERE i.catalog_id=catalog AND i.fixed_side_id IS NOT DISTINCT FROM fixed_side AND i.first_floating_side_id IS NOT DISTINCT FROM first_side AND i.floating_side_id=floating_side AND i.fixed_coefficient=coefficient AND i.discounting=save_composed_swap.discounting;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments VALUES(catalog,instrument,fixed_side,floating_side,coefficient,discounting,implementation,publication_number,from_at,first_side);
 END IF;
 IF trade IS NULL THEN
  trade=gen_random_uuid();binding=gen_random_uuid();booking=booking||jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request_value,publication_number,executed);
 ELSE
  IF(book,instrument,quantity,memo,booking) IS NOT DISTINCT FROM(head.book_id,head.instrument_id,head.quantity,head.memo,security.trade_party_payload(head.trade_date,head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision)) THEN RAISE EXCEPTION 'swap_no_change';END IF;
  binding=CASE WHEN instrument=head.instrument_id THEN head.binding_id ELSE gen_random_uuid() END;
 END IF;
 INSERT INTO trading.swap_trade_revisions VALUES(catalog,trade,coalesce(head.revision,0)+1,head.revision,gen_random_uuid(),'monthly_financial_swap_booking_v2',CASE WHEN head.revision IS NULL THEN 'booked' ELSE 'amended' END,
  book,binding,instrument,quantity,executed,effective,operation,c.actor_id,CASE WHEN head.revision IS NULL THEN 'Initial booking' ELSE btrim(reason) END,request_value,publication_number,head.trader_id,head.trader_revision,memo,(booking->>'tradeDate')::date,(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,(booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 RETURN security.swap_operation_status(catalog,operation);
END $$;
-- Keep legacy admission pinned to legacy side economics even when a newer
-- fixing-aware side has the same visible projection/payment terms.
CREATE OR REPLACE FUNCTION security.save_swap_with_parties(catalog uuid,operation uuid,trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,
 curve text,index_name text,fixed_currency text,float_currency text,fixed_from date,fixed_to date,fixed_payment timestamptz,float_from date,float_to date,
 fixing timestamptz,float_payment timestamptz,averaging text,price numeric,quantity numeric,coefficient numeric,discounting boolean,
 valuation timestamptz,executed timestamptz,effective timestamptz,reason text,implementation text,fixed_schedule jsonb,float_schedule jsonb,commodity text,trader uuid,trader_revision integer,memo text,booking jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE fixed_rule uuid;float_rule uuid;c security.request_contexts;prior trading.swap_trade_revisions;head trading.swap_trade_revisions;request_value jsonb;permission text;publication_number integer;
 cv market_data.curves;q ref_data.quote_specifications;k market_data.contracts;target ref_data.settlement_targets;fixed_side uuid;floating_side uuid;instrument uuid;binding uuid;from_at timestamptz;
BEGIN
 permission=CASE WHEN trade IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501'; END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001'; END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF executed IS NOT NULL OR trader IS NOT NULL OR trader_revision IS NOT NULL OR booking->>'tradeDate' IS NOT NULL THEN RAISE EXCEPTION 'trade_booking_read_only' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501'; END IF;
 executed=valuation;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR implementation IS DISTINCT FROM 'independent_side_financial_swap_v2' OR
  discounting IS NULL OR coefficient IS NULL OR coefficient NOT IN(-1,1) OR quantity IS NULL OR quantity<=0 OR abs(quantity)>=1e24 OR scale(quantity)>18 OR
  valuation IS NULL OR executed IS NULL OR effective IS NULL OR NOT isfinite(valuation) OR NOT isfinite(executed) OR NOT isfinite(effective) OR executed>effective OR effective>valuation OR
  fixed_from IS NULL OR fixed_to IS NULL OR fixed_to<=fixed_from OR fixed_to-fixed_from>366 OR
  fixed_payment IS NULL OR float_payment IS NULL OR fixing IS NULL OR NOT isfinite(fixed_payment) OR NOT isfinite(float_payment) OR NOT isfinite(fixing) OR
  valuation>=fixing OR valuation>=fixed_payment OR valuation>=float_payment OR fixing>=float_payment OR(fixed_schedule IS NULL AND(fixed_payment AT TIME ZONE 'UTC')::date<fixed_to) OR(float_schedule IS NULL AND(float_payment AT TIME ZONE 'UTC')::date<float_to) OR
  averaging IS NULL OR averaging NOT IN('monthly_index_price','calendar_day_arithmetic') OR price IS NULL OR abs(price)>=1e24 OR scale(price)>18
 THEN RAISE EXCEPTION 'swap_input' USING ERRCODE='P0001'; END IF;
 request_value=jsonb_build_object('trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,
  'curve',curve,'index',index_name,'fixedCurrency',fixed_currency,'floatingCurrency',float_currency,'fixedFrom',fixed_from,'fixedTo',fixed_to,'fixedPayment',fixed_payment AT TIME ZONE 'UTC',
  'floatingFrom',float_from,'floatingTo',float_to,'firstFixing',fixing AT TIME ZONE 'UTC','floatingPayment',float_payment AT TIME ZONE 'UTC','averaging',averaging,
  'price',price,'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation AT TIME ZONE 'UTC','executed',NULL,'effective',effective AT TIME ZONE 'UTC','reason',reason,'implementation',implementation);
 IF fixed_schedule IS NOT NULL OR float_schedule IS NOT NULL OR commodity IS NOT NULL THEN request_value=request_value||jsonb_build_object('fixedSchedule',fixed_schedule,'floatingSchedule',float_schedule,'commodity',commodity);END IF;
 request_value=request_value||jsonb_build_object('booking',booking,'bookingFactsPolicy','server_booking_utc_v1');
 IF trader IS NOT NULL OR trader_revision IS NOT NULL OR memo IS NOT NULL THEN request_value=request_value||jsonb_build_object('trader',trader,'traderRevision',trader_revision,'memo',memo);END IF;
 SELECT * INTO prior FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request<>request_value THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
  RETURN security.swap_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation)
 THEN RAISE EXCEPTION 'swap_conflict' USING ERRCODE='P0001'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501'; END IF;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade' USING ERRCODE='P0001';END IF;
  executed=head.execution_at;trader=head.trader_id;trader_revision=head.trader_revision;
  booking=booking||jsonb_build_object('tradeDate',coalesce(head.trade_date,(SELECT f.trade_date FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(catalog,trade))));
 ELSE
  booking=booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date);
 END IF;
 PERFORM security.require_trade_annotation(catalog,trader,trader_revision,memo,executed);
 PERFORM security.require_trade_parties(catalog,booking,executed);
 IF fixed_schedule IS NULL OR float_schedule IS NULL OR commodity IS NULL THEN RAISE EXCEPTION 'payment_rule' USING ERRCODE='P0001';END IF;
 IF fixed_payment IS DISTINCT FROM(security.swap_payment_date(ref_catalog,ref_revision,fixed_from,fixed_to,fixed_schedule)::timestamp AT TIME ZONE 'UTC') OR float_payment IS DISTINCT FROM(security.swap_payment_date(ref_catalog,ref_revision,float_from,float_to,float_schedule)::timestamp AT TIME ZONE 'UTC') THEN RAISE EXCEPTION 'payment_date_mismatch' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE b.catalog_id=catalog AND b.id=book AND b.effective_at<=executed AND p.effective_at<=executed)
 THEN RAISE EXCEPTION 'swap_book' USING ERRCODE='P0001'; END IF;
 SELECT * INTO cv FROM market_data.curves WHERE catalog_id=catalog AND revision_id=definition AND code=curve AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.quote_code);
 SELECT * INTO target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.settlement_target_code);
 IF cv.commodity_code IS DISTINCT FROM commodity OR q.kind IS DISTINCT FROM 'outright' OR target.kind IS DISTINCT FROM 'index' OR target.index_a_code IS DISTINCT FROM index_name OR
  q.currency_code IS DISTINCT FROM fixed_currency OR q.currency_code IS DISTINCT FROM float_currency OR
  NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   WHERE(i.catalog_id,i.revision_id,i.code)=(ref_catalog,ref_revision,index_name) AND(i.currency_code,i.commodity_code,i.location_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,cv.commodity_code,cv.location_code,u.unit_code,q.multiplier))
 THEN RAISE EXCEPTION 'swap_definition' USING ERRCODE='P0001'; END IF;
 IF float_from IS NULL OR float_to IS NULL OR extract(day FROM float_from)<>1 OR float_to IS DISTINCT FROM(float_from+INTERVAL '1 month')::date THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 IF averaging='monthly_index_price' THEN
  IF(SELECT count(*) FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_from=float_from AND k1.delivery_to=float_to)<>1 THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
  SELECT k1.* INTO k FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_from=float_from AND k1.delivery_to=float_to;
  IF valuation>=k.expiry_at THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 ELSE
  IF(SELECT count(*) FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
    WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)<>float_to-float_from OR
    (SELECT count(DISTINCT k1.delivery_from) FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
    WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)<>float_to-float_from OR
    EXISTS(SELECT 1 FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
     WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to AND valuation>=k1.expiry_at)
  THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 END IF;
 SELECT effective_from::timestamp AT TIME ZONE 'UTC' INTO from_at FROM market_data.revision_periods WHERE catalog_id=catalog AND revision_id=definition AND effective_from<=(executed AT TIME ZONE 'UTC')::date AND(executed AT TIME ZONE 'UTC')::date<effective_to;
 IF from_at IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE catalog_id=catalog AND id=definition AND state='sealed') THEN RAISE EXCEPTION 'swap_definition' USING ERRCODE='P0001'; END IF;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.swap_trade_revisions r WHERE r.catalog_id=catalog AND r.trade_id=trade ORDER BY r.revision DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'swap_trade' USING ERRCODE='P0001'; END IF;
  IF EXISTS(SELECT 1 FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND i.first_floating_side_id IS NOT NULL) THEN RAISE EXCEPTION 'swap_amendment';END IF;
  IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'swap_revision_conflict' USING ERRCODE='P0001'; END IF;
  IF head.revision>=256 OR effective IS DISTINCT FROM valuation OR effective<head.effective_at OR reason IS NULL OR length(btrim(reason)) NOT BETWEEN 1 AND 500 OR reason ~ '^[[:space:]]*$'
   OR EXISTS(SELECT 1 FROM trading.swap_instruments i JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
    JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id) WHERE i.catalog_id=catalog AND i.instrument_id=head.instrument_id
    AND (valuation>=s.first_fixing_at OR valuation>=s.payment_at OR valuation>=f.payment_at OR f.reference_catalog_id<>ref_catalog OR f.reference_revision_id<>ref_revision OR f.quote_code<>q.code))
  THEN RAISE EXCEPTION 'swap_amendment' USING ERRCODE='P0001'; END IF;
 ELSE
  IF effective IS DISTINCT FROM executed OR expected IS NOT NULL OR (SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'swap_limit' USING ERRCODE='P0001'; END IF;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication_number FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication_number,pg_current_xact_id());
 fixed_rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,fixed_schedule,publication_number,from_at);
 float_rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,float_schedule,publication_number,from_at);
 SELECT id INTO fixed_side FROM trading.swap_fixed_sides WHERE catalog_id=catalog AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision AND quote_code=q.code AND delivery_from=fixed_from AND delivery_to=fixed_to AND payment_at=fixed_payment AND fixed_price=price AND payment_rule_id=fixed_rule;
 IF fixed_side IS NULL THEN
  fixed_side=gen_random_uuid();INSERT INTO trading.swap_fixed_sides VALUES(catalog,fixed_side,ref_catalog,ref_revision,q.code,fixed_from,fixed_to,fixed_payment,price,'fixed_total_payment_v1',publication_number,from_at,fixed_rule);
 END IF;
 SELECT id INTO floating_side FROM trading.swap_floating_sides WHERE catalog_id=catalog AND definition_revision_id=definition AND curve_code=curve AND reference_catalog_id=ref_catalog AND reference_revision_id=ref_revision AND quote_code=q.code AND delivery_from=float_from AND delivery_to=float_to AND index_code=index_name AND first_fixing_at=fixing AND payment_at=float_payment AND swap_floating_sides.averaging=save_swap_with_parties.averaging AND payment_rule_id=float_rule AND spread=0 AND fixing_schedule_id IS NULL AND swap_floating_sides.implementation='prefixing_index_projection_v1';
 IF floating_side IS NOT NULL AND EXISTS(
  SELECT 1 FROM (
   SELECT p.ordinal,p.definition_revision_id,p.curve_code,p.contract_id,p.weight FROM trading.swap_reset_projections p
   WHERE(p.catalog_id,p.side_id)=(catalog,floating_side)
  ) retained FULL JOIN (
   SELECT (row_number() OVER(ORDER BY k1.delivery_from,k1.id)-1)::integer AS ordinal,definition AS definition_revision_id,
    curve AS curve_code,k1.id AS contract_id,1::numeric AS weight
   FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND
    (averaging='monthly_index_price' AND k1.id=k.id OR averaging='calendar_day_arithmetic' AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to)
  ) expected USING(ordinal)
  WHERE retained.ordinal IS NULL OR expected.ordinal IS NULL OR
   (retained.definition_revision_id,retained.curve_code,retained.contract_id,retained.weight)
    IS DISTINCT FROM(expected.definition_revision_id,expected.curve_code,expected.contract_id,expected.weight)
 ) THEN RAISE EXCEPTION 'swap_projection' USING ERRCODE='P0001'; END IF;
 IF floating_side IS NULL THEN
  floating_side=gen_random_uuid();INSERT INTO trading.swap_floating_sides VALUES(catalog,floating_side,definition,curve,k.id,ref_catalog,ref_revision,index_name,q.code,float_from,float_to,fixing,float_payment,averaging,'prefixing_index_projection_v1',publication_number,from_at,float_rule);
  INSERT INTO trading.swap_reset_projections
   SELECT catalog,floating_side,(row_number() OVER(ORDER BY k1.delivery_from,k1.id)-1)::integer,definition,curve,k1.id,1,publication_number,from_at
   FROM market_data.contracts k1 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k1.catalog_id,k1.revision_id,k1.id)
   WHERE m.catalog_id=catalog AND m.revision_id=definition AND m.curve_code=curve AND
    (averaging='monthly_index_price' AND k1.id=k.id OR averaging='calendar_day_arithmetic' AND k1.kind='forward' AND k1.delivery_to=k1.delivery_from+1 AND float_from<=k1.delivery_from AND k1.delivery_from<float_to);
 END IF;
 SELECT instrument_id INTO instrument FROM trading.swap_instruments WHERE catalog_id=catalog AND fixed_side_id=fixed_side AND floating_side_id=floating_side AND fixed_coefficient=coefficient AND swap_instruments.discounting=save_swap_with_parties.discounting;
 IF instrument IS NULL THEN
  instrument=gen_random_uuid();INSERT INTO trading.instruments VALUES(catalog,instrument,publication_number,from_at);
  INSERT INTO trading.swap_instruments VALUES(catalog,instrument,fixed_side,floating_side,coefficient,discounting,implementation,publication_number,from_at);
 END IF;
 IF trade IS NULL THEN
  trade=gen_random_uuid();binding=gen_random_uuid();
  booking=booking||jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request_value,publication_number,executed);
 ELSE
  IF(book,instrument,quantity,executed,trader,trader_revision,memo,booking) IS NOT DISTINCT FROM(head.book_id,head.instrument_id,head.quantity,head.execution_at,head.trader_id,head.trader_revision,head.memo,security.trade_party_payload(head.trade_date,head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision)) THEN RAISE EXCEPTION 'swap_no_change' USING ERRCODE='P0001'; END IF;
  binding=CASE WHEN instrument=head.instrument_id THEN head.binding_id ELSE gen_random_uuid() END;
 END IF;
 INSERT INTO trading.swap_trade_revisions VALUES(catalog,trade,coalesce(head.revision,0)+1,head.revision,gen_random_uuid(),'monthly_financial_swap_booking_v2',CASE WHEN head.revision IS NULL THEN 'booked' ELSE 'amended' END,
  book,binding,instrument,quantity,executed,effective,operation,c.actor_id,CASE WHEN head.revision IS NULL THEN 'Initial booking' ELSE btrim(reason) END,request_value,publication_number,trader,trader_revision,memo,(booking->>'tradeDate')::date,(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,(booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501'; END IF;
 RETURN security.swap_operation_status(catalog,operation);
END $$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('retain_swap_fixing_schedule','require_swap_fixing_amendment') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
COMMIT;
