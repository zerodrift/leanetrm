-- A correction names the latest revision known in the ticket's read, including
-- a pending or withdrawn instruction. Valuation keeps its effective-time head.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE FUNCTION security.gas_exercise_action_periods(catalog uuid,trade uuid,instrument uuid,expected_revision integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off SET timezone='UTC' AS $function$
DECLARE as_of timestamptz;result jsonb;
BEGIN
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 -- Reuse the election reader's bound context, current source visibility and exact binding admission.
 PERFORM security.read_gas_exercise_elections(catalog,trade,instrument);
 IF (SELECT r.revision FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
     AND security.trading_visible(catalog,r.publication_order,r.effective_at) ORDER BY r.revision DESC LIMIT 1) IS DISTINCT FROM expected_revision
 THEN RAISE EXCEPTION 'exercise_election_conflict';END IF;
 SELECT q.trading_as_of INTO STRICT as_of FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 WITH heads AS MATERIALIZED(
  SELECT DISTINCT ON(e.ordinal,e.period_from) e.ordinal,e.period_from,e.id,e.outcome,e.effective_at
  FROM trading.gas_exercise_elections e WHERE(e.catalog_id,e.trade_id,e.instrument_id)=(catalog,trade,instrument)
   AND security.trading_visible(catalog,e.publication_order,as_of) ORDER BY e.ordinal,e.period_from,e.revision DESC
 )
 SELECT coalesce(jsonb_agg(jsonb_build_object('right',g.ordinal,'from',g.period_from,'to',g.period_to,'exerciseAt',g.exercise_at,
   'previousId',h.id,'previousOutcome',h.outcome,'previousEffectiveAt',h.effective_at) ORDER BY g.ordinal,g.period_from),'[]') INTO result
 FROM trading.gas_exercise_groups g JOIN trading.gas_exercise_rights r USING(catalog_id,instrument_id,ordinal)
 LEFT JOIN heads h USING(ordinal,period_from) WHERE(g.catalog_id,g.instrument_id,r.settlement)=(catalog,instrument,'physical');
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.gas_component_sources_visible(catalog,instrument) IS NOT TRUE
 THEN RAISE EXCEPTION 'exercise_election_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$function$;
ALTER FUNCTION security.gas_exercise_action_periods(uuid,uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_exercise_action_periods(uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_exercise_action_periods(uuid,uuid,uuid,integer) TO openetrm_runtime;
COMMIT;
