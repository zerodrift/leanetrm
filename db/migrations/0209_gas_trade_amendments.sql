-- Future physical gas amendments retain original bookings and exact prior revisions.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='20s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('security.lock_side_schedule_request(uuid,uuid)','6da11c624e40562151451524a1745764faa54fb55dd893d2c89bbef919cb525c','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb),
  ('security.gas_operation_status(uuid,uuid)','aaa94b36a4dc900476454af5337ed438ac906f700d8d099908b3276a042b3236','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb),
  ('security.publish_side_schedules(uuid,uuid,uuid)','b3d60600a1908003da96468f24b39548b6ed166cbf1e48468afdb3686c0fd562','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb),
  ('security.book_gas_composition(uuid,jsonb,timestamp with time zone)','7b854f999df8d1e8d11f7b1ad202c2700a306911385ec0cc952c2e5ffddd5076','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb),
  ('security.require_gas_quantity_trade(uuid,uuid,boolean)','304db27789bd09536fcb7379d64cf2b3fabbd3d923738c26c0bc4142022ac0a8','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb),
  ('security.read_gas_quantity_rows(uuid,uuid,text)','036fda4502db3cb0a4f7fe3b17ab7c942fdbdc69dae069d105df4d638508784a','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb)
 )v(signature,hash,acl,config) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND p.proowner='openetrm_security_owner'::regrole AND to_jsonb(p.proacl)=helper.acl AND to_jsonb(p.proconfig)=helper.config
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- The shared request lock recognizes the exact physical gas head and retained
-- command, as it already does for the other amendable instrument families.
DO $$DECLARE definition text=pg_get_functiondef('security.lock_side_schedule_request(uuid,uuid)'::regprocedure);before_text text;after_text text;BEGIN
 before_text=$before$UNION ALL SELECT revision FROM trading.ppa_trade_revisions WHERE(catalog_id,trade_id)=(catalog,command.trade_id)) r;$before$;
 after_text=$after$UNION ALL SELECT revision FROM trading.ppa_trade_revisions WHERE(catalog_id,trade_id)=(catalog,command.trade_id)
   UNION ALL SELECT revision FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,command.trade_id)) r;$after$;
 IF (length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1 THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected request lock' USING ERRCODE='55000';END IF;
 definition=replace(definition,before_text,after_text);
 before_text=$before$THEN RAISE EXCEPTION 'side_schedule_revision_conflict';$before$;
 after_text=$after$AND NOT EXISTS(SELECT 1 FROM trading.gas_amendment_commands WHERE(catalog_id,operation_id,trade_id,actor_id,expected_revision)=(catalog,operation,command.trade_id,command.actor_id,command.expected_revision))
  THEN RAISE EXCEPTION 'side_schedule_revision_conflict';$after$;
 IF (length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1 THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected request lock' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before_text,after_text);
END$$;
DO $$DECLARE c record;BEGIN
 FOR c IN SELECT * FROM(VALUES
  ('trading.gas_trade_revisions','gas_trade_revisions_revision_check','5568929f3e547e580cbad7387a5dfa848689b487d5eba273db7bb44f6da82865'),
  ('trading.gas_delivery_obligations','gas_delivery_obligations_catalog_id_trade_id_slot_key','96d2b1eb94d436a975c6a5445114027676029503d9c407f2c9f02a9524dde778')
 )v(relation,name,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid=c.relation::regclass AND conname=c.name AND convalidated
   AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')=c.hash)
  THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected constraint %',c.name USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
DO $$BEGIN IF to_regprocedure('security.write_gas_composition(uuid,jsonb,timestamp with time zone)') IS NOT NULL THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected private writer' USING ERRCODE='55000';END IF;END$$;
ALTER TABLE trading.gas_trade_revisions DROP CONSTRAINT gas_trade_revisions_revision_check,
 ADD CONSTRAINT gas_trade_revisions_revision_check CHECK(revision BETWEEN 1 AND 256),
 ADD UNIQUE(catalog_id,trade_id,revision,operation_id);
ALTER TABLE trading.gas_delivery_obligations DROP CONSTRAINT gas_delivery_obligations_catalog_id_trade_id_slot_key,
 ADD UNIQUE(catalog_id,trade_id,origin_revision,slot);
CREATE TABLE trading.gas_amendment_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 expected_revision integer NOT NULL,expected_instrument_id uuid NOT NULL,result_revision integer NOT NULL,result_instrument_id uuid NOT NULL,result_operation_id uuid,
 changed boolean NOT NULL,reason text NOT NULL CHECK(length(btrim(reason)) BETWEEN 1 AND 500),request jsonb NOT NULL CHECK(pg_column_size(request)<=65536),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id,expected_revision,expected_instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,trade_id,result_revision,result_instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(catalog_id,trade_id,result_revision,result_operation_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,operation_id),
 CHECK((changed AND result_operation_id IS NOT NULL AND result_operation_id=operation_id AND result_revision=expected_revision+1) OR
  (NOT changed AND result_operation_id IS NULL AND result_revision=expected_revision AND result_instrument_id=expected_instrument_id))
);
ALTER TABLE trading.gas_amendment_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_amendment_commands FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON trading.gas_amendment_commands TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON trading.gas_amendment_commands FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(changed AND EXISTS(
 SELECT 1 FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision,r.operation_id,r.instrument_id)=
 (gas_amendment_commands.catalog_id,gas_amendment_commands.trade_id,gas_amendment_commands.result_revision,gas_amendment_commands.operation_id,gas_amendment_commands.result_instrument_id)));
CREATE TRIGGER immutable_command BEFORE UPDATE OR DELETE ON trading.gas_amendment_commands FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE POLICY risk_worker_service ON trading.gas_amendment_commands AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
GRANT SELECT ON trading.gas_amendment_commands TO openetrm_runtime,openetrm_risk_worker;
GRANT SELECT,INSERT ON trading.gas_amendment_commands TO openetrm_security_owner;

-- One eligibility rule serves the working ticket and the locked native write.
-- Existing physical occurrences are never silently carried to replacement Sides.
CREATE FUNCTION security.gas_amendment_refusal(catalog uuid,trade uuid,expected integer,binding uuid,valuation timestamptz) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE h trading.gas_trade_revisions;i trading.gas_instruments;BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'No gas read authority' USING ERRCODE='42501';END IF;
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE THEN RETURN 'gas_amendment_denied';END IF;
 SELECT * INTO h FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RETURN 'gas_revision_conflict';END IF;
 SELECT * INTO STRICT i FROM trading.gas_instruments WHERE(catalog_id,instrument_id)=(catalog,h.instrument_id);
 IF NOT i.composition THEN RETURN 'gas_amendment_legacy';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,h.instrument_id) AND NOT EXISTS(
  SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(catalog,m.definition_revision_id,m.assignment_code)
  AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_access(a.reference_catalog_id,'ReadMarketData')
  AND security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) AND security.gas_setup_visible(a.catalog_id,a.setup_id)))
 THEN RAISE EXCEPTION 'No current gas source authority' USING ERRCODE='42501';END IF;
 IF(h.revision,h.instrument_id) IS DISTINCT FROM(expected,binding) OR valuation IS NULL OR NOT isfinite(valuation) OR valuation<h.effective_at THEN RETURN 'gas_revision_conflict';END IF;
 IF EXISTS(SELECT 1 FROM trading.instrument_side_schedules s JOIN trading.side_schedule_grids g USING(catalog_id,schedule_id)
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(s.catalog_id,s.instrument_id,s.slot)
  WHERE(s.catalog_id,s.instrument_id)=(catalog,h.instrument_id) AND m.role='physical' AND g.origin<=valuation)
 THEN RETURN 'gas_amendment_started';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_quantity_commands WHERE(catalog_id,trade_id)=(catalog,trade)) OR
 EXISTS(SELECT 1 FROM trading.gas_service_accounts WHERE(catalog_id,trade_id)=(catalog,trade)) OR
 EXISTS(SELECT 1 FROM trading.gas_operation_commands WHERE(catalog_id,trade_id)=(catalog,trade)) OR
 EXISTS(SELECT 1 FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.trade_id)=(catalog,trade) AND(
  EXISTS(SELECT 1 FROM trading.gas_quantity_series s WHERE(s.catalog_id,s.obligation_id)=(o.catalog_id,o.id)) OR
  EXISTS(SELECT 1 FROM trading.facility_gas_delivery_scopes l WHERE(l.catalog_id,l.origin_obligation_id)=(o.catalog_id,o.id)) OR
  EXISTS(SELECT 1 FROM trading.facility_gas_targets t WHERE(t.catalog_id,t.obligation_id)=(o.catalog_id,o.id)) OR
  EXISTS(SELECT 1 FROM trading.facility_quantity_commands c WHERE(c.catalog_id,c.create_obligation_id)=(o.catalog_id,o.id))))
 THEN RETURN 'gas_amendment_operational';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamp with time zone) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamp with time zone) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamp with time zone) TO openetrm_runtime;

-- Share the existing admitted builder; retain every unrelated source, quantity,
-- unit, graph, clock, lifecycle and setup check in the pinned predecessor.
DO $patch$ DECLARE definition text=pg_get_functiondef('security.book_gas_composition(uuid,jsonb,timestamp with time zone)'::regprocedure);c record;BEGIN
 FOR c IN SELECT * FROM(VALUES
($before$CREATE OR REPLACE FUNCTION security.book_gas_composition($before$,$after$CREATE FUNCTION security.write_gas_composition($after$),
($before$DECLARE q security.request_contexts;$before$,$after$DECLARE amending boolean=(body->>'tradeId') IS NOT NULL;permission text;amend_head trading.gas_trade_revisions;completed trading.gas_amendment_commands;refusal text;unchanged boolean=false;outcome jsonb;q security.request_contexts;$after$),
($before$ IF NOT security.trading_access(catalog,'BookTrade') OR NOT security.trading_access(catalog,'ReadTrades')$before$,$after$ permission=CASE WHEN amending THEN 'AmendTrade' ELSE 'BookTrade' END;
 IF NOT security.trading_access(catalog,permission) OR NOT security.trading_access(catalog,'ReadTrades')$after$),
($before$ SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation);$before$,$after$ SELECT * INTO completed FROM trading.gas_amendment_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF NOT amending OR completed.actor_id<>q.actor_id OR completed.request IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_conflict';END IF;
  RETURN completed.outcome;
 END IF;
 SELECT * INTO prior FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation);$after$),
($before$'physicalSides','cashSides'))$before$,$after$'physicalSides','cashSides','tradeId','expectedRevision','bindingId','reason','amendmentPolicy'))$after$),
($before$ IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN market_data.revision_periods p$before$,$after$ IF amending THEN
  IF body->>'amendmentPolicy' IS DISTINCT FROM 'future_gas_composition_replace_v1' OR body->>'expectedRevision' IS NULL OR body->>'bindingId' IS NULL OR body->>'reason' IS NULL OR length(btrim(body->>'reason')) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'gas_amendment_input';END IF;
  refusal=security.gas_amendment_refusal(catalog,(body->>'tradeId')::uuid,(body->>'expectedRevision')::integer,(body->>'bindingId')::uuid,valuation);
  IF refusal IS NOT NULL THEN RAISE EXCEPTION '%',refusal;END IF;
  SELECT * INTO STRICT amend_head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id,revision)=(catalog,(body->>'tradeId')::uuid,(body->>'expectedRevision')::integer);
  IF NOT EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id,i.definition_revision_id)=(catalog,amend_head.instrument_id,definition)) THEN RAISE EXCEPTION 'gas_amendment_binding';END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN market_data.revision_periods p$after$),
($before$stage.permission<>'BookTrade' OR stage.command_hash$before$,$after$stage.permission<>permission OR stage.trade_id IS DISTINCT FROM (CASE WHEN amending THEN amend_head.trade_id ELSE NULL END) OR stage.expected_revision IS DISTINCT FROM (CASE WHEN amending THEN amend_head.revision ELSE NULL END) OR stage.command_hash$after$),
($before$ IF(SELECT count(*) FROM trading.gas_trade_revisions WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'gas_limit';END IF;$before$,$after$ IF NOT amending AND(SELECT count(*) FROM trading.gas_trade_revisions WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'gas_limit';END IF;$after$),
($before$ SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;$before$,$after$ BEGIN
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;$after$),
($before$instrument=gen_random_uuid();trade=gen_random_uuid();$before$,$after$instrument=gen_random_uuid();trade=CASE WHEN amending THEN amend_head.trade_id ELSE gen_random_uuid() END;$after$),
($before$ PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades VALUES(catalog,trade,book,operation,q.actor_id,body,publication,valuation);$before$,$after$ -- Equality is proved from admitted typed memberships and native quantities,
 -- not raw JSON formatting. Roll back the candidate publication on a true no-op.
 IF amending AND valuation=amend_head.effective_at AND(amend_head.quantity,amend_head.book_id,amend_head.legal_entity_id,amend_head.legal_entity_revision,amend_head.counterparty_id,amend_head.counterparty_revision)=
  (base,book,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer)
  AND EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id,i.definition_revision_id)=(catalog,amend_head.instrument_id,definition) AND i.gas_setup_id IS NOT DISTINCT FROM setup)
  AND NOT EXISTS((SELECT slot,side_id FROM trading.gas_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument) EXCEPT ALL SELECT slot,side_id FROM trading.gas_memberships WHERE(catalog_id,instrument_id)=(catalog,amend_head.instrument_id)) UNION ALL
   (SELECT slot,side_id FROM trading.gas_memberships WHERE(catalog_id,instrument_id)=(catalog,amend_head.instrument_id) EXCEPT ALL SELECT slot,side_id FROM trading.gas_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument)))
  AND NOT EXISTS((SELECT to_jsonb(m)-'instrument_id' FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) EXCEPT ALL SELECT to_jsonb(m)-'instrument_id' FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,amend_head.instrument_id)) UNION ALL
   (SELECT to_jsonb(m)-'instrument_id' FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,amend_head.instrument_id) EXCEPT ALL SELECT to_jsonb(m)-'instrument_id' FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)))
 THEN RAISE EXCEPTION 'gas_unchanged' USING ERRCODE='P0300';END IF;
 IF amending AND(amend_head.revision>=256 OR(SELECT count(*) FROM trading.gas_trade_revisions WHERE catalog_id=catalog)>=256) THEN RAISE EXCEPTION 'gas_amendment_limit';END IF;
 IF NOT amending THEN
  PERFORM security.stamp_trade_booking(catalog);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,q.actor_id,body,publication,valuation);
 END IF;$after$),
($before$VALUES(catalog,trade,1,instrument,book,operation,q.actor_id,base,NULL,$before$,$after$VALUES(catalog,trade,CASE WHEN amending THEN amend_head.revision+1 ELSE 1 END,instrument,book,operation,q.actor_id,base,NULL,$after$),
($before$ IF NOT security.trading_access(catalog,'BookTrade') THEN RAISE EXCEPTION 'Composition booking authority expired' USING ERRCODE='42501';END IF;$before$,$after$ EXCEPTION WHEN SQLSTATE 'P0300' THEN
  IF SQLERRM<>'gas_unchanged' THEN RAISE;END IF;
  unchanged=true;instrument=amend_head.instrument_id;trade=amend_head.trade_id;
 END;
 IF amending THEN
  outcome=jsonb_build_object('tradeId',trade,'instrumentId',instrument,'revision',amend_head.revision+CASE WHEN unchanged THEN 0 ELSE 1 END,'operationId',operation,'unchanged',unchanged);
  INSERT INTO trading.gas_amendment_commands VALUES(catalog,operation,trade,q.actor_id,amend_head.revision,amend_head.instrument_id,amend_head.revision+CASE WHEN unchanged THEN 0 ELSE 1 END,instrument,CASE WHEN unchanged THEN NULL ELSE operation END,NOT unchanged,body->>'reason',body,outcome);
 END IF;
 IF NOT security.trading_access(catalog,permission) OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Composition write authority expired' USING ERRCODE='42501';END IF;
 IF amending THEN RETURN outcome;END IF;$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected builder' USING ERRCODE='55000';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;EXECUTE definition;
END$patch$;
ALTER FUNCTION security.write_gas_composition(uuid,jsonb,timestamp with time zone) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.write_gas_composition(uuid,jsonb,timestamp with time zone) FROM PUBLIC;
CREATE OR REPLACE FUNCTION security.book_gas_composition(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$BEGIN
 IF body ?| ARRAY['tradeId','expectedRevision','bindingId','reason','amendmentPolicy'] THEN RAISE EXCEPTION 'gas_composition_input';END IF;
 RETURN security.write_gas_composition(catalog,body,valuation);
END$$;
CREATE FUNCTION security.amend_gas_composition(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$BEGIN
 IF body->>'tradeId' IS NULL THEN RAISE EXCEPTION 'gas_amendment_input';END IF;
 RETURN security.write_gas_composition(catalog,body,valuation);
END$$;
ALTER FUNCTION security.amend_gas_composition(uuid,jsonb,timestamp with time zone) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.amend_gas_composition(uuid,jsonb,timestamp with time zone) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.amend_gas_composition(uuid,jsonb,timestamp with time zone) TO openetrm_runtime;
CREATE OR REPLACE FUNCTION security.gas_operation_status(catalog uuid, operation uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
 SET jit TO 'off'
AS $function$
DECLARE r trading.gas_trade_revisions;completed trading.gas_amendment_commands;actor uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No gas read access' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO completed FROM trading.gas_amendment_commands WHERE(catalog_id,operation_id,actor_id)=(catalog,operation,actor);
 IF FOUND THEN RETURN completed.outcome;END IF;
 SELECT * INTO r FROM trading.gas_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation) AND actor_id=actor;
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false);END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Gas read authority expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('tradeId',r.trade_id,'instrumentId',r.instrument_id,'revision',r.revision,'operationId',operation);
END$function$
;

CREATE OR REPLACE FUNCTION security.publish_side_schedules(catalog uuid, operation uuid, instrument uuid)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE staged trading.side_schedule_requests;accepted jsonb;
BEGIN
 staged=security.side_schedule_request_authority(catalog,operation);
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT request INTO accepted FROM(
  SELECT r.request FROM trading.ppa_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,staged.actor_id)
  UNION ALL SELECT r.request-'sideFixings' FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,staged.actor_id)
  UNION ALL SELECT CASE WHEN r.revision=1 AND t.operation_id=r.operation_id THEN t.request ELSE a.request END
   FROM trading.gas_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   LEFT JOIN trading.gas_amendment_commands a ON(a.catalog_id,a.operation_id,a.trade_id,a.actor_id,a.result_revision,a.result_instrument_id,a.result_operation_id,a.changed)=
    (r.catalog_id,r.operation_id,r.trade_id,r.actor_id,r.revision,r.instrument_id,r.operation_id,true)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)
  UNION ALL SELECT t.request FROM trading.crude_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)
  UNION ALL SELECT t.request FROM trading.lng_trade_revisions r JOIN trading.trades t ON(t.catalog_id,t.id)=(r.catalog_id,r.trade_id)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,r.instrument_id)=(catalog,operation,staged.actor_id,instrument)
 ) booking;
 IF accepted IS NULL OR sha256(convert_to(accepted::text,'UTF8')) IS DISTINCT FROM staged.command_hash
 THEN RAISE EXCEPTION 'side_schedule_command';END IF;
 PERFORM security.publish_admitted_side_schedules(catalog,operation,instrument);
END$function$
;

-- Keep the admitted batch read and resolve its selected revision once.
DO $$DECLARE definition text=pg_get_functiondef('security.read_gas_quantity_rows(uuid,uuid,text)'::regprocedure);
 before_text text='r.effective_at<=q.trading_as_of;';BEGIN
 IF (length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1 THEN RAISE EXCEPTION 'gas_amendment_upgrade: unexpected quantity read' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,before_text,'r.effective_at<=q.trading_as_of ORDER BY r.revision DESC LIMIT 1;');
END$$;

CREATE OR REPLACE FUNCTION security.require_gas_quantity_trade(catalog uuid,trade uuid,writing boolean) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$DECLARE h trading.gas_trade_revisions;BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR(writing AND security.trading_access(catalog,'AmendTrade') IS NOT TRUE) THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
 IF writing AND NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND(security.current_write_context() OR security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)) ORDER BY r.revision DESC LIMIT 1;
 IF h.trade_id IS NULL OR NOT EXISTS(SELECT 1 FROM trading.gas_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,h.instrument_id) AND i.composition)
 OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,h.publication_order))
 OR EXISTS(SELECT 1 FROM trading.gas_component_terms m WHERE(m.catalog_id,m.instrument_id)=(catalog,h.instrument_id) AND NOT EXISTS(
  SELECT 1 FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(catalog,m.definition_revision_id,m.assignment_code)
  AND security.market_fact_visible(a.catalog_id,a.revision_id,NULL) AND security.reference_access(a.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) AND security.gas_setup_visible(a.catalog_id,a.setup_id)))
 THEN RAISE EXCEPTION 'gas_quantity_trade';END IF;
END$$;
COMMIT;
