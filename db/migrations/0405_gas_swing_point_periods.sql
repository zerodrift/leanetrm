-- The selected swing source can consume a native day or month, uniquely covering its gas day.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE predecessor record;p pg_proc;
BEGIN
 FOR predecessor IN SELECT * FROM (VALUES
  ('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','1918e843d3f761ae794312ab0ce254df33ef0197aedd8004e1893454bc701098',false,1),
  ('security.record_gas_cash_payment(uuid,jsonb,timestamptz)','13be11b88df674cdc4c978863457a9fa141298cccfb8dd2d7c9d1c57bacebd58',true,1),
  ('security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamptz)','465aa7c77e98dd27ed8a897780b535a0b6dc12bae5b134638c4f991c56574faa',true,1),
  ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric,date[],timestamptz[])','dafd10b08d16657446764c27bd196a6b55099692675af9516bbbd537e6484a7b',false,4)
 )v(signature,digest,runtime,overloads) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(predecessor.signature);
  IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>predecessor.digest
   OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM
     (CASE WHEN predecessor.runtime THEN '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' ELSE '{openetrm_security_owner=X/openetrm_security_owner}' END)
   OR (SELECT count(*) FROM pg_proc q WHERE(q.pronamespace,q.proname)=(p.pronamespace,p.proname))<>predecessor.overloads
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_swing_period_upgrade: unexpected helper %',predecessor.signature;END IF;
 END LOOP;
END$guard$;

DO $shape$
DECLARE signature text='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric,date[],timestamptz[]';
 original text=pg_get_functiondef((signature||')')::regprocedure); definition text=original;change record;
BEGIN
 IF to_regprocedure(signature||',text)') IS NOT NULL THEN RAISE EXCEPTION 'gas_swing_period_upgrade: unexpected overload';END IF;
 FOR change IN SELECT * FROM (VALUES
  ('charge_due timestamp with time zone[])','charge_due timestamp with time zone[], swing_source text)'),
  ('SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m',
   'BEGIN SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m'),
  ($old$AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date;$old$,
   $new$AND ((c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date)
      OR (swing_source IS NOT NULL AND c.delivery_to=c.delivery_from+1))
 AND (swing_source IS NULL OR NOT EXISTS(SELECT FROM market_data.contract_intervals i
      WHERE(i.catalog_id,i.revision_id,i.contract_id)=(c.catalog_id,c.revision_id,c.id)));
 EXCEPTION WHEN no_data_found OR too_many_rows THEN
  IF swing_source IS NULL THEN RAISE;END IF;
  RAISE EXCEPTION 'gas_composition_input' USING DETAIL=jsonb_build_object('side',swing_source,'field','delivery_point')::text;
 END;$new$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>1 THEN RAISE EXCEPTION 'gas_swing_period_upgrade: shape patch mismatch';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;
 EXECUTE definition;
 EXECUTE 'ALTER FUNCTION '||signature||',text) OWNER TO openetrm_security_owner';
 EXECUTE 'REVOKE ALL ON FUNCTION '||signature||',text) FROM PUBLIC';
 EXECUTE split_part(original,'AS $function$',1)||$wrapper$AS $function$
BEGIN
 PERFORM trading.require_gas_program_shape(catalog,schedule,slot,first_day,last_day,payment,unit_total,literal,assignment,delivery,start_at,forecast,unit_denominator,charge_ends,charge_due,NULL::text);
END$function$ $wrapper$;
END$shape$;

-- Session working frames are private API selections. Only the identity issuer can
-- attach one to an unbound publication request. Direct native callers use the
-- visible recorded close. Both paths retain their original deadline until commit.
CREATE TABLE security.gas_swing_booking_frames(
 request_id uuid NOT NULL REFERENCES security.request_contexts(id) ON DELETE CASCADE,
 catalog_id uuid NOT NULL, operation_id uuid NOT NULL, command_hash bytea NOT NULL,
 frame_id uuid NOT NULL, valuation_at timestamptz NOT NULL, authority_until timestamptz NOT NULL,
 permission text NOT NULL CHECK(permission IN('BookTrade','AmendTrade')), admitted boolean NOT NULL DEFAULT false, PRIMARY KEY(request_id,operation_id)
);
CREATE UNIQUE INDEX gas_swing_issued_frame ON security.gas_swing_booking_frames(request_id) WHERE NOT admitted;
ALTER TABLE security.gas_swing_booking_frames ENABLE ROW LEVEL SECURITY;
ALTER TABLE security.gas_swing_booking_frames FORCE ROW LEVEL SECURITY;
CREATE POLICY owner ON security.gas_swing_booking_frames TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT,UPDATE,DELETE ON security.gas_swing_booking_frames TO openetrm_security_owner;

CREATE FUNCTION security.attest_gas_swing_booking_frame(token uuid,source_actor uuid,catalog uuid,body jsonb,frame uuid,at_instant timestamptz,until_at timestamptz)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT pg_has_role(session_user,'openetrm_authenticator','MEMBER') OR pg_has_role(session_user,'openetrm_runtime','MEMBER')
  OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RAISE insufficient_privilege;END IF;
 -- Retain an expired source bound for as long as its publication token can
 -- still bind. A recorded close could otherwise take the direct native path.
 DELETE FROM security.gas_swing_booking_frames f WHERE(f.request_id,f.operation_id) IN (
  SELECT prior.request_id,prior.operation_id FROM security.gas_swing_booking_frames prior
  JOIN security.request_contexts q ON q.id=prior.request_id WHERE NOT prior.admitted AND q.expires_at<=clock_timestamp()
  LIMIT 64 FOR UPDATE OF prior SKIP LOCKED);
 INSERT INTO security.gas_swing_booking_frames(request_id,catalog_id,operation_id,command_hash,frame_id,valuation_at,authority_until,permission)
 SELECT q.id,catalog,(body->>'operationId')::uuid,sha256(convert_to(body::text,'UTF8')),frame,at_instant,least(until_at,q.expires_at),
  CASE WHEN body->>'tradeId' IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END
 FROM security.request_contexts q WHERE q.id=token AND q.actor_id=source_actor AND q.backend_pid IS NULL AND q.expires_at>clock_timestamp()
  AND until_at>clock_timestamp() AND isfinite(until_at) AND isfinite(at_instant) AND (body->>'snapshotId')::uuid=frame;
 IF NOT FOUND THEN RAISE insufficient_privilege;END IF;
END$$;
ALTER FUNCTION security.attest_gas_swing_booking_frame(uuid,uuid,uuid,jsonb,uuid,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.attest_gas_swing_booking_frame(uuid,uuid,uuid,jsonb,uuid,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.attest_gas_swing_booking_frame(uuid,uuid,uuid,jsonb,uuid,timestamptz,timestamptz) TO openetrm_authenticator;

CREATE FUNCTION security.admit_gas_swing_booking_frame(token uuid,catalog uuid,body jsonb,claimed timestamptz,until_at timestamptz)
RETURNS timestamptz LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE frame uuid=(body->>'snapshotId')::uuid;op uuid=(body->>'operationId')::uuid;
 digest bytea=sha256(convert_to(body::text,'UTF8'));proof security.gas_swing_booking_frames;owned_at timestamptz;visible boolean;
BEGIN
 SELECT * INTO proof FROM security.gas_swing_booking_frames f WHERE f.request_id=token AND NOT f.admitted;
 IF FOUND THEN
  IF (proof.catalog_id,proof.operation_id,proof.frame_id,proof.command_hash) IS DISTINCT FROM (catalog,op,frame,digest)
   THEN RAISE EXCEPTION 'swing_booking_frame';END IF;
  until_at=least(proof.authority_until,until_at);
 END IF;
 SELECT c.at,security.market_close_visible(c.catalog_id,c.id) INTO owned_at,visible
 FROM market_data.market_closes c WHERE (c.catalog_id,c.id)=(catalog,frame);
 IF FOUND THEN
  IF visible IS NOT TRUE OR proof.request_id IS NOT NULL AND proof.valuation_at IS DISTINCT FROM owned_at
   THEN RAISE EXCEPTION 'swing_booking_frame';END IF;
 ELSE
  owned_at=proof.valuation_at;
 END IF;
 IF owned_at IS NULL OR owned_at IS DISTINCT FROM claimed THEN RAISE EXCEPTION 'swing_booking_frame';END IF;
 IF until_at IS NULL OR clock_timestamp()>=until_at THEN RAISE insufficient_privilege;END IF;
 IF proof.request_id IS NULL THEN
  INSERT INTO security.gas_swing_booking_frames VALUES(token,catalog,op,digest,frame,owned_at,until_at,
   CASE WHEN body->>'tradeId' IS NULL THEN 'BookTrade' ELSE 'AmendTrade' END,true);
 ELSE
  UPDATE security.gas_swing_booking_frames f SET admitted=true,authority_until=until_at WHERE(f.request_id,f.operation_id)=(token,op);
 END IF;
 RETURN owned_at;
END$$;
ALTER FUNCTION security.admit_gas_swing_booking_frame(uuid,uuid,jsonb,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.admit_gas_swing_booking_frame(uuid,uuid,jsonb,timestamptz,timestamptz) FROM PUBLIC;

CREATE FUNCTION security.commit_gas_swing_booking_frame() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.trading_access(NEW.catalog_id,NEW.permission) IS NOT TRUE OR security.trading_access(NEW.catalog_id,'ReadTrades') IS NOT TRUE
  OR security.market_access(NEW.catalog_id,'ReadMarketData') IS NOT TRUE OR NOT EXISTS(SELECT 1 FROM security.request_contexts q
  WHERE q.id=NEW.request_id AND q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
  AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()) OR clock_timestamp()>=NEW.authority_until
 THEN RAISE insufficient_privilege;END IF;
 DELETE FROM security.gas_swing_booking_frames f WHERE (f.request_id,f.operation_id)=(NEW.request_id,NEW.operation_id);
 RETURN NULL;
END$$;
ALTER FUNCTION security.commit_gas_swing_booking_frame() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.commit_gas_swing_booking_frame() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER commit_frame AFTER INSERT OR UPDATE ON security.gas_swing_booking_frames DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW WHEN (NEW.admitted) EXECUTE FUNCTION security.commit_gas_swing_booking_frame();

DO $patch$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);c record;
BEGIN
 FOR c IN SELECT * FROM(VALUES
 ($before$'exerciseRights','chargeSides','imbalanceCashouts'))$before$,$after$'exerciseRights','chargeSides','imbalanceCashouts','swingRights'))$after$),
 ($before$DECLARE amending boolean=$before$,$after$DECLARE swing_until timestamptz;amending boolean=$after$),
 ($before$ PERFORM pg_advisory_xact_lock(730849102);$before$,$after$ IF body->'swingRights'->0 IS NOT NULL THEN
  swing_until=least(security.authority_until(permission),security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);$after$),
 ($before$ IF amending THEN
  IF body->>'amendmentPolicy'$before$,$after$ IF body->'swingRights'->0 IS NOT NULL THEN
  valuation=security.admit_gas_swing_booking_frame(q.id,catalog,body,valuation,swing_until);
 END IF;
 IF amending THEN
  IF body->>'amendmentPolicy'$after$),
 ($before$ -- Equality is proved$before$,$after$ PERFORM trading.save_gas_swing(catalog,instrument,operation,body->'swingRights',valuation);
 -- Equality is proved$after$),
 ($before$   trading.gas_exercise_terms(catalog,instrument)=$before$,$after$   trading.gas_swing_terms(catalog,instrument)=trading.gas_swing_terms(catalog,amend_head.instrument_id) AND
   trading.gas_exercise_terms(catalog,instrument)=$after$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,c.before_text,'')))/length(c.before_text)<>1 THEN RAISE EXCEPTION 'gas_swing_upgrade: patch mismatch';END IF;
  definition=replace(definition,c.before_text,c.after_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

DO $writer$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);
 original text=$old$PERFORM trading.require_gas_program_shape(catalog,schedule,0,first_day,last_day,end_at,unit_total,NULL,a,d,start_at,item->'forecastCurve');$old$;
BEGIN
 IF (length(definition)-length(replace(definition,original,'')))/length(original)<>1 THEN RAISE EXCEPTION 'gas_swing_period_upgrade: writer patch mismatch';END IF;
 EXECUTE replace(definition,original,$new$IF body->'swingRights'->0->>'side'=item->>'code' THEN
    IF item->'forecastCurve' IS NOT NULL AND item->'forecastCurve'<>'null'::jsonb THEN RAISE EXCEPTION 'swing_model_scope';END IF;
    PERFORM trading.require_gas_program_shape(catalog,schedule,0,first_day,last_day,end_at,unit_total,NULL,a,d,start_at,item->'forecastCurve',1,NULL,NULL,item->>'code');
   ELSE $new$||original||' END IF;');
END$writer$;
-- New cash admission uses the same selected, certified commercial facts as the held valuation.
-- The predecessor's two replay branches run before this check, preserving already admitted payment lineages.
CREATE FUNCTION trading.require_gas_swing_payment(catalog uuid,trade uuid,binding uuid,slot_number integer,
 allocations jsonb,paid timestamptz,as_of timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE right_no integer;cash_schedule uuid;caller security.request_contexts;facts jsonb;
BEGIN
 SELECT r.ordinal INTO right_no FROM trading.gas_swing_rights r
 JOIN trading.gas_swing_members m USING(catalog_id,instrument_id,ordinal)
 WHERE(r.catalog_id,r.instrument_id,m.slot)=(catalog,binding,slot_number);
 IF NOT FOUND THEN RETURN;END IF;
 SELECT schedule_id INTO STRICT cash_schedule FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=(catalog,binding,slot_number);
 SELECT * INTO STRICT caller FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 PERFORM security.use_market_context(catalog,security.resolve_operation_context(catalog,(as_of AT TIME ZONE 'UTC')::date,NULL));
 PERFORM security.use_trading_context(catalog,as_of);
 facts=security.read_gas_swing_elections(catalog,trade,binding);
 IF EXISTS(SELECT FROM jsonb_array_elements(allocations) a WHERE NOT EXISTS(
   SELECT FROM trading.side_schedule_rows cash JOIN trading.gas_swing_days d ON(d.catalog_id,d.instrument_id,d.ordinal)=(catalog,binding,right_no)
     AND d.day>=cash.delivery_from AND d.day<cash.delivery_to
   JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(d.catalog_id,d.schedule_id,d.grid_ordinal)
   JOIN jsonb_array_elements(facts->'elections') e ON(e->>'day')::date=d.day AND e->>'quantity' IS NOT NULL
   WHERE(cash.catalog_id,cash.schedule_id,cash.operation,cash.payment_ordinal)=(catalog,cash_schedule,'output_guard',(a->>'occurrence')::integer)
     AND g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond'<=paid))
 THEN RAISE EXCEPTION 'gas_payment_occurrence';END IF;
 PERFORM security.require_gas_swing_binding(catalog,trade,binding);
 PERFORM settlement_data.require_futures_margin_preview(catalog,(facts->>'authorityUntil')::timestamptz);
 UPDATE security.request_contexts SET market_catalog_id=caller.market_catalog_id,market_context_id=caller.market_context_id,
  trading_as_of=caller.trading_as_of,trading_business_date=caller.trading_business_date,calendar_cut=caller.calendar_cut,read_sources=caller.read_sources
  WHERE id=caller.id;
END$$;
ALTER FUNCTION trading.require_gas_swing_payment(uuid,uuid,uuid,integer,jsonb,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_swing_payment(uuid,uuid,uuid,integer,jsonb,timestamptz,timestamptz) FROM PUBLIC;

DO $cash$
DECLARE change record;definition text;
BEGIN
 FOR change IN SELECT * FROM (VALUES
  ('security.record_gas_cash_payment(uuid,jsonb,timestamptz)',
   ' IF(SELECT count(*) FROM trading.gas_cash_payments WHERE',
   $new$ PERFORM trading.require_gas_swing_payment(catalog,trade,head.instrument_id,slot_number,body->'allocations',paid,as_of);
$new$),
  ('security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamptz)',
   ' IF EXISTS(SELECT 1 FROM trading.instrument_side_schedules s JOIN trading.side_schedule_grids g',
   $new$ IF EXISTS(SELECT FROM trading.gas_swing_elections e WHERE(e.catalog_id,e.trade_id)=(catalog,trade))
 THEN RETURN 'gas_amendment_swing';END IF;
$new$)
 ) v(signature,anchor,addition) LOOP
  definition=pg_get_functiondef(change.signature::regprocedure);
  IF (length(definition)-length(replace(definition,change.anchor,'')))/length(change.anchor)<>1
  THEN RAISE EXCEPTION 'gas_swing_period_upgrade: patch mismatch %',change.signature;END IF;
  EXECUTE replace(definition,change.anchor,change.addition||change.anchor);
 END LOOP;
END$cash$;

GRANT SELECT ON trading.gas_swing_rights,trading.gas_swing_members,trading.gas_swing_days,trading.gas_swing_pricing
 TO openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION trading.gas_swing_terms(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.gas_swing_write_until(uuid),security.require_gas_swing_receipt(uuid,uuid,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.record_gas_swing_elections(uuid,jsonb,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.require_gas_swing_binding(uuid,uuid,uuid),security.read_gas_swing_elections(uuid,uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
