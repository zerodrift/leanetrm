-- An immutable membership policy selects delivery quantities outside economic identity.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

ALTER TABLE trading.gas_component_terms ADD COLUMN quantity_policy text
 CHECK(quantity_policy IS NULL OR quantity_policy='delivery_schedule_v1' AND role='physical' AND source_slot IS NULL);

-- Patch the exact admitted predecessor; preserve its owner, grants and configuration.
DO $patch$
DECLARE p pg_proc;definition text;old_text text;new_text text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'64690d0677ba5b8e3af498aacdf5a0bf3ec649707d5dc0ddce6d1fa7f969dcd9'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict
  OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_quantity_upgrade: unexpected composition writer';END IF;
 FOR old_text,new_text IN SELECT * FROM(VALUES
  ($old$'quantity','link','forecastCurve','calendarPolicy'))$old$,
   $new$'quantity','link','forecastCurve','calendarPolicy','quantityPolicy'))$new$),
  ($old$ SELECT array_agg(x->>'code') INTO codes$old$,
   $new$ IF EXISTS(SELECT 1 FROM jsonb_array_elements(physical) x WHERE x ? 'quantityPolicy' AND x->'quantityPolicy' IS DISTINCT FROM '"delivery_schedule_v1"'::jsonb)
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(physical) x WHERE x ? 'quantityPolicy')
    AND EXISTS(SELECT 1 FROM jsonb_array_elements(physical) x WHERE x->'link' IS DISTINCT FROM 'null'::jsonb)
 THEN RAISE EXCEPTION 'gas_quantity_policy';END IF;
 SELECT array_agg(x->>'code') INTO codes$new$),
  ($old$delivery_from,delivery_to,quantity_basis,independent_quantity)$old$,
   $new$delivery_from,delivery_to,quantity_basis,independent_quantity,quantity_policy)$new$),
  ($old$first_day,last_day,item->>'quantityBasis',(item->>'quantity')::numeric);$old$,
   $new$first_day,last_day,item->>'quantityBasis',(item->>'quantity')::numeric,item->>'quantityPolicy');$new$)
 )patches(a,b) LOOP
  IF (length(definition)-length(replace(definition,old_text,'')))/length(old_text)<>1
   THEN RAISE EXCEPTION 'gas_quantity_upgrade: writer patch mismatch';END IF;
  definition=replace(definition,old_text,new_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

-- Financial reads use the transaction's explicit business instant and knowledge
-- cutoff. The facility workbench's end-of-day clock is deliberately not involved.
CREATE FUNCTION security.read_gas_valuation_quantities(catalog uuid,trade uuid,binding uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $function$
DECLARE q security.request_contexts;head trading.gas_trade_revisions;job risk.jobs;result jsonb;
BEGIN
 SELECT * INTO STRICT q FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF q.market_catalog_id IS DISTINCT FROM catalog OR q.market_context_id IS NULL OR q.trading_as_of IS NULL
  OR NOT EXISTS(SELECT 1 FROM security.operation_trading t WHERE(t.catalog_id,t.context_id)=(catalog,q.market_context_id))
 THEN RAISE EXCEPTION 'gas_quantity_context';END IF;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  IF NOT risk.worker_read() THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
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
 THEN RAISE EXCEPTION 'gas_quantity_binding' USING ERRCODE='42501';END IF;
 IF job.id IS NOT NULL AND(job.request->'bookIds' @> jsonb_build_array(head.book_id) IS NOT TRUE
  OR NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL AND job.request->'tradeIds' @> jsonb_build_array(trade) IS NOT TRUE)
 THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 WITH obligations AS MATERIALIZED(
  SELECT o.* FROM trading.gas_delivery_obligations o
  WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,binding)
 ), manual AS(
  SELECT DISTINCT ON(o.slot,s.kind,r.day) r.id,o.slot,r.day,s.kind,r.status,s.source,trim_scale(r.quantity)::text quantity,r.evidence
  FROM obligations o JOIN trading.gas_quantity_series s ON(s.catalog_id,s.obligation_id)=(o.catalog_id,o.id)
  JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
  WHERE s.stage='delivery' AND s.source='manual_daily_v1' AND s.kind IN('scheduled','allocated')
   AND security.trading_visible(s.catalog_id,s.publication_order,s.effective_at)
   AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
  ORDER BY o.slot,s.kind,r.day,r.revision DESC
 ), facility AS(
  SELECT DISTINCT ON(o.slot,g.day) h.id,o.slot,g.day,'allocated'::text kind,
   CASE WHEN h.state='final' THEN 'final' ELSE 'withdrawn' END status,'facility_allocation_adoption_v1'::text source,
   trim_scale(line.quantity)::text quantity,a.evidence
  FROM obligations o JOIN trading.facility_adopted_targets h ON(h.catalog_id,h.obligation_id)=(o.catalog_id,o.id)
  JOIN trading.facility_quantity_adoptions a ON(a.catalog_id,a.id)=(h.catalog_id,h.adoption_id)
  JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(h.catalog_id,h.ledger_id)
  LEFT JOIN trading.facility_gas_allocations line ON(line.catalog_id,line.plan_id,line.obligation_id)=(h.catalog_id,h.plan_id,h.allocation_obligation_id)
  WHERE security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)
  ORDER BY o.slot,g.day,h.revision DESC
 ), heads AS(SELECT * FROM manual UNION ALL SELECT * FROM facility)
 SELECT coalesce(jsonb_agg(to_jsonb(h) ORDER BY slot,day,kind,source),'[]'::jsonb) INTO result FROM heads h;
 -- Recheck current authority and the active lease immediately before disclosure.
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 IF security.gas_component_sources_visible(catalog,binding) IS NOT TRUE
  OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read()
 THEN RAISE EXCEPTION 'gas_quantity_denied' USING ERRCODE='42501';END IF;
 IF job.id IS NOT NULL THEN PERFORM risk.bound_lifecycle_reader();END IF;
 RETURN result;
END$function$;
ALTER FUNCTION security.read_gas_valuation_quantities(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_valuation_quantities(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_valuation_quantities(uuid,uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;

-- A ticket predating the required field may retry a command already retained for
-- this actor. This is only a reconstruction gate: staging/publication still
-- compare the entire original command hash/body, including amendment coordinates.
CREATE FUNCTION security.gas_ticket_retained_request(catalog uuid,operation uuid,permission_name text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $function$
DECLARE actor uuid;retained boolean;
BEGIN
 IF permission_name NOT IN('BookTrade','AmendTrade') OR permission_name IS NULL
  OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission_name)
 THEN RAISE EXCEPTION 'No current gas ticket write authority' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts
  WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND market_context_id IS NULL;
 SELECT EXISTS(SELECT 1 FROM trading.side_schedule_requests
  WHERE(catalog_id,request_id,actor_id,permission)=(catalog,operation,actor,permission_name)) INTO retained;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission_name)
 THEN RAISE EXCEPTION 'Gas ticket write authority expired' USING ERRCODE='42501';END IF;
 RETURN retained;
END$function$;
ALTER FUNCTION security.gas_ticket_retained_request(uuid,uuid,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_ticket_retained_request(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_ticket_retained_request(uuid,uuid,text) TO openetrm_runtime;
COMMIT;
