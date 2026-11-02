-- An exercise, assignment or instruction cannot be dated after the moment it is recorded (D290). The check
-- follows authorization, input validation and the replay of a recorded request, so a retry still receives
-- its first outcome.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE OR REPLACE FUNCTION security.apply_futures_option_event(catalog uuid,event_request uuid,trade uuid,event_kind text,event_lots numeric,event_at timestamptz,
 snapshot uuid,observations uuid[],request_value jsonb) RETURNS jsonb LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.futures_option_events;r trading.futures_option_trade_revisions;option_contract market_data.contracts;
 series_row ref_data.futures_option_series;product ref_data.futures_option_products;future_contract market_data.contracts;future_product ref_data.futures_products;
 curve text;curves integer;rule ref_data.futures_option_rules;instruction trading.futures_option_events;expiry trading.futures_option_events;
 settlement market_data.observations;settlements integer;open_lots numeric;decided text;ordinal integer;future_trade uuid;holder numeric;e trading.futures_option_events;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'No booking access' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'Booking access expired' USING ERRCODE='42501';END IF;
 IF event_request IS NULL OR event_request='00000000-0000-0000-0000-000000000000'::uuid OR trade IS NULL OR
  event_kind IS NULL OR event_kind NOT IN('exercise','assignment','exercise_instruction','abandon_instruction','expiry') OR
  (event_kind='expiry')<>(observations IS NOT NULL) OR (event_kind='expiry')<>(snapshot IS NOT NULL) OR
  array_position(observations,NULL) IS NOT NULL OR cardinality(observations)>100000 OR
  (event_kind<>'expiry' AND(event_at IS NULL OR NOT isfinite(event_at))) OR
  (event_kind IN('exercise','assignment'))<>(event_lots IS NOT NULL) OR event_lots<=0 OR event_lots<>trunc(event_lots) OR event_lots>100000
 THEN RAISE EXCEPTION 'option_event_input' USING ERRCODE='P0001';END IF;
 -- The option trade, its exact series and the underlying future of the same market revision, read under the caller's
 -- own market and reference authority: a trade the caller cannot see is refused exactly as one that does not exist,
 -- before any recorded outcome is returned.
 SELECT * INTO r FROM trading.futures_option_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade);
 SELECT * INTO option_contract FROM market_data.contracts WHERE(catalog_id,revision_id,id)=(catalog,r.definition_revision_id,r.contract_id);
 SELECT * INTO series_row FROM ref_data.futures_option_series
  WHERE(catalog_id,revision_id,code)=(option_contract.reference_catalog_id,option_contract.reference_revision_id,option_contract.futures_option_series_code);
 SELECT * INTO product FROM ref_data.futures_option_products WHERE(catalog_id,revision_id,code)=(series_row.catalog_id,series_row.revision_id,series_row.product_code);
 SELECT * INTO future_contract FROM market_data.contracts WHERE(catalog_id,revision_id,kind,futures_series_code)=(catalog,r.definition_revision_id,'future',series_row.underlying_series_code)
  AND(reference_catalog_id,reference_revision_id)=(series_row.catalog_id,series_row.revision_id);
 SELECT p.* INTO future_product FROM ref_data.futures_series s JOIN ref_data.futures_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code)
  WHERE(s.catalog_id,s.revision_id,s.code)=(series_row.catalog_id,series_row.revision_id,series_row.underlying_series_code);
 IF r.trade_id IS NULL OR option_contract.id IS NULL OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.market_fact_visible(catalog,r.definition_revision_id,NULL) OR
  NOT security.reference_access(option_contract.reference_catalog_id,'ReadMarketData') OR
  NOT security.reference_version_visible(option_contract.reference_catalog_id,option_contract.reference_revision_id) OR
  series_row.code IS NULL OR product.code IS NULL OR future_product.code IS NULL THEN RAISE EXCEPTION 'option_trade' USING ERRCODE='P0001';END IF;
 SELECT * INTO prior FROM trading.futures_option_events WHERE(catalog_id,request_id)=(catalog,event_request);
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request IS DISTINCT FROM request_value THEN RAISE EXCEPTION 'option_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.futures_option_event_outcome(prior);
 END IF;
 -- A new exercise, assignment or instruction cannot be dated after the moment it is recorded.
 IF event_kind<>'expiry' AND event_at>clock_timestamp() THEN RAISE EXCEPTION 'option_event_future' USING ERRCODE='P0001';END IF;
 -- The linked future is valued, and the option expires, on the underlying's one direct daily-settlement curve.
 SELECT min(m.curve_code),count(*) INTO curve,curves FROM market_data.curve_members m JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(m.catalog_id,m.revision_id,m.curve_code)
  WHERE(m.catalog_id,m.revision_id,m.contract_id)=(catalog,r.definition_revision_id,future_contract.id) AND v.rule='direct' AND v.direct_role='daily_settlement';
 IF future_contract.id IS NULL OR curves<>1 THEN RAISE EXCEPTION 'option_underlying_market' USING ERRCODE='P0001';END IF;
 SELECT * INTO expiry FROM trading.futures_option_events WHERE(catalog_id,trade_id,kind)=(catalog,trade,'expiry');
 IF expiry.id IS NOT NULL AND event_kind<>'expiry' THEN RAISE EXCEPTION 'option_expired' USING ERRCODE='P0001';END IF;
 holder=sign(r.lots);
 SELECT abs(r.lots)-coalesce(sum(lots),0) INTO open_lots FROM trading.futures_option_events WHERE(catalog_id,trade_id)=(catalog,trade) AND kind IN('exercise','assignment');
 SELECT * INTO instruction FROM trading.futures_option_events WHERE(catalog_id,trade_id)=(catalog,trade) AND kind IN('exercise_instruction','abandon_instruction');
 IF event_kind='expiry' THEN
  -- Expiry is known only once the option has expired: the recording time is at or after its expiry instant.
  IF clock_timestamp()<series_row.expiry_at THEN RAISE EXCEPTION 'option_expiry_not_reached' USING ERRCODE='P0001';END IF;
  -- The underlying's final daily settlement in the curve's quote on the expiry date, among the selected observations.
  SELECT count(*),min(o.id::text)::uuid INTO settlements,settlement.id FROM market_data.observations o
   JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
   JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(s.catalog_id,s.revision_id,curve)
   WHERE o.catalog_id=catalog AND o.id=ANY(observations) AND s.revision_id=r.definition_revision_id AND s.contract_id=future_contract.id AND s.quote_code=v.quote_code
   AND o.role='daily_settlement' AND o.publication_state='final' AND security.market_fact_visible(catalog,o.definition_revision_id,o.id)
   AND security.futures_option_market_date(future_product,o.observed_at)=series_row.last_trading_date;
  IF settlements=0 THEN RAISE EXCEPTION 'option_expiry_settlement_missing' USING ERRCODE='P0001';END IF;
  IF settlements>1 THEN RAISE EXCEPTION 'option_expiry_settlement_ambiguous' USING ERRCODE='P0001';END IF;
  -- The holding's expiry on this settlement already exists: the same fact, returned as recorded.
  IF expiry.id IS NOT NULL THEN
   IF expiry.settlement_observation_id<>settlement.id THEN RAISE EXCEPTION 'option_expiry_conflict' USING ERRCODE='P0001';END IF;
   RETURN security.futures_option_event_outcome(expiry);
  END IF;
  IF open_lots=0 THEN RAISE EXCEPTION 'option_holding_closed' USING ERRCODE='P0001';END IF;
  SELECT * INTO settlement FROM market_data.observations WHERE(catalog_id,id)=(catalog,settlement.id);
  event_at=series_row.expiry_at;event_lots=open_lots;
  IF instruction.id IS NOT NULL THEN decided=CASE instruction.kind WHEN 'exercise_instruction' THEN 'exercised' ELSE 'abandoned' END;
  ELSE
   SELECT * INTO rule FROM ref_data.futures_option_rules
    WHERE(catalog_id,revision_id,code,kind)=(product.catalog_id,product.revision_id,product.automatic_exercise_rule_code,product.automatic_exercise_rule_kind);
   decided=security.futures_option_automatic_outcome(rule,series_row.option_right,series_row.strike,settlement.value);
  END IF;
 ELSIF event_kind IN('exercise_instruction','abandon_instruction') THEN
  IF holder<0 THEN RAISE EXCEPTION 'option_event_side' USING ERRCODE='P0001';END IF;
  IF NOT product.contrary_instructions_allowed THEN RAISE EXCEPTION 'option_contrary_instructions_not_accepted' USING ERRCODE='P0001';END IF;
  -- The holding's instruction already exists: the same one is returned as recorded; a contrary one is a conflict.
  IF instruction.id IS NOT NULL THEN
   IF instruction.kind<>event_kind THEN RAISE EXCEPTION 'option_instruction_conflict' USING ERRCODE='P0001';END IF;
   RETURN security.futures_option_event_outcome(instruction);
  END IF;
  -- A new instruction is filed on an open holding, timed from the trade's execution.
  IF open_lots=0 THEN RAISE EXCEPTION 'option_holding_closed' USING ERRCODE='P0001';END IF;
  IF event_at<r.execution_at THEN RAISE EXCEPTION 'option_instruction_window' USING ERRCODE='P0001';END IF;
  IF series_row.instruction_cutoff_at IS NULL THEN RAISE EXCEPTION 'option_instruction_cutoff_unknown' USING ERRCODE='P0001';END IF;
  -- Both the instruction's business time and its recording time lie at or before the cutoff, so an older frame
  -- cannot file a late instruction.
  IF event_at>series_row.instruction_cutoff_at OR clock_timestamp()>series_row.instruction_cutoff_at THEN RAISE EXCEPTION 'option_instruction_closed' USING ERRCODE='P0001';END IF;
 ELSE
  IF event_at<r.execution_at OR security.futures_option_market_date(future_product,event_at)>series_row.last_trading_date
  THEN RAISE EXCEPTION 'option_exercise_window' USING ERRCODE='P0001';END IF;
  IF NOT security.futures_option_business_day(future_product,security.futures_option_market_date(future_product,event_at)) THEN RAISE EXCEPTION 'option_calendar' USING ERRCODE='P0001';END IF;
  IF (event_kind='assignment')=(holder>0) THEN RAISE EXCEPTION 'option_event_side' USING ERRCODE='P0001';END IF;
  IF event_lots>open_lots THEN RAISE EXCEPTION 'option_lots_exceeded' USING ERRCODE='P0001';END IF;
 END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,ordinal,pg_current_xact_id());
 -- A call exercised or a put assigned is a long future; a put exercised or a call assigned is short.
 IF event_kind IN('exercise','assignment') OR decided='exercised' THEN
  IF EXISTS(SELECT 1 FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,security.futures_option_event_operation(event_request))) OR
   EXISTS(SELECT 1 FROM trading.trade_revisions WHERE(catalog_id,operation_id)=(catalog,security.futures_option_event_operation(event_request)))
  THEN RAISE EXCEPTION 'option_conflict' USING ERRCODE='P0001';END IF;
  future_trade=security.record_futures_trade(catalog,security.futures_option_event_operation(event_request),c.actor_id,
   jsonb_build_object('optionEvent',request_value,'optionEventOperation',event_request,'settlementObservation',settlement.id,'implementation','futures_option_exercise_v1'),
   r.book_id,r.definition_revision_id,curve,future_contract.id,event_at,event_at,event_lots*holder*CASE series_row.option_right WHEN 'call' THEN 1 ELSE -1 END,
   series_row.strike,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,
   jsonb_build_object('legalEntityId',r.legal_entity_id,'legalEntityRevision',r.legal_entity_revision,'counterpartyId',r.counterparty_id,'counterpartyRevision',r.counterparty_revision),ordinal);
 END IF;
 INSERT INTO trading.futures_option_events VALUES(catalog,gen_random_uuid(),trade,r.revision,event_request,event_kind,event_lots,
  CASE WHEN event_kind IN('exercise_instruction','abandon_instruction') THEN open_lots ELSE open_lots-event_lots END,event_at,decided,
  CASE WHEN event_kind='expiry' THEN instruction.id END,rule.catalog_id,rule.revision_id,rule.code,settlement.id,settlement.value,snapshot,future_trade,
  c.actor_id,request_value,ordinal,event_at)
  RETURNING * INTO e;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,'BookTrade') OR NOT security.market_access(catalog,'ReadMarketData') OR
  NOT security.reference_access(option_contract.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Booking access expired' USING ERRCODE='42501';END IF;
 RETURN security.futures_option_event_outcome(e);
END$$;
DO $$BEGIN
 IF (SELECT pg_get_userbyid(proowner) FROM pg_proc WHERE oid='security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)'::regprocedure)<>'openetrm_security_owner'
  OR has_function_privilege('public','security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)','EXECUTE') OR has_function_privilege('openetrm_runtime','security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)','EXECUTE')
 THEN RAISE EXCEPTION 'option_event_function_privileges';END IF;
END$$;
COMMIT;
